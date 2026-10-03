using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using OpenFPS.Common;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Does the tail hold still while you do? The listener's trace of one place, standing still, for
/// some seconds: every trace's tail as it was played before (each trace's own samples) and as it
/// is played now (SmoothTail: energy averaged, fixed noise), side by side, through the game's own
/// convolvers and tail renderer (DiffuseTail) to two ears.
///
///   --tail-steady [room=stair|flat|corridor] [seconds=10] [jitter=CM] [skip=4]
///   jitter moves the ear that far at random each trace; skip leaves out the first traces.
///
/// Reports, per octave:
///   steady noise: its level's s.d. in 50 ms windows at the left ear, and the left-right difference's,
///     with a new trace every 250 ms as the mixer swaps them; "frozen" holds the last trace.
///   step, T20, EDT: each band's level change from one trace to the next, and the decay, per trace.
///   at the ears: an impulse through the last trace, EDT, T20, level; and the ring (below) there.
///   hum: a 110.25 Hz hum, 36 harmonics; each harmonic's level s.d. over windows of whole periods.
///     A held response reads 0, so whatever is there is the tail changing: the pulsing.
///   spectrum: third octaves of the late part, smooth against raw.
///   ring: a stretch of the tail, its spectrum against its own local median. Noise has 0.1 % of
///     bins 10 dB over and a flatness of 0.56; a ringing comb has many and less. And the largest
///     repeat in its fine structure (autocorrelation, 0.5-30 ms): a flutter shows there.
///
/// Written for "the reflections pulsate or step, very wavy, and a metallic ringing tail" in the
/// lobby and stairwells of the main street (2026-10-02).
/// </summary>
public static class TailSteadySpike
{
    private static readonly Quaternion Q = Quaternion.Identity;
    private const int Fs = 44100, Block = TracedReverb.TracedFrame;
    private static readonly double[] Octaves = { 125, 250, 500, 1000, 2000, 4000 };

    private static bool Velvet;

