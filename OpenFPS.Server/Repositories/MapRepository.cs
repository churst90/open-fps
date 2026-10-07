using System.Text.Json;
using System.Text.Json.Serialization;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using System.Security.Cryptography;
using Serilog;

namespace OpenFPS.Server.Repositories;

/// <summary>One entity in a map file: a prefab placed, with whatever the map sets over it.</summary>
public class EntityData
{
    public int EntityId { get; set; }
    public string PrefabId { get; set; } = string.Empty;
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 Scale { get; set; } = Vector3.One;
    public int? RegionAId { get; set; }
    public int? RegionBId { get; set; }
    public bool? IsIndoor { get; set; }

    /// <summary>What to call this thing. On a region, the name a player hears as they walk into it.</summary>
    public string? Name { get; set; }
    public float? ApertureSize { get; set; }

    /// <summary>On a door, over its prefab: which side is locked, +1 the leaf's own +Z face, -1 the
    /// other, 0 neither. A fire exit may be keyed outside on one map and not on another.</summary>
    public float? KeyedSide { get; set; }

    /// <summary>On a door, over its prefab: which face it is pushed open from, +1 or -1 (DoorComponent.PushSide).</summary>
    public float? PushSide { get; set; }

    /// <summary>Per-face materials for an acoustic REGION entity, by MATERIAL NAME, in the order
    /// Floor, Ceiling, North, South, East, West. Exactly six entries. Overrides whatever the prefab set.</summary>
    public string[]? RoomMaterials { get; set; }

    /// <summary><see cref="RoomMaterials"/> as raw resonance indices, for the maps that use it; prefer
    /// the names, which are checked at load.</summary>
    public int[]? Materials { get; set; }

    /// <summary>Which tile of a tiled map this stands in ("3,-2": TileMetres squares from the origin,
    /// x then z), written by tools/gen_osm.py. The streamer works tiles out from geometry instead
    /// (MapTiles).</summary>
    public string? Tile { get; set; }

    /// <summary>What a generated map's entity is part of ("roads", "zones", "structure", "rooms",
    /// "interiors", "trees", ...). MapTiles reads it for a tile's coarse level.</summary>
    public string? Layer { get; set; }

    /// <summary>Over its prefab: a solid form from the shape library filling the collider's box
    /// (PrefabTemplate.Form): a ramp, a flight of stairs, an arch.</summary>
    public OpenFPS.Common.Geometry.ShapeSpec? Form { get; set; }
}

/// <summary>Where a map made from a real place sits on the Earth: its (0, 0) in degrees, WGS84.</summary>
public class GeoPoint
{
    public double Lat { get; set; }
    public double Lon { get; set; }
}

public class MapData
{
    public string Id { get; set; } = string.Empty;

    /// <summary>What the map is called in the maps list, and what /join also answers to ("magnolia tx").
    /// Empty: the id. The id stays what the server and saved players know it by.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The name to say: <see cref="Name"/>, or the id when there is none.</summary>
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>On a map of a real place, where its origin is (x east and z north are metres from it).</summary>
    public GeoPoint? GeoOrigin { get; set; }

    /// <summary>The size of the tiles a generated map's entities are tagged with (EntityData.Tile),
    /// metres; 0 for a map that is not tiled.</summary>
    public float TileMetres { get; set; }
    public Vector3 Size { get; set; }
    public Vector3 MinBound { get; set; } = new Vector3(-50, 0, -50);
    public Vector3 MaxBound { get; set; } = new Vector3(50, 20, 50);
    /// <summary>
    /// Where a player, and anything a player drives, can go; omitted, the bounds. MinBound and MaxBound
    /// are the acoustic grid's and can be far bigger than the ground (the city's reach a kilometre out
    /// for the airliners); held to those, you could walk off the edge of the ground.
    /// </summary>
    public Vector3? PlayMin { get; set; }
    public Vector3? PlayMax { get; set; }
    [JsonIgnore] public Vector3 WalkMin => PlayMin ?? MinBound;
    [JsonIgnore] public Vector3 WalkMax => PlayMax ?? MaxBound;
    public Transform SpawnPoint { get; set; } = new();
    public float MinimumY { get; set; } = -10.0f;
    public string Description { get; set; } = string.Empty;
    public float VoxelResolution { get; set; } = 0.5f;
    public float OcclusionFloor { get; set; } = 0.2f;

