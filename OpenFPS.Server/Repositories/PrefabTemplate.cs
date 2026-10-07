using System.Numerics;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// The prefab format, and the spec of it: every key a prefab file may carry is a property here, and
/// <see cref="PrefabValidator"/> rejects values that cannot make a coherent entity.
/// `prefabs/prefab-schema.json` and `docs/AUTHORING.md` are written against this type; keep them in step.
///
/// Every field is optional except <see cref="Id"/> and <see cref="Name"/>; an omitted field means "do not
/// attach this behaviour", not "attach it with a zero". <see cref="PrefabRepository.Spawn"/> is the only
/// reader, and each block below names the component it produces.
/// </summary>
public class PrefabTemplate
{
    // --- Identity ---------------------------------------------------------------------------------

    /// <summary>Unique id, and the name a map's `PrefabId` refers to. Should match the file name.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Spoken name of the entity (NameComponent and IdentityComponent.Name). Required: by ear it
    /// is the only way to refer to the thing.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Longer description, read out on examine. Becomes IdentityComponent.Description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Whether the client speaks this entity's name (and description) as the player walks within
    /// interaction range. Omitted, it follows <see cref="Type"/>: true for Item, NPC and Beacon, false
    /// for StaticObject, Trigger and Projectile, so portals and regions are not read out mid-step. Set it
    /// false on a landmark you do not want narrated, or true on architecture that should be.</summary>
    public bool? Announce { get; set; }

    /// <summary>EntityType by NAME ("StaticObject", "Beacon", "Item", "NPC", ...). Defaults to StaticObject.</summary>
    public EntityType Type { get; set; } = EntityType.StaticObject;

    /// <summary>Surface material by name — one of <see cref="OpenFPS.Common.AcousticRegistry"/>'s materials.
    /// Drives footstep sounds and the per-band acoustic properties of this surface. "None" for something
    /// that is not a surface (an emitter, a region, a portal).</summary>
    public string Material { get; set; } = "None";

    // --- Collider (ColliderComponent) --------------------------------------------------------------

    /// <summary>Full extents in metres, scaled by the map entity's `Scale`. Omit for something with no
    /// physical presence. Every component must be &gt; 0.</summary>
    public Vector3? ColliderSize { get; set; }

    /// <summary>Collider shape by NAME ("Box", "Sphere", "Cylinder", "Cone", "Polygon"). Defaults to Box.
    /// For the round shapes, ColliderSize.X is the diameter and .Y the height.
    ///
    /// Read the caveat in docs/AUTHORING.md before using a non-Box shape on solid geometry: movement
    /// collides against the AABB whatever the shape, and Steam Audio's scene is built from BOX colliders
    /// only, so a solid sphere is walked around but not *heard* as an obstruction.</summary>
    public ColliderShape? Shape { get; set; }

    /// <summary>A solid form from the shape library filling the collider's box (docs/GEOMETRY.md 4.1):
    /// { "Kind": "Wedge" } for a ramp rising toward +Z, { "Kind": "Stairs", "Steps": 14 } for a flight
    /// climbing toward +Z, { "Kind": "Arch", "Thickness": 0.5 }. Omit for a box. Named Form because
    /// <see cref="Shape"/> already names the collider's round or square shape.</summary>
    public OpenFPS.Common.Geometry.ShapeSpec? Form { get; set; }

    /// <summary>Whether the collider blocks movement. Defaults to true when a ColliderSize is given.
    /// A region volume or a portal must set this false.</summary>
    public bool? IsSolid { get; set; }

    // --- Health / item ----------------------------------------------------------------------------

    /// <summary>Spawns a HealthComponent at full health. Omit for something that cannot be damaged.</summary>
    public int? MaxHealth { get; set; }

    /// <summary>Spawns a <see cref="CrowdComponent"/>: how many people are here. Omit for anything
    /// that is not a crowd. Their level follows from the count — a crowd twice the size is three
    /// decibels louder, because independent sources add in power.</summary>
    public int? CrowdPeople { get; set; }

    /// <summary>How close something must come before the crowd reacts, metres. Only meaningful with
    /// <see cref="CrowdPeople"/>.</summary>
    public float? CrowdReactRadiusMetres { get; set; }

    /// <summary>Declares this an item: something that can be picked up, carried and put down. Must
    /// agree with <see cref="Type"/> = Item.</summary>
    public bool IsItem { get; set; }

