using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

namespace OpenFPS.Common;

/// <summary>
/// A window shot out, simulated as the glass it is: the pane struck, cracked or diced, every fragment a
/// small free body with its own modes, the fragments grinding out of the frame, knocking into each other
/// and the sill, falling with drag, and landing on whatever is under the window with bounces, breakage,
/// skittering and a pile that builds. The sound is the sum of what every body does, in pascals at a metre.
///
/// Cody, 2026-10-04: the old glass "sounds fake and not realistic". It was one hiss for the break and a
/// handful of single-mode knocks for the pieces (GlassSound before this), which is a description of glass
/// rather than glass.
///
/// THE PANE. A clamped rectangular plate (glazing bead), its bending modes from E, rho and the thickness:
/// f_mn = (pi/2) sqrt(D/m'') [((m+0.35)/a)^2 + ((n+0.35)/b)^2], the 0.35 standing in for clamped edges
/// (it puts a clamped square's fundamental at 1.82 times the simply supported one, Leissa's 35.99/19.74).
/// The round's impulse J = m dv (dv: the tenth of its speed the pane takes off it, as CombatService flies
/// it) over its transit time 2h/v excites each mode by its shape at the hole, and the modes with a net
/// volume velocity (odd-odd) radiate as a baffled piston: p = rho0 dQ/dt / (2 pi r). Summed, that is the
/// strike: at t = 0 every mode is in phase and the sum is rho0 F(t) / (2 pi m'' r), the point-driven plate.
/// The pane's modes are cut as the cracks cross it: radial cracks (annealed) or the dicing front
/// (tempered) run at the terminal crack speed, about 1500 m/s in soda-lime glass (Doll 1975, in Sundaram
/// and Tippur 2018, J. Mech. Phys. Solids 120), so a pane is pieces within about a millisecond, and the
/// vibration energy it held at that moment is handed to the pieces, weighted towards the hole.
///
/// THE PIECES. Annealed glass breaks into sectors between radial cracks, cut by a few concentric cracks near
/// the hole, the sectors running out to the frame as long daggers, with slivers along the cracks and a
/// spray of fines from the cone under the hole. Tempered glass dices: EN 12150 asks at least 40 pieces in a
/// 50 mm square (4-12 mm glass); the model uses twice that, about 5.6 mm dice in 6 mm glass, every one of
/// them, counted (a 1.2 x 1.6 m pane is about 60 000). Its residual stress (surface compression at least
/// 69 MPa by the standard; 100 MPa is taken, so 50 MPa central tension, a parabolic profile) stores
/// u = (1 - nu) 0.8 sigma_t^2 / E, about 22 kJ/m3; what the new crack faces do not take (G_c = K_IC^2 / E,
/// K_IC 0.75 MPa m^0.5) is partly the dice's kinetic energy, which throws them at about 2 m/s: the pop.
/// Part of the diced sheet hangs together for a moment and falls in clumps, which break up on landing.
///
/// A FRAGMENT'S MODES. A plate-like piece: the free plate's lowest modes from Leissa's free square plate
/// coefficients (lambda^2 = 13.47, 19.60, 24.27, 34.80, 34.80, 61.09, 61.09, 63.69, 69.27; NASA SP-160)
/// on the piece's own area, spread by its irregular shape; above them the plate's modal density,
/// n = S / (2 sqrt(D/m'')) per hertz. A sliver (length four times its width or more): a free-free bar,
/// beta L = 4.730, 7.853, 10.996, 14.137, then (2k + 1) pi / 2, in both planes. A die: its first mode is
/// near 250 kHz, so a die has no note at all, only the click of its contact. Glass's own loss factor
/// (0.0003) is read off the recordings' decays (<see cref="LossFactor"/>); to it are added radiation
/// (rho0 c sigma S / (omega M)) and whatever it is touching: the frame, the ground, the pile. Radiation
/// efficiency is interpolated from the small-body limit ((ka)^4) and coincidence (fc = c^2 / (2 pi
/// sqrt(D/m'')), 1965 Hz for 6 mm), scaled for a free piece (<see cref="FreeRadiation"/>). A piece with
/// more modes in the band than are rendered has the rest stand in log-spaced representatives, each
/// carrying the energy of the modes it stands for.
///
/// A CONTACT. Hertz: duration 2.87 (m^2 / (R E*^2 v))^(1/5), with R half a millimetre for a fracture edge,
/// so concrete gives tens of microseconds and grass milliseconds (its E is a few MPa). Each contact
/// radiates twice: the body's rigid-body acceleration (p = rho0 V_eff cos / (4 pi c r) d(a)/dt, V_eff
/// including the added mass of a plate moving face-on, doubled by a hard ground under it; over the whole
/// body's Hertz time and no quicker than 2a/c), and its modes, rung through the corner's contact (the
/// glass within one thickness of it) by a share of the impact energy (<see cref="RingShare"/>, fitted to
/// the recordings) filtered by the contact's spectrum |cos(pi f tau) / (1 - (2 f tau)^2)|.
///
/// FALL. Quadratic drag, closed form (terminal speed sqrt(2 m g / (rho0 Cd A))), from where each piece was
/// in the pane and when it left. The landing render starts at <see cref="GlassBreak.FallSeconds"/> of the
/// pane's bottom edge, so the first arrival still says the floor (GlassBreak's whole point).
///
/// What is estimated rather than sourced, and named where it is used: the share of an impact's energy that
/// goes into a piece's ringing, restitution and friction on each ground, the frame's damping, how many
/// radial cracks a round makes, the clump share of a tempered sheet, breakage on landing.
///
/// Warren and Verbrugge (1984, J. Exp. Psych. HPP 10(5)) heard breaking from bouncing by the onsets alone:
/// a bounce is one damped quasi-periodic train, a break an initial burst then many independent trains
/// with asynchronous onsets. That is what this produces, because every piece bounces on its own.
/// </summary>
public static class GlassFracture
{
    // ── The material ─────────────────────────────────────────────────────────────────────────────

    public const double YoungsPa = 72e9, Density = 2500, Poisson = 0.22;
    /// <summary>
    /// Glass's internal loss factor. Measured, not taken from a handbook (which gives 0.0006-0.002): the
    /// strongest line of each isolated drop decays with a total loss factor of 0.0006 in "Shards of glass
    /// dropped slowly onto cement" (median of 31, quartiles 0.0004-0.0008), 0.0009 in the el-bee tinkle
    /// texture (539), 0.0005 in the jar; that total is this, the piece's radiation (about 0.0004 for a
    /// 6 mm piece at 12 kHz) and its contact with the cement. AudioLab --glass, decay.py.
    /// </summary>
    public const double LossFactor = 0.0003;
    /// <summary>Terminal crack speed in soda-lime glass, m/s (Doll 1975).</summary>
    public const double CrackSpeed = 1500;
    public const double ToughnessPaRootM = 0.75e6;
    private const double Rho0 = 1.2, C0 = 343, G = 9.81;

    /// <summary>The plate's longitudinal speed, sqrt(E / (rho (1 - nu^2))), about 5500 m/s.</summary>
    private static readonly double PlateSpeed = Math.Sqrt(YoungsPa / (Density * (1 - Poisson * Poisson)));

    /// <summary>sqrt(D / m'') of a sheet <paramref name="h"/> thick, m^2/s.</summary>
    private static double BendRoot(double h) => h * PlateSpeed / Math.Sqrt(12);

    /// <summary>The coincidence frequency of a sheet: about 1965 Hz for 6 mm.</summary>
    public static double CoincidenceHz(double h) => C0 * C0 / (2 * Math.PI * BendRoot(h));

    // ── The keys ─────────────────────────────────────────────────────────────────────────────────

    public const string KeyPrefix = "glass:";
    public const int Variants = 4;

    /// <summary>Which of the three sounds a key names: the pane going (at the window), the pieces arriving
    /// (at the foot of the wall), or a round through a pane that stays up.</summary>
    public enum Part { Break, Land, Hole }

    /// <summary>Everything a render depends on. Sizes in metres, the round in kilograms and m/s at the pane;
    /// <see cref="Drop"/> is the pane's bottom edge above the ground it falls to; <see cref="Ground"/> a
    /// material name from the registry.</summary>
    public readonly record struct Spec(Part Part, GlassType Type, float Width, float Height, float Thickness,
                                       float BulletKg, float BulletSpeed, int Pellets, float Drop, string Ground,
                                       int Variant);

    /// <summary>A round's mass from its calibre and length, a jacketed lead slug filling 80 % of its
    /// cylinder (9 mm: 8.3 g, against the 8.0 of a 124 grain ball), and its speed at the pane.</summary>
    public static (float Kg, int Pellets) BulletOf(WeaponDefinition w)
    {
        float d = w.BulletDiameterMetres > 0f ? w.BulletDiameterMetres : 0.009f;
        float l = w.BulletLengthMetres > 0f ? w.BulletLengthMetres : 0.0156f;
        return (10500f * MathF.PI * d * d / 4f * l * 0.8f, Math.Max(1, w.PelletsPerShot));
    }

    /// <summary>The drop as a key holds it: to the decimetre. Both the server's delay and the render's
    /// start use this, so they agree.</summary>
    public static float QuantiseDrop(float drop) => MathF.Round(Math.Clamp(drop, 0f, 200f) * 10f) / 10f;

    /// <summary>When the landing render begins after the break, seconds: a tenth of a second before the bottom
    /// edge's free fall (<see cref="GlassBreak.FallSeconds"/>), because a piece thrown downwards gets there
    /// first. The server delays the landing by exactly this.</summary>
    public static float LandingStart(float drop) => MathF.Max(0f, GlassBreak.FallSeconds(QuantiseDrop(drop)) - 0.1f);

