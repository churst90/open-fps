using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// What a driver is TOLD, on the real city: the road's name, the junction ahead and its exits, which
/// lane, which way. "Z doesn't tell me if I'm on the street or not, so I have no clue where I'm
/// turning or when" — these are the answers to that, checked against the map rather than a toy.
/// </summary>
public class DrivingAidsTests
{
    private readonly ITestOutputHelper _o;
    public DrivingAidsTests(ITestOutputHelper o) => _o = o;

    private const int CarId = 999_001;

    private static ClientWorldState City()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var client = new ClientWorldState();
        client.Clear(data.Size, data.MinBound, data.MaxBound);
        foreach (var def in EntityDefinitionFactory.StaticDefinitions(world)) client.RegisterDefinition(def);
        return client;
    }

    private static (List<string> Said, DrivingAids Aids) Drive(ClientWorldState client, Vector3 at, float headingDegrees)
        => Drive(client, at, headingDegrees, 0f);

    private static (List<string> Said, DrivingAids Aids) Drive(ClientWorldState client, Vector3 at, float headingDegrees, float speed)
    {
        var car = new EntityDefinition
        {
            EntityId = CarId,
            Type = EntityType.NPC,
            Moves = true,
            Transform = new Transform { Position = at, Rotation = Quaternion.CreateFromYawPitchRoll(headingDegrees * MathF.PI / 180f, 0f, 0f) },
        };
        car.SoundEmitter.SoundId = "engine:i4_economy";
        car.SoundEmitter.IsSynth = true;
        client.RegisterDefinition(car);
        if (speed > 0f)
        {
            float h = headingDegrees * MathF.PI / 180f;
            client.SyncState(new[] { new EntityState
            {
                EntityId = CarId,
                Transform = QuantizedTransform.FromTransform(car.Transform),
                LinearVelocity = new Vector3(MathF.Sin(h), 0f, MathF.Cos(h)) * speed,
            } });
        }

        var said = new List<string>();
        var aids = new DrivingAids(new AudioEngineFacade());      // never initialised: every sound is a no-op
        aids.Announce += said.Add;
        var state = new LocalPlayerState { RidingEntityId = CarId, RidingControls = true };
        aids.Update(client.GetSnapshot(), state, 1.0);
        return (said, aids);
    }

    [Fact]
    public void OnMainStreetNorthboundYouAreToldTheRoadTheLaneAndTheJunctionAhead()
    {
        var client = City();
        // The spawn: Main Street, in the kerb lane heading north.
        var (said, aids) = Drive(client, new Vector3(4.5f, 0.15f, -40f), 0f);
        foreach (var s in said) _o.WriteLine("said: " + s);
        _o.WriteLine("Z: " + aids.Readout);
        Assert.Contains(said, s => s.StartsWith("Main Street, heading north"));
        Assert.Contains(said, s => s.StartsWith("Junction in"));
        Assert.NotNull(aids.Readout);
        Assert.Contains("right lane", aids.Readout);
    }

    [Fact]
    public void FacingTheWrongWayInThatLaneIsTheWrongSideOfTheRoad()
    {
        var client = City();
        var (_, aids) = Drive(client, new Vector3(4.5f, 0.15f, -40f), 180f);
        _o.WriteLine("Z: " + aids.Readout);
        Assert.Contains("wrong side", aids.Readout);
    }

    /// <summary>
    /// Off the road, you are told where the road is. From the first garage bay, nose east, Main
    /// Street is a few metres AHEAD — the garage opens onto it.
    /// </summary>
    [Fact]
    public void OffTheRoadTheReadoutSaysWhereTheNearestRoadIs()
    {
        var client = City();
        var (_, aids) = Drive(client, new Vector3(-16f, 0.25f, 29f), 90f);
        _o.WriteLine("Z: " + aids.Readout);
        Assert.Contains("Main Street", aids.Readout);
        Assert.Contains("ahead", aids.Readout);

        // Turned to face north in the same place, the road is on your RIGHT.
        var (_, north) = Drive(City(), new Vector3(-16f, 0.25f, 29f), 0f);
        _o.WriteLine("Z: " + north.Readout);
        Assert.Contains("to your right", north.Readout);
    }

    /// <summary>
    /// "It's hard to know how far I'm turning, and I overshoot the lane." Pointed twenty degrees off
    /// Main Street at town speed, lane assist steers back toward the middle of the lane — left if
    /// you are pointing right of the road, right if left — and Z says how far off you are. Turned
    /// sixty degrees away you are turning on purpose, and it keeps its hands off.
    /// </summary>
    [Theory]
    [InlineData(20f, -1)]
    [InlineData(-20f, +1)]
    public void LaneAssistSteersBackToTheLane(float offNorth, int expectedSign)
    {
        var (_, aids) = Drive(City(), new Vector3(4.5f, 0.15f, -40f), offNorth, 10f);
        _o.WriteLine($"{offNorth}: assist {aids.AssistSteer}, Z: {aids.Readout}");
        Assert.NotNull(aids.AssistSteer);
        Assert.Equal(expectedSign, MathF.Sign(aids.AssistSteer!.Value));
        Assert.Contains($"pointing 20 degrees {(offNorth > 0 ? "right" : "left")} of the road", aids.Readout);
    }

    [Fact]
    public void LaneAssistLetsGoWhenYouAreTurningOnPurpose()
    {
        var (_, aids) = Drive(City(), new Vector3(4.5f, 0.15f, -40f), 60f, 10f);
        Assert.Null(aids.AssistSteer);
    }

    /// <summary>
    /// "I'm not hearing any cues." None of them played: a cue follows the listener's head but was
    /// left at the default position, the middle of the map, and the voice manager drops anything
    /// more than one and a half ranges from the listener — everywhere past 120 m from the centre,
    /// which is most of Main Street. Two hundred metres up Main Street, the guide must reach the mixer.
    /// </summary>
    [Fact]
    public void TheGuideBeepIsPlayedFarFromTheMiddleOfTheMap()
    {
        var client = City();
        var provider = new VoiceLifecycleTests.RecordingProvider();
        var facade = new AudioEngineFacade(provider);
        facade.InitializeForTest();

        var at = new Vector3(4.5f, 0.15f, 200f);
        var car = new EntityDefinition
        {
            EntityId = CarId, Type = EntityType.NPC, Moves = true,
            Transform = new Transform { Position = at, Rotation = Quaternion.Identity },
        };
        car.SoundEmitter.SoundId = "engine:i4_economy";
        client.RegisterDefinition(car);
        var aids = new DrivingAids(facade);
        var state = new LocalPlayerState { RidingEntityId = CarId, RidingControls = true, Position = at };
        facade.UpdateListener(at + new Vector3(0f, 1f, 0f), Quaternion.Identity, Vector3.Zero, -1);
        for (int i = 0; i < 5; i++)
        {
            aids.Update(client.GetSnapshot(), state, 1.0 + i);
            for (int k = 0; k < 3; k++) facade.PumpForTest();
        }
        _o.WriteLine("played: " + string.Join(", ", provider.PlayedSounds));
        Assert.Contains(provider.PlayedSounds, s => s.Contains("drive_guide"));
    }

    [Fact]
    public void InTheGarageYouAreOffTheRoadAndNobodySaysSoUntilYouHaveBeenOnOne()
    {
        var client = City();
        var (said, aids) = Drive(client, new Vector3(-16f, 0.25f, 29f), 90f);
        _o.WriteLine("Z: " + aids.Readout);
        Assert.Empty(said);
        Assert.StartsWith("Off the road", aids.Readout);
    }
}
