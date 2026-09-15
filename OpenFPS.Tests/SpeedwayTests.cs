using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// Cover for the speedway: the racing line a car is given, and the walls that answer it.
///
/// Both are things that fail QUIETLY. A racing line with one bad node still drives — the car just
/// crawls, and "the cars sound slow" is a long way from "one triple of points in turn three fitted a
/// four-metre radius". A wall segment rotated wrongly still reflects — it just reflects in a
/// direction the wall does not face, and on a curve every segment is rotated differently so there is
/// no single value to eyeball. These assert the two numbers that would otherwise only show up as a
/// sound somebody has to describe.
/// </summary>
public class SpeedwayTests
{
    private const float R = 150f, SX = 164.5f;

    /// <summary>The oval's centreline, drawn the way a map draws one: as a modest number of
    /// waypoints with straight chords between them.</summary>
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
    /// The one that matters. A turn of radius 150 taken at 2.9 lateral g allows about 235 km/h, and
    /// nothing anywhere on the lap may be slower than that — a track made of chords has a corner at
    /// every waypoint join, and measuring the radius across adjacent nodes finds those corners
    /// instead of the turn. When this regressed, the field lapped at 113 km/h.
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

    /// <summary>
    /// The brakes have to be applied BEFORE the corner. Sampling the lap from the end of the
    /// straight back toward the start/finish line must find the limit already coming down.
    /// </summary>
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

    /// <summary>
    /// A wall segment turned to follow a curve must present a face that looks at the track. Taking
    /// the axis-aligned box instead points every face along X or Z, and the reflections then come
    /// back off directions the wall does not face.
    /// </summary>
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

        // The face pointing at the middle of the turn is the one that reflects a car back at the
        // stands. Its normal must point from the wall toward the turn's centre.
        var toCentre = Vector3.Normalize(new Vector3(SX, 1.75f, 0f) - centre);
        float best = -2f;
        for (int i = 0; i < n; i++) best = MathF.Max(best, Vector3.Dot(faces[i].Normal, toCentre));
        Assert.True(best > 0.99f, $"no face looks at the track; best alignment {best:F3}");
    }

    /// <summary>
    /// The map on disk is the thing that ships. Check it parses, claims the login slot, carries no
    /// ambience, and gives every car a track that exists.
    /// </summary>
    [Fact]
    public void TheShippedMapIsCoherent()
    {
        // maps/ is copied next to the test assembly from the real server data (see the csproj), so
        // this reads the file that actually ships rather than a fixture that can drift from it.
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

            // And the line it produces has to be driveable — with the BANKING the track declares,
            // because without it the corner speed is worked out for a flat surface and every car
            // lifts for corners it could take flat.
            var line = new RaceLine(track.Waypoints, v.LaneOffsetMetres, v.TopSpeedKmh / 3.6f,
                                    v.CorneringG, v.BrakingMps2, track.BankingDegrees);
            Assert.True(line.MinSpeed > 15f, $"{v.Name} is limited to {line.MinSpeed * 3.6f:F0} km/h somewhere");
            // A 1.25-mile oval, which is what the generator builds. Generous either side so a
            // dimension can be tuned without the test having to be edited in the same commit.
            Assert.InRange(line.Length, 1900f, 2150f);
        }

        // The listener stands in the INFIELD, which is the only place on a track that is surrounded:
        // cars pass on every side over a lap and the near wall is different in each direction. From
        // the grandstand every car is in front of you and every reflection comes off the one wall
        // behind your head — an easier problem and a much worse demonstration.
        Assert.True(map.SpawnPoint.Position.Y < 3f, "the listener should be down in the infield");
        float fromCentre = new System.Numerics.Vector2(map.SpawnPoint.Position.X, map.SpawnPoint.Position.Z).Length();
        Assert.True(fromCentre < 60f, $"the spawn is {fromCentre:F0} m from the middle of the oval");
    }

}
