using System;
using System.Collections.Generic;

namespace OpenFPS.Common;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  An aircraft, described as the machine it is — the same rule as Engines.cs.
//
//  Everything that flies makes its sound with two or three mechanisms, and they are the same
//  mechanisms whatever the aircraft:
//
//    BLADES. A propeller, a helicopter rotor, a turbofan's fan and a tail rotor are all a row of
//    blades sweeping past you. Each passage is a pressure pulse — the blade's thickness pushing the
//    air aside and its lift pulling on it — and the pulse train's period is the blade-passing rate.
//    How SHARP each pulse is is set by the tip Mach number toward the listener: at Mach 0.5 a blade
//    passage is a soft thump and the sound is nearly a sine at the blade rate; past Mach 0.8 the
//    observer-time compression squeezes each pulse toward a spike and the harmonics come flooding in.
//    That is why a Cessna at full power buzzes where the same prop at cruise hums, and why a fan whose
//    tips go supersonic makes the "buzz saw" — every blade's shock is a little different, so the
//    pattern repeats once per REVOLUTION and the comb of shaft harmonics appears. Nothing declares
//    any of that; it falls out of blade count, diameter and rpm.
//
//    JETS. A stream of hot gas mixing with still air is broadband noise whose power goes as the
//    EIGHTH power of the exit velocity (Lighthill) and whose spectrum peaks at Strouhal 0.2 on the
//    nozzle diameter — a few hundred hertz for a big engine, a kilohertz for a small one. It is loudest
//    thirty or forty degrees off the jet axis, behind. Twice the velocity is 24 dB, which is why a
//    turbofan at takeoff is a roar and the same engine at idle is a hiss.
//
//    THE CORE. Combustion is a low rumble that follows fuel flow; the compressor and turbine are
//    tones at blade-passing rates, most of them above hearing, one or two that are not. What you
//    hear of a helicopter's turboshaft is that whine and nothing else of the engine.
//
//  A piston aircraft is a car engine with a propeller on it and it uses the car engine: EngineKey
//  names an EngineProfile and the same synthesis integrates it, with the prop as its load.
//
//  Levels are anchored, not derived. The SHAPE and the way it changes with rpm, Mach and lever come
//  from the mechanism; the absolute level at one condition is a measured figure (ReferenceDb), the
//  way a tyre's is, because the constants in the radiation integrals are not worth pretending to know.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

public enum AircraftPower { Piston, Turboprop, Turbofan, Turboshaft }

/// <summary>A row of blades in rotation: a propeller, a fan, a main rotor or a tail rotor.</summary>
public sealed record BladeRowSpec
{
    public required int Blades { get; init; }
    public required float DiameterMetres { get; init; }
    /// <summary>Blade chord near the tip, metres. Sets the pulse width: chord over tip speed.</summary>
    public float ChordMetres { get; init; } = 0.15f;
    /// <summary>Thickness to chord of the tip section. Thickness noise scales with it.</summary>
    public float ThicknessRatio { get; init; } = 0.08f;
    public required float RpmMax { get; init; }
    /// <summary>The slowest the row turns while it is turning at all.</summary>
    public float RpmIdle { get; init; }
    /// <summary>Sound pressure level at one metre, in the plane of the disc, at RpmMax and full
    /// loading. The anchor; everything else is relative to it.</summary>
    public float ReferenceDb { get; init; } = 110f;
    /// <summary>How hard the blades meet the tip vortices of the blades ahead of them, 0..1. A rotor
    /// in a descent or fast forward flight slaps; a propeller or a hovering rotor does not.</summary>
    public float BladeVortexInteraction { get; init; }
    /// <summary>A fan inside a duct: heard forward out of the inlet, cut off behind by the core and
    /// bypass streams, and with the low harmonics the duct will not carry removed.</summary>
    public bool Ducted { get; init; }
    /// <summary>Per-blade differences in pitch and track, as a fraction, fixed for the life of the
    /// machine. Real rows are never identical, and the difference is the once-per-revolution "wow"
    /// under a propeller's note and the whole of the buzz-saw comb on a supersonic fan.</summary>
    public float BladeScatter { get; init; } = 0.015f;

    /// <summary>
    /// Broadband self-noise at one metre, in the disc plane, at <see cref="RpmMax"/> and full
    /// loading, dB. Zero means the row does not declare any.
    ///
    /// The pulse train above is the TONAL half of a rotating blade: the same thing happening once
    /// per blade per revolution. The other half is the turbulence — the boundary layer leaving the
    /// trailing edge and the vortex rolling off the tip — which is broadband, and which of the two
    /// dominates is decided by how fast the tips go. A propeller at Mach 0.8 concentrates its energy
    /// into harmonics so hard (the passage compresses by 1/(1-M), which is what the pulse model
    /// already does) that the broadband is twenty decibels under and inaudible; that is why the
    /// aircraft presets declare none and are unchanged by this existing. A mower blade at Mach 0.26
    /// and a condenser fan at Mach 0.06 are the other end of it: almost everything you hear of
    /// either is this, and the blade-passing tone is a thump underneath.
    ///
    /// It scales with the CUBE of tip speed — dipole radiation, power as the sixth — and sits in a
    /// band placed by a Strouhal number on the blade's own THICKNESS, which is the length scale the
    /// vortices are shed on: f = 0.2 U / t. That is why a thin fast fan hisses and a blunt slow
    /// mower blade roars, and neither is an equaliser setting.
    /// </summary>
    public float SelfNoiseDb { get; init; }

