namespace OpenFPS.Common;

/// <summary>
/// How loud a thing is at its source, in decibels, and what that becomes in the mix.
///
/// Every asset ships normalised to the same peak, and a 0-1 <c>Volume</c> multiplier can make a
/// sound quieter but never louder than anything else. Distance attenuation (1/r on the Steam Audio
/// path) starts every source from the same place, so with volume alone a gunshot ten metres away
/// renders FOURTEEN DECIBELS QUIETER than a footstep at one metre. In air those two differ by about
/// eighty-five decibels in the other direction.
///
/// So a sound says how loud it is where it is made, and the engine works out the rest.
///
/// ── The honest part ────────────────────────────────────────────────────────────────────────────
///
/// The real dynamic range here is about 115 dB, from a muzzle blast at 165 down to a quiet ambience
/// around 48. Sixteen-bit audio has 96 dB and a listener in a real room has rather less; render it
/// literally and every footstep in the game is below the noise floor of the headphones. So the range
/// is COMPRESSED — <see cref="DynamicRangeCompression"/> — and that is a deliberate departure from
/// physics, not an accident of tuning. What compression preserves is the ORDER and the ROUGH SPACING:
/// a gunshot still dwarfs a footstep, a shotgun still outweighs a pistol, and a thing twice as far
/// away is still half as loud. What it gives up is the literal ratio, because the literal ratio is
/// unlistenable.
///
/// The numbers below are real measured source levels where such things are published (firearms are
/// well documented, because hearing damage is). They are a starting point for tuning by ear, not a
/// result of it — see todo.md.
/// </summary>
public static class Loudness
{
    // ── Source levels, dB SPL at one metre ──────────────────────────────────────────────────────
    //
    // Firearms are quoted at the shooter's position, which is where they are measured and where the
    // hearing-protection figures come from.

    /// <summary>5.56x45 from a 16-inch barrel. The loudest thing in the game and the reference.</summary>
    public const float Rifle556Db = 165f;
    /// <summary>7.62x39. Slightly quieter than the 5.56 but with far more low frequency, which is why
    /// it carries further through walls even though it measures lower.</summary>
    public const float Rifle762Db = 159f;
    public const float Pistol9mmDb = 160f;
    public const float Pistol45Db = 157f;
    /// <summary>.357 Magnum from a 6-inch revolver: about 164 dB at the shooter, among the loudest
    /// handguns there are. Louder than the 9 mm for its bigger charge, and the cylinder gap vents
    /// beside the shooter's hand as well as at the muzzle.</summary>
    public const float Magnum357Db = 164f;
    public const float Shotgun12GaugeDb = 160f;
    /// <summary>.308 Winchester from a 24-inch bolt gun: about 167 dB at the shooter in the published
    /// hearing-protection tables, two above the 5.56 for nearly twice the powder. The 5.56 stays the
    /// reference the mix was set against; this is the one thing louder.</summary>
    public const float Rifle308Db = 167f;

    /// <summary>A case bouncing on concrete. Quiet, close, and very informative.</summary>
    public const float CasingDb = 75f;

    /// <summary>
    /// A footfall, at one metre — YOUR OWN, which is the case that matters, because your feet are the
    /// only sound in the game you make on purpose to find out where you are.
    ///
    /// DERIVED, not chosen, through the same impact constant as everything else that is struck
    /// (<see cref="PanelAcoustics.ImpactReferenceDb"/>: one joule is 74 dB at a metre). The foot and
    /// shank are about a seventh of a body — ten kilograms of effective mass — and they arrive at
    /// something under a metre a second, so a footfall is a couple of joules: 78 dB if every one of
    /// them radiated. They do not; a sole and a floor between them take most of it, which is the
    /// whole of what <see cref="Footsteps"/> models, and ten decibels is what that costs.
    ///
    /// Not 55: that is a soft trainer on carpet heard from a metre away, not a shoe on concrete under
    /// your own head, and against the rest of this table it puts a footstep twenty decibels under a
    /// spent cartridge case bouncing on the pavement (<see cref="CasingDb"/>). At 55 a walk measured
    /// -26 LUFS against the -18 to -23 a game mix belongs at.
    /// </summary>
    public const float FootstepDb = 68f;

