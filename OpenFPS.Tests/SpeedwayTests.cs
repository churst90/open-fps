using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// The speedway's racing line and the walls that answer it, both of which fail quietly: a bad node makes
/// a car crawl, and a wrongly turned wall segment reflects where the wall does not face.
/// </summary>
public class SpeedwayTests
{
    private const float R = 150f, SX = 164.5f;

    /// <summary>
    /// A car goes round the lap at the speed it is doing, everywhere, on every lane: equal steps of arc move
    /// it equally far. Reported 2026-09-18 as cars stopping for a second on the front straight: the node
    /// index clamped for the forty metres an outside lane is longer than nominal (docs/COMMON_NOTES.md).
    /// </summary>
    [Theory]
    [InlineData(-8f)]
    [InlineData(-4f)]
    [InlineData(0f)]
    [InlineData(+4f)]
    [InlineData(+8f)]
    public void ACarNeverStopsDeadOnTheLine(float lane)
    {
        var line = new RaceLine(Oval(), lane, 90f, 2.9f, 8f);

        const float step = 1f;
        float shortest = float.MaxValue, longest = 0f;
        line.Sample(0f, out Vector3 prev, out _, out _);
        for (float s = step; s <= line.Length; s += step)
        {
            line.Sample(s, out Vector3 p, out _, out _);
            float moved = Vector3.Distance(prev, p);
            shortest = MathF.Min(shortest, moved);
            longest = MathF.Max(longest, moved);
            prev = p;
        }

        // A metre of arc moves the car a metre; chord against arc on the tightest corner is parts per million.
        Assert.True(shortest > 0.97f * step,
            $"lane {lane:+0.0;-0.0} m: a metre of lap moved the car {shortest:F3} m at its worst — it stalls");
        Assert.True(longest < 1.03f * step,
            $"lane {lane:+0.0;-0.0} m: a metre of lap moved the car {longest:F3} m at its worst — it jumps");
    }

    /// <summary>The lap the line reports is the lap the line is: <see cref="RaceLine.Length"/> matches its
    /// nodes, not the nominal centreline (an outside lane really is longer).</summary>
    [Theory]
    [InlineData(-8f)]
    [InlineData(0f)]
    [InlineData(+8f)]
    public void TheLapLengthIsTheSumOfTheSegments(float lane)
    {
        var line = new RaceLine(Oval(), lane, 90f, 2.9f, 8f);

        // Walked in small steps, one lap of arc length covers the lap itself.
        const float step = 0.25f;
        float walked = 0f;
        line.Sample(0f, out Vector3 prev, out _, out _);
        for (float s = step; s <= line.Length; s += step)
        {
            line.Sample(s, out Vector3 p, out _, out _);
            walked += Vector3.Distance(prev, p);
            prev = p;
        }
        Assert.Equal(line.Length, walked, 0);
    }

    /// <summary>An outside lane is longer than an inside one, and both are driven end to end: the property
    /// that made the fault lane-dependent.</summary>
    [Fact]
    public void AnOutsideLaneIsLongerThanAnInsideOne()
    {
        var inner = new RaceLine(Oval(), -8f, 90f, 2.9f, 8f);
        var outer = new RaceLine(Oval(), +8f, 90f, 2.9f, 8f);
        Assert.True(outer.Length > inner.Length,
            $"outer {outer.Length:F1} m should exceed inner {inner.Length:F1} m");
        Assert.Equal(inner.NodeCount, outer.NodeCount);
    }

    /// <summary>The oval's centreline as a map draws one: a modest number of waypoints joined by chords.</summary>
    private static List<Vector3> Oval(int perTurn = 32, int perStraight = 11)
    {
        var pts = new List<Vector3>();
        for (int i = 0; i < perStraight; i++) pts.Add(new Vector3(SX * i / perStraight, 0, -R));
        for (int i = 0; i < perTurn; i++)
        {
            float a = MathF.PI * (-0.5f + (float)i / perTurn);
            pts.Add(new Vector3(SX + R * MathF.Cos(a), 0, R * MathF.Sin(a)));
        }
        for (int i = 0; i < perStraight * 2; i++) pts.Add(new Vector3(SX - 2 * SX * i / (perStraight * 2f), 0, R));
        for (int i = 0; i < perTurn; i++)
        {
            float a = MathF.PI * (0.5f + (float)i / perTurn);
            pts.Add(new Vector3(-SX + R * MathF.Cos(a), 0, R * MathF.Sin(a)));
        }
        for (int i = 0; i < perStraight; i++) pts.Add(new Vector3(-SX + SX * i / perStraight, 0, -R));
        return pts;
    }

