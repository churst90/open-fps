using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Beacons: categories, the map's policy over them, and the player's switches within it — and the
/// ones nobody places, because a door is a door beacon by being a door.
/// </summary>
public class BeaconTests
{
    private readonly ITestOutputHelper _o;
    public BeaconTests(ITestOutputHelper o) => _o = o;

    [Theory]
    [InlineData(Beacons.Policy.DefaultOn, null, true)]
    [InlineData(Beacons.Policy.DefaultOn, false, false)]
    [InlineData(Beacons.Policy.DefaultOff, null, false)]
    [InlineData(Beacons.Policy.DefaultOff, true, true)]
    [InlineData(Beacons.Policy.ForcedOn, false, true)]
    [InlineData(Beacons.Policy.Forbidden, true, false)]
    public void TheMapDecidesAndThePlayerChoosesWithinIt(Beacons.Policy policy, bool? player, bool heard)
        => Assert.Equal(heard, Beacons.IsOn(policy, player));

    [Fact]
    public void AMapPolicyTravelsAndAnythingUnsaidIsOn()
    {
        var p = Beacons.ReadPolicies(new[] { "door=forced_on", "item=forbidden", "nonsense=off", "vehicle=bogus" });
        Assert.Equal(Beacons.Policy.ForcedOn, p[Beacons.Door]);
        Assert.Equal(Beacons.Policy.Forbidden, p[Beacons.Item]);
        Assert.Equal(Beacons.Policy.DefaultOn, p[Beacons.Vehicle]);
        Assert.Equal(Beacons.Policy.DefaultOn, p[Beacons.Exit]);
    }

    [Fact]
    public void ThePlayersSwitchesAreRefusedWhereTheMapHasDecided()
    {
        var aids = new BeaconAids(new AudioEngineFacade(), BeaconPreferences.InMemory());
        aids.SetMapPolicy(new[] { "door=forced_on", "item=forbidden" });
        Assert.Contains("keeps door beacons on", aids.Command(new[] { "doors", "off" }));
        Assert.True(aids.IsOn(Beacons.Door));
        Assert.Contains("does not allow item", aids.Command(new[] { "item", "on" }));
        Assert.False(aids.IsOn(Beacons.Item));
        Assert.Equal("Vehicle beacons off.", aids.Command(new[] { "vehicle" }));
        Assert.False(aids.IsOn(Beacons.Vehicle));
        _o.WriteLine(aids.Command(Array.Empty<string>()));
    }

    /// <summary>
    /// Standing beside a real door on the city, its blip reaches the mixer — through the real facade
    /// and voice manager, which is where the driving cues were silently dropped once.
    /// </summary>
    [Fact]
    public void ADoorNearYouBlips()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var client = new ClientWorldState();
        client.Clear(data.Size, data.MinBound, data.MaxBound);
        EntityDefinition? door = null;
        foreach (var def in EntityDefinitionFactory.StaticDefinitions(world))
        {
            client.RegisterDefinition(def);
            if (door == null && def.Identity.BeaconCategory == Beacons.Door) door = def;
        }
        Assert.NotNull(door);

        var provider = new VoiceLifecycleTests.RecordingProvider();
        var facade = new AudioEngineFacade(provider);
        facade.InitializeForTest();
        var ear = door!.Transform.Position + new Vector3(2f, 0.5f, 0f);
        facade.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
        var aids = new BeaconAids(facade, BeaconPreferences.InMemory());
        for (int i = 0; i < 6; i++)
        {
            aids.Update(client.GetSnapshot(), ear, 10.0 + i);
            for (int k = 0; k < 3; k++) facade.PumpForTest();
        }
        _o.WriteLine("played: " + string.Join(", ", provider.PlayedSounds));
        Assert.Contains(provider.PlayedSounds, s => s.Contains("beacon_door"));

        // ...and switched off, it does not.
        provider.PlayedSounds.Clear();
        aids.Command(new[] { "door", "off" });
        for (int i = 0; i < 6; i++)
        {
            aids.Update(client.GetSnapshot(), ear, 20.0 + i);
            for (int k = 0; k < 3; k++) facade.PumpForTest();
        }
        Assert.DoesNotContain(provider.PlayedSounds, s => s.Contains("beacon_door"));
    }

    /// <summary>
    /// A door on the far side of a wall is somebody else's room: it does not blip through the brick.
    /// Standing against the side of 24 Birch Street, both of its doors are round the corner.
    /// </summary>
    [Fact]
    public void ADoorBehindAWallDoesNotBlip()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var client = new ClientWorldState();
        client.Clear(data.Size, data.MinBound, data.MaxBound);
        foreach (var def in EntityDefinitionFactory.StaticDefinitions(world)) client.RegisterDefinition(def);

        var provider = new VoiceLifecycleTests.RecordingProvider();
        var facade = new AudioEngineFacade(provider);
        facade.InitializeForTest();
        var ear = new Vector3(-340.8f, 1.6f, -13.45f);
        facade.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
        var acoustics = new OpenFPS.Client.AudioEngine.Acoustics.SpatialAcoustics(new OpenFPS.Client.Core.SpatialService());
        var aids = new BeaconAids(facade, BeaconPreferences.InMemory(), acoustics);
        for (int i = 0; i < 6; i++)
        {
            aids.Update(client.GetSnapshot(), ear, 10.0 + i);
            for (int k = 0; k < 3; k++) facade.PumpForTest();
        }
        _o.WriteLine("played: " + string.Join(", ", provider.PlayedSounds));
        Assert.DoesNotContain(provider.PlayedSounds, s => s.Contains("beacon_door"));
    }

    /// <summary>
    /// Nobody placed a beacon on the city, and it is full of them: every door is a door beacon, and
    /// every parked car a vehicle beacon. Checked on the definitions the client is actually sent.
    /// </summary>
    [Fact]
    public void DoorsAndParkedCarsAreBeaconsWithoutBeingPlaced()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        var composites = new CompositeService(maps, prefabs,
            new CompositeRepository(Path.Combine(Path.GetTempPath(), "openfps-no-composites-" + Guid.NewGuid())));
        composites.PlaceRecorded(maps);
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        int doors = 0, cars = 0;
        world.Query(new QueryDescription().WithAll<IdentityComponent>(), (Entity e) =>
        {
            var def = EntityDefinitionFactory.From(world, e);
            if (def.Identity.BeaconCategory == Beacons.Door) doors++;
            if (def.Identity.BeaconCategory == Beacons.Vehicle) cars++;
        });
        _o.WriteLine($"{doors} door beacons, {cars} vehicle beacons");
        Assert.True(doors > 100, $"only {doors} doors carry a door beacon");
        Assert.Equal(4, cars);
    }
}
