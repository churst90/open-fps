using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// What the city's parts are called, and what a scan says about the place you are standing in.
///
/// "I need to hear what I ran into ... something like 'front entrance to x building' or 'west side of
/// parking garage' or, just 'parking garage' and such. ground doesn't need to be announced, that's
/// what z is for to know where I'm standing" (Cody, 2026-10-04). The generator named most parts by
/// their prefab, so a scan on Brandt Court's roof said "Brick Wall", and one in Marlow Tower's
/// stairwell said "Concrete Floor" twice and the stairwell itself.
/// </summary>
public class CityPartNamesTests : IClassFixture<CityPartNamesTests.City>
{
    private readonly City _city;
    private readonly ITestOutputHelper _o;

    public CityPartNamesTests(City city, ITestOutputHelper o) { _city = city; _o = o; }

    /// <summary>The city's map file, read as the generator wrote it.</summary>
    public sealed class City
    {
        public readonly List<(string Prefab, string? Name, Vector3 Centre, Vector3 Size)> Parts = new();
        public readonly Dictionary<string, (string Name, bool Solid, bool Door)> Prefabs = new();
        public readonly List<string> Towers = new() { "Marlow Tower", "Kestrel House", "Union Building", "Brandt Court", "Selby House" };

        public City()
        {
            foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "prefabs"), "*.json"))
            {
                if (path.EndsWith("prefab-schema.json")) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var p = doc.RootElement;
                bool collider = p.TryGetProperty("ColliderSize", out _);
                bool solid = collider && (!p.TryGetProperty("IsSolid", out var s) || s.GetBoolean());
                bool door = p.TryGetProperty("IsDoor", out var d) && d.GetBoolean();
                Prefabs[p.GetProperty("Id").GetString()!] = (p.GetProperty("Name").GetString()!, solid, door);
            }
            using var map = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "maps", "city.json")));
            foreach (var e in map.RootElement.GetProperty("Entities").EnumerateArray())
            {
                string prefab = e.GetProperty("PrefabId").GetString()!;
                string? name = e.TryGetProperty("Name", out var n) ? n.GetString() : null;
                var pos = e.GetProperty("Position");
                var scale = e.TryGetProperty("Scale", out var sc) ? sc : default;
                Vector3 size = scale.ValueKind == JsonValueKind.Object
                    ? new Vector3(scale.GetProperty("X").GetSingle(), scale.GetProperty("Y").GetSingle(), scale.GetProperty("Z").GetSingle())
                    : Vector3.One;
                Parts.Add((prefab, name, new Vector3(pos.GetProperty("X").GetSingle(), pos.GetProperty("Y").GetSingle(), pos.GetProperty("Z").GetSingle()), size));
            }
        }
    }

    [Fact]
    public void EveryPartYouCanRunIntoIsNamedForWhereAndWhatItIs()
    {
        var bare = new List<string>();
        int named = 0;
        foreach (var p in _city.Parts)
        {
            if (!_city.Prefabs.TryGetValue(p.Prefab, out var t) || !(t.Solid || t.Door)) continue;
            if (string.IsNullOrWhiteSpace(p.Name) || p.Name == t.Name)
                bare.Add($"{p.Prefab} at {p.Centre} is '{p.Name ?? t.Name}'");
            else named++;
        }
        _o.WriteLine($"{named} solid parts named, {bare.Count} not");
        foreach (var b in bare.Take(30)) _o.WriteLine(b);
        Assert.Empty(bare);

        // Nothing on the city is called by its material.
        var materialNames = new[] { "Brick Wall", "Concrete Wall", "Concrete Floor", "Plaster Wall", "Metal Wall", "Glass Wall" };
        Assert.DoesNotContain(_city.Parts, p => materialNames.Contains(p.Name));
    }

    [Fact]
    public void EveryTowerNamesItsWallsAndItsFrontEntrance()
    {
        foreach (var tower in _city.Towers)
        {
            foreach (var side in new[] { "north", "south", "east", "west" })
                Assert.Contains(_city.Parts, p => p.Name == $"{tower} {side} wall");
            var entrance = _city.Parts.Where(p => p.Name == $"{tower} front entrance").ToList();
            Assert.Single(entrance);
            Assert.True(_city.Prefabs[entrance[0].Prefab].Door, $"{tower}'s front entrance is not a door");
            Assert.Contains(_city.Parts, p => p.Name!?.StartsWith($"{tower} roof parapet, ") == true);
            Assert.Contains(_city.Parts, p => p.Name == $"{tower} stairwell wall");
            Assert.Contains(_city.Parts, p => p.Name == $"{tower} corridor wall, floor 0");
        }
        // Every house has a front door that says whose it is.
        int houses = _city.Parts.Count(p => p.Prefab == "acoustic_region" && Regex.IsMatch(p.Name ?? "", @"^\d+ \w+ Street$"));
        int frontDoors = _city.Parts.Count(p => Regex.IsMatch(p.Name ?? "", @"^\d+ \w+ Street front door$"));
        Assert.True(houses > 50, $"{houses} houses");
        Assert.Equal(houses, frontDoors);
        // ...and the garage says which side of it you are at.
        Assert.Contains(_city.Parts, p => p.Name == "Parking garage west side, level 2");
        Assert.Contains(_city.Parts, p => p.Name == "Terminal front entrance");
    }

    // ── The scan, from the two places it was heard ──────────────────────────────────────────────

    /// <summary>Brandt Court's roof, where the scan said "Brandt Court roof, 0 metres" and "Brick Wall".</summary>
    private static readonly Vector3 BrandtRoof = new(-25.7f, 18.25f, 155.4f);

    /// <summary>Marlow Tower's stairwell on floor 2, on the landing at the end of the shaft: the scan
    /// said the stairwell, the one under it, "Brick Wall" and "Concrete Floor" twice, all at 0 metres.</summary>
    private static readonly Vector3 MarlowStairs2 = new(-14.0f, 6.28f, -110.6f);

    [Fact]
    public void AScanOnARoofNamesTheRoofsEdgesAndNotTheRoof()
    {
        var said = Scan(BrandtRoof);
        Assert.NotEmpty(said);
        Assert.DoesNotContain(said, s => s == "Brandt Court roof");
        Assert.All(said, s => Assert.DoesNotContain("Brick", s));
        Assert.Contains(said, s => s.StartsWith("Brandt Court roof parapet"));
        Assert.Equal(said.Count, said.Distinct().Count());
    }

    [Fact]
    public void AScanInAStairwellNamesItsWallsAndNotItsFloors()
    {
        var said = Scan(MarlowStairs2);
        Assert.NotEmpty(said);
        Assert.DoesNotContain(said, s => s.StartsWith("Marlow Tower stairwell, floor"));     // the place you are in
        Assert.DoesNotContain(said, s => s.StartsWith("Marlow Tower floor"));                // what you stand on
        Assert.All(said, s => Assert.DoesNotContain("Concrete", s));
        Assert.All(said, s => Assert.DoesNotContain("Brick", s));
        Assert.Contains(said, s => s.StartsWith("Marlow Tower ") && s.Contains("wall"));
    }

    /// <summary>The names a scan says from a spot, in the order it says them.</summary>
    private List<string> Scan(Vector3 feet)
    {
        var rig = new Rig(feet);
        string reply = rig.Run("scan");
        _o.WriteLine(reply);
        return Regex.Matches(reply, @"(?:^|\. )(.+?), ((?:left |right )?(?:in front|behind)|left|right), (\d+) metres?")
                    .Select(m => m.Groups[1].Value).ToList();
    }

    private sealed class Rig
    {
        private readonly SessionManager _sessions = new();
        private readonly CommandHandler _commands;
        private readonly GameServer _server;
        private readonly UserSession _me;

        public Rig(Vector3 feet)
        {
            string mapDir = Path.Combine(Path.GetTempPath(), "openfps-names-" + Guid.NewGuid().ToString("N"), "maps");
            Directory.CreateDirectory(mapDir);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "city.json"), Path.Combine(mapDir, "city.json"));
            AcousticRegistry.Initialize();
            var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            var maps = new MapManager(new MapRepository(mapDir), prefabs);
            maps.Initialize();
            Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
            _server = new GameServer(new NoUsers());
            _commands = new CommandHandler(_sessions, maps, _server);
            var entity = world.Create(
                new PlayerComponent { ConnectionId = 1, Username = "me" },
                EntityType.Player,
                new Transform { Position = feet, Rotation = Quaternion.Identity },
                new Velocity(), new MaterialComponent { Material = "Generic" },
                new NameComponent { Name = "me" },
                new HealthComponent { Current = 100, Max = 100 },
                new ColliderComponent
                {
                    Shape = ColliderShape.Cylinder,
                    Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight, PhysicsConstants.PlayerRadius * 2),
                    IsSolid = true,
                });
            maps.IndexEntity("city", entity);
            _me = new UserSession { ConnectionId = 1, Username = "me", Entity = entity, CurrentMapId = "city", Welcomed = true };
            _sessions.AddSession(1, _me);
            try { Directory.Delete(Path.GetDirectoryName(mapDir)!, true); } catch { }
        }

        public string Run(string command)
        {
            var said = new List<string>();
            _commands.HandleTextCommand(_me.ConnectionId, new TextCommand { Command = command, Args = Array.Empty<string>() },
                m => { if (m is TextEvent t) said.Add(t.Text); });
            _server.DrainCommandBuffer();
            return string.Join(" | ", said);
        }
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }
}
