using System;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>Which way a footfall went: on the level, up a step, or down one.</summary>
public enum StepSlope { Level, Up, Down }

/// <summary>
/// When a body walking on the ground puts a foot down, and when a body that was in the air lands.
///
/// There are two kinds of body that walk and only one kind of walking. The local player's position is
/// predicted here and corrected by the server; everybody else's arrives interpolated between two
/// snapshots. A footstep is a footstep either way, and the rules for what counts as one were every
/// one of them learned from a fault — so they live in ONE place rather than being written a second
/// time for remote bodies and drifting quietly out of agreement with the first.
///
/// What the rules are, and what each is for:
///
/// <b>A stride is something a body did, not something done to it.</b> The accumulator cannot tell
/// walking from being MOVED — spawning, a server correction, a teleport, riding in a car — so it
/// asks two questions rather than one. <see cref="MaxStrideStep"/> catches a jump: a metre in one
/// update is past anything a stride explains at any sane rate, since a sprinter at six metres a
/// second covers a fifth of that between frames. That catches a teleport, which is one big jump, and
/// misses a RECONCILIATION, which is a run of small ones — arriving on a map, predicted and
/// authoritative positions converge in steps of a few centimetres, every one of them "plausible" and
/// together nine metres of phantom walking. <see cref="MinStrideSpeed"/> is what tells those apart:
/// a correction moves you without your legs, so the body's OWN velocity is zero throughout.
///
/// A passenger is NOT silent by that rule, though it was once written that they were. The server
/// gives an occupant the velocity of what they are in (OccupancySystem), which is where they are
/// going but not what their legs are doing.
/// Each caller keeps riders away from here instead: the local player by RidingEntityId, everybody
/// else by the definition's (OtherBodies). Feeding a vehicle's motion to a stride generator is a
/// footstep every stride of ROAD: at sixty miles an hour, a machine gun.
///
/// <b>A step is as long as the speed makes it.</b> How far a body goes between footfalls is not a
/// constant — it is <see cref="StepLength"/>, from the body's leg length and how fast it is moving,
/// because a leg is a pendulum and a fast body takes longer steps rather than more of them.
///
/// <b>A walk's first footfall is at the start of the walk.</b> Distance since the last footfall is
/// what spaces the ones after it, but it cannot place the FIRST: counted from a standstill it puts
/// the opening footfall half a stride in, and a body cannot move half a metre without having already
/// put a foot down. So starting to move is itself a footfall and the distance counts from there.
///
/// The distance test is judged per UPDATE and not as a speed, deliberately. A speed needs a delta
/// time, and this is driven at whatever rate its caller manages — including, in tests, as fast as a
/// loop will go — so a wall clock would make the rule depend on how fast the game happens to run.
/// </summary>
public sealed class StrideAccumulator
{
    /// <summary>
    /// How long a leg is, metres — hip height on a 1.8 m body. It is the ONE number the gait is
    /// built on, because a leg is a pendulum and a pendulum's period is its length.
    /// </summary>
    public const float LegLengthMetres = 0.95f;

    /// <summary>Slowest step a body takes while it is still moving under its own feet, metres.
    /// A floor under the law below, so a body creeping at nothing does not step for ever.</summary>
    public const float MinStepLength = 0.3f;

    /// <summary>
    /// How far a body travels between footfalls at a given speed, metres.
    ///
    /// Not a constant. The game walks at 4.5 m/s, so a fixed half metre a footfall would be **nine
    /// footfalls a second**, and a sprint fourteen: it sounds like insects running. No animal does
    /// that. A human
    /// tops out near four a second however hard they are trying, because a leg has to swing forward
    /// and a leg is a pendulum: past a certain rate you cannot get it round any faster, so everything
    /// above that speed is bought with a LONGER STEP instead.
    ///
    /// That is what this is. Dynamic similarity — Alexander's relation, the one that puts a mouse, a
    /// human and an elephant on a single curve — says relative stride length goes as the 0.3 power of
    /// the Froude number:
    ///
    ///     stride / L  =  2.3 (v² / gL)^0.3
    ///
    /// with L the leg length and a footfall half a stride. Nothing in it was chosen to make the game
    /// sound right; it is the same curve for every legged thing that has been filmed, and what falls
    /// out of it is:
    ///
    ///     1.4 m/s (a real walk)     0.69 m per step    2.0 a second
    ///     4.5 m/s (this game's W)   1.38 m per step    3.3 a second
    ///     7.2 m/s (shift held)      1.82 m per step    4.0 a second
    ///
    /// — a steady cadence that barely rises with speed and a stride that does most of the work, which
    /// is exactly what a run sounds like against a walk. The difference a listener hears between the
    /// two is then the LENGTH of the step and how hard it lands, not a faster machine gun.
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

