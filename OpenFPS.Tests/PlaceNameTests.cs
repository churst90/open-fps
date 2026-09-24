using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// "Sidewalks aren't labeled as sidewalks still, they're just 'outside'."
///
/// Two faults behind one report. The map-wide outdoor region is always in the acoustic map, so the
/// lookup that guarded the name-from-the-ground fallback always succeeded and that fallback never
/// ran: every unboxed metre of open ground was "Outside". And the city's cross streets — Dock,
/// Central, Foundry, North — named their carriageways but not their pavements, so a player walking
/// the footway of any of them was on exactly that unboxed ground.
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

    /// <summary>
    /// Walk both pavements of every east-west street on the real city map, through the same region
    /// lookup and ground probe the client uses, and every step is named as a pavement.
    /// </summary>
    [Fact]
    public void TheCrossStreetPavementsAreNamedPavements()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out Vector3 size, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));

        var client = new ClientWorldState();
        client.Clear(data.Size, data.MinBound, data.MaxBound);
        var defs = EntityDefinitionFactory.StaticDefinitions(world).ToList();
        foreach (var def in defs) client.RegisterDefinition(def);
        client.SetAcousticMap(AcousticVolumeGenerator.GenerateRegions(defs, size, data.MinBound,
                                                                      data.VoxelResolution, data.OcclusionFloor));
        var snap = client.GetSnapshot();
        var spatial = new SpatialService();

        // The cross streets, found from the map's own carriageway regions ("Dock Street, block 1").
        var carriageways = defs.Where(d => Regex.IsMatch(d.Region.FriendlyName ?? "", @"^\w+ Street, block \d+$")
                                           && !d.Region.FriendlyName.StartsWith("Main"))
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
        Assert.True(wrong.Count == 0, $"{wrong.Count} pavement step(s) not named a pavement: {string.Join("  ", wrong.Take(12))}");

        // And a street is not a room. The region prefab defaults to IsIndoor, which would read as
        // full shelter (rain off, "Under Shelter") — the load survey has to measure every cross
        // street and its pavements as open. (Main Street's block 2 is correctly covered: two-thirds
        // of it lies under the tunnel roof.)
        var indoor = defs.Where(d => Regex.IsMatch(d.Region.FriendlyName ?? "", @"^(Dock|Central|Foundry|North) Street( \w+ pavement)?, block \d+$")
                                     && d.Region.IsIndoor)
                         .Select(d => d.Region.FriendlyName).ToList();
        Assert.True(indoor.Count == 0, $"{indoor.Count} street region(s) marked indoor: {string.Join(", ", indoor.Take(8))}");
    }
}
