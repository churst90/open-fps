using System;
using System.Collections.Generic;
using System.Text;
using static OpenFPS.Common.DoorPhysics;

namespace OpenFPS.Common;

/// <summary>
/// A steel push-bar door with a closer, simulated as the object, the way <see cref="KnobDoor"/> is.
///
/// The parts:
///
///   The LEAF: a hollow metal door, two 1.2 mm steel skins on welded steel stiffeners with its edge
///   channels, about 46 kg. A steel sandwich is stiff for its weight, so it radiates well from about
///   180 Hz up: that is its boom. Above about 1.5 kHz the core shears and the modes crowd together.
///
///   The PUSH BAR: an aluminium touchpad on a spring in a steel case screwed to the leaf. Pushing it
///   moves it 14 mm; after 4 mm of play its linkage draws the latch bolt in. It bottoms on its case and,
///   let go, springs back against its outer stop. Every one of those forces is square to the leaf's
///   face, which is why a push bar is heard through the whole door. The push is also what opens the
///   door: before the bolt is clear it presses the bolt's flat face on the strike, which is the drag
///   and then the lurch.
///
///   The LATCH and STRIKE: a rim latch, a heavier bolt and spring than a knob door's, into a steel rim
///   strike on the frame.
///
///   The FRAME: a pressed steel channel set in the wall, with three rubber silencers on its stop that
///   the leaf lands on before it can reach the steel. A door whose silencers are gone lands steel on
///   steel.
///
///   The CLOSER: a spring pushing oil through a valve. In its last ten degrees it is the latch valve's
///   speed and its spring that carry the door into the latch, and the spring keeps pressing after.
///
/// Hinges are ball-bearing butts: no squeak, no friction worth having.
/// </summary>
public static class PushBarDoor
{
    /// <summary>One particular door. Its character is its silencers and how worn its bar is.</summary>
    public sealed class Door
    {
        public float Width = 1.0f, Height = 2.1f;
        public int Variant;
        public int Seed = 1;
    }

    public const double PascalsAtFullScale = 20.0;

    /// <summary>The lab's instrument: when set, each part's pressure alone is written here as PART.raw.</summary>
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

    /// <summary>
    /// Each character: how far its silencers stand proud of the stop, how hard the pad's plastic strikes the
    /// chassis (Hertz stiffness, N/m^1.5: new and resilient up to old and brittle), how much of a metal
    /// blow comes back.
    /// Round 1 put every bar on soft urethane and Cody heard no push at all; bare metal on every one rang
    /// the case's walls at 134 dBA. A new device, nylon; a standard one, acetal; a worn one with its
    /// silencers half gone and its slider worn thin; an old one with no silencers and its slider worn to
    /// almost nothing (bare metal rang the case at 134 dBA: even old devices keep a slider).
    /// </summary>
    /// The last figure is the closer's latch-zone speed at the latch edge, m/s. The ADA setting (0.05-0.08)
    /// creeps; a fire door in use is set to shut itself firmly and comes in at 0.25-0.8, and that clunk is
    /// what Cody expects of one ("they should close on their own and clunk, not sound thin").
    private static (double SilencerMm, double Bumper, double MetalLambda, double LatchSpeed) Character(int variant) => (variant % Variants) switch
    {
        0 => (2.5, 1.5e8, 0.3, 0.25),
        1 => (2.5, 2e8, 0.3, 0.35),
        2 => (1.2, 3e8, 0.2, 0.5),
        _ => (0.0, 8e8, 0.1, 0.8),
    };

    /// <summary>Opening: shove the bar, the bolt draws back, the door goes, the bar is let go at 20 degrees.</summary>
    public static float[] RenderOpen(Door door, int sampleRate, double swingSeconds = 1.4, Report? report = null)
    {
        var sim = new Sim(door, sampleRate, report);
        sim.StartShut();
        sim.ScriptOpen(swingSeconds);
        return sim.Output();
    }

    /// <summary>
    /// Shutting on the closer: the last few degrees at the latch valve's speed (the whole swing would take
    /// <paramref name="latchSwingSeconds"/>), into the latch and the silencers. It starts a few hundredths
    /// of a second before the bolt touches, so the server sends it when the leaf arrives.
    /// </summary>
    public static float[] RenderClose(Door door, int sampleRate, double latchSwingSeconds = 1.4, Report? report = null)
    {
        var sim = new Sim(door, sampleRate, report);
        sim.ScriptCloserLatch();
        return sim.Output();
    }

    // ── The game ─────────────────────────────────────────────────────────────────────────────────

    public const string KeyPrefix = "pushbardoor:";

    /// <summary>Declared levels, dB at a metre, by character: the render's peak, which is what its buffer's
    /// full scale stands for (a world sound's level is its full scale; see <see cref="KnobDoor.OpenLevelDb"/>).
    /// Measured at the lab's 1.0 by 2.1 m leaf (AudioLab --heard-levels survey, 2026-10-04); the client puts
    /// each render's own peak in its place. Opening is the push; closing is the closer bringing it in, from
    /// a well-set closer (quiet) to a fast one onto bare steel. These were the model's LAFmax less a 20 dB
    /// calibration, which with the crack's 20-28 dB over its LAFmax played a push 40 dB under the model.</summary>
    public static float OpenLevelDb(int variant) => (variant % Variants) switch
    {
        0 => 115.8f, 1 => 116.7f, 2 => 116.4f, _ => 123.0f,
    };
    public static float CloseLevelDb(int variant) => (variant % Variants) switch
    {
        0 => 117.1f, 1 => 125.4f, 2 => 131.6f, _ => 138.2f,
    };

    /// <summary>What the model puts at a metre, LAFmax: what is heard on "real" a metre off. The model's own;
    /// the one published figure (a patent: ordinary exit devices 73-79 for the push) is some 20 dB under
    /// the push, which is the physics' to answer for, not a calibration's.</summary>
    public static float OpenLafDb(int variant) => (variant % Variants) switch
    {
        0 => 95.3f, 1 => 96.5f, 2 => 98.0f, _ => 101.5f,
    };
    public static float CloseLafDb(int variant) => (variant % Variants) switch
    {
        0 => 89.2f, 1 => 99.7f, 2 => 107.8f, _ => 114.9f,
    };

