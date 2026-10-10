using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using static OpenFPS.Common.DoorPhysics;

namespace OpenFPS.Common;

/// <summary>
/// What strikes: a mass behind a contact. A Hertz contact (a fingertip, a knuckle, a palm, a shoe's toe, a
/// metal rod) takes its stiffness from the striker's modulus and radius and the struck thing's modulus, so a
/// finger on glass is a short hard contact and on rubber a long soft one; a body (a shoulder, a chest) is a
/// measured spring and damper. Its own volume radiates as it is stopped (acceleration noise).
/// </summary>
/// <param name="Name">The preset's name, as keys carry it.</param>
/// <param name="ModulusGPa">The striker's surface modulus, GPa (the softer of the two decides the contact).</param>
/// <param name="Restitution">How much of the closing speed it gives back, 0..1 (Flores et al. 2011's damping).</param>
/// <param name="LinearStiffness">A measured linear contact stiffness, N/m, in place of Hertz (a body); 0 for Hertz.</param>
/// <param name="LinearDamping">Its measured damping, N s/m.</param>
/// <param name="PadM">A soft pad over a hard core (skin over the bone of a knuckle), metres: once the pad is squeezed
/// this far the core meets the thing too, a second, stiff Hertz contact on what is left. Zero: no core.</param>
/// <param name="CoreModulusGPa">The core's modulus, GPa (bone, about 15).</param>
public readonly record struct Striker(string Name, float MassKg, float RadiusM, float ModulusGPa, float Poisson,
                                     float Restitution, float VolumeM3, float LinearStiffness = 0f, float LinearDamping = 0f,
                                     float PadM = 0f, float CoreModulusGPa = 0f)
{
    /// <summary>
    /// A fingertip's pad: the distal phalanx and the finger behind it moving (about 20 g, EST), an 8 mm pad
    /// on the registry's Skin modulus (1.5 MPa: Serina et al., J. Biomech. 30 (1997) 1035, put fingertip pulp at
    /// about 1-10 N/mm under a tap's force). A tap lasts a few milliseconds and excites only the low modes.
    /// </summary>
    public static Striker Fingertip => new("finger", 0.02f, 0.008f, 0.0015f, 0.49f, 0.3f, 0.02f / TissueKgM3);

    /// <summary>
    /// A knuckle: a millimetre of skin over the bone of the knuckle, the hand swinging from the wrist behind it.
    /// Its moving mass (60 g), the skin's stiffness (10 MPa) and the skin's thickness over the bone (1 mm) are
    /// FITTED to Cody's "Heavy Door Knocks" recording (inbox/door sounds, the spec DoorKnock was fitted to; 19
    /// knocks, the first 120 ms of each, as band shapes) against a knuckle on a solid wooden door, 2.0 x 0.9 m,
    /// 40 mm: 2.6 dB rms from 125 Hz to 16 kHz (`--struck fit`; 1.8 with twice the listed modes). The skin gives the body of the knock, about
    /// 3 ms; the bone through it the crack at 2-8 kHz. Without the bone the top was 12 dB short.
    /// </summary>
    public static Striker Knuckle => new("knuckle", KnuckleMassKg, 0.010f, KnuckleModulusGPa, 0.45f, 0.4f, KnuckleMassKg / TissueKgM3,
                                         PadM: KnucklePadM, CoreModulusGPa: BoneModulusGPa);

    /// <summary>
    /// A palm slap: the hand and forearm behind it (Dempster: hand 0.6 %, forearm 1.6 % of 78 kg, about 1 kg
    /// moving with the slap, EST), a wide pad (50 mm) of palm, 4 mm of it over the bones of the hand (EST), the
    /// skin 5 MPa (EST). A hard slap goes through the pad to the bone, as the knuckle's fit showed a knock does:
    /// the "hard contact of about a millisecond" the old bump lacked (todo, Bump sounds).
    /// </summary>
    public static Striker Palm => new("palm", 1.0f, 0.05f, 0.005f, 0.49f, 0.2f, 1.0f / TissueKgM3, PadM: 0.004f, CoreModulusGPa: BoneModulusGPa);

    /// <summary>The toe of a work boot meeting a wall: foot and shoe 1.1 kg (1.45 % of body mass, Dempster), the
    /// registry's BootRubber sole, a 30 mm toe cap.</summary>
    public static Striker Toe => new("toe", 1.1f, 0.03f, 0.20f, 0.48f, 0.3f, 1.2e-3f);

    /// <summary>
    /// A shoulder or a chest: measured on shoulder checks in ice hockey, 12.8 kN/m and 377 N s/m at an effective
    /// mass of 12.9 kg (Sports Biomechanics 23(10), doi 10.1080/14763141.2021.1951828; 14(1), doi
    /// 10.1080/14763141.2015.1025236): about 100 ms, a heavy soft blow.
    /// </summary>
    public static Striker Body => new("body", 12.9f, 0.19f, 0.0015f, 0.49f, 0.2f, 12.9f / TissueKgM3, 12_800f, 377f);

    /// <summary>A steel rod, 10 mm across and 300 mm long (185 g), its rounded end of 5 mm: a hard, short blow
    /// that reaches everything a thing has.</summary>
    public static Striker Rod => new("rod", 0.185f, 0.005f, 200f, 0.29f, 0.7f, 2.4e-5f);

    /// <summary>A walker's heel: a sixth of 78 kg (Footsteps.EffectiveMassFraction) on a trainer's heel (35 mm,
    /// the registry's Rubber). Only for the level anchor (<see cref="StruckThings.LevelAnchorDb"/>).</summary>
    public static Striker Heel => new("heel", Footsteps.EffectiveMassFraction * 78f, 0.035f, 0.02f, 0.48f, 0.3f, Footsteps.EffectiveMassFraction * 78f / TissueKgM3);

    /// <summary>Soft tissue's density, kg/m^3 (Duck 1990): what a body part's moving mass fills, so the volume
    /// whose stop radiates is the mass that stops.</summary>
    public const float TissueKgM3 = 1050f;

    /// <summary>Cortical bone's modulus, GPa (Currey, Bones, 2002: 15-20): the core under a knuckle's skin.</summary>
    public const float BoneModulusGPa = 15f;

    // The knuckle's fit (StruckSpike "fit" against the door-knock recording).
    internal const float KnuckleMassKg = 0.06f;
    internal const float KnuckleModulusGPa = 0.01f;
    internal const float KnucklePadM = 0.001f;

    public static readonly IReadOnlyDictionary<string, Func<Striker>> Presets = new Dictionary<string, Func<Striker>>(StringComparer.OrdinalIgnoreCase)
    {
        ["finger"] = () => Fingertip, ["knuckle"] = () => Knuckle, ["palm"] = () => Palm,
        ["toe"] = () => Toe, ["body"] = () => Body, ["rod"] = () => Rod, ["heel"] = () => Heel,
    };

    public static bool TryByName(string name, out Striker striker)
    {
        striker = default;
        if (!Presets.TryGetValue(name, out var make)) return false;
        striker = make();
        return true;
    }
}

/// <summary>One blow: who strikes, how fast (m/s), where on the struck face (0..1 each way) and when (s).</summary>
public readonly record struct Blow(Striker Striker, float Speed, float U = 0.5f, float V = 0.5f, float AtSeconds = 0f);

