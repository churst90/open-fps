using System;
using System.IO;
using System.Linq;
using Arch.Core;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Vehicles keep a gap to the one in front (VehicleSystem.Following, the Intelligent Driver Model).
/// Before 2026-09-27 no vehicle knew another was there, and a faster car drove through a slower one.
/// </summary>
public class CarFollowingTests
{
    private readonly ITestOutputHelper _o;
    public CarFollowingTests(ITestOutputHelper o) => _o = o;

    private static (World World, VehicleSystem Vehicles) City(bool streetLife)
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMapData("city", out var data));
        // Nothing staged on top of the driving: no hard stops, no parking, no horns.
        data.StreetLife = streetLife ? new StreetLifeData() : null;
        var vehicles = new VehicleSystem();
        vehicles.Spawn(maps);
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        return (world, vehicles);
    }

    /// <summary>Pairs in one lane whose bodies overlap, counted once a second over the run.</summary>
    private int Overlaps(bool streetLife, double seconds, out int samples)
    {
        var (world, vehicles) = City(streetLife);
        const float dt = 1f / 30f;
        int overlaps = 0;
        samples = 0;
        for (int tick = 0; tick < seconds * 30; tick++)
        {
            vehicles.Update("city", world, dt);
            if (tick < 20 * 30 || tick % 30 != 0) continue;
            samples++;
            foreach (var lane in vehicles.RacersForTest("city").GroupBy(r => (r.Track, MathF.Round(r.Lane * 2f))))
            {
                var cars = lane.OrderBy(r => r.Lap).ToList();
                for (int i = 0; i + 1 < cars.Count; i++)
                {
                    float gap = cars[i + 1].Lap - cars[i].Lap - 0.5f * (cars[i].Length + cars[i + 1].Length);
                    if (gap < 0f) overlaps++;
                }
                if (cars.Count > 1)
                {
                    var (last, first) = (cars[^1], cars[0]);
                    float wrap = first.Lap + last.LapLength - last.Lap - 0.5f * (first.Length + last.Length);
                    if (wrap < 0f) overlaps++;
                }
            }
        }
        return overlaps;
    }

    [Fact]
    public void No_vehicle_drives_through_the_one_in_front()
    {
        int without = Overlaps(streetLife: false, 180, out int n0);
        int with = Overlaps(streetLife: true, 180, out int n1);
        _o.WriteLine($"overlapping pairs over {n0} one-second samples: {without} without following, {with} with it");
        Assert.Equal(0, with);
    }
}
