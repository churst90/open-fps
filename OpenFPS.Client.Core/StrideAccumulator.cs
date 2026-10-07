using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>Which way a footfall went: on the level, up a step, or down one.</summary>
public enum StepSlope { Level, Up, Down }

/// <summary>
/// When a body walking on the ground puts a foot down, and when a body that was in the air lands: the
/// one set of rules for the local player (predicted) and remote bodies (interpolated), each learned
/// from a fault. A stride is something a body did, not something done to it: <see cref="MaxStrideStep"/>
/// catches a teleport and <see cref="MinStrideSpeed"/> a reconciliation (nine metres of phantom
/// walking on arriving at a map). Passengers are NOT silenced here: the server gives an occupant its
/// vehicle's velocity, so each caller must keep riders out (at sixty miles an hour, a machine gun).
/// Distance is judged per update, never with a clock: the caller's rate is not fixed.
/// See docs/COMMON_NOTES.md, "Walking".
/// </summary>
public sealed class StrideAccumulator
{
    /// <summary>Hip height on a 1.8 m body, metres: the one number the gait is built on (a leg is a
    /// pendulum).</summary>
    public const float LegLengthMetres = 0.95f;

    /// <summary>Slowest step a body takes while it is still moving under its own feet, metres.
    /// A floor under the law below, so a body creeping at nothing does not step for ever.</summary>
    public const float MinStepLength = 0.3f;

    /// <summary>
    /// How far a body travels between footfalls at a given speed, metres: half a stride by Alexander's
    /// dynamic similarity, stride / L = 2.3 (v² / gL)^0.3. At 4.5 m/s (W) that is 1.38 m, 3.3 a second;
    /// at 7.2 m/s (Shift) 1.82 m, 4.0 a second. A fixed half metre was nine footfalls a second at the
    /// walk: insects running. See docs/COMMON_NOTES.md, "Walking".
    /// </summary>
    public static float StepLength(float speedMps)
    {
        float v = MathF.Max(0.15f, speedMps);
        float froude = v * v / (9.81f * LegLengthMetres);
        float stride = 2.3f * MathF.Pow(froude, 0.3f) * LegLengthMetres;
        return MathF.Max(MinStepLength, 0.5f * stride);
    }

    /// <summary>Furthest a body could plausibly move under its own feet in ONE update, metres.
    /// Anything past this is a teleport, a spawn or a server correction — not a step.</summary>
    public const float MaxStrideStep = 1.0f;

    /// <summary>Slowest a body's OWN velocity can be and still be walking, m/s. Below this, any
    /// movement in its position is something being done to it, not by it.</summary>
    public const float MinStrideSpeed = 0.5f;

    /// <summary>Slow enough that the body has stopped and its feet are together again, m/s. A latch
    /// with <see cref="MinStrideSpeed"/>, not one threshold: starting to walk puts a foot down, and a
    /// remote body's velocity sitting on the threshold would start a new walk at every crossing.</summary>
    public const float StoppedSpeed = MinStrideSpeed * 0.5f;

    /// <summary>How far to either side of the body's centre a foot goes down, metres. Feet alternate,
    /// so a walker is two sound sources a third of a metre apart rather than one down the middle.</summary>
    public const float StepWidth = 0.15f;

    /// <summary>How soon after landing another landing may sound, seconds.</summary>
    public const double MinSecondsBetweenLandings = 0.5;

    /// <summary>
    /// How fast a body must be falling for arriving to be a landing, m/s: a drop of about 7 cm. Without
    /// it a blip in the ground (a map still streaming, a probe straddling two surfaces, a correction
    /// across a lip) banged as a landing, five in two seconds at the city spawn. Speed, not time in the
    /// air, which would need a clock. A real fall has dropped more than a StepHeight and arrives at
    /// 3.5 m/s, so no real landing is refused.
    /// </summary>
    public const float MinLandingSpeed = 1.5f;

    /// <summary>How far a body must have climbed or dropped since its last footfall for this one to be a
    /// step up or down, metres: over a kerb (12 cm on the city), under the lowest riser built (15 cm).</summary>
    public const float MinStairRise = 0.14f;

    /// <summary>A change of height in one update this big is the body arriving on a tread, metres.
    /// The movement engine moves a body onto a step in one go, so on a flight the height changes in
    /// jumps of a riser and is otherwise still.</summary>
    public const float TreadJump = 0.05f;

    /// <summary>
    /// How much further than its step a footfall due between treads may wait for the next tread, metres:
    /// a little over one going (32 cm on the city). Past it the flight has ended and the foot goes down
    /// where it is.
    /// </summary>
    public const float TreadWaitMetres = 0.35f;

