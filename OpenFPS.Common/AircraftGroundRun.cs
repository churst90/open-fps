using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// An aeroplane from touchdown to lift-off on one runway, as kinematics from the type's own figures
/// (GroundRunSpec):
///
///   1. the landing roll, touchdown speed to taxi speed at a steady deceleration;
///   2. taxiing on to the turn, far enough that the take-off roll back lifts off where the wheels
///      touched, so the climb-out is the approach reversed;
///   3. a 180-degree turn at a tenth of a g, swinging out to one side and back, as a runway backtrack
///      is flown;
///   4. back to the hold point if the turn was further out than the take-off needs, and a stop;
///   5. the hold, then the take-off roll at the type's mean acceleration.
///
/// The ground is the touchdown point's height; the line is the approach's direction.
/// </summary>
public sealed class AircraftGroundRun
{
    public enum Phase { Rollout, Taxi, Hold, TakeoffRoll, Done }

    public GroundRunSpec Spec { get; }
    public Phase State { get; private set; } = Phase.Rollout;
    public Vector3 Position { get; private set; }
    /// <summary>Radians, the way the nose points (x east, z north: atan2(x, z)).</summary>
    public float Heading { get; private set; }
    public float Speed { get; private set; }
    public Vector3 Velocity { get; private set; }
    /// <summary>The ground path, a point every half metre, from touchdown round to lift-off.</summary>
    public IReadOnlyList<Vector3> Path => _path;

    private readonly List<Vector3> _path = new();
    private readonly List<float> _along = new();
    private readonly float _holdSeconds;
    /// <summary>Where on the path the turn's slow section starts and ends, the hold point, and the end.</summary>
    private readonly float _turnFrom, _turnTo, _holdAt, _end;
    private float _s, _held;

    private const float Step = 0.5f;
    /// <summary>Taxi braking and pulling away, m/s²: a gentle tenth of a g.</summary>
    private const float TaxiAccel = 1f;

    /// <param name="touchdown">Where the wheels met the runway.</param>
    /// <param name="landingDirection">The way it was going, in the ground plane.</param>
    /// <param name="speed">How fast, m/s.</param>
    /// <param name="holdSeconds">How long it waits lined up before the take-off roll.</param>
    public AircraftGroundRun(GroundRunSpec spec, Vector3 touchdown, Vector3 landingDirection, float speed, float holdSeconds)
    {
        Spec = spec;
        _holdSeconds = MathF.Max(0f, holdSeconds);
        var dir = new Vector3(landingDirection.X, 0f, landingDirection.Z);
        dir = dir.LengthSquared() < 1e-6f ? Vector3.UnitZ : Vector3.Normalize(dir);
        var left = new Vector3(-dir.Z, 0f, dir.X);       // x east, z north: the left of (dx, dz) is (-dz, dx)
        float r = MathF.Max(2f, spec.TurnRadiusMetres);
        Speed = MathF.Max(0f, speed);

        // How far down the runway the landing roll ends, and how far the take-off roll needs.
        float landing = MathF.Max(0f, (Speed * Speed - spec.TaxiSpeedMps * spec.TaxiSpeedMps) / (2f * MathF.Max(0.1f, spec.LandingDecel)));
        float takeoff = spec.RotateSpeedMps * spec.RotateSpeedMps / (2f * MathF.Max(0.05f, spec.TakeoffAccel));
        float turnAt = MathF.Max(landing, takeoff);

        // 1-2: straight down the runway to the turn.
        AddLine(touchdown, dir, turnAt);
        // 3: out to the right by a radius over three radii of runway, round to the left, and back.
        float blend = 3f * r;
        _turnFrom = Length;
        var start = touchdown + dir * turnAt;
        for (float d = Step; d <= blend; d += Step)
        {
            float f = d / blend;
            Add(start + dir * d - left * (r * 0.5f * (1f - MathF.Cos(MathF.PI * f))));
        }
        var centre = start + dir * blend;
        for (float a = Step / r; a <= MathF.PI; a += Step / r)
            Add(centre - left * (r * MathF.Cos(a)) + dir * (r * MathF.Sin(a)));
        for (float d = Step; d <= blend; d += Step)
        {
            float f = d / blend;
            Add(centre - dir * d + left * (r * 0.5f * (1f + MathF.Cos(MathF.PI * f))));
        }
        _turnTo = Length;
        // 4: back to where the take-off roll starts.
        AddLine(start, -dir, turnAt - takeoff);
        _holdAt = Length;
        // 5: the take-off roll to the touchdown point.
        AddLine(touchdown + dir * takeoff, -dir, takeoff);
        _end = Length;

        Position = touchdown;
        Heading = MathF.Atan2(dir.X, dir.Z);
        Velocity = dir * Speed;
    }

