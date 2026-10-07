using Arch.Core;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
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

    /// <summary>Where the city's junctions are, and how far each reaches.</summary>
    internal static List<(System.Numerics.Vector3 At, float Radius)> Junctions()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetRoads("city", out var net));
        return net.Junctions.Select(j => (j.Position, j.RadiusMetres)).ToList();
    }

    internal static (World World, VehicleSystem Vehicles) City(bool streetLife)
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

    /// <summary>Pairs whose bodies overlap in the same lane, counted once a second over the run: nose
    /// to tail closer than their half-lengths, less than a lane apart sideways, pointing the same way.</summary>
    internal static int Overlaps(World world, VehicleSystem vehicles, double seconds, out int samples, List<string>? where = null,
                                 List<(System.Numerics.Vector3 At, float Radius)>? outside = null)
    {
        const float dt = 1f / 30f;
        int overlaps = 0;
        samples = 0;
        for (int tick = 0; tick < seconds * 30; tick++)
        {
            vehicles.Update("city", world, dt);
            if (tick < 20 * 30 || tick % 30 != 0) continue;
            samples++;
            var cars = vehicles.DriversForTest("city", world).ToList();
            for (int a = 0; a < cars.Count; a++)
                for (int b = a + 1; b < cars.Count; b++)
                {
                    var (p, q) = (cars[a], cars[b]);
                    float turn = MathF.Abs(MathF.IEEERemainder(p.Heading - q.Heading, 2 * MathF.PI));
                    if (turn > 0.5f) continue;
                    var fwd = new System.Numerics.Vector3(MathF.Sin(p.Heading), 0f, MathF.Cos(p.Heading));
                    var d = q.Position - p.Position;
                    float along = MathF.Abs(System.Numerics.Vector3.Dot(d, fwd));
                    float side = MathF.Abs(d.X * fwd.Z - d.Z * fwd.X);
                    // Inside a junction, two cars turning into the same lane is a question of who gives
                    // way (gap acceptance), not of following: counted by its own test, not this one.
                    if (outside != null && outside.Any(j => InJunction(p.Position, j) || InJunction(q.Position, j))) continue;
                    if (side < 1.5f && along < 0.5f * (p.Length + q.Length))
                    {
                        overlaps++;
                        where?.Add($"{p.Name} and {q.Name} at ({p.Position.X:F0}, {p.Position.Z:F0}), {p.Speed:F1} and {q.Speed:F1} m/s");
                    }
                }
        }
        return overlaps;
    }

    /// <summary>Along the lanes, no two vehicles overlap. Junctions are left to gap acceptance.</summary>
    [Fact]
    public void No_vehicle_drives_through_the_one_in_front()
    {
        var (w0, v0) = City(streetLife: false);
        int without = Overlaps(w0, v0, 180, out int n0);
        var (w1, v1) = City(streetLife: true);
        var where = new List<string>();
        int with = Overlaps(w1, v1, 180, out _, where, Junctions());
        foreach (var w in where.Take(12)) _o.WriteLine(w);
        _o.WriteLine($"overlapping pairs over {n0} one-second samples: {without} without following, {with} with it");
        Assert.Equal(0, with);
    }

    /// <summary>
    /// Inside a junction nobody meets anybody: two cars turning into one lane, or crossing each other's
    /// path. Centres within 2.5 m whichever way they point, counted once a second over three minutes.
    /// </summary>
    [Fact]
    public void No_two_vehicles_meet_inside_a_junction()
    {
        var (world, vehicles) = City(streetLife: true);
        var junctions = Junctions();
        const float dt = 1f / 30f;
        var met = new List<string>();
        for (int tick = 0; tick < 180 * 30; tick++)
        {
            vehicles.Update("city", world, dt);
            if (tick < 20 * 30 || tick % 30 != 0) continue;
            var cars = vehicles.DriversForTest("city", world).ToList();
            for (int a = 0; a < cars.Count; a++)
                for (int b = a + 1; b < cars.Count; b++)
                {
                    var (p, q) = (cars[a], cars[b]);
                    if (!junctions.Any(j => InJunction(p.Position, j) && InJunction(q.Position, j))) continue;
                    // Side by side in neighbouring lanes, pointing the same way and a lane apart, is two
                    // cars turning into two lanes, not a meeting (2026-09-28: 2.4 m apart mid-turn).
                    float turnApart = MathF.Abs(MathF.IEEERemainder(p.Heading - q.Heading, 2 * MathF.PI));
                    var fwd = new System.Numerics.Vector3(MathF.Sin(p.Heading), 0f, MathF.Cos(p.Heading));
                    var dd = q.Position - p.Position;
                    if (turnApart < 0.35f && MathF.Abs(dd.X * fwd.Z - dd.Z * fwd.X) >= 2.0f) continue;
                    if (System.Numerics.Vector3.Distance(p.Position, q.Position) < 2.5f)
                        met.Add($"{p.Name} ({p.Position.X:F1},{p.Position.Z:F1}) {p.Speed:F1} m/s hdg {p.Heading * 57.3f:F0} and "
                              + $"{q.Name} ({q.Position.X:F1},{q.Position.Z:F1}) {q.Speed:F1} m/s hdg {q.Heading * 57.3f:F0}");
                }
        }
        foreach (var m in met.Take(10)) _o.WriteLine(m);
        Assert.True(met.Count == 0, $"{met.Count} meetings inside junctions");
    }

    private static bool InJunction(System.Numerics.Vector3 p, (System.Numerics.Vector3 At, float Radius) j)
        => MathF.Abs(p.X - j.At.X) <= j.Radius + 2f && MathF.Abs(p.Z - j.At.Z) <= j.Radius + 2f;
}
