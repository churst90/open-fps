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
/// How a free item of a declared shape rings (docs/PHYSICS_BOUNDARY.md 18.12): its first few modes, each
/// at the frequency its own law gives and with a T60 from the material's loss factor
/// (<see cref="PanelAcoustics.RingSeconds"/>, no mounting loss: a dropped thing is free). Every frequency
/// is a closed form from the shape, the material's modulus and density, and for a hollow shape its wall;
/// no note is a table entry by item name.
/// </summary>
public static class ItemModes
{
    /// <summary>The most modes carried to the synth: four partials is already a clang, and the fifth adds
    /// nothing but aliasing risk.</summary>
    public const int MaxModes = 4;

    /// <summary>Above this a mode is left out: what the ear calls the ring is below it, and a synth partial
    /// near the sampler's ceiling only folds back.</summary>
    public const float MaxHz = 12000f;

    /// <summary>One mode: its note, and seconds to fall 60 dB. The ratio to the first is the synth's.</summary>
    public readonly record struct Mode(float Hz, float Seconds);

    /// <summary>
    /// A free item's modes, lowest first, or empty when it declares no shape or its first mode is above
    /// hearing. <paramref name="massKg"/> matters only for a hollow shape whose wall is not declared: the
    /// wall is the one that makes the declared mass at the material's density.
    /// </summary>
    public static List<Mode> Free(MaterialProperties material, ItemShape shape, float massKg)
    {
        var modes = new List<Mode>(MaxModes);
        float rho = MathF.Max(1f, material.DensityKgM3);
        float e = material.YoungsModulusGPa * 1e9f;
        if (e <= 0f || !shape.IsDeclared) return modes;

        var (longSide, middle, shortSide) = Extents(shape.Size);
        switch (shape.Kind)
        {
            case ItemShapeKind.Bar:
                Beam(modes, material, longSide, shortSide > 0f ? shortSide : middle, middle, rho, e);
                break;

            case ItemShapeKind.Block:
                Beam(modes, material, longSide, shortSide > 0f ? shortSide : middle, middle, rho, e);
                break;

            case ItemShapeKind.Tube:
                Tube(modes, material, longSide, middle, shape.WallMetres, massKg, rho, e);
                break;

            case ItemShapeKind.Plate:
                Plate(modes, material, longSide, middle, shortSide);
                break;

            case ItemShapeKind.Shell:
                Shell(modes, material, longSide, middle, shape.WallMetres, massKg, rho, e);
                break;
        }
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

    /// <summary>
    /// Free-free Euler-Bernoulli bending, f_n = (beta_n L)^2 / (2 pi L^2) sqrt(E I / (rho A)) = (beta_n L)^2 /
    /// (2 pi L^2) r sqrt(E / rho) with r the section's radius of gyration. The roots beta_n L are 4.7300,
    /// 7.8532, 10.9956, 14.1372 (Rayleigh, The Theory of Sound, Vol. I, 2nd ed., section 170; Blevins, Formulas
    /// for Natural Frequency and Mode Shape, 1979, Table 8-1). The thinnest direction bends first, so the
    /// section is width by that thickness.
    /// </summary>
    private static void Beam(List<Mode> modes, MaterialProperties material, float length, float thickness, float width,
                             float rho, float e)
    {
        if (length <= 0f || thickness <= 0f || width <= 0f) return;
        float area = width * thickness;
        float inertia = width * thickness * thickness * thickness / 12f;
        BeamRadii(modes, material, length, MathF.Sqrt(inertia / area), rho, e);
    }

    /// <summary>The free-free beam roots beta_n L (Rayleigh I section 170; Blevins 1979 table 8-1).</summary>
    private static readonly double[] FreeRoots = { 4.73004074, 7.85320462, 10.9956078, 14.1371655 };

    /// <summary>
    /// A tube's whole body bends as the same free-free beam, with I = pi (Ro^4 - Ri^4) / 4 and A = pi (Ro^2 -
    /// Ri^2). A wall nobody declared is the one the declared mass makes at the material's density.
    /// </summary>
    private static void Tube(List<Mode> modes, MaterialProperties material, float length, float diameter,
                             float wall, float massKg, float rho, float e)
    {
        if (length <= 0f || diameter <= 0f) return;
        float ro = diameter / 2f;
        float ri = InnerRadius(ro, wall, massKg, rho, length);
        float area = MathF.PI * (ro * ro - ri * ri);
        if (area <= 0f) return;
        float inertia = MathF.PI * (MathF.Pow(ro, 4) - MathF.Pow(ri, 4)) / 4f;
        float radius = MathF.Sqrt(inertia / area);
        BeamRadii(modes, material, length, radius, rho, e);
    }

    /// <summary>
    /// A thin cylindrical shell: the breathing mode f = c_p / (2 pi a), c_p = sqrt(E / (rho (1 - nu^2))), and the
    /// inextensional bending modes f_n = (1 / (2 pi a^2)) sqrt(D / (rho h)) n (n^2 - 1) / sqrt(n^2 + 1), D = E h^3 /
    /// (12 (1 - nu^2)) (Rayleigh, The Theory of Sound, Vol. II, section 233; Love, A Treatise on the Mathematical
    /// Theory of Elasticity, 4th ed., section 285). n = 2 is the first that bends without moving the whole body.
    /// </summary>
    private static void Shell(List<Mode> modes, MaterialProperties material, float length, float diameter,
                              float wall, float massKg, float rho, float e)
    {
        if (length <= 0f || diameter <= 0f) return;
        float a = diameter / 2f;
        float h = Wall(a, wall, massKg, rho, length);
        float nu = material.Poisson;
        float plateSpeed = MathF.Sqrt(e / (rho * (1f - nu * nu)));
        var found = new List<float>(6);
        found.Add(plateSpeed / (MathF.Tau * a));
        float rigidity = e * MathF.Pow(h, 3) / (12f * (1f - nu * nu));
        float scale = MathF.Sqrt(rigidity / (rho * h)) / (MathF.Tau * a * a);
        for (int n = 2; n <= 6; n++)
            found.Add(scale * n * (n * n - 1) / MathF.Sqrt(n * n + 1));
        found.Sort();
        foreach (float hz in found)
        {
            Add(modes, material, hz);
            if (modes.Count >= MaxModes) return;
        }
    }

    /// <summary>
    /// The plate law <see cref="PanelAcoustics.RingHz"/> already uses: simply supported, f = 0.4755 t sqrt(E / rho)
    /// (m^2 / a^2 + n^2 / b^2) (Leissa, Vibration of Plates, NASA SP-160, 1969, ch. 4). Its lowest audible modes
    /// are taken in order.
    /// </summary>
    private static void Plate(List<Mode> modes, MaterialProperties material, float width, float height, float thickness)
    {
        if (width <= 0f || height <= 0f || thickness <= 0f) return;
        var plate = PanelAcoustics.Modes(material, width, height, thickness, MaxHz, order: 7);
        if (plate.Count == 0)
        {
            Add(modes, material, PanelAcoustics.RingHz(material, width, height, thickness));
            return;
        }
        foreach (var mode in plate)
        {
            Add(modes, material, mode.Hz);
            if (modes.Count >= MaxModes) return;
        }
    }

    private static void BeamRadii(List<Mode> modes, MaterialProperties material, float length, float radius,
                                  float rho, float e)
    {
        foreach (double root in FreeRoots)
        {
            double hz = root * root / (2 * Math.PI * length * length) * radius * MathF.Sqrt(e / rho);
            Add(modes, material, (float)hz);
            if (modes.Count >= MaxModes) return;
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

    private static void Add(List<Mode> modes, MaterialProperties material, float hz)
    {
        if (!(hz > 0f) || hz < PanelAcoustics.MinimumRingHz || hz > MaxHz) return;
        modes.Add(new Mode(hz, PanelAcoustics.RingSeconds(material, hz, mountingLoss: 0f)));
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
