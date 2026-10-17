using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Geometry;
using OpenFPS.Acoustics;

namespace OpenFPS.Client.Core;

/// <summary>
/// The client's geometry queries: rays and sight for prediction and the game, and the acoustics' questions
/// (occlusion, the room a point is in, the first surface along a ray), which the sound library answers over the
/// snapshot as its world input (<see cref="AcousticRays"/>).
/// </summary>
public class SpatialService
{
    public int OwnEntityId { get; set; } = -1;

    // Queried several times per ray, per source, per frame, from the game thread and the acoustic worker
    // at once: hence per-thread scratch, with no allocation, lock or shared state.
    [ThreadStatic] private static List<EntitySnapshot>? _candidates;

    /// <summary>
    /// The entities a query at <paramref name="center"/> could possibly hit, each once (WorldSnapshot.EntitiesNear).
    ///
    /// The returned list is per-thread scratch, valid until this thread calls back in: no caller may nest
    /// a second query inside a walk of the first.
    /// </summary>
    private static List<EntitySnapshot> GetEntitiesToTest(WorldSnapshot world, Vector3 center, float radius, bool staticOnly = false)
    {
        var candidates = _candidates ??= new List<EntitySnapshot>(64);
        world.EntitiesNear(center, radius, staticOnly, candidates);
        return candidates;
    }

    // ═══ The triangle world (docs/GEOMETRY.md stage 1) ═══════════════════════════════════════════
    //
    // With a triangle world the static solids are asked of it; only what it does not hold (what moves,
    // and the Unindexed statics) goes through the per-entity box tests. AudioLab --geometry-parity lists
    // where the two paths differ.

    [ThreadStatic] private static List<EntitySnapshot>? _others;

    /// <summary>Whether this snapshot's static solids are answered by its triangle world.</summary>
    public static bool UsesTriangles(WorldSnapshot world) => AcousticRays.UsesTriangles(world);

    /// <summary>What the triangle world does not answer for: what moves (unless <paramref name="staticOnly"/>)
    /// and the statics it does not hold.</summary>
    private static List<EntitySnapshot> Others(WorldSnapshot world, bool staticOnly)
    {
        var list = _others ??= new List<EntitySnapshot>(32);
        list.Clear();
        foreach (int id in world.UnindexedStatics)
            if (world.Entities.TryGetValue(id, out var s)) list.Add(s);
        if (!staticOnly) list.AddRange(world.DynamicEntities);
        return list;
    }

    /// <summary>Counts what the box path counted: not the stale (rebuilding) and not up to two entities.</summary>
    private struct StaticFilter : OpenFPS.Geometry.IGeometryFilter
    {
        public IReadOnlySet<int>? Stale;
        public int A, B;
        public readonly bool Accept(int owner, in OpenFPS.Geometry.Surface surface)
            => owner != A && owner != B && (Stale == null || !Stale.Contains(owner));
    }

    /// <summary>The box path's predicates over EntitySnapshots, asked of a triangle's owner.</summary>
    private struct PredicateFilter : OpenFPS.Geometry.IGeometryFilter
    {
        public StaticFilter Base;
        public WorldSnapshot World;
        public Func<EntitySnapshot, bool>? Accepts;
        public Func<EntitySnapshot, bool>? Skips;
        public bool GlassBlocks;
        public bool SkipGlass;
        public readonly bool Accept(int owner, in OpenFPS.Geometry.Surface surface)
        {
            if (!Base.Accept(owner, surface)) return false;
            if (SkipGlass && !GlassBlocks && surface.Is(OpenFPS.Geometry.SurfaceFlags.Glass)) return false;
            if (Accepts == null && Skips == null) return true;
            if (!World.Entities.TryGetValue(owner, out var e)) return false;
            if (Accepts != null && !Accepts(e)) return false;
            if (Skips != null && Skips(e)) return false;
            return true;
        }
    }

