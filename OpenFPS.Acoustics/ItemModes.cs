using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Materials;

namespace OpenFPS.Acoustics;

/// <summary>
/// The shape an item declares (docs/MATTER.md 7.2's struck shapes, carried by the item rather than by its
/// prefab name): a compact block, a solid bar or rod, a tube or pipe, a thin sheet, or a cylindrical shell.
/// Append only: the value is on the wire.
/// </summary>
public enum ItemShapeKind : byte
{
    /// <summary>Nothing declared: the item falls and rings as the cube of its mass at its material's density.</summary>
    Unspecified = 0,
    /// <summary>A compact solid: a box, a brick, a lump.</summary>
    Block,
    /// <summary>A solid bar or rod: length and a rectangular cross-section.</summary>
    Bar,
    /// <summary>A tube or pipe: length, outer diameter and wall.</summary>
    Tube,
    /// <summary>A thin sheet or plate: width, height and thickness.</summary>
    Plate,
    /// <summary>A closed cylindrical shell: a bottle, a can, a jar. Length, outer diameter and wall.</summary>
    Shell,
}

/// <summary>
/// An item's declared shape and its full outer extents, metres. The extents are the item's box in its own
/// axes; each law below sorts them, so a bar is as long as its longest side whatever way it was authored.
/// <see cref="WallMetres"/> is a tube's or a shell's wall; zero means "work it out from the declared mass
/// and the material's density", as a hollow thing whose wall nobody measured.
/// </summary>
public readonly record struct ItemShape(ItemShapeKind Kind, Vector3 Size, float WallMetres = 0f)
{
    /// <summary>A shape that was actually declared, with three positive extents.</summary>
    public bool IsDeclared => Kind != ItemShapeKind.Unspecified
                              && Size.X > 0f && Size.Y > 0f && Size.Z > 0f;

    /// <summary>The half-width of the part of the shape that meets a floor: the smallest outer dimension's
    /// half, because a face meets its ground on its edges, not over its whole area.</summary>
    public float ContactRadiusMetres
        => Math.Clamp(0.5f * MathF.Min(Size.X, MathF.Min(Size.Y, Size.Z)), 0.0005f, 0.5f);

    /// <summary>Today's fallback, the cube of the item's mass at its material's density.</summary>
    public static ItemShape Cube(float side) => new(ItemShapeKind.Block, new Vector3(side));
}

/// <summary>
/// How a free item of a declared shape resonates (docs/PHYSICS_BOUNDARY.md 18.12-18.13): its first dozen modes,
/// each at the frequency its own law gives, with its own T60, its own radiated level and its own share of the
/// impact's energy. Every frequency, level and length is a closed form from the shape, the materials, the Hertz
/// contact and the material's losses; no note is a table entry by item name. The resonance is not a bell's chord:
/// a slender bar in bending is a line dipole and its lowest mode is far below its kHz modes in level, the blow's
/// half-sine spectrum colours which modes it excites, and the surface it lands on takes the vibration away.
/// </summary>
public static class ItemModes
{
    /// <summary>The most modes carried to the synth: a dozen is a coloured impact, and the render's cost is
    /// bounded by it.</summary>
    public const int MaxModes = 12;

    /// <summary>Above this a mode is left out: what the ear calls the resonance is below it, and a synth
    /// partial near the sampler's ceiling only folds back.</summary>
    public const float MaxHz = 12000f;

    private static readonly float AirDensity = Air.Reference.Density;
    private static readonly float AirSpeed = AcousticConstants.SpeedOfSound;

    /// <summary>One mode: its note, seconds to fall 60 dB, its level relative to the strongest mode, its total
    /// loss factor and the share of the impact's energy it radiates over its decay.</summary>
    public readonly record struct Mode(float Hz, float Seconds, float Gain, float Loss, float RadiatedShare);

    /// <summary>One geometric mode before the materials, contact and impact decide its level and length.</summary>
    private readonly record struct Geometry(float Hz, float Sigma, float Area, float ModalMass, float Shape);

