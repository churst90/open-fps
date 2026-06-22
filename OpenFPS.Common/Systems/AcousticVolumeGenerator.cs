using System.Numerics;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using System.Collections.Generic;
using System;
using System.Linq;

namespace OpenFPS.Common.Systems;

/// <summary>
/// Responsibility: Generates an AcousticMap from a list of world entities.
/// Optimized Strategy: "Region-First" - only voxelizes explicit room volumes to save CPU/Memory.
/// </summary>
public static class AcousticVolumeGenerator
{
    public static AcousticMap GenerateRegions(IEnumerable<EntityDefinition> entities, Vector3 mapSize, Vector3 minBound, float voxelResolution = 0.5f, float occlusionFloor = 0.05f)
    {
        // 1. Initialize the map with the full map bounds
        var acousticMap = new AcousticMap(mapSize, minBound, voxelResolution);
        acousticMap.OcclusionFloor = occlusionFloor;
        
        var grid = acousticMap.VoxelGrid;
        Vector3 offset = minBound;

        // 2. Pre-fill the grid with "Outside" (GlobalEnvironmentId)
        // This avoids the expensive flood-fill of the outdoor void.
        int outsideRegionId = AcousticConstants.GlobalRegionId;
        var globalDef = entities.FirstOrDefault(e => e.EntityId == outsideRegionId && e.Region.RoomSize.X > 0);
        
        if (globalDef != null)
        {
            acousticMap.Regions[outsideRegionId] = globalDef.Region;
            acousticMap.RegionPositions[outsideRegionId] = globalDef.Transform.Position;
        }
        else
        {
            acousticMap.Regions[outsideRegionId] = new RegionComponent {
                FriendlyName = "Outside",
                IsIndoor = false,
                Environment = AcousticEnvironmentType.LargeOpen,
                RoomSize = mapSize,
                ReverbTimeScale = 0.0f, // Default to dry unless a GlobalEnvironment entity exists
                Materials = new int[] { 1, 1, 1, 15, 1, 1 }
            };
            acousticMap.RegionPositions[outsideRegionId] = Vector3.Zero;
        }
        acousticMap.GlobalEnvironmentId = outsideRegionId;

        // 3. Process Explicit Regions
        // We only voxelize the space INSIDE your defined rooms.
        foreach (var def in entities)
        {
            if (def.Region.RoomSize.X > 0)
            {
                int regionId = def.EntityId;
                acousticMap.Regions[regionId] = def.Region;
                acousticMap.RegionPositions[regionId] = def.Transform.Position;
                acousticMap.RegionRotations[regionId] = def.Transform.Rotation;

                // Voxelize using the high-performance hierarchical OBB method
                grid.SetRegionOBB(def.Transform.Position, def.Region.RoomSize, def.Transform.Rotation, regionId);
            }
        }

        // 4. Automated Portal Discovery (Spatial Audit)
        // Detect gaps between regions and synthesize portals if none exist
        var discoveredPortals = new Dictionary<int, (PortalComponent Portal, Vector3 Position)>();
        int virtualPortalId = -1000;

        foreach (var r1 in acousticMap.Regions.Keys)
        {
            if (r1 == acousticMap.GlobalEnvironmentId) continue;
            if (!acousticMap.RegionPositions.TryGetValue(r1, out var pos1)) continue;
            var size1 = acousticMap.Regions[r1].RoomSize;
            
            // D: Use rotation-aware face normals so buildings rotated away from cardinal axes
            // have their actual wall faces probed, not just the world-axis directions.
            Quaternion regionRot = acousticMap.RegionRotations.GetValueOrDefault(r1, Quaternion.Identity);
            Vector3[] dirs =
            {
                Vector3.Normalize(Vector3.Transform(Vector3.UnitX, regionRot)),
                Vector3.Normalize(Vector3.Transform(-Vector3.UnitX, regionRot)),
                Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, regionRot)),
                Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, regionRot)),
                Vector3.UnitY,   // Vertical faces are never rotated horizontally
                -Vector3.UnitY
            };
            foreach (var dir in dirs)
            {
                Vector3 edgePos = pos1 + (dir * (Math.Max(size1.X, Math.Max(size1.Y, size1.Z)) / 2f + 0.5f));
                int r2 = grid.GetRegionAt(edgePos);
                if (r2 != r1 && r2 != 0) // Valid boundary
                {
                    // Check if a portal already links these
                    bool hasPortal = entities.Any(e => e.Portal.ApertureSize > 0 && 
                        ((e.Portal.RegionAId == r1 && e.Portal.RegionBId == r2) || 
                         (e.Portal.RegionAId == r2 && e.Portal.RegionBId == r1)));
                         
                    if (!hasPortal)
                    {
                        // Auto-generate portal
                        var portal = new PortalComponent { 
                            RegionAId = r1, RegionBId = r2, ApertureSize = 2.0f 
                        };
                        discoveredPortals[virtualPortalId--] = (portal, edgePos);
                    }
                }
            }
        }

        // 5. Process Portals
        // Build the nodal graph for A* pathfinding.
        foreach (var def in entities)
        {
            if (def.Portal.ApertureSize > 0)
            {
                int rA = def.Portal.RegionAId;
                int rB = def.Portal.RegionBId;

                // If IDs are missing, attempt to probe the voxel grid around the portal
                if (rA == 0 && rB == 0) 
                {
                    Vector3 forward = Vector3.Transform(Vector3.UnitZ, def.Transform.Rotation);
                    Vector3[] searchDirs = { forward, -forward, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX, -Vector3.UnitX };
                    
                    HashSet<int> foundRegions = new();
                    foreach (var dir in searchDirs)
                    {
                        for (float d = 0.25f; d <= 5.0f; d += 0.25f)
                        {
                            Vector3 probe = def.Transform.Position + dir * d;
                            int r = grid.GetRegionAt(probe);
                            if (r != 0) { foundRegions.Add(r); break; }
                        }
                    }

                    if (foundRegions.Count >= 2) { var l = foundRegions.ToList(); rA = l[0]; rB = l[1]; }
                    else if (foundRegions.Count == 1) { rA = foundRegions.First(); rB = AcousticConstants.GlobalRegionId; }
                }

                if (rA != rB)
                {
                    var portalComp = def.Portal;
                    portalComp.RegionAId = rA;
                    portalComp.RegionBId = rB;
                    acousticMap.Portals[def.EntityId] = (portalComp, def.Transform.Position);
                }
            }
        }

        foreach (var kvp in discoveredPortals)
        {
            // Only add if we haven't reached the same boundary manually
            if (!acousticMap.Portals.Values.Any(p => 
                (p.Portal.RegionAId == kvp.Value.Portal.RegionAId && p.Portal.RegionBId == kvp.Value.Portal.RegionBId) ||
                (p.Portal.RegionAId == kvp.Value.Portal.RegionBId && p.Portal.RegionBId == kvp.Value.Portal.RegionAId)))
            {
                acousticMap.Portals[kvp.Key] = kvp.Value;
            }
        }

        return acousticMap;
    }

    /// <summary>
    /// Performs a delta update for a single region entity that has moved or changed.
    /// </summary>
    public static void UpdateRegion(AcousticMap map, int regionId, Vector3 pos, Quaternion rot, RegionComponent reg)
    {
        var grid = map.VoxelGrid;
        
        // 1. Re-voxelize at NEW position/rotation (Octree handles overwriting)
        grid.SetRegionOBB(pos, reg.RoomSize, rot, regionId);
        
        // 2. Update metadata
        map.RegionPositions[regionId] = pos;
        map.RegionRotations[regionId] = rot;
        map.Regions[regionId] = reg;
    }
}
