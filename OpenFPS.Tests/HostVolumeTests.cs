using System.Numerics;

using OpenFPS.Audio.Core;
using OpenFPS.Audio.Fmod;

namespace OpenFPS.Tests;

/// <summary>
/// The host's own flat sounds and its volumes (library 0.2, items 2 and 3): a group under the master for a host's music
/// and menus, and the master and the world set in dB while running. The mix is read from a tap on the master, one tone
/// at a time by its frequency, because the peak meter keeps the loudest it has seen. The mixer tests run headless on
/// FMOD's no-sound output; run them with <c>-e OPENFPS_FMOD_TESTS=1 -e LD_LIBRARY_PATH=&lt;repo&gt;/lib</c>.
/// </summary>
public class HostVolumeTests : IDisposable
{
    private const float WorldHz = 330f, HostHz = 880f;
    private readonly string _dir;
    private readonly HashSet<Thread> _before = Shutdown.Alive().ToHashSet();

    public HostVolumeTests()
    {
        string home = System.Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();
        _dir = Path.Combine(home, ".cache", "openfps-build", "lib6a-vol", "testdata", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>A headless mixer with a looping tone placed in the world, and one the host plays flat into its own group.</summary>
    internal sealed class Rig
    {
        public readonly AudioEngineFacade Facade;
        public FMOD.Channel HostChannel;

        public Rig(string dir, bool worldTone = true, bool hostTone = true, float worldHz = WorldHz, float hostHz = HostHz)
        {
            string empty = Path.Combine(dir, "none");
            Directory.CreateDirectory(empty);
            Facade = HostSoundsTests.Headless(empty);
            if (worldTone)
            {
                Assert.True(Facade.AddSound("world", HostSoundsTests.Wav(worldHz, 1.0f)));
                Facade.Submit(HostSoundsTests.Emitter(1, "world", loop: true, at: new Vector3(0f, 0f, 2f)));
            }
            if (hostTone) PlayHost(hostHz, 0.1f);
        }

        public void PlayHost(float hz, float volume)
        {
            var system = Facade.FmodSystem;
            Assert.True(system.hasHandle());
            Assert.True(Facade.HostGroup.hasHandle());
            int rate = Facade.MixerRate;
            int n = rate;
            var pcm = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                short v = (short)(MathF.Sin(2f * MathF.PI * hz * i / rate) * 0.5f * 32767f);
                pcm[i * 2] = (byte)(v & 0xFF); pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            var info = new FMOD.CREATESOUNDEXINFO
            {
                cbsize = System.Runtime.InteropServices.Marshal.SizeOf<FMOD.CREATESOUNDEXINFO>(),
                length = (uint)pcm.Length, numchannels = 1, defaultfrequency = rate, format = FMOD.SOUND_FORMAT.PCM16,
            };
            // A whole number of cycles in the loop, so the join is silent.
            Assert.Equal(FMOD.RESULT.OK, system.createSound(pcm, FMOD.MODE.OPENMEMORY | FMOD.MODE.OPENRAW | FMOD.MODE.CREATESAMPLE | FMOD.MODE.LOOP_NORMAL | FMOD.MODE._2D,
                                                       ref info, out FMOD.Sound sound));
            Assert.Equal(FMOD.RESULT.OK, system.playSound(sound, Facade.HostGroup, true, out HostChannel));
            HostChannel.setVolume(volume);
            HostChannel.setPaused(false);
        }

        /// <summary>Frames in real time while a tap on the master writes <paramref name="path"/>; what it wrote.</summary>
        public float[][] RecordTo(string path, double seconds)
        {
            Facade.FmodSystem.getMasterChannelGroup(out var master);
            var tap = MasterTap.Attach(Facade.FmodSystem, master, path, Facade.MixerRate, asFloat: true);
            Assert.NotNull(tap);
            HostSoundsTests.Hear(Facade, seconds);
            tap!.Dispose();
            return ReadWav(path);
        }
    }

    internal static float[][] ReadWav(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int ch = BitConverter.ToInt16(b, 22);
        int n = (b.Length - 44) / 4 / ch;
        var x = new float[ch][];
        for (int c = 0; c < ch; c++) x[c] = new float[n];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < ch; c++) x[c][i] = BitConverter.ToSingle(b, 44 + (i * ch + c) * 4);
        return x;
    }

    /// <summary>The level of one frequency in a recording, dBFS as a sine's amplitude, both channels' power together, over
    /// the last <paramref name="seconds"/> of it (a whole number of cycles is not needed at this length).</summary>
    internal static double ToneDb(float[][] x, float hz, int rate, double seconds = 0.5)
    {
        int n = Math.Min(x[0].Length, (int)(seconds * rate));
        double power = 0;
        foreach (var ch in x)
        {
            double re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double s = ch[ch.Length - n + i], w = 2 * Math.PI * hz * i / rate;
                re += s * Math.Cos(w); im += s * Math.Sin(w);
            }
            double amp = 2 * Math.Sqrt(re * re + im * im) / n;
            power += amp * amp;
        }
        return 10 * Math.Log10(Math.Max(power, 1e-20));
    }

