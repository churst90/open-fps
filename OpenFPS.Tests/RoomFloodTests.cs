using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// Rooms measured from the walls (docs/GEOMETRY.md 2.7, 12.6): a flood from each room's hint box through the space
/// the solids leave, stopped by walls, floors, ceilings and shut door leaves, kept within the hint grown by a margin.
/// </summary>
public class RoomFloodTests
{
    private static Surface S(Vector3 size) => EntityGeometry.SurfaceOf("Concrete", size, 0, 0, false, 0, 0, false, false, false, null);

    private static SolidSpec Box(int id, Vector3 lo, Vector3 hi) => Box(id, lo, hi, Quaternion.Identity);

    /// <summary>A box from its corners, turned by <paramref name="q"/> about the world's middle.</summary>
    private static SolidSpec Box(int id, Vector3 lo, Vector3 hi, Quaternion q)
    {
        var size = hi - lo;
        return SolidSpec.Of(id, Vector3.Transform((lo + hi) * 0.5f, q), q, size, S(size), null);
    }

    private static List<SolidSpec> Turned(List<SolidSpec> solids, Quaternion q)
        => solids.Select(s => s with { Position = Vector3.Transform(s.Position, q), Rotation = q * s.Rotation }).ToList();

    /// <summary>A room 4 by 3 m inside, 2.7 m high, walls 0.25 thick, a floor and a ceiling; <paramref name="gap"/>
    /// a doorway 1 m wide in its south wall, <paramref name="leaf"/> a 5 cm door leaf shut in it.</summary>
    private static List<SolidSpec> Room(bool gap = false, bool leaf = false, float bay = 0f)
    {
        int id = 1;
        var s = new List<SolidSpec>
        {
            Box(id++, new Vector3(-10, -0.2f, -10), new Vector3(10, 0, 10)),                 // floor
            Box(id++, new Vector3(-2.25f, 2.7f, -1.75f), new Vector3(2.25f + bay, 2.9f, 1.75f)),   // ceiling
            Box(id++, new Vector3(-2.25f, 0, 1.5f), new Vector3(2.25f + bay, 2.7f, 1.75f)),      // north
            Box(id++, new Vector3(-2.25f, 0, -1.5f), new Vector3(-2, 2.7f, 1.5f)),               // west
        };
        if (bay > 0)
        {
            // East wall with a bay: the room reaches bay metres further east between z -1 and 1.
            s.Add(Box(id++, new Vector3(2, 0, -1.5f), new Vector3(2.25f, 2.7f, -1f)));
            s.Add(Box(id++, new Vector3(2, 0, 1f), new Vector3(2.25f, 2.7f, 1.5f)));
            s.Add(Box(id++, new Vector3(2 + bay, 0, -1.25f), new Vector3(2.25f + bay, 2.7f, 1.25f)));
            s.Add(Box(id++, new Vector3(2.25f, 0, -1.25f), new Vector3(2 + bay, 2.7f, -1f)));
            s.Add(Box(id++, new Vector3(2.25f, 0, 1f), new Vector3(2 + bay, 2.7f, 1.25f)));
        }
        else s.Add(Box(id++, new Vector3(2, 0, -1.5f), new Vector3(2.25f, 2.7f, 1.5f)));
        if (gap)
        {
            s.Add(Box(id++, new Vector3(-2.25f, 0, -1.75f), new Vector3(-0.5f, 2.7f, -1.5f)));
            s.Add(Box(id++, new Vector3(0.5f, 0, -1.75f), new Vector3(2.25f + bay, 2.7f, -1.5f)));
            s.Add(Box(id++, new Vector3(-0.5f, 2.1f, -1.75f), new Vector3(0.5f, 2.7f, -1.5f)));
            if (leaf) s.Add(Box(id++, new Vector3(-0.5f, 0, -1.65f), new Vector3(0.5f, 2.1f, -1.6f)));
        }
        else s.Add(Box(id++, new Vector3(-2.25f, 0, -1.75f), new Vector3(2.25f + bay, 2.7f, -1.5f)));
        return s;
    }

    private static TriangleWorld World(List<SolidSpec> solids) => new TriangleWorldBuilder(250f).Build(solids, Array.Empty<SolidSpec>());

    private static readonly Vector3 Origin = new(-100, -50, -100);

    private static RoomFlood.Seed Hint(int id = 500) => new(id, new Vector3(0, 1.35f, 0), new Vector3(4, 2.7f, 3), Quaternion.Identity);

