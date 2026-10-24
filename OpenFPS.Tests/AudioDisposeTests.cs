using System.Numerics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Models;
using OpenFPS.Sound;

namespace OpenFPS.Tests;

/// <summary>
/// One dispose stops and joins everything the audio started (AudioEngineFacade.Dispose): the audio thread, the acoustic
/// worker, the traced-reverb threads, the door prewarm and every background task, then frees the mixer and, last, the
/// Steam Audio objects it read. Done twice, or before init, it does nothing more.
/// </summary>
public class AudioDisposeTests
{
    /// <summary>The threads the audio has started that are alive now.</summary>
    private static HashSet<Thread> Live() => Shutdown.Alive().ToHashSet();

    /// <summary>Names of the audio's threads alive now that were not in <paramref name="before"/>, after a moment for a
    /// thread whose last act was to say it was done.</summary>
    private static List<string> Left(HashSet<Thread> before)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        List<Thread> left;
        while ((left = Live().Where(t => !before.Contains(t)).ToList()).Count > 0 && clock.ElapsedMilliseconds < 2000)
            Thread.Sleep(10);
        return left.Select(t => t.Name ?? "?").ToList();
    }

    [Fact]
    public void Dispose_stops_the_threads_and_tasks_the_world_started()
    {
        var before = Live();
        var h = new ClientAudioHarness();
        h.StandAt(new Vector3(0, 0, 0));
        h.AddWall(900, new Vector3(6, 2, 0), new Vector3(1, 4, 12));
        h.AddCar(1, "i4_economy", new Vector3(20, 0, 3), new Vector3(8, 0, 0));
        h.Tick(120);
        Assert.Contains(Live().Where(t => !before.Contains(t)), t => t.Name == "AcousticWorkerThread");

        h.Facade.Dispose();

        Assert.Empty(Left(before));
        Assert.Equal(0, h.Audio.World.BackgroundRunning);
    }

    [Fact]
    public void Dispose_twice_and_the_world_after_it_do_nothing()
    {
        var h = new ClientAudioHarness();
        h.Tick(10);
        h.Facade.Dispose();
        h.Facade.Dispose();
        h.Audio.World.Dispose();
        h.Audio.World.Dispose();
        Assert.False(h.Facade.IsInitialized);
    }

    [Fact]
    public void Dispose_before_init_does_nothing_and_a_later_init_starts_nothing()
    {
        var before = Live();
        var facade = new AudioEngineFacade(new RecordingMixer());
        facade.Dispose();
        facade.Dispose();
        facade.Initialize();
        facade.InitializeForTest();
        Assert.False(facade.IsInitialized);
        Assert.Empty(Left(before));
    }

    [Fact]
    public void An_unstarted_FMOD_provider_disposes_without_touching_FMOD()
    {
        var facade = new AudioEngineFacade(new FmodAudioProvider());
        facade.Dispose();
        facade.Dispose();
        Assert.False(facade.IsInitialized);
    }

    [Fact]
    public void A_world_disposed_alone_stops_its_worker_and_leaves_the_mixer_to_the_facade()
    {
        var before = Live();
        var h = new ClientAudioHarness();
        h.Tick(10);
        h.Audio.World.Dispose();
        Assert.DoesNotContain("AcousticWorkerThread", Left(before));
        h.Facade.Dispose();
        Assert.Empty(Left(before));
    }

    [Fact]
    public void Background_work_is_waited_for_by_name()
    {
        var work = new BackgroundWork();
        var gate = new ManualResetEventSlim();
        BackgroundWork.Run(work, "slow", () => gate.Wait());
        BackgroundWork.Run(work, "quick", () => { });
        Assert.Equal(new[] { "slow" }, work.Wait(100));
        Assert.Equal(1, work.Running);
        gate.Set();
        Assert.Empty(work.Wait(5000));
        Assert.Equal(0, work.Running);
    }

    /// <summary>
    /// A live FMOD mixer on its own audio thread, a world and a car over it, then the dispose: none of the audio's
    /// threads is left, and a second dispose does nothing. Run with
    /// <c>-e OPENFPS_FMOD_TESTS=1 -e LD_LIBRARY_PATH=&lt;repo&gt;/lib</c>.
    /// </summary>
    [FmodFact]
    public void A_live_FMOD_mixer_and_its_world_dispose_cleanly()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-dispose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var before = Live();
            var facade = new AudioEngineFacade(new FmodAudioProvider { WavPath = Path.Combine(dir, "mix.wav") });
            facade.Initialize();
            Assert.True(facade.IsInitialized);

            var world = new ClientWorldState();
            world.Clear(new Vector3(4000, 400, 4000));
            var player = new LocalPlayerState();
            world.Clock = facade.Clock;
            world.Hearing = facade.Hearing;
            var sounds = new SoundMappingService(player);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var audio = new ClientAudioSystem(facade, sounds, player, () => clock.Elapsed.TotalSeconds, prewarm: false);

            var def = new EntityDefinition
            {
                EntityId = 1, Type = EntityType.NPC, Moves = true,
                Transform = new Transform { Position = new Vector3(15, 0, 4), Rotation = Quaternion.Identity },
            };
            def.SoundEmitter.IsSynth = true;
            def.SoundEmitter.SoundId = "engine:i4_economy";
            def.SoundEmitter.Mode = PlaybackMode.LoopOne;
            def.SoundEmitter.Volume = 1f;
            def.SoundEmitter.Range = facade.Hearing.AudibleRange(MachineRegistry.VehicleFor("i4_economy").SourceLevelDb);
            def.SoundEmitter.MinDistance = 3f;
            world.RegisterDefinition(def);
            world.SyncState(new[] { new EntityState { EntityId = 1, Transform = QuantizedTransform.FromTransform(def.Transform) } });

            var until = System.Diagnostics.Stopwatch.StartNew();
            while (until.Elapsed.TotalSeconds < 2.5)
            {
                audio.Update(world.GetSnapshot());
                Thread.Sleep(16);
            }
            Assert.Contains(Live().Where(t => !before.Contains(t)), t => t.Name == "AudioEngineThread");

            facade.Dispose();
            facade.Dispose();

            Assert.Empty(Left(before));
            Assert.Equal(0, audio.World.BackgroundRunning);
            Assert.False(facade.IsInitialized);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
