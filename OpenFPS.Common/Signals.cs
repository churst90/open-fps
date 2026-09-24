using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OpenFPS.Common;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  Things built to be heard: air horns, steam whistles and struck bells.
//
//  Same rule as Engines.cs and Aircraft.cs — a part with dimensions, never a note. A horn is a
//  length of tapered pipe with a steel reed across its throat, and its note is c/2L, so the chord a
//  five-chime plays is a consequence of five lengths of brass and nothing declares it. A steam
//  whistle is a pipe CLOSED at the top, so it sounds c/4L and its odd harmonics, and it sits a long
//  way sharp of the same length in air because the sound speed in hot steam is half as much again as
//  in air — which is also why a whistle rises in pitch through its first second, as the bell fills
//  and warms. A bell is a shell, and what makes it a bell rather than a drum is that a shell's modes
//  are not harmonic.
//
//  The one number in each that is not a dimension is the level anchor (ReferenceDb): the SPL at one
//  metre, on axis, blown properly. Radiation integrals for a flaring horn are not worth pretending
//  to know to a decibel, and a locomotive horn has a legal level, which is a better figure than
//  anything I would derive.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>One bell of a chime horn: a tapered pipe with a reed across its small end.</summary>
public sealed record ChimeBellSpec
{
    /// <summary>Throat to mouth along the axis, metres. THIS is the note: a horn flaring from a
    /// small throat behaves like a full cone, so it sounds c/2L and all of its harmonics — unlike a
    /// cylinder with a reed on it, which sounds c/4L and only the odd ones.</summary>
    public required float LengthMetres { get; init; }
    /// <summary>The mouth, metres. Sets the end correction, the horn's low cutoff and how hard it
    /// beams: a horn is a directional thing, which is why one coming at you is bright and the same
    /// horn going away is dull.</summary>
    public float MouthDiameterMetres { get; init; } = 0.11f;
    /// <summary>The throat, metres, where the reed sits.</summary>
    public float ThroatDiameterMetres { get; init; } = 0.022f;
    /// <summary>When air reaches this bell relative to the first, seconds. The manifold does not
    /// feed them all at once and they do not start together — the little upward smear at the
    /// beginning of a horn blast, and its mirror at the end.</summary>
    public float StartDelaySeconds { get; init; }
    /// <summary>Per-bell level adjustment, dB. Big bells breathe more air and are louder.</summary>
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
    public required string Name { get; init; }
    public required ChimeBellSpec[] Bells { get; init; }
    /// <summary>Supply pressure, kPa gauge. A locomotive blows its horn on 140 psi of main
    /// reservoir air; a truck on 120. Pressure decides how hard the reed is driven, which decides
    /// the harmonics — and it is why a horn on a leaking line goes flat and breathy.</summary>
    public float SupplyKPa { get; init; } = 965f;
    /// <summary>
    /// The reed's own resonance as a multiple of the bell's note, and it is BELOW it.
    ///
    /// A diaphragm lying over a port is an outward-striking valve: pressure in the throat pushes it
    /// back onto its seat. Such a valve only pumps energy into a pipe when it is driven ABOVE its
    /// own resonance, where it is mass-controlled and its motion lags the pressure by half a cycle —
    /// the same reason a brass player's lips buzz below the note they are playing, and why a
    /// floppier diaphragm plays a lower chime rather than a duller one. Tune the reed above the
    /// column instead and the whole thing damps and you get a hiss; that is exactly what the first
    /// version of this model did, and the measured note in Describe() is what caught it.
    /// </summary>
    public float ReedRatio { get; init; } = 0.62f;
    /// <summary>
    /// How much of each cycle the diaphragm is off its seat at full blow, 0..1.
    ///
    /// A beating valve makes a pulse, and the pulse's width is its timbre: a wide pulse is little
    /// more than a half-wave sine — fundamental, an octave, and almost nothing above the third
    /// harmonic, the mellow round tone a locomotive CHIME is built to make, with its big soft
    /// diaphragm over a wide port. A stiff small diaphragm over a narrow port lifts late and slams
    /// back early; its pulse is short, and a short pulse keeps its harmonics level up to about the
    /// reciprocal of its width. That is the difference between a chime and a trumpet: the same
    /// column, blatted instead of blown.
    /// </summary>
    public float ReedOpenFraction { get; init; } = 0.46f;
    /// <summary>How long the valve takes to reach full pressure, seconds, and to fall.</summary>
    public float RiseSeconds { get; init; } = 0.09f;
    public float FallSeconds { get; init; } = 0.13f;
    /// <summary>SPL at one metre on axis with every bell blowing. A locomotive horn is required to
    /// make 96-110 dBA at 100 feet ahead of it, which is 125-140 at a metre.</summary>
    public float ReferenceDb { get; init; } = 138f;

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Nathan AirChime K5LA: the five-chime on most Amtrak power and a great many freight
    /// locomotives, and the sound most people in North America mean by "train horn". Five bells from
    /// 518 mm down to 282 mm, which come out as a D#-F#-G#-A#-C# — a minor chord with the fourth in
    /// it, and the reason it is mournful rather than triumphant. The bells are spread across the
    /// manifold and speak over about forty milliseconds, so it swells into the chord.
    /// </summary>
    public static ChimeHornSpec NathanK5LA => new()
    {
        Name = "Nathan AirChime K5LA, five chime",
        Bells = new[]
        {
            new ChimeBellSpec { LengthMetres = 0.518f, MouthDiameterMetres = 0.110f, ThroatDiameterMetres = 0.024f, StartDelaySeconds = 0.000f, LevelTrimDb = 0f },
            new ChimeBellSpec { LengthMetres = 0.431f, MouthDiameterMetres = 0.102f, ThroatDiameterMetres = 0.023f, StartDelaySeconds = 0.012f, LevelTrimDb = -1f },
            new ChimeBellSpec { LengthMetres = 0.383f, MouthDiameterMetres = 0.098f, ThroatDiameterMetres = 0.022f, StartDelaySeconds = 0.020f, LevelTrimDb = -1.5f },
            new ChimeBellSpec { LengthMetres = 0.338f, MouthDiameterMetres = 0.094f, ThroatDiameterMetres = 0.022f, StartDelaySeconds = 0.030f, LevelTrimDb = -2f },
            new ChimeBellSpec { LengthMetres = 0.282f, MouthDiameterMetres = 0.088f, ThroatDiameterMetres = 0.021f, StartDelaySeconds = 0.040f, LevelTrimDb = -3f },
        },
        SupplyKPa = 965f, ReferenceDb = 139f,
    };

