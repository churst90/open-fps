using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>
/// Sight rays, cheaply enough to look ten times a second while walking.
///
/// <para>The collision grid's ten-metre cells are asked by radius, so a twenty-metre ray asked about a
/// fifty-metre square and a look of four or five rays cost 1.5 ms in the city. This keeps its own index
/// of the fixed things, <see cref="CellMetres"/> cells walked in order along the ray, box before shape.
/// Floors, roofs and roads are kept apart as slabs and tested whole; door leaves and moving things are
/// gathered fresh for each look (<see cref="Prepare"/>).</para>
///
/// <para>The answers are <see cref="SpatialService.RaycastSingle(WorldSnapshot, Vector3, Vector3, float, Func{EntitySnapshot, bool}, out EntitySnapshot, out float)"/>'s,
/// same thing at the same distance; only the cost differs.</para>
///
/// <para>With a triangle world the fixed boxes are its (physical layers for solids, GeometryLayers.Announced
/// for things named but not solid) and a look asks its tree. The index then keeps only what the
/// triangles do not hold: a fixed thing that is not a box, and anything changed since the last build.</para>
/// </summary>
public sealed class SightGrid
{
    public const float CellMetres = 4f;
    /// <summary>Anything covering more cells than this is a slab, tested whole.</summary>
    public const int SlabCells = 24;
    /// <summary>How often the fixed things are counted to see whether the index is stale, seconds.</summary>
    public const double CheckSeconds = 2.0;

    private readonly record struct Entry(int Id, Vector3 Min, Vector3 Max);

    private Entry[] _entries = Array.Empty<Entry>();
    private int[] _stamp = Array.Empty<int>();
    private int _stampNow;
    private readonly Dictionary<long, List<int>> _cells = new();
    private readonly List<int> _slabs = new();
    private long _signature = long.MinValue;
    private double _checkedAt = double.NegativeInfinity;
    private int _checkedCount = -1;

    // Per look: the slabs, door leaves and other things within reach.
    private readonly List<int> _nearSlabs = new();
    private readonly List<EntitySnapshot> _nearLoose = new();

    /// <summary>How many fixed things the index holds, and how many of them are slabs.</summary>
    public (int Things, int Slabs) Size => (_entries.Length, _slabs.Count);

    /// <summary>Whether a thing is one a look can find among the fixed things: fixed, a solid or an
    /// announced thing, and not a door leaf.</summary>
    private static bool Seen(in EntitySnapshot e)
        => e.Definition.Type == OpenFPS.Common.Components.EntityType.StaticObject && !e.Definition.Moves
           && (e.Definition.Collider.IsSolid || e.Definition.Identity.Announce)
           && e.Definition.Collider.Size.X > 0f
           && !OpeningGraph.IsDoorLeaf(e.Definition);

    /// <summary>Whether the triangle world holds a thing (EntityGeometry), so a look asks its tree for it.</summary>
    private static bool InTriangles(in EntitySnapshot e)
        => EntityGeometry.RoleOf(e.Definition) is GeometryRole.Static or GeometryRole.SightOnly or GeometryRole.Mover;

    /// <summary>Whether this index holds a thing: one a look can find that the triangles do not hold.</summary>
    private bool Indexed(in EntitySnapshot e) => Seen(e) && !(_triangles && InTriangles(e));

    /// <summary>Whether the index was built for a world with triangles.</summary>
    private bool _triangles;

    /// <summary>Rebuilds the index if the world's fixed things have changed: checked when the number of
    /// things in the world changes, at most every <see cref="CheckSeconds"/>.</summary>
    public void Refresh(WorldSnapshot world, double now)
    {
        bool triangles = SpatialService.UsesTriangles(world);
        if (triangles != _triangles) { _triangles = triangles; _signature = long.MinValue; _checkedCount = -1; }
        if (world.Entities.Count == _checkedCount && (_entries.Length > 0 || _triangles)) return;
        if (_checkedCount >= 0 && now - _checkedAt < CheckSeconds) return;
        _checkedCount = world.Entities.Count;
        _checkedAt = now;
        long signature = 17;
        int n = 0;
        foreach (var e in world.Entities.Values)
            if (Indexed(e)) { signature = signature * 31 + e.Id; n++; }
        signature = signature * 31 + n;
        if (signature == _signature) return;
        _signature = signature;
        Build(world, n);
    }

