using System.Text.Json;
using System.Numerics;
using OpenFPS.Common.Components;
using Arch.Core;

namespace OpenFPS.Server.Repositories;

public class PrefabTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Material { get; set; } = "None";
    public Vector3? ColliderSize { get; set; }
    public EntityType Type { get; set; } = EntityType.StaticObject;
    public int? MaxHealth { get; set; }
    public bool IsItem { get; set; }
    public bool? IsSolid { get; set; }
    public float? ItemWeight { get; set; }
    
    public float? TransmissionLow { get; set; }
    public float? TransmissionMid { get; set; }
    public float? TransmissionHigh { get; set; }
    public float? Absorption { get; set; }
    public float? Scattering { get; set; }
    public float? ShellThickness { get; set; }
    public int? FaceMask { get; set; }
    public List<string>? MissingFaces { get; set; }

    public float? Mass { get; set; }
    public float? Friction { get; set; }
    public float? Restitution { get; set; }
    public float? Drag { get; set; }

    // Sound Emitter Extension
    public bool HasEmitter { get; set; }
    public string? SoundId { get; set; }
    public float? Volume { get; set; }
    public float? Range { get; set; }
    public PlaybackMode? Mode { get; set; }
    public float? ConeInsideAngle { get; set; }
    public float? ConeOutsideAngle { get; set; }
    public float? ConeOutsideVolume { get; set; }
    public float? MinDistance { get; set; }
    
    // Granular Synthesis Extension
    public bool? IsGranular { get; set; }
    public float? GranularPosition { get; set; }
    public float? GranularGrainSize { get; set; }
    public float? GranularDensity { get; set; }
    public float? GranularPitch { get; set; }
    public float? GranularPosJitter { get; set; }
    public float? GranularPitchJitter { get; set; }

    public bool? IsSynth { get; set; }
    public int? SynthWave { get; set; }
    public float? SynthFreq { get; set; }
    public float? SynthLfoRate { get; set; }
    public float? SynthLfoDepth { get; set; }
    public float? SynthFilterCutoff { get; set; }
    public float? SynthFilterResonance { get; set; }
    public float? SynthPulseWidth { get; set; }

    // Acoustic Region/Portal
    public bool? IsIndoor { get; set; }
    public AcousticEnvironmentType? EnvType { get; set; }
    public Vector3? RoomSize { get; set; }
    public string? AmbienceId { get; set; }
    public float? ReverbScale { get; set; }
    
    public int? RegionAId { get; set; }
    public int? RegionBId { get; set; }
    public float? ApertureSize { get; set; }
}

public class PrefabRepository
{
    private readonly string _directory;
    private Dictionary<string, PrefabTemplate> _prefabs = new();

    public PrefabRepository(string directory)
    {
        _directory = directory;
        if (!Directory.Exists(_directory)) Directory.CreateDirectory(_directory);
        LoadAll();
        CreateDefaults();
    }

    public void LoadAll()
    {
        _prefabs.Clear();
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { 
                new System.Text.Json.Serialization.JsonStringEnumConverter(),
                new OpenFPS.Common.Networking.Vector3Converter(),
                new OpenFPS.Common.Networking.QuaternionConverter()
            }
        };

        foreach (var file in Directory.GetFiles(_directory, "*.json"))
        {
            if (file.EndsWith("-schema.json", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                string json = File.ReadAllText(file);
                var template = JsonSerializer.Deserialize<PrefabTemplate>(json, options);
                if (template != null && !string.IsNullOrEmpty(template.Id)) 
                {
                    _prefabs[template.Id.ToLower()] = template;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PrefabRepository] Skipping {Path.GetFileName(file)}: Not a valid prefab template. ({ex.Message})");
            }
        }
    }

    private void CreateDefaults()
    {
        // Handled entirely by external JSON files in LoadAll().
        // Legacy hardcoded defaults have been moved to /prefabs/ directory.
    }

    private void EnsureDefault(PrefabTemplate prefab)
    {
        if (!_prefabs.ContainsKey(prefab.Id.ToLower()))
        {
            Save(prefab);
            _prefabs[prefab.Id.ToLower()] = prefab;
        }
    }

    public void Save(PrefabTemplate prefab)
    {
        string path = Path.Combine(_directory, $"{prefab.Id}.json");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { 
                new System.Text.Json.Serialization.JsonStringEnumConverter(),
                new OpenFPS.Common.Networking.Vector3Converter(),
                new OpenFPS.Common.Networking.QuaternionConverter()
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(prefab, options));
    }

