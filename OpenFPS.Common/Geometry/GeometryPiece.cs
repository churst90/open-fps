using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace OpenFPS.Common.Geometry;

/// <summary>Which faces a ray answers to: those it enters a solid by, those it leaves by, or both.</summary>
[Flags]
public enum RayFaces : byte { Front = 1, Back = 2, Both = 3 }

/// <summary>
/// Decides, per thing struck, whether a query counts it: the owning entity and the surface. A struct, so
/// each query is compiled for its own filter and asks nothing it does not need.
/// </summary>
public interface IGeometryFilter
{
    bool Accept(int owner, in Surface surface);
}

/// <summary>Counts everything the layers let through.</summary>
public struct AcceptAll : IGeometryFilter
{
    public readonly bool Accept(int owner, in Surface surface) => true;
}

/// <summary>Counts everything but up to two entities (a listener's own body, a source's own box).</summary>
public struct ExceptOwners : IGeometryFilter
{
    public int A, B;
    public ExceptOwners(int a, int b = int.MinValue) { A = a; B = b; }
    public readonly bool Accept(int owner, in Surface surface) => owner != A && owner != B;
}

/// <summary>
/// One solid that a piece holds: who owns it, what it is made of, where it is (in the piece's frame), its
/// triangles and, for a convex solid, the planes of its faces. The box it was made from is kept too, so
/// code not yet moved to triangles still sees a box of the right size (docs/GEOMETRY.md 4.1).
/// </summary>
public struct SolidRecord
{
    public int Owner;
    public int Surface;
    public Vector3 Min, Max;
    public int TriStart, TriCount;
    public int PlaneStart, PlaneCount;
    public bool Convex, Closed;
    /// <summary>The box this solid is, in the piece's frame: centre, size and turn. Size zero when the
    /// solid is not a box.</summary>
    public Vector3 BoxCentre, BoxSize;
    public Quaternion BoxRotation;
}

/// <summary>A triangle as the ray test wants it: one corner, the two edges from it, and its solid.</summary>
public struct GeometryTriangle
{
    public Vector3 V0, E1, E2;
    public int Solid;

    /// <summary>The face normal, outward for a solid, not normalised.</summary>
    public readonly Vector3 Normal => Vector3.Cross(E1, E2);
}

/// <summary>
/// What one solid is built from, before it is a piece (<see cref="GeometryPiece.Build"/>): its owner,
/// its mesh (null for a box of <paramref name="BoxSize"/>, which is every entity in stage 1), where it
/// stands and how it is turned, and its surface.
/// </summary>
public readonly record struct SolidSpec(int Owner, Vector3 Position, Quaternion Rotation, Vector3 BoxSize,
                                        Surface Surface, MeshAsset? Mesh = null);

/// <summary>
/// The bottom level of the two-level BVH (docs/GEOMETRY.md 2.2): an immutable set of solids in one frame,
/// with a tree over their triangles for rays and a tree over the solids themselves for "what is here" and
/// "what is near". A tile's static geometry is one piece in the tile's frame (its corner the origin, so a
/// float holds sub-millimetre detail anywhere in the world); a door leaf is a piece of its own, placed by
/// an instance that moves.
/// </summary>
public sealed class GeometryPiece
{
    public TileKey Key { get; }
    /// <summary>Where the piece's frame starts, in the world: its tile's corner, or zero.</summary>
    public Vector3 Origin { get; }
    /// <summary>A hash of everything the piece was built from: the same solids give the same signature.</summary>
    public ulong Signature { get; }

    internal readonly GeometryTriangle[] Tris;
    internal readonly ushort[] TriSurface;
    internal readonly BvhNode[] TriNodes;
    internal readonly SolidRecord[] Solids;
    internal readonly int[] SolidTris;
    internal readonly Vector4[] Planes;
    internal readonly BvhNode[] SolidNodes;
    internal readonly int[] SolidOrder;
    public Surface[] Surfaces { get; }

