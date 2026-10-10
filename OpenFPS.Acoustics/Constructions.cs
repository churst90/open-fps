using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// Solids in contact are one construction: a floor is a slab, the ceiling under it and the carpet on it,
/// and sound crossing it crosses one layered panel (<see cref="WallTransmission.LayeredBandGains"/>), not
/// three barriers in a row. Two walls with air between them stay two.
///
/// <para>Two solids are layers of one construction where they are sheets (<see cref="IsSheet"/>) facing
/// the same way, their faces touch or overlap (within <see cref="ContactMetres"/>), and they share some
/// of their face. Fixed solids only: a door leaf is hung, not bonded, and what moves is not built in. The
/// rule is the same for a floor, a wall lined on both sides or a road on the ground: it reads materials
/// and geometry, never what a thing is called.</para>
///
/// <para>Along a line (the hand-rolled tracer, the legs of a route) the solids it crosses one straight
/// after another are grouped here (<see cref="Through"/>). The Steam Audio scene, which only multiplies
/// what each face it meets lets through, is given the construction's faces instead (<see cref="LayeredFaces"/>).</para>
/// </summary>
public static class Constructions
{
    /// <summary>Faces this close count as touching, metres: a map's numbers round to a millimetre.</summary>
    public const float ContactMetres = 0.002f;

    /// <summary>
    /// A layer is a sheet: no thicker than a quarter of its narrower side (a 35 cm wall a storey high is
    /// 0.13, a slab 0.01). A thicker solid is a block (a bed, 0.6 m on a 1.8 m base, or a sofa), crossed
    /// one face at a time: laid into the floor it stands on, its top would carry the floor's whole loss to
    /// a ray that only clips its corner.
    /// </summary>
    public const float SheetAspect = 0.25f;

    /// <summary>Whether two directions are the same line (sheets facing the same way).</summary>
    private const float ParallelCos = 0.9999f;

    /// <summary>Whether a box of this size is a sheet that can be a layer.</summary>
    public static bool IsSheet(Vector3 size)
    {
        WallTransmission.Faces(size, out float t, out float a, out float b);
        return t > 0f && t <= SheetAspect * MathF.Min(a, b);
    }

    /// <summary>The local axis a box's thickness runs along (its smallest side): 0, 1 or 2.</summary>
    public static int ThicknessAxis(Vector3 size)
    {
        float x = MathF.Abs(size.X), y = MathF.Abs(size.Y), z = MathF.Abs(size.Z);
        if (x <= y && x <= z) return 0;
        if (y <= x && y <= z) return 1;
        return 2;
    }

    /// <summary>A local axis turned into the world.</summary>
    public static Vector3 Axis(int axis, Quaternion rotation)
        => Vector3.Transform(axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ, rotation);

    /// <summary>The direction a box's thickness runs in the world.</summary>
    public static Vector3 Normal(Vector3 size, Quaternion rotation) => Axis(ThicknessAxis(size), rotation);

    /// <summary>Whether two sheets face the same way.</summary>
    public static bool Parallel(Vector3 a, Vector3 b) => MathF.Abs(Vector3.Dot(a, b)) > ParallelCos;

    /// <summary>One solid a line passes through.</summary>
    public struct Crossing
    {
        /// <summary>Where the line goes in and comes out, metres from its start (in at 0 if it starts inside).</summary>
        public float In, Out;
        public MaterialProperties Props;
        /// <summary>The panel crossed: its smallest side is its thickness, the other two its face.</summary>
        public Vector3 Panel;
        public WallBuild Build;
        /// <summary>What it lets through on its own, per band (WallTransmission.BandGains of the panel).</summary>
        public Vector3 Gains;
        /// <summary>How many times its own figure is paid: the walls of a hollow shell crossed.</summary>
        public int Times;
        /// <summary>Its thickness direction in the world if it can be a layer (a fixed sheet), else zero.</summary>
        public Vector3 Normal;
    }

    /// <summary>A crossing of a solid on its own: its gains worked out from its panel.</summary>
    public static Crossing Of(float tIn, float tOut, in MaterialProperties props, Vector3 panel, WallBuild build, int times, Vector3 normal)
    {
        var (l, m, h) = WallTransmission.BandGains(props, panel, build);
        return new Crossing { In = tIn, Out = tOut, Props = props, Panel = panel, Build = build, Gains = new Vector3(l, m, h), Times = times, Normal = normal };
    }

    /// <summary>
    /// What a line in direction <paramref name="dir"/> (unit) keeps after the solids it crosses, per band:
    /// each construction once, solids on their own each by their own figure. Sorts the list by where the
    /// line goes in.
    /// </summary>
    public static Vector3 Through(List<Crossing> crossings, Vector3 dir)
    {
        if (crossings.Count == 0) return Vector3.One;
        if (crossings.Count > 1) crossings.Sort(static (x, y) => x.In.CompareTo(y.In));
        var result = Vector3.One;
        int i = 0;
        List<WallTransmission.Layer>? layers = null;
        while (i < crossings.Count)
        {
            var first = crossings[i];
            int j = i + 1;
            if (first.Normal != Vector3.Zero && first.Times == 1)
            {
                // How far along the line a millimetre's gap between two faces is, crossing them at this angle.
                float slack = ContactMetres / MathF.Max(0.05f, MathF.Abs(Vector3.Dot(dir, first.Normal)));
                float reach = first.Out;
                while (j < crossings.Count)
                {
                    var next = crossings[j];
                    if (next.Normal == Vector3.Zero || next.Times != 1 || !Parallel(next.Normal, first.Normal)) break;
                    if (next.In > reach + slack) break;
                    reach = MathF.Max(reach, next.Out);
                    j++;
                }
            }
            if (j == i + 1)
            {
                for (int k = 0; k < first.Times; k++) result *= first.Gains;
                i = j;
                continue;
            }
            // One construction, its layers in the order crossed; what overlaps a layer before it is not
            // crossed twice. The heaviest layer's face sets the flanking.
            layers ??= new List<WallTransmission.Layer>(j - i);
            layers.Clear();
            float covered = float.NegativeInfinity, heaviest = -1f, faceA = 0f, faceB = 0f;
            for (int k = i; k < j; k++)
            {
                var c = crossings[k];
                WallTransmission.Faces(c.Panel, out float t, out float a, out float b);
                float chord = c.Out - c.In;
                float own = chord > 1e-6f ? MathF.Max(0f, c.Out - MathF.Max(c.In, covered)) / chord : 1f;
                covered = MathF.Max(covered, c.Out);
                layers.Add(new WallTransmission.Layer(c.Props, t * own, c.Build));
                float mass = c.Props.DensityKgM3 * t;
                if (mass > heaviest) { heaviest = mass; faceA = a; faceB = b; }
            }
            var (gl, gm, gh) = WallTransmission.LayeredBandGains(layers, faceA, faceB);
            result *= new Vector3(gl, gm, gh);
            i = j;
        }
        return result;
    }
}
