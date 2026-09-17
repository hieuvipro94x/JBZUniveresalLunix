using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

/// <summary>
/// UART/RS232 client for the waterproof/leak tester.
///
/// Design goals:
/// - This service owns only the leak COM port and never touches the D2XX board transport.
/// - Only one public COM operation is allowed at a time.
/// - Every production test run owns its COM session.
/// - A timed-out/cancelled old run is not allowed to close or replace the COM port of a newer run.
/// - Logging/progress callbacks are never allowed to break the serial state machine.
/// </summary>
public sealed class WaterProofSerialService : IAsyncDisposable
{
    private const int OpenPortTimeoutMs = 5_000;
    private const int ClosePortTimeoutMs = 2_000;

    // Serializes public connect/test/disconnect operations.
    // IMPORTANT: RunTestAsync acquires this gate BEFORE its watchdog starts,
    // so a queued second run cannot time out and abort the first run's COM port.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly object _stateGate = new();
    private readonly object _closeGate = new();

    private readonly HashSet<Task> _pendingCloseTasks = [];

    private SerialPort? _port;
    private string _connectedPort = string.Empty;
    private int _connectedBaud;

    // 0 = idle/manual connection. > 0 = production run that owns _port.
    private int _portOwnerRun;

    // Run currently authorized to register/use a production COM session.
    // Guarded by _stateGate.
    private int _activeRunId;

    private int _runSequence;

    public event EventHandler<string>? Log;

