using System.Collections.Generic;
using System.Text;
using static OpenFPS.Common.DoorPhysics;

namespace OpenFPS.Common;

/// <summary>
/// A car's power window: the openings it makes in the cabin, and the sound of it going down and up,
/// simulated as the mechanism the way the doors are (<see cref="SlidingDoor"/>, <see cref="KnobDoor"/>).
///
/// The parts:
///
///   The MOTOR: a 12 V permanent-magnet DC motor with its armature's resistance and inductance and its
///   back EMF, so it slows and draws more current as the load rises, and stalls when the glass can go no
///   further. Its can is pulled out of round by the torque ripple at the commutator's bar rate and
///   knocked by the brushes crossing the bars; its rotor is not perfectly balanced. That is the whine
///   and its harmonics, and the pitch falls as the glass binds.
///
///   The WORM GEAR: a single-start worm on a plastic wheel at 87:1, about 45 per cent efficient and so
///   self-locking: the weight of the glass cannot drive it backwards, which is why a window stays where
///   it was left and why lowering one still takes the motor's torque. The wheel's tooth error is a force
///   between worm and wheel at the worm's own rotation rate, ringing the gearbox housing, and a ripple
///   in the pull on the cable.
///
///   The REGULATOR: a cable round a drum, carrying the glass on a clamp. The cable is a spring, so the
///   glass rides on it, and a glass that sticks in its channels and lets go judders on it.
///
///   The GLASS: 4 mm toughened side glass sliding in flocked run channels at its front and rear edges,
///   with a rubber sweep pressed on each face at the waist of the door. The channels' friction is the
///   load the motor feels; the sweeps' lips stick and slip on the glass, which is the squeak, and only
///   where the lip is pressed hard and its flocking is worn. The pane's own bending field radiates into
///   the cabin.
///
///   The ENDS: at the top the glass edge runs into the header channel, which pinches it, and seats on
///   the channel's rubber floor: a soft thud and the seal squeezing, then the motor stalls against it
///   until the door module sees the stall and cuts it. At the bottom the glass clamp lands on the
///   regulator's rubber stop: a thunk through the door, and the same stall.
///
///   The DOOR: the motor and regulator are bolted to the door's inner panel, a damped 0.7 mm steel
///   sheet, and everything inside the door reaches the cabin through the trim card in front of it.
///
/// One door's window, for now: every vehicle uses it, as every vehicle uses the one car door.
/// </summary>
public static class CarWindow
{
    // ── The openings ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The door frame and the pillar between one side window and the next, metres along the
    /// side. A window's glass is the row's length less this.</summary>
    public const float PillarM = 0.12f;

    /// <summary>What of the glass height the frame and the waist seal cover, metres.</summary>
    public const float FrameM = 0.04f;

    /// <summary>How much of the rectangle a side window fills. Its top rear corner follows the rake of
    /// the roof and the rear of a back door's glass the wheel arch, so it is not a rectangle.</summary>
    public const float ShapeShare = 0.85f;

    /// <summary>
    /// What a vehicle's side windows are: how many (one each side of each row of seats), how big each
    /// is when fully down, and the cabin they open: its whole wall area and its volume.
    /// </summary>
    public readonly record struct Openings(int Count, float AreaEachM2, float CabinSurfaceM2, float CabinVolumeM3,
                                           float GlassHeightM, int Rows);

    /// <summary>The side windows of a vehicle with a cabin, or null for one with none (a motorcycle,
    /// a formula car).</summary>
    public static Openings? Measure(VehicleProfile v)
    {
        if (VehicleCabin.Measure(v) is not { } g) return null;
        int rows = VehicleCabin.Rows(g);
        float length = g.Lc / rows - PillarM;
        float height = g.RoofUnder - g.Belt - FrameM;
        if (length <= 0.1f || height <= 0.1f) return null;
        float surface = 2f * (g.Lc * g.Wc + g.Lc * g.Hc + g.Wc * g.Hc);
        return new Openings(2 * rows, length * height * ShapeShare, surface, g.Lc * g.Wc * g.Hc, height, rows);
    }

    /// <summary>
    /// The share of the cabin's wall that is a hole, with every side window <paramref name="open"/> of the
    /// way down. A hole has no mass and passes everything, so this is also the share of the power outside
    /// that reaches the inside through it, and of the power inside that gets out.
    /// </summary>
    public static float OpenShare(VehicleProfile v, float open)
        => Measure(v) is { } o ? Math.Clamp(open, 0f, 1f) * o.Count * o.AreaEachM2 / o.CabinSurfaceM2 : 0f;

    /// <summary>Car glass, metres. The windows are most of a cabin's area and all of its weakest panels,
    /// so the airborne path through the body is theirs.</summary>
    public const float GlassThicknessM = 0.004f;

