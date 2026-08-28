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