    // AirPressure is millibars, not atmospheres: a 1.0 made every map a near-vacuum to the client's air
    // absorption. NormalizeAtmosphere catches it.
    public float Gravity { get; set; } = PhysicsConstants.Gravity;
    public float Temperature { get; set; } = 20.0f;
    public float Humidity { get; set; } = 0.5f;
    public float AirPressure { get; set; } = 1013.25f;
    public float AirAbsorptionMultiplier { get; set; } = 1.0f;

    /// <summary>Outdoor ambience bed for the whole map — an ambisonic recording under ASSETS/SOUNDS,
    /// e.g. "AMBIENCE/woods_mid_day". See MapManifest.AmbienceId.</summary>
    public string AmbienceId { get; set; } = string.Empty;

    /// <summary>
    /// Which beacon categories this map allows, category to policy: "default_on", "default_off",
    /// "forced_on" or "forbidden". Anything not listed is default_on. See OpenFPS.Common.Beacons.
    /// </summary>
    public Dictionary<string, string>? BeaconPolicy { get; set; }

    /// <summary>A weather this map always has ("clear", "rain", "snow", "storm"), or null to follow the
    /// server's sky. Set in the world editor (map settings) or by the map's author.</summary>
    public string? HeldWeather { get; set; }

    /// <summary>An hour of the day this map always has, 0 to 24, or null to follow the server's clock.</summary>
    public float? HeldHour { get; set; }

    /// <summary>The prefab laid as natural ground where the map has none of its own; null is dirt
    /// (MapManager.NaturalGroundPrefab).</summary>
    public string? GroundPrefab { get; set; }

    public List<EntityData> Entities { get; set; } = new();

    /// <summary>Vehicles that drive the map's roads. See VehicleSystem.</summary>
    public List<VehicleData>? Vehicles { get; set; }
    /// <summary>Trains that run the map's rail tracks. See RailSystem.</summary>
    public List<TrainData>? Trains { get; set; }

    /// <summary>Closed circuits the map's vehicles can lap. See TrackData.</summary>
    public List<TrackData>? Tracks { get; set; }

    /// <summary>What the people driving this map's traffic do besides drive — honk, stand on the
    /// brakes, park. Null on a map whose traffic is racing, which is every map but a street.</summary>
    public StreetLifeData? StreetLife { get; set; }

    /// <summary>Where roads cross the railway on the level. See LevelCrossingData.</summary>
    public List<LevelCrossingData>? Crossings { get; set; }

    /// <summary>The map's roads: centrelines, lanes and surfaces. See OpenFPS.Common.Roads.</summary>
    public List<RoadData>? Roads { get; set; }

    /// <summary>Where the roads meet. The lanes through each are worked out from the roads.</summary>
    public List<JunctionData>? Junctions { get; set; }

    /// <summary>Places on the roads where vehicles stop. See RoadStopData.</summary>
    public List<RoadStopData>? RoadStops { get; set; }

    /// <summary>People with names and lives of their own on this map, one of each (CharacterSystem).</summary>
    public List<CharacterData>? Characters { get; set; }

    /// <summary>Composites placed on this map, made at load in this order. A composite placed at run
    /// time and not recorded here is gone at the next restart.</summary>
    public List<CompositePlacement>? Composites { get; set; }

