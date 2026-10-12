using System.Diagnostics;

namespace OpenFPS.Common;

/// <summary>
/// The clock of one audio world, which its host gives it (docs/SOUND_LIBRARY_BOUNDARY.md 4.1, stage 5): the
/// game loop, the audio update and the mixer's 250 Hz attribute loop must share a timebase to say how old a
/// position is, so every timestamp that will be subtracted from another comes from one of these. It was a
/// static (AudioClock.Now), one per process whatever the process held.
/// </summary>
public interface IAudioClock
{
    /// <summary>Seconds on a monotonic clock that is never reset. Never a clock something else may restart: the
    /// dead reckoning once read a stopwatch the mixer-load report restarted every 250 ms, ages came out
    /// negative, and the fix for a close pass stepping in pitch did nothing for most of every quarter second.</summary>
    double Now { get; }

    /// <summary>The one world time the server and every client agree on, s (WindField.Now: UTC seconds since
    /// 2026-01-01): the gusts, a lit fire's age, a hob's key. Not a world's state.</summary>
    double WorldTime { get; }
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
