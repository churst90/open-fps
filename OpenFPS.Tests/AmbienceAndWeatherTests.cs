using System.Numerics;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Arch.Core;

namespace OpenFPS.Tests;

/// <summary>
/// An ambience id reaching the client, and the weather staying put when asked. `AmbienceId` was once a
/// dead field (in the spec, the component and the wire, and read by no client), so the value is walked
/// the whole way rather than checked at any one link.
/// </summary>
public class AmbienceAndWeatherTests
{
    private static string MapDirectory => System.IO.Path.Combine(AppContext.BaseDirectory, "maps");

    /// <summary>
    /// No shipped map lays a recorded loop over the world (Cody, 2026-09-18: the outdoor loop "needs to
    /// come out, it doesn't add anything"; docs/SOUND_INVENTORY.md, "Not wanted"). A bed is made by
    /// nothing, so it cannot be occluded or walked round and says nothing about where you are. A region
    /// may still name an ambience: that is a sound with a place in it.
    /// </summary>
    [Fact]
    public void NoShippedMapLaysARecordedBedOverTheWorld()
    {
        foreach (var data in new MapRepository(MapDirectory).LoadAll())
            Assert.True(string.IsNullOrWhiteSpace(data.AmbienceId),
                $"map '{data.Id}' names the ambience bed '{data.AmbienceId}'; what a place sounds like has to come from what is in it");
    }

    [Fact]
    public void ARegionsAmbienceSurvivesTheTripFromPrefabToEntityDefinition()
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
            var entity = repo.Spawn(world, "hum_room", Vector3.Zero);

            // On the component...
            Assert.Equal("AMBIENCE/machine_hum", world.Get<RegionComponent>(entity).AmbienceId);

            // ...and on the wire.
            var defs = EntityDefinitionFactory.StaticDefinitions(world);
            Assert.Contains(defs, d => d.Region.AmbienceId == "AMBIENCE/machine_hum");
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheMapsAmbienceIsCarriedOnTheManifest()
    {
        // The manifest is the only thing the client sees before the world arrives, so an outdoor bed
        // that is not on it cannot start until something else happens to mention it.
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openfps-amb-" + Guid.NewGuid().ToString("N"));
        string mapDir = System.IO.Path.Combine(dir, "maps");
        System.IO.Directory.CreateDirectory(mapDir);
        try
        {
            var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(System.IO.Path.Combine(AppContext.BaseDirectory, "prefabs")));
            maps.Initialize();
            var woods = MapTemplates.Flat("woods", "tester");
            woods.AmbienceId = "AMBIENCE/woods_mid_day";
            Assert.True(maps.CreateMap(woods, out string e1), e1);
            Assert.True(maps.CreateMap(MapTemplates.Flat("bare", "tester"), out string e2), e2);

            var sessions = new SessionManager();
            var server = new OpenFPS.Server.GameServer(new NoUsers());
            server.Attach(maps, sessions, new OccupancyService(maps), new HandsService(maps));
            var sent = new List<IMessage>();
            server.Sent = (_, m) => sent.Add(m);

            MapManifest ManifestFor(string mapId)
            {
                sent.Clear();
                server.SendManifest(new UserSession { ConnectionId = 1, Username = "tester", CurrentMapId = mapId });
                return Assert.Single(sent.OfType<MapManifest>());
            }

            Assert.Equal("AMBIENCE/woods_mid_day", ManifestFor("woods").AmbienceId);
            Assert.Equal("", ManifestFor("bare").AmbienceId);
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
