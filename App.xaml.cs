using System;
using System.Windows;
using System.Windows.Threading;
using JBZUniveresalLunix.Services;
using JBZUniveresalLunix.Versioning;
using JBZUniveresalLunix.ViewModels;
using JBZUniveresalLunix.Views;

namespace JBZUniveresalLunix;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\JBZUniveresalLunix.Production",
            createdNew: out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            MessageBox.Show(
                "JBZ Universal Tester đang chạy. Không thể mở thêm phiên thứ hai vì bo và dữ liệu sản xuất chỉ được phép có một chủ sở hữu.",
                "JBZ Universal Tester",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(2);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        StartupBootstrapService.EnsureFastConfiguration();
        var productionSettings = ProductionConfigService.Load();
        AsyncFileLogService.Current.Configure(productionSettings.EnableSystemLogs);
        AsyncFileLogService.Current.Application($"STARTUP {AppVersion.DisplayVersion}");
        StartupPerformanceTrace.Mark("T0 App.OnStartup READY");

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();

        // Defer audio I/O until MainWindow has rendered.
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                try
                {
                    AppSoundService.Current.Initialize();
                    AppSoundService.Current.PlayStartup();
                }
                catch (Exception ex)
                {
                    AsyncFileLogService.Current.Error($"Startup sound init failed: {ex}");
                }
            }),
            DispatcherPriority.ApplicationIdle);
    }

    private static async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashDiagnostics(e.Exception, "DispatcherUnhandledException");

        if (e.Exception is OutOfMemoryException)
        {
            // Không tiếp tục một visual tree nửa khởi tạo sau OOM. Ghi crash report
            // ở trên, đưa hardware về safe state best-effort rồi shutdown có kiểm soát.
            e.Handled = true;
            try
            {
                if (Current?.MainWindow?.DataContext is MainViewModel viewModel)
                    await viewModel.ShutdownAsync();
            }
            catch (Exception shutdownException)
            {
                WriteCrashDiagnostics(shutdownException, "OutOfMemory.SafeShutdown");
            }

            try
            {
                MessageBox.Show(
                    "Phần mềm cần khởi động lại để tiếp tục. Báo cáo lỗi đã được lưu.",
                    "VUI LÒNG KHỞI ĐỘNG LẠI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
            }

            Current?.Shutdown(-1);
            return;
        }

        // V15.1 SAFE OFFLINE MODE - lớp bảo vệ cuối cùng cho lỗi UI/async event.
        // Các thao tác phần cứng dự kiến phải được catch ở ViewModel/Command trước;
        // guard này tránh app văng ra nếu vẫn còn exception phục hồi được lọt tới Dispatcher.
        e.Handled = true;
        try
        {
            MessageBox.Show(
                "Thao tác chưa hoàn thành. Vui lòng khởi động lại phần mềm.",
                "THÔNG BÁO",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
        }
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        Exception exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown fatal exception");
        WriteCrashDiagnostics(exception, "AppDomain.UnhandledException");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AsyncFileLogService.Current.Error($"UnobservedTaskException: {e.Exception}");
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AsyncFileLogService.Current.Application($"SHUTDOWN exitCode={e.ApplicationExitCode}");
        AppSoundService.Current.Dispose();

        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        AsyncFileLogService.Current.Dispose();
        if (_ownsSingleInstanceMutex)
            _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    private static string BuildRuntimeContext()
    {
        try
        {
            if (Current?.MainWindow?.DataContext is not MainViewModel viewModel)
                return "MainViewModel unavailable";

            return $"Model={viewModel.Model?.ModelName ?? "(none)"}; " +
                   $"Part={viewModel.Model?.PartNumber ?? "(none)"}; " +
                   $"AppState={viewModel.Status}; TestState={viewModel.Test.State}";
        }
        catch
        {
            return "Runtime context unavailable";
        }
    }

    private static void WriteCrashDiagnostics(Exception exception, string source)
    {
        // Crash diagnostics must stay best-effort, especially for OOM where even
        // formatting a second diagnostic can fail before hardware reaches safe state.
        try
        {
            CrashReportService.Write(exception, source, BuildRuntimeContext());
        }
        catch
        {
            // The original fatal exception remains authoritative.
        }

        try
        {
            AsyncFileLogService.Current.Error($"{source}: {exception}");
        }
        catch
        {
            // Logging cannot be allowed to recursively fail the fatal-error handler.
        }
    }
}