    /// <summary>
    /// The sound pressure level that renders at full scale, dB SPL.
    ///
    /// Not the level of the loudest SOURCE — the level at the LISTENER that uses all the headroom.
    /// 130 dB is about where sound stops being loud and starts being pain, which is a defensible place
    /// to put the top of a mix. A rifle at thirty metres arrives at 129 dB, so it renders at full
    /// scale; the same rifle at ten metres arrives at 139 and is simply clipped, which is what happens
    /// to your ears as well.
    ///
    /// Setting this to the source level instead — 165, the rifle's level at one metre — is a mistake
    /// worth naming because it looks so reasonable. It makes
    /// a gunshot full-scale only when you are standing AT the muzzle, and at any real distance the
    /// inverse-square law has already taken 30 dB off it before the mix sees it. Guns are very loud;
    /// they have to be loud at the ranges people actually shoot from.
    /// </summary>
    // Not 130. At 130 the only thing that ever reaches full scale is gunfire: a door slam at 88 dB
    // renders at -28 dBFS and a pane of glass across a street at -41, and they sound weak and dull
    // while the physics is right. Everyday sounds are 60 to 95 dB and they are what the game is mostly
    // made of, so that is the range the mix should spend itself on. Gunfire runs into the ceiling and
    // clips, which is what a gunshot does to an ear and to a microphone.
    //
    // A PIVOT, not a fixed ceiling. With the compression a setting, a fixed 112 dB ceiling would make
    // "real" (1.0) put everything below a jackhammer as far below the volume knob as it is below one:
    // a street scene 40 dB down, footsteps and beacons all but gone. So the compression turns about
    // an everyday level instead: a sound 70 dB at its reference distance
    // (the 1.2 m minimum, for anything that quiet) plays at the same level at
    // any setting, and the level that reaches full scale follows — 112 dB at the shipped 0.45, where
    // nothing has moved, and about 89 dB at 1.0, where a V8 floored beside you runs into the ceiling
    // the way it does into an ear, and a door, a footstep and a beacon stay where they were.
    public const float PivotDb = 70f;
    private const float ShippedCeilingDb = 112f;

    /// <summary>Where a <see cref="PivotDb"/> sound plays, dBFS at its reference: the level the shipped
    /// mix gave it, held whatever the compression.</summary>
    public static float PivotRenderedDb => (PivotDb - ShippedCeilingDb) * DefaultCompression;

    /// <summary>The level at the ear that plays at full scale, for the compression in force.</summary>
    public static float RenderCeilingDb => PivotDb - PivotRenderedDb / DynamicRangeCompression;

    /// <summary>
    /// How much of the real decibel difference survives into the mix. 1.0 is literal physics and
    /// unlistenable; 0 would make everything the same loudness. At 0.45 the 110 dB between a rifle and
    /// an ambience bed becomes about 50 dB of rendered range, which fits in a mix and still leaves a
    /// gunshot startling next to a footstep.
    ///
    /// This compresses SOURCE levels only. Distance is deliberately left literal — the engine's 1/r —
    /// because compressing that would flatten the range cues the whole game is built on.
    ///
    /// A SETTING, not a constant: at 0.45 a hot rod 19 dB louder than an economy car is placed only
    /// 9 dB louder, and a car flooring it rises by under half its real surge
    /// (the engine's idle lift follows this same number). It applies to every source in the game —
    /// placement, how far it carries, the engine lift — so louder things always carry further, and
    /// the less compression, the more so. The client sets it (`/levels`, saved in ClientSettings);
    /// OPENFPS_LEVEL_COMPRESSION overrides it for a run. Held to 0.2..1.
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

    /// <summary>Largest reference distance we will hand out. Past this a source stops being a point
    /// and the 1/r model stops meaning much anyway.</summary>
    public const float MaxReferenceDistance = 40f;
    // Not half a metre. The reference distance is where a sound stops getting louder as you
    // approach, and beyond it everything falls away as 1/d — so a floor of 0.5 m would have a quiet
    // source already six decibels down a metre from it. Nothing in this game is heard from closer
    // than about a metre; a door at arm's length must not be attenuated as though the listener's ear
    // were pressed to the latch.
    public const float MinReferenceDistance = 1.2f;

