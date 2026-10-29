namespace GalDanmaku.Core;

public sealed class SimulationCadence
{
    private const double IntervalSeconds = 0.9;
    private double _untilNext;

    public bool Advance(double elapsedSeconds)
    {
        DanmakuEngine.ValidateNonNegativeFinite(elapsedSeconds, nameof(elapsedSeconds));
        _untilNext -= elapsedSeconds;
        if (_untilNext > 0)
        {
            return false;
        }

        // Skip missed slots rather than flooding the screen after a stalled frame.
        _untilNext = IntervalSeconds;
        return true;
    }

    public void Reset() => _untilNext = 0;
}
