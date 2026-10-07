using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// The push-bar door against recordings of real ones, both measured the same way. The recordings are a
/// yardstick only: nothing of them is played by the game.
///
///   --pushbar-vs-ref [refs=DIR] [before=DIR] [out=DIR] [only=v1]
///
/// The recordings (inbox/door-sounds-2026-10-02/pushbar and inbox/door-types-2026-10-02/real):
///   kyles (an institutional door, the microphone near the latch, in a corridor): the bar pushed (0.07 s),
///     let go (0.39 s), and after an edit the door slamming on its closer (2.06 s);
///   berumen (a fire exit door, a room with a 0.7 s tail): five slams on the closer (5.0, 11.8, 19.9, 28.7,
///     36.9 s), each the latch bolt riding the strike, then the blow.
/// Each event is cut into hits: the peaks of the 1 ms envelope above 1 kHz that rise 6 dB over the 4 ms
/// before them and stand within 30 dB of the loudest; hits under 80 ms apart are one cluster. For each
/// cluster: its loudest 5 ms (dB re the event's loudest), the hits (ms after the first, dB re the loudest),
/// and the loudest hit's first 30 ms by band (dB re its whole): under 300 Hz, 300 Hz-1 kHz, 1-4 kHz,
/// 4-16 kHz. Ours are rendered in each character, opening (the push, then the release) and shutting.
/// With out= it writes the listening set, every file levelled by its own LAFmax.
/// </summary>
public static class PushBarRefSpike
{
    private static readonly string[] Characters = { "new", "standard", "worn", "old" };
    private static readonly double[] BandEdges = { 30, 300, 1000, 4000, 16000 };

    public sealed class Cluster
    {
        public double Start, Level5;
        public List<(double Ms, double Db)> Hits = new();
        public double[] Bands = new double[4];
        public double SpanMs => Hits.Count == 0 ? 0 : Hits[^1].Ms;
    }

    /// <summary>The kyles events and the berumen slams: (name, file, from, to) in seconds.</summary>
    private static readonly (string Name, string File, double From, double To)[] RefEvents =
    {
        ("kyles push", "kyles", 0.0, 0.33),
        ("kyles release", "kyles", 0.33, 1.2),
        ("kyles slam", "kyles", 1.95, 2.89),
        ("berumen slam 1", "berumen", 4.86, 5.48),
        ("berumen slam 2", "berumen", 11.66, 12.25),
        ("berumen slam 3", "berumen", 19.80, 20.39),
        ("berumen slam 4", "berumen", 28.55, 29.14),
        ("berumen slam 5", "berumen", 36.78, 37.37),
    };

