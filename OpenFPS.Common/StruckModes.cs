using System.Collections.Generic;
using static OpenFPS.Common.DoorPhysics;

namespace OpenFPS.Common;

/// <summary>The simple shapes a struck thing can be (docs/MATTER.md 7.2). Append only: keys carry the number.</summary>
public enum StruckShape : byte
{
    /// <summary>A solid box: a cube, a brick, a block of stone. Its modes by Rayleigh-Ritz.</summary>
    Block,
    /// <summary>A bar or a plank, struck on its face: bending (and twisting, off the centre line).</summary>
    Bar,
    /// <summary>A thin panel held at its edges (simply supported): a wall's leaf, a pane, a door.</summary>
    Plate,
    /// <summary>A thin sheet free at its edges: a sheet hung on a string, a tray.</summary>
    FreePlate,
    /// <summary>A pipe or a tube, struck on its side: the beam's bending and the wall's ring modes.</summary>
    Tube,
    /// <summary>A closed box of sheet (a cabinet, a car's door, a bin): its face over the air inside.</summary>
    ShellBox,
}

/// <summary>What holds a struck thing, and so how much of its ring the holding takes. Append only.</summary>
public enum StruckSupport : byte
{
    /// <summary>On a string: free, nothing takes its ring but the air.</summary>
    Hung,
    /// <summary>Lying on the ground: the ground takes a little of the ring and the force goes into it.</summary>
    Resting,
    /// <summary>In a hand: the hand's flesh damps it.</summary>
    Held,
    /// <summary>Fixed into a building or a frame: its edges carry the ring away (EN 12354-1 Annex C).</summary>
    Built,
}

/// <summary>
/// The vibration modes of the simple shapes, from the material and the size (docs/MATTER.md 7.1-7.2): each
/// mode's frequency, modal mass, shape at the struck point, loss and radiation. For a given shape every mode
/// scales as sqrt(E / rho) over the size (and thickness over span squared for thin things), so the dimensionless
/// part is computed once per shape and aspect and kept; only the scaling is per material and size.
///
/// Analytic where the shape has a closed form: Euler-Bernoulli bars (free-free and pinned) with Rayleigh-
/// Timoshenko's shear and rotary-inertia correction, the simply supported orthotropic plate (Huber's rigidity
/// for wood across the grain), Warburton's free-plate approximation (Leissa, Vibration of Plates, NASA SP-160,
/// 1969, ch. 4), thin rings for a tube's wall (Love). A solid block has no closed form and is solved by
/// Rayleigh-Ritz on Legendre polynomials (Visscher et al., JASA 90 (1991) 2154, the method behind resonant
/// ultrasound spectroscopy), split by its eight symmetry classes.
/// </summary>
internal static class StruckModes
{
    /// <summary>One mode: its note, modal mass (kg), loss factor, its radiation (pressure at a metre per
    /// unit modal velocity, with the sign its pressure arrives with) and its shape at each point asked for.</summary>
    internal sealed class Set
    {
        public readonly List<double> Hz = new(), Mass = new(), Loss = new(), Radiation = new();
        /// <summary>The pressure at the listener per unit modal acceleration, in phase and in quadrature
        /// (DoorPhysics.Modes' Gain and GainQuad): a panel's from Rayleigh's integral towards the listener, so its
        /// modes add as they do there (coherently: below coincidence a point-driven panel's sound is mostly their
        /// cancelling); anything else's from its power, with a sign of its own.</summary>
        public readonly List<double> Acc = new(), Quad = new();
        public readonly List<double[]> Shape = new();
        public int Count => Hz.Count;
        public double TotalMass, Volume, RadiatingArea;
        /// <summary>Above this the thing's modes are too dense to list one by one and a statistical field
        /// carries them (plates); zero when the list is complete.</summary>
        public double DenseFromHz;
        public double PlateThickness, PlateA, PlateB;
        /// <summary>A thin part's critical frequency (Hz), infinity for a solid one.</summary>
        public double CriticalHz = double.PositiveInfinity;

        public void Add(double hz, double mass, double loss, double radiation, double[] shape)
        {
            Hz.Add(hz); Mass.Add(mass); Loss.Add(loss); Radiation.Add(radiation); Shape.Add(shape);
            // p = R v: in DoorPhysics.Modes' terms, -GainQuad w V with GainQuad = -R / w.
            Acc.Add(0); Quad.Add(-radiation / (2 * Math.PI * hz));
        }

        public void AddCoherent(double hz, double mass, double loss, double radiation, double acc, double quad, double[] shape)
        {
            Hz.Add(hz); Mass.Add(mass); Loss.Add(loss); Radiation.Add(radiation); Shape.Add(shape);
            Acc.Add(acc); Quad.Add(quad);
        }
    }

    /// <summary>The highest mode kept, Hz: the decimation to the mixer's rate removes everything above 20 kHz.</summary>
    internal const double MaxHz = 20000;

    /// <summary>The most modes listed one by one. Past this a plate's are a dense field (DoorPhysics.DenseField),
    /// at a fraction of the cost and, where the modes overlap, the same sound.</summary>
    internal const int MaxListed = 60;

