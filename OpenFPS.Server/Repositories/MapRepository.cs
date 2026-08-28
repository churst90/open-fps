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

    public List<EntityData> Entities { get; set; } = new();
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

    public List<MapData> LoadAll()
    {
        var maps = new List<MapData>();
        var options = new JsonSerializerOptions { 
            Converters = { 
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
