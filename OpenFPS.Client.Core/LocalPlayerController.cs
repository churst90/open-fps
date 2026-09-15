using System;
using System.Numerics;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.Core;

/// <summary>
/// Responsibility: Owns the high-level movement logic, air-time tracking, 
/// and physical event generation (footsteps, landing) for the local player.
/// </summary>
public class LocalPlayerController
{
    private readonly LocalPlayerState _state;
    
    private Vector3? _lastPosition = null;
    private float _accumulatedDistance = 0.0f;
    private int _stepCount = 0;
    private bool _wasInAir = false;
    private DateTime _lastFootstepTime = DateTime.MinValue;
    private DateTime _lastLandTime = DateTime.MinValue;

    /// <summary>Furthest a player could plausibly move under their own feet in ONE update, metres.
    /// Anything past this is a teleport, a spawn or a server correction — not a step.</summary>
    private const float MaxStrideStep = 1.0f;

    /// <summary>Slowest the player's OWN velocity can be and still be walking, m/s. Below this any
    /// movement in the position is something being done to them, not by them.</summary>
    private const float MinStrideSpeed = 0.5f;

    /// <summary>
    /// Forgets where the player was, for a spawn or a teleport.
    ///
    /// Without it the first update after a spawn measures a stride from wherever the player last
    /// stood — the lobby, the previous map — which is caught by the size test only because it
    /// happens to be large. Saying so explicitly is better than relying on the distance being big
    /// enough, and it costs one call at the one place that knows a teleport happened.
    /// </summary>
    public void Teleported()
    {
        _lastPosition = null;
        _accumulatedDistance = 0f;
        _wasInAir = false;
    }

    public event Action<Vector3, string, string>? OnStepTriggered; // Position, Material, Variant
    public event Action<Vector3, string, string>? OnLandTriggered;

    public LocalPlayerController(LocalPlayerState state)
    {
        _state = state;
    }

    public void Update(Vector3 newPosition, Vector3 velocity)
    {
        // Grounded based on physics state
        bool isGrounded = _state.IsGrounded; 

        if (!isGrounded) 
        {
            _wasInAir = true;
        }

        if (isGrounded && _wasInAir)
        {
            // We just landed!
            // We allow landing sounds even if material is "None" as a fallback to Generic
            if ((DateTime.Now - _lastLandTime).TotalMilliseconds > 500)
            {
                string mat = _state.CurrentMaterial == "None" ? "Generic" : _state.CurrentMaterial;
                OnLandTriggered?.Invoke(newPosition, mat, _state.CurrentVariant);
                _lastLandTime = DateTime.Now;
                _accumulatedDistance = 0.0f; // Reset stride on landing
            }
            _wasInAir = false;
        }

        if (_lastPosition.HasValue)
        {
            // Only accumulate distance for footsteps if we are actually grounded
            if (isGrounded)
            {
                Vector3 flatMove = new Vector3(newPosition.X - _lastPosition.Value.X, 0, newPosition.Z - _lastPosition.Value.Z);
                float moveLen = flatMove.Length();

                // A stride is something a person did. A jump in position is not.
                //
                // The accumulator cannot tell walking from being MOVED: spawning, a server
                // correction, a teleport. Arriving on a map, the client's predicted position and the
                // server's authoritative one reconcile over several frames, and every correction was
                // banked as distance walked — so a player who had not touched a key heard a burst of
                // footsteps on landing.
                //
                // Judged as a distance per update rather than a speed, deliberately. A speed needs a
                // delta time, and this is called at whatever rate its caller manages — including, in
                // tests, as fast as a loop will go — so a wall clock makes the rule depend on how
                // fast the game happens to be running. A metre in one update is past anything a
                // stride explains at any sane rate: a sprinter at six metres a second covers a fifth
                // of that between frames.
                // ...and the second half of the same idea: a stride is something a person did ON
                // PURPOSE, so ask the player's own VELOCITY whether they were walking at all.
                //
                // The size test above catches a teleport, which is one big jump. It does not catch a
                // RECONCILIATION, which is a run of small ones: arriving on a map, the predicted
                // position and the server's authoritative one converge over several frames in steps
                // of a few centimetres each — every one of them under a metre, every one of them
                // "plausible", and together more than enough to bank a stride. Heard as a few
                // footsteps on being dropped onto the map, from a player who has not touched a key,
                // dying away as the reconciliation settles.
                //
                // Velocity is the thing that tells them apart, and it was already being passed in
                // and thrown away. A correction moves you without your legs; standing still, the
                // predicted velocity is zero however far the server drags you.
                float ownSpeed = new Vector2(velocity.X, velocity.Z).Length();
                bool walking = ownSpeed > MinStrideSpeed;
                bool plausibleStride = moveLen <= MaxStrideStep;
                if (!plausibleStride || !walking) _accumulatedDistance = 0f;
                else if (moveLen > 0.001f) _accumulatedDistance += moveLen;
            }
            else
            {
                _accumulatedDistance = 0; // Don't bank up footsteps while in mid-air
            }
            
            if (_accumulatedDistance > 1.0f) _accumulatedDistance = 1.0f; 
        }
        _lastPosition = newPosition;

        if (isGrounded && _accumulatedDistance >= 0.5f)
        {
            if ((DateTime.Now - _lastFootstepTime).TotalMilliseconds > 200)
            {
                _stepCount++;
                float lateralOffset = (_stepCount % 2 == 0) ? 0.15f : -0.15f; 
                Vector3 rightVector = Vector3.Transform(Vector3.UnitX, _state.Rotation);
                
                Vector3 stepPos = newPosition + (rightVector * lateralOffset);
                stepPos.Y = newPosition.Y; 

                string mat = _state.CurrentMaterial == "None" ? "Generic" : _state.CurrentMaterial;
                OnStepTriggered?.Invoke(stepPos, mat, _state.CurrentVariant);
                _lastFootstepTime = DateTime.Now;
            }
            // Always consume the distance so we don't spam footsteps when the timer expires while stationary
            _accumulatedDistance %= 0.5f; 
        }
    }
}