    [Fact]
    public void A_closed_room_is_its_inside()
    {
        var rooms = RoomFlood.Flood(World(Room()), new[] { Hint() }, Origin);
        var r = rooms[500];
        Assert.False(r.Open);
        Assert.False(r.Unseeded);
        // Less the cells that hold its floor and ceiling, and the quantisation of the quarter-metre cells.
        Assert.InRange(r.Volume, 4 * 3 * 2.7f * 0.8f, 4 * 3 * 2.7f * 1.05f);
        Assert.All(r.Cells, p => Assert.True(MathF.Abs(p.X) < 2 && MathF.Abs(p.Z) < 1.5f && p.Y > 0 && p.Y < 2.7f));
    }

    [Fact]
    public void A_bay_beyond_the_box_is_part_of_the_room()
    {
        var closed = RoomFlood.Flood(World(Room()), new[] { Hint() }, Origin)[500];
        var r = RoomFlood.Flood(World(Room(bay: 1.5f)), new[] { Hint() }, Origin, margin: 2f)[500];
        Assert.False(r.Open);
        Assert.Contains(r.Cells, p => p.X > 3f);
        Assert.DoesNotContain(r.Cells, p => p.X > 3.5f);
        // The bay's 1.5 by 2 m, less the quantisation of the cells.
        Assert.InRange(r.Volume - closed.Volume, 1.5f * 2f * 2.5f * 0.6f, 1.5f * 2f * 2.7f * 1.1f);
    }

    [Fact]
    public void An_open_doorway_lets_it_out_as_far_as_its_margin_and_a_shut_leaf_does_not()
    {
        var open = RoomFlood.Flood(World(Room(gap: true)), new[] { Hint() }, Origin)[500];
        Assert.True(open.Open);
        Assert.Contains(open.Cells, p => p.Z < -1.75f);
        Assert.All(open.Cells, p => Assert.True(p.Z > -1.5f - 1f - 0.25f));   // its margin
        var shut = RoomFlood.Flood(World(Room(gap: true, leaf: true)), new[] { Hint() }, Origin)[500];
        Assert.False(shut.Open);
        Assert.DoesNotContain(shut.Cells, p => p.Z < -1.75f);
    }

    [Fact]
    public void Its_cells_say_which_room_a_point_is_in_and_a_cache_floods_only_what_changed()
    {
        var world = World(Room(gap: true));
        var cache = new RoomFlood.Cache();
        var rooms = RoomFlood.Flood(world, new[] { Hint() }, Origin, cache: cache);
        Assert.Equal(1, cache.Misses);
        var cells = RoomCells.From(rooms);
        Assert.Contains(500, cells.Flooded);
        Assert.All(rooms[500].Cells.Concat(rooms[500].Edge), p => Assert.True(cells.TryRoomAt(p, out int id) && id == 500));
        Assert.True(cells.TryRoomAt(new Vector3(1.9f, 2.5f, 1.4f), out _));       // a hand's width from two walls
        Assert.False(cells.TryRoomAt(new Vector3(0, 1, 5f), out _));             // out in the street
        Assert.False(cells.TryRoomAt(new Vector3(0, 1, -1.5f - 1.25f - 0.5f), out _));
        var again = RoomFlood.Flood(world, new[] { Hint() }, Origin, cache: cache);
        Assert.Equal((1, 0), (cache.Hits, cache.Misses));
        Assert.Same(rooms[500], again[500]);
        // The leaf shut: the walls round the room changed, so it floods again.
        RoomFlood.Flood(World(Room(gap: true, leaf: true)), new[] { Hint() }, Origin, cache: cache);
        Assert.Equal((0, 1), (cache.Hits, cache.Misses));
    }

    [Fact]
    public void Two_rooms_through_an_archway_share_it_by_distance()
    {
        // The room with its south doorway open, and a second room south of it of the same size.
        var solids = Room(gap: true);
        int id = 100;
        solids.Add(Box(id++, new Vector3(-2.25f, 2.7f, -5f), new Vector3(2.25f, 2.9f, -1.75f)));
        solids.Add(Box(id++, new Vector3(-2.25f, 0, -5f), new Vector3(2.25f, 2.7f, -4.75f)));
        solids.Add(Box(id++, new Vector3(-2.25f, 0, -4.75f), new Vector3(-2, 2.7f, -1.75f)));
        solids.Add(Box(id++, new Vector3(2, 0, -4.75f), new Vector3(2.25f, 2.7f, -1.75f)));
        var south = new RoomFlood.Seed(600, new Vector3(0, 1.35f, -3.25f), new Vector3(4, 2.7f, 3), Quaternion.Identity);
        var rooms = RoomFlood.Flood(World(solids), new[] { Hint(), south }, Origin);
        var a = rooms[500].Cells.ToHashSet();
        Assert.DoesNotContain(rooms[600].Cells, a.Contains);
        Assert.InRange(rooms[500].Volume + rooms[600].Volume, 2 * 4 * 3 * 2.7f * 0.55f, 2 * 4 * 3 * 2.7f * 1.05f);
    }

