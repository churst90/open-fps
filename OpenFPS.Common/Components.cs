using MemoryPack;
using System.Numerics;
using System;

namespace OpenFPS.Common.Components;

/// <summary>Stored and sent by number: append only. Owner has every permission and cannot be taken
/// off the server's last owner (docs/SERVER_SECURITY.md).</summary>
public enum UserRole { Player, Dev, Admin, Moderator, Owner }
public enum EntityType { None, Player, NPC, Beacon, StaticObject, Item, Projectile, Trigger }
public enum WeatherType { Clear, Rain, Snow, Storm }
/// <summary>Terrain is a tile of ground (TerrainTileComponent); its collider is not solid, so the readers of
/// boxes pass it by, and the triangle world takes it as a heightfield. Append new shapes only.</summary>
public enum ColliderShape { Box, Sphere, Cylinder, Cone, Polygon, Terrain }

/// <summary>
/// A tile of ground (docs/GEOMETRY.md 2.3): <see cref="Posts"/> a side, <see cref="Spacing"/> apart, from
/// the entity's position less half the tile's size in x and z. Heights are whole centimetres over the
/// entity's height, so every machine turns them into the same floats. A material per cell, as an index
/// into <see cref="Materials"/>. Sent with the entity's definition and stored with its tile.
/// </summary>
[MemoryPackable]
public partial class TerrainTileComponent
{
    public int Posts;
    public float Spacing;
    public short[] HeightsCm = Array.Empty<short>();
    public byte[] Cells = Array.Empty<byte>();
    public string[] Materials = Array.Empty<string>();
    // APPEND ONLY below this line: the wire format is positional.

    /// <summary>Post to post across the tile, metres.</summary>
    [MemoryPackIgnore] public float Size => (Posts - 1) * Spacing;

    private OpenFPS.Common.Geometry.Heightfield? _field;
    private float _fieldBase = float.NaN;

    /// <summary>The heightfield for a tile whose base height is <paramref name="baseY"/>, made once.</summary>
    public OpenFPS.Common.Geometry.Heightfield Field(float baseY)
    {
        var f = _field;
        if (f != null && _fieldBase == baseY) return f;
        f = OpenFPS.Common.Geometry.Heightfield.FromCentimetres(Posts, Spacing, baseY, HeightsCm,
                                                              Cells.Length > 0 ? Cells : null, Materials.Length > 0 ? Materials : null);
        _fieldBase = baseY;
        _field = f;
        return f;
    }
}

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
    /// <summary>The team this player is in, or "" — the server's word, from teams.json. Appended last:
    /// components serialise positionally.</summary>
    public string Team { get; set; } = "";
    public PlayerComponent() { }
}


[MemoryPackable]
public partial struct NameComponent { public string Name { get; set; } = ""; public NameComponent() { } }

[MemoryPackable]
public partial struct ZoneComponent 
{ 
    public string MapId { get; set; } = ""; 
    public Vector3 Size { get; set; }
    public Vector3 MinBound { get; set; }
    public Vector3 MaxBound { get; set; }
    public float Gravity { get; set; } = PhysicsConstants.Gravity;
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

    /// <summary>Whether the client says this thing as the player walks up to it. Walls, portals and
    /// region volumes carry names too, and announcing them read a portal prefab's authoring notes aloud
    /// mid-stride. Set by the prefab's `Announce` (PrefabTemplate), true by default only for Item, NPC
    /// and Beacon.</summary>
    public bool Announce { get; set; } = false;

    // APPEND ONLY below this line: the order of a component is a network protocol (SoundEmitterComponent).

    /// <summary>The prefab this entity is an instance of, or empty if built by hand. Saving a built
    /// house back out to a template needs to know that this wall is a `concrete_wall`.</summary>
    public string PrefabId { get; set; } = "";

    /// <summary>The beacon category ("door", "item", "vehicle"...), or empty for none
    /// (<see cref="OpenFPS.Common.Beacons"/>).</summary>
    public string BeaconCategory { get; set; } = "";

    /// <summary>A person with a name of their own (a character such as Alex, not a passer-by): whatever
    /// names people says <see cref="Name"/>, where a walker is "person" or "someone".</summary>
    public bool Named { get; set; } = false;

