using MemoryPack;
using System.Numerics;
using OpenFPS.Common.Components;

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
[MemoryPackUnion(38, typeof(TileStreamUpdate))]
[MemoryPackUnion(39, typeof(EntityDefinitionPack))]
// The world editor (docs/WORLD_EDITOR.md section 6).
[MemoryPackUnion(40, typeof(EditorMenu))]
[MemoryPackUnion(41, typeof(ModelUpdate))]
// The world editor, phase 2 (section 11.7): a map's settings changed live.
[MemoryPackUnion(42, typeof(MapSettingsUpdate))]
// 43 is free; 44 is the driving aids'.
[MemoryPackUnion(44, typeof(MapRoads))]
public partial interface IMessage { }

/// <summary>What choosing an item of the world editor's menu does.</summary>
public enum EditorItemKind : byte
{
    /// <summary>Nothing: choosing it says it again.</summary>
    Info = 0,
    /// <summary>Opens another menu: <see cref="EditorMenuItem.Command"/> is its path, asked for with `edit menu PATH`.</summary>
    Menu = 1,
    /// <summary>Sends <see cref="EditorMenuItem.Command"/>, the text of an /edit command, as if typed.</summary>
    Action = 2,
    /// <summary>Puts <see cref="EditorMenuItem.Command"/> on the command line for the player to finish (a number).</summary>
    Input = 3,
}

/// <summary>One item of a world editor menu.</summary>
[MemoryPackable]
public partial struct EditorMenuItem
{
    public string Label;
    public EditorItemKind Kind;
    public string Command;
    /// <summary>The menu stays open after the action, for something done again and again (a nudge).</summary>
    public bool Stay;

    public EditorMenuItem()
    {
        Label = "";
        Command = "";
    }
}

/// <summary>
/// A world editor menu, built by the server: the client only shows it (MenuStack), so it knows nothing
/// about kinds of model or their fields, and a text player is sent the same menu as numbered lines.
/// </summary>
[MemoryPackable]
public partial class EditorMenu : IMessage
{
    /// <summary>Which menu this is ("root", "selected", "model:small_machine:ac_condenser").</summary>
    public string Path = "";
    public string Title = "";
    public EditorMenuItem[] Items = Array.Empty<EditorMenuItem>();
    /// <summary>A new copy of a menu already open: it replaces that one where it stands, without being
    /// said, so a value in a label is current after it was changed. Ignored if that menu is not open.</summary>
    public bool Refresh;
    public EditorMenu() { }
}

/// <summary>
/// A model's version in use now, sent when it is changed in the world editor and to each player as
/// they arrive on a map. The client puts it into its ModelLibrary and restarts that model's voices.
/// </summary>
[MemoryPackable]
public partial class ModelUpdate : IMessage
{
    public string Kind = "";
    public string Id = "";
    public int Version;
    /// <summary>The model as ModelLibrary.SpecJson writes it.</summary>
    public string SpecJson = "";
    public ModelUpdate() { }
}

