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

    // APPEND ONLY below this line — see SoundEmitterComponent for why the order of a component is a
    // network protocol.

    /// <summary>
    /// The prefab this entity is an instance OF, or empty if it was built by hand.
    ///
    /// Identity in the literal sense: what kind of thing this is, as opposed to what it is called.
    /// Nothing recorded it before, and the cost of that only becomes visible when you try to go the
    /// other way — saving a house somebody built back out to a template needs to know that this wall
    /// is a `concrete_wall`, and an entity that cannot say so cannot be rebuilt.
    /// </summary>
    public string PrefabId { get; set; } = "";

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

    // ── APPEND ONLY BELOW THIS LINE ─────────────────────────────────────────────────────────────
    //
    // MemoryPack serialises these members POSITIONALLY, in declaration order, with no names on the
    // wire. That makes the order of this struct a network protocol: inserting a member in the middle
    // does not add a field, it renumbers every field after it.
    //
    // Which is not a hypothetical. Offset went in after MinDistance, and against a server that had
    // not been restarted the client read a Vector3 where a float had been written, lost twelve bytes
    // of alignment, and came out the other side with IsSynth false on every vehicle in the world.
    // Thirty cars turned into thirty attempts to play a sample called "engine:nascar_v8", and the
    // speedway went completely silent. Nothing threw; the numbers were simply the wrong numbers.
    //
    // Appended, the worst an out-of-date peer can do is not send it, and a member nobody sent reads
    // back as its default — which for an emitter offset is the origin, exactly the old behaviour.

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

    public SoundEmitterComponent() { }

    /// <summary>
    /// Does this emitter make sound BY ITSELF, or only when something triggers it?
    ///
    /// A method rather than a property on purpose: MemoryPack serialises properties, and this is a
    /// question ABOUT the data, not part of it. See the APPEND ONLY note above.
    ///
    /// The client registers an entity for per-frame audio processing on the strength of this, and it
    /// used to ask a narrower question — literally "is the mode LoopOne, or is it a synth" — which is
    /// a list of two cases rather than a rule. Anything else was never registered and so was never
    /// processed at all: no voice, no occlusion, no reverb, no log line. A public-address horn set to
    /// play a single announcement every twenty seconds was simply not in the world as far as the
    /// audio system was concerned, and the repeat logic written to serve it could never run. So were
    /// Sequential and LoopFolder emitters, which nothing had happened to author yet.
    ///
    /// The rule is about RESPONSIBILITY. A looping, folder-looping, sequential or synthesised emitter
    /// is producing sound continuously; a repeater is producing it on a schedule of its own. All of
    /// those own their own voice and have to be looked at every frame. A plain Single with no repeat
    /// interval is a one-shot waiting for something to fire it — a door, a footstep, a gunshot — and
    /// registering one would make it retrigger endlessly, which is the fault the old narrow test was
    /// really guarding against.
    /// </summary>
    public readonly bool RunsOnItsOwn()
        => IsSynth
        || Mode is PlaybackMode.LoopOne or PlaybackMode.LoopFolder or PlaybackMode.Sequential
        || (RepeatIntervalSeconds > 0f && !string.IsNullOrEmpty(SoundId));
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

/// <summary>
/// What a player has slung on them rather than in their hands.
///
/// ENTITY IDS, and that was right from the first day it was written: a rifle on your back is the
/// same entity the rifle on the floor was, still with a mass, a material and a position — it just
/// happens to be parented to you at shoulder height. Which is why dropping it makes the noise that
/// mass and that material make meeting that floor, from the height it actually fell from, through a
/// calculation that already existed and knows nothing about rifles. A list of item NAMES would have
/// needed every bit of that inventing again, and inventing it worse.
///
/// The limit on this is a MASS and not a number of slots (see <c>HandsService.CarryCapacityKg</c>),
/// because two rifles and a crowbar is a load and six torches is not, and a count cannot tell those
/// apart.
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

/// <summary>
/// A named group of entities that is ONE THING: a house, a vehicle, a market stall, a barricade.
///
/// The realisation this exists to act on is that a map, a house, a car and a thing somebody invented
/// from scratch are the same idea at four scales. All of them are "a set of entities with a local
/// origin, which can be saved, placed again, owned, and entered". Building that once means placing a
/// house and driving a car stop being separate features.
///
/// It carries almost nothing, because almost nothing is needed: the members are ordinary entities
/// wearing a <see cref="ParentComponent"/> that points here, and ParentSystem — which has existed and
/// run every tick all along — already carries them with the root. A composite that never moves and a
/// composite you can drive away differ only in whether anything is allowed to move the root.
/// </summary>
[MemoryPackable]
public partial struct CompositeComponent
{
    /// <summary>The template this was placed from, or empty when it was grouped in place and has not
    /// been saved as anything. Saving it later fills this in; that is the whole of "build it out of
    /// parts, then classify it as an object".</summary>
    public string TemplateId { get; set; }

