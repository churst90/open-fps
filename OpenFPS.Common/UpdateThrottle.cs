using System;

namespace OpenFPS.Common;

/// <summary>
/// A fixed-rate gate: "has enough time passed to do this again?"
///
/// The client's game loop runs as fast as it can poll the network — a sleep of 5 ms between iterations, so
/// roughly 200 Hz — and it drove the whole audio update from that loop. Occlusion queries, listener sync,
/// reverb routing and the FMOD tick therefore ran three times more often than any of it can be heard, on the
/// same thread that has to service the socket. Nothing in the audio path resolves faster than a frame at
/// 60 Hz, so this caps it there and hands the rest of the loop back to the network.
///
/// The clock is passed in rather than read here so the rule is testable without sleeping.
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
    /// True when <paramref name="nowSeconds"/> is at or past the next due time, in which case the gate is
    /// re-armed for one interval from NOW (not from the due time). Re-arming from now means a loop that
    /// stalls does not then burn several catch-up updates in a row to "repay" the gap — for audio there is
    /// nothing to repay, the missed frames are simply gone.
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
