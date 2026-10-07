using System.Diagnostics;
using System.Numerics;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The per-wheel model (WheelDynamics), the driver who steers it along a line (LineFollower), and
/// the chassis data every preset declares (stage 3 of docs/NEXT_BODIES_WHEELS_ROADS.md).
/// </summary>
public class WheelDynamicsTests
{
    private readonly ITestOutputHelper _o;
    public WheelDynamicsTests(ITestOutputHelper o) => _o = o;

    private const float G = WheelDynamics.G;

    // ── Tyre sizes ────────────────────────────────────────────────────────────────────────────

    /// <summary>205/55R16: a 16 inch rim (203.2 mm radius) and a sidewall 55 % of 205 mm (112.75),
    /// 315.95 mm free; rolling, 98 % of that.</summary>
    [Fact]
    public void Rolling_radius_comes_from_the_size_code()
    {
        var t = TyreSize.Parse("205/55R16");
        Assert.Equal(0.31595f, t.UnloadedRadiusMetres, 4);
        Assert.Equal(0.31595f * 0.98f, t.RollingRadiusMetres, 4);
        Assert.Equal("205/55R16", t.Code);
    }

    [Theory]
    [InlineData("P225/60R16", 225, 60, 16)]
    [InlineData("LT245/75R16", 245, 75, 16)]
    [InlineData("235/35ZR20", 235, 35, 20)]
    [InlineData("120/70ZR17", 120, 70, 17)]
    [InlineData("130/90B16", 130, 90, 16)]
    [InlineData("80/100-21", 80, 100, 21)]
    [InlineData("295/75R22.5", 295, 75, 22.5)]
    public void Every_sidewall_code_in_use_parses(string code, float width, float aspect, float rim)
    {
        var t = TyreSize.Parse(code);
        Assert.Equal(width, t.WidthMm);
        Assert.Equal(aspect, t.AspectPercent);
        Assert.Equal(rim, t.RimInches);
    }

    // ── Loads ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Standing still, every preset's wheel loads add up to its weight, and each axle group
    /// carries the share its centre of gravity puts on it.</summary>
    [Fact]
    public void At_rest_the_loads_sum_to_the_weight_in_the_declared_split()
    {
        foreach (var key in VehicleProfile.Presets.Keys)
        {
            var p = VehicleProfile.ByName(key);
            var body = new WheelDynamics(p);
            body.Loads(0f, 0f);
            float weight = p.MassKg * G;
            float sum = body.Wheels.Sum(w => w.Load);
            Assert.True(MathF.Abs(sum - weight) < weight * 1e-4f, $"{key}: loads {sum:F0} N against a weight of {weight:F0} N");

            float front = body.Wheels.Where(w => w.Front).Sum(w => w.Load);
            float share = body.B / (body.A + body.B);
            Assert.True(MathF.Abs(front - weight * share) < weight * 1e-4f, $"{key}: front {front:F0} N, expected {weight * share:F0}");
            _o.WriteLine($"{key,-24} {p.Running.Axles[0].Tyre,-12} front {front / weight:P0}  h {p.Running.CentreOfGravityHeightMetres:F2} m  wheels {body.Wheels.Length}");
        }
    }

    /// <summary>Braking at a, load moves from the rear group to the front by m a h / wheelbase.</summary>
    [Fact]
    public void Hard_braking_moves_load_to_the_front_by_m_a_h_over_the_wheelbase()
    {
        var p = VehicleProfile.ByName("i4_midsize");
        var body = new WheelDynamics(p);
        body.Loads(0f, 0f);
        float frontStatic = body.Wheels.Where(w => w.Front).Sum(w => w.Load);
        const float decel = 8f;
        body.Loads(-decel, 0f);
        float front = body.Wheels.Where(w => w.Front).Sum(w => w.Load);
        float expected = p.MassKg * decel * p.Running.CentreOfGravityHeightMetres / body.Wheelbase;
        Assert.Equal(expected, front - frontStatic, 0);
        Assert.Equal(p.MassKg * G, body.Wheels.Sum(w => w.Load), 0);
    }