    /// <summary>The map a player lands on when they log in, if no other map claims it. Exactly one
    /// map should set it; if several do, the first loaded wins and the rest are logged.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Whose map it is — a username, or empty for a map that ships with the server. It is
    /// the owner who may edit it, and the owner whose private maps are listed only to them.</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Whether anybody may walk into it. Defaults to true, so a map that says nothing stays listed.</summary>
    public bool IsPublic { get; set; } = true;

    /// <summary>Who the owner has let into a private map (/map invite), by username. Kept, with the
    /// owner and the public flag, in map_access.json (MapAccessRepository) rather than in the map file,
    /// so changing who may come in never rewrites the map.</summary>
    [JsonIgnore] public List<string> Invited { get; set; } = new();

    /// <summary>Who the owner has asked to edit the map with the world editor (/map editor add), by
    /// username. Kept in map_access.json beside the invitations, never in the map file.</summary>
    [JsonIgnore] public List<string> Editors { get; set; } = new();
}

/// <summary>
/// How often, across the whole map, the drivers do the things drivers do: each a random event with this
/// mean interval, from a vehicle chosen at random, not a timetable. Zero turns one off. Also the
/// following, giving-way and crossing figures traffic drives by.
/// </summary>
public class StreetLifeData
{
    /// <summary>Somebody somewhere on the map sounds their horn, on average this often, seconds.</summary>
    public float HornEverySeconds { get; set; }
    /// <summary>Somebody stands on the brakes, on average this often. The tyres squeal because the
    /// braking is past their grip, not because a squeal was asked for.</summary>
    public float HardBrakeEverySeconds { get; set; }
    /// <summary>A car pulls in to the kerb near a door, the driver gets out and goes inside, and
    /// later comes back and drives off — on average this often across the map.</summary>
    public float ParkEverySeconds { get; set; }
    /// <summary>Somebody on foot fires a few rounds, on average this often across the map.</summary>
    public float GunfireEverySeconds { get; set; }
    /// <summary>A car standing empty at the kerb has its alarm go off, on average this often.</summary>
    public float AlarmEverySeconds { get; set; }

    /// <summary>
    /// How far behind the vehicle ahead a driver keeps, in seconds of their own speed: the IDM's
    /// time headway T (Treiber, Hennecke and Helbing 2000). Treiber and Kesting's city value is
    /// 1.0-1.5 s; the two-second rule is what drivers are taught and few keep.
    /// </summary>
    public float FollowHeadwaySeconds { get; set; } = 1.5f;
    /// <summary>The gap left to a stopped vehicle ahead, metres: the IDM's s0. Two metres in the
    /// same source.</summary>
    public float FollowMinGapMetres { get; set; } = 2f;

    /// <summary>
    /// The smallest gap in priority traffic a driver giving way will take, seconds: the Highway
    /// Capacity Manual's base critical headways for two-way stop control (HCM 2010, exhibit 19-10).
    /// A right turn from the minor road 6.2 s, straight across 6.5, a left turn across both streams
    /// 7.1; a left turn off the major road across oncoming traffic 4.1.
    /// </summary>
    public float CriticalGapRightSeconds { get; set; } = 6.2f;
    public float CriticalGapStraightSeconds { get; set; } = 6.5f;
    public float CriticalGapLeftSeconds { get; set; } = 7.1f;
    public float CriticalGapMajorLeftSeconds { get; set; } = 4.1f;
    /// <summary>How fast a driver giving way arrives at the line to look, km/h. A clear junction is
    /// taken at this without stopping.</summary>
    public float GiveWayApproachKmh { get; set; } = 15f;
    /// <summary>How long drivers at a junction where everyone is giving way to someone wait before one
    /// of them goes anyway, seconds.</summary>
    public float GiveWayPatienceSeconds { get; set; } = 6f;
    /// <summary>
    /// A pedestrian crossing a road where nothing controls it takes a gap in the traffic of at least
    /// the time to walk across plus this, seconds: the Highway Capacity Manual's pedestrian critical
    /// headway t_c = L / S_p + t_s, where t_s is the start-up and end clearance time (HCM 2010,
    /// chapter 19; 3 s, *to confirm* against the text).
    /// </summary>
    public float PedestrianStartUpSeconds { get; set; } = 3f;
    /// <summary>How long somebody stands at the kerb before drivers who can stop comfortably stop for
    /// them, seconds. Drivers always stop for somebody already on the crossing.</summary>
    public float PedestrianAssertSeconds { get; set; } = 8f;

