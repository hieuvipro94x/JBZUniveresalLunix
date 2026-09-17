using System.Threading.Channels;
using JBZUniveresalLunix.Models;
using Microsoft.Data.Sqlite;

namespace JBZUniveresalLunix.Services;

/// <summary>
/// Serialized production persistence boundary. All mutations are executed by
/// one background writer and acknowledged only after SQLite commits.
/// </summary>
public sealed class ProductionPersistenceService : IAsyncDisposable
{
    private static readonly TimeSpan[] BusyRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(350)
    ];

    private readonly TestHistoryStore _repository;
    private readonly Channel<Func<Task>> _commands;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writer;
    private long _currentRunId;
    private int _pendingCount;
    private string _lastDatabaseError = string.Empty;

    public ProductionPersistenceService(
        TestHistoryStore repository,
        ProductionSettings settings,
        string appVersion)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _commands = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _writer = Task.Run(ProcessCommandsAsync);
        Initialization = EnqueueAsync(() =>
        {
            _currentRunId = _repository.StartProductionRun(settings, appVersion);
            return true;
        });
    }

    public Task<bool> Initialization { get; }
    public string DatabasePath => _repository.DatabasePath;
    public int SchemaVersion => _repository.SchemaVersion;
    public long CurrentRunId => Interlocked.Read(ref _currentRunId);
    public int PendingPersistenceCount => Volatile.Read(ref _pendingCount);
    public string LastDatabaseError => Volatile.Read(ref _lastDatabaseError);
    public DatabaseMigrationReport MigrationReport => _repository.LastMigrationReport;

    public Task<ProductionCommitResult> CommitTestResultAsync(
        ProductionResultCommitRequest request,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            () => _repository.CommitResult(request, CurrentRunId),
            cancellationToken);

    public Task<bool> UpsertActiveCycleAsync(
        string cycleId,
        string partNumber,
        string modelPath,
        DateTime startedAt,
        string stage,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(() =>
        {
            _repository.UpsertActiveCycle(cycleId, partNumber, modelPath, startedAt, stage);
            return true;
        }, cancellationToken);

    public Task<ProductionStatisticsSnapshot> GetStatisticsAsync(
        PartIdentitySnapshot part,
        DateTime now,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(() => _repository.GetStatistics(part, now), cancellationToken);

    public Task<ProbeCounterSnapshot> GetProbeCounterAsync(
        PartIdentitySnapshot part,
        long defaultThreshold,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            () => _repository.GetProbeCounter(part, defaultThreshold),
            cancellationToken);

    public Task<ProbeCounterSnapshot> IncrementProbeCounterAsync(
        PartIdentitySnapshot part,
        long threshold,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(() => _repository.IncrementProbeCounter(part, threshold), cancellationToken);

    public Task<ProbeCounterSnapshot> ResetProbeCounterAsync(
        PartIdentitySnapshot part,
        long threshold,
        string operatorName,
        string memo,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            () => _repository.ResetProbeCounter(part, threshold, operatorName, memo),
            cancellationToken);

    public Task<bool> UpdateRemovalTimingAsync(
        string cycleId,
        DateTime removalStartedAt,
        DateTime? removedAt,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            () => _repository.UpdateRemovalTiming(cycleId, removalStartedAt, removedAt),
            cancellationToken);

    public Task<bool> TryBeginFirstPrintAsync(
        long historyId,
        string cycleId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(() => _repository.TryBeginFirstPrint(historyId, cycleId), cancellationToken);

    public Task UpdateLabelPrintOutcomeAsync(
        long historyId,
        string cycleId,
        LabelPrintStatus status,
        DateTime? printTimestamp,
        string message,
        string? printedBarcode = null,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(() =>
        {
            _repository.UpdateLabelPrintOutcome(
                historyId, cycleId, status, printTimestamp, message, printedBarcode);
            return true;
        }, cancellationToken);

    public Task IncrementLabelReprintAsync(
        long historyId,
        string cycleId,
        DateTime printedAt,
        string message,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(() =>
        {
            _repository.IncrementLabelReprint(historyId, cycleId, printedAt, message);
            return true;
        }, cancellationToken);

    public Task<bool> IsLegacyImportRequiredAsync(
        LegacyImportFile file,
        string contentHash,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(() => _repository.IsLegacyImportRequired(file, contentHash), cancellationToken);

    public Task<LegacyImportResult> ImportLegacyFileAsync(
        LegacyImportFile file,
        string contentHash,
        IReadOnlyList<TestHistoryRecord> records,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(() => _repository.ImportLegacyFile(file, contentHash, records), cancellationToken);

    public Task<bool> IsRuntimeMigrationCompletedAsync(
        string migrationKey,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(() => _repository.IsRuntimeMigrationCompleted(migrationKey), cancellationToken);

    public Task CompleteRuntimeMigrationAsync(
        string migrationKey,
        string details,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(() =>
        {
            _repository.CompleteRuntimeMigration(migrationKey, details);
            return true;
        }, cancellationToken);

    private Task<T> EnqueueAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        if (_shutdown.IsCancellationRequested)
            return Task.FromException<T>(new ObjectDisposedException(nameof(ProductionPersistenceService)));

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref _pendingCount);
        if (!_commands.Writer.TryWrite(async () =>
            {
                try
                {
                    T result = await ExecuteWithBusyRetryAsync(action, cancellationToken)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _lastDatabaseError, string.Empty);
                    completion.TrySetResult(result);
                }
                catch (OperationCanceledException exception)
                {
                    completion.TrySetCanceled(exception.CancellationToken);
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref _lastDatabaseError, exception.Message);
                    completion.TrySetException(exception);
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingCount);
                }

                await Task.CompletedTask;
            }))
        {
            Interlocked.Decrement(ref _pendingCount);
            completion.TrySetException(new ObjectDisposedException(nameof(ProductionPersistenceService)));
        }

        return completion.Task;
    }

    private static async Task<T> ExecuteWithBusyRetryAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return action();
            }
            catch (SqliteException exception) when (
                IsTransientLock(exception) && attempt < BusyRetryDelays.Length)
            {
                TimeSpan delay = BusyRetryDelays[attempt];
                AsyncFileLogService.Current.Application(
                    $"SQLITE_BUSY_RETRY attempt={attempt + 1}/{BusyRetryDelays.Length} " +
                    $"delay_ms={delay.TotalMilliseconds:0} code={exception.SqliteErrorCode} " +
                    $"extended={exception.SqliteExtendedErrorCode}");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientLock(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6; // SQLITE_BUSY / SQLITE_LOCKED

    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (Func<Task> command in _commands.Reader.ReadAllAsync(_shutdown.Token)
                               .ConfigureAwait(false))
            {
                await command().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_shutdown.IsCancellationRequested)
            return;

        try
        {
            if (CurrentRunId > 0)
                await EnqueueAsync(() =>
                {
                    _repository.FinishProductionRun(CurrentRunId);
                    return true;
                }).ConfigureAwait(false);
        }
        finally
        {
            _commands.Writer.TryComplete();
            try
            {
                await _writer.ConfigureAwait(false);
            }
            finally
            {
                _shutdown.Cancel();
                _shutdown.Dispose();
            }
        }
    }
}
