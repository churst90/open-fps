using System.Numerics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Data;
using OpenFPS.Audio.Fmod;
using OpenFPS.Sound;

namespace OpenFPS.Tests;

/// <summary>
/// Sounds from where the host says (library 0.2, item 7): the loader reads under the facade's Paths.Sounds, so the
/// bank and the loader agree, and takes a sound's bytes from the host under an id, which win over a file. The mixer
/// tests run headless on FMOD's no-sound output and read the mix (MixPeakDb); run them with
/// <c>-e OPENFPS_FMOD_TESTS=1 -e LD_LIBRARY_PATH=&lt;repo&gt;/lib</c>.
/// </summary>
public class HostSoundsTests : IDisposable
{
    private readonly string _dir;

    public HostSoundsTests()
    {
        // Under the build's artifacts, outside the program's folder (and not /tmp, which is RAM).
        string home = System.Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();
        _dir = Path.Combine(home, ".cache", "openfps-build", "lib6a-play", "testdata", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>A mono 16-bit WAV file: a sine at <paramref name="hz"/>, or silence at 0.</summary>
    internal static byte[] Wav(float hz, float seconds = 0.5f, float amplitude = 0.5f, int rate = 44100)
    {
        int n = (int)(seconds * rate);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        for (int i = 0; i < n; i++)
            w.Write((short)(hz <= 0f ? 0 : MathF.Sin(2f * MathF.PI * hz * i / rate) * amplitude * 32767f));
        return ms.ToArray();
    }

    internal static SpatialEmitter Emitter(int id, string sound, bool loop = true, Vector3? at = null) => new()
    {
        EntityId = id, SoundId = sound, Type = EmitterType.WorldLocked,
        Mode = loop ? PlaybackMode.LoopOne : PlaybackMode.Single, IsEvent = !loop,
        Position = at ?? new Vector3(2f, 0f, 0f), Volume = 0.5f, MinDistance = 1f, Range = 200f, Pitch = 1f,
        EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
    };

    /// <summary>The loudest the mix gets over about a second and a half of real time, stepping the facade by hand.</summary>
    internal static float Hear(AudioEngineFacade facade, double seconds = 1.5)
    {
        float peak = float.NegativeInfinity;
        var until = System.Diagnostics.Stopwatch.StartNew();
        while (until.Elapsed.TotalSeconds < seconds)
        {
            facade.StepByHand();
            float now = facade.MixPeakDb;
            if (float.IsFinite(now)) peak = Math.Max(peak, now);
            Thread.Sleep(16);
        }
        return peak;
    }

    internal static AudioEngineFacade Headless(string soundsFolder)
    {
        var facade = new AudioEngineFacade(new FmodAudioProvider { NoSound = true }, paths: new AudioDataPaths(soundsFolder, null, null));
        facade.InitializeSteppedByHand();
        facade.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);
        return facade;
    }

    /// <summary>The audio's threads alive before the test began: whatever else is alive after the dispose is a leak.</summary>
    private readonly HashSet<Thread> _before = Shutdown.Alive().ToHashSet();

    private void ClosesClean(AudioEngineFacade facade)
    {
        facade.Dispose();
        Assert.False(facade.IsInitialized);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (Shutdown.Alive().Any(t => !_before.Contains(t)) && wait.ElapsedMilliseconds < 2000) Thread.Sleep(10);
        Assert.Empty(Shutdown.Alive().Where(t => !_before.Contains(t)).Select(t => t.Name));
    }

    // ── The guard: an id leaves the folder, or names a share, and the sound reads as missing ──────────────────

    [Theory]
    [InlineData("../outside.wav")]
    [InlineData("sub/../../outside.wav")]
    [InlineData("/etc/passwd")]
    public void An_id_that_leaves_the_sounds_folder_is_refused(string id)
    {
        var source = new SoundSource(_dir);
        Assert.Equal("", source.PathOf(id));
    }

    [Theory]
    [InlineData(@"\\evil.example\share\x.wav")]
    [InlineData("//evil.example/share/x.wav")]
    public void An_id_that_names_a_network_share_is_refused(string id)
    {
        var source = new SoundSource(_dir);
        Assert.Equal("", source.PathOf(id));
        // Nor with the program's folder named in it, which is how a share was once let through.
        Assert.Equal("", source.PathOf(id.Replace("share", "ASSETS")));
    }

    [Fact]
    public void An_id_inside_the_folder_is_read_from_the_folder_and_not_from_beside_the_program()
    {
        var source = new SoundSource(_dir);
        Assert.Equal(Path.Combine(_dir, "doors", "slam.wav"), source.PathOf("doors/slam.wav"));
        // The program's own sounds, with no folder said, are where they were.
        Assert.Equal(Path.Combine(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory), "ASSETS", "SOUNDS", "a", "b.ogg"),
                     FmodAudioProvider.SoundFilePath("a/b.ogg"));
    }

