using System.Diagnostics;
using System.Windows.Threading;

namespace JBZUniveresalLunix.Services;

public sealed class UiStallWatchdog : IDisposable
{
    private const int HeartbeatMs = 250;
    private readonly DispatcherTimer _timer;
    private long _lastTick;

    public UiStallWatchdog(Dispatcher dispatcher)
    {
        _lastTick = Stopwatch.GetTimestamp();
        _timer = new DispatcherTimer(DispatcherPriority.ContextIdle, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(HeartbeatMs)
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedMs = Stopwatch.GetElapsedTime(_lastTick, now).TotalMilliseconds;
        _lastTick = now;
        double stallMs = elapsedMs - HeartbeatMs;
        if (stallMs > 100)
            AsyncFileLogService.Current.Performance($"UI_STALL duration_ms={stallMs:0.###}");
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
