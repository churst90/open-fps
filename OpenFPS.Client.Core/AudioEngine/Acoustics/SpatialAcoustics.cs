using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.Core;
using OpenFPS.Client.AudioEngine.Data;

namespace OpenFPS.Client.AudioEngine.Acoustics;

/// <summary>
/// Responsibility: High-level acoustic path simulation logic. 
/// Translates raw geometric data from SpatialService into acoustic parameters (Occlusion, Diffraction).
/// </summary>
public class SpatialAcoustics
{
    private readonly SpatialService _spatial;
    private readonly AcousticPathfinder _pathfinder;

    public SpatialAcoustics() : this(new SpatialService()) { }

    public SpatialAcoustics(SpatialService spatial)
    {
        _spatial = spatial;
        _pathfinder = new AcousticPathfinder(spatial);
    }

    /// <summary>
    /// Calculates the complex acoustic path sound takes through the world.
    /// </summary>
    private readonly Random _rng = new();

    public List<AcousticPathData> CalculateAcousticPaths(WorldSnapshot world, int entityId, Vector3 listenerPos, Vector3 sourcePos, bool isImportant = true)
    {
        var rawResults = new List<AcousticPathData>();
        var localPlayer = world.Entities.Values.FirstOrDefault(e => e.Definition.Type == EntityType.Player && Vector3.Distance(e.Transform.Position, listenerPos) < 2.0f);
        int localPlayerId = (localPlayer.Id != 0) ? localPlayer.Id : AcousticConstants.GlobalRegionId;

        // 1. Main Direct Path (includes portal diffraction)
        rawResults.Add(CalculateMainPath(world, entityId, listenerPos, sourcePos, localPlayerId));

        // 2. High-Order Recursive Reflections
        int maxBounces = isImportant ? AcousticConstants.MaxReflectionOrder : 1;
        var r = new Random(entityId + DateTime.Now.Millisecond);

        _spatial.GetReflectionData(world, listenerPos, 40.0f, out var primarySurfaces);

        foreach (var refl in primarySurfaces)
        {
            float jitter = 0.05f;
            Vector3 jitteredNormal = Vector3.Normalize(refl.normal + new Vector3(
                (float)(r.NextDouble() * 2 - 1) * jitter,
                (float)(r.NextDouble() * 2 - 1) * jitter,
                (float)(r.NextDouble() * 2 - 1) * jitter
            ));

            Vector3 bounce1Point = listenerPos - (refl.normal * refl.distance);

            if (TrySolveRecursivePath(world, listenerPos, bounce1Point, jitteredNormal, refl.absorption, sourcePos, out var path, maxBounces, entityId))
            {
                // --- PHASE 2: Stable Reflection Identity ---
                // Calculate a stable ID based on the surface normal and position 
                // to prevent the "mono clump" of re-triggering looping sounds.
                int spatialHash = (int)(refl.normal.X * 100) ^ (int)(refl.normal.Z * 100) ^ (int)(bounce1Point.X * 10) ^ (int)(bounce1Point.Z * 10);
                path.ReflectionId = Math.Abs(spatialHash);

                // Calculate Perceptual Spread based on distance and material scattering
                float distToWall = Vector3.Distance(listenerPos, bounce1Point);
                float angularWidth = MathF.Atan2(5.0f, Math.Max(1.0f, distToWall)) * (180.0f / MathF.PI);
                
                // --- PHASE 3: Scattering-Based Spread ---
                // Pinpoint reflections for smooth materials (Glass/Metal), wide for rough ones (Brick/Dirt)
                float scatteringWidth = path.Scattering * 45.0f; 
                path.Spread = Math.Clamp(angularWidth + scatteringWidth + (path.ReflectionIndex * 20.0f), AcousticConstants.ReflectionMinSpread, AcousticConstants.ReflectionMaxSpread);
                
                rawResults.Add(path);
            }
        }

        // 3. REFLECTION MERGING (Robustness Fix)
        // Group reflections that are physically close to prevent the "Point Source" feel of modular boxes.
        var mergedResults = new List<AcousticPathData>();
        if (rawResults.Count > 0) mergedResults.Add(rawResults[0]); // Keep direct path

        var reflectionCandidates = rawResults.Skip(1).ToList();
        while (reflectionCandidates.Count > 0)
        {
            var current = reflectionCandidates[0];
            reflectionCandidates.RemoveAt(0);

            // Find neighbors within merging distance
            var neighbors = reflectionCandidates.Where(n => Vector3.Distance(current.ApparentPosition, n.ApparentPosition) < AcousticConstants.ReflectionMergeDistance).ToList();
            
            if (neighbors.Count > 0)
            {
                // Merge into a single volumetric reflection
                Vector3 avgPos = current.ApparentPosition;
                float totalEnergy = current.MaterialAbsorption;
                float maxEq = current.EqHigh;

                foreach (var n in neighbors)
                {
                    avgPos += n.ApparentPosition;
                    totalEnergy += n.MaterialAbsorption;
                    maxEq = Math.Max(maxEq, n.EqHigh);
                    reflectionCandidates.Remove(n);
                }

                avgPos /= (neighbors.Count + 1);
                current.ApparentPosition = avgPos;
                current.MaterialAbsorption = Math.Min(1.0f, totalEnergy);
                current.EqHigh = maxEq;
                current.Spread = Math.Min(AcousticConstants.ReflectionMaxSpread, current.Spread + (neighbors.Count * 15.0f));
            }
            
            mergedResults.Add(current);
            if (mergedResults.Count >= 5) break; // Hard limit on active emitters per sound
        }

        return mergedResults;
    }

