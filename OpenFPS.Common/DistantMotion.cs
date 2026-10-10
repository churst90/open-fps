using System.Numerics;

namespace OpenFPS.Common.Networking;

/// <summary>
/// How a moving thing is carried between the states the server sends about it, and when a far one must be
/// sent again (docs/CODY_ASKS_2026-10-08.md section 4; docs/WORLD_STREAMING.md, "Far things less often").
///
/// <para>Beyond <see cref="FullRateMetres"/> a moving thing goes at most every <see cref="IntervalTicks"/>
/// ticks, with how it is changing as the server has it (<see cref="Rates"/>: its speed's rate and its
/// heading's turn). Between its states the client carries it forward on those numbers (<see cref="Predict"/>).
/// The server runs the same prediction for every far thing and sends it again the tick the prediction strays
/// from the truth by more than an ear could tell (<see cref="Strayed"/>), and once more the tick after, so
/// one lost packet does not leave the client carrying it wrongly. In the last tick before a state arrives the
/// client goes from the prediction to the state in a straight line, as it goes from tick to tick for
/// everything near.</para>
///
/// <para>The tolerances are a fifth of what was agreed as inaudible (a bearing within 1 degree, a pitch within
/// 0.5 %), and a position never more out than a tenth of the way the thing moves in a tick, so the
/// correction in that last tick is never heard as a step.</para>
/// </summary>
public static class DistantMotion
{
    /// <summary>Nearer than this a thing goes every tick, as before. Cody's figure (2026-10-08).</summary>
    public const float FullRateMetres = 150f;

    /// <summary>The longest a far moving thing goes without a state: 5 times a second at the 30 Hz tick.</summary>
    public const int IntervalTicks = 6;

    /// <summary>From here out a moving thing's state carries its rates, so the first far prediction has them.</summary>
    public const float RatesMetres = 0.8f * FullRateMetres;

    /// <summary>Longest the client carries a thing forward with nothing newer. Past it the thing waits where
    /// the prediction took it: the stream has stalled, or the thing has gone without a word.</summary>
    public const float MaxPredictSeconds = 0.5f;

    /// <summary>The bearing a prediction may be out by, degrees: a tenth of the agreed 1.</summary>
    public const float BearingToleranceDegrees = 0.1f;

    /// <summary>The share of a tick's travel a prediction may be out by: the last tick's correction adds no
    /// more than this to how far the thing moves in it.</summary>
    public const float StepTolerance = 0.1f;

    /// <summary>The speed a prediction may be out by, a fraction: an engine's pitch follows its road speed,
    /// so 0.1 % of speed is 0.1 % of pitch.</summary>
    public const float SpeedTolerance = 0.001f;

    /// <summary>...and never asked finer than this, m/s (the wire carries a millimetre a second).</summary>
    public const float SpeedFloor = 0.002f;

    /// <summary>How far the velocity may be out in any direction, m/s: Doppler goes as the speed toward the
    /// listener over the speed of sound, and 0.1 % of 343 m/s is 0.34 m/s.</summary>
    public const float VelocityTolerance = 0.001f * 343f;

    /// <summary>The heading a prediction may be out by, degrees (a horn's or a tailpipe's aim).</summary>
    public const float TurnToleranceDegrees = 1f;

    /// <summary>How far a tyre demand may move before it goes, as a fraction of the limit.</summary>
    public const float DemandTolerance = 0.05f;

    /// <summary>How a thing is changing: its speed's rate (m/s²), and its heading's turn as a rotation vector
    /// (radians a second about its axis). The body turns with the heading.</summary>
    public readonly struct Rates
    {
        public readonly float SpeedRate;
        public readonly Vector3 Turn;

        public Rates(float speedRate, Vector3 turn) { SpeedRate = speedRate; Turn = turn; }

        public static readonly Rates None = default;

        /// <summary>As a state carries them.</summary>
        public static Rates Of(in EntityState s)
            => new(s.SpeedRate / 1000f, new Vector3(s.TurnX, s.TurnY, s.TurnZ) / 1000f);

        /// <summary>Into a state, rounded as the wire carries them; returns what the client will read.</summary>
        public Rates WriteTo(ref EntityState s)
        {
            s.SpeedRate = Short(SpeedRate);
            s.TurnX = Short(Turn.X); s.TurnY = Short(Turn.Y); s.TurnZ = Short(Turn.Z);
            return Of(s);
        }

        private static short Short(float v)
            => float.IsFinite(v) ? (short)Math.Clamp((int)MathF.Round(v * 1000f), -short.MaxValue, short.MaxValue) : (short)0;
    }

    /// <summary>Below this a thing has no heading to turn, m/s.</summary>
    private const float Still = 0.05f;

