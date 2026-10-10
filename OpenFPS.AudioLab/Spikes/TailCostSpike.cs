using System.Diagnostics;
using System.Numerics;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// --tail-cost [t60=2] [seconds=20] [ears=two] [others=1]: the late tail's cost per 256-sample piece on
/// one idle thread. The one-channel late part (LateTailConvolver, DiffuseTail's velvet branches, head
/// responses, ear velvet) against the room you are in as it plays: the late field (DiffuseLateConvolver)
/// and the directional part (SharedInputConvolver), one head response per direction for both.
/// ears=two: the directional part through head responses of its own, as before 2026-10-09. others=1:
/// any other room's stage too (Steam Audio's convolution of a traced concrete room). A synthetic room
/// 60 dB down in t60 seconds, so at the default the whole two seconds are in use.
///
/// Run it with DOTNET_TieredCompilation=0, pinned to one core. With tiering on, pinned so, the room
/// you are in measured 1,071 us a piece where the fully optimised code takes 763 (2026-10-09): the
/// first sections timed code the runtime had not yet optimised.
/// </summary>
public static class TailCostSpike
{
    private const int Fs = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.DefaultRate, Sub = TracedReverb.TracedFrame;
    /// <summary>The mixer's block (FmodAudioProvider's setDSPBufferSize) in the stage's pieces.</summary>
    private const int MixerPieces = 1024 / Sub;

