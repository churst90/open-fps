using System.Numerics;
using MemoryPack;

namespace OpenFPS.Common.Geometry;

/// <summary>Which shape of the library a solid is (docs/GEOMETRY.md 2.6). Append only: it is on the wire.</summary>
public enum ShapeKind : byte
{
    /// <summary>A box of the collider's size: every entity that names no shape.</summary>
    Box = 0,
    /// <summary>A ramp filling the collider's box: height 0 along its -Z edge, the full height along +Z.</summary>
    Wedge = 1,
    /// <summary>A flight of solid steps filling the collider's box, climbing toward +Z.</summary>
    Stairs = 2,
    /// <summary>A block with a half-elliptical opening through it along Z, springing from the ground.</summary>
    Arch = 3,
    /// <summary>An upright round column filling the box (an ellipse when X and Z differ): a column, a trunk, a tank.</summary>
    Cylinder = 4,
    /// <summary>An upright cone or frustum: its base the box's bottom, its top <see cref="ShapeSpec.Top"/> of the base.</summary>
    Cone = 5,
    /// <summary>A ball filling the box.</summary>
    Sphere = 6,
    /// <summary>The top half of a ball: a flat base at the bottom of the box, the crown at its top.</summary>
    Dome = 7,
    /// <summary>An outline (<see cref="ShapeSpec.Outline"/>, with any <see cref="ShapeSpec.Holes"/>) stood up the
    /// box's height: a footprint's floor, a walled yard, a mitred wall.</summary>
    Prism = 8,
    /// <summary>A roof over an outline, filling the box from its eaves (the bottom) to its ridge (the top):
    /// <see cref="ShapeSpec.Style"/> hip, gable, shed or flat. A hip is the outline's straight skeleton.</summary>
    Roof = 9,
    /// <summary>A profile (<see cref="ShapeSpec.Profile"/>) drawn along a path (<see cref="ShapeSpec.Path"/>):
    /// a kerb, a rail, a gutter, a moulding.</summary>
    Swept = 10,
    /// <summary>An imported mesh asset (<see cref="ShapeSpec.Mesh"/>, docs/GEOMETRY.md 4.3) fitted to the box: a
    /// fountain, a statue, a church made in Blender. Its box until the asset has arrived.</summary>
    Mesh = 11,
}

/// <summary>What a roof's slopes do (<see cref="ShapeKind.Roof"/>). Append only: on the wire.</summary>
public enum RoofStyle : byte
{
    /// <summary>Every side slopes, at one pitch: the straight skeleton.</summary>
    Hip = 0,
    /// <summary>A hip whose triangular ends stand up as walls.</summary>
    Gable = 1,
    /// <summary>One slope, rising toward the box's +Z side.</summary>
    Shed = 2,
    /// <summary>Level: the outline the box's height thick.</summary>
    Flat = 3,
}

/// <summary>
/// A shape and its numbers (docs/GEOMETRY.md 4.1): what a map or a prefab says in "Shape", and what a
/// definition carries to a client inside its collider. The shape fills the collider's box, so
/// <c>ColliderSize</c> stays the bounding box every box reader sees; the numbers here are only what the
/// size does not say. Fields append only (positional on the wire).
/// </summary>
[MemoryPackable]
public sealed partial class ShapeSpec : IEquatable<ShapeSpec>
{
    public ShapeKind Kind { get; set; }
    /// <summary>Stairs: how many steps. The rise of each is the height over this, the going the length
    /// (less any landing) over this.</summary>
    public int Steps { get; set; }
    /// <summary>Stairs: a flat landing at the top, metres along Z, part of the box.</summary>
    public float Landing { get; set; }
    /// <summary>Arch: how thick the ring over the opening and the piers beside it are, metres.</summary>
    public float Thickness { get; set; }
    /// <summary>Arch: how many straight pieces the curve of the opening is made of (12 if not said). Round
    /// shapes: how many sides (0: from the size, as few as sound can tell apart).</summary>
    public int Segments { get; set; }
    /// <summary>Cone: the top's radius over the base's (0 a point, 1 a cylinder).</summary>
    public float Top { get; set; }
    /// <summary>Prism and roof: the outline, x and z pairs in metres about the shape's middle (x along the
    /// box's X, z along its Z). The shape is fitted to the box, so only its proportions bind.</summary>
    public float[]? Outline { get; set; }
    /// <summary>Prism: holes through it, each x and z pairs inside the outline, in the outline's metres.</summary>
    public float[][]? Holes { get; set; }
    /// <summary>Roof: hip, gable, shed or flat.</summary>
    public RoofStyle Style { get; set; }
    /// <summary>Swept: the profile, across and up pairs in metres about the path (across is to the right of
    /// the way the path goes, up is up).</summary>
    public float[]? Profile { get; set; }
    /// <summary>Swept: the path, x, y and z triples in metres. Fitted to the box with the profile.</summary>
    public float[]? Path { get; set; }
    /// <summary>A material for each of the shape's surfaces in turn (<see cref="Shapes.SurfaceNames"/>); an
    /// empty or missing one is the thing's own material.</summary>
    public string[]? Materials { get; set; }
    /// <summary>Mesh: the asset's id, sixteen hex digits (<see cref="MeshLibrary"/>).</summary>
    public string? Mesh { get; set; }