    public bool IsConnected
    {
        get
        {
            lock (_stateGate)
            {
                try
                {
                    return _port is { IsOpen: true };
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    public string ConnectedPort
    {
        get
        {
            lock (_stateGate)
            {
                try
                {
                    return _port is { IsOpen: true } ? _connectedPort : string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }
    }

    public async Task EnsureConnectedAsync(
        WaterProofMachineSettings machine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machine);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedCoreAsync(machine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WaterProofRunResult> RunTestAsync(
        WaterProofMachineSettings machine,
        WaterProofModelSettings profile,
        Action<WaterProofProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(profile);

        if (!profile.Enabled)
            throw new InvalidOperationException("HÃY BẬT TEST LEAK TRƯỚC.");

        if (profile.EnabledChannelCount == 0)
            throw new InvalidOperationException("CHỌN KÊNH KIỂM TRA");

        // Capture the caller context (normally WPF DispatcherSynchronizationContext)
        // so progress updates are marshalled back to the UI thread safely.
        SynchronizationContext? progressContext = SynchronizationContext.Current;

        // Acquire the run gate BEFORE starting the watchdog.
        // A second RunTestAsync therefore waits here and cannot abort the active run.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        int runNumber = Interlocked.Increment(ref _runSequence);
        Task<WaterProofRunResult>? worker = null;

        try
        {
            ActivateRun(runNumber);

            // Each product uses a fresh COM session.
            // Close any idle/stale connection before opening the run-owned port.
            DisconnectCore();
            await WaitForPendingCloseAsync(cancellationToken).ConfigureAwait(false);

            RaiseLog($"[WP] RUN #{runNumber} START");

            worker = Task.Run(
                () => RunTestWorkerAsync(
                    runNumber,
                    machine,
                    profile,
                    progress,
                    progressContext,
                    cancellationToken),
                CancellationToken.None);

            int watchdogMs = Math.Max(
                13_000,
                profile.PressTimeMs + profile.WaitTimeMs + 13_000);

            try
            {
                WaterProofRunResult result = await worker.WaitAsync(
                    TimeSpan.FromMilliseconds(watchdogMs),
                    cancellationToken).ConfigureAwait(false);

                // The worker detaches and schedules close in its finally block.
                // Do not use the caller's cancellation token here: once RESULT has
                // been received successfully, cleanup must not turn PASS/FAIL into Cancel.
                await WaitForPendingCloseBestEffortAsync().ConfigureAwait(false);

                RaiseLog("[WP] COMPLETED SESSION CLOSED - next run will reconnect cleanly");
                return result;
            }
            catch (TimeoutException ex)
            {
                DeactivateRun(runNumber);
                AbortRunPort(runNumber);
                ObserveLateWorker(worker);

                throw new TimeoutException(
                    $"Máy Leak/driver COM không phản hồi trong {watchdogMs / 1000.0:0.#} giây.",
                    ex);
            }
            catch (OperationCanceledException)
            {
                DeactivateRun(runNumber);
                AbortRunPort(runNumber);
                ObserveLateWorker(worker);
                throw;
            }
        }
        finally
        {
            DeactivateRun(runNumber);
            _gate.Release();
            RaiseLog($"[WP] RUN #{runNumber} PUBLIC GATE RELEASED");
        }
    }

    private async Task<WaterProofRunResult> RunTestWorkerAsync(
        int runNumber,
        WaterProofMachineSettings machine,
        WaterProofModelSettings profile,
        Action<WaterProofProgress>? progress,
        SynchronizationContext? progressContext,
        CancellationToken cancellationToken)
    {
        Stopwatch runWatch = Stopwatch.StartNew();
        Stopwatch lastRxWatch = Stopwatch.StartNew();
        SerialPort? port = null;

        try
        {
            port = await OpenRunPortAsync(runNumber, machine, cancellationToken)
                .ConfigureAwait(false);

            RaiseLog(
                $"[WP] COM state before test port={machine.PortName?.Trim()} baud={machine.BaudRate} " +
                $"isOpen={SafeIsOpen(port)} readTimeout={port.ReadTimeout} writeTimeout={port.WriteTimeout}");

            ClearStaleSerialBuffers(port);

            string command = BuildTestCommand(profile);
            RaiseLog($"[WP] RUN #{runNumber} TX {command.Trim()}");
            port.Write(command);

            var buffer = new StringBuilder();
            int timeoutMs = Math.Max(
                10_000,
                profile.PressTimeMs + profile.WaitTimeMs + 10_000);

            while (runWatch.ElapsedMilliseconds < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    lastRxWatch.Restart();
                    buffer.Append(chunk);

                    foreach (string line in ExtractCompleteLines(buffer))
                    {
                        WaterProofRunResult? final =
                            ProcessLine(runNumber, line, profile, progress, progressContext);

                        if (final is not null)
                        {
                            RaiseLog(
                                $"[WP] RUN #{runNumber} COMPLETE elapsed_ms={runWatch.ElapsedMilliseconds}");
                            return final;
                        }
                    }

                    // Some leak machines return :RESULT without CR/LF.
                    // Process it once all six values are present.
                    string pending = buffer.ToString().Trim();
                    if (pending.StartsWith(":RESULT", StringComparison.OrdinalIgnoreCase) &&
                        pending.Split(',').Length >= 7)
                    {
                        buffer.Clear();

                        WaterProofRunResult? final =
                            ProcessLine(runNumber, pending, profile, progress, progressContext);

                        if (final is not null)
                        {
                            RaiseLog(
                                $"[WP] RUN #{runNumber} COMPLETE elapsed_ms={runWatch.ElapsedMilliseconds}");
                            return final;
                        }
                    }
                }

                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Qua thoi gian {timeoutMs / 1000.0:0} giay cho :RESULT tu may leak.");
        }
        catch (Exception ex) when (IsRecoverableSerialFault(ex))
        {
            RaiseLog(
                $"[WP] RUN #{runNumber} {(ex is TimeoutException ? "TIMEOUT" : "ERROR")} " +
                $"{ex.GetType().Name}: {ex.Message}");
            RaiseLog($"[WP] RUN #{runNumber} last RX age={lastRxWatch.ElapsedMilliseconds}ms");
            RaiseLog($"[WP] RUN #{runNumber} serial IsOpen={SafeIsOpen(port)}");
            RaiseLog($"[WP] RUN #{runNumber} INVALIDATE OWNED COM");

            // Only this run's port may be invalidated.
            // Never close a newer run's COM port.
            AbortRunPort(runNumber);
            throw;
        }
        finally
        {
            if (port is not null)
                ReleaseRunPort(runNumber, port);

            RaiseLog($"[WP] RUN #{runNumber} WORKER FINISHED");
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        // Abort immediately without waiting for the public gate.
        // This is intentionally allowed so a stuck serial operation can be interrupted.
        AbortActiveRun();

        try
        {
            await WaitForPendingCloseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Continue to the gate attempt. The COM object has already been detached
            // from this service even if the OS driver is still completing Close().
        }

        bool entered = await _gate.WaitAsync(
            TimeSpan.FromMilliseconds(ClosePortTimeoutMs),
            cancellationToken).ConfigureAwait(false);

        if (!entered)
        {
            RaiseLog(
                "[WP] DISCONNECT TIMEOUT - COM đã được tách khỏi service; " +
                "không chờ driver vô hạn.");
            return;
        }

        try
        {
            DisconnectCore();

            try
            {
                await WaitForPendingCloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                RaiseLog(
                    "[WP] DISCONNECT CLOSE PENDING - driver COM vẫn đang nhả handle nền.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Immediately invalidates the currently active/idle COM object.
    /// Safe to call from watchdog/cancel/dispose paths.
    /// </summary>
    public void AbortActiveRun()
    {
        SerialPort? port;

        lock (_stateGate)
        {
            _activeRunId = 0;

            port = _port;
            _port = null;
            _connectedPort = string.Empty;
            _connectedBaud = 0;
            _portOwnerRun = 0;
        }

        if (port is null)
            return;

        RaiseLog("[WP] ABORT ACTIVE COM - yêu cầu hủy I/O đang chờ.");
        ScheduleClose(port);
    }

    private async Task EnsureConnectedCoreAsync(
        WaterProofMachineSettings machine,
        CancellationToken cancellationToken)
    {
        ValidateMachineSettings(machine);

        string portName = machine.PortName!.Trim();

        lock (_stateGate)
        {
            if (_activeRunId != 0)
                throw new InvalidOperationException("Máy Leak đang chạy test; không thể mở COM thủ công.");

            if (IsSameOpenPortLocked(portName, machine.BaudRate))
                return;
        }

        DisconnectCore();
        await WaitForPendingCloseAsync(cancellationToken).ConfigureAwait(false);

        SerialPort port = await OpenSerialPortAsync(machine, cancellationToken)
            .ConfigureAwait(false);

        bool registered;
        lock (_stateGate)
        {
            registered =
                _activeRunId == 0 &&
                _port is null;

            if (registered)
            {
                _port = port;
                _connectedPort = portName;
                _connectedBaud = machine.BaudRate;
                _portOwnerRun = 0;
            }
        }

        if (!registered)
        {
            ScheduleClose(port);
            throw new InvalidOperationException(
                "Trạng thái COM máy Leak đã thay đổi trong lúc kết nối.");
        }

        RaiseLog($"CONNECTED {portName} {machine.BaudRate} 8N1 ASCII");
    }

    private async Task<SerialPort> OpenRunPortAsync(
        int runNumber,
        WaterProofMachineSettings machine,
        CancellationToken cancellationToken)
    {
        ValidateMachineSettings(machine);

        string portName = machine.PortName!.Trim();

        // A previous Close() may still be running in the OS driver.
        // Never open a new session on top of an old handle.
        await WaitForPendingCloseAsync(cancellationToken).ConfigureAwait(false);

        SerialPort port = await OpenSerialPortAsync(machine, cancellationToken)
            .ConfigureAwait(false);

        bool registered;
        lock (_stateGate)
        {
            registered =
                _activeRunId == runNumber &&
                _port is null;

            if (registered)
            {
                _port = port;
                _connectedPort = portName;
                _connectedBaud = machine.BaudRate;
                _portOwnerRun = runNumber;
            }
        }

        if (!registered)
        {
            ScheduleClose(port);

            throw new OperationCanceledException(
                $"RUN #{runNumber} đã bị hủy trước khi COM mở xong.",
                null,
                cancellationToken);
        }

        RaiseLog($"[WP] RUN #{runNumber} CONNECTED {portName} {machine.BaudRate} 8N1 ASCII");
        return port;
    }

    private async Task<SerialPort> OpenSerialPortAsync(
        WaterProofMachineSettings machine,
        CancellationToken cancellationToken)
    {
        string portName = machine.PortName!.Trim();

        Task<SerialPort> openTask = Task.Run(() =>
        {
            var port = new SerialPort(
                portName,
                machine.BaudRate,
                Parity.None,
                8,
                StopBits.One)
            {
                Encoding = Encoding.ASCII,
                NewLine = "\r\n",
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false,
                ReadTimeout = Math.Clamp(machine.ReadTimeoutMs, 100, 30_000),
                WriteTimeout = Math.Clamp(machine.WriteTimeoutMs, 100, 30_000)
            };

            try
            {
                port.Open();
                port.DiscardInBuffer();
                port.DiscardOutBuffer();
                return port;
            }
            catch
            {
                try
                {
                    port.Dispose();
                }
                catch
                {
                }

                throw;
            }
        }, CancellationToken.None);

        try
        {
            return await openTask.WaitAsync(
                TimeSpan.FromMilliseconds(OpenPortTimeoutMs),
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            ObserveLateOpen(openTask);

            throw new TimeoutException(
                $"Không mở được {portName} trong {OpenPortTimeoutMs / 1000.0:0.#} giây. " +
                "Driver COM có thể đang giữ handle cũ.",
                ex);
        }
        catch (OperationCanceledException)
        {
            ObserveLateOpen(openTask);
            throw;
        }
    }

    private void ActivateRun(int runNumber)
    {
        lock (_stateGate)
        {
            _activeRunId = runNumber;
        }
    }

    private void DeactivateRun(int runNumber)
    {
        lock (_stateGate)
        {
            if (_activeRunId == runNumber)
                _activeRunId = 0;
        }
    }

    private void AbortRunPort(int runNumber)
    {
        SerialPort? port = null;

        lock (_stateGate)
        {
            if (_activeRunId == runNumber)
                _activeRunId = 0;

            if (_portOwnerRun == runNumber && _port is not null)
            {
                port = _port;
                _port = null;
                _connectedPort = string.Empty;
                _connectedBaud = 0;
                _portOwnerRun = 0;
            }
        }

        if (port is null)
            return;

        RaiseLog($"[WP] RUN #{runNumber} ABORT OWNED COM");
        ScheduleClose(port);
    }

    private void ReleaseRunPort(int runNumber, SerialPort port)
    {
        bool detached = false;

        lock (_stateGate)
        {
            if (_portOwnerRun == runNumber &&
                ReferenceEquals(_port, port))
            {
                _port = null;
                _connectedPort = string.Empty;
                _connectedBaud = 0;
                _portOwnerRun = 0;
                detached = true;
            }
        }

        // If AbortRunPort already detached it, that path already scheduled Close().
        if (detached)
            ScheduleClose(port);
    }

    private WaterProofRunResult? ProcessLine(
        int runNumber,
        string rawLine,
        WaterProofModelSettings profile,
        Action<WaterProofProgress>? progress,
        SynchronizationContext? progressContext)
    {
        string line = rawLine.Trim();
        if (line.Length == 0)
            return null;

        if (TryParseTriple(line, ":PRESS", out double[] press))
        {
            RaiseLog($"[WP] RUN #{runNumber} RX PRESS {line}");
            SafeProgress(
                progress,
                new WaterProofProgress(WaterProofStage.Pressurizing, press, line),
                runNumber,
                progressContext);
            return null;
        }

        if (TryParseTriple(line, ":WAIT", out double[] wait))
        {
            RaiseLog($"[WP] RUN #{runNumber} RX WAIT {line}");
            SafeProgress(
                progress,
                new WaterProofProgress(WaterProofStage.Waiting, wait, line),
                runNumber,
                progressContext);
            return null;
        }

        if (!TryParseResult(line, out double[] result))
            return null;

        RaiseLog($"[WP] RUN #{runNumber} RX RESULT {line}");
        SafeProgress(
            progress,
            new WaterProofProgress(WaterProofStage.Evaluating, result, line),
            runNumber,
            progressContext);

        var channels = new List<WaterProofChannelMeasurement>(3);
        bool allPassed = true;

        for (int channel = 1; channel <= 3; channel++)
        {
            bool enabled = profile.IsChannelEnabled(channel);
            int offset = (channel - 1) * 2;

            // Preserve the existing evaluation behavior:
            // each enabled channel consumes two RESULT values,
            // leak = absolute difference between them.
            double first = Math.Abs(result[offset]);
            double second = Math.Abs(result[offset + 1]);
            double leak = Math.Abs(first - second);

            bool passed =
                !enabled ||
                (second >= Math.Abs(profile.PressMin) &&
                 leak <= Math.Abs(profile.LeakLimit));

            channels.Add(new WaterProofChannelMeasurement(
                channel,
                enabled,
                first,
                second,
                leak,
                passed));

            if (enabled && !passed)
                allPassed = false;
        }

        return new WaterProofRunResult(channels, allPassed, line);
    }

    public static string BuildTestCommand(WaterProofModelSettings profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return
            $":TEST,{(profile.Channel1Enabled ? 1 : 0)}," +
            $"{(profile.Channel2Enabled ? 1 : 0)}," +
            $"{(profile.Channel3Enabled ? 1 : 0)},0," +
            $"{profile.PressTimeMs},{profile.WaitTimeMs}\r\n";
    }

    private static bool TryParseTriple(
        string line,
        string prefix,
        out double[] values)
    {
        values = [];

        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string[] parts = line[prefix.Length..]
            .Trim()
            .TrimStart(',')
            .Split(
                ',',
                StringSplitOptions.TrimEntries |
                StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3)
            return false;

        values = new double[3];

        for (int i = 0; i < 3; i++)
        {
            if (!double.TryParse(
                    parts[i],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out values[i]))
            {
                values = [];
                return false;
            }

            // Keep compatibility with the previous service/UI behavior.
            values[i] = Math.Abs(values[i]);
        }

        return true;
    }

    private static bool TryParseResult(string line, out double[] values)
    {
        values = [];

        const string prefix = ":RESULT";
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string[] parts = line[prefix.Length..]
            .Trim()
            .TrimStart(',')
            .Split(
                ',',
                StringSplitOptions.TrimEntries |
                StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 6)
        {
            throw new InvalidDataException(
                $"RESULT may leak can 6 gia tri, nhan {parts.Length}: {line}");
        }

        values = new double[6];

        for (int i = 0; i < 6; i++)
        {
            if (!double.TryParse(
                    parts[i],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out values[i]))
            {
                throw new InvalidDataException(
                    $"RESULT may leak co gia tri khong hop le '{parts[i]}'.");
            }

            // Keep compatibility with the previous service/UI behavior.
            values[i] = Math.Abs(values[i]);
        }

        return true;
    }

    private static IEnumerable<string> ExtractCompleteLines(StringBuilder buffer)
    {
        while (true)
        {
            string text = buffer.ToString();
            int index = text.IndexOfAny(['\r', '\n']);

            if (index < 0)
                yield break;

            string line = text[..index];
            int remove = index + 1;

            while (remove < text.Length &&
                   (text[remove] == '\r' || text[remove] == '\n'))
            {
                remove++;
            }

            buffer.Remove(0, remove);

            if (!string.IsNullOrWhiteSpace(line))
                yield return line;
        }
    }

    private void ClearStaleSerialBuffers(SerialPort port)
    {
        try
        {
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
        }
        catch (Exception ex)
        {
            RaiseLog($"[WP] purge stale RX/TX skipped: {ex.Message}");
        }
    }

    private void DisconnectCore()
    {
        SerialPort? port;

        lock (_stateGate)
        {
            port = _port;

            _port = null;
            _connectedPort = string.Empty;
            _connectedBaud = 0;
            _portOwnerRun = 0;
        }

        if (port is not null)
            ScheduleClose(port);
    }

    private void ScheduleClose(SerialPort port)
    {
        Task closeTask = Task.Run(() => CloseAndDispose(port), CancellationToken.None);

        lock (_closeGate)
        {
            _pendingCloseTasks.Add(closeTask);
        }

        _ = closeTask.ContinueWith(
            completed =>
            {
                // Observe any unexpected Task fault, then remove from tracking.
                _ = completed.Exception;

                lock (_closeGate)
                {
                    _pendingCloseTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WaitForPendingCloseAsync(
        CancellationToken cancellationToken)
    {
        Task[] closeTasks;

        lock (_closeGate)
        {
            closeTasks = _pendingCloseTasks
                .Where(static task => !task.IsCompleted)
                .ToArray();
        }

        if (closeTasks.Length == 0)
            return;

        try
        {
            await Task.WhenAll(closeTasks)
                .WaitAsync(
                    TimeSpan.FromMilliseconds(ClosePortTimeoutMs),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"Cổng COM máy Leak chưa nhả handle cũ trong " +
                $"{ClosePortTimeoutMs / 1000.0:0.#} giây; không mở chồng phiên mới.",
                ex);
        }
    }

    private async Task WaitForPendingCloseBestEffortAsync()
    {
        try
        {
            await WaitForPendingCloseAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // A valid RESULT must not be discarded only because a broken USB/COM
            // driver is slow to return from Close(). The next run will check again
            // before opening a new session.
            RaiseLog($"[WP] CLOSE STILL PENDING AFTER RESULT: {ex.Message}");
        }
    }

    private void CloseAndDispose(SerialPort port)
    {
        try
        {
            if (SafeIsOpen(port))
                port.Close();
        }
        catch (Exception ex)
        {
            RaiseLog($"[WP] close COM skipped: {ex.Message}");
        }
        finally
        {
            try
            {
                port.Dispose();
            }
            catch (Exception ex)
            {
                RaiseLog($"[WP] dispose COM skipped: {ex.Message}");
            }
        }
    }

    private void ObserveLateOpen(Task<SerialPort> openTask)
    {
        _ = openTask.ContinueWith(
            completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    // Open() returned after its watchdog/cancellation.
                    // Never let that late handle re-enter service state.
                    RaiseLog("[WP] LATE COM OPEN COMPLETED - closing stale handle.");
                    ScheduleClose(completed.Result);
                }
                else
                {
                    _ = completed.Exception;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ObserveLateWorker(Task worker)
    {
        _ = worker.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void SafeProgress(
        Action<WaterProofProgress>? progress,
        WaterProofProgress value,
        int runNumber,
        SynchronizationContext? progressContext)
    {
        if (progress is null)
            return;

        // RunTestWorkerAsync executes on a worker thread. If RunTestAsync was
        // started from the WPF UI thread, marshal progress back to that context.
        if (progressContext is not null &&
            !ReferenceEquals(SynchronizationContext.Current, progressContext))
        {
            try
            {
                progressContext.Post(
                    _ => InvokeProgressSubscribers(progress, value, runNumber),
                    null);
            }
            catch (Exception ex)
            {
                RaiseLog(
                    $"[WP] RUN #{runNumber} PROGRESS DISPATCH ERROR " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            return;
        }

        InvokeProgressSubscribers(progress, value, runNumber);
    }

    private void InvokeProgressSubscribers(
        Action<WaterProofProgress> progress,
        WaterProofProgress value,
        int runNumber)
    {
        foreach (Delegate subscriber in progress.GetInvocationList())
        {
            try
            {
                ((Action<WaterProofProgress>)subscriber)(value);
            }
            catch (Exception ex)
            {
                // A UI callback must never be interpreted as a serial fault
                // and must never close the leak COM port.
                RaiseLog(
                    $"[WP] RUN #{runNumber} PROGRESS CALLBACK ERROR " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void RaiseLog(string text)
    {
        EventHandler<string>? handlers = Log;
        if (handlers is null)
            return;

        foreach (Delegate subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<string>)subscriber)(this, text);
            }
            catch
            {
                // Logging is diagnostic only.
                // A subscriber is never allowed to break COM cleanup,
                // semaphore release, or the production test state machine.
            }
        }
    }

    private static bool SafeIsOpen(SerialPort? port)
    {
        if (port is null)
            return false;

        try
        {
            return port.IsOpen;
        }
        catch
        {
            return false;
        }
    }

    private bool IsSameOpenPortLocked(string portName, int baudRate)
    {
        try
        {
            return _port is { IsOpen: true } &&
                   _portOwnerRun == 0 &&
                   string.Equals(
                       _connectedPort,
                       portName,
                       StringComparison.OrdinalIgnoreCase) &&
                   _connectedBaud == baudRate;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateMachineSettings(
        WaterProofMachineSettings machine)
    {
        string portName = (machine.PortName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new InvalidOperationException(
                "Chua cau hinh cong COM UART/RS232 cho may leak.");
        }

        if (machine.BaudRate <= 0)
            throw new InvalidOperationException("Baudrate may leak khong hop le.");
    }

    private static bool IsRecoverableSerialFault(Exception ex) =>
        ex is IOException
            or TimeoutException
            or InvalidOperationException
            or UnauthorizedAccessException
            or InvalidDataException
            or ObjectDisposedException;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Dispose must be best-effort. Never hang/throw indefinitely because
            // a USB/serial driver is broken during process shutdown.
            AbortActiveRun();
        }

        // Deliberately do not dispose _gate here.
        // A broken COM driver can leave a late worker/continuation alive briefly;
        // disposing the semaphore could turn a recoverable shutdown into
        // ObjectDisposedException on that late path.
    }
}