    public static int Run(string[] args)
    {
        string inbox = LabPaths.InRepo("inbox");
        if (!Directory.Exists(Path.Combine(inbox, "door-sounds-2026-10-02"))) inbox = Path.Combine(LabPaths.Checkout, "inbox");
        string kylesPath = Path.Combine(inbox, "door-sounds-2026-10-02", "pushbar", "ref-kyles-institutional-bar-and-slam.wav");
        string berumenPath = Path.Combine(inbox, "door-types-2026-10-02", "real", "2-firedoor-berumen-fire-exit-latch.wav");
        if (!File.Exists(kylesPath) || !File.Exists(berumenPath)) { Console.Error.WriteLine($"recordings not found under {inbox}"); return 1; }
        var files = new Dictionary<string, (float[] Pcm, int Rate)>
        {
            ["kyles"] = ReloadSpecSpike.ReadWav(kylesPath),
            ["berumen"] = ReloadSpecSpike.ReadWav(berumenPath),
        };

        Console.WriteLine("The recordings: each cluster's loudest 5 ms re the event's loudest; hits ms:dB; the loudest hit's first 30 ms by band");
        foreach (var (name, file, from, to) in RefEvents)
        {
            var (pcm, rate) = files[file];
            var seg = pcm[(int)(from * rate)..Math.Min(pcm.Length, (int)(to * rate))];
            Print(name, Clusters(seg, rate), from);
        }

        string? only = Arg(args, "only");
        var ours = new List<(string Name, float[] Pcm, PushBarDoor.Report Report)>();
        Console.WriteLine();
        Console.WriteLine("Ours (dry, 1 m)");
        for (int v = 0; v < PushBarDoor.Variants; v++)
            foreach (bool closing in new[] { false, true })
            {
                string name = $"{Characters[v]} {(closing ? "close" : "open")}";
                if (only != null && !name.Contains(only, StringComparison.Ordinal) && !$"v{v}".Equals(only, StringComparison.Ordinal)) continue;
                var door = new PushBarDoor.Door { Variant = v, Seed = 1 + v };
                var rep = new PushBarDoor.Report();
                var pcm = closing ? PushBarDoor.RenderClose(door, 48000, rep) : PushBarDoor.RenderOpen(door, 48000, 1.4, rep);
                ours.Add((name, pcm, rep));
                Print(name, Clusters(pcm, 48000), 0);
                Console.WriteLine($"    LAFmax {LafDbfs(pcm) + 20 * Math.Log10(PushBarDoor.PascalsAtFullScale / 2e-5):F1} dB at 1 m");
            }

        string? before = Arg(args, "before");
        if (before != null && Directory.Exists(before))
        {
            Console.WriteLine();
            Console.WriteLine($"Before (the approved round 7, {before})");
            foreach (var f in Directory.GetFiles(before, "pushbar-*.wav").OrderBy(f => f))
            {
                var (pcm, rate) = ReloadSpecSpike.ReadWav(f);
                Print(Path.GetFileNameWithoutExtension(f), Clusters(pcm, rate), 0);
            }
        }

        string? outDir = Arg(args, "out");
        if (outDir != null) WriteSet(outDir, files, ours, before);
        return 0;
    }

    // ── Measuring ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The clusters of hits in a stretch of sound, as the class summary describes.</summary>
    public static List<Cluster> Clusters(float[] pcm, int rate, double hitDb = 30, double gapMs = 80)
    {
        var x = Biquad(pcm.Select(v => (double)v).ToArray(), rate, 30, high: true);
        var hi = Biquad(Biquad(Biquad(Biquad(x, rate, 1000, true), rate, 1000, true), rate, Math.Min(16000, 0.45 * rate), false), rate, Math.Min(16000, 0.45 * rate), false);
        int ms = rate / 1000, n = x.Length / ms;
        var eh = new double[n];
        for (int i = 0; i < n; i++)
        {
            double e = 0;
            for (int k = i * ms; k < (i + 1) * ms; k++) e += hi[k] * hi[k];
            eh[i] = 10 * Math.Log10(e / ms + 1e-24);
        }
        int f5 = 5 * ms, n5 = x.Length / f5;
        var e5 = new double[Math.Max(1, n5)];
        for (int i = 0; i < n5; i++)
        {
            double e = 0;
            for (int k = i * f5; k < (i + 1) * f5; k++) e += x[k] * x[k];
            e5[i] = 10 * Math.Log10(e / f5 + 1e-24);
        }
        double top = eh.Max(), top5 = e5.Max();
        var hits = new List<int>();
        for (int i = 5; i < n - 1; i++)
        {
            double before = double.MaxValue;
            for (int k = i - 5; k < i - 1; k++) before = Math.Min(before, eh[k]);
            if (eh[i] >= eh[i - 1] && eh[i] > eh[i + 1] && eh[i] > before + 6 && eh[i] > top - hitDb) hits.Add(i);
        }
        var groups = new List<List<int>>();
        foreach (int h in hits)
            if (groups.Count > 0 && h - groups[^1][^1] < gapMs) groups[^1].Add(h);
            else groups.Add(new List<int> { h });
        var result = new List<Cluster>();
        foreach (var g in groups)
        {
            int pk = g.OrderByDescending(h => eh[h]).First();
            var c = new Cluster { Start = g[0] / 1000.0, Level5 = e5[Math.Min(e5.Length - 1, pk / 5)] - top5 };
            foreach (int h in g) c.Hits.Add((h - g[0], eh[h] - eh[pk]));
            int a = Math.Max(0, (pk - 1) * ms), b = Math.Min(x.Length, (pk + 29) * ms);
            c.Bands = BandShares(x, a, b, rate);
            result.Add(c);
        }
        return result;
    }

