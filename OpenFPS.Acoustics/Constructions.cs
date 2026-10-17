using System.Numerics;
using OpenFPS.Geometry;

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
        /// <summary>How far the line runs inside it when it went in or came out by a narrow side and ran on
        /// further than its thickness (<see cref="RunInside"/>), metres; 0 when it was crossed face to face.
        /// <see cref="Gains"/> is then what that much of it lets through.</summary>
        public float Run;
        /// <summary>What it lets through crossed face to face, per band: <see cref="Gains"/> when
        /// <see cref="Run"/> is 0.</summary>
        public Vector3 AsPanel;
    }

    /// <summary>A crossing of a solid on its own: its gains worked out from its panel, or from what the line
    /// runs through of it (<paramref name="run"/>, <see cref="RunInside"/>).</summary>
    public static Crossing Of(float tIn, float tOut, in MaterialProperties props, Vector3 panel, WallBuild build, int times, Vector3 normal,
                              float run = 0f)
    {
        var (l, m, h) = WallTransmission.BandGains(props, panel, build);
        return Of(tIn, tOut, props, panel, build, times, normal, new Vector3(l, m, h), run);
    }

    /// <summary><see cref="Of(float, float, in MaterialProperties, Vector3, WallBuild, int, Vector3, float)"/>
    /// with the panel's figure already worked out (<paramref name="asPanel"/>).</summary>
    public static Crossing Of(float tIn, float tOut, in MaterialProperties props, Vector3 panel, WallBuild build, int times, Vector3 normal,
                              Vector3 asPanel, float run)
    {
        // A two-leaf build, or an assembly of several stuffs, is not one stuff: a line along it runs in its
        // cavity, and pays its panel.
        if (PanelsOnlyForLab || times != 1 || build.LeafMetres > 0f || build.Assembly != 0) run = 0f;
        return new Crossing
        {
            In = tIn, Out = tOut, Props = props, Panel = panel, Build = build, Times = times, Normal = normal,
            Run = run, AsPanel = asPanel, Gains = run > 0f ? Along(props, panel, build, run) : asPanel,
        };
    }

    /// <summary>Every solid one panel whatever way the line goes through it, as before 2026-10-11. For the
    /// lab's before-and-after only.</summary>
    public static volatile bool PanelsOnlyForLab;

    // ═══ A solid gone into by its edge ═══════════════════════════════════════════════════════════════
    //
    // A panel's loss (WallTransmission) is that of a plate driven on one broad face and radiating from the
    // other, bending, at field incidence: a line crossing it obliquely pays its thickness, not its chord. A
    // line that goes into a sheet by one of its narrow sides (a floor slab's edge in a facade, a wall's top)
    // and runs on in its plane has not crossed a panel. The side it meets does not move as a plate face, and
    // a sheet's in-plane motion radiates next to nothing; its bending does (Cremer, Heckl and Petersson,
    // Structure-Borne Sound, 3rd ed. 2005). So what gets through either crosses all the stuff on the line or
    // turns into bending where the sheet meets another, which costs more again: EN 12354-1's flanking path
    // is the two elements' mean loss plus the junction's vibration reduction index, at least 5.7 dB for a
    // rigid cross junction (Hopkins, Sound Insulation, 2007). Such a crossing is charged as the stuff the
    // line passes through: a panel as thick as its run inside, the sheet's own face for the flanking share,
    // and never less than the sheet crossed face to face. A block entered by any side is the same rule. A
    // two-leaf build (a stud wall, sealed glazing) or an assembly (a timber-framed house wall, Assemblies) is
    // not one stuff: a line along it runs in its cavity, and it keeps its panel figure.
    // Approximate: the run is a thin plate's mass law and coincidence carried to a thick block, which
    // overstates a thick wall's high band; and the flanking by the junctions a sheet is built into is no
    // path of its own (WallTransmission's flanking share stands for it).

    /// <summary>How far inside each face the inside of a box begins: a line in a face, or run along a
    /// joint, has touched the solid, not entered it, and pays its panel.</summary>
    private const float InsideMetres = ContactMetres;

    /// <summary>How near the whole thickness a crossing's depth must come to be face to face, metres: a
    /// float's rounding over a line tens of metres long.</summary>
    private const float FaceToFaceSlack = 5e-4f;

    /// <summary>
    /// How far the segment from <paramref name="a"/> to <paramref name="b"/> runs inside a box when it goes in
    /// or comes out by a side other than its two broad faces, metres; 0 when it crosses face to face, only
    /// touches it, or runs no further inside than the box is thick (a corner clipped). A box's thickness
    /// runs along its smallest side.
    /// </summary>
    public static float RunInside(Vector3 a, Vector3 b, Vector3 centre, Vector3 size, Quaternion rotation)
    {
        var q = rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        var inv = Quaternion.Inverse(q);
        return RunInsideLocal(Vector3.Transform(a - centre, inv), Vector3.Transform(b - centre, inv), size);
    }

    /// <summary><see cref="RunInside"/> with the segment's ends in the box's own frame (its centre at zero).</summary>
    public static float RunInsideLocal(Vector3 la, Vector3 lb, Vector3 size)
    {
        size = Vector3.Abs(size);
        // Inside at all: through the box shrunk by a hair, so a line in a face or along a joint is not.
        var inner = size * 0.5f - new Vector3(InsideMetres);
        if (inner.X <= 0f || inner.Y <= 0f || inner.Z <= 0f || !Clip(la, lb, inner, out _, out _)) return 0f;
        if (!Clip(la, lb, size * 0.5f, out float s0, out float s1)) return 0f;
        int k = ThicknessAxis(size);
        float t = Comp(size, k);
        var run = (lb - la) * (s1 - s0);
        // Face to face: it went the whole of the thickness.
        if (MathF.Abs(Comp(run, k)) >= t - FaceToFaceSlack) return 0f;
        float length = run.Length();
        return length > t ? length : 0f;
    }

    /// <summary>Where a segment runs inside a box of these half sides about the origin, as fractions of it.</summary>
    private static bool Clip(Vector3 la, Vector3 lb, Vector3 half, out float s0, out float s1)
    {
        var d = lb - la;
        s0 = 0f; s1 = 1f;
        for (int ax = 0; ax < 3; ax++)
        {
            float o = Comp(la, ax), dd = Comp(d, ax), hh = Comp(half, ax);
            if (MathF.Abs(dd) < 1e-9f)
            {
                if (o <= -hh || o >= hh) return false;
                continue;
            }
            float ta = (-hh - o) / dd, tb = (hh - o) / dd;
            if (ta > tb) (ta, tb) = (tb, ta);
            s0 = MathF.Max(s0, ta); s1 = MathF.Min(s1, tb);
            if (s0 >= s1) return false;
        }
        return true;
    }

    private static float Comp(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>A run's figures, kept to a centimetre of it and a decimetre of the face.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float, float, int, int, int, WallBuild), Vector3> _along = new();
    private const int AlongCacheLimit = 20_000;

    /// <summary>What <paramref name="run"/> metres of a sheet's stuff lets through, per band: a panel that
    /// thick with the sheet's own face (WallTransmission). A porous one keeps its table figures.</summary>
    public static Vector3 Along(in MaterialProperties props, Vector3 panel, WallBuild build, float run)
    {
        if (props.Porous || props.DensityKgM3 <= 0f)
            return new Vector3(props.TransmissionLow, props.TransmissionMid, props.TransmissionHigh);
        WallTransmission.Faces(panel, out _, out float a, out float b);
        int cm = Math.Max(1, (int)MathF.Round(run * 100f)), da = Math.Max(1, (int)MathF.Round(a * 10f)), db = Math.Max(1, (int)MathF.Round(b * 10f));
        var key = (props.DensityKgM3, props.YoungsModulusGPa, props.LossFactor, cm, da, db, build);
        if (_along.TryGetValue(key, out var g)) return g;
        var (l, m, h) = WallTransmission.PanelBandGains(props, cm / 100f, da / 10f, db / 10f, build);
        g = new Vector3(l, m, h);
        if (_along.Count > AlongCacheLimit) _along.Clear();
        _along[key] = g;
        return g;
    }

    /// <summary>
    /// What a line in direction <paramref name="dir"/> (unit) keeps after the solids it crosses, per band:
    /// each construction once, solids on their own each by their own figure. Sorts the list by where the
    /// line goes in.
    /// </summary>
    public static Vector3 Through(List<Crossing> crossings, Vector3 dir) => Through(crossings, dir, asPanels: false);

    /// <summary>
    /// <see cref="Through(List{Crossing}, Vector3)"/>, or with <paramref name="asPanels"/> every solid crossed
    /// face to face whatever way the line went: what a scene whose faces each carry their panel's figure
    /// gives (<see cref="InPlaneCorrection"/>).
    /// </summary>
    public static Vector3 Through(List<Crossing> crossings, Vector3 dir, bool asPanels)
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
                var own = asPanels ? first.AsPanel : first.Gains;
                for (int k = 0; k < first.Times; k++) result *= own;
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
                // A layer the line runs along is as thick as its run.
                float depth = !asPanels && c.Run > 0f ? c.Run : t;
                // An assembly's plies run along its normal: crossed the other way, they come in reverse.
                layers.Add(new WallTransmission.Layer(c.Props, depth * own, c.Build, Vector3.Dot(dir, c.Normal) < 0f));
                float mass = WallTransmission.MassPerArea(c.Props, t, c.Build);
                if (mass > heaviest) { heaviest = mass; faceA = a; faceB = b; }
            }
            var (gl, gm, gh) = WallTransmission.LayeredBandGains(layers, faceA, faceB);
            result *= new Vector3(gl, gm, gh);
            i = j;
        }
        return result;
    }

    /// <summary>
    /// What charging a line's solids by what it runs through of them (<see cref="Crossing.Run"/>) does to it,
    /// per band, against every solid crossed face to face: 1 where the line crossed them all face to face.
    /// Steam Audio's direct simulation only multiplies a figure per face its rays meet, and a face's figure
    /// cannot know how far a ray runs inside behind it, so its scene keeps each solid's panel figure on every
    /// face and the worker multiplies its answer by this (AsyncAcousticWorker).
    /// </summary>
    public static Vector3 InPlaneCorrection(List<Crossing> crossings, Vector3 dir)
    {
        bool any = false;
        foreach (var c in crossings) if (c.Run > 0f) { any = true; break; }
        if (!any) return Vector3.One;
        var panels = Through(crossings, dir, asPanels: true);
        var runs = Through(crossings, dir, asPanels: false);
        static float Ratio(float x, float y) => y > 1e-12f ? MathF.Min(1f, x / y) : 1f;
        return new Vector3(Ratio(runs.X, panels.X), Ratio(runs.Y, panels.Y), Ratio(runs.Z, panels.Z));
    }
}
