using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// --early-tail [room=flat|stair|corridor] [hrtf]: the first 120 ms of the listener's trace, energy per
/// 2 ms in three band groups, beside the first- and second-order image sources from the same point
/// (EarlyReflections, the search the placed copies use). hrtf: where an impulse leaves the binaural effect.
/// </summary>
public static class EarlyTailSpike
{
    private const int Fs = 44100;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        if (args.Contains("hrtf")) return HrtfDelay();
        string room = args.FirstOrDefault(a => a.StartsWith("room="))?[5..] ?? "flat";
        var (boxes, ear) = room.StartsWith("floor")
            ? (new List<SteamAudioScene.Box> { new(new Vector3(0, -0.1f, 0), new Vector3(200f, 0.2f, 200f), Quaternion.Identity, "Concrete") },
               new Vector3(0f, float.Parse(room.Length > 5 ? room[5..] : "1.7", System.Globalization.CultureInfo.InvariantCulture), 0f))
            : TailSteadySpike.Scene(room);
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no context"); return 1; }
        using var scene = new SteamAudioScene(ctx);
        scene.Build(boxes);
        using var tr = new TracedReverb(ctx) { ExtractLate = true };
        tr.SetScene(scene);
        tr.SetListener(ear);
        for (int t = 0; t < 300 && (tr.LastReadBack == null || tr.Runs < 3); t++) Thread.Sleep(100);
        var w = tr.LastReadBack;
        if (w == null) { Console.WriteLine("FAIL: no read-back"); return 1; }

        // Band groups of SmoothTail's octaves: below 710 Hz, 710-2840, above.
        var split = new SmoothTail.Splitter(Fs);
        Span<double> band = stackalloc double[SmoothTail.Bands];
        int bin = Fs / 500, bins = 60;
        var e = new double[bins, 4];
        for (int i = 0; i < bins * bin && i < w.Length; i++)
        {
            split.Run(w[i], band);
            int k = i / bin;
            for (int b = 0; b < SmoothTail.Bands; b++)
            {
                int g = b < 4 ? 0 : b < 6 ? 1 : 2;
                e[k, g] += band[b] * band[b];
            }
            e[k, 3] += w[i] * (double)w[i];
        }

        var solids = boxes.Select(b => new EarlyReflections.Solid(b.Center, b.Size, b.Rotation, b.Material)).ToList();
        var into = new List<EarlyReflections.Arrival>();
        var src = ear + new Vector3(0f, 0f, 0.01f);
        EarlyReflections.Find(src, ear, solids, into, 343f, maxOrder: 2, keep: 64, maxExtraPathMetres: 0.12f * 343f);
        float direct = Vector3.Distance(src, ear);
        var img = new double[bins, 4];
        Console.WriteLine($"{room}: {into.Count} images within 120 ms");
        if (EarlyCopies.From(scene.Solids, ear, Fs) is { } ec)
        {
            var ect = ec.Total();
            Console.WriteLine($"  first reflection {ec.FirstArrival * 1000.0 / Fs:F2} ms (the played response starts {tr.Smooth?.Start * 1000.0 / Fs:F2} ms); "
                            + $"{ec.Copies.Count} copies carry {10 * Math.Log10(ect.Sum()):F1} dB, per band: {string.Join(" ", ect.Select(v => (10 * Math.Log10(v + 1e-30)).ToString("F1")))}");
        }
        foreach (var a in into.OrderBy(a => a.PathLength))
        {
            double kL = a.GainLow * a.PathLength / direct, kM = a.GainMid * a.PathLength / direct, kH = a.GainHigh * a.PathLength / direct;
            double l2 = a.PathLength * (double)a.PathLength;
            int k = (int)(a.PathLength / 343.0 * 500);
            if (k < bins) { img[k, 0] += kL * kL / l2; img[k, 1] += kM * kM / l2; img[k, 2] += kH * kH / l2; img[k, 3] += kM * kM / l2; }
            Console.WriteLine($"  order {a.Order} {a.PathLength / 343.0 * 1000,6:F1} ms {a.PathLength,6:F2} m  keep {kL:F2}/{kM:F2}/{kH:F2}  s {a.Scattering:F2}  energy {10 * Math.Log10(kM * kM / l2),6:F1} dB");
        }
        Console.WriteLine("  ms    trace low/mid/high/all dB       images low/mid/high dB");
        string D(double v) => v > 0 ? $"{10 * Math.Log10(v),6:F1}" : "     -";
        for (int k = 0; k < bins; k++)
            Console.WriteLine($"  {k * 2,3}  {D(e[k, 0])} {D(e[k, 1])} {D(e[k, 2])} {D(e[k, 3])}    {D(img[k, 0])} {D(img[k, 1])} {D(img[k, 2])}");
        double tot = 0, totImg = 0;
        for (int k = 0; k < bins; k++) { tot += e[k, 1]; totImg += img[k, 1]; }
        Console.WriteLine($"  0-120 ms, mid group: trace {D(tot)} dB, images {D(totImg)} dB");

