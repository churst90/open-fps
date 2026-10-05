using System;
using System.Collections.Generic;
using System.Text;
using static OpenFPS.Common.DoorPhysics;

namespace OpenFPS.Common;

/// <summary>
/// One leaf of a lift's centre-opening landing doors, simulated as the object, the way <see cref="SlidingDoor"/>
/// is: a stainless panel hung on two rollers from a steel track, guided at its foot by nylon shoes in the
/// sill's groove, run by the car's door operator through its coupler, and meeting its partner at a rubber
/// astragal.
///
/// The parts:
///
///   The PANEL: 0.55 by 2.1 m, a 1.2 mm stainless face on pressed steel stiffeners with sound-deadening on its
///   back, about 25 kg. Its low modes are the stiffened panel's; above them its face rings at a thin sheet's
///   density, lightly damped where the deadening does not reach: the long steel ring after a lift door shuts.
///
///   The HANGER: two 65 mm polyurethane-tyred rollers on a bracket, rolling on a steel track under the header.
///   What a roller rolls over is the track's roughness and its own out-of-round, through a Hertz contact under
///   half the panel's weight, and the panel bounces on them. The track and the header round it ring as a
///   steel box's walls.
///
///   The SHOES: two nylon guide shoes sliding in the aluminium sill's groove: a drag and the scuff of their
///   asperities catching and letting go, into the sill.
///
///   The OPERATOR (on the car, heard through the gap; one leaf of a pair carries it): a VVVF motor and a
///   toothed belt, following its controller's speed profile: an S-curve, a run, a check speed into each end
///   and a slow creep before the leaves meet (closing energy is held under 10 J). The motor's torque ripple at
///   its rotor slots rings its cast housing; the belt's teeth seat on the pulley.
///
///   The COUPLER and LOCK: opening, the car's vane closes on the landing door's rubber-tyred rollers (the clunk
///   before a lift door moves) and lifts the landing lock's hook off its keeper; shut, the hook drops back.
///
///   The ASTRAGAL: a hollow rubber section on the leading edge. Two leaves meeting are each a leaf meeting the
///   middle plane, through two astragals in series. The open end has a rubber bumper.
/// </summary>
public static class ElevatorDoor
{
    public sealed class Door
    {
        public float Width = 0.55f, Height = 2.1f;
        public int Variant;
        public int Seed = 1;
        /// <summary>This leaf's render carries the operator's motor and belt: one leaf of a pair does.</summary>
        public bool Operator = true;
    }

    public const double PascalsAtFullScale = 20.0;
    public const int Variants = 4;
    public static string? StemFolder;

    public sealed class Report
    {
        public readonly List<string> Events = new();
        public double PeakPascals;
        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var e in Events) sb.AppendLine("    " + e);
            sb.Append($"    peak {20 * Math.Log10(Math.Max(1e-9, PeakPascals) / 2e-5):F1} dB SPL at 1 m");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Each character: the track's and the tyres' roughness (RMS, um), a flat on the tyres (um), the shoes'
    /// drag (N), and the astragal's rubber (new and soft, or hardened with age, as a Hertz stiffness).
    /// </summary>
    private static (double RailMicron, double WheelMicron, double FlatMicron, double ShoeDrag, double AstragalK) Character(int variant)
        => (((variant % Variants) + Variants) % Variants) switch
        {
            0 => (0.5, 2, 5, 3, 1.2e6),
            1 => (1.0, 4, 15, 4, 1.8e6),
            2 => (2.0, 6, 40, 6, 2.5e6),
            _ => (3.0, 8, 80, 8, 4e6),
        };

    public static float[] RenderOpen(Door door, int sampleRate, double travelSeconds = 1.8, Report? report = null)
    {
        var sim = new Sim(door, sampleRate, report);
        sim.Run(opening: true, travelSeconds);
        return sim.Output();
    }

    public static float[] RenderClose(Door door, int sampleRate, double travelSeconds = 2.5, Report? report = null)
    {
        var sim = new Sim(door, sampleRate, report);
        sim.Run(opening: false, travelSeconds);
        return sim.Output();
    }

