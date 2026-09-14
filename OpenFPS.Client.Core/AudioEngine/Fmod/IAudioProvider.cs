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

    /// <summary>Overrides the listener-region reverb decay (FMOD SFXREVERB ms) with a geometry-derived
    /// value from the Steam Audio reflection simulation. 0 = no override (keep the Sabine estimate).</summary>
    void SetSimulatedReverbDecay(float decayMs);

    /// <summary>Sets the air temperature (°C) the Doppler math uses for the speed of sound. This is how
    /// the simulated weather reaches the mix: c = 331.3 + 0.606·T.</summary>
    void SetAirTemperature(float celsius);
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
    /// Plays a short synthesized sine tone at a given frequency as a non-spatial UI sound.
    /// Used for voice-transmission indicators and accessibility cues.
    /// </summary>
    void PlayUiBeep(float frequencyHz, float durationMs);

    // --- Diagnostics (Step 1a): an isolated mono source for verifying HRTF / 3D panning. ---
    void StartDiagnosticSound();
    void SetDiagnosticPosition(Vector3 position);
    void StopDiagnosticSound();
}