    private static StaticFilter Filter(WorldSnapshot world, int a = int.MinValue, int b = int.MinValue)
        => new() { Stale = world.GeometryStale, A = a, B = b };

    // ═══ The acoustics' questions, answered by the library (AcousticRays) ═════════════════════════

    /// <summary>How blocked the line between two points is, 0 to 1, and the bleed (the mean of what the
    /// bands let through).</summary>
    public float GetOcclusionFactor(WorldSnapshot world, Vector3 start, Vector3 end, out float bleed, int ignoreEntityId = -1, int ignoreEntityId2 = -1)
        => AcousticRays.OcclusionFactor(world, start, end, out bleed, ignoreEntityId, ignoreEntityId2);

    /// <summary><see cref="GetOcclusionData"/> averaged over five rays in a cross round the end point
    /// (<see cref="AcousticRays.MultiPointOcclusion"/>).</summary>
    public void GetMultiPointOcclusionData(WorldSnapshot world, Vector3 start, Vector3 end, out float maxBlock, out float cumulativeBleed, out float eqLow, out float eqMid, out float eqHigh, int ignoreEntityId = -1, int ignoreEntityId2 = -1)
        => AcousticRays.MultiPointOcclusion(world, start, end, out maxBlock, out cumulativeBleed, out eqLow, out eqMid, out eqHigh, ignoreEntityId, ignoreEntityId2);

    /// <summary>What every wall between two points lets through, per band (<see cref="AcousticRays.Occlusion"/>).</summary>
    public void GetOcclusionData(WorldSnapshot world, Vector3 start, Vector3 end, out float maxBlock, out float cumulativeBleed, out float eqLow, out float eqMid, out float eqHigh, int ignoreEntityId = -1, int ignoreEntityId2 = -1)
        => AcousticRays.Occlusion(world, start, end, out maxBlock, out cumulativeBleed, out eqLow, out eqMid, out eqHigh, ignoreEntityId, ignoreEntityId2);

    /// <summary>The first solid along each of <paramref name="directions"/> (<see cref="AcousticRays.FirstSurfaces"/>).</summary>
    public void RaycastAll(WorldSnapshot world, Vector3 start, Vector3[] directions, float maxDist,
                           float[] distances, float[] absorptions, string[] materials, bool staticOnly = false,
                           Vector3[]? normals = null, int count = -1)
        => AcousticRays.FirstSurfaces(world, start, directions, maxDist, distances, absorptions, materials, staticOnly, normals, count);

    /// <summary>The acoustic region a position is in (<see cref="AcousticRays.RegionAt"/>).</summary>
    public int GetRegionAt(WorldSnapshot world, Vector3 position) => AcousticRays.RegionAt(world, position);

    /// <summary>The first solid along a ray and its material, this client's own body and
    /// <paramref name="ignoreEntityId"/> not counted (<see cref="AcousticRays.FirstMaterial"/>).</summary>
    public bool RaycastMaterial(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, out float distance, out Vector3 normal, out string material, int ignoreEntityId = -1)
        => AcousticRays.FirstMaterial(world, start, dir, maxDist, out distance, out normal, out material, ignoreEntityId, OwnEntityId);

