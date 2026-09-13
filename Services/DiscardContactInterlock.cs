using JBZUniversalTester.Models;

namespace JBZUniversalTester.Services;

public enum DiscardContactTransition
{
    None = 0,
    FirstPassDetected = 1,
    Completed = 2
}

/// <summary>
/// Theo dõi một lần đưa hàng qua cảm biến thùng hàng lỗi. Một lần hợp lệ gồm
/// cảm biến THÔNG khi hàng đi qua và sau đó phải NGẮT khi hàng đã qua khỏi cảm biến.
/// Nếu lúc ARM đang THÔNG, bắt buộc chờ NGẮT làm baseline để tiếp điểm kẹt
/// không thể tự tạo một lần xác nhận giả.
/// </summary>
public sealed class DiscardContactInterlock
{
    private enum State
    {
        Idle,
        AwaitingOpenBaseline,
        AwaitingFirstPass,
        AwaitingReleaseAfterPass,
        Completed
    }

    private readonly object _gate = new();
    private State _state;

    public bool IsArmed
    {
        get { lock (_gate) return _state is not State.Idle and not State.Completed; }
    }

    public bool IsCompleted
    {
        get { lock (_gate) return _state == State.Completed; }
    }

    public void Arm(bool contactClosed)
    {
        lock (_gate)
            _state = contactClosed ? State.AwaitingOpenBaseline : State.AwaitingFirstPass;
    }

    public void Reset()
    {
        lock (_gate)
            _state = State.Idle;
    }

    public DiscardContactTransition Observe(bool contactClosed)
    {
        lock (_gate)
        {
            switch (_state)
            {
                case State.AwaitingOpenBaseline when !contactClosed:
                    _state = State.AwaitingFirstPass;
                    break;
                case State.AwaitingFirstPass when contactClosed:
                    _state = State.AwaitingReleaseAfterPass;
                    return DiscardContactTransition.FirstPassDetected;
                case State.AwaitingReleaseAfterPass when !contactClosed:
                    _state = State.Completed;
                    return DiscardContactTransition.Completed;
            }

            return DiscardContactTransition.None;
        }
    }

    public static bool IsContactClosed(ScanFrame frame, IReadOnlyList<int> contactIo) =>
        GetActiveContactIo(frame, contactIo).Count > 0;

    /// <summary>
    /// Hai dòng _DISCARD là hai I/O cảm biến, không bắt buộc phải tạo đúng một
    /// cạnh trực tiếp IO-A &lt;-&gt; IO-B. Một I/O được xem là tác động khi xuất hiện
    /// ở ActiveIo, TargetHits, đầu nguồn có đích, hoặc làm đích của một nguồn.
    /// </summary>
    public static IReadOnlyList<int> GetActiveContactIo(
        ScanFrame frame,
        IReadOnlyList<int> contactIo)
    {
        if (!frame.Complete || frame.UnknownBytes != 0 || contactIo.Count != 2)
            return [];

        return contactIo
            .Where(io => io > 0)
            .Distinct()
            .Where(io =>
                frame.ActiveIo.Contains(io) ||
                frame.TargetHits.ContainsKey(io) ||
                (frame.Connections.TryGetValue(io, out IReadOnlySet<int>? targets) && targets.Count > 0) ||
                frame.Connections.Values.Any(targets => targets.Contains(io)))
            .ToArray();
    }

    public static ScanFrame RemoveDiscardIo(ScanFrame frame, IReadOnlyCollection<int> discardIo)
    {
        if (discardIo.Count == 0)
            return frame;

        HashSet<int> excluded = discardIo.ToHashSet();
        IReadOnlySet<int> active = frame.ActiveIo
            .Where(io => !excluded.Contains(io))
            .ToHashSet();

        var connections = new Dictionary<int, IReadOnlySet<int>>();
        foreach ((int source, IReadOnlySet<int> targets) in frame.Connections)
        {
            if (excluded.Contains(source))
                continue;

            HashSet<int> filteredTargets = targets
                .Where(target => !excluded.Contains(target))
                .ToHashSet();
            if (filteredTargets.Count > 0 || targets.Count == 0)
                connections[source] = filteredTargets;
        }

        IReadOnlyDictionary<int, int> targetHits = frame.TargetHits
            .Where(pair => !excluded.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        return frame with
        {
            ActiveIo = active,
            ConnectionsBySource = connections,
            TargetHitCounts = targetHits,
            SourceCount = connections.Count
        };
    }
}
