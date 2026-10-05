using System;
using System.Collections.Generic;
using System.Text;
using static OpenFPS.Common.DoorPhysics;

namespace OpenFPS.Common;

/// <summary>
/// A glass storefront or building entrance door, simulated as the object, the way <see cref="KnobDoor"/> and
/// <see cref="PushBarDoor"/> are: the city's apartment towers' front doors (glass-pushbar: a key outside, a
/// push bar inside) and the shops' pull doors (glass-pull: a pull handle each side, no latch).
///
/// The parts:
///
///   The LEAF: a medium-stile aluminium door (stiles and top rail 89 mm by 44.5 mm, bottom rail 254 mm,
///   extruded 6063 with 3.2 mm walls), about 15 kg of frame round a 12 mm toughened pane (or a 6+6 laminated
///   one) in EPDM glazing gaskets. The frame bends as a hinged plate with the stiles' and rails' stiffness and
///   the whole door's mass; the pane is a second plate, simply supported in its gaskets, riding on the frame:
///   when the frame is jolted the pane is thrown about in its own modes from 50 Hz up. Those are the "thunk"
///   of a glass door. The frame's tube walls ring at a thin aluminium plate's density: its clank.
///
///   The FRAME the leaf shuts into: an aluminium storefront frame (44.5 by 114 mm tubes, 3.2 mm walls)
///   anchored in the opening, with a weatherstrip along the stop on the lock jamb and the head: an EPDM
///   compression bulb (fresh, or taken a set), or a polypropylene pile, or on an old door none left. The leaf
///   lands on the seal; squashed flat, a bulb is still a rubber pad, a pile a mat of fibres; with none the
///   stile meets the stop leg aluminium on aluminium.
///
///   The LATCH (glass-pushbar only): a narrow-stile deadlatch (Adams Rite 4900 type), a 20 g bolt thrown
///   12.7 mm by its spring, with a 45 degree bevel, into a stainless strike in the jamb. The key outside or
///   the bar inside draws it in.
///
///   The PUSH BAR (inside, glass-pushbar): a narrow-stile rim exit device's touchbar, an extruded aluminium
///   case on the stiles with a steel mechanism and a plastic pad, as on the steel push-bar door.
///
///   The PULL HANDLE: a 25 mm stainless tube on two posts 305 mm apart, bolted through the lock stile with
///   a little play. Gripped, the hand damps it; let go, it rings at its clamped-tube modes (1.6, 4.5, 8.8
///   kHz).
///
///   The CLOSER: a hydraulic overhead closer, size 4: a spring through a rack and pinion, oil through a sweep
///   valve and a latch valve, a check valve that lets it open easily and a backcheck past 70 degrees.
///
///   The SWEEP: a brush sweep on the bottom rail that wipes the threshold for the first few centimetres of
///   travel: a soft hiss from its fibres catching and letting go, the only noise in a pull door.
///
/// Offset pivots, ball-bearing: they are silent.
///
/// Push and pull: a door opens away from the side its stop is on. From that side it is pushed (the bar, or a
/// hand shoving the push side's handle); from the other it is pulled (a hand gripping the pull handle and
/// drawing the leaf). From the keyed side of a front door the key has drawn the latch first (see
/// <see cref="LockCylinder"/>): the hand pulls on the handle with the key held turned, and lets the key go
/// once the leaf is clear, and the latch springs back out against its stop.
/// </summary>
public static class GlassDoor
{
    public enum Kind
    {
        /// <summary>A building's front door: a key cylinder and pull handle outside, a push bar inside, a latch.</summary>
        PushBar,
        /// <summary>A shop's door: a pull handle on each side, no latch; the closer holds it shut.</summary>
        Pull,
    }

    public enum Glazing { Tempered, Laminated }

    /// <summary>How a hand opens it.</summary>
    public enum Opening
    {
        /// <summary>From the stop side: the bar shoved (a front door), or the push side's handle shoved (a pull door).</summary>
        Push,
        /// <summary>From the swing side: the handle gripped and drawn. On a front door the latch is held back
        /// (dogged), as a shop's is by day.</summary>
        Pull,
        /// <summary>From the keyed side of a front door: the key has drawn the latch, the handle is pulled, and
        /// the key let go once the leaf is clear.</summary>
        Key,
    }

    public sealed class Door
    {
        public Kind Kind;
        public Glazing Glass;
        public float Width = 1.0f, Height = 2.1f;
        public int Variant;
        public int Seed = 1;
    }

    public const double PascalsAtFullScale = 20.0;

    /// <summary>The lab's instrument: when set, each part's pressure alone is written here as gd-PART.raw.</summary>
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

    public const int Variants = 4;

    private enum Seal { Bulb, Pile, None }

    /// <summary>
    /// Each character: the stop's seal and how far it stands proud of the aluminium stop leg, mm (a new EPDM
    /// bulb 3.5, one taken a compression set 2.5, a pile weatherstrip 3, none at all on an old door); how fast
    /// the closer's latch valve brings the latch edge in, m/s (a well-set closer 0.2, one opened up to slam
    /// 0.7); the pull handle's play on its through-bolts, mm (none on a tight one); the bar's crank play, mm;
    /// the sweep's drag, N.
    /// </summary>
    private static (Seal Seal, double SealMm, double LatchSpeed, double HandlePlayMm, double CrankPlayMm, double SweepDrag) Character(int variant)
        => (((variant % Variants) + Variants) % Variants) switch
        {
            0 => (Seal.Bulb, 3.5, 0.2, 0, 0.5, 4),
            1 => (Seal.Bulb, 2.5, 0.3, 0, 1.0, 5),
            2 => (Seal.Pile, 3.0, 0.45, 0.05, 1.5, 6),
            _ => (Seal.None, 0.0, 0.7, 0.25, 2.0, 3),
        };

    /// <summary>Opening: pushed, pulled, or pulled with the latch held back by a key, swung to about 85
    /// degrees over <paramref name="swingSeconds"/>.</summary>
    public static float[] RenderOpen(Door door, Opening how, int sampleRate, double swingSeconds = 1.1, Report? report = null)
    {
        var sim = new Sim(door, sampleRate, report);
        sim.StartShut(how);
        if (how == Opening.Push && door.Kind == Kind.PushBar) sim.ScriptBar(swingSeconds);
        else sim.ScriptHandle(how, swingSeconds);
        return sim.Output();
    }

    /// <summary>Shutting on the closer: the last few degrees at the latch valve's speed, onto the bulb and
    /// into the latch. It starts a few hundredths of a second before the first contact, so the server sends
    /// it when the leaf arrives.</summary>
    public static float[] RenderClose(Door door, int sampleRate, Report? report = null)
    {
        var sim = new Sim(door, sampleRate, report);
        sim.ScriptCloserLatch();
        return sim.Output();
    }

    // ── The game ─────────────────────────────────────────────────────────────────────────────────

    public const string KeyPrefix = "glassdoor:";

    /// <summary>Declared levels, dB at a metre: the render's peak, which is what its buffer's full scale stands
    /// for (see <see cref="KnobDoor.OpenLevelDb"/>); the client puts each render's own peak in its place.
    /// Measured at the prefab's 1.0 by 2.1 m leaf (AudioLab --door-models, 2026-10-05).</summary>
    public static float OpenLevelDb(Kind kind, Opening how) => (kind, how) switch
    {
        (Kind.PushBar, Opening.Push) => 118f,
        (Kind.PushBar, _) => 104f,
        _ => 96f,
    };
    public static float CloseLevelDb(Kind kind, int variant) => (kind, ((variant % Variants) + Variants) % Variants) switch
    {
        (Kind.PushBar, 0) => 112f, (Kind.PushBar, 1) => 116f, (Kind.PushBar, 2) => 120f, (Kind.PushBar, _) => 126f,
        (_, 0) => 104f, (_, 1) => 108f, (_, 2) => 112f, _ => 120f,
    };

