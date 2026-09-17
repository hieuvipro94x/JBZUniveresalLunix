using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace JBZUniveresalLunix.Services;

/// <summary>
/// Quản lý toàn bộ âm thanh của ứng dụng.
/// Các file WAV được đọc trực tiếp từ EmbeddedResource trong EXE.
/// </summary>
public sealed class AppSoundService : IDisposable
{
    private static readonly Lazy<AppSoundService> LazyInstance =
        new(() => new AppSoundService());

    private readonly object _gate = new();

    private SoundPlayer? _clickPlayer;
    private SoundPlayer? _productStartPlayer;
    private SoundPlayer? _testOkPlayer;
    private SoundPlayer? _startupPlayer;
    private SoundPlayer? _testPointContactPlayer;
    private SoundPlayer? _wiringFaultPlayer;
    private SoundPlayer? _discardContactPlayer;

    // Giữ stream sống trong toàn bộ vòng đời SoundPlayer.
    private MemoryStream? _clickStream;
    private MemoryStream? _productStartStream;
    private MemoryStream? _testOkStream;
    private MemoryStream? _startupStream;
    private MemoryStream? _testPointContactStream;
    private MemoryStream? _wiringFaultStream;
    private MemoryStream? _discardContactStream;

    private bool _initialized;
    private bool _disposed;
    private bool _wiringFaultAlarmActive;
    private bool _testPointContactSoundActive;

    private int _globalButtonHandlerRegistered;
    private int _startupPlayed;
    private int _startupPlaybackActive;
    private int _productStartPlaybackActive;
    private int _testOkPlaybackActive;

    public static AppSoundService Current => LazyInstance.Value;

    public bool IsWiringFaultAlarmActive
    {
        get
        {
            lock (_gate)
            {
                return _wiringFaultAlarmActive;
            }
        }
    }

    public bool IsTestPointContactSoundActive
    {
        get
        {
            lock (_gate)
            {
                return _testPointContactSoundActive;
            }
        }
    }

    private AppSoundService()
    {
    }

