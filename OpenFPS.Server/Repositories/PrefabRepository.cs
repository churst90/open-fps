using System.Text.Json;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using Arch.Core;
using Serilog;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// Loads <see cref="PrefabTemplate"/> files, the only reader of them, and turns one into an entity.
/// Every file is checked by <see cref="PrefabValidator"/>; one the engine cannot honour is rejected with
/// the reasons named, and a map that places a rejected id is told so rather than "not found".
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

    /// <summary>The folder the prefabs are read from (prefab-schema.json is there too).</summary>
    public string Folder => _directory;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(),
            new OpenFPS.Common.Networking.Vector3Converter(),
            new OpenFPS.Common.Networking.QuaternionConverter()
        }
    };

    /// <summary>A prefab as its file would have it: enums by name, nothing written for an omitted field.</summary>
    public static string ToJson(PrefabTemplate t) => JsonSerializer.Serialize(t, WriteOptions);

    /// <summary>
    /// A prefab read from JSON and checked as the loader checks a file. Throws, saying what is wrong, if
    /// it is not a prefab the engine can honour.
    /// </summary>
    public static PrefabTemplate FromJson(string json)
    {
        List<string> keys;
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("A prefab is a JSON object.");
            keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        }
        var t = JsonSerializer.Deserialize<PrefabTemplate>(json, ReadOptions()) ?? throw new ArgumentException("The prefab is empty.");
        var result = PrefabValidator.Validate(t, keys);
        if (!result.IsValid) throw new ArgumentException(string.Join(" ", result.Errors.Take(3)));
        return t;
    }

    /// <summary>Puts a prefab in use in memory, replacing one of the same id (the world editor's versions;
    /// no file is written).</summary>
    public void Put(PrefabTemplate t) => _prefabs[t.Id.ToLowerInvariant()] = t;

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
                // Parsed twice: System.Text.Json drops an unknown key without a word, and the validator
                // needs the keys to name a mistyped field.
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

    /// <summary>Whether this prefab is spoken as the player walks up to it. An explicit `Announce` wins;
    /// otherwise only items, NPCs and beacons are, which keeps portals, regions and walls silent.</summary>
    internal static bool AnnouncesByDefault(PrefabTemplate t) =>
        t.Announce ?? t.Type is EntityType.Item or EntityType.NPC or EntityType.Beacon;

    /// <summary>A thing's beacon category by what it is: a door, an item, or a Beacon's own category
    /// (a waypoint if it names none). Empty for anything else.</summary>
    public static string BeaconCategoryOf(PrefabTemplate t)
    {
        if (!string.IsNullOrWhiteSpace(t.BeaconCategory) && OpenFPS.Common.Beacons.IsCategory(t.BeaconCategory))
            return t.BeaconCategory!.ToLowerInvariant();
        if (t.IsDoor == true) return OpenFPS.Common.Beacons.Door;
        if (t.Type == EntityType.Item) return OpenFPS.Common.Beacons.Item;
        if (t.Type == EntityType.Beacon) return OpenFPS.Common.Beacons.Waypoint;
        return "";
    }

    /// <summary>A prefab made into an entity.</summary>
    /// <param name="regionName">What to call the region this spawns, if it declares one: a prefab names
    /// a kind of room ("Acoustic Region"), the map that places it names that room ("Pit lane").</param>
    public Entity Spawn(World world, string prefabId, Vector3 position, Quaternion? rotation = null,
                        Vector3? scale = null, string? regionName = null)
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
            // PrefabId on the instance, so a composite saved from it can be written back out.
            new IdentityComponent { Name = t.Name, Description = t.Description, Announce = AnnouncesByDefault(t), PrefabId = t.Id,
                                    BeaconCategory = BeaconCategoryOf(t) },
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
                Shape = t.Shape ?? ColliderShape.Box,
                Form = t.Form,
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
        if (t.CrowdPeople.HasValue)
        {
            components.Add(new CrowdComponent
            {
                People = Math.Max(1, t.CrowdPeople.Value),
                ReactRadiusMetres = t.CrowdReactRadiusMetres ?? 60f,
                CooldownSeconds = 6f,
            });
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

        // A door's skins are its leaves: one datum, read by the door's ring and by what gets through it.
        float leaf = t.LeafMetres ?? (t.IsDoor == true ? t.DoorSkinMetres ?? 0f : 0f);
        if (t.TransmissionLow.HasValue || t.TransmissionMid.HasValue || t.TransmissionHigh.HasValue || t.Absorption.HasValue || t.Scattering.HasValue || t.ShellThickness.HasValue || t.FaceMask.HasValue || t.MissingFaces != null
            || leaf > 0f || t.StudSpacingMetres.HasValue)
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
                FaceMask = finalFaceMask,
                LeafMetres = MathF.Max(0f, leaf),
                StudSpacingMetres = MathF.Max(0f, t.StudSpacingMetres ?? 0f),
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
                // Local-space aim, rotated by the entity on the client; zero means the entity's forward.
                Direction = t.EmitterDirection ?? Vector3.Zero,
                // Where the sound comes out, in the entity's own frame; zero is the origin. See AudioEmission.
                Offset = t.EmitterOffset ?? Vector3.Zero,
                ConeInsideAngle = t.ConeInsideAngle ?? 360f,
                ConeOutsideAngle = t.ConeOutsideAngle ?? 360f,
                ConeOutsideVolume = t.ConeOutsideVolume ?? 1.0f,
                RepeatIntervalSeconds = t.RepeatIntervalSeconds ?? 0f,
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
                SynthPulseWidth = t.SynthPulseWidth ?? 0.5f,
                SynthRunning = t.SynthRunning ?? true,
                Loudspeaker = t.Loudspeaker ?? "",
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

        // Any region field declares a region; the validator insists on the RoomSize the reverb needs.
        if (t.IsIndoor.HasValue || t.RoomSize.HasValue ||
            !string.IsNullOrEmpty(t.AmbienceId) || t.ReverbScale.HasValue || t.RoomMaterials != null)
        {
            var region = new RegionComponent
            {
                FriendlyName = string.IsNullOrWhiteSpace(regionName) ? t.Name : regionName,
                IsIndoor = t.IsIndoor ?? true,
                // The room scales with the entity, as the collider does.
                RoomSize = (t.RoomSize ?? Vector3.Zero) * (scale ?? Vector3.One),
                AmbienceId = t.AmbienceId ?? "",
                ReverbTimeScale = t.ReverbScale ?? 1.0f,
                Materials = ResolveRoomMaterials(t.RoomMaterials)
            };
            components.Add(region);
        }

        if (t.IsItem)
        {
            components.Add(new ItemComponent
            {
                MassKg = MathF.Max(0.01f, t.ItemWeight ?? 1f),
                Hands = t.Hands is 2 ? 2 : 1,
                WeaponId = t.WeaponId ?? "",
            });
        }

        bool isDoor = t.IsDoor == true;

        // A door is always a portal. One with no regions named joins the outside, which is right for a
        // front door and harmless for any other.
        if (t.RegionAId.HasValue || t.RegionBId.HasValue || isDoor)
        {
            components.Add(new PortalComponent
            {
                RegionAId = t.RegionAId ?? AcousticConstants.GlobalRegionId,
                RegionBId = t.RegionBId ?? AcousticConstants.GlobalRegionId,
                // A door's aperture is however far its leaf has swung: none at rest.
                ApertureSize = isDoor ? 0f : t.ApertureSize ?? 0f
            });
        }

        if (isDoor)
        {
            OpenFPS.Common.DoorEvents.TryParseKind(t.DoorKind, out var kind);
            components.Add(new DoorComponent
            {
                Kind = (int)kind,
                Slides = t.Slides ?? OpenFPS.Common.DoorEvents.SlidesByDefault(kind),
                Powered = t.Powered ?? false,
                SensorMetres = MathF.Max(0f, t.SensorMetres ?? 0f),
                CloseAfterSeconds = MathF.Max(0f, t.CloseAfterSeconds ?? 0f),
                CloseSeconds = MathF.Max(0f, t.CloseSeconds ?? 0f),
                KeyedSide = t.KeyedSide is > 0 ? 1f : t.KeyedSide is < 0 ? -1f : 0f,
                PushSide = t.PushSide is < 0 ? -1f : 1f,
                SwingSeconds = t.SwingSeconds ?? 0.9f,
                SwingRadians = (t.SwingDegrees ?? 90f) * (MathF.PI / 180f),
                HingeSide = t.HingeSide is < 0 ? -1f : 1f,
                SkinMetres = MathF.Max(0f, t.DoorSkinMetres ?? 0f),
                // Zero: DoorSystem takes the hole's size from the leaf itself.
                Aperture = 0f,
            });
        }

        var entity = world.Create();
        foreach (var component in components)
        {
            world.Add(entity, component);
        }
        return entity;
    }

    /// <summary>The six face material names as the resonance indices RegionComponent stores, in the
    /// order the Sabine reverb reads them (Floor, Ceiling, North, South, East, West), which is not the
    /// FaceMask bit order.</summary>
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
