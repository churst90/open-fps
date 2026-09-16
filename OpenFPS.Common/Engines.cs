using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenFPS.Common;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  An engine, described as the machine it is — not as the sound it makes.
//
//  Every number here is a physical quantity with a unit: a bore in millimetres, a cam duration in
//  crank degrees, a pipe length in metres, a pressure in bar. The synthesis (EngineSynth, ExhaustNetwork,
//  IntakeNetwork) integrates the gas through those dimensions, so changing a value changes the sound
//  the way changing the part would. There are no "tone" or "brightness" knobs; the closest things to
//  taste controls are the few scale factors that stand in for physics the model does not carry
//  (turbulence strength, mechanical noise level) and they are marked as such.
//
//  Everything is a record with init-only properties, so any preset can be varied with `with { }`.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

public enum EngineLayout { Inline, Vee, Flat }
public enum FuelType { Petrol, Diesel }
public enum Induction { NaturallyAspirated, Turbocharged, Supercharged }

/// <summary>How the two banks' pipes meet downstream of the collectors, if at all.</summary>
public enum CrossoverKind
{
    /// <summary>True duals: each bank has its own system to its own tailpipe and they never meet.</summary>
    None,
    /// <summary>A balance tube between the two systems: partial coupling, its own quarter-wave.</summary>
    HPipe,
    /// <summary>The two pipes cross and merge over a short length: strong coupling, near-complete
    /// averaging of the banks.</summary>
    XPipe,
    /// <summary>Both banks feed one single system (a Y into a single muffler and tailpipe).</summary>
    Merged,
}

public enum MufflerKind
{
    /// <summary>Straight pipe: nothing in the way.</summary>
    None,
    /// <summary>Expansion chambers with baffles (Flowmaster, most performance mufflers). Reflects rather
    /// than absorbs, so it filters by cancellation and keeps the rasp.</summary>
    Chambered,
    /// <summary>A perforated straight tube in a packed can (glasspack, most "straight-through"
    /// performance mufflers). Loses the top progressively and leaves the bottom alone.</summary>
    Absorptive,
    /// <summary>A stock baffled muffler: chambers, absorption and a tuned resonator all at once. Quiet.</summary>
    Baffled,
}

/// <summary>A cam lobe as it affects the valve it drives.</summary>
public sealed record CamLobe
{
    /// <summary>Seat-to-seat ("advertised") duration in crank degrees. Stock cars run 250-280, a mild
    /// street cam 280-295, a big lumpy one 300-320.</summary>
    public required float DurationDegrees { get; init; }
    /// <summary>Peak valve lift, millimetres. Typical 9-14 mm.</summary>
    public float MaxLiftMm { get; init; } = 12f;
    /// <summary>How much of the duration is spent in the slow opening and closing ramps rather than on
    /// the main lobe, 0..0.5. Aggressive roller cams have short ramps and reach lift fast.</summary>
    public float RampFraction { get; init; } = 0.22f;
    /// <summary>Lobe centreline, crank degrees from TDC of the firing stroke: BEFORE for exhaust
    /// (positive means the exhaust lobe centres before BDC... expressed as degrees ATDC-firing, so an
    /// exhaust lobe centred 110 degrees before overlap TDC sits at 250 ATDC-firing), AFTER for intake
    /// (an intake lobe centred 106 ATDC of the overlap TDC sits at 466).</summary>
    public required float CentrelineDegrees { get; init; }

    /// <summary>Opening angle, crank degrees ATDC-firing.</summary>
    public float OpensDegrees => CentrelineDegrees - DurationDegrees * 0.5f;
    /// <summary>Closing angle, crank degrees ATDC-firing.</summary>
    public float ClosesDegrees => CentrelineDegrees + DurationDegrees * 0.5f;
}

/// <summary>The valves for one function (all exhaust valves, or all intake valves) on one cylinder.</summary>
public sealed record ValveSpec
{
    public int Count { get; init; } = 1;
    public required float DiameterMm { get; init; }
    /// <summary>Discharge coefficient of the port at full lift. Real heads run 0.55-0.75.</summary>
    public float DischargeCoefficient { get; init; } = 0.62f;
}

/// <summary>A muffler, as the things inside the can.</summary>
public sealed record MufflerSpec
{
    public MufflerKind Kind { get; init; } = MufflerKind.Chambered;
    /// <summary>Chamber lengths, metres, in flow order. Each is an expansion of the pipe into the
    /// can's cross-section and back, so each cancels around c/2L and its multiples.</summary>
    public float[] ChamberLengthsMetres { get; init; } = { 0.10f, 0.14f, 0.18f };
    /// <summary>Can cross-section over pipe cross-section. 4-9 for a typical oval can on a 2.5 inch pipe.
    /// The larger it is, the deeper the chambers cancel.</summary>
    public float ExpansionRatio { get; init; } = 6f;
    /// <summary>How much of each chamber's internal reflection is lost to baffles and deflectors, 0..1.
    /// Zero is a clean expansion chamber, which rings; a Flowmaster's deflectors are around 0.3.</summary>
    public float BaffleLoss { get; init; } = 0.3f;
    /// <summary>Acoustic absorption of the packing, 0..1, applied to the top of the band on every pass.
    /// A fresh glasspack is 0.6-0.8, a blown-out one 0.2, a chambered muffler 0.</summary>
    public float Absorption { get; init; } = 0f;
    /// <summary>Length of the absorptive section, metres.</summary>
    public float AbsorptiveLengthMetres { get; init; } = 0.45f;
    /// <summary>A Helmholtz resonator tuned to a drone frequency, Hz. 0 for none.</summary>
    public float ResonatorHz { get; init; } = 0f;
    /// <summary>How sharply the resonator is tuned. Q of 4-8 is a real one.</summary>
    public float ResonatorQ { get; init; } = 5f;

    /// <summary>
    /// The CAN ITSELF, as metal that rings — null for a muffler whose case is not worth modelling.
    ///
    /// Everything above this line describes what the muffler does to the GAS: chambers that cancel,
    /// packing that absorbs, a resonator that notches a drone. None of it is the case, and until this
    /// existed nothing in the model was: a Flowmaster was a set of gas volumes with no steel around
    /// them. But the case is a bare steel box driven from the inside by the full pressure wave, and
    /// it is most of what people mean by a metallic exhaust note.
    ///
    /// It matters that the case is driven by the pressure INSIDE rather than by what comes out of the
    /// tailpipe. Those are different signals: the chambers cancel particular frequencies on the way
    /// through, so a note can be quiet at the pipe and still ring loudly off the can. A muffler that
    /// cancels well can still be the loudest-sounding thing on the car.
    /// </summary>
    public VehicleBody? Shell { get; init; }

    /// <summary>
    /// How loud the case is against the tailpipe, as a fraction of the internal pressure that ends up
    /// radiating at one metre.
    ///
    /// A ratio rather than a level, because the internal wave and the radiated pressure at a metre are
    /// not in the same units by a long way — the number folds together transmission through the steel,
    /// the case's radiating area and the spreading out to a metre. Measured rather than chosen: see
    /// the calibration note in ExhaustNetwork.
    /// </summary>
    public float ShellLevel { get; init; } = 0f;

    public static MufflerSpec StraightPipe => new() { Kind = MufflerKind.None };

    /// <summary>A two-chamber 40-series style muffler: aggressive, short, and loud — and a bare
    /// steel case with nothing in it to stop the case ringing, which is the metallic half of it.</summary>
    public static MufflerSpec Chambered40 => new()
    {
        Kind = MufflerKind.Chambered,
        ChamberLengthsMetres = new[] { 0.09f, 0.115f, 0.145f },
        ExpansionRatio = 5.5f,
        BaffleLoss = 0.32f,
        Shell = VehicleBody.MufflerCase,
        ShellLevel = ShellCalibration,
    };

    /// <summary>
    /// What one pascal inside the can becomes at one metre outside it — measured, not chosen.
    ///
    /// The internal wave runs to thousands of pascals and a metre away is tens, so this carries the
    /// whole conversion: how much gets through the steel, how much of the case radiates, and the
    /// spreading out to a metre. It is one ratio because measuring one ratio is honest and guessing
    /// three factors is not. See the calibration run in the vehicles notes.
    /// </summary>
    public const float ShellCalibration = 0.035f;

    /// <summary>A packed straight-through can: deep, less rasp — and the packing is pressed against
    /// the case, so the case is damped too. Its shell is the same model with a loss factor ten times
    /// higher, which is the whole of the difference.</summary>
    public static MufflerSpec Glasspack => new()
    {
        Shell = VehicleBody.PackedMufflerCase,
        ShellLevel = ShellCalibration,
        Kind = MufflerKind.Absorptive,
        Absorption = 0.62f,
        AbsorptiveLengthMetres = 0.50f,
    };

    /// <summary>What a production car leaves the factory with: quiet.</summary>
    public static MufflerSpec Stock => new()
    {
        Kind = MufflerKind.Baffled,
        ChamberLengthsMetres = new[] { 0.16f, 0.22f, 0.30f },
        ExpansionRatio = 9f,
        BaffleLoss = 0.55f,
        Absorption = 0.45f,
        AbsorptiveLengthMetres = 0.35f,
        ResonatorHz = 95f,
    };
}

/// <summary>The exhaust system: primaries, collectors, the run down the car, and the ends.</summary>
public sealed record ExhaustSpec
{
    /// <summary>Length of each cylinder's primary pipe, valve to collector, metres, in cylinder order.
    /// Null means every cylinder gets <see cref="PrimaryLengthMetres"/> spread by
    /// <see cref="PrimarySpread"/>, front to back down each bank.</summary>
    public float[]? PrimaryLengthsMetres { get; init; }
    public float PrimaryLengthMetres { get; init; } = 0.80f;
    /// <summary>How unequal the primaries are, as a fraction of their length. A fabricated header
    /// holds them within 0.05-0.15; a cast log manifold is 0.4 and up — and the manifold sounds
    /// coarser and burblier for it, because eight pipes at eight pitches is a band and eight at one
    /// pitch is a tube.</summary>
    public float PrimarySpread { get; init; } = 0.12f;
    public float PrimaryDiameterMm { get; init; } = 44f;

    /// <summary>Which cylinders join which collector, as lists of cylinder indices. Null means one
    /// collector per bank. An inline-6 with two 3-into-1 headers is {{0,1,2},{3,4,5}}; a 4-2-1 header
    /// on an inline-4 is {{0,3},{1,2}} — and that grouping is most of why those sound the way they do.</summary>
    public int[][]? CollectorGroups { get; init; }
    public float CollectorDiameterMm { get; init; } = 63f;
    /// <summary>From each collector to where the systems meet (or to the muffler if they never do), metres.</summary>
    public float CollectorPipeMetres { get; init; } = 1.10f;

    public CrossoverKind Crossover { get; init; } = CrossoverKind.HPipe;
    /// <summary>Length of the balance tube for an H-pipe, metres. Its own quarter-wave is audible.</summary>
    public float CrossoverTubeMetres { get; init; } = 0.35f;
    /// <summary>Cross-section of the balance tube relative to the system pipe, 0..1.5.</summary>
    public float CrossoverArea { get; init; } = 0.6f;