    public static string Key(bool closing, int variant, float swingSeconds, float width, float height)
        => FormattableString.Invariant(
            $"{KeyPrefix}{(closing ? "close" : "open")}:{((variant % Variants) + Variants) % Variants}:{(int)MathF.Round(swingSeconds * 100f)}:{(int)MathF.Round(width * 100f)}:{(int)MathF.Round(height * 100f)}");

    public static bool TryParseKey(string? key, out bool closing, out Door door, out float swingSeconds)
    {
        closing = false; door = new Door(); swingSeconds = 1.4f;
        if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
        var p = key.Substring(KeyPrefix.Length).Split(':');
        if (p.Length != 5 || (p[0] != "open" && p[0] != "close")) return false;
        if (!int.TryParse(p[1], out int v) || !int.TryParse(p[2], out int s) || !int.TryParse(p[3], out int w)
            || !int.TryParse(p[4], out int h)) return false;
        closing = p[0] == "close";
        swingSeconds = Math.Clamp(s / 100f, 0.3f, 5f);
        door = new Door { Variant = v, Seed = 1 + v, Width = Math.Clamp(w / 100f, 0.5f, 1.5f), Height = Math.Clamp(h / 100f, 1.5f, 3f) };
        return true;
    }

    /// <summary>The sound a key names, peak one.</summary>
    public static float[] RenderKey(string key, int sampleRate) => RenderKey(key, sampleRate, out _);

    /// <summary>The same, and its own peak, dB SPL at a metre: the level its full scale stands for.</summary>
    public static float[] RenderKey(string key, int sampleRate, out float fullScaleDb)
    {
        fullScaleDb = 0f;
        if (!TryParseKey(key, out bool closing, out var door, out float swing)) return new float[16];
        float[] pcm = closing ? RenderClose(door, sampleRate, swing) : RenderOpen(door, sampleRate, swing);
        return KnobDoor.PeakToFullScale(pcm, PascalsAtFullScale, out fullScaleDb);
    }

    // ── Constants, each a property of a part ─────────────────────────────────────────────────────

    // Leaf: 1.2 mm steel skins, 44 mm between their centres, on welded steel stiffeners (hat sections
    // every 150 mm, the usual fire door core), edge channels and stiffeners 7 kg. Its shear stiffness
    // is that of the stiffeners, about 500 MPa through the depth: a paper honeycomb (40 MPa) would put
    // nearly every mode in the core's shear and crowd sixteen thousand of them under 14 kHz.
    private const double SkinE = 200e9, SkinRho = 7850, SkinT = 0.0012, SkinSpacing = 0.0444, CoreShearModulus = 500e6;
    private const double CoreAndChannelsKg = 7.0;
    /// <summary>Steel skins bonded to their stiffeners: the adhesive takes a little; steel doors ring.</summary>
    private const double LeafLoss = 0.008, MountingLoss = 0.005, LeafModeMaxHz = 10000, SkinCrossoverHz = 1500;
    /// <summary>The patch of skin and edge channel a hard blow moves before the rest of the leaf knows:
    /// bending waves in 1.2 mm steel cover about 16 mm in the 50 us of a steel contact, so about 10 g,
    /// on the leaf through the skin's own bending, a resonance near 8 kHz.</summary>
    private const double SkinPatchMass = 0.01, SkinPatchStiffness = 2.5e7, SkinPatchTravel = 0.0001;
    /// <summary>The skin between the edge channel and the first stiffener, and its loss: bonded steel.</summary>
    private const double SkinPanelWidth = 0.15, SkinLoss = 0.01;

    // Silencers: neoprene domes 12 mm across, E about 5 MPa; three on the strike jamb.
    private static readonly double[] SilencerHeights = { 0.35, 1.05, 1.75 };
    private const double SilencerK = 6.9e5, SilencerLambda = 1.5;
    private const double SteelContactK = 2e10, SteelContactLambda = 0.2;

    // Rim latch: 30 g bolt, 16 mm throw, stiff spring.
    /// <summary>A Von Duprin 99-type rim latch: 3/4 in throw.</summary>
    private const double BoltMass = 0.03, Throw = 0.019, SpringPreload = 8, SpringRate = 500;
    private const double LatchGap = 0.004, LatchHeight = 1.0;
    /// <summary>Where the drawn-back bolt meets the back of its case: 2 mm out, inside the linkage's reach,
    /// so a shoved bar drives the bolt into it.</summary>
    private const double RetractedAt = 0.002;
    private const double MetalContactK = 4e9, MetalContactLambda = 0.05, BoltStopLambda = 0.3;
    /// <summary>The bolt's side give in its rim case, and the steel rim strike on the steel frame.</summary>
    private const double BoltSideStiffness = 2e7, StrikeMass = 0.1, StrikeMountStiffness = 5e7;
    private const double LatchCaseMass = 0.25, LatchCaseStiffness = 3e7, LatchCaseDamping = 2500, LatchCaseArea = 0.12 * 0.05;
    private const double LatchBending = 0.1;   // a rim case is on the face: its blows are not in the leaf's plane