    // ── Losses ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the holding takes, as a loss factor. Built: the larger of the edges' coupling into a structure of
    /// its own weight, m''/(485 sqrt f) (EN 12354-1:2000 eq. C.4), and a light panel's mounting loss
    /// (<see cref="PanelAcoustics.MountedLoss"/>). The others are estimates: a string takes almost nothing;
    /// resting on hard ground, the few points it touches (a few thousandths, the range Cremer and Heckl give for
    /// built-up structures' joints); a hand's flesh (a few hundredths: a tuning fork held by its stem rings, held
    /// by the tines it does not).
    /// </summary>
    internal static double SupportLoss(StruckSupport support, double surfaceDensity, double hz, bool thinAndFlat, bool glazed = false)
        => support switch
        {
            // A pane in its frame sits in EPDM gaskets: 0.02, and more at its lowest modes, where its edges turn
            // most in them, 3 / f: GlassDoor's figures, which ring a pane as the recordings' glass doors ring
            // (75-81 Hz, T60 0.39-0.51 s).
            StruckSupport.Built when glazed => 0.02 + 3 / Math.Max(hz, 20),
            StruckSupport.Hung => 2e-5,
            // A sheet lying flat on the ground has the air film and the ground under all of it.
            StruckSupport.Resting => thinAndFlat ? 0.05 : 0.001,
            StruckSupport.Held => 0.03,
            _ => Math.Max(surfaceDensity / (485 * Math.Sqrt(Math.Max(hz, 20))), PanelAcoustics.MountedLoss),
        };

    /// <summary>A mode's radiation and the loss it causes, from its radiation efficiency: radiated power
    /// rho0 c S sigma &lt;phi^2&gt; v^2 over the energy M v^2 the mode holds. Pressure at a metre from that
    /// power spread over <paramref name="solidAngle"/>.</summary>
    internal static (double Gain, double Loss) Radiate(double hz, double modalMass, double area, double meanShape2,
                                                         double sigma, double solidAngle)
    {
        double w = 2 * Math.PI * hz;
        double loss = Rho0 * C0 * area * sigma * meanShape2 / (w * modalMass);
        double gain = Rho0 * C0 * Math.Sqrt(Math.Max(0, area * sigma * meanShape2 / solidAngle));
        return (gain, loss);
    }

    /// <summary>
    /// Front and back of a thing in open air (not set in a wall) push the air opposite ways, and below the
    /// frequency where the way round its edge is half a wavelength the two cancel, as a dipole: the share of
    /// the baffled radiation left, (k d/2)^2 / (1 + (k d/2)^2), d its narrower face (EST, the dipole's low-
    /// frequency law with its corner put at that edge). Why a bar of a glockenspiel needs a resonator under it.
    /// </summary>
    internal static double Unbaffled(double hz, double narrowest)
    {
        double x = 2 * Math.PI * hz / C0 * narrowest / 2;
        return x * x / (1 + x * x);
    }

    // ── Bars ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Free-free beam roots, beta_n L; past these, (2n + 1) pi / 2.</summary>
    private static readonly double[] FreeRoots = { 4.73004074, 7.85320462, 10.9956078, 14.1371655, 17.2787597 };

    internal static double FreeRoot(int n) => n < FreeRoots.Length ? FreeRoots[n] : (2 * n + 3) * Math.PI / 2;

    /// <summary>The free-free beam's mode n (0 first) at x/L, normalised to a mean square of one.</summary>
    internal static double FreeBeam(int n, double xOverL)
    {
        double b = FreeRoot(n), x = b * Math.Clamp(xOverL, 0, 1);
        double s = (Math.Cosh(b) - Math.Cos(b)) / (Math.Sinh(b) - Math.Sin(b));
        // Past about 8 the hyperbolic terms cancel to rounding error: use the asymptotic form there.
        if (b > 30)
        {
            double xl = Math.Clamp(xOverL, 0, 1);
            return Math.Cos(x) - Math.Sin(x) + Math.Exp(-x) + (n % 2 == 0 ? 1 : -1) * Math.Exp(-b * (1 - xl));
        }
        return Math.Cosh(x) + Math.Cos(x) - s * (Math.Sinh(x) + Math.Sin(x));
    }

