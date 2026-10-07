namespace OpenFPS.Common;

/// <summary>
/// A fixed-rate gate: has enough time passed to do this again? The client's loop polls the network at
/// about 200 Hz, and the audio update ran there, three times more often than anything in it can be heard,
/// on the socket's thread; this holds it to 60 Hz. The clock is passed in so tests need not sleep.
/// </summary>
public sealed class UpdateThrottle
{
    private double _nextDueSeconds = double.NegativeInfinity;

    /// <summary>The minimum gap between two accepted runs.</summary>
    public double IntervalSeconds { get; }

    /// <summary>Accepted runs so far. Read by tests and by the perf report.</summary>
    public long Runs { get; private set; }

    /// <summary>Calls that were turned away because they came too soon.</summary>
    public long Skipped { get; private set; }

    public UpdateThrottle(double hz)
    {
        if (hz <= 0) throw new ArgumentOutOfRangeException(nameof(hz), "Update rate must be positive.");
        IntervalSeconds = 1.0 / hz;
    }

    /// <summary>
    /// True when <paramref name="nowSeconds"/> is at or past the next due time, re-arming one interval from
    /// now rather than from the due time: after a stall there is nothing to catch up on.
    /// </summary>
    public bool ShouldRun(double nowSeconds)
    {
        if (nowSeconds < _nextDueSeconds)
        {
            Skipped++;
            return false;
        }

        _nextDueSeconds = nowSeconds + IntervalSeconds;
        Runs++;
        return true;
    }

    /// <summary>Makes the next <see cref="ShouldRun"/> succeed whatever the clock says.</summary>
    public void Arm() => _nextDueSeconds = double.NegativeInfinity;
}
