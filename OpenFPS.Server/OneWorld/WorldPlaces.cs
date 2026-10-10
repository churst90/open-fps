using System.Numerics;
using System.Text.Json;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using EntityData = OpenFPS.Server.Repositories.EntityData;
using Serilog;

namespace OpenFPS.Server.OneWorld;

/// <summary>
/// The maps of real places (tools/gen_osm.py) copied into the one world (docs/WORLD_STREAMING.md, Places
/// in the world). A place's map is laid on the world's UTM grid, so a tile of the world over it is the map
/// moved, never turned:
/// <list type="bullet">
/// <item>its ground is the map's survey posts graded to the map's slabs, exactly as the map's own terrain is
/// (TerrainBuilder), laid over whole world tiles;</item>
/// <item>its things are the map's, each in the one tile its middle is in, rooms and the doorways between
/// them together in the tile of their middle, with the materials and indoor-ness measured on the map;</item>
/// <item>its roads, junctions and traffic are the place's as a whole, given to a frame of the world that
/// reaches the place (<see cref="ForFrame"/>), not cut into tiles.</item>
/// </list>
/// Tiles are made through WorldTileService (its Placed hook) and kept in the world store like any other,
/// marked as placed: the cap never drops one, and a tile copied from an older map is copied again.
/// </summary>
public sealed class WorldPlaces
{
    /// <summary>What a copied tile holds and how: a change copies every place again.</summary>
    public const int FormatVersion = 2;

    public sealed class Place
    {
        public required string MapId;
        public required MapData Map;
        public int Zone;
        public bool North;
        /// <summary>The map's (0, 0) on the grid, and the height over the sea of its y = 0.</summary>
        public double Easting, Northing, SeaY;
        /// <summary>The world tiles the place's ground covers, corner to corner.</summary>
        public WorldTileKey Min, Max;
        public string Version = "";
        public readonly Dictionary<WorldTileKey, List<EntityData>> Things = new();
        public List<TerrainBuilder.Slab> Slabs = new();
        internal Dictionary<WorldTileKey, float[]>? Ground;
        /// <summary>Each tile's drainage, worked out with its ground (docs/RUNNING_WATER.md 13).</summary>
        internal Dictionary<WorldTileKey, Water.TileDrainage>? Drainage;
        /// <summary>What covers each 2 m cell of the place's tiles, read off the map's things when it was copied
        /// (Water.SurfaceRaster), row by row from the first tile's south-west corner.</summary>
        internal byte[] Surfaces = Array.Empty<byte>();
        internal readonly object Gate = new();

        public bool Covers(WorldTileKey k)
            => k.Zone == Zone && k.North == North && k.X >= Min.X && k.X <= Max.X && k.Z >= Min.Z && k.Z <= Max.Z;

        /// <summary>A point of the map on the grid.</summary>
        public (double Easting, double Northing, double OverSea) Grid(Vector3 map)
            => (Easting + map.X, Northing + map.Z, SeaY + map.Y);
    }

    private readonly List<Place> _places = new();
    public IReadOnlyList<Place> Places => _places;

