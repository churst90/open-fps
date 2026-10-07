using System.Collections.Generic;
using System.Text.Json.Serialization;
using OpenFPS.Common.Editing;

namespace OpenFPS.Common;

// Things built to be heard: air horns, steam whistles and struck bells. As in Engines.cs, a part has
// dimensions, never a note: a horn sounds c/2L, a steam whistle (closed at the top) c/4L and its odd
// harmonics, sharp because sound is half again as fast in hot steam; a bell's shell modes are not
// harmonic. The one number that is not a dimension is the level anchor (ReferenceDb, SPL at a metre on
// axis), because a horn's legal level is a better figure than a radiation integral.

/// <summary>One bell of a chime horn: a tapered pipe with a reed across its small end.</summary>
public sealed record ChimeBellSpec
{
    /// <summary>Throat to mouth along the axis, metres: the note. A horn flaring from a small throat
    /// behaves like a full cone, c/2L and all harmonics; a cylinder with a reed is c/4L, odd ones only.</summary>
    [Tunable("m", 0.1, 1.5, "Throat to mouth along the bell's axis. This sets the note: a longer bell is lower.", Label = "length", Step = 0.005)]
    public required float LengthMetres { get; init; }
    /// <summary>The mouth, metres: the end correction, the low cutoff and how hard it beams (bright
    /// coming at you, dull going away).</summary>
    [Tunable("m", 0.03, 0.3, "Diameter of the bell's mouth. It sets the low cutoff and how hard the bell beams forward.", Label = "mouth diameter", Step = 0.002)]
    public float MouthDiameterMetres { get; init; } = 0.11f;
    /// <summary>The throat, metres, where the reed sits.</summary>
    [Tunable("m", 0.005, 0.05, "Diameter of the throat, where the reed sits.", Label = "throat diameter", Step = 0.001)]
    public float ThroatDiameterMetres { get; init; } = 0.022f;
    /// <summary>When air reaches this bell relative to the first, seconds: the manifold's smear at the
    /// start of a blast, and its mirror at the end.</summary>
    [Tunable("s", 0, 0.2, "How long after the first bell the air reaches this one. Spread bells swell into the chord.", Label = "start delay", Step = 0.002)]
    public float StartDelaySeconds { get; init; }
    /// <summary>Per-bell level adjustment, dB. Big bells breathe more air and are louder.</summary>
    [Tunable("dB", -12, 6, "This bell's level against the others. Big bells take more air and are louder.", Label = "level against the others", Step = 0.5)]
    public float LevelTrimDb { get; init; }

    /// <summary>Effective length: the tube plus the flanged-mouth end correction 0.6a.</summary>
    [JsonIgnore]
    public float EffectiveLengthMetres => LengthMetres + 0.6f * 0.5f * MouthDiameterMetres;
    /// <summary>The note, hertz, at 20 C. A full cone: all harmonics of c/2L.</summary>
    [JsonIgnore]
    public float Hz => 343f / (2f * MathF.Max(0.02f, EffectiveLengthMetres));
}

/// <summary>An air horn: one or more bells on a common air supply.</summary>
public sealed record ChimeHornSpec
{
    [Tunable("", 0, 0, "The horn's name as it is said.")]
    public required string Name { get; init; }
    public required ChimeBellSpec[] Bells { get; init; }
    /// <summary>Supply pressure, kPa gauge: a locomotive's main reservoir is 140 psi, a truck's 120. It
    /// drives the reed, and so the harmonics; a horn on a leaking line goes flat and breathy.</summary>
    [Tunable("kPa", 200, 1200, "Air pressure at the horn valve, gauge. A locomotive blows on about 965, a truck on about 830. More pressure drives the reed harder and brightens the note.", Label = "supply pressure", Step = 10)]
    public float SupplyKPa { get; init; } = 965f;
    /// <summary>
    /// The reed's own resonance as a multiple of the bell's note, and below it. A diaphragm over a port
    /// is an outward-striking valve, which pumps energy into the pipe only when driven above its own
    /// resonance (as a brass player's lips). Tuned above the column it damps to a hiss, as the first
    /// version did; the measured note in Describe() caught it.
    /// </summary>
    [Tunable("", 0.3, 0.95, "The diaphragm's own resonance as a fraction of the bell's note. It must sit below the note; a floppier diaphragm is lower.", Label = "reed tuning", Step = 0.01)]
    public float ReedRatio { get; init; } = 0.62f;
    /// <summary>
    /// How much of each cycle the diaphragm is off its seat at full blow, 0..1: the pulse width is the
    /// timbre. A wide pulse is nearly a half-wave sine, the mellow locomotive chime; a short one keeps
    /// its harmonics level up to about one over its width, a trumpet.
    /// </summary>
    [Tunable("", 0.05, 0.9, "How much of each cycle the diaphragm is off its seat at full blow. Wide is a mellow chime, narrow is a brassy trumpet.", Label = "reed open fraction", Step = 0.01)]
    public float ReedOpenFraction { get; init; } = 0.46f;
    /// <summary>
    /// How far flat the note sits while the air is only just reaching the reed, as a fraction of the
    /// note. Under 1%: the column's resonances are about 3% wide, and a larger bend drags the harmonics
    /// off them so the horn is weak at both ends (6.5% was tried on every air horn and heard that way).
    /// </summary>
    [Tunable("", 0, 0.03, "How far flat the note sits while the air is only just reaching the reed, as a fraction of the note. Keep it under 0.01.", Label = "pitch bend at start and end", Step = 0.001)]
    public float PitchBend { get; init; } = 0.008f;
    /// <summary>How long the valve takes to reach full pressure, seconds, and to fall. A slow valve
    /// is heard as a weak start and a tail that hangs on 15-25 dB down for half a second.</summary>
    [Tunable("s", 0.002, 0.5, "How long the valve takes to bring the horn up to full pressure. A slow valve is a weak start.", Label = "valve rise time", Step = 0.005)]
    public float RiseSeconds { get; init; } = 0.025f;
    [Tunable("s", 0.002, 0.5, "How long the pressure takes to fall when the valve closes. A slow valve leaves a tail.", Label = "valve fall time", Step = 0.005)]
    public float FallSeconds { get; init; } = 0.03f;
    /// <summary>SPL at one metre on axis with every bell blowing. A locomotive horn is required to
    /// make 96-110 dBA at 100 feet ahead of it, which is 125-140 at a metre.</summary>
    [Tunable("dB", 100, 150, "Sound level at one metre on axis with every bell blowing.", Label = "level at one metre", Step = 1, Source = "locomotive horn requirement: 96 to 110 dBA at 100 feet ahead, 125 to 140 dB at a metre")]
    public float ReferenceDb { get; init; } = 138f;

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Nathan AirChime K5LA, the five-chime on most Amtrak and much freight power. D#4 F#4 G#4 B4 D#5
    /// (311, 370, 415, 494, 622 Hz), a B major sixth over a D# bass; A#4 and C#5 on top would be a Leslie
    /// S-4T's D# minor seventh (train-horn.com's K3LA/K5LA and Nathan/Leslie guides). The bells speak
    /// over about forty milliseconds.
    /// </summary>
    public static ChimeHornSpec NathanK5LA => new()
    {
        Name = "Nathan AirChime K5LA, five chime",
        Bells = new[]
        {
            new ChimeBellSpec { LengthMetres = 0.518f, MouthDiameterMetres = 0.110f, ThroatDiameterMetres = 0.024f, StartDelaySeconds = 0.000f, LevelTrimDb = 0f },
            new ChimeBellSpec { LengthMetres = 0.431f, MouthDiameterMetres = 0.102f, ThroatDiameterMetres = 0.023f, StartDelaySeconds = 0.012f, LevelTrimDb = -1f },
            new ChimeBellSpec { LengthMetres = 0.383f, MouthDiameterMetres = 0.098f, ThroatDiameterMetres = 0.022f, StartDelaySeconds = 0.020f, LevelTrimDb = -1.5f },
            new ChimeBellSpec { LengthMetres = 0.319f, MouthDiameterMetres = 0.094f, ThroatDiameterMetres = 0.022f, StartDelaySeconds = 0.030f, LevelTrimDb = -2f },
            new ChimeBellSpec { LengthMetres = 0.249f, MouthDiameterMetres = 0.088f, ThroatDiameterMetres = 0.021f, StartDelaySeconds = 0.040f, LevelTrimDb = -3f },
        },
        SupplyKPa = 965f, ReferenceDb = 139f,
    };

