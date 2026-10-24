using System.Diagnostics;
using System.Numerics;
using OpenFPS.Acoustics;
using OpenFPS.Audio.Acoustics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Data;
using OpenFPS.Audio.Fmod;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Environment;
using OpenFPS.Geometry;
using OpenFPS.Materials;
using OpenFPS.Models;
using OpenFPS.Sound;
using OpenFPS.Sound.Core;
using OpenFPS.Client.Services;
using Serilog;

namespace OpenFPS.Client.Core;

/// <summary>
/// Renders every kind of voice once while the player is at the login screen, on a small made-up scene, and
/// throws the sound away: the first engine, machine, train, fire or bird in the city is then not the first
/// one the process has ever made. It runs on a mixer of its own that writes a temporary WAV file, so none of
/// it reaches the live mix or the speakers and nothing of it is left in the live audio world. A login does
/// not wait for it; <see cref="Stop"/> lets the map's load go on while it winds down.
/// </summary>
public sealed class AudioWarmUp
{
    /// <summary>How much world time the scene is run for, seconds. Birds call on their own schedule, so
    /// this is longer than the audio itself needs.</summary>
    private const double SceneSeconds = 12.0;

    /// <summary>The most wall time this may take before it stops itself, seconds: a login should never
    /// wait on it, and a warm-up that has not finished by now has already warmed what it can.</summary>
    private const double WallLimitSeconds = 20.0;

    private readonly AudioDataPaths _paths;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    public AudioWarmUp(AudioDataPaths paths) => _paths = paths;

