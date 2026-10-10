using System.Collections.Generic;

using OpenFPS.Common.Editing;

namespace OpenFPS.Common;

// An aircraft as the machine it is, from three mechanisms (docs/AIRCRAFT.md): blades, whose pulses
// sharpen with tip Mach toward the listener (a soft thump at 0.5, a buzz past 0.8, the buzz-saw comb
// when a fan's tips go supersonic); jets, broadband at the eighth power of exit velocity (Lighthill),
// peaking at Strouhal 0.2 on the nozzle, loudest 30-40 degrees off the axis behind; and the core's
// rumble and whine. A piston aircraft runs the car engine (EngineKey) with the prop as its load. The
// shape comes from the mechanism; the level at one condition is anchored (ReferenceDb).

public enum AircraftPower { Piston, Turboprop, Turbofan, Turboshaft }

/// <summary>A row of blades in rotation: a propeller, a fan, a main rotor or a tail rotor.</summary>
public sealed record BladeRowSpec
{
    [Tunable("", 1, 16, "How many blades in the row.", Label = "blades")]
    public required int Blades { get; init; }
    [Tunable("m", 0.05, 20, "Tip to tip.", Label = "diameter", Step = 0.01)]
    public required float DiameterMetres { get; init; }
    /// <summary>Blade chord near the tip, metres. Sets the pulse width: chord over tip speed.</summary>
    [Tunable("m", 0.01, 1, "Blade chord near the tip; sets the pulse width.", Label = "chord", Step = 0.005)]
    public float ChordMetres { get; init; } = 0.15f;
    /// <summary>Thickness to chord of the tip section. Thickness noise scales with it.</summary>
    [Tunable("", 0.02, 0.4, "Thickness to chord of the tip section. Thickness noise scales with it.", Label = "thickness ratio", Step = 0.01)]
    public float ThicknessRatio { get; init; } = 0.08f;
    [Tunable("rpm", 50, 30000, "The fastest the row turns.", Label = "top speed", Step = 10)]
    public required float RpmMax { get; init; }
    /// <summary>The slowest the row turns while it is turning at all.</summary>
    [Tunable("rpm", 0, 30000, "The slowest the row turns while it is turning at all.", Label = "idle speed", Step = 10)]
    public float RpmIdle { get; init; }
    /// <summary>SPL at one metre in the plane of the disc, at RpmMax and full loading: the anchor.</summary>
    [Tunable("dB", 30, 160, "Level at one metre in the plane of the disc, at top speed and full loading.", Label = "tone level", Step = 1)]
    public float ReferenceDb { get; init; } = 110f;
    /// <summary>How hard the blades meet the tip vortices of the blades ahead of them, 0..1. A rotor
    /// in a descent or fast forward flight slaps; a propeller or a hovering rotor does not.</summary>
    public float BladeVortexInteraction { get; init; }
    /// <summary>A fan inside a duct: heard forward out of the inlet, cut off behind by the core and
    /// bypass streams, and with the low harmonics the duct will not carry removed.</summary>
    public bool Ducted { get; init; }
    /// <summary>Per-blade differences in pitch and track, as a fraction: the once-per-revolution "wow"
    /// under a propeller's note and the whole of the buzz-saw comb on a supersonic fan.</summary>
    [Tunable("", 0, 0.2, "Per-blade differences in pitch and track, as a fraction.", Label = "blade scatter", Step = 0.005)]
    public float BladeScatter { get; init; } = 0.015f;

