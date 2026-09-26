using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --traced-echoes: the per-source tracer, measured headless on a street canyon (two rows of brick
/// buildings 20 m apart on asphalt). Six sources traced at once: what a trace costs, what one
/// source's effect costs the mixer per block, and — driving one source down the street at 15 m/s
/// past a listener on the pavement — how the echo energy and its early part move from trace to
/// trace: smoothly, or in jumps as the mirror images did.
/// </summary>
public static class TracedEchoesSpike
{
    public static int Run()
    {
        AcousticRegistry.Initialize();
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("context failed"); return 1; }
        var q = Quaternion.Identity;
        var boxes = new List<SteamAudioScene.Box> { new(new Vector3(0, -0.25f, 0), new Vector3(600, 0.5f, 600), q, "Asphalt") };
        for (int i = 0; i < 12; i++)
        {
            float z = -110f + i * 20f;
            boxes.Add(new(new Vector3(-15f, 9f, z), new Vector3(10f, 18f, 18f), q, "Brick"));
            boxes.Add(new(new Vector3(15f, 9f, z), new Vector3(10f, 18f, 18f), q, "Brick"));
        }
        using var scene = new SteamAudioScene(ctx);
        scene.Build(boxes);
        using var tr = new TracedEchoes(ctx);
        tr.SetScene(scene);
        var ear = new Vector3(8f, 1.6f, 0f);
        tr.SetListener(ear);
        var slots = new List<int>();
        for (int k = 0; k < TracedEchoes.MaxSources; k++) slots.Add(tr.Acquire(new Vector3(-3f + k, 0.5f, -40f + 15f * k)));
        var until = DateTime.UtcNow.AddSeconds(20);
        while (tr.Runs < 3 && DateTime.UtcNow < until) Thread.Sleep(50);
        Console.WriteLine($"six sources: trace {tr.LastRunMs:F0} ms (refresh every 125 ms)");
        {
            // Does a source that has not moved come back with the same IR every trace, or does the
            // ray sampling change it? Metered by the same impulse method, five traces in a row.
            var au0 = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = 1024 };
            var es0 = new Phonon.IPLReflectionEffectSettings { type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize = tr.IrSize, numChannels = TracedEchoes.Channels };
            Phonon.iplReflectionEffectCreate(ctx, ref au0, ref es0, out IntPtr fx);
            var ib = new Phonon.IPLAudioBuffer(); var ob = new Phonon.IPLAudioBuffer();
            Phonon.iplAudioBufferAllocate(ctx, 1, 1024, ref ib);
            Phonon.iplAudioBufferAllocate(ctx, TracedEchoes.Channels, 1024, ref ob);
            var m = new float[1024]; var il = new float[1024 * TracedEchoes.Channels];
            var line = new List<string>();
            for (int k = 0; k < 5; k++)
            {
                int r0 = tr.Runs;
                while (tr.Runs < r0 + 1) Thread.Sleep(10);
                tr.TryGetParams(slots[0], tr.NewestBank, out var pp);
                Phonon.iplReflectionEffectReset(fx);
                double e = 0, first = 0;
                for (int b = 0; b < 44100 * 2 / 1024; b++)
                {
                    Array.Clear(m); if (b == 0) m[0] = 1f;
                    Phonon.iplAudioBufferDeinterleave(ctx, m, ref ib);
                    Phonon.iplReflectionEffectApply(fx, ref pp, ref ib, ref ob, IntPtr.Zero);
                    Phonon.iplAudioBufferInterleave(ctx, ref ob, il);
                    for (int j = 0; j < 1024; j++) { double x = il[j * TracedEchoes.Channels]; e += x * x; if (b * 1024 + j < 4410) first += x * x; }
                }
                line.Add($"{10 * Math.Log10(e + 1e-20):F2}/{10 * Math.Log10(first + 1e-20):F2}");
            }
            Console.WriteLine("same position, five traces (total / first 100 ms, dB): " + string.Join("  ", line));
            Phonon.iplReflectionEffectRelease(ref fx);
        }
        for (int k = 1; k < slots.Count; k++) tr.Release(slots[k]);

        var au = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = 1024 };
        var es = new Phonon.IPLReflectionEffectSettings { type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize = tr.IrSize, numChannels = TracedEchoes.Channels };
        Phonon.iplReflectionEffectCreate(ctx, ref au, ref es, out IntPtr effect);
        var inBuf = new Phonon.IPLAudioBuffer(); var outBuf = new Phonon.IPLAudioBuffer();
        Phonon.iplAudioBufferAllocate(ctx, 1, 1024, ref inBuf);
        Phonon.iplAudioBufferAllocate(ctx, TracedEchoes.Channels, 1024, ref outBuf);
        var mono = new float[1024];
        var inter = new float[1024 * TracedEchoes.Channels];

        // Drive slot 0 down the street, 15 m/s, from 45 m before the listener to 45 m past; at each
        // trace, play an impulse through a fresh effect and meter the echo's W channel: all of it,
        // and the first 60 ms after the direct path would have arrived.
        Console.WriteLine("  z (m)  dist   echo total dB   early 60 ms dB   (re the direct sound at 1 m)");
        int s0 = slots[0];
        for (float z = -45f; z <= 45f; z += 15f * 0.125f)
        {
            var src = new Vector3(0f, 0.5f, z);
            tr.SetSource(s0, src);
            int r0 = tr.Runs;
            var t = DateTime.UtcNow.AddSeconds(5);
            while (tr.Runs < r0 + 2 && DateTime.UtcNow < t) Thread.Sleep(10);
            if (!tr.TryGetParams(s0, tr.NewestBank, out var prm)) { Console.WriteLine($"{z,6:F1}  no IR"); continue; }
            Phonon.iplReflectionEffectReset(effect);
            var w = new List<float>();
            for (int b = 0; b < 44100 * 2 / 1024; b++)
            {
                Array.Clear(mono);
                if (b == 0) mono[0] = 1f;
                Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
                Phonon.iplReflectionEffectApply(effect, ref prm, ref inBuf, ref outBuf, IntPtr.Zero);
                Phonon.iplAudioBufferInterleave(ctx, ref outBuf, inter);
                for (int k = 0; k < 1024; k++) w.Add(inter[k * TracedEchoes.Channels]);
            }
            float d = Vector3.Distance(src, ear);
            int direct = (int)(d / 343f * 44100f);
            double all = 0, early = 0;
            for (int k = 0; k < w.Count; k++)
            {
                all += w[k] * (double)w[k];
                if (k >= direct && k < direct + 2646) early += w[k] * (double)w[k];
            }
            Console.WriteLine($"{z,7:F1} {d,5:F1}   {10 * Math.Log10(all + 1e-20),13:F1}   {10 * Math.Log10(early + 1e-20),14:F1}");
        }
        // Steadiness: steady noise into a source driving down the street at 15 m/s, the echo metered in
        // 23 ms blocks. One effect following the newest trace (the old way) against two, one per
        // bank, crossfaded towards the newer over the refresh (the rig's way). Reported as the mean
        // block-to-block change in level, dB, after smoothing out the noise's own fluctuation over
        // four blocks.
        {
            var ra = Phonon.iplReflectionEffectCreate(ctx, ref au, ref es, out IntPtr fa);
            var rb = Phonon.iplReflectionEffectCreate(ctx, ref au, ref es, out IntPtr fb);
            var r1 = Phonon.iplReflectionEffectCreate(ctx, ref au, ref es, out IntPtr f1);
            Console.WriteLine($"effects: A {ra} {fa}, B {rb} {fb}, single {r1} {f1}");
            var ob2 = new Phonon.IPLAudioBuffer(); var ob3 = new Phonon.IPLAudioBuffer();
            Phonon.iplAudioBufferAllocate(ctx, TracedEchoes.Channels, 1024, ref ob2);
            Phonon.iplAudioBufferAllocate(ctx, TracedEchoes.Channels, 1024, ref ob3);
            var ia = new float[1024 * TracedEchoes.Channels]; var ib = new float[1024 * TracedEchoes.Channels];
            var rngn = new Random(7);
            var single = new List<double>(); var dual = new List<double>();
            float blend = -1f, stepB = 1f / (TracedEchoes.CrossfadeSeconds * 44100f);
            var t0 = DateTime.UtcNow; float z0 = -40f;
            // The single effect reads its OWN source: a Steam Audio IR update goes to the first effect
            // that reads it, and a shared one starves the other.
            int s1 = tr.Acquire(new Vector3(0f, 0.5f, z0));
            int ch = TracedEchoes.Channels;
            for (int b = 0; b < 44100 * 6 / 1024; b++)
            {
                float zNow = z0 + 15f * (float)(DateTime.UtcNow - t0).TotalSeconds;
                tr.SetSource(s0, new Vector3(0f, 0.5f, zNow));
                tr.SetSource(s1, new Vector3(0f, 0.5f, zNow));
                for (int k = 0; k < 1024; k++) mono[k] = (float)(rngn.NextDouble() * 2 - 1);
                Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
                int newest = tr.NewestBank;
                bool hA = tr.TryGetParams(s0, 0, out var pa), hB = tr.TryGetParams(s0, 1, out var pb);
                tr.TryGetParams(s1, newest, out var pn);
                Phonon.iplReflectionEffectApply(f1, ref pn, ref inBuf, ref outBuf, IntPtr.Zero);
                Phonon.iplAudioBufferInterleave(ctx, ref outBuf, inter);
                double e1 = 0; for (int k = 0; k < 1024; k++) e1 += inter[k * ch] * (double)inter[k * ch];
                single.Add(10 * Math.Log10(e1 + 1e-20));
                bool swap = Environment.GetEnvironmentVariable("SWAP") == "1";
                Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
                if (hA) Phonon.iplReflectionEffectApply(swap ? fb : fa, ref pa, ref inBuf, ref ob2, IntPtr.Zero);
                Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
                if (hB) Phonon.iplReflectionEffectApply(swap ? fa : fb, ref pb, ref inBuf, ref ob3, IntPtr.Zero);
                Phonon.iplAudioBufferInterleave(ctx, ref ob2, ia);
                Phonon.iplAudioBufferInterleave(ctx, ref ob3, ib);
                float target = newest == 1 ? 1f : 0f;
                if (blend < 0f) blend = target;
                double e2 = 0;
                for (int k = 0; k < 1024; k++)
                {
                    blend = target > blend ? MathF.Min(target, blend + stepB) : MathF.Max(target, blend - stepB);
                    double w = ia[k * ch] * (1 - blend) + ib[k * ch] * blend;
                    e2 += w * w;
                }
                dual.Add(10 * Math.Log10(e2 + 1e-20));
                if (b < 60 && b % 6 == 0) Console.WriteLine($"   pa ir {pa.ir} ch {pa.numChannels} size {pa.irSize} type {pa.type} | pb ir {pb.ir} ch {pb.numChannels} size {pb.irSize} type {pb.type} | pn ir {pn.ir}");
                if (b < 60)
                {
                    double ea = 0, eb = 0; for (int k = 0; k < 1024; k++) { ea += ia[k * ch] * (double)ia[k * ch]; eb += ib[k * ch] * (double)ib[k * ch]; }
                    Console.WriteLine($"  b{b,3} newest {newest} hA {hA} hB {hB} A {10 * Math.Log10(ea + 1e-20),7:F1} B {10 * Math.Log10(eb + 1e-20),7:F1} single {single[^1],7:F1} blend {blend:F2}");
                }
                Thread.Sleep(20);
            }
            double Rough(List<double> v)
            {
                var sm = new List<double>();
                for (int k = 33; k < v.Count; k++) sm.Add((v[k] + v[k - 1] + v[k - 2] + v[k - 3]) / 4);
                double d = 0; for (int k = 1; k < sm.Count; k++) d += Math.Abs(sm[k] - sm[k - 1]);
                double mx = 0; for (int k = 1; k < sm.Count; k++) mx = Math.Max(mx, Math.Abs(sm[k] - sm[k - 1]));
                return d / (sm.Count - 1) + mx * 0;
            }
            double Worst(List<double> v)
            {
                var sm = new List<double>();
                for (int k = 33; k < v.Count; k++) sm.Add((v[k] + v[k - 1] + v[k - 2] + v[k - 3]) / 4);
                double mx = 0; for (int k = 1; k < sm.Count; k++) mx = Math.Max(mx, Math.Abs(sm[k] - sm[k - 1]));
                return mx;
            }
            Console.WriteLine($"driving past: block-to-block change, one effect {Rough(single):F2} dB (worst {Worst(single):F2}), two banks crossfaded {Rough(dual):F2} dB (worst {Worst(dual):F2})");
        }
        // Two stages on the listener's trace: sharing one reader (as every outdoor bus used to) and
        // with a reader each. What each stage's effect hands back, dB, after the traces have run.
        {
            using var lt = new TracedReverb(ctx);
            lt.SetScene(scene);
            lt.SetListener(ear);
            lt.EnsureReader(1);
            var until2 = DateTime.UtcNow.AddSeconds(20);
            while (lt.Runs < 3 && DateTime.UtcNow < until2) Thread.Sleep(50);
            var es2 = new Phonon.IPLReflectionEffectSettings { type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize = lt.IrSize, numChannels = TracedReverb.Channels };
            double Stage(int[] readers)
            {
                var fxs = new IntPtr[readers.Length];
                for (int k = 0; k < fxs.Length; k++) Phonon.iplReflectionEffectCreate(ctx, ref au, ref es2, out fxs[k]);
                var outs = new double[readers.Length];
                var ob4 = new Phonon.IPLAudioBuffer();
                Phonon.iplAudioBufferAllocate(ctx, TracedReverb.Channels, 1024, ref ob4);
                var il4 = new float[1024 * TracedReverb.Channels];
                var rn = new Random(3);
                for (int b = 0; b < 44100 * 2 / 1024; b++)
                {
                    for (int k = 0; k < 1024; k++) mono[k] = (float)(rn.NextDouble() * 2 - 1);
                    for (int r = 0; r < readers.Length; r++)
                    {
                        Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
                        if (!lt.TryGetParams(readers[r], out var pr)) continue;
                        Phonon.iplReflectionEffectApply(fxs[r], ref pr, ref inBuf, ref ob4, IntPtr.Zero);
                        Phonon.iplAudioBufferInterleave(ctx, ref ob4, il4);
                        if (b > 40) for (int k = 0; k < 1024; k++) outs[r] += il4[k * TracedReverb.Channels] * (double)il4[k * TracedReverb.Channels];
                    }
                    Thread.Sleep(20);
                }
                for (int k = 0; k < fxs.Length; k++) Phonon.iplReflectionEffectRelease(ref fxs[k]);
                Console.WriteLine($"   readers [{string.Join(",", readers)}]: " + string.Join("  ", outs.Select(e => $"{10 * Math.Log10(e + 1e-20):F1} dB")));
                return 0;
            }
            Console.WriteLine("two stages on the listener's trace:");
            Stage(new[] { 0, 0 });
            Stage(new[] { 0, 1 });
        }
        var rng = new Random(1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        tr.TryGetParams(s0, tr.NewestBank, out var p2);
        for (int b = 0; b < 200; b++)
        {
            for (int k = 0; k < 1024; k++) mono[k] = (float)(rng.NextDouble() * 2 - 1) * 0.1f;
            Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
            Phonon.iplReflectionEffectApply(effect, ref p2, ref inBuf, ref outBuf, IntPtr.Zero);
        }
        Console.WriteLine($"one source's effect: {sw.Elapsed.TotalMilliseconds / 200:F2} ms per 23 ms block");
        Phonon.iplAudioBufferFree(ctx, ref inBuf); Phonon.iplAudioBufferFree(ctx, ref outBuf);
        Phonon.iplReflectionEffectRelease(ref effect);
        Phonon.iplContextRelease(ref ctx);
        return 0;
    }
}