    // The push bar: touchpad 0.35 kg, 14 mm travel, 4 mm play, return spring 20 N preload and 2 N/mm.
    /// <summary>The touchpad: 19 mm of stroke (3-13/16 in to 3-1/16 in projection), 4 mm of it taking up the
    /// linkage before the latch moves. Its return spring starts at 10 N and reaches about 25 N at the bottom:
    /// the whole push may need no more than 67 N with the latch's spring (A156.3). (At 58 N it threw the pad
    /// back at nearly 3 m/s and the release was 15 dB over the push, where real ones are the other way.)</summary>
    private const double BarMass = 0.2, BarTravel = 0.019, BarPlay = 0.004, BarPreload = 10, BarRate = 800;
    /// <summary>The moulded pad: ABS or nylon, about 2.5 GPa and 1150 kg/m3, losing about 0.03.</summary>
    private const double PadE = 2.5e9, PadRho = 1150, PadLoss = 0.03;
    /// <summary>Inside the case the pad drives a steel drive bar (0.2 kg) through bell cranks with a
    /// millimetre of play; the drive bar has its own stops at both ends of its stroke. Its knock against
    /// them, a moment apart from the pad's, is the second half of the ka-chunk.</summary>
    private const double DriveBarMass = 0.2, DriveBarPlay = 0.001, DriveBarLink = 2e5;
    /// <summary>The cranks' pivots are greased and the return is damped with the pad's: the drive bar does
    /// not swing on and drag the pad back off its stop (it did, three times, 30 ms apart).</summary>
    private const double CrankDamping = 0.7;
    /// <summary>The air in the hollow case: its length modes (0.8 m, from 214 Hz) and its cross modes (845,
    /// 2450, 2860 Hz), pumped by the walls when the mechanism knocks and heard through the pad's slot.</summary>
    private static readonly double[] CaseAirHz = { 214, 428, 642, 845, 856, 1070, 1284, 1498, 2450, 2860 };
    /// <summary>The air in the case leaks out of the pad's slot and round the mechanism that fills it: its
    /// modes are broad (0.05 rang as clean tones).</summary>
    private const double CaseAirLoss = 0.2, CaseToAir = 0.02, CaseSlotArea = 0.6 * 0.005;

    /// <summary>What holds a patch of the case's wall where the stops are: the stops sit on the chassis
    /// bolted through the door, so the blow goes into the door, N/m.</summary>
    private const double PortStiffness = 2e7, ChassisPatch = 0.1;
    /// <summary>Plastic on steel: E* about 2.8 GPa on a 3 mm rib edge gives about 2e8 N/m^1.5; a plastic
    /// gives back about half its speed (0.5 s/m).</summary>
    private const double PlasticLambda = 0.25;
    /// <summary>The pad rides its guide rails with a quarter of a millimetre of play either side: a loose bar,
    /// which knocks on its rails when it is shoved and when it lands. A hand never pushes quite square: a
    /// tenth of the push goes sideways, and a landing jolts it sideways by a fifth of the blow.</summary>
    private const double GuidePlay = 0.00025, SidePush = 0.1, SideJolt = 0.2, RailGrease = 0.3;
    /// <summary>The case is not an empty sheet: the drive bar, cranks, springs and their grease touch its
    /// walls, which takes its ring within a few tenths of a second (a bare sheet's 0.005 rang on and on).</summary>
    private const double MechanismLoss = 0.01;
    /// <summary>The pad lands on its stops through a damped slider, both ways: one clack, not a rattle.
    /// Cody: "the bar release on the opening rattles too much", "it needs a shorter clack".</summary>
    private const double ReturnDamping = 0.5;
    private const double LinkStiffness = 1e6, LinkDamping = 80;
    /// <summary>The case's two end brackets across the leaf, and how far across its push acts.</summary>
    private const double MountNear = 0.3, MountFar = 0.9;
    // The case: a 0.75 m pressed steel channel between screws 0.25 m apart; the bar an aluminium extrusion.
    private const double CaseLoss = 0.02, BarLoss = 0.01;

    // The frame: pressed 1.5 mm steel channel, about 2e-7 m^4, 2.9 kg/m, anchored every 0.7 m; it radiates
    // as its 0.1 m face.
    // Anchored in masonry and partly filled with mortar where it is grouted: it does not ring like a bell.
    private const double FrameEI = 40000, FrameKgPerM = 2.9, FrameSpan = 0.7, FrameFace = 0.1, FrameLoss = 0.04;
    /// <summary>The frame's own thin walls: 1.5 mm steel, its 0.1 m face between anchors. Their dense high
    /// modes are the clang of a hollow metal frame; the beam's few modes cannot carry it. Part grouted.</summary>
    private const double FrameWallT = 0.0015, FrameWallLoss = 0.02;

    // The closer: size 3, 18 N m at the latch and rising 6 N m per radian open; its latch valve sets the
    // speed in the last ten degrees.
    /// <summary>A size 3-4 closer: 35 N m at the latch, enough to drive a 3/4 in latch home against the
    /// up to 20 N it may take (A156.3) with friction on top.</summary>
    private const double CloserTorque = 35, CloserRate = 6, CloserShoeX = 0.25;
    private const double CloserOpening = 2;   // N m s: the check valve lets it open easily
    /// <summary>The hiss of the closer's oil through its latch valve at 0.1 rad/s, Pa at a metre: about 35 dB.</summary>
    private const double CloserHissPa = 0.001;