    public static string Key(Kind kind, bool closing, Opening how, Glazing glass, int variant, float swingSeconds, float width, float height)
        => FormattableString.Invariant(
            $"{KeyPrefix}{(kind == Kind.PushBar ? "bar" : "pull")}:{(closing ? "close" : "open")}:{(closing ? "-" : how.ToString().ToLowerInvariant())}:{(glass == Glazing.Laminated ? "laminated" : "tempered")}:{((variant % Variants) + Variants) % Variants}:{(int)MathF.Round(swingSeconds * 100f)}:{(int)MathF.Round(width * 100f)}:{(int)MathF.Round(height * 100f)}");

    public static bool TryParseKey(string? key, out bool closing, out Opening how, out Door door, out float swingSeconds)
    {
        closing = false; how = Opening.Pull; door = new Door(); swingSeconds = 1.1f;
        if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
        var p = key.Substring(KeyPrefix.Length).Split(':');
        if (p.Length != 8 || (p[0] != "bar" && p[0] != "pull") || (p[1] != "open" && p[1] != "close")) return false;
        closing = p[1] == "close";
        if (!closing)
        {
            if (p[2] == "push") how = Opening.Push;
            else if (p[2] == "pull") how = Opening.Pull;
            else if (p[2] == "key") how = Opening.Key;
            else return false;
        }
        if (!int.TryParse(p[4], out int v) || !int.TryParse(p[5], out int s) || !int.TryParse(p[6], out int w)
            || !int.TryParse(p[7], out int h)) return false;
        swingSeconds = Math.Clamp(s / 100f, 0.3f, 5f);
        door = new Door
        {
            Kind = p[0] == "bar" ? Kind.PushBar : Kind.Pull,
            Glass = p[3] == "laminated" ? Glazing.Laminated : Glazing.Tempered,
            Variant = v, Seed = 1 + v,
            Width = Math.Clamp(w / 100f, 0.6f, 1.5f), Height = Math.Clamp(h / 100f, 1.8f, 3f),
        };
        return true;
    }

    public static float[] RenderKey(string key, int sampleRate) => RenderKey(key, sampleRate, out _);

    /// <summary>The sound a key names, peak one, and its own peak, dB SPL at a metre.</summary>
    public static float[] RenderKey(string key, int sampleRate, out float fullScaleDb)
    {
        fullScaleDb = 0f;
        if (!TryParseKey(key, out bool closing, out var how, out var door, out float swing)) return new float[16];
        float[] pcm = closing ? RenderClose(door, sampleRate) : RenderOpen(door, how, sampleRate, swing);
        return KnobDoor.PeakToFullScale(pcm, PascalsAtFullScale, out fullScaleDb);
    }

    // ── Constants, each a property of a part ─────────────────────────────────────────────────────

    private const double G = 9.81;
    private const double AlE = 70e9, AlRho = 2700, GlassE = 70e9, GlassRho = 2500, GlassPoisson = 0.22;
    /// <summary>The leaf's sections: stiles and top rail 89 by 44.5 mm, bottom rail 254 by 44.5 mm, 3.2 mm
    /// (0.125 in) walls: 2.2 and 5.4 kg a metre.</summary>
    private const double Stile = 0.089, BottomRail = 0.254, Depth = 0.0445, Wall = 0.0032;
    /// <summary>Glass: the prefab's 12 mm toughened pane; a laminated one is two 6 mm plies on 0.76 mm PVB.</summary>
    private const double PaneT = 0.012;
    /// <summary>Hardware on the leaf (the lock body, the pivots' arms, the handle, the bar), kg.</summary>
    private const double HardwareKg = 3;
    /// <summary>
    /// The frame's bending stiffness as a plate, N m: two stiles of EI 19,100 N m^2 each across a metre, and
    /// the rails' (19,100 and 50,700 N m^2) over the leaf's height, about the same, with the pane's own
    /// 10,600 N m stiffening it through its setting blocks and gaskets. Orthotropy is not modelled.
    /// </summary>
    private const double FrameD = 45000;
    /// <summary>The frame's loss: aluminium itself barely loses; its corner joints, the gaskets and the pane
    /// rubbing in them take about 0.03, and more where they move slowly.</summary>
    private const double FrameLoss = 0.03, LeafModeMaxHz = 1500;
    /// <summary>The pane in its gaskets: glass loses 0.002; EPDM gaskets round its edge take about 0.02, and
    /// more at the lowest modes where the edges rotate most in them: 0.02 + 3 / f, 0.06 at 75 Hz, a T60 of
    /// 0.5 s there, as the recordings' glass doors ring (75-81 Hz, T60 0.39-0.51 s). A laminated pane's PVB
    /// interlayer adds about 0.05 at room temperature.</summary>
    private const double GasketLoss = 0.02, GasketLowLoss = 3, LaminatedLoss = 0.05, PaneModeMaxHz = 3000;
    /// <summary>A laminated pane bends as about 0.6 of the monolithic pane of its total thickness at audio
    /// frequencies: the interlayer passes only part of the shear between the plies.</summary>
    private const double LaminatedStiffness = 0.6;
    /// <summary>The frame's tube walls (3.2 mm aluminium, the faces of 89 by 44.5 mm tubes, 0.27 m round),
    /// with a bare panel's loss and what the glazing pocket and the pane in it take (Q 130-280 measured at
    /// 1.5-3 kHz on a glass door's frame, Q 1300 at 6.7 kHz: Irvine's thin panel figure gives that alone).</summary>
    private const double FrameGirth = 0.267, FrameFace = 0.089, FrameWallLoss = 0.004;
    /// <summary>The storefront frame the door shuts into: 44.5 by 114 mm tubes, 3.2 mm walls, anchored in the
    /// opening every 0.6 m and sealed to it, losing more than the leaf: 0.03 on its walls.</summary>
    private const double JambGirth = 0.317, JambFace = 0.114, JambWallLoss = 0.03;
    private const double JambEI = 1.07e5, JambKgPerM = 2.6, JambSpan = 0.6, JambLoss = 0.04;
    private const double PortStiffness = 2e7;

    /// <summary>The stop's EPDM bulb: a 10 mm hollow bulb, about 30 N to close it 3 mm over the 0.7 m of stop
    /// each point stands for (Hertz form, N/m^1.5), and rubber's loss. Squashed flat it is a pad of its two
    /// 1 mm walls: 5 MPa over 10 mm by 0.6 m through 2 mm, about 1.5e7 N/m, and lossy.</summary>
    private const double BulbK = 2e5, BulbLambda = 1.2, PadK = 6.7e8, PadLambda = 1.5;
    /// <summary>A pile weatherstrip: polypropylene fibres 6 mm tall on a backing, a few newtons to press flat
    /// over a stop's length, and flat a mat of fibres, stiffer than rubber and giving back more.</summary>
    private const double PileK = 4e4, PileLambda = 2, PileMatK = 3e9, PileMatLambda = 0.5;
    /// <summary>Aluminium on aluminium through the bulb's flattened lip: a stile's edge on the stop leg.</summary>
    private const double AlContactK = 3e9, AlContactLambda = 0.15;
    private static readonly double[] JambStops = { 0.3, 1.05, 1.8 };