    private (double World, double Host) Levels(Rig rig, double settle = 0.6, double window = 0.8)
    {
        HostSoundsTests.Hear(rig.Facade, settle);
        var x = rig.RecordTo(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".wav"), window);
        int rate = rig.Facade.MixerRate;
        return (ToneDb(x, WorldHz, rate), ToneDb(x, HostHz, rate));
    }

    private void ClosesClean(Rig rig)
    {
        rig.Facade.Dispose();
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (Shutdown.Alive().Any(t => !_before.Contains(t)) && wait.ElapsedMilliseconds < 2000) Thread.Sleep(10);
        Assert.Empty(Shutdown.Alive().Where(t => !_before.Contains(t)).Select(t => t.Name));
    }

    // ── Without a mixer ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Without_a_mixer_there_is_no_system_or_group_and_the_volume_calls_are_harmless()
    {
        var facade = new AudioEngineFacade(new RecordingMixer());
        Assert.False(facade.FmodSystem.hasHandle());
        Assert.False(facade.HostGroup.hasHandle());
        facade.SetMasterVolumeDb(-20f);
        facade.SetWorldVolumeDb(-20f);
        facade.Dispose();
    }

    // ── The mixer ────────────────────────────────────────────────────────────────────────────────────────────

    [FmodFact]
    public void A_flat_sound_in_the_hosts_group_is_in_the_mix_and_the_world_fade_leaves_it_alone()
    {
        var rig = new Rig(_dir);
        var open = Levels(rig);
        Assert.True(open.Host > -60.0, $"the host's tone is not in the mix: {open.Host:F1} dB");
        Assert.True(open.World > -90.0, $"the world's tone is not in the mix: {open.World:F1} dB");
        Assert.True(rig.Facade.MixPeakDb > -60f, $"the peak meter does not see the mix: {rig.Facade.MixPeakDb}");

        rig.Facade.SetWorldFade(0.1f);   // 20 dB down
        var faded = Levels(rig);
        Assert.InRange(faded.World - open.World, -21.5, -18.5);
        Assert.InRange(faded.Host - open.Host, -0.5, 0.5);

        rig.Facade.SetWorldFade(1f);
        var back = Levels(rig);
        Assert.InRange(back.World - open.World, -0.7, 0.7);
        Assert.InRange(back.Host - open.Host, -0.5, 0.5);
        ClosesClean(rig);
    }

    [FmodFact]
    public void The_world_volume_takes_the_world_down_and_not_the_hosts_sound()
    {
        var rig = new Rig(_dir);
        var open = Levels(rig);
        rig.Facade.SetWorldVolumeDb(-20f);
        var down = Levels(rig);
        Assert.InRange(down.World - open.World, -21.0, -19.0);
        Assert.InRange(down.Host - open.Host, -0.5, 0.5);

        // Beside the fade: the two multiply.
        rig.Facade.SetWorldFade(0.1f);
        var both = Levels(rig);
        Assert.InRange(both.World - open.World, -41.5, -38.5);
        Assert.InRange(both.Host - open.Host, -0.5, 0.5);

        rig.Facade.SetWorldFade(1f);
        rig.Facade.SetWorldVolumeDb(0f);
        var back = Levels(rig);
        Assert.InRange(back.World - open.World, -0.7, 0.7);
        ClosesClean(rig);
    }

    [FmodFact]
    public void The_master_volume_taken_down_twenty_decibels_takes_a_steady_sound_down_by_about_that()
    {
        var rig = new Rig(_dir);
        var open = Levels(rig);
        rig.Facade.SetMasterVolumeDb(-20f);
        var down = Levels(rig);
        Assert.InRange(down.World - open.World, -21.0, -19.0);
        Assert.InRange(down.Host - open.Host, -21.0, -19.0);

        // A number that is not a number is no change from the start.
        rig.Facade.SetMasterVolumeDb(float.NaN);
        var nan = Levels(rig);
        Assert.InRange(nan.Host - open.Host, -0.5, 0.5);
        rig.Facade.SetMasterVolumeDb(0f);
        ClosesClean(rig);
    }

    [FmodFact]
    public void A_master_volume_step_is_ramped_and_not_a_click()
    {
        var rig = new Rig(_dir, worldTone: false);
        HostSoundsTests.Hear(rig.Facade, 0.6);
        Facade_Step(rig);
        ClosesClean(rig);
    }

    [FmodFact]
    public void The_world_muffle_takes_the_high_band_of_the_world_and_leaves_the_low_band_and_the_hosts_sound()
    {
        const float low = 200f, high = 6000f;
        var rig = new Rig(_dir, worldHz: low);
        Assert.True(rig.Facade.AddSound("high", HostSoundsTests.Wav(high, 1.0f)));
        rig.Facade.Submit(HostSoundsTests.Emitter(2, "high", loop: true, at: new Vector3(0f, 0f, 2f)));
        int rate = rig.Facade.MixerRate;

        double[] Read()
        {
            HostSoundsTests.Hear(rig.Facade, 0.6);
            var x = rig.RecordTo(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".wav"), 0.8);
            return new[] { ToneDb(x, low, rate), ToneDb(x, high, rate), ToneDb(x, HostHz, rate) };
        }

        var open = Read();
        Assert.True(open[1] > -90.0, $"the high tone is not in the mix: {open[1]:F1} dB");

        rig.Facade.SetWorldMuffle(1000f);
        var muffled = Read();
        Assert.True(muffled[1] - open[1] < -25.0, $"the high band fell only {open[1] - muffled[1]:F1} dB");
        Assert.InRange(muffled[0] - open[0], -1.5, 1.0);
        Assert.InRange(muffled[2] - open[2], -0.5, 0.5);

        // Off again: the open mix back.
        rig.Facade.SetWorldMuffle(0f);
        var back = Read();
        Assert.InRange(back[1] - open[1], -0.7, 0.7);
        Assert.InRange(back[0] - open[0], -0.7, 0.7);

        // Numbers that are not a cutoff are off.
        rig.Facade.SetWorldMuffle(float.NaN);
        rig.Facade.SetWorldMuffle(-5f);
        ClosesClean(rig);
    }

    private void Facade_Step(Rig rig)
    {
        rig.Facade.FmodSystem.getMasterChannelGroup(out var master);
        string path = Path.Combine(_dir, "step.wav");
        var tap = MasterTap.Attach(rig.Facade.FmodSystem, master, path, rig.Facade.MixerRate, asFloat: true);
        Assert.NotNull(tap);
        HostSoundsTests.Hear(rig.Facade, 0.4);
        rig.Facade.SetMasterVolumeDb(-20f);
        HostSoundsTests.Hear(rig.Facade, 0.5);
        tap!.Dispose();
        var x = ReadWav(path)[0];
        int rate = rig.Facade.MixerRate;
        int stepAt = (int)(0.4 * rate);
        double before = 0, after = 0;
        for (int i = 2048; i < stepAt - 4096; i++) before = Math.Max(before, Math.Abs(x[i] - x[i - 1]));
        for (int i = stepAt - 4096; i < x.Length; i++) after = Math.Max(after, Math.Abs(x[i] - x[i - 1]));
        // A tone's steepest step is set by its own height and pitch; a click is a step beyond it.
        Assert.True(before > 0.005, "the tone is not in the recording");
        Assert.True(after <= before * 1.05, $"a step in the gain showed as a click: {after:F5} against the tone's {before:F5}");
        double last = 0;
        for (int i = x.Length - 8192; i < x.Length; i++) last = Math.Max(last, Math.Abs(x[i]));
        Assert.True(last < 0.02, $"the master did not settle at -20 dB: peak {last}");
    }
}
