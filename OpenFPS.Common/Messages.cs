using MemoryPack;
using System.Numerics;
using OpenFPS.Common.Components;
using System;

namespace OpenFPS.Common.Networking;

[MemoryPackable]
[MemoryPackUnion(0, typeof(ServerStateUpdate))]
[MemoryPackUnion(1, typeof(ClientInputUpdate))]
[MemoryPackUnion(2, typeof(PlayerJoined))]
[MemoryPackUnion(3, typeof(ChatMessage))]
[MemoryPackUnion(4, typeof(LoginRequest))]
[MemoryPackUnion(5, typeof(LoginResponse))]
[MemoryPackUnion(6, typeof(TextCommand))]
[MemoryPackUnion(7, typeof(TextEvent))]
[MemoryPackUnion(8, typeof(InteractRequest))]
[MemoryPackUnion(9, typeof(VoiceData))]
[MemoryPackUnion(10, typeof(WorldStateUpdate))]
[MemoryPackUnion(11, typeof(RegisterRequest))]
[MemoryPackUnion(12, typeof(RegisterResponse))]
[MemoryPackUnion(13, typeof(LogoutRequest))]
[MemoryPackUnion(14, typeof(StatsUpdate))]
[MemoryPackUnion(15, typeof(EntityDefinition))]
[MemoryPackUnion(16, typeof(CollisionEvent))]
[MemoryPackUnion(17, typeof(MapManifest))]
[MemoryPackUnion(18, typeof(PlayerSpawned))]
[MemoryPackUnion(19, typeof(MapLoadComplete))]
[MemoryPackUnion(20, typeof(MapDataRequest))]
[MemoryPackUnion(21, typeof(PlayerListRequest))]
[MemoryPackUnion(22, typeof(PlayerListResponse))]
[MemoryPackUnion(23, typeof(FriendListRequest))]
[MemoryPackUnion(24, typeof(FriendListResponse))]
[MemoryPackUnion(25, typeof(MapPublishRequest))]
[MemoryPackUnion(26, typeof(EntityRemoved))]
public partial interface IMessage { }

public enum PlayerListScope
{
    Server,
    Map
}

[MemoryPackable]
public partial class PlayerListRequest : IMessage
{
    public PlayerListScope Scope;
    public PlayerListRequest() { }
}

[MemoryPackable]
public partial class PlayerListResponse : IMessage
{
    public string[] Players = Array.Empty<string>();
    public PlayerListResponse() { }
}

[MemoryPackable]
public partial class FriendListRequest : IMessage
{
    public FriendListRequest() { }
}

[MemoryPackable]
public partial class FriendListResponse : IMessage
{
    public string[] Friends = Array.Empty<string>();
    public FriendListResponse() { }
}

[MemoryPackable]
public partial class MapPublishRequest : IMessage
{
    public string MapName = "";
    public bool IsPublic;
    public MapPublishRequest() { }
}

[MemoryPackable]
public partial class MapManifest : IMessage
{
    public string MapName = "default";
    public string Checksum = ""; 
    public Vector3 WorldSize;
    public Vector3 MapMin = new Vector3(-50, 0, -50);
    public Vector3 MapMax = new Vector3(50, 10, 50);
    public Transform SpawnPoint;
    public float MinimumY;
    public int ExpectedEntityCount;
    public float VoxelResolution = 0.5f;
    public float OcclusionFloor = 0.2f;
    public string OwnerId = "";
    public bool IsPublic = false;

    // Atmospheric & Physics
    public float Gravity = 15.0f;
    public float Temperature = 20.0f;
    public float Humidity = 0.5f;
    public float AirPressure = 1.0f;
    public float AirAbsorptionMultiplier = 1.0f;

    public MapManifest() { }
}

[MemoryPackable]
public partial class MapDataRequest : IMessage
{
    public string MapName = "";
    public MapDataRequest() { }
}

[MemoryPackable]
public partial class MapLoadComplete : IMessage { public MapLoadComplete() { } }

/// <summary>
/// Tells a client that entities it was told about are gone — destroyed, or left its area of interest.
/// Sent reliably: a client that misses this keeps a ghost forever, because every other message about an
/// entity is additive. The client must purge the id from definitions, transforms, velocities, audio ids
/// and any voice currently playing on it.
/// </summary>
[MemoryPackable]
public partial class EntityRemoved : IMessage
{
    public List<int> EntityIds = new();
    public EntityRemoved() { }
}

[MemoryPackable]
public partial class PlayerSpawned : IMessage
{
    public int EntityId;
    public Transform SpawnTransform;
    public PlayerSpawned() { }
}

[MemoryPackable]
public partial class EntityDefinition : IMessage
{
    public int EntityId;
    public EntityType Type;
    public ColliderComponent Collider;
    public IdentityComponent Identity;
    public AcousticComponent Acoustics;
    public PhysicsPropertyComponent Physics;
    public MaterialComponent Material;
    public SoundEmitterComponent SoundEmitter;
    public RegionComponent Region;
    public PortalComponent Portal;
    public Transform Transform;