    /// <summary>What a hand will put on a bar, N: a door pressed on its latch takes more than the 67 N an
    /// unloaded device is allowed to need.</summary>
    private const double HandPushRamp = 0.05, HandPush = 250, HandHold = 40;
    /// <summary>The hand meets the bar through the palm, about 50 N/mm when it shoves, and the arm drives
    /// it at 1.5 m/s, a 19 mm stroke in about 15 ms, until the palm carries what the arm wants: a person
    /// going through a fire door shoves the bar, and its bottoming is a clack.</summary>
    private const double PalmStiffness = 5e4, PalmDamping = 150, ArmSpeed = 1.5;

    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Sim
    {
        private readonly Door door;
        private readonly int rate;
        private readonly double dt;
        private readonly Report? report;
        private readonly Random rng;
        private readonly double width, height, mass, inertia;
        private readonly double silencer, bumper, metalLambda, keeperPlay, latchSpeed;
        private double theta, omega;

        private readonly Modes leaf, frame, strike, caseAir;
        private readonly DenseField caseField, padField, skinField;
        private readonly double[] caseHit, padHit, driveHit;
        private readonly AccelerationNoise padNoise, driveNoise, boltNoise, strikeNoise;
        private double driveBar, driveRate, lastBarRate, hissState, boltAccNow;
        private double padSide, padSideRate, padSideAcc, sideSign = 1;
        private double[] padSideHit = Array.Empty<double>(), railHit = Array.Empty<double>();
        private AccelerationNoise padSideNoise = null!;
        private Port padSidePort = null!;
        private readonly SmallRadiator slotSound;
        private readonly Port padPort, casePad, caseDrive2;
        private readonly HighPass[] skinHigh;
        private readonly double[] latchShape, nearShape, farShape, shoeShape;
        private readonly (double X, double Y, double Warp, bool Rubber, double[] Shape, double[] Frame)[] stops;
        private readonly Mount[] skinPatches;
        private readonly double[] frameAtLatch;
        private readonly Modes frameWall;
        private readonly Plate frameWallPlate;
        private readonly double rigidGain;
        private double rigidLow;

        private double bolt = Throw, boltRate, bar, barRate;
        private bool boltInStrike = true;
        private LuGre keeperFriction;
        private readonly Mount boltSide, strikeBody, latchCase;
        private readonly SmallRadiator strikeSound, latchSound;
        private readonly double[] ones = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };

        private double handForce, handX, handV, lastAlpha;
        private Func<double, (double Angle, double Rate)>? leafPath;
        private double handK, handC;
        private bool closerLatchValve;
        private double latchDamping;
        private double time;

        private readonly List<float> outHi = new();
        private readonly double[] skinMid;
        private readonly double[][] skinAtStops;
        private readonly double[] skinAtLatch;
        private double barAccLow;
        private readonly Dictionary<string, (double Start, double Peak, bool On)> contactLog = new();
        private readonly double[] peaks = new double[7];
        private static readonly string[] PeakNames = { "leaf", "piston", "frame", "strike", "latch", "bar", "case" };
        private List<float>[]? stems;

        public Sim(Door door, int sampleRate, Report? report)
        {
            this.door = door; this.report = report;
            rate = sampleRate * Oversample; dt = 1.0 / rate;
            rng = new Random(door.Seed);
            width = door.Width; height = door.Height;
            (double silencerMm, bumper, metalLambda, latchSpeed) = Character(door.Variant);
            silencer = silencerMm / 1000;
            // The strike is set so the bolt drops in as the silencers take the closer's push.
            keeperPlay = Math.Max(silencer - 0.0004, 0.001);

            double area = width * height;
            mass = 2 * SkinT * SkinRho * area + CoreAndChannelsKg;
            double rhoH = mass / area;
            double d = 2 * SkinE * SkinT * (SkinSpacing / 2) * (SkinSpacing / 2) / (1 - Poisson * Poisson);
            inertia = mass * width * width / 3;
            var plate = new Plate(width, height, d, rhoH, LeafLoss + MountingLoss, LeafModeMaxHz, true, rng, 0.03,
                                  CoreShearModulus * SkinSpacing);
            // A cored steel door's loss (0.05 below 500 Hz, falling as 1/f), not a bare sheet's: it clunks,
            // it does not bong.
            for (int k = 0; k < plate.Loss.Count; k++) plate.Loss[k] += SandwichLoss(plate.Hz[k]) - LeafLoss;
            leaf = new Modes(plate.Hz, plate.Loss, plate.Mass, plate.Gain, dt, plate.GainQuad);
            rigidGain = Rho0 / (2 * Math.PI) * height * width * width / 2;
            latchShape = plate.Shape(width - 0.03, LatchHeight);
            nearShape = plate.Shape(MountNear, LatchHeight);
            farShape = plate.Shape(MountFar, LatchHeight);
            shoeShape = plate.Shape(CloserShoeX, height - 0.05);

            frame = StripModes(dt, rng);
            double wallD = SkinE * Math.Pow(FrameWallT, 3) / (12 * (1 - Poisson * Poisson));
            frameWallPlate = new Plate(FrameFace, FrameSpan, wallD, FrameWallT * SkinRho, FrameWallLoss, 16000, false, rng, 0.03,
                                       0, 1000, 3);
            frameWall = new Modes(frameWallPlate.Hz, frameWallPlate.Loss, frameWallPlate.Mass, frameWallPlate.Gain, dt, frameWallPlate.GainQuad);
            frameAtLatch = FrameShape(LatchHeight);
            var list = new List<(double, double, double, bool, double[], double[])>();
            foreach (double y in SilencerHeights)
                list.Add((width, y, (rng.NextDouble() - 0.4) * 0.0008, true, plate.Shape(width, y), FrameShape(y)));
            foreach (double x in new[] { 0.85 * width, 0.5 * width })
                list.Add((x, height - 0.01, silencer + (rng.NextDouble() - 0.4) * 0.0008, false, plate.Shape(x, height - 0.01), FrameShape(height)));
            stops = list.ToArray();
            skinPatches = new Mount[stops.Length];
            for (int i = 0; i < stops.Length; i++) skinPatches[i] = new Mount(SkinPatchMass, SkinPatchStiffness, 0.1);

            // The exit device is a hollow box: a 2 mm aluminium case (about 0.2 m2 of wall) over the
            // mechanism, and a pressed aluminium pad. Their walls ring at a thin plate's own density, with
            // a bare panel's loss, and the air inside the case has its own modes.
            // The mechanism case is pressed steel, 1.5 mm, with the mechanism in it; the pad an aluminium
            // extrusion with 3 mm walls. (Thin aluminium for both rang long and high: "a spoon in the sink".)
            // Both at their true density (a mode every 23 Hz on the case): thinned to one in 40 Hz and left to
            // ring, the modes stood apart as notes.
            caseField = new DenseField(0.25, 0.8, 0.0015, SkinE, SkinRho, Poisson, f => Math.Max(ThinPanelLoss(f), MechanismLoss), 300, 16000, rng, dt, 15);
            // The pad is moulded plastic (Cody: "the push bar that gets pushed in is usually plastic, the
            // casing around the pusher is usually metal"): 3 mm walls, a plastic's own loss.
            padField = new DenseField(0.06, 0.6, 0.003, PadE, PadRho, 0.38, _ => PadLoss, 300, 16000, rng, dt, 15);
            caseHit = caseField.Point(); padHit = padField.Point(); driveHit = caseField.Point();
            var airLoss = new double[CaseAirHz.Length]; var airMass = new double[CaseAirHz.Length]; var airGain = new double[CaseAirHz.Length];
            for (int i = 0; i < CaseAirHz.Length; i++) { airLoss[i] = CaseAirLoss; airMass[i] = 0.002; airGain[i] = SmallPlateGain(CaseSlotArea, 1); }
            caseAir = new Modes(CaseAirHz, airLoss, airMass, airGain, dt);
            padNoise = new AccelerationNoise(BarMass / PadRho, dt);
            padSideNoise = new AccelerationNoise(BarMass / PadRho, dt);
            padSideHit = padField.Point(); railHit = caseField.Point();
            padSidePort = new Port(Math.Max(padField.PatchMass, 0.02), PortStiffness, padField.Impedance);
            sideSign = rng.NextDouble() < 0.5 ? -1 : 1;
            driveNoise = new AccelerationNoise(DriveBarMass / 7850, dt);
            boltNoise = new AccelerationNoise(BoltMass / 7850, dt);
            strikeNoise = new AccelerationNoise(StrikeMass / 7850, dt);
            slotSound = new SmallRadiator(CaseSlotArea, dt);
            padPort = new Port(padField.PatchMass, PortStiffness, padField.Impedance);
            // The stops sit on the device's steel chassis, not on its thin cover: the patch a stop moves is the
            // chassis under it, a tenth of a kilogram.
            casePad = new Port(ChassisPatch, PortStiffness, caseField.Impedance);
            caseDrive2 = new Port(ChassisPatch, PortStiffness, caseField.Impedance);
            // The rim strike: a steel block on the frame; what rings is its lip.
            strike = new Modes(new[] { Beam(0.012, 0.003, 7850, 200e9, 1.875) }, new[] { 0.03 }, new[] { 0.01 },
                               new[] { SmallPlateGain(0.012 * 0.03, 0.6) }, dt);

            boltSide = new Mount(BoltMass, BoltSideStiffness, 0.2);
            strikeBody = new Mount(StrikeMass, StrikeMountStiffness, 0.15);
            latchCase = new Mount(LatchCaseMass, LatchCaseStiffness, LatchCaseDamping / (2 * Math.Sqrt(LatchCaseStiffness * LatchCaseMass)));
            strikeSound = new SmallRadiator(0.03 * 0.08, dt);
            latchSound = new SmallRadiator(LatchCaseArea, dt);
            keeperFriction = new LuGre { MuStatic = 0.4, MuSliding = 0.25, StribeckSpeed = 0.01, Viscous = 0 };

            // The leaf's skins: 1.2 mm steel, a mode every 1.7 Hz, overlapping (modal overlap 2-3) from
            // 100 Hz up: the tinny wash of a steel door. Above 1.5 kHz, where the sandwich's own modes stop.
            skinField = new DenseField(width, height, SkinT, SkinE, SkinRho, Poisson, SandwichLoss, SkinCrossoverHz, 16000, rng, dt);
            skinMid = skinField.Point();
            skinAtLatch = skinField.Point();
            skinAtStops = new double[stops.Length][];
            for (int i = 0; i < stops.Length; i++) skinAtStops[i] = skinField.Point();
            skinHigh = new HighPass[stops.Length + 2];
            for (int i = 0; i < skinHigh.Length; i++) skinHigh[i] = new HighPass(SkinCrossoverHz, rate);
            handK = 170 * inertia;
            handC = 2 * 0.7 * Math.Sqrt(handK * inertia);
        }

