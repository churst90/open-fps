using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// The patio sliding door against the recording Cody named as THE patio door (goblinjack's slides), both
/// measured the same way. The recording is a yardstick only: nothing of it is played by the game.
///
///   --patio-vs-ref [ref=WAV] [wav=FILE,FILE] [only=open-v1] [out=DIR]
///
/// Without wav= it renders the patio door in each character, opening and shutting, and measures those.
/// Each file is cut the same way: its stops are the loud blows that follow a slide, its slides are what
/// sounds between, away from any blow. Then, side by side:
///   the slides' octave bands (63 Hz to 16 kHz, dB re the loudest band) and centroid;
///   the slides' envelope modulation spectrum, 3-40 Hz: the strongest beat and how far it stands above
///   the rest (a roller's turn is a beat at its rotation rate);
///   each stop's ring (the strongest line from 40 to 400 Hz in the quarter second after the blow), how
///   long its low end (below 300 Hz) takes to fall 30 dB, and the hits in its first 120 ms, counted on
///   the part above 1 kHz where each blow is a burst of its own.
/// With out= it writes the listening set: each character open and shut, the recording, and a file that
/// plays a slide of the recording, then one of ours, at matched slide loudness.
/// </summary>
public static class PatioRefSpike
{
    private const string RefName = "ref-goblinjack-slides.wav";
    private static readonly double[] Octaves = { 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    private static readonly string[] Characters = { "new", "standard", "worn", "old" };

    public sealed class Stop
    {
        public double At, PeakDb, RingHz, LowDecay30, Decay30;
        public List<double> Hits = new();
    }

    public sealed class Measures
    {
        public string Name = "";
        public double[] Bands = new double[Octaves.Length];
        public double Centroid, SlideSeconds, SlideDbfs, ModHz, ModProminence, Mod2Hz, ModDepth;
        public List<(double From, double To)> Slides = new();
        public List<Stop> Stops = new();
        public double PeakDbfs, EdgeDb, BandMax, StartDb, StepDb;
    }

    private static bool Detail;

    public static int Run(string[] args)
    {
        Detail = args.Contains("detail");
        string? refPath = Arg(args, "ref");
        if (refPath == null)
        {
            refPath = LabPaths.InRepo("inbox", "door-sounds-2026-10-02", "patio-slide", RefName);
            if (!File.Exists(refPath)) refPath = Path.Combine(LabPaths.Checkout, "inbox", "door-sounds-2026-10-02", "patio-slide", RefName);
        }
        if (!File.Exists(refPath)) { Console.Error.WriteLine($"no reference at {refPath}"); return 1; }
        var (refPcm, refRate) = ReloadSpecSpike.ReadWav(refPath);
        var refM = Measure(refPcm, refRate, "reference");

        var ours = new List<(string Name, float[] Pcm, int Rate)>();
        string? wavs = Arg(args, "wav");
        string? only = Arg(args, "only");
        if (wavs != null)
            foreach (var f in wavs.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var (p, r) = ReloadSpecSpike.ReadWav(f);
                ours.Add((Path.GetFileNameWithoutExtension(f), p, r));
            }
        else
            for (int v = 0; v < SlidingDoor.Variants; v++)
                foreach (bool closing in new[] { false, true })
                {
                    string name = $"{(closing ? "close" : "open")}-v{v}";
                    if (only != null && !name.Contains(only)) continue;
                    var door = new SlidingDoor.Door { Kind = SlidingDoor.Kind.Patio, Variant = v, Seed = 1 + v, Width = 0.9f, Height = 2.03f };
                    var rep = new SlidingDoor.Report();
                    SlidingDoor.StemFolder = Arg(args, "stems");
                    if (SlidingDoor.StemFolder != null) Directory.CreateDirectory(SlidingDoor.StemFolder);
                    var pcm = closing ? SlidingDoor.RenderClose(door, 48000, 1.4, rep) : SlidingDoor.RenderOpen(door, 48000, 1.4, rep);
                    if (args.Contains("events")) Console.WriteLine(name + "\n" + rep);
                    ours.Add(($"{Characters[v]} {(closing ? "close" : "open")}", pcm, 48000));
                    if (SlidingDoor.StemFolder != null) Stems(SlidingDoor.StemFolder, Measure(pcm, 48000, name));
                }
        var measured = new List<Measures> { refM };
        foreach (var (name, pcm, rate) in ours)
        {
            var m = Measure(pcm, rate, name);
            if (rate == 48000)
                Console.WriteLine($"{name}: LAFmax {LafMax(pcm, SlidingDoor.PascalsAtFullScale):F1} dB at 1 m, slide {m.SlideDbfs + 20 * Math.Log10(SlidingDoor.PascalsAtFullScale / 2e-5):F1} dB SPL");
            measured.Add(m);
        }
        Print(measured);

        string? outDir = Arg(args, "out");
        if (outDir != null && wavs == null) WriteSet(outDir, refPcm, refRate, refM, ours, measured);
        return 0;
    }

    // ── Measuring ───────────────────────────────────────────────────────────────────────────────────

    public static Measures Measure(float[] pcm, int rate, string name)
    {
        var m = new Measures { Name = name };
        var x = pcm.Select(v => (double)v).ToArray();
        var hp = HighPass(x, rate, 30);
        int f10 = rate / 100, f2 = rate / 500;
        var e10 = FrameDb(hp, f10);
        var e2 = FrameDb(hp, f2);
        double max10 = e10.Max();
        var sorted = e10.OrderBy(v => v).ToArray();
        double floor = Math.Max(sorted[sorted.Length / 10], max10 - 70);
        double peak = x.Max(Math.Abs);
        m.PeakDbfs = 20 * Math.Log10(Math.Max(peak, 1e-12));
        double edge = 0;
        int ten = rate / 100;
        for (int i = 0; i < ten && i < x.Length; i++) edge = Math.Max(edge, Math.Abs(x[x.Length - 1 - i]));
        m.EdgeDb = 20 * Math.Log10(Math.Max(edge, 1e-12) / Math.Max(peak, 1e-12));
        // A click is a jump between samples: the first sample against the peak, and the biggest step anywhere.
        double step = 0;
        for (int i = 1; i < x.Length; i++) step = Math.Max(step, Math.Abs(x[i] - x[i - 1]));
        m.StartDb = 20 * Math.Log10(Math.Max(Math.Abs(x[0]), 1e-12) / Math.Max(peak, 1e-12));
        m.StepDb = 20 * Math.Log10(Math.Max(step, 1e-12) / Math.Max(peak, 1e-12));

        // Blows: a 2 ms frame 15 dB over what the 300 ms before it held, within 30 dB of the loudest. A blow
        // goes on for as long as frames keep coming 15 dB up within 60 ms of each other.
        double max2 = e2.Max();
        var onsets = new List<(int Frame, double Peak)>();
        int lastHit = -1000;
        for (int i = 0; i < e2.Length; i++)
        {
            double t = (double)i * f2 / rate;
            int a = Math.Max(0, (int)((t - 0.3) * 100)), b = Math.Max(a + 1, (int)((t - 0.02) * 100));
            double before = Median(e10, a, Math.Min(b, e10.Length));
            if (e2[i] > Math.Max(before, floor) + 15 && e2[i] > max2 - 30)
            {
                bool same = (i - lastHit) * f2 < 0.06 * rate;
                lastHit = i;
                if (same) continue;
                double p = e2[i];
                for (int j = i; j < Math.Min(e2.Length, i + 30); j++) p = Math.Max(p, e2[j]);
                onsets.Add((i, p));
            }
        }
        // Slides: 10 ms frames 10 dB over the floor. A stop is a loud blow (within 20 dB of the loudest) with
        // the slide sounding in the 300 ms before it; it and its ring, and the latch thrown after it, are not
        // slide, for 0.8 s. Any other loud blow (a latch before the leaf moves) is not slide for 80 ms. A
        // grain's tick is part of the slide.
        var active = new bool[e10.Length];
        for (int i = 0; i < e10.Length; i++) active[i] = e10[i] > floor + 10;
        var stops = new List<(double T, double P)>();
        foreach (var (frame, p) in onsets)
        {
            double t = (double)frame * f2 / rate;
            if (p < max2 - 20) continue;
            int a = Math.Max(0, (int)((t - 0.35) * 100)), b = Math.Max(a, (int)((t - 0.05) * 100));
            int on = 0; for (int i = a; i < b; i++) if (active[i]) on++;
            bool stop = t >= 0.35 && on >= 0.6 * (b - a);
            if (stop) stops.Add((t, p));
            double after = stop ? 0.8 : 0.08;
            for (int i = Math.Max(0, (int)((t - 0.03) * 100)); i < Math.Min(e10.Length, (int)((t + after) * 100)); i++) active[i] = false;
        }
        for (int i = 0; i < e10.Length; )
        {
            if (!active[i]) { i++; continue; }
            int j = i; while (j < e10.Length && active[j]) j++;
            if (j - i >= 25) m.Slides.Add((i / 100.0, j / 100.0));
            i = j;
        }
        if (Detail)
            foreach (var (frame, p) in onsets)
                Console.WriteLine($"  {name}: blow at {(double)frame * f2 / rate:F3} s, {p - max2:F1} dB re the loudest{(stops.Any(s => Math.Abs(s.T - (double)frame * f2 / rate) < 1e-6) ? ", a stop" : "")}");
        foreach (var (t, p) in stops) m.Stops.Add(MeasureStop(x, rate, t, p));

        // Slide bands.
        double energy = 0, samples = 0;
        foreach (var (from, to) in m.Slides)
        {
            int a = (int)(from * rate), b = (int)(to * rate);
            for (int i = a; i < b; i++) energy += x[i] * x[i];
            samples += b - a;
        }
        m.SlideSeconds = samples / rate;
        m.SlideDbfs = 10 * Math.Log10(Math.Max(energy / Math.Max(samples, 1), 1e-24));
        m.Bands = BandsOver(x, rate, m.Slides, out m.Centroid);
        if (Detail) Thirds(x, rate, m.Slides, name);
        double bmax = m.Bands.Max();
        m.BandMax = bmax;
        for (int o = 0; o < Octaves.Length; o++) m.Bands[o] -= bmax;

        // The slides' beat: the envelope of 150 Hz-4 kHz in 5 ms frames, over its own 255 ms average.
        var band = LowPass(HighPass(x, rate, 150), rate, 4000);
        int f5 = rate / 200;
        var mod = new double[1024];
        var dev = new List<double>();
        foreach (var (from, to) in m.Slides)
        {
            int a = (int)(from * rate) / f5, b = (int)(to * rate) / f5;
            int n = b - a;
            if (n < 40) continue;
            var env = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = 0;
                for (int k = (a + i) * f5; k < (a + i + 1) * f5; k++) s += band[k] * band[k];
                env[i] = Math.Sqrt(s / f5);
            }
            var norm = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = 0; int c = 0;
                for (int k = Math.Max(0, i - 25); k <= Math.Min(n - 1, i + 25); k++) { s += env[k]; c++; }
                norm[i] = env[i] / Math.Max(s / c, 1e-12) - 1;
                dev.Add(norm[i]);
            }
            int len = 2048;
            var re = new double[len]; var im = new double[len];
            for (int i = 0; i < Math.Min(n, len); i++) re[i] = norm[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
            Fft(re, im);
            for (int k = 0; k < 1024; k++) mod[k] += (re[k] * re[k] + im[k] * im[k]) * n;
        }
        double mHz = 200.0 / 2048;
        int lo = (int)(3 / mHz), hi = (int)(40 / mHz);
        int best = lo; for (int k = lo; k <= hi; k++) if (mod[k] > mod[best]) best = k;
        int second = -1;
        for (int k = lo; k <= hi; k++)
            if (Math.Abs(k - best) * mHz > 2.5 && (k == lo || mod[k] >= mod[k - 1]) && (k == hi || mod[k] >= mod[k + 1]) && (second < 0 || mod[k] > mod[second])) second = k;
        double med = Median(mod, lo, hi + 1);
        m.ModHz = best * mHz;
        m.ModProminence = 10 * Math.Log10(Math.Max(mod[best], 1e-30) / Math.Max(med, 1e-30));
        m.Mod2Hz = second * mHz;
        m.ModDepth = dev.Count > 0 ? Math.Sqrt(dev.Average(v => v * v)) : 0;
        if (Detail)
        {
            var peaks = new List<(double Hz, double Db)>();
            for (int k = lo + 1; k < hi; k++) if (mod[k] > mod[k - 1] && mod[k] >= mod[k + 1]) peaks.Add((k * mHz, 10 * Math.Log10(mod[k] / Math.Max(med, 1e-30))));
            Console.WriteLine($"  {name} beats: " + string.Join(", ", peaks.OrderByDescending(p => p.Db).Take(6).Select(p => $"{p.Hz:F1} Hz +{p.Db:F1}")));
        }
        return m;
    }

