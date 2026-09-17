using JBZUniveresalLunix.Models;

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace JBZUniveresalLunix.Services;

public sealed record ExpectedNetworkDiagnostic(
    string Key,
    string Name,
    string Category,
    int SourceIo,
    IReadOnlyList<int> ExpectedIo,
    bool Passed)
{
    public string Display => ExpectedIo.Count == 0
        ? $"{Name}: IO{SourceIo}"
        : $"{Name}: IO{SourceIo}<->{string.Join(",", ExpectedIo.Select(io => $"IO{io}"))}";
}

public sealed record PassGateDiagnostics(
    int ExpectedNetCount,
    int PassedNetCount,
    IReadOnlyList<ExpectedNetworkDiagnostic> ExpectedNetworks,
    IReadOnlyList<ExpectedNetworkDiagnostic> RemainingNetworks,
    int WrongCandidateCount,
    int WrongConfirmedCount,
    int ShortCandidateCount,
    int ShortConfirmedCount,
    bool HasProductActivity,
    bool HasExpectedSourceCoverage,
    bool ProductStable,
    bool ContactUnstable,
    bool ContactLossTimedOut,
    bool ContinuityPassed,
    bool HasWiringFault,
    bool LastFrameValid,
    long LastFrameSequence,
    int LastFrameUnknownBytes);

/// <summary>
/// Authoritative electrical state produced from one complete Production frame.
/// Product evidence, realtime fault eligibility and full continuity are separate
/// facts; consumers must not infer one from another.
/// </summary>
public readonly record struct ProductionElectricalSnapshot(
    long FrameSequence,
    long ScanGeneration,
    bool ProductEvidence,
    bool StableProductPresence,
    bool RealtimeEvaluationEnabled,
    bool ContinuityComplete,
    bool HasConfirmedWiringFault,
    double EngineComputeMilliseconds);

public enum ProductPresenceState
{
    WaitingForProduct = 0,
    Present = 1
}

/// <summary>
/// Authoritative product evidence from one complete Production frame. A real
/// non-self electrical edge can be expected, wrong or short; raw self-activity
/// and Probe evidence remain diagnostic-only and cannot create presence.
/// </summary>
public readonly record struct ProductEvidenceSnapshot(
    long FrameSequence,
    long ScanGeneration,
    int RawActiveIoCount,
    int ExpectedConnectedCount,
    int WrongCandidateCount,
    int WrongConfirmedCount,
    int ShortCandidateCount,
    int ShortConfirmedCount,
    bool ProbeEvidence,
    bool ValidProductEvidence,
    ProductPresenceState State,
    string Reason);

public sealed record TestEnginePresentationSnapshot(
    ProductionElectricalSnapshot Electrical,
    bool Removal,
    IReadOnlyList<FaultRow> Rows,
    double RowBuildMilliseconds);

/// <summary>
/// V10.3 continuity engine reconstructed from the 2026-08-07 production and
/// TestPin traces. RX is a SOURCE -> TARGET relation table (80/81 -> A0/A1),
/// terminated by C0 00. The first I/O of each THT wire-name network is the
/// production source/stimulus; remaining I/Os are the expected targets.
/// </summary>
public sealed class TestEngine : IDisposable
{
    public sealed class PreparedModelState
    {
        internal ProductModel Model { get; init; } = null!;
        internal Dictionary<int, int> ComponentByIo { get; init; } = null!;
        internal HashSet<int> ModelIo { get; init; } = null!;
        internal Dictionary<PinRecord, WireNet[]> NetsByPin { get; init; } = null!;
        internal Dictionary<PinRecord, int> DisplayOrderByPin { get; init; } = null!;
        internal Dictionary<WireNet, int> DisplayOrderByNet { get; init; } = null!;
        internal Dictionary<WireNet, string> ConfirmationKeyByNet { get; init; } = null!;
        internal Dictionary<ClipBranch, string> ConfirmationKeyByClip { get; init; } = null!;
    }
    const int ClipDisplayOrderBase = -1_000_000;
    const string PendingConnectionStatus = "CHƯA KẾT NỐI";

    readonly IBoardTransport _board;
    readonly KeysightVisaService _visa;
    readonly AppSettings _settings;
    readonly ProductionSettings _production;
    readonly ProductionFaultConfirmationGate _faultConfirmation;
    readonly object _gate = new();
    readonly SemaphoreSlim _relayPulseGate = new(1, 1);

    ProductModel? _model;
    readonly HashSet<string> _passedNets = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> _stableCounters = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<int> _currentActive = [];
    readonly Dictionary<int, HashSet<int>> _currentConnections = [];
    // Snapshot of the electrical edges that belonged to the product at the moment
    // a committed result entered THÁO SẢN PHẨM. Removal is complete when these
    // original edges are all gone. New incidental OTHER/SHORT contacts that appear
    // later must not keep the previous product locked forever.
    readonly HashSet<(int LowIo, int HighIo)> _productRemovalBaselineEdges = [];
    bool _productRemovalBaselineArmed;
    // Presentation-only overlay populated from changed SOURCE relations before C0.
    // It never participates in PASS/FAIL, fault debounce, relay or counters.
    readonly Dictionary<int, HashSet<int>> _continuityPreviewConnections = [];
    // Rev 1.42 renders OTHER/SHORT immediately in TestDialog::viewOther().
    // Keep the same operator feedback as a presentation-only preview; these
    // entries are never copied into _wiringFaults/_candidateWiringFaults and
    // therefore cannot decide PASS/FAIL, relay actions or production counters.
    readonly Dictionary<int, HashSet<WiringFaultPair>> _continuityPreviewWiringBySource = [];
    long _continuityPreviewSequence;
    readonly HashSet<int> _unexpectedIo = [];
    readonly HashSet<WiringFaultPair> _wiringFaults = [];
    readonly HashSet<WiringFaultPair> _candidateWiringFaults = [];
    // Probe frames are kept outside ProcessFrame. If a weaker leading frame was
    // evaluated before the classifier confirmed the probe signature, exclude
    // only edges touching the confirmed probe IO from product evidence until
    // the next authoritative non-probe frame replaces the snapshot.
    readonly HashSet<int> _probeEvidenceExcludedIo = [];
    readonly HashSet<string> _confirmedOpenKeys = new(StringComparer.Ordinal);
    readonly HashSet<string> _latchedClipKeys = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<(int SourceIo, int TargetIo)> _unexpectedPairScratch = [];
    Dictionary<int, int> _componentByIo = [];
    HashSet<int> _modelIo = [];
    Dictionary<int, int> _actualComponentByIo = [];
    Dictionary<PinRecord, WireNet[]> _netsByPin = new(ReferenceEqualityComparer.Instance);
    Dictionary<WireNet, string> _confirmationKeyByNet = new(ReferenceEqualityComparer.Instance);
    Dictionary<ClipBranch, string> _confirmationKeyByClip = new(ReferenceEqualityComparer.Instance);
    Dictionary<PinRecord, int> _displayOrderByPin = new(ReferenceEqualityComparer.Instance);
    Dictionary<WireNet, int> _displayOrderByNet = new(ReferenceEqualityComparer.Instance);
    Dictionary<WireNet, FaultRow[]> _displayRowsByNet = new(ReferenceEqualityComparer.Instance);
    Dictionary<ClipBranch, FaultRow> _displayRowByClip = new(ReferenceEqualityComparer.Instance);
    FaultRow? _clipCommonDisplayRow;
    readonly Dictionary<string, bool> _expectedConnectionScratch = new(StringComparer.Ordinal);
    volatile bool _frameProcessingEnabled = true;
    bool _forceNextFrameChanged = true;
    bool _contactUnstable;
    bool _contactLossTimedOut;
    bool _productStable;
    bool _readyToEvaluateProductFaults;
    bool _hasExpectedSourceCoverage;
    bool _lastFrameValid;
    long _lastFrameSequence;
    long _lastFrameScanGeneration;
    long _framesProcessed;
    int _lastFrameUnknownBytes;
    double _lastEngineComputeMilliseconds;
    bool _disposed;

    public event EventHandler? Changed;

    public IReadOnlyCollection<string> PassedNets => _passedNets;
    public IReadOnlyCollection<int> UnexpectedIo => _unexpectedIo;

    public IReadOnlyCollection<WiringFaultPair> WiringFaults
    {
        get
        {
            lock (_gate)
                return _wiringFaults.ToArray();
        }
    }
    public int ExpectedNetCount
    {
        get
        {
            lock (_gate)
                return _model is null ? 0 : ProductionExpectedNetCount(_model);
        }
    }
    public bool HasResistanceSteps => _model is not null && ResistanceStepCount > 0;
    public int ResistanceStepCount => _model is null
        ? 0
        : ResistanceMeasurementPlan.BuildEnabledSteps(_production).Count;

    public bool HasWiringFault
    {
        get
        {
            lock (_gate)
                return _wiringFaults.Count > 0;
        }
    }

    public bool HasConfirmedOpenCircuit
    {
        get { lock (_gate) return _confirmedOpenKeys.Count > 0; }
    }

    public bool HasContactInstability
    {
        get { lock (_gate) return _contactUnstable; }
    }

    public bool ContactLossTimedOut
    {
        get { lock (_gate) return _contactLossTimedOut; }
    }

    public bool ReadyToEvaluateProductFaults
    {
        get { lock (_gate) return _readyToEvaluateProductFaults; }
    }

    /// <summary>
    /// Wrong-wire/short evaluation starts with the first real product edge. It
    /// intentionally does not wait for source coverage or full continuity.
    /// </summary>
    public bool RealtimeEvaluationEnabled
    {
        get
        {
            lock (_gate)
                return _model is not null && HasProductActivityUnsafe(_model);
        }
    }

    public bool HasExpectedSourceCoverage
    {
        get { lock (_gate) return _hasExpectedSourceCoverage; }
    }

    public long FramesProcessed => Interlocked.Read(ref _framesProcessed);

    public bool LastFrameValid
    {
        get { lock (_gate) return _lastFrameValid; }
    }

    public long LastFrameSequence
    {
        get { lock (_gate) return _lastFrameSequence; }
    }

    public int LastFrameUnknownBytes
    {
        get { lock (_gate) return _lastFrameUnknownBytes; }
    }

    public ProductionElectricalSnapshot GetProductionElectricalSnapshot()
    {
        lock (_gate)
            return BuildProductionElectricalSnapshotUnsafe();
    }

    public ProductEvidenceSnapshot GetProductEvidenceSnapshot()
    {
        lock (_gate)
            return BuildProductEvidenceSnapshotUnsafe();
    }

    // Compatibility shim for older callers/tests. Universal Tester New does not
    // have a separate removal presentation; the same missing/open
    // pair table is used in every phase.
    public IReadOnlyList<FaultRow> BuildRemovalRows() => BuildRows();

