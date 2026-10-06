using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using OpenFPS.Common.Hearing;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// What each sound in the game has been measured to be made of: its one-third-octave spectrum shape
/// (<see cref="Timbre"/>), by the id it is played under. The ear model's law and ranking read it here
/// (docs/EAR_MODEL.md).
///
/// Filled by whoever hears the sound first: the provider measures a recording or a rendered buffer
/// from its samples the first time it plays (off the audio thread), and a live voice (a machine, the
/// fountain, rain) from its own output as it runs. Never authored.
///
/// A sound not measured yet is looked for under its folder (the other takes of the same footstep
/// bank, the other lines of the same voice), which is the same kind of sound; failing that it has
/// none, and the law treats it as the reference sound, which is the unweighted law.
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
        return folder.Length > 0 && _byFolder.TryGetValue(folder, out var f) ? f : null;
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

    /// <summary>
    /// Measures a sound's samples on the thread pool and keeps the result under its id. Once per id:
    /// a second request while one is running, or after it finished, does nothing.
    /// </summary>
    public static void MeasureAsync(string soundId, float[] mono, int sampleRate)
    {
        if (string.IsNullOrEmpty(soundId) || mono.Length == 0 || Has(soundId)) return;
        if (!_pending.TryAdd(soundId, 0)) return;
        Task.Run(() =>
        {
            try
            {
                var t = Timbre.FromBandPowers(BandAnalyser.Measure(mono, sampleRate), soundId);
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

    /// <summary>
    /// The correction the law in loudness units gives a sound of this level and this id over the
    /// unweighted law, dB; zero for a sound with no declared level or no measurement yet.
    /// </summary>
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
