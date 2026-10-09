using System.Numerics;
using System.Text.Json;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.OneWorld;

/// <summary>A place in the world a player can arrive at: what it is called and where it is.</summary>
public sealed record WorldPlace(string Id, string Name, double Lat, double Lon);

/// <summary>
/// The one world on this server (docs/WORLD_STREAMING.md, stage 2): frames of it as maps, their tiles
/// loaded as players come near and let go when nobody is near, arrivals at places by name.
///
/// <para><b>A frame</b> is a map whose (0, 0) is the south-west corner of a 250 m square of a UTM zone,
/// x east and z north along the grid, y metres over the sea less the frame's base (the ground where it
/// was first arrived at, to the metre). Its tile (x, z) is the world tile that many squares from the
/// corner. A frame reaches <see cref="FrameHalfMetres"/> each way; past that is the edge of the map, and
/// moving a frame along with its players (a rebase) is stage 3. Arrivals within a frame's reach share
/// it, so two players arriving in one town meet.</para>
///
/// <para><b>Tiles.</b> Every few ticks, for each player on a frame, the world tiles within their far
/// radius are wanted: a stored one is loaded into the frame (its ground an entity), a missing one is made
/// in the background (WorldTileService). A tile nobody holds and nobody is near goes after
/// <see cref="UnloadAfter"/>. A tile not loaded cannot be walked into (SharedMovementEngine's fence).</para>
/// </summary>
public sealed class WorldMaps
{
    public const string IdPrefix = "world@";
    public const float FrameHalfMetres = 6000f;
    public static readonly TimeSpan UnloadAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ArrivalTimeout = TimeSpan.FromMinutes(2);

    private readonly MapManager _maps;
    private readonly WorldTileService _service;
    private readonly Func<IEnumerable<UserSession>> _sessions;
    private readonly Dictionary<string, Frame> _frames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Pending> _pending = new();

    public WorldTileService Service => _service;
    public IReadOnlyList<WorldPlace> Places { get; }

    /// <summary>A frame: where it is, its base height, and the tiles loaded in it.</summary>
    public sealed class Frame
    {
        public string Id = "";
        public WorldTileKey Origin;
        public float BaseY;
        public WorldPlace Place = null!;
        public readonly Dictionary<TileKey, List<Entity>> Loaded = new();
        public readonly Dictionary<TileKey, DateTime> UnwantedSince = new();
        public DateTime EmptySince = DateTime.UtcNow;

        /// <summary>The world tile under one of the frame's own tiles.</summary>
        public WorldTileKey WorldKeyOf(TileKey local) => Origin.Offset(local.X, local.Z);

        /// <summary>Where a point of the zone is in the frame.</summary>
        public Vector3 Local(double easting, double northing, double heightOverSea)
            => new((float)(easting - Origin.Easting), (float)(heightOverSea - BaseY), (float)(northing - Origin.Northing));
    }

    private sealed record Pending(UserSession Session, WorldPlace Place, WorldTileKey Key, double Easting, double Northing,
                                  DateTime Until, Action<string> Say);

    public WorldMaps(MapManager maps, WorldTileService service, Func<IEnumerable<UserSession>> sessions, IReadOnlyList<WorldPlace> places)
    {
        _maps = maps;
        _service = service;
        _sessions = sessions;
        Places = places;
        _service.InUse = InUse;
    }

