using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using JBZUniveresalLunix.ViewModels;
using JBZUniveresalLunix.Models;
using JBZUniveresalLunix.Services;
using JBZUniveresalLunix.Versioning;

namespace JBZUniveresalLunix.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan StartupControlUnlockTimeout = TimeSpan.FromSeconds(8);
    private readonly MainViewModel _viewModel;
    private TestWindow? _testWindow;
    private ProductionSettingsPage? _settingsPage;
    private HistoryPage? _historyPage;
    private BoardMaintenanceWindow? _maintenanceWindow;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private bool _startupStarted;
    private long _internalPageGeneration;
    private UiStallWatchdog? _uiStallWatchdog;

    public MainWindow()
    {
        InitializeComponent();
        StartupPerformanceTrace.Mark("T1 MainWindow constructed");
        Title = $"UniversalTester {AppVersion.DisplayVersion} - JBZ Production";
        AppVersionText.Text = $"JBZ Universal : {AppVersion.DisplayVersion}";

        _viewModel = new MainViewModel();
        _viewModel.ExplicitModelLoaded += ViewModel_ExplicitModelLoaded;
        _viewModel.Test.PropertyChanged += TestViewModel_PropertyChanged;
        DataContext = _viewModel;
        ContentRendered += MainWindow_ContentRendered;
        UpdateProductRemovalGate();
    }

    private async void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= MainWindow_ContentRendered;
        StartupPerformanceTrace.Mark("T2 MainWindow first ContentRendered");
        LogMemory("MEM STARTUP");
        _uiStallWatchdog = new UiStallWatchdog(Dispatcher);

        // Quan trọng: không khởi tạo FTDI/model/printer trong Loaded.
        // ContentRendered đảm bảo WPF đã vẽ frame đầu tiên; Task.Yield trả
        // quyền cho Dispatcher xử lý input/paint trước khi bắt đầu startup I/O.
        await Task.Yield();
        await InitializeApplicationAfterFirstRenderAsync();
    }

    private void ViewModel_ExplicitModelLoaded(ProductModel model)
    {
        // File dialog vừa đóng và model đã được parse/SetModel hoàn chỉnh.
        // Tự vào TestView ngay, không yêu cầu click BẮT ĐẦU lần thứ hai.
        Dispatcher.BeginInvoke(new Action(() => OpenTestWindowCore(allowViewWhenInsufficient: true)),
            System.Windows.Threading.DispatcherPriority.Background);
    }


    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Loaded có thể xảy ra trước ContentRendered. Chỉ khóa các nút phụ thuộc
        // hardware ở đây; tuyệt đối không chạy FTDI/model/printer trước frame đầu.
        StartTestButton.IsEnabled = false;
        SelectModelButton.IsEnabled = false;
    }

    private async Task InitializeApplicationAfterFirstRenderAsync()
    {
        if (_startupStarted)
            return;

        _startupStarted = true;

        try
        {
            // Tự nạp mã gần nhất và kết nối bo một lần sau khi cửa sổ đã
            // render. Các API bên dưới vẫn await bình thường nên Dispatcher
            // không bị giữ trong thời gian handshake/delay phần cứng.
            Task initialization = _viewModel.InitializeApplicationAsync();
            Task completed = await Task.WhenAny(
                initialization,
                Task.Delay(StartupControlUnlockTimeout));

            if (completed != initialization)
            {
                // UART discovery có thể cần thời gian để thử các COM khả dụng.
                // Giữ thao tác sản xuất khóa cho đến khi handshake hoàn tất.
                _viewModel.Status =
                    "ĐANG DÒ BO UART - CHỜ TÊN FIRMWARE...";
                AsyncFileLogService.Current.Performance(
                    $"STARTUP UART discovery still running after {StartupControlUnlockTimeout.TotalSeconds:0}s.");
                _ = ObserveDeferredStartupAsync(initialization);
                return;
            }

            await initialization;
        }
        catch (Exception ex)
        {
            if (_viewModel.Test.IsDeviceFault)
                return;

            // Startup phải luôn để MainWindow sử dụng được. Không đưa exception
            // kỹ thuật lên giao diện vận hành.
            AsyncFileLogService.Current.Error($"MainWindow startup failed: {ex}");
            MessageBox.Show(
                this,
                "Phần mềm chưa sẵn sàng. Vui lòng khởi động lại.",
                "CHƯA SẴN SÀNG",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            UpdateProductRemovalGate();
        }
    }

    private async Task ObserveDeferredStartupAsync(Task initialization)
    {
        try
        {
            await initialization;
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"DEFERRED STARTUP FAILED: {ex}");
        }
        finally
        {
            if (!_shutdownStarted)
                await Dispatcher.InvokeAsync(UpdateProductRemovalGate);
        }
    }

    private void OpenTestWindow_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenTestWindowCore(allowViewWhenInsufficient: false);
    }

    private void OpenTopologyLearning_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.Test.IsBoardConnected || _viewModel.Test.IsDeviceFault)
        {
            _viewModel.Test.ReportBoardUnavailableForOperatorAction("OpenTopologyLearning");
            return;
        }

        if (_viewModel.Test.IsManualModeActive)
        {
            MessageBox.Show(this,
                "Hãy tắt hoặc RESET relay tay trước khi học topology.",
                "Đang ở Manual", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var window = new TopologyLearningWindow(_viewModel.Test)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenTestWindowCore(bool allowViewWhenInsufficient = false)
    {
        if (!_viewModel.Test.IsBoardConnected || _viewModel.Test.IsDeviceFault)
        {
            _viewModel.Test.ReportBoardUnavailableForOperatorAction("OpenTestWindow");
            return;
        }

        if (_viewModel.Model is null)
        {
            MessageBox.Show(
                "Hãy chọn mã hàng trước khi bắt đầu kiểm tra.",
                "Chưa chọn mã hàng",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_viewModel.Test.IsManualModeActive)
        {
            MessageBox.Show(
                "Relay tay đang bật. Hãy bấm TẮT hoặc RESET trước khi bắt đầu Production Test.",
                "Production bị khóa",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // Khi người vận hành vừa chọn model, vẫn mở TestView để nhìn toàn bộ
        // cấu hình dù số card chưa đủ; chỉ KHÔNG ARM test. MainViewModel đã
        // cảnh báo thiếu card ngay sau khi load. Với nút BẮT ĐẦU thủ công thì
        // vẫn chặn như máy gốc.
        bool hasCapacity = _viewModel.EnsureModelCardCapacity(
            showWarning: !allowViewWhenInsufficient);
        if (!hasCapacity && !allowViewWhenInsufficient)
            return;

        if (_testWindow is { IsLoaded: true })
        {
            if (_testWindow.WindowState == WindowState.Minimized)
                _testWindow.WindowState = WindowState.Maximized;

            _testWindow.Activate();
            return;
        }

        try
        {
            _testWindow = new TestWindow(
                _viewModel.Test,
                autoStartProduction: hasCapacity);
            _testWindow.Closed += TestWindow_Closed;

            // Faults đã được SetModel/BuildRows trước khi Show(), vì vậy DataGrid
            // có cấu hình THT ngay frame render đầu tiên của TestWindow.
            _testWindow.Show();
            LogMemory("MEM TESTWINDOW_OPEN");
            Hide();
        }
        catch (Exception ex)
        {
            if (_testWindow is not null)
            {
                _testWindow.Closed -= TestWindow_Closed;
                _testWindow = null;
            }

            AsyncFileLogService.Current.Error($"Open TestWindow failed: {ex}");
            MessageBox.Show(
                "Chưa mở được màn hình kiểm tra. Vui lòng thử lại.",
                "CHƯA MỞ ĐƯỢC MÀN HÌNH KIỂM TRA",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void TestWindow_Closed(
        object? sender,
        EventArgs e)
    {
        if (_testWindow is not null)
        {
            _testWindow.Closed -= TestWindow_Closed;
            _testWindow = null;
        }

        if (_shutdownStarted || _viewModel.Test.IsDeviceFault)
            return;

        Show();
        LogMemory("MEM TESTWINDOW_CLOSE");
        WindowState = WindowState.Maximized;
        UpdateProductRemovalGate();
        Activate();
        Focus();
    }

    private void TestViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TestViewModel.IsProductRemovalPending) or
            nameof(TestViewModel.IsBoardConnected) or
            nameof(TestViewModel.IsBoardIdentityVerified) or
            nameof(TestViewModel.IsDeviceFault))
        {
            if (_viewModel.Test.IsDeviceFault)
            {
                _viewModel.Status =
                    "MẤT KẾT NỐI BO - THOÁT VÀ MỞ LẠI ỨNG DỤNG";
            }
            Dispatcher.BeginInvoke(UpdateProductRemovalGate);
        }
    }

    private void UpdateProductRemovalGate()
    {
        bool blocked = _viewModel.Test.IsProductRemovalPending;
        bool hardwareReady = _viewModel.Test.IsBoardIdentityVerified;
        ProductRemovalNotice.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        StartTestButton.IsEnabled = hardwareReady && _viewModel.Model is not null;
        SelectModelButton.IsEnabled = hardwareReady && !blocked;
        LearnTopologyButton.IsEnabled = hardwareReady;
        BoardMaintenanceButton.IsEnabled = hardwareReady && !blocked && _testWindow is null;
    }

    private void OpenBoardMaintenance_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.Test.IsBoardConnected || _viewModel.Test.IsDeviceFault ||
            _viewModel.Test.IsProductRemovalPending || _testWindow is not null)
        {
            MessageBox.Show(this,
                "Chỉ được bảo trì khi bo đã kết nối, không có sản phẩm và cửa sổ kiểm tra đã đóng.",
                "KHÔNG THỂ BẢO TRÌ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _maintenanceWindow = new BoardMaintenanceWindow(_viewModel.BoardTransport) { Owner = this };
        _maintenanceWindow.Closed += (_, _) =>
        {
            _maintenanceWindow = null;
            UpdateProductRemovalGate();
        };
        _maintenanceWindow.ShowDialog();
    }

    private async void OpenSettings_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            await ShowSettingsPageAsync();
        }
        catch (Exception ex)
        {
            if (_viewModel.Test.IsDeviceFault)
                return;

            AsyncFileLogService.Current.Error($"Open Settings failed: {ex}");
            MessageBox.Show(
                this,
                "Chưa mở được Cài đặt. Vui lòng thử lại.",
                "CHƯA MỞ ĐƯỢC CÀI ĐẶT",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task ShowSettingsPageAsync()
    {
        await CloseInternalPageAsync();
        long navigationGeneration = Volatile.Read(ref _internalPageGeneration);
        LogMemory("MEM BEFORE_SETTINGS");

        // Config/THT preparation có disk/parse work; tạo ViewModel ngoài
        // Dispatcher để BAML của page được cấp phát sau khi UI đã rảnh.
        ProductionSettingsViewModel settingsViewModel = await Task.Run(
            () => new ProductionSettingsViewModel(_viewModel.Test));

        // Người dùng có thể chuyển sang History/đóng app trong lúc config/THT
        // đang được chuẩn bị. Không dựng BAML cho một navigation đã stale.
        if (_shutdownStarted ||
            navigationGeneration != Volatile.Read(ref _internalPageGeneration))
        {
            return;
        }

        _settingsPage = new ProductionSettingsPage(_viewModel, settingsViewModel);
        _settingsPage.RequestClose += InternalPage_RequestClose;
        _settingsPage.SettingsSaved += SettingsPage_SettingsSaved;

        InternalPageHost.Content = _settingsPage;
        InternalPageHost.Visibility = Visibility.Visible;
        LogMemory("MEM AFTER_SETTINGS_OPEN");
    }

    private async void SettingsPage_SettingsSaved(object? sender, EventArgs e)
    {
        ProductionSettingsPage? savedPage = sender as ProductionSettingsPage;
        try
        {
            if (savedPage is not null)
                await savedPage.ReleaseManualOutputsAsync();
            await _viewModel.ReloadProductionSettingsAsync();

            // SETTINGS_SAVE_SILENT_2026-09-05:
            // Lưu và đồng bộ runtime thành công thì đóng trang Cài đặt luôn.
            // Không hiện popup "Đã lưu/đã đồng bộ với BO".
            // Popup lỗi bên dưới vẫn giữ nguyên nếu đồng bộ thật sự thất bại.
        }
        catch (Exception ex)
        {
            if (_viewModel.Test.IsDeviceFault)
                return;

            AsyncFileLogService.Current.Error($"Apply production settings failed: {ex}");
            MessageBox.Show(
                this,
                "Chưa áp dụng được Cài đặt. Vui lòng khởi động lại phần mềm.",
                "CHƯA ÁP DỤNG ĐƯỢC CÀI ĐẶT",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (ReferenceEquals(_settingsPage, savedPage))
            await CloseInternalPageAsync();
    }

    private async void OpenHistory_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            await CloseInternalPageAsync();

            _historyPage = new HistoryPage();
            _historyPage.RequestClose += InternalPage_RequestClose;

            InternalPageHost.Content = _historyPage;
            InternalPageHost.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (_viewModel.Test.IsDeviceFault)
                return;

            AsyncFileLogService.Current.Error($"Open History failed: {ex}");
            MessageBox.Show(
                this,
                "Chưa mở được Lịch sử kiểm tra. Vui lòng thử lại.",
                "CHƯA MỞ ĐƯỢC LỊCH SỬ",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void InternalPage_RequestClose(object? sender, EventArgs e)
    {
        try
        {
            await CloseInternalPageAsync();
        }
        catch (Exception ex)
        {
            if (_viewModel.Test.IsDeviceFault)
                return;

            AsyncFileLogService.Current.Error($"Settings close relay safety failed: {ex}");
            CrashReportService.Write(
                ex,
                "Hardware.SettingsCloseRelaySafety",
                $"Model={_viewModel.Model?.ModelName ?? "(none)"}");
            MessageBox.Show(
                this,
                "Máy test chưa về trạng thái an toàn. Vui lòng khởi động lại phần mềm.",
                "VUI LÒNG KHỞI ĐỘNG LẠI",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task CloseInternalPageAsync()
    {
        Interlocked.Increment(ref _internalPageGeneration);

        ProductionSettingsPage? settingsPage = _settingsPage;
        if (settingsPage is not null)
        {
            // Không tháo page khỏi visual tree trước khi board xác nhận chuỗi
            // Manual RESET: OFF -> RESET_CLEAR -> OFF và Production scan phục hồi.
            await settingsPage.ReleaseManualOutputsAsync();
            if (ReferenceEquals(_settingsPage, settingsPage))
            {
                settingsPage.RequestClose -= InternalPage_RequestClose;
                settingsPage.SettingsSaved -= SettingsPage_SettingsSaved;
                settingsPage.ReleasePageResources();
                _settingsPage = null;
                LogMemory("MEM SETTINGS_CLOSE");
            }
        }

        if (_historyPage is not null)
        {
            _historyPage.RequestClose -= InternalPage_RequestClose;
            _historyPage.ReleasePageResources();
            _historyPage = null;
        }

        InternalPageHost.Content = null;
        InternalPageHost.Visibility = Visibility.Collapsed;
    }

    private static void LogMemory(string marker)
    {
        using Process process = Process.GetCurrentProcess();
        AsyncFileLogService.Current.Performance(
            $"{marker} private_mb={process.PrivateMemorySize64 / 1048576d:0.###} " +
            $"working_set_mb={process.WorkingSet64 / 1048576d:0.###} " +
            $"gc_heap_mb={GC.GetTotalMemory(false) / 1048576d:0.###} " +
            $"handles={process.HandleCount} threads={process.Threads.Count}");
    }

    private async void MainWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (_shutdownComplete)
            return;

        // Không cho WPF hủy cửa sổ trước khi worker D2XX/VISA được dừng.
        e.Cancel = true;

        if (_shutdownStarted)
            return;

        _shutdownStarted = true;

        try
        {
            try
            {
                await CloseInternalPageAsync();
            }
            catch (Exception ex)
            {
                // ShutdownAsync/DisconnectAsync vẫn phải chạy để thử OFF lần
                // cuối và đóng handle, kể cả RESET của trang Cài đặt thất bại.
                _viewModel.Test.AddExternalLog(
                    $"Không thể RESET relay khi đóng trang Cài đặt lúc thoát: {ex.Message}");
            }
            _uiStallWatchdog?.Dispose();
            _uiStallWatchdog = null;
            _viewModel.ExplicitModelLoaded -= ViewModel_ExplicitModelLoaded;
            _viewModel.Test.PropertyChanged -= TestViewModel_PropertyChanged;
            await _viewModel.ShutdownAsync();
        }
        finally
        {
            _shutdownComplete = true;

            // Đóng lần hai sau khi mọi handle đã được giải phóng.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

}