    /// <summary>What it weighs, kilograms. Not bookkeeping — it is what you hear when it lands.</summary>
    public float? ItemWeight { get; set; }

    /// <summary>How many hands it takes: one or two. Defaults to one.</summary>
    public int? Hands { get; set; }

    /// <summary>The weapon this is, if it is one: a key into the weapon registry.</summary>
    public string? WeaponId { get; set; }

    /// <summary>A premium item: hard to come by, so only somebody with the give-premium permission can
    /// give one (the teleporter). Ordinary items are given by developers. Only meaningful on an item.</summary>
    public bool Premium { get; set; }

    // --- Acoustics (AcousticComponent) -------------------------------------------------------------

    /// <summary>Fraction of low/mid/high-band energy that passes THROUGH this surface, 0..1.
    /// Any one of them attaches an AcousticComponent; the others default to 1 (fully transparent).</summary>
    public float? TransmissionLow { get; set; }
    /// <inheritdoc cref="TransmissionLow"/>
    public float? TransmissionMid { get; set; }
    /// <inheritdoc cref="TransmissionLow"/>
    public float? TransmissionHigh { get; set; }

    /// <summary>Fraction of energy absorbed on reflection, 0..1 (0 = a perfect mirror).</summary>
    public float? Absorption { get; set; }

    /// <summary>Fraction of reflected energy scattered diffusely, 0..1.</summary>
    public float? Scattering { get; set; }

    /// <summary>Wall thickness in metres for a hollow shell; &gt; 0 marks the collider hollow.</summary>
    public float? ShellThickness { get; set; }

    /// <summary>A wall built as two leaves of its material over a cavity: each leaf's thickness, metres
    /// (0.0125 for plasterboard). The rest of the box's thickness is the cavity. Absent for a solid
    /// wall. Decides how much gets through it, band by band (WallTransmission).</summary>
    public float? LeafMetres { get; set; }

    /// <summary>The centres of the studs a two-leaf wall's leaves are fixed to, metres (0.6 for
    /// standard framing). Absent when the leaves meet only at the edges of the panel.</summary>
    public float? StudSpacingMetres { get; set; }

    /// <summary>Which faces exist, as a bit mask: North 1, South 2, East 4, West 8, Top 16, Bottom 32
    /// (63 = closed box). Mutually exclusive with <see cref="MissingFaces"/> — set one or the other.</summary>
    public int? FaceMask { get; set; }

    /// <summary>The readable form of <see cref="FaceMask"/>: face names to REMOVE
    /// ("North", "South", "East", "West", "Top"/"Ceiling", "Bottom"/"Floor").</summary>
    public List<string>? MissingFaces { get; set; }

    // --- Physics (PhysicsPropertyComponent) ---------------------------------------------------------

    /// <summary>Any one of these attaches a PhysicsPropertyComponent; the rest take their defaults
    /// (Mass 0, Friction 0.5, Restitution 0, Drag 0.1).</summary>
    public float? Mass { get; set; }
    /// <inheritdoc cref="Mass"/>
    public float? Friction { get; set; }
    /// <inheritdoc cref="Mass"/>
    public float? Restitution { get; set; }
    /// <inheritdoc cref="Mass"/>
    public float? Drag { get; set; }

    // --- Sound emitter (SoundEmitterComponent) ------------------------------------------------------

    /// <summary>Gate for the whole emitter block. False (the default) means every field below is ignored,
    /// so setting one without this is rejected rather than silently producing a mute object.</summary>
    public bool HasEmitter { get; set; }

    /// <summary>Sound id resolved by the client's SoundMappingService (e.g. "BEACONS/siren").
    /// Required unless <see cref="IsSynth"/>, which generates its own signal.</summary>
    public string? SoundId { get; set; }

    /// <summary>Played once when the voice starts, before <see cref="SoundId"/> takes over (a spin-up).</summary>
    public string? StartSoundId { get; set; }

    /// <summary>Played once when the voice is asked to stop, in place of cutting it (a spin-down).</summary>
    public string? StopSoundId { get; set; }

    /// <summary>Linear gain. 0..1 normally; above 1 is amplification and is reported as a warning.</summary>
    public float? Volume { get; set; }

    /// <summary>Audible radius in metres. Beyond it the voice is not submitted at all.</summary>
    public float? Range { get; set; }

    /// <summary>Distance in metres below which the sound stops getting louder. Must be &lt; Range.</summary>
    public float? MinDistance { get; set; }