    // In the rain. "Wet" is the road's texture full of water, "heavy" rain at Rainfall.HeavyRate
    // (RoadWaterSystem). Rakha et al., FHWA-HOP-07-073 (2007), Table ES.2: free-flow speed 2-3.6 % lower
    // in light rain and 6-9 % at about 16 mm/h; capacity 10-11 % lower in either with jam density
    // unchanged, a time headway about 12 % longer. Zero turns either off.

    /// <summary>The share of their speed drivers give up on a wet road in light rain.</summary>
    public float WetSpeedReduction { get; set; } = 0.03f;
    /// <summary>...and in heavy rain.</summary>
    public float HeavyRainSpeedReduction { get; set; } = 0.08f;
    /// <summary>How much longer a time headway drivers keep on a wet road, as a share.</summary>
    public float WetHeadwayIncrease { get; set; } = 0.12f;
    /// <summary>...and in heavy rain (the same: the capacity loss did not grow with the rain).</summary>
    public float HeavyRainHeadwayIncrease { get; set; } = 0.12f;
    /// <summary>After this long at the kerb a pedestrian takes a gap only just long enough to walk across,
    /// without the start-up margin, seconds. The HCM puts pedestrians' likelihood of taking risks as high
    /// above 30 s of delay at an uncontrolled crossing (HCM 2010, chapter 19, *to confirm*).</summary>
    public float PedestrianRiskSeconds { get; set; } = 30f;
}

/// <summary>A closed circuit: its centreline as a loop of points. A vehicle picks its own line by
/// offsetting sideways from it.</summary>
public class TrackData
{
    public string Id { get; set; } = string.Empty;
    /// <summary>Centreline points in order. The loop closes from the last back to the first, so do
    /// not repeat the first point at the end.</summary>
    public List<Vector3> Waypoints { get; set; } = new();
    /// <summary>Surface width, metres. Bounds how far a vehicle may pull off the centreline.</summary>
    public float WidthMetres { get; set; } = 15f;
    /// <summary>
    /// How steeply the turns are banked, degrees; zero is flat. The racing line cannot work out the
    /// cross-slope from a line of points. Left at zero on a banked track, the cars lift for corners they
    /// could take flat: three to four semitones of rev drop twice a lap on the speedway.
    /// </summary>
    public float BankingDegrees { get; set; } = 0f;

    /// <summary>Places on this route where a vehicle stops: bus stops, platforms, give-way lines,
    /// crossings. A bus's air brakes and doors fire only once it has been still a couple of seconds.</summary>
    public List<TrackStopData> Stops { get; set; } = new();
}

/// <summary>
/// A place where a road crosses the railway on the level, declared as a point only. Which line and
/// roads run through it, and where along them, the server works out from the geometry, so a declared
/// offset cannot drift from the track and ring for nothing.
/// </summary>
public class LevelCrossingData
{
    public string? Name { get; set; }
    /// <summary>Where the rails meet the road.</summary>
    public Vector3 Position { get; set; }
    /// <summary>How long before a train arrives the bells ring, seconds (twenty in most places). The
    /// approach distance follows from this and the line's speed limit unless <see cref="WarningMetres"/>
    /// overrides it.</summary>
    public float WarningSeconds { get; set; } = 20f;
    /// <summary>Overrides the derived distance, metres. Zero means work it out from the time.</summary>
    public float WarningMetres { get; set; }
    /// <summary>How far past the crossing the last vehicle must be before the road reopens.</summary>
    public float ClearMetres { get; set; } = 30f;
    /// <summary>Which bell hangs on it — a <c>StruckBellSpec</c> preset.</summary>
    public string Bell { get; set; } = "crossing_gong";
}

