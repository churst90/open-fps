using Arch.Core;
using Arch.Core.Utils;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Core;

public class MapManager
{
    private readonly MapRepository _mapRepo;
    private readonly PrefabRepository _prefabRepo;
    private readonly Dictionary<string, (World world, Vector3 size, SpatialGrid<Entity> grid, Dictionary<int, Entity> lookup, MapData data)> _maps = new();

    public MapManager(MapRepository mapRepo, PrefabRepository prefabRepo) 
    {
        _mapRepo = mapRepo;
        _prefabRepo = prefabRepo;
    }

    public void Initialize()
    {
        foreach (var m in _mapRepo.LoadAll()) CreateMapInstance(m);
    }

    private void CreateMapInstance(MapData m)
    {
        var world = World.Create();
        var grid = new SpatialGrid<Entity>(new Vector2(m.MinBound.X, m.MinBound.Z), new Vector2(m.MaxBound.X, m.MaxBound.Z), 10.0f);
        var lookup = new Dictionary<int, Entity>();
        var idMap = new Dictionary<int, int>(); // JSON ID -> ECS ID
        
        float foundMinimumY = 1000f;
        bool hasAnyFloor = false;

        bool foundationExists = false;
        
        // 1st Pass: Spawn everything
        foreach (var entityData in m.Entities)
        {
            try 
            {
                var entity = _prefabRepo.Spawn(world, entityData.PrefabId, entityData.Position, entityData.Rotation, entityData.Scale);
                lookup[entityData.EntityId > 0 ? entityData.EntityId : entity.Id] = entity;
                if (entityData.EntityId > 0) idMap[entityData.EntityId] = entity.Id;

                ApplyRoomMaterials(world, entity, entityData, m.Id);

                if (entityData.IsIndoor.HasValue && world.Has<RegionComponent>(entity))
                {
                    ref var r = ref world.Get<RegionComponent>(entity);
                    r.IsIndoor = entityData.IsIndoor.Value;
                }

                if (entityData.PrefabId.Equals("concrete_floor", StringComparison.OrdinalIgnoreCase) && 
                    Vector3.Distance(entityData.Position, Vector3.Zero) < 0.1f)
                {
                    foundationExists = true;
                }

                // Track minimum Y for safety floor
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

        // 2nd Pass: Link Portals and Regions using the ID map
        int portalsLinked = 0;
        foreach (var entityData in m.Entities)
        {
            if (!lookup.TryGetValue(entityData.EntityId > 0 ? entityData.EntityId : -1, out var entity)) continue;

            // A map entity DECLARES a portal by carrying any of the portal fields. Previously this pass
            // only wrote into a PortalComponent the prefab had already attached — and `portal.json` carried
            // no portal fields, so PrefabRepository never attached one and every authored portal in every
            // map was silently discarded. The map entity's own fields must be able to CREATE the component.
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

            // Translate Region IDs from JSON context to ECS context. Only overwrite what the map actually
            // declared, so a prefab that ships sensible defaults keeps them. An unresolvable id (including
            // the conventional -1) means "the outside".
            if (entityData.RegionAId.HasValue)
                p.RegionAId = idMap.TryGetValue(entityData.RegionAId.Value, out var ecsA) ? ecsA : AcousticConstants.GlobalRegionId;
            if (entityData.RegionBId.HasValue)
                p.RegionBId = idMap.TryGetValue(entityData.RegionBId.Value, out var ecsB) ? ecsB : AcousticConstants.GlobalRegionId;

            if (entityData.ApertureSize.HasValue) p.ApertureSize = entityData.ApertureSize.Value;

            // An aperture of 0 means "no opening", which downstream reads as "not a portal at all".
            // Derive one from the doorway's own collider so an author can drop a portal prefab in a gap
            // and get the physically obvious opening size without restating it.
            if (p.ApertureSize <= 0f)
            {
                float derived = 1.0f;
                if (world.Has<ColliderComponent>(entity))
                {
                    var size = world.Get<ColliderComponent>(entity).Size;
                    if (size.X > 0 || size.Y > 0) derived = MathF.Max(size.X, size.Y);
                }
                p.ApertureSize = derived;
                Log.Information("MapManager: Portal entity {Id} in '{Map}' had no ApertureSize; derived {Aperture:F2} from its collider.",
                    entityData.EntityId, m.Id, derived);
            }

            if (p.RegionAId == p.RegionBId)
            {
                Log.Warning("MapManager: Portal entity {Id} in '{Map}' links region {Region} to itself — it will be ignored. " +
                            "Set RegionAId/RegionBId to the two region EntityIds it joins (-1 = outside).",
                    entityData.EntityId, m.Id, p.RegionAId);
            }
            else
            {
                portalsLinked++;
            }
        }
        Log.Information("MapManager: Linked {Count} portal(s) for map '{Id}'.", portalsLinked, m.Id);

        // AUTO-GENERATE FOUNDATION if missing
        if (!foundationExists)
        {
            Log.Information("MapManager: No foundation detected for '{Id}'. Injecting auto-scaled foundation.", m.Id);
            Vector3 mapSize = m.MaxBound - m.MinBound;
            // Place floor so its top surface is at Y=0
            var foundation = _prefabRepo.Spawn(world, "concrete_floor", new Vector3(0, -0.05f, 0), Quaternion.Identity, new Vector3(mapSize.X / 10f, 1f, mapSize.Z / 10f));
            lookup[foundation.Id] = foundation;
            hasAnyFloor = true;
            foundMinimumY = 0f;
        }

        // Set MinimumY as a "Void Plane" 20 meters below the lowest floor surface found
        m.MinimumY = hasAnyFloor ? (foundMinimumY - 20.0f) : -50.0f;
        
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
        RefreshGrid(m.Id);
        VerifySpawnPoint(m);
    }

    /// <summary>
    /// Writes a map entity's per-face room materials onto its RegionComponent. Six faces, in the order
    /// Floor, Ceiling, North, South, East, West — the order the reverb math reads them, which is NOT the
    /// FaceMask bit order, so it is worth saying out loud wherever it is written down.
    ///
    /// `RoomMaterials` names them ("Concrete", "Carpet"); `Materials` is the same thing as raw resonance
    /// indices, kept for the maps that already use it. Both are checked here: a name the registry does not
    /// know, or an array that is not six long, is reported with the entity that carries it rather than
    /// silently leaving that face as material 0 ("None"), which reads as a perfectly reflective surface.
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
        
        // Try to find the floor under the spawn point
        float ground = PhysicsUtils.GetGroundHeight(data.world, data.grid, m.SpawnPoint.Position, out _);
        
        if (ground > -500f)
        {
            // Found a floor! Place player slightly above it.
            var sp = m.SpawnPoint;
            sp.Position = new Vector3(m.SpawnPoint.Position.X, ground + 1.0f, m.SpawnPoint.Position.Z);
            m.SpawnPoint = sp;
            Log.Information("MapManager: Verified SpawnPoint for {Id} on floor at {Pos}", m.Id, m.SpawnPoint.Position);
        }
        else
        {
            // No floor found under spawn point. 
            // Instead of dropping to the void, we'll try to find ANY floor or fallback to Y=2.
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

    public void RegisterEntity(string mapId, Entity e) => _maps[mapId].lookup[e.Id] = e;
    public void UnregisterEntity(string mapId, int entityId) => _maps[mapId].lookup.Remove(entityId);

    /// <summary>
    /// The one way anything enters a live map after load. <paramref name="create"/> builds the entity in
    /// the map's world; this then does the three things that made it real and that every ad-hoc
    /// <c>world.Create</c> forgot: register it in the id lookup, index it in the spatial grid, and mark it
    /// dirty so the next broadcast carries its definition to every client in range. An entity created
    /// without those is invisible to collision, to <c>/scan</c> and to every client — while the command
    /// that made it reports success.
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

        // Dynamic entities are re-added to the grid every tick from scratch; only static geometry needs
        // a durable entry, and only the static half survives the per-tick Clear().
        bool isDynamic = world.Has<Velocity>(entity) || world.Has<PlayerComponent>(entity);
        if (!isDynamic && world.Has<ColliderComponent>(entity))
            data.grid.AddOverlapping(t.Position, world.Get<ColliderComponent>(entity).Size, entity, isStatic: true);
    }

    /// <summary>
    /// Removes an entity from the map: out of the lookup, out of the world, and — for static geometry —
    /// out of the spatial grid, which can only forget an entry by being rebuilt.
    /// </summary>
    public void DestroyEntity(string mapId, Entity entity)
    {
        if (!_maps.TryGetValue(mapId, out var data)) return;
        if (!data.world.IsAlive(entity)) { data.lookup.Remove(entity.Id); return; }

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

    public void RefreshGrid(string mapId)
    {
        if (!_maps.TryGetValue(mapId, out var data)) return;
        data.grid.ClearAll();
        int gridCount = 0;
        
        data.world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e, ref Transform t, ref ColliderComponent c) =>
        {
            data.grid.AddOverlapping(t.Position, c.Size, e, isStatic: true);
            gridCount++;
        });

        if (gridCount == 0)
        {
            Log.Warning("MapManager: Spatial grid for '{Id}' is EMPTY.", mapId);
        }
        else
        {
            Log.Information("MapManager: Refreshed static spatial grid for '{Id}'. Entities indexed: {Count}", mapId, gridCount);
        }
    }

    public bool TryGetMap(string id, out World world, out Vector3 size, out SpatialGrid<Entity> grid, out Dictionary<int, Entity> lookup)
    {
        if (_maps.TryGetValue(id, out var data)) { world = data.world; size = data.size; grid = data.grid; lookup = data.lookup; return true; }
        world = null!; size = default; grid = null!; lookup = null!; return false;
    }

    /// <summary>
    /// The loaded map's authored data. The login path used to call <c>MapRepository.LoadAll()</c> for
    /// this — re-reading and re-parsing every map file on disk, per login, to read one record that was
    /// already in memory.
    /// </summary>
    public bool TryGetMapData(string id, out MapData data)
    {
        if (_maps.TryGetValue(id, out var entry)) { data = entry.data; return true; }
        data = null!;
        return false;
    }

    public string GetMapChecksum(string id) => _mapRepo.GetMapChecksum(id);
    public IEnumerable<KeyValuePair<string, (World world, Vector3 size, SpatialGrid<Entity> grid, Dictionary<int, Entity> lookup, MapData data)>> GetAllMaps() => _maps;
}
