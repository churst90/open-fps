using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using static OpenFPS.Common.PhysicsConstants;

namespace OpenFPS.Client.Core;

/// <summary>
/// Owns client-side prediction bookkeeping: the unacknowledged-input history, the replay that
/// follows a server correction, and the yaw reconciliation that keeps the local heading honest.
///
/// Both heads (Windows Forms and GTK) drive this, so there is exactly one implementation of the
/// reconciliation rules rather than two that can drift apart.
/// </summary>
public sealed class PredictionReconciler
{
    private readonly LocalPlayerState _state;
    private readonly ClientPhysicsSystem _physics;

    /// <summary>Inputs sent but not yet acknowledged by the server, oldest first.</summary>
    private readonly List<ClientInputUpdate> _history = new();

    /// <summary>Yaw error (radians) tolerated before the local heading is snapped to the server's.</summary>
    private const float YawReconcileThreshold = 0.05f;

    /// <summary>Position error (metres) above which we snap outright instead of smoothing.</summary>
    private const float SnapDistance = 5.0f;

    public PredictionReconciler(LocalPlayerState state, ClientPhysicsSystem physics)
    {
        _state = state;
        _physics = physics;
    }

    public int PendingInputs => _history.Count;

    /// <summary>Drops the whole history — call on spawn or any teleport, where replay is meaningless.</summary>
    public void Reset() => _history.Clear();

    /// <summary>
    /// Applies one freshly gathered input: rotate, predict, and remember it for replay.
    /// </summary>
    public void Step(ClientInputUpdate input, WorldSnapshot snapshot, float dt)
    {
        _physics.ApplyLook(input, dt);
        _physics.Predict(input, snapshot, dt);

        _history.Add(input);

        // Bound the history. Without this, a client whose acks stop arriving (server hitch, packet
        // loss, a dropped session) grows the list forever and re-simulates all of it every frame.
        if (_history.Count > MaxInputHistory)
            _history.RemoveRange(0, _history.Count - MaxInputHistory);
    }

    /// <summary>
    /// Re-simulates the player's path from a verified server state, then measures the visual error
    /// so the caller can bleed it away instead of snapping.
    /// </summary>
    public void ApplyServerCorrection(EntityState serverState, long lastProcessedId, WorldSnapshot snapshot)
    {
        Vector3 predictedPos = _state.Position;

        var transform = serverState.Transform.ToTransform();
        _state.Position = transform.Position;
        _state.Velocity = serverState.LinearVelocity;

        // Purge inputs the server has already folded into that state.
        _history.RemoveAll(i => i.SequenceId <= lastProcessedId);

        ReconcileYaw(transform.Rotation);

        // Replay only the movement of the still-unacknowledged inputs. Look is NOT replayed: the
        // local heading is already ahead of the server by exactly those inputs (see ReconcileYaw),
        // and re-applying the deltas here would double-count every turn.
        foreach (var input in _history)
            _physics.Predict(input, snapshot, input.DeltaTime);

        _state.VisualOffset = predictedPos - _state.Position;
        if (_state.VisualOffset.Length() > SnapDistance) _state.VisualOffset = Vector3.Zero;
    }

    /// <summary>
    /// Reconciles the look angles against the server's quantized rotation. The server's yaw is
    /// behind ours by the unacknowledged look deltas, so we compare against that projection rather
    /// than the raw server value — and only correct when the disagreement is real, so quantization
    /// noise never twitches the player's heading.
    /// </summary>
    private void ReconcileYaw(Quaternion serverRotation)
    {
        MathHelper.ToYawPitch(serverRotation, out float serverYaw, out float serverPitch);

        float pendingYaw = 0f;
        float pendingPitch = 0f;
        foreach (var input in _history)
        {
            pendingYaw -= input.LookDelta.X * RotationSpeed * input.DeltaTime;
            pendingPitch += input.LookDelta.Y * RotationSpeed * input.DeltaTime;
        }

        float expectedYaw = serverYaw + pendingYaw;
        float expectedPitch = Math.Clamp(serverPitch + pendingPitch, -1.5f, 1.5f);

        if (MathF.Abs(MathHelper.WrapAngle(expectedYaw - _state.Yaw)) > YawReconcileThreshold ||
            MathF.Abs(expectedPitch - _state.Pitch) > YawReconcileThreshold)
        {
            _state.Yaw = MathHelper.WrapAngle(expectedYaw);
            _state.Pitch = expectedPitch;
            _state.Rotation = Quaternion.CreateFromYawPitchRoll(_state.Yaw, _state.Pitch, 0);
        }
    }
}
