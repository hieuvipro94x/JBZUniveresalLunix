using System.Diagnostics;

namespace JBZUniveresalLunix.Services;

public static class StartupPerformanceTrace
{
    private static readonly long Started = Stopwatch.GetTimestamp();

    public static void Mark(string marker)
    {
        double elapsedMs = Stopwatch.GetElapsedTime(Started).TotalMilliseconds;
        AsyncFileLogService.Current.Application($"STARTUP_PERF {marker} elapsed_ms={elapsedMs:0.###}");
    }

    public static IDisposable Measure(string marker) => new Scope(marker);

    private sealed class Scope : IDisposable
    {
        private readonly string _marker;
        private readonly long _started = Stopwatch.GetTimestamp();

        public Scope(string marker)
        {
            _marker = marker;
            Mark($"{marker}_BEGIN");
        }

        public void Dispose()
        {
            double elapsedMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            AsyncFileLogService.Current.Application($"STARTUP_PERF {_marker}_END duration_ms={elapsedMs:0.###}");
        }
    }
}
