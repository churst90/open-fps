using System;
using System.Globalization;
using System.Threading;

namespace OpenFPS.Common.Hearing;

/// <summary>
/// The ear model's live switches and the playback calibration: what the law, the engine lift and the
/// loudness compensation read. See docs/EAR_MODEL.md.
///
/// Static, like <see cref="Loudness.DynamicRangeCompression"/> and for the same reason: every voice and
/// every head reads one value, and the client's settings write it.
/// </summary>
public static class EarModel
{
    /// <summary>
    /// The whole model on or off: the law's loudness correction, the engine lift's tone term and the
    /// compensation. Off is exactly the game before 2026-10-06 (the unweighted law and lift, with the
    /// lift's old 20 dB cap). OPENFPS_EAR_MODEL=0 starts with it off; /ear switches it.
    /// </summary>
    public static bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }
    private static int _enabled = Environment.GetEnvironmentVariable("OPENFPS_EAR_MODEL") is "0" or "off" ? 0 : 1;

    /// <summary>
    /// The level, dB SPL at the player's ears, at which a sound the law places at the pivot reaches
    /// them: how loud their headphones play the game. 70, the default, is the pivot playing at its own
    /// level, so a talker at normal effort a metre away plays at about 62 dB, conversational. Set by the
    /// calibration (/listening) and saved in ClientSettings. Held to 40..100.
    /// </summary>
    public static float ListeningLevelDb
    {
        get => Volatile.Read(ref _listening);
        set => Volatile.Write(ref _listening, Math.Clamp(float.IsFinite(value) ? value : DefaultListeningLevelDb, MinListeningLevelDb, MaxListeningLevelDb));
    }
    private static float _listening = FromEnvironment();

    public const float DefaultListeningLevelDb = 70f, MinListeningLevelDb = 40f, MaxListeningLevelDb = 100f;

    private static float FromEnvironment()
        => float.TryParse(Environment.GetEnvironmentVariable("OPENFPS_LISTENING_LEVEL"), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
           ? Math.Clamp(v, MinListeningLevelDb, MaxListeningLevelDb) : DefaultListeningLevelDb;

    /// <summary>Whether the environment chose the listening level for this run, over any saved setting.</summary>
    public static bool ListeningFromEnvironment => Environment.GetEnvironmentVariable("OPENFPS_LISTENING_LEVEL") != null;

    /// <summary>
    /// The level, dB SPL, the law means a 0 dB rendered source of the reference sound to play at: the
    /// level that makes the pivot play at its own level. 70 - c (70 - C), 88.9 dB at 45 %. The law is
    /// worked out at this nominal playback; the player's real playback only changes the compensation.
    /// </summary>
    public static float NominalFullScaleDb => Loudness.PivotDb - Loudness.PivotRenderedDb;

    /// <summary>How much louder than the law's nominal playback the player's headphones actually play, dB.</summary>
    public static float PlaybackOffsetDb => ListeningLevelDb - Loudness.PivotDb;

    /// <summary>
    /// The level at the ear, dB SPL, of a source placed by the law, at <paramref name="distance"/> metres
    /// with <paramref name="pathDb"/> taken by the way there: where the voice actually plays. Flat inside
    /// its reference distance, as the mixer plays it.
    /// </summary>
    public static float PlayedAtEarDb(float placedDb, float referenceDistance, float distance, float pathDb)
        => NominalFullScaleDb + PlaybackOffsetDb + placedDb
           - 20f * MathF.Log10(MathF.Max(MathF.Max(distance, referenceDistance), 0.05f)) + pathDb;

    /// <summary>The level at the ear, dB SPL, of the same source in the real world: its level at a metre
    /// spread over the distance (from a metre) and through the path.</summary>
    public static float RealAtEarDb(float sourceLevelDb, float distance, float pathDb)
        => sourceLevelDb - 20f * MathF.Log10(MathF.Max(1f, distance)) + pathDb;
}