    /// <summary>
    /// A Leslie three-chime, common on transit and older passenger power: harder-edged, the bells short
    /// for their mouths. The RS3L's bells 25, 31 and 44 sound C4, D#4 and A4 (262, 311, 440 Hz,
    /// locomotivehorns.info).
    /// </summary>
    public static ChimeHornSpec LeslieRS3L => new()
    {
        Name = "Leslie RS3L, three chime",
        Bells = new[]
        {
            new ChimeBellSpec { LengthMetres = 0.618f, MouthDiameterMetres = 0.125f, ThroatDiameterMetres = 0.026f, StartDelaySeconds = 0f },
            new ChimeBellSpec { LengthMetres = 0.517f, MouthDiameterMetres = 0.116f, ThroatDiameterMetres = 0.025f, StartDelaySeconds = 0.010f, LevelTrimDb = -1f },
            new ChimeBellSpec { LengthMetres = 0.358f, MouthDiameterMetres = 0.106f, ThroatDiameterMetres = 0.024f, StartDelaySeconds = 0.022f, LevelTrimDb = -2f },
        },
        SupplyKPa = 965f, ReferenceDb = 137f,
    };

    /// <summary>
    /// A European two-tone to UIC 644 / EN 15153-2: 370 Hz and 660 Hz, sounded together or
    /// alternately.
    /// </summary>
    public static ChimeHornSpec TwoToneEuropean => new()
    {
        Name = "two-tone, high and low",
        Bells = new[]
        {
            new ChimeBellSpec { LengthMetres = 0.4365f, MouthDiameterMetres = 0.090f, ThroatDiameterMetres = 0.020f, StartDelaySeconds = 0f },
            new ChimeBellSpec { LengthMetres = 0.2373f, MouthDiameterMetres = 0.075f, ThroatDiameterMetres = 0.019f, StartDelaySeconds = 0.006f, LevelTrimDb = -1f },
        },
        SupplyKPa = 800f, ReferenceDb = 131f,
    };

    /// <summary>
    /// A North American light-rail vehicle's two-chime: D#4 and A4 (311 and 440 Hz), the Leslie S-2M
    /// pairing. No maker's figure was found for the S70, SD160 or Flexity; EN 15153-4 puts the urban-rail
    /// low tone at 370.
    /// </summary>
    public static ChimeHornSpec LightRailTwoChime => new()
    {
        Name = "light rail, two chime",
        Bells = new[]
        {
            new ChimeBellSpec { LengthMetres = 0.5213f, MouthDiameterMetres = 0.100f, ThroatDiameterMetres = 0.021f, StartDelaySeconds = 0f },
            new ChimeBellSpec { LengthMetres = 0.3628f, MouthDiameterMetres = 0.090f, ThroatDiameterMetres = 0.020f, StartDelaySeconds = 0.008f, LevelTrimDb = -1f },
        },
        SupplyKPa = 760f, ReferenceDb = 124f,
    };

    /// <summary>
    /// A tractor unit's roof trumpets on 120 psi of brake air, 173 and 228 Hz. Real ones are 0.55-0.95 m
    /// (Grover's common pair 24.5 and 21.5 in) and speak around 150-250 Hz; Leslie's table puts a 24.9 in
    /// Tyfon at 156 Hz. A flared reed horn speaks nearer c/3L than c/2L, so these lengths are effective
    /// lengths for the measured notes, not the metal.
    /// </summary>
    public static ChimeHornSpec TruckDualTrumpet => new()
    {
        Name = "tractor unit, dual trumpet",
        Bells = new[]
        {
            new ChimeBellSpec { LengthMetres = 0.952f, MouthDiameterMetres = 0.130f, ThroatDiameterMetres = 0.022f, StartDelaySeconds = 0f },
            new ChimeBellSpec { LengthMetres = 0.715f, MouthDiameterMetres = 0.120f, ThroatDiameterMetres = 0.021f, StartDelaySeconds = 0.004f, LevelTrimDb = -1.5f },
        },
        SupplyKPa = 827f, ReferenceDb = 126f, ReedOpenFraction = 0.30f,
        RiseSeconds = 0.02f, FallSeconds = 0.025f,
    };