    /// <summary>
    /// A Leslie three-chime, the other voice of North American railroading: fewer bells, wider
    /// spacing, and a harder edge because the bells are shorter for their mouths. Common on transit
    /// and on older passenger power.
    /// </summary>
    public static ChimeHornSpec LeslieRS3L => new()
    {
        Name = "Leslie RS3L, three chime",
        Bells = new[]
        {
            new ChimeBellSpec { LengthMetres = 0.706f, MouthDiameterMetres = 0.125f, ThroatDiameterMetres = 0.026f, StartDelaySeconds = 0f },
            new ChimeBellSpec { LengthMetres = 0.588f, MouthDiameterMetres = 0.116f, ThroatDiameterMetres = 0.025f, StartDelaySeconds = 0.010f, LevelTrimDb = -1f },
            new ChimeBellSpec { LengthMetres = 0.459f, MouthDiameterMetres = 0.106f, ThroatDiameterMetres = 0.024f, StartDelaySeconds = 0.022f, LevelTrimDb = -2f },
        },
        SupplyKPa = 965f, ReferenceDb = 137f,
    };

    /// <summary>
    /// A European two-tone: a high and a low a fourth apart, sounded together or alternately. Much
    /// shorter bells than an American five-chime, so it sits an octave up and cuts rather than
    /// mourns.
    /// </summary>
    public static ChimeHornSpec TwoToneEuropean => new()
    {
        Name = "two-tone, high and low",
        Bells = new[]
        {
            new ChimeBellSpec { LengthMetres = 0.243f, MouthDiameterMetres = 0.080f, ThroatDiameterMetres = 0.020f, StartDelaySeconds = 0f },
            new ChimeBellSpec { LengthMetres = 0.182f, MouthDiameterMetres = 0.072f, ThroatDiameterMetres = 0.019f, StartDelaySeconds = 0.006f, LevelTrimDb = -1f },
        },
        SupplyKPa = 800f, ReferenceDb = 131f,
    };

    /// <summary>
    /// The pair of trumpets on the roof of a tractor unit, pulled with a lanyard: 0.47 m and 0.35 m,
    /// a fifth apart, on 120 psi of the same air that works the brakes. Less pressure and smaller
    /// mouths than a locomotive's, so it is louder in the top than at the bottom and carries nothing
    /// like as far.
    /// </summary>
    public static ChimeHornSpec TruckDualTrumpet => new()
    {
        Name = "tractor unit, dual trumpet",
        Bells = new[]
        {
            new ChimeBellSpec { LengthMetres = 0.470f, MouthDiameterMetres = 0.086f, ThroatDiameterMetres = 0.020f, StartDelaySeconds = 0f },
            new ChimeBellSpec { LengthMetres = 0.352f, MouthDiameterMetres = 0.078f, ThroatDiameterMetres = 0.019f, StartDelaySeconds = 0.004f, LevelTrimDb = -1.5f },
        },
        SupplyKPa = 827f, ReferenceDb = 126f, ReedOpenFraction = 0.30f,
    };

