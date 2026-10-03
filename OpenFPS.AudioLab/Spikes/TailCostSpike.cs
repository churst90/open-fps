using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// What the late tail of the room you stand in costs the mixer, per 256-sample piece, on one thread
/// with nothing else running: the one-channel late part (LateTailConvolver, then DiffuseTail's velvet
/// branches, head responses and ear velvet) against the late field (DiffuseLateConvolver, then a head
/// response per direction). A synthetic room, 60 dB down in <c>t60</c> seconds, so the whole two
/// seconds are in use at the default.
///
///   --tail-cost [t60=2] [seconds=20]
/// </summary>
public static class TailCostSpike
{
    private const int Fs = 44100, Sub = TracedReverb.TracedFrame;

    public static int Run(string[] args)
    {
        double t60 = double.TryParse(Arg(args, "t60"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t) ? t : 2.0;
        double seconds = double.TryParse(Arg(args, "seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 20.0;
        int len = 2 * Fs;
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no context"); return 1; }
        var au = new Phonon.IPLAudioSettings { samplingRate = Fs, frameSize = Sub };
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);

        // One trace of a room from everywhere, decaying 60 dB in t60.
        var rng = new Random(3);
        var w = new float[len];
        for (int i = 0; i < len; i++)
            w[i] = (float)((rng.NextDouble() * 2 - 1) * Math.Pow(10, -3.0 * i / (t60 * Fs)));
        var c = Enumerable.Range(0, 3).Select(_ => w.Select(v => v * (float)(rng.NextDouble() * 2 - 1) * 0.3f).ToArray()).ToArray();
        var dirs = Enumerable.Range(0, DiffuseBranch.Count).Select(DiffuseTail.Direction).ToArray();
        var tail = new SmoothTail(Fs, len, DiffuseBranch.Count);
        tail.Add(w, c[0], c[1], c[2], Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, dirs, null, Vector3.Zero, 1, false);
        var late = tail.BuildLate(Sub, len / Sub + 1, afterDirectional: true);
        var sw = Stopwatch.StartNew();
        var noise = DiffuseLateNoise.Shared(Fs, len, DiffuseBranch.Count);
        double noiseMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var field = tail.BuildDiffuseLate(noise);
        double buildMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"t60 {t60:F1} s: the one-channel late part {late.Partitions} blocks of {Sub}; the field {field.Partitions} blocks of {DiffuseLateNoise.Block}, x {DiffuseBranch.Count} directions");
        Console.WriteLine($"  the field's noise made once: {noiseMs:F0} ms, {4.0 * noise.N0Re.Length * 4 / 1e6:F0} MB; each trace's envelope: {buildMs:F1} ms");

        int n = (int)(seconds * Fs) / Sub * Sub;
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)(rng.NextDouble() * 2 - 1) * 0.1f;
        double Us(long ticks) => ticks * 1e6 / Stopwatch.Frequency;

        // The one-channel way, as TracedReverbDsp ran it.
        {
            var lc = new LateTailConvolver(Sub, len / Sub + 1);
            lc.SetIr(late);
            var df = DiffuseTail.Create(ctx, Sub, TracedReverb.Channels, hrtf)!;
            var ambi = new float[Sub * TracedReverb.Channels]; var tmp = new float[Sub];
            long conv = 0, render = 0, worst = 0; int pieces = 0;
            for (int at = 0; at + Sub <= n; at += Sub)
            {
                long t0 = Stopwatch.GetTimestamp();
                lc.Process(x.AsSpan(at, Sub), tmp);
                long t1 = Stopwatch.GetTimestamp();
                Array.Clear(ambi);
                for (int k = 0; k < Sub; k++) ambi[k * TracedReverb.Channels] = tmp[k];
                df.RenderBinaural(ambi, Sub, TracedReverb.Channels);
                long t2 = Stopwatch.GetTimestamp();
                if (at < Fs) continue;                          // warm up
                conv += t1 - t0; render += t2 - t1; worst = Math.Max(worst, t2 - t0); pieces++;
            }
            Console.WriteLine($"  one channel, velvet: convolution {Us(conv) / pieces:F0} us, velvet + head responses + ear velvet {Us(render) / pieces:F0} us; total {Us(conv + render) / pieces:F0} us a piece ({100 * Us(conv + render) / pieces / (1e6 * Sub / Fs):F1} % of a core), worst {Us(worst):F0} us");
            df.Release();
        }

        // The field, as TracedReverbDsp runs it now. Swapped every 250 ms, as the tracer does.
        {
            var lf = new DiffuseLateConvolver(Sub, noise.Partitions, DiffuseBranch.Count, noise.Start);
            var other = tail.BuildDiffuseLate(noise);
            var df = DiffuseTail.Create(ctx, Sub, TracedReverb.Channels, hrtf)!;
            long conv = 0, render = 0, worst = 0, worstConv = 0; int pieces = 0;
            int per = Fs / 4 / Sub;
            for (int at = 0, piece = 0; at + Sub <= n; at += Sub, piece++)
            {
                lf.Set((piece / per) % 2 == 0 ? field : other);
                long t0 = Stopwatch.GetTimestamp();
                lf.Process(x.AsSpan(at, Sub), df.LateIn);
                long t1 = Stopwatch.GetTimestamp();
                df.RenderLate(Sub);
                long t2 = Stopwatch.GetTimestamp();
                if (at < Fs) continue;
                conv += t1 - t0; render += t2 - t1; worst = Math.Max(worst, t2 - t0); worstConv = Math.Max(worstConv, t1 - t0); pieces++;
            }
            Console.WriteLine($"  the field: convolution {Us(conv) / pieces:F0} us (worst piece {Us(worstConv):F0}), head responses {Us(render) / pieces:F0} us; total {Us(conv + render) / pieces:F0} us a piece ({100 * Us(conv + render) / pieces / (1e6 * Sub / Fs):F1} % of a core), worst {Us(worst):F0} us");
            df.Release();
        }

        // The pieces of it.
        {
            var fft = new Fft(2 * DiffuseLateNoise.Block);
            var re = new float[2 * DiffuseLateNoise.Block]; var im = new float[2 * DiffuseLateNoise.Block];
            for (int i = 0; i < re.Length; i++) re[i] = (float)rng.NextDouble();
            for (int k = 0; k < 20; k++) fft.Forward(re, im);
            long t0 = Stopwatch.GetTimestamp();
            for (int k = 0; k < 200; k++) { fft.Forward(re, im); fft.Inverse(re, im); }
            Console.WriteLine($"  one transform of {2 * DiffuseLateNoise.Block}: {Us(Stopwatch.GetTimestamp() - t0) / 400:F0} us");
        }
        Phonon.iplHRTFRelease(ref hrtf);
        Phonon.iplContextRelease(ref ctx);
        return 0;
    }

    private static string? Arg(string[] args, string key)
    {
        foreach (var a in args) if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[(key.Length + 1)..];
        return null;
    }
}