    public TestEnginePresentationSnapshot CapturePresentationSnapshot(bool removal)
    {
        long started = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            // Universal Tester New always presents the inverse-open table:
            // missing/open pairs are visible, connected pairs disappear.
            // There is no separate removal presentation.
            IReadOnlyList<FaultRow> rows = BuildRows();
            double rowBuildMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return new TestEnginePresentationSnapshot(
                BuildProductionElectricalSnapshotUnsafe(),
                false,
                rows,
                rowBuildMs);
        }
    }

    private ProductionElectricalSnapshot BuildProductionElectricalSnapshotUnsafe()
    {
        ProductEvidenceSnapshot evidence = BuildProductEvidenceSnapshotUnsafe();
        bool productEvidence = evidence.ValidProductEvidence;
        int expected = _model is null ? 0 : ProductionExpectedNetCount(_model);
        int passed = _model is null ? 0 : CountPassedExpectedUnsafe(_model);
        bool continuityComplete = _model is not null &&
                                  expected > 0 &&
                                  passed == expected &&
                                  _candidateWiringFaults.Count == 0 &&
                                  _wiringFaults.Count == 0;
        return new ProductionElectricalSnapshot(
            _lastFrameSequence,
            _lastFrameScanGeneration,
            productEvidence,
            _productStable,
            productEvidence,
            continuityComplete,
            _wiringFaults.Count > 0,
            _lastEngineComputeMilliseconds);
    }

    private ProductEvidenceSnapshot BuildProductEvidenceSnapshotUnsafe()
    {
        int expectedConnected = CountExpectedConnectivityEdgesUnsafe();
        int wrongCandidates = _candidateWiringFaults.Count(item =>
            item.FaultType == ProductFaultType.WrongWiring);
        int wrongConfirmed = _wiringFaults.Count(item =>
            item.FaultType == ProductFaultType.WrongWiring);
        int shortCandidates = _candidateWiringFaults.Count(item =>
            item.FaultType == ProductFaultType.ShortCircuit);
        int shortConfirmed = _wiringFaults.Count(item =>
            item.FaultType == ProductFaultType.ShortCircuit);
        bool valid = _model is not null && HasProductActivityUnsafe(_model);
        string reason = !valid
            ? _currentActive.Count > 0 ? "RAW_ACTIVITY_ONLY" : "NO_ACTIVITY"
            : wrongConfirmed > 0
                ? "WRONG_CONFIRMED"
                : shortConfirmed > 0
                    ? "SHORT_CONFIRMED"
                    : wrongCandidates > 0
                        ? "WRONG_CANDIDATE"
                        : shortCandidates > 0
                            ? "SHORT_CANDIDATE"
                            : "EXPECTED_CONNECTIVITY";

        return new ProductEvidenceSnapshot(
            _lastFrameSequence,
            _lastFrameScanGeneration,
            _currentActive.Count,
            expectedConnected,
            wrongCandidates,
            wrongConfirmed,
            shortCandidates,
            shortConfirmed,
            ProbeEvidence: false,
            valid,
            valid ? ProductPresenceState.Present : ProductPresenceState.WaitingForProduct,
            reason);
    }

    private int CountExpectedConnectivityEdgesUnsafe()
    {
        var expectedEdges = new HashSet<(int Low, int High)>();
        foreach (KeyValuePair<int, HashSet<int>> pair in _currentConnections)
        {
            if (!_componentByIo.TryGetValue(pair.Key, out int sourceComponent))
                continue;

            foreach (int target in pair.Value)
            {
                if (target == pair.Key ||
                    _probeEvidenceExcludedIo.Contains(pair.Key) ||
                    _probeEvidenceExcludedIo.Contains(target) ||
                    !_componentByIo.TryGetValue(target, out int targetComponent) ||
                    sourceComponent != targetComponent)
                {
                    continue;
                }

                expectedEdges.Add((Math.Min(pair.Key, target), Math.Max(pair.Key, target)));
            }
        }

        return expectedEdges.Count;
    }

    /// <summary>
    /// Có ít nhất một cạnh continuity vật lý hợp lệ trong snapshot Production.
    /// Cạnh đúng, sai dây/chập vào I/O ngoài THT đều là bằng chứng sản phẩm;
    /// chỉ self-edge, I/O ignored và I/O đang bị Probe exclusion mới bị loại.
    /// </summary>
    public bool HasProductActivity
    {
        get
        {
            lock (_gate)
            {
                if (_model is null)
                    return false;

                return _currentConnections.Any(pair =>
                    pair.Value.Any(target =>
                        IsProductConnectivityEdgeUnsafe(_model, pair.Key, target)));
            }
        }
    }

    public bool IsProductReleased
    {
        get
        {
            lock (_gate)
            {
                if (_model is null)
                    return true;

                // CLIP A0/AO -> I/O cấu hình là một phần continuity sản phẩm,
                // nên chỉ released khi các quan hệ đó cũng đã mất.
                return !_currentConnections.Any(pair =>
                    pair.Value.Any(target =>
                        IsProductConnectivityEdgeUnsafe(_model, pair.Key, target)));
            }
        }
    }

    /// <summary>
    /// SOURCE mà firmware JBZ phải báo OPEN khi toàn bộ harness đã rời JIG.
    /// Chỉ gồm network production thật; single-pin/probe-only không được dùng
    /// để chặn ProductRemoved vì firmware không phát OPEN cho các row đó.
    /// </summary>
    public int[] GetRequiredRemovalSourceIos()
    {
        lock (_gate)
        {
            if (_model is null)
                return [];

            HashSet<int> required = _model.Nets
                .Where(IsEligibleProductionNet)
                .Select(net => net.SourceIo)
                .Where(io => io > 0)
                .ToHashSet();

            if (_model.Clip is { } clip &&
                clip.CommonIo > 0 &&
                clip.Branches.Any(branch => IsEligibleClipBranch(clip, branch)))
            {
                required.Add(clip.CommonIo);
            }

            return required.OrderBy(io => io).ToArray();
        }
    }

    /// <summary>
    /// Universal Tester New không luôn phát C0/CIRCUIT cho trạng thái all-open.
    /// TestViewModel vì vậy xác nhận một sweep UART thật: CLEAR -> OPEN cho toàn
    /// bộ SOURCE production và không có OTHER/SHORT/CIRCUIT trong khoảng guard.
    /// Khi sweep đó đã được xác nhận, snapshot cũ (đặc biệt reverse OTHER như
    /// IO8->IO7) phải bị xoá atomically để ProductRemoved có thể hoàn tất.
    ///
    /// Đây là đường removal-only, không được gọi trong Continuity/PASS evaluation.
    /// </summary>
    public bool ConfirmProductReleasedFromOpenSweep(
        IReadOnlyCollection<int> openSourceIos,
        long sweepId)
    {
        ArgumentNullException.ThrowIfNull(openSourceIos);

        int[] required;
        lock (_gate)
        {
            if (_disposed || !_frameProcessingEnabled || _model is null)
                return false;

            required = _model.Nets
                .Where(IsEligibleProductionNet)
                .Select(net => net.SourceIo)
                .Where(io => io > 0)
                .Distinct()
                .OrderBy(io => io)
                .ToArray();

            if (_model.Clip is { } clip &&
                clip.CommonIo > 0 &&
                clip.Branches.Any(branch => IsEligibleClipBranch(clip, branch)) &&
                !required.Contains(clip.CommonIo))
            {
                required = required.Append(clip.CommonIo).OrderBy(io => io).ToArray();
            }

            if (required.Length == 0)
                return false;

            HashSet<int> opened = openSourceIos
                .Where(io => io > 0)
                .ToHashSet();
            if (required.Any(io => !opened.Contains(io)))
                return false;

            // Sweep all-open đã được VM debounce bằng raw UART. Không giữ bất kỳ
            // relation cũ nào vì OTHER hai chiều có thể còn sót một reverse source
            // mà firmware không bao giờ phát OPEN (ví dụ IO8 của IO7<->IO8).
            ResetUnsafe();
        }

        AsyncFileLogService.Current.Performance(
            $"REMOVAL_OPEN_SWEEP_CONFIRMED sweep={sweepId} " +
            $"sources=[{string.Join(",", required.Select(io => $"IO{io}"))}]");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // Universal Tester New removal is binary: after a committed result the UI stays
    // in one THÁO SẢN PHẨM state until IsProductReleased becomes true.
    // No partial-removal milestone is kept.

    public int MissingConnectionCount
    {
        get
        {
            lock (_gate)
            {
                if (_model is null)
                    return 0;

                int ordinaryMissing = _model.Nets.Sum(net =>
                {
                    if (!IsEligibleProductionNet(net))
                        return 0;

                    return CountDisconnectedEndpoints(net);
                });

                int clipMissing = _model.Clip?.Branches.Count(branch =>
                    !_passedNets.Contains(branch.NetName) &&
                    !_latchedClipKeys.Contains(ClipConfirmationKey(branch.NetName))) ?? 0;

                return ordinaryMissing + clipMissing;
            }
        }
    }

    public bool ContinuityPassed
    {
        get
        {
            lock (_gate)
            {
                int expected = _model is null ? 0 : ProductionExpectedNetCount(_model);
                int passed = _model is null ? 0 : CountPassedExpectedUnsafe(_model);

                return _model is not null &&
                       expected > 0 &&
                       passed == expected &&
                       _candidateWiringFaults.Count == 0 &&
                       _wiringFaults.Count == 0;
            }
        }
    }

    public PassGateDiagnostics GetPassGateDiagnostics()
    {
        lock (_gate)
        {
            ProductModel? model = _model;
            ExpectedNetworkDiagnostic[] expectedNetworks = model is null
                ? []
                : BuildExpectedNetworkDiagnosticsUnsafe(model);
            ExpectedNetworkDiagnostic[] remainingNetworks = expectedNetworks
                .Where(item => !item.Passed)
                .ToArray();

            return new PassGateDiagnostics(
                expectedNetworks.Length,
                expectedNetworks.Count(item => item.Passed),
                expectedNetworks,
                remainingNetworks,
                _candidateWiringFaults.Count(item => item.FaultType == ProductFaultType.WrongWiring),
                _wiringFaults.Count(item => item.FaultType == ProductFaultType.WrongWiring),
                _candidateWiringFaults.Count(item => item.FaultType == ProductFaultType.ShortCircuit),
                _wiringFaults.Count(item => item.FaultType == ProductFaultType.ShortCircuit),
                model is not null && HasProductActivityUnsafe(model),
                _hasExpectedSourceCoverage,
                _productStable,
                _contactUnstable,
                _contactLossTimedOut,
                ContinuityPassed,
                _wiringFaults.Count > 0,
                _lastFrameValid,
                _lastFrameSequence,
                _lastFrameUnknownBytes);
        }
    }

    public TestEngine(
        IBoardTransport board,
        KeysightVisaService visa,
        AppSettings settings,
        ProductionSettings? production = null,
        TimeProvider? timeProvider = null)
    {
        _board = board;
        _visa = visa;
        _settings = settings;
        _production = production ?? new ProductionSettings();
        _faultConfirmation = new ProductionFaultConfirmationGate(_production, timeProvider);
        // V11.8: TestEngine KHÔNG subscribe trực tiếp board nữa.
        // TestViewModel là router duy nhất quyết định Production/Probe, tránh
        // tuyệt đối snapshot đầu dò lọt vào logic đấu sai/chập.
    }

    public PreparedModelState PrepareModel(ProductModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Build immutable topology outside the frame lock. Only the short
        // reference swap/reset is serialized with ProcessFrame.
        Dictionary<int, int> componentByIo = BuildExpectedComponents(model);
        HashSet<int> modelIo = model.Pins
            .Select(pin => pin.IoNumber)
            .Where(io => io > 0 && !model.IgnoredIo.Contains(io))
            .ToHashSet();
        if (model.Clip is not null)
        {
            if (model.Clip.CommonIo > 0)
                modelIo.Add(model.Clip.CommonIo);
            foreach (ClipBranch branch in model.Clip.Branches)
            {
                if (branch.TargetIo > 0)
                    modelIo.Add(branch.TargetIo);
            }
        }
        Dictionary<PinRecord, WireNet[]> netsByPin = BuildNetsByPin(model);
        Dictionary<PinRecord, int> displayOrderByPin = BuildDisplayOrderByPin(model);
        Dictionary<WireNet, int> displayOrderByNet = BuildNetworkDisplayOrder(model);
        var confirmationKeyByNet = new Dictionary<WireNet, string>(ReferenceEqualityComparer.Instance);
        foreach (WireNet net in model.Nets)
            confirmationKeyByNet[net] = NetConfirmationKey(net.Name);

        var confirmationKeyByClip = new Dictionary<ClipBranch, string>(ReferenceEqualityComparer.Instance);
        foreach (ClipBranch branch in model.Clip?.Branches ?? [])
            confirmationKeyByClip[branch] = ClipConfirmationKey(branch.NetName);

        return new PreparedModelState
        {
            Model = model,
            ComponentByIo = componentByIo,
            ModelIo = modelIo,
            NetsByPin = netsByPin,
            DisplayOrderByPin = displayOrderByPin,
            DisplayOrderByNet = displayOrderByNet,
            ConfirmationKeyByNet = confirmationKeyByNet,
            ConfirmationKeyByClip = confirmationKeyByClip
        };
    }

    public void CommitPreparedModel(PreparedModelState prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        lock (_gate)
        {
            _model = prepared.Model;
            _componentByIo = prepared.ComponentByIo;
            _modelIo = prepared.ModelIo;
            _netsByPin = prepared.NetsByPin;
            _displayOrderByPin = prepared.DisplayOrderByPin;
            _displayOrderByNet = prepared.DisplayOrderByNet;
            _confirmationKeyByNet = prepared.ConfirmationKeyByNet;
            _confirmationKeyByClip = prepared.ConfirmationKeyByClip;
            // Cache toàn bộ text/topology tĩnh
            // một lần khi load THT; frame chỉ chọn row cần hiện, không tạo lại
            // Connector/Pin/WireName/Color/IO-CN-PN cho hàng trăm endpoint.
            _displayRowsByNet = new Dictionary<WireNet, FaultRow[]>(
                ReferenceEqualityComparer.Instance);
            foreach (WireNet net in prepared.Model.Nets)
                _displayRowsByNet[net] = CreateNetworkMappingRowsCore(prepared.Model, net, PendingConnectionStatus);

            _displayRowByClip = new Dictionary<ClipBranch, FaultRow>(
                ReferenceEqualityComparer.Instance);
            foreach (ClipBranch branch in prepared.Model.Clip?.Branches ?? [])
                _displayRowByClip[branch] = CreateMissingClipConnectionRow(
                    prepared.Model.Clip!, branch, PendingConnectionStatus);
            _clipCommonDisplayRow = prepared.Model.Clip is null
                ? null
                : CreateClipCommonDisplayRow(prepared.Model.Clip, PendingConnectionStatus);
            _latchedClipKeys.Clear();
            ClearProductRemovalBaselineUnsafe();
            ResetUnsafe();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetModel(ProductModel model) => CommitPreparedModel(PrepareModel(model));

    static Dictionary<PinRecord, WireNet[]> BuildNetsByPin(ProductModel model)
    {
        // BuildRows được gọi rất thường xuyên. Không được mỗi lần lại quét
        // model.Nets cho từng pin (O(P*N)). Dựng lookup một lần khi load THT.
        var temp = new Dictionary<PinRecord, List<WireNet>>(ReferenceEqualityComparer.Instance);

        foreach (PinRecord pin in model.Pins)
            temp[pin] = [];

        foreach (WireNet net in model.Nets)
        {
            foreach (PinRecord pin in net.Pins)
            {
                if (!temp.TryGetValue(pin, out List<WireNet>? list))
                {
                    list = [];
                    temp[pin] = list;
                }

                list.Add(net);
            }
        }

        var result = new Dictionary<PinRecord, WireNet[]>(
            ReferenceEqualityComparer.Instance);

        foreach (KeyValuePair<PinRecord, List<WireNet>> pair in temp)
            result[pair.Key] = pair.Value.ToArray();

        return result;
    }

    static Dictionary<PinRecord, int> BuildDisplayOrderByPin(ProductModel model)
    {
        var result = new Dictionary<PinRecord, int>(ReferenceEqualityComparer.Instance);
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string NormalizeConnector(string connector) => (connector ?? string.Empty).Trim();

        string Find(string connector)
        {
            connector = NormalizeConnector(connector);
            if (!parent.TryGetValue(connector, out string? value))
            {
                parent[connector] = connector;
                return connector;
            }

            if (value.Equals(connector, StringComparison.OrdinalIgnoreCase))
                return connector;

            string root = Find(value);
            parent[connector] = root;
            return root;
        }

        void Union(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return;

            string rootA = Find(a);
            string rootB = Find(b);
            if (!rootA.Equals(rootB, StringComparison.OrdinalIgnoreCase))
                parent[rootB] = rootA;
        }

        foreach (PinRecord pin in model.Pins)
        {
            if (!string.IsNullOrWhiteSpace(pin.Connector))
                Find(pin.Connector);
        }

        foreach (WireNet net in model.Nets)
        {
            string[] connectors = net.Pins
                .Select(pin => NormalizeConnector(pin.Connector))
                .Where(connector => !string.IsNullOrWhiteSpace(connector))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            for (int i = 1; i < connectors.Length; i++)
                Union(connectors[0], connectors[i]);
        }

        if (model.Clip is not null)
        {
            foreach (ClipBranch branch in model.Clip.Branches)
            {
                Union(model.Clip.CommonPin.Connector, branch.ClipPin.Connector);
                if (branch.TargetPin is not null)
                    Union(model.Clip.CommonPin.Connector, branch.TargetPin.Connector);
            }
        }

        var pinsByConnector = model.Pins
            .Where(pin => !string.IsNullOrWhiteSpace(pin.Connector))
            .GroupBy(pin => NormalizeConnector(pin.Connector), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var connectorsByComponent = pinsByConnector.Keys
            .GroupBy(Find, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                string[] connectors = group.ToArray();
                int minOriginal = connectors
                    .SelectMany(connector => pinsByConnector[connector])
                    .Select(pin => pin.OriginalOrder > 0 ? pin.OriginalOrder : int.MaxValue)
                    .DefaultIfEmpty(int.MaxValue)
                    .Min();

                return new
                {
                    Connectors = connectors,
                    Priority = connectors.Any(IsAoFamilyConnector) ? 0 : 1,
                    MinOriginal = minOriginal,
                    Natural = connectors.Select(NaturalSortKey).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).First()
                };
            })
            .OrderBy(group => group.Priority)
            .ThenBy(group => group.MinOriginal)
            .ThenBy(group => group.Natural, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int order = 0;
        foreach (var component in connectorsByComponent)
        {
            foreach (string connector in component.Connectors
                         .OrderBy(AoFamilyConnectorRank)
                         .ThenBy(connector => ConnectorOriginalOrder(connector, pinsByConnector))
                         .ThenBy(NaturalSortKey, StringComparer.OrdinalIgnoreCase))
            {
                foreach (PinRecord pin in pinsByConnector[connector]
                             .OrderBy(pin => pin.OriginalOrder > 0 ? pin.OriginalOrder : int.MaxValue)
                             .ThenBy(pin => NaturalSortKey(pin.PinNumber), StringComparer.OrdinalIgnoreCase)
                             .ThenBy(pin => pin.IoNumber))
                {
                    result[pin] = order++;
                }
            }
        }

        foreach (PinRecord pin in model.Pins)
        {
            if (!result.ContainsKey(pin))
                result[pin] = order++;
        }

        return result;
    }

    static int ConnectorOriginalOrder(
        string connector,
        IReadOnlyDictionary<string, PinRecord[]> pinsByConnector)
    {
        return pinsByConnector.TryGetValue(connector, out PinRecord[]? pins)
            ? pins.Select(pin => pin.OriginalOrder > 0 ? pin.OriginalOrder : int.MaxValue)
                .DefaultIfEmpty(int.MaxValue)
                .Min()
            : int.MaxValue;
    }

    static Dictionary<WireNet, int> BuildNetworkDisplayOrder(ProductModel model)
    {
        var result = new Dictionary<WireNet, int>(ReferenceEqualityComparer.Instance);
        var entries = model.Nets
            .Select((net, modelIndex) => new
            {
                Net = net,
                ModelIndex = modelIndex,
                RelationKey = ConnectorRelationKey(net),
                Priority = IsAoPriorityNet(net) ? 0 : 1,
                FirstOriginal = FirstOriginalOrder(net),
                RelationNatural = ConnectorRelationNaturalKey(net)
            })
            .Where(entry => IsEligibleProductionNet(entry.Net))
            .ToArray();

        var groupOrder = entries
            .GroupBy(entry => entry.RelationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Key = group.Key,
                Priority = group.Min(entry => entry.Priority),
                FirstOriginal = group.Min(entry => entry.FirstOriginal),
                FirstModelIndex = group.Min(entry => entry.ModelIndex),
                Natural = group
                    .Select(entry => entry.RelationNatural)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .First()
            })
            .OrderBy(group => group.Priority)
            .ThenBy(group => group.FirstOriginal)
            .ThenBy(group => group.FirstModelIndex)
            .ThenBy(group => group.Natural, StringComparer.OrdinalIgnoreCase)
            .Select((group, index) => new { group.Key, Index = index })
            .ToDictionary(group => group.Key, group => group.Index, StringComparer.OrdinalIgnoreCase);

        int order = 0;
        foreach (var group in entries
                     .GroupBy(entry => entry.RelationKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => groupOrder[group.Key]))
        {
            foreach (var entry in group
                         .OrderBy(item => item.Priority)
                         .ThenBy(item => item.FirstOriginal)
                         .ThenBy(item => item.ModelIndex))
            {
                result[entry.Net] = order++ * 1000;
            }
        }

        return result;
    }

    static bool IsAoPriorityNet(WireNet net) =>
        string.Equals((net.Name ?? string.Empty).Trim(), "AO", StringComparison.OrdinalIgnoreCase) ||
        NetworkConnectors(net).Any(IsAoFamilyConnector);

    static bool IsAoFamilyConnector(string? connector)
    {
        string value = (connector ?? string.Empty).Trim();
        if (value.Equals("AO", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("A0", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Regex.IsMatch(value, @"^a\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    static int AoFamilyConnectorRank(string? connector)
    {
        string value = (connector ?? string.Empty).Trim();
        if (value.Equals("AO", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("A0", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        Match branch = Regex.Match(value, @"^a(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (branch.Success &&
            int.TryParse(branch.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            return 1 + Math.Max(0, number);
        }

        return int.MaxValue;
    }

    static string ConnectorRelationKey(WireNet net)
    {
        string[] connectors = NetworkConnectors(net)
            .Select(NaturalSortKey)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return connectors.Length == 0
            ? $"<no-connector>{net.Name}"
            : string.Join("\u001f", connectors);
    }

    static string ConnectorRelationNaturalKey(WireNet net) =>
        string.Join("\u001f", NetworkConnectors(net).Select(NaturalSortKey));

    static string[] NetworkConnectors(WireNet net)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connectors = new List<string>();

        foreach (PinRecord pin in net.Pins
                     .OrderBy(pin => pin.OriginalOrder > 0 ? pin.OriginalOrder : int.MaxValue)
                     .ThenBy(pin => pin.IoNumber))
        {
            string connector = (pin.Connector ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(connector) || !seen.Add(connector))
                continue;

            connectors.Add(connector);
        }

        return connectors.ToArray();
    }

    static int FirstOriginalOrder(WireNet net) =>
        net.Pins
            .Select(pin => pin.OriginalOrder > 0 ? pin.OriginalOrder : int.MaxValue)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

    static string NaturalSortKey(string value)
    {
        string normalized = (value ?? string.Empty).Trim();
        Match match = Regex.Match(
            normalized,
            @"^(?<prefix>\D*?)(?<number>\d+)(?<suffix>.*)$",
            RegexOptions.CultureInvariant);

        if (!match.Success)
            return normalized;

        return match.Groups["prefix"].Value +
               int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture)
                   .ToString("D10", CultureInfo.InvariantCulture) +
               match.Groups["suffix"].Value;
    }

    /// <summary>
    /// PinProbe chỉ cần raw ScanFrame để nhận IO que dò; tắt engine production
    /// trong thời gian đó để không tốn CPU cho pass/fault/UI và không tạo trạng
    /// thái continuity giả. Khi quay lại production phải bật lại.
    /// </summary>
    public void SetFrameProcessingEnabled(bool enabled)
    {
        _frameProcessingEnabled = enabled;
        if (!enabled)
            Reset();
    }

    public void Reset()
    {
        lock (_gate)
            ResetUnsafe();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ResetUnsafe()
    {
        _passedNets.Clear();
        _stableCounters.Clear();
        _currentActive.Clear();
        _currentConnections.Clear();
        _continuityPreviewConnections.Clear();
        _continuityPreviewWiringBySource.Clear();
        _continuityPreviewSequence = 0;
        _actualComponentByIo.Clear();
        _unexpectedIo.Clear();
        _wiringFaults.Clear();
        _candidateWiringFaults.Clear();
        _probeEvidenceExcludedIo.Clear();
        _confirmedOpenKeys.Clear();
        _faultConfirmation.Reset();
        _contactUnstable = false;
        _contactLossTimedOut = false;
        _productStable = false;
        _readyToEvaluateProductFaults = false;
        _hasExpectedSourceCoverage = false;
        _lastFrameValid = false;
        _lastFrameSequence = 0;
        _lastFrameScanGeneration = 0;
        _lastFrameUnknownBytes = 0;
        _lastEngineComputeMilliseconds = 0;
        _forceNextFrameChanged = true;
    }

    public void ResetProductCycle()
    {
        lock (_gate)
        {
            _latchedClipKeys.Clear();
            ClearProductRemovalBaselineUnsafe();
            ResetUnsafe();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Captures the exact electrical edges that belong to the product/result that
    /// is about to enter THÁO SẢN PHẨM. This must be called before Reset() clears
    /// the authoritative result snapshot. The captured set survives ordinary
    /// Reset() calls and is cleared only when a new product cycle/model starts.
    /// </summary>
    public int ArmProductRemovalBaseline()
    {
        lock (_gate)
        {
            _productRemovalBaselineEdges.Clear();
            if (_model is not null)
            {
                foreach ((int source, HashSet<int> targets) in _currentConnections)
                {
                    foreach (int target in targets)
                    {
                        if (!IsProductConnectivityEdgeUnsafe(_model, source, target))
                            continue;

                        _productRemovalBaselineEdges.Add(CanonicalEdge(source, target));
                    }
                }
            }

            _productRemovalBaselineArmed = true;
            return _productRemovalBaselineEdges.Count;
        }
    }

    public int ProductRemovalBaselineEdgeCount
    {
        get
        {
            lock (_gate)
                return _productRemovalBaselineEdges.Count;
        }
    }

    public int ProductRemovalBaselineRemainingEdgeCount
    {
        get
        {
            lock (_gate)
            {
                if (!_productRemovalBaselineArmed || _productRemovalBaselineEdges.Count == 0)
                    return HasProductActivityUnsafeFallback() ? 1 : 0;

                return _productRemovalBaselineEdges.Count(edge =>
                    HasElectricalEdge(_currentConnections, edge.LowIo, edge.HighIo));
            }
        }
    }

    public bool IsProductRemovalBaselineReleased
    {
        get
        {
            lock (_gate)
            {
                // Compatibility/safety fallback for diagnostic callers that arm
                // removal before a complete product snapshot exists.
                if (!_productRemovalBaselineArmed || _productRemovalBaselineEdges.Count == 0)
                    return !HasProductActivityUnsafeFallback();

                return !_productRemovalBaselineEdges.Any(edge =>
                    HasElectricalEdge(_currentConnections, edge.LowIo, edge.HighIo));
            }
        }
    }

    public void ClearProductRemovalBaseline()
    {
        lock (_gate)
            ClearProductRemovalBaselineUnsafe();
    }

    private bool HasProductActivityUnsafeFallback() =>
        _model is not null && HasProductActivityUnsafe(_model);

    private void ClearProductRemovalBaselineUnsafe()
    {
        _productRemovalBaselineEdges.Clear();
        _productRemovalBaselineArmed = false;
    }

    private static (int LowIo, int HighIo) CanonicalEdge(int a, int b) =>
        (Math.Min(a, b), Math.Max(a, b));

    /// <summary>
    /// Applies one finalized SOURCE relation from the in-progress Production scan.
    /// The overlay is presentation-only: expected topology edges are filtered here
    /// and the authoritative continuity state remains untouched until a complete C0 frame.
    /// </summary>
    public bool ApplyContinuityPreviewSource(
        int sourceIo,
        IReadOnlyCollection<int> targets,
        long sequence,
        IReadOnlyDictionary<(int SourceIo, int TargetIo), ProductFaultType>? explicitFaults = null)
    {
        if (_disposed || sourceIo <= 0)
            return false;

        lock (_gate)
        {
            if (!_frameProcessingEnabled || _model is null)
                return false;

            bool sourceComponentKnown = _componentByIo.TryGetValue(
                sourceIo,
                out int expectedComponent);

            if (_continuityPreviewSequence != 0 &&
                sequence > 0 &&
                sequence != _continuityPreviewSequence)
            {
                _continuityPreviewConnections.Clear();
                _continuityPreviewWiringBySource.Clear();
            }
            if (sequence > 0)
                _continuityPreviewSequence = sequence;

            var filteredTargets = new HashSet<int>();
            var previewFaults = new HashSet<WiringFaultPair>();
            foreach (int target in targets)
            {
                if (target <= 0 || target == sourceIo)
                    continue;

                if (sourceComponentKnown &&
                    _componentByIo.TryGetValue(target, out int targetComponent) &&
                    targetComponent == expectedComponent)
                {
                    filteredTargets.Add(target);
                    continue;
                }

                // OTHER/SHORT from the JBZ firmware is already an electrical
                // relation. Mirror Rev 1.42 viewOther() immediately on screen,
                // but keep it strictly outside the authoritative fault state.
                WiringFaultPair inferred = ClassifyUnexpectedPair(_model, sourceIo, target);
                previewFaults.Add(ApplyExplicitFaultType(
                    inferred,
                    sourceIo,
                    target,
                    explicitFaults));
            }

            bool changed = false;
            if (previewFaults.Count == 0)
                changed |= _continuityPreviewWiringBySource.Remove(sourceIo);
            else if (!_continuityPreviewWiringBySource.TryGetValue(sourceIo, out HashSet<WiringFaultPair>? oldPreviewFaults) ||
                     !oldPreviewFaults.SetEquals(previewFaults))
            {
                _continuityPreviewWiringBySource[sourceIo] = previewFaults;
                changed = true;
            }

            // If the expected-edge portion of this preview matches the last
            // authoritative C0 relation, remove only that expected-edge override.
            // A simultaneous presentation-only OTHER/SHORT fault still remains.
            bool matchesAuthoritative = !sourceComponentKnown ||
                (_currentConnections.TryGetValue(
                    sourceIo,
                    out HashSet<int>? authoritativeTargets)
                    ? FilterExpectedComponentTargetsUnsafe(
                        authoritativeTargets,
                        expectedComponent).SetEquals(filteredTargets)
                    : filteredTargets.Count == 0);

            if (matchesAuthoritative)
            {
                changed |= _continuityPreviewConnections.Remove(sourceIo);
                return changed;
            }

            if (_continuityPreviewConnections.TryGetValue(
                    sourceIo,
                    out HashSet<int>? existing) &&
                existing.SetEquals(filteredTargets))
            {
                return changed;
            }

            _continuityPreviewConnections[sourceIo] = filteredTargets;
            return true;
        }
    }

    /// <summary>
    /// During THÁO SẢN PHẨM the firmware still reports one finalized SOURCE
    /// relation at a time before the next C0. Unlike the normal continuity preview,
    /// removal must update the authoritative electrical graph immediately; otherwise
    /// IsProductReleased can remain stuck on the last failed C0 frame forever.
    ///
    /// This method is removal-only: it never creates/confirm new wiring faults and
    /// never participates in PASS. It only replaces the physical relation for the
    /// supplied source, rebuilds the current graph and raises Changed so the existing
    /// ProductRemoved state machine can reset the cycle as soon as all product edges
    /// have actually disappeared.
    /// </summary>
    public bool ApplyRemovalPreviewSource(
        int sourceIo,
        IReadOnlyCollection<int> targets,
        long sequence,
        long scanGeneration)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (_disposed || sourceIo <= 0)
            return false;

        bool changed;
        bool released;
        int[] normalizedTargets;
        lock (_gate)
        {
            ProductModel? model = _model;
            if (!_frameProcessingEnabled || model is null)
                return false;

            normalizedTargets = targets
                .Where(io => io > 0 &&
                             io != sourceIo &&
                             !model.IgnoredIo.Contains(io))
                .Distinct()
                .OrderBy(io => io)
                .ToArray();

            var nextTargets = normalizedTargets.ToHashSet();
            if (nextTargets.Count == 0)
            {
                changed = _currentConnections.Remove(sourceIo);
            }
            else if (_currentConnections.TryGetValue(sourceIo, out HashSet<int>? existing) &&
                     existing.SetEquals(nextTargets))
            {
                changed = false;
            }
            else
            {
                _currentConnections[sourceIo] = nextTargets;
                changed = true;
            }

            // Removal frames are source-by-source. A no-op source still must not
            // create a fake engine event; the next source/C0 will continue the scan.
            if (!changed)
                return false;

            _currentActive.Clear();
            foreach ((int source, HashSet<int> currentTargets) in _currentConnections)
            {
                if (currentTargets.Count == 0)
                    continue;

                _currentActive.Add(source);
                foreach (int target in currentTargets)
                    _currentActive.Add(target);
            }

            _actualComponentByIo = BuildActualComponents(_currentConnections);
            _lastFrameValid = true;
            if (sequence > 0)
                _lastFrameSequence = Math.Max(_lastFrameSequence, sequence);
            if (scanGeneration > 0)
                _lastFrameScanGeneration = scanGeneration;
            _lastFrameUnknownBytes = 0;

            // Do not let post-result removal activity become PASS/NG evidence.
            _productStable = false;
            _readyToEvaluateProductFaults = false;
            released = !HasProductActivityUnsafe(model);
        }

        AsyncFileLogService.Current.Performance(
            $"REMOVAL_SOURCE_APPLIED source=IO{sourceIo} seq={sequence} " +
            $"targets=[{string.Join(",", normalizedTargets.Select(io => $"IO{io}"))}] released={released}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Promote firmware-authoritative :OTHER/:SHORT relations observed before C0
    /// into the real production fault state. The partial frame is still NOT used
    /// for PASS/open evaluation; only an explicit firmware fault hint is trusted.
    /// This closes the gap where TryHandleContinuityPreviewFrame consumed the UART
    /// event for presentation and the fault never reached ProcessFrame().
    /// </summary>
    public bool PromoteAuthoritativeContinuityPreviewFaults(
        int sourceIo,
        long sequence,
        long scanGeneration,
        IReadOnlyDictionary<(int SourceIo, int TargetIo), ProductFaultType>? explicitFaults)
    {
        if (_disposed || sourceIo <= 0 || explicitFaults is null || explicitFaults.Count == 0)
            return false;

        bool changed = false;
        WiringFaultPair[] promoted = [];
        lock (_gate)
        {
            if (!_frameProcessingEnabled || _model is null ||
                !_continuityPreviewWiringBySource.TryGetValue(sourceIo, out HashSet<WiringFaultPair>? previewFaults))
            {
                return false;
            }

            promoted = previewFaults
                .Where(fault => IsFirmwareAuthoritativeFault(fault, explicitFaults))
                .ToArray();
            if (promoted.Length == 0)
                return false;

            foreach (WiringFaultPair fault in promoted)
            {
                changed |= _candidateWiringFaults.Add(fault);
                changed |= _wiringFaults.Add(fault);
                _unexpectedIo.Add(fault.SourceIo);
                _unexpectedIo.Add(fault.TargetIo);
            }

            if (changed)
            {
                // Valid for FAIL lifecycle only. PASS still requires the complete
                // continuity snapshot and ReadyToEvaluateProductFaults.
                _lastFrameValid = true;
                if (sequence > 0)
                    _lastFrameSequence = Math.Max(_lastFrameSequence, sequence);
                if (scanGeneration > 0)
                    _lastFrameScanGeneration = scanGeneration;
                _lastFrameUnknownBytes = 0;
            }
        }

        if (changed)
        {
            AsyncFileLogService.Current.Performance(
                $"REALTIME_FAULT_PROMOTED source=IO{sourceIo} seq={sequence} " +
                $"faults={string.Join('|', promoted.Select(f => $"{f.FaultType}:IO{f.SourceIo}-IO{f.TargetIo}"))}");
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>
    /// Universal Tester New can report a physical bridge as two simultaneous
    /// TESTPIN contacts instead of :SHORT. One probe touching a normal network may
    /// expose one or more I/O from the SAME expected component; that remains probe
    /// presentation only. Two active TESTPIN I/O from DIFFERENT expected THT
    /// components are an electrical bridge and are promoted to a latched SHORT.
    /// </summary>
    public bool PromoteCrossNetworkTestPinShort(
        IReadOnlyCollection<int> probeIos,
        long sequence,
        long scanGeneration)
    {
        ArgumentNullException.ThrowIfNull(probeIos);
        if (_disposed || probeIos.Count < 2)
            return false;

        bool changed = false;
        WiringFaultPair[] promoted = [];
        lock (_gate)
        {
            ProductModel? model = _model;
            if (!_frameProcessingEnabled || model is null)
                return false;

            int[] ios = probeIos
                .Where(io => io > 0 && !model.IgnoredIo.Contains(io) && _componentByIo.ContainsKey(io))
                .Distinct()
                .OrderBy(io => io)
                .ToArray();
            if (ios.Length < 2)
                return false;

            var faults = new List<WiringFaultPair>();
            for (int i = 0; i < ios.Length - 1; i++)
            {
                int source = ios[i];
                int sourceComponent = _componentByIo[source];
                for (int j = i + 1; j < ios.Length; j++)
                {
                    int target = ios[j];
                    if (_componentByIo[target] == sourceComponent)
                        continue;

                    faults.Add(new WiringFaultPair(
                        source,
                        target,
                        $"Phát hiện chập IO{source} <-> IO{target}: TESTPIN đồng thời thuộc hai network khác nhau",
                        ProductFaultType.ShortCircuit,
                        null,
                        null));
                }
            }

            if (faults.Count == 0)
                return false;

            promoted = faults.Distinct().ToArray();
            foreach (WiringFaultPair fault in promoted)
            {
                changed |= _candidateWiringFaults.Add(fault);
                changed |= _wiringFaults.Add(fault);
                _unexpectedIo.Add(fault.SourceIo);
                _unexpectedIo.Add(fault.TargetIo);
            }

            if (changed)
            {
                _lastFrameValid = true;
                if (sequence > 0)
                    _lastFrameSequence = Math.Max(_lastFrameSequence, sequence);
                if (scanGeneration > 0)
                    _lastFrameScanGeneration = scanGeneration;
                _lastFrameUnknownBytes = 0;
            }
        }

        if (changed)
        {
            AsyncFileLogService.Current.Performance(
                $"REALTIME_SHORT_FROM_TESTPIN seq={sequence} ios={string.Join(",", probeIos.OrderBy(io => io))} " +
                $"faults={string.Join('|', promoted.Select(f => $"IO{f.SourceIo}-IO{f.TargetIo}"))}");
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>
    /// Presentation-only evidence that the in-progress scan already contains an
    /// expected product edge. It never participates in PASS/FAIL or removal.
    /// </summary>
    public bool HasContinuityPreviewProductActivity
    {
        get
        {
            lock (_gate)
            {
                if (_model is null)
                    return false;

                return _continuityPreviewWiringBySource.Count > 0 ||
                       _continuityPreviewConnections.Any(pair =>
                           pair.Value.Any(target =>
                               IsProductConnectivityEdgeUnsafe(_model, pair.Key, target)));
            }
        }
    }

    public long ContinuityPreviewSequence
    {
        get
        {
            lock (_gate)
                return _continuityPreviewSequence;
        }
    }

    /// <summary>
    /// Clears the presentation overlay when an authoritative complete frame is about
    /// to be processed. Returns true only when the visible preview could have changed.
    /// </summary>
    public bool ClearContinuityPreview()
    {
        lock (_gate)
        {
            if (_continuityPreviewConnections.Count == 0 &&
                _continuityPreviewWiringBySource.Count == 0)
            {
                _continuityPreviewSequence = 0;
                return false;
            }

            _continuityPreviewConnections.Clear();
            _continuityPreviewWiringBySource.Clear();
            _continuityPreviewSequence = 0;
            return true;
        }
    }

    private HashSet<int> FilterExpectedComponentTargetsUnsafe(
        IEnumerable<int> targets,
        int expectedComponent)
    {
        var filtered = new HashSet<int>();
        foreach (int target in targets)
        {
            if (_componentByIo.TryGetValue(target, out int component) &&
                component == expectedComponent)
            {
                filtered.Add(target);
            }
        }
        return filtered;
    }

    public bool ProcessFrame(ScanFrame frame, bool preserveConfirmedWiringFaults = false)
    {
        if (_disposed)
            return false;

        // Tuyệt đối không cho snapshot TestPin đi vào logic production.
        // Điều này ngăn que GND tạo cảnh báo đấu sai/chập giả.
        if (!_frameProcessingEnabled ||
            frame.Mode != BoardScanMode.Production ||
            !frame.Complete ||
            frame.UnknownBytes > 0)
            return false;

        long computeStarted = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _framesProcessed);
        bool changed;

        lock (_gate)
        {
            // Có thể SetFrameProcessingEnabled(false) xảy ra sau check phía
            // ngoài nhưng trước khi callback lấy được lock. Kiểm tra lại để
            // frame production cũ không tạo lỗi đúng lúc chuyển sang TestPin.
            ProductModel? model = _model;
            if (!_frameProcessingEnabled || frame.Mode != BoardScanMode.Production || model is null)
                return false;

            bool sameActive = _currentActive.SetEquals(frame.ActiveIo);
            bool sameConnections = ConnectionsEqual(_currentConnections, frame.Connections);
            bool passedChanged = false;
            bool previousContactUnstable = _contactUnstable;
            bool previousContactLossTimedOut = _contactLossTimedOut;
            bool previousProductStable = _productStable;
            bool previousReadyToEvaluate = _readyToEvaluateProductFaults;
            WiringFaultPair[] previousConfirmedWiringFaults = preserveConfirmedWiringFaults
                ? _wiringFaults.ToArray()
                : [];

            if (!sameActive)
            {
                _currentActive.Clear();
                foreach (int io in frame.ActiveIo)
                    _currentActive.Add(io);
            }

            if (!sameConnections)
            {
                _currentConnections.Clear();
                foreach (var pair in frame.Connections)
                    _currentConnections[pair.Key] = pair.Value.ToHashSet();

                // Một điểm dập chung có thể được firmware phát dưới dạng một
                // source với nhiều target. Các target không nhất thiết có cạnh
                // trực tiếp với nhau dù chúng thuộc cùng một cụm điện thật.
                // Dựng component thực tế một lần khi frame đổi để mọi WireNet
                // dùng chung I/O (ví dụ nhiều dây về IO518) được đánh giá đúng.
                _actualComponentByIo = BuildActualComponents(_currentConnections);
            }

            // Frame của bo thường lặp lại nguyên trạng nhiều lần. Chỉ dựng lại
            // topology continuity khi các cạnh thực sự đổi; fault debounce theo
            // thời gian bên dưới vẫn được Observe trên mọi complete frame.
            bool evaluateConnections =
                _forceNextFrameChanged ||
                !sameConnections ||
                _expectedConnectionScratch.Count == 0;
            if (evaluateConnections)
            {
                // Continuity vật lý là cạnh điện hai chiều. Htdrv có thể thấy
                // IO1->IO2 hoặc IO2->IO1 cho cùng một dây hai chân; cả hai đều
                // phải được tính là đúng network, không phải OPEN/missing.
                _expectedConnectionScratch.Clear();
                foreach (WireNet net in model.Nets)
                {
                    if (!IsEligibleProductionNet(net))
                        continue;

                    bool connected = IsWireNetConnected(net);
                    _expectedConnectionScratch[_confirmationKeyByNet[net]] = connected;

                    if (!connected)
                    {
                        _stableCounters[net.Name] = 0;
                        passedChanged |= _passedNets.Remove(net.Name);
                        continue;
                    }

                    int stable = _stableCounters.GetValueOrDefault(net.Name) + 1;
                    _stableCounters[net.Name] = stable;

                    // Kết nối đúng được chấp nhận ngay trên một complete board snapshot.
                    // IoConfirm*/RequiredStableFrames chỉ còn thuộc đường xác nhận lỗi
                    // đấu sai/chập, không được làm chậm PASS sạch.
                    passedChanged |= _passedNets.Add(net.Name);
                }

                // CLIP không phải WireNet thường. A0/AO là common, còn aN là
                // tên nhánh; đầu còn lại phải tới đúng I/O ghi trên row aN. Kiểm
                // tra hai chiều để không phụ thuộc source/target mà firmware
                // chọn khi phát frame.
                if (model.Clip is not null)
                {
                    foreach (ClipBranch branch in model.Clip.Branches)
                    {
                        if (!IsEligibleClipBranch(model.Clip, branch))
                            continue;

                        string clipKey = ClipConfirmationKey(branch.NetName);
                        if (_latchedClipKeys.Contains(clipKey))
                        {
                            _expectedConnectionScratch[_confirmationKeyByClip[branch]] = true;
                            passedChanged |= _passedNets.Add(branch.NetName);
                            continue;
                        }

                        bool connected = IsClipBranchConnected(
                            model.Clip,
                            branch,
                            _currentConnections);
                        _expectedConnectionScratch[_confirmationKeyByClip[branch]] = connected;

                        if (!connected)
                        {
                            _stableCounters[branch.NetName] = 0;
                            passedChanged |= _passedNets.Remove(branch.NetName);
                            continue;
                        }

                        int stable = _stableCounters.GetValueOrDefault(branch.NetName) + 1;
                        _stableCounters[branch.NetName] = stable;

                        passedChanged |= _latchedClipKeys.Add(clipKey);
                        passedChanged |= _passedNets.Add(branch.NetName);
                    }
                }
            }

            bool hasProductActivity = HasProductActivityUnsafe(model);
            bool hasExpectedSourceCoverage = HasExpectedSourceCoverageUnsafe(model);
            bool allExpectedConnectionsPresent =
                _expectedConnectionScratch.Count > 0 &&
                _expectedConnectionScratch.Values.All(static connected => connected);
            _hasExpectedSourceCoverage = hasExpectedSourceCoverage;
            // Một cạnh continuity là hai chiều. Một số bo Htdrv phát đầu THT
            // canonical ở phía source, trong khi bo khác phát chính cạnh đó theo
            // chiều ngược lại. Khi toàn bộ mạng kỳ vọng đã hiện diện trong cùng
            // complete frame, chính các cạnh đó đã chứng minh đủ coverage để PASS;
            // không được khóa chu kỳ chỉ vì hướng source của firmware khác nhau.
            // Với topology chưa đủ, vẫn giữ source-coverage gate cũ để không xác
            // nhận WRONG/SHORT trong lúc người vận hành đang lắp sản phẩm.
            _readyToEvaluateProductFaults =
                hasProductActivity &&
                (hasExpectedSourceCoverage || allExpectedConnectionsPresent);
            _lastFrameValid = true;
            _lastFrameSequence = frame.Sequence;
            _lastFrameScanGeneration = frame.ScanGeneration;
            _lastFrameUnknownBytes = frame.UnknownBytes;

            // Snapshot đã được classifier xác định là đầu dò chỉ dùng để hiển
            // thị Pin đang chạm. Không cho snapshot này tạo candidate/confirmed
            // WRONG hoặc SHORT mới; các lỗi thật đã có trước đó vẫn được giữ.
            // PASS cần coverage đầy đủ của model; một cạnh SAI thật thì không.
            // Với topology suy luận (không có firmware hint), vẫn giữ debounce.
            // Nhưng Universal Tester New đã phân loại trực tiếp :OTHER/:SHORT;
            // các hint đó là authoritative và được confirm ngay ở UpdateWiringFaults.
            bool realtimeEvaluationEnabled = hasProductActivity;
            bool wiringChanged = !preserveConfirmedWiringFaults && UpdateWiringFaults(
                model,
                _expectedConnectionScratch,
                hasProductActivity,
                realtimeEvaluationEnabled,
                frame.FaultHints);

            if (preserveConfirmedWiringFaults &&
                previousConfirmedWiringFaults.Length > 0 &&
                _wiringFaults.Count == 0)
            {
                foreach (WiringFaultPair fault in previousConfirmedWiringFaults)
                    _wiringFaults.Add(fault);

                _unexpectedIo.Clear();
                foreach (WiringFaultPair fault in _wiringFaults)
                {
                    _unexpectedIo.Add(fault.SourceIo);
                    _unexpectedIo.Add(fault.TargetIo);
                }

                wiringChanged = true;
            }

            changed =
                _forceNextFrameChanged ||
                !sameActive ||
                !sameConnections ||
                passedChanged ||
                wiringChanged ||
                previousContactUnstable != _contactUnstable ||
                previousContactLossTimedOut != _contactLossTimedOut ||
                previousProductStable != _productStable ||
                previousReadyToEvaluate != _readyToEvaluateProductFaults;

            _forceNextFrameChanged = false;
            _lastEngineComputeMilliseconds =
                Stopwatch.GetElapsedTime(computeStarted).TotalMilliseconds;
        }

        // Không block worker UART. TestViewModel sẽ marshal async sang UI.
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);

        return changed;
    }

    bool UpdateWiringFaults(
        ProductModel model,
        IReadOnlyDictionary<string, bool> expectedConnections,
        bool hasProductActivity,
        bool readyToEvaluateFaults,
        IReadOnlyDictionary<(int SourceIo, int TargetIo), ProductFaultType>? explicitFaults = null)
    {
        // Đấu sai phải xét theo COMPONENT điện thật, không chỉ theo một chiều
        // source->target. Trace cho thấy một component nhiều nhánh có thể sinh
        // quan hệ ngược/transitive (ví dụ 14->26) nhưng vẫn là dây đúng.
        // Component THT được tính một lần khi SetModel; không union-find lại
        // mỗi frame. Đây là một trong các tối ưu quan trọng để scan gần tốc độ
        // Htdrv gốc khi model có hàng trăm pin.
        IReadOnlyDictionary<int, int> componentByIo = _componentByIo;
        HashSet<(int SourceIo, int TargetIo)> unexpectedNow = _unexpectedPairScratch;
        unexpectedNow.Clear();

        foreach (var pair in _currentConnections)
        {
            int source = pair.Key;

            if (model.IgnoredIo.Contains(source) ||
                _probeEvidenceExcludedIo.Contains(source))
            {
                continue;
            }

            bool sourceMapped = _modelIo.Contains(source);
            bool sourceComponentKnown = componentByIo.TryGetValue(source, out int sourceComponent);

            foreach (int target in pair.Value)
            {
                if (model.IgnoredIo.Contains(target) ||
                    _probeEvidenceExcludedIo.Contains(target))
                {
                    continue;
                }

                // Một word target thay source có thể được decoder biểu diễn
                // tạm thời là IOx -> IOx. Đây là một đầu đang chạm, không hề
                // chứng minh có dây nối sai; chỉ cạnh giữa HAI IO khác nhau mới
                // được phép tạo WRONG/SHORT candidate.
                if (source == target)
                    continue;

                bool targetMapped = _modelIo.Contains(target);
                bool targetComponentKnown = componentByIo.TryGetValue(target, out int targetComponent);

                // Every real non-self electrical edge in the active board range must be
                // evaluated. A harness can be miswired onto I/O that is not present in the
                // loaded THT; treating that edge as RAW_ACTIVITY_ONLY hides exactly the
                // wrong-wire/short conditions production must catch. Probe frames are
                // quarantined before ProcessFrame and confirmed probe I/O is excluded
                // separately, so model membership is not a safe noise filter here.
                if (!sourceMapped || !targetMapped ||
                    !sourceComponentKnown || !targetComponentKnown ||
                    sourceComponent != targetComponent)
                    unexpectedNow.Add((source, target));
            }
        }

        WiringFaultPair[] classified = unexpectedNow.Count == 0
            ? Array.Empty<WiringFaultPair>()
            : unexpectedNow
                .Select(pair =>
                {
                    WiringFaultPair inferred = ClassifyUnexpectedPair(
                        model,
                        pair.SourceIo,
                        pair.TargetIo);
                    return ApplyExplicitFaultType(
                        inferred,
                        pair.SourceIo,
                        pair.TargetIo,
                        explicitFaults);
                })
                .ToArray();

        bool changed = false;
        if (!_candidateWiringFaults.SetEquals(classified))
        {
            changed = true;
            _candidateWiringFaults.Clear();
            foreach (WiringFaultPair fault in classified)
                _candidateWiringFaults.Add(fault);
        }

        ProductionFaultConfirmationSnapshot snapshot = _faultConfirmation.Observe(
            expectedConnections,
            classified.Length == 0
                ? Array.Empty<UnexpectedFaultObservation>()
                : classified.Select(fault => new UnexpectedFaultObservation(
                    fault.SourceIo,
                    fault.TargetIo,
                    fault.FaultType))
                .ToArray(),
            hasProductActivity,
            readyToEvaluateFaults);

        if (!_confirmedOpenKeys.SetEquals(snapshot.ConfirmedOpenKeys))
        {
            changed = true;
            _confirmedOpenKeys.Clear();
            foreach (string key in snapshot.ConfirmedOpenKeys)
                _confirmedOpenKeys.Add(key);
        }

        _contactUnstable = snapshot.ContactUnstable;
        _contactLossTimedOut = snapshot.ContactLossTimedOut;
        _productStable = snapshot.ProductStable;

        if (classified.Length == 0)
        {
            if (_wiringFaults.Count > 0 || _unexpectedIo.Count > 0)
            {
                changed = true;
                _wiringFaults.Clear();
                _unexpectedIo.Clear();
            }

            return changed;
        }

        WiringFaultPair[] confirmedFaults = classified
            .Where(fault =>
                IsFirmwareAuthoritativeFault(fault, explicitFaults) ||
                snapshot.ConfirmedUnexpectedPairs.Contains((fault.SourceIo, fault.TargetIo)))
            .ToArray();

        if (!_wiringFaults.SetEquals(confirmedFaults))
        {
            changed = true;
            _wiringFaults.Clear();
            foreach (WiringFaultPair fault in confirmedFaults)
                _wiringFaults.Add(fault);

            _unexpectedIo.Clear();
            foreach (WiringFaultPair fault in _wiringFaults)
            {
                _unexpectedIo.Add(fault.SourceIo);
                _unexpectedIo.Add(fault.TargetIo);
            }
        }

        return changed;
    }

    private static bool IsFirmwareAuthoritativeFault(
        WiringFaultPair fault,
        IReadOnlyDictionary<(int SourceIo, int TargetIo), ProductFaultType>? explicitFaults)
    {
        if (explicitFaults is null || explicitFaults.Count == 0)
            return false;

        var key = (Math.Min(fault.SourceIo, fault.TargetIo), Math.Max(fault.SourceIo, fault.TargetIo));
        return explicitFaults.TryGetValue(key, out ProductFaultType type) &&
               type is ProductFaultType.WrongWiring or ProductFaultType.ShortCircuit;
    }

    private static WiringFaultPair ApplyExplicitFaultType(
        WiringFaultPair inferred,
        int sourceIo,
        int targetIo,
        IReadOnlyDictionary<(int SourceIo, int TargetIo), ProductFaultType>? explicitFaults)
    {
        if (explicitFaults is null || explicitFaults.Count == 0)
            return inferred;

        var key = (Math.Min(sourceIo, targetIo), Math.Max(sourceIo, targetIo));
        if (!explicitFaults.TryGetValue(key, out ProductFaultType explicitType) ||
            explicitType is not (ProductFaultType.WrongWiring or ProductFaultType.ShortCircuit))
        {
            return inferred;
        }

        string reason = explicitType == ProductFaultType.ShortCircuit
            ? $"Firmware SHORT: IO{sourceIo} <-> IO{targetIo}"
            : $"Firmware OTHER: IO{sourceIo} đang nối nhầm IO{targetIo}";

        return inferred with
        {
            FaultType = explicitType,
            Reason = reason
        };
    }

    private static WiringFaultPair ClassifyUnexpectedPair(
        ProductModel model,
        int actualSource,
        int actualTarget)
    {
        // Nếu SOURCE chính là source được khai báo của một network nhưng trả về
        // một target ngoài network đó, đây là lỗi ĐẤU SAI: ta biết chính xác
        // "đáng lẽ source này phải đi tới đâu" và "thực tế đang đi tới đâu".
        WireNet? sourceNet = model.Nets.FirstOrDefault(net =>
            net.SourceIo == actualSource && net.ExpectedActiveIo.Count > 0);

        if (sourceNet is not null)
        {
            int expectedTarget = sourceNet.ExpectedActiveIo
                .FirstOrDefault(io => io != actualTarget);
            if (expectedTarget <= 0)
                expectedTarget = sourceNet.ExpectedActiveIo[0];

            return new WiringFaultPair(
                actualSource,
                actualTarget,
                // ExpectedSourceIo/ExpectedTargetIo vẫn giữ riêng bên dưới.
                // Reason chỉ mô tả kết nối sai THỰC TẾ để UI/popup/history gọn.
                $"IO{actualSource} đang nối nhầm IO{actualTarget}",
                ProductFaultType.WrongWiring,
                actualSource,
                expectedTarget);
        }

        // Một cạnh điện nối hai component THT khác nhau nhưng SOURCE không phải
        // source định nghĩa của network nào thường biểu hiện một cầu nối/chập
        // giữa hai network. Tách riêng để UI/History không còn ghi chung chung.
        bool sourceMapped = model.Nets.Any(net => net.IoNumbers.Contains(actualSource)) ||
                            (model.Clip?.CommonIo == actualSource) ||
                            (model.Clip?.Branches.Any(branch => branch.TargetIo == actualSource) == true);
        bool targetMapped = model.Nets.Any(net => net.IoNumbers.Contains(actualTarget)) ||
                            (model.Clip?.CommonIo == actualTarget) ||
                            (model.Clip?.Branches.Any(branch => branch.TargetIo == actualTarget) == true);

        ProductFaultType type = sourceMapped && targetMapped
            ? ProductFaultType.ShortCircuit
            : ProductFaultType.WrongWiring;

        string reason = type == ProductFaultType.ShortCircuit
            ? $"Phát hiện chập IO{actualSource} <-> IO{actualTarget} giữa hai network khác nhau"
            : $"IO{actualSource} đang thông nhầm IO{actualTarget}";

        return new WiringFaultPair(
            actualSource,
            actualTarget,
            reason,
            type,
            null,
            null);
    }

    /// <summary>
    /// Xóa riêng lỗi đấu sai/chập đang treo mà không reset trạng thái PASS/open.
    /// Dùng khi phát hiện chữ ký đầu dò GND trong luồng scan production để
    /// một lần chạm que không thể tiếp tục kích popup từ frame trước.
    /// </summary>
    public void ClearTransientWiringFaults()
    {
        bool changed = false;
        lock (_gate)
        {
            changed = _wiringFaults.Count > 0 || _unexpectedIo.Count > 0 || _candidateWiringFaults.Count > 0;
            _wiringFaults.Clear();
            _candidateWiringFaults.Clear();
            _unexpectedIo.Clear();
            _faultConfirmation.ClearUnexpected();
            _productStable = false;
        }

        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<FaultDetail> BuildConfirmedOpenFaults()
    {
        // OPEN/missing expected connections are legacy/history data only.
        // Current Production flow must not generate product OPEN faults.
        return [];
    }

    private bool HasProductActivityUnsafe(ProductModel model)
    {
        foreach (KeyValuePair<int, HashSet<int>> pair in _currentConnections)
        {
            foreach (int target in pair.Value)
            {
                if (IsProductConnectivityEdgeUnsafe(model, pair.Key, target))
                    return true;
            }
        }

        return false;
    }

    private bool HasExpectedSourceCoverageUnsafe(ProductModel model)
    {
        int expectedSources = 0;

        foreach (WireNet net in model.Nets)
        {
            if (net.ExpectedActiveIo.Count == 0)
                continue;

            expectedSources++;
            if (!_currentConnections.ContainsKey(net.SourceIo))
                return false;
        }

        if (model.Clip is not null && model.Clip.Branches.Count > 0)
        {
            expectedSources++;
            if (!_currentConnections.ContainsKey(model.Clip.CommonIo))
                return false;
        }

        return expectedSources > 0;
    }

    private static string NetConfirmationKey(string name) => $"NET:{name}";
    private static string ClipConfirmationKey(string name) => $"CLIP:{name}";

    private static int ProductionExpectedNetCount(ProductModel model) =>
        model.Nets.Count(IsEligibleProductionNet) +
        (model.Clip?.Branches.Count(branch => IsEligibleClipBranch(model.Clip, branch)) ?? 0);

    private static bool IsEligibleProductionNet(WireNet net) =>
        net.SourceIo > 0 && net.ExpectedActiveIo.Count > 0;

    private static bool IsEligibleClipBranch(ClipTopology clip, ClipBranch branch) =>
        clip.CommonIo > 0 && branch.TargetIo > 0;

    private ExpectedNetworkDiagnostic[] BuildExpectedNetworkDiagnosticsUnsafe(ProductModel model)
    {
        var result = new List<ExpectedNetworkDiagnostic>();

        foreach (WireNet net in model.Nets)
        {
            if (!IsEligibleProductionNet(net))
                continue;

            result.Add(new ExpectedNetworkDiagnostic(
                NetConfirmationKey(net.Name),
                net.Name,
                net.IsSplice ? "normal-splice" : "normal",
                net.SourceIo,
                net.ExpectedActiveIo.ToArray(),
                _passedNets.Contains(net.Name)));
        }

        if (model.Clip is not null)
        {
            foreach (ClipBranch branch in model.Clip.Branches)
            {
                if (!IsEligibleClipBranch(model.Clip, branch))
                    continue;

                result.Add(new ExpectedNetworkDiagnostic(
                    ClipConfirmationKey(branch.NetName),
                    branch.NetName,
                    "CLIP",
                    model.Clip.CommonIo,
                    [branch.TargetIo],
                    _passedNets.Contains(branch.NetName) ||
                    _latchedClipKeys.Contains(ClipConfirmationKey(branch.NetName))));
            }
        }

        return result.ToArray();
    }

    private int CountPassedExpectedUnsafe(ProductModel model)
    {
        int count = model.Nets.Count(net =>
            IsEligibleProductionNet(net) &&
            _passedNets.Contains(net.Name));

        if (model.Clip is not null)
        {
            count += model.Clip.Branches.Count(branch =>
                IsEligibleClipBranch(model.Clip, branch) &&
                (_passedNets.Contains(branch.NetName) ||
                 _latchedClipKeys.Contains(ClipConfirmationKey(branch.NetName))));
        }

        return count;
    }


    private bool IsProductConnectivityEdgeUnsafe(
        ProductModel model,
        int source,
        int target)
    {
        if (source <= 0 || target <= 0 || source == target ||
            _probeEvidenceExcludedIo.Contains(source) ||
            _probeEvidenceExcludedIo.Contains(target) ||
            model.IgnoredIo.Contains(source) || model.IgnoredIo.Contains(target))
        {
            return false;
        }

        // Do not require either endpoint to belong to the THT. A wrong harness
        // can physically connect two otherwise-unused fixture I/O; that edge is
        // still authoritative product evidence and must reach the wiring evaluator.
        // Probe is filtered by the router/classifier and by _probeEvidenceExcludedIo.
        return true;
    }

    private static bool IsClipBranchConnected(
        ClipTopology clip,
        ClipBranch branch,
        IReadOnlyDictionary<int, HashSet<int>> connections)
    {
        // aN chỉ là tên/thứ tự nhánh. I/O đích là giá trị cột I/O của row aN.
        // Chấp nhận hai chiều vì firmware có thể đảo SOURCE/TARGET.
        return HasElectricalEdge(connections, clip.CommonIo, branch.TargetIo);
    }

    private static bool HasElectricalEdge(
        IReadOnlyDictionary<int, HashSet<int>> connections,
        int a,
        int b)
    {
        return (connections.TryGetValue(a, out HashSet<int>? fromA) && fromA.Contains(b)) ||
               (connections.TryGetValue(b, out HashSet<int>? fromB) && fromB.Contains(a));
    }

    private bool IsWireNetConnected(WireNet net) =>
        IsWireNetConnected(net, _actualComponentByIo);

    private static bool IsWireNetConnected(
        WireNet net,
        IReadOnlyDictionary<int, int> actualComponents)
    {
        if (!IsEligibleProductionNet(net))
            return false;

        if (!actualComponents.TryGetValue(net.SourceIo, out int sourceComponent))
            return false;

        foreach (int endpoint in net.IoNumbers)
        {
            // Preserve the parser's existing compatibility behavior: blank or
            // unmapped IO values are ignored for the all-connected decision.
            if (endpoint <= 0)
                continue;

            if (!actualComponents.TryGetValue(endpoint, out int component) ||
                component != sourceComponent)
            {
                return false;
            }
        }

        return true;
    }

    private int CountDisconnectedEndpoints(WireNet net)
    {
        if (!IsEligibleProductionNet(net))
            return 0;

        if (!_actualComponentByIo.TryGetValue(net.SourceIo, out int sourceComponent))
            return net.ExpectedActiveIo.Count;

        int disconnected = 0;
        foreach (int endpoint in net.ExpectedActiveIo)
        {
            if (!_actualComponentByIo.TryGetValue(endpoint, out int component) ||
                component != sourceComponent)
            {
                disconnected++;
            }
        }

        return disconnected;
    }

    private HashSet<int> BuildReachableNetEndpoints(WireNet net) =>
        BuildReachableNetEndpoints(net, _actualComponentByIo);

    private static HashSet<int> BuildReachableNetEndpoints(
        WireNet net,
        IReadOnlyDictionary<int, int> actualComponents)
    {
        var reachable = new HashSet<int>();
        if (net.SourceIo <= 0)
            return reachable;

        if (!actualComponents.TryGetValue(net.SourceIo, out int sourceComponent))
            return reachable;

        foreach (int endpoint in net.IoNumbers)
        {
            if (endpoint > 0 &&
                actualComponents.TryGetValue(endpoint, out int component) &&
                component == sourceComponent)
                reachable.Add(endpoint);
        }

        return reachable;
    }

    private static Dictionary<int, int> BuildActualComponents(
        IReadOnlyDictionary<int, HashSet<int>> connections)
    {
        var parent = new Dictionary<int, int>();

        int Find(int value)
        {
            if (!parent.TryGetValue(value, out int current))
            {
                parent[value] = value;
                return value;
            }

            if (current == value)
                return value;

            int root = Find(current);
            parent[value] = root;
            return root;
        }

        void Union(int a, int b)
        {
            int rootA = Find(a);
            int rootB = Find(b);
            if (rootA != rootB)
                parent[rootB] = rootA;
        }

        foreach ((int source, HashSet<int> targets) in connections)
        {
            Find(source);
            foreach (int target in targets)
                Union(source, target);
        }

        return parent.Keys.ToDictionary(io => io, Find);
    }

    static Dictionary<int, int> BuildExpectedComponents(ProductModel model)
    {
        var parent = new Dictionary<int, int>();

        int Find(int value)
        {
            if (!parent.TryGetValue(value, out int p))
            {
                parent[value] = value;
                return value;
            }

            if (p == value)
                return value;

            int root = Find(p);
            parent[value] = root;
            return root;
        }

        void Union(int a, int b)
        {
            int rootA = Find(a);
            int rootB = Find(b);
            if (rootA != rootB)
                parent[rootB] = rootA;
        }

        foreach (WireNet net in model.Nets)
        {
            if (net.IoNumbers.Count == 0)
                continue;

            int first = net.IoNumbers[0];
            Find(first);

            foreach (int io in net.IoNumbers.Skip(1))
                Union(first, io);
        }

        // Toàn bộ CLIP dùng chung A0. Vì vậy A0 và các I/O được cấu hình
        // trên row aN đều thuộc cùng component điện hợp lệ. Điều này ngăn
        // engine báo short giả giữa các nhánh CLIP vốn được thiết kế chung A0.
        if (model.Clip is not null)
        {
            int common = model.Clip.CommonIo;
            Find(common);

            foreach (ClipBranch branch in model.Clip.Branches)
                Union(common, branch.TargetIo);
        }

        return parent.Keys.ToDictionary(io => io, Find);
    }

    static bool ConnectionsEqual(
        IReadOnlyDictionary<int, HashSet<int>> current,
        IReadOnlyDictionary<int, IReadOnlySet<int>> next)
    {
        if (current.Count != next.Count)
            return false;

        foreach (KeyValuePair<int, HashSet<int>> pair in current)
        {
            if (!next.TryGetValue(pair.Key, out IReadOnlySet<int>? nextTargets) ||
                !pair.Value.SetEquals(nextTargets))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Bảng động theo đúng cách vận hành Htdrv:
    /// - Ban đầu hiển thị endpoint rows của các network chưa hoàn thành.
    /// - Network PASS sạch: mọi endpoint row của network biến mất khỏi bảng.
    /// - Network mở lại: endpoint rows tự hiện lại theo thứ tự model.
    /// - Splice nhiều nhánh chỉ biến mất khi toàn bộ logical network đã đạt.
    /// - I/O bất thường được giữ tại đúng vị trí pin và tô đỏ ở View.
    /// </summary>
    public IReadOnlyList<FaultRow> BuildRows()
    {
        ProductModel? model = _model;
        if (model is null)
            return [];

        var rows = new List<FaultRow>();

        lock (_gate)
        {
            Dictionary<int, int>? previewComponents =
                BuildContinuityPreviewComponentsUnsafe(out HashSet<int> previewExpectedComponents);

            // HTDRV_WIRING_FAIL_DISPLAY_2026-09-05: chỉ confirmed fault trong
            // _wiringFaults được phép thay presentation. Candidate tuyệt đối
            // không đi vào bảng operator.
            HashSet<int> diagnosticIos = [];
            rows.AddRange(BuildConfirmedWiringDisplayRows(model, diagnosticIos));
            rows.AddRange(BuildPreviewWiringDisplayRows(model, diagnosticIos));

            // CLIP giữ nguyên semantics latch hiện hữu. Continuity preview chỉ
            // tác động WireNet thường để không thay đổi workflow CLIP/PASS.
            if (model.Clip is not null)
            {
                if (!AnyClipBranchLatched(model.Clip) &&
                    _clipCommonDisplayRow is not null &&
                    !diagnosticIos.Contains(model.Clip.CommonIo))
                    rows.Add(_clipCommonDisplayRow);

                foreach (ClipBranch branch in OrderedClipBranches(model.Clip))
                {
                    if (!IsEligibleClipBranch(model.Clip, branch))
                        continue;

                    if (_latchedClipKeys.Contains(ClipConfirmationKey(branch.NetName)))
                        continue;

                    if (!diagnosticIos.Contains(branch.TargetIo) &&
                        _displayRowByClip.TryGetValue(branch, out FaultRow? row))
                        rows.Add(row);
                }
            }

            foreach (WireNet net in model.Nets
                         .Where(IsEligibleProductionNet)
                         .OrderBy(net => _displayOrderByNet.TryGetValue(net, out int order) ? order : int.MaxValue))
            {

                bool previewAffected = previewComponents is not null &&
                    IsNetAffectedByContinuityPreviewUnsafe(net, previewExpectedComponents);
                bool electricallyComplete = previewAffected
                    ? IsWireNetConnected(net, previewComponents!)
                    : _passedNets.Contains(net.Name);

                if (electricallyComplete || net.IoNumbers.Any(diagnosticIos.Contains))
                    continue;

                if (_displayRowsByNet.TryGetValue(net, out FaultRow[]? cachedRows))
                {
                    HashSet<int> reachable = previewAffected
                        ? BuildReachableNetEndpoints(net, previewComponents!)
                        : BuildReachableNetEndpoints(net);
                    if (reachable.Count < 2)
                        reachable.Clear();

                    bool retainerPair = IsRetainerPair(net);
                    rows.AddRange(retainerPair
                        ? cachedRows
                        : cachedRows.Where(row => !reachable.Contains(row.Io)));
                }
            }
        }

        return PromoteAoFamilyRows(rows);
    }

    private static IReadOnlyList<FaultRow> PromoteAoFamilyRows(List<FaultRow> rows)
    {
        // Universal Tester New: AO/A0/a1/a2/... is the fixture-common family
        // operators must see first. Keep every non-AO row in its existing stable
        // order; only lift AO-family rows to the head and order those connectors
        // AO -> a1 -> a2 -> a3... . This also keeps AO wrong/short rows visible
        // at the top instead of letting diagnostic insertion order push them down.
        if (rows.Count < 2 || !rows.Any(row => IsAoFamilyConnector(row.Connector)))
            return rows;

        FaultRow[] aoRows = rows
            .Where(row => IsAoFamilyConnector(row.Connector))
            .OrderBy(row => AoFamilyConnectorRank(row.Connector))
            .ThenBy(row => row.DisplayOrder)
            .ToArray();

        var ordered = new List<FaultRow>(rows.Count);
        ordered.AddRange(aoRows);
        ordered.AddRange(rows.Where(row => !IsAoFamilyConnector(row.Connector)));
        return ordered;
    }

    private Dictionary<int, int>? BuildContinuityPreviewComponentsUnsafe(
        out HashSet<int> affectedExpectedComponents)
    {
        affectedExpectedComponents = [];
        if (_continuityPreviewConnections.Count == 0)
            return null;

        var merged = new Dictionary<int, HashSet<int>>(
            _currentConnections.Count + _continuityPreviewConnections.Count);
        foreach ((int source, HashSet<int> targets) in _currentConnections)
            merged[source] = targets.ToHashSet();

        foreach ((int source, HashSet<int> targets) in _continuityPreviewConnections)
        {
            merged[source] = targets.ToHashSet();
            if (_componentByIo.TryGetValue(source, out int expectedComponent))
                affectedExpectedComponents.Add(expectedComponent);
        }

        return BuildActualComponents(merged);
    }

    private bool IsNetAffectedByContinuityPreviewUnsafe(
        WireNet net,
        IReadOnlySet<int> affectedExpectedComponents)
    {
        return _componentByIo.TryGetValue(net.SourceIo, out int expectedComponent) &&
               affectedExpectedComponents.Contains(expectedComponent);
    }

    private IReadOnlyList<FaultRow> BuildConfirmedWiringDisplayRows(
        ProductModel model,
        HashSet<int> diagnosticIos) =>
        BuildWiringDisplayRows(model, diagnosticIos, _wiringFaults);

    private IReadOnlyList<FaultRow> BuildPreviewWiringDisplayRows(
        ProductModel model,
        HashSet<int> diagnosticIos)
    {
        WiringFaultPair[] preview = _continuityPreviewWiringBySource.Values
            .SelectMany(static faults => faults)
            .Where(fault => !_wiringFaults.Contains(fault))
            .Distinct()
            .ToArray();
        return BuildWiringDisplayRows(model, diagnosticIos, preview);
    }

    private IReadOnlyList<FaultRow> BuildWiringDisplayRows(
        ProductModel model,
        HashSet<int> diagnosticIos,
        IEnumerable<WiringFaultPair> faults)
    {
        var rows = new List<FaultRow>();
        HashSet<string> keys = new(StringComparer.Ordinal);

        foreach (WiringFaultPair fault in faults
                     .OrderBy(item => item.SourceIo)
                     .ThenBy(item => item.TargetIo))
        {
            int[] relation = [fault.SourceIo, fault.TargetIo];

            // Universal Tester New already distinguishes OTHER and SHORT.
            // Show that firmware classification directly instead of expanding
            // one electrical pair into the legacy three-row guess
            // (wrong + short + open), which confused the TestView operator.
            if (fault.FaultType is ProductFaultType.WrongWiring or ProductFaultType.ShortCircuit)
            {
                bool isShort = fault.FaultType == ProductFaultType.ShortCircuit;
                string status = isShort ? "CHẬP MẠCH" : "SAI DÂY";
                FaultKind kind = isShort ? FaultKind.Short : FaultKind.WrongWiring;
                AddDiagnosticRow(rows, keys, diagnosticIos, model, fault,
                    fault.SourceIo, status, kind, fault.FaultType, relation);
                AddDiagnosticRow(rows, keys, diagnosticIos, model, fault,
                    fault.TargetIo, status, kind, fault.FaultType, relation);
                continue;
            }

            if (TryResolveExpectedDisplayRelation(
                    model,
                    fault,
                    out int expectedSource,
                    out int expectedTarget))
            {
                int wrongPeer = expectedSource == fault.SourceIo
                    ? fault.TargetIo
                    : fault.SourceIo;
                AddDiagnosticRow(rows, keys, diagnosticIos, model, fault,
                    expectedSource, "SAI DÂY", FaultKind.WrongWiring,
                    ProductFaultType.WrongWiring, relation);
                AddDiagnosticRow(rows, keys, diagnosticIos, model, fault,
                    wrongPeer, "CHẬP MẠCH", FaultKind.Short,
                    ProductFaultType.ShortCircuit, relation);
                AddDiagnosticRow(rows, keys, diagnosticIos, model, fault,
                    expectedTarget, "HỞ MẠCH", FaultKind.Open,
                    ProductFaultType.OpenCircuit, relation);
                continue;
            }

            string fallbackStatus = fault.FaultType == ProductFaultType.ShortCircuit
                ? "CHẬP MẠCH"
                : "SAI DÂY";
            FaultKind fallbackKind = fault.FaultType == ProductFaultType.ShortCircuit
                ? FaultKind.Short
                : FaultKind.WrongWiring;
            ProductFaultType fallbackType = fault.FaultType == ProductFaultType.ShortCircuit
                ? ProductFaultType.ShortCircuit
                : ProductFaultType.WrongWiring;

            AddDiagnosticRow(rows, keys, diagnosticIos, model, fault,
                fault.SourceIo, fallbackStatus, fallbackKind,
                fallbackType, relation);
            AddDiagnosticRow(rows, keys, diagnosticIos, model, fault,
                fault.TargetIo, fallbackStatus, fallbackKind,
                fallbackType, relation);
        }

        return rows;
    }

    private static bool TryResolveExpectedDisplayRelation(
        ProductModel model,
        WiringFaultPair fault,
        out int expectedSource,
        out int expectedTarget)
    {
        if (fault.ExpectedSourceIo is int suppliedSource &&
            fault.ExpectedTargetIo is int suppliedTarget)
        {
            expectedSource = suppliedSource;
            expectedTarget = suppliedTarget;
            return true;
        }

        // Firmware có thể báo cùng cạnh điện theo chiều ngược. Chỉ phục hồi
        // metadata presentation từ source topology đã biết; không đổi internal
        // ProductFaultType/classifier.
        WireNet? sourceNet = model.Nets.FirstOrDefault(net =>
            net.SourceIo == fault.SourceIo || net.SourceIo == fault.TargetIo);
        if (sourceNet is not null)
        {
            expectedSource = sourceNet.SourceIo;
            int actualPeer = expectedSource == fault.SourceIo
                ? fault.TargetIo
                : fault.SourceIo;
            expectedTarget = sourceNet.ExpectedActiveIo.FirstOrDefault(io => io != actualPeer);
            if (expectedTarget <= 0)
                expectedTarget = sourceNet.ExpectedActiveIo.FirstOrDefault();
            return expectedTarget > 0;
        }

        if (model.Clip is not null &&
            (model.Clip.CommonIo == fault.SourceIo || model.Clip.CommonIo == fault.TargetIo))
        {
            expectedSource = model.Clip.CommonIo;
            int actualPeer = expectedSource == fault.SourceIo
                ? fault.TargetIo
                : fault.SourceIo;
            expectedTarget = model.Clip.Branches
                .Select(branch => branch.TargetIo)
                .FirstOrDefault(io => io > 0 && io != actualPeer);
            return expectedTarget > 0;
        }

        expectedSource = 0;
        expectedTarget = 0;
        return false;
    }

    private void AddDiagnosticRow(
        List<FaultRow> rows,
        HashSet<string> keys,
        HashSet<int> diagnosticIos,
        ProductModel model,
        WiringFaultPair fault,
        int io,
        string status,
        FaultKind kind,
        ProductFaultType productFaultType,
        int[] relation)
    {
        if (io <= 0)
            return;

        PinRecord? pin = ResolveProductionDisplayPin(model, io);
        string network = ResolveTopologyName(model, io);
        string key = $"{network}|{io}|{status}|{Math.Min(relation[0], relation[1])}:{Math.Max(relation[0], relation[1])}";
        if (!keys.Add(key))
            return;

        diagnosticIos.Add(io);
        bool unused = pin is null;
        rows.Add(new FaultRow
        {
            Kind = kind,
            ProductFaultType = productFaultType,
            FaultType = unused ? string.Empty : ResolveTopologyType(model, io),
            Io = io,
            IoTextOverride = unused ? $"IO ({io})" : string.Empty,
            DisplayOrder = pin is null ? int.MaxValue : ResolveDisplayOrder(pin),
            ExpectedSourceIo = fault.ExpectedSourceIo,
            ExpectedTargetIo = fault.ExpectedTargetIo,
            ActualSourceIo = fault.SourceIo,
            ActualTargetIo = fault.TargetIo,
            RelatedIos = relation,
            // IO không có PinRecord phải nằm ở cột IO. Không dùng cột
            // Connector để chứa nhãn IO vì sẽ khiến người vận hành hiểu nhầm.
            Connector = unused ? string.Empty : pin!.Connector,
            Pin = unused ? string.Empty : pin!.PinNumber,
            WireName = unused ? string.Empty : pin!.WireName,
            Splice = unused ? string.Empty : pin!.SpliceName,
            Section = unused ? string.Empty : pin!.Section,
            Color = unused ? string.Empty : pin!.Color,
            IoCnPnOverride = unused ? $"IO{io}" : string.Empty,
            Status = status
        });
    }

    private PinRecord? ResolveProductionDisplayPin(ProductModel model, int io) =>
        model.Pins
            .Where(pin => pin.IoNumber == io && !string.IsNullOrWhiteSpace(pin.WireName))
            .Where(pin => (_netsByPin.GetValueOrDefault(pin)?.Length ?? 0) > 0 ||
                          model.Clip?.IsSpecialPin(pin) == true)
            .OrderBy(ResolveDisplayOrder)
            .FirstOrDefault();

    private static string ResolveTopologyName(ProductModel model, int io) =>
        model.Nets.FirstOrDefault(net => net.IoNumbers.Contains(io))?.Name ??
        model.Clip?.Branches.FirstOrDefault(branch => branch.TargetIo == io)?.NetName ??
        (model.Clip?.CommonIo == io ? "CLIP" : string.Empty);

    private static string ResolveTopologyType(ProductModel model, int io)
    {
        WireNet? net = model.Nets.FirstOrDefault(item => item.IoNumbers.Contains(io));
        if (net is not null)
            return !net.IsSplice && net.IoNumbers.Where(value => value > 0).Distinct().Count() == 2
                ? "Đơn"
                : "Nối chung";
        return model.Clip is not null &&
               (model.Clip.CommonIo == io || model.Clip.Branches.Any(branch => branch.TargetIo == io))
            ? "Nối chung"
            : string.Empty;
    }

    private FaultRow[] CreateNetworkMappingRowsCore(ProductModel model, WireNet net, string status)
    {

        PinRecord[] pins = net.Pins
            .Where(pin => pin.IoNumber > 0)
            .GroupBy(pin => pin.IoNumber)
            .Select(group => group.First())
            .OrderBy(ResolveDisplayOrder)
            .ToArray();

        if (pins.Length == 0)
            return [];

        HashSet<int> endpointIos = net.IoNumbers
            .Where(io => io > 0)
            .Distinct()
            .ToHashSet();
        int[] relatedIos = endpointIos
            .OrderBy(io => ResolvePeerOrder(net, io))
            .ToArray();
        string topologyType = !net.IsSplice && endpointIos.Count == 2
            ? "Đơn"
            : "Nối chung";

        return pins
            .Select((pin, endpointIndex) =>
            {
                int canonicalSourceIo = net.SourceIo > 0
                    ? net.SourceIo
                    : pins[0].IoNumber;
                int? rowTargetIo = pin.IoNumber == canonicalSourceIo
                    ? endpointIos
                        .Where(io => io != canonicalSourceIo)
                        .OrderBy(io => ResolvePeerOrder(net, io))
                        .Cast<int?>()
                        .FirstOrDefault()
                    : pin.IoNumber;

                return new FaultRow
                {
                    Kind = FaultKind.MissingConnection,
                    ProductFaultType = ProductFaultType.None,
                    FaultType = topologyType,
                    Io = pin.IoNumber,
                    DisplayOrder = ResolveNetworkEndpointDisplayOrder(net, pin, endpointIndex),
                    ExpectedSourceIo = canonicalSourceIo,
                    ExpectedTargetIo = rowTargetIo,
                    RelatedIos = relatedIos,
                    Connector = pin.Connector,
                    Pin = pin.PinNumber,
                    WireName = pin.WireName,
                    Splice = pin.SpliceName,
                    Section = pin.Section,
                    Color = pin.Color,
                    Status = status,
                    // Htdrv renders CONNECTOR/PIN from the network endpoint,
                    // but IO-CN-PN from the canonical physical mapping of the
                    // IO. These are not always the same PinRecord (for
                    // example IO122 is displayed as CN8/14 but maps to
                    // 122-4-26).
                    IoCnPnOverride = BuildCanonicalIoCnPn(model, pin),
                    IsNetworkStart = endpointIndex == 0
                };
            })
            .ToArray();
    }

    private static string BuildCanonicalIoCnPn(ProductModel model, PinRecord displayedPin)
    {
        if (displayedPin.IoNumber <= 0)
            return string.Empty;

        // Htdrv's IO-CN-PN is the physical 32-I/O address, not the endpoint
        // connector/pin stored on the WireNet row. IO122 is therefore
        // 122-4-26 even when the displayed endpoint is CN8/14.
        int zeroBased = displayedPin.IoNumber - 1;
        int connector = (zeroBased / BoardCapacity.IoPerPort) + 1;
        int pin = (zeroBased % BoardCapacity.IoPerPort) + 1;
        return string.Join("-", displayedPin.IoNumber, connector, pin);
    }

    private static bool IsRetainerPair(WireNet net) =>
        net.IoNumbers.Count == 2 &&
        net.Pins.Count >= 2 &&
        net.Pins.All(pin => pin.WireName.StartsWith("RET", StringComparison.OrdinalIgnoreCase));

    public bool SuppressProbeRelatedWiringFaults(IReadOnlyCollection<int> probeIos)
    {
        ArgumentNullException.ThrowIfNull(probeIos);
        HashSet<int> suppressed = probeIos.Where(io => io > 0).ToHashSet();
        if (suppressed.Count == 0)
            return false;

        bool changed;
        lock (_gate)
        {
            changed = !_probeEvidenceExcludedIo.SetEquals(suppressed);
            _probeEvidenceExcludedIo.Clear();
            foreach (int io in suppressed)
                _probeEvidenceExcludedIo.Add(io);

            changed |= _candidateWiringFaults.RemoveWhere(fault =>
                          (suppressed.Contains(fault.SourceIo) || suppressed.Contains(fault.TargetIo)) &&
                          !_wiringFaults.Contains(fault)) > 0;

            // Không xóa candidate đã confirmed hoặc _wiringFaults: đó có thể là
            // lỗi SHORT/WRONG thật tồn tại trước khi TESTPIN/probe xuất hiện.
        }

        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    /// <summary>
    /// Called immediately before the next complete frame that is not classified
    /// as Probe. That frame becomes the new authoritative product snapshot.
    /// </summary>
    public void ClearProbeEvidenceExclusions()
    {
        lock (_gate)
            _probeEvidenceExcludedIo.Clear();
    }

    /// <summary>
    /// Xác nhận có ít nhất một cạnh continuity hiện tại chạm vào bất kỳ pin
    /// nào của connector. Gate này chỉ dùng để khởi động Leak trước continuity;
    /// nó không thay thế điều kiện PASS toàn bộ topology của sản phẩm.
    /// </summary>
    public bool HasConnectorActivity(string? connectorId)
    {
        if (string.IsNullOrWhiteSpace(connectorId))
            return false;

        lock (_gate)
        {
            if (_model is null)
                return false;

            ConnectorDefinition? connector = _model.Connectors.FirstOrDefault(item =>
                string.Equals(item.ConnectorId, connectorId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (connector is null)
                return false;

            HashSet<int> connectorIo = connector.Pins
                .Select(pin => pin.PhysicalIo)
                .Where(io => io > 0 && !_model.IgnoredIo.Contains(io))
                .ToHashSet();
            if (connectorIo.Count == 0)
                return false;

            return _currentConnections.Any(pair =>
                pair.Value.Any(target =>
                    IsProductConnectivityEdgeUnsafe(_model, pair.Key, target) &&
                    (connectorIo.Contains(pair.Key) || connectorIo.Contains(target))));
        }
    }

    /// <summary>
    /// Leak chỉ được kích hoạt khi một mạng dây có mã RET/RT + số
    /// (RET1, RET01, RT1, RET02', RT15...) đã thông đúng toàn bộ topology và mạng đó
    /// đi qua connector được cấu hình cho kênh Leak.
    /// </summary>
    public bool HasConnectedRetWire(string? connectorId)
    {
        if (string.IsNullOrWhiteSpace(connectorId))
            return false;

        string selectedConnector = connectorId.Trim();
        lock (_gate)
        {
            if (_model is null || !_model.Connectors.Any(connector =>
                    string.Equals(
                        connector.ConnectorId,
                        selectedConnector,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return _model.Nets
                .Where(IsEligibleProductionNet)
                .Where(net => IsRetWireName(net.Name))
                .Where(net => net.Pins.Any(pin =>
                    string.Equals(
                        pin.Connector,
                        selectedConnector,
                        StringComparison.OrdinalIgnoreCase)))
                .Any(IsWireNetConnected);
        }
    }

    private static bool IsRetWireName(string? wireName)
    {
        if (string.IsNullOrWhiteSpace(wireName))
            return false;

        ReadOnlySpan<char> value = wireName.Trim().AsSpan();
        int numericIndex = value.StartsWith("RET", StringComparison.OrdinalIgnoreCase)
            ? 3
            : value.StartsWith("RT", StringComparison.OrdinalIgnoreCase)
                ? 2
                : -1;
        return numericIndex >= 0 &&
               value.Length > numericIndex &&
               char.IsDigit(value[numericIndex]);
    }

    /// <summary>
    /// Xác nhận connector được chọn cho máy Leak đang thực sự có continuity
    /// đúng theo topology THT. Không suy diễn kênh Leak từ số I/O/connector.
    /// </summary>
    public bool IsConnectorConnected(string? connectorId)
    {
        if (string.IsNullOrWhiteSpace(connectorId))
            return false;

        lock (_gate)
        {
            if (_model is null)
                return false;

            ConnectorDefinition? connector = _model.Connectors.FirstOrDefault(item =>
                string.Equals(item.ConnectorId, connectorId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (connector is null)
                return false;

            WireNet[] requiredNets = _model.Nets
                .Where(IsEligibleProductionNet)
                .Where(net => net.Pins.Any(pin =>
                    string.Equals(pin.Connector, connector.ConnectorId, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            ClipBranch[] requiredClipBranches = _model.Clip?.Branches
                .Where(branch => IsEligibleClipBranch(_model.Clip, branch))
                .Where(branch =>
                    string.Equals(branch.ClipPin.Connector, connector.ConnectorId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(branch.TargetPin?.Connector, connector.ConnectorId, StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? [];

            int requiredRelationCount = requiredNets.Length + requiredClipBranches.Length;
            if (requiredRelationCount == 0)
                return false;

            return requiredNets.All(IsWireNetConnected) &&
                   requiredClipBranches.All(branch =>
                       IsClipBranchConnected(_model.Clip!, branch, _currentConnections));
        }
    }

    /// <summary>
    /// Xác nhận toàn bộ quan hệ continuity thuộc connector đã mất. Khác với
    /// !IsConnectorConnected: chỉ một chân chập chờn/thiếu không được xem là
    /// thao tác tháo connector để kích hoạt Leak retest.
    /// </summary>
    public bool IsConnectorDisconnected(string? connectorId)
    {
        if (string.IsNullOrWhiteSpace(connectorId))
            return false;

        lock (_gate)
        {
            if (_model is null)
                return false;

            ConnectorDefinition? connector = _model.Connectors.FirstOrDefault(item =>
                string.Equals(item.ConnectorId, connectorId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (connector is null)
                return false;

            WireNet[] requiredNets = _model.Nets
                .Where(IsEligibleProductionNet)
                .Where(net => net.Pins.Any(pin =>
                    string.Equals(pin.Connector, connector.ConnectorId, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            ClipBranch[] requiredClipBranches = _model.Clip?.Branches
                .Where(branch => IsEligibleClipBranch(_model.Clip, branch))
                .Where(branch =>
                    string.Equals(branch.ClipPin.Connector, connector.ConnectorId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(branch.TargetPin?.Connector, connector.ConnectorId, StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? [];

            int requiredRelationCount = requiredNets.Length + requiredClipBranches.Length;
            if (requiredRelationCount == 0)
                return false;

            return requiredNets.All(net => !IsWireNetConnected(net)) &&
                   requiredClipBranches.All(branch =>
                       !IsClipBranchConnected(_model.Clip!, branch, _currentConnections));
        }
    }

    int ResolveNetworkEndpointDisplayOrder(WireNet net, PinRecord pin, int endpointIndex) =>
        _displayOrderByNet.TryGetValue(net, out int baseOrder)
            ? baseOrder + Math.Clamp(endpointIndex, 0, 999)
            : ResolveDisplayOrder(pin);

    FaultRow CreateMissingClipConnectionRow(ClipTopology clip, ClipBranch branch, string status)
    {
        PinRecord displayPin = branch.TargetPin ?? branch.ClipPin;
        return new FaultRow
        {
            Kind = FaultKind.MissingConnection,
            ProductFaultType = ProductFaultType.None,
            FaultType = "Nối chung",
            Io = branch.TargetIo,
            DisplayOrder = ClipDisplayOrder(branch),
            ExpectedSourceIo = clip.CommonIo,
            ExpectedTargetIo = branch.TargetIo,
            Connector = displayPin.Connector,
            Pin = displayPin.PinNumber,
            WireName = ResolveClipDisplayName(displayPin.WireName, branch.Name),
            Splice = displayPin.SpliceName,
            Section = displayPin.Section,
            Color = displayPin.Color,
            Status = status
        };
    }

    FaultRow CreateClipCommonDisplayRow(ClipTopology clip, string status)
    {
        PinRecord common = clip.CommonPin;
        return new FaultRow
        {
            Kind = FaultKind.MissingConnection,
            ProductFaultType = ProductFaultType.None,
            FaultType = "Nối chung",
            Io = common.IoNumber,
            DisplayOrder = ClipDisplayOrderBase,
            Connector = common.Connector,
            Pin = common.PinNumber,
            WireName = ResolveClipCommonDisplayName(common),
            Splice = common.SpliceName,
            Section = common.Section,
            Color = common.Color,
            Status = status
        };
    }

    bool AnyClipBranchLatched(ClipTopology clip) =>
        clip.Branches.Any(branch => _latchedClipKeys.Contains(ClipConfirmationKey(branch.NetName)));

    static IEnumerable<ClipBranch> OrderedClipBranches(ClipTopology clip) =>
        clip.Branches
            .OrderBy(ClipOriginalOrder)
            .ThenBy(branch => branch.BranchNumber)
            .ThenBy(branch => branch.TargetIo);

    static int ClipOriginalOrder(ClipBranch branch)
    {
        int clipOrder = branch.ClipPin.OriginalOrder > 0
            ? branch.ClipPin.OriginalOrder
            : int.MaxValue;
        int targetOrder = branch.TargetPin?.OriginalOrder > 0
            ? branch.TargetPin.OriginalOrder
            : int.MaxValue;

        return Math.Min(clipOrder, targetOrder);
    }

    static int ClipDisplayOrder(ClipBranch branch)
    {
        int original = ClipOriginalOrder(branch);
        int sequence = original == int.MaxValue
            ? Math.Clamp(branch.BranchNumber, 1, 999_999)
            : Math.Clamp(original, 1, 999_999);

        return ClipDisplayOrderBase + sequence;
    }

    static string FirstNotEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    static string ResolveClipDisplayName(string? pinWireName, string branchName)
    {
        string candidate = FirstNotEmpty(pinWireName);
        if (candidate.StartsWith("CLIP-", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("CLIP ", StringComparison.OrdinalIgnoreCase))
        {
            return branchName;
        }

        return FirstNotEmpty(candidate, branchName);
    }

    static string ResolveClipCommonDisplayName(PinRecord common)
    {
        string candidate = FirstNotEmpty(common.WireName);
        if (candidate.StartsWith("CLIP-", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("CLIP ", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNotEmpty(common.PinType, common.PinNumber, "A0");
        }

        return FirstNotEmpty(candidate, common.PinType, common.PinNumber);
    }

    int ResolveDisplayOrder(PinRecord pin) =>
        _displayOrderByPin.TryGetValue(pin, out int order)
            ? order
            : pin.OriginalOrder > 0
                ? pin.OriginalOrder
                : int.MaxValue;

    int ResolvePeerOrder(WireNet net, int io)
    {
        PinRecord? pin = net.Pins.FirstOrDefault(item => item.IoNumber == io);
        return pin is null ? int.MaxValue : ResolveDisplayOrder(pin);
    }


    /// <summary>
    /// Sau khi người vận hành XÁC NHẬN hàng lỗi, pulse đúng Relay vật lý 1
    /// (firmware OUTPUT channel 0) để nhả JIG. Không chạy MARKING của PASS.
    /// </summary>
    public Task EjectFaultProductAsync(CancellationToken ct = default)
        => PulseJigRelayAsync(ct);

    /// <summary>
    /// Universal Tester New: OUTPUT JIG chỉ được pulse đúng một lần.
    /// Dù delay bị hủy hoặc board phát sinh exception, finally vẫn cố đưa toàn bộ OUTPUT về OFF.
    /// </summary>
    public Task PulseJigRelayAsync(CancellationToken ct = default)
    {
        int relay = ConfiguredJigRelay;
        string relayName = "RELAY 1 / OUT0 JIG";
        return _production.JigEjectRelayEnabled
            ? PulseRelaySafeAsync(relay, Math.Clamp(_production.Relay1JigPulseMs, 50, 5_000), relayName, ct)
            : SkipDisabledRelayAsync(relayName, ct);
    }

    /// <summary>OUTPUT MARKING được pulse đúng một lần và luôn chờ firmware xác nhận OFF.</summary>
    public Task PulseMarkingRelayAsync(CancellationToken ct = default)
    {
        int relay = ConfiguredMarkingRelay;
        string relayName = "RELAY 5 / OUT4 MARKING";
        return _production.PassMarkingRelayEnabled
            ? PulseRelaySafeAsync(relay, Math.Clamp(_production.Relay2MarkingPulseMs, 50, 5_000), relayName, ct)
            : SkipDisabledRelayAsync(relayName, ct);
    }

    /// <summary>Thử đúng relay vật lý theo số, không áp dụng ánh xạ vai trò production.</summary>
    public Task PulsePhysicalRelayAsync(int relay, CancellationToken ct = default)
    {
        if (!JbzBoardTransportAdapter.IsPhysicalRelayOutput(relay))
            throw new ArgumentOutOfRangeException(nameof(relay), relay,
                "Only physical Relay 1 (OUT0) and Relay 5 (OUT4) are valid.");

        int duration = relay == ConfiguredMarkingRelay
            ? _production.Relay2MarkingPulseMs
            : _production.Relay1JigPulseMs;
        return PulseRelaySafeAsync(relay, duration, relay == 0 ? "RELAY 1 / OUT0 MANUAL" : "RELAY 5 / OUT4 MANUAL", ct);
    }

    private int ConfiguredJigRelay => JbzBoardTransportAdapter.Relay1OutputChannel;
    private int ConfiguredMarkingRelay => JbzBoardTransportAdapter.Relay5OutputChannel;

    /// <summary>
    /// Eject riêng cho Master Sample. Chỉ OUTPUT JIG đã cấu hình được pulse;
    /// tuyệt đối không MARKING và không dùng behavior Product FAIL.
    /// </summary>
    public Task EjectMasterSampleAsync(CancellationToken ct = default)
        => PulseJigRelayAsync(ct);

    private async Task PulseRelaySafeAsync(int relay, int durationMs, string relayName, CancellationToken ct)
    {
        int pulseMs = Math.Clamp(durationMs, 50, 5_000);
        await _relayPulseGate.WaitAsync(ct);
        Exception? offFailure = null;
        try
        {
            // Luôn bắt đầu từ trạng thái OFF để không kế thừa trạng thái relay trước đó.
            await ForceAllRelaysOffAsync(relayName + " PRE", ct);
            string relayMarker = relay == ConfiguredMarkingRelay
                ? "T_OUTPUT_MARKING"
                : relay == ConfiguredJigRelay
                    ? "T_OUTPUT_JIG"
                    : $"T_OUTPUT_{relay}";
            AsyncFileLogService.Current.Performance(
                $"PASS_LATENCY {relayMarker}_START relay={relay} name=\"{relayName}\" pulse_ms={pulseMs}");
            await _board.SetRelayAsync(relay, ct);
            AsyncFileLogService.Current.Test($"RELAY {relayName} ON - pulse {pulseMs} ms");
            await Task.Delay(pulseMs, ct);
        }
        finally
        {
            try
            {
                // CancellationToken của cycle có thể đã cancel. Safe-OFF vẫn phải cố chạy độc lập.
                await ForceAllRelaysOffAsync(relayName + " POST", CancellationToken.None);
                string relayMarker = relay == ConfiguredMarkingRelay
                    ? "T_OUTPUT_MARKING"
                    : relay == ConfiguredJigRelay
                        ? "T_OUTPUT_JIG"
                        : $"T_OUTPUT_{relay}";
                AsyncFileLogService.Current.Performance(
                    $"PASS_LATENCY {relayMarker}_END relay={relay} name=\"{relayName}\"");
                AsyncFileLogService.Current.Test($"RELAY {relayName} OFF - safe idle");
            }
            catch (Exception ex)
            {
                offFailure = ex;
                AsyncFileLogService.Current.Error($"RELAY SAFE-OFF FAILED after {relayName}: {ex.Message}");
            }
            _relayPulseGate.Release();
        }

        // Nếu OFF không xác nhận được, không cho workflow tiếp tục sang relay tiếp theo.
        if (offFailure is not null)
            throw new InvalidOperationException($"Không thể đưa relay về OFF sau {relayName}.", offFailure);
    }

    private async Task ForceAllRelaysOffAsync(string reason, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _board.AllRelaysOffAsync(ct);
                AsyncFileLogService.Current.Test($"ALL RELAYS OFF [{reason}] attempt={attempt}");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                AsyncFileLogService.Current.Error($"ALL RELAYS OFF FAILED [{reason}] attempt={attempt}: {ex.Message}");
                if (attempt < 3)
                    await Task.Delay(80, CancellationToken.None);
            }
        }

        throw new InvalidOperationException($"Không thể cưỡng bức ALL RELAYS OFF ({reason}).", last);
    }

    private async Task SkipDisabledRelayAsync(string relayName, CancellationToken ct)
    {
        await ForceAllRelaysOffAsync(relayName + " DISABLED", ct);
        AsyncFileLogService.Current.Test($"RELAY {relayName} SKIPPED - disabled by Production Settings");
    }

    public Task<List<ResistanceResult>> MeasureResistanceAsync(CancellationToken ct = default) =>
        MeasureResistanceAsync(null, ct);

    public async Task<List<ResistanceResult>> MeasureResistanceAsync(
        Action<ResistanceResult>? onChannelUpdated,
        CancellationToken ct = default)
    {
        ProductModel? model = _model;
        if (model is null)
            return [];

        foreach (ResistanceChannelSetting configured in _production.ResistanceChannels)
        {
            if (!configured.Enabled)
            {
                AsyncFileLogService.Current.Test(
                    $"[AUTO-R] {configured.Name} disabled -> skipped");
            }
            else if (configured.Channel == 0)
            {
                AsyncFileLogService.Current.Test(
                    $"[AUTO-R] {configured.Name} channel=0 -> skipped");
            }
        }

        List<ResistanceStep> enabledSteps = ResistanceMeasurementPlan.BuildEnabledSteps(_production);
        return await MeasureResistanceStepsAsync(enabledSteps, onChannelUpdated, ct);
    }

    public async Task<List<ResistanceResult>> MeasureResistanceStepsAsync(
        IReadOnlyList<ResistanceStep> steps,
        Action<ResistanceResult>? onChannelUpdated = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(steps);

        ResistanceStep[] enabledSteps = steps
            .Where(step => step.Channel is >= ResistanceMeasurementPlan.MinChannel and
                <= ResistanceMeasurementPlan.MaxChannel)
            .ToArray();
        if (enabledSteps.Length == 0)
            return [];

        if (!_visa.IsConnected)
            throw new InvalidOperationException("Chưa kết nối Keysight 34461A");

        // Real production trace:
        // STOP_SCAN -> RESET_CLEAR -> R1 route -> measure -> R2 route -> measure
        // -> three INIT_1/INIT_2 recovery cycles. Do not release between R1/R2.
        await _board.StopScanAsync(ct);
        await _board.ResetClearAsync(ct);

        var results = new List<ResistanceResult>();

        try
        {
            foreach (ResistanceStep step in enabledSteps)
            {
                ct.ThrowIfCancellationRequested();

                // Production Settings là nguồn duy nhất cho danh sách bước,
                // kênh vật lý và giới hạn Min/Max. Kênh điện trở UART lấy trực tiếp
                // từ Channel trong Production Settings;
                // không phụ thuộc block resistance THT hay RouteA/RouteB cũ.
                AsyncFileLogService.Current.Test(
                    $"[AUTO-R] {step.Name} enabled=true channel={step.Channel} " +
                    $"uart_channel={step.Channel}");
                await _board.SelectResistanceRouteAsync(step, ct);

                var measuring = new ResistanceResult
                {
                    Name = step.Name,
                    Channel = step.Channel,
                    MinOhm = step.MinOhm,
                    MaxOhm = step.MaxOhm,
                    MeasurementStatus = "ĐANG ĐO"
                };
                onChannelUpdated?.Invoke(measuring);

                ResistanceResult result = await MeasureChannelStableAsync(step, ct);
                results.Add(result);
                onChannelUpdated?.Invoke(result);
            }
        }
        finally
        {
            await _board.ReleaseResistanceRouteAsync(CancellationToken.None);
        }

        return results;
    }

    private async Task<ResistanceResult> MeasureChannelStableAsync(
        ResistanceStep step,
        CancellationToken ct)
    {
        int minimumSettleMs = Math.Max(
            Math.Max(0, _settings.Keysight.SettleDelayMs),
            Math.Max(
                Math.Max(0, _production.ResistanceDelayMs),
                Math.Max(0, _settings.Test.ResistanceMinimumSettleMs)));
        AsyncFileLogService.Current.Test(
            $"[AUTO-R] CH{step.Channel} minimum settle start ms={minimumSettleMs}");
        if (minimumSettleMs > 0)
            await Task.Delay(minimumSettleMs, ct);
        AsyncFileLogService.Current.Test($"[AUTO-R] CH{step.Channel} minimum settle complete");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int requiredSamples = Math.Max(2, _settings.Test.ResistanceStableSampleCount);
        int timeoutMs = Math.Clamp(
            _settings.Test.ResistanceStabilityTimeoutMs,
            100,
            120_000);
        int intervalMs = Math.Max(0, _settings.Test.ResistanceSampleIntervalMs);
        var numericWindow = new Queue<double>(requiredSamples);
        int consecutiveOpen = 0;
        int sampleCount = 0;
        double? stableValue = null;
        bool confirmedOpen = false;
        bool stable = false;

        while (!stable && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            double sample = await Task.Run(
                () => _visa.MeasureResistance(_settings.Keysight.Command),
                ct);
            sampleCount++;
            bool open = !double.IsFinite(sample) ||
                        Math.Abs(sample) >= _settings.Test.ResistanceOpenThreshold;
            AsyncFileLogService.Current.Test(open
                ? $"[AUTO-R] CH{step.Channel} sample#{sampleCount}=OPEN"
                : $"[AUTO-R] CH{step.Channel} sample#{sampleCount}={sample:0.###}");

            if (open)
            {
                numericWindow.Clear();
                consecutiveOpen++;
                if (consecutiveOpen >= requiredSamples)
                {
                    confirmedOpen = true;
                    stable = true;
                }
            }
            else
            {
                consecutiveOpen = 0;
                numericWindow.Enqueue(sample);
                while (numericWindow.Count > requiredSamples)
                    numericWindow.Dequeue();

                if (numericWindow.Count == requiredSamples &&
                    AreResistanceSamplesStable(numericWindow))
                {
                    stableValue = numericWindow.Average();
                    stable = true;
                }
            }

            if (!stable && stopwatch.ElapsedMilliseconds < timeoutMs && intervalMs > 0)
                await Task.Delay(intervalMs, ct);
        }

        if (!stable && consecutiveOpen > 0)
        {
            // Bounded timeout is authoritative: an OPEN that never recovered
            // during the whole channel window is a real final OPEN failure.
            confirmedOpen = true;
            stable = true;
        }

        bool passed = stable &&
                      !confirmedOpen &&
                      stableValue is double stableOhm &&
                      stableOhm >= step.MinOhm &&
                      stableOhm <= step.MaxOhm;
        string status = stable ? passed ? "PASS" : "FAIL" : "UNSTABLE";
        string stableText = confirmedOpen
            ? "OPEN"
            : stableValue is double displayOhm
                ? displayOhm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : "NONE";
        AsyncFileLogService.Current.Test(
            $"[AUTO-R] CH{step.Channel} stable={stable.ToString().ToLowerInvariant()} " +
            $"samples={sampleCount} value={stableText}");
        AsyncFileLogService.Current.Test(
            $"[AUTO-R] CH{step.Channel} FINAL {status} time={stopwatch.ElapsedMilliseconds}ms");

        ResistanceResult result = BuildResistanceResult(
            step,
            stableValue,
            confirmedOpen,
            stable,
            status,
            sampleCount,
            stopwatch.ElapsedMilliseconds);
        LogResistanceDiagnostic(result);
        return result;
    }

    private bool AreResistanceSamplesStable(IEnumerable<double> samples)
    {
        double[] values = samples.ToArray();
        if (values.Length < 2)
            return false;

        double averageMagnitude = Math.Abs(values.Average());
        double allowedDelta = Math.Max(
            _settings.Test.ResistanceStableAbsoluteToleranceOhm,
            averageMagnitude * _settings.Test.ResistanceStableRelativeTolerancePercent / 100d);
        return values.Max() - values.Min() <= allowedDelta;
    }

    private void LogResistanceDiagnostic(ResistanceResult result)
    {
        string raw = string.IsNullOrWhiteSpace(_visa.LastRawResistanceResponse)
            ? "(not captured)"
            : _visa.LastRawResistanceResponse;
        string valueText = result.IsOpen
            ? "OPEN"
            : result.ValueOhm is double value
                ? FormattableString.Invariant($"{value:0.##########}")
                : "NO VALUE";

        AsyncFileLogService.Current.Test($"[RES] {result.ChannelText} raw instrument response = {raw}");
        AsyncFileLogService.Current.Test($"[RES] {result.ChannelText} ValueOhm = {valueText}");
        AsyncFileLogService.Current.Test(FormattableString.Invariant($"[RES] {result.ChannelText} MinOhm = {result.MinOhm:0.##########}"));
        AsyncFileLogService.Current.Test(FormattableString.Invariant($"[RES] {result.ChannelText} MaxOhm = {result.MaxOhm:0.##########}"));
        AsyncFileLogService.Current.Test($"[RES] {result.ChannelText} DisplayUnit = {result.DisplayUnitText}");
        AsyncFileLogService.Current.Test($"[RES] {result.ChannelText} DisplayValue = {result.Display}");
        AsyncFileLogService.Current.Test($"[RES] {result.ChannelText} Result = {result.ResultText}");
    }

    private static ResistanceResult BuildResistanceResult(
        ResistanceStep step,
        double? valueOhm,
        bool open,
        bool stable,
        string status,
        int sampleCount,
        long stabilizationTimeMs)
    {
        bool passed = stable &&
                      !open &&
                      valueOhm is double value &&
                      value >= step.MinOhm &&
                      value <= step.MaxOhm;

        return new ResistanceResult
        {
            Name = step.Name,
            Channel = step.Channel,
            ValueOhm = open ? null : valueOhm,
            MinOhm = step.MinOhm,
            MaxOhm = step.MaxOhm,
            IsOpen = open,
            IsStable = stable,
            Passed = passed,
            MeasurementStatus = status,
            SampleCount = sampleCount,
            StabilizationTimeMs = stabilizationTimeMs
        };
    }

    public Task<bool> CompletePassAsync(
        IReadOnlyList<ResistanceResult> resistance,
        Action? onPassStarted = null,
        bool markingEnabled = true,
        bool continuityAlreadyValidated = false,
        CancellationToken ct = default)
    {
        ProductModel? model = _model;
        if (model is null)
            return Task.FromResult(false);

        int expectedResistanceCount = ResistanceMeasurementPlan.BuildEnabledSteps(_production).Count;
        bool resistanceOk = expectedResistanceCount == 0 ||
                            (resistance.Count == expectedResistanceCount && resistance.All(x => x.Passed));
        bool ok = (ContinuityPassed || continuityAlreadyValidated) && resistanceOk;
        if (ok) onPassStarted?.Invoke();

        // Production PASS relay/removal is owned by firmware via PASSPEN -> PEN -> UNCONNECT.
        // OUTPUTTEST remains available for Manual Relay / Master / explicit fault eject only.
        return Task.FromResult(ok);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _frameProcessingEnabled = false;
        Changed = null;
        _relayPulseGate.Dispose();
    }

}
