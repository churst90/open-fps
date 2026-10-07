using System.Numerics;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.AudioEngine.Data;

public enum EmitterType { EntityAttached, WorldLocked, Atmospheric, UI }
public enum SynthWaveType { Sine, Square, Triangle, Saw, Noise }

/// <summary>
/// Everything the mixer needs to place and play one voice. EmitterStreamReplayTests logs every field
/// in declaration order: adding, removing or reordering one changes the stored stream.
/// </summary>
public struct SpatialEmitter
{
    public int EntityId;
    public string SoundId;
    public string StartSoundId; // played when the emitter becomes active
    public string StopSoundId;  // played when it is stopped
    public PlaybackMode Mode; 
    public Vector3 Position; 
    public Vector3 ApparentPosition; 
    public float EffectiveDistance;
    public float Occlusion; 
    public float Volume; 
    public float Range; 
    public float Pitch; 
    public EmitterType Type;

    /// <summary>
    /// Pinned above the physics because a player needs it: a short explicit list (speech, your own
    /// footsteps, a warning tone), not a knob on every object. Ranking kinds rather than audibility
    /// would let a clap 200 m away outrank a car at 5 m. Everything else ranks on what reaches the ear
    /// (VoiceManager.Audibility).
    /// </summary>
    public bool Essential;

    /// <summary>
    /// How big the source is, metres; zero is a point. Inside its own size the inverse law does not
    /// hold, so <see cref="OpenFPS.Common.Loudness.Place(float, float)"/> widens the reference distance
    /// and pays the gain down: the far field is unchanged. Widened without paying back, a quiet vehicle
    /// came out up to 8 dB over its own level.
    /// </summary>
    public float ExtentMetres;

    public bool IsReflection; 
    /// <summary>A sound that is part of the listener — their own feet — and is placed at
    /// <see cref="ListenerOffset"/> from the listener's head every tick, whatever Position says.</summary>
    public bool FollowsListener;

    /// <summary>Made by the vehicle the listener sits in (its own door, its latch), so not heard through
    /// its glass.</summary>
    public bool InsideListenersVehicle;

    /// <summary>
    /// The band gains, air loss and room here are the path, and a re-submission while playing updates
    /// them: for a caller that works out the path itself (a person talking as they walk). Otherwise the
    /// acoustic worker owns the path and re-submissions leave the tone alone.
    /// </summary>
    public bool CarriesPath;
    public Vector3 ListenerOffset;
    public float DelayMs;
    /// <summary>For a reflection: the entity it copies. The copy starts at that voice's own playback
    /// position, so an echo of a sustained sound lags it by the path's extra delay.</summary>
    public int ReflectionOf;
    public long SequenceId; 
    public float ConeInside; 
    public float ConeOutside; 
    public float ConeOutsideVolume;
    public float MinDistance;
    public int TargetRegionId;
    public Vector3 Velocity;
    /// <summary>
    /// When <see cref="Position"/> was true, seconds on <see cref="OpenFPS.Common.AudioClock"/>; zero
    /// is unknown, taken as now. Stamped at sample time, never at submission: stamped on submission,
    /// dead reckoning was reset by every re-submission and a close fast car moved in a 30 Hz staircase
    /// (2.3 semitones and 60 degrees a step at 3 m and 235 km/h), heard as "it stops for a second and
    /// then continues". See docs/AUDIO_GHOSTS_AND_STUTTERS.md, rule 5.
    /// </summary>
    public double PositionSampledAt;
    public Vector3 Direction;
    public float ApertureFactor;
    public float TransmissionBleed;
    public bool IsEvent; 
    /// <summary>A one-off's declared level at a metre, dB SPL, or 0 when it has none. What the ear
    /// overload is worked out from: see FmodAudioProvider.Overload.</summary>
    public float LevelDb;