    /// <summary>
    /// The first solid thing along a line of sight, out to <paramref name="maxDist"/>: what a scope sees,
    /// or what stops it seeing further.
    ///
    /// Without triangles the static grid is walked in 40 m stretches, stopping at the first with a hit:
    /// asking for half a 600 m ray round its middle is the whole city. Players are not solid to sight
    /// here (the scope finds them itself), and glass is seen through unless <paramref name="glassBlocks"/>.
    /// </summary>
    public bool CastSight(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, out EntitySnapshot hitEntity,
                          out float hitDistance, Func<EntitySnapshot, bool>? skip = null, bool glassBlocks = false)
    {
        hitEntity = default;
        hitDistance = maxDist;
        bool found = false;
        dir = Vector3.Normalize(dir);

        bool Test(in EntitySnapshot e, ref float best)
        {
            if (e.Id == OwnEntityId) return false;
            var def = e.Definition;
            if (def.Collider.Size.X <= 0f || !def.Collider.IsSolid) return false;
            if (def.Type is EntityType.Player) return false;
            if (!glassBlocks && AcousticRegistry.IsGlass(def.Material.Material)) return false;
            if (skip != null && skip(e)) return false;
            float d;
            bool hit;
            var t = e.Transform;
            switch (def.Collider.Shape)
            {
                case ColliderShape.Box:
                    hit = GeometryUtils.RayIntersectsOBB(start, dir, t.Position, def.Collider.Size, t.Rotation, out d);
                    break;
                case ColliderShape.Sphere:
                    hit = GeometryUtils.RayIntersectsSphere(start, dir, t.Position, def.Collider.Size.X * 0.5f, out d, out _);
                    break;
                case ColliderShape.Cylinder:
                case ColliderShape.Cone:
                    hit = GeometryUtils.RayIntersectsCylinder(start, dir, t.Position, def.Collider.Size.X * 0.5f, def.Collider.Size.Y, out d);
                    break;
                default:
                    return false;
            }
            if (!hit || d < 0f || d >= best) return false;
            best = d;
            return true;
        }

        if (UsesTriangles(world))
        {
            var geo = world.Geometry!;
            var filter = new PredicateFilter
            {
                Base = Filter(world, OwnEntityId), World = world, Skips = skip, GlassBlocks = glassBlocks, SkipGlass = true,
            };
            if (geo.Enter(start, dir, maxDist, OpenFPS.Geometry.GeometryLayers.Sight, ref filter, out var h)
                && h.T < hitDistance && world.Entities.TryGetValue(h.Owner, out var e))
            {
                hitDistance = h.T; hitEntity = e; found = true;
            }
            var others = Others(world, staticOnly: true);
            for (int i = 0; i < others.Count; i++)
                if (Test(others[i], ref hitDistance)) { hitEntity = others[i]; found = true; }
        }
        else
        {
            const float Stretch = 40f;
            for (float from = 0f; from < maxDist && from < hitDistance; from += Stretch)
            {
                float to = MathF.Min(maxDist, from + Stretch);
                var candidates = GetEntitiesToTest(world, start + dir * (0.5f * (from + to)), 0.5f * (to - from) + 2f, staticOnly: true);
                for (int i = 0; i < candidates.Count; i++)
                    if (Test(candidates[i], ref hitDistance)) { hitEntity = candidates[i]; found = true; }
            }
        }
        for (int i = 0; i < world.DynamicEntities.Count; i++)
        {
            var e = world.DynamicEntities[i];
            if (Test(e, ref hitDistance)) { hitEntity = e; found = true; }
        }
        return found;
    }

    /// <summary>What the first surface a look meets along a line is (<see cref="OpenFPS.Geometry.SurfaceFlags"/>),
    /// glass included; none without the triangle world, or where it meets nothing.</summary>
    public OpenFPS.Geometry.SurfaceFlags SurfaceFlagsAlong(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist)
    {
        if (!UsesTriangles(world)) return OpenFPS.Geometry.SurfaceFlags.None;
        var geo = world.Geometry!;
        var filter = new PredicateFilter { Base = Filter(world, OwnEntityId), World = world, GlassBlocks = true, SkipGlass = true };
        return geo.Enter(start, Vector3.Normalize(dir), maxDist, OpenFPS.Geometry.GeometryLayers.Sight, ref filter, out var h)
            ? geo.SurfaceOf(h).Flags : OpenFPS.Geometry.SurfaceFlags.None;
    }