    /// <summary>Cornering right, the left (outside) wheels gain what the right ones lose, m ay h
    /// over the track in all.</summary>
    [Fact]
    public void In_a_corner_the_outside_wheels_gain_load()
    {
        var p = VehicleProfile.ByName("i4_economy");
        var body = new WheelDynamics(p);
        body.Loads(0f, 0f);
        var still = body.Wheels.Select(w => w.Load).ToArray();
        const float ay = 5f;                              // turning right
        body.Loads(0f, ay);
        float moved = 0f;
        for (int i = 0; i < body.Wheels.Length; i++)
        {
            var w = body.Wheels[i];
            if (w.Y < 0f) Assert.True(w.Load > still[i], "an outside wheel lost load");
            if (w.Y > 0f) Assert.True(w.Load < still[i], "an inside wheel gained load");
            if (w.Y < 0f) moved += w.Load - still[i];
        }
        // Each axle moves its roll share of m ay h over its own track; the two tracks differ a little.
        float h = p.Running.CentreOfGravityHeightMetres;
        float tMin = p.Running.Axles.Min(a => a.TrackMetres), tMax = p.Running.Axles.Max(a => a.TrackMetres);
        Assert.InRange(moved, p.MassKg * ay * h / tMax - 1f, p.MassKg * ay * h / tMin + 1f);
    }

    // ── Wheels turning ────────────────────────────────────────────────────────────────────────

    /// <summary>Rolling free in a straight line, a wheel turns at v / r: no force, no slip.</summary>
    [Fact]
    public void A_free_rolling_wheel_turns_at_speed_over_rolling_radius()
    {
        var p = VehicleProfile.ByName("i4_turbo");
        var body = new WheelDynamics(p) { Vx = 20f };
        body.Step(1f / 30f, 0f, 0f);
        foreach (var w in body.Wheels)
        {
            Assert.Equal(0f, w.SlipRatio, 4);
            Assert.Equal(body.Vx / w.Radius, w.AngularSpeed, 3);
        }
        // ...and a driven wheel pulling turns a little faster than that, a braked one slower.
        body.Step(1f / 30f, 0f, 3f);
        Assert.Contains(body.Wheels, w => w.Driven && w.SlipRatio > 0.001f && w.AngularSpeed > body.Vx / w.Radius);
        body.Step(1f / 30f, 0f, -6f);
        Assert.All(body.Wheels, w => Assert.True(w.SlipRatio < 0f && w.AngularSpeed < body.Vx / w.Radius));
    }

    // ── Corners ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A steady corner taken faster and faster (a 50 m radius, the steady state at each speed): the
    /// outside wheels carry more load, and the front axle reaches its friction limit while the rear
    /// still has grip in hand — understeer, from a front-heavy car on tyres whose grip per newton
    /// falls with load. The outside front is the most loaded wheel and makes the most force.
    ///
    /// Within the front axle the two wheels run at one slip angle, and the light inside one, whose
    /// force peaks at a smaller slip angle (Pacejka's cornering stiffness saturates with load), gets
    /// there a little before the outside one. So "the outside front reaches its limit first" holds
    /// for the axle the outside front is on, not against its partner; the test says so.
    /// </summary>
    [Fact]
    public void In_a_steady_corner_the_front_axle_reaches_its_limit_first()
    {
        var p = VehicleProfile.ByName("i4_economy");
        const float r = 50f;
        var body = new WheelDynamics(p);
        float frontAt = 0f, rearAt = 0f;
        WheelDynamics.Wheel[]? atLimit = null;
        for (float v = 5f; v < 40f && frontAt == 0f; v += 0.05f)
        {
            float worst = body.SteadyTurn(v, 1f / r);          // turning right: the left wheels are outside
            if (worst >= 2f) break;
            float front = body.Wheels.Where(w => w.Front).Max(w => w.Demand);
            float rear = body.Wheels.Where(w => !w.Front).Max(w => w.Demand);
            if (front >= 0.99f) { frontAt = v * v / r / G; atLimit = body.Wheels.ToArray(); }
            if (rear >= 0.99f && rearAt == 0f) rearAt = v * v / r / G;
        }
        Assert.True(frontAt > 0f, "the front never reached its limit");
        Assert.True(rearAt == 0f, $"the rear reached its limit (at {rearAt:F2} g) no later than the front ({frontAt:F2} g)");
        _o.WriteLine($"front axle at its limit at {frontAt:F2} g; " + string.Join("  ", atLimit!.Select(w => $"[x {w.X:F2} y {w.Y:F2}: {w.Load:F0} N, Fy {w.Fy:F0}, demand {w.Demand:F2}]")));

        var outsideFront = atLimit.First(w => w.Front && w.Y < 0f);
        var insideFront = atLimit.First(w => w.Front && w.Y > 0f);
        Assert.Equal(atLimit.Max(w => w.Load), outsideFront.Load);
        Assert.Equal(atLimit.Max(w => MathF.Abs(w.Fy)), MathF.Abs(outsideFront.Fy));
        Assert.True(outsideFront.Load > insideFront.Load);
        Assert.True(atLimit.Where(w => w.Y < 0f).All(w => w.Load > atLimit.First(o => o.Axle == w.Axle && o.Y > 0f).Load));
        Assert.True(outsideFront.Demand > 0.8f, $"the outside front is at {outsideFront.Demand:F2} of its grip when the axle lets go");
    }