    private float Length => _along.Count == 0 ? 0f : _along[^1];

    private void Add(Vector3 p)
    {
        if (_path.Count == 0) { _path.Add(p); _along.Add(0f); return; }
        float d = Vector3.Distance(new Vector3(p.X, 0f, p.Z), new Vector3(_path[^1].X, 0f, _path[^1].Z));
        if (d < 1e-4f) return;
        _path.Add(p);
        _along.Add(_along[^1] + d);
    }

    private void AddLine(Vector3 from, Vector3 dir, float metres)
    {
        if (_path.Count == 0) Add(from);
        for (float d = Step; d <= metres; d += Step) Add(from + dir * d);
        if (metres > 0f) Add(from + dir * metres);
    }

    public void Update(float dt)
    {
        if (State == Phase.Done || dt <= 0f) return;
        switch (State)
        {
            case Phase.Rollout:
                Speed = MathF.Max(Spec.TaxiSpeedMps, Speed - Spec.LandingDecel * dt);
                if (Speed <= Spec.TaxiSpeedMps + 1e-3f) State = Phase.Taxi;
                break;
            case Phase.Taxi:
            {
                // Taxi speed, slowed for the turn ahead and to a stop at the hold point.
                float want = Spec.TaxiSpeedMps;
                float toTurn = _turnFrom - _s;
                float vt = Spec.TurnSpeedMps;
                if (_s < _turnTo) want = MathF.Min(want, toTurn <= 0f ? vt : MathF.Sqrt(vt * vt + 2f * TaxiAccel * toTurn));
                float toHold = _holdAt - _s;
                want = MathF.Min(want, MathF.Sqrt(2f * TaxiAccel * MathF.Max(0f, toHold)));
                Speed = want > Speed ? MathF.Min(want, Speed + TaxiAccel * dt) : MathF.Max(want, Speed - 2f * TaxiAccel * dt);
                if (toHold <= 0.05f && Speed < 0.2f) { Speed = 0f; State = Phase.Hold; _held = 0f; }
                break;
            }
            case Phase.Hold:
                Speed = 0f;
                _held += dt;
                if (_held >= _holdSeconds) State = Phase.TakeoffRoll;
                break;
            case Phase.TakeoffRoll:
                Speed += Spec.TakeoffAccel * dt;
                break;
        }
        _s = MathF.Min(_end, _s + Speed * dt);
        if (State == Phase.Taxi && _s >= _holdAt - 0.05f && Speed < 0.5f) { _s = _holdAt; Speed = 0f; State = Phase.Hold; _held = 0f; }
        if (State == Phase.TakeoffRoll && _s >= _end - 1e-3f) State = Phase.Done;
        Place();
    }

    private void Place()
    {
        Position = PointAt(_s);
        // The chord across half a metre either side, so the heading turns smoothly through a bend.
        var d = PointAt(MathF.Min(_end, _s + 0.5f)) - PointAt(MathF.Max(0f, _s - 0.5f));
        d.Y = 0f;
        if (d.LengthSquared() > 1e-8f)
        {
            d = Vector3.Normalize(d);
            Heading = MathF.Atan2(d.X, d.Z);
            Velocity = d * Speed;
        }
    }

    private Vector3 PointAt(float s)
    {
        int i = _along.BinarySearch(s);
        if (i < 0) i = ~i;
        i = Math.Clamp(i, 1, _path.Count - 1);
        float a0 = _along[i - 1], a1 = _along[i];
        float f = a1 > a0 ? (s - a0) / (a1 - a0) : 0f;
        return Vector3.Lerp(_path[i - 1], _path[i], Math.Clamp(f, 0f, 1f));
    }

    /// <summary>How far along the ground path, metres.</summary>
    public float Along => _s;
}