    public float TipSpeed(float rpm) => MathF.PI * DiameterMetres * rpm / 60f;
    public float BladePassHz(float rpm) => Blades * rpm / 60f;
}

/// <summary>The gas turbine: what comes out of the back, and the one or two tones you hear of it.</summary>
public sealed record GasTurbineSpec
{
    /// <summary>The fan on the N1 spool, turbofan only.</summary>
    public BladeRowSpec? Fan { get; init; }
    public required float CoreNozzleDiameterMetres { get; init; }
    public required float CoreExitVelocityIdle { get; init; }
    public required float CoreExitVelocityMax { get; init; }
    /// <summary>Core exhaust temperature, Kelvin. A hot jet is a light one and radiates less for
    /// the same velocity.</summary>
    public float CoreExitKelvin { get; init; } = 800f;
    /// <summary>Bypass stream, turbofan only: the annulus as an equivalent diameter, and its speed.</summary>
    public float BypassNozzleDiameterMetres { get; init; }
    public float BypassExitVelocityMax { get; init; }
    /// <summary>Combustion rumble at one metre at full power: low, broadband, follows fuel flow.</summary>
    public float CombustorDb { get; init; } = 90f;
    /// <summary>The compressor or turbine tone that is inside hearing, Hz at full speed, and its level.</summary>
    public float WhineHz { get; init; } = 8000f;
    public float WhineDb { get; init; } = 80f;
    /// <summary>
    /// Where the core jet's level sits against Lighthill's law with K = 1e-4, dB. An anchor like
    /// ReferenceDb, and SETTLED BY EAR: the first four flyovers (2026-09-18) were rendered with the
    /// jets about sixteen decibels under that law's near-field figure, and the verdict was "really
    /// really good" — so that balance is the baseline, held here as a visible number rather than
    /// rediscovered. A jet's one-metre figure is a near-field fiction anyway (the mixing region is
    /// metres long); what is real is the balance against the blades and the core, and that is what
    /// was approved.
    /// </summary>
    public float CoreJetTrimDb { get; init; } = -16f;
    /// <summary>The same anchor for the bypass stream (turbofan). Its lower Strouhal band sat a
    /// further five decibels down in the approved render.</summary>
    public float BypassJetTrimDb { get; init; } = -21f;
    /// <summary>How long the spool takes to follow the lever, seconds. A big fan is slow.</summary>
    public float SpoolSeconds { get; init; } = 4f;
    /// <summary>Spool speed at idle as a fraction of maximum.</summary>
    public float IdleFraction { get; init; } = 0.25f;
}

public sealed record AircraftProfile
{
    public required string Name { get; init; }
    public required AircraftPower Power { get; init; }
    /// <summary>Piston only: which car engine. It IS a car engine — the same EngineSynth runs it.</summary>
    public string? EngineKey { get; init; }
    /// <summary>The propeller (piston, turboprop) or the main rotor (turboshaft).</summary>
    public BladeRowSpec? Propeller { get; init; }
    public BladeRowSpec? TailRotor { get; init; }
    public GasTurbineSpec? Turbine { get; init; }
    /// <summary>Piston only: prop rpm over crank rpm. Direct drive is 1.</summary>
    public float PropGearRatio { get; init; } = 1f;
    /// <summary>Rotating inertia the crank sees through the prop, kg m^2. Piston only.</summary>
    public float PropInertiaKgM2 { get; init; } = 1.5f;
    public float CruiseSpeedMps { get; init; } = 60f;
    /// <summary>Overall level at one metre at full power, for placing the voice.</summary>
    public required float SourceLevelDb { get; init; }

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A light single: a 5.2 litre flat-four at 2,700 rpm swinging a two-blade metal prop of 1.93 m
    /// straight off the crank. Tip speed 273 m/s at full power — Mach 0.8 — which is why it buzzes on
    /// the climb and hums at cruise, and why the prop and not the engine is most of what you hear.
    /// </summary>
    public static AircraftProfile PistonSingle => new()
    {
        Name = "light single, flat-four, two-blade prop",
        Power = AircraftPower.Piston,
        EngineKey = "aero_flat4",
        Propeller = new BladeRowSpec
        {
            Blades = 2, DiameterMetres = 1.93f, ChordMetres = 0.13f, ThicknessRatio = 0.06f,
            RpmMax = 2700f, RpmIdle = 650f, ReferenceDb = 118f, BladeScatter = 0.02f,
        },
        PropGearRatio = 1f,
        PropInertiaKgM2 = 1.6f,
        CruiseSpeedMps = 55f,
        SourceLevelDb = 118f,
    };