    /// <summary>A stretch's share in each band of <see cref="BandEdges"/>, dB re its whole (zero-padded FFT).</summary>
    public static double[] BandShares(double[] x, int a, int b, int rate)
    {
        int n = 1;
        while (n < b - a) n <<= 1;
        var re = new double[n]; var im = new double[n];
        for (int i = a; i < b; i++) re[i - a] = x[i];
        Fft(re, im);
        double all = 0;
        var bands = new double[BandEdges.Length - 1];
        for (int k = 1; k < n / 2; k++)
        {
            double f = (double)k * rate / n, p = re[k] * re[k] + im[k] * im[k];
            all += p;
            for (int j = 0; j < bands.Length; j++) if (f >= BandEdges[j] && f < BandEdges[j + 1]) bands[j] += p;
        }
        return bands.Select(v => 10 * Math.Log10(Math.Max(v, 1e-30) / Math.Max(all, 1e-30))).ToArray();
    }

    private static void Print(string name, List<Cluster> cs, double offset)
    {
        Console.WriteLine($"  {name}");
        foreach (var c in cs)
            Console.WriteLine($"    {offset + c.Start,7:F3} s  {c.Level5,6:F1} dB  {c.Hits.Count,2} hits over {c.SpanMs,3:F0} ms   30 ms bands "
                              + string.Join(" ", c.Bands.Select(v => $"{v,6:F1}"))
                              + "\n        " + string.Join(" ", c.Hits.Select(h => $"{h.Ms:F0}:{h.Db:F0}")));
    }

    // ── The listening set ───────────────────────────────────────────────────────────────────────────

    private static void WriteSet(string dir, Dictionary<string, (float[] Pcm, int Rate)> files,
                                 List<(string Name, float[] Pcm, PushBarDoor.Report Report)> ours, string? before)
    {
        Directory.CreateDirectory(dir);
        float[] Ref(string file, double from, double to)
        {
            var (pcm, rate) = files[file];
            var y = Resample(pcm[(int)(from * rate)..Math.Min(pcm.Length, (int)(to * rate))], rate, 48000);
            return Fade(y);
        }
        var refOpen = Ref("kyles", 0.0, 0.95);
        var refCloseBerumen = Ref("berumen", 11.62, 12.45);
        var refCloseKyles = Ref("kyles", 1.95, 2.89);
        float[]? Ours(string name) => ours.FirstOrDefault(o => o.Name == name).Pcm;

        var tour = new List<float>();
        var gap = new float[(int)(0.8 * 48000)];
        foreach (var part in new[] { refOpen, Ours("standard open"), refCloseBerumen, Ours("standard close") })
        {
            if (part == null) continue;
            tour.AddRange(Levelled(part)); tour.AddRange(gap);
        }
        Write(Path.Combine(dir, "00 tour reference then ours.wav"), tour.ToArray());

        int n = 1;
        foreach (bool closing in new[] { false, true })
            foreach (string c in Characters)
            {
                var pcm = Ours($"{c} {(closing ? "close" : "open")}");
                if (pcm != null) Write(Path.Combine(dir, $"{n:00} {c} {(closing ? "close" : "open")}.wav"), Levelled(pcm));
                n++;
            }
        Write(Path.Combine(dir, "09 reference push and release (kyles).wav"), Levelled(refOpen));
        Write(Path.Combine(dir, "10 reference slam (berumen).wav"), Levelled(refCloseBerumen));
        Write(Path.Combine(dir, "11 reference slam (kyles).wav"), Levelled(refCloseKyles));
        Write(Path.Combine(dir, "12 reference slams (berumen, all five).wav"),
              Levelled(new[] { (4.86, 5.6), (11.66, 12.4), (19.80, 20.55), (28.55, 29.3), (36.78, 37.5) }
                  .SelectMany(t => Ref("berumen", t.Item1, t.Item2).Concat(new float[(int)(0.5 * 48000)])).ToArray()));
        if (before != null)
        {
            var bo = Path.Combine(before, "pushbar-open-v1.wav");
            var bc = Path.Combine(before, "pushbar-close-v1.wav");
            if (File.Exists(bo)) Write(Path.Combine(dir, "13 before standard open.wav"), Levelled(ReloadSpecSpike.ReadWav(bo).Pcm));
            if (Ours("standard open") is { } so) Write(Path.Combine(dir, "14 after standard open.wav"), Levelled(so));
            if (File.Exists(bc)) Write(Path.Combine(dir, "15 before standard close.wav"), Levelled(ReloadSpecSpike.ReadWav(bc).Pcm));
            if (Ours("standard close") is { } sc) Write(Path.Combine(dir, "16 after standard close.wav"), Levelled(sc));
        }
        Console.WriteLine($"wrote the listening set to {dir}");
    }

