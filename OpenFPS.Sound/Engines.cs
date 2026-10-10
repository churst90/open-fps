using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common.Editing;

namespace OpenFPS.Common;

// An engine, described as the machine it is, not as the sound it makes. Every number is a physical
// quantity with a unit, and the synthesis (EngineSynth, ExhaustNetwork, IntakeNetwork) integrates the
// gas through them, so changing a value changes the sound the way changing the part would. The only
// taste controls are the few scale factors standing in for physics the model does not carry
// (turbulence strength, mechanical noise level), and they say so.

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
    [Tunable("degrees", 90, 360, "How long the valve is open, seat to seat, in crank degrees. Longer holds the valve open into the next stroke: more top end, rougher idle.", Step = 2)]
    public required float DurationDegrees { get; init; }
    /// <summary>Peak valve lift, millimetres. Typical 9-14 mm.</summary>
    [Tunable("mm", 2, 50, "How far the valve opens at the top of the lobe. More lift lets more gas through.", Label = "peak lift", Step = 0.1)]
    public float MaxLiftMm { get; init; } = 12f;
    /// <summary>How much of the duration is spent in the slow opening and closing ramps rather than on
    /// the main lobe, 0..0.5. Aggressive roller cams have short ramps and reach lift fast.</summary>
    [Tunable("", 0, 0.5, "Share of the duration spent on the slow opening and closing ramps. Short ramps open the valve faster.", Step = 0.01)]
    public float RampFraction { get; init; } = 0.22f;
    /// <summary>Lobe centreline, crank degrees after the firing TDC: an exhaust lobe centred 110 degrees
    /// before the overlap TDC sits at 250, an intake lobe centred 106 after it at 466.</summary>
    [Tunable("degrees", 90, 540, "Where the lobe is centred, in crank degrees after the firing top dead centre. Moving the two lobes closer together adds overlap.", Label = "lobe centre", Step = 1)]
    public required float CentrelineDegrees { get; init; }

    /// <summary>Opening angle, crank degrees ATDC-firing.</summary>
    public float OpensDegrees => CentrelineDegrees - DurationDegrees * 0.5f;
    /// <summary>Closing angle, crank degrees ATDC-firing.</summary>
    public float ClosesDegrees => CentrelineDegrees + DurationDegrees * 0.5f;
}

/// <summary>The valves for one function (all exhaust valves, or all intake valves) on one cylinder.</summary>
public sealed record ValveSpec
{
    [Tunable("", 1, 6, "How many valves of this kind each cylinder has.", Label = "valves per cylinder")]
    public int Count { get; init; } = 1;
    [Tunable("mm", 10, 200, "Head diameter of each valve. A bigger valve flows more gas at the same lift.", Label = "diameter", Step = 0.5)]
    public required float DiameterMm { get; init; }
    /// <summary>Discharge coefficient of the port at full lift. Real heads run 0.55-0.75.</summary>
    [Tunable("", 0.4, 0.85, "How well the port flows at full lift, against a perfect hole of the same size. Real heads are 0.55 to 0.75.", Step = 0.01)]
    public float DischargeCoefficient { get; init; } = 0.62f;
}

/// <summary>A muffler, as the things inside the can.</summary>
public sealed record MufflerSpec
{
    [Tunable("", 0, 0, "What is inside the can: nothing, chambers, packing, or a stock baffled muffler.", Label = "muffler type")]
    public MufflerKind Kind { get; init; } = MufflerKind.Chambered;
    /// <summary>Chamber lengths, metres, in flow order. Each is an expansion of the pipe into the
    /// can's cross-section and back, so each cancels around c/2L and its multiples.</summary>
    public float[] ChamberLengthsMetres { get; init; } = { 0.10f, 0.14f, 0.18f };
    /// <summary>Can cross-section over pipe cross-section. 4-9 for a typical oval can on a 2.5 inch pipe.
    /// The larger it is, the deeper the chambers cancel.</summary>
    [Tunable("", 1, 20, "Cross-section of the can over that of the pipe. Larger makes each chamber cancel more deeply.", Step = 0.5)]
    public float ExpansionRatio { get; init; } = 6f;
    /// <summary>How much of each chamber's internal reflection is lost to baffles and deflectors, 0..1.
    /// Zero is a clean expansion chamber, which rings; a Flowmaster's deflectors are around 0.3.</summary>
    [Tunable("", 0, 1, "Share of each chamber's internal reflection lost to baffles and deflectors. Zero is a clean chamber that rings.", Step = 0.01)]
    public float BaffleLoss { get; init; } = 0.3f;
    /// <summary>Acoustic absorption of the packing, 0..1, applied to the top of the band on every pass.
    /// A fresh glasspack is 0.6-0.8, a blown-out one 0.2, a chambered muffler 0.</summary>
    [Tunable("", 0, 1, "Acoustic absorption of the packing, taken from the top of the band. A fresh glasspack is 0.6 to 0.8, a chambered muffler 0.", Label = "packing absorption", Step = 0.01)]
    public float Absorption { get; init; } = 0f;
    /// <summary>Length of the absorptive section, metres.</summary>
    [Tunable("m", 0, 1.5, "Length of the packed section. Longer takes more of the top off.", Label = "packed length", Step = 0.01)]
    public float AbsorptiveLengthMetres { get; init; } = 0.45f;
    /// <summary>A Helmholtz resonator tuned to a drone frequency, Hz. 0 for none.</summary>
    [Tunable("Hz", 0, 500, "Frequency of the side resonator that cancels a drone. Zero for none.", Label = "resonator tuning", Step = 5)]
    public float ResonatorHz { get; init; } = 0f;
    /// <summary>How sharply the resonator is tuned. Q of 4-8 is a real one.</summary>
    [Tunable("", 1, 20, "How narrowly the resonator is tuned. A real one is 4 to 8.", Label = "resonator sharpness", Step = 0.5)]
    public float ResonatorQ { get; init; } = 5f;

    /// <summary>
    /// The can itself, as metal that rings, or null. Everything above is what the muffler does to the
    /// gas; the case is a bare steel box driven by the pressure inside, most of what people mean by a
    /// metallic exhaust. Driven from inside, not by the tailpipe: a note the chambers cancel at the pipe
    /// can still ring loudly off the can.
    /// </summary>
    public VehicleBody? Shell { get; init; }

    /// <summary>How loud the case is: the fraction of the internal pressure that radiates at one metre,
    /// folding together the steel, the radiating area and the spreading (the calibration note in
    /// ExhaustNetwork).</summary>
    public float ShellLevel { get; init; } = 0f;

    public static MufflerSpec StraightPipe => new() { Kind = MufflerKind.None };

    /// <summary>A two-chamber 40-series style muffler: short, loud, and a bare ringing case.</summary>
    public static MufflerSpec Chambered40 => new()
    {
        Kind = MufflerKind.Chambered,
        ChamberLengthsMetres = new[] { 0.09f, 0.115f, 0.145f },
        ExpansionRatio = 5.5f,
        BaffleLoss = 0.32f,
        Shell = VehicleBody.MufflerCase,
        ShellLevel = ShellCalibration,
    };

    /// <summary>What one pascal inside the can becomes at one metre outside it, measured (the
    /// calibration note in ExhaustNetwork): one ratio measured rather than three factors guessed.</summary>
    public const float ShellCalibration = 0.035f;

    /// <summary>A packed straight-through can: deep, less rasp, and the packing damps the case too (its
    /// shell's loss factor is ten times higher).</summary>
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
    [Tunable("m", 0.05, 2.5, "Length of each primary pipe from the valve to the collector, when the lengths are not listed one by one.", Label = "primary length", Step = 0.01)]
    public float PrimaryLengthMetres { get; init; } = 0.80f;
    /// <summary>How unequal the primaries are, as a fraction of their length: a fabricated header
    /// 0.05-0.15, a cast log manifold 0.4 and up, coarser because eight pitches are a band.</summary>
    [Tunable("", 0, 0.8, "How unequal the primaries are, as a share of their length. A fabricated header is 0.05 to 0.15, a cast log manifold 0.4 and up.", Label = "primary length spread", Step = 0.01)]
    public float PrimarySpread { get; init; } = 0.12f;
    [Tunable("mm", 15, 150, "Inside diameter of each primary pipe.", Label = "primary diameter", Step = 0.5)]
    public float PrimaryDiameterMm { get; init; } = 44f;

    /// <summary>Which cylinders join which collector, as lists of cylinder indices. Null means one
    /// collector per bank. An inline-6 with two 3-into-1 headers is {{0,1,2},{3,4,5}}; a 4-2-1 header
    /// on an inline-4 is {{0,3},{1,2}}.</summary>
    public int[][]? CollectorGroups { get; init; }
    [Tunable("mm", 15, 250, "Inside diameter of the collector and the pipe after it.", Label = "collector diameter", Step = 1)]
    public float CollectorDiameterMm { get; init; } = 63f;
    /// <summary>From each collector to where the systems meet (or to the muffler if they never do), metres.</summary>
    [Tunable("m", 0, 3, "Pipe from each collector to where the systems meet, or to the muffler if they never do.", Label = "collector pipe length", Step = 0.01)]
    public float CollectorPipeMetres { get; init; } = 1.10f;

    [Tunable("", 0, 0, "How the two banks' pipes meet: not at all, by an H balance tube, by an X, or merged into one system.")]
    public CrossoverKind Crossover { get; init; } = CrossoverKind.HPipe;
    /// <summary>Length of the balance tube for an H-pipe, metres. Its own quarter-wave is audible.</summary>
    [Tunable("m", 0.05, 1.5, "Length of the H-pipe balance tube. Its own quarter wave is audible.", Label = "balance tube length", Step = 0.01)]
    public float CrossoverTubeMetres { get; init; } = 0.35f;
    /// <summary>Cross-section of the balance tube relative to the system pipe, 0..1.5.</summary>
    [Tunable("", 0, 1.5, "Cross-section of the balance tube over that of the system pipe. Bigger couples the banks more.", Label = "balance tube area", Step = 0.05)]
    public float CrossoverArea { get; init; } = 0.6f;

    /// <summary>From the crossover (or collector pipe) to the muffler inlet, metres.</summary>
    [Tunable("m", 0, 8, "Pipe from the crossover or collector pipe to the muffler inlet.", Label = "mid pipe length", Step = 0.05)]
    public float MidPipeMetres { get; init; } = 1.20f;
    public MufflerSpec Muffler { get; init; } = MufflerSpec.Chambered40;
    /// <summary>Tailpipe from the muffler to the open air, metres. Two branches get two lengths; if
    /// only one is given the second is 9% longer, because two equal tailpipes let the banks arrive in
    /// step and cancel each other's unevenness.</summary>
    public float[] TailpipeMetres { get; init; } = { 0.60f };
    [Tunable("mm", 15, 350, "Inside diameter of the tailpipe. A wider open end radiates the low notes better.", Label = "tailpipe diameter", Step = 1)]
    public float TailpipeDiameterMm { get; init; } = 63f;

    /// <summary>
    /// Where each tailpipe leaves the car, metres, in the machine's frame (x across, y up, z forward)
    /// from the exhaust part's position, one per branch; null puts every exit at one point. Summed at one
    /// point, an even-firing V10's banks are anti-phase at the bank firing rate and cancel the engine's
    /// fundamental: order 2.5 measured 12-19 dB under order 5 on the sum and level with it on one pipe,
    /// heard an octave up as a gliding siren. Set only where the geometry is proven to matter; a
    /// cross-plane V8 has no such symmetry (ExhaustNetwork.SetListener).
    /// </summary>
    public Vector3[]? TailpipeExitsMetres { get; init; }

