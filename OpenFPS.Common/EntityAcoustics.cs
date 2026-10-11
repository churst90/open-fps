using System.Numerics;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;

namespace OpenFPS.Common;

// The one place the game's entities become the acoustics' rooms, openings and solids, kept on the host's
// side of the library as EntityGeometry is for the geometry (docs/SOUND_LIBRARY_BOUNDARY.md, stage 4). The
// server builds no acoustic map; the client (ClientWorldState), the lab and the tests build theirs here.

/// <summary>What the game's entities are to the acoustics: regions as rooms, portals as openings, fixed
/// boxes as the solids openings are found between. Every list is in entity order and every id is the
/// entity's, so the map comes out as it did when the generator read the entities itself.</summary>
public static class EntityAcoustics
{
    public static Room ToRoom(this in RegionComponent region) => new()
    {
        FriendlyName = region.FriendlyName,
        IsIndoor = region.IsIndoor,
        RoomSize = region.RoomSize,
        ReverbTimeScale = region.ReverbTimeScale,
        Materials = region.Materials,
    };

    public static Opening ToOpening(this in PortalComponent portal) => new()
    {
        RegionAId = portal.RegionAId,
        RegionBId = portal.RegionBId,
        ApertureSize = portal.ApertureSize,
        OpeningCentre = portal.OpeningCentre,
        OpeningRotation = portal.OpeningRotation,
    };

    /// <summary>Every entity with a region box, as a room where it stands.</summary>
    public static List<PlacedRoom> Rooms(IEnumerable<EntityDefinition> entities)
    {
        var rooms = new List<PlacedRoom>();
        foreach (var def in entities)
            if (def.Region.RoomSize.X > 0)
                rooms.Add(new PlacedRoom(def.EntityId, def.Transform.Position, def.Transform.Rotation, def.Region.ToRoom()));
        return rooms;
    }

    /// <summary>Every entity with a portal, as an opening where it stands.</summary>
    public static List<PlacedOpening> Openings(IEnumerable<EntityDefinition> entities)
    {
        var openings = new List<PlacedOpening>();
        foreach (var def in entities)
        {
            if (def.Portal.ApertureSize <= 0) continue;
            openings.Add(new PlacedOpening(def.EntityId, def.Transform.Position, def.Transform.Rotation, def.Portal.ToOpening()));
        }
        return openings;
    }

