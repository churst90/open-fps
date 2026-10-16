using System.Threading;

namespace OpenFPS.Common.Hearing;

/// <summary>
/// How one audio world is heard: the loudness law's compression (`/levels`), whether the ear model is on
/// (`/ear`), and how loud the player's headphones play (`/listening`). Every placement of that world goes
/// through it (<see cref="Place(float)"/> and its kin), so a voice, a head and the mixer of one world read one
/// value, and two worlds in one process (a test beside a session, a lab comparing two settings) hear each
/// with its own.
///
/// Who holds it: the mixer (FmodAudioProvider.Hearing), and the host reaches the same one through
/// AudioEngineFacade.Hearing. open-fps's client puts its saved settings into it, and its `/levels`,
/// `/ear` and `/listening` change it. A new one is the shipped hearing; <see cref="FromEnvironment"/> is
/// that, unless a run's environment says otherwise.
/// </summary>
public sealed class HearingSettings
{
    /// <summary>
    /// How much of the real decibel difference survives into the mix: 1.0 is literal physics; at 0.45
    /// the 110 dB between a rifle and an ambience bed is about 50 dB rendered. Source levels only:
    /// distance stays literal (1/r), because it carries the range cues. At 0.45 a hot rod 19 dB louder
    /// than an economy car is placed 9 dB louder, and the engine's idle lift follows the same number.
    /// Held to <see cref="Loudness.MinCompression"/>..1.
    /// </summary>
    public float Compression
    {
        get => Volatile.Read(ref _compression);
        set => Volatile.Write(ref _compression, Math.Clamp(value, Loudness.MinCompression, 1f));
    }
    private float _compression = Loudness.DefaultCompression;

    /// <summary>
    /// The whole ear model on or off: the law's loudness correction, the engine lift's tone term and the
    /// compensation. Off is exactly the game before 2026-10-06 (the unweighted law and lift, with the
    /// lift's old 20 dB cap).
    /// </summary>
    public bool EarModelOn
    {
        get => Volatile.Read(ref _earModel) != 0;
        set => Volatile.Write(ref _earModel, value ? 1 : 0);
    }
    private int _earModel = 1;

    /// <summary>
    /// How loud the player's headphones play the game, as the level, dB SPL at their ears, of a normal
    /// voice a metre away as the game plays it at the shipped /levels. The default, 62.35 (ANSI S3.5
    /// normal effort at a metre), is that voice as loud as life: conversational, which is where most
    /// people set headphones for speech in a quiet room. Held to 40..90. It moves no level in the mix, only
    /// the tone correction.
    /// </summary>
    public float ListeningLevelDb
    {
        get => Volatile.Read(ref _listening);
        set => Volatile.Write(ref _listening, Math.Clamp(float.IsFinite(value) ? value : EarModel.DefaultListeningLevelDb,
                                                         EarModel.MinListeningLevelDb, EarModel.MaxListeningLevelDb));
    }
    private float _listening = EarModel.DefaultListeningLevelDb;

    /// <summary>The ranking as it was before 2026-10-07, on the played gain: for an instrument's before and
    /// after (RankingByLoudnessProbe). Nothing in the game sets it.</summary>
    public bool RankOnPlayedGain { get; set; }

    /// <summary>The shipped hearing, unless the run's environment chose any of it: OPENFPS_LEVEL_COMPRESSION,
    /// OPENFPS_EAR_MODEL=0 or off, OPENFPS_LISTENING_LEVEL. What the game's mixer starts with.</summary>
    public static HearingSettings FromEnvironment() => new()
    {
        Compression = Loudness.CompressionFromEnvironmentOrDefault(),
        EarModelOn = EarModel.OnFromEnvironmentOrDefault(),
        ListeningLevelDb = EarModel.ListeningFromEnvironmentOrDefault(),
    };

    // ── The law, as this world hears it (Loudness) ──────────────────────────────────────────────

    /// <summary>The level at the ear that plays at full scale, for this world's compression.</summary>
    public float RenderCeilingDb => Loudness.RenderCeilingDbAt(Compression);

