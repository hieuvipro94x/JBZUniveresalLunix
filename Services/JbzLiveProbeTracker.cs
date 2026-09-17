namespace JBZUniveresalLunix.Services;

/// <summary>Physical TESTPIN contacts, newest contact first.</summary>
public sealed class JbzLiveProbeTracker
{
    private readonly Dictionary<int, long> _active = [];
    private readonly object _gate = new();
    private long _order;

    public IReadOnlyList<int> ActivePins
    {
        get
        {
            lock (_gate)
                return _active.OrderByDescending(entry => entry.Value)
                    .Select(entry => entry.Key).ToArray();
        }
    }

    public bool Update(int physicalPin, bool on)
    {
        if (physicalPin <= 0) return false;
        lock (_gate)
        {
            if (on)
            {
                if (_active.ContainsKey(physicalPin)) return false;
                _active.Add(physicalPin, ++_order);
                return true;
            }
            return _active.Remove(physicalPin);
        }
    }

    public bool Clear()
    {
        lock (_gate)
        {
            if (_active.Count == 0) return false;
            _active.Clear();
            return true;
        }
    }
}