    /// <summary>
    /// A bar struck on its face: bending in its thickness, and if struck off its centre line, twisting.
    /// Free at both ends unless built in, where it is pinned at both (a pale between its rails). Rayleigh-
    /// Timoshenko: the Euler-Bernoulli note divided by sqrt(1 + (beta r)^2 (1 + E/(kappa G))), r = h/sqrt 12,
    /// which a stubby bar needs (Goens 1931; Han, Benaroya and Wei, J. Sound Vib. 225 (1999) 935).
    /// </summary>
    internal static Set Bar(MaterialProperties m, double length, double width, double thick, StruckSupport support,
                            IReadOnlyList<(double U, double V)> points, int seed)
    {
        var set = new Set();
        double rho = Math.Max(1, m.DensityKgM3), e = m.YoungsModulusGPa * 1e9, nu = m.Poisson;
        // Wood's shear modulus is about a fifteenth of its stiffness along the grain (Wood Handbook table 5-1:
        // G_LR/E_L 0.06-0.08); anything else is isotropic.
        double g = m.TransverseModulusGPa > 0 ? 0.07 * e : e / (2 * (1 + nu));
        double area = width * thick, r = thick / Math.Sqrt(12);
        double mass = rho * area * length;
        set.TotalMass = mass; set.Volume = area * length; set.RadiatingArea = 2 * (length * width);
        bool pinned = support == StruckSupport.Built;
        var rng = new Random(seed);
        // Bending.
        for (int n = 0; n < 60; n++)
        {
            double betaL = pinned ? (n + 1) * Math.PI : FreeRoot(n);
            double beta = betaL / length;
            double hzEb = betaL * betaL / (2 * Math.PI * length * length) * r * Math.Sqrt(e / rho);
            double hz = hzEb / Math.Sqrt(1 + beta * beta * r * r * (1 + e / (5.0 / 6 * g)));
            if (hz > MaxHz) break;
            if (hz < 15) continue;
            var shape = new double[points.Count];
            for (int i = 0; i < points.Count; i++)
                shape[i] = pinned ? Math.Sqrt(2) * Math.Sin((n + 1) * Math.PI * points[i].U) : FreeBeam(n, points[i].U);
            double fc = WallTransmission.CriticalHz((float)e, (float)rho, (float)thick);
            double sigma = RadiationEfficiency(hz, fc, length, width) * Unbaffled(hz, width);
            double loss = m.LossAt((float)hz) + SupportLoss(support, rho * thick, hz, false);
            var (gain, radLoss) = Radiate(hz, mass, set.RadiatingArea, 1.0, sigma, 4 * Math.PI);
            set.Add(hz, mass, loss + radLoss, gain * Sign(rng), shape);
        }
        // Twisting: torsion constant of a rectangle (Roark), polar inertia w h (w^2 + h^2) / 12.
        double wide = Math.Max(width, thick), narrow = Math.Min(width, thick);
        double j = wide * narrow * narrow * narrow * (1.0 / 3 - 0.21 * narrow / wide * (1 - Math.Pow(narrow / wide, 4) / 12));
        double ip = width * thick * (width * width + thick * thick) / 12;
        for (int n = 1; n < 30; n++)
        {
            double hz = n / (2 * length) * Math.Sqrt(g * j / (rho * ip));
            if (hz > MaxHz) break;
            // A rotation theta moves the face at offset y across the width by theta y; the modal "mass" is the
            // twisting inertia, rho Ip L / 2, per unit of theta, so per metre at the edge it is that over (w/2)^2.
            double modal = rho * ip * length / 2 / (width * width / 4);
            var shape = new double[points.Count];
            for (int i = 0; i < points.Count; i++)
                shape[i] = (points[i].V - 0.5) * 2 * (pinned ? Math.Sin(n * Math.PI * points[i].U) : Math.Cos(n * Math.PI * points[i].U));
            double sigma = RadiationEfficiency(hz, WallTransmission.CriticalHz((float)e, (float)rho, (float)thick), length, width)
                           * Unbaffled(hz, width / 2) * Unbaffled(hz, width / 2);
            double loss = m.LossAt((float)hz) + SupportLoss(support, rho * thick, hz, false);
            var (gain, radLoss) = Radiate(hz, modal, set.RadiatingArea, 1.0 / 6, sigma, 4 * Math.PI);
            set.Add(hz, modal, loss + radLoss, gain * Sign(rng), shape);
        }
        return set;
    }

