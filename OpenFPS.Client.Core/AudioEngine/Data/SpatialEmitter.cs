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

    /// <summary>
    /// Pinned above the physics because a player needs it, whatever the arithmetic says.
    ///
    /// A SHORT, explicit list on purpose — speech, the player's own footsteps, a warning tone — and
    /// not a knob on every object. It replaces an authored `Priority` integer that ranked KINDS of
    /// thing rather than what could be heard: engines were 1 and transients were 2, so a clap two
    /// hundred metres away outranked a car at five metres by four to one, and the car that lost was
    /// not faded but STOPPED and rebuilt the next frame. For a synthesized engine that means a fresh
    /// ring, priming silence and an envelope fade — which is what "vehicles stop close in front of me
    /// while going past" sounds like.
    ///
    /// Everything else is ranked on <see cref="OpenFPS.Common.Loudness.RenderedGain"/> times what the
    /// path lets through: the level this voice will actually deliver to the ear. See VoiceManager.
    /// </summary>
    public bool Essential;

    /// <summary>
    /// How big the source is, metres. Zero means a point.
    ///
    /// A car is three and a half metres of machine, a grandstand is eighty metres of people, a
    /// fountain is three metres of falling water — and inside a source's own size the inverse law
    /// does not hold, because stepping a metre nearer one part of it steps you a metre further from
    /// another. <see cref="OpenFPS.Common.Loudness.Place(float, float)"/> is what does the arithmetic:
    /// the reference distance widens to the source's radius and the gain is paid down to match, so
    /// the FAR FIELD IS UNCHANGED and only the near field flattens.
    ///
    /// It replaces `MathF.Max(reference, 3f)` in ClientAudioSystem, which widened a vehicle's
    /// reference without paying anything back and so made every quiet vehicle up to eight decibels
    /// louder than its own level said it was.
    /// </summary>
    public float ExtentMetres;

    public bool IsReflection; 
    /// <summary>A sound that is part of the listener — their own feet — and is placed at
    /// <see cref="ListenerOffset"/> from the listener's head every tick, whatever Position says.</summary>
    public bool FollowsListener;

    /// <summary>
    /// Made by the vehicle the listener is sitting in — its own door, its own latch — and so not
    /// heard through that vehicle's glass. Everything else outside the car is.
    /// </summary>
    public bool InsideListenersVehicle;
    public Vector3 ListenerOffset;
    public float DelayMs; 
    /// <summary>For a reflection: the entity whose sound this is a copy of. The provider starts the copy
    /// at that voice's own playback position, so an echo of a sustained sound lags it by exactly the
    /// path's extra delay instead of being the same file started again from the top.</summary>
    public int ReflectionOf;
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
    /// <summary>
    /// A physical model other than a vehicle engine, run live in the mixer, as the WHOLE prefixed id
    /// the entity carries: "machine:ac_window", "aircraft:airliner".
    ///
    /// The prefix is kept rather than stripped because it is the only thing that says which library
    /// to look the name up in, and there is more than one — a governed single-cylinder machine and a
    /// turbofan are different models with different inputs. One field with the prefix left on means
    /// one place decides what a name means (FmodAudioProvider), rather than every reader carrying a
    /// flag for which kind it was handed.
    ///
    /// Separate from <see cref="EngineKey"/>, which buys a whole VEHICLE — driveline, gearbox,
    /// tyres, a driver following a road speed — and is the one model with two outlets, echoes and
    /// borrowed voices hanging off it.
    /// </summary>
    public string PhysicalKey = "";

    /// <summary>Whether the listener is sitting in this vehicle, so its engine voice renders what
    /// gets through the body rather than what radiates from it. See EngineVoiceState.Interior.</summary>
    public bool Interior;
    /// <summary>
    /// The power lever of anything that has one, 0..1 — an aircraft.
    ///
    /// Read off the CLIMB ANGLE rather than scripted (ClientAudioSystem.PowerLeverFor): an aeroplane
    /// going up is at or near full power, one holding height is at cruise, one coming down is at
    /// idle with the drag doing the work. That is why the same aeroplane overhead and on approach
    /// are completely different sounds with nothing about the aeroplane changed, and doing it this
    /// way means the sound falls out of the flight path instead of being painted onto it.
    /// </summary>
    public float PowerLever;

    /// <summary>How hard a rotor is meeting its own wake, 0..1 — a helicopter descending or in fast
    /// forward flight slaps, one in a hover does not. Ignored by anything without a rotor.</summary>
    public float RotorWake;

    /// <summary>
    /// An aeroplane's wheels are on the ground. Read off the flight path like the power lever is:
    /// an aeroplane at runway height that has stopped going down has landed, and nothing scripts
    /// it. The transition into it is the touchdown; see AircraftVoiceState.
    /// </summary>
    public bool OnGround;

    /// <summary>Road speed the engine follows, m/s.</summary>
    public float EngineSpeed;
    public bool EngineRunning;
    /// <summary>When non-zero, this emitter is a reflection of that entity's live engine: the same
    /// signal delayed by EchoDelaySeconds and scaled by EchoGain, placed at the mirrored source.</summary>
    public int EchoOfEntity;
    public float EchoDelaySeconds;
    public float EchoGain;
    /// <summary>
    /// When non-zero, this voice is the FRONT OUTLET of that entity's live engine — what the machine
    /// breathes through, and the block behind it — placed at its own point on the machine.
    ///
    /// Not a second engine: the same integration writes both taps, so this costs a buffer read and a
    /// voice. The two sum to exactly what the single voice was, so a machine does not change level
    /// when it gains or loses its second outlet. See EngineTapState.
    /// </summary>
    public int IntakeOfEntity;
    /// <summary>
    /// How hard the road is working this vehicle's tyres, as a fraction of the grip they have.
    ///
    /// Zero is rolling; one is the limit, where a tyre squeals; above that it is sliding. Computed
    /// from the entity's OWN motion rather than from anything knowing what a corner is — see
    /// ClientAudioSystem.TyreDemand — so a car, a bus, a runaway trolley and a player-driven vehicle
    /// all get it on the same terms.
    /// </summary>
    public float TyreSlip;
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
        Essential = false;
        ExtentMetres = 0f;
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