    [Fact]
    public void A_middle_in_a_column_moves_aside_and_one_in_a_block_cannot_start()
    {
        var solids = Room();
        solids.Add(Box(77, new Vector3(-0.15f, 0, -0.15f), new Vector3(0.15f, 2.7f, 0.15f)));
        Assert.False(RoomFlood.Flood(World(solids), new[] { Hint() }, Origin)[500].Unseeded);
        var block = Room();
        block.Add(Box(78, new Vector3(-2f, 0, -1.5f), new Vector3(2f, 2.7f, 1.5f)));
        Assert.True(RoomFlood.Flood(World(block), new[] { Hint() }, Origin)[500].Unseeded);
    }

    [Fact]
    public void A_bay_deeper_than_the_margin_is_still_the_room_s()
    {
        // 3 m deep, past the metre the room may reach: closed in by walls, so the room's, and not open.
        var r = RoomFlood.Flood(World(Room(bay: 3f)), new[] { Hint() }, Origin)[500];
        Assert.False(r.Open);
        Assert.True(r.BeyondCount > 0);
        Assert.Contains(r.Cells, p => p.X > 4.5f);
        Assert.DoesNotContain(r.Cells, p => p.X > 5f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.07f)]
    [InlineData(0.13f)]
    [InlineData(0.2f)]
    public void A_hall_a_metre_wide_keeps_its_air(float shift)
    {
        // 1.1 m between walls a quarter metre thick, 5 m long, under a 6 cm ceiling: wherever the grid falls.
        float x0 = shift, x1 = shift + 1.1f;
        var solids = new List<SolidSpec>
        {
            Box(1, new Vector3(-10, -0.2f, -10), new Vector3(10, 0, 10)),
            Box(2, new Vector3(x0 - 0.25f, 2.6f, -2.75f), new Vector3(x1 + 0.25f, 2.66f, 2.75f)),
            Box(3, new Vector3(x0 - 0.25f, 0, -2.75f), new Vector3(x0, 2.6f, 2.75f)),
            Box(4, new Vector3(x1, 0, -2.75f), new Vector3(x1 + 0.25f, 2.6f, 2.75f)),
            Box(5, new Vector3(x0, 0, -2.75f), new Vector3(x1, 2.6f, -2.5f)),
            Box(6, new Vector3(x0, 0, 2.5f), new Vector3(x1, 2.6f, 2.75f)),
        };
        var hint = new RoomFlood.Seed(500, new Vector3((x0 + x1) / 2, 1.3f, 0), new Vector3(1.1f, 2.6f, 5f), Quaternion.Identity);
        var r = RoomFlood.Flood(World(solids), new[] { hint }, Origin)[500];
        Assert.False(r.Open);
        Assert.False(r.Unseeded);
        // Three to five quarter-metre cells across its 1.1 m, as the grid falls, less its floor's and ceiling's.
        Assert.InRange(r.Volume, 0.75f * 1.1f * 2.6f * 5f * 0.8f, 1.25f * 2.6f * 5f);
        var across = r.Cells.GroupBy(p => (MathF.Round(p.Y, 3), MathF.Round(p.Z, 3))).Select(g => g.Count()).ToList();
        Assert.True(across.Max() >= 3);
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(17f)]
    [InlineData(30f)]
    [InlineData(45f)]
    [InlineData(61f)]
    [InlineData(83f)]
    public void A_shut_leaf_stops_it_whatever_the_wall_s_angle(float degrees)
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180f);
        var hint = Hint() with { Centre = Vector3.Transform(Hint().Centre, q), Rotation = q };
        var shut = RoomFlood.Flood(World(Turned(Room(gap: true, leaf: true), q)), new[] { hint }, Origin)[500];
        Assert.False(shut.Open);
        Assert.False(shut.Unseeded);
        var open = RoomFlood.Flood(World(Turned(Room(gap: true), q)), new[] { hint }, Origin)[500];
        Assert.True(open.Open);
    }

    [Fact]
    public void A_roof_with_no_ceiling_under_it_closes_the_room_to_its_eaves()
    {
        // A barn's room: walls to 2.7 m and a hip roof straight on them, a wedge thinning to nothing at the eaves.
        var solids = Room().Where(s => s.Position.Y < 2.7f).ToList();
        var form = new ShapeSpec { Kind = ShapeKind.Roof, Style = RoofStyle.Hip, Outline = new[] { -2.25f, -1.75f, 2.25f, -1.75f, 2.25f, 1.75f, -2.25f, 1.75f } };
        var size = new Vector3(4.5f, 0.6f, 3.5f);
        solids.Add(SolidSpec.OfShape(90, new Vector3(0, 3.0f, 0), Quaternion.Identity, size, S(size), form));
        var r = RoomFlood.Flood(World(solids), new[] { Hint() }, Origin)[500];
        Assert.False(r.Open);
        Assert.DoesNotContain(r.Cells, p => p.Y > 3.3f);
    }

    [Fact]
    public void The_city_s_rooms_are_closed_but_where_they_open_by_design_and_are_their_boxes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-flood-" + Guid.NewGuid().ToString("N"));
        string maps = Path.Combine(dir, "maps");
        Directory.CreateDirectory(maps);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "city.json"), Path.Combine(maps, "city.json"));
            var m = new MapManager(new MapRepository(maps), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
            m.Initialize();
            Assert.True(m.TryGetMap("city", out var ecs, out _, out _, out _));
            Assert.True(m.TryGetMapData("city", out var data));
            Assert.True(m.TryGetGeometry("city", out var geometry));
            var g = Gate.Measure(geometry.World, ecs, data.MinBound);
            // Open by design: the car park's open sides, the tunnel's mouth, the hangar's open front, a walled
            // garden and the street cut down to the tunnel, all under the sky.
            string[] designed = { "Parking garage", "Tunnel, south portal", "Hangar", "back garden", "Main Street, block" };
            Assert.All(g.Open, name => Assert.Contains(designed, d => name.Contains(d)));
            Assert.True(g.MedianIou > 0.8, $"median IoU {g.MedianIou:F2}");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}