    // ── Plates ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A thin plate held at its edges, a by b, with the grain (if any) along a: Huber's orthotropic plate,
    /// f = (pi / 2) sqrt(1 / rho h) sqrt(Dx (m/a)^4 + 2 H (m/a)^2 (n/b)^2 + Dy (n/b)^4), H = sqrt(Dx Dy).
    /// Each mode radiates from the Rayleigh integral over its face into the half space in front (baffled),
    /// as the approved doors' panels do (DoorPhysics.Plate). The lowest <see cref="MaxListed"/> are listed;
    /// above them the field is dense (<see cref="Set.DenseFromHz"/>).
    /// </summary>
    internal static Set Plate(MaterialProperties m, double a, double b, double h, StruckSupport support, double panelLoss,
                              double cavityDepth, IReadOnlyList<(double U, double V)> points, int seed, bool baffled = true)
    {
        var set = new Set();
        double rho = Math.Max(1, m.DensityKgM3), nu = m.Poisson;
        double ex = m.YoungsModulusGPa * 1e9, ey = m.AcrossGrainGPa * 1e9;
        double dx = ex * h * h * h / (12 * (1 - nu * nu)), dy = ey * h * h * h / (12 * (1 - nu * nu));
        double rhoH = rho * h;
        set.TotalMass = rhoH * a * b; set.Volume = a * b * h; set.RadiatingArea = a * b;
        set.PlateThickness = h; set.PlateA = a; set.PlateB = b;
        set.CriticalHz = WallTransmission.CriticalHz((float)Math.Sqrt(ex * ey), (float)rho, (float)h);
        var list = new List<(double Hz, int M, int N)>();
        for (int i = 1; i < 200; i++)
        {
            bool any = false;
            for (int j = 1; j < 200; j++)
            {
                double p = i / a, q = j / b;
                double hz = Math.PI / 2 * Math.Sqrt((dx * p * p * p * p + 2 * Math.Sqrt(dx * dy) * p * p * q * q + dy * q * q * q * q) / rhoH);
                if (hz > MaxHz) break;
                any = true;
                list.Add((hz, i, j));
            }
            if (!any) break;
        }
        list.Sort((x, y) => x.Hz.CompareTo(y.Hz));
        if (list.Count > MaxListed) { set.DenseFromHz = list[MaxListed].Hz; list.RemoveRange(MaxListed, list.Count - MaxListed); }
        var rng = new Random(seed);
        double modal = rhoH * a * b / 4;
        // The air behind a leaf (a stud wall's cavity, a box's inside) is a spring on every mode that moves air:
        // rho0 c^2 (integral of phi)^2 / V (the mass-air-mass resonance of a double wall, mode by mode).
        double cavityVolume = cavityDepth > 0 ? a * b * cavityDepth : 0;
        foreach (var (hz0, i, j) in list)
        {
            double hz = hz0;
            if (cavityVolume > 0 && i % 2 == 1 && j % 2 == 1)
            {
                double net = 4 * a * b / (i * j * Math.PI * Math.PI);
                double w0 = 2 * Math.PI * hz0;
                hz = Math.Sqrt(w0 * w0 + Rho0 * C0 * C0 * net * net / cavityVolume / modal) / (2 * Math.PI);
            }
            var shape = new double[points.Count];
            for (int k = 0; k < points.Count; k++)
                shape[k] = Math.Sin(i * Math.PI * points[k].U) * Math.Sin(j * Math.PI * points[k].V);
            double w = 2 * Math.PI * hz;
            double loss = m.LossAt((float)hz) + (panelLoss > 0 ? panelLoss : SupportLoss(support, rhoH, hz, true, m.Family == "glass"));
            if (baffled)
            {
                // In a wall: Rayleigh's integral. The level from its mean over the half space in front, the phase from
                // the listener's direction (DoorPhysics.Plate's way), so the modes add as they would there.
                double meanP2 = RayleighMeanP2(i, j, a, b, hz);
                var (pr, pi) = RayleighAt(i, j, a, b, hz);
                double mag = Math.Sqrt(pr * pr + pi * pi);
                if (mag < 1e-30) { pr = 1; pi = 0; mag = 1; }
                double power = meanP2 * 2 * Math.PI / (2 * Rho0 * C0);
                double sigma = Math.Min(power / (Rho0 * C0 * a * b * 0.25 * 0.5 / (w * w)), 2.0);
                double radLoss = Rho0 * C0 * sigma / (w * rhoH);
                set.AddCoherent(hz, modal, loss + radLoss, Math.Sqrt(meanP2) * w, Math.Sqrt(meanP2) * pr / mag,
                                Math.Sqrt(meanP2) * pi / mag, shape);
            }
            else
            {
                // A sheet in open air: Maidanik's average, its edges cancelling below their dipole corner.
                double sigma = RadiationEfficiency(hz, set.CriticalHz, a, b) * Unbaffled(hz, Math.Min(a, b));
                double radLoss = 2 * Rho0 * C0 * sigma / (w * rhoH);
                double gain = Radiate(hz, modal, 2 * a * b, 0.25, sigma, 4 * Math.PI).Gain;
                set.Add(hz, modal, loss + radLoss, gain * Sign(rng), shape);
            }
        }
        return set;
    }

    /// <summary>The far-field integral of mode (m, n) towards the listener (DoorPhysics.Plate.ListenerTheta and
    /// ListenerPhi), per unit acceleration: its real and imaginary parts.</summary>
    private static (double Re, double Im) RayleighAt(int m, int n, double a, double b, double hz)
    {
        double k = 2 * Math.PI * hz / C0, km = m * Math.PI / a, kn = n * Math.PI / b;
        double th = DoorPhysics.Plate.ListenerTheta, ph = DoorPhysics.Plate.ListenerPhi;
        var cx = DoorPhysics.Plate.Integral(x => Math.Sin(km * x), a, k * Math.Sin(th) * Math.Cos(ph), km);
        var cy = DoorPhysics.Plate.Integral(y => Math.Sin(kn * y), b, k * Math.Sin(th) * Math.Sin(ph), kn);
        return (cx.re * cy.re - cx.im * cy.im, cx.re * cy.im + cx.im * cy.re);
    }

    private static double RayleighMeanP2(int m, int n, double a, double b, double hz)
    {
        double k = 2 * Math.PI * hz / C0, km = m * Math.PI / a, kn = n * Math.PI / b;
        const int rings = 8, spokes = 12;
        double sumP2 = 0, sumW = 0;
        for (int i = 0; i < rings; i++)
        {
            double th = (i + 0.5) * (Math.PI / 2) / rings;
            double wgt = Math.Sin(th);
            for (int j = 0; j < spokes; j++)
            {
                double ph = (j + 0.5) * 2 * Math.PI / spokes;
                var ix = DoorPhysics.Plate.Integral(x => Math.Sin(km * x), a, k * Math.Sin(th) * Math.Cos(ph), km);
                var iy = DoorPhysics.Plate.Integral(y => Math.Sin(kn * y), b, k * Math.Sin(th) * Math.Sin(ph), kn);
                double re = ix.re * iy.re - ix.im * iy.im, im = ix.re * iy.im + ix.im * iy.re;
                sumP2 += (re * re + im * im) * wgt;
                sumW += wgt;
            }
        }
        double scale = Rho0 / (2 * Math.PI);
        return sumP2 / sumW * scale * scale;
    }

