using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Core;

/// <summary>
/// Which tiles of a streamed map each of its fixed entities belongs to, and how much detail a client
/// must have of a tile to be sent it. Built once, when the map loads (MapManager). A map with no
/// TileMetres has none and is sent whole. See docs/WORLD_STREAMING.md.
///
/// Membership is worked out from geometry, not from the generator's "Tile" tag: an entity belongs to
/// every tile its footprint overlaps. The ground (one slab under the whole map) is in every tile; a
/// named stretch of road goes with each tile it crosses; a door on a tile edge goes with both.
/// Everything fixed that did not come from the map file (the map's zone entity, a foundation the loader
/// added, anything a composite placed) is global: sent to every client wherever it is.
/// </summary>
public sealed class MapTiles
{
    /// <summary>The layers a coarse tile is made of: what keeps far buildings in the way of sound and
    /// the far roads under the traffic. Everything else needs full detail.</summary>
    public static readonly IReadOnlySet<string> CoarseLayers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ground", "roads", "structure", "rail", "water" };

    public readonly record struct Membership(TileKey[] Tiles, TileDetail Needs);

    public float TileMetres { get; }
    public TileKey Min { get; }
    public TileKey Max { get; }

    private readonly Dictionary<int, Membership> _members = new();
    private readonly Dictionary<TileKey, List<int>> _byTile = new();
    private readonly List<int> _global = new();
    private readonly Dictionary<int, Entity> _globalSet = new();

    /// <summary>Entities of the map file in at least one tile.</summary>
    public int TiledCount => _members.Count;
    /// <summary>Fixed entities sent to everyone.</summary>
    public IReadOnlyList<int> Global => _global;
    public IEnumerable<TileKey> Tiles => _byTile.Keys;

    public MapTiles(float tileMetres, TileKey min, TileKey max)
    {
        TileMetres = tileMetres;
        Min = min;
        Max = max;
    }

    /// <summary>
    /// The index for a loaded map. <paramref name="layers"/> names the layer of every entity that came
    /// from the map file (by entity id; null or empty for one with no layer); fixed entities not in it
    /// are global.
    /// </summary>
    public static MapTiles Build(World world, float tileMetres, Vector3 minBound, Vector3 maxBound,
                                 IReadOnlyDictionary<int, string?> layers)
    {
        var tiles = new MapTiles(tileMetres, TileKey.Of(minBound, tileMetres), TileKey.Of(maxBound, tileMetres));
        world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) =>
        {
            if (world.Has<PlayerComponent>(e) || world.Has<Velocity>(e)) return;
            if (!layers.TryGetValue(e.Id, out var layer)) { tiles.AddGlobal(e); return; }
            var (lo, hi) = Footprint(world, e, t);
            var keys = new List<TileKey>();
            var a = TileKey.Of(lo, tileMetres);
            var b = TileKey.Of(hi, tileMetres);
            // Clamped to the map: a slab hanging over the edge belongs to the edge tiles.
            int x0 = Math.Clamp(a.X, tiles.Min.X, tiles.Max.X), x1 = Math.Clamp(b.X, tiles.Min.X, tiles.Max.X);
            int z0 = Math.Clamp(a.Z, tiles.Min.Z, tiles.Max.Z), z1 = Math.Clamp(b.Z, tiles.Min.Z, tiles.Max.Z);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    keys.Add(new TileKey(x, z));
            tiles.Add(e, keys.ToArray(), Needs(world, e, layer));
        });
        return tiles;
    }

    /// <summary>The detail a tile must be at for this entity to be sent with it: coarse for the layers
    /// a coarse tile is made of, except a door or a doorway (it opens into a room a coarse tile does not
    /// have) and a room or named place; full for everything else.</summary>
    internal static TileDetail Needs(World world, Entity e, string? layer)
    {
        if (string.IsNullOrEmpty(layer) || !CoarseLayers.Contains(layer)) return TileDetail.Full;
        if (world.Has<DoorComponent>(e) || world.Has<PortalComponent>(e)) return TileDetail.Full;
        if (world.Has<RegionComponent>(e) && world.Get<RegionComponent>(e).RoomSize.X > 0f) return TileDetail.Full;
        return TileDetail.Coarse;
    }

    /// <summary>The square on the ground an entity covers: its collider or its room, turned.</summary>
    private static (Vector3 Min, Vector3 Max) Footprint(World world, Entity e, in Transform t)
    {
        Vector3 size = Vector3.Zero;
        if (world.Has<ColliderComponent>(e)) size = Vector3.Max(size, world.Get<ColliderComponent>(e).Size);
        if (world.Has<RegionComponent>(e)) size = Vector3.Max(size, world.Get<RegionComponent>(e).RoomSize);
        var rot = t.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : t.Rotation;
        var half = OpenFPS.Common.Systems.FaceOpenings.AxisAlignedHalfExtents(size * 0.5f, rot);
        return (t.Position - half, t.Position + half);
    }

    internal void Add(Entity e, TileKey[] keys, TileDetail needs)
    {
        int id = e.Id;
        if (keys.Length == 0) { AddGlobal(e); return; }
        _members[id] = new Membership(keys, needs);
        foreach (var k in keys)
        {
            if (!_byTile.TryGetValue(k, out var list)) _byTile[k] = list = new List<int>();
            list.Add(id);
        }
    }

    private void AddGlobal(Entity e)
    {
        if (_globalSet.TryAdd(e.Id, e)) _global.Add(e.Id);
    }

    /// <summary>Whether this is one of the map's own fixed entities that every client is sent.</summary>
    public bool IsGlobal(int entityId) => _globalSet.ContainsKey(entityId);

    /// <summary>A global entity by id. Some are not in the map's lookup (the map's own zone entity), so
    /// the index keeps them.</summary>
    public bool TryGetGlobal(int entityId, out Entity entity) => _globalSet.TryGetValue(entityId, out entity);

    /// <summary>Whether this entity is the tile streamer's to send (a fixed entity from the map file).</summary>
    public bool IsTiled(int entityId) => _members.ContainsKey(entityId);

    public bool TryGet(int entityId, out Membership membership) => _members.TryGetValue(entityId, out membership);

    /// <summary>The entities in a tile, at any detail.</summary>
    public IReadOnlyList<int> Members(TileKey key) => _byTile.TryGetValue(key, out var l) ? l : Array.Empty<int>();

    /// <summary>Whether a client holding <paramref name="levels"/> should have this entity: global
    /// entities always, a tiled one if any of its tiles is at the detail it needs.</summary>
    public bool Wanted(int entityId, IReadOnlyDictionary<TileKey, TileDetail> levels)
    {
        if (!_members.TryGetValue(entityId, out var m)) return true;
        foreach (var k in m.Tiles)
            if (levels.TryGetValue(k, out var level) && level >= m.Needs) return true;
        return false;
    }

    /// <summary>Whether a point is in a tile the client has at any detail: what decides whether a moving
    /// thing, or anything not from the map file, is sent.</summary>
    public bool Holds(Vector3 position, IReadOnlyDictionary<TileKey, TileDetail> levels)
        => levels.ContainsKey(TileKey.Of(position, TileMetres));
}
