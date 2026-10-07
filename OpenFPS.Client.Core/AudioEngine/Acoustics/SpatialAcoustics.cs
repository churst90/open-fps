using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common.Components;
using OpenFPS.Client.Core;
using OpenFPS.Client.AudioEngine.Data;

namespace OpenFPS.Client.AudioEngine.Acoustics;

/// <summary>
/// The acoustic paths a voice takes without the Steam Audio simulator: through the walls, by the
/// openings, and off the surfaces, from SpatialService's geometry.
/// </summary>
public class SpatialAcoustics
{
    private readonly SpatialService _spatial;
    /// <summary>For a caller that needs one plain ray.</summary>
    public SpatialService Spatial => _spatial;

    public SpatialAcoustics() : this(new SpatialService()) { }

    public SpatialAcoustics(SpatialService spatial)
    {
        _spatial = spatial;
    }

    /// <summary>The main path and the separate early reflections.</summary>
    public List<AcousticPathData> CalculateAcousticPaths(WorldSnapshot world, int entityId, Vector3 listenerPos, Vector3 sourcePos)
    {
        var rawResults = new List<AcousticPathData>();
        var localPlayer = world.Entities.Values.FirstOrDefault(e => e.Definition.Type == EntityType.Player && Vector3.Distance(e.Transform.Position, listenerPos) < 2.0f);
        int localPlayerId = (localPlayer.Id != 0) ? localPlayer.Id : AcousticConstants.GlobalRegionId;

        rawResults.Add(CalculateMainPath(world, entityId, listenerPos, sourcePos, localPlayerId));

        // Early reflections: one image-source model, shared with the Steam Audio path
        // (AsyncAcousticWorker.AddEarlyReflections), the same every tick for the same geometry. A ray
        // solve seeded from the clock here once moved every wall's reflection from frame to frame.
        _reflectionScratch ??= new List<EarlyReflections.Arrival>();
        FindReflections(world, sourcePos, listenerPos, _reflectionScratch, 343.0f);
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
    private long _reflectionSolidsVersion;
    private IReadOnlyList<EarlyReflections.Solid> _reflectionSolids = System.Array.Empty<EarlyReflections.Solid>();

    // The occlusion worker's acoustic triangle world (AcousticGeometry, geometry stage 2): the boxes
    // ReflectionSolids lists, as a tree the image-source search asks what is near a path, instead of
    // testing every leg against every near solid (a one-off call in dense woods grew with the square of
    // what was near). A snapshot of another map is answered from the list until the worker catches up.
    private sealed record ReflectionScene(OpenFPS.Common.Geometry.TriangleWorld World, object? Map);
    private volatile ReflectionScene? _reflectionWorld;

    public void PublishReflectionWorld(OpenFPS.Common.Geometry.TriangleWorld? world, object? map)
        => _reflectionWorld = world == null ? null : new ReflectionScene(world, map);

    /// <summary>The acoustic triangle world for this snapshot's map, if the worker has one built.</summary>
    public OpenFPS.Common.Geometry.TriangleWorld? ReflectionWorldFor(WorldSnapshot world)
    {
        var r = _reflectionWorld;
        return r != null && OpenFPS.Common.Geometry.TriangleGeometry.Enabled && ReferenceEquals(r.Map, world.AcousticMap) ? r.World : null;
    }

    /// <summary>
    /// The copies of a sound off the surfaces (EarlyReflections.Find): asked of the worker's acoustic
    /// triangle world when it has one for this map, of the list of boxes otherwise.
    /// </summary>
    public void FindReflections(WorldSnapshot world, Vector3 source, Vector3 listener, List<EarlyReflections.Arrival> into,
                                float speedOfSound, int maxOrder = 1, bool separateFirst = false, bool flutter = false, int keep = 0,
                                float maxExtraPathMetres = EarlyReflections.RangeMetres)
    {
        var geo = ReflectionWorldFor(world);
        if (geo != null)
        {
            EarlyReflections.Find(source, listener, geo, into, speedOfSound, maxOrder, separateFirst, flutter, keep, maxExtraPathMetres);
            return;
        }
        var solids = ReflectionSolids(world);
        EarlyReflections.Find(source, listener, solids, into, speedOfSound, maxOrder, separateFirst, flutter, keep, maxExtraPathMetres);
    }

    /// <summary>The world's solid boxes for the reflection model, rebuilt when the map or its geometry
    /// changes: the simulator scene's own boxes, so the two paths agree about what a wall is.</summary>
    public IReadOnlyList<EarlyReflections.Solid> ReflectionSolids(WorldSnapshot world)
    {
        // The version too: tiles of a streamed map arrive and leave under the same map object.
        if (ReferenceEquals(_reflectionSolidsFor, world.AcousticMap) && _reflectionSolidsVersion == world.GeometryVersion
            && _reflectionSolids.Count > 0)
            return _reflectionSolids;
        var boxes = OpenFPS.Client.Core.AudioEngine.SteamAudio.SteamAudioScene.BoxesFromWorld(world);
        var solids = new List<EarlyReflections.Solid>(boxes.Count);
        foreach (var b in boxes) solids.Add(new EarlyReflections.Solid(b.Center, b.Size, b.Rotation, b.Material));
        _reflectionSolids = solids;
        _reflectionSolidsFor = world.AcousticMap;
        _reflectionSolidsVersion = world.GeometryVersion;
        return solids;
    }

    // ── The routes through the openings ─────────────────────────────────────────────────────────
    //
    // One graph for every voice, the occlusion worker's when it has one. Until then, or with no
    // simulator, one is built here: at once for a new map, and in the background when a door near the
    // listener moves, the old graph answering meanwhile (the city's takes a few hundred milliseconds,
    // and its walkers open doors all day). The same rules as the worker's door rebuilds.
    private volatile OpeningRoutes? _routes;
    private volatile OpeningRoutes? _localRoutes;
    private object? _localRoutesMap;
    private long _localRoutesDoors;
    private long _localRoutesVersion;
    private long _localRoutesStartedAt;
    private System.Threading.Tasks.Task? _localRoutesBuild;
    private readonly object _localRoutesLock = new();
    /// <summary>Metres: only doors this near the listener count (as AsyncAcousticWorker's).</summary>
    private const float DoorNearMetres = 50f;
    /// <summary>Seconds between two door rebuilds while a door swings (as the worker's).</summary>
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
                _localRoutesVersion = world.GeometryVersion;
                return current;
            }
            // Tiles arriving or leaving count as a door moving: the graph is rebuilt in the background.
            if (_localRoutesVersion != world.GeometryVersion) { _localRoutesVersion = world.GeometryVersion; _localRoutesDoors = ~doors; }
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
        // The emitter and the local player are both left out of every ray, so occlusion is symmetric.
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
        // The small-room lift is pressure building up between surfaces: no surfaces, no lift (an
        // infield-sized region read as a room took 6 dB off every car on the map).
        if (world.AcousticMap != null && world.AcousticMap.Regions.TryGetValue(regionId, out var region)
            && RoomAcoustics.IsEnclosure(region))
        {
            float volume = region.RoomSize.X * region.RoomSize.Y * region.RoomSize.Z;
            roomGain = Math.Clamp(1000.0f / Math.Max(50.0f, volume), 0.5f, 4.0f);
            if (region.IsIndoor && listenerRegionId == regionId) roomGain *= 1.25f;
        }

