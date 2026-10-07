using System;
using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common.Components;

namespace OpenFPS.Common;

/// <summary>
/// Shared physics utilities to ensure client-server parity for grounding and collision.
/// </summary>
public static class PhysicsUtils
{
    // The candidate gather runs on the server tick thread, the client game thread and the acoustic worker
    // thread. Per-thread scratch keeps it allocation-free without any of them waiting on the others.
    [ThreadStatic] private static List<Entity>? _entityScratch;
    [ThreadStatic] private static HashSet<Entity>? _entitySeen;
    [ThreadStatic] private static List<int>? _idScratch;
    [ThreadStatic] private static HashSet<int>? _idSeen;
    [ThreadStatic] private static List<EntitySnapshot>? _snapshotScratch;

    /// <summary>
    /// Performs a standardized vertical probe to find the floor height at a given position.
    /// Uses a "Thin Probe" (0.1m footprint) to prevent standing on walls.
    /// Version for Server (using Arch World).
    ///
    /// This is the single most-run query on the server — once per input, not once per tick — so callers
    /// with a <see cref="GroundProbeMemo"/> should go through
    /// <see cref="GetGroundHeight(World, SpatialGrid{Entity}, Vector3, ref GroundProbeMemo, out string)"/>
    /// instead and pay for it only when the answer can actually have changed.
    /// </summary>
    public static float GetGroundHeight(World world, SpatialGrid<Entity> grid, Vector3 pos, out string material)
        => GetGroundHeight(world, grid, pos, null, out material);