    private bool TrySolveRecursivePath(WorldSnapshot world, Vector3 listenerPos, Vector3 firstHitPoint, Vector3 firstNormal, float firstAbsorb, Vector3 sourcePos, out AcousticPathData path, int maxBounces, int entityId = -1)
    {
        path = default;
        Vector3 currentStart = firstHitPoint;
        Vector3 currentNormal = firstNormal;
        float cumulativeAbsorb = firstAbsorb;
        float totalDist = Vector3.Distance(listenerPos, firstHitPoint);
        
        // --- PHASE 1 FIX: Correct Mirror Position Logic ---
        // Mirror the source across the FIRST wall immediately
        Vector3 mirrorPos = Vector3.Reflect(sourcePos - firstHitPoint, firstNormal) + firstHitPoint; 

        int listenerRegionId = GetRegionAt(world, listenerPos);
        int sourceRegionId = GetRegionAt(world, sourcePos);

        for (int bounce = 1; bounce <= maxBounces; bounce++)
        {
            float occlusion = _spatial.GetOcclusionFactor(world, currentStart, sourcePos, out _, -1);
            if (occlusion < 0.2f) 
            {
                totalDist += Vector3.Distance(currentStart, sourcePos);
                float directDist = Vector3.Distance(listenerPos, sourcePos);
                float delayMs = (totalDist - directDist) / 0.343f;
                if (delayMs < 1.0f) return false;

                // --- PHASE 2 FIX: Source Directivity (Cone Filtering) ---
                float coneMultiplier = 1.0f;
                if (entityId != -1 && world.Entities.TryGetValue(entityId, out var sourceSnap))
                {
                    var def = sourceSnap.Definition.SoundEmitter;
                    if (def.ConeInsideAngle < 360f)
                    {
                        Vector3 sourceToWall = Vector3.Normalize(currentStart - sourcePos);
                        Vector3 sourceForward = Vector3.Transform(Vector3.UnitZ, sourceSnap.Transform.Rotation);
                        float dot = Vector3.Dot(sourceForward, sourceToWall);
                        float angle = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * (180.0f / MathF.PI);
                        
                        if (angle > def.ConeInsideAngle)
                        {
                            coneMultiplier = def.ConeOutsideVolume;
                        }
                    }
                }

                // --- PHASE 3 FIX: Incidence-Based Absorption ---
                Vector3 rayToWall = Vector3.Normalize(currentStart - (bounce == 1 ? listenerPos : mirrorPos));
                float incidenceDot = Math.Abs(Vector3.Dot(rayToWall, currentNormal));
                float incidenceMuffle = Math.Clamp(incidenceDot, 0.4f, 1.0f); // Glancing blows (low dot) keep more high-end

                int bounceRegionId = GetRegionAt(world, firstHitPoint);
                float portalPenalty = 0;

                if (bounceRegionId != listenerRegionId || sourceRegionId != listenerRegionId)
                {
                    var pPath = _pathfinder.FindPath(world, listenerPos, sourcePos);
                    if (!pPath.Found) return false; 
                    portalPenalty = Math.Clamp(1.0f - (pPath.MinAperture * 2.0f), 0.0f, 0.4f);
                }

                float remainingEnergy = (1.0f - cumulativeAbsorb - portalPenalty) * coneMultiplier;
                if (remainingEnergy < AcousticConstants.ReflectionEnergyThreshold) return false;

                path = new AcousticPathData(
                    Math.Min(0.95f, 0.3f + portalPenalty), 
                    mirrorPos, totalDist, remainingEnergy, 1.0f, 0.1f, totalDist / 250.0f, AcousticConstants.GlobalRegionId
                );
                path.IsReflection = true;
                path.ReflectionDelayMs = delayMs;
                
                // --- PHASE 4 FIX: Material-Specific Decay ---
                path.EqHigh = Math.Clamp((1.0f - cumulativeAbsorb) * incidenceMuffle, 0.05f, 1.0f);
                path.MaterialAbsorption = cumulativeAbsorb; 
                path.ReflectionIndex = bounce;
                return true;
            }

            if (bounce == maxBounces) break;

            Vector3 dirToSource = Vector3.Normalize(sourcePos - currentStart);
            bool hit = false;
            float hitDist = 0;
            Vector3 nextNormal = Vector3.Zero;
            string material = "Generic";

            if (bounce >= 2 && world.AcousticMap != null)
            {
                float step = world.AcousticMap.VoxelGrid.MinVoxel;
                for (float d = step; d < 30.0f; d += step)
                {
                    Vector3 p = currentStart + currentNormal * 0.05f + dirToSource * d;
                    int r = world.AcousticMap.VoxelGrid.GetRegionAt(p);
                    if (r != AcousticConstants.GlobalRegionId && r != GetRegionAt(world, currentStart))
                    {
                        hit = true;
                        hitDist = d;
                        nextNormal = -dirToSource; 
                        material = "Concrete"; 
                        break;
                    }
                }
            }
            else
            {
                hit = _spatial.RaycastMaterial(world, currentStart + currentNormal * 0.05f, dirToSource, 30.0f, out hitDist, out nextNormal, out material);
            }

            if (hit)
            {
                currentStart = currentStart + dirToSource * hitDist;
                currentNormal = nextNormal;
                var props = AcousticRegistry.GetProperties(material);
                cumulativeAbsorb += props.Absorption;
                totalDist += hitDist;
                // Recursive Mirroring
                mirrorPos = Vector3.Reflect(mirrorPos - currentStart, currentNormal) + currentStart;

                if (cumulativeAbsorb > (1.0f - AcousticConstants.ReflectionEnergyThreshold)) break; 
            }
            else break;
        }

        return false;
    }

