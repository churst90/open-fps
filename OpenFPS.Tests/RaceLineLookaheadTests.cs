using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// A driver reads the whole road ahead, not one point on it. Cody, 2026-09-26: the diesel pickup
/// "steps on the gas real hard, lets off, then steps on it real hard again".
/// </summary>
public class RaceLineLookaheadTests
{
    /// <summary>Twin-turbo Cummins 6, as the map declares it, round the downtown square as the roads
    /// lay it out (RouteTrafficTests.DowntownTour).</summary>
    private static RaceLine DowntownPickupLine()
        => new RaceLine(RouteTrafficTests.DowntownTour(), 0f, 52f / 3.6f, 0.42f, 2.58f);

    [Fact]
    public void The_slowest_point_between_the_car_and_its_lookahead_is_seen()
    {
        var line = DowntownPickupLine();
        // The tightest point on the loop, found rather than remembered, and a car nine metres short of
        // it looking 17.5 m on: the far end of its look is past the corner, where the limit is rising.
        float tightest = 0f, lowest = float.MaxValue;
        for (float d = 0f; d < line.Length; d += 0.5f)
        {
            line.Sample(d, out _, out _, out float lim);
            if (lim < lowest) { lowest = lim; tightest = d; }
        }
        float car = tightest - 9f;
        line.Sample(car + 17.5f, out _, out _, out float farEnd);
        float slowest = line.SlowestWithin(car, 17.5f);
        Assert.True(farEnd > lowest + 0.3f, $"far end {farEnd:F2} against {lowest:F2}");
        Assert.True(slowest < lowest + 0.05f, $"slowest {slowest:F2} against {lowest:F2}");
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
