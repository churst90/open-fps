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

    /// <summary>How far past a player's far radius tiles are wanted, metres: a tile is loaded on the server
    /// before the player's client is due it.</summary>
    public const float WantMarginMetres = 100f;

    /// <summary>
    /// Seconds ahead a moving player's tiles are wanted however far that is: a tile they could reach in
    /// this long is built or queued before the ones beside or behind them. 3DEP took 2.5 s for 41 tiles on
    /// one run and 60 s on another (two at a time, so up to 3 s a tile), and a single answer can take ten
    /// times that; 30 s is a slow answer and a queue of slow ones ahead of it.
    /// </summary>
    public const float LookAheadSeconds = 30f;

    /// <summary>How fast anybody can head for a tile they are not already moving toward, m/s: a run.
    /// What a tile beside or behind a driver is reached at, and every tile round somebody standing.</summary>
    public const float FreeSpeed = PhysicsConstants.SprintSpeed;

    /// <summary>How far round each place in world_places.json the tiles are made at start, metres: the
    /// far radius at high detail and the margin, so the first visitor at any detail waits for nothing.</summary>
    public const float PrebuildMetres = 1200f + WantMarginMetres;

    private readonly MapManager _maps;
    private readonly WorldTileService _service;
    private readonly Func<IEnumerable<UserSession>> _sessions;
    private readonly Dictionary<string, Frame> _frames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Pending> _pending = new();

    public WorldTileService Service => _service;
    public IReadOnlyList<WorldPlace> Places { get; }

    /// <summary>The time, UTC: what arrivals, held joins and letting tiles go are timed by. A test turns its own.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

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
        /// <summary>Stored tiles being read off the tick thread, and how soon anybody could be in each:
        /// <see cref="Pump"/> puts them in, soonest first.</summary>
        public readonly Dictionary<TileKey, (Task<WorldTile?> Read, double Soon)> Reading = new();
        /// <summary>The real places whose roads and traffic this frame took (WorldPlaces.ForFrame).</summary>
        public List<string> PlacesTaken = new();

        /// <summary>The world tile under one of the frame's own tiles.</summary>
        public WorldTileKey WorldKeyOf(TileKey local) => Origin.Offset(local.X, local.Z);

        /// <summary>Where a point of the zone is in the frame.</summary>
        public Vector3 Local(double easting, double northing, double heightOverSea)
            => new((float)(easting - Origin.Easting), (float)(heightOverSea - BaseY), (float)(northing - Origin.Northing));
    }

    private sealed record Pending(UserSession Session, WorldPlace Place, WorldTileKey Key, double Easting, double Northing,
                                  DateTime Until, Action<string> Say, double? OverSea, Action? Failed);

    /// <summary>A player in a frame on the loading screen: their join is sent when the tiles round where
    /// they will stand, out to their far radius, are all loaded.</summary>
    private sealed class Held
    {
        public required UserSession Session;
        public required string FrameId;
        public required Vector3 At;
        public required float Far;
        public required Action Release;
        public required Action<int, int, bool> Progress;
        public DateTime Since;
        public int LastDone = -1;
        public DateTime LastSent, LastSpoken;
    }

    private readonly List<Held> _held = new();

    /// <summary>How often the waiting player hears how far the building has got, at most.</summary>
    public static readonly TimeSpan SpeakEvery = TimeSpan.FromSeconds(5);

    public WorldMaps(MapManager maps, WorldTileService service, Func<IEnumerable<UserSession>> sessions, IReadOnlyList<WorldPlace> places,
                     WorldPlaces? copied = null)
    {
        _maps = maps;
        _service = service;
        _sessions = sessions;
        Places = places;
        Copied = copied;
        _service.InUse = InUse;
        if (copied != null) _service.Placed = copied.Make;
    }

    /// <summary>The maps of real places copied into the world, or null.</summary>
    public WorldPlaces? Copied { get; }

    /// <summary>A frame was made: its map is loaded, its roads and traffic given (the server spawns its vehicles).</summary>
    public event Action<string>? FrameMade;

    /// <summary>How long a tick may spend putting read tiles into frames (<see cref="Pump"/>).</summary>
    public static readonly TimeSpan PumpBudget = TimeSpan.FromMilliseconds(6);

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
        double? overSea = null;
        // A place with a map in the world arrives where the map's spawn is: the same drive, not the geocode.
        if (Copied?.ByMap(place.Id) is { } copied && copied.Zone == zone && copied.North == north)
        {
            (e, n, double y) = copied.Grid(copied.Map.SpawnPoint.Position);
            overSea = y;
        }
        ArriveAt(session, place, zone, north, e, n, overSea, ArrivalTimeout, say, null);
    }

    /// <summary>
    /// Takes a player to a point of a zone (<paramref name="label"/> names the frame if one is made), stood on
    /// the ground under <paramref name="overSea"/> (metres over the sea; the top of whatever is there if
    /// null). <paramref name="failed"/> is called if they cannot be put there within <paramref name="timeout"/>.
    /// </summary>
    public void ArriveAt(UserSession session, WorldPlace label, int zone, bool north, double easting, double northing, double? overSea,
                         TimeSpan timeout, Action<string> say, Action? failed)
    {
        var key = WorldTileKey.Of(zone, north, easting, northing);
        lock (_pending)
        {
            _pending.RemoveAll(p => p.Session == session);
            _pending.Add(new Pending(session, label, key, easting, northing, Clock() + timeout, say, overSea, failed));
        }
        // Every tile they will be sent on arriving, nearest first: the one they arrive in before any.
        WantRound(key, easting, northing, session.Tiles.Radii.FarMetres + WantMarginMetres, 0.0);
        if (!_service.Store.Has(key)) say($"Building the world at {label.Name}. This takes a moment the first time anyone goes there.");
    }

    /// <summary>
    /// Where a saved place on a frame of the world is (PlayerState: a frame's id names its corner tile): the
    /// zone, half, easting, northing and height over the sea, or null for a map that is not a frame.
    /// </summary>
    public static (int Zone, bool North, double Easting, double Northing, double OverSea)? WhereSaved(string frameId, Vector3 local, float baseY)
    {
        if (!IsWorldMap(frameId) || !WorldTileKey.TryParse(frameId[IdPrefix.Length..], out var origin)) return null;
        return (origin.Zone, origin.North, origin.Easting + local.X, origin.Northing + local.Z, baseY + local.Y);
    }

    /// <summary>The height over the sea a frame's y is counted from: the frame's if it is made, else what it
    /// will be when it is made again (its corner tile's ground, to the metre); null if that is not stored.</summary>
    public double? BaseYOf(string frameId)
    {
        if (TryGetFrame(frameId, out var f)) return f.BaseY;
        if (!IsWorldMap(frameId) || !WorldTileKey.TryParse(frameId[IdPrefix.Length..], out var origin)) return null;
        return _service.Store.TryRead(origin, out var tile) && tile.Terrain != null ? MathF.Round(tile.Terrain.BaseY) : null;
    }

    /// <summary>The listed place nearest a point of a zone, to name a frame by; the point itself if none is
    /// within 50 km.</summary>
    public WorldPlace NearestPlace(int zone, bool north, double easting, double northing)
    {
        WorldPlace? best = null;
        double bestD = 50_000;
        foreach (var p in Places)
        {
            if (p.Lat == 0 && p.Lon == 0) continue;
            var (z, nh, e, n) = Utm.FromLatLon(p.Lat, p.Lon);
            if (z != zone || nh != north) continue;
            double d = Math.Sqrt((e - easting) * (e - easting) + (n - northing) * (n - northing));
            if (d < bestD) { bestD = d; best = p; }
        }
        if (best != null) return best;
        var (lat, lon) = Utm.ToLatLon(zone, north, easting, northing);
        return new WorldPlace($"{lat:F5},{lon:F5}", $"{lat:F5}, {lon:F5}", lat, lon);
    }

    /// <summary>Asks for every tile within <paramref name="metres"/> of a point of a zone, in order of how far
    /// each is, after <paramref name="first"/> (0 for a player waiting, <see cref="WorldTileService.Background"/>
    /// for nobody yet).</summary>
    private void WantRound(WorldTileKey key, double easting, double northing, float metres, double first)
    {
        var round = new Dictionary<WorldTileKey, double>();
        Round(round, key, easting, northing, metres, first);
        foreach (var (k, p) in round.OrderBy(kv => kv.Value)) _service.Want(k, p);
    }

    /// <summary>The tiles within <paramref name="metres"/> of a point of a zone, with how soon somebody
    /// standing there could be in each, after <paramref name="first"/>; kept at the soonest.</summary>
    private static void Round(Dictionary<WorldTileKey, double> into, WorldTileKey key, double easting, double northing, float metres, double first)
    {
        int reach = (int)Math.Ceiling(metres / WorldTileKey.TileMetres);
        var at = new Vector3((float)(easting - key.Easting), 0f, (float)(northing - key.Northing));
        for (int dx = -reach; dx <= reach; dx++)
            for (int dz = -reach; dz <= reach; dz++)
            {
                float d = new TileKey(dx, dz).DistanceFrom(at, (float)WorldTileKey.TileMetres);
                if (d > metres) continue;
                var k = key.Offset(dx, dz);
                double p = first + d / FreeSpeed;
                if (!into.TryGetValue(k, out double had) || p < had) into[k] = p;
            }
    }

    /// <summary>
    /// The tiles round every place to arrive at, made in the background at start after anything a player
    /// wants, so the first visitor to a listed place waits for nothing.
    /// </summary>
    public void Prebuild(float metres = PrebuildMetres)
    {
        foreach (var p in Places)
        {
            if (p.Lat == 0 && p.Lon == 0) continue;
            var (zone, north, e, n) = Utm.FromLatLon(p.Lat, p.Lon);
            WantRound(WorldTileKey.Of(zone, north, e, n), e, n, metres, WorldTileService.Background);
        }
    }

    /// <summary>
    /// Seconds before somebody at <paramref name="at"/> moving at <paramref name="velocity"/> could be in a
    /// tile: its nearest point, at their speed toward it if they are heading that way, or at
    /// <see cref="FreeSpeed"/> (turning and running) if that is sooner. 0 for the tile they are in.
    /// </summary>
    public static double SecondsToReach(Vector3 at, Vector3 velocity, TileKey key, float tileMetres)
    {
        float x0 = key.X * tileMetres, z0 = key.Z * tileMetres;
        float dx = Math.Clamp(at.X, x0, x0 + tileMetres) - at.X;
        float dz = Math.Clamp(at.Z, z0, z0 + tileMetres) - at.Z;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < 1e-3f) return 0.0;
        float toward = (velocity.X * dx + velocity.Z * dz) / d;
        return d / MathF.Max(toward, FreeSpeed);
    }

    /// <summary>
    /// The tiles one player needs, with the seconds before they could be in each: every tile within
    /// <paramref name="reach"/>, and every tile they could reach in <see cref="LookAheadSeconds"/> however
    /// far, kept at the soonest any player could be there.
    /// </summary>
    internal static void Interest(Dictionary<TileKey, double> wanted, MapTiles tiles, Vector3 at, Vector3 velocity, float reach)
    {
        float speed = MathF.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z);
        float box = MathF.Max(reach, speed * LookAheadSeconds);
        var lo = TileKey.Of(at - new Vector3(box, 0, box), tiles.TileMetres);
        var hi = TileKey.Of(at + new Vector3(box, 0, box), tiles.TileMetres);
        for (int x = Math.Max(lo.X, tiles.Min.X); x <= Math.Min(hi.X, tiles.Max.X); x++)
            for (int z = Math.Max(lo.Z, tiles.Min.Z); z <= Math.Min(hi.Z, tiles.Max.Z); z++)
            {
                var k = new TileKey(x, z);
                double t = SecondsToReach(at, velocity, k, tiles.TileMetres);
                if (k.DistanceFrom(at, tiles.TileMetres) > reach && t > LookAheadSeconds) continue;
                if (!wanted.TryGetValue(k, out double had) || t < had) wanted[k] = t;
            }
    }

    /// <summary>Arrivals whose tile is ready: into their frame (made if need be), stood on the ground.</summary>
    private void Arrivals(Action<UserSession, string, Vector3> go)
    {
        List<Pending> ready;
        lock (_pending)
        {
            var now = Clock();
            foreach (var p in _pending.Where(p => p.Until < now).ToList())
            {
                p.Say($"The world at {p.Place.Name} could not be built just now. Try again in a minute.");
                _pending.Remove(p);
                p.Failed?.Invoke();
            }
            ready = _pending.Where(p => _service.Store.Has(p.Key)).ToList();
            foreach (var p in ready) _pending.Remove(p);
        }
        foreach (var p in ready)
        {
            var frame = FrameFor(p.Key, p.Easting, p.Northing, p.Place);
            if (frame == null) { p.Say($"The world at {p.Place.Name} could not be read. Try again."); p.Failed?.Invoke(); continue; }
            var local = new TileKey(p.Key.X - frame.Origin.X, p.Key.Z - frame.Origin.Z);
            if (!Load(frame, local)) { p.Failed?.Invoke(); continue; }
            _maps.RefreshGrid(frame.Id);
            var at = frame.Local(p.Easting, p.Northing, frame.BaseY);
            // Under a known height (a map's spawn, where they left), the ground under it: a drive or a floor,
            // not the roof over it; elsewhere the top of whatever is there.
            float probe = p.OverSea is double overSea ? (float)(overSea - frame.BaseY) + 2f - at.Y : 2000f;
            float ground = GroundAt(frame, at, probe);
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
        // The roads, junctions and traffic of the real places it reaches, whole.
        if (Copied != null) frame.PlacesTaken = Copied.ForFrame(map, key, frame.BaseY, half);
        if (!_maps.AddWorldMap(map)) return null;
        lock (_frames) _frames[frame.Id] = frame;
        Log.Information("World: frame {Frame} made round {Place} (base {Base} m over the sea){Places}.", frame.Id, frame.Place.Name, frame.BaseY,
                        frame.PlacesTaken.Count > 0 ? $", with the roads and traffic of {string.Join(", ", frame.PlacesTaken)}" : "");
        FrameMade?.Invoke(frame.Id);
        return frame;
    }

    private float GroundAt(Frame frame, Vector3 at, float probe = 2000f)
    {
        if (!_maps.TryGetMap(frame.Id, out var world, out _, out var grid, out _)) return -1000f;
        _maps.SyncGeometry(frame.Id);
        return PhysicsUtils.GetGroundHeight(world, grid, at + new Vector3(0f, probe, 0f), out _);
    }

    // ═══ Tiles round players ══════════════════════════════════════════════════════════════════════

    /// <summary>One pass over every frame: tiles wanted round each player, loaded when they are made, let
    /// go when nobody is near; then the arrivals that are ready. Called every few ticks on the tick thread.</summary>
    public void Update(Action<UserSession, string, Vector3> go)
    {
        var sessions = _sessions().ToList();
        List<Frame> frames;
        lock (_frames) frames = _frames.Values.ToList();
        List<Held> holding;
        lock (_held) holding = _held.ToList();
        var ranks = new Dictionary<WorldTileKey, double>();
        foreach (var frame in frames)
        {
            if (!_maps.TryGetTiles(frame.Id, out var tiles)) continue;
            var wanted = new Dictionary<TileKey, double>();
            var held = new HashSet<TileKey>();
            foreach (var s in sessions)
            {
                if (!s.CurrentMapId.Equals(frame.Id, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var k in s.Tiles.Levels.Keys) held.Add(k);
                if (!TryMotionOf(frame.Id, s, out var at, out var velocity)) continue;
                Interest(wanted, tiles, at, velocity, s.Tiles.Radii.FarMetres + WantMarginMetres);
            }
            foreach (var h in holding)
                if (h.FrameId.Equals(frame.Id, StringComparison.OrdinalIgnoreCase))
                    Interest(wanted, tiles, h.At, Vector3.Zero, h.Far + WantMarginMetres);
            bool changed = false;
            using (_maps.DeferGrid())
            {
                foreach (var (k, t) in wanted.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key.X).ThenBy(kv => kv.Key.Z))
                {
                    frame.UnwantedSince.Remove(k);
                    if (frame.Loaded.ContainsKey(k)) continue;
                    var wk = frame.WorldKeyOf(k);
                    if (_service.Store.Has(wk)) Read(frame, k, t);
                    else if (!ranks.TryGetValue(wk, out double had) || t < had) ranks[wk] = t;
                }
                var now = Clock();
                foreach (var k in frame.Loaded.Keys.ToList())
                {
                    if (wanted.ContainsKey(k) || held.Contains(k)) continue;
                    if (!frame.UnwantedSince.TryGetValue(k, out var since)) { frame.UnwantedSince[k] = now; continue; }
                    if (now - since < UnloadAfter) continue;
                    tiles.RemoveEntities(frame.Loaded[k].Select(e => e.Id));
                    foreach (var e in frame.Loaded[k]) _maps.DestroyEntity(frame.Id, e);
                    tiles.RemoveTile(k);
                    frame.Loaded.Remove(k);
                    frame.UnwantedSince.Remove(k);
                    changed = true;
                }
            }
            if (changed) _maps.RefreshGrid(frame.Id);
        }
        // Arrivals whose frame is not made yet: their rings are wanted as much as anyone's.
        lock (_pending)
            foreach (var p in _pending)
                Round(ranks, p.Key, p.Easting, p.Northing, p.Session.Tiles.Radii.FarMetres + WantMarginMetres, 0.0);
        _service.Rank(ranks);
        Holds(sessions);
        Arrivals(go);
    }

    /// <summary>Where a player is and how fast they are going: their own body's velocity, or the vehicle's
    /// they are riding in.</summary>
    private bool TryMotionOf(string mapId, UserSession s, out Vector3 at, out Vector3 velocity)
    {
        at = default;
        velocity = default;
        if (s.Entity == Entity.Null || !_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) return false;
        if (!world.IsAlive(s.Entity) || !world.Has<Transform>(s.Entity)) return false;
        at = world.Get<Transform>(s.Entity).Position;
        if (world.Has<Velocity>(s.Entity)) velocity = world.Get<Velocity>(s.Entity).Linear;
        if (world.Has<OccupantComponent>(s.Entity) && world.Get<OccupantComponent>(s.Entity).RootEntityId is int root and >= 0
            && lookup.TryGetValue(root, out var vehicle) && world.IsAlive(vehicle) && world.Has<Velocity>(vehicle))
            velocity = world.Get<Velocity>(vehicle).Linear;
        return true;
    }

    // ═══ Waiting to arrive ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Holds a player's join to a frame (the map data, or for a text session the spawn) on the loading
    /// screen until every tile round where they will stand, out to their far radius, is loaded; true if it
    /// is held, false if it is all there now and the join goes ahead. <paramref name="progress"/> is told
    /// how many of how many, and whether to say it aloud.
    /// </summary>
    public bool Hold(UserSession session, Vector3 at, Action release, Action<int, int, bool> progress)
    {
        if (!TryGetFrame(session.CurrentMapId, out var frame)) return false;
        var h = new Held
        {
            Session = session, FrameId = frame.Id, At = at, Far = session.Tiles.Radii.FarMetres,
            Release = release, Progress = progress, Since = Clock(),
        };
        var (done, total, _) = Ring(frame, h);
        if (done >= total) return false;
        lock (_held)
        {
            _held.RemoveAll(o => o.Session == session);
            _held.Add(h);
        }
        h.LastDone = done;
        h.LastSent = h.LastSpoken = Clock();
        progress(done, total, true);
        return true;
    }

    /// <summary>Whether a player's join is being held.</summary>
    public bool IsHeld(UserSession session)
    {
        lock (_held) return _held.Any(h => h.Session == session);
    }

    /// <summary>The ring a held player is waiting for: how many tiles are loaded, of how many, and whether
    /// any of the rest is still on its way (queued, being made, or stored and not loaded yet).</summary>
    private (int Done, int Total, bool Coming) Ring(Frame frame, Held h)
    {
        if (!_maps.TryGetTiles(frame.Id, out var tiles)) return (0, 0, false);
        int done = 0, total = 0;
        bool coming = false;
        var lo = TileKey.Of(h.At - new Vector3(h.Far, 0, h.Far), tiles.TileMetres);
        var hi = TileKey.Of(h.At + new Vector3(h.Far, 0, h.Far), tiles.TileMetres);
        for (int x = Math.Max(lo.X, tiles.Min.X); x <= Math.Min(hi.X, tiles.Max.X); x++)
            for (int z = Math.Max(lo.Z, tiles.Min.Z); z <= Math.Min(hi.Z, tiles.Max.Z); z++)
            {
                var k = new TileKey(x, z);
                if (k.DistanceFrom(h.At, tiles.TileMetres) > h.Far) continue;
                total++;
                if (frame.Loaded.ContainsKey(k)) { done++; continue; }
                if (_service.StateOf(frame.WorldKeyOf(k)) != WorldTileService.TileState.Failed) coming = true;
            }
        return (done, total, coming);
    }

    /// <summary>Each held join: let go when its ring is loaded, when nothing more of it can come (the survey
    /// cannot be asked; the edge stops them), or after <see cref="ArrivalTimeout"/>; else told how it goes.</summary>
    private void Holds(List<UserSession> sessions)
    {
        List<Held> holding;
        lock (_held) holding = _held.ToList();
        var now = Clock();
        foreach (var h in holding)
        {
            bool gone = !sessions.Contains(h.Session) || !h.Session.CurrentMapId.Equals(h.FrameId, StringComparison.OrdinalIgnoreCase);
            var (done, total, coming) = !gone && TryGetFrame(h.FrameId, out var frame) ? Ring(frame, h) : (0, 0, false);
            if (gone || done >= total || !coming || now - h.Since > ArrivalTimeout)
            {
                lock (_held) _held.Remove(h);
                if (gone) continue;
                if (done < total)
                    Log.Information("World: {User} let into {Frame} with {Done} of {Total} tiles round them ({Why}).", h.Session.Username,
                                    h.FrameId, done, total, coming ? "waited too long" : "the rest cannot be made now");
                h.Progress(done, total, false);
                h.Release();
                continue;
            }
            if (done == h.LastDone && now - h.LastSent < TimeSpan.FromSeconds(1)) continue;
            bool speak = now - h.LastSpoken >= SpeakEvery;
            h.LastDone = done;
            h.LastSent = now;
            if (speak) h.LastSpoken = now;
            h.Progress(done, total, speak);
        }
    }

    /// <summary>A stored tile into its frame: its ground as an entity on the "ground" layer.</summary>
    private bool Load(Frame frame, TileKey local)
    {
        if (frame.Loaded.ContainsKey(local)) return true;
        if (!_service.Store.TryRead(frame.WorldKeyOf(local), out var tile)) return false;
        frame.Reading.Remove(local);
        return Put(frame, local, tile);
    }

    /// <summary>Starts reading a stored tile off the tick thread, for <see cref="Pump"/> to put in.</summary>
    private void Read(Frame frame, TileKey local, double soon)
    {
        if (frame.Reading.TryGetValue(local, out var reading)) { frame.Reading[local] = (reading.Read, Math.Min(reading.Soon, soon)); return; }
        var wk = frame.WorldKeyOf(local);
        frame.Reading[local] = (Task.Run(() => _service.Store.TryRead(wk, out var tile) ? tile : null), soon);
    }

    /// <summary>
    /// Puts tiles that have been read into their frames, soonest first, for at most <paramref name="budget"/>
    /// (a tile of a town is a thousand things or more). Every tick, on the tick thread.
    /// </summary>
    public void Pump(TimeSpan budget)
    {
        List<Frame> frames;
        lock (_frames) frames = _frames.Values.ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var frame in frames)
        {
            if (frame.Reading.Count == 0) continue;
            bool changed = false;
            using (_maps.DeferGrid())
                foreach (var (local, (read, _)) in frame.Reading.Where(kv => kv.Value.Read.IsCompleted).OrderBy(kv => kv.Value.Soon).ToList())
                {
                    if (clock.Elapsed > budget) break;
                    frame.Reading.Remove(local);
                    if (frame.Loaded.ContainsKey(local) || read.Result is not { } tile) continue;
                    changed |= Put(frame, local, tile);
                }
            if (changed) _maps.RefreshGrid(frame.Id);
        }
    }

    /// <summary>Waits for every tile being read and puts them all in: a test's tick.</summary>
    internal void SettleForTest()
    {
        List<Frame> frames;
        lock (_frames) frames = _frames.Values.ToList();
        foreach (var frame in frames) Task.WaitAll(frame.Reading.Values.Select(r => r.Read).ToArray(), TimeSpan.FromSeconds(30));
        Pump(TimeSpan.MaxValue);
    }

    /// <summary>A tile into its frame: its ground as an entity on the "ground" layer, and what a real place
    /// has standing on it (WorldPlaces), moved from the tile's own metres into the frame's.</summary>
    private bool Put(Frame frame, TileKey local, WorldTile tile)
    {
        if (frame.Loaded.ContainsKey(local)) return true;
        if (!_maps.TryGetMap(frame.Id, out var world, out _, out _, out _) || !_maps.TryGetTiles(frame.Id, out var tiles)) return false;
        var members = new List<(Entity Entity, string? Layer)>();
        if (tile.Terrain is { Posts: >= 2 } t)
        {
            var e = TerrainTiles.Spawn(world, local.X * tiles.TileMetres, local.Z * tiles.TileMetres, t.BaseY - frame.BaseY, t.ToComponent());
            _maps.IndexEntity(frame.Id, e);
            members.Add((e, "ground"));
        }
        if (tile.Entities.Count > 0)
            members.AddRange(_maps.SpawnCopied(frame.Id, tile.Entities, new Vector3(local.X * tiles.TileMetres, -frame.BaseY, local.Z * tiles.TileMetres)));
        tiles.AddPlaced(world, local, members);
        frame.Loaded[local] = members.Select(m => m.Entity).ToList();
        _service.Store.Touch(frame.WorldKeyOf(local));
        return true;
    }
}