    /// <summary>
    /// Broadband self-noise (trailing-edge and tip turbulence) at one metre, in the disc plane, at
    /// <see cref="RpmMax"/> and full loading, dB; zero for none. A propeller at Mach 0.8 puts its energy
    /// into harmonics and its broadband is twenty decibels under, so the aircraft presets declare none;
    /// a mower blade (Mach 0.26) or a condenser fan (0.06) is almost all this. Amplitude goes as tip
    /// speed cubed, in a band at f = 0.2 U / t on the blade's thickness: a thin fast fan hisses, a blunt
    /// mower blade roars.
    /// </summary>
    [Tunable("dB", 0, 160, "Broadband self-noise at one metre at top speed; 0 for none.", Label = "rush level", Step = 1)]
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
    /// <summary>Core exhaust temperature, Kelvin. A hot jet is light and radiates less for its
    /// velocity.</summary>
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
    /// Where the core jet's level sits against Lighthill's law with K = 1e-4, dB. Settled by ear: the
    /// first four flyovers (2026-09-18) had the jets about sixteen decibels under that law's near-field
    /// figure and were "really really good". What was approved is the balance against the blades and
    /// the core; a jet's one-metre figure is a near-field fiction.
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

/// <summary>
/// The undercarriage, heard on arrival: a still wheel spun up by the runway slides at full slip until
/// it is up to speed, the touchdown chirp. How long is I ω / T, with I = ½ m r², ω = v / r and
/// T = μ W r: about 0.3 s for an airliner's 110 kg main wheel under 23 kN, a twentieth of a second for a
/// light single's. W comes from the gear's stroke (<see cref="WeightOnWheelsAtTouchdown"/>).
/// </summary>
public sealed record LandingGearSpec
{
    /// <summary>The tyre, the same model a car's wheels use.</summary>
    public required TyreProfile Tyre { get; init; }
    /// <summary>Main wheels that touch. The nose wheel arrives later and carries almost no load.</summary>
    public int Wheels { get; init; } = 4;
    public required float WheelRadiusMetres { get; init; }
    /// <summary>One wheel and tyre assembly, kilograms. It is the flywheel that has to be spun up.</summary>
    public required float WheelMassKg { get; init; }
    /// <summary>What the aeroplane weighs when it arrives, kilograms.</summary>
    public required float LandingMassKg { get; init; }
    /// <summary>Sliding friction of rubber smeared on concrete, lower than a rolling tyre's peak.</summary>
    public float SlidingMu { get; init; } = 0.55f;

    /// <summary>How fast the aeroplane is still sinking at touchdown, m/s. Three feet a second is a firm,
    /// normal arrival; both trainer and airliner are certified to ten (14 CFR 23.473, 25.473).</summary>
    public float TouchdownSinkMps { get; init; } = 0.9f;

    /// <summary>How far the gear gives in stopping that sink, metres: an airliner's oleo a third of a
    /// metre, a light single's spring-steel leg about ten centimetres.</summary>
    public float StrokeMetres { get; init; } = 0.35f;

    /// <summary>
    /// The energy the gear takes up over its stroke over stroke times peak force: an oleo-pneumatic
    /// strut 0.75-0.9, a steel spring 0.5 (Currey, Aircraft Landing Gear Design, table 2.2).
    /// </summary>
    public float StrokeEfficiency { get; init; } = 0.8f;

    /// <summary>
    /// How much of the weight is on the wheels while they spin up, n = v² / (2 g η s): the wing still
    /// carries nearly one g. An airliner on a third of a metre of oleo has about a seventh on its mains
    /// (a third of a second of smoke); a light single on ten centimetres of spring, four fifths (the
    /// chirp). The load is the stroke's peak, reached a little after first touch on a spring, so a light
    /// single's chirp is if anything a little short here.
    /// </summary>
    public float WeightOnWheelsAtTouchdown
    {
        get
        {
            float v = MathF.Max(0f, TouchdownSinkMps);
            float n = v * v / (2f * 9.81f * Math.Clamp(StrokeEfficiency, 0.1f, 1f) * MathF.Max(0.01f, StrokeMetres));
            return Math.Clamp(n, 0.01f, 1f);
        }
    }

