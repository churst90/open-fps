using MemoryPack;
using System.Numerics;
using OpenFPS.Common.Components;
using System;

namespace OpenFPS.Common.Networking;

[MemoryPackable]
[MemoryPackUnion(0, typeof(ServerStateUpdate))]
[MemoryPackUnion(1, typeof(ClientInputUpdate))]
// 2 was PlayerJoined, which the server never sent. Do not reuse the number.
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
// 16 was CollisionEvent, which nothing ever sent or handled. Do not reuse the number.
[MemoryPackUnion(17, typeof(MapManifest))]
[MemoryPackUnion(18, typeof(PlayerSpawned))]
[MemoryPackUnion(19, typeof(MapLoadComplete))]
[MemoryPackUnion(20, typeof(MapDataRequest))]
[MemoryPackUnion(21, typeof(PlayerListRequest))]
[MemoryPackUnion(22, typeof(PlayerListResponse))]
[MemoryPackUnion(23, typeof(FriendListRequest))]
[MemoryPackUnion(24, typeof(FriendListResponse))]
// 25 was MapPublishRequest, which no client ever sent. Do not reuse the number.
[MemoryPackUnion(26, typeof(EntityRemoved))]
[MemoryPackUnion(27, typeof(WorldAudioEvent))]
[MemoryPackUnion(28, typeof(MapListRequest))]
[MemoryPackUnion(29, typeof(MapListResponse))]
[MemoryPackUnion(30, typeof(EntityDefinitionBatch))]
[MemoryPackUnion(31, typeof(HitConfirm))]
// 32-34 are spoken for by other work in progress. Do not take them here.
[MemoryPackUnion(35, typeof(ScopedShot))]
[MemoryPackUnion(36, typeof(InventoryRequest))]
[MemoryPackUnion(37, typeof(InventoryList))]
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
    /// <summary>The bare usernames, parallel to <see cref="Players"/> (same order), for menus that act on a person.</summary>
    public string[] Usernames = Array.Empty<string>();
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
    /// <summary>Whether each friend is connected now, parallel to <see cref="Friends"/>.</summary>
    public bool[] Online = Array.Empty<bool>();
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

    /// <summary>What the map is called in a list ("magnolia tx"); empty means say the id. Appended:
    /// messages are serialised by position.</summary>
    public string Name;

    public MapSummary()
    {
        Id = "";
        OwnerId = "";
        Name = "";
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
    public float Gravity = PhysicsConstants.Gravity;
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

    /// <summary>Where a player can walk and drive. MapMin/MapMax are the acoustic grid's, which can
    /// be much bigger than the ground. Appended last: the manifest serialises field by field.</summary>
    public Vector3 PlayMin = new Vector3(-50, 0, -50);
    public Vector3 PlayMax = new Vector3(50, 10, 50);

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
/// Many entity definitions in one reliable message, for the map load.
///
/// One message per definition made the load bound by round trips, not bytes: a reliable channel
/// keeps only so many packets in flight, and 6,408 small ones to a VPS took eight seconds. A batch
/// fills each packet instead. The client files each definition exactly as if it had come alone.
/// </summary>
[MemoryPackable]
public partial class EntityDefinitionBatch : IMessage
{
    /// <summary>How many definitions the server puts in one batch.</summary>
    public const int Size = 256;

    public List<EntityDefinition> Definitions = new();
    public EntityDefinitionBatch() { }
}

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

    /// <summary>
    /// For a player, the team they are in, or "". Carried in the definition because that is the one
    /// thing a client is told about another player besides where they are; a change of team re-sends
    /// the definition (GameServer.SyncAudioComponent). Appended last: the wire format is positional.
    /// </summary>
    public string Team = "";

    /// <summary>
    /// For a player, what they are sitting in (its root's entity id), or -1 on their own feet. A seated
    /// body moves with its seat and is given the seat's velocity, which is the vehicle's, so to a client
    /// it looks exactly like somebody running down the road: it has to be told, or it hears footsteps
    /// at the vehicle's speed (Cody, 2026-10-05: "when I'm a passenger in a car and he's driving, I hear
    /// footsteps like his footsteps"). Getting in and getting out re-send the definition
    /// (GameServer.BroadcastWorldState). Appended last: the wire format is positional.
    /// </summary>
    public int RidingEntityId = -1;

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
    /// car were sliding — measured on the speedway it comes out at 1.43 to 1.59 against a full-slide
    /// threshold of 1.45, which is every car in every corner rendering pure broadband skid noise for
    /// the length of both turns: a long white-noise tail travelling with the vehicles.
    ///
    /// One byte per dynamic entity per tick, and no numerical differentiation of an interpolated
    /// velocity, which is fragile for its own reasons.
    /// </summary>
    public byte TyreDemand;

    /// <summary>
    /// Each wheel of a vehicle, front axle first and left before right; null for anything without
    /// wheels. What the server's wheel model (WheelDynamics) worked out this tick: the load on it,
    /// its slip, how fast it turns and what it is standing on. Eight bytes a wheel.
    /// </summary>
    public WheelState[]? Wheels;

    public EntityState()
    {
        EntityId = 0;
        Transform = new QuantizedTransform();
        LinearVelocity = Vector3.Zero;
    }

    /// <summary>The demand as a fraction, 0..2.</summary>
    public float TyreDemandFraction => TyreDemand / 127.5f;

    public static byte EncodeTyreDemand(float fraction)
        => (byte)Math.Clamp((int)MathF.Round(fraction * 127.5f), 0, 255);
}