    public IdentityComponent() { }
}

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
    /// Replay this sound every N seconds; zero is not a repeater. Any emitter's, not a PA system's: a
    /// foghorn, a station bell and a dripping tap repeat too. Unlike LoopOne it leaves a gap, and the gap
    /// is what makes an announcement a landmark you can wait for rather than a drone you stop hearing.
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

    // ── APPEND ONLY BELOW THIS LINE ─────────────────────────────────────────────────────────────
    //
    // MemoryPack serialises members positionally, in declaration order, with no names on the wire:
    // inserting a member renumbers every member after it. Offset once went in after MinDistance, a
    // stale server's emitters read back with IsSynth false and the speedway went silent with nothing
    // thrown (docs/AUDIO_GHOSTS_AND_STUTTERS.md). Appended, a member an old peer does not send reads
    // back as its default.

    /// <summary>
    /// Where the sound comes out, in the entity's own frame (x right, y up, z forward); zero is the
    /// origin. Every acoustic question is about the emission point: a vehicle's origin is its contact
    /// patch, and an occlusion probe there sat half underground, about half its samples "blocked" before
    /// any wall: six decibels down and forty off the top, permanently. Authored per emitter (a tailpipe,
    /// a chimney, a drain); a fixed height for everything would be the same mistake the other way.
    /// </summary>
    public Vector3 Offset { get; set; }

    /// <summary>
    /// How big the thing making the sound is, metres; zero is a point. Inside a source's own size the
    /// level is flat, and it falls only once the whole of it is in front of you. The gain comes down as
    /// the reference widens (<see cref="OpenFPS.Common.Loudness.Widen(float, float, float)"/>):
    /// beyond the patch an extended source and a point of the same power are identical, so setting this
    /// says how big the thing is, not that it is louder.
    /// </summary>
    public float ExtentMetres { get; set; }

    public SoundEmitterComponent() { }

    /// <summary>
    /// Whether this emitter makes sound by itself (a loop, a sequence, a synth, a repeater) rather than
    /// waiting to be fired. A method because MemoryPack serialises properties. The client processes
    /// only what this says yes to, so it must be a rule, not a list of cases: an emitter it misses has
    /// no voice, no occlusion and no log line, and a one-shot it wrongly takes retriggers endlessly.
    /// </summary>
    public readonly bool RunsOnItsOwn()
        => IsSynth
        || Mode is PlaybackMode.LoopOne or PlaybackMode.LoopFolder or PlaybackMode.Sequential
        || (RepeatIntervalSeconds > 0f && !string.IsNullOrEmpty(SoundId));

    /// <summary>
    /// Whether a synthesised source is sounding now, for what a client cannot work out for itself: a
    /// level crossing's bell rings for a train the listener may be a kilometre from. True by default,
    /// so every other emitter, and a peer that does not send it, reads as sounding.
    /// </summary>
    public bool SynthRunning { get; set; } = true;

    /// <summary>
    /// Standing at a stop that takes passengers. At a junction a bus holds its service brake; at a bus
    /// stop it sets the spring brakes, kneels and opens its doors, and its speed cannot tell the two apart.
    /// </summary>
    public bool ServingStop { get; set; }

    /// <summary>
    /// Where a vehicle's side windows are going, 0 shut to 1 fully down; shut by default. The glass gets
    /// there at its motor's pace (<see cref="OpenFPS.Common.CarWindow.Glide"/>) on the server and every
    /// client from this one number, so it is sent once per press, not every tick.
    /// </summary>
    public float WindowsOpen { get; set; }
}

[MemoryPackable]
public partial struct ColliderComponent
{
    public ColliderShape Shape { get; set; }
    public Vector3 Size { get; set; }
    public bool IsSolid { get; set; }
    public ColliderComponent() { }

    // APPEND ONLY below this line: components serialise positionally.

    /// <summary>
    /// The solid's form from the shape library (docs/GEOMETRY.md 2.6): a ramp, stairs, an arch, filling
    /// the box of <see cref="Size"/>; null for a plain box. Code that reads boxes still sees the box.
    /// </summary>
    public OpenFPS.Common.Geometry.ShapeSpec? Form { get; set; }
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

    // APPEND ONLY below this line: components serialise positionally.

    /// <summary>How the wall is built: the thickness of each of its two leaves, metres, with the rest of
    /// the box's thickness the cavity between them. Zero for a solid panel. A door's skins are its
    /// leaves. See <see cref="WallBuild"/>.</summary>
    public float LeafMetres { get; set; }

