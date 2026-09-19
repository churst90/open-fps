using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.AudioEngine.Fmod;

public interface IAudioProvider : IDisposable
{
    bool Initialize();
    void Update();

    /// <summary>Mean and worst time one attribute pass took since this was last read, how many passes
    /// there were, and how many voices each walked. Reading it resets the window. See
    /// AudioEngineFacade's loop: the period is what makes a pass-by glide instead of step, so
    /// somebody has to be able to check that it is actually being met.</summary>
    (double MeanMs, double MaxMs, int Calls, int Voices) TakeUpdateCost() => (0, 0, 0, 0);
    void UpdateListener(Vector3 position, Quaternion rotation, Vector3 velocity, int regionId);
    void UpdateShelter(float shelterFactor);
    /// <summary>Describes the surfaces immediately around the listener's head — one probe per
    /// direction, in HEAD space — so the mixer can render each as its own early reflection.</summary>
    void UpdateBoundaries(ReadOnlySpan<BoundaryProbe> probes);

    /// <summary>
    /// Starts an ambisonic ambience bed, or re-aims a playing one at a new level. The soundfield is
    /// fixed in the WORLD: it is rotated by the listener's orientation and decoded binaurally every
    /// block, so turning your head moves you through it rather than carrying it with you. Returns false
    /// (having said why in the log) when the file is not a full-sphere ambisonic recording, or when
    /// Steam Audio is unavailable to decode one.
    /// </summary>
    bool PlayAmbientBed(string soundId, AmbisonicLayout layout, float volume, bool loop = true);

    /// <summary>Sets the level a bed glides toward. Two beds at two levels is a cross-fade.</summary>
    void SetAmbientBedVolume(string soundId, float volume);

    /// <summary>Stops a bed and frees its decoder.</summary>
    void StopAmbientBed(string soundId);
    void SetAcousticMap(OpenFPS.Common.AcousticMap map);
    void PlaySpatialSound(SpatialEmitter emitter);
    void UpdateSpatialAttributes(SpatialEmitter emitter);
    void SetAcousticPath(int entityId, AcousticPathData path);

    /// <summary>
    /// Overrides the listener-region reverb decay (FMOD SFXREVERB ms) with a geometry-derived value from
    /// the Steam Audio reflection simulation. 0 = no override (keep the Sabine estimate).
    ///
    /// <paramref name="enclosure"/> is the OTHER half of the same answer, 0 (open field) to 1 (sealed
    /// box): the decay says how long a tail would last here, and the enclosure says whether there is
    /// one. They have to arrive together, because a long decay measured where nothing comes back is
    /// precisely the reading that put a cathedral over an open racetrack. See OpenFPS.Common.Enclosure.
    /// </summary>
    void SetSimulatedReverbDecay(float decayMs, float enclosure, float hfDecayRatio, float lfDecayRatio);
    /// <summary>Where the listener's reverberant field comes from (world space, unit or zero) and how
    /// one-sided it is, 0..1. The listener's reverb is steered by it. See Enclosure.ReturnCentroid.</summary>
    void SetListenerReverbField(Vector3 returnDirection, float anisotropy, float meanFreePathMetres);

    /// <summary>Sets the air temperature (°C) the Doppler math uses for the speed of sound. This is how
    /// the simulated weather reaches the mix: c = 331.3 + 0.606·T.</summary>
    void SetAirTemperature(float celsius);
    /// <summary>The mixer's DSP load, 0..1+. 1 means the callback is using its whole deadline.</summary>
    float MixerLoad { get; }

    /// <summary>
    /// How many binaural voices are still free — the resource a voice budget is actually spending.
    ///
    /// Every spatialised voice needs an HRTF slot. A voice that cannot get one still plays, and that
    /// is the trouble: it plays FLAT, with no position at all, which on a map navigated by ear is
    /// worse than silence because it lies about where something is.
    /// </summary>
    int SpatialVoicesFree { get; }

    /// <summary>Brings a live engine voice back to full after a fade-out was started. Idempotent.</summary>
    void ReviveEngine(int entityId);

    /// <summary>Asks a live engine voice to fade out; true once it is silent and safe to stop.
    /// True also when there is no such voice, so "gone" and "never existed" look the same.</summary>
    bool FadeOutEngine(int entityId);

    /// <summary>
    /// Takes ANY voice down to silence over about eighty milliseconds; true once it is there.
    ///
    /// The budget's way of letting go of a continuous source. Distinct from FadeOutEngine, which
    /// slews the SYNTHESIS's own envelope inside the DSP: this is the channel's gain, so it works for
    /// a sample, a loop, a granular voice and a synthesized engine alike. True also when there is no
    /// such voice, so "gone" and "never existed" look the same.
    /// </summary>
    bool FadeOutVoice(int entityId);

    /// <summary>Brings one back after a fade was started. Idempotent.</summary>
    void CancelVoiceFade(int entityId);

    /// <summary>
    /// What one car's engine is actually doing: the road speed it has been TOLD, the speed its own
    /// driveline has reached, the crank speed, and the gear. Diagnostic.
    ///
    /// "The cars sound like they are slowing down" has at least four different causes that sound
    /// identical from a chair — the cars really are slowing (an oval makes them lift twice a lap),
    /// the world is reporting a speed that is too low, the virtual driver is not holding the speed it
    /// was given, or the driver is shifting up. Reading the four numbers separates them in one line.
    /// </summary>
    bool TryGetEngineTelemetry(int entityId, out float toldSpeed, out float ownSpeed, out float rpm, out int gear)
    {
        toldSpeed = ownSpeed = rpm = 0f; gear = 0; return false;
    }
    void StopSound(int entityId);
    bool IsPlaying(int entityId);
    Vector3 GetSoundPosition(int entityId);
    float GetPlaybackProgress(int entityId);
    IEnumerable<int> GetActiveSpatialSoundIds();
    void Preload(string soundId);

    /// <summary>
    /// Plays a decoded PCM voice packet as a one-shot 3D sound at the given world position.
    /// pcmData is 16-bit signed, mono, 48kHz.
    /// </summary>
    void PlayVoice(int senderId, Vector3 position, byte[] pcmData);

    /// <summary>
    /// Makes a buffer the game synthesised available under a sound id.
    ///
    /// The bridge between physical modelling and everything else: once registered, a rendered door
    /// latch is an ordinary sound id, so it is placed, attenuated, occluded and reverberated by the
    /// same path that handles a recording, and none of that path needs to know nobody recorded it.
    /// </summary>
    bool RegisterSynthesisedSound(string soundId, byte[] pcm16Mono, int sampleRate);

    /// <summary>
    /// Plays a short synthesized sine tone at a given frequency as a non-spatial UI sound.
    /// Used for voice-transmission indicators and accessibility cues.
    /// </summary>
    void PlayUiBeep(float frequencyHz, float durationMs);

    // --- Diagnostics (Step 1a): an isolated mono source for verifying HRTF / 3D panning. ---
    void StartDiagnosticSound();
    void SetDiagnosticPosition(Vector3 position);
    void StopDiagnosticSound();
}