    // Warburton's coefficients for a free edge: G, H, J by the number of nodal lines across.
    private static (double G, double H, double J) Warburton(int m)
    {
        if (m == 0) return (0, 0, 0);
        if (m == 1) return (0, 0, 12 / (Math.PI * Math.PI));
        if (m == 2) return (1.506, 1.248, 5.017);
        double g = m - 1.5;
        return (g, g * g * (1 - 2 / (g * Math.PI)), g * g * (1 + 6 / (g * Math.PI)));
    }

    /// <summary>A free edge's beam function for Warburton index m: flat (0), a tilt (1), the free-free beam's
    /// mode m - 2 after; mean square one.</summary>
    private static double FreeFunction(int m, double u)
        => m == 0 ? 1 : m == 1 ? Math.Sqrt(3) * (2 * u - 1) : FreeBeam(m - 2, u);

    /// <summary>
    /// A thin sheet free at every edge (hung, held, or lying loose): Warburton's approximation (Leissa 1969,
    /// eq. 4.25), lambda^2 = Gx^4 + Gy^4 (a/b)^4 + 2 (a/b)^2 [nu Hx Hy + (1 - nu) Jx Jy], w = (pi^2 / a^2)
    /// sqrt(D / rho h) lambda, with the rigidities split by the grain as Huber's. Within about 5 % of Leissa's
    /// exact values for a square (13.5 against 14.2 for the first, twisting, mode).
    /// </summary>
    internal static Set FreePlate(MaterialProperties m, double a, double b, double h, StruckSupport support,
                                  IReadOnlyList<(double U, double V)> points, int seed)
    {
        var set = new Set();
        double rho = Math.Max(1, m.DensityKgM3), nu = m.Poisson;
        double ex = m.YoungsModulusGPa * 1e9, ey = m.AcrossGrainGPa * 1e9;
        double dx = ex * h * h * h / (12 * (1 - nu * nu)), dy = ey * h * h * h / (12 * (1 - nu * nu)), dxy = Math.Sqrt(dx * dy);
        double rhoH = rho * h, r = a / b;
        set.TotalMass = rhoH * a * b; set.Volume = a * b * h; set.RadiatingArea = 2 * a * b;
        set.PlateThickness = h; set.PlateA = a; set.PlateB = b;
        set.CriticalHz = WallTransmission.CriticalHz((float)Math.Sqrt(ex * ey), (float)rho, (float)h);
        var list = new List<(double Hz, int M, int N)>();
        for (int i = 0; i < 120; i++)
        {
            bool any = false;
            for (int j = 0; j < 120; j++)
            {
                if (i + j < 2 || (i < 2 && j < 2 && !(i == 1 && j == 1))) continue;   // the rigid motions
                var (gx, hx, jx) = Warburton(i);
                var (gy, hy, jy) = Warburton(j);
                double lam2 = dx / dxy * Math.Pow(gx, 4) + dy / dxy * Math.Pow(gy, 4) * Math.Pow(r, 4)
                              + 2 * r * r * (nu * hx * hy + (1 - nu) * jx * jy);
                double hz = Math.PI * Math.PI / (a * a) * Math.Sqrt(dxy / rhoH) * Math.Sqrt(Math.Max(0, lam2)) / (2 * Math.PI);
                if (hz > MaxHz) { if (j > 2) break; continue; }
                any = true;
                list.Add((hz, i, j));
            }
            if (!any && i > 2) break;
        }
        list.Sort((x, y) => x.Hz.CompareTo(y.Hz));
        if (list.Count > MaxListed) { set.DenseFromHz = list[MaxListed].Hz; list.RemoveRange(MaxListed, list.Count - MaxListed); }
        var rng = new Random(seed);
        double modal = rhoH * a * b;   // shapes of mean square one
        foreach (var (hz, i, j) in list)
        {
            var shape = new double[points.Count];
            for (int k = 0; k < points.Count; k++) shape[k] = FreeFunction(i, points[k].U) * FreeFunction(j, points[k].V);
            double sigma = RadiationEfficiency(hz, set.CriticalHz, a, b) * Unbaffled(hz, Math.Min(a, b));
            double loss = m.LossAt((float)hz) + SupportLoss(support, rhoH, hz, true);
            var (gain, radLoss) = Radiate(hz, modal, 2 * a * b, 1.0, sigma, 4 * Math.PI);
            set.Add(hz, modal, loss + radLoss, gain * Sign(rng), shape);
        }
        return set;
    }