    private void Build(WorldSnapshot world, int count)
    {
        var entries = new Entry[count];
        int i = 0;
        _cells.Clear();
        _slabs.Clear();
        foreach (var e in world.Entities.Values)
        {
            if (!Indexed(e) || i >= count) continue;
            var (min, max) = Sightline.WorldBounds(e);
            entries[i] = new Entry(e.Id, min, max);
            int x0 = Cell(min.X), x1 = Cell(max.X), z0 = Cell(min.Z), z1 = Cell(max.Z);
            if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > SlabCells) _slabs.Add(i);
            else
                for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long key = Key(x, z);
                        if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>(8);
                        list.Add(i);
                    }
            i++;
        }
        _entries = entries;
        _stamp = new int[entries.Length];
        _stampNow = 0;
    }

    private static int Cell(float v) => (int)MathF.Floor(v / CellMetres);
    private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

    /// <summary>
    /// Gathers what each cast of this look tests besides the index: the slabs, door leaves and things
    /// that are not fixed, within <paramref name="reach"/> of <paramref name="at"/>. Casts that leave
    /// that sphere miss whatever is outside it.
    /// </summary>
    public void Prepare(WorldSnapshot world, Vector3 at, float reach, IReadOnlyList<SightIndex.Mark> doors)
    {
        _nearSlabs.Clear();
        foreach (int s in _slabs)
        {
            var en = _entries[s];
            var nearest = Vector3.Clamp(at, en.Min, en.Max);
            if (Vector3.DistanceSquared(nearest, at) <= reach * reach) _nearSlabs.Add(s);
        }
        _nearLoose.Clear();
        float r2 = (reach + 3f) * (reach + 3f);
        foreach (var e in world.DynamicEntities)
            if (Vector3.DistanceSquared(e.Transform.Position, at) <= r2 + e.Definition.Collider.Size.LengthSquared())
                _nearLoose.Add(e);
        // A fixed thing whose solid changed since the triangles were built is looked at the old way until
        // they catch up.
        if (_triangles && world.GeometryStale is { Count: > 0 } stale)
            foreach (int id in stale)
                if (world.Entities.TryGetValue(id, out var e) && Seen(e) && Vector3.DistanceSquared(e.Transform.Position, at) <= r2 + e.Definition.Collider.Size.LengthSquared())
                    _nearLoose.Add(e);
        for (int i = 0; i < doors.Count; i++)
        {
            var d = doors[i];
            if (d.Stairs || Vector3.DistanceSquared(d.Centre, at) > r2) continue;
            if (world.Entities.TryGetValue(d.Id, out var leaf)) _nearLoose.Add(leaf);
        }
    }

    /// <summary>
    /// The nearest thing along the ray that <paramref name="accept"/> admits, within
    /// <paramref name="range"/>, over what <see cref="Prepare"/> gathered and the index.
    /// </summary>
    public bool Cast(WorldSnapshot world, Vector3 origin, Vector3 dir, float range, Func<EntitySnapshot, bool> accept,
                     out EntitySnapshot hit, out float distance)
    {
        hit = default;
        distance = range;
        bool found = false;
        if (++_stampNow == int.MaxValue) { Array.Clear(_stamp); _stampNow = 1; }

        // From the triangle world: where the ray enters the first box the look stops at, or at once if it
        // starts inside one, as the box test does.
        if (_triangles && world.Geometry is { } geo)
        {
            var look = new Looks { World = world, Stops = accept, Stale = world.GeometryStale };
            if (geo.Enter(origin, dir, range, OpenFPS.Common.Geometry.GeometryLayers.Sight | OpenFPS.Common.Geometry.GeometryLayers.Announced,
                          ref look, out var h)
                && h.T < distance && world.Entities.TryGetValue(h.Owner, out var e))
            {
                distance = h.T; hit = e; found = true;
            }
        }

        foreach (int s in _nearSlabs) TestEntry(world, s, origin, dir, accept, ref hit, ref distance, ref found);
        for (int i = 0; i < _nearLoose.Count; i++)
        {
            var e = _nearLoose[i];
            if (!accept(e)) continue;
            if (Shape(e, origin, dir, range, out float d) && d < distance) { distance = d; hit = e; found = true; }
        }

        // Walk the cells the ray crosses, in order (a 2-D DDA over x and z).
        int cx = Cell(origin.X), cz = Cell(origin.Z);
        int stepX = dir.X > 0 ? 1 : -1, stepZ = dir.Z > 0 ? 1 : -1;
        float tDeltaX = MathF.Abs(dir.X) > 1e-9f ? CellMetres / MathF.Abs(dir.X) : float.PositiveInfinity;
        float tDeltaZ = MathF.Abs(dir.Z) > 1e-9f ? CellMetres / MathF.Abs(dir.Z) : float.PositiveInfinity;
        float nextX = MathF.Abs(dir.X) > 1e-9f
            ? ((stepX > 0 ? (cx + 1) * CellMetres : cx * CellMetres) - origin.X) / dir.X : float.PositiveInfinity;
        float nextZ = MathF.Abs(dir.Z) > 1e-9f
            ? ((stepZ > 0 ? (cz + 1) * CellMetres : cz * CellMetres) - origin.Z) / dir.Z : float.PositiveInfinity;
        for (int guard = 0; guard < 256; guard++)
        {
            if (_cells.TryGetValue(Key(cx, cz), out var list))
                for (int i = 0; i < list.Count; i++)
                {
                    int idx = list[i];
                    if (_stamp[idx] == _stampNow) continue;
                    _stamp[idx] = _stampNow;
                    TestEntry(world, idx, origin, dir, accept, ref hit, ref distance, ref found);
                }
            float exit = MathF.Min(nextX, nextZ);
            if (exit >= distance || exit >= range) break;
            if (nextX < nextZ) { cx += stepX; nextX += tDeltaX; }
            else { cz += stepZ; nextZ += tDeltaZ; }
        }
        return found;
    }

    /// <summary>What a look stops at, asked of a triangle's owner.</summary>
    private struct Looks : OpenFPS.Common.Geometry.IGeometryFilter
    {
        public WorldSnapshot World;
        public Func<EntitySnapshot, bool> Stops;
        public IReadOnlySet<int>? Stale;
        public readonly bool Accept(int owner, in OpenFPS.Common.Geometry.Surface surface)
            => (Stale == null || !Stale.Contains(owner)) && World.Entities.TryGetValue(owner, out var e) && Seen(e) && Stops(e);
    }

    private void TestEntry(WorldSnapshot world, int idx, Vector3 origin, Vector3 dir, Func<EntitySnapshot, bool> accept,
                           ref EntitySnapshot hit, ref float distance, ref bool found)
    {
        var en = _entries[idx];
        if (!BoxHit(origin, dir, en.Min, en.Max, distance)) return;
        if (!world.Entities.TryGetValue(en.Id, out var e) || !accept(e)) return;
        if (Shape(e, origin, dir, distance, out float d) && d < distance) { distance = d; hit = e; found = true; }
    }

    /// <summary>Whether the ray meets the box before <paramref name="limit"/>.</summary>
    private static bool BoxHit(Vector3 o, Vector3 d, Vector3 min, Vector3 max, float limit)
    {
        float tmin = 0f, tmax = limit;
        for (int a = 0; a < 3; a++)
        {
            float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z;
            float da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            float lo = a == 0 ? min.X : a == 1 ? min.Y : min.Z;
            float hi = a == 0 ? max.X : a == 1 ? max.Y : max.Z;
            if (MathF.Abs(da) < 1e-9f) { if (oa < lo || oa > hi) return false; continue; }
            float t1 = (lo - oa) / da, t2 = (hi - oa) / da;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tmin) tmin = t1;
            if (t2 < tmax) tmax = t2;
            if (tmin > tmax) return false;
        }
        return true;
    }

    /// <summary>The shape test, as SpatialService.RaycastSingle makes it.</summary>
    private static bool Shape(in EntitySnapshot e, Vector3 origin, Vector3 dir, float range, out float distance)
    {
        var def = e.Definition;
        var t = e.Transform;
        distance = range;
        if (def.Collider.Size.X <= 0) return false;
        bool hit;
        switch (def.Collider.Shape)
        {
            case OpenFPS.Common.Components.ColliderShape.Box:
                hit = GeometryUtils.RayIntersectsOBB(origin, dir, t.Position, def.Collider.Size, t.Rotation, out distance);
                break;
            case OpenFPS.Common.Components.ColliderShape.Sphere:
                hit = GeometryUtils.LineIntersectsSphere(origin, origin + dir * range, t.Position, def.Collider.Size.X / 2f);
                if (hit) distance = Vector3.Distance(origin, t.Position) - def.Collider.Size.X / 2f;
                break;
            case OpenFPS.Common.Components.ColliderShape.Cylinder:
            case OpenFPS.Common.Components.ColliderShape.Cone:
                hit = GeometryUtils.RayIntersectsCylinder(origin, dir, t.Position, def.Collider.Size.X / 2f, def.Collider.Size.Y, out distance);
                break;
            default:
                return false;
        }
        return hit && distance >= 0f;
    }
}
