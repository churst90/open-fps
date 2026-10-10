namespace OpenFPS.Common;

/// <summary>
/// How loud a thing is at its source, in decibels, and what that becomes in the mix. Assets are
/// normalised to one peak, so by volume alone a gunshot at ten metres rendered 14 dB quieter than a
/// footstep at one; in air they differ by about 85 dB the other way. So a sound says how loud it is
/// where it is made, and the engine works out the rest.
///
/// The real range, about 115 dB from a muzzle blast at 165 to a quiet ambience at 48, does not fit in
/// 16-bit audio or a room, so source levels are compressed (<see cref="DynamicRangeCompression"/>) on
/// purpose: order and rough spacing survive, the literal ratio does not. The levels below are published
/// measurements where they exist (firearms, for hearing protection), a starting point for the ear.
/// </summary>
public static class Loudness
{
    // ── Source levels, dB SPL at one metre ──────────────────────────────────────────────────────
    //
    // Firearms are quoted at the shooter's position, where the hearing-protection figures are measured.

    /// <summary>5.56x45 from a 16-inch barrel: the reference the mix was set against.</summary>
    public const float Rifle556Db = 165f;
    /// <summary>7.62x39: lower than the 5.56 but with far more low frequency, so it carries further
    /// through walls.</summary>
    public const float Rifle762Db = 159f;
    public const float Pistol9mmDb = 160f;
    public const float Pistol45Db = 157f;
    /// <summary>.357 Magnum from a 6-inch revolver, about 164 dB: a bigger charge than the 9 mm, and the
    /// cylinder gap vents beside the hand as well as at the muzzle.</summary>
    public const float Magnum357Db = 164f;
    public const float Shotgun12GaugeDb = 160f;
    /// <summary>.308 Winchester from a 24-inch bolt gun: about 167 dB in the published hearing-protection
    /// tables, two above the 5.56 for nearly twice the powder.</summary>
    public const float Rifle308Db = 167f;

    /// <summary>A case bouncing on concrete. Quiet, close, and very informative.</summary>
    public const float CasingDb = 75f;

    /// <summary>
    /// Your own footfall at one metre, derived through the impact constant
    /// (<see cref="PanelAcoustics.ImpactReferenceDb"/>: one joule is 74 dB at a metre): about ten
    /// kilograms of foot and shank at under a metre a second is a couple of joules, 78 dB, less the ten
    /// a sole and a floor take (<see cref="Footsteps"/>). Not 55, a trainer on carpet: that put a step
    /// twenty decibels under a spent case (<see cref="CasingDb"/>) and a walk at -26 LUFS against the
    /// -18 to -23 a game mix belongs at.
    /// </summary>
    public const float FootstepDb = 68f;

    /// <summary>
    /// The level the compression turns about, dB: a sound 70 dB at its reference distance plays at the
    /// same level at every /levels setting, and the level that reaches full scale follows (112 dB at the
    /// shipped 0.45, about 89 at 1.0). A fixed ceiling made "real" put a street scene 40 dB down.
    /// </summary>
    // The ceiling is not 130 dB, where only gunfire reaches full scale: a door slam at 88 dB rendered
    // at -28 dBFS and glass across a street at -41, weak and dull. Everyday sounds are 60 to 95 dB and
    // the mix spends itself there; gunfire clips, as it does an ear. Nor is it a source level (165):
    // a gunshot would be full scale only at the muzzle (docs/COMMON_NOTES.md, Loudness).
    public const float PivotDb = 70f;
    private const float ShippedCeilingDb = 112f;

    /// <summary>Where a <see cref="PivotDb"/> sound plays, dBFS at its reference: the level the shipped
    /// mix gave it, held whatever the compression.</summary>
    public static float PivotRenderedDb => (PivotDb - ShippedCeilingDb) * DefaultCompression;

    /// <summary>The level at the ear that plays at full scale, for the compression in force.</summary>
    public static float RenderCeilingDb => PivotDb - PivotRenderedDb / DynamicRangeCompression;

