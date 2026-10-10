using System.Globalization;

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
    /// How loud the player's headphones play the game, as the level, dB SPL at their ears, of a normal
    /// voice a metre away as the game plays it at the shipped /levels. The default, 62.35 (ANSI S3.5
    /// normal effort at a metre), is that voice as loud as life: conversational, which is where most
    /// people set headphones for speech in a quiet room. Set by the calibration (/listening) and saved
    /// in ClientSettings. Held to 40..90. It moves no level in the mix, only the tone correction.
    /// </summary>
    public static float ListeningLevelDb
    {
        get => Volatile.Read(ref _listening);
        set => Volatile.Write(ref _listening, Math.Clamp(float.IsFinite(value) ? value : DefaultListeningLevelDb, MinListeningLevelDb, MaxListeningLevelDb));
    }
    private static float _listening = FromEnvironment();

    public const float DefaultListeningLevelDb = ReferenceVoice.NormalDb, MinListeningLevelDb = 40f, MaxListeningLevelDb = 90f;

    private static float FromEnvironment()
        => float.TryParse(Environment.GetEnvironmentVariable("OPENFPS_LISTENING_LEVEL"), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
           ? Math.Clamp(v, MinListeningLevelDb, MaxListeningLevelDb) : DefaultListeningLevelDb;

    /// <summary>Whether the environment chose the listening level for this run, over any saved setting.</summary>
    public static bool ListeningFromEnvironment => Environment.GetEnvironmentVariable("OPENFPS_LISTENING_LEVEL") != null;

    /// <summary>
    /// The level, dB SPL at the ear, of a 0 dB rendered level on the player's headphones: the designed
    /// playback (Loudness.DesignFullScaleDb, about 100.8) moved by how far their listening level is from
    /// the default.
    /// </summary>
    public static float PlaybackFullScaleDb => Loudness.DesignFullScaleDb + (ListeningLevelDb - DefaultListeningLevelDb);

    /// <summary>
    /// The level at the ear, dB SPL, of a voice placed by the law (<paramref name="placedDb"/>, as
    /// Loudness.PlacedDb) whose RMS sits <paramref name="digitalRmsDb"/> under its full scale, at
    /// <paramref name="distance"/> metres with <paramref name="pathDb"/> taken by the way: where the voice
    /// actually plays. Flat inside its reference distance, as the mixer plays it.
    /// </summary>
    public static float PlayedAtEarDb(float placedDb, float digitalRmsDb, float referenceDistance, float distance, float pathDb)
        => PlaybackFullScaleDb + placedDb + digitalRmsDb
           - 20f * MathF.Log10(MathF.Max(MathF.Max(distance, referenceDistance), 0.05f)) + pathDb;

    /// <summary>The level at the ear, dB SPL, of the same source in the real world: its real level at a
    /// metre spread over the distance (from a metre) and through the path.</summary>
    public static float RealAtEarDb(float realLevelDb, float distance, float pathDb)
        => realLevelDb - 20f * MathF.Log10(MathF.Max(1f, distance)) + pathDb;
}
