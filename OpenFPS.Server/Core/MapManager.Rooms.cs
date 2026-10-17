using System.Numerics;
using System.Runtime.CompilerServices;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Geometry;
using Serilog;
using OpenFPS.Acoustics;

namespace OpenFPS.Server.Core;

/// <summary>
/// The rooms from the walls (docs/GEOMETRY.md 12.6): each map's indoor rooms flooded through its triangle world with
/// the door leaves shut, from the same definitions the clients are sent and by the same call (EntityAcoustics
/// .RoomsByFlood), so the server and every client put a point in the same room. Flooded again, only where something
/// changed, after every change to the fixed geometry. The game's server floods in the background and the boxes serve
/// until a flood lands; without that (a test) the first question floods, and waits for any flood under way.
/// </summary>
public partial class MapManager
{
    /// <summary>Flood each map's rooms in the background as soon as it loads or changes (the game's server), rather
    /// than when they are first asked about (a test).</summary>
    public bool PrefetchRooms { get; init; }

    private sealed class MapRooms
    {
        public readonly object Lock = new();
        public readonly RoomFlood.Cache Cache = new();
        public long Version, Queued = -1, Flooded = -1;
        public RoomCells? Cells;
        public Task Running = Task.CompletedTask;
    }

    private sealed class Owner
    {
        public readonly MapManager Maps;
        public readonly string MapId;
        public Owner(MapManager maps, string mapId) { Maps = maps; MapId = mapId; }
    }

    private readonly Dictionary<string, MapRooms> _rooms = new();
    private static readonly ConditionalWeakTable<World, Owner> Worlds = new();

    /// <summary>Threads the server floods on: half the machine, the game keeps the rest.</summary>
    private static int RoomThreads => Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>The fixed geometry of a map changed (or it loaded): its rooms are to be flooded again.</summary>
    private void RoomsChanged(string mapId)
    {
        if (!TriangleGeometry.Enabled || !_maps.TryGetValue(mapId, out var m)) return;
        MapRooms r;
        lock (_rooms) if (!_rooms.TryGetValue(mapId, out r!)) _rooms[mapId] = r = new MapRooms();
        lock (r.Lock) r.Version++;
        Worlds.AddOrUpdate(m.world, new Owner(this, mapId));
        if (PrefetchRooms) Queue(mapId, r);
    }

    /// <summary>A flood of what the map holds now, after any under way (they share a cache). On the game thread: the
    /// entities are read here, the flood runs off it.</summary>
    private void Queue(string mapId, MapRooms r)
    {
        long version;
        lock (r.Lock)
        {
            if (r.Queued == r.Version) return;
            version = r.Version;
            r.Queued = version;
        }
        if (!_maps.TryGetValue(mapId, out var m) || !_geometry.TryGetValue(mapId, out var geometry)) return;
        var defs = EntityDefinitionFactory.StaticDefinitions(m.world);
        var world = geometry.World;
        var origin = m.data.MinBound;
        lock (r.Lock)
        {
            r.Running = r.Running.ContinueWith(_ =>
            {
                try
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var cells = EntityAcoustics.RoomsByFlood(world, defs, origin, r.Cache, RoomThreads);
                    lock (r.Lock) if (version > r.Flooded) { r.Cells = cells; r.Flooded = version; }
                    Log.Information("MapManager: '{Id}' rooms from the walls: {Rooms} flooded in {Ms:F0} ms ({Hits} group(s) as they were, {Misses} flooded).",
                                    mapId, cells.Flooded.Count, clock.Elapsed.TotalMilliseconds, r.Cache.Hits, r.Cache.Misses);
                }
                catch (Exception ex) { Log.Error(ex, "MapManager: '{Id}': the rooms could not be flooded; their boxes serve.", mapId); }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>The rooms of a map as its walls make them, flooded now if they are not (null: no triangle world).</summary>
    public RoomCells? RoomsOf(string mapId)
    {
        MapRooms? r;
        lock (_rooms) _rooms.TryGetValue(mapId, out r);
        if (r == null) return null;
        Queue(mapId, r);
        if (PrefetchRooms) lock (r.Lock) return r.Cells;   // never a tick held up: the last flood, or the boxes
        Task running;
        lock (r.Lock) running = r.Running;
        running.Wait();
        lock (r.Lock) return r.Cells;
    }

    /// <summary>The rooms of the map a world is, as its walls make them; null for a world no map holds.</summary>
    public static RoomCells? RoomsOf(World world)
        => Worlds.TryGetValue(world, out var owner) ? owner.Maps.RoomsOf(owner.MapId) : null;

    /// <summary>
    /// The region a point is in, as the client finds it (SpatialService.GetRegionAt): the room whose flood holds it,
    /// else the smallest box holding it of the regions that did not flood (a yard, a street, a room whose middle is
    /// in a solid), else the outside.
    /// </summary>
    public static int RegionAt(World world, Vector3 p)
    {
        var floods = RoomsOf(world);
        if (floods != null && floods.TryRoomAt(p, out int room)) return room;
        int best = AcousticConstants.GlobalRegionId;
        float volume = float.MaxValue;
        world.Query(new QueryDescription().WithAll<Transform, RegionComponent>(), (Entity e, ref Transform t, ref RegionComponent r) =>
        {
            if (floods != null && floods.Flooded.Contains(e.Id)) return;
            var size = r.RoomSize;
            if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) return;
            var local = Vector3.Transform(p - t.Position, Quaternion.Inverse(t.Rotation));
            if (MathF.Abs(local.X) > size.X * 0.5f || MathF.Abs(local.Y) > size.Y * 0.5f || MathF.Abs(local.Z) > size.Z * 0.5f) return;
            float v = size.X * size.Y * size.Z;
            if (v < volume) { volume = v; best = e.Id; }
        });
        return best;
    }
}