/// <summary>Somewhere on a route that a vehicle stops: how far round, and for how long.</summary>
public class TrackStopData
{
    /// <summary>Distance round the lap, metres.</summary>
    public float AtMetres { get; set; }
    /// <summary>How long it stands there. A bus stop is fifteen to thirty seconds; a platform is
    /// longer; a junction is a few.</summary>
    public float DwellSeconds { get; set; } = 18f;
    /// <summary>
    /// What kind of stop it is, which decides how long it waits and whether a bus kneels and opens its
    /// doors; the sound is whatever the vehicle's parts make when it halts.
    ///
    ///   "bus_stop" / "platform"  a fixed dwell, for passengers
    ///   "give_way"               a fixed, short dwell at a junction
    ///   "crossing"               held only while the crossing is closed, else driven through
    /// </summary>
    public string Kind { get; set; } = "stop";
    /// <summary>Only vehicles whose preset contains this stop here. Empty means everything does: right for
    /// a junction, wrong for a bus stop.</summary>
    public string? ForPreset { get; set; }
}

/// <summary>
/// A train on a map: which consist (a <c>TrainProfile</c> preset), which track, how fast. The
/// server spawns one entity per sound source of the consist and moves them all along the track
/// together; see RailSystem and TrainLayout.
/// </summary>
public class TrainData
{
    public string? Name { get; set; }
    public string Preset { get; set; } = "light_rail";
    public string Track { get; set; } = "";
    public float TopSpeedKmh { get; set; } = 45f;
    public float StartOffsetMetres { get; set; }
    public float AccelerationMps2 { get; set; } = 0.9f;
    public float BrakingMps2 { get; set; } = 1.0f;
}

/// <summary>
/// Somebody who lives on a map rather than walking a line of it: a name, a voice, and what kind of
/// life they lead, which decides where they go (CharacterSystem). Where those places are is found
/// from the map itself: its bus stops, its buildings' front entrances, its squares.
/// </summary>
public class CharacterData
{
    /// <summary>What people call them, and what the world says: "Alex".</summary>
    public string Name { get; set; } = "";
    /// <summary>The voice they speak in (Speech/voices.csv).</summary>
    public string Voice { get; set; } = "";
    /// <summary>The life they lead. "homeless": no home and no money, the street by day and a
    /// lobby or a shelter by night.</summary>
    public string Kind { get; set; } = "homeless";
    /// <summary>How they are described when looked at: "a homeless man".</summary>
    public string Description { get; set; } = "";
}

/// <summary>A vehicle on a map: which car, which road, how fast on each pass.</summary>
public class VehicleData
{
    public string? Name { get; set; }
    /// <summary>A VehicleProfile preset key: v8_muscle, i4_economy, diesel_truck, ...</summary>
    public string Preset { get; set; } = "v8_muscle";
    /// <summary>Two walkers with the same Pair walk together and talk to each other ("" for none).</summary>
    public string? Pair { get; set; }
    public Vector3 RoadStart { get; set; }
    public Vector3 RoadEnd { get; set; }
    /// <summary>Speed of each pass in turn, km/h; wraps round. Shuttle mode only.</summary>
    public float[]? SpeedsKmh { get; set; }
    public float AccelerationMps2 { get; set; }
    public float BrakingMps2 { get; set; }
    /// <summary>How long it idles at each end before setting off. Shuttle mode only.</summary>
    public float WaitSeconds { get; set; }
    public float StartDelaySeconds { get; set; }

    // ── Racing: set Track and the vehicle laps that circuit instead of shuttling a road ──────────

