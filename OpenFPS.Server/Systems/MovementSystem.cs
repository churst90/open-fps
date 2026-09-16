using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using System.Numerics;
using System.Collections.Generic;
using System;
using System.Buffers;
using Serilog;
using static OpenFPS.Common.PhysicsConstants;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Authoritative system responsible for calculating entity movement.
/// Uses a stateless SharedMovementEngine to ensure deterministic parity with the client.
/// Optimized for zero-allocation performance and sub-tick input precision.
/// </summary>
public static class MovementSystem
{
    private static MapManager _maps = null!;

    // The tick runs on one thread; these are reused across every player and every sub-tick input so the
    // collision gather allocates nothing. CollectInRadius also walks the grid cells once and yields each
    // obstacle once — the previous Count()-then-foreach walked them twice, and a wall spanning four cells
    // was copied into the collider array four times, quadrupling the work the physics step then did.
    private static readonly List<Entity> _nearbyScratch = new(64);
    private static readonly HashSet<Entity> _nearbySeen = new();

    public static void Update(World world, Vector3 mapMin, Vector3 mapMax, SpatialGrid<Entity> grid,
                              Dictionary<int, Entity> lookup, SessionManager sessions, MapManager maps, float dt)
    {
        _maps = maps;
        
        float mapMinimumY = MapMinimumY; 
        float mapGravity = Gravity;

        world.Query(new QueryDescription().WithAll<ZoneComponent>(), (ref ZoneComponent zone) => {
            mapMinimumY = zone.MinimumY;
            mapGravity = zone.Gravity;
        });

        // Add a safety buffer below the map minimum to allow for natural falls before respawn.
        float voidThreshold = mapMinimumY - 5.0f;

        world.Query(new QueryDescription().WithAll<PlayerComponent, Transform, Velocity, MaterialComponent>(), (Entity e, ref PlayerComponent player, ref Transform transform, ref Velocity velocity, ref MaterialComponent material) =>
        {
            if (!sessions.TryGetSession(player.ConnectionId, out var session)) return;

            // Sub-tick simulation with a real-time budget. The tick grants exactly one tick of
            // simulated time (plus a small backlog for lag), and each input spends what it claims.
            // Inputs that outrun the budget stay queued for a later tick rather than being
            // integrated now, which is what closes the "send inputs faster than real time" speed
            // hack: extra packets buy latency, never distance.
            session.InputBudget = MathF.Min(session.InputBudget + dt, FixedDeltaTime * MaxInputBudgetTicks);

            int processedInputs = 0;
            while (processedInputs < MaxInputsPerTick
                   && session.InputBudget > 0f
                   && session.InputQueue.TryDequeue(out var input))
            {
                processedInputs++;
                session.LastProcessedSequenceId = input.SequenceId;

                // The client's claimed step is advisory: clamp it, then trim it to the budget.
                float stepDt = input.DeltaTime > 0f ? MathF.Min(input.DeltaTime, MaxInputDeltaTime) : dt;
                stepDt = MathF.Min(stepDt, session.InputBudget);
                session.InputBudget -= stepDt;

                // 0. SITTING DOWN
                //
                // An occupant's body is not theirs to move. The seat owns where they are — OccupancySystem
                // puts them in it once everything that could have moved the vehicle has run — so none of
                // the walking below applies: no input direction, no gravity, no ground probe, no
                // collision gather. Two things are still theirs. Where they are LOOKING, always; and in a
                // control seat, what the thing they are sitting in is being asked to do. That second one
                // is the whole of driving, and it is deliberately fed from the same input queue, budget
                // and sequence numbering as walking: a driver cannot outrun the speed limiter by sending
                // packets faster any more than a pedestrian can.
                if (world.Has<OccupantComponent>(e))
                {
                    if (input.LookDelta != Vector2.Zero)
                    {
                        player.Yaw -= input.LookDelta.X * RotationSpeed * stepDt;
                        player.Pitch = Math.Clamp(player.Pitch + (input.LookDelta.Y * RotationSpeed * stepDt), -1.5f, 1.5f);
                    }

                    var occupant = world.Get<OccupantComponent>(e);
                    if (occupant.Controls
                        && lookup.TryGetValue(occupant.RootEntityId, out var vehicle)
                        && world.IsAlive(vehicle))
                    {
                        DrivingSystem.ApplyControls(world, vehicle, input);
                    }
                    continue;
                }

                // 1. VOID CHECK (Safety Net)
                if (transform.Position.Y < voidThreshold)
                {
                    Log.Warning("MovementSystem: Player {User} fell into void at Y={Y}. Respawning.", player.Username, transform.Position.Y);
                    var sp = _maps.GetSpawnPoint(session.CurrentMapId);
                    transform.Position = sp.Position;
                    transform.Rotation = sp.Rotation;
                    velocity.Linear = Vector3.Zero;
                    transform.IsDirty = true;
                    
                    // CLEAR INPUT QUEUE: After a respawn, we discard the rest of the sub-tick inputs
                    // to prevent the player from instantly walking away from the spawn point 
                    // before the client acknowledges the teleport.
                    while (session.InputQueue.TryDequeue(out _)) { }
                    session.GroundProbe.Invalidate(); // the remembered floor is at the old position
                    break; 
                }

                // 2. ROTATION
                if (input.LookDelta != Vector2.Zero)
                {
                    player.Yaw -= input.LookDelta.X * RotationSpeed * stepDt;
                    player.Pitch = Math.Clamp(player.Pitch + (input.LookDelta.Y * RotationSpeed * stepDt), -1.5f, 1.5f);
                    transform.Rotation = Quaternion.CreateFromYawPitchRoll(player.Yaw, player.Pitch, 0);
                    transform.IsDirty = true;
                }

                float groundY = PhysicsUtils.GetGroundHeight(world, grid, transform.Position, ref session.GroundProbe, out string floorMat);
                if (floorMat != null) material.Material = floorMat;

                Vector3 inputDir = Vector3.Zero;
                if (input.MoveDirection != Vector3.Zero)
                {
                    inputDir = Vector3.Transform(input.MoveDirection, Quaternion.CreateFromYawPitchRoll(player.Yaw, 0, 0));
                }

                // 3. COLLISION GATHERING
                grid.CollectInRadius(transform.Position, CollisionSearchRadius, _nearbyScratch, _nearbySeen);
                var colliderArray = ArrayPool<SharedMovementEngine.Collider>.Shared.Rent(Math.Max(1, _nearbyScratch.Count));
                int colliderCount = 0;

                try
                {
                    foreach (var obstacle in _nearbyScratch)
                    {
                        if (obstacle.Id == e.Id) continue;
                        if (!world.Has<ColliderComponent>(obstacle)) continue;
                        
                        ref var t = ref world.Get<Transform>(obstacle);
                        ref var c = ref world.Get<ColliderComponent>(obstacle);
                        if (!c.IsSolid) continue;

                        colliderArray[colliderCount++] = new SharedMovementEngine.Collider {
                            Position = t.Position,
                            Size = c.Size,
                            Rotation = t.Rotation,
                            Material = world.Has<MaterialComponent>(obstacle) ? world.Get<MaterialComponent>(obstacle).Material : "Generic"
                        };
                    }

                    var collidersSlice = new ReadOnlySpan<SharedMovementEngine.Collider>(colliderArray, 0, colliderCount);

                    var ctx = new SharedMovementEngine.MovementContext
                    {
                        Position = transform.Position,
                        Velocity = velocity.Linear,
                        InputDirection = inputDir,
                        DeltaTime = stepDt,
                        GroundHeight = groundY,
                        Gravity = mapGravity,
                        JumpForce = JumpPower,
                        Speed = input.Sprint ? SprintSpeed : WalkSpeed,
                        PlayerRadius = PlayerRadius,
                        PlayerHeight = PlayerHeight,
                        StepHeight = StepHeight,
                        IsJumpRequested = input.Jump,
                        MapMin = mapMin,
                        MapMax = mapMax
                    };

                    // 4. PHYSICS STEP
                    var result = SharedMovementEngine.Step(ctx, collidersSlice);

                    transform.Position = result.NewPosition;
                    velocity.Linear = result.NewVelocity;
                    player.IsGrounded = result.IsGrounded;
                    transform.IsDirty = true;
                }
                finally
                {
                    ArrayPool<SharedMovementEngine.Collider>.Shared.Return(colliderArray);
                }
            }
        });
    }

