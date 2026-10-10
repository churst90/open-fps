using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Geometry;

namespace OpenFPS.Tests;

/// <summary>
/// Rooms measured from the walls (docs/GEOMETRY.md 2.7, 12.6): a flood from each room's hint box through the space
/// the solids leave, stopped by walls, floors, ceilings and shut door leaves, kept within the hint grown by a margin.
/// </summary>
public class RoomFloodTests
{
    private static Surface S(Vector3 size) => EntityGeometry.SurfaceOf("Concrete", size, 0, 0, false, 0, 0, false, false, false, null);

    private static SolidSpec Box(int id, Vector3 lo, Vector3 hi)
    {
        var size = hi - lo;
        return SolidSpec.Of(id, (lo + hi) * 0.5f, Quaternion.Identity, size, S(size), null);
    }

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
        // Less a half-metre cell round its walls, floor and ceiling (a wall is a cell's diagonal thick to the flood).
        Assert.InRange(r.Volume, 4 * 3 * 2.7f * 0.6f, 4 * 3 * 2.7f * 1.05f);
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
        // The bay's 1.5 by 2 m, less the quantisation of the half-metre cells.
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
}
