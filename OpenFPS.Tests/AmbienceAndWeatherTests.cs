using System;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Arch.Core;
using System.Linq;

namespace OpenFPS.Tests;

/// <summary>
/// Cover for the ambience bed reaching the client, and for the weather staying put when asked.
///
/// The ambience half exists because `AmbienceId` was a dead field for the whole life of the project: it
/// was in the prefab spec, the validator accepted it, the repository wrote it into the component and the
/// server serialized it — and no client code ever read it. Every one of those steps looked correct on
/// its own. So the test walks the value the whole way rather than checking any single link.
/// </summary>
public class AmbienceAndWeatherTests
{
    private static string PrefabDirectory => System.IO.Path.Combine(AppContext.BaseDirectory, "prefabs");
    private static string MapDirectory => System.IO.Path.Combine(AppContext.BaseDirectory, "maps");

    [Fact]
    public void TheShippedMapDeclaresAnOutdoorAmbience()
    {
        var data = new MapRepository(MapDirectory).LoadAll().Single(m => m.Id == "default");
        Assert.False(string.IsNullOrWhiteSpace(data.AmbienceId),
            "the default map should name an outdoor ambience bed");
        Assert.StartsWith("AMBIENCE/", data.AmbienceId);
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

            // ...and on the wire, which is the step that was missing.
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
        var manifest = new MapManifest { AmbienceId = "AMBIENCE/woods_mid_day" };
        Assert.Equal("AMBIENCE/woods_mid_day", manifest.AmbienceId);
        Assert.Equal("", new MapManifest().AmbienceId);
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