    /// <summary>PlaybackMode by NAME: "Single", "LoopOne", "LoopFolder", "Sequential", "StateMachine".</summary>
    public PlaybackMode? Mode { get; set; }

    /// <summary>Direction the emitter points, in the entity's LOCAL space (rotated by the map entity's
    /// `Rotation`). Omit for the default forward (0,0,1). Only audible with a cone narrower than 360°.</summary>
    public Vector3? EmitterDirection { get; set; }

    /// <summary>
    /// Where the sound comes out, in the entity's local space: the emitter slot. Omit for the origin.
    /// Every acoustic question (occlusion, distance, bearing) is asked about this point, so author it for
    /// anything that does not sound from its base: an origin on the road put a vehicle's occlusion probe
    /// inside the road surface.
    /// </summary>
    public Vector3? EmitterOffset { get; set; }

    /// <summary>Directivity cone. Inside the inner angle the sound is at full volume, outside the outer
    /// angle it is at <see cref="ConeOutsideVolume"/>, between them it interpolates. Both in degrees,
    /// 0..360, inner &lt;= outer; 360/360 (the default) is omnidirectional.</summary>
    public float? ConeInsideAngle { get; set; }
    /// <inheritdoc cref="ConeInsideAngle"/>
    public float? ConeOutsideAngle { get; set; }
    /// <inheritdoc cref="ConeInsideAngle"/>
    public float? ConeOutsideVolume { get; set; }

    /// <summary>Replays the sound every N seconds with silence between, unlike LoopOne, which restarts
    /// at once. Zero is not a repeater.</summary>
    public float? RepeatIntervalSeconds { get; set; }

    // --- Granular synthesis (SoundEmitterComponent) --------------------------------------------------

    /// <summary>Play <see cref="SoundId"/> as a grain cloud rather than a sample.</summary>
    public bool? IsGranular { get; set; }
    /// <summary>Playhead position in the source sample, 0..1.</summary>
    public float? GranularPosition { get; set; }
    /// <summary>Grain length in milliseconds (1..1000).</summary>
    public float? GranularGrainSize { get; set; }
    /// <summary>Grains per second (&gt; 0).</summary>
    public float? GranularDensity { get; set; }
    /// <summary>Playback rate multiplier (&gt; 0).</summary>
    public float? GranularPitch { get; set; }
    /// <summary>Random spread applied to position (0..1) and pitch (&gt;= 0) per grain.</summary>
    public float? GranularPosJitter { get; set; }
    /// <inheritdoc cref="GranularPosJitter"/>
    public float? GranularPitchJitter { get; set; }

    // --- Synthesis (SoundEmitterComponent) -----------------------------------------------------------

    /// <summary>Generate the signal instead of playing a sample.</summary>
    public bool? IsSynth { get; set; }
    /// <summary>Whether the synthesised source starts sounding (SoundEmitterComponent.SynthRunning). False
    /// for something a player turns on: a tap. Default true.</summary>
    public bool? SynthRunning { get; set; }
    /// <summary>Waveform: 0 Sine, 1 Square, 2 Triangle, 3 Saw, 4 Noise.</summary>
    public int? SynthWave { get; set; }
    /// <summary>Base frequency in Hz (1..20000).</summary>
    public float? SynthFreq { get; set; }
    /// <summary>Amplitude-LFO rate in Hz (&gt;= 0) and depth (0..1).</summary>
    public float? SynthLfoRate { get; set; }
    /// <inheritdoc cref="SynthLfoRate"/>
    public float? SynthLfoDepth { get; set; }
    /// <summary>Low-pass cutoff as a fraction of Nyquist, 0..1 (1 = open).</summary>
    public float? SynthFilterCutoff { get; set; }
    /// <summary>Filter resonance, 0..1.</summary>
    public float? SynthFilterResonance { get; set; }
    /// <summary>Square-wave duty cycle, exclusive 0..1.</summary>
    public float? SynthPulseWidth { get; set; }

    // --- Acoustic region (RegionComponent) ------------------------------------------------------------

    /// <summary>Any field in this block declares the entity an acoustic REGION — a room volume the
    /// listener can be inside. A region must not be solid, and must carry a non-zero
    /// <see cref="RoomSize"/>, because its reverb is computed from that volume.</summary>
    public bool? IsIndoor { get; set; }
    /// <summary>Interior dimensions in metres, used for the room's volume and surface areas.</summary>
    public Vector3? RoomSize { get; set; }
    /// <summary>Ambience loop crossfaded in while the listener is inside this region.</summary>
    public string? AmbienceId { get; set; }
    /// <summary>Multiplier on the computed reverb time (&gt; 0).</summary>
    public float? ReverbScale { get; set; }

