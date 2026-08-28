using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// Cover for the one prefab spec: <see cref="PrefabTemplate"/> is the format, `prefab-schema.json`
/// describes exactly it, every shipped prefab satisfies it, and a prefab the engine cannot honour is
/// rejected at load with the reason named rather than spawned with the offending part missing.
///
/// This exists because the three descriptions of the format disagreed. The schema documented a nested
/// `Collider` / `SoundEmitter` / `Acoustics` shape and an integer `Type`; the map standard documented a
/// third; the loader read neither. An author following either document produced a file that deserialized
/// into all-defaults and spawned an invisible, silent, materialless cube — and nothing said a word,
/// because System.Text.Json drops an unrecognised key without comment.
/// </summary>
public class PrefabSpecTests
{
    private static string PrefabDirectory => Path.Combine(AppContext.BaseDirectory, "prefabs");

    private static IEnumerable<string> PrefabFiles =>
        Directory.GetFiles(PrefabDirectory, "*.json")
                 .Where(f => !f.EndsWith("-schema.json", StringComparison.OrdinalIgnoreCase));

    private static JsonSerializerOptions ReadOptions => new()
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

    private static PrefabValidationResult ValidateFile(string file)
    {
        string json = File.ReadAllText(file);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        var template = JsonSerializer.Deserialize<PrefabTemplate>(json, ReadOptions);
        Assert.NotNull(template);
        return PrefabValidator.Validate(template!, names);
    }

    // --- The spec is one thing ---------------------------------------------------------------------