    [Fact]
    public void LapLengthMatchesTheGeometry()
    {
        var line = new RaceLine(Oval(), 0f, 100f, 2.9f, 8f);
        float expected = 4 * SX + 2 * MathF.PI * R;      // two straights and two half-circles
        Assert.InRange(line.Length, expected - 12f, expected + 12f);
    }

    /// <summary>
    /// A turn of radius 150 at 2.9 lateral g allows about 235 km/h, and nothing on the lap is slower: measuring
    /// the radius across adjacent nodes finds the chords' corners instead of the turn. When this regressed
    /// the field lapped at 113 km/h.
    /// </summary>
    [Fact]
    public void CornerSpeedComesFromTheTurnAndNotFromTheWaypointJoins()
    {
        float g = 2.9f;
        float physical = MathF.Sqrt(g * 9.81f * R);       // 65.3 m/s = 235 km/h
        var line = new RaceLine(Oval(), 0f, 300f / 3.6f, g, 8f);

        // Within a few per cent of the real corner speed, and never above it.
        Assert.InRange(line.MinSpeed, physical * 0.94f, physical * 1.06f);
        Assert.True(line.MaxSpeed <= 300f / 3.6f + 0.01f, $"top speed leaked: {line.MaxSpeed}");
    }

    /// <summary>A coarser drawing of the same oval must give the same car the same lap.</summary>
    [Fact]
    public void TheAnswerDoesNotDependOnHowFinelyTheMapDrewTheTrack()
    {
        var coarse = new RaceLine(Oval(perTurn: 20, perStraight: 6), 0f, 90f, 2.9f, 8f);
        var fine = new RaceLine(Oval(perTurn: 90, perStraight: 30), 0f, 90f, 2.9f, 8f);
        Assert.InRange(coarse.MinSpeed, fine.MinSpeed * 0.92f, fine.MinSpeed * 1.08f);
    }

    /// <summary>More grip is more corner speed, and it goes as the square root of it.</summary>
    [Fact]
    public void GripSetsCornerSpeedAsTheSquareRoot()
    {
        var slow = new RaceLine(Oval(), 0f, 120f, 1.1f, 6.5f);
        var fast = new RaceLine(Oval(), 0f, 120f, 4.4f, 14f);
        // Four times the grip is twice the speed.
        Assert.InRange(fast.MinSpeed / slow.MinSpeed, 1.85f, 2.15f);
    }

    /// <summary>The brakes go on before the corner: the limit is already coming down at the end of the
    /// straight.</summary>
    [Fact]
    public void BrakingBeginsBeforeTheCornerRatherThanAtIt()
    {
        var line = new RaceLine(Oval(), 0f, 320f / 3.6f, 2.9f, 8f);
        // The straight runs to about SX metres round; sample back from there.
        line.Sample(SX - 5f, out _, out _, out float atTurnIn);
        line.Sample(SX - 120f, out _, out _, out float earlier);
        Assert.True(atTurnIn < earlier - 2f,
            $"no braking zone: {earlier * 3.6f:F0} km/h 120 m out, {atTurnIn * 3.6f:F0} km/h at turn-in");
    }

    /// <summary>A car on the inside line laps a shorter distance than one on the outside.</summary>
    [Fact]
    public void TheInsideLineIsShorter()
    {
        var inside = new RaceLine(Oval(), -6f, 90f, 2.9f, 8f);
        var outside = new RaceLine(Oval(), +6f, 90f, 2.9f, 8f);
        Assert.True(inside.Length < outside.Length - 20f,
            $"inside {inside.Length:F0} m, outside {outside.Length:F0} m");
    }

