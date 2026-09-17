namespace JBZUniveresalLunix.Services;

/// <summary>
/// Rejects callbacks from an older runtime, product cycle, D2XX scan session or
/// complete frame. A newer revision of the same frame remains valid because
/// confirmation timers can change the engine result without changing topology.
/// </summary>
public sealed class ProductionUiVersionGate
{
    private readonly object _gate = new();
    private long _runtimeGeneration = -1;
    private long _cycleEpoch = -1;
    private long _scanGeneration = -1;
    private long _frameSequence = -1;
    private long _revision = -1;

    public bool TryAccept(
        long runtimeGeneration,
        long cycleEpoch,
        long scanGeneration,
        long frameSequence,
        long revision)
    {
        lock (_gate)
        {
            if (runtimeGeneration < _runtimeGeneration ||
                (runtimeGeneration == _runtimeGeneration && cycleEpoch < _cycleEpoch))
            {
                return false;
            }

            bool newContext = runtimeGeneration > _runtimeGeneration ||
                              cycleEpoch > _cycleEpoch;
            if (!newContext)
            {
                if (scanGeneration > 0 && _scanGeneration > 0)
                {
                    if (scanGeneration < _scanGeneration)
                        return false;
                    if (scanGeneration == _scanGeneration &&
                        frameSequence > 0 && _frameSequence > 0 &&
                        frameSequence < _frameSequence)
                    {
                        return false;
                    }
                }
                else if (frameSequence > 0 && _frameSequence > 0 &&
                         frameSequence < _frameSequence)
                {
                    return false;
                }

                if (scanGeneration == _scanGeneration &&
                    frameSequence == _frameSequence &&
                    revision <= _revision)
                {
                    return false;
                }
            }

            _runtimeGeneration = runtimeGeneration;
            _cycleEpoch = cycleEpoch;
            _scanGeneration = scanGeneration;
            _frameSequence = frameSequence;
            _revision = revision;
            return true;
        }
    }
}
