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
    /// <summary>How much ground it covers (its box's width by depth, or its bounds'): of two surfaces in
    /// the same place, the smaller patch is the one met (a path laid flush on the ground is the path, and
    /// a map's own ground beats the bigger foundation the loader lays under it).</summary>
    public float Footprint;
    /// <summary>The box this solid is, in the piece's frame: centre, size and turn. Size zero when the
    /// solid is not a box.</summary>
    public Vector3 BoxCentre, BoxSize;
    public Quaternion BoxRotation;
    /// <summary>Where the solid was placed, in the world, exactly as given: the frame's origin plus
    /// <see cref="BoxCentre"/> is that to a float's rounding, and code that still reads boxes meets their
    /// edges exactly where the box test did (a wall ending at x = -11.5 ends there, not a hair past it).</summary>
    public Vector3 PlacedAt;
    /// <summary>The convex pieces of a solid that is not convex (stairs, an arch), as a range of the
    /// piece's <see cref="SolidPart"/>s; none for a convex solid, which is its own one piece.</summary>
    public int PartStart, PartCount;
}

/// <summary>One convex piece of a solid that is not convex: its triangles (in the piece's part list, not
/// the tree rays walk) and the planes of its faces.</summary>
public struct SolidPart
{
    public int TriStart, TriCount;
    public int PlaneStart, PlaneCount;
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
                                        Surface Surface, MeshAsset? Mesh = null, MeshAsset[]? Parts = null)
{
    /// <summary>A solid of a shape from the library (null for a box), filling a box of <paramref name="size"/>.</summary>
    public static SolidSpec Of(int owner, Vector3 position, Quaternion rotation, Vector3 size, Surface surface, ShapeMesh? shape)
        => new(owner, position, rotation, size, surface, shape?.Outer, shape?.Parts);
}

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
    internal readonly SolidPart[] Parts;
    internal readonly GeometryTriangle[] PartTris;
    public Surface[] Surfaces { get; }

    public int TriangleCount => Tris.Length;
    public int SolidCount => Solids.Length;
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }
    /// <summary>What the piece holds in memory, bytes (arrays only).</summary>
    public long Bytes => (long)Tris.Length * 40 + TriSurface.Length * 2 + (long)TriNodes.Length * 32
                         + (long)Solids.Length * 124 + SolidTris.Length * 4 + Planes.Length * 16
                         + (long)SolidNodes.Length * 32 + SolidOrder.Length * 4 + (long)Parts.Length * 16 + (long)PartTris.Length * 40;

    private GeometryPiece(TileKey key, Vector3 origin, ulong signature, GeometryTriangle[] tris, ushort[] triSurface, BvhNode[] triNodes,
                          SolidRecord[] solids, int[] solidTris, Vector4[] planes, BvhNode[] solidNodes, int[] solidOrder,
                          Surface[] surfaces, SolidPart[] parts, GeometryTriangle[] partTris)
    {
        Key = key; Origin = origin; Signature = signature;
        Tris = tris; TriSurface = triSurface; TriNodes = triNodes;
        Solids = solids; SolidTris = solidTris; Planes = planes;
        SolidNodes = solidNodes; SolidOrder = solidOrder; Surfaces = surfaces;
        Parts = parts; PartTris = partTris;
        if (solids.Length > 0) { BoundsMin = triNodes[0].Min; BoundsMax = triNodes[0].Max; }
    }

    public ref readonly SolidRecord Solid(int index) => ref Solids[index];
    public ref readonly GeometryTriangle Triangle(int index) => ref Tris[index];
    public ref readonly Surface SurfaceOfTriangle(int index) => ref Surfaces[TriSurface[index]];
    /// <summary>The triangles of one solid, as indices into the piece's triangles.</summary>
    public ReadOnlySpan<int> TrianglesOf(int solid) => new(SolidTris, Solids[solid].TriStart, Solids[solid].TriCount);
    /// <summary>The face planes of a convex solid (normal, offset; a point p is inside when n·p - d &lt; 0 for all).</summary>
    public ReadOnlySpan<Vector4> PlanesOf(int solid) => new(Planes, Solids[solid].PlaneStart, Solids[solid].PlaneCount);
    /// <summary>The convex pieces of a solid: empty for a convex one.</summary>
    public ReadOnlySpan<SolidPart> PartsOf(int solid) => new(Parts, Solids[solid].PartStart, Solids[solid].PartCount);

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
        var parts = new List<SolidPart>();
        var partTris = new List<GeometryTriangle>();
        Span<Vector3> corners = stackalloc Vector3[8];
        int t = 0;
        for (int si = 0; si < solids.Count; si++)
        {
            var s = solids[si];
            var rotation = s.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(s.Rotation);
            var turn = ShapeLibrary.RotationMatrix(s.Rotation);
            int surface = SurfaceIndex(s.Surface);
            if (surface > ushort.MaxValue) throw new InvalidOperationException("more than 65,536 surfaces in one piece");
            ref var rec = ref records[si];
            rec.Owner = s.Owner; rec.Surface = surface; rec.TriStart = t;
            rec.Closed = s.Mesh?.Closed ?? true; rec.Convex = s.Mesh?.Convex ?? true;
            rec.BoxCentre = s.Position - origin; rec.BoxRotation = rotation; rec.PlacedAt = s.Position;
            // A shape fills its collider's box, and code that still reads boxes sees that box.
            rec.BoxSize = s.BoxSize;
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            if (s.Mesh == null)
            {
                for (int c = 0; c < 8; c++)
                {
                    corners[c] = ShapeLibrary.BoxCorner(c, s.Position, s.BoxSize, turn, origin);
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
                    verts[v] = at + Vector3.TransformNormal(m.Vertices[v], turn);
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
            var extent = s.BoxSize.X > 0f && s.BoxSize.Z > 0f ? s.BoxSize : hi - lo;
            rec.Footprint = extent.X * extent.Z;
            rec.PlaneStart = planes.Count;
            if (rec.Convex && rec.Closed) AddPlanes(tris, rec.TriStart, rec.TriCount, planes);
            rec.PlaneCount = planes.Count - rec.PlaneStart;
            rec.PartStart = parts.Count;
            if (s.Parts != null && !(rec.Convex && rec.Closed))
            {
                // The convex pieces a body is met against, placed exactly as the outer triangles are.
                var at = s.Position - origin;
                foreach (var m in s.Parts)
                {
                    var part = new SolidPart { TriStart = partTris.Count, PlaneStart = planes.Count };
                    for (int k = 0; k < m.Indices.Length; k += 3)
                    {
                        Vector3 a = at + Vector3.TransformNormal(m.Vertices[m.Indices[k]], turn);
                        Vector3 b = at + Vector3.TransformNormal(m.Vertices[m.Indices[k + 1]], turn);
                        Vector3 c = at + Vector3.TransformNormal(m.Vertices[m.Indices[k + 2]], turn);
                        partTris.Add(new GeometryTriangle { V0 = a, E1 = b - a, E2 = c - a, Solid = si });
                    }
                    part.TriCount = partTris.Count - part.TriStart;
                    AddPlanes(partTris, part.TriStart, part.TriCount, planes);
                    part.PlaneCount = planes.Count - part.PlaneStart;
                    parts.Add(part);
                }
            }
            rec.PartCount = parts.Count - rec.PartStart;
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
                                 planes.ToArray(), solidNodes, solidOrder, surfaces.ToArray(), parts.ToArray(), partTris.ToArray());
    }

    /// <summary>The distinct face planes of a convex solid, from its triangles.</summary>
    private static void AddPlanes(IReadOnlyList<GeometryTriangle> tris, int start, int count, List<Vector4> planes)
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
    /// filter count. <paramref name="best"/> narrows as hits are found. Of two at exactly the same
    /// distance (two surfaces in the same place) the smaller solid's wins, and of two the same size the
    /// lower owner's (<see cref="Ties"/>), so a server and a client agree on which one a ray met whatever
    /// order their trees visit them in.</summary>
    internal bool Closest<F>(Vector3 o, Vector3 d, Vector3 inv, float tMin, float tMax, ref float best, ref Ties tie, RayFaces faces,
                             GeometryLayers layers, ref F filter, int ownerOverride, out int triangle, out bool front)
        where F : IGeometryFilter
    {
        triangle = -1; front = false;
        if (Tris.Length == 0) return false;
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        if (BvhBuilder.Slab(TriNodes[0], o, inv, tMin, MathF.Min(tMax, best + TieSlack(best))) == float.MaxValue) return false;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref TriNodes[stack[--sp]];
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    float slack = TieSlack(best);
                    if (!HitTriangle(Tris[i], o, d, tMin, MathF.Min(tMax, best + slack), out float t, out bool f)) continue;
                    if ((faces & (f ? RayFaces.Front : RayFaces.Back)) == 0) continue;
                    ref readonly var surface = ref Surfaces[TriSurface[i]];
                    if ((surface.Layers & layers) == 0) continue;
                    ref readonly var rec = ref Solids[Tris[i].Solid];
                    int owner = ownerOverride >= 0 ? ownerOverride : rec.Owner;
                    // Within a hair of the best so far is the same place (two faces laid flush, worked out
                    // from different triangles): the tie rule decides, not the rounding.
                    if (t >= best - slack && !tie.Beats(rec.Footprint, owner)) continue;
                    if (!filter.Accept(owner, surface)) continue;
                    best = t; tie = new Ties(rec.Footprint, owner); triangle = i; front = f;
                }
                continue;
            }
            int a = n.LeftFirst, b = a + 1;
            float reach = MathF.Min(tMax, best + TieSlack(best));
            float ta = BvhBuilder.Slab(TriNodes[a], o, inv, tMin, reach), tb = BvhBuilder.Slab(TriNodes[b], o, inv, tMin, reach);
            if (ta > tb) { (a, b) = (b, a); (ta, tb) = (tb, ta); }
            if (tb != float.MaxValue) stack[sp++] = b;
            if (ta != float.MaxValue) stack[sp++] = a;
        }
        return triangle >= 0;
    }

    /// <summary>How close two hits are to be the same place, metres: a few millionths of the distance
    /// (what Möller–Trumbore's rounding leaves between two coplanar faces) and never less than 2 µm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float TieSlack(float t) => 2e-6f * (1f + MathF.Abs(t));

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

    /// <summary>Whether a point is inside a solid, by more than <paramref name="slack"/>: inside its planes
    /// when it is convex, inside one of its convex pieces when it is not.</summary>
    internal bool Inside(int solid, Vector3 p, float slack)
    {
        ref readonly var s = ref Solids[solid];
        if (s.PartCount > 0)
        {
            for (int k = s.PartStart; k < s.PartStart + s.PartCount; k++)
                if (InsidePlanes(Parts[k].PlaneStart, Parts[k].PlaneCount, p, slack)) return true;
            return false;
        }
        return InsidePlanes(s.PlaneStart, s.PlaneCount, p, slack);
    }

    private bool InsidePlanes(int start, int count, Vector3 p, float slack)
    {
        if (count == 0) return false;
        for (int i = start; i < start + count; i++)
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

    /// <summary>Every counted solid whose bounds, grown by <paramref name="grow"/>, reach up to
    /// <paramref name="fromY"/> over the ground-plane segment (ax, az)-(bx, bz): what the vertical plane
    /// through two points above that height can cut. In the piece's frame.</summary>
    internal void Column<F>(float ax, float az, float bx, float bz, float fromY, float grow, GeometryLayers layers, ref F filter,
                            int instance, int ownerOverride, List<SolidRef> into) where F : IGeometryFilter
    {
        if (Solids.Length == 0) return;
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref SolidNodes[stack[--sp]];
            if (!PieceColumns.Meets(n.Min, n.Max, ax, az, bx, bz, fromY, grow)) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    int s = SolidOrder[i];
                    ref readonly var rec = ref Solids[s];
                    if (!PieceColumns.Meets(rec.Min, rec.Max, ax, az, bx, bz, fromY, grow)) continue;
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

public static class PieceColumns
{
    /// <summary>Whether a box (grown by <paramref name="grow"/>) reaches up to <paramref name="fromY"/> and
    /// its footprint meets the ground-plane segment from (ax, az) to (bx, bz).</summary>
    internal static bool Meets(Vector3 min, Vector3 max, float ax, float az, float bx, float bz, float fromY, float grow)
    {
        if (max.Y + grow < fromY) return false;
        float t0 = 0f, t1 = 1f;
        return Slab2(ax, bx - ax, min.X - grow, max.X + grow, ref t0, ref t1)
            && Slab2(az, bz - az, min.Z - grow, max.Z + grow, ref t0, ref t1);
    }

    private static bool Slab2(float o, float d, float lo, float hi, ref float t0, ref float t1)
    {
        if (MathF.Abs(d) < 1e-12f) return o >= lo && o <= hi;
        float a = (lo - o) / d, b = (hi - o) / d;
        if (a > b) (a, b) = (b, a);
        if (a > t0) t0 = a;
        if (b < t1) t1 = b;
        return t0 <= t1;
    }
}

/// <summary>
/// Which of two surfaces met at the same distance counts: the one whose solid covers less ground, then the
/// lower owner's. Two surfaces in the same place are a thing laid flush on another (a drive on the ground,
/// a rug on a floor), and the thing laid on is the smaller patch. Never the order they were found in: that
/// differs between the server and a client.
/// </summary>
public readonly struct Ties
{
    public readonly float Footprint;
    public readonly int Owner;
    public Ties(float footprint, int owner) { Footprint = footprint; Owner = owner; }
    public static Ties None => new(float.MaxValue, int.MaxValue);
    public bool Beats(float footprint, int owner) => footprint < Footprint || (footprint == Footprint && owner < Owner);
}

/// <summary>A solid in a world: which instance (a tile's piece, or a door leaf) and which solid in it.</summary>
public readonly record struct SolidRef(int Instance, int Solid);

/// <summary>A ray meeting a triangle: how far along, whether it entered a solid there, which solid,
/// which triangle (in its piece) and who owns it.</summary>
public readonly record struct GeometryCrossing(float T, bool Front, SolidRef Solid, int Triangle, int Owner);