        // What is played (the published directional part and late field, every direction summed)
        // against the trace itself, 10 ms at a time, both ways round the 2026-10-03 change.
        var played = new Dictionary<bool, double[]>();
        foreach (bool old in new[] { true, false })
        {
            SmoothTail.FromFiftyMs = old;
            using var t2 = new TracedReverb(ctx) { ExtractLate = true };
            t2.SetScene(scene);
            t2.SetListener(ear);
            for (int t = 0; t < 300 && (t2.LateSdm == null || t2.Runs < 6); t++) Thread.Sleep(100);
            var e10 = new double[40];
            void Add(float[] x, int offset = 0) { for (int i = 0; i < x.Length; i++) { int k = (i + offset) / (Fs / 100); if (k < e10.Length) e10[k] += x[i] * (double)x[i]; } }
            if (t2.LateSdm is { } sdm) foreach (var d in sdm.PerDirection) Add(TailSteadySpike.ToTime(d));
            if (t2.DiffuseLate is { } dl) for (int d = 0; d < DiffuseBranch.Count; d++) Add(dl.ToTime(d));
            else Add(TailSteadySpike.ToTime(t2.Late));
            played[old] = e10;
        }
        SmoothTail.FromFiftyMs = false;
        var tr10 = new double[40];
        for (int i = 0; i < w.Length && i / (Fs / 100) < 40; i++) tr10[i / (Fs / 100)] += w[i] * (double)w[i];
        Console.WriteLine("  10 ms   trace   played before   played now (dB re a 1 m impulse)");
        for (int k = 0; k < 40; k++) Console.WriteLine($"  {k * 10,4}  {D(tr10[k])}   {D(played[true][k])}   {D(played[false][k])}");
        return 0;
    }

    /// <summary>An impulse through Steam Audio's binaural effect at the voices' frame (1,024) and the
    /// traced stage's (256): where it comes out, per direction.</summary>
    private static int HrtfDelay()
    {
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no context"); return 1; }
        foreach (int frame in new[] { 1024, 256 })
        {
            var au = new Phonon.IPLAudioSettings { samplingRate = Fs, frameSize = frame };
            var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
            Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);
            var bs = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
            foreach (var dir in new[] { new Vector3(0, 0, -1), new Vector3(1, 0, 0), new Vector3(0, -0.8f, -0.6f), new Vector3(0, 1, 0),
                                        Vector3.Zero, new Vector3(1e-20f, 0, 0), new Vector3(float.NaN, 0, 0) })
            {
                Phonon.iplBinauralEffectCreate(ctx, ref au, ref bs, out IntPtr fx);
                var inB = new Phonon.IPLAudioBuffer(); var outB = new Phonon.IPLAudioBuffer();
                Phonon.iplAudioBufferAllocate(ctx, 1, frame, ref inB); Phonon.iplAudioBufferAllocate(ctx, 2, frame, ref outB);
                var mono = new float[frame]; var st = new float[2 * frame];
                var y = new List<float>();
                for (int b = 0; b < 4; b++)
                {
                    Array.Clear(mono); if (b == 1) mono[0] = 1f;     // the impulse in the second block: the first warms the effect
                    Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inB);
                    var p = new Phonon.IPLBinauralEffectParams { direction = new Phonon.IPLVector3 { x = dir.X, y = dir.Y, z = dir.Z }, interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR, spatialBlend = 1f, hrtf = hrtf };
                    Phonon.iplBinauralEffectApply(fx, ref p, ref inB, ref outB);
                    Phonon.iplAudioBufferInterleave(ctx, ref outB, st);
                    for (int k = 0; k < frame; k++) y.Add(MathF.Abs(st[2 * k]) + MathF.Abs(st[2 * k + 1]));
                }
                if (y.Any(v => !float.IsFinite(v))) Console.WriteLine($"  frame {frame,4}, direction ({dir.X}, {dir.Y}, {dir.Z}): NOT FINITE output");
                float pk = y.Max(); int peak = y.IndexOf(pk) - frame, onset = y.FindIndex(v => v >= 0.1f * pk) - frame;
                Console.WriteLine($"  frame {frame,4}, direction ({dir.X:F1}, {dir.Y:F1}, {dir.Z:F1}): onset {onset} samples, peak {peak} samples after the impulse");
                Phonon.iplBinauralEffectRelease(ref fx);
                Phonon.iplAudioBufferFree(ctx, ref inB); Phonon.iplAudioBufferFree(ctx, ref outB);
            }
            Phonon.iplHRTFRelease(ref hrtf);
        }
        Phonon.iplContextRelease(ref ctx);
        return 0;
    }
}