    /// <summary>Id of a <see cref="TrackData"/> on this map. When set, RoadStart/RoadEnd are ignored
    /// and the vehicle laps the circuit continuously.</summary>
    public string? Track { get; set; }
    /// <summary>What this car will do on the straight, km/h. Its own limit, not the track's.</summary>
    public float TopSpeedKmh { get; set; }
    /// <summary>How hard it corners, g: the corner speed is sqrt(g * 9.81 * R), which is the sound of a
    /// lap. A road car on a flat bend 0.9, a stock car on a banked oval nearer 2.8, a winged formula car
    /// 4 and up.</summary>
    public float CorneringG { get; set; }
    /// <summary>
    /// How much grip the tyres have, g, as distinct from how hard it chooses to corner
    /// (<see cref="CorneringG"/>); zero means the same, which is a racing line. Tyres are measured
    /// against this: with the two equal every corner is at the limit and squeals, which is right for a
    /// race car and wrong for traffic, which corners at about a third of its grip. Every vehicle on the
    /// city screeched until they were separated.
    /// </summary>
    public float GripG { get; set; }

    /// <summary>Where on the lap this car starts, metres along from the first waypoint.</summary>
    public float StartOffsetMetres { get; set; }
    /// <summary>The line this car takes, metres to the RIGHT of the centreline (negative is left,
    /// which on an anticlockwise oval is the inside). Clamped to the track width.</summary>
    public float LaneOffsetMetres { get; set; }

    /// <summary>
    /// A way round the map's roads instead of a Track: the vehicle drives the lanes, turning through
    /// junctions, round a tour built from the road network at load. See RouteData.
    /// </summary>
    public RouteData? Route { get; set; }
}

/// <summary>
/// Where a vehicle drives on a map with roads. Either a fixed tour (a bus route): the junctions in
/// order, by id, and back to the first. Or a wander: from a road, turning at random at each junction,
/// seeded so the map is the same every time it loads.
/// </summary>
public class RouteData
{
    /// <summary>Junction ids in order. Empty for a wander.</summary>
    public List<string> Via { get; set; } = new();
    /// <summary>A wander starts on this road (its id), going the way its centreline runs if
    /// <see cref="Direction"/> is +1.</summary>
    public string? StartRoad { get; set; }
    public int Direction { get; set; } = 1;
    public int Seed { get; set; }
    /// <summary>How far a wander goes before it heads back to where it began, metres.</summary>
    public float WanderMetres { get; set; } = 900f;
}

/// <summary>
/// A place on a road where vehicles stop: a bus stop, a stop line. Declared as a point; every vehicle
/// whose way passes within a lane's width of it, and whose preset matches, stops there.
/// </summary>
public class RoadStopData
{
    public string? Name { get; set; }
    public Vector3 Position { get; set; }
    /// <summary>As TrackStopData.Kind: "bus_stop", "give_way", "stop".</summary>
    public string Kind { get; set; } = "bus_stop";
    public float DwellSeconds { get; set; } = 16f;
    public string? ForPreset { get; set; }
}
public class MapRepository
{
    private readonly string _directory;

    public MapRepository(string directory)
    {
        // Found from the repo root or from the server's own folder.
        if (!Directory.Exists(directory) && Directory.Exists(Path.Combine("OpenFPS.Server", directory)))
        {
            _directory = Path.GetFullPath(Path.Combine("OpenFPS.Server", directory));
        }
        else
        {
            _directory = Path.GetFullPath(directory);
        }

        if (!Directory.Exists(_directory)) Directory.CreateDirectory(_directory);
        Log.Information("MapRepository: Initialized with directory {Path}", _directory);
    }

