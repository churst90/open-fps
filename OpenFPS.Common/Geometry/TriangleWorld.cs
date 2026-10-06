using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace OpenFPS.Common.Geometry;

/// <summary>
/// One piece placed in the world: a tile's static geometry at its tile's corner, or a moving thing (a
/// door leaf) at its pose. <see cref="Owner"/>, when not -1, is who every solid in the piece belongs to:
/// door leaves of one size and build share one piece and are told apart by their instances.
/// </summary>
public readonly struct GeometryInstance
{
    public readonly GeometryPiece Piece;
    public readonly Vector3 Position;
    public readonly Quaternion Rotation;
    public readonly Quaternion Inverse;
    public readonly bool Rotated;
    public readonly int Owner;
    public readonly Vector3 Min, Max;

    public GeometryInstance(GeometryPiece piece, Vector3 position, Quaternion rotation, int owner)
    {
        Piece = piece; Position = position; Owner = owner;
        if (rotation.LengthSquared() < 1e-6f) rotation = Quaternion.Identity;
        Rotated = rotation != Quaternion.Identity;
        Rotation = rotation;
        Inverse = Rotated ? Quaternion.Inverse(rotation) : Quaternion.Identity;
        if (piece.SolidCount == 0) { Min = Max = position; return; }
        var lo = piece.BoundsMin; var hi = piece.BoundsMax;
        if (!Rotated) { Min = position + lo; Max = position + hi; return; }
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var p = new Vector3((c & 1) == 0 ? lo.X : hi.X, (c & 2) == 0 ? lo.Y : hi.Y, (c & 4) == 0 ? lo.Z : hi.Z);
            var w = position + Vector3.Transform(p, rotation);
            mn = Vector3.Min(mn, w); mx = Vector3.Max(mx, w);
        }
        Min = mn; Max = mx;
    }

    /// <summary>A world point in the piece's frame.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 ToLocal(Vector3 world) => Rotated ? Vector3.Transform(world - Position, Inverse) : world - Position;
    /// <summary>A world direction in the piece's frame.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 DirectionToLocal(Vector3 d) => Rotated ? Vector3.Transform(d, Inverse) : d;
    /// <summary>A direction in the piece's frame, in the world.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 DirectionToWorld(Vector3 d) => Rotated ? Vector3.Transform(d, Rotation) : d;
    /// <summary>A point in the piece's frame, in the world.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 ToWorld(Vector3 local) => Rotated ? Position + Vector3.Transform(local, Rotation) : Position + local;
}

/// <summary>The nearest thing along a ray: how far, the face's outward normal in the world (the way back
/// along the ray when the ray began inside it), the solid and its owner, and whether it began inside.</summary>
public struct GeometryHit
{
    public float T;
    public Vector3 Normal;
    public SolidRef Solid;
    public int Triangle;
    public int Owner;
    public bool Front;
    public bool Inside;
}

/// <summary>
/// The triangle world (docs/GEOMETRY.md 2.2): the top level of the two-level BVH, a tree over placed
/// pieces, and every query the game asks of geometry. Immutable: a change makes a new world that shares
/// every piece that did not change, so a reader on any thread holds a world that never changes under it,
/// and nothing locks.
///
/// Distances are along a unit direction, in metres. Points and directions are in the world; each piece is
/// asked in its own frame (a tile's corner, a door's pose) and its answers are brought back.
/// </summary>
public sealed class TriangleWorld
{
    private readonly GeometryInstance[] _instances;
    private readonly BvhNode[] _nodes;
    private readonly int[] _order;

    /// <summary>The world with nothing in it.</summary>
    public static readonly TriangleWorld Empty = new(Array.Empty<GeometryInstance>(), 0);

    /// <summary>Counts the worlds made, so a cache keyed on a world can tell a new one from an old.</summary>
    public long Version { get; }
    private static long _versions;

    public int InstanceCount => _instances.Length;
    public ref readonly GeometryInstance Instance(int i) => ref _instances[i];
    public ReadOnlySpan<GeometryInstance> Instances => _instances;

    public int TriangleCount { get; }
    public int SolidCount { get; }

