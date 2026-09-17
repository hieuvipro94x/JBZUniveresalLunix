using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using JBZUniveresalLunix.Models;
using JBZUniveresalLunix.Services;
using JBZUniveresalLunix.ViewModels;
using JBZUniveresalLunix.Versioning;

namespace JBZUniveresalLunix.Views;

public partial class TestWindow : Window
{
    private const double TestGridReferenceWidth = 1366;
    private const double TestGridReferenceHeight = 768;
    private const double TestGridMaximumScale = 1.25;
    private bool _allowClose;
    private bool _initializationStarted;
    private bool _closeInProgress;
    private readonly bool _autoStartProduction;
    private readonly DispatcherTimer _clockTimer;
    private NotifyCollectionChangedEventHandler? _faultsChangedHandler;
    private int _scrollDispatchQueued;
    public TestWindow(
        TestViewModel viewModel,
        bool autoStartProduction = true)
    {
        InitializeComponent();
        Title = $"UniversalTester {AppVersion.DisplayVersion} - Màn hình kiểm tra";
        TestAppVersionText.Text = AppVersion.DisplayVersion;
        DataContext = viewModel;
        _autoStartProduction = autoStartProduction;

        if (!_autoStartProduction)
            viewModel.State = "CẤU HÌNH CARD KHÔNG ĐỦ";

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += ClockTimer_Tick;

        UpdateClock();
        ContentRendered += TestWindow_ContentRendered;
    }