    /// <summary>The centres of the studs the two leaves are fixed to, metres; zero when they meet only
    /// at the panel's edges. See <see cref="WallBuild"/>.</summary>
    public float StudSpacingMetres { get; set; }

    public AcousticComponent() { FaceMask = 63; }
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

    // A still, temperate, sea-level day: the client's acoustics use the default until the first
    // WorldStateUpdate, and zeros would be a freezing near-vacuum with no air absorption.
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

/// <summary>
/// What a player has slung on them rather than in their hands, as entity ids: a rifle on your back is
/// the same entity it was on the floor, so dropping it makes the noise its mass and material make.
/// The limit is a mass, not a count of slots (<c>HandsService.CarryCapacityKg</c>): two rifles and a
/// crowbar is a load and six torches is not.
/// </summary>
[MemoryPackable]
public partial struct InventoryComponent { public List<int> ItemEntityIds { get; set; } = new(); public InventoryComponent() { } }

[MemoryPackable]
public partial struct HealthComponent { public int Current { get; set; } public int Max { get; set; } public HealthComponent() { } }

[MemoryPackable]
public partial struct RegionComponent
{
    public string FriendlyName { get; set; } = "";
    public bool IsIndoor { get; set; }
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

    // APPEND ONLY below this line: components serialise positionally.

    /// <summary>
    /// Where the doorway is in the world and which way it faces: the leaf's pose when shut (X across the
    /// opening, Y up). A door's transform swings with the leaf; this is the hole. An all-zero rotation
    /// means not known: a portal with no leaf, placed where it stands.
    /// </summary>
    public Vector3 OpeningCentre { get; set; }
    /// <inheritdoc cref="OpeningCentre"/>
    public Quaternion OpeningRotation { get; set; }

    public PortalComponent() { }
}

/// <summary>
/// A named group of entities that is one thing: a house, a vehicle, a market stall, a barricade
/// (docs/SERVER_NOTES.md, Composites). The members are ordinary entities whose
/// <see cref="ParentComponent"/> points here, and ParentSystem carries them with the root.
/// </summary>
[MemoryPackable]
public partial struct CompositeComponent
{
    /// <summary>The template this was placed from, or empty when it was grouped in place and not yet
    /// saved; saving it fills this in.</summary>
    public string TemplateId { get; set; }

    /// <summary>What it is called when a player walks up to it.</summary>
    public string Name { get; set; }

    /// <summary>Whether this is fixed to the world: the only difference between a house and a
    /// caravan.</summary>
    public bool Anchored { get; set; }

    // APPEND ONLY below this line: members serialise positionally.

    /// <summary>
    /// Who this belongs to, or empty for public property. It gates only taking it apart, saving it as
    /// your own, changing what it is and driving it; standing in someone's house or riding in their
    /// passenger seat is not trespass.
    /// </summary>
    public string Owner { get; set; }