    public TriangleWorld(GeometryInstance[] instances, long version = -1)
    {
        _instances = instances;
        Version = version >= 0 ? version : System.Threading.Interlocked.Increment(ref _versions);
        var bmin = new Vector3[instances.Length]; var bmax = new Vector3[instances.Length];
        int tris = 0, solids = 0;
        for (int i = 0; i < instances.Length; i++)
        {
            bmin[i] = instances[i].Min; bmax[i] = instances[i].Max;
            tris += instances[i].Piece.TriangleCount; solids += instances[i].Piece.SolidCount;
        }
        TriangleCount = tris; SolidCount = solids;
        _nodes = BvhBuilder.Build(bmin, bmax, instances.Length, 1, out _order);
    }

    private TriangleWorld(GeometryInstance[] instances, BvhNode[] nodes, int[] order, int tris, int solids)
    {
        _instances = instances; _nodes = nodes; _order = order;
        TriangleCount = tris; SolidCount = solids;
        Version = System.Threading.Interlocked.Increment(ref _versions);
    }

    /// <summary>
    /// The same world with some instances moved (door leaves swinging): their poses replaced, the tree
    /// refitted rather than rebuilt. Nothing is added or taken away.
    /// </summary>
    public TriangleWorld WithMoved(ReadOnlySpan<(int Index, Vector3 Position, Quaternion Rotation)> moves)
    {
        if (moves.Length == 0) return this;
        var instances = (GeometryInstance[])_instances.Clone();
        foreach (var (i, p, r) in moves)
            instances[i] = new GeometryInstance(instances[i].Piece, p, r, instances[i].Owner);
        var bmin = new Vector3[instances.Length]; var bmax = new Vector3[instances.Length];
        for (int i = 0; i < instances.Length; i++) { bmin[i] = instances[i].Min; bmax[i] = instances[i].Max; }
        var nodes = (BvhNode[])_nodes.Clone();
        BvhBuilder.Refit(nodes, _order, bmin, bmax);
        return new TriangleWorld(instances, nodes, _order, TriangleCount, SolidCount) { _movers = _movers };
    }

    /// <summary>Where each mover's instance is, by owner. Set by the builder.</summary>
    private Dictionary<int, int>? _movers;

    internal TriangleWorld WithMoverIndex(Dictionary<int, int> movers) { _movers = movers; return this; }

    /// <summary>Whether an owner is a mover here, and which instance places it.</summary>
    public bool TryGetMover(int owner, out int instance)
    {
        instance = -1;
        return _movers != null && _movers.TryGetValue(owner, out instance);
    }

    public int MoverCount => _movers?.Count ?? 0;

    /// <summary>
    /// The same world with its movers at the poses <paramref name="poseOf"/> gives (false: leave it where
    /// it is). Returns this world when none moved.
    /// </summary>
    public TriangleWorld WithMoverPoses(Func<int, (bool Known, Vector3 Position, Quaternion Rotation)> poseOf)
    {
        if (_movers == null || _movers.Count == 0) return this;
        List<(int, Vector3, Quaternion)>? moved = null;
        foreach (var (owner, i) in _movers)
        {
            var (known, p, r) = poseOf(owner);
            if (!known) continue;
            if (r.LengthSquared() < 1e-6f) r = Quaternion.Identity;
            ref readonly var inst = ref _instances[i];
            if (inst.Position == p && inst.Rotation == r) continue;
            (moved ??= new List<(int, Vector3, Quaternion)>()).Add((i, p, r));
        }
        if (moved == null) return this;
        return WithMoved(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(moved));
    }

    // ═══ What a solid is ═════════════════════════════════════════════════════════════════════════

    public int OwnerOf(SolidRef s)
    {
        ref readonly var inst = ref _instances[s.Instance];
        return inst.Owner >= 0 ? inst.Owner : inst.Piece.Solids[s.Solid].Owner;
    }

    public ref readonly Surface SurfaceOf(SolidRef s)
    {
        var piece = _instances[s.Instance].Piece;
        return ref piece.Surfaces[piece.Solids[s.Solid].Surface];
    }

