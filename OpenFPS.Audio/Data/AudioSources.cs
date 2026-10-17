using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Geometry;

namespace OpenFPS.Client.AudioEngine.Data;

/// <summary>
/// What a source is, as its host declares it (docs/SOUND_LIBRARY_BOUNDARY.md 7.2, decision 10): open-fps's
/// SoundEmitterComponent field by field without its wire format, and the size of the body it sits on. The host
/// fills it from what it holds; nothing here is sent anywhere, so it may change shape freely.
/// </summary>
public readonly record struct SourceSpec
{
    public string? SoundId { get; init; }
    /// <summary>Played when the source starts and when it stops.</summary>
    public string? StartSoundId { get; init; }
    public string? StopSoundId { get; init; }
    public PlaybackMode Mode { get; init; }
    public float Volume { get; init; }
    public float Range { get; init; }
    public float MinDistance { get; init; }
    /// <summary>Its aim in its own frame; zero is straight ahead.</summary>
    public Vector3 Direction { get; init; }
    public float ConeInsideAngle { get; init; }
    public float ConeOutsideAngle { get; init; }
    public float ConeOutsideVolume { get; init; }
    /// <summary>Replayed every so many seconds; zero is not a repeater.</summary>
    public float RepeatIntervalSeconds { get; init; }

    public bool IsGranular { get; init; }
    public float GranularPosition { get; init; }
    public float GranularGrainSizeMs { get; init; }
    public float GranularDensity { get; init; }
    public float GranularPitch { get; init; }
    public float GranularPositionJitter { get; init; }
    public float GranularPitchJitter { get; init; }

    public bool IsSynth { get; init; }
    public int SynthWave { get; init; }
    public float SynthFrequency { get; init; }
    public float SynthLfoRate { get; init; }
    public float SynthLfoDepth { get; init; }
    public float SynthFilterCutoff { get; init; }
    public float SynthFilterResonance { get; init; }
    public float SynthPulseWidth { get; init; }

    /// <summary>Where the sound comes out in the thing's own frame: authored, or else the one its kind implies
    /// (a vehicle's exhaust), which is the host's to say since it is the catalogue's.</summary>
    public Vector3 Offset { get; init; }
    /// <summary>How big the thing making the sound is, metres; zero is a point.</summary>
    public float ExtentMetres { get; init; }
    /// <summary>Whether a synthesised source is sounding, for what the host cannot see from here (a crossing's
    /// bell for a train a kilometre off).</summary>
    public bool SynthRunning { get; init; }
    /// <summary>Standing at a stop that takes passengers.</summary>
    public bool ServingStop { get; init; }
    /// <summary>Where a vehicle's side windows are going, 0 shut to 1 down.</summary>
    public float WindowsOpen { get; init; }
    /// <summary>The loudspeaker model a recording is played through, or empty.</summary>
    public string? Loudspeaker { get; init; }
    /// <summary>A fire's share of its heat release water is taking, 0..1.</summary>
    public float Quench { get; init; }
    /// <summary>A source laid along a line of its own, its points from end to end in its own frame; null for a point
    /// or a source whose places are its model's (SoundEmitterComponent.Line).</summary>
    public Vector3[]? Line { get; init; }

    /// <summary>The body it sits on: its collider's shape and full size (a fire's bed, a shore's stretch, a
    /// vehicle the catalogue cannot name).</summary>
    public ColliderShape Shape { get; init; }
    public Vector3 Size { get; init; }
}

/// <summary>Where a source is and what it is doing, as the host last heard: its pose and velocity, and what a
/// vehicle, a train or an aeroplane sends of itself.</summary>
public readonly record struct SourceState
{
    public Vector3 Position { get; init; }
    public Quaternion Rotation { get; init; }
    public Vector3 Velocity { get; init; }
    /// <summary>How hard a vehicle works its tyres, 0..2 with 1 the limit.</summary>
    public float TyreDemand { get; init; }
    /// <summary>Each wheel as last sent, front axle first; null for anything without.</summary>
    public WheelState[]? Wheels { get; init; }
    /// <summary>A vehicle's horn and siren switches (VehicleSignalBits); zero for anything without.</summary>
    public byte Signals { get; init; }
    /// <summary>How hard a train or an aeroplane is working, 0..1.</summary>
    public float Lever { get; init; }
}

