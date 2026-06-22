using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common.Components;

namespace OpenFPS.Common;

/// <summary>
/// Shared physics utilities to ensure client-server parity for grounding and collision.
/// </summary>
public static class PhysicsUtils
{
    /// <summary>
    /// Performs a standardized vertical probe to find the floor height at a given position.
    /// Uses a "Thin Probe" (0.1m footprint) to prevent standing on walls.
    /// Version for Server (using Arch World).
    /// </summary>
    public static float GetGroundHeight(World world, SpatialGrid<Entity> grid, Vector3 pos, out string material)
    {
        const float stepHeight = 0.4f;

        // 1. Optimized Grid Search
        var candidates = grid.GetItemsInRadius(pos, 50.0f).ToList();
        float ground = CalculateHeightFromCandidates(world, candidates, pos, stepHeight, out material);
        
        // 2. REAL SOLUTION: Exhaustive Fallback
        // If the grid search missed the floor (e.g. edge case or grid stale), 
        // perform a global scan of all solid entities to guarantee grounding.
        if (ground < -900f) 
        {
            var allStatic = new List<Entity>();
            world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e) => {
                allStatic.Add(e);
            });
            ground = CalculateHeightFromCandidates(world, allStatic, pos, stepHeight, out material);
        }

        return ground;
    }

    private static float CalculateHeightFromCandidates(World world, IEnumerable<Entity> candidates, Vector3 pos, float stepHeight, out string material)
    {
        float bestY = -1000f;
        material = "Generic";
        string bestMat = "Generic";

        // Probing points: Center + 4 points at the edge of the player radius
        const float radius = PhysicsConstants.PlayerRadius;
        Span<Vector3> probes = stackalloc Vector3[5];
        probes[0] = pos;
        probes[1] = pos + new Vector3(radius, 0, 0);
        probes[2] = pos + new Vector3(-radius, 0, 0);
        probes[3] = pos + new Vector3(0, 0, radius);
        probes[4] = pos + new Vector3(0, 0, -radius);

        foreach (var e in candidates)
        {
            if (!world.Has<Transform>(e) || !world.Has<ColliderComponent>(e)) continue;
            
            ref var t = ref world.Get<Transform>(e);
            ref var c = ref world.Get<ColliderComponent>(e);
            if (!c.IsSolid) continue;

            float objTop = t.Position.Y + (c.Size.Y / 2f);
            if (objTop > pos.Y + stepHeight) continue;

            // Check if any of our probe points are within this object's footprint
            bool hit = false;
            foreach (var p in probes)
            {
                if (GeometryUtils.IsPointInOBB(p, t.Position, new Vector3(c.Size.X, 40000f, c.Size.Z), t.Rotation))
                {
                    hit = true;
                    break;
                }
            }

            if (hit && objTop > bestY)
            {
                bestY = objTop;
                bestMat = world.Has<MaterialComponent>(e) ? world.Get<MaterialComponent>(e).Material : "Generic";
            }
        }
        
        material = bestMat;
        return bestY;
    }

    /// <summary>
    /// Performs a standardized vertical probe to find the floor height at a given position.
    /// Version for Client (using WorldSnapshot).
    /// </summary>
    public static float GetGroundHeight(WorldSnapshot snapshot, Vector3 pos, int ownEntityId, out string material)
    {
        const float stepHeight = 0.4f;

        // 1. Optimized Grid Search
        IEnumerable<EntitySnapshot> candidates;
        var gridResults = snapshot.StaticGrid?.GetItemsInRadius(pos, 50.0f);
        if (gridResults != null && gridResults.Any())
            candidates = gridResults.Select(id => snapshot.Entities[id]);
        else
            candidates = snapshot.Entities.Values;

        float ground = CalculateHeightFromSnapshots(candidates, pos, stepHeight, ownEntityId, out material);

        // 2. REAL SOLUTION: Exhaustive Fallback
        if (ground < -900f && snapshot.Entities.Count > 0)
        {
            ground = CalculateHeightFromSnapshots(snapshot.Entities.Values, pos, stepHeight, ownEntityId, out material);
        }

        return ground;
    }

    private static float CalculateHeightFromSnapshots(IEnumerable<EntitySnapshot> candidates, Vector3 pos, float stepHeight, int ownEntityId, out string material)
    {
        float bestY = -1000f;
        material = "Generic";
        string bestMat = "Generic";

        const float radius = PhysicsConstants.PlayerRadius;
        Span<Vector3> probes = stackalloc Vector3[5];
        probes[0] = pos;
        probes[1] = pos + new Vector3(radius, 0, 0);
        probes[2] = pos + new Vector3(-radius, 0, 0);
        probes[3] = pos + new Vector3(0, 0, radius);
        probes[4] = pos + new Vector3(0, 0, -radius);

        foreach (var entity in candidates)
        {
            if (entity.Id == ownEntityId) continue;
            var def = entity.Definition;
            if (!def.Collider.IsSolid) continue;

            float objTop = entity.Transform.Position.Y + (def.Collider.Size.Y / 2f);
            if (objTop > pos.Y + stepHeight) continue;
            
            bool hit = false;
            foreach (var p in probes)
            {
                if (GeometryUtils.IsPointInOBB(p, entity.Transform.Position, new Vector3(def.Collider.Size.X, 40000f, def.Collider.Size.Z), entity.Transform.Rotation))
                {
                    hit = true;
                    break;
                }
            }

            if (hit && objTop > bestY)
            {
                bestY = objTop;
                bestMat = string.IsNullOrEmpty(def.Material.Material) ? "Generic" : def.Material.Material;
            }
        }
        
        material = bestMat;
        return bestY;
    }
}