    /// <summary>A transit bus: one small trumpet under the front, on the brake system's air. 400 Hz,
    /// in the 340-440 Hz of a bus's electric horn.</summary>
    public static ChimeHornSpec BusAirHorn => new()
    {
        Name = "transit bus, single trumpet",
        Bells = new[]
        {
            new ChimeBellSpec { LengthMetres = 0.408f, MouthDiameterMetres = 0.070f, ThroatDiameterMetres = 0.018f, StartDelaySeconds = 0f },
        },
        SupplyKPa = 760f, ReferenceDb = 118f, ReedOpenFraction = 0.30f,
        RiseSeconds = 0.02f, FallSeconds = 0.025f,
    };

    public static IReadOnlyDictionary<string, Func<ChimeHornSpec>> Presets { get; } =
        new Dictionary<string, Func<ChimeHornSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["k5la"] = () => NathanK5LA,
            ["rs3l"] = () => LeslieRS3L,
            ["two_tone"] = () => TwoToneEuropean,
            ["lrv_two_chime"] = () => LightRailTwoChime,
            ["truck_dual"] = () => TruckDualTrumpet,
            ["bus_horn"] = () => BusAirHorn,
        };

    public static ChimeHornSpec ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No horn preset '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}

// The electric horn under nearly every car's grille: a buzzer, a coil whose armature on a steel
// diaphragm opens its own contact points, and strikes the pole piece every cycle, steel on steel four
// or five hundred times a second. The note is a factory setting of the points' screw (sold as "H" and
// "L"), so it is declared, not derived. A disc horn's tone disc rings at 2-4 kHz on each strike, the
// brassy formant; a trumpet (snail) horn's coiled column lets out only its resonances, n·c/2L, above
// its flare cutoff: rounder and louder.

/// <summary>How an electric horn lets the diaphragm's motion out.</summary>
public enum ElectricHornKind
{
    /// <summary>A flat tone disc on the armature, ringing at its own modes: the brassy formant.</summary>
    Disc,
    /// <summary>A coiled exponential horn in front of the diaphragm: rounder and louder.</summary>
    Trumpet,
}

/// <summary>One horn of a set: a coil, an armature on a diaphragm, and a pair of contact points.</summary>
public sealed record ElectricHornUnitSpec
{
    /// <summary>The note, hertz: a factory setting of the contact points' screw, near the diaphragm's
    /// own resonance.</summary>
    public required float Hz { get; init; }
    /// <summary>Level of this horn against the others in the set, dB. The low horn of a pair is
    /// usually the bigger and a decibel or two louder.</summary>
    public float LevelTrimDb { get; init; }
    /// <summary>The diaphragm, metres. Sets where the radiator starts to beam.</summary>
    public float DiaphragmDiameterMetres { get; init; } = 0.090f;
    /// <summary>
    /// Disc horns: the tone disc's first mode, hertz, declared from what disc horns measure (2-4 kHz)
    /// because makers publish none of its dimensions. The next axisymmetric mode is about 6.3 times
    /// higher, mostly past hearing.
    /// </summary>
    public float ToneDiscHz { get; init; } = 2600f;
    /// <summary>Trumpet horns: the mouth of the coiled horn, metres.</summary>
    public float MouthDiameterMetres { get; init; } = 0.075f;
    /// <summary>Trumpet horns: the throat where the diaphragm's chamber opens into it, metres.</summary>
    public float ThroatDiameterMetres { get; init; } = 0.010f;

    /// <summary>Trumpet horns: the coiled column, cut to the note: c/2f less the mouth's end
    /// correction.</summary>
    [JsonIgnore]
    public float ColumnLengthMetres => MathF.Max(0.05f, 343f / (2f * MathF.Max(50f, Hz)) - 0.3f * MouthDiameterMetres);
}

/// <summary>An electric horn: one or more buzzers on one relay, sounding together.</summary>
public sealed record ElectricHornSpec
{
    public required string Name { get; init; }
    public required ElectricHornUnitSpec[] Units { get; init; }
    public ElectricHornKind Kind { get; init; } = ElectricHornKind.Disc;
    /// <summary>The coil's L/R, seconds: how long the current takes to rise when the points close.
    /// About a millisecond on a car horn, which is why the pull is rounded and the strike is not.</summary>
    public float CoilRiseSeconds { get; init; } = 0.0010f;
    /// <summary>How much of each cycle the points are closed. Wider points, harder pull.</summary>
    public float ContactDuty { get; init; } = 0.5f;
    /// <summary>
    /// How long the supply takes to come up when the button is pressed, seconds (a time constant): the
    /// relay pulling in and its contacts bouncing, and the diaphragm building over its first cycles. A
    /// swell of a couple of dozen milliseconds, heard as instant but not a click.
    /// </summary>
    public float RelayMakeSeconds { get; init; } = 0.010f;
    /// <summary>
    /// How long the supply takes to die away when the button is let go, seconds (a time constant): the
    /// relay's suppression diode lets its grip decay slowly, and the buzzer strikes softer until the
    /// diaphragm and disc ring down on their own.
    /// </summary>
    public float RelayBreakSeconds { get; init; } = 0.018f;
    /// <summary>SPL, RMS, at one metre on axis with every horn sounding. Legal horns are 93-112 dBA at
    /// two metres (ECE R28, FMVSS), 99-118 at one.</summary>
    public float ReferenceDb { get; init; } = 110f;

    /// <summary>The notes of the set, hertz, in the order the units are declared.</summary>
    [JsonIgnore]
    public float[] Notes => Array.ConvertAll(Units, u => u.Hz);

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Most cars' and pickups' pair: disc horns at about 410 and 510 Hz, roughly a major third,
    /// beating against each other.</summary>
    public static ElectricHornSpec DiscPair => new()
    {
        Name = "car disc horns, high and low",
        Kind = ElectricHornKind.Disc,
        Units = new[]
        {
            new ElectricHornUnitSpec { Hz = 410f, DiaphragmDiameterMetres = 0.095f, ToneDiscHz = 2400f, LevelTrimDb = 0f },
            new ElectricHornUnitSpec { Hz = 510f, DiaphragmDiameterMetres = 0.090f, ToneDiscHz = 2900f, LevelTrimDb = -1f },
        },
        ReferenceDb = 112f,
    };