    private static Stop MeasureStop(double[] x, int rate, double t0, double peakDb)
    {
        var s = new Stop { At = t0, PeakDb = peakDb };
        int a = (int)((t0 + 0.01) * rate), b = Math.Min(x.Length, (int)((t0 + 0.26) * rate));
        int len = 1; while (len < rate) len <<= 1;
        var re = new double[len]; var im = new double[len];
        for (int i = a; i < b; i++) re[i - a] = x[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * (i - a) / (b - a - 1)));
        Fft(re, im);
        double bin = (double)rate / len, bestP = 0;
        for (int k = (int)(40 / bin); k <= (int)(400 / bin); k++)
        {
            double p = re[k] * re[k] + im[k] * im[k];
            if (p > bestP) { bestP = p; s.RingHz = k * bin; }
        }
        if (Detail)
        {
            var lines = new List<(double Hz, double Db)>();
            for (int k = (int)(40 / bin) + 1; k < (int)(400 / bin); k++)
            {
                double p = re[k] * re[k] + im[k] * im[k];
                if (p > re[k - 1] * re[k - 1] + im[k - 1] * im[k - 1] && p >= re[k + 1] * re[k + 1] + im[k + 1] * im[k + 1])
                    lines.Add((k * bin, 10 * Math.Log10(p / bestP)));
            }
            Console.WriteLine($"  stop at {t0:F3}: lines " + string.Join(", ", lines.OrderByDescending(l => l.Db).Take(8).Select(l => $"{l.Hz:F0} Hz {l.Db:F1}")));
            // Its low end against the whole blow, in the first 60 ms.
            double low = 0, all = 0;
            var lp = LowPass(LowPass(x, rate, 300), rate, 300);
            for (int i = (int)(t0 * rate); i < Math.Min(x.Length, (int)((t0 + 0.06) * rate)); i++) { low += lp[i] * lp[i]; all += x[i] * x[i]; }
            Console.WriteLine($"  stop at {t0:F3}: below 300 Hz is {10 * Math.Log10(low / Math.Max(all, 1e-30)):F1} dB of the first 60 ms");
        }
        s.LowDecay30 = Decay(LowPass(LowPass(x, rate, 300), rate, 300), rate, t0, 30);
        s.Decay30 = Decay(x, rate, t0, 30);
        // Hits: the part above 1 kHz in 1 ms frames; a hit is a peak within 20 dB of the loudest, with a dip
        // of 6 dB between it and the hit before.
        var h = HighPass(HighPass(x, rate, 1000), rate, 1000);
        int f1 = rate / 1000;
        int start = Math.Max(0, (int)((t0 - 0.005) * rate)) / f1, end = Math.Min(x.Length / f1 - 1, (int)((t0 + 0.12) * rate) / f1);
        var e = new double[end - start];
        for (int i = 0; i < e.Length; i++)
        {
            double acc = 0;
            for (int k = (start + i) * f1; k < (start + i + 1) * f1; k++) acc += h[k] * h[k];
            e[i] = 10 * Math.Log10(acc / f1 + 1e-24);
        }
        if (e.Length < 3) return s;
        double top = e.Max();
        double lastPeak = double.NaN, dip = double.PositiveInfinity;
        for (int i = 1; i < e.Length - 1; i++)
        {
            dip = Math.Min(dip, e[i]);
            if (e[i] < top - 20 || e[i] < e[i - 1] || e[i] < e[i + 1]) continue;
            if (double.IsNaN(lastPeak) || Math.Min(lastPeak, e[i]) - dip >= 6)
            {
                s.Hits.Add((start + i) * f1 / (double)rate - t0);
                lastPeak = e[i]; dip = e[i];
            }
            else if (e[i] > lastPeak) lastPeak = e[i];
        }
        return s;
    }

    /// <summary>Seconds from the loudest 5 ms frame in the first 60 ms after a blow until a 20 ms average
    /// falls <paramref name="db"/> below it; NaN when the file or its floor ends first.</summary>
    private static double Decay(double[] x, int rate, double t0, double db)
    {
        int f5 = rate / 200;
        int a = Math.Max(0, (int)((t0 - 0.005) * rate)) / f5;
        var e = FrameDb(x, f5);
        int pk = a; for (int i = a; i < Math.Min(e.Length, a + 12); i++) if (e[i] > e[pk]) pk = i;
        for (int i = pk + 1; i + 3 < e.Length && i < pk + 400; i++)
        {
            double avg = 10 * Math.Log10((Math.Pow(10, e[i] / 10) + Math.Pow(10, e[i + 1] / 10) + Math.Pow(10, e[i + 2] / 10) + Math.Pow(10, e[i + 3] / 10)) / 4);
            if (avg < e[pk] - db) return (i - pk) * 0.005;
        }
        return double.NaN;
    }

    private static void Print(List<Measures> ms)
    {
        Console.WriteLine();
        Console.WriteLine("slides: octave bands dB re the loudest, centroid, beat");
        Console.Write($"{"",-16}");
        foreach (double o in Octaves) Console.Write($"{(o >= 1000 ? (o / 1000) + "k" : o.ToString()),6}");
        Console.WriteLine($"{"cent",7}{"slide s",8}{"beat Hz",8}{"+dB",5}{"2nd",6}{"depth",6}");
        foreach (var m in ms)
        {
            Console.Write($"{m.Name,-16}");
            foreach (double b in m.Bands) Console.Write($"{b,6:F1}");
            Console.WriteLine($"{m.Centroid,7:F0}{m.SlideSeconds,8:F2}{m.ModHz,8:F1}{m.ModProminence,5:F1}{m.Mod2Hz,6:F1}{m.ModDepth,6:F2}");
        }
        var r = ms[0];
        Console.WriteLine();
        Console.WriteLine("slides: bands against the reference, dB (125 Hz to 8 kHz is the target, within 3)");
        foreach (var m in ms.Skip(1))
        {
            Console.Write($"{m.Name,-16}");
            double worst = 0;
            for (int o = 0; o < Octaves.Length; o++)
            {
                double d = m.Bands[o] - r.Bands[o];
                Console.Write($"{d,6:F1}");
                if (o >= 1 && o <= 7) worst = Math.Max(worst, Math.Abs(d));
            }
            Console.WriteLine($"   worst {worst:F1}");
        }
        Console.WriteLine();
        Console.WriteLine("stops: at s, 2 ms peak over the slides dB, ring Hz, low end -30 dB s, all -30 dB s, hits ms");
        foreach (var m in ms)
            foreach (var s in m.Stops)
                Console.WriteLine($"{m.Name,-16}{s.At,6:F2}{s.PeakDb - m.SlideDbfs,6:F1}{s.RingHz,7:F0}{s.LowDecay30,7:F3}{s.Decay30,7:F3}   {s.Hits.Count}: {string.Join(" ", s.Hits.Select(h => (h * 1000).ToString("F0")))}");
        Console.WriteLine();
        foreach (var m in ms)
            Console.WriteLine($"{m.Name,-16}slides {string.Join(" ", m.Slides.Select(s => $"{s.From:F2}-{s.To:F2}"))}  peak {m.PeakDbfs:F1} dBFS; starts {m.StartDb:F0}, ends {m.EdgeDb:F0}, biggest step {m.StepDb:F1} dB re peak");
    }

    /// <summary>Each part's own octave bands over the mix's slides, dB re the mix's loudest band: which part
    /// puts the energy where.</summary>
    private static void Stems(string dir, Measures mix)
    {
        foreach (var f in Directory.GetFiles(dir, "sd-*.raw").OrderBy(f => f))
        {
            var bytes = File.ReadAllBytes(f);
            var x = new double[bytes.Length / 4];
            for (int i = 0; i < x.Length; i++) x[i] = BitConverter.ToSingle(bytes, 4 * i);
            var b = BandsOver(x, 48000 * 4, mix.Slides, out double c);
            Console.Write($"  {mix.Name} {Path.GetFileNameWithoutExtension(f),-10}");
            foreach (double v in b) Console.Write($"{v - mix.BandMax,6:F1}");
            Console.WriteLine($"{c,7:F0}");
        }
    }

    // ── The listening set ───────────────────────────────────────────────────────────────────────────

    private static void WriteSet(string dir, float[] refPcm, int refRate, Measures refM,
                                 List<(string Name, float[] Pcm, int Rate)> ours, List<Measures> measured)
    {
        Directory.CreateDirectory(dir);
        var ref48 = Resample(refPcm, refRate, 48000);
        // Every file is levelled so its slides play at the same loudness, unless that would put a peak
        // above -1 dBFS.
        const double slideTarget = -24;
        int n = 1;
        string[] order = { "standard", "new", "worn", "old" };
        foreach (var (name, pcm, _) in ours.OrderBy(o => Array.IndexOf(order, o.Name.Split(' ')[0])))
        {
            var m = measured.First(x => x.Name == name);
            double gain = Math.Min(Math.Pow(10, (slideTarget - m.SlideDbfs) / 20), Math.Pow(10, (-1 - m.PeakDbfs) / 20));
            Write(Path.Combine(dir, $"{n++} {name}.wav"), pcm, gain);
        }
        var refM48 = Measure(ref48, 48000, "reference");
        double refGain = Math.Min(Math.Pow(10, (slideTarget - refM48.SlideDbfs) / 20), Math.Pow(10, (-1 - refM48.PeakDbfs) / 20));
        Write(Path.Combine(dir, "9 reference.wav"), ref48, refGain);

        // Reference, ours, reference, ours: each of the recording's slides with what follows it, then one of
        // the standard door's, open and shut in turn, both at the same slide loudness.
        var std = ours.Where(o => o.Name.StartsWith("standard")).ToList();
        if (std.Count == 0) return;
        var stdM = std.Select(o => measured.First(x => x.Name == o.Name)).ToList();
        double ourSlide = 10 * Math.Log10(stdM.Average(x => Math.Pow(10, x.SlideDbfs / 10)));
        double ourGain = Math.Pow(10, (refM48.SlideDbfs - ourSlide) / 20);
        var seq = new List<float>();
        var gap = new float[(int)(0.7 * 48000)];
        // The recording's slides as a person made them: stretches less than half a second apart are one slide
        // (a click in it splits it), and a fragment shorter than half a second is not one.
        var slides = new List<(double From, double To)>();
        foreach (var sl in refM48.Slides)
            if (slides.Count > 0 && sl.From - slides[^1].To < 0.5) slides[^1] = (slides[^1].From, sl.To);
            else slides.Add(sl);
        slides.RemoveAll(sl => sl.To - sl.From < 0.5);
        for (int k = 0; k < slides.Count; k++)
        {
            var (from, to) = slides[k];
            var stop = refM48.Stops.FirstOrDefault(s => s.At > to - 0.05 && s.At < to + 0.1);
            double end = stop != null ? stop.At + 0.6 : to + 0.3;
            if (k + 1 < slides.Count) end = Math.Min(end, slides[k + 1].From - 0.2);
            int a = Math.Max(0, (int)((from - 0.15) * 48000)), b = Math.Min(ref48.Length, (int)(end * 48000));
            seq.AddRange(Fade(ref48[a..b]));
            seq.AddRange(gap);
            var o = std[k % std.Count];
            seq.AddRange(o.Pcm.Select(v => (float)(v * ourGain)));
            seq.AddRange(gap);
        }
        float pk = seq.Max(Math.Abs);
        Write(Path.Combine(dir, "0 reference then standard.wav"), seq.ToArray(), Math.Pow(10, -1.0 / 20) / Math.Max(pk, 1e-9));
        Console.WriteLine($"wrote the listening set to {dir}");
    }

    private static float[] Fade(float[] x)
    {
        int f = Math.Min(x.Length / 2, 480);
        for (int i = 0; i < f; i++) { float g = i / (float)f; x[i] *= g; x[^(i + 1)] *= g; }
        return x;
    }

    private static void Write(string path, float[] pcm, double gain)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(48000); w.Write(96000); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (float v in pcm) w.Write((short)Math.Clamp(v * gain * 32767, -32768, 32767));
    }

    // ── Signal tools ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Mean square in each octave band over the given stretches, dB (so files at different rates
    /// compare), and the power centroid below 20 kHz.</summary>
    public static double[] BandsOver(double[] x, int rate, List<(double From, double To)> parts, out double centroid)
    {
        const int n = 8192;
        var psd = new double[n / 2 + 1];
        int frames = 0;
        foreach (var (from, to) in parts) frames += Welch(x, (int)(from * rate), Math.Min(x.Length, (int)(to * rate)), psd);
        double binHz = (double)rate / n, num = 0, den = 0;
        for (int k = 1; k < psd.Length; k++) { double f = k * binHz; if (f < 20000) { num += f * psd[k]; den += psd[k]; } }
        centroid = num / Math.Max(den, 1e-30);
        // A Hann window's power is 3/8 of the frame's: each frame's bins sum to N^2 3/8 times its mean square.
        double scale = 2.0 / (n * (double)n * 0.375 * Math.Max(frames, 1));
        var bands = new double[Octaves.Length];
        for (int o = 0; o < Octaves.Length; o++)
        {
            double s = 0;
            for (int k = 1; k < psd.Length; k++) { double f = k * binHz; if (f >= Octaves[o] / Math.Sqrt(2) && f < Octaves[o] * Math.Sqrt(2)) s += psd[k]; }
            bands[o] = 10 * Math.Log10(Math.Max(s * scale, 1e-30));
        }
        return bands;
    }

    /// <summary>The slides in third octaves, 50 Hz to 16 kHz, dB re the loudest.</summary>
    private static void Thirds(double[] x, int rate, List<(double From, double To)> parts, string name)
    {
        const int n = 8192;
        var psd = new double[n / 2 + 1];
        foreach (var (from, to) in parts) Welch(x, (int)(from * rate), Math.Min(x.Length, (int)(to * rate)), psd);
        double binHz = (double)rate / n;
        var bands = new List<(double Hz, double Db)>();
        for (int b = -13; b <= 12; b++)
        {
            double fc = 1000 * Math.Pow(2, b / 3.0), sum = 0;
            for (int k = 1; k < psd.Length; k++) { double f = k * binHz; if (f >= fc / Math.Pow(2, 1 / 6.0) && f < fc * Math.Pow(2, 1 / 6.0)) sum += psd[k]; }
            bands.Add((fc, 10 * Math.Log10(Math.Max(sum, 1e-30))));
        }
        double top = bands.Max(b => b.Db);
        Console.WriteLine($"  {name} thirds: " + string.Join(" ", bands.Select(b => $"{(b.Hz >= 1000 ? (b.Hz / 1000).ToString("0.#") + "k" : b.Hz.ToString("F0"))}:{b.Db - top:F0}")));
    }

    private static double[] FrameDb(double[] x, int frame)
    {
        var e = new double[x.Length / frame];
        for (int i = 0; i < e.Length; i++)
        {
            double s = 0;
            for (int k = i * frame; k < (i + 1) * frame; k++) s += x[k] * x[k];
            e[i] = 10 * Math.Log10(s / frame + 1e-24);
        }
        return e;
    }

    private static double Median(double[] v, int a, int b)
    {
        if (b <= a) return double.NegativeInfinity;
        var s = v[a..b]; Array.Sort(s); return s[s.Length / 2];
    }

    private static int Welch(double[] x, int a, int b, double[] psd)
    {
        const int n = 8192;
        var re = new double[n]; var im = new double[n];
        int frames = 0;
        for (int s = a; s + n <= b || (s == a && b - a > 1024); s += n / 2)
        {
            Array.Clear(re); Array.Clear(im);
            int len = Math.Min(n, b - s);
            for (int i = 0; i < len; i++) re[i] = x[s + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (len - 1)));
            Fft(re, im);
            for (int k = 0; k < psd.Length; k++) psd[k] += re[k] * re[k] + im[k] * im[k];
            frames++;
            if (s + n > b) break;
        }
        return frames;
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int p = i + k, q = p + len / 2;
                    double tr = re[q] * cr - im[q] * ci, ti = re[q] * ci + im[q] * cr;
                    re[q] = re[p] - tr; im[q] = im[p] - ti; re[p] += tr; im[p] += ti;
                    double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }

    private static double[] HighPass(double[] x, int rate, double hz) => Biquad(x, rate, hz, high: true);
    private static double[] LowPass(double[] x, int rate, double hz) => Biquad(x, rate, hz, high: false);

    private static double[] Biquad(double[] x, int rate, double hz, bool high)
    {
        double w = 2 * Math.PI * hz / rate, c = Math.Cos(w), al = Math.Sin(w) / Math.Sqrt(2), a0 = 1 + al;
        double b0 = (high ? (1 + c) : (1 - c)) / 2 / a0, b1 = (high ? -(1 + c) : (1 - c)) / a0, b2 = b0;
        double a1 = -2 * c / a0, a2 = (1 - al) / a0;
        var y = new double[x.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v; y[i] = v;
        }
        return y;
    }

    /// <summary>Band-limited resampling: a Blackman-windowed sinc, 32 taps a side, cut below both rates'
    /// Nyquist.</summary>
    private static float[] Resample(float[] x, int from, int to)
    {
        if (from == to) return (float[])x.Clone();
        int n = (int)((long)x.Length * to / from);
        var y = new float[n];
        double fc = 0.45 * Math.Min(from, to) / from;
        for (int j = 0; j < n; j++)
        {
            double t = (double)j * from / to;
            int c = (int)Math.Floor(t);
            double acc = 0;
            for (int k = c - 31; k <= c + 32; k++)
            {
                if (k < 0 || k >= x.Length) continue;
                double d = t - k;
                double sinc = Math.Abs(d) < 1e-9 ? 2 * fc : Math.Sin(2 * Math.PI * fc * d) / (Math.PI * d);
                double u = (d + 32) / 64;
                double win = 0.42 - 0.5 * Math.Cos(2 * Math.PI * u) + 0.08 * Math.Cos(4 * Math.PI * u);
                acc += x[k] * sinc * win;
            }
            y[j] = (float)acc;
        }
        return y;
    }

    /// <summary>LAFmax, dB re 20 uPa, at 48 kHz (A-weighting by its bilinear filter, a 125 ms meter).</summary>
    private static double LafMax(float[] x, double fullScale)
    {
        double[] b = { 0.234301792299513, -0.468603584599026, -0.234301792299513, 0.937207168598053, -0.234301792299513, -0.468603584599026, 0.234301792299513 };
        double[] a = { 1.0, -4.113043408775871, 6.553121752655047, -4.990849294163381, 1.785737302937573, -0.246190595319487, 0.011224250033231 };
        var xs = new double[7]; var ys = new double[7];
        double k = Math.Exp(-1 / (0.125 * 48000)), e = 0, max = 0;
        foreach (float s in x)
        {
            Array.Copy(xs, 0, xs, 1, 6); xs[0] = s * fullScale;
            double y = 0;
            for (int i = 0; i < 7; i++) y += b[i] * xs[i];
            for (int i = 1; i < 7; i++) y -= a[i] * ys[i - 1];
            Array.Copy(ys, 0, ys, 1, 6); ys[0] = y;
            e = k * e + (1 - k) * y * y;
            max = Math.Max(max, e);
        }
        return 10 * Math.Log10(max / 4e-10);
    }

    private static string? Arg(string[] args, string name)
        => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
}