    public bool Equals(ShapeSpec? o) => o is not null && Kind == o.Kind && Steps == o.Steps && Landing.Equals(o.Landing)
                                        && Thickness.Equals(o.Thickness) && Segments == o.Segments && Top.Equals(o.Top)
                                        && Same(Outline, o.Outline) && SameHoles(Holes, o.Holes) && Style == o.Style
                                        && Same(Profile, o.Profile) && Same(Path, o.Path) && SameStrings(Materials, o.Materials)
                                        && string.Equals(Mesh ?? "", o.Mesh ?? "", StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => obj is ShapeSpec s && Equals(s);
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Kind); h.Add(Steps); h.Add(Landing); h.Add(Thickness); h.Add(Segments); h.Add(Top); h.Add(Style);
        h.Add((Mesh ?? "").ToLowerInvariant());
        foreach (var a in new[] { Outline, Profile, Path }) { h.Add(a?.Length ?? 0); if (a != null) foreach (float f in a) h.Add(f); }
        h.Add(Holes?.Length ?? 0);
        return h.ToHashCode();
    }

    private static bool Same(float[]? a, float[]? b) => (a?.Length ?? 0) == (b?.Length ?? 0) && (a == null || b == null || a.AsSpan().SequenceEqual(b));
    private static bool SameHoles(float[][]? a, float[][]? b)
    {
        if ((a?.Length ?? 0) != (b?.Length ?? 0)) return false;
        for (int i = 0; i < (a?.Length ?? 0); i++) if (!Same(a![i], b![i])) return false;
        return true;
    }
    private static bool SameStrings(string[]? a, string[]? b)
    {
        int n = Math.Max(a?.Length ?? 0, b?.Length ?? 0);
        for (int i = 0; i < n; i++)
        {
            string x = a != null && i < a.Length ? a[i] ?? "" : "", y = b != null && i < b.Length ? b[i] ?? "" : "";
            if (!string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>A deep copy: arrays included, so a copy can be changed without touching what it came from.</summary>
    public ShapeSpec Copy() => new()
    {
        Kind = Kind, Steps = Steps, Landing = Landing, Thickness = Thickness, Segments = Segments, Top = Top,
        Outline = (float[]?)Outline?.Clone(), Holes = Holes?.Select(h => (float[])h.Clone()).ToArray(), Style = Style,
        Profile = (float[]?)Profile?.Clone(), Path = (float[]?)Path?.Clone(), Materials = (string[]?)Materials?.Clone(), Mesh = Mesh,
    };

    public override string ToString() => Kind switch
    {
        ShapeKind.Stairs => $"stairs, {Steps} steps" + (Landing > 0 ? $", a {Landing:0.##} m landing" : ""),
        ShapeKind.Arch => $"arch, {Thickness:0.##} m thick",
        ShapeKind.Cylinder => "round column",
        ShapeKind.Cone => Top > 0 ? $"cone, its top {Top:0.##} of its base" : "cone",
        ShapeKind.Prism => $"prism, {(Outline?.Length ?? 0) / 2} corners" + (Holes is { Length: > 0 } ? $", {Holes.Length} holes" : ""),
        ShapeKind.Roof => $"{Style.ToString().ToLowerInvariant()} roof",
        ShapeKind.Swept => $"swept profile, {(Path?.Length ?? 0) / 3} points along",
        ShapeKind.Mesh => $"mesh {Mesh}",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// A shape made into triangles: its outer surface (what rays, sound and the ground meet) and, when it is
/// not convex, the convex pieces a body is met against (each a closed convex solid; together they fill
/// the shape exactly). A convex shape is its own one piece.
/// </summary>
public sealed class ShapeMesh
{
    public MeshAsset Outer { get; }
    /// <summary>Null when <see cref="Outer"/> is convex.</summary>
    public MeshAsset[]? Parts { get; }
    public ShapeMesh(MeshAsset outer, MeshAsset[]? parts) { Outer = outer; Parts = parts; }
}

/// <summary>The shape library (docs/GEOMETRY.md 2.6), made from its numbers by the same code on the server and
/// every client: stage 2's ramps, stairs and arches here, the round, outlined, roofed and swept shapes in
/// Shapes.More.cs.</summary>
public static partial class Shapes
{
    /// <summary>The highest riser a flight may have, metres: what a walking body steps up (the game's
    /// StepHeight, 0.4 m) unless a caller says otherwise.</summary>
    public const float DefaultMaxRise = 0.4f;

    /// <summary>Why a shape's numbers cannot make it, or null when they can. A flight whose risers are over
    /// <paramref name="maxRise"/> cannot be climbed, and is refused.</summary>
    public static string? Problem(ShapeSpec? spec, Vector3 size, float maxRise = DefaultMaxRise)
    {
        if (spec == null || spec.Kind == ShapeKind.Box) return null;
        if (!(size.X > 0f && size.Y > 0f && size.Z > 0f)) return "a shape needs a collider size in all three directions";
        switch (spec.Kind)
        {
            case ShapeKind.Wedge: return null;
            case ShapeKind.Stairs:
                if (spec.Steps < 1 || spec.Steps > 200) return "stairs need between 1 and 200 steps";
                if (spec.Landing < 0f || spec.Landing >= size.Z) return "a landing must be shorter than the stairs";
                if (size.Y / spec.Steps > maxRise)
                    return $"each rise is {size.Y / spec.Steps:0.###} m, over the {maxRise} m a body can step";
                return null;
            case ShapeKind.Arch:
                if (spec.Thickness <= 0f) return "an arch needs a thickness";
                if (2f * spec.Thickness >= size.X || spec.Thickness >= size.Y) return "an arch thicker than itself has no opening";
                if (spec.Segments is < 0 or > 64) return "an arch's curve is 1 to 64 pieces";
                return null;
            default: return MoreProblem(spec, size);
        }
    }

    /// <summary>
    /// The triangles of <paramref name="spec"/> filling a box of <paramref name="size"/> centred on its own
    /// origin, or null for a box (or a shape that cannot be made: see <see cref="Problem"/>). The same
    /// numbers give the same bits everywhere (the mesh's hash says so). Made each time it is asked: a flight
    /// of sixteen steps is a few hundred triangles, and only things that have a form ask.
    /// </summary>
    public static ShapeMesh? Make(ShapeSpec? spec, Vector3 size)
    {
        if (spec == null || spec.Kind == ShapeKind.Box) return null;
        // A mesh not here yet is its box until it is (and that is not kept).
        if (spec.Kind == ShapeKind.Mesh && !MeshLibrary.Shared.Contains(spec.Mesh ?? "")) return null;
        // Kept with the spec it was made from: a roof's skeleton is worth making once, and the geometry, the
        // acoustic store and the echoes each ask for the same thing's shape. A spec changed in place is made again.
        if (Made.TryGetValue(spec, out var had) && had.Size == size && had.Spec.Equals(spec)) return had.Mesh;
        var mesh = Problem(spec, size, float.MaxValue) != null ? null : MakeNew(spec, size);
        Made.AddOrUpdate(spec, new MadeShape(size, spec.Copy(), mesh));
        return mesh;
    }

    private sealed record MadeShape(Vector3 Size, ShapeSpec Spec, ShapeMesh? Mesh);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ShapeSpec, MadeShape> Made = new();

    private static ShapeMesh? MakeNew(ShapeSpec spec, Vector3 size)
    {
        return spec.Kind switch
        {
            ShapeKind.Wedge => Wedge(size),
            ShapeKind.Stairs => Stairs(size, spec.Steps, spec.Landing),
            ShapeKind.Arch => Arch(size, spec.Thickness, spec.Segments > 0 ? spec.Segments : 12),
            _ => MakeMore(spec, size),
        };
    }

    /// <summary>What each of a shape's surfaces is called, in the order <see cref="ShapeSpec.Materials"/> names
    /// their materials. Every triangle of a shape is on one of them.</summary>
    public static string[] SurfaceNames(ShapeKind kind) => kind switch
    {
        ShapeKind.Stairs => new[] { "body", "treads" },
        ShapeKind.Cylinder or ShapeKind.Cone => new[] { "side", "ends" },
        ShapeKind.Dome => new[] { "dome", "base" },
        ShapeKind.Prism => new[] { "sides", "top", "bottom" },
        ShapeKind.Roof => new[] { "slopes", "ends", "underside" },
        ShapeKind.Swept => new[] { "sides", "ends" },
        _ => new[] { "body" },
    };

    // ── Builders: every face wound counter-clockwise seen from outside ─────────────────────────────

    internal sealed class Builder
    {
        public readonly List<Vector3> V = new();
        public readonly List<int> I = new();
        public readonly List<byte> S = new();
        /// <summary>The surface the next triangles are on.</summary>
        public byte Slot;
        private int Add(Vector3 v) { V.Add(v); return V.Count - 1; }
        public void Tri(Vector3 a, Vector3 b, Vector3 c)
        {
            // A triangle with no area (a pole of a ball, a corner where a slope meets the eaves) is left out:
            // it has no plane to meet a body or a ray with.
            if (Vector3.Cross(b - a, c - a).LengthSquared() <= 1e-14f) return;
            I.Add(Add(a)); I.Add(Add(b)); I.Add(Add(c)); S.Add(Slot);
        }
        /// <summary>A planar quad a-b-c-d in order round its edge, counter-clockwise seen from outside.</summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { Tri(a, b, c); Tri(a, c, d); }
        public MeshAsset Make(bool convex)
        {
            // Corners shared by position, so a closed shape is closed by its indices too.
            var map = new Dictionary<Vector3, int>();
            var verts = new List<Vector3>();
            var idx = new int[I.Count];
            for (int k = 0; k < I.Count; k++)
            {
                var v = V[I[k]];
                if (!map.TryGetValue(v, out int at)) { at = verts.Count; verts.Add(v); map[v] = at; }
                idx[k] = at;
            }
            return new MeshAsset(verts.ToArray(), idx, S.ToArray(), closed: true, convex);
        }
    }

    /// <summary>A closed convex box from <paramref name="lo"/> to <paramref name="hi"/>: a convex piece.</summary>
    internal static MeshAsset BoxPiece(Vector3 lo, Vector3 hi) => BoxPart(lo, hi);

    /// <summary>A box from <paramref name="lo"/> to <paramref name="hi"/>, in a shape's frame.</summary>
    private static MeshAsset BoxPart(Vector3 lo, Vector3 hi)
    {
        var b = new Builder();
        Vector3 C(int x, int y, int z) => new(x == 0 ? lo.X : hi.X, y == 0 ? lo.Y : hi.Y, z == 0 ? lo.Z : hi.Z);
        b.Quad(C(0, 0, 0), C(0, 1, 0), C(1, 1, 0), C(1, 0, 0));   // -Z
        b.Quad(C(0, 0, 1), C(1, 0, 1), C(1, 1, 1), C(0, 1, 1));   // +Z
        b.Quad(C(0, 0, 0), C(0, 0, 1), C(0, 1, 1), C(0, 1, 0));   // -X
        b.Quad(C(1, 0, 0), C(1, 1, 0), C(1, 1, 1), C(1, 0, 1));   // +X
        b.Quad(C(0, 0, 0), C(1, 0, 0), C(1, 0, 1), C(0, 0, 1));   // -Y
        b.Quad(C(0, 1, 0), C(0, 1, 1), C(1, 1, 1), C(1, 1, 0));   // +Y
        return b.Make(convex: true);
    }

    /// <summary>A ramp: its foot along -Z at the bottom, its top along +Z at full height.</summary>
    private static ShapeMesh Wedge(Vector3 size)
    {
        var h = size * 0.5f;
        var b = new Builder();
        Vector3 b0 = new(-h.X, -h.Y, -h.Z), b1 = new(h.X, -h.Y, -h.Z), b2 = new(h.X, -h.Y, h.Z), b3 = new(-h.X, -h.Y, h.Z);
        Vector3 t2 = new(h.X, h.Y, h.Z), t3 = new(-h.X, h.Y, h.Z);
        b.Quad(b0, b1, b2, b3);   // bottom, facing down
        b.Quad(b3, b2, t2, t3);   // back, facing +Z
        b.Quad(b0, t3, t2, b1);   // the slope, facing up and toward -Z
        b.Tri(b0, b3, t3);        // -X side
        b.Tri(b1, t2, b2);        // +X side
        return new ShapeMesh(b.Make(convex: true), null);
    }

    /// <summary>
    /// Solid stairs: <paramref name="steps"/> treads climbing toward +Z, each rise the height over the
    /// steps, each going the length less the landing over the steps, the last tread running on as the
    /// landing. Each step is a column from the floor to its tread: the convex pieces a body meets.
    /// </summary>
    private static ShapeMesh Stairs(Vector3 size, int steps, float landing)
    {
        var h = size * 0.5f;
        float going = (size.Z - landing) / steps, rise = size.Y / steps;
        float Z(int i) => i >= steps ? h.Z : -h.Z + i * going;   // the front of step i; the back of the last is the box's end
        float Top(int i) => i == steps - 1 ? h.Y : -h.Y + (i + 1) * rise;
        var b = new Builder();
        var parts = new MeshAsset[steps];
        // Each side is one stepped polygon, so its edges are the treads' and risers' own and the solid is closed
        // edge for edge: (z, y) from the front foot along the bottom, up the back, and down the steps.
        var side = new List<Point2> { new(-h.Z, -h.Y), new(h.Z, -h.Y), new(h.Z, h.Y) };
        for (int i = steps - 1; i >= 0; i--)
        {
            float z0 = Z(i), top = Top(i), below = i == 0 ? -h.Y : Top(i - 1);
            side.Add(new Point2(z0, top));
            if (i > 0) side.Add(new Point2(z0, below));
        }
        for (int i = 0; i < steps; i++)
        {
            float z0 = Z(i), z1 = Z(i + 1), top = Top(i), below = i == 0 ? -h.Y : Top(i - 1);
            parts[i] = BoxPart(new Vector3(-h.X, -h.Y, z0), new Vector3(h.X, top, z1));
            // The tread and the riser in front of it (from the tread below).
            b.Slot = 1;
            b.Quad(new(-h.X, top, z0), new(-h.X, top, z1), new(h.X, top, z1), new(h.X, top, z0));
            b.Slot = 0;
            b.Quad(new(-h.X, below, z0), new(-h.X, top, z0), new(h.X, top, z0), new(h.X, below, z0));
        }
        // The polygon goes counter-clockwise in (z, y), which faces -X: turned over for the +X side.
        var tris = Polygons.Triangulate(side);
        for (int t = 0; t < tris.Count; t += 3)
        {
            Vector3 P(int k, float x) => new(x, (float)side[tris[t + k]].Y, (float)side[tris[t + k]].X);
            b.Tri(P(0, -h.X), P(1, -h.X), P(2, -h.X));
            b.Tri(P(0, h.X), P(2, h.X), P(1, h.X));
        }
        b.Quad(new(-h.X, -h.Y, h.Z), new(h.X, -h.Y, h.Z), new(h.X, h.Y, h.Z), new(-h.X, h.Y, h.Z));       // back
        b.Quad(new(-h.X, -h.Y, -h.Z), new(h.X, -h.Y, -h.Z), new(h.X, -h.Y, h.Z), new(-h.X, -h.Y, h.Z));   // bottom
        return new ShapeMesh(b.Make(convex: steps == 1), steps == 1 ? null : parts);
    }

    /// <summary>
    /// A block with a half-elliptical opening through it along Z: the opening springs from the ground
    /// <paramref name="thickness"/> in from each side and rises to <paramref name="thickness"/> under the
    /// top. The curve is <paramref name="segments"/> straight pieces; the convex pieces are the two piers
    /// and a column over each piece of the curve.
    /// </summary>
    private static ShapeMesh Arch(Vector3 size, float thickness, int segments)
    {
        var h = size * 0.5f;
        float a = h.X - thickness, rise = size.Y - thickness;
        var xs = new float[segments + 1]; var ys = new float[segments + 1];
        for (int k = 0; k <= segments; k++)
        {
            // From the left springing (-a, floor) over the crown to the right (a, floor), by angle, and the
            // ends exactly at the springings.
            var (sin, cos) = DetMath.SinCosOfTurn(segments - k, 2 * segments);
            xs[k] = k == 0 ? -a : k == segments ? a : (float)(a * cos);
            ys[k] = k == 0 || k == segments ? -h.Y : -h.Y + (float)(rise * sin);
        }
        var b = new Builder();
        var parts = new List<MeshAsset>
        {
            BoxPart(new Vector3(-h.X, -h.Y, -h.Z), new Vector3(-a, h.Y, h.Z)),
            BoxPart(new Vector3(a, -h.Y, -h.Z), new Vector3(h.X, h.Y, h.Z)),
        };
        // The piers' feet.
        foreach (var (x0, x1) in new[] { (-h.X, -a), (a, h.X) })
            b.Quad(new(x0, -h.Y, -h.Z), new(x1, -h.Y, -h.Z), new(x1, -h.Y, h.Z), new(x0, -h.Y, h.Z));
        b.Quad(new(-h.X, -h.Y, -h.Z), new(-h.X, -h.Y, h.Z), new(-h.X, h.Y, h.Z), new(-h.X, h.Y, -h.Z));   // -X side
        b.Quad(new(h.X, -h.Y, -h.Z), new(h.X, h.Y, -h.Z), new(h.X, h.Y, h.Z), new(h.X, -h.Y, h.Z));       // +X side
        b.Quad(new(-h.X, h.Y, -h.Z), new(-h.X, h.Y, h.Z), new(h.X, h.Y, h.Z), new(h.X, h.Y, -h.Z));       // top
        // The front and back, each one polygon in (x, y): along the bottom to the left springing, round the
        // curve, on to the right side, up and back along the top. Counter-clockwise faces +Z.
        var face = new List<Point2> { new(-h.X, -h.Y) };
        for (int k = 0; k <= segments; k++)
            if (k == 0 || k == segments || xs[k] - xs[k - 1] >= 1e-6f) face.Add(new Point2(xs[k], ys[k]));
        face.Add(new Point2(h.X, -h.Y)); face.Add(new Point2(h.X, h.Y)); face.Add(new Point2(-h.X, h.Y));
        var tris = Polygons.Triangulate(face);
        for (int t = 0; t < tris.Count; t += 3)
        {
            Vector3 P(int k, float z) => new((float)face[tris[t + k]].X, (float)face[tris[t + k]].Y, z);
            b.Tri(P(0, h.Z), P(1, h.Z), P(2, h.Z));
            b.Tri(P(0, -h.Z), P(2, -h.Z), P(1, -h.Z));
        }
        for (int k = 0; k < segments; k++)
        {
            float x0 = xs[k], x1 = xs[k + 1], y0 = ys[k], y1 = ys[k + 1];
            if (x1 - x0 < 1e-6f) continue;
            // The curve's face under the column over this piece of it.
            b.Quad(new(x0, y0, -h.Z), new(x1, y1, -h.Z), new(x1, y1, h.Z), new(x0, y0, h.Z));
            parts.Add(Column(x0, y0, x1, y1, h.Y, h.Z));
        }
        return new ShapeMesh(b.Make(convex: false), parts.ToArray());

        // A convex prism over the chord (x0, y0)-(x1, y1) up to the top, through the depth.
        static MeshAsset Column(float x0, float y0, float x1, float y1, float top, float hz)
        {
            var c = new Builder();
            c.Quad(new(x0, y0, -hz), new(x0, top, -hz), new(x1, top, -hz), new(x1, y1, -hz));
            c.Quad(new(x0, y0, hz), new(x1, y1, hz), new(x1, top, hz), new(x0, top, hz));
            c.Quad(new(x0, y0, -hz), new(x1, y1, -hz), new(x1, y1, hz), new(x0, y0, hz));
            c.Quad(new(x0, top, -hz), new(x0, top, hz), new(x1, top, hz), new(x1, top, -hz));
            c.Quad(new(x0, y0, -hz), new(x0, y0, hz), new(x0, top, hz), new(x0, top, -hz));
            c.Quad(new(x1, y1, -hz), new(x1, top, -hz), new(x1, top, hz), new(x1, y1, hz));
            return c.Make(convex: true);
        }
    }
}
