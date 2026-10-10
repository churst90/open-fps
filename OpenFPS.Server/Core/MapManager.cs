using Arch.Core;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>The loaded maps: each one's world, spatial grid, id lookup and data, and the one way in and out of them.</summary>
public class MapManager
{
    private readonly MapRepository _mapRepo;
    private readonly PrefabRepository _prefabRepo;
    private readonly Dictionary<string, (World world, Vector3 size, SpatialGrid<Entity> grid, Dictionary<int, Entity> lookup, MapData data)> _maps = new();

    /// <summary>The map a player lands on at login: whichever map sets <c>IsDefault</c>, and the
    /// map literally called "default" when none does.</summary>
    public string DefaultMapId { get; private set; } = "default";

    private readonly Dictionary<string, float> _earshot = new();

    /// <summary>Each map's static geometry as triangles (docs/GEOMETRY.md stage 1), beside its grid.</summary>
    private readonly Dictionary<string, ServerGeometry> _geometry = new();

    /// <summary>A map's triangle world and its builder, when the triangle path is on.</summary>
    public bool TryGetGeometry(string mapId, out ServerGeometry geometry) => _geometry.TryGetValue(mapId, out geometry!);

    /// <summary>
    /// Once a tick, after doors have swung and parts have been placed: door leaves move in the triangle
    /// world, and statics spawned since the last build are taken in.
    /// </summary>
    public void SyncGeometry(string mapId)
    {
        if (!_maps.TryGetValue(mapId, out var data) || !_geometry.TryGetValue(mapId, out var geometry)) return;
        geometry.Sync(data.world, data.grid);
    }

    private readonly Dictionary<string, int> _trackObstructions = new();

    /// <summary>
    /// How many places each track on each loaded map is not driveable, keyed "&lt;map&gt;/&lt;track&gt;".
    /// Zero everywhere is the only acceptable state, and a test holds the shipped maps to it: a car
    /// inside a solid box is heard as the car vanishing (the speedway's straight ran through the
    /// grandstand for 120 m and passed for an audio fault for four days).
    /// </summary>
    public IReadOnlyDictionary<string, int> TrackObstructions => _trackObstructions;

    /// <summary>
    /// A map entity's form over its prefab's (a ramp, a flight of stairs, an arch), and either one checked
    /// against the box it must fill: a form that cannot be made is said, and the entity is a box.
    /// </summary>
    internal static void ApplyForm(World world, Entity entity, Repositories.EntityData entityData, string mapId)
    {
        if (!world.Has<ColliderComponent>(entity)) return;
        ref var col = ref world.Get<ColliderComponent>(entity);
        if (entityData.Form != null) col.Form = entityData.Form;
        if (col.Form == null) return;
        if (col.Form.Kind == OpenFPS.Common.Geometry.ShapeKind.Box) { col.Form = null; return; }
        if (OpenFPS.Common.Geometry.Shapes.Problem(col.Form, col.Size, PhysicsConstants.StepHeight) is { } problem)
        {
            Log.Warning("MapManager: '{Map}' entity {Id} ({Prefab}): its form ({Form}) cannot be made: {Problem}. It is a box.",
                        mapId, entityData.EntityId, entityData.PrefabId, col.Form, problem);
            col.Form = null;
        }
    }

    /// <summary>The prefab the loader lays as a map's ground where it has none: dirt, ten metres square,
    /// scaled to the bounds. Later the top of a soil profile (docs/GEOMETRY.md, stage 5).</summary>
    public const string NaturalGroundPrefab = "dirt_floor";

    /// <summary>Every entity's turn made unit length, a zero turn the identity. Once per load.</summary>
    internal static void NormaliseTurns(MapData m)
    {
        foreach (var e in m.Entities)
        {
            var q = e.Rotation;
            float l2 = q.LengthSquared();
            if (l2 < 1e-12f || !float.IsFinite(l2)) e.Rotation = Quaternion.Identity;
            else if (l2 != 1f) e.Rotation = Quaternion.Normalize(q);
        }
    }

    /// <summary>
    /// Whether the solid floors at ground level (a top between a metre below and half a metre above 0)
    /// cover the whole of [min, max] seen from above, sampled at cell centres no more than 2 m apart
    /// (at most 128 a side). A ground made of many slabs counts as well as one.
    /// </summary>
    internal static bool GroundCovers(World world, Vector3 min, Vector3 max)
    {
        var floors = new List<(Vector3 Centre, Vector2 Half, Quaternion Inverse, Vector2 Lo, Vector2 Hi)>();
        var q = new QueryDescription().WithAll<Transform, ColliderComponent>();
        world.Query(in q, (Entity e, ref Transform t, ref ColliderComponent c) =>
        {
            if (!c.IsSolid || c.Shape != ColliderShape.Box) return;
            if (world.Has<Velocity>(e) || world.Has<PlayerComponent>(e) || world.Has<DoorComponent>(e)) return;
            if (world.Has<EntityType>(e) && world.Get<EntityType>(e) != EntityType.StaticObject) return;
            float top = t.Position.Y + c.Size.Y / 2f;
            if (top < -1f || top > 0.5f) return;
            var rot = t.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(t.Rotation);
            var half = OpenFPS.Common.Systems.FaceOpenings.AxisAlignedHalfExtents(c.Size * 0.5f, rot);
            floors.Add((t.Position, new Vector2(c.Size.X / 2f, c.Size.Z / 2f), Quaternion.Inverse(rot),
                        new Vector2(t.Position.X - half.X, t.Position.Z - half.Z), new Vector2(t.Position.X + half.X, t.Position.Z + half.Z)));
        });
        if (floors.Count == 0) return false;

        float w = max.X - min.X, d = max.Z - min.Z;
        if (w <= 0f || d <= 0f) return true;
        int nx = Math.Clamp((int)MathF.Ceiling(w / 2f), 1, 128), nz = Math.Clamp((int)MathF.Ceiling(d / 2f), 1, 128);
        // Bucket the floors by the sample columns they can cover, so a town of slabs is not a scan of
        // all of them at every point.
        var buckets = new List<int>[nx * nz];
        for (int i = 0; i < floors.Count; i++)
        {
            var f = floors[i];
            int x0 = Math.Max(0, (int)MathF.Floor((f.Lo.X - min.X) / w * nx)), x1 = Math.Min(nx - 1, (int)MathF.Floor((f.Hi.X - min.X) / w * nx));
            int z0 = Math.Max(0, (int)MathF.Floor((f.Lo.Y - min.Z) / d * nz)), z1 = Math.Min(nz - 1, (int)MathF.Floor((f.Hi.Y - min.Z) / d * nz));
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    (buckets[z * nx + x] ??= new List<int>()).Add(i);
        }
        for (int z = 0; z < nz; z++)
            for (int x = 0; x < nx; x++)
            {
                var p = new Vector3(min.X + (x + 0.5f) * w / nx, 0f, min.Z + (z + 0.5f) * d / nz);
                bool covered = false;
                if (buckets[z * nx + x] is { } list)
                    foreach (int i in list)
                    {
                        var f = floors[i];
                        var local = Vector3.Transform(new Vector3(p.X - f.Centre.X, 0f, p.Z - f.Centre.Z), f.Inverse);
                        if (MathF.Abs(local.X) <= f.Half.X + 1e-3f && MathF.Abs(local.Z) <= f.Half.Y + 1e-3f) { covered = true; break; }
                    }
                if (!covered) return false;
            }
        return true;
    }

