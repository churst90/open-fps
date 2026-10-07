using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// The speedway says where you are standing. With no regions at all the map loaded, the cars ran, and a
/// player walking the whole circuit was told "Outside" once and then nothing: on a two-kilometre loop of
/// one width and surface, nothing else says which part you are on.
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

    /// <summary>A region is the size the map asked for, not its prefab's: the acoustic volume ignored the
    /// entity's scale (every region 10 x 5 x 10) while its collider scaled.</summary>
    [Fact]
    public void ARegionIsTheSizeTheMapAskedFor()
    {
        var (defs, _) = LoadSpeedway();
        var infield = defs.Single(d => d.Region.FriendlyName == "Infield");
        Assert.True(infield.Region.RoomSize.X > 200f,
            $"the infield should be a couple of hundred metres across; it is {infield.Region.RoomSize.X:F0} m — the scale was ignored.");
        Assert.True(infield.Region.RoomSize.Z > 200f);
    }

    /// <summary>Walking the real map's racing line names all four sectors in order and never leaves you
    /// nameless.</summary>
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