    /// <summary>Slow enough that the body has stopped and its feet are together again, m/s.
    ///
    /// Starting to walk puts a foot down (see <see cref="Update"/>), so "is it walking" has to be a
    /// latch and not a comparison: a velocity sitting exactly on <see cref="MinStrideSpeed"/> — which
    /// a remote body's, arriving a tick at a time through the interpolator, can do — would otherwise
    /// cross it back and forth and start a new walk on every crossing. A body is walking above the one
    /// number and standing below the other, and between them it is doing whatever it was doing
    /// before.</summary>
    public const float StoppedSpeed = MinStrideSpeed * 0.5f;

    /// <summary>How far to either side of the body's centre a foot goes down, metres. Feet alternate,
    /// so a walker is two sound sources a third of a metre apart rather than one down the middle.</summary>
    public const float StepWidth = 0.15f;

    /// <summary>How soon after landing another landing may sound, seconds.</summary>
    public const double MinSecondsBetweenLandings = 0.5;

    /// <summary>
    /// How fast a body must be falling for arriving to be a LANDING, m/s.
    ///
    /// You cannot land from a fall you were not falling in. A blip in the ground underfoot is not a
    /// fall — and blips happen: a map still streaming in has no floor yet, a probe straddling the
    /// edge of two surfaces flips between them, a server correction moves you across a lip. Without
    /// this threshold every one of those sounds a landing, a heavy sound gated to two a second, so it
    /// comes out as an irregular bang: five landings in two seconds at the city's spawn point, before
    /// the player has taken a step.
    ///
    /// SPEED rather than time in the air, and that matters. Time needs a clock, and this is driven at
    /// whatever rate its caller manages — the same reason the distance rule is judged per update
    /// rather than as a speed. How fast you were going when you arrived needs no clock at all: it is
    /// in the velocity the caller is already passing, and it is also the thing that decides whether
    /// an arrival is a landing in the first place.
    ///
    /// A metre and a half a second is a drop of about seven centimetres — under the smallest step
    /// anybody would call a fall. Anything the movement engine genuinely puts in the air has dropped
    /// further than a full StepHeight first (see SharedMovementEngine's step down), which is 3.5 m/s
    /// by the time it arrives, so this refuses no real landing.
    /// </summary>
    public const float MinLandingSpeed = 1.5f;

    /// <summary>
    /// How far a body must have climbed or dropped since its last footfall for this one to be a step
    /// up or down, metres. Over a kerb (12 cm on the city) and under the lowest riser anybody builds
    /// (15 cm): a kerb is walked over, a stair is climbed.
    /// </summary>
    public const float MinStairRise = 0.14f;

    /// <summary>A change of height in one update this big is the body arriving on a tread, metres.
    /// The movement engine moves a body onto a step in one go, so on a flight the height changes in
    /// jumps of a riser and is otherwise still.</summary>
    public const float TreadJump = 0.05f;

    /// <summary>
    /// How much further than its step a body on a flight may go waiting for a tread to put its foot
    /// on, metres: a little over one going (32 cm on the city). A foot does not land in the middle of
    /// a riser, so on stairs a footfall that falls due between treads waits for the next one; past
    /// this it is not on stairs any more (the flight has ended in a landing) and goes down where it is.
    /// </summary>
    public const float TreadWaitMetres = 0.35f;

    /// <summary>How fast a foot meets the floor, m/s: walking on the level, going up a stair, coming down one.</summary>
    public const float LevelFootMps = 0.6f, UpFootMps = 0.4f, DownFootMps = 1.0f;

    /// <summary>
    /// How loud a step is against a step on the level, from how fast the foot meets the floor: the
    /// impact's energy goes as the square of that speed, so its level as 20 log of the ratio.
    ///
    /// Going UP, the ball of the foot is put down on the tread with the knee bent and the weight still
    /// on the other leg — the leg is about to lift the body, not land it — so the foot arrives slower
    /// than a heel strike on the flat: about 0.4 m/s against 0.6. Going DOWN, the heel drops onto the
    /// tread below with the body already falling through the riser, and arrives faster: about 1.0 m/s.
    /// Those speeds are estimates of a controlled stair gait, not measurements; what they give is a
    /// toe-first step three and a half decibels under a level one and a heel drop four and a half over.
    /// </summary>
    public static float SlopeDb(StepSlope slope) => slope switch
    {
        StepSlope.Up => 20f * MathF.Log10(UpFootMps / LevelFootMps),
        StepSlope.Down => 20f * MathF.Log10(DownFootMps / LevelFootMps),
        _ => 0f,
    };

    /// <summary>
    /// How a step's take is pitched against a step on the level. The ball of the foot is a smaller,
    /// stiffer contact than a heel and rings a little higher; a heel dropped with the body's weight
    /// behind it puts more mass on the floor and sounds a little lower.
    /// </summary>
    public static float SlopePitch(StepSlope slope) => slope switch
    {
        StepSlope.Up => 1.04f,
        StepSlope.Down => 0.96f,
        _ => 1f,
    };

    private float? _lastStepY;
    private int _treadsSinceStep;
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

