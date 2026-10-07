using System.Diagnostics;

namespace OpenFPS.Common;

/// <summary>
/// One monotonic clock for everything that timestamps a sound: the game loop, the audio update and the
/// 250 Hz attribute loop must share a timebase to say how old a position is. Every timestamp that will be
/// subtracted from another comes from here.
///
/// Never a clock something else may restart: the dead reckoning once read a stopwatch the mixer-load
/// report restarted every 250 ms, ages came out negative, and the fix for a close pass stepping in pitch
/// did nothing for most of every quarter second.
/// </summary>
public static class AudioClock
{
    private static readonly Stopwatch _clock = Stopwatch.StartNew();
    private static Func<double>? _test;

    /// <summary>Seconds since the process's audio subsystem started. Monotonic; never reset.</summary>
    public static double Now => _test?.Invoke() ?? _clock.Elapsed.TotalSeconds;

    /// <summary>
    /// For the emitter-stream replay (OpenFPS.Tests): every reader of <see cref="Now"/> reads the test's
    /// clock until the returned handle is disposed. The test assembly runs one test at a time. Goes when
    /// the clock is an instance the host passes in (docs/SOUND_LIBRARY_BOUNDARY.md, stage 5).
    /// </summary>
    internal static IDisposable UseForTest(Func<double> now)
    {
        _test = now;
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => _test = null;
    }
}