    public EntityDefinition()
    {
        Identity.Name = "";
        Identity.Description = "";
        Material.Material = "Generic";
        Material.Variant = "0";
        SoundEmitter.SoundId = "";
        Region.FriendlyName = "";
    }
}

[MemoryPackable]
public partial struct QuantizedTransform
{
    public int X; // Millimeter precision (supports +/- 2000 km world)
    public int Y;
    public int Z;
    public short QX; // Quantized Quaternion (fixed-point -1.0 to 1.0)
    public short QY;
    public short QZ;
    public short QW;

    public static QuantizedTransform FromTransform(Transform t)
    {
        return new QuantizedTransform {
            X = (int)(t.Position.X * 1000f),
            Y = (int)(t.Position.Y * 1000f),
            Z = (int)(t.Position.Z * 1000f),
            QX = (short)(t.Rotation.X * 32767f),
            QY = (short)(t.Rotation.Y * 32767f),
            QZ = (short)(t.Rotation.Z * 32767f),
            QW = (short)(t.Rotation.W * 32767f)
        };
    }

    public Transform ToTransform()
    {
        return new Transform {
            Position = new Vector3(X / 1000f, Y / 1000f, Z / 1000f),
            Rotation = Quaternion.Normalize(new Quaternion(QX / 32767f, QY / 32767f, QZ / 32767f, QW / 32767f))
        };
    }
}

[MemoryPackable]
public partial struct EntityState
{
    public int EntityId;
    public QuantizedTransform Transform;
    public Vector3 LinearVelocity;
    public BeaconData ExtraData;

    public EntityState()
    {
        EntityId = 0;
        Transform = new QuantizedTransform();
        LinearVelocity = Vector3.Zero;
        ExtraData = new BeaconData();
    }
}

/// <summary>
/// Deprecated. Retained in the protocol for wire-compatibility only.
/// Sound-emitting entities use SoundEmitterComponent (loop/oneshot/periodic modes).
/// A "beacon" is simply an entity whose SoundEmitter is enabled — no special handling required.
/// </summary>
[MemoryPackable]
public partial struct BeaconData
{
    public float Frequency;
    public float Interval;
}
[MemoryPackable]
public partial class CollisionEvent : IMessage
{
    public int EntityId;
    public Vector3 Position;
    public string Material = "Generic";
    public float ImpactForce; 
}

[MemoryPackable]
public partial class StatsUpdate : IMessage
{
    public int Health;
    public int MaxHealth;
    public string CurrentMaterial = "Generic";
    public string CurrentVariant = "0";
}

[MemoryPackable]
public partial class ServerStateUpdate : IMessage
{
    public long Tick;
    public long LastProcessedSequenceId;
    public List<EntityState> States = new();
}

[MemoryPackable]
public partial class ClientInputUpdate : IMessage
{
    public long SequenceId;
    public Vector3 MoveDirection;
    public Vector2 LookDelta; 
    public bool Jump;
    public float DeltaTime;
}

[MemoryPackable]
public partial class PlayerJoined : IMessage { public int ConnectionId; public string Username = string.Empty; }

[MemoryPackable]
public partial class ChatMessage : IMessage { public string Sender = string.Empty; public string Text = string.Empty; }

[MemoryPackable]
public partial class LoginRequest : IMessage { public string Username = string.Empty; public string Password = string.Empty; }

[MemoryPackable]
public partial class LoginResponse : IMessage
{
    public bool Success;
    public string Message = string.Empty;
    public string Username = string.Empty;
    public UserRole Role;
}

[MemoryPackable]
public partial class LogoutRequest : IMessage { }

[MemoryPackable]
public partial class RegisterRequest : IMessage { public string Username = string.Empty; public string Password = string.Empty; }

[MemoryPackable]
public partial class RegisterResponse : IMessage { public bool Success; public string Message = string.Empty; }

[MemoryPackable]
public partial class TextCommand : IMessage { public string Command = string.Empty; public string[] Args = Array.Empty<string>(); }

[MemoryPackable]
public partial class TextEvent : IMessage { public string Text = string.Empty; }

[MemoryPackable]
public partial class InteractRequest : IMessage { public string Action = string.Empty; public int? TargetEntityId; }

[MemoryPackable]
public partial class VoiceData : IMessage { public int SenderId; public byte[] OpusData = Array.Empty<byte>(); }

[MemoryPackable]
public partial class WorldStateUpdate : IMessage
{
    public float GameTime;
    public string Season = "Spring";
    
    // Physical Atmospheric State
    public float Temperature; // Celsius
    public float Humidity; // 0.0 - 1.0
    public float AirPressure; // millibars
    public float AirAbsorptionMultiplier;
    public Vector3 WindVelocity; // m/s
    public float WindGustiness; // 0.0 - 1.0
    public float PrecipitationIntensity; // 0.0 - 1.0
}
