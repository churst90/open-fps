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
    /// <summary>How far a body walks between footfalls, metres.</summary>
    public const float StrideLength = 0.5f;

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

    private Vector3? _lastPosition;
    private float _accumulatedDistance;
    private int _stepCount;
    private bool _wasInAir;
    private bool _feetMoving;
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

        if (!isGrounded) _wasInAir = true;

        if (isGrounded && _wasInAir)
        {
            if (now - _lastLandAt > MinSecondsBetweenLandings)
            {
                landed = true;
                _lastLandAt = now;
                _accumulatedDistance = 0f; // A landing is not half a stride.
            }
            _wasInAir = false;
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

            if (_accumulatedDistance > 2f * StrideLength) _accumulatedDistance = 2f * StrideLength;
        }
        _lastPosition = position;

        bool stepped = false;
        var stepPosition = position;

        // Half a metre of walking is one footfall, and there is deliberately no floor under the rate.
        //
        // There used to be one — no more than five steps a second — and it was quietly eating most of
        // them: a walk is 4.5 m/s, which is a footfall every 111 ms, so more than half of every walk
        // was silent, and a run lost seven in ten. A cadence cap is a rule about the CLOCK standing in
        // for a rule about DISTANCE, and the distance rule is the true one: a body that is not moving
        // banks nothing, and a body that is being moved rather than walking is refused by its own
        // velocity long before any timer would have caught it.
        if (startedWalking || (isGrounded && _accumulatedDistance >= StrideLength))
        {
            stepped = true;
            _stepCount++;
            float lateral = (_stepCount % 2 == 0) ? StepWidth : -StepWidth;
            var right = Vector3.Transform(Vector3.UnitX, facing);
            stepPosition = position + right * lateral;
            stepPosition.Y = position.Y;

            // A walk that starts here has walked nothing yet; one half a metre in has walked whatever
            // is over the half metre, and that remainder is what keeps a long walk's cadence honest
            // instead of quantising it to the update rate.
            if (startedWalking) _accumulatedDistance = 0f;
            else _accumulatedDistance %= StrideLength;
        }

        return new Footfall(stepped, stepPosition, landed);
    }
}
