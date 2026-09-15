using MemoryPack;
using System.Numerics;
using System.Collections.Generic;
using System;

namespace OpenFPS.Common.Components;

public enum UserRole { Player, Dev, Admin }
public enum EntityType { None, Player, NPC, Beacon, StaticObject, Item, Projectile, Trigger }
public enum AIState { Idle, Wander, Chase, Attack }
public enum BTNodeStatus { Success, Failure, Running }
public enum BTNodeType { Sequence, Selector, Action }
public enum BTActionType { Wait, MoveTo, Attack, Patrol }

[MemoryPackable]
public partial struct BehaviorTreeComponent
{
    public AIState State { get; set; }
    public string BehaviorId { get; set; }
    public Vector3 TargetPosition { get; set; }
    public int TargetEntityId { get; set; }
    public float WaitTimer { get; set; }

    public BehaviorTreeComponent()
    {
        State = AIState.Idle;
        BehaviorId = "";
        TargetPosition = Vector3.Zero;
        TargetEntityId = -1;
        WaitTimer = 0;
    }
}
public enum WeatherType { Clear, Rain, Snow, Storm }
public enum AcousticEnvironmentType { Atmospheric, Vacuum, Underwater, Digital, LargeOpen, SmallTight }
public enum ColliderShape { Box, Sphere, Cylinder, Cone, Polygon } 

[MemoryPackable]
public partial struct Transform 
{ 
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 Scale { get; set; }
    public bool IsDirty { get; set; }
    public Transform() { Position = Vector3.Zero; Rotation = Quaternion.Identity; Scale = Vector3.One; IsDirty = false; }
}

[MemoryPackable]
public partial struct PlayerComponent 
{ 
    public string Username { get; set; } = ""; 
    public int ConnectionId { get; set; }
    public UserRole Role { get; set; }
    public bool IsInVehicle { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public bool IsGrounded { get; set; }
    public PlayerComponent() { }
}


[MemoryPackable]
public partial struct NameComponent { public string Name { get; set; } = ""; public NameComponent() { } }
[MemoryPackable]
public partial struct DescriptionComponent { public string Description { get; set; } = ""; public DescriptionComponent() { } }

[MemoryPackable]
public partial struct ZoneComponent 
{ 
    public string MapId { get; set; } = ""; 
    public Vector3 Size { get; set; }
    public Vector3 MinBound { get; set; }
    public Vector3 MaxBound { get; set; }
    public float Gravity { get; set; } = 15.0f;
    public float MinimumY { get; set; }
    
    // Environment Overrides. AirPressure is MILLIBARS (sea level 1013.25), matching what the client's
    // air-absorption model divides by — not atmospheres.
    public float Temperature { get; set; } = 20.0f;
    public float Humidity { get; set; } = 0.5f;
    public float AirPressure { get; set; } = 1013.25f;
    public float AirAbsorptionMultiplier { get; set; } = 1.0f;

    public ZoneComponent() { }
}

[MemoryPackable]
public partial struct IdentityComponent
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Whether the client should SAY this thing as the player walks up to it.
    ///
    /// Not every named entity is an interactable. The acoustic scaffolding — portals, region volumes —
    /// and the architecture itself (walls, floors, the auto-injected foundation) all carry names so that
    /// authors and logs can refer to them, and announcing those meant that crossing a doorway read the
    /// portal prefab's AUTHORING NOTES aloud, mid-stride. Default false: a thing earns its announcement.
    /// The prefab's `Announce` field sets it (see PrefabTemplate), defaulting to true only for the
    /// types a player can actually encounter: Item, NPC, Beacon.</summary>
    public bool Announce { get; set; } = false;

    public IdentityComponent() { }
}

[MemoryPackable]
public partial struct DirtyComponent { public DirtyComponent() { } }

[MemoryPackable]
public partial struct MaterialComponent
{
    public string Material { get; set; } = "Generic";
    public string Variant { get; set; } = "0";
    public MaterialComponent() { }
}