    public static bool IsWorldMap(string mapId) => mapId.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);

    private static readonly string[] Compass8 = { "north", "north east", "east", "south east", "south", "south west", "west", "north west" };

    public bool TryGetFrame(string mapId, out Frame frame)
    {
        lock (_frames) return _frames.TryGetValue(mapId, out frame!);
    }

    /// <summary>The frame a place would be arrived at in, if one has been made.</summary>
    public string? FrameIdOf(WorldPlace place)
    {
        var (zone, north, e, n) = Utm.FromLatLon(place.Lat, place.Lon);
        lock (_frames)
            foreach (var f in _frames.Values)
                if (f.Origin.Zone == zone && f.Origin.North == north
                    && Math.Abs(e - f.Origin.Easting) < FrameHalfMetres - 1000 && Math.Abs(n - f.Origin.Northing) < FrameHalfMetres - 1000)
                    return f.Id;
        return null;
    }

    /// <summary>Where a point of a frame is, said for a player: how far and which way from the place the
    /// frame was made round ("1.2 kilometres north-east of Magnolia, Texas").</summary>
    public string Describe(Frame frame, Vector3 at)
    {
        if (frame.Place.Lat == 0 && frame.Place.Lon == 0) return "the world";
        var (_, _, e, n) = Utm.FromLatLon(frame.Place.Lat, frame.Place.Lon);
        var placeAt = frame.Local(e, n, frame.BaseY);
        float dx = at.X - placeAt.X, dz = at.Z - placeAt.Z;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < 30f) return $"the world, at {frame.Place.Name}";
        float bearing = (MathF.Atan2(dx, dz) * 180f / MathF.PI + 360f) % 360f;
        string way = Compass8[(int)((bearing + 22.5f) / 45f) % 8];
        string far = d < 1000f ? $"{d:F0} metres" : $"{d / 1000f:F1} kilometres";
        return $"the world, {far} {way} of {frame.Place.Name}";
    }

    /// <summary>Whether a world tile is loaded in any frame: the store's cap never drops it.</summary>
    public bool InUse(WorldTileKey key)
    {
        lock (_frames)
            foreach (var f in _frames.Values)
                if (f.Origin.Zone == key.Zone && f.Origin.North == key.North
                    && f.Loaded.ContainsKey(new TileKey(key.X - f.Origin.X, key.Z - f.Origin.Z))) return true;
        return false;
    }

    // ═══ Places ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The places to arrive at: world_places.json in the server's folder (a list of Id, Name, Lat, Lon),
    /// and every loaded map of a real place, at its origin (where its spawn address is).
    /// </summary>
    public static List<WorldPlace> LoadPlaces(MapManager maps, string? path = null)
    {
        var places = new List<WorldPlace>();
        path ??= Path.GetFullPath("world_places.json");
        try
        {
            if (File.Exists(path))
                places.AddRange(JsonSerializer.Deserialize<List<WorldPlace>>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                    ?? new List<WorldPlace>());
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Warning(ex, "World: {Path} could not be read.", path);
        }
        foreach (var id in maps.LoadedMapIds.OrderBy(i => i, StringComparer.Ordinal))
            if (maps.TryGetMapData(id, out var d) && d.GeoOrigin is { } g && !places.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                places.Add(new WorldPlace(id, d.DisplayName, g.Lat, g.Lon));
        return places;
    }

    /// <summary>A place by its id, or by the words of its name ("magnolia", "bobcat lane"), or a latitude and
    /// longitude ("30.1237, -95.7409"), which only builders are told about.</summary>
    public WorldPlace? FindPlace(string said)
    {
        said = said.Trim();
        if (said.Length == 0) return null;
        var parts = said.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lat)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lon)
            && Math.Abs(lat) <= 84 && Math.Abs(lon) <= 180)
            return new WorldPlace($"{lat:F5},{lon:F5}", $"{lat:F5}, {lon:F5}", lat, lon);
        foreach (var p in Places)
            if (p.Id.Equals(said, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(said, StringComparison.OrdinalIgnoreCase)) return p;
        var words = said.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Places.FirstOrDefault(p => words.All(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                                                        || p.Id.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    // ═══ Arriving ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Takes a player to a place in the world: into the frame it is in (made when it is the first), as
    /// soon as the tile they arrive in is there. <paramref name="go"/> moves them to a map (GameServer.MoveToMap);
    /// <paramref name="say"/> tells them what is happening.
    /// </summary>
    public void Arrive(UserSession session, WorldPlace place, Action<string> say)
    {
        var (zone, north, e, n) = Utm.FromLatLon(place.Lat, place.Lon);
        var key = WorldTileKey.Of(zone, north, e, n);
        lock (_pending)
        {
            _pending.RemoveAll(p => p.Session == session);
            _pending.Add(new Pending(session, place, key, e, n, DateTime.UtcNow + ArrivalTimeout, say));
        }
        // The tile they arrive in and the eight round it, nearest first.
        _service.Want(key);
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if (dx != 0 || dz != 0) _service.Want(key.Offset(dx, dz));
        if (!_service.Store.Has(key)) say($"Building the world at {place.Name}. This takes a moment the first time anyone goes there.");
    }

    /// <summary>Arrivals whose tile is ready: into their frame (made if need be), stood on the ground.</summary>
    private void Arrivals(Action<UserSession, string, Vector3> go)
    {
        List<Pending> ready;
        lock (_pending)
        {
            var now = DateTime.UtcNow;
            foreach (var p in _pending.Where(p => p.Until < now).ToList())
            {
                p.Say($"The world at {p.Place.Name} could not be built just now. Try again in a minute.");
                _pending.Remove(p);
            }
            ready = _pending.Where(p => _service.Store.Has(p.Key)).ToList();
            foreach (var p in ready) _pending.Remove(p);
        }
        foreach (var p in ready)
        {
            var frame = FrameFor(p.Key, p.Easting, p.Northing, p.Place);
            if (frame == null) { p.Say($"The world at {p.Place.Name} could not be read. Try again."); continue; }
            var local = new TileKey(p.Key.X - frame.Origin.X, p.Key.Z - frame.Origin.Z);
            if (!Load(frame, local)) continue;
            _maps.RefreshGrid(frame.Id);
            var at = frame.Local(p.Easting, p.Northing, frame.BaseY);
            float ground = GroundAt(frame, at);
            at.Y = ground > -1000f ? ground + 0.2f : 0f;
            go(p.Session, frame.Id, at);
        }
    }

    /// <summary>The frame a point of the world is arrived at in: one already made that reaches it with room
    /// to spare, or a new one with its corner at the point's tile and its base the ground there.</summary>
    private Frame? FrameFor(WorldTileKey key, double easting, double northing, WorldPlace place)
    {
        lock (_frames)
            foreach (var f in _frames.Values)
                if (f.Origin.Zone == key.Zone && f.Origin.North == key.North
                    && Math.Abs(easting - f.Origin.Easting) < FrameHalfMetres - 1000 && Math.Abs(northing - f.Origin.Northing) < FrameHalfMetres - 1000)
                    return f;
        if (!_service.Store.TryRead(key, out var tile) || tile.Terrain == null) return null;
        var frame = new Frame
        {
            Id = IdPrefix + key,
            Origin = key,
            BaseY = MathF.Round(tile.Terrain.BaseY),
            // Named for the place it was first arrived at: "300 metres north of" it.
            Place = place,
        };
        float half = FrameHalfMetres;
        var map = new MapData
        {
            Id = frame.Id,
            Name = "the world",
            Description = $"The world, round {frame.Place.Name}: ground from the US Geological Survey's 3D Elevation Program, made as players come near.",
            TileMetres = (float)WorldTileKey.TileMetres,
            MinBound = new Vector3(-half, -600f, -half),
            MaxBound = new Vector3(half, 1500f, half),
            PlayMin = new Vector3(-half, -600f, -half),
            PlayMax = new Vector3(half, 1500f, half),
            MinimumY = -620f,
            Size = new Vector3(2 * half, 2100f, 2 * half),
            SpawnPoint = new Transform { Position = new Vector3(125f, 1f, 125f), Rotation = Quaternion.Identity },
            VoxelResolution = 1f,
            OcclusionFloor = 0.1f,
            IsPublic = true,
        };
        if (!_maps.AddWorldMap(map)) return null;
        lock (_frames) _frames[frame.Id] = frame;
        Log.Information("World: frame {Frame} made round {Place} (base {Base} m over the sea).", frame.Id, frame.Place.Name, frame.BaseY);
        return frame;
    }

    private float GroundAt(Frame frame, Vector3 at)
    {
        if (!_maps.TryGetMap(frame.Id, out var world, out _, out var grid, out _)) return -1000f;
        _maps.SyncGeometry(frame.Id);
        return PhysicsUtils.GetGroundHeight(world, grid, at + new Vector3(0f, 2000f, 0f), out _);
    }

    // ═══ Tiles round players ══════════════════════════════════════════════════════════════════════

    /// <summary>One pass over every frame: tiles wanted round each player, loaded when they are made, let
    /// go when nobody is near; then the arrivals that are ready. Called every few ticks on the tick thread.</summary>
    public void Update(Action<UserSession, string, Vector3> go)
    {
        var sessions = _sessions().ToList();
        List<Frame> frames;
        lock (_frames) frames = _frames.Values.ToList();
        foreach (var frame in frames)
        {
            if (!_maps.TryGetTiles(frame.Id, out var tiles)) continue;
            var wanted = new HashSet<TileKey>();
            var held = new HashSet<TileKey>();
            foreach (var s in sessions)
            {
                if (!s.CurrentMapId.Equals(frame.Id, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var k in s.Tiles.Levels.Keys) held.Add(k);
                if (!TryPositionOf(frame.Id, s, out var at)) continue;
                float reach = s.Tiles.Radii.FarMetres + 100f;
                var lo = TileKey.Of(at - new Vector3(reach, 0, reach), tiles.TileMetres);
                var hi = TileKey.Of(at + new Vector3(reach, 0, reach), tiles.TileMetres);
                for (int x = Math.Max(lo.X, tiles.Min.X); x <= Math.Min(hi.X, tiles.Max.X); x++)
                    for (int z = Math.Max(lo.Z, tiles.Min.Z); z <= Math.Min(hi.Z, tiles.Max.Z); z++)
                    {
                        var k = new TileKey(x, z);
                        if (k.DistanceFrom(at, tiles.TileMetres) <= reach) wanted.Add(k);
                    }
            }
            bool changed = false;
            using (_maps.DeferGrid())
            {
                foreach (var k in wanted.OrderBy(k => k.X).ThenBy(k => k.Z))
                {
                    frame.UnwantedSince.Remove(k);
                    if (frame.Loaded.ContainsKey(k)) continue;
                    var wk = frame.WorldKeyOf(k);
                    if (_service.Store.Has(wk)) changed |= Load(frame, k);
                    else _service.Want(wk);
                }
                var now = DateTime.UtcNow;
                foreach (var k in frame.Loaded.Keys.ToList())
                {
                    if (wanted.Contains(k) || held.Contains(k)) continue;
                    if (!frame.UnwantedSince.TryGetValue(k, out var since)) { frame.UnwantedSince[k] = now; continue; }
                    if (now - since < UnloadAfter) continue;
                    foreach (var e in frame.Loaded[k]) _maps.DestroyEntity(frame.Id, e);
                    tiles.RemoveTile(k);
                    frame.Loaded.Remove(k);
                    frame.UnwantedSince.Remove(k);
                    changed = true;
                }
            }
            if (changed) _maps.RefreshGrid(frame.Id);
        }
        Arrivals(go);
    }

    private bool TryPositionOf(string mapId, UserSession s, out Vector3 at)
    {
        at = default;
        if (s.Entity == Entity.Null || !_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return false;
        if (!world.IsAlive(s.Entity) || !world.Has<Transform>(s.Entity)) return false;
        at = world.Get<Transform>(s.Entity).Position;
        return true;
    }

    /// <summary>A stored tile into its frame: its ground as an entity on the "ground" layer.</summary>
    private bool Load(Frame frame, TileKey local)
    {
        if (frame.Loaded.ContainsKey(local)) return true;
        if (!_maps.TryGetMap(frame.Id, out var world, out _, out _, out _) || !_maps.TryGetTiles(frame.Id, out var tiles)) return false;
        var wk = frame.WorldKeyOf(local);
        if (!_service.Store.TryRead(wk, out var tile)) return false;
        var entities = new List<Entity>();
        if (tile.Terrain is { Posts: >= 2 } t)
        {
            var e = TerrainTiles.Spawn(world, local.X * tiles.TileMetres, local.Z * tiles.TileMetres, t.BaseY - frame.BaseY, t.ToComponent());
            _maps.IndexEntity(frame.Id, e);
            entities.Add(e);
        }
        tiles.AddTile(local, entities.Select(e => (e, TileDetail.Coarse)));
        frame.Loaded[local] = entities;
        _service.Store.Touch(wk);
        return true;
    }
}