    /// <summary>How long the wheels take to come up to speed, seconds; at least a millisecond.</summary>
    public float SpinUpSeconds(float groundSpeedMps)
    {
        float r = MathF.Max(0.05f, WheelRadiusMetres);
        float inertia = 0.5f * MathF.Max(1f, WheelMassKg) * r * r;
        float loadN = MathF.Max(1f, LandingMassKg) * 9.81f
                    * Math.Clamp(WeightOnWheelsAtTouchdown, 0.01f, 1f) / MathF.Max(1, Wheels);
        float torque = MathF.Max(1f, SlidingMu * loadN * r);
        return MathF.Max(0.001f, inertia * (MathF.Max(0f, groundSpeedMps) / r) / torque);
    }
}

public sealed record AircraftProfile
{
    public required string Name { get; init; }
    public required AircraftPower Power { get; init; }
    /// <summary>Piston only: which car engine; the same EngineSynth runs it.</summary>
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

    /// <summary>
    /// Over the threshold, m/s. Declared, not a fraction of cruise: it goes with wing and weight, and a
    /// jet cruising four times as fast as a light single lands at barely twice the speed (at 0.62 of
    /// cruise the airliner came over the fence at 277 knots).
    /// </summary>
    public float ApproachSpeedMps { get; init; }

    /// <summary>
    /// How many power units the aeroplane has, each built and run slightly differently: two fans a few
    /// rpm apart beat at a cycle or two a second, the throb under a twin, which three decibels on one
    /// engine cannot give. The broadband streams add as power (two engines three decibels).
    /// </summary>
    public int Engines { get; init; } = 1;

    /// <summary>
    /// The propellers are held to one speed by a synchrophaser, as on a regional turboprop. Two props
    /// half a per cent apart flange (Cody: "the prop plane ... flange[s] when [it's] flying"); with
    /// `--aircraft steady` the twin's spectrum wandered 3.4 dB frame to frame against 1.1 for one
    /// engine. An airliner's fans are not phased, and their beat is the throb of a twin jet.
    /// </summary>
    public bool Synchrophased { get; init; }

    /// <summary>
    /// Between the outboard engines, metres: the voice's extent, as a bus's nose-to-tail separation is.
    /// Up close a twin is not a point source. Zero for a single, which falls back to its disc.
    /// </summary>
    public float EngineSpanMetres { get; init; }

    /// <summary>Wing tip to wing tip, metres. The aeroplane's real size.</summary>
    public float WingspanMetres { get; init; } = 10f;
    /// <summary>Nose to tail, metres.</summary>
    public float LengthMetres { get; init; } = 8f;

    /// <summary>The undercarriage, if this aeroplane's is modelled. Only heard on arrival.</summary>
    public LandingGearSpec? Gear { get; init; }

    /// <summary>How it lands, turns round and takes off on a runway (AircraftGroundRun); null for an
    /// aircraft that does not roll (a helicopter).</summary>
    public GroundRunSpec? Ground { get; init; }

    /// <summary>Overall level at one metre at full power, for placing the voice.</summary>
    public required float SourceLevelDb { get; init; }

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A light single: a 5.2 litre flat-four at 2,700 rpm with a 1.93 m two-blade prop off the crank.
    /// Tips at 273 m/s (Mach 0.8) at full power: it buzzes on the climb and hums at cruise.
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
        ApproachSpeedMps = 31f,      // 60 knots over the fence
        Engines = 1,
        WingspanMetres = 11.0f, LengthMetres = 8.3f,
        // Cessna 172S Information Manual, sections 4 and 5, at sea level and gross weight: touchdown
        // about 50 KIAS after a 61 KIAS approach, landing ground roll 575 ft with flaps 30 and maximum
        // braking; take-off ground roll 960 ft, rotating at 55 KIAS. Taxied at a brisk walk.
        Ground = new GroundRunSpec
        {
            TouchdownSpeedMps = 26f, LandingRollMetres = 175f,
            RotateSpeedMps = 28f, TakeoffRollMetres = 293f,
            TaxiSpeedMps = 5f, TurnRadiusMetres = 5f,
        },
        // The squeal level follows the friction work, the load on the tyre: a car's tyre declares 92 dB
        // on about 3.7 kN, so 5.4 kN (1,100 kg on two mains) is 94; the airliner's 108 on 160 kN and the
        // turboprop's 103 on 49 kN are the same law. An unfounded 88 left the touchdown inaudible.
        Gear = new LandingGearSpec
        {
            Tyre = TyreProfile.SportsOnAsphalt with { TreadBlocks = 0, SquealHz = 1250f, SquealQ = 9f, SquealDb = 94f, PeakGripG = 0.7f },
            Wheels = 2, WheelRadiusMetres = 0.20f, WheelMassKg = 9f, LandingMassKg = 1100f,
            StrokeMetres = 0.10f, StrokeEfficiency = 0.5f,
        },
        SourceLevelDb = 117f,        // 116.5 measured
    };

