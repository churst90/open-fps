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
    public float Gravity { get; set; } = PhysicsConstants.Gravity;

    private readonly List<OpenFPS.Common.Geometry.SolidRef> _solids = new(32);

    /// <summary>Every solid but the body's own and those the triangle world is rebuilding.</summary>
    private struct OwnAndStale : OpenFPS.Common.Geometry.IGeometryFilter
    {
        public int Own;
        public IReadOnlySet<int>? Stale;
        public readonly bool Accept(int owner, in OpenFPS.Common.Geometry.Surface surface)
            => owner != Own && (Stale == null || !Stale.Contains(owner));
    }

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
    /// <returns>What the body pressed into and was held off this step (see
    /// <see cref="SharedMovementEngine.Contact"/>), with the entity it was; null when nothing stopped it.
    /// Prediction replays inputs after every correction, so only the caller knows whether this step
    /// was a fresh one — see <see cref="PredictionReconciler.LastContact"/>.</returns>
    public BodyContact? Predict(ClientInputUpdate input, WorldSnapshot snapshot, float dt)
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
        // WHAT IS NEAR ENOUGH TO WALK INTO, and nothing else.
        //
        // The grid returning an EMPTY set is an answer: there is no static geometry within the search
        // radius, which is what standing in the middle of a road is. Treating it as a failure and
        // falling back to every entity in the world put the whole map through the collision solver on
        // every physics tick, in the one place a player spends most of their time. Five hundred boxes
        // survived it; a city of six thousand does not, and it is felt as the client locking up the
        // moment you start moving.
        //
        // Only a MISSING grid — the seconds between a map arriving and the first rebuild — is a
        // reason to consider everything.
        //
        // ...and what is near enough to walk into and MOVING. The static grid holds only static
        // objects, so a car or a bus was never a candidate here even once the server made it solid:
        // the client walked straight through it and the server pulled it back out, every tick, which
        // is a rubber band rather than a car. The moving set is tens of things, not thousands, and
        // only those within reach are kept.
        IEnumerable<EntitySnapshot> candidates;
        bool triangles = SpatialService.UsesTriangles(snapshot);
        var gridResults = triangles ? null : snapshot.StaticGrid?.GetItemsInRadius(_state.Position, CollisionSearchRadius);
        if (triangles)
        {
            // The static solids from the triangle world (gathered below, once the step's reach is known);
            // here only the statics it does not hold and what moves near enough to walk into.
            float reach = CollisionSearchRadius;
            var unindexed = snapshot.UnindexedStatics.Where(id => snapshot.Entities.ContainsKey(id)).Select(id => snapshot.Entities[id]);
            var moving = snapshot.DynamicEntities.Where(d =>
                d.Definition.Collider.IsSolid
                && Vector3.Distance(d.Transform.Position, _state.Position)
                   <= reach + d.Definition.Collider.Size.Length() * 0.5f);
            candidates = unindexed.Concat(moving);
        }
        else if (gridResults != null)
        {
            float reach = CollisionSearchRadius;
            var moving = snapshot.DynamicEntities.Where(d =>
                d.Definition.Collider.IsSolid
                && Vector3.Distance(d.Transform.Position, _state.Position)
                   <= reach + d.Definition.Collider.Size.Length() * 0.5f);
            candidates = gridResults.Select(id => snapshot.Entities[id]).Concat(moving);
        }
        else
            candidates = snapshot.Entities.Values;

        int maxCandidates = candidates.Count();
        var colliderArray = ArrayPool<SharedMovementEngine.Collider>.Shared.Rent(maxCandidates);
        var idArray = ArrayPool<int>.Shared.Rent(maxCandidates);
        int colliderCount = 0;

        try
        {
            foreach (var entity in candidates)
            {
                if (entity.Id == OwnEntityId) continue;
                var def = entity.Definition;
                if (!def.Collider.IsSolid) continue;

                idArray[colliderCount] = entity.Id;
                colliderArray[colliderCount++] = new SharedMovementEngine.Collider {
                    Position = entity.Transform.Position,
                    Size = def.Collider.Size,
                    Rotation = SharedMovementEngine.StandingRotation(def.Collider.Shape, entity.Transform.Rotation),
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
                Speed = PhysicsConstants.FootSpeed(input.Sprint, _state.SpeedLimit),
                PlayerRadius = PlayerRadius,
                PlayerHeight = PlayerHeight,
                StepHeight = StepHeight,
                IsJumpRequested = input.Jump,
                MapMin = MapMin,
                MapMax = MapMax
            };

            var collidersSlice = new ReadOnlySpan<SharedMovementEngine.Collider>(colliderArray, 0, colliderCount);
            var obstacles = new SharedMovementEngine.Obstacles(collidersSlice);
            var solids = _solids;
            solids.Clear();
            if (triangles)
            {
                var filter = new OwnAndStale { Own = OwnEntityId, Stale = snapshot.GeometryStale };
                // As the server: the capsule on triangles, and the grade of the floor along the way.
                ctx.Body = OpenFPS.Common.Geometry.BodyShape.Capsule;
                ctx.Grade = SharedMovementEngine.GradeAlong(snapshot.Geometry!, ref filter, _state.Position, inputDir);
                SharedMovementEngine.GatherSolids(ctx, snapshot.Geometry!, ref filter, solids);
                obstacles = new SharedMovementEngine.Obstacles(collidersSlice, snapshot.Geometry,
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(solids));
            }
            var result = SharedMovementEngine.Step(ctx, obstacles, out var contact);

            _state.Position = result.NewPosition;
            _state.Velocity = result.NewVelocity;
            _state.IsGrounded = result.IsGrounded;

            if (!contact.Blocked || contact.ColliderIndex < 0) return null;
            int hitId;
            if (contact.ColliderIndex < colliderCount) hitId = idArray[contact.ColliderIndex];
            else if (contact.ColliderIndex - colliderCount < solids.Count) hitId = snapshot.Geometry!.OwnerOf(solids[contact.ColliderIndex - colliderCount]);
            else return null;
            return new BodyContact(hitId, contact.Normal, contact.IntoSpeed, ctx.Speed, result.NewPosition);
        }
        finally
        {
            ArrayPool<SharedMovementEngine.Collider>.Shared.Return(colliderArray);
            ArrayPool<int>.Shared.Return(idArray);
        }
    }
}

/// <summary>
/// The body pressed into something and was held off it: which entity, the surface's outward normal,
/// how fast the attempted motion went INTO it, how fast the body was trying to go at all, and where
/// its feet ended up. <see cref="Intent"/> is the share of the attempt aimed at the surface — one
/// walking straight at a wall, near zero brushing along it.
/// </summary>
public readonly record struct BodyContact(int EntityId, Vector3 Normal, float IntoSpeed, float Speed, Vector3 Feet)
{
    public float Intent => Speed > 1e-4f ? Math.Clamp(IntoSpeed / Speed, 0f, 1f) : 0f;
}