    /// <summary>The six interior surfaces by material name, in the order the reverb reads them: Floor,
    /// Ceiling, North, South, East, West (not the <see cref="FaceMask"/> bit order). The readable form of
    /// RegionComponent.Materials.</summary>
    public string[]? RoomMaterials { get; set; }

    // --- Acoustic portal (PortalComponent) -------------------------------------------------------------

    /// <summary>Either field declares the entity a PORTAL — an opening joining two regions. The values are
    /// the map `EntityId`s of the regions; -1 means "the outside". A prefab normally leaves both at -1 and
    /// the MAP entity names the pair, since which rooms a doorway joins is a property of where it is placed.
    /// A portal must not be solid.</summary>
    public int? RegionAId { get; set; }
    /// <inheritdoc cref="RegionAId"/>
    public int? RegionBId { get; set; }
    /// <summary>Width of the opening in metres (&gt;= 0). 0 means "derive it from the collider at map load".</summary>
    public float? ApertureSize { get; set; }

    // --- Door (DoorComponent) --------------------------------------------------------------------

    /// <summary>
    /// Declares the entity a door: a leaf that swings out of its own doorway. The one thing that may be a
    /// portal and solid at once. It needs a collider: the leaf's width is both what it blocks and the
    /// size of the hole it leaves.
    /// </summary>
    public bool? IsDoor { get; set; }

    /// <summary>The beacon category of a Beacon: "exit", "stairs", "waypoint"... (a waypoint if absent).
    /// Doors and items are door and item beacons without saying. See OpenFPS.Common.Beacons.</summary>
    public string? BeaconCategory { get; set; }

    /// <summary>How long the full swing takes, seconds. Defaults to a little under a second.</summary>
    public float? SwingSeconds { get; set; }

    /// <summary>How far it opens, degrees. A quarter turn by default.</summary>
    public float? SwingDegrees { get; set; }

    /// <summary>Which edge it is hinged on: -1 the left, +1 the right. Decides which way it sweeps,
    /// and therefore which side of the doorway an open leaf is heard on.</summary>
    public float? HingeSide { get; set; }

    /// <summary>A hollow door's skin thickness, metres: two sheets over a core. Absent for a solid
    /// leaf. Decides what it weighs and what note it rings at; it has nothing to do with how much it
    /// lets through, which the transmission figures say.</summary>
    public float? DoorSkinMetres { get; set; }

    /// <summary>The door's hardware: "knob" (the default), "pushbar", "glass-pushbar", "glass-pull",
    /// "auto-slide", "patio-slide" or "elevator". Names its sound events (docs/DOOR_TYPES_EVENTS.md).</summary>
    public string? DoorKind { get; set; }

    /// <summary>The leaf slides along its own width instead of swinging; HingeSide is the way it slides.
    /// Defaults to true for the sliding kinds.</summary>
    public bool? Slides { get; set; }

    /// <summary>Moved by a motor: not opened by hand, and it reverses for anyone in the doorway.</summary>
    public bool? Powered { get; set; }

    /// <summary>Opens by itself when anyone is this close in front of it, either side, metres.</summary>
    public float? SensorMetres { get; set; }

    /// <summary>Closes by itself once the doorway has been clear this long, seconds (a closer, or an
    /// automatic door's hold-open time).</summary>
    public float? CloseAfterSeconds { get; set; }

    /// <summary>How long closing by itself takes from fully open, seconds.</summary>
    public float? CloseSeconds { get; set; }

    /// <summary>Which side needs a key: +1 the leaf's own +Z side, -1 the other, 0 or absent neither.
    /// From that side a shut door is locked: opening it is the key, then the hand.</summary>
    public float? KeyedSide { get; set; }

    /// <summary>Which face of a hinged leaf you push it open from: +1 its own +Z face (the default), -1
    /// the other. It swings away from that face and is pulled from the other. A door's +Z face is its
    /// outside: a room door is pushed from outside and swings in (+1); an exit door is pushed from
    /// inside, where its push bar is, and swings out (-1).</summary>
    public float? PushSide { get; set; }
}
