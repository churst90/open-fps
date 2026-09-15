using System.Numerics;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.AudioEngine.Data;

public enum EmitterType { EntityAttached, WorldLocked, Atmospheric, UI }
public enum SynthWaveType { Sine, Square, Triangle, Saw, Noise }

public struct SpatialEmitter
{
    public int EntityId; 
    public string SoundId; 
    public string StartSoundId; // Triggered when emitter becomes active
    public string StopSoundId;  // Triggered when emitter is deactivated
    public PlaybackMode Mode; 
    public Vector3 Position; 
    public Vector3 ApparentPosition; 
    public float EffectiveDistance;
    public float Occlusion; 
    public float Volume; 
    public float Range; 
    public float Pitch; 
    public EmitterType Type; 
    public int Priority; 
    public bool IsReflection; 
    public float DelayMs; 
    public long SequenceId; 
    public float ConeInside; 
    public float ConeOutside; 
    public float ConeOutsideVolume;
    public float MinDistance;
    public bool EnableReverb;
    public int TargetRegionId;
    public Vector3 Velocity;
    /// <summary>
    /// When <see cref="Position"/> was TRUE, seconds on <see cref="OpenFPS.Common.AudioClock"/>.
    /// Zero means "nobody knows", and the provider then treats it as now.
    ///
    /// The distinction between when a position was sampled and when it was handed over is the whole
    /// of why a close, fast car stopped mid-pass. Remote entities are interpolated once per 33 ms
    /// simulation step; the audio update submits at about 45 Hz, so half its submissions carry a
    /// position that has not changed; and the 250 Hz attribute loop then re-applies whatever it was
    /// last given. Stamped on SUBMISSION, every one of those looked freshly sampled, dead reckoning
    /// saw an age of four milliseconds, and the loop wrote the same pitch and the same bearing eight
    /// times over before jumping — a 30 Hz staircase, which at three metres and 235 km/h is two and a
    /// third semitones and sixty degrees a step. Freeze, jump, freeze, jump is what "it stops for a
    /// second and then continues" sounds like.
    ///
    /// Stamped at SAMPLE time, the same value can be re-applied as often as anything likes and the
    /// age keeps growing, which is what makes the reckoning carry the car between updates instead of
    /// being reset by its own re-submission. It is the same rule for a car, a drone and a running NPC.
    /// </summary>
    public double PositionSampledAt;
    public Vector3 Direction;
    public float ApertureFactor;
    public float TransmissionBleed;
    public bool IsEvent; 
    public bool IsImportant; // If true, receives high-fidelity 3rd-order reflection tracing.
    public float ReflectionSpread; // (0-360) How wide the reflection feels in 3D space.

    // Granular Synthesis Parameters
    public bool IsGranular;
    public float GranularPosition; // 0.0 to 1.0, position in the source file
    public float GranularGrainSizeMs; // Length of each grain (e.g. 10 to 200 ms)
    public float GranularDensity; // Grains per second (e.g. 10 to 500)
    public float GranularPitch; // Base pitch of grains
    public float GranularPositionJitter; // Randomness in position (0.0 to 1.0)
    public float GranularPitchJitter; // Randomness in pitch

    // Synthesizer Parameters
    public bool IsSynth;
    /// <summary>A vehicle engine preset (see VehicleProfile.Presets) run live in the mixer. Set when
    /// the entity's SoundId is "engine:&lt;preset&gt;".</summary>
    public string EngineKey;
    /// <summary>Road speed the engine follows, m/s.</summary>
    public float EngineSpeed;
    public bool EngineRunning;
    /// <summary>When non-zero, this emitter is a reflection of that entity's live engine: the same
    /// signal delayed by EchoDelaySeconds and scaled by EchoGain, placed at the mirrored source.</summary>
    public int EchoOfEntity;
    public float EchoDelaySeconds;
    public float EchoGain;
    public SynthWaveType SynthWave;
    public float SynthFrequency; // Base frequency (e.g. 440.0f)
    public float SynthLfoRate; // Lfo speed in Hz
    public float SynthLfoDepth; // Amount of modulation (0.0 to 1.0)
    public float SynthFilterCutoff; // Filter cutoff (0.0 to 1.0)
    public float SynthFilterResonance; // Filter resonance (0.0 to 1.0)
    public float SynthPulseWidth; // For Square/Pulse waves

    // 3-Band EQ Multipliers (1.0 = unity gain, 0.0 = mute)
    public float EqLow;
    public float EqMid;
    public float EqHigh;

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
        Priority = 5;
        IsReflection = false;
        DelayMs = 0;
        SequenceId = 0;
        ConeInside = 360f;
        ConeOutside = 360f;
        ConeOutsideVolume = 1.0f;
        MinDistance = 3.0f;
        EnableReverb = true;
        TargetRegionId = -1;
        Direction = Vector3.UnitZ;
        Velocity = Vector3.Zero;
        PositionSampledAt = 0;
        ApertureFactor = 1.0f;
        TransmissionBleed = 1.0f;
        IsEvent = false;
        
        // Ensure audio is audible by default
        EqLow = 1.0f;
        EqMid = 1.0f;
        EqHigh = 1.0f;
    }
}