/// <summary>
/// One thing the host holds, as the audio world reads it this frame: what it is, where it is, and where its
/// sound comes out. Anything may be asked about, not only what sounds: somebody talking, a door a beacon sits
/// on. One without an emitter has an empty spec.
/// </summary>
public sealed class AudioSource
{
    public AudioSource(int id) => Id = id;

    /// <summary>The host's id for it (open-fps: the entity id).</summary>
    public int Id { get; }
    public SourceSpec Spec;
    public SourceState State;
    /// <summary>Where its sound comes out, in the world (<see cref="AudioEmission.PointFor"/>).</summary>
    public Vector3 Emission;
    /// <summary>How big a sphere may be probed there (<see cref="AudioEmission.OcclusionRadiusFor"/>).</summary>
    public float ProbeRadius;
    /// <summary>The host has it switched off (open-fps: a beacon whose category is off): its voice is stopped.</summary>
    public bool Silenced;

    /// <summary>Fills it for this frame, and works out where its sound comes out.</summary>
    public void Set(in SourceSpec spec, in SourceState state, bool silenced = false)
    {
        Spec = spec;
        State = state;
        Silenced = silenced;
        Emission = AudioEmission.PointFor(state.Position, state.Rotation, spec.Offset);
        ProbeRadius = AudioEmission.OcclusionRadiusFor(state.Position, Emission);
    }
}

/// <summary>
/// The sources one frame of an audio world reads (docs/SOUND_LIBRARY_BOUNDARY.md 7.2, stage 7): every thing by
/// its id, what sounds by itself and what moves in the host's order, and what the weather is doing to the
/// sources this frame. The host fills it once a frame and never changes it while the frame is placed; the
/// shapes and the air are <see cref="IAcousticWorld"/>'s.
/// </summary>
public interface IAudioSources
{
    /// <summary>A thing by its id, as of this frame; false for one the host does not hold.</summary>
    bool TryGet(int id, [NotNullWhen(true)] out AudioSource? source);

    /// <summary>Whether the host holds a thing by this id.</summary>
    bool Contains(int id);

    /// <summary>Every thing that sounds by itself, in the host's order: the order voices are given out in.</summary>
    IReadOnlyList<int> Audible { get; }

    /// <summary>How many things move, and each in the host's order.</summary>
    int MovingCount { get; }
    AudioSource MovingAt(int index);

    /// <summary>When the poses were sampled, seconds on the audio clock.</summary>
    double SampledAt { get; }

    /// <summary>The woods the far trees are heard as (WoodChorus), or null.</summary>
    WoodChorus? Woods { get; }

    /// <summary>What is falling as the host reports it, the rate of rain from a host that sends only that (mm/h),
    /// and what is coming down of either.</summary>
    Precipitation Precipitation { get; }
    float RainRateMmPerHour { get; }
    Precipitation Falling { get; }

    /// <summary>The water in a wheel path of the roads, mm, for vehicles whose wheels are not sent.</summary>
    float RoadWaterMm { get; }

    /// <summary>The air outdoors as the host's weather has it (for thunder rendered for where the listener stands).</summary>
    OutdoorAir Outdoors { get; }
}

/// <summary>
/// The listener as the host has them (docs/SOUND_LIBRARY_BOUNDARY.md 7.2): where the feet are as the listener is
/// heard (smoothed, so a correction from the host's authority is not heard as a jump) and where the body is as the
/// host's physics has it, how high the ears are above the feet, which way the head faces, how fast it moves, what it
/// rides in, how sheltered it is and whether it is indoors.
/// </summary>
public readonly record struct ListenerState
{
    public Vector3 Feet { get; init; }
    public Vector3 Body { get; init; }
    public float EyeHeight { get; init; }
    public Quaternion Rotation { get; init; }
    /// <summary>The head's turn about the vertical, radians: what the mouth faces.</summary>
    public float Yaw { get; init; }
    public Vector3 Velocity { get; init; }
    /// <summary>The id of what the listener rides in, or -1.</summary>
    public int RidingId { get; init; }
    public float Shelter { get; init; }
    public bool Indoor { get; init; }

    public bool Riding => RidingId >= 0;
}

/// <summary>A level crossing's rails as a voiced wheel meets them: the crossing's middle, the way the rails run, how
/// far either side of the middle they are crossed, and a point on each rail.</summary>
public readonly record struct RailCrossing(Vector3 Centre, Vector3 Along, float HalfLengthMetres, Vector3 Left, Vector3 Right);