    /// <summary>
    /// How many treads a footfall takes on a flight: <see cref="StepLength"/> over the going, rounded,
    /// one or two (a leg spans two risers at a run, not three). At the game's walk, two at a time; the
    /// foot always lands on a tread (docs/GEOMETRY.md stage 2).
    /// </summary>
    public static int TreadsPerStep(float speedMps, float goingMetres)
        => goingMetres <= 0f ? 1 : Math.Clamp((int)MathF.Round(StepLength(speedMps) / goingMetres, MidpointRounding.AwayFromZero), 1, 2);

    /// <summary>Furthest apart two treads can be and be a flight's, metres along the way: past this a
    /// rise is a kerb or a doorstep on its own, walked over at the walk's own cadence.</summary>
    public const float FlightGoingMetres = 0.6f;

    /// <summary>How fast a foot meets the floor, m/s: walking on the level, going up a stair, coming down one.</summary>
    public const float LevelFootMps = 0.6f, UpFootMps = 0.4f, DownFootMps = 1.0f;

    /// <summary>
    /// How loud a step is against one on the level: 20 log of the foot's speed ratio. Up, the ball of
    /// the foot is put down with the weight still behind (0.4 m/s against 0.6); down, the heel drops with
    /// the body falling (1.0 m/s). Estimates of a controlled stair gait, not measurements: -3.5 dB and
    /// +4.4 dB.
    /// </summary>
    public static float SlopeDb(StepSlope slope) => slope switch
    {
        StepSlope.Up => 20f * MathF.Log10(UpFootMps / LevelFootMps),
        StepSlope.Down => 20f * MathF.Log10(DownFootMps / LevelFootMps),
        _ => 0f,
    };

    /// <summary>How a step's take is pitched against a level one: the ball of the foot, a smaller and
    /// stiffer contact, a little higher; a heel dropped with the body's weight, a little lower.</summary>
    public static float SlopePitch(StepSlope slope) => slope switch
    {
        StepSlope.Up => 1.04f,
        StepSlope.Down => 0.96f,
        _ => 1f,
    };

    private float? _lastStepY;
    private int _treadsSinceStep;
    /// <summary>The floor the body last settled on, for when it arrives on the next tread; how far it has
    /// walked since; and the going between the last two treads (0 when they were not a flight's).</summary>
    private float? _treadRef;
    private float _sinceTread = float.MaxValue, _going;
    private Vector3? _lastPosition;
    private float _accumulatedDistance;
    private int _stepCount;
    private bool _wasInAir;
    private bool _feetMoving;
    private float _fastestFall;
    private double _lastLandAt = double.NegativeInfinity;

    /// <summary>What the body did this update, if anything. Both can be true at once: a body that
    /// lands and keeps running lands and then steps.</summary>
    public readonly record struct Footfall(bool Stepped, Vector3 StepPosition, bool Landed, StepSlope Slope = StepSlope.Level)
    {
        public bool Anything => Stepped || Landed;
    }

    /// <summary>Forgets where the body was, for a spawn, a teleport, or getting in and out of something,
    /// rather than relying on <see cref="MaxStrideStep"/> to catch the jump.</summary>
    public void Forget()
    {
        _lastPosition = null;
        _lastStepY = null;
        _treadsSinceStep = 0;
        _treadRef = null;
        _sinceTread = float.MaxValue;
        _going = 0f;
        _accumulatedDistance = 0f;
        _wasInAir = false;
        _feetMoving = false;
        _fastestFall = 0f;
    }

