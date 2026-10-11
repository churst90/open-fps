using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.OneWorld;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;
using EntityData = OpenFPS.Server.Repositories.EntityData;

namespace OpenFPS.Tests;

/// <summary>
/// The city's floors are constructions (docs/GEOMETRY.md 12.6, "A floor is a construction"), as the real places'
/// are: every room has a structure under what a body stands on at nine points over its floor, a slab or boards over
/// a void (FloorStructure). A room is a place the server finds indoors (its survey) with something solid over its
/// middle: a back garden fenced in on four sides is indoors to the survey and open to the sky. The city's ground is a
/// box of dirt, a solid like any other, with no terrain under it. A stairwell's storey runs down through the slab
/// under it (one shaft) and the garage's lowest deck is laid on the ground under its box, so a floor's top is
/// looked for up to 0.3 m over a room's underside.
/// </summary>
public class CityFloorsTests
{
    private readonly ITestOutputHelper _o;
    public CityFloorsTests(ITestOutputHelper o) => _o = o;

    /// <summary>A storey's slab and its tile, 0.28 m, and a hand over.</summary>
    private const float Reach = 0.30f;

    [Fact]
    public void Every_room_s_floor_stands_on_a_structure()
    {
        AcousticRegistry.Initialize();
        var maps = DoorChoiceTests.Maps;
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var prefabs = WorldBuildings.PrefabsFrom(new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")).Prefabs);
        var indoor = new HashSet<int>();
        foreach (var (id, e) in maps.AuthoredEntities("city"))
            if (world.Has<RegionComponent>(e) && world.Get<RegionComponent>(e).IsIndoor) indoor.Add(id);
        // What a room is roofed by: a solid that is not porous (foliage is) over its middle, 1.5 m over its floor or more.
        var over = data.Entities.Where(x => prefabs.TryGetValue(x.PrefabId, out var pf) && !pf.Hinged && pf.Material is { Length: > 0 } m
                                            && m != "None" && !AcousticRegistry.GetProperties(m).Porous).ToList();
        int open = 0;
        bool Room(EntityData r)
        {
            if (!indoor.Contains(r.EntityId)) return false;
            var half = prefabs[r.PrefabId].Size * r.Scale * 0.5f;
            float floor = r.Position.Y - half.Y;
            foreach (var s in over)
            {
                var sh = prefabs[s.PrefabId].Size * s.Scale * 0.5f;
                if (s.Position.Y - sh.Y < floor + 1.5f) continue;
                var rot = s.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(s.Rotation);
                var l = Vector3.Transform(new Vector3(r.Position.X, s.Position.Y, r.Position.Z) - s.Position, Quaternion.Inverse(rot));
                if (MathF.Abs(l.X) <= sh.X && MathF.Abs(l.Z) <= sh.Z) return true;
            }
            open++;
            _o.WriteLine($"  indoors and open to the sky: {r.Name}");
            return false;
        }
        var r = FloorStructure.Check(data.Entities, prefabs, (_, _) => null, Room, Reach);
        _o.WriteLine($"city: {indoor.Count} places indoors, {open} open to the sky; {r.Rooms} rooms, {r.Points} points, "
                     + $"{r.ByStack.Values.Sum()} rooms with a floor on no structure");
        foreach (var (stack, n) in r.ByStack.OrderByDescending(kv => kv.Value).Take(12)) _o.WriteLine($"  {n} on {stack}");
        foreach (var b in r.Bare.Take(40)) _o.WriteLine("  " + b);
        Assert.True(r.Rooms > 300, $"{r.Rooms} rooms");
        Assert.Empty(r.Bare);
    }
}