    public CompositeComponent() { TemplateId = ""; Name = ""; Anchored = true; Owner = ""; }
}

[MemoryPackable]
public partial struct ParentComponent
{
    public int ParentEntityId { get; set; }
    public Vector3 LocalPosition { get; set; }
    public Quaternion LocalRotation { get; set; }
    public ParentComponent() { ParentEntityId = -1; LocalPosition = Vector3.Zero; LocalRotation = Quaternion.Identity; }
}

// ── Occupancy: getting inside a composite ───────────────────────────────────────────────────────

/// <summary>One place a person can be inside a composite, in the composite's own frame. A kitchen chair
/// and a driver's seat differ only by <see cref="Controls"/>.</summary>
[MemoryPackable]
public partial struct Seat
{
    /// <summary>What it is called when the seats are read out: "driver", "passenger", "back left".</summary>
    public string Name { get; set; }
    /// <summary>Where the occupant's FEET go, relative to the composite's origin.</summary>
    public Vector3 LocalPosition { get; set; }
    /// <summary>Which way the seat faces within the composite, radians. Forward is zero.</summary>
    public float LocalYaw { get; set; }
    /// <summary>Whether sitting here drives it.</summary>
    public bool Controls { get; set; }
    public Seat() { Name = ""; }
}

/// <summary>
/// The seats a composite has, on the root. Who sits in one is on the occupant
/// (<see cref="OccupantComponent"/>), so only one place knows.
/// </summary>
[MemoryPackable]
public partial struct OccupancyComponent
{
    public List<Seat> Seats { get; set; }
    public OccupancyComponent() { Seats = new List<Seat>(); }
}

/// <summary>
/// On a player: which composite they are inside, and which seat. The seat carries the body, but where
/// they look stays their own (restored after the carry): a passenger who navigates by ear turns their head.
/// </summary>
[MemoryPackable]
public partial struct OccupantComponent
{
    public int RootEntityId { get; set; }
    public int SeatIndex { get; set; }
    /// <summary>Whether this seat drives. Cached from the seat so the movement path need not look it up.</summary>
    public bool Controls { get; set; }
    /// <summary>Where they were standing when they got in, so getting out puts them back outside it.</summary>
    public Vector3 BoardedFrom { get; set; }
    public OccupantComponent() { RootEntityId = -1; SeatIndex = -1; }
}

/// <summary>
/// A composite a person can drive, and what its driver is asking of it now. The controls are held
/// between packets but decay with <see cref="ControlAge"/>, so a client that dies mid-corner coasts to a
/// stop. How it moves comes from <see cref="OpenFPS.Common.VehicleProfile"/>, not from here.
/// </summary>
[MemoryPackable]
public partial struct DriveComponent
{
    /// <summary>Key into <see cref="OpenFPS.Common.VehicleProfile.Presets"/>.</summary>
    public string Preset { get; set; }
    /// <summary>0..1.</summary>
    public float Throttle { get; set; }
    /// <summary>0..1.</summary>
    public float Brake { get; set; }
    /// <summary>-1..1, left negative.</summary>
    public float Steer { get; set; }
    /// <summary>Metres per second along the heading. Negative is reversing.</summary>
    public float Speed { get; set; }
    /// <summary>Radians. The way the nose points.</summary>
    public float Heading { get; set; }
    /// <summary>Seconds since the driver last said anything. Held controls decay once this grows.</summary>
    public float ControlAge { get; set; }
    /// <summary>Where the driver's hands are asking the wheel to go, -1..1. <see cref="Steer"/> follows
    /// at the speed hands turn a wheel, so a key does not throw it to full lock.</summary>
    public float SteerTarget { get; set; }
    /// <summary>How much of the tyres' grip this tick asked for, before the friction circle cut it
    /// back; over 1 is sliding. The same number traffic reports, and what the client squeals on.</summary>
    public float TyreDemand { get; set; }
    /// <summary>Whether the ignition is on. A parked car's is not: it starts with the key.</summary>
    public bool EngineOn { get; set; }
    /// <summary>Seconds since the key was turned: the engine cranks for the first second or so, and the
    /// client hears the cranking the server is waiting out.</summary>
    public float EngineOnFor { get; set; }
    public DriveComponent() { Preset = ""; }
}

/// <summary>
/// Marks the region a composite derived for itself, so a rebuild replaces what it made last time and
/// never what a person authored. The room is a part at the middle of the enclosed space, not at the
/// origin, which is where the composite meets the ground: there its ceiling would be at your knees.
/// </summary>
[MemoryPackable]
public partial struct DerivedRoomComponent
{
    public DerivedRoomComponent() { }
}

/// <summary>
/// A door: a part that swings or slides out of its own doorway. Its kind (<see cref="Kind"/>) comes with
/// its behaviour in data: <see cref="Slides"/>, <see cref="Powered"/>, <see cref="SensorMetres"/>,
/// <see cref="CloseAfterSeconds"/>. The leaf is always solid; opening moves it, and a
/// <see cref="PortalComponent"/> on the same part opens with it, so the room beyond is heard gradually.
/// </summary>
[MemoryPackable]
public partial struct DoorComponent
{
    /// <summary>0 is shut, 1 is as far as it goes.</summary>
    public float Openness { get; set; }

    /// <summary>What it is swinging toward; <see cref="Openness"/> follows over the swing.</summary>
    public float Target { get; set; }

    /// <summary>How long the full swing takes, seconds.</summary>
    public float SwingSeconds { get; set; }

    /// <summary>How far it opens, radians. A quarter turn for nearly everything.</summary>
    public float SwingRadians { get; set; }

    /// <summary>Which edge it is hinged on: -1 left, +1 right. It decides which side of the doorway the
    /// leaf blocks, and which side an open door is heard on.</summary>
    public float HingeSide { get; set; }