        /// <summary>A bar or channel standing in the open: it radiates as its swept area only where it is
        /// a wavelength across; below that air slips round its edges, as it does round the frame's face.</summary>
        private static Modes BeamModes(double[] betaL, double span, double ei, double mu, double loss, double area, double dt)
        {
            double halfWidth = area / span / 2;
            var hz = new double[betaL.Length]; var m = new double[betaL.Length];
            var l = new double[betaL.Length]; var g = new double[betaL.Length];
            for (int i = 0; i < betaL.Length; i++)
            {
                hz[i] = betaL[i] * betaL[i] / (2 * Math.PI * span * span) * Math.Sqrt(ei / mu);
                m[i] = 0.4 * mu * span; l[i] = loss;
                double ka = 2 * Math.PI * hz[i] / C0 * halfWidth;
                g[i] = SmallPlateGain(area, 0.4 / (i + 1)) * ka / Math.Sqrt(1 + ka * ka);
            }
            return new Modes(hz, l, m, g, dt);
        }

        /// <summary>The frame channel between anchors, as a clamped beam radiating as its face.</summary>
        private static Modes StripModes(double dt, Random rng)
        {
            var hz = new List<double>(); var l = new List<double>(); var m = new List<double>(); var g = new List<double>();
            for (int n = 1; n < 40; n++)
            {
                double bl = (n + 0.5) * Math.PI;
                double f = bl * bl / (2 * Math.PI * FrameSpan * FrameSpan) * Math.Sqrt(FrameEI / FrameKgPerM);
                if (f > 8000) break;
                f *= 1 + 0.03 * (rng.NextDouble() * 2 - 1);
                hz.Add(f); m.Add(FrameKgPerM * FrameSpan / 2); l.Add(FrameLoss);
                double ka = 2 * Math.PI * f / C0 * FrameFace;
                g.Add(Rho0 / (2 * Math.PI) * FrameFace * FrameSpan * 0.5 / n * ka / Math.Sqrt(1 + ka * ka));
            }
            return new Modes(hz, l, m, g, dt);
        }