    /// <summary>What it is called when a player walks up to it.</summary>
    public string Name { get; set; }

    /// <summary>
    /// Whether this is fixed to the world.
    ///
    /// The ONLY difference between a house and a caravan, and it is deliberately not a difference of
    /// kind. A house is anchored because houses are; unanchor the same set of walls and it is
    /// something you can tow. Nothing else in the model changes.
    /// </summary>
    public bool Anchored { get; set; }

    // APPEND ONLY BELOW THIS LINE — members serialise positionally; inserting one renumbers the rest.

    /// <summary>
    /// Who this belongs to, or empty for public property.
    ///
    /// Recorded by whoever grouped or placed it. What it gates is deliberately narrow: taking a thing
    /// APART, saving it out as your own, changing what it is, and driving it. Standing in someone
    /// else's house, or riding in their passenger seat, is not trespass — it is how a world with
    /// other people in it works.
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

// ── Occupancy ───────────────────────────────────────────────────────────────────────────────────
//
// Getting INSIDE a composite, which is the last of the four things a composite is for: a set of
// entities with a local origin, which can be saved, placed again, owned, and ENTERED.
//
// A seat is where in a composite's own frame a person sits and which way they face, and whether
// sitting there drives the thing. That last flag is the whole of the difference between a kitchen
// chair and a driver's seat — not a difference of kind, the same as a house and a caravan differ
// only by <see cref="CompositeComponent.Anchored"/>.

/// <summary>One place a person can be inside a composite, in the composite's OWN frame.</summary>
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
/// The seats a composite has. Carried by the ROOT, because a seat is a property of the thing, not of
/// whoever happens to be in it — who is in it is on the occupant (see <see cref="OccupantComponent"/>),
/// so there is exactly one place that knows, and nothing to keep in step.
/// </summary>
[MemoryPackable]
public partial struct OccupancyComponent
{
    public List<Seat> Seats { get; set; }
    public OccupancyComponent() { Seats = new List<Seat>(); }
}

/// <summary>
/// On a PLAYER: which composite they are inside, and which seat.
///
/// While this is worn, the body's position is not its own — it belongs to the seat, and the root
/// carries it. What stays the player's own is where they are LOOKING, which is why the occupant's
/// rotation is restored after the carry rather than being taken from the seat: a passenger can turn
/// their head, and for a player who navigates by ear that is most of what a passenger does.
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
/// A composite that a person can drive, and what its driver is asking of it right now.
///
/// The controls are held, not sampled: a driver who stops sending packets for a moment does not lift
/// off, because a real one would not. They do decay — <see cref="ControlAge"/> — so a client that
/// dies mid-corner coasts to a stop instead of driving away forever.
///
/// Everything about HOW it then moves comes out of <see cref="OpenFPS.Common.VehicleProfile"/>: the
/// engine's torque through the gearbox for what it pulls, the tyres' peak grip for what it can
/// corner and brake at, the mass and drag area for what it cannot. There are no handling numbers
/// here, because a car's handling is not a property of the act of driving.
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
    public DriveComponent() { Preset = ""; }
}

/// <summary>
/// Marks the region entity a composite DERIVED for itself, rather than one somebody authored.
///
/// A composite that encloses space grows a room: an ordinary entity, parented to the root like every
/// other part, carrying the <see cref="RegionComponent"/> and sitting at the middle of the space
/// rather than at the origin — because a composite's origin is where it meets the GROUND, and a
/// room's centre is half its height above that. Putting the volume at the origin would leave its
/// ceiling at your knees.
///
/// Doing it as a part rather than as a component on the root means ParentSystem carries it with the
/// thing for free, and it is exactly what a person authoring a building by hand is already told to do
/// (see the `building_box` prefab: "place an acoustic_region inside it if the interior is
/// enterable"). The marker is what tells the derived one apart from the authored one, so a rebuild
/// replaces what it produced last time and never touches what a person put there.
/// </summary>
[MemoryPackable]
public partial struct DerivedRoomComponent
{
    public DerivedRoomComponent() { }
}

/// <summary>
/// A door: a part that swings out of its own doorway.
///
/// It is an ordinary part of a composite — a leaf like a wall is a leaf — and everything that makes
/// it a door rather than a wall is here. Two things happen when it opens, and only one of them is
/// the obvious one.
///
/// The leaf SWINGS ASIDE. It is always solid; what changes is where it is. A door that went
/// non-solid to let you through would be a door you could walk through while it was shut and standing
/// in front of you, and one whose open leaf was not in the way of anything — which is wrong twice.
///
/// And the OPENING appears. A <see cref="PortalComponent"/> on the same part has its aperture driven
/// by how far the leaf has swung, so the room beyond opens up gradually as it moves. That is the half
/// that matters to somebody listening: a door is not a thing you hear, it is a thing that changes
/// what you can hear through it, and the existing portal machinery already knows how to do that. No
/// new acoustics were needed, only something to move the number.
/// </summary>
[MemoryPackable]
public partial struct DoorComponent
{
    /// <summary>0 is shut, 1 is as far as it goes.</summary>
    public float Openness { get; set; }