    /// <summary>Exhaust gas temperature at the port, Celsius, idling and at full load. The speed of
    /// sound goes as the root of the absolute temperature, so the system speaks nearly half an octave
    /// higher working than idling.</summary>
    [Tunable("°C", 50, 700, "Exhaust gas temperature at the port when idling. Hotter gas raises every pipe resonance.", Label = "gas temperature at idle", Step = 10)]
    public float GasCelsiusIdle { get; init; } = 330f;
    [Tunable("°C", 300, 1100, "Exhaust gas temperature at the port at full load.", Label = "gas temperature at full load", Step = 10)]
    public float GasCelsiusFull { get; init; } = 820f;
    /// <summary>What fraction of the port's temperature rise survives to the tailpipe.</summary>
    [Tunable("", 0, 1, "Share of the port's temperature rise still left in the gas at the tailpipe.", Label = "heat kept to the tailpipe", Step = 0.05)]
    public float TailCooling { get; init; } = 0.45f;

    /// <summary>Multiplier on the viscothermal wall loss, to stand in for bends, joints, flex sections
    /// and rust that a straight smooth pipe does not have. 1 is a straight smooth pipe; 2-3 is a
    /// production system with four bends and two flanges.</summary>
    [Tunable("", 0.5, 4, "Wall loss against a straight smooth pipe, for bends, joints and flex sections. 1 is a straight smooth pipe; 2 to 3 is a production system with four bends and two flanges.", Label = "wall loss", Step = 0.05)]
    public float WallLossMultiplier { get; init; } = 1.8f;
    /// <summary>Scale on the finite-amplitude steepening of the wave fronts, 0..1; 1 is the physics (a
    /// half-bar pulse arrives sharper than it left). The rasp and crackle of an engine under load.</summary>
    public float Steepening { get; init; } = 1f;
    /// <summary>Resistive loss at junctions from the mean flow, as a fraction of the junction's
    /// admittance at full load. Stands in for vortex shedding at the collector and muffler inlet.</summary>
    public float FlowLoss { get; init; } = 0.12f;

    /// <summary>Jet noise of the exhaust leaving the tailpipe, a level scale standing in for nozzle
    /// detail; the exponent on exit velocity is physics, so it is nothing at idle.</summary>
    public float JetNoiseLevel { get; init; } = 1f;
    /// <summary>Turbulence at the valve seat during blowdown, sonic through a narrow curtain; a level
    /// scale.</summary>
    public float PortNoiseLevel { get; init; } = 1f;

    /// <summary>Chance per second of unburnt fuel lighting in the hot pipe on the overrun.</summary>
    [Tunable("per second", 0, 30, "How often unburnt fuel lights in the hot pipe with the throttle shut. Zero for an engine that cuts its fuel.", Label = "overrun pops", Step = 0.5)]
    public float OverrunPopRate { get; init; } = 6f;

    /// <summary>How many separate systems reach the air: one per collector group unless
    /// <see cref="Crossover"/> is Merged.</summary>
    public int TailpipeCount(int collectors)
        => Crossover == CrossoverKind.Merged ? 1 : collectors;
}

/// <summary>The intake tract: valve, runner, plenum, throttle, airbox, snorkel.</summary>
public sealed record IntakeSpec
{
    [Tunable("m", 0.02, 1, "Length of each intake runner from the plenum to the valve. It tunes where the engine breathes best.", Label = "runner length", Step = 0.01)]
    public float RunnerLengthMetres { get; init; } = 0.30f;
    [Tunable("mm", 10, 200, "Inside diameter of each intake runner.", Label = "runner diameter", Step = 1)]
    public float RunnerDiameterMm { get; init; } = 42f;
    [Tunable("litres", 0.05, 400, "Volume of the plenum the runners draw from.", Label = "plenum volume", Step = 0.1)]
    public float PlenumLitres { get; init; } = 4.5f;
    [Tunable("mm", 10, 400, "Bore of the throttle. Several throttles count as one of the same total area.", Label = "throttle diameter", Step = 1)]
    public float ThrottleDiameterMm { get; init; } = 80f;
    [Tunable("litres", 0.5, 1500, "Volume of the airbox. A big box on a small snorkel silences the intake.", Label = "airbox volume", Step = 0.5)]
    public float AirboxLitres { get; init; } = 8f;
    [Tunable("m", 0.02, 3, "Length of the snorkel from the open air to the airbox.", Label = "snorkel length", Step = 0.01)]
    public float SnorkelLengthMetres { get; init; } = 0.45f;
    [Tunable("mm", 10, 400, "Inside diameter of the snorkel.", Label = "snorkel diameter", Step = 1)]
    public float SnorkelDiameterMm { get; init; } = 70f;
    /// <summary>Acoustic absorption of the airbox lining and filter, 0..1.</summary>
    [Tunable("", 0, 1, "Acoustic absorption of the airbox lining and filter.", Label = "airbox absorption", Step = 0.01)]
    public float Absorption { get; init; } = 0.35f;
    /// <summary>How much of the intake noise reaches the outside of the car. An open filter under the
    /// bonnet is 1; a factory airbox with a resonator in the snorkel is 0.25.</summary>
    public float Level { get; init; } = 0.6f;

    /// <summary>
    /// What the airbox takes out on the way past, dB, from its geometry: an expansion chamber,
    /// TL = 10 log10[1 + (1/4)(m - 1/m)^2 sin^2(kL)] with m = A_box / A_snorkel, averaged over frequency
    /// (sin^2 -> 1/2), the box taken as a cube (A_box = V^(2/3)).
    ///
    /// Without it every car with a silenced exhaust radiated more from its airbox than its tailpipe
    /// (--voice-levels parts: economy four +8 dB, V6 +11, road police +10) where a real one is eight to
    /// fifteen below. A blanket correction would move the race engines too: a big box on a small
    /// snorkel silences (an economy four, 13 dB), a small box on a big one barely does (an open-element
    /// big-block, 4 dB).
    /// </summary>
    public float AirboxLossDb
    {
        get
        {
            float vBox = MathF.Max(1e-4f, AirboxLitres * 1e-3f);          // m^3
            float aBox = MathF.Pow(vBox, 2f / 3f);                        // a cube's face
            float dSnorkel = MathF.Max(5f, SnorkelDiameterMm) * 1e-3f;
            float aSnorkel = MathF.PI * dSnorkel * dSnorkel * 0.25f;
            float m = MathF.Max(1f, aBox / MathF.Max(1e-6f, aSnorkel));
            float d = m - 1f / m;
            return 10f * MathF.Log10(1f + 0.125f * d * d);
        }
    }

    /// <summary>
    /// Turbulence at the throttle plate, as a multiple of what the flow predicts; 0 for none. The
    /// counterpart of <see cref="ExhaustSpec.JetNoiseLevel"/>, and the only drive the airbox resonator
    /// gets at its own tens of hertz (the firing harmonics are far above it). Its peak follows gap
    /// velocity over gap size, so a nearly shut throttle hisses. Scaled by the pressure drop across the
    /// plate, so zero on a diesel, which has no plate: unscaled, a diesel's 300-460 Hz tract modes made
    /// an audible note of it.
    /// </summary>
    public float FlowNoiseLevel { get; init; } = 1f;
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
    [Tunable("", 0, 40, "Alternator whine as an engine order: pulley ratio times pole pairs. A 12-pole alternator on a 2.8 to 1 pulley is 16.8. Zero for none.", Label = "alternator whine order", Step = 0.1)]
    public float AccessoryWhineOrder { get; init; } = 16.8f;
    public float AccessoryWhineLevel { get; init; } = 0.15f;
    /// <summary>Supercharger rotor whine (Roots/twin-screw): order = lobes times drive ratio.</summary>
    [Tunable("", 0, 30, "Supercharger rotor whine as an engine order: lobes times drive ratio. Zero for no blower.", Label = "blower whine order", Step = 0.1)]
    public float BlowerWhineOrder { get; init; } = 0f;
    public float BlowerWhineLevel { get; init; } = 0f;
    /// <summary>Turbocharger: whistle level under boost and the lag of the shaft, seconds.</summary>
    public float TurboWhistleLevel { get; init; } = 0f;
    [Tunable("s", 0.05, 6, "How long the turbo shaft takes to spool up to boost.", Label = "turbo lag", Step = 0.05)]
    public float TurboLagSeconds { get; init; } = 0.8f;
    /// <summary>
    /// How fast the turbo's shaft turns at idle, as a fraction of its speed at full boost. A small turbo
    /// barely turns; a big one, or a compound pair, freewheels at a third and whistles standing still
    /// ("even at idle you could hear the whistle from the turbos"). Zero spools on the throttle alone.
    /// </summary>
    [Tunable("", 0, 0.8, "Turbo shaft speed at idle as a share of its speed at full boost. A big turbo freewheels at a third.", Label = "turbo speed at idle", Step = 0.05)]
    public float TurboIdleSpool { get; init; } = 0f;
}

/// <summary>
/// An engine, complete. Firing angles and bank assignment are in CYLINDER order, degrees of crank
/// after cylinder 0 fires, over a 720 degree cycle (360 for a two-stroke).
/// </summary>
public sealed record EngineProfile
{
    [Tunable("", 0, 0, "What this engine is called.", Label = "name")]
    public required string Name { get; init; }
    [Tunable("", 0, 0, "How the cylinders are arranged: in a line, in a vee, or flat.")]
    public EngineLayout Layout { get; init; } = EngineLayout.Vee;
    [Tunable("", 2, 4, "Strokes per cycle: 4 fires each cylinder every other turn, 2 every turn.", Label = "strokes per cycle", Step = 2)]
    public int Strokes { get; init; } = 4;
    [Tunable("", 0, 0, "Petrol is lit by a spark; diesel lights by compression and knocks.")]
    public FuelType Fuel { get; init; } = FuelType.Petrol;
    [Tunable("", 0, 0, "How the air gets in: drawn by the pistons, pushed by a turbo, or pushed by a supercharger.")]
    public Induction Induction { get; init; } = Induction.NaturallyAspirated;
    /// <summary>Peak boost, bar gauge, for a turbo or blower.</summary>
    [Tunable("bar", 0, 4, "Peak boost pressure above the atmosphere, for a turbo or a supercharger.", Label = "peak boost", Step = 0.05)]
    public float BoostBar { get; init; } = 0f;

    /// <summary>Crank angle at which each cylinder fires (its combustion TDC), degrees, cylinder order.</summary>
    public required float[] FiringAngles { get; init; }
    /// <summary>Which bank each cylinder is on, cylinder order. Inline engines are all bank 0.</summary>
    public required int[] Bank { get; init; }
    public int Cylinders => FiringAngles.Length;
    public float CycleDegrees => Strokes == 2 ? 360f : 720f;

    // ── Geometry ────────────────────────────────────────────────────────────────────────────────
    [Tunable("mm", 30, 250, "Cylinder diameter. A wider bore makes a bigger, lower knock and a bigger charge.", Label = "bore", Step = 0.5)]
    public required float BoreMm { get; init; }
    [Tunable("mm", 30, 300, "How far the piston travels. With the bore, this sets the displacement.", Label = "stroke", Step = 0.5)]
    public required float StrokeMm { get; init; }
    /// <summary>Connecting rod length over crank radius. 1.5-1.8 for road engines.</summary>
    [Tunable("", 1.3, 3, "Connecting rod length over crank radius. Road engines are 1.5 to 1.8.", Step = 0.01)]
    public float RodRatio { get; init; } = 1.7f;
    [Tunable("", 6, 23, "Cylinder volume at the bottom of the stroke over the volume at the top.", Step = 0.1)]
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
    /// Valve overlap in crank degrees, both valves open across TDC: the number behind a lopey idle, where
    /// exhaust pushed back into the cylinder dilutes the charge unevenly. Stock is 20-40, a street cam
    /// 50-70, a race cam 80 and up.
    /// </summary>
    public float OverlapDegrees => MathF.Max(0f, ExhaustCam.ClosesDegrees - IntakeCam.OpensDegrees);

