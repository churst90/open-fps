using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The driving cues' planner on the real city: the line ahead through a junction, how hard a driver
/// will have to brake for the turn, the give-way line, a closed level crossing and the end of a road.
/// </summary>
public class DrivingCueTests
{
    private readonly ITestOutputHelper _o;
    public DrivingCueTests(ITestOutputHelper o) => _o = o;

    private static RoadMapData? _city;

    /// <summary>The city's roads as a client is sent them, crossings from its real rail line.</summary>
    internal static RoadMapData City()
    {
        if (_city != null) return _city;
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMapData("city", out var data));
        var rail = new RailSystem();
        rail.Spawn(maps);
        var crossings = new CrossingSystem(rail);
        crossings.Spawn(maps);
        var built = MapRoadsBuilder.Build(data, crossings.Rails("city"));
        // Through the wire's own form, so the JSON is what is tested.
        return _city = RoadMapData.FromJson(built.ToJson())!;
    }

    private static readonly float Dry = 0.95f * WheelDynamics.G;
    private const float HalfCar = 2.1f;

    /// <summary>North up Main Street's kerb lane (x = 4.5), the given distance short of Central Street's line.</summary>
    private static Vector3 MainStreetNorth(float zBeforeJunction) => new(4.5f, 0.05f, -7f - zBeforeJunction);

    [Fact]
    public void TheCityGoesToTheClientWithItsCrossingsOnTheRails()
    {
        var city = City();
        _o.WriteLine($"{city.Roads.Count} roads, {city.Junctions.Count} junctions, {city.Crossings.Count} crossings, {city.Tracks.Count} tracks");
        Assert.True(city.Roads.Count > 10);
        Assert.NotEmpty(city.Crossings);
        foreach (var c in city.Crossings)
        {
            _o.WriteLine($"{c.Name}: centre {c.Centre}, along {c.Along}, half {c.HalfLengthMetres:F1} m");
            // The southern leg of the loop runs east-west.
            Assert.True(MathF.Abs(c.Along.X) > 0.95f, $"{c.Name} rails run {c.Along}");
            Assert.InRange(c.Centre.Z, -181.5f, -178.5f);
        }
        // No train runs on a track a car could drive: the rail loop is not sent as one.
        Assert.DoesNotContain(city.Tracks, t => t.Id == "rail_loop");
    }

    [Fact]
    public void AStraightRoadAtTheLimitAsksForNothing()
    {
        var p = new DrivingCuePlanner(City());
        var plan = p.Update(new Vector3(4.5f, 0.05f, 60f), Vector3.UnitZ, 50f / 3.6f, Dry, HalfCar);
        _o.WriteLine($"{plan.RoadName} limit {plan.SpeedLimitMps * 3.6f:F0}, ratio {plan.BrakeRatio:F2}, hazard {plan.Hazard} at {plan.HazardDistance:F0}, junction {plan.Junction?.Name} at {plan.JunctionDistance:F0}");
        Assert.True(plan.Located);
        Assert.Equal("Main Street", plan.RoadName);
        Assert.Equal(50f, plan.SpeedLimitMps * 3.6f, 1);
        Assert.True(plan.BrakeRatio < DrivingCueBands.Lift);
    }

    [Fact]
    public void TheRightIndicatorPutsTheLineIntoTheTurnAndTheBrakeCueRisesOnTheApproach()
    {
        var p = new DrivingCuePlanner(City()) { Indicator = +1 };
        float v = 50f / 3.6f;
        float last = 0f;
        foreach (float d in new[] { 100f, 80f, 50f, 30f, 15f })
        {
            var plan = p.Update(MainStreetNorth(d), Vector3.UnitZ, v, Dry, HalfCar);
            _o.WriteLine($"{d,4:F0} m: turn {plan.NextTurn} onto {plan.NextRoad}, ratio {plan.BrakeRatio:F2}, need {plan.NeededDecel:F2} m/s2 for {plan.Hazard} at {plan.HazardDistance:F0} m to {plan.HazardSpeed * 3.6f:F0} km/h");
            Assert.Equal(Turn.Right, plan.NextTurn);
            Assert.Equal("Central Street", plan.NextRoad);
            Assert.True(plan.BrakeRatio >= last - 1e-3f, "the cue only rises as the turn comes closer at the same speed");
            last = plan.BrakeRatio;
        }
        // Thirty metres out at fifty, the turn needs firm braking now (0.3 g).
        var close = p.Update(MainStreetNorth(30f), Vector3.UnitZ, v, Dry, HalfCar);
        Assert.Equal(CueHazard.Turn, close.Hazard);
        Assert.True(close.BrakeRatio > DrivingCueBands.Brake);
        // ...and the line goes right: the guide is east of the lane once the turn is inside its reach.
        var inTurn = p.Update(MainStreetNorth(3f), Vector3.UnitZ, 4f, Dry, HalfCar);
        _o.WriteLine($"guide at {inTurn.GuidePoint} from {MainStreetNorth(3f)}");
        Assert.True(inTurn.GuidePoint.X > 5f);
    }

    [Fact]
    public void WithoutAnIndicatorTheLineGoesStraightOnAndThereIsNothingToBrakeFor()
    {
        var p = new DrivingCuePlanner(City());
        var plan = p.Update(MainStreetNorth(30f), Vector3.UnitZ, 50f / 3.6f, Dry, HalfCar);
        _o.WriteLine($"turn {plan.NextTurn}, ratio {plan.BrakeRatio:F2}, gives way {plan.GivesWay}, exits {string.Join(", ", plan.Exits)}");
        Assert.Equal(Turn.Straight, plan.NextTurn);
        // Main Street has priority at Central Street.
        Assert.False(plan.GivesWay);
        Assert.True(plan.BrakeRatio < DrivingCueBands.Lift);
        Assert.Contains(plan.Exits, e => e.Turn == Turn.Left);
        Assert.Contains(plan.Exits, e => e.Turn == Turn.Right);
    }

    [Fact]
    public void ASideRoadGivesWayAndTheCueSaysToSlowForTheLine()
    {
        var p = new DrivingCuePlanner(City());
        // East along Central Street's kerb lane (south side, z = -4.5), toward Main Street at x = 0.
        var at = new Vector3(-7f - 25f, 0.05f, -4.5f);
        var plan = p.Update(at, Vector3.UnitX, 50f / 3.6f, Dry, HalfCar);
        _o.WriteLine($"{plan.RoadName}: gives way {plan.GivesWay} at {plan.JunctionDistance:F0} m, hazard {plan.Hazard}, ratio {plan.BrakeRatio:F2}");
        Assert.Equal("Central Street", plan.RoadName);
        Assert.True(plan.GivesWay);
        Assert.Equal(CueHazard.GiveWay, plan.Hazard);
        Assert.True(plan.BrakeRatio > DrivingCueBands.Brake);
    }

    [Fact]
    public void AWetRoadRaisesTheCue()
    {
        var p = new DrivingCuePlanner(City()) { Indicator = +1 };
        var dry = p.Update(MainStreetNorth(50f), Vector3.UnitZ, 50f / 3.6f, Dry, HalfCar);
        float wetGrip = RoadWaterLaw.GripFactor(RoadSurfaces.IndexOf("Asphalt"), 1.5f, 50f / 3.6f, 220f, 5f);
        var wet = p.Update(MainStreetNorth(50f), Vector3.UnitZ, 50f / 3.6f, Dry * wetGrip, HalfCar);
        _o.WriteLine($"grip factor {wetGrip:F2}: ratio dry {dry.BrakeRatio:F2}, wet {wet.BrakeRatio:F2}");
        Assert.True(wetGrip < 1f);
        Assert.True(wet.BrakeRatio > dry.BrakeRatio);
    }

    [Fact]
    public void AClosedCrossingIsAStopBeforeTheRails()
    {
        var city = City();
        var west = city.Crossings.First(c => c.Name.Contains("west", StringComparison.OrdinalIgnoreCase));
        var p = new DrivingCuePlanner(city);
        // South down Mill Road (x = -160) toward the crossing at z = -180: southbound kerb lane is x = -164.5.
        var at = new Vector3(-164.5f, 0.05f, -140f);
        var open = p.Update(at, -Vector3.UnitZ, 40f / 3.6f, Dry, HalfCar, _ => false);
        var shut = p.Update(at, -Vector3.UnitZ, 40f / 3.6f, Dry, HalfCar, c => c == west || c.Name == west.Name);
        _o.WriteLine($"{open.RoadName}: crossing {open.Crossing?.Name} at {open.CrossingDistance:F1} m; open ratio {open.BrakeRatio:F2}, closed ratio {shut.BrakeRatio:F2} for {shut.Hazard} at {shut.HazardDistance:F1} m");
        Assert.Equal(west.Name, open.Crossing?.Name);
        Assert.InRange(open.CrossingDistance, 35f, 40f);
        Assert.False(open.CrossingClosed);
        Assert.True(shut.CrossingClosed);
        Assert.Equal(CueHazard.Crossing, shut.Hazard);
        Assert.Equal(0f, shut.HazardSpeed);
        Assert.InRange(shut.HazardDistance, open.CrossingDistance - DrivingCuePlanner.StopLineFromRail - HalfCar - 0.6f,
                                            open.CrossingDistance - DrivingCuePlanner.StopLineFromRail - HalfCar + 0.6f);
        Assert.True(shut.BrakeRatio > open.BrakeRatio);
    }

    [Fact]
    public void TheEndOfTheRoadIsAStop()
    {
        var p = new DrivingCuePlanner(City());
        // Main Street runs to z = 420; north of the last junction at z = 260 it ends.
        var plan = p.Update(new Vector3(4.5f, 0.05f, 380f), Vector3.UnitZ, 40f / 3.6f, Dry, HalfCar);
        _o.WriteLine($"hazard {plan.Hazard} at {plan.HazardDistance:F0} m, ratio {plan.BrakeRatio:F2}");
        Assert.Equal(CueHazard.RoadEnd, plan.Hazard);
        Assert.InRange(plan.HazardDistance, 30f, 40f);
    }

    [Fact]
    public void RailCrossingFindsBothRails()
    {
        var c = new CrossingRails { Centre = Vector3.Zero, Along = Vector3.UnitX, HalfLengthMetres = 6f };
        Assert.True(DrivingCuePlanner.RailCrossing(c, new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, 0f), out float t1));
        Assert.Equal(1f - CrossingRails.StandardRailCentres * 0.5f, t1, 2);
        Assert.False(DrivingCuePlanner.RailCrossing(c, new Vector3(0f, 0f, -3f), new Vector3(0f, 0f, -2f), out _));
        // Beyond the planking along the rails: not this crossing.
        Assert.False(DrivingCuePlanner.RailCrossing(c, new Vector3(9f, 0f, -1f), new Vector3(9f, 0f, 1f), out _));
    }
}