    /// <summary>
    /// Forgets where the body was, for a spawn, a teleport, or getting in and out of something.
    ///
    /// Without it the first update after a spawn measures a stride from wherever the body last stood
    /// — the lobby, the previous map — which the size test only catches because it happens to be
    /// large. Saying so explicitly is better than relying on the distance being big enough, and it
    /// costs one call at the one place that knows a teleport happened.
    /// </summary>
    public void Forget()
    {
        _lastPosition = null;
        _lastStepY = null;
        _treadsSinceStep = 0;
        _accumulatedDistance = 0f;
        _wasInAir = false;
        _feetMoving = false;
        _fastestFall = 0f;
    }

    /// <summary>
    /// Advances one update. <paramref name="velocity"/> is the body's OWN velocity — the thing that
    /// separates walking from being carried — and <paramref name="facing"/> decides which side of it
    /// this foot goes down.
    /// </summary>
    public Footfall Update(Vector3 position, Vector3 velocity, bool isGrounded, Quaternion facing)
    {
        double now = AudioClock.Now;
        bool landed = false;

        if (!isGrounded)
        {
            _wasInAir = true;
            // The fastest it was going DOWN while it was up there: the impact speed, which is the
            // whole of what makes an arrival a landing.
            if (velocity.Y < _fastestFall) _fastestFall = velocity.Y;
        }

        if (isGrounded && _wasInAir)
        {
            // Falling hard enough to have landed, and not so soon after the last landing that one
            // fall is being heard as two.
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

        // ── The first footfall of a walk is at the START of it ──────────────────────────────────
        //
        // A body standing still has both feet planted and its weight between them. It cannot begin to
        // move without lifting one and putting it down somewhere else, so the first footfall happens
        // when the walking starts — not half a stride into it, which is where counting distance from
        // a standstill puts it.
        //
        // Each tap of a movement key is a footstep. A tap moves the player for one 30 Hz tick at
        // 4.5 m/s, which is 15 cm, so counting distance from a standstill would need three or four
        // taps to bank a step and the first few would be silent. With the stride phased from the
        // start of the walk a tap is one footfall, and a longer press is that footfall and then one
        // every step length.
        //
        // Only a body on the GROUND can be said to have stopped: a run that ends in a jump has not put
        // its feet together, it has them in the air, so the latch is left alone until they are back
        // down and a landing does not read as a fresh start. And a landing IS a foot going down — the
        // one it lands on — so it takes the place of this footfall rather than sounding beside it.
        if (isGrounded && ownSpeed < StoppedSpeed) _feetMoving = false;
        bool startedWalking = isGrounded && walking && !_feetMoving && !landed;
        if (isGrounded && walking) _feetMoving = true;

        // A jump in height on the ground is the body arriving on a tread (see TreadJump). Not a jump
        // bigger than a step: that is a teleport or a correction, not a stair.
        float dy = _lastPosition.HasValue ? MathF.Abs(position.Y - _lastPosition.Value.Y) : 0f;
        bool treadNow = isGrounded && dy >= TreadJump && dy <= PhysicsConstants.StepHeight + 0.05f;
        if (treadNow) _treadsSinceStep++;

        // How high the foot is, for whether this footfall went up or down: the lower of where the body
        // is and where it was an update ago. The movement engine takes a step up by lifting the body
        // the whole StepHeight and lets the ground probe settle it onto the tread on the next update,
        // so for one update a body climbing stairs is 40 cm over the tread it came from. A footfall on
        // that update measured from there put the next one, on the landing at the top, 22 cm BELOW it,
        // and the last step of a flight of 17.6 cm risers went down as a heel drop (2026-10-04, the
        // walk up Selby House). On the way down there is no such lift and the lower is where you are.
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

        // A step of ground is one footfall, and how long a step is comes from how fast the body is
        // going — see StepLength. There is deliberately no floor under the RATE.
        //
        // A cadence cap (say, five steps a second) silently eats footfalls. It is a rule about the
        // CLOCK standing in for a rule about the BODY, and the body's rule is the true one: a leg is
        // a pendulum, so a fast body takes longer steps rather than more of them, and the cadence
        // comes out under four a second on its own without anything watching a timer.
        bool due = startedWalking || (isGrounded && _accumulatedDistance >= StepLength(ownSpeed));
        // On a flight a foot goes down on a tread: a step that falls due between treads waits for the
        // next one, up to TreadWaitMetres further on. A flight is two treads or more since the last
        // footfall; one is a kerb or a doorstep, and a level walk past it keeps its own cadence.
        if (due && !startedWalking && _treadsSinceStep >= 2 && !treadNow
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

            // A walk that starts here has walked nothing yet; one a step in has walked whatever is
            // over that step, and that remainder is what keeps a long walk's cadence honest instead
            // of quantising it to the update rate.
            if (startedWalking) _accumulatedDistance = 0f;
            else _accumulatedDistance %= StepLength(ownSpeed);
        }

        // A landing is a foot going down as well, and the next step's rise counts from it.
        if (landed && !stepped) _lastStepY = position.Y;
        return new Footfall(stepped, stepPosition, landed, slope);
    }
}