    private sealed class Trace
    {
        public float[] RawLate = Array.Empty<float>(), SmoothLate = Array.Empty<float>();
        public float[][] RawDirs = Array.Empty<float[]>(), SmoothDirs = Array.Empty<float[]>();
        public LateTailIr? RawLateIr, SmoothLateIr;
        public SdmTailIr? RawSdmIr, SmoothSdmIr;
        public DiffuseLateIr? Field;
        public double Weight;
    }

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string room = Arg(args, "room") ?? "stair";
        // late=velvet: the smooth tail's late part as it was, one channel through the velvet branches
        // and the ear velvet; the default is the game's, the late field (DiffuseLate). earvelvet=1 puts
        // the ear velvet on the field too.
        Velvet = Arg(args, "late") == "velvet";
        DiffuseTail.LateEarVelvet = Arg(args, "earvelvet") == "1";
        // early=old: the smooth tail as it was before 2026-10-03, from 50 ms (SmoothTail.FromFiftyMs).
        SmoothTail.FromFiftyMs = Arg(args, "early") == "old";
        double seconds = double.TryParse(Arg(args, "seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 10;
        var (boxes, ear) = Scene(room);

        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no context"); return 1; }
        using var scene = new SteamAudioScene(ctx);
        scene.Build(boxes);
        var traces = new List<Trace>();
        int want = (int)Math.Ceiling(seconds * 4) + 1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double extractMs = 0, runMs = 0;
        var smoothMs = new List<double>();
        using (var tr = new TracedReverb(ctx) { ExtractLate = true, KeepRaw = true })
        {
            tr.SetScene(scene);
            tr.SetListener(ear, 1);
            LateTailIr? seen = null;
            // jitter=CM: the ear moved this far at random each trace, as a standing player's does not
            // quite stay put (a trace from exactly the same point is the same trace).
            float jitter = float.TryParse(Arg(args, "jitter"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float jc) ? jc / 100f : 0f;
            var jr = new Random(5);
            while (traces.Count < want && sw.Elapsed.TotalSeconds < seconds * 6 + 30)
            {
                Thread.Sleep(3);
                var late = tr.Late;
                if (late == null || ReferenceEquals(late, seen)) continue;
                if (jitter > 0)
                    tr.SetListener(ear + new Vector3((float)(jr.NextDouble() * 2 - 1), (float)(jr.NextDouble() * 2 - 1), (float)(jr.NextDouble() * 2 - 1)) * jitter, 1);
                // Both are published before the next trace begins (250 ms on); read them together.
                seen = late;
                var t = new Trace
                {
                    SmoothLateIr = late, SmoothSdmIr = tr.LateSdm, RawLateIr = tr.RawLate, RawSdmIr = tr.RawLateSdm, Field = tr.DiffuseLate,
                    Weight = tr.Smooth?.LastWeight ?? 0,
                };
                traces.Add(t);
                extractMs += tr.LastExtractMs; runMs += tr.LastRunMs; smoothMs.Add(tr.LastSmoothMs);
            }
        }
        if (traces.Count < 8) { Console.WriteLine($"FAIL: only {traces.Count} traces"); return 1; }
        foreach (var t in traces)
        {
            t.RawLate = ToTime(t.RawLateIr); t.SmoothLate = ToTime(t.SmoothLateIr);
            t.RawDirs = t.RawSdmIr?.PerDirection.Select(ToTime).ToArray() ?? Array.Empty<float[]>();
            t.SmoothDirs = t.SmoothSdmIr?.PerDirection.Select(ToTime).ToArray() ?? Array.Empty<float[]>();
        }
        Console.WriteLine($"{room}: {traces.Count} traces; trace {runMs / traces.Count:F0} ms, read-back and both builds {extractMs / traces.Count:F0} ms each; the smoothed tail {smoothMs.Skip(1).Average():F0} ms (the first, with its noise made: {smoothMs[0]:F0} ms)");
        Console.WriteLine($"  weights: {string.Join(" ", traces.Take(12).Select(t => t.Weight.ToString("F2")))} ...");

        // From the fourth trace on: the average has had a second to settle.
        int skip = int.TryParse(Arg(args, "skip"), out int sk) ? sk : 4;
        var use = traces.Skip(skip).ToList();

        // ── The pulsing: steady noise through the swapping tail ──────────────────────────────
        int n = (int)(use.Count * 0.25 * Fs) / Block * Block;
        var x = new float[n];
        var rng = new Random(9);
        for (int i = 0; i < n; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);
        // At the ears, through the game's own rendering of the tail (DiffuseTail: the late part round
        // the head, the directional part from its directions, each through its head response).
        var au = new Phonon.IPLAudioSettings { samplingRate = Fs, frameSize = Block };
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);
        var raw = Render(x, use, false, false, ctx, hrtf);
        var rfz = Render(x, use, false, true, ctx, hrtf);
        var smo = Render(x, use, true, false, ctx, hrtf);
        var frz = Render(x, use, true, true, ctx, hrtf);
        int from = 2 * Fs;                        // the convolution full: two seconds of response
        Console.WriteLine("  steady noise at the left ear: level s.d. in 50 ms windows (dB); frozen = the last trace held. Then the left-right difference's s.d.:");
        Console.WriteLine("     band |   raw  frozen | smooth  frozen ||  L-R: raw  frozen | smooth  frozen");
        foreach (double f in Octaves)
        {
            double Sd1(float[][] y) => Spread(Band(y[0], f), from).Sd;
            double Ild(float[][] y) => IldSpread(Band(y[0], f), Band(y[1], f), from);
            Console.WriteLine($"  {f,7:F0} | {Sd1(raw),5:F2} {Sd1(rfz),7:F2} | {Sd1(smo),6:F2} {Sd1(frz),7:F2} ||      {Ild(raw),5:F2} {Ild(rfz),7:F2} | {Ild(smo),6:F2} {Ild(frz),7:F2}");
        }
        {
            double eR = 0, eS = 0;
            for (int i = from; i < n; i++) { eR += raw[0][i] * (double)raw[0][i] + raw[1][i] * (double)raw[1][i]; eS += smo[0][i] * (double)smo[0][i] + smo[1][i] * (double)smo[1][i]; }
            Console.WriteLine($"  level at the ears, smooth against raw: {10 * Math.Log10(eS / eR):F2} dB");
        }

        // ── Trace to trace: the step, and the decay ─────────────────────────────────────────
        Console.WriteLine("  per band, trace to trace: level step rms / max (dB), T20 mean +- s.d. (s), EDT from 50 ms (s):");
        Console.WriteLine("     band |  step raw      smooth    |  T20 raw          smooth        | late level smooth-raw (dB)");
        foreach (double f in Octaves)
        {
            var eRaw = use.Select(t => BandEnergy(Sum(t.RawLate, t.RawDirs, coherent: true), f)).ToArray();
            var eSmo = use.Select(t => BandEnergy(Sum(t.SmoothLate, t.SmoothDirs, coherent: false), f)).ToArray();
            var (rR, mR) = Steps(eRaw.Select(e => e.Total).ToArray());
            var (rS, mS) = Steps(eSmo.Select(e => e.Total).ToArray());
            var tR = eRaw.Select(e => e.T20).Where(v => v > 0).ToArray(); var tS = eSmo.Select(e => e.T20).Where(v => v > 0).ToArray();
            double lvl = 10 * Math.Log10(eSmo.Average(e => e.Total) / eRaw.Average(e => e.Total));
            Console.WriteLine($"  {f,7:F0} | {rR,5:F2} / {mR,5:F2}   {rS,5:F2} / {mS,5:F2} | {Mean(tR),5:F2} +- {Sd(tR),4:F2}    {Mean(tS),5:F2} +- {Sd(tS),4:F2} | {lvl,6:F2} | EDT {eRaw.Average(e => e.Edt),5:F2} {eSmo.Average(e => e.Edt),5:F2}");
        }

        // ── The last trace's tail at the ears: an impulse through it, both ears' energy ───────
        {
            var imp = new float[2 * Fs + Block];
            imp[0] = 1f;
            var er = Render(imp, use, false, true, ctx, hrtf);
            var es = Render(imp, use, true, true, ctx, hrtf);
            Console.WriteLine("  the last trace at the ears (an impulse, both ears): octave | EDT raw smooth | T20 raw smooth | level 50-300 ms, 300 ms on: smooth - raw dB");
            foreach (double f in Octaves)
            {
                var (eR, tR, dR, l1R, l2R) = EarDecay(er, f);
                var (eS, tS, dS, l1S, l2S) = EarDecay(es, f);
                Console.WriteLine($"  {f,7:F0} | {dR,5:F2} {dS,5:F2} | {tR,5:F2} {tS,5:F2} | {10 * Math.Log10(l1S / l1R),6:F2} {10 * Math.Log10(l2S / l2R),6:F2}");
            }
            // The ring at the ear itself, after the head responses and the decorrelation.
            // 60-240 ms is the directional part alone (its own head responses); 400-900 ms the late
            // part alone (DiffuseTail's velvet branches, then the ear decorrelation).
            foreach (var (a, b, what) in new[] { (0.1, 0.6, "100-600 ms"), (0.06, 0.24, "60-240 ms, directional"), (0.4, 0.9, "400-900 ms, late") })
            {
                var qr = Ring(er[0], a, b); var qs = Ring(es[0], a, b);
                Console.WriteLine($"  ring at the left ear, {what}: raw {qr.Over10 * 100:F2} % over 10 dB, 99.9th {qr.P999:F1} dB, flatness {qr.Flat:F2}, repeat {qr.Repeat:F2}"
                                + $" | smooth {qs.Over10 * 100:F2} %, {qs.P999:F1} dB, {qs.Flat:F2}, {qs.Repeat:F2} at {qs.Lag:F1} ms");
                var rr2 = Ring(es[1], a, b);
                Console.WriteLine($"    right ear, smooth: {rr2.Over10 * 100:F2} %, {rr2.P999:F1} dB, flatness {rr2.Flat:F2}, repeat {rr2.Repeat:F2}");
            }
            // How alike the two ears are, per octave, in the late part of the impulse (a diffuse
            // field on this head: about 0.9 at 125 Hz, 0.7 at 250, 0.1 at 500, 0.03 above; --tail-iacc).
            foreach (var (a, b, what) in new[] { (0.06, 0.24, "60-240 ms"), (0.4, 0.9, "400-900 ms") })
            {
                var line = new System.Text.StringBuilder($"  IACC at the ears, {what}: raw");
                foreach (double f in Octaves) line.Append($" {Iacc(Band(er[0], f), Band(er[1], f), a, b):F2}");
                line.Append(" | smooth");
                foreach (double f in Octaves) line.Append($" {Iacc(Band(es[0], f), Band(es[1], f), a, b):F2}");
                Console.WriteLine(line);
            }
            if (Pieces > 0)
                Console.WriteLine($"  the smooth late part's cost per 256-sample piece ({(Velvet ? "one channel, velvet" : "field")}): convolution {FieldTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / Pieces:F0} us mean, worst {WorstFieldTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency:F0} us; directions and ears {RenderTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / Pieces:F0} us mean");
        }

        // ── A steady hum through it: each harmonic's level, 50 ms at a time ──────────────────
        // Noise in gives noise out whatever the response's fine structure; a tone does not. Its level
        // is the response's gain at that frequency, and if the fine structure changes every trace,
        // every harmonic of an engine or a voice jumps every 250 ms: the waviness.
        {
            var hum = new float[n];
            var hr = new Random(11);
            const double f0 = Fs / 400.0;          // 110.25 Hz: a period of exactly 400 samples
            int harmonics = 36;
            var ph = Enumerable.Range(0, harmonics).Select(_ => hr.NextDouble() * 2 * Math.PI).ToArray();
            for (int i = 0; i < n; i++)
            {
                double v = 0;
                for (int k = 1; k <= harmonics; k++) v += Math.Sin(2 * Math.PI * f0 * k * i / Fs + ph[k - 1]);
                hum[i] = (float)(v / harmonics);
            }
            var hRaw = Render(hum, use, false, false, ctx, hrtf)[0];
            var hRfz = Render(hum, use, false, true, ctx, hrtf)[0];
            var hSmo = Render(hum, use, true, false, ctx, hrtf)[0];
            var hFrz = Render(hum, use, true, true, ctx, hrtf)[0];
            Console.WriteLine("  a 110 Hz hum, 36 harmonics, left ear: each harmonic's level s.d. over 54 ms windows of whole periods (dB; a held response reads 0), median over the harmonics in each range:");
            Console.WriteLine("     range      |   raw  frozen | smooth  frozen");
            foreach (var (lo, hi) in new[] { (100.0, 500.0), (500.0, 1500.0), (1500.0, 4000.0) })
            {
                var ks = Enumerable.Range(1, harmonics).Where(k => f0 * k >= lo && f0 * k < hi).ToArray();
                double Med(float[] y) { var v = ks.Select(k => HarmonicSpread(y, f0 * k, from)).OrderBy(z => z).ToArray(); return v[v.Length / 2]; }
                Console.WriteLine($"  {lo,5:F0}-{hi,5:F0} Hz | {Med(hRaw),5:F2} {Med(hRfz),7:F2} | {Med(hSmo),6:F2} {Med(hFrz),7:F2}");
            }
        }

        // ── Fine structure: does one trace's tail look like the last one's? ──────────────────
        {
            double Corr(float[] a1, float[] b1)
            {
                int i0 = (int)(0.1 * Fs), i1 = Math.Min(Math.Min(a1.Length, b1.Length), (int)(0.6 * Fs));
                double ab = 0, aa = 0, bb = 0;
                for (int i = i0; i < i1; i++) { ab += a1[i] * (double)b1[i]; aa += a1[i] * (double)a1[i]; bb += b1[i] * (double)b1[i]; }
                return ab / Math.Sqrt(aa * bb + 1e-30);
            }
            var sr = use.Select(t => Sum(t.RawLate, t.RawDirs, coherent: true)).ToArray();
            var ss = use.Select(t => Sum(t.SmoothLate, t.SmoothDirs, coherent: false)).ToArray();
            double cr = Enumerable.Range(1, sr.Length - 1).Average(i => Corr(sr[i - 1], sr[i]));
            double cs2 = Enumerable.Range(1, ss.Length - 1).Average(i => Corr(ss[i - 1], ss[i]));
            Console.WriteLine($"  one trace's tail (100-600 ms) against the last one's, correlation: raw {cr:F3}, smooth {cs2:F3}");

            // The spectrum, third octaves, averaged over the traces: smooth against raw.
            var line = new System.Text.StringBuilder("  late spectrum (50 ms on), third octaves, smooth - raw dB:");
            double worst = 0;
            var pr = sr.Select(Power).ToArray(); var ps = ss.Select(Power).ToArray();
            for (int j = 0; j < 22; j++)
            {
                double fc = 100 * Math.Pow(2, j / 3.0);
                double er = pr.Average(p => ThirdOctave(p, fc)), es = ps.Average(p => ThirdOctave(p, fc));
                double d = 10 * Math.Log10(es / er);
                worst = Math.Max(worst, Math.Abs(d));
                line.Append($" {fc:F0}:{d:+0.0;-0.0}");
            }
            Console.WriteLine(line);
            Console.WriteLine($"    worst {worst:F1} dB");
        }

        // ── The ring ─────────────────────────────────────────────────────────────────────────
        var rr = use.Select(t => Ring(Sum(t.RawLate, t.RawDirs, coherent: true))).ToArray();
        var rs = use.Select(t => Ring(Sum(t.SmoothLate, t.SmoothDirs, coherent: false))).ToArray();
        Console.WriteLine("  the tail 100-600 ms: bins 10 dB over local median (noise 0.10 %), 99.9th percentile over median, flatness (noise 0.56), largest repeat 0.5-30 ms");
        Console.WriteLine($"    raw    {rr.Average(r => r.Over10) * 100,5:F2} %  {rr.Average(r => r.P999),5:F1} dB  {rr.Average(r => r.Flat),5:F2}  {rr.Average(r => r.Repeat),5:F2} at {rr.OrderByDescending(r => r.Repeat).First().Lag:F1} ms");
        Console.WriteLine($"    smooth {rs.Average(r => r.Over10) * 100,5:F2} %  {rs.Average(r => r.P999),5:F1} dB  {rs.Average(r => r.Flat),5:F2}  {rs.Average(r => r.Repeat),5:F2} at {rs.OrderByDescending(r => r.Repeat).First().Lag:F1} ms");
        Phonon.iplHRTFRelease(ref hrtf);
        Phonon.iplContextRelease(ref ctx);
        return 0;
    }

    // ── Places ─────────────────────────────────────────────────────────────────────────────────

    internal static (List<SteamAudioScene.Box>, Vector3) Scene(string room)
    {
        var b = new List<SteamAudioScene.Box>();
        if (room == "flat")
        {
            const float W = 8.65f, D = 17.86f;
            b.Add(new(new Vector3(0, 0.04f, 0), new Vector3(W, 0.04f, D), Q, "Carpet"));
            b.Add(new(new Vector3(0, -0.07f, 0), new Vector3(21f, 0.17f, 90f), Q, "Concrete"));
            b.Add(new(new Vector3(0, 2.735f, 0), new Vector3(20.5f, 0.03f, 89.3f), Q, "Plaster"));
            b.Add(new(new Vector3(4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, 80f), Q, "Brick"));
            b.Add(new(new Vector3(-4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, D), Q, "Plaster"));
            b.Add(new(new Vector3(0, 1.4f, -9.0f), new Vector3(W, 2.73f, 0.2f), Q, "Plaster"));
            b.Add(new(new Vector3(0, 1.4f, 9.0f), new Vector3(W, 2.73f, 0.2f), Q, "Plaster"));
            b.Add(new(new Vector3(3.2f, 0.32f, -3.4f), new Vector3(1.8f, 0.6f, 1.8f), Q, "Audience"));
            return (b, new Vector3(0.175f, 1.7f, 0.16f));
        }
        if (room == "corridor")
        {
            b.Add(new(new Vector3(0, -0.1f, 0), new Vector3(3.4f, 0.2f, 60f), Q, "Concrete"));
            b.Add(new(new Vector3(0, 3.1f, 0), new Vector3(3.4f, 0.2f, 60f), Q, "Concrete"));
            b.Add(new(new Vector3(-1.6f, 1.5f, 0), new Vector3(0.2f, 3f, 60f), Q, "Concrete"));
            b.Add(new(new Vector3(1.6f, 1.5f, 0), new Vector3(0.2f, 3f, 60f), Q, "Concrete"));
            b.Add(new(new Vector3(0, 1.5f, -30.1f), new Vector3(3.4f, 3f, 0.2f), Q, "Concrete"));
            b.Add(new(new Vector3(0, 1.5f, 30.1f), new Vector3(3.4f, 3f, 0.2f), Q, "Concrete"));
            return (b, new Vector3(0f, 1.6f, 0f));
        }
        // A main-street tower's stairwell, as gen_city.py builds it: 9 x 6.5 m, three storeys of
        // 3 m, a tile floor, brick on three sides, plaster on the corridor side with a doorway in it
        // on every floor, concrete slabs with the stair's void through them, concrete steps.
        const float X = 4.5f, Z = 3.25f, T = 0.35f;
        b.Add(new(new Vector3(0, -0.08f, 0), new Vector3(2 * X, 0.16f, 2 * Z), Q, "Concrete"));
        b.Add(new(new Vector3(0, 0.01f, 0), new Vector3(2 * X, 0.04f, 2 * Z), Q, "Tile"));
        for (int st = 0; st < 3; st++)
        {
            float y0 = st * 3f, top = y0 + 2.75f, mid = (y0 + top) / 2, h = 2.75f;
            b.Add(new(new Vector3(-X - T / 2, mid, 0), new Vector3(T, h, 2 * Z + 2 * T), Q, "Brick"));
            b.Add(new(new Vector3(X + T / 2, mid, 0), new Vector3(T, h, 2 * Z + 2 * T), Q, "Brick"));
            b.Add(new(new Vector3(0, mid, -Z - T / 2), new Vector3(2 * X, h, T), Q, "Brick"));
            // The corridor wall, a 1.4 m doorway in its middle, 2.1 m high.
            b.Add(new(new Vector3(-(X + 0.7f) / 2, mid, Z + T / 2), new Vector3(X - 0.7f, h, T), Q, "Plaster"));
            b.Add(new(new Vector3((X + 0.7f) / 2, mid, Z + T / 2), new Vector3(X - 0.7f, h, T), Q, "Plaster"));
            b.Add(new(new Vector3(0, y0 + 2.1f + (h - 2.1f) / 2, Z + T / 2), new Vector3(1.4f, h - 2.1f, T), Q, "Plaster"));
            // The slab over, the void over the stair (x -0.8..0.8, z -2.5..2.5) left open but on the roof.
            float sy = top + 0.125f;
            if (st == 2) b.Add(new(new Vector3(0, sy, 0), new Vector3(2 * X, 0.25f, 2 * Z), Q, "Concrete"));
            else
            {
                b.Add(new(new Vector3(-(X + 0.8f) / 2, sy, 0), new Vector3(X - 0.8f, 0.25f, 2 * Z), Q, "Concrete"));
                b.Add(new(new Vector3((X + 0.8f) / 2, sy, 0), new Vector3(X - 0.8f, 0.25f, 2 * Z), Q, "Concrete"));
                b.Add(new(new Vector3(0, sy, -(Z + 2.5f) / 2), new Vector3(1.6f, 0.25f, Z - 2.5f), Q, "Concrete"));
                b.Add(new(new Vector3(0, sy, (Z + 2.5f) / 2), new Vector3(1.6f, 0.25f, Z - 2.5f), Q, "Concrete"));
                b.Add(new(new Vector3(0, top + 0.27f, 0), new Vector3(2 * X, 0.04f, 2 * Z), Q, "Tile"));
                for (int k = 0; k < 10; k++)
                {
                    float hk = (k + 1) * 0.3f;
                    b.Add(new(new Vector3(0, y0 + hk / 2, -1.6f + 0.32f * k + 0.16f), new Vector3(1.6f, hk, 0.32f), Q, "Concrete"));
                }
            }
        }
        return (b, new Vector3(2.0f, 1.6f, 0.5f));
    }

    // ── Rendering ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The partitions of an IR back to time.</summary>
    internal static float[] ToTime(LateTailIr? ir)
    {
        if (ir == null) return Array.Empty<float>();
        int blk = ir.Block, nfft = 2 * blk;
        var fft = new Fft(nfft);
        var y = new float[ir.Partitions * blk];
        var re = new float[nfft]; var im = new float[nfft];
        for (int p = 0; p < ir.Partitions; p++)
        {
            for (int k = 0; k < ir.Bins; k++) { re[k] = ir.Re[p * ir.Bins + k]; im[k] = ir.Im[p * ir.Bins + k]; }
            for (int k = 1; k < blk; k++) { re[nfft - k] = re[k]; im[nfft - k] = -im[k]; }
            fft.Inverse(re, im);
            Array.Copy(re, 0, y, p * blk, blk);
        }
        return y;
    }

    /// <summary>The late part and every direction's, as one response. The raw parts are pieces of the
    /// same samples and add as they are; the smooth ones are independent noise and add as they are too
    /// (their energies add). Either way the sum is what a single ear's energy follows.</summary>
    private static float[] Sum(float[] late, float[][] dirs, bool coherent)
    {
        int n = Math.Max(late.Length, dirs.Length == 0 ? 0 : dirs.Max(d => d.Length));
        var y = new float[Math.Max(n, 1)];
        for (int i = 0; i < late.Length; i++) y[i] += late[i];
        foreach (var d in dirs) for (int i = 0; i < d.Length; i++) y[i] += d[i];
        return y;
    }

    /// <summary>Noise through the tail as the mixer runs it (TracedReverbDsp's tail-only path): the late
    /// convolver into DiffuseTail's directions, the directional convolver into its own, a new trace every
    /// 250 ms (or the last held, <paramref name="frozen"/>). Left and right ears.</summary>
    private static float[][] Render(float[] x, List<Trace> use, bool smooth, bool frozen, IntPtr ctx, IntPtr hrtf)
    {
        var lc = new LateTailConvolver(Block, Fs * 2 / Block + 1);
        var sc = new SharedInputConvolver(Block, SdmTailIr.PartitionsFor(Fs, Block), DiffuseBranch.Count);
        var lf = new DiffuseLateConvolver(Block, DiffuseLateNoise.PartitionsFor(Fs, 2 * Fs), DiffuseBranch.Count, DiffuseLateNoise.StartFor(Fs));
        var df = DiffuseTail.Create(ctx, Block, TracedReverb.Channels, hrtf) ?? throw new InvalidOperationException("no DiffuseTail");
        var ambi = new float[Block * TracedReverb.Channels];
        var y = new[] { new float[x.Length], new float[x.Length] };
        var tmp = new float[Block];
        int per = (int)(0.25 * Fs);
        for (int at = 0; at + Block <= x.Length; at += Block)
        {
            var t = frozen ? use[^1] : use[Math.Min(use.Count - 1, at / per)];
            var sdm = smooth ? t.SmoothSdmIr : t.RawSdmIr;
            if (smooth && !Velvet && t.Field != null)
            {
                // As the game plays it: the late part as a field (TracedReverbDsp).
                lf.Set(t.Field);
                long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                lf.Process(x.AsSpan(at, Block), df.LateIn);
                FieldTicks += System.Diagnostics.Stopwatch.GetTimestamp() - c0;
                df.LateShares = sdm?.LateShare;
                long r0 = System.Diagnostics.Stopwatch.GetTimestamp();
                df.RenderLate(Block);
                RenderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - r0;
                Pieces++;
                WorstFieldTicks = Math.Max(WorstFieldTicks, lf.WorstTicks); lf.WorstTicks = 0;
            }
            else
            {
                lc.SetIr(smooth ? t.SmoothLateIr : t.RawLateIr);
                long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                lc.Process(x.AsSpan(at, Block), tmp);
                long c1 = System.Diagnostics.Stopwatch.GetTimestamp();
                Array.Clear(ambi);
                for (int k = 0; k < Block; k++) ambi[k * TracedReverb.Channels] = tmp[k];
                df.RenderBinaural(ambi, Block, TracedReverb.Channels);
                if (smooth) { FieldTicks += c1 - c0; RenderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - c1; Pieces++; WorstFieldTicks = Math.Max(WorstFieldTicks, c1 - c0); }
            }
            if (df.SdmReady)
            {
                df.LateShares = sdm?.LateShare;
                sc.Set(sdm);
                sc.Process(x.AsSpan(at, Block), df.SdmOut);
                df.AddDirectional(Block);
            }
            for (int k = 0; k < Block; k++)
            {
                y[0][at + k] = df.Stereo[2 * k] + df.Low[k];
                y[1][at + k] = df.Stereo[2 * k + 1] + df.Low[k];
            }
        }
        df.Release();
        return y;
    }

    // What the smooth tail's late part cost to render, per 256-sample piece: its convolution, and
    // the spreading over the directions and their head responses.
    private static long FieldTicks, RenderTicks, Pieces, WorstFieldTicks;

    /// <summary>The s.d. of the left-right level difference over 50 ms windows (dB): where the tail
    /// seems to come from, moving.</summary>
    private static double IldSpread(float[] l, float[] r, int from)
    {
        int win = Fs / 20;
        var d = new List<double>();
        for (int a = from; a + win <= l.Length; a += win)
        {
            double el = 0, er = 0;
            for (int i = a; i < a + win; i++) { el += l[i] * (double)l[i]; er += r[i] * (double)r[i]; }
            d.Add(10 * Math.Log10((el + 1e-30) / (er + 1e-30)));
        }
        double m = d.Average();
        return Math.Sqrt(d.Sum(v => (v - m) * (v - m)) / d.Count);
    }

    // ── Measures ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Level spread in 50 ms windows (dB s.d.); the mean |change| between windows that a
    /// trace change falls between, and between those it does not.</summary>
    private static (double Sd, double AtChange, double Elsewhere) Spread(float[] y, int from)
    {
        int win = Fs / 20, per = (int)(0.25 * Fs);
        var lv = new List<double>(); var starts = new List<int>();
        for (int a = from; a + win <= y.Length; a += win)
        {
            double e = 0; for (int i = a; i < a + win; i++) e += y[i] * (double)y[i];
            lv.Add(10 * Math.Log10(e / win + 1e-30)); starts.Add(a);
        }
        double m = lv.Average(), sd = Math.Sqrt(lv.Sum(v => (v - m) * (v - m)) / lv.Count);
        double sumC = 0, sumO = 0; int nC = 0, nO = 0;
        for (int j = 1; j < lv.Count; j++)
        {
            int boundary = starts[j];
            bool change = boundary / per != (boundary - win) / per || boundary % per == 0;
            double d = Math.Abs(lv[j] - lv[j - 1]);
            if (change) { sumC += d; nC++; } else { sumO += d; nO++; }
        }
        return (sd, nC > 0 ? sumC / nC : 0, nO > 0 ? sumO / nO : 0);
    }

    /// <summary>One harmonic's level, Goertzel over 50 ms windows: its s.d. in dB.</summary>
    private static double HarmonicSpread(float[] y, double f, int from)
    {
        int win = 2400;                           // six whole periods: no leakage between harmonics
        var lv = new List<double>();
        double w = 2 * Math.PI * f / Fs, c = 2 * Math.Cos(w);
        for (int a = from; a + win <= y.Length; a += win)
        {
            double s1 = 0, s2 = 0;
            for (int i = a; i < a + win; i++) { double s0 = y[i] + c * s1 - s2; s2 = s1; s1 = s0; }
            lv.Add(10 * Math.Log10(s1 * s1 + s2 * s2 - c * s1 * s2 + 1e-30));
        }
        double m = lv.Average();
        return Math.Sqrt(lv.Sum(v => (v - m) * (v - m)) / lv.Count);
    }

    private const int SpecN = 1 << 17;

    /// <summary>A response's power spectrum from 50 ms on.</summary>
    private static double[] Power(float[] h)
    {
        int a = (int)(0.05 * Fs);
        var re = new float[SpecN]; var im = new float[SpecN];
        for (int i = a; i < h.Length && i - a < SpecN; i++) re[i - a] = h[i];
        new Fft(SpecN).Forward(re, im);
        var p = new double[SpecN / 2];
        for (int k = 0; k < p.Length; k++) p[k] = re[k] * (double)re[k] + im[k] * (double)im[k];
        return p;
    }

    /// <summary>The energy of a power spectrum in the third octave round <paramref name="fc"/>.</summary>
    private static double ThirdOctave(double[] p, double fc)
    {
        double hz = (double)Fs / SpecN, lo = fc / Math.Pow(2, 1.0 / 6), hi = fc * Math.Pow(2, 1.0 / 6), e = 0;
        for (int k = (int)(lo / hz); k <= (int)(hi / hz); k++) e += p[k];
        return e;
    }

    private static (double Total, double T20, double Edt) BandEnergy(float[] h, double f)
    {
        var y = Band(h, f);
        int a = (int)(0.05 * Fs);
        var e = new double[y.Length]; double acc = 0;
        for (int i = y.Length - 1; i >= a; i--) { acc += y[i] * (double)y[i]; e[i] = acc; }
        double total = acc;
        if (total <= 0) return (0, 0, 0);
        int i5 = -1, i10 = -1, i25 = -1;
        for (int i = a; i < y.Length; i++)
        {
            double db = 10 * Math.Log10(e[i] / total + 1e-30);
            if (i5 < 0 && db <= -5) i5 = i;
            if (i10 < 0 && db <= -10) i10 = i;
            if (i25 < 0 && db <= -25) { i25 = i; break; }
        }
        return (total, i5 >= 0 && i25 > i5 ? 3.0 * (i25 - i5) / Fs : 0, i10 > a ? 6.0 * (i10 - a) / Fs : 0);
    }

    private static (double Rms, double Max) Steps(double[] e)
    {
        double s = 0, m = 0; int n = 0;
        for (int i = 1; i < e.Length; i++)
        {
            if (e[i] <= 0 || e[i - 1] <= 0) continue;
            double d = 10 * Math.Log10(e[i] / e[i - 1]);
            s += d * d; m = Math.Max(m, Math.Abs(d)); n++;
        }
        return (n > 0 ? Math.Sqrt(s / n) : 0, m);
    }

    /// <summary>The 100-600 ms of a response: how far its spectrum stands over its own local median,
    /// how flat it is, and the strongest repeat in its fine structure.</summary>
    private static (double Over10, double P999, double Flat, double Repeat, double Lag) Ring(float[] h, double from = 0.1, double to = 0.6)
    {
        int a = (int)(from * Fs), b = Math.Min(h.Length, (int)(to * Fs));
        if (b - a < Fs / 20) return (0, 0, 0, 0, 0);
        int nfft = 32768;
        var re = new float[nfft]; var im = new float[nfft];
        // Flatten the decay first, so the spectrum is of the fine structure, not of its first 50 ms.
        var seg = new float[b - a];
        int sm = Fs / 100;
        for (int i = 0; i < seg.Length; i++)
        {
            double e = 0; int c = 0;
            for (int j = Math.Max(a, a + i - sm); j < Math.Min(b, a + i + sm); j += 4) { e += h[j] * (double)h[j]; c++; }
            seg[i] = (float)(h[a + i] / Math.Sqrt(e / Math.Max(1, c) + 1e-30));
        }
        for (int i = 0; i < seg.Length && i < nfft; i++) re[i] = seg[i] * (0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / seg.Length));
        new Fft(nfft).Forward(re, im);
        var p = new double[nfft / 2];
        for (int k = 0; k < p.Length; k++) p[k] = re[k] * (double)re[k] + im[k] * (double)im[k];
        double hz = (double)Fs / nfft;
        int k0 = (int)(100 / hz), k1 = (int)(10000 / hz);
        var ratios = new List<double>();
        double logSum = 0, linSum = 0; int cnt = 0;
        for (int k = k0; k < k1; k++)
        {
            int lo = (int)(k / Math.Pow(2, 1.0 / 6)), hi = (int)(k * Math.Pow(2, 1.0 / 6));
            var win = new double[hi - lo + 1];
            Array.Copy(p, lo, win, 0, win.Length);
            Array.Sort(win);
            double med = win[win.Length / 2];
            ratios.Add(p[k] / (med + 1e-30));
            logSum += Math.Log(p[k] + 1e-30); linSum += p[k]; cnt++;
        }
        ratios.Sort();
        double over = ratios.Count(r => r > 10) / (double)ratios.Count;
        double p999 = 10 * Math.Log10(ratios[(int)(ratios.Count * 0.999)]);
        double flat = Math.Exp(logSum / cnt) / (linSum / cnt);
        // Autocorrelation of the flattened segment, 0.5 to 30 ms.
        double e0 = 0; foreach (var v in seg) e0 += v * (double)v;
        double best = 0, lag = 0;
        for (int l = (int)(0.0005 * Fs); l < (int)(0.03 * Fs); l++)
        {
            double c = 0; for (int i = 0; i + l < seg.Length; i++) c += seg[i] * (double)seg[i + l];
            c /= e0;
            if (c > best) { best = c; lag = l * 1000.0 / Fs; }
        }
        return (over, p999, flat, best, lag);
    }

