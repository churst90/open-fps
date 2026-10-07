using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// A player cannot walk off the ground: on every shipped map each corner of the walkable area, a metre
/// in, has ground under it. Walking once used the city's acoustic bounds, a kilometre out for its
/// aircraft, past ground that stops at about 500 m.
/// </summary>
public class MapEdgeTests
{
    [Fact]
    public void EveryCornerOfTheWalkableAreaHasGroundUnderIt()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        var missing = new List<string>();
        foreach (var (id, (world, _, grid, _, data)) in maps.GetAllMaps())
        {
            Vector3 lo = data.WalkMin, hi = data.WalkMax;
            foreach (var (x, z) in new[] { (lo.X + 1f, lo.Z + 1f), (lo.X + 1f, hi.Z - 1f), (hi.X - 1f, lo.Z + 1f), (hi.X - 1f, hi.Z - 1f) })
            {
                float ground = PhysicsUtils.GetGroundHeight(world, grid, new Vector3(x, 1.0f, z), out _);
                if (ground < data.MinimumY)
                    missing.Add($"{id} at ({x:F0}, {z:F0})");
            }
        }
        Assert.True(missing.Count == 0, "no ground under the edge of the walkable area: " + string.Join("; ", missing));
    }
}