    /// <summary>
    /// A regional turboprop: six-blade 3.93 m props at a constant 1,200 rpm (blade-passing 120 Hz) on a
    /// free-turbine core. The lever changes the props' loading, not their note: louder and harder for
    /// takeoff at the same pitch. The core's whine is what you hear at the gate.
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
        ApproachSpeedMps = 60f,      // 117 knots
        // A twin, the props eight metres apart, and synchrophased as on the real type.
        Engines = 2,
        Synchrophased = true,
        EngineSpanMetres = 8.1f,
        WingspanMetres = 27.05f, LengthMetres = 25.7f,
        // ATR 72-600 (ATR airport planning manual and type figures, sea level, typical weights):
        // touchdown about 105 kt after a 117 kt approach, landing ground roll about 600 m on the
        // brakes with ground idle; rotation about 110 kt and a take-off ground roll of about 1,000 m.
        // Taxied at 15 kt; a 180-degree turn needs about 25 m of pavement.
        Ground = new GroundRunSpec
        {
            TouchdownSpeedMps = 54f, LandingRollMetres = 600f,
            RotateSpeedMps = 57f, TakeoffRollMetres = 1000f,
            TaxiSpeedMps = 8f, TurnRadiusMetres = 12f,
        },
        Gear = new LandingGearSpec
        {
            Tyre = TyreProfile.TruckOnAsphalt with { TreadBlocks = 0, SquealHz = 620f, SquealQ = 7f, SquealDb = 103f, PeakGripG = 0.65f },
            Wheels = 4, WheelRadiusMetres = 0.40f, WheelMassKg = 48f, LandingMassKg = 20000f,
            StrokeMetres = 0.35f, StrokeEfficiency = 0.8f,     // oleo-pneumatic main legs
        },
        // Measured with `--spool`. The declaration sets both the placement and the voice's full-scale
        // reference, which pull opposite ways: declaring 129 played this aeroplane five decibels too quiet.
        SourceLevelDb = 120f,
    };

    /// <summary>
    /// A narrow-body airliner's high-bypass turbofan: a 1.55 m, 24-blade fan, 1,200 rpm at idle and
    /// 5,200 at takeoff, where the tips are at 422 m/s (Mach 1.23) and the buzz-saw appears. The bypass
    /// (300 m/s) and core (480 m/s, 800 K, 0.6 m) jets are the takeoff roar.
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
            // The whine is anchored at full power, where a high-bypass fan's forward tone is of the
            // jet's order (fan tones dominate a takeoff certification measurement). At 84 dB, sixty
            // under the jets, "the whistle stops when it spools up"; the approved flyover moved under a dB.
            CombustorDb = 96f, WhineHz = 6200f, WhineDb = 118f, SpoolSeconds = 5f, IdleFraction = 0.23f,
        },
        CruiseSpeedMps = 230f,
        ApproachSpeedMps = 71f,      // 138 knots, a narrow-body at landing weight
        Engines = 2,
        EngineSpanMetres = 11.6f,
        WingspanMetres = 35.8f, LengthMetres = 39.5f,
        // A320 (Airbus aircraft characteristics for airport planning, sea level, typical weights):
        // touchdown about 130 kt after a 138 kt approach, landing ground roll about 1,100 m with
        // autobrake low and idle reverse (about 0.2 g); rotation about 145 kt and a take-off ground
        // roll of about 1,800 m. Taxied at 20 kt; a 180-degree turn needs about 23 m of pavement
        // either side of the nose wheel's path.
        Ground = new GroundRunSpec
        {
            TouchdownSpeedMps = 67f, LandingRollMetres = 1100f,
            RotateSpeedMps = 75f, TakeoffRollMetres = 1800f,
            TaxiSpeedMps = 10f, TurnRadiusMetres = 15f,
        },
        Gear = new LandingGearSpec
        {
            Tyre = TyreProfile.TruckOnAsphalt with { TreadBlocks = 0, SquealHz = 430f, SquealQ = 6f, SquealDb = 108f, PeakGripG = 0.6f },
            Wheels = 4, WheelRadiusMetres = 0.56f, WheelMassKg = 110f, LandingMassKg = 65000f,
            StrokeMetres = 0.35f, StrokeEfficiency = 0.8f,
        },
        // Measured at the loudest bearing at full power. An estimated 145 played it four decibels quiet
        // in the mix, and on approach the engines idle forty below this.
        SourceLevelDb = 138f,
    };

    /// <summary>
    /// A light turbine helicopter: a two-blade 10.2 m main rotor at 394 rpm (13 Hz, heard as harmonics
    /// and blade slap), a two-blade tail rotor at 2,550 rpm (the 85 Hz buzz people remember), and a
    /// turboshaft heard only as its whine.
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
        Engines = 1,
        // A helicopter's "span" is its rotor, which is what the air knows about it.
        WingspanMetres = 10.16f, LengthMetres = 12.9f,
        // Measured. A light helicopter carries because its rotor radiates downward from overhead.
        SourceLevelDb = 104f,
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

