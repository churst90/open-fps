using System;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

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
/// That second rule is also why a passenger is silent for free. The server zeroes an occupant's
/// velocity and its movement system leaves their body alone — the seat owns where they are — so a
/// rider is a body being carried at zero velocity, which is the same thing as a body being dragged
/// by a correction, and it needs no rule of its own. Feeding a vehicle's motion to a stride
/// generator would otherwise be a footstep every half metre of ROAD: at sixty miles an hour, a
/// machine gun.
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
    /// This used to be HALF A METRE, FULL STOP — and that one constant is what a listener heard as
    /// *"sounds like cockroaches running"*. The game walks at 4.5 m/s, so half a metre a footfall is
    /// **nine footfalls a second**, and a sprint is fourteen. No animal has ever done that. A human
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
    /// edge of two surfaces flips between them, a server correction moves you across a lip. Every one
    /// of those used to sound a landing, which is a heavy sound gated to two a second, so it came out
    /// as an irregular bang. Measured on arrival at the city map: five landings in two seconds, all
    /// at the spawn point, before the player had taken a step.
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

    private Vector3? _lastPosition;
    private float _accumulatedDistance;
    private int _stepCount;
    private bool _wasInAir;
    private bool _feetMoving;
    private float _fastestFall;
    private double _lastLandAt = double.NegativeInfinity;

    /// <summary>What the body did this update, if anything. Both can be true at once: a body that
    /// lands and keeps running lands and then steps.</summary>
    public readonly record struct Footfall(bool Stepped, Vector3 StepPosition, bool Landed)
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
        // Reported as "each W A S D press should be a footstep, not every two or three presses", and
        // that is exactly the arithmetic: a tap moves the player for one 30 Hz tick at 4.5 m/s, which
        // is 15 cm, so three or four taps were needed to bank the half metre and the first two or
        // three were silent. With the stride phased from the start of the walk a tap is one footfall,
        // a longer press is that footfall and then one every half metre, and nothing about the walk
        // itself has changed.
        //
        // Only a body on the GROUND can be said to have stopped: a run that ends in a jump has not put
        // its feet together, it has them in the air, so the latch is left alone until they are back
        // down and a landing does not read as a fresh start. And a landing IS a foot going down — the
        // one it lands on — so it takes the place of this footfall rather than sounding beside it.
        if (isGrounded && ownSpeed < StoppedSpeed) _feetMoving = false;
        bool startedWalking = isGrounded && walking && !_feetMoving && !landed;
        if (isGrounded && walking) _feetMoving = true;

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
            if (_accumulatedDistance > 2f * step) _accumulatedDistance = 2f * step;
        }
        _lastPosition = position;

        bool stepped = false;
        var stepPosition = position;

        // A step of ground is one footfall, and how long a step is comes from how fast the body is
        // going — see StepLength. There is deliberately no floor under the RATE.
        //
        // There used to be one — no more than five steps a second — and it was quietly eating most of
        // them. Taking it out was right; what was wrong was the half-metre constant beside it, which
        // made a walk nine footfalls a second and a run fourteen. A cadence cap is a rule about the
        // CLOCK standing in for a rule about the BODY, and the body's rule is the true one: a leg is
        // a pendulum, so a fast body takes longer steps rather than more of them, and the cadence
        // comes out under four a second on its own without anything watching a timer.
        if (startedWalking || (isGrounded && _accumulatedDistance >= StepLength(ownSpeed)))
        {
            stepped = true;
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

        return new Footfall(stepped, stepPosition, landed);
    }
}