public enum PlaybackMode { Single, LoopOne, LoopFolder, Sequential, StateMachine }

[MemoryPackable]
public partial struct SoundEmitterComponent 
{ 
    public float Volume { get; set; } = 1.0f; 
    public float Range { get; set; } = 50.0f; 
    public string SoundId { get; set; } = ""; 
    public string StartSoundId { get; set; } = "";
    public string StopSoundId { get; set; } = "";
    public PlaybackMode Mode { get; set; } = PlaybackMode.Single;
    public Vector3 Direction { get; set; }
    public float ConeInsideAngle { get; set; } = 360f;
    public float ConeOutsideAngle { get; set; } = 360f;
    public float ConeOutsideVolume { get; set; } = 1.0f;
    public float MinDistance { get; set; } = 3.0f;

    /// <summary>
    /// Where the sound comes OUT, relative to the entity's origin and in its own frame
    /// (x right, y up, z forward). Zero — the default — means the origin itself.
    ///
    /// The emission point, not the object's position, is what every acoustic question is about: what
    /// is in the way of the sound, how far it has come, which direction it arrives from. Asking those
    /// about the origin was worth a whole class of fault. A vehicle's origin is its contact patch on
    /// the road, so the occlusion probe — a half-metre sphere — sat HALF UNDERGROUND on every level
    /// stretch of every track, and roughly half its samples reported "blocked" before any wall was
    /// considered. Six decibels down and forty off the top, permanently, for being a car on a road.
    ///
    /// Authored per emitter rather than corrected per case, because the answer is different for every
    /// object and known for all of them: a tailpipe is a third of a metre up and a metre or two back,
    /// a chimney is on the roof, a drain is at ground level, a speaker is where it was bolted. A fixed
    /// height added to everything would be the same mistake pointing the other way.
    /// </summary>
    public Vector3 Offset { get; set; }

    /// <summary>
    /// Replay this sound every N seconds. Zero (the default) means it is not a repeater.
    ///
    /// Deliberately a property of ANY emitter rather than of a public-address system: the thing that
    /// wanted it first was a PA announcing a racetrack, but a repeating one-shot from a fixed point
    /// is also a foghorn, a station bell, a level-crossing, a dripping tap and a klaxon. Nothing
    /// about it should know what a racetrack is.
    ///
    /// Distinct from LoopOne, which restarts the instant the sample ends and so has no gap. This is
    /// for a sound with SILENCE around it, where the silence is most of the point — the gap is what
    /// makes an announcement a landmark you can wait for rather than a drone you stop hearing.
    /// </summary>
    public float RepeatIntervalSeconds { get; set; }

    // Granular Synthesis
    public bool IsGranular { get; set; }
    public float GranularPosition { get; set; }
    public float GranularGrainSizeMs { get; set; }
    public float GranularDensity { get; set; }
    public float GranularPitch { get; set; }
    public float GranularPositionJitter { get; set; }
    public float GranularPitchJitter { get; set; }

    // Real-time Synthesis
    public bool IsSynth { get; set; }
    public int SynthWave { get; set; } // 0: Sine, 1: Square, 2: Triangle, 3: Saw, 4: Noise
    public float SynthFrequency { get; set; }
    public float SynthLfoRate { get; set; }
    public float SynthLfoDepth { get; set; }
    public float SynthFilterCutoff { get; set; }
    public float SynthFilterResonance { get; set; }
    public float SynthPulseWidth { get; set; }

    public SoundEmitterComponent() { }
}

[MemoryPackable]
public partial struct ColliderComponent
{
    public ColliderShape Shape { get; set; }
    public Vector3 Size { get; set; }
    public bool IsSolid { get; set; }
    public ColliderComponent() { }
}

