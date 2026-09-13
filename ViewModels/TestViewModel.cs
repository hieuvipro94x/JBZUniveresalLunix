using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using JBZUniversalTester.Core;
using JBZUniversalTester.Models;
using JBZUniversalTester.Services;

namespace JBZUniversalTester.ViewModels;

public enum ProductionRuntimeState
{
    WaitingForProduct = 0,
    TestingRealtime = 1,
    PassSequence = 2,
    WaitingForRemoval = 3,
    Failed = 4
}

public enum ProbePresentationState
{
    Inactive = 0,
    Candidate = 1,
    Touch = 2,
    Released = 3
}

public enum ProductionPresentationMode
{
    Waiting = 0,
    Product = 1,
    Probe = 2,
    LiveTopology = 3
}

public sealed class TestViewModel : ObservableObject
{
    private sealed record LabelPrintContext(
        LabelPrintRequest Request,
        TestHistoryStore HistoryStore,
        long HistoryId);

    private enum RuntimeMode
    {
        Background = 0,
        Production = 1,
        Probe = 2,
        ShuttingDown = 3
    }

    private enum ProductionPhase
    {
        WaitingProduct = 0,
        Continuity = 1,
        Resistance = 2,
        WaterProof = 3,
        WaitingFaultConfirmation = 4,
        WaitingProductRemoval = 5,
        Completed = 6,
        EquipmentError = 7
    }

    private enum WaterProofRetestConnectorState
    {
        Inactive = 0,
        AwaitingConnectorRemoval = 1,
        AwaitingConnectorReconnect = 2,
        Running = 3
    }

    private readonly MainViewModel _main;
    private readonly TestEngine _engine;
    private readonly IBoardTransport _board;
    private readonly ScanSupervisor _scanSupervisor;
    private readonly KeysightVisaService _visa;
    private readonly WaterProofSerialService _waterProof;
    private readonly AppSettings _settings;
    private readonly ProductionSettings _productionSettings;
    private readonly LotSequenceService _lotSequence;
    private readonly Lazy<PartCounterStore> _partCounterStore = new(() => new PartCounterStore());
    private readonly LegacyPhtHistoryService _legacyHistory;
    private readonly SemaphoreSlim _statisticsLoadGate = new(1, 1);
    private readonly SemaphoreSlim _productionPersistenceGate = new(1, 1);
    private readonly SemaphoreSlim _removalPersistenceGate = new(1, 1);
    private readonly SemaphoreSlim _modelPersistenceGate = new(1, 1);
    private Task _modelPersistenceTask = Task.CompletedTask;
    private Task _probePersistenceTask = Task.CompletedTask;
    private Task _removalPersistenceTask = Task.CompletedTask;
    private Task _masterPersistenceTask = Task.CompletedTask;
    private Task _deviceFaultHardwareLockTask = Task.CompletedTask;
    private long _statisticsLoadGeneration;
    private Task _statisticsLoadTask = Task.CompletedTask;
    private readonly bool _requireStartupIoClear;
    private readonly object _historyStoreGate = new();
    private TestHistoryStore? _historyStore;
    private ProductionPersistenceService? _productionPersistence;
    private readonly LabelPrintService _labelPrintService = new();
    private readonly AppSoundService _sound = AppSoundService.Current;
    private readonly DiscardContactInterlock _discardInterlock = new();
    private readonly ThtModelParser _modelParser = new();
    private readonly object _initializationGate = new();
    private readonly object _cycleTokenGate = new();
    private readonly object _labelStateGate = new();
    private readonly ProductionUiVersionGate _productionUiVersionGate = new();
    private readonly SemaphoreSlim _manualRelayGate = new(1, 1);
    private readonly SemaphoreSlim _scanRecoveryGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _cycleCts;

    private Task? _initializationTask;

    private ProductModel? _model;
    private ILookup<int, PinRecord> _pinsByIoLookup = Array.Empty<PinRecord>().ToLookup(pin => pin.IoNumber);
    private string _state = "CHỜ CHỌN MÃ HÀNG";
    private string _lot = "0";
    private string _keysightResource;
    private WaterProofModelSettings _waterProofProfile = new();
    private WaterProofStage _waterProofStage = WaterProofStage.Idle;
    private string _waterProofStageText = "CHỜ KIỂM TRA";
    private string _waterProofOverallResult = "---";
    private int _waterProofRunning;
    private int _preContinuityWaterProofStarted;
    private int _preContinuityWaterProofPassed;
    private int _waterProofFailureFinalizing;
    private int _waterProofRetestConnectorState;
    private int _waterProofRetestAttempt;
    // Runtime-only baseline dùng để hiển thị độ rò realtime trong giai đoạn WAIT.
    // Không tham gia PASS/FAIL, history hoặc kết quả :RESULT cuối cùng.
    private readonly double?[] _waterProofLivePressBaseline = new double?[3];
    private string _hardwareStatus = "Bo: đang khởi tạo...";
    private string _boardConnectionMessage = "Chưa kết nối bo JBZ.";
    private string? _currentModelPath;

    private int _total;
    private int _pass;
    private int _fail;
    private bool _cycleActive;
    private bool _waitForProductRelease;
    private int _rearmAfterProductRemoval = 1;
    private int _productRemovalPending;
    private int _removalMonitoringFromMain;
    private bool _waitForFaultProductRemoval;
    private int _faultProductRemoved;
    private int _discardRequiredForFault;
    private int _discardContactClosed;
    private int _discardStandaloneLocked;
    private bool _waterProofEquipmentErrorAwaitingRemoval;
    private int _postContinuityStarted;
    private int _wiringFaultHandlingStarted;
    private Task? _hardwareInitializationTask;
    private Task? _hardwareMonitorTask;
    private int _selectedOperationTabIndex;
    private int _shutdownStarted;
    private bool _productDetectedThisCycle;
    private bool _presentationCycleStarted;
    private int _productStartSoundPlayed;
    private int _probeSessionActive;
    private int _topologyLearningActive;
    private int _runtimeMode = (int)RuntimeMode.Background;
    private int _productionPhase = (int)ProductionPhase.WaitingProduct;
    private long _runtimeGeneration;
    private int _enginePresentationWorkerRunning;
    private long _engineUiUpdateRevision;
    private long _engineUiLastCompletedRevision;
    private long _productionUiCycleEpoch;
    private long _inlineProbeUiRevision;
    private long _ioMappingUiRevision;
    private readonly object _probePreviewGate = new();
    private ProductionProbePreview? _pendingProductionProbePreview;
    private LiveTopologySnapshot _lastLiveTopologySnapshot = LiveTopologySnapshot.Empty();
    private int _probePresentationState = (int)ProbePresentationState.Inactive;
    private int _productionPresentationMode = (int)ProductionPresentationMode.Waiting;
    private int _productionRuntimeState = (int)ProductionRuntimeState.WaitingForProduct;
    private EngineUiUpdateRequest? _latestEngineUiUpdateRequest;
    // Separate presentation queue for pre-C0 continuity preview. It never runs
    // ProcessEngineChangedOnUi, so no PASS/FAIL/relay state machine can be entered.
    private int _continuityPreviewUiQueued;
    private long _continuityPreviewUiRevision;
    private long _continuityPreviewUiGeneration;
    private long _continuityPreviewUiCycleEpoch;
    private long _continuityPreviewUiSequence;
    private int _deviceFault;
    private int _boardUnavailablePresentationApplied;
    private int _manualModeActive;
    private int _hardwareReconfigurationActive;
    private int _deviceFaultDialogShown;
    private int _deviceFaultTransitionCount;
    private int _deviceFaultDialogCount;
    private int _manualActiveRelay;
    private int _firstFrameReceivedLogged;
    private int _firstLogicalStateLogged;
    private int _firstUiUpdateRenderedLogged;
    private long _lastObservedProductionFrameSequence;
    private long _lastObservedProductionScanGeneration;
    private long _cycleStartFrameSequence;
    private long _cycleStartScanGeneration;
    private int _freshFrameGateActive;
    // 0 = chờ frame sạch, 1 = đang chuyển trạng thái trên UI, 2 = đã mở khóa.
    // Frame không được đưa vào TestEngine trước khi đạt trạng thái 2.
    private int _startupIoInterlockState = 2;
    private string _startupIoWarningSignature = string.Empty;
    private int _stalePreCycleFrameLogged;
    private int _secondRequiredNetSeenLogged;
    private int _continuityPassedLogged;
    private long _productionFramesReceived;
    private long _productionFramesProcessed;
    private long _productionFramesDropped;
    private long _productionFramesRoutedToProbe;
    private long _engineUiUpdatesScheduled;
    private long _engineUiUpdatesRendered;
    private long _uiRequestCount;
    private long _uiDispatcherEnqueueCount;
    private long _uiRenderedCount;
    private long _lastContinuousScanMetricsTick;
    private string _lastPassGateSignature = string.Empty;
    private string _lastFaultGateSignature = string.Empty;
    private string _lastFaultGateSuppressedSignature = string.Empty;
    private string _lastPassRemainingSignature = string.Empty;
    private string _lastProductEvidenceSignature = string.Empty;
    private string _lastIoMappingSignature = string.Empty;
    // V12.10.3: TestEngine.Reset() phát Changed đồng bộ. Trong Master state machine,
    // reset nội bộ không được phép tái nhập OnEngineChanged trước khi state hoàn tất.
    private int _suppressEngineChanged;
    // Gate liên luồng: mỗi chu kỳ chỉ một caller được chốt side effects.
    private int _resultRecordedThisCycle;
    private int _probeCycleRecordedThisCycle;
    private DateTime _cycleStartedAt = DateTime.Now;
    private DateTime? _cycleTestStartedAt;
    private DateTime? _cycleRemovalStartedAt;
    private DateTime? _cycleContinuityCompletedAt;
    private DateTime? _cycleResistanceStartedAt;
    private DateTime? _cycleResistanceCompletedAt;
    private DateTime? _cycleWaterProofStartedAt;
    private DateTime? _cycleWaterProofCompletedAt;
    private string _cycleWaterProofSummary = string.Empty;
    private IReadOnlyList<WaterProofChannelMeasurement> _lastWaterProofMeasurements = [];
    private string _activeCycleId = Guid.NewGuid().ToString("N");
    private string _recordedHistoryCycleId = string.Empty;
    private TestHistoryStore? _recordedHistoryStore;
    private string _lastFaultRejectSignature = string.Empty;
    private long _dailyTestCount;
    private long _monthlyTestCount;
    private long _lifetimeTestCount;
    private long _probeCycleCount;
    private long _probeReplacementThreshold = PartCounterStore.DefaultReplacementThreshold;
    private string _deviceFaultMessage =
        "Mất kết nối với máy test. Vui lòng khởi động lại.";
    private LabelPrintContext? _failedLabelPrint;
    private LabelPrintContext? _lastSuccessfulLabelPrint;
    private string _labelStatusText = "TEM: CHỜ LẮP SẢN PHẨM";
    private int _cachedOpenCount;
    private int _cachedWrongCount;
    private int _cachedShortCount;
    private FaultDetail? _visiblePrimaryFaultSnapshot;
    private bool _visiblePrimaryFaultSnapshotValid;

    // V11.9: nhận dạng đầu dò GND ngay cả khi TestView đang mở. Firmware có
    // chữ ký fan-out dày (một source kéo theo hàng chục target liên tiếp).
    // Frame dạng này là thao tác dò pin, không phải chập mạch sản phẩm.
    // _inlineProbeContactIo chỉ là sentinel/primary để các interlock cũ đọc lock-free.
    // Danh sách thật được giữ riêng để V12.8 có thể hiển thị đồng thời 2 I/O.
    private int _inlineProbeContactIo;
    private readonly object _inlineProbeGate = new();
    private int[] _inlineProbeContactIos = Array.Empty<int>();
    private long _inlineProbeLastSeenUtcTicks;
    private readonly ProbeStateTracker _probeStateTracker = new(confirmFrames: 2, releaseFrames: 1, maxContacts: 64);
    private readonly ManualProbeSession _manualProbeSession = new(confirmFrames: 2, releaseFrames: 1);
    // V12.9.2: Probe UI tuyệt đối không dùng TTL/quarantine dài.
    // Timestamp chỉ còn phục vụ interlock relay chống rung cực ngắn sau RELEASE,
    // không được phép giữ ProbeContacts trên giao diện.
    private const int ProbeRelayReleaseDebounceMs = 40;

    // V12.9.5 - MASTER SAMPLE hoàn toàn tự động. Không còn manual command/checkbox.
    // Master NG chỉ mở khóa khi đủ N fault dây DUY NHẤT; cùng fault lặp nhiều frame
    // không được tăng bộ đếm. Master không bao giờ cộng LOT/Pass/Fail production.
    private MasterSequenceState _masterSequenceState = MasterSequenceState.WaitingGoodMaster;
    private bool _masterApproved;
    private bool _masterGoodVerified;
    private bool _masterBadVerified;
    private int _masterRequiredFaultCount = 2;
    private readonly HashSet<MasterFaultKey> _masterDetectedFaultKeys = [];
    // V12.10.1: cùng key dùng để dựng snapshot FaultGrid MasterBad. DataGrid chỉ
    // hiển thị một dòng cho mỗi fault unique, không lặp theo số frame scan.
    private readonly Dictionary<MasterFaultKey, FaultDetail> _masterDetectedFaultDetails = [];
    private bool _masterFaultCollectionLocked;
    private int _masterPostStarted;
    private int _masterEjectStarted;
    private int _legacyGoodMasterRecorded;
    private int _legacyBadMasterRecorded;
    private long _masterBadCollectNotBeforeUtcTicks;
    private DateTime? _masterInstallStartedAt;
    private DateTime? _masterTestStartedAt;
    private DateTime? _masterRemovalStartedAt;
    private string _masterHistoryCycleId = string.Empty;
    private string _masterHistoryInspectionType = string.Empty;
    private TestHistoryStore? _masterRecordedHistoryStore;
    private const int MasterBadSettleMs = 120;
    private string _masterStatus = "KIỂM TRA MASTER PASS";

    // Mỗi lần người vận hành chọn model mới sẽ tăng generation. Tác vụ
    // auto-load model gần nhất lúc startup chỉ được áp dụng nếu generation
    // vẫn không đổi. Nhờ vậy model cũ không thể hoàn thành muộn rồi ghi đè
    // model mới, vốn là nguyên nhân bảng TestView xuất hiện chậm/đổi model.
    private int _modelLoadGeneration;

    public FaultRowCollection Faults { get; } = new();

    /// <summary>Danh sách fault duy nhất đã xác nhận trên MASTER NG.</summary>
    public ObservableCollection<MasterFaultDisplayRow> MasterFaults { get; } = new();

    // V12.8: đầu dò chạy SONG SONG với bảng cấu hình production. ProbeContacts
    // chỉ cấp dữ liệu cho thanh trạng thái đầu dò, không bao giờ thay thế Faults.
    public ObservableCollection<FaultRow> ProbeContacts { get; } = new();

    // Card 64-I/O được sinh động từ BoardCapacity. Probe chỉ đổi
    // HasProbeActivity; card vẫn ACTIVE khi nhấc que.
    public ObservableCollection<BoardCardState> Cards { get; } = new();
    // Alias tương thích mã cũ; collection này chứa cả card bật và card tắt.
    public ObservableCollection<BoardCardState> ActiveCards => Cards;
    public BoardCapacity BoardCapacity => _board.Capacity;
    public string BoardCapacityText =>
        $"{BoardCapacity.ExpansionCardCount} CARD " +
        $"({BoardCapacity.StartCardNumber}-{BoardCapacity.ScanCardCount}) / " +
        $"{BoardCapacity.TotalIoCapacity} I/O";

    public bool HasInlineProbeContacts => ProbeContacts.Count > 0;
    public string ProbeModeText => HasInlineProbeContacts
        ? $"ĐANG DÒ ({SnapshotInlineProbeContacts().Length})"
        : "LẮP SẢN PHẨM";
    public string ProbeBarText => ProbeContacts.FirstOrDefault()?.Status
        ?? "LẮP SẢN PHẨM - BO TỰ PHÁT HIỆN ĐẦU DÒ TRONG CHU KỲ KIỂM TRA";
    public string ProbeBarBackground => HasInlineProbeContacts ? "#23D9D9" : "#F8F8F6";
    public ObservableCollection<ResistanceResult> Resistance { get; } = new();
    public ObservableCollection<ResistanceResult> ResistanceDisplayRows { get; } = new();
    public ObservableCollection<WaterProofChannelResult> WaterProofChannels { get; } = new();
    public bool IsWaterProofCardVisible => _model is not null && _waterProofProfile.Enabled;
    public string WaterProofStageText => _waterProofStageText;
    public string WaterProofOverallResult => _waterProofOverallResult;
    public string WaterProofLeakLimitText => _waterProofProfile.LeakLimit.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    public string WaterProofPortText => string.IsNullOrWhiteSpace(_productionSettings.WaterProofMachine.PortName)
        ? "CHƯA CẤU HÌNH COM"
        : _productionSettings.WaterProofMachine.PortName;
    public string WaterProofCardBackground => _waterProofStage switch
    {
        WaterProofStage.Passed => "#E8F6EC",
        WaterProofStage.Failed or WaterProofStage.Error => "#FDECEC",
        WaterProofStage.Pressurizing or WaterProofStage.Waiting or WaterProofStage.Evaluating => "#FFF6C9",
        _ => "#FFFFFF"
    };
    public string WaterProofAccentBrush => _waterProofStage switch
    {
        WaterProofStage.Passed => "#26A653",
        WaterProofStage.Failed or WaterProofStage.Error => "#D32F2F",
        WaterProofStage.Pressurizing or WaterProofStage.Waiting or WaterProofStage.Evaluating => "#F2B705",
        _ => "#2B9C76"
    };

    public bool IsDeviceFault => Volatile.Read(ref _deviceFault) != 0;
    public bool IsManualModeActive => Volatile.Read(ref _manualModeActive) != 0;
    public bool CanEnterManualMode => !IsDeviceFault && !IsManualForbiddenWorkActive;
    public string DeviceFaultMessage => _deviceFaultMessage;
    public int DeviceFaultTransitionCount => Volatile.Read(ref _deviceFaultTransitionCount);
    public int DeviceFaultDialogCount => Volatile.Read(ref _deviceFaultDialogCount);
    public long ProductionFramesReceived => Interlocked.Read(ref _productionFramesReceived);
    public long ProductionFramesProcessed => Interlocked.Read(ref _productionFramesProcessed);
    public long ProductionFramesDropped => Interlocked.Read(ref _productionFramesDropped);
    public long ProductionFramesRoutedToProbe => Interlocked.Read(ref _productionFramesRoutedToProbe);
    public long EngineUiUpdatesScheduled => Interlocked.Read(ref _engineUiUpdatesScheduled);
    public long EngineUiUpdatesRendered => Interlocked.Read(ref _engineUiUpdatesRendered);
    public long EngineUiRequestCount => Interlocked.Read(ref _uiRequestCount);
    public long EngineUiDispatcherEnqueueCount =>
        Interlocked.Read(ref _uiDispatcherEnqueueCount);
    public long EngineUiRenderedCount => Interlocked.Read(ref _uiRenderedCount);
    public ProductionRuntimeState CurrentProductionRuntimeState =>
        (ProductionRuntimeState)Volatile.Read(ref _productionRuntimeState);

    /// <summary>
    /// Phát trực tiếp frame scan đã được transport map về I/O toàn cục.
    /// PinProbe dùng event này thay vì cố phân tích chuỗi log.
    /// </summary>
    public event EventHandler<ScanFrame>? ScanFrameReceived;

    /// <summary>
    /// Báo hoạt động frame thật cho lớp hiển thị trạng thái; không tham gia xử lý kết quả test.
    /// </summary>
    public event EventHandler<ScanFrame>? BoardFrameActivity;

    public string State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(StateBackground));
                Raise(nameof(StateForeground));
                Raise(nameof(ResultStatusText));
                RaiseCenterPresentation();
                RaiseActiveFault();
            }
        }
    }

    // HTDRV_TESTWINDOW_FINAL_2026-09-05
    // HTDRV_WAIT_PRODUCT_2026-09-05: latch này chỉ nhận activity từ snapshot
    // Production đã qua TestEngine; Probe không đi vào ProcessFrame nên không
    // thể làm hiện bảng hoặc xóa lời nhắc lắp sản phẩm.
    // HTDRV_CENTER_RESULT_2026-09-05
    public string CenterResultText => IsFinalPassPresentation
        ? "PASS"
        : IsDiscardFaultConfirmationPresentation
            ? "ĐƯA HÀNG VÀO THÙNG HÀNG LỖI"
            : IsWaitingProductPresentation
                ? "LẮP SẢN PHẨM"
                : string.Empty;

    public bool IsCenterResultVisible =>
        IsFinalPassPresentation ||
        IsDiscardFaultConfirmationPresentation ||
        IsWaitingProductPresentation;

    public bool IsCenterPassPresentation => IsFinalPassPresentation;

    // Sau khi FAIL đã xác nhận và model có _DISCARD, vùng chữ lớn giữa màn hình
    // dành riêng cho hướng dẫn đưa hàng NG qua cảm biến. Ô trạng thái nhỏ vẫn
    // hiển thị CHỜ XÁC NHẬN THÙNG LỖI qua ResultStatusText.
    private bool IsDiscardFaultConfirmationPresentation =>
        !IsDeviceFault &&
        _waitForFaultProductRemoval &&
        Volatile.Read(ref _discardRequiredForFault) != 0;

    private bool IsFinalPassPresentation =>
        !IsDeviceFault &&
        State.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) &&
        CurrentProductionPhase == ProductionPhase.Completed &&
        !IsProductRemovalPending;

    private bool IsWaitingProductPresentation =>
        !IsDeviceFault &&
        CurrentProductionRuntimeState == ProductionRuntimeState.WaitingForProduct &&
        CurrentProductionPresentationMode == ProductionPresentationMode.Waiting &&
        CurrentProbePresentationState is ProbePresentationState.Inactive or ProbePresentationState.Released &&
        !IsProbeOwningProductionPresentation() &&
        !IsProductRemovalPending &&
        CurrentProductionPhase is ProductionPhase.WaitingProduct or ProductionPhase.Continuity;

    private void RaiseCenterPresentation()
    {
        Raise(nameof(CenterResultText));
        Raise(nameof(IsCenterResultVisible));
        Raise(nameof(IsCenterPassPresentation));
    }

    private void ResetProductPresentationCycle()
    {
        _presentationCycleStarted = false;
        RaiseCenterPresentation();
    }

    private bool IsProbeOwningProductionPresentation()
    {
        ProbePresentationState probeState = CurrentProbePresentationState;
        if (probeState is ProbePresentationState.Candidate or ProbePresentationState.Touch ||
            _probeStateTracker.HasTrackedContacts ||
            Volatile.Read(ref _inlineProbeContactIo) != 0)
        {
            return true;
        }

        lock (_probePreviewGate)
            return _pendingProductionProbePreview is not null;
    }

    public string ResultStatusText
    {
        get
        {
            if (IsDeviceFault)
                return "LỖI THIẾT BỊ";

            string value = State ?? string.Empty;

            // After PASS the product-removal latch owns the operator message.
            // Keep PASS out of the status box while the center result has
            // already been shown once; this prevents repeated PASS display
            // until the product is fully removed and the cycle is re-armed.
            if (IsProductRemovalPending ||
                CurrentProductionPhase == ProductionPhase.WaitingProductRemoval)
            {
                // FAIL có cấu hình _DISCARD: ô nhỏ chỉ báo trạng thái chờ xác nhận.
                // Hướng dẫn thao tác lớn được hiển thị ở CenterResultText.
                if (_waitForFaultProductRemoval &&
                    Volatile.Read(ref _discardRequiredForFault) != 0)
                {
                    return "CHỜ XÁC NHẬN THÙNG LỖI";
                }

                return value.Contains("VUI LÒNG", StringComparison.OrdinalIgnoreCase)
                    ? "VUI LÒNG THÁO SẢN PHẨM"
                    : "THÁO SẢN PHẨM";
            }

            if (IsManualModeActive || value.Equals("MANUAL", StringComparison.OrdinalIgnoreCase))
                return "MANUAL";

            if (!value.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) &&
                value.Contains("VUI LÒNG THÁO SẢN PHẨM", StringComparison.OrdinalIgnoreCase))
                return "VUI LÒNG THÁO SẢN PHẨM";

            if (value.Contains("THÁO SẢN PHẨM", StringComparison.OrdinalIgnoreCase))
                return "THÁO SẢN PHẨM";

            if (value.Contains("ĐỒNG BỘ DỮ LIỆU BO", StringComparison.OrdinalIgnoreCase))
                return "ĐỒNG BỘ BO";

            if (value.Contains("CHƯA KẾT NỐI", StringComparison.OrdinalIgnoreCase))
                return "CHƯA KẾT NỐI BO";

            if (value.Contains("KẾT NỐI BO", StringComparison.OrdinalIgnoreCase))
                return "ĐANG KẾT NỐI BO";

            if (value.StartsWith("PASS", StringComparison.OrdinalIgnoreCase))
                return "PASS";

            if (IsMasterSequenceActive)
            {
                // Ô trạng thái lớn phải luôn cho người vận hành biết chính xác
                // loại mẫu Master cần kiểm tra. Trạng thái THÁO SẢN PHẨM ở trên
                // vẫn có ưu tiên cao hơn để không che mất hướng dẫn an toàn.
                return IsMasterBadPhase
                    ? "KIỂM TRA MASTER LỖI"
                    : "KIỂM TRA MASTER ĐẠT";
            }

            if (value.Contains("ĐANG TEST LEAK", StringComparison.OrdinalIgnoreCase))
                return "ĐANG TEST LEAK";

            if (value.Contains("CHƯA ĐẠT", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("KHÔNG ĐẠT", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("LỖI", StringComparison.OrdinalIgnoreCase))
                return "KHÔNG ĐẠT";

            if (value.Contains("CHỜ THÁO", StringComparison.OrdinalIgnoreCase))
                return "CHỜ THÁO";

            if (value.Contains("ĐANG", StringComparison.OrdinalIgnoreCase))
                return "ĐANG TEST";

            if (!_presentationCycleStarted &&
                !IsProductRemovalPending &&
                CurrentProductionPhase is ProductionPhase.WaitingProduct or ProductionPhase.Continuity &&
                !_engine.HasProductActivity)
                return "LẮP SẢN PHẨM";

            return "LẮP SẢN PHẨM";
        }
    }

    /// <summary>
    /// Màu trạng thái lớn giống máy production: PASS phải xanh lá; lỗi đỏ;
    /// chờ/đang kiểm tra dùng nền vàng dễ quan sát từ xa.
    /// </summary>
    public string StateBackground
    {
        get
        {
            if (IsDeviceFault)
                return "#C62828";

            string value = State ?? string.Empty;

            if (IsManualModeActive || value.Equals("MANUAL", StringComparison.OrdinalIgnoreCase))
                return "#FFF3A0";

            if (!value.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) &&
                value.Contains("VUI LÒNG THÁO SẢN PHẨM", StringComparison.OrdinalIgnoreCase))
                return "#E65100";

            if (value.Contains("CHƯA KẾT NỐI", StringComparison.OrdinalIgnoreCase))
                return "#C62828";

            if (IsMasterSequenceActive)
            {
                if (value.Contains("LỖI THIẾT BỊ", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("FAIL", StringComparison.OrdinalIgnoreCase))
                    return "#C62828";

                return "#FFF3A0";
            }

            if (MasterApproved && value.Contains("CHỜ LẮP SẢN PHẨM", StringComparison.OrdinalIgnoreCase))
                return "#FFF3A0";

            if (value.StartsWith("PASS", StringComparison.OrdinalIgnoreCase))
                return "#2AA84A";

            if (value.Contains("LỖI", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("CHƯA ĐẠT", StringComparison.OrdinalIgnoreCase))
                return "#C62828";

            if (value.Contains("ĐANG KIỂM TRA", StringComparison.OrdinalIgnoreCase))
                return "#FFF3A0";

            if (value.Contains("LẮP SẢN PHẨM", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("CHỜ", StringComparison.OrdinalIgnoreCase))
                return "#FFF3A0";

            return "#FFF3A0";
        }
    }

    public string StateForeground => StateBackground.Equals("#FFF3A0", StringComparison.OrdinalIgnoreCase)
        ? "#222222"
        : "#FFFFFF";

    public string Lot
    {
        get => _lot;
        private set => Set(ref _lot, value);
    }

    public string KeysightResource
    {
        get => _keysightResource;
        set => Set(ref _keysightResource, value);
    }

    public string HardwareStatus
    {
        get => _hardwareStatus;
        set => Set(ref _hardwareStatus, value);
    }

    public bool IsBoardConnected => _board.IsConnected;

    public string BoardConnectionMessage
    {
        get => _boardConnectionMessage;
        private set
        {
            if (Set(ref _boardConnectionMessage, value))
            {
                Raise(nameof(HasBoardConnectionError));
            }
        }
    }

    /// <summary>
    /// Dùng để tự động ẩn/hiện thanh cảnh báo lỗi kết nối trong TestWindow.
    /// </summary>
    public bool HasBoardConnectionError =>
        !string.IsNullOrWhiteSpace(BoardConnectionMessage);

    public string? CurrentModelPath
    {
        get => _currentModelPath;
        private set => Set(ref _currentModelPath, value);
    }

    public int Total
    {
        get => _total;
        set
        {
            if (Set(ref _total, value))
            {
                Raise(nameof(Rate));
            }
        }
    }

    public int Pass
    {
        get => _pass;
        set
        {
            if (Set(ref _pass, value))
            {
                Raise(nameof(Rate));
            }
        }
    }

    public int Fail
    {
        get => _fail;
        set
        {
            if (Set(ref _fail, value))
            {
                Raise(nameof(Rate));
            }
        }
    }

    // Htdrv gốc hiển thị/đếm theo từng dòng pin map đang còn trên bảng.
    // Chưa nối là trạng thái hiển thị thao tác, không phải OPEN fault/FAIL.
    public int OpenCount => _cachedOpenCount;

    public int WrongCount => _cachedWrongCount;

    public string WrongCountText => IsMasterBadPhase
        ? $"{MasterDetectedFaultCount}/{MasterRequiredFaultCount}"
        : WrongCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public int ShortCount => _cachedShortCount;

    public int WiringFaultCount => WrongCount + ShortCount;

    public int PassedNetworkCount =>
        _engine.PassedNets.Count;

    public int ExpectedNetworkCount =>
        _engine.ExpectedNetCount;

    public string NetworkProgress => IsMasterBadPhase
        ? $"{MasterDetectedFaultCount}/{MasterRequiredFaultCount}"
        : $"{PassedNetworkCount}/{ExpectedNetworkCount}";

    public string ActiveFaultTitle
    {
        get
        {
            if (IsMasterSequenceActive)
                return NormalizeSingleLine(State);

            if (State.Equals("PASS", StringComparison.OrdinalIgnoreCase))
                return "PASS";

            FaultDetail? fault = GetVisiblePrimaryFault();
            return fault is null
                ? State
                : FaultDisplayFormatter.OperatorInstruction(fault.Type);
        }
    }

    private static string NormalizeSingleLine(string? value) =>
        (value ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " • ", StringComparison.Ordinal)
            .Trim();

    public string ActiveFaultMessage
    {
        get
        {
            if (IsMasterSequenceActive)
                return MasterStatus;

            FaultDetail? fault = GetVisiblePrimaryFault();
            if (fault is null)
                return string.Empty;

            if (fault.Type == ProductFaultType.OpenCircuit)
                return string.IsNullOrWhiteSpace(fault.WireName)
                    ? fault.Message
                    : $"Dây {fault.WireName} chưa kết nối";

            if (fault.Type == ProductFaultType.ResistanceOutOfRange)
                return fault.Message;

            return fault.Message;
        }
    }

    public string ActiveFaultExpectedText
    {
        get
        {
            FaultDetail? fault = GetVisiblePrimaryFault();
            if (fault?.ExpectedSourceIo is not int source || fault.ExpectedTargetIo is not int target)
                return string.Empty;
            return $"Tiêu chuẩn: {DescribeIoCompact(source)}  →  {DescribeIoCompact(target)}";
        }
    }

    public string ActiveFaultActualText
    {
        get
        {
            FaultDetail? fault = GetVisiblePrimaryFault();
            if (fault is null)
                return string.Empty;

            if (fault.ActualSourceIo is int source && fault.ActualTargetIo is int target)
                return $"Thực tế: {DescribeIoCompact(source)}  →  {DescribeIoCompact(target)}";

            if (fault.Type == ProductFaultType.ShortCircuit && fault.RelatedIos.Length > 1)
                return "Thực tế: " + string.Join("  ↔  ", fault.RelatedIos.Select(DescribeIoCompact));

            return string.Empty;
        }
    }

    public string ActiveFaultBackground
    {
        get
        {
            if (IsMasterSequenceActive)
                return StateBackground;

            FaultDetail? fault = GetVisiblePrimaryFault();
            if (State.Equals("PASS", StringComparison.OrdinalIgnoreCase))
                return "#58D36B";
            return fault?.Type switch
            {
                ProductFaultType.ShortCircuit => "#D32F2F",
                ProductFaultType.WrongWiring => "#E53935",
                ProductFaultType.OpenCircuit => "#F28C28",
                ProductFaultType.ResistanceOutOfRange => "#E53935",
                ProductFaultType.WaterProofLeak => "#E53935",
                _ => StateBackground
            };
        }
    }

    public string ActiveFaultForeground =>
        IsMasterSequenceActive || GetVisiblePrimaryFault() is null || State.Equals("PASS", StringComparison.OrdinalIgnoreCase)
            ? StateForeground
            : "#FFFFFF";

    public string ModelName =>
        _model?.ModelName ?? string.Empty;

    public IReadOnlyList<string> CurrentConnectorIds => _model?.Connectors
        .Select(connector => connector.ConnectorId)
        .Where(connector => !string.IsNullOrWhiteSpace(connector))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];

    public string PartNumber =>
        _model?.PartNumber ?? string.Empty;

    public string ProductName =>
        _model?.ProductName ?? string.Empty;

    public string VehicleType =>
        _model?.VehicleType ?? string.Empty;

    public string CustomerCode =>
        _model?.CustomerCode ?? string.Empty;

    public string Eco => _model?.Eco ?? string.Empty;
    public string Nco => _model?.Nco ?? string.Empty;
    public string Alc => _model?.Alc ?? string.Empty;

    public int ItemHeight => Math.Clamp(_productionSettings.ItemHeight, 30, 44);
    public int ScrollDelay => Math.Clamp(_productionSettings.ScrollDelay, 0, 5000);
    public int PageDelay => Math.Clamp(_productionSettings.PageDelay, 0, 5000);
    public bool ShowTitle => _productionSettings.ShowTitle;
    public bool ShowConnector => _productionSettings.ShowConnector;

    public PinRecord? FindPinByIo(int io) =>
        _pinsByIoLookup[io].FirstOrDefault();

    /// <summary>
    /// V12.9: lookup 1 -> N, không làm mất duplicate mapping khi một Global IO
    /// xuất hiện ở nhiều connector/pin trong THT.
    /// </summary>
    public IReadOnlyList<PinRecord> FindPinsByIo(int io) =>
        _pinsByIoLookup[io].ToArray();

    /// <summary>
    /// Trả các I/O cùng một tên dây/network với chân đang dò. TestPin dùng
    /// thông tin này để hiển thị đúng kiểu máy gốc: IO hiện tại + tên dây +
    /// màu + chân đó đang ghép với I/O nào trong file THT.
    /// </summary>
    public IReadOnlyList<int> FindRelatedIo(int io, string? wireName)
    {
        ProductModel? model = _model;
        if (model is null)
            return Array.Empty<int>();

        // V12.4: CLIP phải tra theo topology, không phụ thuộc WireName vì các
        // row AO/aN trong THT thực tế có thể để trống tên dây.
        if (model.Clip is not null)
        {
            IEnumerable<ClipBranch> clipBranches = model.Clip.Branches.Where(branch =>
                io == model.Clip.CommonIo ||
                io == branch.TargetIo);

            int[] clipRelated = clipBranches
                .SelectMany(branch => new[]
                {
                    model.Clip.CommonIo,
                    branch.TargetIo
                })
                .Where(candidate => candidate != io)
                .Distinct()
                .OrderBy(candidate => candidate)
                .ToArray();

            if (clipRelated.Length > 0)
                return clipRelated;
        }

        if (string.IsNullOrWhiteSpace(wireName))
            return Array.Empty<int>();

        string name = wireName.Trim();

        WireNet? net = model.Nets.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase) ||
            candidate.Pins.Any(pin =>
                string.Equals(pin.WireName?.Trim(), name, StringComparison.OrdinalIgnoreCase)));

        IEnumerable<int> related = net is not null
            ? net.IoNumbers
            : model.Pins
                .Where(pin =>
                    string.Equals(pin.WireName?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                .Select(pin => pin.IoNumber);

        return related
            .Where(candidate => candidate != io)
            .Distinct()
            .OrderBy(candidate => candidate)
            .ToArray();
    }

    public double Rate =>
        Total == 0 ? 0 : 100.0 * Pass / Total;

    public long DailyTestCount
    {
        get => _dailyTestCount;
        private set => Set(ref _dailyTestCount, value);
    }

    public long MonthlyTestCount
    {
        get => _monthlyTestCount;
        private set => Set(ref _monthlyTestCount, value);
    }

    public long LifetimeTestCount
    {
        get => _lifetimeTestCount;
        private set => Set(ref _lifetimeTestCount, value);
    }

    public long ProbeCycleCount
    {
        get => _probeCycleCount;
        private set
        {
            if (Set(ref _probeCycleCount, value))
            {
                Raise(nameof(ProbeCycleText));
                Raise(nameof(ProbeMaintenanceDue));
                Raise(nameof(ProbeMaintenanceStatus));
                Raise(nameof(ProbeMaintenanceBackground));
            }
        }
    }

    public long ProbeReplacementThreshold => _probeReplacementThreshold;

    private static string FormatProbeCycleNumber(long value) =>
        value.ToString(
            "N0",
            System.Globalization.CultureInfo.GetCultureInfo("vi-VN"));

    public string ProbeCycleText =>
        $"{FormatProbeCycleNumber(ProbeCycleCount)}/{FormatProbeCycleNumber(ProbeReplacementThreshold)}";
    public bool ProbeMaintenanceDue => ProbeCycleCount >= ProbeReplacementThreshold;
    public string ProbeMaintenanceStatus => ProbeMaintenanceDue
        ? "ĐẾN CHU KỲ THAY PROBE PIN"
        : "PROBE PIN ĐANG TRONG CHU KỲ SỬ DỤNG";
    public string ProbeMaintenanceBackground => ProbeMaintenanceDue ? "#C62828" : "#16734B";

    public MasterSequenceState MasterState
    {
        get => _masterSequenceState;
        private set
        {
            if (Set(ref _masterSequenceState, value))
            {
                Raise(nameof(IsMasterSequenceActive));
                Raise(nameof(IsMasterBadPhase));
                Raise(nameof(ProductionEnabled));
                Raise(nameof(ResultStatusText));
                Raise(nameof(WrongCountText));
                Raise(nameof(NetworkProgress));
                RaiseActiveFault();
            }
        }
    }

    public bool MasterApproved
    {
        get => _masterApproved;
        private set
        {
            if (Set(ref _masterApproved, value))
            {
                Raise(nameof(IsMasterSequenceActive));
                Raise(nameof(ProductionEnabled));
                Raise(nameof(ResultStatusText));
                Raise(nameof(WrongCountText));
                Raise(nameof(NetworkProgress));
                RaiseActiveFault();
            }
        }
    }

    public bool ProductionEnabled => MasterApproved;
    public bool IsIoMappingMode => _model is not null && _engine.ExpectedNetCount == 0;
    public ProbePresentationState CurrentProbePresentationState =>
        (ProbePresentationState)Volatile.Read(ref _probePresentationState);
    public ProductionPresentationMode CurrentProductionPresentationMode =>
        (ProductionPresentationMode)Volatile.Read(ref _productionPresentationMode);
    public bool IsMasterSequenceActive => _model is not null && !MasterApproved;
    public bool IsMasterBadPhase => MasterState is
        MasterSequenceState.WaitingBadMaster or
        MasterSequenceState.TestingBadMaster or
        MasterSequenceState.EjectingBadMaster;

    public int MasterRequiredFaultCount => _masterRequiredFaultCount;
    public int MasterDetectedFaultCount => _masterDetectedFaultKeys.Count;

    public string MasterStatus
    {
        get => _masterStatus;
        private set
        {
            if (Set(ref _masterStatus, value))
            {
                RaiseActiveFault();
            }
        }
    }

    public int SelectedOperationTabIndex
    {
        get => _selectedOperationTabIndex;
        set => Set(ref _selectedOperationTabIndex, value);
    }

    public AsyncRelayCommand ConnectKeysightCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand MeasureCommand { get; }
    public AsyncRelayCommand CompleteCommand { get; }
    public AsyncRelayCommand Relay1Command { get; }
    public AsyncRelayCommand Relay2Command { get; }
    public AsyncRelayCommand RelaysOffCommand { get; }
    public AsyncRelayCommand RetryLabelCommand { get; }
    public AsyncRelayCommand ReprintLabelCommand { get; }

    public string LabelStatusText
    {
        get => _labelStatusText;
        private set => Set(ref _labelStatusText, value);
    }

    public bool IsLabelPrinterConnected => _labelPrintService.IsConnected;

    public string LabelPrinterConnectedPort => _labelPrintService.ConnectedPort;

    public bool CanRetryLabel
    {
        get
        {
            lock (_labelStateGate)
                return _failedLabelPrint is not null;
        }
    }

    public bool CanReprintLabel
    {
        get
        {
            lock (_labelStateGate)
                return _lastSuccessfulLabelPrint is not null;
        }
    }

    public TestViewModel(
        MainViewModel main,
        TestEngine engine,
        IBoardTransport board,
        KeysightVisaService visa,
        WaterProofSerialService waterProof,
        AppSettings settings,
        ProductionSettings productionSettings,
        LegacyPhtHistoryService? legacyHistory = null,
        bool requireStartupIoClear = true)
    {
        _main = main;
        _engine = engine;
        _board = board;
        _scanSupervisor = new ScanSupervisor(_board, AddLog);
        _visa = visa;
        _waterProof = waterProof;
        _settings = settings;
        _productionSettings = productionSettings;

        // ALWAYS_PROBE_2026-09-05:
        // Test pointer không còn là tùy chọn vận hành. Que dò luôn hoạt động.
        // Giữ property legacy = true để các service/code cũ còn đọc UseTestPointer
        // cũng nhận đúng hành vi, nhưng UI không còn cho bật/tắt nữa.
        _productionSettings.UseTestPointer = true;

        _legacyHistory = legacyHistory ?? new LegacyPhtHistoryService();
        _requireStartupIoClear = requireStartupIoClear;
        _lotSequence = new LotSequenceService(_productionSettings);
        UpdateDailyLotDisplay();
        // App sở hữu lifecycle của AppSoundService. Không initialize audio trong
        // constructor ViewModel vì constructor chạy trước frame render đầu tiên.
        _keysightResource =
            settings.Keysight.Resource ?? string.Empty;

        _engine.Changed += OnEngineChanged;
        _board.Log += OnBoardLog;
        _board.FrameReceived += OnBoardFrameReceived;
        _board.ProductionProbePreviewReceived += OnProductionProbePreviewReceived;
        _waterProof.Log += OnWaterProofLog;

        // FileSystemWatcher được tạo sau first-render trong InitializeCoreAsync,
        // không làm nặng constructor của MainWindow/TestViewModel.
        RebuildActiveCards();

        ConnectKeysightCommand =
            new AsyncRelayCommand(ConnectKeysightAsync);

        StopCommand =
            new AsyncRelayCommand(StopTestAsync);

        MeasureCommand =
            new AsyncRelayCommand(MeasureResistanceAsync);

        CompleteCommand =
            new AsyncRelayCommand(CompleteTestAsync);

        Relay1Command =
            new AsyncRelayCommand(async () =>
            {
                if (!EnsureManualBoardReady("thử Relay 1", requireD2xxRelay: true))
                    return;

                int relay1Ms = _productionSettings.Relay1JigPulseMs;
                AddLog($"THỬ RELAY 1 vật lý: pulse 1 lần ({relay1Ms} ms)");
                await _engine.PulsePhysicalRelayAsync(1);
                AddLog("Relay 1 OFF - đã cưỡng bức về trạng thái chờ.");
            });

        Relay2Command =
            new AsyncRelayCommand(async () =>
            {
                if (!EnsureManualBoardReady("thử Relay 2", requireD2xxRelay: true))
                    return;

                int relay2Ms = _productionSettings.Relay2MarkingPulseMs;
                AddLog($"THỬ RELAY 2 vật lý: pulse 1 lần ({relay2Ms} ms)");
                await _engine.PulsePhysicalRelayAsync(2);
                AddLog("Relay 2 OFF - đã cưỡng bức về trạng thái chờ.");
            });

        RelaysOffCommand =
            new AsyncRelayCommand(async () =>
            {
                if (!EnsureManualBoardReady("tắt relay", requireD2xxRelay: true))
                    return;

                await _board.AllRelaysOffAsync();
                AddLog("Tất cả relay OFF");
            });

        RetryLabelCommand = new AsyncRelayCommand(RetryLastFailedLabelAsync, () => CanRetryLabel);
        ReprintLabelCommand = new AsyncRelayCommand(ReprintLastSuccessfulLabelAsync, () => CanReprintLabel);

    }

    private bool EnsureManualBoardReady(string action, bool requireD2xxRelay = false)
    {
        if (!_board.IsConnected)
        {
            EnterDeviceFault(
                new InvalidOperationException($"Không thể {action}: bo kiểm tra không còn kết nối."),
                $"Manual:{action}");
            return false;
        }

        return true;
    }

    public async Task EnterManualModeAsync()
    {
        if (!CanEnterManualMode)
            throw new InvalidOperationException(
                "Không thể bật Manual khi đang kiểm tra. Hãy kết thúc chu kỳ trước.");

        Interlocked.Exchange(ref _manualModeActive, 1);
        CancelCycleOperations();
        ResetManualProbeSession("enter-manual-mode");
        SwitchRuntimeMode(RuntimeMode.Background);
        Interlocked.Exchange(ref _probeSessionActive, 0);
        Interlocked.Exchange(ref _postContinuityStarted, 0);
        Interlocked.Exchange(ref _wiringFaultHandlingStarted, 0);
        Interlocked.Exchange(ref _masterPostStarted, 0);
        Interlocked.Exchange(ref _masterEjectStarted, 0);
        _cycleActive = false;
        _waitForProductRelease = false;
        _waitForFaultProductRemoval = false;
        _productDetectedThisCycle = false;
        _sound.SetWiringFaultAlarm(false);
        _sound.StopAll();
        _engine.SetFrameProcessingEnabled(false);

        if (_board.IsConnected)
        {
            if (_board.IsScanning)
                await StopScanIntentionallyAsync("ManualMode");
            await _board.AllRelaysOffAsync();
        }

        Volatile.Write(ref _manualActiveRelay, 0);
        State = "MANUAL";
        Raise(nameof(IsManualModeActive));
        Raise(nameof(CanEnterManualMode));
        AddLog("MANUAL TỰ ĐỘNG ON - thao tác relay tay, tất cả relay đã OFF an toàn.");
    }

    public bool IsProductRemovalPending =>
        Volatile.Read(ref _productRemovalPending) != 0;

    private void SetProductRemovalPending(bool pending)
    {
        if (pending && Volatile.Read(ref _resultRecordedThisCycle) != 0)
            MarkProductRemovalStarted();

        int next = pending ? 1 : 0;
        if (Interlocked.Exchange(ref _productRemovalPending, next) != next)
            Raise(nameof(IsProductRemovalPending));
    }

    public async Task ExitManualModeAsync(bool outputsAlreadyOff = false)
    {
        await _manualRelayGate.WaitAsync();
        try
        {
            if (_board.IsConnected && !outputsAlreadyOff)
                await _board.AllRelaysOffAsync();
            Volatile.Write(ref _manualActiveRelay, 0);
            Interlocked.Exchange(ref _manualModeActive, 0);
        }
        finally
        {
            _manualRelayGate.Release();
        }

        Raise(nameof(IsManualModeActive));
        Raise(nameof(CanEnterManualMode));
        State = ReadyStateForCurrentModel();
        AddLog("MANUAL TỰ ĐỘNG OFF - relay OFF, quét Production tiếp tục.");

        if (_board.IsConnected)
            await EnsureContinuousProductionScanAsync();
    }

    /// <summary>
    /// Manual relay tương thích JBZ I/O Monitor V1.9: BẬT giữ đúng một relay,
    /// TẮT cưỡng bức cả hai relay OFF rồi mới khôi phục Production scan.
    /// </summary>
    public async Task<int> SetManualRelayAsync(int relay, bool turnOn)
    {
        if (IsDeviceFault)
            throw new InvalidOperationException("DeviceFault đang khóa lệnh Manual. Hãy thoát và mở lại ứng dụng.");
        if (relay is not 1 and not 2)
            throw new ArgumentOutOfRangeException(nameof(relay));
        if (!EnsureManualBoardReady($"manual Relay {relay}", requireD2xxRelay: true))
            return Volatile.Read(ref _manualActiveRelay);
        if (!IsManualModeActive)
            await EnterManualModeAsync();

        long started = Stopwatch.GetTimestamp();
        AsyncFileLogService.Current.Performance(
            $"MANUAL_RELAY_LATENCY relay={relay} action={(turnOn ? "ON" : "OFF")} event=button_click");

        int activeRelay;
        await _manualRelayGate.WaitAsync();
        try
        {
            AsyncFileLogService.Current.Performance(
                $"MANUAL_RELAY_LATENCY relay={relay} action={(turnOn ? "ON" : "OFF")} event=command_enqueued");

            if (_board.IsScanning)
                await StopScanIntentionallyAsync("ManualRelay");

            try
            {
                // Mỗi lần BẬT luôn gửi OFF trước để relay đang giữ phải nhả
                // hoàn toàn, sau đó mới chọn đúng một relay vật lý cần bật.
                await _board.AllRelaysOffAsync();
                if (turnOn)
                    await _board.SetRelayAsync(relay);

                Volatile.Write(ref _manualActiveRelay, turnOn ? relay : 0);
                double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                AsyncFileLogService.Current.Performance(
                    $"MANUAL_RELAY_LATENCY relay={relay} action={(turnOn ? "ON" : "OFF")} event=ui_update elapsed_ms={elapsedMs:0.###}");
                AddLog(turnOn
                    ? $"MANUAL Relay {relay} ON - relay còn lại đã OFF."
                    : $"MANUAL Relay {relay} OFF - tất cả relay OFF.");
                activeRelay = Volatile.Read(ref _manualActiveRelay);
            }
            catch (Exception ex)
            {
                try { await _board.AllRelaysOffAsync(); }
                catch (Exception offEx) { AddLog($"MANUAL safe OFF sau lỗi relay thất bại: {offEx.Message}"); }
                Volatile.Write(ref _manualActiveRelay, 0);
                EnterDeviceFault(ex, "ManualRelay");
                throw;
            }
        }
        finally
        {
            _manualRelayGate.Release();
        }

        // Giữ Manual khi relay ON. Chỉ khi bấm TẮT mới thoát Manual và
        // khôi phục scan Production.
        if (!turnOn)
            await ExitManualModeAsync(outputsAlreadyOff: true);

        return activeRelay;
    }

    public async Task ResetManualOutputsAsync()
    {
        if (IsDeviceFault)
            throw new InvalidOperationException("DeviceFault đang khóa lệnh Manual. Hãy thoát và mở lại ứng dụng.");
        if (!EnsureManualBoardReady("manual RESET", requireD2xxRelay: true))
            return;
        if (!IsManualModeActive)
            await EnterManualModeAsync();

        await _manualRelayGate.WaitAsync();
        try
        {
            try
            {
                if (_board.IsScanning)
                    await StopScanIntentionallyAsync("ManualReset");
                await _board.AllRelaysOffAsync();
                await _board.ResetClearAsync();
                await _board.AllRelaysOffAsync();
                Volatile.Write(ref _manualActiveRelay, 0);
                State = "MANUAL";
                AddLog("MANUAL RESET - reset clear hoàn tất, tất cả relay OFF.");
            }
            catch (Exception ex)
            {
                try { await _board.AllRelaysOffAsync(); }
                catch (Exception offEx) { AddLog($"MANUAL safe OFF sau lỗi reset thất bại: {offEx.Message}"); }
                Volatile.Write(ref _manualActiveRelay, 0);
                EnterDeviceFault(ex, "ManualReset");
                throw;
            }
        }
        finally
        {
            _manualRelayGate.Release();
        }

        await ExitManualModeAsync(outputsAlreadyOff: true);
    }

    public async Task<IReadOnlyList<ResistanceResult>> MeasureManualResistanceAsync(
        IReadOnlyList<ResistanceStep> steps,
        Action<ResistanceResult>? onChannelUpdated = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
            throw new InvalidOperationException("Chưa có kênh điện trở hợp lệ để đo.");
        if (IsDeviceFault)
            throw new InvalidOperationException("DeviceFault đang khóa thao tác phần cứng.");
        if (!EnsureManualBoardReady("đo điện trở bằng tay", requireD2xxRelay: true))
            return [];
        if (!IsManualModeActive)
            await EnterManualModeAsync();

        await _manualRelayGate.WaitAsync(ct);
        try
        {
            State = "ĐO ĐIỆN TRỞ BẰNG TAY";
            AddLog(
                "[MANUAL-R] Bắt đầu đo " +
                string.Join(", ", steps.Select(step => $"{step.Name}/CH{step.Channel}")));
            await EnsureKeysightConnectedAsync();
            _scanSupervisor.Suspend("Resistance");
            List<ResistanceResult> results =
                await _engine.MeasureResistanceStepsAsync(steps, onChannelUpdated, ct);
            AddLog(
                "[MANUAL-R] Hoàn thành: " +
                string.Join(", ", results.Select(result =>
                    $"{result.Name}/CH{result.Channel}={result.Display} {result.ResultText}")));
            return results;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            EnterDeviceFault(ex, "ManualResistance");
            throw;
        }
        finally
        {
            _manualRelayGate.Release();
            if (IsManualModeActive)
                await ExitManualModeAsync();
        }
    }

    public async Task<WaterProofRunResult> TestManualWaterProofAsync(
        WaterProofMachineSettings machine,
        WaterProofModelSettings profile,
        Action<WaterProofProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.EnabledChannelCount == 0)
            throw new InvalidOperationException("Chưa bật kênh Leak nào để test.");
        if (!IsManualModeActive && !CanEnterManualMode)
            throw new InvalidOperationException("Không thể chạy thử máy Leak khi chu trình production đang hoạt động.");
        if (Interlocked.CompareExchange(ref _waterProofRunning, 1, 0) != 0)
            throw new InvalidOperationException("Máy Leak đang được một chu trình khác sử dụng.");

        try
        {
            if (!IsManualModeActive)
                await EnterManualModeAsync();

            await _manualRelayGate.WaitAsync(ct);
            try
            {
                if (_board.IsConnected)
                {
                    if (_board.IsScanning)
                        await StopScanIntentionallyAsync("ManualLeak");
                    await _board.AllRelaysOffAsync();
                }
                Volatile.Write(ref _manualActiveRelay, 0);

                State = "CHẠY THỬ MÁY LEAK";
                AddLog(
                    $"[MANUAL-LEAK] START port={machine.PortName?.Trim()} baud={WaterProofMachineSettings.DefaultBaudRate}; " +
                    "không ghi sản lượng, không chạy relay.");
                WaterProofRunResult result = await _waterProof.RunTestAsync(
                    machine,
                    profile,
                    progress,
                    ct);
                AddLog($"[MANUAL-LEAK] COMPLETE result={(result.Passed ? "PASS" : "FAIL")}");
                return result;
            }
            finally
            {
                _manualRelayGate.Release();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _waterProofRunning, 0);
            if (IsManualModeActive)
                await ExitManualModeAsync(outputsAlreadyOff: true);
        }
    }

    private string ReadyStateForCurrentModel()
    {
        if (IsProductRemovalPending)
            return "VUI LÒNG THÁO SẢN PHẨM";

        if (IsManualModeActive)
            return "MANUAL";

        if (_model is null)
            return "CHỜ CHỌN MÃ HÀNG";
        if (IsIoMappingMode)
            return "LẮP SẢN PHẨM";
        if (_requireStartupIoClear && Volatile.Read(ref _startupIoInterlockState) != 2)
            return "CHỜ ĐỒNG BỘ DỮ LIỆU BO";
        return MasterApproved
            ? "CHỜ LẮP SẢN PHẨM"
            : "KIỂM TRA MASTER PASS";
    }

    private string PassRelaySequenceText()
    {
        bool jigEnabled = _productionSettings.JigEjectRelayEnabled;
        bool markingEnabled = _productionSettings.PassMarkingRelayEnabled;
        int jigRelay = _productionSettings.RelayWiringMode == 1 ? 2 : 1;
        int markingRelay = _productionSettings.RelayWiringMode == 1 ? 1 : 2;

        if (!jigEnabled && !markingEnabled)
            return "PASS không kích relay theo cấu hình";
        if (!jigEnabled)
            return $"Relay {markingRelay} MARKING";
        if (!markingEnabled)
            return $"Relay {jigRelay} mở JIG";

        return $"Relay {markingRelay} MARKING -> Relay {jigRelay} mở JIG";
    }

    private string FaultJigRelayText()
    {
        int relay = _productionSettings.RelayWiringMode == 1 ? 2 : 1;
        int pulseMs = relay == 2
            ? _productionSettings.Relay2MarkingPulseMs
            : _productionSettings.Relay1JigPulseMs;
        return $"Relay {relay} mở JIG ({pulseMs} ms)";
    }

    private void ReportDeviceFaultForTest(Exception exception, int desiredRowsCount = -1) =>
        EnterDeviceFault(exception, "SELF-TEST", desiredRowsCount);

    private void EnterDeviceFault(Exception exception, string source, int desiredRowsCount = -1)
    {
        ArgumentNullException.ThrowIfNull(exception);

        bool firstTransition = Interlocked.Exchange(ref _deviceFault, 1) == 0;
        if (firstTransition)
            Interlocked.Increment(ref _deviceFaultTransitionCount);

        string diagnostic =
            $"DEVICE FAULT [{source}]{Environment.NewLine}" +
            $"Timestamp={DateTime.Now:O}{Environment.NewLine}" +
            $"Exception={exception.GetType().FullName}{Environment.NewLine}" +
            $"Message={exception.Message}{Environment.NewLine}" +
            $"StackTrace={exception.StackTrace}{Environment.NewLine}" +
            $"InnerException={exception.InnerException}{Environment.NewLine}" +
            $"Thread={Environment.CurrentManagedThreadId}{Environment.NewLine}" +
            $"CycleId={_activeCycleId}{Environment.NewLine}" +
            $"MasterState={MasterState}{Environment.NewLine}" +
            $"ProductionState={State}{Environment.NewLine}" +
            $"Faults.Count={Faults.Count}{Environment.NewLine}" +
            $"desiredRows.Count={desiredRowsCount}{Environment.NewLine}" +
            $"Model={_model?.ModelName ?? "(none)"} / {_model?.PartNumber ?? "(none)"}{Environment.NewLine}" +
            $"BoardGeneration={Volatile.Read(ref _runtimeGeneration)}{Environment.NewLine}" +
            $"LastFrameSequence={_engine.LastFrameSequence}";

        AsyncFileLogService.Current.Error(diagnostic);
        AddLog($"DEVICE FAULT: {exception.GetType().Name}: {exception.Message}");

        if (!firstTransition)
            return;

        if (Application.Current is not null)
        {
            CrashReportService.Write(
                exception,
                $"Hardware.DeviceFault.{source}",
                diagnostic);
        }

        _deviceFaultMessage = "Mất kết nối với máy test. Vui lòng khởi động lại.";
        BoardConnectionMessage = _deviceFaultMessage;
        HardwareStatus = "Máy test: MẤT KẾT NỐI";
        ResetManualProbeSession("device-fault");
        _cycleActive = false;
        _waitForProductRelease = false;
        _waitForFaultProductRemoval = false;
        _productDetectedThisCycle = false;
        Interlocked.Exchange(ref _postContinuityStarted, 0);
        Interlocked.Exchange(ref _wiringFaultHandlingStarted, 0);
        Interlocked.Exchange(ref _masterPostStarted, 0);
        Interlocked.Exchange(ref _masterEjectStarted, 0);
        Interlocked.Exchange(ref _resultRecordedThisCycle, 0);
        Interlocked.Exchange(ref _manualModeActive, 0);
        Volatile.Write(ref _manualActiveRelay, 0);
        SwitchRuntimeMode(RuntimeMode.Background);
        CancelCycleOperations();
        _sound.SetWiringFaultAlarm(false);
        _engine.SetFrameProcessingEnabled(false);
        ShowBoardUnavailablePresentation(force: true);
        State = "LỖI THIẾT BỊ";
        RaiseDeviceFaultState();

        _deviceFaultHardwareLockTask = SafeLockHardwareForDeviceFaultAsync();
        ShowDeviceFaultDialogOnce();
    }

    private async Task SafeLockHardwareForDeviceFaultAsync()
    {
        try
        {
            if (_board.IsConnected)
            {
                _scanSupervisor.MarkFaulted("device-fault-hardware-lock");
                await _board.StopScanAsync();
                await _board.AllRelaysOffAsync();
            }
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"DeviceFault hardware lock failed: {ex}");
            AddLog($"DEVICE FAULT: không thể cưỡng bức dừng phần cứng: {ex.Message}");
        }
    }

    private void ShowDeviceFaultDialogOnce()
    {
        if (Interlocked.Exchange(ref _deviceFaultDialogShown, 1) != 0)
            return;

        Interlocked.Increment(ref _deviceFaultDialogCount);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.BeginInvoke(new Action(async () =>
        {
            var dialog = new JBZUniversalTester.Views.FaultConfirmationWindow(
                [new FaultDetail
                {
                    Type = ProductFaultType.SystemDeviceError,
                    Message = _deviceFaultMessage
                }],
                "Bấm XÁC NHẬN để đóng chương trình.",
                windowHeader: "LỖI THIẾT BỊ");
            Window? owner = ResolveOperatorDialogOwner();
            if (owner is not null)
                dialog.Owner = owner;
            dialog.ShowDialog();

            await ExitApplicationAfterDeviceFaultAsync();
        }));
    }

    private async Task ExitApplicationAfterDeviceFaultAsync()
    {
        try
        {
            await _deviceFaultHardwareLockTask;
            await ShutdownAsync();
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"DeviceFault shutdown failed: {ex}");
        }

        Application? application = Application.Current;
        if (application?.MainWindow is Window mainWindow)
            mainWindow.Close();
        else
            application?.Shutdown(1);
    }

    private void RaiseDeviceFaultState()
    {
        Raise(nameof(IsDeviceFault));
        Raise(nameof(IsBoardConnected));
        Raise(nameof(BoardConnectionMessage));
        Raise(nameof(HasBoardConnectionError));
        Raise(nameof(IsManualModeActive));
        Raise(nameof(CanEnterManualMode));
        Raise(nameof(DeviceFaultMessage));
        Raise(nameof(DeviceFaultTransitionCount));
        Raise(nameof(DeviceFaultDialogCount));
        Raise(nameof(ResultStatusText));
        Raise(nameof(StateBackground));
        Raise(nameof(StateForeground));
        RaiseActiveFault();
    }

    private static string ResolveHistoryDatabasePath(ProductionSettings settings) =>
        RuntimePaths.DatabaseFile;

    private PartCounterStore PartCounterStore => _partCounterStore.Value;

    private TestHistoryStore HistoryStore
    {
        get
        {
            lock (_historyStoreGate)
                return _historyStore ??= new TestHistoryStore(ResolveHistoryDatabasePath(_productionSettings));
        }
    }

    private ProductionPersistenceService ProductionPersistence
    {
        get
        {
            lock (_historyStoreGate)
            {
                TestHistoryStore repository = _historyStore ??=
                    new TestHistoryStore(ResolveHistoryDatabasePath(_productionSettings));
                return _productionPersistence ??= new ProductionPersistenceService(
                    repository,
                    _productionSettings,
                    ProgramIdentityService.VersionText);
            }
        }
    }

    private CancellationToken BeginCycleOperations()
    {
        CancellationTokenSource? old;

        lock (_cycleTokenGate)
        {
            old = _cycleCts;
            _cycleCts = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCts.Token);
        }

        if (old is not null)
        {
            try { old.Cancel(); } catch { }
            old.Dispose();
        }

        return _cycleCts.Token;
    }

    private CancellationToken CurrentCycleToken()
    {
        lock (_cycleTokenGate)
            return _cycleCts?.Token ?? CancellationToken.None;
    }

    private void CancelCycleOperations()
    {
        CancellationTokenSource? current;

        lock (_cycleTokenGate)
        {
            current = _cycleCts;
            _cycleCts = null;
        }

        if (current is not null)
        {
            try { current.Cancel(); } catch { }
            current.Dispose();
        }

        // SerialPort driver có thể đang kẹt trong ReadExisting/Write. Tách và
        // đóng handle trên worker riêng để đóng view/app không chờ COM vô hạn.
        _waterProof.AbortActiveRun();
    }

    /// <summary>
    /// Chạy đúng một lần khi TestWindow được mở: kết nối bo trước,
    /// sau đó tự tải file model đã kiểm tra gần nhất nếu chưa có model hiện tại.
    /// </summary>
    /// <summary>
    /// Khởi động kết nối bo đúng một lần ngay khi ứng dụng được tạo.
    /// Không yêu cầu người vận hành bấm nút KẾT NỐI BO.
    /// </summary>
    public Task InitializeHardwareAsync()
    {
        lock (_initializationGate)
        {
            if (IsDeviceFault)
                return Task.CompletedTask;

            if (_board.IsConnected)
                return EnsureContinuousProductionScanAsync();

            // Một phiên ứng dụng chỉ được mở bo đúng một lần. Kết quả lỗi được
            // cache suốt phiên; chỉ đóng/mở lại ứng dụng mới tạo lifecycle mới.
            return _hardwareInitializationTask ??= ConnectBoardAsync();
        }
    }

    private async Task EnsureContinuousProductionScanAsync()
    {
        if (_lifetimeCts.IsCancellationRequested ||
            IsManualModeActive ||
            Volatile.Read(ref _probeSessionActive) != 0 ||
            Volatile.Read(ref _postContinuityStarted) != 0 ||
            Volatile.Read(ref _wiringFaultHandlingStarted) != 0 ||
            !_board.IsConnected)
        {
            return;
        }

        // Không return chỉ vì firmware đang scan. Model có thể vừa đổi từ
        // active=1 sang active=8 trong khi stream cũ vẫn đang chạy. ScanSupervisor
        // so AppliedScanCapacity với requested capacity và chỉ STOP/START khi dải
        // firmware thực sự cần đổi; cùng dải thì hoàn toàn reuse, không gửi lệnh.
        try
        {
            bool backgroundOnly = CurrentRuntimeMode == RuntimeMode.Background &&
                                  !_cycleActive &&
                                  !_waitForProductRelease &&
                                  !_waitForFaultProductRemoval;
            if (backgroundOnly)
                _engine.SetFrameProcessingEnabled(false);
            bool started = await _scanSupervisor.EnsureProductionScanAsync(
                _model?.MaxIo ?? 0,
                _lifetimeCts.Token);
            if (backgroundOnly)
                State = ReadyStateForCurrentModel();
            if (started)
                AddLog("Bo đã kết nối và START SCAN I/O liên tục ở chế độ nền.");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _scanSupervisor.BeginRecovery("ensure-production-failed");
            if (!await RecoverProductionScanAsync("EnsureProductionScan"))
                EnterDeviceFault(ex, "EnsureProductionScan");
        }
    }

    private async Task StartProductionScanAndVerifyFrameAsync(
        CancellationToken ct,
        string reason)
    {
        try
        {
            await _scanSupervisor.StartProductionScanAndVerifyFrameAsync(
                _model?.MaxIo ?? 0,
                ct,
                reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _scanSupervisor.BeginRecovery($"start-first-frame-failed:{reason}");
            if (await RecoverProductionScanAsync($"ProductionScan:{reason}"))
                return;

            EnterDeviceFault(ex, $"ProductionScan:{reason}");
            throw;
        }
    }

    private async Task StopScanIntentionallyAsync(
        string reason,
        CancellationToken ct = default)
    {
        // Suspend before STOP so the watchdog can never observe a stopped scan
        // while still using the previous Monitoring baseline.
        _scanSupervisor.Suspend(reason);
        await _board.StopScanAsync(ct);
    }

    private async Task<bool> RecoverProductionScanAsync(string reason)
    {
        bool recoveryGateEntered = false;
        try
        {
            await _scanRecoveryGate.WaitAsync(_lifetimeCts.Token);
            recoveryGateEntered = true;
            if (_scanSupervisor.HealthSnapshot.State == ScanHealthState.Monitoring &&
                _board.IsConnected &&
                _board.IsScanning)
            {
                return true;
            }

            BoardScanMode resumeMode = _scanSupervisor.HealthSnapshot.ExpectedMode;
            int maxIo = _model?.MaxIo ?? 0;
            if (await _scanSupervisor.RecoverSoftAsync(
                    maxIo,
                    resumeMode,
                    _lifetimeCts.Token))
            {
                InvokeUi(UpdateCardScanningState);
                return true;
            }

            bool reopened = await _scanSupervisor.RecoverReopenAsync(
                maxIo,
                resumeMode,
                _lifetimeCts.Token);
            if (reopened)
            {
                BoardConnectionMessage = string.Empty;
                HardwareStatus = "Bo: đã tự phục hồi kết nối";
                Raise(nameof(IsBoardConnected));
                InvokeUi(UpdateCardScanningState);
                AddLog($"SCAN_RECOVERY hoàn tất sau {reason}; giữ nguyên lifecycle sản phẩm hiện tại.");
            }
            return reopened;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (recoveryGateEntered)
                _scanRecoveryGate.Release();
        }
    }

    private async Task HardwareMonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (IsDeviceFault)
                    break;

                if (!_board.IsConnected)
                {
                    if (_scanSupervisor.TryBeginDisconnectedRecovery(out ScanHealthSnapshot disconnected))
                    {
                        AsyncFileLogService.Current.Performance(
                            $"SCAN_WATCHDOG disconnected state={disconnected.State} reason={disconnected.Reason}");
                        if (!await RecoverProductionScanAsync("HardwareMonitor.Disconnected"))
                        {
                            EnterDeviceFault(
                                new IOException("Bo D2XX mất kết nối và cả soft/reopen recovery đều thất bại."),
                                "HardwareMonitor.Disconnected");
                            break;
                        }
                    }
                }

                ScanHealthSnapshot health = _scanSupervisor.HealthSnapshot;
                if (health.State is ScanHealthState.Starting or ScanHealthState.Monitoring)
                {
                    int firstFrameTimeoutMs =
                        ScanSupervisor.ResolveFirstFrameTimeoutMs(_board.Capacity);
                    int stallTimeoutMs =
                        ScanSupervisor.ResolveProductionStallTimeoutMs(_board.Capacity);
                    if (_scanSupervisor.TryBeginWatchdogRecovery(
                            firstFrameTimeoutMs,
                            stallTimeoutMs,
                            out ScanHealthSnapshot stalled))
                    {
                        AsyncFileLogService.Current.Performance(
                            $"SCAN_WATCHDOG stall_age_ms={stalled.CompleteFrameAgeMilliseconds:0.###} " +
                            $"state_age_ms={stalled.StateAgeMilliseconds:0.###} " +
                            $"generation={stalled.ScanGeneration} reason={stalled.Reason}");
                        if (!await RecoverProductionScanAsync("ScanWatchdog"))
                        {
                            EnterDeviceFault(
                                new TimeoutException(
                                    $"Scan stall không phục hồi được: state={stalled.State}, " +
                                    $"age={stalled.CompleteFrameAgeMilliseconds:0} ms."),
                                "ScanWatchdog.RecoveryFailed");
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                EnterDeviceFault(ex, "HardwareMonitor");
                break;
            }

            try
            {
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public Task InitializeAsync()
    {
        lock (_initializationGate)
        {
            return _initializationTask ??= InitializeCoreAsync();
        }
    }

    private async Task InitializeCoreAsync()
    {
        AddLog("Khởi tạo ứng dụng: kết nối bo trước, sau đó mới nạp mã gần nhất.");

        // SetModel mở gate kiểm tra IO nền. Vì vậy phải chờ D2XX Connect/handshake
        // hoàn tất trước khi nạp THT, tránh kích hoạt gate bằng frame trong giai
        // đoạn transport còn đang khởi tạo.
        await InitializeHardwareAsync();

        if (IsDeviceFault || !_board.IsConnected)
        {
            if (!IsDeviceFault)
            {
                EnterDeviceFault(
                    new IOException("Bo không còn kết nối sau bước khởi tạo."),
                    "InitializeCore.NoBoard");
            }
            AddLog("Khởi động bo thất bại; phiên hiện tại bị khóa đến khi thoát và mở lại ứng dụng.");
            StartupPerformanceTrace.Mark("T12 STARTUP_BOARD_FAULT");
            return;
        }

        if (_board.IsConnected)
        {
            if (_model is null)
            {
                await LoadLastTestedModelAsync();
            }
            else
            {
                CurrentModelPath = ResolveOptionalModelPath(_model.SourcePath);
                AddLog($"Giữ model đang chọn: {ModelName}");
            }
        }
        await EnsureContinuousProductionScanAsync();

        // Watchdog chỉ khóa phiên khi mất bo/scan; không tự reconnect.
        _hardwareMonitorTask ??= HardwareMonitorLoopAsync(_lifetimeCts.Token);

        State = ReadyStateForCurrentModel();
        StartupPerformanceTrace.Mark("T12 STARTUP_READY");
    }

    private static void ValidateModelPath(string path, out string fullPath)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Đường dẫn model không hợp lệ.", nameof(path));

        fullPath = ResolveModelPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Không tìm thấy file model .tht.", fullPath);
    }

    /// <summary>
    /// Nạp model do NGƯỜI VẬN HÀNH chọn. Lựa chọn này luôn ưu tiên hơn
    /// auto-load model startup. Parse chạy background, SetModel trở lại UI
    /// continuation nên bảng TestView được dựng ngay khi task hoàn tất.
    /// </summary>
    public async Task<ProductModel?> LoadSelectedModelFromPathAsync(string path)
    {
        ValidateModelPath(path, out string fullPath);

        int generation = Interlocked.Increment(ref _modelLoadGeneration);
        State = "ĐANG NẠP MÃ HÀNG...";

        ProductModel model;
        TestEngine.PreparedModelState preparedEngineModel;
        using (StartupPerformanceTrace.Measure("THT_MODEL_LOAD"))
        {
            IReadOnlyList<ProductModel> candidates = await Task.Run(() =>
            {
                long parseStarted = Stopwatch.GetTimestamp();
                IReadOnlyList<ProductModel> parsed = _modelParser.LoadAll(fullPath);
                double parseMs = Stopwatch.GetElapsedTime(parseStarted).TotalMilliseconds;
                AsyncFileLogService.Current.Performance(
                    $"MODEL_LOAD_PERF phase=THT_PARSE path={Path.GetFileName(fullPath)} duration_ms={parseMs:0.###}");
                StartupPerformanceTrace.Mark("T8 MODEL_PARSE_DONE");
                return parsed;
            });

            if (generation != Volatile.Read(ref _modelLoadGeneration))
                return null;

            if (candidates.Count > 1)
            {
                var dialog = new JBZUniversalTester.Views.PartSelectionWindow(
                    candidates,
                    _productionSettings.LastThtPartKey)
                {
                    Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                };
                if (dialog.ShowDialog() != true || dialog.SelectedModel is null)
                {
                    State = "ĐÃ HỦY CHỌN PART";
                    return null;
                }
                model = dialog.SelectedModel;
            }
            else
            {
                model = candidates[0];
            }

            ModelFileIdentityService.Capture(model, fullPath);
            preparedEngineModel = await Task.Run(() =>
            {
                long engineStarted = Stopwatch.GetTimestamp();
                TestEngine.PreparedModelState prepared = _engine.PrepareModel(model);
                AsyncFileLogService.Current.Performance(
                    $"MODEL_LOAD_PERF phase=ENGINE_MODEL_BUILD model={model.ModelName} duration_ms={Stopwatch.GetElapsedTime(engineStarted).TotalMilliseconds:0.###}");
                StartupPerformanceTrace.Mark("T9 MODEL_LOGIC_READY");
                return prepared;
            });
        }

        // Nếu người vận hành chọn tiếp file khác trong lúc file này đang parse,
        // bỏ kết quả cũ thay vì ghi đè model mới hơn.
        if (generation != Volatile.Read(ref _modelLoadGeneration))
            return null;

        _productionSettings.LastThtPartKey = JBZUniversalTester.Views.PartSelectionWindow.PartKey(model);
        SetModel(model, preparedEngineModel);
        // SetModel chỉ đổi requested active range. Nếu firmware đang chạy dải của
        // model trước, reconcile ngay tại đường load async trước khi MainWindow tự
        // mở TestView. Healthy same-capacity stream được supervisor giữ nguyên.
        if (_board.IsConnected)
            await EnsureContinuousProductionScanAsync();
        StartupPerformanceTrace.Mark("T10 MODEL_UI_READY");
        State = _board.IsConnected && !IsDeviceFault
            ? ReadyStateForCurrentModel()
            : "LỖI THIẾT BỊ";
        return model;
    }

    /// <summary>Nhận model đã parse sẵn bởi caller nội bộ.</summary>
    public async Task<ProductModel?> LoadPreparedModelAsync(ProductModel model)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        Interlocked.Increment(ref _modelLoadGeneration);
        TestEngine.PreparedModelState prepared = await Task.Run(() =>
        {
            ModelFileIdentityService.Capture(model);
            return _engine.PrepareModel(model);
        });
        SetModel(model, prepared);
        if (_board.IsConnected)
            await EnsureContinuousProductionScanAsync();
        State = _board.IsConnected && !IsDeviceFault
            ? ReadyStateForCurrentModel()
            : "LỖI THIẾT BỊ";
        return model;
    }

    /// <summary>Compatibility helper for internal callers.</summary>
    public async Task LoadModelFromPathAsync(string path)
    {
        await LoadSelectedModelFromPathAsync(path);
    }

    private async Task LoadLastTestedModelAsync()
    {
        var savedPath = _productionSettings.LastThtPath;

        if (string.IsNullOrWhiteSpace(savedPath))
        {
            AddLog("Chưa có file model được kiểm tra gần nhất.");
            return;
        }

        var fullPath = ResolveModelPath(savedPath);
        if (!File.Exists(fullPath))
        {
            AddLog($"File model gần nhất không còn tồn tại: {fullPath}");
            return;
        }

        try
        {
            State = "ĐANG TẢI MODEL GẦN NHẤT";

            // Startup model chỉ là lựa chọn dự phòng. Nếu người vận hành chọn
            // file mới trong lúc parse đang chạy, generation đổi và kết quả
            // startup này bị bỏ ngay, tuyệt đối không ghi đè model mới.
            int generation = Volatile.Read(ref _modelLoadGeneration);
            (ProductModel Model, TestEngine.PreparedModelState Prepared) startup = await Task.Run(() =>
            {
                long parseStarted = Stopwatch.GetTimestamp();
                IReadOnlyList<ProductModel> candidates = _modelParser.LoadAll(fullPath);
                ProductModel parsed = candidates.FirstOrDefault(candidate =>
                        JBZUniversalTester.Views.PartSelectionWindow.PartKey(candidate)
                            .Equals(_productionSettings.LastThtPartKey, StringComparison.OrdinalIgnoreCase))
                    ?? candidates[0];
                ModelFileIdentityService.Capture(parsed, fullPath);
                double parseMs = Stopwatch.GetElapsedTime(parseStarted).TotalMilliseconds;
                AsyncFileLogService.Current.Performance(
                    $"MODEL_LOAD_PERF phase=STARTUP_THT_PARSE path={Path.GetFileName(fullPath)} duration_ms={parseMs:0.###}");
                StartupPerformanceTrace.Mark("T8 MODEL_PARSE_DONE");
                long engineStarted = Stopwatch.GetTimestamp();
                TestEngine.PreparedModelState prepared = _engine.PrepareModel(parsed);
                AsyncFileLogService.Current.Performance(
                    $"MODEL_LOAD_PERF phase=ENGINE_MODEL_BUILD model={parsed.ModelName} duration_ms={Stopwatch.GetElapsedTime(engineStarted).TotalMilliseconds:0.###}");
                StartupPerformanceTrace.Mark("T9 MODEL_LOGIC_READY");
                return (parsed, prepared);
            });

            if (generation == Volatile.Read(ref _modelLoadGeneration) && _model is null)
            {
                _productionSettings.LastThtPartKey =
                    JBZUniversalTester.Views.PartSelectionWindow.PartKey(startup.Model);
                SetModel(startup.Model, startup.Prepared);
                StartupPerformanceTrace.Mark("T10 MODEL_UI_READY");
                AddLog($"Đã tự tải model gần nhất: {Path.GetFileName(fullPath)}");
            }
            else
            {
                AddLog($"Bỏ model startup {Path.GetFileName(fullPath)} vì người vận hành đã chọn model mới.");
            }

            State = _board.IsConnected && !IsDeviceFault
                ? ReadyStateForCurrentModel()
                : "LỖI THIẾT BỊ";
        }
        catch (Exception ex)
        {
            State = _board.IsConnected
                ? "LẮP SẢN PHẨM"
                : "LỖI KẾT NỐI BO";
            AddLog($"Không thể tải model gần nhất: {ex.Message}");
        }
    }

    private static string ResolveModelPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Đường dẫn model không hợp lệ.", nameof(path));
        }

        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private RuntimeMode CurrentRuntimeMode =>
        (RuntimeMode)Volatile.Read(ref _runtimeMode);

    private long SwitchRuntimeMode(RuntimeMode mode)
    {
        Volatile.Write(ref _runtimeMode, (int)mode);
        return Interlocked.Increment(ref _runtimeGeneration);
    }

    private bool IsRuntimeMode(RuntimeMode mode) =>
        CurrentRuntimeMode == mode;

    private bool IsRuntimeContext(RuntimeMode mode, long generation) =>
        CurrentRuntimeMode == mode &&
        Volatile.Read(ref _runtimeGeneration) == generation;

    private ProductionPhase CurrentProductionPhase =>
        (ProductionPhase)Volatile.Read(ref _productionPhase);

    private void SetProductionPhase(ProductionPhase phase)
    {
        Volatile.Write(ref _productionPhase, (int)phase);
        ProductionRuntimeState? lifecycleState = phase switch
        {
            ProductionPhase.WaitingProduct => ProductionRuntimeState.WaitingForProduct,
            ProductionPhase.WaitingProductRemoval => ProductionRuntimeState.WaitingForRemoval,
            ProductionPhase.WaitingFaultConfirmation or ProductionPhase.EquipmentError =>
                ProductionRuntimeState.Failed,
            ProductionPhase.Resistance or ProductionPhase.WaterProof or ProductionPhase.Completed =>
                ProductionRuntimeState.PassSequence,
            _ => null
        };
        if (lifecycleState.HasValue)
            SetProductionRuntimeState(lifecycleState.Value);
    }

    private long AdvanceProductionUiCycleEpoch() =>
        Interlocked.Increment(ref _productionUiCycleEpoch);

    private void SetProductionRuntimeState(
        ProductionRuntimeState state,
        long frameSequence = 0,
        string reason = "LIFECYCLE")
    {
        int previous = Interlocked.Exchange(ref _productionRuntimeState, (int)state);
        if (previous != (int)state)
        {
            Raise(nameof(CurrentProductionRuntimeState));
            AsyncFileLogService.Current.Performance(
                $"PRODUCT_STATE old={(ProductionRuntimeState)previous} new={state} " +
                $"seq={frameSequence} reason={reason}");
        }
    }

    private void SetProbePresentationState(
        ProbePresentationState state,
        IReadOnlyList<int> ios,
        long frameSequence)
    {
        int previous = Interlocked.Exchange(ref _probePresentationState, (int)state);
        if (previous == (int)state)
            return;

        Raise(nameof(CurrentProbePresentationState));
        // Center overlay depends on Probe state: hide "LẮP SẢN PHẨM" while
        // Candidate/Touch owns the presentation, restore it only after Release.
        RaiseCenterPresentation();
        AsyncFileLogService.Current.Performance(
            $"PROBE_STATE old={(ProbePresentationState)previous} new={state} " +
            $"ios={string.Join(',', ios.OrderBy(io => io).Select(io => $"IO{io}"))} seq={frameSequence}");
    }

    private void SetProductionPresentationMode(
        ProductionPresentationMode mode,
        long frameSequence,
        string reason)
    {
        int previous = Interlocked.Exchange(ref _productionPresentationMode, (int)mode);
        if (previous == (int)mode)
            return;

        Raise(nameof(CurrentProductionPresentationMode));
        // Center waiting overlay is presentation-owned. When Probe/Product takes
        // visual priority it must be hidden immediately, without changing ProductState.
        RaiseCenterPresentation();
        AsyncFileLogService.Current.Performance(
            $"PRESENTATION_STATE old={(ProductionPresentationMode)previous} new={mode} " +
            $"seq={frameSequence} reason={reason}");
    }

    /// <summary>
    /// Chỉ Production thật mới được phép tạo lỗi dây/popup.
    /// Probe phải bị loại kể cả khi callback Production cũ đã được xếp hàng
    /// trước lúc chuyển cửa sổ.
    /// </summary>
    private bool IsProductionFaultContext(long generation) =>
        IsRuntimeContext(RuntimeMode.Production, generation) &&
        Volatile.Read(ref _probeSessionActive) == 0 &&
        Volatile.Read(ref _inlineProbeContactIo) == 0 &&
        MasterApproved;

    /// <summary>
    /// API chẩn đoán explicit Probe: khóa Production trước khi chuyển transport.
    /// V12.9 không còn cửa sổ PinProbe riêng; UI vận hành dùng Probe song song
    /// trong TestWindow. StartProbeScanAsync vẫn được giữ cho diagnostic/service
    /// sau đó, nhưng từ thời điểm này không callback Production nào được phép đổi UI,
    /// phát âm lỗi hoặc mở popup.
    /// </summary>
    public void PrepareProbeUiMode()
    {
        // ALWAYS_PROBE_2026-09-05:
        // Probe/TestPin luôn là lớp quan sát song song trên stream Production.
        // Không còn trạng thái bật/tắt từ Production Settings.
        InvokeUi(RaiseTestStatistics);
    }

    private static string? ResolveOptionalModelPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : ResolveModelPath(path);
    }

    private void ResetEngineWithoutChangedReentry()
    {
        Interlocked.Increment(ref _suppressEngineChanged);
        try
        {
            _engine.Reset();
        }
        finally
        {
            Interlocked.Decrement(ref _suppressEngineChanged);
        }
    }

    private void OnEngineChanged(object? sender, EventArgs e)
    {
        if (IsDeviceFault)
            return;

        // TestEngine.Reset() phát Changed đồng bộ. Bỏ qua callback lồng nhau
        // khi chính ViewModel đang reset engine trong một transition Master.
        if (Volatile.Read(ref _suppressEngineChanged) != 0)
            return;

        // V11.6: chỉ RuntimeMode.Production mới được phép cập nhật bảng lỗi,
        // phát TESTPOINT hoặc mở popup. Probe/Background bị chặn tuyệt đối.
        if (!IsRuntimeMode(RuntimeMode.Production) ||
            Volatile.Read(ref _probeSessionActive) != 0)
        {
            return;
        }

        long generation = Volatile.Read(ref _runtimeGeneration);

        try
        {
            ScheduleEngineUiUpdate(generation);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            EnterDeviceFault(ex, "EngineChanged");
        }
    }

    private void ScheduleEngineUiUpdate(long generation)
    {
        Interlocked.Increment(ref _uiRequestCount);
        long revision = Interlocked.Increment(ref _engineUiUpdateRevision);
        long cycleEpoch = Volatile.Read(ref _productionUiCycleEpoch);
        long probeRevision = Volatile.Read(ref _inlineProbeUiRevision);
        Volatile.Write(
            ref _latestEngineUiUpdateRequest,
            new EngineUiUpdateRequest(
                revision,
                generation,
                cycleEpoch,
                probeRevision,
                Stopwatch.GetTimestamp()));
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            try
            {
                EngineUiUpdateRequest request =
                    Volatile.Read(ref _latestEngineUiUpdateRequest)!;
                TestEnginePresentationSnapshot? snapshot = BuildEngineFaultRowsSnapshot();
                if (snapshot is null || !TryAcceptProductionUiSnapshot(request, snapshot))
                    return;

                Interlocked.Increment(ref _engineUiUpdatesScheduled);
                Interlocked.Increment(ref _engineUiUpdatesRendered);
                Interlocked.Increment(ref _uiRenderedCount);
                ProcessScheduledEngineChangedOnUi(request, snapshot);
                Volatile.Write(ref _engineUiLastCompletedRevision, request.Revision);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
            {
                EnterDeviceFault(ex, "ProcessEngineChangedOnUi");
            }
            return;
        }

        QueueEnginePresentationWorker(dispatcher);
    }

    private void QueueEnginePresentationWorker(System.Windows.Threading.Dispatcher dispatcher)
    {
        if (Interlocked.Exchange(ref _enginePresentationWorkerRunning, 1) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_lifetimeCts.IsCancellationRequested)
                {
                    EngineUiUpdateRequest? request = Volatile.Read(ref _latestEngineUiUpdateRequest);
                    if (request is null)
                        return;

                    TestEnginePresentationSnapshot? rows = BuildEngineFaultRowsSnapshot();
                    if (Volatile.Read(ref _latestEngineUiUpdateRequest)?.Revision != request.Revision)
                        continue;

                    long queuedAt = Stopwatch.GetTimestamp();
                    Interlocked.Increment(ref _engineUiUpdatesScheduled);
                    Interlocked.Increment(ref _uiDispatcherEnqueueCount);
                    await dispatcher.InvokeAsync(() =>
                    {
                        if (Volatile.Read(ref _latestEngineUiUpdateRequest)?.Revision != request.Revision)
                            return;

                        if (rows is null || !TryAcceptProductionUiSnapshot(request, rows))
                            return;

                        Interlocked.Increment(ref _engineUiUpdatesRendered);
                        Interlocked.Increment(ref _uiRenderedCount);
                        ProcessScheduledEngineChangedOnUi(request, rows);
                        Volatile.Write(ref _engineUiLastCompletedRevision, request.Revision);
                        double queueDelayMs = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
                        if (queueDelayMs > 16)
                        {
                            AsyncFileLogService.Current.Performance(
                                $"UI_PERF_WARNING phase=ENGINE_DISPATCHER duration_ms={queueDelayMs:0.###}");
                        }
                    });

                    if (Volatile.Read(ref _latestEngineUiUpdateRequest)?.Revision == request.Revision)
                        return;
                }
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
            {
                EnterDeviceFault(ex, "EnginePresentationWorker");
            }
            finally
            {
                Interlocked.Exchange(ref _enginePresentationWorkerRunning, 0);
                EngineUiUpdateRequest? latest = Volatile.Read(ref _latestEngineUiUpdateRequest);
                if (!_lifetimeCts.IsCancellationRequested &&
                    latest is not null &&
                    latest.Revision > Volatile.Read(ref _engineUiLastCompletedRevision))
                {
                    QueueEnginePresentationWorker(dispatcher);
                }
            }
        });
    }

    private TestEnginePresentationSnapshot? BuildEngineFaultRowsSnapshot()
    {
        bool removal = _waitForProductRelease || _waitForFaultProductRemoval;
        // Frame rỗng đã là nguồn authoritative cho ProductRemoved. Không dựng
        // lại bảng removal hàng trăm dòng trước khi Dispatcher được quyền reset
        // cycle và hiện CHỜ LẮP SẢN PHẨM.
        if (removal && _engine.IsProductReleased)
        {
            ProductionElectricalSnapshot electrical = _engine.GetProductionElectricalSnapshot();
            return new TestEnginePresentationSnapshot(
                electrical,
                Removal: true,
                Array.Empty<FaultRow>(),
                RowBuildMilliseconds: 0);
        }

        return _engine.CapturePresentationSnapshot(removal);
    }

    private sealed record EngineUiUpdateRequest(
        long Revision,
        long Generation,
        long CycleEpoch,
        long ProbeRevision,
        long RequestedAt);

    private bool TryAcceptProductionUiSnapshot(
        EngineUiUpdateRequest request,
        TestEnginePresentationSnapshot snapshot)
    {
        if (!IsRuntimeContext(RuntimeMode.Production, request.Generation) ||
            request.CycleEpoch != Volatile.Read(ref _productionUiCycleEpoch) ||
            request.ProbeRevision != Volatile.Read(ref _inlineProbeUiRevision))
        {
            return false;
        }

        return _productionUiVersionGate.TryAccept(
            request.Generation,
            request.CycleEpoch,
            snapshot.Electrical.ScanGeneration,
            snapshot.Electrical.FrameSequence,
            request.Revision);
    }

    private void ProcessScheduledEngineChangedOnUi(
        EngineUiUpdateRequest request,
        TestEnginePresentationSnapshot rowsSnapshot)
    {
        long applyStarted = Stopwatch.GetTimestamp();
        (string Key, string Status)[] rowsBefore = Faults
            .Select(row => (RowKey(row), row.Status))
            .ToArray();
        long generation = request.Generation;
        if (!MasterApproved)
        {
            // Khi người vận hành đã rời TestView giữa chu trình Master, frame
            // tiếp theo chỉ còn nhiệm vụ xác nhận đã tháo toàn bộ sản phẩm.
            // Không đưa frame đó trở lại state machine Master vì snapshot cũ
            // có thể đã làm StopView ARM removal gate ngay sau khi sản phẩm
            // được tháo ngoài thực tế.
            if (Volatile.Read(ref _removalMonitoringFromMain) != 0 &&
                IsProductRemovalPending &&
                _waitForProductRelease)
            {
                ProcessMasterRemovalAfterReturningToMain(generation);
                return;
            }

            ProcessMasterEngineChangedOnUi(generation);
        }
        else
            ProcessEngineChangedOnUi(generation, rowsSnapshot);

        double applyMs = Stopwatch.GetElapsedTime(applyStarted).TotalMilliseconds;
        double queueDelayMs = Stopwatch.GetElapsedTime(request.RequestedAt).TotalMilliseconds - applyMs;
        (int removed, int added, int changed) = CalculateRenderedRowDelta(rowsBefore, Faults);
        LogUiSnapshot(
            rowsSnapshot.Electrical.FrameSequence,
            rowsSnapshot.Electrical.ScanGeneration,
            rowsSnapshot.Rows.Count,
            "engine",
            removed,
            added,
            changed);
        AsyncFileLogService.Current.Performance(
            $"UI_FRAME seq={rowsSnapshot.Electrical.FrameSequence} " +
            $"generation={request.Generation}/{rowsSnapshot.Electrical.ScanGeneration} " +
            $"product_state={CurrentProductionRuntimeState} " +
            $"probe_state={CurrentProbePresentationState} " +
            $"presentation={CurrentProductionPresentationMode} " +
            $"rendered_rows={Faults.Count} added={added} removed={removed} changed={changed} " +
            $"engine_compute_ms={rowsSnapshot.Electrical.EngineComputeMilliseconds:0.###} " +
            $"row_diff_ms={rowsSnapshot.RowBuildMilliseconds:0.###} " +
            $"queue_delay_ms={Math.Max(0, queueDelayMs):0.###} apply_ms={applyMs:0.###}");
    }

    private void LogUiSnapshot(
        long frameSequence,
        long scanGeneration,
        int sourceRows,
        string source,
        int removed = 0,
        int added = 0,
        int changed = 0)
    {
        AsyncFileLogService.Current.Performance(
            $"UI_SNAPSHOT seq={frameSequence} generation={scanGeneration} " +
            $"product_state={CurrentProductionRuntimeState} " +
            $"probe_state={CurrentProbePresentationState} " +
            $"presentation_mode={CurrentProductionPresentationMode} " +
            $"source_rows={sourceRows} rendered_rows={Faults.Count} " +
            $"removed={removed} added={added} changed={changed} source={source}");
    }

    private void ProcessMasterRemovalAfterReturningToMain(long generation)
    {
        if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
            !_engine.IsProductReleased)
        {
            return;
        }

        _waitForProductRelease = false;
        SetProductRemovalPending(false);
        Interlocked.Exchange(ref _removalMonitoringFromMain, 0);
        _cycleActive = false;
        SetProductionPhase(ProductionPhase.WaitingProduct);
        SwitchRuntimeMode(RuntimeMode.Background);
        _engine.SetFrameProcessingEnabled(false);
        ResetProductPresentationCycle();
        MasterFaults.Clear();
        SynchronizeFaultRows(Array.Empty<FaultRow>());
        State = ReadyStateForCurrentModel();
        AddLog("Đã xác nhận frame rỗng sau khi rời kiểm tra Master; xóa khóa tháo sản phẩm.");
    }

    private void ProcessEngineChangedOnUi(
        long generation,
        TestEnginePresentationSnapshot? rowsSnapshot = null)
    {
        if (IsDeviceFault)
            return;

        if (!IsProductionFaultContext(generation))
            return;

        ApplyAuthoritativeProductionState(
            rowsSnapshot?.Electrical ?? _engine.GetProductionElectricalSnapshot());

        // Sau lỗi: chỉ chờ tháo sản phẩm, không phát lại lỗi.
        if (_waitForFaultProductRemoval)
        {
            if (_engine.IsProductReleased &&
                Interlocked.Exchange(ref _faultProductRemoved, 1) == 0)
            {
                MarkProductRemoved();
                AddLog("Đã tháo sản phẩm lỗi khỏi JIG.");
            }

            TryCompleteFaultProductRemoval();
            if (_waitForFaultProductRemoval)
            {
                RefreshFaultsFromSnapshot(rowsSnapshot);
                ObserveWaterProofRetestConnectorCycle(generation);
            }

            return;
        }

        // Sau PASS bắt buộc phải THÁO TOÀN BỘ sản phẩm khỏi JIG trước khi
        // ARM lượt test mới. Mất/chạm lại chỉ một I/O KHÔNG được xem là
        // sản phẩm đã tháo và tuyệt đối không được làm relay chạy lại.
        // Chỉ khi không còn bất kỳ quan hệ continuity sản phẩm nào thì mới
        // reset engine và chuyển về CHỜ LẮP SẢN PHẨM cho lượt tiếp theo.
        if (_waitForProductRelease)
        {
            if (_engine.IsProductReleased)
            {
                MarkProductRemoved();
                _waitForProductRelease = false;
                SetProductRemovalPending(false);
                bool returnedToMain =
                    Interlocked.Exchange(ref _removalMonitoringFromMain, 0) != 0;
                bool rearmAfterRemoval =
                    Interlocked.Exchange(ref _rearmAfterProductRemoval, 1) != 0;
                bool wasWaterProofEquipmentRecovery = _waterProofEquipmentErrorAwaitingRemoval;
                _waterProofEquipmentErrorAwaitingRemoval = false;
                ResetFullCycleAfterProductRemoved();
                if (returnedToMain || !rearmAfterRemoval)
                {
                    _cycleActive = false;
                    SetProductionPhase(ProductionPhase.WaitingProduct);
                    _engine.SetFrameProcessingEnabled(false);
                    if (returnedToMain)
                        SwitchRuntimeMode(RuntimeMode.Background);
                }
                State = "CHỜ LẮP SẢN PHẨM";
                AddLog(wasWaterProofEquipmentRecovery
                    ? "Đã tháo sản phẩm sau lỗi thiết bị leak - ARM lại chu kỳ, leak COM sẽ reconnect ở lần chạy kế tiếp."
                    : "PASS đã tháo hoàn toàn: toàn bộ continuity sản phẩm đã mất -> ARM lượt test mới.");
            }
            else
            {
                RefreshFaultsFromSnapshot(rowsSnapshot);
                ObserveWaterProofRetestConnectorCycle(generation);
            }

            return;
        }

        RefreshFaultsFromSnapshot(rowsSnapshot);
        LogFaultGate(generation);
        if (Volatile.Read(ref _firstLogicalStateLogged) != 0 &&
            Interlocked.CompareExchange(ref _firstUiUpdateRenderedLogged, 1, 0) == 0)
        {
            AsyncFileLogService.Current.Performance("FIRST_UI_UPDATE_RENDERED");
        }

        // Chỉ product fault đã qua monotonic confirmation gate mới được
        // dừng scan/popup/ghi FAIL. Candidate raw không đi vào lifecycle FAIL.
        ProductionPhase phase = CurrentProductionPhase;

        // Khi Leak chạy trước continuity, kết quả FAIL chỉ là trạng thái chờ
        // sửa tiếp xúc connector. Chỉ chốt lỗi sản phẩm khi snapshot D2XX đã
        // PASS toàn bộ topology và Leak là điều kiện duy nhất còn FAIL.
        if (_cycleActive &&
            phase == ProductionPhase.WaterProof &&
            Volatile.Read(ref _preContinuityWaterProofPassed) == 0 &&
            _model is ProductModel waterProofCycleModel)
        {
            var retestState = (WaterProofRetestConnectorState)Volatile.Read(
                ref _waterProofRetestConnectorState);
            if (retestState is WaterProofRetestConnectorState.AwaitingConnectorReconnect or
                WaterProofRetestConnectorState.Running)
            {
                ObserveWaterProofRetestConnectorCycle(generation);
                return;
            }

            if (HasFailedWaterProofResult() &&
                _engine.ContinuityPassed &&
                !_engine.HasWiringFault &&
                _engine.ReadyToEvaluateProductFaults)
            {
                _ = FinalizeWaterProofProductFailureAsync(
                    waterProofCycleModel,
                    generation,
                    CurrentCycleToken());
            }
            else if (Volatile.Read(ref _waterProofRunning) == 0)
            {
                ObserveWaterProofRetestConnectorCycle(generation);
            }
            return;
        }

        // Model có Leak luôn chạy Leak trước. Mỗi connector đã gán chỉ cần
        // một pin tạo cạnh continuity là đủ khởi động máy Leak; toàn bộ mạng
        // dây vẫn phải PASS ở pha Continuity kế tiếp.
        if (_cycleActive &&
            phase == ProductionPhase.Continuity &&
            IsWaterProofEnabledForCurrentModel() &&
            Volatile.Read(ref _preContinuityWaterProofPassed) == 0 &&
            (rowsSnapshot?.Electrical.ProductEvidence ?? _engine.HasProductActivity))
        {
            if (!TryValidateWaterProofConnectorGate(_model!, out string connectorGateError))
            {
                State = connectorGateError.Contains("không tồn tại", StringComparison.OrdinalIgnoreCase) ||
                        connectorGateError.Contains("chưa chọn", StringComparison.OrdinalIgnoreCase)
                    ? "LỖI CẤU HÌNH LEAK"
                    : connectorGateError;
                return;
            }

            if (Interlocked.CompareExchange(ref _preContinuityWaterProofStarted, 1, 0) == 0)
            {
                CaptureProductTestStartedAt();
                _productDetectedThisCycle = true;
                SetProductionPhase(ProductionPhase.WaterProof);
                _ = RunPreContinuityWaterProofAsync(_model!, generation);
            }
            return;
        }

        if (_cycleActive &&
            phase == ProductionPhase.Continuity &&
            _engine.ReadyToEvaluateProductFaults &&
            _engine.HasProductActivity)
        {
            CaptureProductTestStartedAt();
        }

        if (_cycleActive &&
            phase == ProductionPhase.Continuity &&
            _engine.LastFrameValid &&
            _engine.HasWiringFault &&
            Interlocked.CompareExchange(ref _wiringFaultHandlingStarted, 1, 0) == 0)
        {
            IReadOnlyCollection<WiringFaultPair> wiringFaults = _engine.WiringFaults;
            int faultCount = wiringFaults.Count;
            string faultType = FaultTypeCatalog.Code(wiringFaults.FirstOrDefault()?.FaultType ?? ProductFaultType.WrongWiring);
            double cycleFaultMs = Math.Max(0, (DateTime.Now - _cycleStartedAt).TotalMilliseconds);
            AsyncFileLogService.Current.Performance(
                $"FAULT_CONFIRMATION_LATENCY type={faultType} cycle_elapsed_ms={cycleFaultMs:0.###} " +
                $"frame={_engine.LastFrameSequence} count={faultCount}");
            AddLog(
                "[FAIL-AUDIT] " +
                $"CycleId={_activeCycleId} Generation={generation} Mode=Production State={State} " +
                $"ReadyToTest={_engine.ReadyToEvaluateProductFaults} FrameValid={_engine.LastFrameValid} " +
                $"FrameId={_engine.LastFrameSequence} FaultType={faultType} FaultCount={faultCount} " +
                $"ResultCommitted={Volatile.Read(ref _resultRecordedThisCycle) != 0} Reason=ConfirmedProductFault");
            _ = HandleWiringFaultAsync(generation);
            return;
        }
        else if (_engine.HasWiringFault)
        {
            AddFaultGateSuppressedLog(phase);
        }

        if (_cycleActive && _engine.HasContactInstability)
        {
            _sound.SetWiringFaultAlarm(false);
            Interlocked.Exchange(ref _postContinuityStarted, 0);
            bool hasProductEvidence = rowsSnapshot?.Electrical.ProductEvidence ??
                                      _engine.HasProductActivity;
            if (hasProductEvidence)
                State = "TIẾP XÚC JIG/PROBE KHÔNG ỔN ĐỊNH — KIỂM TRA PROBE PIN/JIG";

            if (!_engine.HasProductActivity && _productDetectedThisCycle)
            {
                // Complete frame hiện tại đã xác nhận mất TOÀN BỘ connectivity
                // model-aware. Reset ngay trong cùng UI update; không chờ cửa
                // sổ contact-loss vì operator cần lắp/tháo phản hồi tức thời.
                // ResetProductCycle phải xóa cả latch WireNet và CLIP AO/A0-aN.
                // Nếu còn dù chỉ một cạnh dây thường hoặc CLIP thì
                // HasProductActivity vẫn true và tuyệt đối không vào nhánh này.
                ResetFullCycleAfterProductRemoved();
                State = "LẮP SẢN PHẨM";
                AddLog("Complete frame đã xác nhận tháo hoàn toàn; reset ngay dây thường và toàn bộ nhánh CLIP để lắp lại từ đầu.");
            }
            else if (_engine.HasProductActivity && !_productDetectedThisCycle)
            {
                _cycleStartedAt = DateTime.Now;
                _productDetectedThisCycle = true;
                RecordProbeCycleStarted();
            }

            return;
        }

        // Chỉ khi không có lỗi mới cập nhật trạng thái lắp sản phẩm.
        if (_cycleActive)
        {
            bool hasActivity = rowsSnapshot?.Electrical.ProductEvidence ??
                               _engine.HasProductActivity;

            if (hasActivity)
            {
                if (!_productDetectedThisCycle)
                {
                    _cycleStartedAt = DateTime.Now;
                    RecordProbeCycleStarted();
                }
                _productDetectedThisCycle = true;
                if (!State.Equals("PASS", StringComparison.OrdinalIgnoreCase))
                    State = "ĐANG KIỂM TRA...";
            }
            else if (_productDetectedThisCycle)
            {
                // Không suy diễn "mất hết activity" thành OPEN product.
                // Confirmation gate sẽ phân nhánh contact warning/re-evaluation.
                State = "TIẾP XÚC JIG/PROBE KHÔNG ỔN ĐỊNH — KIỂM TRA PROBE PIN/JIG";
            }
        }

        if (_cycleActive &&
            phase == ProductionPhase.Continuity &&
            _engine.ContinuityPassed &&
            !_engine.HasWiringFault &&
            _engine.ReadyToEvaluateProductFaults &&
            Interlocked.CompareExchange(ref _postContinuityStarted, 1, 0) == 0)
        {
            AsyncFileLogService.Current.Performance(
                $"AUTO_RESISTANCE_TRIGGER continuity_complete={_engine.ContinuityPassed} " +
                $"resistance_enabled={IsResistanceEnabledForModel(_model)} scan_running={_board.IsScanning}");
            _ = RunAutomaticPostContinuityAsync();
        }
    }

    private void ApplyAuthoritativeProductionState(ProductionElectricalSnapshot electrical)
    {
        ProductionRuntimeState runtimeState = CurrentProductionPhase switch
        {
            ProductionPhase.WaitingProduct => ProductionRuntimeState.WaitingForProduct,
            ProductionPhase.WaitingProductRemoval => ProductionRuntimeState.WaitingForRemoval,
            ProductionPhase.WaitingFaultConfirmation or ProductionPhase.EquipmentError =>
                ProductionRuntimeState.Failed,
            ProductionPhase.Resistance or ProductionPhase.WaterProof or ProductionPhase.Completed =>
                ProductionRuntimeState.PassSequence,
            _ when electrical.HasConfirmedWiringFault => ProductionRuntimeState.Failed,
            _ when electrical.ProductEvidence => ProductionRuntimeState.TestingRealtime,
            _ => ProductionRuntimeState.WaitingForProduct
        };

        SetProductionRuntimeState(
            runtimeState,
            electrical.FrameSequence,
            electrical.ProductEvidence ? "PRODUCT_EVIDENCE" : "NO_PRODUCT_EVIDENCE");
        SetProductionPresentationMode(
            runtimeState == ProductionRuntimeState.WaitingForProduct
                ? ProductionPresentationMode.Waiting
                : ProductionPresentationMode.Product,
            electrical.FrameSequence,
            "ENGINE_SNAPSHOT");
        if (!_cycleActive || CurrentProductionPhase != ProductionPhase.Continuity ||
            _waitForProductRelease || _waitForFaultProductRemoval || IsProductRemovalPending)
        {
            return;
        }

        // The complete engine snapshot is the only owner of the normal
        // Waiting/Testing operator message. A raw ActiveIo callback is never
        // allowed to overwrite this state later on the Dispatcher.
        State = runtimeState == ProductionRuntimeState.TestingRealtime
            ? "ĐANG KIỂM TRA..."
            : "CHỜ LẮP SẢN PHẨM";
    }

    private void CaptureProductTestStartedAt()
    {
        if (_cycleTestStartedAt.HasValue)
            return;

        DateTime now = DateTime.Now;
        if (!_productDetectedThisCycle)
        {
            _cycleStartedAt = now;
            _productDetectedThisCycle = true;
            RecordProbeCycleStarted();
        }

        _cycleTestStartedAt = now < _cycleStartedAt ? _cycleStartedAt : now;
        _ = PersistActiveCycleStageAsync("TEST_STARTED");
    }

    private void MarkProductRemovalStarted()
    {
        DateTime removalStarted = _cycleRemovalStartedAt ?? DateTime.Now;
        _cycleRemovalStartedAt = removalStarted;

        TestHistoryStore? store = _recordedHistoryStore;
        string cycleId = _recordedHistoryCycleId;
        if (store is null || string.IsNullOrWhiteSpace(cycleId))
            return;

        _removalPersistenceTask = PersistRemovalTimingAsync(
            cycleId,
            removalStarted,
            removedAt: null,
            "HISTORY_REMOVAL_START");
    }

    private void MarkProductRemoved()
    {
        TestHistoryStore? store = _recordedHistoryStore;
        string cycleId = _recordedHistoryCycleId;
        if (store is null || string.IsNullOrWhiteSpace(cycleId))
            return;

        DateTime removedAt = DateTime.Now;
        DateTime removalStarted = _cycleRemovalStartedAt ?? removedAt;
        _cycleRemovalStartedAt = removalStarted;

        _removalPersistenceTask = PersistRemovalTimingAsync(
            cycleId,
            removalStarted,
            removedAt,
            "HISTORY_REMOVAL_COMPLETE");
    }

    private async Task PersistRemovalTimingAsync(
        string cycleId,
        DateTime removalStarted,
        DateTime? removedAt,
        string performanceMarker)
    {
        await _removalPersistenceGate.WaitAsync();
        try
        {
            long timestamp = Stopwatch.GetTimestamp();
            bool updated = await ProductionPersistence.UpdateRemovalTimingAsync(
                cycleId, removalStarted, removedAt, _lifetimeCts.Token);
            if (!updated)
                return;

            double durationMs = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
            AsyncFileLogService.Current.Performance(
                $"{performanceMarker} cycle={cycleId} db_ms={durationMs:0.###}");
            if (removedAt.HasValue)
                AddLog($"History: cycle {cycleId} đã lưu xác nhận tháo toàn bộ sản phẩm.");
        }
        catch (Exception ex)
        {
            string action = removedAt.HasValue ? "tháo hoàn tất" : "bắt đầu tháo";
            AddLog($"Không thể lưu thời điểm {action} cho cycle {cycleId}: {ex.Message}");
        }
        finally
        {
            _removalPersistenceGate.Release();
        }
    }

    private static DateTime? NormalizeCycleTimestamp(DateTime? value, DateTime lower, DateTime upper)
    {
        if (value is not DateTime timestamp || timestamp < lower || timestamp > upper)
            return null;
        return timestamp;
    }

    private void ResetCycleInspectionTrace()
    {
        Interlocked.Exchange(ref _preContinuityWaterProofStarted, 0);
        Interlocked.Exchange(ref _preContinuityWaterProofPassed, 0);
        Interlocked.Exchange(ref _waterProofFailureFinalizing, 0);
        _cycleContinuityCompletedAt = null;
        _cycleResistanceStartedAt = null;
        _cycleResistanceCompletedAt = null;
        _cycleWaterProofStartedAt = null;
        _cycleWaterProofCompletedAt = null;
        _cycleWaterProofSummary = string.Empty;
        _lastWaterProofMeasurements = [];
    }

    private async Task PersistActiveCycleStageAsync(string stage)
    {
        ProductModel? model = _model;
        string cycleId = _activeCycleId;
        if (model is null || string.IsNullOrWhiteSpace(cycleId))
            return;
        try
        {
            await ProductionPersistence.UpsertActiveCycleAsync(
                cycleId,
                model.PartNumber,
                model.SourcePath,
                _cycleStartedAt,
                stage,
                _lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AddLog($"Không thể lưu tiến trình cycle {cycleId}/{stage}: {ex.Message}");
        }
    }

    private void ResetFullCycleAfterProductRemoved()
    {
        // Invalidate every queued presentation callback from the product that
        // has just been removed before Reset() emits its synchronous Changed.
        AdvanceProductionUiCycleEpoch();
        ResetManualProbeSession("product-removed");
        _engine.ResetProductCycle();
        Interlocked.Exchange(ref _wiringFaultHandlingStarted, 0);
        Interlocked.Exchange(ref _postContinuityStarted, 0);
        Interlocked.Exchange(ref _waterProofRunning, 0);
        Interlocked.Exchange(
            ref _waterProofRetestConnectorState,
            (int)WaterProofRetestConnectorState.Inactive);
        Interlocked.Exchange(ref _waterProofRetestAttempt, 0);
        Interlocked.Exchange(ref _resultRecordedThisCycle, 0);
        Interlocked.Exchange(ref _probeCycleRecordedThisCycle, 0);
        _cycleActive = true;
        SetProductionPhase(ProductionPhase.Continuity);
        _waterProofEquipmentErrorAwaitingRemoval = false;
        _productDetectedThisCycle = false;
        SetProductionRuntimeState(ProductionRuntimeState.WaitingForProduct);
        ResetProductPresentationCycle();
        Interlocked.Exchange(ref _productStartSoundPlayed, 0);
        _lastFaultRejectSignature = string.Empty;
        _lastPassGateSignature = string.Empty;
        _lastFaultGateSignature = string.Empty;
        _lastFaultGateSuppressedSignature = string.Empty;
        _lastPassRemainingSignature = string.Empty;
        _lastProductEvidenceSignature = string.Empty;
        _activeCycleId = Guid.NewGuid().ToString("N");
        _cycleStartedAt = DateTime.Now;
        _cycleTestStartedAt = null;
        _cycleRemovalStartedAt = null;
        ResetCycleInspectionTrace();
        _recordedHistoryCycleId = string.Empty;
        _recordedHistoryStore = null;
        UpdateDailyLotDisplay();
        Resistance.Clear();
        ResistanceDisplayRows.Clear();
        ResetWaterProofDisplay();
        SelectedOperationTabIndex = 0;
        ClearInlineProbeContactsState(clearLastSeen: true);
        InvokeUi(ClearInlineProbeDisplay);
        RefreshFaults();
        RaiseActiveFault();
    }

    private Task InvokeUiAsync(Action action)
    {
        if (Application.Current?.Dispatcher is null || Application.Current.Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Application.Current.Dispatcher.InvokeAsync(action).Task;
    }

    private void OnBoardLog(object? sender, string text)
    {
        AppLogLevel level = text.StartsWith("RX frame", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("TX ", StringComparison.OrdinalIgnoreCase)
            ? AppLogLevel.Diagnostic
            : AppLogLevel.Normal;
        AsyncFileLogService.Current.Board(text, level);
    }

    private void OnWaterProofLog(object? sender, string text)
    {
        AsyncFileLogService.Current.Board($"WATERPROOF {text}", AppLogLevel.Normal);
        AddLog($"[WATERPROOF] {text}");
    }

    private static bool IsContinuityPreviewFrame(ScanFrame frame) =>
        frame.Mode == BoardScanMode.Production &&
        !frame.Complete &&
        frame.UnknownBytes == 0 &&
        frame.SourceCount == 1 &&
        frame.Connections.Count == 1 &&
        frame.EndMarkerCode is null &&
        !frame.TerminatorKnown;

    private bool TryHandleContinuityPreviewFrame(ScanFrame frame)
    {
        if (!IsContinuityPreviewFrame(frame))
            return false;

        // The sentinel frame is presentation-only. Always consume it here so it
        // can never reach discard/startup/probe/PASS/FAIL/relay logic below.
        if (!_board.IsScanning ||
            !IsRuntimeMode(RuntimeMode.Production) ||
            Volatile.Read(ref _probeSessionActive) != 0 ||
            !MasterApproved ||
            IsIoMappingMode ||
            CurrentProductionPhase is not (ProductionPhase.Continuity or ProductionPhase.WaitingProductRemoval))
        {
            return true;
        }

        var relation = frame.Connections.First();
        int sourceIo = relation.Key;

        IReadOnlyCollection<int> targets = relation.Value.ToArray();
        if (_model is { DiscardContactIo.Count: > 0 } discardModel)
        {
            if (discardModel.DiscardContactIo.Contains(sourceIo))
                return true;

            targets = targets
                .Where(io => !discardModel.DiscardContactIo.Contains(io))
                .ToArray();
        }

        if (!_engine.ApplyContinuityPreviewSource(sourceIo, targets, frame.Sequence))
            return true;

        long generation = Volatile.Read(ref _runtimeGeneration);
        if (!_presentationCycleStarted &&
            CurrentProductionPhase == ProductionPhase.Continuity &&
            !IsProbeOwningProductionPresentation() &&
            _engine.HasContinuityPreviewProductActivity)
        {
            // Lần lắp đầu tiên không được chờ C0 của toàn bộ dải 4/10 card.
            // Preview đã được TestEngine lọc về đúng expected product edge và
            // chỉ được phép đổi presentation, không PASS/FAIL/counter/relay.
            long cycleEpoch = Volatile.Read(ref _productionUiCycleEpoch);
            InvokeUi(() =>
            {
                if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
                    cycleEpoch != Volatile.Read(ref _productionUiCycleEpoch) ||
                    _presentationCycleStarted ||
                    CurrentProductionPhase != ProductionPhase.Continuity ||
                    IsProbeOwningProductionPresentation() ||
                    IsProductRemovalPending)
                {
                    return;
                }

                _presentationCycleStarted = true;
                RaiseCenterPresentation();
                State = "ĐANG TEST";
            });
        }
        QueueContinuityPreviewUi(generation);
        return true;
    }

    private void QueueContinuityPreviewUi(long generation)
    {
        Volatile.Write(ref _continuityPreviewUiGeneration, generation);
        Volatile.Write(ref _continuityPreviewUiCycleEpoch, Volatile.Read(ref _productionUiCycleEpoch));
        Volatile.Write(ref _continuityPreviewUiSequence, _engine.ContinuityPreviewSequence);
        Interlocked.Increment(ref _continuityPreviewUiRevision);
        if (Interlocked.CompareExchange(ref _continuityPreviewUiQueued, 1, 0) != 0)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            ProcessContinuityPreviewUi();
        else
            dispatcher.BeginInvoke(ProcessContinuityPreviewUi, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ProcessContinuityPreviewUi()
    {
        long revision = Volatile.Read(ref _continuityPreviewUiRevision);
        long generation = Volatile.Read(ref _continuityPreviewUiGeneration);
        long cycleEpoch = Volatile.Read(ref _continuityPreviewUiCycleEpoch);
        long sequence = Volatile.Read(ref _continuityPreviewUiSequence);
        try
        {
            if (IsRuntimeContext(RuntimeMode.Production, generation) &&
                cycleEpoch == Volatile.Read(ref _productionUiCycleEpoch) &&
                (sequence <= 0 || _engine.LastFrameSequence <= 0 ||
                 sequence >= _engine.LastFrameSequence) &&
                Volatile.Read(ref _probeSessionActive) == 0 &&
                !IsIoMappingMode &&
                _presentationCycleStarted &&
                CurrentProductionPhase is ProductionPhase.Continuity or ProductionPhase.WaitingProductRemoval)
            {
                // RefreshFaults only synchronizes presentation rows. It does not
                // evaluate TestEngine state or trigger result/relay side effects.
                RefreshFaults();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _continuityPreviewUiQueued, 0);
            if (Volatile.Read(ref _continuityPreviewUiRevision) != revision &&
                Interlocked.CompareExchange(ref _continuityPreviewUiQueued, 1, 0) == 0)
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.CheckAccess())
                    ProcessContinuityPreviewUi();
                else
                    dispatcher.BeginInvoke(ProcessContinuityPreviewUi, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }

    private void OnBoardFrameReceived(object? sender, ScanFrame frame)
    {
        if (IsDeviceFault || !_board.IsConnected)
            return;

        if (TryHandleContinuityPreviewFrame(frame))
            return;

        bool restoreBoardPresentation = false;

        // BoardFrameActivity chỉ là telemetry cho UI LED. Subscriber presentation
        // không được phép làm gián đoạn hot path nhận/giải mã frame của bo.
        try
        {
            BoardFrameActivity?.Invoke(this, frame);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"BoardFrameActivity observer error: {ex}");
        }

        try
        {
            RuntimeMode mode = CurrentRuntimeMode;
            long generation = Volatile.Read(ref _runtimeGeneration);
            if (mode == RuntimeMode.Production &&
                frame.Mode == BoardScanMode.Production &&
                Interlocked.CompareExchange(ref _firstFrameReceivedLogged, 1, 0) == 0)
            {
                AsyncFileLogService.Current.Performance(
                    $"FIRST_FRAME_RECEIVED seq={frame.Sequence} complete={frame.Complete}");
            }

            if (frame.Mode == BoardScanMode.Production &&
                frame.Complete &&
                frame.UnknownBytes == 0 &&
                frame.Sequence > 0)
            {
                Volatile.Write(ref _lastObservedProductionFrameSequence, frame.Sequence);
                Volatile.Write(ref _lastObservedProductionScanGeneration, frame.ScanGeneration);
                restoreBoardPresentation =
                    Interlocked.Exchange(ref _boardUnavailablePresentationApplied, 0) != 0;
            }

            // _DISCARD là cặp tiếp điểm thùng hàng lỗi, không phải topology sản
            // phẩm. Quan sát frame raw để phát âm/UI/interlock, rồi loại hai I/O
            // cảm biến này trước mọi startup/probe/continuity/fault engine.
            bool discardBlocksProduction = false;
            if (frame.Mode == BoardScanMode.Production &&
                _model is { DiscardContactIo.Count: > 0 } discardModel)
            {
                if (discardModel.HasDiscardInterlock)
                    discardBlocksProduction = ProcessDiscardContactFrame(
                        frame,
                        discardModel,
                        generation,
                        mode);
                frame = DiscardContactInterlock.RemoveDiscardIo(
                    frame,
                    discardModel.DiscardContactIo);
            }

            // Một lần đưa hàng qua _DISCARD ngoài chu trình FAIL khóa scan logic
            // trong lúc cảm biến đang tác động. Khi cảm biến nhả, lượt 1/1 hoàn tất
            // và Production được mở khóa lại.
            if (discardBlocksProduction)
                return;

            // MainWindow vẫn giữ scan D2XX chạy nền. Dùng snapshot hoàn chỉnh này
            // để khóa chọn mã/START nếu còn bất kỳ tiếp điểm sản phẩm hoặc pin kẹt,
            // nhưng tuyệt đối không đưa frame nền vào fault engine hay relay flow.
            if (mode == RuntimeMode.Background && frame.Mode == BoardScanMode.Production)
            {
                // Cửa sổ QUÉT/HỌC MÃ là observer riêng của cùng stream. Không
                // diễn giải frame chạm GND ở đây lần thứ hai thành các cặp sản phẩm.
                if (Volatile.Read(ref _topologyLearningActive) == 0)
                    HandleBackgroundProductRemovalInterlock(frame, generation);
                return;
            }

            // V12.9.2: router duy nhất + cập nhật Probe theo snapshot/event hiện tại.
            // Không Task.Delay, không DispatcherTimer và không TTL để giữ contact cũ.
            if (mode == RuntimeMode.Probe)
            {
                if (Volatile.Read(ref _probeSessionActive) != 0 &&
                    frame.Mode == BoardScanMode.Probe)
                {
                    int[] probeIos = frame.ActiveIo
                        .Where(_board.Capacity.ContainsGlobalIo)
                        .Distinct()
                        .Take(2)
                        .OrderBy(value => value)
                        .ToArray();

                    bool hadTrackedContacts = _probeStateTracker.HasTrackedContacts;
                    bool changed = probeIos.Length > 0
                        ? UpdateInlineProbeContacts(probeIos)
                        : ClearInlineProbeContactsState();
                    long probeRevision = (!hadTrackedContacts || changed)
                        ? Interlocked.Increment(ref _inlineProbeUiRevision)
                        : Volatile.Read(ref _inlineProbeUiRevision);

                    if (changed)
                    {
                        DateTime requestedAt = DateTime.Now;
                        InvokeUi(() =>
                        {
                            if (!IsRuntimeContext(RuntimeMode.Probe, generation) ||
                                Volatile.Read(ref _probeSessionActive) == 0 ||
                                probeRevision != Volatile.Read(ref _inlineProbeUiRevision))
                            {
                                return;
                            }

                            if (probeIos.Length > 0)
                                ShowInlineProbeContacts(probeIos);
                            else
                                ClearInlineProbeDisplay();

                            LogProbeLatency(frame, requestedAt, probeIos);
                        });
                    }

                    ScanFrameReceived?.Invoke(this, frame);
                }

                return;
            }

            if (mode == RuntimeMode.Production &&
                Volatile.Read(ref _probeSessionActive) == 0 &&
                frame.Mode == BoardScanMode.Production)
            {
                Interlocked.Increment(ref _productionFramesReceived);

                if (Volatile.Read(ref _freshFrameGateActive) != 0)
                {
                    long cycleStartSequence = Volatile.Read(ref _cycleStartFrameSequence);
                    long cycleStartGeneration = Volatile.Read(ref _cycleStartScanGeneration);
                    bool sameScanSession = cycleStartGeneration == 0 ||
                                           frame.ScanGeneration == 0 ||
                                           frame.ScanGeneration == cycleStartGeneration;
                    if (cycleStartSequence > 0 &&
                        sameScanSession &&
                        frame.Sequence > 0 &&
                        frame.Sequence <= cycleStartSequence)
                    {
                        if (Interlocked.CompareExchange(ref _stalePreCycleFrameLogged, 1, 0) == 0)
                        {
                            AsyncFileLogService.Current.Performance(
                                $"PASS_GATE seq={frame.Sequence} cycleStartSeq={cycleStartSequence} " +
                                $"generation={frame.ScanGeneration}/{cycleStartGeneration} scanMode={frame.Mode} " +
                                $"frameComplete={frame.Complete} reason=STALE_PRE_CYCLE_FRAME action=ignored");
                        }

                        Interlocked.Increment(ref _productionFramesDropped);
                        LogContinuousScanMetricsIfDue();
                        return;
                    }

                    Interlocked.Exchange(ref _freshFrameGateActive, 0);
                    AsyncFileLogService.Current.Performance(
                        $"FRESH_FRAME_ACCEPTED seq={frame.Sequence} cycleStartSeq={cycleStartSequence} " +
                        $"generation={frame.ScanGeneration}/{cycleStartGeneration}");
                }

                // Probe classification phải hoàn tất trước mọi ProductEvidence,
                // startup product interlock và wiring evaluator. Preview candidate
                // cùng sequence/generation cũng được quarantine tại đây.
                bool frameClassifiedAsProbe =
                    TryDetectInlineProbeContacts(frame, out int[] touchedIos);
                bool probeTransitionPendingAtFrameStart =
                    _probeStateTracker.HasTrackedContacts;

                // Không có eligible expected network là LiveTopology mode, không
                // phụ thuộc tên file/kích thước hay cờ parser của một biến thể THT.
                if (IsIoMappingMode)
                {
                    ProcessIoMappingFrame(
                        frame,
                        generation,
                        frameClassifiedAsProbe ? touchedIos : Array.Empty<int>());
                    Interlocked.Increment(ref _productionFramesProcessed);
                    LogContinuousScanMetricsIfDue();
                    return;
                }

                if (!frameClassifiedAsProbe &&
                    !probeTransitionPendingAtFrameStart &&
                    !HandleStartupIoInterlock(frame, generation))
                {
                    LogContinuousScanMetricsIfDue();
                    return;
                }

                // Probe là lớp quan sát SONG SONG. Snapshot có chữ ký fan-in
                // mạnh được cô lập khỏi TestEngine; fault Production đã có
                // vẫn giữ nguyên. UI chỉ đổi khi tracker đổi state.
                bool probeChanged;
                bool preserveProductionFaultsForProbe = false;
                int[] displayedProbeIos;
                if (_manualProbeSession.IsActive)
                {
                    preserveProductionFaultsForProbe = ProcessManualProbeFrame(frame, generation);
                }
                else if (frameClassifiedAsProbe)
                {
                    Interlocked.Increment(ref _productionFramesRoutedToProbe);
                    preserveProductionFaultsForProbe = true;
                    bool hadTrackedContacts = _probeStateTracker.HasTrackedContacts;
                    probeChanged = UpdateInlineProbeContacts(touchedIos);
                    displayedProbeIos = SnapshotInlineProbeContacts();
                    bool probeTrackingStarted =
                        !hadTrackedContacts && _probeStateTracker.HasTrackedContacts;
                    if (probeTrackingStarted && !probeChanged)
                        SetProbePresentationState(ProbePresentationState.Candidate, touchedIos, frame.Sequence);
                    if (probeChanged && displayedProbeIos.Length > 0)
                        SetProbePresentationState(ProbePresentationState.Touch, displayedProbeIos, frame.Sequence);
                    long probeRevision = (probeTrackingStarted || probeChanged)
                        ? Interlocked.Increment(ref _inlineProbeUiRevision)
                        : Volatile.Read(ref _inlineProbeUiRevision);

                    // Nếu một frame đầu của cùng thao tác chạm đã kịp tạo candidate
                    // WRONG/SHORT trước khi đủ chữ ký classifier, xóa đúng các fault
                    // liên quan Pin đầu dò. Fault thật ở I/O khác vẫn được giữ nguyên.
                    _engine.SuppressProbeRelatedWiringFaults(touchedIos);
                    if (!_engine.HasWiringFault)
                    {
                        Interlocked.Exchange(ref _wiringFaultHandlingStarted, 0);
                        _sound.SetWiringFaultAlarm(false);
                    }

                    TestEnginePresentationSnapshot probeSnapshot =
                        _engine.CapturePresentationSnapshot(removal: false);
                    if (probeChanged)
                    {
                        DateTime requestedAt = DateTime.Now;
                        InvokeUi(() =>
                        {
                            if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
                                Volatile.Read(ref _probeSessionActive) != 0 ||
                                probeRevision != Volatile.Read(ref _inlineProbeUiRevision))
                            {
                                return;
                            }

                            ApplyProbePresentationSnapshot(
                                probeSnapshot,
                                displayedProbeIos,
                                released: false,
                                frame.Sequence);
                            LogProbeLatency(frame, requestedAt, displayedProbeIos);
                        });
                    }
                }
                else
                {
                    // Một frame chuyển tiếp có thể tụt dưới ngưỡng fan-in trước
                    // khi contact biến mất hoàn toàn. Giữ cách ly cho tới khi
                    // tracker nhận đủ RELEASE frame, nếu không frame đuôi này sẽ
                    // lọt sang ProductDetect/WRONG_CANDIDATE.
                    bool probeTransitionPending = _probeStateTracker.HasTrackedContacts;
                    bool directConnectionEvidence =
                        ProbeContactClassifier.HasDirectConnectionEvidence(frame);
                    bool discardContactClosed =
                        Volatile.Read(ref _discardContactClosed) != 0 &&
                        _model is { HasDiscardInterlock: true };
                    probeChanged = discardContactClosed
                        ? false
                        : UpdateInlineProbeContacts(Array.Empty<int>());
                    // Trace Htdrv: sau khi nhấc đầu dò, dây chập còn lại xuất hiện
                    // ngay thành cạnh low-fan-in (hit=1). Không được để debounce
                    // RELEASE nuốt các frame điện thật đầu tiên của thao tác ngắn.
                    preserveProductionFaultsForProbe =
                        probeTransitionPending && !directConnectionEvidence;
                    if (preserveProductionFaultsForProbe)
                        Interlocked.Increment(ref _productionFramesRoutedToProbe);

                    if (probeChanged)
                    {
                        SetProbePresentationState(
                            ProbePresentationState.Released,
                            Array.Empty<int>(),
                            frame.Sequence);
                        long probeRevision = Interlocked.Increment(ref _inlineProbeUiRevision);
                        TestEnginePresentationSnapshot probeSnapshot =
                            _engine.CapturePresentationSnapshot(removal: false);
                        DateTime requestedAt = DateTime.Now;
                        InvokeUi(() =>
                        {
                            if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
                                Volatile.Read(ref _probeSessionActive) != 0 ||
                                probeRevision != Volatile.Read(ref _inlineProbeUiRevision))
                            {
                                return;
                            }

                            ApplyProbePresentationSnapshot(
                                probeSnapshot,
                                Array.Empty<int>(),
                                released: true,
                                frame.Sequence);
                            LogProbeLatency(frame, requestedAt, Array.Empty<int>());
                        });
                    }
                }

                long processStarted = Stopwatch.GetTimestamp();
                // Drop only the presentation overlay immediately before the
                // authoritative C0 snapshot is evaluated. PASS/FAIL state itself
                // has never been changed by the preview path.
                bool continuityPreviewCleared = false;
                bool engineChanged = false;
                // Product presence comes only from model-aware connectivity in
                // the complete engine snapshot. Raw/self-edge activity cannot
                // move the operator UI into the Testing state.
                // Chữ ký Probe mạnh chỉ là lớp quan sát. Không được
                // đổi LẮP SẢN PHẨM -> ĐANG TEST chỉ vì target đầu dò.
                if (!preserveProductionFaultsForProbe)
                {
                    _engine.ClearProbeEvidenceExclusions();
                    continuityPreviewCleared = _engine.ClearContinuityPreview();
                    engineChanged = _engine.ProcessFrame(frame, false);
                }
                Interlocked.Increment(ref _productionFramesProcessed);
                if (continuityPreviewCleared && !engineChanged)
                    QueueContinuityPreviewUi(generation);
                if (!preserveProductionFaultsForProbe && restoreBoardPresentation && !engineChanged)
                {
                    // UI đã xóa snapshot lúc đổi lifecycle. Frame đầu tiên của phiên mới
                    // phải dựng lại presentation kể cả topology vật lý không đổi.
                    OnEngineChanged(_engine, EventArgs.Empty);
                }
                PlayProductStartSoundOnce(generation, preserveProductionFaultsForProbe);
                if (preserveProductionFaultsForProbe)
                    LogProbeProductEvidence(frame);
                double processMs = Stopwatch.GetElapsedTime(processStarted).TotalMilliseconds;
                // Diagnostic topology có thể lớn hàng trăm network. Chỉ dựng khi
                // trạng thái logic đổi; frame giống hệt vẫn đi qua debounce engine.
                if (engineChanged)
                    LogPassGateAfterProductionFrame(frame, processMs);
                LogContinuousScanMetricsIfDue();
                if (processMs > 50)
                    AsyncFileLogService.Current.Performance(
                        $"HOT_PATH_WARNING phase=TestEngine.ProcessFrame seq={frame.Sequence} duration_ms={processMs:0.###}");
                if (Interlocked.CompareExchange(ref _firstLogicalStateLogged, 1, 0) == 0)
                {
                    AsyncFileLogService.Current.Performance(
                        $"FIRST_LOGICAL_STATE_READY seq={frame.Sequence} duration_ms={processMs:0.###}");
                }
            }

            // Background/ShuttingDown: chỉ quét nền, không test và không tạo lỗi.
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            EnterDeviceFault(ex, "BoardFrameReceived");
        }
    }

    private void OnProductionProbePreviewReceived(
        object? sender,
        ProductionProbePreview preview)
    {
        if (IsDeviceFault ||
            !_board.IsScanning ||
            !IsRuntimeMode(RuntimeMode.Production) ||
            Volatile.Read(ref _probeSessionActive) != 0)
        {
            return;
        }

        int[] probeIos = preview.ActiveIo
            .Where(_board.Capacity.ContainsGlobalIo)
            .Distinct()
            .Take(2)
            .OrderBy(value => value)
            .ToArray();
        if (probeIos.Length == 0)
            return;

        // Preview trước C0 vừa quarantine complete frame sắp tới, vừa sở hữu
        // presentation ngay. Đây chỉ là UI; Product state/engine/sound giữ nguyên.
        lock (_probePreviewGate)
            _pendingProductionProbePreview = preview with { ActiveIo = probeIos };
        long probeRevision = Interlocked.Increment(ref _inlineProbeUiRevision);
        SetProbePresentationState(
            ProbePresentationState.Candidate,
            probeIos,
            preview.Sequence);
        long generation = Volatile.Read(ref _runtimeGeneration);
        InvokeUi(() =>
        {
            if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
                Volatile.Read(ref _probeSessionActive) != 0 ||
                probeRevision != Volatile.Read(ref _inlineProbeUiRevision))
            {
                return;
            }

            ShowProductionProbePreview(probeIos, preview.Sequence);
        });
        AsyncFileLogService.Current.Performance(
            $"PROBE_PREVIEW candidate={string.Join(",", probeIos.Select(io => $"IO{io}"))} " +
            $"seq={preview.Sequence} hits={preview.PeakHitCount}/{preview.RequiredHitCount}",
            AppLogLevel.Diagnostic);
    }

    private void ArmFaultProductRemoval(ProductModel model)
    {
        _waitForFaultProductRemoval = true;
        Interlocked.Exchange(ref _faultProductRemoved, 0);
        SetProductRemovalPending(true);
        Interlocked.Exchange(ref _removalMonitoringFromMain, 0);
        SetProductionPhase(ProductionPhase.WaitingProductRemoval);

        if (model.HasDiscardInterlock)
        {
            Interlocked.Exchange(ref _discardRequiredForFault, 1);
            bool currentlyClosed = Volatile.Read(ref _discardContactClosed) != 0;
            _discardInterlock.Arm(currentlyClosed);
            AddLog(
                $"[DISCARD] Đã ARM cặp IO({model.DiscardContactIo[0]})-IO({model.DiscardContactIo[1]}); " +
                (currentlyClosed
                    ? "cảm biến đang tác động khi ARM; chờ nhả làm baseline rồi đưa hàng qua 1 lần."
                    : "chờ đưa hàng qua cảm biến 1 lần (tác động rồi nhả)."));
        }
        else
        {
            Interlocked.Exchange(ref _discardRequiredForFault, 0);
            _discardInterlock.Reset();
        }
    }

    private void ShowFaultConfirmationDialog(
        IReadOnlyList<FaultDetail> faults,
        ProductModel model,
        Window? owner = null)
    {
        var dialog = new JBZUniversalTester.Views.FaultConfirmationWindow(
            faults,
            model.HasDiscardInterlock
                ? "Bấm XÁC NHẬN để mở JIG. Sau đó đưa hàng lỗi qua cảm biến thùng NG 1 lần."
                : "Bấm XÁC NHẬN để mở đầu gá và tháo sản phẩm.",
            FindPinByIo);
        Window? resolvedOwner = owner ?? ResolveOperatorDialogOwner();
        if (resolvedOwner is not null)
            dialog.Owner = resolvedOwner;
        dialog.ShowDialog();
    }

    private static string FaultRemovalWaitingText(ProductModel model) =>
        model.HasDiscardInterlock
            ? "CHỜ XÁC NHẬN THÙNG LỖI"
            : "CHỜ THÁO SẢN PHẨM";

    private void TryCompleteFaultProductRemoval()
    {
        if (!_waitForFaultProductRemoval ||
            Volatile.Read(ref _faultProductRemoved) == 0)
        {
            return;
        }

        bool discardRequired = Volatile.Read(ref _discardRequiredForFault) != 0;
        if (discardRequired && !_discardInterlock.IsCompleted)
        {
            State = "CHỜ XÁC NHẬN THÙNG LỖI";
            return;
        }

        _waitForFaultProductRemoval = false;
        SetProductRemovalPending(false);
        bool returnedToMain =
            Interlocked.Exchange(ref _removalMonitoringFromMain, 0) != 0;
        _discardInterlock.Arm(Volatile.Read(ref _discardContactClosed) != 0);
        Interlocked.Exchange(ref _discardRequiredForFault, 0);
        ResetFullCycleAfterProductRemoved();
        if (returnedToMain)
        {
            _cycleActive = false;
            SetProductionPhase(ProductionPhase.WaitingProduct);
            SwitchRuntimeMode(RuntimeMode.Background);
            _engine.SetFrameProcessingEnabled(false);
        }

        State = "CHỜ LẮP SẢN PHẨM";
        AddLog(discardRequired
            ? "Đã tháo sản phẩm và xác nhận thùng hàng lỗi - mở khóa chu kỳ mới."
            : "Đã tháo sản phẩm lỗi - chờ lắp sản phẩm lại.");
    }

    private bool ProcessDiscardContactFrame(
        ScanFrame frame,
        ProductModel model,
        long generation,
        RuntimeMode mode)
    {
        if (!frame.Complete || frame.UnknownBytes != 0)
            return Volatile.Read(ref _discardStandaloneLocked) != 0;

        IReadOnlyList<int> activeDiscardIo = DiscardContactInterlock.GetActiveContactIo(
            frame,
            model.DiscardContactIo);
        bool closed = activeDiscardIo.Count > 0;
        int previous = Interlocked.Exchange(ref _discardContactClosed, closed ? 1 : 0);

        if (closed && previous == 0)
        {
            _sound.PlayDiscardContact();
            AddLog(
                $"[DISCARD] Cảm biến tác động: " +
                string.Join(", ", activeDiscardIo.Select(io => $"IO({io})")) + ".");
        }

        if (mode == RuntimeMode.Production && previous != (closed ? 1 : 0))
        {
            int[] displayIo = closed ? model.DiscardContactIo.ToArray() : [];
            InvokeUi(() =>
            {
                if (!IsRuntimeContext(RuntimeMode.Production, generation))
                    return;

                if (displayIo.Length > 0)
                    ShowDiscardContacts(displayIo);
                else
                    ClearInlineProbeDisplay();
            });
        }

        DiscardContactTransition transition = _discardInterlock.Observe(closed);
        if (transition == DiscardContactTransition.FirstPassDetected)
        {
            AddLog("[DISCARD] Đã nhận hàng qua cảm biến - chờ cảm biến nhả để hoàn tất 1/1.");
            if (_waitForFaultProductRemoval)
            {
                InvokeUi(() => State = "CHỜ XÁC NHẬN THÙNG LỖI");
            }
            else if (mode is RuntimeMode.Production or RuntimeMode.Background)
            {
                LockProductionForStandaloneDiscard(mode);
            }
        }
        else if (transition == DiscardContactTransition.Completed)
        {
            AddLog("[DISCARD] Cảm biến đã nhả sau một lần đưa hàng qua - xác nhận thùng NG 1/1 hoàn tất.");
            InvokeUi(() =>
            {
                if (_waitForFaultProductRemoval)
                {
                    State = "CHỜ XÁC NHẬN THÙNG LỖI";
                    TryCompleteFaultProductRemoval();
                    if (closed)
                        ShowDiscardContacts(model.DiscardContactIo);
                }
                else if (Interlocked.Exchange(ref _discardStandaloneLocked, 0) != 0)
                {
                    UnlockProductionAfterStandaloneDiscard(mode, closed, model.DiscardContactIo);
                }
            });
        }

        return Volatile.Read(ref _discardStandaloneLocked) != 0;
    }

    private void LockProductionForStandaloneDiscard(RuntimeMode mode)
    {
        if (Interlocked.Exchange(ref _discardStandaloneLocked, 1) != 0)
            return;

        CancelCycleOperations();
        _cycleActive = false;
        _waitForProductRelease = false;
        SetProductionPhase(ProductionPhase.WaitingProductRemoval);
        SetProductRemovalPending(true);
        _engine.SetFrameProcessingEnabled(false);
        ResetEngineWithoutChangedReentry();
        InvokeUi(() =>
        {
            State = "THÙNG LỖI ĐÃ KHÓA - CHỜ NHẢ CẢM BIẾN";
            RefreshFaults();
        });
        AddLog($"[DISCARD] Khóa Production độc lập khi đang ở chế độ {mode}.");
    }

    private void UnlockProductionAfterStandaloneDiscard(
        RuntimeMode mode,
        bool contactClosed,
        IReadOnlyList<int> displayIo)
    {
        SetProductRemovalPending(false);
        _discardInterlock.Arm(contactClosed);

        if (mode == RuntimeMode.Production)
        {
            BeginCycleOperations();
            ResetFullCycleAfterProductRemoved();
            _engine.SetFrameProcessingEnabled(!IsIoMappingMode);
            State = MasterApproved ? "CHỜ LẮP SẢN PHẨM" : "KIỂM TRA MASTER PASS";
            if (contactClosed)
                ShowDiscardContacts(displayIo);
        }
        else
        {
            _cycleActive = false;
            SetProductionPhase(ProductionPhase.WaitingProduct);
            _engine.SetFrameProcessingEnabled(false);
            State = ReadyStateForCurrentModel();
        }

        AddLog("[DISCARD] Đã mở khóa Production sau một lần đưa hàng qua cảm biến (tác động rồi nhả).");
    }

    private void PlayProductStartSoundOnce(
        long generation,
        bool frameClassifiedAsProbe)
    {
        if (frameClassifiedAsProbe ||
            !_cycleActive ||
            _waitForProductRelease ||
            _waitForFaultProductRemoval ||
            CurrentProductionPhase != ProductionPhase.Continuity ||
            !IsProductionFaultContext(generation) ||
            !_engine.HasProductActivity ||
            Interlocked.CompareExchange(ref _productStartSoundPlayed, 1, 0) != 0)
        {
            return;
        }

        _sound.PlayProductStart();
    }

    private void ProcessIoMappingFrame(
        ScanFrame frame,
        long generation,
        IReadOnlyList<int> detectedProbeIos)
    {
        if (!frame.Complete || frame.UnknownBytes != 0 || !frame.TerminatorKnown)
            return;

        bool hadTrackedProbe = SnapshotInlineProbeContacts().Length > 0;
        if (detectedProbeIos.Count > 0)
        {
            int[] activeProbeIos = detectedProbeIos
                .Where(_board.Capacity.ContainsGlobalIo)
                .Distinct()
                .OrderBy(io => io)
                .ToArray();
            if (activeProbeIos.Length == 0)
                return;

            bool changed = !SnapshotInlineProbeContacts().SequenceEqual(activeProbeIos);
            lock (_inlineProbeGate)
                _inlineProbeContactIos = activeProbeIos;
            Volatile.Write(ref _inlineProbeContactIo, activeProbeIos[0]);
            Interlocked.Exchange(ref _inlineProbeLastSeenUtcTicks, DateTime.UtcNow.Ticks);
            _sound.SetTestPointContactSound(true);
            SetProbePresentationState(ProbePresentationState.Touch, activeProbeIos, frame.Sequence);
            if (!changed)
                return;
            long probeRevision = Interlocked.Increment(ref _inlineProbeUiRevision);
            FaultRow[] probeRows = BuildProbeDisplayRows(activeProbeIos).ToArray();
            InvokeUi(() =>
            {
                if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
                    !IsIoMappingMode ||
                    probeRevision != Volatile.Read(ref _inlineProbeUiRevision))
                {
                    return;
                }

                ProbeContacts.Clear();
                foreach (FaultRow row in probeRows)
                    ProbeContacts.Add(row);
                SetProductionPresentationMode(
                    ProductionPresentationMode.Probe,
                    frame.Sequence,
                    "LIVE_TOPOLOGY_PROBE_TOUCH");
                SynchronizeFaultRows(probeRows);
                UpdateProbeCardActivity(activeProbeIos);
                Raise(nameof(HasInlineProbeContacts));
                RaiseTestStatistics();
                LogUiSnapshot(frame.Sequence, frame.ScanGeneration, probeRows.Length, "live-probe");
            });
            return;
        }

        if (hadTrackedProbe)
        {
            ClearInlineProbeContactsState();
            SetProbePresentationState(ProbePresentationState.Released, [], frame.Sequence);
            long releaseRevision = Interlocked.Increment(ref _inlineProbeUiRevision);
            LiveTopologySnapshot restore = _lastLiveTopologySnapshot;
            InvokeUi(() =>
            {
                if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
                    !IsIoMappingMode ||
                    releaseRevision != Volatile.Read(ref _inlineProbeUiRevision))
                {
                    return;
                }

                ProbeContacts.Clear();
                SetLiveTopologyProductState(restore, "PROBE_RELEASE_RESTORE");
                SynchronizeFaultRows(restore.Rows);
                UpdateProbeCardActivity(Array.Empty<int>());
                Raise(nameof(HasInlineProbeContacts));
                RaiseTestStatistics();
                LogUiSnapshot(frame.Sequence, frame.ScanGeneration, restore.Rows.Count, "live-restore");
            });
            return;
        }

        SetProbePresentationState(ProbePresentationState.Inactive, [], frame.Sequence);
        LiveTopologySnapshot topology = LiveTopologyPresenter.Build(frame, _board.Capacity);
        LiveTopologySnapshot previousTopology = _lastLiveTopologySnapshot;
        bool sameGeneration = previousTopology.ScanGeneration == 0 ||
                              topology.ScanGeneration == 0 ||
                              previousTopology.ScanGeneration == topology.ScanGeneration;
        if (sameGeneration &&
            previousTopology.FrameSequence > 0 &&
            topology.FrameSequence > 0 &&
            topology.FrameSequence <= previousTopology.FrameSequence)
        {
            AsyncFileLogService.Current.Performance(
                $"UI_SNAPSHOT_REJECT mode=LiveTopology seq={topology.FrameSequence} " +
                $"rendered_seq={previousTopology.FrameSequence} generation={topology.ScanGeneration}");
            return;
        }
        if (string.Equals(topology.Signature, _lastIoMappingSignature, StringComparison.Ordinal))
            return;

        HashSet<LiveTopologyPair> previousPairs = previousTopology.Pairs.ToHashSet();
        HashSet<LiveTopologyPair> currentPairs = topology.Pairs.ToHashSet();
        int addedPairs = currentPairs.Count(pair => !previousPairs.Contains(pair));
        int removedPairs = previousPairs.Count(pair => !currentPairs.Contains(pair));
        _lastLiveTopologySnapshot = topology;
        _lastIoMappingSignature = topology.Signature;
        long mappingRevision = Interlocked.Increment(ref _ioMappingUiRevision);
        InvokeUi(() =>
        {
            if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
                !IsIoMappingMode ||
                mappingRevision != Volatile.Read(ref _ioMappingUiRevision))
            {
                return;
            }

            ProbeContacts.Clear();
            SetLiveTopologyProductState(topology, "LIVE_CONNECTIVITY");
            SynchronizeFaultRows(topology.Rows);
            RaiseTestStatistics();
            LogUiSnapshot(
                topology.FrameSequence,
                topology.ScanGeneration,
                topology.Rows.Count,
                "live-topology");
        });

        string components = string.Join(';', topology.Components.Select(component =>
            $"[{string.Join(',', component.Select(io => $"IO{io}"))}]"));
        AsyncFileLogService.Current.Performance(
            $"LIVE_TOPOLOGY seq={frame.Sequence} pairs={topology.Pairs.Count} " +
            $"components={components} stable=1 added={addedPairs} removed={removedPairs}");
    }

    private void SetLiveTopologyProductState(LiveTopologySnapshot topology, string reason)
    {
        bool present = topology.Pairs.Count > 0;
        SetProductionRuntimeState(
            present
                ? ProductionRuntimeState.TestingRealtime
                : ProductionRuntimeState.WaitingForProduct,
            topology.FrameSequence,
            reason);
        SetProductionPresentationMode(
            present ? ProductionPresentationMode.LiveTopology : ProductionPresentationMode.Waiting,
            topology.FrameSequence,
            reason);
        State = present ? "ĐANG KIỂM TRA..." : "LẮP SẢN PHẨM";
    }

    private bool HandleStartupIoInterlock(ScanFrame frame, long generation)
    {
        if (!_requireStartupIoClear || Volatile.Read(ref _startupIoInterlockState) == 2)
            return true;

        if (!frame.Complete || frame.UnknownBytes > 0 ||
            Volatile.Read(ref _startupIoInterlockState) == 1)
        {
            return false;
        }

        IReadOnlyList<StartupIoContactPair> pairs = StartupIoInterlock.FindConnectedPairs(
            frame,
            _model,
            _board.Capacity);
        if (pairs.Count > 0)
        {
            SetProductRemovalPending(true);
            string signature = string.Join('|', pairs.Select(pair => $"{pair.FirstIo}-{pair.SecondIo}"));
            if (!string.Equals(signature, _startupIoWarningSignature, StringComparison.Ordinal))
            {
                _startupIoWarningSignature = signature;
                AddLog(
                    "STARTUP IO INTERLOCK: chưa ARM vì đang có kết nối " +
                    string.Join(", ", pairs.Select(pair => $"IO{pair.FirstIo}<->IO{pair.SecondIo}")));
                InvokeUi(() => ShowStartupIoWarning(generation, pairs));
            }

            return false;
        }

        if (Interlocked.CompareExchange(ref _startupIoInterlockState, 1, 0) != 0)
            return false;

        _startupIoWarningSignature = string.Empty;
        InvokeUi(() => CompleteStartupIoInterlock(generation));
        return false;
    }

    private void ShowStartupIoWarning(
        long generation,
        IReadOnlyList<StartupIoContactPair> pairs)
    {
        if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
            Volatile.Read(ref _startupIoInterlockState) != 0)
        {
            return;
        }

        _cycleActive = false;
        SetProductionPhase(ProductionPhase.WaitingProduct);
        SelectedOperationTabIndex = 0;
        var rows = new List<FaultRow>(pairs.Count);
        foreach (StartupIoContactPair pair in pairs)
        {
            PinRecord? first = FindPinByIo(pair.FirstIo);

            rows.Add(new FaultRow
            {
                Kind = FaultKind.Info,
                ProductFaultType = ProductFaultType.None,
                FaultType = "CHỜ THÁO SẢN PHẨM",
                Io = pair.FirstIo,
                ActualSourceIo = pair.FirstIo,
                ActualTargetIo = pair.SecondIo,
                RelatedIos = [pair.FirstIo, pair.SecondIo],
                Connector = first?.Connector ?? string.Empty,
                Pin = first?.PinNumber ?? string.Empty,
                WireName = first?.WireName ?? string.Empty,
                Section = first?.Section ?? string.Empty,
                Color = first?.Color ?? string.Empty,
                Status = "SẢN PHẨM VẪN ĐANG LẮP — VUI LÒNG THÁO SẢN PHẨM"
            });
        }

        SynchronizeFaultRows(rows);

        State = "VUI LÒNG THÁO SẢN PHẨM";
    }

    private void CompleteStartupIoInterlock(long generation)
    {
        if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
            Volatile.Read(ref _startupIoInterlockState) != 1)
        {
            return;
        }

        Interlocked.Exchange(ref _startupIoInterlockState, 2);
        SetProductRemovalPending(false);
        bool returnedToMain =
            Interlocked.Exchange(ref _removalMonitoringFromMain, 0) != 0;

        RefreshFaults();
        if (returnedToMain)
        {
            _cycleActive = false;
            SetProductionPhase(ProductionPhase.WaitingProduct);
            SwitchRuntimeMode(RuntimeMode.Background);
            _engine.SetFrameProcessingEnabled(false);
            State = ReadyStateForCurrentModel();
            AddLog("STARTUP IO INTERLOCK: đã tháo hết sản phẩm; mở khóa màn hình chính.");
            return;
        }

        if (MasterApproved)
        {
            _cycleActive = true;
            SetProductionPhase(ProductionPhase.Continuity);
            State = "CHỜ LẮP SẢN PHẨM";
            AddLog("STARTUP IO INTERLOCK: frame sạch, Production đã được ARM.");
        }
        else
        {
            State = "KIỂM TRA MASTER PASS";
            AddLog("STARTUP IO INTERLOCK: frame sạch, bắt đầu chuỗi MASTER.");
            _ = StartAutomaticMasterSequenceAsync();
        }

    }

    private void HandleBackgroundProductRemovalInterlock(ScanFrame frame, long generation)
    {
        if (!IsRuntimeContext(RuntimeMode.Background, generation) ||
            !frame.Complete ||
            frame.UnknownBytes > 0 ||
            _waitForProductRelease ||
            _waitForFaultProductRemoval)
        {
            return;
        }

        IReadOnlyList<StartupIoContactPair> pairs = StartupIoInterlock.FindConnectedPairs(
            frame,
            _model,
            _board.Capacity);
        if (pairs.Count > 0)
        {
            Interlocked.Exchange(ref _startupIoInterlockState, 0);
            SetProductRemovalPending(true);

            string signature = string.Join('|', pairs.Select(pair => $"{pair.FirstIo}-{pair.SecondIo}"));
            if (!string.Equals(signature, _startupIoWarningSignature, StringComparison.Ordinal))
            {
                _startupIoWarningSignature = signature;
                AddLog(
                    "MAIN IO INTERLOCK: khóa chọn mã và START vì đang có kết nối " +
                    string.Join(", ", pairs.Select(pair => $"IO{pair.FirstIo}<->IO{pair.SecondIo}")));
            }

            State = "VUI LÒNG THÁO SẢN PHẨM";
            return;
        }

        bool wasBlocked = IsProductRemovalPending ||
                          Volatile.Read(ref _startupIoInterlockState) != 2;
        Interlocked.Exchange(ref _startupIoInterlockState, 2);
        _startupIoWarningSignature = string.Empty;
        SetProductRemovalPending(false);

        if (wasBlocked)
        {
            State = ReadyStateForCurrentModel();
            AddLog("MAIN IO INTERLOCK: frame sạch, đã mở khóa chọn mã và START.");
        }
    }

    private static void LogProbeLatency(ScanFrame frame, DateTime uiRequestedAt, IReadOnlyList<int> ios)
    {
        DateTime renderedAt = DateTime.Now;
        double rxToVmMs = Math.Max(0, (uiRequestedAt - frame.Timestamp).TotalMilliseconds);
        double vmToUiMs = Math.Max(0, (renderedAt - uiRequestedAt).TotalMilliseconds);
        string state = ios.Count == 0
            ? "RELEASE"
            : $"TOUCH {string.Join(", ", ios.Select(io => $"IO{io}"))}";

        AsyncFileLogService.Current.Performance(
            $"PROBE_LATENCY {state}; RX->VM={rxToVmMs:0.0} ms; VM->UI={vmToUiMs:0.0} ms; seq={frame.Sequence}",
            AppLogLevel.Normal);
    }

    private void LogContinuousScanMetricsIfDue()
    {
        // Production mặc định tắt system log. Tránh đọc hàng loạt counter và
        // dựng chuỗi metrics trên hot path khi writer chắc chắn sẽ bỏ bản ghi.
        if (!AsyncFileLogService.Current.FileLoggingEnabled)
            return;

        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastContinuousScanMetricsTick);
        if (previous != 0 && now - previous < 5000)
            return;
        if (Interlocked.CompareExchange(ref _lastContinuousScanMetricsTick, now, previous) != previous)
            return;

        long received = Interlocked.Read(ref _productionFramesReceived);
        long processed = Interlocked.Read(ref _productionFramesProcessed);
        long dropped = Interlocked.Read(ref _productionFramesDropped);
        long probeRouted = Interlocked.Read(ref _productionFramesRoutedToProbe);
        long engineProcessed = _engine.FramesProcessed;
        long uiScheduled = Interlocked.Read(ref _engineUiUpdatesScheduled);
        long uiRendered = Interlocked.Read(ref _engineUiUpdatesRendered);
        long uiRequests = Interlocked.Read(ref _uiRequestCount);
        long dispatcherEnqueued = Interlocked.Read(ref _uiDispatcherEnqueueCount);
        long pipelineRendered = Interlocked.Read(ref _uiRenderedCount);
        long coalesced = Math.Max(0, uiRequests - pipelineRendered);

        AsyncFileLogService.Current.Performance(
            "CONTINUOUS_SCAN_METRICS " +
            $"rx_production={received} engine_processed_by_vm={processed} " +
            $"engine_processed_total={engineProcessed} dropped={dropped} probe_routed={probeRouted} " +
            $"ui_scheduled={uiScheduled} ui_rendered={uiRendered} " +
            $"scan_running={_board.IsScanning} mode={CurrentRuntimeMode}");
        AsyncFileLogService.Current.Performance(
            $"UI_PIPELINE requests={uiRequests} " +
            $"dispatcher_enqueued={dispatcherEnqueued} " +
            $"rendered={pipelineRendered} coalesced={coalesced}");
    }

    private void LogPassGateAfterProductionFrame(ScanFrame frame, double processMs)
    {
        // GetPassGateDiagnostics dựng snapshot của toàn bộ network. Không làm
        // việc đó trên mỗi thay đổi IO khi system log đang tắt; nhánh này chỉ
        // phục vụ chẩn đoán, không tham gia PASS/FAIL hay điều khiển relay.
        if (!AsyncFileLogService.Current.FileLoggingEnabled ||
            !frame.Complete ||
            frame.UnknownBytes > 0)
            return;

        PassGateDiagnostics gate = _engine.GetPassGateDiagnostics();
        LogProductEvidence(frame);
        LogPassLatencyMarkers(frame, gate, processMs);

        if (gate.ContinuityPassed &&
            _cycleActive &&
            MasterApproved &&
            Volatile.Read(ref _postContinuityStarted) == 0)
        {
            return;
        }

        string reason = ResolvePassGateReason(gate);
        // Sequence thay đổi ở mọi frame nên không được dùng làm chữ ký trạng thái.
        // Nếu có sequence ở đây, cùng một trạng thái logic vẫn ghi PASS_GATE liên
        // tục theo tốc độ scan và tạo I/O đĩa không cần thiết trên máy cấu hình yếu.
        string signature =
            $"{gate.ExpectedNetCount}|{gate.PassedNetCount}|" +
            $"{gate.WrongCandidateCount}|{gate.WrongConfirmedCount}|" +
            $"{gate.ShortCandidateCount}|{gate.ShortConfirmedCount}|" +
            $"{gate.HasProductActivity}|{MasterApproved}|{_cycleActive}|" +
            $"{Volatile.Read(ref _postContinuityStarted)}|{reason}";

        if (!string.Equals(signature, _lastPassGateSignature, StringComparison.Ordinal))
        {
            _lastPassGateSignature = signature;
            AsyncFileLogService.Current.Performance(
                $"PASS_GATE seq={frame.Sequence} cycle={_activeCycleId} scanMode={frame.Mode} " +
                $"frameComplete={frame.Complete} expected={gate.ExpectedNetCount} passed={gate.PassedNetCount} " +
                $"remaining={gate.RemainingNetworks.Count} wrongCandidates={gate.WrongCandidateCount} " +
                $"wrongConfirmed={gate.WrongConfirmedCount} shortCandidates={gate.ShortCandidateCount} " +
                $"shortConfirmed={gate.ShortConfirmedCount} hasWiringFault={gate.HasWiringFault} " +
                $"continuityPassed={gate.ContinuityPassed} productDetected={gate.HasProductActivity} " +
                $"sourceCoverage={gate.HasExpectedSourceCoverage} productStable={gate.ProductStable} " +
                $"readyToEvaluate={_engine.ReadyToEvaluateProductFaults} " +
                $"masterApproved={MasterApproved} cycleActive={_cycleActive} probeActive={Volatile.Read(ref _probeSessionActive) != 0} " +
                $"postContinuityStarted={Volatile.Read(ref _postContinuityStarted)} process_ms={processMs:0.###} reason={reason}");
        }

        if (gate.RemainingNetworks.Count > 0)
        {
            string remaining = string.Join(" | ", gate.RemainingNetworks.Select(item => item.Display));
            string remainingSignature = remaining;
            if (!string.Equals(remainingSignature, _lastPassRemainingSignature, StringComparison.Ordinal))
            {
                _lastPassRemainingSignature = remainingSignature;
                AsyncFileLogService.Current.Performance(
                    $"PASS_REMAINING seq={frame.Sequence} count={gate.RemainingNetworks.Count} nets={remaining}");
            }
        }
    }

    private void LogFaultGate(long generation)
    {
        // Tránh dựng PassGateDiagnostics lần thứ hai trên UI thread khi không
        // có nơi nhận log. Logic bắt lỗi đã hoàn tất trước khi vào hàm này.
        if (!AsyncFileLogService.Current.FileLoggingEnabled)
            return;

        PassGateDiagnostics gate = _engine.GetPassGateDiagnostics();
        ProductionPhase phase = CurrentProductionPhase;
        string signature =
            $"{generation}|{_cycleActive}|{phase}|{Volatile.Read(ref _waterProofRunning)}|" +
            $"{Volatile.Read(ref _postContinuityStarted)}|{_engine.ReadyToEvaluateProductFaults}|" +
            $"{_engine.LastFrameValid}|{_engine.HasWiringFault}|{gate.WrongConfirmedCount}|" +
            $"{gate.ShortConfirmedCount}";

        if (string.Equals(signature, _lastFaultGateSignature, StringComparison.Ordinal))
            return;

        _lastFaultGateSignature = signature;
        AsyncFileLogService.Current.Performance(
            "[FAULT-GATE] " +
            $"cycleActive={_cycleActive} phase={phase} waterProofRunning={Volatile.Read(ref _waterProofRunning)} " +
            $"postContinuityStarted={Volatile.Read(ref _postContinuityStarted)} " +
            $"readyToEvaluate={_engine.ReadyToEvaluateProductFaults} lastFrameValid={_engine.LastFrameValid} " +
            $"hasWiringFault={_engine.HasWiringFault} wrongConfirmed={gate.WrongConfirmedCount} " +
            $"shortConfirmed={gate.ShortConfirmedCount}");
    }

    private void AddFaultGateSuppressedLog(ProductionPhase phase)
    {
        if (!AsyncFileLogService.Current.FileLoggingEnabled)
            return;

        if (_cycleActive &&
            phase == ProductionPhase.Continuity &&
            _engine.ReadyToEvaluateProductFaults &&
            _engine.LastFrameValid)
        {
            return;
        }

        string reason = ResolveFaultGateSuppressReason(phase);
        string signature =
            $"{reason}|{_cycleActive}|{phase}|{Volatile.Read(ref _waterProofRunning)}|" +
            $"{Volatile.Read(ref _postContinuityStarted)}|{_engine.ReadyToEvaluateProductFaults}|" +
            $"{_engine.LastFrameValid}|{_engine.HasWiringFault}";

        if (string.Equals(signature, _lastFaultGateSuppressedSignature, StringComparison.Ordinal))
            return;

        _lastFaultGateSuppressedSignature = signature;
        AsyncFileLogService.Current.Performance(
            "[FAULT-GATE] SUPPRESSED " +
            $"reason={reason} cycleActive={_cycleActive} phase={phase} " +
            $"waterProofRunning={Volatile.Read(ref _waterProofRunning)} " +
            $"postContinuityStarted={Volatile.Read(ref _postContinuityStarted)} " +
            $"readyToEvaluate={_engine.ReadyToEvaluateProductFaults} lastFrameValid={_engine.LastFrameValid} " +
            $"hasWiringFault={_engine.HasWiringFault}");
    }

    private string ResolveFaultGateSuppressReason(ProductionPhase phase)
    {
        if (!_cycleActive)
            return "CYCLE_INACTIVE";
        if (phase != ProductionPhase.Continuity)
            return $"PHASE_{phase}";
        if (!_engine.ReadyToEvaluateProductFaults)
            return "NOT_READY_TO_EVALUATE";
        if (!_engine.LastFrameValid)
            return "LAST_FRAME_INVALID";
        return "HANDLER_ALREADY_RUNNING_OR_GUARD";
    }

    private void LogProductEvidence(ScanFrame frame)
    {
        ProductEvidenceSnapshot evidence = _engine.GetProductEvidenceSnapshot();
        WriteProductEvidenceLog(frame, evidence);
    }

    private void LogProbeProductEvidence(ScanFrame frame)
    {
        if (!AsyncFileLogService.Current.FileLoggingEnabled)
            return;

        ProductEvidenceSnapshot current = _engine.GetProductEvidenceSnapshot();
        ProductEvidenceSnapshot probe = current with
        {
            FrameSequence = frame.Sequence,
            ScanGeneration = frame.ScanGeneration,
            RawActiveIoCount = frame.ActiveIo.Count,
            ProbeEvidence = true,
            Reason = current.ValidProductEvidence
                ? "PROBE_WITH_EXISTING_PRODUCT"
                : "PROBE_ONLY"
        };
        WriteProductEvidenceLog(frame, probe);
    }

    private void WriteProductEvidenceLog(
        ScanFrame frame,
        ProductEvidenceSnapshot evidence)
    {
        string signature =
            $"{evidence.RawActiveIoCount}|{evidence.ExpectedConnectedCount}|" +
            $"{evidence.WrongCandidateCount}|{evidence.WrongConfirmedCount}|" +
            $"{evidence.ShortCandidateCount}|{evidence.ShortConfirmedCount}|" +
            $"{evidence.ProbeEvidence}|{evidence.ValidProductEvidence}|" +
            $"{evidence.State}|{evidence.Reason}";
        if (string.Equals(signature, _lastProductEvidenceSignature, StringComparison.Ordinal))
            return;

        _lastProductEvidenceSignature = signature;
        AsyncFileLogService.Current.Performance(
            $"PRODUCT_EVIDENCE seq={frame.Sequence} generation={frame.ScanGeneration} " +
            $"raw_active_count={evidence.RawActiveIoCount} " +
            $"expected_connected={evidence.ExpectedConnectedCount} " +
            $"wrong_candidates={evidence.WrongCandidateCount} " +
            $"wrong_confirmed={evidence.WrongConfirmedCount} " +
            $"short_candidates={evidence.ShortCandidateCount} " +
            $"short_confirmed={evidence.ShortConfirmedCount} " +
            $"probe_evidence={evidence.ProbeEvidence} " +
            $"valid_product_evidence={evidence.ValidProductEvidence} " +
            $"state={evidence.State} reason={evidence.Reason}");
    }

    private void LogPassLatencyMarkers(ScanFrame frame, PassGateDiagnostics gate, double processMs)
    {
        if (gate.ExpectedNetCount == 2 &&
            gate.PassedNetCount == 2 &&
            Interlocked.CompareExchange(ref _secondRequiredNetSeenLogged, 1, 0) == 0)
        {
            AsyncFileLogService.Current.Performance(
                $"PASS_LATENCY T_SECOND_REQUIRED_NET_SEEN seq={frame.Sequence} process_ms={processMs:0.###}");
        }

        if (gate.ContinuityPassed &&
            Interlocked.CompareExchange(ref _continuityPassedLogged, 1, 0) == 0)
        {
            AsyncFileLogService.Current.Performance(
                $"PASS_LATENCY T_CONTINUITY_PASSED seq={frame.Sequence} process_ms={processMs:0.###} expected={gate.ExpectedNetCount} passed={gate.PassedNetCount}");
        }
    }

    private string ResolvePassGateReason(PassGateDiagnostics gate)
    {
        if (!_cycleActive)
            return "CYCLE_INACTIVE";
        if (!MasterApproved)
            return "MASTER_LOCKED";
        if (Volatile.Read(ref _probeSessionActive) != 0 || IsProbeRelayInterlockActive())
            return "PROBE_INTERLOCK";
        if (gate.WrongConfirmedCount > 0)
            return "WRONG_CONFIRMED";
        if (gate.ShortConfirmedCount > 0)
            return "SHORT_CONFIRMED";
        if (gate.WrongCandidateCount > 0)
            return "WRONG_CANDIDATE";
        if (gate.ShortCandidateCount > 0)
            return "SHORT_CANDIDATE";
        if (gate.ExpectedNetCount <= 0)
            return "NO_EXPECTED_NET";
        if (gate.PassedNetCount != gate.ExpectedNetCount)
            return "MISSING_REQUIRED_NET";
        if (!gate.HasProductActivity)
            return "NO_PRODUCT_ACTIVITY";
        if (Volatile.Read(ref _postContinuityStarted) != 0)
            return "POST_CONTINUITY_ALREADY_STARTED";
        return "UNKNOWN";
    }

    private bool HasInstalledProductEvidenceForProbe()
    {
        // Htdrv chỉ nhận TestPoint khi bộ dây/bo test thật đang hiện diện.
        // Một tiếp xúc cơ thể người trên IO trống có thể tạo fan-in nhiễu, nhưng
        // không thể làm thông một network kỳ vọng của model. Dùng snapshot đã
        // xác nhận từ TestEngine làm gate; preview vẫn nhanh từ frame kế tiếp.
        if (_model is null || IsIoMappingMode)
            return false;

        PassGateDiagnostics gate = _engine.GetPassGateDiagnostics();
        return gate.ExpectedNetCount > 0 &&
               gate.PassedNetCount > 0 &&
               gate.LastFrameValid;
    }

    private bool IsMappedProbeIo(int io)
    {
        ProductModel? model = _model;
        if (model is null || io <= 0)
            return false;

        if (model.Pins.Any(pin => pin.IoNumber == io))
            return true;

        return model.Clip is not null &&
               (model.Clip.CommonIo == io ||
                model.Clip.Branches.Any(branch => branch.TargetIo == io));
    }

    private bool TryDetectInlineProbeContacts(ScanFrame frame, out int[] ios)
    {
        ios = Array.Empty<int>();
        if (frame.Mode != BoardScanMode.Production)
        {
            return false;
        }

        int[] previewIos = TakeProbePreviewForFrame(frame);
        IReadOnlyList<int> classifiedIos = ProbeContactClassifier
            .DetectMany(
                frame,
                _model,
                maxContacts: _probeStateTracker.MaxContacts,
                boardCapacity: _board.Capacity)
            .Select(detection => detection.Io)
            .ToArray();
        int[] observedIos = previewIos
            .Concat(classifiedIos)
            .Where(_board.Capacity.ContainsGlobalIo)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

        if (observedIos.Length > 0)
        {
            ios = observedIos
                .Where(value => value > 0)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();

            if (ios.Length > 0)
            {
                Interlocked.Exchange(ref _inlineProbeLastSeenUtcTicks, DateTime.UtcNow.Ticks);
                return true;
            }
        }

        // V12.9.2: không giữ contact cũ bằng TTL/quarantine. Frame hiện tại
        // không còn chữ ký Probe thì RELEASE được áp dụng ngay ở OnBoardFrameReceived.
        // Production có thể giữ stable-frame riêng trong TestEngine, nhưng Probe UI
        // không được chờ RequiredStableFrames hoặc timer 500-2000 ms.
        return false;
    }

    private int[] TakeProbePreviewForFrame(ScanFrame frame)
    {
        lock (_probePreviewGate)
        {
            ProductionProbePreview? preview = _pendingProductionProbePreview;
            if (preview is null)
                return [];

            bool sameGeneration = preview.ScanGeneration == 0 ||
                                  frame.ScanGeneration == 0 ||
                                  preview.ScanGeneration == frame.ScanGeneration;
            if (sameGeneration && preview.Sequence == frame.Sequence)
            {
                _pendingProductionProbePreview = null;
                return preview.ActiveIo.ToArray();
            }

            if (!sameGeneration || frame.Sequence >= preview.Sequence)
                _pendingProductionProbePreview = null;
            return [];
        }
    }

    private bool ProcessManualProbeFrame(ScanFrame frame, long generation)
    {
        bool hadTransientEvidence = _manualProbeSession.HasTransientContactEvidence;
        IReadOnlyList<ProbeContactClassifier.Detection> detections =
            ProbeContactClassifier.DetectMany(
                frame,
                _model,
                maxContacts: 4,
                boardCapacity: _board.Capacity);

        ManualProbeUpdate update = _manualProbeSession.Update(
            frame.Sequence,
            detections,
            _model,
            _board.Capacity);

        if (update.Transition == ManualProbeTransition.None)
        {
            return detections.Count > 0 ||
                   hadTransientEvidence ||
                   _manualProbeSession.HasTransientContactEvidence;
        }

        AsyncFileLogService.Current.Performance(
            $"MANUAL_PROBE transition={update.Transition} phase={update.Phase} " +
            $"tp=IO{update.ProbeIo} contact=IO{update.ContactIo} " +
            $"candidate=IO{update.CandidateIo} stable={update.StableFrames} seq={frame.Sequence}");

        if (update.Transition == ManualProbeTransition.PointerConfirmed)
        {
            AddLog($"TEST POINTER: đã khóa IO{update.ProbeIo}; bắt đầu CONNECTOR CHECK.");
            return true;
        }

        if (update.Transition is not (ManualProbeTransition.ConnectorChanged or
            ManualProbeTransition.ConnectorReleased))
        {
            return detections.Count > 0;
        }

        int contactIo = update.ContactIo;
        long probeRevision = Interlocked.Increment(ref _inlineProbeUiRevision);
        lock (_inlineProbeGate)
        {
            _inlineProbeContactIos = contactIo > 0 ? [contactIo] : Array.Empty<int>();
        }
        Volatile.Write(ref _inlineProbeContactIo, contactIo);
        _sound.SetTestPointContactSound(contactIo > 0);
        if (contactIo > 0)
            Interlocked.Exchange(ref _inlineProbeLastSeenUtcTicks, DateTime.UtcNow.Ticks);

        InvokeUi(() =>
        {
            if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
                !_manualProbeSession.IsActive ||
                probeRevision != Volatile.Read(ref _inlineProbeUiRevision))
            {
                return;
            }

            if (contactIo > 0)
                ShowInlineProbeContacts([contactIo]);
            else
                ClearInlineProbeDisplay();
        });

        return detections.Count > 0;
    }

    private void StartManualProbeSession(string reason)
    {
        ClearInlineProbeContactsState(clearLastSeen: true);
        InvokeUi(ClearInlineProbeDisplay);
        ManualProbeUpdate update = _manualProbeSession.Start();
        AsyncFileLogService.Current.Performance(
            $"MANUAL_PROBE transition={update.Transition} phase={update.Phase} reason={reason}");
    }

    private void ResetManualProbeSession(string reason)
    {
        ManualProbeUpdate update = _manualProbeSession.Reset();
        ClearInlineProbeContactsState(clearLastSeen: true);
        InvokeUi(ClearInlineProbeDisplay);
        if (update.Transition != ManualProbeTransition.None)
        {
            AsyncFileLogService.Current.Performance(
                $"MANUAL_PROBE transition={update.Transition} phase={update.Phase} reason={reason}");
        }
    }

    private int[] SnapshotInlineProbeContacts()
    {
        lock (_inlineProbeGate)
            return _inlineProbeContactIos.ToArray();
    }

    private bool UpdateInlineProbeContacts(IReadOnlyList<int> ios)
    {
        BoardCapacity capacity = _board.Capacity;
        int[] normalized = ios
            .Where(value => capacity.ContainsGlobalIo(value))
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

        bool changed = _probeStateTracker.Update(normalized);
        int[] active = _probeStateTracker.ActiveIos.ToArray();
        lock (_inlineProbeGate)
        {
            _inlineProbeContactIos = active;
        }

        Volatile.Write(ref _inlineProbeContactIo, active.FirstOrDefault());
        if (active.Length > 0)
        {
            Interlocked.Exchange(ref _inlineProbeLastSeenUtcTicks, DateTime.UtcNow.Ticks);
            if (changed)
                _sound.SetTestPointContactSound(true);
        }
        else if (changed)
        {
            _sound.SetTestPointContactSound(false);
        }
        return changed;
    }

    private bool ClearInlineProbeContactsState(bool clearLastSeen = false)
    {
        bool changed = _probeStateTracker.Clear();
        if (clearLastSeen)
            Interlocked.Increment(ref _inlineProbeUiRevision);
        _sound.SetTestPointContactSound(false);
        lock (_inlineProbeGate)
        {
            _inlineProbeContactIos = Array.Empty<int>();
        }

        Volatile.Write(ref _inlineProbeContactIo, 0);

        // RELEASE xóa UI ngay; lastSeen chỉ được giữ cho interlock relay 40 ms
        // chống rung rất ngắn. Reset timestamp hoàn toàn khi đổi phiên/chu kỳ.
        if (clearLastSeen)
            Interlocked.Exchange(ref _inlineProbeLastSeenUtcTicks, 0);

        return changed;
    }

    private void ApplyProbePresentationSnapshot(
        TestEnginePresentationSnapshot productSnapshot,
        IReadOnlyList<int> probeIos,
        bool released,
        long frameSequence)
    {
        IReadOnlyList<FaultRow> desiredRows;
        if (!released && probeIos.Count > 0)
        {
            desiredRows = BuildProbeDisplayRows(probeIos);
            ProbeContacts.Clear();
            foreach (FaultRow row in desiredRows)
                ProbeContacts.Add(row);
            SetProductionPresentationMode(
                ProductionPresentationMode.Probe,
                frameSequence,
                "PROBE_TOUCH");
        }
        else
        {
            _sound.SetTestPointContactSound(false);
            ProbeContacts.Clear();

            // Restore Product truth first. Probe is presentation-only and must not
            // manufacture ProductEvidence or repopulate model rows while waiting.
            ApplyAuthoritativeProductionState(productSnapshot.Electrical);

            bool waitingForProduct =
                CurrentProductionRuntimeState == ProductionRuntimeState.WaitingForProduct;
            bool presentationCycleStarted = productSnapshot.Electrical.ProductEvidence;
            if (_presentationCycleStarted != presentationCycleStarted)
            {
                _presentationCycleStarted = presentationCycleStarted;
                RaiseCenterPresentation();
            }
            desiredRows = waitingForProduct
                ? Array.Empty<FaultRow>()
                : productSnapshot.Rows;

            if (waitingForProduct &&
                CurrentProductionPhase == ProductionPhase.Continuity)
            {
                State = "LẮP SẢN PHẨM";
            }

            SetProductionPresentationMode(
                waitingForProduct
                    ? ProductionPresentationMode.Waiting
                    : ProductionPresentationMode.Product,
                frameSequence,
                "PROBE_RELEASE_RESTORE");
        }

        // Faults is the collection bound by FaultGrid. Apply exactly one
        // reducer result so Probe and Product callbacks cannot overwrite each
        // other through separate Clear/Insert/restore operations.
        SynchronizeFaultRows(desiredRows);
        UpdateProbeCardActivity(probeIos);
        Raise(nameof(HasInlineProbeContacts));
        Raise(nameof(ProbeModeText));
        Raise(nameof(ProbeBarText));
        Raise(nameof(ProbeBarBackground));
        RaiseTestStatistics();
        LogUiSnapshot(
            productSnapshot.Electrical.FrameSequence,
            productSnapshot.Electrical.ScanGeneration,
            desiredRows.Count,
            "probe-reducer");
    }


    private bool IsProbeRelayInterlockActive()
    {
        // ALWAYS_PROBE_2026-09-05: interlock que dò luôn có hiệu lực.
        if (Volatile.Read(ref _probeSessionActive) != 0 ||
            Volatile.Read(ref _inlineProbeContactIo) != 0)
        {
            return true;
        }

        long lastTicks = Interlocked.Read(ref _inlineProbeLastSeenUtcTicks);
        if (lastTicks <= 0)
            return false;

        long elapsedTicks = Math.Max(0, DateTime.UtcNow.Ticks - lastTicks);
        return elapsedTicks < TimeSpan.FromMilliseconds(ProbeRelayReleaseDebounceMs).Ticks;
    }

    private async Task WaitForProbeRelayInterlockAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        int requestedMs = 0;
        if (!IsProbeRelayInterlockActive())
        {
            AsyncFileLogService.Current.Performance("PROBE_INTERLOCK_WAIT requested_ms=0 actual_ms=0");
            return;
        }

        bool logged = false;
        while (IsProbeRelayInterlockActive())
        {
            ct.ThrowIfCancellationRequested();
            if (!logged)
            {
                logged = true;
                AddLog("Khóa relay an toàn: chờ debounce RELEASE đầu dò rất ngắn trước khi cho phép chuỗi PASS.");
            }

            int delayMs = 5;
            long lastTicks = Interlocked.Read(ref _inlineProbeLastSeenUtcTicks);
            if (lastTicks > 0 && Volatile.Read(ref _inlineProbeContactIo) == 0)
            {
                double elapsedMs = Math.Max(0, TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastTicks).TotalMilliseconds);
                double remainingMs = ProbeRelayReleaseDebounceMs - elapsedMs;
                delayMs = (int)Math.Clamp(Math.Ceiling(remainingMs), 1, 10);
            }

            requestedMs += delayMs;
            await Task.Delay(delayMs, ct);
        }

        double actualMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        AsyncFileLogService.Current.Performance(
            $"PROBE_INTERLOCK_WAIT requested_ms={requestedMs} actual_ms={actualMs:0.###}");
    }

    private IReadOnlyList<FaultRow> BuildProbeDisplayRows(IEnumerable<int> ios)
    {
        return ios
            .Where(io => io > 0)
            .Distinct()
            .SelectMany(BuildProbeDisplayRows)
            .ToArray();
    }

    private IReadOnlyList<FaultRow> BuildProbeDisplayRows(int io)
    {
        ProductModel? model = _model;
        if (model is null)
            return BuildUnmappedProbeRow(io);

        // Htdrv hiển thị đúng row TP của chính I/O đang chạm, không bung các
        // endpoint còn lại cùng network. Giữ đủ duplicate mapping nếu một
        // Global IO được khai báo tại nhiều connector/pin trong THT.
        PinRecord[] pins = model.Pins
            .Where(pin => pin.IoNumber == io)
            .Distinct()
            .OrderBy(pin => pin.OriginalOrder > 0 ? pin.OriginalOrder : int.MaxValue)
            .ThenBy(pin => pin.Connector, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pin => pin.PinNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (pins.Length == 0)
            return BuildUnmappedProbeRow(io);

        return pins
            .Select(pin => new FaultRow
            {
                Kind = FaultKind.Probe,
                FaultType = "TP",
                Io = pin.IoNumber,
                // Pin vẫn thuộc bảng Pin của THT nên giữ Connector/Pin. Khi
                // chưa đặt tên dây, đánh dấu IO vật lý ngay tại cột IO thay vì
                // đưa chuỗi IO sang cột Connector hoặc Tên dây.
                IoTextOverride = string.IsNullOrWhiteSpace(pin.WireName)
                    ? $"IO ({pin.IoNumber})"
                    : string.Empty,
                RelatedIos = [io],
                Connector = pin.Connector,
                Pin = pin.PinNumber,
                WireName = pin.WireName,
                Splice = pin.SpliceName,
                Section = pin.Section,
                Color = pin.Color,
                Status = $"TP - IO({pin.IoNumber})",
                DisplayOrder = pin.OriginalOrder > 0
                    ? pin.OriginalOrder
                    : pin.IoNumber
            })
            .ToArray();
    }

    private static IReadOnlyList<FaultRow> BuildUnmappedProbeRow(int io) =>
    [
        new FaultRow
        {
            Kind = FaultKind.Probe,
            FaultType = "TP",
            // Không có PinRecord trong THT: vẫn giữ IO vật lý do đầu dò phát
            // hiện để cột IO chỉ đúng vị trí đang chạm. Các cột metadata THT
            // tiếp tục để trống; không suy diễn Connector/Pin.
            Io = io,
            IoTextOverride = $"IO ({io})",
            RelatedIos = [io],
            Status = $"TP - IO({io})",
            DisplayOrder = io
        }
    ];

    private void ShowDiscardContacts(IReadOnlyList<int> ios)
    {
        FaultRow[] rows = ios
            .Where(io => io > 0)
            .Distinct()
            .Take(2)
            .Select(io => new FaultRow
            {
                Kind = FaultKind.Probe,
                Io = io,
                RelatedIos = [io],
                WireName = $"_DISCARD IO({io})",
                DisplayOrder = io
            })
            .ToArray();

        ProbeContacts.Clear();
        foreach (FaultRow row in rows)
            ProbeContacts.Add(row);
        SynchronizeFaultRows(rows);
        UpdateProbeCardActivity(ios);
        Raise(nameof(HasInlineProbeContacts));
        Raise(nameof(ProbeModeText));
        Raise(nameof(ProbeBarText));
        Raise(nameof(ProbeBarBackground));
    }

    private void ShowInlineProbeContacts(IReadOnlyList<int> ios)
    {
        IReadOnlyList<FaultRow> rows = BuildProbeDisplayRows(ios);

        ProbeContacts.Clear();
        foreach (FaultRow row in rows)
            ProbeContacts.Add(row);
        // Probe Pin luôn hiển thị độc lập với latch sản phẩm. Nó không bật
        // production cycle và không đưa metadata dây vào fault presentation.
        SynchronizeInlineProbeFaultRows(rows);

        UpdateProbeCardActivity(ios);
        Raise(nameof(HasInlineProbeContacts));
        Raise(nameof(ProbeModeText));
        Raise(nameof(ProbeBarText));
        Raise(nameof(ProbeBarBackground));

        string display = string.Join(", ", ios.Select(io => $"IO({io})"));
        AddLog($"Đầu dò phát hiện {display}; hiển thị song song và bỏ qua logic chập của frame probe.");
    }

    private void ShowProductionProbePreview(IReadOnlyList<int> ios, long frameSequence)
    {
        IReadOnlyList<FaultRow> rows = BuildProbeDisplayRows(ios);
        ProbeContacts.Clear();
        foreach (FaultRow row in rows)
            ProbeContacts.Add(row);

        SetProductionPresentationMode(
            ProductionPresentationMode.Probe,
            frameSequence,
            "PROBE_PREVIEW");
        SynchronizeFaultRows(rows);
        UpdateProbeCardActivity(ios);
        Raise(nameof(HasInlineProbeContacts));
        Raise(nameof(ProbeModeText));
        Raise(nameof(ProbeBarText));
        Raise(nameof(ProbeBarBackground));
        RaiseTestStatistics();
    }

    private void ClearInlineProbeDisplay()
    {
        _sound.SetTestPointContactSound(false);
        if (ProbeContacts.Count > 0)
            ProbeContacts.Clear();
        SynchronizeFaultRows(Faults.Where(row => row.Kind != FaultKind.Probe).ToArray());

        UpdateProbeCardActivity(Array.Empty<int>());
        Raise(nameof(HasInlineProbeContacts));
        Raise(nameof(ProbeModeText));
        Raise(nameof(ProbeBarText));
        Raise(nameof(ProbeBarBackground));
    }

    private void SynchronizeInlineProbeFaultRows(IReadOnlyList<FaultRow> rows)
    {
        SynchronizeFaultRows(rows);
        RaiseTestStatistics();
    }

    private void RemoveInlineProbeFaultRows()
    {
        SynchronizeFaultRows(Faults.Where(row => row.Kind != FaultKind.Probe).ToArray());
    }

    private void RebuildActiveCards()
    {
        BoardCapacity capacity = _board.Capacity;
        int[] currentProbe = SnapshotInlineProbeContacts();

        Cards.Clear();
        for (int cardNumber = 1; cardNumber <= BoardCapacity.MaxExpansionCardCount; cardNumber++)
        {
            bool enabled = cardNumber >= capacity.StartCardNumber &&
                           cardNumber <= capacity.ScanCardCount;
            int logicalCardIndex = cardNumber - capacity.StartCardNumber;
            int firstIo = enabled
                ? (logicalCardIndex * BoardCapacity.IoPerExpansionCard) + 1
                : 0;
            int lastIo = enabled
                ? firstIo + BoardCapacity.IoPerExpansionCard - 1
                : 0;

            Cards.Add(new BoardCardState
            {
                CardNumber = cardNumber,
                ExpansionCardNumber = cardNumber,
                FirstGlobalIo = firstIo,
                LastGlobalIo = lastIo,
                IsEnabled = enabled,
                IsScanning = enabled && _board.IsScanning,
                HasProbeActivity = enabled &&
                    currentProbe.Any(io => io >= firstIo && io <= lastIo)
            });
        }

        Raise(nameof(BoardCapacity));
        Raise(nameof(BoardCapacityText));
    }

    private void UpdateProbeCardActivity(IReadOnlyList<int> ios)
    {
        foreach (BoardCardState card in Cards)
        {
            card.HasProbeActivity = card.IsEnabled && ios.Any(io =>
                io >= card.FirstGlobalIo && io <= card.LastGlobalIo);
        }
    }

    private void UpdateCardScanningState()
    {
        bool scanning = _board.IsScanning;
        foreach (BoardCardState card in Cards)
            card.IsScanning = card.IsEnabled && scanning;
    }

    public IReadOnlyList<FaultRow> GetProbeRows(int io) => BuildProbeDisplayRows(io);

    private async Task ConnectBoardAsync()
    {
        if (IsDeviceFault || _lifetimeCts.IsCancellationRequested)
            return;

        try
        {
            if (_board.IsConnected)
            {
                BoardConnectionMessage = string.Empty;
                HardwareStatus = "Bo: đã kết nối";
                State = ReadyStateForCurrentModel();
                Raise(nameof(IsBoardConnected));
                await EnsureContinuousProductionScanAsync();
                return;
            }

            State = "ĐANG KẾT NỐI BO";
            StartupPerformanceTrace.Mark("T4 D2XX open started");

            var info = await _board.ConnectAsync(_lifetimeCts.Token);
            StartupPerformanceTrace.Mark("T5 D2XX open completed");
            StartupPerformanceTrace.Mark("T6 Handshake completed");

            if (_lifetimeCts.IsCancellationRequested)
                return;

            // MainWindow có thể đã chốt startup timeout trong lúc driver còn
            // mở handle. Không cho kết nối hoàn tất muộn hồi sinh phiên đã lỗi.
            if (IsDeviceFault)
            {
                await _board.DisconnectAsync();
                return;
            }

            BoardConnectionMessage = string.Empty;
            HardwareStatus =
                $"Bo: {info.Description} [{info.SerialNumber}] - ĐÃ KẾT NỐI";

            AddLog(
                $"Đã kết nối bo: {info.Description} [{info.SerialNumber}]");

            State = ReadyStateForCurrentModel();
            Raise(nameof(IsBoardConnected));

            // Kết nối thành công là START SCAN ngay. Không chờ mở TestView.
            await EnsureContinuousProductionScanAsync();
            StartupPerformanceTrace.Mark("T7 SCANNER_READY");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Ứng dụng đang thoát; không cập nhật UI và không báo lỗi kết nối.
            return;
        }
        catch (Exception ex)
        {
            EnterDeviceFault(ex, "BoardStartup");
        }
    }

    public void ReportBoardUnavailableForOperatorAction(string source)
    {
        if (!IsDeviceFault)
        {
            EnterDeviceFault(
                new InvalidOperationException("Bo kiểm tra chưa kết nối hoặc đã mất kết nối."),
                source);
            return;
        }

        ShowDeviceFaultDialogOnce();
    }

    public void ReportStartupBoardTimeout() =>
        EnterDeviceFault(
            new TimeoutException("Khởi tạo bo vượt quá thời gian cho phép."),
            "BoardStartupTimeout");

    private async Task ConnectKeysightAsync()
    {
        // Giữ command để tương thích binding cũ, nhưng việc kết nối thực tế là tự động
        // và chỉ xảy ra khi model có bước đo điện trở sau continuity PASS.
        await EnsureKeysightConnectedAsync();
    }

    private async Task EnsureKeysightConnectedAsync()
    {
        if (_visa.IsConnected) return;

        State = "ĐANG CHUẨN BỊ ĐO ĐIỆN TRỞ";
        AddLog("[AUTO-R] Keysight connecting");
        var idn = await Task.Run(() =>
            _visa.ConnectAutomatic(_settings.Keysight.Resource));
        AddLog($"[AUTO-R] Keysight connected: {idn}");
        AddLog($"Đã tự kết nối Keysight: {idn}");
    }

    /// <summary>
    /// Dừng mọi thao tác của TestView/PinProbe nhưng giữ kết nối board để có
    /// thể quay về MainWindow rồi test lại ngay. Cửa sổ phải await hàm này
    /// trước khi đóng để không để worker scan chạy ngầm.
    /// </summary>
    public Task StopViewAsync() => StopTestAsync();

    /// <summary>
    /// Shutdown cuối cùng của ứng dụng. Idempotent: chỉ chạy một lần.
    /// Dừng scan -> relay OFF -> reset -> disconnect FTDI -> đóng VISA ->
    /// bỏ toàn bộ event subscription.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        SwitchRuntimeMode(RuntimeMode.ShuttingDown);
        _scanSupervisor.Suspend("Shutdown");
        Interlocked.Increment(ref _statisticsLoadGeneration);
        _lifetimeCts.Cancel();
        CancelCycleOperations();
        ResetManualProbeSession("shutdown");

        if (_hardwareMonitorTask is not null)
        {
            try { await _hardwareMonitorTask; } catch { }
        }

        // Các commit đã nhận trước thời điểm shutdown phải hoàn tất trước khi
        // đóng store/service. Mỗi trường là task mới nhất của một hàng đợi có
        // semaphore tuần tự, nên chờ task cuối cũng chờ toàn bộ task trước đó.
        try { await _modelPersistenceTask; } catch { }
        try { await _probePersistenceTask; } catch { }
        try { await _removalPersistenceTask; } catch { }
        try { await _masterPersistenceTask; } catch { }
        try { await _statisticsLoadTask; } catch { }

        _cycleActive = false;
        _waitForProductRelease = false;
        _waitForFaultProductRemoval = false;
        _productDetectedThisCycle = false;
        Interlocked.Exchange(ref _postContinuityStarted, 0);
        Interlocked.Exchange(ref _wiringFaultHandlingStarted, 0);
        Interlocked.Exchange(ref _probeSessionActive, 0);

        _sound.SetWiringFaultAlarm(false);
        _sound.StopAll();
        _engine.SetFrameProcessingEnabled(false);

        try
        {
            // DisconnectAsync V10.8 tự chạy đúng sequence cuối của Htdrv:
            // STOP -> RESET -> INIT1 -> INIT2 -> STOP -> FT_Close.
            // Không gửi cleanup trùng lặp từ ViewModel nữa.
            await _board.DisconnectAsync();
        }
        catch (Exception ex)
        {
            AddLog($"Cleanup board khi thoát chưa hoàn chỉnh: {ex.Message}");
        }

        try
        {
            _visa.Dispose();
        }
        catch
        {
        }

        try
        {
            await _labelPrintService.DisposeAsync();
        }
        catch (Exception ex)
        {
            AddLog($"Cleanup máy in tem khi thoát chưa hoàn chỉnh: {ex.Message}");
        }

        ProductionPersistenceService? persistence;
        lock (_historyStoreGate)
            persistence = _productionPersistence;
        if (persistence is not null)
        {
            try
            {
                await persistence.DisposeAsync();
            }
            catch (Exception ex)
            {
                AddLog($"Không thể đóng phiên lưu dữ liệu production hoàn chỉnh: {ex.Message}");
            }
        }

        _engine.Changed -= OnEngineChanged;
        _board.Log -= OnBoardLog;
        _board.FrameReceived -= OnBoardFrameReceived;
        _board.ProductionProbePreviewReceived -= OnProductionProbePreviewReceived;
        _waterProof.Log -= OnWaterProofLog;
        _lifetimeCts.Dispose();
    }

    public async Task<LabelPrinterConnectionResult> ConnectLabelPrinterAsync(
        LabelSettings settings,
        CancellationToken ct = default)
    {
        LabelPrinterConnectionResult result = await _labelPrintService.ConnectAsync(settings, ct);
        InvokeUi(() =>
        {
            LabelStatusText = $"TEM: {result.Message}";
            Raise(nameof(IsLabelPrinterConnected));
            Raise(nameof(LabelPrinterConnectedPort));
        });
        AddLog($"LABEL PRINTER: {result.Message}");
        return result;
    }

    public async Task AutoConnectLabelPrinterAsync()
    {
        if (string.IsNullOrWhiteSpace(_productionSettings.Label.PrinterCom))
        {
            InvokeUi(() => LabelStatusText = "TEM: CHƯA CHỌN CỔNG IN");
            return;
        }

        try
        {
            await ConnectLabelPrinterAsync(_productionSettings.Label, _lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
    }

    public async Task DisconnectLabelPrinterAsync(CancellationToken ct = default)
    {
        await _labelPrintService.DisconnectAsync(ct);
        InvokeUi(() =>
        {
            LabelStatusText = "TEM: KHÔNG DÙNG CỔNG COM";
            Raise(nameof(IsLabelPrinterConnected));
            Raise(nameof(LabelPrinterConnectedPort));
        });
        AddLog("LABEL PRINTER: đã đóng cổng COM theo lựa chọn của người vận hành.");
    }

    public Task<LabelPrintTransportResult> PrintSettingsLabelAsync(
        LabelPrintRequest request,
        CancellationToken ct = default) =>
        _labelPrintService.PrintPassLabelAsync(request, ct);

    public async Task StartProbeScanAsync()
    {
        PrepareProbeUiMode();
        try
        {
            if (!_board.IsConnected)
                throw new InvalidOperationException("Bo JBZ chưa kết nối.");

            bool started = await _scanSupervisor.EnsureProductionScanAsync(
                _model?.MaxIo ?? 0,
                _lifetimeCts.Token);
            if (started)
            {
                InvokeUi(UpdateCardScanningState);
            }

            StartManualProbeSession("manual-start");
            AddLog("TESTPIN/Probe observer ON - dùng stream Production, không reset chu kỳ/engine/relay.");
        }
        catch (Exception ex)
        {
            EnterDeviceFault(ex, "ProbeScan");
        }
    }

    public async Task StopProbeScanAsync()
    {
        // Probe Pin là observer bắt buộc của TestWindow, không còn trạng thái
        // OFF trong production. API legacy được giữ để caller cũ không lỗi,
        // nhưng chỉ bảo đảm stream Production vẫn chạy.
        await EnsureContinuousProductionScanAsync();
        if (IsDeviceFault)
            return;
        ResetManualProbeSession("manual-stop");
        ClearInlineProbeContactsState(clearLastSeen: true);
        InvokeUi(ClearInlineProbeDisplay);
        AddLog("TESTPIN/Probe session OFF; stream Production vẫn tiếp tục.");
    }

    /// <summary>
    /// Learning chỉ được chạy từ MainWindow/background. ScanSupervisor vẫn là
    /// owner duy nhất; maxIo=0 yêu cầu toàn bộ số card operator đã cấu hình.
    /// </summary>
    public async Task StartTopologyLearningAsync()
    {
        if (IsDeviceFault || !_board.IsConnected)
            throw new InvalidOperationException("Bo chưa kết nối hoặc đang lỗi.");
        if (CurrentRuntimeMode != RuntimeMode.Background ||
            _cycleActive || _waitForProductRelease || _waitForFaultProductRemoval ||
            Volatile.Read(ref _resultRecordedThisCycle) != 0)
        {
            throw new InvalidOperationException(
                "Chỉ được học topology ở MainWindow khi không có chu kỳ Production đang chờ xử lý.");
        }

        try
        {
            Volatile.Write(ref _topologyLearningActive, 1);
            bool started = await _scanSupervisor.EnsureProductionScanAsync(0, _lifetimeCts.Token);
            if (started)
                InvokeUi(UpdateCardScanningState);
            AddLog("TOPOLOGY LEARNING ON - quét toàn bộ card đã cấu hình, không ARM Production.");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _topologyLearningActive, 0);
            EnterDeviceFault(ex, "TopologyLearning");
        }
    }

    public async Task StopTopologyLearningAsync()
    {
        if (IsDeviceFault || !_board.IsConnected || CurrentRuntimeMode != RuntimeMode.Background)
        {
            Volatile.Write(ref _topologyLearningActive, 0);
            return;
        }

        try
        {
            await EnsureContinuousProductionScanAsync();
            AddLog("TOPOLOGY LEARNING OFF - khôi phục dải scan nền của mã hiện tại.");
        }
        finally
        {
            Volatile.Write(ref _topologyLearningActive, 0);
        }
    }

    /// <summary>
    /// Được TestWindow gọi tự động ngay sau khi người vận hành bấm
    /// "BẮT ĐẦU KIỂM TRA" tại MainWindow. Không còn nút Start I/O thứ hai.
    /// </summary>
    public Task StartProductionTestAsync() => StartTestAsync();

    private async Task StartTestAsync()
    {
        AsyncFileLogService.Current.Performance("TEST_START_CLICK");

        // Sản phẩm do scan nền phát hiện chỉ khóa thao tác ĐỔI MÃ. Khi process
        // vừa mở lại và vẫn giữ model cuối, chuyển thẳng sang Production để
        // frame kế tiếp nhận diện continuity đang có. Gate sau kết quả đã commit
        // hoặc gate thuộc runtime Production vẫn giữ nguyên để tránh test trùng.
        bool resumeCurrentStartupModel =
            IsProductRemovalPending &&
            Volatile.Read(ref _discardStandaloneLocked) == 0 &&
            !_requireStartupIoClear &&
            CurrentRuntimeMode == RuntimeMode.Background &&
            Volatile.Read(ref _resultRecordedThisCycle) == 0;

        if (IsProductRemovalPending && !resumeCurrentStartupModel)
        {
            bool discardLocked = Volatile.Read(ref _discardStandaloneLocked) != 0;
            State = discardLocked
                ? "THÙNG LỖI ĐÃ KHÓA - CHỜ NHẢ CẢM BIẾN"
                : "VUI LÒNG THÁO SẢN PHẨM";
            AddLog(discardLocked
                ? "BLOCKED: _DISCARD đã nhận hàng; chờ cảm biến nhả để hoàn tất xác nhận 1/1."
                : "BLOCKED: chưa thể bắt đầu kiểm tra vì sản phẩm chưa được tháo hoàn toàn khỏi JIG.");
            return;
        }

        if (resumeCurrentStartupModel)
        {
            SetProductRemovalPending(false);
            Interlocked.Exchange(ref _removalMonitoringFromMain, 0);
            Interlocked.Exchange(ref _startupIoInterlockState, 2);
            _startupIoWarningSignature = string.Empty;
            AddLog("START: giữ mã hiện tại và nhận tiếp sản phẩm đang lắp; removal gate vẫn khóa thao tác đổi mã.");
        }

        if (IsDeviceFault)
        {
            AddLog("Không thể bắt đầu chu kỳ mới vì TestWindow đang khóa DeviceFault.");
            return;
        }

        if (IsManualModeActive)
        {
            State = "MANUAL";
            AddLog("Không thể bắt đầu Production Test vì Manual Mode đang bật.");
            return;
        }

        if (_model is null)
        {
            throw new InvalidOperationException(
                "Chưa tải model .tht.");
        }

        bool ioMappingMode = IsIoMappingMode;

        if (!_board.IsConnected)
        {
            ReportBoardUnavailableForOperatorAction("StartProduction.NoBoard");
            return;
        }

        if (!_board.IsScanning || _board.CurrentScanMode != BoardScanMode.Production)
        {
            State = "BO ĐANG CHUẨN BỊ";
            AddLog("Chưa thể ARM kiểm tra vì luồng quét nền chưa sẵn sàng; vui lòng chờ trạng thái CHỜ LẮP SẢN PHẨM.");
            return;
        }

        // Production và TestPin loại trừ lẫn nhau. Đổi generation trước khi
        // ARM engine để callback Probe/Background cũ không thể lọt sang test.
        Interlocked.Exchange(ref _probeSessionActive, 0);
        SwitchRuntimeMode(RuntimeMode.Production);
        ResetManualProbeSession("new-test");
        AsyncFileLogService.Current.Performance("TEST_ARM_BEGIN");

        // Chu kỳ mới có CancellationToken riêng. Khi đóng TestView/thoát app,
        // mọi delay/relay/đo còn chạy của chu kỳ cũ sẽ bị hủy trước cleanup board.
        CancellationToken cycleToken = BeginCycleOperations();

        // Chu kỳ mới bắt đầu với trạng thái cảnh báo sạch.
        _cycleActive = !ioMappingMode && !_requireStartupIoClear && MasterApproved;
        SetProductionPhase(_cycleActive ? ProductionPhase.Continuity : ProductionPhase.WaitingProduct);
        Interlocked.Exchange(
            ref _startupIoInterlockState,
            !ioMappingMode && _requireStartupIoClear ? 0 : 2);
        _startupIoWarningSignature = string.Empty;
        _waitForProductRelease = false;
        _waitForFaultProductRemoval = false;
        Interlocked.Exchange(ref _waterProofRunning, 0);
        Interlocked.Exchange(ref _postContinuityStarted, 0);
        Interlocked.Exchange(ref _wiringFaultHandlingStarted, 0);
        Interlocked.Exchange(ref _masterPostStarted, 0);
        Interlocked.Exchange(ref _firstFrameReceivedLogged, 0);
        Interlocked.Exchange(ref _firstLogicalStateLogged, 0);
        Interlocked.Exchange(ref _firstUiUpdateRenderedLogged, 0);
        Interlocked.Exchange(ref _stalePreCycleFrameLogged, 0);
        Interlocked.Exchange(ref _secondRequiredNetSeenLogged, 0);
        Interlocked.Exchange(ref _continuityPassedLogged, 0);
        Interlocked.Exchange(ref _productionFramesReceived, 0);
        Interlocked.Exchange(ref _productionFramesProcessed, 0);
        Interlocked.Exchange(ref _productionFramesDropped, 0);
        Interlocked.Exchange(ref _productionFramesRoutedToProbe, 0);
        Interlocked.Exchange(ref _engineUiUpdatesScheduled, 0);
        Interlocked.Exchange(ref _engineUiUpdatesRendered, 0);
        Interlocked.Exchange(ref _uiRequestCount, 0);
        Interlocked.Exchange(ref _uiDispatcherEnqueueCount, 0);
        Interlocked.Exchange(ref _uiRenderedCount, 0);
        Interlocked.Exchange(ref _lastContinuousScanMetricsTick, 0);
        _lastPassGateSignature = string.Empty;
        _lastPassRemainingSignature = string.Empty;
        _lastProductEvidenceSignature = string.Empty;
        _lastIoMappingSignature = string.Empty;
        _lastLiveTopologySnapshot = LiveTopologySnapshot.Empty();
        lock (_probePreviewGate)
            _pendingProductionProbePreview = null;
        Interlocked.Exchange(ref _probePresentationState, (int)ProbePresentationState.Inactive);
        Interlocked.Exchange(ref _productionPresentationMode, (int)ProductionPresentationMode.Waiting);
        Interlocked.Increment(ref _ioMappingUiRevision);
        ClearInlineProbeContactsState(clearLastSeen: true);
        InvokeUi(ClearInlineProbeDisplay);
        _sound.SetWiringFaultAlarm(false);
        // V12.9.5: engine phải chạy cả khi Master Gate đang khóa để state machine
        // tự xác nhận Good/Bad Master. Context Master không được ghi production result.
        _engine.SetFrameProcessingEnabled(!ioMappingMode);
        _engine.Reset();
        if (ioMappingMode)
        {
            MasterApproved = true;
            MasterState = MasterSequenceState.Completed;
            MasterStatus = "IO MAPPING MODE • KHÔNG PASS/FAIL";
        }
        _productDetectedThisCycle = false;
        ResetProductPresentationCycle();
        Interlocked.Exchange(ref _productStartSoundPlayed, 0);
        Interlocked.Exchange(ref _resultRecordedThisCycle, 0);
        Interlocked.Exchange(ref _probeCycleRecordedThisCycle, 0);
        _activeCycleId = Guid.NewGuid().ToString("N");
        _lastFaultRejectSignature = string.Empty;
        _cycleStartedAt = DateTime.Now;
        _ = PersistActiveCycleStageAsync("ARMED");
        _cycleTestStartedAt = null;
        _cycleRemovalStartedAt = null;
        ResetCycleInspectionTrace();
        _recordedHistoryCycleId = string.Empty;
        _recordedHistoryStore = null;
        UpdateDailyLotDisplay();

        Resistance.Clear();
        ResistanceDisplayRows.Clear();
        RefreshFaults();

        RaiseTestStatistics();
        SelectedOperationTabIndex = 0;

        long cycleStartSequence = Volatile.Read(ref _lastObservedProductionFrameSequence);
        long cycleStartGeneration = Volatile.Read(ref _lastObservedProductionScanGeneration);
        Volatile.Write(ref _cycleStartFrameSequence, cycleStartSequence);
        Volatile.Write(ref _cycleStartScanGeneration, cycleStartGeneration);
        Interlocked.Exchange(ref _freshFrameGateActive, cycleStartSequence > 0 ? 1 : 0);
        AsyncFileLogService.Current.Performance("TEST_START_SCAN_REUSED");
        AsyncFileLogService.Current.Performance(
            $"FRESH_FRAME_GATE_ARMED active={cycleStartSequence > 0} cycleStartSeq={cycleStartSequence} " +
            $"generation={cycleStartGeneration}");
        AddLog("Tái sử dụng scan nền Production đang chạy; ARM không gửi lệnh phần cứng.");
        InvokeUi(UpdateCardScanningState);

        if (ioMappingMode)
        {
            State = "LẮP SẢN PHẨM";
            AddLog(
                "THT trống: đã bật chế độ lập bản đồ IO. Đầu dò và các cặp IO thông nhau " +
                "chỉ hiển thị trên bảng; không PASS/FAIL, không cộng sản lượng và không kích relay.");
            return;
        }

        if (_requireStartupIoClear)
        {
            State = "ĐANG ĐỒNG BỘ DỮ LIỆU BO...";
            AddLog("Đang chờ một frame hoàn chỉnh và sạch trước khi ARM Production/Master.");
            return;
        }

        if (!MasterApproved)
        {
            await StartAutomaticMasterSequenceAsync();
            return;
        }

        // Scan nền có thể phát frame ngay trong lúc StartTestAsync đang ARM.
        // Không được ghi đè ĐANG KIỂM TRA/ĐO ĐIỆN TRỞ vừa được callback cập
        // nhật bằng trạng thái CHỜ LẮP (UI=SẴN SÀNG).
        if (CurrentProductionPhase == ProductionPhase.Continuity &&
            Volatile.Read(ref _postContinuityStarted) == 0)
        {
            State = _engine.HasProductActivity
                ? "ĐANG KIỂM TRA..."
                : "CHỜ LẮP SẢN PHẨM";
        }
        AddLog("Đã ARM chu kỳ production trên luồng scan I/O đang chạy liên tục.");
        AsyncFileLogService.Current.Performance("TEST_ARM_READY");
    }

    private async Task StopTestAsync()
    {
        ResetManualProbeSession("leave-test-view");
        if (Volatile.Read(ref _discardStandaloneLocked) != 0)
        {
            CancelCycleOperations();
            Interlocked.Exchange(ref _removalMonitoringFromMain, 1);
            _cycleActive = false;
            SetProductionPhase(ProductionPhase.WaitingProductRemoval);
            SwitchRuntimeMode(RuntimeMode.Background);
            _engine.SetFrameProcessingEnabled(false);
            State = "THÙNG LỖI ĐÃ KHÓA - CHỜ NHẢ CẢM BIẾN";

            if (_board.IsConnected && !_board.IsScanning)
                await EnsureContinuousProductionScanAsync();

            AddLog("Đã về màn hình chính nhưng vẫn giữ khóa _DISCARD và chờ cảm biến nhả để hoàn tất 1/1.");
            return;
        }

        // Nếu người vận hành quay về Main khi sản phẩm đang lắp dở, chuyển sang
        // cùng removal gate với PASS/FAIL. Reset snapshot để frame mới xác nhận
        // việc tháo hoàn toàn; không suy diễn một cạnh vừa mất là ProductRemoved.
        if (!IsProductRemovalPending && _engine.HasProductActivity)
        {
            ResetEngineWithoutChangedReentry();
            _engine.SetFrameProcessingEnabled(true);
            _waitForProductRelease = true;
            SetProductRemovalPending(true);
        }

        // Khóa/cancel workflow TRƯỚC khi gửi lệnh board. Mọi trạng thái đang
        // chờ tháo phải tiếp tục được giám sát sau khi TestWindow đã đóng.
        if (IsProductRemovalPending)
        {
            if (!_waitForProductRelease && !_waitForFaultProductRemoval)
            {
                ResetEngineWithoutChangedReentry();
                _engine.SetFrameProcessingEnabled(true);
                _waitForProductRelease = true;
            }

            CancelCycleOperations();
            Interlocked.Exchange(ref _removalMonitoringFromMain, 1);
            _cycleActive = true;
            SetProductionPhase(ProductionPhase.WaitingProductRemoval);
            _engine.SetFrameProcessingEnabled(true);
            Interlocked.Exchange(ref _postContinuityStarted, 0);
            Interlocked.Exchange(ref _wiringFaultHandlingStarted, 0);
            _sound.SetTestPointContactSound(false);
            _sound.SetWiringFaultAlarm(false);
            State = "VUI LÒNG THÁO SẢN PHẨM";

            if (_board.IsConnected && !_board.IsScanning)
                await EnsureContinuousProductionScanAsync();

            AddLog("Đã về màn hình chính nhưng vẫn giữ khóa ProductRemoved và tiếp tục giám sát IO.");
            return;
        }

        _cycleActive = false;
        Interlocked.Exchange(ref _removalMonitoringFromMain, 0);
        SetProductionPhase(ProductionPhase.WaitingProduct);
        SwitchRuntimeMode(RuntimeMode.Background);
        CancelCycleOperations();

        try
        {
            // Đóng TestView không dừng phần cứng. Bo tiếp tục quét I/O nền để
            // lần mở TestView sau không có độ trễ START/INIT.
            _engine.SetFrameProcessingEnabled(false);
            _engine.Reset();
            if (_board.IsConnected && !_board.IsScanning)
                await EnsureContinuousProductionScanAsync();
        }
        finally
        {
            _waitForProductRelease = false;
            _waitForFaultProductRemoval = false;
            SetProductRemovalPending(false);
            _productDetectedThisCycle = false;
            Interlocked.Exchange(ref _postContinuityStarted, 0);
            Interlocked.Exchange(ref _wiringFaultHandlingStarted, 0);
            _sound.SetTestPointContactSound(false);
            _sound.SetWiringFaultAlarm(false);
            State = ReadyStateForCurrentModel();
            AddLog("Đã rời TestView; scan I/O nền vẫn chạy liên tục.");
        }
    }

    private bool IsProbeSessionActive =>
        IsRuntimeMode(RuntimeMode.Probe) &&
        Volatile.Read(ref _probeSessionActive) != 0;

    private bool IsManualForbiddenWorkActive =>
        _cycleActive ||
        _waitForProductRelease ||
        _waitForFaultProductRemoval ||
        Volatile.Read(ref _probeSessionActive) != 0 ||
        Volatile.Read(ref _postContinuityStarted) != 0 ||
        Volatile.Read(ref _wiringFaultHandlingStarted) != 0 ||
        Volatile.Read(ref _masterPostStarted) != 0 ||
        Volatile.Read(ref _masterEjectStarted) != 0 ||
        CurrentRuntimeMode == RuntimeMode.Probe;

    private void AbortProductionFaultForProbe()
    {
        Interlocked.Exchange(ref _wiringFaultHandlingStarted, 0);
        _sound.SetWiringFaultAlarm(false);
    }

    private async Task HandleWiringFaultAsync(long generation)
    {
        // Callback production cũ có thể đã được schedule ngay trước khi mở
        // TestPin. Probe mode tuyệt đối không được hiện popup chập/đấu sai.
        if (!IsProductionFaultContext(generation))
        {
            AbortProductionFaultForProbe();
            SetProductionPhase(ProductionPhase.Continuity);
            return;
        }

        if (_model is null)
            return;

        ProductModel cycleModel = _model;
        CancellationToken cycleToken = CurrentCycleToken();

        _cycleActive = false;
        SetProductionPhase(ProductionPhase.WaitingFaultConfirmation);
        State = "ĐANG XỬ LÝ LỖI DÂY";
        SelectedOperationTabIndex = 0;

        // TESTPOINT.wav phải kêu liên tục cho tới khi người vận hành xác nhận.
        _sound.SetWiringFaultAlarm(true);

        try
        {
            if (_board.IsConnected)
            {
                await StopScanIntentionallyAsync("WiringFaultConfirmation");
                await _board.AllRelaysOffAsync();
            }
        }
        catch (Exception ex)
        {
            EnterDeviceFault(ex, "WiringFault.StopBoard");
            return;
        }

        // TestPin có thể được mở trong lúc handler production đang await
        // StopScan. Phải kiểm tra LẠI sau await; nếu không callback cũ vẫn
        // có thể bật popup chập dù transport đã chuyển sang Probe.
        if (!IsProductionFaultContext(generation))
        {
            AbortProductionFaultForProbe();
            return;
        }

        WiringFaultPair[] wiringPairs = _engine.WiringFaults
            .OrderBy(x => x.SourceIo)
            .ThenBy(x => x.TargetIo)
            .ToArray();

        FaultDetail[] dialogFaults = wiringPairs
            .Select(pair => EnrichFaultDetail(new FaultDetail
            {
                Type = pair.FaultType,
                ExpectedSourceIo = pair.ExpectedSourceIo,
                ExpectedTargetIo = pair.ExpectedTargetIo,
                ActualSourceIo = pair.SourceIo,
                ActualTargetIo = pair.TargetIo,
                RelatedIos = [pair.SourceIo, pair.TargetIo],
                Message = pair.Reason
            }))
            .GroupBy(fault => new
            {
                fault.Type,
                fault.ExpectedSourceIo,
                fault.ExpectedTargetIo,
                fault.ActualSourceIo,
                fault.ActualTargetIo,
                fault.WireName
            })
            .Select(group => group.First())
            .ToArray();

        if (dialogFaults.Length == 0)
        {
            AbortProductionFaultForProbe();
            SetProductionPhase(ProductionPhase.Continuity);
            State = "ĐANG KIỂM TRA...";
            return;
        }

        ProductFaultType primaryType = dialogFaults
            .Select(fault => fault.Type)
            .OrderBy(FaultTypeCatalog.Priority)
            .First();
        string primaryName = FaultTypeCatalog.DisplayName(primaryType);
        State = FaultDisplayFormatter.OperatorInstruction(primaryType);

        AddLog(
            $"DỪNG TEST do {primaryName}: " +
            string.Join(", ", dialogFaults.Select(fault =>
                $"{fault.Code} {fault.ExpectedText} {fault.ActualText}".Trim())));

        // Chốt cuối ngay trước UI modal. Từ thời điểm Probe bật, tuyệt đối
        // không được phép hiện popup production.
        if (!IsProductionFaultContext(generation))
        {
            AbortProductionFaultForProbe();
            SetProductionPhase(ProductionPhase.Continuity);
            return;
        }

        // Popup NG chỉ được mở sau khi result FAIL đã commit thành công.
        if (!_productDetectedThisCycle)
            _cycleStartedAt = DateTime.Now;
        bool committed = await RecordCompletedProductAsync(false, primaryName, cycleModel, generation, cycleToken);
        if (!committed)
        {
            AbortProductionFaultForProbe();
            await RecoverAfterUncommittedFailAsync(
                cycleModel,
                generation,
                cycleToken,
                "WIRING_FAIL");
            return;
        }

        AddLog(
            "[NG-DIALOG] " +
            $"CycleId={_activeCycleId} Reason=Committed{FaultTypeCatalog.Code(primaryType)} " +
            $"State={State} ReadyToTest={_engine.ReadyToEvaluateProductFaults} FrameValid={_engine.LastFrameValid}");

        ShowFaultConfirmationDialog(dialogFaults, cycleModel);
        SelectedOperationTabIndex = 0;

        // Sau xác nhận FAIL chỉ relay đã được người cài đặt thử và chọn là
        // relay mở JIG được pulse. Không chạy chuỗi MARKING của PASS.
        _sound.SetWiringFaultAlarm(false);

        try
        {
            State = "ĐANG MỞ JIG HÀNG LỖI";
            await _engine.EjectFaultProductAsync();
            AddLog($"Lỗi đã xác nhận: {FaultJigRelayText()} pulse đúng 1 lần rồi OFF; không chạy MARKING PASS.");

            ArmFaultProductRemoval(cycleModel);
            await StartProductionScanAndVerifyFrameAsync(
                CurrentCycleToken(),
                "FAIL_CONFIRM_RELAY");
            State = _waitForFaultProductRemoval
                ? FaultRemovalWaitingText(cycleModel)
                : "LẮP SẢN PHẨM";
        }
        catch (Exception ex)
        {
            EnterDeviceFault(ex, "WiringFault.EjectOrScan");
        }
    }

    private string DescribeIoCompact(int io)
    {
        PinRecord? pin = FindPinByIo(io);
        if (pin is null)
            return $"IO {io}";

        string connector = EmptyAsDash(pin.Connector);
        string pinNumber = EmptyAsDash(pin.PinNumber);
        return $"IO {io} / {connector}-PIN{pinNumber}";
    }

    private IReadOnlyList<FaultDetail> CaptureFaultDetails()
    {
        var details = _engine.WiringFaults
            .Select(pair =>
            {
                var detail = new FaultDetail
                {
                    Type = pair.FaultType,
                    ExpectedSourceIo = pair.ExpectedSourceIo,
                    ExpectedTargetIo = pair.ExpectedTargetIo,
                    ActualSourceIo = pair.SourceIo,
                    ActualTargetIo = pair.TargetIo,
                    RelatedIos = new[] { pair.SourceIo, pair.TargetIo },
                    Message = pair.Reason
                };

                PinRecord? fromPin = FindPinByIo(pair.ExpectedSourceIo ?? pair.SourceIo);
                PinRecord? toPin = FindPinByIo(pair.ExpectedTargetIo ?? pair.TargetIo);
                PinRecord? actualFromPin = FindPinByIo(pair.SourceIo);
                PinRecord? actualToPin = FindPinByIo(pair.TargetIo);
                if (fromPin is not null)
                {
                    detail.ConnectorFrom = fromPin.Connector;
                    detail.PinFrom = fromPin.PinNumber;
                    detail.WireName = fromPin.WireName;
                    detail.WireColor = fromPin.Color;
                }
                if (toPin is not null)
                {
                    detail.ConnectorTo = toPin.Connector;
                    detail.PinTo = toPin.PinNumber;
                }
                if (actualFromPin is not null)
                {
                    detail.ActualConnectorFrom = actualFromPin.Connector;
                    detail.ActualPinFrom = actualFromPin.PinNumber;
                }
                if (actualToPin is not null)
                {
                    detail.ActualConnectorTo = actualToPin.Connector;
                    detail.ActualPinTo = actualToPin.PinNumber;
                }
                return detail;
            })
            .ToList();

        if (!MasterApproved && IsMasterBadPhase)
        {
            // Master BAD giữ semantics evidence riêng, không phải product FAIL.
            details.AddRange(_engine.BuildRows()
                .Where(row => row.ProductFaultType != ProductFaultType.None &&
                              row.ProductFaultType != ProductFaultType.SystemDeviceError)
                .Select(row => EnrichFaultDetail(row.ToFaultDetail())));
        }
        else
        {
            // OPEN/missing expected connections are not product faults in current Production flow.
        }

        foreach (ResistanceResult resistance in Resistance.Where(item => !item.Passed))
            details.Add(CreateResistanceFaultDetail(resistance));

        foreach (WaterProofChannelResult channel in WaterProofChannels
                     .Where(item => item.Enabled && item.IsMeasured && !item.Passed))
        {
            details.Add(CreateWaterProofFaultDetail(channel));
        }

        return details
            .GroupBy(fault => new
            {
                fault.Type,
                fault.ExpectedSourceIo,
                fault.ExpectedTargetIo,
                fault.ActualSourceIo,
                fault.ActualTargetIo,
                fault.WireName,
                fault.MeasuredResistance
            })
            .Select(group => group.First())
            .OrderBy(fault => FaultTypeCatalog.Priority(fault.Type))
            .ToArray();
    }

    private FaultDetail EnrichFaultDetail(FaultDetail detail)
    {
        int? fromIo = detail.ExpectedSourceIo
            ?? detail.ActualSourceIo
            ?? detail.RelatedIos.ElementAtOrDefault(0);
        int? toIo = detail.ExpectedTargetIo
            ?? detail.ActualTargetIo
            ?? detail.RelatedIos.ElementAtOrDefault(1);

        if (fromIo is int source && source > 0)
        {
            PinRecord? fromPin = FindPinByIo(source);
            if (fromPin is not null)
            {
                detail.ConnectorFrom = fromPin.Connector;
                detail.PinFrom = fromPin.PinNumber;
                if (string.IsNullOrWhiteSpace(detail.WireName))
                    detail.WireName = fromPin.WireName;
                if (string.IsNullOrWhiteSpace(detail.WireColor))
                    detail.WireColor = fromPin.Color;
            }
        }

        if (toIo is int target && target > 0)
        {
            PinRecord? toPin = FindPinByIo(target);
            if (toPin is not null)
            {
                detail.ConnectorTo = toPin.Connector;
                detail.PinTo = toPin.PinNumber;
            }
        }

        if (detail.ActualSourceIo is int actualSource)
        {
            PinRecord? actualFromPin = FindPinByIo(actualSource);
            if (actualFromPin is not null)
            {
                detail.ActualConnectorFrom = actualFromPin.Connector;
                detail.ActualPinFrom = actualFromPin.PinNumber;
            }
        }

        if (detail.ActualTargetIo is int actualTarget)
        {
            PinRecord? actualToPin = FindPinByIo(actualTarget);
            if (actualToPin is not null)
            {
                detail.ActualConnectorTo = actualToPin.Connector;
                detail.ActualPinTo = actualToPin.PinNumber;
            }
        }

        return detail;
    }

    private static FaultDetail CreateResistanceFaultDetail(ResistanceResult resistance) => new()
    {
        Type = ProductFaultType.ResistanceOutOfRange,
        MeasuredResistance = resistance.ValueOhm,
        ResistanceMin = resistance.MinOhm,
        ResistanceMax = resistance.MaxOhm,
        WireName = resistance.Name,
        Message = $"{resistance.ChannelText} {resistance.Name}: MIN {resistance.MinDisplayText}; Đo {resistance.Display}; MAX {resistance.MaxDisplayText}"
    };

    private FaultDetail? GetVisiblePrimaryFault()
    {
        if (_visiblePrimaryFaultSnapshotValid)
            return _visiblePrimaryFaultSnapshot;

        bool stateIsFault =
            State.Contains("LỖI", StringComparison.OrdinalIgnoreCase) ||
            State.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
            State.Contains("CHẬP", StringComparison.OrdinalIgnoreCase) ||
            State.Contains("SAI KẾT NỐI", StringComparison.OrdinalIgnoreCase) ||
            State.Contains("HỞ MẠCH", StringComparison.OrdinalIgnoreCase) ||
            State.Contains("ĐẤU SAI", StringComparison.OrdinalIgnoreCase) ||
            State.Contains("DÂY CHƯA", StringComparison.OrdinalIgnoreCase) ||
            State.Contains("ĐIỆN TRỞ KHÔNG ĐẠT", StringComparison.OrdinalIgnoreCase) ||
            State.Contains("KÍN NƯỚC", StringComparison.OrdinalIgnoreCase);

        if (!_productDetectedThisCycle && !_waitForFaultProductRemoval && !stateIsFault)
        {
            _visiblePrimaryFaultSnapshot = null;
            _visiblePrimaryFaultSnapshotValid = true;
            return null;
        }

        _visiblePrimaryFaultSnapshot = CaptureFaultDetails().FirstOrDefault();
        _visiblePrimaryFaultSnapshotValid = true;
        return _visiblePrimaryFaultSnapshot;
    }

    private static string EmptyAsDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private void ResetMasterGateForModel()
    {
        MasterApproved = false;
        _masterGoodVerified = false;
        _masterBadVerified = false;
        _masterFaultCollectionLocked = false;
        _masterDetectedFaultKeys.Clear();
        _masterDetectedFaultDetails.Clear();
        MasterFaults.Clear();
        _masterRequiredFaultCount = _model is null
            ? Math.Clamp(_productionSettings.MasterFaultRequiredCount, 0, 99)
            : ProductionConfigService.GetMasterFaultRequiredCount(_productionSettings, _model);
        Interlocked.Exchange(ref _masterPostStarted, 0);
        Interlocked.Exchange(ref _masterEjectStarted, 0);
        Interlocked.Exchange(ref _legacyGoodMasterRecorded, 0);
        Interlocked.Exchange(ref _legacyBadMasterRecorded, 0);
        Interlocked.Exchange(ref _masterBadCollectNotBeforeUtcTicks, 0);
        ResetMasterHistoryTracking();

        if (_masterRequiredFaultCount <= 0)
        {
            MasterApproved = true;
            MasterState = MasterSequenceState.Completed;
            MasterStatus = "MASTER DISABLED";
            State = ReadyStateForCurrentModel();
            AddLog("MASTER DISABLED - cấu hình Số lỗi Master tối thiểu = 0, vào Production trực tiếp.");
            RaiseMasterState();
            return;
        }

        MasterState = MasterSequenceState.WaitingGoodMaster;
        MasterStatus = "KIỂM TRA MASTER PASS";
        State = "KIỂM TRA MASTER PASS";
        RaiseMasterState();
    }

    private void RaiseMasterState()
    {
        Raise(nameof(MasterRequiredFaultCount));
        Raise(nameof(MasterDetectedFaultCount));
        Raise(nameof(WrongCountText));
        Raise(nameof(NetworkProgress));
        Raise(nameof(IsMasterBadPhase));
        Raise(nameof(IsMasterSequenceActive));
        Raise(nameof(ProductionEnabled));
        RaiseActiveFault();
    }

    private async Task StartAutomaticMasterSequenceAsync()
    {
        if (_model is null || MasterApproved)
            return;

        if (MasterRequiredFaultCount <= 0)
        {
            MasterApproved = true;
            MasterState = MasterSequenceState.Completed;
            MasterStatus = "MASTER DISABLED";
            State = "CHỜ LẮP SẢN PHẨM";
            RaiseMasterState();
            return;
        }

        if (!_board.IsConnected)
        {
            ReportBoardUnavailableForOperatorAction("MasterSequence.NoBoard");
            return;
        }

        _productionSettings.AutoMasterSequence = true;
        _cycleActive = false;
        _productDetectedThisCycle = false;
        Interlocked.Exchange(ref _productStartSoundPlayed, 0);
        Interlocked.Exchange(ref _resultRecordedThisCycle, 0);
        _waitForProductRelease = false;
        _waitForFaultProductRemoval = false;
        _masterGoodVerified = false;
        _masterBadVerified = false;
        _masterFaultCollectionLocked = false;
        _masterDetectedFaultKeys.Clear();
        _masterDetectedFaultDetails.Clear();
        MasterFaults.Clear();
        Interlocked.Exchange(ref _masterPostStarted, 0);
        Interlocked.Exchange(ref _masterEjectStarted, 0);
        Interlocked.Exchange(ref _legacyGoodMasterRecorded, 0);
        Interlocked.Exchange(ref _legacyBadMasterRecorded, 0);
        Interlocked.Exchange(ref _masterBadCollectNotBeforeUtcTicks, 0);
        ResetMasterHistoryTracking();

        _engine.SetFrameProcessingEnabled(true);
        ResetEngineWithoutChangedReentry();
        RefreshFaults();

        MasterState = MasterSequenceState.WaitingGoodMaster;
        State = "KIỂM TRA MASTER PASS";
        MasterStatus = "KIỂM TRA MASTER PASS";
        AddLog("MASTER GOOD START - production gate LOCKED; không cộng LOT/Pass/Fail.");

        await EnsureContinuousProductionScanAsync();
        if (IsDeviceFault)
            return;

        RaiseMasterState();
    }

    private void ProcessMasterEngineChangedOnUi(long generation)
    {
        if (IsDeviceFault || !IsRuntimeContext(RuntimeMode.Production, generation) || MasterApproved)
            return;

        RefreshFaults();

        switch (MasterState)
        {
            case MasterSequenceState.WaitingGoodMaster:
                if (_engine.HasProductActivity)
                {
                    BeginMasterHistoryCycle(HistoryInspectionType.MasterGood);
                    MasterState = MasterSequenceState.TestingGoodMaster;
                    State = "KIỂM TRA MASTER PASS";
                    MasterStatus = "KIỂM TRA MASTER PASS";
                    AddLog("MASTER GOOD: phát hiện mẫu, bắt đầu kiểm tra tự động.");
                }
                break;

            case MasterSequenceState.TestingGoodMaster:
                if (_engine.IsProductReleased)
                {
                    MarkMasterRemoved();
                    Interlocked.Exchange(ref _masterPostStarted, 0);
                    MasterState = MasterSequenceState.WaitingGoodMaster;
                    ResetEngineWithoutChangedReentry();
                    ResetProductPresentationCycle();
                    RefreshFaults();
                    State = "KIỂM TRA MASTER PASS";
                    MasterStatus = "KIỂM TRA MASTER PASS";
                    AddLog("MASTER GOOD chưa PASS và đã tháo; giữ gate LOCKED, chờ kiểm tra lại.");
                    break;
                }

                if (_engine.HasWiringFault)
                {
                    State = "MASTER PASS - FAIL";
                    MasterStatus = "MẪU MASTER PASS ĐANG CÓ LỖI DÂY - KIỂM TRA / THÁO MẪU";
                    // Không alarm/eject theo logic Product FAIL. Good master chỉ được eject sau PASS thật.
                    _sound.SetWiringFaultAlarm(false);
                    if (Interlocked.CompareExchange(ref _masterPostStarted, 1, 0) == 0)
                    {
                        CaptureMasterTestStartedAt();
                        RecordMasterHistory(
                            HistoryInspectionType.MasterGood,
                            passed: false,
                            CaptureFaultDetails());
                        MarkMasterRemovalStarted();
                    }
                    break;
                }

                if (_engine.ContinuityPassed &&
                    Interlocked.CompareExchange(ref _masterPostStarted, 1, 0) == 0)
                {
                    CaptureMasterTestStartedAt();
                    _ = CompleteGoodMasterAsync(generation);
                }
                break;

            case MasterSequenceState.EjectingGoodMaster:
                if (_engine.IsProductReleased)
                {
                    MarkMasterRemoved();
                    TransitionToBadMaster();
                }
                break;

            case MasterSequenceState.WaitingBadMaster:
                if (_engine.HasProductActivity)
                {
                    BeginMasterHistoryCycle(HistoryInspectionType.MasterBad);
                    _presentationCycleStarted = true;
                    RaiseCenterPresentation();
                    MasterState = MasterSequenceState.TestingBadMaster;
                    State = "KIỂM TRA MẪU MASTER LỖI";
                    MasterStatus = State;
                    Interlocked.Exchange(
                        ref _masterBadCollectNotBeforeUtcTicks,
                        DateTime.UtcNow.AddMilliseconds(MasterBadSettleMs).Ticks);
                    AddLog($"MASTER BAD START - cần {MasterRequiredFaultCount} fault dây duy nhất.");
                    _ = CollectMasterFaultsAfterSettleAsync(generation);
                }
                break;

            case MasterSequenceState.TestingBadMaster:
                if (_engine.IsProductReleased)
                {
                    // Không xóa HashSet 1/N: cùng mẫu có thể mất contact tạm thời. Gate chỉ mở ở N/N.
                    Interlocked.Exchange(ref _masterBadCollectNotBeforeUtcTicks, 0);
                    MasterState = MasterSequenceState.WaitingBadMaster;
                    ResetEngineWithoutChangedReentry();
                    ResetProductPresentationCycle();
                    RefreshFaults();
                    State = "LẮP SẢN PHẨM";
                    MasterStatus = "KIỂM TRA MẪU MASTER LỖI";
                    AddLog($"MASTER BAD released khi mới {MasterDetectedFaultCount}/{MasterRequiredFaultCount}; không mở Production.");
                    break;
                }

                CollectCurrentMasterFaults(generation);
                break;

            case MasterSequenceState.EjectingBadMaster:
                if (_engine.IsProductReleased)
                {
                    MarkMasterRemoved();
                    CompleteMasterValidation();
                }
                break;
        }
    }

    private async Task CollectMasterFaultsAfterSettleAsync(long generation)
    {
        try
        {
            await Task.Delay(MasterBadSettleMs, CurrentCycleToken());
        }
        catch (OperationCanceledException)
        {
            return;
        }

        InvokeUi(() => CollectCurrentMasterFaults(generation));
    }

    private void CollectCurrentMasterFaults(long generation)
    {
        if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
            MasterApproved ||
            MasterState != MasterSequenceState.TestingBadMaster ||
            _masterFaultCollectionLocked)
        {
            return;
        }

        long notBeforeTicks = Interlocked.Read(ref _masterBadCollectNotBeforeUtcTicks);
        if (notBeforeTicks > 0 && DateTime.UtcNow.Ticks < notBeforeTicks)
            return;

        CaptureMasterTestStartedAt();

        FaultDetail[] candidates = CaptureFaultDetails()
            .Where(fault => fault.Type is
                ProductFaultType.OpenCircuit or
                ProductFaultType.WrongWiring or
                ProductFaultType.ShortCircuit)
            .OrderBy(fault => FaultTypeCatalog.Priority(fault.Type))
            .ThenBy(fault => fault.ExpectedSourceIo ?? fault.ActualSourceIo ?? 0)
            .ThenBy(fault => fault.ExpectedTargetIo ?? fault.ActualTargetIo ?? 0)
            .ToArray();

        foreach (FaultDetail fault in candidates)
        {
            if (_masterFaultCollectionLocked)
                break;

            MasterFaultKey key = MasterFaultKey.From(fault);
            if (!_masterDetectedFaultKeys.Add(key))
            {
                // V12.10.2: cùng cạnh điện có thể được engine mô tả trước là
                // Short rồi frame sau có đủ Expected* để mô tả WrongWiring rõ hơn.
                // Không tăng bộ đếm, chỉ nâng chất lượng dòng đang hiển thị.
                if (_masterDetectedFaultDetails.TryGetValue(key, out FaultDetail? existing) &&
                    existing is not null &&
                    ShouldReplaceMasterFaultDetail(existing, fault))
                {
                    _masterDetectedFaultDetails[key] = fault;
                    RebuildMasterFaultDisplayRows();
                    SynchronizeFaultRows(BuildMasterFaultGridRows());
                }

                continue;
            }

            int number = _masterDetectedFaultKeys.Count;
            _masterDetectedFaultDetails[key] = fault;
            RebuildMasterFaultDisplayRows();
            SynchronizeFaultRows(BuildMasterFaultGridRows());
            MasterStatus = "KIỂM TRA MẪU MASTER LỖI";
            State = MasterStatus;
            AddLog(
                $"MASTER BAD FAULT {number}/{MasterRequiredFaultCount} " +
                $"{FaultTypeCatalog.Code(fault.Type)} | {fault.Summary}");
            RaiseMasterState();

            if (number >= MasterRequiredFaultCount)
            {
                _masterFaultCollectionLocked = true;
                _masterBadVerified = true;
                RecordMasterHistory(
                    HistoryInspectionType.MasterBad,
                    passed: true,
                    _masterDetectedFaultDetails.Values.ToArray());
                _ = EjectValidatedBadMasterAsync(generation);
                break;
            }
        }
    }

    /// <summary>
    /// V12.10.1: FaultGrid là vùng hiển thị chính cho cả MasterBad và Production.
    /// MasterBad chỉ đưa các fault đã được HashSet xác nhận unique vào grid; vì vậy
    /// cùng IO/fault lặp 50-100 frame không sinh thêm dòng.
    /// </summary>
    private static bool ShouldReplaceMasterFaultDetail(FaultDetail current, FaultDetail candidate)
    {
        static int Quality(FaultDetail fault)
        {
            int score = 0;
            if (fault.ExpectedSourceIo is int expectedSource && expectedSource > 0) score += 4;
            if (fault.ExpectedTargetIo is int expectedTarget && expectedTarget > 0) score += 4;
            if (fault.ActualSourceIo is int actualSource && actualSource > 0) score += 2;
            if (fault.ActualTargetIo is int actualTarget && actualTarget > 0) score += 2;
            if (!string.IsNullOrWhiteSpace(fault.ConnectorFrom)) score += 1;
            if (!string.IsNullOrWhiteSpace(fault.PinFrom)) score += 1;
            if (!string.IsNullOrWhiteSpace(fault.WireName)) score += 1;

            // Khi cùng một cạnh bị phân loại hai cách, WrongWiring có Expected*
            // hữu ích hơn cho người vận hành so với nhãn Short chung chung.
            if (fault.Type == ProductFaultType.WrongWiring) score += 1;
            return score;
        }

        return Quality(candidate) > Quality(current);
    }

    private void RebuildMasterFaultDisplayRows()
    {
        MasterFaults.Clear();
        int number = 0;
        foreach (FaultDetail detail in _masterDetectedFaultDetails.Values)
        {
            number++;
            MasterFaults.Add(BuildMasterFaultDisplayRow(number, detail));
        }
    }

    private IReadOnlyList<FaultRow> BuildMasterFaultGridRows()
    {
        return _masterDetectedFaultDetails.Values
            .OrderBy(fault => FaultTypeCatalog.Priority(fault.Type))
            .ThenBy(fault => fault.ActualSourceIo ?? fault.ExpectedSourceIo ?? fault.RelatedIos.FirstOrDefault())
            .ThenBy(fault => fault.ActualTargetIo ?? fault.ExpectedTargetIo ?? 0)
            .Select(BuildMasterFaultGridRow)
            .ToArray();
    }

    private FaultRow BuildMasterFaultGridRow(FaultDetail fault)
    {
        int primaryIo = fault.ExpectedSourceIo
            ?? fault.ActualSourceIo
            ?? fault.ExpectedTargetIo
            ?? fault.ActualTargetIo
            ?? fault.RelatedIos.FirstOrDefault();

        PinRecord? pin = primaryIo > 0 ? FindPinByIo(primaryIo) : null;

        string status = fault.Type switch
        {
            ProductFaultType.WrongWiring => "SAI DÂY",
            ProductFaultType.ShortCircuit => "CHẬP MẠCH",
            ProductFaultType.OpenCircuit => "HỞ MẠCH",
            _ => fault.Summary
        };

        FaultKind kind = fault.Type switch
        {
            ProductFaultType.OpenCircuit => FaultKind.Open,
            ProductFaultType.WrongWiring => FaultKind.WrongWiring,
            ProductFaultType.ShortCircuit => FaultKind.Short,
            ProductFaultType.ResistanceOutOfRange => FaultKind.Resistance,
            _ => FaultKind.Info
        };

        return new FaultRow
        {
            Kind = kind,
            ProductFaultType = fault.Type,
            FaultType = FaultTypeCatalog.DisplayName(fault.Type),
            Io = primaryIo,
            ExpectedSourceIo = fault.ExpectedSourceIo,
            ExpectedTargetIo = fault.ExpectedTargetIo,
            ActualSourceIo = fault.ActualSourceIo,
            ActualTargetIo = fault.ActualTargetIo,
            RelatedIos = fault.RelatedIos,
            Connector = pin?.Connector ?? fault.ConnectorFrom,
            Pin = pin?.PinNumber ?? fault.PinFrom,
            WireName = pin?.WireName ?? fault.WireName,
            Splice = pin?.SpliceName ?? string.Empty,
            Section = pin?.Section ?? string.Empty,
            Color = pin?.Color ?? fault.WireColor,
            Status = status
        };
    }

    private MasterFaultDisplayRow BuildMasterFaultDisplayRow(int number, FaultDetail fault)
    {
        string detail = string.Empty;
        string expected = string.Empty;
        string actual = string.Empty;

        if (fault.Type == ProductFaultType.WrongWiring)
        {
            if (fault.ExpectedSourceIo is int es && fault.ExpectedTargetIo is int et)
                expected = $"Tiêu chuẩn: {DescribeIoCompact(es)}  →  {DescribeIoCompact(et)}";
            if (fault.ActualSourceIo is int actualSource && fault.ActualTargetIo is int actualTarget)
                actual = $"Thực tế: {DescribeIoCompact(actualSource)}  →  {DescribeIoCompact(actualTarget)}";
        }
        else
        {
            int[] ios = fault.RelatedIos
                .Concat(new[] { fault.ExpectedSourceIo ?? 0, fault.ExpectedTargetIo ?? 0, fault.ActualSourceIo ?? 0, fault.ActualTargetIo ?? 0 })
                .Where(io => io > 0)
                .Distinct()
                .Take(6)
                .ToArray();

            if (ios.Length > 0)
                detail = string.Join("  ↔  ", ios.Select(DescribeIoCompact));
            else
                detail = fault.Summary;
        }

        return new MasterFaultDisplayRow
        {
            Number = number,
            RequiredCount = MasterRequiredFaultCount,
            Title = FaultTypeCatalog.DisplayName(fault.Type),
            Detail = detail,
            Expected = expected,
            Actual = actual
        };
    }

    private async Task CompleteGoodMasterAsync(long generation)
    {
        if (_model is null ||
            !IsRuntimeContext(RuntimeMode.Production, generation) ||
            MasterState != MasterSequenceState.TestingGoodMaster)
        {
            Interlocked.Exchange(ref _masterPostStarted, 0);
            return;
        }

        CancellationToken ct = CurrentCycleToken();
        try
        {
            Resistance.Clear();
            ResistanceDisplayRows.Clear();
            bool resistancePassed = true;
            DateTime? masterPassAt = null;

            if (IsResistanceEnabledForModel(_model))
            {
                await EnsureKeysightConnectedAsync();
                _scanSupervisor.Suspend("Resistance");
                List<ResistanceResult> results = await _engine.MeasureResistanceAsync(ct);
                foreach (ResistanceResult result in results)
                {
                    Resistance.Add(result);
                    ResistanceDisplayRows.Add(result);
                }
                resistancePassed = Resistance.Count == ResistanceMeasurementPlan.BuildEnabledSteps(_productionSettings).Count && Resistance.All(item => item.Passed);
            }

            if (!resistancePassed)
            {
                State = "MASTER PASS - FAIL";
                MasterStatus = "MASTER PASS: ĐIỆN TRỞ KHÔNG ĐẠT - KHÔNG MỞ PRODUCTION";
                AddLog("MASTER GOOD FAIL - resistance out of range; không eject tự động.");
                RecordMasterHistory(
                    HistoryInspectionType.MasterGood,
                    passed: false,
                    Resistance
                        .Where(item => !item.Passed)
                        .Select(CreateResistanceFaultDetail)
                        .ToArray());
                MarkMasterRemovalStarted();
                return;
            }

            await WaitForProbeRelayInterlockAsync(ct);
            if (!_engine.ContinuityPassed || _engine.HasWiringFault)
            {
                State = "MASTER PASS - FAIL";
                MasterStatus = "MASTER PASS MẤT ĐIỀU KIỆN PASS - KIỂM TRA LẠI";
                AddLog("MASTER GOOD FAIL - continuity không còn PASS trước relay.");
                RecordMasterHistory(
                    HistoryInspectionType.MasterGood,
                    passed: false,
                    CaptureFaultDetails());
                MarkMasterRemovalStarted();
                return;
            }

            bool ok = await _engine.CompletePassAsync(
                Resistance,
                onPassStarted: () =>
                {
                    masterPassAt ??= DateTime.Now;
                    State = "HOÀN THÀNH MẪU MASTER ĐẠT";
                    MasterStatus = State;
                    _sound.PlayTestOk();
                },
                markingEnabled: false,
                ct: ct);

            if (!ok)
            {
                State = "MASTER PASS - FAIL";
                MasterStatus = "MASTER PASS KHÔNG HOÀN THÀNH PASS - KIỂM TRA LẠI";
                AddLog("MASTER GOOD FAIL - CompletePassAsync trả false.");
                RecordMasterHistory(
                    HistoryInspectionType.MasterGood,
                    passed: false,
                    CaptureFaultDetails());
                MarkMasterRemovalStarted();
                return;
            }

            // Chốt trạng thái an toàn trước khi khởi động lại scan Master.
            await _board.AllRelaysOffAsync(CancellationToken.None);

            _masterGoodVerified = true;
            RecordMasterHistory(
                HistoryInspectionType.MasterGood,
                passed: true,
                [],
                masterPassAt);
            MarkMasterRemovalStarted();
            TryAppendLegacyMasterHistory(goodMaster: true);
            MasterState = MasterSequenceState.EjectingGoodMaster;
            State = "HOÀN THÀNH MẪU MASTER ĐẠT";
            MasterStatus = State;
            AddLog("MASTER GOOD PASS");
            AddLog("MASTER GOOD EJECT - Relay 1 JIG; không MARKING, không cộng sản lượng.");

            // CompletePass đã STOP/RESET transport. Reset nội bộ không được phát callback
            // EjectingGoodMaster giả; chỉ frame scan thật sau restart mới xác nhận RELEASE.
            ResetEngineWithoutChangedReentry();
            _engine.SetFrameProcessingEnabled(true);
            await StartProductionScanAndVerifyFrameAsync(ct, "MASTER_GOOD_EJECT");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MasterStatus = "MẤT KẾT NỐI MÁY TEST - VUI LÒNG KHỞI ĐỘNG LẠI";
            AddLog($"LỖI KIỂM TRA MASTER PASS: {ex}");
            EnterDeviceFault(ex, "MasterGood");
        }
        finally
        {
            // Giữ latch cho tới khi MASTER GOOD được tháo/nhả. Nếu resistance hoặc
            // continuity không đạt, không được tự đo/lặp PASS liên tục theo từng frame.
            // Nhánh IsProductReleased ở state TestingGoodMaster sẽ reset latch về 0.
            SelectedOperationTabIndex = 0;
            RaiseMasterState();
        }
    }

    private void TransitionToBadMaster()
    {
        if (!_masterGoodVerified || MasterApproved)
            return;

        // Đổi state trước khi reset để tuyệt đối không thể tái nhập
        // EjectingGoodMaster -> TransitionToBadMaster -> Reset -> Changed -> ...
        MasterState = MasterSequenceState.WaitingBadMaster;
        ResetEngineWithoutChangedReentry();
        _masterDetectedFaultKeys.Clear();
        _masterDetectedFaultDetails.Clear();
        MasterFaults.Clear();
        _masterFaultCollectionLocked = false;
        _masterBadVerified = false;
        Interlocked.Exchange(ref _masterEjectStarted, 0);
        Interlocked.Exchange(ref _masterBadCollectNotBeforeUtcTicks, 0);

        ResetProductPresentationCycle();
        RefreshFaults();
        State = "LẮP SẢN PHẨM";
        MasterStatus = "KIỂM TRA MẪU MASTER LỖI";
        AddLog("MASTER GOOD đã tháo khỏi JIG. Chuyển sang MASTER BAD tự động.");
        RaiseMasterState();
    }

    private async Task EjectValidatedBadMasterAsync(long generation)
    {
        if (_model is null ||
            !IsRuntimeContext(RuntimeMode.Production, generation) ||
            MasterDetectedFaultCount < MasterRequiredFaultCount ||
            Interlocked.CompareExchange(ref _masterEjectStarted, 1, 0) != 0)
        {
            return;
        }

        CancellationToken ct = CurrentCycleToken();
        try
        {
            _masterFaultCollectionLocked = true;
            MasterState = MasterSequenceState.EjectingBadMaster;
            State = "HOÀN THÀNH MẪU MASTER LỖI";
            MasterStatus = State;
            _sound.SetWiringFaultAlarm(false);
            AddLog($"MASTER BAD PASS - đủ {MasterDetectedFaultCount}/{MasterRequiredFaultCount} fault duy nhất.");

            // MASTER BAD fault là EXPECTED evidence: chỉ eject JIG sau N/N, không dùng Product FAIL behavior.
            await _engine.EjectMasterSampleAsync(ct);
            MarkMasterRemovalStarted();
            TryAppendLegacyMasterHistory(goodMaster: false);
            AddLog("MASTER BAD EJECT - Relay 1 JIG tự động; không tăng FAIL/LOT.");

            // Chờ frame thật xác nhận MASTER BAD đã rời jig; Reset không được
            // tự phát Changed và hoàn tất Master Gate ngay trong cùng call stack.
            ResetEngineWithoutChangedReentry();
            _engine.SetFrameProcessingEnabled(true);
            await StartProductionScanAndVerifyFrameAsync(ct, "MASTER_BAD_EJECT");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MasterStatus = "MẤT KẾT NỐI MÁY TEST - VUI LÒNG KHỞI ĐỘNG LẠI";
            AddLog($"LỖI EJECT MASTER: {ex}");
            Interlocked.Exchange(ref _masterEjectStarted, 0);
            EnterDeviceFault(ex, "MasterBad");
        }
        finally
        {
            RaiseMasterState();
        }
    }

    private void BeginMasterHistoryCycle(string inspectionType)
    {
        DateTime now = DateTime.Now;
        _masterInstallStartedAt = now;
        _masterTestStartedAt = null;
        _masterRemovalStartedAt = null;
        _masterHistoryCycleId = Guid.NewGuid().ToString("N");
        _masterHistoryInspectionType = inspectionType;
        _masterRecordedHistoryStore = null;

        if (inspectionType == HistoryInspectionType.MasterBad)
        {
            Resistance.Clear();
            ResistanceDisplayRows.Clear();
        }
    }

    private void ResetMasterHistoryTracking()
    {
        _masterInstallStartedAt = null;
        _masterTestStartedAt = null;
        _masterRemovalStartedAt = null;
        _masterHistoryCycleId = string.Empty;
        _masterHistoryInspectionType = string.Empty;
        _masterRecordedHistoryStore = null;
    }

    private void CaptureMasterTestStartedAt()
    {
        DateTime now = DateTime.Now;
        _masterInstallStartedAt ??= now;
        _masterTestStartedAt ??= now < _masterInstallStartedAt.Value
            ? _masterInstallStartedAt.Value
            : now;
    }

    private void RecordMasterHistory(
        string inspectionType,
        bool passed,
        IReadOnlyList<FaultDetail> faults,
        DateTime? resultAtOverride = null)
    {
        ProductModel? model = _model;
        if (model is null ||
            !string.Equals(_masterHistoryInspectionType, inspectionType, StringComparison.Ordinal) ||
            _masterRecordedHistoryStore is not null)
        {
            return;
        }

        DateTime resultAt = resultAtOverride ?? DateTime.Now;
        DateTime installStarted = _masterInstallStartedAt ?? resultAt;
        DateTime testStarted = _masterTestStartedAt ?? resultAt;
        if (testStarted < installStarted || testStarted > resultAt)
            testStarted = resultAt;

        string cycleId = string.IsNullOrWhiteSpace(_masterHistoryCycleId)
            ? Guid.NewGuid().ToString("N")
            : _masterHistoryCycleId;
        FaultDetail[] faultSnapshot = faults
            .OrderBy(fault => FaultTypeCatalog.Priority(fault.Type))
            .ToArray();
        FaultDetail? primaryFault = faultSnapshot.FirstOrDefault();
        CustomerFaultDisplay? customerFault = primaryFault is null
            ? null
            : FaultDisplayFormatter.FormatCustomer(primaryFault);
        string resistanceText = string.Join(
            "; ",
            Resistance.Select(item => $"{item.Name}={item.Display}({item.ResultText})"));

        var history = new TestHistoryRecord
        {
            Started = installStarted,
            Finished = resultAt,
            InstallStartedAt = installStarted,
            TestStartedAt = testStarted,
            ResultAt = resultAt,
            InspectionType = inspectionType,
            PartName = model.ProductName,
            PartNumber = model.PartNumber,
            VehicleType = model.VehicleType,
            Eco = model.Eco,
            Nco = model.Nco,
            Alc = model.Alc,
            LotNo = 0,
            ProductionCounter = 0,
            Result = inspectionType switch
            {
                HistoryInspectionType.MasterGood when passed => "MASTER_GOOD_PASS",
                HistoryInspectionType.MasterGood => "MASTER_GOOD_FAIL",
                HistoryInspectionType.MasterBad when passed => "MASTER_BAD_CONFIRMED",
                _ => "MASTER_BAD_FAIL"
            },
            Passed = passed,
            ModelName = model.ModelName,
            ModelFile = model.SourcePath,
            HtdrvName = ProgramIdentityService.BuildHtdrvName(),
            LotText = _productionSettings.Lot,
            InspectionTrace = $"{resultAt:HH:mm:ss} 회로검사:{(
                inspectionType == HistoryInspectionType.MasterBad
                    ? (passed ? "FAIL" : "PASS")
                    : (passed ? "PASS" : "FAIL"))}",
            OpenCount = faultSnapshot.Count(fault => fault.Type == ProductFaultType.OpenCircuit),
            WrongCount = faultSnapshot.Count(fault => fault.Type == ProductFaultType.WrongWiring),
            ShortCount = faultSnapshot.Count(fault => fault.Type == ProductFaultType.ShortCircuit),
            Resistance = resistanceText,
            DeviceName = _productionSettings.DeviceName,
            DeviceNumber = _productionSettings.DeviceNumber,
            OperatorCompany = _productionSettings.OperatorCompany,
            ProductionLine = _productionSettings.ProductionLine,
            FaultType = customerFault?.FaultType ?? string.Empty,
            FaultCode = primaryFault?.Code ?? string.Empty,
            ExpectedSourceIo = primaryFault?.ExpectedSourceIo,
            ExpectedTargetIo = primaryFault?.ExpectedTargetIo,
            ActualSourceIo = primaryFault?.ActualSourceIo,
            ActualTargetIo = primaryFault?.ActualTargetIo,
            FaultDetailsJson = System.Text.Json.JsonSerializer.Serialize(faultSnapshot),
            FaultSummary = customerFault is null
                ? string.Empty
                : FaultDisplayFormatter.CustomerSummary(customerFault),
            MeasuredResistance = primaryFault?.MeasuredResistance,
            ResistanceMin = primaryFault?.ResistanceMin,
            ResistanceMax = primaryFault?.ResistanceMax,
            CycleId = cycleId,
            PrintStatus = LabelPrintStatus.NotRequested.ToString()
        };

        TestHistoryStore store = HistoryStore;
        _masterRecordedHistoryStore = store;
        _masterHistoryCycleId = cycleId;
        ProductionResultCommitRequest request = ProductionResultCommitRequest.Capture(
            history,
            model,
            _productionSettings,
            faultSnapshot,
            Resistance.ToArray(),
            waterProof: null,
            ProgramIdentityService.VersionText);
        _masterPersistenceTask = PersistMasterHistoryAsync(request, inspectionType, cycleId);
    }

    private async Task PersistMasterHistoryAsync(
        ProductionResultCommitRequest request,
        string inspectionType,
        string cycleId)
    {
        try
        {
            await ProductionPersistence.CommitTestResultAsync(request, _lifetimeCts.Token);
            AddLog(
                $"History: {inspectionType} cycle {cycleId} đã commit SQLite; " +
                "không tăng LOT/sản lượng/Probe counter.");
        }
        catch (Exception ex)
        {
            AddLog($"LỖI LƯU DỮ LIỆU MASTER {inspectionType}: {ex.Message}");
            throw;
        }
    }

    private void MarkMasterRemovalStarted()
    {
        DateTime removalStarted = _masterRemovalStartedAt ?? DateTime.Now;
        _masterRemovalStartedAt = removalStarted;
        UpdateMasterRemovalHistory(removalStarted, null);
    }

    private void MarkMasterRemoved()
    {
        if (_masterRecordedHistoryStore is null || string.IsNullOrWhiteSpace(_masterHistoryCycleId))
            return;

        DateTime removedAt = DateTime.Now;
        DateTime removalStarted = _masterRemovalStartedAt ?? removedAt;
        _masterRemovalStartedAt = removalStarted;
        UpdateMasterRemovalHistory(removalStarted, removedAt);
    }

    private void UpdateMasterRemovalHistory(DateTime removalStarted, DateTime? removedAt)
    {
        TestHistoryStore? store = _masterRecordedHistoryStore;
        string cycleId = _masterHistoryCycleId;
        if (store is null || string.IsNullOrWhiteSpace(cycleId))
            return;

        _masterPersistenceTask = UpdateMasterRemovalHistoryAsync(
            cycleId, removalStarted, removedAt);
    }

    private async Task UpdateMasterRemovalHistoryAsync(
        string cycleId,
        DateTime removalStarted,
        DateTime? removedAt)
    {
        long timestamp = Stopwatch.GetTimestamp();
        try
        {
            if (await ProductionPersistence.UpdateRemovalTimingAsync(
                    cycleId, removalStarted, removedAt, _lifetimeCts.Token))
            {
                double durationMs = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
                AsyncFileLogService.Current.Performance(
                    $"HISTORY_MASTER_REMOVAL cycle={cycleId} complete={removedAt.HasValue} db_ms={durationMs:0.###}");
            }
        }
        catch (Exception ex)
        {
            AddLog($"Không thể cập nhật thời gian tháo MASTER cycle {cycleId}: {ex.Message}");
        }
    }

    private void TryAppendLegacyMasterHistory(bool goodMaster)
    {
        ProductModel? model = _model;
        if (model is null)
            return;

        bool reserved = goodMaster
            ? Interlocked.CompareExchange(ref _legacyGoodMasterRecorded, 1, 0) == 0
            : Interlocked.CompareExchange(ref _legacyBadMasterRecorded, 1, 0) == 0;
        if (!reserved)
            return;

        Task databaseCommit = _masterPersistenceTask;
        _masterPersistenceTask = AppendLegacyMasterHistoryAsync(
            databaseCommit, model, goodMaster);
    }

    private async Task AppendLegacyMasterHistoryAsync(
        Task databaseCommit,
        ProductModel model,
        bool goodMaster)
    {
        try
        {
            await databaseCommit;
            ProbeCounterSnapshot counter = await ProductionPersistence.GetProbeCounterAsync(
                PartIdentitySnapshot.Capture(model),
                ProbeReplacementThreshold,
                _lifetimeCts.Token);
            string path = await Task.Run(() => _legacyHistory.AppendMaster(
                model,
                DateTime.Now,
                counter.Counter,
                goodMaster), _lifetimeCts.Token);
            AddLog(
                $"PHT HISTORY: {(goodMaster ? "MASTER GOOD" : "MASTER BAD")} " +
                $"đã append sau SQLite commit vào {path}; counter không tăng.");
        }
        catch (Exception ex)
        {
            if (goodMaster)
                Interlocked.Exchange(ref _legacyGoodMasterRecorded, 0);
            else
                Interlocked.Exchange(ref _legacyBadMasterRecorded, 0);
            AddLog($"LEGACY_EXPORT_FAILED MASTER: {ex.Message}");
        }
    }

    private void CompleteMasterValidation()
    {
        if (!_masterGoodVerified || !_masterBadVerified || MasterDetectedFaultCount < MasterRequiredFaultCount)
            return;

        MasterApproved = true;
        MasterState = MasterSequenceState.Completed;
        _masterFaultCollectionLocked = true;
        _cycleActive = true;
        SetProductionPhase(ProductionPhase.Continuity);
        _productDetectedThisCycle = false;
        Interlocked.Exchange(ref _resultRecordedThisCycle, 0);
        _waitForProductRelease = false;
        _waitForFaultProductRemoval = false;
        Interlocked.Exchange(ref _postContinuityStarted, 0);
        Interlocked.Exchange(ref _wiringFaultHandlingStarted, 0);
        Interlocked.Exchange(ref _masterPostStarted, 0);
        Interlocked.Exchange(ref _masterEjectStarted, 0);
        _sound.SetWiringFaultAlarm(false);
        ResetEngineWithoutChangedReentry();
        _engine.SetFrameProcessingEnabled(true);
        RefreshFaults();

        State = "CHỜ LẮP SẢN PHẨM";
        MasterStatus = "MASTER HOÀN TẤT • PRODUCTION ENABLED";
        AddLog("MASTER VALIDATION COMPLETED - MASTER GATE PASS, ProductionEnabled=true.");
        RaiseMasterState();
    }

    private async Task MeasureResistanceAsync()
    {
        // Không cho đo thủ công trước khi continuity PASS.
        // Command được giữ lại chỉ để tương thích với XAML/phiên bản cũ.
        if (!_cycleActive || !_engine.ContinuityPassed)
        {
            AddLog("Điện trở chỉ được đo tự động sau khi toàn bộ mạng I/O PASS.");
            return;
        }

        if (Interlocked.CompareExchange(ref _postContinuityStarted, 1, 0) == 0)
            await RunAutomaticPostContinuityAsync();
    }

    private async Task CompleteTestAsync()
    {
        // PASS/relay cũng được tự động hóa để không thể xác nhận PASS trước điện trở.
        if (!_cycleActive || !_engine.ContinuityPassed)
        {
            AddLog("Chưa đủ điều kiện PASS toàn bộ mạng I/O.");
            return;
        }

        if (Interlocked.CompareExchange(ref _postContinuityStarted, 1, 0) == 0)
            await RunAutomaticPostContinuityAsync();
    }

    private void PrepareResistanceRows(ProductModel model)
    {
        Resistance.Clear();
        ResistanceDisplayRows.Clear();

        foreach (ResistanceStep step in ResistanceMeasurementPlan.BuildEnabledSteps(_productionSettings))
        {
            var row = new ResistanceResult
            {
                Name = step.Name,
                Channel = step.Channel,
                MinOhm = step.MinOhm,
                MaxOhm = step.MaxOhm
            };
            Resistance.Add(row);
            ResistanceDisplayRows.Add(row);
        }

        if (IsWaterProofEnabledForCurrentModel() &&
            Volatile.Read(ref _preContinuityWaterProofPassed) != 0)
        {
            string channels = string.Join(", ", _lastWaterProofMeasurements
                .Where(item => item.Enabled)
                .OrderBy(item => item.Channel)
                .Select(item => $"CH{item.Channel}"));
            string values = string.Join("; ", _lastWaterProofMeasurements
                .Where(item => item.Enabled)
                .OrderBy(item => item.Channel)
                .Select(item => $"CH{item.Channel} Δ{item.Leak:0.###}"));
            ResistanceDisplayRows.Add(new ResistanceResult
            {
                Name = "TEST LEAK",
                ChannelTextOverride = channels,
                MinDisplayTextOverride = "—",
                DisplayOverride = values,
                MaxDisplayTextOverride = $"≤ {_waterProofProfile.LeakLimit:0.###}",
                ResultTextOverride = "PASS"
            });
        }
    }

    private bool IsResistanceEnabledForModel(ProductModel? model) =>
        model is not null && ResistanceMeasurementPlan.BuildEnabledSteps(_productionSettings).Count > 0;

    private void UpdateResistanceRows(IReadOnlyList<ResistanceResult> results)
    {
        foreach (ResistanceResult result in results)
        {
            ResistanceResult? row = Resistance.FirstOrDefault(item =>
                item.Channel == result.Channel &&
                string.Equals(item.Name, result.Name, StringComparison.OrdinalIgnoreCase));

            if (row is null)
            {
                Resistance.Add(result);
                ResistanceDisplayRows.Add(result);
                continue;
            }

            row.ValueOhm = result.ValueOhm;
            row.IsOpen = result.IsOpen;
            row.IsStable = result.IsStable;
            row.Passed = result.Passed;
            row.MeasurementStatus = result.MeasurementStatus;
            row.SampleCount = result.SampleCount;
            row.StabilizationTimeMs = result.StabilizationTimeMs;
        }
    }

    private bool IsWaterProofEnabledForCurrentModel() =>
        _model is not null && _waterProofProfile.Enabled;

    private void LoadWaterProofProfileForCurrentModel()
    {
        _waterProofProfile = ProductionConfigService.GetWaterProofProfileForPath(
            _productionSettings,
            CurrentModelPath ?? _model?.SourcePath);
        Interlocked.Exchange(
            ref _waterProofRetestConnectorState,
            (int)WaterProofRetestConnectorState.Inactive);
        Interlocked.Exchange(ref _waterProofRetestAttempt, 0);
        ResetWaterProofDisplay();
        Raise(nameof(IsWaterProofCardVisible));
        Raise(nameof(WaterProofLeakLimitText));
        Raise(nameof(WaterProofPortText));
    }

    private void ResetWaterProofDisplay()
    {
        void ResetCore()
        {
            Array.Clear(
                _waterProofLivePressBaseline,
                0,
                _waterProofLivePressBaseline.Length);

            WaterProofChannels.Clear();
            if (_waterProofProfile.Enabled)
            {
                for (int channel = 1; channel <= 3; channel++)
                {
                    if (_waterProofProfile.IsChannelEnabled(channel))
                        WaterProofChannels.Add(new WaterProofChannelResult
                        {
                            Channel = channel,
                            Enabled = true,
                            Connector = _waterProofProfile.ConnectorForChannel(channel),
                            LeakLimit = _waterProofProfile.LeakLimit
                        });
                }
            }

            _waterProofStage = WaterProofStage.Idle;
            _waterProofStageText = "CHỜ KIỂM TRA";
            _waterProofOverallResult = "---";
            Raise(nameof(WaterProofStageText));
            Raise(nameof(WaterProofOverallResult));
            Raise(nameof(WaterProofCardBackground));
            Raise(nameof(WaterProofAccentBrush));
        }

        InvokeUi(ResetCore);
    }

    private void SetWaterProofStage(WaterProofStage stage, string text, string? overall = null)
    {
        void Apply()
        {
            _waterProofStage = stage;
            _waterProofStageText = text;
            if (overall is not null)
                _waterProofOverallResult = overall;
            Raise(nameof(WaterProofStageText));
            Raise(nameof(WaterProofOverallResult));
            Raise(nameof(WaterProofCardBackground));
            Raise(nameof(WaterProofAccentBrush));
        }

        InvokeUi(Apply);
    }

    private void ApplyWaterProofProgress(WaterProofProgress progress)
    {
        InvokeUi(() =>
        {
            _waterProofStage = progress.Stage;
            _waterProofStageText = progress.Stage switch
            {
                WaterProofStage.Pressurizing => "ĐANG TẠO ÁP",
                WaterProofStage.Waiting => "ĐANG ĐO ĐỘ RÒ",
                WaterProofStage.Evaluating => "ĐANG ĐÁNH GIÁ",
                _ => _waterProofStageText
            };

            if (progress.Stage is WaterProofStage.Pressurizing or WaterProofStage.Waiting)
            {
                for (int i = 0; i < progress.Values.Count && i < 3; i++)
                {
                    WaterProofChannelResult? row =
                        WaterProofChannels.FirstOrDefault(item => item.Channel == i + 1);

                    if (row is null)
                        continue;

                    double current = Math.Abs(progress.Values[i]);
                    row.LiveMachineValue = current;

                    if (progress.Stage == WaterProofStage.Pressurizing)
                    {
                        // Giữ giá trị PRESS mới nhất làm mốc. Khi máy chuyển sang
                        // WAIT, độ rò realtime được tính từ đúng mốc áp cuối này.
                        row.PressPressure = current;
                        row.FirstResultPressure = current;
                        row.SecondResultPressure = null;
                        _waterProofLivePressBaseline[i] = current;

                        // Trong thời gian tạo áp chưa có sụt áp nên hiển thị 0.
                        // Không set IsMeasured/PASS ở đây để tránh hiện PASS sớm.
                        row.Leak = 0.0;
                    }
                    else
                    {
                        row.WaitPressure = current;
                        row.SecondResultPressure = current;

                        if (_waterProofLivePressBaseline[i] is double baseline)
                        {
                            // Cùng semantics với kết quả cuối:
                            // Leak = độ sụt áp tuyệt đối từ PRESS sang WAIT.
                            // Mỗi frame :WAIT làm giá trị trên UI thay đổi ngay.
                            row.Leak = Math.Abs(baseline - current);
                        }
                    }
                }
            }

            Raise(nameof(WaterProofStageText));
            Raise(nameof(WaterProofCardBackground));
            Raise(nameof(WaterProofAccentBrush));
        });
    }

    private Task ApplyWaterProofFinalResultAsync(
        WaterProofRunResult result,
        bool captureForProductHistory = true)
    {
        if (captureForProductHistory)
            _lastWaterProofMeasurements = result.Channels.ToArray();
        return InvokeUiAsync(() =>
        {
            foreach (WaterProofChannelMeasurement measurement in result.Channels.Where(item => item.Enabled))
            {
                WaterProofChannelResult? row = WaterProofChannels.FirstOrDefault(item => item.Channel == measurement.Channel);
                if (row is null)
                {
                    row = new WaterProofChannelResult
                    {
                        Channel = measurement.Channel,
                        Enabled = true,
                        Connector = _waterProofProfile.ConnectorForChannel(measurement.Channel)
                    };
                    WaterProofChannels.Add(row);
                }

                row.LeakLimit = _waterProofProfile.LeakLimit;
                row.FirstResultPressure = measurement.FirstPressure;
                row.SecondResultPressure = measurement.SecondPressure;
                row.Leak = measurement.Leak;
                row.LiveMachineValue = measurement.Leak;
                row.Passed = measurement.Passed;
                row.IsMeasured = true;
            }

            _waterProofStage = result.Passed ? WaterProofStage.Passed : WaterProofStage.Failed;
            _waterProofStageText = result.Passed ? "KÍN NƯỚC PASS" : "KÍN NƯỚC KHÔNG ĐẠT";
            _waterProofOverallResult = result.Passed ? "PASS" : "FAIL";
            SelectedOperationTabIndex = 0;
            Raise(nameof(WaterProofStageText));
            Raise(nameof(WaterProofOverallResult));
            Raise(nameof(WaterProofCardBackground));
            Raise(nameof(WaterProofAccentBrush));
        });
    }

    private void ArmWaterProofFaultRemovalWait()
    {
        // Scan vẫn có thể chạy trong lúc popup Leak đang mở. Reset snapshot
        // trước khi chờ tháo để frame kế tiếp luôn phát Changed; nếu sản phẩm
        // đã được tháo trong popup thì không bị bỏ lỡ ProductRemoved.
        ResetEngineWithoutChangedReentry();
        _engine.SetFrameProcessingEnabled(true);
        Interlocked.Exchange(ref _postContinuityStarted, 0);
        if (_model is ProductModel model)
            ArmFaultProductRemoval(model);
        else
        {
            _waitForFaultProductRemoval = true;
            Interlocked.Exchange(ref _faultProductRemoved, 0);
            Interlocked.Exchange(ref _discardRequiredForFault, 0);
            _discardInterlock.Reset();
            SetProductRemovalPending(true);
            Interlocked.Exchange(ref _removalMonitoringFromMain, 0);
            SetProductionPhase(ProductionPhase.WaitingProductRemoval);
        }
        // Kết quả FAIL đã chốt không được chạy Leak lại. Chỉ khi tháo toàn bộ
        // sản phẩm mới ResetFullCycleAfterProductRemoved() và ARM chu kỳ mới.
    }

    private void ShowWaterProofOperationPanel() =>
        InvokeUi(() =>
        {
            SelectedOperationTabIndex = 0;
            SynchronizeFaultRows(Array.Empty<FaultRow>());
        });

    private static Window? ResolveOperatorDialogOwner()
    {
        Application? application = Application.Current;
        if (application is null)
            return null;

        Window? active = application.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsVisible && window.IsActive);
        if (active is not null)
            return active;

        Window? visible = application.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsVisible);
        if (visible is not null)
            return visible;

        return application.MainWindow is { IsVisible: true } mainWindow
            ? mainWindow
            : null;
    }

    private void ArmWaterProofEquipmentErrorRemovalWait()
    {
        ResetEngineWithoutChangedReentry();
        _engine.SetFrameProcessingEnabled(true);
        _waterProofEquipmentErrorAwaitingRemoval = true;
        _waitForProductRelease = true;
        SetProductRemovalPending(true);
        Interlocked.Exchange(ref _removalMonitoringFromMain, 0);
        Interlocked.Exchange(ref _postContinuityStarted, 0);
        SetProductionPhase(ProductionPhase.EquipmentError);
        SelectedOperationTabIndex = 0;
    }

    private async Task PauseProductionScanForWaterProofAsync(CancellationToken ct)
    {
        if (_board.IsConnected && _board.IsScanning)
            await StopScanIntentionallyAsync("WaterProof", ct);

        AddLog("[WATERPROOF] D2XX scan đã dừng trong công đoạn Leak; giữ snapshot continuity hiện tại.");
    }

    private async Task PauseProductionScanForFinalPassAsync(CancellationToken ct)
    {
        if (_board.IsConnected && _board.IsScanning)
            await StopScanIntentionallyAsync("PassRelaySequence", ct);

        AddLog("[PASS] D2XX scan đã dừng trước chuỗi PASS; khóa snapshot continuity đã xác nhận.");
    }

    private void ArmPassProductRemovalWait()
    {
        // Kết quả PASS đã commit là bất biến. Chỉ reset snapshot PC và ARM
        // ProductRemoved; không gửi RESET_CLEAR lần hai sau chuỗi relay PASS.
        ResetEngineWithoutChangedReentry();
        Interlocked.Exchange(ref _postContinuityStarted, 0);
        _waterProofEquipmentErrorAwaitingRemoval = false;
        _waitForProductRelease = true;
        SetProductRemovalPending(true);
        Interlocked.Exchange(ref _removalMonitoringFromMain, 0);
        _cycleActive = true;
        SetProductionPhase(ProductionPhase.WaitingProductRemoval);
        StartManualProbeSession("post-pass");
        // Không ẩn bảng kết quả ngay khi PASS. Người vận hành phải còn nhìn
        // thấy kết quả cho tới khi D2XX xác nhận toàn bộ continuity đã mất.
        // ResetFullCycleAfterProductRemoved() sẽ chuyển tab về 0.
        State = "PASS - THÁO SẢN PHẨM";
    }

    private bool TryValidateWaterProofConnectorGate(
        ProductModel model,
        out string error)
    {
        for (int channel = 1; channel <= 3; channel++)
        {
            if (!_waterProofProfile.IsChannelEnabled(channel))
                continue;

            string connectorId = _waterProofProfile.ConnectorForChannel(channel).Trim();
            if (string.IsNullOrWhiteSpace(connectorId))
            {
                error = $"CH{channel} chưa chọn connector THT.";
                return false;
            }

            if (!model.Connectors.Any(connector =>
                    string.Equals(connector.ConnectorId, connectorId, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"CH{channel}: connector '{connectorId}' không tồn tại trong THT {model.ModelName}.";
                return false;
            }

            if (!_engine.HasConnectedRetWire(connectorId))
            {
                error = $"LẮP ĐÚNG CẶP DÂY RET QUA CONNECTOR {connectorId} (VÍ DỤ RET1 ↔ RET1).";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private string[] ConfiguredWaterProofConnectorIds() =>
        Enumerable.Range(1, 3)
            .Where(_waterProofProfile.IsChannelEnabled)
            .Select(channel => _waterProofProfile.ConnectorForChannel(channel).Trim())
            .Where(connectorId => connectorId.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void ArmWaterProofRetestConnectorCycle()
    {
        string[] connectorIds = ConfiguredWaterProofConnectorIds();
        bool preContinuityRetry =
            CurrentProductionPhase == ProductionPhase.WaterProof &&
            Volatile.Read(ref _preContinuityWaterProofPassed) == 0;
        if (!IsWaterProofEnabledForCurrentModel() ||
            _lastWaterProofMeasurements.Count == 0 ||
            connectorIds.Length == 0 ||
            (!preContinuityRetry && !_waitForProductRelease && !_waitForFaultProductRemoval))
        {
            Interlocked.Exchange(
                ref _waterProofRetestConnectorState,
                (int)WaterProofRetestConnectorState.Inactive);
            return;
        }

        Interlocked.Exchange(
            ref _waterProofRetestConnectorState,
            (int)WaterProofRetestConnectorState.AwaitingConnectorRemoval);
        AddLog(
            $"[WATERPROOF-RETEST] Đã ARM; tháo rồi lắp lại connector " +
            $"{string.Join(", ", connectorIds)} để chỉ chạy lại Leak trên cùng sản phẩm.");
    }

    private void ObserveWaterProofRetestConnectorCycle(long generation)
    {
        bool preContinuityRetry =
            CurrentProductionPhase == ProductionPhase.WaterProof &&
            Volatile.Read(ref _preContinuityWaterProofPassed) == 0;
        if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
            Volatile.Read(ref _removalMonitoringFromMain) != 0 ||
            !IsWaterProofEnabledForCurrentModel() ||
            (!preContinuityRetry && !_waitForProductRelease && !_waitForFaultProductRemoval) ||
            _model is not ProductModel cycleModel)
        {
            return;
        }

        string[] connectorIds = ConfiguredWaterProofConnectorIds();
        if (connectorIds.Length == 0)
            return;

        var current = (WaterProofRetestConnectorState)Volatile.Read(
            ref _waterProofRetestConnectorState);
        if (current == WaterProofRetestConnectorState.AwaitingConnectorRemoval)
        {
            bool allDisconnected = connectorIds.All(_engine.IsConnectorDisconnected);
            if (!allDisconnected)
                return;

            if (Interlocked.CompareExchange(
                    ref _waterProofRetestConnectorState,
                    (int)WaterProofRetestConnectorState.AwaitingConnectorReconnect,
                    (int)WaterProofRetestConnectorState.AwaitingConnectorRemoval) ==
                (int)WaterProofRetestConnectorState.AwaitingConnectorRemoval)
            {
                State = $"LẮP LẠI CONNECTOR LEAK {string.Join(", ", connectorIds)}";
                AddLog(
                    $"[WATERPROOF-RETEST] Đã xác nhận tháo connector {string.Join(", ", connectorIds)}; " +
                    "sản phẩm vẫn còn trên JIG, chờ lắp lại.");
            }
            return;
        }

        if (current != WaterProofRetestConnectorState.AwaitingConnectorReconnect ||
            !connectorIds.All(_engine.HasConnectedRetWire))
        {
            return;
        }

        if (Interlocked.CompareExchange(
                ref _waterProofRetestConnectorState,
                (int)WaterProofRetestConnectorState.Running,
                (int)WaterProofRetestConnectorState.AwaitingConnectorReconnect) !=
            (int)WaterProofRetestConnectorState.AwaitingConnectorReconnect)
        {
            return;
        }

        AddLog(
            $"[WATERPROOF-RETEST] Connector {string.Join(", ", connectorIds)} đã lắp lại; " +
            "bắt đầu chu kỳ Leak-only.");
        _ = RunWaterProofRetestOnlyAsync(cycleModel, generation);
    }

    private async Task RunWaterProofRetestOnlyAsync(
        ProductModel cycleModel,
        long generation)
    {
        CancellationToken ct = CurrentCycleToken();
        bool ownsWaterProofRun = false;
        bool restartProductionScan = false;
        bool armAnotherRetry = false;

        try
        {
            // FAIL ban đầu ARM chờ tháo ngay trước khi method Leak gốc nhả cờ.
            // Chờ ngắn có hủy để không thể tạo hai owner trên cùng Leak COM.
            for (int attempt = 0; attempt < 40 && !ownsWaterProofRun; attempt++)
            {
                ownsWaterProofRun = Interlocked.CompareExchange(ref _waterProofRunning, 1, 0) == 0;
                if (!ownsWaterProofRun)
                    await Task.Delay(50, ct);
            }

            if (!ownsWaterProofRun)
                throw new InvalidOperationException("Leak COM vẫn đang bận sau khi connector được lắp lại.");

            bool preContinuityRetry =
                CurrentProductionPhase == ProductionPhase.WaterProof &&
                Volatile.Read(ref _preContinuityWaterProofPassed) == 0;
            if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
                Volatile.Read(ref _removalMonitoringFromMain) != 0 ||
                !ReferenceEquals(_model, cycleModel) ||
                !IsWaterProofEnabledForCurrentModel() ||
                !preContinuityRetry)
            {
                return;
            }

            if (!TryValidateWaterProofConnectorGate(cycleModel, out string connectorGateError))
                throw new InvalidOperationException(connectorGateError);

            int retestAttempt = Interlocked.Increment(ref _waterProofRetestAttempt);
            ResetWaterProofDisplay();
            ShowWaterProofOperationPanel();
            State = $"ĐANG TEST LEAK LẠI LẦN {retestAttempt}";
            SetWaterProofStage(WaterProofStage.Connecting, "ĐANG KẾT NỐI", "---");
            AddLog(
                $"[WATERPROOF-RETEST] START attempt={retestAttempt} parentCycle={_activeCycleId}; " +
                "không tăng LOT/sản lượng và không chạy relay.");

            await PauseProductionScanForWaterProofAsync(ct);
            WaterProofRunResult run = await _waterProof.RunTestAsync(
                _productionSettings.WaterProofMachine,
                _waterProofProfile,
                ApplyWaterProofProgress,
                ct);
            await ApplyWaterProofFinalResultAsync(run);
            if (run.Passed)
            {
                Interlocked.Exchange(ref _preContinuityWaterProofPassed, 1);
                Interlocked.Exchange(ref _preContinuityWaterProofStarted, 0);
                Interlocked.Exchange(
                    ref _waterProofRetestConnectorState,
                    (int)WaterProofRetestConnectorState.Inactive);
                SetProductionPhase(ProductionPhase.Continuity);
                State = "LEAK PASS - KIỂM TRA THÔNG MẠCH";
                restartProductionScan = true;
                AddLog(
                    $"[WATERPROOF-RETEST] PASS attempt={retestAttempt}; " +
                    "chuyển sang kiểm tra toàn bộ continuity, không cộng sản lượng và không chạy relay.");
            }
            else if (_engine.ContinuityPassed &&
                     !_engine.HasWiringFault &&
                     _engine.ReadyToEvaluateProductFaults)
            {
                AddLog("[WATERPROOF-RETEST] Leak vẫn FAIL trong khi continuity đã PASS toàn bộ; chốt FAIL sản phẩm.");
                await FinalizeWaterProofProductFailureAsync(cycleModel, generation, ct);
            }
            else
            {
                State = WaterProofRetryInstruction();
                armAnotherRetry = true;
                restartProductionScan = true;
                AddLog(
                    $"[WATERPROOF-RETEST] FAIL attempt={retestAttempt}; " +
                    "chưa chốt lỗi vì continuity chưa PASS toàn bộ, chờ tháo/lắp connector Leak.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AddLog("[WATERPROOF-RETEST] Đã hủy vì rời chu kỳ hiện tại.");
        }
        catch (Exception ex)
        {
            SetWaterProofStage(WaterProofStage.Error, "LỖI LEAK RETEST", "ERROR");
            State = "LỖI LEAK RETEST - THÁO/LẮP CONNECTOR ĐỂ THỬ LẠI";
            armAnotherRetry = true;
            restartProductionScan = true;
            AddLog($"[WATERPROOF-RETEST] ERROR: {ex.Message}");
            try
            {
                await _waterProof.DisconnectAsync();
            }
            catch (Exception disconnectEx)
            {
                AddLog($"[WATERPROOF-RETEST] Không thể disconnect Leak COM: {disconnectEx.Message}");
            }
        }
        finally
        {
            if (ownsWaterProofRun)
                Interlocked.Exchange(ref _waterProofRunning, 0);

            if (restartProductionScan &&
                IsRuntimeContext(RuntimeMode.Production, generation) &&
                Volatile.Read(ref _removalMonitoringFromMain) == 0 &&
                ReferenceEquals(_model, cycleModel) &&
                IsWaterProofEnabledForCurrentModel())
            {
                if (armAnotherRetry && CurrentProductionPhase == ProductionPhase.WaterProof)
                    ArmWaterProofRetestConnectorCycle();
                try
                {
                    await StartProductionScanAndVerifyFrameAsync(
                        CurrentCycleToken(),
                        "WATERPROOF_RETEST_COMPLETE");
                }
                catch (Exception scanEx)
                {
                    AddLog($"[WATERPROOF-RETEST] Không thể restart D2XX chờ tháo/lắp: {scanEx.Message}");
                }
            }
        }
    }

    private async Task<bool> SaveWaterProofRetestHistoryAsync(
        ProductModel model,
        WaterProofRunResult run,
        int attempt,
        DateTime startedAt,
        DateTime finishedAt)
    {
        FaultDetail[] faults = run.Channels
            .Where(channel => channel.Enabled && !channel.Passed)
            .Select(channel => new FaultDetail
            {
                Type = ProductFaultType.WaterProofLeak,
                WireName = $"CH{channel.Channel} • {_waterProofProfile.ConnectorForChannel(channel.Channel)}".TrimEnd(),
                Message = $"CH{channel.Channel}: áp giữ {channel.SecondPressure:0.###}; " +
                          $"độ rò {channel.Leak:0.###}; yêu cầu áp >= {_waterProofProfile.PressMin:0.###}, " +
                          $"độ sụt <= {_waterProofProfile.LeakLimit:0.###}"
            })
            .ToArray();
        var completed = new CompletedTestResult
        {
            Started = startedAt,
            Finished = finishedAt,
            Passed = run.Passed,
            ResultText = run.Passed ? "PASS" : "FAIL",
            Faults = faults
        };
        string summary = BuildWaterProofHistorySummary(run);
        string parentCycleId = string.IsNullOrWhiteSpace(_activeCycleId)
            ? Guid.NewGuid().ToString("N")
            : _activeCycleId;
        var history = new TestHistoryRecord
        {
            Started = startedAt,
            Finished = finishedAt,
            TestStartedAt = startedAt,
            ResultAt = finishedAt,
            InspectionType = HistoryInspectionType.LeakRetest,
            PartName = model.ProductName,
            PartNumber = model.PartNumber,
            VehicleType = model.VehicleType,
            Eco = model.Eco,
            Nco = model.Nco,
            Alc = model.Alc,
            LotNo = 0,
            ProductionCounter = 0,
            Result = completed.ResultText,
            Passed = completed.Passed,
            ModelName = model.ModelName,
            ModelFile = model.SourcePath,
            HtdrvName = ProgramIdentityService.BuildHtdrvName(),
            LotText = _productionSettings.Lot,
            InspectionTrace =
                $"{startedAt:HH:mm:ss}~{finishedAt:HH:mm:ss} LEAK RETEST #{attempt} {summary}",
            DeviceName = _productionSettings.DeviceName,
            DeviceNumber = _productionSettings.DeviceNumber,
            OperatorCompany = _productionSettings.OperatorCompany,
            ProductionLine = _productionSettings.ProductionLine,
            FaultType = run.Passed
                ? string.Empty
                : FaultTypeCatalog.DisplayName(ProductFaultType.WaterProofLeak),
            FaultCode = run.Passed
                ? string.Empty
                : FaultTypeCatalog.Code(ProductFaultType.WaterProofLeak),
            FaultDetailsJson = completed.FaultDetailsJson,
            FaultSummary = summary,
            CycleId = $"{parentCycleId}-leak-retest-{attempt:D4}",
            PrintStatus = LabelPrintStatus.NotRequested.ToString()
        };

        try
        {
            ProductionResultCommitRequest request = ProductionResultCommitRequest.Capture(
                history,
                model,
                _productionSettings,
                faults,
                [],
                run.Channels,
                ProgramIdentityService.VersionText);
            ProductionCommitResult result = await ProductionPersistence.CommitTestResultAsync(
                request,
                _lifetimeCts.Token);
            AddLog(
                $"[WATERPROOF-RETEST] History id={result.TestId} cycle={history.CycleId}" +
                (result.AlreadyCommitted ? " (đã tồn tại)." : " đã lưu."));
            return true;
        }
        catch (Exception ex)
        {
            AddLog($"[WATERPROOF-RETEST] LỖI LƯU LỊCH SỬ: {ex.Message}");
            await InvokeUiAsync(() => MessageBox.Show(
                ResolveOperatorDialogOwner(),
                $"Đã có kết quả Leak retest {completed.ResultText} nhưng chưa lưu được lịch sử.",
                "CHƯA LƯU ĐƯỢC KẾT QUẢ",
                MessageBoxButton.OK,
                MessageBoxImage.Error));
            return false;
        }
    }

    private static bool ShouldRestartAfterPass(
        bool autoRestartConfigured,
        bool waterProofCompleted) =>
        autoRestartConfigured || waterProofCompleted;

    private FaultDetail CreateWaterProofFaultDetail(WaterProofChannelResult channel) => new()
    {
        Type = ProductFaultType.WaterProofLeak,
        WireName = channel.ChannelText,
        Message = $"{channel.ChannelText}: áp giữ {channel.SecondPressureText}; độ rò {channel.LeakText}; " +
                  $"yêu cầu áp >= {_waterProofProfile.PressMin:0.###}, độ sụt <= {_waterProofProfile.LeakLimit:0.###}"
    };

    private string BuildWaterProofHistorySummary(WaterProofRunResult run)
    {
        string F(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        return string.Join('/', run.Channels
            .Where(channel => channel.Enabled)
            .OrderBy(channel => channel.Channel)
            .Select(channel =>
            {
                string connector = _waterProofProfile.ConnectorForChannel(channel.Channel).Trim();
                string name = connector.Length == 0
                    ? $"CH{channel.Channel}"
                    : $"CH{channel.Channel}/{connector}";
                return $"[{name}: {F(channel.FirstPressure)}→{F(channel.SecondPressure)} " +
                       $"Δ{F(channel.Leak)}≤{F(_waterProofProfile.LeakLimit)}:{(channel.Passed ? "PASS" : "FAIL")}]";
            }));
    }

    private bool HasFailedWaterProofResult() =>
        _lastWaterProofMeasurements.Any(channel => channel.Enabled && !channel.Passed);

    private string WaterProofRetryInstruction()
    {
        string connectors = string.Join(", ", ConfiguredWaterProofConnectorIds());
        return connectors.Length == 0
            ? "LEAK FAIL - THÁO/LẮP LẠI CONNECTOR LEAK"
            : $"LEAK FAIL - THÁO RỒI LẮP LẠI CONNECTOR {connectors}";
    }

    private async Task RunPreContinuityWaterProofAsync(
        ProductModel cycleModel,
        long generation)
    {
        CancellationToken ct = CurrentCycleToken();
        try
        {
            if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
                !ReferenceEquals(_model, cycleModel) ||
                CurrentProductionPhase != ProductionPhase.WaterProof)
            {
                return;
            }

            await PauseProductionScanForWaterProofAsync(ct);
            bool passed = await RunAutomaticWaterProofAsync(cycleModel, generation, ct);
            if (passed)
            {
                Interlocked.Exchange(ref _preContinuityWaterProofPassed, 1);
                Interlocked.Exchange(ref _preContinuityWaterProofStarted, 0);
                SetProductionPhase(ProductionPhase.Continuity);
                State = "LEAK PASS - KIỂM TRA THÔNG MẠCH";
                AddLog("[WATERPROOF] Leak đầu chu kỳ PASS; tiếp tục kiểm tra toàn bộ continuity.");
                await StartProductionScanAndVerifyFrameAsync(ct, "PRE_CONTINUITY_LEAK_PASS");
                return;
            }

            if (!_cycleActive || CurrentProductionPhase != ProductionPhase.WaterProof)
                return;

            if (_engine.ContinuityPassed &&
                !_engine.HasWiringFault &&
                _engine.ReadyToEvaluateProductFaults)
            {
                await FinalizeWaterProofProductFailureAsync(cycleModel, generation, ct);
                return;
            }

            State = WaterProofRetryInstruction();
            ArmWaterProofRetestConnectorCycle();
            AddLog("[WATERPROOF] Leak FAIL nhưng continuity chưa PASS toàn bộ; chưa ghi FAIL sản phẩm.");
            await StartProductionScanAndVerifyFrameAsync(ct, "PRE_CONTINUITY_LEAK_FAIL_RETRY");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AddLog("[WATERPROOF] Đã hủy Leak đầu chu kỳ vì runtime/model thay đổi.");
        }
        catch (Exception ex)
        {
            EnterDeviceFault(ex, "RunPreContinuityWaterProof");
        }
    }

    private async Task FinalizeWaterProofProductFailureAsync(
        ProductModel cycleModel,
        long generation,
        CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _waterProofFailureFinalizing, 1, 0) != 0)
            return;

        if (!IsRuntimeContext(RuntimeMode.Production, generation) ||
            !ReferenceEquals(_model, cycleModel) ||
            !HasFailedWaterProofResult() ||
            !_engine.ContinuityPassed ||
            _engine.HasWiringFault ||
            !_engine.ReadyToEvaluateProductFaults)
        {
            Interlocked.Exchange(ref _waterProofFailureFinalizing, 0);
            return;
        }

        _cycleContinuityCompletedAt ??= DateTime.Now;
        _cycleActive = false;
        SetProductionPhase(ProductionPhase.WaitingFaultConfirmation);
        FaultDetail[] faults = WaterProofChannels
            .Where(item => item.Enabled && item.IsMeasured && !item.Passed)
            .Select(CreateWaterProofFaultDetail)
            .ToArray();
        bool committed = await RecordCompletedProductAsync(
            false,
            FaultTypeCatalog.DisplayName(ProductFaultType.WaterProofLeak),
            cycleModel,
            generation,
            ct,
            failureDetails: faults);
        if (!committed)
        {
            AddLog("[WATERPROOF] FAIL đã được cycle hiện tại xử lý trước đó; bỏ qua popup/eject lặp.");
            return;
        }

        State = FaultDisplayFormatter.OperatorInstruction(ProductFaultType.WaterProofLeak);
        RaiseTestStatistics();

        AddLog("[WATERPROOF] Continuity PASS toàn bộ nhưng Leak FAIL; đã ghi lịch sử FAIL và yêu cầu xác nhận.");
        await InvokeUiAsync(() =>
            ShowFaultConfirmationDialog(faults, cycleModel, ResolveOperatorDialogOwner()));

        try
        {
            State = "ĐANG MỞ JIG HÀNG LỖI";
            await _engine.EjectFaultProductAsync();
            ArmWaterProofFaultRemovalWait();
            await StartProductionScanAndVerifyFrameAsync(
                CurrentCycleToken(),
                "WATERPROOF_FAIL_CONFIRM_RELAY");
            if (_waitForFaultProductRemoval)
                State = FaultRemovalWaitingText(cycleModel);
            else
                AddLog("Sản phẩm Leak FAIL đã tháo hoàn toàn; chu kỳ mới đã ARM.");
        }
        catch (Exception ex)
        {
            EnterDeviceFault(ex, "WaterProofFail.EjectOrScan");
        }
    }

    private async Task<bool> RunAutomaticWaterProofAsync(
        ProductModel cycleModel,
        long generation,
        CancellationToken ct)
    {
        if (!IsWaterProofEnabledForCurrentModel())
        {
            AddLog("[WATERPROOF] Model không bật kiểm tra kín nước - bỏ qua UART leak.");
            return true;
        }

        if (Interlocked.CompareExchange(ref _waterProofRunning, 1, 0) != 0)
            throw new InvalidOperationException("Một chu trình kiểm tra kín nước khác đang chạy.");

        try
        {
            if (_waterProofProfile.EnabledChannelCount == 0)
                throw new InvalidOperationException("Model bật kiểm tra kín nước nhưng chưa chọn CH1/CH2/CH3.");

            // Một lượt Leak mới tuyệt đối không dùng lại baseline của sản phẩm trước.
            Array.Clear(
                _waterProofLivePressBaseline,
                0,
                _waterProofLivePressBaseline.Length);

            _cycleWaterProofStartedAt ??= DateTime.Now;
            _ = PersistActiveCycleStageAsync("LEAK_STARTED");
            State = "ĐANG TEST LEAK";
            ShowWaterProofOperationPanel();
            SetWaterProofStage(WaterProofStage.Connecting, "ĐANG KẾT NỐI", "---");
            AddLog(
                $"[WATERPROOF] START model={cycleModel.ModelName} port={_productionSettings.WaterProofMachine.PortName} " +
                $"channels={string.Join(",", Enumerable.Range(1, 3).Where(_waterProofProfile.IsChannelEnabled).Select(x =>
                    string.IsNullOrWhiteSpace(_waterProofProfile.ConnectorForChannel(x))
                        ? $"CH{x}"
                        : $"CH{x}={_waterProofProfile.ConnectorForChannel(x)}"))}");

            WaterProofRunResult run;
            try
            {
                run = await _waterProof.RunTestAsync(
                    _productionSettings.WaterProofMachine,
                    _waterProofProfile,
                    ApplyWaterProofProgress,
                    ct);
                _cycleWaterProofCompletedAt = DateTime.Now;
                _ = PersistActiveCycleStageAsync("LEAK_COMPLETED");
                _cycleWaterProofSummary = BuildWaterProofHistorySummary(run);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!_board.IsConnected)
                {
                    EnterDeviceFault(
                        new IOException("Bo D2XX mất kết nối trong công đoạn Leak.", ex),
                        "WaterProof.BoardDisconnected");
                    return false;
                }

                SetWaterProofStage(WaterProofStage.Error, "LỖI THIẾT BỊ LEAK", "ERROR");
                State = "LỖI THIẾT BỊ KÍN NƯỚC";
                AddLog($"[WATERPROOF] DEVICE ERROR: {ex.Message}");
                AddLog("[WATERPROOF] Không ghi PASS/FAIL sản phẩm vì đây là lỗi thiết bị leak UART.");

                try
                {
                    await _waterProof.DisconnectAsync();
                    AddLog("[WATERPROOF] Leak COM đã disconnect riêng; D2XX/scan production giữ nguyên.");
                }
                catch (Exception disconnectEx)
                {
                    AddLog($"[WATERPROOF] Không thể disconnect Leak COM sau lỗi: {disconnectEx.Message}");
                }

                await InvokeUiAsync(() => MessageBox.Show(
                    ResolveOperatorDialogOwner(),
                    "Mất kết nối máy Leak. Hãy rút/cắm lại cáp, chọn lại cổng COM rồi thử lại.",
                    "KIỂM TRA MÁY LEAK",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error));

                ArmWaterProofEquipmentErrorRemovalWait();
                try
                {
                    await StartProductionScanAndVerifyFrameAsync(
                        CurrentCycleToken(),
                        "WATERPROOF_DEVICE_ERROR");
                    if (_waitForProductRelease)
                        State = "LỖI THIẾT BỊ LEAK - CHỜ THÁO SẢN PHẨM";
                }
                catch (Exception scanEx)
                {
                    AddLog($"[WATERPROOF] Không thể restart D2XX sau lỗi Leak: {scanEx.Message}");
                }
                return false;
            }

            // Chờ UI/result collection cập nhật xong trước khi ghi history hoặc dựng popup FAIL.
            // Nếu chỉ BeginInvoke rồi chạy tiếp, FaultConfirmation có thể nhận danh sách rỗng.
            await ApplyWaterProofFinalResultAsync(run);
            if (run.Passed)
            {
                AddLog("[WATERPROOF] PASS - tất cả kênh được bật đều đạt.");
                return true;
            }
            AddLog("[WATERPROOF] FAIL - chờ continuity đầy đủ hoặc thao tác tháo/lắp connector để test lại.");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _waterProofRunning, 0);
            AddLog($"[WATERPROOF] RUN FLAG RELEASED waterProofRunning={Volatile.Read(ref _waterProofRunning)}");
        }
    }

    private async Task RunAutomaticPostContinuityAsync()
    {
        if (IsDeviceFault || !_cycleActive || _model is null)
            return;

        ProductModel cycleModel = _model;
        long generation = Volatile.Read(ref _runtimeGeneration);
        CancellationToken ct = CurrentCycleToken();
        if (ct.IsCancellationRequested)
            return;

        try
        {
            ct.ThrowIfCancellationRequested();
            if (CurrentProductionPhase != ProductionPhase.Continuity ||
                !_cycleActive ||
                !_engine.ContinuityPassed ||
                _engine.HasWiringFault ||
                !_engine.ReadyToEvaluateProductFaults ||
                (IsWaterProofEnabledForCurrentModel() &&
                 Volatile.Read(ref _preContinuityWaterProofPassed) == 0))
            {
                Interlocked.Exchange(ref _postContinuityStarted, 0);
                AddLog(
                    "[AUTO-R] Abort post-continuity: " +
                    $"phase={CurrentProductionPhase} cycleActive={_cycleActive} " +
                    $"continuityPassed={_engine.ContinuityPassed} hasWiringFault={_engine.HasWiringFault} " +
                    $"readyToEvaluate={_engine.ReadyToEvaluateProductFaults}");
                return;
            }

            AsyncFileLogService.Current.Performance(
                $"PASS_LATENCY T_POST_CONTINUITY_TASK_START cycle={_activeCycleId}");
            _cycleContinuityCompletedAt ??= DateTime.Now;
            _ = PersistActiveCycleStageAsync("CONTINUITY_COMPLETED");
            AddLog("Toàn bộ mạng I/O đã đạt theo model THT.");
            AddLog($"[AUTO-R] Continuity complete = {_engine.ContinuityPassed}");
            AddLog($"[AUTO-R] Continuity passed = {_engine.ContinuityPassed}");
            AddLog($"[AUTO-R] Wrong/Short active = {_engine.HasWiringFault}");
            AddLog($"[AUTO-R] Resistance setting enabled = {IsResistanceEnabledForModel(_model)}");
            AddLog($"[AUTO-R] Model resistance step count = {_model.ResistanceSteps.Count}");
            List<ResistanceStep> configuredResistanceSteps =
                ResistanceMeasurementPlan.BuildEnabledSteps(_productionSettings);
            AddLog($"[AUTO-R] Selected channel count = {configuredResistanceSteps.Count}");
            AddLog($"[AUTO-R] Selected channels = {string.Join(",", configuredResistanceSteps.Select(step => $"CH{step.Channel}"))}");
            // Không tạo trạng thái trung gian trên bảng lớn. Người vận hành chỉ
            // thấy CHỜ LẮP -> ĐANG KIỂM TRA -> PASS.

            if (IsResistanceEnabledForModel(_model))
            {
                if (!_cycleActive || !_engine.ContinuityPassed || _engine.HasWiringFault)
                {
                    Interlocked.Exchange(ref _postContinuityStarted, 0);
                    AddLog("[AUTO-R] Hủy đo điện trở vì trạng thái wiring đổi trước Resistance.");
                    return;
                }

                _cycleResistanceStartedAt ??= DateTime.Now;
                _ = PersistActiveCycleStageAsync("RESISTANCE_STARTED");
                SetProductionPhase(ProductionPhase.Resistance);
                State = "KIỂM TRA ĐIỆN TRỞ";
                AddLog("[AUTO-R] Trigger automatic resistance = YES");
                if (_productionSettings.PageDelay > 0)
                {
                    AsyncFileLogService.Current.Performance(
                        $"RESISTANCE_PAGE_DELAY_SKIPPED configured_ms={Math.Clamp(_productionSettings.PageDelay, 0, 5000)}");
                }

                PrepareResistanceRows(_model);
                SelectedOperationTabIndex = 1;
                AddLog("[AUTO-R] Switching TestWindow to resistance view");

                await EnsureKeysightConnectedAsync();
                // TestEngine owns STOP_SCAN and the resistance route sequence.
                // Suspend first so the watchdog treats IsScanning=false as the
                // intentional AUTO-R phase until a later production START.
                _scanSupervisor.Suspend("Resistance");

                List<ResistanceResult> results =
                    await _engine.MeasureResistanceAsync(
                        result => UpdateResistanceRows([result]),
                        ct);

                UpdateResistanceRows(results);
                _cycleResistanceCompletedAt = DateTime.Now;
                _ = PersistActiveCycleStageAsync("RESISTANCE_COMPLETED");

                AddLog(
                    $"Hoàn thành {Resistance.Count}/{configuredResistanceSteps.Count} " +
                    "phép đo điện trở.");

                if (Resistance.Count != configuredResistanceSteps.Count ||
                    Resistance.Any(x => !x.Passed))
                {
                    _cycleActive = false;
                    SetProductionPhase(ProductionPhase.WaitingFaultConfirmation);
                    bool committed = await RecordCompletedProductAsync(
                        false,
                        FaultTypeCatalog.DisplayName(ProductFaultType.ResistanceOutOfRange),
                        cycleModel,
                        generation,
                        ct);
                    if (!committed)
                    {
                        Interlocked.Exchange(ref _postContinuityStarted, 0);
                        AddLog("Resistance FAIL đã được chu kỳ hiện tại xử lý trước đó; bỏ qua popup/eject lặp.");
                        await RecoverAfterUncommittedFailAsync(
                            cycleModel,
                            generation,
                            ct,
                            "RESISTANCE_FAIL");
                        return;
                    }

                    State = FaultDisplayFormatter.OperatorInstruction(ProductFaultType.ResistanceOutOfRange);
                    RaiseTestStatistics();
                    AddLog("Điện trở không đạt. Không chạy relay PASS.");

                    FaultDetail[] resistanceFaults = Resistance
                        .Where(item => !item.Passed)
                        .Select(CreateResistanceFaultDetail)
                        .ToArray();
                    ShowFaultConfirmationDialog(resistanceFaults, cycleModel);
                    SelectedOperationTabIndex = 0;

                    try
                    {
                        State = "ĐANG MỞ JIG HÀNG LỖI";
                        await _engine.EjectFaultProductAsync();
                        AddLog($"Resistance FAIL đã xác nhận: {FaultJigRelayText()} pulse rồi OFF; không chạy MARKING PASS.");

                        ArmFaultProductRemoval(cycleModel);
                        await StartProductionScanAndVerifyFrameAsync(
                            CurrentCycleToken(),
                            "RESISTANCE_FAIL_CONFIRM_RELAY");
                        // Callback frame đầu tiên có thể đã xác nhận ProductRemoved
                        // và đưa chu kỳ về READY. Không được ghi đè READY bằng trạng
                        // thái FAIL sau khi hộp thoại đã được người vận hành xác nhận.
                        State = _waitForFaultProductRemoval
                            ? FaultRemovalWaitingText(cycleModel)
                            : "LẮP SẢN PHẨM";
                    }
                    catch (Exception ex)
                    {
                        EnterDeviceFault(ex, "ResistanceFail.EjectOrScan");
                    }
                    return;
                }

                AddLog("Tất cả phép đo điện trở PASS.");
            }
            else
            {
                AddLog("[AUTO-R] Trigger automatic resistance = NO");
                AddLog("Model không yêu cầu đo điện trở - bỏ qua Keysight.");
            }

            bool waterProofCompleted =
                IsWaterProofEnabledForCurrentModel() &&
                Volatile.Read(ref _preContinuityWaterProofPassed) != 0;

            SetProductionPhase(ProductionPhase.Completed);
            bool passUiTriggered = false;
            long passUiTimestamp = 0;
            DateTime? passResultAt = null;
            void TriggerPassUi()
            {
                if (passUiTriggered)
                    return;

                passUiTriggered = true;
                passUiTimestamp = Stopwatch.GetTimestamp();
                passResultAt = DateTime.Now;
                State = "PASS";
                AsyncFileLogService.Current.Performance(
                    $"PASS_LATENCY T_PASS_UI cycle={_activeCycleId}");
                _sound.SetWiringFaultAlarm(false);
                _sound.PlayTestOk();
                AddLog("PASS - continuity/điện trở/kín nước theo cấu hình đã đạt; chuẩn bị chuỗi relay MARKING/JIG.");
            }

            TriggerPassUi();
            await PauseProductionScanForFinalPassAsync(ct);

            // PASS UI/sound phải bật ngay khi điều kiện logic đã đạt.
            // Relay chạy sau theo cấu hình Production Settings.
            // Tuyệt đối không cho relay PASS chạy trong lúc/Ngay sau khi que
            // dò GND còn tạo tín hiệu. Sau lockout phải xác nhận continuity
            // vẫn PASS và không có wiring fault mới được MARKING/JIG.
            await WaitForProbeRelayInterlockAsync(ct);
            // Continuity đã được chốt trước khi vào bước điện trở cuối cùng.
            // Các thao tác đo có thể dừng scan nên dùng snapshot đã xác nhận.
            bool continuityLatchedForFinalPass = _cycleContinuityCompletedAt.HasValue;
            if (!_cycleActive ||
                (!continuityLatchedForFinalPass &&
                 (!_engine.ContinuityPassed || _engine.HasWiringFault)))
            {
                Interlocked.Exchange(ref _postContinuityStarted, 0);
                State = "ĐANG KIỂM TRA...";
                AddLog("Đã hủy chuỗi relay PASS vì trạng thái I/O thay đổi sau đầu dò.");
                return;
            }

            bool ok = await _engine.CompletePassAsync(
                Resistance,
                onPassStarted: () =>
                {
                    if (!passUiTriggered)
                    {
                        TriggerPassUi();
                        return;
                    }

                    AddLog(PassRelaySequenceText() + " bắt đầu.");
                },
                continuityAlreadyValidated: continuityLatchedForFinalPass,
                ct: ct);

            if (!ok)
            {
                await HandleFinalPassRejectedAsync(cycleModel, generation, ct);
                return;
            }

            bool passCommitted = await RecordCompletedProductAsync(
                true,
                "PASS",
                cycleModel,
                generation,
                ct,
                resultAtOverride: passResultAt);
            if (!passCommitted)
            {
                Interlocked.Exchange(ref _postContinuityStarted, 0);
                await RecoverAfterUncommittedFailAsync(
                    cycleModel,
                    generation,
                    ct,
                    "PASS_COMMIT_REJECTED");
                return;
            }

            AddLog("Chuỗi PASS hoàn tất: " + PassRelaySequenceText() + " -> tất cả relay OFF.");
            RaiseTestStatistics();

            bool rearmAfterRemoval = ShouldRestartAfterPass(
                _settings.Test.AutoRestartAfterPass,
                waterProofCompleted);
            Interlocked.Exchange(ref _rearmAfterProductRemoval, rearmAfterRemoval ? 1 : 0);
            if (!rearmAfterRemoval)
                SelectedOperationTabIndex = 0;

            // CompletePassAsync đã đưa relay về OFF. ARM chờ tháo ngay trước
            // mọi I/O tiếp theo để lỗi restart scan không thể đổi PASS thành đỏ
            // hoặc giữ màn hình ở bảng kết quả Leak.
            ArmPassProductRemovalWait();

            if (passUiTimestamp != 0)
            {
                double passToReadyMs = Stopwatch.GetElapsedTime(passUiTimestamp).TotalMilliseconds;
                AsyncFileLogService.Current.Performance(
                    $"PASS_LATENCY T_PASS_TO_WAIT_REMOVE cycle={_activeCycleId} duration_ms={passToReadyMs:0.###}");
                AddLog($"PASS -> CHỜ THÁO: {passToReadyMs:0} ms; đã ARM ProductRemoved.");
            }

            try
            {
                await StartProductionScanAndVerifyFrameAsync(ct, "PASS_RELAY_SEQUENCE");
                AddLog("Đã restart scan. Chờ nhả sản phẩm/jig trước chu kỳ tiếp theo.");
            }
            catch (Exception ex)
            {
                // PASS đã commit nên không đổi thành FAIL. DeviceFault khóa
                // phiên và yêu cầu khởi động lại; không thử hồi sinh scan.
                AddLog($"PASS đã lưu; restart scan chờ tháo chưa thành công: {ex.Message}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AddLog("Chu trình cũ đã được hủy sạch.");
            return;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            EnterDeviceFault(ex, "RunAutomaticPostContinuity");
            return;
        }
        catch (Exception ex)
        {
            EnterDeviceFault(ex, "RunAutomaticPostContinuity");
        }
    }

    private async Task HandleFinalPassRejectedAsync(
        ProductModel cycleModel,
        long generation,
        CancellationToken ct)
    {
        _cycleActive = false;
        SetProductionPhase(ProductionPhase.WaitingFaultConfirmation);
        State = "CHƯA ĐẠT";
        SelectedOperationTabIndex = 0;

        try
        {
            if (_board.IsConnected)
            {
                await StopScanIntentionallyAsync("FinalPassRejected");
                await _board.AllRelaysOffAsync();
            }
        }
        catch (Exception ex)
        {
            EnterDeviceFault(ex, "FinalPassRejected.StopBoard");
            return;
        }

        FaultDetail[] faults = BuildFinalPassRejectionFaults(cycleModel);
        bool committed = await RecordCompletedProductAsync(
            false,
            "CHƯA ĐẠT",
            cycleModel,
            generation,
            ct,
            faults);
        if (!committed)
        {
            Interlocked.Exchange(ref _postContinuityStarted, 0);
            AddLog("Final PASS rejection không commit được; không mở popup/eject lặp vì lifecycle kết quả đã đổi chủ.");
            await RecoverAfterUncommittedFailAsync(
                cycleModel,
                generation,
                ct,
                "FINAL_PASS_REJECT");
            return;
        }

        AddLog(
            "[NG-DIALOG] " +
            $"CycleId={_activeCycleId} Reason=FinalPassRejected " +
            $"ContinuityPassed={_engine.ContinuityPassed} " +
            $"Resistance={Resistance.Count}/{ResistanceMeasurementPlan.BuildEnabledSteps(_productionSettings).Count}");

        ShowFaultConfirmationDialog(faults, cycleModel);

        try
        {
            State = "ĐANG MỞ JIG HÀNG LỖI";
            await _engine.EjectFaultProductAsync();
            AddLog($"Final PASS rejection đã xác nhận: {FaultJigRelayText()} pulse rồi OFF; không chạy MARKING PASS.");

            ArmFaultProductRemoval(cycleModel);
            await StartProductionScanAndVerifyFrameAsync(
                CurrentCycleToken(),
                "FINAL_PASS_REJECT_CONFIRM_RELAY");
            State = _waitForFaultProductRemoval
                ? FaultRemovalWaitingText(cycleModel)
                : "LẮP SẢN PHẨM";
        }
        catch (Exception ex)
        {
            EnterDeviceFault(ex, "FinalPassRejected.EjectOrScan");
        }
    }

    private async Task RecoverAfterUncommittedFailAsync(
        ProductModel cycleModel,
        long generation,
        CancellationToken cycleToken,
        string reason)
    {
        _sound.SetWiringFaultAlarm(false);
        _waitForFaultProductRemoval = false;

        bool currentProductionContext =
            !cycleToken.IsCancellationRequested &&
            ReferenceEquals(_model, cycleModel) &&
            IsRuntimeContext(RuntimeMode.Production, generation) &&
            Volatile.Read(ref _probeSessionActive) == 0 &&
            MasterApproved;

        AddLog(
            $"[FAIL-RECOVERY] Reason={reason} CurrentContext={currentProductionContext} " +
            $"ResultCommitted={Volatile.Read(ref _resultRecordedThisCycle) != 0} " +
            $"InlineProbeIo={Volatile.Read(ref _inlineProbeContactIo)} " +
            $"TokenCanceled={cycleToken.IsCancellationRequested}");

        if (!currentProductionContext)
            return;

        // Không được cộng FAIL lần hai hoặc mở popup lặp. Tuy nhiên chu kỳ cũng
        // không được nằm vĩnh viễn ở KHÔNG ĐẠT: scan lại và bắt buộc xác nhận
        // tháo toàn bộ sản phẩm trước khi ResetFullCycleAfterProductRemoved().
        _waitForProductRelease = true;
        SetProductRemovalPending(true);
        Interlocked.Exchange(ref _removalMonitoringFromMain, 0);
        _cycleActive = true;
        SetProductionPhase(ProductionPhase.WaitingProductRemoval);
        State = "CHỜ THÁO SẢN PHẨM";

        try
        {
            await StartProductionScanAndVerifyFrameAsync(
                CurrentCycleToken(),
                reason + "_UNCOMMITTED_RECOVERY");
        }
        catch (Exception ex)
        {
            AddLog($"Không thể phục hồi scan sau {reason}: {ex.Message}");
        }
    }

    private FaultDetail[] BuildFinalPassRejectionFaults(ProductModel model)
    {
        FaultDetail[] captured = CaptureFaultDetails().ToArray();
        if (captured.Length > 0)
            return captured;

        int expectedResistanceCount = ResistanceMeasurementPlan.BuildEnabledSteps(_productionSettings).Count;
        string failedResistance = string.Join(
            ", ",
            Resistance.Where(item => !item.Passed).Select(item => $"{item.Name}={item.ResultText}"));
        string resistanceDetail = string.IsNullOrWhiteSpace(failedResistance)
            ? $"Resistance {Resistance.Count}/{expectedResistanceCount}"
            : $"Resistance {Resistance.Count}/{expectedResistanceCount}: {failedResistance}";

        return
        [
            new FaultDetail
            {
                Type = ProductFaultType.None,
                Message =
                    "Điều kiện PASS cuối cùng đã thay đổi trước khi chạy relay. " +
                    $"Continuity={_engine.ContinuityPassed}; " +
                    $"Network={PassedNetworkCount}/{ExpectedNetworkCount}; {resistanceDetail}."
            }
        ];
    }

    public void RequireApplicationRestartAfterBoardSettingsChange()
    {
        EnterDeviceFault(
            new InvalidOperationException(
                "Loại bo đã thay đổi trong Cài đặt. Cần thoát và mở lại ứng dụng để áp dụng bằng lifecycle mới."),
            "BoardSettingsChanged");
    }

    public void RefreshProductionConfiguration()
    {
        int maxIo = _model?.MaxIo ?? 0;
        if (_model is not null)
        {
            int configuredMasterFaults = ProductionConfigService.GetMasterFaultRequiredCount(_productionSettings, _model);
            if (configuredMasterFaults != _masterRequiredFaultCount)
                ResetMasterGateForModel();
        }
        _board.ConfigureActiveScanRange(maxIo);
        InvokeUi(RebuildActiveCards);
        LoadWaterProofProfileForCurrentModel();
        RefreshProductionUiSettings();
        AddLog(
            $"Đã nạp lại cấu hình production: model max IO {maxIo}, LOTNO {Lot}; " +
            $"{_board.Capacity}.");
    }

    public void RefreshProductionSettingsOnly()
    {
        if (_model is not null)
        {
            int configuredMasterFaults = ProductionConfigService.GetMasterFaultRequiredCount(_productionSettings, _model);
            if (configuredMasterFaults != _masterRequiredFaultCount && !IsManualModeActive)
            {
                ResetMasterGateForModel();
                AddLog($"Cấu hình Số lỗi Master thay đổi -> reset Master Gate về 0/{configuredMasterFaults}.");
            }
        }

        LoadWaterProofProfileForCurrentModel();
        RefreshProductionUiSettings();
        State = ReadyStateForCurrentModel();
        AddLog("Đã áp dụng cấu hình mềm; không restart scan và không reconnect FTDI.");
    }

    /// <summary>
    /// V12.9: áp dụng thay đổi card xuống tận runtime. Scan cũ bị dừng,
    /// generation transport bị invalidate, RX được purge bởi command D2XX,
    /// decoder/card UI được dựng lại rồi scan nền được khởi động lại.
    /// Không đóng/mở FTDI.
    /// </summary>
    public async Task RefreshProductionConfigurationAsync(bool forceNativeRestart = false)
    {
        if (IsDeviceFault)
            return;

        Interlocked.Exchange(ref _hardwareReconfigurationActive, 1);
        try
        {
            int maxIo = _model?.MaxIo ?? 0;
            bool wasScanning = _board.IsScanning;
            RuntimeMode runtimeMode = CurrentRuntimeMode;
            BoardScanMode resumeMode = runtimeMode == RuntimeMode.Probe
                ? BoardScanMode.Probe
                : BoardScanMode.Production;

            _board.ConfigureActiveScanRange(maxIo);
            BoardCapacity requestedActiveCapacity = _board.Capacity;
            BoardCapacity? appliedActiveCapacity = _board.AppliedScanCapacity;
            bool activeCapacityChanged = appliedActiveCapacity is null ||
                appliedActiveCapacity.StartScanParameter != requestedActiveCapacity.StartScanParameter ||
                appliedActiveCapacity.TotalIoCapacity != requestedActiveCapacity.TotalIoCapacity;
            bool restartRequired = activeCapacityChanged || forceNativeRestart;

            ClearInlineProbeContactsState(clearLastSeen: true);
            InvokeUi(ClearInlineProbeDisplay);
            _sound.SetWiringFaultAlarm(false);
            _engine.ClearTransientWiringFaults();

            if (_board.IsConnected && wasScanning && restartRequired)
            {
                await StopScanIntentionallyAsync("ProductionReconfigure");
                await _board.AllRelaysOffAsync();
            }

            InvokeUi(RebuildActiveCards);
            LoadWaterProofProfileForCurrentModel();
            RefreshProductionUiSettings();

            if (_model is not null)
            {
                int configuredMasterFaults = ProductionConfigService.GetMasterFaultRequiredCount(
                    _productionSettings,
                    _model);
                if (configuredMasterFaults != _masterRequiredFaultCount)
                {
                    ResetMasterGateForModel();
                    AddLog($"Cấu hình Số lỗi Master thay đổi -> reset Master Gate về 0/{configuredMasterFaults}.");
                }
            }

            if (_board.IsConnected && wasScanning && restartRequired &&
                _board.ScanCapacity.IsModelWithinInstalledCapacity)
            {
                if (resumeMode == BoardScanMode.Production)
                {
                    await StartProductionScanAndVerifyFrameAsync(
                        _lifetimeCts.Token,
                        "PRODUCTION_RECONFIGURE");
                }
                else
                {
                    await _board.StartScanAsync(resumeMode, _lifetimeCts.Token);
                }
            }

            // Chỉ Production đang ARM mới được nối lại engine. Background vẫn chỉ scan nền.
            _engine.SetFrameProcessingEnabled(
                runtimeMode == RuntimeMode.Production &&
                Volatile.Read(ref _probeSessionActive) == 0 &&
                (MasterApproved || IsMasterSequenceActive));

            AddLog(
                $"Đã reconfigure card runtime không đóng/mở FTDI: {_board.Capacity}; " +
                $"resume={resumeMode}, wasScanning={wasScanning}, restart={restartRequired}.");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            EnterDeviceFault(ex, "ProductionReconfigure");
        }
        finally
        {
            Interlocked.Exchange(ref _hardwareReconfigurationActive, 0);
        }
    }

    private void RefreshProductionUiSettings()
    {
        _lotSequence.RefreshActiveProduct();
        lock (_historyStoreGate)
            _historyStore = null;
        UpdateDailyLotDisplay();

        // ALWAYS_PROBE_2026-09-05: nếu file cấu hình cũ còn UseTestPointer=false,
        // bỏ qua giá trị đó và giữ chức năng que dò luôn bật.
        _productionSettings.UseTestPointer = true;

        Raise(nameof(ItemHeight));
        Raise(nameof(ScrollDelay));
        Raise(nameof(PageDelay));
        Raise(nameof(ShowTitle));
        Raise(nameof(ShowConnector));
        Raise(nameof(BoardCapacity));
        Raise(nameof(BoardCapacityText));
    }

    public void SetModel(ProductModel model) => SetModel(model, preparedEngineModel: null);

    private void SetModel(ProductModel model, TestEngine.PreparedModelState? preparedEngineModel)
    {
        if (IsProductRemovalPending)
            throw new InvalidOperationException("VUI LÒNG THÁO SẢN PHẨM");

        // Đổi mã hàng phải hủy sạch chu trình cũ trước khi thay _model; nếu
        // không một task PASS/FAIL cũ hoàn thành muộn có thể cộng sản lượng
        // nhầm sang mã hàng vừa chọn.
        CancelCycleOperations();
        ResetManualProbeSession("model-change");
        _cycleActive = false;
        SetProductionPhase(ProductionPhase.WaitingProduct);
        _waitForProductRelease = false;
        _waitForFaultProductRemoval = false;
        _waterProofEquipmentErrorAwaitingRemoval = false;
        Interlocked.Exchange(ref _waterProofRunning, 0);
        Interlocked.Exchange(ref _postContinuityStarted, 0);
        Interlocked.Exchange(ref _wiringFaultHandlingStarted, 0);
        Interlocked.Exchange(ref _resultRecordedThisCycle, 0);
        Interlocked.Exchange(ref _probeCycleRecordedThisCycle, 0);
        Interlocked.Exchange(ref _startupIoInterlockState, 0);
        _startupIoWarningSignature = string.Empty;
        _lastIoMappingSignature = string.Empty;
        _lastLiveTopologySnapshot = LiveTopologySnapshot.Empty();
        lock (_probePreviewGate)
            _pendingProductionProbePreview = null;
        Interlocked.Exchange(ref _probePresentationState, (int)ProbePresentationState.Inactive);
        Interlocked.Exchange(ref _productionPresentationMode, (int)ProductionPresentationMode.Waiting);
        Interlocked.Increment(ref _ioMappingUiRevision);
        _sound.SetTestPointContactSound(false);
        _discardInterlock.Reset();
        Interlocked.Exchange(ref _discardContactClosed, 0);
        Interlocked.Exchange(ref _discardStandaloneLocked, 0);
        Interlocked.Exchange(ref _faultProductRemoved, 0);
        Interlocked.Exchange(ref _discardRequiredForFault, 0);

        bool migrateLegacyLot = !_productionSettings.LotSettingsByProduct.Keys.Any(key =>
            !string.Equals(key, "DEFAULT", StringComparison.OrdinalIgnoreCase));

        _model = model ??
            throw new ArgumentNullException(nameof(model));
        if (_model.HasDiscardInterlock)
            _discardInterlock.Arm(contactClosed: false);
        _lotSequence.SelectProduct(
            ProductionConfigService.GetLotProductKey(_model),
            migrateLegacyLot);
        // Không để sản lượng mã trước còn hiện trong lúc SQLite đang tải mã mới.
        // Mỗi mã bắt đầu UI ở 0 rồi nhận đúng daily statistics theo PartNumber.
        Total = 0;
        Pass = 0;
        Fail = 0;
        DailyTestCount = 0;
        MonthlyTestCount = 0;
        LifetimeTestCount = 0;
        ProbeCycleCount = 0;
        _pinsByIoLookup = _model.Pins.ToLookup(pin => pin.IoNumber);

        _sound.SetWiringFaultAlarm(false);
        long setModelStarted = Stopwatch.GetTimestamp();
        if (preparedEngineModel is null)
            _engine.SetModel(model);
        else
            _engine.CommitPreparedModel(preparedEngineModel);
        double setModelMs = Stopwatch.GetElapsedTime(setModelStarted).TotalMilliseconds;
        AsyncFileLogService.Current.Performance(
            $"MODEL_LOAD_PERF phase=TestEngine.SetModel model={model.ModelName} duration_ms={setModelMs:0.###}");
        ResetMasterGateForModel();

        // Chỉ áp dụng SỐ CARD ĐÃ CẤU HÌNH. Không tự nâng theo model.
        // MainWindow sẽ chặn test nếu max IO của THT vượt dung lượng card.
        _board.ConfigureActiveScanRange(model.MaxIo);
        InvokeUi(RebuildActiveCards);

        CurrentModelPath = ResolveOptionalModelPath(model.SourcePath);
        if (!string.IsNullOrWhiteSpace(CurrentModelPath))
            _productionSettings.LastThtPath = CurrentModelPath;

        _productionSettings.ResistanceChannels =
            ProductionConfigService.GetResistanceProfileForPath(
                _productionSettings,
                CurrentModelPath ?? model.SourcePath);
        LoadWaterProofProfileForCurrentModel();

        // Đồng bộ model lên MainWindow ngay cả khi model được tự nạp lúc startup.
        // Đồng thời lưu ngay lựa chọn model; không chờ tới lúc bắt đầu test.
        _main.Model = model;
        _main.Home.Refresh();
        ScheduleSelectedModelPersistence(CurrentModelPath);

        Raise(nameof(ModelName));
        Raise(nameof(PartNumber));
        Raise(nameof(ProductName));
        Raise(nameof(VehicleType));
        Raise(nameof(CustomerCode));
        Raise(nameof(Eco));
        Raise(nameof(Nco));
        Raise(nameof(Alc));
        Raise(nameof(IsIoMappingMode));

        ScheduleStatisticsLoadForModel(model);
        UpdateDailyLotDisplay();

        // _engine.SetModel() -> Reset() đã phát Changed và dựng bảng một lần.
        // Không RefreshFaults() lần hai vì model lớn có hàng trăm pin sẽ làm
        // người vận hành cảm giác load THT chậm gấp đôi.
        AddLog($"Đã nạp model {model.ModelName}: {model.Nets.Count} mạng I/O thường, " +
               $"{model.Clip?.Branches.Count ?? 0} nhánh CLIP, {model.ResistanceSteps.Count} bước đo điện trở.");

        if (model.HasDiscardInterlock)
        {
            AddLog(
                $"Thùng hàng lỗi _DISCARD: IO({model.DiscardContactIo[0]})-" +
                $"IO({model.DiscardContactIo[1]}), xác nhận 1 lần đưa hàng qua (THÔNG rồi NHẢ).");
        }

        if (IsIoMappingMode)
        {
            AddLog(
                "Model không có mạng Production hợp lệ: dùng LiveTopology realtime; " +
                "mọi kết nối chỉ hiển thị, không tham gia Production PASS/FAIL.");
            AsyncFileLogService.Current.Performance("MODEL_MODE mode=LiveTopology expected=0");
        }
        else
            AsyncFileLogService.Current.Performance(
                $"MODEL_MODE mode=ExpectedTopology expected={_engine.ExpectedNetCount}");

        if (model.Clip is not null)
        {
            string clipMap = string.Join(
                ", ",
                model.Clip.Branches.Select(branch =>
                    $"{branch.Name}->IO{branch.TargetIo}"));

            AddLog($"CLIP THT: A0/AO common=IO{model.Clip.CommonIo}; {clipMap}");
        }
    }

    private void LogExpectedNetBuild(ProductModel model)
    {
        PassGateDiagnostics gate = _engine.GetPassGateDiagnostics();
        BoardCapacity capacity = _board.Capacity;
        int duplicateNetNames = model.Nets
            .GroupBy(net => net.Name, StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1);
        int duplicatePhysical = model.Nets
            .GroupBy(net => string.Join(",", net.IoNumbers.OrderBy(io => io)))
            .Count(group => group.Count() > 1);
        int singlePin = model.Nets.Count(net => net.SourceIo > 0 && net.ExpectedActiveIo.Count == 0);
        int invalid = model.Nets.Count(net => net.SourceIo <= 0 || net.IoNumbers.Any(io => io <= 0));
        int outsideCapacity = model.Pins
            .Where(pin => pin.IoNumber > 0)
            .Select(pin => pin.IoNumber)
            .Distinct()
            .Count(io => !capacity.ContainsGlobalIo(io));
        int probeOnly = model.Pins.Count(pin =>
            !string.IsNullOrWhiteSpace(pin.WireName) &&
            model.Nets.All(net => !net.Pins.Contains(pin)) &&
            model.Clip?.IsSpecialPin(pin) != true);
        int normal = gate.ExpectedNetworks.Count(item => item.Category.StartsWith("normal", StringComparison.OrdinalIgnoreCase));
        int clip = gate.ExpectedNetworks.Count(item => item.Category.Equals("CLIP", StringComparison.OrdinalIgnoreCase));
        string expectedList = string.Join(" | ", gate.ExpectedNetworks.Select(item => item.Display));

        AsyncFileLogService.Current.Performance(
            $"EXPECTED_NET_BUILD model=\"{model.ModelName}\" thtPinRows={model.Pins.Count} rawNets={model.Nets.Count} " +
            $"eligibleProductionNets={gate.ExpectedNetCount} ExpectedNetCount={gate.ExpectedNetCount} maxIo={model.MaxIo} " +
            $"configuredCapacity={capacity.TotalIoCapacity} capacityRange=IO{capacity.FirstGlobalIo}-{capacity.LastGlobalIo} " +
            $"normal={normal} duplicateName={duplicateNetNames} duplicatePhysical={duplicatePhysical} CLIP={clip} " +
            $"probeOnly={probeOnly} outsideCapacity={outsideCapacity} invalid={invalid} singlePin={singlePin} " +
            $"resistanceOnly={model.ResistanceSteps.Count} expected=\"{expectedList}\"");
    }

    private void LogModelTopology(ProductModel model)
    {
        AsyncFileLogService.Current.Performance(
            $"MODEL_TOPOLOGY model=\"{model.ModelName}\" terminal_rows={model.Pins.Count} " +
            $"connectors={model.Connectors.Count} required_networks={model.Nets.Count}");

        foreach (string warning in model.TopologyWarnings)
            AsyncFileLogService.Current.Performance(warning);

        foreach (ConnectorDefinition connector in model.Connectors)
        {
            AsyncFileLogService.Current.Performance(
                $"CONNECTOR id={connector.ConnectorId} declaredPins={connector.DeclaredPinCount?.ToString() ?? "-"} mappedPins={connector.Pins.Count}");
        }

        foreach (WireNet net in model.Nets)
        {
            string ios = string.Join(",", net.IoNumbers.Select(io => $"IO{io}"));
            string endpoints = string.Join(
                ", ",
                net.Pins.Select(pin => $"C{pin.Connector}:P{pin.PinNumber}->IO{pin.IoNumber}"));

            AsyncFileLogService.Current.Performance(
                $"NETWORK name=\"{net.Name}\" ios=[{ios}] endpoints=[{endpoints}]");
        }
    }

    private void ScheduleStatisticsLoadForModel(ProductModel model)
    {
        long generation = Interlocked.Increment(ref _statisticsLoadGeneration);
        StartupPerformanceTrace.Mark("T11 STATS_BACKGROUND");

        // Giữ reference task để exception luôn được observe trong method bên dưới.
        // Gate serializes fast model changes so stale SQLite queries cannot
        // overwrite statistics for the newly selected model.
        _statisticsLoadTask = LoadStatisticsForModelAsync(model, generation);
    }

    private async Task LoadStatisticsForModelAsync(ProductModel model, long generation)
    {
        try
        {
            await _statisticsLoadGate.WaitAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (generation != Volatile.Read(ref _statisticsLoadGeneration) ||
                _lifetimeCts.IsCancellationRequested)
            {
                return;
            }

            PartIdentitySnapshot part = PartIdentitySnapshot.Capture(model);
            Task<ProductionStatisticsSnapshot> statisticsTask =
                ProductionPersistence.GetStatisticsAsync(part, DateTime.Now, _lifetimeCts.Token);
            Task<ProbeCounterSnapshot> probeTask = ProductionPersistence.GetProbeCounterAsync(
                part,
                _productionSettings.ProbeReplacementThreshold,
                _lifetimeCts.Token);
            await Task.Run(() =>
            {
                // Diagnostic topology có thể có hàng trăm network; dựng chuỗi/log ở
                // worker thay vì giữ Dispatcher sau khi parse THT.
                LogExpectedNetBuild(model);
                LogModelTopology(model);
            }, _lifetimeCts.Token);
            var snapshot = (
                Stats: await statisticsTask,
                PartCounter: await probeTask);

            if (generation != Volatile.Read(ref _statisticsLoadGeneration) ||
                _lifetimeCts.IsCancellationRequested)
            {
                return;
            }

            await InvokeUiAsync(() =>
            {
                if (!IsActiveStatisticsContext(model, part, generation))
                {
                    return;
                }

                ApplyProductionStatistics(snapshot.Stats);
                ApplyPartCounter(snapshot.PartCounter);
                RaiseTestStatistics();

                AddLog(
                    $"Đã nạp sản lượng từ SQLite: Tổng {Total}, PASS {Pass}, FAIL {Fail}, " +
                    $"Tỷ lệ {Rate:0.00}%.");
            });
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await InvokeUiAsync(() =>
            {
                if (!IsActiveStatisticsContext(
                        model,
                        PartIdentitySnapshot.Capture(model),
                        generation))
                {
                    return;
                }

                Total = 0;
                Pass = 0;
                Fail = 0;
                DailyTestCount = 0;
                MonthlyTestCount = 0;
                LifetimeTestCount = 0;
                ProbeCycleCount = 0;
                UpdateDailyLotDisplay();
                RaiseTestStatistics();
                AddLog($"Không thể nạp lịch sử sản lượng: {ex.Message}");
            });
        }
        finally
        {
            _statisticsLoadGate.Release();
        }
    }

    private bool IsActiveStatisticsContext(
        ProductModel requestedModel,
        PartIdentitySnapshot requestedPart,
        long generation)
    {
        ProductModel? activeModel = _model;
        return ReferenceEquals(activeModel, requestedModel) &&
               generation == Volatile.Read(ref _statisticsLoadGeneration) &&
               activeModel is not null &&
               string.Equals(
                   PartIdentitySnapshot.Capture(activeModel).PartKey,
                   requestedPart.PartKey,
                   StringComparison.Ordinal);
    }

    private string BuildProductInspectionTrace(DateTime resultAt)
    {
        var stages = new List<string>();
        DateTime continuityAt = _cycleContinuityCompletedAt ?? resultAt;
        stages.Add($"{continuityAt:HH:mm:ss} 회로검사:{(_cycleContinuityCompletedAt.HasValue ? "PASS" : "FAIL")}");

        if (_cycleResistanceStartedAt is DateTime resistanceStarted)
        {
            DateTime resistanceCompleted = _cycleResistanceCompletedAt ?? resultAt;
            string resistanceSummary = string.Join('/', Resistance.Select(item =>
            {
                string name = !string.IsNullOrWhiteSpace(item.Name)
                    ? item.Name.Trim()
                    : item.ChannelText;
                string value = item.IsOpen ? "OPEN" : item.Display;
                return $"[{name}: {item.MinDisplayText} < {value} < {item.MaxDisplayText} :{item.ResultText}]";
            }));
            stages.Add(
                $"{resistanceStarted:HH:mm:ss}~{resistanceCompleted:HH:mm:ss} 저항검사" +
                (resistanceSummary.Length == 0 ? ":FAIL" : $" {resistanceSummary}"));
        }

        if (_cycleWaterProofStartedAt is DateTime waterProofStarted)
        {
            DateTime waterProofCompleted = _cycleWaterProofCompletedAt ?? resultAt;
            stages.Add(
                $"{waterProofStarted:HH:mm:ss}~{waterProofCompleted:HH:mm:ss} 기밀검사" +
                (string.IsNullOrWhiteSpace(_cycleWaterProofSummary)
                    ? ":FAIL"
                    : $" {_cycleWaterProofSummary}"));
        }

        return string.Join(' ', stages);
    }

    private async Task<bool> RecordCompletedProductAsync(
        bool passed,
        string resultText,
        ProductModel cycleModel,
        long runtimeGeneration,
        CancellationToken cycleToken,
        IReadOnlyList<FaultDetail>? failureDetails = null,
        DateTime? resultAtOverride = null)
    {
        if (cycleToken.IsCancellationRequested ||
            !ReferenceEquals(_model, cycleModel) ||
            !IsRuntimeContext(RuntimeMode.Production, runtimeGeneration) ||
            Volatile.Read(ref _probeSessionActive) != 0 ||
            Volatile.Read(ref _inlineProbeContactIo) != 0 ||
            !MasterApproved ||
            Interlocked.CompareExchange(ref _resultRecordedThisCycle, 1, 0) != 0)
            return false;

        ProductModel model = cycleModel;

        DateTime finished = resultAtOverride ?? DateTime.Now;
        DateTime started = _cycleStartedAt <= finished ? _cycleStartedAt : finished;
        string cycleId = string.IsNullOrWhiteSpace(_activeCycleId)
            ? Guid.NewGuid().ToString("N")
            : _activeCycleId;
        bool autoPrintRequested = ShouldAutoPrintLabel(
            passed,
            _productionSettings.AutoPrintLabelOnPass);
        bool shouldAutoPrint = autoPrintRequested &&
                               HasConfiguredLabelTransport(_productionSettings.Label);
        if (autoPrintRequested && !shouldAutoPrint)
        {
            AddLog(
                "LABEL SKIPPED: AutoPrintLabelOnPass đang bật nhưng chưa cấu hình " +
                "PrinterName/PrinterCom/RawDestination/ExternalHelperPath; kết quả PASS vẫn được lưu bình thường.");
        }
        long completedLot = shouldAutoPrint
            ? _lotSequence.ReserveForCycle(cycleId)
            : _lotSequence.NextLot;

        IReadOnlyList<FaultDetail> faultDetails = passed
            ? Array.Empty<FaultDetail>()
            : failureDetails?.ToArray() ?? CaptureFaultDetails();
        FaultDetail? primaryFault = faultDetails
            .OrderBy(fault => FaultTypeCatalog.Priority(fault.Type))
            .FirstOrDefault();

        string resultStatus = passed ? "PASS" : "FAIL";
        string primaryFaultName = primaryFault?.Name ?? string.Empty;
        string failureName = passed
            ? string.Empty
            : !string.IsNullOrWhiteSpace(primaryFaultName)
                ? primaryFaultName
                : string.IsNullOrWhiteSpace(resultText) ? "FAIL" : resultText.Trim();

        var completed = new CompletedTestResult
        {
            Started = started,
            Finished = finished,
            Passed = passed,
            ResultText = resultStatus,
            Faults = faultDetails,
            Resistance = Resistance.ToArray()
        };

        int openCount = faultDetails.Count(x => x.Type == ProductFaultType.OpenCircuit);
        int wrongOnly = faultDetails.Count(x => x.Type == ProductFaultType.WrongWiring);
        int shortOnly = faultDetails.Count(x => x.Type == ProductFaultType.ShortCircuit);
        string resistanceText = string.Join(
            "; ",
            Resistance.Select(x => $"{x.Name}={x.Display}({x.ResultText})"));

        ResistanceResult? failedResistance = Resistance.FirstOrDefault(item => !item.Passed);
        CustomerFaultDisplay? customerFault = primaryFault is null
            ? null
            : FaultDisplayFormatter.FormatCustomer(primaryFault);

        var history = new TestHistoryRecord
        {
            Started = completed.Started,
            Finished = completed.Finished,
            InstallStartedAt = completed.Started,
            TestStartedAt = NormalizeCycleTimestamp(
                _cycleTestStartedAt,
                completed.Started,
                completed.Finished),
            ResultAt = completed.Finished,
            InspectionType = HistoryInspectionType.Product,
            PartName = model.ProductName,
            PartNumber = model.PartNumber,
            VehicleType = model.VehicleType,
            Eco = model.Eco,
            Nco = model.Nco,
            Alc = model.Alc,
            LotNo = completedLot,
            ProductionCounter = ProbeCycleCount,
            Result = completed.ResultText,
            Passed = completed.Passed,
            ModelName = model.ModelName,
            ModelFile = model.SourcePath,
            HtdrvName = ProgramIdentityService.BuildHtdrvName(),
            LotText = _productionSettings.Lot,
            InspectionTrace = BuildProductInspectionTrace(finished),
            OpenCount = openCount,
            WrongCount = wrongOnly,
            ShortCount = shortOnly,
            Resistance = resistanceText,
            DeviceName = _productionSettings.DeviceName,
            DeviceNumber = _productionSettings.DeviceNumber,
            OperatorCompany = _productionSettings.OperatorCompany,
            ProductionLine = _productionSettings.ProductionLine,
            FaultType = customerFault?.FaultType ?? string.Empty,
            FaultCode = primaryFault?.Code ?? string.Empty,
            ExpectedSourceIo = primaryFault?.ExpectedSourceIo,
            ExpectedTargetIo = primaryFault?.ExpectedTargetIo,
            ActualSourceIo = primaryFault?.ActualSourceIo,
            ActualTargetIo = primaryFault?.ActualTargetIo,
            FaultDetailsJson = completed.FaultDetailsJson,
            FaultSummary = customerFault is null
                ? string.Empty
                : FaultDisplayFormatter.CustomerSummary(customerFault),
            MeasuredResistance = failedResistance?.ValueOhm,
            ResistanceMin = failedResistance?.MinOhm,
            ResistanceMax = failedResistance?.MaxOhm,
            CycleId = cycleId,
            PrintStatus = LabelPrintStatus.NotRequested.ToString()
        };

        LabelPrintRequest? printRequest = null;
        string labelPreparationError = string.Empty;
        if (shouldAutoPrint)
        {
            if (TryCapturePassLabel(
                    history,
                    model,
                    _productionSettings.Label,
                    out printRequest,
                    out LabelIdentity? identity,
                    out labelPreparationError))
            {
                history.LabelSerial = identity!.SerialText;
                history.LabelProfile = printRequest!.FormatName;
                history.LabelTemplateType = LabelProfileResolver.NormalizeTemplateType(
                    _productionSettings.Label.TemplateType);
                history.LabelPayload = printRequest.Payload;
                history.Printer = printRequest.Printer;
                history.LabelCopies = printRequest.Copies;
            }
            else
            {
                // Tem là tác vụ phụ sau khi sản phẩm đã PASS. Cấu hình tem
                // thiếu/không hợp lệ không được phép ném ngược ra vòng đời
                // production, đổi PASS thành EquipmentError hoặc làm mất ARM
                // ProductRemoved. Vẫn lưu history với trạng thái in thất bại.
                history.PrintStatus = LabelPrintStatus.Failed.ToString();
                history.PrintMessage = $"Label preparation failed: {labelPreparationError}";
                AddLog($"LABEL PREPARATION FAILED: cycle {cycleId}; {labelPreparationError}. Kết quả sản phẩm vẫn PASS.");
                InvokeUi(() => LabelStatusText = $"LỖI CẤU HÌNH TEM - LOT {completedLot}: {labelPreparationError}");
            }
        }

        TestHistoryStore historyStore = HistoryStore;
        bool historySaved = false;
        ProductionCommitResult? databaseResult = null;
        try
        {
            ProductionResultCommitRequest commitRequest = ProductionResultCommitRequest.Capture(
                history,
                model,
                _productionSettings,
                faultDetails,
                Resistance.ToArray(),
                _lastWaterProofMeasurements,
                ProgramIdentityService.VersionText);
            databaseResult = await ProductionPersistence.CommitTestResultAsync(
                commitRequest,
                cycleToken);
            history.Id = databaseResult.TestId;
            historySaved = true;
            _recordedHistoryCycleId = cycleId;
            _recordedHistoryStore = historyStore;
            await InvokeUiAsync(() =>
            {
                // SQLite commit owns the immutable cycle Part. A model switch can
                // happen while the queued writer is running; that durable result
                // must never be painted onto counters/LOT of the newly active Part.
                ProductModel? activeModel = _model;
                if (!ReferenceEquals(activeModel, cycleModel) ||
                    !IsRuntimeContext(RuntimeMode.Production, runtimeGeneration) ||
                    activeModel is null ||
                    !string.Equals(
                        PartIdentitySnapshot.Capture(activeModel).PartKey,
                        commitRequest.Part.PartKey,
                        StringComparison.Ordinal))
                {
                    return;
                }

                ApplyProductionStatistics(databaseResult.Statistics);
                ApplyPartCounter(databaseResult.ProbeCounter);
            });
            AsyncFileLogService.Current.Performance(
                $"PASS_LATENCY T_HISTORY_COMMIT cycle={cycleId} result={resultStatus}");
            string historyFault = passed ? string.Empty : $" - {failureName}";
            AddLog(
                $"History: cycle {cycleId}, LOT {completedLot} {resultStatus}{historyFault} " +
                $"đã commit atomic vào {historyStore.DatabasePath}" +
                (databaseResult.AlreadyCommitted ? " (CycleId đã tồn tại, không cộng trùng)." : "."));
        }
        catch (Exception ex)
        {
            AddLog($"LỖI LƯU DỮ LIỆU: kết quả cycle {cycleId} chưa được xác nhận trong SQLite: {ex.Message}");
            await InvokeUiAsync(() => State = "LỖI LƯU DỮ LIỆU - KIỂM TRA Ổ ĐĨA");
            return false;
        }

        // Các định dạng cũ chỉ được sinh sau khi SQLite đã commit. Hỏng mirror
        // không được rollback hoặc cộng lại một kết quả production đã durable.
        try
        {
            string legacyPath = await Task.Run(
                () => _legacyHistory.AppendProduct(model, completed, completedLot),
                cycleToken);
            AddLog(
                $"PHT HISTORY: {resultStatus} LOT {completedLot:N0} " +
                $"đã append vào {legacyPath}.");
        }
        catch (Exception ex)
        {
            AddLog($"LEGACY_{(passed ? "PASS" : "ERROR")}_EXPORT_FAILED: {ex.Message}");
        }

        try
        {
            ProductionCommitResult committed = databaseResult!;
            await Task.Run(
                () => PartCounterStore.Mirror(model, committed.ProbeCounter),
                cycleToken);
        }
        catch (Exception ex)
        {
            AddLog($"LEGACY_MIRROR_FAILED PartCnt.txt: {ex.Message}");
        }

        if (shouldAutoPrint &&
            printRequest is null &&
            !string.IsNullOrWhiteSpace(labelPreparationError))
        {
            AddLog(
                $"LABEL BLOCKED: LOT {completedLot} không in do cấu hình tem không hợp lệ; " +
                "PASS/ProductRemoved vẫn tiếp tục bình thường.");
        }
        else if (shouldAutoPrint && printRequest is not null)
        {
            if (!historySaved)
            {
                const string message = "Không thể lưu giao dịch in vào lịch sử; first-print đã bị chặn để tránh mất traceability.";
                AddLog($"LABEL BLOCKED: {message}");
                ShowLabelWarning($"Sản phẩm đã PASS nhưng không in tem.\n\n{message}");
            }
            else
            {
                _ = PrintPassLabelSafeAsync(printRequest, historyStore, history.Id);
            }
        }

        await InvokeUiAsync(RaiseTestStatistics);
        AddLog(
            $"Đã lưu kết quả mã hàng: LOT {completedLot}, {resultStatus}" +
            (passed ? ", " : $" - {failureName}, ") +
            $"Tổng {Total}, PASS {Pass}, FAIL {Fail}, tỷ lệ {Rate:0.00}%. " +
            $"LOTNO kế tiếp: {_productionSettings.LotNo}.");

        if (!passed)
        {
            AddLog(
                "[FAIL-COMMIT] " +
                $"CycleId={cycleId} Result=FAIL FaultType={primaryFault?.Code ?? failureName} " +
                $"CounterIncremented=true HistorySaved={historySaved} ResultCommitted=true");
        }

        return true;
    }

    private static bool TryCapturePassLabel(
        TestHistoryRecord history,
        ProductModel model,
        LabelSettings settings,
        out LabelPrintRequest? request,
        out LabelIdentity? identity,
        out string error)
    {
        try
        {
            request = LabelPrintRequest.Capture(history, model, settings);
            string templateType = LabelProfileResolver.NormalizeTemplateType(settings.TemplateType);
            identity = EplLabelService.BuildIdentity(
                request.Data,
                includeAlcLotSuffix: templateType == LabelSettings.LargeTemplate);
            if (templateType == LabelSettings.SmallTemplate ||
                templateType == LabelSettings.SmallQrTemplate)
            {
                identity = identity with { BarcodeValue = request.Data.Barcode };
            }
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            request = null;
            identity = null;
            error = ex.Message;
            return false;
        }
    }

    private static bool ShouldAutoPrintLabel(bool passed, bool autoPrintEnabled) =>
        passed && autoPrintEnabled;

    private static bool HasConfiguredLabelTransport(LabelSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.PrinterName) ||
        !string.IsNullOrWhiteSpace(settings.PrinterCom) ||
        !string.IsNullOrWhiteSpace(settings.RawDestination) ||
        !string.IsNullOrWhiteSpace(settings.ExternalHelperPath);

    private void ApplyDailyProductionStatistics(ModelProductionStatistics stats)
    {
        Total = checked((int)stats.DailyTestCount);
        Pass = checked((int)stats.DailyPassCount);
        Fail = checked((int)stats.DailyFailCount);
        UpdateDailyLotDisplay();
    }

    private void ApplyProductionStatistics(ProductionStatisticsSnapshot stats)
    {
        Total = checked((int)stats.DailyTotal);
        Pass = checked((int)stats.DailyPass);
        Fail = checked((int)stats.DailyFail);
        DailyTestCount = stats.DailyTotal;
        MonthlyTestCount = stats.MonthlyTotal;
        LifetimeTestCount = stats.LifetimeTotal;
        UpdateDailyLotDisplay();
    }

    private void UpdateDailyLotDisplay()
    {
        // LOT hiển thị = LOTNO bắt đầu riêng của mã hàng + Tổng đạt trong ngày.
        // Ví dụ base 2000 và PASS 10 => 2010. Giá trị in tem thực tế vẫn được
        // commit riêng sau khi máy in xác nhận để tránh trùng LOT vật lý.
        long passedToday = Math.Max(0, Pass);
        long startLot = _lotSequence.StartLot;
        long displayLot = startLot > long.MaxValue - passedToday
            ? long.MaxValue
            : startLot + passedToday;
        Lot = displayLot.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void ApplyPartCounter(PartCounterEntry entry)
    {
        ProbeCycleCount = entry.Counter;
        _probeReplacementThreshold = Math.Max(1, entry.ReplacementThreshold);
        Raise(nameof(ProbeReplacementThreshold));
        Raise(nameof(ProbeCycleText));
        Raise(nameof(ProbeMaintenanceDue));
        Raise(nameof(ProbeMaintenanceStatus));
        Raise(nameof(ProbeMaintenanceBackground));
    }

    private void ApplyPartCounter(ProbeCounterSnapshot entry)
    {
        ProbeCycleCount = entry.Counter;
        _probeReplacementThreshold = Math.Max(1, entry.ReplacementThreshold);
        Raise(nameof(ProbeReplacementThreshold));
        Raise(nameof(ProbeCycleText));
        Raise(nameof(ProbeMaintenanceDue));
        Raise(nameof(ProbeMaintenanceStatus));
        Raise(nameof(ProbeMaintenanceBackground));
    }

    private void RecordProbeCycleStarted()
    {
        ProductModel? model = _model;
        if (model is null ||
            !MasterApproved ||
            IsProbeSessionActive ||
            Interlocked.CompareExchange(ref _probeCycleRecordedThisCycle, 1, 0) != 0)
        {
            return;
        }

        bool wasDue = ProbeCycleCount >= ProbeReplacementThreshold;
        _probePersistenceTask = PersistProbeCycleStartedAsync(model, wasDue);
    }

    private void ScheduleSelectedModelPersistence(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            return;
        _modelPersistenceTask = PersistSelectedModelPathAsync(selectedPath);
    }

    private async Task PersistSelectedModelPathAsync(string selectedPath)
    {
        await _modelPersistenceGate.WaitAsync();
        try
        {
            if (!string.Equals(CurrentModelPath, selectedPath, StringComparison.OrdinalIgnoreCase))
                return;
            await Task.Run(() =>
            {
                ProductionConfigService.Save(_productionSettings);
                string fullPath = ResolveModelPath(selectedPath);
                if (!string.Equals(
                        ResolveOptionalModelPath(_productionSettings.LastThtPath),
                        fullPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _productionSettings.LastThtPath = fullPath;
                    ProductionConfigService.Save(_productionSettings);
                }
            });
        }
        catch (Exception ex)
        {
            AddLog($"Không thể lưu mã hàng gần nhất: {ex.Message}");
        }
        finally
        {
            _modelPersistenceGate.Release();
        }
    }

    private async Task PersistProbeCycleStartedAsync(ProductModel model, bool wasDue)
    {
        await _productionPersistenceGate.WaitAsync();
        try
        {
            ProbeCounterSnapshot counter = await ProductionPersistence.IncrementProbeCounterAsync(
                PartIdentitySnapshot.Capture(model),
                ProbeReplacementThreshold,
                _lifetimeCts.Token);
            try
            {
                await Task.Run(
                    () => PartCounterStore.Mirror(model, counter),
                    _lifetimeCts.Token);
            }
            catch (Exception ex)
            {
                AddLog($"LEGACY_MIRROR_FAILED ProbeCycleCount: {ex.Message}");
            }

            AddLog($"SQLite ProbeCounter: {counter.PartNumber} {counter.Counter:N0}/{counter.ReplacementThreshold:N0}.");
            bool reachedDue = counter.Counter >= counter.ReplacementThreshold;
            InvokeUi(() =>
            {
                if (!ReferenceEquals(_model, model))
                    return;
                ApplyPartCounter(counter);
                if (!wasDue && reachedDue)
                {
                    MessageBox.Show(
                        Application.Current?.MainWindow,
                        $"ĐẾN CHU KỲ THAY PROBE PIN\n\nMã hàng: {PartNumber}\n" +
                        $"Chu kỳ hiện tại: {ProbeCycleCount:N0}\nChu kỳ thay thế: {ProbeReplacementThreshold:N0}\n\n" +
                        "Trạng thái: CẦN THAY PROBE PIN",
                        "Cảnh báo bảo trì Probe Pin",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            });
        }
        catch (Exception ex)
        {
            AddLog($"Không thể lưu ProbeCycleCount: {ex.Message}");
        }
        finally
        {
            _productionPersistenceGate.Release();
        }
    }

    public async Task<(bool Reset, string Message)> TryResetProbeCycleAsync(string password)
    {
        ProductModel? model = _model;
        if (model is null)
            return (false, "Chưa chọn mã hàng.");

        if (_productDetectedThisCycle || _waitForProductRelease || _waitForFaultProductRemoval)
            return (false, "Không thể reset trong khi sản phẩm/JIG đang ở trong chu kỳ test.");

        if (!MasterApproved)
            return (false, "Không thể reset counter trong chu trình xác nhận MASTER.");

        if (!AdminAuthenticationService.VerifyProbeMaintenance(password))
            return (false, "Mật khẩu quản trị thay Probe Pin không đúng.");

        try
        {
            long previous = ProbeCycleCount;
            ProbeCounterSnapshot reset = await ProductionPersistence.ResetProbeCounterAsync(
                PartIdentitySnapshot.Capture(model),
                ProbeReplacementThreshold,
                "PROBE_ADMIN",
                _productionSettings.DeviceName,
                _lifetimeCts.Token);
            try
            {
                await Task.Run(
                    () => PartCounterStore.Mirror(model, reset),
                    _lifetimeCts.Token);
            }
            catch (Exception ex)
            {
                AddLog($"LEGACY_MIRROR_FAILED sau reset Probe Pin: {ex.Message}");
            }

            ApplyPartCounter(reset);
            Interlocked.Exchange(ref _probeCycleRecordedThisCycle, 0);
            string message = $"Đã lưu thay Probe Pin vào SQLite. Counter {previous:N0} → 0.";
            AddLog(
                $"MAINTENANCE: PROBE PIN REPLACED; part={reset.PartNumber}; " +
                $"previous={previous}; database={HistoryStore.DatabasePath}; admin=PROBE_ADMIN.");
            return (true, message);
        }
        catch (Exception ex)
        {
            AddLog($"Không thể lưu reset Probe Pin: {ex.Message}");
            return (false, "Chưa lưu được thay đổi Probe Pin. Vui lòng thử lại.");
        }
    }

    private async Task PrintPassLabelSafeAsync(
        LabelPrintRequest request,
        TestHistoryStore historyStore,
        long historyId)
    {
        try
        {
            if (!await ProductionPersistence.TryBeginFirstPrintAsync(
                    historyId, request.CycleId, _lifetimeCts.Token))
            {
                AddLog($"LABEL DUPLICATE BLOCKED: cycle {request.CycleId} đã có first-print transaction.");
                return;
            }

            if (!_lotSequence.IsCommitCandidate(request.CycleId, request.Data.LotNo))
            {
                string blocked = $"LOT {request.Data.LotNo} đang chờ LOT trước đó được in/commit; chưa gửi dữ liệu tới máy in.";
                await UpdateLabelPrintOutcomeSafeAsync(
                    historyStore, historyId, request.CycleId, LabelPrintStatus.Failed, null, blocked);
                SetFailedLabelContext(new LabelPrintContext(request, historyStore, historyId), blocked);
                AddLog($"LABEL BLOCKED: cycle {request.CycleId}; {blocked}");
                return;
            }

            LabelPrintTransportResult result = await _labelPrintService.PrintPassLabelAsync(
                request, _lifetimeCts.Token);

            bool commitUnknown = false;
            if (result.Printed &&
                !_lotSequence.TryCommitSuccessfulPrint(request.CycleId, request.Data.LotNo, out string commitError))
            {
                commitUnknown = true;
                result = new LabelPrintTransportResult(
                    false,
                    $"Printer accepted LOT {request.Data.LotNo}, but LOT commit failed: {commitError}");
            }

            LabelPrintStatus status = commitUnknown
                ? LabelPrintStatus.Unknown
                : result.Printed
                ? LabelPrintStatus.Printed
                : LabelPrintStatus.Failed;
            DateTime? printedAt = result.Printed ? DateTime.Now : null;
            await UpdateLabelPrintOutcomeSafeAsync(
                historyStore,
                historyId,
                request.CycleId,
                status,
                printedAt,
                result.Message,
                status == LabelPrintStatus.Printed ? request.Data.Barcode : null);
            AddLog($"LABEL {status.ToString().ToUpperInvariant()}: cycle {request.CycleId}; {result.Message}");

            if (result.Printed)
            {
                InvokeUi(UpdateDailyLotDisplay);
                SetSuccessfulLabelContext(new LabelPrintContext(request, historyStore, historyId));
            }

            if (commitUnknown)
            {
                SetUnknownLabelStatus(request, result.Message);
                ShowLabelWarning(
                    $"Tem LOT {request.Data.LotNo} có thể đã được in. Không tự in lại để tránh trùng tem.");
            }
            else if (!result.Printed)
            {
                SetFailedLabelContext(new LabelPrintContext(request, historyStore, historyId), result.Message);
                ShowLabelWarning(
                    "Sản phẩm đã PASS nhưng chưa in được tem. Hãy rút/cắm lại cáp và chọn lại cổng COM.");
            }
        }
        catch (OperationCanceledException)
        {
            const string message = "Giao dịch in bị hủy; trạng thái tem vật lý chưa thể xác định. Không tự retry.";
            await UpdateLabelPrintOutcomeSafeAsync(
                historyStore, historyId, request.CycleId, LabelPrintStatus.Unknown, null, message);
            AddLog($"LABEL UNKNOWN: cycle {request.CycleId}; {message}");
            SetUnknownLabelStatus(request, message);
        }
        catch (Exception ex)
        {
            string message = $"Printer/driver/transport error: {ex.Message}. Trạng thái tem vật lý chưa xác định; không tự retry.";
            await UpdateLabelPrintOutcomeSafeAsync(
                historyStore, historyId, request.CycleId, LabelPrintStatus.Unknown, null, message);
            AddLog($"LABEL UNKNOWN: cycle {request.CycleId}; {message}");
            SetUnknownLabelStatus(request, message);
            ShowLabelWarning(
                "Sản phẩm vẫn PASS nhưng chưa xác định được trạng thái in tem. Không tự in lại để tránh trùng tem.");
        }
    }

    private async Task RetryLastFailedLabelAsync()
    {
        LabelPrintContext? context;
        lock (_labelStateGate)
            context = _failedLabelPrint;

        if (context is null)
            return;

        if (!_lotSequence.TryRestoreReservation(context.Request.CycleId, context.Request.Data.LotNo))
        {
            ShowLabelWarning($"Chưa thể thử in lại tem LOT {context.Request.Data.LotNo}.");
            return;
        }

        await PrintPassLabelSafeAsync(context.Request, context.HistoryStore, context.HistoryId);
    }

    private async Task ReprintLastSuccessfulLabelAsync()
    {
        LabelPrintContext? context;
        lock (_labelStateGate)
            context = _lastSuccessfulLabelPrint;

        if (context is null)
            return;

        try
        {
            LabelPrintTransportResult result = await _labelPrintService.PrintPassLabelAsync(
                context.Request, _lifetimeCts.Token);
            if (!result.Printed)
            {
                InvokeUi(() => LabelStatusText = $"LỖI IN LẠI TEM - LOT {context.Request.Data.LotNo}");
                ShowLabelWarning(
                    $"Chưa in lại được tem LOT {context.Request.Data.LotNo}. " +
                    "Hãy rút/cắm lại cáp và chọn lại cổng COM.");
                return;
            }

            await ProductionPersistence.IncrementLabelReprintAsync(
                context.HistoryId,
                context.Request.CycleId,
                DateTime.Now,
                result.Message,
                _lifetimeCts.Token);
            InvokeUi(() => LabelStatusText = $"TEM: ĐÃ IN LẠI LOT {context.Request.Data.LotNo}");
            AddLog($"LABEL REPRINTED: cycle {context.Request.CycleId}; LOT {context.Request.Data.LotNo}; không tăng LOT.");
        }
        catch (Exception ex)
        {
            AddLog($"LABEL REPRINT ERROR: {ex}");
            InvokeUi(() => LabelStatusText = $"LỖI IN LẠI TEM - LOT {context.Request.Data.LotNo}");
            ShowLabelWarning(
                $"Chưa in lại được tem LOT {context.Request.Data.LotNo}. Hãy rút/cắm lại cáp và chọn lại cổng COM.");
        }
    }

    private void SetFailedLabelContext(LabelPrintContext context, string message)
    {
        lock (_labelStateGate)
            _failedLabelPrint = context;

        InvokeUi(() =>
        {
            LabelStatusText = $"LỖI IN TEM - LOT {context.Request.Data.LotNo}";
            Raise(nameof(CanRetryLabel));
            RetryLabelCommand.RaiseCanExecuteChanged();
        });
    }

    private void SetSuccessfulLabelContext(LabelPrintContext context)
    {
        lock (_labelStateGate)
        {
            _failedLabelPrint = null;
            _lastSuccessfulLabelPrint = context;
        }

        InvokeUi(() =>
        {
            LabelStatusText = $"TEM: ĐÃ IN LOT {context.Request.Data.LotNo}";
            Raise(nameof(CanRetryLabel));
            Raise(nameof(CanReprintLabel));
            RetryLabelCommand.RaiseCanExecuteChanged();
            ReprintLabelCommand.RaiseCanExecuteChanged();
        });
    }

    private void SetUnknownLabelStatus(LabelPrintRequest request, string message)
    {
        lock (_labelStateGate)
            _failedLabelPrint = null;

        InvokeUi(() =>
        {
            LabelStatusText = $"TEM CHƯA XÁC ĐỊNH - LOT {request.Data.LotNo}";
            Raise(nameof(CanRetryLabel));
            RetryLabelCommand.RaiseCanExecuteChanged();
        });
    }

    private async Task UpdateLabelPrintOutcomeSafeAsync(
        TestHistoryStore historyStore,
        long historyId,
        string cycleId,
        LabelPrintStatus status,
        DateTime? printTimestamp,
        string message,
        string? printedBarcode = null)
    {
        try
        {
            await ProductionPersistence.UpdateLabelPrintOutcomeAsync(
                historyId,
                cycleId,
                status,
                printTimestamp,
                message,
                printedBarcode,
                _lifetimeCts.Token);
        }
        catch (Exception ex)
        {
            AddLog($"LABEL HISTORY ERROR: cycle {cycleId}; không thể lưu {status}: {ex.Message}");
        }
    }

    private void ShowLabelWarning(string message) =>
        InvokeUi(() => MessageBox.Show(
            message,
            "Lỗi in tem",
            MessageBoxButton.OK,
            MessageBoxImage.Warning));

    private void RefreshFaults() => RefreshFaultsFromSnapshot(rowsSnapshot: null);

    private void RefreshFaultsFromSnapshot(TestEnginePresentationSnapshot? rowsSnapshot)
    {
        long refreshStarted = Stopwatch.GetTimestamp();
        if (IsRuntimeMode(RuntimeMode.Probe) ||
            Volatile.Read(ref _probeSessionActive) != 0)
        {
            // Probe chạy song song: không đụng vào Faults (bảng cấu hình Production).
            // Chỉ tắt cảnh báo dây để Probe không bị hiểu nhầm là lỗi sản phẩm.
            _sound.SetWiringFaultAlarm(false);
            RaiseTestStatistics();
            return;
        }

        IReadOnlyList<FaultRow> desiredRows;
        if (!MasterApproved && IsMasterBadPhase)
        {
            // Master lỗi dùng cùng bảng lỗi sản xuất, nhưng trước khi có mẫu
            // thật trên JIG bảng phải hoàn toàn trống.
            desiredRows = _presentationCycleStarted
                ? BuildMasterFaultGridRows()
                : Array.Empty<FaultRow>();
        }
        else
        {
            bool masterCycleActive = !MasterApproved && IsMasterSequenceActive;
            bool hasProductEvidence = rowsSnapshot?.Electrical.ProductEvidence ??
                                      _engine.HasProductActivity;
            bool probeOwnsPresentation = IsProbeOwningProductionPresentation();
            if (!_presentationCycleStarted &&
                (_cycleActive || masterCycleActive) &&
                !probeOwnsPresentation &&
                hasProductEvidence)
            {
                _presentationCycleStarted = true;
                RaiseCenterPresentation();
            }
            else if (rowsSnapshot is not null &&
                     !hasProductEvidence &&
                     !probeOwnsPresentation &&
                     CurrentProductionRuntimeState == ProductionRuntimeState.WaitingForProduct &&
                     CurrentProductionPresentationMode == ProductionPresentationMode.Waiting &&
                     CurrentProbePresentationState is ProbePresentationState.Inactive or ProbePresentationState.Released &&
                     _presentationCycleStarted)
            {
                _presentationCycleStarted = false;
                RaiseCenterPresentation();
            }

            if (CurrentProductionPhase == ProductionPhase.WaterProof &&
                !_waitForProductRelease &&
                !_waitForFaultProductRemoval)
            {
                // Trong khi Leak đang chạy/chờ lắp lại RET, vùng dưới phải
                // trống. Kết quả Leak chỉ hiển thị ở thẻ TEST LEAK phía trên.
                desiredRows = Array.Empty<FaultRow>();
            }
            else if (_waitForProductRelease || _waitForFaultProductRemoval)
            {
                // HTDRV_REMOVAL_DISPLAY_2026-09-05: sau PASS/FAIL, đảo ý
                // nghĩa bảng sang "connection còn trên jig". Engine chỉ đọc
                // snapshot quan hệ hiện tại, không sửa detection/PASS latch.
                desiredRows = rowsSnapshot is { Removal: true }
                    ? rowsSnapshot.Rows
                    : _engine.BuildRemovalRows();
            }
            else
            {
                desiredRows = _presentationCycleStarted
                    ? rowsSnapshot is { Removal: false }
                        ? rowsSnapshot.Rows
                        : _engine.BuildRows()
                    : Array.Empty<FaultRow>();
            }
        }

        FaultRow[] probeRows = ProbeContacts.ToArray();
        if (probeRows.Length > 0 && IsRuntimeMode(RuntimeMode.Production))
            desiredRows = probeRows.Concat(desiredRows).ToArray();

        long synchronizeStarted = Stopwatch.GetTimestamp();
        SynchronizeFaultRows(desiredRows);
        double synchronizeMs = Stopwatch.GetElapsedTime(synchronizeStarted).TotalMilliseconds;
        double refreshMs = Stopwatch.GetElapsedTime(refreshStarted).TotalMilliseconds;
        if (synchronizeMs > 16 || refreshMs > 16)
        {
            AsyncFileLogService.Current.Performance(
                $"UI_PERF_WARNING phase=FAULT_ROWS rows={desiredRows.Count} " +
                $"sync_ms={synchronizeMs:0.###} total_ms={refreshMs:0.###}");
        }

        RaiseTestStatistics();

        // Master GOOD/BAD dùng fault làm dữ liệu xác nhận, không phải Product FAIL.
        // Không phát TESTPOINT, không popup/eject fault, không tăng thống kê.
        if (!MasterApproved)
        {
            _sound.SetWiringFaultAlarm(false);
            return;
        }

        // Chỉ phát TESTPOINT khi đang ở chu kỳ production và chưa bước vào
        // giai đoạn chờ tháo sản phẩm. HandleWiringFaultAsync giữ âm lặp cho
        // tới khi popup được xác nhận.
        if (!_waitForFaultProductRemoval)
            _sound.SetWiringFaultAlarm(WiringFaultCount > 0 && (_cycleActive || _sound.IsWiringFaultAlarmActive));
        else
            _sound.SetWiringFaultAlarm(false);


    }

    private void SynchronizeFaultRows(IReadOnlyList<FaultRow> desiredRows)
    {
        UpdateCachedFaultCounts(desiredRows);
        try
        {
            // HTDRV_UI_DELTA_10CARD_2026-09-05: collection được đồng bộ vi sai;
            // không Clear/Add lại toàn bảng khi chỉ một network thay đổi.
            // Lần đầu hiện model lớn chỉ gửi một Reset notification. Sau đó mọi
            // frame đều chạy delta; không Reset lại DataGrid.
            // Snapshot đầu tiên/xóa toàn bộ chỉ phát một Reset để DataGrid
            // virtualized dựng hoặc giải phóng row đúng một lần. Sau đó giữ
            // delta cho cả bảng lớn: một dây đạt chỉ Remove đúng endpoint của
            // dây đó, không rebuild hàng trăm row còn lại.
            if (Faults.Count == 0)
            {
                if (desiredRows.Count > 0)
                    Faults.ReplaceAll(desiredRows);
                return;
            }

            if (desiredRows.Count == 0)
            {
                Faults.ReplaceAll(Array.Empty<FaultRow>());
                return;
            }

            // Đường nóng Production: các row tĩnh được cache từ lúc SetModel.
            // Khi một network PASS, desiredRows chỉ là collection hiện tại đã
            // bỏ vài object nhưng vẫn giữ nguyên thứ tự. Xóa trực tiếp bằng
            // reference để không dựng RowKey string và hai dictionary cho cả
            // bảng 500/640 IO. Mọi trường hợp add/replace/reorder vẫn đi qua
            // bộ đồng bộ tổng quát phía dưới, nên presentation không đổi.
            if (TryRemoveMissingFaultRowsInOrder(desiredRows))
                return;

            // Khi một dây vừa mất tiếp xúc, các row cache của dây đó xuất hiện
            // lại đúng vị trí cũ. Đây là phép chèn thuần theo thứ tự, xử lý O(n)
            // và không reset toàn DataGrid 300-640 dòng.
            if (TryInsertMissingFaultRowsInOrder(desiredRows))
                return;

            // Xóa key thừa TRƯỚC khi căn vị trí. Nếu BG01 ở đầu bảng PASS,
            // cách cũ Move toàn bộ BG02..BG200 lên rồi mới xóa đuôi. Cách này
            // chỉ phát đúng các Remove của BG01, các row sau tự dịch chỉ số.
            Dictionary<string, int> desiredCounts = new(StringComparer.Ordinal);
            foreach (FaultRow desired in desiredRows)
            {
                string key = RowKey(desired);
                desiredCounts[key] = desiredCounts.GetValueOrDefault(key) + 1;
            }

            Dictionary<string, int> currentCounts = new(StringComparer.Ordinal);
            foreach (FaultRow current in Faults)
            {
                string key = RowKey(current);
                currentCounts[key] = currentCounts.GetValueOrDefault(key) + 1;
            }

            int sharedRows = 0;
            foreach ((string key, int desiredCount) in desiredCounts)
                sharedRows += Math.Min(desiredCount, currentCounts.GetValueOrDefault(key));

            int structuralChanges =
                (Faults.Count - sharedRows) + (desiredRows.Count - sharedRows);
            int largeChangeThreshold = Math.Max(32, Math.Max(Faults.Count, desiredRows.Count) / 4);
            if (structuralChanges > largeChangeThreshold)
            {
                Faults.ReplaceAll(desiredRows);
                return;
            }

            if (structuralChanges == 0)
            {
                bool reordered = false;
                for (int index = 0; index < desiredRows.Count; index++)
                {
                    if (!string.Equals(RowKey(Faults[index]), RowKey(desiredRows[index]), StringComparison.Ordinal))
                    {
                        reordered = true;
                        break;
                    }
                }

                if (reordered)
                {
                    Faults.ReplaceAll(desiredRows);
                    return;
                }
            }

            for (int currentIndex = Faults.Count - 1; currentIndex >= 0; currentIndex--)
            {
                string key = RowKey(Faults[currentIndex]);
                int allowed = desiredCounts.GetValueOrDefault(key);
                if (currentCounts[key] <= allowed)
                    continue;

                Faults.RemoveAt(currentIndex);
                currentCounts[key]--;
            }

            for (int desiredIndex = 0; desiredIndex < desiredRows.Count; desiredIndex++)
            {
                FaultRow desired = desiredRows[desiredIndex];
                string desiredKey = RowKey(desired);

                if (desiredIndex < Faults.Count &&
                    string.Equals(RowKey(Faults[desiredIndex]), desiredKey, StringComparison.Ordinal))
                {
                    Faults[desiredIndex].Status = desired.Status;
                    continue;
                }

                int matchingIndex = -1;
                for (int currentIndex = desiredIndex + 1; currentIndex < Faults.Count; currentIndex++)
                {
                    if (string.Equals(RowKey(Faults[currentIndex]), desiredKey, StringComparison.Ordinal))
                    {
                        matchingIndex = currentIndex;
                        break;
                    }
                }

                if (matchingIndex >= 0)
                {
                    Faults.Move(matchingIndex, desiredIndex);
                    Faults[desiredIndex].Status = desired.Status;
                }
                else
                {
                    Faults.Insert(desiredIndex, desired);
                }
            }

            while (Faults.Count > desiredRows.Count)
                Faults.RemoveAt(Faults.Count - 1);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            // Đây là lỗi đồng bộ giao diện, không phải bằng chứng card/IO chập
            // chờn. Tự dựng lại bảng và tiếp tục scan; không khóa phần cứng và
            // không yêu cầu operator khởi tạo lại thiết bị.
            AsyncFileLogService.Current.Error(
                $"FAULT ROW UI RECOVERY desired={desiredRows.Count} current={Faults.Count}: {ex}");
            Faults.ReplaceAll(desiredRows);
            AddLog("Danh sách lỗi CLIP/I/O đã tự đồng bộ lại; thiết bị tiếp tục chạy, không cần khởi tạo lại.");
        }
    }

    private bool TryRemoveMissingFaultRowsInOrder(IReadOnlyList<FaultRow> desiredRows)
    {
        if (desiredRows.Count >= Faults.Count)
            return false;

        int desiredIndex = 0;
        for (int currentIndex = 0;
             currentIndex < Faults.Count && desiredIndex < desiredRows.Count;
             currentIndex++)
        {
            if (ReferenceEquals(Faults[currentIndex], desiredRows[desiredIndex]))
                desiredIndex++;
        }

        // Không phải phép xóa thuần theo thứ tự: để thuật toán key-based hiện
        // tại xử lý add/replace/move và các fault row động.
        if (desiredIndex != desiredRows.Count)
            return false;

        // Một thay đổi lớn mà phát từng Remove sẽ bắt DataGrid layout lại nhiều
        // lần trên cùng một frame. Một Reset duy nhất rẻ hơn và vẫn giữ snapshot
        // cuối cùng chính xác; delta nhỏ tiếp tục dùng RemoveAt để giữ scroll.
        if (Faults.Count - desiredRows.Count > 8)
        {
            Faults.ReplaceAll(desiredRows);
            return true;
        }

        desiredIndex = desiredRows.Count - 1;
        for (int currentIndex = Faults.Count - 1; currentIndex >= 0; currentIndex--)
        {
            if (desiredIndex >= 0 &&
                ReferenceEquals(Faults[currentIndex], desiredRows[desiredIndex]))
            {
                desiredIndex--;
                continue;
            }

            Faults.RemoveAt(currentIndex);
        }

        return true;
    }

    private bool TryInsertMissingFaultRowsInOrder(IReadOnlyList<FaultRow> desiredRows)
    {
        int addedCount = desiredRows.Count - Faults.Count;
        if (addedCount <= 0 ||
            addedCount > Math.Max(32, desiredRows.Count / 4))
        {
            return false;
        }

        int currentIndex = 0;
        for (int desiredIndex = 0;
             desiredIndex < desiredRows.Count && currentIndex < Faults.Count;
             desiredIndex++)
        {
            if (ReferenceEquals(desiredRows[desiredIndex], Faults[currentIndex]))
                currentIndex++;
        }

        // Không phải phép chèn thuần của các row cache theo thứ tự. Fault động,
        // replace hoặc reorder tiếp tục dùng bộ đồng bộ key-based tổng quát.
        if (currentIndex != Faults.Count)
            return false;

        // Tương tự đường xóa: tránh hàng chục/hàng trăm CollectionChanged/Add
        // khiến WPF đo và layout DataGrid lặp lại trong một lần cập nhật.
        if (addedCount > 8)
        {
            Faults.ReplaceAll(desiredRows);
            return true;
        }

        currentIndex = 0;
        for (int desiredIndex = 0; desiredIndex < desiredRows.Count; desiredIndex++)
        {
            FaultRow desired = desiredRows[desiredIndex];
            if (currentIndex < Faults.Count &&
                ReferenceEquals(Faults[currentIndex], desired))
            {
                currentIndex++;
                continue;
            }

            Faults.Insert(currentIndex, desired);
            currentIndex++;
        }

        return true;
    }

    private static string RowKey(FaultRow row) => row.PresentationKey;

    private static (int Removed, int Added, int Changed) CalculateRenderedRowDelta(
        IReadOnlyList<(string Key, string Status)> before,
        IReadOnlyList<FaultRow> after)
    {
        Dictionary<string, List<string>> beforeByKey = before
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Status).OrderBy(value => value, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        Dictionary<string, List<string>> afterByKey = after
            .GroupBy(RowKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Status).OrderBy(value => value, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        int removed = 0;
        int added = 0;
        int contentChanged = 0;
        foreach (string key in beforeByKey.Keys.Union(afterByKey.Keys, StringComparer.Ordinal))
        {
            List<string> oldValues = beforeByKey.GetValueOrDefault(key) ?? [];
            List<string> newValues = afterByKey.GetValueOrDefault(key) ?? [];
            int shared = Math.Min(oldValues.Count, newValues.Count);
            removed += oldValues.Count - shared;
            added += newValues.Count - shared;
            for (int index = 0; index < shared; index++)
            {
                if (!string.Equals(oldValues[index], newValues[index], StringComparison.Ordinal))
                    contentChanged++;
            }
        }

        return (removed, added, removed + added + contentChanged);
    }

    private void UpdateCachedFaultCounts(IReadOnlyList<FaultRow> rows)
    {
        int open = 0;
        int wrong = 0;
        int shortCount = 0;
        foreach (FaultRow row in rows)
        {
            switch (row.Kind)
            {
                case FaultKind.MissingConnection:
                    open++;
                    break;
                case FaultKind.WrongWiring:
                    wrong++;
                    break;
                case FaultKind.Short:
                    shortCount++;
                    break;
            }
        }

        bool openChanged = _cachedOpenCount != open;
        bool wrongChanged = _cachedWrongCount != wrong;
        bool shortChanged = _cachedShortCount != shortCount;
        int previousWiring = _cachedWrongCount + _cachedShortCount;

        _cachedOpenCount = open;
        _cachedWrongCount = wrong;
        _cachedShortCount = shortCount;

        if (openChanged)
            Raise(nameof(OpenCount));
        if (wrongChanged)
        {
            Raise(nameof(WrongCount));
            Raise(nameof(WrongCountText));
        }
        if (shortChanged)
            Raise(nameof(ShortCount));
        if (previousWiring != wrong + shortCount)
            Raise(nameof(WiringFaultCount));
    }

    private void RaiseTestStatistics()
    {
        Raise(nameof(PassedNetworkCount));
        Raise(nameof(ExpectedNetworkCount));
        Raise(nameof(NetworkProgress));
        Raise(nameof(MasterDetectedFaultCount));
        Raise(nameof(MasterRequiredFaultCount));
        Raise(nameof(WrongCountText));
        RaiseActiveFault();
    }

    private void RaiseActiveFault()
    {
        _visiblePrimaryFaultSnapshotValid = false;
        _visiblePrimaryFaultSnapshot = null;
        Raise(nameof(ResultStatusText));
        Raise(nameof(ActiveFaultTitle));
        Raise(nameof(ActiveFaultMessage));
        Raise(nameof(ActiveFaultExpectedText));
        Raise(nameof(ActiveFaultActualText));
        Raise(nameof(ActiveFaultBackground));
        Raise(nameof(ActiveFaultForeground));
    }

    public void AddExternalLog(string message) => AddLog(message);

    private void AddLog(string text)
    {
        AsyncFileLogService.Current.Test(text);
    }

    private void ShowBoardUnavailablePresentation(bool force = false)
    {
        int alreadyApplied = Interlocked.Exchange(ref _boardUnavailablePresentationApplied, 1);
        if (!force && alreadyApplied != 0)
            return;

        InvokeUi(() =>
        {
            // Snapshot frame cuối không còn đáng tin cậy sau khi bo mất kết nối.
            // Giữ bảng continuity/header hiện hữu nhưng không để row cũ khiến
            // người vận hành hiểu nhầm là trạng thái phần cứng hiện tại.
            SelectedOperationTabIndex = 0;
            SynchronizeFaultRows(Array.Empty<FaultRow>());
            ResetProductPresentationCycle();
        });
    }

    private static void InvokeUi(Action action)
    {
        var dispatcher =
            Application.Current?.Dispatcher;

        if (dispatcher is null ||
            dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}