/// <summary>
/// One wheel on the wire, quantised. An unmanaged struct, copied as its eight bytes.
/// </summary>
public struct WheelState
{
    /// <summary>Normal load, decanewtons (to 655 kN).</summary>
    public ushort LoadDaN;
    /// <summary>Angular speed, 1/50 rad/s (to 655 rad/s), forward positive.</summary>
    public short AngularSpeed;
    /// <summary>Slip ratio, -1..1 in 1/127ths.</summary>
    public sbyte SlipRatio;
    /// <summary>Slip angle, -0.5..0.5 rad in 1/254ths of a radian.</summary>
    public sbyte SlipAngle;
    /// <summary>The surface under it, an index into <see cref="OpenFPS.Common.RoadSurfaces"/>.</summary>
    public byte Surface;
    /// <summary>Its share of its grip in use, 0..2 with 1 the limit, as <see cref="EntityState.TyreDemand"/>.</summary>
    public byte Demand;

    public float LoadNewtons => LoadDaN * 10f;
    public float AngularSpeedRadPerSec => AngularSpeed / 50f;
    public float SlipRatioValue => SlipRatio / 127f;
    public float SlipAngleRad => SlipAngle / 254f;
    public float DemandFraction => Demand / 127.5f;

    public static WheelState Encode(float loadNewtons, float angularSpeed, float slipRatio, float slipAngle, byte surface, float demand)
        => new()
        {
            LoadDaN = (ushort)Math.Clamp((int)MathF.Round(loadNewtons / 10f), 0, ushort.MaxValue),
            AngularSpeed = (short)Math.Clamp((int)MathF.Round(angularSpeed * 50f), short.MinValue, short.MaxValue),
            SlipRatio = (sbyte)Math.Clamp((int)MathF.Round(slipRatio * 127f), -127, 127),
            SlipAngle = (sbyte)Math.Clamp((int)MathF.Round(slipAngle * 254f), -127, 127),
            Surface = surface,
            Demand = EntityState.EncodeTyreDemand(demand),
        };
}

[MemoryPackable]
public partial class StatsUpdate : IMessage
{
    public int Health;
    public int MaxHealth;
    public string CurrentMaterial = "Generic";
    public string CurrentVariant = "0";

    // APPEND ONLY BELOW THIS LINE — members serialise positionally.

    /// <summary>The weapon in the player's hands (a <see cref="OpenFPS.Common.WeaponRegistry"/> id), or
    /// empty. The client needs it for its keys: Enter fires only a gun, and R reloads one.</summary>
    public string HeldWeaponId = "";
    /// <summary>Rounds in that weapon, or -1 with none.</summary>
    public int HeldRounds = -1;
    /// <summary>The scope on that weapon (a <see cref="OpenFPS.Common.ScopeRegistry"/> id), or empty.
    /// The client needs it for numpad star: only a scoped gun can be raised to the eye.</summary>
    public string HeldScopeId = "";
    /// <summary>The fastest the player may move on foot, metres per second, or 0 for no limit beyond the
    /// walk and the run: set while they carry a body (PhysicsConstants.CarryingSpeed). The client's own
    /// prediction needs it, or it walks ahead of the server and is pulled back every step.</summary>
    public float SpeedLimit = 0f;
}

/// <summary>
/// Your shot hit somebody: sent to the shooter alone, who hears a chime for it. Nobody else is told;
/// they heard the shot.
/// </summary>
[MemoryPackable]
public partial class HitConfirm : IMessage
{
    /// <summary>Who was hit.</summary>
    public int TargetEntityId;
    /// <summary>The hit killed them. Sent once: a body is not hit again.</summary>
    public bool Killed;
    /// <summary>The bullet struck the head. Only a flown bullet (a scoped shot) is placed on the body;
    /// a hip shot is never a head shot.</summary>
    public bool Headshot;
    public HitConfirm() { }
}

/// <summary>
/// A shot taken through a scope: client to server, in place of the "fire" command.
///
/// It carries the aim because the aim is the whole of the shot. The look keys travel as inputs that
/// the server spends a tick at a time, so when the trigger arrives the server's copy of the heading
/// can be a tick or two behind; through a 12-power scope that is a body's width at 600 m. The server
/// takes this aim if it is within a few degrees of its own, and its own if not.
/// </summary>
/// <summary>Asks for what you are carrying, as a list to choose from (the I key).</summary>
[MemoryPackable]
public partial class InventoryRequest : IMessage { }