    /// <summary>A small or cheap car's single disc horn, about 420 Hz. The bleat.</summary>
    public static ElectricHornSpec DiscSingle => new()
    {
        Name = "car disc horn, single",
        Kind = ElectricHornKind.Disc,
        Units = new[] { new ElectricHornUnitSpec { Hz = 420f, DiaphragmDiameterMetres = 0.085f, ToneDiscHz = 2600f } },
        ReferenceDb = 108f,
    };

    /// <summary>A pair of snail (trumpet) horns, 400 and 500 Hz, a major third: rounder, louder.</summary>
    public static ElectricHornSpec TrumpetPair => new()
    {
        Name = "trumpet (snail) horns, high and low",
        Kind = ElectricHornKind.Trumpet,
        Units = new[]
        {
            new ElectricHornUnitSpec { Hz = 400f, DiaphragmDiameterMetres = 0.080f, MouthDiameterMetres = 0.080f, ThroatDiameterMetres = 0.010f },
            new ElectricHornUnitSpec { Hz = 500f, DiaphragmDiameterMetres = 0.075f, MouthDiameterMetres = 0.070f, ThroatDiameterMetres = 0.009f, LevelTrimDb = -1f },
        },
        ReferenceDb = 116f,
    };

    /// <summary>A motorcycle's horn: one small disc, about 450 Hz, with a small bright tone disc.</summary>
    public static ElectricHornSpec MotoDisc => new()
    {
        Name = "motorcycle disc horn",
        Kind = ElectricHornKind.Disc,
        Units = new[] { new ElectricHornUnitSpec { Hz = 450f, DiaphragmDiameterMetres = 0.070f, ToneDiscHz = 3300f } },
        ReferenceDb = 105f,
    };

    public static IReadOnlyDictionary<string, Func<ElectricHornSpec>> Presets { get; } =
        new Dictionary<string, Func<ElectricHornSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["disc_pair"] = () => DiscPair,
            ["disc_single"] = () => DiscSingle,
            ["trumpet_pair"] = () => TrumpetPair,
            ["moto_disc"] = () => MotoDisc,
        };

    public static ElectricHornSpec ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No electric horn preset '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}

/// <summary>One bell of a steam whistle: a tube closed at the top, blown across a gap at the bottom.</summary>
public sealed record WhistleBellSpec
{
    /// <summary>The sounding length from the lip to the closed top, metres.</summary>
    [Tunable("m", 0.05, 1.2, "Sounding length from the lip to the closed top. A stopped pipe sounds a quarter wave of it: longer is lower.", Label = "length", Step = 0.005)]
    public required float LengthMetres { get; init; }
    [Tunable("m", 0.02, 0.25, "Inside diameter of the bell. It adds to the sounding length through the end correction.", Label = "bore", Step = 0.002)]
    public float BoreMetres { get; init; } = 0.075f;
    /// <summary>Level adjustment against the others, dB.</summary>
    [Tunable("dB", -12, 6, "This bell's level against the others.", Label = "level against the others", Step = 0.5)]
    public float LevelTrimDb { get; init; }

    [JsonIgnore]
    public float EffectiveLengthMetres => LengthMetres + 0.3f * BoreMetres;
    /// <summary>The note at a given sound speed. A STOPPED pipe: c/4L, odd harmonics.</summary>
    public float HzAt(float soundSpeed) => soundSpeed / (4f * MathF.Max(0.02f, EffectiveLengthMetres));
}

/// <summary>A steam whistle: one or more stopped bells over a common annular steam jet.</summary>
public sealed record WhistleSpec
{
    [Tunable("", 0, 0, "The whistle's name as it is said.")]
    public required string Name { get; init; }
    public required WhistleBellSpec[] Bells { get; init; }
    /// <summary>Boiler pressure at the whistle valve, kPa gauge. Sets the jet speed and so how hard
    /// the whistle is driven and how much of it is noise rather than note.</summary>
    [Tunable("kPa", 300, 2100, "Boiler pressure at the whistle valve, gauge. It sets the jet speed: more pressure drives the whistle harder and makes more of it noise.", Label = "steam pressure", Step = 10)]
    public float SupplyKPa { get; init; } = 1380f;
    /// <summary>The steam's temperature once the bell is hot, Kelvin: the sound speed, and so the pitch.
    /// At 430 K it is about 510 m/s against air's 343, half again as sharp as an organ pipe.</summary>
    [Tunable("K", 373, 650, "Temperature of the steam once the bell is hot. It sets the sound speed in the bell, so hotter steam sounds higher.", Label = "steam temperature", Step = 5)]
    public float SteamKelvin { get; init; } = 430f;
    /// <summary>How long the bell takes to fill and warm, seconds: the pitch climbs as steam displaces
    /// the cold air.</summary>
    [Tunable("s", 0.05, 3, "How long the bell takes to fill with steam and warm. The pitch climbs over this time.", Label = "warm-up time", Step = 0.05)]
    public float WarmSeconds { get; init; } = 0.55f;
    /// <summary>How much of the output is the jet's turbulence rather than the pipe's note, 0..1.
    /// A big chime whistle on wet steam is half noise, which is why it sounds like weather.</summary>
    [Tunable("", 0, 1, "How much of the sound is the turbulence of the jet rather than the note of the pipes.", Step = 0.02)]
    public float Breathiness { get; init; } = 0.4f;
    [Tunable("dB", 100, 150, "Sound level at one metre with every bell blowing.", Label = "level at one metre", Step = 1)]
    public float ReferenceDb { get; init; } = 132f;

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A big American three-chime passenger whistle, as on a Northern: stopped bells of 300, 238
    /// and 200 mm, at 510 m/s 400, 500 and 590 Hz, a deliberately rough minor triad.</summary>
    public static WhistleSpec ThreeChimePassenger => new()
    {
        Name = "three-chime passenger whistle",
        Bells = new[]
        {
            new WhistleBellSpec { LengthMetres = 0.300f, BoreMetres = 0.082f },
            new WhistleBellSpec { LengthMetres = 0.238f, BoreMetres = 0.076f, LevelTrimDb = -1.5f },
            new WhistleBellSpec { LengthMetres = 0.200f, BoreMetres = 0.070f, LevelTrimDb = -2.5f },
        },
        SupplyKPa = 1550f, SteamKelvin = 445f, Breathiness = 0.34f, ReferenceDb = 133f,
    };

