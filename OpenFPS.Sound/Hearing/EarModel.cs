using System.Globalization;

namespace OpenFPS.Sound.Hearing;

/// <summary>
/// The ear model's fixed parts: the range of the playback calibration and where a sound reaches the ear in
/// the real world. Its live switches (whether it is on, the player's listening level) are a world's
/// (<see cref="HearingSettings"/>), so two worlds in one process hear each with its own. See docs/EAR_MODEL.md.
/// </summary>
public static class EarModel
{
    public const float DefaultListeningLevelDb = ReferenceVoice.NormalDb, MinListeningLevelDb = 40f, MaxListeningLevelDb = 90f;

    /// <summary>Whether the environment chose the ear model's switch, OPENFPS_EAR_MODEL=0 or off starting with it off.</summary>
    internal static bool OnFromEnvironmentOrDefault()
        => Environment.GetEnvironmentVariable("OPENFPS_EAR_MODEL") is not ("0" or "off");

    /// <summary>The listening level OPENFPS_LISTENING_LEVEL asks for, held to its range, or the default.</summary>
    internal static float ListeningFromEnvironmentOrDefault()
        => float.TryParse(Environment.GetEnvironmentVariable("OPENFPS_LISTENING_LEVEL"), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
           ? Math.Clamp(v, MinListeningLevelDb, MaxListeningLevelDb) : DefaultListeningLevelDb;

    /// <summary>Whether the environment chose the listening level for this run, over any saved setting.</summary>
    public static bool ListeningFromEnvironment => Environment.GetEnvironmentVariable("OPENFPS_LISTENING_LEVEL") != null;

    /// <summary>The level at the ear, dB SPL, of the same source in the real world: its real level at a
    /// metre spread over the distance (from a metre) and through the path.</summary>
    public static float RealAtEarDb(float realLevelDb, float distance, float pathDb)
        => realLevelDb - 20f * MathF.Log10(MathF.Max(1f, distance)) + pathDb;
}