    /// <summary>The surface of the triangle a ray met.</summary>
    public ref readonly Surface SurfaceOf(in GeometryHit hit)
        => ref hit.Triangle >= 0 ? ref _instances[hit.Solid.Instance].Piece.SurfaceOfTriangle(hit.Triangle) : ref SurfaceOf(hit.Solid);

    /// <summary>The box a solid was made from, in the world (size zero if it was not a box).</summary>
    public (Vector3 Centre, Vector3 Size, Quaternion Rotation) BoxOf(SolidRef s)
    {
        ref readonly var inst = ref _instances[s.Instance];
        ref readonly var rec = ref inst.Piece.Solids[s.Solid];
        var rotation = inst.Rotated ? Quaternion.Normalize(inst.Rotation * rec.BoxRotation) : rec.BoxRotation;
        return (inst.ToWorld(rec.BoxCentre), rec.BoxSize, rotation);
    }

    /// <summary>A solid's bounds in the world (loose for a turned instance).</summary>
    public (Vector3 Min, Vector3 Max) BoundsOf(SolidRef s)
    {
        ref readonly var inst = ref _instances[s.Instance];
        ref readonly var rec = ref inst.Piece.Solids[s.Solid];
        if (!inst.Rotated) return (inst.Position + rec.Min, inst.Position + rec.Max);
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var p = new Vector3((c & 1) == 0 ? rec.Min.X : rec.Max.X, (c & 2) == 0 ? rec.Min.Y : rec.Max.Y, (c & 4) == 0 ? rec.Min.Z : rec.Max.Z);
            var w = inst.ToWorld(p);
            mn = Vector3.Min(mn, w); mx = Vector3.Max(mx, w);
        }
        return (mn, mx);
    }

    /// <summary>
    /// A solid's triangles relative to <paramref name="relativeTo"/> (a body's centre, say): three
    /// corners each into <paramref name="into"/>, which must hold 3 × the solid's triangle count. Returns
    /// how many triangles. Worked out as (instance place − relativeTo) + local corner, so a body far from
    /// the world's origin still meets a wall to a fraction of a millimetre.
    /// </summary>
    public int TrianglesOf(SolidRef s, Vector3 relativeTo, Span<Vector3> into)
    {
        ref readonly var inst = ref _instances[s.Instance];
        var piece = inst.Piece;
        var tris = piece.TrianglesOf(s.Solid);
        var shift = inst.Position - relativeTo;
        for (int k = 0; k < tris.Length; k++)
        {
            ref readonly var tr = ref piece.Tris[tris[k]];
            Vector3 a = tr.V0, b = tr.V0 + tr.E1, c = tr.V0 + tr.E2;
            if (inst.Rotated) { a = Vector3.Transform(a, inst.Rotation); b = Vector3.Transform(b, inst.Rotation); c = Vector3.Transform(c, inst.Rotation); }
            into[3 * k] = shift + a; into[3 * k + 1] = shift + b; into[3 * k + 2] = shift + c;
        }
        return tris.Length;
    }

    /// <summary>How big a solid is (Ties).</summary>
    public float VolumeOf(SolidRef s) => _instances[s.Instance].Piece.Solids[s.Solid].Volume;

    /// <summary>How many triangles a solid has.</summary>
    public int TriangleCountOf(SolidRef s) => _instances[s.Instance].Piece.Solids[s.Solid].TriCount;

    /// <summary>A convex solid's face planes relative to <paramref name="relativeTo"/>: (n, d) with n·p - d
    /// &lt; 0 inside, into <paramref name="into"/>. Returns how many (0 for a solid that is not convex).</summary>
    public int PlanesOf(SolidRef s, Vector3 relativeTo, Span<Vector4> into)
    {
        ref readonly var inst = ref _instances[s.Instance];
        var planes = inst.Piece.PlanesOf(s.Solid);
        var shift = inst.Position - relativeTo;
        for (int k = 0; k < planes.Length; k++)
        {
            var n = new Vector3(planes[k].X, planes[k].Y, planes[k].Z);
            float d = planes[k].W;
            if (inst.Rotated) n = Vector3.Transform(n, inst.Rotation);
            // p_local = R⁻¹(p_rel - shift): n_local·p_local = n·(p_rel - shift) for the turned n.
            into[k] = new Vector4(n, d + Vector3.Dot(n, shift));
        }
        return planes.Length;
    }

    /// <summary>The outward unit normal of a triangle a ray met, in the world.</summary>
    public Vector3 NormalOf(SolidRef s, int triangle)
    {
        ref readonly var inst = ref _instances[s.Instance];
        var n = Vector3.Normalize(inst.Piece.Tris[triangle].Normal);
        return inst.DirectionToWorld(n);
    }

    // ═══ Rays ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The nearest counted face along a ray from <paramref name="o"/> along the unit
    /// <paramref name="d"/>, between <paramref name="tMin"/> and <paramref name="tMax"/>. The normal is
    /// the face's outward normal.
    /// </summary>
    public bool Closest<F>(Vector3 o, Vector3 d, float tMax, GeometryLayers layers, RayFaces faces, ref F filter,
                           out GeometryHit hit, float tMin = 0f) where F : IGeometryFilter
    {
        hit = default; hit.Triangle = -1; hit.T = tMax;
        if (_instances.Length == 0) return false;
        var inv = BvhBuilder.Inverse(d);
        float best = tMax;
        var tie = Ties.None;
        bool found = false;
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        if (BvhBuilder.Slab(_nodes[0], o, inv, tMin, best) == float.MaxValue) return false;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref _nodes[stack[--sp]];
            if (BvhBuilder.Slab(n, o, inv, tMin, best) == float.MaxValue) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    int ii = _order[i];
                    ref readonly var inst = ref _instances[ii];
                    Vector3 lo = inst.ToLocal(o), ld = inst.DirectionToLocal(d);
                    var linv = inst.Rotated ? BvhBuilder.Inverse(ld) : inv;
                    if (inst.Piece.Closest(lo, ld, linv, tMin, ref best, ref tie, faces, layers, ref filter, inst.Owner, out int tri, out bool front))
                    {
                        found = true;
                        hit.T = best; hit.Triangle = tri; hit.Front = front;
                        hit.Solid = new SolidRef(ii, inst.Piece.Tris[tri].Solid);
                    }
                }
                continue;
            }
            int a = n.LeftFirst, b = a + 1;
            float ta = BvhBuilder.Slab(_nodes[a], o, inv, tMin, best), tb = BvhBuilder.Slab(_nodes[b], o, inv, tMin, best);
            if (ta > tb) { (a, b) = (b, a); (ta, tb) = (tb, ta); }
            if (tb != float.MaxValue) stack[sp++] = b;
            if (ta != float.MaxValue) stack[sp++] = a;
        }
        if (!found) return false;
        hit.Owner = OwnerOf(hit.Solid);
        hit.Normal = NormalOf(hit.Solid, hit.Triangle);
        return true;
    }

    /// <summary>
    /// The nearest solid along a ray as a box test answers it: a ray that begins inside a solid meets it
    /// at once (distance 0, <see cref="GeometryHit.Inside"/>); otherwise the first face it enters by.
    /// What RaycastSingle, CastSight and a bullet's segment ask.
    /// </summary>
    public bool Enter<F>(Vector3 o, Vector3 d, float tMax, GeometryLayers layers, ref F filter, out GeometryHit hit)
        where F : IGeometryFilter
    {
        var inside = Scratch.Solids;
        inside.Clear();
        Containing(o, layers, ref filter, inside);
        if (inside.Count > 0)
        {
            // Of several, the smaller, then the lower owner (Ties): a rule the server and a client share
            // whatever order they met them in.
            var pick = inside[0];
            int owner = OwnerOf(pick);
            var tie = new Ties(VolumeOf(pick), owner);
            for (int i = 1; i < inside.Count; i++)
            {
                int other = OwnerOf(inside[i]);
                float volume = VolumeOf(inside[i]);
                if (tie.Beats(volume, other)) { owner = other; pick = inside[i]; tie = new Ties(volume, other); }
            }
            // Its normal is the face the ray came in by, behind where it starts, as a box test reports it:
            // back along the ray, the face it leaves the solid by.
            var normal = -d;
            var only = new OnlyOwner(owner);
            if (Closest(o, -d, GroundReach, layers, RayFaces.Back, ref only, out var back)) normal = back.Normal;
            hit = new GeometryHit { T = 0f, Normal = normal, Solid = pick, Triangle = -1, Owner = owner, Inside = true, Front = true };
            return true;
        }
        if (!Closest(o, d, tMax, layers, RayFaces.Front, ref filter, out hit, -InsideSlack)) return false;
        if (hit.T < 0f) hit.T = 0f;
        return true;
    }

    /// <summary>Whether anything counted lies along the ray within [tMin, tMax].</summary>
    public bool Any<F>(Vector3 o, Vector3 d, float tMax, GeometryLayers layers, RayFaces faces, ref F filter, float tMin = 0f)
        where F : IGeometryFilter
    {
        if (_instances.Length == 0) return false;
        var inv = BvhBuilder.Inverse(d);
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref _nodes[stack[--sp]];
            if (BvhBuilder.Slab(n, o, inv, tMin, tMax) == float.MaxValue) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    ref readonly var inst = ref _instances[_order[i]];
                    Vector3 lo = inst.ToLocal(o), ld = inst.DirectionToLocal(d);
                    var linv = inst.Rotated ? BvhBuilder.Inverse(ld) : inv;
                    if (inst.Piece.Any(lo, ld, linv, tMin, tMax, faces, layers, ref filter, inst.Owner)) return true;
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
        return false;
    }

    /// <summary>Every counted face along the ray within [tMin, tMax], sorted by distance (ties by owner).</summary>
    public void All<F>(Vector3 o, Vector3 d, float tMax, GeometryLayers layers, ref F filter, List<GeometryCrossing> into,
                       float tMin = 0f) where F : IGeometryFilter
    {
        into.Clear();
        if (_instances.Length == 0) return;
        var inv = BvhBuilder.Inverse(d);
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref _nodes[stack[--sp]];
            if (BvhBuilder.Slab(n, o, inv, tMin, tMax) == float.MaxValue) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    int ii = _order[i];
                    ref readonly var inst = ref _instances[ii];
                    Vector3 lo = inst.ToLocal(o), ld = inst.DirectionToLocal(d);
                    var linv = inst.Rotated ? BvhBuilder.Inverse(ld) : inv;
                    inst.Piece.All(lo, ld, linv, tMin, tMax, layers, ref filter, ii, inst.Owner, into);
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
        if (into.Count > 1) into.Sort(static (x, y) => x.T != y.T ? x.T.CompareTo(y.T) : x.Owner.CompareTo(y.Owner));
    }

    // ═══ Places and neighbourhoods ═══════════════════════════════════════════════════════════════

    /// <summary>How far inside a solid a point must be to count as in it, metres: a point on a face is not.</summary>
    public const float InsideSlack = 1e-5f;

    /// <summary>Every counted solid the point is inside (by more than <see cref="InsideSlack"/>).</summary>
    public void Containing<F>(Vector3 p, GeometryLayers layers, ref F filter, List<SolidRef> into) where F : IGeometryFilter
    {
        if (_instances.Length == 0) return;
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref _nodes[stack[--sp]];
            if (!BvhBuilder.Contains(n, p)) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    int ii = _order[i];
                    ref readonly var inst = ref _instances[ii];
                    inst.Piece.Containing(inst.ToLocal(p), InsideSlack, layers, ref filter, ii, inst.Owner, into);
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
    }

    /// <summary>Every counted solid whose bounds overlap the world box [min, max]: the candidates an exact
    /// test then looks at.</summary>
    public void Overlapping<F>(Vector3 min, Vector3 max, GeometryLayers layers, ref F filter, List<SolidRef> into)
        where F : IGeometryFilter
    {
        if (_instances.Length == 0) return;
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref _nodes[stack[--sp]];
            if (!BvhBuilder.Overlaps(n, min, max)) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    int ii = _order[i];
                    ref readonly var inst = ref _instances[ii];
                    Vector3 lmin, lmax;
                    if (!inst.Rotated) { lmin = min - inst.Position; lmax = max - inst.Position; }
                    else
                    {
                        lmin = new Vector3(float.MaxValue); lmax = new Vector3(float.MinValue);
                        for (int c = 0; c < 8; c++)
                        {
                            var w = new Vector3((c & 1) == 0 ? min.X : max.X, (c & 2) == 0 ? min.Y : max.Y, (c & 4) == 0 ? min.Z : max.Z);
                            var l = inst.ToLocal(w);
                            lmin = Vector3.Min(lmin, l); lmax = Vector3.Max(lmax, l);
                        }
                    }
                    inst.Piece.Overlapping(lmin, lmax, layers, ref filter, ii, inst.Owner, into);
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
    }

    /// <summary>Every counted solid whose bounds, grown by <paramref name="grow"/>, the segment from
    /// <paramref name="a"/> to <paramref name="b"/> passes through. Candidates for an exact test.</summary>
    public void Along<F>(Vector3 a, Vector3 b, float grow, GeometryLayers layers, ref F filter, List<SolidRef> into)
        where F : IGeometryFilter
    {
        if (_instances.Length == 0) return;
        var seg = b - a;
        float len = seg.Length();
        var d = len > 1e-9f ? seg / len : Vector3.UnitX;
        var inv = BvhBuilder.Inverse(d);
        var g = new Vector3(grow);
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref _nodes[stack[--sp]];
            var grown = new BvhNode { Min = n.Min - g, Max = n.Max + g };
            if (BvhBuilder.Slab(grown, a, inv, 0f, len) == float.MaxValue) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    int ii = _order[i];
                    ref readonly var inst = ref _instances[ii];
                    Vector3 lo = inst.ToLocal(a), ld = inst.DirectionToLocal(d);
                    var linv = inst.Rotated ? BvhBuilder.Inverse(ld) : inv;
                    inst.Piece.Along(lo, ld, linv, len, grow, layers, ref filter, ii, inst.Owner, into);
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
    }

    /// <summary>
    /// Every counted solid whose bounds, grown by <paramref name="grow"/>, stand over the ground between
    /// <paramref name="a"/> and <paramref name="b"/> and reach up to <paramref name="fromY"/>: what the
    /// vertical plane through the two points can cut above that height. Candidates for an exact test.
    /// Unturned instances only (tiles); a turned one is asked by its bounds.
    /// </summary>
    public void Column<F>(Vector3 a, Vector3 b, float fromY, float grow, GeometryLayers layers, ref F filter, List<SolidRef> into)
        where F : IGeometryFilter
    {
        if (_instances.Length == 0) return;
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref _nodes[stack[--sp]];
            if (!PieceColumns.Meets(n.Min, n.Max, a.X, a.Z, b.X, b.Z, fromY, grow)) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    int ii = _order[i];
                    ref readonly var inst = ref _instances[ii];
                    if (inst.Rotated)
                    {
                        // A door leaf: every solid of it whose world bounds meet the column.
                        for (int s = 0; s < inst.Piece.SolidCount; s++)
                        {
                            var (mn, mx) = BoundsOf(new SolidRef(ii, s));
                            if (!PieceColumns.Meets(mn, mx, a.X, a.Z, b.X, b.Z, fromY, grow)) continue;
                            ref readonly var surface = ref inst.Piece.Surfaces[inst.Piece.Solids[s].Surface];
                            if ((surface.Layers & layers) == 0) continue;
                            if (!filter.Accept(inst.Owner >= 0 ? inst.Owner : inst.Piece.Solids[s].Owner, surface)) continue;
                            into.Add(new SolidRef(ii, s));
                        }
                        continue;
                    }
                    var p = inst.Position;
                    inst.Piece.Column(a.X - p.X, a.Z - p.Z, b.X - p.X, b.Z - p.Z, fromY - p.Y, grow, layers, ref filter, ii, inst.Owner, into);
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
    }

    /// <summary>Which instance holds the piece of a tile, or -1.</summary>
    public int InstanceOfTile(TileKey key)
    {
        for (int i = 0; i < _instances.Length; i++)
            if (_instances[i].Owner < 0 && _instances[i].Piece.Key == key) return i;
        return -1;
    }

    // ═══ The ground ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The highest counted upward face under five points (the centre and four at <paramref name="radius"/>
    /// east, west, north and south of it) no higher than <paramref name="pos"/>.Y + <paramref name="step"/>:
    /// the floor a body standing at <paramref name="pos"/> stands on, as PhysicsUtils.GetGroundHeight asks
    /// it. Returns -1000 when there is none. Of two floors at the same height, the lower owner.
    /// </summary>
    public float Ground<F>(Vector3 pos, float radius, float step, GeometryLayers layers, ref F filter, out SolidRef solid, out int owner)
        where F : IGeometryFilter
    {
        solid = default; owner = -1;
        float bestY = -1000f;
        if (_instances.Length == 0) return bestY;
        float top = pos.Y + step;
        Span<Vector3> probes = stackalloc Vector3[5];
        probes[0] = pos;
        probes[1] = pos + new Vector3(radius, 0, 0);
        probes[2] = pos + new Vector3(-radius, 0, 0);
        probes[3] = pos + new Vector3(0, 0, radius);
        probes[4] = pos + new Vector3(0, 0, -radius);
        var down = -Vector3.UnitY;
        for (int k = 0; k < 5; k++)
        {
            var from = new Vector3(probes[k].X, top, probes[k].Z);
            // A face exactly at the top is counted and one a hair above it is not, as the box test did:
            // the height is read exactly off the face's plane, and a face the ray found that is in fact
            // over the top is stepped past.
            float tFrom = -InsideSlack;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (!Closest(from, down, GroundReach, layers, RayFaces.Front, ref filter, out var hit, tFrom)) break;
                float y = HeightOn(hit, from.X, from.Z);
                if (y > top) { tFrom = MathF.Max(hit.T, tFrom) + 1e-6f; continue; }
                if (y > bestY || (y == bestY && owner >= 0 && new Ties(VolumeOf(solid), owner).Beats(VolumeOf(hit.Solid), hit.Owner)))
                {
                    bestY = y; solid = hit.Solid; owner = hit.Owner;
                }
                break;
            }
        }
        return bestY;
    }

    /// <summary>How far down the ground is looked for, metres (the box test's footprint was 40 km tall).</summary>
    public const float GroundReach = 40000f;

    /// <summary>
    /// The height of the face a ray met at (x, z), on its plane. For a level face this is exactly the
    /// height its corners were made at, which is exactly the top a box test reads (centre + half size).
    /// </summary>
    public float HeightOn(in GeometryHit hit, float x, float z)
    {
        ref readonly var inst = ref _instances[hit.Solid.Instance];
        ref readonly var tr = ref inst.Piece.Tris[hit.Triangle];
        if (inst.Rotated)
        {
            // Down through (x, z) in the piece's frame, from height 0: the face is met at -t.
            var o = inst.ToLocal(new Vector3(x, 0f, z));
            var dl = inst.DirectionToLocal(-Vector3.UnitY);
            var n = tr.Normal;
            float den = Vector3.Dot(n, dl);
            if (MathF.Abs(den) < 1e-12f) return float.MinValue;
            return -Vector3.Dot(n, tr.V0 - o) / den;
        }
        var nn = tr.Normal;
        if (nn.Y == 0f) return float.MinValue;
        float lx = x - inst.Position.X, lz = z - inst.Position.Z;
        float ly = tr.V0.Y;
        if (nn.X != 0f || nn.Z != 0f) ly -= (nn.X * (lx - tr.V0.X) + nn.Z * (lz - tr.V0.Z)) / nn.Y;
        return inst.Position.Y + ly;
    }

    private readonly struct OnlyOwner : IGeometryFilter
    {
        private readonly int _owner;
        public OnlyOwner(int owner) => _owner = owner;
        public bool Accept(int owner, in Surface surface) => owner == _owner;
    }

    // ═══ Scratch, one per thread ═════════════════════════════════════════════════════════════════

    internal static class Scratch
    {
        [ThreadStatic] private static List<SolidRef>? _solids;
        public static List<SolidRef> Solids => _solids ??= new List<SolidRef>(16);
    }
}