    private AcousticPathData CalculateMainPath(WorldSnapshot world, int entityId, Vector3 listenerPos, Vector3 sourcePos, int localPlayerId)
    {
        // C5: Both the emitter entity and the local player entity are excluded from all rays,
        // ensuring symmetric occlusion that does not depend on the order of comparison arms.
        int ignoreA = entityId != AcousticConstants.GlobalRegionId ? entityId : -1;
        int ignoreB = localPlayerId != AcousticConstants.GlobalRegionId ? localPlayerId : -1;
        _spatial.GetMultiPointOcclusionData(world, listenerPos, sourcePos, out float directOcclusion, out float directBleed, out float eqL, out float eqM, out float eqH, ignoreA, ignoreB);

        float directDist = Vector3.Distance(listenerPos, sourcePos);
        var portalPath = _pathfinder.FindPath(world, listenerPos, sourcePos);

        float finalOcclusion = directOcclusion;
        Vector3 apparentPos = sourcePos;
        float finalEffectiveDist = directDist;
        float finalAperture = 1.0f;
        float finalBleed = directBleed;
        float finalEqL = eqL, finalEqM = eqM, finalEqH = eqH;

        if (portalPath.Found)
        {
            _spatial.GetOcclusionData(world, listenerPos, portalPath.ApparentPos, out float portalDirectOcclusion, out _, out _, out _, out _, localPlayerId);
            
            float detourFactor = portalPath.EffectiveDist / Math.Max(0.1f, directDist);
            float detourPenalty = Math.Clamp((detourFactor - 1.0f) * AcousticConstants.DetourPenaltyMultiplier, 0.0f, AcousticConstants.DetourPenaltyCap);
            
            float distToPortal = Vector3.Distance(listenerPos, portalPath.ApparentPos);
            float apertureRatio = portalPath.MinAperture / Math.Max(1.0f, distToPortal);
            float aperturePenalty = Math.Clamp(1.0f - (apertureRatio * 1.5f), 0.0f, 0.7f);
            
            float indirectOcclusion = Math.Clamp(portalDirectOcclusion + detourPenalty + (aperturePenalty * AcousticConstants.AperturePenaltyMultiplier), 0.0f, 1.0f);

            // --- PHASE 3: Aperture Choking ---
            // If the Doorway path is significantly clearer than the wall path,
            // we "snap" to the doorway completely to avoid muffled double-audio.
            if (indirectOcclusion < (directOcclusion - 0.15f) || directOcclusion > 0.8f)
            {
                finalOcclusion = indirectOcclusion; 
                float morphFactor = Math.Clamp(distToPortal / 5.0f, 0f, 1f);
                apparentPos = Vector3.Lerp(portalPath.ApparentPos, portalPath.ApparentPos + new Vector3(0, 0.5f, 0), morphFactor * 0.2f);
                
                finalEffectiveDist = portalPath.EffectiveDist; 
                finalAperture = Math.Clamp(apertureRatio * 2.0f, 0.1f, 1.0f);
                
                // --- PHASE 3: Aperture Choking Logic ---
                // Reduce volume if the sound is passing through a small aperture.
                // Even with a clear LOS, a 1m door cannot pass the same energy as a missing wall.
                float apertureChoke = Math.Clamp(portalPath.MinAperture / 2.0f, 0.3f, 1.0f);
                finalBleed = directBleed * 0.2f * apertureChoke; 
                
                finalEqL = 1.0f - (portalPath.MuffleL * 0.5f); 
                finalEqM = 1.0f - (portalPath.MuffleM * 0.7f); 
                finalEqH = 1.0f - portalPath.MuffleH;
            }
        }

        float airAbsMultiplier = world.AirAbsorptionMultiplier;
        // Normalize air pressure to standard atmosphere (1013.25 mbar).
        // High altitude (low pressure) → sound scatters more → shorter effective absorption distance.
        float pressureNorm = Math.Clamp(world.AirPressure / 1013.25f, 0.5f, 2.0f);
        float effectiveAbsorbDist = Math.Max(50.0f,
            (AcousticConstants.AirAbsorptionReferenceDist - (world.Humidity * 100f) + (Math.Max(0, 20f - world.Temperature) * 2f))
            / Math.Max(0.1f, airAbsMultiplier)
            * pressureNorm);
        
        if (GetRegionAt(world, listenerPos) != AcousticConstants.GlobalRegionId) effectiveAbsorbDist *= 2.0f;
        float airAbsorption = Math.Clamp((finalEffectiveDist - AcousticConstants.AirAbsorptionMinDist) / effectiveAbsorbDist, 0.0f, AcousticConstants.AirAbsorptionMaxMuffle);

        int regionId = GetRegionAt(world, sourcePos + new Vector3(0, 0.5f, 0));
        float roomGain = 1.0f;
        if (world.AcousticMap != null && regionId != AcousticConstants.GlobalRegionId && world.AcousticMap.Regions.TryGetValue(regionId, out var region))
        {
            float volume = region.RoomSize.X * region.RoomSize.Y * region.RoomSize.Z;
            roomGain = Math.Clamp(1000.0f / Math.Max(50.0f, volume), 0.5f, 4.0f);
            if (region.IsIndoor && GetRegionAt(world, listenerPos) == regionId) roomGain *= 1.25f;
        }

        finalOcclusion = Math.Min(finalOcclusion, AcousticConstants.OcclusionCap);
        var pathData = new AcousticPathData(finalOcclusion, apparentPos, finalEffectiveDist, 0f, finalAperture, finalBleed, airAbsorption, regionId, finalEqL, finalEqM, finalEqH);
        pathData.RoomGain = roomGain;
        return pathData;
    }

    public int GetRegionAt(WorldSnapshot world, Vector3 position) 
    {
        return _spatial.GetRegionAt(world, position);
    }

    public AcousticPathData CalculateAcousticPath(WorldSnapshot world, int entityId, Vector3 listenerPos, Vector3 sourcePos)
    {
        var localPlayer = world.Entities.Values.FirstOrDefault(e => e.Definition.Type == EntityType.Player && Vector3.Distance(e.Transform.Position, listenerPos) < 2.0f);
        return CalculateMainPath(world, entityId, listenerPos, sourcePos, localPlayer.Id != 0 ? localPlayer.Id : AcousticConstants.GlobalRegionId);
    }
}