    public static string Key(Spec s)
    {
        string part = s.Part switch { Part.Break => "break", Part.Land => "land", _ => "hole" };
        string type = s.Type switch { GlassType.Tempered => "tempered", GlassType.Laminated => "laminated", _ => "annealed" };
        // The break needs the drop only to know when its pieces stop ringing in the air, so it is held
        // coarsely and not at all above 6 m, where they have died away before they land.
        float drop = s.Part == Part.Break ? MathF.Round(Math.Min(6f, s.Drop) * 2f) / 2f
                   : s.Part == Part.Land ? QuantiseDrop(s.Drop) : 0f;
        string ground = s.Part == Part.Land ? Sanitise(s.Ground) : "-";
        int v = ((s.Variant % Variants) + Variants) % Variants;
        return string.Create(CultureInfo.InvariantCulture,
            $"{KeyPrefix}{part}:{type}:{Cm(s.Width)}:{Cm(s.Height)}:{(int)MathF.Round(s.Thickness * 2000f)}:"
          + $"{(int)MathF.Round(s.BulletKg * 2000f)}:{(int)MathF.Round(s.BulletSpeed / 25f) * 25}:{Math.Max(1, s.Pellets)}:"
          + $"{(int)MathF.Round(drop * 10f)}:{ground}:{v}");
    }

    private static int Cm(float m) => (int)MathF.Round(Math.Clamp(m, 0.05f, 20f) * 20f) * 5;

    private static string Sanitise(string? material)
    {
        if (string.IsNullOrWhiteSpace(material)) return "Generic";
        var chars = material.ToCharArray();
        for (int i = 0; i < chars.Length; i++) if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
        return new string(chars);
    }

    public static bool TryParseKey(string? key, out Spec spec)
    {
        spec = default;
        if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
        var p = key.Substring(KeyPrefix.Length).Split(':');
        if (p.Length != 11) return false;
        Part part = p[0] switch { "break" => Part.Break, "land" => Part.Land, "hole" => Part.Hole, _ => (Part)(-1) };
        if ((int)part < 0) return false;
        GlassType type = p[1] switch { "tempered" => GlassType.Tempered, "laminated" => GlassType.Laminated, "annealed" => GlassType.Annealed, _ => (GlassType)(-1) };
        if ((int)type < 0) return false;
        var ci = CultureInfo.InvariantCulture;
        if (!int.TryParse(p[2], NumberStyles.Integer, ci, out int w) || !int.TryParse(p[3], NumberStyles.Integer, ci, out int h)
            || !int.TryParse(p[4], NumberStyles.Integer, ci, out int t) || !int.TryParse(p[5], NumberStyles.Integer, ci, out int g)
            || !int.TryParse(p[6], NumberStyles.Integer, ci, out int speed) || !int.TryParse(p[7], NumberStyles.Integer, ci, out int pellets)
            || !int.TryParse(p[8], NumberStyles.Integer, ci, out int drop) || !int.TryParse(p[10], NumberStyles.Integer, ci, out int variant))
            return false;
        if (w <= 0 || h <= 0 || t <= 0 || g <= 0 || speed <= 0 || pellets <= 0 || drop < 0) return false;
        spec = new Spec(part, type, w / 100f, h / 100f, t / 2000f, g / 2000f, speed, pellets, drop / 10f,
                        p[9] == "-" ? "Concrete" : p[9], ((variant % Variants) + Variants) % Variants);
        return true;
    }

    /// <summary>The sound a key names, peak one, and the level that peak is, dB SPL at a metre: a world
    /// sound's level is its buffer's full scale.</summary>
    public static float[] RenderKey(string key, int sampleRate, out float fullScaleDb)
    {
        fullScaleDb = 0f;
        if (!TryParseKey(key, out var spec)) return new float[16];
        var pa = Render(spec, sampleRate);
        double peak = 1e-12;
        foreach (double v in pa) peak = Math.Max(peak, Math.Abs(v));
        var pcm = new float[pa.Length];
        for (int i = 0; i < pa.Length; i++) pcm[i] = (float)(pa[i] / peak);
        fullScaleDb = (float)(20 * Math.Log10(peak / 2e-5));
        return pcm;
    }

    /// <summary>
    /// The level the server declares for a key, dB SPL peak at a metre: the median of the model's own
    /// peaks (AudioLab --glass survey), by part and type, moved by how much glass there is. The client
    /// places each render at its own peak once it has it (WorldAudioPlayer.AtOwnLevel); this is what the
    /// rest of the server (and a listener's ranking) has to go on until then.
    /// </summary>
    public static float DeclaredDb(Spec s)
    {
        double area = Math.Max(0.02, s.Width * s.Height);
        double sizeDb = 10 * Math.Log10(area / (1.2 * 1.6));
        // Survey of 2026-10-04 (a 1.2 x 1.6 m annealed window, 2 x 2.5 m and 2 x 3 m tempered panes, a car's
        // side window; Glock, AKM, shotgun; two variants; five grounds and two drops for the landing):
        // break medians 137 (annealed), 133-140 (tempered); hole 133; landing 141 (annealed), 136-138 (big
        // tempered), 124 (the car window).
        return s.Part switch
        {
            Part.Hole => s.Type == GlassType.Laminated ? 125f : 133f,
            Part.Break => s.Type == GlassType.Tempered ? 135f : 137f,
            _ => (float)((s.Type == GlassType.Tempered ? 134 : 141) + sizeDb),
        };
    }

    // ── The simulation ───────────────────────────────────────────────────────────────────────────

    private enum Kind { Shard, Sliver, Clump }

    private sealed class Frag
    {
        public Kind Kind;
        public double Lx, Ly, H, Area, Mass, X, Y, R;
        public double Release = -1;              // seconds after the strike it leaves the frame; < 0 stays
        public double V0Up, V0Side;              // m/s at release
        public double LandAt = -1, LandV, LandSide;
        public double EScale = 1, ExtraEta;
        public double[] Hz = Array.Empty<double>(), Count = Array.Empty<double>(), Gain = Array.Empty<double>(), EtaRad = Array.Empty<double>();
        public double ModesUpTo(double f) => Area > 0 ? Area * f / (2 * BendRoot(H) * Math.Sqrt(EScale)) : 0;
        /// <summary>What happens to its modes, in time order: an impulse of energy (J) or a change of what
        /// damps it (state: 0 frame, 1 air, 2 ground, 3 silenced).</summary>
        public readonly List<(double T, double Joules, double Tau, int State, double Eta)> Events = new();
        public bool OnGround;
        /// <summary>Made on the ground, by a piece breaking there: the landing render hears it from its birth.</summary>
        public bool Born;
    }

    /// <summary>One contact's click: when, its acceleration-noise scale (Pa s at a metre per unit-area pulse
    /// derivative), its duration.</summary>
    private readonly record struct Click(double T, double Amp, double Tau);

    private sealed class Sim
    {
        public readonly Spec S;
        public readonly Random Rng;
        public readonly List<Frag> Frags = new();
        public readonly List<Click> WindowClicks = new(), GroundClicks = new();
        public double M2;                    // m'' of the pane
        public double ImpactX, ImpactY;      // in the pane, from its bottom-left
        public double J, TauStrike;          // the round's impulse to the pane and its transit
        public double CutAt;                 // when the pane is pieces
        public double PaneVibJ;              // its vibration energy then
        public double DieSize, DieKick;
        public int Dice, DiceLoose;
        public double T0;                    // the landing render's zero: FallSeconds of the bottom edge
        public MaterialProperties Ground = new();
        public double GroundEStar, GlassEStar, Restitution, Friction, AsperityM, GroundEta, GroundImage;
        public double PileArea, PileCover;
        public readonly List<int> Landed = new();
        /// <summary>Where clicks go as they are made, when the render's buffer already exists (the landing,
        /// whose dice would otherwise be millions of stored clicks).</summary>
        public double[]? Out;
        public double OutT0;
        public int Rate;
        /// <summary>Contacts made (clicks emitted), and the span of their times, for the lab.</summary>
        public long Emitted;
        public double FirstT = double.MaxValue, LastT;

        public void Emit(List<Click> list, Click c)
        {
            Emitted++;
            if (Counted != null) lock (Counted) Counted.Add(c.T);
            if (c.T < FirstT) FirstT = c.T;
            if (c.T > LastT) LastT = c.T;
            if (Out != null) { if (Want("clicks")) RenderClick(c, Out, Rate, OutT0); }
            else list.Add(c);
        }

        /// <summary>How much one piece landing here counts towards the pile: more than one when this is one of
        /// several workers landing a share of the dice each.</summary>
        public double PileGrowth = 1;
        /// <summary>Lab: every contact's time, when counting (see Contacts); shared by the workers.</summary>
        public List<double>? Counted;
        /// <summary>Clumps that have landed and come apart: when, how many dice, how fast it arrived. Their dice
        /// are scattered with the loose ones, on the workers.</summary>
        public readonly List<(double T, int Dice, double Speed)> Bursts = new();

        /// <summary>A worker's copy for landing a share of the dice: the same ground and pile, its own random
        /// sequence and its own buffer.</summary>
        public Sim(Sim parent, int worker, int workers, double[] buffer) : this(parent.S)
        {
            Rng = new Random(Seed(parent.S) ^ ((worker + 1) * 7919));
            Ground = parent.Ground; GroundEStar = parent.GroundEStar; GlassEStar = parent.GlassEStar;
            Restitution = parent.Restitution; Friction = parent.Friction; AsperityM = parent.AsperityM;
            GroundEta = parent.GroundEta; GroundImage = parent.GroundImage; PileArea = parent.PileArea;
            PileCover = parent.PileCover; PileGrowth = workers;
            DieSize = parent.DieSize; DieKick = parent.DieKick; T0 = parent.T0;
            Out = buffer; OutT0 = parent.OutT0; Rate = parent.Rate; Counted = parent.Counted;
        }

        public Sim(Spec s)
        {
            S = s;
            Rng = new Random(Seed(s));
            M2 = Density * s.Thickness;
            J = s.BulletKg * Math.Max(1, s.Pellets) * 0.1 * s.BulletSpeed;
            TauStrike = 2 * s.Thickness / Math.Max(50, s.BulletSpeed);
        }
    }