    /// <summary>A transit bus: one small trumpet under the front, on the brake system's air.</summary>
    public static ChimeHornSpec BusAirHorn => new()
    {
        Name = "transit bus, single trumpet",
        Bells = new[]
        {
            new ChimeBellSpec { LengthMetres = 0.300f, MouthDiameterMetres = 0.070f, ThroatDiameterMetres = 0.018f, StartDelaySeconds = 0f },
        },
        SupplyKPa = 760f, ReferenceDb = 118f, RiseSeconds = 0.05f, FallSeconds = 0.07f, ReedOpenFraction = 0.30f,
    };

    public static IReadOnlyDictionary<string, Func<ChimeHornSpec>> Presets { get; } =
        new Dictionary<string, Func<ChimeHornSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["k5la"] = () => NathanK5LA,
            ["rs3l"] = () => LeslieRS3L,
            ["two_tone"] = () => TwoToneEuropean,
            ["truck_dual"] = () => TruckDualTrumpet,
            ["bus_horn"] = () => BusAirHorn,
        };

    public static ChimeHornSpec ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No horn preset '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  The electric horn: what nearly every car, pickup and motorcycle has under its grille.
//
//  Not an air horn. There is no air supply and no reed: it is a BUZZER — a coil, an iron armature
//  riveted to a steel diaphragm, and a pair of contact points the armature itself pushes open. The
//  coil pulls, the armature moves, the points open, the coil lets go, the diaphragm springs back, the
//  points close, and the coil pulls again. The note is near the diaphragm's own resonance and is set
//  at the factory with an adjusting screw on the points, which is why horns are sold as a nominal
//  "H" and "L" and why the note here is DECLARED rather than derived: it is a setting, not a length.
//
//  What makes it a car horn rather than a doorbell is that the armature is set to STRIKE the pole
//  piece every cycle. Steel on steel with almost no bounce, four or five hundred times a second: a
//  hard stop is where the buzz comes from. Two ways of letting that out:
//
//    DISC — a flat spring-steel tone disc on the end of the armature, which rings at its own modes
//    around 2-4 kHz each time the armature hits. That is the brassy, nasal formant of a normal car
//    horn, and the reason two of them a third apart sound like a car and not like a chord.
//
//    TRUMPET (snail) — the diaphragm drives a coiled exponential horn instead. The column only lets
//    out what is near its own resonances, n·c/2L, and nothing below its flare cutoff, so the strike
//    is filtered into a rounder, louder note. The "European" horn on a Mercedes or a Fiat.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

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
    /// <summary>
    /// The note, hertz. Set at the factory by the screw on the contact points, near the
    /// diaphragm-and-armature's own resonance; the label on the horn is "H" or "L" and this.
    /// Declared, because it is a SETTING — nothing about the steel would tell you which way the
    /// screw was turned.
    /// </summary>
    public required float Hz { get; init; }
    /// <summary>Level of this horn against the others in the set, dB. The low horn of a pair is
    /// usually the bigger and a decibel or two louder.</summary>
    public float LevelTrimDb { get; init; }
    /// <summary>The diaphragm, metres. Sets where the radiator starts to beam.</summary>
    public float DiaphragmDiameterMetres { get; init; } = 0.090f;
    /// <summary>
    /// DISC horns: the tone disc's first ringing mode, hertz. A disc clamped at its centre and free
    /// at its rim; its note depends on diameter, thickness and the dish pressed into it, and the
    /// makers do not publish any of the three, so it is declared from what disc horns measure at:
    /// 2-4 kHz. The disc's next axisymmetric mode is about 6.3 times higher (a clamped-free plate
    /// behaves like a cantilever there), which is mostly past hearing.
    /// </summary>
    public float ToneDiscHz { get; init; } = 2600f;
    /// <summary>TRUMPET horns: the mouth of the coiled horn, metres.</summary>
    public float MouthDiameterMetres { get; init; } = 0.075f;
    /// <summary>TRUMPET horns: the throat where the diaphragm's chamber opens into it, metres.</summary>
    public float ThroatDiameterMetres { get; init; } = 0.010f;

    /// <summary>TRUMPET horns: the length of the coiled column. It is cut to the note — a trumpet
    /// horn's column and its diaphragm are made to agree — so this is c/2f less the mouth's end
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
    /// How long the supply takes to come up at the horn when the button is pressed, seconds (a time
    /// constant). The horn is not wired to the button: the button pulls in a relay, the relay's
    /// armature travels, its contacts touch, bounce and seat, and the battery then pushes several
    /// amps through the harness into a coil that is still settling. The diaphragm meanwhile builds
    /// from rest over its first few cycles, swinging short of the pole until the pull is strong
    /// enough to throw it all the way. Together that is a swell of a couple of dozen milliseconds —
    /// short enough to be heard as instant, long enough not to be a click.
    /// </summary>
    public float RelayMakeSeconds { get; init; } = 0.010f;
    /// <summary>
    /// How long the supply takes to die away when the button is let go, seconds (a time constant).
    /// A horn relay's coil carries a suppression diode, which lets its current — and so its grip —
    /// decay slowly rather than snap; the contacts part while the arc between them still carries a
    /// falling current; and the buzzer keeps interrupting all the way down, striking softer and then
    /// not at all as the pull fades, until the diaphragm and the tone disc ring down on their own.
    /// </summary>
    public float RelayBreakSeconds { get; init; } = 0.018f;
    /// <summary>
    /// SPL, RMS, at one metre on axis with every horn in the set sounding. Legal horns are
    /// 93-112 dBA at two metres (ECE R28, FMVSS), which is 99-118 at one. An anchor rather than a
    /// radiation integral, for the same reason as the air horns: the law is a better number.
    /// </summary>
    public float ReferenceDb { get; init; } = 110f;

    /// <summary>The notes of the set, hertz, in the order the units are declared.</summary>
    [JsonIgnore]
    public float[] Notes => Array.ConvertAll(Units, u => u.Hz);

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pair behind the grille of most cars and pickups: a high and a low disc horn, about 510 and
    /// 410 Hz, roughly a major third apart. They beat against each other, and that roughness is the
    /// sound of a car horn as much as either note is.
    /// </summary>
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
    public required float LengthMetres { get; init; }
    public float BoreMetres { get; init; } = 0.075f;
    /// <summary>Level adjustment against the others, dB.</summary>
    public float LevelTrimDb { get; init; }

    [JsonIgnore]
    public float EffectiveLengthMetres => LengthMetres + 0.3f * BoreMetres;
    /// <summary>The note at a given sound speed. A STOPPED pipe: c/4L, odd harmonics.</summary>
    public float HzAt(float soundSpeed) => soundSpeed / (4f * MathF.Max(0.02f, EffectiveLengthMetres));
}