    // The latch: Adams Rite 4900 type, 1/2 in throw, a stainless bolt, in a lock body in the lock stile.
    private const double BoltMass = 0.02, Throw = 0.0127, SpringPreload = 6, SpringRate = 400;
    private const double LatchGap = 0.003, LatchHeight = 1.0;
    private const double RetractedAt = 0.0015;
    private const double MetalContactK = 4e9, MetalContactLambda = 0.05, BoltStopLambda = 0.3;
    private const double BoltSideStiffness = 2e7, StrikeMass = 0.04, StrikeMountStiffness = 3e7;
    /// <summary>The lock body: a 0.35 kg steel case in the stile's pocket, held by its two face screws.</summary>
    private const double LockBodyKg = 0.35, LockBodyStiffness = 3e7, LockBodyZeta = 0.15, LockBodyArea = 0.03 * 0.15;
    /// <summary>A narrow-stile lock body sits in the stile's tube, square to the leaf: a tenth of its blows
    /// bend the stile's face.</summary>
    private const double LatchBending = 0.1;
    /// <summary>A key turned and held draws the bolt to 1.5 mm out, through the lock's hub: as stiff as the
    /// knob door's cam.</summary>
    private const double KeyHoldStiffness = 2e6, KeyHoldDamping = 40;

    // The touchbar (as the steel door's, on an aluminium case).
    private const double BarMass = 0.15, BarTravel = 0.016, BarPlay = 0.004, BarPreload = 8, BarRate = 600;
    private const double PadE = 2.5e9, PadRho = 1150, PadLoss = 0.03;
    private const double DriveBarMass = 0.15, DriveBarLink = 2e6, CrankDamping = 0.05, GuideFriction = 4;
    private const double StopK = 5e9, StopLambda = 0.1, StopTab = 0.02;
    private const double CaseWall = 0.002, CaseFaceWidth = 0.05, CaseLength = 0.8, MechanismLoss = 0.01;
    private const double LinkStiffness = 1e6, LinkDamping = 80;
    private const double MountNear = 0.15, MountFarInset = 0.1;

    // The pull handle: a 25.4 mm by 1.6 mm stainless tube, 305 mm between its posts, 76 mm off the face.
    private const double HandleKg = 0.5, HandleSpan = 0.305, HandleOD = 0.0254, HandleID = 0.0222;
    private const double HandleHeight = 1.05, HandleInset = 0.09;
    /// <summary>The posts on their through-bolts: steel on aluminium where they bear, either end of the play.</summary>
    private const double PostK = 4e9, PostLambda = 0.1;
    /// <summary>The bolts' tension holds a loose handle near the middle of its play: a few newtons over a
    /// tenth of a millimetre. A tight handle is clamped to the stile by its bolts' preload: a stiff mount.</summary>
    private const double HandleCentring = 3e4, HandleClamp = 3e7, HandleClampZeta = 0.05;
    private const double HandleHeldLoss = 0.3, HandleFreeLoss = 0.003;

    // The closer: size 4 (30 N m at the latch, 8 N m per radian more open), its arm's shoe 0.25 m out.
    private const double CloserTorque = 30, CloserRate = 8, CloserShoeX = 0.25;
    private const double CloserOpening = 2, BackcheckFrom = 70 * Math.PI / 180, Backcheck = 60;
    private const double CloserHissPa = 0.001;

    // The hand.
    private const double PalmStiffness = 5e4, PalmDamping = 150, ArmSpeed = 1.5;
    /// <summary>The hand on a handle: about half a kilogram moving with the palm, and an arm that gives way to
    /// the door at about 150 N s/m while it takes it up (ISO 10068, as the knob door's hand).</summary>
    private const double HandKg = 0.5, ArmGive = 150;
    /// <summary>A shove on a bar (as the steel door's), a shove on a push handle, and a grip's pull, N, with the
    /// time each comes up over. A hand pulls a door more slowly than it shoves one: the fingers close on the
    /// handle first.</summary>
    private const double BarShove = 200, HandleShove = 120, HandlePull = 90, ShoveRamp = 0.05, PullRamp = 0.15;