    [Fact]
    public void The_facade_points_the_loader_at_the_folder_it_was_given()
    {
        var provider = new FmodAudioProvider();
        var facade = new AudioEngineFacade(provider, paths: new AudioDataPaths(_dir, null, null));
        Assert.Equal(_dir, provider.Sounds.Folder);
        facade.Dispose();
    }

    // ── The mixer: a folder outside the program's, and bytes from memory ─────────────────────────────────────

    [FmodFact]
    public void A_sound_in_a_folder_outside_the_program_is_heard_and_one_outside_the_folder_is_not()
    {
        string folder = Path.Combine(_dir, "game", "sounds");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "tone.wav"), Wav(440f));
        File.WriteAllBytes(Path.Combine(_dir, "game", "secret.wav"), Wav(440f));

        var facade = Headless(folder);
        facade.Submit(Emitter(1, "tone.wav"));
        float heard = Hear(facade);
        Assert.True(heard > -60f, $"the sound from the host's folder was not heard: {heard} dBFS");
        facade.StopSound(1);
        ClosesClean(facade);

        // The same tone one folder up, asked for by an id that climbs out of the folder to it.
        facade = Headless(folder);
        facade.Submit(Emitter(2, "../secret.wav"));
        float silent = Hear(facade);
        Assert.False(silent > -80f, $"a file outside the folder was heard: {silent} dBFS");
        Assert.False(facade.IsPlaying(2));
        ClosesClean(facade);
    }

    [FmodFact]
    public void A_sound_handed_in_as_bytes_is_heard_with_no_file_at_all()
    {
        string empty = Path.Combine(_dir, "none");
        Directory.CreateDirectory(empty);
        var facade = Headless(empty);
        Assert.True(facade.AddSound("packaged/tone", Wav(440f)));
        facade.Submit(Emitter(1, "packaged/tone"));
        float heard = Hear(facade);
        Assert.True(heard > -60f, $"the sound from memory was not heard: {heard} dBFS");
        facade.StopSound(1);
        ClosesClean(facade);
        Assert.False(facade.AddSound("late", Wav(440f)));
    }

    [FmodFact]
    public void A_stream_is_read_to_its_end_and_played_like_bytes()
    {
        string empty = Path.Combine(_dir, "none");
        Directory.CreateDirectory(empty);
        var facade = Headless(empty);
        using (var stream = new MemoryStream(Wav(660f)))
            Assert.True(facade.AddSound("streamed", stream));
        facade.Submit(Emitter(1, "streamed"));
        float heard = Hear(facade);
        Assert.True(heard > -60f, $"the sound from a stream was not heard: {heard} dBFS");
        ClosesClean(facade);
    }

    [FmodFact]
    public void The_hosts_bytes_win_over_a_file_of_the_same_id_even_once_the_file_has_played()
    {
        string folder = Path.Combine(_dir, "sounds");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "dup.wav"), Wav(0f));
        var facade = Headless(folder);

        facade.Submit(Emitter(1, "dup"));
        float file = Hear(facade);
        Assert.False(file > -80f, $"the silent file was heard: {file} dBFS");
        facade.StopSound(1);
        Hear(facade, 0.3);

        Assert.True(facade.AddSound("dup", Wav(440f)));
        facade.Submit(Emitter(2, "dup"));
        float bytes = Hear(facade);
        Assert.True(bytes > -60f, $"the host's bytes did not win over the file: {bytes} dBFS");
        facade.StopSound(2);
        // The mix's peak is held for five seconds: wait the tone out before listening for silence.
        Hear(facade, 5.5);

        // Taken back, the file (silent here) is what plays again.
        Assert.True(facade.RemoveSound("dup"));
        facade.Submit(Emitter(3, "dup"));
        float again = Hear(facade);
        Assert.False(again > -80f, $"the file did not come back after the bytes were taken back: {again} dBFS");
        ClosesClean(facade);
    }
}