/// <summary>A steam whistle: one or more stopped bells over a common annular steam jet.</summary>
public sealed record WhistleSpec
{
    public required string Name { get; init; }
    public required WhistleBellSpec[] Bells { get; init; }
    /// <summary>Boiler pressure at the whistle valve, kPa gauge. Sets the jet speed and so how hard
    /// the whistle is driven and how much of it is noise rather than note.</summary>
    public float SupplyKPa { get; init; } = 1380f;
    /// <summary>The steam's temperature once the bell is hot, Kelvin. This sets the sound speed in
    /// the bell, and so the PITCH: steam at 430 K carries sound at about 510 m/s against air's 343,
    /// so a whistle sounds half again as sharp as a pipe organ of the same length.</summary>
    public float SteamKelvin { get; init; } = 430f;
    /// <summary>How long the bell takes to fill and warm, seconds. The first moment of a whistle is
    /// cold air, and the pitch climbs as the steam displaces it — the wail into the note.</summary>
    public float WarmSeconds { get; init; } = 0.55f;
    /// <summary>How much of the output is the jet's turbulence rather than the pipe's note, 0..1.
    /// A big chime whistle on wet steam is half noise, which is why it sounds like weather.</summary>
    public float Breathiness { get; init; } = 0.4f;
    public float ReferenceDb { get; init; } = 132f;

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A big American three-chime passenger whistle, the kind on a Northern: three stopped bells of
    /// 300, 238 and 200 mm which at 510 m/s sound 400, 500 and 590 Hz — a rough minor triad, and
    /// deliberately not a clean one. Odd harmonics from the stopped pipes and a great deal of jet
    /// noise; it is a chord and a roar at the same time.
    /// </summary>
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

    /// <summary>
    /// A five-chime freight whistle: five short bells, higher and harder, meant to be heard over a
    /// mile of coal train. More bells is not more chord — it is more beating between bells that are
    /// close together, which is where the "hollow" in a big whistle comes from.
    /// </summary>
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

