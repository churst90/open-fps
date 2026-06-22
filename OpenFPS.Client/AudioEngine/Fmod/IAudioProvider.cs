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
}
