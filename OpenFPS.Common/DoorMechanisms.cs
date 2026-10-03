using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenFPS.Common;

/// <summary>
/// The sound of each mechanical event a door makes (docs/DOOR_TYPES_EVENTS.md), synthesised to the
/// measured spec in docs/DOOR_TYPES.md.
///
/// Solid parts are CONTACT PLUS MODES: a force pulse of length tau strikes a body and the body rings
/// at its own modes. Material comes from the modes. The leaf's modes come from plate theory and the
/// leaf's own material and size: a solid plate (wood, a glass pane), a steel sandwich (two skins on a
/// honeycomb core, whose core shear makes the 1.0 m fire door's first mode 129 Hz and the 0.55 m lift
/// leaf's 175 Hz, as measured, from one core shear modulus), or an insulated glass unit (two panes
/// on an air spring, which lifts the radiating modes into a cluster just above the mass-air-mass
/// resonance). The leaf's edge members (stiles and rails: the door's own material, or aluminium round
/// a glass leaf) ring along their length at n c / 2L. How long all of it rings is the material's own
/// loss, the edge loss every hung leaf has, the honeycomb core's (low frequencies) and the glazing
/// gasket's (glass). A leaf hung on rollers keeps its members' rings; a leaf seated on hinges and
/// silencers loses them faster.
///
/// Two failures shaped this. A house door made of octave-band noise was rejected (2026-10-01, "scratchy
/// and grainy, wood and steel sound the same"), so nothing solid here is noise. A car door made of a
/// few resonators was rejected as "too tonal", so a leaf is hundreds of plate modes, thinned to a few
/// dozen per octave, never a handful. Only small hardware (a latch, a knob, a bar, a hook rod, a lock)
/// rings as a few clear tones, at the frequencies and Qs measured from it.
///
/// Noise is used only where the source is noise: rollers on a track, a motor's run, a brush seal
/// wiping the frame.
///
/// Rendered dry: the game's room adds the room. Each event is one buffer, normalised so its loudest
/// 2 ms frame sits at <see cref="FrameReference"/>; the level the server sends is that frame's level
/// at a metre (<see cref="RelativeDb"/> over the door's main hit), so the measured level steps between
/// events survive exactly.
/// </summary>
public static class DoorMechanisms
{
    public const string KeyPrefix = "doorsnd:";

    /// <summary>The loudest 2 ms frame of every event buffer, RMS. Leaves room above it for the crest
    /// of a steel click.</summary>
    public const float FrameReference = 0.158f;

    /// <summary>How long a key turn takes before the latch comes back, seconds.</summary>
    public const float KeySeconds = 0.36f;

    /// <summary>How long the brush seal wipes the frame before a glass leaf shuts, seconds.</summary>
    public const float SealSweepSeconds = 0.6f;

    /// <summary>
    /// Everything that decides one event's sound. The leaf: what it is made of (a registry material
    /// name), its width, height and thickness, the skin of a hollow (sandwich) leaf and the pane of an
    /// insulated unit. The travel: how long a motion lasts and which way.
    /// </summary>
    public readonly record struct Spec(DoorKind Kind, string Event, string Material, float Width, float Height,
                                       float Thickness, float SkinMetres = 0f, float PaneMetres = 0f,
                                       float Seconds = 0f, bool Opening = true);

    // ── Keys ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sound's name, which is also its identity in the client's buffer cache, so every number
    /// in it is rounded to what can be heard: centimetres of leaf, millimetres of thickness, tenths of
    /// a millimetre of skin and pane, tenths of a second of travel. Only a motion carries its travel.
    /// </summary>
    public static string Key(in Spec s)
    {
        bool motion = IsMotionEvent(s.Event);
        return string.Create(CultureInfo.InvariantCulture,
            $"{KeyPrefix}{DoorEvents.Slug(s.Kind)}:{s.Event}:{s.Material}:{Q(s.Width, 100)}:{Q(s.Height, 100)}:{Q(s.Thickness, 1000)}:"
          + $"{Q(s.SkinMetres, 10000)}:{Q(s.PaneMetres, 10000)}:{(motion ? Q(s.Seconds, 10) : 0)}:{(s.Opening ? 'o' : 'c')}");
    }

    private static int Q(float v, float scale) => (int)MathF.Round(MathF.Max(0f, v) * scale);

