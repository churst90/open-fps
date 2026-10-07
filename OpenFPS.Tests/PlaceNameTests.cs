using System.Numerics;
using System.Text.RegularExpressions;
using Arch.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// "Sidewalks aren't labeled as sidewalks still, they're just 'outside'." Two faults: the map-wide outdoor
/// region always matched, so the name-from-the-ground fallback never ran; and the cross streets named
/// their carriageways but not their pavements.
/// </summary>
public class PlaceNameTests
{
    private readonly ITestOutputHelper _o;
    public PlaceNameTests(ITestOutputHelper o) => _o = o;

    private static AcousticMap EmptyMap() => AcousticVolumeGenerator.GenerateRegions(
        new List<EntityDefinition>(), new Vector3(100, 20, 100), new Vector3(-50, -5, -50), 1.0f);

    /// <summary>With no named place over you, the ground names it — never the outdoor region's "Outside".</summary>
    [Theory]
    [InlineData("Concrete", "sidewalk")]
    [InlineData("Asphalt", "road")]
    [InlineData("Grass", "grass")]
    public void OpenGroundIsNamedFromWhatYouStandOn(string material, string expected)
    {
        var map = EmptyMap();
        Assert.True(map.Regions.ContainsKey(AcousticConstants.GlobalRegionId), "the outdoor region is what used to win");
        Assert.Equal(expected, ClientAudioSystem.NameOfPlace(map, AcousticConstants.GlobalRegionId, material, 0f));
    }

    /// <summary>A named place still says its own name, whatever is underfoot.</summary>
    [Fact]
    public void ANamedPlaceStillWins()
    {
        var map = EmptyMap();
        map.Regions[42] = new RegionComponent { FriendlyName = "Market Square", RoomSize = new Vector3(10, 5, 10) };
        Assert.Equal("Market Square", ClientAudioSystem.NameOfPlace(map, 42, "Concrete", 0f));
    }

    /// <summary>Ground the table does not know keeps the outdoor region's own name rather than nothing.</summary>
    [Fact]
    public void UnknownGroundFallsBackToTheOutdoorName()
    {
        Assert.Equal("Outside", ClientAudioSystem.NameOfPlace(EmptyMap(), AcousticConstants.GlobalRegionId, "Generic", 0f));
    }

    /// <summary>A doorway, in no zone, is named from the zones either side: the one between Union Building's
    /// corridor and flat 00B said "Under Shelter".</summary>
    [Fact]
    public void ADoorwayIsNamedFromTheZonesEitherSide()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out Vector3 size, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var client = new ClientWorldState();
        client.Clear(data.Size);
        var defs = EntityDefinitionFactory.StaticDefinitions(world).ToList();
        foreach (var def in defs) client.RegisterDefinition(def);
        client.SetAcousticMap(AcousticVolumeGenerator.GenerateRegions(defs, size, data.MinBound,
                                                                      data.VoxelResolution, data.OcclusionFloor));
        var snap = client.GetSnapshot();
        var spatial = new SpatialService();

        var eye = new Vector3(21.55f, 1.7f, 28.28f);
        Assert.Equal(AcousticConstants.GlobalRegionId, spatial.GetRegionAt(snap, eye));
        string? name = ClientAudioSystem.NameOfGap(snap, eye, p => spatial.GetRegionAt(snap, p));
        Assert.NotNull(name);
        Assert.StartsWith(ClientAudioSystem.DoorwayPrefix + " between", name);
        Assert.Contains("Union Building corridor, floor 0", name);
        Assert.Contains("Union Building flat 00B", name);
    }

    /// <summary>Every step along both pavements of every east-west city street is named as a pavement,
    /// through the client's region lookup and ground probe.</summary>
    [Fact]
    public void TheCrossStreetPavementsAreNamedPavements()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out Vector3 size, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));

        var client = new ClientWorldState();
        client.Clear(data.Size);
        var defs = EntityDefinitionFactory.StaticDefinitions(world).ToList();
        foreach (var def in defs) client.RegisterDefinition(def);
        client.SetAcousticMap(AcousticVolumeGenerator.GenerateRegions(defs, size, data.MinBound,
                                                                      data.VoxelResolution, data.OcclusionFloor));
        var snap = client.GetSnapshot();
        var spatial = new SpatialService();

        // The cross streets, found from the map's own carriageway regions ("Dock Street, block 1").
        var carriageways = defs.Where(d => d.Region.FriendlyName is { } name
                                           && Regex.IsMatch(name, @"^\w+ Street, block \d+$")
                                           && !name.StartsWith("Main"))
                               .ToList();
        Assert.True(carriageways.Select(d => d.Region.FriendlyName.Split(',')[0]).Distinct().Count() >= 4,
            "expected Dock, Central, Foundry and North Street");

        int walked = 0;
        var wrong = new List<string>();
        foreach (var c in carriageways)
        {
            var p = c.Transform.Position;
            float half = c.Region.RoomSize.X * 0.5f;
            float kerb = c.Region.RoomSize.Z * 0.5f;
            foreach (float side in new[] { -1f, 1f })
            {
                float z = p.Z + side * (kerb + 1.75f);           // the middle of a 3.5 m pavement
                for (float x = p.X - half + 1f; x < p.X + half - 1f; x += 3f)
                {
                    var feet = new Vector3(x, 1.0f, z);
                    float ground = PhysicsUtils.GetGroundHeight(snap, feet, -1, out string material);
                    if (material != "Concrete") continue;        // a crossing, a kerb gap: not the footway
                    var eye = new Vector3(x, ground + 1.7f, z);
                    int region = spatial.GetRegionAt(snap, eye);
                    string name = ClientAudioSystem.NameOfPlace(snap.AcousticMap, region, material, 0f);
                    walked++;
                    if (name == "Outside" || !(name.Contains("pavement") || name.Contains("sidewalk")))
                        wrong.Add($"({x:F0},{z:F1}) '{name}'");
                }
            }
        }
        _o.WriteLine($"{walked} pavement steps on {carriageways.Count} blocks of cross street");
        Assert.True(walked > 100, $"only {walked} concrete steps found beside the cross streets");
        Assert.True(wrong.Count == 0, $"{wrong.Count} pavement step(s) not named a sidewalk: {string.Join("  ", wrong.Take(12))}");

        // A street is not a room: the region prefab defaults to IsIndoor, so the load survey must measure
        // the cross streets as open. (Main Street's block 2 is rightly covered, under the tunnel roof.)
        var indoor = defs.Where(d => Regex.IsMatch(d.Region.FriendlyName ?? "", @"^(Dock|Central|Foundry|North) Street(, block \d+| sidewalk)$")
                                     && d.Region.IsIndoor)
                         .Select(d => d.Region.FriendlyName).ToList();
        Assert.True(indoor.Count == 0, $"{indoor.Count} street region(s) marked indoor: {string.Join(", ", indoor.Take(8))}");
    }
}