    /// <summary>
    /// Where to put a sound of a given source level: the gain it plays at, and the reference distance
    /// inside which it does not get any louder.
    ///
    /// These two have to be decided together, not separately. The engine attenuates by <c>MinDistance / distance</c>, so the
    /// reference distance is not a detail of the rolloff — it is what decides how loud the sound still
    /// is at the range it is actually heard from. With a fixed 2 m reference a muzzle blast is down to
    /// a fifteenth of its level by thirty metres, and no amount of source gain rescues it because the
    /// gain is already clamped at full scale.
    ///
    /// So the reference distance is derived: it is the distance at which this source's SPL falls to
    /// <see cref="RenderCeilingDb"/>. A 159 dB rifle reaches 130 dB at about twenty-eight metres, so
    /// it plays at full scale out to twenty-eight metres and only then begins to fall. A footstep
    /// reaches the ceiling nowhere at all, so it clamps to <see cref="MinReferenceDistance"/> and
    /// takes its quietness from the gain instead.
    /// </summary>
    /// <summary>
    /// Where the law puts a source of this level, as one number: 20 log10 of gain times reference
    /// distance, which is its rendered level at one metre's worth of 1/r. Two levels' difference in
    /// this is how far apart the mix places them — what the engine's idle lift is computed from.
    /// </summary>
    public static float PlacedDb(float sourceLevelDb)
    {
        var (gain, reference) = Place(sourceLevelDb);
        return 20f * MathF.Log10(MathF.Max(1e-9f, gain * reference));
    }

    public static (float Gain, float ReferenceDistance) Place(float sourceLevelDb)
    {
        float ceiling = RenderCeilingDb;
        float ideal = MathF.Pow(10f, (sourceLevelDb - ceiling) / 20f);
        float reference = Math.Clamp(ideal, MinReferenceDistance, MaxReferenceDistance);

        // What is left over after the clamp, compressed. For a loud source the clamp does nothing and
        // this comes out at unity; for a quiet one it is the whole of its quietness.
        float atReference = sourceLevelDb - 20f * MathF.Log10(MathF.Max(0.01f, reference));
        float renderedDb = (atReference - ceiling) * DynamicRangeCompression;
        return (MathF.Min(1f, MathF.Pow(10f, renderedDb / 20f)), reference);
    }

    // ── The law in loudness units (docs/EAR_MODEL.md) ─────────────────────────────────────────
    //
    // The law above works on unweighted level. The ear does not: an idling engine is mostly bass the
    // ear barely hears, and a pure tone is fifteen phon quieter than speech of the same level. So the
    // law is applied to what a source sounds like: the source is turned into the speech line that is
    // just as loud (ISO 532-1, from the source's own measured spectrum, at its real level), the old law
    // places that line, and the source plays as loud as the line plays. Speech is placed exactly as
    // before, and everything keeps its order of loudness.
    //
    // Two conventions for a declared level live side by side, and loudness needs the real one. A
    // recording or a render declares its buffer's FULL SCALE at a metre (Speech.LevelDb: a line sits
    // 28 dB under it), so its real level is the declared level plus where the sound sits in its buffer
    // (Timbre.GatedRmsDbfs, measured). A physical voice (an engine, a machine, rain, the wind at the
    // ears) declares its RMS level and plays it PeakHeadroomDb under full scale. Timbre.RealOffsetDb and
    // DigitalRmsDb carry which.

    /// <summary>Where a physical voice's RMS sits under full scale, dB (VehicleProfile.PeakHeadroomDb).</summary>
    public const float PhysicalRmsDbfs = -VehicleProfile.PeakHeadroomDb;

    /// <summary>Where the reference sound, a speech line, sits under its full scale (Speech.BufferRmsDbfs).</summary>
    public const float ReferenceRmsDbfs = Speech.BufferRmsDbfs;

    /// <summary>
    /// The playback the game is designed for, dB SPL at the ear of a 0 dB rendered level: the one at
    /// which a normal voice a metre away (ANSI S3.5, 62.35 dB) plays as loud as it is, at the shipped
    /// /levels. About 100.8. The law in loudness units compares sounds at this playback; the player's
    /// own playback (Hearing.EarModel.ListeningLevelDb) only moves the tone correction.
    /// </summary>
    public static readonly float DesignFullScaleDb = DesignFullScale();

