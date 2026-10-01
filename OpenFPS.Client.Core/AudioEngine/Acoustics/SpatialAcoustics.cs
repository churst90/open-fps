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
    /// <summary>The geometry queries this answers from, for a caller that needs one plain ray.</summary>
    public SpatialService Spatial => _spatial;

    public SpatialAcoustics() : this(new SpatialService()) { }

    public SpatialAcoustics(SpatialService spatial)
    {
        _spatial = spatial;
    }

    /// <summary>
    /// Calculates the complex acoustic path sound takes through the world.
    /// </summary>
    public List<AcousticPathData> CalculateAcousticPaths(WorldSnapshot world, int entityId, Vector3 listenerPos, Vector3 sourcePos)
    {
        var rawResults = new List<AcousticPathData>();
        var localPlayer = world.Entities.Values.FirstOrDefault(e => e.Definition.Type == EntityType.Player && Vector3.Distance(e.Transform.Position, listenerPos) < 2.0f);
        int localPlayerId = (localPlayer.Id != 0) ? localPlayer.Id : AcousticConstants.GlobalRegionId;

        // 1. Main path: through the walls, or by the openings where that delivers more
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
    public IReadOnlyList<EarlyReflections.Solid> ReflectionSolids(WorldSnapshot world)
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

    // ── The routes through the openings ─────────────────────────────────────────────────────────
    //
    // One model for every voice: the occlusion worker builds it with each scene (the leaves where they
    // stand) and hands it here, so a footstep, a bird or a word asks the same graph a car does. Until
    // it has — or where there is no simulator at all — one is built here from the world: at once for a
    // new map, and in the background when a door leaf near the listener has moved, the old graph
    // answering until the new one is ready (the city's takes a few hundred milliseconds, and on the
    // city walkers open doors all day). The same rules as the worker's door rebuilds.
    private volatile OpeningRoutes? _routes;
    private volatile OpeningRoutes? _localRoutes;
    private object? _localRoutesMap;
    private long _localRoutesDoors;
    private long _localRoutesStartedAt;
    private System.Threading.Tasks.Task? _localRoutesBuild;
    private readonly object _localRoutesLock = new();
    /// <summary>Only doors this near the listener count, metres (as AsyncAcousticWorker's).</summary>
    private const float DoorNearMetres = 50f;
    /// <summary>The least time between two door rebuilds while a door swings, seconds (as the worker's).</summary>
    private const double DoorRebuildSeconds = 0.3;

    /// <summary>The graph the occlusion worker built with its scene. Null hands it back to the local one.</summary>
    public OpeningRoutes? Routes { get => _routes; set => _routes = value; }

    /// <summary>The graph to ask: the worker's, or one built from this world, with the door leaves near
    /// <paramref name="listener"/> (all of them when not given) where they stand.</summary>
    public OpeningRoutes? RoutesFor(WorldSnapshot world, Vector3? listener = null)
    {
        var shared = _routes;
        if (shared != null) return shared;
        if (world.AcousticMap == null) return null;
        long doors = DoorPoses(world, listener);
        lock (_localRoutesLock)
        {
            var current = _localRoutes;
            if (current == null || !ReferenceEquals(_localRoutesMap, world.AcousticMap))
            {
                current = BuildLocalRoutes(world);
                _localRoutes = current;
                _localRoutesMap = world.AcousticMap;
                _localRoutesDoors = doors;
                return current;
            }
            if (_localRoutesDoors == doors || _localRoutesBuild is { IsCompleted: false }) return current;
            long now = Environment.TickCount64;
            if (now - _localRoutesStartedAt < DoorRebuildSeconds * 1000) return current;
            _localRoutesStartedAt = now;
            _localRoutesDoors = doors;
            var map = world.AcousticMap;
            _localRoutesBuild = System.Threading.Tasks.Task.Run(() =>
            {
                var built = BuildLocalRoutes(world);
                lock (_localRoutesLock)
                    if (ReferenceEquals(_localRoutesMap, map)) _localRoutes = built;
            });
            return current;
        }
    }

    private OpeningRoutes BuildLocalRoutes(WorldSnapshot world)
        => OpeningGraph.Build(world, OpenFPS.Client.Core.AudioEngine.SteamAudio.SteamAudioScene.BoxesFromWorld(world),
                              p => GetRegionAt(world, p));

    /// <summary>Where the door leaves near the listener stand, folded into one number.</summary>
    private static long DoorPoses(WorldSnapshot world, Vector3? listener)
    {
        long doors = 17;
        foreach (var snap in world.Entities.Values)
        {
            if (!OpeningGraph.IsDoorLeaf(snap.Definition)) continue;
            var p = snap.Transform.Position; var q = snap.Transform.Rotation;
            if (listener is { } l && Vector3.DistanceSquared(p, l) > DoorNearMetres * DoorNearMetres) continue;
            doors = doors * 31 + snap.Id;
            doors = doors * 31 + (long)MathF.Round(p.X * 100f); doors = doors * 31 + (long)MathF.Round(p.Z * 100f);
            doors = doors * 31 + (long)MathF.Round(q.Y * 100f); doors = doors * 31 + (long)MathF.Round(q.W * 100f);
        }
        return doors;
    }

    private AcousticPathData CalculateMainPath(WorldSnapshot world, int entityId, Vector3 listenerPos, Vector3 sourcePos, int localPlayerId)
    {
        // C5: Both the emitter entity and the local player entity are excluded from all rays,
        // ensuring symmetric occlusion that does not depend on the order of comparison arms.
        int ignoreA = entityId != AcousticConstants.GlobalRegionId ? entityId : -1;
        int ignoreB = localPlayerId != AcousticConstants.GlobalRegionId ? localPlayerId : -1;
        _spatial.GetMultiPointOcclusionData(world, listenerPos, sourcePos, out float directOcclusion, out float directBleed, out float eqL, out float eqM, out float eqH, ignoreA, ignoreB);

        float directDist = Vector3.Distance(listenerPos, sourcePos);
        Vector3 apparentPos = sourcePos;
        // Through the walls: the band gains themselves (SpatialService, WallTransmission).
        float gainL = eqL, gainM = eqM, gainH = eqH;

        // ...and by the openings, where those deliver more (OpeningRoutes): the same rule, and the same
        // graph, as the simulator's path.
        int listenerRegionId = GetRegionAt(world, listenerPos);
        var routes = RoutesFor(world, listenerPos);
        if (routes != null
            && routes.Route(sourcePos, GetRegionAt(world, sourcePos), listenerPos, listenerRegionId, out var route))
        {
            var g = OpeningRoutes.Better(new Vector3(gainL, gainM, gainH), route, out bool routeWins);
            gainL = g.X; gainM = g.Y; gainH = g.Z;
            // Heard from the opening it arrives through, at the source's own distance: the level has
            // already paid for the longer way round.
            Vector3 toOpening = route.Apparent - listenerPos;
            if (routeWins && toOpening.LengthSquared() > 1e-6f)
                apparentPos = listenerPos + Vector3.Normalize(toOpening) * directDist;
        }

        // What the air took, per band (ISO 9613-1): the same law the Steam Audio path uses.
        var air = AudioPhysics.AirLossDb(directDist, world.Humidity, world.Temperature,
                                         world.AirPressure, world.AirAbsorptionMultiplier);

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

        // The mixer takes each band's gain as the WHOLE of what the path does to that band, applied
        // once, and these are those gains: what came through the walls, or by the openings where that
        // delivers more. The broadband occlusion is read off them, for whatever ranks voices by it.
        float occlusion = Math.Min(Math.Min(directOcclusion, 1f - MathF.Max(gainL, MathF.Max(gainM, gainH))),
                                   AcousticConstants.OcclusionCap);
        var pathData = new AcousticPathData(occlusion, apparentPos, directDist, 0f, 1f, directBleed, regionId,
            gainL, gainM, gainH);
        pathData.RoomGain = roomGain;
        (pathData.AirLowDb, pathData.AirMidDb, pathData.AirHighDb) = air;
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