    /// <summary>The lope, 0..1, from overlap, for reading a preset at a glance; the synthesis uses the
    /// overlap itself.</summary>
    public float CamLope => Math.Clamp((OverlapDegrees - 30f) / 60f, 0f, 1f);

    // ── Combustion ──────────────────────────────────────────────────────────────────────────────
    /// <summary>Gas temperature in the cylinder at exhaust valve opening, at full load, Kelvin.</summary>
    [Tunable("K", 800, 1600, "Gas temperature in the cylinder when the exhaust valve opens at full load.", Label = "gas temperature at exhaust opening", Step = 10)]
    public float EvoTemperatureK { get; init; } = 1150f;
    /// <summary>Manifold absolute pressure at idle, bar. A big cam idles at 0.5-0.6 because it cannot
    /// pull a vacuum; a stock engine idles at 0.3.</summary>
    [Tunable("bar", 0.15, 2, "Absolute manifold pressure at idle. A stock engine is 0.3; a big cam cannot pull a vacuum and sits at 0.5 to 0.6; a diesel has no throttle and sits near 1.", Label = "manifold pressure at idle", Step = 0.01)]
    public float IdleMapBar { get; init; } = 0.35f;
    /// <summary>Cycle-to-cycle combustion variation at full load under clean conditions, as a
    /// fraction (a healthy engine measures 2-4% COV of IMEP). Idle variation is derived from overlap.</summary>
    [Tunable("", 0, 0.15, "Cycle to cycle variation of combustion at full load, as a fraction. A healthy engine is 0.02 to 0.04.", Label = "cycle to cycle variation", Step = 0.005)]
    public float CombustionVariation { get; init; } = 0.03f;
    /// <summary>Extra idle roughness on top of what overlap predicts, 0..1. A carburetted engine
    /// with a lumpy cam and no idle control is up near 1; fuel injection with closed-loop idle is 0.</summary>
    public float IdleRoughness { get; init; } = 0.3f;

    // ── Rotating assembly and the way it is driven ──────────────────────────────────────────────
    [Tunable("rpm", 200, 5000, "The speed the engine settles at with the throttle shut.", Label = "idle speed", Step = 10)]
    public required float IdleRpm { get; init; }
    [Tunable("rpm", 500, 20000, "The highest speed the engine is run to.", Label = "redline", Step = 100)]
    public required float RedlineRpm { get; init; }
    /// <summary>The speed the starter turns it at, rpm: 150-250 for a diesel, 200-300 for a petrol
    /// engine (Pearson, Diesel Engine Starting Systems); six recorded starts beat at 150-225.</summary>
    [Tunable("rpm", 50, 1000, "How fast the starter turns the engine: 150 to 250 for a diesel, 200 to 300 for petrol.", Label = "cranking speed", Step = 10, Source = "Pearson, Diesel Engine Starting Systems; six recorded starts at 150 to 225")]
    public float CrankingRpm { get; init; } = 200f;
    /// <summary>
    /// Crank revolutions the starter turns before the first cylinder fires: the engine computer finds
    /// crank and cam (up to two revolutions) first, and a common-rail diesel raises its rail. Firing on
    /// the first compression, the synthesis caught in 50 ms, too fast to hear a start. NaN takes three
    /// for petrol and four for diesel, about 0.9 s for a car and two for a bus (a port-injected engine
    /// starts in 0.66-0.95 s, US5088465).
    /// </summary>
    public float RevolutionsBeforeFiring { get; init; } = float.NaN;
    public float FiringAfterRevolutions => float.IsNaN(RevolutionsBeforeFiring)
        ? (Fuel == FuelType.Diesel ? 4f : 3f) : RevolutionsBeforeFiring;
    /// <summary>Rotating inertia of crank, flywheel, clutch and damper, kg m^2: a heavy flywheel is
    /// 0.35-0.5, a race one 0.1.</summary>
    [Tunable("kg m²", 0.005, 250, "Rotating inertia of crank, flywheel, clutch and damper. Less makes the revs climb and fall faster.", Label = "inertia", Step = 0.01)]
    public float InertiaKgM2 { get; init; } = 0.30f;
    /// <summary>Mechanical friction torque, Nm, at rest and per 1000 rpm. About 0.95 bar of friction
    /// mean effective pressure at idle for a petrol engine (7.6 Nm per litre), 1.5 bar for a diesel,
    /// rising 0.35 bar per 1000 rpm. Pumping loss is not in here: the cylinders compute it.</summary>
    [Tunable("Nm", 0, 4000, "Mechanical friction torque at rest, not counting pumping.", Label = "friction torque", Step = 0.5)]
    public float FrictionNm { get; init; } = 20f;
    [Tunable("Nm per 1000 rpm", 0, 1500, "How much the friction torque rises for each thousand rpm.", Label = "friction rise", Step = 0.1)]
    public float FrictionNmPerKrpm { get; init; } = 9f;
    [Tunable("Nm", 1, 40000, "The most torque the engine makes.", Label = "peak torque", Step = 1)]
    public float PeakTorqueNm { get; init; } = 500f;
    [Tunable("rpm", 200, 18000, "The speed at which the engine makes its peak torque.", Label = "peak torque speed", Step = 50)]
    public float PeakTorqueRpm { get; init; } = 4200f;
    /// <summary>How the idle control fights the engine's own unevenness: the gain of the governor, 1/s.
    /// Electronic throttle idle control is quick (3-5); a carburettor's idle screw is zero.</summary>
    [Tunable("per second", 0, 20, "How hard the idle control corrects the speed. Electronic idle control is 3 to 5; a carburettor's idle screw is 0.", Label = "idle control gain", Step = 0.1)]
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

    /// <summary>The intervals between firings down one collector group's pipe, degrees.</summary>
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

    // ── Presets: each a real kind of engine ─────────────────────────────────────────────────────

    /// <summary>
    /// A 7-litre big-block V8 with a long cam, long-tube headers, true duals and chambered mufflers.
    /// Cross-plane crank, GM firing order 1-8-4-3-6-5-7-2: each bank fires at 90/180/180/270 degree
    /// intervals, and that unevenness down its own pipe is the American V8 burble.
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
            // Unequal enough to growl: the half-orders live in the difference between the primaries.
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

    // ── Four ways to exhaust the same V8 ────────────────────────────────────────────────────────
    //
    // Each changes the hardware and nothing else (the same cylinders, firing order and cam unless
    // stated), so the character is heard to come from the mechanism.

