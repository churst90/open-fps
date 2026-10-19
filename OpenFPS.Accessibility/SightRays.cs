using System.Numerics;
using OpenFPS.Acoustics;
using OpenFPS.Geometry;
using OpenFPS.Materials;

namespace OpenFPS.Client.Core;

/// <summary>
/// Rays over the scene view (stage 9, docs/ENGINE_PACKAGES.md): what a look meets, and the nearest thing
/// along a ray. The sight aids ask it directly; the game's <see cref="SpatialService"/> unwraps its answers
/// to snapshots for the scope, the session and the tests. <c>AcousticRays</c> is the acoustic sibling.
///
/// <para>With a triangle world the static solids are asked of it; only what it does not hold (what moves,
/// and the Unindexed statics) goes through the per-body box tests. AudioLab --geometry-parity lists where
/// the two paths differ.</para>
/// </summary>
public static class SightRays
{
    // Queried several times per ray, per source, per frame, from the game thread and the acoustic worker
    // at once: hence per-thread scratch, with no allocation, lock or shared state.
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

    /// <summary>Whether this world's static solids are answered by its triangle world.</summary>
    public static bool UsesTriangles(IAcousticWorld world) => AcousticRays.UsesTriangles(world);

    /// <summary>Counts what the box path counted: not the stale (rebuilding) and not up to two things.</summary>
    private struct StaticFilter : IGeometryFilter
    {
        public IReadOnlySet<int>? Stale;
        public int A, B;
        public readonly bool Accept(int owner, in Surface surface)
            => owner != A && owner != B && (Stale == null || !Stale.Contains(owner));
    }

    /// <summary>The box path's predicates over bodies, asked of a triangle's owner.</summary>
    private struct PredicateFilter : IGeometryFilter
    {
        public StaticFilter Base;
        public IAcousticWorld World;
        public Func<AcousticBody, bool>? Accepts;
        public Func<AcousticBody, bool>? Skips;
        public bool GlassBlocks;
        public bool SkipGlass;
        public readonly bool Accept(int owner, in Surface surface)
        {
            if (!Base.Accept(owner, surface)) return false;
            if (SkipGlass && !GlassBlocks && surface.Is(SurfaceFlags.Glass)) return false;
            if (Accepts == null && Skips == null) return true;
            if (!World.TryGetBody(owner, out var b)) return false;
            if (Accepts != null && !Accepts(b)) return false;
            if (Skips != null && Skips(b)) return false;
            return true;
        }
    }

    private static StaticFilter Filter(IAcousticWorld world, int a = int.MinValue, int b = int.MinValue)
        => new() { Stale = world.GeometryStale, A = a, B = b };

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
            if (geo.Enter(start, dir, maxDist, GeometryLayers.Sight, ref filter, out var h)
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

    /// <summary>What the first surface a look meets along a line is (<see cref="SurfaceFlags"/>),
    /// glass included; none without the triangle world, or where it meets nothing.</summary>
    public static SurfaceFlags SurfaceFlagsAlong(IAcousticWorld world, Vector3 start, Vector3 dir, float maxDist, int ownEntityId = -1)
    {
        if (!UsesTriangles(world)) return SurfaceFlags.None;
        var geo = world.Geometry!;
        var filter = new PredicateFilter { Base = Filter(world, ownEntityId), World = world, GlassBlocks = true, SkipGlass = true };
        return geo.Enter(start, Vector3.Normalize(dir), maxDist, GeometryLayers.Sight, ref filter, out var h)
            ? geo.SurfaceOf(h).Flags : SurfaceFlags.None;
    }

    /// <summary>Whether nothing a look meets is over <paramref name="at"/> for <paramref name="metres"/>, glass included:
    /// the open sky. Inside a solid (a roof's own volume) is under it.</summary>
    public static bool UnderOpenSky(IAcousticWorld world, Vector3 at, float metres, int ownEntityId = -1)
    {
        if (!UsesTriangles(world))
            return !CastSight(world, at, Vector3.UnitY, metres, out _, out _, glassBlocks: true, ownEntityId: ownEntityId);
        var filter = new PredicateFilter { Base = Filter(world, ownEntityId), World = world, GlassBlocks = true, SkipGlass = true };
        if (world.Geometry!.Any(at, Vector3.UnitY, metres, GeometryLayers.Sight, RayFaces.Both, ref filter))
            return false;
        return !CastSight(world, at, Vector3.UnitY, metres, out _, out _, glassBlocks: true, ownEntityId: ownEntityId);
    }

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
            if (geo.Enter(start, dir, maxDist, GeometryLayers.Physical, ref filter, out var h)
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
}