    /// <summary>
    /// The rates between two velocities <paramref name="seconds"/> apart: how the speed changed, and the angle
    /// the heading turned through. A speed and a turn, not a vector acceleration: going round a bend at a steady
    /// speed, the difference of two velocities reads as braking (the chord of the turn is shorter than the
    /// arc), 1 m/s² at 13 m/s on a 15 m radius over a fifth of a second.
    /// </summary>
    public static Rates RatesOf(Vector3 velocityBefore, Vector3 velocity, float seconds)
    {
        if (!(seconds > 1e-4f)) return Rates.None;
        float s0 = velocityBefore.Length(), s1 = velocity.Length();
        float speedRate = (s1 - s0) / seconds;
        if (!(s0 > Still && s1 > Still)) return new Rates(speedRate, Vector3.Zero);
        Vector3 u0 = velocityBefore / s0, u1 = velocity / s1;
        Vector3 cross = Vector3.Cross(u0, u1);
        float sin = cross.Length();
        if (!(sin > 1e-7f)) return new Rates(speedRate, Vector3.Zero);
        float angle = MathF.Atan2(sin, Vector3.Dot(u0, u1));
        return new Rates(speedRate, cross / sin * (angle / seconds));
    }

    /// <summary>
    /// Where a thing told of at (<paramref name="position"/>, <paramref name="velocity"/>,
    /// <paramref name="rotation"/>) is <paramref name="seconds"/> later, changing at <paramref name="rates"/>:
    /// the speed changing steadily (to a stop, not past it), the heading and the body turning steadily, so a
    /// bend at a steady speed is followed exactly. Nothing moves at zero seconds, bit for bit.
    /// </summary>
    public static (Vector3 Position, Vector3 Velocity, Quaternion Rotation) Predict(
        Vector3 position, Vector3 velocity, Quaternion rotation, in Rates rates, float seconds)
    {
        if (!(seconds > 0f)) return (position, velocity, rotation);
        if (seconds > MaxPredictSeconds) seconds = MaxPredictSeconds;

        float s = velocity.Length();
        if (s <= 0f) return (position, velocity, rotation);
        Vector3 u = velocity / s;

        // The speed, to a stop and no further.
        float t = seconds;
        if (rates.SpeedRate < 0f) t = MathF.Min(t, s / -rates.SpeedRate);
        float along = s * t + 0.5f * rates.SpeedRate * t * t;
        float speed = MathF.Max(0f, s + rates.SpeedRate * seconds);

        float turnRate = rates.Turn.Length();
        if (!(turnRate > 1e-6f))
            return (position + u * along, u * speed, rotation);

        // Round an arc: the chord goes at half the turn, and is shorter than the arc by sin(x)/x.
        Vector3 axis = rates.Turn / turnRate;
        float half = 0.5f * turnRate * t;
        float chord = half > 1e-5f ? along * MathF.Sin(half) / half : along;
        Vector3 mid = Vector3.Transform(u, Quaternion.CreateFromAxisAngle(axis, half));
        var turned = Quaternion.CreateFromAxisAngle(axis, turnRate * seconds);
        Vector3 now = Vector3.Transform(u, turned);
        return (position + mid * chord, now * speed, Quaternion.Normalize(turned * rotation));
    }

    /// <summary>
    /// How far off a far thing's position may be, metres: its distance times the tangent of
    /// <see cref="BearingToleranceDegrees"/> (26 cm at 150 m), and no more than <see cref="StepTolerance"/> of
    /// a tick's travel at <paramref name="speed"/> (5 cm at 15 m/s), nor less than a millimetre.
    /// </summary>
    public static float PositionTolerance(float distance, float speed)
        => MathF.Max(0.001f, MathF.Min(MathF.Max(distance, FullRateMetres) * MathF.Tan(BearingToleranceDegrees * MathF.PI / 180f),
                                       StepTolerance * speed * PhysicsConstants.FixedDeltaTime));

    /// <summary>
    /// Whether the client's prediction (<paramref name="predicted"/>) has strayed from the truth by more than
    /// an ear could tell at <paramref name="distance"/>: the bearing or a step, the Doppler, the engine's pitch
    /// through the speed, or the heading. Positions and velocities as the wire carries them.
    /// </summary>
    public static bool Strayed(in (Vector3 Position, Vector3 Velocity, Quaternion Rotation) predicted,
                               Vector3 position, Vector3 velocity, Quaternion rotation, float distance)
    {
        float speed = velocity.Length();
        float tolerance = PositionTolerance(distance, speed);
        if (Vector3.DistanceSquared(predicted.Position, position) > tolerance * tolerance) return true;
        if (Vector3.DistanceSquared(predicted.Velocity, velocity) > VelocityTolerance * VelocityTolerance) return true;
        if (MathF.Abs(predicted.Velocity.Length() - speed) > MathF.Max(SpeedFloor, SpeedTolerance * speed)) return true;
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(predicted.Rotation), Quaternion.Normalize(rotation)));
        float degrees = 2f * MathF.Acos(MathF.Min(1f, dot)) * (180f / MathF.PI);
        return degrees > TurnToleranceDegrees;
    }
}