    /// <summary>An item's free resonance in nothing: no support takes it and no contact colours it, for the
    /// material and shape laws alone.</summary>
    public static List<Mode> Free(MaterialProperties material, ItemShape shape, float massKg, int seed = 0)
        => Free(material, shape, massKg, default, 0f, massKg, seed);

    /// <summary>
    /// A free item's modes, lowest first, or empty when it declares no shape or its first mode is above
    /// hearing. <paramref name="support"/> is what it lands on; <paramref name="contactSeconds"/> the Hertz
    /// contact's length; <paramref name="reducedMassKg"/> the pair's reduced mass; <paramref name="seed"/>
    /// gives the contact point and the section's irregularity, so two landings are not identical.
    /// </summary>
    public static List<Mode> Free(MaterialProperties material, ItemShape shape, float massKg,
                                  MaterialProperties support, float contactSeconds, float reducedMassKg, int seed = 0)
    {
        var geometry = new List<Geometry>(MaxModes);
        float rho = MathF.Max(1f, material.DensityKgM3);
        float e = material.YoungsModulusGPa * 1e9f;
        if (e <= 0f || !shape.IsDeclared) return new List<Mode>();

        var rng = new Random(seed);
        // A landed bar meets the floor near an end, a plate near a corner: its mode shapes there decide which
        // modes the blow excites, and they are not all alike (a centre blow misses the odd modes).
        float contactX = 0.04f + 0.12f * (float)rng.NextDouble();
        float contactY = 0.04f + 0.12f * (float)rng.NextDouble();

        var (longSide, middle, shortSide) = Extents(shape.Size);
        switch (shape.Kind)
        {
            case ItemShapeKind.Bar:
                Beam(geometry, longSide, middle, shortSide, massKg, rho, e, contactX);
                break;

            case ItemShapeKind.Block:
                Block(geometry, longSide, middle, shortSide, massKg, rho, e, contactX);
                break;

            case ItemShapeKind.Tube:
                Tube(geometry, longSide, middle, shape.WallMetres, massKg, rho, e, contactX);
                break;

            case ItemShapeKind.Plate:
                Plate(geometry, longSide, middle, shortSide, rho, e, contactX, contactY);
                break;

            case ItemShapeKind.Shell:
                Shell(geometry, material, longSide, middle, shape.WallMetres, massKg, rho, e);
                break;
        }

        var modes = new List<Mode>(geometry.Count);
        float strongest = 0f;
        foreach (var mode in geometry)
        {
            // A real section is not the ideal one: a small irregularity detunes a mode by a few parts in a
            // thousand and changes how strongly the contact meets it (Rayleigh, The Theory of Sound, I, sec. 68).
            float hz = mode.Hz * (1f + 0.004f * (float)(rng.NextDouble() * 2.0 - 1.0));
            if (!(hz > 0f) || hz < PanelAcoustics.MinimumRingHz || hz > MaxHz) continue;

            float supportLoss = SupportLoss(material, support, hz);
            float loss = MathF.Max(1e-5f, material.LossAt(hz) + supportLoss);
            float omega = MathF.Tau * hz;
            float farField = AirDensity * AirSpeed * MathF.Max(0f, mode.Sigma) * MathF.Max(0f, mode.Area)
                             / (omega * MathF.Max(1e-4f, mode.ModalMass));
            float total = loss + farField;
            float seconds = PanelAcoustics.RingSeconds(total, hz);

            // The blow's spectrum at this mode and the shape's motion where it landed set how much of the
            // impact's energy this mode gets (Impulse 1: F(f)/J is the half-sine's normalised spectrum).
            float force = ForceSpectrum(hz, contactSeconds);
            float vibration = force * force * mode.Shape * mode.Shape
                              * MathF.Max(0f, reducedMassKg) / MathF.Max(1e-4f, mode.ModalMass);
            float radiated = vibration * farField / total;

            float gain = force * MathF.Abs(mode.Shape) * omega
                         * MathF.Sqrt(MathF.Max(1e-8f, mode.Sigma) * MathF.Max(1e-8f, mode.Area))
                         / MathF.Max(1e-4f, mode.ModalMass)
                         * (1f + 0.08f * (float)(rng.NextDouble() * 2.0 - 1.0));
            strongest = MathF.Max(strongest, gain);
            modes.Add(new Mode(hz, seconds, gain, total, MathF.Max(0f, radiated)));
        }

        // Levels are relative; the impact's is applied once to the whole resonance (ImpactAcoustics.Dropped).
        if (strongest > 1e-12f)
            for (int i = 0; i < modes.Count; i++) modes[i] = modes[i] with { Gain = modes[i].Gain / strongest };
        return modes;
    }