    // ── Tubes ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A tube struck on its side: the whole tube bends as a beam (I = pi (Ro^4 - Ri^4) / 4), and its wall rings
    /// in its circumferential modes n = 2, 3, ... as a thin ring (DoorPhysics.Ring, Love's inextensional
    /// modes), whose shapes cos(n theta) all move at the struck line. A ring mode radiates as a multipole
    /// of order n: little until the circumference is n wavelengths round (EST: 1 / (1 + (n / kR)^(2n))).
    /// </summary>
    internal static Set Tube(MaterialProperties m, double length, double diameter, double wall, StruckSupport support,
                             IReadOnlyList<(double U, double V)> points, int seed)
    {
        var set = new Set();
        double rho = Math.Max(1, m.DensityKgM3), e = m.YoungsModulusGPa * 1e9;
        double ro = diameter / 2, ri = Math.Max(0, ro - wall), rm = (ro + ri) / 2;
        double area = Math.PI * (ro * ro - ri * ri), inertia = Math.PI * (Math.Pow(ro, 4) - Math.Pow(ri, 4)) / 4;
        double mass = rho * area * length, rg = Math.Sqrt(inertia / area);
        set.TotalMass = mass; set.Volume = area * length; set.RadiatingArea = Math.PI * diameter * length;
        bool pinned = support == StruckSupport.Built;
        var rng = new Random(seed);
        for (int n = 0; n < 40; n++)
        {
            double betaL = pinned ? (n + 1) * Math.PI : FreeRoot(n);
            double hz = betaL * betaL / (2 * Math.PI * length * length) * rg * Math.Sqrt(e / rho);
            if (hz > MaxHz) break;
            if (hz < 15) continue;
            var shape = new double[points.Count];
            for (int i = 0; i < points.Count; i++)
                shape[i] = pinned ? Math.Sqrt(2) * Math.Sin((n + 1) * Math.PI * points[i].U) : FreeBeam(n, points[i].U);
            // A cylinder moving sideways is a line dipole: (k ro)^2 below its corner.
            double sigma = Unbaffled(hz, 2 * ro);
            double loss = m.LossAt((float)hz) + SupportLoss(support, rho * wall, hz, false);
            var (gain, radLoss) = Radiate(hz, mass, set.RadiatingArea, 0.5, sigma, 4 * Math.PI);
            set.Add(hz, mass, loss + radLoss, gain * Sign(rng), shape);
        }
        if (wall < ro * 0.5)
        {
            double h = ro - ri;
            for (int n = 2; n < 24; n++)
            {
                double hz = Ring(rm, h, rho, e, n);
                if (hz > MaxHz) break;
                // The ring's radial and tangential motion together: kinetic energy (1 + 1/n^2) of the radial's.
                double modal = rho * h * length * Math.PI * rm * (1 + 1.0 / (n * n)) / 2 * 2;
                var shape = new double[points.Count];
                for (int i = 0; i < points.Count; i++) shape[i] = 1;   // the struck line is an antinode
                double kr = 2 * Math.PI * hz / C0 * ro;
                double sigma = 1 / (1 + Math.Pow(n / Math.Max(1e-6, kr), 2 * n));
                double loss = m.LossAt((float)hz) + SupportLoss(support, rho * h, hz, false);
                var (gain, radLoss) = Radiate(hz, modal, set.RadiatingArea, 0.5, sigma, 4 * Math.PI);
                set.Add(hz, modal, loss + radLoss, gain * Sign(rng), shape);
            }
        }
        return set;
    }

    // ── Solid blocks: Rayleigh-Ritz ────────────────────────────────────────────────────────────────────

    /// <summary>The highest total polynomial degree of the Ritz basis: the first few dozen modes of a box to
    /// well under a per cent (Visscher et al. 1991 use 10-12).</summary>
    internal const int RitzOrder = 8;

    /// <summary>One solved box shape: dimensionless eigenvalues (w^2 rho a^2 / mu, a the first half-side) and the
    /// polynomial coefficients of each mode's displacement.</summary>
    internal sealed class Box
    {
        public readonly List<double> Eigen = new();
        public readonly List<(int Comp, int L, int M, int N, double C)[]> Vectors = new();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, int), Box> Boxes = new();

    /// <summary>The modes of a free box of half-sides 1, b/a, c/a and Poisson's ratio nu, solved once per shape
    /// (to a per cent of aspect and a hundredth of nu) and kept.</summary>
    internal static Box SolveBox(double bOverA, double cOverA, double nu)
    {
        var key = ((int)Math.Round(bOverA * 100), (int)Math.Round(cOverA * 100), (int)Math.Round(nu * 100));
        return Boxes.GetOrAdd(key, k => Ritz(k.Item1 / 100.0, k.Item2 / 100.0, k.Item3 / 100.0, RitzOrder));
    }

    /// <summary>Normalised Legendre polynomial n (orthonormal on [-1, 1]) and its derivative.</summary>
    internal static (double P, double D) Legendre(int n, double x)
    {
        double p0 = 1, p1 = x, d0 = 0, d1 = 1;
        if (n == 0) return (Math.Sqrt(0.5), 0);
        for (int k = 2; k <= n; k++)
        {
            double p2 = ((2 * k - 1) * x * p1 - (k - 1) * p0) / k;
            double d2 = d0 + (2 * k - 1) * p1;   // P'_k = P'_{k-2} + (2k - 1) P_{k-1}
            p0 = p1; p1 = p2; d0 = d1; d1 = d2;
        }
        double s = Math.Sqrt((2 * n + 1) / 2.0);
        return (p1 * s, d1 * s);
    }

