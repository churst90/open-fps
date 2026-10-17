using System.Numerics;
using OpenFPS.Geometry;
using static OpenFPS.Common.BodyConstants;

namespace OpenFPS.Common;

/// <summary>
/// Where the floor is under a body on foot and where its foot goes down, asked of a triangle world
/// (docs/GEOMETRY.md 3.3, 3.8). The server and a client ask the same question of their own world through
/// <c>PhysicsUtils</c>, each with a filter that says which solids are theirs to count.
/// </summary>
public static class Footing
{
    /// <summary>How near the body's height a floor is to be the one it stands on, metres.</summary>
    public const float FootOnFloorTolerance = 0.002f;

    /// <summary>
    /// The floor under a body at <paramref name="pos"/>: five points across its footprint, the highest top no
    /// higher than a step above the feet. Below -1000 where there is none, and <paramref name="solid"/> says
    /// which solid it is.
    /// </summary>
    public static float Ground<F>(TriangleWorld geo, ref F filter, Vector3 pos, out SolidRef solid)
        where F : IGeometryFilter
        => geo.Ground(pos, PlayerRadius, StepHeight, GeometryLayers.Ground, ref filter, out solid, out _);

    /// <summary>No floor under a point: every probe answers below this.</summary>
    public const float NoFloor = -1000f;

    /// <summary>
    /// The floor under a body in a physics world (docs/PHYSICS_BOUNDARY.md stage 3): the triangle world's, the
    /// boxes' near it (the statics the triangles do not hold and everything that moves), the higher of the two;
    /// and, when nothing near is under it, every box in the world. The server and every client ask this of the
    /// world each has, so a body on a parked car stands on its roof on both. Not counting <paramref name="own"/>
    /// or <paramref name="ignore"/> (a car's own parts and occupants). <paramref name="scratch"/> is the caller's.
    /// </summary>
    public static float GroundOn(IPhysicsWorld world, Vector3 pos, int own, IReadOnlyList<int>? ignore,
                                 List<PhysicsBody> scratch, out string material)
    {
        float floor = TriangleGround(world, pos, own, ignore, out string under);
        return GroundOn(world, pos, own, ignore, floor, under, scratch, out material);
    }

    /// <summary>
    /// The triangle world's part of <see cref="GroundOn(IPhysicsWorld, Vector3, int, IReadOnlyList{int}, List{PhysicsBody}, out string)"/>,
    /// which changes only with the static geometry: a host may remember it for a point (the server's
    /// <c>GroundProbeMemo</c>). <see cref="NoFloor"/> where there is none or no triangle world.
    /// </summary>
    public static float TriangleGround(IPhysicsWorld world, Vector3 pos, int own, IReadOnlyList<int>? ignore, out string material)
    {
        material = "Generic";
        var geo = world.Triangles;
        if (geo == null) return NoFloor;
        var filter = new BodyFilter(world, own, ignore);
        float y = Ground(geo, ref filter, pos, out var solid);
        if (y > NoFloor)
        {
            var raw = geo.SurfaceOf(solid).Material;
            if (!string.IsNullOrEmpty(raw)) material = raw;
        }
        return y;
    }

    /// <summary>The rest of the ground probe, given the triangle world's floor under the point.</summary>
    public static float GroundOn(IPhysicsWorld world, Vector3 pos, int own, IReadOnlyList<int>? ignore,
                                 float triangleFloor, string triangleMaterial, List<PhysicsBody> scratch, out string material)
    {
        float y = triangleFloor;
        material = triangleMaterial;
        var skip = new BodyFilter { Own = own, Ignore = ignore is { Count: > 0 } ? ignore : null };
        world.Near(pos, PlayerRadius, scratch);
        float boxes = BoxFloor(scratch, pos, skip, out string boxMaterial);
        if (boxes > y) { y = boxes; material = boxMaterial; }
        // Nothing near (an edge case, a stale grid, a body far out on a vehicle): everything is asked.
        if (y < -900f)
        {
            world.Everything(scratch);
            y = BoxFloor(scratch, pos, skip, out material);
        }
        return y;
    }