    /// <summary>The three sides, longest first; a zero or negative side is no shape at all.</summary>
    private static (float Long, float Middle, float Short) Extents(Vector3 size)
    {
        float a = MathF.Abs(size.X), b = MathF.Abs(size.Y), c = MathF.Abs(size.Z);
        float longSide = MathF.Max(a, MathF.Max(b, c));
        float shortSide = MathF.Min(a, MathF.Min(b, c));
        float middle = a + b + c - longSide - shortSide;
        return (longSide, middle, shortSide);
    }

    // ── Bars, blocks and a tube's whole-body bending ───────────────────────────────────────────────

    /// <summary>
    /// Free-free Euler-Bernoulli bending, f_n = (beta_n L)^2 / (2 pi L^2) sqrt(E I / (rho A)) = (beta_n L)^2 /
    /// (2 pi L^2) r sqrt(E / rho) with r the section's radius of gyration. The roots beta_n L are 4.7300,
    /// 7.8532, 10.9956, 14.1372, 17.2788; past these (2n + 3) pi / 2 (Rayleigh, The Theory of Sound, Vol. I,
    /// 2nd ed., section 170; Blevins, Formulas for Natural Frequency and Mode Shape, 1979, Table 8-1). The
    /// thinnest direction bends first.
    /// </summary>
    private static void Beam(List<Geometry> modes, float length, float width, float thickness,
                             float massKg, float rho, float e, float contact)
    {
        if (length <= 0f || thickness <= 0f || width <= 0f) return;
        float radius = thickness / MathF.Sqrt(12f);
        // The two front and back faces of the section are the dipole; they are a thickness apart.
        BeamModes(modes, length, radius, thickness * 0.5f, 2f * length * width, 1f, massKg, rho, e, contact);
    }

    /// <summary>
    /// A compact block takes the same free-free bending law from its own longest side, its first modes; it is
    /// the fallback shape and has no closed-form first mode. A compact body radiates as a monopole whose
    /// efficiency is (k a)^2 / (1 + (k a)^2), a its size (Fahy, Sound and Structural Vibration, 2nd ed.,
    /// section 4.5; Morse and Ingard, Theoretical Acoustics, 1968, chapter 7).
    /// </summary>
    private static void Block(List<Geometry> modes, float length, float width, float thickness,
                              float massKg, float rho, float e, float contact)
    {
        if (length <= 0f || width <= 0f || thickness <= 0f) return;
        float radius = thickness / MathF.Sqrt(12f);
        float volume = length * width * thickness;
        float size = MathF.Cbrt(volume);
        float surface = 2f * (length * width + length * thickness + width * thickness);
        float modalMass = massKg;
        for (int n = 0; n < MaxModes; n++)
        {
            double root = FreeRoot(n);
            double hz = root * root / (2 * Math.PI * length * length) * radius * MathF.Sqrt(e / rho);
            if (hz > MaxHz) break;
            float sigma = Compact((float)hz, size);
            modes.Add(new Geometry((float)hz, sigma, surface, modalMass, NormalisedBeam(n, contact)));
        }
    }

    /// <summary>The free-free beam roots beta_n L (Rayleigh I section 170; Blevins 1979 table 8-1).</summary>
    private static readonly double[] FreeRoots = { 4.73004074, 7.85320462, 10.9956078, 14.1371655, 17.2787597 };

    private static double FreeRoot(int n) => n < FreeRoots.Length ? FreeRoots[n] : (2 * n + 3) * Math.PI / 2;

