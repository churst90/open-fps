using System.Numerics;
using Arch.Core;
using OpenFPS.Audio.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
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

    /// <summary>Beside a real city door its blip reaches the mixer through the real facade and voice
    /// manager, where the driving cues were once silently dropped.</summary>
    [Fact]
    public void ADoorNearYouBlips()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var client = new ClientWorldState();
        client.Clear(data.Size);
        EntityDefinition? door = null;
        foreach (var def in EntityDefinitionFactory.StaticDefinitions(world))
        {
            client.RegisterDefinition(def);
            if (door == null && def.Identity.BeaconCategory == Beacons.Door) door = def;
        }
        Assert.NotNull(door);

        var provider = new VoiceLifecycleTests.RecordingProvider();
        var facade = new AudioEngineFacade(provider);
        facade.InitializeSteppedByHand();
        var ear = door!.Transform.Position + new Vector3(2f, 0.5f, 0f);
        facade.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
        var aids = new BeaconAids(facade, BeaconPreferences.InMemory());
        for (int i = 0; i < 6; i++)
        {
            aids.Update(client.GetSnapshot(), ear, 10.0 + i);
            for (int k = 0; k < 3; k++) facade.StepByHand();
        }
        _o.WriteLine("played: " + string.Join(", ", provider.PlayedSounds));
        Assert.Contains(provider.PlayedSounds, s => s.Contains("beacon_door"));

        // ...and switched off, it does not.
        provider.PlayedSounds.Clear();
        aids.Command(new[] { "door", "off" });
        for (int i = 0; i < 6; i++)
        {
            aids.Update(client.GetSnapshot(), ear, 20.0 + i);
            for (int k = 0; k < 3; k++) facade.StepByHand();
        }
        Assert.DoesNotContain(provider.PlayedSounds, s => s.Contains("beacon_door"));
    }

    /// <summary>A door whose face is in plain view blips, though the path model calls many half blocked
    /// by their own jamb at an angle ("I don't hear the beacons for doors now where I heard them before").</summary>
    [Fact]
    public void ADoorInViewBlipsFromAnAngle()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var client = new ClientWorldState();
        client.Clear(data.Size);
        var doors = new System.Collections.Generic.List<EntityDefinition>();
        foreach (var def in EntityDefinitionFactory.StaticDefinitions(world))
        {
            client.RegisterDefinition(def);
            if (def.Identity.BeaconCategory == Beacons.Door) doors.Add(def);
        }
        var snap = client.GetSnapshot();
        var acoustics = new OpenFPS.Audio.Acoustics.SpatialAcoustics();
        var aids = new BeaconAids(new AudioEngineFacade(new VoiceLifecycleTests.RecordingProvider()),
                                  BeaconPreferences.InMemory(), acoustics);
        int tried = 0, halfBlocked = 0, silent = 0;
        foreach (var d in doors)
        {
            var p = d.Transform.Position;
            var n = Vector3.Transform(Vector3.UnitZ, d.Transform.Rotation);
            foreach (var normal in new[] { n, -n })
            {
                var face = p + normal * (0.5f * d.Collider.Size.Z + 0.35f);
                var side = new Vector3(normal.Z, 0, -normal.X);
                foreach (var ear in new[] { face + normal * 3f + side * 1.5f + Vector3.UnitY * 0.5f,
                                            face + normal * 1.5f + side * 1.5f + Vector3.UnitY * 0.5f })
                {
                    // Really in view: clear from the face to the ear, and from the ear to a point straight out.
                    var dir = ear - face; float len = dir.Length();
                    if (new SpatialService().RaycastSingle(snap, face, dir / len, len, out _, out _)) continue;
                    tried++;
                    if (acoustics.CalculateAcousticPath(snap, d.EntityId, ear, p).Occlusion > 0.5f) halfBlocked++;
                    if (!aids.Reaches(snap, d.EntityId, ear, p, out _)) silent++;
                    break;
                }
            }
        }
        _o.WriteLine($"{tried} places a door is in view; {halfBlocked} read over half blocked; {silent} silent");
        Assert.True(tried > doors.Count / 2);
        Assert.Equal(0, silent);
    }

    /// <summary>A door on the far side of a wall does not blip through the brick: against the side of
    /// 24 Birch Street, both its doors are round the corner.</summary>
    [Fact]
    public void ADoorBehindAWallDoesNotBlip()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var client = new ClientWorldState();
        client.Clear(data.Size);
        foreach (var def in EntityDefinitionFactory.StaticDefinitions(world)) client.RegisterDefinition(def);

        var provider = new VoiceLifecycleTests.RecordingProvider();
        var facade = new AudioEngineFacade(provider);
        facade.InitializeSteppedByHand();
        var ear = new Vector3(-340.8f, 1.6f, -13.45f);
        facade.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
        var acoustics = new OpenFPS.Audio.Acoustics.SpatialAcoustics();
        var aids = new BeaconAids(facade, BeaconPreferences.InMemory(), acoustics);
        for (int i = 0; i < 6; i++)
        {
            aids.Update(client.GetSnapshot(), ear, 10.0 + i);
            for (int k = 0; k < 3; k++) facade.StepByHand();
        }
        _o.WriteLine("played: " + string.Join(", ", provider.PlayedSounds));
        Assert.DoesNotContain(provider.PlayedSounds, s => s.Contains("beacon_door"));
    }

    /// <summary>
    /// A blip is heard by the acoustic worker's answer for it, as every sustained voice is (P6, Cody 2026-10-11):
    /// each near beacon is asked under a probe of its own every frame, so when it blips the answer is in and
    /// decides whether it sounds and from where. Behind the wall at 24 Birch Street the worker's answer keeps the
    /// doors silent too; beside a door it blips on it.
    /// </summary>
    [Fact]
    public void A_blip_is_heard_by_the_workers_answer()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var client = new ClientWorldState();
        client.Clear(data.Size);
        var doors = new System.Collections.Generic.List<EntityDefinition>();
        foreach (var def in EntityDefinitionFactory.StaticDefinitions(world))
        {
            client.RegisterDefinition(def);
            if (def.Identity.BeaconCategory == Beacons.Door) doors.Add(def);
        }
        // A metre and a half out from a door's face, in plain view of it.
        var spatial = new OpenFPS.Client.Core.SpatialService();
        Vector3? before = null;
        foreach (var d in doors)
        {
            var n = Vector3.Transform(Vector3.UnitZ, d.Transform.Rotation);
            var face = d.Transform.Position + n * (0.5f * d.Collider.Size.Z + 0.35f);
            var at = face + n * 1.5f + Vector3.UnitY * 0.5f;
            var dir = at - face;
            if (spatial.RaycastSingle(client.GetSnapshot(), face, Vector3.Normalize(dir), dir.Length(), out _, out _)) continue;
            before = at;
            break;
        }
        Assert.NotNull(before);

        foreach (var (ear, heard) in new[] { (before!.Value, true), (new Vector3(-340.8f, 1.6f, -13.45f), false) })
        {
            var provider = new VoiceLifecycleTests.RecordingProvider();
            var facade = new AudioEngineFacade(provider);
            facade.InitializeSteppedByHand();
            facade.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
            var acoustics = new OpenFPS.Audio.Acoustics.SpatialAcoustics();
            using var worker = new OpenFPS.Audio.Acoustics.AsyncAcousticWorker(acoustics) { Manual = true };
            var aids = new BeaconAids(facade, BeaconPreferences.InMemory(), acoustics) { Worker = worker };
            var snap = client.GetSnapshot();
            worker.UpdateWorld(snap);
            for (int i = 0; i < 120; i++)
            {
                aids.Update(snap, ear, 10.0 + i / 60.0);
                worker.StepForTest();
                for (int k = 0; k < 3; k++) facade.StepByHand();
            }
            _o.WriteLine($"ear {ear}: played {string.Join(", ", provider.PlayedSounds)}; {aids.AnsweredByWorker} decided on the worker's answer");
            if (heard)
            {
                Assert.Contains(provider.PlayedSounds, s => s.Contains("beacon_door"));
                Assert.True(aids.AnsweredByWorker > 0, "no blip was decided on the worker's answer");
            }
            else Assert.DoesNotContain(provider.PlayedSounds, s => s.Contains("beacon_door"));
        }
    }

    /// <summary>Every city door is a door beacon and every parked car a vehicle beacon, though nobody placed
    /// one; checked on the definitions the client is sent.</summary>
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

    /// <summary>The city with its parked cars placed, as a client is sent it, and the cars.</summary>
    private static (WorldSnapshot Snap, System.Collections.Generic.List<EntityDefinition> Cars) CityWithParkedCars()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        var composites = new CompositeService(maps, prefabs,
            new CompositeRepository(Path.Combine(Path.GetTempPath(), "openfps-no-composites-" + Guid.NewGuid())));
        composites.PlaceRecorded(maps);
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var client = new ClientWorldState();
        client.Clear(data.Size);
        var cars = new System.Collections.Generic.List<EntityDefinition>();
        // Every entity, the moving ones too: a drivable car and its panels carry a Velocity, so they
        // reach a client by the broadcast rather than the map stream.
        world.Query(new QueryDescription().WithAll<Transform>(), (Entity e) =>
        {
            var def = EntityDefinitionFactory.From(world, e);
            client.RegisterDefinition(def);
            if (def.Identity.BeaconCategory == Beacons.Vehicle) cars.Add(def);
        });
        Assert.Equal(4, cars.Count);
        return (client.GetSnapshot(), cars);
    }

    /// <summary>A parked car's beacon hangs just off the body on your side, heard from all round; from the
    /// footprint's middle every path read 0.95 blocked (Cody, 2026-10-05: "I'm not sure I hear vehicle
    /// beacons").</summary>
    [Fact]
    public void AParkedCarBlipsFromWhereverYouStand()
    {
        var (snap, cars) = CityWithParkedCars();
        var acoustics = new OpenFPS.Audio.Acoustics.SpatialAcoustics();
        int tried = 0, silent = 0;
        foreach (var c in cars)
        {
            var p = c.Transform.Position;
            var fwd = Vector3.Transform(Vector3.UnitZ, c.Transform.Rotation);
            var right = Vector3.Transform(Vector3.UnitX, c.Transform.Rotation);
            foreach (var (name, off) in new[] { ("beside, 2 m", right * 2f), ("beside, 4 m", right * 4f), ("other side, 3 m", -right * 3f),
                                                ("in front, 4 m", fwd * 4f), ("behind, 5 m", -fwd * 5f), ("off a corner", (fwd + right) * 4.2f) })
            {
                var ear = p + off + new Vector3(0f, 1.6f, 0f);
                var mixer = new EmitterRecordingProvider();
                var audio = new AudioEngineFacade(mixer);
                audio.InitializeSteppedByHand();
                audio.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
                var aids = new BeaconAids(audio, BeaconPreferences.InMemory(), acoustics);
                aids.Ping(snap, c.EntityId, TrackCategory.Vehicles, ear);
                for (int k = 0; k < 3; k++) audio.StepByHand();
                int at = mixer.Played.FindIndex(e => e.SoundId == "SYNTH/beacon_vehicle_hum");
                tried++;
                if (at < 0) { silent++; _o.WriteLine($"{c.Identity.Name}, {name}: silent"); continue; }
                var blip = mixer.Played[at];
                // From the car: within its own box and a little, in plan, and between its sills and roof.
                var local = Vector3.Transform(blip.Position - p, Quaternion.Inverse(c.Transform.Rotation));
                _o.WriteLine($"{c.Identity.Name}, {name}: from {local} in the car's frame, occlusion {blip.Occlusion:F2}");
                Assert.InRange(MathF.Abs(local.X), 0f, c.Collider.Size.X * 0.5f + 0.31f);
                Assert.InRange(MathF.Abs(local.Z), 0f, c.Collider.Size.Z * 0.5f + 0.31f);
                Assert.InRange(local.Y, 0.4f, c.Collider.Size.Y);
            }
        }
        Assert.Equal(24, tried);
        Assert.Equal(0, silent);
    }

    /// <summary>Among parked cars the nearest blip on their own beats, and the car you sit in is not one of
    /// them: the next one along takes its place.</summary>
    [Fact]
    public void TheNearestParkedCarsBlipButNotTheOneYouAreIn()
    {
        var (snap, cars) = CityWithParkedCars();
        var acoustics = new OpenFPS.Audio.Acoustics.SpatialAcoustics();
        var first = cars.OrderBy(c => c.Transform.Position.Z).First();
        var right = Vector3.Transform(Vector3.UnitX, first.Transform.Rotation);

        System.Collections.Generic.List<OpenFPS.Audio.Data.SpatialEmitter> Listen(Vector3 ear, bool paths = true)
        {
            var mixer = new EmitterRecordingProvider();
            var audio = new AudioEngineFacade(mixer);
            audio.InitializeSteppedByHand();
            audio.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
            var aids = new BeaconAids(audio, BeaconPreferences.InMemory(), paths ? acoustics : null);
            for (int i = 0; i < 40; i++)
            {
                aids.Update(snap, ear, 10.0 + i * 0.1);
                for (int k = 0; k < 3; k++) audio.StepByHand();
            }
            return mixer.Played.Where(e => e.SoundId == "SYNTH/beacon_vehicle_hum").ToList();
        }
        bool From(OpenFPS.Audio.Data.SpatialEmitter e, EntityDefinition car)
            => Vector2.Distance(new Vector2(e.Position.X, e.Position.Z), new Vector2(car.Transform.Position.X, car.Transform.Position.Z))
               < car.Collider.Size.Z * 0.5f + 0.5f;

        var beside = Listen(first.Transform.Position + right * 2.5f + new Vector3(0f, 1.6f, 0f));
        _o.WriteLine($"beside: {beside.Count} blips");
        Assert.Contains(beside, e => From(e, first));

        // No paths: the car's own panels would shut out the others, which is not under test here.
        var inside = Listen(first.Transform.Position + new Vector3(0f, 1.1f, 0f), paths: false);
        _o.WriteLine($"inside: {inside.Count} blips, from {string.Join(", ", inside.Select(e => e.Position))}");
        Assert.DoesNotContain(inside, e => From(e, first));
        Assert.NotEmpty(inside);
    }
}