    /// <summary>
    /// Open headers: the primaries dump into the air at the collector, with no mid-pipe, crossover,
    /// muffler or tailpipe. Nothing cancels or absorbs, the overrun cracks, and there is no can to ring.
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
    /// The same big block through glasspacks, a packed straight-through can either side: the packing
    /// thins the harmonics from the top down instead of notching them, and damps the case's ring.
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
    /// A mild small block: a short cam, smaller valves and cast log manifolds (PrimarySpread 0.42 against
    /// a header's 0.12). It idles straight and runs out of breath early.
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
    /// A big block on a long cam through 40-series chambered cans. The 330-degree cam overlaps so much
    /// that a cylinder breathes its neighbour's exhaust and the idle hunts; above it, the chambers notch,
    /// the harmonics between survive, and the case rings.
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
    /// A litre sports bike: an inline four to 14,500 rpm, a firing rate of 483 Hz, so the fundamental is
    /// a pitch and the orders run into the kilohertz; sixteen valves each closing 120 times a second.
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
        // A stiff governor because the flywheel is tiny (0.055 kg m² against a big block's 0.42): the
        // same disturbance moves it eight times as far, and a lazy governor let the idle hunt past 2,500.
        IdleRoughness = 0.15f, IdleGovernorGain = 14f,
        IdleRpm = 1300f, RedlineRpm = 14500f,
        // At 11,000 rpm the 55 mm stroke moves the pistons at 20 m/s, where engines run 2-4 bar of
        // friction mean effective pressure: 3.2 bar here, about 26 Nm.
        InertiaKgM2 = 0.055f, FrictionNm = 6f, FrictionNmPerKrpm = 1.8f,
        PeakTorqueNm = 112f, PeakTorqueRpm = 11000f,
        Mechanical = new MechanicalSpec { ValvetrainLevel = 1.0f, CombustionKnock = 0.08f, AccessoryWhineLevel = 0.15f, AccessoryWhineOrder = 2.5f },
        Exhaust = new ExhaustSpec
        {
            // A short system, set by ear: the length is in the headers and link pipes, not the can.
            PrimaryLengthMetres = 0.30f, PrimarySpread = 0.10f, PrimaryDiameterMm = 34f,
            CollectorDiameterMm = 50f, CollectorPipeMetres = 0.12f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.04f,
            // A stock system: the pre-chamber under the engine (catalyst and two short expansions) and
            // a packed can. A quarter-packed glasspack was 119 dB at a metre flat out, twenty over a
            // stock litre bike (which passes at about 80 at 7.5 m).
            Muffler = MufflerSpec.Stock with { ChamberLengthsMetres = new[] { 0.10f, 0.14f }, ExpansionRatio = 7f },
            TailpipeMetres = new[] { 0.08f },
            TailpipeDiameterMm = 50f,
            // Short, thin, hot pipes and a hard blowdown keep the top end.
            WallLossMultiplier = 1.0f,
            Steepening = 1.6f,
            OverrunPopRate = 12f,
        },
    };


    /// <summary>
    /// A blown big block: 7.4 litres with a Roots supercharger on top. The rotors whine at a high order
    /// of engine speed, and the boost gives every cylinder a harder blowdown. Geared to the crank, it
    /// has no lag.
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

    /// <summary>
    /// The pace car's 7.0 V8: the big block with a race cam. Overlap is the lobe centres, not the
    /// duration: 320/316 degrees on 102 centres (against the big block's 306/300 on 108) gives about 116
    /// degrees of overlap to its 68, and the lope comes out of the valves. Open collectors, straight pipe.
    /// </summary>
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
        IdleRoughness = 1.0f, IdleGovernorGain = 1.2f,
        IdleRpm = 950f, RedlineRpm = 6800f,
        InertiaKgM2 = 0.38f, FrictionNm = 55f, FrictionNmPerKrpm = 20f,
        PeakTorqueNm = 810f, PeakTorqueRpm = 4200f,
        Exhaust = new ExhaustSpec
        {
            // Long tube headers into a short collector, no muffler.
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
            OverrunPopRate = 14f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.20f, RunnerDiameterMm = 54f, PlenumLitres = 7f, ThrottleDiameterMm = 105f, AirboxLitres = 6f, SnorkelLengthMetres = 0.25f, SnorkelDiameterMm = 110f, Level = 1.0f, Absorption = 0.1f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 1.1f, CombustionKnock = 0.08f, AccessoryWhineLevel = 0.1f },
    };

    /// <summary>
    /// The interceptor V8 as a road car has it: 5.0 litres, a stock cam, cast manifolds with short
    /// primaries, and a muffler where <see cref="PoliceV8"/> has straight pipe, which is most of the
    /// difference between them.
    /// </summary>
    public static EngineProfile PoliceInterceptorV8 => new()
    {
        Name = "5.0 interceptor V8, stock exhaust",
        Layout = EngineLayout.Vee,
        FiringAngles = EvenFire(new[] { 1, 5, 4, 8, 6, 3, 7, 2 }),
        Bank = AlternatingBanks(8),
        BoreMm = 92.2f, StrokeMm = 92.7f, RodRatio = 1.66f, CompressionRatio = 12.0f,
        ExhaustCam = new CamLobe { DurationDegrees = 274f, MaxLiftMm = 12.0f, RampFraction = 0.22f, CentrelineDegrees = 250f },
        IntakeCam = new CamLobe { DurationDegrees = 270f, MaxLiftMm = 12.2f, RampFraction = 0.22f, CentrelineDegrees = 470f },
        ExhaustValve = new ValveSpec { DiameterMm = 33f, DischargeCoefficient = 0.66f },
        IntakeValve = new ValveSpec { DiameterMm = 37f, DischargeCoefficient = 0.68f },
        EvoTemperatureK = 1180f, IdleMapBar = 0.32f,
        IdleRoughness = 0.12f, IdleGovernorGain = 0.8f,
        IdleRpm = 680f, RedlineRpm = 6500f,
        InertiaKgM2 = 0.30f, FrictionNm = 42f, FrictionNmPerKrpm = 16f,
        PeakTorqueNm = 530f, PeakTorqueRpm = 4250f,
        Exhaust = new ExhaustSpec
        {
            // Cast manifolds: short and fat.
            PrimaryLengthMetres = 0.34f, PrimarySpread = 0.05f, PrimaryDiameterMm = 42f,
            CollectorDiameterMm = 63f, CollectorPipeMetres = 0.55f,
            Crossover = CrossoverKind.HPipe,
            MidPipeMetres = 1.6f,
            // A pursuit exhaust, a straight-through can in place of the baffled one: 99.6 dB flat out
            // on the live voice, seven over a fully stock saloon (93), fourteen under a mild muscle car.
            Muffler = MufflerSpec.Stock with { BaffleLoss = 0.04f, Absorption = 0.08f },
            Steepening = 0.9f,
            TailpipeMetres = new[] { 0.60f, 0.60f },
            TailpipeDiameterMm = 57f,
            GasCelsiusIdle = 330f, GasCelsiusFull = 820f,
            WallLossMultiplier = 1.0f,
            OverrunPopRate = 0.5f,
        },
        // Level 0.12, a sealed airbox with a Helmholtz resonator in a long snorkel: at 0.55 the intake
        // measured 109 dB against an 86 dB exhaust and this saloon came out louder than a muscle car.
        Intake = new IntakeSpec { RunnerLengthMetres = 0.26f, RunnerDiameterMm = 44f, PlenumLitres = 5.5f, ThrottleDiameterMm = 80f, AirboxLitres = 12f, SnorkelLengthMetres = 0.35f, SnorkelDiameterMm = 85f, Level = 0.12f, Absorption = 0.65f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.45f, CombustionKnock = 0.04f, AccessoryWhineLevel = 0.12f },
    };

    /// <summary>
    /// A 1969 Charger's 7.2-litre V8 in street spec: a 284-degree hydraulic cam (a little lope), cast
    /// manifolds, an H-pipe, and chambered cans on 2.5 inch pipe worth well over ten decibels against
    /// open headers.
    /// </summary>
    public static EngineProfile V8Charger440 => new()
    {
        Name = "7.2 big-block V8, H-pipe, chambered 40s",
        Layout = EngineLayout.Vee,
        // Chrysler B/RB firing order 1-8-4-3-6-5-7-2.
        FiringAngles = EvenFire(new[] { 1, 8, 4, 3, 6, 5, 7, 2 }),
        Bank = AlternatingBanks(8),
        BoreMm = 109.7f, StrokeMm = 95.2f, RodRatio = 1.70f, CompressionRatio = 10.1f,
        ExhaustCam = new CamLobe { DurationDegrees = 284f, MaxLiftMm = 11.9f, RampFraction = 0.20f, CentrelineDegrees = 254f },
        IntakeCam = new CamLobe { DurationDegrees = 280f, MaxLiftMm = 11.9f, RampFraction = 0.20f, CentrelineDegrees = 468f },
        ExhaustValve = new ValveSpec { DiameterMm = 44.5f, DischargeCoefficient = 0.67f },
        IntakeValve = new ValveSpec { DiameterMm = 54f, DischargeCoefficient = 0.69f },
        EvoTemperatureK = 1210f, IdleMapBar = 0.38f,
        IdleRoughness = 0.42f, IdleGovernorGain = 0.9f,
        IdleRpm = 750f, RedlineRpm = 5600f,
        InertiaKgM2 = 0.42f, FrictionNm = 52f, FrictionNmPerKrpm = 18f,
        PeakTorqueNm = 664f, PeakTorqueRpm = 3200f,
        Exhaust = new ExhaustSpec
        {
            // Cast manifolds, short and fat: part of why a stock muscle car rumbles where a race car barks.
            PrimaryLengthMetres = 0.38f, PrimarySpread = 0.05f, PrimaryDiameterMm = 45f,
            CollectorDiameterMm = 64f, CollectorPipeMetres = 0.5f,
            // The H lets the banks share pulses: the cross-plane burble, not two inline fours.
            Crossover = CrossoverKind.HPipe,
            MidPipeMetres = 1.2f,
            Muffler = MufflerSpec.Chambered40,
            Steepening = 1.05f,
            TailpipeMetres = new[] { 0.85f, 0.90f },
            TailpipeDiameterMm = 63.5f,
            GasCelsiusIdle = 360f, GasCelsiusFull = 880f,
            WallLossMultiplier = 1.05f,
            OverrunPopRate = 3.5f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.22f, RunnerDiameterMm = 50f, PlenumLitres = 6f, ThrottleDiameterMm = 92f, AirboxLitres = 5f, SnorkelLengthMetres = 0.20f, SnorkelDiameterMm = 95f, Level = 0.30f, Absorption = 0.35f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.7f, CombustionKnock = 0.05f, AccessoryWhineLevel = 0.12f },
    };

    /// <summary>
    /// <see cref="VTwin45"/> with the factory's baffled cans; the 315/405 beat is the crank and survives
    /// any exhaust. A motorcycle must pass 80 dB(A) at fifty feet to be sold, about 104 at a metre.
    /// </summary>
    public static EngineProfile VTwin45Stock => VTwin45 with
    {
        Name = "1.75 V-twin, 45 degrees, stock mufflers",
        Exhaust = VTwin45.Exhaust with
        {
            Muffler = MufflerSpec.Stock,
            TailpipeMetres = new[] { 0.24f, 0.32f },
            TailpipeDiameterMm = 44f,
            OverrunPopRate = 1.0f,
        },
        Intake = VTwin45.Intake with { Level = 0.35f, Absorption = 0.5f },
    };

    /// <summary>The V-twin on stock head pipes with aftermarket slip-on cans: packed, straight-through,
    /// between stock and open pipe.</summary>
    public static EngineProfile VTwin45SlipOn => VTwin45 with
    {
        Name = "1.75 V-twin, 45 degrees, slip-on cans",
        Exhaust = VTwin45.Exhaust with
        {
            // A short dense pack, 350 mm against a car glasspack's 500: at Glasspack's 0.62 it came
            // out within four decibels of open pipe, where a slip-on is halfway to stock.
            Muffler = MufflerSpec.Glasspack with { Absorption = 0.86f, AbsorptiveLengthMetres = 0.35f },
            TailpipeMetres = new[] { 0.22f, 0.30f },
            TailpipeDiameterMm = 48f,
            OverrunPopRate = 6f,
        },
        Intake = VTwin45.Intake with { Level = 0.5f },
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
    /// The same 2.0 four with a turbo, and what follows from it: compression down to 9.4 from 11.5, less
    /// overlap (no reversion into a pressurised charge, so a cleaner idle), and a turbine that turns the
    /// sharp pulses into shaft work, so it sounds flat and woofly and the interest moves to the intake.
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
        // Torque arrives early and stays: the shape of a boosted engine.
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

    /// <summary>A 3.5-litre 60-degree V6 with a stock system: even 120-degree firing, 1-2-3-4-5-6 on
    /// alternating banks, so each bank fires evenly every 240.</summary>
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
            // A short system, like the sports bike's: the rear pipe about a metre end to end.
            PrimaryLengthsMetres = new[] { 0.38f, 0.52f }, PrimaryDiameterMm = 45f,
            CollectorGroups = new[] { new[] { 0 }, new[] { 1 } },
            CollectorDiameterMm = 45f, CollectorPipeMetres = 0.10f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.05f,
            Muffler = MufflerSpec.StraightPipe,
            TailpipeMetres = new[] { 0.25f, 0.35f },
            TailpipeDiameterMm = 50f,
            GasCelsiusIdle = 330f, GasCelsiusFull = 800f,

            // A slow twin fires 50 times a second at 3,000 rpm to a V8's 200, so a kilohertz is its
            // twentieth harmonic and any per-harmonic rolloff hits it four times as hard: without these
            // two it measured 76.5 % below 200 Hz and half a per cent at 800 Hz-2.5 kHz. The pipes are
            // short, smooth and hot (little wall loss), and the biggest single-cylinder charge in the
            // catalogue steepens as it travels: the bark.
            WallLossMultiplier = 0.85f,
            Steepening = 2.0f,
            OverrunPopRate = 16f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.12f, RunnerDiameterMm = 45f, PlenumLitres = 0.6f, ThrottleDiameterMm = 50f, AirboxLitres = 2f, SnorkelLengthMetres = 0.15f, SnorkelDiameterMm = 60f, Level = 1f, Absorption = 0.1f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 1.2f, CombustionKnock = 0.08f, AccessoryWhineLevel = 0.05f },
    };

    /// <summary>A 450 cc single: one bang every 720 degrees, a short pipe and a small can, the crank
    /// speed rippling hard between firings.</summary>
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
        IdleRpm = 1900f, RedlineRpm = 11000f,
        InertiaKgM2 = 0.02f, FrictionNm = 3.4f, FrictionNmPerKrpm = 1.3f,
        PeakTorqueNm = 48f, PeakTorqueRpm = 7000f,
        Exhaust = new ExhaustSpec
        {
            // About half a metre end to end (at a metre or more it sounded like a straw): header 0.22,
            // collector 0.06, mid 0.02, silencer 0.15, tail 0.05.
            PrimaryLengthMetres = 0.22f, PrimaryDiameterMm = 42f,
            CollectorGroups = new[] { new[] { 0 } },
            CollectorDiameterMm = 42f, CollectorPipeMetres = 0.06f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.02f,
            Muffler = MufflerSpec.Glasspack with { Absorption = 0.45f, AbsorptiveLengthMetres = 0.15f },
            TailpipeMetres = new[] { 0.05f },
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
    /// A NASCAR Cup V8: 358 cubic inches, pushrod, two valves, no muffler. The cross-plane crank fires
    /// each bank at 90/180/180/270, a pattern repeating every two revolutions that puts energy on the
    /// half orders, the rumble an evenly firing F1 V10 lacks. Long equal 4-into-1 headers dump out of the
    /// side; no muffler is worth 20-30 dB over a street car, most of a Cup car's 130 dB.
    /// </summary>
    public static EngineProfile NascarV8 => new()
    {
        Name = "5.9 NASCAR V8, open side exits",
        Layout = EngineLayout.Vee,
        FiringAngles = EvenFire(new[] { 1, 8, 7, 3, 6, 5, 4, 2 }),
        Bank = AlternatingBanks(8),
        BoreMm = 106.3f, StrokeMm = 82.55f, RodRatio = 1.95f, CompressionRatio = 12f,
        // A solid roller, 280-plus degrees at fifty thou, lift near an inch, on wide (about 116 degree)
        // lobe centres: overlap still near 80, twice a street car's, so it cannot idle below about 1200
        // and is ragged when it does.
        ExhaustCam = new CamLobe { DurationDegrees = 316f, MaxLiftMm = 20f, RampFraction = 0.12f, CentrelineDegrees = 244f },
        IntakeCam = new CamLobe { DurationDegrees = 312f, MaxLiftMm = 21f, RampFraction = 0.12f, CentrelineDegrees = 478f },
        ExhaustValve = new ValveSpec { DiameterMm = 41.3f, DischargeCoefficient = 0.74f },
        IntakeValve = new ValveSpec { DiameterMm = 55.4f, DischargeCoefficient = 0.74f },
        EvoTemperatureK = 1280f, IdleMapBar = 0.58f,
        IdleRoughness = 0.5f, IdleGovernorGain = 3f,
        IdleRpm = 1300f, RedlineRpm = 9200f,
        // A thin flywheel, but the damper, clutch pack and crank are not: light enough to hear on a
        // gearchange, heavy enough to hold the idle between firings.
        InertiaKgM2 = 0.15f,
        FrictionNm = 44.5f, FrictionNmPerKrpm = 18f,
        // About 13.5 bar BMEP, which is where a restricted Cup engine actually lives.
        PeakTorqueNm = 620f, PeakTorqueRpm = 7600f,
        Exhaust = new ExhaustSpec
        {
            // Long equal primaries into one collector per bank, which turns out through the side.
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
            // Nothing between port and air, so the pulses arrive steep enough to shock: the crack.
            Steepening = 1.3f,
            FlowLoss = 0.08f,
            JetNoiseLevel = 1.4f, PortNoiseLevel = 1.3f,
            OverrunPopRate = 14f,
        },
        // One big throttle body on a tall single-plane plenum, and a suitcase-sized cowl.
        Intake = new IntakeSpec { RunnerLengthMetres = 0.20f, RunnerDiameterMm = 54f, PlenumLitres = 7f, ThrottleDiameterMm = 100f, AirboxLitres = 20f, SnorkelLengthMetres = 0.55f, SnorkelDiameterMm = 120f, Level = 1f, Absorption = 0.1f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 1.3f, CombustionKnock = 0.07f, AccessoryWhineOrder = 9.5f, AccessoryWhineLevel = 0.12f },
    };

    /// <summary>
    /// A three-litre Formula One V10: pneumatic valves, four a cylinder, trumpets in an airbox over the
    /// driver's head, ten unsilenced pipes. Bore two and a half times the stroke; the banks fire evenly,
    /// 144 degrees apart, so no half orders and no rumble, only the fifth order: the scream. It idles at
    /// 4000 rpm because the overlap dilutes the charge past burning below that.
    ///
    /// The real one turned 19,000 (BMW's 2005 V10, the same 98 mm bore, quoted 350 Nm). This one stops
    /// at 15,500 because the synthesis does, measured with --engine-alias:
    ///
    ///     held at 19,000 rpm     samples/firing   half/whole   structure
    ///       44,100 Hz                      27.7      +1.1 dB     13.4 dB
    ///       88,200 Hz                      55.3      -5.7 dB     13.6 dB
    ///
    /// An even ten has no half-order energy, so half orders over whole ones is the integrator aliasing
    /// the firing events; two times oversampling fixes it and four gains nothing. At 15,500 the same
    /// reads -5.8 dB and structure 52.5, a clean engine, firing at 1,304 Hz.
    /// </summary>
    public static EngineProfile F1V10 => new()
    {
        Name = "3.0 V10, 15,500 rpm, open pipes",
        Layout = EngineLayout.Vee,
        FiringAngles = EvenFire(new[] { 1, 6, 5, 10, 2, 7, 3, 8, 4, 9 }),
        // 1-5 down one bank and 6-10 the other: an even 144 degrees each. Each pipe's order 2.5 is
        // anti-phase with the other's, so the exits are placed (TailpipeExitsMetres) or it is a siren.
        Bank = HalfBanks(10),
        // 300 cc a cylinder: mean piston speed at 19,000 rpm just past a road engine's at 8,000.
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
        // BMW's quoted 350 Nm: 14.7 bar BMEP, where a naturally aspirated racing V10 lives.
        PeakTorqueNm = 350f, PeakTorqueRpm = 13500f,
        Exhaust = new ExhaustSpec
        {
            // Equal-length 5-into-1 per bank, then a few centimetres of pipe.
            PrimaryLengthMetres = 0.62f, PrimarySpread = 0.02f, PrimaryDiameterMm = 40f,
            CollectorDiameterMm = 72f, CollectorPipeMetres = 0.22f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.12f,
            Muffler = MufflerSpec.StraightPipe,
            TailpipeMetres = new[] { 0.18f, 0.20f },
            TailpipeDiameterMm = 72f,
            // One exit each side of the gearbox, about sixty centimetres apart.
            TailpipeExitsMetres = new[] { new Vector3(-0.30f, 0f, 0f), new Vector3(0.30f, 0f, 0f) },
            GasCelsiusIdle = 520f, GasCelsiusFull = 1020f,
            WallLossMultiplier = 1.0f,
            Steepening = 1.25f,
            FlowLoss = 0.06f,
            JetNoiseLevel = 1.5f, PortNoiseLevel = 1.4f,
            OverrunPopRate = 3f,   // the fuelling is cut on the overrun
        },
        // Ten short trumpets in a big airbox, the intake's resonance up where the engine runs. Ten 46 mm
        // throttles carried as one of the same area, 145 mm: as a single 46 it strangled above 12,000.
        // The exhaust has nothing below 200 Hz (ninety per cent in 0.8-2.5 kHz; it fires 1,292 times a
        // second at 15,500). The 26-litre box on a 0.8 m snorkel is the body: --intake-ir f1_v10
        // measures modes at 45.4, 206, 393, 530 and 631 Hz (48 predicted), 14.9 % of a thump below
        // 200 Hz, driven by the throttle plate (IntakeSpec.FlowNoiseLevel).
        Intake = new IntakeSpec { RunnerLengthMetres = 0.11f, RunnerDiameterMm = 50f, PlenumLitres = 3f, ThrottleDiameterMm = 145f, AirboxLitres = 26f, SnorkelLengthMetres = 0.8f, SnorkelDiameterMm = 150f, Level = 1f, Absorption = 0.08f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.9f, CombustionKnock = 0.02f, AccessoryWhineOrder = 22f, AccessoryWhineLevel = 0.18f },
    };

    /// <summary>
    /// The 5.9 Cummins 6BT out of a Dodge Ram, straight-piped: 102 x 120 mm, 5.88 litres, 17.0:1, two
    /// valves a cylinder (the twelve-valve), 1-5-3-6-2-4, 460 lb-ft (624 Nm) at 1,600, governed under
    /// 3,000. A log manifold, the turbo, then five inches of pipe whose half-wavelength is 27 Hz. Its
    /// 102 mm bore knocks near 5.2 kHz where the bus's 116 mm does at 4.5, from the bore alone. The
    /// turbine stage every <see cref="Induction.Turbocharged"/> engine gets eats the pulses: rush, not
    /// beats.
    /// </summary>
    public static EngineProfile DieselCumminsI6 => new()
    {
        Name = "5.9 Cummins 12v, straight pipe",
        Layout = EngineLayout.Inline,
        Fuel = FuelType.Diesel, Induction = Induction.Turbocharged, BoostBar = 1.5f,
        FiringAngles = EvenFire(new[] { 1, 5, 3, 6, 2, 4 }),
        Bank = OneBank(6),
        BoreMm = 102f, StrokeMm = 120f, RodRatio = 1.7f, CompressionRatio = 17f,
        ExhaustCam = new CamLobe { DurationDegrees = 236f, MaxLiftMm = 11f, RampFraction = 0.26f, CentrelineDegrees = 256f },
        IntakeCam = new CamLobe { DurationDegrees = 232f, MaxLiftMm = 11f, RampFraction = 0.26f, CentrelineDegrees = 478f },
        ExhaustValve = new ValveSpec { Count = 1, DiameterMm = 40f, DischargeCoefficient = 0.6f },
        IntakeValve = new ValveSpec { Count = 1, DiameterMm = 44f, DischargeCoefficient = 0.6f },
        EvoTemperatureK = 1020f, IdleMapBar = 1.0f,
        CombustionVariation = 0.025f, IdleRoughness = 0.2f, IdleGovernorGain = 5f,
        IdleRpm = 750f, RedlineRpm = 2900f,
        InertiaKgM2 = 0.9f, FrictionNm = 70f, FrictionNmPerKrpm = 26f,
        PeakTorqueNm = 624f, PeakTorqueRpm = 1600f,
        Exhaust = new ExhaustSpec
        {
            // A cast log: short, fat, all six into one.
            PrimaryLengthMetres = 0.18f, PrimarySpread = 0.55f, PrimaryDiameterMm = 48f,
            CollectorGroups = new[] { new[] { 0, 1, 2, 3, 4, 5 } },
            CollectorDiameterMm = 90f, CollectorPipeMetres = 0.35f,
            Crossover = CrossoverKind.Merged,
            MidPipeMetres = 2.4f,
            Muffler = MufflerSpec.StraightPipe,
            TailpipeMetres = new[] { 1.1f },
            TailpipeDiameterMm = 127f,
            GasCelsiusIdle = 150f, GasCelsiusFull = 620f,
            WallLossMultiplier = 1.2f,
            OverrunPopRate = 0f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.2f, RunnerDiameterMm = 45f, PlenumLitres = 6f, ThrottleDiameterMm = 76f, AirboxLitres = 18f, SnorkelLengthMetres = 0.9f, SnorkelDiameterMm = 90f, Level = 0.45f, Absorption = 0.4f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.75f, CombustionKnock = 1.5f, AccessoryWhineLevel = 0.15f, TurboWhistleLevel = 1.0f, TurboLagSeconds = 1.0f },
    };

    /// <summary>
    /// The 7.3 Power Stroke (Navistar T444E) of the late-1990s Ford Super Duty: a 90-degree V8 diesel,
    /// 104.4 x 106.2 mm, 17.5:1, one Garrett turbo fed by both banks' up-pipes, HEUI injectors, about
    /// 500 lb-ft (680 Nm) at 1,600 and governed near 3,300. Stock exhaust: a muffler and a four-inch
    /// pipe. Known by the turbo's whistle, there at idle, and the clatter.
    /// </summary>
    public static EngineProfile PowerStroke73 => DieselCumminsI6 with
    {
        Name = "7.3 Power Stroke V8, stock exhaust",
        Layout = EngineLayout.Vee,
        FiringAngles = EvenFire(new[] { 1, 2, 7, 3, 4, 5, 6, 8 }),
        Bank = AlternatingBanks(8),
        BoreMm = 104.4f, StrokeMm = 106.2f, CompressionRatio = 17.5f,
        BoostBar = 1.2f,
        IdleRpm = 680f, RedlineRpm = 3300f,
        InertiaKgM2 = 1.0f, FrictionNm = 85f, FrictionNmPerKrpm = 28f,
        PeakTorqueNm = 680f, PeakTorqueRpm = 1600f,
        Exhaust = DieselCumminsI6.Exhaust with
        {
            CollectorGroups = new[] { new[] { 0, 1, 2, 3, 4, 5, 6, 7 } },
            CollectorDiameterMm = 95f,
            Muffler = MufflerSpec.Stock,
            TailpipeDiameterMm = 102f,
        },
        Mechanical = DieselCumminsI6.Mechanical with
        {
            CombustionKnock = 1.6f, TurboWhistleLevel = 1.4f, TurboLagSeconds = 1.1f, TurboIdleSpool = 0.25f,
        },
    };

    /// <summary>
    /// A 6.6 Duramax (LB7) with compound turbos and a five-inch straight pipe: 103 x 99 mm, four
    /// valves a cylinder, firing 1-2-7-8-4-5-6-3, and the pair of turbos — a big atmospheric one
    /// feeding the stock one — at about 3.2 bar. Built to around 900 lb-ft. Two big turbines
    /// freewheel at idle, so it whistles standing still, and on the throttle they scream.
    /// </summary>
    public static EngineProfile DuramaxCompound => PowerStroke73 with
    {
        Name = "6.6 Duramax, compound turbos, straight pipe",
        FiringAngles = EvenFire(new[] { 1, 2, 7, 8, 4, 5, 6, 3 }),
        BoreMm = 103f, StrokeMm = 99f,
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 30f, DischargeCoefficient = 0.6f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 34f, DischargeCoefficient = 0.6f },
        BoostBar = 3.2f,
        IdleRpm = 680f, RedlineRpm = 3400f,
        PeakTorqueNm = 1220f, PeakTorqueRpm = 1900f,
        // A five-inch straight pipe, less lost to its walls: +3 dB over a four-inch one.
        Exhaust = PowerStroke73.Exhaust with
        {
            Muffler = MufflerSpec.StraightPipe, TailpipeDiameterMm = 127f, Steepening = 1.5f, WallLossMultiplier = 0.8f,
        },
        Mechanical = PowerStroke73.Mechanical with
        {
            CombustionKnock = 1.3f, TurboWhistleLevel = 2.4f, TurboLagSeconds = 0.8f, TurboIdleSpool = 0.35f,
        },
    };

    /// <summary>
    /// The 5.9 Cummins with compound turbos and a five-inch straight pipe: the same engine as
    /// <see cref="DieselCumminsI6"/>, with a big turbo feeding the HX35 at 3.5 bar and built to about
    /// 950 lb-ft. Whistling at idle, screaming on the throttle.
    /// </summary>
    public static EngineProfile CumminsCompound => DieselCumminsI6 with
    {
        Name = "5.9 Cummins, compound turbos, straight pipe",
        BoostBar = 3.5f,
        RedlineRpm = 3200f,
        PeakTorqueNm = 1290f, PeakTorqueRpm = 2000f,
        // A six-inch stack and less lost to its walls, as the Duramax's.
        Exhaust = DieselCumminsI6.Exhaust with { TailpipeDiameterMm = 152f, Steepening = 1.5f, WallLossMultiplier = 0.8f },
        Mechanical = DieselCumminsI6.Mechanical with
        {
            TurboWhistleLevel = 2.6f, TurboLagSeconds = 0.8f, TurboIdleSpool = 0.35f,
        },
    };

    /// <summary>The 5.9 Cummins ISB (24 valves) in a parcel step van: a muffler, a long pipe to the
    /// back, a modest turbo.</summary>
    public static EngineProfile CumminsIsbStepVan => DieselCumminsI6 with
    {
        Name = "5.9 Cummins ISB, step van",
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 30f, DischargeCoefficient = 0.6f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 33f, DischargeCoefficient = 0.6f },
        BoostBar = 1.8f,
        IdleRpm = 700f, RedlineRpm = 2600f,
        PeakTorqueNm = 800f, PeakTorqueRpm = 1600f,
        Exhaust = DieselCumminsI6.Exhaust with { Muffler = MufflerSpec.Stock, MidPipeMetres = 3.2f, TailpipeDiameterMm = 102f },
        Mechanical = DieselCumminsI6.Mechanical with { TurboWhistleLevel = 0.5f, CombustionKnock = 1.2f },
    };

    /// <summary>
    /// GM's 2.5 "Iron Duke" four, as in the Grumman LLV mail truck: 101.6 x 76.2 mm, pushrods, two
    /// valves a cylinder, about 180 Nm, and a three-speed automatic. A coarse, busy little engine.
    /// </summary>
    public static EngineProfile IronDuke25 => Inline4Economy with
    {
        Name = "2.5 Iron Duke four, mail truck",
        BoreMm = 101.6f, StrokeMm = 76.2f, CompressionRatio = 8.3f,
        ExhaustValve = new ValveSpec { Count = 1, DiameterMm = 38f },
        IntakeValve = new ValveSpec { Count = 1, DiameterMm = 44f },
        IdleRpm = 700f, RedlineRpm = 5000f,
        PeakTorqueNm = 180f, PeakTorqueRpm = 3200f,
        InertiaKgM2 = 0.22f,
        Mechanical = Inline4Economy.Mechanical with { ValvetrainLevel = 0.6f, AccessoryWhineLevel = 0.25f },
    };

    /// <summary>A 5.3 small-block V8 as a full-size pickup has it: the road interceptor's stock
    /// manifolds, catalyst and silencer, on a truck's bore and stroke (96 x 92 mm) and torque.</summary>
    public static EngineProfile PickupV8Stock => PoliceInterceptorV8 with
    {
        Name = "5.3 V8 pickup, stock",
        BoreMm = 96f, StrokeMm = 92f,
        IdleRpm = 600f, RedlineRpm = 5600f,
        PeakTorqueNm = 450f, PeakTorqueRpm = 4000f,
    };

    // ── Street engines: the speedway engines with road exhausts; see Vehicles.cs "Street cars".

    public static EngineProfile Inline4Compact18 => Inline4Economy with
    {
        Name = "1.8 inline-4, stock exhaust",
        BoreMm = 80.5f, StrokeMm = 88.3f, PeakTorqueNm = 172f, PeakTorqueRpm = 4200f,
    };

    public static EngineProfile Inline4Midsize25 => Inline4Economy with
    {
        Name = "2.5 inline-4, stock exhaust",
        BoreMm = 90f, StrokeMm = 98f, PeakTorqueNm = 240f, PeakTorqueRpm = 4000f, RedlineRpm = 6200f,
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 30f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 36f },
        Exhaust = EngineProfile.Inline4Economy.Exhaust with { PrimaryDiameterMm = 38f, CollectorDiameterMm = 55f, TailpipeDiameterMm = 54f },
    };

    public static EngineProfile Inline4SportStreet => Inline4Sport with
    {
        Name = "2.0 inline-4, sport cat-back",
        Exhaust = EngineProfile.Inline4Sport.Exhaust with
        {
            Muffler = MufflerSpec.Stock with { Absorption = 0.15f, BaffleLoss = 0.35f, ResonatorHz = 0f },
        },
    };

    public static EngineProfile Boxer4Street => Boxer4 with
    {
        Name = "2.5 flat-4, unequal headers, cat-back",
        Exhaust = EngineProfile.Boxer4.Exhaust with
        {
            Muffler = MufflerSpec.Stock with { Absorption = 0.3f, ResonatorHz = 0f },
        },
    };

    public static EngineProfile Inline6Street => Inline6 with
    {
        Name = "3.0 inline-6, stock exhaust",
        Exhaust = EngineProfile.Inline6.Exhaust with { Muffler = MufflerSpec.Stock with { ResonatorHz = 110f } },
    };

    /// <summary>
    /// The International DT466 out of a school bus: 116.5 x 118.9 mm on six, 7.63 litres, 16.5:1,
    /// 800 lb-ft (1,085 Nm), governed around 2,500. Against the Cummins, geometry: a bore 14 mm wider
    /// knocks lower (4.5 kHz against 5.2), and four metres of pipe into a chambered can make the soft
    /// chuffing idle of a bus at a stop.
    /// </summary>
    public static EngineProfile DieselBusI6 => new()
    {
        Name = "7.6 DT466 bus diesel",
        Layout = EngineLayout.Inline,
        Fuel = FuelType.Diesel, Induction = Induction.Turbocharged, BoostBar = 1.5f,
        FiringAngles = EvenFire(new[] { 1, 5, 3, 6, 2, 4 }),
        Bank = OneBank(6),
        BoreMm = 116.5f, StrokeMm = 118.9f, RodRatio = 1.75f, CompressionRatio = 16.5f,
        ExhaustCam = new CamLobe { DurationDegrees = 238f, MaxLiftMm = 11.5f, RampFraction = 0.26f, CentrelineDegrees = 256f },
        IntakeCam = new CamLobe { DurationDegrees = 234f, MaxLiftMm = 11.5f, RampFraction = 0.26f, CentrelineDegrees = 478f },
        ExhaustValve = new ValveSpec { Count = 1, DiameterMm = 46f, DischargeCoefficient = 0.6f },
        IntakeValve = new ValveSpec { Count = 1, DiameterMm = 50f, DischargeCoefficient = 0.6f },
        EvoTemperatureK = 990f, IdleMapBar = 1.0f,
        CombustionVariation = 0.02f, IdleRoughness = 0.16f, IdleGovernorGain = 5.5f,
        IdleRpm = 700f, RedlineRpm = 2500f,
        InertiaKgM2 = 1.4f, FrictionNm = 95f, FrictionNmPerKrpm = 32f,
        PeakTorqueNm = 1085f, PeakTorqueRpm = 1400f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.2f, PrimarySpread = 0.55f, PrimaryDiameterMm = 50f,
            CollectorGroups = new[] { new[] { 0, 1, 2, 3, 4, 5 } },
            CollectorDiameterMm = 102f, CollectorPipeMetres = 0.5f,
            Crossover = CrossoverKind.Merged,
            MidPipeMetres = 4.0f,   // under the floor to a big can at the back
            Muffler = MufflerSpec.Stock with { ChamberLengthsMetres = new[] { 0.35f, 0.45f }, ExpansionRatio = 8f, Absorption = 0.55f, ResonatorHz = 45f },
            TailpipeMetres = new[] { 0.7f },
            TailpipeDiameterMm = 102f,
            GasCelsiusIdle = 150f, GasCelsiusFull = 580f,
            WallLossMultiplier = 2f,
            OverrunPopRate = 0f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.22f, RunnerDiameterMm = 48f, PlenumLitres = 9f, ThrottleDiameterMm = 85f, AirboxLitres = 26f, SnorkelLengthMetres = 1.1f, SnorkelDiameterMm = 100f, Level = 0.3f, Absorption = 0.5f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.8f, CombustionKnock = 1.4f, AccessoryWhineLevel = 0.2f, TurboWhistleLevel = 0.7f, TurboLagSeconds = 1.3f },
    };

    /// <summary>
    /// The 5.9 Cummins without the turbo: the 6B, sold in tractors, boats and gensets. Compression up to
    /// 19:1 (only the piston heats the air), so a shorter ignition delay and a smaller premixed spike:
    /// it clatters less. A third less torque. Every pulse goes out the pipe with no turbine to absorb it
    /// (the loud part), and the intake honks where a turbo one whooshes. All of it from Induction and
    /// the compression ratio.
    /// </summary>
    public static EngineProfile DieselCumminsNaI6 => DieselCumminsI6 with
    {
        Name = "5.9 Cummins 6B, no turbo",
        Induction = Induction.NaturallyAspirated, BoostBar = 0f,
        CompressionRatio = 19f,
        PeakTorqueNm = 470f, PeakTorqueRpm = 1500f,
        Mechanical = DieselCumminsI6.Mechanical with { TurboWhistleLevel = 0f },
    };

    /// <summary>The DT466 as first sold: naturally aspirated, 17.5:1, about two thirds of the torque
    /// (as <see cref="DieselCumminsNaI6"/>).</summary>
    public static EngineProfile DieselBusNaI6 => DieselBusI6 with
    {
        Name = "7.6 DT466, no turbo",
        Induction = Induction.NaturallyAspirated, BoostBar = 0f,
        CompressionRatio = 17.5f,
        PeakTorqueNm = 700f, PeakTorqueRpm = 1400f,
        Mechanical = DieselBusI6.Mechanical with { TurboWhistleLevel = 0f },
    };

    /// <summary>
    /// A GE 7FDL16: the prime mover in an Amtrak Genesis and in thousands of freight locomotives.
    /// Sixteen cylinders of 229 mm bore and 267 mm stroke — 175 litres — turbocharged, four-stroke,
    /// and governed to eight fixed notches from 440 rpm to 1,050. Its firing rate is therefore 59 Hz
    /// at idle and 140 Hz flat out, in notches you can count. A 229 mm bore knocks at about a third of a
    /// truck engine's frequency, a thud; a dustbin-sized turbine and a short half-metre stack leave
    /// almost no pipe tuning, so you hear the ports and the turbo, not a note.
    /// </summary>
    public static EngineProfile Ge7Fdl16 => new()
    {
        Name = "GE 7FDL16, 175 litre turbocharged V16",
        Layout = EngineLayout.Vee,
        Fuel = FuelType.Diesel, Induction = Induction.Turbocharged, BoostBar = 1.7f,
        FiringAngles = EvenFire(new[] { 1, 10, 3, 12, 5, 14, 7, 16, 2, 9, 4, 11, 6, 13, 8, 15 }),
        Bank = HalfBanks(16),
        BoreMm = 228.6f, StrokeMm = 266.7f, RodRatio = 1.9f, CompressionRatio = 12.7f,
        ExhaustCam = new CamLobe { DurationDegrees = 250f, MaxLiftMm = 22f, RampFraction = 0.28f, CentrelineDegrees = 254f },
        IntakeCam = new CamLobe { DurationDegrees = 244f, MaxLiftMm = 22f, RampFraction = 0.28f, CentrelineDegrees = 472f },
        ExhaustValve = new ValveSpec { Count = 2, DiameterMm = 76f, DischargeCoefficient = 0.6f },
        IntakeValve = new ValveSpec { Count = 2, DiameterMm = 82f, DischargeCoefficient = 0.6f },
        EvoTemperatureK = 1010f, IdleMapBar = 1.05f,
        CombustionVariation = 0.018f, IdleRoughness = 0.12f, IdleGovernorGain = 9f,
        IdleRpm = 440f, RedlineRpm = 1050f, CrankingRpm = 120f,
        InertiaKgM2 = 165f, FrictionNm = 2600f, FrictionNmPerKrpm = 900f,
        PeakTorqueNm = 29800f, PeakTorqueRpm = 1050f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.34f, PrimarySpread = 0.5f, PrimaryDiameterMm = 92f,
            CollectorGroups = new[] { new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, new[] { 8, 9, 10, 11, 12, 13, 14, 15 } },
            CollectorDiameterMm = 185f, CollectorPipeMetres = 1.3f,
            Crossover = CrossoverKind.Merged, CrossoverTubeMetres = 0.5f, CrossoverArea = 0.9f,
            MidPipeMetres = 0.7f,
            // Not a muffler but the turbine: a big lossy absorptive expansion with no tuning to speak of.
            Muffler = MufflerSpec.Chambered40 with
            {
                Kind = MufflerKind.Absorptive, ChamberLengthsMetres = new[] { 0.42f },
                ExpansionRatio = 14f, Absorption = 0.62f, AbsorptiveLengthMetres = 0.5f, BaffleLoss = 0.2f,
            },
            TailpipeMetres = new[] { 0.55f }, TailpipeDiameterMm = 260f,
            GasCelsiusIdle = 220f, GasCelsiusFull = 640f,
            WallLossMultiplier = 1.6f, Steepening = 1.1f, JetNoiseLevel = 1.3f, OverrunPopRate = 0f,
        },
        Intake = new IntakeSpec
        {
            RunnerLengthMetres = 0.30f, RunnerDiameterMm = 86f, PlenumLitres = 120f,
            ThrottleDiameterMm = 300f, AirboxLitres = 900f,
            SnorkelLengthMetres = 1.6f, SnorkelDiameterMm = 330f, Level = 0.35f, Absorption = 0.55f,
        },
        Mechanical = new MechanicalSpec
        {
            ValvetrainLevel = 0.9f, CombustionKnock = 1.5f,
            AccessoryWhineOrder = 9.5f, AccessoryWhineLevel = 0.3f,
            TurboWhistleLevel = 0.9f, TurboLagSeconds = 3.5f,
        },
    };

    /// <summary>
    /// An EMD 645E3: sixteen cylinders, 230 by 254 mm, a two-stroke. At its 900 rpm maximum it fires 240
    /// times a second to a GE's 140 at 1,050, so it hums where the GE hammers. Uniflow scavenged: four
    /// exhaust valves in the head and a ring of liner ports the piston uncovers round bottom centre, fed
    /// by a Roots blower, so the "intake cam" is the piston edge.
    /// </summary>
    public static EngineProfile Emd645E3 => new()
    {
        Name = "EMD 645E3, 169 litre two-stroke V16",
        Layout = EngineLayout.Vee,
        Strokes = 2,
        Fuel = FuelType.Diesel, Induction = Induction.Turbocharged, BoostBar = 1.4f,
        FiringAngles = EvenFire(new[] { 1, 8, 9, 16, 3, 6, 11, 14, 4, 5, 12, 13, 2, 7, 10, 15 }, 2),
        Bank = HalfBanks(16),
        BoreMm = 230.2f, StrokeMm = 254f, RodRatio = 2.0f, CompressionRatio = 14.5f,
        // Exhaust valves open 75 degrees before bottom centre and shut 45 after: centred on BDC.
        ExhaustCam = new CamLobe { DurationDegrees = 150f, MaxLiftMm = 20f, RampFraction = 0.3f, CentrelineDegrees = 182f },
        // The ports: uncovered 55 degrees before bottom centre and covered 55 after, square about BDC.
        IntakeCam = new CamLobe { DurationDegrees = 110f, MaxLiftMm = 40f, RampFraction = 0.12f, CentrelineDegrees = 180f },
        ExhaustValve = new ValveSpec { Count = 4, DiameterMm = 62f, DischargeCoefficient = 0.62f },
        IntakeValve = new ValveSpec { Count = 1, DiameterMm = 170f, DischargeCoefficient = 0.72f },
        EvoTemperatureK = 1040f, IdleMapBar = 1.15f,
        CombustionVariation = 0.02f, IdleRoughness = 0.1f, IdleGovernorGain = 9f,
        IdleRpm = 315f, RedlineRpm = 900f, CrankingRpm = 110f,
        InertiaKgM2 = 175f, FrictionNm = 2900f, FrictionNmPerKrpm = 1100f,
        PeakTorqueNm = 31500f, PeakTorqueRpm = 900f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.30f, PrimarySpread = 0.45f, PrimaryDiameterMm = 100f,
            CollectorGroups = new[] { new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, new[] { 8, 9, 10, 11, 12, 13, 14, 15 } },
            CollectorDiameterMm = 190f, CollectorPipeMetres = 1.1f,
            Crossover = CrossoverKind.Merged, CrossoverTubeMetres = 0.45f, CrossoverArea = 0.9f,
            MidPipeMetres = 0.6f,
            Muffler = MufflerSpec.Chambered40 with
            {
                Kind = MufflerKind.Absorptive, ChamberLengthsMetres = new[] { 0.40f },
                ExpansionRatio = 12f, Absorption = 0.55f, AbsorptiveLengthMetres = 0.45f, BaffleLoss = 0.2f,
            },
            TailpipeMetres = new[] { 0.5f }, TailpipeDiameterMm = 270f,
            GasCelsiusIdle = 200f, GasCelsiusFull = 590f,
            WallLossMultiplier = 1.5f, Steepening = 1.1f, JetNoiseLevel = 1.35f, OverrunPopRate = 0f,
        },
        Intake = new IntakeSpec
        {
            RunnerLengthMetres = 0.25f, RunnerDiameterMm = 170f, PlenumLitres = 260f,
            ThrottleDiameterMm = 360f, AirboxLitres = 700f,
            SnorkelLengthMetres = 1.2f, SnorkelDiameterMm = 340f, Level = 0.55f, Absorption = 0.4f,
        },
        Mechanical = new MechanicalSpec
        {
            ValvetrainLevel = 1.0f, CombustionKnock = 1.3f,
            // The Roots blower's three lobes on each of two rotors: the whine under every EMD.
            AccessoryWhineOrder = 6f, AccessoryWhineLevel = 0.2f,
            BlowerWhineOrder = 15.6f, BlowerWhineLevel = 0.45f,
            // The turbo is geared to the crank through an overrunning clutch until the exhaust carries
            // it: a two-stroke cannot otherwise clear its cylinders. Under 0.4 it will not fire at idle.
            TurboWhistleLevel = 0.5f, TurboLagSeconds = 2.5f, TurboIdleSpool = 0.45f,
        },
    };

    /// <summary>
    /// A 5.2 litre aviation flat-four, the Lycoming O-320 kind: 130 by 98 mm, 8.5:1, two big valves a
    /// cylinder, magnetos, redline 2,700 because the prop on the crank has its tips at Mach 0.8. At
    /// 2,700 rpm it fires at 90 Hz, on top of a two-blade prop's 90 Hz blade passing: one sound.
    /// </summary>
    public static EngineProfile AeroFlat4 => new()
    {
        Name = "5.2 aviation flat-four",
        Layout = EngineLayout.Flat,
        FiringAngles = EvenFire(new[] { 1, 3, 2, 4 }),
        Bank = AlternatingBanks(4),
        BoreMm = 130.2f, StrokeMm = 98.4f, RodRatio = 1.75f, CompressionRatio = 8.5f,
        ExhaustCam = new CamLobe { DurationDegrees = 250f, MaxLiftMm = 11f, RampFraction = 0.25f, CentrelineDegrees = 252f },
        IntakeCam = new CamLobe { DurationDegrees = 246f, MaxLiftMm = 11f, RampFraction = 0.25f, CentrelineDegrees = 474f },
        ExhaustValve = new ValveSpec { DiameterMm = 46f, DischargeCoefficient = 0.6f },
        IntakeValve = new ValveSpec { DiameterMm = 52f, DischargeCoefficient = 0.62f },
        EvoTemperatureK = 1150f, IdleMapBar = 0.35f,
        IdleRoughness = 0.35f, IdleGovernorGain = 1.5f,
        IdleRpm = 650f, RedlineRpm = 2700f,
        // The crank alone; the prop is added by whoever bolts one on (AircraftProfile.PropInertiaKgM2).
        InertiaKgM2 = 0.25f,
        FrictionNm = 39.5f, FrictionNmPerKrpm = 14f,
        PeakTorqueNm = 400f, PeakTorqueRpm = 2400f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.45f, PrimarySpread = 0.2f, PrimaryDiameterMm = 44f,
            CollectorDiameterMm = 57f, CollectorPipeMetres = 0.25f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.15f,
            Muffler = new MufflerSpec { Kind = MufflerKind.Absorptive, Absorption = 0.4f, AbsorptiveLengthMetres = 0.3f },
            TailpipeMetres = new[] { 0.25f, 0.27f },
            TailpipeDiameterMm = 57f,
            TailpipeExitsMetres = new[] { new Vector3(-0.45f, 0f, 0f), new Vector3(0.45f, 0f, 0f) },
            GasCelsiusIdle = 300f, GasCelsiusFull = 760f,
            WallLossMultiplier = 1.6f,
            OverrunPopRate = 2f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.35f, RunnerDiameterMm = 40f, PlenumLitres = 3f, ThrottleDiameterMm = 55f, AirboxLitres = 6f, SnorkelLengthMetres = 0.4f, SnorkelDiameterMm = 80f, Level = 0.7f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 0.7f, CombustionKnock = 0.04f, AccessoryWhineOrder = 0f, AccessoryWhineLevel = 0f },
    };

    /// <summary>
    /// A 163 cc overhead-valve single: a walk-behind mower's engine. Governed at 2,900 rpm it fires at
    /// 24 Hz, below pitch, so a mower chuffs and you hear its second order (48 Hz) and the blade's 97 Hz.
    /// Fifteen centimetres of pipe into a fist-sized can; the intake nearly as loud as the exhaust. The
    /// governor holds one speed from the start (<see cref="GovernorSpec"/>).
    /// </summary>
    public static EngineProfile MowerSingle => new()
    {
        Name = "163 cc OHV single",
        Layout = EngineLayout.Inline,
        // A pull cord and a magneto sparking on the first compression.
        CrankingRpm = 600f, RevolutionsBeforeFiring = 1f,
        FiringAngles = new[] { 0f },
        Bank = new[] { 0 },
        BoreMm = 68f, StrokeMm = 45f, RodRatio = 1.9f, CompressionRatio = 8.5f,
        // As mild as a cam gets: almost no overlap.
        ExhaustCam = new CamLobe { DurationDegrees = 216f, MaxLiftMm = 6.2f, RampFraction = 0.25f, CentrelineDegrees = 246f },
        IntakeCam = new CamLobe { DurationDegrees = 212f, MaxLiftMm = 6.0f, RampFraction = 0.25f, CentrelineDegrees = 478f },
        ExhaustValve = new ValveSpec { Count = 1, DiameterMm = 23f, DischargeCoefficient = 0.6f },
        IntakeValve = new ValveSpec { Count = 1, DiameterMm = 27f, DischargeCoefficient = 0.6f },
        EvoTemperatureK = 1050f, IdleMapBar = 0.5f,
        IdleRoughness = 0.35f, IdleGovernorGain = 2f,
        IdleRpm = 1600f, RedlineRpm = 3600f,
        // The flywheel only; the blade's inertia is the machine's (ExternalInertia).
        InertiaKgM2 = 0.011f, FrictionNm = 0.8f, FrictionNmPerKrpm = 0.5f,
        PeakTorqueNm = 7.4f, PeakTorqueRpm = 2600f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthMetres = 0.15f, PrimaryDiameterMm = 22f,
            CollectorGroups = new[] { new[] { 0 } },
            CollectorDiameterMm = 22f, CollectorPipeMetres = 0.05f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.04f,
            // A stamped can with two baffles and no packing: tinny rather than quiet.
            Muffler = new MufflerSpec
            {
                Kind = MufflerKind.Baffled,
                ChamberLengthsMetres = new[] { 0.06f, 0.08f },
                ExpansionRatio = 7f, BaffleLoss = 0.5f,
                Absorption = 0.12f, AbsorptiveLengthMetres = 0.05f,
                ResonatorHz = 0f,
            },
            TailpipeMetres = new[] { 0.04f },
            TailpipeDiameterMm = 24f,
            GasCelsiusIdle = 260f, GasCelsiusFull = 620f,
            WallLossMultiplier = 1.1f,   // too short a run to lose the top
            OverrunPopRate = 1f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.06f, RunnerDiameterMm = 20f, PlenumLitres = 0.15f, ThrottleDiameterMm = 20f, AirboxLitres = 1.1f, SnorkelLengthMetres = 0.06f, SnorkelDiameterMm = 26f, Level = 1.0f, Absorption = 0.25f },
        // Solid lifters: audible tappets.
        Mechanical = new MechanicalSpec { ValvetrainLevel = 1.1f, CombustionKnock = 0.03f },
    };

    /// <summary>
    /// A 500 cc air-cooled 90-degree V-twin: a lawn tractor's engine. Firing twice per cycle, 53 Hz at
    /// 3,200 rpm, it drones where a push mower chuffs; the vee angle spaces the two bangs unevenly, the
    /// lope.
    /// </summary>
    public static EngineProfile MowerTwin => new()
    {
        Name = "500 cc air-cooled V-twin",
        Layout = EngineLayout.Vee,
        // A small electric starter, and the magneto fires on the first compression.
        CrankingRpm = 300f, RevolutionsBeforeFiring = 1f,
        // Both rods on one crankpin, 90 degrees apart: 270 then 450.
        FiringAngles = IntervalFire(new[] { 1, 2 }, new[] { 270f, 450f }),
        Bank = new[] { 0, 1 },
        BoreMm = 68f, StrokeMm = 68f, RodRatio = 1.8f, CompressionRatio = 8.8f,
        ExhaustCam = new CamLobe { DurationDegrees = 224f, MaxLiftMm = 7.0f, RampFraction = 0.25f, CentrelineDegrees = 244f },
        IntakeCam = new CamLobe { DurationDegrees = 220f, MaxLiftMm = 6.8f, RampFraction = 0.25f, CentrelineDegrees = 476f },
        ExhaustValve = new ValveSpec { Count = 1, DiameterMm = 26f, DischargeCoefficient = 0.6f },
        IntakeValve = new ValveSpec { Count = 1, DiameterMm = 30f, DischargeCoefficient = 0.6f },
        EvoTemperatureK = 1070f, IdleMapBar = 0.48f,
        IdleRoughness = 0.3f, IdleGovernorGain = 2f,
        IdleRpm = 1500f, RedlineRpm = 3900f,
        InertiaKgM2 = 0.03f, FrictionNm = 2.1f, FrictionNmPerKrpm = 0.9f,
        PeakTorqueNm = 27f, PeakTorqueRpm = 2800f,
        Exhaust = new ExhaustSpec
        {
            PrimaryLengthsMetres = new[] { 0.22f, 0.30f }, PrimaryDiameterMm = 26f,
            CollectorGroups = new[] { new[] { 0, 1 } },
            CollectorDiameterMm = 30f, CollectorPipeMetres = 0.10f,
            Crossover = CrossoverKind.None,
            MidPipeMetres = 0.08f,
            Muffler = new MufflerSpec
            {
                Kind = MufflerKind.Baffled,
                ChamberLengthsMetres = new[] { 0.10f, 0.14f },
                ExpansionRatio = 8f, BaffleLoss = 0.55f,
                Absorption = 0.2f, AbsorptiveLengthMetres = 0.1f,
                ResonatorHz = 0f,
            },
            TailpipeMetres = new[] { 0.08f },
            TailpipeDiameterMm = 30f,
            GasCelsiusIdle = 270f, GasCelsiusFull = 650f,
            WallLossMultiplier = 1.15f,
            OverrunPopRate = 2f,
        },
        Intake = new IntakeSpec { RunnerLengthMetres = 0.09f, RunnerDiameterMm = 26f, PlenumLitres = 0.3f, ThrottleDiameterMm = 26f, AirboxLitres = 2f, SnorkelLengthMetres = 0.08f, SnorkelDiameterMm = 34f, Level = 0.95f, Absorption = 0.25f },
        Mechanical = new MechanicalSpec { ValvetrainLevel = 1.0f, CombustionKnock = 0.035f },
    };

    /// <summary>Every preset by a short key: an engine a machine's parts list names needs one here.</summary>
    public static IReadOnlyDictionary<string, Func<EngineProfile>> Presets { get; } =
        new Dictionary<string, Func<EngineProfile>>(StringComparer.OrdinalIgnoreCase)
        {
            ["mower_single"] = () => MowerSingle,
            ["mower_twin"] = () => MowerTwin,
            ["v8_muscle"] = () => V8MuscleBigBlock,
            ["v8_sports"] = () => V8SportsFlowmaster40,
            ["v8_flatplane"] = () => V8FlatPlane,
            ["i4_economy"] = () => Inline4Economy,
            ["i4_compact"] = () => Inline4Compact18,
            ["i4_midsize"] = () => Inline4Midsize25,
            ["i4_sport_street"] = () => Inline4SportStreet,
            ["boxer4_street"] = () => Boxer4Street,
            ["i6_street"] = () => Inline6Street,
            ["i4_sport"] = () => Inline4Sport,
            ["i4_turbo"] = () => I4Turbo,
            ["i6"] = () => Inline6,
            ["v6"] = () => V6Sedan,
            ["vtwin"] = () => VTwin45,
            ["single"] = () => Single450,
            ["diesel_i4"] = () => DieselPickupI4,
            ["diesel_truck"] = () => DieselTruckI6,
            ["diesel_cummins"] = () => DieselCumminsI6,
            ["powerstroke73"] = () => PowerStroke73,
            ["duramax_compound"] = () => DuramaxCompound,
            ["cummins_compound"] = () => CumminsCompound,
            ["cummins_isb"] = () => CumminsIsbStepVan,
            ["iron_duke"] = () => IronDuke25,
            ["pickup_v8"] = () => PickupV8Stock,
            ["diesel_bus"] = () => DieselBusI6,
            ["diesel_cummins_na"] = () => DieselCumminsNaI6,
            ["diesel_bus_na"] = () => DieselBusNaI6,
            ["boxer4"] = () => Boxer4,
            ["v10"] = () => V10,
            ["v12"] = () => V12,
            ["nascar_v8"] = () => NascarV8,
            ["f1_v10"] = () => F1V10,
            ["police_v8"] = () => PoliceV8,
            ["police_interceptor"] = () => PoliceInterceptorV8,
            ["v8_charger440"] = () => V8Charger440,
            ["vtwin_stock"] = () => VTwin45Stock,
            ["vtwin_slipon"] = () => VTwin45SlipOn,
            ["v8_open_headers"] = () => V8OpenHeaders,
            ["v8_glasspack"] = () => V8BigBlockGlasspack,
            ["v8_mild"] = () => V8MildSmallBlock,
            ["v8_bigcam"] = () => V8BigCam,
            ["v8_blown"] = () => V8Blown,
            ["sportbike"] = () => SportBike,
            ["aero_flat4"] = () => AeroFlat4,
            ["ge_7fdl16"] = () => Ge7Fdl16,
            ["emd_645e3"] = () => Emd645E3,
        };

    /// <summary>An engine by its preset name: as changed in the world editor if it has been (it is a
    /// library kind, ModelLibrary.Kinds.Engine), otherwise as built. Every vehicle, small machine,
    /// train and aircraft that names it has the change.</summary>
    public static EngineProfile ByName(string key)
        => ModelLibrary.IsAuthored(ModelLibrary.Kinds.Engine, key) ? ModelLibrary.Get<EngineProfile>(ModelLibrary.Kinds.Engine, key)
         : Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No engine preset '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}
