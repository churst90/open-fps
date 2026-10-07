using System.Numerics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.AudioEngine.Fmod;

public interface IAudioProvider : IDisposable
{
    bool Initialize();
    void Update();

    /// <summary>Mean and worst time of one attribute pass since the last read, the passes and the voices
    /// each walked; reading resets the window. The period is what makes a pass-by glide instead of step
    /// (AudioEngineFacade's loop), so this checks it is met.</summary>
    (double MeanMs, double MaxMs, int Calls, int Voices) TakeUpdateCost() => (0, 0, 0, 0);
    void UpdateListener(Vector3 position, Quaternion rotation, Vector3 velocity, int regionId);
    void UpdateShelter(float shelterFactor);
    /// <summary>What the body of the vehicle the listener sits in takes off everything outside it, dB per
    /// band (negative); zero on foot.</summary>
    void SetListenerEnclosure(float lowDb, float midDb, float highDb) { }
    /// <summary>For the wind at the ears (EarWind); null when nobody is in a world.</summary>
    void SetEarWind(OpenFPS.Common.EarWindListener? listener) { }
    /// <summary>The surfaces right round the listener's head, one probe per direction in head space, each
    /// rendered as its own early reflection.</summary>
    void UpdateBoundaries(ReadOnlySpan<BoundaryProbe> probes);

    /// <summary>
    /// Starts an ambisonic ambience bed, or re-aims a playing one at a new level. The soundfield is fixed
    /// in the world, rotated by the listener and decoded binaurally every block. False (said in the log)
    /// when the file is not a full-sphere ambisonic recording or Steam Audio cannot decode it.
    /// </summary>
    bool PlayAmbientBed(string soundId, AmbisonicLayout layout, float volume, bool loop = true);

    /// <summary>Stops a bed and frees its decoder.</summary>
    void StopAmbientBed(string soundId);
    void SetAcousticMap(OpenFPS.Common.AcousticMap map);
    void PlaySpatialSound(SpatialEmitter emitter);
    void UpdateSpatialAttributes(SpatialEmitter emitter);
    void SetAcousticPath(int entityId, AcousticPathData path);

    /// <summary>
    /// The listener's surveyed reverb decay, ms (0: keep the Sabine estimate), with
    /// <paramref name="enclosure"/>, 0 (open field) to 1 (sealed box). They arrive together: the decay
    /// says how long a tail would last and the enclosure whether there is one, and a long decay where
    /// nothing comes back put a cathedral over an open racetrack (OpenFPS.Common.Enclosure). The
    /// FMOD provider does not read <paramref name="hfDecayRatio"/> or <paramref name="lfDecayRatio"/>.
    /// </summary>
    void SetSimulatedReverbDecay(float decayMs, float enclosure, float hfDecayRatio, float lfDecayRatio);

    /// <summary>°C, for the speed of sound in the Doppler (c = 331.3 + 0.606·T).</summary>
    void SetAirTemperature(float celsius);
    /// <summary>0..1+; 1 is the callback using its whole deadline.</summary>
    float MixerLoad { get; }

    /// <summary>
    /// Binaural (HRTF) voices still free: what a voice budget really spends. A voice without one still
    /// plays, flat, with no position, which on a map navigated by ear is worse than silence.
    /// </summary>
    int SpatialVoicesFree { get; }

    /// <summary>Brings a live engine voice back after a fade-out was started. Idempotent.</summary>
    void ReviveEngine(int entityId);

    /// <summary>Asks a live engine voice to fade out; true once it is silent and safe to stop.
    /// True also when there is no such voice, so "gone" and "never existed" look the same.</summary>
    bool FadeOutEngine(int entityId);

    /// <summary>
    /// Takes any voice to silence over about eighty milliseconds by the channel's gain (FadeOutEngine
    /// slews the synthesis instead); true once silent, or when there is no such voice. The budget's way
    /// of letting go of a continuous source.
    /// </summary>
    bool FadeOutVoice(int entityId);

    /// <summary>Brings one back after a fade was started. Idempotent.</summary>
    void CancelVoiceFade(int entityId);

    /// <summary>Whether a vehicle voice has its doors open (a bus at a stop): the voice decides from its
    /// own speed history.</summary>
    bool EngineDoorsOpen(int entityId) => false;
    /// <summary>A train ("preset/train") sounds its horn or whistle in this rhythm and rings its bell
    /// for this long, begun <paramref name="secondsAgo"/> before now (TrainSignal). Played by the train's
    /// own synth, on its own outlets.</summary>
    void SignalTrain(string train, float[] warning, float bellSeconds, double secondsAgo) { }
    /// <summary>
    /// A car engine's told road speed, its own driveline's speed, its crank speed and gear. "The cars
    /// sound like they are slowing down" has four causes that sound alike (they are, the world reports
    /// too low, the driver is not holding speed, the driver shifts up); these four numbers tell them apart.
    /// </summary>
    bool TryGetEngineTelemetry(int entityId, out float toldSpeed, out float ownSpeed, out float rpm, out int gear)
    {
        toldSpeed = ownSpeed = rpm = 0f; gear = 0; return false;
    }