/// <summary>
/// A thing that can be struck: what it is made of (a registry name), its shape and size, how it is held, and
/// what in it is loose. Length is along its grain or its long way, Width across, Thickness through the struck
/// face (a box's depth, a tube's wall in <see cref="Wall"/>, its diameter in Width).
/// </summary>
public readonly record struct StruckThing
{
    public string Material { get; init; }
    public StruckShape Shape { get; init; }
    public float Length { get; init; }
    public float Width { get; init; }
    public float Thickness { get; init; }
    /// <summary>A tube's wall, a shell box's sheet, metres.</summary>
    public float Wall { get; init; }
    public StruckSupport Support { get; init; }
    /// <summary>Air behind a panel, metres (a stud wall's cavity); a shell box's is its depth.</summary>
    public float Cavity { get; init; }
    /// <summary>A loose fit, millimetres: a door leaf in its latch, a pale on its bolts, a part in a car's door.
    /// Zero for none.</summary>
    public float PlayMm { get; init; }
    /// <summary>The loose part's mass, kg; zero when the thing itself moves in its play (a door, a pale).</summary>
    public float LooseKg { get; init; }
    /// <summary>What meets in the loose fit (a registry name): steel on steel at a latch, plastic on steel.</summary>
    public string LooseMaterial { get; init; }
    /// <summary>A loss factor as fitted, in place of the support's (a car's panels with their deadening,
    /// VehicleBody.PanelLoss); zero for the support's own.</summary>
    public float FittedLoss { get; init; }

    /// <summary>Its mass, kg, solid or as sheet.</summary>
    public float MassKg
    {
        get
        {
            var p = AcousticRegistry.GetProperties(Material);
            return Shape switch
            {
                StruckShape.Tube => p.DensityKgM3 * MathF.PI * (Width * Width / 4 - MathF.Pow(MathF.Max(0, Width / 2 - Wall), 2)) * Length,
                StruckShape.ShellBox => p.DensityKgM3 * Wall * 2 * (Length * Width + Length * Thickness + Width * Thickness),
                _ => p.DensityKgM3 * Length * Width * Thickness,
            };
        }
    }
}

/// <summary>A thing and the blows on it: one sound. <see cref="Seed"/> varies the radiation phases (0..3).</summary>
public readonly record struct Strike(StruckThing Thing, IReadOnlyList<Blow> Blows, int Seed = 0);

/// <summary>
/// Sound from a struck thing's own material, size and shape (docs/MATTER.md section 7): modal synthesis.
/// The thing's modes (<see cref="StruckModes"/>) are a bank of exactly stepped resonators (DoorPhysics.Modes);
/// each blow is a contact integrated against the thing's motion at the struck point, at four times the mixer's
/// rate while anything touches, so a contact a tenth of a millisecond long is resolved; then the bank rings
/// down at the mixer's rate. Radiation: each mode by its efficiency (Rayleigh's integral for a panel in a
/// wall, the dipole law for a thing in open air), the striker and a free thing by their acceleration noise,
/// and what a thing rests on by the point-driven plate's law. A loose fit (a door in its latch, a pale on its
/// bolt) is a gap with a hard contact at each end: the rattle.
///
/// Rendered once per key on a worker, like the door models, and cached by its id; nothing here runs in a
/// mixer callback.
/// </summary>
public static class StruckThings
{
    public const string KeyPrefix = "strike:";

    /// <summary>Internal steps per output sample while anything is in contact.</summary>
    public const int Oversample = 4;

    /// <summary>The longest a strike rings, seconds: a 1 m aluminium cube on a string would ring for a minute.</summary>
    public const float MaxSeconds = 4f;

    /// <summary>A ring is cut where it is this far under its peak, dB.</summary>
    public const float TailDb = 70f;

    /// <summary>How far a body's clothes and flesh give before its measured spring is met, metres (EST).</summary>
    private const double ClothesM = 0.003;

    /// <summary>Standard gravity, m/s^2: a resting thing's weight is its contact's preload.</summary>
    private const double StandardGravity = 9.80665;

    /// <summary>What a thing resting on the ground pushes into: a 150 mm concrete slab, kg/m^2.</summary>
    public const float GroundSurfaceDensity = 2400f * 0.15f;

    /// <summary>A hand holding a thing: what it adds to the thing's mass (kg) and how stiffly it holds (N/m, EST).</summary>
    private const double HandKg = 0.4, HandStiffness = 3000, HandDampingRatio = 0.5;

    /// <summary>How stiff the frame's port is, N/m (GlassDoor's PortStiffness).</summary>
    private const double PortStiffness = 2e7;

    /// <summary>A panel's port into its dense field, tuned so the patch rides on the listed modes below the field
    /// and drives the field above it: the patch on its spring resonates an octave under where the field begins.
    /// Stiffer, the patch is locked to the listed modes and the field hears nothing (a rod on glass came out with
    /// its top 15 dB short); softer, it lengthens every contact.</summary>
    private static double PortTuned(double patchMass, double denseFromHz)
        => patchMass * Math.Pow(Math.PI * denseFromHz, 2);

    /// <summary>How stiffly a loose fit's stop is held, N/m: a latch bolt sideways in its keeper (GlassDoor's
    /// BoltSideStiffness, 2e7) in series with the keeper on its screws (StrikeMountStiffness, 3e7), about 1.2e7.
    /// A point contact of steel on steel alone put a door leaf's whole mass on a few micrometres and rang 111 dB.</summary>
    private const double StopMountStiffness = 1.2e7;

    /// <summary>How much of the closing speed a loose fit's stop gives back (EST: metal on metal through mounts
    /// that damp, the door models' bolt and keeper).</summary>
    private const double StopRestitution = 0.5;

    /// <summary>A dense field's effective modes are this far apart, Hz: half the door models' density (40 Hz),
    /// which where the modes overlap is the same sound at half the cost.</summary>
    private const double DenseSpacing = 120;

    // ── The level anchor ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The loudest 20 ms of the footstep bank's concrete takes, A-weighted, dBFS: the median of the takes in
    /// ASSETS/SOUNDS/FOOTSTEPS/Concrete (measured 2026-10-10 by `--struck anchor`; the bank's TakeLevels brings
    /// each take's loudest 20 ms to its folder's median, so this is every take's). The bank plays a take's full
    /// scale at <see cref="Loudness.FootstepDb"/>, so a footstep on concrete is this plus that, dB(A) at a metre.
    /// </summary>
    public const float FootstepTakeLoudestA20msDbfs = -22.0f;

    private static readonly Lazy<float> _anchor = new(ComputeAnchor);

    /// <summary>
    /// What every strike's level is moved by, dB, so that a heel on a concrete slab, struck by this model, is
    /// as loud to the ear (A-weighted, loudest 20 ms) as the footstep bank plays a step on concrete (todo, Bump
    /// sounds: "anchor the level to the footstep takes"). One number for every strike: the model's levels
    /// against each other stay its own.
    /// </summary>
    public static float LevelAnchorDb => _anchor.Value;

    /// <summary>The model's own heel strike on a slab: the loudest 20 ms, dB(A) at a metre.</summary>
    public static float ModelFootstepDb { get; private set; }

    private static float ComputeAnchor()
    {
        var heel = new Strike(Slab, new[] { new Blow(Striker.Heel, Footsteps.HeelVelocityRatio * 1.4f, 0.4f, 0.45f) });
        var p = RenderPascals(heel, Footsteps.SampleRate, out _);
        ModelFootstepDb = LoudestA20msDb(p, Footsteps.SampleRate);
        return Loudness.FootstepDb + FootstepTakeLoudestA20msDbfs - ModelFootstepDb;
    }

    /// <summary>A floor slab, 150 mm of concrete spanning 4 m: what the anchor's heel lands on.</summary>
    public static StruckThing Slab => new()
    {
        Material = "Concrete", Shape = StruckShape.Plate, Length = 4f, Width = 4f, Thickness = 0.15f, Support = StruckSupport.Built,
    };