/// <summary>
/// A map's settings changed in the world editor while players are on it (docs/WORLD_EDITOR.md section
/// 11.7): its beacon rules, as MapManifest.BeaconPolicy carries them on arrival. The weather and the hour
/// a map holds travel in the sky's state as they always do. Union tag 42; append fields only.
/// </summary>
[MemoryPackable]
public partial class MapSettingsUpdate : IMessage
{
    public string MapId = "";
    /// <summary>"category=policy" pairs, as MapManifest.BeaconPolicy.</summary>
    public string[] BeaconPolicy = Array.Empty<string>();
    public MapSettingsUpdate() { }
}

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

    // The map's atmosphere, applied when the manifest lands so the world sounds right before the first
    // WorldStateUpdate. AirPressure is millibars (sea level 1013.25).
    public float Gravity = PhysicsConstants.Gravity;
    public float Temperature = 20.0f;
    public float Humidity = 0.5f;
    public float AirPressure = 1013.25f;
    public float AirAbsorptionMultiplier = 1.0f;

    /// <summary>The map's outdoor ambience bed, an ambisonic recording under ASSETS/SOUNDS (e.g.
    /// "AMBIENCE/woods_mid_day"), or empty. The map's, because outdoors is everywhere a region is not;
    /// the client ducks it by the listener's shelter rather than switching it off.</summary>
    public string AmbienceId = "";

    /// <summary>The map's beacon policies, as "category=policy" — see Beacons.ReadPolicies. A
    /// category the map does not mention is on by default and the player's to change.</summary>
    public string[] BeaconPolicy = Array.Empty<string>();

    /// <summary>Where a player can walk and drive. MapMin/MapMax are the acoustic grid's, which can
    /// be much bigger than the ground. Appended last: the manifest serialises field by field.</summary>
    public Vector3 PlayMin = new Vector3(-50, 0, -50);
    public Vector3 PlayMax = new Vector3(50, 10, 50);

    /// <summary>The side of the tiles this map is streamed in, metres; 0 for a map sent whole. On a
    /// streamed map the client is sent the tiles near it, and every definition batch after
    /// MapLoadComplete is a tile arriving (docs/WORLD_STREAMING.md). Appended last.</summary>
    public float TileMetres;

    public MapManifest() { }
}

/// <summary>
/// The map's roads, junctions, level crossings and drivable tracks (<see cref="OpenFPS.Common.RoadMapData"/>
/// as JSON), sent with the map's data before MapLoadComplete. A driver's client plans its cues from them:
/// the lane ahead, the turn, the give-way line, the speed limit, the rails. Empty for a map without.
/// </summary>
[MemoryPackable]
public partial class MapRoads : IMessage
{
    public string MapName = "";
    public string Json = "";
    public MapRoads() { }
}

[MemoryPackable]
public partial class MapDataRequest : IMessage
{
    public string MapName = "";
    /// <summary>On a streamed map, how far round the player everything is sent, and how far the coarse
    /// layer (ground, roads, building shells) is: the client's world detail setting. 0 asks for the
    /// server's default. The server clamps both (StreamRadii.Clamp). Appended last.</summary>
    public float FullDetailMetres;
    public float FarMetres;
    public MapDataRequest() { }
}

/// <summary>
/// Tiles of a streamed map that changed for this client: each tile and the detail it is at now
/// (None: dropped). Sent after the definitions and removals it describes, on the same reliable channel,
/// so by the time it arrives they have all arrived; the client then brings its acoustic map and Steam
/// Audio scene up to date with them, in the background. See docs/WORLD_STREAMING.md.
/// </summary>
[MemoryPackable]
public partial class TileStreamUpdate : IMessage
{
    public float TileMetres;
    public List<TileState> Tiles = new();
    /// <summary>Definitions sent for these tiles since the last update.</summary>
    public int Definitions;
    /// <summary>Entities taken away because their tiles went.</summary>
    public int Removed;
    public TileStreamUpdate() { }
}

[MemoryPackable]
public partial class MapLoadComplete : IMessage { public MapLoadComplete() { } }

/// <summary>
/// Many entity definitions in one reliable message, for the map load. One message each was bound by
/// round trips, not bytes: 6,408 small ones to a VPS took eight seconds.
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
/// An <see cref="EntityDefinitionBatch"/>, Brotli-compressed, as the map load and tile streamer send it.
/// A definition is about 700 bytes, mostly the same for every wall of a kind, so a batch of 256 packs
/// about sixteen to one (Magnolia's join at medium detail: 8.1 MB as batches, 0.5 MB packed, 14 ms).
/// </summary>
[MemoryPackable]
public partial class EntityDefinitionPack : IMessage
{
    /// <summary>How many definitions are in it.</summary>
    public int Count;
    /// <summary>The batch, serialised as an IMessage and compressed.</summary>
    public byte[] Brotli = Array.Empty<byte>();

    /// <summary>Unpacked, no batch is bigger than this: a pack that claims more is refused.</summary>
    public const int MaxUnpackedBytes = 32 * 1024 * 1024;

