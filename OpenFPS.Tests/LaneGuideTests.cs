using System;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The painted lines, as the driver has them. A city avenue is twelve metres of asphalt: two lanes
/// each way, a centre line, and a broken line between each pair.
/// </summary>
public class LaneGuideTests
{
    // Twelve metres across (X), a hundred long (Z): a north-south avenue.
    private static readonly LaneGuide.Road Avenue = new(Vector3.Zero, new Vector3(12f, 0.1f, 100f), Quaternion.Identity);

    [Fact]
    public void AnAvenueHasTwoLanesEachWayAndACentreLine()
    {
        Assert.True(LaneGuide.Locate(Avenue, new Vector3(4.5f, 0f, 0f), Vector3.UnitZ, out var p));
        Assert.Equal(4, p.Lanes);
        Assert.True(p.TwoWay);
        var lines = LaneGuide.Lines(p).OrderBy(l => l.At).ToList();
        Assert.Equal(new[] { -6f, -3f, 0f, 3f, 6f }, lines.Select(l => l.At).ToArray());
        Assert.Equal(LaneGuide.Line.Centre, lines[2].Kind);
        Assert.Equal(LaneGuide.Line.Lane, lines[3].Kind);
    }

    /// <summary>
    /// Northbound in the kerb lane: the kerb is on your right and the broken line on your left.
    /// Turn round in the same place and they swap — the road did not move, you did.
    /// </summary>
    [Fact]
    public void LeftAndRightAreTheDriversNotTheRoads()
    {
        Assert.True(LaneGuide.Locate(Avenue, new Vector3(4.5f, 0f, 0f), Vector3.UnitZ, out var north));
        var (l, r) = LaneGuide.Sides(north, 0.9f);
        Assert.Equal(LaneGuide.Line.Lane, l.Kind);
        Assert.Equal(LaneGuide.Line.Kerb, r.Kind);
        Assert.Equal(0.6f, r.Gap, 3);                   // 1.5 m to the kerb, less half a car

        Assert.True(LaneGuide.Locate(Avenue, new Vector3(4.5f, 0f, 0f), -Vector3.UnitZ, out var south));
        var (l2, r2) = LaneGuide.Sides(south, 0.9f);
        Assert.Equal(LaneGuide.Line.Kerb, l2.Kind);
        Assert.Equal(LaneGuide.Line.Lane, r2.Kind);
    }

    /// <summary>Drift over the centre line and the gap goes negative: you are on it.</summary>
    [Fact]
    public void CrossingTheCentreLineReadsAsOverIt()
    {
        Assert.True(LaneGuide.Locate(Avenue, new Vector3(0.4f, 0f, 0f), Vector3.UnitZ, out var p));
        var (l, _) = LaneGuide.Sides(p, 0.9f);
        Assert.Equal(LaneGuide.Line.Centre, l.Kind);
        Assert.True(l.Gap < 0f);
    }

    /// <summary>A hundred metres is 8.2 dash periods — eight or nine dashes depending on where they
    /// start — whichever way you drive it: the tick rate is speed.</summary>
    [Fact]
    public void DashesPassedCountsTheLinesGoingBy()
    {
        Assert.InRange(LaneGuide.DashesPassed(-50f, 50f), 8, 9);
        Assert.Equal(LaneGuide.DashesPassed(-50f, 50f), LaneGuide.DashesPassed(50f, -50f));
        Assert.Equal(0, LaneGuide.DashesPassed(1f, 2f));
        Assert.Equal(1, LaneGuide.DashesPassed(12f, 12.5f));    // across one dash start
    }

    /// <summary>A road running east-west is the same road turned: its lines run the other way.</summary>
    [Fact]
    public void ATurnedRoadIsTheSameRoad()
    {
        var eastWest = new LaneGuide.Road(Vector3.Zero, new Vector3(100f, 0.1f, 12f), Quaternion.Identity);
        // Eastbound in the kerb lane is on the SOUTH side of the road (driving on the right).
        Assert.True(LaneGuide.Locate(eastWest, new Vector3(0f, 0f, -4.5f), Vector3.UnitX, out var p));
        var (l, r) = LaneGuide.Sides(p, 0.9f);
        Assert.Equal(LaneGuide.Line.Kerb, r.Kind);
        Assert.Equal(LaneGuide.Line.Lane, l.Kind);
        Assert.False(LaneGuide.Locate(eastWest, new Vector3(0f, 0f, 9f), Vector3.UnitX, out _));
    }
}