    /// <summary>How long a run's sound lasts: its travel and what follows it.</summary>
    public static float Seconds(bool closing, float travelSeconds) => travelSeconds + (closing ? 1.4f : 0.6f) + 0.25f;

    // ── The game ─────────────────────────────────────────────────────────────────────────────────

    public const string KeyPrefix = "elevatordoor:";

    /// <summary>Declared levels, dB at a metre: the render's peak (the client puts each render's own in its
    /// place). Measured with AudioLab --door-models, 2026-10-05.</summary>
    public static float OpenLevelDb(int variant) => 95f;
    public static float CloseLevelDb(int variant) => 105f;

    public static string Key(bool closing, int variant, float travelSeconds, float width, float height, bool drivesOperator)
        => FormattableString.Invariant(
            $"{KeyPrefix}{(closing ? "close" : "open")}:{((variant % Variants) + Variants) % Variants}:{(int)MathF.Round(travelSeconds * 100f)}:{(int)MathF.Round(width * 100f)}:{(int)MathF.Round(height * 100f)}:{(drivesOperator ? "op" : "leaf")}");

    public static bool TryParseKey(string? key, out bool closing, out Door door, out float travelSeconds)
    {
        closing = false; door = new Door(); travelSeconds = 1.8f;
        if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
        var p = key.Substring(KeyPrefix.Length).Split(':');
        if (p.Length != 6 || (p[0] != "open" && p[0] != "close") || (p[5] != "op" && p[5] != "leaf")) return false;
        if (!int.TryParse(p[1], out int v) || !int.TryParse(p[2], out int s) || !int.TryParse(p[3], out int w)
            || !int.TryParse(p[4], out int h)) return false;
        closing = p[0] == "close";
        travelSeconds = Math.Clamp(s / 100f, 0.8f, 8f);
        door = new Door { Variant = v, Seed = 1 + v, Width = Math.Clamp(w / 100f, 0.3f, 1.2f), Height = Math.Clamp(h / 100f, 1.8f, 3f), Operator = p[5] == "op" };
        return true;
    }

    public static float[] RenderKey(string key, int sampleRate) => RenderKey(key, sampleRate, out _);

    public static float[] RenderKey(string key, int sampleRate, out float fullScaleDb)
    {
        fullScaleDb = 0f;
        if (!TryParseKey(key, out bool closing, out var door, out float travel)) return new float[16];
        float[] pcm = closing ? RenderClose(door, sampleRate, travel) : RenderOpen(door, sampleRate, travel);
        return KnobDoor.PeakToFullScale(pcm, PascalsAtFullScale, out fullScaleDb);
    }

    // ── Constants ─────────────────────────────────────────────────────────────────────────────────

