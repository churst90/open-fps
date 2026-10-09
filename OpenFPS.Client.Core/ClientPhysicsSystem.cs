using System.Numerics;
using System.Buffers;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using static OpenFPS.Common.PhysicsConstants;

namespace OpenFPS.Client.Core;

/// <summary>
/// Client-side prediction of the player's own movement, through the server's SharedMovementEngine, so
/// a key press moves you before the server answers.
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

    /// <summary>On the world: which of its tiles are there to walk into (held, and their ground built), and
    /// their size; null elsewhere. The server fences the same edges (SharedMovementEngine).</summary>
    public Func<OpenFPS.Common.TileKey, bool>? TileReady { get; set; }
    public float TileMetres { get; set; }

    /// <summary>The last step was stopped at the edge of a tile not built yet.</summary>
    public bool Fenced { get; private set; }

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
    /// Applies a look delta with the server MovementSystem's formula, no smoothing. Call once per input,
    /// when it is gathered: rotation is not part of <see cref="Predict"/>, because prediction is
    /// replayed on every correction and a replayed, lerped rotation made the heading depend on how many
    /// packets were in flight.
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
    /// Advances the local player one step. Pure in what it reads (position, velocity and yaw in, new
    /// position and velocity out), so replaying the same inputs from the same server state lands in the
    /// same place.
    /// </summary>
    /// <returns>What the body pressed into and was held off this step (see
    /// <see cref="SharedMovementEngine.Contact"/>), with its entity; null when nothing stopped it. Only
    /// the caller knows whether the step was fresh or a replay: see
    /// <see cref="PredictionReconciler.LastContact"/>.</returns>
    public BodyContact? Predict(ClientInputUpdate input, WorldSnapshot snapshot, float dt)
    {
        float groundY = PhysicsUtils.GetGroundHeight(snapshot, _state.Position, OwnEntityId, out string mat);
        _state.CurrentMaterial = mat;

        Vector3 inputDir = Vector3.Zero;
        if (input.MoveDirection != Vector3.Zero)
        {
            inputDir = Vector3.Transform(input.MoveDirection, Quaternion.CreateFromYawPitchRoll(_state.Yaw, 0, 0));
        }

        // Only what is near enough to walk into. An EMPTY grid answer means no static geometry in reach
        // (the middle of a road); reading it as a failure put the whole map through the solver every
        // tick, and a city of six thousand boxes locked the client up. Only a MISSING grid (between a
        // map arriving and its first rebuild) means consider everything. Moving solids are added
        // separately: the static grid never holds them, and without them the client walked through a
        // bus and the server pulled it back every tick.
        IEnumerable<EntitySnapshot> candidates;
        bool triangles = SpatialService.UsesTriangles(snapshot);
        var gridResults = triangles ? null : snapshot.StaticGrid?.GetItemsInRadius(_state.Position, CollisionSearchRadius);
        if (triangles)
        {
            // The triangle world's solids are gathered below; here only the statics it does not hold.
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
                MapMax = MapMax,
                // Held, and its ground built into the triangles: a foot never comes down on ground the client
                // cannot stand on yet.
                TileReady = TileReady is { } held ? key => held(key) && snapshot.Geometry is { } g && g.InstanceOfTile(key) >= 0 : null,
                TileMetres = TileMetres,
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
            Fenced = contact.Fenced;

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
/// The body pressed into something and was held off it: the entity, the surface's outward normal, the
/// attempted speed into it, the speed attempted at all, and where the feet ended up.
/// <see cref="Intent"/> is the share aimed at the surface: one walking straight at a wall, near zero
/// brushing along it.
/// </summary>
public readonly record struct BodyContact(int EntityId, Vector3 Normal, float IntoSpeed, float Speed, Vector3 Feet)
{
    public float Intent => Speed > 1e-4f ? Math.Clamp(IntoSpeed / Speed, 0f, 1f) : 0f;
}
