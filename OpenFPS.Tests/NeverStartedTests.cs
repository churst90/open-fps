using System.Diagnostics;
using System.Numerics;
using OpenFPS.Audio.Data;
using OpenFPS.Audio.Fmod;
using OpenFPS.Sound;
using Verdict = OpenFPS.Audio.Fmod.FmodAudioProvider.StartVerdict;

namespace OpenFPS.Tests;

/// <summary>
/// Every recording leaves its first sample (FmodAudioProvider.Starts.cs). Resonance's copy of the library found 58 in
/// about 58,000 that never did, past their delayed start, not paused and not virtual, and cut footsteps off. The
/// judgement is checked here on its own, and the whole start through a live FMOD mixer when FMOD can be loaded.
/// </summary>
public class NeverStartedTests
{
    private const ulong Margin = 4800, GiveUp = 96_000;

    [Fact]
    public void A_start_is_not_judged_before_its_margin()
        => Assert.Equal(Verdict.Wait, FmodAudioProvider.Judge(10_000, 10_000 + Margin - 1, 0, 0, false, false, Margin, GiveUp));

    [Fact]
    public void A_recording_that_moved_has_started()
        => Assert.Equal(Verdict.Started, FmodAudioProvider.Judge(10_000, 10_000 + Margin, 0, 512, false, false, Margin, GiveUp));

