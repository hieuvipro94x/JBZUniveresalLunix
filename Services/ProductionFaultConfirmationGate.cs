using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public sealed record UnexpectedFaultObservation(
    int SourceIo,
    int TargetIo,
    ProductFaultType Type);

public readonly record struct ProductionFaultConfirmationSnapshot(
    IReadOnlySet<string> ConfirmedOpenKeys,
    IReadOnlySet<(int SourceIo, int TargetIo)> ConfirmedUnexpectedPairs,
    bool ContactUnstable,
    bool ContactLossTimedOut,
    bool ProductStable);

/// <summary>
/// Confirmation state above raw transport observations. Missing/open contacts are
/// ignored as product faults; Short/WrongConnection keep their own debounce.
/// </summary>
public sealed class ProductionFaultConfirmationGate
{
    private static readonly IReadOnlySet<string> EmptyOpenKeys =
        new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlySet<(int SourceIo, int TargetIo)> EmptyUnexpectedPairs =
        new HashSet<(int SourceIo, int TargetIo)>();

    private readonly ProductionSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<string> _confirmedOpen = new(StringComparer.Ordinal);
    private readonly Dictionary<(int SourceIo, int TargetIo), UnexpectedCandidate> _unexpectedCandidates = [];
    private readonly HashSet<(int SourceIo, int TargetIo)> _confirmedUnexpected = [];

    private bool _productActivitySeen;
    private bool _lastHasProductActivity;
    private bool _contactUnstable;
    private bool _contactLossTimedOut;
    private bool _productStable;
    private long? _firstActivityAt;
    private long? _contactLossSince;
    private long? _allCorrectSince;

