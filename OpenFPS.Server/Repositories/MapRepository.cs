using System.Text.Json;
using System.Text.Json.Serialization;
using System.Numerics;
using OpenFPS.Common.Components;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace OpenFPS.Server.Repositories;

// Generic ECS Component Container
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
    public float? ApertureSize { get; set; }

    /// <summary>Per-face materials for an acoustic REGION entity, by MATERIAL NAME, in the order
    /// Floor, Ceiling, North, South, East, West. Exactly six entries. Overrides whatever the prefab set.</summary>
    public string[]? RoomMaterials { get; set; }

    /// <summary>The same thing as <see cref="RoomMaterials"/> written as raw resonance indices.
    /// Kept for the maps that already use it; prefer the names, which can be checked at load.</summary>
    public int[]? Materials { get; set; }
}

public class MapData
{
    public string Id { get; set; } = string.Empty;
    public Vector3 Size { get; set; }
    public Vector3 MinBound { get; set; } = new Vector3(-50, 0, -50);
    public Vector3 MaxBound { get; set; } = new Vector3(50, 20, 50);
    public Transform SpawnPoint { get; set; } = new();
    public float MinimumY { get; set; } = -10.0f;
    public string Description { get; set; } = string.Empty;
    public float VoxelResolution { get; set; } = 0.5f;
    public float OcclusionFloor { get; set; } = 0.2f;

    // Atmospheric & Physics Overrides.
    // AirPressure is MILLIBARS, not atmospheres: sea level is 1013.25, not 1. The old default of 1.0
    // sailed straight into the client's `AirPressure / 1013.25` normalisation and clamped at the floor,
    // so every map on the server was authored, silently, as near-vacuum. NormalizeAtmosphere now says so.
    public float Gravity { get; set; } = 15.0f;
    public float Temperature { get; set; } = 20.0f;
    public float Humidity { get; set; } = 0.5f;
    public float AirPressure { get; set; } = 1013.25f;
    public float AirAbsorptionMultiplier { get; set; } = 1.0f;

    /// <summary>Outdoor ambience bed for the whole map — an ambisonic recording under ASSETS/SOUNDS,
    /// e.g. "AMBIENCE/woods_mid_day". See MapManifest.AmbienceId.</summary>
    public string AmbienceId { get; set; } = string.Empty;

    public List<EntityData> Entities { get; set; } = new();

    /// <summary>Vehicles that drive the map's roads. See VehicleSystem.</summary>
    public List<VehicleData>? Vehicles { get; set; }

    /// <summary>Closed circuits the map's vehicles can lap. See TrackData.</summary>
    public List<TrackData>? Tracks { get; set; }

    /// <summary>
    /// Composites placed on this map — houses, stalls, barricades, anything built out of parts and
    /// saved. Instantiated at load in the order they appear.
    ///
    /// This list is what makes a building PERMANENT. A composite placed at run time and not recorded
    /// here is a house until the next restart, which is not a house; it is a rehearsal.
    /// </summary>
    public List<CompositePlacement>? Composites { get; set; }

    /// <summary>The map a player lands on when they log in, if no other map claims it. Exactly one
    /// map should set it; if several do, the first loaded wins and the rest are logged.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Whose map it is — a username, or empty for a map that ships with the server. It is
    /// the owner who may edit it, and the owner whose private maps are listed only to them.</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Whether anybody may walk into it. Defaults to true, so that a map authored before
    /// there was such a question does not vanish from the list by having said nothing.</summary>
    public bool IsPublic { get; set; } = true;
}

/// <summary>
/// A closed circuit: the centreline, as a loop of points a car follows round and round.
///
/// It is deliberately just a polyline. An oval, a road course and a figure of eight are the same
/// object to the code that drives it, and the shape lives in the map where it can be seen and
/// changed rather than in a track-generator nobody can read. The points are the CENTRELINE; a
/// vehicle picks its own line by offsetting sideways from it.
/// </summary>
public class TrackData
{
    public string Id { get; set; } = string.Empty;
    /// <summary>Centreline points in order. The loop closes from the last back to the first, so do
    /// not repeat the first point at the end.</summary>
    public List<Vector3> Waypoints { get; set; } = new();
    /// <summary>Surface width, metres. Bounds how far a vehicle may pull off the centreline.</summary>
    public float WidthMetres { get; set; } = 15f;
    /// <summary>
    /// How steeply the turns are banked, degrees. Zero is a flat track.
    ///
    /// The racing line needs this and cannot work it out: the waypoints give the centreline's
    /// elevation, and the bank is the CROSS-slope, which a single line of points does not describe.
    /// Leaving it at zero on a track whose geometry is banked makes the cars lift for corners they
    /// could take flat — on the speedway that was three to four semitones of rev drop, twice a lap,
    /// for every car.
    /// </summary>
    public float BankingDegrees { get; set; } = 0f;
}

