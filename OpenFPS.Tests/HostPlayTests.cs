using System.Numerics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;

namespace OpenFPS.Tests;

/// <summary>
/// A recording played at a place by the host (library 0.2, item 1: AudioWorld.Play and its PlacedSound handle), through
/// the world's own frame and so the same placing as any other recording. The mixer tests run headless on FMOD's
/// no-sound output; run them with <c>-e OPENFPS_FMOD_TESTS=1 -e LD_LIBRARY_PATH=&lt;repo&gt;/lib</c>.
/// </summary>
public class HostPlayTests : IDisposable
{
    private readonly string _dir;
    private readonly HashSet<Thread> _before = Shutdown.Alive().ToHashSet();

    public HostPlayTests()
    {
        string home = System.Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();
        _dir = Path.Combine(home, ".cache", "openfps-build", "lib6a-play", "testdata", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>A host with no map: the facade headless, the audio system over an empty world, frames stepped by hand.</summary>
    private sealed class Host
    {
        public readonly FmodAudioProvider Provider = new() { NoSound = true };
        public readonly AudioEngineFacade Facade;
        public readonly ClientAudioSystem Audio;
        public readonly ClientWorldState World = new();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        public Host(string soundsFolder)
        {
            Facade = new AudioEngineFacade(Provider, paths: new AudioDataPaths(soundsFolder, null, null));
            Facade.InitializeSteppedByHand();
            World.Clear(new Vector3(4000, 400, 4000));
            World.Clock = Facade.Clock;
            World.Hearing = Facade.Hearing;
            var player = new LocalPlayerState();
            Audio = new ClientAudioSystem(Facade, new SoundMappingService(player), player, () => _clock.Elapsed.TotalSeconds, prewarm: false);
        }

        public AudioWorld Place => Audio.World;

        /// <summary>Frames in real time (the mixer runs in real time); the loudest the mix got meanwhile.</summary>
        public float Run(double seconds)
        {
            float peak = float.NegativeInfinity;
            var until = System.Diagnostics.Stopwatch.StartNew();
            while (until.Elapsed.TotalSeconds < seconds)
            {
                Audio.Update(World.GetSnapshot());
                Facade.StepByHand();
                float now = Facade.MixPeakDb;
                if (float.IsFinite(now)) peak = Math.Max(peak, now);
                Thread.Sleep(16);
            }
            return peak;
        }

        public bool Sounds(int id) => Provider.IsPlaying(id);
    }

    private void ClosesClean(Host host)
    {
        host.Facade.Dispose();
        Assert.False(host.Facade.IsInitialized);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (Shutdown.Alive().Any(t => !_before.Contains(t)) && wait.ElapsedMilliseconds < 2000) Thread.Sleep(10);
        Assert.Empty(Shutdown.Alive().Where(t => !_before.Contains(t)).Select(t => t.Name));
        Assert.Equal(0, host.Audio.World.BackgroundRunning);
    }

    private Host Make()
    {
        string empty = Path.Combine(_dir, "none");
        Directory.CreateDirectory(empty);
        var host = new Host(empty);
        Assert.True(host.Facade.AddSound("loop", HostSoundsTests.Wav(330f, 1.0f)));
        Assert.True(host.Facade.AddSound("once", HostSoundsTests.Wav(660f, 0.6f)));
        return host;
    }

    // ── The call and its handle, with no mixer ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_bad_place_gain_or_sound_is_refused_and_a_good_one_is_given_an_id_in_the_band()
    {
        var facade = new AudioEngineFacade(new RecordingMixer());
        var player = new LocalPlayerState();
        var audio = new ClientAudioSystem(facade, new SoundMappingService(player), player, () => 0.0, prewarm: false);
        var world = audio.World;
        Assert.Throws<ArgumentException>(() => world.Play("", Vector3.Zero));
        Assert.Throws<ArgumentException>(() => world.Play("a", new Vector3(float.NaN, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.Play("a", Vector3.Zero, float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.Play("a", Vector3.Zero, 0f, false, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.Play("a", Vector3.Zero, 0f, false, 1f, -5f));

        var a = world.Play("a", new Vector3(1, 2, 3), -6f, loop: true);
        var b = world.Play("b", Vector3.Zero);
        Assert.True(a.Id >= HostedSounds.FirstId && b.Id == a.Id + 1);
        Assert.Equal(-6f, a.GainDb);
        Assert.Equal(new Vector3(1, 2, 3), a.Position);
        a.MoveTo(new Vector3(4, 5, 6));
        Assert.Equal(new Vector3(4, 5, 6), a.Position);
        Assert.Throws<ArgumentException>(() => a.MoveTo(new Vector3(0, float.NaN, 0)));
        Assert.True(a.IsPlaying && b.IsPlaying);
        a.Stop();
        Assert.False(a.IsPlaying);
        Assert.True(b.IsPlaying);
        facade.Dispose();
    }

    // ── The mixer: a loop and a once sound through the real frame ────────────────────────────────────────────

    [FmodFact]
    public void A_loop_and_a_once_sound_are_heard_moved_stopped_and_gone_and_the_dispose_is_clean()
    {
        var host = Make();
        var loop = host.Place.Play("loop", new Vector3(3f, 0f, 0f), loop: true);
        var once = host.Place.Play("once", new Vector3(-3f, 0f, 0f));

        float heard = host.Run(1.0);
        Assert.True(heard > -60f, $"the played sounds were not heard: {heard} dBFS");
        // Through the registry, as every recording is: its entity id is its voice, declared as a recording.
        Assert.True(host.Sounds(loop.Id), "the loop has no voice");
        Assert.True(host.Place.Voices.TryGet(loop.Id, out var entry) && entry.Kind == VoiceKind.Recording && entry.OwnerId == loop.Id);
        Assert.True(loop.IsPlaying);

        // The once sound plays out by itself (0.6 s) and its handle says so; the loop goes on.
        host.Run(2.0);
        Assert.False(once.IsPlaying, "the once sound did not finish by itself");
        Assert.False(host.Sounds(once.Id), "the once sound's voice is still there");
        Assert.True(loop.IsPlaying && host.Sounds(loop.Id));
        Assert.False(host.Place.Voices.IsRegistered(once.Id));

        // Moved far away, it is heard from further off: the mixer's own position for it moves with it.
        float before = Vector3.Distance(host.Provider.GetSoundPosition(loop.Id), Vector3.Zero);
        loop.MoveTo(new Vector3(60f, 0f, 0f));
        host.Run(0.5);
        float after = Vector3.Distance(host.Provider.GetSoundPosition(loop.Id), Vector3.Zero);
        Assert.True(after > before + 30f, $"moving the handle did not move the voice: {before} m then {after} m");

        // Stopped, it fades out and is gone: no voice, no entry, no id left that is its own.
        loop.Stop();
        Assert.False(loop.IsPlaying);
        host.Run(1.0);
        Assert.False(host.Sounds(loop.Id), "the stopped loop still sounds");
        Assert.False(host.Place.Voices.IsRegistered(loop.Id));
        Assert.DoesNotContain(host.Provider.GetActiveSpatialSoundIds(), id => id >= HostedSounds.FirstId);
        Assert.Equal(0, host.Place.PlayedCount);

        ClosesClean(host);
    }

    [FmodFact]
    public void A_gain_in_decibels_is_the_gain_heard_and_a_change_of_it_is_heard_from_the_next_frame()
    {
        // The voice's own level at the ear (distance, occlusion and air applied), not the mix's peak, which is held for
        // five seconds and shows a voice's first buffers.
        static float Level(Host host, PlacedSound sound)
            => host.Provider.LoudestVoices(8).Where(v => v.EntityId == sound.Id).Select(v => v.Db).DefaultIfEmpty(float.NegativeInfinity).Max();

        float Settled(float db)
        {
            var host = Make();
            var loop = host.Place.Play("loop", new Vector3(2f, 0f, 0f), db, loop: true);
            host.Run(1.0);
            float level = Level(host, loop);
            ClosesClean(host);
            return level;
        }
        float loud = Settled(0f);
        float quiet = Settled(-24f);
        Assert.True(loud > -60f, $"the loop at 0 dB was not heard: {loud} dB");
        Assert.InRange(loud - quiet, 22f, 26f);

        // Turned down while it plays: the handle's gain reaches the voice from the next frame.
        var host2 = Make();
        var loop2 = host2.Place.Play("loop", new Vector3(2f, 0f, 0f), 0f, loop: true);
        host2.Run(1.0);
        float full = Level(host2, loop2);
        loop2.GainDb = -24f;
        host2.Run(0.5);
        float down = Level(host2, loop2);
        Assert.InRange(full - down, 22f, 26f);
        ClosesClean(host2);
    }

    [FmodFact]
    public void Leaving_the_world_ends_a_loop_and_its_handle_reads_as_finished()
    {
        var host = Make();
        var loop = host.Place.Play("loop", new Vector3(3f, 0f, 0f), loop: true);
        host.Run(0.5);
        Assert.True(host.Sounds(loop.Id));
        host.Place.LeaveWorld(Array.Empty<int>());
        Assert.False(loop.IsPlaying);
        host.Run(0.5);
        Assert.False(host.Sounds(loop.Id));
        ClosesClean(host);
        Assert.False(loop.IsPlaying);
    }
}