    /// <summary>The free-free beam's mode n at x/L (Rayleigh I section 170; Blevins 1979 table 8-1).</summary>
    private static double FreeBeam(int n, double xOverL)
    {
        double b = FreeRoot(n), x = b * Math.Clamp(xOverL, 0, 1);
        double s = (Math.Cosh(b) - Math.Cos(b)) / (Math.Sinh(b) - Math.Sin(b));
        return Math.Cosh(x) + Math.Cos(x) - s * (Math.Sinh(x) + Math.Sin(x));
    }

    /// <summary>The beam's mode n at x/L normalised to a mean square of one, so the modal mass is the whole
    /// mass and a mode's share of an impulse's energy is its shape there squared.</summary>
    private static float NormalisedBeam(int n, float xOverL)
    {
        const int steps = 128;
        double sum = 0, dx = 1.0 / steps;
        for (int i = 0; i <= steps; i++)
        {
            double s = FreeBeam(n, i * dx);
            double w = i == 0 || i == steps ? 1 : i % 2 == 0 ? 2 : 4;
            sum += w * s * s;
        }
        double rms = Math.Sqrt(sum * dx / 3);
        return (float)(FreeBeam(n, xOverL) / Math.Max(1e-9, rms));
    }

    private static void BeamModes(List<Geometry> modes, float length, float radius, float dipoleHalf,
                                  float area, float meanSquare, float modalMass, float rho, float e, float contact)
    {
        for (int n = 0; n < MaxModes; n++)
        {
            double root = FreeRoot(n);
            double hz = root * root / (2 * Math.PI * length * length) * radius * MathF.Sqrt(e / rho);
            if (hz > MaxHz) break;
            if (hz < PanelAcoustics.MinimumRingHz) continue;
            float sigma = Dipole((float)hz, dipoleHalf);
            modes.Add(new Geometry((float)hz, sigma, area * meanSquare, modalMass, NormalisedBeam(n, contact)));
        }
    }

    /// <summary>
    /// A tube's whole body bends as the same free-free beam, with I = pi (Ro^4 - Ri^4) / 4 and A = pi (Ro^2 -
    /// Ri^2); its wall rings in its circumferential modes n = 2, ... (Love; Rayleigh II section 233). A wall
    /// nobody declared is the one the declared mass makes at the material's density. A cylinder moving
    /// sideways is a line dipole (Fahy section 4.5); a ring mode is a multipole of order n, nearly silent
    /// until its circumference is n wavelengths round (Junger and Feit, Sound, Structures and Their
    /// Interaction, 2nd ed., chapter 7).
    /// </summary>
    private static void Tube(List<Geometry> modes, float length, float diameter, float wall,
                             float massKg, float rho, float e, float contact)
    {
        if (length <= 0f || diameter <= 0f) return;
        float ro = diameter / 2f;
        float ri = InnerRadius(ro, wall, massKg, rho, length);
        float area = MathF.PI * (ro * ro - ri * ri);
        if (area <= 0f) return;
        float inertia = MathF.PI * (MathF.Pow(ro, 4) - MathF.Pow(ri, 4)) / 4f;
        float radius = MathF.Sqrt(inertia / area);
        // A cylinder's transverse motion radiates as a line dipole: its surface moves as cos(theta) round it,
        // a mean square of a half, from the whole lateral surface.
        BeamModes(modes, length, radius, ro, MathF.PI * diameter * length, 0.5f, massKg, rho, e, contact);

        float h = MathF.Max(1e-4f, ro - ri);
        for (int n = 2; n <= 12; n++)
        {
            double hz = Ring((ro + ri) / 2f, h, rho, e, 0.3, n);
            if (hz > MaxHz) break;
            if (hz < PanelAcoustics.MinimumRingHz) continue;
            float kr = MathF.Tau * (float)hz / AirSpeed * ro;
            float sigma = 1f / (1f + MathF.Pow(n / MathF.Max(1e-6f, kr), 2f * n));
            // The ring's radial and tangential motion together: kinetic energy (1 + 1/n^2) of the radial's.
            float modal = rho * h * length * MathF.PI * (ro + ri) / 2f * (1f + 1f / (n * n));
            modes.Add(new Geometry((float)hz, sigma, MathF.PI * diameter * length * 0.5f, modal, 1f));
        }
    }

