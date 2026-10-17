using System.Collections.Concurrent;

namespace OpenFPS.Sound;

/// <summary>
/// The model ids that did not resolve, each logged once. Below the catalogue, so a sound model that cannot
/// make what an id names (a horn of no kind, a cabin door with no port) says so here as the kind table
/// does (ModelKindTable.Fail, which writes here).
/// </summary>
public static class ModelFailures
{
    /// <summary>Logs, once per id, that a model id does not resolve. Never thrown: a voice that cannot be
    /// made is not made, and the log says which.</summary>
    public static void Fail(string soundId, string what)
    {
        if (_failed.TryAdd(soundId, what))
            Serilog.Log.Warning("Model table: '{SoundId}' {What}", soundId, what);
    }

    private static readonly ConcurrentDictionary<string, string> _failed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ids logged so far, with why.</summary>
    public static IReadOnlyDictionary<string, string> All => _failed;
}