    internal static Box Ritz(double bb, double cc, double nu, int order)
    {
        // Lame constants with mu = 1: lambda = 2 nu / (1 - 2 nu).
        double lambda = 2 * nu / (1 - 2 * nu), mu = 1;
        double[] h = { 1, bb, cc };
        // 1D integrals by Gauss-Legendre, exact for these degrees.
        int q = order + 2;
        var (gx, gw) = GaussLegendre(q);
        int n1 = order + 1;
        var dd = new double[n1, n1]; var ed = new double[n1, n1];
        for (int i = 0; i < n1; i++)
            for (int j = 0; j < n1; j++)
            {
                double sd = 0, se = 0;
                for (int k = 0; k < q; k++)
                {
                    var (pi, di) = Legendre(i, gx[k]); var (pj, dj) = Legendre(j, gx[k]);
                    sd += gw[k] * di * dj; se += gw[k] * di * pj;
                }
                dd[i, j] = sd; ed[i, j] = se;
            }
        var box = new Box();
        var all = new List<(double Eig, (int, int, int, int, double)[] Vec)>();
        for (int cls = 0; cls < 8; cls++)
        {
            int[] bits = { cls & 1, (cls >> 1) & 1, (cls >> 2) & 1 };
            var basis = new List<(int Comp, int[] Lmn)>();
            for (int comp = 0; comp < 3; comp++)
                for (int l = 0; l <= order; l++)
                    for (int mm = 0; mm + l <= order; mm++)
                        for (int nn = 0; nn + mm + l <= order; nn++)
                        {
                            int[] lmn = { l, mm, nn };
                            bool ok = true;
                            for (int axis = 0; axis < 3 && ok; axis++)
                            {
                                bool odd = (comp == axis) ^ (bits[axis] == 1);
                                if ((lmn[axis] % 2 == 1) != odd) ok = false;
                            }
                            if (ok) basis.Add((comp, lmn));
                        }
            int size = basis.Count;
            if (size == 0) continue;
            var k = new double[size, size];
            for (int x = 0; x < size; x++)
                for (int y = x; y < size; y++)
                {
                    var (ci, la) = basis[x]; var (ck, lb) = basis[y];
                    double T(int j1, int j2)
                    {
                        double v = h[0] * h[1] * h[2] / (h[j1] * h[j2]);
                        for (int t = 0; t < 3; t++)
                        {
                            if (t == j1 && t == j2) v *= dd[la[t], lb[t]];
                            else if (t == j1) v *= ed[la[t], lb[t]];
                            else if (t == j2) v *= ed[lb[t], la[t]];
                            else if (la[t] != lb[t]) return 0;
                        }
                        return v;
                    }
                    double val = lambda * T(ci, ck) + mu * T(ck, ci);
                    if (ci == ck) val += mu * (T(0, 0) + T(1, 1) + T(2, 2));
                    k[x, y] = val; k[y, x] = val;
                }
            // Mass is rho a b c times the identity in this basis; with rho = 1 and half-sides h, divide by that.
            double massScale = h[0] * h[1] * h[2];
            var (vals, vecs) = Jacobi(k);
            for (int i = 0; i < size; i++)
            {
                double eig = vals[i] / massScale;
                if (eig < 1e-6) continue;   // a rigid motion
                var vec = new (int, int, int, int, double)[size];
                for (int x = 0; x < size; x++) vec[x] = (basis[x].Comp, basis[x].Lmn[0], basis[x].Lmn[1], basis[x].Lmn[2], vecs[x, i]);
                all.Add((eig, vec));
            }
        }
        all.Sort((x, y) => x.Eig.CompareTo(y.Eig));
        // The upper half of a Ritz spectrum has not converged: keep the lower part.
        int keep = Math.Min(all.Count, 60);
        for (int i = 0; i < keep; i++) { box.Eigen.Add(all[i].Eig); box.Vectors.Add(all[i].Vec); }
        return box;
    }

    /// <summary>Displacement of a Ritz mode at a point of the reference cube [-1, 1]^3.</summary>
    internal static (double X, double Y, double Z) BoxDisplacement((int Comp, int L, int M, int N, double C)[] vec, double x, double y, double z)
    {
        double ux = 0, uy = 0, uz = 0;
        foreach (var (comp, l, mm, nn, c) in vec)
        {
            if (Math.Abs(c) < 1e-12) continue;
            double v = c * Legendre(l, x).P * Legendre(mm, y).P * Legendre(nn, z).P;
            if (comp == 0) ux += v; else if (comp == 1) uy += v; else uz += v;
        }
        return (ux, uy, uz);
    }