    /// <summary>
    /// For the ear model (docs/EAR_MODEL.md): the source's declared level at a metre, dB SPL, or 0 to
    /// leave the voice alone. A reflection carries its source's level here, and how far under it it is
    /// in <see cref="EarCopyDb"/>.
    /// </summary>
    public float EarLevelDb;
    /// <summary>A copy's level under its source, dB (20 log10 of what the surface and the path kept); 0
    /// for a direct sound.</summary>
    public float EarCopyDb;
    public float ReflectionSpread; // degrees, 0-360: how wide the reflection feels

    public bool IsGranular;
    public float GranularPosition; // 0.0 to 1.0, position in the source file
    public float GranularGrainSizeMs; // Length of each grain (e.g. 10 to 200 ms)
    public float GranularDensity; // Grains per second (e.g. 10 to 500)
    public float GranularPitch; // Base pitch of grains
    public float GranularPositionJitter; // Randomness in position (0.0 to 1.0)
    public float GranularPitchJitter; // Randomness in pitch

    public bool IsSynth;
    /// <summary>A vehicle engine preset (see VehicleProfile.Presets) run live in the mixer. Set when
    /// the entity's SoundId is "engine:&lt;preset&gt;".</summary>
    public string EngineKey;
    /// <summary>
    /// A physical model other than a vehicle engine, run live in the mixer, as the whole prefixed id
    /// ("machine:ac_window", "aircraft:airliner"): the prefix says which library the name is in, and
    /// FmodAudioProvider is the one place that reads it. <see cref="EngineKey"/> is a whole vehicle
    /// (driveline, tyres, a driver), with outlets, echoes and borrowed voices.
    /// </summary>
    public string PhysicalKey = "";

    /// <summary>Whether the listener is sitting in this vehicle, so its engine voice renders what
    /// gets through the body rather than what radiates from it. See EngineVoiceState.Interior.</summary>
    public bool Interior;
    /// <summary>An aircraft's power lever, 0..1, read off its climb angle (ClientAudioSystem.PowerLeverFor):
    /// climbing near full, level at cruise, descending at idle.</summary>
    public float PowerLever;

    /// <summary>How hard a rotor is meeting its own wake, 0..1: a helicopter descending or in fast
    /// forward flight slaps, one in a hover does not.</summary>
    public float RotorWake;

    /// <summary>An aeroplane's wheels are on the ground, read off the flight path (at runway height and
    /// no longer descending); the change is the touchdown (AircraftVoiceState).</summary>
    public bool OnGround;

