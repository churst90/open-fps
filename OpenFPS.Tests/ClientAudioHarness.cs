using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Tests;

/// <summary>
/// A whole ClientAudioSystem with no sound card: a real facade over a mixer that only writes down
/// what it was asked to do, a real client world with cars in it, and a clock the test turns.
///
/// ClientAudioSystem is where it is decided which voices play, where, at what level and with which
/// acoustic path, and until this existed none of that could be asked about outside a running game.
/// Everything below it is real — the voice budget, the acoustic worker thread, the facade's queues —
/// so a test here sees what the mixer would have been told.
///
/// Coordinates are the engine's: x east, y UP, z north.
/// </summary>
internal sealed class ClientAudioHarness
{
    public readonly RecordingMixer Mixer = new();
    public readonly AudioEngineFacade Facade;
    public readonly ClientWorldState World = new();
    public readonly LocalPlayerState Player = new();
    public readonly ClientAudioSystem Audio;

    /// <summary>The system's clock, seconds. Advanced one audio frame per <see cref="Tick"/>.</summary>
    public double Now { get; private set; } = 1.0;

    public ClientAudioHarness()
    {
        World.Clear(new Vector3(4000, 400, 4000), new Vector3(-2000, -100, -2000), new Vector3(2000, 300, 2000));
        Facade = new AudioEngineFacade(Mixer);
        Facade.InitializeForTest();
        Audio = new ClientAudioSystem(Facade, new SoundMappingService(Player), Player, () => Now);
    }

    /// <summary>Stands the listener here, feet on the ground (the ear is EyeHeight above).</summary>
    public void StandAt(Vector3 feet) => Player.Position = feet;

    /// <summary>
    /// Puts a car on the map the way the server does (VehicleSystem / CompositeService): an engine
    /// emitter on a moving entity, and a position that arrives as state.
    /// </summary>
    public void AddCar(int id, string preset, Vector3 position, Vector3 velocity = default)
    {
        var profile = MachineRegistry.VehicleFor(preset);
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.NPC,
            Moves = true,
            Transform = new Transform { Position = position, Rotation = Quaternion.Identity },
        };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = "engine:" + preset;
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.Range = Loudness.AudibleRange(profile.SourceLevelDb);
        def.SoundEmitter.MinDistance = 3f;
        World.RegisterDefinition(def);
        World.SyncState(new[] { new EntityState
        {
            EntityId = id,
            Transform = QuantizedTransform.FromTransform(def.Transform),
            LinearVelocity = velocity,
        } });
    }

    /// <summary>The server's word that this car sounded its horn.</summary>
    public void Honk(int carId, string horn = "electric:disc_pair", float seconds = 30f)
        => Audio.WorldAudio.HornReceived!(carId, horn, new[] { seconds });

    /// <summary>One audio frame: the update, then the facade's audio thread, pumped by hand.</summary>
    public void Tick()
    {
        Now += 1.0 / ClientAudioSystem.UpdateHz;
        Audio.Update(World.GetSnapshot());
        // Twice: the budget plays a submitted voice by queueing a direct play, which the next pump
        // hands to the mixer.
        Facade.PumpForTest();
        Facade.PumpForTest();
    }

    /// <summary>Lets time pass with no update in between — the engine hold, for instance.</summary>
    public void Wait(double seconds) => Now += seconds;

    public void Tick(int frames)
    {
        for (int i = 0; i < frames; i++) Tick();
    }

    /// <summary>
    /// Ticks until the condition holds, giving the acoustic worker (a real thread) a moment each
    /// frame. True if it held within the limit.
    /// </summary>
    public bool TickUntil(Func<bool> condition, int maxFrames = 600)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            Tick();
            if (condition()) return true;
            System.Threading.Thread.Sleep(2);
        }
        return false;
    }

    /// <summary>The id a car's borrowed voice plays under.</summary>
    public static int DistantVoice(int carId) => ClientAudioSystem.DistantVoiceBase - Math.Abs(carId);
    /// <summary>The id a car's horn plays under.</summary>
    public static int HornVoice(int carId) => ClientAudioSystem.HornVoiceBase - Math.Abs(carId);
}

