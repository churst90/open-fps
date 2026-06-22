using Arch.Core;
using OpenFPS.Common.Components;
using System.Numerics;
using System.Collections.Generic;
using System;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Static logic for AI Finite State Machine updates.
/// </summary>
public static class AISystem
{
    public static void Update(World world, Dictionary<int, Entity> lookup, float dt)
    {
        var playerBuffer = new List<(Entity entity, Vector3 position)>();
        
        // 1. Gather all living players for target acquisition
        world.Query(new QueryDescription().WithAll<PlayerComponent, Transform>(), (Entity e, ref PlayerComponent p, ref Transform t) => {
            playerBuffer.Add((e, t.Position));
        });

        // 2. Update AI State Machines
        world.Query(new QueryDescription().WithAll<Transform, Velocity, BehaviorTreeComponent, NameComponent>(), (Entity e, ref Transform transform, ref Velocity velocity, ref BehaviorTreeComponent bt, ref NameComponent name) =>
        {
            // Defensive check: Is the entity still alive in this tick?
            if (!world.IsAlive(e)) return;

            switch (bt.State)
            {
                case AIState.Idle:
                    bt.WaitTimer -= dt;
                    velocity.Linear = Vector3.Zero;
                    if (bt.WaitTimer <= 0)
                    {
                        bt.State = AIState.Wander;
                        bt.TargetPosition = transform.Position + new Vector3(Random.Shared.NextSingle() * 20 - 10, 0, Random.Shared.NextSingle() * 20 - 10);
                    }
                    break;

                case AIState.Wander:
                    Vector3 toTarget = bt.TargetPosition - transform.Position;
                    toTarget.Y = 0;
                    if (toTarget.Length() < 1.0f)
                    {
                        bt.State = AIState.Idle;
                        bt.WaitTimer = 2.0f + Random.Shared.NextSingle() * 3.0f;
                        velocity.Linear = Vector3.Zero;
                    }
                    else
                    {
                        velocity.Linear = Vector3.Normalize(toTarget) * 2.5f;
                        if (velocity.Linear.LengthSquared() > 0.01f)
                            transform.Rotation = Quaternion.CreateFromYawPitchRoll(MathF.Atan2(velocity.Linear.X, velocity.Linear.Z), 0, 0);
                    }
                    break;

                case AIState.Chase:
                    // Only chase if target still exists and is alive
                    if (lookup.TryGetValue(bt.TargetEntityId, out var targetEntity) && world.IsAlive(targetEntity))
                    {
                        var targetPos = world.Get<Transform>(targetEntity).Position;
                        Vector3 toEnemy = targetPos - transform.Position;
                        if (toEnemy.Length() > 30.0f) { bt.State = AIState.Wander; velocity.Linear = Vector3.Zero; }
                        else
                        {
                            velocity.Linear = Vector3.Normalize(toEnemy) * 4.5f;
                            if (velocity.Linear.LengthSquared() > 0.01f)
                                transform.Rotation = Quaternion.CreateFromYawPitchRoll(MathF.Atan2(velocity.Linear.X, velocity.Linear.Z), 0, 0);
                        }
                    }
                    else { bt.State = AIState.Idle; velocity.Linear = Vector3.Zero; }
                    break;
            }

            // 3. Proximity-based target acquisition
            if (bt.State != AIState.Chase)
            {
                foreach (var player in playerBuffer)
                {
                    if (Vector3.Distance(transform.Position, player.position) < 15.0f)
                    {
                        bt.State = AIState.Chase;
                        bt.TargetEntityId = player.entity.Id;
                        break;
                    }
                }
            }
        });
    }
}