    /// <summary>Whether nothing a look meets is over <paramref name="at"/> for <paramref name="metres"/>, glass included:
    /// the open sky. Inside a solid (a roof's own volume) is under it.</summary>
    public bool UnderOpenSky(WorldSnapshot world, Vector3 at, float metres)
    {
        if (!UsesTriangles(world)) return !CastSight(world, at, Vector3.UnitY, metres, out _, out _, glassBlocks: true);
        var filter = new PredicateFilter { Base = Filter(world, OwnEntityId), World = world, GlassBlocks = true, SkipGlass = true };
        if (world.Geometry!.Any(at, Vector3.UnitY, metres, OpenFPS.Geometry.GeometryLayers.Sight, OpenFPS.Geometry.RayFaces.Both, ref filter))
            return false;
        return !CastSight(world, at, Vector3.UnitY, metres, out _, out _, glassBlocks: true);
    }

    public bool RaycastSingle(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, out EntitySnapshot hitEntity, out float hitDistance)
        => RaycastSingle(world, start, dir, maxDist, null, out hitEntity, out hitDistance);

    /// <summary>The nearest thing along a ray that <paramref name="accept"/> admits; everything else is
    /// looked straight through.</summary>
    public bool RaycastSingle(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, Func<EntitySnapshot, bool>? accept,
                              out EntitySnapshot hitEntity, out float hitDistance)
    {
        hitDistance = maxDist;
        hitEntity = default;
        bool found = false;

        Vector3 center = start + (dir * (maxDist / 2.0f));
        List<EntitySnapshot> entitiesToTest;
        if (UsesTriangles(world))
        {
            var geo = world.Geometry!;
            var filter = new PredicateFilter { Base = Filter(world), World = world, Accepts = accept };
            if (geo.Enter(start, dir, maxDist, OpenFPS.Geometry.GeometryLayers.Physical, ref filter, out var h)
                && h.T < hitDistance && world.Entities.TryGetValue(h.Owner, out var e))
            {
                hitDistance = h.T; hitEntity = e; found = true;
            }
            entitiesToTest = Others(world, staticOnly: false);
        }
        else entitiesToTest = GetEntitiesToTest(world, center, (maxDist / 2.0f) + 1.0f);

        foreach (var entitySnap in entitiesToTest)
        {
            var def = entitySnap.Definition;
            if (def.Collider.Size.X <= 0) continue;
            if (accept != null && !accept(entitySnap)) continue;
            var transform = entitySnap.Transform;
            bool intersected = false;
            float dist = maxDist;

            if (def.Collider.Shape == ColliderShape.Box)
            {
                intersected = GeometryUtils.RayIntersectsOBB(start, dir, transform.Position, def.Collider.Size, transform.Rotation, out dist);
            }
            else
            {
                intersected = def.Collider.Shape switch
                {
                    ColliderShape.Sphere => GeometryUtils.LineIntersectsSphere(start, start + dir * maxDist, transform.Position, def.Collider.Size.X / 2.0f),
                    ColliderShape.Cylinder or ColliderShape.Cone => GeometryUtils.RayIntersectsCylinder(start, dir, transform.Position, def.Collider.Size.X / 2.0f, def.Collider.Size.Y, out dist),
                    _ => false
                };
                if (intersected && def.Collider.Shape == ColliderShape.Sphere) dist = Vector3.Distance(start, transform.Position) - (def.Collider.Size.X / 2f);
            }

            if (intersected && dist >= 0 && dist < hitDistance)
            {
                hitDistance = dist;
                hitEntity = entitySnap;
                found = true;
            }
        }
        return found;
    }
}

/// <summary>Where the listener stands, for naming it: the game's question of the sound's places.</summary>
public static class Zones
{
    /// <summary>The place a point is in by the boxes alone (a doorway is in none), for naming where the
    /// listener stands, not for sound. A named part of a room (a flight of stairs, a landing) answers
    /// here; it is not a room, so the acoustics' <c>GetRegionAt</c> never does.</summary>
    public static int GetZoneAt(this OpenFPS.Audio.Acoustics.SpatialAcoustics acoustics, WorldSnapshot world, Vector3 position)
        => NamedPlaces.At(world, position) ?? acoustics.GetRoomAt(world, position);
}