[MemoryPackable]
public partial struct AcousticComponent
{
    public float Absorption { get; set; }
    public float TransmissionLow { get; set; }
    public float TransmissionMid { get; set; }
    public float TransmissionHigh { get; set; }
    public float Scattering { get; set; }
    public bool IsHollow { get; set; }
    public float ShellThickness { get; set; }
    /// <summary>
    /// Bitmask for active faces: 1=North, 2=South, 4=East, 8=West, 16=Top, 32=Bottom.
    /// Default is 63 (all faces active).
    /// </summary>
    public int FaceMask { get; set; }
    public AcousticComponent() { FaceMask = 63; }
}

[MemoryPackable]
public partial struct AcousticEnvironmentComponent
{
    public string ReverbType { get; set; } = "City";
    public float AirAbsorption { get; set; } = 0.1f;
    public string BackgroundAmbientLoop { get; set; } = "";
    public int Priority { get; set; } = 0;
    public AcousticEnvironmentComponent() { }
}

[MemoryPackable]
public partial struct PhysicsPropertyComponent
{
    public float Mass { get; set; }
    public float Friction { get; set; }
    public float Restitution { get; set; }
    public float Drag { get; set; }
    public PhysicsPropertyComponent() { }
}

[MemoryPackable]
public partial struct WorldEnvironmentComponent
{
    public float GameTime { get; set; }
    public int DayOfYear { get; set; }

    // Defaulted to a still, temperate, sea-level day. A default-constructed instance used to describe a
    // freezing near-vacuum with an air-absorption multiplier of zero, which is what the client's world
    // state started at and handed to the acoustics until the first WorldStateUpdate arrived.
    public float Temperature { get; set; } = 20.0f;
    public float Humidity { get; set; } = 0.5f;
    /// <summary>Millibars. Sea level is 1013.25.</summary>
    public float AirPressure { get; set; } = 1013.25f;
    /// <summary>Scales the air-absorption reference distance. Must be positive; 1 is no scaling.</summary>
    public float AirAbsorptionMultiplier { get; set; } = 1.0f;
    public Vector3 WindVelocity { get; set; }
    public float WindGustiness { get; set; }
    public float PrecipitationIntensity { get; set; }
    public WorldEnvironmentComponent() { }
}

[MemoryPackable]
public partial struct VehicleComponent
{
    public string VehicleType { get; set; } = "";
    public int MaxSeats { get; set; }
    public List<int> OccupantEntityIds { get; set; } = new();
    public float Speed { get; set; }
    public VehicleComponent() { }
}

[MemoryPackable]
public partial struct Velocity { public Vector3 Linear { get; set; } public Velocity() { } }

[MemoryPackable]
public partial struct BeaconComponent { public float Frequency { get; set; } public float Interval { get; set; } public float LastPulseTime { get; set; } public BeaconComponent() { } }

[MemoryPackable]
public partial struct InventoryComponent { public List<int> ItemEntityIds { get; set; } = new(); public InventoryComponent() { } }

[MemoryPackable]
public partial struct HealthComponent { public int Current { get; set; } public int Max { get; set; } public HealthComponent() { } }

[MemoryPackable]
public partial struct RegionComponent
{
    public string FriendlyName { get; set; } = "";
    public bool IsIndoor { get; set; }
    public AcousticEnvironmentType Environment { get; set; } = AcousticEnvironmentType.Atmospheric;
    public Vector3 RoomSize { get; set; }
    public float ReverbTimeScale { get; set; } = 1.0f;
    public int[] Materials { get; set; } = new int[6]; 
    public string AmbienceId { get; set; } = ""; 
    public RegionComponent() { }
}
[MemoryPackable]
public partial struct PortalComponent
{
    public int RegionAId { get; set; }
    public int RegionBId { get; set; }
    public float ApertureSize { get; set; } 
    public PortalComponent() { }
}

[MemoryPackable]
public partial struct ParentComponent
{
    public int ParentEntityId { get; set; }
    public Vector3 LocalPosition { get; set; }
    public Quaternion LocalRotation { get; set; }
    public ParentComponent() { ParentEntityId = -1; LocalPosition = Vector3.Zero; LocalRotation = Quaternion.Identity; }
}
