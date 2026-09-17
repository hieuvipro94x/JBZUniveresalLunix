using System.IO;
using System.Text;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

/// <summary>Adapts semantic JBZ UART events to the established production engine contract.</summary>
public sealed class JbzBoardTransportAdapter : IBoardTransport
{
    // Universal Tester New has exactly two physical relay outputs. Firmware channels are zero-based.
    public const int Relay1OutputChannel = 0;
    public const int Relay5OutputChannel = 4;

    public static bool IsPhysicalRelayOutput(int channel) =>
        channel == Relay1OutputChannel || channel == Relay5OutputChannel;
    private readonly JbzSerialBoardTransport _serial = new();
    private readonly SemaphoreSlim _modelOperationGate = new(1, 1);
    private readonly ProductionSettings _settings;
    private string _preferredPort;
    private readonly Dictionary<int, int[]> _open = [];
    private readonly Dictionary<(int Low, int High), (int Source, int Target, ProductFaultType Type)> _unexpected = [];
    private readonly JbzLiveProbeTracker _liveProbe = new();
    private ProductModel? _model;
    private JbzCompiledModel? _compiledModel;
    private string _uploadedModel = string.Empty;
    private bool _uploadFailed;
    private string _identity = string.Empty;
    private BoardConnectionState _state;
    private BoardScanMode _mode;
    private long _sequence;
    private long _generation;
    private long _frames;
    private DateTime _lastFrameUtc;
    private int _maxIo;

    public JbzBoardTransportAdapter(string preferredPort, ProductionSettings settings)
    {
        _preferredPort = preferredPort;
        _settings = settings;
        _serial.EventReceived += OnEvent;
        _serial.Log += (_, value) => Log?.Invoke(this, value);
    }

    public bool IsConnected => _serial.IsConnected;
    public string BoardFirmwareIdentity => IsConnected ? _identity : string.Empty;
    public bool ProducesPassiveScanFrames => false;
    public BoardConnectionState ConnectionState => _state;
    public bool IsScanning => _state == BoardConnectionState.Scanning;
    // V6: firmware flashing temporarily owns the UART/COM exclusively.
    // TestViewModel uses this to suppress ScanWatchdog/recovery while the
    // bootloader is programming or while the adapter is deliberately paused.
    public bool IsFirmwareMaintenanceActive =>
        _state == BoardConnectionState.PausedForHardwareOperation || _serial.IsFirmwareUpdating;
    public BoardScanMode CurrentScanMode => _mode;
    public BoardCapacity InstalledCapacity => BoardCapacity.FromSettings(_settings);
    public BoardCapacity Capacity => InstalledCapacity;
    public BoardCapacity? AppliedScanCapacity => IsScanning ? Capacity : null;
    public BoardScanCapacity ScanCapacity => BoardScanCapacity.Create(_settings, Math.Max(0, _maxIo));
    public DateTime LastFrameTimestampUtc => _lastFrameUtc;
    public long LastFrameSequence => _sequence;
    public long LastCompleteFrameSequence => _sequence;
    public long FramesReceived => _frames;
    public long CompleteFramesReceived => _frames;
    public int LastFrameSourceCount { get; private set; }
    public byte? LastFrameEndMarkerCode => null;
    public int LastFrameUnknownBytes => 0;
    public event EventHandler<ScanFrame>? FrameReceived;
    public event EventHandler<ProductionProbePreview>? ProductionProbePreviewReceived;
    public event EventHandler<string>? Log;