    /// <summary>What it is swinging toward. The difference between this and <see cref="Openness"/> is
    /// what makes a door take a moment rather than teleporting between two states.</summary>
    public float Target { get; set; }

    /// <summary>How long the full swing takes, seconds.</summary>
    public float SwingSeconds { get; set; }

    /// <summary>How far it opens, radians. A quarter turn for nearly everything.</summary>
    public float SwingRadians { get; set; }

    /// <summary>
    /// Which edge it is hinged on: -1 for the left edge, +1 for the right.
    ///
    /// Not cosmetic. It decides which way the leaf sweeps, and therefore which side of the doorway is
    /// blocked while it is moving — and for somebody who navigates by ear, an open door heard on your
    /// left is a different piece of information from one heard on your right.
    /// </summary>
    public float HingeSide { get; set; }

    /// <summary>How wide the opening is when the leaf is out of the way, metres. Derived from the
    /// leaf itself at capture: a door makes a hole exactly its own size.</summary>
    public float Aperture { get; set; }

    /// <summary>Where the leaf sits when shut, in whatever frame it lives in — parent-local for a
    /// door in a composite, world for one standing on its own.</summary>
    public Vector3 ShutPosition { get; set; }

    /// <summary>...and which way it faces when shut, in that same frame.</summary>
    public float ShutYaw { get; set; }

    /// <summary>Whether the shut pose above has been taken yet. A door records where "shut" is the
    /// first time it is looked at, so a door placed anywhere by anything is shut where it was put.</summary>
    public bool Captured { get; set; }

    public DoorComponent()
    {
        SwingSeconds = 0.9f;
        SwingRadians = MathF.PI / 2f;
        HingeSide = 1f;
    }
}

// ── Holding things ──────────────────────────────────────────────────────────────────────────────
//
// An item in your hands is the SAME ENTITY as one on the ground. That is already what
// InventoryComponent says it believes, and it is the right belief: a rifle you are carrying has a
// material, a mass and a position, and dropping it should make the noise that mass and that material
// make when they meet that floor — which is a calculation that already exists and knows nothing about
// rifles. Items as rows in a table would need all of that inventing again, wrongly.

/// <summary>
/// Something that can be picked up, carried and put down.
/// </summary>
[MemoryPackable]
public partial struct ItemComponent
{
    /// <summary>What it weighs. Not bookkeeping: it is what you hear when it lands, and what makes
    /// carrying a thing different from not carrying it.</summary>
    public float MassKg { get; set; }

    /// <summary>
    /// How many hands it takes. One or two.
    ///
    /// The constraint that makes an inventory a spatial thing you reason about by ear rather than a
    /// menu. A rifle takes both hands, so a rifle and a torch is a decision — and a decision a
    /// player has to make out loud, in the moment, is worth more than a list they can scroll.
    /// </summary>
    public int Hands { get; set; }

    /// <summary>The weapon this IS, or empty. A key into <see cref="OpenFPS.Common.WeaponRegistry"/>,
    /// so a thing you are holding can be fired without anything knowing what a weapon is.</summary>
    public string WeaponId { get; set; }

    public ItemComponent() { MassKg = 1f; Hands = 1; WeaponId = ""; }
}

/// <summary>
/// What a player has hold of.
///
/// Two slots, and something needing both is recorded in BOTH of them — the same entity id twice.
/// That way "have I a hand free" is one question with one answer, rather than a rule about a flag
/// that some code remembers to check and some does not.
/// </summary>
[MemoryPackable]
public partial struct HandsComponent
{
    public int RightEntityId { get; set; }
    public int LeftEntityId { get; set; }
    public HandsComponent() { RightEntityId = -1; LeftEntityId = -1; }
}

/// <summary>
/// On the item: who has it, and whether it is filling both their hands.
///
/// "Has it" and not "is holding it" — this is on a thing slung on a back exactly as it is on a thing
/// in a fist, and it is the one question anything reaching for an item needs to ask: nobody can lift
/// a thing off the floor, or off your shoulder, while this is set. WHICH of the two it is gets
/// answered by <see cref="HandsComponent"/> and <see cref="InventoryComponent"/>, each of which
/// names the ids it owns — so there are three places a thing can be, and each is one question with
/// one answer rather than a flag some code remembers to check.
/// </summary>
[MemoryPackable]
public partial struct HeldComponent
{
    public int HolderEntityId { get; set; }
    public bool BothHands { get; set; }
    public HeldComponent() { HolderEntityId = -1; }
}
