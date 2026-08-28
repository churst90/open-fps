using System;
using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using System.Buffers;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using static OpenFPS.Common.PhysicsConstants;

namespace OpenFPS.Client.Core;

/// <summary>
/// Responsibility: Performs local physics prediction (Client-Side Prediction).
/// Ensures the player feels immediate response while waiting for server confirmation.
/// Now uses the unified SharedMovementEngine for sliding and step-climbing.
/// Optimized for zero-allocation performance on the client.
/// </summary>
public class ClientPhysicsSystem
{
    private readonly LocalPlayerState _state;
    private readonly SpatialService _spatial;
    public int OwnEntityId { get; set; } = -1;
    public SpatialService Spatial => _spatial;

    public Vector3 MapMin { get; set; } = new(-50, 0, -50);
    public Vector3 MapMax { get; set; } = new(50, 20, 50);
    public float Gravity { get; set; } = 15.0f;

    public ClientPhysicsSystem(LocalPlayerState state, SpatialService spatial)
    {
        _state = state;
        _spatial = spatial;
    }

    /// <summary>
    /// Applies a look delta to the player's orientation using the *same* formula the server's
    /// MovementSystem uses — no smoothing, no target angles. Rotation is deliberately NOT part of
    /// <see cref="Predict"/>: prediction is replayed on every server correction, and replaying a
    /// stateful, lerped rotation made the client's heading depend on how many packets were in
    /// flight. Call this once per input, when the input is first gathered.
    /// </summary>
    public void ApplyLook(ClientInputUpdate input, float dt)
    {
        if (input.LookDelta != Vector2.Zero)
        {
            _state.Yaw -= input.LookDelta.X * RotationSpeed * dt;
            _state.Pitch = Math.Clamp(_state.Pitch + (input.LookDelta.Y * RotationSpeed * dt), -1.5f, 1.5f);
        }
        _state.Rotation = Quaternion.CreateFromYawPitchRoll(_state.Yaw, _state.Pitch, 0);
    }

    /// <summary>
    /// Advances the local player one step. Pure in the state it reads: position, velocity and the
    /// current yaw in, new position/velocity out — so replaying the same input list from the same
    /// server state always lands in the same place.
    /// </summary>
    public void Predict(ClientInputUpdate input, WorldSnapshot snapshot, float dt)
    {
        // 1. Vertical Physics (Unified logic with server)
        float groundY = PhysicsUtils.GetGroundHeight(snapshot, _state.Position, OwnEntityId, out string mat);
        _state.CurrentMaterial = mat;

        Vector3 inputDir = Vector3.Zero;
        if (input.MoveDirection != Vector3.Zero)
        {
            inputDir = Vector3.Transform(input.MoveDirection, Quaternion.CreateFromYawPitchRoll(_state.Yaw, 0, 0));
        }

        // 2. GATHER COLLIDERS from Snapshot using ArrayPool
        IEnumerable<EntitySnapshot> candidates;
        var gridResults = snapshot.StaticGrid?.GetItemsInRadius(_state.Position, CollisionSearchRadius);
        if (gridResults != null && gridResults.Any())
            candidates = gridResults.Select(id => snapshot.Entities[id]);
        else
            candidates = snapshot.Entities.Values;

        int maxCandidates = candidates.Count();
        var colliderArray = ArrayPool<SharedMovementEngine.Collider>.Shared.Rent(maxCandidates);
        int colliderCount = 0;

        try
        {
            foreach (var entity in candidates)
            {
                if (entity.Id == OwnEntityId) continue;
                var def = entity.Definition;
                if (!def.Collider.IsSolid) continue;

                colliderArray[colliderCount++] = new SharedMovementEngine.Collider {
                    Position = entity.Transform.Position,
                    Size = def.Collider.Size,
                    Rotation = entity.Transform.Rotation,
                    Material = def.Material.Material
                };
            }

            var ctx = new SharedMovementEngine.MovementContext
            {
                Position = _state.Position,
                Velocity = _state.Velocity,
                InputDirection = inputDir,
                DeltaTime = dt,
                GroundHeight = groundY,
                Gravity = Gravity,
                JumpForce = JumpPower,
                Speed = WalkSpeed,
                PlayerRadius = PlayerRadius,
                PlayerHeight = PlayerHeight,
                StepHeight = StepHeight,
                IsJumpRequested = input.Jump,
                MapMin = MapMin,
                MapMax = MapMax
            };

            var collidersSlice = new ReadOnlySpan<SharedMovementEngine.Collider>(colliderArray, 0, colliderCount);
            var result = SharedMovementEngine.Step(ctx, collidersSlice);

            _state.Position = result.NewPosition;
            _state.Velocity = result.NewVelocity;
            _state.IsGrounded = result.IsGrounded;
        }
        finally
        {
            ArrayPool<SharedMovementEngine.Collider>.Shared.Return(colliderArray);
        }
    }
}
