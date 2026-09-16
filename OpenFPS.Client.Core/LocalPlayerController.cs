using System;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.Core;

/// <summary>
/// Responsibility: the local player's physical events — footsteps and landings — from the position
/// prediction already owns.
///
/// What a stride IS lives in <see cref="StrideAccumulator"/>, which this shares with every other body
/// on the map (see <see cref="OtherBodies"/>). All that is local about the local player is where the
/// three facts come from: the ground state and the material underfoot are the ones the client's own
/// physics worked out this frame, rather than anything that had to travel.
/// </summary>
public class LocalPlayerController
{
    private readonly LocalPlayerState _state;
    private readonly StrideAccumulator _stride = new();
    private readonly Breathing _lungs = new();
    private double _lastUpdateAt = -1;

    /// <summary>How far above the player's feet the breath comes from, metres.</summary>
    private const float HeadHeight = 1.7f;

    /// <summary>Forgets where the player was, for a spawn, a teleport, or getting in or out of a seat.</summary>
    public void Teleported()
    {
        _stride.Forget();
        _lastUpdateAt = -1;
    }

    public event Action<Vector3, string, string>? OnStepTriggered; // Position, Material, Variant
    public event Action<Vector3, string, string>? OnLandTriggered;

    /// <summary>The player took a breath. Getting in and out of a seat does NOT reset this the way it
    /// resets the stride: a driver who sprinted to the car is still out of breath in it.</summary>
    public event Action<Vector3, Breath>? OnBreath;

    /// <summary>How hard the player is working, 0 to 1. For a spoken readout, and for tests.</summary>
    public float Exertion => _lungs.Exertion;

    public LocalPlayerController(LocalPlayerState state)
    {
        _state = state;
    }

    public void Update(Vector3 newPosition, Vector3 velocity)
    {
        Breathe(newPosition, velocity);

        var fall = _stride.Update(newPosition, velocity, _state.IsGrounded, _state.Rotation);
        if (!fall.Anything) return;

        // "None" is what the ground probe says when it found no floor to name; a landing is still a
        // landing on it, so it falls back rather than going silent.
        string mat = _state.CurrentMaterial == "None" ? "Generic" : _state.CurrentMaterial;

        if (fall.Landed) OnLandTriggered?.Invoke(newPosition, mat, _state.CurrentVariant);
        if (fall.Stepped) OnStepTriggered?.Invoke(fall.StepPosition, mat, _state.CurrentVariant);
    }

    /// <summary>
    /// Lungs run on elapsed time, not on frames — this is called at whatever rate the renderer
    /// manages, and how out of breath somebody is cannot depend on that.
    /// </summary>
    private void Breathe(Vector3 position, Vector3 velocity)
    {
        double now = OpenFPS.Common.AudioClock.Now;
        if (_lastUpdateAt < 0) { _lastUpdateAt = now; return; }

        float dt = (float)(now - _lastUpdateAt);
        _lastUpdateAt = now;
        // A gap that long is a stall, a load or a breakpoint, and integrating it would have the
        // player recover a minute of breath in one frame.
        if (dt <= 0f || dt > 1f) return;

        float speed = new Vector2(velocity.X, velocity.Z).Length();
        if (_lungs.Update(speed, dt, out var breath))
            OnBreath?.Invoke(position + new Vector3(0, HeadHeight, 0), breath);
    }
}
