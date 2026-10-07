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
    /// <summary>What the body of the vehicle the listener is sitting in takes off everything outside
    /// it, dB per band (negative). Zero when on foot.</summary>
    void SetListenerEnclosure(float lowDb, float midDb, float highDb) { }
    /// <summary>Where the listener is, for the wind at their ears (EarWind); null when nobody is in a
    /// world.</summary>
    void SetEarWind(OpenFPS.Common.EarWindListener? listener) { }
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
    /// <summary>Whether a vehicle voice has its doors standing open (a bus at a stop). The voice decides
    /// that from its own speed history, so it is the one to ask.</summary>
    bool EngineDoorsOpen(int entityId) => false;
    /// <summary>A train ("preset/train") sounds its horn or whistle in this rhythm and rings its bell
    /// for this long, begun <paramref name="secondsAgo"/> before now (TrainSignal). Played by the train's
    /// own synth, on its own outlets.</summary>
    void SignalTrain(string train, float[] warning, float bellSeconds, double secondsAgo) { }
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

    /// <summary>
    /// Decodes a sound from the bank to interleaved float PCM, without keeping it. False when there is
    /// no such sound or nothing to decode it with.
    /// </summary>
    bool TryDecode(string soundId, out float[] pcm, out int channels, out int sampleRate)
    {
        pcm = Array.Empty<float>(); channels = 0; sampleRate = 0; return false;
    }

    /// <summary>
    /// Makes a buffer the game synthesised available under a sound id.
    ///
    /// The bridge between physical modelling and everything else: once registered, a rendered door
    /// latch is an ordinary sound id, so it is placed, attenuated, occluded and reverberated by the
    /// same path that handles a recording, and none of that path needs to know nobody recorded it.
    /// </summary>
    bool RegisterSynthesisedSound(string soundId, byte[] pcm16Mono, int sampleRate);

    /// <summary>
    /// Lets go of a buffer registered by <see cref="RegisterSynthesisedSound"/>, once nothing is
    /// playing it: releasing a sound stops every channel still playing it. For one-off renders that
    /// will never be asked for again (a strike's thunder, worked out for one listener), which would
    /// otherwise stay in memory for the rest of the session. False if there was nothing to release.
    /// </summary>
    bool ReleaseSynthesisedSound(string soundId) => false;

    /// <summary>
    /// The same as <see cref="RegisterSynthesisedSound"/>, kept in 32-bit float. For a long render with
    /// a wide range in it (a strike's thunder: a crack and then a minute of rumble 40-60 dB under it),
    /// whose quiet end sixteen bits would leave as a few steps of the last bit: heard as crackle and
    /// as stretches of exact silence. Falls back to sixteen bits where a provider has no float path.
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

    /// <summary>Plays a short interface sound in both ears, not in the world: no position, no room.
    /// The buffer is made once per id and kept; <paramref name="volume"/> is 0..1.</summary>
    void PlayUiSound(string id, Func<float[]> render, int sampleRate, float volume);
    /// <summary>
    /// Keeps an interface LOOP playing in a named slot (the scope's guidance tone): the loop
    /// <paramref name="id"/> at <paramref name="volume"/> and playback rate <paramref name="pitch"/>.
    /// The same id again only changes the volume and rate; a different id fades the old loop out and
    /// the new one in, so changing between them never clicks.
    /// </summary>
    void SetUiLoop(string slot, string id, Func<float[]> render, int sampleRate, float volume, float pitch) { }
    /// <summary>Fades the loop in a slot out and stops it.</summary>
    void StopUiLoop(string slot) { }
    /// <summary>Fades the world (everything but the interface sounds), 0..1. 1 is as authored.</summary>
    void SetWorldFade(float gain) { }

    /// <summary>True when a microphone is connected.</summary>
    bool HasRecordingDevice => false;
    /// <summary>Starts recording from the named input device (empty: the default). Gives the rate it
    /// records at. False when there is no device or it would not open.</summary>
    bool StartRecording(string deviceName, out int sampleRate) { sampleRate = 0; return false; }
    /// <summary>Appends what has been recorded since the last call, mixed to mono, -1..1. Returns how many.</summary>
    int ReadRecording(List<float> mono) => 0;
    /// <summary>Stops recording and lets the device go.</summary>
    void StopRecording() { }
    /// <summary>The voices reaching the listener loudest, most first. Diagnostic.</summary>
    IReadOnlyList<VoiceLevel> LoudestVoices(int count);
    /// <summary>The output devices the system offers, by name, in driver order.</summary>
    IReadOnlyList<string> OutputDevices();
    /// <summary>The recording devices the system offers, by name.</summary>
    IReadOnlyList<string> InputDevices();
    /// <summary>Switches output to the named device ("" for the system default). False if there is no
    /// such device, in which case nothing changes.</summary>
    bool SetOutputDevice(string name);

    // --- Diagnostics (Step 1a): an isolated mono source for verifying HRTF / 3D panning. ---
    void StartDiagnosticSound();
    void SetDiagnosticPosition(Vector3 position);
    void StopDiagnosticSound();
}

/// <summary>One voice as it reaches the listener: its level in dB full scale in its loudest band, how much
/// is blocked, the level in each of the low/mid/high bands (distance, occlusion, air, shelter and cone
/// all applied), and whether it is a reflection or arriving from somewhere other than its source (round
/// an edge).</summary>
public readonly record struct VoiceLevel(int EntityId, string SoundId, float Distance, float Db, float Occlusion,
                                         float Low, float Mid, float High, bool Reflection, bool Redirected);
