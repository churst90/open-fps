using System.Text.Json;
using System.Numerics;
using OpenFPS.Materials;
using OpenFPS.Common.Components;
using Arch.Core;
using Serilog;
using OpenFPS.Geometry;
using OpenFPS.Acoustics;
using OpenFPS.Sound;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// The prefabs (PrefabLibrary, which loads and checks them) and the making of one into an entity.
/// </summary>
public class PrefabRepository : PrefabLibrary
{
    public PrefabRepository(string directory) : base(directory) { }

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
        if (!Prefabs.TryGetValue(prefabId.ToLowerInvariant(), out var t))
        {
            if (RejectedPrefabs.TryGetValue(prefabId, out int problems))
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
            || leaf > 0f || t.StudSpacingMetres.HasValue || t.Assembly != null)
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
                Assembly = Assemblies.Named(t.Assembly)?.Id ?? 0,
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

        // A region: its box is the one its collider fills, scaled as the entity is (where nothing floods, a yard or a
        // street, the box is the place; a room's flood measures the rest). No materials or reverb scale are authored.
        if (t.IsIndoor.HasValue)
        {
            var region = new RegionComponent
            {
                FriendlyName = string.IsNullOrWhiteSpace(regionName) ? t.Name : regionName,
                IsIndoor = t.IsIndoor.Value,
                RoomSize = (t.ColliderSize ?? Vector3.Zero) * (scale ?? Vector3.One),
                ReverbTimeScale = 1.0f,
                Materials = ResolveRoomMaterials(null)
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
                ApertureSize = isDoor ? 0f : t.ApertureSize ?? 0f,
                // Its gaps shut, where the prefab gives them; zero is the default where it hangs.
                GapMetres = isDoor ? MathF.Max(0f, t.DoorGapMetres ?? 0f) : 0f,
                UndercutMetres = isDoor ? MathF.Max(0f, t.DoorUndercutMetres ?? 0f) : 0f,
            });
        }

        if (isDoor)
        {
            OpenFPS.Sound.DoorEvents.TryParseKind(t.DoorKind, out var kind);
            components.Add(new DoorComponent
            {
                Kind = (int)kind,
                Slides = t.Slides ?? OpenFPS.Sound.DoorEvents.SlidesByDefault(kind),
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
