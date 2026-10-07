using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// How a driver steers to follow a line: the gains of the path-following law, and how fast hands
/// turn a wheel.
/// </summary>
public sealed record DriverSteering
{
    /// <summary>Cross-track gain k of the Stanley law, 1/s: atan(k e / (v_soft + v)).</summary>
    public float CrossTrackGain { get; init; } = 2.5f;
    /// <summary>The softening speed that keeps the cross-track term finite at walking pace, m/s.</summary>
    public float SoftSpeed { get; init; } = 1f;
    /// <summary>Seconds of steer per rad/s of yaw rate the body is short of the line's: damping.</summary>
    public float YawDamping { get; init; } = 0.05f;
    /// <summary>Seconds for hands to wind the wheel from straight to full lock, as the player's car.</summary>
    public float SecondsToLock { get; init; } = 0.7f;

    public static DriverSteering Default { get; } = new();

    /// <summary>
    /// The side friction, g, at which ordinary drivers start to feel uncomfortable in a bend, against
    /// speed in m/s. AASHTO, A Policy on Geometric Design of Highways and Streets (the Green Book), 2011,
    /// section 3.3, from ball-bank indicators (figure 3-6): 0.38 at 10 mph, 0.26 at 20, 0.20 at 30, 0.17
    /// at 40; for high-speed design 0.14 at 50, 0.12 at 60, 0.08 at 80. Held flat outside the table.
    /// </summary>
    public static readonly (float Speed, float SideFriction)[] ComfortTable =
    {
        (10f * 0.44704f, 0.38f), (20f * 0.44704f, 0.26f), (30f * 0.44704f, 0.20f), (40f * 0.44704f, 0.17f),
        (50f * 0.44704f, 0.14f), (60f * 0.44704f, 0.12f), (80f * 0.44704f, 0.08f),
    };

    /// <summary>The comfortable side friction at this speed (see <see cref="ComfortTable"/>).</summary>
    public static float ComfortSideFriction(float speed)
    {
        var t = ComfortTable;
        if (speed <= t[0].Speed) return t[0].SideFriction;
        for (int i = 1; i < t.Length; i++)
            if (speed <= t[i].Speed)
            {
                float f = (speed - t[i - 1].Speed) / (t[i].Speed - t[i - 1].Speed);
                return t[i - 1].SideFriction + (t[i].SideFriction - t[i - 1].SideFriction) * f;
            }
        return t[^1].SideFriction;
    }

    /// <summary>The speed an ordinary driver takes a steady bend of this curvature at, m/s: where
    /// v^2 |k| reaches the comfortable side friction, found by halving (it rises with v).</summary>
    public static float ComfortTurnSpeed(float curvature)
    {
        float k = MathF.Abs(curvature);
        if (k < 1e-5f) return float.PositiveInfinity;
        float lo = 0f, hi = 80f;
        for (int i = 0; i < 30; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (mid * mid * k <= ComfortSideFriction(mid) * WheelDynamics.G) lo = mid; else hi = mid;
        }
        return lo;
    }
}

/// <summary>
/// A vehicle driven along a <see cref="RaceLine"/> by a driver who steers it. The body moves under its
/// tyres (<see cref="WheelDynamics"/>) and is kept relative to the line: a distance along it (the point
/// beside the body, which the traffic logic runs on), an offset to its right and a heading error.
///
/// The steering law is Stanley's (Thrun et al., "Stanley: the robot that won the DARPA Grand
/// Challenge", Journal of Field Robotics 23(9), 2006; Hoffmann, Tomlin, Montemerlo and Thrun,
/// "Autonomous automobile trajectory tracking for off-road driving", American Control Conference
/// 2007), measured at the front axle, with the line's curvature fed forward through the kinematic
/// angle and the body's understeer gradient, and a yaw-rate damping term:
///
///   delta = atan(L k_line) + K_us v^2 k_line - psi_e - atan(k e_front / (v_soft + v)) + c (r_line - r)
///
/// where e_front is how far right of where it should be the front axle is and psi_e is the angle
/// between the line and the body's heading, less the sideslip a steady turn would give it. It is turned no faster
/// than hands turn a wheel, no further than the lock, and no further past the front axle's own
/// direction of travel than the slip angle at which its tyres give their most.
/// </summary>
public sealed class LineFollower
{
    public WheelDynamics Body { get; }
    public DriverSteering Driver { get; }

    /// <summary>Metres to the right of the line the body's centre is.</summary>
    public float Offset;
    /// <summary>Radians the body points to the right of the line.</summary>
    public float HeadingError;
    /// <summary>The road-wheel angle on, radians.</summary>
    public float Steer => Body.SteerAngle;

