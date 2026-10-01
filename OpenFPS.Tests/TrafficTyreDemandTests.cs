using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Ordinary driving does not make tyres squeal. Real traffic brakes at 1 to 3 m/s^2 and corners
/// at the side friction drivers find comfortable (DriverSteering.ComfortSideFriction); a tyre sings
/// on a hard stop or a fast turn, not at a junction taken normally. These check the wheels the city's
/// traffic runs on against that, wheel by wheel.
/// </summary>
public class TrafficTyreDemandTests
{
    private readonly ITestOutputHelper _o;
    public TrafficTyreDemandTests(ITestOutputHelper o) => _o = o;

    /// <summary>The city's presets with the grip the map gives them, and the service brake the map
    /// derives from it (a third of the grip, tools/gen_city.py brake_for).</summary>
    public static IEnumerable<object[]> CityVehicles() => new[]
    {
        new object[] { "i4_midsize", 0.85f }, new object[] { "v6", 0.85f }, new object[] { "i4_economy", 0.85f },
        new object[] { "cummins_compound", 0.75f }, new object[] { "mail_truck", 0.75f },
        new object[] { "step_van", 0.70f }, new object[] { "transit_bus", 0.70f },
    };

    private static float ServiceBrake(float grip) => MathF.Round(grip * 9.81f * 0.35f, 2);

    /// <summary>
    /// An ordinary stop from 50 km/h at the vehicle's service braking: no wheel reaches the squeal
    /// onset, and no wheel's slip ratio goes past a few per cent (a tyre's longitudinal force peaks
    /// near 10 % slip).
    /// </summary>
    [Theory]
    [MemberData(nameof(CityVehicles))]
    public void An_ordinary_stop_from_50_is_quiet_on_every_wheel(string preset, float grip)
    {
        var body = new WheelDynamics(VehicleProfile.ByName(preset), grip) { ForwardOnly = true, Modulated = true, Vx = 50f / 3.6f };
        float b = ServiceBrake(grip);
        const float dt = 1f / 30f;
        float worst = 0f, slip = 0f;
        for (int t = 0; t < 30 * 20 && body.Vx > 0.05f; t++)
        {
            // As traffic asks for it: the speed it wants at the end of the tick, no lower than the
            // brake allows.
            float target = MathF.Max(0f, body.Vx - b * dt);
            body.Step(dt, 0f, (target - body.Vx) / dt);
            foreach (var w in body.Wheels) { worst = MathF.Max(worst, w.Demand); slip = MathF.Max(slip, MathF.Abs(w.SlipRatio)); }
        }
        _o.WriteLine($"{preset}: braking at {b:F2} m/s2, worst wheel {worst:F2} of its grip, slip ratio {slip:F3}");
        Assert.True(body.Vx < 0.1f, $"it did not stop ({body.Vx:F2} m/s)");
        Assert.True(worst < TyreFriction.SquealOnset, $"a wheel reached {worst:F2} of its grip in an ordinary stop");
        Assert.True(slip < 0.04f, $"a wheel slipped {slip:F3} in an ordinary stop");
    }