    private static int Seed(Spec s)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (int)s.Type;
            h = h * 31 + (int)MathF.Round(s.Width * 100);
            h = h * 31 + (int)MathF.Round(s.Height * 100);
            h = h * 31 + (int)MathF.Round(s.Thickness * 10000);
            h = h * 31 + (int)MathF.Round(s.BulletKg * 10000);
            h = h * 31 + (int)MathF.Round(s.BulletSpeed);
            h = h * 31 + s.Pellets;
            h = h * 31 + s.Variant;
            return h & 0x7fffffff;
        }
    }

    /// <summary>A render in pascals at a metre.</summary>
    public static double[] Render(Spec s, int rate)
    {
        var sim = new Sim(s);
        Shatter(sim);
        return s.Part switch
        {
            Part.Hole => RenderHole(sim, rate),
            Part.Break => RenderBreak(sim, rate),
            _ => RenderLand(sim, rate),
        };
    }

    /// <summary>Lab: how many contacts a render makes (every click: a strike, a slip, a knock, a landing, a
    /// bounce, a tap of a slide), the busiest 100 ms of them per second, and the span they fall over.</summary>
    public static (long Contacts, double BusiestPerSecond, double First, double Last) Contacts(Spec s)
    {
        var sim = new Sim(s);
        Shatter(sim);
        var times = new List<double>();
        if (s.Part == Part.Land)
        {
            // The landing's contacts are made while it renders: count them on a run with nowhere to render.
            var counting = new Sim(s);
            Shatter(counting);
            counting.Out = new double[16]; counting.OutT0 = 1e9; counting.Rate = 48000;
            counting.Counted = times;
            LandingContacts(counting, new List<Frag>());
            sim = counting;
        }
        else foreach (var c in sim.WindowClicks) times.Add(c.T);
        if (times.Count == 0) return (0, 0, 0, 0);
        times.Sort();
        int best = 0;
        for (int i = 0, j = 0; i < times.Count; i++)
        {
            while (times[i] - times[j] > 0.1) j++;
            best = Math.Max(best, i - j + 1);
        }
        return (times.Count, best * 10.0, times[0] - (s.Part == Part.Land ? sim.T0 : 0), times[^1] - (s.Part == Part.Land ? sim.T0 : 0));
    }

    /// <summary>How many pieces a break makes, by kind, and how many fall: for tests and the lab.</summary>
    public static (int Shards, int Slivers, int Clumps, int Dice, int Falling) Census(Spec s)
    {
        var sim = new Sim(s);
        Shatter(sim);
        int falling = 0;
        foreach (var f in sim.Frags) if (f.Release >= 0) falling++;
        return (sim.Frags.FindAll(f => f.Kind == Kind.Shard && f.Lx >= 0.004).Count, sim.Frags.FindAll(f => f.Kind == Kind.Sliver).Count,
                sim.Frags.FindAll(f => f.Kind == Kind.Clump).Count, sim.Dice, falling + sim.DiceLoose);
    }

    private static double U(Random r, double a, double b) => a + (b - a) * r.NextDouble();

    /// <summary>The whole event, decided: the pattern, who leaves when, every contact at the window and on
    /// the ground. Deterministic from the spec, so the break and the landing renders agree about the pieces.</summary>
    private static void Shatter(Sim sim)
    {
        var s = sim.S;
        var r = sim.Rng;
        double w = s.Width, h = s.Height;
        // Where the round hit: near the middle, a little different every variant.
        sim.ImpactX = w * U(r, 0.35, 0.65);
        sim.ImpactY = h * U(r, 0.35, 0.65);
        sim.T0 = LandingStart(s.Drop);

        sim.Ground = AcousticRegistry.GetProperties(s.Ground ?? "Concrete");
        double eGround = Math.Max(1e-3, sim.Ground.YoungsModulusGPa) * 1e9;
        sim.GroundEStar = 1 / ((1 - Poisson * Poisson) / YoungsPa + (1 - 0.2 * 0.2) / eGround);
        sim.GlassEStar = YoungsPa / (2 * (1 - Poisson * Poisson));
        double groundLoss = Math.Clamp(sim.Ground.LossFactor, 0.0, 1.0);
        // Estimates: an irregular plate keeps less of its bounce than a ball (rotation takes some), a soft
        // lossy ground keeps less still; friction of glass on stone about 0.6.
        sim.Restitution = 0.45 * (1 - Math.Min(0.9, groundLoss));
        sim.Friction = 0.6 + 0.2 * Math.Min(1, groundLoss * 3);
        sim.AsperityM = groundLoss > 0.3 ? 0 : 0.002 + 0.02 * groundLoss;   // grass and carpet hold a piece still
        sim.GroundEta = GroundContactLoss(sim.Ground);
        // A hard ground is a mirror under the contact: the image doubles the pressure.
        sim.GroundImage = 1 + (1 - Math.Clamp(sim.Ground.AbsorptionHigh, 0, 1));
        sim.PileArea = (w + 1.0) * (1.0 + 0.15 * s.Drop);

        if (s.Part == Part.Hole) { PaneModesEnergy(sim, cut: false); return; }

        PaneModesEnergy(sim, cut: true);
        if (s.Type == GlassType.Tempered) Dice(sim); else Crack(sim);
        Fall(sim);
    }

    // ── The pane ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pane's vibration energy from the strike, and when the cracks cut it.</summary>
    private static void PaneModesEnergy(Sim sim, bool cut)
    {
        var s = sim.S;
        double a = s.Width, b = s.Height, M = sim.M2 * a * b, br = BendRoot(s.Thickness);
        double fExc = Math.Min(80000, 1.5 / sim.TauStrike), e = 0;
        for (int m = 1; ; m++)
        {
            double fm = Math.PI / 2 * br * Sq((m + 0.35) / a);
            if (fm > fExc) break;
            double sx = Math.Sin(m * Math.PI * sim.ImpactX / a);
            for (int n = 1; ; n++)
            {
                double f = Math.PI / 2 * br * (Sq((m + 0.35) / a) + Sq((n + 0.35) / b));
                if (f > fExc) break;
                double phi = 2 * sx * Math.Sin(n * Math.PI * sim.ImpactY / b);
                double v = sim.J * phi * Contact(f, sim.TauStrike) / M;
                e += 0.5 * M * v * v;
            }
        }
        sim.PaneVibJ = e;
        // Radial cracks or the dicing front reach the frame: the farther corner, at the crack speed.
        double far = 0;
        foreach (var (cx, cy) in new[] { (0.0, 0.0), (a, 0.0), (0.0, b), (a, b) })
            far = Math.Max(far, Math.Sqrt(Sq(cx - sim.ImpactX) + Sq(cy - sim.ImpactY)));
        sim.CutAt = cut ? far / CrackSpeed : double.PositiveInfinity;
    }

    private static double Sq(double x) => x * x;

    /// <summary>What lying on the ground adds to a piece's loss. On something hard a piece touches at a few
    /// points and keeps ringing: the recording's pieces on cement ring as if free (see <see cref="LossFactor"/>),
    /// so 0.0002. In grass or carpet it lies in the pile, which takes a share of the pile's own loss
    /// (estimate: three tenths).</summary>
    private static double GroundContactLoss(MaterialProperties ground)
        => ground.YoungsModulusGPa >= 1 ? 0.0002 : 0.3 * Math.Clamp(ground.LossFactor, 0, 1);

    /// <summary>The same for a piece of a given length: a big piece lies on its face, not on three points, and
    /// pumps the air under it (estimate: rising to 0.02 at 30 cm).</summary>
    private static double LyingLoss(double groundLoss, double length)
        => groundLoss + 0.02 * Math.Clamp((length - 0.05) / 0.25, 0, 1);

    /// <summary>
    /// The share of a contact's kinetic energy that goes into the bodies' ringing. Fitted, not sourced: a
    /// glass piece dropped on cement rings with 9 dB more energy over 3-60 ms than its click carries in the
    /// first 2 ms (median over the drops in "Shards of glass dropped slowly onto cement", 9.3 dB of 32; the
    /// el-bee tinkle texture, 9.5 of 587; the jar, 8.4), above 2 kHz; single drops through this model match
    /// that at this share (AudioLab --glass ringfit). OPENFPS_GLASS_RING_SHARE overrides it in the lab.
    /// </summary>
    private static readonly double RingShare =
        double.TryParse(Environment.GetEnvironmentVariable("OPENFPS_GLASS_RING_SHARE"), NumberStyles.Float, CultureInfo.InvariantCulture, out double share)
            ? share : 0.3;

    /// <summary>
    /// Lab: one piece of glass <paramref name="lx"/> by <paramref name="ly"/> metres dropped flat-ish from
    /// <paramref name="height"/> onto <paramref name="ground"/>, pascals at a metre: its arrival, rocking,
    /// bounces and slide, as the landing render does them, from t = 0 at the first contact.
    /// </summary>
    public static double[] RenderDrop(double lx, double ly, double thickness, double height, string ground, int rate, int seed)
    {
        var sim = new Sim(new Spec(Part.Land, GlassType.Annealed, 1f, 1f, (float)thickness, 0.008f, 300f, 1, (float)height, ground, seed));
        var r = sim.Rng;
        sim.Ground = AcousticRegistry.GetProperties(ground);
        double eGround = Math.Max(1e-3, sim.Ground.YoungsModulusGPa) * 1e9;
        sim.GroundEStar = 1 / ((1 - Poisson * Poisson) / YoungsPa + (1 - 0.2 * 0.2) / eGround);
        sim.GlassEStar = YoungsPa / (2 * (1 - Poisson * Poisson));
        double loss = Math.Clamp(sim.Ground.LossFactor, 0.0, 1.0);
        sim.Restitution = 0.45 * (1 - Math.Min(0.9, loss));
        sim.Friction = 0.6 + 0.2 * Math.Min(1, loss * 3);
        sim.AsperityM = loss > 0.3 ? 0 : 0.002 + 0.02 * loss;
        sim.GroundEta = GroundContactLoss(sim.Ground);
        sim.GroundImage = 1 + (1 - Math.Clamp(sim.Ground.AbsorptionHigh, 0, 1));
        sim.PileArea = 1e9;
        double ignore = 0;
        var f = AddFrag(sim, lx / ly >= 4 ? Kind.Sliver : Kind.Shard, lx, ly, 0.5, 0.5, 1, ref ignore);
        f.Release = 0;
        f.Events.Add((0, 0, 0, 1, 0));
        double v = Math.Sqrt(2 * G * height);
        var y = new double[(int)(1.0 * rate)];
        sim.Out = y; sim.OutT0 = -0.01; sim.Rate = rate;
        var pieces = new List<Frag>();
        ArriveAndSettle(sim, f, 0, f.Mass, VolumeOf(f, U(r, 0, 1)), v, U(r, 0, 0.3), 0.0005, f.H, pieces);
        AllModes(sim.Frags.FindAll(p => p.OnGround), y, rate, -0.01, landing: true);
        return y;
    }

    /// <summary>The spectrum of a half-sine force of duration tau, relative to its impulse.</summary>
    private static double Contact(double f, double tau)
    {
        double x = 2 * f * tau;
        if (Math.Abs(1 - x * x) < 1e-6) return Math.PI / 4;
        return Math.Abs(Math.Cos(Math.PI * f * tau) / (1 - x * x));
    }

    // ── Annealed: radial and concentric cracks ───────────────────────────────────────────────────

    private static void Crack(Sim sim)
    {
        var s = sim.S;
        var r = sim.Rng;
        double w = s.Width, hgt = s.Height, h = s.Thickness;
        double deposited = 0.5 * s.BulletKg * Math.Max(1, s.Pellets) * Sq(s.BulletSpeed) * (1 - 0.81);
        // Estimate: more radial cracks for more energy (no measured count was found); four to sixteen.
        int radial = (int)Math.Clamp(Math.Round(4 + 3 * Math.Log2(Math.Max(1, deposited / 50)) + U(r, -1, 1)), 4, 16);
        if (s.Pellets > 1) radial = Math.Min(20, radial + 2 * (int)Math.Sqrt(s.Pellets));
        var angles = new List<double>();
        double start = U(r, 0, 2 * Math.PI);
        for (int i = 0; i < radial; i++) angles.Add(start + 2 * Math.PI * (i + U(r, -0.3, 0.3)) / radial);
        angles.Sort();
        // Concentric cracks near the hole, where the bending was; beyond, the radial cracks run to the frame
        // and branch, so a sector wider than a hand splits in two.
        var rings = new List<double> { 0.012 + 0.02 * Math.Sqrt(deposited / 100) };
        for (double rr = rings[0] * U(r, 2.2, 3); rr < 0.25 * Math.Sqrt(w * hgt); rr *= U(r, 1.6, 2.2)) rings.Add(rr);
        double totalWeight = 0;
        for (int i = 0; i < angles.Count; i++)
        {
            double a0 = angles[i], a1 = i + 1 < angles.Count ? angles[i + 1] : angles[0] + 2 * Math.PI;
            Sector(sim, a0, a1, rings, 1, rings[0], ref totalWeight);
        }
        // The cone: the crushed glass under the hole goes as fines.
        int fines = (int)(200 * Math.Sqrt(deposited / 100) * Math.Max(1, Math.Sqrt(s.Pellets)));
        for (int i = 0; i < fines; i++)
        {
            double size = 0.0006 * Math.Pow(U(r, 1, 6), 1.0);
            AddFrag(sim, Kind.Shard, size, size, sim.ImpactX, sim.ImpactY, 0, ref totalWeight, fine: true);
        }
        // The pane's vibration when the cracks cut it goes to the pieces, more to those near the hole.
        foreach (var f in sim.Frags)
            f.Events.Add((sim.CutAt * U(r, 0.3, 1.0), sim.PaneVibJ * f.Area / (f.R + 0.05) / totalWeight, sim.TauStrike, 0, 0));

        // Who leaves and when. Pieces at the hole go at once, pushed by the round; the rest of a cracked pane
        // works loose over a few hundred milliseconds, the upper pieces first, and some of the pieces along
        // the frame stay in it (estimate: a third of those with a long edge in the bead).
        foreach (var f in sim.Frags)
        {
            bool edge = f.X - f.Lx / 2 < 0.03 || f.X + f.Lx / 2 > w - 0.03 || f.Y - f.Ly / 2 < 0.03 || f.Y + f.Ly / 2 > hgt - 0.03;
            if (edge && f.Lx > 0.06 && r.NextDouble() < 0.33) { f.Release = -1; continue; }
            if (f.Lx < 0.004)
            {
                f.Release = U(r, 0, 0.003);
                f.V0Side = U(r, 4, 25);
                f.V0Up = U(r, -3, 4);
                continue;
            }
            if (f.R < 0.08) { f.Release = U(r, 0.002, 0.03); f.V0Side = U(r, 0.5, 3); f.V0Up = U(r, -0.5, 0.5); }
            else
            {
                double up = f.Y / Math.Max(0.1, hgt);
                f.Release = 0.02 + U(r, 0, 1) * (0.12 + 0.3 * (1 - up)) + U(r, 0, 0.05);
                f.V0Side = U(r, 0.05, 0.6);
                f.V0Up = 0;
            }
        }
        if (sim.S.Part == Part.Break) WindowContacts(sim);
    }

    /// <summary>One sector between two radial cracks, from a ring outwards; splits when it gets wide.</summary>
    private static void Sector(Sim sim, double a0, double a1, List<double> rings, int ring, double rIn, ref double weight)
    {
        var r = sim.Rng;
        double mid = 0.5 * (a0 + a1);
        double edge = EdgeDistance(sim, mid);
        if (rIn >= edge) return;
        double rOut = ring < rings.Count ? Math.Min(edge, rings[ring]) : edge;
        // Beyond the rings a radial crack runs on, cut now and then by a transverse one (branching and the
        // reflected stress wave): every 10 to 40 cm.
        if (ring >= rings.Count) rOut = Math.Min(edge, rIn + U(r, 0.10, 0.40));
        double arc = (a1 - a0) * 0.5 * (rIn + rOut);
        if (arc > 0.16 && ring > 1 && a1 - a0 > 0.08)
        {
            double split = a0 + (a1 - a0) * U(r, 0.35, 0.65);
            Sector(sim, a0, split, rings, ring, rIn, ref weight);
            Sector(sim, split, a1, rings, ring, rIn, ref weight);
            return;
        }
        double radialLen = rOut - rIn;
        double area = 0.5 * (a1 - a0) * (rOut * rOut - rIn * rIn) * 0.85;   // the rectangle clips some
        double width = Math.Max(0.003, area / Math.Max(0.003, radialLen));
        double rc = 0.5 * (rIn + rOut);
        double x = sim.ImpactX + rc * Math.Cos(mid), y = sim.ImpactY + rc * Math.Sin(mid);
        double lx = Math.Max(radialLen, width), ly = Math.Min(radialLen, width);
        AddFrag(sim, lx / ly >= 4 ? Kind.Sliver : Kind.Shard, lx, ly, x, y, rc, ref weight);
        // Slivers flake off along the crack faces: one or two per piece, a few mm wide.
        int slivers = r.NextDouble() < 0.6 ? 1 + (r.NextDouble() < 0.3 ? 1 : 0) : 0;
        for (int k = 0; k < slivers; k++)
            AddFrag(sim, Kind.Sliver, Math.Min(radialLen, U(r, 0.03, 0.15)), U(r, 0.003, 0.008), x, y, rc, ref weight);
        if (rOut < edge - 1e-3) Sector(sim, a0, a1, rings, ring + 1, rOut, ref weight);
    }

    /// <summary>How far from the hole the frame is along a direction.</summary>
    private static double EdgeDistance(Sim sim, double angle)
    {
        double dx = Math.Cos(angle), dy = Math.Sin(angle), best = double.MaxValue;
        if (dx > 1e-9) best = Math.Min(best, (sim.S.Width - sim.ImpactX) / dx);
        if (dx < -1e-9) best = Math.Min(best, -sim.ImpactX / dx);
        if (dy > 1e-9) best = Math.Min(best, (sim.S.Height - sim.ImpactY) / dy);
        if (dy < -1e-9) best = Math.Min(best, -sim.ImpactY / dy);
        return best;
    }

    private static Frag AddFrag(Sim sim, Kind kind, double lx, double ly, double x, double y, double rDist, ref double weight,
                                bool fine = false, double eScale = 1, double extraEta = 0)
    {
        double h = Math.Min(sim.S.Thickness, Math.Max(lx, ly));
        var f = new Frag
        {
            Kind = kind, Lx = Math.Max(lx, ly), Ly = Math.Min(lx, ly), H = h,
            X = Math.Clamp(x, 0, sim.S.Width), Y = Math.Clamp(y, 0, sim.S.Height), R = rDist,
            EScale = eScale, ExtraEta = extraEta,
        };
        f.Area = f.Lx * f.Ly * (kind == Kind.Shard ? 0.75 : 1.0);
        f.Mass = Density * f.Area * h;
        if (!fine) BuildModes(f);
        sim.Frags.Add(f);
        weight += f.Area / (f.R + 0.05);
        return f;
    }

    // ── A fragment's modes ───────────────────────────────────────────────────────────────────────

    private static readonly double[] FreeSquare = { 13.47, 19.60, 24.27, 34.80, 34.80, 61.09, 61.09, 63.69, 69.27 };
    private static readonly double[] FreeBeam = { 4.730, 7.853, 10.996, 14.137 };
    private const double TopHz = 20000;

    /// <summary>
    /// How well a small free piece radiates, against a baffled plate above coincidence. A piece's radiation
    /// is also a loss, and the measured decay on cement (total 0.0006, see <see cref="LossFactor"/>) leaves room
    /// for no more than about a third of the baffled figure (which alone would be 0.0007-0.0015 for 3-6 mm
    /// pieces at 12 kHz).
    /// </summary>
    private const double FreeRadiation = 0.3;

    /// <summary>What a cracked piece still in the frame loses to the bead and to rubbing its neighbours
    /// (estimate).</summary>
    private const double InFrameLoss = 0.005;
    private const int Representatives = 6;

    private static void BuildModes(Frag f)
    {
        var hz = new List<double>();
        var count = new List<double>();
        double br = BendRoot(f.H) * Math.Sqrt(f.EScale);
        var r = new Random((int)(f.Area * 1e7 + f.X * 1e4 + f.Y * 1e3) & 0x7fffffff);
        if (f.Lx / f.Ly >= 4)
        {
            // A free-free bar, bending through its thickness and across its width.
            double c = Math.Sqrt(YoungsPa * f.EScale / Density) / Math.Sqrt(12);
            foreach (double depth in new[] { f.H, f.Ly })
                for (int k = 1; k <= 30; k++)
                {
                    double bl = k <= 4 ? FreeBeam[k - 1] : (2 * k + 1) * Math.PI / 2;
                    double fk = bl * bl / (2 * Math.PI * f.Lx * f.Lx) * depth * c * U(r, 0.96, 1.04);
                    if (fk > TopHz) break;
                    hz.Add(fk); count.Add(1);
                }
        }
        else
        {
            double a2 = f.Area;
            double aspect = Math.Sqrt(f.Lx / f.Ly);
            for (int k = 0; k < FreeSquare.Length; k++)
            {
                // An irregular piece: each mode moved a little, pairs split by the aspect.
                double split = k % 2 == 0 ? aspect : 1 / aspect;
                double fk = FreeSquare[k] / (2 * Math.PI * a2) * br * Math.Pow(split, 0.3) * U(r, 0.9, 1.1);
                if (fk <= TopHz) { hz.Add(fk); count.Add(1); }
            }
            // Above the tabulated ones, the plate's modal density stands in, in log-spaced bands.
            double n9 = 9, nTop = f.ModesUpTo(TopHz);
            if (nTop > n9 + 1)
            {
                double f9 = 2 * n9 * br / a2, ratio = Math.Pow(TopHz / f9, 1.0 / Representatives);
                for (int k = 0; k < Representatives; k++)
                {
                    double lo = f9 * Math.Pow(ratio, k), hi = lo * ratio;
                    double n = a2 * (hi - lo) / (2 * br);
                    if (n < 0.5) continue;
                    hz.Add(Math.Sqrt(lo * hi) * U(r, 0.93, 1.07)); count.Add(n);
                }
            }
        }
        hz.Sort();
        int m = hz.Count;
        f.Hz = hz.ToArray(); f.Count = count.ToArray();
        f.Gain = new double[m]; f.EtaRad = new double[m];
        double fc = CoincidenceHz(f.H) / Math.Sqrt(f.EScale);
        double sRad = 2 * f.Area, aEq = Math.Sqrt(f.Area / Math.PI);
        for (int k = 0; k < m; k++)
        {
            double ka = 2 * Math.PI * f.Hz[k] * aEq / C0;
            // A free piece, unbaffled, cancels round its own edges: the interpolation is scaled by
            // FreeRadiation, which the measured decay bounds (see the constant).
            double sigma = FreeRadiation * Math.Pow(ka, 4) / (1 + Math.Pow(ka, 4)) / (1 + Sq(fc / f.Hz[k]));
            // Peak pressure at a metre per unit modal velocity: rho0 c sqrt(sigma S / 4 pi).
            f.Gain[k] = Rho0 * C0 * Math.Sqrt(sigma * sRad / (4 * Math.PI));
            f.EtaRad[k] = Rho0 * C0 * sigma * sRad / (2 * Math.PI * f.Hz[k] * f.Mass);
        }
    }

    // ── Tempered: dicing ─────────────────────────────────────────────────────────────────────────

    private static void Dice(Sim sim)
    {
        var s = sim.S;
        var r = sim.Rng;
        double w = s.Width, hgt = s.Height, h = s.Thickness;
        // EN 12150: at least 40 in 50 x 50 mm for 4-12 mm; twice that is taken, coarser for thicker glass.
        sim.DieSize = 0.0056 * Math.Sqrt(h / 0.006);
        double perM2 = 1 / Sq(sim.DieSize);
        // The stored energy and what the new faces cost; a third of the rest throws the dice.
        double sigmaT = 50e6;
        double u = (1 - Poisson) * 0.8 * sigmaT * sigmaT / YoungsPa;   // J/m3
        double perArea = u * h, crack = Sq(ToughnessPaRootM) / YoungsPa * 2 * h / sim.DieSize;
        double kinetic = Math.Max(0, perArea - crack) / 3;
        sim.DieKick = Math.Sqrt(2 * kinetic / (Density * h));

        // Estimates: two fifths of the area hangs together in clumps for a moment, a tenth stays in the frame,
        // the rest falls as loose dice.
        double totalWeight = 0;
        double clumpArea = 0.4 * w * hgt;
        double clumpMedian = 0.06 * Math.Sqrt(h / 0.006);
        while (clumpArea > 0)
        {
            double side = Math.Clamp(clumpMedian * Math.Exp(0.5 * Normal(r)), 2 * sim.DieSize, 0.3);
            double ly = side * U(r, 0.6, 1);
            var f = AddFrag(sim, Kind.Clump, side, ly, U(r, 0, w), U(r, 0, hgt), 0, ref totalWeight,
                            eScale: 0.05, extraEta: 0.08);
            f.R = Math.Sqrt(Sq(f.X - sim.ImpactX) + Sq(f.Y - sim.ImpactY));
            // The sheet sags and lets go over a few hundred milliseconds, most of it early, the last of it
            // trailing off rather than stopping on a step.
            f.Release = 0.03 + Math.Min(0.9, 0.12 * -Math.Log(1 - r.NextDouble())) + 0.15 * (1 - f.Y / hgt) * r.NextDouble();
            f.V0Side = U(r, 0.1, 0.8);
            f.V0Up = U(r, -0.3, 0.3);
            clumpArea -= f.Area;
        }
        sim.Dice = (int)(w * hgt * perM2);
        sim.DiceLoose = (int)(0.5 * sim.Dice);
        // The clumps get the pane's own vibration by area. The stored stress goes into the cracks and the dice's
        // flight; what a clump would ring with is scattered by the cracks that run through it.
        foreach (var f in sim.Frags)
            f.Events.Add((f.R / CrackSpeed, sim.PaneVibJ * f.Area / (w * hgt), sim.DieSize / CrackSpeed, 0, 0));
        if (sim.S.Part == Part.Break) WindowContacts(sim);
    }

    private static double Normal(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    // ── Contacts ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Hertz contact duration: 2.87 (m^2 / (R E*^2 v))^(1/5).</summary>
    private static double Hertz(double mass, double radius, double eStar, double v)
        => 2.87 * Math.Pow(mass * mass / (radius * eStar * eStar * Math.Max(0.01, v)), 0.2);

    /// <summary>The mass that takes a corner's blow: the piece, or the glass within one thickness of the corner
    /// if that is less. A cube of twice the thickness gave contacts of 100 microseconds, which filtered away the
    /// rings the recordings' pieces have (`--glass ringfit`).</summary>
    private static double CornerMass(double mass, double h) => Math.Min(mass, Density * Math.Pow(h, 3));

    /// <summary>
    /// A contact: its click (rigid-body acceleration noise) into <paramref name="clicks"/>, and its ringing as
    /// an impulse into each body it touches. <paramref name="volume"/> is the moving body's volume with its
    /// added mass of air. Estimate: a tenth of the contact's energy goes into the bodies' ringing.
    /// </summary>
    private static void Hit(Sim sim, List<Click> clicks, double t, double mass, double volume, double dv, double eStar,
                            double radius, double restitution, double image, Frag? a, Frag? b, double vibShare = -1,
                            double hEdge = 0, double sizeMass = 0)
    {
        if (dv <= 0) return;
        if (vibShare < 0) vibShare = RingShare;
        double thickness = hEdge > 0 ? hEdge : sim.S.Thickness;
        double m = CornerMass(mass, thickness);
        // The corner's contact, which is what rings the piece's modes...
        double tau = Hertz(m, radius, eStar, dv);
        // ...and the whole body's change of speed, which is what the air sees as a compact dipole: no quicker
        // than the whole mass can be stopped (Hertz on all of it), and no quicker than sound crosses the body
        // (2a/c), because above ka of about one a body radiates by its vibration, which is the modes.
        double extent = Math.Sqrt((sizeMass > 0 ? sizeMass : mass) / (Density * Math.Max(1e-4, thickness)) / Math.PI);
        double tauBody = Math.Max(Hertz(mass, radius, eStar, dv), 2 * extent / C0);
        // Pressure: rho0 V_eff cos(theta) / (4 pi c) times the derivative of the acceleration, whose integral
        // is the change of velocity. cos(theta) averaged over directions, with a random sign.
        double amp = Rho0 * volume * dv * (1 + restitution) * 0.6 * image / (4 * Math.PI * C0);
        if (sim.Rng.NextDouble() < 0.5) amp = -amp;
        sim.Emit(clicks, new Click(t, amp, tauBody));
        double joules = vibShare * 0.5 * (b == null ? mass : mass / 2) * dv * dv;
        if (a != null && a.Hz.Length > 0) a.Events.Add((t, b != null && b.Hz.Length > 0 ? joules / 2 : joules, tau, -1, 0));
        if (b != null && b.Hz.Length > 0) b.Events.Add((t, a != null && a.Hz.Length > 0 ? joules / 2 : joules, tau, -1, 0));
    }

    /// <summary>A plate's volume with its added mass when it moves face-on: a disc's (8/3) a^3, by how
    /// face-on it is.</summary>
    private static double VolumeOf(Frag f, double faceOn)
    {
        double a = Math.Sqrt(f.Area / Math.PI);
        return f.Area * f.H + 8.0 / 3 * a * a * a * faceOn * faceOn;
    }

    /// <summary>At the window: pieces grinding out of the cracked pane, knocking each other in the air, the
    /// low ones hitting the sill. Tempered: every die kicked by the stress, knocking its neighbours.</summary>
    private static void WindowContacts(Sim sim)
    {
        var r = sim.Rng;
        var clicks = sim.WindowClicks;
        double h = sim.S.Thickness;
        var falling = sim.Frags.FindAll(f => f.Release >= 0 && f.Lx >= 0.004);
        falling.Sort((p, q) => p.Release.CompareTo(q.Release));
        foreach (var f in sim.Frags)
        {
            if (f.Release < 0) continue;
            f.Events.Add((f.Release, 0, 0, 1, 0));
            if (f.Hz.Length == 0 && f.Lx < 0.004) continue;    // fines leave clean
            // Grinding along the crack faces as it works loose: a few slips.
            int slips = 2 + r.Next(4);
            for (int k = 0; k < slips; k++)
                Hit(sim, clicks, Math.Max(0, f.Release - U(r, 0, 0.02)), f.Mass, VolumeOf(f, 0.2), U(r, 0.03, 0.25), sim.GlassEStar, 0.0005,
                    0.3, 1, f, null, -1, f.H);
            // A knock or two against other falling pieces, which fall together and meet slowly.
            int knocks = PoissonCount(r, 0.5);
            for (int k = 0; k < knocks && falling.Count > 1; k++)
            {
                var other = falling[r.Next(falling.Count)];
                if (other == f) continue;
                double t = Math.Max(f.Release, other.Release) + U(r, 0.005, 0.25);
                Hit(sim, clicks, t, Math.Min(f.Mass, other.Mass), VolumeOf(f.Mass < other.Mass ? f : other, 0.5), U(r, 0.1, 0.5),
                    sim.GlassEStar, 0.0005, 0.5, 1, f, other, -1, h);
            }
            // Some of the lowest pieces come down on the sill on their way (estimate: three in ten of the bottom
            // quarter), from where they were.
            // A long dagger stands on the bead and topples rather than dropping on the sill.
            if (f.Y < 0.25 * sim.S.Height && f.Lx < 0.12 && r.NextDouble() < 0.3)
                Hit(sim, clicks, f.Release + Math.Sqrt(2 * Math.Max(0.02, f.Y) / G), f.Mass, VolumeOf(f, U(r, 0, 1)),
                    Math.Sqrt(2 * G * Math.Max(0.02, f.Y)), 2.2e10, 0.0005, 0.3, 1, f, null, -1, f.H);
        }

        if (sim.S.Type != GlassType.Tempered) return;
        // Every die: kicked as the front passes (its click), then knocking its neighbours in the sheet; the
        // loose ones grind out as the sheet sags.
        double dieVol = Math.Pow(sim.DieSize, 2) * h, dieMass = Density * dieVol;
        // A die is kicked as fast as the front crosses it, but radiates as a compact body no quicker than
        // sound crosses it.
        double dieBody = 2 * sim.DieSize / Math.Sqrt(Math.PI) / C0;
        double tauKick = Math.Max(sim.DieSize / CrackSpeed, dieBody);
        for (int i = 0; i < sim.Dice; i++)
        {
            double x = r.NextDouble() * sim.S.Width, y = r.NextDouble() * sim.S.Height;
            double t = Math.Sqrt(Sq(x - sim.ImpactX) + Sq(y - sim.ImpactY)) / CrackSpeed;
            double kick = sim.DieKick * U(r, 0.5, 1.2);
            double amp = Rho0 * 2 * dieVol * kick * U(r, -1, 1) / (4 * Math.PI * C0);
            sim.Emit(clicks, new Click(t, amp, tauKick));
            for (int k = 0; k < 2; k++)
            {
                double dv = kick * U(r, 0.2, 0.6);
                double tau = Math.Max(dieBody, Hertz(dieMass / 2, 0.0005, sim.GlassEStar, dv));
                sim.Emit(clicks, new Click(t + U(r, 0.0001, 0.004), Rho0 * 2 * dieVol * dv * 1.5 * 0.6 * Sign(r) / (4 * Math.PI * C0), tau));
            }
            if (i < sim.DiceLoose)
            {
                double rel = t + 0.25 * Sq(r.NextDouble()) + 0.1 * (1 - y / sim.S.Height) * r.NextDouble();
                double dv = U(r, 0.1, 0.4);
                sim.Emit(clicks, new Click(rel, Rho0 * 2 * dieVol * dv * 1.3 * 0.6 * Sign(r) / (4 * Math.PI * C0),
                                     Math.Max(dieBody, Hertz(dieMass / 2, 0.0005, sim.GlassEStar, dv))));
                if (r.NextDouble() < 0.4)
                {
                    double dv2 = U(r, 0.2, 1.0);
                    sim.Emit(clicks, new Click(rel + U(r, 0.01, 0.3), Rho0 * 2 * dieVol * dv2 * 1.5 * 0.6 * Sign(r) / (4 * Math.PI * C0),
                                         Math.Max(dieBody, Hertz(dieMass / 2, 0.0005, sim.GlassEStar, dv2))));
                }
            }
        }
    }

    private static double Sign(Random r) => r.NextDouble() < 0.5 ? -1 : 1;

    private static int PoissonCount(Random r, double mean)
    {
        double l = Math.Exp(-mean), p = 1;
        int k = 0;
        do { k++; p *= r.NextDouble(); } while (p > l && k < 50);
        return k - 1;
    }

    // ── The fall and the landing ─────────────────────────────────────────────────────────────────

    /// <summary>Time to fall <paramref name="height"/> from an upward speed <paramref name="up"/>, with
    /// quadratic drag of terminal speed <paramref name="vt"/>; and the speed it arrives at.</summary>
    public static (double Seconds, double Speed) FallWithDrag(double height, double up, double vt)
    {
        double t = 0;
        if (up > 0)
        {
            t = vt / G * Math.Atan(up / vt);
            height += vt * vt / (2 * G) * Math.Log(1 + up * up / (vt * vt));
            up = 0;
        }
        double down = Math.Min(-up, 0.999 * vt);
        double phi0 = Atanh(down / vt);
        double arg = Math.Cosh(phi0) * Math.Exp(G * Math.Max(0, height) / (vt * vt));
        double phi = Math.Log(arg + Math.Sqrt(arg * arg - 1));
        t += vt / G * (phi - phi0);
        return (t, vt * Math.Tanh(phi));
    }

    private static double Atanh(double x) => 0.5 * Math.Log((1 + x) / (1 - x));

    /// <summary>Terminal speed of a plate tumbling (drag coefficient 1.2 on half its face, on average),
    /// or of a die (1.05 on one face).</summary>
    private static double Terminal(double mass, double faceArea, bool cube)
        => Math.Sqrt(2 * mass * G / (Rho0 * (cube ? 1.05 : 1.2) * (cube ? faceArea : 0.5 * faceArea)));

    private static void Fall(Sim sim)
    {
        var r = sim.Rng;
        double drop = QuantiseDrop(sim.S.Drop);
        foreach (var f in sim.Frags)
        {
            if (f.Release < 0) continue;
            bool cube = f.Lx < 0.004 || f.Kind == Kind.Clump && f.Lx < 0.01;
            var (secs, speed) = FallWithDrag(drop + f.Y, f.V0Up, Terminal(f.Mass, f.Area, cube));
            f.LandAt = f.Release + secs;
            f.LandV = speed;
            f.LandSide = f.V0Side;
            f.Events.Add((f.LandAt, 0, 0, 4, 0));    // landed: the break render stops ringing it here
        }
    }

    /// <summary>Every arrival on the ground, in time order, for the landing render.</summary>
    private static void LandingContacts(Sim sim, List<Frag> pieces)
    {
        var r = sim.Rng;
        var clicks = sim.GroundClicks;
        double drop = QuantiseDrop(sim.S.Drop), h = sim.S.Thickness;
        var order = sim.Frags.FindAll(f => f.Release >= 0);
        order.Sort((p, q) => p.LandAt.CompareTo(q.LandAt));
        foreach (var f in order)
        {
            if (f.Hz.Length == 0 && f.Lx < 0.004)
            {
                // A fine: thrown metres, a tiny tick where it comes down and a hop.
                ArriveAndSettle(sim, null, f.LandAt, f.Mass, f.Area * f.H * 2, f.LandV, f.LandSide * 0.3, 0.0003, f.H);
                continue;
            }
            ArriveAndSettle(sim, f, f.LandAt, f.Mass, VolumeOf(f, U(r, 0, 1)), f.LandV, f.LandSide, 0.0005, f.H, pieces);
        }

        if (sim.S.Type != GlassType.Tempered) return;
        // The loose dice, counted: each from its own height and release, with drag; a share to each core,
        // each with its own buffer, because there are tens of thousands and a pane is always a first hearing.
        double dieVol = Sq(sim.DieSize) * h, dieMass = Density * dieVol, vtDie = Terminal(dieMass, Sq(sim.DieSize), true);
        int workers = sim.Out == null ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        var buffers = new double[workers][];
        System.Threading.Tasks.Parallel.For(0, workers, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = workers }, w =>
        {
            var mine = workers == 1 ? sim : new Sim(sim, w, workers, new double[sim.Out!.Length]);

            var rw = mine.Rng;
            for (int i = w; i < sim.DiceLoose; i += workers)
            {
                double y = rw.NextDouble() * sim.S.Height;
                double rel = 0.25 * Sq(rw.NextDouble()) + 0.1 * (1 - y / sim.S.Height) * rw.NextDouble();
                double up = sim.DieKick * U(rw, -0.5, 0.5);
                var (secs, speed) = FallWithDrag(drop + y, up, vtDie);
                ArriveAndSettle(mine, null, rel + secs, dieMass, 2 * dieVol, speed, sim.DieKick * U(rw, 0, 0.8), 0.0005, h);
            }
            // The clumps' dice, scattered where each clump came apart.
            double dieVolume = Sq(sim.DieSize) * sim.S.Thickness;
            for (int b = 0; b < sim.Bursts.Count; b++)
            {
                var (t, n, vz) = sim.Bursts[b];
                for (int k = w; k < n; k += workers)
                {
                    double hop = U(rw, 0.05, 0.4) * vz * mine.Restitution;
                    Bounces(mine, t + U(rw, 0, 0.006), Density * dieVolume, 2 * dieVolume, hop, U(rw, 0.2, 1.2) + 0.3 * vz * rw.NextDouble(),
                            0.0005, sim.S.Thickness, null, sim.DieSize);
                }
            }
            if (workers > 1) buffers[w] = mine.Out!;
            if (workers > 1)
                lock (sim)
                {
                    sim.Emitted += mine.Emitted;
                    sim.FirstT = Math.Min(sim.FirstT, mine.FirstT);
                    sim.LastT = Math.Max(sim.LastT, mine.LastT);
                }
        });
        if (workers > 1)
            foreach (var b in buffers)
                for (int i = 0; i < b.Length; i++) sim.Out![i] += b[i];
    }

    /// <summary>
    /// A piece meeting the ground (or the glass already there): its first contact, a plate's slap as it
    /// rocks flat, breaking if it is big and fast enough on something hard, its bounces, then sliding to rest.
    /// </summary>
    private static void ArriveAndSettle(Sim sim, Frag? f, double t, double mass, double volume, double vz, double vSide,
                                        double radius, double thick, List<Frag>? pieces = null)
    {
        var r = sim.Rng;
        var clicks = sim.GroundClicks;
        bool onGlass = r.NextDouble() < 1 - Math.Exp(-sim.PileCover);
        double eStar = onGlass ? sim.GlassEStar : sim.GroundEStar;
        double e = onGlass ? 0.5 : sim.Restitution;
        double image = onGlass ? 2 : sim.GroundImage;
        Frag? under = null;
        if (onGlass && sim.Landed.Count > 0 && pieces != null) under = pieces[sim.Landed[r.Next(sim.Landed.Count)]];
        bool hard = onGlass || sim.Ground.YoungsModulusGPa >= 1;

        // A piece landing on glass that lies on something soft meets a piece that gives into the carpet or
        // the grass under it: the contact stops it only against that piece, so it is the pair's reduced mass
        // that takes the blow. On something hard the piece underneath is held, and it is the falling one's.
        // Its own change of speed is then that mass over its own, and that is what its click radiates.
        double struck = mass, radiating = volume;
        if (onGlass && sim.Ground.YoungsModulusGPa < 1)
        {
            double other = under?.Mass ?? (sim.S.Type == GlassType.Tempered ? Density * Sq(sim.DieSize) * thick : mass);
            struck = mass * other / (mass + other);
            radiating = volume * struck / mass;
        }
        Hit(sim, clicks, t, struck, radiating, vz, eStar, radius, e, image, f, under, -1, thick, sizeMass: mass);
        if (f != null)
        {
            f.Events.Add((t, 0, 0, 2, LyingLoss(onGlass ? 0.001 : sim.GroundEta, f.Lx)));
            f.OnGround = true;
            // A plate rocks down onto its face a few milliseconds later: the slap, face-on.
            if (f.Kind != Kind.Sliver && f.Lx > 0.01)
                Hit(sim, clicks, t + U(r, 0.001, 0.008), mass, VolumeOf(f, 1), 0.4 * vz, eStar, 0.002, e, image, f, null, RingShare / 2, thick);
            sim.PileCover += f.Area / sim.PileArea;
            if (pieces != null && f.Hz.Length > 0) { pieces.Add(f); sim.Landed.Add(pieces.Count - 1); }

            // Estimate: an annealed piece breaks on hard ground once its speed passes about 2.5 m/s for a
            // 10 cm piece, less for a bigger one (the bending stress grows with its span).
            if (hard && f.Kind != Kind.Clump && f.Lx > 0.02)
            {
                double vBreak = 2.5 * Math.Sqrt(0.1 / f.Lx);
                double pBreak = vz > vBreak ? 1 - Math.Exp(-(Sq(vz / vBreak) - 1)) : 0;
                if (r.NextDouble() < 0.9 * pBreak)
                {
                    f.Events.Add((t + 0.0005, 0, 0, 3, 0));
                    int n = 2 + r.Next(1 + (int)(f.Lx / 0.04));
                    double ke = 0.5 * mass * vz * vz;
                    for (int k = 0; k < n; k++)
                    {
                        double lx = f.Lx * U(r, 0.2, 0.7), ly = Math.Max(0.003, f.Ly * U(r, 0.3, 0.9));
                        double ignore = 0;
                        var piece = AddFrag(sim, lx / ly >= 4 ? Kind.Sliver : Kind.Shard, lx, ly, f.X, f.Y, 1, ref ignore);
                        piece.Born = true;
                        piece.Events.Add((t + 0.0002, 0, 0, 1, 0));
                        piece.Events.Add((t + 0.0003, RingShare * ke / n, Hertz(CornerMass(piece.Mass, thick), radius, eStar, vz), -1, 0));
                        // Thrown sideways and up a little, then down again.
                        double hop = vz * e * U(r, 0.1, 0.5);
                        ArriveAndSettle(sim, piece, t + 2 * hop / G, piece.Mass, VolumeOf(piece, U(r, 0, 1)), hop,
                                        vz * U(r, 0.1, 0.4) + Math.Abs(vSide) * 0.5, radius, thick, pieces);
                    }
                    return;
                }
            }
        }
        else if (sim.S.Type == GlassType.Tempered)
            sim.PileCover += sim.PileGrowth * Sq(sim.DieSize) / sim.PileArea;

        // A clump lands and comes apart into its dice, which scatter.
        if (f != null && f.Kind == Kind.Clump && hard)
        {
            f.Events.Add((t + 0.003, 0, 0, 3, 0));
            sim.Bursts.Add((t, (int)(f.Area / Sq(sim.DieSize)), vz));
            return;
        }

        double v1 = vz * e * U(r, 0.5, 1);
        double side = Math.Abs(vSide) * 0.6 + 0.3 * vz * r.NextDouble() * e;
        Bounces(sim, t, mass, volume, v1, side, radius, thick, f, f == null ? Math.Max(sim.DieSize, thick) : 0);
    }

    /// <summary>Hops of decreasing height, then a slide whose rough ground taps it (the skitter).</summary>
    private static void Bounces(Sim sim, double t, double mass, double volume, double v, double side, double radius,
                                double thick, Frag? f, double asperity = 0)
    {
        var r = sim.Rng;
        var clicks = sim.GroundClicks;
        double e = sim.Restitution;
        bool onGlass = r.NextDouble() < 1 - Math.Exp(-sim.PileCover);
        double eStar = onGlass ? sim.GlassEStar : sim.GroundEStar;
        double image = onGlass ? 2 : sim.GroundImage;
        int guard = 0;
        while (v > 0.12 && guard++ < 12)
        {
            t += 2 * v / G;
            Hit(sim, clicks, t, mass, volume, v, eStar, radius, e, image, f, null, -1, thick);
            v *= e * U(r, 0.6, 1.0);
            side *= 0.8;
        }
        if (sim.AsperityM <= 0 || side < 0.08) return;
        // Sliding: decelerates at mu g; the ground's grain taps it every asperity (a die tumbles, a face at a
        // time).
        double step = Math.Max(sim.AsperityM, asperity);
        double decel = sim.Friction * G, slide = side / decel, s = 0;
        int taps = 0, most = asperity > 0 ? 20 : 80;
        while (s < slide && taps++ < most)
        {
            double speed = side - decel * s;
            if (speed < 0.05) break;
            double dt = step / speed * U(r, 0.5, 1.5);
            s += dt;
            double dv = 0.12 * speed * U(r, 0.3, 1);
            Hit(sim, clicks, t + s, mass, volume, dv, eStar, radius, 0.2, image, f, null, -1, thick);
        }
    }

    // ── Rendering ────────────────────────────────────────────────────────────────────────────────

    private static double[] RenderHole(Sim sim, int rate)
    {
        double eta = sim.S.Type == GlassType.Laminated ? 0.3 : LossFactor + 0.03 + 0.02;   // frame, cracks
        int n = (int)(1.2 * rate);
        var y = new double[n];
        PaneModes(sim, y, rate, eta);
        return Trim(y, rate);
    }

    /// <summary>Lab only: render just one component ("pane", "clicks", "modes"), from OPENFPS_GLASS_ONLY.</summary>
    private static readonly string? Only = Environment.GetEnvironmentVariable("OPENFPS_GLASS_ONLY");
    private static bool Want(string part) => Only == null || Only == part;

    private static double[] RenderBreak(Sim sim, int rate)
    {
        // Long enough for the last piece to leave and the air-borne rings to fade, no longer than 2.5 s.
        double last = 0.2;
        foreach (var f in sim.Frags) if (f.Release >= 0) last = Math.Max(last, Math.Min(f.Release + 1.2, f.LandAt));
        foreach (var c in sim.WindowClicks) last = Math.Max(last, c.T + 0.05);
        int n = (int)(Math.Min(2.5, last + 0.05) * rate);
        var y = new double[n];
        if (Want("pane")) PaneModes(sim, y, rate, LossFactor + 0.03);
        if (Want("clicks")) Clicks(sim.WindowClicks, y, rate, 0);
        if (Want("modes")) AllModes(sim.Frags, y, rate, 0, landing: false);
        return Trim(y, rate);
    }

    private static double[] RenderLand(Sim sim, int rate)
    {
        // The buffer first, so the dice's millions of clicks go straight into it: as long as the last
        // arrival (a die from the top of the pane, released late, thrown up) and a second and a half of
        // bouncing and sliding.
        double last = 0, drop = QuantiseDrop(sim.S.Drop);
        foreach (var f in sim.Frags) if (f.Release >= 0) last = Math.Max(last, f.LandAt);
        if (sim.S.Type == GlassType.Tempered)
        {
            double dieMass = Density * Sq(sim.DieSize) * sim.S.Thickness;
            last = Math.Max(last, 0.35 + FallWithDrag(drop + sim.S.Height, 0.5 * sim.DieKick, Terminal(dieMass, Sq(sim.DieSize), true)).Seconds);
        }
        double t0 = sim.T0;
        int n = (int)((Math.Clamp(last - t0, 0, 8) + 1.5) * rate);
        var y = new double[Math.Max(16, n)];
        sim.Out = y; sim.OutT0 = t0; sim.Rate = rate;
        var pieces = new List<Frag>();
        LandingContacts(sim, pieces);
        if (Want("modes")) AllModes(sim.Frags.FindAll(f => f.OnGround), y, rate, t0, landing: true);
        return Trim(y, rate);
    }

    /// <summary>The pane's odd-odd modes as a baffled piston, struck by the round, cut as the cracks cross it.</summary>
    private static void PaneModes(Sim sim, double[] y, int rate, double eta)
    {
        var s = sim.S;
        double a = s.Width, b = s.Height, M = sim.M2 * a * b, area = a * b, br = BendRoot(s.Thickness);
        double cutStart = 0.5 * sim.CutAt, cutEnd = 1.5 * sim.CutAt;
        int nMax = double.IsInfinity(sim.CutAt) ? y.Length : Math.Min(y.Length, (int)(cutEnd * rate) + 2);
        double fMax = Math.Min(TopHz, 0.45 * rate);
        for (int m = 1; ; m += 2)
        {
            if (Math.PI / 2 * br * Sq((m + 0.35) / a) > fMax) break;
            double sx = Math.Sin(m * Math.PI * sim.ImpactX / a);
            for (int nn = 1; ; nn += 2)
            {
                double f = Math.PI / 2 * br * (Sq((m + 0.35) / a) + Sq((nn + 0.35) / b));
                if (f > fMax) break;
                double phi = 2 * sx * Math.Sin(nn * Math.PI * sim.ImpactY / b);
                double v = sim.J * phi * Contact(f, sim.TauStrike) / M;
                double q = v * area * 8 / (Math.PI * Math.PI * m * nn);
                double w = 2 * Math.PI * f;
                double p = Rho0 * w * q / (2 * Math.PI);       // a metre away, baffled
                double decay = Math.Exp(-eta * w / 2 / rate);
                double c = Math.Cos(w / rate), sn = Math.Sin(w / rate);
                double re = p, im = 0;
                for (int i = 0; i < nMax; i++)
                {
                    double t = (double)i / rate, g = 1;
                    if (t > cutStart) g = t >= cutEnd ? 0 : 0.5 * (1 + Math.Cos(Math.PI * (t - cutStart) / (cutEnd - cutStart)));
                    y[i] += re * g;
                    double nr = (re * c - im * sn) * decay;
                    im = (re * sn + im * c) * decay;
                    re = nr;
                    if (g == 0) break;
                    if ((i & 255) == 0 && Math.Abs(re) + Math.Abs(im) < 1e-6) break;
                }
            }
        }
    }

    /// <summary>Every piece's modes, a few pieces to a core: a pane breaks once, so its render is always a
    /// first hearing and has to be quick. Each worker sums into its own buffer.</summary>
    private static void AllModes(List<Frag> frags, double[] y, int rate, double t0, bool landing)
    {
        // Each piece's events in order first (the sort is not thread-safe on a shared list).
        foreach (var f in frags)
            f.Events.Sort((p, q) => p.T != q.T ? p.T.CompareTo(q.T) : (p.State >= 0 ? 0 : 1).CompareTo(q.State >= 0 ? 0 : 1));
        int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        if (workers == 1 || frags.Count < 8) { foreach (var f in frags) Modes(f, y, rate, t0, landing); return; }
        var partial = new double[workers][];
        System.Threading.Tasks.Parallel.For(0, workers, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = workers }, w =>
        {
            var mine = new double[y.Length];
            for (int i = w; i < frags.Count; i += workers) Modes(frags[i], mine, rate, t0, landing);
            partial[w] = mine;
        });
        foreach (var part in partial)
            for (int i = 0; i < y.Length; i++) y[i] += part[i];
    }

    /// <summary>Each fragment's modes: impulses at its contacts, damped by whatever it is touching. The break
    /// hears a piece until it lands (or is destroyed); the landing from when it lands (or is born there).</summary>
    private static void Modes(Frag f, double[] y, int rate, double t0, bool landing)
    {
        if (f.Hz.Length == 0 || f.Events.Count == 0) return;
        var ev = f.Events;     // in time order, a change of state before an impulse at the same moment (AllModes)
        var r = new Random((int)(f.Mass * 1e9 + f.X * 1e5) & 0x7fffffff);
        for (int k = 0; k < f.Hz.Length; k++)
        {
            if (f.Hz[k] > 0.45 * rate) continue;
            double w = 2 * Math.PI * f.Hz[k];
            double c = Math.Cos(w / rate), sn = Math.Sin(w / rate), gain = f.Gain[k];
            double re = 0, im = 0, etaState = InFrameLoss;      // in the frame until it is released
            bool on = !landing || f.Born, stopped = false;
            int i = 0;
            foreach (var x in ev)
            {
                int at = (int)Math.Round((x.T - t0) * rate);
                if (landing && x.State == 2) on = true;
                bool stop = landing ? on && x.State == 3 : x.State == 3 || x.State == 4;
                if (at > i)
                {
                    if (on) Run(y, ref re, ref im, ref i, Math.Min(y.Length, at), gain, c, sn, Decay(f, k, etaState, w, rate));
                    else i = at;
                }
                if (stop) { FadeOut(y, ref re, ref im, ref i, rate, gain, c, sn); stopped = true; break; }
                if (i >= y.Length) { stopped = true; break; }
                if (x.State == 0) etaState = InFrameLoss;
                else if (x.State == 1) etaState = 0;
                else if (x.State == 2) etaState = x.Eta;
                if (on && x.Joules > 0)
                {
                    // This mode's share of the energy: how many modes it stands for, through the contact's
                    // spectrum, over every mode the contact reaches (most of a small piece's are ultrasonic).
                    double reach = Math.Max(1, f.ModesUpTo(1.0 / Math.Max(1e-7, x.Tau)));
                    double total = 0;
                    for (int j = 0; j < f.Hz.Length; j++) total += f.Count[j] * Sq(Contact(f.Hz[j], x.Tau));
                    double frac = f.Count[k] * Sq(Contact(f.Hz[k], x.Tau)) / Math.Max(reach, total);
                    re += Math.Sqrt(2 * x.Joules * frac / f.Mass) * (r.NextDouble() < 0.5 ? -1 : 1);
                }
            }
            if (on && !stopped) Run(y, ref re, ref im, ref i, y.Length, gain, c, sn, Decay(f, k, etaState, w, rate));
        }
    }

    private static double Decay(Frag f, int k, double etaState, double w, int rate)
        => Math.Exp(-(LossFactor + f.ExtraEta + f.EtaRad[k] + etaState) * w / 2 / rate);

    private static void Run(double[] y, ref double re, ref double im, ref int i, int until, double gain, double c, double sn, double decay)
    {
        for (; i < until && i < y.Length; i++)
        {
            y[i] += gain * re;
            double nr = (re * c - im * sn) * decay;
            im = (re * sn + im * c) * decay;
            re = nr;
            if ((i & 63) == 0 && gain * (Math.Abs(re) + Math.Abs(im)) < SilentPascals) { re = im = 0; i = Math.Max(i, until); break; }
        }
    }

    private static void FadeOut(double[] y, ref double re, ref double im, ref int i, int rate, double gain, double c, double sn)
    {
        int n = rate / 500;
        for (int k = 0; k < n && i < y.Length; k++, i++)
        {
            y[i] += gain * re * (1 - (double)k / n);
            double nr = re * c - im * sn;
            im = re * sn + im * c;
            re = nr;
        }
        re = im = 0;
    }

    private static void Clicks(List<Click> clicks, double[] y, int rate, double t0)
    {
        foreach (var c in clicks) RenderClick(c, y, rate, t0);
    }

    private static void RenderClick(Click c, double[] y, int rate, double t0)
    {
        int at = (int)Math.Floor((c.T - t0) * rate);
        if (at < 0 || at >= y.Length) return;
        var k = Kernel(c.Tau, rate);
        int lead = KernelLead;
        for (int j = 0; j < k.Length; j++)
        {
            int idx = at - lead + j;
            if ((uint)idx < (uint)y.Length) y[idx] += c.Amp * k[j];
        }
    }

    /// <summary>Output samples of each kernel before its pulse begins (its filter's half length).</summary>
    private const int KernelLead = 12;

    /// <summary>A mode quieter than this at a metre (43 dB SPL, some 80 dB under the crash it is part of)
    /// stops being computed until something strikes it again.</summary>
    private const double SilentPascals = 3e-3;

    private static readonly ConcurrentDictionary<(int Bin, int Rate), double[]> _kernels = new();

    /// <summary>Lab: a contact kernel's peak and length, against the unfiltered pulse's pi^2 / (2 tau^2).</summary>
    public static (double Peak, int Length, double Analytic) KernelInfo(double tau, int rate)
    {
        var k = Kernel(tau, rate);
        double peak = 0;
        foreach (double v in k) peak = Math.Max(peak, Math.Abs(v));
        return (peak, k.Length, Math.PI * Math.PI / (2 * tau * tau));
    }

    /// <summary>
    /// The derivative of a unit-area half-sine pulse of duration tau, band-limited to the output rate: what
    /// a contact's acceleration does to the air. Built at sixteen times the rate and filtered down, cached
    /// by duration in 4 % steps.
    /// </summary>
    private static double[] Kernel(double tau, int rate)
    {
        int bin = (int)Math.Round(Math.Log(Math.Clamp(tau, 1e-6, 0.02) / 1e-6) / Math.Log(1.04));
        return _kernels.GetOrAdd((bin, rate), key =>
        {
            double t = 1e-6 * Math.Pow(1.04, key.Bin);
            // Sixteen times the rate for a contact shorter than a few samples; a longer one is smooth already.
            int over = t * key.Rate > 8 ? 1 : 16;
            double fs = (double)key.Rate * over, dt = 1 / fs;
            int pulse = Math.Max(1, (int)Math.Round(t * fs));
            int half = KernelLead * over;
            int len = pulse + 2 * half + 2;
            // The half-sine sampled finely with an area of one, and its derivative as a finite difference, so
            // the derivative integrates to exactly nothing and its integral's integral is one.
            var pulseS = new double[pulse];
            double sum = 0;
            for (int i = 0; i < pulse; i++) { pulseS[i] = Math.Sin(Math.PI * (i + 0.5) / pulse); sum += pulseS[i] * dt; }
            var d = new double[len];
            for (int i = 0; i <= pulse; i++)
            {
                double now = i < pulse ? pulseS[i] / sum : 0, before = i > 0 ? pulseS[i - 1] / sum : 0;
                d[half + i] = (now - before) / dt;
            }
            // Low-pass at 0.45 of the output rate (Blackman-windowed sinc) and take every sixteenth sample.
            double fc = 0.45 * key.Rate / fs;
            var outLen = len / over + 1;
            var o = new double[outLen];
            for (int k = 0; k < outLen; k++)
            {
                int centre = k * over;
                double acc = 0;
                for (int j = -half; j <= half; j++)
                {
                    int idx = centre + j;
                    if (idx < 0 || idx >= len || d[idx] == 0) continue;
                    double x = j;
                    double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
                    double win = 0.42 + 0.5 * Math.Cos(Math.PI * j / half) + 0.08 * Math.Cos(2 * Math.PI * j / half);
                    acc += d[idx] * sinc * win;
                }
                o[k] = acc;
            }
            return o;
        });
    }

    private static double[] Trim(double[] y, int rate)
    {
        double peak = 0;
        foreach (double v in y) peak = Math.Max(peak, Math.Abs(v));
        int end = y.Length;
        while (end > rate / 20 && Math.Abs(y[end - 1]) < peak * 1e-4) end--;
        int fade = Math.Min(end, rate / 100);
        for (int i = 0; i < fade; i++) y[end - 1 - i] *= (double)i / fade;
        if (end < y.Length) Array.Resize(ref y, end);
        return y;
    }
}