    /// <summary>
    /// How much of the real decibel difference survives into the mix: 1.0 is literal physics; at 0.45
    /// the 110 dB between a rifle and an ambience bed is about 50 dB rendered. Source levels only:
    /// distance stays literal (1/r), because it carries the range cues. At 0.45 a hot rod 19 dB louder
    /// than an economy car is placed 9 dB louder, and the engine's idle lift follows the same number.
    /// Set by the client (`/levels`, ClientSettings), OPENFPS_LEVEL_COMPRESSION for a run; held to 0.2..1.
    /// </summary>
    public static float DynamicRangeCompression
    {
        get => System.Threading.Volatile.Read(ref _compression);
        set => System.Threading.Volatile.Write(ref _compression, Math.Clamp(value, MinCompression, 1f));
    }

    /// <summary>What the game shipped with, and what a player who has chosen nothing gets.</summary>
    public const float DefaultCompression = 0.45f;
    public const float MinCompression = 0.2f;
    private static float _compression = FromEnvironment();

    private static float FromEnvironment()
        => float.TryParse(Environment.GetEnvironmentVariable("OPENFPS_LEVEL_COMPRESSION"),
                          System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)
           ? Math.Clamp(v, MinCompression, 1f) : DefaultCompression;

    /// <summary>Whether the environment chose the compression for this run, over any saved setting.</summary>
    public static bool CompressionFromEnvironment => Environment.GetEnvironmentVariable("OPENFPS_LEVEL_COMPRESSION") != null;

    /// <summary>Below this the engine should not bother playing the voice at all.</summary>
    public const float SilenceGain = 0.0002f;   // about -74 dBFS

    /// <summary>Largest reference distance handed out: past this a source is no longer a point.</summary>
    public const float MaxReferenceDistance = 40f;
    /// <summary>Smallest reference distance: nothing is heard from closer than about a metre, and a
    /// floor of 0.5 m had a quiet source six decibels down a metre from it.</summary>
    public const float MinReferenceDistance = 1.2f;

    /// <summary>
    /// Where the law puts a source of this level, as one number: 20 log10 of gain times reference
    /// distance. Two levels' difference in it is how far apart the mix places them (the engine's idle lift).
    /// </summary>
    public static float PlacedDb(float sourceLevelDb)
    {
        var (gain, reference) = Place(sourceLevelDb);
        return 20f * MathF.Log10(MathF.Max(1e-9f, gain * reference));
    }

    /// <summary>
    /// Where to put a sound of a given source level: its gain and the reference distance inside which
    /// it gets no louder, decided together. The reference is where the source's SPL falls to
    /// <see cref="RenderCeilingDb"/>: a 159 dB rifle plays at full scale out to about twenty-eight
    /// metres (with a fixed 2 m reference it was a fifteenth by thirty), and a footstep clamps to
    /// <see cref="MinReferenceDistance"/> and takes its quietness from the gain.
    /// </summary>
    public static (float Gain, float ReferenceDistance) Place(float sourceLevelDb)
    {
        float ceiling = RenderCeilingDb;
        float ideal = MathF.Pow(10f, (sourceLevelDb - ceiling) / 20f);
        float reference = Math.Clamp(ideal, MinReferenceDistance, MaxReferenceDistance);

        // What is left after the clamp, compressed: unity for a loud source, all its quietness for a quiet one.
        float atReference = sourceLevelDb - 20f * MathF.Log10(MathF.Max(0.01f, reference));
        float renderedDb = (atReference - ceiling) * DynamicRangeCompression;
        return (MathF.Min(1f, MathF.Pow(10f, renderedDb / 20f)), reference);
    }

    // ── The law in loudness units (docs/EAR_MODEL.md) ─────────────────────────────────────────
    //
    // The ear does not hear unweighted level: a pure tone is fifteen phon quieter than speech of the
    // same level. A source is turned into the speech line just as loud (ISO 532-1, its own measured
    // spectrum), the law above places that line, and the source plays as loud as the line plays.
    //
    // Two conventions of declared level: a recording or render declares its buffer's full scale at a
    // metre (ReferenceVoice.LevelDb: a line sits 28 dB under it), a physical voice its RMS, played
    // PeakHeadroomDb under full scale. Timbre.RealOffsetDb and DigitalRmsDb carry which.