    public static int Run(string[] args)
    {
        double t60 = double.TryParse(Arg(args, "t60"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t) ? t : 2.0;
        bool twoEars = Arg(args, "ears") == "two";
        double seconds = double.TryParse(Arg(args, "seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 20.0;
        int len = 2 * Fs;
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no context"); return 1; }
        var au = new Phonon.IPLAudioSettings { samplingRate = Fs, frameSize = Sub };
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);

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
            var df = DiffuseTail.Create(ctx, Sub, TracedReverb.Channels, hrtf, Fs)!;
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

        // The field and the directional part, as TracedReverbDsp runs them for the room you are in.
        // Swapped every 250 ms, as the tracer does.
        {
            var lf = new DiffuseLateConvolver(Sub, noise.Partitions, DiffuseBranch.Count, noise.Start);
            var other = tail.BuildDiffuseLate(noise);
            var sdm = tail.BuildDirectional(Sub);
            var sdmOther = tail.BuildDirectional(Sub);
            var sc = new SharedInputConvolver(Sub, SdmTailIr.PartitionsFor(Fs, Sub), DiffuseBranch.Count);
            var df = DiffuseTail.Create(ctx, Sub, TracedReverb.Channels, hrtf, Fs)!;
            long conv = 0, sconv = 0, render = 0, worst = 0, worstConv = 0, worstBlock = 0, block = 0; int pieces = 0;
            var sByPiece = new long[MixerPieces];
            int per = Fs / 4 / Sub;
            for (int at = 0, piece = 0; at + Sub <= n; at += Sub, piece++)
            {
                bool even = (piece / per) % 2 == 0;
                lf.Set(even ? field : other);
                sc.Set(even ? sdm : sdmOther);
                long t0 = Stopwatch.GetTimestamp();
                lf.Process(x.AsSpan(at, Sub), df.LateIn);
                long t1 = Stopwatch.GetTimestamp();
                sc.Process(x.AsSpan(at, Sub), df.SdmOut);
                long t2 = Stopwatch.GetTimestamp();
                if (twoEars) { df.RenderLate(Sub); df.AddDirectional(Sub); }
                else df.RenderLate(Sub, directional: true);
                long t3 = Stopwatch.GetTimestamp();
                if (at < Fs) continue;
                conv += t1 - t0; sconv += t2 - t1; render += t3 - t2; sByPiece[pieces % MixerPieces] += t2 - t1;
                worst = Math.Max(worst, t3 - t0); worstConv = Math.Max(worstConv, t1 - t0); pieces++;
                block += t3 - t0;
                if (pieces % MixerPieces == 0) { worstBlock = Math.Max(worstBlock, block); block = 0; }
            }
            double total = Us(conv + sconv + render) / pieces;
            Console.WriteLine($"  the field: convolution {Us(conv) / pieces:F0} us (worst piece {Us(worstConv):F0})");
            Console.WriteLine($"  the directional part ({sdm.PerDirection.Count(p => p != null)} directions, up to {SdmTailIr.PartitionsFor(Fs, Sub)} blocks of {Sub}): convolution {Us(sconv) / pieces:F0} us (" + string.Join(", ", sByPiece.Select(t => $"{Us(t) * MixerPieces / pieces:F0}")) + " by piece of the mixer block)");
            Console.WriteLine($"  head responses ({(twoEars ? "one for the field and one for the directional part" : "one per direction for both")}): {Us(render) / pieces:F0} us");
            Console.WriteLine($"  the room you are in: {total:F0} us a piece ({100 * total / (1e6 * Sub / Fs):F1} % of a core), worst piece {Us(worst):F0} us; "
                            + $"{total * MixerPieces:F0} us a mixer block of {MixerPieces * Sub}, worst block {Us(worstBlock):F0} us");
            df.Release();
        }

        // Any other room's stage: Steam Audio's convolution of the whole traced response, decoded round
        // the head (TracedReverbDsp, not TailOnly). A concrete room, traced.
        if (Arg(args, "others") == "1")
        {
            OpenFPS.Common.AcousticRegistry.Initialize();
            var q = Quaternion.Identity;
            var room = new List<SteamAudioScene.Box>
            {
                new(new Vector3(0, -0.25f, 0), new Vector3(600, 0.5f, 600), q, "Concrete"),
                new(new Vector3(0, 1.5f, -6), new Vector3(12, 3, 0.3f), q, "Concrete"), new(new Vector3(0, 1.5f, 6), new Vector3(12, 3, 0.3f), q, "Concrete"),
                new(new Vector3(-6, 1.5f, 0), new Vector3(0.3f, 3, 12), q, "Concrete"), new(new Vector3(6, 1.5f, 0), new Vector3(0.3f, 3, 12), q, "Concrete"),
                new(new Vector3(0, 3.15f, 0), new Vector3(12, 0.3f, 12), q, "Concrete"),
            };
            using var scene = new SteamAudioScene(ctx);
            scene.Build(room);
            using var tr = new TracedReverb(ctx, Fs, Sub);
            tr.SetScene(scene);
            tr.SetListener(new Vector3(1f, 1.6f, 0.5f));
            var until = DateTime.UtcNow.AddSeconds(30);
            while (tr.Runs < 2 && DateTime.UtcNow < until) Thread.Sleep(50);
            if (!tr.TryGetParams(out var prm)) Console.WriteLine("  another room: no trace");
            else
            {
                var es = new Phonon.IPLReflectionEffectSettings { type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize = tr.IrSize, numChannels = TracedReverb.Channels };
                Phonon.iplReflectionEffectCreate(ctx, ref au, ref es, out IntPtr effect);
                var ds = new Phonon.IPLAmbisonicsDecodeEffectSettings { speakerLayout = Phonon.StereoLayout(), hrtf = hrtf, maxOrder = TracedReverb.Order };
                Phonon.iplAmbisonicsDecodeEffectCreate(ctx, ref au, ref ds, out IntPtr decode);
                var inBuf = new Phonon.IPLAudioBuffer(); var ambi = new Phonon.IPLAudioBuffer(); var st = new Phonon.IPLAudioBuffer();
                Phonon.iplAudioBufferAllocate(ctx, 1, Sub, ref inBuf);
                Phonon.iplAudioBufferAllocate(ctx, TracedReverb.Channels, Sub, ref ambi);
                Phonon.iplAudioBufferAllocate(ctx, 2, Sub, ref st);
                var dp = new Phonon.IPLAmbisonicsDecodeEffectParams { order = TracedReverb.Order, hrtf = hrtf, orientation = Phonon.ListenerFrame(Quaternion.Identity), binaural = Phonon.IPL_TRUE };
                var mono = new float[Sub]; var stI = new float[2 * Sub];
                long conv = 0, dec = 0; int pieces = 0;
                for (int at = 0; at + Sub <= n; at += Sub)
                {
                    Array.Copy(x, at, mono, 0, Sub);
                    long t0 = Stopwatch.GetTimestamp();
                    Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
                    Phonon.iplReflectionEffectApply(effect, ref prm, ref inBuf, ref ambi, IntPtr.Zero);
                    long t1 = Stopwatch.GetTimestamp();
                    Phonon.iplAmbisonicsDecodeEffectApply(decode, ref dp, ref ambi, ref st);
                    Phonon.iplAudioBufferInterleave(ctx, ref st, stI);
                    long t2 = Stopwatch.GetTimestamp();
                    if (at < Fs) continue;
                    conv += t1 - t0; dec += t2 - t1; pieces++;
                }
                Console.WriteLine($"  another room ({tr.IrSize} samples x {TracedReverb.Channels} channels): Steam Audio's convolution {Us(conv) / pieces:F0} us, decode {Us(dec) / pieces:F0} us a piece");
                Phonon.iplAudioBufferFree(ctx, ref inBuf); Phonon.iplAudioBufferFree(ctx, ref ambi); Phonon.iplAudioBufferFree(ctx, ref st);
                Phonon.iplAmbisonicsDecodeEffectRelease(ref decode);
                Phonon.iplReflectionEffectRelease(ref effect);
            }
        }

        // The directional part alone, held and swapped, by its piece in the long block.
        {
            var sdm = tail.BuildDirectional(Sub);
            var sdmOther = tail.BuildDirectional(Sub);
            var sc = new SharedInputConvolver(Sub, SdmTailIr.PartitionsFor(Fs, Sub), DiffuseBranch.Count);
            var outs = Enumerable.Range(0, DiffuseBranch.Count).Select(_ => new float[Sub]).ToArray();
            foreach (bool swaps in new[] { false, true })
            {
                sc.Set(sdm);
                var byPiece = new long[MixerPieces]; int pieces = 0;
                for (int at = 0, piece = 0; at + Sub <= n; at += Sub, piece++)
                {
                    if (swaps && piece % 47 == 0) sc.Set((piece / 47) % 2 == 0 ? sdm : sdmOther);
                    long t0 = Stopwatch.GetTimestamp();
                    sc.Process(x.AsSpan(at, Sub), outs);
                    if (at < Fs) continue;
                    byPiece[piece % MixerPieces] += Stopwatch.GetTimestamp() - t0; pieces++;
                }
                Console.WriteLine($"  the directional part alone, {(swaps ? "a new trace every 47 pieces" : "one trace")}: "
                                + string.Join(", ", byPiece.Select(t => $"{Us(t) / (pieces / MixerPieces):F0}")) + " us by piece of the mixer block");
            }
        }

        // The field's pieces, one at a time.
        {
            var fft = new Fft(2 * DiffuseLateNoise.Block);
            var re = new float[2 * DiffuseLateNoise.Block]; var im = new float[2 * DiffuseLateNoise.Block];
            for (int i = 0; i < re.Length; i++) re[i] = (float)rng.NextDouble();
            for (int k = 0; k < 20; k++) fft.Forward(re, im);
            long t0 = Stopwatch.GetTimestamp();
            for (int k = 0; k < 200; k++) { fft.Forward(re, im); fft.Inverse(re, im); }
            Console.WriteLine($"  one transform of {2 * DiffuseLateNoise.Block}: {Us(Stopwatch.GetTimestamp() - t0) / 400:F0} us");
            var small = new Fft(2 * Sub);
            var sr = new float[2 * Sub]; var si = new float[2 * Sub];
            for (int i = 0; i < sr.Length; i++) sr[i] = (float)rng.NextDouble();
            t0 = Stopwatch.GetTimestamp();
            for (int k = 0; k < 20000; k++) { small.Forward(sr, si); small.Inverse(sr, si); }
            Console.WriteLine($"  one transform of {2 * Sub}: {Us(Stopwatch.GetTimestamp() - t0) / 40000:F2} us");
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
