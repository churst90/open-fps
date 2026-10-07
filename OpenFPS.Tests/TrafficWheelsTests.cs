using System.Diagnostics;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The city's traffic on its wheels: steered along its lanes by a driver, on tyres that decide what
/// it can do (stage 3 of docs/NEXT_BODIES_WHEELS_ROADS.md).
/// </summary>
public class TrafficWheelsTests
{
    private readonly ITestOutputHelper _o;
    public TrafficWheelsTests(ITestOutputHelper o) => _o = o;

    /// <summary>
    /// Three minutes of the city's traffic: every vehicle a driver steers stays within a lane's
    /// half-width of its line, its tyres sing (past the squeal onset) only now and then, and the
    /// wheels cost a few microseconds a vehicle a tick.
    ///
    /// Also measured and printed, not asserted: how often a body corner is off the asphalt, for the
    /// body as it really goes and for a body of the same size held exactly on its line — which says
    /// whether a corner cut over the kerb is the steering's doing or the route's.
    /// </summary>
    [Fact]
    public void Traffic_keeps_to_its_lanes_on_its_tyres()
    {
        var (world, vehicles) = CarFollowingTests.City(streetLife: true);
        var asphalt = EntityDefinitionFactory.StaticDefinitions(world)
            .Where(d => d.Collider.Shape == ColliderShape.Box && d.Material.Material == "Asphalt")
            .ToList();
        const float dt = 1f / 30f;
        var worstOffset = new Dictionary<string, float>();
        int samples = 0, squealing = 0, sliding = 0;
        int cornerSamples = 0, bodyOff = 0, lineOff = 0;
        var offWhere = new List<string>();
        var clock = new Stopwatch();
        long ticks = 0;
        int steered = vehicles.WheelsForTest("city").Count(c => c.Driver != null);
        for (int tick = 0; tick < 180 * 30; tick++)
        {
            clock.Start();
            vehicles.Update("city", world, dt);
            clock.Stop();
            ticks++;
            if (tick < 10 * 30) continue;
            foreach (var c in vehicles.WheelsForTest("city"))
            {
                if (c.Driver == null) continue;
                samples++;
                if (c.TyreDemand >= TyreFriction.SquealOnset) squealing++;
                if (c.TyreDemand >= TyreFriction.SlideOnset) sliding++;
                if (c.KerbShift == 0f)
                    worstOffset[c.Name] = MathF.Max(worstOffset.GetValueOrDefault(c.Name), MathF.Abs(c.Driver.Offset));

                if (tick % 15 != 0 || c.KerbShift != 0f || c.Speed < 0.5f) continue;
                var profile = MachineRegistry.VehicleFor(c.Preset);
                var t = world.Get<Transform>(c.Entity);
                c.Line!.Sample(c.Lap, out var onLine, out float lineHeading, out _);
                foreach (var (sx, sz) in new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) })
                {
                    var local = new Vector3(sx * 0.5f * profile.WidthMetres, 0f, sz * 0.5f * profile.LengthMetres);
                    var body = t.Position + Vector3.Transform(local, t.Rotation);
                    var held = onLine + Vector3.Transform(local, Quaternion.CreateFromYawPitchRoll(lineHeading, 0f, 0f));
                    cornerSamples++;
                    if (!OnAsphalt(asphalt, body))
                    {
                        bodyOff++;
                        if (offWhere.Count < 6) offWhere.Add($"{c.Name} corner at ({body.X:F1}, {body.Z:F1}), {c.Speed:F1} m/s, {c.Driver.Offset:F2} m off its line");
                    }
                    if (!OnAsphalt(asphalt, held)) lineOff++;
                }
            }
        }
        double perVehicle = clock.Elapsed.TotalMilliseconds * 1000.0 / ticks / Math.Max(1, steered);
        foreach (var kv in worstOffset.OrderByDescending(k => k.Value).Take(6)) _o.WriteLine($"furthest off its line: {kv.Key} {kv.Value:F2} m");
        _o.WriteLine($"{steered} vehicles steered; whole update {clock.Elapsed.TotalMilliseconds / ticks * 1000.0:F0} us a tick, {perVehicle:F1} us a steered vehicle");
        _o.WriteLine($"{squealing} of {samples} vehicle-ticks past the squeal onset ({100.0 * squealing / samples:F1} %), {sliding} sliding");
        _o.WriteLine($"body corners off the asphalt: {bodyOff} of {cornerSamples} as driven, {lineOff} of {cornerSamples} held exactly on the line");
        foreach (var w in offWhere) _o.WriteLine("  " + w);

        // Half a lane (a 3.5 m lane): the body keeps to its lane.
        Assert.True(worstOffset.Values.Max() < 1.75f, $"a vehicle ran {worstOffset.Values.Max():F2} m off its line");
        // Tyres singing in under one vehicle-tick in twenty, and sliding hardly ever.
        Assert.True(squealing < samples / 20, $"{squealing} of {samples} vehicle-ticks past the squeal onset");
        Assert.True(sliding < samples / 200, $"{sliding} of {samples} vehicle-ticks sliding");
    }

    private static bool OnAsphalt(List<EntityDefinition> asphalt, Vector3 p)
    {
        var probe = new Vector3(p.X, 0.03f, p.Z);
        return asphalt.Any(a => GeometryUtils.IsPointInOBB(probe, a.Transform.Position, a.Collider.Size, a.Transform.Rotation));
    }
}