    /// <summary>Where to put a sound of a given source level: gain and reference distance
    /// (<see cref="Loudness.PlaceAt(float, float)"/>).</summary>
    public (float Gain, float ReferenceDistance) Place(float sourceLevelDb) => Loudness.PlaceAt(sourceLevelDb, Compression);

    /// <summary>The same placement for a source with a size: the reference distance is its radius, the gain
    /// paid down for it (<see cref="Loudness.Widen((float Gain, float ReferenceDistance), float)"/>).</summary>
    public (float Gain, float ReferenceDistance) Place(float sourceLevelDb, float extentMetres)
        => Loudness.Widen(Place(sourceLevelDb), extentMetres);

    /// <summary>Where the law puts a source of this level and this spectrum (<see cref="Loudness.PlaceAt(float, Timbre, float, bool)"/>):
    /// a null timbre, the reference (speech), or the ear model off is the unweighted law exactly.</summary>
    public (float Gain, float ReferenceDistance) Place(float sourceLevelDb, Timbre? timbre)
        => Loudness.PlaceAt(sourceLevelDb, timbre, Compression, EarModelOn);

    /// <summary>The loudness-unit placement, widened for a source with a size.</summary>
    public (float Gain, float ReferenceDistance) Place(float sourceLevelDb, Timbre? timbre, float extentMetres)
        => Loudness.Widen(Place(sourceLevelDb, timbre), extentMetres);

    /// <summary>Where the law puts a source of this level, as one number (<see cref="Loudness.PlacedDb"/>).</summary>
    public float PlacedDb(float sourceLevelDb) => Loudness.PlacedDb(Place(sourceLevelDb));

    /// <summary><see cref="PlacedDb(float)"/> for a source of this spectrum.</summary>
    public float PlacedDb(float sourceLevelDb, Timbre? timbre) => Loudness.PlacedDb(Place(sourceLevelDb, timbre));

    /// <summary>
    /// How much more the law in loudness units gives a source than the unweighted law, dB: the same at
    /// every distance beyond the reference, so one gain puts an unweighted placement right.
    /// </summary>
    public float TimbreCorrectionDb(float sourceLevelDb, Timbre? timbre)
        => timbre == null || timbre.IsReference || !EarModelOn ? 0f
         : PlacedDb(sourceLevelDb, timbre) - PlacedDb(sourceLevelDb);

    /// <summary>How loud a voice is to the ear, for ranking (<see cref="Loudness.HeardGainAt"/>).</summary>
    public float HeardGain(float playedGain, Timbre? timbre, bool physical)
        => Loudness.HeardGainAt(playedGain, timbre, physical, byEar: EarModelOn && !RankOnPlayedGain);

    /// <summary>Just the gain, for a caller that is setting the reference distance itself.</summary>
    public float GainFor(float sourceLevelDb) => Place(sourceLevelDb).Gain;

    /// <summary>The distance at which a source of this level drops to the quietest thing worth rendering
    /// (<see cref="Loudness.AudibleRange"/>).</summary>
    public float AudibleRange(float sourceLevelDb) => Loudness.AudibleRange(sourceLevelDb, Compression);

    // ── The ear (EarModel) ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The level, dB SPL at the ear, of a 0 dB rendered level on the player's headphones: the designed
    /// playback (Loudness.DesignFullScaleDb, about 100.8) moved by how far their listening level is from
    /// the default.
    /// </summary>
    public float PlaybackFullScaleDb => Loudness.DesignFullScaleDb + (ListeningLevelDb - EarModel.DefaultListeningLevelDb);

    /// <summary>
    /// The level at the ear, dB SPL, of a voice placed by the law (<paramref name="placedDb"/>, as
    /// <see cref="PlacedDb(float)"/>) whose RMS sits <paramref name="digitalRmsDb"/> under its full scale, at
    /// <paramref name="distance"/> metres with <paramref name="pathDb"/> taken by the way: where the voice
    /// actually plays. Flat inside its reference distance, as the mixer plays it.
    /// </summary>
    public float PlayedAtEarDb(float placedDb, float digitalRmsDb, float referenceDistance, float distance, float pathDb)
        => PlaybackFullScaleDb + placedDb + digitalRmsDb
           - 20f * MathF.Log10(MathF.Max(MathF.Max(distance, referenceDistance), 0.05f)) + pathDb;
}