    /// <summary>
    /// A single-note "banshee" hooter: one long bell, no chord at all, and nothing to hide behind.
    /// A 560 mm stopped pipe sounds 215 Hz, which is low enough to carry for miles and mournful
    /// enough that railroads that used them are remembered for it.
    /// </summary>
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
/// A struck bell — a crossing gong, a locomotive bell, a tram's foot gong.
///
/// What makes a bell a bell is that it is a SHELL and not a plate: the modes of a flat disc are one
/// family, and curving it adds a membrane stiffness that pushes the low modes up and leaves the high
/// ones nearly alone. So the partials of a bell are neither harmonic nor the plate's — they are
/// somewhere between, and how far between is the rise of the dome. Flatten a gong and it becomes a
/// cymbal; deepen it and it becomes a church bell.
/// </summary>
public sealed record StruckBellSpec
{
    public required string Name { get; init; }
    /// <summary>The mouth, metres. A grade-crossing gong is 250-300 mm; a locomotive bell 400.</summary>
    public required float DiameterMetres { get; init; }
    /// <summary>Wall thickness at the rim, metres. With the diameter this is the whole note: the
    /// plate family goes as h/a^2, so halving the diameter of the same casting raises it two
    /// octaves.</summary>
    public required float ThicknessMetres { get; init; }
    /// <summary>How deep the dome is, metres — the rise of the crown above the rim. Zero is a flat
    /// plate. The membrane stiffness it adds is what separates a gong from a bell.</summary>
    public float RiseMetres { get; init; } = 0.05f;
    /// <summary>Young's modulus (Pa), density (kg/m^3): bell bronze 105 GPa and 8800, steel 210 and
    /// 7850. Bronze is slower and denser, so a bronze bell of the same size is lower — and it has an
    /// internal loss twenty times smaller than steel's, which is why it rings for seconds.</summary>
    public float YoungsPa { get; init; } = 105e9f;
    public float DensityKgM3 { get; init; } = 8800f;
    /// <summary>Internal loss factor. Bell bronze is about 3e-5; cast iron 1e-3; steel 2e-4.</summary>
    public float LossFactor { get; init; } = 4e-5f;
    /// <summary>Where the clapper lands as a fraction of the radius: 1 is the rim, 0 the crown. The
    /// strike point decides which modes answer — a bell struck at its crown is a thud.</summary>
    public float StrikeRadiusFraction { get; init; } = 0.92f;
    /// <summary>The clapper: mass in kg and how fast it arrives, m/s. The contact time follows from
    /// the Hertzian stiffness of steel on bronze and it is what sets the brightness — a soft heavy
    /// clapper cannot excite a mode whose period is shorter than the contact.</summary>
    public float ClapperKg { get; init; } = 0.35f;
    public float ClapperMps { get; init; } = 2.2f;
    /// <summary>Whether the clapper stays on the bell after the blow (an electric gong's does not;
    /// a hand bell's often does), 0..1 of the ring damped away.</summary>
    public float ClapperDamping { get; init; } = 0.06f;
    /// <summary>How much the mounting takes out of it. A gong is bolted through its crown, which
    /// is a node for every mode with two or more nodal diameters and an antinode for the ones with
    /// none — so the bolt kills the low breathing modes and barely touches the ones that sing. That
    /// is why a bell screwed to a mast still rings.</summary>
    public float MountLossFactor { get; init; } = 0.02f;
    /// <summary>Strikes a second when it is ringing continuously. A North American crossing gong
    /// runs at about 2.3.</summary>
    public float StrikesPerSecond { get; init; } = 2.3f;
    /// <summary>
    /// SPL at one metre, RMS, while it is RINGING — not the height of one blow. That is what a
    /// bell's published figure means: a crossing gong is required to make about 75 dB at three
    /// metres and a locomotive bell 80 at thirty, and both of those are meter readings of a bell in
    /// use. A struck bell's crest factor is twenty-odd decibels, so anchoring the peak instead
    /// hides it completely under anything else that is happening.
    /// </summary>
    public float ReferenceDb { get; init; } = 86f;

    /// <summary>Longitudinal wave speed in the metal, m/s.</summary>
    [JsonIgnore]
    public float PlateWaveSpeed => MathF.Sqrt(YoungsPa / DensityKgM3);

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The grade-crossing gong: a 260 mm bronze casting on a shallow dome, rung by a solenoid about
    /// twice a second, mounted on a mast under the flashers. Small, thin, and struck hard, so it is
    /// bright and its partials are wide apart — it is the least bell-like bell on the railway and
    /// carries a mile down a quiet street.
    /// </summary>
    public static StruckBellSpec CrossingGong => new()
    {
        Name = "grade-crossing gong, 10 inch bronze",
        DiameterMetres = 0.260f, ThicknessMetres = 0.0045f, RiseMetres = 0.036f,
        YoungsPa = 105e9f, DensityKgM3 = 8800f, LossFactor = 5e-5f,
        StrikeRadiusFraction = 0.94f, ClapperKg = 0.22f, ClapperMps = 2.6f,
        ClapperDamping = 0.05f, StrikesPerSecond = 2.3f, ReferenceDb = 86f,
    };

