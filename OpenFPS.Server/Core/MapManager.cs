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

                if (entityData.Materials != null && world.Has<RegionComponent>(entity))
                {
                    ref var r = ref world.Get<RegionComponent>(entity);
                    for (int i = 0; i < Math.Min(6, entityData.Materials.Length); i++)
                    {
                        r.Materials[i] = entityData.Materials[i];
                    }
                }

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
        foreach (var entityData in m.Entities)
        {
            if (!lookup.TryGetValue(entityData.EntityId > 0 ? entityData.EntityId : -1, out var entity)) continue;

            if (world.Has<PortalComponent>(entity))
            {
                ref var p = ref world.Get<PortalComponent>(entity);
                // Translate Region IDs from JSON context to ECS context
                p.RegionAId = (entityData.RegionAId.HasValue && idMap.TryGetValue(entityData.RegionAId.Value, out var ecsA)) ? ecsA : AcousticConstants.GlobalRegionId;
                p.RegionBId = (entityData.RegionBId.HasValue && idMap.TryGetValue(entityData.RegionBId.Value, out var ecsB)) ? ecsB : AcousticConstants.GlobalRegionId;

                if (entityData.ApertureSize.HasValue) p.ApertureSize = entityData.ApertureSize.Value;
            }
        }

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

    public string GetMapChecksum(string id) => _mapRepo.GetMapChecksum(id);
    public IEnumerable<KeyValuePair<string, (World world, Vector3 size, SpatialGrid<Entity> grid, Dictionary<int, Entity> lookup, MapData data)>> GetAllMaps() => _maps;
}
