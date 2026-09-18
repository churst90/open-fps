using System;
using System.Collections.Generic;

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
    public float EffectiveLengthMetres => LengthMetres + 0.6f * 0.5f * MouthDiameterMetres;
    /// <summary>The note, hertz, at 20 C. A full cone: all harmonics of c/2L.</summary>
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
        SupplyKPa = 827f, ReferenceDb = 126f,
    };

    /// <summary>A transit bus: one small trumpet under the front, on the brake system's air.</summary>
    public static ChimeHornSpec BusAirHorn => new()
    {
        Name = "transit bus, single trumpet",
        Bells = new[]
        {
            new ChimeBellSpec { LengthMetres = 0.300f, MouthDiameterMetres = 0.070f, ThroatDiameterMetres = 0.018f, StartDelaySeconds = 0f },
        },
        SupplyKPa = 760f, ReferenceDb = 118f, RiseSeconds = 0.05f, FallSeconds = 0.07f,
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

/// <summary>One bell of a steam whistle: a tube closed at the top, blown across a gap at the bottom.</summary>
public sealed record WhistleBellSpec
{
    /// <summary>The sounding length from the lip to the closed top, metres.</summary>
    public required float LengthMetres { get; init; }
    public float BoreMetres { get; init; } = 0.075f;
    /// <summary>Level adjustment against the others, dB.</summary>
    public float LevelTrimDb { get; init; }

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