    /// <summary>For the log: what a vehicle voice puts out and what the mix does with it, stage by stage
    /// (the voice's own output, its envelope and idle lift, the channel's volume and FMOD's audibility).
    /// Empty when there is no such voice.</summary>
    string EngineVoiceDetail(int entityId) => "";
    void StopSound(int entityId);
    bool IsPlaying(int entityId);
    Vector3 GetSoundPosition(int entityId);
    IEnumerable<int> GetActiveSpatialSoundIds();
    void Preload(string soundId);

    /// <summary>Decodes a bank sound to interleaved float PCM without keeping it.</summary>
    bool TryDecode(string soundId, out float[] pcm, out int channels, out int sampleRate)
    {
        pcm = Array.Empty<float>(); channels = 0; sampleRate = 0; return false;
    }

    /// <summary>
    /// Makes a synthesised buffer an ordinary sound id, placed, occluded and reverberated by the same
    /// path as a recording.
    /// </summary>
    bool RegisterSynthesisedSound(string soundId, byte[] pcm16Mono, int sampleRate);

    /// <summary>
    /// Lets go of a buffer from <see cref="RegisterSynthesisedSound"/> once nothing plays it (releasing
    /// stops every channel still playing it): for one-off renders such as a strike's thunder.
    /// </summary>
    bool ReleaseSynthesisedSound(string soundId) => false;

    /// <summary>
    /// <see cref="RegisterSynthesisedSound"/> in 32-bit float, for a render with a wide range (thunder's
    /// crack, then a minute of rumble 40-60 dB under it), whose quiet end sixteen bits turn to crackle
    /// and stretches of exact silence. Sixteen bits where a provider has no float path.
    /// </summary>
    bool RegisterSynthesisedSoundFloat(string soundId, float[] pcm, int sampleRate)
    {
        var bytes = new byte[pcm.Length * 2];
        for (int i = 0; i < pcm.Length; i++)
        {
            short v = (short)Math.Clamp(pcm[i] * 32767f, short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return RegisterSynthesisedSound(soundId, bytes, sampleRate);
    }

    /// <summary>An interface sound in both ears: no position, no room. The buffer is made once per id
    /// and kept; <paramref name="volume"/> is 0..1.</summary>
    void PlayUiSound(string id, Func<float[]> render, int sampleRate, float volume);
    /// <summary>
    /// Keeps an interface loop playing in a named slot (the scope's guidance tone). The same
    /// <paramref name="id"/> again only changes <paramref name="volume"/> and <paramref name="pitch"/>; a
    /// different one cross-fades, so changing never clicks.
    /// </summary>
    void SetUiLoop(string slot, string id, Func<float[]> render, int sampleRate, float volume, float pitch) { }
    /// <summary>Fades the loop in a slot out and stops it.</summary>
    void StopUiLoop(string slot) { }
    /// <summary>Everything but the interface sounds, 0..1; 1 is as authored.</summary>
    void SetWorldFade(float gain) { }

    /// <summary>True when a microphone is connected.</summary>
    bool HasRecordingDevice => false;
    /// <summary>From the named input device (empty: the default). False when there is none or it would
    /// not open.</summary>
    bool StartRecording(string deviceName, out int sampleRate) { sampleRate = 0; return false; }
    /// <summary>Appends what was recorded since the last call, mixed to mono; returns how many.</summary>
    int ReadRecording(List<float> mono) => 0;
    void StopRecording() { }
    /// <summary>The voices reaching the listener loudest, loudest first. Diagnostic.</summary>
    IReadOnlyList<VoiceLevel> LoudestVoices(int count);
    /// <summary>In driver order.</summary>
    IReadOnlyList<string> OutputDevices();
    IReadOnlyList<string> InputDevices();
    /// <summary>"" for the system default. False, and nothing changes, if there is no such device.</summary>
    bool SetOutputDevice(string name);

    // An isolated mono source for checking HRTF and 3D panning by ear (AudioDiagnostics).
    void StartDiagnosticSound();
    void SetDiagnosticPosition(Vector3 position);
    void StopDiagnosticSound();
}

/// <summary>One voice as it reaches the listener: dBFS in its loudest band, how much is blocked, each band's
/// level (distance, occlusion, air, shelter and cone applied), and whether it is a reflection or arrives
/// from somewhere other than its source (round an edge).</summary>
public readonly record struct VoiceLevel(int EntityId, string SoundId, float Distance, float Db, float Occlusion,
                                         float Low, float Mid, float High, bool Reflection, bool Redirected);