    /// <summary>How a map file is read: one definition, so the server, the tests and the tools agree
    /// about trailing commas, comments and how a Vector3 is spelled.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
                new OpenFPS.Common.Networking.Vector3Converter(),
                new OpenFPS.Common.Networking.QuaternionConverter()
            },
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        // Exact enum names too, not only camelCase.
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Reads one map file. Returns null if it does not parse or has no Id.</summary>
    public static MapData? LoadFromFile(string path)
    {
        var data = JsonSerializer.Deserialize<MapData>(File.ReadAllText(path), JsonOptions);
        return data != null && !string.IsNullOrEmpty(data.Id) ? data : null;
    }

    public List<MapData> LoadAll()
    {
        var maps = new List<MapData>();
        var options = JsonOptions;

        // Shipped maps, then real places (places/, so the tests that load every shipped map do not load
        // a town each), then players' maps (players/, so a checkout never carries anybody's map).
        var files = Directory.GetFiles(_directory, "*.json").ToList();
        if (Directory.Exists(PlacesDirectory)) files.AddRange(Directory.GetFiles(PlacesDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal));
        if (Directory.Exists(PlayerDirectory)) files.AddRange(Directory.GetFiles(PlayerDirectory, "*.json"));
        foreach (var file in files)
        {
            try
            {
                string json = File.ReadAllText(file);
                ReportUnknownFields(json, Path.GetFileName(file));
                var data = JsonSerializer.Deserialize<MapData>(json, options);
                if (data != null && !string.IsNullOrEmpty(data.Id)) 
                {
                    NormalizeAtmosphere(data, Path.GetFileName(file));
                    maps.Add(data);
                    Log.Information("MapRepository: Successfully loaded map '{Id}' from {File}.", data.Id, Path.GetFileName(file));
                }
                else
                {
                    Log.Warning("MapRepository: Skipping {File} - Missing Id property or invalid format.", Path.GetFileName(file));
                }
            }
            catch (Exception ex)
            {
                Log.Error("MapRepository: CRITICAL FAILURE loading map from {File}. This map will be ignored to prevent data loss. Error: {Error}", file, ex.Message);
            }
        }

        if (maps.Count == 0 && Directory.GetFiles(_directory, "*.json").Length == 0)
        {
            Log.Information("MapRepository: No maps found. Generating fresh default map.");
            var defaultMap = new MapData { Id = "default", Size = new Vector3(100, 10, 100), Description = "The default starting zone." };
            Save(defaultMap);
            maps.Add(defaultMap);
        }

        return maps;
    }

    /// <summary>Checks the authored atmosphere against the units the engine reads it in; a value out of
    /// range is replaced and the replacement logged, not the map rejected.</summary>
    public static void NormalizeAtmosphere(MapData data, string fileName)
    {
        // Below the summit of Everest (~337 mb): an author writing atmospheres (1.0) for millibars.
        const float MinPlausibleMb = 300.0f;
        const float MaxPlausibleMb = 1100.0f;
        const float SeaLevelMb = 1013.25f;

        if (data.AirPressure < MinPlausibleMb || data.AirPressure > MaxPlausibleMb)
        {
            Log.Warning("MapRepository: map '{Id}' in {File} authors AirPressure {Value} — that is not millibars " +
                        "(sea level is {SeaLevel}, and the plausible range is {Min}-{Max}). Using {SeaLevel}. " +
                        "Air absorption would otherwise be computed for a near-vacuum.",
                data.Id, fileName, data.AirPressure, SeaLevelMb, MinPlausibleMb, MaxPlausibleMb);
            data.AirPressure = SeaLevelMb;
        }

        if (data.AirAbsorptionMultiplier <= 0f)
        {
            Log.Warning("MapRepository: map '{Id}' in {File} authors AirAbsorptionMultiplier {Value}; it scales a " +
                        "distance and must be positive. Using 1.0 (no scaling).",
                data.Id, fileName, data.AirAbsorptionMultiplier);
            data.AirAbsorptionMultiplier = 1.0f;
        }

        if (data.Humidity < 0f || data.Humidity > 1f)
        {
            float clamped = Math.Clamp(data.Humidity, 0f, 1f);
            Log.Warning("MapRepository: map '{Id}' in {File} authors Humidity {Value}; the range is 0 to 1. Using {Clamped}.",
                data.Id, fileName, data.Humidity, clamped);
            data.Humidity = clamped;
        }
    }

    /// <summary>Logs every key the map file carries that the loader does not know: System.Text.Json drops
    /// them without a word. Unlike a prefab, the entity still loads; dropping it would delete a wall.</summary>
    private static void ReportUnknownFields(string json, string fileName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!MapFields.Contains(property.Name))
                    Log.Error("MapRepository: {File} has unknown map field '{Field}', which the loader ignores. Known: {Known}.",
                        fileName, property.Name, string.Join(", ", MapFields));
            }

            if (!doc.RootElement.TryGetProperty("Entities", out var entities) || entities.ValueKind != JsonValueKind.Array) return;

            int index = 0;
            foreach (var entity in entities.EnumerateArray())
            {
                string label = entity.TryGetProperty("EntityId", out var idElement) ? idElement.ToString() : $"#{index}";
                if (entity.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in entity.EnumerateObject())
                    {
                        if (!EntityFields.Contains(property.Name))
                            Log.Error("MapRepository: {File} entity {Entity} has unknown field '{Field}', which the loader ignores. Known: {Known}.",
                                fileName, label, property.Name, string.Join(", ", EntityFields));
                    }
                }
                index++;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("MapRepository: could not scan {File} for unknown fields. {Error}", fileName, ex.Message);
        }
    }

    private static readonly HashSet<string> MapFields = new(
        typeof(MapData).GetProperties().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> EntityFields = new(
        typeof(EntityData).GetProperties().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the world editor keeps each map's edits (docs/WORLD_EDITOR.md section 7). Not read as maps.</summary>
    public string OverlayDirectory => Path.Combine(_directory, "overlays");

    /// <summary>Where the maps players make are kept: a folder of their own beside the shipped maps.</summary>
    public string PlayerDirectory => Path.Combine(_directory, "players");

    /// <summary>Where the maps of real places are kept (tools/gen_osm.py writes them there).</summary>
    public string PlacesDirectory => Path.Combine(_directory, "places");

    /// <summary>The file a map is in, or would be written to: a player's map in <see cref="PlayerDirectory"/>
    /// (one already there, or a new one with an owner), every other map beside the shipped ones.</summary>
    public string PathFor(MapData map)
    {
        string shipped = Path.Combine(_directory, $"{map.Id}.json");
        string players = Path.Combine(PlayerDirectory, $"{map.Id}.json");
        string place = Path.Combine(PlacesDirectory, $"{map.Id}.json");
        if (File.Exists(players)) return players;
        if (File.Exists(place)) return place;
        if (!File.Exists(shipped) && !string.IsNullOrWhiteSpace(map.OwnerId)) return players;
        return shipped;
    }

    /// <summary>Whether a map of this id has a file, shipped or a player's.</summary>
    public bool Exists(string mapId)
        => File.Exists(Path.Combine(_directory, $"{mapId}.json")) || File.Exists(Path.Combine(PlayerDirectory, $"{mapId}.json"))
           || File.Exists(Path.Combine(PlacesDirectory, $"{mapId}.json"));

    public string GetMapChecksum(string mapId)
    {
        string filePath = Path.Combine(_directory, $"{mapId}.json");
        if (!File.Exists(filePath)) filePath = Path.Combine(PlacesDirectory, $"{mapId}.json");
        if (!File.Exists(filePath)) filePath = Path.Combine(PlayerDirectory, $"{mapId}.json");
        if (!File.Exists(filePath)) return "";
        
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>Writes a map back to its own file as it now stands, composites placed at run time
    /// included.</summary>
    public void Save(MapData map)
    {
        string filePath = PathFor(map);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string json = JsonSerializer.Serialize(map, new JsonSerializerOptions { 
            WriteIndented = true, 
            Converters = { 
                new JsonStringEnumConverter(),
                new OpenFPS.Common.Networking.Vector3Converter(),
                new OpenFPS.Common.Networking.QuaternionConverter()
            }
        });
        File.WriteAllText(filePath, json);
    }
}
