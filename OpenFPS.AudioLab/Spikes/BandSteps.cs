using System.Globalization;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --band-steps FILE.wav [from=s] [to=s] [ref=REFERENCE.wav]
///
/// Whether a sound steps: in each octave from 125 Hz to 8 kHz, the energy of every 2 ms frame against the median of the
/// 50 ms round it (as --moving-echoes reads the top octaves; held to at least -30 dB under the band's mean over the second
/// round it, so a sound rising out of silence is judged against itself, and to -90 dBFS, under hearing at game level), the
/// worst frame and where, the 99.9th percentile and how many stand 10 dB over. A texture of events (water's bubbles) has its own: ref= measures a steady stretch of the same source
/// beside it, so a step that is the cut's, the onset's or a gust's, not the water's, stands out of it.
/// </summary>
public static class BandSteps
{
    public static readonly float[] Centres = { 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f };

    /// <summary>Where each octave's worst frame was in the last measure, s from the start of the file.</summary>
    public static readonly double[] WorstAt = new double[7];

    /// <summary>Nothing under -90 dBFS a band is judged a step: at game level (-20 dBFS is 94 dB SPL) that is under 4 dB
    /// SPL, below hearing; the first whitecaps of a rising wind start there, out of digital silence.</summary>
    private const double Audible = 1e-9;

    public static int Run(string[] args)
    {
        string? file = args.FirstOrDefault(a => a.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("ref=", StringComparison.Ordinal));
        if (file == null) { Console.WriteLine("--band-steps FILE.wav [from=s] [to=s] [ref=REF.wav]"); return 1; }
        double from = Arg(args, "from=", 0), to = Arg(args, "to=", double.MaxValue);
        var (l, r, sr) = ClapRoomSpike.ReadStereo(file);
        Console.WriteLine($"{file}: {l.Length / (double)sr:F1} s");
        Print("this", Measure(l, r, sr, from, to));
        if (args.FirstOrDefault(a => a.StartsWith("ref=", StringComparison.Ordinal)) is { } re)
        {
            var (rl, rr, rsr) = ClapRoomSpike.ReadStereo(re[4..]);
            Print("reference", Measure(rl, rr, rsr, 0, double.MaxValue));
        }
        return 0;
    }

    /// <summary>Per octave: the worst 2 ms frame over the median of the 50 ms round it, the 99.9th percentile (dB), and how
    /// many frames stand over 10 dB, from <paramref name="from"/> to <paramref name="to"/> seconds.</summary>
    public static (double Worst, double P999, int Over10, int Frames)[] Measure(float[] l, float[]? r, int sr, double from = 0, double to = double.MaxValue)
    {
        int a = (int)Math.Clamp(from * sr, 0, l.Length), b = (int)Math.Clamp(to * sr, 0, l.Length);
        int frame = sr / 500;
        const int Round = 12;
        var result = new (double, double, int, int)[Centres.Length];
        for (int band = 0; band < Centres.Length; band++)
        {
            var (b0, b1, b2, a1, a2) = BandPass(Centres[band], sr);
            var e = new List<double>();
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0, u1 = 0, u2 = 0, z1 = 0, z2 = 0;
            double acc = 0;
            int inFrame = 0;
            for (int i = a; i < b; i++)
            {
                double x = r != null ? 0.5 * (l[i] + r[i]) : l[i];
                double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
                double z = b0 * y + b1 * u1 + b2 * u2 - a1 * z1 - a2 * z2;
                u2 = u1; u1 = y; z2 = z1; z1 = z;
                acc += z * z;
                if (++inFrame == frame) { e.Add(acc / frame + 1e-30); acc = 0; inFrame = 0; }
            }
            // Against the median of the 50 ms round it, held to at least a thousandth (-30 dB) of the band's mean over the second
            // round it: a sound rising out of silence is measured against how loud it is, not against nothing.
            var db = new List<double>();
            var prefix = new double[e.Count + 1];
            for (int i = 0; i < e.Count; i++) prefix[i + 1] = prefix[i] + e[i];
            const int Second = 250;
            for (int i = Round + 5; i < e.Count - Round; i++)
            {
                var near = e.GetRange(i - Round, 2 * Round + 1).OrderBy(v => v).ToList();
                int lo = Math.Max(0, i - Second), hi = Math.Min(e.Count, i + Second);
                double mean = (prefix[hi] - prefix[lo]) / (hi - lo);
                db.Add(10 * Math.Log10(e[i] / Math.Max(Math.Max(near[Round], 1e-3 * mean), Audible)));
            }
            if (db.Count == 0) continue;
            var sorted = db.OrderBy(v => v).ToList();
            result[band] = (sorted[^1], sorted[Math.Min(sorted.Count - 1, (int)(0.999 * sorted.Count))], db.Count(v => v > 10), db.Count);
            WorstAt[band] = a / (double)sr + (db.IndexOf(sorted[^1]) + Round + 5) * frame / (double)sr;
        }
        return result;
    }

    public static void Print(string name, (double Worst, double P999, int Over10, int Frames)[] m)
    {
        Console.WriteLine($"  {name}: 2 ms frames over the 50 ms round them, dB (worst / 99.9th percentile / frames over 10 dB of all)");
        for (int b = 0; b < Centres.Length; b++)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    {(Centres[b] >= 1000 ? $"{Centres[b] / 1000:F0}k" : $"{Centres[b]:F0}"),5}  {m[b].Worst,6:F1}  {m[b].P999,6:F1}  {m[b].Over10,5} of {m[b].Frames}   worst at {WorstAt[b]:F2} s"));
    }

    /// <summary>An octave's band-pass (RBJ, constant peak gain), run twice in cascade.</summary>
    private static (double, double, double, double, double) BandPass(float hz, int sr)
    {
        double w = 2 * Math.PI * hz / sr, q = 1.41, alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
        return (alpha / a0, 0, -alpha / a0, -2 * Math.Cos(w) / a0, (1 - alpha) / a0);
    }

    private static double Arg(string[] args, string prefix, double fallback)
        => double.TryParse(args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length),
                           NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
}