    private const double G = 9.81, SteelE = 200e9, SteelRho = 7900;
    /// <summary>The panel: a 1.2 mm face, pressed stiffeners 25 mm deep, deadening; 25 kg on a 0.55 m leaf.</summary>
    private const double FaceT = 0.0012, StiffenerDepth = 0.025, PanelKgPerM2 = 21.5;
    /// <summary>The stiffened panel's low modes lose about 0.03 to the deadening; the face between the
    /// stiffeners rings at a bare sheet's loss and a little more where the deadening reaches.</summary>
    private const double PanelLoss = 0.03, FaceLoss = 0.0015, PanelModeMaxHz = 1200;
    /// <summary>Hanger rollers: 65 mm polyurethane tyres (Hertz, under 120 N each), 0.4 kg with their
    /// bracket's free end, on a bracket spring; 60 mm in from each edge.</summary>
    private const double WheelR = 0.0325, WheelKg = 0.4, TyreK = 1.2e7, TyreLambda = 2.0, BracketK = 5e6, BracketZeta = 0.1, WheelInset = 0.06;
    /// <summary>The track and header: 3 mm steel, about 0.3 m round, 2.4 m long, bolted to the landing wall.</summary>
    private const double HeaderT = 0.003, HeaderGirth = 0.3, HeaderLength = 2.4, HeaderLoss = 0.02;
    /// <summary>The sill: an aluminium extrusion 3 mm, 0.15 m across, bedded in the floor.</summary>
    private const double SillT = 0.003, SillGirth = 0.15, SillLoss = 0.03;
    /// <summary>A shoe's nylon on the sill's groove: its asperities catch and let go every 20 um of travel,
    /// about 20 000 of them in contact.</summary>
    private const double ShoeSlip = 2e-5, ShoeAsperities = 2e4;
    /// <summary>Controller: opening at up to 0.5 m/s, closing at 0.3 (10 J for 50 kg of doors), 0.6 m/s2 and an
    /// S-curve; a check speed of 0.05 m/s for the last 40 mm opening, and a creep of 0.06 m/s for the last 50
    /// mm closing.</summary>
    private const double OpenRun = 0.5, CloseRun = 0.3, Accel = 0.6, Jerk = 3, Check = 0.05, CheckZone = 0.04, Creep = 0.06, CreepZone = 0.05;
    /// <summary>Belt: 5 mm pitch, about 2e5 N/m between pulley and hanger, 0.3 of critical.</summary>
    private const double BeltPitch = 0.005, BeltK = 2e5, BeltZeta = 0.3;
    /// <summary>The motor: 4 poles, 36 rotor slots, about 1400 rpm at the run's 0.5 m/s; a 2 % torque ripple.
    /// Its cast housing's walls from about 1.1 kHz, lossy (0.06).</summary>
    private const double MotorRevPerMetre = 1400.0 / 60 / 0.5, RotorSlots = 36, Ripple = 0.02;
    /// <summary>Astragal: two hollow rubber sections in series, 2 m long, about 100 N a millimetre between them
    /// at 2 mm (Hertz form by character), 4 mm proud; squashed flat, solid rubber. The open bumper rubber.</summary>
    private const double AstragalLambda = 1.5, AstragalProud = 0.004, FlatK = 5e8, BumperK = 2e6, BumperLambda = 1.2;
    /// <summary>Upthrust rollers under the track, 0.5 mm clear of it: a leaf rocked on its hangers lifts a roller
    /// into one, polyurethane on steel.</summary>
    private const double Upthrust = 0.0005;
    /// <summary>The coupler's vane closing on the landing rollers: 0.25 kg of vane at 0.15 m/s onto rubber
    /// tyres; the landing lock's hook, 40 g, lifted 6 mm and dropped back onto its steel keeper.</summary>
    private const double VaneKg = 0.25, VaneSpeed = 0.15, RollerK = 3e7, RollerLambda = 1.0, HookKg = 0.04, HookDrop = 0.006;
    private const double HookK = 4e9, HookLambda = 0.3, HookLower = 0.1;

    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Sim
    {
        private readonly Door door;
        private readonly Report? report;
        private readonly int rate;
        private readonly double dt;
        private readonly Random rng;
        private readonly (double Rail, double Wheel, double Flat, double ShoeDrag, double AstragalK) ch;
        private readonly double mass, width, height;
        private double time;

        // Motion along the track and the controller.
        private double x, v, motorX, motorV, motorA;
        // Each roller: its vertical place and speed on its bracket; the panel's heave.
        private readonly double[] wz = new double[2], wv = new double[2], wArm = new double[2];
        private double heave, heaveRate;
        private readonly double[] railRough, wheelRough0, wheelRough1;
        private readonly double railStep, wheelStep;

        // Bodies.
        private readonly Modes panel, motorHousing;
        private readonly DenseField faceField, headerField, sillField;
        private readonly double[][] panelAtWheel = new double[2][], faceAtWheel = new double[2][];
        private readonly double[] panelAtEdge, panelAtFoot, faceAtEdge, faceAtFoot, headerHit0, headerHit1, sillHit, pulleyHit, housingShape;
        private readonly Port[] trackPort = new Port[2];
        private readonly Port edgePort, sillPort;
        private double shoeNoise, shoeLow;
        private readonly AccelerationNoise hookNoise;
        private double hook, hookRate, hookApproach;
        private readonly Port keeperPort, pulleyPort;
        private readonly double[] keeperHit;
        private bool hookFalling;
        private double vaneX, vaneV;
        private bool vaneOn;
        private double toothPhase;

        private readonly List<float> outHi = new();
        private readonly Dictionary<string, (double Start, double Peak, bool On)> contactLog = new();
        private static readonly string[] PeakNames = { "panel", "header", "sill", "operator", "lock" };
        private readonly double[] peaks = new double[PeakNames.Length];
        private List<float>[]? stems;

        public Sim(Door door, int sampleRate, Report? report)
        {
            this.door = door; this.report = report;
            rate = sampleRate * Oversample; dt = 1.0 / rate;
            rng = new Random(door.Seed);
            ch = Character(door.Variant);
            width = door.Width; height = door.Height;
            mass = PanelKgPerM2 * width * height;

            double d = 2 * SteelE * FaceT * (StiffenerDepth / 2) * (StiffenerDepth / 2) / (1 - Poisson * Poisson) * 0.5;
            var plate = new Plate(width, height, d, mass / (width * height), PanelLoss, PanelModeMaxHz, false, rng, 0.03);
            panel = new Modes(plate.Hz, plate.Loss, plate.Mass, plate.Gain, dt, plate.GainQuad);
            faceField = new DenseField(width, height, FaceT, SteelE, SteelRho, Poisson, f => ThinPanelLoss(f) * 0.5 + FaceLoss, PanelModeMaxHz, 16000, rng, dt);
            headerField = new DenseField(HeaderGirth, HeaderLength, HeaderT, SteelE, SteelRho, Poisson, f => ThinPanelLoss(f) + HeaderLoss, 200, 16000, rng, dt, DenseField.CapSpacing, 0.1);
            sillField = new DenseField(SillGirth, 2 * width, SillT, 70e9, 2700, Poisson, f => ThinPanelLoss(f) + SillLoss, 300, 16000, rng, dt);
            for (int i = 0; i < 2; i++)
            {
                wArm[i] = i == 0 ? WheelInset : width - WheelInset;
                panelAtWheel[i] = plate.Shape(wArm[i], height - 0.02);
                faceAtWheel[i] = faceField.Point();
                trackPort[i] = new Port(headerField.PatchMass, 2e7, headerField.Impedance);
                wz[i] = 0;
            }
            heave = -mass * G / (2 * BracketK);
            panelAtEdge = plate.Shape(width - 0.01, height / 2);
            panelAtFoot = plate.Shape(width / 2, 0.02);
            faceAtEdge = faceField.Point(); faceAtFoot = faceField.Point();
            headerHit0 = headerField.Point(); headerHit1 = headerField.Point(); sillHit = sillField.Point(); pulleyHit = headerField.Point();
            edgePort = new Port(faceField.PatchMass, 2e7, faceField.Impedance);
            sillPort = new Port(sillField.PatchMass, 2e7, sillField.Impedance);
            hookNoise = new AccelerationNoise(HookKg / 7850, dt);
            keeperPort = new Port(headerField.PatchMass, 2e7, headerField.Impedance);
            pulleyPort = new Port(headerField.PatchMass, 2e7, headerField.Impedance);
            keeperHit = headerField.Point();

            // The motor's housing: a cast box's walls, from 1.1 kHz.
            var hz = new List<double>(); var l = new List<double>(); var m = new List<double>(); var g = new List<double>();
            for (int i = 0; i < 14; i++)
            {
                hz.Add(1100 * Math.Pow(7, i / 13.0) * (0.95 + 0.1 * rng.NextDouble()));
                l.Add(0.06); m.Add(0.1); g.Add(SmallPlateGain(0.02, 0.3) * (rng.NextDouble() < 0.5 ? -1 : 1));
            }
            motorHousing = new Modes(hz, l, m, g, dt);
            housingShape = new double[hz.Count];
            for (int i = 0; i < housingShape.Length; i++) housingShape[i] = (rng.NextDouble() * 2 - 1) * Math.Sqrt(3);

            // The track's roughness along it, and each tyre's round it, with a flat.
            double trackLen = HeaderLength;
            railRough = SurfaceProfile(rng, trackLen, 0.004);
            for (int i = 0; i < railRough.Length; i++) railRough[i] *= ch.Rail * 1e-6;
            railStep = trackLen / railRough.Length;
            double circ = 2 * Math.PI * WheelR;
            wheelRough0 = Tyre(circ); wheelRough1 = Tyre(circ);
            wheelStep = circ / wheelRough0.Length;
        }

        private double[] Tyre(double circ)
        {
            var w = SurfaceProfile(rng, circ, 0.004);
            double flat = ch.Flat * 1e-6 * (0.6 + 0.8 * rng.NextDouble()), at = rng.NextDouble() * circ, half = Math.Sqrt(2 * WheelR * Math.Max(flat, 1e-12));
            for (int k = 0; k < w.Length; k++)
            {
                double s = k * circ / w.Length, dd = Math.Abs(s - at);
                dd = Math.Min(dd, circ - dd);
                w[k] = w[k] * ch.Wheel * 1e-6 - (dd < half ? flat - dd * dd / (2 * WheelR) : 0);
            }
            return w;
        }

        private static double Lookup(double[] a, double step, double s)
        {
            double f = s / step; int k = (int)Math.Floor(f); double t = f - k;
            int n = a.Length; k = ((k % n) + n) % n;
            return a[k] * (1 - t) + a[(k + 1) % n] * t;
        }

        // ── The run ──────────────────────────────────────────────────────────────────────────────

        /// <summary>A whole run: opening, the coupler and the lock first; closing, the meeting and the lock after.
        /// The controller's run speed is set so the leaf takes the server's <paramref name="travelSeconds"/>.</summary>
        public void Run(bool opening, double travelSeconds)
        {
            double travel = width - 0.01;
            // x is the leading edge's distance from the middle plane: 0 shut, travel open.
            x = opening ? 0.0 : travel; motorX = x; v = 0;
            // The run speed that makes the profile take travelSeconds, by bisection on the profile itself.
            double lo = 0.05, hi = opening ? OpenRun * 2 : CloseRun * 2;
            for (int it = 0; it < 30; it++)
            {
                double mid = 0.5 * (lo + hi);
                if (ProfileSeconds(opening, travel, mid) > travelSeconds) lo = mid; else hi = mid;
            }
            double vRun = Math.Min(hi, opening ? OpenRun : CloseRun);
            Log($"run speed {vRun:F2} m/s for {travelSeconds:F2} s ({ProfileSeconds(opening, travel, vRun):F2} s)");
            double start = 0, end = 10;
            if (opening)
            {
                // The vane closes on the landing door's rollers, and lifts the hook.
                vaneOn = true; vaneX = -0.003; vaneV = VaneSpeed;
                hook = 0;
                while (time < 0.18) Tick(opening, 0);
                start = time;
            }
            double profU = 0, profA = 0; bool arrived = false; double arrivedAt = -1;
            while (time < end)
            {
                if (!arrived)
                {
                    double to = opening ? travel : 0, dir = opening ? 1 : -1;
                    double left = Math.Abs(to - motorX);
                    double want = opening ? (left < CheckZone ? Check : vRun) : (left < CreepZone ? Creep : vRun);
                    // Braking ahead of the zone: the speed whose stop at Accel lands at the zone's edge.
                    double zone = opening ? CheckZone : CreepZone, slow = opening ? Check : Creep;
                    double brake = Math.Sqrt(slow * slow + 2 * Accel * Math.Max(0, left - zone));
                    want = Math.Min(want, brake);
                    double aWant = Math.Clamp((want - profU) / 0.05, -Accel, Accel);
                    profA += Math.Clamp(aWant - profA, -Jerk * dt, Jerk * dt);
                    profU = Math.Max(0, profU + profA * dt);
                    motorV = dir * profU; motorA = dir * profA;
                    motorX += motorV * dt;
                    if ((opening && motorX >= to - 0.002) || (!opening && motorX <= to + 0.001))
                    {
                        arrived = true; arrivedAt = time; motorV = 0; motorA = 0;
                        Log($"{time * 1000:F0} ms  the operator at its end ({(time - start):F2} s of travel), leaf edge {x * 1000:F1} mm, {v:F3} m/s");
                        end = time + (opening ? 0.6 : 1.4);
                        // Shut: the operator leans on the leaves with about 60 N through its belt, and a moment later
                        // the vane opens and the landing lock's hook falls into its keeper.
                        if (!opening) { motorX = -60 / BeltK; hookAt = time + 0.3; }
                    }
                }
                if (hookAt > 0 && time >= hookAt) { hookAt = -1; hookFalling = true; hook = HookDrop; Log($"{time * 1000:F0} ms  the hook falls"); }
                Tick(opening, motorV);
            }
        }
        private double hookAt = -1;

        private static double ProfileSeconds(bool opening, double travel, double vRun)
        {
            double x = 0, u = 0, a = 0, t = 0, step = 1e-3;
            double zone = opening ? CheckZone : CreepZone, slow = opening ? Check : Creep;
            while (t < 30)
            {
                double left = travel - x;
                if (left <= 0.003) break;
                double want = left < zone ? slow : vRun;
                want = Math.Min(want, Math.Sqrt(slow * slow + 2 * Accel * Math.Max(0, left - zone)));
                double aWant = Math.Clamp((want - u) / 0.05, -Accel, Accel);
                a += Math.Clamp(aWant - a, -Jerk * step, Jerk * step);
                u = Math.Max(0, u + a * step); x += u * step; t += step;
            }
            return t;
        }

        // ── One step ─────────────────────────────────────────────────────────────────────────────

        private void Tick(bool opening, double motorSpeed)
        {
            double fx = 0, pLock = 0, pOp = 0;

            // The belt from the operator's pulley to the hanger.
            double belt = BeltK * (motorX - x) + 2 * BeltZeta * Math.Sqrt(BeltK * mass) * (motorV - v);
            fx += belt;

            // Rolling resistance and the shoes' drag.
            double moving = Math.Tanh(v / 0.005);
            fx -= (0.01 * mass * G + ch.ShoeDrag) * moving;

            // The shoes in the sill: their asperities' catches as a flutter of the drag, into the sill.
            double slipHz = Math.Min(20000, Math.Abs(v) / ShoeSlip);
            double a1 = 1 - Math.Exp(-2 * Math.PI * Math.Max(slipHz, 1) * dt);
            shoeNoise += a1 * ((rng.NextDouble() * 2 - 1) * Math.Sqrt(3) - shoeNoise);
            double flutter = ch.ShoeDrag * Math.Abs(moving) * 0.3 / Math.Sqrt(ShoeAsperities) * shoeNoise / Math.Sqrt(a1 / (2 - a1));
            shoeLow += a1 * (flutter - shoeLow);
            flutter -= shoeLow;
            sillField.Modes.Push(sillHit, sillPort.Step(flutter, dt, out _));
            panel.Push(panelAtFoot, -flutter * 0.3);

            // The astragal at the middle (shut) and the bumper at the open end.
            double fA = Contact(ch.AstragalK, AstragalLambda, AstragalProud - x, -v) + Contact(FlatK, AstragalLambda, -x, -v);
            double fB = Contact(BumperK, BumperLambda, x - (width + 0.001), v);
            fx += fA - fB;
            Note("astragal", fA); Note("open-bumper", fB);
            double edgeDrive = edgePort.Step(fA, dt, out double edgeHost);
            faceField.Modes.Push(faceAtEdge, edgeDrive);
            panel.Push(panelAtEdge, edgeHost * 0.2 + fB * 0.1);

            // A blow along the track at the leaf's edge (the astragal, the bumper) is at mid-height, a metre under the
            // hangers: it rocks the leaf on its two rollers, loading one and lifting the other.
            double rock = (fA - fB) * (height / 2) / (wArm[1] - wArm[0]);
            // The rollers on the track: Hertz under the panel's weight, over the track's and the tyre's roughness.
            double heaveForce = -mass * G;
            for (int i = 0; i < 2; i++)
            {
                double s = x + wArm[i] + 0.5;
                double rough = Lookup(railRough, railStep, s) + Lookup(i == 0 ? wheelRough0 : wheelRough1, wheelStep, x);
                double sink0 = Math.Pow(mass * G / 2 / TyreK, 2.0 / 3);
                // z up: the tyre is pressed into the track by what it carries; the bracket pulls the wheel towards
                // the panel hanging under it.
                double depth = sink0 + rough + trackPort[i].X - wz[i];
                double rateD = trackPort[i].V - wv[i] + (Lookup(railRough, railStep, s + v * dt) - Lookup(railRough, railStep, s)) / dt;
                double fc = Contact(TyreK, TyreLambda, depth, rateD);
                // Lifted, the wheel's top meets its upthrust roller.
                double up = Contact(TyreK, TyreLambda, -depth - Upthrust, -rateD);
                fc -= up;
                Note("upthrust", up);
                double bracket = BracketK * (heave - wz[i]) + 2 * BracketZeta * Math.Sqrt(BracketK * WheelKg) * (heaveRate - wv[i]);
                double share = i == 0 ? rock : -rock;
                double wa = (bracket + fc - share) / WheelKg - G;
                wv[i] += wa * dt; wz[i] += wv[i] * dt;
                headerField.Modes.Push(i == 0 ? headerHit0 : headerHit1, trackPort[i].Step(-fc + WheelKg * G + mass * G / 2, dt, out _));
                heaveForce -= bracket;
                double fluct = bracket + mass * G / 2;
                // The hanger stands 30 mm off the panel's face: a tenth of what it carries bends the panel.
                panel.Push(panelAtWheel[i], fluct + 0.1 * share);
                faceField.Modes.Push(faceAtWheel[i], fluct * 0.1);
                Note("tyre-off", fc <= 0 ? 1 : 0);
            }
            heaveRate += heaveForce / mass * dt;
            heave += heaveRate * dt;

            // The coupler's vane closing on the landing rollers, and the hook.
            if (vaneOn)
            {
                // It comes in at its own speed, and once on the rollers its mechanism presses with about 20 N.
                double fv = Contact(RollerK, RollerLambda, vaneX, vaneV);
                vaneV += ((vaneX > 0 ? 20 : 0) - fv) / VaneKg * dt;
                vaneX += vaneV * dt;
                panel.Push(panelAtWheel[1], fv);
                headerField.Modes.Push(keeperHit, keeperPort.Step(fv * 0.2, dt, out _));
                Note("vane", fv);
                if (vaneX > 0.0005 && hook == 0) { hook = 1e-9; hookRate = 0.05; Log($"{time * 1000:F0} ms  the hook lifts"); }
            }
            if (hook > 0 && !hookFalling)
            {
                hookRate = Math.Max(0, hookRate - 0.5 * dt); hook = Math.Min(HookDrop, hook + hookRate * dt);
            }
            if (hookFalling)
            {
                // The hook comes down on the coupler's roller as the vane opens, no faster than the vane lets it
                // (about 0.1 m/s), and lands on its keeper.
                double fk = ContactRestitution(HookK, 0.35, -hook, -hookRate, ref hookApproach);
                double ha = -G * 3 + fk / HookKg;     // a spring helps gravity
                hookRate += ha * dt;
                if (hook > 0 && hookRate < -HookLower) hookRate = -HookLower;
                hook += hookRate * dt;
                headerField.Modes.Push(keeperHit, keeperPort.Step(fk, dt, out _));
                pLock += hookNoise.Pressure(ha);
                Note("hook-drop", fk);
            }

            // The panel along the track.
            double ax = fx / mass;
            v += ax * dt; x += v * dt;

            // The operator: the motor's torque ripple at its slot rate, and the belt's teeth on the pulley.
            if (door.Operator)
            {
                double rev = Math.Abs(motorV) * MotorRevPerMetre;
                motorPhase += 2 * Math.PI * rev * RotorSlots * dt;
                double torqueForce = Math.Abs(belt) + 30 * Math.Abs(motorV);
                motorHousing.Push(housingShape, Ripple * torqueForce * Math.Sin(motorPhase) * (1 + 0.3 * Math.Sin(motorPhase / RotorSlots * 2)));
                toothPhase += Math.Abs(motorV) / BeltPitch * dt;
                if (toothPhase >= 1)
                {
                    toothPhase -= 1;
                    toothLeft = 1.5e-4; toothForce = 0.05 * Math.Abs(belt) * (0.7 + 0.6 * rng.NextDouble()) + 0.5 * Math.Abs(motorV);
                }
                if (toothLeft > 0)
                {
                    double f = toothForce * Math.Sin(Math.PI * (1 - toothLeft / 1.5e-4));
                    headerField.Modes.Push(pulleyHit, pulleyPort.Step(f, dt, out _));
                    toothLeft -= dt;
                }
                pOp = motorHousing.Step();
            }

            double pPanel = panel.Step() + faceField.Modes.Step();
            double pHeader = headerField.Modes.Step();
            double pSill = sillField.Modes.Step();
            double p = pPanel + pHeader + pSill + pOp + pLock;
            double[] parts = { pPanel, pHeader, pSill, pOp, pLock };
            for (int i = 0; i < parts.Length; i++) peaks[i] = Math.Max(peaks[i], Math.Abs(parts[i]));
            if (StemFolder != null)
            {
                stems ??= new List<float>[parts.Length];
                for (int i = 0; i < parts.Length; i++) (stems[i] ??= new List<float>()).Add((float)(parts[i] / PascalsAtFullScale));
            }
            outHi.Add((float)p);
            time += dt;
        }

        private double motorPhase, toothLeft, toothForce;

        private void Log(string s) => report?.Events.Add(s);

        private void Note(string name, double force)
        {
            contactLog.TryGetValue(name, out var c);
            if (force > 0)
            {
                if (!c.On) c = (time, force, true); else c.Peak = Math.Max(c.Peak, force);
                contactLog[name] = c;
            }
            else if (c.On)
            {
                Log($"{c.Start * 1000:F1} ms  {name}: peak {c.Peak:F1} N, {(time - c.Start) * 1e6:F0} us");
                contactLog[name] = (c.Start, c.Peak, false);
            }
        }

        public float[] Output()
        {
            var sb = new StringBuilder("peaks by part, dB SPL at 1 m:");
            for (int i = 0; i < peaks.Length; i++) sb.Append($" {PeakNames[i]} {20 * Math.Log10(Math.Max(1e-9, peaks[i]) / 2e-5):F0}");
            Log(sb.ToString());
            Log($"modes: panel {panel.N} ({panel.Hz[0]:F0}-{panel.Hz[panel.N - 1]:F0} Hz), face {faceField.Modes.N}, header {headerField.Modes.N}, sill {sillField.Modes.N}; panel {mass:F0} kg");
            if (StemFolder != null && stems != null)
                for (int i = 0; i < stems.Length; i++)
                    using (var f = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(StemFolder, "lift-" + PeakNames[i] + ".raw"))))
                        foreach (var v in stems[i]) f.Write(v);
            var y = Decimate(outHi, rate, PascalsAtFullScale, out double peak);
            if (report != null) report.PeakPascals = peak;
            return y;
        }
    }
}