        private readonly Dictionary<double, double[]> wallShapes = new();
        /// <summary>Where on the frame's wall a blow lands: near the stop's rebate, at its height within
        /// its span between anchors.</summary>
        private double[] WallShape(double y)
        {
            if (!wallShapes.TryGetValue(y, out var s))
                wallShapes[y] = s = frameWallPlate.Shape(0.03, Math.Clamp(y % FrameSpan, 0.05, FrameSpan - 0.05));
            return s;
        }

        private double[] FrameShape(double y)
        {
            var s = new double[frame.N];
            double local = (y % FrameSpan) / FrameSpan;
            for (int n = 0; n < s.Length; n++) s[n] = Math.Sin((n + 1) * Math.PI * Math.Clamp(local, 0.05, 0.95));
            return s;
        }

        // ── Scripts ──────────────────────────────────────────────────────────────────────────────

        public void StartShut()
        {
            // Resting on its silencers under the closer's push, the bolt in the strike; and nowhere in the
            // steel, wherever the leaf's warp puts its corners.
            theta = Math.Max(0, silencer - 0.00042) / width;
            foreach (var s in stops) theta = Math.Max(theta, -s.Warp / s.X + 1e-6);
            omega = 0; bolt = Throw; boltInStrike = true;
        }

        public void ScriptOpen(double swingSeconds)
        {
            const double reach = 0.02;
            double cleared = -1, released = -1, end = 10;
            double wRate = Math.PI / 2 / swingSeconds;
            while (time < end)
            {
                if (time > reach && cleared < 0)
                    handForce = Math.Min(1, (time - reach) / HandPushRamp) * HandPush;
                if (cleared < 0 && !boltInStrike && time > reach)
                {
                    cleared = time;
                    Log($"{time * 1000:F0} ms  bolt clear; the door goes");
                    double a0 = theta, t0 = time;
                    leafPath = t => (a0 + wRate * (t - t0), wRate);
                }
                if (cleared > 0 && released < 0)
                {
                    // Walking through: the hand keeps the bar down and the door going at the server's pace.
                    var (a, r) = leafPath!(time);
                    double need = (handK * (a - theta) + handC * (r - omega) + CloserTorque + CloserRate * theta) / (0.5 * (MountNear + MountFar));
                    handForce = Math.Clamp(need, HandHold, 200);
                    // The hand comes off the bar once the door is going, as in the recordings: about a
                    // quarter of a second after the bolt clears.
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

        public void ScriptCloserLatch()
        {
            // Fifteen millimetres before the bevel meets the lip, coming in at the latch valve's speed
            // under the closer: at a right setting the bevel scrapes for a quarter of a second.
            theta = (keeperPlay + (Throw - LatchGap) + 0.015) / width;
            double latchRate = latchSpeed / width;
            omega = -latchRate;
            closerLatchValve = true;
            latchDamping = (CloserTorque + CloserRate * theta) / latchRate;
            bolt = Throw; boltInStrike = false;
            double firstHit = -1, end = 3;
            while (time < end)
            {
                if (firstHit < 0 && (contactLog.ContainsKey("bevel") || contactLog.ContainsKey("silencer")))
                {
                    firstHit = time; end = time + 0.9 + (Throw + 0.015) / latchSpeed;
                }
                Tick(opening: false);
            }
        }

        // ── One step ─────────────────────────────────────────────────────────────────────────────

        private void Tick(bool opening)
        {
            double torque = 0, latchEdgeForce = 0, frameLatch = 0;

            // The closer, always: its spring toward shut, its oil against motion.
            double closer = -(CloserTorque + CloserRate * Math.Max(0, theta));
            if (omega > 0) closer -= CloserOpening * omega;
            else if (closerLatchValve) closer -= latchDamping * omega;
            torque += closer;
            leaf.Push(shoeShape, closer / CloserShoeX);
            double faceForce = closer / CloserShoeX;

            // The stop: silencers, then steel.
            bool near = theta * width < 0.02;
            double silencerSum = 0, steelSum = 0;
            if (near)
                for (int i = 0; i < stops.Length; i++)
                {
                    var s = stops[i];
                    // The stop meets the skin patch, which gives before the leaf does.
                    var patch = skinPatches[i];
                    double pos = s.X * theta + leaf.At(s.Shape) + s.Warp + patch.X;
                    double vel = s.X * omega + leaf.RateAt(s.Shape) + patch.V;
                    double f = 0;
                    if (s.Rubber && silencer > 0)
                    {
                        double fr = Contact(SilencerK, SilencerLambda, silencer - pos, -vel);
                        f += fr; silencerSum += fr;
                    }
                    double fs = Contact(SteelContactK, SteelContactLambda, -pos, -vel);
                    f += fs; steelSum += fs;
                    if (f > 0)
                    {
                        patch.F += f;
                        faceForce += f;

                        frame.Push(s.Frame, -f);
                        frameWall.Push(WallShape(s.Y), -f);
                    }
                }
            for (int i = 0; i < stops.Length; i++)
            {
                // A tenth of a millimetre in, the skin is on its edge channel and the channel takes it.
                var sp = skinPatches[i];
                double bottom = Contact(SteelContactK, SteelContactLambda, Math.Abs(sp.X) - SkinPatchTravel, Math.Sign(sp.X) * sp.V);
                sp.F -= Math.Sign(sp.X) * bottom;
                double r = sp.Reaction + Math.Sign(sp.X) * bottom;
                if (r == 0) continue;
                torque += r * stops[i].X;
                leaf.Push(stops[i].Shape, r);
            }
            Note("silencer", silencerSum);
            Note("steel", steelSum);

            // The latch, as on the knob door but heavier.
            double latchW = near ? leaf.At(latchShape) : 0, latchWRate = near ? leaf.RateAt(latchShape) : 0;
            double edge = width * theta + latchW, edgeRate = width * omega + latchWRate;
            double boltForce = SpringPreload + SpringRate * (Throw - bolt);
            double strikeForce = 0;
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
                    Note("keeper", fk);
                }
            }
            else if (boltInStrike) { Note("keeper", 0); boltInStrike = false; Log($"{time * 1000:F1} ms  bolt in"); }

            // The push bar and its linkage.
            double palm = 0;
            if (handForce > 0)
            {
                palm = Math.Max(0, PalmStiffness * (handX - bar) + PalmDamping * (handV - barRate));
                handV = ArmSpeed * Math.Clamp(2 * (handForce - palm) / handForce, 0, 1);
                handX += handV * dt;
            }
            else { handX = Math.Min(handX, bar - 0.005); handV = 0; }
            // The bar rides on the leaf: when the leaf is stopped dead, the bar is thrown on its stops.
            // Where the case pushes, and only the leaf's slower motion: that is what throws the bar.
            double leafAccAtBar = lastAlpha * 0.5 * (MountNear + MountFar);
            for (int k = 0; k < leaf.N; k++) leafAccAtBar += 0.5 * (nearShape[k] + farShape[k]) * leaf.Acc[k];
            barAccLow += (1 - Math.Exp(-2 * Math.PI * 500 * dt)) * (leafAccAtBar - barAccLow);
            leafAccAtBar = barAccLow;
            double barForce = palm - (BarPreload + BarRate * Math.Max(0, bar)) - BarMass * leafAccAtBar;
            double caseForce = BarPreload + BarRate * Math.Max(0, bar);           // on the case, toward the leaf: opens
            // The pad drives the drive bar through the cranks' play; the drive bar draws the bolt.
            double driveForce = -DriveBarMass * leafAccAtBar;
            double rel = bar - driveBar, relDepth = Math.Abs(rel) - DriveBarPlay;
            double fCrank = relDepth > 0 ? Math.Sign(rel) * Math.Max(0, DriveBarLink * relDepth
                            + 2 * CrankDamping * Math.Sqrt(DriveBarLink * DriveBarMass) * Math.Sign(rel) * (barRate - driveRate)) : 0;
            barForce -= fCrank; driveForce += fCrank;
            Note("crank", Math.Abs(fCrank));
            double ratio = Throw / (BarTravel - BarPlay);
            double drawn = Math.Clamp((driveBar - BarPlay) * ratio, 0, Throw);
            double linkDepth = bolt - (Throw - drawn);
            double fLink = linkDepth > 0 && driveBar > BarPlay
                ? Math.Max(0, LinkStiffness * linkDepth + LinkDamping * (boltRate + (drawn < Throw ? driveRate * ratio : 0))) : 0;
            boltForce -= fLink;
            driveForce -= fLink * ratio;
            caseForce += fLink * ratio;
            Note("link", fLink);
            // The pad's stops are on the case's thin wall and the pad is a thin pressing: each side of the
            // blow is a patch that gives and passes into its panel through the panel's impedance.
            double padAt = bar + padPort.X, padAtRate = barRate + padPort.V;
            double fIn = BarStop(padAt - BarTravel - casePad.X, padAtRate - casePad.V);
            double fOut = BarStop(casePad.X - padAt, casePad.V - padAtRate, ReturnDamping);
            double padDrive = padPort.Step(fOut - fIn, dt, out double padHost);
            barForce += padHost;
            double caseDrive = casePad.Step(fIn - fOut, dt, out double caseHost);
            caseForce += caseHost;
            double dIn = BarStop(driveBar - BarTravel - caseDrive2.X, driveRate - caseDrive2.V);
            double dOut = BarStop(caseDrive2.X - driveBar, caseDrive2.V - driveRate, ReturnDamping);
            driveForce += dOut - dIn;
            double caseDrive2Force = caseDrive2.Step(dIn - dOut, dt, out double caseHost2);
            caseForce += caseHost2;
            Note("bar-bottom", fIn); Note("bar-back", fOut); Note("drive-stop", dIn + dOut);
            padField.Modes.Push(padHit, padDrive);

            // The loose pad on its rails: pushed a little sideways by the hand and jolted sideways by every
            // landing, it knocks across its play.
            double side = SidePush * palm * sideSign + SideJolt * (fIn + fOut) * sideSign;
            double sideAt = padSide + padSidePort.X, sideAtRate = padSideRate + padSidePort.V;
            double guide = Contact(bumper, PlasticLambda, Math.Abs(sideAt) - GuidePlay, Math.Sign(sideAt) * sideAtRate);
            // The rails are greased: a loose pad knocks once or twice, it does not chatter on.
            double onPad = side - 2 * RailGrease * Math.Sqrt(1e4 * BarMass) * padSideRate;
            double sideDrive = padSidePort.Step(-Math.Sign(sideAt) * guide, dt, out double sideHost);
            onPad += sideHost;
            padSideAcc = onPad / BarMass;
            padSideRate += padSideAcc * dt; padSide += padSideRate * dt;
            padField.Modes.Push(padSideHit, sideDrive);
            Note("rail", guide);
            caseField.Modes.Push(caseHit, caseDrive);
            caseField.Modes.Push(driveHit, caseDrive2Force);
            // The walls pump the air inside; it answers at the case's own air modes.
            caseAir.Push(ones, (Math.Abs(caseDrive) + Math.Abs(caseDrive2Force)) * CaseToAir);
            driveRate += driveForce / DriveBarMass * dt; driveBar += driveRate * dt;
            double driveAcc = driveForce / DriveBarMass;

            // The case pushes the leaf at its two brackets, square to the face.
            torque += caseForce * 0.5 * (MountNear + MountFar);
            leaf.Push(nearShape, caseForce * 0.5); leaf.Push(farShape, caseForce * 0.5);
            faceForce += caseForce;


            // The bolt's own stops in its rim case: thrown out to full throw, and yanked back in against the
            // case's back when the bar is shoved (the "chunk" of the push: the pad's landing alone, under a
            // pressing palm, was 14 dB below the release).
            double fStop = Contact(MetalContactK, BoltStopLambda, bolt - Throw - latchCase.X, boltRate - latchCase.V);
            double fBack = Contact(MetalContactK, BoltStopLambda, -(bolt - RetractedAt) + latchCase.X, -(boltRate - latchCase.V));
            boltForce += fBack - fStop;
            latchCase.F += fStop - fBack + fLink * 0.5;
            Note("bolt-stop", fStop); Note("bolt-back", fBack);

            // Mounts into their hosts.
            latchEdgeForce += boltSide.Reaction;
            frameLatch += strikeBody.Reaction;
            torque += latchEdgeForce * width;
            leaf.Push(latchShape, latchEdgeForce + latchCase.Reaction * LatchBending);
            faceForce += latchEdgeForce + latchCase.Reaction * LatchBending;

            if (strikeForce != 0) strike.Push(ones, strikeForce);
            if (frameLatch != 0) { frame.Push(frameAtLatch, frameLatch); frameWall.Push(WallShape(LatchHeight), frameLatch); }

            // A hand on the leaf only while a script holds a path and the bar is not doing the work.
            // (Opening drives through the bar; closing is the closer's.)

            // Air.
            torque -= 0.5 * Rho0 * 1.2 * height * Math.Pow(width, 4) / 4 * omega * Math.Abs(omega);

            // Rigid motion.
            double alpha = torque / inertia;
            lastAlpha = alpha;
            omega += alpha * dt; theta += omega * dt;
            boltAccNow = (boltForce - 0.5 * Math.Tanh(boltRate / 0.01)) / BoltMass;
            boltRate += boltAccNow * dt; bolt += boltRate * dt;
            barRate += barForce / BarMass * dt; bar += barRate * dt;
            boltSide.Step(dt); strikeBody.Step(dt); latchCase.Step(dt);
            if (near) foreach (var patch in skinPatches) patch.Step(dt);

            // Radiate.
            double pLeaf = leaf.Step(), pFrame = frame.Step() + frameWall.Step(), pStrike = strike.Step() + strikeSound.Pressure(strikeBody.Acc);
            double barAcc = (barRate - lastBarRate) / dt; lastBarRate = barRate;
            double pLatch = latchSound.Pressure(latchCase.Acc) + boltNoise.Pressure(boltAccNow);
            double pBar = padField.Modes.Step() + padNoise.Pressure(barAcc) + padSideNoise.Pressure(padSideAcc) + driveNoise.Pressure(driveAcc);
            double pCase = caseField.Modes.Step() + caseAir.Step();
            pStrike += strikeNoise.Pressure(strikeBody.Acc);
            // The closer's oil through its latch valve: turbulence, its pressure going as the flow cubed.
            if (!opening && omega < 0)
            {
                double flow = -omega / 0.1;
                hissState = 0.9 * hissState + 0.1 * (rng.NextDouble() * 2 - 1);
                pCase += CloserHissPa * flow * flow * flow * ((rng.NextDouble() * 2 - 1) - hissState);
            }
            double corner = C0 / (2 * Math.PI * Math.Sqrt(width * height / Math.PI));
            rigidLow += (1 - Math.Exp(-2 * Math.PI * corner * dt)) * (alpha - rigidLow);
            double pRigid = rigidGain * rigidLow * Math.Clamp(1 - theta / 0.15, 0, 1);
            // The skins between the stiffeners: a thin steel sheet's dense high modes, the tinny ring of a
            // steel door, which the sandwich's modes cannot describe.
            // The skins ride on the leaf: where it moves fast, above where the sandwich's own modes stop,
            // they are driven by its motion there through their impedance.
            for (int i = 0; i < stops.Length; i++)
            {
                double v = stops[i].X * omega + leaf.RateAt(stops[i].Shape) + skinPatches[i].V;
                skinField.Modes.Push(skinAtStops[i], skinField.Impedance * skinHigh[i].Next(v));
            }
            double vLatch = width * omega + leaf.RateAt(latchShape);
            skinField.Modes.Push(skinAtLatch, skinField.Impedance * skinHigh[stops.Length].Next(vLatch));
            double vMid = 0.5 * (MountNear + MountFar) * omega + 0.5 * (leaf.RateAt(nearShape) + leaf.RateAt(farShape));
            skinField.Modes.Push(skinMid, skinField.Impedance * skinHigh[stops.Length + 1].Next(vMid));
            pLeaf += skinField.Modes.Step();
            double p = pLeaf + pFrame + pStrike + pLatch + pBar + pCase + pRigid;
            double[] parts = { pLeaf, pRigid, pFrame, pStrike, pLatch, pBar, pCase };
            for (int i = 0; i < parts.Length; i++) peaks[i] = Math.Max(peaks[i], Math.Abs(parts[i]));
            if (StemFolder != null)
            {
                stems ??= new List<float>[parts.Length];
                for (int i = 0; i < parts.Length; i++) (stems[i] ??= new List<float>()).Add((float)(parts[i] / PascalsAtFullScale));
            }
            outHi.Add((float)p);
            time += dt;
        }

        /// <summary>The bar on one of its stops: a urethane pad, or bare metal on a worn-out device.</summary>
        /// <summary>The plastic pad, or the steel drive bar, striking the metal chassis: a Hertz contact of the
        /// plastic's stiffness (the character sets how hard the plastic has aged), damped as plastic is.</summary>
        private double BarStop(double depth, double rate, double zeta = 0.3)
            => Contact(bumper, PlasticLambda * zeta / 0.3, depth, rate);

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
            if (StemFolder != null && stems != null)
                for (int i = 0; i < stems.Length; i++)
                    using (var f = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(StemFolder, "pb-" + PeakNames[i] + ".raw"))))
                        foreach (var v in stems[i]) f.Write(v);
            var y = Decimate(outHi, rate, PascalsAtFullScale, out double peak);
            if (report != null) report.PeakPascals = peak;
            return y;
        }
    }
}