    /// <summary>
    /// The bell on the front of a locomotive: 400 mm of bronze, thick, swung or air-rung at about
    /// 1.6 a second. Twice the gong's diameter and three times its wall, so it sounds an octave and
    /// a half lower and holds on much longer.
    /// </summary>
    public static StruckBellSpec LocomotiveBell => new()
    {
        Name = "locomotive bell, 16 inch bronze",
        DiameterMetres = 0.400f, ThicknessMetres = 0.013f, RiseMetres = 0.115f,
        YoungsPa = 105e9f, DensityKgM3 = 8800f, LossFactor = 3e-5f,
        StrikeRadiusFraction = 0.90f, ClapperKg = 1.1f, ClapperMps = 2.0f,
        ClapperDamping = 0.10f, StrikesPerSecond = 1.6f, ReferenceDb = 110f,
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

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  THE ELECTRONIC SIREN
//
//  Everything above this line is a pneumatic instrument — air through a reed into a pipe. A police
//  siren is not: it is an amplifier driving a compression driver into a horn, and every part of
//  what it sounds like comes from that chain rather than from a recording of one.
//
//    THE OSCILLATOR. A siren amplifier's tone generator does not make a sine. The classic heads
//    (and the DSP ones that imitate them) put out a sawtooth, because a sawtooth has every harmonic
//    and a siren's whole job is to be heard through traffic — a sine at 900 Hz disappears behind a
//    bus and a sawtooth at 900 Hz does not. What SWEEPS is that oscillator's frequency, and the
//    different "sounds" on a siren head are nothing but different sweep rates over the same range.
//
//    THE DRIVER. A hundred watts into a one-inch compression driver is well past where the
//    diaphragm moves linearly, so the wave is squashed on the way out. That is why a siren at full
//    power has a hard, brassy edge that the same head at low volume does not.
//
//    THE HORN. This is the part that makes it a siren rather than a loudspeaker. A horn will not
//    radiate below its flare cutoff — the mouth has to be about a wavelength over pi across before
//    the air will take the energy — so everything under about five hundred hertz is simply gone,
//    which is why a siren has no body at all and is all bite. At the top, the driver's diaphragm
//    mass rolls it off above four or five kilohertz. What is left is a band from roughly 500 Hz to
//    4 kHz: exactly where the ear is most sensitive and where engine and tyre noise are weakest.
//    Nothing about that band is an equaliser setting; it is the geometry of the horn.
//
//    AND IT POINTS FORWARD. A horn under a bumper beams. Ten decibels front to back, which is why
//    you hear one coming long before it is a problem and why it drops away so fast once past.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Which sound the head is making. The hardware is identical; only the sweep rate changes.</summary>
public enum SirenMode
{
    /// <summary>Off.</summary>
    Off,
    /// <summary>The long one: about twelve sweeps a minute. What a car uses on an open road.</summary>
    Wail,
    /// <summary>Three sweeps a second. What it changes to at a junction, because a fast sweep is far
    /// easier to localise — the ear gets many onsets a second instead of one every five.</summary>
    Yelp,
    /// <summary>Ten sweeps a second: the hard stutter, for the last few metres when nobody has moved.</summary>
    Phaser,
    /// <summary>Two fixed tones a fifth apart, alternating about twice a second. The European voice.</summary>
    HiLo,
}

/// <summary>
/// An electronic siren head: an amplifier, a compression driver and a horn.
///
/// Levels are anchored the way certification anchors them — the legal figure is measured at TEN
/// FEET on axis, not at a metre, because a metre from a horn mouth is inside its near field and
/// means nothing. <see cref="ReferenceDbAt3m"/> holds the spec figure and the model converts.
/// </summary>
public sealed record SirenSpec
{
    public required string Name { get; init; }

    /// <summary>
    /// On-axis SPL at ten feet (3.05 m), dB — the figure sirens are actually specified and type-
    /// approved at. California Title 13 and SAE J1849 want at least 110; a 100 W head on a modern
    /// speaker makes 118-123, and that is what these presets carry.
    /// </summary>
    public float ReferenceDbAt3m { get; init; } = 120f;

    /// <summary>Mouth diameter of the horn, metres. It SETS THE CUTOFF — see
    /// <see cref="FlareCutoffHz"/> — so a bigger horn is not a louder siren, it is a deeper one.</summary>
    public float HornMouthMetres { get; init; } = 0.20f;

    /// <summary>Where the compression driver runs out, Hz: diaphragm mass and the phase plug.</summary>
    public float DriverTopHz { get; init; } = 4500f;

    /// <summary>
    /// How hard the driver is being pushed, 0..1 — how much of the wave is squashed flat. A siren
    /// head at full volume is well into this and it is most of the brassiness.
    /// </summary>
    public float Compression { get; init; } = 0.55f;

    /// <summary>
    /// Duty cycle of the oscillator, 0..1 — and this is what decides whether it is a SQUARE or a
    /// sawtooth, which is the difference between a siren and a trumpet.
    ///
    /// The first version of this model used a sawtooth, on the reasoning that a sawtooth has every
    /// harmonic and a siren's job is to be heard. That is true of the job and wrong about the
    /// hardware. The instrument every electronic siren was built to imitate is a ROTARY CHOPPER —
    /// a rotor spinning inside a stator, both cut with ports, so the airflow is switched fully on
    /// and fully off once per port per revolution. Ports and lands are cut about equally wide, so
    /// what comes out is very nearly a square wave at fifty per cent duty, and a square wave has
    /// ODD HARMONICS ONLY: 1, 3, 5, 7, at 1/n. The analogue tone generators in the electronic heads
    /// that replaced it were built to match, and the modern DSP ones to match those.
    ///
    /// Odd-harmonic and all-harmonic are not a subtle difference. A sawtooth's even harmonics fill
    /// in the octave above every partial and the result reads as BRASSY — a horn, a trumpet. A
    /// square leaves those gaps open and reads as hollow and hard, which is the siren sound.
    /// Reported by ear before it was reasoned about: "are real sirens based on square waves or
    /// sawtooth waves? Sounds like these are sawtooth."
    ///
    /// Exactly a half is a pure odd series. Real ports are not machined perfectly, and a hair off
    /// centre puts a little of the even series back, which is what stops it sounding synthetic.
    ///
    /// A HAIR. The series amplitude is sin(pi k d)/(pi k), and how far d sits from a half is
    /// multiplied by k — so a two per cent error that is inaudible on the 2nd harmonic has grown
    /// eight times over by the 8th, and the odd-harmonic character quietly disappears up the
    /// series. Measured with duty 0.48: the 3rd stood 14 dB over the 2nd and the 5th only 8.7 dB
    /// over the 4th, which is halfway back to a sawtooth. One per cent holds it.
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

    /// <summary>
    /// The horn's flare cutoff, Hz — DERIVED, not declared. A horn radiates only once its mouth
    /// circumference is comparable with the wavelength: f = c / (pi * D). A 0.2 m mouth cuts off at
    /// 546 Hz, which is why a siren has no bottom end at all.
    /// </summary>
    public float FlareCutoffHz => 343f / (MathF.PI * MathF.Max(0.05f, HornMouthMetres));

    /// <summary>The spec figure carried back to one metre, for the emitter placement that wants it
    /// there. A near-field fiction, like a jet's, and honest about being one.</summary>
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
        // An eleven-inch speaker assembly, which is what a 100 W siren is fitted with — not the
        // eight inches the first version assumed. It matters twice over: the cutoff falls to
        // 390 Hz, so the bottom of the wail actually radiates instead of being filtered away, and
        // the beam is correspondingly wider at the low end.
        HornMouthMetres = 0.28f,
        DriverTopHz = 4500f,
        Compression = 0.55f,
        // 500 to 1500. The old 650 bottom sat only a quarter-octave over a 546 Hz cutoff, so the
        // wail's descent ran straight into the horn's own high-pass and stopped sounding like it
        // was going down — "on the down wail I think it should go a little lower".
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
        // Wide enough to radiate its own low note: a 435 Hz tone needs a cutoff below 435, and
        // a 240 mm mouth cuts off at 455. Caught by TheBottomOfTheWailIsAboveTheHornsCutoff,
        // which exists because a note under the cutoff does not sound low, it sounds absent.
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
/// Which sound a siren head is making, decided from what the vehicle is DOING.
///
/// Nothing on the wire carries a siren mode and nothing scripts one, which is the same rule the
/// aircraft power lever and the air brakes follow. But unlike those, this one is a PERSON'S
/// decision, and a person's decision has hysteresis in it: a crew that switches to yelp for a
/// junction holds it through the junction and out the other side. Read straight off the
/// instantaneous deceleration it does not — on a city lap the racing line brakes for every corner,
/// so the head flipped between wail and yelp several times a lap and sounded like it could not
/// make up its mind. That is what "the sirens are still wrong on the map" was.
///
/// So this smooths what it is looking at, requires the braking to be SUSTAINED rather than
/// momentary, and then holds whatever it chose. It lives here, in Common, rather than in the audio
/// system because a decision with state in it is a thing a test can drive — see the siren tests,
/// which run it against the city's own racing line and count the changes per lap.
/// </summary>
public sealed class SirenController
{
    /// <summary>Under this, the vehicle is parked and the head is off.</summary>
    public float MovingMps { get; init; } = 2f;
    /// <summary>
    /// Deceleration that counts as "coming up on something", m/s².
    ///
    /// High on purpose. A racing line brakes for every corner, and taking every corner as a
    /// junction is what made the head change character nine times in two laps — measured, in
    /// ASirenDoesNotChangeItsMindEveryCorner. Only the hardest braking on the route is a crew
    /// arriving somewhere; the rest is just driving round a block.
    /// </summary>
    public float BrakingMps2 { get; init; } = 2.3f;
    /// <summary>How long it has to keep braking before the crew reaches for the switch.</summary>
    public float SustainSeconds { get; init; } = 0.9f;
    /// <summary>
    /// The shortest a chosen mode lasts. Long enough to be recognised as a mode rather than as a
    /// glitch, short enough that a junction gets its own sound.
    /// </summary>
    public float HoldSeconds { get; init; } = 6f;
    /// <summary>Time constant on the speed the decision looks at. A network speed is a sampled,
    /// dead-reckoned quantity and differencing it raw is mostly noise.</summary>
    public float SmoothSeconds { get; init; } = 0.35f;

    /// <summary>
    /// How long a call lasts and how long the car goes about its business between calls, seconds.
    ///
    /// THIS IS THE ONE THAT MATTERS. A patrol car with its siren on for ever is not a patrol car,
    /// and it is not what a street sounds like: the head is 130 dB and the car is 95, so a siren
    /// that never stops means the engine never exists. Reported exactly — "the police cars sound
    /// like they have no engine and they're all siren, just sounds like a siren driving by" — and
    /// the answer is not to turn the siren down (it is the right level, it is a siren) but to turn
    /// it OFF most of the time, which is what a real one is.
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

    /// <summary>Seeded per vehicle, so two cars on the same street are never in step — and so the
    /// same car does the same thing twice, which is what makes a fault reproducible.</summary>
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

        // On a call, or going about its business. Nothing observable decides this — a call is not
        // a property of the road — so it is a clock, seeded per car so no two are in step.
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

        // Coming up on a junction: the crew goes to a fast sweep, because a fast sweep is far
        // easier for anyone in the way to place. WHICH fast sweep is a person's habit rather than
        // a rule, so it is drawn rather than fixed — some crews yelp, some run the phaser.
        Set(_braking >= SustainSeconds
            ? (_rng.NextDouble() < 0.35 ? SirenMode.Phaser : SirenMode.Yelp)
            : Cruising());
        return Mode;
    }

    /// <summary>What it runs between junctions. Mostly the long one, occasionally not — a siren
    /// held on one sound for a whole shift is as wrong as one that changes every corner.</summary>
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
/// The beeper on a bus door — the "beep beep beep" while it kneels and the doors are open.
///
/// It is not pneumatic and it is not part of the air system; it is a piezo disc with a square wave
/// on it, behind a grille over the doorway. Which matters, because a piezo is a RESONATOR: it is
/// driven at its own mechanical resonance (that is the only place it is efficient) and what comes
/// out is very nearly a pure tone with a hard edge to it, not a buzzer's rasp. The frequency is
/// chosen high — two and a half to three kilohertz — for exactly the reason the siren's band is
/// chosen: it is where the ear is most sensitive and where a diesel is weakest, so it cuts through
/// the bus it is bolted to.
///
/// And it answers a question that comes up whenever this sort of thing is added: NO, none of this
/// is the inside of the bus. The kneel, the doors and this are all heard from the pavement — they
/// are what a bus does at a stop, from outside it. What the cabin does to the engine when you are
/// sitting IN one is a different model entirely.
/// </summary>
public sealed record DoorChimeSpec
{
    /// <summary>The piezo's own resonance, Hz. Everything it radiates is here and at its octave.</summary>
    public float ToneHz { get; init; } = 2730f;
    /// <summary>Beeps per second.</summary>
    public float RateHz { get; init; } = 2.0f;
    /// <summary>How much of each cycle is sounding, 0..1.</summary>
    public float Duty { get; init; } = 0.45f;
    /// <summary>Rise and fall of each beep, seconds. A piezo is light and starts fast, but not
    /// instantly, and a truly instant edge is a click rather than a beep.</summary>
    public float EdgeSeconds { get; init; } = 0.004f;
    /// <summary>SPL at one metre. A door beeper is made to be heard across a pavement and no
    /// further: 80 dB is the usual figure and it is what this is.</summary>
    public float ReferenceDb { get; init; } = 80f;
    /// <summary>How much of the square's second harmonic survives the disc. A piezo is a narrow
    /// resonator, so not much — but enough to give it the edge that makes it a warning.</summary>
    public float SecondHarmonic { get; init; } = 0.3f;

    /// <summary>A transit bus's door beeper.</summary>
    public static DoorChimeSpec TransitBus => new();
}