    /// <summary>
    /// The ground under a point, not counting <paramref name="ignore"/>.
    ///
    /// A driven vehicle asks where the road is under it, and its own floor is standing right there,
    /// a quarter of a metre up and inside the step height. Counted, it is "ground": the car steps up
    /// onto its own floor, and does it again the next tick from there, and climbs out of the world.
    /// </summary>
    public static float GetGroundHeight(World world, SpatialGrid<Entity> grid, Vector3 pos,
                                        ICollection<Entity>? ignore, out string material)
    {
        const float stepHeight = 0.4f;

        if (grid.Geometry != null && Geometry.TriangleGeometry.Enabled)
            return GroundFromGeometry(world, grid, pos, ignore, stepHeight, out material);

        // 1. Optimized Grid Search. CollectInRadius walks the cells ONCE and yields each candidate once;
        //    the old ToList() over the iterator handed a multi-cell floor back as many times as it spanned
        //    cells, and every one of those repeats was then tested against all five probe points.
        var candidates = _entityScratch ??= new List<Entity>(64);
        var seen = _entitySeen ??= new HashSet<Entity>();
        grid.CollectInRadius(pos, 50.0f, candidates, seen);
        if (ignore != null && ignore.Count > 0) candidates.RemoveAll(ignore.Contains);

        float ground = CalculateHeightFromCandidates(world, candidates, pos, stepHeight, out material);

        // 2. REAL SOLUTION: Exhaustive Fallback
        // If the grid search missed the floor (e.g. edge case or grid stale),
        // perform a global scan of all solid entities to guarantee grounding.
        if (ground < -900f)
        {
            var allStatic = new List<Entity>();
            world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e) => {
                if (ignore == null || !ignore.Contains(e)) allStatic.Add(e);
            });
            ground = CalculateHeightFromCandidates(world, allStatic, pos, stepHeight, out material);
        }

        return ground;
    }

    /// <summary>Counts every solid but those of the entities in <paramref name="Ignore"/>.</summary>
    private readonly struct IgnoreEntities : Geometry.IGeometryFilter
    {
        private readonly ICollection<Entity>? _ignore;
        public IgnoreEntities(ICollection<Entity>? ignore) => _ignore = ignore is { Count: > 0 } ? ignore : null;
        public bool Accept(int owner, in Geometry.Surface surface)
        {
            if (_ignore == null) return true;
            foreach (var e in _ignore) if (e.Id == owner) return false;
            return true;
        }
    }

    /// <summary>
    /// The server ground probe on the triangle world (docs/GEOMETRY.md 3.3): the static solids from it, and
    /// what moves and what it does not hold the old way; the higher of the two. Five points, the highest
    /// top no higher than a step above the feet, exactly as <see cref="CalculateHeightFromCandidates"/>.
    /// </summary>
    private static float GroundFromGeometry(World world, SpatialGrid<Entity> grid, Vector3 pos, ICollection<Entity>? ignore,
                                            float stepHeight, out string material)
    {
        var filter = new IgnoreEntities(ignore);
        var geo = grid.Geometry!;
        float ground = geo.Ground(pos, PhysicsConstants.PlayerRadius, stepHeight, Geometry.GeometryLayers.Ground, ref filter, out var solid, out _);
        material = ground > -1000f ? GroundMaterial(geo.SurfaceOf(solid).Material) : "Generic";

        var candidates = _entityScratch ??= new List<Entity>(64);
        var seen = _entitySeen ??= new HashSet<Entity>();
        grid.CollectDynamicInRadius(pos, 50.0f, candidates, seen);
        if (ignore != null && ignore.Count > 0) candidates.RemoveAll(ignore.Contains);
        float other = CalculateHeightFromCandidates(world, candidates, pos, stepHeight, out string otherMaterial);
        if (other > ground) { ground = other; material = otherMaterial; }

        if (ground < -900f)
        {
            // As the box path: nothing found near, so everything is asked (a body far out on a vehicle).
            var allStatic = new List<Entity>();
            world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e) => {
                if (ignore == null || !ignore.Contains(e)) allStatic.Add(e);
            });
            ground = CalculateHeightFromCandidates(world, allStatic, pos, stepHeight, out material);
        }
        return ground;
    }

    /// <summary>The material a floor reports: the server's box path read the component as it was.</summary>
    private static string GroundMaterial(string material) => material;

    /// <summary>
    /// The memoized form of the server ground probe: recomputes only when the static geometry has changed,
    /// the probe point has moved, or the remembered answer has aged out. See <see cref="GroundProbeMemo"/>
    /// for why each of those three is a condition and what the age bound costs.
    /// </summary>
    public static float GetGroundHeight(World world, SpatialGrid<Entity> grid, Vector3 pos,
        ref GroundProbeMemo memo, out string material)
    {
        long nowMs = Environment.TickCount64;
        if (memo.TryGet(pos, grid.StaticVersion, nowMs, out float cached, out material))
        {
            PerfProbe.Count("server.groundprobe.hit");
            return cached;
        }

        using (PerfProbe.Measure("server.groundprobe"))
        {
            float ground = GetGroundHeight(world, grid, pos, out material);
            memo.Store(pos, grid.StaticVersion, nowMs, ground, material);
            return ground;
        }
    }

    private static float CalculateHeightFromCandidates(World world, IEnumerable<Entity> candidates, Vector3 pos, float stepHeight, out string material)
    {
        float bestY = -1000f;
        material = "Generic";
        string bestMat = "Generic";

        // Probing points: Center + 4 points at the edge of the player radius
        const float radius = PhysicsConstants.PlayerRadius;
        Span<Vector3> probes = stackalloc Vector3[5];
        probes[0] = pos;
        probes[1] = pos + new Vector3(radius, 0, 0);
        probes[2] = pos + new Vector3(-radius, 0, 0);
        probes[3] = pos + new Vector3(0, 0, radius);
        probes[4] = pos + new Vector3(0, 0, -radius);

        foreach (var e in candidates)
        {
            // Gone from the world but still in the grid: asking it anything throws (2026-09-28, 92
            // "failed to move" errors from the parked cars after something was removed).
            if (!world.IsAlive(e) || !world.Has<Transform>(e) || !world.Has<ColliderComponent>(e)) continue;

            ref var t = ref world.Get<Transform>(e);
            ref var c = ref world.Get<ColliderComponent>(e);
            if (!c.IsSolid) continue;

            float objTop = t.Position.Y + (c.Size.Y / 2f);
            if (objTop > pos.Y + stepHeight) continue;

            // Check if any of our probe points are within this object's footprint
            bool hit = false;
            foreach (var p in probes)
            {
                if (GeometryUtils.IsPointInOBB(p, t.Position, new Vector3(c.Size.X, 40000f, c.Size.Z), SharedMovementEngine.StandingRotation(c.Shape, t.Rotation)))
                {
                    hit = true;
                    break;
                }
            }

            if (hit && objTop > bestY)
            {
                bestY = objTop;
                bestMat = world.Has<MaterialComponent>(e) ? world.Get<MaterialComponent>(e).Material : "Generic";
            }
        }

        material = bestMat;
        return bestY;
    }

    /// <summary>
    /// Performs a standardized vertical probe to find the floor height at a given position.
    /// Version for Client (using WorldSnapshot).
    /// </summary>
    public static float GetGroundHeight(WorldSnapshot snapshot, Vector3 pos, int ownEntityId, out string material)
    {
        const float stepHeight = 0.4f;

        if (snapshot.Geometry != null && Geometry.TriangleGeometry.Enabled)
        {
            // The triangle world for the static solids (docs/GEOMETRY.md 3.3), the old test for the statics
            // it does not hold; nothing that moves, as the box path counted only the static grid.
            var filter = new SnapshotGround { Stale = snapshot.GeometryStale, Own = ownEntityId };
            var geo = snapshot.Geometry;
            float y = geo.Ground(pos, PhysicsConstants.PlayerRadius, stepHeight, Geometry.GeometryLayers.Ground, ref filter, out var solid, out _);
            string m = "Generic";
            if (y > -1000f) { var raw = geo.SurfaceOf(solid).Material; m = string.IsNullOrEmpty(raw) ? "Generic" : raw; }
            if (snapshot.UnindexedStatics.Count > 0)
            {
                var others = _snapshotScratch ??= new List<EntitySnapshot>(64);
                others.Clear();
                foreach (int id in snapshot.UnindexedStatics)
                    if (snapshot.Entities.TryGetValue(id, out var s)) others.Add(s);
                float o = CalculateHeightFromSnapshots(others, pos, stepHeight, ownEntityId, out string om);
                if (o > y) { y = o; m = om; }
            }
            if (y < -900f && snapshot.Entities.Count > 0)
                y = CalculateHeightFromSnapshots(snapshot.Entities.Values, pos, stepHeight, ownEntityId, out m);
            material = m;
            return y;
        }

        // 1. Optimized Grid Search — one walk of the cells, each candidate once (see the server version).
        var candidates = _snapshotScratch ??= new List<EntitySnapshot>(64);
        candidates.Clear();

        if (snapshot.StaticGrid != null)
        {
            var ids = _idScratch ??= new List<int>(64);
            var seen = _idSeen ??= new HashSet<int>();
            snapshot.StaticGrid.CollectInRadius(pos, 50.0f, ids, seen);
            for (int i = 0; i < ids.Count; i++)
                if (snapshot.Entities.TryGetValue(ids[i], out var snap)) candidates.Add(snap);
        }

        float ground = candidates.Count > 0
            ? CalculateHeightFromSnapshots(candidates, pos, stepHeight, ownEntityId, out material)
            : CalculateHeightFromSnapshots(snapshot.Entities.Values, pos, stepHeight, ownEntityId, out material);

        // 2. REAL SOLUTION: Exhaustive Fallback
        if (ground < -900f && snapshot.Entities.Count > 0)
        {
            ground = CalculateHeightFromSnapshots(snapshot.Entities.Values, pos, stepHeight, ownEntityId, out material);
        }

        return ground;
    }

    /// <summary>
    /// Where a foot goes down, and on what (docs/GEOMETRY.md 3.8): the floor under the foot, not the body's.
    /// The body stands at the highest floor anywhere under its footprint, so on a flight it is already at
    /// the next tread's height while its middle is over the one below, and a foot put down under its middle
    /// was in the air over the lower tread. The foot goes on the floor the body is standing on: under the
    /// foot if that is the body's height, or a body's radius ahead along <paramref name="way"/> (the tread
    /// it is stepping up onto). Nothing found at the body's height (a body on a vehicle's floor, a floor the
    /// triangles do not hold) leaves the foot where it was and <paramref name="material"/> null.
    /// </summary>
    public static Vector3 FootOnFloor(WorldSnapshot snapshot, Vector3 foot, Vector3 feet, Vector3 way, int ownEntityId, out string? material)
    {
        material = null;
        var geo = snapshot.Geometry;
        if (geo == null || !Geometry.TriangleGeometry.Enabled) return foot;
        var filter = new SnapshotGround { Stale = snapshot.GeometryStale, Own = ownEntityId };
        return FootOnFloor(geo, ref filter, foot, feet, way, out material);
    }

    /// <summary><see cref="FootOnFloor(WorldSnapshot, Vector3, Vector3, Vector3, int, out string?)"/> asked of a
    /// triangle world directly (the server's, a lab's).</summary>
    public static Vector3 FootOnFloor<F>(Geometry.TriangleWorld geo, ref F filter, Vector3 foot, Vector3 feet, Vector3 way, out string? material)
        where F : Geometry.IGeometryFilter
    {
        material = null;
        float top = feet.Y + PhysicsConstants.StepHeight;
        var ahead = new Vector3(way.X, 0f, way.Z);
        ahead = ahead.LengthSquared() > 1e-8f ? Vector3.Normalize(ahead) * PhysicsConstants.PlayerRadius : Vector3.Zero;
        float under = geo.FloorAt(foot.X, foot.Z, top, Geometry.GeometryLayers.Ground, ref filter, out var underHit);
        // On the floor the body stands on, under the foot...
        if (under > -1000f && MathF.Abs(under - feet.Y) <= FootOnFloorTolerance) return On(geo, foot, under, underHit, out material);
        // ...or a stride ahead, the tread a body climbing a flight is stepping up onto...
        if (ahead != Vector3.Zero)
        {
            var at = foot + ahead;
            float y = geo.FloorAt(at.X, at.Z, top, Geometry.GeometryLayers.Ground, ref filter, out var hit);
            if (y > -1000f && MathF.Abs(y - feet.Y) <= FootOnFloorTolerance) return On(geo, at, y, hit, out material);
        }
        // ...or whatever is under the foot, within a step of the feet: going down a flight the body is held
        // at the tread it is leaving until its whole footprint is past it, and the foot is already on the
        // one below; stepping up, the body is lifted for a moment and the foot is still on the tread under it.
        if (under > -1000f && MathF.Abs(under - feet.Y) <= PhysicsConstants.StepHeight + 0.01f) return On(geo, foot, under, underHit, out material);
        return foot;

        static Vector3 On(Geometry.TriangleWorld geo, Vector3 at, float y, in Geometry.GeometryHit hit, out string? material)
        {
            var raw = geo.SurfaceOf(hit).Material;
            material = string.IsNullOrEmpty(raw) ? "Generic" : raw;
            return new Vector3(at.X, y, at.Z);
        }
    }

    /// <summary>How near the body's height a floor is to be the one it stands on, metres.</summary>
    public const float FootOnFloorTolerance = 0.002f;

    private struct SnapshotGround : Geometry.IGeometryFilter
    {
        public IReadOnlySet<int>? Stale;
        public int Own;
        public readonly bool Accept(int owner, in Geometry.Surface surface) => owner != Own && (Stale == null || !Stale.Contains(owner));
    }

    private static float CalculateHeightFromSnapshots(IEnumerable<EntitySnapshot> candidates, Vector3 pos, float stepHeight, int ownEntityId, out string material)
    {
        float bestY = -1000f;
        material = "Generic";
        string bestMat = "Generic";

        const float radius = PhysicsConstants.PlayerRadius;
        Span<Vector3> probes = stackalloc Vector3[5];
        probes[0] = pos;
        probes[1] = pos + new Vector3(radius, 0, 0);
        probes[2] = pos + new Vector3(-radius, 0, 0);
        probes[3] = pos + new Vector3(0, 0, radius);
        probes[4] = pos + new Vector3(0, 0, -radius);

        foreach (var entity in candidates)
        {
            if (entity.Id == ownEntityId) continue;
            var def = entity.Definition;
            if (!def.Collider.IsSolid) continue;

            float objTop = entity.Transform.Position.Y + (def.Collider.Size.Y / 2f);
            if (objTop > pos.Y + stepHeight) continue;

            bool hit = false;
            foreach (var p in probes)
            {
                if (GeometryUtils.IsPointInOBB(p, entity.Transform.Position, new Vector3(def.Collider.Size.X, 40000f, def.Collider.Size.Z), SharedMovementEngine.StandingRotation(def.Collider.Shape, entity.Transform.Rotation)))
                {
                    hit = true;
                    break;
                }
            }

            if (hit && objTop > bestY)
            {
                bestY = objTop;
                bestMat = string.IsNullOrEmpty(def.Material.Material) ? "Generic" : def.Material.Material;
            }
        }

        material = bestMat;
        return bestY;
    }
}