    /// <summary>Where a physical voice's RMS sits under full scale, dB (VehicleProfile.PeakHeadroomDb).</summary>
    public const float PhysicalRmsDbfs = -VehicleProfile.PeakHeadroomDb;

    /// <summary>Where the reference sound, a speech line, sits under its full scale (Hearing.ReferenceVoice.BufferRmsDbfs).</summary>
    public const float ReferenceRmsDbfs = Hearing.ReferenceVoice.BufferRmsDbfs;

    /// <summary>
    /// The designed playback, dB SPL at the ear for 0 dB rendered: where a normal voice at a metre (ANSI
    /// S3.5, 62.35 dB) plays as loud as it is at the shipped /levels, about 100.8. The player's own
    /// playback (Hearing.EarModel.ListeningLevelDb) moves only the tone correction.
    /// </summary>
    public static readonly float DesignFullScaleDb = DesignFullScale();

    private static float DesignFullScale()
    {
        float declared = Hearing.ReferenceVoice.LevelDb(Hearing.ReferenceVoice.NormalDb);
        float reference = Math.Clamp(MathF.Pow(10f, (declared - ShippedCeilingDb) / 20f), MinReferenceDistance, MaxReferenceDistance);
        float rendered = (declared - 20f * MathF.Log10(reference) - ShippedCeilingDb) * DefaultCompression;
        return Hearing.ReferenceVoice.NormalDb - rendered - ReferenceRmsDbfs;
    }

    /// <summary>
    /// Where the law puts a source of this level and this spectrum: gain and reference distance, as
    /// <see cref="Place(float)"/>. A null timbre, the reference (speech), or the ear model switched off
    /// is the unweighted law exactly.
    /// </summary>
    public static (float Gain, float ReferenceDistance) Place(float sourceLevelDb, Hearing.Timbre? timbre)
    {
        if (timbre == null || timbre.IsReference || !Hearing.EarModel.Enabled) return Place(sourceLevelDb);
        return timbre.Placed(sourceLevelDb, DynamicRangeCompression, PlaceHeard);
    }

    /// <summary>The loudness-unit placement, widened for a source with a size (see <see cref="Place(float, float)"/>).</summary>
    public static (float Gain, float ReferenceDistance) Place(float sourceLevelDb, Hearing.Timbre? timbre, float extentMetres)
        => Widen(Place(sourceLevelDb, timbre), extentMetres);

    /// <summary><see cref="PlacedDb(float)"/> for a source of this spectrum.</summary>
    public static float PlacedDb(float sourceLevelDb, Hearing.Timbre? timbre)
    {
        var (gain, reference) = Place(sourceLevelDb, timbre);
        return 20f * MathF.Log10(MathF.Max(1e-9f, gain * reference));
    }

    /// <summary>
    /// How much more the law in loudness units gives a source than the unweighted law, dB: the same at
    /// every distance beyond the reference, so one gain puts an unweighted placement right.
    /// </summary>
    public static float TimbreCorrectionDb(float sourceLevelDb, Hearing.Timbre? timbre)
        => timbre == null || timbre.IsReference || !Hearing.EarModel.Enabled ? 0f
         : PlacedDb(sourceLevelDb, timbre) - PlacedDb(sourceLevelDb);

    /// <summary>The ranking as it was before 2026-10-07, on the played gain: for an instrument's before and
    /// after (RankingByLoudnessProbe). Nothing in the game sets it.</summary>
    public static bool RankOnPlayedGain { get; set; }

