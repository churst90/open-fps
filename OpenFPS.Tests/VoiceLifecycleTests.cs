using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Tests;

/// <summary>
/// That every voice which can be started can also be stopped.
///
/// This is the bug that made a whole map unusable, and it was invisible everywhere except in a
/// running game. Voices come into existence by two routes: submitted to the VoiceManager, which
/// scores them and decides what deserves a channel, or played DIRECTLY — which is what reflections
/// and echoes do, because a reflection should follow its source rather than compete with it for a
/// slot. Stopping went down one route only. A directly-played voice was asked to stop, the manager
/// did not recognise the id, and the request was dropped.
///
/// Nothing failed. The voice simply never stopped, and on a racetrack — where each car has a
/// reflection voice against each wall, and cars come and go from the mix constantly — the count ran
/// from 24 to 123 in under a minute with the mixer at 100%, which is heard as the entire soundscape
/// crackling and stuttering.
/// </summary>
public class VoiceLifecycleTests
{
    /// <summary>Records what actually reached the provider. Everything else is a no-op.</summary>
    private sealed class RecordingProvider : IAudioProvider
    {
        public readonly List<int> Played = new();
        public readonly List<int> Stopped = new();
        public readonly HashSet<int> Live = new();

        public bool Initialize() => true;
        public void Update() { }
        public void UpdateListener(Vector3 p, Quaternion r, Vector3 v, int region) { }
        public void UpdateShelter(float f) { }
        public void UpdateBoundaries(ReadOnlySpan<BoundaryProbe> probes) { }
        public bool PlayAmbientBed(string id, AmbisonicLayout l, float v, bool loop = true) => true;
        public void SetAmbientBedVolume(string id, float v) { }
        public void StopAmbientBed(string id) { }
        public void SetAcousticMap(AcousticMap map) { }
        public void PlaySpatialSound(SpatialEmitter e) { Played.Add(e.EntityId); Live.Add(e.EntityId); }
        public void UpdateSpatialAttributes(SpatialEmitter e) { }
        public void SetAcousticPath(int id, AcousticPathData p) { }
        public void SetSimulatedReverbDecay(float ms, float enclosure, float hf, float lf) { }
        public void SetListenerReverbField(Vector3 returnDirection, float anisotropy, float meanFreePathMetres, float surfaceAreaSquareMetres = 0f) { }
        public void SetAirTemperature(float c) { }
        public float MixerLoad => 0f;
        public void ReviveEngine(int id) { }
        public bool FadeOutEngine(int id) => true;
        public int SpatialVoicesFree => 96;
        public bool FadeOutVoice(int id) => true;
        public void CancelVoiceFade(int id) { }
        public void StopSound(int id) { Stopped.Add(id); Live.Remove(id); }
        public bool IsPlaying(int id) => Live.Contains(id);
        public Vector3 GetSoundPosition(int id) => Vector3.Zero;
        public float GetPlaybackProgress(int id) => 0f;
        public IEnumerable<int> GetActiveSpatialSoundIds() => new List<int>(Live);
        public void Preload(string id) { }
        public void PlayVoice(int sender, Vector3 pos, byte[] pcm) { }

        /// <summary>Records nothing: this fake has no FMOD behind it to hand a buffer to, and
        /// every test here is about voice LIFECYCLE rather than about what a voice sounds like.</summary>
        public bool RegisterSynthesisedSound(string soundId, byte[] pcm16Mono, int sampleRate) => true;
        public void PlayUiBeep(float hz, float ms) { }
        public void StartDiagnosticSound() { }
        public void SetDiagnosticPosition(Vector3 p) { }
        public void StopDiagnosticSound() { }
        public void Dispose() { }
    }

    private static SpatialEmitter Echo(int id) => new()
    {
        EntityId = id, SoundId = "engine-echo", IsSynth = true, EngineKey = "",
        Type = EmitterType.WorldLocked, Mode = PlaybackMode.LoopOne,
        Position = new Vector3(10, 0, 0), Volume = 1f, Range = 100f, MinDistance = 3f, Pitch = 1f,
    };

    [Fact]
    public void ADirectlyPlayedVoiceCanBeStopped()
    {
        var provider = new RecordingProvider();
        var facade = new AudioEngineFacade(provider);
        facade.InitializeForTest();

        const int echoId = -600001;
        facade.PlayPhysicalSoundDirect(Echo(echoId));
        Pump(facade);
        Assert.Contains(echoId, provider.Played);
        Assert.True(provider.IsPlaying(echoId));

        facade.StopSound(echoId);
        Pump(facade);

        Assert.True(provider.Stopped.Contains(echoId),
            "a voice played directly was never stopped — this is the leak that pegged the mixer at 100%");
        Assert.False(provider.IsPlaying(echoId));
    }

    /// <summary>
    /// The shape of the failure, rather than one instance of it: start and stop many direct voices
    /// the way a track full of cars does, and the number still alive must not grow.
    /// </summary>
    [Fact]
    public void DirectVoicesDoNotAccumulateOverManyStartsAndStops()
    {
        var provider = new RecordingProvider();
        var facade = new AudioEngineFacade(provider);
        facade.InitializeForTest();

        int id = -600000;
        for (int round = 0; round < 40; round++)
        {
            var batch = new List<int>();
            for (int i = 0; i < 8; i++) { batch.Add(--id); facade.PlayPhysicalSoundDirect(Echo(id)); }
            Pump(facade);
            foreach (int v in batch) facade.StopSound(v);
            Pump(facade);
            Assert.True(provider.Live.Count == 0,
                $"round {round}: {provider.Live.Count} voice(s) still alive after every one was stopped");
        }
    }

    /// <summary>Pushes the facade's queues through, which its audio thread would otherwise do.</summary>
    private static void Pump(AudioEngineFacade facade)
    {
        for (int i = 0; i < 3; i++) facade.PumpForTest();
    }
}
