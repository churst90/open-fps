using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>
/// The local player's footsteps, landings and breaths, from the predicted position. The stride is
/// <see cref="StrideAccumulator"/>, shared with every other body (<see cref="OtherBodies"/>); what is
/// local is that the ground state and the material come from this frame's own physics.
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

    public event Action<Vector3, string, string, StepSlope>? OnStepTriggered; // Position, Material, Variant, up/down
    public event Action<Vector3, string, string>? OnLandTriggered;

    /// <summary>A breath. A seat does not reset the lungs as it resets the stride: a driver who sprinted
    /// to the car is still out of breath in it.</summary>
    public event Action<Vector3, Breath>? OnBreath;

    /// <summary>How hard the player is working, 0 to 1. For a spoken readout, and for tests.</summary>
    public float Exertion => _lungs.Exertion;

    public LocalPlayerController(LocalPlayerState state)
    {
        _state = state;
    }

    /// <summary>
    /// Where a foot goes down and on what, given where the stride put it, the body's feet and the way it is
    /// going (PhysicsUtils.FootOnFloor over the session's snapshot): the floor under the foot, a tread on a
    /// flight. Null leaves the foot where the stride put it, on the body's floor.
    /// </summary>
    public Func<Vector3, Vector3, Vector3, (Vector3 At, string? Material)>? Footing { get; set; }

    public void Update(Vector3 newPosition, Vector3 velocity)
    {
        Breathe(newPosition, velocity);

        var fall = _stride.Update(newPosition, velocity, _state.IsGrounded, _state.Rotation);
        if (!fall.Anything) return;

        // "None" is what the ground probe says when it found no floor to name; a landing is still a
        // landing on it, so it falls back rather than going silent.
        string mat = _state.CurrentMaterial == "None" ? "Generic" : _state.CurrentMaterial;

        if (fall.Landed) OnLandTriggered?.Invoke(newPosition, mat, _state.CurrentVariant);
        if (fall.Stepped)
        {
            var at = fall.StepPosition;
            if (Footing != null)
            {
                var (foot, footMaterial) = Footing(at, newPosition, velocity);
                at = foot;
                if (!string.IsNullOrEmpty(footMaterial) && footMaterial != "None") mat = footMaterial;
            }
            OnStepTriggered?.Invoke(at, mat, _state.CurrentVariant, fall.Slope);
        }
    }

    /// <summary>Lungs run on elapsed time, not frames: how out of breath somebody is cannot depend on the
    /// frame rate.</summary>
    private void Breathe(Vector3 position, Vector3 velocity)
    {
        double now = OpenFPS.Common.AudioClock.Now;
        if (_lastUpdateAt < 0) { _lastUpdateAt = now; return; }

        float dt = (float)(now - _lastUpdateAt);
        _lastUpdateAt = now;
        // A gap that long is a stall or a load; integrating it would recover a minute of breath at once.
        if (dt <= 0f || dt > 1f) return;

        float speed = new Vector2(velocity.X, velocity.Z).Length();
        if (_lungs.Update(speed, dt, out var breath))
            OnBreath?.Invoke(position + new Vector3(0, HeadHeight, 0), breath);
    }
}