    /// <summary>
    /// How loud a voice is to the ear, for ranking (docs/EAR_MODEL.md, Ranking): a voice playing at
    /// <paramref name="playedGain"/>, of this spectrum, as the gain at which a speech line would be as
    /// loud (<see cref="Hearing.Timbre.HeardPhons"/>, at the designed playback). A speech line or an
    /// unmeasured recording ranks at its played gain; an unmeasured physical voice is taken as
    /// speech-shaped. Not the compensated gain: a 65 dB rumble at 25 Hz is played about 21 dB above
    /// speech to be heard at its real loudness, and ranking on that put it 21 dB up the list.
    /// </summary>
    public static float HeardGain(float playedGain, Hearing.Timbre? timbre, bool physical)
    {
        if (!(playedGain > 0f)) return 0f;
        if (!Hearing.EarModel.Enabled || RankOnPlayedGain) return playedGain;
        // The reference itself: exactly its own gain.
        if (timbre?.IsReference ?? !physical) return playedGain;
        var t = timbre ?? Hearing.Timbre.Speech;
        float digital = timbre?.DigitalRmsDb ?? (physical ? PhysicalRmsDbfs : ReferenceRmsDbfs);
        float atEar = DesignFullScaleDb + 20f * MathF.Log10(playedGain) + digital;
        float speechAtEar = Hearing.Timbre.Speech.LevelForHeardPhons(t.HeardPhons(atEar));
        return MathF.Pow(10f, (speechAtEar - DesignFullScaleDb - ReferenceRmsDbfs) / 20f);
    }

    private static (float Gain, float ReferenceDistance) PlaceHeard(float sourceLevelDb, Hearing.Timbre timbre, float compression)
    {
        var speech = Hearing.Timbre.Speech;
        float ceiling = PivotDb - PivotRenderedDb / compression;           // the old law's ceiling, declared
        float fullScale = DesignFullScaleDb;
        float real = timbre.RealOffsetDb, digital = timbre.DigitalRmsDb;
        // Declared level of this source as loud as a speech line declared at the ceiling: where it
        // reaches the ceiling, and so its reference distance.
        float ceilingHere = timbre.LevelForPhons(speech.Phons(ceiling + ReferenceRmsDbfs)) - real;
        float ideal = MathF.Pow(10f, (sourceLevelDb - ceilingHere) / 20f);
        float reference = Math.Clamp(ideal, MinReferenceDistance, MaxReferenceDistance);
        float atReference = sourceLevelDb - 20f * MathF.Log10(MathF.Max(0.01f, reference));
        // The speech line just as loud, as the line would be declared...
        float line = speech.LevelForPhons(timbre.Phons(atReference + real)) - ReferenceRmsDbfs;
        // ...where the old law plays that line (its RMS, at the designed playback)...
        float linePlayed = fullScale + (line - ceiling) * compression + ReferenceRmsDbfs;
        // ...and this source as loud as that, back in its own digital units.
        float played = timbre.LevelForPhons(speech.Phons(linePlayed));
        float renderedDb = played - fullScale - digital;
        // Not held to 1, as the unweighted gain is: a sound the ear hears less of per decibel (an idling
        // engine, a pure tone) needs more than speech's full scale at the ceiling, and holding it would
        // flatten every such source above the pivot to one level.
        return (MathF.Pow(10f, renderedDb / 20f), reference);
    }

    /// <summary>
    /// The same placement for a source with a size (a crowd, a waterfall, a motorway): the level is flat
    /// across it, so the reference distance is its radius. The gain comes down to pay for it: beyond the
    /// patch a distributed source and a point of the same power are identical, so the product of gain and
    /// reference distance is held and only the near field changes.
    /// </summary>
    public static (float Gain, float ReferenceDistance) Place(float sourceLevelDb, float extentMetres)
        => Widen(Place(sourceLevelDb), extentMetres);

    /// <summary>
    /// The same widening for a gain and reference distance with no source level (an authored emitter).
    /// Widening without paying the gain down once handed every quiet vehicle up to eight decibels it had
    /// not earned (a reference widened to three metres).
    /// </summary>
    public static (float Gain, float ReferenceDistance) Widen((float Gain, float ReferenceDistance) placed, float extentMetres)
        => extentMetres > placed.ReferenceDistance
         ? (placed.Gain * (placed.ReferenceDistance / extentMetres), extentMetres)
         : placed;

    /// <summary>The same, spelled out for a caller holding two loose floats.</summary>
    public static (float Gain, float ReferenceDistance) Widen(float gain, float referenceDistance, float extentMetres)
        => Widen((gain, referenceDistance), extentMetres);