    /// <summary>Levelled by loudness: LAFmax at -24 dBFS, unless that puts a peak over -1 dBFS.</summary>
    private static float[] Levelled(float[] x)
    {
        double laf = LafDbfs(x), peak = 20 * Math.Log10(Math.Max(1e-12, x.Max(Math.Abs)));
        double g = Math.Min(Math.Pow(10, (-24 - laf) / 20), Math.Pow(10, (-1 - peak) / 20));
        return x.Select(v => (float)(v * g)).ToArray();
    }

    private static float[] Fade(float[] x)
    {
        int f = Math.Min(x.Length / 2, 480);
        for (int i = 0; i < f; i++) { float g = i / (float)f; x[i] *= g; x[^(i + 1)] *= g; }
        return x;
    }

    private static void Write(string path, float[] pcm)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(48000); w.Write(96000); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (float v in pcm) w.Write((short)Math.Clamp(v * 32767, -32768, 32767));
    }

    // ── Signal tools ────────────────────────────────────────────────────────────────────────────────

    /// <summary>LAFmax of 48 kHz samples, dB re full scale (A-weighting by its bilinear filter, a 125 ms meter).</summary>
    public static double LafDbfs(float[] x)
    {
        double[] b = { 0.234301792299513, -0.468603584599026, -0.234301792299513, 0.937207168598053, -0.234301792299513, -0.468603584599026, 0.234301792299513 };
        double[] a = { 1.0, -4.113043408775871, 6.553121752655047, -4.990849294163381, 1.785737302937573, -0.246190595319487, 0.011224250033231 };
        var xs = new double[7]; var ys = new double[7];
        double k = Math.Exp(-1 / (0.125 * 48000)), e = 0, max = 1e-30;
        foreach (float s in x)
        {
            Array.Copy(xs, 0, xs, 1, 6); xs[0] = s;
            double y = 0;
            for (int i = 0; i < 7; i++) y += b[i] * xs[i];
            for (int i = 1; i < 7; i++) y -= a[i] * ys[i - 1];
            Array.Copy(ys, 0, ys, 1, 6); ys[0] = y;
            e = k * e + (1 - k) * y * y;
            max = Math.Max(max, e);
        }
        return 10 * Math.Log10(max);
    }

    /// <summary>An RBJ second-order Butterworth high- or low-pass.</summary>
    private static double[] Biquad(double[] x, int rate, double hz, bool high)
    {
        double w = 2 * Math.PI * hz / rate, cs = Math.Cos(w), al = Math.Sin(w) / Math.Sqrt(2), a0 = 1 + al;
        double b0 = (high ? (1 + cs) / 2 : (1 - cs) / 2) / a0, b1 = (high ? -(1 + cs) : 1 - cs) / a0, b2 = b0;
        double a1 = -2 * cs / a0, a2 = (1 - al) / a0;
        var y = new double[x.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v; y[i] = v;
        }
        return y;
    }

    /// <summary>Band-limited resampling: a Blackman-windowed sinc, 32 taps a side.</summary>
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
                acc += x[k] * sinc * (0.42 - 0.5 * Math.Cos(2 * Math.PI * u) + 0.08 * Math.Cos(4 * Math.PI * u));
            }
            y[j] = (float)acc;
        }
        return y;
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

    private static string? Arg(string[] args, string name)
        => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
}