    public EntityDefinitionPack() { }

    public static EntityDefinitionPack Pack(EntityDefinitionBatch batch, int quality = 1)
    {
        byte[] raw = MemoryPackSerializer.Serialize<IMessage>(batch);
        var packed = new byte[System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        using var encoder = new System.IO.Compression.BrotliEncoder(quality, 22);
        encoder.Compress(raw, packed, out _, out int written, isFinalBlock: true);
        return new EntityDefinitionPack { Count = batch.Definitions.Count, Brotli = packed.AsSpan(0, written).ToArray() };
    }

    /// <summary>The batch it carries, or null if it is not one.</summary>
    public EntityDefinitionBatch? Unpack()
    {
        try
        {
            using var input = new System.IO.MemoryStream(Brotli);
            using var brotli = new System.IO.Compression.BrotliStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var output = new System.IO.MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxUnpackedBytes) return null;
            }
            return MemoryPackSerializer.Deserialize<IMessage>(output.ToArray()) as EntityDefinitionBatch;
        }
        catch (Exception ex) when (ex is System.IO.InvalidDataException or MemoryPackSerializationException) { return null; }
    }
}

/// <summary>
/// Entities a client was told about are gone (destroyed, or out of its area). Reliable: every other
/// message about an entity is additive, so a client that misses this keeps a ghost forever. The client
/// purges the id from definitions, transforms, velocities, audio ids and any voice playing on it.
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
    // APPEND ONLY below this line: the wire format is positional.

    /// <summary>
    /// Whether this thing can move (the server's Velocity component), which its type does not say: a
    /// car's panels are StaticObjects, and filed as static they left a car-shaped ghost in the collision
    /// grid and the acoustic scene when it drove away.
    /// </summary>
    public bool Moves;

    /// <summary>For a player, their team, or "". A change of team re-sends the definition
    /// (GameServer.SyncAudioComponent).</summary>
    public string Team = "";

    /// <summary>
    /// For a player, the root entity they sit in, or -1 on their own feet. A seated body has the
    /// vehicle's velocity, so a client not told this hears footsteps at the vehicle's speed (Cody,
    /// 2026-10-05). Getting in and out re-sends the definition (GameServer.BroadcastWorldState).
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
    /// How hard this vehicle is working its tyres, 0 to 2 with 1 the limit, in steps of about 0.008.
    /// Sent, because a client cannot tell a banked corner from a flat one from the kinematics: dividing
    /// lateral acceleration by flat-ground grip read the speedway's banked turns at 1.43 to 1.59 against
    /// a full-slide threshold of 1.45, every car in every corner a white-noise skid.
    /// </summary>
    public byte TyreDemand;

    /// <summary>
    /// Each wheel, front axle first and left before right, as WheelDynamics worked it out this tick;
    /// null for anything without wheels. Ten bytes a wheel.
    /// </summary>
    public WheelState[]? Wheels;

    /// <summary>
    /// A vehicle's horn and siren as the driver has them switched (<see cref="OpenFPS.Common.VehicleSignalBits"/>);
    /// zero for anything not a player's vehicle. Nothing a client observes says a hand is on the horn.
    /// </summary>
    public byte Signals;

    /// <summary>
    /// How a far moving thing is changing, as the server has it, for the client to carry it between states
    /// (<see cref="DistantMotion"/>): its speed's rate in millimetres a second squared, and the turn of its
    /// heading as a rotation vector in milliradians a second. Zero for anything near or steady.
    /// </summary>
    public short SpeedRate;
    public short TurnX, TurnY, TurnZ;

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
/// One wheel on the wire, quantised. An unmanaged struct, copied as its bytes (nine, padded to ten).
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
    // APPEND ONLY BELOW THIS LINE: the struct is copied as its bytes.
    /// <summary>The water under it, mm from the bottom of the road's texture (RoadWater), on a square
    /// root scale: (Water / 40)^2 mm, a hundredth of a millimetre at 4, a millimetre at 40, 40 mm at 255.</summary>
    public byte Water;

    public float LoadNewtons => LoadDaN * 10f;
    public float WaterMm => (Water / 40f) * (Water / 40f);
    public static byte EncodeWater(float mm) => (byte)Math.Clamp((int)MathF.Round(40f * MathF.Sqrt(MathF.Max(0f, float.IsFinite(mm) ? mm : 0f))), 0, 255);
    public float AngularSpeedRadPerSec => AngularSpeed / 50f;
    public float SlipRatioValue => SlipRatio / 127f;
    public float SlipAngleRad => SlipAngle / 254f;
    public float DemandFraction => Demand / 127.5f;

    public static WheelState Encode(float loadNewtons, float angularSpeed, float slipRatio, float slipAngle, byte surface, float demand,
                                    float waterMm = 0f)
        => new()
        {
            Water = EncodeWater(waterMm),
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

/// <summary>
/// A shot taken through a scope: client to server, in place of the "fire" command. It carries the aim
/// because the server's heading can be a tick or two behind the look keys, a body's width at 600 m
/// through a 12-power scope. The server takes this aim if it is within a few degrees of its own.
/// </summary>
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
    /// The composite this client is riding in, or -1 on their own feet. While set, the client neither
    /// predicts its movement (the seat's, which it cannot simulate) nor makes footsteps.
    /// </summary>
    public int RidingEntityId = -1;

    /// <summary>Whether the seat this client is in drives the thing. A driver hears the lane lines;
    /// a passenger does not need them.</summary>
    public bool RidingControls;

    /// <summary>
    /// The states as the server sends them, packed by <see cref="StatePacking"/> to about half the size.
    /// Unpacked onto <see cref="States"/> as the message is read and cleared, so nothing past the socket
    /// sees it set.
    /// </summary>
    public byte[]? Packed;

    /// <summary>Whether the server is holding this client's body where it is: frozen by the admin gun,
    /// or dead. The client neither walks nor turns while it is set.</summary>
    public bool Held;

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
    // so inserting a member renumbers every one after it, silently.

    /// <summary>The run key was held. The server applies the speed; prediction must read it the same way
    /// or every stride mispredicts.</summary>
    public bool Sprint;

    /// <summary>The horn key is down. Only a driver's counts; the server sounds the horn of the vehicle
    /// they are driving for as long as it stays down.</summary>
    public bool Horn;
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
/// What a presence notice says happened to somebody: the client plays a sound by it, which a player can
/// turn off without losing the words.
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
    /// <summary>Set when the server says somebody came, went or is away: the Sender is then the person it
    /// is about, and the Text the whole notice.</summary>
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
    /// <summary>The sender's frame count, wrapping: a listener reorders frames by it and knows when one
    /// went missing. Zero, from a client before it existed, plays frames as they come.</summary>
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
    // APPEND ONLY BELOW THIS LINE: members are serialised by position.
    /// <summary>The server's wind clock (WindField.Now) and how far the eddy pattern had travelled,
    /// metres east and north, so a gust reaches the same tree at the same moment for every client. Zero
    /// clock: a server from before it existed.</summary>
    public double WindClock;
    public double WindTravelEast;
    public double WindTravelNorth;
    /// <summary>The rain rate the precipitation is, mm/h: zero when dry or snowing (Rainfall.RateFor,
    /// worked out on the server so every client hears the same rain).</summary>
    public float RainRateMmPerHour;
    /// <summary>What is falling (PrecipitationKind), the median drop of the rain (mm; zero for the
    /// rate's own), and for hail the median stone (mm). RainRateMmPerHour is the water-equivalent rate
    /// of whatever falls.</summary>
    public int PrecipitationKind;
    public float RainMedianDropMm;
    public float HailDiameterMm;
    /// <summary>The water on this map's roads (RoadWater.Save): the texture, the running sheet and the
    /// puddles, so a client works out the same water anywhere the server does. Null from an older server.</summary>
    public float[]? RoadWater;
}