/// <summary>
/// What an aeroplane does on a runway, from each type's published performance: touchdown and landing
/// roll, rotation and take-off roll, taxi pace and turn. Accelerations follow as v² / 2s.
/// </summary>
public sealed record GroundRunSpec
{
    /// <summary>At the wheels touching, m/s: a little under the approach speed, after the flare.</summary>
    public required float TouchdownSpeedMps { get; init; }
    /// <summary>From touchdown to taxi speed, metres.</summary>
    public required float LandingRollMetres { get; init; }
    /// <summary>Where the nose comes up and the wheels leave, m/s.</summary>
    public required float RotateSpeedMps { get; init; }
    /// <summary>From standing to rotation, metres.</summary>
    public required float TakeoffRollMetres { get; init; }
    public float TaxiSpeedMps { get; init; } = 8f;
    /// <summary>The path of its middle in a 180-degree turn, metres.</summary>
    public float TurnRadiusMetres { get; init; } = 10f;

    /// <summary>The landing roll's steady deceleration, m/s².</summary>
    public float LandingDecel => (TouchdownSpeedMps * TouchdownSpeedMps - TaxiSpeedMps * TaxiSpeedMps) / (2f * MathF.Max(1f, LandingRollMetres));
    /// <summary>The take-off roll's mean acceleration, m/s².</summary>
    public float TakeoffAccel => RotateSpeedMps * RotateSpeedMps / (2f * MathF.Max(1f, TakeoffRollMetres));
    /// <summary>The speed it takes the turn at: no more than taxi speed, and no more than a tenth of a g sideways.</summary>
    public float TurnSpeedMps => MathF.Min(TaxiSpeedMps, MathF.Sqrt(0.1f * 9.80665f * TurnRadiusMetres));
}
