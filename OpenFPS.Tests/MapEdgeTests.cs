using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A player cannot walk off the edge of the ground.
///
/// Walking is held inside a map's walkable bounds, so those bounds must not reach past the ground.
/// The city's acoustic bounds reach a kilometre out for its aircraft, walking used them, and the
/// ground stops about 500 m from the centre: you could walk off it, fall 25 m, and be put back at the
/// spawn point by the void check. On every shipped map, each corner of the walkable area, a metre in,
/// has ground under it.
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
