using System.Diagnostics;

namespace OpenFPS.Common;

/// <summary>
/// One monotonic clock for everything that timestamps a sound.
///
/// Three threads have an opinion about when a position was true — the game loop that interpolated it,
/// the audio update that submitted it, and the 250 Hz attribute loop that places it — and until they
/// share a timebase none of them can say how OLD anything is. They cannot each start their own
/// stopwatch either: a Stopwatch measures time since IT started, so two of them differ by whenever
/// their owners happened to be constructed, and subtracting one from the other is nonsense that looks
/// like a number.
///
/// This is process-wide and starts once. <see cref="Now"/> is seconds since then, and the only rule is
/// that every timestamp which will ever be subtracted from another comes from here.
///
/// It is also a warning about reusing a clock for two jobs. The provider's dead reckoning used to read
/// a stopwatch that the mixer-load report RESTARTED every 250 ms, so a position stamped at 0.24 s was
/// compared against a "now" of 0.01 s, the age came out negative, and the reckoning — the entire fix
/// for a close pass stepping in pitch — quietly did nothing for most of every quarter second. A clock
/// that something else is allowed to restart is not a clock.
/// </summary>
public static class AudioClock
{
    private static readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Seconds since the process's audio subsystem started. Monotonic; never reset.</summary>
    public static double Now => _clock.Elapsed.TotalSeconds;
}
