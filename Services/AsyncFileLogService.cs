using System.Threading.Channels;
using System.IO;
using System.Diagnostics;
using System.Text;

namespace JBZUniveresalLunix.Services;

public enum AppLogCategory
{
    Application,
    Board,
    Test,
    Performance,
    Error
}

public enum AppLogLevel
{
    Normal = 0,
    Diagnostic = 1,
    ProtocolTrace = 2
}

/// <summary>
/// Writer log không block luồng scan/UI. Mọi dòng được đưa vào Channel và
/// một reader duy nhất append ra file theo ngày.
/// </summary>
public sealed class AsyncFileLogService : IDisposable
{
    private const int MaxWriteBatch = 256;
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(25);

    public static AsyncFileLogService Current { get; } = new();

    private readonly Channel<(AppLogCategory Category, DateTime Timestamp, string Message)> _channel =
        Channel.CreateUnbounded<(AppLogCategory, DateTime, string)>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private readonly CancellationTokenSource _cts = new();
    private Task? _writerTask;
    private int _started;
    private int _disposed;
    private int _fileLoggingEnabled;

    public string LogFilePath { get; private set; } = RuntimePaths.LogFile;

    public string RootDirectory =>
        Path.GetDirectoryName(LogFilePath) ?? RuntimePaths.AppDirectory;

    public AppLogLevel Level { get; private set; } = AppLogLevel.Normal;

    // EnableSystemLogs is the master switch for the canonical runtime log.
    // History and crash reports use separate persistence services.
    public bool FileLoggingEnabled => Volatile.Read(ref _fileLoggingEnabled) != 0;

    public void Configure(bool enabled, string? rootDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(rootDirectory) && Volatile.Read(ref _started) == 0)
        {
            string root = Path.IsPathRooted(rootDirectory)
                ? rootDirectory
                : Path.Combine(AppContext.BaseDirectory, rootDirectory);
            LogFilePath = Path.Combine(root, "JBZUniveresalLunix.log");
        }

        // When disabled, do not enqueue or create the canonical runtime log.
        // Re-enabling after startup initializes the writer on demand.
        Level = enabled ? AppLogLevel.ProtocolTrace : AppLogLevel.Normal;
        Volatile.Write(ref _fileLoggingEnabled, enabled ? 1 : 0);
        Initialize();
    }

    public void Initialize(string? rootDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(rootDirectory) && Volatile.Read(ref _started) == 0)
        {
            string root = Path.IsPathRooted(rootDirectory)
                ? rootDirectory
                : Path.Combine(AppContext.BaseDirectory, rootDirectory);
            LogFilePath = Path.Combine(root, "JBZUniveresalLunix.log");
        }

        if (!FileLoggingEnabled)
            return;

        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;

        try
        {
            string? directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            _writerTask = Task.Run(WriterLoopAsync);
            Write(AppLogCategory.Application, $"Log service initialized. File={LogFilePath}");
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _started, 0);
            Volatile.Write(ref _fileLoggingEnabled, 0);
            Debug.WriteLine($"AsyncFileLogService initialization failed: {ex}");
        }
    }

    public void Write(AppLogCategory category, string message, AppLogLevel level = AppLogLevel.Normal)
    {
        if (!FileLoggingEnabled ||
            Volatile.Read(ref _started) == 0 ||
            Volatile.Read(ref _disposed) != 0 ||
            level > Level)
            return;

        string safe = string.IsNullOrWhiteSpace(message) ? "(empty)" : message.Trim();
        _channel.Writer.TryWrite((category, DateTime.Now, $"[{level}] {safe}"));
    }

    public void Application(string message, AppLogLevel level = AppLogLevel.Normal) => Write(AppLogCategory.Application, message, level);
    public void Board(string message, AppLogLevel level = AppLogLevel.Normal) => Write(AppLogCategory.Board, message, level);
    public void Test(string message, AppLogLevel level = AppLogLevel.Normal) => Write(AppLogCategory.Test, message, level);
    public void Performance(string message, AppLogLevel level = AppLogLevel.Diagnostic) => Write(AppLogCategory.Performance, message, level);
    public void Error(string message) => Write(AppLogCategory.Error, message, AppLogLevel.Normal);

    private async Task WriterLoopAsync()
    {
        try
        {
            var batch = new List<(AppLogCategory Category, DateTime Timestamp, string Message)>(MaxWriteBatch);
            var buffer = new StringBuilder();

            while (await _channel.Reader.WaitToReadAsync(_cts.Token))
            {
                batch.Clear();
                DrainAvailableEntries(batch);

                // Gom các log phát sinh cùng frame/state transition. Scan và UI
                // chỉ TryWrite nên không phải chờ I/O đĩa của writer này.
                if (batch.Count < MaxWriteBatch && !_channel.Reader.Completion.IsCompleted)
                {
                    await Task.Delay(BatchWindow, _cts.Token);
                    DrainAvailableEntries(batch);
                }

                buffer.Clear();
                foreach (var item in batch)
                {
                    buffer.Append('[')
                        .Append(item.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                        .Append("] [")
                        .Append(item.Category)
                        .Append("] ")
                        .Append(item.Message)
                        .AppendLine();
                }

                await File.AppendAllTextAsync(
                    LogFilePath,
                    buffer.ToString(),
                    new UTF8Encoding(false),
                    _cts.Token);
            }

            void DrainAvailableEntries(List<(AppLogCategory Category, DateTime Timestamp, string Message)> destination)
            {
                while (destination.Count < MaxWriteBatch && _channel.Reader.TryRead(out var item))
                    destination.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Logging tuyệt đối không được làm process crash.
            Debug.WriteLine($"AsyncFileLogService writer stopped: {ex}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _channel.Writer.TryComplete();
        try
        {
            if (_writerTask is not null && !_writerTask.Wait(TimeSpan.FromSeconds(2)))
                _cts.Cancel();
        }
        catch
        {
            _cts.Cancel();
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
