using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A driver reads the whole road ahead, not one point on it. Cody, 2026-09-26: the diesel pickup
/// "steps on the gas real hard, lets off, then steps on it real hard again".
/// </summary>
public class RaceLineLookaheadTests
{
    private static RaceLine DowntownPickupLine()
    {
        string dir = AppContext.BaseDirectory;
        var d = new DirectoryInfo(dir);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "OpenFPS.Server", "maps", "city.json"))) d = d.Parent;
        string path = d != null ? Path.Combine(d.FullName, "OpenFPS.Server", "maps", "city.json")
                                : "/home/cody/external-rescue/Github/open-fps/OpenFPS.Server/maps/city.json";
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var track = doc.RootElement.GetProperty("Tracks").EnumerateArray()
                       .First(t => t.GetProperty("Id").GetString() == "downtown_cw");
        var pts = track.GetProperty("Waypoints").EnumerateArray()
                       .Select(p => new Vector3(p.GetProperty("X").GetSingle(), p.GetProperty("Y").GetSingle(), p.GetProperty("Z").GetSingle()))
                       .ToList();
        // Twin-turbo Cummins 6, as the map declares it.
        return new RaceLine(pts, 1.8f, 52f / 3.6f, 0.42f, 2.58f);
    }

    [Fact]
    public void The_slowest_point_between_the_car_and_its_lookahead_is_seen()
    {
        var line = DowntownPickupLine();
        // Mid-corner: the tightest point (about 9.0 m/s at 522 m) lies between the car at 513 m and
        // its lookahead 17.5 m on, where the limit is already rising again.
        line.Sample(513f + 17.5f, out _, out _, out float farEnd);
        float slowest = line.SlowestWithin(513f, 17.5f);
        Assert.True(farEnd > 9.5f, $"far end {farEnd:F2}");
        Assert.True(slowest < 9.05f, $"slowest {slowest:F2}");
    }

    [Fact]
    public void A_pickup_round_the_downtown_loop_never_accelerates_and_brakes_again_inside_a_corner()
    {
        var line = DowntownPickupLine();
        float lap = 0f, speed = 10f, accel = 2.2f, brake = 2.58f, dt = 1f / 30f;
        double lastAccelAt = double.NegativeInfinity, t = 0;
        int surges = 0;
        for (int k = 0; k < 30 * 120; k++, t += dt)
        {
            float want = line.SlowestWithin(lap, MathF.Max(8f, speed * speed / (2f * brake)));
            float was = speed;
            speed = want > speed ? MathF.Min(want, speed + accel * dt) : MathF.Max(want, speed - brake * dt);
            float a = (speed - was) / dt;
            if (a > 1f) lastAccelAt = t;
            // Braking hard less than a second and a half after being on the throttle: a surge.
            if (a < -1f && t - lastAccelAt < 1.5) { surges++; lastAccelAt = double.NegativeInfinity; }
            lap = (lap + speed * dt) % line.Length;
        }
        Assert.Equal(0, surges);
    }
}
