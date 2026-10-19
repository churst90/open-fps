using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Geometry;
using OpenFPS.Acoustics;
using OpenFPS.Materials;

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
    // at once: hence per-thread scratch, with no allocation, lock or shared state. The body list is the
    // view's answer to Questions, the snapshot list the host's: the view is what the queries are written over
    // (stage 9), and the snapshot overloads below only unwrap their answers for the game's older callers.
    [ThreadStatic] private static List<AcousticBody>? _bodies;

    /// <summary>
    /// The things a query could possibly hit, into per-thread scratch valid until this thread calls back
    /// in: with a triangle world what it does not hold, without one what is filed round
    /// <paramref name="center"/>. No caller may nest a second query inside a walk of the first.
    /// </summary>
    private static List<AcousticBody> Bodies(IAcousticWorld world, bool triangles, Vector3 center, float radius, bool staticOnly = false)
    {
        var list = _bodies ??= new List<AcousticBody>(64);
        if (triangles) world.Unindexed(staticOnly, list);
        else world.Near(center, radius, staticOnly, list);
        return list;
    }

    // ═══ The triangle world (docs/GEOMETRY.md stage 1) ═══════════════════════════════════════════
    //
    // With a triangle world the static solids are asked of it; only what it does not hold (what moves,
    // and the Unindexed statics) goes through the per-body box tests. AudioLab --geometry-parity lists
    // where the two paths differ.

    /// <summary>Whether this world's static solids are answered by its triangle world.</summary>
    public static bool UsesTriangles(IAcousticWorld world) => AcousticRays.UsesTriangles(world);

    /// <summary>Counts what the box path counted: not the stale (rebuilding) and not up to two things.</summary>
    private struct StaticFilter : OpenFPS.Geometry.IGeometryFilter
    {
        public IReadOnlySet<int>? Stale;
        public int A, B;
        public readonly bool Accept(int owner, in OpenFPS.Geometry.Surface surface)
            => owner != A && owner != B && (Stale == null || !Stale.Contains(owner));
    }

    /// <summary>The box path's predicates over bodies, asked of a triangle's owner.</summary>
    private struct PredicateFilter : OpenFPS.Geometry.IGeometryFilter
    {
        public StaticFilter Base;
        public IAcousticWorld World;
        public Func<AcousticBody, bool>? Accepts;
        public Func<AcousticBody, bool>? Skips;
        public bool GlassBlocks;
        public bool SkipGlass;
        public readonly bool Accept(int owner, in OpenFPS.Geometry.Surface surface)
        {
            if (!Base.Accept(owner, surface)) return false;
            if (SkipGlass && !GlassBlocks && surface.Is(OpenFPS.Geometry.SurfaceFlags.Glass)) return false;
            if (Accepts == null && Skips == null) return true;
            if (!World.TryGetBody(owner, out var b)) return false;
            if (Accepts != null && !Accepts(b)) return false;
            if (Skips != null && Skips(b)) return false;
            return true;
        }
    }

    private static StaticFilter Filter(IAcousticWorld world, int a = int.MinValue, int b = int.MinValue)
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
    public static bool CastSight(IAcousticWorld world, Vector3 start, Vector3 dir, float maxDist, out AcousticBody hitBody,
                                 out float hitDistance, Func<AcousticBody, bool>? skip = null, bool glassBlocks = false, int ownEntityId = -1)
    {
        hitBody = default;
        hitDistance = maxDist;
        bool found = false;
        dir = Vector3.Normalize(dir);

        bool Test(in AcousticBody b, ref float best)
        {
            if (b.Id == ownEntityId) return false;
            if (b.Size.X <= 0f || !b.Solid) return false;
            if (b.Kind is BodyKind.Player) return false;
            if (!glassBlocks && AcousticRegistry.IsGlass(b.Material)) return false;
            if (skip != null && skip(b)) return false;
            float d;
            bool hit;
            switch (b.Shape)
            {
                case ColliderShape.Box:
                    hit = GeometryUtils.RayIntersectsOBB(start, dir, b.Position, b.Size, b.Rotation, out d);
                    break;
                case ColliderShape.Sphere:
                    hit = GeometryUtils.RayIntersectsSphere(start, dir, b.Position, b.Size.X * 0.5f, out d, out _);
                    break;
                case ColliderShape.Cylinder:
                case ColliderShape.Cone:
                    hit = GeometryUtils.RayIntersectsCylinder(start, dir, b.Position, b.Size.X * 0.5f, b.Size.Y, out d);
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
                Base = Filter(world, ownEntityId), World = world, Skips = skip, GlassBlocks = glassBlocks, SkipGlass = true,
            };
            if (geo.Enter(start, dir, maxDist, OpenFPS.Geometry.GeometryLayers.Sight, ref filter, out var h)
                && h.T < hitDistance && world.TryGetBody(h.Owner, out var owner))
            {
                hitDistance = h.T; hitBody = owner; found = true;
            }
            var others = Bodies(world, triangles: true, default, 0f, staticOnly: true);
            for (int i = 0; i < others.Count; i++)
                if (Test(others[i], ref hitDistance)) { hitBody = others[i]; found = true; }
        }
        else
        {
            const float Stretch = 40f;
            for (float from = 0f; from < maxDist && from < hitDistance; from += Stretch)
            {
                float to = MathF.Min(maxDist, from + Stretch);
                var candidates = Bodies(world, triangles: false, start + dir * (0.5f * (from + to)), 0.5f * (to - from) + 2f, staticOnly: true);
                for (int i = 0; i < candidates.Count; i++)
                    if (Test(candidates[i], ref hitDistance)) { hitBody = candidates[i]; found = true; }
            }
        }
        for (int i = 0; i < world.MovingCount; i++)
        {
            var b = world.MovingAt(i);
            if (Test(b, ref hitDistance)) { hitBody = b; found = true; }
        }
        return found;
    }

    /// <summary>The game's own body and snapshots: <see cref="CastSight(IAcousticWorld, Vector3, Vector3, float, out AcousticBody, out float, Func{AcousticBody, bool}?, bool, int)"/>
    /// unwrapped. The scope and the turn narration read it.</summary>
    public bool CastSight(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, out EntitySnapshot hitEntity,
                          out float hitDistance, Func<EntitySnapshot, bool>? skip = null, bool glassBlocks = false)
    {
        Func<AcousticBody, bool>? bodySkip = skip == null ? null : b => world.Entities.TryGetValue(b.Id, out var e) && skip(e);
        bool found = CastSight(world, start, dir, maxDist, out var body, out hitDistance, bodySkip, glassBlocks, OwnEntityId);
        hitEntity = found && world.Entities.TryGetValue(body.Id, out var hit) ? hit : default;
        return found;
    }

    /// <summary>What the first surface a look meets along a line is (<see cref="OpenFPS.Geometry.SurfaceFlags"/>),
    /// glass included; none without the triangle world, or where it meets nothing.</summary>
    public static OpenFPS.Geometry.SurfaceFlags SurfaceFlagsAlong(IAcousticWorld world, Vector3 start, Vector3 dir, float maxDist, int ownEntityId = -1)
    {
        if (!UsesTriangles(world)) return OpenFPS.Geometry.SurfaceFlags.None;
        var geo = world.Geometry!;
        var filter = new PredicateFilter { Base = Filter(world, ownEntityId), World = world, GlassBlocks = true, SkipGlass = true };
        return geo.Enter(start, Vector3.Normalize(dir), maxDist, OpenFPS.Geometry.GeometryLayers.Sight, ref filter, out var h)
            ? geo.SurfaceOf(h).Flags : OpenFPS.Geometry.SurfaceFlags.None;
    }

    /// <summary>The game's own: <see cref="SurfaceFlagsAlong(IAcousticWorld, Vector3, Vector3, float, int)"/>.</summary>
    public OpenFPS.Geometry.SurfaceFlags SurfaceFlagsAlong(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist)
        => SurfaceFlagsAlong(world, start, dir, maxDist, OwnEntityId);

    /// <summary>Whether nothing a look meets is over <paramref name="at"/> for <paramref name="metres"/>, glass included:
    /// the open sky. Inside a solid (a roof's own volume) is under it.</summary>
    public static bool UnderOpenSky(IAcousticWorld world, Vector3 at, float metres, int ownEntityId = -1)
    {
        if (!UsesTriangles(world))
            return !CastSight(world, at, Vector3.UnitY, metres, out _, out _, glassBlocks: true, ownEntityId: ownEntityId);
        var filter = new PredicateFilter { Base = Filter(world, ownEntityId), World = world, GlassBlocks = true, SkipGlass = true };
        if (world.Geometry!.Any(at, Vector3.UnitY, metres, OpenFPS.Geometry.GeometryLayers.Sight, OpenFPS.Geometry.RayFaces.Both, ref filter))
            return false;
        return !CastSight(world, at, Vector3.UnitY, metres, out _, out _, glassBlocks: true, ownEntityId: ownEntityId);
    }

    /// <summary>The game's own: <see cref="UnderOpenSky(IAcousticWorld, Vector3, float, int)"/>.</summary>
    public bool UnderOpenSky(WorldSnapshot world, Vector3 at, float metres) => UnderOpenSky(world, at, metres, OwnEntityId);

    public bool RaycastSingle(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, out EntitySnapshot hitEntity, out float hitDistance)
        => RaycastSingle(world, start, dir, maxDist, null, out hitEntity, out hitDistance);

    /// <summary>The nearest thing along a ray that <paramref name="accept"/> admits; everything else is
    /// looked straight through.</summary>
    public static bool RaycastSingle(IAcousticWorld world, Vector3 start, Vector3 dir, float maxDist, Func<AcousticBody, bool>? accept,
                                     out AcousticBody hitBody, out float hitDistance)
    {
        hitDistance = maxDist;
        hitBody = default;
        bool found = false;

        Vector3 center = start + (dir * (maxDist / 2.0f));
        List<AcousticBody> bodies;
        if (UsesTriangles(world))
        {
            var geo = world.Geometry!;
            var filter = new PredicateFilter { Base = Filter(world), World = world, Accepts = accept };
            if (geo.Enter(start, dir, maxDist, OpenFPS.Geometry.GeometryLayers.Physical, ref filter, out var h)
                && h.T < hitDistance && world.TryGetBody(h.Owner, out var owner))
            {
                hitDistance = h.T; hitBody = owner; found = true;
            }
            bodies = Bodies(world, triangles: true, default, 0f, staticOnly: false);
        }
        else bodies = Bodies(world, triangles: false, center, (maxDist / 2.0f) + 1.0f);

        foreach (var body in bodies)
        {
            if (body.Size.X <= 0) continue;
            if (accept != null && !accept(body)) continue;
            bool intersected = false;
            float dist = maxDist;

            if (body.Shape == ColliderShape.Box)
            {
                intersected = GeometryUtils.RayIntersectsOBB(start, dir, body.Position, body.Size, body.Rotation, out dist);
            }
            else
            {
                intersected = body.Shape switch
                {
                    ColliderShape.Sphere => GeometryUtils.LineIntersectsSphere(start, start + dir * maxDist, body.Position, body.Size.X / 2.0f),
                    ColliderShape.Cylinder or ColliderShape.Cone => GeometryUtils.RayIntersectsCylinder(start, dir, body.Position, body.Size.X / 2.0f, body.Size.Y, out dist),
                    _ => false
                };
                if (intersected && body.Shape == ColliderShape.Sphere) dist = Vector3.Distance(start, body.Position) - (body.Size.X / 2f);
            }

            if (intersected && dist >= 0 && dist < hitDistance)
            {
                hitDistance = dist;
                hitBody = body;
                found = true;
            }
        }
        return found;
    }

    /// <summary>The game's own snapshots: <see cref="RaycastSingle(IAcousticWorld, Vector3, Vector3, float, Func{AcousticBody, bool}?, out AcousticBody, out float)"/>
    /// unwrapped.</summary>
    public bool RaycastSingle(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, Func<EntitySnapshot, bool>? accept,
                              out EntitySnapshot hitEntity, out float hitDistance)
    {
        Func<AcousticBody, bool>? bodyAccept = accept == null ? null : b => world.Entities.TryGetValue(b.Id, out var e) && accept(e);
        bool found = RaycastSingle(world, start, dir, maxDist, bodyAccept, out var body, out hitDistance);
        hitEntity = found && world.Entities.TryGetValue(body.Id, out var hit) ? hit : default;
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