    /// <summary>From the crossover (or collector pipe) to the muffler inlet, metres.</summary>
    public float MidPipeMetres { get; init; } = 1.20f;
    public MufflerSpec Muffler { get; init; } = MufflerSpec.Chambered40;
    /// <summary>Tailpipe from the muffler to the open air, metres. Two branches get two lengths; if
    /// only one is given the second is 9% longer, because two equal tailpipes let the banks arrive in
    /// step and cancel each other's unevenness.</summary>
    public float[] TailpipeMetres { get; init; } = { 0.60f };
    public float TailpipeDiameterMm { get; init; } = 63f;
    /// <summary>Second exhaust system count for engines that split by bank. Derived: it is the number
    /// of collector groups unless <see cref="Crossover"/> is Merged.</summary>

    /// <summary>Exhaust gas temperature at the port, Celsius, idling and at full load. The speed of
    /// sound in every pipe follows the square root of the absolute temperature, so the whole system
    /// speaks nearly half an octave higher working than idling.</summary>
    public float GasCelsiusIdle { get; init; } = 330f;
    public float GasCelsiusFull { get; init; } = 820f;
    /// <summary>What fraction of the port's temperature rise survives to the tailpipe.</summary>
    public float TailCooling { get; init; } = 0.45f;

    /// <summary>Multiplier on the viscothermal wall loss, to stand in for bends, joints, flex sections
    /// and rust that a straight smooth pipe does not have. 1 is a straight smooth pipe; 2-3 is a
    /// production system with four bends and two flanges.</summary>
    public float WallLossMultiplier { get; init; } = 1.8f;
    /// <summary>Scale on the finite-amplitude steepening of the wave fronts, 0..1. 1 is the physics —
    /// a half-bar pulse arrives noticeably sharper than it left. It is the mechanism behind the rasp
    /// and crackle of an engine under load, and it is why headers sound hard and a stock manifold at
    /// idle does not.</summary>
    public float Steepening { get; init; } = 1f;
    /// <summary>Resistive loss at junctions from the mean flow, as a fraction of the junction's
    /// admittance at full load. Stands in for vortex shedding at the collector and muffler inlet.</summary>
    public float FlowLoss { get; init; } = 0.12f;

    /// <summary>Turbulent mixing noise at the tailpipe orifice, as a level scale. This is the jet noise
    /// of the exhaust leaving the pipe; it goes as a high power of the exit velocity so it is
    /// negligible at idle and part of the roar at full load. The exponent is physics; this scale
    /// stands in for the nozzle detail the model does not carry.</summary>
    public float JetNoiseLevel { get; init; } = 1f;
    /// <summary>Turbulence generated at the valve seat during blowdown, level scale. The flow is sonic
    /// through a narrow curtain and it is not quiet.</summary>
    public float PortNoiseLevel { get; init; } = 1f;

    /// <summary>Chance per second of unburnt fuel lighting in the hot pipe on the overrun.</summary>
    public float OverrunPopRate { get; init; } = 6f;

    public int TailpipeCount(int collectors)
        => Crossover == CrossoverKind.Merged ? 1 : collectors;
}

/// <summary>The intake tract: valve, runner, plenum, throttle, airbox, snorkel.</summary>
public sealed record IntakeSpec
{
    public float RunnerLengthMetres { get; init; } = 0.30f;
    public float RunnerDiameterMm { get; init; } = 42f;
    public float PlenumLitres { get; init; } = 4.5f;
    public float ThrottleDiameterMm { get; init; } = 80f;
    public float AirboxLitres { get; init; } = 8f;
    public float SnorkelLengthMetres { get; init; } = 0.45f;
    public float SnorkelDiameterMm { get; init; } = 70f;
    /// <summary>Acoustic absorption of the airbox lining and filter, 0..1.</summary>
    public float Absorption { get; init; } = 0.35f;
    /// <summary>How much of the intake noise reaches the outside of the car. An open filter under the
    /// bonnet is 1; a factory airbox with a resonator in the snorkel is 0.25.</summary>
    public float Level { get; init; } = 0.6f;
}

/// <summary>The noises the block makes that are not gas: valvetrain, injection, accessories.</summary>
public sealed record MechanicalSpec
{
    /// <summary>Valvetrain tick per valve event, level scale. Solid lifters are loud, hydraulic quiet.</summary>
    public float ValvetrainLevel { get; init; } = 0.5f;
    /// <summary>Combustion knock radiated by the block, level scale: the sharp pressure rise of
    /// ignition heard through the metal. Near zero for a petrol engine, the defining sound of a diesel.</summary>
    public float CombustionKnock { get; init; } = 0.05f;
    /// <summary>Alternator (or any accessory) whine: engine order and level. Order = pulley ratio times
    /// pole pairs; a 12-pole alternator on a 2.8:1 pulley whines at order 16.8.</summary>
    public float AccessoryWhineOrder { get; init; } = 16.8f;
    public float AccessoryWhineLevel { get; init; } = 0.15f;
    /// <summary>Supercharger rotor whine (Roots/twin-screw): order = lobes times drive ratio.</summary>
    public float BlowerWhineOrder { get; init; } = 0f;
    public float BlowerWhineLevel { get; init; } = 0f;
    /// <summary>Turbocharger: whistle level under boost and the lag of the shaft, seconds.</summary>
    public float TurboWhistleLevel { get; init; } = 0f;
    public float TurboLagSeconds { get; init; } = 0.8f;
}

/// <summary>
/// An engine, complete. Firing angles and bank assignment are in CYLINDER order, degrees of crank
/// after cylinder 0 fires, over a 720 degree cycle (360 for a two-stroke).
/// </summary>
public sealed record EngineProfile
{
    public required string Name { get; init; }
    public EngineLayout Layout { get; init; } = EngineLayout.Vee;
    public int Strokes { get; init; } = 4;
    public FuelType Fuel { get; init; } = FuelType.Petrol;
    public Induction Induction { get; init; } = Induction.NaturallyAspirated;
    /// <summary>Peak boost, bar gauge, for a turbo or blower.</summary>
    public float BoostBar { get; init; } = 0f;

    /// <summary>Crank angle at which each cylinder fires (its combustion TDC), degrees, cylinder order.</summary>
    public required float[] FiringAngles { get; init; }
    /// <summary>Which bank each cylinder is on, cylinder order. Inline engines are all bank 0.</summary>
    public required int[] Bank { get; init; }
    public int Cylinders => FiringAngles.Length;
    public float CycleDegrees => Strokes == 2 ? 360f : 720f;

    // ── Geometry ────────────────────────────────────────────────────────────────────────────────
    public required float BoreMm { get; init; }
    public required float StrokeMm { get; init; }
    /// <summary>Connecting rod length over crank radius. 1.5-1.8 for road engines.</summary>
    public float RodRatio { get; init; } = 1.7f;
    public float CompressionRatio { get; init; } = 10f;

    public float CylinderDisplacementLitres
        => MathF.PI * 0.25f * BoreMm * BoreMm * StrokeMm * 1e-6f;
    public float DisplacementLitres => CylinderDisplacementLitres * Cylinders;

    // ── Valvetrain ──────────────────────────────────────────────────────────────────────────────
    public CamLobe ExhaustCam { get; init; } = new() { DurationDegrees = 270f, CentrelineDegrees = 250f };
    public CamLobe IntakeCam { get; init; } = new() { DurationDegrees = 270f, CentrelineDegrees = 470f };
    public ValveSpec ExhaustValve { get; init; } = new() { DiameterMm = 38f };
    public ValveSpec IntakeValve { get; init; } = new() { DiameterMm = 46f };

    /// <summary>
    /// Valve overlap in crank degrees: how long both valves are open across TDC. THE number behind a
    /// lopey idle. With a lot of overlap and little exhaust velocity at idle, exhaust gas is pushed back
    /// into the cylinder and the intake charge is diluted, cycle by cycle and unevenly — which is what
    /// a lope is. Stock is 20-40, a street cam 50-70, a race cam 80 and up.
    /// </summary>
    public float OverlapDegrees => MathF.Max(0f, ExhaustCam.ClosesDegrees - IntakeCam.OpensDegrees);

    /// <summary>The lope, 0..1, derived from overlap. Kept as a derived number so a preset's idle
    /// character can be read at a glance; the synthesis uses the overlap itself.</summary>
    public float CamLope => Math.Clamp((OverlapDegrees - 30f) / 60f, 0f, 1f);

    // ── Combustion ──────────────────────────────────────────────────────────────────────────────
    /// <summary>Gas temperature in the cylinder at exhaust valve opening, at full load, Kelvin.</summary>
    public float EvoTemperatureK { get; init; } = 1150f;
    /// <summary>Manifold absolute pressure at idle, bar. A big cam idles at 0.5-0.6 because it cannot
    /// pull a vacuum; a stock engine idles at 0.3.</summary>
    public float IdleMapBar { get; init; } = 0.35f;
    /// <summary>Cycle-to-cycle combustion variation at full load under clean conditions, as a
    /// fraction (a healthy engine measures 2-4% COV of IMEP). Idle variation is derived from overlap.</summary>
    public float CombustionVariation { get; init; } = 0.03f;
    /// <summary>Extra idle roughness on top of what overlap predicts, 0..1. A carburetted engine
    /// with a lumpy cam and no idle control is up near 1; fuel injection with closed-loop idle is 0.</summary>
    public float IdleRoughness { get; init; } = 0.3f;

    // ── Rotating assembly and the way it is driven ──────────────────────────────────────────────
    public required float IdleRpm { get; init; }
    public required float RedlineRpm { get; init; }
    public float CrankingRpm { get; init; } = 250f;
    /// <summary>Rotating inertia of crank, flywheel, clutch and damper, kg m^2. A heavy flywheel
    /// is 0.35-0.5, a race one 0.1. It decides how fast a free rev climbs and how much the crank
    /// speed ripples between firings.</summary>
    public float InertiaKgM2 { get; init; } = 0.30f;
    /// <summary>Mechanical friction torque, Nm, at rest and per 1000 rpm. About 0.95 bar of friction
    /// mean effective pressure at idle for a petrol engine (7.6 Nm per litre), 1.5 bar for a diesel,
    /// rising 0.35 bar per 1000 rpm. Pumping loss is not in here: the cylinders compute it.</summary>
    public float FrictionNm { get; init; } = 20f;
    public float FrictionNmPerKrpm { get; init; } = 9f;
    public float PeakTorqueNm { get; init; } = 500f;
    public float PeakTorqueRpm { get; init; } = 4200f;
    /// <summary>How the idle control fights the engine's own unevenness: the gain of the governor, 1/s.
    /// Electronic throttle idle control is quick (3-5); a carburettor's idle screw is zero.</summary>
    public float IdleGovernorGain { get; init; } = 2.5f;

    public ExhaustSpec Exhaust { get; init; } = new();
    public IntakeSpec Intake { get; init; } = new();
    public MechanicalSpec Mechanical { get; init; } = new();

    // ── Derived ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The firing order as cylinder numbers (1-based) in the order they fire.</summary>
    public string FiringOrder
    {
        get
        {
            var idx = Enumerable.Range(0, Cylinders).ToArray();
            Array.Sort(idx, (a, b) => FiringAngles[a].CompareTo(FiringAngles[b]));
            return string.Join("-", idx.Select(i => (i + 1).ToString()));
        }
    }

