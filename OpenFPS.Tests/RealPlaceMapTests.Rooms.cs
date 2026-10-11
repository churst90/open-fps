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

    /// <summary>
    /// Nothing laid along a line goes into a building (docs/GEOMETRY.md 12.6, "A road stops at a wall"): no piece of a
    /// road, verge, sidewalk, kerb, drive or path overlaps any building's footprint (SurfacesClear). Until 2026-10-11,
    /// 1024 Belmont Avenue Southwest's living room had 1034's driveway in it and a service road ran through the Exxon on
    /// Magnolia: on the maps before, 276 pieces on Albany and 110 on Magnolia.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void Nothing_laid_along_a_line_enters_a_building(string id)
    {
        var p = _maps.Get(id);
        var prefabs = OpenFPS.Server.OneWorld.WorldBuildings.PrefabsFrom(
            new OpenFPS.Server.Repositories.PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")).Prefabs);
        var r = SurfacesClear.Check(p.Data.Entities, prefabs);
        _o.WriteLine($"{id}: {r.Pieces} pieces, {r.Footprints} footprints, {r.Overlapping} pieces in a building");
        foreach (var f in r.Found.Take(20)) _o.WriteLine("  " + f);
        Assert.True(r.Pieces > 1000 && r.Footprints > 1000, $"{r.Pieces} pieces, {r.Footprints} footprints");
        Assert.Empty(r.Found);
    }

    /// <summary>
    /// Every wall is framed (docs/GEOMETRY.md 12.6, "Timber walls are framed"): each upright sheet of wood, plasterboard
    /// or brick a building is made of is cladding, sheathing, a glass-fibre stud cavity and plasterboard, its cladding
    /// facing out, or a partition of plasterboard on studs, and a shed of one solid is two thin walls and its air
    /// (FramedWalls, by material and build). On the maps before, every outside wall was 25 cm of solid wood or brick and
    /// its lining 3 cm of solid plaster, and a shed a block of wood: 9,497 on Magnolia and 18,522 on Albany failed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void Every_wall_is_framed(string id)
    {
        var map = OpenFPS.Server.Repositories.MapRepository.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "places", id + ".json"))!;
        var prefabs = new OpenFPS.Server.Repositories.PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")).Prefabs;
        var r = FramedWalls.Check(map.Entities, prefabs);
        _o.WriteLine($"{id}: {r.Outer} outside walls lined, {r.Unlined} unlined with {r.Boards} boards, {r.Partitions} partitions, {r.Sheds} sheds; "
                     + $"{r.Why.Values.Sum()} not framed");
        foreach (var (why, n) in r.Why.OrderByDescending(kv => kv.Value)) _o.WriteLine($"  {n}: {why}");
        foreach (var b in r.Bad.Take(10)) _o.WriteLine("  " + b);
        Assert.True(r.Outer > 3000 && r.Unlined > 200 && r.Boards > 400 && r.Partitions > 500 && r.Sheds > 20,
                    $"{r.Outer} outside walls, {r.Unlined} unlined, {r.Boards} boards, {r.Partitions} partitions, {r.Sheds} sheds");
        Assert.Empty(r.Bad);
    }
}
