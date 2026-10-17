using System.Diagnostics;

namespace OpenFPS.Common;

/// <summary>
/// The clock of one audio world, which its host gives it (docs/SOUND_LIBRARY_BOUNDARY.md 4.1, stage 5): the
/// game loop, the audio update and the mixer's 250 Hz attribute loop must share a timebase to say how old a
/// position is, so every timestamp that will be subtracted from another comes from one of these. It was a
/// static (AudioClock.Now), one per process whatever the process held. Its world time
/// (<see cref="IWorldClock.WorldTime"/>) is the one the server and every client agree on: the gusts, a lit
/// fire's age, a hob's key. Not a world's state.
/// </summary>
public interface IAudioClock : IWorldClock
{
    /// <summary>Seconds on a monotonic clock that is never reset. Never a clock something else may restart: the
    /// dead reckoning once read a stopwatch the mixer-load report restarted every 250 ms, ages came out
    /// negative, and the fix for a close pass stepping in pitch did nothing for most of every quarter second.</summary>
    double Now { get; }
}

/// <summary>
/// A world's clock as a whole-number stamp, for the holders that keep one in a long and read zero as "never":
/// counted from a day before the clock's own zero, so no real stamp is ever zero or near it, whatever the clock
/// reads (a stepped clock starts at nought). Only ever subtracted from another stamp of the same clock.
/// </summary>
public static class AudioClockStamps
{
    private const double OriginSeconds = 86_400.0;

    /// <summary>Milliseconds on <paramref name="clock"/>.</summary>
    public static long StampMs(this IAudioClock clock) => (long)((clock.Now + OriginSeconds) * 1000.0);

    /// <summary>Ticks of 100 ns on <paramref name="clock"/>.</summary>
    public static long StampTicks(this IAudioClock clock) => (long)((clock.Now + OriginSeconds) * TimeSpan.TicksPerSecond);
}

/// <summary>
/// Real elapsed time, for what waits on real work and not on the world: how long a native scene that a tracer's
/// thread may still hold is kept before it is freed, how long a sound's decoding is given, whether the
/// microphone is still delivering, how long a build took. Never a world's clock: a stepped clock that jumped
/// five seconds would free a scene under a running trace, and one standing still would never free it.
/// Monotonic, where the calendar clock (DateTime.UtcNow) can step.
/// </summary>
public static class RealTime
{
    /// <summary>Milliseconds on a counter that only goes forward.</summary>
    public static long Milliseconds => Environment.TickCount64;

    /// <summary>Ticks of 100 ns on a counter that only goes forward.</summary>
    public static long Ticks => (long)(Stopwatch.GetTimestamp() * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));
}

/// <summary>The clock a running game gives its audio: a stopwatch started when it is made, and the shared
/// world time.</summary>
public sealed class StopwatchAudioClock : IAudioClock
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public double Now => _clock.Elapsed.TotalSeconds;

    public double WorldTime => WindField.Now();
}

/// <summary>A clock its holder steps: an offline render's, or a test's, where every reader must read the
/// same moment on every run.</summary>
public sealed class SteppedAudioClock : IAudioClock
{
    private readonly Func<double>? _now;
    private double _set;
    private double _worldTime = double.NaN;

    /// <summary>A clock set by hand, from zero.</summary>
    public SteppedAudioClock() { }

    /// <summary>A clock that reads its holder's own time.</summary>
    public SteppedAudioClock(Func<double> now) => _now = now;

    public double Now
    {
        get => _now?.Invoke() ?? _set;
        set => _set = value;
    }

    /// <summary>The world time read with it; the shared one unless set.</summary>
    public double WorldTime
    {
        get => double.IsNaN(_worldTime) ? WindField.Now() : _worldTime;
        set => _worldTime = value;
    }
}