    /// <summary>A wall segment turned to follow a curve presents a face that looks at the track; an
    /// axis-aligned box points every face along X or Z.</summary>
    [Fact]
    public void ARotatedWallFacesTheWayItWasTurned()
    {
        // A segment of the turn-one retaining wall: out at 45 degrees round the east arc.
        float a = MathF.PI * 0.25f;
        float rw = R + 11f;
        var centre = new Vector3(SX + rw * MathF.Cos(a), 1.75f, rw * MathF.Sin(a));
        // Its run is the arc's tangent; the prefab's length is along local X, so yaw = atan2(-tz, tx).
        float tx = -MathF.Sin(a), tz = MathF.Cos(a);
        var rot = Quaternion.CreateFromYawPitchRoll(MathF.Atan2(-tz, tx), 0f, 0f);

        Span<ReflectingSurface> faces = stackalloc ReflectingSurface[6];
        int n = ImageSource.FacesOfBox(centre, new Vector3(20f, 3.5f, 0.6f), rot, 0.02f, 1, faces);
        Assert.Equal(6, n);

        // The face toward the turn's centre reflects a car back at the stands; its normal points there.
        var toCentre = Vector3.Normalize(new Vector3(SX, 1.75f, 0f) - centre);
        float best = -2f;
        for (int i = 0; i < n; i++) best = MathF.Max(best, Vector3.Dot(faces[i].Normal, toCentre));
        Assert.True(best > 0.99f, $"no face looks at the track; best alignment {best:F3}");
    }

    /// <summary>The shipped map parses, claims the login slot, carries no ambience, and gives every car a
    /// track that exists.</summary>
    [Fact]
    public void TheShippedMapIsCoherent()
    {
        // maps/ is the real server data copied beside the test assembly (see the csproj), not a fixture.
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "maps", "speedway.json");
        Assert.True(System.IO.File.Exists(path), $"speedway.json was not copied to {path}");
        var map = MapRepository.LoadFromFile(path);
        Assert.NotNull(map);
        Assert.Equal("speedway", map!.Id);
        Assert.True(map.IsDefault, "the speedway should be where a player lands");
        Assert.True(string.IsNullOrEmpty(map.AmbienceId), "the speedway is meant to have no ambience bed");
        Assert.NotNull(map.Tracks);
        Assert.NotNull(map.Vehicles);
        Assert.True(map.Vehicles!.Count >= 6, "a race wants a field");

        foreach (var v in map.Vehicles!)
        {
            Assert.True(VehicleProfile.Presets.ContainsKey(v.Preset), $"unknown preset '{v.Preset}'");
            Assert.False(string.IsNullOrEmpty(v.Track), $"{v.Name} has no track");
            var track = map.Tracks!.Find(t => t.Id == v.Track);
            Assert.True(track != null, $"{v.Name} names track '{v.Track}', which the map does not have");
            Assert.True(track!.Waypoints.Count >= 3);

            // Driveable with the track's banking: without it every car lifts for corners it could take flat.
            var line = new RaceLine(track.Waypoints, v.LaneOffsetMetres, v.TopSpeedKmh / 3.6f,
                                    v.CorneringG, v.BrakingMps2, track.BankingDegrees);
            Assert.True(line.MinSpeed > 15f, $"{v.Name} is limited to {line.MinSpeed * 3.6f:F0} km/h somewhere");
            // A 1.25-mile oval, as the generator builds it, with room to tune a dimension.
            Assert.InRange(line.Length, 1900f, 2150f);
        }

        // The listener stands in the infield, the one place cars pass on every side and the near wall differs
        // in each direction.
        Assert.True(map.SpawnPoint.Position.Y < 3f, "the listener should be down in the infield");
        float fromCentre = new System.Numerics.Vector2(map.SpawnPoint.Position.X, map.SpawnPoint.Position.Z).Length();
        Assert.True(fromCentre < 60f, $"the spawn is {fromCentre:F0} m from the middle of the oval");
    }

}
