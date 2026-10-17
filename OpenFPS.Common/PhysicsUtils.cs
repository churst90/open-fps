using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Geometry;

namespace OpenFPS.Common;

/// <summary>
/// The ground probes, shared so the client and the server agree where the floor is. The question itself is
/// <see cref="Footing"/>'s, asked of a physics world (docs/PHYSICS_BOUNDARY.md stage 3); these ask it of the
/// server's world and grid and of a client's snapshot, through the view each fills.
/// </summary>
public static class PhysicsUtils
{
    // The probes run on the server tick thread, the client game thread and the acoustic worker thread.
    // Per-thread scratch keeps them allocation-free without any of them waiting on the others.
    [ThreadStatic] private static List<PhysicsBody>? _bodies;
    [ThreadStatic] private static List<int>? _ignored;

    private static List<PhysicsBody> Bodies => _bodies ??= new List<PhysicsBody>(64);

    /// <summary>The floor height under a point, on the server (<see cref="Footing"/>'s ground probe over
    /// <see cref="ServerPhysicsView"/>). A player's step goes through
    /// <see cref="GetGroundHeight(World, SpatialGrid{Entity}, Vector3, int, ref GroundProbeMemo, out string)"/>.</summary>
    public static float GetGroundHeight(World world, SpatialGrid<Entity> grid, Vector3 pos, out string material)
        => GetGroundHeight(world, grid, pos, null, out material);

    /// <summary>
    /// The ground under a point, not counting <paramref name="ignore"/>: a driven vehicle's own floor is
    /// inside the step height, and counted it climbs onto it every tick and out of the world.
    /// </summary>
    public static float GetGroundHeight(World world, SpatialGrid<Entity> grid, Vector3 pos,
                                        ICollection<Entity>? ignore, out string material)
    {
        List<int>? skip = null;
        if (ignore is { Count: > 0 })
        {
            skip = _ignored ??= new List<int>(8);
            skip.Clear();
            foreach (var e in ignore) skip.Add(e.Id);
        }
        return Footing.GroundOn(ServerPhysicsView.Of(world, grid), pos, int.MinValue, skip, Bodies, out material);
    }

    /// <summary>
    /// A body's floor on the server, the triangle world's part remembered (the session's memo): asked
    /// again only when the point or the static geometry changed. What moves and the statics the triangles do
    /// not hold are asked every time, so the answer is the one a fresh probe and a client's give.
    /// </summary>
    public static float GetGroundHeight(World world, SpatialGrid<Entity> grid, Vector3 pos, int own,
        ref GroundProbeMemo memo, out string material)
    {
        // The view first: handing out the triangle world places its movers, which may move the version.
        var view = ServerPhysicsView.Of(world, grid);
        if (memo.TryGet(pos, grid.StaticVersion, out float floor, out string under))
            PerfProbe.Count("server.groundprobe.hit");
        else
            using (PerfProbe.Measure("server.groundprobe"))
            {
                floor = Footing.TriangleGround(view, pos, own, null, out under);
                memo.Store(pos, grid.StaticVersion, floor, under);
            }
        return Footing.GroundOn(view, pos, own, null, floor, under, Bodies, out material);
    }

    /// <summary>The floor height under a point, on the client: the same question as the server's, of the
    /// client's snapshot (<see cref="SnapshotPhysicsView"/>), what moves included.</summary>
    public static float GetGroundHeight(WorldSnapshot snapshot, Vector3 pos, int ownEntityId, out string material)
        => Footing.GroundOn(SnapshotPhysicsView.Of(snapshot), pos, ownEntityId, null, Bodies, out material);

    /// <summary>
    /// Where a foot goes down on a client, and on what (<see cref="Footing.FootOnFloor"/>): on the snapshot's
    /// triangle world, not counting the body itself or a solid whose place there is stale. No triangle world
    /// leaves the foot where it was and <paramref name="material"/> null.
    /// </summary>
    public static Vector3 FootOnFloor(WorldSnapshot snapshot, Vector3 foot, Vector3 feet, Vector3 way, int ownEntityId, out string? material)
    {
        material = null;
        var view = SnapshotPhysicsView.Of(snapshot);
        if (view.Triangles is not { } geo) return foot;
        var filter = new BodyFilter(view, ownEntityId);
        return Footing.FootOnFloor(geo, ref filter, foot, feet, way, out material);
    }
}
