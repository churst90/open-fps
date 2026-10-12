namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// The threads that place every sound: the audio update (ClientAudioSystem.Update) and the audio thread
/// (AudioEngineFacade's tick). While either is busy no source moves, so work that can take longer than a
/// frame (a model calibrating itself, a train's model, the routes through a map's openings, a room's reverb
/// stage, a cabin's trace, a cabin path's trim, another room's field) is done elsewhere (VoiceBuilds,
/// TracedStages, TracedReverbSet). A train's first horn once held every sound still for 0.8 s this way, and
/// getting into a car up to 0.55 s.
/// </summary>
public static class PlacementThread
{
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static int _heavyHere;
    [ThreadStatic] private static string? _lastHeavyHere;

    private static int _heavy;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Logged = new();

    /// <summary>Whether the calling thread is placing sounds now.</summary>
    public static bool IsCurrent => _depth > 0;

    /// <summary>Marks the calling thread as placing sounds until the scope is disposed.</summary>
    public static Scope Enter()
    {
        _depth++;
        return default;
    }

    public readonly struct Scope : IDisposable
    {
        public void Dispose() => _depth--;
    }

    /// <summary>
    /// Says that work which can take longer than a frame is starting. On a placement thread it is a fault:
    /// counted, and logged once for each kind of work, so a regression shows in the log and in the guard
    /// test (PlacementThreadTests) rather than as sounds standing still.
    /// </summary>
    public static void Heavy(string what)
    {
        if (_depth == 0) return;
        _heavyHere++;
        _lastHeavyHere = what;
        Interlocked.Increment(ref _heavy);
        if (Logged.TryAdd(what, 0))
            Serilog.Log.Warning("{What} was made on a thread that places sounds: every source held still until it was done.", what);
    }

    /// <summary>Heavy work started on a placement thread, by any thread, since the client started.</summary>
    public static int HeavyCount => Volatile.Read(ref _heavy);

    /// <summary>Heavy work started while the calling thread was placing sounds, and the last of it (tests).</summary>
    public static (int Count, string? Last) HeavyOnThisThread => (_heavyHere, _lastHeavyHere);
}