    public int TriangleCount => Tris.Length;
    public int SolidCount => Solids.Length;
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }
    /// <summary>What the piece holds in memory, bytes (arrays only).</summary>
    public long Bytes => (long)Tris.Length * 40 + TriSurface.Length * 2 + (long)TriNodes.Length * 32
                         + (long)Solids.Length * 112 + SolidTris.Length * 4 + Planes.Length * 16
                         + (long)SolidNodes.Length * 32 + SolidOrder.Length * 4;

    private GeometryPiece(TileKey key, Vector3 origin, ulong signature, GeometryTriangle[] tris, ushort[] triSurface, BvhNode[] triNodes,
                          SolidRecord[] solids, int[] solidTris, Vector4[] planes, BvhNode[] solidNodes, int[] solidOrder,
                          Surface[] surfaces)
    {
        Key = key; Origin = origin; Signature = signature;
        Tris = tris; TriSurface = triSurface; TriNodes = triNodes;
        Solids = solids; SolidTris = solidTris; Planes = planes;
        SolidNodes = solidNodes; SolidOrder = solidOrder; Surfaces = surfaces;
        if (solids.Length > 0) { BoundsMin = triNodes[0].Min; BoundsMax = triNodes[0].Max; }
    }

    public ref readonly SolidRecord Solid(int index) => ref Solids[index];
    public ref readonly GeometryTriangle Triangle(int index) => ref Tris[index];
    public ref readonly Surface SurfaceOfTriangle(int index) => ref Surfaces[TriSurface[index]];
    /// <summary>The triangles of one solid, as indices into the piece's triangles.</summary>
    public ReadOnlySpan<int> TrianglesOf(int solid) => new(SolidTris, Solids[solid].TriStart, Solids[solid].TriCount);
    /// <summary>The face planes of a convex solid (normal, offset; a point p is inside when n·p - d &lt; 0 for all).</summary>
    public ReadOnlySpan<Vector4> PlanesOf(int solid) => new(Planes, Solids[solid].PlaneStart, Solids[solid].PlaneCount);

    // ═══ Building ════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A piece from <paramref name="solids"/>, in the frame whose origin is <paramref name="origin"/>. The
    /// solids are taken in the order given: callers sort them (by owner) so that the server and a client
    /// holding the same things build the same piece.
    /// </summary>
    public static GeometryPiece Build(TileKey key, Vector3 origin, IReadOnlyList<SolidSpec> solids, ulong signature)
    {
        var surfaces = new List<Surface>();
        var surfaceIndex = new Dictionary<Surface, int>();
        int SurfaceIndex(in Surface s)
        {
            if (surfaceIndex.TryGetValue(s, out int i)) return i;
            i = surfaces.Count; surfaces.Add(s); surfaceIndex[s] = i;
            return i;
        }

        int triCount = 0;
        foreach (var s in solids) triCount += s.Mesh?.TriangleCount ?? 12;
        var tris = new GeometryTriangle[triCount];
        var triSurface = new ushort[triCount];
        var records = new SolidRecord[solids.Count];
        var planes = new List<Vector4>(solids.Count * 6);
        Span<Vector3> corners = stackalloc Vector3[8];
        int t = 0;
        for (int si = 0; si < solids.Count; si++)
        {
            var s = solids[si];
            var rotation = s.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : s.Rotation;
            int surface = SurfaceIndex(s.Surface);
            if (surface > ushort.MaxValue) throw new InvalidOperationException("more than 65,536 surfaces in one piece");
            ref var rec = ref records[si];
            rec.Owner = s.Owner; rec.Surface = surface; rec.TriStart = t;
            rec.Closed = s.Mesh?.Closed ?? true; rec.Convex = s.Mesh?.Convex ?? true;
            rec.BoxCentre = s.Position - origin; rec.BoxRotation = rotation;
            rec.BoxSize = s.Mesh == null ? s.BoxSize : Vector3.Zero;
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            if (s.Mesh == null)
            {
                for (int c = 0; c < 8; c++)
                {
                    corners[c] = ShapeLibrary.BoxCorner(c, s.Position, s.BoxSize, rotation, origin);
                    lo = Vector3.Min(lo, corners[c]); hi = Vector3.Max(hi, corners[c]);
                }
                var idx = ShapeLibrary.BoxTriangles;
                for (int k = 0; k < idx.Length; k += 3, t++)
                {
                    Vector3 a = corners[idx[k]], b = corners[idx[k + 1]], c = corners[idx[k + 2]];
                    tris[t] = new GeometryTriangle { V0 = a, E1 = b - a, E2 = c - a, Solid = si };
                    triSurface[t] = (ushort)surface;
                }
            }
            else
            {
                var m = s.Mesh;
                var at = s.Position - origin;
                var verts = new Vector3[m.Vertices.Length];
                for (int v = 0; v < verts.Length; v++)
                {
                    verts[v] = at + Vector3.Transform(m.Vertices[v], rotation);
                    lo = Vector3.Min(lo, verts[v]); hi = Vector3.Max(hi, verts[v]);
                }
                for (int k = 0; k < m.Indices.Length; k += 3, t++)
                {
                    Vector3 a = verts[m.Indices[k]], b = verts[m.Indices[k + 1]], c = verts[m.Indices[k + 2]];
                    tris[t] = new GeometryTriangle { V0 = a, E1 = b - a, E2 = c - a, Solid = si };
                    triSurface[t] = (ushort)surface;   // stage 1: one surface a solid; slots come with meshes
                }
            }
            rec.TriCount = t - rec.TriStart;
            rec.Min = lo; rec.Max = hi;
            rec.PlaneStart = planes.Count;
            if (rec.Convex && rec.Closed) AddPlanes(tris, rec.TriStart, rec.TriCount, planes);
            rec.PlaneCount = planes.Count - rec.PlaneStart;
        }

        // The tree over triangles, and the triangles put in its leaf order.
        var bmin = new Vector3[triCount]; var bmax = new Vector3[triCount];
        for (int i = 0; i < triCount; i++)
        {
            ref var tr = ref tris[i];
            Vector3 b = tr.V0 + tr.E1, c = tr.V0 + tr.E2;
            bmin[i] = Vector3.Min(tr.V0, Vector3.Min(b, c));
            bmax[i] = Vector3.Max(tr.V0, Vector3.Max(b, c));
        }
        var triNodes = BvhBuilder.Build(bmin, bmax, triCount, 4, out var order);
        var sortedTris = new GeometryTriangle[triCount];
        var sortedSurface = new ushort[triCount];
        var position = new int[triCount];
        for (int i = 0; i < triCount; i++) { sortedTris[i] = tris[order[i]]; sortedSurface[i] = triSurface[order[i]]; position[order[i]] = i; }
        // Each solid's triangles, as positions in the sorted list, still grouped by solid.
        var solidTris = new int[triCount];
        for (int i = 0; i < triCount; i++) solidTris[i] = position[i];

        // The tree over the solids.
        var smin = new Vector3[records.Length]; var smax = new Vector3[records.Length];
        for (int i = 0; i < records.Length; i++) { smin[i] = records[i].Min; smax[i] = records[i].Max; }
        var solidNodes = BvhBuilder.Build(smin, smax, records.Length, 2, out var solidOrder);

        return new GeometryPiece(key, origin, signature, sortedTris, sortedSurface, triNodes, records, solidTris,
                                 planes.ToArray(), solidNodes, solidOrder, surfaces.ToArray());
    }

    /// <summary>The distinct face planes of a convex solid, from its triangles.</summary>
    private static void AddPlanes(GeometryTriangle[] tris, int start, int count, List<Vector4> planes)
    {
        int first = planes.Count;
        for (int i = start; i < start + count; i++)
        {
            var n = tris[i].Normal;
            float len = n.Length();
            if (len < 1e-12f) continue;
            n /= len;
            float d = Vector3.Dot(n, tris[i].V0);
            bool seen = false;
            for (int p = first; p < planes.Count; p++)
            {
                var q = planes[p];
                if (Vector3.Dot(n, new Vector3(q.X, q.Y, q.Z)) > 0.99999f && MathF.Abs(d - q.W) < 1e-4f) { seen = true; break; }
            }
            if (!seen) planes.Add(new Vector4(n, d));
        }
    }

    // ═══ Queries, in the piece's own frame ═════════════════════════════════════════════════════

    /// <summary>Barycentric slack: a ray through the edge two triangles share hits one of them, never neither.</summary>
    private const float EdgeSlack = 1e-6f;

    /// <summary>Möller–Trumbore. <paramref name="front"/> is whether the ray enters the solid there (it
    /// meets the face from outside). Hits within [tMin, tMax].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HitTriangle(in GeometryTriangle tr, Vector3 o, Vector3 d, float tMin, float tMax, out float t, out bool front)
    {
        t = 0f; front = false;
        var p = Vector3.Cross(d, tr.E2);
        float det = Vector3.Dot(tr.E1, p);
        if (MathF.Abs(det) < 1e-14f) return false;
        float inv = 1f / det;
        var s = o - tr.V0;
        float u = Vector3.Dot(s, p) * inv;
        if (u < -EdgeSlack || u > 1f + EdgeSlack) return false;
        var q = Vector3.Cross(s, tr.E1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < -EdgeSlack || u + v > 1f + EdgeSlack) return false;
        t = Vector3.Dot(tr.E2, q) * inv;
        if (t < tMin || t > tMax) return false;
        // det = E1·(d×E2) = -d·(E1×E2): positive when the ray runs against the outward normal.
        front = det > 0f;
        return true;
    }

    /// <summary>The nearest triangle along a ray, among faces of the given kind, that the layers and the
    /// filter count. <paramref name="best"/> narrows as hits are found.</summary>
    internal bool Closest<F>(Vector3 o, Vector3 d, Vector3 inv, float tMin, ref float best, RayFaces faces,
                             GeometryLayers layers, ref F filter, int ownerOverride, out int triangle, out bool front)
        where F : IGeometryFilter
    {
        triangle = -1; front = false;
        if (Tris.Length == 0) return false;
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        if (BvhBuilder.Slab(TriNodes[0], o, inv, tMin, best) == float.MaxValue) return false;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref TriNodes[stack[--sp]];
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    if (!HitTriangle(Tris[i], o, d, tMin, best, out float t, out bool f)) continue;
                    if ((faces & (f ? RayFaces.Front : RayFaces.Back)) == 0) continue;
                    ref readonly var surface = ref Surfaces[TriSurface[i]];
                    if ((surface.Layers & layers) == 0) continue;
                    if (!filter.Accept(ownerOverride >= 0 ? ownerOverride : Solids[Tris[i].Solid].Owner, surface)) continue;
                    best = t; triangle = i; front = f;
                }
                continue;
            }
            int a = n.LeftFirst, b = a + 1;
            float ta = BvhBuilder.Slab(TriNodes[a], o, inv, tMin, best), tb = BvhBuilder.Slab(TriNodes[b], o, inv, tMin, best);
            if (ta > tb) { (a, b) = (b, a); (ta, tb) = (tb, ta); }
            if (tb != float.MaxValue) stack[sp++] = b;
            if (ta != float.MaxValue) stack[sp++] = a;
        }
        return triangle >= 0;
    }

    /// <summary>Whether anything counted lies along the ray within [tMin, tMax].</summary>
    internal bool Any<F>(Vector3 o, Vector3 d, Vector3 inv, float tMin, float tMax, RayFaces faces,
                         GeometryLayers layers, ref F filter, int ownerOverride) where F : IGeometryFilter
    {
        if (Tris.Length == 0) return false;
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref TriNodes[stack[--sp]];
            if (BvhBuilder.Slab(n, o, inv, tMin, tMax) == float.MaxValue) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    if (!HitTriangle(Tris[i], o, d, tMin, tMax, out _, out bool f)) continue;
                    if ((faces & (f ? RayFaces.Front : RayFaces.Back)) == 0) continue;
                    ref readonly var surface = ref Surfaces[TriSurface[i]];
                    if ((surface.Layers & layers) == 0) continue;
                    if (!filter.Accept(ownerOverride >= 0 ? ownerOverride : Solids[Tris[i].Solid].Owner, surface)) continue;
                    return true;
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
        return false;
    }

    /// <summary>Every counted triangle along the ray within [tMin, tMax], in no particular order.</summary>
    internal void All<F>(Vector3 o, Vector3 d, Vector3 inv, float tMin, float tMax, GeometryLayers layers, ref F filter,
                         int instance, int ownerOverride, List<GeometryCrossing> into) where F : IGeometryFilter
    {
        if (Tris.Length == 0) return;
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref TriNodes[stack[--sp]];
            if (BvhBuilder.Slab(n, o, inv, tMin, tMax) == float.MaxValue) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    if (!HitTriangle(Tris[i], o, d, tMin, tMax, out float t, out bool f)) continue;
                    ref readonly var surface = ref Surfaces[TriSurface[i]];
                    if ((surface.Layers & layers) == 0) continue;
                    int owner = ownerOverride >= 0 ? ownerOverride : Solids[Tris[i].Solid].Owner;
                    if (!filter.Accept(owner, surface)) continue;
                    into.Add(new GeometryCrossing(t, f, new SolidRef(instance, Tris[i].Solid), i, owner));
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
    }

    /// <summary>Whether a point is inside a convex solid, by more than <paramref name="slack"/>.</summary>
    internal bool Inside(int solid, Vector3 p, float slack)
    {
        ref readonly var s = ref Solids[solid];
        if (s.PlaneCount == 0) return false;
        for (int i = s.PlaneStart; i < s.PlaneStart + s.PlaneCount; i++)
        {
            var q = Planes[i];
            if (q.X * p.X + q.Y * p.Y + q.Z * p.Z - q.W > -slack) return false;
        }
        return true;
    }

    /// <summary>Every counted solid the point is inside (closed, convex solids only in stage 1).</summary>
    internal void Containing<F>(Vector3 p, float slack, GeometryLayers layers, ref F filter, int instance, int ownerOverride,
                                List<SolidRef> into) where F : IGeometryFilter
    {
        if (Solids.Length == 0) return;
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref SolidNodes[stack[--sp]];
            if (!BvhBuilder.Contains(n, p)) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    int s = SolidOrder[i];
                    ref readonly var surface = ref Surfaces[Solids[s].Surface];
                    if ((surface.Layers & layers) == 0) continue;
                    if (!Inside(s, p, slack)) continue;
                    if (!filter.Accept(ownerOverride >= 0 ? ownerOverride : Solids[s].Owner, surface)) continue;
                    into.Add(new SolidRef(instance, s));
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
    }

    /// <summary>Every counted solid whose bounds overlap the box [min, max].</summary>
    internal void Overlapping<F>(Vector3 min, Vector3 max, GeometryLayers layers, ref F filter, int instance, int ownerOverride,
                                 List<SolidRef> into) where F : IGeometryFilter
    {
        if (Solids.Length == 0) return;
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref SolidNodes[stack[--sp]];
            if (!BvhBuilder.Overlaps(n, min, max)) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    int s = SolidOrder[i];
                    ref readonly var rec = ref Solids[s];
                    if (rec.Max.X < min.X || rec.Min.X > max.X || rec.Max.Y < min.Y || rec.Min.Y > max.Y || rec.Max.Z < min.Z || rec.Min.Z > max.Z) continue;
                    ref readonly var surface = ref Surfaces[rec.Surface];
                    if ((surface.Layers & layers) == 0) continue;
                    if (!filter.Accept(ownerOverride >= 0 ? ownerOverride : rec.Owner, surface)) continue;
                    into.Add(new SolidRef(instance, s));
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
    }

    /// <summary>Every counted solid whose bounds a segment from <paramref name="o"/> along
    /// <paramref name="d"/> passes through within [0, tMax], the bounds grown by <paramref name="grow"/>.</summary>
    internal void Along<F>(Vector3 o, Vector3 d, Vector3 inv, float tMax, float grow, GeometryLayers layers, ref F filter,
                           int instance, int ownerOverride, List<SolidRef> into) where F : IGeometryFilter
    {
        if (Solids.Length == 0) return;
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        var g = new Vector3(grow);
        while (sp > 0)
        {
            ref readonly var n = ref SolidNodes[stack[--sp]];
            var grown = new BvhNode { Min = n.Min - g, Max = n.Max + g };
            if (BvhBuilder.Slab(grown, o, inv, 0f, tMax) == float.MaxValue) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    int s = SolidOrder[i];
                    ref readonly var rec = ref Solids[s];
                    var box = new BvhNode { Min = rec.Min - g, Max = rec.Max + g };
                    if (BvhBuilder.Slab(box, o, inv, 0f, tMax) == float.MaxValue) continue;
                    ref readonly var surface = ref Surfaces[rec.Surface];
                    if ((surface.Layers & layers) == 0) continue;
                    if (!filter.Accept(ownerOverride >= 0 ? ownerOverride : rec.Owner, surface)) continue;
                    into.Add(new SolidRef(instance, s));
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
    }
}

/// <summary>A solid in a world: which instance (a tile's piece, or a door leaf) and which solid in it.</summary>
public readonly record struct SolidRef(int Instance, int Solid);

/// <summary>A ray meeting a triangle: how far along, whether it entered a solid there, which solid,
/// which triangle (in its piece) and who owns it.</summary>
public readonly record struct GeometryCrossing(float T, bool Front, SolidRef Solid, int Triangle, int Owner);
