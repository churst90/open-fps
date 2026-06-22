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
        ApertureFactor = 1.0f;
        TransmissionBleed = 1.0f;
        IsEvent = false;
        
        // Ensure audio is audible by default
        EqLow = 1.0f;
        EqMid = 1.0f;
        EqHigh = 1.0f;
    }
}