    /// <summary>
    /// A solid box, Length x Width x Thickness, struck on its Length x Width face. Every Ritz mode's note is
    /// sqrt(eig mu / rho) / a with a the half-length; its modal mass rho V / 8 for the normalised shape; its
    /// radiation from the mean square of the normal motion over the whole surface, with a compact body's
    /// efficiency (k L)^2 / (1 + (k L)^2) (EST: unity for anything metal, stone or glass, where kL at the first
    /// mode is about forty whatever the size).
    /// </summary>
    internal static Set Block(MaterialProperties m, double length, double width, double thick, StruckSupport support,
                              IReadOnlyList<(double U, double V)> points, int seed)
    {
        var set = new Set();
        double rho = Math.Max(1, m.DensityKgM3), e = m.YoungsModulusGPa * 1e9, nu = Math.Clamp(m.Poisson, 0.05, 0.45);
        double mu = e / (2 * (1 + nu));
        double a = length / 2, b = width / 2, c = thick / 2;
        var box = SolveBox(b / a, c / a, nu);
        double vol = length * width * thick;
        set.TotalMass = rho * vol; set.Volume = vol;
        double surface = 2 * (length * width + length * thick + width * thick);
        set.RadiatingArea = surface;
        double modal = rho * a * b * c;   // rho V / 8 for a unit coefficient vector
        double size = Math.Cbrt(vol);
        var rng = new Random(seed);
        for (int i = 0; i < box.Eigen.Count; i++)
        {
            double hz = Math.Sqrt(box.Eigen[i] * mu / rho) / a / (2 * Math.PI);
            if (hz > MaxHz) break;
            var vec = box.Vectors[i];
            var shape = new double[points.Count];
            for (int k = 0; k < points.Count; k++)
                shape[k] = BoxDisplacement(vec, 2 * points[k].U - 1, 2 * points[k].V - 1, 1).Z;
            double meanSq = BoxSurfaceMeanSquare(vec, b / a, c / a);
            double kl = 2 * Math.PI * hz / C0 * size;
            double sigma = kl * kl / (1 + kl * kl);
            double loss = m.LossAt((float)hz) + SupportLoss(support, rho * thick, hz, false);
            var (gain, radLoss) = Radiate(hz, modal, surface, meanSq, sigma, 4 * Math.PI);
            set.Add(hz, modal, loss + radLoss, gain * Sign(rng), shape);
        }
        return set;
    }

    /// <summary>The mean square of a Ritz mode's normal displacement over the box's six faces, area-weighted.</summary>
    private static double BoxSurfaceMeanSquare((int Comp, int L, int M, int N, double C)[] vec, double bb, double cc)
    {
        const int g = 6;
        var (gx, gw) = GaussLegendre(g);
        double sum = 0, area = 0;
        double[] h = { 1, bb, cc };
        for (int axis = 0; axis < 3; axis++)
        {
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            double faceArea = h[u] * h[v];
            for (int side = -1; side <= 1; side += 2)
                for (int i = 0; i < g; i++)
                    for (int j = 0; j < g; j++)
                    {
                        var p = new double[3];
                        p[axis] = side; p[u] = gx[i]; p[v] = gx[j];
                        var d = BoxDisplacement(vec, p[0], p[1], p[2]);
                        double n = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
                        double w = gw[i] * gw[j] / 4 * faceArea;
                        sum += w * n * n; area += w;
                    }
        }
        return sum / area;
    }

    // ── Numerics ──────────────────────────────────────────────────────────────────────────────────────

    internal static (double[] X, double[] W) GaussLegendre(int n)
    {
        var x = new double[n]; var w = new double[n];
        for (int i = 0; i < n; i++)
        {
            double z = Math.Cos(Math.PI * (i + 0.75) / (n + 0.5));
            for (int it = 0; it < 100; it++)
            {
                double p0 = 1, p1 = z;
                for (int k = 2; k <= n; k++) { double p2 = ((2 * k - 1) * z * p1 - (k - 1) * p0) / k; p0 = p1; p1 = p2; }
                double dp = n * (z * p1 - p0) / (z * z - 1);
                double dz = p1 / dp;
                z -= dz;
                if (Math.Abs(dz) < 1e-15) break;
            }
            double q0 = 1, q1 = z;
            for (int k = 2; k <= n; k++) { double q2 = ((2 * k - 1) * z * q1 - (k - 1) * q0) / k; q0 = q1; q1 = q2; }
            double d = n * (z * q1 - q0) / (z * z - 1);
            x[i] = z; w[i] = 2 / ((1 - z * z) * d * d);
        }
        return (x, w);
    }

    /// <summary>Cyclic Jacobi rotations of a symmetric matrix: its eigenvalues and eigenvectors (as columns).</summary>
    internal static (double[] Values, double[,] Vectors) Jacobi(double[,] input)
    {
        int n = input.GetLength(0);
        var a = (double[,])input.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++) v[i, i] = 1;
        for (int sweep = 0; sweep < 60; sweep++)
        {
            double off = 0, diag = 0;
            for (int i = 0; i < n; i++)
            {
                diag += a[i, i] * a[i, i];
                for (int j = i + 1; j < n; j++) off += a[i, j] * a[i, j];
            }
            if (off <= 1e-22 * diag) break;
            for (int p = 0; p < n - 1; p++)
                for (int q = p + 1; q < n; q++)
                {
                    double apq = a[p, q];
                    if (Math.Abs(apq) < 1e-300) continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * apq);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        var values = new double[n];
        for (int i = 0; i < n; i++) values[i] = a[i, i];
        return (values, v);
    }

    private static double Sign(Random rng) => rng.NextDouble() < 0.5 ? -1 : 1;
}