/// <summary>A vehicle on a map: which car, which road, how fast on each pass.</summary>
public class VehicleData
{
    public string? Name { get; set; }
    /// <summary>A VehicleProfile preset key: v8_muscle, i4_economy, diesel_truck, ...</summary>
    public string Preset { get; set; } = "v8_muscle";
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
    /// <summary>Lateral grip in g. This is what decides corner speed — v = sqrt(g * 9.81 * R) at the
    /// local radius — and therefore how much a car has to lift and how hard it gets back on the
    /// throttle, which is the whole sound of a lap. A road car on a flat bend is 0.9; a stock car on
    /// a banked oval is nearer 2.8 because the banking carries part of the load; a formula car with
    /// wings is 4 and up.</summary>
    public float CorneringG { get; set; }
    /// <summary>Where on the lap this car starts, metres along from the first waypoint. Spreading a
    /// field out is the difference between a race and a convoy.</summary>
    public float StartOffsetMetres { get; set; }
    /// <summary>The line this car takes, metres to the RIGHT of the centreline (negative is left,
    /// which on an anticlockwise oval is the inside). Clamped to the track width.</summary>
    public float LaneOffsetMetres { get; set; }
}
public class MapRepository
{
    private readonly string _directory;

    public MapRepository(string directory)
    {
        // Path Discovery: Check local, then check OpenFPS.Server/
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

    /// <summary>
    /// How a map file is read. One definition, so anything that loads a map — the server at startup,
    /// a test, a tool — agrees about trailing commas, comments and how a Vector3 is spelled. Maps are
    /// hand-edited, and a loader that silently disagrees with the one the server uses is a map that
    /// passes its test and fails in the game.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), // Support both CamelCase and exact matches
                new OpenFPS.Common.Networking.Vector3Converter(),
                new OpenFPS.Common.Networking.QuaternionConverter()
            },
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        // Add a case-insensitive string-to-enum fallback
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

        foreach (var file in Directory.GetFiles(_directory, "*.json"))
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

        // Only create a default map if NO maps exist in the directory at all
        if (maps.Count == 0 && Directory.GetFiles(_directory, "*.json").Length == 0)
        {
            Log.Information("MapRepository: No maps found. Generating fresh default map.");
            var defaultMap = new MapData { Id = "default", Size = new Vector3(100, 10, 100), Description = "The default starting zone." };
            Save(defaultMap);
            maps.Add(defaultMap);
        }

        return maps;
    }

    /// <summary>
    /// Checks the map's authored atmosphere against the units the engine actually reads it in, and
    /// says so out loud when it does not match.
    ///
    /// This is the same rule as everywhere else in the loader: a value the engine cannot honour is
    /// named, not silently absorbed. A map is not rejected for it (that would delete a playable world
    /// over a number), but the substitution is reported so the number can be fixed at the source.
    /// </summary>
    public static void NormalizeAtmosphere(MapData data, string fileName)
    {
        // Below 300 mb is lower than the summit of Everest (~337 mb) — no map is up there, so a value
        // this small is an author writing atmospheres (1.0) where the engine reads millibars.
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

    /// <summary>
    /// Names every key the map file carries that the loader does not understand.
    ///
    /// System.Text.Json drops an unrecognised property without a word, so a mistyped field — `Aperture`
    /// for `ApertureSize`, `Materials` on an entity that is not a region — is a setting that never applies
    /// and never complains, and the only symptom is that the map sounds wrong. Unlike a prefab, a map
    /// entity is NOT rejected for it: dropping it would delete a wall. It is reported and loaded.
    /// </summary>
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

    public string GetMapChecksum(string mapId)
    {
        string filePath = Path.Combine(_directory, $"{mapId}.json");
        if (!File.Exists(filePath)) return "";
        
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Writes a map back to its own file, exactly as it now stands.
    ///
    /// Load-bearing since composites: a building placed at run time is appended to the map's own data
    /// the moment it is placed, and this is what commits that to disk. Without it a house lasts until
    /// the next restart, which is not a house, it is a rehearsal.
    /// </summary>
    public void Save(MapData map)
    {
        string filePath = Path.Combine(_directory, $"{map.Id}.json");
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

    public void Delete(string id)
    {
        string filePath = Path.Combine(_directory, $"{id}.json");
        if (File.Exists(filePath)) File.Delete(filePath);
    }
}
