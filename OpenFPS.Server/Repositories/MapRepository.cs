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
    public int? RegionId { get; set; }
    public int? RegionAId { get; set; }
    public int? RegionBId { get; set; }
    public bool? IsIndoor { get; set; }
    public float? ApertureSize { get; set; }
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

    // Atmospheric & Physics Overrides
    public float Gravity { get; set; } = 15.0f;
    public float Temperature { get; set; } = 20.0f;
    public float Humidity { get; set; } = 0.5f;
    public float AirPressure { get; set; } = 1.0f;
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
                var data = JsonSerializer.Deserialize<MapData>(json, options);
                if (data != null && !string.IsNullOrEmpty(data.Id)) 
                {
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
