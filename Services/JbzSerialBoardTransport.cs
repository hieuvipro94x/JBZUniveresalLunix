using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

/// <summary>Single-owner Windows COM transport for the JBZ Universal Tester firmware.</summary>
public sealed class JbzSerialBoardTransport : IAsyncDisposable
{
    public const int BaudRate = 115200;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _transaction = new(1, 1);
    private readonly ConcurrentDictionary<JbzEventFamily, Channel<JbzBoardEvent>> _queues = new();
    private readonly Channel<JbzBoardEvent> _commandResponses = Channel.CreateUnbounded<JbzBoardEvent>();
    private readonly JbzFirmwareUpdateService _firmware;
    private SerialPort? _port;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private long _generation;
    private int _disposed;
    private int _firmwareMode;

    public JbzSerialBoardTransport() => _firmware = new JbzFirmwareUpdateService(WriteFirmwareRawAsync);

    public bool IsConnected => _port?.IsOpen == true;
    public string PortName => _port?.PortName ?? string.Empty;
    public event EventHandler<JbzBoardEvent>? EventReceived;
    public event EventHandler<string>? Log;
    public bool IsFirmwareUpdating => Volatile.Read(ref _firmwareMode) != 0;

    public static string[] CandidatePorts(string? preferred = null)
    {
        string[] ports = SerialPort.GetPortNames().OrderBy(ComNumber).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(preferred.Trim(), "^COM[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidDataException($"Invalid JBZ board COM port: {preferred}");
            return new[] { preferred.Trim() }.Concat(ports).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return ports;
    }

    private static readonly TimeSpan NormalIdentityTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NormalModelNameTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DiscoveryIdentityTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan DiscoveryModelNameTimeout = TimeSpan.FromMilliseconds(750);
    private const int PreferredBusyRetryCount = 80;
    private static readonly TimeSpan PreferredBusyRetryDelay = TimeSpan.FromMilliseconds(250);

    public static async Task<(string Port, string Idn, string? Model)> DiscoverAsync(
        string? preferred = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? excludedPorts = null)
    {
        // Compatibility API for diagnostics/tests. Production uses the instance
        // ConnectFirstAvailableAsync so the successfully probed port remains open
        // instead of performing a second open + handshake.
        await using var transport = new JbzSerialBoardTransport();
        return await transport.ConnectFirstAvailableAsync(preferred, ct, excludedPorts);
    }

    /// <summary>
    /// Connects this transport to the first verified Universal Tester.
    /// A previously validated/preferred COM is tried with the normal production
    /// timeout. Only when that fast path fails do we probe the remaining COM ports
    /// sequentially with a shorter discovery timeout.
    /// </summary>
    public async Task<(string Port, string Idn, string? Model)> ConnectFirstAvailableAsync(
        string? preferred = null,
        CancellationToken ct = default,
        IReadOnlyCollection<string>? excludedPorts = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var excluded = new HashSet<string>(excludedPorts ?? [], StringComparer.OrdinalIgnoreCase);
        excluded.RemoveWhere(string.IsNullOrWhiteSpace);
        string preferredPort = preferred?.Trim() ?? string.Empty;
        if (preferredPort.Length > 0 && excluded.Contains(preferredPort))
            throw new IOException($"{preferredPort} is reserved for the Leak machine or label printer.");

        string[] candidates = CandidatePorts(preferredPort)
            .Where(port => !excluded.Contains(port))
            .ToArray();
        if (candidates.Length == 0)
            throw new IOException("No Windows COM port is available for the JBZ board.");

        Log?.Invoke(this,
            $"COM_DISCOVERY_BEGIN preferred={(preferredPort.Length == 0 ? "<none>" : preferredPort)} " +
            $"candidates={string.Join(',', candidates)}");

        Exception? lastError = null;
        foreach (string port in candidates)
        {
            bool fastPath = preferredPort.Length > 0 &&
                            port.Equals(preferredPort, StringComparison.OrdinalIgnoreCase);
            TimeSpan idnTimeout = fastPath ? NormalIdentityTimeout : DiscoveryIdentityTimeout;
            TimeSpan modelTimeout = fastPath ? NormalModelNameTimeout : DiscoveryModelNameTimeout;
            long started = Stopwatch.GetTimestamp();
            int openAttempts = fastPath ? PreferredBusyRetryCount : 1;

            Log?.Invoke(this, fastPath
                ? $"COM_FASTPATH_BEGIN port={port}"
                : $"COM_PROBE_BEGIN port={port}");

            for (int openAttempt = 1; openAttempt <= openAttempts; openAttempt++)
            {
                try
                {
                    (string idn, string? model) = await ConnectAsync(
                        port, idnTimeout, modelTimeout, ct);

                    double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Log?.Invoke(this, fastPath
                        ? $"COM_FASTPATH_OK port={port} latency_ms={elapsedMs:0}"
                        : $"COM_PROBE_OK port={port} latency_ms={elapsedMs:0}");
                    Log?.Invoke(this, $"BOARD_CONNECT_READY port={port} total_ms={elapsedMs:0}");
                    return (port, idn, model);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (fastPath && IsPortBusy(ex) && openAttempt < openAttempts)
                {
                    lastError = ex;
                    // Firmware flashers/serial monitors own COM exclusively on Windows.
                    // Wait for the validated JBZ port to be released instead of
                    // immediately probing unrelated COM devices. Throttle the log so
                    // a post-flash wait does not flood the production log.
                    if (openAttempt == 1 || openAttempt % 8 == 0 || openAttempt + 1 == openAttempts)
                    {
                        Log?.Invoke(this,
                            $"COM_FASTPATH_BUSY port={port} attempt={openAttempt}/{openAttempts} " +
                            $"retry_ms={PreferredBusyRetryDelay.TotalMilliseconds:0} reason={ex.Message}");
                    }
                    await Task.Delay(PreferredBusyRetryDelay, ct);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Log?.Invoke(this, fastPath
                        ? $"COM_FASTPATH_FAIL port={port} latency_ms={elapsedMs:0} reason={ex.Message}"
                        : $"COM_PROBE_FAIL port={port} latency_ms={elapsedMs:0} reason={ex.Message}");
                    break;
                }
            }
        }

        throw new IOException("No COM port answered as a JBZ Universal Tester.", lastError);
    }

    private static bool IsPortBusy(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
            return true;

        if (ex is IOException)
        {
            string message = ex.Message;
            return message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("in use", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("being used", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("access", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public Task<(string Idn, string? Model)> ConnectAsync(
        string portName,
        CancellationToken ct = default) =>
        ConnectAsync(portName, NormalIdentityTimeout, NormalModelNameTimeout, ct);

    private async Task<(string Idn, string? Model)> ConnectAsync(
        string portName,
        TimeSpan identityTimeout,
        TimeSpan modelNameTimeout,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _transaction.WaitAsync(ct);
        try { await _lifecycle.WaitAsync(ct); }
        catch { _transaction.Release(); throw; }
        try
        {
            await DisconnectCoreAsync();
            var port = CreatePort(portName, 100, 2000);
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            _port = port;
            long generation = Interlocked.Increment(ref _generation);
            _readerCts = new CancellationTokenSource();
            _readerTask = ReaderLoopAsync(port, generation, _readerCts.Token);

            Drain(JbzEventFamily.Idn);
            await SendCoreAsync("*IDN?", ct);
            JbzBoardEvent idn = await WaitAsync(JbzEventFamily.Idn, identityTimeout, ct);
            if (!JbzProtocolParser.IsUniversalTesterIdentity(idn.Raw))
                throw new InvalidDataException(
                    $"COM {portName} did not identify as a JBZ Universal Tester: {idn.Raw}");

            LogFirmwareIdentity(portName, idn.Raw);

            Drain(JbzEventFamily.ModelName);
            await SendCoreAsync(":MODELNAME?", ct);
            string? model = null;
            try
            {
                model = (await WaitAsync(
                    JbzEventFamily.ModelName,
                    modelNameTimeout,
                    ct)).Raw;
            }
            catch (TimeoutException)
            {
                // MODELNAME is useful metadata but IDN is the authoritative board identity.
            }

            return (idn.Raw, model);
        }
        catch
        {
            await DisconnectCoreAsync();
            throw;
        }
        finally
        {
            _lifecycle.Release();
            _transaction.Release();
        }
    }

    /// <summary>
    /// Opens the configured JBZ COM port without requiring *IDN?.
    /// Used only by firmware recovery when the application firmware is wedged,
    /// still scanning, or currently sitting in the bootloader and therefore cannot
    /// answer the normal identity handshake.
    /// </summary>
    public async Task OpenMaintenancePortAsync(string portName, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(portName))
            throw new InvalidDataException("JBZ recovery COM port is empty.");

        await _transaction.WaitAsync(ct);
        try { await _lifecycle.WaitAsync(ct); }
        catch { _transaction.Release(); throw; }

        try
        {
            await DisconnectCoreAsync();
            var port = CreatePort(portName.Trim(), 100, 2000);
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            _port = port;
            long generation = Interlocked.Increment(ref _generation);
            _readerCts = new CancellationTokenSource();
            _readerTask = ReaderLoopAsync(port, generation, _readerCts.Token);
            Log?.Invoke(this, $"FIRMWARE_RECOVERY_PORT_OPEN port={port.PortName}");
        }
        catch
        {
            await DisconnectCoreAsync();
            throw;
        }
        finally
        {
            _lifecycle.Release();
            _transaction.Release();
        }
    }

    /// <summary>
    /// Best-effort recovery transition to idle before entering :DOWNLOAD.
    /// A board may remain inside START/MEASURE after the previous application
    /// process was terminated, in which state *IDN? can time out even though COM
    /// and firmware are still alive. Failure to receive :STOP is not fatal because
    /// the next firmware step can also attach directly to an already-active bootloader.
    /// </summary>
    public async Task<bool> TryStopForFirmwareRecoveryAsync(CancellationToken ct = default)
    {
        await _transaction.WaitAsync(ct);
        try
        {
            if (!IsConnected)
                throw new IOException("JBZ board COM is not connected.");

            Drain(JbzEventFamily.Stop);
            Task<JbzBoardEvent> ack = WaitAsync(
                JbzEventFamily.Stop,
                TimeSpan.FromMilliseconds(900),
                ct);
            await SendCoreAsync(":STOP", ct);
            try
            {
                await ack;
                Log?.Invoke(this, "FIRMWARE_RECOVERY_STOP_ACK");
                return true;
            }
            catch (TimeoutException)
            {
                Log?.Invoke(this, "FIRMWARE_RECOVERY_STOP_NO_ACK");
                return false;
            }
        }
        finally
        {
            _transaction.Release();
        }
    }

    public async Task StartAsync(int maxExt, CancellationToken ct = default)
    {
        if (IsFirmwareUpdating)
            throw new InvalidOperationException("Normal UART commands are locked while firmware is updating.");

        await _transaction.WaitAsync(ct);
        try
        {
            if (!IsConnected)
                throw new IOException("JBZ board COM is not connected.");

            // UniversalTester Rev 1.42: START is a state transition, not a
            // fire-and-forget write. The original waits for :START,ON and then
            // immediately sends :MAXEXT before normal MEASURE/CLEAR/OPEN events.
            Drain(JbzEventFamily.Start);
            Drain(JbzEventFamily.Measure);
            Log?.Invoke(this, "SCAN_START_BEGIN");
            await SendCoreAsync(":START", ct);
            await WaitAsync(JbzEventFamily.Start, TimeSpan.FromSeconds(2), ct);
            Log?.Invoke(this, "SCAN_START_ACK");
            await WaitAsync(JbzEventFamily.Measure, TimeSpan.FromSeconds(2), ct);
            await SendCoreAsync($":MAXEXT,{maxExt}", ct);
            Log?.Invoke(this, $"MAXEXT_SENT value={maxExt}");
        }
        finally { _transaction.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (IsFirmwareUpdating)
            throw new InvalidOperationException("Normal UART commands are locked while firmware is updating.");

        await _transaction.WaitAsync(ct);
        try
        {
            if (!IsConnected)
                throw new IOException("JBZ board COM is not connected.");

            // Do not allow MODEL/START to overtake STOP. Captured firmware
            // always answers the mode transition with an exact :STOP line.
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                Drain(JbzEventFamily.Stop);
                Task<JbzBoardEvent> acknowledgment = WaitAsync(JbzEventFamily.Stop, TimeSpan.FromSeconds(2), ct);
                long started = Stopwatch.GetTimestamp();
                Log?.Invoke(this, $"STOP_BEGIN scanning=true attempt={attempt}");
                await SendCoreAsync(":STOP", ct);
                try
                {
                    await acknowledgment;
                    Log?.Invoke(this, $"STOP_ACK latency_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}");
                    return;
                }
                catch (TimeoutException) when (attempt == 1)
                {
                    Log?.Invoke(this, "STOP_TIMEOUT attempt=1");
                }
            }
            Log?.Invoke(this, "STOP_TIMEOUT attempt=2");
            Drain(JbzEventFamily.Idn);
            Task<JbzBoardEvent> liveness = WaitAsync(JbzEventFamily.Idn, TimeSpan.FromSeconds(2), ct);
            await SendCoreAsync("*IDN?", ct);
            try
            {
                JbzBoardEvent identity = await liveness;
                if (JbzProtocolParser.IsUniversalTesterIdentity(identity.Raw))
                {
                    Log?.Invoke(this, "BOARD_ALIVE_STOP_NOT_CONFIRMED");
                    throw new TimeoutException("Board alive but STOP transition was not confirmed.");
                }
            }
            catch (TimeoutException ex) when (ex.Message.StartsWith("Timeout waiting for Idn", StringComparison.Ordinal))
            {
                Log?.Invoke(this, "STOP liveness probe timed out");
            }
            throw new TimeoutException("STOP transition was not confirmed; model upload remains blocked.");
        }
        finally { _transaction.Release(); }
    }

    public Task PassPenAsync(int delayMs, int pinCount, CancellationToken ct = default) => SendAsync($":PASSPEN,{delayMs},{pinCount}", ct);
    public Task UnconnectAsync(int delayMs, int pinCount, CancellationToken ct = default) => SendAsync($":UNCONNECT,{delayMs},{pinCount}", ct);
    public Task OutputAsync(int channel, bool state, CancellationToken ct = default) => SendAsync($":OUTPUTTEST,{channel},{(state ? 1 : 0)}", ct);
    public Task MeasureResistanceAsync(int channel, CancellationToken ct = default) => SendAsync($":RESISTORTEST,{channel},200,1,0", ct);

    public Task UploadModelAsync(JbzCompiledModel model, CancellationToken ct = default) =>
        UploadModelAsync(model, null, ct);

    public async Task UploadModelAsync(JbzCompiledModel model,
        IProgress<JbzModelUploadProgress>? progress, CancellationToken ct = default)
    {
        await _transaction.WaitAsync(ct);
        try
        {
            if (!IsConnected) throw new IOException("JBZ board COM is not connected.");
            int completed = 0;
            int total = model.Commands.Count;
            progress?.Report(new(0, completed, total, "Preparing model download"));
            foreach (JbzProtocolCommand command in model.Commands)
            {
                DrainCommandResponses();
                await SendCoreAsync(command.Text, ct);
                await JbzModelAckWaiter.WaitAsync(command, _commandResponses.Reader, ct);
                completed++;
                progress?.Report(new(Math.Min(99, completed * 100 / total), completed, total, command.Text));
            }
            Drain(JbzEventFamily.Boot);
            await SendCoreAsync(":RESET", ct);
            // BootLoader is an expected reset response, but absence is not treated as an error.
            using var bootTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bootTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                JbzBoardEvent boot = await ReadQueueAsync(JbzEventFamily.Boot, bootTimeout.Token);
                if (boot.Raw == "BootLoader")
                    await SendCoreAsync(":STOP", ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { Log?.Invoke(this, "RESET: no BootLoader/BOOT line within 3 s"); }
            progress?.Report(new(100, completed, total, ":RESET"));
        }
        finally { _transaction.Release(); }
    }

    public async Task<string> UpdateFirmwareAsync(
        JbzFirmwareImage image,
        IProgress<JbzFirmwareProgress>? progress,
        CancellationToken ct = default)
    {
        if (!IsConnected) throw new IOException("JBZ board COM is not connected.");
        await _transaction.WaitAsync(ct);
        try
        {
            if (Interlocked.CompareExchange(ref _firmwareMode, 1, 0) != 0)
                throw new InvalidOperationException("Firmware update is already active.");
            Drain(JbzEventFamily.Idn);
            await _firmware.UpdateAsync(image, progress, ct);
        }
        finally
        {
            Interlocked.Exchange(ref _firmwareMode, 0);
            _transaction.Release();
        }
        // Firmware has just restarted into START PROCESS. Always read a fresh
        // identity from the running application firmware; never reuse the IDN
        // captured before flashing. A few boards need a short settle after the
        // bootloader releases the UART, so retry the IDN transaction itself.
        return await QueryIdentityAsync(ct, attempts: 3, retryDelay: TimeSpan.FromMilliseconds(200));
    }

    public async Task<string> QueryIdentityAsync(
        CancellationToken ct = default,
        int attempts = 1,
        TimeSpan? retryDelay = null)
    {
        attempts = Math.Max(1, attempts);
        TimeSpan delay = retryDelay ?? TimeSpan.Zero;
        Exception? lastError = null;

        await _transaction.WaitAsync(ct);
        try
        {
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    if (!IsConnected)
                        throw new IOException("JBZ board COM is not connected.");

                    Drain(JbzEventFamily.Idn);
                    Task<JbzBoardEvent> response = WaitAsync(
                        JbzEventFamily.Idn,
                        NormalIdentityTimeout,
                        ct);
                    await SendCoreAsync("*IDN?", ct);
                    JbzBoardEvent idn = await response;
                    if (!JbzProtocolParser.IsUniversalTesterIdentity(idn.Raw))
                        throw new InvalidDataException($"Unexpected JBZ firmware identity: {idn.Raw}");

                    LogFirmwareIdentity(PortName, idn.Raw);
                    return idn.Raw;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < attempts)
                {
                    lastError = ex;
                    Log?.Invoke(this,
                        $"FIRMWARE_IDENTITY_RETRY attempt={attempt}/{attempts} reason={ex.Message}");
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, ct);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
        }
        finally
        {
            _transaction.Release();
        }

        throw new IOException("Unable to read JBZ firmware identity/version.", lastError);
    }

    private void LogFirmwareIdentity(string portName, string identity)
    {
        string version = JbzProtocolParser.ExtractFirmwareVersion(identity);
        Log?.Invoke(this,
            $"FIRMWARE_IDENTITY port={portName} version={(version.Length == 0 ? "<unknown>" : version)} raw={identity}");
    }

    public async Task SendAsync(string command, CancellationToken ct = default)
    {
        if (IsFirmwareUpdating)
            throw new InvalidOperationException("Normal UART commands are locked while firmware is updating.");
        await _transaction.WaitAsync(ct);
        try { await SendCoreAsync(command, ct); }
        finally { _transaction.Release(); }
    }

    private async Task SendCoreAsync(string command, CancellationToken ct)
    {
        if (IsFirmwareUpdating)
            throw new InvalidOperationException("Normal UART commands are locked while firmware is updating.");
        SerialPort port = _port is { IsOpen: true } value ? value : throw new IOException("JBZ board COM is not connected.");
        string normalized = command.TrimEnd('\r', '\n');
        if (normalized.Contains('\r') || normalized.Contains('\n')) throw new ArgumentException("One command must contain one line.", nameof(command));
        await _write.WaitAsync(ct);
        try { byte[] payload = Encoding.ASCII.GetBytes(normalized + "\r\n"); await port.BaseStream.WriteAsync(payload, ct); await port.BaseStream.FlushAsync(ct); Log?.Invoke(this, $"TX {normalized}"); }
        finally { _write.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _transaction.WaitAsync();
        try
        {
            await _lifecycle.WaitAsync();
            try { await DisconnectCoreAsync(); }
            finally { _lifecycle.Release(); }
        }
        finally { _transaction.Release(); }
    }

    private async Task ReaderLoopAsync(SerialPort port, long generation, CancellationToken ct)
    {
        var parser = new JbzProtocolParser(); byte[] buffer = new byte[1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int count = await port.BaseStream.ReadAsync(buffer, ct);
                if (count == 0) throw new EndOfStreamException("UART reader reached end of stream.");
                foreach (JbzBoardEvent boardEvent in parser.Push(buffer.AsSpan(0, count)))
                {
                    if (generation != Volatile.Read(ref _generation)) return;
                    Log?.Invoke(this, $"RX {boardEvent.Raw}"); Queue(boardEvent);
                    if (IsFirmwareUpdating)
                        _firmware.NotifyLine(boardEvent.Raw);
                    EventReceived?.Invoke(this, boardEvent);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"SERIAL ERROR {ex.Message}");
            if (generation == Volatile.Read(ref _generation))
                Queue(new(JbzEventFamily.Error, $":ERROR,DISCONNECTED,{ex.GetType().Name}"));
        }
    }

    private void Queue(JbzBoardEvent value)
    {
        _queues.GetOrAdd(value.Family, _ => Channel.CreateUnbounded<JbzBoardEvent>()).Writer.TryWrite(value);
        if (value.Family is JbzEventFamily.Ok or JbzEventFamily.Error)
            _commandResponses.Writer.TryWrite(value);
    }
    private async Task WriteFirmwareRawAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        SerialPort port = _port is { IsOpen: true } value ? value : throw new IOException("JBZ board COM is not connected.");
        await _write.WaitAsync(ct);
        try { await port.BaseStream.WriteAsync(data, ct); await port.BaseStream.FlushAsync(ct); Log?.Invoke(this, $"FW TX {data.Length} bytes"); }
        finally { _write.Release(); }
    }
    private Task<JbzBoardEvent> ReadQueueAsync(JbzEventFamily family, CancellationToken ct) => _queues.GetOrAdd(family, _ => Channel.CreateUnbounded<JbzBoardEvent>()).Reader.ReadAsync(ct).AsTask();
    private async Task<JbzBoardEvent> WaitAsync(JbzEventFamily family, TimeSpan timeout, CancellationToken ct) { using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct); linked.CancelAfter(timeout); try { return await ReadQueueAsync(family, linked.Token); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"Timeout waiting for {family}."); } }
    private void Drain(JbzEventFamily family) { ChannelReader<JbzBoardEvent> reader = _queues.GetOrAdd(family, _ => Channel.CreateUnbounded<JbzBoardEvent>()).Reader; while (reader.TryRead(out _)) { } }
    private void DrainCommandResponses() { while (_commandResponses.Reader.TryRead(out _)) { } }
    private async Task DisconnectCoreAsync()
    {
        Interlocked.Increment(ref _generation);
        _readerCts?.Cancel();
        // Close unblocks a pending BaseStream.ReadAsync on Windows. Never dispose before it ends.
        SerialPort? port = _port;
        _port = null;
        if (port?.IsOpen == true) port.Close();
        if (_readerTask is not null)
        {
            try { await _readerTask; }
            catch (OperationCanceledException) { }
        }
        _readerCts?.Dispose(); _readerCts = null; _readerTask = null;
        port?.Dispose();
        DrainCommandResponses();
        Drain(JbzEventFamily.Idn); Drain(JbzEventFamily.ModelName); Drain(JbzEventFamily.Boot);
    }
    private static SerialPort CreatePort(string name, int readTimeout, int writeTimeout) => new(name, BaudRate, Parity.None, 8, StopBits.One) { Handshake = Handshake.None, ReadTimeout = readTimeout, WriteTimeout = writeTimeout, DtrEnable = false, RtsEnable = false };
    private static int ComNumber(string name) => int.TryParse(name.AsSpan(Math.Min(3, name.Length)), out int number) ? number : int.MaxValue;
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; await DisconnectAsync(); _lifecycle.Dispose(); _write.Dispose(); _transaction.Dispose(); }
}
