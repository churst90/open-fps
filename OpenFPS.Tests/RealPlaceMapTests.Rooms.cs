using OpenFPS.Common.Geometry;

namespace OpenFPS.Tests;

public partial class RealPlaceMapTests
{
    /// <summary>
    /// Rooms by flood (docs/GEOMETRY.md 12.6): every room of a place flooded through its server's triangle world with
    /// the doors shut and stopped at its openings. None meets the sky (a barn's doorway with no leaf in it is an
    /// opening to the outdoors), no room is without a start, and half the rooms or more share over 0.8 of their air with
    /// their boxes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void Rooms_flood_shut_and_as_drawn(string id)
    {
        var p = _maps.Get(id);
        var g = Gate.Measure(ServerWorld(p), p.Ecs, p.Data.MinBound);
        _o.WriteLine($"{id}: {g.Rooms} rooms, {g.Open.Count} open ({string.Join("; ", g.Open)}), {g.Unseeded} unseeded, median IoU {g.MedianIou:F2}");
        Assert.Empty(g.Open);
        Assert.Equal(0, g.Unseeded);
        Assert.True(g.MedianIou > 0.8, $"median IoU {g.MedianIou:F2}");
    }

    /// <summary>
    /// Every room's floor is a construction (docs/GEOMETRY.md 12.6, "A floor is a construction"): at nine points over each
    /// room's floor, what a body stands on has a structure under it, a slab or boards over a void, the ground
    /// read from the place's own terrain (FloorStructure). Until 2026-10-11 a home at medium detail stood on 27 cm of
    /// carpet or wood laid on the ground: on the maps before, 1,441 rooms on Magnolia and 1,959 on Albany fail.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void Every_room_s_floor_stands_on_a_structure(string id)
    {
        var p = _maps.Get(id);
        var world = ServerWorld(p);
        var terrain = new OnlyOwners(TerrainIds(p.Ecs));
        var prefabs = OpenFPS.Server.OneWorld.WorldBuildings.PrefabsFrom(
            new OpenFPS.Server.Repositories.PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")).Prefabs);
        var r = FloorStructure.Check(p.Data.Entities, prefabs, (x, z) =>
        {
            float g = GroundUnder(world, x, z, ref terrain);
            return g > -1000f ? g : null;
        });
        _o.WriteLine($"{id}: {r.Rooms} rooms, {r.Points} points, {r.ByStack.Values.Sum()} rooms with a floor on no structure");
        foreach (var (stack, n) in r.ByStack.OrderByDescending(kv => kv.Value).Take(12)) _o.WriteLine($"  {n} on {stack}");
        foreach (var b in r.Bare.Take(10)) _o.WriteLine("  " + b);
        Assert.True(r.Rooms > 100, $"{r.Rooms} rooms");
        Assert.Empty(r.Bare);
    }
}