    /// <summary>
    /// Every loaded map of a real place on the grid (MapData.Utm and its survey), copied out ready to be
    /// cut into tiles. Runs on the tick thread at start: it reads the maps' worlds for what their rooms
    /// measured and what their ground is graded to.
    /// </summary>
    public static WorldPlaces FromMaps(MapManager maps)
    {
        var places = new WorldPlaces();
        foreach (var id in maps.LoadedMapIds.OrderBy(i => i, StringComparer.Ordinal))
        {
            if (!maps.TryGetMapData(id, out var m) || m.Utm == null || m.Elevation == null || m.IsWorld) continue;
            if (!maps.TryGetMap(id, out var world, out _, out _, out _)) continue;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                places._places.Add(Copy(maps, id, m, world));
                Log.Information("World: '{Map}' copied into the world as {Tiles} tiles ({Ms} ms).", id,
                                places._places[^1].Things.Count, clock.ElapsedMilliseconds);
            }
            catch (Exception ex) { Log.Error(ex, "World: '{Map}' could not be copied into the world.", id); }
        }
        return places;
    }

    private static Place Copy(MapManager maps, string id, MapData m, World world)
    {
        var e = m.Elevation!;
        var u = m.Utm!;
        var p = new Place
        {
            MapId = id, Map = m, Zone = u.Zone, North = u.North, Easting = u.Easting, Northing = u.Northing,
            SeaY = -e.SeaLevelY,
            Version = $"{FormatVersion}:{maps.GetMapChecksum(id)}",
        };
        // The survey's posts span whole tiles of the grid (fetch_place.py elevation).
        double e0 = u.Easting + e.OriginX, n0 = u.Northing + e.OriginZ;
        double e1 = e0 + (e.Columns - 1) * e.Spacing, n1 = n0 + (e.Rows - 1) * e.Spacing;
        p.Min = WorldTileKey.Of(u.Zone, u.North, e0 + 1, n0 + 1);
        p.Max = WorldTileKey.Of(u.Zone, u.North, e1 - 1, n1 - 1);
        p.Slabs = TerrainBuilder.Slabs(world);
        // What covers the ground, for the rain: read now, while the map's world is at hand (the tick thread).
        int cellsPerTile = (int)Math.Round(WorldTileKey.TileMetres / WorldTileService.Spacing);
        p.Surfaces = Water.SurfaceRaster.Of(world, (float)(p.Min.Easting - p.Easting), (float)(p.Min.Northing - p.Northing),
                                            (p.Max.X - p.Min.X + 1) * cellsPerTile, (p.Max.Z - p.Min.Z + 1) * cellsPerTile,
                                            WorldTileService.Spacing);

        var measured = maps.AuthoredEntities(id);
        var names = MaterialNames();
        var byId = new Dictionary<int, EntityData>();
        foreach (var d in m.Entities) if (d.EntityId > 0) byId[d.EntityId] = d;

        // Rooms and the doorways between them go together, in the tile of their middle.
        var parent = new Dictionary<int, int>();
        int Find(int x) { while (parent.TryGetValue(x, out var q) && q != x) x = parent[x] = parent.GetValueOrDefault(q, q); return x; }
        foreach (var d in m.Entities)
        {
            if (d.EntityId <= 0) continue;
            foreach (var room in new[] { d.RegionAId, d.RegionBId })
                if (room is int r && byId.ContainsKey(r))
                {
                    parent.TryAdd(d.EntityId, d.EntityId);
                    parent.TryAdd(r, r);
                    int a = Find(d.EntityId), b = Find(r);
                    if (a != b) parent[a] = b;
                }
        }
        var middle = new Dictionary<int, (double X, double Z, int N)>();
        foreach (int fid in parent.Keys)
        {
            int root = Find(fid);
            var at = byId[fid].Position;
            middle[root] = middle.TryGetValue(root, out var s) ? (s.X + at.X, s.Z + at.Z, s.N + 1) : (at.X, at.Z, 1);
        }

        foreach (var d in m.Entities)
        {
            double ox = d.Position.X, oz = d.Position.Z;
            if (d.EntityId > 0 && parent.ContainsKey(d.EntityId))
            {
                var s = middle[Find(d.EntityId)];
                ox = s.X / s.N; oz = s.Z / s.N;
            }
            var key = WorldTileKey.Of(p.Zone, p.North, p.Easting + ox, p.Northing + oz);
            var c = d.Copy();
            c.Position = new Vector3((float)(p.Easting + d.Position.X - key.Easting), (float)(p.SeaY + d.Position.Y),
                                     (float)(p.Northing + d.Position.Z - key.Northing));
            c.Tile = null;
            // What the map's rooms measured (MapManager.SurveyRegions), carried so a tile needs no survey.
            if (d.EntityId > 0 && measured.TryGetValue(d.EntityId, out var made) && world.IsAlive(made) && world.Has<RegionComponent>(made))
            {
                var r = world.Get<RegionComponent>(made);
                if (d.RoomMaterials == null && d.Materials == null)
                    c.RoomMaterials = r.Materials.Select(i => names.TryGetValue(i, out var n) ? n : "Generic").ToArray();
                c.IsIndoor ??= r.IsIndoor;
            }
            if (!p.Things.TryGetValue(key, out var list)) p.Things[key] = list = new List<EntityData>();
            list.Add(c);
        }
        return p;
    }

    /// <summary>Each acoustic material's index (as a room's faces hold it) to its name.</summary>
    private static Dictionary<int, string> MaterialNames()
    {
        var names = new Dictionary<int, string>();
        foreach (var n in AcousticRegistry.KnownMaterials())
            if (AcousticRegistry.TryGetResonanceIndex(n, out int i)) names.TryAdd(i, n);
        return names;
    }

    public bool TryGetPlace(WorldTileKey key, out Place place)
    {
        foreach (var p in _places)
            if (p.Covers(key)) { place = p; return true; }
        place = null!;
        return false;
    }

    public Place? ByMap(string mapId) => _places.FirstOrDefault(p => p.MapId.Equals(mapId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Drops every stored tile of a place that was not copied from the map as it is now (an older
    /// map, or the survey alone before the place was copied in), so it is copied again when wanted.</summary>
    public int DropStale(WorldStore store)
    {
        int dropped = 0;
        foreach (var p in _places)
            for (int x = p.Min.X; x <= p.Max.X; x++)
                for (int z = p.Min.Z; z <= p.Max.Z; z++)
                {
                    var k = new WorldTileKey(p.Zone, p.North, x, z);
                    if (!store.Has(k)) continue;
                    var (place, version) = store.PlacedFrom(k);
                    if (place == p.MapId && version == p.Version) continue;
                    store.Drop(k);
                    dropped++;
                }
        return dropped;
    }

    /// <summary>
    /// A tile of the world over a place, copied from its map; null for a tile no place covers. The service's
    /// Placed hook: runs on its threads, and reads only what <see cref="FromMaps"/> copied out.
    /// </summary>
    public WorldTile? Make(WorldTileKey key)
    {
        if (!TryGetPlace(key, out var p)) return null;
        var ground = GroundOf(p);
        if (!ground.TryGetValue(key, out var heights)) return null;
        var terrain = TerrainTiles.Component(WorldTileService.Posts, WorldTileService.Spacing, heights, out float baseY);
        return new WorldTile
        {
            Key = key.ToString(),
            Generator = WorldStore.GeneratorVersion,
            MadeUtc = DateTime.UtcNow,
            Source = $"{p.Map.DisplayName} ({p.Map.Elevation!.Source}; {p.MapId}.json)",
            Place = p.MapId,
            PlaceVersion = p.Version,
            Terrain = new WorldTile.TerrainData
            {
                Posts = terrain.Posts, Spacing = terrain.Spacing, BaseY = baseY,
                HeightsCm = terrain.HeightsCm, Cells = terrain.Cells, Materials = terrain.Materials,
            },
            Entities = p.Things.TryGetValue(key, out var things) ? things : new List<EntityData>(),
            Drainage = p.Drainage != null && p.Drainage.TryGetValue(key, out var drainage) ? drainage : null,
        };
    }

    /// <summary>
    /// The place's ground over every tile it covers, metres over the sea: the map's posts and slabs moved so
    /// the world's tiles are TerrainBuilder's tiles, then laid and graded as the map's own terrain is. Made
    /// once, the first time a tile of the place is.
    /// </summary>
    private static Dictionary<WorldTileKey, float[]> GroundOf(Place p)
    {
        lock (p.Gate)
        {
            if (p.Ground != null) return p.Ground;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Moved so (0, 0) is the corner of the place's first tile, and up so heights are over the sea.
            double dx = p.Easting - p.Min.Easting, dz = p.Northing - p.Min.Northing, dy = p.SeaY;
            var elevation = p.Map.Elevation!.Moved(dx, dy, dz);
            var off2 = new Vector2((float)dx, (float)dz);
            var slabs = p.Slabs.Select(s => new TerrainBuilder.Slab(s.Point + new Vector3((float)dx, (float)dy, (float)dz), s.Up,
                                                                     s.A + off2, s.B + off2, s.C + off2, s.D + off2)).ToList();
            var size = new Vector3((p.Max.X - p.Min.X + 1) * (float)WorldTileKey.TileMetres, 0f, (p.Max.Z - p.Min.Z + 1) * (float)WorldTileKey.TileMetres);
            var ground = new Dictionary<WorldTileKey, float[]>();
            var laid = TerrainBuilder.Lay(elevation, slabs, Vector3.Zero, size, WorldTileService.Spacing);
            foreach (var t in laid)
                ground[p.Min.Offset(t.X, t.Z)] = t.Heights;
            p.Drainage = DrainageOf(p, laid);
            Log.Information("World: '{Map}' ground laid over {Tiles} tiles of the world, graded to {Slabs} slabs ({Ms} ms).",
                            p.MapId, ground.Count, slabs.Count, clock.ElapsedMilliseconds);
            return p.Ground = ground;
        }
    }

    /// <summary>The drainage of every tile of a place, the place's whole ground routed at once as the map's own is
    /// (Water.MapDrainage), so the place's tiles agree with each other and with the map.</summary>
    private static Dictionary<WorldTileKey, Water.TileDrainage> DrainageOf(Place p, List<TerrainBuilder.Tile> laid)
    {
        var result = new Dictionary<WorldTileKey, Water.TileDrainage>();
        if (laid.Count == 0) return result;
        int posts = laid[0].Posts, cells = posts - 1;
        int ntx = laid.Max(t => t.X) + 1, ntz = laid.Max(t => t.Z) + 1;
        int px = ntx * cells + 1, pz = ntz * cells + 1;
        var grid = new float[px * pz];
        foreach (var t in laid)
            for (int j = 0; j < posts; j++) Array.Copy(t.Heights, j * posts, grid, (t.Z * cells + j) * px + t.X * cells, posts);
        int cx = px - 1;
        var perTile = Water.Drainage.OfGrid(grid, px, pz, cells, WorldTileService.Spacing, (tx, tz) =>
        {
            var s = new byte[cells * cells];
            for (int j = 0; j < cells; j++)
                for (int i = 0; i < cells; i++)
                {
                    int k = (tz * cells + j) * cx + tx * cells + i;
                    byte v = k < p.Surfaces.Length ? p.Surfaces[k] : Water.SurfaceRaster.Uncovered;
                    s[j * cells + i] = v == Water.SurfaceRaster.Uncovered ? (byte)GroundSurface.Open : v;
                }
            return s;
        }, Water.Drainage.WholeGrid);
        for (int tx = 0; tx < perTile.GetLength(0); tx++)
            for (int tz = 0; tz < perTile.GetLength(1); tz++)
                result[p.Min.Offset(tx, tz)] = perTile[tx, tz];
        return result;
    }

    // ═══ A frame of the world over a place ═════════════════════════════════════════════════════════

    /// <summary>
    /// What a frame reaching a place takes from its map as a whole: its roads, junctions, traffic and street
    /// life, moved into the frame (whose (0, 0) is <paramref name="origin"/>'s corner, y metres over the sea
    /// less <paramref name="baseY"/>). Filled into <paramref name="frame"/>; the places it took, by map id.
    /// </summary>
    public List<string> ForFrame(MapData frame, WorldTileKey origin, float baseY, float halfMetres)
    {
        var took = new List<string>();
        foreach (var p in _places)
        {
            if (p.Zone != origin.Zone || p.North != origin.North) continue;
            double dx = p.Easting - origin.Easting, dz = p.Northing - origin.Northing, dy = p.SeaY - baseY;
            // Only a place whose ground lies wholly inside the frame.
            double x0 = p.Min.Easting - origin.Easting, z0 = p.Min.Northing - origin.Northing;
            double x1 = (p.Max.X + 1) * WorldTileKey.TileMetres - origin.Easting, z1 = (p.Max.Z + 1) * WorldTileKey.TileMetres - origin.Northing;
            if (x0 < -halfMetres || z0 < -halfMetres || x1 > halfMetres || z1 > halfMetres) continue;
            var d = new Vector3((float)dx, (float)dy, (float)dz);
            var m = p.Map;
            if (m.Roads != null)
                foreach (var r in Clone(m.Roads))
                {
                    r.Centreline = r.Centreline.Select(v => v + d).ToList();
                    r.Tiles = null;
                    (frame.Roads ??= new List<RoadData>()).Add(r);
                }
            if (m.Junctions != null)
                foreach (var j in Clone(m.Junctions))
                {
                    j.Position += d;
                    j.Tile = null;
                    (frame.Junctions ??= new List<JunctionData>()).Add(j);
                }
            if (m.Vehicles != null)
                foreach (var v in Clone(m.Vehicles))
                {
                    if (v.RoadStart != Vector3.Zero) v.RoadStart += d;
                    if (v.RoadEnd != Vector3.Zero) v.RoadEnd += d;
                    (frame.Vehicles ??= new List<VehicleData>()).Add(v);
                }
            if (m.StreetLife != null) frame.StreetLife ??= m.StreetLife;
            if (took.Count == 0)
            {
                frame.Temperature = m.Temperature;
                frame.Humidity = m.Humidity;
            }
            took.Add(p.MapId);
        }
        return took;
    }

    private static List<T> Clone<T>(List<T> list)
        => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(list, MapRepository.JsonOptions), MapRepository.JsonOptions)!;
}