    /// <summary>
    /// What the cabin's walls take off a sound passing through them, either way, dB (negative) at the
    /// mixer's three bands (150 Hz, 1 kHz, 4 kHz).
    ///
    /// Three paths in parallel, as power: the glass by its mass (transmission falls as rho*c / (pi*f*m)),
    /// the seals (<see cref="VehicleBody.SealLeak"/>, which have no mass and let a little of everything
    /// through), and whatever is simply open, <paramref name="openShare"/> of the wall: a window down, a
    /// bus's doorway. What is open is no longer glass, so the glass's share shrinks by as much.
    /// </summary>
    public static (float Low, float Mid, float High) CabinLossDb(VehicleBody body, float openShare)
    {
        float mass = MathF.Max(1f, AcousticRegistry.GetProperties("Glass").DensityKgM3 * GlassThicknessM);
        float open = Math.Clamp(openShare, 0f, 1f);
        float leak = body.SealLeak * body.SealLeak + open;
        float Loss(float hz)
        {
            float t = 415f / (MathF.PI * hz * mass);
            return 10f * MathF.Log10(MathF.Min(1f, t * t * (1f - open) + leak));
        }
        return (Loss(150f), Loss(1000f), Loss(4000f));
    }

    /// <summary>
    /// Moves a window's open fraction toward where it is going at the motor's own pace: the server
    /// moves the glass with this, and a client follows the same glass from the same two numbers, so
    /// what is heard opening and the opening itself arrive together.
    /// </summary>
    public static float Glide(float open, float target, float dt)
    {
        if (open < target) return MathF.Min(target, open + dt / DownSeconds);
        if (open > target) return MathF.Max(target, open - dt / UpSeconds);
        return open;
    }

    private static float? _down, _up;

    /// <summary>Seconds from shut to fully down: the model's own travel, worked out once.</summary>
    public static float DownSeconds => _down ??= Travel(0, 0f, 1f);

    /// <summary>Seconds from fully down to shut: slower, because going up the motor lifts the glass.</summary>
    public static float UpSeconds => _up ??= Travel(0, 1f, 0f);

    // ── The game ─────────────────────────────────────────────────────────────────────────────────

    public const string KeyPrefix = "carwindow:";
    public const int Variants = 4;

    /// <summary>A window's stroke is named to the nearest quarter: every stroke a player can ask for is
    /// one of a handful, and each can be rendered once and kept.</summary>
    public static float Quarter(float open) => MathF.Round(Math.Clamp(open, 0f, 1f) * 4f) / 4f;

    public static string Key(int variant, float from, float to)
        => FormattableString.Invariant(
            $"{KeyPrefix}{((variant % Variants) + Variants) % Variants}:{(int)MathF.Round(Quarter(from) * 4f)}:{(int)MathF.Round(Quarter(to) * 4f)}");

    public static bool TryParseKey(string? key, out int variant, out float from, out float to)
    {
        variant = 0; from = 0f; to = 0f;
        if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
        var p = key.Substring(KeyPrefix.Length).Split(':');
        if (p.Length != 3 || !int.TryParse(p[0], out int v) || !int.TryParse(p[1], out int f) || !int.TryParse(p[2], out int t))
            return false;
        if (f < 0 || f > 4 || t < 0 || t > 4 || f == t) return false;
        variant = ((v % Variants) + Variants) % Variants;
        from = f / 4f; to = t / 4f;
        return true;
    }

    /// <summary>The sound a key names, peak one.</summary>
    public static float[] RenderKey(string key, int sampleRate)
    {
        if (!TryParseKey(key, out int variant, out float from, out float to)) return new float[16];
        var pcm = Render(variant, from, to, sampleRate);
        float peak = 1e-9f;
        foreach (float v in pcm) peak = MathF.Max(peak, MathF.Abs(v));
        for (int i = 0; i < pcm.Length; i++) pcm[i] /= peak;
        return pcm;
    }

    /// <summary>
    /// The loudest a stroke is at a metre from the door on the cabin side, dB SPL peak: the model's own,
    /// by variant (all the old character since 2026-10-04, each its own seed) and by how the stroke ends, read off the renders with no
    /// calibration (CarWindowTests holds them to it). Fully down, the clamp landing on its stop; fully up,
    /// the glass seating and the header's lips squeezing; part way, the motor running and nothing hit.
    /// </summary>
    public static float LevelDb(int variant, float to)
    {
        int v = ((variant % Variants) + Variants) % Variants;
        return to >= 1f ? BottomDb[v] : to <= 0f ? TopDb[v] : BetweenDb[v];
    }

    // By seed now that every window is the old one (2026-10-04); part way is the mean of the stroke down
    // and the stroke up, which differ by up to 3 dB.
    private static readonly float[] BottomDb = { 87.1f, 83.6f, 86.6f, 86.2f };
    private static readonly float[] TopDb = { 83.1f, 80.6f, 80.1f, 80.9f };
    private static readonly float[] BetweenDb = { 76.9f, 72.7f, 76.7f, 75.4f };