    // ── Following a lane ──────────────────────────────────────────────────────────────────────

    /// <summary>Two straights joined by half-circles of radius r, driven anticlockwise from the
    /// start of the first straight.</summary>
    private static List<Vector3> Stadium(float r, float straight)
    {
        var pts = new List<Vector3>();
        for (int k = 0; k < 40; k++) pts.Add(new Vector3(r, 0f, straight * k / 40f));
        for (int k = 0; k < 36; k++) { double a = Math.PI * k / 36; pts.Add(new Vector3((float)(r * Math.Cos(a)), 0f, straight + (float)(r * Math.Sin(a)))); }
        for (int k = 0; k < 40; k++) pts.Add(new Vector3(-r, 0f, straight - straight * k / 40f));
        for (int k = 0; k < 36; k++) { double a = Math.PI * k / 36; pts.Add(new Vector3(-(float)(r * Math.Cos(a)), 0f, -(float)(r * Math.Sin(a)))); }
        return pts;
    }

    /// <summary>
    /// A driver as traffic has one: the speed the line and the bends allow, reached at the car's
    /// acceleration and braking, steered along the line at an offset.
    /// </summary>
    private static float Lap(RaceLine line, LineFollower driver, ref float at, float cornering, float accel, float brake,
                             Func<float, float> offset, int ticks, Action<int>? each = null)
    {
        const float dt = 1f / 30f;
        var body = driver.Body;
        float worst = 0f;
        for (int t = 0; t < ticks; t++)
        {
            float look = MathF.Max(8f, body.Vx * body.Vx / (2f * brake));
            float want = MathF.Min(line.SlowestWithin(at, look),
                                   line.BendSpeedWithin(at, 2f * look, cornering, brake, k => body.SteadyTurnSpeed(k, TyreFriction.SquealOnset)));
            float target = want > body.Vx ? MathF.Min(want, body.Vx + accel * dt) : MathF.Max(want, body.Vx - brake * dt);
            at += driver.Drive(line, at, target, offset(t * dt), dt);
            each?.Invoke(t);
            worst = MathF.Max(worst, body.MaxDemand);
        }
        return worst;
    }