    /// <summary>A block of four straights joined by quarter turns of radius r, as a car goes round a
    /// city block turning at each junction.</summary>
    private static List<Vector3> Block(float r, float side)
    {
        var pts = new List<Vector3>();
        var signs = new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(-1, -1), new Vector2(1, -1) };
        Vector2 Centre(int c) => signs[c % 4] * (side - r);
        Vector2 On(int c, double a) => Centre(c) + r * new Vector2((float)Math.Cos(a), (float)Math.Sin(a));
        for (int c = 0; c < 4; c++)
        {
            double a0 = Math.PI / 2 * c;
            // Along the side from the last corner's end into this corner, then round it.
            var leaving = On(c + 3, a0);
            var arriving = On(c, a0);
            for (int k = 0; k < 30; k++)
            {
                var p = Vector2.Lerp(leaving, arriving, k / 30f);
                pts.Add(new Vector3(p.X, 0f, p.Y));
            }
            for (int k = 0; k < 12; k++)
            {
                var p = On(c, a0 + Math.PI / 2 * k / 12);
                pts.Add(new Vector3(p.X, 0f, p.Y));
            }
        }
        return pts;
    }

    /// <summary>
    /// Driven as the city's traffic drives: the slowest of the line's limit and what the bends ahead
    /// allow (RaceLine.BendSpeedWithin with the driver's comfortable side friction and the tyres'
    /// quiet limit), reached at the map's acceleration and service braking, pulling away only with
    /// what the comfortable ellipse leaves beside the cornering the driver feels.
    /// </summary>
    private static void DriveLikeTraffic(RaceLine line, LineFollower driver, float cornering, float accel, float brake, int ticks, Action each)
    {
        const float dt = 1f / 30f;
        var body = driver.Body;
        float at = 0f;
        Func<float, float> corner = k => MathF.Min(DriverSteering.ComfortTurnSpeed(k), body.SteadyTurnSpeed(k, TyreFriction.SquealOnset));
        for (int t = 0; t < ticks; t++)
        {
            float look = MathF.Max(8f, body.Vx * body.Vx / (2f * brake));
            float want = MathF.Min(line.SlowestWithin(at, look),
                                   line.BendSpeedWithin(at, 2f * look, cornering, brake, corner, DriverSteering.ComfortSideFriction));
            float lateral = MathF.Max(body.Vx * body.Vx * MathF.Abs(line.CurvatureAt(at)), MathF.Abs(body.Ay));
            float used = lateral / (MathF.Min(cornering, DriverSteering.ComfortSideFriction(body.Vx)) * WheelDynamics.G);
            float up = accel * MathF.Sqrt(MathF.Max(0f, 1f - used * used));
            float target = want > body.Vx ? MathF.Min(want, body.Vx + up * dt) : MathF.Max(want, body.Vx - brake * dt);
            at += driver.Drive(line, at, target, 0f, dt);
            each();
        }
    }

    /// <summary>
    /// Round a city block at 50 km/h on the straights, turning at each corner on a 10 m radius: the
    /// tyres stay below the squeal onset and the slip angles small all the way round, for each of the
    /// city's vehicles.
    /// </summary>
    [Theory]
    [MemberData(nameof(CityVehicles))]
    public void Turning_at_city_junctions_does_not_scrub(string preset, float grip)
    {
        var line = new RaceLine(Block(10f, 60f), 0f, 50f / 3.6f, 0.5f, ServiceBrake(grip));
        var body = new WheelDynamics(VehicleProfile.ByName(preset), grip) { ForwardOnly = true, Modulated = true };
        var driver = new LineFollower(body);
        float worst = 0f, angle = 0f, fastest = 0f;
        int tick = 0;
        DriveLikeTraffic(line, driver, 0.48f, 2.2f, ServiceBrake(grip), 30 * 90, () =>
        {
            if (tick++ < 30 * 15) return;
            worst = MathF.Max(worst, body.MaxDemand);
            foreach (var w in body.Wheels) angle = MathF.Max(angle, MathF.Abs(w.SlipAngle));
            fastest = MathF.Max(fastest, body.Vx);
        });
        _o.WriteLine($"{preset}: worst wheel {worst:F2} of its grip, largest slip angle {angle * 180f / MathF.PI:F1} deg, top {fastest * 3.6f:F0} km/h, {driver.Offset:F2} m off");
        Assert.True(fastest > 30f / 3.6f, $"it crawled ({fastest * 3.6f:F0} km/h at best)");
        // A driver pulling away out of the turn eases off at the squeal onset (WheelDynamics.PullingUse),
        // so a light-tailed van can sit exactly there: silent, which is what is asked.
        Assert.True(TyreFriction.SquealAmount(worst) < 0.01f, $"a tyre reached {worst:F2} of its grip at an ordinary junction");
        Assert.True(angle < 4f * MathF.PI / 180f, $"a tyre ran at {angle * 180f / MathF.PI:F1} degrees of slip");
    }

    /// <summary>
    /// At walking pace, steering and easing on and off the brakes, every wheel's slip stays finite,
    /// small, and changes smoothly from tick to tick: no division by a speed going to zero.
    /// </summary>
    [Fact]
    public void Slip_is_bounded_and_smooth_at_walking_pace()
    {
        var body = new WheelDynamics(VehicleProfile.ByName("i4_midsize"), 0.85f) { ForwardOnly = true, Modulated = true };
        const float dt = 1f / 30f;
        float[] lastRatio = new float[body.Wheels.Length], lastAngle = new float[body.Wheels.Length];
        float worstRatio = 0f, worstAngle = 0f, jumpRatio = 0f, jumpAngle = 0f;
        for (int t = 0; t < 30 * 20; t++)
        {
            float s = t * dt;
            // Pull away to 2 m/s, creep, stop, pull away again, with the wheel turning to and fro.
            float want = s < 4f ? 2f : s < 8f ? 0.6f : s < 11f ? 0f : 1.5f;
            float accel = Math.Clamp((want - body.Vx) / dt, -2.9f, 1.5f);
            float steer = 0.45f * MathF.Sin(s * 0.9f);
            body.Step(dt, steer, accel);
            for (int i = 0; i < body.Wheels.Length; i++)
            {
                var w = body.Wheels[i];
                Assert.True(float.IsFinite(w.SlipRatio) && float.IsFinite(w.SlipAngle) && float.IsFinite(w.Demand));
                worstRatio = MathF.Max(worstRatio, MathF.Abs(w.SlipRatio));
                worstAngle = MathF.Max(worstAngle, MathF.Abs(w.SlipAngle));
                if (t > 0)
                {
                    jumpRatio = MathF.Max(jumpRatio, MathF.Abs(w.SlipRatio - lastRatio[i]));
                    jumpAngle = MathF.Max(jumpAngle, MathF.Abs(w.SlipAngle - lastAngle[i]));
                }
                lastRatio[i] = w.SlipRatio; lastAngle[i] = w.SlipAngle;
            }
            Assert.True(body.MaxDemand < TyreFriction.SquealOnset, $"at {body.Vx:F2} m/s a wheel is at {body.MaxDemand:F2} of its grip");
        }
        _o.WriteLine($"slip ratio up to {worstRatio:F3} (largest step {jumpRatio:F3}), slip angle up to {worstAngle * 180f / MathF.PI:F2} deg (largest step {jumpAngle * 180f / MathF.PI:F2} deg)");
        Assert.True(worstRatio < 0.03f, $"slip ratio reached {worstRatio:F3} at walking pace");
        Assert.True(worstAngle < 3f * MathF.PI / 180f, $"slip angle reached {worstAngle * 180f / MathF.PI:F1} degrees at walking pace");
        Assert.True(jumpRatio < 0.02f, $"slip ratio jumped {jumpRatio:F3} in one tick");
        Assert.True(jumpAngle < 1f * MathF.PI / 180f, $"slip angle jumped {jumpAngle * 180f / MathF.PI:F2} degrees in one tick");
    }

    /// <summary>The comfortable side friction follows the Green Book's low-speed figures, and the
    /// speed it gives round a 10 m junction turn is what drivers do (about 20 km/h).</summary>
    [Fact]
    public void Comfortable_side_friction_is_the_green_books()
    {
        Assert.Equal(0.38f, DriverSteering.ComfortSideFriction(10f * 0.44704f), 3);
        Assert.Equal(0.26f, DriverSteering.ComfortSideFriction(20f * 0.44704f), 3);
        Assert.Equal(0.20f, DriverSteering.ComfortSideFriction(30f * 0.44704f), 3);
        float v = DriverSteering.ComfortTurnSpeed(1f / 10f);
        _o.WriteLine($"10 m turn: {v * 3.6f:F1} km/h, {v * v / 10f / 9.81f:F2} g");
        Assert.InRange(v * 3.6f, 17f, 23f);
        Assert.Equal(v, DriverSteering.ComfortTurnSpeed(-1f / 10f));
    }

    /// <summary>
    /// The planner's braking room over a stretch whose curvature climbs into a bend: driving the
    /// envelope it plans, braking and cornering together stay inside the comfortable ellipse (the
    /// service brake on one axis, the comfortable side friction on the other).
    /// </summary>
    [Fact]
    public void Braking_into_a_tightening_bend_stays_inside_the_comfortable_ellipse()
    {
        var line = new RaceLine(Block(8f, 60f), 0f, 50f / 3.6f, 0.5f, 2.92f);
        Func<float, float> corner = DriverSteering.ComfortTurnSpeed;
        float worst = 0f, v = 50f / 3.6f, at = 0f;
        const float dt = 1f / 30f;
        for (int t = 0; t < 30 * 60; t++)
        {
            float want = line.BendSpeedWithin(at, 40f, 0.48f, 2.92f, corner, DriverSteering.ComfortSideFriction);
            float next = want > v ? MathF.Min(want, v + 1f * dt) : MathF.Max(want, v - 2.92f * dt);
            float decel = MathF.Max(0f, (v - next) / dt);
            float lateral = next * next * MathF.Abs(line.CurvatureAt(at));
            float side = DriverSteering.ComfortSideFriction(next) * WheelDynamics.G;
            float use = MathF.Sqrt((decel / 2.92f) * (decel / 2.92f) + (lateral / side) * (lateral / side));
            if (t > 30 * 5) worst = MathF.Max(worst, use);
            v = next;
            at += v * dt;
        }
        _o.WriteLine($"braking and cornering together reached {worst:F2} of the comfortable ellipse");
        Assert.True(worst < 1.1f, $"braking and cornering together reached {worst:F2} of the comfortable ellipse");
    }

    /// <summary>
    /// Following: creeping up a stopped queue a little inside the standing gap, the ACC model brakes
    /// near the comfortable rate where the IDM alone stamps on the brakes; closing fast on a stopped
    /// car it still brakes hard.
    /// </summary>
    [Fact]
    public void Following_brakes_hard_only_when_it_has_to()
    {
        float a = 2.2f, b = 2.92f;
        // 1.4 m/s, a metre from a stopped car, wanting two.
        float s = 1f, v = 1.4f, s0 = 2f;
        float sStar = s0 + v * 1.5f + v * v / (2f * MathF.Sqrt(a * b));
        float idm = a * (1f - (sStar / s) * (sStar / s));
        float acc = VehicleSystem.Acc(idm, v, 0f, 0f, s, a, b);
        _o.WriteLine($"creeping: IDM {idm:F1}, ACC {acc:F1} m/s2");
        Assert.True(idm < -2f * b);
        Assert.True(acc > -1.5f * b, $"the ACC model braked at {acc:F1} m/s2 creeping up a queue");
        // 12 m/s, 10 m from a stopped car: needs 7.2 m/s2 to stop in time.
        s = 10f; v = 12f;
        sStar = s0 + v * 1.5f + v * v / (2f * MathF.Sqrt(a * b));
        idm = a * (1f - (sStar / s) * (sStar / s));
        acc = VehicleSystem.Acc(idm, v, 0f, 0f, s, a, b);
        _o.WriteLine($"closing: IDM {idm:F1}, ACC {acc:F1} m/s2");
        Assert.True(acc < -v * v / (2f * s), $"closing on a stopped car it braked at only {acc:F1} m/s2");
    }
}
