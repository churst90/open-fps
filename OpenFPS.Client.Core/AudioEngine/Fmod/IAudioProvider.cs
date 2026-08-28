using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Data;

namespace OpenFPS.Client.AudioEngine.Fmod;

public interface IAudioProvider : IDisposable
{
    bool Initialize();
    void Update();
    void UpdateListener(Vector3 position, Quaternion rotation, Vector3 velocity, int regionId);
    void UpdateShelter(float shelterFactor);
    void UpdateProximity(float nearestWallDistance);
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