    /// <summary>Love's inextensional ring mode n: f = (1 / (2 pi a^2)) sqrt(D / (rho h)) n (n^2 - 1) /
    /// sqrt(n^2 + 1), D = E h^3 / (12 (1 - nu^2)) (Love, A Treatise on the Mathematical Theory of Elasticity,
    /// 4th ed., section 285; Rayleigh, The Theory of Sound, Vol. II, section 233).</summary>
    private static double Ring(double radius, double wall, double rho, double e, double nu, int n)
    {
        double rigidity = e * Math.Pow(wall, 3) / (12 * (1 - nu * nu));
        double scale = Math.Sqrt(rigidity / (rho * wall)) / (Math.Tau * radius * radius);
        return scale * n * (n * n - 1) / Math.Sqrt(n * n + 1);
    }

    // ── Plates and shells ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The plate law <see cref="PanelAcoustics.RingHz"/> already uses: simply supported, f = 0.4755 t sqrt(E /
    /// rho) (m^2 / a^2 + n^2 / b^2) (Leissa, Vibration of Plates, NASA SP-160, 1969, chapter 4). A point blow
    /// excites every m and n, so the first several are taken in order; a free plate below coincidence
    /// radiates from its edges only (Maidanik, J. Acoust. Soc. Am. 34 (1962) 809) and its two faces cancel
    /// towards a dipole until the edges are a wavelength apart (Fahy section 4.5).
    /// </summary>
    private static void Plate(List<Geometry> modes, float width, float height, float thickness,
                              float rho, float e, float contactU, float contactV)
    {
        if (width <= 0f || height <= 0f || thickness <= 0f) return;
        float a = MathF.Max(0.02f, width), b = MathF.Max(0.02f, height);
        float t = Math.Clamp(thickness, 0.0002f, 0.5f);
        float scale = 0.4755f * t * MathF.Sqrt(e / rho);
        float fc = WallTransmission.CriticalHz(e, rho, t);
        float modal = rho * t * a * b / 4f;
        float area = 2f * a * b;
        var found = new List<(float Hz, int M, int N, float Shape)>();

        for (int m = 1; m <= 20; m++)
            for (int n = 1; n <= 20; n++)
            {
                float hz = scale * (m * m / (a * a) + n * n / (b * b));
                if (hz < PanelAcoustics.MinimumRingHz) continue;
                if (hz > MaxHz) break;
                // Mean square one: sin^2 averages a half each way.
                float shape = 2f * MathF.Sin(m * MathF.PI * contactU) * MathF.Sin(n * MathF.PI * contactV);
                found.Add((hz, m, n, shape));
            }

        found.Sort((x, y) => x.Hz.CompareTo(y.Hz));
        foreach (var (hz, _, _, shape) in found)
        {
            if (modes.Count >= MaxModes) break;
            float sigma = PlateSigma(hz, fc, a, b) * Dipole(hz, MathF.Min(a, b) * 0.5f);
            modes.Add(new Geometry(hz, sigma, area, modal, shape));
        }
    }

    /// <summary>
    /// A thin cylindrical shell: the breathing mode f = c_p / (2 pi a), c_p = sqrt(E / (rho (1 - nu^2))), and the
    /// inextensional bending modes from <see cref="Ring"/> (Rayleigh II section 233; Love section 285). The
    /// breathing mode radiates as a compact source, (k a)^2 / (1 + (k a)^2); the n-th bending mode as a
    /// multipole of order n (Junger and Feit chapter 7).
    /// </summary>
    private static void Shell(List<Geometry> modes, MaterialProperties material, float length, float diameter,
                              float wall, float massKg, float rho, float e)
    {
        if (length <= 0f || diameter <= 0f) return;
        float a = diameter / 2f;
        float h = Wall(a, wall, massKg, rho, length);
        float nu = material.Poisson;
        float plateSpeed = MathF.Sqrt(e / (rho * (1f - nu * nu)));
        float area = MathF.PI * diameter * length;
        float breathing = plateSpeed / (MathF.Tau * a);
        if (breathing >= PanelAcoustics.MinimumRingHz && breathing <= MaxHz)
            modes.Add(new Geometry(breathing, Compact(breathing, a), area, massKg, 1f));

        for (int n = 2; n <= 12; n++)
        {
            double hz = Ring(a, h, rho, e, nu, n);
            if (hz > MaxHz) break;
            if (hz < PanelAcoustics.MinimumRingHz) continue;
            float kr = MathF.Tau * (float)hz / AirSpeed * a;
            float sigma = 1f / (1f + MathF.Pow(n / MathF.Max(1e-6f, kr), 2f * n));
            float modal = rho * h * length * MathF.PI * a * (1f + 1f / (n * n));
            modes.Add(new Geometry((float)hz, sigma, area * 0.5f, modal, 1f));
        }
    }