    /// <summary>
    /// Warns at load, in the map author's terms (which track, where, how far off the line), wherever a
    /// track passes through or within a vehicle's width of something solid. A warning, not a refusal:
    /// the map is still playable.
    /// </summary>
    private void ValidateTracks(MapData m, World world)
    {
        if (m.Tracks == null || m.Tracks.Count == 0) return;

        var solids = new List<TrackClearance.Solid>();
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(),
            (Entity e, ref Transform t, ref ColliderComponent c) =>
            {
                if (!c.IsSolid || c.Shape != ColliderShape.Box) return;
                if (c.Size.X <= 0 || c.Size.Y <= 0 || c.Size.Z <= 0) return;
                solids.Add(new TrackClearance.Solid(t.Position, c.Size, t.Rotation));
            });
        if (solids.Count == 0) return;

        foreach (var track in m.Tracks)
        {
            var bad = TrackClearance.Check(track.Waypoints, track.WidthMetres, solids);
            _trackObstructions[$"{m.Id}/{track.Id}"] = bad.Count;
            if (bad.Count == 0) continue;

            var first = bad[0];
            Log.Warning("MapManager: track '{Track}' in '{Map}' is blocked at {Count} of its sampled points — "
                      + "the first is {Point} ({Offset:F1} m off the centreline), inside a {Size} solid centred at {Centre}. "
                      + "Vehicles driving that route will be inside geometry, which is heard as them disappearing.",
                        track.Id, m.Id, bad.Count, first.Point, first.LateralOffset, first.ObstacleSize, first.ObstacleCentre);
        }
    }

    /// <summary>
    /// How far from a player the server tells them about things, metres: the range of the map's loudest
    /// emitter (Loudness.AudibleRange), no further than the map's diagonal, clamped between
    /// <see cref="DefaultEarshotRange"/> and <see cref="MaxEarshotRange"/>. See docs/WORLD_STREAMING.md,
    /// "The broadcast radius".
    /// </summary>
    public float GetEarshotRange(string mapId)
        => _earshot.TryGetValue(mapId, out float r) ? r : DefaultEarshotRange;

    /// <summary>The floor, for a map whose loudest thing is quiet or which has no emitters at all.</summary>
    public const float DefaultEarshotRange = 200f;
    /// <summary>
    /// The ceiling. It must not be below what the loudest source carries: at 1,200 m an airliner
    /// (AudibleRange 3,000 m) stopped being sent and the sky was empty. 3,000 is AudibleRange's own cap.
    /// </summary>
    public const float MaxEarshotRange = 3000f;

    /// <summary>
    /// Recomputes every map's broadcast radius from what is in it now. Call it after everything that
    /// emits has been spawned: vehicles come after map load, and measured before them every racetrack
    /// came out at the 200 m floor.
    /// </summary>
    public void RefreshEarshotRanges()
    {
        foreach (var kv in _maps) ComputeEarshot(kv.Value.data, kv.Value.world);
        foreach (var kv in _earshot)
            Log.Information("MapManager: map '{Map}' broadcasts within {Range:F0} m of a player.", kv.Key, kv.Value);
    }

    /// <summary>One map's broadcast radius again, after its bounds changed (/setmapsize).</summary>
    public void RefreshEarshot(string mapId)
    {
        if (_maps.TryGetValue(mapId, out var entry)) ComputeEarshot(entry.data, entry.world);
    }

    /// <summary>
    /// Where natural ground is laid under a map's bounds: top at Y=0, centred on the bounds, not the
    /// origin (centred on 0 it left a strip with no floor). The prefab is ten metres square.
    /// </summary>
    internal static (Vector3 Position, Vector3 Scale) NaturalGroundPose(MapData m)
    {
        Vector3 size = m.MaxBound - m.MinBound, centre = (m.MinBound + m.MaxBound) * 0.5f;
        return (new Vector3(centre.X, -0.05f, centre.Z), new Vector3(size.X / 10f, 1f, size.Z / 10f));
    }

    /// <summary>The prefab a map's natural ground is: the one it chose, if this server has it, or dirt.</summary>
    internal string NaturalGroundOf(MapData m)
        => m.GroundPrefab is { } g && _prefabRepo.Prefabs.ContainsKey(g.ToLowerInvariant()) ? g : NaturalGroundPrefab;

    private void ComputeEarshot(MapData m, World world)
    {
        float loudest = DefaultEarshotRange;
        world.Query(new QueryDescription().WithAll<SoundEmitterComponent>(), (Entity e, ref SoundEmitterComponent s) =>
        {
            if (s.Range > loudest) loudest = s.Range;
        });
        var size = m.MaxBound - m.MinBound;
        float diagonal = new Vector2(size.X, size.Z).Length();
        _earshot[m.Id] = Math.Clamp(MathF.Min(loudest, diagonal), DefaultEarshotRange, MaxEarshotRange);
    }

    public MapManager(MapRepository mapRepo, PrefabRepository prefabRepo) 
    {
        _mapRepo = mapRepo;
        _prefabRepo = prefabRepo;
    }

    /// <summary>The map a player lands on, overriding whichever map claims <c>IsDefault</c>. Set from
    /// <c>--map &lt;id&gt;</c> before <see cref="Initialize"/>.</summary>
    public string? RequestedMapId { get; set; }

    /// <summary>Who owns each map, whether it is public, and who is invited (map_access.json). Null in a
    /// test rig that does not keep them; the access then lives in memory only.</summary>
    public MapAccessRepository? Access { get; set; }

    /// <summary>The world editor's edits, one overlay file per map, laid over each map's own data as it
    /// loads (docs/WORLD_EDITOR.md section 7). Null in a test rig that keeps none.</summary>
    public Editor.MapOverlayStore? Overlays { get; set; }

    /// <summary>
    /// Each map's authored entity ids (the EntityId in the map file, or the one its overlay gave) to the
    /// runtime entity made from it. The world editor names things by the authored id, which is the same
    /// every load; the runtime id is not.
    /// </summary>
    private readonly Dictionary<string, Dictionary<int, Entity>> _authored = new();

    /// <summary>The authored ids of a map's things, and the entities made from them (the editor's index).</summary>
    public Dictionary<int, Entity> AuthoredEntities(string mapId)
    {
        if (!_authored.TryGetValue(mapId, out var map)) _authored[mapId] = map = new Dictionary<int, Entity>();
        return map;
    }

    public void Initialize()
    {
        foreach (var m in _mapRepo.LoadAll())
        {
            Access?.ApplyTo(m);
            Overlays?.ApplyBefore(m);
            CreateMapInstance(m);
            Overlays?.ApplyAfter(this, m.Id);
        }

        // After loading: a map that does not exist is refused by name, not given as an empty world.
        if (!string.IsNullOrWhiteSpace(RequestedMapId))
        {
            if (_maps.ContainsKey(RequestedMapId))
            {
                Log.Information("MapManager: --map {Map} overrides the map claiming IsDefault ('{WasDefault}').",
                                RequestedMapId, DefaultMapId);
                DefaultMapId = RequestedMapId;
            }
            else
            {
                Log.Warning("MapManager: --map {Map} names no map that loaded; players land on '{Default}'. Loaded: {Maps}.",
                            RequestedMapId, DefaultMapId, string.Join(", ", _maps.Keys));
            }
        }

        // Logged so a wrong landing map can be told apart from an older server that lacks the map.
        Log.Information("MapManager: {Count} map(s) loaded; players will land on '{Default}'.",
                        _maps.Count, DefaultMapId);
    }

    private void CreateMapInstance(MapData m)
    {
        var world = World.Create();
        var grid = new SpatialGrid<Entity>(10.0f);
        var lookup = new Dictionary<int, Entity>();
        // Two id namespaces, kept apart: `lookup` is keyed by the runtime (ECS) id that everything carries;
        // `authored` and `idMap` by the ids written in the map file. Mixed into one, a map thing could not
        // be found by its own runtime id (a rifle you had just picked up resolved to nothing).
        var authored = new Dictionary<int, Entity>();   // JSON ID -> entity, during load only
        var idMap = new Dictionary<int, int>();         // JSON ID -> ECS ID
        // Regions whose faces or indoor flag the map set; the survey below leaves them alone.
        var materialsAuthored = new HashSet<int>();     // ECS ID
        var indoorAuthored = new HashSet<int>();        // ECS ID: maps that said IsIndoor themselves
        
        float foundMinimumY = 1000f;
        bool hasAnyFloor = false;
        // On a map streamed in tiles, the layer of everything that came from the file (MapTiles).
        var layers = m.TileMetres > 0f && !m.IsWorld ? new Dictionary<int, string?>() : null;

        // Maps write quaternions in six digits, not quite unit length, and Vector3.Transform scales by the
        // length squared: a slab's top a float's last bit low on one path and not another. Normalised
        // once, here, so the server, its triangles and every client read the same turn.
        NormaliseTurns(m);

        // First pass: spawn everything.
        foreach (var entityData in m.Entities)
        {
            try
            {
                var entity = SpawnFromData(world, entityData, m.Id, Vector3.Zero, materialsAuthored, indoorAuthored);
                lookup[entity.Id] = entity;
                if (layers != null) layers[entity.Id] = entityData.Layer;
                if (entityData.EntityId > 0)
                {
                    authored[entityData.EntityId] = entity;
                    idMap[entityData.EntityId] = entity.Id;
                }

                // The lowest floor, for the void plane.
                if (world.Has<Transform>(entity) && world.Has<ColliderComponent>(entity))
                {
                    ref var t = ref world.Get<Transform>(entity);
                    ref var c = ref world.Get<ColliderComponent>(entity);
                    
                    if (c.IsSolid && c.Shape == ColliderShape.Box)
                    {
                        float surfaceY = t.Position.Y + (c.Size.Y / 2f);
                        if (surfaceY < foundMinimumY) foundMinimumY = surfaceY;
                        hasAnyFloor = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("MapManager: Failed to spawn entity {PrefabId} at {Pos}. {Error}", entityData.PrefabId, entityData.Position, ex.Message);
            }
        }

        // Second pass: link portals to regions through the id map.
        int portalsLinked = LinkPortals(world, m.Entities, authored, idMap, m.Id);
        Log.Information("MapManager: Linked {Count} portal(s) for map '{Id}'.", portalsLinked, m.Id);

        SurveyRegions(world, m, materialsAuthored, indoorAuthored);
        CreateMapInstanceRest(m, world, grid, lookup, authored, layers, foundMinimumY, hasAnyFloor);
    }

    /// <summary>
    /// One thing of a map file into a world: its prefab where the file puts it (moved by
    /// <paramref name="offset"/>), named, with the room materials, form, door sides and indoor-ness the file
    /// gives. A map's load and a world tile copied from a map (OneWorld.WorldPlaces) both come this way.
    /// </summary>
    private Entity SpawnFromData(World world, Repositories.EntityData entityData, string mapId, Vector3 offset,
                                 HashSet<int>? materialsAuthored, HashSet<int>? indoorAuthored)
    {
        var entity = _prefabRepo.Spawn(world, entityData.PrefabId, entityData.Position + offset,
                                       entityData.Rotation, entityData.Scale, entityData.Name);
        if (!string.IsNullOrWhiteSpace(entityData.Name))
        {
            if (world.Has<NameComponent>(entity)) world.Get<NameComponent>(entity).Name = entityData.Name;
            if (world.Has<IdentityComponent>(entity)) world.Get<IdentityComponent>(entity).Name = entityData.Name;
        }

        ApplyRoomMaterials(world, entity, entityData, mapId);
        ApplyForm(world, entity, entityData, mapId);

        // Which side of this door is locked and which way it is pushed depend on where it is put,
        // so the map may say, over the prefab.
        if (world.Has<DoorComponent>(entity) && (entityData.KeyedSide.HasValue || entityData.PushSide.HasValue))
        {
            ref var door = ref world.Get<DoorComponent>(entity);
            if (entityData.KeyedSide.HasValue)
                door.KeyedSide = entityData.KeyedSide > 0 ? 1f : entityData.KeyedSide < 0 ? -1f : 0f;
            if (entityData.PushSide.HasValue) door.PushSide = entityData.PushSide < 0 ? -1f : 1f;
        }
        if (entityData.RoomMaterials != null || entityData.Materials != null)
            materialsAuthored?.Add(entity.Id);

        if (entityData.IsIndoor.HasValue && world.Has<RegionComponent>(entity))
        {
            ref var r = ref world.Get<RegionComponent>(entity);
            r.IsIndoor = entityData.IsIndoor.Value;
            indoorAuthored?.Add(entity.Id);
        }
        return entity;
    }

    /// <summary>
    /// Things copied from a map into a live map (a world tile, OneWorld.WorldPlaces): each spawned at its
    /// place moved by <paramref name="offset"/>, its doorways linked to the rooms among them by the ids in
    /// the file, every one indexed. Their rooms carry what was measured on the map (no survey here). The
    /// entities and their layers, in order; the caller calls <see cref="RefreshGrid"/>.
    /// </summary>
    public List<(Entity Entity, string? Layer)> SpawnCopied(string mapId, IReadOnlyList<Repositories.EntityData> entities, Vector3 offset)
    {
        var made = new List<(Entity, string?)>(entities.Count);
        if (!_maps.TryGetValue(mapId, out var data)) return made;
        var authored = new Dictionary<int, Entity>();
        var idMap = new Dictionary<int, int>();
        foreach (var d in entities)
        {
            try
            {
                var e = SpawnFromData(data.world, d, mapId, offset, null, null);
                made.Add((e, d.Layer));
                if (d.EntityId > 0) { authored[d.EntityId] = e; idMap[d.EntityId] = e.Id; }
            }
            catch (Exception ex)
            {
                Log.Error("MapManager: Failed to spawn entity {PrefabId} at {Pos} on '{Map}'. {Error}", d.PrefabId, d.Position + offset, mapId, ex.Message);
            }
        }
        LinkPortals(data.world, entities, authored, idMap, mapId, quiet: true);
        foreach (var (e, _) in made) IndexEntity(mapId, e);
        return made;
    }

    /// <summary>A map's doorways to the rooms they join, through the file's ids; how many were linked.</summary>
    private static int LinkPortals(World world, IEnumerable<Repositories.EntityData> entities, Dictionary<int, Entity> authored,
                                   Dictionary<int, int> idMap, string mapId, bool quiet = false)
    {
        int portalsLinked = 0;
        foreach (var entityData in entities)
        {
            if (!authored.TryGetValue(entityData.EntityId > 0 ? entityData.EntityId : -1, out var entity)) continue;

            // Any portal field on the map entity makes it a portal: portal.json carries none, so a portal
            // that waited for its prefab to attach the component was silently discarded.
            bool declaresPortal = entityData.RegionAId.HasValue || entityData.RegionBId.HasValue || entityData.ApertureSize.HasValue;
            if (!declaresPortal && !world.Has<PortalComponent>(entity)) continue;

            if (!world.Has<PortalComponent>(entity))
            {
                // Add BEFORE taking a ref — Add moves the entity to a new archetype and would invalidate it.
                world.Add(entity, new PortalComponent
                {
                    RegionAId = AcousticConstants.GlobalRegionId,
                    RegionBId = AcousticConstants.GlobalRegionId,
                    ApertureSize = 0f
                });
            }

            ref var p = ref world.Get<PortalComponent>(entity);

            // File ids to runtime ids, only where the map declared them. An id that resolves to nothing
            // (-1 by convention) is the outside.
            if (entityData.RegionAId.HasValue)
                p.RegionAId = idMap.TryGetValue(entityData.RegionAId.Value, out var ecsA) ? ecsA : AcousticConstants.GlobalRegionId;
            if (entityData.RegionBId.HasValue)
                p.RegionBId = idMap.TryGetValue(entityData.RegionBId.Value, out var ecsB) ? ecsB : AcousticConstants.GlobalRegionId;

            if (entityData.ApertureSize.HasValue) p.ApertureSize = entityData.ApertureSize.Value;

            // Where the opening is, as authored: a door is loaded shut, so its leaf's pose IS the
            // doorway's. DoorSystem records it again when it first takes hold of the door.
            if (world.Has<DoorComponent>(entity) && world.Has<Transform>(entity))
            {
                var t = world.Get<Transform>(entity);
                p.OpeningCentre = t.Position;
                p.OpeningRotation = t.Rotation;
            }

            // An aperture of 0 reads downstream as "not a portal", so one is taken from the doorway's collider.
            if (p.ApertureSize <= 0f)
            {
                float derived = 1.0f;
                if (world.Has<ColliderComponent>(entity))
                {
                    var size = world.Get<ColliderComponent>(entity).Size;
                    if (size.X > 0 || size.Y > 0) derived = MathF.Max(size.X, size.Y);
                }
                p.ApertureSize = derived;
                if (!quiet)
                    Log.Information("MapManager: Portal entity {Id} in '{Map}' had no ApertureSize; derived {Aperture:F2} from its collider.",
                        entityData.EntityId, mapId, derived);
            }

            if (p.RegionAId == p.RegionBId)
            {
                if (!quiet)
                    Log.Warning("MapManager: Portal entity {Id} in '{Map}' links region {Region} to itself — it will be ignored. " +
                                "Set RegionAId/RegionBId to the two region EntityIds it joins (-1 = outside).",
                        entityData.EntityId, mapId, p.RegionAId);
            }
            else
            {
                portalsLinked++;
            }
        }
        return portalsLinked;
    }

    /// <summary>A map's load after its things are spawned and its rooms measured: its ground, the void
    /// plane, its zone, its tiles and roads.</summary>
    private void CreateMapInstanceRest(MapData m, World world, SpatialGrid<Entity> grid, Dictionary<int, Entity> lookup,
                                       Dictionary<int, Entity> authored, Dictionary<int, string?>? layers,
                                       float foundMinimumY, bool hasAnyFloor)
    {
        // The ground from the survey on a map of a real place (docs/GEOMETRY.md 5.1), graded to what rests
        // on it, in tiles of ground.
        bool hasTerrain = false;
        if (m.Elevation != null)
        {
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var slabs = TerrainBuilder.Slabs(world);
                var tiles = TerrainBuilder.Lay(m.Elevation, slabs, m.MinBound, m.MaxBound);
                foreach (var e in TerrainBuilder.Spawn(world, tiles))
                {
                    lookup[e.Id] = e;
                    if (layers != null) layers[e.Id] = "ground";
                }
                float low = float.MaxValue;
                foreach (var t in tiles) foreach (float h in t.Heights) low = MathF.Min(low, h);
                foundMinimumY = MathF.Min(foundMinimumY, low);
                hasAnyFloor = hasTerrain = tiles.Count > 0;
                Log.Information("MapManager: '{Id}' lays {Tiles} tiles of ground from its survey, graded to {Slabs} slabs ({Ms} ms).",
                                m.Id, tiles.Count, slabs.Count, clock.ElapsedMilliseconds);
                // Which way the rain runs over it, and the running water it makes where it gathers
                // (docs/RUNNING_WATER.md 13).
                try
                {
                    foreach (var e in OpenFPS.Server.Water.MapDrainage.AtLoad(m.Id, world, tiles))
                    {
                        lookup[e.Id] = e;
                        if (layers != null) layers[e.Id] = OpenFPS.Server.Water.MapDrainage.Layer;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "MapManager: '{Id}' drainage could not be worked out; no water runs over its ground.", m.Id);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MapManager: '{Id}' has an elevation that could not be laid; its ground is flat.", m.Id);
            }
        }

        // Natural ground (dirt, Cody 2026-10-06) where the map has none of its own. A map whose ground
        // covers its play area gets none: a slab flush under the city's own was met wherever ties went its way.
        if (!hasTerrain && !m.IsWorld && !GroundCovers(world, m.WalkMin, m.WalkMax))
        {
            Log.Information("MapManager: '{Id}' has no ground of its own under all of its play area. Laying natural ground ({Ground}) under its bounds.", m.Id, m.GroundPrefab ?? NaturalGroundPrefab);
            var (at, scale) = NaturalGroundPose(m);
            var foundation = _prefabRepo.Spawn(world, NaturalGroundOf(m), at, Quaternion.Identity, scale);
            // Named "Ground": a round that ended in it was "Hit Concrete Floor" on the city's open grass.
            if (world.Has<IdentityComponent>(foundation)) world.Get<IdentityComponent>(foundation).Name = "Ground";
            if (world.Has<NameComponent>(foundation)) world.Get<NameComponent>(foundation).Name = "Ground";
            lookup[foundation.Id] = foundation;
            hasAnyFloor = true;
            foundMinimumY = 0f;
        }

        ValidateTracks(m, world);

        // The void plane: 20 m below the lowest floor. The world's is its own (its ground comes later).
        if (!m.IsWorld) m.MinimumY = hasAnyFloor ? (foundMinimumY - 20.0f) : -50.0f;
        
        world.Create(
            new NameComponent { Name = m.Id }, 
            new ZoneComponent 
            { 
                MapId = m.Id, 
                Size = m.Size, 
                MinBound = m.MinBound, 
                MaxBound = m.MaxBound, 
                Gravity = m.Gravity, 
                MinimumY = m.MinimumY,
                Temperature = m.Temperature,
                Humidity = m.Humidity,
                AirPressure = m.AirPressure,
                AirAbsorptionMultiplier = m.AirAbsorptionMultiplier
            }, 
            new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity }
        );

        Log.Information("MapManager: Loaded map '{Id}' with {Count} entities. Void Plane (MinimumY): {MinY}", m.Id, m.Entities.Count, m.MinimumY);
        
        _maps[m.Id] = (world, m.Size, grid, lookup, m);
        _authored[m.Id] = new Dictionary<int, Entity>(authored);
        if (layers != null)
        {
            var tiles = MapTiles.Build(world, m.TileMetres, m.MinBound, m.MaxBound, layers);
            _tiles[m.Id] = tiles;
            Log.Information("MapManager: '{Id}' is streamed in {Count} tiles of {Metres} m ({Tiled} entities in tiles, {Global} sent to everyone).",
                            m.Id, tiles.Tiles.Count(), m.TileMetres, tiles.TiledCount, tiles.Global.Count);
        }
        else if (m.IsWorld)
        {
            // The world's tiles arrive as players near them (OneWorld.WorldMaps).
            _tiles[m.Id] = new MapTiles(m.TileMetres, TileKey.Of(m.MinBound, m.TileMetres), TileKey.Of(m.MaxBound, m.TileMetres), dynamic: true);
        }
        else _tiles.Remove(m.Id);
        BuildRoads(m);
        ComputeEarshot(m, world);
        if (m.IsDefault)
        {
            if (DefaultMapId == "default" || DefaultMapId == m.Id) DefaultMapId = m.Id;
            else Log.Warning("MapManager: map '{Map}' also claims IsDefault, but '{Winner}' claimed it first; players will land on '{Winner}'.", m.Id, DefaultMapId);
        }
        RefreshGrid(m.Id);
        if (!m.IsWorld) VerifySpawnPoint(m);
    }

    /// <summary>
    /// What each named place on a map is made of and whether it is enclosed, measured from the walls
    /// round it (<see cref="CompositeAcoustics.SurveyBox(World, List{Entity}, Vector3, Vector3)"/>).
    /// An authored list always wins; this only fills in faces left as "None". See
    /// docs/THE_CITY_BLOCK.md, "Rooms that measure themselves".
    /// </summary>
    private static void SurveyRegions(World world, Repositories.MapData m,
                                      HashSet<int> materialsAuthored, HashSet<int> indoorAuthored)
    {
        // Everything solid enough to be a wall, a floor or a ceiling. Regions and portals are not.
        var solids = new List<Entity>();
        var solidQuery = new QueryDescription().WithAll<Transform, ColliderComponent>();
        world.Query(in solidQuery, (Entity e) =>
        {
            if (world.Has<RegionComponent>(e)) return;
            var c = world.Get<ColliderComponent>(e);
            if (!c.IsSolid || c.Shape != ColliderShape.Box) return;
            solids.Add(e);
        });
        if (solids.Count == 0) return;

        int surveyed = 0, skipped = 0;
        var regionQuery = new QueryDescription().WithAll<Transform, RegionComponent>();
        var regions = new List<Entity>();
        world.Query(in regionQuery, (Entity e) => regions.Add(e));

        // Parts filed by column so a region asks only about the parts near it: asking every region about
        // every box took 18 s for a town. Same set, same order as the full scan.
        var extents = new (Vector3 Lo, Vector3 Hi)[solids.Count];
        var columns = new BoxColumns();
        for (int i = 0; i < solids.Count; i++)
        {
            var et = world.Get<Transform>(solids[i]);
            var half = CompositeAcoustics.AxisAlignedHalfExtents(world.Get<ColliderComponent>(solids[i]).Size * 0.5f, et.Rotation);
            extents[i] = (et.Position - half, et.Position + half);
            columns.Add(i, extents[i].Lo, extents[i].Hi);
        }
        var candidates = new List<int>();

        foreach (var region in regions)
        {
            var t = world.Get<Transform>(region);
            ref var r = ref world.Get<RegionComponent>(region);
            if (r.RoomSize.X <= 0f || r.RoomSize.Y <= 0f || r.RoomSize.Z <= 0f) continue;

            if (materialsAuthored.Contains(region.Id)) { skipped++; continue; }

            // The parts overlapping its box with WallReach of slack; a turned region reaches as far as its turned box.
            bool turned = !IsUpright(t.Rotation);
            var reach = turned ? CompositeAcoustics.AxisAlignedHalfExtents(r.RoomSize * 0.5f, t.Rotation) : r.RoomSize * 0.5f;
            var lo = t.Position - reach - new Vector3(WallReach);
            var hi = t.Position + reach + new Vector3(WallReach);
            var near = new List<Entity>();
            columns.Collect(lo, hi, candidates);
            foreach (int i in candidates)
            {
                var (eLo, eHi) = extents[i];
                if (eHi.X < lo.X || eLo.X > hi.X) continue;
                if (eHi.Y < lo.Y || eLo.Y > hi.Y) continue;
                if (eHi.Z < lo.Z || eLo.Z > hi.Z) continue;
                near.Add(solids[i]);
            }
            if (near.Count == 0) continue;

            // A turned room is measured in its own frame; in the map's, a house turned a quarter round came out half open.
            var survey = turned
                ? CompositeAcoustics.SurveyBox(world, near, t.Position, r.RoomSize, t.Rotation)
                : CompositeAcoustics.SurveyBox(world, near, t.Position, r.RoomSize);

            // Fill in blanks, never overrule, and only where enclosed: outdoors the reflections come from
            // the walls themselves, and filling faces there would respec the approved speedway's 42 regions.
            // IsIndoor is measured for every region the map did not assert it for, before the materials.
            if (!indoorAuthored.Contains(region.Id)) r.IsIndoor = survey.Covered;

            if (!survey.Covered) continue;

            int filled = 0;
            var took = new List<string>();
            for (int f = 0; f < 6; f++)
            {
                if (r.Materials[f] != 0) continue;                                     // the author's
                if (survey.Coverage[f] < CompositeAcoustics.FaceCoverage) continue;    // nothing there
                if (!AcousticRegistry.TryGetResonanceIndex(survey.Materials[f], out int index)) continue;
                r.Materials[f] = index;
                filled++;
                took.Add($"{CompositeAcoustics.FaceNames[f]} {survey.Materials[f]}");
            }

            if (filled == 0) continue;
            surveyed++;

            var line = "MapManager: '{Map}' measured '{Name}' ({Size}): {Walls}/6 walled, {Solid:P0} solid -> took {Took}{Indoor}";
            object[] args = { m.Id, r.FriendlyName, r.RoomSize, survey.Walls, survey.SolidFraction,
                              string.Join(", ", took), survey.Covered ? "" : " (open)" };
            Log.Information(line, args);
        }

        if (surveyed + skipped > 0)
            Log.Information("MapManager: '{Map}': {Surveyed} region(s) had blank faces filled in from the geometry, {Skipped} kept an authored list.",
                m.Id, surveyed, skipped);
    }

    /// <summary>Whether a rotation leaves a box as it is (or is unset).</summary>
    private static bool IsUpright(Quaternion q) => q.IsIdentity || q == default;

    /// <summary>How far outside a region's box a wall may stand and still be its wall, metres: a region is
    /// drawn to the inside of a room. The same reach the client's <see cref="OpenFPS.Common.Systems.FaceOpenings"/>
    /// uses, so faces and the gaps in them are measured from the same walls.</summary>
    private const float WallReach = OpenFPS.Common.Systems.FaceOpenings.WallReachMetres;

    /// <summary>
    /// A map entity's per-face room materials onto its RegionComponent: by name (RoomMaterials) or as raw
    /// resonance indices (Materials). Faces in the order Floor, Ceiling, North, South, East, West, which
    /// is the reverb's order and NOT the FaceMask bit order. A bad name or length is logged with the entity:
    /// a face left at "None" reads as perfectly reflective.
    /// </summary>
    private static void ApplyRoomMaterials(World world, Entity entity, Repositories.EntityData entityData, string mapId)
    {
        if (entityData.RoomMaterials == null && entityData.Materials == null) return;

        if (!world.Has<RegionComponent>(entity))
        {
            Log.Warning("MapManager: Entity {Id} in '{Map}' sets room materials but prefab '{Prefab}' is not an acoustic region, so they are ignored.",
                entityData.EntityId, mapId, entityData.PrefabId);
            return;
        }

        ref var r = ref world.Get<RegionComponent>(entity);

        if (entityData.RoomMaterials != null)
        {
            if (entityData.RoomMaterials.Length != 6)
                Log.Warning("MapManager: Entity {Id} in '{Map}' has {Count} RoomMaterials; it needs exactly 6 ({Order}). The rest keep the prefab's.",
                    entityData.EntityId, mapId, entityData.RoomMaterials.Length, string.Join(", ", PrefabValidator.RoomFaceOrder));

            for (int i = 0; i < Math.Min(6, entityData.RoomMaterials.Length); i++)
            {
                if (AcousticRegistry.TryGetResonanceIndex(entityData.RoomMaterials[i], out int index))
                    r.Materials[i] = index;
                else
                    Log.Warning("MapManager: Entity {Id} in '{Map}' names material '{Material}' for its {Face}, which is not a known material. Known: {Known}.",
                        entityData.EntityId, mapId, entityData.RoomMaterials[i], PrefabValidator.RoomFaceOrder[i],
                        string.Join(", ", AcousticRegistry.KnownMaterials()));
            }
        }

        if (entityData.Materials != null)
        {
            if (entityData.Materials.Length != 6)
                Log.Warning("MapManager: Entity {Id} in '{Map}' has {Count} Materials indices; it needs exactly 6 ({Order}).",
                    entityData.EntityId, mapId, entityData.Materials.Length, string.Join(", ", PrefabValidator.RoomFaceOrder));

            for (int i = 0; i < Math.Min(6, entityData.Materials.Length); i++)
            {
                r.Materials[i] = entityData.Materials[i];
            }
        }
    }

    private void VerifySpawnPoint(MapData m)
    {
        if (!_maps.TryGetValue(m.Id, out var data)) return;
        
        float ground = PhysicsUtils.GetGroundHeight(data.world, data.grid, m.SpawnPoint.Position, out _);

        if (ground > -500f)
        {
            var sp = m.SpawnPoint;
            sp.Position = new Vector3(m.SpawnPoint.Position.X, ground + 1.0f, m.SpawnPoint.Position.Z);
            m.SpawnPoint = sp;
            Log.Information("MapManager: Verified SpawnPoint for {Id} on floor at {Pos}", m.Id, m.SpawnPoint.Position);
        }
        else
        {
            float fallbackY = 2.0f;
            Log.Warning("MapManager: NO FLOOR DETECTED under SpawnPoint for {Id}. Falling back to Y={Fallback}", m.Id, fallbackY);
            
            var sp = m.SpawnPoint;
            sp.Position = new Vector3(m.SpawnPoint.Position.X, fallbackY, m.SpawnPoint.Position.Z);
            m.SpawnPoint = sp;
        }
    }

    public Transform GetSpawnPoint(string mapId)
    {
        if (_maps.TryGetValue(mapId, out var data)) return data.data.SpawnPoint;
        return new Transform { Position = new Vector3(0, 5, 0) };
    }

    /// <summary>The prefabs this server knows, by id.</summary>
    public IReadOnlyDictionary<string, OpenFPS.Server.Repositories.PrefabTemplate> Prefabs => _prefabRepo.Prefabs;
    /// <summary>The prefab library itself: the world editor puts new versions of prefabs in it.</summary>
    public OpenFPS.Server.Repositories.PrefabRepository PrefabRepository => _prefabRepo;

    /// <summary>A prefab made into a live entity on a map, through <see cref="SpawnEntity"/>.</summary>
    public Entity SpawnPrefab(string mapId, string prefabId, Vector3 position)
        => SpawnEntity(mapId, w => _prefabRepo.Spawn(w, prefabId, position));

    /// <summary>A prefab made into a live entity on a map, turned, scaled and named as a map file would
    /// have it (the world editor's way in).</summary>
    public Entity SpawnPrefab(string mapId, string prefabId, Vector3 position, Quaternion rotation, Vector3 scale, string? name)
        => SpawnEntity(mapId, w =>
        {
            var e = _prefabRepo.Spawn(w, prefabId, position, rotation, scale, name);
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (w.Has<NameComponent>(e)) w.Get<NameComponent>(e).Name = name;
                if (w.Has<IdentityComponent>(e)) w.Get<IdentityComponent>(e).Name = name;
            }
            return e;
        });

    /// <summary>
    /// The one way anything enters a live map after load. <paramref name="create"/> builds the entity;
    /// this registers it in the id lookup, indexes it in the grid and marks it dirty for the broadcast.
    /// A bare <c>world.Create</c> is invisible to collision, to /scan and to every client.
    /// </summary>
    public Entity SpawnEntity(string mapId, Func<World, Entity> create)
    {
        if (!_maps.TryGetValue(mapId, out var data))
        {
            Log.Warning("MapManager: SpawnEntity called for unknown map '{Id}'.", mapId);
            return Entity.Null;
        }

        var entity = create(data.world);
        IndexEntity(mapId, entity);
        return entity;
    }

    /// <summary>
    /// Registers and indexes an entity that already exists in the map's world (the player-spawn path
    /// builds its entity component by component, so it cannot use <see cref="SpawnEntity"/>).
    /// </summary>
    public void IndexEntity(string mapId, Entity entity)
    {
        if (!_maps.TryGetValue(mapId, out var data)) return;
        var world = data.world;
        if (!world.IsAlive(entity)) return;

        data.lookup[entity.Id] = entity;
        if (!world.Has<Transform>(entity)) return;

        ref var t = ref world.Get<Transform>(entity);
        t.IsDirty = true;

        // Only static geometry needs a durable grid entry: the dynamic half is rebuilt every tick.
        bool isDynamic = world.Has<Velocity>(entity) || world.Has<PlayerComponent>(entity);
        if (!isDynamic && world.Has<ColliderComponent>(entity))
        {
            data.grid.AddOverlapping(t.Position, world.Get<ColliderComponent>(entity).Size, t.Rotation, entity, isStatic: true);
            // Tested the old way until the triangle world takes it in, at the end of this tick.
            if (_geometry.TryGetValue(mapId, out var geometry))
            {
                data.grid.AddUnindexed(entity);
                geometry.NoteIndexed(entity);
            }
        }
    }

    /// <summary>
    /// Removes an entity from the map: out of the lookup, out of the world, and — for static geometry —
    /// out of the spatial grid, which can only forget an entry by being rebuilt.
    /// </summary>
    public void DestroyEntity(string mapId, Entity entity)
    {
        if (!_maps.TryGetValue(mapId, out var data)) return;
        // Already gone: forget the id only if it is still this entity's. Arch reuses ids, and a stale handle
        // (a shot driver already taken away) took the new entity's id out of the lookup.
        if (!data.world.IsAlive(entity))
        {
            if (data.lookup.TryGetValue(entity.Id, out var known) && known == entity) data.lookup.Remove(entity.Id);
            return;
        }

        bool wasStatic = !data.world.Has<Velocity>(entity) && !data.world.Has<PlayerComponent>(entity)
                         && data.world.Has<ColliderComponent>(entity);

        data.lookup.Remove(entity.Id);
        data.world.Destroy(entity);

        if (wasStatic) RefreshGrid(mapId);
    }

    /// <summary>Tears down every map world. Called once, on shutdown.</summary>
    public void Shutdown()
    {
        foreach (var kv in _maps)
        {
            try { World.Destroy(kv.Value.world); }
            catch (Exception ex) { Log.Warning(ex, "MapManager: error destroying world for map '{Id}'.", kv.Key); }
        }
        _maps.Clear();
    }

    private int _gridDeferred;
    /// <summary>Grid refreshes run, for the tests.</summary>
    internal int GridRefreshes { get; private set; }
    private readonly HashSet<string> _gridDirty = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Holds every RefreshGrid until the scope ends, then refreshes each map once.</summary>
    // Undoing a row of fifty otherwise refiles the map fifty times, ~2 ms and a log line each, on the tick thread.
    public IDisposable DeferGrid()
    {
        _gridDeferred++;
        return new GridScope(this);
    }

    private sealed class GridScope(MapManager maps) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--maps._gridDeferred > 0) return;
            var dirty = maps._gridDirty.ToList();
            maps._gridDirty.Clear();
            foreach (var mapId in dirty) maps.RefreshGrid(mapId);
        }
    }

    public void RefreshGrid(string mapId)
    {
        if (_gridDeferred > 0) { _gridDirty.Add(mapId); return; }
        GridRefreshes++;
        if (!_maps.TryGetValue(mapId, out var data)) return;
        // Only what changed, once the map's fixed things have been filed whole (ServerGeometry.Refresh).
        if (OpenFPS.Common.Geometry.TriangleGeometry.Enabled && OpenFPS.Common.Geometry.TriangleGeometry.Incremental
            && _geometry.TryGetValue(mapId, out var known) && known.Primed)
        {
            known.Refresh(data.world, data.grid, g => FileStatics(data.world, g));
            if (known.LastChanged > 0)
                Log.Information("MapManager: '{Id}': {Changed} fixed thing(s) changed, filed again in {Ms:F1} ms ({Built} tile(s) built).",
                                mapId, known.LastChanged, known.LastRefreshMs, known.LastBuilt);
            return;
        }
        data.grid.ClearAll();
        int gridCount = FileStatics(data.world, data.grid);

        // The same static geometry as triangles: only the tiles whose solids changed are built again.
        if (OpenFPS.Common.Geometry.TriangleGeometry.Enabled)
        {
            if (!_geometry.TryGetValue(mapId, out var geometry))
                _geometry[mapId] = geometry = new ServerGeometry(data.data.TileMetres > 0f ? data.data.TileMetres : 250f);
            geometry.Rebuild(data.world, data.grid);
            if (geometry.LastBuilt > 0)
                Log.Information("MapManager: '{Id}' as triangles: {Tris:N0} in {Tiles} tile(s), {Built} built in {Ms:F0} ms.",
                                mapId, geometry.World.TriangleCount, geometry.Builder.TileCount, geometry.LastBuilt, geometry.LastBuildMs);
        }

        if (gridCount == 0)
        {
            Log.Warning("MapManager: Spatial grid for '{Id}' is EMPTY.", mapId);
        }
        else
        {
            Log.Information("MapManager: Refreshed static spatial grid for '{Id}'. Entities indexed: {Count}", mapId, gridCount);
        }
    }

    /// <summary>Every fixed thing of a world into a grid's static half; how many.</summary>
    internal static int FileStatics(World world, SpatialGrid<Entity> grid)
    {
        int gridCount = 0;
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e, ref Transform t, ref ColliderComponent c) =>
        {
            // The same test IndexEntity uses, and it must be: a static entry for something that moves is a
            // permanent ghost of where it was when this ran.
            if (world.Has<Velocity>(e) || world.Has<PlayerComponent>(e)) return;
            grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, isStatic: true);
            gridCount++;
        });
        return gridCount;
    }

    public bool TryGetMap(string id, out World world, out Vector3 size, out SpatialGrid<Entity> grid, out Dictionary<int, Entity> lookup)
    {
        if (_maps.TryGetValue(id, out var data)) { world = data.world; size = data.size; grid = data.grid; lookup = data.lookup; return true; }
        world = null!; size = default; grid = null!; lookup = null!; return false;
    }

    /// <summary>Every map currently loaded, by id.</summary>
    public IEnumerable<string> LoadedMapIds => _maps.Keys;

    /// <summary>
    /// The loaded map a player means, by its id or by the name it is listed under, whatever the case
    /// and whether the words are joined by spaces, underscores or hyphens: "magnolia tx", "Magnolia_TX"
    /// and "magnolia-tx" are all magnolia_tx. Null if none.
    /// </summary>
    public string? ResolveMapId(string said)
    {
        if (string.IsNullOrWhiteSpace(said)) return null;
        string exact = _maps.Keys.FirstOrDefault(id => id.Equals(said.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "";
        if (exact.Length > 0) return exact;
        string want = Loose(said);
        foreach (var (id, entry) in _maps.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            if (Loose(id) == want || Loose(entry.data.Name) == want) return id;
        return null;

        static string Loose(string? s) => string.Join(" ", (s ?? "").ToLowerInvariant()
            .Split(new[] { ' ', '_', '-', '\t' }, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>What a loaded map is called aloud: its listed name, or its id.</summary>
    public string DisplayName(string mapId) => TryGetMapData(mapId, out var d) ? d.DisplayName : mapId;

    /// <summary>
    /// Writes a map back to disk as it now stands, including what was built on it since. Explicit, never
    /// automatic: a world that rewrites its own map on every change cannot be experimented with.
    /// </summary>
    public bool SaveMap(string mapId, out string error)
    {
        error = "";
        if (!_maps.TryGetValue(mapId, out var entry)) { error = $"map '{mapId}' is not loaded"; return false; }
        try
        {
            _mapRepo.Save(entry.data);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>
    /// Whether a loaded map is one of the server's own, not one a player made. Their files are written
    /// by generators (tools/gen_city.py, gen_speedway.py, gen_osm.py), so what is spawned on them is not
    /// recorded for /savemap; the world editor's overlay keeps lasting edits to them.
    /// </summary>
    public bool IsShipped(string mapId)
        => TryGetMapData(mapId, out var data)
        && !Path.GetDirectoryName(_mapRepo.PathFor(data))!.Equals(_mapRepo.PlayerDirectory, StringComparison.Ordinal);

    // ── Maps players make ───────────────────────────────────────────────────────────────────────

    /// <summary>How many maps one person may own.</summary>
    public const int MaxMapsPerOwner = 3;
    /// <summary>How many maps players may have made between them, so a server cannot be filled with them.</summary>
    public const int MaxPlayerMaps = 100;

    /// <summary>Whether a username owns a loaded map.</summary>
    public bool IsOwner(string mapId, string username)
        => TryGetMapData(mapId, out var data) && !string.IsNullOrWhiteSpace(data.OwnerId)
        && data.OwnerId.Equals(username, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the owner of a loaded map has made a username one of its editors (/map editor add).</summary>
    public bool IsEditor(string mapId, string username)
        => TryGetMapData(mapId, out var data)
        && data.Editors.Any(n => n.Equals(username, StringComparison.OrdinalIgnoreCase));

    /// <summary>The loaded maps a username owns.</summary>
    public List<string> OwnedBy(string username)
        => _maps.Where(kv => !string.IsNullOrWhiteSpace(kv.Value.data.OwnerId)
                          && kv.Value.data.OwnerId.Equals(username, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// A new map, from a template: written to the players' map folder, loaded, and recorded with its
    /// owner, so it is there after a restart. Refused (with the reason in <paramref name="error"/>) for
    /// an id already used by a map loaded or on disk.
    /// </summary>
    public bool CreateMap(MapData map, out string error)
    {
        error = "";
        if (_maps.Keys.Any(k => k.Equals(map.Id, StringComparison.OrdinalIgnoreCase)) || _mapRepo.Exists(map.Id))
        { error = $"there is already a map called {map.Id}"; return false; }
        try { _mapRepo.Save(map); }
        catch (Exception ex) { error = ex.Message; return false; }
        CreateMapInstance(map);
        Access?.Record(map);
        Log.Information("MapManager: made map '{Map}' for {Owner}.", map.Id, map.OwnerId);
        return true;
    }

    /// <summary>A frame of the world, made by the server (OneWorld.WorldMaps): never written to disk, its
    /// tiles loaded as players near them. False if a map of that id is loaded already.</summary>
    public bool AddWorldMap(MapData map)
    {
        if (_maps.ContainsKey(map.Id)) return false;
        map.IsWorld = true;
        CreateMapInstance(map);
        return true;
    }

    /// <summary>Keeps a map's owner, public flag and invitations as they now stand (map_access.json).</summary>
    public void RecordAccess(string mapId)
    {
        if (TryGetMapData(mapId, out var data)) Access?.Record(data);
    }

    private readonly Dictionary<string, RoadNetwork> _roads = new();

    private readonly Dictionary<string, MapTiles> _tiles = new();

    /// <summary>The tiles of a map that is streamed (MapData.TileMetres above 0); false for a map sent whole.</summary>
    public bool TryGetTiles(string mapId, out MapTiles tiles) => _tiles.TryGetValue(mapId, out tiles!);

    /// <summary>
    /// The map's road network, from its roads and junctions. Its problems are logged at load, one line
    /// each: a lane that arrives at a junction it cannot leave is traffic that stops for ever.
    /// </summary>
    private void BuildRoads(MapData m)
    {
        if (m.Roads == null || m.Roads.Count == 0) { _roads.Remove(m.Id); return; }
        var net = new RoadNetwork(m.Roads, m.Junctions);
        _roads[m.Id] = net;
        Log.Information("MapManager: '{Map}' roads: {Roads} roads, {Junctions} junctions, {Segments} lane segments, {Dead} dead ends.",
            m.Id, net.Roads.Count, net.Junctions.Count, net.Segments.Count, net.DeadEnds);
        foreach (var problem in net.Problems)
            Log.Warning("MapManager: '{Map}' roads: {Problem}", m.Id, problem);
    }

    /// <summary>A map's road network, if it has roads.</summary>
    public bool TryGetRoads(string id, out RoadNetwork roads) => _roads.TryGetValue(id, out roads!);

    /// <summary>A loaded map's data, from memory.</summary>
    public bool TryGetMapData(string id, out MapData data)
    {
        if (_maps.TryGetValue(id, out var entry)) { data = entry.data; return true; }
        data = null!;
        return false;
    }

    public string GetMapChecksum(string id) => _mapRepo.GetMapChecksum(id);
    public IEnumerable<KeyValuePair<string, (World world, Vector3 size, SpatialGrid<Entity> grid, Dictionary<int, Entity> lookup, MapData data)>> GetAllMaps() => _maps;
}