    public ProductionFaultConfirmationGate(
        ProductionSettings settings,
        TimeProvider? timeProvider = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ProductionFaultConfirmationSnapshot Observe(
        IReadOnlyDictionary<string, bool> expectedConnections,
        IReadOnlyCollection<UnexpectedFaultObservation> unexpectedConnections,
        bool hasProductActivity,
        bool readyToEvaluateFaults = true)
    {
        ArgumentNullException.ThrowIfNull(expectedConnections);
        ArgumentNullException.ThrowIfNull(unexpectedConnections);

        long now = _timeProvider.GetTimestamp();
        UpdateProductPresence(hasProductActivity, now);
        bool productSettled = readyToEvaluateFaults &&
                              IsReadyToEvaluateProductFaults(hasProductActivity, now);
        // Wrong/Short là tín hiệu chủ động từ một frame Production hợp lệ. Không
        // bắt chúng chờ thêm ProductSettleTime: debounce riêng 100 ms đã đủ lọc
        // một frame nhiễu và giúp lỗi được báo ngay ở frame ổn định kế tiếp.
        bool readyForUnexpectedFault = readyToEvaluateFaults && hasProductActivity;
        UpdateOpenCandidates(expectedConnections, productSettled, now);
        UpdateUnexpectedCandidates(unexpectedConnections, readyForUnexpectedFault, now);
        UpdateCleanStability(expectedConnections, unexpectedConnections, hasProductActivity, now);
        _lastHasProductActivity = hasProductActivity;

        return Snapshot();
    }

    public void Reset()
    {
        _confirmedOpen.Clear();
        _unexpectedCandidates.Clear();
        _confirmedUnexpected.Clear();
        _productActivitySeen = false;
        _lastHasProductActivity = false;
        _contactUnstable = false;
        _contactLossTimedOut = false;
        _productStable = false;
        _firstActivityAt = null;
        _contactLossSince = null;
        _allCorrectSince = null;
    }

    public void ClearUnexpected()
    {
        _unexpectedCandidates.Clear();
        _confirmedUnexpected.Clear();
        _allCorrectSince = null;
        _productStable = false;
    }

    private void UpdateProductPresence(bool hasActivity, long now)
    {
        if (hasActivity)
        {
            if (!_lastHasProductActivity)
                _firstActivityAt = now;

            _productActivitySeen = true;
            _contactLossSince = null;
            _contactLossTimedOut = false;
            return;
        }

        _firstActivityAt = null;
        _allCorrectSince = null;
        _productStable = false;

        if (!_productActivitySeen)
            return;

        // Máº¥t toÃ n bá»™ contact trong má»™t cycle chÆ°a chá»‘t káº¿t quáº£ khÃ´ng
        // Ä‘á»§ báº±ng chá»©ng Ä‘á»ƒ káº¿t luáº­n OPEN cá»§a sáº£n pháº©m.
        _contactUnstable = true;
        _confirmedOpen.Clear();
        _contactLossSince ??= now;
        _contactLossTimedOut = HasElapsed(
            _contactLossSince.Value,
            ProductionTimingPolicy.DefaultJigContactUnstableWindowMs,
            now);
    }

    private bool IsReadyToEvaluateProductFaults(bool hasProductActivity, long now) =>
        hasProductActivity &&
        _firstActivityAt is long firstActivity &&
        HasElapsed(firstActivity, ProductionTimingPolicy.DefaultProductSettleTimeMs, now);

    private void UpdateOpenCandidates(
        IReadOnlyDictionary<string, bool> expectedConnections,
        bool readyToEvaluate,
        long now)
    {
        _confirmedOpen.Clear();
    }

    private void UpdateUnexpectedCandidates(
        IReadOnlyCollection<UnexpectedFaultObservation> observations,
        bool readyToEvaluate,
        long now)
    {
        if (observations.Count == 0 || !readyToEvaluate)
        {
            _unexpectedCandidates.Clear();
            _confirmedUnexpected.Clear();
            return;
        }

        var current = observations
            .GroupBy(item => (item.SourceIo, item.TargetIo))
            .ToDictionary(group => group.Key, group => group.First().Type);

        foreach ((int SourceIo, int TargetIo) key in _unexpectedCandidates.Keys
                     .Where(key => !current.ContainsKey(key))
                     .ToArray())
        {
            _unexpectedCandidates.Remove(key);
            _confirmedUnexpected.Remove(key);
        }

        foreach (((int SourceIo, int TargetIo) key, ProductFaultType type) in current)
        {
            if (!_unexpectedCandidates.TryGetValue(key, out UnexpectedCandidate? candidate) ||
                candidate.Type != type)
            {
                candidate = new UnexpectedCandidate(type, now);
                _unexpectedCandidates[key] = candidate;
                _confirmedUnexpected.Remove(key);
            }

            int confirmMs = type == ProductFaultType.ShortCircuit
                ? ProductionTimingPolicy.DefaultShortCircuitConfirmMs
                : ProductionTimingPolicy.DefaultWrongConnectionConfirmMs;

            if (HasElapsed(candidate.StartedAt, confirmMs, now))
                _confirmedUnexpected.Add(key);
        }
    }

    private void UpdateCleanStability(
        IReadOnlyDictionary<string, bool> expectedConnections,
        IReadOnlyCollection<UnexpectedFaultObservation> unexpectedConnections,
        bool hasProductActivity,
        long now)
    {
        bool allExpectedConnected = expectedConnections.Count > 0;
        if (allExpectedConnected)
        {
            foreach (bool connected in expectedConnections.Values)
            {
                if (!connected)
                {
                    allExpectedConnected = false;
                    break;
                }
            }
        }

        bool allCorrect = hasProductActivity &&
                          allExpectedConnected &&
                          unexpectedConnections.Count == 0;

        if (!allCorrect)
        {
            _allCorrectSince = null;
            _productStable = false;
            return;
        }

        _allCorrectSince ??= now;
        _productStable = HasElapsed(
            _allCorrectSince.Value,
            ProductionTimingPolicy.DefaultProductSettleTimeMs,
            now);

        if (_productStable && _contactUnstable)
        {
            // Má»™t chu ká»³ continuity sáº¡ch, liÃªn tá»¥c lÃ  re-evaluation PASS
            // cho contact warning; khÃ´ng dá»‹ch ngÆ°á»£c warning thÃ nh product fault.
            _contactUnstable = false;
            _contactLossTimedOut = false;
        }
    }

    private bool HasElapsed(long started, int milliseconds, long now) =>
        milliseconds <= 0 ||
        _timeProvider.GetElapsedTime(started, now) >= TimeSpan.FromMilliseconds(milliseconds);

    private ProductionFaultConfirmationSnapshot Snapshot() => new(
        _confirmedOpen.Count == 0
            ? EmptyOpenKeys
            : _confirmedOpen.ToHashSet(StringComparer.Ordinal),
        _confirmedUnexpected.Count == 0
            ? EmptyUnexpectedPairs
            : _confirmedUnexpected.ToHashSet(),
        _contactUnstable,
        _contactLossTimedOut,
        _productStable);

    private sealed record UnexpectedCandidate(ProductFaultType Type, long StartedAt);
}