    private void TestWindow_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= TestWindow_ContentRendered;
        StartupPerformanceTrace.Mark("T9 TestWindow first rendered");
    }

    private void UpdateClock() => CurrentTimeText.Text = DateTime.Now.ToString("HH:mm:ss");

    private void ClockTimer_Tick(object? sender, EventArgs e) => UpdateClock();

    private void TestWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TestHeaderSurface is null)
        {
            return;
        }

        // Ở 1920x1080 header giãn ra dùng toàn bộ màn hình. Chỉ khi viewport
        // nhỏ hơn thiết kế 1344px (ví dụ 1024x768), Viewbox mới scale xuống.
        TestHeaderSurface.Width = Math.Max(1344, e.NewSize.Width - 16);
        ApplyResponsiveTestGridLayout(e.NewSize.Width, e.NewSize.Height);
    }

    private void ApplyResponsiveTestGridLayout(double viewportWidth, double viewportHeight)
    {
        // 1280x768 vẫn giữ cỡ chữ lớn, dễ đọc. Từ mốc 1366x768 trở lên,
        // chữ, chiều cao dòng và header tăng cùng một tỷ lệ; cột DataGrid dùng
        // Star Width nên tự nhận phần chiều rộng tăng thêm một cách đồng đều.
        double widthScale = Math.Max(1, viewportWidth) / TestGridReferenceWidth;
        double heightScale = Math.Max(1, viewportHeight) / TestGridReferenceHeight;
        double scale = Math.Clamp(
            Math.Min(widthScale, heightScale),
            1.0,
            TestGridMaximumScale);

        Resources["TestGridBaseFontSize"] = ResponsiveValue(18, scale);
        Resources["TestFaultGridFontSize"] = ResponsiveValue(20, scale);
        Resources["TestGridHeaderFontSize"] = ResponsiveValue(18, scale);
        Resources["TestFaultGridHeaderFontSize"] = ResponsiveValue(20, scale);
        Resources["TestGridRowHeight"] = ResponsiveValue(34, scale);
        Resources["TestGridColumnHeaderHeight"] = ResponsiveValue(40, scale);
    }

    private static double ResponsiveValue(double baseline, double scale) =>
        Math.Round(baseline * scale * 2, MidpointRounding.AwayFromZero) / 2;

    private async void TestWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initializationStarted)
            return;

        _initializationStarted = true;
        if (DataContext is not TestViewModel viewModel)
            return;

        _clockTimer.Start();
        ModelTitleText.Visibility = viewModel.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
        ConnectorColumn.Visibility = Visibility.Visible;

        _faultsChangedHandler = (_, args) =>
        {
            if (ShouldAutoScrollToFirstFault(args))
                ScheduleScrollToFirstFault(viewModel);
        };
        viewModel.Faults.CollectionChanged += _faultsChangedHandler;

        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (_autoStartProduction)
                await viewModel.StartProductionTestAsync();
        }
        catch (Exception ex)
        {
            if (viewModel.IsDeviceFault)
                return;

            AsyncFileLogService.Current.Error($"TestWindow initialization failed: {ex}");
            MessageBox.Show(this,
                "Chưa thể bắt đầu kiểm tra. Vui lòng quay về trang chính và thử lại.",
                "CHƯA BẮT ĐẦU KIỂM TRA", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ScheduleScrollToFirstFault(TestViewModel viewModel)
    {
        // Một delta có thể phát vài CollectionChanged liên tiếp. Chỉ giữ một
        // callback scroll pending để không tạo/hủy CTS và Dispatcher operation
        // cho từng row trên model 10 card.
        if (Interlocked.Exchange(ref _scrollDispatchQueued, 1) != 0)
            return;

        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (_closeInProgress || viewModel.Faults.Count == 0)
                    return;

                FaultRow firstFault = viewModel.Faults[0];
                if (IsFaultRowVisible(firstFault))
                    return;

                int delay = viewModel.ScrollDelay;
                if (delay > 0)
                    await Task.Delay(delay);
                if (_closeInProgress || viewModel.Faults.Count == 0)
                    return;

                firstFault = viewModel.Faults[0];
                if (!IsFaultRowVisible(firstFault))
                    FaultGrid.ScrollIntoView(firstFault);
            }
            finally
            {
                Interlocked.Exchange(ref _scrollDispatchQueued, 0);
            }
        }, DispatcherPriority.Background);
    }

    private static bool ShouldAutoScrollToFirstFault(NotifyCollectionChangedEventArgs args) =>
        // Remove là đường nóng khi một network PASS và biến mất. Không ép
        // DataGrid ScrollIntoView/layout lại chỉ vì các row còn lại dịch lên.
        // Add/Replace là fault mới; Reset dùng cho lần đầu dựng/xóa cả bảng.
        args.Action is NotifyCollectionChangedAction.Add or
            NotifyCollectionChangedAction.Replace or
            NotifyCollectionChangedAction.Reset;

    private bool IsFaultRowVisible(FaultRow row)
    {
        if (FaultGrid.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement container ||
            !container.IsVisible ||
            container.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            Point topLeft = container.TranslatePoint(new Point(0, 0), FaultGrid);
            double bottom = topLeft.Y + container.ActualHeight;
            return bottom > FaultGrid.ColumnHeaderHeight && topLeft.Y < FaultGrid.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            // Container có thể vừa bị recycle giữa hai lần cập nhật collection.
            return false;
        }
    }

    private async void ResetProbeCounter_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TestViewModel viewModel)
            return;

        var dialog = new ProbeMaintenanceResetWindow(
            viewModel.PartNumber,
            viewModel.ModelName,
            viewModel.ProbeCycleCount,
            viewModel.ProbeReplacementThreshold)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        (bool reset, string message) = await viewModel.TryResetProbeCycleAsync(dialog.AdminPassword);
        MessageBox.Show(
            this,
            message,
            reset ? "Đã reset counter Pin" : "Không thể reset counter Pin",
            MessageBoxButton.OK,
            reset ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void BackToMain_Click(object sender, RoutedEventArgs e)
    {
        if (_closeInProgress)
            return;
        _closeInProgress = true;

        try
        {
            if (DataContext is TestViewModel viewModel)
                await viewModel.StopViewAsync();
        }
        catch { }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    private async void TestWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            CleanupUiHandlers();
            return;
        }

        if (DataContext is TestViewModel deviceFaultViewModel && deviceFaultViewModel.IsDeviceFault)
        {
            _allowClose = true;
            CleanupUiHandlers();
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
            return;

        MessageBoxResult result = MessageBox.Show(this,
            "Bạn có muốn dừng kiểm tra và quay về màn hình chọn mã?",
            "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        _closeInProgress = true;
        try
        {
            if (DataContext is TestViewModel viewModel)
                await viewModel.StopViewAsync();
        }
        catch { }
        finally
        {
            _allowClose = true;
            CleanupUiHandlers();
            Close();
        }
    }

    private void CleanupUiHandlers()
    {
        ContentRendered -= TestWindow_ContentRendered;
        _clockTimer.Stop();
        _clockTimer.Tick -= ClockTimer_Tick;
        Interlocked.Exchange(ref _scrollDispatchQueued, 0);
        if (DataContext is TestViewModel vm)
        {
            if (_faultsChangedHandler is not null)
                vm.Faults.CollectionChanged -= _faultsChangedHandler;
        }
        _faultsChangedHandler = null;
        DataContext = null;
    }
}