    /// <summary>A hollow shape's inner radius: its declared wall, else the wall the declared mass makes on
    /// its lateral surface at the material's density. Clamped to a thin section so the laws stay thin.</summary>
    private static float InnerRadius(float outer, float wall, float massKg, float rho, float length)
    {
        if (wall <= 0f)
        {
            float area = MathF.Max(1e-8f, massKg / (rho * MathF.Max(1e-4f, length)));
            float innerArea = MathF.Max(0f, MathF.PI * outer * outer - area);
            return MathF.Sqrt(innerArea / MathF.PI);
        }
        return MathF.Max(0f, outer - Math.Clamp(wall, 0.0002f, outer / 2f));
    }

    /// <summary>A shell's wall: declared, else the one the declared mass makes over its lateral surface,
    /// pi D L, at the material's density.</summary>
    private static float Wall(float radius, float wall, float massKg, float rho, float length)
    {
        if (wall > 0f) return Math.Clamp(wall, 0.0002f, radius / 2f);
        float area = MathF.Max(1e-8f, MathF.PI * 2f * radius * MathF.Max(1e-4f, length));
        return Math.Clamp(massKg / (rho * area), 0.0002f, radius / 2f);
    }

    // ── Radiation, excitation and the surface it lands on ──────────────────────────────────────────

    /// <summary>
    /// A compact dipole's radiation efficiency, (k a)^2 / (1 + (k a)^2), a the half-separation of the two
    /// faces that move opposite ways: a slender bar's faces, a tube's front and back, a free plate's two
    /// sides. It is nearly silent where ka is much below one (Fahy, Sound and Structural Vibration, 2nd ed.,
    /// section 4.5; Morse and Ingard, Theoretical Acoustics, 1968, chapter 7).
    /// </summary>
    private static float Dipole(float hz, float a)
    {
        float x = MathF.Tau * hz / AirSpeed * MathF.Max(1e-4f, a);
        return x * x / (1f + x * x);
    }

    /// <summary>A compact body's radiation: (k a)^2 / (1 + (k a)^2) (Fahy section 4.5).</summary>
    private static float Compact(float hz, float a) => Dipole(hz, a);

    /// <summary>
    /// A simply supported plate's radiation efficiency: Maidanik's edge and corner radiation below
    /// coincidence, about one above it (Maidanik, J. Acoust. Soc. Am. 34 (1962) 809; Leppington, Broadbent
    /// and Heron, J. Sound Vib. 61 (1978) 1).
    /// </summary>
    private static float PlateSigma(float f, float fc, float a, float b)
    {
        if (!(fc > 0f) || float.IsInfinity(fc)) return 1f;
        float lc = AirSpeed / fc, area = a * b, perimeter = 2f * (a + b);
        if (f >= fc)
        {
            float above = 1f / MathF.Sqrt(MathF.Max(1e-3f, 1f - fc / f));
            float near = MathF.Sqrt(a / lc) + MathF.Sqrt(b / lc);
            return MathF.Min(above, near);
        }
        float alpha = MathF.Sqrt(f / fc);
        float g1 = f < fc / 2f ? 4f / MathF.Pow(MathF.PI, 4f) * (1f - 2f * alpha * alpha)
                                  / (alpha * MathF.Sqrt(1f - alpha * alpha)) : 0f;
        float g2 = ((1f - alpha * alpha) * MathF.Log((1f + alpha) / (1f - alpha)) + 2f * alpha)
                   / (4f * MathF.PI * MathF.PI * MathF.Pow(1f - alpha * alpha, 1.5f));
        float sigma = lc * lc / area * g1 + perimeter * lc / area * g2;
        return Math.Clamp(sigma, 1e-5f, MathF.Sqrt(a / lc) + MathF.Sqrt(b / lc));
    }