        // The mixer takes each band's gain as the whole of what the path does to it, applied once. The
        // broadband occlusion is read off them, for whatever ranks voices by it.
        float occlusion = Math.Min(Math.Min(directOcclusion, 1f - MathF.Max(gainL, MathF.Max(gainM, gainH))),
                                   AcousticConstants.OcclusionCap);
        var pathData = new AcousticPathData(occlusion, apparentPos, directDist, 0f, 1f, directBleed, regionId,
            gainL, gainM, gainH);
        pathData.RoomGain = roomGain;
        (pathData.AirLowDb, pathData.AirMidDb, pathData.AirHighDb) = air;
        return pathData;
    }

    /// <summary>
    /// The place a point is in, for sound: the smallest box that holds it, and in a doorway between two
    /// rooms, the room on its side (OpeningRoutes.RoomInOpening), never the outdoors. Uses the graph
    /// already built, never builds one: the build asks this, about points outside every opening.
    /// </summary>
    public int GetRegionAt(WorldSnapshot world, Vector3 position)
    {
        int region = _spatial.GetRegionAt(world, position);
        if (region != AcousticConstants.GlobalRegionId) return region;
        var routes = _routes ?? _localRoutes;
        return routes != null && RoomInOpening(routes, position, p => _spatial.GetRegionAt(world, p), out int room) ? room : region;
    }

    /// <summary>
    /// The room a point standing in an opening belongs to: the place on its side of the opening's middle,
    /// found by asking <paramref name="boxAt"/> (the place by boxes alone) just past the face on the point's
    /// side, since an opening's normal points at neither room. False in no opening, or on the open-air
    /// side of one to the outdoors.
    /// </summary>
    /// <remarks>
    /// A doorway, the wall's own thickness, is in no box, so by box alone it is the outdoors: every
    /// crossing let the city and the outdoor reverberation in for a step (Cody, 2026-10-03; the outdoor bus
    /// went from 1 % to 100 % in a stairwell's doorway). Here and not in OpeningRoutes: OpenFPS.Common's
    /// files are the build hash a login is checked by.
    /// </remarks>
    internal static bool RoomInOpening(OpeningRoutes routes, Vector3 point, Func<Vector3, int> boxAt, out int region)
    {
        region = AcousticConstants.GlobalRegionId;
        foreach (var o in routes.Openings)
        {
            if (o.NodeA == o.NodeB) continue;
            Vector3 d = point - o.Centre;
            float depth = Vector3.Dot(d, o.Normal);
            if (MathF.Abs(depth) > o.HalfDepth + InOpeningSlack
                || MathF.Abs(Vector3.Dot(d, o.Across)) > o.HalfWidth + InOpeningSlack
                || MathF.Abs(Vector3.Dot(d, o.Up)) > o.HalfHeight + InOpeningSlack) continue;
            Vector3 beyond = point + o.Normal * ((depth >= 0f ? 1f : -1f) * (o.HalfDepth + InOpeningSlack + 0.2f) - depth);
            int side = boxAt(beyond);
            if (side != o.RegionA && side != o.RegionB) continue;
            if (routes.NodeOf(side) == OpeningRoutes.Outside) return false;
            region = side;
            return true;
        }
        return false;
    }

    /// <summary>Metres past its faces a point still counts as in an opening: a box and the wall beside it
    /// rarely meet to the centimetre.</summary>
    private const float InOpeningSlack = 0.15f;

    /// <summary>The place a point is in by the boxes alone (a doorway is in none), for naming where the
    /// listener stands, not for sound. A named part of a room (a flight of stairs, a landing) answers
    /// here; it is not a room, so <see cref="GetRegionAt"/> never does.</summary>
    public int GetZoneAt(WorldSnapshot world, Vector3 position)
        => NamedPlaces.At(world, position) ?? GetRoomAt(world, position);

    /// <summary>The region a point is in by the boxes alone, leaving out named places: the room you are
    /// in, as a crossing between rooms is counted. A doorway is in none.</summary>
    public int GetRoomAt(WorldSnapshot world, Vector3 position) => _spatial.GetRegionAt(world, position);

    public AcousticPathData CalculateAcousticPath(WorldSnapshot world, int entityId, Vector3 listenerPos, Vector3 sourcePos)
    {
        var localPlayer = world.Entities.Values.FirstOrDefault(e => e.Definition.Type == EntityType.Player && Vector3.Distance(e.Transform.Position, listenerPos) < 2.0f);
        return CalculateMainPath(world, entityId, listenerPos, sourcePos, localPlayer.Id != 0 ? localPlayer.Id : AcousticConstants.GlobalRegionId);
    }
}
