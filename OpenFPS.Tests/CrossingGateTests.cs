using OpenFPS.Common;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Level crossing gates: one on each road approach, where the rules put it, and an arm that keeps the
/// rules' timing from the crossing's one signal.
/// </summary>
public class CrossingGateTests
{
    private readonly ITestOutputHelper _o;
    public CrossingGateTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void EveryRoadOverTheCitysRailsHasAGateOnEachApproachOffTheCarriageway()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out _));
        var rail = new RailSystem();
        rail.Spawn(maps);
        var crossings = new CrossingSystem(rail);
        crossings.Spawn(maps);
        var posts = crossings.GatePosts("city", world).ToList();
        foreach (var (name, at) in posts) _o.WriteLine($"{name}: gate at {at}");
        // Mill Road, Tanner Road and Main Street each cross the south leg once.
        Assert.Equal(3, posts.Select(p => p.Crossing).Distinct().Count());
        Assert.Equal(6, posts.Count);
        foreach (var (_, at) in posts)
        {
            // The rails run east-west at z = -180: a gate stands its clearance and half the rails short of them.
            Assert.InRange(MathF.Abs(at.Z + 180f), 4.2f, 4.7f);
        }
    }

    [Fact]
    public void TheArmWaitsComesDownInItsTimeAndGoesBackUpFaster()
    {
        var arm = new GateArm(CrossingGateSpec.Standard);
        float t = 0f, dt = 0.01f, startedDown = -1f, down = -1f;
        while (t < 30f && down < 0f)
        {
            arm.Update(closed: true, dt);
            t += dt;
            if (startedDown < 0f && arm.Raised < 1f) startedDown = t;
            if (arm.Arrived == true) down = t;
        }
        _o.WriteLine($"started down at {startedDown:F2} s, down at {down:F2} s");
        // No sooner than 3 s after the bells (49 CFR 234.223), and down in 10-15 s.
        Assert.InRange(startedDown, 3f, 5f);
        Assert.InRange(down - startedDown, 10f, 15f);
        Assert.Equal(0f, arm.Raised);
        float up = -1f;
        t = 0f;
        while (t < 30f && up < 0f)
        {
            arm.Update(closed: false, dt);
            t += dt;
            if (arm.Arrived == false) up = t;
            if (arm.State == GateArm.Phase.Raising) Assert.True(arm.Driving);
        }
        _o.WriteLine($"up in {up:F2} s");
        Assert.InRange(up, 5f, down - startedDown);
        Assert.Equal(1f, arm.Raised);
        // Rising, a train coming sends it straight back down from where it is.
        for (int i = 0; i < 300; i++) arm.Update(false, dt);
        for (int i = 0; i < 1000; i++) arm.Update(true, dt);
        Assert.True(arm.State is GateArm.Phase.Lowering or GateArm.Phase.Down);
    }

    [Fact]
    public void AGateMadeWhileTheCrossingIsClosedIsAlreadyDown()
    {
        var arm = new GateArm(CrossingGateSpec.Standard, closed: true);
        arm.Update(true, 0.1f);
        Assert.Equal(GateArm.Phase.Down, arm.State);
        Assert.Equal(0f, arm.MotorSpeed);
    }
}