    private static float DesignFullScale()
    {
        float declared = Speech.LevelDb(Speech.NormalDb);
        float reference = Math.Clamp(MathF.Pow(10f, (declared - ShippedCeilingDb) / 20f), MinReferenceDistance, MaxReferenceDistance);
        float rendered = (declared - 20f * MathF.Log10(reference) - ShippedCeilingDb) * DefaultCompression;
        return Speech.NormalDb - rendered - ReferenceRmsDbfs;
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
    /// How much more (or less) the law in loudness units gives a source than the unweighted law, dB.
    /// Beyond the reference distance it is the same at every distance, so a voice placed by the
    /// unweighted law is put right by this one gain.
    /// </summary>
    public static float TimbreCorrectionDb(float sourceLevelDb, Hearing.Timbre? timbre)
        => timbre == null || timbre.IsReference || !Hearing.EarModel.Enabled ? 0f
         : PlacedDb(sourceLevelDb, timbre) - PlacedDb(sourceLevelDb);

    /// <summary>The ranking as it was before 2026-10-07, on the played gain: for an instrument's before and
    /// after (RankingByLoudnessProbe). Nothing in the game sets it.</summary>
    public static bool RankOnPlayedGain { get; set; }

    /// <summary>
    /// How loud a voice is to the ear, for ranking (docs/EAR_MODEL.md, Ranking): a voice that plays at
    /// <paramref name="playedGain"/> (the rendered amplitude, the law's correction and the path
    /// included), of this spectrum, turned into the gain at which a speech line would play exactly as
    /// loud. A voice that is heard as louder ranks higher, whatever the law had to do to its gain.
    ///
    /// Its level at the ear is taken at the designed playback, in its own convention (a recording's RMS
    /// sits at its gated RMS under full scale, a physical voice's at <see cref="PhysicalRmsDbfs"/>); its
    /// loudness level from ISO 532-1 with its own measured spectrum (<see cref="Hearing.Timbre.HeardPhons"/>);
    /// and back through the reference sound. So a speech line, or a recording not measured yet (taken
    /// as one), ranks at exactly its played gain, as it always did. A physical voice not measured yet
    /// is taken as speech-shaped at its own RMS.
    ///
    /// Not the compensated gain: the law plays a sound the ear hears less of LOUDER so that it is heard
    /// at its real loudness (a 65 dB rumble at 25 Hz gets about 21 dB more than speech), and ranking on
    /// that gain put the rumble 21 dB up the list. With the ear model off this is the played gain.
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
        // Not held to 1, as the unweighted law's gain is. There the gain reaches 1 exactly where the
        // source reaches the ceiling and never passes it; here a sound the ear hears less of per
        // decibel (an idling engine, a pure tone) needs more level than speech to be as loud, and at
        // the ceiling that is more than the reference sound's full scale. Holding it would flatten
        // every such source above the pivot to one level.
        return (MathF.Pow(10f, renderedDb / 20f), reference);
    }

    /// <summary>
    /// The same placement, for a source that is not a point.
    ///
    /// A crowd, a waterfall or a motorway has a SIZE, and inside it the inverse law does not hold:
    /// stepping a metre towards one clapper steps you a metre away from another, so the level is flat
    /// across the patch and only starts falling once the whole of it is in front of you. So the
    /// reference distance is the thing's own radius.
    ///
    /// The gain comes down to pay for it, and that is the half that is easy to get wrong. Beyond the
    /// patch a distributed source and a point source of the same total power sound IDENTICAL — that is
    /// what makes the point model usable at all — so widening the reference without touching the gain
    /// would not model a crowd, it would just make one louder than physics allows at every distance
    /// that matters. What the inverse law carries is the PRODUCT of gain and reference distance, so
    /// that product is held and only the near field changes.
    /// </summary>
    public static (float Gain, float ReferenceDistance) Place(float sourceLevelDb, float extentMetres)
        => Widen(Place(sourceLevelDb), extentMetres);

    /// <summary>
    /// The same widening, for a caller that has a gain and a reference distance but no source level.
    ///
    /// An authored emitter — a fountain, a ventilation grille, a waterfall — carries a volume rather
    /// than a level in decibels, and it is just as much a thing with a SIZE. The rule is the one that
    /// matters and the one that is easy to get wrong in the other direction: what the inverse law
    /// carries is the PRODUCT of gain and reference distance, so widening the reference without
    /// paying the gain down does not model an extended source, it makes a louder one at every
    /// distance that matters.
    ///
    /// That mistake was in the engine: a vehicle's reference was widened to three metres with nothing
    /// paid back, which handed every quiet vehicle up to eight decibels it had not earned.
    /// </summary>
    public static (float Gain, float ReferenceDistance) Widen((float Gain, float ReferenceDistance) placed, float extentMetres)
        => extentMetres > placed.ReferenceDistance
         ? (placed.Gain * (placed.ReferenceDistance / extentMetres), extentMetres)
         : placed;

