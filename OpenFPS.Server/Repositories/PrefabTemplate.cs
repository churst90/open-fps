using System.Numerics;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// The prefab format. This class *is* the spec: every key a prefab JSON file may carry is a property here,
/// nothing else is accepted, and <see cref="PrefabValidator"/> rejects a file whose values cannot produce a
/// coherent entity. `prefabs/prefab-schema.json` and `docs/AUTHORING.md` are written against this type;
/// before they were, they described two formats the loader had never read (a nested
/// `Collider`/`SoundEmitter`/`Acoustics` shape and an integer `Type`), so an author following either
/// produced a file that deserialized into all-defaults and spawned an invisible, silent, materialless cube.
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

    /// <summary>Spoken name of the entity. Becomes NameComponent/IdentityComponent.Name — in a game played
    /// by ear this is the only way the thing can be referred to at all, so it is required.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Longer description, read out on examine. Becomes IdentityComponent.Description.</summary>
    public string Description { get; set; } = string.Empty;

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

    /// <summary>Whether the collider blocks movement. Defaults to true when a ColliderSize is given.
    /// A region volume or a portal must set this false.</summary>
    public bool? IsSolid { get; set; }

    // --- Health / item ----------------------------------------------------------------------------

    /// <summary>Spawns a HealthComponent at full health. Omit for something that cannot be damaged.</summary>
    public int? MaxHealth { get; set; }

    /// <summary>Declares this an item. Recorded on the template only — there is no inventory system yet,
    /// so nothing consumes it; it must agree with <see cref="Type"/> = Item.</summary>
    public bool IsItem { get; set; }

    /// <summary>Item weight. Recorded only; see <see cref="IsItem"/>.</summary>
    public float? ItemWeight { get; set; }

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

    /// <summary>Directivity cone. Inside the inner angle the sound is at full volume, outside the outer
    /// angle it is at <see cref="ConeOutsideVolume"/>, between them it interpolates. Both in degrees,
    /// 0..360, inner &lt;= outer; 360/360 (the default) is omnidirectional.</summary>
    public float? ConeInsideAngle { get; set; }
    /// <inheritdoc cref="ConeInsideAngle"/>
    public float? ConeOutsideAngle { get; set; }
    /// <inheritdoc cref="ConeInsideAngle"/>
    public float? ConeOutsideVolume { get; set; }

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
    /// <summary>AcousticEnvironmentType by NAME: "Atmospheric", "Vacuum", "Underwater", "Digital",
    /// "LargeOpen", "SmallTight".</summary>
    public AcousticEnvironmentType? EnvType { get; set; }
    /// <summary>Interior dimensions in metres, used for the room's volume and surface areas.</summary>
    public Vector3? RoomSize { get; set; }
    /// <summary>Ambience loop crossfaded in while the listener is inside this region.</summary>
    public string? AmbienceId { get; set; }
    /// <summary>Multiplier on the computed reverb time (&gt; 0).</summary>
    public float? ReverbScale { get; set; }

    /// <summary>The six interior surfaces by MATERIAL NAME, in the order the reverb math reads them:
    /// Floor, Ceiling, North, South, East, West. Exactly six entries. (This is the readable form of
    /// RegionComponent.Materials, which stores resonance indices — authoring by raw index is unverifiable.)
    /// Note this order is NOT the <see cref="FaceMask"/> bit order.</summary>
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
}