    /// <summary>
    /// How far a sound arrives over the output's ceiling, rendered dB: what the rest of the world gives
    /// way by while the ear is overloaded (FmodAudioProvider). The level at a metre, spread over
    /// <paramref name="distance"/>, less the path (<paramref name="pathGain"/>, an amplitude) and the
    /// air, over the shipped ceiling (112 dB) at the shipped compression. About 20 dB for a pistol at a
    /// metre, 11 at 10 m, 2 at 100 m; nothing from a jackhammer.
    /// </summary>
    public static float OverloadDb(float levelDb, float distance, float pathGain = 1f, float airDb = 0f)
    {
        if (levelDb <= 0f) return 0f;
        float atEar = levelDb - 20f * MathF.Log10(MathF.Max(1f, distance))
                    + 20f * MathF.Log10(Math.Clamp(pathGain, 1e-4f, 1f)) - airDb;
        // The ear's reflex, not the player's setting: tied to /levels at "real" (a ceiling near 89 dB)
        // the world gave way 24 dB for a revolver 260 m off, and a little for every hand clap (Cody,
        // 2026-10-02: "does the ducking mean the shooter is very close?").
        return MathF.Max(0f, (atEar - ShippedCeilingDb) * DefaultCompression);
    }

    /// <summary>Just the gain, for a caller that is setting the reference distance itself.</summary>
    public static float GainFor(float sourceLevelDb) => Place(sourceLevelDb).Gain;

    /// <summary>
    /// What distance does to a voice, as the mixer applies it: 1/d from the reference distance out, and
    /// a fade over the last quarter of the range. A range shorter than the sound is heard silences it
    /// early (<see cref="AudibleRange"/> is what a range should be). Here, not in the FMOD provider, so a
    /// test can check a balance without a sound card.
    /// </summary>
    public static float RenderedGain(float gain, float referenceDistance, float range, float distance)
    {
        float min = MathF.Max(0.1f, referenceDistance);
        float span = MathF.Max(0.01f, range - min);
        float inv = Math.Clamp(min / MathF.Max(distance, min), 0f, 1f);
        float edgeFade = Math.Clamp((range - distance) / (0.25f * span), 0f, 1f);
        return gain * inv * edgeFade;
    }

    /// <summary>The muzzle blast level for a weapon, from its cartridge. An unknown cartridge gets the
    /// 7.62's, audible and plausible, and a warning once, so a guessed level shows.</summary>
    public static float MuzzleBlastDb(WeaponDefinition w)
    {
        switch (w.Cartridge)
        {
            case "5.56x45mm": return Rifle556Db;
            case "7.62x39mm": return Rifle762Db;
            case "9x19mm": return Pistol9mmDb;
            case ".45 ACP": return Pistol45Db;
            case ".357 Magnum": return Magnum357Db;
            case "12 gauge 00 buck": return Shotgun12GaugeDb;
            case ".308 Winchester": return Rifle308Db;
        }
        string cartridge = w.Cartridge ?? "";
        if (_unknownCartridges.TryAdd(cartridge, 0))
            Serilog.Log.Warning("Loudness: no blast level for cartridge {Cartridge} (weapon {Weapon}); using {Db} dB",
                                cartridge, w.Id, Rifle762Db);
        return Rifle762Db;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _unknownCartridges =
        new(StringComparer.Ordinal);

    /// <summary>The cartridges <see cref="MuzzleBlastDb"/> has had to guess for so far.</summary>
    public static System.Collections.Generic.ICollection<string> UnknownCartridges => _unknownCartridges.Keys;

    /// <summary>
    /// The distance at which a source of this level drops to the quietest thing worth rendering: what
    /// <c>SpatialEmitter.Range</c> should be, from the level rather than by feel.
    /// </summary>
    public static float AudibleRange(float sourceLevelDb)
    {
        var (gain, reference) = Place(sourceLevelDb);
        if (gain <= SilenceGain) return reference;
        return MathF.Min(3000f, reference * gain / SilenceGain);
    }

    /// <summary>A source's physical level at a distance, dB SPL, before the mix's compression.</summary>
    public static float SplAt(float sourceLevelDb, float distanceMetres) =>
        sourceLevelDb - 20f * MathF.Log10(MathF.Max(1f, distanceMetres));
}