    public static bool TryParseKey(string? key, out Spec spec)
    {
        spec = default;
        if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
        var p = key[KeyPrefix.Length..].Split(':');
        if (p.Length != 10 || !DoorEvents.TryParseKind(p[0], out var kind) || p[1].Length == 0) return false;
        var f = new float[6];
        for (int i = 0; i < 6; i++)
            if (!int.TryParse(p[3 + i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return false;
            else f[i] = v;
        spec = new Spec(kind, p[1], p[2], f[0] / 100f, f[1] / 100f, f[2] / 1000f, f[3] / 10000f, f[4] / 10000f,
                        f[5] / 10f, p[9] == "o");
        return spec.Width > 0f && spec.Height > 0f;
    }

    /// <summary>Events that last as long as the leaf travels.</summary>
    public static bool IsMotionEvent(string ev) => ev is DoorEvents.Rollers or DoorEvents.Reopen;

    /// <summary>A key whose sound lasts the travel: the client lets it go if the leaf turns back.</summary>
    public static bool IsMotion(string? key)
        => TryParseKey(key, out var s) && IsMotionEvent(s.Event);

    // ── Levels ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An event's loudest 2 ms frame against the door's main hit, dB, as measured (docs/DOOR_TYPES.md);
    /// NaN for an event that makes no sound of its own (a swing with dry hinges, a steel door's
    /// sealed closer).
    /// </summary>
    public static float RelativeDb(DoorKind kind, string ev, bool opening = true) => (kind, ev) switch
    {
        (DoorKind.Hinged, DoorEvents.LatchRetract) => -16f,      // kyles: knob work 14-24 dB under the slam
        (DoorKind.Hinged, DoorEvents.Latch) => 0f,
        (DoorKind.PushBar, DoorEvents.Bar) => -1f,               // the last press contact, -1 dB re the slam
        (DoorKind.PushBar, DoorEvents.Latch) => 0f,
        (DoorKind.GlassPushBar, DoorEvents.Key) => -3f,          // vaztur: the throw, -3 dB re its slam
        (DoorKind.GlassPushBar, DoorEvents.LatchRetract) => -12f,
        (DoorKind.GlassPushBar, DoorEvents.Bar) => -1f,
        (DoorKind.GlassPushBar, DoorEvents.Closer) => -28f,      // the seal sweep, about 30 dB under the shut
        (DoorKind.GlassPushBar, DoorEvents.Latch) => 0f,
        (DoorKind.GlassPull, DoorEvents.Pull) => -8f,            // kraft, ifm: -5 to -13
        (DoorKind.GlassPull, DoorEvents.Closer) => -28f,
        (DoorKind.GlassPull, DoorEvents.Latch) => 0f,
        (DoorKind.AutoSliding, DoorEvents.MotorStart) => -3f,    // 4-6 dB over the run
        (DoorKind.AutoSliding, DoorEvents.Rollers) => -8f,       // the stops 4-15 dB over the run
        (DoorKind.AutoSliding, DoorEvents.Stop) => 0f,
        (DoorKind.AutoSliding, DoorEvents.Shut) => 0f,
        (DoorKind.AutoSliding, DoorEvents.Reopen) => -3f,
        (DoorKind.PatioSliding, DoorEvents.LatchRetract) => -14f,
        (DoorKind.PatioSliding, DoorEvents.Rollers) => -12f,
        (DoorKind.PatioSliding, DoorEvents.Stop) => 0f,          // kijjaz: the open stop as loud as the close
        (DoorKind.PatioSliding, DoorEvents.Latch) => 0f,
        (DoorKind.Elevator, DoorEvents.MotorStart) => opening ? -6f : -16f,   // the clutch, -1 to -11 re the bump
        (DoorKind.Elevator, DoorEvents.Rollers) => -12f,         // launchsite, krystian: motion 12-20 dB under the bump
        (DoorKind.Elevator, DoorEvents.Stop) => -30f,            // 15-25 dB under the motion
        (DoorKind.Elevator, DoorEvents.Shut) => 0f,
        (DoorKind.Elevator, DoorEvents.Reopen) => -12f,
        _ => float.NaN,
    };

    /// <summary>The leaf's mass, kg, from what it is made of and how it is built.</summary>
    public static float LeafMassKg(in Spec s) => Leaf.From(s).SurfaceMass * s.Width * s.Height;

    /// <summary>The leaf's modes, lowest first: frequency, impulse amplitude, T60. For instruments
    /// and tests.</summary>
    public static List<(float Hz, float Amplitude, float T60)> LeafModes(in Spec s)
    {
        var b = Leaf.From(s).Bank();
        var list = new List<(float, float, float)>(b.F.Count);
        for (int i = 0; i < b.F.Count; i++) list.Add((b.F[i], b.A[i], b.T60[i]));
        list.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return list;
    }

    /// <summary>
    /// The door's main hit at a metre, dB: the kinetic energy it arrives with (a hinged leaf turning
    /// about its edge has m v² / 6 at edge speed v; a sliding one m v² / 2), by the level law every
    /// impact in the game uses.
    /// </summary>
    public static float MainHitDb(float massKg, float arrivalSpeed, bool hinged)
        => PanelAcoustics.ImpactDb(massKg * arrivalSpeed * arrivalSpeed / (hinged ? 6f : 2f));

    /// <summary>
    /// How fast a leaf is going when it arrives, from its average speed over the travel: a closer or
    /// a hand arrives at its own speed, a hand-pushed slider slows a little at the end, a motor brakes
    /// to a creep before the stop (docs/DOOR_TYPES.md types 5 and 7).
    /// </summary>
    public static float ArrivalSpeed(float averageSpeed, bool slides, bool powered)
        => averageSpeed * (powered ? 0.3f : slides ? 0.8f : 1f);

    // ── Rendering ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One event, mono, at <paramref name="sampleRate"/>, deterministic for a seed. Its loudest 2 ms
    /// frame is <see cref="FrameReference"/>. Silent events come back as a few samples of nothing.
    /// </summary>
    public static float[] Render(in Spec s, int sampleRate, int seed)
    {
        var rng = new Random(seed * 7919 + Hash(s.Event) + (int)s.Kind * 31);
        var leaf = Leaf.From(s);
        var m = new Mix(sampleRate, rng);
        bool done = (s.Kind, s.Event) switch
        {
            (DoorKind.Hinged, DoorEvents.LatchRetract) => KnobRetract(m, leaf),
            (DoorKind.Hinged, DoorEvents.Latch) => KnobLatch(m, leaf),
            (DoorKind.PushBar, DoorEvents.Bar) => BarPress(m, leaf, aluminium: false),
            (DoorKind.PushBar, DoorEvents.Latch) => SteelSlam(m, leaf),
            (DoorKind.GlassPushBar, DoorEvents.Key) => KeyTurn(m, leaf),
            (DoorKind.GlassPushBar, DoorEvents.LatchRetract) => GlassRetract(m, leaf),
            (DoorKind.GlassPushBar, DoorEvents.Bar) => BarPress(m, leaf, aluminium: true),
            (DoorKind.GlassPushBar, DoorEvents.Closer) => SealSweep(m),
            (DoorKind.GlassPushBar, DoorEvents.Latch) => GlassSlam(m, leaf),
            (DoorKind.GlassPull, DoorEvents.Pull) => Pull(m, leaf),
            (DoorKind.GlassPull, DoorEvents.Closer) => SealSweep(m),
            (DoorKind.GlassPull, DoorEvents.Latch) => GlassShut(m, leaf),
            (DoorKind.AutoSliding, DoorEvents.MotorStart) => AutoStart(m, leaf),
            (DoorKind.AutoSliding, DoorEvents.Rollers) => Motion(m, s.Kind, s.Seconds, s.Opening, leaf, 0f),
            (DoorKind.AutoSliding, DoorEvents.Stop) => AutoStop(m, leaf, shut: false),
            (DoorKind.AutoSliding, DoorEvents.Shut) => AutoStop(m, leaf, shut: true),
            (DoorKind.AutoSliding, DoorEvents.Reopen) => AutoStart(m, leaf) && Motion(m, s.Kind, s.Seconds, true, leaf, -5f),
            (DoorKind.PatioSliding, DoorEvents.LatchRetract) => PatioUnlatch(m, leaf),
            (DoorKind.PatioSliding, DoorEvents.Rollers) => Motion(m, s.Kind, s.Seconds, s.Opening, leaf, 0f),
            (DoorKind.PatioSliding, DoorEvents.Stop) => PatioHit(m, leaf, latch: false),
            (DoorKind.PatioSliding, DoorEvents.Latch) => PatioHit(m, leaf, latch: true),
            (DoorKind.Elevator, DoorEvents.MotorStart) => LiftStart(m, leaf, s.Opening),
            (DoorKind.Elevator, DoorEvents.Rollers) => Motion(m, s.Kind, s.Seconds, s.Opening, leaf, 0f),
            (DoorKind.Elevator, DoorEvents.Stop) => LiftStop(m, leaf),
            (DoorKind.Elevator, DoorEvents.Shut) => LiftShut(m, leaf),
            (DoorKind.Elevator, DoorEvents.Reopen) => LiftStart(m, leaf, false) && Motion(m, s.Kind, s.Seconds, true, leaf, -2f),
            _ => false,
        };
        return done ? m.Finish() : new float[16];
    }

    // ── The leaf ────────────────────────────────────────────────────────────────────────────────

    private const float Poisson = 0.3f;
    /// <summary>Kraft honeycomb: what fills a hollow metal door, its density and its shear modulus.
    /// The shear modulus is the one number fitted: it puts the fire door's first mode at 129 Hz, and
    /// then, unchanged, the lift leaf's at 175 Hz (measured 172).</summary>
    private const float CoreKgM3 = 150f, CoreShearPa = 25.7e6f;
    /// <summary>The edge loss every hung leaf has: energy into its own frame members and fixings.</summary>
    private const float EdgeLoss = 0.0045f;
    /// <summary>A glass leaf's members: aluminium.</summary>
    private const float AluminiumC = 5100f, AluminiumLoss = 0.0001f;
    /// <summary>
    /// Plate modes kept per octave at least; the rest are folded into them. Where the leaf is lightly
    /// damped more are kept, enough that each mode's bandwidth reaches the next (0.69 / eta per
    /// octave): a real leaf's modes overlap up there, and a thinned set would stand out of the gaps
    /// between them as a chord.
    /// </summary>
    private const int ModesPerOctave = 36;
    private const int MostPerOctave = 360;
    private const float TopHz = 13000f;
    /// <summary>Timber's stiffness across the grain over along it.</summary>
    private const float WoodAcrossGrain = 0.07f;
    /// <summary>Torsional over longitudinal wave speed in a metal bar: sqrt(G / E), 0.62 for steel and aluminium.</summary>
    private const float Torsion = 0.62f;

    /// <summary>What a leaf is, as far as its sound goes.</summary>
    private sealed class Leaf
    {
        public float W, H, SurfaceMass, E, Rho, Loss;
        public bool Metal, Glass, Sandwich, Insulated, Hung;
        public float Skin, Depth, Pane, MassAirHz;
        /// <summary>Stiffness across the leaf over stiffness along it: wood bends far more easily
        /// across its grain (which runs up a door) than along it. 1 for steel and glass.</summary>
        public float Ortho = 1f;
        public float MemberC, MemberLoss;
        public bool MemberGlazed;
        public float[] EnvDb = Array.Empty<float>();
        public float MemberDb;
        public int Seed;
        private Bank? _bank;
        /// <summary>Every leaf heard so far: a building has a few kinds of door and each is worked out once.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Bank> Banks = new();

        public static Leaf From(in Spec s)
        {
            var mat = AcousticRegistry.GetProperties(s.Material);
            var l = new Leaf
            {
                W = MathF.Max(0.2f, s.Width), H = MathF.Max(0.3f, s.Height),
                Rho = MathF.Max(100f, mat.DensityKgM3), E = MathF.Max(0.5f, mat.YoungsModulusGPa) * 1e9f,
                Loss = MathF.Max(1e-5f, mat.LossFactor),
                Hung = DoorEvents.SlidesByDefault(s.Kind),
                Seed = Hash(string.Create(CultureInfo.InvariantCulture,
                    $"{s.Material}:{s.Width:F2}:{s.Height:F2}:{s.Thickness:F3}:{s.SkinMetres:F4}:{s.PaneMetres:F4}")),
            };
            l.Metal = l.Rho >= 4000f;
            l.Glass = !l.Metal && l.Rho is > 2000f and < 3500f && l.E >= 40e9f;
            float d = MathF.Max(0.003f, s.Thickness);
            if (s.SkinMetres > 0f && s.SkinMetres * 2f < d)
            {
                l.Sandwich = true; l.Skin = s.SkinMetres; l.Depth = d;
                l.SurfaceMass = 2f * l.Skin * l.Rho + (d - 2f * l.Skin) * CoreKgM3;
            }
            else if (l.Glass && s.PaneMetres > 0f && s.PaneMetres * 2f < d)
            {
                // Two panes on an air spring: each bends alone, and the air between them stiffens the
                // modes that change its volume.
                l.Insulated = true; l.Pane = s.PaneMetres; l.Depth = d;
                l.SurfaceMass = 2f * l.Pane * l.Rho;
                float gap = d - 2f * l.Pane, perPane = l.Pane * l.Rho;
                l.MassAirHz = MathF.Sqrt(1.2f * 343f * 343f / gap * 2f / perPane) / (2f * MathF.PI);
            }
            else { l.Depth = d; l.SurfaceMass = l.Rho * d; }

            // The members round the edge: the leaf's own stuff, or aluminium round glass.
            l.MemberC = l.Glass ? AluminiumC : MathF.Sqrt(l.E / l.Rho);
            l.MemberLoss = (l.Glass ? AluminiumLoss : l.Loss) + (l.Hung ? 0.0004f : 0.003f);
            // A glazed frame holds its pane in the same gasket that damps the pane.
            l.MemberGlazed = l.Glass;
            if (!l.Metal && !l.Glass) l.Ortho = WoodAcrossGrain;

            // How each kind of stuff radiates under a flat drive, octaves 63 Hz-16 kHz.
            l.EnvDb = l.Metal ? new[] { 0f, -6f, -8f, -5f, -4f, -4f, -3f, 0f, -6f }
                    : l.Glass ? new[] { -12f, 0f, -6f, -14f, -12f, -7f, -2f, 0f, -8f }
                    : new[] { -6f, -4f, -6f, -3f, 0f, 0f, -1f, -3f, -6f };
            l.MemberDb = l.Metal ? -10f : l.Glass ? -18f : -20f;
            return l;
        }

        /// <summary>Bending stiffness per width at wavenumber squared k2.</summary>
        public float D(float k2)
        {
            float nu = 1f - Poisson * Poisson;
            if (Sandwich)
            {
                float db = E * Skin * (Depth - Skin) * (Depth - Skin) / (2f * nu);
                float shear = CoreShearPa * (Depth - 2f * Skin);
                float skins = 2f * E * Skin * Skin * Skin / (12f * nu);
                return skins + 1f / (1f / db + k2 / shear);
            }
            float h = Insulated ? Pane : Depth;
            return E * h * h * h / (12f * nu);
        }

        /// <summary>Mass per area that one bending wave carries.</summary>
        public float BendingMass => Insulated ? Pane * Rho : SurfaceMass;

        public float Hz(float k2) => k2 / (2f * MathF.PI) * MathF.Sqrt(D(k2) / BendingMass);

        /// <summary>Mode (m, n): m half-waves across the width, n up the height.</summary>
        public float ModeHz(int m, int n)
        {
            float pi2 = MathF.PI * MathF.PI;
            float k2 = pi2 * (m * m / (W * W) + n * n / (H * H));
            if (Ortho >= 1f) return Hz(k2);
            // An orthotropic plate: D across, D along, and their geometric mean for the twist.
            float dy = D(k2), dx = Ortho * dy, h = MathF.Sqrt(dx * dy);
            float x = m * m / (W * W), y = n * n / (H * H);
            return MathF.PI / 2f * MathF.Sqrt((dx * x * x + 2f * h * x * y + dy * y * y) / BendingMass);
        }

        /// <summary>The leaf's loss factor at a frequency.</summary>
        public float Eta(float f)
        {
            // What a hung leaf loses through its edges falls as the bending wavelength shortens
            // (as one over the root of frequency), and so does what a timber leaf's in-situ loss is
            // made of: the residential door's 70-90 Hz modes have Q 40-50, its 1.2-1.6 kHz ones 160-190.
            float eta = Loss * MathF.Min(1f, MathF.Sqrt(250f / MathF.Max(1f, f)))
                      + EdgeLoss * MathF.Min(1f, MathF.Sqrt(2000f / MathF.Max(1f, f)));
            if (Sandwich) eta += 0.025f / (1f + (f / 400f) * (f / 400f));          // the core damps the low modes
            if (Glass) eta += 0.05f * MathF.Min(1f, MathF.Pow(75f / MathF.Max(1f, f), 0.67f));   // the gasket
            return eta;
        }

        public float Env(float f) => Gain(Interp(EnvDb, f));

        /// <summary>The leaf as a bank of modes: its plate modes, thinned, and its members.</summary>
        public Bank Bank()
        {
            if (_bank != null) return _bank;
            int cacheKey = Seed * 2 + (Hung ? 1 : 0);
            if (Banks.TryGetValue(cacheKey, out var known)) return _bank = known;
            var rng = new Random(Seed);
            var raw = new List<(float F, float W)>();
            float a = W, b = H, pi2 = MathF.PI * MathF.PI;
            const float x0 = 0.88f, y0 = 0.46f;     // struck near the latch stile, half way up
            float coincidence = TopHz;
            for (float k = 1f; k < 2000f; k *= 1.02f)
            {
                float f = Hz(k * k);
                if (2f * MathF.PI * f / k >= 343f) { coincidence = f; break; }
                if (f > TopHz) break;
            }
            for (int mm = 1; mm < 500; mm++)
            {
                if (ModeHz(mm, 1) > TopHz) break;
                for (int nn = 1; nn < 1000; nn++)
                {
                    float f = ModeHz(mm, nn);
                    if (f > TopHz) break;
                    bool oddOdd = (mm & 1) == 1 && (nn & 1) == 1;
                    if (Insulated && oddOdd) f = MathF.Sqrt(f * f + MassAirHz * MassAirHz);
                    if (f < 25f) continue;
                    float phi = 2f * MathF.Abs(MathF.Sin(mm * MathF.PI * x0) * MathF.Sin(nn * MathF.PI * y0));
                    float rad = f < coincidence ? (oddOdd ? 1f : 0.3f) : 1f;
                    raw.Add((f * (1f + 0.012f * Gauss(rng)), phi * rad));
                }
            }
            raw.Sort((p, q) => p.F.CompareTo(q.F));
            var freqs = new float[raw.Count];
            for (int i = 0; i < raw.Count; i++) freqs[i] = raw[i].F;
            var bank = new Bank();
            foreach (var (f, w) in raw)
            {
                int lo = LowerBound(freqs, f / MathF.Sqrt(2f)), hi = LowerBound(freqs, f * MathF.Sqrt(2f));
                int count = Math.Max(1, hi - lo);
                float wanted = Math.Clamp(0.8f / Eta(f), ModesPerOctave, MostPerOctave);
                float keep = MathF.Min(1f, wanted / count);
                if (rng.NextDouble() > keep) continue;
                float t60 = 2.2f / (Eta(f) * f) * (0.9f + 0.2f * (float)rng.NextDouble());
                // Each mode with its own sign: where it was struck and where it is heard from, it
                // moves one way or the other, so their tails below resonance do not pile up into a thump.
                float sign = rng.NextDouble() < 0.5 ? -1f : 1f;
                bank.Add(f, sign * w * Env(f) / MathF.Sqrt(count * keep), MathF.Min(t60, 3f));
            }
            // The members, two of each, never quite equal: each rings along its length (n c / 2L)
            // and twists about it (torsion travels at 0.62 c in steel and aluminium), the first two
            // of each. Stiles and rails of different lengths make these an inharmonic handful: the
            // lift leaf's are 0.75, 1.21, 1.5, 2.4, 2.9 and 4.6 kHz (measured 0.8, 1.25-1.28, 2.96).
            // A whole n c / 2L series would be a pitched note, which no door is.
            foreach (float len in new[] { H, H * 1.004f, W, W * 0.995f })
                for (int n = 1; n <= 2; n++)
                    foreach (float speed in new[] { MemberC, MemberC * Torsion })
                    {
                        float f = n * speed / (2f * len);
                        if (f > TopHz) continue;
                        float amp = Env(f) * Gain(MemberDb) * (0.5f + 0.5f * (float)rng.NextDouble()) / MathF.Sqrt(n)
                                  * (speed < MemberC ? 0.8f : 1f);
                        float loss = MemberLoss + (MemberGlazed ? 0.05f * MathF.Min(1f, MathF.Pow(75f / f, 0.67f)) : 0f);
                        bank.Add(f, amp, MathF.Min(2.2f / (loss * f), 4f));
                    }
            if (Banks.Count > 256) Banks.Clear();
            Banks[cacheKey] = bank;
            return _bank = bank;
        }

        /// <summary>The leaf struck at its edge by something hard: its members and its upper modes,
        /// not the pane's low ones, which a blow at the frame barely moves.</summary>
        public Bank Frame() => _frame ??= Above(800f);
        private Bank? _frame;

        /// <summary>Only the part of the bank above a frequency: what a light tap on glass rings.</summary>
        public Bank Above(float hz)
        {
            var b = new Bank();
            var all = Bank();
            for (int i = 0; i < all.F.Count; i++) if (all.F[i] >= hz) b.Add(all.F[i], all.A[i], all.T60[i]);
            return b;
        }
    }

    // ── Hardware, as measured ───────────────────────────────────────────────────────────────────

    /// <summary>A few clear tones: frequency, Q and level for each.</summary>
    private static Bank Hardware(params (float Hz, float Q, float Db)[] rings)
    {
        var b = new Bank();
        foreach (var (hz, q, db) in rings) b.Add(hz, Gain(db), q * 6.91f / (MathF.PI * hz));
        // ...over the rest of the mechanism: screws, springs, the plate it is fixed through, each with
        // modes of its own, small and heavily damped. Together they are the click under the ring, and
        // a recording's lines stand 10-27 dB over them, not over silence.
        var rng = new Random(rings.Length * 7 + (int)rings[0].Hz);
        float lowest = float.MaxValue;
        foreach (var r in rings) lowest = MathF.Min(lowest, r.Hz);
        float from = MathF.Max(700f, 0.5f * lowest), span = 14000f / from;     // the smaller the part, the higher its bed
        for (int i = 0; i < BedModes; i++)
        {
            float f = from * MathF.Pow(span, (i + (float)rng.NextDouble()) / BedModes);
            float q = 30f + 90f * (float)rng.NextDouble();
            // As loud an octave as the next: a higher mode dies sooner, so it starts a little louder.
            float sign = rng.NextDouble() < 0.5 ? -1f : 1f;
            b.Add(f, sign * Gain(BedDb) * MathF.Sqrt(f / from) * (0.4f + 0.6f * (float)rng.NextDouble()), q * 6.91f / (MathF.PI * f));
        }
        return b;
    }

    /// <summary>The mechanism's bed: how many small damped modes, 700 Hz-14 kHz, and how loud
    /// against the clearest ring.</summary>
    private const int BedModes = 120;
    private const float BedDb = 0f;

    /// <summary>A lever's cam and bolt sliding (Sudd): 4.6-10.6 kHz.</summary>
    private static Bank KnobTurnBank() => Hardware((4600f, 600f, 0f), (5600f, 800f, -2f), (7900f, 1000f, -3f), (10600f, 1200f, -4f),
                                                  (2400f, 150f, -6f), (3900f, 350f, -8f));
    /// <summary>The latch bolt and its knob: 2.4-3.9 kHz latch, 10-11.4 kHz knob (kyles, Sudd).</summary>
    /// <summary>The bolt dropping into the strike as the leaf shuts: the knob's own rings (kyles,
    /// 10-11.4 kHz, Q 1300-5400) say "metal knob"; the latch's are far under them.</summary>
    private static Bank KnobCloseBank() => Hardware((10030f, 2500f, -6f), (10340f, 2500f, -6f), (11350f, 2500f, -8f),
                                                   (2400f, 150f, -14f), (2850f, 200f, -16f), (3900f, 350f, -16f));
    private static Bank KnobLatchBank() => Hardware((2400f, 150f, -6f), (2850f, 200f, -9f), (3900f, 350f, -10f),
                                                   (10030f, 2500f, -16f), (10340f, 2500f, -15f), (11350f, 2500f, -16f));
    /// <summary>A steel push bar's pivots and rods (kyles): 1.44 kHz up to 15 kHz.</summary>
    private static Bank BarBank(bool aluminium)
    {
        float r = aluminium ? 1.3f : 1f, q = aluminium ? 2f : 1f;
        return Hardware((1440f * r, 110f * q, 0f), (2750f * r, 400f * q, -4f), (3900f * r, 260f * q, -3f), (4660f * r, 400f * q, -5f),
                        (9600f * r, 2000f * q, -8f), (MathF.Min(15000f * r, 18000f), 800f * q, -12f));
    }
    /// <summary>The bar coming back: 1.64 kHz and 8.9-9.1 kHz.</summary>
    private static Bank BarReturnBank(bool aluminium)
    {
        float r = aluminium ? 1.3f : 1f, q = aluminium ? 2f : 1f;
        return Hardware((1640f * r, 170f * q, 0f), (8900f * r, 350f * q, -4f), (9100f * r, 350f * q, -6f));
    }
    /// <summary>A steel latch bolt on its strike: the 4 kHz ride and the latch rings.</summary>
    private static Bank SteelLatchBank() => Hardware((3900f, 300f, 0f), (4300f, 300f, -2f), (5200f, 500f, -3f), (8400f, 650f, -4f),
                                                    (10500f, 800f, -8f), (12300f, 1000f, -10f));
    /// <summary>A storefront latch: 2.7-3.9 kHz and 7-9.4 kHz (vaztur).</summary>
    private static Bank GlassLatchBank() => Hardware((2760f, 400f, -6f), (3840f, 580f, -8f), (7000f, 400f, -9f), (9370f, 400f, -10f));
    /// <summary>A lock's cam and bolt (vaztur, FOSSarts): 1.5-8.7 kHz, the 8.6 kHz ring longest.</summary>
    private static Bank LockBank() => Hardware((1530f, 230f, -3f), (3580f, 300f, -4f), (5350f, 180f, 0f), (6810f, 300f, -2f),
                                              (8600f, 600f, 0f), (8730f, 500f, -4f));
    /// <summary>A pull handle on its stile (ifm): 760 Hz to 11 kHz.</summary>
    private static Bank PullBank() => Hardware((760f, 90f, -2f), (2130f, 150f, 0f), (6700f, 1300f, -8f), (7600f, 250f, -3f), (11000f, 700f, -6f));
    /// <summary>An automatic door's lock: steel, 3-6 kHz.</summary>
    private static Bank SlideLockBank() => Hardware((3200f, 300f, 0f), (4400f, 400f, -2f), (5700f, 400f, -4f));
    /// <summary>A lift door clutch: the 500 Hz cluster and a weak 3-6 kHz set.</summary>
    private static Bank ClutchBank() => Hardware((480f, 40f, 0f), (520f, 40f, -2f), (565f, 45f, -3f), (3300f, 200f, -12f),
                                                (4700f, 200f, -14f), (5900f, 200f, -15f));
    /// <summary>
    /// A patio door's hook rod, an aluminium bar 1.22 m long ringing along its length: n × 2.09 kHz,
    /// partials 0, -15, -20, -23, -34 dB, T60 0.155 s on the first rising to 0.75 s (applecorey).
    /// </summary>
    private static Bank HookBank()
    {
        var b = new Bank();
        float f0 = AluminiumC / (2f * 1.22f);
        float[] db = { 0f, -15f, -20f, -23f, -34f };
        float[] t60 = { 0.155f, 0.3f, 0.45f, 0.6f, 0.75f };
        for (int n = 1; n <= 5; n++) b.Add(n * f0, Gain(db[n - 1]), t60[n - 1]);
        return b;
    }

    // ── Type 1: knob ───────────────────────────────────────────────────────────────────────────

    private static bool KnobRetract(Mix m, Leaf leaf)
    {
        m.Length(1.3f);
        var turn = KnobTurnBank();
        var latch = KnobLatchBank();
        // The turn: 6-12 small contacts over 80-100 ms, the cam sliding and the bolt moving.
        var contacts = new List<(float, float, float)>();
        float t = 0.005f;
        int count = 6 + m.Rng.Next(7);
        for (int i = 0; i < count && t < 0.1f; i++)
        {
            contacts.Add((t, 0.5f + 0.5f * m.U(), 0.05f + 0.05f * m.U()));
            t += 0.005f + 0.012f * m.U();
        }
        m.Add(m.Strike(turn, contacts), -1f);
        m.Add(m.Strike(turn, (0.27f, 1f, 0.08f)), -8f);                   // the handle's end stop
        m.Add(m.Strike(latch, (0.59f, 1f, 0.07f)), 0f);                   // the bolt snaps out
        m.Add(m.Strike(leaf.Bank(), (0.59f, 1f, 0.25f)), -32f);           // ...through the leaf it is in
        m.Add(m.Strike(latch, (0.67f, 1f, 0.07f)), -7f);                  // the handle's own spring stop
        return true;
    }

    private static bool KnobLatch(Mix m, Leaf leaf)
    {
        float ride = 0.03f + 0.03f * m.U();
        float hit = 0.004f + ride;
        m.Length(hit + 1.1f);
        var latch = KnobCloseBank();
        // The bolt rides the strike's ramp: friction, a train of tiny contacts rising toward the hit.
        var rideContacts = new List<(float, float, float)>();
        var rideLeaf = new List<(float, float, float)>();
        for (float t = 0.004f; t < hit; t += 0.0005f + 0.0015f * m.U())
        {
            float up = Gain(-8f * (1f - (t - 0.004f) / ride)) * (0.4f + 0.6f * m.U());
            rideContacts.Add((t, up, 0.05f));
            rideLeaf.Add((t, up, 0.12f));
        }
        // Friction is many tiny contacts, and a train of them through high-Q rings is a squeak. What
        // they mostly move is the leaf's edge and the strike plate's own dense small modes.
        var ridePart = m.Strike(leaf.Frame(), rideContacts);
        Mix.Accumulate(ridePart, m.Strike(leaf.Bank(), rideLeaf), 1.2f);
        Mix.Accumulate(ridePart, m.Strike(latch, rideContacts), 0.15f);
        m.Add(ridePart, -24f);
        // The leaf on its stop and the bolt into the strike, within a few milliseconds.
        m.Add(m.Strike(leaf.Bank(), (hit, 1f, 0.45f)), 0f);
        var bolt = m.Strike(latch, (hit + 0.003f, 1f, 0.07f));
        Mix.Accumulate(bolt, m.Strike(leaf.Bank(), (hit + 0.003f, 1f, 0.07f)), 3f);   // through the leaf it is in
        m.Add(bolt, -1f);
        // The bolt and knob settling.
        var settle = new List<(float, float, float)>();
        int n = 3 + m.Rng.Next(6);
        for (int i = 0; i < n; i++) settle.Add((hit + 0.02f + 0.04f * m.U(), Gain(-6f * m.U()), 0.2f + 0.2f * m.U()));
        var settled = m.Strike(leaf.Frame(), settle);
        Mix.Accumulate(settled, m.Strike(latch, settle), 0.3f);
        m.Add(settled, -12f);
        return true;
    }

    // ── Type 2: push bar, and type 3's aluminium bar ───────────────────────────────────────────

    private static bool BarPress(Mix m, Leaf leaf, bool aluminium)
    {
        m.Length(0.9f);
        var bar = BarBank(aluminium);
        float spread = 0.07f + 0.05f * m.U();
        // Three contacts, the last (the bolt reaching its stop) loudest and duller.
        (float At, float Db, float Tau)[] press = { (0.005f, -7f, 0.05f), (0.005f + spread * 0.36f, -6f, 0.06f), (0.005f + spread, 0f, 0.15f) };
        var body = new List<(float, float, float)>();
        foreach (var (at, db, tau) in press)
        {
            m.Add(m.Strike(bar, (at, 1f, tau)), db);
            body.Add((at, Gain(db), tau));
            body.Add((at, Gain(db - 2f), 1f));
        }
        m.Add(m.Strike(leaf.Bank().Sparse(2), body), -10f);                       // the bar is bolted to the leaf
        float back = 0.005f + 0.24f + 0.08f * m.U();
        m.Add(m.Strike(BarReturnBank(aluminium), (back, 1f, 0.1f)), -4f);
        return true;
    }

    private static bool SteelSlam(Mix m, Leaf leaf)
    {
        float ride = 0.06f + 0.08f * m.U();
        float hit = 0.004f + ride;
        m.Length(hit + 1.2f);
        var latch = SteelLatchBank();
        var rc = new List<(float, float, float)>();
        for (float t = 0.004f; t < hit; t += 0.0006f + 0.002f * m.U())
            rc.Add((t, Gain(-9f * (1f - (t - 0.004f) / ride)) * (0.4f + 0.6f * m.U()), 0.05f));
        var ridden = m.Strike(leaf.Frame(), rc);
        Mix.Accumulate(ridden, m.Strike(latch, rc), 0.15f);
        m.Add(ridden, -19f);
        // The leaf onto its silencers, and the bolt and edge onto the steel frame.
        m.Add(m.Strike(leaf.Bank(), (hit, 1f, 3f)), -7f);
        var edge = m.Strike(leaf.Bank(), (hit + 0.001f, 1f, 0.05f));
        Mix.Accumulate(edge, m.Strike(latch, (hit + 0.002f, 1f, 0.05f)), 0.1f);
        m.Add(edge, 0f);
        // The bar, rods and bolt shaking in their guides: irregular contacts from the moment the
        // leaf lands, falling 20 dB over 300-500 ms, the leaf answering most of each.
        var leafHits = new List<(float, float, float)>();
        var edgeHits = new List<(float, float, float)>();
        float span = 0.3f + 0.2f * m.U(), from = hit + 0.03f + 0.03f * m.U();
        for (float t = from; t < from + span; t += 0.004f + 0.026f * m.U() * m.U())
        {
            float a = Gain(-20f * (t - from) / span) * (0.3f + 0.7f * m.U());
            leafHits.Add((t, a, 1f + m.U()));
            edgeHits.Add((t + 0.0005f, a, 0.15f + 0.3f * m.U()));
        }
        var rattle = m.Strike(leaf.Bank().Sparse(3), leafHits);
        Mix.Accumulate(rattle, m.Strike(leaf.Frame().Sparse(2), edgeHits), 0.8f);
        Mix.Accumulate(rattle, m.Strike(BarBank(false), edgeHits), 0.1f);
        m.Add(rattle, -7f);
        return true;
    }

    // ── Type 3: glass front door ────────────────────────────────────────────────────────────────

    private static bool KeyTurn(Mix m, Leaf leaf)
    {
        m.Length(KeySeconds + 0.7f);
        var lockBank = LockBank();
        int cams = 2 + m.Rng.Next(4);
        float t = 0.01f;
        for (int i = 0; i < cams; i++)
        {
            m.Add(m.Strike(lockBank, (t, 1f, 0.05f)), -15f - 5f * m.U());
            t += (KeySeconds - 0.08f) / cams * (0.7f + 0.6f * m.U());
            t = MathF.Min(t, KeySeconds - 0.06f);
        }
        m.Add(m.Strike(lockBank, (KeySeconds - 0.03f, 1f, 0.05f)), 0f);          // the bolt thrown
        m.Add(m.Strike(leaf.Frame(), (KeySeconds - 0.03f, 1f, 0.05f)), -8f);     // in its case, in the stile
        m.Add(m.Strike(leaf.Bank(), (KeySeconds - 0.03f, 1f, 1f)), -14f);
        m.Add(m.Strike(lockBank, (KeySeconds + 0.03f, 1f, 0.05f)), -14f);        // the cylinder's own stop
        return true;
    }

    private static bool GlassRetract(Mix m, Leaf leaf)
    {
        m.Length(0.6f);
        m.Add(m.Strike(GlassLatchBank(), (0.005f, 1f, 0.07f)), 0f);
        m.Add(m.Strike(leaf.Frame(), (0.005f, 1f, 0.07f)), -6f);
        m.Add(m.Strike(leaf.Bank(), (0.005f, 1f, 0.3f)), -16f);
        return true;
    }

    private static bool GlassSlam(Mix m, Leaf leaf)
    {
        const float ride = 0.064f;
        float hit = 0.004f + ride;
        m.Length(hit + 1.5f);
        var latch = GlassLatchBank();
        var rc = new List<(float, float, float)>();
        for (float t = 0.004f; t < hit; t += 0.0006f + 0.002f * m.U())
            rc.Add((t, Gain(-8f * (1f - (t - 0.004f) / ride)) * (0.4f + 0.6f * m.U()), 0.05f));
        var ridden = m.Strike(leaf.Frame(), rc);
        Mix.Accumulate(ridden, m.Strike(latch, rc), 0.15f);
        m.Add(ridden, -31f);
        // The leaf through its gasket, the stile on the frame, the bolt into the strike.
        m.Add(m.Strike(leaf.Bank(), (hit, 1f, 3f)), -16f);
        m.Add(m.Strike(leaf.Frame(), (hit + 0.0015f, 1f, 0.05f)), -1f);
        m.Add(m.Strike(latch, (hit + 0.003f, 1f, 0.05f)), -8f);
        // The pane shifting in its gasket, the leaf rocking on the latch: the sound of a glass door.
        var rattle = new List<(float, float, float)>();
        float span = 0.8f + 0.1f * m.U();
        // Densest while the leaf still has its swing in it, sparser as it settles.
        for (float t = hit + 0.015f + 0.02f * m.U(); t < hit + 0.09f + span; )
        {
            float settled = MathF.Max(0f, t - hit - 0.09f) / span;
            rattle.Add((t, Gain(-28f * settled) * (0.3f + 0.7f * m.U()), 0.05f + 0.2f * m.U()));
            t += 0.003f + (0.006f + 0.025f * settled) * m.U();
        }
        var rattled = m.Strike(leaf.Frame().Sparse(2), rattle);
        Mix.Accumulate(rattled, m.Strike(latch, rattle), 0.1f);
        m.Add(rattled, -2f);
        return true;
    }

    /// <summary>A brush seal wiping the frame over the last of a closer's sweep: noise in the 4 and
    /// 8 kHz octaves, rising with the leaf, ending at the shut.</summary>
    private static bool SealSweep(Mix m)
    {
        float len = SealSweepSeconds;
        m.Length(len + 0.03f);
        var noise = m.Shaped(new[] { -60f, -60f, -60f, -40f, -30f, -15f, -2f, 0f, -25f });
        var part = new float[m.N];
        for (int i = 0; i < part.Length; i++)
        {
            float t = (float)i / m.Sr;
            float rise = Smooth(t / 0.35f) * (0.7f + 0.3f * t / len);
            float end = t < len ? 1f : MathF.Max(0f, 1f - (t - len) / 0.012f);
            part[i] = noise[i] * rise * end;
        }
        m.Add(part, 0f);
        return true;
    }

    // ── Type 4: pulled glass door ───────────────────────────────────────────────────────────────

    private static bool Pull(Mix m, Leaf leaf)
    {
        m.Length(0.7f);
        m.Add(m.Strike(PullBank(), (0.005f, 1f, 0.1f)), 0f);
        m.Add(m.Strike(leaf.Frame(), (0.005f, 1f, 0.1f)), -2f);
        m.Add(m.Strike(leaf.Bank(), (0.005f, 1f, 1f)), -14f);
        return true;
    }

    private static bool GlassShut(Mix m, Leaf leaf)
    {
        m.Length(1.3f);
        float mass = leaf.SurfaceMass * leaf.W * leaf.H;
        float tau = mass > 80f ? 5f : 2f;          // a heavy door sinks into its seal slower
        m.Add(m.Strike(leaf.Bank(), (0.005f, 1f, tau)), mass > 80f ? 0f : -12f);
        m.Add(m.Strike(leaf.Frame(), (0.006f, 1f, 0.14f)), 0f);    // the stile on the frame stop's rubber strip
        return true;
    }

    // ── Type 5: automatic slider ────────────────────────────────────────────────────────────────

    private static bool AutoStart(Mix m, Leaf leaf)
    {
        m.Length(MathF.Max(m.Seconds, 0.6f));
        m.Add(m.Strike(leaf.Frame(), (0.005f, 1f, 0.3f)), 0f);   // the belt taking up the carriage
        m.Add(m.Strike(leaf.Bank(), (0.005f, 1f, 3f)), -6f);
        return true;
    }

    private static bool AutoStop(Mix m, Leaf leaf, bool shut)
    {
        m.Length(1.2f);
        m.Add(m.Strike(leaf.Bank(), (0.005f, 1f, 4f)), -18f);        // the leaf into the rubber stop
        m.Add(m.Strike(leaf.Frame(), (0.006f, 1f, 0.4f)), 0f);       // the aluminium carriage
        if (shut) m.Add(m.Strike(SlideLockBank(), (0.5f, 1f, 0.1f)), -6f);
        else m.Add(m.Strike(leaf.Bank(), (0.45f, 1f, 3f)), -1f);      // the leaf settling
        return true;
    }

    // ── Type 6: patio slider ────────────────────────────────────────────────────────────────────

    private static bool PatioUnlatch(Mix m, Leaf leaf)
    {
        m.Length(0.7f);
        m.Add(m.Strike(leaf.Bank(), (0.005f, 1f, 0.12f)), 0f);        // the lever flipped
        m.Add(m.Strike(HookBank(), (0.03f, 1f, 0.05f)), -9f);         // the hook lets go
        return true;
    }

    private static bool PatioHit(Mix m, Leaf leaf, bool latch)
    {
        m.Length(latch ? 1.6f : 1.0f);
        m.Add(m.Strike(leaf.Bank(), (0.005f, 1f, 3f)), 0f);
        m.Add(m.Strike(leaf.Frame(), (0.006f, 1f, 0.3f)), -6f);      // the sash's interlock
        m.Add(m.Strike(leaf.Bank(), (0.35f + 0.15f * m.U(), 1f, 4f)), -20f - 6f * m.U());   // the bounce
        if (latch)
        {
            m.Add(m.Strike(leaf.Bank(), (0.62f, 1f, 0.12f)), -12f);    // the lever
            m.Add(m.Strike(HookBank(), (0.68f, 1f, 0.05f)), -6f);      // the hook engages
        }
        return true;
    }

    // ── Type 7: lift doors ──────────────────────────────────────────────────────────────────────

    private static bool LiftStart(Mix m, Leaf leaf, bool opening)
    {
        m.Length(MathF.Max(m.Seconds, 1.0f));
        if (opening)
        {
            m.Add(m.Strike(leaf.Bank(), (0.005f, 1f, 4f)), 0f);      // the clutch takes the landing door
            m.Add(m.Strike(ClutchBank(), (0.008f, 1f, 0.3f)), -8f);
        }
        else
        {
            m.Add(m.Strike(leaf.Bank(), (0.005f, 1f, 6f)), 0f);          // the operator taking up
            m.Add(m.Strike(ClutchBank(), (0.006f, 1f, 0.5f)), -10f);
        }
        return true;
    }

    private static bool LiftStop(Mix m, Leaf leaf)
    {
        m.Length(0.8f);
        m.Add(m.Strike(leaf.Bank(), (0.005f, 1f, 6f)), 0f);
        m.Add(m.Strike(leaf.Bank(), (0.125f, 1f, 6f)), -3f);
        return true;
    }

    private static bool LiftShut(Mix m, Leaf leaf)
    {
        m.Length(3.0f);
        m.Add(m.Strike(leaf.Bank(), (0.005f, 1f, 5f)), 0f);              // the rubber edges meet
        m.Add(m.Strike(leaf.Bank(), (0.006f, 1f, 0.1f)), -14f);          // the hangers and locks, steel
        m.Add(m.Strike(leaf.Bank(), (0.07f, 1f, 6f)), -1.5f);            // rebounds
        m.Add(m.Strike(leaf.Bank(), (0.23f, 1f, 6f)), -4f);
        return true;
    }

    // ── Motion: rollers, belts, motors ─────────────────────────────────────────────────────────

    /// <summary>
    /// A leaf travelling for <paramref name="seconds"/>: its speed over the travel, rolling noise
    /// (wheels and sheaves on a track, the only noise here) at a level going as speed squared, and
    /// for a motor its tones. An automatic door's gear tone runs with speed (1.85 kHz at the opening
    /// run, braking down below 0.85 kHz) beside a fixed 700 Hz line; a lift's operator hums at 250,
    /// 700 and 1000 Hz times its speed. A hand-pushed patio leaf rolls with small grit ticks.
    /// </summary>
    private static bool Motion(Mix m, DoorKind kind, float seconds, bool opening, Leaf leaf, float db)
    {
        float d = Math.Clamp(seconds, 0.3f, 8f);
        float start = m.N > 0 ? 0.01f : 0.005f;
        m.Length(MathF.Max(m.Seconds, start + d + 0.05f));
        var v = new float[m.N];
        float ratio = 1f;
        for (int i = 0; i < v.Length; i++)
        {
            float t = (float)i / m.Sr - start;
            v[i] = t < 0f || t > d ? 0f : kind switch
            {
                DoorKind.AutoSliding => AutoSpeed(t, d, opening),
                DoorKind.Elevator => LiftSpeed(t, d, opening),
                _ => HandSpeed(t, d),
            };
        }
        // Wander: nothing on a track runs at exactly one speed.
        var wander = SlowNoise(m.N, m.Sr, m.Rng, 3f);
        float[] bands;
        switch (kind)
        {
            case DoorKind.AutoSliding:
                ratio = opening ? 1f : 0.65f;      // the operator closes at 60-70 % of its opening speed
                bands = new[] { -20f, -12f, -4f, -3f, 0f, -8f, -12f, -13f, -25f };
                break;
            case DoorKind.Elevator:
                ratio = opening ? 1f : 0.7f;
                bands = new[] { -6f, 0f, 0f, -1f, -6f, -14f, -17f, -19f, -30f };
                break;
            default:
                bands = new[] { -14f, -6f, 0f, 0f, -4f, -13f, -19f, -20f, -30f };   // healthy rollers (goblinjack)
                break;
        }
        var rolling = m.Shaped(bands);
        var part = new float[m.N];
        for (int i = 0; i < part.Length; i++)
        {
            float s = v[i] * (1f + 0.04f * wander[i]);
            part[i] = rolling[i] * s * s * ratio;
        }
        // The rolling noise's 1 kHz octave, for setting the tones against it.
        float octave = 0.17f * Gain(Interp(bands, 1000f));
        if (kind == DoorKind.AutoSliding)
        {
            float phase = 0f, fixedPhase = 0f, gate = 0f;
            for (int i = 0; i < part.Length; i++)
            {
                float s = v[i] * ratio;
                float f = (600f + 1250f * s) * (1f + 0.01f * wander[i]);
                phase += 2f * MathF.PI * f / m.Sr;
                if (phase > 2f * MathF.PI * 64f) phase -= 2f * MathF.PI * 64f;
                gate += ((v[i] > 0.02f ? 1f : 0f) - gate) * 0.0005f;
                fixedPhase += 2f * MathF.PI * 700f / m.Sr;
                if (fixedPhase > 2f * MathF.PI) fixedPhase -= 2f * MathF.PI;
                float tone = MathF.Sin(phase) + 0.25f * MathF.Sin(2f * phase) + 0.22f * MathF.Sin(3f * phase);
                part[i] += octave * 0.35f * (s * tone * ratio + 1.0f * gate * ratio * MathF.Sin(fixedPhase));
            }
        }
        else if (kind == DoorKind.Elevator)
        {
            float[] runs = { 250f, 700f, 1000f };
            float[] lv = { 1f, 0.7f, 0.5f };
            var ph = new float[3];
            float low = 0.17f * Gain(Interp(bands, 250f));
            for (int i = 0; i < part.Length; i++)
            {
                float s = v[i] * ratio;
                for (int k = 0; k < 3; k++)
                {
                    float f = MathF.Max(30f, runs[k] * s * (1f + 0.015f * wander[i]));
                    ph[k] += 2f * MathF.PI * f / m.Sr;
                    if (ph[k] > 2f * MathF.PI) ph[k] -= 2f * MathF.PI;
                    part[i] += low * 0.35f * lv[k] * s * MathF.Sin(ph[k]);
                }
            }
        }
        m.Add(part, db);
        if (kind == DoorKind.PatioSliding)
        {
            // Grit and track joints: a few ticks a second while it rolls, into the glass.
            var ticks = new List<(float, float, float)>();
            for (int i = 0; i < v.Length; i += m.Sr / 100)
                if (m.U() < v[i] * 5f / 100f) ticks.Add(((float)i / m.Sr, 0.5f + 0.5f * m.U(), 0.1f + 0.2f * m.U()));
            if (ticks.Count > 0) m.Add(m.Strike(leaf.Bank().Sparse(3), ticks), db - 9f);
        }
        return true;
    }

    private static float AutoSpeed(float t, float d, bool opening)
    {
        float ramp = MathF.Min(opening ? 0.3f : 0.6f, 0.35f * d), brake = MathF.Min(0.22f, 0.2f * d);
        if (t < ramp) return Smooth(t / ramp);
        if (t > d - brake) return 1f - 0.88f * Smooth((t - (d - brake)) / brake);
        return 1f;
    }

    private static float LiftSpeed(float t, float d, bool opening)
    {
        if (opening)
        {
            float ramp = MathF.Min(1f, 0.33f * d), slow = MathF.Min(0.9f, 0.3f * d);
            if (t < ramp) return Smooth(t / ramp);
            if (t > d - slow) return 1f - 0.85f * Smooth((t - (d - slow)) / slow);
            return 1f;
        }
        // Closing: a run, then the operator slows the leaves to a creep before they meet.
        float r = MathF.Min(0.6f, 0.25f * d), creep = MathF.Min(0.7f, 0.28f * d), into = 0.15f;
        if (t < r) return Smooth(t / r);
        if (t > d - creep) return 0.25f;
        if (t > d - creep - into) return 1f - 0.75f * Smooth((t - (d - creep - into)) / into);
        return 1f;
    }

    private static float HandSpeed(float t, float d)
    {
        float rise = MathF.Min(0.3f, 0.3f * d), fall = MathF.Min(0.12f, 0.15f * d);
        if (t < rise) return Smooth(t / rise);
        if (t > d - fall) return 1f - 0.2f * Smooth((t - (d - fall)) / fall);
        return 1f;
    }

    // ── The engine ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Modes: frequency, amplitude of the impulse response, T60.</summary>
    private sealed class Bank
    {
        public readonly List<float> F = new(), A = new(), T60 = new();
        public void Add(float f, float a, float t60) { F.Add(f); A.Add(a); T60.Add(MathF.Max(0.003f, t60)); }

        /// <summary>Every k-th mode, each carrying the energy of the k it stands for: for many quiet
        /// contacts (a rattle, grit ticks) that would otherwise ring the whole bank for a second.</summary>
        public Bank Sparse(int k)
        {
            var b = new Bank();
            float g = MathF.Sqrt(k);
            for (int i = 0; i < F.Count; i += k) b.Add(F[i], A[i] * g, T60[i]);
            return b;
        }
    }

    /// <summary>One event's buffer, built from parts each set to its own 2 ms peak level.</summary>
    private sealed class Mix
    {
        public readonly int Sr;
        public readonly Random Rng;
        public float[] Out = Array.Empty<float>();
        public int N => Out.Length;
        public float Seconds => (float)Out.Length / Sr;
        public Mix(int sr, Random rng) { Sr = sr; Rng = rng; }

        public float U() => (float)Rng.NextDouble();

        public void Length(float seconds)
        {
            int n = (int)(seconds * Sr);
            if (n <= Out.Length) return;
            var o = new float[n];
            Array.Copy(Out, o, Out.Length);
            Out = o;
        }

        /// <summary>Adds a part with its loudest 2 ms frame at <paramref name="db"/> (0 dB is the
        /// event's own top).</summary>
        public void Add(float[] part, float db)
        {
            float peak = PeakFrame(part, Sr);
            if (peak <= 1e-12f) return;
            float g = Gain(db) / peak;
            for (int i = 0; i < Math.Min(part.Length, Out.Length); i++) Out[i] += part[i] * g;
        }

        public static void Accumulate(float[] into, float[] part, float gain)
        {
            for (int i = 0; i < Math.Min(into.Length, part.Length); i++) into[i] += part[i] * gain;
        }

        public float[] Strike(Bank bank, (float At, float Amp, float TauMs) c) => Strike(bank, new[] { c });

        /// <summary>Contacts into a bank: each a raised-cosine force pulse of unit area, so tau sets
        /// only how far up the spectrum it reaches.</summary>
        public float[] Strike(Bank bank, IReadOnlyList<(float At, float Amp, float TauMs)> contacts)
        {
            var force = new float[N];
            int first = N, last = 0;
            foreach (var (at, amp, tau) in contacts)
            {
                int len = Math.Max(1, (int)MathF.Round(tau * 1e-3f * Sr));
                int s0 = (int)(at * Sr);
                for (int i = 0; i < len; i++)
                {
                    int k = s0 + i;
                    if (k < 0 || k >= N) continue;
                    force[k] += amp * (1f - MathF.Cos(2f * MathF.PI * (i + 0.5f) / len)) / len;
                    first = Math.Min(first, k); last = Math.Max(last, k);
                }
            }
            var y = new float[N];
            if (first > last) return y;
            // What each contact can reach: a raised cosine of length tau is flat to about 0.5 / tau
            // and falls away steeply above 2 / tau. A mode the drive cannot reach is not rendered.
            var taus = new Dictionary<int, float>();
            foreach (var (_, amp, tau) in contacts)
            {
                int key = (int)MathF.Round(tau * 100f);
                taus[key] = MathF.Max(taus.TryGetValue(key, out float a) ? a : 0f, MathF.Abs(amp));
            }
            var reach = new float[bank.F.Count];
            float most = 0f;
            for (int q = 0; q < reach.Length; q++)
            {
                float best = 0f;
                foreach (var (key, amp) in taus) best = MathF.Max(best, amp * Pulse(bank.F[q] * MathF.Max(1, key) * 1e-5f));
                reach[q] = best * MathF.Abs(bank.A[q]);
                most = MathF.Max(most, reach[q]);
            }
            // The modes worth rendering, longest first, run four at a time: each resonator waits on
            // its own last sample, so four side by side cost little more than one.
            var live = new List<int>(bank.F.Count);
            for (int q = 0; q < bank.F.Count; q++)
                if (bank.F[q] < 0.45f * Sr && reach[q] >= most * 1e-3f) live.Add(q);
            live.Sort((p, q) => bank.T60[q].CompareTo(bank.T60[p]));
            var c = new double[4 * 3];
            for (int k = 0; k < live.Count; k += 4)
            {
                int n = Math.Min(4, live.Count - k);
                Array.Clear(c);
                for (int j = 0; j < n; j++)
                {
                    int q = live[k + j];
                    double w = 2.0 * Math.PI * bank.F[q] / Sr;
                    double r = Math.Exp(-6.9078 / (bank.T60[q] * Sr));
                    c[j * 3] = 2.0 * r * Math.Cos(w); c[j * 3 + 1] = -r * r; c[j * 3 + 2] = bank.A[q] * Math.Sin(w);
                }
                int end = Math.Min(N, last + (int)(bank.T60[live[k]] * 1.15f * Sr) + 1);
                double a10 = c[0], a20 = c[1], g0 = c[2], a11 = c[3], a21 = c[4], g1 = c[5];
                double a12 = c[6], a22 = c[7], g2 = c[8], a13 = c[9], a23 = c[10], g3 = c[11];
                double p0 = 0, q0 = 0, p1 = 0, q1 = 0, p2 = 0, q2 = 0, p3 = 0, q3 = 0;
                for (int i = first; i < end; i++)
                {
                    double x = force[i];
                    double v0 = a10 * p0 + a20 * q0 + g0 * x;
                    double v1 = a11 * p1 + a21 * q1 + g1 * x;
                    double v2 = a12 * p2 + a22 * q2 + g2 * x;
                    double v3 = a13 * p3 + a23 * q3 + g3 * x;
                    y[i] += (float)(v0 + v1 + v2 + v3);
                    q0 = p0; p0 = v0; q1 = p1; p1 = v1; q2 = p2; p2 = v2; q3 = p3; p3 = v3;
                }
            }
            return y;
        }

        /// <summary>White noise shaped to octave levels, dB, 63 Hz-16 kHz.</summary>
        public float[] Shaped(float[] bandsDb)
        {
            int pre = Sr / 10;
            var white = new float[N + pre];
            for (int i = 0; i < white.Length; i++) white[i] = (U() * 2f - 1f) * 1.7320508f;
            var y = new float[N];
            for (int b = 0; b < SoundMeasure.Centres.Length; b++)
            {
                if (bandsDb[b] <= -59f) continue;
                float c = SoundMeasure.Centres[b];
                if (c / MathF.Sqrt(2f) >= 0.45f * Sr) continue;
                var x = (float[])white.Clone();
                CarDoor.ButterworthBandPass(x, c / MathF.Sqrt(2f), MathF.Min(c * MathF.Sqrt(2f), 0.47f * Sr), Sr);
                // White noise has equal power per hertz, so each octave holds twice the one below.
                float g = Gain(bandsDb[b]) / MathF.Sqrt(c / 1000f);
                for (int i = 0; i < N; i++) y[i] += x[i + pre] * g;
            }
            return y;
        }

        /// <summary>The finished event: its top 2 ms frame at the reference, a short fade at the end.</summary>
        public float[] Finish()
        {
            float peak = PeakFrame(Out, Sr);
            if (peak <= 1e-12f) return new float[16];
            float g = FrameReference / peak;
            float crest = 0f;
            foreach (float v in Out) crest = MathF.Max(crest, MathF.Abs(v));
            // A click whose crest would clip is brought under full scale, and its level with it.
            if (crest * g > 0.99f) g = 0.99f / crest;
            int fade = Math.Min(Out.Length, Sr / 50);
            for (int i = 0; i < Out.Length; i++)
            {
                float f = i >= Out.Length - fade ? (float)(Out.Length - i) / fade : 1f;
                Out[i] *= g * f;
            }
            return Out;
        }
    }

    /// <summary>A unit-area raised-cosine pulse's spectrum at f times its length.</summary>
    private static float Pulse(float ft)
    {
        if (ft < 1e-4f) return 1f;
        if (MathF.Abs(ft - 1f) < 1e-3f) return 0.5f;
        float x = MathF.PI * ft;
        return MathF.Abs(MathF.Sin(x) / x / (1f - ft * ft));
    }

    /// <summary>The loudest 2 ms frame's RMS, linear.</summary>
    private static float PeakFrame(float[] x, int sr)
    {
        int n = Math.Max(1, sr / 500);
        double best = 0;
        for (int f = 0; f + n <= x.Length; f += n)
        {
            double s = 0;
            for (int i = f; i < f + n; i++) s += (double)x[i] * x[i];
            best = Math.Max(best, s / n);
        }
        return (float)Math.Sqrt(best);
    }

    private static float[] SlowNoise(int n, int sr, Random rng, float hz)
    {
        var y = new float[n];
        float a = 1f - MathF.Exp(-2f * MathF.PI * hz / sr), s = 0f, s2 = 0f;
        for (int i = 0; i < n; i++)
        {
            s += ((float)rng.NextDouble() * 2f - 1f - s) * a;
            s2 += (s - s2) * a;
            y[i] = s2 * 6f;
        }
        return y;
    }

    private static float Smooth(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }

    private static float Gain(float db) => MathF.Pow(10f, db / 20f);

    /// <summary>Octave values (63 Hz-16 kHz) read at any frequency, straight lines in log frequency.</summary>
    private static float Interp(float[] octaves, float f)
    {
        float x = MathF.Log2(MathF.Max(1f, f) / 63f);
        if (x <= 0f) return octaves[0];
        if (x >= octaves.Length - 1) return octaves[^1];
        int i = (int)x;
        return octaves[i] + (octaves[i + 1] - octaves[i]) * (x - i);
    }

    private static float Gauss(Random rng)
    {
        double u = 1.0 - rng.NextDouble(), v = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v));
    }

    private static int LowerBound(float[] sorted, float v)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi) { int mid = (lo + hi) / 2; if (sorted[mid] < v) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>A hash that is the same in every process (string.GetHashCode is not).</summary>
    private static int Hash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return (int)(h & 0x7fffffff);
        }
    }
}
