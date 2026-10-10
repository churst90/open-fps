using System.Numerics;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Arch.Core;

namespace OpenFPS.Tests;

/// <summary>
/// No ambience beds, and the weather staying put when asked. A bed is a recorded loop made by nothing in the world:
/// it cannot be occluded or walked round and says nothing about where you are. The map's outdoor bed went on
/// 2026-09-18 ("it doesn't add anything"), and the region beds with the rest of the machinery on 2026-10-10 (Cody:
/// no beds at all). The fields stay on the wire, unread.
/// </summary>
public class AmbienceAndWeatherTests
{
    private static string MapDirectory => System.IO.Path.Combine(AppContext.BaseDirectory, "maps");

    /// <summary>No shipped map file or prefab names an ambience bed.</summary>
    [Fact]
    public void NoShippedMapOrPrefabNamesABed()
    {
        var named = new System.Text.RegularExpressions.Regex("\"AmbienceId\"\\s*:\\s*\"[^\"]");
        foreach (var dir in new[] { MapDirectory, System.IO.Path.Combine(AppContext.BaseDirectory, "prefabs") })
            foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.json", System.IO.SearchOption.AllDirectories))
                Assert.False(named.IsMatch(System.IO.File.ReadAllText(file)), $"{file} names an ambience bed");
    }

    /// <summary>A prefab that still names an ambience is refused at load, as any field the loader does not read is.</summary>
    [Fact]
    public void APrefabNamingABedIsRefused()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openfps-amb-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "hum_room.json"), """
            { "Id": "hum_room", "Name": "Machine Room", "Type": "StaticObject", "Material": "None",
              "IsSolid": false, "ColliderSize": { "X": 10, "Y": 5, "Z": 10 },
              "IsIndoor": true, "RoomSize": { "X": 10, "Y": 5, "Z": 10 },
              "AmbienceId": "AMBIENCE/machine_hum" }
            """);
            var repo = new PrefabRepository(dir);
            using var world = World.Create();
            var e = Assert.ThrowsAny<Exception>(() => repo.Spawn(world, "hum_room", Vector3.Zero));
            Assert.Contains("REJECTED", e.Message);
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    /// <summary>The manifest's field is sent empty.</summary>
    [Fact]
    public void TheManifestCarriesNoBed()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openfps-amb-" + Guid.NewGuid().ToString("N"));
        string mapDir = System.IO.Path.Combine(dir, "maps");
        System.IO.Directory.CreateDirectory(mapDir);
        try
        {
            var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(System.IO.Path.Combine(AppContext.BaseDirectory, "prefabs")));
            maps.Initialize();
            Assert.True(maps.CreateMap(MapTemplates.Flat("bare", "tester"), out string e), e);
            var server = new OpenFPS.Server.GameServer(new NoUsers());
            server.Attach(maps, new SessionManager(), new OccupancyService(maps), new HandsService(maps));
            var sent = new List<IMessage>();
            server.Sent = (_, m) => sent.Add(m);
            server.SendManifest(new UserSession { ConnectionId = 1, Username = "tester", CurrentMapId = "bare" });
            Assert.Equal("", Assert.Single(sent.OfType<MapManifest>()).AmbienceId);
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }

    [Fact]
    public void TheSirensAreGoneFromTheShippedMap()
    {
        var data = new MapRepository(MapDirectory).LoadAll().Single(m => m.Id == "default");
        Assert.DoesNotContain(data.Entities, e =>
            string.Equals(e.PrefabId, "chirp_beacon", StringComparison.OrdinalIgnoreCase));
    }

    // ── Weather ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PinningTheWeatherStopsFrontsRollingIn()
    {
        // Precipitation and temperature swap the material under the player's feet, so weather changing
        // on its own mid-session reads as a bug in whatever was actually being listened to.
        var previous = Environment.GetEnvironmentVariable("OPENFPS_WEATHER");
        try
        {
            Environment.SetEnvironmentVariable("OPENFPS_WEATHER", "Clear");
            var system = new WorldEnvironmentSystem(new Random(1));

            Assert.Equal(WeatherType.Clear, system.CurrentScenario);
            Assert.Equal(0, system.FrontProbabilityPerTick);

            // A full simulated hour: with fronts disabled the scenario cannot move.
            for (int i = 0; i < 30 * 60 * 60; i++) system.Update(1f / 30f);
            Assert.Equal(WeatherType.Clear, system.CurrentScenario);
        }
        finally { Environment.SetEnvironmentVariable("OPENFPS_WEATHER", previous); }
    }

    [Fact]
    public void AnUnrecognisedWeatherNameIsReportedRatherThanObeyed()
    {
        var previous = Environment.GetEnvironmentVariable("OPENFPS_WEATHER");
        try
        {
            Environment.SetEnvironmentVariable("OPENFPS_WEATHER", "drizzle-ish");
            var system = new WorldEnvironmentSystem(new Random(1));
            // Left rolling normally rather than silently pinned to whatever Clear happens to be.
            Assert.True(system.FrontProbabilityPerTick > 0);
        }
        finally { Environment.SetEnvironmentVariable("OPENFPS_WEATHER", previous); }
    }

    [Fact]
    public void WithoutThePinTheWeatherStillRolls()
    {
        var previous = Environment.GetEnvironmentVariable("OPENFPS_WEATHER");
        try
        {
            Environment.SetEnvironmentVariable("OPENFPS_WEATHER", null);
            var system = new WorldEnvironmentSystem(new Random(1));
            Assert.True(system.FrontProbabilityPerTick > 0);
        }
        finally { Environment.SetEnvironmentVariable("OPENFPS_WEATHER", previous); }
    }
}
