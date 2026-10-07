using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// "Aircraft roll out after landing instead of reversing at the end of the runway." An approach used
/// to brake to a stop in the air over the runway point and spin round where it stood. Now it lands,
/// rolls out on its type's figures, turns round on the runway and takes off back up the line.
/// </summary>
public class AircraftRolloutTests
{
    private readonly ITestOutputHelper _o;
    public AircraftRolloutTests(ITestOutputHelper o) => _o = o;

    [Theory]
    [InlineData("piston_single")]
    [InlineData("turboprop")]
    [InlineData("airliner")]
    public void TheGroundRunRollsOutTurnsRoundAndLiftsOffWhereItTouchedDown(string preset)
    {
        var air = AircraftProfile.ByName(preset);
        var spec = air.Ground!;
        var touchdown = new Vector3(0f, 0.1f, 0f);
        var run = new AircraftGroundRun(spec, touchdown, Vector3.UnitZ, spec.TouchdownSpeedMps, holdSeconds: 5f);
        float dt = 1f / 30f, t = 0f, lastHeading = run.Heading, maxTurnRate = 0f, maxLateral = 0f, farthest = 0f;
        float rolloutEnd = -1f, rolloutDist = 0f, holdAt = -1f;
        var phases = new List<AircraftGroundRun.Phase>();
        while (run.State != AircraftGroundRun.Phase.Done && t < 900f)
        {
            var before = run.State;
            run.Update(dt);
            t += dt;
            if (phases.Count == 0 || phases[^1] != run.State) phases.Add(run.State);
            if (before == AircraftGroundRun.Phase.Rollout && run.State != before) { rolloutEnd = t; rolloutDist = run.Position.Z; }
            if (run.State == AircraftGroundRun.Phase.Hold && holdAt < 0f) holdAt = t;
            float dh = MathF.Abs(MathF.IEEERemainder(run.Heading - lastHeading, 2f * MathF.PI));
            maxTurnRate = MathF.Max(maxTurnRate, dh / dt);
            lastHeading = run.Heading;
            maxLateral = MathF.Max(maxLateral, MathF.Abs(run.Position.X));
            farthest = MathF.Max(farthest, run.Position.Z);
            Assert.Equal(touchdown.Y, run.Position.Y, 3);
        }
        _o.WriteLine($"{preset}: phases {string.Join(" > ", phases)}; roll-out {rolloutDist:F0} m in {rolloutEnd:F1} s, "
                   + $"farthest {farthest:F0} m, widest {maxLateral:F1} m, hold at {holdAt:F1} s, off at {t:F1} s at {run.Speed:F1} m/s, "
                   + $"turn rate up to {maxTurnRate * 180f / MathF.PI:F1} deg/s");
        Assert.Equal(AircraftGroundRun.Phase.Done, run.State);
        // The landing roll is the type's: down to taxi speed in its published distance.
        Assert.InRange(rolloutDist, spec.LandingRollMetres * 0.9f, spec.LandingRollMetres * 1.1f);
        // Turned round within its radius of the centreline, smoothly (no spin on the spot).
        Assert.InRange(maxLateral, spec.TurnRadiusMetres * 0.9f, spec.TurnRadiusMetres * 1.1f);
        // Its turning speed round its radius: v / R, under 30 degrees a second.
        float expected = spec.TurnSpeedMps / spec.TurnRadiusMetres * 180f / MathF.PI;
        Assert.True(maxTurnRate * 180f / MathF.PI < MathF.Max(30f, expected * 1.3f), "it turned faster than a wheel on the ground can");
        // ...and lifted off at rotation speed where it touched down, facing back up the approach.
        Assert.InRange(run.Position.Z, -0.5f, 0.5f);
        Assert.InRange(run.Speed, spec.RotateSpeedMps * 0.97f, spec.RotateSpeedMps * 1.05f);
        Assert.True(MathF.Cos(run.Heading) < -0.99f, "it takes off the way it came");
        Assert.True(holdAt > 0f);
    }

    [Fact]
    public void AnApproachOnTheMapLandsInsteadOfStoppingInTheAir()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out _));
        var vehicles = new VehicleSystem();
        var air = AircraftProfile.ByName("piston_single");
        var from = new Vector3(380f, 90f, -1700f);
        var to = new Vector3(380f, 0.1f, -250f);
        var e = vehicles.SpawnOne(maps, null, "city", new VehicleData
        {
            Name = "Test single", Preset = "piston_single", RoadStart = from, RoadEnd = to,
            SpeedsKmh = new[] { air.ApproachSpeedMps * 3.6f, 160f }, AccelerationMps2 = 1.5f, BrakingMps2 = 2f,
            WaitSeconds = 6f,
        });
        Assert.NotEqual(Entity.Null, e);
        float dt = 1f / 30f, t = 0f;
        float touchdownAt = -1f, slowestAirborne = float.MaxValue, beyond = 0f, liftoffAt = -1f;
        bool onGround = false;
        Vector3 last = world.Get<Transform>(e).Position;
        while (t < 400f && liftoffAt < 0f)
        {
            vehicles.Update("city", world, dt);
            t += dt;
            var p = world.Get<Transform>(e).Position;
            float speed = world.Get<Velocity>(e).Linear.Length();
            bool ground = p.Y < 0.2f;
            if (ground && !onGround && touchdownAt < 0f) touchdownAt = t;
            // The last 400 m of the approach (the shuttle starts its leg from rest, far out).
            if (!ground && touchdownAt < 0f && p.Z > to.Z - 400f) slowestAirborne = MathF.Min(slowestAirborne, speed);
            if (ground) beyond = MathF.Max(beyond, p.Z - to.Z);
            if (onGround && !ground && touchdownAt > 0f) liftoffAt = t;
            onGround = ground;
            last = p;
        }
        _o.WriteLine($"touchdown at {touchdownAt:F1} s, slowest in the air before it {slowestAirborne:F1} m/s, "
                   + $"rolled {beyond:F0} m past the touchdown point, lifted off at {liftoffAt:F1} s at {last}");
        Assert.True(touchdownAt > 0f, "it never reached the runway");
        // It flew the approach: nowhere in the air did it slow below its touchdown speed.
        Assert.True(slowestAirborne >= air.Ground!.TouchdownSpeedMps * 0.95f);
        // It rolled on down the runway after touching down, rather than stopping on the spot.
        Assert.True(beyond > air.Ground.LandingRollMetres * 0.8f);
        Assert.True(liftoffAt > touchdownAt, "it never took off again");
        Assert.InRange(last.Z, to.Z - 30f, to.Z + 5f);
    }
}