    [Fact]
    public void Schema_DescribesExactlyTheTemplateClass()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PrefabDirectory, "prefab-schema.json")));
        var root = doc.RootElement;

        var documented = root.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var actual = typeof(PrefabTemplate).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                           .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.True(documented.SetEquals(actual),
            $"prefab-schema.json and PrefabTemplate disagree. Only in the schema: [{string.Join(", ", documented.Except(actual))}]. " +
            $"Only in the class: [{string.Join(", ", actual.Except(documented))}].");

        // A schema that tolerates unknown keys cannot catch the typo that the loader also cannot catch.
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());

        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "Id", "Name" }, required);
    }

    [Fact]
    public void EveryShippedPrefab_IsValid()
    {
        var problems = new List<string>();
        foreach (var file in PrefabFiles)
        {
            var result = ValidateFile(file);
            foreach (var error in result.Errors) problems.Add($"{Path.GetFileName(file)}: {error}");
        }

        Assert.True(problems.Count == 0, "Shipped prefabs must satisfy their own spec:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void EveryShippedPrefab_LoadsIntoTheRepository()
    {
        var repo = new PrefabRepository(PrefabDirectory);

        Assert.Empty(repo.RejectedPrefabs);
        Assert.Equal(PrefabFiles.Count(), repo.Prefabs.Count);
    }

    // --- Incoherent prefabs are rejected, not half-spawned -------------------------------------------

    private static PrefabTemplate Valid() => new() { Id = "test_prefab", Name = "Test Prefab", Material = "Concrete" };

    private static void Rejects(PrefabTemplate t, string expectedFragment, IEnumerable<string>? jsonNames = null)
    {
        var result = PrefabValidator.Validate(t, jsonNames);
        Assert.False(result.IsValid, "Expected this prefab to be rejected, but it validated clean.");
        Assert.Contains(result.Errors, e => e.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BaselinePrefab_IsAccepted() => Assert.True(PrefabValidator.Validate(Valid()).IsValid);

    [Fact]
    public void UnknownField_IsRejectedByName()
    {
        // The whole class of bug this catches: a field the loader will drop without a word.
        Rejects(Valid(), "'ColiderSize'", new[] { "Id", "Name", "ColiderSize" });
    }

    [Fact]
    public void MissingName_IsRejected() => Rejects(new PrefabTemplate { Id = "x" }, "Name is missing");

    [Fact]
    public void UnknownMaterial_IsRejected()
    {
        var t = Valid();
        t.Material = "Cheese";
        Rejects(t, "not a known material");
    }

    [Fact]
    public void EmitterSettingsWithoutHasEmitter_AreRejected()
    {
        var t = Valid();
        t.SoundId = "BEACONS/siren";
        t.Range = 20f;
        Rejects(t, "HasEmitter is false");
    }

    [Fact]
    public void EmitterWithNothingToPlay_IsRejected()
    {
        var t = Valid();
        t.HasEmitter = true;
        Rejects(t, "nothing to play");
    }

    [Fact]
    public void ConeInsideWiderThanOutside_IsRejected()
    {
        var t = Valid();
        t.HasEmitter = true;
        t.SoundId = "BEACONS/siren";
        t.ConeInsideAngle = 200f;
        t.ConeOutsideAngle = 90f;
        Rejects(t, "inside out");
    }

    [Fact]
    public void MinDistanceBeyondRange_IsRejected()
    {
        var t = Valid();
        t.HasEmitter = true;
        t.SoundId = "BEACONS/siren";
        t.Range = 5f;
        t.MinDistance = 10f;
        Rejects(t, "inverted");
    }

    [Fact]
    public void ZeroLengthEmitterDirection_IsRejected()
    {
        var t = Valid();
        t.HasEmitter = true;
        t.SoundId = "BEACONS/siren";
        t.EmitterDirection = Vector3.Zero;
        Rejects(t, "points nowhere");
    }

    [Fact]
    public void SilentBeacon_IsRejected()
    {
        var t = Valid();
        t.Type = EntityType.Beacon;
        Rejects(t, "silent one");
    }

    [Fact]
    public void SynthParametersWithoutIsSynth_AreRejected()
    {
        var t = Valid();
        t.HasEmitter = true;
        t.SoundId = "BEACONS/siren";
        t.SynthFreq = 440f;
        Rejects(t, "IsSynth is false");
    }

    [Fact]
    public void SolidRegion_IsRejected()
    {
        // The real one: building_box declared itself a 10x5x10 SOLID box and an indoor region, so its
        // region voxels sat inside geometry the listener can never stand in.
        var t = Valid();
        t.ColliderSize = new Vector3(10, 5, 10);
        t.IsSolid = true;
        t.IsIndoor = true;
        t.RoomSize = new Vector3(10, 5, 10);
        Rejects(t, "region AND a solid collider");
    }

    [Fact]
    public void RegionWithoutRoomSize_IsRejected()
    {
        var t = Valid();
        t.IsSolid = false;
        t.AmbienceId = "AMBIENCE/room";
        Rejects(t, "RoomSize");
    }

    [Fact]
    public void RoomMaterials_WrongLengthOrUnknownName_AreRejected()
    {
        var t = Valid();
        t.IsSolid = false;
        t.RoomSize = new Vector3(4, 3, 4);
        t.RoomMaterials = new[] { "Concrete", "Concrete" };
        Rejects(t, "exactly 6");

        t.RoomMaterials = new[] { "Concrete", "Concrete", "Concrete", "Concrete", "Concrete", "Cheese" };
        Rejects(t, "not a known material");
    }

    [Fact]
    public void PortalLinkingARegionToItself_IsRejected()
    {
        var t = Valid();
        t.IsSolid = false;
        t.RegionAId = 210;
        t.RegionBId = 210;
        Rejects(t, "two DIFFERENT regions");
    }

    [Fact]
    public void SolidPortal_IsRejected()
    {
        var t = Valid();
        t.ColliderSize = new Vector3(2, 2, 0.1f);
        t.IsSolid = true;
        t.RegionAId = -1;
        t.RegionBId = 210;
        Rejects(t, "doorway that blocks");
    }

    [Fact]
    public void FaceMaskAndMissingFaces_Conflict_IsRejected()
    {
        var t = Valid();
        t.FaceMask = 63;
        t.MissingFaces = new List<string> { "Top" };
        Rejects(t, "Keep one");
    }

    [Fact]
    public void ZeroColliderExtent_IsRejected()
    {
        var t = Valid();
        t.ColliderSize = new Vector3(2, 0, 2);
        Rejects(t, "non-positive extent");
    }

    [Fact]
    public void IsSolidWithoutACollider_IsRejected()
    {
        var t = Valid();
        t.IsSolid = true;
        Rejects(t, "nothing is solid");
    }

    [Fact]
    public void ItemFlagAndTypeMustAgree()
    {
        var t = Valid();
        t.IsItem = true;
        Rejects(t, "Type must be 'Item'");
    }

    [Fact]
    public void SolidNonBoxCollider_Warns_ButLoads()
    {
        var t = Valid();
        t.ColliderSize = new Vector3(2, 2, 2);
        t.Shape = ColliderShape.Sphere;

        var result = PrefabValidator.Validate(t);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("box colliders only", StringComparison.OrdinalIgnoreCase));
    }

    // --- Rejection is visible at the point of use -----------------------------------------------------

    [Fact]
    public void RejectedPrefab_IsNotSpawnable_AndTheErrorSaysWhy()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-prefab-spec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Emitter settings with the emitter switched off: the old loader accepted this and spawned
            // an object that was permanently silent.
            File.WriteAllText(Path.Combine(dir, "broken.json"),
                """{ "Id": "broken", "Name": "Broken", "SoundId": "BEACONS/siren", "Range": 20 }""");

            var repo = new PrefabRepository(dir);

            Assert.Contains("broken", repo.RejectedPrefabs.Keys);
            Assert.Empty(repo.Prefabs);

            using var world = new WorldScope();
            var ex = Assert.Throws<Exception>(() => repo.Spawn(world.World, "broken", Vector3.Zero));
            Assert.Contains("REJECTED", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DuplicateIds_AreRejected_RatherThanOneSilentlyReplacingTheOther()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-prefab-dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a_wall.json"), """{ "Id": "a_wall", "Name": "Wall", "Material": "Concrete" }""");
            File.WriteAllText(Path.Combine(dir, "b_wall.json"), """{ "Id": "a_wall", "Name": "Other Wall", "Material": "Wood" }""");

            var repo = new PrefabRepository(dir);

            Assert.Single(repo.Prefabs);
            Assert.Contains("a_wall", repo.RejectedPrefabs.Keys);
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- The new fields actually reach their components ------------------------------------------------

    [Fact]
    public void Spawn_AppliesTheFieldsTheFormatGained()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-prefab-spawn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "siren_post.json"),
                """
                {
                  "Id": "siren_post",
                  "Name": "Siren Post",
                  "Description": "A post with a siren on top.",
                  "Material": "Metal",
                  "ColliderSize": { "X": 0.4, "Y": 2, "Z": 0.4 },
                  "Shape": "Cylinder",
                  "IsSolid": false,
                  "HasEmitter": true,
                  "SoundId": "BEACONS/siren",
                  "StartSoundId": "BEACONS/spinup",
                  "StopSoundId": "BEACONS/spindown",
                  "EmitterDirection": { "X": 1, "Y": 0, "Z": 0 },
                  "ConeInsideAngle": 40,
                  "ConeOutsideAngle": 110,
                  "ConeOutsideVolume": 0.05
                }
                """);

            var repo = new PrefabRepository(dir);
            Assert.Empty(repo.RejectedPrefabs);

            using var world = new WorldScope();
            var entity = repo.Spawn(world.World, "siren_post", Vector3.Zero);

            Assert.Equal(ColliderShape.Cylinder, world.World.Get<ColliderComponent>(entity).Shape);
            Assert.Equal("A post with a siren on top.", world.World.Get<IdentityComponent>(entity).Description);

            var emitter = world.World.Get<SoundEmitterComponent>(entity);
            Assert.Equal("BEACONS/spinup", emitter.StartSoundId);
            Assert.Equal("BEACONS/spindown", emitter.StopSoundId);
            Assert.Equal(new Vector3(1, 0, 0), emitter.Direction);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Spawn_ResolvesRoomMaterialNamesToResonanceIndices()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-prefab-room-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "gym.json"),
                """
                {
                  "Id": "gym",
                  "Name": "Gym",
                  "Material": "None",
                  "IsSolid": false,
                  "IsIndoor": true,
                  "RoomSize": { "X": 10, "Y": 5, "Z": 10 },
                  "RoomMaterials": ["Wood", "Concrete", "Concrete", "Concrete", "Concrete", "Carpet"]
                }
                """);

            var repo = new PrefabRepository(dir);
            Assert.Empty(repo.RejectedPrefabs);

            using var world = new WorldScope();
            var region = world.World.Get<RegionComponent>(repo.Spawn(world.World, "gym", Vector3.Zero));

            AcousticRegistry.EnsureInitialized();
            Assert.Equal(AcousticRegistry.GetProperties("Wood").ResonanceIndex, region.Materials[0]);     // floor
            Assert.Equal(AcousticRegistry.GetProperties("Concrete").ResonanceIndex, region.Materials[1]); // ceiling
            Assert.Equal(AcousticRegistry.GetProperties("Carpet").ResonanceIndex, region.Materials[5]);   // west
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DefaultMap_RoomMaterialNames_ReachTheRegionComponents()
    {
        var prefabs = new PrefabRepository(PrefabDirectory);
        var maps = new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        var manager = new MapManager(maps, prefabs);
        manager.Initialize();

        Assert.True(manager.TryGetMap("default", out World world, out _, out _, out _));

        var defs = EntityDefinitionFactory.StaticDefinitions(world);
        // The gym in the material lab: wood floor, concrete everywhere else.
        var gym = defs.Single(d => d.Region.RoomSize.X > 0 && Vector3.Distance(d.Transform.Position, new Vector3(5, 2, 35)) < 0.01f);

        AcousticRegistry.EnsureInitialized();
        Assert.Equal(AcousticRegistry.GetProperties("Wood").ResonanceIndex, gym.Region.Materials[0]);
        Assert.Equal(AcousticRegistry.GetProperties("Concrete").ResonanceIndex, gym.Region.Materials[1]);
    }

    [Fact]
    public void DefaultMap_UsesOnlyFieldsTheLoaderReads()
    {
        // The map equivalent of the unknown-prefab-field rule. A key the loader does not know is dropped
        // in silence, so the authored setting simply never happens.
        var mapFields = typeof(MapData).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entityFields = typeof(OpenFPS.Server.Repositories.EntityData).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "maps", "default.json"));
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var unknown = new List<string>();
        foreach (var property in doc.RootElement.EnumerateObject())
            if (!mapFields.Contains(property.Name)) unknown.Add($"map field '{property.Name}'");

        foreach (var entity in doc.RootElement.GetProperty("Entities").EnumerateArray())
        {
            string label = entity.TryGetProperty("EntityId", out var id) ? id.ToString() : "?";
            foreach (var property in entity.EnumerateObject())
                if (!entityFields.Contains(property.Name)) unknown.Add($"entity {label} field '{property.Name}'");
        }

        Assert.True(unknown.Count == 0, "maps/default.json carries fields the loader ignores: " + string.Join(", ", unknown));
    }

    /// <summary>Arch worlds are process-global; each test that creates one must destroy it.</summary>
    private sealed class WorldScope : IDisposable
    {
        public World World { get; } = World.Create();
        public void Dispose() => World.Destroy(World);
    }
}