    /// <summary>Starts the warm-up on a background thread. Called once, at the login screen.</summary>
    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(() => RunToCompletion()) { IsBackground = true, Name = "AudioWarmUp" };
        _thread.Start();
    }

    /// <summary>The map is arriving: the warm-up is no longer the best use of the machine, so it stops
    /// at its next step and leaves its own world behind. The load is never held up by it.</summary>
    public void Stop() => _stop.Cancel();

    /// <summary>Waits for the warm-up's thread to end, which includes its own mixer's dispose. False, and logged, when it
    /// is still running after <paramref name="ms"/>.</summary>
    public bool Join(int ms)
    {
        var thread = _thread;
        if (thread == null || !thread.IsAlive || thread == Thread.CurrentThread) return true;
        if (thread.Join(ms)) return true;
        Log.Warning("Audio shutdown: AudioWarmUp did not stop within {Ms} ms.", ms);
        return false;
    }

    /// <summary>
    /// Runs the whole warm-up on the calling thread (the lab's warm= option, and a test), so its cost can be
    /// measured. True when it finished its scene; false when <see cref="Stop"/> cut it short.
    /// </summary>
    public bool RunToCompletion()
    {
        var wall = Stopwatch.StartNew();
        var warmed = new SortedSet<string>(StringComparer.Ordinal);
        // The scene's mixer writes here and nowhere else: FMOD's WAV writer replaces the sound card, so a
        // warm voice cannot reach the player's ears. The file is deleted on the way out.
        string wav = Path.Combine(Path.GetTempPath(), $"openfps-warmup-{System.Environment.ProcessId}-{Guid.NewGuid():N}.wav");
        bool finished = false;
        bool binaural = false;
        try
        {
            // The bank, the models and the material table are the first-time work too, and the city's own
            // systems would otherwise make them again in the load.
            AcousticRegistry.Initialize();
            MachineRegistry.EnsureLoaded();
            ModelLibrary.EnsureLoaded();

            var clock = new WarmClock();
            // The worker's Steam Audio simulator is left off: building its scene and traces is the native
            // crash --city-load still sees (docs/AUDIO_WARM_START.md, part 3a), and a login must not risk it.
            // The mixer's HRTF stages and the room's reverb bus are still made and run; the city simulates.
            var provider = new FmodAudioProvider { Clock = clock, Lab = new LabLevers { WithoutSteamAudio = true }, WavPath = wav };
            var facade = new AudioEngineFacade(provider, paths: _paths);
            try
            {
                facade.Initialize();
                if (!facade.IsInitialized) { Log.Warning("Warm-up: the audio engine did not start; nothing was warmed."); return false; }
                binaural = facade.SpatialVoicesFree > 0;

                var player = new LocalPlayerState { Position = new Vector3(0f, 0f, 0f) };
                var mapping = new SoundMappingService(player);
                mapping.Initialize(_paths.Sounds);
                var world = Scene();
                world.Clock = facade.Clock;
                world.Hearing = facade.Hearing;
                var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Now, prewarm: false);

                finished = RunScene(audio, player, world, clock, wall, warmed);
            }
            finally
            {
                try { facade.Dispose(); } catch (Exception ex) { Log.Debug(ex, "Warm-up: the mixer did not stop cleanly."); }
            }
        }
        catch (Exception ex)
        {
            // A warm-up that fails is a slower first load, never a failed login.
            Log.Warning(ex, "Warm-up failed; the city will make its first voices itself.");
        }
        finally
        {
            try { if (File.Exists(wav)) File.Delete(wav); } catch (Exception ex) { Log.Debug(ex, "Warm-up: could not delete its silent output."); }
            double seconds = wall.Elapsed.TotalSeconds;
            string kinds = warmed.Count == 0 ? "nothing" : string.Join(", ", warmed);
            string stages = binaural ? " on the binaural mixer" : "";
            if (finished) Log.Information("Warm-up: {Kinds} rendered and thrown away{Stages} in {Seconds:F1} s.", kinds, stages, seconds);
            else if (_stop.IsCancellationRequested)
                Log.Information("Warm-up: stopped for the map after {Seconds:F1} s; {Kinds} had been rendered.", seconds, kinds);
            else Log.Information("Warm-up: gave up after {Seconds:F1} s; {Kinds} had been rendered.", seconds, kinds);
        }
        return finished;
    }

    /// <summary>The made-up scene: ground, a wall for paths and echoes, and one source of every kind a
    /// city load starts. Ids matter only to the scene and its bird perch.</summary>
    private static ClientWorldState Scene()
    {
        var world = new ClientWorldState();
        var size = new Vector3(200f, 40f, 200f);
        var min = new Vector3(-100f, -10f, -100f);
        world.Clear(size);
        world.ConfigureAcoustics(min, AcousticConstants.DefaultVoxelResolution, AcousticConstants.DefaultOcclusionFloor, 0f);
        // One global room and one reverb bus: the city's rooms are the first thing a load makes, and the
        // mix's reverb and its traced stage are made per room.
        var map = new AcousticMap(size, min) { GlobalEnvironmentId = AcousticConstants.GlobalRegionId };
        map.Regions[AcousticConstants.GlobalRegionId] = new Room
        {
            FriendlyName = "Warm-up", RoomSize = size, Materials = new int[6], ReverbTimeScale = 1f,
        };
        world.SetAcousticMap(map);
        world.UpdateAtmosphere(new WorldStateUpdate
        {
            Temperature = 15f, Humidity = 0.6f, AirPressure = 1013.25f, AirAbsorptionMultiplier = 1f,
        });

        // The ear stands on a hard floor; a wall stands close enough to answer it.
        world.RegisterDefinition(Box(1, new Vector3(0f, -0.5f, 0f), new Vector3(200f, 1f, 200f), "Concrete", solid: true));
        world.RegisterDefinition(Box(2, new Vector3(0f, 3f, 9f), new Vector3(30f, 8f, 0.35f), "Brick", solid: true));

        // An engine and, beside it, one on a call: its siren and horn are the car's own voices.
        AddCar(world, 10, "i4_economy", new Vector3(8f, 0f, -6f), new Vector3(0f, 0f, -8f));
        AddCar(world, 11, "police_interceptor", new Vector3(-7f, 0f, -9f), new Vector3(0f, 0f, -6f),
               VehicleSignalBits.Encode(manual: false, horn: false, sirenOn: true, SirenMode.Wail));

        // The standing kinds: a machine, a train's field source, running water, a fire and a bird's tree.
        Source(world, 20, "machine:ac_condenser", new Vector3(4f, 1f, 3f), new Vector3(0.6f, 0.4f, 0.6f), "Metal");
        Source(world, 21, "rail:light_rail/warm/0", new Vector3(20f, 1f, -4f), null, "Steel");
        Source(world, 22, "flow:creek", new Vector3(-3f, 0.2f, 5f), new Vector3(2f, 0.2f, 1f), "Water");
        Source(world, 23, "fire:fire_pit", new Vector3(-4f, 0.5f, -3f), new Vector3(1.2f, 1f, 1.2f), "Asphalt");
        // Perches whose hashes give sparrow groups (BirdLife.Hash), far enough that the birds are not shy.
        // Several, because a group drawn quiet for up to half a minute before its first call would otherwise
        // leave the birds unwarmed on some logins.
        int[] perches = { 15, 30, 31, 38, 42, 47, 61, 69 };
        for (int i = 0; i < perches.Length; i++)
        {
            float angle = MathF.Tau * i / perches.Length;
            float radius = 20f + (i % 2) * 6f;
            Source(world, perches[i], "foliage:park_tree",
                   new Vector3(6f * MathF.Cos(angle), 6f, 18f + radius * MathF.Sin(angle)),
                   new Vector3(8f, 12f, 8f), "Foliage");
        }
        return world;
    }

    /// <summary>Runs the scene at the game's audio rate, faster than real time where it can, for the scene's
    /// twelve seconds or until the map arrives; the mixer's last voices stay up a moment so its effects run.</summary>
    private bool RunScene(ClientAudioSystem audio, LocalPlayerState player, ClientWorldState world, WarmClock clock,
                          Stopwatch wall, SortedSet<string> warmed)
    {
        double step = 1.0 / ClientAudioSystem.UpdateHz;
        int frames = (int)(SceneSeconds / step);
        var snapshot = world.GetSnapshot();
        for (int frame = 0; frame < frames; frame++)
        {
            if (_stop.IsCancellationRequested || wall.Elapsed.TotalSeconds > WallLimitSeconds) return false;
            clock.Now = 1.0 + frame * step;

            // A step every half second and a horn at the start: the recorded one-shot path as well as the
            // synthesised ones. The horn is the police car's, so its own path is what is asked for.
            if (frame == 2) audio.WorldAudio.HornReceived?.Invoke(11, "electric:disc_pair", new[] { 1.2f, 0.3f, 0.6f });
            if (frame % 30 == 0) audio.OnOwnFootstep(player.Position, "Concrete", "0");

            snapshot = world.GetSnapshot();
            audio.Update(snapshot);
            Sample(audio.World.Voices.Entries, warmed);
            Thread.Sleep(2);
        }
        // The last voices play while the mixer's stages run at least a few blocks: a voice started and
        // stopped inside one block would warm nothing of its binaural or reverb stage.
        for (int i = 0; i < 60 && !_stop.IsCancellationRequested; i++) Thread.Sleep(5);
        Sample(audio.World.Voices.Entries, warmed);
        audio.LeaveWorld(snapshot.Entities.Keys.ToList());
        return !_stop.IsCancellationRequested;
    }

    /// <summary>Notes each kind of voice the scene has had, by the registry's own names. A quiet scene's
    /// one-shots can be gone between two samples, hence the sweep on every frame.</summary>
    private static void Sample(IReadOnlyCollection<VoiceEntry> entries, SortedSet<string> warmed)
    {
        foreach (var entry in entries)
            switch (entry.Kind)
            {
                case VoiceKind.Engine: warmed.Add("engine"); break;
                case VoiceKind.Machine:
                    warmed.Add(entry.OwnerId switch
                    {
                        20 => "machine", 22 => "running water", 23 => "fire", _ => "tree",
                    });
                    break;
                case VoiceKind.TrainField or VoiceKind.TrainSignal: warmed.Add("rail"); break;
                case VoiceKind.Horn or VoiceKind.HeldHorn: warmed.Add("horn"); break;
                case VoiceKind.Siren: warmed.Add("siren"); break;
                case VoiceKind.Bird: warmed.Add("birds"); break;
                case VoiceKind.OwnStep or VoiceKind.Footstep: warmed.Add("footsteps"); break;
                case VoiceKind.Reflection or VoiceKind.EngineEcho: warmed.Add("echoes"); break;
            }
    }

    private static void AddCar(ClientWorldState world, int id, string preset, Vector3 at, Vector3 velocity, byte signals = 0)
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.NPC,
            Moves = true,
            Transform = new Transform { Position = at, Rotation = Quaternion.Identity },
        };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = "engine:" + preset;
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.MinDistance = 3f;
        def.SoundEmitter.Range = 200f;
        world.RegisterDefinition(def);
        world.SyncState(new[]
        {
            new EntityState
            {
                EntityId = id,
                Transform = QuantizedTransform.FromTransform(def.Transform),
                LinearVelocity = velocity,
                Signals = signals,
            },
        });
    }

    private static void Source(ClientWorldState world, int id, string soundId, Vector3 at, Vector3? size, string material)
    {
        var def = Box(id, at, size ?? Vector3.Zero, material, solid: false);
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = soundId;
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.MinDistance = 1f;
        def.SoundEmitter.Range = 50f;
        world.RegisterDefinition(def);
    }

    private static EntityDefinition Box(int id, Vector3 at, Vector3 size, string material, bool solid) => new()
    {
        EntityId = id,
        Type = EntityType.StaticObject,
        Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
        Collider = size == Vector3.Zero ? default : new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = solid },
        Material = new MaterialComponent { Material = material },
    };

    /// <summary>A clock the warm-up steps itself, so the scene's twelve seconds need not be twelve seconds of
    /// the player's login. The provider's voices age on this clock as the game's age on the mixer's.</summary>
    private sealed class WarmClock : IAudioClock
    {
        private double _now;
        public double Now { get => Volatile.Read(ref _now); set => Volatile.Write(ref _now, value); }
        public double WorldTime => Now;
    }
}