    /// <summary>How wide the opening is with the leaf out of the way, metres: the leaf's own width,
    /// taken at capture.</summary>
    public float Aperture { get; set; }

    /// <summary>Where the leaf sits when shut, in whatever frame it lives in — parent-local for a
    /// door in a composite, world for one standing on its own.</summary>
    public Vector3 ShutPosition { get; set; }

    /// <summary>...and which way it faces when shut, in that same frame.</summary>
    public float ShutYaw { get; set; }

    /// <summary>Whether the shut pose has been taken: a door records it the first time it is looked at,
    /// so a door placed anywhere is shut where it was put.</summary>
    public bool Captured { get; set; }

    /// <summary>For a hollow door, the thickness of each of its two skins, metres; zero for a solid
    /// leaf. A steel door is sheet over a core; reckoned as a solid slab it weighs tonnes.</summary>
    public float SkinMetres { get; set; }

    // ── What kind of door it is, and how it moves (docs/DOOR_TYPES_EVENTS.md). APPEND ONLY:
    // components serialise positionally, so new fields go at the end.

    /// <summary>The hardware: a <see cref="OpenFPS.Common.DoorKind"/> as an int. 0 is a hinged door
    /// with a knob or lever, which is what every door saved before kinds existed was.</summary>
    public int Kind { get; set; }

    /// <summary>The leaf slides along its own width instead of swinging. <see cref="HingeSide"/> is
    /// then the way it slides: +1 toward the leaf's own +X, -1 the other way.</summary>
    public bool Slides { get; set; }

    /// <summary>Moved by a motor (an automatic door, a lift's doors). Nobody opens it by hand, and it
    /// reverses for anyone in the doorway while it is closing.</summary>
    public bool Powered { get; set; }

    /// <summary>Opens by itself when anyone is this close in front of it, either side, metres. 0 for
    /// a door with no sensor.</summary>
    public float SensorMetres { get; set; }

    /// <summary>Closes by itself once the doorway has been clear this long with the door fully open,
    /// seconds: a door closer, or an automatic door's hold-open time. 0 stays where it is left.</summary>
    public float CloseAfterSeconds { get; set; }

    /// <summary>How long closing by itself takes from fully open, seconds: the closer's sweep, or the
    /// motor's closing speed. A swinging door's closer runs the last part at
    /// <see cref="SwingSeconds"/> to snap the latch.</summary>
    public float CloseSeconds { get; set; }

    /// <summary>Which side needs a key to open it: +1 the leaf's own +Z side, -1 the other, 0 neither.
    /// A building's front door is keyed outside and opened by its push bar inside.</summary>
    public float KeyedSide { get; set; }

    /// <summary>Running state: how long the doorway has been clear with the door fully open, seconds.</summary>
    public float ClearSeconds { get; set; }

    /// <summary>Running state: it is closing by itself (closer or motor), not by somebody's hand.</summary>
    public bool SelfClosing { get; set; }

    /// <summary>Running state: which way it was moving last tick: +1 opening, -1 closing, 0 still.</summary>
    public int Travel { get; set; }

    /// <summary>Running state: the last opening was from the keyed side, so a key was turned.</summary>
    public bool KeyTurned { get; set; }

    // ── Push and pull, the key and who has hold of it (docs/DOOR_TYPES_EVENTS.md). Appended.

    /// <summary>
    /// Which face of a hinged leaf you push it open from: +1 its own +Z face (its outside), -1 the other;
    /// from the other face it is pulled. 0, a door saved before sides existed, is +1. A room door is
    /// pushed from outside (+1); an exit door from inside (-1), where its push bar is.
    /// </summary>
    public float PushSide { get; set; }

    /// <summary>Running state: how the hand that opened it last got it moving: +1 pushed, -1 pulled,
    /// 0 not by hand or not known (a sliding leaf, the lift).</summary>
    public int OpenedFrom { get; set; }

    /// <summary>Running state: whose hand is moving it, as the entity id plus one; 0 nobody's (shut,
    /// still, or on its closer or motor). That person walks with the leaf, so it never stops against them.</summary>
    public int HandId { get; set; }

    /// <summary>Running state: seconds left of the key going into the lock and turning, during which
    /// the leaf has not started to move. 0 when no key is being used.</summary>
    public float KeySeconds { get; set; }

