using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The people driving the city's traffic: horns now and again from different vehicles, a hard stop
/// that is past what the tyres grip at, and somebody parking and going indoors.
/// </summary>
public class StreetLifeTests
{
    private readonly ITestOutputHelper _o;
    public StreetLifeTests(ITestOutputHelper o) => _o = o;

    private sealed record Heard(int Id, string Label, TransientSound Sound, double At);

    private static (MapManager Maps, World World, VehicleSystem Vehicles) LoadCity(StreetLifeData? life = null)
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        if (life != null)
        {
            Assert.True(maps.TryGetMapData("city", out var data));
            data.StreetLife = life;
        }
        var vehicles = new VehicleSystem();
        vehicles.Spawn(maps);
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        return (maps, world, vehicles);
    }

    private static List<Heard> Run(VehicleSystem vehicles, World world, double seconds, Action<double>? each = null)
    {
        var heard = new List<Heard>();
        double now = 0;
        vehicles.Heard = (map, id, label, sounds) => { foreach (var s in sounds) heard.Add(new Heard(id, label, s, now)); };
        const float dt = 1f / 30f;
        for (int tick = 0; tick < seconds * 30; tick++)
        {
            now = tick * dt;
            vehicles.Update("city", world, dt);
            each?.Invoke(now);
        }
        return heard;
    }

    [Fact]
    public void HornsComeNowAndAgainFromDifferentVehicles()
    {
        var (_, world, vehicles) = LoadCity(new StreetLifeData { HornEverySeconds = 45 });
        var horns = Run(vehicles, world, 20 * 60).Where(h => h.Label == "horn").ToList();

        foreach (var h in horns)
            Assert.True(Honk.TryParse(h.Sound.SynthKey, out _, out _), h.Sound.SynthKey);
        int cars = horns.Select(h => h.Id).Distinct().Count();
        var kinds = horns.Select(h => { Honk.TryParse(h.Sound.SynthKey, out var horn, out _); return horn; }).Distinct().ToList();
        _o.WriteLine($"{horns.Count} honks in 20 minutes from {cars} vehicles; horns: {string.Join(", ", kinds)}");
        var gaps = horns.Zip(horns.Skip(1), (a, b) => b.At - a.At).ToList();
        if (gaps.Count > 0) _o.WriteLine($"gaps: shortest {gaps.Min():F0} s, longest {gaps.Max():F0} s, mean {gaps.Average():F0} s");

        // About one every 45 s: 27 expected in twenty minutes. Random, so a wide band.
        Assert.InRange(horns.Count, 12, 50);
        Assert.True(cars >= horns.Count / 2, "the honks are coming from a few vehicles, not from the traffic");
        Assert.True(kinds.Count >= 2, "every vehicle has the same horn");
    }

    [Fact]
    public void AHardStopIsPastWhatTheTyresGripAtAndIsOftenAnsweredWithTheHorn()
    {
        var (_, world, vehicles) = LoadCity(new StreetLifeData { HardBrakeEverySeconds = 20 });
        var ids = new List<int>();
        world.Query(new QueryDescription().WithAll<VehicleComponent>(), (Entity e) => ids.Add(e.Id));
        float worst = 0f;
        int squealing = 0;
        var heard = Run(vehicles, world, 10 * 60, _ =>
        {
            foreach (int id in ids)
                if (vehicles.TryGetTyreDemand(id, out float d))
                {
                    worst = MathF.Max(worst, d);
                    if (d >= TyreFriction.SquealOnset) squealing++;
                }
        });
        int horns = heard.Count(h => h.Label == "horn");
        _o.WriteLine($"worst tyre demand {worst:F2}; {squealing} vehicle-ticks past the squeal onset; {horns} honks");
        Assert.True(worst >= TyreFriction.SquealOnset, $"nobody braked hard enough to squeal (worst {worst:F2})");
        Assert.True(worst < TyreFriction.FullSlide, "a hard stop locked the wheels");
        Assert.True(horns > 0, "nobody honked at whoever made them stop");
    }

    [Fact]
    public void ACarParksTheDriverGoesInsideAndComesBackAndItDrivesOff()
    {
        var (maps, world, vehicles) = LoadCity(new StreetLifeData { ParkEverySeconds = 20 });
        var removed = new List<int>();
        var engine = new List<(int Id, bool Running)>();
        vehicles.Removed = (map, id) => removed.Add(id);
        vehicles.AudioChanged = id =>
        {
            foreach (var kv in maps.GetAllMaps())
                if (kv.Key == "city" && kv.Value.lookup.TryGetValue(id, out var e) && world.Has<SoundEmitterComponent>(e))
                    engine.Add((id, world.Get<SoundEmitterComponent>(e).SynthRunning));
        };
        var doorsOpened = new HashSet<int>();
        var heard = Run(vehicles, world, 12 * 60, _ =>
            world.Query(new QueryDescription().WithAll<DoorComponent>(), (Entity e, ref DoorComponent d) =>
            {
                if (d.Target > 0.5f) doorsOpened.Add(e.Id);
            }));

        int carDoors = heard.Count(h => h.Label == "car door");
        var off = engine.Where(x => !x.Running).Select(x => x.Id).ToHashSet();
        var backOn = engine.Where(x => x.Running && off.Contains(x.Id)).Select(x => x.Id).ToHashSet();
        _o.WriteLine($"engines off {off.Count}, back on {backOn.Count}; car door sounds {carDoors}; "
                   + $"building doors opened {doorsOpened.Count}; people gone indoors or back in the car {removed.Count}");
        Assert.NotEmpty(off);
        Assert.NotEmpty(backOn);
        Assert.NotEmpty(doorsOpened);
        Assert.True(removed.Count >= 2, "the driver neither went indoors nor got back in");
        Assert.True(carDoors >= 2);
    }

    [Fact]
    public void ABusBetweenYouAndACarTakesTheTopOffIt()
    {
        // A 12 m bus, broadside, halfway between a listener and a car 20 m away.
        var bus = new Vector3(2.55f, 3.2f, 12f);
        var rot = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f);
        float detour = VehicleShadow.Detour(new Vector3(0, 0, 10), rot, bus, new Vector3(0, 1.6f, 0), new Vector3(0, 0.4f, 20));
        float low = VehicleShadow.Loss(detour, 150f), mid = VehicleShadow.Loss(detour, 1000f), high = VehicleShadow.Loss(detour, 4000f);
        _o.WriteLine($"bus: detour {detour:F2} m; loss {low:F1} / {mid:F1} / {high:F1} dB");
        Assert.True(high > mid && mid > low);
        Assert.InRange(high, 10f, 15f);
        Assert.True(low < 8f, "a bus is not a wall to a rumble");

        // A hatchback in the same place: the line from an ear at 1.6 m clears most of its roof.
        float hatch = VehicleShadow.Detour(new Vector3(0, 0, 10), rot, new Vector3(1.7f, 1.45f, 4f),
                                           new Vector3(0, 1.6f, 0), new Vector3(0, 0.4f, 20));
        _o.WriteLine($"hatchback: detour {hatch:F3} m, high loss {VehicleShadow.Loss(hatch, 4000f):F1} dB");
        Assert.True(hatch < detour);

        // Off to one side it is not in the way at all.
        Assert.Equal(0f, VehicleShadow.Detour(new Vector3(20, 0, 10), rot, bus, new Vector3(0, 1.6f, 0), new Vector3(0, 0.4f, 20)));
    }
}
