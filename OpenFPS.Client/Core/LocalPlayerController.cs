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
                if (moveLen > 0.001f) _accumulatedDistance += moveLen;
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