    /// <summary>A five-chime freight whistle: five short bells, higher and harder. The beating between
    /// close bells is the "hollow" of a big whistle.</summary>
    public static WhistleSpec FiveChimeFreight => new()
    {
        Name = "five-chime freight whistle",
        Bells = new[]
        {
            new WhistleBellSpec { LengthMetres = 0.266f, BoreMetres = 0.070f },
            new WhistleBellSpec { LengthMetres = 0.224f, BoreMetres = 0.066f, LevelTrimDb = -1f },
            new WhistleBellSpec { LengthMetres = 0.200f, BoreMetres = 0.062f, LevelTrimDb = -1.5f },
            new WhistleBellSpec { LengthMetres = 0.178f, BoreMetres = 0.058f, LevelTrimDb = -2f },
            new WhistleBellSpec { LengthMetres = 0.150f, BoreMetres = 0.054f, LevelTrimDb = -3f },
        },
        SupplyKPa = 1550f, SteamKelvin = 445f, Breathiness = 0.32f, ReferenceDb = 134f,
    };

    /// <summary>A single-note "banshee" hooter: one 560 mm stopped bell, 215 Hz.</summary>
    public static WhistleSpec SingleNoteHooter => new()
    {
        Name = "single-note hooter",
        Bells = new[] { new WhistleBellSpec { LengthMetres = 0.560f, BoreMetres = 0.100f } },
        SupplyKPa = 1380f, SteamKelvin = 440f, Breathiness = 0.40f, WarmSeconds = 0.8f, ReferenceDb = 131f,
    };

    /// <summary>A small industrial or switching whistle: one short bell, shrill, over quickly.</summary>
    public static WhistleSpec SwitcherPeanut => new()
    {
        Name = "switcher's peanut whistle",
        Bells = new[] { new WhistleBellSpec { LengthMetres = 0.115f, BoreMetres = 0.044f } },
        SupplyKPa = 1240f, SteamKelvin = 420f, Breathiness = 0.34f, WarmSeconds = 0.3f, ReferenceDb = 124f,
    };

    public static IReadOnlyDictionary<string, Func<WhistleSpec>> Presets { get; } =
        new Dictionary<string, Func<WhistleSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["three_chime"] = () => ThreeChimePassenger,
            ["five_chime"] = () => FiveChimeFreight,
            ["hooter"] = () => SingleNoteHooter,
            ["peanut"] = () => SwitcherPeanut,
        };

    public static WhistleSpec ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No whistle preset '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}

/// <summary>
/// A struck bell: a crossing gong, a locomotive bell, a tram's foot gong. A shell, not a plate: the
/// dome's membrane stiffness pushes the low modes up and leaves the high ones nearly alone, so the rise
/// decides how far the partials sit between a cymbal's and a church bell's.
/// </summary>
public sealed record StruckBellSpec
{
    [Tunable("", 0, 0, "The bell's name as it is said.")]
    public required string Name { get; init; }
    /// <summary>The mouth, metres. A grade-crossing gong is 250-300 mm; a locomotive bell 400.</summary>
    [Tunable("m", 0.05, 1.5, "Diameter of the mouth. A crossing gong is 0.25 to 0.3, a locomotive bell 0.4. Bigger is lower.", Label = "diameter", Step = 0.01)]
    public required float DiameterMetres { get; init; }
    /// <summary>Wall thickness at the rim, metres. The note goes as h/a², so halving the diameter of the
    /// same casting raises it two octaves.</summary>
    [Tunable("m", 0.001, 0.05, "Wall thickness at the rim. Thicker is higher: the note goes as thickness over diameter squared.", Label = "rim thickness", Step = 0.0005)]
    public required float ThicknessMetres { get; init; }
    /// <summary>The rise of the crown above the rim, metres. Zero is a flat plate.</summary>
    [Tunable("m", 0, 0.5, "Height of the crown above the rim. Zero is a flat plate; a deeper dome is more of a bell and less of a gong.", Label = "dome rise", Step = 0.005)]
    public float RiseMetres { get; init; } = 0.05f;
    /// <summary>Young's modulus (Pa), density (kg/m³): bell bronze 105 GPa and 8800, steel 210 and 7850.
    /// Bronze is lower for its size, and its internal loss is twenty times smaller than steel's.</summary>
    [Tunable("Pa", 5e10, 2.5e11, "Stiffness of the metal. Bell bronze is 105 billion, steel 210 billion. Stiffer is higher.", Label = "Young's modulus", Step = 1e9)]
    public float YoungsPa { get; init; } = 105e9f;
    [Tunable("kg/m³", 2000, 12000, "Density of the metal. Bell bronze is 8800, steel 7850. Denser is lower.", Label = "density", Step = 50)]
    public float DensityKgM3 { get; init; } = 8800f;
    /// <summary>Internal loss factor. Bell bronze is about 3e-5; cast iron 1e-3; steel 2e-4.</summary>
    [Tunable("", 0.00001, 0.01, "Internal loss of the metal. Bell bronze is about 0.00003, steel 0.0002, cast iron 0.001. Higher dies away sooner.", Label = "internal loss factor", Step = 0.00001)]
    public float LossFactor { get; init; } = 4e-5f;
    /// <summary>Where the clapper lands as a fraction of the radius: 1 is the rim, 0 the crown (a
    /// thud).</summary>
    [Tunable("", 0, 1, "Where the clapper lands, as a fraction of the radius: 1 is the rim, 0 the crown. Struck at the crown it is a thud.", Label = "strike point", Step = 0.01)]
    public float StrikeRadiusFraction { get; init; } = 0.92f;
    /// <summary>The clapper: mass in kg and how fast it arrives, m/s. The Hertzian contact time sets the
    /// brightness: no mode whose period is shorter than the contact is excited.</summary>
    [Tunable("kg", 0.01, 10, "Mass of the clapper. A heavy clapper stays in contact longer and the blow is darker.", Label = "clapper mass", Step = 0.01)]
    public float ClapperKg { get; init; } = 0.35f;
    [Tunable("m/s", 0.1, 10, "How fast the clapper arrives. Faster is a harder, brighter blow.", Label = "clapper speed", Step = 0.1)]
    public float ClapperMps { get; init; } = 2.2f;
    /// <summary>Whether the clapper stays on the bell after the blow (an electric gong's does not;
    /// a hand bell's often does), 0..1 of the ring damped away.</summary>
    [Tunable("", 0, 1, "How much of the ring the clapper takes away by resting on the bell after the blow.", Label = "clapper damping", Step = 0.01)]
    public float ClapperDamping { get; init; } = 0.06f;
    /// <summary>How much the mounting takes out of it. The crown bolt is a node for every mode with two
    /// or more nodal diameters, so it kills the low breathing modes and barely touches the ones that
    /// sing.</summary>
    [Tunable("", 0, 0.2, "How much the mounting bolt through the crown takes out of the ring. It damps the low breathing modes most.", Label = "mounting loss", Step = 0.005)]
    public float MountLossFactor { get; init; } = 0.02f;
    /// <summary>Strikes a second when it is ringing continuously. A North American crossing gong
    /// runs at about 2.3.</summary>
    [Tunable("per second", 0.2, 10, "Blows a second while it is ringing continuously. A North American crossing gong runs at about 2.3.", Label = "strike rate", Step = 0.1)]
    public float StrikesPerSecond { get; init; } = 2.3f;
    /// <summary>
    /// SPL at one metre, RMS, while it is ringing, not the peak of one blow: the published figures (a
    /// crossing gong about 75 dB at 3 m, a locomotive bell 80 at 30 m) are meter readings in use. The
    /// crest factor is twenty-odd decibels, so anchoring the peak would bury the bell.
    /// </summary>
    [Tunable("dB", 60, 130, "RMS sound level at one metre while it is ringing, not the peak of one blow.", Label = "ringing level at one metre", Step = 1, Source = "required levels: a crossing gong about 75 dB at 3 m, a locomotive bell 80 dB at 30 m")]
    public float ReferenceDb { get; init; } = 86f;

