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
/// A whole ClientAudioSystem with no sound card: the real facade, voice budget and acoustic worker over
/// a mixer that only records, a client world with cars in it, and a clock the test turns. Coordinates
/// are the engine's: x east, y up, z north.
/// </summary>
internal sealed class ClientAudioHarness
{
    public readonly RecordingMixer Mixer = new();
    public readonly AudioEngineFacade Facade;
    public readonly ClientWorldState World = new();
    public readonly LocalPlayerState Player = new();
    public readonly ClientAudioSystem Audio;

    /// <summary>The system's clock, seconds. Advanced one audio frame per <see cref="Tick()"/>.</summary>
    public double Now { get; private set; } = 1.0;

    /// <param name="soundsPath">A sound bank to resolve recorded sounds from, such as footsteps. None by
    /// default: the synthesised voices need no files.</param>
    public ClientAudioHarness(string? soundsPath = null)
    {
        World.Clear(new Vector3(4000, 400, 4000));
        Facade = new AudioEngineFacade(Mixer);
        Facade.InitializeForTest(soundsPath);
        var sounds = new SoundMappingService(Player);
        if (soundsPath != null) sounds.Initialize(soundsPath);
        // No door prewarm: with the render cache empty (TestConfigIsolation) every harness rendered the
        // whole city's doors and starved the tests' own threads.
        Audio = new ClientAudioSystem(Facade, sounds, Player, () => Now, prewarm: false);
    }

    /// <summary>Stands the listener here, feet on the ground (the ear is EyeHeight above).</summary>
    public void StandAt(Vector3 feet) => Player.Position = feet;

    /// <summary>A car as the server puts one (VehicleSystem, CompositeService): an engine emitter on a
    /// moving entity, its position arriving as state.</summary>
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

    /// <summary>A solid box that stays where it is: a wall, as a map's static definitions give one.</summary>
    public void AddWall(int id, Vector3 centre, Vector3 size, string material = "Brick")
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
            Material = new MaterialComponent { Material = material },
        };
        World.RegisterDefinition(def);
    }

    /// <summary>The server's word that this car sounded its horn.</summary>
    public void Honk(int carId, string horn = "electric:disc_pair", float seconds = 30f)
        => Audio.WorldAudio.HornReceived!(carId, horn, new[] { seconds });

    /// <summary>One audio frame: the update, then the facade's audio thread, pumped by hand.</summary>
    public void Tick()
    {
        Now += 1.0 / ClientAudioSystem.UpdateHz;
        Audio.Update(World.GetSnapshot());
        // Twice: a submitted voice is queued as a direct play, which the next pump hands to the mixer.
        Facade.PumpForTest();
        Facade.PumpForTest();
    }

    /// <summary>Lets time pass with no update in between — the engine hold, for instance.</summary>
    public void Wait(double seconds) => Now += seconds;

    public void Tick(int frames)
    {
        for (int i = 0; i < frames; i++) Tick();
    }

    /// <summary>Ticks until the condition holds, giving the acoustic worker thread a moment each frame.
    /// True if it held within the limit.</summary>
    public bool TickUntil(Func<bool> condition, int maxFrames = 600)
        => TickUntil(condition, maxFrames, TimeSpan.Zero);

    /// <summary>As <see cref="TickUntil(Func{bool}, int)"/>, but ticks past maxFrames until
    /// <paramref name="atLeast"/> of wall time has gone: a background worker (the rain survey) is far
    /// slower on a loaded two-core CI runner.</summary>
    public bool TickUntil(Func<bool> condition, int maxFrames, TimeSpan atLeast)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < maxFrames || clock.Elapsed < atLeast; i++)
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

/// <summary>A mixer that records everything asked of it, keyed by voice id, and plays nothing. Called
/// only from the thread that pumps the facade: here, the test's own.</summary>
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
    public void StopAmbientBed(string id) { }
    public void SetAcousticMap(AcousticMap map) { }
    public void SetSimulatedReverbDecay(float ms, float enclosure, float hf, float lf) { }
    public void SetAirTemperature(float c) { }
    /// <summary>The mixer's load as the budget reads it, 0..1. A test sets it to drive the control loop.</summary>
    public float Load;
    public float MixerLoad => Load;
    public void ReviveEngine(int id) { }
    public bool FadeOutEngine(int id) => true;
    public int SpatialVoicesFree => 256;
    public bool FadeOutVoice(int id) => true;
    public void CancelVoiceFade(int id) { }
    public Vector3 GetSoundPosition(int id) => Latest.TryGetValue(id, out var e) ? e.Position : Vector3.Zero;
    public void Preload(string id) { }
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