    /// <summary>How long a stroke's sound lasts, seconds: the travel, the stall at an end, the ring-down.</summary>
    public static float Seconds(float from, float to)
    {
        float travel = MathF.Abs(to - from) * (to > from ? DownSeconds : UpSeconds);
        bool end = to <= 0f || to >= 1f;
        return PreRoll + travel + (end ? (float)StallCutSeconds + 0.1f : 0.05f) + TailSeconds;
    }

    // ── Rendering ────────────────────────────────────────────────────────────────────────────────

    public const double PascalsAtFullScale = 2.0;

    public sealed class Report
    {
        public readonly List<string> Events = new();
        public double PeakPascals;
        /// <summary>Seconds from the switch closing to the glass arriving.</summary>
        public double TravelSeconds;
        public double PeakAmps, RunningAmps, RunningRpm;
        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var e in Events) sb.AppendLine("    " + e);
            sb.Append($"    travel {TravelSeconds:F2} s, running {RunningAmps:F1} A at {RunningRpm:F0} rpm, peak {PeakAmps:F1} A; "
                    + $"peak {20 * Math.Log10(Math.Max(1e-9, PeakPascals) / 2e-5):F1} dB SPL at 1 m");
            return sb.ToString();
        }
    }

    /// <summary>One stroke, from <paramref name="from"/> open to <paramref name="to"/> open (0 shut, 1 fully
    /// down), in pascals over <see cref="PascalsAtFullScale"/>, at a metre from the door on the cabin side.</summary>
    public static float[] Render(int variant, float from, float to, int sampleRate, Report? report = null)
    {
        var sim = new Sim(variant, sampleRate, report, quiet: false);
        sim.Run(from, to);
        return sim.Output();
    }

    /// <summary>How long the glass takes to get from one place to another, seconds: the mechanism alone,
    /// with nothing listened to.</summary>
    public static float Travel(int variant, float from, float to)
    {
        var sim = new Sim(variant, 48000, null, quiet: true);
        sim.Run(from, to);
        return (float)sim.TravelTime;
    }

    // ── Constants, each a property of a part ─────────────────────────────────────────────────────

    /// <summary>Internal steps per output sample. Nothing here is stiff the way a steel latch on a steel
    /// strike is: the stiffest parts are a rubber lip (about 500 Hz, its bristles a few kilohertz) and the
    /// brushes' 0.2 ms knocks, which the output rate itself follows. A window is rendered as it is first
    /// heard, so every step it does not need is a step the game is not waiting for.</summary>
    private const int Over = 1;
    private const double G = 9.81;
    private const double GlassE = 70e9, GlassRho = 2500, SteelE = 200e9, SteelRho = 7850;
    /// <summary>The pane: a front door's toughened glass, 0.75 by 0.55 m, 4 mm, about 4.1 kg, and the
    /// regulator's clamp and lifter plate under it. Its drop is 0.45 m from shut to fully down.</summary>
    private const double PaneW = 0.75, PaneH = 0.55, PaneT = 0.004, CarrierKg = 0.35, Drop = 0.45;
    /// <summary>The motor (a typical window lift motor): 12.6 V from a battery at rest, 0.6 ohm and
    /// 0.5 mH of armature (21 A stalled), 0.017 V s/rad of back EMF (about 7000 rpm unloaded), 2e-5
    /// kg m^2 of armature and worm, and its brushes' and bearings' drag.</summary>
    private const double Volts = 12.6, Ohms = 0.6, Henries = 5e-4, MotorK = 0.017, RotorJ = 2e-5;
    private const double BrushDragNm = 0.004, ViscousNm = 2e-6;
    /// <summary>Ten commutator bars; a 1.2 mm can 48 mm across; the brushes ride a 6 mm commutator.</summary>
    private const int Bars = 10;
    private const double CanR = 0.024, CanT = 0.0012, CommutatorR = 0.006;
    private const double BrushKg = 0.002, BarGap = 5e-4, BrushLanding = 2e-4;
    /// <summary>The rotor's residual unbalance, kg m: about a gram-millimetre, a balancing grade a cheap
    /// motor meets. It shakes the motor at the rotation rate: the fundamental under the whine.</summary>
    private const double Unbalance = 1e-6;
    /// <summary>Torque ripple at the bar rate, as a share of the torque: a few per cent on ten bars.</summary>
    private const double Ripple = 0.05;
    /// <summary>The worm: one start, an 87-tooth wheel 40 mm across, 45 per cent efficient forward. Below
    /// half it cannot be driven backwards: lowering the glass takes the motor (1/eta - 2) of what the
    /// glass's weight would give it.</summary>
    private const double Ratio = 87, Eta = 0.45, WheelR = 0.02;
    /// <summary>The gear mesh's stiffness, plastic on steel, N/m: a few microns of tooth error is newtons
    /// between worm and wheel.</summary>
    private const double MeshK = 2e6;
    /// <summary>The regulator: a 22 mm cable drum; two runs of 1.5 mm cable and their sheaves, about
    /// 6e4 N/m to the glass, which rides on them at about 19 Hz, losing a fifth of critical in the sheaves
    /// and the cable's sleeves.</summary>
    private const double DrumR = 0.022, CableK = 6e4, CableZeta = 0.2;
    /// <summary>The motor, its gearbox and the drum's housing: 0.9 kg bolted through the regulator's plate
    /// to the door's inner panel, standing on it at about 200 Hz.</summary>
    private const double DriveKg = 0.9, DriveMountHz = 200, DriveMountZeta = 0.1, DriveArm = 0.03;
    /// <summary>The top of the travel: the header channel's lips pinch the last 15 mm of the glass with up
    /// to 40 N more, and its rubber floor stops the edge: Hunt-Crossley, about 3 mm under 300 N, lossy.</summary>
    private const double SealEntry = 0.015, TopPinchN = 40, SealK = 1.8e6, SealLambda = 3;
    /// <summary>The bottom: the glass clamp on the regulator rail's rubber stop.</summary>
    private const double StopK = 8e6, StopLambda = 1.0;
    /// <summary>What share of a blow at the frame or the rail bends the door's inner sheet rather than
    /// moving the door: the sheet's point impedance (about 50 N s/m for 0.7 mm steel) against the
    /// flanged, hemmed edge where the rail and the header are fixed, which is several times stiffer.</summary>
    private const double FrameShare = 0.1;
    /// <summary>The door module cuts a stalled motor after this long: it counts the current's commutation
    /// ripple, and when that stops it waits a little to be sure.</summary>
    private const double StallCutSeconds = 0.15, StallShare = 0.3;
    /// <summary>The sweeps at the waist, one on each face of the glass: a strip of rubber whose lip moves
    /// with the glass until it lets go, about 3 g of lip on 3e4 N/m of its own shear (500 Hz), lightly
    /// damped. Rubber on glass slips in pre-slides far longer than a steel pin's.</summary>
    private const double LipKg = 0.003, LipK = 3e4, LipZeta = 0.06, LipBristle = 2e5;
    /// <summary>A lip stands at an angle to the pane, so its stick-slip pushes the glass as much as it drags
    /// it; the regulator's clamp pulls in the plane of the pane a few millimetres off its middle.</summary>
    private const double LipCoupling = 0.5, CarrierEccentricity = 0.1;
    /// <summary>The run channels' flock: fibres slipping one at a time, so their drag flutters by about
    /// 0.3 / sqrt(fibres) of itself, with a couple of hundred thousand fibres on the glass.</summary>
    private const double FlockFibres = 2e5;
    /// <summary>The door's trim card: a third of the pressure comes straight through its openings (the
    /// speaker grille, the handle recess, the gaps at its clips); the card itself passes less the higher it
    /// goes, by its mass, 6 dB an octave above about 600 Hz.</summary>
    private const double TrimLeak = 0.35, TrimHz = 600;
    /// <summary>Silence before the switch, and after the motor stops.</summary>
    private const float PreRoll = 0.03f, TailSeconds = 0.35f;

    /// <summary>
    /// What wear does to a window, by character: new, standard, worn, old. The run channels' preload and
    /// friction (static and sliding), the sweeps' load each and their friction, the gear's tooth error,
    /// and the step between commutator bars the brushes fall over.
    /// </summary>
    private readonly record struct Character(double ChannelN, double ChannelMuS, double ChannelMuK,
        double LipN, double LipMuS, double LipMuK, double ToothM, double BarStepM);

    /// <summary>Every window has the old window's character: Cody, 2026-10-04, "the car window v3 sounds
    /// the best, use that" (the other three are in docs/COMMON_NOTES.md). The variant still seeds the
    /// random detail, so two windows rolling together are never one sound copied to two places.</summary>
    private static readonly Character Old = new(65, 0.62, 0.48, 5.0, 1.00, 0.62, 18e-6, 8e-6);

    private sealed class Sim
    {
        private readonly int rate;
        private readonly double dt;
        private readonly bool quiet;
        private readonly Report? report;
        private readonly Random rng;
        private readonly Character ch;
        private readonly List<float> outHi = new();
        private bool recording;
        private double time, switchedAt = -1, arrivedAt = -1;
        public double TravelTime => switchedAt >= 0 && arrivedAt >= switchedAt ? arrivedAt - switchedAt : 0;

        // The glass, up positive from fully down; the rotor; the armature current.
        private readonly double glassKg;
        private double x, v, theta, w, amps;
        private double drive;                 // +1 up, -1 down, 0 released (terminals shorted)
        private readonly double wFree;
        private LuGre channel;
        // Three lips on the glass: the sweeps on its two faces at the waist, and the header channel's lips,
        // which only touch it in the last few centimetres of its travel.
        private const int Lips = 3;
        private readonly LuGre[] lip = new LuGre[Lips];
        private readonly double[] lipX = new double[Lips], lipV = new double[Lips], lipAcc = new double[Lips];
        private readonly double[] channelProfile, lipProfile0, lipProfile1;
        private readonly double profileStep;

        // The sound.
        private readonly DenseField? glassField, doorField;
        private readonly Modes? can, gearbox;
        private readonly double[] canShape = Array.Empty<double>(), gearShape = Array.Empty<double>();
        private readonly Mount? mount;
        private readonly Port? mountPort, sealPort, carrierPort;
        private readonly Port[] lipPort = new Port[Lips];
        private readonly double[] mountHit = Array.Empty<double>(), stopHit = Array.Empty<double>(),
                                  sealHit = Array.Empty<double>(), sealFrameHit = Array.Empty<double>(),
                                  carrierHit = Array.Empty<double>(), flockHit = Array.Empty<double>();
        private readonly double[][] lipHit = new double[Lips][];
        private readonly HighPass?[] lipHigh = new HighPass?[Lips];
        private readonly HighPass? carrierHigh;
        private readonly double[] toothError = new double[(int)Ratio];
        private readonly double[] profilePhase = new double[8];
        private double meshTurns, lastBar, brushLeft, brushPeak, trimLow, flock;
        private double peakGlass, peakDoor, peakMotor;

        public Sim(int variant, int sampleRate, Report? report, bool quiet)
        {
            this.report = report; this.quiet = quiet;
            rate = sampleRate * Over; dt = 1.0 / rate;
            rng = new Random(1 + ((variant % Variants) + Variants) % Variants);
            ch = Old;
            glassKg = PaneW * PaneH * PaneT * GlassRho + CarrierKg;
            wFree = (Volts - Ohms * BrushDragNm / MotorK) / MotorK;

            channel = new LuGre { MuStatic = ch.ChannelMuS, MuSliding = ch.ChannelMuK, StribeckSpeed = 0.004,
                                  Viscous = 0.3 };
            for (int i = 0; i < Lips; i++)
                lip[i] = new LuGre { MuStatic = ch.LipMuS, MuSliding = ch.LipMuK, StribeckSpeed = 0.01,
                                     Viscous = 0.05, Bristle = LipBristle };
            // How hard the channels and the sweeps press varies along the glass: a curved pane in channels
            // that are not quite the same curve, and sweeps whose lips are not quite straight. Smooth over
            // a few centimetres; the same window every time it runs.
            profileStep = 0.005;
            int n = (int)((Drop + 0.1) / profileStep) + 2;
            channelProfile = Smooth(n, 8); lipProfile0 = Smooth(n, 5); lipProfile1 = Smooth(n, 5);
            for (int i = 0; i < toothError.Length; i++) toothError[i] = (rng.NextDouble() * 2 - 1) * 0.15;
            for (int h = 0; h < profilePhase.Length; h++) profilePhase[h] = rng.NextDouble() * 2 * Math.PI;
            if (quiet) return;

            // The pane in its channels: glass barely loses on its own; the flocked channels round its
            // edges and the sweeps take a few per cent, more low down where the edges move most.
            glassField = new DenseField(PaneW, PaneH, PaneT, GlassE, GlassRho, 0.22, f => 0.03 + 10 / f,
                                        50, 8000, rng, dt, capSpacing: 90);
            // The door's inner panel: 0.7 mm steel with bitumen pads and the trim card's clips on it.
            doorField = new DenseField(0.9, 0.5, 0.0007, SteelE, SteelRho, Poisson, f => ThinPanelLoss(f) + 0.05,
                                       60, 5000, rng, dt, capSpacing: 90);
            mount = new Mount(DriveKg, DriveKg * Math.Pow(2 * Math.PI * DriveMountHz, 2), DriveMountZeta);
            mountPort = new Port(doorField.PatchMass, 2e7, doorField.Impedance);
            sealPort = new Port(glassField.PatchMass, 2e5, glassField.Impedance);
            carrierPort = new Port(glassField.PatchMass, 2e5, glassField.Impedance);
            mountHit = doorField.Point(); stopHit = doorField.Point(); sealFrameHit = doorField.Point();
            sealHit = glassField.Point(); carrierHit = glassField.Point(); flockHit = glassField.Point();
            for (int i = 0; i < Lips; i++)
            {
                lipPort[i] = new Port(glassField.PatchMass, 2e5, glassField.Impedance);
                lipHit[i] = glassField.Point();
                lipHigh[i] = new HighPass(200, rate);
            }
            carrierHigh = new HighPass(40, rate);

            // The motor's can: its ring modes n = 2 to 6, a longer and a shorter axial version of each.
            var hz = new List<double>(); var l = new List<double>(); var m = new List<double>(); var gn = new List<double>();
            for (int k = 2; k <= 6; k++)
                foreach (double axial in new[] { 1.0, 1.25 })
                {
                    hz.Add(Ring(CanR, CanT, SteelRho, SteelE, k) * axial);
                    l.Add(0.08); m.Add(0.04); gn.Add(SmallPlateGain(0.015, 0.2) * (rng.NextDouble() < 0.5 ? -1 : 1));
                }
            can = new Modes(hz, l, m, gn, dt);
            canShape = new double[hz.Count];
            for (int i = 0; i < canShape.Length; i++) canShape[i] = (rng.NextDouble() * 2 - 1) * Math.Sqrt(3);
            // The gearbox: a glass-filled plastic housing whose walls ring from about 900 Hz, lossy.
            var gh = new List<double>(); var gl = new List<double>(); var gm = new List<double>(); var gg = new List<double>();
            for (int i = 0; i < 12; i++)
            {
                gh.Add(900 * Math.Pow(7, i / 11.0) * (0.95 + 0.1 * rng.NextDouble()));
                gl.Add(0.08); gm.Add(0.03); gg.Add(SmallPlateGain(0.008, 0.3) * (rng.NextDouble() < 0.5 ? -1 : 1));
            }
            gearbox = new Modes(gh, gl, gm, gg, dt);
            gearShape = new double[gh.Count];
            for (int i = 0; i < gearShape.Length; i++) gearShape[i] = (rng.NextDouble() * 2 - 1) * Math.Sqrt(3);
        }

        private double[] Smooth(int n, int width)
        {
            var raw = new double[n + 2 * width];
            for (int i = 0; i < raw.Length; i++) raw[i] = rng.NextDouble() * 2 - 1;
            var s = new double[n];
            double sum2 = 0;
            for (int i = 0; i < n; i++)
            {
                double a = 0;
                for (int j = -width; j <= width; j++) a += raw[i + width + j] * (1 - Math.Abs(j) / (double)(width + 1));
                s[i] = a; sum2 += a * a;
            }
            double rms = Math.Sqrt(sum2 / n);
            for (int i = 0; i < n; i++) s[i] /= rms;
            return s;
        }

        private double ProfileAt(double[] p, double at)
        {
            double u = Math.Clamp((at + 0.05) / profileStep, 0, p.Length - 1.001);
            int i = (int)u; double f = u - i;
            return p[i] * (1 - f) + p[i + 1] * f;
        }

        private void Log(string s) => report?.Events.Add(s);

        public void Run(float from, float to)
        {
            from = Math.Clamp(from, 0f, 1f); to = Math.Clamp(to, 0f, 1f);
            // Where the glass starts: shut, it is pressed into the header seal by however hard the motor
            // stalled it there; fully down, it sits on its stop.
            x = Drop * (1 - from);
            if (from <= 0f) x = Drop + 0.002;
            if (from >= 1f) x = 0;
            // The drum where the cable holds the glass there, so nothing jumps as the simulation starts.
            theta = x / DrumR * Ratio;
            double target = Drop * (1 - to);
            bool toEnd = to <= 0f || to >= 1f;
            drive = 0;
            // Let it settle on its cable and its seal before anything is listened to.
            for (double t = 0; t < 0.3; t += dt) Step();
            recording = true;
            time = 0;
            for (double t = 0; t < PreRoll; t += dt) Step();

            double dir = to < from ? 1 : -1;
            drive = dir;
            switchedAt = time;
            Log($"switch {(dir > 0 ? "up" : "down")} from {from:F2} to {to:F2}");
            double stalledFor = 0, runAmps = 0, runW = 0; int runN = 0;
            double peakAmps = 0;
            while (time < 15)
            {
                Step();
                peakAmps = Math.Max(peakAmps, Math.Abs(amps));
                double elapsed = time - switchedAt;
                if (elapsed > 0.4 && arrivedAt < 0) { runAmps += Math.Abs(amps); runW += Math.Abs(w); runN++; }
                if (toEnd)
                {
                    bool atEnd = dir > 0 ? x >= Drop - 0.0005 : x <= 0.0005;
                    if (atEnd && arrivedAt < 0) { arrivedAt = time; Log($"glass at the {(dir > 0 ? "top" : "bottom")} at {elapsed:F2} s"); }
                    // The module cuts a motor that has stopped turning, once it is sure.
                    if (elapsed > 0.25 && Math.Abs(w) < StallShare * wFree) stalledFor += dt; else stalledFor = 0;
                    if (stalledFor >= StallCutSeconds) { Log($"stall cut at {elapsed:F2} s, {Math.Abs(amps):F1} A"); break; }
                }
                else if ((x - target) * dir >= 0)
                {
                    arrivedAt = time;
                    Log($"switch released at {elapsed:F2} s");
                    break;
                }
            }
            if (arrivedAt < 0) arrivedAt = time;
            drive = 0;
            double end = time + TailSeconds;
            while (time < end) Step();
            if (report != null)
            {
                report.TravelSeconds = TravelTime;
                report.PeakAmps = peakAmps;
                report.RunningAmps = runN > 0 ? runAmps / runN : 0;
                report.RunningRpm = runN > 0 ? runW / runN * 60 / (2 * Math.PI) : 0;
            }
        }

        private void Step()
        {
            // ── The motor's circuit ──
            // Switched on, the battery across the armature; released, the switch shorts its terminals, so
            // the back EMF drives a current that brakes it to a stop in a few hundredths of a second.
            double emf = MotorK * w;
            double steady = (Volts * drive - emf) / Ohms;
            amps = steady + (amps - steady) * Math.Exp(-dt * Ohms / Henries);
            double torque = MotorK * amps;

            // ── The worm's tooth error, at the wheel's pitch circle ──
            meshTurns += w / (2 * Math.PI) * dt;           // one wheel tooth per turn of a single-start worm
            int teeth = toothError.Length;
            int tooth = (int)((Math.Floor(meshTurns) % teeth + teeth) % teeth);
            double frac = meshTurns - Math.Floor(meshTurns);
            double profile = 0;
            for (int h = 0; h < profilePhase.Length; h++) profile += Math.Sin(2 * Math.PI * (h + 1) * frac + profilePhase[h]) / (h + 1);
            double te = ch.ToothM * (0.5 * profile + toothError[tooth] * (1 - frac) + toothError[(tooth + 1) % teeth] * frac);

            // ── The cable between the drum and the glass ──
            double drum = theta / Ratio * DrumR + te * DrumR / WheelR;
            double drumRate = w / Ratio * DrumR;
            double cableC = 2 * CableZeta * Math.Sqrt(CableK * glassKg);
            double cable = CableK * (drum - x) + cableC * (drumRate - v);

            // ── The rotor, through a self-locking worm ──
            // What the cable asks of the drum, seen at the worm: divided by the ratio and the efficiency when
            // the motor drives it, and still a drag of (1/eta - 2) of it when the glass's weight would like
            // to run it the other way, because a worm under half efficient cannot be driven backwards.
            double tOut = cable * DrumR;                    // resists turning up
            double ResistIn(double s)
            {
                double resist = tOut * s;
                return resist >= 0 ? resist / (Ratio * Eta) : -resist * (1 / Eta - 2) / Ratio;
            }
            if (Math.Abs(w) > 1e-3)
            {
                double s = Math.Sign(w);
                double acc = (torque - s * (ResistIn(s) + BrushDragNm) - ViscousNm * w) / RotorJ;
                double wNew = w + acc * dt;
                w = Math.Sign(wNew) != s ? 0 : wNew;
            }
            else
            {
                // At rest it turns only if the motor beats the load AND the friction in the chosen direction.
                double s = torque >= 0 ? 1 : -1;
                double net = torque * s - ResistIn(s) - BrushDragNm;
                w = net > 0 ? s * net / RotorJ * dt : 0;
            }
            theta += w * dt;

            // ── The glass ──
            double topIn = x - (Drop - SealEntry);
            double pinch = topIn > 0 ? TopPinchN * Math.Min(1, topIn / SealEntry) : 0;
            double load = Math.Max(0.3 * ch.ChannelN, ch.ChannelN * (1 + 0.25 * ProfileAt(channelProfile, x)));
            double friction = channel.Force(v, load, glassKg, dt);
            double seal = Contact(SealK, SealLambda, x - Drop, v);
            double stop = Contact(StopK, StopLambda, -x, -v);
            double lipsOnGlass = 0;
            for (int i = 0; i < Lips; i++)
            {
                double press = i < 2 ? ch.LipN * Math.Max(0.1, 1 + 0.5 * ProfileAt(i == 0 ? lipProfile0 : lipProfile1, x))
                                     : pinch;
                double rel = v - lipV[i];
                // Quiet, only the drag matters, and the lip's own motion is too small to change it.
                double f = quiet ? (press > 0 ? lip[i].Force(v, press, glassKg, dt) : 0) : lip[i].Force(rel, press, LipKg, dt);
                lipsOnGlass += f;
                if (quiet) continue;
                double lipC = 2 * LipZeta * Math.Sqrt(LipK * LipKg);
                lipAcc[i] = (f - LipK * lipX[i] - lipC * lipV[i]) / LipKg;
                lipV[i] += lipAcc[i] * dt; lipX[i] += lipV[i] * dt;
            }
            double glassAcc = (cable - glassKg * G - friction - lipsOnGlass - seal + stop) / glassKg;
            v += glassAcc * dt; x += v * dt;

            if (quiet) { time += dt; return; }

            // ── What it all does to the panels ──
            // The motor on its bracket: the rotor's unbalance at the rotation rate, the reaction of the
            // torque ripple at the bar rate, and the mesh force, through the bracket into the door's panel.
            double turning = Math.Tanh(Math.Abs(w) / 50);
            mount!.F += Unbalance * w * w * Math.Sin(theta)
                      + Ripple * torque / DriveArm * Math.Sin(Bars * theta)
                      + MeshK * te * turning;
            mount.Step(dt);
            doorField!.Modes.Push(mountHit, mountPort!.Step(mount.Reaction, dt, out _));
            // The gearbox housing, rung by the mesh, the harder the more it carries.
            gearbox!.Push(gearShape, MeshK * te * turning * (0.5 + Math.Abs(tOut) / 2));
            // The brushes: a knock as each bar passes under them; the can pulled out of round by the ripple.
            double surface = Math.Abs(w) * CommutatorR;
            double bar = Math.Floor(theta * Bars / (2 * Math.PI));
            if (bar != lastBar && surface > 0.05)
            {
                lastBar = bar;
                brushLeft = BrushLanding;
                brushPeak = BrushKg * surface * ch.BarStepM / BarGap * (0.7 + 0.6 * rng.NextDouble()) * Math.PI / (2 * BrushLanding);
            }
            double brush = 0;
            if (brushLeft > 0) { brush = brushPeak * Math.Sin(Math.PI * (1 - brushLeft / BrushLanding)); brushLeft -= dt; }
            brush += Ripple * torque / CanR * (Math.Sin(Bars * theta) + 0.5 * Math.Sin(2 * Bars * theta + 0.7));
            can!.Push(canShape, brush);

            // The glass: the clamp's pull wavering with the mesh, the sweeps' lips, the flock, the seal.
            glassField!.Modes.Push(carrierHit, carrierPort!.Step(CarrierEccentricity * carrierHigh!.Next(cable), dt, out _));
            for (int i = 0; i < Lips; i++)
            {
                double f = LipKg * lipAcc[i];               // what the lip's spring and the glass trade
                glassField.Modes.Push(lipHit[i], lipPort[i].Step(LipCoupling * lipHigh[i]!.Next(f), dt, out _));
            }
            double white = rng.NextDouble() * 2 - 1;
            double a1 = 1 - Math.Exp(-2 * Math.PI * 4000 * dt);
            flock += a1 * (white - flock);
            double moving = Math.Tanh(Math.Abs(v) / 0.02);
            glassField.Modes.Push(flockHit, friction * moving * 0.3 / Math.Sqrt(FlockFibres) * flock / Math.Sqrt(a1 / (2 - a1)));
            // The ends. The header channel is the top of the door's steel frame and the stop is on the
            // regulator's rail, bolted to the inner panel at its flange. Both are where the door is stiff:
            // most of a blow moves the whole 20 kg door on its hinges and latch, and only the share that
            // reaches the open sheet bends it (fed the whole blow, the bottom stop came out at 97 dB).
            // The glass edge seating in the channel rocks the pane in its channels a little as well.
            glassField.Modes.Push(sealHit, sealPort!.Step(0.3 * seal, dt, out _));
            doorField.Modes.Push(sealFrameHit, FrameShare * seal);
            doorField.Modes.Push(stopHit, FrameShare * stop);

            // ── Radiate ──
            // The glass straight into the cabin; the door's panel, the can and the gearbox through the
            // trim card.
            double pGlass = glassField.Modes.Step();
            double pDoor = doorField.Modes.Step();
            double pMotor = can.Step() + gearbox.Step();
            trimLow += (1 - Math.Exp(-2 * Math.PI * TrimHz * dt)) * (pDoor + pMotor - trimLow);
            double p = pGlass + TrimLeak * trimLow;
            if (recording)
            {
                peakGlass = Math.Max(peakGlass, Math.Abs(pGlass));
                peakDoor = Math.Max(peakDoor, Math.Abs(TrimLeak * pDoor));
                peakMotor = Math.Max(peakMotor, Math.Abs(TrimLeak * pMotor));
                if (!double.IsFinite(p)) p = 0;
                outHi.Add((float)p);
            }
            time += dt;
        }

        public float[] Output()
        {
            static string Db(double pa) => $"{20 * Math.Log10(Math.Max(1e-9, pa) / 2e-5):F0}";
            Log($"peaks by part, dB SPL at 1 m: glass {Db(peakGlass)} door {Db(peakDoor)} motor {Db(peakMotor)}");
            var y = Decimate(outHi, rate, PascalsAtFullScale, out double peak, Over);
            if (report != null) report.PeakPascals = peak;
            return y;
        }
    }
}
