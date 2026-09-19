using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
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
    public List<AcousticPathData> CalculateAcousticPaths(WorldSnapshot world, int entityId, Vector3 listenerPos, Vector3 sourcePos, bool isImportant = true)
    {
        var rawResults = new List<AcousticPathData>();
        var localPlayer = world.Entities.Values.FirstOrDefault(e => e.Definition.Type == EntityType.Player && Vector3.Distance(e.Transform.Position, listenerPos) < 2.0f);
        int localPlayerId = (localPlayer.Id != 0) ? localPlayer.Id : AcousticConstants.GlobalRegionId;

        // 1. Main Direct Path (includes portal diffraction)
        rawResults.Add(CalculateMainPath(world, entityId, listenerPos, sourcePos, localPlayerId));

        // 2. Early reflections — the copies the surfaces send back.
        //
        // ONE model, shared with the Steam Audio path (see AsyncAcousticWorker.AddEarlyReflections),
        // because two reflection generators is two answers to the same question. What used to be here
        // was a recursive ray solve that jittered every surface normal with `new Random(entityId +
        // DateTime.Now.Millisecond)` — a fresh seed on every call, so a wall's reflection moved
        // slightly every frame and no two ticks agreed about where it was. It then merged whatever
        // came out by proximity to hide the scatter. Both are gone: an image source is exact, it is
        // the same every tick for the same geometry, and it needs no merging because a surface
        // produces one arrival by construction.
        _reflectionScratch ??= new List<EarlyReflections.Arrival>();
        EarlyReflections.Find(sourcePos, listenerPos, ReflectionSolids(world), _reflectionScratch);
        for (int i = 0; i < _reflectionScratch.Count; i++)
        {
            var a = _reflectionScratch[i];
            // Same rule as the simulator path: a fused arrival is the room, not an event. See
            // EarlyReflections.FusionSeconds.
            if (!EarlyReflections.IsSeparateEvent(a)) continue;
            rawResults.Add(new AcousticPathData
            {
                IsReflection = true,
                ReflectionId = a.SurfaceId,
                ReflectionIndex = i,
                ApparentPosition = a.ImagePosition,
                EffectiveDistance = a.PathLength,
                ReflectionDelayMs = a.ExtraDelaySeconds * 1000f,
                Occlusion = 0f,
                EqLow = a.GainLow,
                EqMid = a.GainMid,
                EqHigh = a.GainHigh,
                MaterialAbsorption = 1f - a.GainMid,
                Scattering = a.Scattering,
                Spread = a.Scattering * 90f,
                ApertureFactor = 1f,
                RoomGain = 1f,
                RegionId = GetRegionAt(world, listenerPos),
            });
        }

        return rawResults;
    }

    private List<EarlyReflections.Arrival>? _reflectionScratch;
    private object? _reflectionSolidsFor;
    private IReadOnlyList<EarlyReflections.Solid> _reflectionSolids = System.Array.Empty<EarlyReflections.Solid>();

    /// <summary>The world's solid boxes as the reflection model wants them, rebuilt only when the
    /// acoustic map changes. The same definition of "audio geometry" the simulator's scene uses, so the
    /// two paths cannot disagree about what a wall is.</summary>
    private IReadOnlyList<EarlyReflections.Solid> ReflectionSolids(WorldSnapshot world)
    {
        if (ReferenceEquals(_reflectionSolidsFor, world.AcousticMap) && _reflectionSolids.Count > 0)
            return _reflectionSolids;
        var boxes = OpenFPS.Client.Core.AudioEngine.SteamAudio.SteamAudioScene.BoxesFromWorld(world);
        var solids = new List<EarlyReflections.Solid>(boxes.Count);
        foreach (var b in boxes) solids.Add(new EarlyReflections.Solid(b.Center, b.Size, b.Rotation, b.Material));
        _reflectionSolids = solids;
        _reflectionSolidsFor = world.AcousticMap;
        return solids;
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

        // One law, in AudioPhysics, so the hand-rolled tracer and the Steam Audio path cannot drift.
        // (The absent-multiplier trap is handled in there: an unset field means "no scaling", and the
        // old Math.Max(0.1f, ...) turned it into a TEN-FOLD increase in the absorption distance.)
        // Indoors is a closed boundary around the listener, not the mere fact that the map has a name
        // for where they are standing. The speedway named its sectors and every car on it was suddenly
        // being heard "indoors": the reference distance doubles in here, so two hundred metres of
        // track kept its high frequencies and the field read as small and close.
        int listenerRegionId = GetRegionAt(world, listenerPos);
        bool listenerEnclosed = world.AcousticMap != null
            && world.AcousticMap.Regions.TryGetValue(listenerRegionId, out var listenerRegion)
            && RoomAcoustics.IsEnclosure(listenerRegion);

        float airAbsorption = AudioPhysics.AirAbsorptionFor(
            finalEffectiveDist, world.Humidity, world.Temperature, world.AirPressure,
            world.AirAbsorptionMultiplier,
            listenerIndoors: listenerEnclosed);

        int regionId = GetRegionAt(world, sourcePos + new Vector3(0, 0.5f, 0));
        float roomGain = 1.0f;
        // The small-room lift is a pressure build-up between surfaces. No surfaces, no lift — and a
        // region the size of an infield read as a room was taking 6 dB off every car on the map.
        if (world.AcousticMap != null && world.AcousticMap.Regions.TryGetValue(regionId, out var region)
            && RoomAcoustics.IsEnclosure(region))
        {
            float volume = region.RoomSize.X * region.RoomSize.Y * region.RoomSize.Z;
            roomGain = Math.Clamp(1000.0f / Math.Max(50.0f, volume), 0.5f, 4.0f);
            if (region.IsIndoor && listenerRegionId == regionId) roomGain *= 1.25f;
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