    /// <summary>Advances one update. <paramref name="velocity"/> is the body's own, which separates
    /// walking from being moved; <paramref name="facing"/> decides which side a foot goes down.</summary>
    public Footfall Update(Vector3 position, Vector3 velocity, bool isGrounded, Quaternion facing)
    {
        double now = AudioClock.Now;
        bool landed = false;

        if (!isGrounded)
        {
            _wasInAir = true;
            // The impact speed.
            if (velocity.Y < _fastestFall) _fastestFall = velocity.Y;
        }

        if (isGrounded && _wasInAir)
        {
            // Not so soon after the last that one fall is heard as two.
            if (_fastestFall <= -MinLandingSpeed && now - _lastLandAt > MinSecondsBetweenLandings)
            {
                landed = true;
                _lastLandAt = now;
                _accumulatedDistance = 0f; // A landing is not half a stride.
            }
            _wasInAir = false;
            _fastestFall = 0f;
        }

        float ownSpeed = new Vector2(velocity.X, velocity.Z).Length();
        bool walking = ownSpeed > MinStrideSpeed;

        // The first footfall of a walk is at its start, so each tap of a movement key (15 cm) is a
        // footstep. Only a body on the ground has stopped, and a landing takes the place of this
        // footfall. See docs/COMMON_NOTES.md, "Walking".
        if (isGrounded && ownSpeed < StoppedSpeed) _feetMoving = false;
        bool startedWalking = isGrounded && walking && !_feetMoving && !landed;
        if (isGrounded && walking) _feetMoving = true;

        // A jump in height on the ground, no bigger than a step, is arriving on a tread. Measured from
        // the floor last settled on: the engine's one-update lift of a whole StepHeight and the settle
        // after it are one tread, not two.
        bool treadNow = false;
        if (isGrounded)
        {
            if (_treadRef is float settled)
            {
                float rise = position.Y - settled;
                bool lifted = MathF.Abs(rise - PhysicsConstants.StepHeight) < 0.01f;
                if (!lifted)
                {
                    float dy = MathF.Abs(rise);
                    treadNow = dy >= TreadJump && dy <= PhysicsConstants.StepHeight + 0.05f;
                    _treadRef = position.Y;
                }
            }
            else _treadRef = position.Y;
        }
        else _treadRef = null;
        if (treadNow)
        {
            _treadsSinceStep++;
            // Two treads within a flight's going are a flight, and the distance between them (this
            // update's own way included) is its going.
            float movedNow = _lastPosition.HasValue
                ? new Vector2(position.X - _lastPosition.Value.X, position.Z - _lastPosition.Value.Z).Length() : 0f;
            float going = _sinceTread == float.MaxValue ? float.MaxValue : _sinceTread + movedNow;
            _going = going <= FlightGoingMetres ? going : 0f;
            _sinceTread = 0f;
        }

        // The foot's height is the lower of now and an update ago: during the engine's one-update
        // lift a footfall put the landing at the top 22 cm below it, and the last step of a flight
        // went down as a heel drop (2026-10-04, Selby House).
        float footY = isGrounded && _lastPosition.HasValue ? MathF.Min(position.Y, _lastPosition.Value.Y) : position.Y;

        if (_lastPosition.HasValue)
        {
            if (isGrounded)
            {
                var flat = new Vector3(position.X - _lastPosition.Value.X, 0, position.Z - _lastPosition.Value.Z);
                float moved = flat.Length();

                bool plausible = moved <= MaxStrideStep;

                if (!plausible || !walking) _accumulatedDistance = 0f;
                else if (moved > 0.001f) _accumulatedDistance += moved;
                if (plausible && !treadNow && _sinceTread < float.MaxValue) _sinceTread += moved;
            }
            else
            {
                _accumulatedDistance = 0f; // Nothing banks up while a body is off the ground.
            }

            float step = StepLength(ownSpeed);
            float cap = 2f * step + TreadWaitMetres;
            if (_accumulatedDistance > cap) _accumulatedDistance = cap;
        }
        _lastPosition = position;

        bool stepped = false;
        var stepPosition = position;

        // Deliberately no cadence cap: it eats footfalls, and StepLength keeps the cadence under four a
        // second on its own.
        bool due = startedWalking || (isGrounded && _accumulatedDistance >= StepLength(ownSpeed));
        // On a flight a foot goes down on a tread, every TreadsPerStep treads; off it (a landing, the
        // floor at the top) the walk's own cadence comes back.
        bool onFlight = _going > 0f && _sinceTread <= FlightGoingMetres;
        if (onFlight && !startedWalking)
            due = isGrounded && treadNow && _treadsSinceStep >= TreadsPerStep(ownSpeed, _going);
        // On a flight's first treads, before its going is known, a step due between treads waits for the
        // next, up to TreadWaitMetres. Only after two treads: one is a kerb or a doorstep.
        else if (due && !startedWalking && _treadsSinceStep >= 2 && !treadNow
                 && _accumulatedDistance < StepLength(ownSpeed) + TreadWaitMetres)
            due = false;

        var slope = StepSlope.Level;
        if (due)
        {
            stepped = true;
            // Up or down by how far the body climbed or dropped since its last footfall.
            if (_lastStepY is float before)
            {
                float rise = footY - before;
                slope = rise >= MinStairRise ? StepSlope.Up : rise <= -MinStairRise ? StepSlope.Down : StepSlope.Level;
            }
            _lastStepY = footY;
            _treadsSinceStep = 0;
            _stepCount++;
            float lateral = (_stepCount % 2 == 0) ? StepWidth : -StepWidth;
            var right = Vector3.Transform(Vector3.UnitX, facing);
            stepPosition = position + right * lateral;
            stepPosition.Y = position.Y;

            // The remainder over a step is kept, so a long walk's cadence is not quantised to the
            // update rate.
            if (startedWalking) _accumulatedDistance = 0f;
            else _accumulatedDistance %= StepLength(ownSpeed);
        }

        // A landing is a foot going down as well, and the next step's rise counts from it.
        if (landed && !stepped) _lastStepY = position.Y;
        return new Footfall(stepped, stepPosition, landed, slope);
    }
}