    [Fact]
    public void A_recording_still_where_it_began_and_free_to_play_is_stuck()
    {
        Assert.Equal(Verdict.Stuck, FmodAudioProvider.Judge(10_000, 10_000 + Margin, 0, 0, false, false, Margin, GiveUp));
        // A reflection or a joined program begins mid-file: still there is just as stuck.
        Assert.Equal(Verdict.Stuck, FmodAudioProvider.Judge(10_000, 10_000 + Margin, 7_000, 7_000, false, false, Margin, GiveUp));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_paused_or_virtual_recording_is_waited_out_and_let_be(bool paused, bool isVirtual)
    {
        Assert.Equal(Verdict.Wait, FmodAudioProvider.Judge(10_000, 10_000 + Margin, 0, 0, paused, isVirtual, Margin, GiveUp));
        Assert.Equal(Verdict.LetBe, FmodAudioProvider.Judge(10_000, 10_000 + GiveUp, 0, 0, paused, isVirtual, Margin, GiveUp));
    }

    /// <summary>
    /// A few thousand short recordings through a live mixer, their delays scattered round the parent clock's edge
    /// (inside the block the mixer is already past, on a block's edge to the sample, a block or two on), with and
    /// without pitch and fade points, in both groups: none may stay on its first sample. A control started at pitch 0
    /// cannot move, and every one of those must be counted, or the check proves nothing.
    /// </summary>
    [FmodFact]
    public void No_recording_started_across_the_clock_edge_stays_on_its_first_sample()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-never-started-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var provider = new FmodAudioProvider { WavPath = Path.Combine(dir, "mix.wav") };
        try
        {
            Assert.True(provider.Initialize());
            provider.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);
            var rnd = new Random(6061);
            string[] sounds = new string[6];
            for (int s = 0; s < sounds.Length; s++)
            {
                // Half a second: long enough to be playing still when it is judged, a margin after its start.
                var pcm = new float[24_000];
                for (int i = 0; i < pcm.Length; i++)
                    pcm[i] = (float)(rnd.NextDouble() * 2 - 1) * 0.2f * MathF.Exp(-i / (2_000f + 1_500f * s));
                sounds[s] = $"test/never-started/{s}";
                Assert.True(provider.RegisterSynthesisedSoundFloat(sounds[s], pcm, 48_000));
            }

            const float BlockMs = 1024 * 1000f / 48_000f;
            float Edge(Random r) => r.Next(3) switch
            {
                0 => (float)r.NextDouble() * BlockMs,
                1 => BlockMs * r.Next(1, 4) + r.Next(-2, 3) / 48f,
                _ => (float)r.NextDouble() * 3f * BlockMs,
            };
            int id = 2_000_000;
            void Pump(double seconds)
            {
                var t = Stopwatch.StartNew();
                while (t.Elapsed.TotalSeconds < seconds) { provider.Update(); Thread.Sleep(2); }
            }
            (int Watched, int Checked, int Stuck) Run(int count, bool edge, float pitchLow, float pitchHigh, bool oneShot, bool reflection)
            {
                var at = provider.StartCheck;
                var clock = Stopwatch.StartNew();
                double next = 0;
                for (int i = 0; i < count; i++)
                {
                    while (clock.Elapsed.TotalSeconds < next) { provider.Update(); Thread.Sleep(1); }
                    next += 1.0 / 300;
                    provider.PlaySpatialSound(new SpatialEmitter
                    {
                        EntityId = ++id, Seed = id, SoundId = sounds[rnd.Next(sounds.Length)],
                        Type = EmitterType.WorldLocked, Mode = PlaybackMode.Single,
                        Position = new Vector3((float)rnd.NextDouble() * 12f - 6f, 0.1f, (float)rnd.NextDouble() * 12f - 6f),
                        Range = 60f, MinDistance = 1f, Volume = 0.2f,
                        Pitch = pitchLow + (float)rnd.NextDouble() * (pitchHigh - pitchLow),
                        DelayMs = edge ? Edge(rnd) : 0f,
                        IsEvent = oneShot, IsReflection = reflection, Essential = !reflection,
                    });
                }
                Pump(1.0);
                var now = provider.StartCheck;
                return (now.Watched - at.Watched, now.Checked - at.Checked, now.NeverStarted - at.NeverStarted);
            }

            var kinds = new (string Name, bool Edge, float Low, float High, bool OneShot, bool Reflection)[]
            {
                ("now, one-shot", false, 1f, 1f, true, false),
                ("edge, one-shot", true, 1f, 1f, true, false),
                ("edge, one-shot, pitch", true, 0.85f, 1.18f, true, false),
                ("edge, no fades", true, 1f, 1f, false, false),
                ("edge, no fades, pitch", true, 0.85f, 1.18f, false, false),
                ("edge, reflection, pitch", true, 0.85f, 1.18f, true, true),
            };
            var report = new List<string>();
            int stuck = 0;
            foreach (var k in kinds)
            {
                var r = Run(400, k.Edge, k.Low, k.High, k.OneShot, k.Reflection);
                report.Add($"{k.Name}: watched {r.Watched}, judged {r.Checked}, stuck {r.Stuck}");
                Assert.True(r.Watched == 400 && r.Checked == 400, string.Join("; ", report));
                stuck += r.Stuck;
            }
            Assert.True(stuck == 0, string.Join("; ", report));

            var control = Run(20, true, 0f, 0f, true, false);
            Assert.True(control.Stuck == 20, $"the check missed recordings that cannot move: {control}");
        }
        finally
        {
            provider.Dispose();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}

/// <summary>
/// A test that needs a live FMOD mixer: skipped unless OPENFPS_FMOD_TESTS=1 and FMOD loads, as on GitHub's runners.
/// Run it on its own with <c>-e OPENFPS_FMOD_TESTS=1 -e LD_LIBRARY_PATH=&lt;repo&gt;/lib</c>.
/// </summary>
public sealed class FmodFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Loads = new(() =>
    {
        string? asked = System.Environment.GetEnvironmentVariable("OPENFPS_FMOD_TESTS");
        if (string.IsNullOrEmpty(asked) || asked == "0") return false;
        if (!System.Runtime.InteropServices.NativeLibrary.TryLoad(OpenFPS.Native.Platform.NativeAudioLibraries.FmodFileName, out var handle))
            return false;
        System.Runtime.InteropServices.NativeLibrary.Free(handle);
        return true;
    });

    public FmodFactAttribute()
    {
        if (!Loads.Value) Skip = "Needs a live FMOD mixer: run on its own with OPENFPS_FMOD_TESTS=1 and FMOD on the library path.";
    }
}