    /// <summary>
    /// How far its blows stand over <see cref="ReferenceDb"/>, dB: the headroom its voice renders with,
    /// the loudest blow over the RMS across twenty seconds, rounded up. The presets measure 31.8 to 36.4
    /// since the clapper's hold lasts its 12 ms (StruckBell.Strike; 28.7 to 30.1 before); under the fleet's
    /// shared 16 every blow was squared off.
    /// </summary>
    public float PeakHeadroomDb { get; init; } = 31f;

    /// <summary>Longitudinal wave speed in the metal, m/s.</summary>
    [JsonIgnore]
    public float PlateWaveSpeed => MathF.Sqrt(YoungsPa / DensityKgM3);

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The grade-crossing gong: a 260 mm bronze casting on a shallow dome, rung by a solenoid
    /// about twice a second. Small, thin and struck hard, so bright, its partials wide apart.</summary>
    public static StruckBellSpec CrossingGong => new()
    {
        Name = "grade-crossing gong, 10 inch bronze",
        DiameterMetres = 0.260f, ThicknessMetres = 0.0045f, RiseMetres = 0.036f,
        YoungsPa = 105e9f, DensityKgM3 = 8800f, LossFactor = 5e-5f,
        StrikeRadiusFraction = 0.94f, ClapperKg = 0.22f, ClapperMps = 2.6f,
        ClapperDamping = 0.05f, StrikesPerSecond = 2.3f, ReferenceDb = 86f,
        PeakHeadroomDb = 32f,
    };

    /// <summary>A locomotive's bell: 400 mm of thick bronze rung about 1.6 times a second, an octave and
    /// a half below the gong and much longer.</summary>
    public static StruckBellSpec LocomotiveBell => new()
    {
        Name = "locomotive bell, 16 inch bronze",
        DiameterMetres = 0.400f, ThicknessMetres = 0.013f, RiseMetres = 0.115f,
        YoungsPa = 105e9f, DensityKgM3 = 8800f, LossFactor = 3e-5f,
        StrikeRadiusFraction = 0.90f, ClapperKg = 1.1f, ClapperMps = 2.0f,
        ClapperDamping = 0.10f, StrikesPerSecond = 1.6f, ReferenceDb = 110f,
        PeakHeadroomDb = 35f,
    };

    /// <summary>A tram's foot gong: a small steel dome under the floor, struck by a pedal. Steel, so
    /// it is bright and dies away fast where bronze would sing.</summary>
    public static StruckBellSpec TramGong => new()
    {
        Name = "tram foot gong, steel",
        DiameterMetres = 0.220f, ThicknessMetres = 0.0050f, RiseMetres = 0.022f,
        YoungsPa = 210e9f, DensityKgM3 = 7850f, LossFactor = 2.2e-4f,
        StrikeRadiusFraction = 0.85f, ClapperKg = 0.12f, ClapperMps = 2.4f,
        ClapperDamping = 0.18f, StrikesPerSecond = 2.0f, ReferenceDb = 95f,
        PeakHeadroomDb = 37f,
    };

    public static IReadOnlyDictionary<string, Func<StruckBellSpec>> Presets { get; } =
        new Dictionary<string, Func<StruckBellSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["crossing_gong"] = () => CrossingGong,
            ["loco_bell"] = () => LocomotiveBell,
            ["tram_gong"] = () => TramGong,
        };

    public static StruckBellSpec ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No bell preset '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}

// The electronic siren: an oscillator whose frequency sweeps (the head's modes are only sweep rates),
// a compression driver pushed past linear at full power (the brassy edge), and a horn that will not
// radiate below its flare cutoff, about 500 Hz, while the driver rolls off above 4-5 kHz. The band left
// is where the ear is most sensitive and engines and tyres are weakest. The horn beams, about ten
// decibels front to back.

