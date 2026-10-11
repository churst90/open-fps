using System.Globalization;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Systems;

namespace OpenFPS.Server.Core;

/// <summary>
/// Where a body can stand in a room or a named place, as a player types it to /tp. The middle of a room's box
/// is often no such spot: half its height is above the ceiling's reach, a narrow room's middle can be inside a
/// wall, and furniture stands there (Cody, 2026-10-10: every spot of a listening list was refused as solid).
/// </summary>
public static class StandingSpots
{
    /// <summary>The spacing of the points tried across a place, metres.</summary>
    public const float Spacing = 0.25f;
    /// <summary>The most points tried in one place; a big yard is tried more coarsely.</summary>
    public const int MostPoints = 4096;

    /// <summary>The standing spot in a room: <see cref="In"/> over its flood's air where it flooded, the spot in that
    /// air (a body's middle in the room the server says it is in), else over its box.</summary>
    public static Vector3? InRoom(World world, SpatialGrid<Entity> grid, Entity room)
    {
        if (!world.IsAlive(room) || !world.Has<Transform>(room) || !world.Has<RegionComponent>(room)) return null;
        var t = world.Get<Transform>(room);
        var floods = MapManager.RoomsOf(world);
        if (floods != null && floods.TryAir(room.Id, out var lo, out var hi, out _))
        {
            // The cells' middles out to their faces, from its floor (the top of the floor's cells in most of its
            // columns: not a flight going down through it).
            float c = OpenFPS.Common.Geometry.RoomFlood.Cell * 0.5f;
            lo -= new Vector3(c); hi += new Vector3(c);
            if (floods.FloorOf(room.Id) is float floor) lo.Y = MathF.Min(floor, hi.Y - 0.5f);
            // About the room's own point where it lies in the air, so the spot nearest it is tried first.
            var middle = Vector3.Clamp(t.Position, lo, hi);
            var half = Vector3.Max(middle - lo, hi - middle);
            var box = new Vector3(2f * half.X, hi.Y - lo.Y, 2f * half.Z);
            int id = room.Id;
            return In(world, grid, new Vector3(middle.X, (lo.Y + hi.Y) * 0.5f, middle.Z), Quaternion.Identity, box,
                      p => floods.TryRoomAt(p + new Vector3(0f, PhysicsConstants.PlayerHeight * 0.5f, 0f), out int at) && at == id);
        }
        return In(world, grid, t.Position, t.Rotation, world.Get<RegionComponent>(room).RoomSize);
    }

    /// <summary>
    /// The point nearest the middle of a box (centre, turn, size) where a body stands on the floor and
    /// <see cref="MovementSystem.CheckCollision"/> finds nothing solid, given to the tenth of a metre as /tp
    /// takes it, with the height rounded up so the feet are never under the floor. A floor is ground within a step of
    /// the box's bottom: a bed, a sofa or a counter top is not one, nor a stair down through the floor (the middle of
    /// Marlow Tower's roof access is over the top flight); failing that anywhere, any ground. Null when none will do.
    /// </summary>
    public static Vector3? In(World world, SpatialGrid<Entity> grid, Vector3 centre, Quaternion rotation, Vector3 size,
                              Func<Vector3, bool>? inPlace = null)
    {
        if (rotation.LengthSquared() < 1e-6f) rotation = Quaternion.Identity;
        var half = Vector3.Max(size * 0.5f, Vector3.Zero);
        float bottom = centre.Y - half.Y, top = centre.Y + half.Y;
        float inX = MathF.Max(0f, half.X - PhysicsConstants.PlayerRadius), inZ = MathF.Max(0f, half.Z - PhysicsConstants.PlayerRadius);
        float step = Spacing;
        while ((2 * MathF.Floor(inX / step) + 1) * (2 * MathF.Floor(inZ / step) + 1) > MostPoints) step *= 2f;
        var points = new List<(float D, float X, float Z)>();
        int nx = (int)MathF.Floor(inX / step), nz = (int)MathF.Floor(inZ / step);
        for (int i = -nx; i <= nx; i++)
        for (int k = -nz; k <= nz; k++)
            points.Add((i * i + k * k, i * step, k * step));
        points.Sort((a, b) => a.D.CompareTo(b.D));

        // The probe looks a step up from where it starts, so from the floor's own height in a room; a yard's
        // box starts at its lowest ground, so from higher up there.
        float probeY = MathF.Min(bottom + MathF.Min(1.5f, MathF.Max(0.3f, size.Y - 2.5f)), top);
        foreach (bool floorOnly in new[] { true, false })
            foreach (var (_, lx, lz) in points)
            {
                var p = centre + Vector3.Transform(new Vector3(lx, 0f, lz), rotation);
                if (Standing(world, grid, new Vector3(p.X, probeY, p.Z), bottom, floorOnly, centre, rotation, half) is { } spot
                    && (inPlace == null || inPlace(spot)))
                    return spot;
            }
        return null;
    }

    private static Vector3? Standing(World world, SpatialGrid<Entity> grid, Vector3 probe, float bottom, bool floorOnly,
                                     Vector3 centre, Quaternion rotation, Vector3 half)
    {
        float ground = PhysicsUtils.GetGroundHeight(world, grid, probe, out _);
        if (ground < -500f) return null;
        if (floorOnly && (ground > bottom + 0.5f || ground < bottom - 0.3f)) return null;
        var typed = new Vector3(Tenth(probe.X), MathF.Ceiling((ground + 0.02f) * 10f - 1e-3f) / 10f, Tenth(probe.Z));
        // Rounded, the point must still be in the place, on the same floor, and clear.
        var local = Vector3.Transform(typed - centre, Quaternion.Inverse(rotation));
        if (MathF.Abs(local.X) > half.X || MathF.Abs(local.Z) > half.Z) return null;
        float under = PhysicsUtils.GetGroundHeight(world, grid, new Vector3(typed.X, probe.Y, typed.Z), out _);
        if (MathF.Abs(under - ground) > 0.05f) return null;
        if (MovementSystem.CheckCollision(world, grid, typed, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight)) return null;
        return typed;

        static float Tenth(float v) => MathF.Round(v * 10f) / 10f;
    }

    /// <summary>A spot as it is typed after /tp: east, north, height, to the tenth.</summary>
    public static string Typed(Vector3 spot)
        => string.Join(" ", PlayerCoordinates.Format(spot).Split(", "));
}