    /// <summary>
    /// Round a 40 m bend at the speed the tyres allow, a mid-size saloon steered by its driver stays
    /// within a quarter of a metre of its lane's line, all the way round two laps.
    /// </summary>
    [Fact]
    public void A_corner_at_speed_stays_on_the_lane()
    {
        var line = new RaceLine(Stadium(40f, 200f), 0f, 25f, 0.5f, 4f);
        var body = new WheelDynamics(VehicleProfile.ByName("i4_midsize"), 0.85f) { ForwardOnly = true, Modulated = true, Vx = 10f };
        var driver = new LineFollower(body);
        float at = 0f, worstOffset = 0f, fastestInBend = 0f;
        float worst = Lap(line, driver, ref at, 0.5f, 2.5f, 4f, _ => 0f, 30 * 120, t =>
        {
            if (t < 30 * 5) return;
            worstOffset = MathF.Max(worstOffset, MathF.Abs(driver.Offset));
            if (MathF.Abs(line.CurvatureAt(at)) > 0.02f) fastestInBend = MathF.Max(fastestInBend, body.Vx);
        });
        _o.WriteLine($"two laps: furthest {worstOffset:F2} m off the line, {fastestInBend:F1} m/s in the bends, worst tyre {worst:F2}");
        Assert.True(fastestInBend > 10f, $"it crawled round the bends at {fastestInBend:F1} m/s");
        Assert.True(worstOffset < 0.25f, $"it ran {worstOffset:F2} m off its line");
    }

    /// <summary>
    /// A lane change at 54 km/h: moving its aim 3.5 m to the right over four seconds, about as long as
    /// a driver takes (naturalistic lane changes run 3 to 8 s: Lee, Olsen and Wierwille, "A
    /// Comprehensive Examination of Naturalistic Lane-Changes", NHTSA DOT HS 809 702, 2004), the car
    /// is in the new lane within two seconds of the aim arriving, overshooting by less than a
    /// quarter of a metre, and stays there.
    /// </summary>
    [Fact]
    public void A_lane_change_at_speed_settles_in_the_new_lane()
    {
        var line = new RaceLine(Stadium(60f, 1200f), 0f, 15f, 0.5f, 4f);
        var body = new WheelDynamics(VehicleProfile.ByName("i4_midsize"), 0.85f) { ForwardOnly = true, Modulated = true, Vx = 15f };
        var driver = new LineFollower(body);
        float at = 0f, most = 0f, settledAt = float.NaN;
        const float change = 3f, lane = 3.5f, takes = 4f;
        Lap(line, driver, ref at, 0.5f, 2.5f, 4f,
            s => lane * 0.5f * (1f - MathF.Cos(MathF.PI * Math.Clamp((s - change) / takes, 0f, 1f))), 30 * 14, t =>
        {
            float s = t / 30f;
            if (s < change + takes) return;
            most = MathF.Max(most, driver.Offset);
            if (MathF.Abs(driver.Offset - lane) < 0.15f) { if (float.IsNaN(settledAt)) settledAt = s; }
            else settledAt = float.NaN;
        });
        _o.WriteLine($"settled {settledAt - change - takes:F1} s after the aim arrived, overshoot {most - lane:F2} m, ends {driver.Offset:F2} m right at {body.Vx:F1} m/s");
        Assert.True(!float.IsNaN(settledAt) && settledAt - change - takes < 2f, $"it had not settled in the new lane (now {driver.Offset:F2} m)");
        Assert.True(most - lane < 0.25f, $"it overshot the new lane by {most - lane:F2} m");
    }

    /// <summary>The cost of one vehicle's wheels and steering for one tick, printed; generous bound
    /// because the machine running the suite may be busy.</summary>
    [Fact]
    public void A_steered_vehicle_costs_microseconds_a_tick()
    {
        var line = new RaceLine(Stadium(40f, 200f), 0f, 25f, 0.5f, 4f);
        var body = new WheelDynamics(VehicleProfile.ByName("i4_midsize"), 0.85f) { ForwardOnly = true, Modulated = true, Vx = 10f };
        var driver = new LineFollower(body);
        float at = 0f;
        Lap(line, driver, ref at, 0.5f, 2.5f, 4f, _ => 0f, 300);
        var clock = Stopwatch.StartNew();
        const int n = 30 * 60;
        for (int t = 0; t < n; t++) at += driver.Drive(line, at, 12f, 0f, 1f / 30f);
        clock.Stop();
        double us = clock.Elapsed.TotalMilliseconds * 1000.0 / n;
        _o.WriteLine($"{us:F1} us a vehicle a tick (wheels, steering and the line)");
        Assert.True(us < 200.0, $"{us:F0} us a tick");
    }
}