/// <summary>Which sound the head is making. The hardware is identical; only the sweep rate changes.</summary>
public enum SirenMode
{
    /// <summary>Off.</summary>
    Off,
    /// <summary>The long one: about twelve sweeps a minute. What a car uses on an open road.</summary>
    Wail,
    /// <summary>Three sweeps a second, at a junction: many onsets a second are far easier to
    /// localise.</summary>
    Yelp,
    /// <summary>Ten sweeps a second: the hard stutter, for the last few metres when nobody has moved.</summary>
    Phaser,
    /// <summary>Two fixed tones a fifth apart, alternating about twice a second. The European voice.</summary>
    HiLo,
}

/// <summary>
/// An electronic siren head: an amplifier, a compression driver and a horn. Levels are anchored as
/// certification does, at ten feet on axis: a metre from a horn mouth is inside its near field.
/// </summary>
public sealed record SirenSpec
{
    public required string Name { get; init; }

    /// <summary>On-axis SPL at ten feet (3.05 m), dB. California Title 13 and SAE J1849 want at least
    /// 110; a 100 W head on a modern speaker makes 118-123.</summary>
    public float ReferenceDbAt3m { get; init; } = 120f;

    /// <summary>Mouth diameter of the horn, metres. It sets <see cref="FlareCutoffHz"/>: a bigger horn
    /// is a deeper siren, not a louder one.</summary>
    public float HornMouthMetres { get; init; } = 0.20f;

    /// <summary>Where the compression driver runs out, Hz: diaphragm mass and the phase plug.</summary>
    public float DriverTopHz { get; init; } = 4500f;

    /// <summary>How hard the driver is being pushed, 0..1: how much of the wave is squashed flat, most
    /// of the brassiness at full volume.</summary>
    public float Compression { get; init; } = 0.55f;

    /// <summary>
    /// Duty cycle of the oscillator, 0..1. The electronic siren imitates a rotary chopper, whose equal
    /// ports and lands make nearly a square wave: odd harmonics only, hollow and hard, where a
    /// sawtooth's even ones read as brassy, a trumpet. A hair off a half stops it sounding synthetic,
    /// but only a hair: the error grows with harmonic number, and at 0.48 the 3rd stood 14 dB over the
    /// 2nd and the 5th only 8.7 dB over the 4th, halfway back to a sawtooth.
    /// </summary>
    public float Duty { get; init; } = 0.49f;

    /// <summary>The sweep, Hz. A PA300 runs 650 to 1450.</summary>
    public float SweepLowHz { get; init; } = 650f;
    public float SweepHighHz { get; init; } = 1450f;

    /// <summary>Seconds per complete up-and-down sweep, per mode.</summary>
    public float WailSeconds { get; init; } = 5.0f;
    public float YelpSeconds { get; init; } = 0.31f;
    public float PhaserSeconds { get; init; } = 0.10f;

    /// <summary>The two-tone: the low note and the interval above it, and how long each is held.</summary>
    public float HiLoLowHz { get; init; } = 440f;
    public float HiLoRatio { get; init; } = 1.5f;      // a fifth
    public float HiLoHoldSeconds { get; init; } = 0.55f;

    /// <summary>The horn's flare cutoff, Hz: f = c / (π D), where the mouth's circumference reaches the
    /// wavelength. A 0.2 m mouth cuts off at 546 Hz.</summary>
    public float FlareCutoffHz => 343f / (MathF.PI * MathF.Max(0.05f, HornMouthMetres));

    /// <summary>The spec figure carried back to one metre for the emitter: a near-field fiction, like a
    /// jet's.</summary>
    public float SourceLevelDb => ReferenceDbAt3m + 20f * MathF.Log10(3.05f);

    /// <summary>Seconds per sweep for a mode, or zero if the mode does not sweep.</summary>
    public float PeriodFor(SirenMode mode) => mode switch
    {
        SirenMode.Wail => WailSeconds,
        SirenMode.Yelp => YelpSeconds,
        SirenMode.Phaser => PhaserSeconds,
        SirenMode.HiLo => HiLoHoldSeconds * 2f,
        _ => 0f,
    };

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The 100 W head on most North American patrol cars: a single driver in a 200 mm horn behind
    /// the grille, 650-1450 Hz, 120 dB at ten feet. Wail, yelp and phaser off one oscillator.
    /// </summary>
    public static SirenSpec Patrol100W => new()
    {
        Name = "100 W patrol siren, grille horn",
        ReferenceDbAt3m = 120f,
        // An eleven-inch speaker assembly, as a 100 W siren has: a 390 Hz cutoff, so the bottom of the
        // wail radiates.
        HornMouthMetres = 0.28f,
        DriverTopHz = 4500f,
        Compression = 0.55f,
        // Down to 500: a 650 bottom a quarter-octave over the cutoff stopped sounding like it was going
        // down (Cody: "on the down wail I think it should go a little lower").
        SweepLowHz = 500f, SweepHighHz = 1500f,
        WailSeconds = 5.0f, YelpSeconds = 0.31f, PhaserSeconds = 0.10f,
    };

    /// <summary>
    /// A 200 W head with two horns, as fitted to fire apparatus and larger ambulances: a bigger
    /// mouth, so it reaches lower, and six decibels more of it.
    /// </summary>
    public static SirenSpec Apparatus200W => new()
    {
        Name = "200 W apparatus siren, twin horn",
        ReferenceDbAt3m = 126f,
        HornMouthMetres = 0.38f,
        DriverTopHz = 4000f,
        Compression = 0.65f,
        SweepLowHz = 420f, SweepHighHz = 1350f,
        WailSeconds = 5.6f, YelpSeconds = 0.33f, PhaserSeconds = 0.11f,
    };

    /// <summary>The European two-tone: a smaller horn, and it never sweeps.</summary>
    public static SirenSpec EuropeanTwoTone => new()
    {
        Name = "European two-tone",
        ReferenceDbAt3m = 118f,
        // Wide enough to radiate its 435 Hz low note (a 240 mm mouth cuts off at 455): a note under the
        // cutoff sounds absent (TheBottomOfTheWailIsAboveTheHornsCutoff).
        HornMouthMetres = 0.30f,
        DriverTopHz = 4200f,
        Compression = 0.5f,
        HiLoLowHz = 435f, HiLoRatio = 1.5f, HiLoHoldSeconds = 0.55f,
    };