    public async Task<BoardConnectionInfo> ConnectAsync(CancellationToken ct = default)
    {
        _state = BoardConnectionState.Connecting;
        try
        {
            // Keep the successful discovery connection open. The previous flow
            // probed a temporary transport, closed it, then opened/handshook the
            // same COM again, which added several seconds to startup.
            (string port, string idn, string? model) connection;
            try
            {
                connection = await _serial.ConnectFirstAvailableAsync(
                    _preferredPort,
                    ct,
                    [_settings.WaterProofMachine.PortName, _settings.Label.PrinterCom]);
            }
            catch (IOException firstError)
            {
                // V8: do not close/reopen the preferred COM between recovery STOP and
                // the IDN probe. A board stuck in START PROCESS can answer only after
                // :STOP/:RESET, and reopening the port can lose that narrow recovery
                // window. Recover and verify the identity on the same raw COM session.
                (string Port, string Idn, string? Model)? recovered =
                    await TryRecoverStuckStartupPortAsync(ct);
                if (recovered is null)
                    throw new IOException(
                        $"JBZ_RECOVERY_REQUIRED preferred={_preferredPort}; {firstError.Message}",
                        firstError);

                connection = recovered.Value;
                Log?.Invoke(this,
                    $"BOARD_CONNECT_RECOVERY_SUCCESS port={connection.port} raw={connection.idn.Trim()}");
            }

            (string port, string idn, _) = connection;

            if (!JbzProtocolParser.IsUniversalTesterIdentity(idn))
                throw new InvalidDataException(
                    $"COM {port} không trả tên firmware Universal Tester: {idn}");

            _identity = idn.Trim();
            // A physical COM reconnect is a new transport session. Never carry
            // the in-process model synchronization cache across sessions; the
            // operator-selected-model path relies on this to force a real
            // MODEL/PINDATA/ARRAY/CON/CONNECTOR transaction even for the same name.
            _uploadedModel = string.Empty;
            _uploadFailed = false;
            _state = BoardConnectionState.Ready;
            _preferredPort = port;

            // BoardPortName doubles as the preferred/last validated COM. Persist
            // only after IDN verification so the next application start can take
            // the direct fast path and avoid scanning unrelated COM ports.
            if (!port.Equals(_settings.BoardPortName?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                _settings.BoardPortName = port;
                try
                {
                    ProductionConfigService.Save(_settings);
                    Log?.Invoke(this, $"COM_PORT_REMEMBERED port={port}");
                }
                catch (Exception ex)
                {
                    // A read-only configuration must not turn a healthy board
                    // connection into a startup failure.
                    Log?.Invoke(this,
                        $"COM_PORT_REMEMBER_FAILED port={port} reason={ex.Message}");
                }
            }

            return new(idn, port);
        }
        catch
        {
            _identity = string.Empty;
            _state = BoardConnectionState.Faulted;
            throw;
        }
    }

    private async Task<(string Port, string Idn, string? Model)?> TryRecoverStuckStartupPortAsync(
        CancellationToken ct)
    {
        string preferred = _preferredPort?.Trim() ?? string.Empty;
        if (preferred.Length == 0)
            preferred = _settings.BoardPortName?.Trim() ?? string.Empty;

        if (preferred.Length == 0)
        {
            Log?.Invoke(this, "BOARD_CONNECT_RECOVERY_SKIP reason=no-preferred-port");
            return null;
        }

        string[] present = JbzSerialBoardTransport.CandidatePorts();
        if (!present.Contains(preferred, StringComparer.OrdinalIgnoreCase))
        {
            Log?.Invoke(this,
                $"BOARD_CONNECT_RECOVERY_SKIP port={preferred} reason=port-not-present present={string.Join(',', present)}");
            return null;
        }

        // The normal discovery path already accepted this value as the preferred
        // board COM. Do not silently suppress recovery merely because an auxiliary
        // setting accidentally contains the same COM; that made V7 return before
        // emitting any recovery log even though Windows still exposed COM10.
        if (preferred.Equals(_settings.WaterProofMachine.PortName?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            preferred.Equals(_settings.Label.PrinterCom?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Log?.Invoke(this,
                $"BOARD_CONNECT_RECOVERY_WARN port={preferred} reason=aux-port-setting-conflict action=prefer-validated-board-port");
        }

        try
        {
            Log?.Invoke(this,
                $"BOARD_CONNECT_RECOVERY_BEGIN port={preferred} action=RAW_STOP_IDN_RESET_IDN");
            await _serial.OpenMaintenancePortAsync(preferred, ct);

            bool stopAck = false;
            try
            {
                stopAck = await _serial.TryStopForFirmwareRecoveryAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
            {
                Log?.Invoke(this,
                    $"BOARD_CONNECT_RECOVERY_STOP_WARN port={preferred} reason={ex.Message}");
            }

            await Task.Delay(stopAck ? 120 : 220, ct);
            try
            {
                string idn = await _serial.QueryIdentityAsync(
                    ct, attempts: 3, retryDelay: TimeSpan.FromMilliseconds(180));
                Log?.Invoke(this,
                    $"BOARD_CONNECT_RECOVERY_IDN_OK port={preferred} phase=after-stop raw={idn.Trim()}");
                return (preferred, idn.Trim(), null);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException)
            {
                Log?.Invoke(this,
                    $"BOARD_CONNECT_RECOVERY_IDN_FAIL port={preferred} phase=after-stop reason={ex.Message}");
            }

            // Last non-destructive application recovery: reset the board application
            // and give the firmware enough time to leave START PROCESS before IDN.
            try
            {
                Log?.Invoke(this, $"BOARD_CONNECT_RECOVERY_RESET_BEGIN port={preferred}");
                await _serial.SendAsync(":RESET", ct);
                await Task.Delay(700, ct);
                string idn = await _serial.QueryIdentityAsync(
                    ct, attempts: 5, retryDelay: TimeSpan.FromMilliseconds(250));
                Log?.Invoke(this,
                    $"BOARD_CONNECT_RECOVERY_IDN_OK port={preferred} phase=after-reset raw={idn.Trim()}");
                return (preferred, idn.Trim(), null);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or InvalidOperationException)
            {
                Log?.Invoke(this,
                    $"BOARD_CONNECT_RECOVERY_RESET_FAIL port={preferred} reason={ex.Message}");
            }

            try
            {
                if (_serial.IsConnected)
                    await _serial.DisconnectAsync();
            }
            catch { }

            Log?.Invoke(this,
                $"BOARD_CONNECT_RECOVERY_REQUIRED port={preferred} reason=idn-unresponsive firmware-recovery-available=true");
            return null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            try
            {
                if (_serial.IsConnected)
                    await _serial.DisconnectAsync();
            }
            catch { }
            Log?.Invoke(this,
                $"BOARD_CONNECT_RECOVERY_FAIL port={preferred} reason={ex.Message}");
            return null;
        }
    }

    public async Task DisconnectAsync()
    {
        _state = BoardConnectionState.ShuttingDown;
        _identity = string.Empty;
        // Never let ConfigureModelAsync believe a model is synchronized after
        // the UART session was closed. Without this reset a forced reconnect
        // still logged MODEL_UPLOAD skip reason=already-synchronized.
        _uploadedModel = string.Empty;
        ClearLiveProbe();
        await _serial.DisconnectAsync();
        _state = BoardConnectionState.Disconnected;
    }
    public Task HandshakeAsync(CancellationToken ct = default) => IsConnected ? Task.CompletedTask : throw new InvalidOperationException("JBZ UART is disconnected.");
    public Task ResetClearAsync(CancellationToken ct = default) { ClearCycle(); ClearLiveProbe(); return Task.CompletedTask; }
    public void ConfigureActiveScanRange(int maxIo) => _maxIo = Math.Max(0, maxIo);
    public void ConfigureModel(ProductModel model)
    {
        string previousPath = _model?.SourcePath ?? string.Empty;
        string previousUploaded = _uploadedModel;
        _model = model; _maxIo = model.MaxIo; ClearCycle(); ClearLiveProbe();
        _compiledModel = model.SourcePath.EndsWith(".model", StringComparison.OrdinalIgnoreCase)
            ? JbzModelCompiler.Compile(model.SourcePath)
            : null;
        _uploadedModel = _compiledModel is not null &&
            previousPath.Equals(model.SourcePath, StringComparison.OrdinalIgnoreCase) &&
            previousUploaded.Equals(_compiledModel.ModelName, StringComparison.OrdinalIgnoreCase)
            ? previousUploaded : string.Empty;
    }
    public Task ConfigureModelAsync(ProductModel model, CancellationToken ct = default) =>
        ConfigureModelAsync(model, null, ct);

    public async Task ConfigureModelAsync(ProductModel model,
        IProgress<JbzModelUploadProgress>? progress, CancellationToken ct = default)
    {
        await _modelOperationGate.WaitAsync(ct);
        Log?.Invoke(this, "UART_OWNER operation=ModelUpload acquired");
        try
        {
            if (progress is not null && !IsConnected)
                throw new IOException("Bo JBZ UART đã mất kết nối trước khi nạp model.");

            // The startup path may call StartScanAsync (which synchronizes the model)
            // and then enter the normal LoadSelectedModel path for the same file.
            // Detect that exact in-process duplicate BEFORE STOP so the second path
            // does not interrupt a healthy scan merely to discover it has nothing to do.
            bool alreadySynchronizedBeforeStop =
                IsConnected &&
                _compiledModel is not null &&
                _model is not null &&
                _model.SourcePath.Equals(model.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                _uploadedModel.Equals(_compiledModel.ModelName, StringComparison.OrdinalIgnoreCase);
            if (alreadySynchronizedBeforeStop)
            {
                ConfigureModel(model);
                Log?.Invoke(this,
                    $"MODEL_UPLOAD skip reason=already-synchronized model={_compiledModel?.ModelName}");
                if (_compiledModel is not null)
                {
                    progress?.Report(new JbzModelUploadProgress(
                        100,
                        _compiledModel.Commands.Count,
                        _compiledModel.Commands.Count,
                        "ALREADY_SYNCHRONIZED"));
                }
                return;
            }

            if (IsConnected && _state == BoardConnectionState.Scanning)
            {
                // The same operation gate covers STOP confirmation and MODEL.
                // On timeout retain Scanning: the board may still be alive and scanning.
                await _serial.StopAsync(ct);
                _state = BoardConnectionState.Ready;
                ClearLiveProbe();
            }
            else if (IsConnected)
                Log?.Invoke(this, "MODEL_UPLOAD skip_stop reason=board-idle");
            ConfigureModel(model);
            if (IsConnected && _compiledModel is not null)
            {
                // StartScanAsync may already have synchronized this exact compiled
                // model earlier in the same startup. Do not stop the scan and send
                // the full MODEL/PINDATA/ARRAY/CON/CONNECTOR transaction twice.
                // This is an in-process synchronization check only; a new process
                // still verifies/uploads normally, so a changed local .model is
                // never trusted only from :MODELNAME metadata.
                if (_uploadedModel.Equals(_compiledModel.ModelName, StringComparison.OrdinalIgnoreCase))
                {
                    Log?.Invoke(this,
                        $"MODEL_UPLOAD skip reason=already-synchronized model={_compiledModel.ModelName}");
                    progress?.Report(new JbzModelUploadProgress(
                        100,
                        _compiledModel.Commands.Count,
                        _compiledModel.Commands.Count,
                        "ALREADY_SYNCHRONIZED"));
                    return;
                }

                Log?.Invoke(this, "MODEL_UPLOAD_BEGIN");
                try
                {
                    await _serial.UploadModelAsync(_compiledModel, progress, ct);
                    await WaitForApplicationReadyAfterModelResetAsync(ct);
                    _uploadedModel = _compiledModel.ModelName;
                    Log?.Invoke(this, "MODEL_UPLOAD_END status=ok");
                }
                catch
                {
                    _uploadFailed = true; _state = BoardConnectionState.Faulted;
                    Log?.Invoke(this, "MODEL_UPLOAD_END status=failed");
                    throw;
                }
            }
        }
        finally { Log?.Invoke(this, "UART_OWNER operation=ModelUpload released"); _modelOperationGate.Release(); }
    }

    private async Task WaitForApplicationReadyAfterModelResetAsync(CancellationToken ct)
    {
        // :RESET can emit BOOT before the application command loop is ready.
        // Do not immediately send :START after MODEL upload; verify a fresh IDN
        // first so START cannot be lost during START PROCESS initialization.
        await Task.Delay(100, ct);
        string identity = await _serial.QueryIdentityAsync(
            ct,
            attempts: 5,
            retryDelay: TimeSpan.FromMilliseconds(150));
        _identity = identity.Trim();
        Log?.Invoke(this, $"MODEL_POSTRESET_READY raw={_identity}");
    }

    public async Task StartScanAsync(BoardScanMode mode = BoardScanMode.Production, CancellationToken ct = default)
    {
        await _modelOperationGate.WaitAsync(ct);
        try
        {
            if (_serial.IsFirmwareUpdating || _state == BoardConnectionState.PausedForHardwareOperation)
                throw new InvalidOperationException("Production scan is locked during firmware maintenance.");
            if (_uploadFailed)
                throw new InvalidOperationException("Model download failed; reconnect the JBZ board before scanning.");
            if (_compiledModel is not null && _uploadedModel != _compiledModel.ModelName)
            {
                try
                {
                    if (_state == BoardConnectionState.Scanning)
                    {
                        await _serial.StopAsync(ct);
                        _state = BoardConnectionState.Ready;
                    }
                    await _serial.UploadModelAsync(_compiledModel, ct);
                    await WaitForApplicationReadyAfterModelResetAsync(ct);
                    _uploadedModel = _compiledModel.ModelName;
                }
                catch { _uploadFailed = true; _state = BoardConnectionState.Faulted; throw; }
            }
            _mode = mode; Interlocked.Increment(ref _generation); ClearCycle(); ClearLiveProbe();
            await _serial.StartAsync(0, ct); _state = BoardConnectionState.Scanning;
        }
        finally { _modelOperationGate.Release(); }
    }
    public async Task StopScanAsync(CancellationToken ct = default)
    {
        await _modelOperationGate.WaitAsync(ct);
        try
        {
            if (IsConnected && IsScanning)
            {
                await _serial.StopAsync(ct);
                _state = BoardConnectionState.Ready;
            }
            ClearLiveProbe();
        }
        finally { _modelOperationGate.Release(); }
    }
    /// <summary>
    /// JBZ Universal Tester removal handshake reproduced from the verified
    /// JBZ_Windows trace: after a committed FAIL the PC sends
    /// :UNCONNECT,500,&lt;physical_pin_count&gt; and waits for firmware
    /// :REMOVAL followed by :UNCONNECT.  :UNCONNECT is the only clean
    /// product-boundary event; CLEAR/OPEN/OTHER are live topology only.
    /// </summary>
    public async Task RequestProductRemovalAsync(
        int delayMilliseconds,
        int physicalPinCount,
        CancellationToken ct = default)
    {
        if (delayMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(delayMilliseconds));
        if (physicalPinCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(physicalPinCount));

        await _modelOperationGate.WaitAsync(ct);
        try
        {
            if (!IsConnected)
                throw new IOException("Bo JBZ UART không kết nối khi gửi UNCONNECT.");

            string command = $":UNCONNECT,{delayMilliseconds},{physicalPinCount}";
            Log?.Invoke(this,
                $"REMOVAL_HANDSHAKE_TX command={command} state={_state} scanning={IsScanning}");
            await _serial.UnconnectAsync(delayMilliseconds, physicalPinCount, ct);
        }
        finally
        {
            _modelOperationGate.Release();
        }
    }

    public Task EnterIdleAsync(CancellationToken ct = default) => StopScanAsync(ct);
    public Task SelectResistanceRouteAsync(ResistanceStep step, CancellationToken ct = default) => _serial.MeasureResistanceAsync(step.Channel, ct);
    public Task ReleaseResistanceRouteAsync(CancellationToken ct = default) => Task.CompletedTask;
    public async Task SetRelayAsync(int relay, CancellationToken ct = default)
    {
        if (!IsPhysicalRelayOutput(relay))
            throw new ArgumentOutOfRangeException(nameof(relay), relay,
                "Universal Tester New only exposes physical Relay 1 (OUTPUT 0) and Relay 5 (OUTPUT 4).");

        await _modelOperationGate.WaitAsync(ct);
        try { await _serial.OutputAsync(relay, true, ct); }
        finally { _modelOperationGate.Release(); }
    }

    public async Task AllRelaysOffAsync(CancellationToken ct = default)
    {
        await _modelOperationGate.WaitAsync(ct);
        try
        {
            await _serial.OutputAsync(Relay1OutputChannel, false, ct);
            await _serial.OutputAsync(Relay5OutputChannel, false, ct);
        }
        finally { _modelOperationGate.Release(); }
    }

    public async Task<string> UpdateFirmwareAsync(
        JbzFirmwareImage image,
        IProgress<JbzFirmwareProgress>? progress,
        CancellationToken ct = default)
    {
        if (_state == BoardConnectionState.PausedForHardwareOperation)
            throw new InvalidOperationException("Board maintenance is already active.");

        // V6: enter maintenance BEFORE stopping scan/relays so the scan watchdog
        // cannot see the intentional STOP as a production stall and race the
        // bootloader with a reopen/model-upload recovery.
        _state = BoardConnectionState.PausedForHardwareOperation;
        try
        {
            bool recoveryPortOpened = false;
            if (!_serial.IsConnected)
            {
                // V7 recovery: a failed/aborted previous session can leave the board
                // alive on the remembered COM but unable to answer *IDN? (for
                // example still inside START/MEASURE or already in BootLoader).
                // Firmware maintenance is allowed to reopen that exact validated
                // COM without requiring the normal identity handshake.
                await OpenFirmwareRecoveryPortAsync(ct);
                recoveryPortOpened = true;
            }

            if (!recoveryPortOpened && _serial.IsConnected)
            {
                try { await _serial.StopAsync(ct); }
                catch (Exception ex) { Log?.Invoke(this, $"FIRMWARE_PREP_STOP_WARN reason={ex.Message}"); }

                // Best-effort safe outputs. A relay ACK failure must not start a
                // second recovery owner while firmware maintenance owns the COM.
                try
                {
                    await _serial.OutputAsync(Relay1OutputChannel, false, ct);
                    await _serial.OutputAsync(Relay5OutputChannel, false, ct);
                }
                catch (Exception ex)
                {
                    Log?.Invoke(this, $"FIRMWARE_PREP_RELAYS_WARN reason={ex.Message}");
                }
            }

            string identity;
            try
            {
                identity = await _serial.UpdateFirmwareAsync(image, progress, ct);
            }
            catch (IOException ex) when (
                ex.Message.Contains("Unable to read JBZ firmware identity/version", StringComparison.OrdinalIgnoreCase))
            {
                // The verified trace can reach OK,FINISH / START PROCESS while the
                // application-side serial object is momentarily closed. That is a
                // post-flash identity race, not a programming failure. Re-open the
                // board here while maintenance still owns the UART, then verify IDN.
                Log?.Invoke(this,
                    $"FIRMWARE_POSTFLASH_RECONNECT_BEGIN reason={ex.Message}");
                identity = await ReconnectAfterFirmwareFlashAsync(ct);
            }

            _identity = identity.Trim();
            _uploadedModel = string.Empty;
            _uploadFailed = false;
            _state = BoardConnectionState.Ready;
            string version = JbzProtocolParser.ExtractFirmwareVersion(_identity);
            Log?.Invoke(this,
                $"FIRMWARE_REFRESHED version={(version.Length == 0 ? "<unknown>" : version)} raw={_identity}");
            return _identity;
        }
        catch
        {
            _identity = string.Empty;
            _state = BoardConnectionState.Faulted;
            throw;
        }
    }

    private async Task OpenFirmwareRecoveryPortAsync(CancellationToken ct)
    {
        string preferred = _preferredPort?.Trim() ?? string.Empty;
        if (preferred.Length == 0)
            preferred = _settings.BoardPortName?.Trim() ?? string.Empty;

        if (preferred.Length == 0)
            throw new IOException(
                "Không có COM bo đã xác nhận trước đó để mở chế độ phục hồi firmware.");

        if (preferred.Equals(_settings.WaterProofMachine.PortName?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            preferred.Equals(_settings.Label.PrinterCom?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"COM phục hồi {preferred} đang được cấu hình cho thiết bị khác.");
        }

        string[] present = JbzSerialBoardTransport.CandidatePorts();
        if (!present.Contains(preferred, StringComparer.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"Không thấy {preferred} trong Windows. Hãy ngắt/cắm lại nguồn hoặc USB của bo rồi thử lại.");
        }

        Log?.Invoke(this, $"FIRMWARE_RECOVERY_BEGIN port={preferred} mode=no-idn");
        await _serial.OpenMaintenancePortAsync(preferred, ct);

        try
        {
            bool stopped = await _serial.TryStopForFirmwareRecoveryAsync(ct);
            Log?.Invoke(this, $"FIRMWARE_RECOVERY_PREP port={preferred} stopAck={stopped}");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            // An already-running bootloader will not understand :STOP. Keep the
            // raw COM open and let the firmware updater try :DOWNLOAD then direct CFT.
            Log?.Invoke(this, $"FIRMWARE_RECOVERY_PREP_WARN port={preferred} reason={ex.Message}");
        }

        await Task.Delay(120, ct);
    }

    private async Task<string> ReconnectAfterFirmwareFlashAsync(CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (_serial.IsConnected)
                    await _serial.DisconnectAsync();
            }
            catch (Exception ex)
            {
                last = ex;
            }

            // Give the application firmware time to leave the bootloader and
            // recreate the COM session. Later attempts wait slightly longer.
            await Task.Delay(150 + (attempt * 100), ct);

            try
            {
                (string port, string idn, _) = await _serial.ConnectFirstAvailableAsync(
                    _preferredPort,
                    ct,
                    [_settings.WaterProofMachine.PortName, _settings.Label.PrinterCom]);

                if (!JbzProtocolParser.IsUniversalTesterIdentity(idn))
                    throw new InvalidDataException(
                        $"COM {port} không trả tên firmware Universal Tester: {idn}");

                _preferredPort = port;
                Log?.Invoke(this,
                    $"FIRMWARE_POSTFLASH_RECONNECT_OK attempt={attempt} port={port} raw={idn.Trim()}");
                return idn.Trim();
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException)
            {
                last = ex;
                Log?.Invoke(this,
                    $"FIRMWARE_POSTFLASH_RECONNECT_RETRY attempt={attempt}/6 reason={ex.Message}");
            }
        }

        throw new IOException(
            "Firmware đã ghi xong nhưng không thể kết nối lại bo để xác nhận phiên bản.",
            last);
    }

    private void OnEvent(object? sender, JbzBoardEvent value)
    {
        switch (value.Family)
        {
            // Rev 1.42 calls openProcess() as OPEN events arrive, before the
            // terminal CIRCUIT event. Use the existing presentation-only
            // ContinuityPreview contract so Test rows react immediately without
            // allowing an in-progress UART scan to decide PASS/FAIL/removal.
            case JbzEventFamily.Clear:
                ClearLiveProbe();
                ResetLiveCycleBaseline();
                PublishAllOpenPreviews();
                break;
            case JbzEventFamily.Open:
                UpdateOpen(value.Numbers ?? []);
                if (value.Numbers is { Count: > 0 })
                    PublishContinuityPreview(value.Numbers[0]);
                break;
            case JbzEventFamily.Short:
                UpdateUnexpected(value.Numbers ?? [], ProductFaultType.ShortCircuit);
                if (value.Numbers is { Count: > 0 })
                    PublishContinuityPreview(value.Numbers[0]);
                break;
            case JbzEventFamily.Other:
                UpdateUnexpected(value.Numbers ?? [], ProductFaultType.WrongWiring);
                if (value.Numbers is { Count: > 0 })
                    PublishContinuityPreview(value.Numbers[0]);
                break;
            case JbzEventFamily.TestPin:
                if (IsConnected && !IsScanning) break;
                PublishProbe(value);
                break;
            case JbzEventFamily.Removal:
                // Verified JBZ_Windows behavior: REMOVAL is only an intermediate
                // notification that the operator is in the removal phase.  Do NOT
                // clear TestView here.  The clean product boundary is :UNCONNECT.
                Log?.Invoke(this, "REMOVAL_HANDSHAKE_RX event=REMOVAL waiting_for=UNCONNECT");
                break;
            case JbzEventFamily.Unconnect:
                // Verified trace boundary.  The firmware has completed its own
                // removal state machine; only now is it safe to discard every
                // OPEN/OTHER snapshot from the previous unit.  The next product
                // must start from a fresh :START.
                Log?.Invoke(this, "REMOVAL_HANDSHAKE_RX event=UNCONNECT boundary=clean");
                ClearCycle();
                ClearLiveProbe();
                _state = BoardConnectionState.Ready;
                Publish(new HashSet<int>(), new Dictionary<int, IReadOnlySet<int>>(), BoardScanMode.Production);
                break;
            case JbzEventFamily.Circuit:
                PublishCircuit(value.Numbers?.FirstOrDefault() ?? -1);
                break;
        }
    }

    private void UpdateOpen(IReadOnlyList<int> numbers)
    {
        if (numbers.Count == 0)
            return;

        int source = numbers[0];
        // Firmware OPEN uses the first number as the canonical source. When
        // fully open it may repeat the source in the missing list:
        //   :OPEN,1,1,31,32
        // and when the net is complete it emits only :OPEN,1.
        int[] missing = numbers.Skip(1).Where(io => io != source).Distinct().ToArray();
        if (missing.Length == 0)
            _open.Remove(source);
        else
            _open[source] = missing;
    }

    private void UpdateUnexpected(
        IReadOnlyList<int> numbers,
        ProductFaultType reportedType)
    {
        if (numbers.Count < 2)
            return;

        int source = numbers[0];
        foreach (int target in numbers.Skip(1).Distinct())
        {
            if (source <= 0 || target <= 0 || source == target)
                continue;

            var key = (Math.Min(source, target), Math.Max(source, target));

            // SHORT is stronger/more specific than OTHER for the same pair.
            // Never downgrade an already reported SHORT to WRONG_WIRING when
            // firmware later emits the reciprocal OTHER line.
            if (_unexpected.TryGetValue(key, out var previous) &&
                previous.Type == ProductFaultType.ShortCircuit)
            {
                continue;
            }

            _unexpected[key] = (source, target, reportedType);
        }
    }

    private void PublishAllOpenPreviews()
    {
        ProductModel? model = _model;
        if (model is null || _mode != BoardScanMode.Production)
            return;

        foreach (int source in model.Nets
                     .Where(net => net.SourceIo > 0 && net.ExpectedActiveIo.Count > 0)
                     .Select(net => net.SourceIo)
                     .Distinct())
        {
            PublishContinuityPreview(source);
        }
    }

    private void PublishContinuityPreview(int source)
    {
        ProductModel? model = _model;
        if (model is null || _mode != BoardScanMode.Production || source <= 0)
            return;

        HashSet<int> actual = model.Nets
            .Where(net => net.SourceIo == source)
            .SelectMany(net => net.ExpectedActiveIo)
            .ToHashSet();

        if (_open.TryGetValue(source, out int[]? missing))
            actual.ExceptWith(missing);

        foreach ((_, (int unexpectedSource, int unexpectedTarget, ProductFaultType _)) in _unexpected)
        {
            if (unexpectedSource == source)
                actual.Add(unexpectedTarget);
        }

        // A Rev 1.42 OTHER/SHORT source may be the reverse endpoint or even an
        // unmapped pin. Still forward that electrical relation for presentation;
        // TestEngine keeps it outside authoritative PASS/FAIL state.
        if (actual.Count == 0 &&
            !model.Nets.Any(net => net.SourceIo == source))
            return;

        var relation = new Dictionary<int, IReadOnlySet<int>>(1)
        {
            [source] = actual
        };
        long previewSequence = _sequence + 1;
        FrameReceived?.Invoke(this, new ScanFrame(
            DateTime.UtcNow,
            0,
            actual,
            Array.Empty<byte>(),
            Complete: false,
            UnknownBytes: 0,
            Sequence: previewSequence,
            ConnectionsBySource: relation,
            Mode: BoardScanMode.Production,
            ExpectedIoCount: _maxIo,
            SourceCount: 1,
            EndMarkerCode: null,
            TerminatorKnown: false,
            ScanGeneration: _generation,
            ExplicitFaults: BuildFaultHintsForSource(source)));
    }

    private void PublishCircuit(int result)
    {
        if (_model is null)
        {
            Log?.Invoke(this, "CIRCUIT ignored: no .model is configured");
            return;
        }

        if (result == 0)
        {
            // CIRCUIT,0 is the firmware's authoritative clean topology result.
            _open.Clear();
            _unexpected.Clear();
        }
        PublishTopologySnapshot(result);
    }

    private void PublishTopologySnapshot(int? circuitResult = null)
    {
        ProductModel? model = _model;
        if (model is null)
            return;

        var connections = model.Nets
            .Where(net => net.SourceIo > 0)
            .GroupBy(net => net.SourceIo)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<int>)group.SelectMany(net => net.ExpectedActiveIo).ToHashSet());

        foreach ((int source, int[] missing) in _open)
        {
            if (!connections.TryGetValue(source, out IReadOnlySet<int>? expected))
                continue;
            connections[source] = expected.Except(missing).ToHashSet();
        }

        foreach ((_, (int source, int target, ProductFaultType _)) in _unexpected)
        {
            var actual = connections.TryGetValue(source, out IReadOnlySet<int>? current)
                ? current.ToHashSet()
                : [];
            actual.Add(target);
            connections[source] = actual;
        }

        HashSet<int> active = connections.Values.SelectMany(values => values).ToHashSet();
        Publish(active, connections, BoardScanMode.Production, circuitResult);
    }

    private void ResetLiveCycleBaseline()
    {
        _open.Clear();
        _unexpected.Clear();
        if (_model is null)
            return;

        foreach (WireNet net in _model.Nets)
        {
            if (net.SourceIo <= 0 || net.ExpectedActiveIo.Count == 0)
                continue;
            _open[net.SourceIo] = net.ExpectedActiveIo.Distinct().ToArray();
        }
    }

    private void PublishProbe(JbzBoardEvent value)
    {
        if (value.Numbers is not { Count: > 0 } || value.Values is not { Count: > 0 }) return;
        bool on = value.Values[0] == "ON";
        if (!_liveProbe.Update(value.Numbers[0], on)) return;
        int[] active = _liveProbe.ActivePins.ToArray();
        Log?.Invoke(this, $"PROBE pin={value.Numbers[0]} state={(on ? "ON" : "OFF")}");
        ProductionProbePreviewReceived?.Invoke(this, new(DateTime.UtcNow, active, 1, active.Length, _sequence + 1, _generation));
    }
    private void ClearLiveProbe()
    {
        if (_liveProbe.Clear())
            ProductionProbePreviewReceived?.Invoke(this, new(DateTime.UtcNow, [], 1, 0, _sequence + 1, _generation));
    }
    private void Publish(
        IReadOnlySet<int> active,
        IReadOnlyDictionary<int, IReadOnlySet<int>> connections,
        BoardScanMode mode,
        int? circuitResult = null)
    {
        _lastFrameUtc = DateTime.UtcNow;
        _frames++;
        _sequence++;
        LastFrameSourceCount = connections.Count;

        FrameReceived?.Invoke(this, new ScanFrame(
            _lastFrameUtc,
            0,
            active,
            Encoding.ASCII.GetBytes("JBZ-UART"),
            true,
            Sequence: _sequence,
            ConnectionsBySource: connections,
            Mode: mode,
            ExpectedIoCount: _maxIo,
            SourceCount: connections.Count,
            ScanGeneration: _generation,
            ExplicitFaults: BuildAllFaultHints(),
            CircuitResult: circuitResult));
    }

    private IReadOnlyDictionary<(int SourceIo, int TargetIo), ProductFaultType> BuildAllFaultHints()
    {
        if (_unexpected.Count == 0)
            return new Dictionary<(int SourceIo, int TargetIo), ProductFaultType>();

        return _unexpected.ToDictionary(
            pair => (pair.Key.Low, pair.Key.High),
            pair => pair.Value.Type);
    }

    private IReadOnlyDictionary<(int SourceIo, int TargetIo), ProductFaultType> BuildFaultHintsForSource(int source)
    {
        if (_unexpected.Count == 0)
            return new Dictionary<(int SourceIo, int TargetIo), ProductFaultType>();

        return _unexpected
            .Where(pair => pair.Value.Source == source || pair.Value.Target == source)
            .ToDictionary(
                pair => (pair.Key.Low, pair.Key.High),
                pair => pair.Value.Type);
    }
    private void ClearCycle() { _open.Clear(); _unexpected.Clear(); }
    public async ValueTask DisposeAsync() { _serial.EventReceived -= OnEvent; await _serial.DisposeAsync(); _state = BoardConnectionState.Disconnected; }
}