    /// <summary>
    /// Nạp tài nguyên âm thanh và đăng ký tiếng click cho toàn bộ Button WPF.
    /// Nên gọi một lần trong App.OnStartup().
    /// </summary>
    public void Initialize()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (!_initialized)
            {
                _clickPlayer = CreatePlayer("CLICK.wav", out _clickStream);
                _productStartPlayer = CreatePlayer("COMPUTER.wav", out _productStartStream);
                _testOkPlayer = CreatePlayer("DINGDONG.wav", out _testOkStream);
                _startupPlayer = CreatePlayer("START.wav", out _startupStream);
                _testPointContactPlayer = CreatePlayer("TESTPOINT.wav", out _testPointContactStream);
                _wiringFaultPlayer = CreatePlayer("TESTPOINT.wav", out _wiringFaultStream);
                _discardContactPlayer = CreatePlayer("DRIP.wav", out _discardContactStream);

                _initialized = true;
            }
        }

        if (Interlocked.Exchange(ref _globalButtonHandlerRegistered, 1) == 0)
        {
            EventManager.RegisterClassHandler(
                typeof(Button),
                Button.ClickEvent,
                new RoutedEventHandler(OnAnyButtonClick),
                handledEventsToo: true);
        }
    }

    /// <summary>
    /// Phát âm thanh khởi động đúng một lần trong mỗi lần chạy ứng dụng.
    /// </summary>
    public void PlayStartup()
    {
        EnsureInitialized();

        if (Interlocked.Exchange(ref _startupPlayed, 1) != 0)
        {
            return;
        }

        SoundPlayer? player = _startupPlayer;
        if (player is null)
        {
            AsyncFileLogService.Current.Error("STARTUP_SOUND resource START.wav is unavailable");
            return;
        }

        // SoundPlayer dùng chung thiết bị PlaySound của Windows. Một Stop() từ
        // luồng reset Probe lúc nạp model có thể cắt START.wav. PlaySync trên
        // worker đánh dấu một khoảng bảo vệ, không khóa Dispatcher/UI.
        _ = Task.Run(() =>
        {
            Interlocked.Exchange(ref _startupPlaybackActive, 1);
            try
            {
                AsyncFileLogService.Current.Application("STARTUP_SOUND PLAY_BEGIN");
                SafePlaySync(player);
                AsyncFileLogService.Current.Application("STARTUP_SOUND PLAY_END");
            }
            finally
            {
                Interlocked.Exchange(ref _startupPlaybackActive, 0);
            }
        });
    }

    public void PlayClick()
    {
        EnsureInitialized();
        if (Volatile.Read(ref _startupPlaybackActive) != 0 ||
            Volatile.Read(ref _productStartPlaybackActive) != 0 ||
            Volatile.Read(ref _testOkPlaybackActive) != 0)
            return;
        SafePlay(_clickPlayer);
    }

    /// <summary>Phát COMPUTER.wav một lần khi chu kỳ nhận kết nối sản phẩm đầu tiên.</summary>
    public void PlayProductStart()
    {
        EnsureInitialized();

        if (Interlocked.CompareExchange(ref _productStartPlaybackActive, 1, 0) != 0)
            return;

        SoundPlayer? player;
        lock (_gate)
        {
            if (_disposed || Volatile.Read(ref _testOkPlaybackActive) != 0)
            {
                Interlocked.Exchange(ref _productStartPlaybackActive, 0);
                return;
            }

            SafeStop(_clickPlayer);
            player = _productStartPlayer;
        }

        if (player is null)
        {
            Interlocked.Exchange(ref _productStartPlaybackActive, 0);
            AsyncFileLogService.Current.Error("PRODUCT_START_SOUND resource COMPUTER.wav is unavailable");
            return;
        }

        // SoundPlayer.Play() trả về ngay. PlaySync giữ tài nguyên PlaySound suốt
        // file COMPUTER.wav (~2,8 s), khiến Stop/Play của đầu dò, lỗi hoặc PASS
        // có thể chặn Dispatcher đúng lúc cần phản hồi nhanh nhất.
        AsyncFileLogService.Current.Application("PRODUCT_START_SOUND PLAY_ASYNC");
        SafePlay(player);
        Interlocked.Exchange(ref _productStartPlaybackActive, 0);
    }

    public void PlayTestOk()
    {
        EnsureInitialized();

        // DINGDONG phải tiếp tục phát xuyên suốt các cập nhật UI
        // PASS -> THÁO SẢN PHẨM -> SẴN SÀNG. SoundPlayer dùng chung
        // PlaySound của Windows, vì vậy Stop() trên player Probe/fault khác
        // cũng có thể cắt âm PASS nếu không có khoảng bảo vệ này.
        if (Interlocked.CompareExchange(ref _testOkPlaybackActive, 1, 0) != 0)
            return;

        SoundPlayer? player;
        lock (_gate)
        {
            if (_disposed)
            {
                Interlocked.Exchange(ref _testOkPlaybackActive, 0);
                return;
            }

            _wiringFaultAlarmActive = false;
            _testPointContactSoundActive = false;
            SafeStop(_clickPlayer);
            SafeStop(_productStartPlayer);
            SafeStop(_startupPlayer);
            SafeStop(_testPointContactPlayer);
            SafeStop(_wiringFaultPlayer);
            player = _testOkPlayer;
        }

        if (player is null)
        {
            Interlocked.Exchange(ref _testOkPlaybackActive, 0);
            AsyncFileLogService.Current.Error("PASS_SOUND resource DINGDONG.wav is unavailable");
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                AsyncFileLogService.Current.Application("PASS_SOUND PLAY_BEGIN");
                SafePlaySync(player);
                AsyncFileLogService.Current.Application("PASS_SOUND PLAY_END");
            }
            finally
            {
                CompleteTestOkPlayback();
            }
        });
    }

    /// <summary>Phát TESTPOINT.wav một lần khi đầu dò firmware chuyển sang ON.</summary>
    public void PlayTestPoint()
    {
        EnsureInitialized();
        lock (_gate)
        {
            if (_disposed || _wiringFaultAlarmActive ||
                Volatile.Read(ref _testOkPlaybackActive) != 0)
                return;
            SafePlay(_testPointContactPlayer);
        }
    }

    /// <summary>Phát DRIP.wav một lần khi tiếp điểm _DISCARD chuyển sang THÔNG.</summary>
    public void PlayDiscardContact()
    {
        EnsureInitialized();
        lock (_gate)
        {
            if (_disposed || Volatile.Read(ref _testOkPlaybackActive) != 0)
                return;

            SafePlay(_discardContactPlayer);
        }
    }

    /// <summary>
    /// Phát TESTPOINT.wav liên tục trong thời gian đầu dò đang chạm Pin.
    /// Đây chỉ là âm xác nhận tiếp xúc, hoàn toàn độc lập với trạng thái lỗi dây.
    /// </summary>
    public void SetTestPointContactSound(bool active)
    {
        EnsureInitialized();

        lock (_gate)
        {
            if (_disposed)
                return;

            if (Volatile.Read(ref _testOkPlaybackActive) != 0)
            {
                // Ghi nhớ trạng thái mới nhất nhưng không gọi Play/Stop vì mọi
                // thao tác PlaySound lúc này đều có thể cắt DINGDONG.
                _testPointContactSoundActive = active;
                return;
            }

            // RELEASE luôn cưỡng bức Stop, kể cả cờ trạng thái đã về false
            // từ một callback trước đó. Điều này tránh WAV looping bị sót khi
            // frame/UI release đến gần nhau.
            if (active && _testPointContactSoundActive)
                return;

            _testPointContactSoundActive = active;
            try
            {
                if (active)
                    _testPointContactPlayer?.PlayLooping();
                else if (Volatile.Read(ref _startupPlaybackActive) == 0)
                    _testPointContactPlayer?.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Không thể đổi âm tiếp xúc đầu dò: {ex}");
            }
        }
    }

    /// <summary>
    /// Bật/tắt âm thanh cảnh báo chập mạch hoặc đấu sai.
    /// Khi bật, TESTPOINT.wav sẽ lặp liên tục cho đến khi trạng thái lỗi hết.
    /// </summary>
    public void SetWiringFaultAlarm(bool active)
    {
        EnsureInitialized();

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (Volatile.Read(ref _testOkPlaybackActive) != 0)
            {
                // Nếu fault vẫn còn sau DINGDONG thì bật lại alarm ở
                // CompleteTestOkPlayback; nếu đã hết thì trạng thái false thắng.
                _wiringFaultAlarmActive = active;
                return;
            }

            if (_wiringFaultAlarmActive == active)
                return;

            _wiringFaultAlarmActive = active;

            try
            {
                if (active)
                {
                    _wiringFaultPlayer?.PlayLooping();
                }
                else
                {
                    _wiringFaultPlayer?.Stop();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Không thể đổi trạng thái âm cảnh báo: {ex}");
            }
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _wiringFaultAlarmActive = false;
            _testPointContactSoundActive = false;

            SafeStop(_clickPlayer);
            SafeStop(_productStartPlayer);
            SafeStop(_testOkPlayer);
            SafeStop(_startupPlayer);
            SafeStop(_testPointContactPlayer);
            SafeStop(_wiringFaultPlayer);
            SafeStop(_discardContactPlayer);
        }
    }

    private void OnAnyButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.IsEnabled)
        {
            PlayClick();
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            Initialize();
        }
    }

    private static SoundPlayer? CreatePlayer(
        string fileName,
        out MemoryStream? retainedStream)
    {
        retainedStream = null;

        try
        {
            Assembly assembly = typeof(AppSoundService).Assembly;

            string? resourceName = assembly
                .GetManifestResourceNames()
                .FirstOrDefault(name =>
                    name.EndsWith(
                        ".Assets.Sounds." + fileName,
                        StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(
                        "." + fileName,
                        StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                Debug.WriteLine(
                    $"Không tìm thấy EmbeddedResource âm thanh: {fileName}");
                return null;
            }

            using Stream? resourceStream =
                assembly.GetManifestResourceStream(resourceName);

            if (resourceStream is null)
            {
                Debug.WriteLine(
                    $"Không mở được EmbeddedResource âm thanh: {resourceName}");
                return null;
            }

            using var buffer = new MemoryStream();
            resourceStream.CopyTo(buffer);

            retainedStream = new MemoryStream(
                buffer.ToArray(),
                writable: false);

            var player = new SoundPlayer(retainedStream);
            player.Load();
            return player;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Không thể nạp âm thanh {fileName}: {ex}");
            retainedStream?.Dispose();
            retainedStream = null;
            return null;
        }
    }

    private static void SafePlay(SoundPlayer? player)
    {
        try
        {
            player?.Play();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Không thể phát âm thanh: {ex}");
        }
    }

    private static void SafePlaySync(SoundPlayer? player)
    {
        try
        {
            player?.PlaySync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Không thể phát hết âm thanh: {ex}");
            AsyncFileLogService.Current.Error($"SYNC_SOUND PLAY_FAILED: {ex.Message}");
        }
    }

    private void CompleteTestOkPlayback()
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _testOkPlaybackActive, 0);
            if (_disposed)
                return;

            try
            {
                // Wiring fault có độ ưu tiên cao hơn âm tiếp xúc Probe. Chỉ
                // resume yêu cầu vẫn còn hiệu lực ở đúng thời điểm PASS kết thúc.
                if (_wiringFaultAlarmActive)
                    _wiringFaultPlayer?.PlayLooping();
                else if (_testPointContactSoundActive)
                    _testPointContactPlayer?.PlayLooping();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Không thể khôi phục âm sau PASS: {ex}");
            }
        }
    }

    private static void SafeStop(SoundPlayer? player)
    {
        try
        {
            player?.Stop();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Không thể dừng âm thanh: {ex}");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AppSoundService));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            StopAll();

            _clickPlayer?.Dispose();
            _productStartPlayer?.Dispose();
            _testOkPlayer?.Dispose();
            _startupPlayer?.Dispose();
            _testPointContactPlayer?.Dispose();
            _wiringFaultPlayer?.Dispose();
            _discardContactPlayer?.Dispose();

            _clickStream?.Dispose();
            _productStartStream?.Dispose();
            _testOkStream?.Dispose();
            _startupStream?.Dispose();
            _testPointContactStream?.Dispose();
            _wiringFaultStream?.Dispose();
            _discardContactStream?.Dispose();

            _disposed = true;
        }
    }
}