    /// <summary>The loudest 20 ms of a pressure signal, A-weighted, dB(A) re 20 uPa: how loud it is to the ear,
    /// which a heel's 30 Hz thud is not.</summary>
    public static float LoudestA20msDb(float[] p, int rate) => Loudest20msDb(AWeighted(p, rate), rate);

    /// <summary>
    /// The A weighting (IEC 61672-1): two poles at 20.6 Hz, one at 107.7, one at 737.9 (high-passes) and two at
    /// 12194 (low-passes), each a first-order section by the bilinear transform with its pole prewarped, scaled to
    /// unity at 1 kHz.
    /// </summary>
    public static float[] AWeighted(float[] x, int rate)
    {
        var y = (float[])x.Clone();
        void Section(double hz, bool highPass)
        {
            double wp = 2 * rate * Math.Tan(Math.PI * hz / rate), k = 2.0 * rate;
            double a0 = k + wp, a1 = (wp - k) / a0;
            double b0 = highPass ? k / a0 : wp / a0, b1 = highPass ? -k / a0 : wp / a0;
            double x1 = 0, y1 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double xi = y[i], yi = b0 * xi + b1 * x1 - a1 * y1;
                x1 = xi; y1 = yi; y[i] = (float)yi;
            }
        }
        Section(20.598997, true); Section(20.598997, true); Section(107.65265, true); Section(737.86223, true);
        Section(12194.217, false); Section(12194.217, false);
        // The sections' gain at 1 kHz, to divide out.
        double g = 1;
        foreach (var (hz, hp) in new[] { (20.598997, true), (20.598997, true), (107.65265, true), (737.86223, true), (12194.217, false), (12194.217, false) })
        {
            double wp = 2 * rate * Math.Tan(Math.PI * hz / rate), w = 2 * rate * Math.Tan(Math.PI * 1000.0 / rate);
            g *= hp ? w / Math.Sqrt(w * w + wp * wp) : wp / Math.Sqrt(w * w + wp * wp);
        }
        for (int i = 0; i < y.Length; i++) y[i] = (float)(y[i] / g);
        return y;
    }

    /// <summary>The loudest 20 ms of a pressure signal, dB SPL (re 20 uPa).</summary>
    public static float Loudest20msDb(float[] p, int rate)
    {
        int w = Math.Max(1, rate / 50);
        double acc = 0, best = 0;
        for (int i = 0; i < p.Length; i++)
        {
            acc += (double)p[i] * p[i];
            if (i >= w) acc -= (double)p[i - w] * p[i - w];
            best = Math.Max(best, acc / w);
        }
        return (float)(10 * Math.Log10(Math.Max(best, 1e-20) / 4e-10));
    }

    // ── Rendering ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The strike, normalised to a peak of one; <paramref name="peakDb"/> is what that peak stands for,
    /// dB SPL at a metre, anchored (<see cref="LevelAnchorDb"/>).</summary>
    public static float[] Render(Strike strike, int rate, out float peakDb)
    {
        var p = RenderPascals(strike, rate, out float peak);
        peakDb = 20f * MathF.Log10(MathF.Max(peak, 1e-9f) / 2e-5f) + LevelAnchorDb;
        if (peak > 0f) for (int i = 0; i < p.Length; i++) p[i] /= peak;
        return p;
    }

    /// <summary>The strike as pressure at a metre, pascals, before the anchor; <paramref name="peak"/> its peak.</summary>
    public static float[] RenderPascals(Strike strike, int rate, out float peak)
    {
        var thing = strike.Thing;
        var m = AcousticRegistry.GetProperties(thing.Material);
        int seed = strike.Seed;
        LastContactSeconds = 0; LastPeakForce = 0;
        var blows = strike.Blows;
        bool loose = thing.PlayMm > 0f;
        // The points the model needs the shapes at: each blow's, then the loose fit's (the latch edge, or the
        // part's mount, a little in from the struck face's edge at mid height).
        var points = new List<(double U, double V)>();
        foreach (var b in blows) points.Add((Math.Clamp(b.U, 0.02, 0.98), Math.Clamp(b.V, 0.02, 0.98)));
        int mountAt = points.Count;
        points.Add(thing.LooseKg > 0f ? (0.7, 0.5) : (0.95, 0.5));

        var set = BuildModes(thing, m, points, seed);
        double dtF = 1.0 / (rate * Oversample);
        double dtC = 1.0 / rate;

        // Mode arrays, shapes per point.
        int n = set.Count;
        var fine = new Modes(set.Hz, set.Loss, set.Mass, set.Acc, dtF, set.Quad);
        var shapeAt = new double[points.Count][];
        for (int i = 0; i < points.Count; i++)
        {
            shapeAt[i] = new double[n];
            for (int k = 0; k < n; k++) shapeAt[i][k] = set.Shape[k][i];
        }

        // A dense field above the listed modes (plates): its own statistical modes, and a port at each point.
        DenseField? field = null, fieldC = null;
        Port[]? ports = null;
        double[][]? fieldHit = null;
        if (set.DenseFromHz > 0 && set.DenseFromHz < 16000)
        {
            Func<double, double> loss = f => m.LossAt((float)f)
                + (thing.FittedLoss > 0 ? thing.FittedLoss : StruckModes.SupportLoss(thing.Support, m.DensityKgM3 * set.PlateThickness, f, true, m.Family == "glass"));
            double e = Math.Sqrt(m.YoungsModulusGPa * m.AcrossGrainGPa) * 1e9;
            field = new DenseField(set.PlateA, set.PlateB, set.PlateThickness, e, m.DensityKgM3, m.Poisson, loss,
                                   set.DenseFromHz, 16000, new Random(seed * 7 + 1), dtF, DenseSpacing);
            fieldC = new DenseField(set.PlateA, set.PlateB, set.PlateThickness, e, m.DensityKgM3, m.Poisson, loss,
                                    set.DenseFromHz, 16000, new Random(seed * 7 + 1), dtC, DenseSpacing);
            ports = new Port[points.Count];
            fieldHit = new double[points.Count][];
            var pointRng = new Random(seed * 7 + 2);
            for (int i = 0; i < points.Count; i++)
            {
                ports[i] = new Port(field.PatchMass, PortTuned(field.PatchMass, set.DenseFromHz), field.Impedance);
                fieldHit[i] = new double[field.Modes.N];
                for (int k = 0; k < field.Modes.N; k++) fieldHit[i][k] = (pointRng.NextDouble() * 2 - 1) * Math.Sqrt(3);
            }
        }

        // The thing as a whole: free on a string, or in a hand, it moves; built in or on the ground it does not,
        // unless it is loose in its fit (a door in its latch: about a third of the leaf's mass at the latch edge,
        // turning on its hinges; a pale on its bolts: the pale).
        double total = Math.Max(1e-4, thing.MassKg);
        bool resting = thing.Support == StruckSupport.Resting;
        bool moves = thing.Support != StruckSupport.Built || (loose && thing.LooseKg <= 0f);
        double rigidMass = thing.Support == StruckSupport.Held ? total + HandKg
                         : loose && thing.LooseKg <= 0f && thing.Support == StruckSupport.Built ? total / 3 : total;
        double weight = total * StandardGravity;
        double rigidK = thing.Support == StruckSupport.Held ? HandStiffness
                      : resting ? RestingStiffness(m, weight)
                      : loose && thing.LooseKg <= 0f ? 400 : 0;   // a seal or a closer holding a door in the middle of its play (EST)
        double rigidC = thing.Support == StruckSupport.Held ? 2 * HandDampingRatio * Math.Sqrt(HandStiffness * rigidMass)
                      : resting ? 2 * RestingDampingRatio * Math.Sqrt(rigidK * rigidMass)
                      : rigidK > 0 ? 2 * 0.2 * Math.Sqrt(rigidK * rigidMass) : 0;
        double xr = 0, vr = 0;
        // A thing on the ground moves against the ground: its image in it cancels its push below a wavelength
        // or so of its height. On a string or in a hand it is in open air.
        var rigidNoise = new NearSurfaceDipole(Math.Max(1e-7, set.Volume), dtF, resting ? Math.Max(0.005, thing.Thickness / 2) : 0);
        // The loose fit's stops: hard contacts of its own material on the thing's.
        double play = thing.PlayMm / 1000.0;
        var looseMat = AcousticRegistry.GetProperties(string.IsNullOrEmpty(thing.LooseMaterial) ? "Metal" : thing.LooseMaterial);
        double stopK = HertzK(looseMat.YoungsModulusGPa, looseMat.Poisson, m.AcrossGrainGPa, m.Poisson, 0.003);
        double partKg = thing.LooseKg, partX = 0, partV = 0, stopApproach = 0;
        var partNoise = new AccelerationNoise(partKg > 0 ? partKg / Math.Max(100, looseMat.DensityKgM3) : 1e-7, dtF);
        // What the stops are fixed to (a door's jamb, a fence's rail): a small aluminium or steel section, ringing as
        // a dense field and radiating; the frame is the loose material's.
        DenseField? frame = null, frameC = null;
        Port? framePort = null;
        double[]? frameHit = null;
        if (loose && thing.LooseKg <= 0f)
        {
            DenseField Frame(double dt) => new(0.06, 2.0, 0.002, looseMat.YoungsModulusGPa * 1e9, looseMat.DensityKgM3, looseMat.Poisson,
                                               f => ThinPanelLoss(f), 300, 16000, new Random(seed * 7 + 3), dt, DenseSpacing, 0.03);
            frame = Frame(dtF);
            frameC = Frame(dtC);
            framePort = new Port(frame.PatchMass, PortStiffness, frame.Impedance);
            var hitRng = new Random(seed * 7 + 4);
            frameHit = new double[frame.Modes.N];
            for (int k = 0; k < frameHit.Length; k++) frameHit[k] = (hitRng.NextDouble() * 2 - 1) * Math.Sqrt(3);
        }

        // The blows.
        int nb = blows.Count;
        var x = new double[nb]; var v = new double[nb]; var approach = new double[nb];
        var started = new bool[nb]; var done = new bool[nb];
        var noise = new NearSurfaceDipole[nb];
        var bodyAcc = new double[nb];
        var portForce = new double[nb];
        double BodyAlpha = 1 - Math.Exp(-2 * Math.PI * 150 * dtF);
        var k1 = new double[nb]; var k2 = new double[nb];
        for (int b = 0; b < nb; b++)
        {
            var s = blows[b].Striker;
            // It stops against the thing's face: its image in that face cancels it at low frequencies.
            noise[b] = new NearSurfaceDipole(Math.Max(1e-7, s.VolumeM3), dtF, Math.Max(0.003, s.RadiusM));
            k1[b] = s.LinearStiffness > 0 ? s.LinearStiffness : HertzK(s.ModulusGPa, s.Poisson, m.AcrossGrainGPa, m.Poisson, s.RadiusM);
            k2[b] = s.PadM > 0 ? HertzK(s.CoreModulusGPa, 0.3, m.AcrossGrainGPa, m.Poisson, s.RadiusM) : 0;
        }
        var floorHp = new HighPass(30, rate * Oversample);
        double floorGain = thing.Support == StruckSupport.Resting ? Rho0 / (2 * Math.PI * GroundSurfaceDensity) : 0;
        // A panel's listed modes add coherently, and between them carry the near field round the blow; the dense
        // field above them is statistical and does not. There, and below coincidence, the point-driven plate's own
        // law stands in: p = rho0 F / (2 pi m'' r), the same at every frequency (Cremer and Heckl, Structure-Borne
        // Sound, the power a point force radiates from an infinite plate, rho0 F^2 / (2 pi c m''^2)): the
        // "radiation efficiency below a panel's critical frequency" the old bump lacked (todo, Bump sounds).
        double nearGain = 0;
        HighPass? nearHp = null;
        double nearLp = 0, nearLp2 = 0, nearAlpha = 0;
        if (field != null && set.DenseFromHz < set.CriticalHz)
        {
            nearGain = Rho0 / (2 * Math.PI * m.DensityKgM3 * set.PlateThickness);
            nearHp = new HighPass(set.DenseFromHz, rate * Oversample);
            nearAlpha = 1 - Math.Exp(-2 * Math.PI * Math.Min(set.CriticalHz, 0.45 * rate) * dtF);
        }
        double hardness = m.HardnessMPa * 1e6;

        // The modes (which ring on at the mixer's rate after the contacts) and everything else (the strikers' and
        // the thing's own motion, the ground, the near field), which is finished at the fine rate on its own: cut
        // where the modes change rate, the thing's last bounce on the ground clicked.
        var hi = new List<float>(rate * Oversample / 2);
        var hiT = new List<float>(rate * Oversample / 2);
        double lastTouch = 0, lastStart = 0;
        foreach (var b in blows) lastStart = Math.Max(lastStart, b.AtSeconds);
        int maxFine = (int)(Math.Min(MaxSeconds, lastStart + 1.5) * rate) * Oversample;
        int step = 0;
        int switchAt = -1;
        double[]? qSnap = null, vSnap = null, fqSnap = null, fvSnap = null, gqSnap = null, gvSnap = null;
        int margin = 160 * Oversample;
        for (; step < maxFine + margin; step++)
        {
            double t = step * dtF;
            double p = 0, pt = 0, fTotal = 0, rigidF = 0;
            bool touching = false;
            for (int b = 0; b < nb; b++)
            {
                if (done[b] || t < blows[b].AtSeconds) continue;
                var s = blows[b].Striker;
                double w = fine.At(shapeAt[b]) + (ports != null ? ports[b].X : 0) + (moves ? xr : 0);
                double wd = fine.RateAt(shapeAt[b]) + (ports != null ? ports[b].V : 0) + (moves ? vr : 0);
                if (!started[b]) { started[b] = true; x[b] = w; v[b] = Math.Max(0.01, blows[b].Speed); }
                double depth = x[b] - w, rate1 = v[b] - wd;
                double f;
                if (s.LinearStiffness > 0)
                {
                    double peakDepth = Math.Max(1e-4, blows[b].Speed * Math.Sqrt(s.MassKg / s.LinearStiffness));
                    // Clothes and the flesh first: the measured spring takes over after a few millimetres (EST 3 mm);
                    // a spring touching at full stiffness is a step in the jerk, a click across the whole band.
                    double soft = depth > 0 ? depth * depth / (depth + ClothesM) : 0;
                    f = depth > 0 ? Math.Max(0, s.LinearStiffness * soft + s.LinearDamping * (soft / peakDepth) * rate1) : 0;
                }
                else
                {
                    f = ContactRestitution(k1[b], Math.Clamp(s.Restitution, 0.05, 0.95), depth, rate1, ref approach[b]);
                    // Through the pad to the bone: the knuckle's crack.
                    if (s.PadM > 0 && depth > s.PadM)
                    {
                        double coreApproach = approach[b];
                        f += ContactRestitution(k2[b], Math.Clamp(s.Restitution, 0.05, 0.95), depth - s.PadM, rate1, ref coreApproach);
                    }
                    // A striker harder than the thing dents it once the mean pressure under it reaches its
                    // hardness: the force grows only with the area, pi R depth H (Johnson, Contact Mechanics, ch. 6).
                    if (s.ModulusGPa > m.AcrossGrainGPa && hardness > 0 && depth > 0)
                        f = Math.Min(f, Math.PI * s.RadiusM * depth * hardness);
                }
                if (depth <= 0 && rate1 < 0 && t > blows[b].AtSeconds + 1e-4) { done[b] = true; f = 0; }
                double acc = -f / s.MassKg;
                v[b] += acc * dtF; x[b] += v[b] * dtF;
                // A body is not rigid: its bulk slows over the time a shear wave takes through its flesh, so
                // only its contact patch stops sharply. Its stop radiates below about 150 Hz (EST: soft tissue's
                // shear waves at a few metres a second across a shoulder's 20 cm).
                if (s.LinearStiffness > 0) { bodyAcc[b] += BodyAlpha * (acc - bodyAcc[b]); pt += On(4) * noise[b].Pressure(bodyAcc[b]); }
                else pt += On(4) * noise[b].Pressure(acc);
                if (f > 0) { touching = true; lastTouch = t; if (b == 0) { LastContactSeconds += dtF; LastPeakForce = Math.Max(LastPeakForce, f); } }
                fTotal += f;
                if (ports != null) portForce[b] = f;
                else fine.Push(shapeAt[b], f);
                if (moves) rigidF += f;
            }
            // Every port every step, touched or not: a patch let go mid-swing kept its spring's last push on the
            // panel and its last drive on the field, a step in both, heard as a click as the shoulder came away.
            if (ports != null)
                for (int b = 0; b < nb; b++)
                {
                    if (!started[b]) continue;
                    double drive = ports[b].Step(portForce[b], dtF, out double host);
                    field!.Modes.Push(fieldHit![b], drive);
                    fine.Push(shapeAt[b], host);
                    portForce[b] = 0;
                }

            // The loose fit.
            if (loose)
            {
                double wm = fine.At(shapeAt[mountAt]) + (moves ? xr : 0);
                double wmd = fine.RateAt(shapeAt[mountAt]) + (moves ? vr : 0);
                double gap, gapRate;
                if (partKg > 0) { gap = partX - wm; gapRate = partV - wmd; }
                else { gap = xr; gapRate = vr; }
                double over = Math.Abs(gap) - play;
                double fs = 0;
                if (over > 0)
                {
                    // The two meet at a point (Hertz) through what holds each (a bolt in its keeper, a strike on its
                    // screws): the softer of the two decides, the mount for anything but the first micrometres.
                    double closing = Math.Sign(gap) * gapRate;
                    if (stopApproach <= 0) stopApproach = Math.Max(Math.Abs(closing), 1e-4);
                    double elastic = Math.Min(stopK * over * Math.Sqrt(over), StopMountStiffness * over);
                    fs = Math.Max(0, elastic * (1 + 8 * (1 - StopRestitution) / (5 * StopRestitution) * closing / stopApproach)) * Math.Sign(gap);
                }
                else stopApproach = 0;
                if (fs != 0) { touching = true; lastTouch = t; }
                if (partKg > 0)
                {
                    double pa = (-fs - 200 * partX) / partKg;   // a weak spring centring it (EST)
                    partV += pa * dtF; partX += partV * dtF;
                    pt += On(32) * partNoise.Pressure(pa);
                    fine.Push(shapeAt[mountAt], fs);
                }
                else
                {
                    // The leaf meets its stop: the force goes into the leaf at its latch edge and into the frame.
                    rigidF -= fs;
                    fine.Push(shapeAt[mountAt], -fs);
                    if (frame != null)
                    {
                        double drive = framePort!.Step(fs, dtF, out _);
                        frame.Modes.Push(frameHit!, drive);
                    }
                }
            }

            double floorForce = 0;
            if (moves)
            {
                double hold = rigidK * xr + rigidC * vr;
                // On the ground the contact only pushes: struck hard enough, a light thing hops (its weight is the
                // preload the contact already carries).
                if (resting) { hold = Math.Max(-weight, hold); floorForce = hold; }
                double ra = (rigidF - hold) / rigidMass;
                vr += ra * dtF; xr += vr * dtF;
                if (thing.Support != StruckSupport.Built) pt += On(8) * rigidNoise.Pressure(ra);
            }

            p += On(1) * fine.Step();
            if (field != null) p += On(2) * field.Modes.Step();
            if (frame != null) p += On(32) * frame.Modes.Step();
            if (floorGain > 0) pt += On(8) * floorGain * floorHp.Next(floorForce);
            if (nearHp != null)
            {
                nearLp += nearAlpha * (nearHp.Next(fTotal) - nearLp);
                nearLp2 += nearAlpha * (nearLp - nearLp2);
                pt += On(16) * nearGain * nearLp2;
            }
            hi.Add((float)p);
            hiT.Add((float)pt);

            // Settled: every blow has left, nothing has touched for 20 ms, and the loose part has come to rest.
            // From here the modes only ring, and ring on at the mixer's rate; the fine run goes on a little
            // further only to fill the decimation filter.
            bool settled = step % Oversample == 0 && t > lastStart + 0.002 && !touching
                           && AllDone(done, started) && t - lastTouch > 0.02
                           && (!loose || t - lastTouch > 0.05)
                           && (!moves || loose || thing.Support == StruckSupport.Hung || Math.Abs(vr) < 1e-4);
            if (switchAt < 0 && (settled || step + 1 >= maxFine))
            {
                switchAt = step + 1;
                qSnap = (double[])fine.Q.Clone(); vSnap = (double[])fine.V.Clone();
                if (field != null) { fqSnap = (double[])field.Modes.Q.Clone(); fvSnap = (double[])field.Modes.V.Clone(); }
                if (frame != null) { gqSnap = (double[])frame.Modes.Q.Clone(); gvSnap = (double[])frame.Modes.V.Clone(); }
                maxFine = Math.Min(maxFine, step + 1);
            }
        }

        // The motion's own tail: no contacts are left, so it runs on alone until it has died away.
        int tailSteps = (int)(0.2 * rate) * Oversample;
        for (int i = 0; i < tailSteps && (moves || nearHp != null || floorGain > 0); i++)
        {
            double pt = 0, floorForce = 0;
            if (moves)
            {
                double hold = rigidK * xr + rigidC * vr;
                if (resting) { hold = Math.Max(-weight, hold); floorForce = hold; }
                double ra = -hold / rigidMass;
                vr += ra * dtF; xr += vr * dtF;
                if (thing.Support != StruckSupport.Built) pt += On(8) * rigidNoise.Pressure(ra);
            }
            if (floorGain > 0) pt += On(8) * floorGain * floorHp.Next(floorForce);
            if (nearHp != null)
            {
                nearLp += nearAlpha * (nearHp.Next(0) - nearLp);
                nearLp2 += nearAlpha * (nearLp - nearLp2);
                pt += On(16) * nearGain * nearLp2;
            }
            hiT.Add((float)pt);
        }
        var transient = Decimate(hiT, rate * Oversample, 1.0, out _, Oversample);

        var head = Decimate(hi, rate * Oversample, 1.0, out _, Oversample);
        int headLength = Math.Min(head.Length, switchAt / Oversample);

        // Ringing down at the mixer's rate: the same modes, those under the decimation's 20 kHz.
        var keep = new List<int>();
        for (int k = 0; k < n; k++) if (set.Hz[k] < 0.45 * rate) keep.Add(k);
        var coarse = new Modes(Pick(set.Hz, keep), Pick(set.Loss, keep), Pick(set.Mass, keep), Pick(set.Acc, keep), dtC,
                               Pick(set.Quad, keep));
        for (int i = 0; i < keep.Count; i++) { coarse.Q[i] = qSnap![keep[i]]; coarse.V[i] = vSnap![keep[i]]; }
        if (fieldC != null)
        {
            Array.Copy(fqSnap!, fieldC.Modes.Q, fieldC.Modes.N);
            Array.Copy(fvSnap!, fieldC.Modes.V, fieldC.Modes.N);
        }
        if (frameC != null)
        {
            Array.Copy(gqSnap!, frameC.Modes.Q, frameC.Modes.N);
            Array.Copy(gvSnap!, frameC.Modes.V, frameC.Modes.N);
        }
        float headPeak = 0f;
        for (int i = 0; i < headLength; i++) headPeak = MathF.Max(headPeak, MathF.Abs(head[i]));
        var outp = new List<float>(head.Take(headLength));
        int maxOut = (int)(MaxSeconds * rate);
        float runPeak = headPeak;
        int block = rate / 20;
        while (outp.Count < maxOut)
        {
            float blockPeak = 0f;
            for (int i = 0; i < block; i++)
            {
                double p = On(1) * coarse.Step() + (fieldC != null ? On(2) * fieldC.Modes.Step() : 0) + (frameC != null ? On(32) * frameC.Modes.Step() : 0);
                outp.Add((float)p);
                blockPeak = MathF.Max(blockPeak, MathF.Abs((float)p));
            }
            runPeak = MathF.Max(runPeak, blockPeak);
            if (blockPeak <= runPeak * MathF.Pow(10f, -TailDb / 20f)) break;
        }
        // The motion's tail, from the start, under the modes; trailing silence it adds is trimmed.
        for (int i = 0; i < transient.Length; i++)
        {
            if (i < outp.Count) outp[i] += transient[i];
            else outp.Add(transient[i]);
        }
        // Cut where everything has fallen TailDb under the peak, as the ring alone is cut.
        float allPeak = 0f;
        foreach (float ov in outp) allPeak = MathF.Max(allPeak, MathF.Abs(ov));
        float floor = allPeak * MathF.Pow(10f, -TailDb / 20f);
        int keepTo = outp.Count;
        while (keepTo > 1 && MathF.Abs(outp[keepTo - 1]) <= floor) keepTo--;
        keepTo = Math.Min(outp.Count, keepTo + rate / 100);
        if (keepTo < outp.Count) outp.RemoveRange(keepTo, outp.Count - keepTo);
        // A few milliseconds' fade where it is cut.
        int fade = Math.Min(outp.Count, rate / 100);
        for (int i = 0; i < fade; i++) outp[outp.Count - 1 - i] *= (float)i / fade;
        var result = outp.ToArray();
        peak = 0f;
        foreach (float s in result) peak = MathF.Max(peak, MathF.Abs(s));
        return result;
    }

    /// <summary>
    /// For instruments: which parts of a strike are heard, as bits (1 the listed modes, 2 the dense field,
    /// 4 the strikers' own stop, 8 the thing's own motion and the ground under it, 16 the near field, 32 the
    /// loose fit's part and frame). All of them unless an instrument says otherwise; nothing in the game sets it.
    /// </summary>
    public static int Heard { get; set; } = -1;

    private static double On(int part) => (Heard & part) != 0 ? 1 : 0;

    /// <summary>For instruments: how long the first blow of the last strike rendered on this thread touched, and
    /// its peak force (N).</summary>
    [ThreadStatic] public static double LastContactSeconds, LastPeakForce;

    private static bool AllDone(bool[] done, bool[] started)
    {
        for (int i = 0; i < done.Length; i++) if (!done[i] || !started[i]) return false;
        return true;
    }

    private static List<double> Pick(List<double> from, List<int> keep)
    {
        var r = new List<double>(keep.Count);
        foreach (int k in keep) r.Add(from[k]);
        return r;
    }

    /// <summary>
    /// A small rigid body's acceleration noise (DoorPhysics.AccelerationNoise: a dipole, rho V' (da/dt) / (c r))
    /// moving along the normal of a rigid surface it is <paramref name="distance"/> from. Its image in the surface
    /// points the other way, so on the normal the two give s(t) - s(t - 2d/c): a quadrupole, nothing at the
    /// bottom, the plain dipole again above c / 4d. A hand stopping on a wall, a block bouncing on the floor.
    /// Zero distance: in open air, no image.
    /// </summary>
    private sealed class NearSurfaceDipole
    {
        private readonly AccelerationNoise dipole;
        private readonly double[] ring;
        private int at;
        public NearSurfaceDipole(double volume, double dt, double distance)
        {
            dipole = new AccelerationNoise(volume, dt);
            int delay = distance > 0 ? Math.Max(1, (int)Math.Round(2 * distance / C0 / dt)) : 0;
            ring = new double[delay];
        }
        public double Pressure(double acc)
        {
            double s = dipole.Pressure(acc);
            if (ring.Length == 0) return s;
            double image = ring[at];
            ring[at] = s;
            at = (at + 1) % ring.Length;
            return s - image;
        }
    }

    /// <summary>
    /// How stiffly the ground holds a thing resting on it: no two surfaces are flat, so it stands on its three
    /// highest points, each a Hertz contact (asperities of about a millimetre, EST) of its material on concrete
    /// carrying a third of its weight, k = (3/2) K^(2/3) (W/3)^(1/3) each (Johnson, Contact Mechanics, 4.2). A
    /// 20 cm aluminium cube sits at about 145 Hz on it, a 5 cm one at 570 Hz, a 1 m one at 30 Hz: a heavy thing
    /// barely shakes the floor it stands on.
    /// </summary>
    internal static double RestingStiffness(MaterialProperties m, double weight)
    {
        var ground = AcousticRegistry.GetProperties("Concrete");
        double k = HertzK(m.AcrossGrainGPa, m.Poisson, ground.YoungsModulusGPa, ground.Poisson, 0.001);
        return 3 * 1.5 * Math.Pow(k, 2.0 / 3) * Math.Pow(Math.Max(1e-3, weight / 3), 1.0 / 3);
    }

    /// <summary>The damping of that contact, as a ratio of critical (EST: micro-slip and the ground's loss).</summary>
    private const double RestingDampingRatio = 0.1;

    /// <summary>Hertz's contact stiffness for a sphere of radius R on a flat: (4/3) E* sqrt(R), with
    /// 1/E* = (1 - v1^2)/E1 + (1 - v2^2)/E2. The softer of the two decides it.</summary>
    internal static double HertzK(double e1GPa, double nu1, double e2GPa, double nu2, double radius)
    {
        double e1 = Math.Max(1e4, e1GPa * 1e9), e2 = Math.Max(1e4, e2GPa * 1e9);
        double eStar = 1 / ((1 - nu1 * nu1) / e1 + (1 - nu2 * nu2) / e2);
        return 4.0 / 3 * eStar * Math.Sqrt(Math.Max(1e-4, radius));
    }

    /// <summary>One listed mode, for instruments and tests: its note, total loss factor, its shape at the point
    /// asked for, and its radiation (pressure at a metre per unit modal velocity).</summary>
    public readonly record struct ModeInfo(double Hz, double Loss, double Drive, double Radiation, double MassKg);

    /// <summary>A thing's listed modes, lowest first, with their shapes at (u, v) on the struck face, and where a
    /// dense field takes over (zero: never).</summary>
    public static (List<ModeInfo> Modes, double DenseFromHz) ModesOf(StruckThing thing, float u = 0.5f, float v = 0.5f, int seed = 0)
    {
        var m = AcousticRegistry.GetProperties(thing.Material);
        var set = BuildModes(thing, m, new[] { ((double)u, (double)v) }, seed);
        var list = new List<ModeInfo>(set.Count);
        for (int k = 0; k < set.Count; k++) list.Add(new ModeInfo(set.Hz[k], set.Loss[k], set.Shape[k][0], set.Radiation[k], set.Mass[k]));
        list.Sort((a, b) => a.Hz.CompareTo(b.Hz));
        return (list, set.DenseFromHz);
    }

    /// <summary>The modes of a thing, by its shape.</summary>
    internal static StruckModes.Set BuildModes(StruckThing t, MaterialProperties m, IReadOnlyList<(double U, double V)> points, int seed)
    {
        double l = Math.Max(0.005, t.Length), w = Math.Max(0.005, t.Width), h = Math.Max(0.0002, t.Thickness);
        return t.Shape switch
        {
            StruckShape.Bar => StruckModes.Bar(m, l, w, h, t.Support, points, seed),
            StruckShape.Plate => StruckModes.Plate(m, l, w, h, t.Support, t.FittedLoss, t.Cavity, points, seed,
                                                   baffled: t.Support == StruckSupport.Built),
            StruckShape.FreePlate => StruckModes.FreePlate(m, l, w, h, t.Support, points, seed),
            StruckShape.Tube => StruckModes.Tube(m, l, w, Math.Max(0.0003, t.Wall > 0 ? t.Wall : w / 2), t.Support, points, seed),
            // A shell box: its struck face over the air inside, its edges at the box's corners.
            StruckShape.ShellBox => StruckModes.Plate(m, l, w, Math.Max(0.0003, t.Wall), StruckSupport.Built, t.FittedLoss,
                                                      t.Cavity > 0 ? t.Cavity : h, points, seed, baffled: true),
            _ => StruckModes.Block(m, l, w, h, t.Support, points, seed),
        };
    }

    // ── Keys ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The strike as a key, every number in it, so the client renders exactly what was struck and two strikes of
    /// one thing share a buffer: material, shape, the sizes in tenths of a millimetre, the support, what is
    /// loose, then each blow (striker, speed in cm/s, where in per cent, when in ms), then the seed (0..3).
    /// </summary>
    public static string Key(Strike s)
    {
        var t = s.Thing;
        var ci = CultureInfo.InvariantCulture;
        string Tenth(float metres) => ((int)MathF.Round(metres * 10000f)).ToString(ci);
        var parts = new List<string>
        {
            t.Material ?? "Generic", ((int)t.Shape).ToString(ci), Tenth(t.Length), Tenth(t.Width), Tenth(t.Thickness), Tenth(t.Wall),
            ((int)t.Support).ToString(ci), Tenth(t.Cavity), ((int)MathF.Round(t.PlayMm * 100f)).ToString(ci),
            ((int)MathF.Round(t.LooseKg * 1000f)).ToString(ci), t.LooseMaterial ?? "", ((int)MathF.Round(t.FittedLoss * 10000f)).ToString(ci),
        };
        var blows = new List<string>();
        foreach (var b in s.Blows)
            blows.Add(string.Join(",", b.Striker.Name, ((int)MathF.Round(b.Speed * 100f)).ToString(ci),
                                  ((int)MathF.Round(b.U * 100f)).ToString(ci), ((int)MathF.Round(b.V * 100f)).ToString(ci),
                                  ((int)MathF.Round(b.AtSeconds * 1000f)).ToString(ci)));
        parts.Add(string.Join(";", blows));
        parts.Add((s.Seed & 3).ToString(ci));
        return KeyPrefix + string.Join("|", parts);
    }

    public static bool TryParseKey(string? key, out Strike strike)
    {
        strike = default;
        if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
        var f = key[KeyPrefix.Length..].Split('|');
        if (f.Length != 14) return false;
        var ci = CultureInfo.InvariantCulture;
        bool I(string s, out int v) => int.TryParse(s, NumberStyles.Integer, ci, out v);
        if (!I(f[1], out int shape) || !I(f[2], out int l) || !I(f[3], out int w) || !I(f[4], out int h) || !I(f[5], out int wall)
            || !I(f[6], out int support) || !I(f[7], out int cavity) || !I(f[8], out int play) || !I(f[9], out int looseG)
            || !I(f[11], out int fitted) || !I(f[13], out int seed)) return false;
        if (shape < 0 || shape > (int)StruckShape.ShellBox || support < 0 || support > (int)StruckSupport.Built) return false;
        // Sizes within what can be built (a tenth of a millimetre to 50 m): the key is the server's word, never a
        // way to ask a client for a render that will not finish.
        if (l <= 0 || w <= 0 || h <= 0 || l > 500000 || w > 500000 || h > 500000) return false;
        var blows = new List<Blow>();
        foreach (var bs in f[12].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var b = bs.Split(',');
            if (b.Length != 5 || !Striker.TryByName(b[0], out var striker)) return false;
            if (!I(b[1], out int speed) || !I(b[2], out int u) || !I(b[3], out int v) || !I(b[4], out int at)) return false;
            if (speed <= 0 || speed > 2000 || at < 0 || at > 2000) return false;
            blows.Add(new Blow(striker, speed / 100f, Math.Clamp(u, 0, 100) / 100f, Math.Clamp(v, 0, 100) / 100f, at / 1000f));
        }
        if (blows.Count == 0 || blows.Count > 8) return false;
        strike = new Strike(new StruckThing
        {
            Material = f[0], Shape = (StruckShape)shape, Length = l / 10000f, Width = w / 10000f, Thickness = h / 10000f,
            Wall = wall / 10000f, Support = (StruckSupport)support, Cavity = cavity / 10000f, PlayMm = play / 100f,
            LooseKg = looseG / 1000f, LooseMaterial = f[10], FittedLoss = fitted / 10000f,
        }, blows, seed & 3);
        return true;
    }

    /// <summary>Renders a key at <paramref name="rate"/>: silence for a key that does not parse.</summary>
    public static float[] RenderKey(string key, int rate, out float peakDb)
    {
        peakDb = 0f;
        return TryParseKey(key, out var s) ? Render(s, rate, out peakDb) : new float[16];
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> _declared = new(StringComparer.Ordinal);

    /// <summary>A strike's peak level, dB SPL at a metre, as the server declares it with the key; rendered once
    /// per key and kept. The client places it at its own render's level all the same.</summary>
    public static float DeclaredDb(Strike s)
    {
        string key = Key(s);
        if (_declared.TryGetValue(key, out float db)) return db;
        Render(s, Footsteps.SampleRate, out db);
        if (_declared.Count < 4096) _declared[key] = db;
        return db;
    }

    // ── What a thing in the world is, as a struck thing ─────────────────────────────────────────────

    /// <summary>
    /// A box in the world as a struck thing, from what the map already says of it: its material, its collider's
    /// size and which face was met (<paramref name="localNormal"/>, in the box's own frame), its build (a stud
    /// wall's leaves), whether it moves (a door's leaf in its latch) and whether it is a vehicle. The face met is
    /// the struck face and the axis through it the thickness. Nothing per map, nothing by name.
    /// <list type="bullet">
    /// <item>A wall of two leaves on studs: one leaf between two studs, the cavity behind it.</item>
    /// <item>A vehicle: its body panels (VehicleBody: steel skin, the span between stiffeners, fitted loss), over
    /// the cabin, with a part loose in the door (lock rods, the window in its channel: EST 0.3 kg, 0.8 mm).</item>
    /// <item>A palisade fence: a pale, pinned between its rails a metre apart, on its bolt with 0.3 mm of play
    /// (EST).</item>
    /// <item>A thing that moves and is thin (a door leaf): a plate in its frame, loose in its latch by the glass
    /// door model's keeper play (GlassDoor: the seal's compression plus half a millimetre, about 2 mm).</item>
    /// <item>Anything else thin (its thickness under a fifth of its face): a plate, built in.</item>
    /// <item>Long and narrow: a bar. Otherwise a block.</item>
    /// </list>
    /// </summary>
    public static StruckThing Describe(string? material, Vector3 size, Vector3 localNormal, float leafMetres, float studSpacing,
                                       bool moves, bool isVehicle, out bool lengthIsUp, VehicleBody? body = null,
                                       Geometry.ShapeSpec? form = null)
    {
        string mat = string.IsNullOrEmpty(material) ? "Generic" : material;
        float ax = MathF.Abs(localNormal.X), ay = MathF.Abs(localNormal.Y), az = MathF.Abs(localNormal.Z);
        // A shape is struck as the part it has, not the box round it (docs/GEOMETRY.md 12.6): a pitched roof is its
        // deck over the slope, a flight one step.
        if (form is { Kind: Geometry.ShapeKind.Roof, Style: not Geometry.RoofStyle.Flat })
        {
            lengthIsUp = false;
            float deck = Geometry.Shapes.PanelOf(form, size).Y;
            return new StruckThing
            {
                Material = mat, Shape = StruckShape.Plate, Length = MathF.Max(size.X, size.Z), Width = MathF.Min(size.X, size.Z),
                Thickness = MathF.Max(0.005f, deck), Support = StruckSupport.Built,
            };
        }
        if (form is { Kind: Geometry.ShapeKind.Stairs, Steps: > 0 })
        {
            lengthIsUp = false;
            float rise = size.Y / form.Steps, going = MathF.Max(0.05f, (size.Z - form.Landing) / form.Steps);
            return new StruckThing
            {
                Material = mat, Shape = StruckShape.Block, Length = MathF.Max(size.X, going), Width = MathF.Min(size.X, going),
                Thickness = MathF.Max(0.02f, rise), Support = StruckSupport.Built,
            };
        }
        // The face met: across (horizontal) and up (vertical) when it stands; both across when it is a top.
        float thick, across, up;
        bool standing = true;
        if (ax >= ay && ax >= az) { thick = size.X; across = size.Z; up = size.Y; }
        else if (az >= ay) { thick = size.Z; across = size.X; up = size.Y; }
        else { thick = size.Y; across = size.X; up = size.Z; standing = false; }
        across = MathF.Max(0.01f, across); up = MathF.Max(0.01f, up); thick = MathF.Max(0.0005f, thick);
        var support = moves ? StruckSupport.Resting : StruckSupport.Built;
        // Long way first: a plank's grain runs its length, and a door's its height.
        bool upIsLong = standing && up >= across;
        float longSide = MathF.Max(across, up), shortSide = MathF.Min(across, up);
        lengthIsUp = upIsLong;

        if (isVehicle)
        {
            var vb = body ?? VehicleBody.Saloon;
            float span = vb.PanelSpansM.Length > 0 ? vb.PanelSpansM[0] : 0.35f;
            float span2 = vb.PanelSpansM.Length > 1 ? vb.PanelSpansM[1] : span;
            lengthIsUp = false;
            return new StruckThing
            {
                Material = vb.PanelMaterial, Shape = StruckShape.ShellBox, Length = span, Width = span2,
                Thickness = MathF.Max(0.1f, vb.CabinWidthM / 2), Wall = vb.PanelThicknessM, Support = StruckSupport.Built,
                Cavity = MathF.Max(0.1f, vb.CabinWidthM / 2), FittedLoss = vb.PanelLoss,
                PlayMm = 0.8f, LooseKg = 0.3f, LooseMaterial = "Plastic",
            };
        }
        if (leafMetres > 0f && thick >= 2 * leafMetres + WallTransmission.MinCavityMetres)
        {
            // One board between two studs, the wall's height, over the cavity.
            float between = studSpacing > 0f ? MathF.Min(studSpacing, across) : across;
            lengthIsUp = standing;
            return new StruckThing
            {
                Material = mat, Shape = StruckShape.Plate, Length = standing ? up : across, Width = standing ? between : up,
                Thickness = leafMetres, Support = StruckSupport.Built, Cavity = thick - 2 * leafMetres,
            };
        }
        if (string.Equals(AcousticRegistry.Canonical(mat), "Fence", StringComparison.OrdinalIgnoreCase))
        {
            // A pale: a pressed steel section about 65 mm wide and 3 mm thick, pinned between rails a metre apart.
            lengthIsUp = standing;
            return new StruckThing
            {
                Material = mat, Shape = StruckShape.Bar, Length = MathF.Min(1f, standing ? up : longSide), Width = 0.065f, Thickness = 0.003f,
                Support = StruckSupport.Built, PlayMm = 0.3f, LooseMaterial = "Metal",
            };
        }
        bool thin = thick < 0.2f * shortSide;
        if (moves && thin)
            return new StruckThing
            {
                Material = mat, Shape = StruckShape.Plate, Length = longSide, Width = shortSide, Thickness = thick, Support = StruckSupport.Built,
                PlayMm = 2f, LooseMaterial = "Metal",
            };
        if (thin)
            return new StruckThing { Material = mat, Shape = StruckShape.Plate, Length = longSide, Width = shortSide, Thickness = thick, Support = StruckSupport.Built };
        if (longSide > 4f * MathF.Max(shortSide, thick))
            return new StruckThing
            {
                Material = mat, Shape = StruckShape.Bar, Length = longSide, Width = shortSide, Thickness = thick, Support = support,
            };
        lengthIsUp = false;
        return new StruckThing { Material = mat, Shape = StruckShape.Block, Length = across, Width = up, Thickness = thick, Support = support };
    }

    /// <summary>
    /// A person walking or running into something: a hand put out first (a palm slap), the toe of a shoe at the
    /// foot of it, then the shoulder and chest; the hand and the body at the walking pace, the toe a little
    /// slower (EST: the order and gaps of catching yourself). Where on the face, as fractions of it: the hand at
    /// <paramref name="handUp"/> of its height, the toe at the bottom, the body half way up; all near
    /// <paramref name="across"/>. <paramref name="lengthIsUp"/> says which of the thing's sides is the height
    /// (Describe's answer).
    /// </summary>
    public static IReadOnlyList<Blow> BodyBump(float closingSpeed, float across, float handUp, bool lengthIsUp, float seed01)
    {
        float s = MathF.Max(0.3f, closingSpeed);
        float jitter = 0.01f * seed01;
        Blow At(Striker who, float speed, float h, float v, float when)
        {
            h = Math.Clamp(h, 0.05f, 0.95f); v = Math.Clamp(v, 0.05f, 0.95f);
            return lengthIsUp ? new Blow(who, speed, v, h, when) : new Blow(who, speed, h, v, when);
        }
        return new[]
        {
            At(Striker.Palm, s, across, handUp, 0f),
            At(Striker.Toe, s * 0.8f, across + 0.05f, 0.05f, 0.03f + jitter),
            At(Striker.Body, s, across - 0.05f, 0.5f, 0.09f + jitter),
        };
    }
}