    private static float[] Band(float[] x, double f0)
    {
        var y = (float[])x.Clone();
        for (int pass = 0; pass < 2; pass++)
        {
            double w = 2 * Math.PI * f0 / Fs, q = Math.Sqrt(2), alpha = Math.Sin(w) / (2 * q);
            double b0 = alpha, b2 = -alpha, a0 = 1 + alpha, a1 = -2 * Math.Cos(w), a2 = 1 - alpha;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double xi = y[i], yi = (b0 * xi + b2 * x2 - a1 * y1 - a2 * y2) / a0;
                x2 = x1; x1 = xi; y2 = y1; y1 = yi; y[i] = (float)yi;
            }
        }
        return y;
    }

    /// <summary>The largest normalised cross-correlation within 1 ms, over <paramref name="from"/>..<paramref name="to"/> seconds.</summary>
    private static double Iacc(float[] l, float[] r, double from, double to)
    {
        int a = (int)(from * Fs), b = Math.Min(Math.Min(l.Length, r.Length), (int)(to * Fs)), lag = Fs / 1000;
        double el = 0, er = 0;
        for (int i = a; i < b; i++) { el += l[i] * (double)l[i]; er += r[i] * (double)r[i]; }
        double best = 0;
        for (int d = -lag; d <= lag; d++)
        {
            double c = 0;
            for (int i = Math.Max(a, a - d); i < b && i + d < b; i++) c += l[i] * (double)r[i + d];
            best = Math.Max(best, Math.Abs(c) / Math.Sqrt(el * er + 1e-30));
        }
        return best;
    }

    /// <summary>Both ears' energy in an octave from 50 ms: the total, T20, EDT, and the energy in
    /// 50-300 ms and from 300 ms on.</summary>
    private static (double Total, double T20, double Edt, double Early, double Late) EarDecay(float[][] y, double f)
    {
        var l = Band(y[0], f); var r = Band(y[1], f);
        int a = (int)(0.05 * Fs), m = (int)(0.3 * Fs);
        var e = new double[l.Length]; double acc = 0, early = 0, late = 0;
        for (int i = l.Length - 1; i >= a; i--)
        {
            double v = l[i] * (double)l[i] + r[i] * (double)r[i];
            acc += v; e[i] = acc;
            if (i < m) early += v; else late += v;
        }
        if (acc <= 0) return (0, 0, 0, 0, 0);
        int i5 = -1, i10 = -1, i25 = -1;
        for (int i = a; i < l.Length; i++)
        {
            double db = 10 * Math.Log10(e[i] / acc + 1e-30);
            if (i5 < 0 && db <= -5) i5 = i;
            if (i10 < 0 && db <= -10) i10 = i;
            if (i25 < 0 && db <= -25) { i25 = i; break; }
        }
        return (acc, i5 >= 0 && i25 > i5 ? 3.0 * (i25 - i5) / Fs : 0, i10 > a ? 6.0 * (i10 - a) / Fs : 0, early, late);
    }

    private static double Mean(double[] v) => v.Length > 0 ? v.Average() : 0;
    private static double Sd(double[] v) { if (v.Length < 2) return 0; double m = v.Average(); return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Length - 1)); }

    private static string? Arg(string[] args, string key)
    {
        foreach (var a in args) if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[(key.Length + 1)..];
        return null;
    }
}