/// <summary>
/// A mixer that records everything asked of it, keyed by voice id. It plays nothing.
///
/// Called only from the thread that pumps the facade, which in these tests is the test's own.
/// </summary>
internal sealed class RecordingMixer : IAudioProvider
{
    /// <summary>Every voice started, in order.</summary>
    public readonly List<SpatialEmitter> Started = new();
    /// <summary>The last emitter each voice was started or re-placed with.</summary>
    public readonly Dictionary<int, SpatialEmitter> Latest = new();
    /// <summary>Every acoustic path handed to each voice, in order.</summary>
    public readonly Dictionary<int, List<AcousticPathData>> Paths = new();
    public readonly List<int> Stopped = new();
    public readonly HashSet<int> Live = new();

    public bool HasPath(int id) => Paths.ContainsKey(id);
    public AcousticPathData LastPath(int id) => Paths[id][^1];
    public bool WasStarted(int id) => Started.Exists(e => e.EntityId == id);

    public void PlaySpatialSound(SpatialEmitter e) { Started.Add(e); Latest[e.EntityId] = e; Live.Add(e.EntityId); }
    public void UpdateSpatialAttributes(SpatialEmitter e) => Latest[e.EntityId] = e;
    public void SetAcousticPath(int id, AcousticPathData p)
    {
        if (!Paths.TryGetValue(id, out var list)) Paths[id] = list = new List<AcousticPathData>();
        list.Add(p);
    }
    public void StopSound(int id) { Stopped.Add(id); Live.Remove(id); }
    public bool IsPlaying(int id) => Live.Contains(id);
    public IEnumerable<int> GetActiveSpatialSoundIds() => new List<int>(Live);

    public bool Initialize() => true;
    public void Update() { }
    public void UpdateListener(Vector3 p, Quaternion r, Vector3 v, int region) { }
    public void UpdateShelter(float f) { }
    public void UpdateBoundaries(ReadOnlySpan<BoundaryProbe> probes) { }
    public bool PlayAmbientBed(string id, AmbisonicLayout l, float v, bool loop = true) => true;
    public void SetAmbientBedVolume(string id, float v) { }
    public void StopAmbientBed(string id) { }
    public void SetAcousticMap(AcousticMap map) { }
    public void SetSimulatedReverbDecay(float ms, float enclosure, float hf, float lf) { }
    public void SetListenerReverbField(Vector3 returnDirection, float anisotropy, float meanFreePathMetres, float surfaceAreaSquareMetres = 0f) { }
    public void SetAirTemperature(float c) { }
    public float MixerLoad => 0f;
    public void ReviveEngine(int id) { }
    public bool FadeOutEngine(int id) => true;
    public int SpatialVoicesFree => 256;
    public bool FadeOutVoice(int id) => true;
    public void CancelVoiceFade(int id) { }
    public Vector3 GetSoundPosition(int id) => Latest.TryGetValue(id, out var e) ? e.Position : Vector3.Zero;
    public float GetPlaybackProgress(int id) => 0f;
    public void Preload(string id) { }
    public void PlayVoice(int sender, Vector3 pos, byte[] pcm) { }
    public bool RegisterSynthesisedSound(string soundId, byte[] pcm16Mono, int sampleRate) => true;
    public void PlayUiSound(string id, Func<float[]> render, int sampleRate, float volume) { }
    public IReadOnlyList<VoiceLevel> LoudestVoices(int count) => Array.Empty<VoiceLevel>();
    public IReadOnlyList<string> OutputDevices() => new[] { "Test output" };
    public IReadOnlyList<string> InputDevices() => new[] { "Test input" };
    public bool SetOutputDevice(string name) => true;
    public void StartDiagnosticSound() { }
    public void SetDiagnosticPosition(Vector3 p) { }
    public void StopDiagnosticSound() { }
    public void Dispose() { }
}