    /// <summary>Collector grouping actually in force: the profile's, or one per bank.</summary>
    public int[][] CollectorGroups
    {
        get
        {
            if (Exhaust.CollectorGroups != null) return Exhaust.CollectorGroups;
            int banks = Bank.Max() + 1;
            var groups = new List<int>[banks];
            for (int b = 0; b < banks; b++) groups[b] = new List<int>();
            for (int c = 0; c < Cylinders; c++) groups[Bank[c]].Add(c);
            return groups.Select(g => g.ToArray()).ToArray();
        }
    }

    /// <summary>Firing angles of the cylinders in one collector group, sorted, so the pattern a
    /// listener hears down one pipe can be printed.</summary>
    public float[] GroupIntervals(int group)
    {
        var angles = CollectorGroups[group].Select(c => FiringAngles[c]).OrderBy(a => a).ToArray();
        var intervals = new float[angles.Length];
        for (int i = 0; i < angles.Length; i++)
        {
            float next = i + 1 < angles.Length ? angles[i + 1] : angles[0] + CycleDegrees;
            intervals[i] = next - angles[i];
        }
        return intervals;
    }

    // ── Building blocks for firing patterns ─────────────────────────────────────────────────────

    /// <summary>Even firing: cylinders in the given 1-based firing order fire at equal intervals.</summary>
    public static float[] EvenFire(int[] order, int strokes = 4)
    {
        float cycle = strokes == 2 ? 360f : 720f;
        var angles = new float[order.Length];
        for (int i = 0; i < order.Length; i++) angles[order[i] - 1] = cycle / order.Length * i;
        return angles;
    }

    /// <summary>Uneven firing: explicit intervals between successive firings, in firing order.</summary>
    public static float[] IntervalFire(int[] order, float[] intervals)
    {
        var angles = new float[order.Length];
        float at = 0f;
        for (int i = 0; i < order.Length; i++)
        {
            angles[order[i] - 1] = at;
            at += intervals[i];
        }
        return angles;
    }

    /// <summary>Banks by the common convention: odd cylinders one side, even the other.</summary>
    public static int[] AlternatingBanks(int n) => Enumerable.Range(0, n).Select(i => i & 1).ToArray();
    /// <summary>Banks split front-and-back: the first half one side, the second half the other
    /// (Ford V8 numbering, most V12s).</summary>
    public static int[] HalfBanks(int n) => Enumerable.Range(0, n).Select(i => i < n / 2 ? 0 : 1).ToArray();
    public static int[] OneBank(int n) => new int[n];

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    //  Presets. Each is a real kind of engine, with numbers from the kind of engine it is.
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A 7-litre big-block V8 with a long cam, long-tube headers, true duals and chambered mufflers.
    /// Cross-plane crank, GM firing order 1-8-4-3-6-5-7-2: each bank fires at 90/180/180/270 degree
    /// intervals, and that unevenness, heard down its own pipe, is the American V8 burble.
    /// </summary>
    public static EngineProfile V8MuscleBigBlock => new()
    {
        Name = "7.0 big block V8, true duals",
        Layout = EngineLayout.Vee,
        FiringAngles = EvenFire(new[] { 1, 8, 4, 3, 6, 5, 7, 2 }),
        Bank = AlternatingBanks(8),
        BoreMm = 108f, StrokeMm = 95.5f, RodRatio = 1.62f, CompressionRatio = 10.5f,
        ExhaustCam = new CamLobe { DurationDegrees = 306f, MaxLiftMm = 14.5f, RampFraction = 0.16f, CentrelineDegrees = 250f },
        IntakeCam = new CamLobe { DurationDegrees = 300f, MaxLiftMm = 14.5f, RampFraction = 0.16f, CentrelineDegrees = 466f },
        ExhaustValve = new ValveSpec { DiameterMm = 48f, DischargeCoefficient = 0.66f },
        IntakeValve = new ValveSpec { DiameterMm = 56f, DischargeCoefficient = 0.66f },
        EvoTemperatureK = 1200f, IdleMapBar = 0.55f,
        IdleRoughness = 0.75f, IdleGovernorGain = 1.5f,
        IdleRpm = 720f, RedlineRpm = 5800f,
        InertiaKgM2 = 0.42f, FrictionNm = 53.2f, FrictionNmPerKrpm = 19.6f,
        PeakTorqueNm = 750f, PeakTorqueRpm = 3600f,
        Exhaust = new ExhaustSpec
        {
            // Unequal enough to growl: the half-orders live in the difference between the primaries,
            // and a stock cast manifold is far less even than a fabricated header.
            PrimaryLengthMetres = 0.92f, PrimarySpread = 0.24f, PrimaryDiameterMm = 47.6f,
            CollectorDiameterMm = 76f, CollectorPipeMetres = 0.9f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 1.3f,
            Muffler = MufflerSpec.Chambered40 with { ChamberLengthsMetres = new[] { 0.115f, 0.15f, 0.19f }, ExpansionRatio = 5f, BaffleLoss = 0.25f },
            Steepening = 1.15f,
            TailpipeMetres = new[] { 0.75f, 0.82f },
            TailpipeDiameterMm = 76f,
            GasCelsiusIdle = 320f, GasCelsiusFull = 810f,
            WallLossMultiplier = 1.5f,
            OverrunPopRate = 8f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.22f, RunnerDiameterMm = 50f, PlenumLitres = 6f, ThrottleDiameterMm = 95f, AirboxLitres = 5f, SnorkelLengthMetres = 0.3f, SnorkelDiameterMm = 100f, Level = 0.9f, Absorption = 0.15f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.9f, CombustionKnock = 0.06f, AccessoryWhineLevel = 0.08f },
    };

    /// <summary>
    /// A 7.0 police interceptor: the big block, but with a cam that has no business in a road car.
    ///
    /// The thump is OVERLAP, and overlap is the lobe centres, not the duration. The stock big block
    /// above runs 306/300 degrees on 108-degree centres; this runs longer AND tighter — 320/316 on
    /// 102 — which puts roughly 116 degrees of overlap in it against the stock engine's 68. At idle
    /// that means exhaust blowing back through the intake and a cylinder that only sometimes gets a
    /// clean charge, so it lopes: the classic cammed-V8 thump, and it comes out of the model on its
    /// own because the model is running the valves.
    ///
    /// Cross-plane firing (1-8-4-3-6-5-7-2) is what makes it BIG rather than flat: the banks fire
    /// unevenly, so each side of the car has its own ragged half-order pattern and the two beat
    /// against each other. Open collectors and a straight pipe take the muffler out of the way of
    /// all of it.
    /// </summary>
    // ── Four ways to exhaust the same V8 ────────────────────────────────────────────────────────
    //
    // Each of these changes the HARDWARE and nothing else: the same cylinders, the same firing
    // order, the same cam unless it is stated. They exist because a field of one car cannot show
    // that the character comes from the mechanism, and because the difference between open headers
    // and a packed can is the single clearest demonstration this engine has that nothing here is a
    // recording — no sample library ships the same engine four ways.

    /// <summary>
    /// OPEN HEADERS. The primaries dump straight into the air at the collector — no mid-pipe, no
    /// crossover, no muffler, no tailpipe.
    ///
    /// What you should hear: everything, unfiltered. Nothing cancels the harmonics and nothing
    /// absorbs them, so the whole series survives and the overrun cracks. It is also the only one
    /// with no muffler CASE, so none of the metallic ring the others have — the rawness is the
    /// absence of hardware, not the addition of any.
    /// </summary>
    public static EngineProfile V8OpenHeaders => V8MuscleBigBlock with
    {
        Name = "7.0 big block V8, open headers",
        Exhaust = V8MuscleBigBlock.Exhaust with
        {
            CollectorPipeMetres = 0.18f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.05f,
            Muffler = MufflerSpec.StraightPipe,
            TailpipeMetres = new[] { 0.10f },
        },
    };

    /// <summary>
    /// The same big block through GLASSPACKS: a packed straight-through can either side.
    ///
    /// What you should hear: the same engine, mellowed. The packing absorbs the top of the band
    /// rather than cancelling bands out of it, so the harmonics thin from the top down instead of
    /// being notched — and the packing is pressed against the case, so it damps that too and the
    /// metallic ring goes with it. Mellow is an ABSENCE here, which is why it cannot be faked by
    /// turning something down.
    /// </summary>
    public static EngineProfile V8BigBlockGlasspack => V8MuscleBigBlock with
    {
        Name = "7.0 big block V8, glasspacks",
        Exhaust = V8MuscleBigBlock.Exhaust with
        {
            Crossover = CrossoverKind.XPipe,
            Muffler = MufflerSpec.Glasspack with { Absorption = 0.55f },
        },
    };

    /// <summary>
    /// A MILD small block: shorter cam, smaller valves, stock manifolds rather than headers.
    ///
    /// What you should hear: the least dramatic car on the circuit, and deliberately. Cast log
    /// manifolds hold their primaries nowhere near equal — PrimarySpread 0.42 against a fabricated
    /// header's 0.12 — and eight pipes at eight pitches is a band where eight at one pitch is a
    /// tube. It idles straight because the cam is short, and it runs out of breath early.
    /// </summary>
    public static EngineProfile V8MildSmallBlock => V8SportsFlowmaster40 with
    {
        Name = "5.0 small block V8, stock manifolds",
        BoreMm = 101.6f, StrokeMm = 76.2f,
        ExhaustCam = new CamLobe { DurationDegrees = 258f, MaxLiftMm = 11.2f, RampFraction = 0.24f, CentrelineDegrees = 254f },
        IntakeCam = new CamLobe { DurationDegrees = 254f, MaxLiftMm = 11.2f, RampFraction = 0.24f, CentrelineDegrees = 470f },
        IdleRoughness = 0.10f, IdleRpm = 700f, RedlineRpm = 5600f,
        PeakTorqueNm = 420f, PeakTorqueRpm = 3200f,
        Exhaust = V8SportsFlowmaster40.Exhaust with
        {
            PrimaryLengthMetres = 0.30f, PrimarySpread = 0.42f, PrimaryDiameterMm = 38f,
            Muffler = MufflerSpec.Glasspack with { Absorption = 0.68f },
        },
    };

    /// <summary>
    /// The beefiest of them: a big block on a LONG cam, through 40-series chambered cans.
    ///
    /// What you should hear: an idle that will not sit still. A 330-degree cam overlaps so much at
    /// low lift that a cylinder breathes its neighbour's exhaust, the burn goes ragged, and the
    /// engine hunts — the lope is a misfire that nobody fixed because it is what the cam is for.
    /// Everything above the idle is the chambered can: notches where the chambers cancel, the
    /// harmonics between them surviving intact because nothing absorbs, and the case ringing.
    /// </summary>
    public static EngineProfile V8BigCam => V8MuscleBigBlock with
    {
        Name = "7.4 big block V8, long cam, 40-series",
        BoreMm = 111.8f, StrokeMm = 101.6f,
        ExhaustCam = new CamLobe { DurationDegrees = 330f, MaxLiftMm = 16.0f, RampFraction = 0.13f, CentrelineDegrees = 246f },
        IntakeCam = new CamLobe { DurationDegrees = 324f, MaxLiftMm = 16.0f, RampFraction = 0.13f, CentrelineDegrees = 462f },
        IdleRoughness = 1.0f, IdleGovernorGain = 1.2f,
        IdleRpm = 900f, RedlineRpm = 6400f,
        PeakTorqueNm = 880f, PeakTorqueRpm = 4200f,
        Exhaust = V8MuscleBigBlock.Exhaust with
        {
            PrimaryLengthMetres = 0.90f, PrimaryDiameterMm = 48f,
            Crossover = CrossoverKind.XPipe,
            Muffler = MufflerSpec.Chambered40,
        },
    };

