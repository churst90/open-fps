using System.Text.Json;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using Arch.Core;
using Serilog;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// Loads <see cref="PrefabTemplate"/> files and turns one into an entity. The template class is the format;
/// this is the only reader of it.
///
/// Loading is not just deserialization: every file is checked against <see cref="PrefabValidator"/> and a
/// prefab whose description the engine cannot honour is REJECTED with the reasons named, rather than
/// spawning something that quietly lacks whatever was wrong. Rejected ids are remembered, so the map that
/// references one is told the prefab was rejected instead of that it does not exist.
/// </summary>
public class PrefabRepository
{
    private readonly string _directory;
    private Dictionary<string, PrefabTemplate> _prefabs = new();
    private readonly Dictionary<string, int> _rejected = new(StringComparer.OrdinalIgnoreCase);

    public PrefabRepository(string directory)
    {
        _directory = directory;
        if (!Directory.Exists(_directory)) Directory.CreateDirectory(_directory);
        // Validation resolves material NAMES, and the server never initialized the registry.
        AcousticRegistry.EnsureInitialized();
        LoadAll();
    }

    /// <summary>Ids that failed validation, with how many problems each had.</summary>
    public IReadOnlyDictionary<string, int> RejectedPrefabs => _rejected;

    public IReadOnlyDictionary<string, PrefabTemplate> Prefabs => _prefabs;