    /// <summary>
    /// The solid boxes, as the scene has them, with each door's leaf standing shut in its doorway: fixed, solid,
    /// a box of some size, not a region, not a sound source.
    /// </summary>
    public static List<FaceOpenings.Box> Solids(IEnumerable<EntityDefinition?> entities)
    {
        var solids = new List<FaceOpenings.Box>();
        foreach (var def in entities)
        {
            if (def == null || !def.Collider.IsSolid || def.Collider.Shape != ColliderShape.Box) continue;
            if (def.Region.RoomSize.X > 0f || def.Moves) continue;
            if (!string.IsNullOrEmpty(def.SoundEmitter.SoundId)) continue;
            var size = def.Collider.Size;
            if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) continue;
            bool leaf = def.Portal.RegionAId != 0 || def.Portal.RegionBId != 0;
            Vector3 at = def.Transform.Position;
            Quaternion rot = def.Transform.Rotation == default ? Quaternion.Identity : def.Transform.Rotation;
            if (leaf && def.Portal.OpeningRotation != default) { at = def.Portal.OpeningCentre; rot = def.Portal.OpeningRotation; }
            solids.Add(new FaceOpenings.Box(at, size, rot));
        }
        return solids;
    }

    /// <summary>The map of these entities' rooms and openings (<see cref="AcousticVolumeGenerator.GenerateRegions"/>),
    /// the indoor rooms where their floods are when <paramref name="floods"/> is given (<see cref="RoomsByFlood"/>).</summary>
    public static AcousticMap GenerateRegions(IEnumerable<EntityDefinition> entities, Vector3 mapSize, Vector3 minBound, float voxelResolution = 0.5f, float occlusionFloor = AcousticConstants.DefaultOcclusionFloor, bool autoDiscoverPortals = false, bool report = true,
                                              Geometry.RoomCells? floods = null)
        => AcousticVolumeGenerator.GenerateRegions(Rooms(entities), Openings(entities), Solids(entities), mapSize, minBound,
                                                   voxelResolution, occlusionFloor, autoDiscoverPortals, report, floods);

    /// <summary>The openings a flood stops at: every portal with no leaf (a door's leaf is shut in its doorway
    /// instead), as its frame, the entity's own place, turn and size (X across, Y up, Z through).</summary>
    public static List<Geometry.RoomFlood.Barrier> FloodBarriers(IEnumerable<EntityDefinition> entities)
    {
        var barriers = new List<Geometry.RoomFlood.Barrier>();
        foreach (var def in entities)
        {
            if (def.Portal.RegionAId == def.Portal.RegionBId || def.Portal.OpeningRotation != default) continue;
            var size = def.Collider.Size;
            if (size.X <= 0f || size.Y <= 0f) continue;
            bool outdoors = def.Portal.RegionAId == AcousticConstants.GlobalRegionId || def.Portal.RegionBId == AcousticConstants.GlobalRegionId;
            barriers.Add(new Geometry.RoomFlood.Barrier(def.EntityId, def.Transform.Position, size,
                                                         def.Transform.Rotation == default ? Quaternion.Identity : def.Transform.Rotation, outdoors));
        }
        barriers.Sort((a, b) => a.Id.CompareTo(b.Id));
        return barriers;
    }

    /// <summary>
    /// Every indoor room of these entities flooded from its point through <paramref name="world"/> with each door's
    /// leaf shut in its doorway, stopped by the portals with no leaf (docs/GEOMETRY.md 2.7, 12.6). The server and the client both call this, with the same
    /// definitions and the same geometry, so both put a point in the same room. With <paramref name="measure"/>
    /// (the client, which hears them) what lines each room is measured too.
    /// </summary>
    public static Geometry.RoomCells RoomsByFlood(Geometry.TriangleWorld world, IEnumerable<EntityDefinition> entities, Vector3 origin,
                                                  Geometry.RoomFlood.Cache? cache = null, int threads = 1, bool measure = false)
    {
        var seeds = new List<Geometry.RoomFlood.Seed>();
        var shut = new Dictionary<int, (Vector3, Quaternion)>();
        var defs = entities as IReadOnlyCollection<EntityDefinition> ?? entities.ToList();
        foreach (var def in defs)
        {
            if (def.Region.IsIndoor && def.Region.RoomSize.X > 0f && def.Region.RoomSize.Y > 0f && def.Region.RoomSize.Z > 0f)
                seeds.Add(new Geometry.RoomFlood.Seed(def.EntityId, def.Transform.Position, def.Region.RoomSize, def.Transform.Rotation));
            if (def.Portal.OpeningRotation != default) shut[def.EntityId] = (def.Portal.OpeningCentre, def.Portal.OpeningRotation);
        }
        seeds.Sort((a, b) => a.Id.CompareTo(b.Id));
        var shutWorld = world.WithMoverPoses(id => shut.TryGetValue(id, out var p) ? (true, p.Item1, p.Item2) : (false, default, default));
        var barriers = FloodBarriers(defs);
        return Geometry.RoomCells.From(Geometry.RoomFlood.Flood(shutWorld, seeds, origin, barriers, threads: threads, cache: cache, measure: measure), barriers);
    }

    /// <summary>A region entity's room, as its flood measured it where <paramref name="cells"/> holds its lining.</summary>
    public static Room RoomOf(EntityDefinition def, Geometry.RoomCells? cells)
        => cells != null && cells.TryLining(def.EntityId, out var lining) ? RoomAcoustics.Measured(def.Region.ToRoom(), lining) : def.Region.ToRoom();

    /// <summary>The map's openings framed from its rooms' floods (<see cref="AcousticVolumeGenerator.ApplyFloodOpenings"/>).</summary>
    public static void ApplyFloodOpenings(AcousticMap map, Geometry.RoomCells floods) => AcousticVolumeGenerator.ApplyFloodOpenings(map, floods);

    /// <summary>The openings in the faces of the map's rooms, or of only <paramref name="rooms"/>, from these
    /// entities' solids (<see cref="AcousticVolumeGenerator.AddFaceOpenings"/>).</summary>
    public static int AddFaceOpenings(AcousticMap map, IEnumerable<EntityDefinition> entities, IReadOnlyCollection<int>? rooms = null)
        => AcousticVolumeGenerator.AddFaceOpenings(map, Solids(entities), rooms);
}