    /// <summary>
    /// A regional turboprop: six-blade 3.93 m props at a constant 1,200 rpm (blade-passing 120 Hz)
    /// on a free-turbine core. The prop is constant-speed, so the lever changes its LOADING and not
    /// its note — that is the whole difference in feel from a piston aircraft, and why a turboprop
    /// spooling up for takeoff gets louder and harder without changing pitch. The core's exhaust is a
    /// small hot jet, and its whine is the thing you hear at the gate.
    /// </summary>
    public static AircraftProfile TurbopropRegional => new()
    {
        Name = "regional turboprop, six-blade",
        Power = AircraftPower.Turboprop,
        Propeller = new BladeRowSpec
        {
            Blades = 6, DiameterMetres = 3.93f, ChordMetres = 0.30f, ThicknessRatio = 0.05f,
            RpmMax = 1200f, RpmIdle = 820f, ReferenceDb = 124f, BladeScatter = 0.01f,
        },
        Turbine = new GasTurbineSpec
        {
            CoreNozzleDiameterMetres = 0.35f,
            CoreExitVelocityIdle = 90f, CoreExitVelocityMax = 220f, CoreExitKelvin = 850f,
            CombustorDb = 92f, WhineHz = 9500f, WhineDb = 86f, SpoolSeconds = 2.5f, IdleFraction = 0.6f,
        },
        CruiseSpeedMps = 140f,
        SourceLevelDb = 126f,
    };

    /// <summary>
    /// A narrow-body airliner's high-bypass turbofan: a 1.55 m fan of 24 blades on an N1 spool that
    /// runs 1,200 rpm at idle and 5,200 at takeoff. At 5,200 the tips are doing 422 m/s — Mach 1.23 —
    /// and the buzz-saw appears on its own from the per-blade shock differences. The bypass stream
    /// (300 m/s over a metre and a half of annulus) and the core (480 m/s, 800 K, 0.6 m) are the
    /// takeoff roar, and their eighth-power law is why the same engine at idle is a hiss.
    /// </summary>
    public static AircraftProfile TurbofanAirliner => new()
    {
        Name = "airliner turbofan, high bypass",
        Power = AircraftPower.Turbofan,
        Turbine = new GasTurbineSpec
        {
            Fan = new BladeRowSpec
            {
                Blades = 24, DiameterMetres = 1.55f, ChordMetres = 0.20f, ThicknessRatio = 0.04f,
                RpmMax = 5200f, RpmIdle = 1200f, ReferenceDb = 128f, Ducted = true, BladeScatter = 0.02f,
            },
            CoreNozzleDiameterMetres = 0.60f,
            CoreExitVelocityIdle = 110f, CoreExitVelocityMax = 480f, CoreExitKelvin = 800f,
            BypassNozzleDiameterMetres = 1.45f, BypassExitVelocityMax = 300f,
            CombustorDb = 96f, WhineHz = 6200f, WhineDb = 84f, SpoolSeconds = 5f, IdleFraction = 0.23f,
        },
        CruiseSpeedMps = 230f,
        SourceLevelDb = 142f,
    };

    /// <summary>
    /// A light turbine helicopter: a two-blade 10.2 m main rotor at 394 rpm (13 Hz — you feel it
    /// more than hear it; what you hear is its harmonics and the slap when the blades hit their own
    /// wake), a two-blade tail rotor at 2,550 rpm whose 85 Hz buzz is the pitch most people remember,
    /// and a small turboshaft whose whine is the only sound the engine itself contributes.
    /// </summary>
    public static AircraftProfile HelicopterLight => new()
    {
        Name = "light turbine helicopter, two-blade",
        Power = AircraftPower.Turboshaft,
        Propeller = new BladeRowSpec
        {
            Blades = 2, DiameterMetres = 10.16f, ChordMetres = 0.33f, ThicknessRatio = 0.12f,
            RpmMax = 394f, RpmIdle = 394f, ReferenceDb = 112f, BladeVortexInteraction = 0.55f, BladeScatter = 0.02f,
        },
        TailRotor = new BladeRowSpec
        {
            Blades = 2, DiameterMetres = 1.65f, ChordMetres = 0.13f, ThicknessRatio = 0.10f,
            RpmMax = 2550f, RpmIdle = 2550f, ReferenceDb = 104f, BladeScatter = 0.015f,
        },
        Turbine = new GasTurbineSpec
        {
            CoreNozzleDiameterMetres = 0.16f,
            CoreExitVelocityIdle = 70f, CoreExitVelocityMax = 160f, CoreExitKelvin = 820f,
            CombustorDb = 84f, WhineHz = 11500f, WhineDb = 88f, SpoolSeconds = 2f, IdleFraction = 0.65f,
        },
        CruiseSpeedMps = 55f,
        SourceLevelDb = 116f,
    };

    public static IReadOnlyDictionary<string, Func<AircraftProfile>> Presets { get; } =
        new Dictionary<string, Func<AircraftProfile>>(StringComparer.OrdinalIgnoreCase)
        {
            ["piston_single"] = () => PistonSingle,
            ["turboprop"] = () => TurbopropRegional,
            ["airliner"] = () => TurbofanAirliner,
            ["helicopter"] = () => HelicopterLight,
        };

    public static AircraftProfile ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No aircraft preset '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}