    private static JsonSerializerOptions ReadOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = {
            new System.Text.Json.Serialization.JsonStringEnumConverter(),
            new OpenFPS.Common.Networking.Vector3Converter(),
            new OpenFPS.Common.Networking.QuaternionConverter()
        }
    };

    public void LoadAll()
    {
        _prefabs.Clear();
        _rejected.Clear();
        var options = ReadOptions();
        var sourceFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(_directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith("-schema.json", StringComparison.OrdinalIgnoreCase)) continue;

            PrefabTemplate? template;
            List<string> jsonProperties;
            try
            {
                string json = File.ReadAllText(file);
                // Parsed twice on purpose: once for the values, once for the KEYS. System.Text.Json drops a
                // property it does not recognise without a word, so a mistyped field is a setting that never
                // applies and never complains. The validator needs the names to say so.
                using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        Log.Error("PrefabRepository: {File} is not a JSON object — skipped.", name);
                        continue;
                    }
                    jsonProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
                }

                template = JsonSerializer.Deserialize<PrefabTemplate>(json, options);
            }
            catch (Exception ex)
            {
                Log.Error("PrefabRepository: {File} could not be read as a prefab and was REJECTED. {Error}", name, ex.Message);
                continue;
            }

            if (template == null)
            {
                Log.Error("PrefabRepository: {File} deserialized to nothing and was REJECTED.", name);
                continue;
            }

            var result = PrefabValidator.Validate(template, jsonProperties);
            string id = string.IsNullOrWhiteSpace(template.Id) ? Path.GetFileNameWithoutExtension(file) : template.Id;

            string stem = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrWhiteSpace(template.Id) && !string.Equals(template.Id, stem, StringComparison.OrdinalIgnoreCase))
                result.Warnings.Add($"Id '{template.Id}' does not match the file name '{stem}'. Maps reference the Id, not the file.");

            if (sourceFile.TryGetValue(id, out var firstFile))
                result.Errors.Add($"Id '{id}' is already defined by {firstFile}. One of the two would silently replace the other.");

            foreach (var warning in result.Warnings)
                Log.Warning("PrefabRepository: {File} — {Warning}", name, warning);

            if (!result.IsValid)
            {
                _rejected[id] = result.Errors.Count;
                Log.Error("PrefabRepository: REJECTED {File} — {Count} problem(s):", name, result.Errors.Count);
                foreach (var error in result.Errors) Log.Error("PrefabRepository:   • {Error}", error);
                continue;
            }

            sourceFile[id] = name;
            _prefabs[id.ToLowerInvariant()] = template;
        }

        if (_rejected.Count > 0)
            Log.Error("PrefabRepository: {Loaded} prefab(s) loaded, {Rejected} REJECTED ({Ids}). " +
                      "Any map entity using a rejected prefab will fail to spawn.",
                _prefabs.Count, _rejected.Count, string.Join(", ", _rejected.Keys));
        else
            Log.Information("PrefabRepository: {Loaded} prefab(s) loaded, all valid.", _prefabs.Count);
    }

    public void Save(PrefabTemplate prefab)
    {
        string path = Path.Combine(_directory, $"{prefab.Id}.json");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
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
        if (!_prefabs.TryGetValue(prefabId.ToLowerInvariant(), out var t))
        {
            if (_rejected.TryGetValue(prefabId, out int problems))
                throw new Exception($"Prefab '{prefabId}' was REJECTED at load with {problems} problem(s) — see the startup log for each one.");
            throw new Exception($"Prefab {prefabId} not found.");
        }

        var components = new List<object>
        {
            new Transform { Position = position, Rotation = rotation ?? Quaternion.Identity, Scale = scale ?? Vector3.One },
            new NameComponent { Name = t.Name },
            new IdentityComponent { Name = t.Name, Description = t.Description },
            t.Type
        };

        if (t.ColliderSize.HasValue)
        {
            var finalSize = t.ColliderSize.Value;
            if (scale != null) finalSize *= scale.Value;
            components.Add(new ColliderComponent
            {
                Size = finalSize,
                IsSolid = t.IsSolid ?? true,
                Shape = t.Shape ?? ColliderShape.Box
            });
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
                StartSoundId = t.StartSoundId ?? "",
                StopSoundId = t.StopSoundId ?? "",
                Volume = t.Volume ?? 1.0f,
                Range = t.Range ?? 50.0f,
                Mode = t.Mode ?? PlaybackMode.Single,
                // Local-space aim, rotated into the world by the entity's own rotation on the client.
                // Zero means "use the entity's forward", which is what every emitter did implicitly before.
                Direction = t.EmitterDirection ?? Vector3.Zero,
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

        // ANY region field declares a region. This used to key off IsIndoor/RoomSize alone, so a prefab
        // that named only an AmbienceId or an EnvType got no RegionComponent at all and the setting
        // vanished; the validator now also insists such a prefab carries the RoomSize the reverb needs.
        if (t.IsIndoor.HasValue || t.EnvType.HasValue || t.RoomSize.HasValue ||
            !string.IsNullOrEmpty(t.AmbienceId) || t.ReverbScale.HasValue || t.RoomMaterials != null)
        {
            var region = new RegionComponent
            {
                FriendlyName = t.Name,
                IsIndoor = t.IsIndoor ?? true,
                Environment = t.EnvType ?? AcousticEnvironmentType.Atmospheric,
                RoomSize = t.RoomSize ?? Vector3.Zero,
                AmbienceId = t.AmbienceId ?? "",
                ReverbTimeScale = t.ReverbScale ?? 1.0f,
                Materials = ResolveRoomMaterials(t.RoomMaterials)
            };
            components.Add(region);
        }

        if (t.RegionAId.HasValue || t.RegionBId.HasValue)
        {
            components.Add(new PortalComponent
            {
                RegionAId = t.RegionAId ?? AcousticConstants.GlobalRegionId,
                RegionBId = t.RegionBId ?? AcousticConstants.GlobalRegionId,
                ApertureSize = t.ApertureSize ?? 0f
            });
        }

        var entity = world.Create();
        foreach (var component in components)
        {
            world.Add(entity, component);
        }
        return entity;
    }

    /// <summary>
    /// Turns the six face MATERIAL NAMES into the resonance indices RegionComponent stores.
    /// Order is Floor, Ceiling, North, South, East, West — the order the Sabine reverb math reads them,
    /// which is deliberately not the FaceMask bit order.
    /// </summary>
    internal static int[] ResolveRoomMaterials(string[]? names)
    {
        var indices = new int[6];
        if (names == null) return indices;
        for (int i = 0; i < indices.Length && i < names.Length; i++)
        {
            if (AcousticRegistry.TryGetResonanceIndex(names[i], out int index)) indices[i] = index;
        }
        return indices;
    }
}
