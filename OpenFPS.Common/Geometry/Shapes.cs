using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// <summary>Arch: how many straight pieces the curve of the opening is made of (12 if not said).</summary>
    public int Segments { get; set; }

    public bool Equals(ShapeSpec? o) => o is not null && Kind == o.Kind && Steps == o.Steps && Landing.Equals(o.Landing)
                                        && Thickness.Equals(o.Thickness) && Segments == o.Segments;
    public override bool Equals(object? obj) => obj is ShapeSpec s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(Kind, Steps, Landing, Thickness, Segments);
    public override string ToString() => Kind switch
    {
        ShapeKind.Stairs => $"stairs, {Steps} steps" + (Landing > 0 ? $", a {Landing:0.##} m landing" : ""),
        ShapeKind.Arch => $"arch, {Thickness:0.##} m thick",
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

/// <summary>The shapes of stage 2, made from their numbers by the same code on the server and every client.</summary>
public static class Shapes
{
    /// <summary>Why a shape's numbers cannot make it, or null when they can.</summary>
    public static string? Problem(ShapeSpec? spec, Vector3 size)
    {
        if (spec == null || spec.Kind == ShapeKind.Box) return null;
        if (!(size.X > 0f && size.Y > 0f && size.Z > 0f)) return "a shape needs a collider size in all three directions";
        switch (spec.Kind)
        {
            case ShapeKind.Wedge: return null;
            case ShapeKind.Stairs:
                if (spec.Steps < 1 || spec.Steps > 200) return "stairs need between 1 and 200 steps";
                if (spec.Landing < 0f || spec.Landing >= size.Z) return "a landing must be shorter than the stairs";
                if (size.Y / spec.Steps > PhysicsConstants.StepHeight)
                    return $"each rise is {size.Y / spec.Steps:0.###} m, over the {PhysicsConstants.StepHeight} m a body can step";
                return null;
            case ShapeKind.Arch:
                if (spec.Thickness <= 0f) return "an arch needs a thickness";
                if (2f * spec.Thickness >= size.X || spec.Thickness >= size.Y) return "an arch thicker than itself has no opening";
                if (spec.Segments is < 0 or > 64) return "an arch's curve is 1 to 64 pieces";
                return null;
            default: return $"no shape called {spec.Kind}";
        }
    }

    private static readonly ConcurrentDictionary<(ShapeKind, int, int, int, int, int, int, int), ShapeMesh> Cache = new();

    /// <summary>
    /// The triangles of <paramref name="spec"/> filling a box of <paramref name="size"/> centred on its own
    /// origin, or null for a box (or a shape that cannot be made: see <see cref="Problem"/>). The same
    /// numbers give the same bits everywhere, and the same object while the process runs.
    /// </summary>
    public static ShapeMesh? Make(ShapeSpec? spec, Vector3 size)
    {
        if (spec == null || spec.Kind == ShapeKind.Box || Problem(spec, size) != null) return null;
        var key = (spec.Kind, spec.Steps, Bits(spec.Landing), Bits(spec.Thickness), spec.Segments, Bits(size.X), Bits(size.Y), Bits(size.Z));
        return Cache.GetOrAdd(key, _ => spec.Kind switch
        {
            ShapeKind.Wedge => Wedge(size),
            ShapeKind.Stairs => Stairs(size, spec.Steps, spec.Landing),
            ShapeKind.Arch => Arch(size, spec.Thickness, spec.Segments > 0 ? spec.Segments : 12),
            _ => throw new ArgumentOutOfRangeException(nameof(spec)),
        });
        static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
    }

    // ── Builders: every face wound counter-clockwise seen from outside ─────────────────────────────

    private sealed class Builder
    {
        public readonly List<Vector3> V = new();
        public readonly List<int> I = new();
        private int Add(Vector3 v) { V.Add(v); return V.Count - 1; }
        public void Tri(Vector3 a, Vector3 b, Vector3 c) { I.Add(Add(a)); I.Add(Add(b)); I.Add(Add(c)); }
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
            return new MeshAsset(verts.ToArray(), idx, new byte[idx.Length / 3], closed: true, convex);
        }
    }

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
        for (int i = 0; i < steps; i++)
        {
            float z0 = Z(i), z1 = Z(i + 1), top = Top(i), below = i == 0 ? -h.Y : Top(i - 1);
            parts[i] = BoxPart(new Vector3(-h.X, -h.Y, z0), new Vector3(h.X, top, z1));
            // The tread, the riser in front of it (from the tread below), and this column's piece of each side.
            b.Quad(new(-h.X, top, z0), new(-h.X, top, z1), new(h.X, top, z1), new(h.X, top, z0));
            b.Quad(new(-h.X, below, z0), new(-h.X, top, z0), new(h.X, top, z0), new(h.X, below, z0));
            b.Quad(new(-h.X, -h.Y, z0), new(-h.X, -h.Y, z1), new(-h.X, top, z1), new(-h.X, top, z0));
            b.Quad(new(h.X, -h.Y, z0), new(h.X, top, z0), new(h.X, top, z1), new(h.X, -h.Y, z1));
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
            double th = Math.PI * (1.0 - (double)k / segments);
            xs[k] = k == 0 ? -a : k == segments ? a : (float)(a * Math.Cos(th));
            ys[k] = k == 0 || k == segments ? -h.Y : -h.Y + (float)(rise * Math.Sin(th));
        }
        var b = new Builder();
        var parts = new List<MeshAsset>
        {
            BoxPart(new Vector3(-h.X, -h.Y, -h.Z), new Vector3(-a, h.Y, h.Z)),
            BoxPart(new Vector3(a, -h.Y, -h.Z), new Vector3(h.X, h.Y, h.Z)),
        };
        // The piers: front and back faces, the outer sides, their feet.
        foreach (var (x0, x1) in new[] { (-h.X, -a), (a, h.X) })
        {
            b.Quad(new(x0, -h.Y, -h.Z), new(x0, h.Y, -h.Z), new(x1, h.Y, -h.Z), new(x1, -h.Y, -h.Z));
            b.Quad(new(x0, -h.Y, h.Z), new(x1, -h.Y, h.Z), new(x1, h.Y, h.Z), new(x0, h.Y, h.Z));
            b.Quad(new(x0, -h.Y, -h.Z), new(x1, -h.Y, -h.Z), new(x1, -h.Y, h.Z), new(x0, -h.Y, h.Z));
        }
        b.Quad(new(-h.X, -h.Y, -h.Z), new(-h.X, -h.Y, h.Z), new(-h.X, h.Y, h.Z), new(-h.X, h.Y, -h.Z));   // -X side
        b.Quad(new(h.X, -h.Y, -h.Z), new(h.X, h.Y, -h.Z), new(h.X, h.Y, h.Z), new(h.X, -h.Y, h.Z));       // +X side
        b.Quad(new(-h.X, h.Y, -h.Z), new(-h.X, h.Y, h.Z), new(h.X, h.Y, h.Z), new(h.X, h.Y, -h.Z));       // top
        for (int k = 0; k < segments; k++)
        {
            float x0 = xs[k], x1 = xs[k + 1], y0 = ys[k], y1 = ys[k + 1];
            if (x1 - x0 < 1e-6f) continue;
            var lo = new Vector3(x0, y0, -h.Z);
            // The column over this piece of the curve: its front and back, and the curve's face under it.
            b.Quad(new(x0, y0, -h.Z), new(x0, h.Y, -h.Z), new(x1, h.Y, -h.Z), new(x1, y1, -h.Z));
            b.Quad(new(x0, y0, h.Z), new(x1, y1, h.Z), new(x1, h.Y, h.Z), new(x0, h.Y, h.Z));
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