    /// <summary>
    /// A litre sports bike: an inline four that revs to fourteen and a half thousand.
    ///
    /// The reason it sounds nothing like a car is not that it is small, it is that it is FAST. Four
    /// cylinders firing every 180 degrees at 14,500 rpm is a firing rate of 483 Hz — above the note
    /// of most cars' third harmonic — so the fundamental itself is a pitch rather than a beat, and
    /// the orders above it run into the kilohertz where the ear is most sensitive. Its valvetrain is
    /// busy for the same reason: sixteen valves closing 120 times a second each.
    ///
    /// Almost no exhaust to speak of. A short 4-into-1 and a can the size of a shoe, so nothing
    /// cancels and nothing absorbs.
    /// </summary>
    public static EngineProfile SportBike => new()
    {
        Name = "1000 cc inline-four sports bike",
        Layout = EngineLayout.Inline,
        FiringAngles = EvenFire(new[] { 1, 2, 4, 3 }),
        Bank = new int[4],
        BoreMm = 76f, StrokeMm = 55f, RodRatio = 1.72f, CompressionRatio = 13.0f,
        ExhaustCam = new CamLobe { DurationDegrees = 284f, MaxLiftMm = 9.4f, RampFraction = 0.14f, CentrelineDegrees = 250f },
        IntakeCam = new CamLobe { DurationDegrees = 280f, MaxLiftMm = 9.8f, RampFraction = 0.14f, CentrelineDegrees = 472f },
        ExhaustValve = new ValveSpec { DiameterMm = 24f, DischargeCoefficient = 0.70f },
        IntakeValve = new ValveSpec { DiameterMm = 30f, DischargeCoefficient = 0.72f },
        EvoTemperatureK = 1180f, IdleMapBar = 0.34f,
        // A stiff governor, because a bike has almost no flywheel — 0.055 kg m² against a big block's
        // 0.42 — so the same disturbance moves it eight times as far and a lazy governor lets the
        // idle hunt up past 2,500. Modern bikes hold theirs with an idle-air valve for exactly this
        // reason; it is the low inertia that makes the stiffness necessary, not the revs.
        IdleRoughness = 0.15f, IdleGovernorGain = 14f,
        IdleRpm = 1300f, RedlineRpm = 14500f,
        // Low inertia AND high losses, which is the whole of why a bike behaves as it does. The crank
        // is a tenth of a big block's — 0.055 against 0.42 — so anything that pushes it moves it a
        // long way, and the catch flare overshot to 4,500 rpm before the governor could get near it.
        // What brings it back is friction: a 1000 cc four at speed is pumping and rubbing far harder
        // for its size than a lazy V8 is, which is why a bike's revs FALL as fast as they rise and a
        // big block's coast down. Raising the losses to match the inertia settles it without a
        // governor stiff enough to be doing the physics' job for it.
        InertiaKgM2 = 0.055f, FrictionNm = 12.5f, FrictionNmPerKrpm = 5.0f,
        PeakTorqueNm = 112f, PeakTorqueRpm = 11000f,
        // Sixteen valves at very high speed: a bike's top end is a large part of its voice.
        Mechanical = new MechanicalSpec { ValvetrainLevel = 1.0f, CombustionKnock = 0.08f, AccessoryWhineLevel = 0.15f, AccessoryWhineOrder = 2.5f },
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.42f, PrimarySpread = 0.10f, PrimaryDiameterMm = 34f,
            CollectorDiameterMm = 50f, CollectorPipeMetres = 0.30f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.15f,
            Muffler = MufflerSpec.Glasspack with { Absorption = 0.25f, AbsorptiveLengthMetres = 0.24f },
            TailpipeMetres = new[] { 0.12f },
            TailpipeDiameterMm = 50f,
        },
    };

    /// <summary>
    /// A blown big block: 7.4 litres with a Roots supercharger sitting on top of it.
    ///
    /// The blower is the point, and it is two sounds rather than one. It WHINES, because a pair of
    /// meshing rotors pumps in discrete gulps and that gulp rate is a high multiple of engine speed —
    /// a pitch that rises with the revs and sits right on top of the exhaust note. And it MOVES AIR,
    /// so every cylinder gets more of it: more pressure, more torque, a harder blowdown into the
    /// pipes. The whine is what everyone recognises; the second is what makes it sound heavy.
    ///
    /// Unlike a turbo it has no lag worth the name. It is geared to the crank, so it is making boost
    /// at idle and there is nothing to spool — which is exactly why it sounds instant and a turbo
    /// does not.
    /// </summary>
    public static EngineProfile V8Blown => V8BigCam with
    {
        Name = "7.4 blown big block V8, 40-series",
        Induction = Induction.Supercharged,
        BoostBar = 0.75f,
        IdleRpm = 950f, RedlineRpm = 6200f,
        PeakTorqueNm = 1180f, PeakTorqueRpm = 4400f,
        InertiaKgM2 = 0.52f, FrictionNm = 74f, FrictionNmPerKrpm = 26f,
        // Order twelve: three lobes on each of two rotors, geared above the crank. At 4,000 rpm that
        // is 800 Hz, which is where a blower actually sits.
        Mechanical = V8BigCam.Mechanical with { BlowerWhineOrder = 12f, BlowerWhineLevel = 0.85f },
    };

    public static EngineProfile PoliceV8 => new()
    {
        Name = "7.0 interceptor V8, lopey cam, open pipes",
        Layout = EngineLayout.Vee,
        FiringAngles = EvenFire(new[] { 1, 8, 4, 3, 6, 5, 7, 2 }),
        Bank = AlternatingBanks(8),
        BoreMm = 108f, StrokeMm = 95.5f, RodRatio = 1.62f, CompressionRatio = 11.2f,
        // 102-degree lobe centres: exhaust centreline pulled in, intake pulled back.
        ExhaustCam = new CamLobe { DurationDegrees = 320f, MaxLiftMm = 16.5f, RampFraction = 0.14f, CentrelineDegrees = 258f },
        IntakeCam = new CamLobe { DurationDegrees = 316f, MaxLiftMm = 16.5f, RampFraction = 0.14f, CentrelineDegrees = 462f },
        ExhaustValve = new ValveSpec { DiameterMm = 50f, DischargeCoefficient = 0.70f },
        IntakeValve = new ValveSpec { DiameterMm = 58f, DischargeCoefficient = 0.70f },
        EvoTemperatureK = 1240f, IdleMapBar = 0.42f,
        // It cannot idle smoothly and should not pretend to. This is the lope.
        IdleRoughness = 1.0f, IdleGovernorGain = 1.2f,
        IdleRpm = 950f, RedlineRpm = 6800f,
        InertiaKgM2 = 0.38f, FrictionNm = 55f, FrictionNmPerKrpm = 20f,
        PeakTorqueNm = 810f, PeakTorqueRpm = 4200f,
        Exhaust = new ExhaustSpec
        {
            // Long tube headers into a short collector and then out. No muffler: this is the scream.
            PrimaryLengthMetres = 0.95f, PrimarySpread = 0.06f, PrimaryDiameterMm = 50.8f,
            CollectorDiameterMm = 89f, CollectorPipeMetres = 0.40f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.35f,
            Muffler = MufflerSpec.StraightPipe,
            Steepening = 1.3f,
            TailpipeMetres = new[] { 0.45f, 0.50f },
            TailpipeDiameterMm = 89f,
            GasCelsiusIdle = 400f, GasCelsiusFull = 950f,
            WallLossMultiplier = 1.15f,
            // A big cam on a closed throttle at speed is where the bangs come from.
            OverrunPopRate = 14f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.20f, RunnerDiameterMm = 54f, PlenumLitres = 7f, ThrottleDiameterMm = 105f, AirboxLitres = 6f, SnorkelLengthMetres = 0.25f, SnorkelDiameterMm = 110f, Level = 1.0f, Absorption = 0.1f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 1.1f, CombustionKnock = 0.08f, AccessoryWhineLevel = 0.1f },
    };

    /// <summary>A modern 6.2-litre pushrod V8 with a mild street cam, shorty headers, an H-pipe and
    /// 40-series chambered mufflers. GM LS firing order 1-8-7-2-6-5-4-3.</summary>
    public static EngineProfile V8SportsFlowmaster40 => new()
    {
        Name = "6.2 V8, H-pipe, Flowmaster 40s",
        Layout = EngineLayout.Vee,
        FiringAngles = EvenFire(new[] { 1, 8, 7, 2, 6, 5, 4, 3 }),
        Bank = AlternatingBanks(8),
        BoreMm = 103.25f, StrokeMm = 92f, RodRatio = 1.67f, CompressionRatio = 10.7f,
        ExhaustCam = new CamLobe { DurationDegrees = 284f, MaxLiftMm = 13.7f, RampFraction = 0.2f, CentrelineDegrees = 252f },
        IntakeCam = new CamLobe { DurationDegrees = 280f, MaxLiftMm = 13.7f, RampFraction = 0.2f, CentrelineDegrees = 474f },
        ExhaustValve = new ValveSpec { DiameterMm = 40.4f, DischargeCoefficient = 0.68f },
        IntakeValve = new ValveSpec { DiameterMm = 52f, DischargeCoefficient = 0.68f },
        EvoTemperatureK = 1150f, IdleMapBar = 0.38f,
        IdleRoughness = 0.25f, IdleGovernorGain = 3f,
        IdleRpm = 780f, RedlineRpm = 6600f,
        InertiaKgM2 = 0.28f, FrictionNm = 46.8f, FrictionNmPerKrpm = 17.2f,
        PeakTorqueNm = 624f, PeakTorqueRpm = 4600f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.55f, PrimarySpread = 0.26f, PrimaryDiameterMm = 44f,
            CollectorDiameterMm = 70f, CollectorPipeMetres = 1.0f,
            Crossover = CrossoverKind.HPipe, CrossoverTubeMetres = 0.35f, CrossoverArea = 0.56f,
            MidPipeMetres = 1.2f,
            Muffler = MufflerSpec.Chambered40,
            TailpipeMetres = new[] { 0.6f, 0.66f },
            TailpipeDiameterMm = 70f,
            GasCelsiusIdle = 340f, GasCelsiusFull = 840f,
            OverrunPopRate = 6f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.25f, RunnerDiameterMm = 46f, PlenumLitres = 5f, ThrottleDiameterMm = 90f, AirboxLitres = 9f, SnorkelLengthMetres = 0.5f, Level = 0.5f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.5f, CombustionKnock = 0.04f },
    };

    /// <summary>A 4.5-litre flat-plane V8: 180-degree crank, each bank fires evenly every 180 degrees,
    /// equal-length headers and an X-pipe. Same eight cylinders as the muscle car; it screams instead.</summary>
    public static EngineProfile V8FlatPlane => new()
    {
        Name = "4.5 flat-plane V8, X-pipe",
        Layout = EngineLayout.Vee,
        FiringAngles = EvenFire(new[] { 1, 5, 3, 7, 4, 8, 2, 6 }),
        Bank = HalfBanks(8),
        BoreMm = 94f, StrokeMm = 81f, RodRatio = 1.85f, CompressionRatio = 12.5f,
        ExhaustCam = new CamLobe { DurationDegrees = 276f, MaxLiftMm = 11f, RampFraction = 0.2f, CentrelineDegrees = 254f },
        IntakeCam = new CamLobe { DurationDegrees = 272f, MaxLiftMm = 11f, RampFraction = 0.2f, CentrelineDegrees = 470f },
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 31f, DischargeCoefficient = 0.7f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 36f, DischargeCoefficient = 0.7f },
        EvoTemperatureK = 1180f, IdleMapBar = 0.33f,
        IdleRoughness = 0.1f, IdleGovernorGain = 4f,
        IdleRpm = 900f, RedlineRpm = 9000f,
        InertiaKgM2 = 0.14f, FrictionNm = 34.2f, FrictionNmPerKrpm = 12.6f,
        PeakTorqueNm = 540f, PeakTorqueRpm = 6000f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.62f, PrimarySpread = 0.03f, PrimaryDiameterMm = 42f,
            CollectorDiameterMm = 63f, CollectorPipeMetres = 0.7f,
            Crossover = CrossoverKind.XPipe,
            MidPipeMetres = 1.1f,
            Muffler = MufflerSpec.Glasspack with { Absorption = 0.4f },
            TailpipeMetres = new[] { 0.5f, 0.55f },
            TailpipeDiameterMm = 63f,
            GasCelsiusIdle = 380f, GasCelsiusFull = 900f,
            WallLossMultiplier = 1.3f,
            OverrunPopRate = 10f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.28f, RunnerDiameterMm = 40f, PlenumLitres = 3.5f, ThrottleDiameterMm = 70f, AirboxLitres = 6f, SnorkelLengthMetres = 0.35f, Level = 0.8f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.6f, CombustionKnock = 0.03f },
    };

    /// <summary>A 1.6-litre economy four: stock cam, cast manifold into a single system, a quiet
    /// baffled muffler. Fires evenly every 180 degrees, 1-3-4-2.</summary>
    public static EngineProfile Inline4Economy => new()
    {
        Name = "1.6 inline-4, stock exhaust",
        Layout = EngineLayout.Inline,
        FiringAngles = EvenFire(new[] { 1, 3, 4, 2 }),
        Bank = OneBank(4),
        BoreMm = 79f, StrokeMm = 81.5f, RodRatio = 1.65f, CompressionRatio = 10.5f,
        ExhaustCam = new CamLobe { DurationDegrees = 236f, MaxLiftMm = 8.5f, RampFraction = 0.25f, CentrelineDegrees = 258f },
        IntakeCam = new CamLobe { DurationDegrees = 240f, MaxLiftMm = 9f, RampFraction = 0.25f, CentrelineDegrees = 478f },
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 26f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 30f },
        EvoTemperatureK = 1100f, IdleMapBar = 0.3f,
        IdleRoughness = 0.05f, IdleGovernorGain = 4f,
        IdleRpm = 750f, RedlineRpm = 6500f,
        InertiaKgM2 = 0.11f, FrictionNm = 12.2f, FrictionNmPerKrpm = 4.5f,
        PeakTorqueNm = 150f, PeakTorqueRpm = 4000f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.18f, PrimarySpread = 0.45f, PrimaryDiameterMm = 34f,
            CollectorGroups = new[] { new[] { 0, 1, 2, 3 } },
            CollectorDiameterMm = 50f, CollectorPipeMetres = 0.8f,
            Crossover = CrossoverKind.Merged,
            MidPipeMetres = 1.6f,
            Muffler = MufflerSpec.Stock,
            TailpipeMetres = new[] { 0.45f },
            TailpipeDiameterMm = 48f,
            GasCelsiusIdle = 300f, GasCelsiusFull = 780f,
            WallLossMultiplier = 2.5f,
            OverrunPopRate = 0.5f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.35f, RunnerDiameterMm = 36f, PlenumLitres = 2.5f, ThrottleDiameterMm = 55f, AirboxLitres = 7f, SnorkelLengthMetres = 0.6f, SnorkelDiameterMm = 60f, Level = 0.25f, Absorption = 0.5f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.35f, CombustionKnock = 0.03f, AccessoryWhineLevel = 0.12f },
    };

    /// <summary>A 2.0-litre sport four on a 4-into-1 header with a straight-through muffler: the
    /// hot-hatch rasp.</summary>
    public static EngineProfile Inline4Sport => new()
    {
        Name = "2.0 inline-4, 4-1 header, straight-through",
        Layout = EngineLayout.Inline,
        FiringAngles = EvenFire(new[] { 1, 3, 4, 2 }),
        Bank = OneBank(4),
        BoreMm = 86f, StrokeMm = 86f, RodRatio = 1.6f, CompressionRatio = 11.5f,
        ExhaustCam = new CamLobe { DurationDegrees = 272f, MaxLiftMm = 10.5f, RampFraction = 0.18f, CentrelineDegrees = 252f },
        IntakeCam = new CamLobe { DurationDegrees = 268f, MaxLiftMm = 11f, RampFraction = 0.18f, CentrelineDegrees = 470f },
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 29f, DischargeCoefficient = 0.68f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 34f, DischargeCoefficient = 0.68f },
        EvoTemperatureK = 1150f, IdleMapBar = 0.34f,
        IdleRoughness = 0.15f, IdleGovernorGain = 3.5f,
        IdleRpm = 850f, RedlineRpm = 8200f,
        InertiaKgM2 = 0.09f, FrictionNm = 15.2f, FrictionNmPerKrpm = 5.6f,
        PeakTorqueNm = 210f, PeakTorqueRpm = 5500f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.70f, PrimarySpread = 0.06f, PrimaryDiameterMm = 42f,
            CollectorGroups = new[] { new[] { 0, 1, 2, 3 } },
            CollectorDiameterMm = 63f, CollectorPipeMetres = 0.9f,
            Crossover = CrossoverKind.Merged,
            MidPipeMetres = 1.3f,
            Muffler = MufflerSpec.Glasspack,
            TailpipeMetres = new[] { 0.5f },
            TailpipeDiameterMm = 63f,
            GasCelsiusIdle = 360f, GasCelsiusFull = 870f,
            WallLossMultiplier = 1.4f,
            OverrunPopRate = 7f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.30f, RunnerDiameterMm = 40f, PlenumLitres = 2.8f, ThrottleDiameterMm = 65f, AirboxLitres = 5f, SnorkelLengthMetres = 0.4f, Level = 0.7f, Absorption = 0.2f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.5f, CombustionKnock = 0.03f },
    };

    /// <summary>
    /// The same 2.0 four with a turbo bolted to it — and almost everything about how it SOUNDS
    /// follows from that one change rather than from any of it being described separately.
    ///
    /// Compression comes down (9.4 from 11.5) because you cannot run eleven-to-one on boost, which
    /// takes some of the hard edge off the combustion event. The cam loses overlap, because a turbo
    /// engine does not want exhaust reversion diluting a pressurised intake charge, and less overlap
    /// is a cleaner idle — a boosted engine idles smoother than the naturally aspirated version of
    /// itself, which surprises people. The exhaust gets bigger and quieter downstream because the
    /// turbine is a muffler: it takes the sharp pressure pulses and turns them into shaft work, which
    /// is why a turbo car sounds flat and woofly next to the crack of an atmospheric one, and why the
    /// interesting noise moves to the INTAKE side.
    ///
    /// The whistle, the spool lag and the way boost raises airbox pressure are already in the
    /// synthesis; this is the first petrol engine to ask for them.
    /// </summary>
    public static EngineProfile I4Turbo => new()
    {
        Name = "2.0 turbo inline-4",
        Layout = EngineLayout.Inline,
        Fuel = FuelType.Petrol, Induction = Induction.Turbocharged, BoostBar = 1.45f,
        FiringAngles = EvenFire(new[] { 1, 3, 4, 2 }),
        Bank = OneBank(4),
        BoreMm = 86f, StrokeMm = 86f, RodRatio = 1.6f, CompressionRatio = 9.4f,
        // Tight lobe centres and little overlap: on boost the intake is at higher pressure than the
        // exhaust, so overlap would blow fresh charge straight out of the pipe.
        ExhaustCam = new CamLobe { DurationDegrees = 252f, MaxLiftMm = 10f, RampFraction = 0.2f, CentrelineDegrees = 246f },
        IntakeCam = new CamLobe { DurationDegrees = 248f, MaxLiftMm = 10.5f, RampFraction = 0.2f, CentrelineDegrees = 478f },
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 28f, DischargeCoefficient = 0.66f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 34f, DischargeCoefficient = 0.68f },
        EvoTemperatureK = 1120f, IdleMapBar = 0.38f,
        IdleRoughness = 0.09f, IdleGovernorGain = 3.5f,
        IdleRpm = 820f, RedlineRpm = 6800f,
        InertiaKgM2 = 0.11f, FrictionNm = 16f, FrictionNmPerKrpm = 5.2f,
        // Torque arrives early and stays: the defining shape of a boosted engine, and the reason it
        // needs fewer gears and pulls from nothing.
        PeakTorqueNm = 380f, PeakTorqueRpm = 3200f,
        Exhaust = new ExhaustSpec
        {
            // Short primaries into a close-coupled turbine, then a big soft system after it.
            PrimaryLengthMetres = 0.34f, PrimarySpread = 0.03f, PrimaryDiameterMm = 40f,
            CollectorGroups = new[] { new[] { 0, 1, 2, 3 } },
            CollectorDiameterMm = 60f, CollectorPipeMetres = 0.35f,
            Crossover = CrossoverKind.Merged,
            MidPipeMetres = 2.0f,
            Muffler = MufflerSpec.Glasspack,
            TailpipeMetres = new[] { 0.6f },
            TailpipeDiameterMm = 70f,
            // The turbine drops a lot of heat and most of the pulse energy across itself.
            GasCelsiusIdle = 300f, GasCelsiusFull = 720f,
            WallLossMultiplier = 2.1f,
            OverrunPopRate = 11f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.26f, RunnerDiameterMm = 42f, PlenumLitres = 3.2f, ThrottleDiameterMm = 70f, AirboxLitres = 7f, SnorkelLengthMetres = 0.5f, Level = 0.95f, Absorption = 0.15f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.45f, CombustionKnock = 0.05f, TurboWhistleLevel = 0.85f, TurboLagSeconds = 0.38f },
    };

    /// <summary>A 3.0-litre straight six on two 3-into-1 headers joined into a single system: silky
    /// 120-degree firing, each collector seeing an even 240.</summary>
    public static EngineProfile Inline6 => new()
    {
        Name = "3.0 inline-6, twin 3-1 headers",
        Layout = EngineLayout.Inline,
        FiringAngles = EvenFire(new[] { 1, 5, 3, 6, 2, 4 }),
        Bank = OneBank(6),
        BoreMm = 84f, StrokeMm = 89.6f, RodRatio = 1.6f, CompressionRatio = 10.5f,
        ExhaustCam = new CamLobe { DurationDegrees = 258f, MaxLiftMm = 9.5f, RampFraction = 0.22f, CentrelineDegrees = 256f },
        IntakeCam = new CamLobe { DurationDegrees = 256f, MaxLiftMm = 9.8f, RampFraction = 0.22f, CentrelineDegrees = 474f },
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 28f, DischargeCoefficient = 0.66f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 32f, DischargeCoefficient = 0.66f },
        EvoTemperatureK = 1120f, IdleMapBar = 0.32f,
        IdleRoughness = 0.08f, IdleGovernorGain = 4f,
        IdleRpm = 700f, RedlineRpm = 7000f,
        InertiaKgM2 = 0.2f, FrictionNm = 22.8f, FrictionNmPerKrpm = 8.4f,
        PeakTorqueNm = 300f, PeakTorqueRpm = 4200f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.55f, PrimarySpread = 0.12f, PrimaryDiameterMm = 38f,
            CollectorGroups = new[] { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } },
            CollectorDiameterMm = 57f, CollectorPipeMetres = 0.8f,
            Crossover = CrossoverKind.Merged,
            MidPipeMetres = 1.5f,
            Muffler = MufflerSpec.Glasspack with { Absorption = 0.5f },
            TailpipeMetres = new[] { 0.55f },
            TailpipeDiameterMm = 63f,
            GasCelsiusIdle = 340f, GasCelsiusFull = 830f,
            WallLossMultiplier = 1.8f,
            OverrunPopRate = 4f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.32f, RunnerDiameterMm = 38f, PlenumLitres = 4f, ThrottleDiameterMm = 70f, AirboxLitres = 7f, SnorkelLengthMetres = 0.5f, Level = 0.45f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.4f, CombustionKnock = 0.03f },
    };

    /// <summary>A 3.5-litre 60-degree V6 with a stock system: even 120-degree firing, each bank an
    /// uneven... no — each bank fires evenly every 240 with the order 1-2-3-4-5-6 and alternating banks.</summary>
    public static EngineProfile V6Sedan => new()
    {
        Name = "3.5 V6, stock",
        Layout = EngineLayout.Vee,
        FiringAngles = EvenFire(new[] { 1, 2, 3, 4, 5, 6 }),
        Bank = AlternatingBanks(6),
        BoreMm = 94f, StrokeMm = 83f, RodRatio = 1.7f, CompressionRatio = 10.8f,
        ExhaustCam = new CamLobe { DurationDegrees = 250f, MaxLiftMm = 9.5f, RampFraction = 0.24f, CentrelineDegrees = 256f },
        IntakeCam = new CamLobe { DurationDegrees = 252f, MaxLiftMm = 10f, RampFraction = 0.24f, CentrelineDegrees = 476f },
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 31f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 36f },
        EvoTemperatureK = 1120f, IdleMapBar = 0.31f,
        IdleRoughness = 0.06f, IdleGovernorGain = 4f,
        IdleRpm = 680f, RedlineRpm = 6800f,
        InertiaKgM2 = 0.18f, FrictionNm = 26.6f, FrictionNmPerKrpm = 9.8f,
        PeakTorqueNm = 340f, PeakTorqueRpm = 4500f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.22f, PrimarySpread = 0.35f, PrimaryDiameterMm = 38f,
            CollectorDiameterMm = 57f, CollectorPipeMetres = 0.9f,
            Crossover = CrossoverKind.Merged,
            MidPipeMetres = 1.4f,
            Muffler = MufflerSpec.Stock,
            TailpipeMetres = new[] { 0.5f },
            TailpipeDiameterMm = 57f,
            GasCelsiusIdle = 320f, GasCelsiusFull = 800f,
            WallLossMultiplier = 2.5f,
            OverrunPopRate = 0.5f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.3f, RunnerDiameterMm = 40f, PlenumLitres = 4f, ThrottleDiameterMm = 75f, AirboxLitres = 8f, SnorkelLengthMetres = 0.6f, Level = 0.25f, Absorption = 0.5f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.3f, CombustionKnock = 0.03f },
    };

    /// <summary>A 45-degree V-twin, 1.75 litres, both pistons on one crankpin: fires at 315 then 405
    /// degrees — the potato-potato — into two short straight pipes.</summary>
    public static EngineProfile VTwin45 => new()
    {
        Name = "1.75 V-twin, 45 degrees, straight pipes",
        Layout = EngineLayout.Vee,
        FiringAngles = IntervalFire(new[] { 1, 2 }, new[] { 315f, 405f }),
        Bank = new[] { 0, 1 },
        BoreMm = 100f, StrokeMm = 111f, RodRatio = 1.75f, CompressionRatio = 10f,
        ExhaustCam = new CamLobe { DurationDegrees = 258f, MaxLiftMm = 12f, RampFraction = 0.22f, CentrelineDegrees = 252f },
        IntakeCam = new CamLobe { DurationDegrees = 262f, MaxLiftMm = 12.5f, RampFraction = 0.22f, CentrelineDegrees = 470f },
        ExhaustValve = new ValveSpec { DiameterMm = 40f, DischargeCoefficient = 0.6f },
        IntakeValve = new ValveSpec { DiameterMm = 48f, DischargeCoefficient = 0.6f },
        EvoTemperatureK = 1100f, IdleMapBar = 0.4f,
        IdleRoughness = 0.5f, IdleGovernorGain = 2f,
        IdleRpm = 950f, RedlineRpm = 5600f,
        InertiaKgM2 = 0.09f, FrictionNm = 13.3f, FrictionNmPerKrpm = 4.9f,
        PeakTorqueNm = 150f, PeakTorqueRpm = 3200f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthsMetres = new[] { 0.55f, 0.85f }, PrimaryDiameterMm = 45f,
            CollectorGroups = new[] { new[] { 0 }, new[] { 1 } },
            CollectorDiameterMm = 45f, CollectorPipeMetres = 0.25f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.2f,
            Muffler = MufflerSpec.Glasspack with { Absorption = 0.3f, AbsorptiveLengthMetres = 0.3f },
            TailpipeMetres = new[] { 0.25f, 0.35f },
            TailpipeDiameterMm = 50f,
            GasCelsiusIdle = 330f, GasCelsiusFull = 800f,
            WallLossMultiplier = 1.2f,
            OverrunPopRate = 12f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.12f, RunnerDiameterMm = 45f, PlenumLitres = 0.6f, ThrottleDiameterMm = 50f, AirboxLitres = 2f, SnorkelLengthMetres = 0.15f, SnorkelDiameterMm = 60f, Level = 1f, Absorption = 0.1f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 1.2f, CombustionKnock = 0.08f, AccessoryWhineLevel = 0.05f },
    };

    /// <summary>A 450 cc single: one bang every 720 degrees, a short pipe and a small can. The
    /// crank speed ripples enormously between firings, which is most of what a thumper sounds like.</summary>
    public static EngineProfile Single450 => new()
    {
        Name = "450 single",
        Layout = EngineLayout.Inline,
        FiringAngles = new[] { 0f },
        Bank = new[] { 0 },
        BoreMm = 96f, StrokeMm = 62.1f, RodRatio = 1.7f, CompressionRatio = 12.5f,
        ExhaustCam = new CamLobe { DurationDegrees = 270f, MaxLiftMm = 9.5f, RampFraction = 0.2f, CentrelineDegrees = 250f },
        IntakeCam = new CamLobe { DurationDegrees = 268f, MaxLiftMm = 10.5f, RampFraction = 0.2f, CentrelineDegrees = 470f },
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 31f, DischargeCoefficient = 0.68f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 37f, DischargeCoefficient = 0.68f },
        EvoTemperatureK = 1180f, IdleMapBar = 0.36f,
        IdleRoughness = 0.4f, IdleGovernorGain = 2.5f,
        IdleRpm = 1500f, RedlineRpm = 11000f,
        InertiaKgM2 = 0.02f, FrictionNm = 3.4f, FrictionNmPerKrpm = 1.3f,
        PeakTorqueNm = 48f, PeakTorqueRpm = 7000f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.75f, PrimaryDiameterMm = 42f,
            CollectorGroups = new[] { new[] { 0 } },
            CollectorDiameterMm = 42f, CollectorPipeMetres = 0.3f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.2f,
            Muffler = MufflerSpec.Glasspack with { Absorption = 0.45f, AbsorptiveLengthMetres = 0.35f },
            TailpipeMetres = new[] { 0.15f },
            TailpipeDiameterMm = 45f,
            GasCelsiusIdle = 350f, GasCelsiusFull = 850f,
            WallLossMultiplier = 1.3f,
            OverrunPopRate = 6f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.1f, RunnerDiameterMm = 44f, PlenumLitres = 0.3f, ThrottleDiameterMm = 44f, AirboxLitres = 4f, SnorkelLengthMetres = 0.2f, SnorkelDiameterMm = 55f, Level = 0.9f, Absorption = 0.3f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.9f, CombustionKnock = 0.06f },
    };

    /// <summary>A 2.8-litre four-cylinder turbo-diesel pickup: high compression, injection knock,
    /// a turbo on the manifold, a single quiet system.</summary>
    public static EngineProfile DieselPickupI4 => new()
    {
        Name = "2.8 turbo-diesel inline-4",
        Layout = EngineLayout.Inline,
        Fuel = FuelType.Diesel, Induction = Induction.Turbocharged, BoostBar = 1.4f,
        FiringAngles = EvenFire(new[] { 1, 3, 4, 2 }),
        Bank = OneBank(4),
        BoreMm = 94f, StrokeMm = 100f, RodRatio = 1.6f, CompressionRatio = 16.5f,
        ExhaustCam = new CamLobe { DurationDegrees = 232f, MaxLiftMm = 8f, RampFraction = 0.26f, CentrelineDegrees = 258f },
        IntakeCam = new CamLobe { DurationDegrees = 230f, MaxLiftMm = 8f, RampFraction = 0.26f, CentrelineDegrees = 478f },
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 28f, DischargeCoefficient = 0.6f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 30f, DischargeCoefficient = 0.6f },
        EvoTemperatureK = 1050f, IdleMapBar = 0.98f,
        CombustionVariation = 0.02f, IdleRoughness = 0.12f, IdleGovernorGain = 5f,
        IdleRpm = 750f, RedlineRpm = 4400f,
        InertiaKgM2 = 0.3f, FrictionNm = 34.0f, FrictionNmPerKrpm = 11.0f,
        PeakTorqueNm = 450f, PeakTorqueRpm = 1800f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.15f, PrimarySpread = 0.4f, PrimaryDiameterMm = 34f,
            CollectorGroups = new[] { new[] { 0, 1, 2, 3 } },
            CollectorDiameterMm = 57f, CollectorPipeMetres = 0.9f,
            Crossover = CrossoverKind.Merged,
            MidPipeMetres = 2.2f,
            Muffler = MufflerSpec.Stock with { Absorption = 0.55f, ResonatorHz = 60f },
            TailpipeMetres = new[] { 0.9f },
            TailpipeDiameterMm = 57f,
            GasCelsiusIdle = 180f, GasCelsiusFull = 650f,
            WallLossMultiplier = 2.5f,
            OverrunPopRate = 0f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.2f, RunnerDiameterMm = 36f, PlenumLitres = 3f, ThrottleDiameterMm = 60f, AirboxLitres = 9f, SnorkelLengthMetres = 0.7f, Level = 0.3f, Absorption = 0.5f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.6f, CombustionKnock = 1.0f, AccessoryWhineLevel = 0.15f, TurboWhistleLevel = 0.5f, TurboLagSeconds = 0.9f },
    };

    /// <summary>A 13-litre straight-six truck diesel: 600 rpm idle, huge cylinders, a vertical stack.</summary>
    public static EngineProfile DieselTruckI6 => new()
    {
        Name = "13 litre truck diesel inline-6",
        Layout = EngineLayout.Inline,
        Fuel = FuelType.Diesel, Induction = Induction.Turbocharged, BoostBar = 2.2f,
        FiringAngles = EvenFire(new[] { 1, 5, 3, 6, 2, 4 }),
        Bank = OneBank(6),
        BoreMm = 130f, StrokeMm = 160f, RodRatio = 1.65f, CompressionRatio = 17f,
        ExhaustCam = new CamLobe { DurationDegrees = 240f, MaxLiftMm = 12f, RampFraction = 0.26f, CentrelineDegrees = 256f },
        IntakeCam = new CamLobe { DurationDegrees = 236f, MaxLiftMm = 12f, RampFraction = 0.26f, CentrelineDegrees = 476f },
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 40f, DischargeCoefficient = 0.6f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 44f, DischargeCoefficient = 0.6f },
        EvoTemperatureK = 1000f, IdleMapBar = 1.0f,
        CombustionVariation = 0.02f, IdleRoughness = 0.15f, IdleGovernorGain = 6f,
        IdleRpm = 600f, RedlineRpm = 2100f,
        InertiaKgM2 = 2.4f, FrictionNm = 154.4f, FrictionNmPerKrpm = 49.8f,
        PeakTorqueNm = 2400f, PeakTorqueRpm = 1200f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.25f, PrimarySpread = 0.5f, PrimaryDiameterMm = 55f,
            CollectorGroups = new[] { new[] { 0, 1, 2, 3, 4, 5 } },
            CollectorDiameterMm = 120f, CollectorPipeMetres = 0.8f,
            Crossover = CrossoverKind.Merged,
            MidPipeMetres = 1.6f,
            Muffler = MufflerSpec.Stock with { ChamberLengthsMetres = new[] { 0.3f, 0.4f }, ExpansionRatio = 7f, Absorption = 0.5f, ResonatorHz = 40f },
            TailpipeMetres = new[] { 2.4f },
            TailpipeDiameterMm = 120f,
            GasCelsiusIdle = 160f, GasCelsiusFull = 600f,
            WallLossMultiplier = 2f,
            OverrunPopRate = 0f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.25f, RunnerDiameterMm = 50f, PlenumLitres = 12f, ThrottleDiameterMm = 100f, AirboxLitres = 30f, SnorkelLengthMetres = 1.2f, SnorkelDiameterMm = 120f, Level = 0.35f, Absorption = 0.5f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.8f, CombustionKnock = 1.3f, AccessoryWhineLevel = 0.2f, TurboWhistleLevel = 0.9f, TurboLagSeconds = 1.4f },
    };

    /// <summary>A 2.5-litre flat four with unequal-length headers: fires evenly every 180 but each
    /// bank sees 180 then 540, and through unequal pipes that gap is the boxer rumble.</summary>
    public static EngineProfile Boxer4 => new()
    {
        Name = "2.5 flat-4, unequal headers",
        Layout = EngineLayout.Flat,
        FiringAngles = EvenFire(new[] { 1, 3, 2, 4 }),
        Bank = new[] { 0, 1, 0, 1 },
        BoreMm = 99.5f, StrokeMm = 79f, RodRatio = 1.65f, CompressionRatio = 10f,
        ExhaustCam = new CamLobe { DurationDegrees = 250f, MaxLiftMm = 9.5f, RampFraction = 0.24f, CentrelineDegrees = 256f },
        IntakeCam = new CamLobe { DurationDegrees = 252f, MaxLiftMm = 10f, RampFraction = 0.24f, CentrelineDegrees = 476f },
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 31f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 36f },
        EvoTemperatureK = 1120f, IdleMapBar = 0.32f,
        IdleRoughness = 0.15f, IdleGovernorGain = 3.5f,
        IdleRpm = 700f, RedlineRpm = 6500f,
        InertiaKgM2 = 0.16f, FrictionNm = 19.0f, FrictionNmPerKrpm = 7.0f,
        PeakTorqueNm = 240f, PeakTorqueRpm = 4000f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthsMetres = new[] { 0.45f, 0.85f, 0.50f, 0.90f }, PrimaryDiameterMm = 42f,
            CollectorGroups = new[] { new[] { 0, 2 }, new[] { 1, 3 } },
            CollectorDiameterMm = 57f, CollectorPipeMetres = 0.5f,
            Crossover = CrossoverKind.Merged,
            MidPipeMetres = 1.5f,
            Muffler = MufflerSpec.Glasspack with { Absorption = 0.5f },
            TailpipeMetres = new[] { 0.5f },
            TailpipeDiameterMm = 63f,
            GasCelsiusIdle = 330f, GasCelsiusFull = 820f,
            WallLossMultiplier = 2f,
            OverrunPopRate = 3f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.28f, RunnerDiameterMm = 40f, PlenumLitres = 3.5f, ThrottleDiameterMm = 65f, AirboxLitres = 7f, SnorkelLengthMetres = 0.5f, Level = 0.4f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.4f, CombustionKnock = 0.03f },
    };

    /// <summary>An 8.4-litre V10: 90-degree block with split pins for even 72-degree firing, each
    /// bank an even 144, side pipes.</summary>
    public static EngineProfile V10 => new()
    {
        Name = "8.4 V10, side pipes",
        Layout = EngineLayout.Vee,
        FiringAngles = EvenFire(new[] { 1, 10, 9, 4, 3, 6, 5, 8, 7, 2 }),
        Bank = AlternatingBanks(10),
        BoreMm = 103f, StrokeMm = 100.6f, RodRatio = 1.6f, CompressionRatio = 10.2f,
        ExhaustCam = new CamLobe { DurationDegrees = 282f, MaxLiftMm = 14f, RampFraction = 0.18f, CentrelineDegrees = 252f },
        IntakeCam = new CamLobe { DurationDegrees = 278f, MaxLiftMm = 14f, RampFraction = 0.18f, CentrelineDegrees = 470f },
        ExhaustValve = new ValveSpec { DiameterMm = 42f, DischargeCoefficient = 0.66f },
        IntakeValve = new ValveSpec { DiameterMm = 52f, DischargeCoefficient = 0.66f },
        EvoTemperatureK = 1180f, IdleMapBar = 0.4f,
        IdleRoughness = 0.3f, IdleGovernorGain = 2.5f,
        IdleRpm = 750f, RedlineRpm = 6200f,
        InertiaKgM2 = 0.36f, FrictionNm = 63.8f, FrictionNmPerKrpm = 23.5f,
        PeakTorqueNm = 813f, PeakTorqueRpm = 5000f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.5f, PrimarySpread = 0.15f, PrimaryDiameterMm = 44f,
            CollectorDiameterMm = 76f, CollectorPipeMetres = 0.6f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.5f,
            Muffler = MufflerSpec.Glasspack with { Absorption = 0.35f, AbsorptiveLengthMetres = 0.6f },
            TailpipeMetres = new[] { 0.4f, 0.44f },
            TailpipeDiameterMm = 76f,
            GasCelsiusIdle = 350f, GasCelsiusFull = 850f,
            WallLossMultiplier = 1.4f,
            OverrunPopRate = 8f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.26f, RunnerDiameterMm = 48f, PlenumLitres = 8f, ThrottleDiameterMm = 90f, AirboxLitres = 8f, SnorkelLengthMetres = 0.4f, SnorkelDiameterMm = 100f, Level = 0.7f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.7f, CombustionKnock = 0.05f },
    };

    /// <summary>A 6-litre 60-degree V12: two inline-sixes on one crank, 60-degree firing, each bank
    /// an even 120, separate systems per bank.</summary>
    public static EngineProfile V12 => new()
    {
        Name = "6.0 V12",
        Layout = EngineLayout.Vee,
        FiringAngles = EvenFire(new[] { 1, 7, 5, 11, 3, 9, 6, 12, 2, 8, 4, 10 }),
        Bank = HalfBanks(12),
        BoreMm = 89f, StrokeMm = 80.2f, RodRatio = 1.75f, CompressionRatio = 11f,
        ExhaustCam = new CamLobe { DurationDegrees = 262f, MaxLiftMm = 10.5f, RampFraction = 0.2f, CentrelineDegrees = 254f },
        IntakeCam = new CamLobe { DurationDegrees = 260f, MaxLiftMm = 11f, RampFraction = 0.2f, CentrelineDegrees = 472f },
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 30f, DischargeCoefficient = 0.68f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 35f, DischargeCoefficient = 0.68f },
        EvoTemperatureK = 1160f, IdleMapBar = 0.3f,
        IdleRoughness = 0.05f, IdleGovernorGain = 4f,
        IdleRpm = 750f, RedlineRpm = 7500f,
        InertiaKgM2 = 0.3f, FrictionNm = 45.6f, FrictionNmPerKrpm = 16.8f,
        PeakTorqueNm = 620f, PeakTorqueRpm = 5500f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.45f, PrimarySpread = 0.1f, PrimaryDiameterMm = 38f,
            CollectorDiameterMm = 63f, CollectorPipeMetres = 0.9f,
            Crossover = CrossoverKind.XPipe,
            MidPipeMetres = 1.2f,
            Muffler = MufflerSpec.Glasspack with { Absorption = 0.45f },
            TailpipeMetres = new[] { 0.5f, 0.54f },
            TailpipeDiameterMm = 63f,
            GasCelsiusIdle = 350f, GasCelsiusFull = 860f,
            WallLossMultiplier = 1.6f,
            OverrunPopRate = 5f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.3f, RunnerDiameterMm = 38f, PlenumLitres = 6f, ThrottleDiameterMm = 80f, AirboxLitres = 10f, SnorkelLengthMetres = 0.5f, Level = 0.5f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.5f, CombustionKnock = 0.03f },
    };

    /// <summary>
    /// A NASCAR Cup V8: 358 cubic inches, pushrod, two valves, a cross-plane crank and NO MUFFLER.
    ///
    /// The crank is the whole point, and it is what separates this from every other race engine. A
    /// 90-degree cross-plane crank fires each BANK at 90/180/180/270 degree intervals, so a bank's
    /// own pipe hears an uneven pattern that only repeats every two revolutions — which puts energy on
    /// the half orders, and the half orders are the rumble. An F1 V10 fires its banks evenly and has
    /// none of it. That is why a stock car at nine thousand rpm still sounds like a big American V8
    /// and a formula car at nine thousand sounds like a siren.
    ///
    /// Everything else is a race engine: a solid roller cam with enormous duration and lift, valves
    /// filling the bore, 12:1 compression, a tiny flywheel, and long equal-length 4-into-1 headers
    /// dumping out of the side of the car about a metre past the collector with nothing in the way.
    /// No muffler at all is worth 20-30 dB over a street car, and it is most of why a Cup car measures
    /// 130 dB in the grandstand.
    /// </summary>
    public static EngineProfile NascarV8 => new()
    {
        Name = "5.9 NASCAR V8, open side exits",
        Layout = EngineLayout.Vee,
        FiringAngles = EvenFire(new[] { 1, 8, 7, 3, 6, 5, 4, 2 }),
        Bank = AlternatingBanks(8),
        BoreMm = 106.3f, StrokeMm = 82.55f, RodRatio = 1.95f, CompressionRatio = 12f,
        // A solid roller with 280-plus degrees at fifty thou: well over 320 advertised, and lift
        // approaching an inch at the valve. The overlap that comes with it is why it cannot idle
        // below about 1200 and why it sounds ragged when it does.
        // Wide lobe centres: a restricted race engine spreads them to about 116 degrees, which keeps
        // the duration without the overlap of a drag cam. Overlap still lands near 80 — twice a
        // street car's, and audible as a ragged, diluted idle.
        ExhaustCam = new CamLobe { DurationDegrees = 316f, MaxLiftMm = 20f, RampFraction = 0.12f, CentrelineDegrees = 244f },
        IntakeCam = new CamLobe { DurationDegrees = 312f, MaxLiftMm = 21f, RampFraction = 0.12f, CentrelineDegrees = 478f },
        ExhaustValve = new ValveSpec { DiameterMm = 41.3f, DischargeCoefficient = 0.74f },
        IntakeValve = new ValveSpec { DiameterMm = 55.4f, DischargeCoefficient = 0.74f },
        EvoTemperatureK = 1280f, IdleMapBar = 0.58f,
        IdleRoughness = 0.5f, IdleGovernorGain = 3f,
        IdleRpm = 1300f, RedlineRpm = 9200f,
        // A Cup flywheel is thin, but the damper, clutch pack and crank are not: light for a road
        // car, and light enough to hear on a gearchange, without being so light that the idle cannot
        // be held between firings.
        InertiaKgM2 = 0.15f,
        FrictionNm = 44.5f, FrictionNmPerKrpm = 18f,
        // About 13.5 bar BMEP, which is where a restricted Cup engine actually lives.
        PeakTorqueNm = 620f, PeakTorqueRpm = 7600f,
        Exhaust = new ExhaustSpec
        {
            // Long equal-length primaries into one collector per bank, and then almost nothing: the
            // collector turns straight out through the side of the car behind the door.
            PrimaryLengthMetres = 1.02f, PrimarySpread = 0.04f, PrimaryDiameterMm = 47.6f,
            CollectorDiameterMm = 89f, CollectorPipeMetres = 0.35f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.25f,
            Muffler = MufflerSpec.StraightPipe,
            TailpipeMetres = new[] { 0.30f, 0.33f },
            TailpipeDiameterMm = 89f,
            GasCelsiusIdle = 420f, GasCelsiusFull = 980f,
            // Fabricated, mandrel-bent, two bends and no joints.
            WallLossMultiplier = 1.1f,
            // Nothing between the port and the air, so the pulses arrive at the end still steep
            // enough to shock — the hard crack a stock car has and a muffled one never does.
            Steepening = 1.3f,
            FlowLoss = 0.08f,
            JetNoiseLevel = 1.4f, PortNoiseLevel = 1.3f,
            OverrunPopRate = 14f,
        },
        // One big throttle body on a tall single-plane plenum, and a cowl the size of a suitcase.
        Intake = new IntakeSpec { RunnerLengthMetres = 0.20f, RunnerDiameterMm = 54f, PlenumLitres = 7f, ThrottleDiameterMm = 100f, AirboxLitres = 20f, SnorkelLengthMetres = 0.55f, SnorkelDiameterMm = 120f, Level = 1f, Absorption = 0.1f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 1.3f, CombustionKnock = 0.07f, AccessoryWhineOrder = 9.5f, AccessoryWhineLevel = 0.12f },
    };

    /// <summary>
    /// A three-litre Formula One V10: pneumatic valves, four valves a cylinder, individual trumpets
    /// in an airbox over the driver's head, and ten unsilenced pipes.
    ///
    /// It is the opposite engine to the stock car in every way that matters to the ear. The bore is
    /// two and a half times the stroke, so the piston can be asked to do three hundred revolutions a
    /// second. The banks fire EVENLY — 144 degrees apart down each pipe — so there is no half-order
    /// energy and no rumble at all: what comes out is the firing order itself, a pure fifth order,
    /// 1580 Hz at the limiter. That is the scream, and it is not a timbre choice, it is what an
    /// even-firing ten-cylinder does.
    ///
    /// It idles at 4000 rpm because it cannot idle lower: the cams are enormous, the flywheel is a
    /// carbon disc, and at anything less the overlap dilutes the charge past the point of burning.
    ///
    /// The real thing turned 19,000. This one is limited to 15,500, and the limit is the SYNTHESIS
    /// rather than the engine: at 44.1 kHz a firing every 28 samples is no longer enough to resolve
    /// the blowdown transient, and past about sixteen thousand the order structure measurably falls
    /// apart — half orders climb above whole ones on an engine that fires evenly and can have none,
    /// which is aliasing of the firing events and not a sound. Fifteen five still puts the firing
    /// frequency at 1290 Hz, which is the scream. Raising it needs the engine oversampled, and an
    /// oversampled engine costs four times the CPU of one that is already the most expensive voice
    /// in the mixer. Measured with `--engine-orders f1_v10 rpm=15000,17000 thr=1`.
    /// </summary>
    public static EngineProfile F1V10 => new()
    {
        Name = "3.0 V10, 15,500 rpm, open pipes",
        Layout = EngineLayout.Vee,
        FiringAngles = EvenFire(new[] { 1, 6, 5, 10, 2, 7, 3, 8, 4, 9 }),
        // Cylinders 1-5 down one bank and 6-10 down the other, which with this order gives each
        // bank an EVEN 144 degrees between firings. No half orders, no rumble — just order five.
        Bank = HalfBanks(10),
        // 98 mm bore on a 39.75 mm stroke: 300 cc a cylinder, and a mean piston speed at 19,000 rpm
        // that is only just past what a road engine sees at 8,000.
        BoreMm = 98f, StrokeMm = 39.75f, RodRatio = 2.6f, CompressionRatio = 13.5f,
        ExhaustCam = new CamLobe { DurationDegrees = 300f, MaxLiftMm = 13f, RampFraction = 0.1f, CentrelineDegrees = 252f },
        IntakeCam = new CamLobe { DurationDegrees = 296f, MaxLiftMm = 14f, RampFraction = 0.1f, CentrelineDegrees = 466f },
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 28f, DischargeCoefficient = 0.76f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 34f, DischargeCoefficient = 0.76f },
        EvoTemperatureK = 1320f, IdleMapBar = 0.55f,
        IdleRoughness = 0.35f, IdleGovernorGain = 3.5f,
        IdleRpm = 4000f, RedlineRpm = 15500f,
        InertiaKgM2 = 0.035f,
        FrictionNm = 22.8f, FrictionNmPerKrpm = 9f,
        // About 16 bar BMEP, which is where a naturally aspirated racing V10 lives.
        PeakTorqueNm = 375f, PeakTorqueRpm = 13500f,
        Exhaust = new ExhaustSpec
        {
            // Equal-length 5-into-1 per bank, tuned for the top of the range, then straight out the
            // back. There is nothing downstream of the collector but a few centimetres of pipe.
            PrimaryLengthMetres = 0.62f, PrimarySpread = 0.02f, PrimaryDiameterMm = 40f,
            CollectorDiameterMm = 72f, CollectorPipeMetres = 0.22f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.12f,
            Muffler = MufflerSpec.StraightPipe,
            TailpipeMetres = new[] { 0.18f, 0.20f },
            TailpipeDiameterMm = 72f,
            GasCelsiusIdle = 520f, GasCelsiusFull = 1020f,
            WallLossMultiplier = 1.0f,
            Steepening = 1.25f,
            FlowLoss = 0.06f,
            JetNoiseLevel = 1.5f, PortNoiseLevel = 1.4f,
            // It does not pop on the overrun the way a carburetted V8 does; the fuelling is cut.
            OverrunPopRate = 3f,
        },
        // Ten short trumpets standing in a big airbox: the runners are barely longer than the port,
        // which puts the intake's own resonance up where the engine actually runs.
        // Ten individual 46 mm throttles, which the model carries as the ONE throttle of the same
        // total area — 145 mm. Sized as a single 46 the engine strangles above 12,000 and the
        // manifold never reaches atmosphere at full throttle, which is audible as a V10 that will
        // not pull to the limiter.
        Intake = new IntakeSpec { RunnerLengthMetres = 0.11f, RunnerDiameterMm = 50f, PlenumLitres = 3f, ThrottleDiameterMm = 145f, AirboxLitres = 26f, SnorkelLengthMetres = 0.8f, SnorkelDiameterMm = 150f, Level = 1f, Absorption = 0.08f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.9f, CombustionKnock = 0.02f, AccessoryWhineOrder = 22f, AccessoryWhineLevel = 0.18f },
    };

    /// <summary>Every preset, by a short key a map or a command line can name.</summary>
    public static IReadOnlyDictionary<string, Func<EngineProfile>> Presets { get; } =
        new Dictionary<string, Func<EngineProfile>>(StringComparer.OrdinalIgnoreCase)
        {
            ["v8_muscle"] = () => V8MuscleBigBlock,
            ["v8_sports"] = () => V8SportsFlowmaster40,
            ["v8_flatplane"] = () => V8FlatPlane,
            ["i4_economy"] = () => Inline4Economy,
            ["i4_sport"] = () => Inline4Sport,
            ["i6"] = () => Inline6,
            ["v6"] = () => V6Sedan,
            ["vtwin"] = () => VTwin45,
            ["single"] = () => Single450,
            ["diesel_i4"] = () => DieselPickupI4,
            ["diesel_truck"] = () => DieselTruckI6,
            ["boxer4"] = () => Boxer4,
            ["v10"] = () => V10,
            ["v12"] = () => V12,
            ["nascar_v8"] = () => NascarV8,
            ["f1_v10"] = () => F1V10,
            ["police_v8"] = () => PoliceV8,
            ["v8_open_headers"] = () => V8OpenHeaders,
            ["v8_glasspack"] = () => V8BigBlockGlasspack,
            ["v8_mild"] = () => V8MildSmallBlock,
            ["v8_bigcam"] = () => V8BigCam,
            ["v8_blown"] = () => V8Blown,
            ["sportbike"] = () => SportBike,
        };

    public static EngineProfile ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No engine preset '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}
