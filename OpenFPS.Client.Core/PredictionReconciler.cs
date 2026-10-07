using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using static OpenFPS.Common.PhysicsConstants;

namespace OpenFPS.Client.Core;

/// <summary>
/// Client-side prediction: the unacknowledged inputs, their replay after a server correction, and the
/// yaw reconciliation. One implementation for both heads.
/// </summary>
public sealed class PredictionReconciler
{
    private readonly LocalPlayerState _state;
    private readonly ClientPhysicsSystem _physics;

    /// <summary>Inputs sent but not yet acknowledged by the server, oldest first.</summary>
    private readonly List<ClientInputUpdate> _history = new();

    /// <summary>Yaw error (radians) tolerated before the local heading is snapped to the server's.</summary>
    private const float YawReconcileThreshold = 0.05f;

    /// <summary>Position error, metres, above which the position snaps instead of smoothing.</summary>
    private const float SnapDistance = 5.0f;

    public PredictionReconciler(LocalPlayerState state, ClientPhysicsSystem physics)
    {
        _state = state;
        _physics = physics;
    }

    public int PendingInputs => _history.Count;

    /// <summary>
    /// What the body pressed into on the last fresh step, or null. Replays do not set it: a wall bump
    /// heard again on every server correction would be a bump per packet.
    /// </summary>
    public BodyContact? LastContact { get; private set; }

    /// <summary>Drops the history: on a spawn or a teleport, where replay means nothing.</summary>
    public void Reset() { _history.Clear(); LastContact = null; }

    /// <summary>
    /// The position is a seat's, not the player's. A passenger causes no movement the client could
    /// predict (the vehicle is simulated on the server), so it follows the server; looking round is
    /// still predicted.
    /// </summary>
    public bool Riding { get; set; }

    /// <summary>Whether the server is holding the body still (frozen, or dead). Nothing is predicted:
    /// the server moves a held body not at all, not even by gravity.</summary>
    public bool Held { get; set; }

    /// <summary>Applies a fresh input (look, then movement) and keeps it for replay.</summary>
    public void Step(ClientInputUpdate input, WorldSnapshot snapshot, float dt)
    {
        _physics.ApplyLook(input, dt);
        LastContact = Riding || Held ? null : _physics.Predict(input, snapshot, dt);

        // Still recorded while riding: the server acknowledges these sequence numbers, and the yaw
        // reconciliation below needs to know which look deltas it has not seen yet.
        _history.Add(input);

        // Bounded: a client whose acks stop arriving would otherwise re-simulate an ever longer list
        // every frame.
        if (_history.Count > MaxInputHistory)
            _history.RemoveRange(0, _history.Count - MaxInputHistory);
    }

    /// <summary>
    /// Re-simulates from a server state and leaves the visual error for the caller to bleed away.
    /// </summary>
    /// <returns>
    /// True when the correction was a teleport, too far to smooth, and the position snapped. The caller
    /// then drops whatever measures continuous motion (the stride accumulator would count the jump as
    /// walked). <c>/tp</c> and an admin move arrive only this way: no message says "you were moved".
    /// </returns>
    public bool ApplyServerCorrection(EntityState serverState, long lastProcessedId, WorldSnapshot snapshot)
    {
        Vector3 predictedPos = _state.Position;

        var transform = serverState.Transform.ToTransform();
        _state.Position = transform.Position;
        _state.Velocity = serverState.LinearVelocity;

        _history.RemoveAll(i => i.SequenceId <= lastProcessedId);

        // A passenger faces the way the vehicle faces; the session sets that every frame, and the
        // server's copy of it is a network trip behind (see ClientGameSession.FollowRide).
        if (!Riding) ReconcileYaw(transform.Rotation);

        // Movement only: the local heading is already ahead by these inputs' look (ReconcileYaw), and
        // replaying it would count every turn twice. A passenger replays nothing.
        if (!Riding && !Held)
            foreach (var input in _history)
                _physics.Predict(input, snapshot, input.DeltaTime);

        _state.VisualOffset = predictedPos - _state.Position;
        bool snapped = _state.VisualOffset.Length() > SnapDistance;
        if (snapped) _state.VisualOffset = Vector3.Zero;
        return snapped;
    }

    /// <summary>
    /// Compares the look angles with the server's quantized rotation plus the unacknowledged look
    /// deltas, and corrects only past a threshold so quantization noise never twitches the heading.
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