    /// <summary>Road speed the engine follows, m/s.</summary>
    public float EngineSpeed;
    public bool EngineRunning;
    /// <summary>Standing at a stop that takes passengers; see SoundEmitterComponent.ServingStop.</summary>
    public bool ServingStop;
    /// <summary>How far down a vehicle's side windows are, 0 shut to 1 fully down, where the client has
    /// the glass now (see CabinWalls.WindowsOpen).</summary>
    public float WindowsOpen;
    /// <summary>When non-zero, this emitter is a reflection of that entity's live engine: the same
    /// signal delayed by EchoDelaySeconds and scaled by EchoGain, placed at the mirrored source.</summary>
    public int EchoOfEntity;
    public float EchoDelaySeconds;
    public float EchoGain;
    /// <summary>How rough the surface an echo came off is, 0..1 — how much the renderer smears it.
    /// See EngineEchoState.Scattering.</summary>
    public float EchoScattering;
    /// <summary>The height of the surface the ground reflection bounces off, world metres (where its
    /// image is); meaningful only when the ground gains are above zero.</summary>
    public float GroundHeight;
    /// <summary>The ground between source and listener (GroundReflection): the extra path, seconds, and
    /// the pressure handed back below and above a kilohertz, spreading included. All zero: none.</summary>
    public float GroundDelaySeconds;
    public float GroundLowGain;
    public float GroundHighGain;
    /// <summary>
    /// When non-zero, this voice is the front outlet (intake and block) of that entity's live engine,
    /// at its own point on the machine. The same integration writes both taps, and they sum to exactly
    /// the single voice, so a machine keeps its level when it gains or loses the outlet. See
    /// EngineTapState.
    /// </summary>
    public int IntakeOfEntity;
    /// <summary>
    /// When non-zero, this voice is one path into the cabin of that entity, the vehicle the listener
    /// is sitting in (CabinPaths): path <see cref="CabinPath"/> (1 and up), read from its own ring of
    /// the vehicle's live engine and played from where it comes in. See EngineTapState.
    /// </summary>
    public int CabinOfEntity;
    public int CabinPath;
    /// <summary>For the vehicle the listener sits in: where the ear is across its cabin, metres right
    /// of its middle (which side's windows the outside comes in by).</summary>
    public float CabinEarX;
    /// <summary>
    /// When non-zero, this voice is place <see cref="Place"/> of that entity's tree or fire: one of the
    /// independent streams its synth renders across its extent (ExtendedSources, NaturePlaceState). Its
    /// PhysicalKey is the source's own.
    /// </summary>
    public int PlaceOfEntity;
    public int Place;
    /// <summary>For an extended source's own voice: how much of it its outer places carry, 0 to 1,
    /// already slewed (ExtendedSources.SpreadFor, Slew).</summary>
    public float Spread;
    /// <summary>For a wood heard as one (WoodChorus): how many of its trees its synth stands for now.
    /// Read only for a wood's voice.</summary>
    public float Trees;
    /// <summary>How hard the road is working this vehicle's tyres, as a fraction of their grip: zero
    /// rolling, one the limit where a tyre squeals, above it sliding. From the server's
    /// <see cref="OpenFPS.Common.Networking.EntityState.TyreDemand"/>.</summary>
    public float TyreSlip;
    /// <summary>Each wheel as the server sent it, front axle first, or null. The tyre voices take
    /// each axle's share of <see cref="TyreSlip"/> from it.</summary>
    public OpenFPS.Common.Networking.WheelState[]? Wheels;
    /// <summary>Wheels about to strike a step in the road (a rail), with when: handed to the vehicle's
    /// voice the once they are carried, and null otherwise. See WheelStrikes.</summary>
    public OpenFPS.Client.AudioEngine.Core.WheelStrike[]? WheelStrikes;
    /// <summary>The road's water under a vehicle whose wheels are not sent, mm (WorldSnapshot.RoadWaterMm).</summary>
    public float RoadWaterMm;
    public SynthWaveType SynthWave;
    public float SynthFrequency; // Hz
    public float SynthLfoRate; // Hz
    public float SynthLfoDepth; // 0..1
    public float SynthFilterCutoff; // 0..1
    public float SynthFilterResonance; // 0..1
    public float SynthPulseWidth; // square and pulse waves

    // Band gains, 1 unity: the whole of what the path does to each band (occlusion, transmission,
    // diffraction), applied once by the mixer.
    public float EqLow;
    public float EqMid;
    public float EqHigh;

    /// <summary>What the air takes over the path, dB per band (ISO 9613-1). A one-shot has no later
    /// path update to bring these in, so they ride on the emitter from the start.</summary>
    public float AirLowDb, AirMidDb, AirHighDb;

    public SpatialEmitter()
    {
        EntityId = 0;
        SoundId = "";
        StartSoundId = "";
        StopSoundId = "";
        Mode = PlaybackMode.Single;
        Position = Vector3.Zero;
        ApparentPosition = Vector3.Zero;
        EffectiveDistance = 0;
        Occlusion = 0;
        Volume = 1.0f;
        Range = 100.0f;
        Pitch = 1.0f;
        Type = EmitterType.EntityAttached;
        Essential = false;
        ExtentMetres = 0f;
        IsReflection = false;
        DelayMs = 0;
        SequenceId = 0;
        ConeInside = 360f;
        ConeOutside = 360f;
        ConeOutsideVolume = 1.0f;
        MinDistance = 3.0f;
        TargetRegionId = -1;
        Direction = Vector3.UnitZ;
        Velocity = Vector3.Zero;
        PositionSampledAt = 0;
        ApertureFactor = 1.0f;
        TransmissionBleed = 1.0f;
        IsEvent = false;

        EqLow = 1.0f;
        EqMid = 1.0f;
        EqHigh = 1.0f;
    }
}
