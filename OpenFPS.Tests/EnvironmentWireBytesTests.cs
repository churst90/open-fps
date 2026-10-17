using System.Security.Cryptography;
using MemoryPack;
using OpenFPS.Environment;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Systems;
using OpenFPS.Sound;
using OpenFPS.Materials;

namespace OpenFPS.Tests;

/// <summary>
/// What the weather, the water and the lightning are on the wire, byte for byte, as they were before package
/// stage 3 moved them into OpenFPS.Environment (docs/ENGINE_PACKAGES.md, stage 3): a map's weather as the
/// broadcast fills it (the air, the wind, what falls, the water on the roads and over the ground) and a
/// strike's sound. The expected hashes were written on main `13e0f460`, before any change; a value moving
/// between assemblies, or the air's numbers gathered into one value, must not move a byte.
/// </summary>
public class EnvironmentWireBytesTests
{
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A storm on a map, half a minute into it, as <c>GameServer</c>'s broadcast fills its update.</summary>
    private static WorldStateUpdate StormUpdate()
    {
        var env = new WorldEnvironmentSystem(new Random(7)) { FrontProbabilityPerTick = 0 };
        env.SetDate(15.5f, 200);
        env.SetScenario(WeatherType.Storm);
        for (int i = 0; i < 900; i++) env.Update(1f / 30f);
        var state = env.GetStateForMap(new MapAtmosphere(24f, 0.6f, 990f, 1.5f));
        var falling = env.PrecipitationFor(state);

        var roads = new RoadWater();
        var ground = new GroundWater();
        float rain = env.RainRate(state);
        for (int i = 0; i < 120; i++) roads.Step(rain, RoadWaterSystem.Evaporation(state, roads.Drainage), 0.25f);
        // Three hours of it over the ground, so open ground and lawns run off too (their soil decides how much).
        for (int i = 0; i < 19; i++) ground.Step(rain, 600f);
        ground.SetSpills(new[] { new KeyValuePair<int, float>(3, 0.25f), new KeyValuePair<int, float>(11, 4.5f) });

        return new WorldStateUpdate
        {
            GameTime = state.GameTime,
            Temperature = state.Temperature, Humidity = state.Humidity,
            AirPressure = state.AirPressure, AirAbsorptionMultiplier = state.AirAbsorptionMultiplier,
            WindVelocity = state.WindVelocity,
            WindGustiness = state.WindGustiness, PrecipitationIntensity = state.PrecipitationIntensity,
            WindClock = 1234.5, WindTravelEast = env.WindTravel.East, WindTravelNorth = env.WindTravel.North,
            RainRateMmPerHour = falling.RateMmPerHour,
            PrecipitationKind = (int)falling.Kind,
            RainMedianDropMm = falling.MedianDropMm,
            HailDiameterMm = falling.HailMm,
            RoadWater = roads.Save(),
            GroundWater = ground.Save(),
            DayOfYear = state.DayOfYear,
        };
    }

    [Fact]
    public void AMapsWeatherIsTheSameBytes()
    {
        Assert.Equal(Expected.Weather, Sha(MemoryPackSerializer.Serialize<IMessage>(StormUpdate())));
    }

    /// <summary>Every weather a map can hold, and every kind of what falls, by number.</summary>
    [Fact]
    public void TheWeathersAndWhatFallsAreTheSameNumbers()
    {
        Assert.Equal("Clear=0,Rain=1,Snow=2,Storm=3",
                     string.Join(",", Enum.GetValues<WeatherType>().Select(w => $"{w}={(int)w}")));
        Assert.Equal("Rain=0,FreezingRain=1,Sleet=2,Snow=3,Hail=4",
                     string.Join(",", Enum.GetValues<PrecipitationKind>().Select(k => $"{k}={(int)k}")));
    }

    /// <summary>The strikes of a seeded storm, each as the one sound it is sent as.</summary>
    [Fact]
    public void AStrikesSoundIsTheSameBytes()
    {
        var lightning = new LightningSystem(seed: 42);
        var env = new WorldEnvironmentComponent { PrecipitationIntensity = 1f, WindVelocity = new System.Numerics.Vector3(6f, 0f, -3f) };
        var sounds = new List<TransientSound>();
        for (int i = 0; i < 30 * 600 && sounds.Count < 4; i++)
            lightning.Update(1f / 30f, WeatherType.Storm, env, Array.Empty<HeldSky>(), (s, _) => sounds.Add(LightningSystem.SoundFor(s)));
        Assert.Equal(4, sounds.Count);
        Assert.Equal(Expected.Strikes, Sha(sounds.SelectMany(s => MemoryPackSerializer.Serialize(s)).ToArray()));
    }

    /// <summary>The world's air as one value (OutdoorAir) is the component's fields, each where it was: the laws that
    /// read it (thunder, a bullet's and a car's drag) see what the broadcast sends.</summary>
    [Fact]
    public void TheWorldsAirIsOneValueOfItsFields()
    {
        var env = new WorldEnvironmentComponent
        {
            Temperature = 12.5f, Humidity = 0.85f, AirPressure = 990f, WindVelocity = new System.Numerics.Vector3(6f, 0f, -3f),
            WindGustiness = 0.7f, AirAbsorptionMultiplier = 1.5f, PrecipitationIntensity = 0.4f,
        };
        Assert.Equal(new OutdoorAir(12.5f, 0.85f, 990f, new System.Numerics.Vector3(6f, 0f, -3f), 0.7f), env.Outdoors);
        Assert.Equal(new Air(12.5f, 990f), env.Outdoors.Air);
    }

    private static class Expected
    {
        public const string Weather = "81a57a8e95894b8e1901579e8a9c947c2b15f9ce79204dd73c23fffbd351757d";
        public const string Strikes = "f815ca8ee0076e2ede76ae61309a19e6c4a9e50bfe03ab93b76045fa9a13f69a";
    }
}
