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