    public Entity Spawn(World world, string prefabId, Vector3 position, Quaternion? rotation = null, Vector3? scale = null)
    {
        if (!_prefabs.TryGetValue(prefabId.ToLower(), out var t))
        {
            throw new Exception($"Prefab {prefabId} not found.");
        }

        var components = new List<object>
        {
            new Transform { Position = position, Rotation = rotation ?? Quaternion.Identity, Scale = scale ?? Vector3.One },
            new NameComponent { Name = t.Name },
            new IdentityComponent { Name = t.Name },
            t.Type
        };

        if (t.ColliderSize.HasValue)
        {
            var finalSize = t.ColliderSize.Value;
            if (scale != null) finalSize *= scale.Value;
            components.Add(new ColliderComponent { Size = finalSize, IsSolid = t.IsSolid ?? true, Shape = ColliderShape.Box });
        }

        if (!string.IsNullOrEmpty(t.Material))
        {
            components.Add(new MaterialComponent { Material = t.Material });
        }

        if (t.MaxHealth.HasValue)
        {
            components.Add(new HealthComponent { Current = t.MaxHealth.Value, Max = t.MaxHealth.Value });
        }

        int finalFaceMask = t.FaceMask ?? 63;
        if (t.MissingFaces != null)
        {
            finalFaceMask = 63;
            foreach (var face in t.MissingFaces)
            {
                if (face.Equals("North", StringComparison.OrdinalIgnoreCase)) finalFaceMask &= ~1;
                if (face.Equals("South", StringComparison.OrdinalIgnoreCase)) finalFaceMask &= ~2;
                if (face.Equals("East", StringComparison.OrdinalIgnoreCase)) finalFaceMask &= ~4;
                if (face.Equals("West", StringComparison.OrdinalIgnoreCase)) finalFaceMask &= ~8;
                if (face.Equals("Top", StringComparison.OrdinalIgnoreCase) || face.Equals("Ceiling", StringComparison.OrdinalIgnoreCase)) finalFaceMask &= ~16;
                if (face.Equals("Bottom", StringComparison.OrdinalIgnoreCase) || face.Equals("Floor", StringComparison.OrdinalIgnoreCase)) finalFaceMask &= ~32;
            }
        }

        if (t.TransmissionLow.HasValue || t.TransmissionMid.HasValue || t.TransmissionHigh.HasValue || t.Absorption.HasValue || t.Scattering.HasValue || t.ShellThickness.HasValue || t.FaceMask.HasValue || t.MissingFaces != null)
        {
            components.Add(new AcousticComponent 
            { 
                TransmissionLow = t.TransmissionLow ?? 1.0f, 
                TransmissionMid = t.TransmissionMid ?? 1.0f, 
                TransmissionHigh = t.TransmissionHigh ?? 1.0f,
                Absorption = t.Absorption ?? 0.0f,
                Scattering = t.Scattering ?? 0.0f,
                ShellThickness = t.ShellThickness ?? 0.0f,
                IsHollow = t.ShellThickness.HasValue && t.ShellThickness.Value > 0,
                FaceMask = finalFaceMask
            });
        }

        if (t.HasEmitter)
        {
            components.Add(new SoundEmitterComponent
            {
                SoundId = t.SoundId ?? "",
                Volume = t.Volume ?? 1.0f,
                Range = t.Range ?? 50.0f,
                Mode = t.Mode ?? PlaybackMode.Single,
                ConeInsideAngle = t.ConeInsideAngle ?? 360f,
                ConeOutsideAngle = t.ConeOutsideAngle ?? 360f,
                ConeOutsideVolume = t.ConeOutsideVolume ?? 1.0f,
                MinDistance = t.MinDistance ?? 3.0f,
                
                IsGranular = t.IsGranular ?? false,
                GranularPosition = t.GranularPosition ?? 0f,
                GranularGrainSizeMs = t.GranularGrainSize ?? 50f,
                GranularDensity = t.GranularDensity ?? 20f,
                GranularPitch = t.GranularPitch ?? 1.0f,
                GranularPositionJitter = t.GranularPosJitter ?? 0f,
                GranularPitchJitter = t.GranularPitchJitter ?? 0f,

                IsSynth = t.IsSynth ?? false,
                SynthWave = t.SynthWave ?? 0,
                SynthFrequency = t.SynthFreq ?? 440f,
                SynthLfoRate = t.SynthLfoRate ?? 0f,
                SynthLfoDepth = t.SynthLfoDepth ?? 0f,
                SynthFilterCutoff = t.SynthFilterCutoff ?? 1.0f,
                SynthFilterResonance = t.SynthFilterResonance ?? 0.0f,
                SynthPulseWidth = t.SynthPulseWidth ?? 0.5f
            });
        }

        if (t.Mass.HasValue || t.Friction.HasValue || t.Restitution.HasValue || t.Drag.HasValue)
        {
            components.Add(new PhysicsPropertyComponent
            {
                Mass = t.Mass ?? 0.0f,
                Friction = t.Friction ?? 0.5f,
                Restitution = t.Restitution ?? 0.0f,
                Drag = t.Drag ?? 0.1f
            });
        }

        if (t.IsIndoor.HasValue || t.RoomSize.HasValue)
        {
            components.Add(new RegionComponent
            {
                FriendlyName = t.Name,
                IsIndoor = t.IsIndoor ?? true,
                Environment = t.EnvType ?? AcousticEnvironmentType.Atmospheric,
                RoomSize = t.RoomSize ?? Vector3.Zero,
                AmbienceId = t.AmbienceId ?? "",
                ReverbTimeScale = t.ReverbScale ?? 1.0f
            });
        }

        if (t.RegionAId.HasValue || t.RegionBId.HasValue)
        {
            components.Add(new PortalComponent
            {
                RegionAId = t.RegionAId ?? -1,
                RegionBId = t.RegionBId ?? -1,
                ApertureSize = t.ApertureSize ?? 1.0f
            });
        }

        var entity = world.Create();
        foreach (var component in components)
        {
            world.Add(entity, component);
        }
        return entity;
    }
}