    // The sweep: a brush of nylon fibres 0.15 mm thick, 6 mm long, about 100 000 of them over the bottom rail.
    private const double SweepFibres = 1e5, SweepReach = 0.03, SweepFibreHz = 950, SweepZeta = 0.3, SweepSlip = 1.5e-4;

    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Sim
    {
        private readonly Door door;
        private readonly int rate;
        private readonly double dt;
        private readonly Report? report;
        private readonly Random rng;
        private readonly double width, height, mass, inertia;
        private readonly double bulb, latchSpeed, handlePlay, crankPlay, sweepDrag, keeperPlay;
        private readonly Seal seal;
        private readonly bool hasLatch;
        private double theta, omega, lastAlpha;

        // The leaf (frame), the pane riding on it, the frame's walls, the jamb.
        private readonly Modes leaf, pane, jambBeam;
        private readonly double[,] paneCoupling;
        private readonly double[] paneRigid;
        private readonly double paneRhoH;
        private readonly double[] paneDrive;
        private int paneTick;
        private readonly DenseField frameField, jambField;
        private readonly (double X, double Y, double Warp, double[] Shape, double[] JambShape)[] stops;
        private readonly Port[] leafPorts, jambPorts;
        private readonly double[][] leafHit, jambHit;
        private readonly double rigidGain;
        private double rigidLow;

        // Latch.
        private double bolt = Throw, boltRate, boltAccNow;
        private bool boltInStrike = true;
        private LuGre keeperFriction;
        private readonly Mount boltSide, strikeBody, lockBody;
        private readonly Modes strike;
        private readonly SmallRadiator strikeSound, lockSound;
        private readonly AccelerationNoise boltNoise, strikeNoise;
        private readonly double[] latchShape, latchHit, strikeHit, strikeBeam;
        private readonly Port strikePort, latchPort, rimStop;
        private readonly Modes lockCase;
        private readonly double[] lockCaseHit;
        private bool keyHeld, dogged;

        // The bar.
        private readonly DenseField? caseField, padField;
        private readonly Port? padPort, casePad, caseDrive;
        private readonly double[] caseHit = Array.Empty<double>(), padHit = Array.Empty<double>(), driveHit = Array.Empty<double>(), rimHit = Array.Empty<double>();
        private readonly double[] nearShape, farShape;
        private readonly AccelerationNoise padNoise, driveNoise;
        private double bar, barRate, driveBar, driveRate, lastBarRate, barAccLow;

        // The handle.
        private Modes handle;
        private readonly double[] handleShape, handleHit;
        private readonly Port handlePort;
        private readonly AccelerationNoise handleNoise;
        private double handleY, handleV, handleAcc;
        private bool handleSeated, gripped;
        private readonly double handleArm;

        // The hand.
        private double handForce, handX, handV;
        private Func<double, (double Angle, double Rate)>? leafPath;
        private readonly double handK, handC;
        private bool onBar, onHandle;

        // The closer and the sweep.
        private readonly double[] shoeShape, sweepHit;
        private bool closerLatchValve;
        private double latchDamping, hissState, sweepState, sweepState2, sweepNoise, sweepLow;
        private readonly double sweepW = 2 * Math.PI * SweepFibreHz;

        private double time;
        private readonly List<float> outHi = new();
        private readonly Dictionary<string, (double Start, double Peak, bool On)> contactLog = new();
        private static readonly string[] PeakNames = { "frame", "pane", "walls", "jamb", "latch", "bar", "handle", "piston" };
        private readonly double[] peaks = new double[PeakNames.Length];
        private List<float>[]? stems;
        private readonly double[] ones = { 1 };

        public Sim(Door door, int sampleRate, Report? report)
        {
            this.door = door; this.report = report;
            rate = sampleRate * Oversample; dt = 1.0 / rate;
            rng = new Random(door.Seed);
            width = door.Width; height = door.Height;
            (seal, double bulbMm, latchSpeed, double handlePlayMm, double crankPlayMm, sweepDrag) = Character(door.Variant);
            bulb = bulbMm / 1000; handlePlay = handlePlayMm / 1000; crankPlay = crankPlayMm / 1000;
            hasLatch = door.Kind == Kind.PushBar;
            // The strike is set so the bolt drops in as the bulb takes the closer's push, and does not bind
            // on the keeper when the leaf rests on a fresh bulb.
            keeperPlay = Math.Max(bulb, 0.0015) + 0.0005;

            // Mass: the pane, the frame's sections round it, the hardware.
            double paneW = width - 2 * Stile, paneH = height - Stile - BottomRail;
            bool laminated = door.Glass == Glazing.Laminated;
            double paneKg = paneW * paneH * (PaneT * GlassRho + (laminated ? 0.00076 * 1070 : 0));
            double tube = (Stile * Depth - (Stile - 2 * Wall) * (Depth - 2 * Wall)) * AlRho;
            double rail = (BottomRail * Depth - (BottomRail - 2 * Wall) * (Depth - 2 * Wall)) * AlRho;
            mass = paneKg + tube * (2 * height + paneW) + rail * paneW + HardwareKg;
            double area = width * height, rhoH = mass / area;
            inertia = mass * width * width / 3;
            var plate = new Plate(width, height, FrameD, rhoH, 0, LeafModeMaxHz, true, rng, 0.03);
            for (int k = 0; k < plate.Loss.Count; k++) plate.Loss[k] += FrameLoss + GasketLowLoss / plate.Hz[k];
            leaf = new Modes(plate.Hz, plate.Loss, plate.Mass, plate.Gain, dt, plate.GainQuad);
            rigidGain = Rho0 / (2 * Math.PI) * height * width * width / 2;

            // The pane: simply supported in its gaskets, carried by the frame.
            double paneD = GlassE * PaneT * PaneT * PaneT / (12 * (1 - GlassPoisson * GlassPoisson)) * (laminated ? LaminatedStiffness : 1);
            paneRhoH = paneKg / (paneW * paneH);
            var panePlate = new Plate(paneW, paneH, paneD, paneRhoH, 0, PaneModeMaxHz, false, rng, 0.02);
            for (int k = 0; k < panePlate.Loss.Count; k++)
                panePlate.Loss[k] += GasketLoss + GasketLowLoss / panePlate.Hz[k] + (laminated ? LaminatedLoss : 0);
            pane = new Modes(panePlate.Hz, panePlate.Loss, panePlate.Mass, panePlate.Gain, dt, panePlate.GainQuad);
            // What the frame's motion does to the pane: each pane mode is pushed by -rho h times the overlap of
            // its shape with the frame's acceleration there (the frame's modes and its turn on the pivots).
            const int gx = 28, gy = 56;
            double cell = paneW / gx * (paneH / gy);
            paneCoupling = new double[pane.N, leaf.N];
            paneRigid = new double[pane.N];
            paneDrive = new double[pane.N];
            var leafAt = new double[leaf.N];
            for (int ix = 0; ix < gx; ix++)
                for (int iy = 0; iy < gy; iy++)
                {
                    double px = (ix + 0.5) * paneW / gx, py = (iy + 0.5) * paneH / gy;
                    double lx = Stile + px, ly = BottomRail + py;
                    for (int j = 0; j < leaf.N; j++) leafAt[j] = plate.ShapeAt(j, lx, ly);
                    for (int k = 0; k < pane.N; k++)
                    {
                        double phi = panePlate.ShapeAt(k, px, py) * cell;
                        paneRigid[k] += phi * lx;
                        for (int j = 0; j < leaf.N; j++) paneCoupling[k, j] += phi * leafAt[j];
                    }
                }

            // The frame's walls and the jamb's, as dense fields struck through patches.
            frameField = new DenseField(FrameGirth, 2 * (width + height), Wall, AlE, AlRho, Poisson,
                                        f => ThinPanelLoss(f) + FrameWallLoss, 300, 16000, rng, dt, DenseField.CapSpacing, FrameFace);
            jambField = new DenseField(JambGirth, 2 * height + width, Wall, AlE, AlRho, Poisson,
                                       f => ThinPanelLoss(f) + JambWallLoss, 300, 16000, rng, dt, DenseField.CapSpacing, JambFace);
            jambBeam = JambModes(dt, rng);

            var list = new List<(double, double, double, double[], double[])>();
            foreach (double y in JambStops)
                list.Add((width, y, (rng.NextDouble() - 0.4) * 0.0008, plate.Shape(width, y), JambShape(y)));
            foreach (double x in new[] { 0.85 * width, 0.5 * width })
                list.Add((x, height - 0.01, (rng.NextDouble() - 0.4) * 0.0008, plate.Shape(x, height - 0.01), JambShape(height - 0.3 + x * 0.2)));
            stops = list.ToArray();
            leafPorts = new Port[stops.Length]; jambPorts = new Port[stops.Length];
            leafHit = new double[stops.Length][]; jambHit = new double[stops.Length][];
            for (int i = 0; i < stops.Length; i++)
            {
                leafPorts[i] = new Port(frameField.PatchMass, PortStiffness, frameField.Impedance);
                jambPorts[i] = new Port(jambField.PatchMass, PortStiffness, jambField.Impedance);
                leafHit[i] = frameField.Point(); jambHit[i] = jambField.Point();
            }

            // Latch, strike, lock body.
            latchShape = plate.Shape(width - 0.02, LatchHeight);
            rimHit = frameField.Point();
            latchHit = frameField.Point(); strikeHit = jambField.Point(); strikeBeam = JambShape(LatchHeight);
            strike = new Modes(new[] { Beam(0.012, 0.0016, 7900, 195e9, 1.875), Beam(0.06, 0.0016, 7900, 195e9, 4.730) },
                               new[] { 0.03, 0.03 }, new[] { 0.008, 0.02 },
                               new[] { SmallPlateGain(0.012 * 0.025, 0.6), SmallPlateGain(0.06 * 0.025, 0.4) }, dt);
            boltSide = new Mount(BoltMass, BoltSideStiffness, 0.2);
            strikeBody = new Mount(StrikeMass, StrikeMountStiffness, 0.15);
            lockBody = new Mount(LockBodyKg, LockBodyStiffness, LockBodyZeta);
            // The lock body's case: 1.5 mm steel walls 25 by 120 mm, their first modes from about 5 kHz, damped by
            // the mechanism packed inside and the stile's pocket round them.
            {
                var hz = new List<double>(); var l = new List<double>(); var m = new List<double>(); var g = new List<double>();
                for (int i = 0; i < 8; i++)
                {
                    hz.Add(Beam(0.025, 0.0015, 7850, 200e9, 4.730 + i * Math.PI) * (1 + 0.04 * (rng.NextDouble() * 2 - 1)));
                    l.Add(0.05); m.Add(0.03);
                    g.Add(SmallPlateGain(0.025 * 0.03, 0.3 / (1 + i)) * (rng.NextDouble() < 0.5 ? -1 : 1));
                }
                lockCase = new Modes(hz, l, m, g, dt);
                lockCaseHit = new double[hz.Count];
                for (int i = 0; i < hz.Count; i++) lockCaseHit[i] = 1;
            }
            strikeSound = new SmallRadiator(0.025 * 0.1, dt);
            lockSound = new SmallRadiator(LockBodyArea, dt);
            boltNoise = new AccelerationNoise(BoltMass / 7900, dt);
            strikeNoise = new AccelerationNoise(StrikeMass / 7900, dt);
            strikePort = new Port(jambField.PatchMass, PortStiffness, jambField.Impedance);
            latchPort = new Port(frameField.PatchMass, PortStiffness, frameField.Impedance);
            rimStop = new Port(StopTab, PortStiffness, frameField.Impedance);
            keeperFriction = new LuGre { MuStatic = 0.4, MuSliding = 0.25, StribeckSpeed = 0.01, Viscous = 0 };

            // The bar, on a front door.
            nearShape = plate.Shape(MountNear, LatchHeight);
            farShape = plate.Shape(width - MountFarInset, LatchHeight);
            padNoise = new AccelerationNoise(BarMass / PadRho, dt);
            driveNoise = new AccelerationNoise(DriveBarMass / 7850, dt);
            if (hasLatch)
            {
                caseField = new DenseField(0.15, CaseLength, CaseWall, AlE, AlRho, Poisson, f => Math.Max(ThinPanelLoss(f), MechanismLoss),
                                           300, 16000, rng, dt, 15, CaseFaceWidth);
                padField = new DenseField(0.04, CaseLength - 0.1, 0.003, PadE, PadRho, 0.38, _ => PadLoss, 300, 16000, rng, dt, 15);
                caseHit = caseField.Point(); padHit = padField.Point(); driveHit = caseField.Point();
                padPort = new Port(padField.PatchMass, PortStiffness, padField.Impedance);
                casePad = new Port(StopTab, PortStiffness, caseField.Impedance);
                caseDrive = new Port(StopTab, PortStiffness, caseField.Impedance);
            }

            // The handle: a clamped tube between its posts, radiating as a cylinder (a dipole until it is a
            // wavelength round).
            handleArm = width - HandleInset;
            handleShape = plate.Shape(handleArm, HandleHeight);
            handleHit = frameField.Point();
            handlePort = new Port(frameField.PatchMass, PortStiffness, frameField.Impedance);
            handleNoise = new AccelerationNoise(HandleKg / 7900, dt);
            {
                double rg = Math.Sqrt((HandleOD * HandleOD + HandleID * HandleID) / 16), tEq = rg * Math.Sqrt(12);
                double[] betaL = { 4.730, 7.853, 10.996, 14.137 };
                var hz = new double[betaL.Length]; var hl = new double[betaL.Length]; var hm = new double[betaL.Length]; var hg = new double[betaL.Length];
                double tubeKg = Math.PI / 4 * (HandleOD * HandleOD - HandleID * HandleID) * 7900 * HandleSpan;
                for (int i = 0; i < betaL.Length; i++)
                {
                    hz[i] = Beam(HandleSpan, tEq, 7900, 195e9, betaL[i]) * (1 + 0.02 * (rng.NextDouble() * 2 - 1));
                    hl[i] = HandleHeldLoss; hm[i] = tubeKg / 2;
                    double ka = 2 * Math.PI * hz[i] / C0 * HandleOD / 2;
                    hg[i] = SmallPlateGain(HandleOD * HandleSpan, 0.5 / (i + 1)) * ka / Math.Sqrt(1 + ka * ka);
                }
                handle = new Modes(hz, hl, hm, hg, dt);
            }

            shoeShape = plate.Shape(CloserShoeX, height - 0.05);
            sweepHit = frameField.Point();
            handK = 170 * inertia;
            handC = 2 * 0.7 * Math.Sqrt(handK * inertia);
        }

        /// <summary>The jamb's tube between anchors, a clamped beam radiating as its face.</summary>
        private static Modes JambModes(double dt, Random rng)
        {
            var hz = new List<double>(); var l = new List<double>(); var m = new List<double>(); var g = new List<double>();
            for (int n = 1; n < 40; n++)
            {
                double bl = (n + 0.5) * Math.PI;
                double f = bl * bl / (2 * Math.PI * JambSpan * JambSpan) * Math.Sqrt(JambEI / JambKgPerM);
                if (f > 8000) break;
                f *= 1 + 0.03 * (rng.NextDouble() * 2 - 1);
                hz.Add(f); m.Add(JambKgPerM * JambSpan / 2); l.Add(JambLoss);
                double ka = 2 * Math.PI * f / C0 * JambFace;
                g.Add(Rho0 / (2 * Math.PI) * JambFace * JambSpan * 0.5 / n * ka / Math.Sqrt(1 + ka * ka));
            }
            return new Modes(hz, l, m, g, dt);
        }

        private double[] JambShape(double y)
        {
            var s = new double[jambBeam?.N ?? 40];
            double local = (y % JambSpan) / JambSpan;
            for (int n = 0; n < s.Length; n++) s[n] = Math.Sin((n + 1) * Math.PI * Math.Clamp(local, 0.05, 0.95));
            return s;
        }

        // ── Scripts ──────────────────────────────────────────────────────────────────────────────

        public void StartShut(Opening how)
        {
            // Resting on its bulbs under the closer's push, nowhere in the aluminium.
            theta = Math.Max(0, bulb - 0.0008) / width;
            foreach (var s in stops) theta = Math.Max(theta, -s.Warp / s.X + 1e-6);
            omega = 0;
            Settle();
            bolt = hasLatch ? Throw : 0; boltInStrike = hasLatch;
            if (hasLatch && how != Opening.Push)
            {
                // The key (or the dogging screw) has the bolt drawn in already.
                keyHeld = how == Opening.Key; dogged = how == Opening.Pull;
                bolt = RetractedAt; boltInStrike = false;
            }
        }

        /// <summary>
        /// The leaf let down onto its seals under the closer, a hand on its edge taking out its motion, and
        /// nothing of it kept: a shut door at rest is where every opening starts.
        /// </summary>
        private void Settle()
        {
            settling = true;
            while (time < 0.3) Tick(opening: true);
            settling = false;
            outHi.Clear(); contactLog.Clear(); Array.Clear(peaks);
            if (stems != null) foreach (var st in stems) st?.Clear();
            time = 0; omega = 0;
        }
        private bool settling;

        /// <summary>A front door from inside: shove the bar, the bolt draws back, the door goes, the bar is let
        /// go at 20 degrees. (The steel door's script on this door's hardware.)</summary>
        public void ScriptBar(double swingSeconds)
        {
            const double reach = 0.02;
            double cleared = -1, released = -1, end = 10;
            double wRate = Math.PI / 2 / swingSeconds;
            onBar = true;
            while (time < end)
            {
                if (time > reach && cleared < 0)
                    handForce = Math.Min(1, (time - reach) / ShoveRamp) * BarShove;
                if (cleared < 0 && !boltInStrike && time > reach)
                {
                    cleared = time;
                    Log($"{time * 1000:F0} ms  bolt clear; the door goes");
                    double a0 = theta, t0 = time;
                    leafPath = t => (a0 + wRate * (t - t0), wRate);
                }
                if (cleared > 0 && released < 0)
                {
                    var (a, r) = leafPath!(time);
                    double arm = 0.5 * (MountNear + width - MountFarInset);
                    double need = (handK * (a - theta) + handC * (r - omega) + CloserTorque + CloserRate * theta) / arm;
                    handForce = Math.Clamp(need, 40, 200);
                    if (theta > 20 * Math.PI / 180)
                    {
                        released = time; handForce = 0;
                        Log($"{time * 1000:F0} ms  bar let go");
                        end = time + 0.6;
                    }
                }
                Tick(opening: true);
            }
        }

        /// <summary>
        /// A hand on the handle: a pull (fingers round the tube, drawing it), or a push on the push side's
        /// handle (a shove), until the leaf is clear of its stop; then it carries the leaf to about 85 degrees
        /// over <paramref name="swingSeconds"/>. With a key, the key is let go once the leaf is 20 mm out.
        /// </summary>
        public void ScriptHandle(Opening how, double swingSeconds)
        {
            const double reach = 0.04;
            bool shove = how == Opening.Push;
            double want = shove ? HandleShove : HandlePull, ramp = shove ? ShoveRamp : PullRamp;
            double away = -1, keyGone = -1, end = 10;
            onHandle = true; gripped = !shove;
            if (shove) { handX = handleY - 0.004; handV = 0; }
            while (time < end)
            {
                double edge = theta * width;
                if (away < 0)
                {
                    if (time > reach) handForce = Math.Min(1, (time - reach) / ramp) * want;
                    if (edge > keeperPlay + 0.004 && time > reach)
                    {
                        away = time;
                        Log($"{time * 1000:F0} ms  leaf off its stop; the hand swings it");
                        double a0 = theta, w0 = omega, a1 = 85 * Math.PI / 180, t0 = time;
                        leafPath = t =>
                        {
                            double u = Math.Clamp((t - t0) / swingSeconds, 0, 1);
                            var (pp, vv) = Hermite(u, a0, w0 * swingSeconds, a1, 0);
                            return (pp, vv / swingSeconds);
                        };
                        end = t0 + swingSeconds + 0.6;
                    }
                }
                else
                {
                    var (a, r) = leafPath!(time);
                    double need = (handK * (a - theta) + handC * (r - omega) + CloserTorque + CloserRate * theta
                                   + BackcheckShare(theta) * Backcheck * Math.Max(0, omega)) / handleArm;
                    handForce = Math.Clamp(need, shove ? 0 : 5, 250);
                }
                if (away < 0 && time > 1.5) { Log($"{time * 1000:F0} ms  the leaf never left its stop"); break; }
                if (keyHeld && edge > 0.02 && keyGone < 0)
                {
                    keyGone = time; keyHeld = false;
                    Log($"{time * 1000:F0} ms  key let go: the latch springs out");
                }
                Tick(opening: true);
            }
        }

        public void ScriptCloserLatch()
        {
            // Fifteen millimetres before the first contact, coming in at the latch valve's speed.
            double first = hasLatch ? keeperPlay + (Throw - LatchGap) : bulb;
            theta = (first + 0.015) / width;
            double latchRate = latchSpeed / width;
            omega = -latchRate;
            closerLatchValve = true;
            latchDamping = (CloserTorque + CloserRate * theta) / latchRate;
            bolt = hasLatch ? Throw : 0; boltInStrike = false;
            double firstHit = -1, end = 3;
            while (time < end)
            {
                if (firstHit < 0 && (contactLog.ContainsKey("bevel") || contactLog.ContainsKey("seal") || contactLog.ContainsKey("frame-on-frame")))
                {
                    firstHit = time; end = time + 0.9 + 0.03 / latchSpeed;
                }
                Tick(opening: false);
            }
        }

        // ── One step ─────────────────────────────────────────────────────────────────────────────

        private void Tick(bool opening)
        {
            double torque = 0;

            // The closer: its spring toward shut, its oil against motion.
            double closer = -(CloserTorque + CloserRate * Math.Max(0, theta));
            if (omega > 0) closer -= (CloserOpening + BackcheckShare(theta) * Backcheck) * omega;
            else if (closerLatchValve) closer -= latchDamping * omega;
            torque += closer;
            leaf.Push(shoeShape, closer / CloserShoeX);

            // The stops: the bulb, then aluminium.
            bool near = theta * width < 0.03;
            double bulbSum = 0, alSum = 0;
            for (int i = 0; i < stops.Length; i++)
            {
                var s = stops[i];
                double f = 0;
                if (near)
                {
                    double pos = s.X * theta + leaf.At(s.Shape) + s.Warp + leafPorts[i].X - jambPorts[i].X;
                    double vel = s.X * omega + leaf.RateAt(s.Shape) + leafPorts[i].V - jambPorts[i].V;
                    double fb = 0, fa;
                    if (seal == Seal.Bulb)
                    {
                        fb = Contact(BulbK, BulbLambda, bulb - pos, -vel);
                        fa = Contact(PadK, PadLambda, -pos, -vel);
                    }
                    else if (seal == Seal.Pile)
                    {
                        fb = Contact(PileK, PileLambda, bulb - pos, -vel);
                        fa = Contact(PileMatK, PileMatLambda, -pos, -vel);
                    }
                    else fa = Contact(AlContactK, AlContactLambda, -pos, -vel);
                    f = fb + fa; bulbSum += fb; alSum += fa;
                }
                frameField.Modes.Push(leafHit[i], leafPorts[i].Step(f, dt, out double host));
                if (host != 0) { torque += host * s.X; leaf.Push(s.Shape, host); }
                jambField.Modes.Push(jambHit[i], jambPorts[i].Step(-f, dt, out double jhost));
                if (jhost != 0) jambBeam.Push(s.JambShape, jhost);
            }
            Note("seal", bulbSum);
            Note(seal == Seal.None ? "frame-on-frame" : "seal-flat", alSum);
            if (settling) torque -= 300 * omega;

            // The latch.
            double latchEdgeForce = 0, strikeForce = 0, boltForce = 0;
            if (hasLatch)
            {
                double latchW = leaf.At(latchShape), latchWRate = leaf.RateAt(latchShape);
                double edge = width * theta + latchW, edgeRate = width * omega + latchWRate;
                boltForce = SpringPreload + SpringRate * (Throw - bolt);
                double across = edge + boltSide.X - strikeBody.X, acrossRate = edgeRate + boltSide.V - strikeBody.V;
                if (bolt > LatchGap)
                {
                    if (!boltInStrike)
                    {
                        double over = (bolt - LatchGap) - (across - keeperPlay);
                        if (across > keeperPlay && across < keeperPlay + Throw)
                        {
                            double fn = Contact(MetalContactK, MetalContactLambda, over / Math.Sqrt(2), (boltRate - acrossRate) / Math.Sqrt(2));
                            double ft = 0.2 * fn * Math.Tanh((boltRate + acrossRate) / Math.Sqrt(2) / 0.002);
                            double onBolt = (-fn - ft) / Math.Sqrt(2), sideways = (fn - ft) / Math.Sqrt(2);
                            boltForce += onBolt;
                            boltSide.F += sideways; strikeBody.F -= sideways;
                            strikeForce -= onBolt;
                            Note("bevel", fn);
                        }
                        else Note("bevel", 0);
                        if (across <= keeperPlay) { boltInStrike = true; Log($"{time * 1000:F1} ms  bolt over the strike"); }
                    }
                    if (boltInStrike)
                    {
                        double fk = Contact(MetalContactK, MetalContactLambda, across - keeperPlay, acrossRate);
                        boltSide.F -= fk; strikeBody.F += fk;
                        if (fk > 0) boltForce -= keeperFriction.Force(boltRate, fk, BoltMass, dt);
                        else keeperFriction.Z = 0;
                        strikeForce += fk * 0.3;
                        Note("keeper", fk);
                    }
                }
                else if (boltInStrike) { Note("keeper", 0); boltInStrike = false; Log($"{time * 1000:F1} ms  bolt in"); }

                // The key, or the dogging, holding the bolt in.
                if (keyHeld || dogged)
                {
                    double depth = bolt - RetractedAt;
                    if (depth > 0) boltForce -= Math.Max(0, KeyHoldStiffness * depth + KeyHoldDamping * boltRate);
                }

                // The bolt's own stops in the lock body: out at full throw, and in against the body's back.
                // They are in the lock body, a steel case on its screws in the stile's pocket: the blow moves the
                // body, and the body's screws pass it to the stile.
                // What the bolt meets first is a tab of the body's steel, which passes the blow into the stile's
                // walls round it. (As a 0.35 kg lump on its screws the body took the highs out of the bolt's 0.1 ms
                // stop, as the steel door's latch case did before its round 8.)
                double stopAt = rimStop.X, stopRate = rimStop.V;
                double fStop = Contact(MetalContactK, BoltStopLambda, bolt - Throw - stopAt, boltRate - stopRate);
                double fBack = Contact(MetalContactK, BoltStopLambda, -(bolt - RetractedAt * 0.5) + stopAt, -(boltRate - stopRate));
                boltForce += fBack - fStop;
                // The blow is along the bolt, in the stile's plane: a tenth of it bends the stile's walls.
                frameField.Modes.Push(rimHit, LatchBending * rimStop.Step(fStop - fBack, dt, out double rimHost));
                lockBody.F += rimHost;
                lockCase.Push(lockCaseHit, fStop + fBack);
                Note("bolt-stop", fStop); Note("bolt-back", fBack);
            }

            // The bar and its linkage.
            double caseForce = 0;
            if (hasLatch && caseField != null)
            {
                double palm = 0;
                if (onBar && handForce > 0)
                {
                    palm = Math.Max(0, PalmStiffness * (handX - bar) + PalmDamping * (handV - barRate));
                    handV = ArmSpeed * Math.Clamp(2 * (handForce - palm) / handForce, 0, 1);
                    handX += handV * dt;
                }
                else if (onBar) { handX = Math.Min(handX, bar - 0.005); handV = 0; }
                double leafAccAtBar = lastAlpha * 0.5 * (MountNear + width - MountFarInset);
                for (int k = 0; k < leaf.N; k++) leafAccAtBar += 0.5 * (nearShape[k] + farShape[k]) * leaf.Acc[k];
                barAccLow += (1 - Math.Exp(-2 * Math.PI * 500 * dt)) * (leafAccAtBar - barAccLow);
                double barForce = palm - (BarPreload + BarRate * Math.Max(0, bar)) - BarMass * barAccLow - GuideFriction * Math.Tanh(barRate / 0.005);
                caseForce = BarPreload + BarRate * Math.Max(0, bar);
                double driveForce = -DriveBarMass * barAccLow - GuideFriction * Math.Tanh(driveRate / 0.005);
                double rel = bar - driveBar, relDepth = Math.Abs(rel) - crankPlay;
                double fCrank = relDepth > 0 ? Math.Sign(rel) * Math.Max(0, DriveBarLink * relDepth
                                + 2 * CrankDamping * Math.Sqrt(DriveBarLink * DriveBarMass) * Math.Sign(rel) * (barRate - driveRate)) : 0;
                barForce -= fCrank; driveForce += fCrank;
                double ratio = Throw / (BarTravel - BarPlay);
                double drawn = Math.Clamp((driveBar - BarPlay) * ratio, 0, Throw);
                double linkDepth = bolt - (Throw - drawn);
                double fLink = linkDepth > 0 && driveBar > BarPlay
                    ? Math.Max(0, LinkStiffness * linkDepth + LinkDamping * (boltRate + (drawn < Throw ? driveRate * ratio : 0))) : 0;
                boltForce -= fLink; driveForce -= fLink * ratio; caseForce += fLink * ratio;
                Note("link", fLink);
                double padAt = bar + padPort!.X, padAtRate = barRate + padPort.V;
                double fIn = Contact(StopK, StopLambda, padAt - BarTravel - casePad!.X, padAtRate - casePad.V);
                double fOut = Contact(StopK, StopLambda, casePad.X - padAt, casePad.V - padAtRate);
                padField!.Modes.Push(padHit, padPort.Step(fOut - fIn, dt, out double padHost));
                barForce += padHost;
                caseField.Modes.Push(caseHit, casePad.Step(fIn - fOut, dt, out double caseHost));
                caseForce += caseHost;
                double dIn = Contact(StopK, StopLambda, driveBar - BarTravel - caseDrive!.X, driveRate - caseDrive.V);
                double dOut = Contact(StopK, StopLambda, caseDrive.X - driveBar, caseDrive.V - driveRate);
                driveForce += dOut - dIn;
                caseField.Modes.Push(driveHit, caseDrive.Step(dIn - dOut, dt, out double caseHost2));
                caseForce += caseHost2;
                Note("bar-bottom", fIn); Note("bar-back", fOut); Note("drive-stop", dIn + dOut);
                double driveAcc = driveForce / DriveBarMass;
                driveRate += driveAcc * dt; driveBar += driveRate * dt;
                barRate += barForce / BarMass * dt; bar += barRate * dt;
                torque += caseForce * 0.5 * (MountNear + width - MountFarInset);
                leaf.Push(nearShape, caseForce * 0.5); leaf.Push(farShape, caseForce * 0.5);
                double barAcc = (barRate - lastBarRate) / dt; lastBarRate = barRate;
                pBarNow = padField.Modes.Step() + caseField.Modes.Step() + padNoise.Pressure(barAcc) + driveNoise.Pressure(driveAcc);
            }

            // The handle on its posts, and the hand on the handle.
            {
                double at = handleArm * theta + leaf.At(handleShape), atRate = handleArm * omega + leaf.RateAt(handleShape);
                if (!handleSeated) { handleY = at; handleV = atRate; handleSeated = true; }
                double gap = handleY - at, gapRate = handleV - atRate;
                double post, onHandle;
                if (handlePlay > 0)
                {
                    post = Contact(PostK, PostLambda, Math.Abs(gap) - handlePlay, Math.Sign(gap) * gapRate);
                    double centring = -HandleCentring * gap - 2 * 0.3 * Math.Sqrt(HandleCentring * HandleKg) * gapRate;
                    onHandle = -Math.Sign(gap) * post + centring;
                }
                else
                {
                    onHandle = -HandleClamp * gap - 2 * HandleClampZeta * Math.Sqrt(HandleClamp * HandleKg) * gapRate;
                    post = 0;
                }
                double palm = 0;
                if (this.onHandle && handForce > 0)
                {
                    // The hand is a mass on the end of the arm: the arm pulls (or shoves) with what the script
                    // wants and gives way to its own motion while it takes up the door; the palm is a spring
                    // between it and the tube, which a grip holds both ways.
                    double sq = PalmStiffness * (handX - handleY) + PalmDamping * (handV - handleV);
                    palm = gripped ? sq : Math.Max(0, sq);
                    double fArm = handForce - (leafPath == null ? ArmGive * handV : 0);
                    handV += (fArm - palm) / HandKg * dt;
                    handX += handV * dt;
                }
                else if (this.onHandle && !gripped) { handX = Math.Min(handX, handleY - 0.004); handV = Math.Min(handV, 0); }
                else if (this.onHandle) { handX = handleY; handV = handleV; }
                onHandle += palm;
                handleAcc = onHandle / HandleKg;
                handleV += handleAcc * dt; handleY += handleV * dt;
                // What the posts take, into the stile.
                frameField.Modes.Push(handleHit, handlePort.Step(-(onHandle - palm), dt, out double hostH));
                torque += hostH * handleArm; leaf.Push(handleShape, hostH);
                if (post > 0) handle.Push(new[] { 0.5, 0.0, 0.5, 0.0 }, post);
                Note("handle-post", post);
                SetHandleLoss(this.onHandle && (gripped || palm > 0));
            }

            // Air, the pivots silent.
            torque -= 0.5 * Rho0 * 1.2 * height * Math.Pow(width, 4) / 4 * omega * Math.Abs(omega);

            // Mounts into their hosts.
            if (hasLatch)
            {
                latchEdgeForce += boltSide.Reaction;
                double toLeaf = latchEdgeForce + lockBody.Reaction * LatchBending;
                frameField.Modes.Push(latchHit, latchPort.Step(toLeaf, dt, out double latchHost));
                torque += latchHost * width;
                leaf.Push(latchShape, latchHost);
                if (strikeForce != 0) strike.Push(ones2, strikeForce);
                double frameLatch = strikeBody.Reaction;
                jambField.Modes.Push(strikeHit, strikePort.Step(frameLatch, dt, out double strikeHost));
                if (strikeHost != 0) jambBeam.Push(strikeBeam, strikeHost);
            }

            // The pane rides on the frame. Its drive is the frame's acceleration, whose modes stop at 1.5 kHz: it
            // is worked out at the output rate and held between.
            if ((paneTick++ & 3) == 0)
                for (int k = 0; k < pane.N; k++)
                {
                    double drive = paneRigid[k] * lastAlpha;
                    for (int j = 0; j < leaf.N; j++) drive += paneCoupling[k, j] * leaf.Acc[j];
                    paneDrive[k] = -paneRhoH * drive;
                }
            for (int k = 0; k < pane.N; k++) pane.F[k] += paneDrive[k];

            // Rigid motion.
            double alpha = torque / inertia;
            lastAlpha = alpha;
            omega += alpha * dt; theta += omega * dt;
            if (hasLatch)
            {
                boltAccNow = (boltForce - 0.4 * Math.Tanh(boltRate / 0.01)) / BoltMass;
                boltRate += boltAccNow * dt; bolt += boltRate * dt;
                boltSide.Step(dt); strikeBody.Step(dt); lockBody.Step(dt);
            }

            // The sweep on the threshold: its fibres catch and let go while it is on it (as the patio door's
            // pile: a fibre slips every 0.15 mm of travel, and the drag flutters by 0.3 / sqrt(N) of itself in
            // the band that slip rate makes, through the fibres' own bending).
            double sweepEdge = theta * width;
            double v = Math.Abs(omega) * width * 0.6;
            double slipHz = Math.Min(20000, v / SweepSlip);
            double a1 = 1 - Math.Exp(-2 * Math.PI * Math.Max(slipHz, 1) * dt);
            sweepNoise += a1 * ((rng.NextDouble() * 2 - 1) * Math.Sqrt(3) - sweepNoise);
            double on = sweepEdge < SweepReach ? 1 - sweepEdge / SweepReach : 0;
            double flutter = sweepDrag * on * Math.Tanh(v / 0.005) * 0.3 / Math.Sqrt(SweepFibres) * sweepNoise / Math.Sqrt(a1 / (2 - a1));
            sweepLow += a1 * (flutter - sweepLow);
            flutter = (flutter - sweepLow) * Math.Sqrt(2);
            double fw = sweepW * sweepW * (flutter - sweepState) - 2 * SweepZeta * sweepW * sweepState2;
            sweepState2 += fw * dt; sweepState += sweepState2 * dt;
            frameField.Modes.Push(sweepHit, sweepState);
            double pSweep = 0;

            // Radiate.
            double pFrame = leaf.Step(), pPane = pane.Step(), pWalls = frameField.Modes.Step();
            double pJamb = jambField.Modes.Step() + jambBeam.Step();
            double pLatch = 0;
            if (hasLatch)
                pLatch = strike.Step() + lockCase.Step() + strikeSound.Pressure(strikeBody.Acc) + strikeNoise.Pressure(strikeBody.Acc)
                         + lockSound.Pressure(lockBody.Acc) + boltNoise.Pressure(boltAccNow);
            double pBar = pBarNow; pBarNow = 0;
            double pHandle = handle.Step() + handleNoise.Pressure(handleAcc);
            if (!opening && omega < 0)
            {
                double flow = -omega / 0.1;
                hissState = 0.9 * hissState + 0.1 * (rng.NextDouble() * 2 - 1);
                pWalls += CloserHissPa * flow * flow * flow * ((rng.NextDouble() * 2 - 1) - hissState);
            }
            double corner = C0 / (2 * Math.PI * Math.Sqrt(width * height / Math.PI));
            rigidLow += (1 - Math.Exp(-2 * Math.PI * corner * dt)) * (alpha - rigidLow);
            double pRigid = rigidGain * rigidLow * Math.Clamp(1 - theta / 0.15, 0, 1);
            double p = pFrame + pPane + pWalls + pJamb + pLatch + pBar + pHandle + pRigid + pSweep;
            double[] parts = { pFrame, pPane, pWalls, pJamb, pLatch, pBar, pHandle, pRigid };
            for (int i = 0; i < parts.Length; i++) peaks[i] = Math.Max(peaks[i], Math.Abs(parts[i]));
            if (StemFolder != null)
            {
                stems ??= new List<float>[parts.Length];
                for (int i = 0; i < parts.Length; i++) (stems[i] ??= new List<float>()).Add((float)(parts[i] / PascalsAtFullScale));
            }
            outHi.Add((float)p);
            time += dt;
        }

        private double pBarNow;

        /// <summary>The backcheck's valve is a port the piston uncovers over its travel: it comes in over about
        /// ten degrees from 70, not at once (at once, its step of torque rang the leaf at the end of every opening).</summary>
        private static double BackcheckShare(double theta) => Math.Clamp((theta - BackcheckFrom) / (10 * Math.PI / 180), 0, 1);
        private readonly double[] ones2 = { 1, 1 };

        private bool handleHeldState = true;
        private void SetHandleLoss(bool held)
        {
            if (held == handleHeldState) return;
            handleHeldState = held;
            var q = (double[])handle.Q.Clone(); var v = (double[])handle.V.Clone();
            var loss = new double[handle.N];
            for (int i = 0; i < loss.Length; i++) loss[i] = held ? HandleHeldLoss : HandleFreeLoss + 0.01;
            var fresh = new Modes(handle.Hz, loss, handle.Mass, handle.Gain, dt);
            Array.Copy(q, fresh.Q, q.Length); Array.Copy(v, fresh.V, v.Length);
            handle = fresh;
        }

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
            foreach (var kv in contactLog)
                if (kv.Value.On) Log($"{kv.Value.Start * 1000:F1} ms  {kv.Key}: peak {kv.Value.Peak:F1} N (still touching)");
            var sb = new StringBuilder("peaks by part, dB SPL at 1 m:");
            for (int i = 0; i < peaks.Length; i++) sb.Append($" {PeakNames[i]} {20 * Math.Log10(Math.Max(1e-9, peaks[i]) / 2e-5):F0}");
            Log(sb.ToString());
            Log($"modes: frame {leaf.N} ({leaf.Hz[0]:F0}-{leaf.Hz[leaf.N - 1]:F0} Hz), pane {pane.N} ({pane.Hz[0]:F0}-{pane.Hz[pane.N - 1]:F0} Hz), walls {frameField.Modes.N}, jamb {jambField.Modes.N}+{jambBeam.N}, leaf {mass:F0} kg");
            if (StemFolder != null && stems != null)
                for (int i = 0; i < stems.Length; i++)
                    using (var f = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(StemFolder, "gd-" + PeakNames[i] + ".raw"))))
                        foreach (var v in stems[i]) f.Write(v);
            var y = Decimate(outHi, rate, PascalsAtFullScale, out double peak);
            if (report != null) report.PeakPascals = peak;
            return y;
        }
    }
}