    /// <summary>
    /// The highest top among <paramref name="boxes"/> under the body's footprint (the centre and four points on
    /// its radius) no higher than a step above its feet; <see cref="NoFloor"/> and Generic where none is. The
    /// first in the list wins a tie.
    /// </summary>
    public static float BoxFloor(List<PhysicsBody> boxes, Vector3 pos, in BodyFilter skip, out string material)
    {
        float best = NoFloor;
        material = "Generic";
        Span<Vector3> probes = stackalloc Vector3[5];
        probes[0] = pos;
        probes[1] = pos + new Vector3(PlayerRadius, 0, 0);
        probes[2] = pos + new Vector3(-PlayerRadius, 0, 0);
        probes[3] = pos + new Vector3(0, 0, PlayerRadius);
        probes[4] = pos + new Vector3(0, 0, -PlayerRadius);
        foreach (var b in boxes)
        {
            if (skip.Ignores(b.Id)) continue;
            float top = b.Centre.Y + b.Size.Y / 2f;
            if (top > pos.Y + StepHeight) continue;
            bool hit = false;
            var column = new Vector3(b.Size.X, 40000f, b.Size.Z);
            foreach (var p in probes)
                if (GeometryUtils.IsPointInOBB(p, b.Centre, column, b.Rotation)) { hit = true; break; }
            if (hit && top > best) { best = top; material = b.Material; }
        }
        return best;
    }

    /// <summary>
    /// Where a foot goes down, and on what (docs/GEOMETRY.md 3.8). The body stands at the highest floor
    /// under its footprint, so on a flight a foot under its middle was in the air over the lower tread.
    /// The foot goes on the floor the body stands on: under the foot, or a body's radius ahead along
    /// <paramref name="way"/>. Nothing at the body's height (a vehicle's floor) leaves the foot where it
    /// was and <paramref name="material"/> null.
    /// </summary>
    public static Vector3 FootOnFloor<F>(TriangleWorld geo, ref F filter, Vector3 foot, Vector3 feet, Vector3 way, out string? material)
        where F : IGeometryFilter
    {
        material = null;
        float top = feet.Y + StepHeight;
        var ahead = new Vector3(way.X, 0f, way.Z);
        ahead = ahead.LengthSquared() > 1e-8f ? Vector3.Normalize(ahead) * PlayerRadius : Vector3.Zero;
        float under = geo.FloorAt(foot.X, foot.Z, top, GeometryLayers.Ground, ref filter, out var underHit);
        // On the floor the body stands on, under the foot...
        if (under > -1000f && MathF.Abs(under - feet.Y) <= FootOnFloorTolerance) return On(geo, foot, under, underHit, out material);
        // ...or a stride ahead, the tread a body climbing a flight is stepping up onto...
        if (ahead != Vector3.Zero)
        {
            var at = foot + ahead;
            float y = geo.FloorAt(at.X, at.Z, top, GeometryLayers.Ground, ref filter, out var hit);
            if (y > -1000f && MathF.Abs(y - feet.Y) <= FootOnFloorTolerance) return On(geo, at, y, hit, out material);
        }
        // ...or whatever is under the foot within a step: going down, the body is held on the tread it is
        // leaving while the foot is already on the one below.
        if (under > -1000f && MathF.Abs(under - feet.Y) <= StepHeight + 0.01f) return On(geo, foot, under, underHit, out material);
        return foot;

        static Vector3 On(TriangleWorld geo, Vector3 at, float y, in GeometryHit hit, out string? material)
        {
            var raw = geo.SurfaceOf(hit).Material;
            material = string.IsNullOrEmpty(raw) ? "Generic" : raw;
            return new Vector3(at.X, y, at.Z);
        }
    }
}