    public DoorComponent()
    {
        SwingSeconds = 0.9f;
        SwingRadians = MathF.PI / 2f;
        HingeSide = 1f;
        PushSide = 1f;
    }
}

// ── Holding things ──────────────────────────────────────────────────────────────────────────────
//
// An item in your hands is the same entity as one on the ground, with its mass and material, so
// dropping it makes the noise they make meeting that floor.

/// <summary>Something that can be picked up, carried and put down.</summary>
[MemoryPackable]
public partial struct ItemComponent
{
    /// <summary>What it weighs: what you hear when it lands, and what you carry.</summary>
    public float MassKg { get; set; }

    /// <summary>How many hands it takes, one or two. A rifle takes both, so a rifle and a torch is a
    /// decision made in the moment, not a list to scroll.</summary>
    public int Hands { get; set; }

    /// <summary>The weapon this is, or empty: a key into <see cref="OpenFPS.Common.WeaponRegistry"/>.</summary>
    public string WeaponId { get; set; }

    public ItemComponent() { MassKg = 1f; Hands = 1; WeaponId = ""; }
}

/// <summary>What a player has hold of. A two-handed thing is in both slots, the same id twice, so "is a
/// hand free" is one question with one answer.</summary>
[MemoryPackable]
public partial struct HandsComponent
{
    public int RightEntityId { get; set; }
    public int LeftEntityId { get; set; }
    public HandsComponent() { RightEntityId = -1; LeftEntityId = -1; }
}

/// <summary>
/// On the item: who has it, in hand or slung, and whether it fills both their hands. Nobody can take a
/// thing while this is set. Which of the two is answered by <see cref="HandsComponent"/> and
/// <see cref="InventoryComponent"/>.
/// </summary>
[MemoryPackable]
public partial struct HeldComponent
{
    public int HolderEntityId { get; set; }
    public bool BothHands { get; set; }
    public HeldComponent() { HolderEntityId = -1; }
}

/// <summary>
/// People in a place who react to what happens in front of them: a source with a position and a size,
/// not an ambience bed. It carries a head count, not a volume: independent voices sum in power, so
/// twice the crowd is three decibels louder (<see cref="Applause"/>). It reacts rather than loops; a
/// loop of applause is heard as one within seconds.
/// </summary>
[MemoryPackable]
public partial struct CrowdComponent
{
    /// <summary>How many people. Decides the level, through a ten-log and not a twenty-log.</summary>
    public int People { get; set; }

    /// <summary>How close something has to come before they react to it, metres.</summary>
    public float ReactRadiusMetres { get; set; }

    /// <summary>Seconds before they will react again, so a field of thirty cars is one reaction and
    /// not thirty.</summary>
    public float CooldownSeconds { get; set; }

    public CrowdComponent()
    {
        People = 200;
        ReactRadiusMetres = 60f;
        CooldownSeconds = 6f;
    }
}

// ── Weapons and wounds ──────────────────────────────────────────────────────────────────────────
//
// Server-side state, never sent: a client learns what it needs about its own gun from StatsUpdate,
// and about everybody else's from the sounds the guns make.

/// <summary>The rounds in a weapon, on the weapon's own entity: whoever picks up a loaded rifle gets its
/// rounds.</summary>
[MemoryPackable]
public partial struct AmmoComponent
{
    /// <summary>Rounds in it now: the magazine, the tube or the cylinder.</summary>
    public int Rounds { get; set; }
    /// <summary>Rounds it holds full.</summary>
    public int Capacity { get; set; }
    /// <summary>Spare rounds that came with it, in the magazines lying beside it. The first person to
    /// pick it up pockets them (their reserve), and then there are none.</summary>
    public int SpareRounds { get; set; }
    public AmmoComponent() { }
}

/// <summary>
/// The ammunition a person carries that is not in a gun, by kind (<see cref="OpenFPS.Common.Ammunition"/>
/// ids). Kept per kind and not per gun: two rifles in the same calibre draw on the same rounds.
/// </summary>
[MemoryPackable]
public partial struct AmmoReserveComponent
{
    public Dictionary<string, int> Rounds { get; set; } = new();
    public AmmoReserveComponent() { }
}

/// <summary>Somebody who has been killed, and when. A player gets up again at the spawn later; a person
/// in the street is taken away and somebody else comes along.</summary>
[MemoryPackable]
public partial struct DeadComponent
{
    /// <summary>When they died, seconds on the server's clock.</summary>
    public double DiedAt { get; set; }
    public DeadComponent() { }
}