    /// <summary>
    /// Checks if a cylinder at 'pos' with 'radius' and 'height' overlaps with any solid geometry in the world.
    /// Used by server commands for safe teleportation.
    /// </summary>
    public static bool CheckCollision(World world, SpatialGrid<Entity> grid, Vector3 pos, float radius, float height, Entity? ignoreEntity = null)
    {
        float footPadding = 0.4f;
        float checkHeight = height - footPadding;
        Vector3 checkCylCenter = pos + new Vector3(0, footPadding + (checkHeight / 2f), 0);

        foreach (var e in grid.GetItemsInRadius(pos, 10.0f))
        {
            if (ignoreEntity.HasValue && e.Id == ignoreEntity.Value.Id) continue;
            if (!world.Has<ColliderComponent>(e)) continue;
            
            ref var t = ref world.Get<Transform>(e);
            ref var c = ref world.Get<ColliderComponent>(e);
            if (!c.IsSolid) continue;

            Matrix4x4 worldToLocal = Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(t.Rotation));
            Vector3 localPos = Vector3.Transform(checkCylCenter - t.Position, worldToLocal);
            
            if (GeometryUtils.AABBIntersectsCylinder(-c.Size/2f, c.Size/2f, localPos, radius, checkHeight))
            {
                return true;
            }
        }
        return false;
    }
}
