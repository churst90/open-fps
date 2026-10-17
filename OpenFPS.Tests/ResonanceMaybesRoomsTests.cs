using System.Collections.Concurrent;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A point in a corner or a narrow pocket of a room is in that room (Resonance's list of 2026-10-17, item 1.4: its
/// listeners in corners were taken for the outdoors, a rush of outdoor sound). The room lookup is the flood's cells
/// (RoomCells.TryRoomAt): a room's air and the wall cells beside it, a quarter metre each. A cell whose middle is in a
/// wall and which touches the room's air only at its own corner is no room's, and the region lookup
/// (MapManager.RegionAt, as the client's SpatialService.GetRegionAt) gives a point in it the outdoors.
/// </summary>
public class ResonanceMaybesRoomsTests
{
    private readonly ITestOutputHelper _o;
    public ResonanceMaybesRoomsTests(ITestOutputHelper o) => _o = o;

    /// <summary>How far apart the points are, metres.</summary>
    private const float Step = 0.05f;

    /// <summary>
    /// How far from every wall a point must be for the lookup to answer for it, metres. A body's middle is never
    /// nearer a wall than its radius (BodyConstants.PlayerRadius, 0.3 m), so neither are the ears; at 0.2 m the city
    /// already has no miss (2026-10-17).
    /// </summary>
    private const float Clearance = 0.2f;

    private sealed record Miss(int Room, string Name, Vector3 At, float Height, float NearX, float NearZ, int Region, int Found);

    /// <summary>
    /// Every free point under a roof, a twentieth of a metre apart, within a cell of each flooded city room's walls, at
    /// the floor (0.1 m), seated (1.0 m) and at the standing ear (1.7 m), that sees the room's own air within 0.6 m and
    /// is not in an opening's thickness: none at least <see cref="Clearance"/> from every wall along x and z is in no
    /// room, or in a room whose air it cannot see.
    ///
    /// Measured on the city, 2026-10-17: 487 rooms, 8,981,267 points, 282,357 of them in corners (both walls within
    /// 0.3 m). In no room: 4,376, all within 0.2 m of a wall (1,440 at 0.05 m or more, 344 at 0.1 m or more; in corners
    /// 611 and 44), each given the outdoors. In a room whose air they cannot see: 16,140, the strip of a corridor a hand's
    /// width from a flat's shared wall, whose cell goes to the flat's nearer seed (RoomCells.Disjoint), all within
    /// 0.2 m. At 0.2 m and more from both walls: none of either. A source set down in a corner is outdoors for the
    /// last 5 to 20 cm; a listener never gets there.
    /// </summary>
    [Fact]
    public void Every_point_a_body_can_reach_in_a_city_room_s_corners_and_pockets_is_in_that_room()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-corners-" + Guid.NewGuid().ToString("N"));
        string maps = Path.Combine(dir, "maps");
        Directory.CreateDirectory(maps);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "city.json"), Path.Combine(maps, "city.json"));
            var m = new MapManager(new MapRepository(maps), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
            m.Initialize();
            Assert.True(m.TryGetMap("city", out var ecs, out _, out _, out _));
            Assert.True(m.TryGetGeometry("city", out var geometry));
            var cells = m.RoomsOf("city");
            Assert.NotNull(cells);
            var world = geometry.World;
            var names = new Dictionary<int, string>();
            ecs.Query(new QueryDescription().WithAll<Transform, RegionComponent>(), (Entity e, ref Transform t, ref RegionComponent r) =>
                names[e.Id] = r.FriendlyName ?? "");

            var misses = new ConcurrentBag<Miss>();
            long free = 0, corners = 0, inOpenings = 0;
            var heights = new[] { 0.1f, 1.0f, 1.7f };
            Parallel.ForEach(cells!.Flooded, new ParallelOptions { MaxDegreeOfParallelism = 8 }, id =>
            {
                if (cells.FloorOf(id) is not float floor || !cells.TryAir(id, out _, out var hi, out _)) return;
                // This room's wall cells in each layer the heights fall in: its cells that are not air.
                var layers = heights.Select(h => RoomFlood.CellOf(new Vector3(0f, floor + h, 0f), cells.Origin).Y).ToArray();
                var wallCells = new Dictionary<int, HashSet<(int X, int Z)>>();
                cells.ForEachSpan((room, y, z, x0, x1) =>
                {
                    if (room != id || !layers.Contains(y)) return;
                    for (int x = x0; x <= x1; x++)
                        if (!cells.TryAirAt(RoomFlood.CentreOf(x, y, z, cells.Origin), out _))
                        {
                            if (!wallCells.TryGetValue(y, out var set)) wallCells[y] = set = new();
                            set.Add((x, z));
                        }
                });
                long localFree = 0, localCorners = 0;
                int per = (int)MathF.Round(RoomFlood.Cell / Step);
                foreach (float h in heights)
                {
                    float y = floor + h;
                    if (y > hi.Y + RoomFlood.Cell) continue;
                    int cy = RoomFlood.CellOf(new Vector3(0f, y, 0f), cells.Origin).Y;
                    if (!wallCells.TryGetValue(cy, out var walls)) continue;
                    // The wall cells and every cell beside them, the diagonal ones in a corner too.
                    var near = new HashSet<(int X, int Z)>();
                    foreach (var (x, z) in walls)
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                                near.Add((x + dx, z + dz));
                    foreach (var (x, z) in near)
                    {
                        var c = RoomFlood.CentreOf(x, cy, z, cells.Origin);
                        for (int j = 0; j < per; j++)
                            for (int i = 0; i < per; i++)
                            {
                                var p = new Vector3(c.X - 0.5f * RoomFlood.Cell + (i + 0.5f) * Step, y, c.Z - 0.5f * RoomFlood.Cell + (j + 0.5f) * Step);
                                // Free, under a roof (past an open side, a portal or a hangar's front is the outdoors), and in
                                // sight of the room's air.
                                if (InSolid(world, p) || !Covered(world, p) || !SeesAir(cells, world, id, p)) continue;
                                localFree++;
                                var (nx, nz) = Nearest(world, p);
                                if (nx <= 0.3f && nz <= 0.3f) localCorners++;
                                bool found = cells.TryRoomAt(p, out int room);
                                if (found && (room == id || SeesAir(cells, world, room, p))) continue;
                                // In an opening's thickness (a car park's open side, a doorway with no leaf), a cell either
                                // way: the edge between two places, not a corner of either.
                                if (InOpening(cells, p)) { Interlocked.Increment(ref inOpenings); continue; }
                                misses.Add(new Miss(id, names.GetValueOrDefault(id, ""), p, h, nx, nz, MapManager.RegionAt(ecs, p), found ? room : 0));
                            }
                    }
                }
                Interlocked.Add(ref free, localFree);
                Interlocked.Add(ref corners, localCorners);
            });

            var all = misses.ToList();
            _o.WriteLine($"{cells.Flooded.Count} rooms; {free} free points within a cell of their walls, {corners} in corners; " +
                         $"{all.Count(x => x.Found == 0)} in no room ({all.Count(x => x.Found == 0 && x.Region == AcousticConstants.GlobalRegionId)} given the outdoors), " +
                         $"{all.Count(x => x.Found != 0)} in a room whose air they do not see; {inOpenings} in an opening's thickness, left out");
            foreach (float d in new[] { 0.05f, 0.1f, Clearance, 0.3f })
                _o.WriteLine($"  at least {d:F2} m from every wall along x and z: {all.Count(x => x.Found == 0 && MathF.Min(x.NearX, x.NearZ) >= d)} in no room " +
                             $"({all.Count(x => x.Found == 0 && MathF.Min(x.NearX, x.NearZ) >= d && x.NearX <= 0.3f && x.NearZ <= 0.3f)} in corners), " +
                             $"{all.Count(x => x.Found != 0 && MathF.Min(x.NearX, x.NearZ) >= d)} in another");
            foreach (var g in all.GroupBy(x => (x.Name, None: x.Found == 0)).OrderByDescending(g => g.Count()).Take(12))
            {
                var x = g.OrderByDescending(x => MathF.Min(x.NearX, x.NearZ)).First();
                _o.WriteLine($"  {g.Count(),5} {(g.Key.None ? "in no room" : "in another")}: {x.Name}, furthest from its walls at ({x.At.X:F2}, {x.At.Z:F2}, {x.At.Y:F2}), " +
                             $"walls {x.NearX:F2} and {x.NearZ:F2} m off, region {x.Region} {names.GetValueOrDefault(x.Region, "")}");
            }

            Assert.True(cells.Flooded.Count > 400 && corners > 100_000, $"{cells.Flooded.Count} rooms, {corners} corner points");
            var reached = all.Where(x => MathF.Min(x.NearX, x.NearZ) >= Clearance).ToList();
            Assert.True(reached.Count == 0, $"{reached.Count} points a body can reach are in no room or the wrong one, e.g. " +
                        string.Join("; ", reached.Take(5).Select(x => $"{x.Name} at ({x.At.X:F2}, {x.At.Z:F2}, {x.At.Y:F2}), walls {x.NearX:F2}/{x.NearZ:F2} m, found {x.Found}")));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static bool InSolid(TriangleWorld world, Vector3 p)
    {
        var into = new List<SolidRef>();
        var all = new AcceptAll();
        world.Containing(p, GeometryLayers.Acoustics, ref all, into);
        return into.Count > 0;
    }

    /// <summary>Whether something is over the point within 60 m.</summary>
    private static bool Covered(TriangleWorld world, Vector3 p)
    {
        var all = new AcceptAll();
        return world.Any(p, Vector3.UnitY, 60f, GeometryLayers.Acoustics, RayFaces.Both, ref all);
    }

    /// <summary>Whether the point is in an opening's frame grown by a cell every way.</summary>
    private static bool InOpening(RoomCells cells, Vector3 p)
    {
        foreach (var f in cells.Openings)
        {
            var local = Vector3.Abs(Vector3.Transform(p - f.Centre, Quaternion.Conjugate(f.Rotation)));
            var half = f.Size * 0.5f + new Vector3(RoomFlood.Cell);
            if (local.X <= half.X && local.Y <= half.Y && local.Z <= half.Z) return true;
        }
        return false;
    }

    /// <summary>Whether a cell of <paramref name="room"/>'s air within 0.6 m is in plain sight of <paramref name="p"/>.</summary>
    private static bool SeesAir(RoomCells cells, TriangleWorld world, int room, Vector3 p)
    {
        var (x0, y0, z0) = RoomFlood.CellOf(p, cells.Origin);
        var all = new AcceptAll();
        for (int dy = -1; dy <= 1; dy++)
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    var c = RoomFlood.CentreOf(x0 + dx, y0 + dy, z0 + dz, cells.Origin);
                    if (!cells.TryAirAt(c, out int r) || r != room) continue;
                    var to = c - p;
                    float d = to.Length();
                    if (d > 0.6f) continue;
                    if (d < 1e-4f || !world.Any(p, to / d, d, GeometryLayers.Acoustics, RayFaces.Both, ref all)) return true;
                }
        return false;
    }

    /// <summary>How far the nearest solid is along x and along z, either way, up to a metre.</summary>
    private static (float X, float Z) Nearest(TriangleWorld world, Vector3 p)
    {
        var all = new AcceptAll();
        float Along(Vector3 d) => world.Closest(p, d, 1f, GeometryLayers.Acoustics, RayFaces.Both, ref all, out var hit) ? hit.T : 1f;
        return (MathF.Min(Along(Vector3.UnitX), Along(-Vector3.UnitX)), MathF.Min(Along(Vector3.UnitZ), Along(-Vector3.UnitZ)));
    }
}
