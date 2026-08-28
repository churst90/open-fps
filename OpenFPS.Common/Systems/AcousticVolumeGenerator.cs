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
    /// <param name="autoDiscoverPortals">
    /// When true, any region boundary the map did not describe gets a synthesized portal at the centre of
    /// that face. This is OFF by default: the placement is a guess, and a guessed portal puts a room's
    /// reverb and its doorway-localized arrival direction on the wrong wall — which sounds worse than an
    /// unopened boundary, because walls still transmit and occlude correctly on their own. Undescribed
    /// boundaries are always reported either way, with the exact portal the map is missing.
    /// </param>
    public static AcousticMap GenerateRegions(IEnumerable<EntityDefinition> entities, Vector3 mapSize, Vector3 minBound, float voxelResolution = 0.5f, float occlusionFloor = 0.05f, bool autoDiscoverPortals = false)
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

        // 4. AUTHORED PORTALS — the primary source of truth.
        // A portal entity carries the two regions it joins and the size of the opening. These are placed
        // by hand at the actual doorway, so they must be processed BEFORE any automatic guessing.
        int authoredCount = 0;
        foreach (var def in entities)
        {
            if (def.Portal.ApertureSize <= 0) continue;

            int rA = def.Portal.RegionAId;
            int rB = def.Portal.RegionBId;

            // Unlinked portal (both ends identical — typically the prefab default, or an author who
            // dropped a portal in a doorway without naming the rooms): probe the voxel grid outward
            // from the opening and take the first two distinct regions found.
            if (rA == rB)
            {
                Vector3 forward = Vector3.Transform(Vector3.UnitZ, def.Transform.Rotation);
                Vector3[] searchDirs = { forward, -forward, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY };

                var foundRegions = new List<int>();
                foreach (var dir in searchDirs)
                {
                    // One sample just past the opening on each side. The octree answers GlobalRegionId
                    // for anything outside a room, so a single sample is enough and is deterministic.
                    int r = grid.GetRegionAt(def.Transform.Position + dir * 0.5f);
                    if (!foundRegions.Contains(r)) foundRegions.Add(r);
                    if (foundRegions.Count >= 2) break;
                }

                if (foundRegions.Count >= 2) { rA = foundRegions[0]; rB = foundRegions[1]; }
                else if (foundRegions.Count == 1) { rA = foundRegions[0]; rB = AcousticConstants.GlobalRegionId; }

                Console.WriteLine(rA != rB
                    ? $"[AcousticMap] Portal {def.EntityId} had no region link; probed the voxel grid and joined region {rA} to {rB}."
                    : $"[AcousticMap] WARNING: portal {def.EntityId} at {def.Transform.Position} has no region link and probing found no rooms — it will be ignored. Set RegionAId/RegionBId on the map entity.");
            }

            if (rA == rB) continue;

            var portalComp = def.Portal;
            portalComp.RegionAId = rA;
            portalComp.RegionBId = rB;
            acousticMap.Portals[def.EntityId] = (portalComp, def.Transform.Position);
            authoredCount++;
        }

        // 5. UNDESCRIBED BOUNDARY AUDIT (and, only on request, synthesis).
        // Report every region boundary the map did not describe. Optionally fill it with a guessed portal
        // at the centre of the face — see the autoDiscoverPortals remarks: the guess is almost never where
        // the real doorway is, and a portal on the wrong wall is exactly what makes a room's reverb arrive
        // from the wrong direction. Reporting is unconditional; synthesizing is not.
        var linkedPairs = new HashSet<(int, int)>();
        foreach (var p in acousticMap.Portals.Values)
            linkedPairs.Add(PairKey(p.Portal.RegionAId, p.Portal.RegionBId));

        int virtualPortalId = -1000;
        int discoveredCount = 0;

        foreach (var r1 in acousticMap.Regions.Keys)
        {
            if (r1 == acousticMap.GlobalEnvironmentId) continue;
            if (!acousticMap.RegionPositions.TryGetValue(r1, out var pos1)) continue;
            var size1 = acousticMap.Regions[r1].RoomSize;

            // D: Use rotation-aware face normals so buildings rotated away from cardinal axes
            // have their actual wall faces probed, not just the world-axis directions.
            // Each face is probed just beyond ITS OWN half-extent — using the largest dimension for all
            // six faces (as this did previously) puts the probe metres past a thin room's short faces.
            Quaternion regionRot = acousticMap.RegionRotations.GetValueOrDefault(r1, Quaternion.Identity);
            (Vector3 dir, float halfExtent)[] faces =
            {
                (Vector3.Normalize(Vector3.Transform(Vector3.UnitX, regionRot)),  size1.X / 2f),
                (Vector3.Normalize(Vector3.Transform(-Vector3.UnitX, regionRot)), size1.X / 2f),
                (Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, regionRot)),  size1.Z / 2f),
                (Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, regionRot)), size1.Z / 2f),
                (Vector3.UnitY,  size1.Y / 2f),   // Vertical faces are never rotated horizontally
                (-Vector3.UnitY, size1.Y / 2f)
            };

            foreach (var (dir, halfExtent) in faces)
            {
                Vector3 edgePos = pos1 + (dir * (halfExtent + 0.5f));
                int r2 = grid.GetRegionAt(edgePos);
                if (r2 == r1 || r2 == 0) continue; // not a boundary

                var key = PairKey(r1, r2);
                if (!linkedPairs.Add(key)) continue; // already described (authored or discovered)

                string nameA = acousticMap.Regions.TryGetValue(r1, out var ra) ? ra.FriendlyName : r1.ToString();
                string nameB = r2 == acousticMap.GlobalEnvironmentId ? "Outside"
                    : acousticMap.Regions.TryGetValue(r2, out var rb) ? rb.FriendlyName : r2.ToString();

                if (!autoDiscoverPortals)
                {
                    Console.WriteLine($"[AcousticMap] NOTE: '{nameA}' ({r1}) borders '{nameB}' ({r2}) with no portal authored, " +
                                      $"so the two are not acoustically coupled. If there is a real opening there, add a portal " +
                                      $"entity at the doorway with RegionAId={r1}, RegionBId={r2}.");
                    continue;
                }

                Console.WriteLine($"[AcousticMap] WARNING: no portal authored between '{nameA}' ({r1}) and '{nameB}' ({r2}). " +
                                  $"Guessing one at the centre of a face, {edgePos} — reverb and occlusion will arrive from the wrong direction. " +
                                  $"Add a portal entity at the real doorway with RegionAId={r1}, RegionBId={r2}.");

                acousticMap.Portals[virtualPortalId--] = (
                    new PortalComponent { RegionAId = r1, RegionBId = r2, ApertureSize = 2.0f },
                    edgePos);
                discoveredCount++;
            }
        }

        Console.WriteLine($"[AcousticMap] {acousticMap.Regions.Count - 1} region(s), {authoredCount} authored portal(s), {discoveredCount} guessed.");

        return acousticMap;
    }

    /// <summary>Order-independent key for a region boundary, so A→B and B→A are the same pair.</summary>
    private static (int, int) PairKey(int a, int b) => a <= b ? (a, b) : (b, a);

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