    /// <summary>The same, spelled out for a caller holding two loose floats.</summary>
    public static (float Gain, float ReferenceDistance) Widen(float gain, float referenceDistance, float extentMetres)
        => Widen((gain, referenceDistance), extentMetres);

    /// <summary>
    /// How far a sound arrives over the output's ceiling, in rendered decibels: what the rest of the
    /// world gives way by while the ear is overloaded (FmodAudioProvider). Its level at the ear is the
    /// declared level at a metre, spread over <paramref name="distance"/> (from a metre), less what
    /// the way to the ear took (<paramref name="pathGain"/>, an amplitude, and the air); the excess
    /// over the shipped ceiling (112 dB) is compressed by the shipped law, independent of the player's
    /// level setting: the reflex is the ear's.
    /// About 20 dB for a pistol at a metre, 11 at 10 m, 2 at 100 m; nothing from a jackhammer.
    /// </summary>
    public static float OverloadDb(float levelDb, float distance, float pathGain = 1f, float airDb = 0f)
    {
        if (levelDb <= 0f) return 0f;
        float atEar = levelDb - 20f * MathF.Log10(MathF.Max(1f, distance))
                    + 20f * MathF.Log10(Math.Clamp(pathGain, 1e-4f, 1f)) - airDb;
        // The ear's, not the player's: fixed at the shipped ceiling and compression, whatever /levels
        // is set to. Tied to the live setting, a player on "real" (1.0, a ceiling near 89 dB) had the
        // world give way the full 24 dB for a revolver 260 m off, and a little for every hand clap
        // and every "thank you" (Cody, 2026-10-02: "does the ducking mean the shooter is very close?").
        return MathF.Max(0f, (atEar - ShippedCeilingDb) * DefaultCompression);
    }

    /// <summary>Just the gain, for a caller that is setting the reference distance itself.</summary>
    public static float GainFor(float sourceLevelDb) => Place(sourceLevelDb).Gain;

    /// <summary>
    /// What distance does to a voice: the law the mixer actually applies, in one place.
    ///
    /// A natural inverse-distance rolloff from the reference distance out — a source is fully itself
    /// up close and falls away as 1/d — and then a fade over the last quarter of its range so it
    /// reaches nothing at the range rather than stopping on a step. That fade is the part worth
    /// knowing about: a range set shorter than a sound can actually be heard does not save anything,
    /// it silences the sound early. See <see cref="AudibleRange"/>, which is what a range should be.
    ///
    /// It lives here rather than inside the FMOD provider so that a test can ask what the mixer will
    /// do without a sound card, which is the only way the balance between two sources can be checked
    /// at all.
    /// </summary>
    public static float RenderedGain(float gain, float referenceDistance, float range, float distance)
    {
        float min = MathF.Max(0.1f, referenceDistance);
        float span = MathF.Max(0.01f, range - min);
        float inv = Math.Clamp(min / MathF.Max(distance, min), 0f, 1f);
        float edgeFade = Math.Clamp((range - distance) / (0.25f * span), 0f, 1f);
        return gain * inv * edgeFade;
    }

    /// <summary>The muzzle blast level for a weapon, from its cartridge. Falls back to the 7.62
    /// figure — audible and plausible — rather than to silence or to the ceiling, and says so once
    /// per cartridge: a new weapon whose cartridge is missing here would otherwise play at 159 dB
    /// with nothing to show it was guessed.</summary>
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
    /// The distance at which a source of this level drops to the quietest thing worth rendering —
    /// what <c>SpatialEmitter.Range</c> should be. A gunshot carries across a map and a footstep does
    /// not, and that difference should fall out of how loud each one is rather than being a separate
    /// decision made by feel.
    /// </summary>
    public static float AudibleRange(float sourceLevelDb)
    {
        var (gain, reference) = Place(sourceLevelDb);
        if (gain <= SilenceGain) return reference;
        return MathF.Min(3000f, reference * gain / SilenceGain);
    }

    /// <summary>What a source of this level actually sounds like at a distance, in dB SPL — the
    /// physical quantity, before any of the mix's compression. For reporting and for tests.</summary>
    public static float SplAt(float sourceLevelDb, float distanceMetres) =>
        sourceLevelDb - 20f * MathF.Log10(MathF.Max(1f, distanceMetres));
}
