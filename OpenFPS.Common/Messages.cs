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
[MemoryPackUnion(27, typeof(WorldAudioEvent))]
[MemoryPackUnion(28, typeof(MapListRequest))]
[MemoryPackUnion(29, typeof(MapListResponse))]
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

/// <summary>Which maps to list: everything this server will let you walk into, or only your own.</summary>
public enum MapListScope
{
    Server,
    Mine,
}

[MemoryPackable]
public partial class MapListRequest : IMessage
{
    public MapListScope Scope;
    public MapListRequest() { }
}

/// <summary>One map, as a chooser needs to know it.</summary>
[MemoryPackable]
public partial struct MapSummary
{
    public string Id;
    public string OwnerId;
    public bool IsPublic;

    /// <summary>How many players are on it right now. The one fact that decides where you go.</summary>
    public int PlayerCount;

    /// <summary>Whether this is the map you are standing on.</summary>
    public bool IsCurrent;

    public MapSummary()
    {
        Id = "";
        OwnerId = "";
    }
}

[MemoryPackable]
public partial class MapListResponse : IMessage
{
    public MapListScope Scope;
    public MapSummary[] Maps = Array.Empty<MapSummary>();
    public MapListResponse() { }
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

    // Atmospheric & Physics. The map's authored atmosphere, applied by the client the moment the
    // manifest lands so the world sounds right before the first WorldStateUpdate arrives a second later.
    // AirPressure is MILLIBARS (sea level 1013.25).
    public float Gravity = 15.0f;
    public float Temperature = 20.0f;
    public float Humidity = 0.5f;
    public float AirPressure = 1013.25f;
    public float AirAbsorptionMultiplier = 1.0f;

    /// <summary>The map's OUTDOOR ambience bed — an ambisonic recording under ASSETS/SOUNDS, e.g.
    /// "AMBIENCE/woods_mid_day". Empty for a map with no outdoor sound of its own.
    ///
    /// It belongs to the map rather than to a region because outdoors is not a region: it is everywhere
    /// a region is not. The client keeps it playing the whole time and ducks it by the listener's
    /// shelter, so walking into a building takes the world outside down rather than switching it off.</summary>
    public string AmbienceId = "";

    /// <summary>The map's beacon policies, as "category=policy" — see Beacons.ReadPolicies. A
    /// category the map does not mention is on by default and the player's to change.</summary>
    public string[] BeaconPolicy = Array.Empty<string>();

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
    /// <summary>
    /// Whether this thing can move — the server's Velocity component, which its TYPE does not say.
    ///
    /// A car's panels are StaticObjects, because they are the same walls a house is built from, and
    /// the client took the type at its word: it filed them in the static collision grid and baked
    /// them into the acoustic scene where they stood at load. Driven away, they left their ghost
    /// behind in both — a car-shaped box of glass and steel in an empty parking bay — and the car
    /// itself was nothing to walk into. Appended last: the wire format is positional.
    /// </summary>
    public bool Moves;

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

    // APPEND ONLY BELOW THIS LINE. MemoryPack writes these positionally with no names on the wire.

    /// <summary>
    /// How hard this vehicle is working its tyres, as a fraction of what they have, over the range
    /// 0 to 2 with 1.0 the limit. One byte, so the resolution is about 0.008 — far finer than the
    /// gap between singing and sliding, which is the only distinction it has to carry.
    ///
    /// It is sent rather than worked out by the listener, and that is the whole point of it. A
    /// listener can differentiate a velocity and get an acceleration, but it CANNOT tell a banked
    /// corner from a flat one: a constant-radius turn at constant speed has a purely horizontal
    /// acceleration either way, and the bank shows up in the normal load, not in the kinematics. So
    /// a client dividing lateral acceleration by flat-ground grip reads a banked oval as though every
    /// car were sliding — measured on the speedway it came out at 1.43 to 1.59 against a full-slide
    /// threshold of 1.45, which is every car in every corner rendering pure broadband skid noise for
    /// the length of both turns. Heard, correctly, as "a long white noise tail travelling with the
    /// vehicles".
    ///
    /// One byte per dynamic entity per tick, and it replaces a numerical differentiation of an
    /// interpolated velocity, which was fragile for its own reasons.
    /// </summary>
    public byte TyreDemand;

    public EntityState()
    {
        EntityId = 0;
        Transform = new QuantizedTransform();
        LinearVelocity = Vector3.Zero;
        ExtraData = new BeaconData();
    }

    /// <summary>The demand as a fraction, 0..2.</summary>
    public float TyreDemandFraction => TyreDemand / 127.5f;

    public static byte EncodeTyreDemand(float fraction)
        => (byte)Math.Clamp((int)MathF.Round(fraction * 127.5f), 0, 255);
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

    // APPEND ONLY BELOW THIS LINE — members serialise positionally.

    /// <summary>
    /// The composite this client is riding in, or -1 for standing on their own two feet.
    ///
    /// The client stops predicting its own movement while this is set, because there is nothing of
    /// its own to predict: a passenger's position belongs to the seat, and the seat belongs to
    /// something the client cannot simulate. Guessing would only produce a correction every tick.
    /// It also stops generating footsteps, which a person sitting down does not make and a person
    /// sitting down travelling at ninety miles an hour would make a great many of.
    /// </summary>
    public int RidingEntityId = -1;

    /// <summary>Whether the seat this client is in drives the thing. A driver hears the lane lines;
    /// a passenger does not need them.</summary>
    public bool RidingControls;
}

[MemoryPackable]
public partial class ClientInputUpdate : IMessage
{
    public long SequenceId;
    public Vector3 MoveDirection;
    public Vector2 LookDelta; 
    public bool Jump;
    public float DeltaTime;

    // APPEND ONLY BELOW THIS LINE. MemoryPack writes these positionally with no names on the wire,
    // so inserting a member in the middle renumbers every one after it — silently.

    /// <summary>Running rather than walking. The speed is the server's to apply; this is only the
    /// claim that the key was held, and prediction and the authority must read it the same way or
    /// every stride mispredicts.</summary>
    public bool Sprint;
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