/// <summary>
/// What you are carrying, one entry per thing: its own entity id (so one of ten rifles with the same name
/// can be picked), a spoken label ("AKM, 30 rounds"), and where it is: "right hand", "left hand",
/// "both hands" or "back".
/// </summary>
[MemoryPackable]
public partial class InventoryList : IMessage
{
    public int[] Ids = Array.Empty<int>();
    public string[] Labels = Array.Empty<string>();
    public string[] Places = Array.Empty<string>();
}

[MemoryPackable]
public partial class ScopedShot : IMessage
{
    /// <summary>The crosshair, radians, as the client's physics holds yaw and pitch (increasing pitch
    /// looks down), with the breathing sway at the moment of the shot already in it.</summary>
    public float Yaw;
    public float Pitch;
    /// <summary>The elevation turret, milliradians above the rifle's base zero.</summary>
    public float ElevationMil;
    /// <summary>The power the scope was at. Logged; it changes nothing about where the bullet goes.</summary>
    public float Magnification;
    public ScopedShot() { }
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

    /// <summary>
    /// The states, packed by <see cref="StatePacking"/> — how the server sends them, at about half the
    /// size <see cref="States"/> took. Unpacked onto the end of <see cref="States"/> as the message is
    /// read, and cleared, so nothing past the socket ever sees this field set.
    /// </summary>
    public byte[]? Packed;

    [MemoryPackOnDeserialized]
    private void UnpackStates()
    {
        if (Packed == null) return;
        States ??= new List<EntityState>();
        StatePacking.Unpack(Packed, States);
        Packed = null;
    }
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

/// <summary>
/// Who a line of chat is for. The client files and words a line by this, never by the sender's name.
/// </summary>
public enum ChatChannel : byte
{
    /// <summary>Everyone on your map — what plain typing sends.</summary>
    Map = 0,
    /// <summary>Everyone on the server: /all.</summary>
    All = 1,
    /// <summary>To you by name, or your own message to someone: /pm.</summary>
    Private = 2,
    /// <summary>The server speaking to everyone — the message of the day, an announcement.</summary>
    Server = 3,
    /// <summary>To your team only: /team chat, or /t. Appended: sent by number.</summary>
    Team = 4,
}

/// <summary>
/// What a presence notice says happened to somebody. The words are in the message's text; this is
/// what the client plays a sound by, and what lets a player turn those sounds off without losing the
/// words.
/// </summary>
public enum PresenceKind : byte
{
    /// <summary>Not a presence notice: somebody said something.</summary>
    None = 0,
    /// <summary>They logged in.</summary>
    LoggedIn = 1,
    /// <summary>They logged out, or were removed from the server.</summary>
    LoggedOut = 2,
    /// <summary>Their connection was lost.</summary>
    WentOffline = 3,
    /// <summary>They said they are away, or have done nothing for a while.</summary>
    Away = 4,
    /// <summary>They are doing things again.</summary>
    Back = 5,
}

[MemoryPackable]
public partial class ChatMessage : IMessage
{
    public string Sender = string.Empty;
    public string Text = string.Empty;
    // Appended: messages serialise positionally.
    public ChatChannel Channel;
    /// <summary>Said by an admin or moderator, so it is heard as one.</summary>
    public bool FromStaff;
    /// <summary>For a private message YOU sent: who it went to. Empty otherwise.</summary>
    public string To = string.Empty;
    /// <summary>
    /// Set when this is the server saying somebody came, went, or is away, rather than somebody
    /// talking. The Sender is then the person it is about, and the Text the whole notice.
    /// </summary>
    public PresenceKind Presence;
}

[MemoryPackable]
public partial class LoginRequest : IMessage
{
    public string Username = string.Empty;
    public string Password = string.Empty;
    // Appended: messages serialise positionally.
    /// <summary>The client's <see cref="WireContract.Hash"/>. The server refuses a network login whose
    /// contract differs from its own, because the two would misread every message after this one.</summary>
    public string Build = string.Empty;
}

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

/// <summary>
/// One 20 ms Opus frame of somebody talking. The server overwrites <see cref="SenderId"/> with who really
/// sent it and relays it unchanged to everyone on the sender's map.
/// </summary>
[MemoryPackable]
public partial class VoiceData : IMessage
{
    public int SenderId;
    public byte[] OpusData = Array.Empty<byte>();
    // APPEND ONLY BELOW THIS LINE: members are serialised by position.
    /// <summary>The sender's frame count, wrapping. A listener puts frames back in order with it, and
    /// knows a frame went missing (to rebuild or conceal) rather than simply playing the next one early.
    /// Zero means none: a client from before it existed, whose frames are played as they come.</summary>
    public ushort Sequence;
}

[MemoryPackable]
public partial class WorldStateUpdate : IMessage
{
    public float GameTime;
    
    // Physical Atmospheric State
    public float Temperature; // Celsius
    public float Humidity; // 0.0 - 1.0
    public float AirPressure; // millibars
    public float AirAbsorptionMultiplier;
    public Vector3 WindVelocity; // m/s
    public float WindGustiness; // 0.0 - 1.0
    public float PrecipitationIntensity; // 0.0 - 1.0
}