    public static IReadOnlyDictionary<string, Func<SirenSpec>> Presets { get; } =
        new Dictionary<string, Func<SirenSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["patrol"] = () => Patrol100W,
            ["apparatus"] = () => Apparatus200W,
            ["two_tone"] = () => EuropeanTwoTone,
        };

    public static SirenSpec ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No siren preset '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}

/// <summary>
/// Which sound a siren head is making, decided from what the vehicle is doing; nothing on the wire
/// carries a mode. A crew's choice has hysteresis: read straight off the deceleration, the head flipped
/// between wail and yelp several times a city lap ("the sirens are still wrong on the map"). So the
/// speed is smoothed, the braking must be sustained, and the choice is held. In Common so tests can
/// drive it against the city's racing line.
/// </summary>
public sealed class SirenController
{
    /// <summary>Under this, the vehicle is parked and the head is off.</summary>
    public float MovingMps { get; init; } = 2f;
    /// <summary>Deceleration that counts as coming up on something, m/s². High, so only the hardest
    /// braking on the route counts: taking every corner as a junction changed the mode nine times in two
    /// laps (ASirenDoesNotChangeItsMindEveryCorner).</summary>
    public float BrakingMps2 { get; init; } = 2.3f;
    /// <summary>How long it has to keep braking before the crew reaches for the switch.</summary>
    public float SustainSeconds { get; init; } = 0.9f;
    /// <summary>The shortest a chosen mode lasts: heard as a mode, not a glitch.</summary>
    public float HoldSeconds { get; init; } = 6f;
    /// <summary>Time constant on the speed the decision looks at: a dead-reckoned network speed
    /// differenced raw is mostly noise.</summary>
    public float SmoothSeconds { get; init; } = 0.35f;

    /// <summary>
    /// How long a call lasts and how long the car goes about its business between calls, seconds. The
    /// head is 130 dB and the car 95, so a siren that never stops hides the engine: the answer is off
    /// most of the time, as a real one is, not quieter.
    /// </summary>
    public float CallSecondsMin { get; init; } = 35f;
    public float CallSecondsMax { get; init; } = 80f;
    public float QuietSecondsMin { get; init; } = 70f;
    public float QuietSecondsMax { get; init; } = 160f;

    public SirenMode Mode { get; private set; } = SirenMode.Off;
    /// <summary>Whether the car is running a call at all. False most of the time.</summary>
    public bool OnCall { get; private set; }
    /// <summary>How many times the mode has changed. For tests and for the trace.</summary>
    public int Changes { get; private set; }

    private readonly Random _rng;
    private float _speed = float.NaN, _decel, _braking, _held = float.MaxValue, _phaseLeft;

    /// <summary>Seeded per vehicle, so two cars are never in step and a fault is reproducible.</summary>
    public SirenController(int seed = 0)
    {
        _rng = new Random(seed * 2654435761u.GetHashCode() ^ 0x5f3a);
        _phaseLeft = Lerp(QuietSecondsMin, QuietSecondsMax, (float)_rng.NextDouble()) * (float)_rng.NextDouble();
    }

    public SirenMode Update(float speedMps, float dt)
    {
        if (dt <= 0f) return Mode;
        if (float.IsNaN(_speed)) _speed = speedMps;

        float a = MathF.Min(1f, dt / MathF.Max(1e-3f, SmoothSeconds));
        float prev = _speed;
        _speed += (speedMps - _speed) * a;
        _decel += (((prev - _speed) / dt) - _decel) * a;
        _braking = _decel > BrakingMps2 ? _braking + dt : 0f;
        _held += dt;

        // A call is not a property of the road, so it is a clock.
        _phaseLeft -= dt;
        if (_phaseLeft <= 0f)
        {
            OnCall = !OnCall;
            _phaseLeft = OnCall ? Lerp(CallSecondsMin, CallSecondsMax, (float)_rng.NextDouble())
                                : Lerp(QuietSecondsMin, QuietSecondsMax, (float)_rng.NextDouble());
            _held = float.MaxValue;   // let the mode change on the instant a call starts or ends
        }

        if (!OnCall || _speed < MovingMps) { Set(SirenMode.Off); return Mode; }
        if (Mode == SirenMode.Off) { Set(Cruising()); return Mode; }
        if (_held < HoldSeconds) return Mode;

        // Coming up on a junction: a fast sweep, easier to place. Which one is a crew's habit, so drawn.
        Set(_braking >= SustainSeconds
            ? (_rng.NextDouble() < 0.35 ? SirenMode.Phaser : SirenMode.Yelp)
            : Cruising());
        return Mode;
    }

    /// <summary>What it runs between junctions: mostly the wail, sometimes the yelp.</summary>
    private SirenMode Cruising() => _rng.NextDouble() < 0.22 ? SirenMode.Yelp : SirenMode.Wail;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private void Set(SirenMode m)
    {
        if (m == Mode) return;
        Mode = m;
        _held = 0f;
        Changes++;
    }
}

/// <summary>
/// The beeper on a bus door while it kneels and the doors are open: a piezo disc driven at its own
/// resonance, so nearly a pure tone with a hard edge, at 2.5-3 kHz where the ear is most sensitive and
/// a diesel weakest. Heard from the pavement; it is not the inside of the bus.
/// </summary>
public sealed record DoorChimeSpec
{
    /// <summary>The piezo's own resonance, Hz. Everything it radiates is here and at its octave.</summary>
    public float ToneHz { get; init; } = 2730f;
    /// <summary>Beeps per second.</summary>
    public float RateHz { get; init; } = 2.0f;
    /// <summary>How much of each cycle is sounding, 0..1.</summary>
    public float Duty { get; init; } = 0.45f;
    /// <summary>Rise and fall of each beep, seconds: an instant edge is a click.</summary>
    public float EdgeSeconds { get; init; } = 0.004f;
    /// <summary>SPL at one metre: 80 dB, the usual figure, heard across a pavement and no further.</summary>
    public float ReferenceDb { get; init; } = 80f;
    /// <summary>How much of the square's second harmonic survives the narrow resonator: the warning's
    /// edge.</summary>
    public float SecondHarmonic { get; init; } = 0.3f;

    /// <summary>A transit bus's door beeper.</summary>
    public static DoorChimeSpec TransitBus => new();
}