/// <summary>Every indoor room of a loaded map flooded through its server's triangle world and set against its box.</summary>
internal static class Gate
{
    public sealed record Result(List<string> Open, int Unseeded, double MedianIou, int Rooms);

    public static Result Measure(TriangleWorld world, World ecs, Vector3 origin)
    {
        var seeds = new List<RoomFlood.Seed>();
        var names = new Dictionary<int, string>();
        ecs.Query(new QueryDescription().WithAll<Transform, RegionComponent>(), (Entity e, ref Transform t, ref RegionComponent r) =>
        {
            if (!r.IsIndoor || r.RoomSize.X <= 0f || r.RoomSize.Y <= 0f || r.RoomSize.Z <= 0f) return;
            seeds.Add(new RoomFlood.Seed(e.Id, t.Position, r.RoomSize, t.Rotation));
            names[e.Id] = r.FriendlyName ?? "";
        });
        seeds.Sort((a, b) => a.Id.CompareTo(b.Id));
        var rooms = RoomFlood.Flood(world, seeds, origin, threads: 4);
        var ious = new List<double>();
        var open = new List<string>();
        int unseeded = 0;
        foreach (var s in seeds)
        {
            var room = rooms[s.Id];
            if (room.Unseeded) { unseeded++; continue; }
            if (room.Open) open.Add(names[s.Id]);
            var rot = s.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(s.Rotation);
            var inv = Quaternion.Conjugate(rot);
            var h = s.Size * 0.5f;
            bool InBox(Vector3 p) { var l = Vector3.Abs(Vector3.Transform(p - s.Centre, inv)); return l.X <= h.X && l.Y <= h.Y && l.Z <= h.Z; }
            long inBoth = room.Cells.Count(InBox), box = 0;
            var ext = Vector3.Abs(Vector3.Transform(new Vector3(h.X, 0, 0), rot)) + Vector3.Abs(Vector3.Transform(new Vector3(0, h.Y, 0), rot))
                      + Vector3.Abs(Vector3.Transform(new Vector3(0, 0, h.Z), rot));
            var (x0, y0, z0) = RoomFlood.CellOf(s.Centre - ext, origin);
            var (x1, y1, z1) = RoomFlood.CellOf(s.Centre + ext, origin);
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        if (InBox(RoomFlood.CentreOf(x, y, z, origin))) box++;
            ious.Add((double)inBoth / Math.Max(1, room.AirCount + box - inBoth));
        }
        ious.Sort();
        return new Result(open, unseeded, ious.Count == 0 ? 0 : ious[ious.Count / 2], seeds.Count);
    }
}
