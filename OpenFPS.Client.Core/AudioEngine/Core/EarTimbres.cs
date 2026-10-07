using System.Collections.Concurrent;
using OpenFPS.Common.Hearing;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// Each sound's measured one-third-octave shape (<see cref="Timbre"/>) by the id it plays under, for
/// the ear model's law and ranking (docs/EAR_MODEL.md). Measured, never authored: a recording or buffer
/// the first time it plays, a live voice from its own output. A sound not measured yet borrows its
/// folder's (the other takes of the same kind); failing that the law treats it as the reference sound.
/// </summary>
public static class EarTimbres
{
    private static readonly ConcurrentDictionary<string, Timbre> _bySound = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Timbre> _byFolder = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The measured timbre of this sound, or of its folder's sounds, or null.</summary>
    public static Timbre? Find(string? soundId)
    {
        if (string.IsNullOrEmpty(soundId)) return null;
        if (_bySound.TryGetValue(soundId, out var t)) return t;
        string folder = Folder(soundId);
        if (folder.Length > 0 && _byFolder.TryGetValue(folder, out var f)) return f;
        // An id that is itself a folder of measured sounds: a model's prefab key ("shore:sea_sand"),
        // whose stretches on a map play under keys that carry their geometry ("shore:sea_sand/300/90/20").
        return _byFolder.TryGetValue(soundId, out var own) ? own : null;
    }

    /// <summary>Whether this exact sound has been measured.</summary>
    public static bool Has(string soundId) => _bySound.ContainsKey(soundId);

    public static void Set(string soundId, Timbre timbre)
    {
        if (string.IsNullOrEmpty(soundId)) return;
        _bySound[soundId] = timbre;
        string folder = Folder(soundId);
        if (folder.Length > 0) _byFolder[folder] = timbre;
    }

    /// <summary>Measures a sound's samples on the thread pool, once per id.</summary>
    public static void MeasureAsync(string soundId, float[] mono, int sampleRate)
    {
        if (string.IsNullOrEmpty(soundId) || mono.Length == 0 || Has(soundId)) return;
        if (!_pending.TryAdd(soundId, 0)) return;
        Task.Run(() =>
        {
            try
            {
                var t = Timbre.FromBandPowers(BandAnalyser.Measure(mono, sampleRate), soundId)?.WithGatedRms(Timbre.GatedRms(mono, sampleRate));
                if (t != null) Set(soundId, t);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning("Ear model: could not measure {Sound}: {Message}", soundId, ex.Message);
            }
        });
    }

    /// <summary>Whether a measurement was asked for (finished or not).</summary>
    public static bool Requested(string soundId) => _pending.ContainsKey(soundId) || Has(soundId);

    /// <summary>The law's correction for this sound's timbre at this level over the unweighted law, dB;
    /// zero with no declared level or no measurement yet.</summary>
    public static float CorrectionDb(string? soundId, float levelDb)
        => levelDb > 0f ? OpenFPS.Common.Loudness.TimbreCorrectionDb(levelDb, Find(soundId)) : 0f;

    private static string Folder(string soundId)
    {
        int slash = Math.Max(soundId.LastIndexOf('/'), soundId.LastIndexOf('\\'));
        return slash > 0 ? soundId[..slash] : "";
    }

    /// <summary>For tests: forget everything.</summary>
    internal static void Clear()
    {
        _bySound.Clear();
        _byFolder.Clear();
        _pending.Clear();
    }
}