    /// <summary>
    /// The normalised spectrum of a half-sine blow of length tau, |cos(pi f tau) / (1 - (2 f tau)^2)|: it is
    /// one at zero and falls as 1/f^2 above 1/tau, its nulls at f = 3/(2 tau), 5/(2 tau), ... (Harris and
    /// Crede, Shock and Vibration Handbook, 3rd ed., chapter 8; Thomson, Theory of Vibration with Applications).
    /// </summary>
    public static float ForceSpectrum(float hz, float contactSeconds)
    {
        if (!(contactSeconds > 0f)) return 1f;
        float x = hz * contactSeconds;
        float denominator = 1f - 4f * x * x;
        if (MathF.Abs(denominator) < 1e-3f) return MathF.PI / 4f;
        return MathF.Abs(MathF.Cos(MathF.PI * x) / denominator);
    }

    /// <summary>
    /// What the floor takes from a resting thing, as a loss factor: the contact puts the two materials in
    /// series, so the surface's own loss counts by the share of the joint's strain energy its compliance
    /// holds (the modal strain energy method, Ungar and Kerwin, J. Acoust. Soc. Am. 34 (1962) 954; support
    /// loss, Hao, Erbil and Ayazi, J. Microelectromech. Syst. 12 (2003) 328). Carpet takes most, concrete
    /// less, metal least, in the struck material's own numbers.
    /// </summary>
    public static float SupportLoss(MaterialProperties material, MaterialProperties support, float hz)
    {
        float item = MathF.Max(1e-4f, material.AcrossGrainGPa);
        float floor = MathF.Max(1e-4f, support.AcrossGrainGPa);
        float share = (1f / floor) / (1f / item + 1f / floor);
        return MathF.Max(0f, support.LossAt(hz)) * share;
    }

    // ── The contact ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How long two things are in contact, seconds: Hertz, t = 2.87 (m*^2 / (R E*^2 v))^1/5, with m* the reduced
    /// mass, 1/E* = (1 - nu1^2)/E1 + (1 - nu2^2)/E2 and v the closing speed (Johnson, Contact Mechanics, 1985,
    /// section 11.4; the same law as Hydrometeors.HertzSeconds). <paramref name="radiusM"/> is the contact's
    /// radius of curvature; <see cref="ItemShape.ContactRadiusMetres"/> is the face's half-thickness.
    /// </summary>
    public static float ContactSeconds(MaterialProperties a, MaterialProperties b, float massA, float massB,
                                       float radiusM, float closingSpeed)
    {
        float ea = MathF.Max(1e5f, a.YoungsModulusGPa * 1e9f);
        float eb = MathF.Max(1e5f, b.YoungsModulusGPa * 1e9f);
        float nua = a.Poisson, nub = b.Poisson;
        float eStar = 1f / ((1f - nua * nua) / ea + (1f - nub * nub) / eb);
        float ma = MathF.Max(0.001f, massA), mb = MathF.Max(0.001f, massB);
        float reduced = ma * mb / (ma + mb);
        float r = MathF.Max(0.0005f, radiusM);
        float v = MathF.Max(0.05f, closingSpeed);
        float tau = 2.87f * MathF.Pow(reduced * reduced / (r * eStar * eStar * v), 0.2f);
        return Math.Clamp(tau, 0.00005f, 0.05f);
    }

    /// <summary>The corner a contact this long has nothing above, Hz: a blow is only as bright as it is short
    /// (the same 1/tau as Footsteps.ImpactCornerHz).</summary>
    public static float ContactHz(float seconds) => Math.Clamp(1f / MathF.Max(1e-5f, seconds), 45f, 8000f);
}
