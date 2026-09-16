using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The speedway has to be able to say where you are standing.
///
/// It could not, and nothing caught it, because "no regions at all" is indistinguishable from
/// "working correctly" to every other test: the map loaded, the cars ran, the walls reflected, and a
/// player walking the whole circuit was told "Outside" once and then nothing for ever. A blind
/// player on a two-kilometre loop of track that is the same width and the same surface the whole way
/// round has no other way to know which part of it they are on.
/// </summary>
public class SpeedwayZoneTests
{
    private static (List<EntityDefinition> Defs, AcousticMap Map) LoadSpeedway()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        var manager = new MapManager(maps, prefabs);
        manager.Initialize();

        Assert.True(manager.TryGetMap("speedway", out World world, out Vector3 size, out _, out _),
            "maps/speedway.json failed to load.");

        var data = manager.GetAllMaps().First(kv => kv.Key == "speedway").Value.data;
        var defs = EntityDefinitionFactory.StaticDefinitions(world);
        var acoustic = AcousticVolumeGenerator.GenerateRegions(defs, size, data.MinBound, 1.0f);
        return (defs, acoustic);
    }

    /// <summary>Every named place the generator lays down actually reaches the acoustic map.</summary>
    [Fact]
    public void TheSpeedwayNamesItsPlaces()
    {
        var (defs, _) = LoadSpeedway();
        var names = defs.Where(d => d.Region.RoomSize.X > 0)
                        .Select(d => d.Region.FriendlyName)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToHashSet();

        foreach (string expected in new[]
                 { "Front straight", "Turns one and two", "Back straight", "Turns three and four",
                   "Infield", "Grandstand" })
            Assert.True(names.Contains(expected), $"the speedway has no region called '{expected}'; it has: {string.Join(", ", names)}");
    }

    /// <summary>
    /// A region placed by a map is the size the MAP asked for, not the size its prefab happens to be.
    ///
    /// The acoustic volume did not follow the entity's scale, so every region in every map was the
    /// prefab's 10 x 5 x 10 however it had been scaled — while its COLLIDER scaled correctly, which
    /// is what made it look right everywhere except where it mattered.
    /// </summary>
    [Fact]
    public void ARegionIsTheSizeTheMapAskedFor()
    {
        var (defs, _) = LoadSpeedway();
        var infield = defs.Single(d => d.Region.FriendlyName == "Infield");
        Assert.True(infield.Region.RoomSize.X > 200f,
            $"the infield should be a couple of hundred metres across; it is {infield.Region.RoomSize.X:F0} m — the scale was ignored.");
        Assert.True(infield.Region.RoomSize.Z > 200f);
    }

    /// <summary>
    /// Walking the racing line names all four sectors, in order, and never leaves you nameless.
    ///
    /// This is the test that would have caught the original fault: it walks the real centreline out
    /// of the real map and asks the real voxel grid what it is standing in.
    /// </summary>
    [Fact]
    public void WalkingTheLapNamesEverySector()
    {
        var (_, acoustic) = LoadSpeedway();

        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        var manager = new MapManager(maps, prefabs);
        manager.Initialize();
        var data = manager.GetAllMaps().First(kv => kv.Key == "speedway").Value.data;
        var line = data.Tracks![0].Waypoints;

        var seen = new List<string>();
        var gaps = new List<string>();
        for (int i = 0; i < line.Count; i++)
        {
            var w = line[i];
            int id = acoustic.VoxelGrid.GetRegionAt(new Vector3(w.X, w.Y + 1.7f, w.Z));
            string name = acoustic.Regions.TryGetValue(id, out var r) ? r.FriendlyName : "";
            if (name is "" or "Outside")
                gaps.Add($"[{i}] ({w.X:F1},{w.Y:F2},{w.Z:F1}) id={id}");
            if (seen.Count == 0 || seen[^1] != name) seen.Add(name);
        }
        Assert.True(gaps.Count == 0, $"{gaps.Count} waypoint(s) are in no named zone: {string.Join("  ", gaps.Take(12))}");

        Assert.DoesNotContain("", seen);
        Assert.DoesNotContain("Outside", seen);
        foreach (string expected in new[]
                 { "Front straight", "Turns one and two", "Back straight", "Turns three and four" })
            Assert.True(seen.Contains(expected),
                $"walking the lap never entered '{expected}'; it passed through: {string.Join(" -> ", seen)}");
    }
}
