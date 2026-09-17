using System.IO;
using System.Windows;
using JBZUniveresalLunix.Core;
using JBZUniveresalLunix.Models;
using JBZUniveresalLunix.Services;

namespace JBZUniveresalLunix.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private object _page;
    private string _status = "CHƯA KẾT NỐI";
    private ProductModel? _model;

    private readonly AppSettings _settings;
    private readonly ProductionSettings _productionSettings;
    private readonly JbzBoardTransportAdapter _board;
    private readonly KeysightVisaService _visa = new();
    private readonly WaterProofSerialService _waterProof = new();
    private readonly TestEngine _engine;
    private readonly SemaphoreSlim _shutdownGate = new(1, 1);
    private bool _shutdownCompleted;
    private readonly object _startupGate = new();
    private Task? _startupTask;

    // Chỉ phát khi NGƯỜI VẬN HÀNH chủ động chọn một file mã hàng mới.
    // MainWindow dùng event này để tự mở TestView ngay sau khi model đã parse
    // xong, không cần bấm BẮT ĐẦU KIỂM TRA lần thứ hai.
    public event Action<ProductModel>? ExplicitModelLoaded;

    public object CurrentPage
    {
        get => _page;
        set => Set(ref _page, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public ProductModel? Model
    {
        get => _model;
        set => Set(ref _model, value);
    }

    // V12.9: MainWindow/Transport/Decoder/TestView cùng đọc một BoardCapacity.
    public BoardMode ActiveBoardMode => BoardMode.JbzSerial;

    public BoardCapacity CurrentBoardCapacity => BoardCapacity.FromSettings(_productionSettings);

    // Các property UI dưới đây vẫn hiển thị "card mở rộng" (1..10), nhưng
    // không tự tính công thức riêng nữa.
    public int RequiredCardCount =>
        Model is null
            ? 1
            : BoardCapacity.RequiredScanUnitsForIo(Model.MaxIo);

    public int ConfiguredCardCount => CurrentBoardCapacity.ExpansionCardCount;
    public int ConfiguredIoCapacity => CurrentBoardCapacity.TotalIoCapacity;
    public long RequiredIoCapacity =>
        (long)RequiredCardCount * BoardCapacity.IoPerExpansionCard;
    public int ConfiguredIoStart => CurrentBoardCapacity.FirstGlobalIo;
    public int ConfiguredIoEnd => CurrentBoardCapacity.LastGlobalIo;

    public ProductionSettings ProductionSettings => _productionSettings;
    public JbzBoardTransportAdapter BoardTransport => _board;

    // Universal Tester New reports/validates the physical model during upload;
    // the legacy JBZ UART expansion-card gate no longer blocks model selection.
    public bool HasEnoughCardsForModel => true;

    public HomeViewModel Home { get; }
    public TestViewModel Test { get; }

    public RelayCommand ShowHomeCommand { get; }
    public RelayCommand ShowTestCommand { get; }
    public RelayCommand ExitCommand { get; }

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _productionSettings = ProductionConfigService.Load();
        _board = new JbzBoardTransportAdapter(
            _productionSettings.BoardPortName,
            _productionSettings
        );

        _engine = new TestEngine(
            _board,
            _visa,
            _settings,
            _productionSettings
        );

        Home = new HomeViewModel(this);

        Test = new TestViewModel(
            this,
            _engine,
            _board,
            _visa,
            _waterProof,
            _settings,
            _productionSettings,
            requireStartupIoClear: false
        );

        // Việc tự nạp mã + tự kết nối bo được bắt đầu tại MainWindow.Loaded.
        // Không dùng fire-and-forget trong constructor để tránh race giữa WPF
        // StartupUri/ShowDialog và lần khởi tạo JBZ UART duy nhất của phiên.
        _page = Home;

        ShowHomeCommand = new RelayCommand(
            () => CurrentPage = Home
        );

        ShowTestCommand = new RelayCommand(
            () => CurrentPage = Test
        );

        ExitCommand = new RelayCommand(() =>
        {
            // Không Shutdown trực tiếp ở đây. MainWindow.Closing sẽ chờ dừng
            // worker JBZ UART/Keysight/relay xong rồi mới cho process thoát.
            Application.Current.MainWindow?.Close();
        });
    }


    public Task InitializeApplicationAsync()
    {
        lock (_startupGate)
        {
            return _startupTask ??= InitializeApplicationCoreAsync();
        }
    }

    private async Task InitializeApplicationCoreAsync()
    {
        Status = "ĐANG NẠP MÃ HÀNG VÀ KẾT NỐI BO...";

        // DB migration/import counter và nhận dạng JBZ UART độc lập. Chỉ kết nối
        // hardware song song; nạp model/statistics chờ bootstrap xong để DB cũ
        // luôn được chuyển sang canonical path trước khi repository có thể tạo DB.
        Task productionDataTask = Task.Run(StartupBootstrapService.EnsureDeferredProductionFiles);

        Task printerConnectionTask = Test.AutoConnectLabelPrinterAsync();
        _ = ObservePrinterStartupAsync(printerConnectionTask);

        Task boardInitializationTask = Test.InitializeHardwareAsync();
        await Task.WhenAll(productionDataTask, boardInitializationTask);

        // Sau khi canonical DB đã sẵn sàng mới nạp model và lịch sử sản lượng.
        // InitializeAsync reuse kết nối vừa hoàn tất, không mở bo lần hai.
        await Test.InitializeAsync();
        StartupPerformanceTrace.Mark("T3 SETTINGS_READY");

        Home.Refresh();
        Raise(nameof(CurrentBoardCapacity));
        Raise(nameof(RequiredCardCount));
        Raise(nameof(ConfiguredCardCount));
        Raise(nameof(ConfiguredIoCapacity));
        Raise(nameof(RequiredIoCapacity));
        Raise(nameof(ConfiguredIoStart));
        Raise(nameof(ConfiguredIoEnd));
        Raise(nameof(HasEnoughCardsForModel));

        if (Test.IsDeviceFault || !Test.IsBoardConnected)
            Status = "MẤT KẾT NỐI BO - THOÁT VÀ MỞ LẠI ỨNG DỤNG";
        else if (Model is not null)
            Status = $"CHỜ LẮP SẢN PHẨM - {Model.ModelName} - BO ĐÃ KẾT NỐI";
        else
            Status = "BO ĐÃ KẾT NỐI - CHƯA CÓ MÃ HÀNG";
    }

    private async Task ObservePrinterStartupAsync(Task printerConnectionTask)
    {
        try
        {
            await printerConnectionTask;
        }
        catch (Exception ex)
        {
            // Máy in là thiết bị phụ trợ: lỗi kết nối được hiển thị riêng,
            // không được giữ trạng thái khởi động của bo/model ở phía sau.
            Test.AddExternalLog($"Không thể tự kết nối máy in tem: {ex.Message}");
        }
    }

    public async Task ShutdownAsync()
    {
        await _shutdownGate.WaitAsync();
        try
        {
            if (_shutdownCompleted)
                return;

            _shutdownCompleted = true;

            // Dừng mọi âm báo ngay để không còn TESTPOINT.wav sau khi UI đóng.
            AppSoundService.Current.StopAll();

            try
            {
                await Test.ShutdownAsync();
            }
            catch
            {
                // Tiếp tục giải phóng phần cứng còn lại.
            }

            try
            {
                _engine.Dispose();
            }
            catch
            {
            }

            try
            {
                _visa.Dispose();
            }
            catch
            {
            }

            try
            {
                await _waterProof.DisposeAsync();
            }
            catch
            {
            }

            try
            {
                await _board.DisposeAsync();
            }
            catch
            {
                // Khi process đang thoát, ưu tiên trả handle về OS hơn popup lỗi.
            }
        }
        finally
        {
            _shutdownGate.Release();
        }
    }

    public async Task<ProductModel?> LoadModelAsync(string path,
        IProgress<JbzModelUploadProgress>? progress = null, CancellationToken ct = default)
    {
        if (Test.IsProductRemovalPending)
            throw new InvalidOperationException("VUI LÒNG THÁO SẢN PHẨM");

        Status = "ĐANG NẠP MÃ HÀNG...";
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("Không tìm thấy file mã hàng.", full);

        ProductModel? model;
        string extension = Path.GetExtension(full).ToLowerInvariant();
        if (extension != ".model")
            throw new InvalidDataException("Ứng dụng chỉ nhận file mã hàng .model của bo JBZ UART.");
        string? setupPath = JbzSetupParser.FindForModel(full);
        JbzSetupProfile? setup = setupPath is null ? null : JbzSetupParser.Load(setupPath, full);
        model = await Test.LoadSelectedModelFromPathAsync(full, progress, ct);

        if (model is null) return null;

        Model = model;
        Home.Refresh();
        Raise(nameof(CurrentBoardCapacity));
        Raise(nameof(RequiredCardCount));
        Raise(nameof(ConfiguredCardCount));
        Raise(nameof(ConfiguredIoCapacity));
        Raise(nameof(RequiredIoCapacity));
        Raise(nameof(ConfiguredIoStart));
        Raise(nameof(ConfiguredIoEnd));
        Raise(nameof(HasEnoughCardsForModel));
        Status = setup is null
            ? $"MODEL ĐÃ TẢI: {model.ModelName} (.setup not found)"
            : $"MODEL ĐÃ TẢI: {model.ModelName} + {Path.GetFileName(setup.SourcePath)}";

        if (!HasEnoughCardsForModel) ShowCardCapacityWarning();

        ExplicitModelLoaded?.Invoke(model);
        return model;
    }

    public void ReloadProductionSettings()
    {
        ProductionConfigService.ReloadInto(_productionSettings);
        AsyncFileLogService.Current.Configure(_productionSettings.EnableSystemLogs);
        Test.RefreshProductionConfiguration();
        RefreshSettingsBindings();
    }

    /// <summary>
    /// V12.9: dùng sau khi Save trang Cài đặt. Ngoài reload file còn stop/restart
    /// scan để capacity mới thực sự đi xuống transport/decoder/TestView.
    /// </summary>
    public async Task ReloadProductionSettingsAsync()
    {
        ProductionSettings old = new()
        {
            BoardMode = _productionSettings.BoardMode,
            BoardPortName = _productionSettings.BoardPortName,
            ExpansionCardCount = _productionSettings.ExpansionCardCount,
            StartCardNumber = _productionSettings.StartCardNumber,
            UsbDelay = _productionSettings.UsbDelay,
            UseTestPointer = _productionSettings.UseTestPointer
        };

        ProductionConfigService.ReloadInto(_productionSettings);
        AsyncFileLogService.Current.Configure(_productionSettings.EnableSystemLogs);
        bool boardSelectionChanged = old.BoardMode != _productionSettings.BoardMode ||
            !string.Equals(old.BoardPortName, _productionSettings.BoardPortName,
                StringComparison.OrdinalIgnoreCase);
        bool scanHardwareChanged =
            old.ExpansionCardCount != _productionSettings.ExpansionCardCount ||
            old.StartCardNumber != _productionSettings.StartCardNumber ||
            old.UsbDelay != _productionSettings.UsbDelay ||
            old.UseTestPointer != _productionSettings.UseTestPointer;

        // Manual relay là trạng thái runtime tức thời, không reload từ file.
        _productionSettings.ManualModeEnabled = false;

        if (boardSelectionChanged)
            Test.RequireApplicationRestartAfterBoardSettingsChange();
        else if (scanHardwareChanged)
            await Test.RefreshProductionConfigurationAsync(
                forceNativeRestart: old.UsbDelay != _productionSettings.UsbDelay ||
                                    old.UseTestPointer != _productionSettings.UseTestPointer);
        else
            Test.RefreshProductionSettingsOnly();

        await Test.AutoConnectLabelPrinterAsync();

        RefreshSettingsBindings();
    }

    private void RefreshSettingsBindings()
    {
        Home.Refresh();

        Raise(nameof(CurrentBoardCapacity));
        Raise(nameof(RequiredCardCount));
        Raise(nameof(ConfiguredCardCount));
        Raise(nameof(ConfiguredIoCapacity));
        Raise(nameof(RequiredIoCapacity));
        Raise(nameof(ConfiguredIoStart));
        Raise(nameof(ConfiguredIoEnd));
        Raise(nameof(HasEnoughCardsForModel));
    }

    public bool EnsureModelCardCapacity(bool showWarning = true)
    {
        if (Model is null)
            return false;

        if (HasEnoughCardsForModel)
            return true;

        if (showWarning)
            ShowCardCapacityWarning();

        return false;
    }

    private void ShowCardCapacityWarning()
    {
        if (Model is null)
            return;

        string extra = Model.MaxIo > BoardCapacity.MaxGlobalIo
            ? $"Model vượt giới hạn {BoardCapacity.MaxGlobalIo} I/O của hệ thống hiện tại."
            : $"Hãy vào CÀI ĐẶT -> Card mở rộng và chọn tối thiểu {RequiredCardCount} card trước khi test.";

        MessageBox.Show(
            $"KHÔNG ĐỦ CARD MỞ RỘNG\n" +
            $"{ConfiguredIoCapacity} / {RequiredIoCapacity} IO\n\n" +
            $"THÔNG TIN PIN / CARD\n\n" +
            $"Số I/O của mã hàng : {Model.MaxIo}\n" +
            $"Card mở rộng cần    : {RequiredCardCount}\n" +
            $"Card mở rộng hiện có: {ConfiguredCardCount}\n" +
            $"Vùng I/O hiện tại   : {ConfiguredIoStart} - {ConfiguredIoEnd} ({ConfiguredIoCapacity} I/O)\n\n" +
            extra,
            "Thiếu card I/O",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