    private float _steer;

    public LineFollower(WheelDynamics body, DriverSteering? driver = null)
    {
        Body = body;
        Driver = driver ?? DriverSteering.Default;
    }

    /// <summary>One step: steer for the line, ask the tyres for the speed, move. Returns the distance
    /// gained along the line.</summary>
    /// <param name="at">Distance along the line now.</param>
    /// <param name="targetSpeed">The speed the driver wants to be doing at the end of the step.</param>
    /// <param name="targetOffset">Where the driver wants to be, metres right of the line.</param>
    public float Drive(RaceLine line, float at, float targetSpeed, float targetOffset, float dt)
    {
        var body = Body;
        float v = body.Vx;
        float lf = body.A;

        // ── Steering ──
        line.Sample(at + lf, out _, out float pathAhead, out _);
        line.Sample(at, out _, out float pathHere, out _);
        float bodyHeading = pathHere + HeadingError;
        // Against the heading it should have, less the steady turn's sideslip, or its nose onto the line
        // holds it wide (Hoffmann et al. 2007). Its actual heading, not its motion, so a sliding tail is
        // caught.
        float k = line.CurvatureAt(at + lf);
        float speed = MathF.Max(0f, v);
        float headingError = MathF.IEEERemainder(bodyHeading + body.SteadySideslip(speed, k) - pathAhead, 2f * MathF.PI);
        float frontError = Offset + lf * MathF.Sin(HeadingError) - targetOffset;
        float feedForward = MathF.Atan(body.Wheelbase * k) + body.UndersteerGradient * speed * speed * k;
        float want = feedForward - headingError
                   - MathF.Atan(Driver.CrossTrackGain * frontError / (Driver.SoftSpeed + speed))
                   + Driver.YawDamping * (speed * line.CurvatureAt(at) - body.YawRate);
        // No further past the front axle's course than the tyres' peak slip angle: further, a tyre gives
        // less and the car ploughs wider. At walking pace the lock is the only limit.
        if (speed > WheelDynamics.KinematicBelow)
        {
            float course = MathF.Atan2(body.Vy + body.YawRate * body.A, speed);
            float peak = body.SteeredPeakSlip();
            want = Math.Clamp(want, course - peak, course + peak);
        }
        float lockAngle = body.Chassis.MaxSteerAngleRad;
        want = Math.Clamp(want, -lockAngle, lockAngle);
        float rate = lockAngle / MathF.Max(0.05f, Driver.SecondsToLock) * dt;
        _steer += Math.Clamp(want - _steer, -rate, rate);

        // ── Moving ──
        body.Step(dt, _steer, (targetSpeed - v) / MathF.Max(1e-3f, dt));

        // The ground covered, read against the line's frame halfway along: exact to second order on a
        // curve (a chord is square to the radius through its middle).
        float sinH = MathF.Sin(bodyHeading), cosH = MathF.Cos(bodyHeading);
        var forward = new Vector2(sinH, cosH);
        var right = new Vector2(cosH, -sinH);
        var moved = forward * body.TickForward + right * body.TickRight;
        float kHere = line.CurvatureAt(at);
        float guess = Vector2.Dot(moved, new Vector2(MathF.Sin(pathHere), MathF.Cos(pathHere)));
        line.Sample(at + 0.5f * guess, out _, out float mid, out _);
        var tangent = new Vector2(MathF.Sin(mid), MathF.Cos(mid));
        var normal = new Vector2(MathF.Cos(mid), -MathF.Sin(mid));
        float shrink = 1f - kHere * Offset;
        float gained = Vector2.Dot(moved, tangent) / MathF.Max(0.2f, shrink);
        Offset += Vector2.Dot(moved, normal);
        line.Sample(at + gained, out _, out float pathNext, out _);
        HeadingError = MathF.IEEERemainder(bodyHeading + body.TickYaw - pathNext, 2f * MathF.PI);
        return gained;
    }

    /// <summary>Put where it stands still: on the line at an offset, square to it.</summary>
    public void Place(float offset)
    {
        Offset = offset;
        HeadingError = 0f;
        _steer = 0f;
        Body.Halt();
    }

    /// <summary>The body's position and heading in the world, at a distance along the line.</summary>
    public void Pose(RaceLine line, float at, out Vector3 position, out float heading)
    {
        line.Sample(at, out var here, out float path, out _);
        position = here + new Vector3(MathF.Cos(path), 0f, -MathF.Sin(path)) * Offset;
        heading = path + HeadingError;
    }
}
