namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// The statistics a listener recognises a sound texture by, after McDermott and Simoncelli (2011,
/// Neuron 71: "Sound texture perception via statistics consistent with peripheral auditory
/// processing"): a cochlear filterbank, each band's envelope compressed as the cochlea compresses it,
/// and the time-averaged moments of those envelopes, how they move together across bands, and how
/// fast they move. Synthetic textures that share these sound like the real thing; noise shaped to the
/// right spectrum does not, because its band envelopes are too steady.
///
/// This is the twin of tools/texture_stats.py and computes the same numbers the same way; the
/// recordings are measured there, the models here and there. Keep the two in step:
///   * 48 kHz in (the caller resamples).
///   * 30 half-cosine bands equally spaced on the ERB-number scale, 50 Hz-16 kHz, applied in the
///     frequency domain; each band's envelope the magnitude of its analytic signal.
///   * The envelope low-passed by two 120-sample moving averages and taken every 120th sample
///     (400 Hz), then raised to the power 0.3.
///   * Per band: mean, coefficient of variation, skewness, kurtosis. Correlations between band
///     envelopes at spacings 1-2, 5-10 and 15+. Modulation power in octave bands 0.5-128 Hz as a share
///     of each envelope's variance.
/// </summary>
public static class TextureStatistics
{
    public const int Rate = 48000;
    public const int Bands = 30;
    public const float LowHz = 50f, HighHz = 16000f;
    public const int EnvelopeRate = 400;
    private const int Hop = Rate / EnvelopeRate;
    public const double Compression = 0.3;
    public static readonly double[] ModulationCentres = Enumerable.Range(0, 9).Select(k => 0.5 * Math.Pow(2, k)).ToArray();

    /// <summary>The four regions the summary averages over. 12-16 kHz is reported, not used: the
    /// fountain, fire and wind references are 128 kbps MP3s, low-passed near 16 kHz.</summary>
    public static readonly (string Name, double Lo, double Hi)[] Regions =
    {
        ("0.2-1k", 200, 1000), ("1-3k", 1000, 3000), ("3-6k", 3000, 6000), ("6-12k", 6000, 12000),
    };

    public sealed class Result
    {
        public double[] Centres = Array.Empty<double>();
        public double[] Power = Array.Empty<double>();
        public double[] Mean = Array.Empty<double>(), Cv = Array.Empty<double>(), Skew = Array.Empty<double>(), Kurtosis = Array.Empty<double>();
        public double[,] Correlation = new double[0, 0];
        public double[,] Modulation = new double[0, 0];

        /// <summary>The summary features, keyed as texture_stats.py keys them.</summary>
        public Dictionary<string, double> Summary()
        {
            var s = new Dictionary<string, double>();
            foreach (var (name, lo, hi) in Regions.Append(("12-16k", 12000.0, 16000.0)))
            {
                var sel = Enumerable.Range(0, Bands).Where(k => Centres[k] >= lo && Centres[k] < hi).ToArray();
                if (sel.Length == 0) continue;
                s[$"cv {name}"] = sel.Average(k => Cv[k]);
                s[$"skew {name}"] = sel.Average(k => Skew[k]);
                s[$"kurt {name}"] = sel.Average(k => Kurtosis[k]);
            }
            double Offsets(int lo, int hi)
            {
                double sum = 0; int n = 0;
                for (int d = lo; d <= hi; d++)
                    for (int i = 0; i + d < Bands; i++)
                        if (Centres[i] >= 200 && Centres[i] < 12000 && Centres[i + d] < 12000) { sum += Correlation[i, i + d]; n++; }
                return n > 0 ? sum / n : double.NaN;
            }
            s["corr near"] = Offsets(1, 2);
            s["corr octave"] = Offsets(5, 10);
            s["corr far"] = Offsets(15, 29);
            var hf = Enumerable.Range(0, Bands).Where(k => Centres[k] >= 1000 && Centres[k] < 12000).ToArray();
            double Mod(int a, int b) => hf.Average(k => { double t = 0; for (int j = a; j <= b; j++) t += Modulation[k, j]; return t; });
            s["mod slow"] = Mod(0, 2);
            s["mod mid"] = Mod(3, 5);
            s["mod fast"] = Mod(6, 8);
            return s;
        }
    }

    private static double ErbNumber(double f) => 21.4 * Math.Log10(1 + 0.00437 * f);
    private static double ErbToHz(double e) => (Math.Pow(10, e / 21.4) - 1) / 0.00437;

    /// <summary>
    /// What happens INSIDE 10 ms, which the envelope statistics cannot see (their envelope is
    /// smoothed to 2.5 ms and compressed): the 4-16 kHz waveform's kurtosis in each 10 ms window,
    /// averaged, and the median crest, dB. Gaussian noise reads 3 and about 10 dB; a window holding a
    /// few needle-sharp clicks far more (rain round 1 read 9-10, every rain recording 3.0-4.4). Windows
    /// 40 dB under the median window are skipped. The band is a fourth-order Butterworth high-pass and
    /// low-pass (tools/texture_stats.py wave uses scipy's eighth-order band-pass; on the recordings
    /// they agree within about 0.3).
    /// </summary>
    public static (double Kurtosis, double CrestDb) Waveform(ReadOnlySpan<float> x, double loHz = 4000, double hiHz = 16000)
    {
        hiHz = Math.Min(hiHz, 0.45 * Rate);
        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = x[i];
        foreach (double q in new[] { 0.5411961, 1.3065630 })
        {
            Section(y, loHz, q, high: true);
            Section(y, hiHz, q, high: false);
        }
        int w = Rate / 100, m = y.Length / w;
        var m2 = new double[m];
        for (int k = 0; k < m; k++) { double s = 0; for (int i = k * w; i < (k + 1) * w; i++) s += y[i] * y[i]; m2[k] = s / w; }
        var sorted = (double[])m2.Clone();
        Array.Sort(sorted);
        double floor = sorted.Length > 0 ? sorted[sorted.Length / 2] * 1e-4 : 0;
        double kurt = 0; int n = 0;
        var crest = new List<double>();
        for (int k = 0; k < m; k++)
        {
            if (m2[k] <= floor) continue;
            double s4 = 0, peak = 0;
            for (int i = k * w; i < (k + 1) * w; i++) { s4 += y[i] * y[i] * y[i] * y[i]; peak = Math.Max(peak, Math.Abs(y[i])); }
            kurt += s4 / w / (m2[k] * m2[k]);
            crest.Add(20 * Math.Log10(peak / Math.Sqrt(m2[k])));
            n++;
        }
        crest.Sort();
        return (n > 0 ? kurt / n : 0, crest.Count > 0 ? crest[crest.Count / 2] : 0);
    }

    private static void Section(double[] y, double hz, double q, bool high)
    {
        double w = 2 * Math.PI * hz / Rate, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        double b0 = high ? (1 + c) / 2 : (1 - c) / 2, b1 = high ? -(1 + c) : 1 - c, a0 = 1 + alpha;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < y.Length; i++)
        {
            double xi = y[i];
            double yi = (b0 * xi + b1 * x1 + b0 * x2 + 2 * c * y1 - (1 - alpha) * y2) / a0;
            x2 = x1; x1 = xi; y2 = y1; y1 = yi;
            y[i] = yi;
        }
    }

    /// <summary>The 10 ms 4-16 kHz kurtosis of the rain recordings (Waveform, mean over windows):
    /// streets, gardens, woods, a car roof, a tiled roof, a sheet-metal roof, a window. Measured with
    /// tools/texture_stats.py wave over each whole file.</summary>
    public const double RainWaveformKurtosisMin = 3.05, RainWaveformKurtosisMax = 4.36;

    /// <summary>The same for the recorded creeks and rivers (the "stream" references), measured the same
    /// way over each whole file (2026-10-06): a riffle heard close is a wash with a few plinks in it.</summary>
    public const double StreamWaveformKurtosisMin = 2.95, StreamWaveformKurtosisMax = 6.96;

    /// <summary>The statistics of a 48 kHz signal. Ten seconds or more; a minute is better.</summary>
    public static Result Analyse(ReadOnlySpan<float> x)
    {
        int n = x.Length;
        int nfft = 1;
        while (nfft < n) nfft <<= 1;
        var re = new double[nfft];
        var im = new double[nfft];
        for (int i = 0; i < n; i++) re[i] = x[i];
        var fft = new Fft(nfft);
        fft.Transform(re, im, inverse: false);

        double eLo = ErbNumber(LowHz), eHi = ErbNumber(Math.Min(HighHz, 0.45 * Rate));
        double spacing = (eHi - eLo) / (Bands - 1);
        var r = new Result
        {
            Centres = new double[Bands], Power = new double[Bands], Mean = new double[Bands], Cv = new double[Bands],
            Skew = new double[Bands], Kurtosis = new double[Bands],
        };
        int half = nfft / 2;
        var ef = new double[half + 1];
        for (int k = 0; k <= half; k++) ef[k] = ErbNumber(Math.Max(1e-6, k * (double)Rate / nfft));

        int m = (n + Hop - 1) / Hop;
        int cut = EnvelopeRate / 4;
        int keep = m > 4 * cut ? m - 2 * cut : m;
        int from = m > 4 * cut ? cut : 0;
        var env = new double[Bands][];
        var br = new double[nfft];
        var bi = new double[nfft];
        var mag = new double[n];
        for (int b = 0; b < Bands; b++)
        {
            double centre = eLo + b * spacing;
            r.Centres[b] = ErbToHz(centre);
            Array.Clear(br); Array.Clear(bi);
            double power = 0;
            for (int k = 0; k <= half; k++)
            {
                double d = (ef[k] - centre) / spacing;
                if (Math.Abs(d) >= 1) continue;
                double h = Math.Cos(d * Math.PI / 2);
                double gr = re[k] * h, gi = im[k] * h;
                power += gr * gr + gi * gi;
                // The analytic signal: the positive half doubled, the negative half empty.
                double twice = k == 0 || k == half ? 1 : 2;
                br[k] = gr * twice; bi[k] = gi * twice;
            }
            r.Power[b] = power;
            fft.Transform(br, bi, inverse: true);
            for (int i = 0; i < n; i++) mag[i] = Math.Sqrt(br[i] * br[i] + bi[i] * bi[i]);
            var dec = Decimate(mag);
            var e = new double[keep];
            for (int i = 0; i < keep; i++) e[i] = Math.Pow(Math.Max(0, dec[from + i]), Compression);
            env[b] = e;
        }

        var z = new double[Bands][];
        for (int b = 0; b < Bands; b++)
        {
            var e = env[b];
            double mean = e.Average();
            double var = e.Sum(v => (v - mean) * (v - mean)) / e.Length;
            double sd = Math.Sqrt(var);
            r.Mean[b] = mean;
            r.Cv[b] = sd / (mean + 1e-30);
            var zz = new double[e.Length];
            double s3 = 0, s4 = 0;
            for (int i = 0; i < e.Length; i++)
            {
                double q = (e[i] - mean) / (sd + 1e-30);
                zz[i] = q;
                s3 += q * q * q; s4 += q * q * q * q;
            }
            r.Skew[b] = s3 / e.Length;
            r.Kurtosis[b] = s4 / e.Length;
            z[b] = zz;
        }
        r.Correlation = new double[Bands, Bands];
        for (int a = 0; a < Bands; a++)
            for (int b = a; b < Bands; b++)
            {
                double c = 0;
                for (int i = 0; i < keep; i++) c += z[a][i] * z[b][i];
                c /= keep;
                r.Correlation[a, b] = r.Correlation[b, a] = c;
            }

        int m2 = 1;
        while (m2 < keep) m2 <<= 1;
        var mfft = new Fft(m2);
        r.Modulation = new double[Bands, ModulationCentres.Length];
        var mr = new double[m2];
        var mi = new double[m2];
        for (int b = 0; b < Bands; b++)
        {
            Array.Clear(mr); Array.Clear(mi);
            double mean = r.Mean[b];
            for (int i = 0; i < keep; i++) mr[i] = env[b][i] - mean;
            mfft.Transform(mr, mi, inverse: false);
            double total = 1e-30;
            var p = new double[m2 / 2 + 1];
            for (int k = 0; k <= m2 / 2; k++) { p[k] = mr[k] * mr[k] + mi[k] * mi[k]; total += p[k]; }
            for (int j = 0; j < ModulationCentres.Length; j++)
            {
                double sum = 0;
                for (int k = 1; k <= m2 / 2; k++)
                {
                    double d = Math.Log2(k * (double)EnvelopeRate / m2 / ModulationCentres[j]);
                    if (Math.Abs(d) >= 1) continue;
                    double h = Math.Cos(d * Math.PI / 2);
                    sum += p[k] * h * h;
                }
                r.Modulation[b, j] = sum / total;
            }
        }
        return r;
    }

    /// <summary>Two centred moving averages of <see cref="Hop"/> samples, every Hop-th sample kept.</summary>
    private static double[] Decimate(double[] x)
    {
        var once = Box(x, Hop);
        var twice = Box(once, Hop);
        int m = (x.Length + Hop - 1) / Hop;
        var y = new double[m];
        for (int i = 0; i < m; i++) y[i] = twice[i * Hop];
        return y;
    }

    private static double[] Box(double[] x, int w)
    {
        var c = new double[x.Length + 1];
        for (int i = 0; i < x.Length; i++) c[i + 1] = c[i] + x[i];
        var y = new double[x.Length];
        int h = w / 2;
        for (int i = 0; i < x.Length; i++)
        {
            int lo = Math.Clamp(i - h, 0, x.Length), hi = Math.Clamp(i - h + w, 0, x.Length);
            y[i] = (c[hi] - c[lo]) / w;
        }
        return y;
    }

    /// <summary>A radix-2 complex FFT with its twiddles made once.</summary>
    private sealed class Fft
    {
        private readonly int _n;
        private readonly double[] _cos, _sin;
        private readonly int[] _rev;

        public Fft(int n)
        {
            _n = n;
            _cos = new double[n / 2];
            _sin = new double[n / 2];
            for (int k = 0; k < n / 2; k++) { _cos[k] = Math.Cos(2 * Math.PI * k / n); _sin[k] = Math.Sin(2 * Math.PI * k / n); }
            _rev = new int[n];
            int bits = 0;
            while ((1 << bits) < n) bits++;
            for (int i = 0; i < n; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                _rev[i] = r;
            }
        }

        public void Transform(double[] re, double[] im, bool inverse)
        {
            int n = _n;
            for (int i = 0; i < n; i++)
            {
                int j = _rev[i];
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            double sign = inverse ? 1 : -1;
            for (int len = 2; len <= n; len <<= 1)
            {
                int step = n / len;
                int hl = len / 2;
                for (int i = 0; i < n; i += len)
                    for (int k = 0; k < hl; k++)
                    {
                        double wr = _cos[k * step], wi = sign * _sin[k * step];
                        int a = i + k, b = a + hl;
                        double tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                    }
            }
            if (inverse)
                for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
        }
    }

    // ── The references ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The summary features of the reference recordings, each cut into 20 s pieces (the length a test
    /// renders; a recording under 20 s is one piece) and each piece measured with tools/texture_stats.py,
    /// so a range is the recordings' own spread including how far one recording wanders from one 20 s
    /// to the next (one 30 s window of a fountain moved its octave correlation from 0.16 to 0.24). The
    /// files are in
    /// ~/openfps-scratch-archive/{nature,rain}-2026-10-05/refs, listed with their sources in
    /// SOURCES.txt). Recordings are a yardstick and are never played in the game; their numbers are
    /// written here so the tests can hold a model inside their spread without them. "fountain", "leaves"
    /// (wind in broadleaf trees), "fire", "rain" (streets, gardens, woods, a car roof, a tiled roof, a
    /// window; not the two whose originals stop at 4 and 8 kHz). Running water (2026-10-06,
    /// ~/openfps-scratch-archive/running-water-2026-10-06/refs): "stream" (six brooks and creeks, two river
    /// riffles), "drain" (storm drains and a trough's overflow drain), "downpipe" (five downpipes and
    /// spouts) and "overflow" (a pond overflow, a tiny dam, a small weir, a fountain's overflow).
    /// </summary>
    public static IReadOnlyList<(string File, IReadOnlyDictionary<string, double> Summary)> References(string texture)
        => ReferenceRows[texture].Select(r => (r.File, (IReadOnlyDictionary<string, double>)Keys.Select((k, i) => (k, r.Values[i])).ToDictionary(p => p.k, p => p.Item2))).ToList();

    /// <summary>The references' range of one feature.</summary>
    public static (double Min, double Max) Range(string texture, string key)
    {
        var rows = References(texture);
        return (rows.Min(r => r.Summary[key]), rows.Max(r => r.Summary[key]));
    }

    /// <summary>The summary keys, in the order the reference rows below are written.</summary>
    public static readonly string[] Keys = { "cv 0.2-1k", "skew 0.2-1k", "kurt 0.2-1k", "cv 1-3k", "skew 1-3k", "kurt 1-3k", "cv 3-6k", "skew 3-6k", "kurt 3-6k", "cv 6-12k", "skew 6-12k", "kurt 6-12k", "cv 12-16k", "skew 12-16k", "kurt 12-16k", "corr near", "corr octave", "corr far", "mod slow", "mod mid", "mod fast" };

    private static readonly Dictionary<string, (string File, double[] Values)[]> ReferenceRows = new()
    {
        ["fountain"] = new[]
        {
            ("fountain_1 0-20 s", new[] { 0.161, -0.087, 2.766, 0.141, 0.372, 3.793, 0.115, 0.598, 4.440, 0.096, 0.729, 5.170, 0.078, 0.502, 4.278, 0.258, 0.083, 0.017, 0.037, 0.247, 0.674 }),
            ("fountain_1 20-40 s", new[] { 0.166, 0.093, 3.494, 0.140, 0.319, 3.529, 0.114, 0.434, 3.656, 0.093, 0.522, 4.027, 0.079, 0.427, 3.889, 0.252, 0.079, 0.002, 0.036, 0.243, 0.670 }),
            ("fountain_1 40-60 s", new[] { 0.165, -0.022, 2.877, 0.139, 0.323, 3.544, 0.113, 0.440, 3.774, 0.097, 0.587, 4.261, 0.083, 0.672, 4.706, 0.255, 0.082, -0.008, 0.035, 0.233, 0.675 }),
            ("fountain_2 0-20 s", new[] { 0.176, 0.213, 3.287, 0.143, 0.214, 3.190, 0.128, 0.232, 3.101, 0.139, -0.089, 2.900, 0.141, -0.129, 2.780, 0.345, 0.158, 0.079, 0.049, 0.137, 0.484 }),
            ("fountain_2 20-40 s", new[] { 0.191, 0.443, 3.816, 0.150, 0.356, 3.341, 0.125, 0.353, 3.265, 0.141, 0.386, 2.790, 0.134, 0.212, 2.455, 0.336, 0.176, 0.108, 0.075, 0.145, 0.432 }),
            ("fountain_2 40-60 s", new[] { 0.182, 0.438, 4.236, 0.139, 0.310, 3.541, 0.101, 0.234, 3.381, 0.093, 0.324, 3.330, 0.080, 0.540, 4.006, 0.215, 0.047, 0.057, 0.054, 0.190, 0.636 }),
            ("fountain_3 0-20 s", new[] { 0.288, 1.315, 6.172, 0.231, 1.094, 5.036, 0.207, 1.321, 6.611, 0.190, 1.196, 5.674, 0.167, 1.026, 4.752, 0.522, 0.153, 0.122, 0.143, 0.373, 0.389 }),
        },
        ["leaves"] = new[]
        {
            ("wind_1 0-20 s", new[] { 0.164, -0.011, 2.854, 0.141, 0.351, 3.492, 0.181, 0.992, 4.128, 0.203, 0.896, 3.123, 0.182, 0.793, 2.788, 0.417, 0.274, 0.227, 0.097, 0.105, 0.348 }),
            ("wind_1 20-40 s", new[] { 0.189, 0.263, 3.012, 0.175, 0.440, 3.051, 0.184, 0.548, 2.874, 0.177, 0.515, 2.707, 0.182, 0.454, 2.467, 0.580, 0.517, 0.475, 0.026, 0.074, 0.267 }),
            ("wind_1 40-60 s", new[] { 0.196, 0.263, 2.843, 0.201, 0.500, 2.669, 0.194, 0.474, 2.514, 0.187, 0.345, 2.011, 0.176, 0.278, 2.030, 0.631, 0.582, 0.505, 0.031, 0.057, 0.211 }),
            ("wind_2 0-20 s", new[] { 0.160, -0.117, 2.722, 0.134, 0.134, 3.073, 0.106, 0.400, 3.467, 0.091, 0.651, 3.913, 0.092, 0.978, 4.652, 0.256, 0.146, 0.058, 0.039, 0.148, 0.555 }),
            ("wind_2 20-40 s", new[] { 0.161, -0.143, 2.738, 0.137, 0.151, 3.182, 0.122, 0.335, 3.123, 0.113, 0.425, 3.151, 0.116, 0.539, 3.517, 0.322, 0.197, 0.030, 0.028, 0.129, 0.469 }),
            ("wind_2 40-60 s", new[] { 0.158, -0.149, 2.753, 0.126, -0.005, 2.878, 0.093, 0.143, 3.319, 0.076, 0.415, 4.160, 0.076, 0.706, 4.858, 0.146, 0.043, 0.000, 0.033, 0.178, 0.682 }),
            ("wind_3 0-20 s", new[] { 0.193, 0.620, 4.755, 0.199, 1.537, 7.796, 0.141, 1.662, 9.689, 0.101, 1.381, 8.621, 0.079, 2.008, 13.775, 0.479, 0.196, 0.058, 0.220, 0.280, 0.378 }),
            ("wind_3 20-40 s", new[] { 0.166, -0.038, 2.845, 0.138, 0.321, 3.954, 0.113, 0.610, 4.453, 0.107, 1.009, 5.456, 0.124, 2.017, 9.061, 0.298, 0.161, 0.074, 0.046, 0.218, 0.537 }),
            ("wind_3 40-60 s", new[] { 0.161, -0.089, 2.798, 0.133, 0.184, 3.304, 0.102, 0.577, 5.139, 0.091, 1.211, 8.675, 0.088, 2.077, 15.723, 0.218, 0.064, -0.008, 0.057, 0.250, 0.622 }),
        },
        ["fire"] = new[]
        {
            ("fire_1 0-20 s", new[] { 0.187, 0.667, 6.258, 0.222, 3.199, 26.946, 0.260, 3.838, 28.227, 0.233, 3.649, 26.277, 0.203, 3.431, 23.804, 0.653, 0.450, 0.173, 0.127, 0.280, 0.512 }),
            ("fire_1 20-40 s", new[] { 0.194, 1.799, 21.976, 0.212, 3.679, 39.870, 0.238, 4.053, 35.813, 0.215, 3.585, 27.268, 0.188, 3.177, 24.379, 0.655, 0.447, 0.173, 0.110, 0.337, 0.526 }),
            ("fire_1 40-60 s", new[] { 0.200, 2.114, 25.113, 0.257, 4.711, 56.804, 0.297, 3.848, 27.438, 0.270, 3.414, 20.840, 0.243, 3.284, 20.201, 0.717, 0.530, 0.212, 0.196, 0.299, 0.448 }),
            ("fire_2 0-20 s", new[] { 0.240, 3.126, 37.134, 0.235, 4.596, 50.993, 0.218, 5.370, 57.486, 0.193, 5.720, 71.011, 0.165, 6.445, 86.399, 0.725, 0.554, 0.346, 0.109, 0.335, 0.492 }),
            ("fire_2 20-40 s", new[] { 0.241, 3.640, 42.504, 0.290, 7.140, 97.767, 0.283, 7.519, 100.923, 0.236, 6.324, 72.920, 0.186, 6.066, 68.974, 0.787, 0.663, 0.411, 0.190, 0.370, 0.412 }),
            ("fire_2 40-60 s", new[] { 0.211, 2.384, 30.248, 0.246, 7.228, 124.694, 0.211, 5.728, 73.080, 0.179, 4.775, 47.911, 0.149, 4.704, 43.754, 0.706, 0.537, 0.254, 0.165, 0.331, 0.465 }),
            ("fire_3 0-20 s", new[] { 0.209, 0.990, 6.065, 0.268, 2.772, 18.985, 0.271, 3.116, 21.154, 0.251, 3.245, 23.165, 0.213, 2.728, 17.135, 0.705, 0.494, 0.127, 0.114, 0.479, 0.358 }),
        },
        // Running water (2026-10-06, docs/RUNNING_WATER.md): ~/openfps-scratch-archive/running-water-2026-10-06/refs,
        // Freesound previews and two Commons originals, measured with tools/texture_stats.py in 20 s pieces.
        ["stream"] = new[]
        {
            ("creek_reinsamba_38024 0-20 s", new[] { 0.188, 0.287, 3.306, 0.160, 0.547, 3.919, 0.135, 0.656, 4.135, 0.126, 0.917, 4.928, 0.111, 0.990, 5.641, 0.334, 0.102, 0.042, 0.043, 0.308, 0.623 }),
            ("creek_reinsamba_38024 20-40 s", new[] { 0.190, 0.350, 3.533, 0.159, 0.588, 4.190, 0.138, 0.749, 4.456, 0.128, 1.004, 5.694, 0.115, 1.086, 6.091, 0.338, 0.096, 0.029, 0.037, 0.315, 0.622 }),
            ("creek_yuval_197023 0-20 s", new[] { 0.226, 0.766, 4.580, 0.184, 0.888, 4.702, 0.162, 1.263, 6.452, 0.157, 1.192, 5.681, 0.151, 1.240, 5.917, 0.354, 0.086, 0.030, 0.052, 0.298, 0.622 }),
            ("creek_yuval_197023 20-40 s", new[] { 0.223, 0.772, 4.735, 0.182, 0.869, 4.576, 0.161, 1.183, 5.976, 0.156, 1.214, 5.658, 0.150, 1.222, 6.011, 0.343, 0.077, 0.034, 0.048, 0.291, 0.635 }),
            ("creek_yuval_197023 40-60 s", new[] { 0.219, 0.622, 4.053, 0.182, 0.894, 4.617, 0.164, 1.364, 6.935, 0.154, 1.197, 5.798, 0.152, 1.205, 5.588, 0.350, 0.086, 0.032, 0.048, 0.289, 0.637 }),
            ("creek_kevp888_700993 0-20 s", new[] { 0.171, 0.076, 2.981, 0.139, 0.291, 3.480, 0.099, 0.286, 3.547, 0.080, 0.435, 3.899, 0.078, 0.755, 4.946, 0.184, 0.031, 0.014, 0.035, 0.237, 0.690 }),
            ("creek_kevp888_700993 20-40 s", new[] { 0.170, 0.064, 3.017, 0.139, 0.245, 3.268, 0.100, 0.296, 3.523, 0.082, 0.571, 4.846, 0.080, 0.842, 5.542, 0.194, 0.032, 0.014, 0.036, 0.233, 0.693 }),
            ("creek_kevp888_700993 40-60 s", new[] { 0.168, 0.069, 2.902, 0.137, 0.208, 3.220, 0.100, 0.295, 3.545, 0.082, 0.500, 4.512, 0.078, 0.669, 4.541, 0.180, 0.032, 0.002, 0.034, 0.227, 0.702 }),
            ("creek_pancakeshrapnel_410770 0-20 s", new[] { 0.201, 0.482, 3.695, 0.162, 0.646, 4.284, 0.139, 0.964, 5.365, 0.139, 1.253, 6.199, 0.130, 1.336, 6.749, 0.333, 0.096, 0.029, 0.061, 0.238, 0.667 }),
            ("creek_pancakeshrapnel_410770 20-40 s", new[] { 0.209, 0.760, 5.688, 0.161, 0.673, 4.551, 0.142, 1.086, 5.955, 0.140, 1.420, 7.416, 0.132, 1.395, 7.001, 0.345, 0.094, 0.038, 0.054, 0.246, 0.667 }),
            ("creek_pancakeshrapnel_410770 40-60 s", new[] { 0.206, 0.545, 3.885, 0.161, 0.699, 4.661, 0.142, 1.082, 5.912, 0.138, 1.382, 7.225, 0.132, 1.606, 9.067, 0.341, 0.095, 0.041, 0.057, 0.248, 0.663 }),
            ("creek_drtrumpet_molln 0-20 s", new[] { 0.193, 0.538, 4.072, 0.157, 0.646, 4.513, 0.143, 1.304, 7.000, 0.120, 1.446, 7.598, 0.140, 1.411, 6.911, 0.358, 0.161, 0.127, 0.075, 0.208, 0.591 }),
            ("creek_dobroide_126281 0-20 s", new[] { 0.164, -0.023, 2.821, 0.134, 0.226, 3.392, 0.098, 0.405, 4.303, 0.086, 0.344, 3.747, 0.076, 0.629, 4.000, 0.133, 0.010, 0.003, 0.043, 0.194, 0.697 }),
            ("creek_dobroide_126281 20-40 s", new[] { 0.162, -0.077, 2.821, 0.134, 0.192, 3.244, 0.097, 0.316, 3.897, 0.079, 0.341, 3.975, 0.065, 0.272, 3.387, 0.126, 0.014, 0.001, 0.039, 0.202, 0.715 }),
            ("creek_dobroide_126281 40-60 s", new[] { 0.164, -0.035, 2.812, 0.134, 0.218, 3.264, 0.103, 0.703, 6.098, 0.088, 1.192, 10.370, 0.077, 2.250, 23.754, 0.178, 0.041, 0.006, 0.067, 0.210, 0.664 }),
            ("river_sadiquecat_698691 0-20 s", new[] { 0.157, -0.141, 2.723, 0.124, -0.046, 2.836, 0.088, 0.004, 2.948, 0.066, 0.036, 3.039, 0.057, 0.225, 3.554, 0.077, 0.005, 0.005, 0.025, 0.175, 0.754 }),
            ("river_sadiquecat_698691 20-40 s", new[] { 0.158, -0.142, 2.724, 0.124, -0.053, 2.851, 0.089, -0.027, 2.970, 0.066, 0.038, 3.057, 0.057, 0.121, 3.301, 0.079, 0.003, 0.007, 0.025, 0.179, 0.751 }),
            ("river_sadiquecat_698691 40-60 s", new[] { 0.157, -0.161, 2.710, 0.123, -0.039, 2.859, 0.089, -0.019, 2.994, 0.067, 0.016, 3.168, 0.057, 0.181, 3.325, 0.076, 0.002, 0.007, 0.025, 0.176, 0.753 }),
            ("river_nicoproson_642774 0-20 s", new[] { 0.161, -0.099, 2.741, 0.127, 0.048, 3.008, 0.093, 0.151, 3.318, 0.072, 0.247, 3.613, 0.064, 0.365, 4.007, 0.118, 0.014, 0.008, 0.028, 0.185, 0.740 }),
            ("river_nicoproson_642774 20-40 s", new[] { 0.161, -0.098, 2.769, 0.127, 0.023, 2.942, 0.093, 0.158, 3.272, 0.072, 0.235, 3.558, 0.064, 0.362, 3.859, 0.118, 0.013, 0.007, 0.028, 0.187, 0.741 }),
            ("river_nicoproson_642774 40-60 s", new[] { 0.162, -0.087, 2.825, 0.127, 0.004, 2.918, 0.092, 0.105, 3.227, 0.072, 0.230, 3.453, 0.064, 0.488, 4.890, 0.117, 0.012, 0.005, 0.025, 0.190, 0.740 }),
        },
        ["drain"] = new[]
        {
            ("drain_heckfricker_723698 0-20 s", new[] { 0.185, 0.492, 4.937, 0.212, 0.871, 4.524, 0.187, 1.088, 5.238, 0.196, 1.132, 5.251, 0.188, 1.401, 8.095, 0.413, 0.060, 0.011, 0.153, 0.477, 0.332 }),
            ("drain_quartzmmn_858174 0-20 s", new[] { 0.182, 0.223, 3.271, 0.139, 0.218, 3.155, 0.097, 0.141, 3.172, 0.078, 0.180, 3.204, 0.072, 0.162, 3.141, 0.184, 0.030, 0.004, 0.064, 0.274, 0.608 }),
            ("drain_quartzmmn_858174 20-40 s", new[] { 0.183, 0.258, 3.561, 0.135, 0.168, 3.182, 0.098, 0.167, 3.260, 0.076, 0.163, 3.347, 0.071, 0.201, 3.437, 0.183, 0.031, 0.018, 0.062, 0.277, 0.620 }),
            ("drain_quartzmmn_858174 40-60 s", new[] { 0.181, 0.154, 3.030, 0.140, 0.275, 3.490, 0.097, 0.167, 3.179, 0.078, 0.212, 3.164, 0.071, 0.222, 3.521, 0.200, 0.042, 0.007, 0.065, 0.288, 0.602 }),
            ("drain_frosthardr_553456 0-20 s", new[] { 0.191, 0.307, 3.435, 0.144, 0.282, 3.289, 0.109, 0.428, 3.739, 0.087, 0.484, 3.920, 0.081, 0.668, 4.404, 0.249, 0.033, 0.045, 0.092, 0.304, 0.561 }),
            ("drain_frosthardr_553456 20-40 s", new[] { 0.190, 0.253, 3.200, 0.147, 0.424, 4.061, 0.108, 0.384, 3.591, 0.090, 0.677, 5.227, 0.082, 0.656, 4.434, 0.257, 0.041, 0.019, 0.107, 0.301, 0.548 }),
            ("drain_frosthardr_553456 40-60 s", new[] { 0.190, 0.251, 3.303, 0.144, 0.421, 3.914, 0.109, 0.535, 4.404, 0.086, 0.465, 3.945, 0.079, 0.505, 3.662, 0.246, 0.047, 0.027, 0.092, 0.311, 0.555 }),
            ("drain_mabbett_horsetrough 0-20 s", new[] { 0.312, 0.887, 3.553, 0.354, 1.595, 5.595, 0.468, 1.865, 6.665, 0.418, 1.641, 6.270, 0.360, 1.502, 5.908, 0.831, 0.674, 0.469, 0.157, 0.138, 0.540 }),
            ("drain_mabbett_horsetrough 20-40 s", new[] { 0.319, 0.877, 3.591, 0.357, 1.442, 5.175, 0.461, 1.765, 6.294, 0.421, 1.611, 6.078, 0.370, 1.415, 5.225, 0.838, 0.683, 0.497, 0.143, 0.136, 0.511 }),
            ("drain_mabbett_horsetrough 40-60 s", new[] { 0.314, 0.963, 3.853, 0.348, 1.645, 6.131, 0.439, 1.944, 7.588, 0.386, 1.526, 6.060, 0.336, 1.511, 6.349, 0.822, 0.659, 0.463, 0.186, 0.150, 0.514 }),
        },
        ["downpipe"] = new[]
        {
            ("downpipe_nicotep_805969 0-20 s", new[] { 0.211, 0.942, 6.075, 0.208, 1.748, 9.658, 0.189, 2.241, 11.906, 0.238, 2.106, 9.488, 0.263, 1.759, 7.227, 0.503, 0.227, 0.098, 0.130, 0.385, 0.454 }),
            ("downpipe_nicotep_805969 20-40 s", new[] { 0.222, 1.027, 6.699, 0.210, 1.671, 8.536, 0.180, 2.243, 12.328, 0.236, 2.050, 9.361, 0.268, 2.096, 10.374, 0.490, 0.210, 0.095, 0.129, 0.396, 0.451 }),
            ("downpipe_nicotep_805969 40-60 s", new[] { 0.199, 0.717, 5.264, 0.211, 1.760, 9.355, 0.187, 2.628, 16.223, 0.225, 2.131, 9.873, 0.250, 1.720, 7.030, 0.474, 0.196, 0.091, 0.132, 0.399, 0.445 }),
            ("downpipe_alienistcog_124975 0-20 s", new[] { 0.269, 0.835, 4.869, 0.244, 0.941, 4.564, 0.190, 0.968, 4.841, 0.160, 1.182, 6.320, 0.140, 1.023, 5.648, 0.466, 0.121, 0.043, 0.085, 0.464, 0.423 }),
            ("downpipe_digifishmusic_71594 0-20 s", new[] { 0.161, -0.099, 2.841, 0.173, 0.839, 5.185, 0.218, 1.593, 7.462, 0.227, 1.217, 5.205, 0.220, 1.061, 4.412, 0.372, 0.121, 0.030, 0.096, 0.379, 0.413 }),
            ("downpipe_digifishmusic_71594 20-40 s", new[] { 0.162, -0.090, 2.834, 0.177, 0.947, 5.750, 0.204, 1.359, 6.008, 0.211, 1.135, 4.946, 0.213, 1.164, 5.276, 0.369, 0.108, 0.030, 0.097, 0.433, 0.433 }),
            ("downpipe_digifishmusic_71594 40-60 s", new[] { 0.161, -0.101, 2.743, 0.164, 0.723, 4.772, 0.211, 1.558, 6.947, 0.213, 1.199, 5.392, 0.213, 1.153, 5.083, 0.356, 0.117, 0.034, 0.095, 0.426, 0.445 }),
            ("downpipe_mudflea2_708168 0-20 s", new[] { 0.235, 1.510, 8.794, 0.332, 2.216, 11.946, 0.391, 1.919, 8.381, 0.359, 1.360, 5.372, 0.334, 1.152, 4.543, 0.797, 0.601, 0.365, 0.085, 0.406, 0.491 }),
            ("downpipe_joedeshon_79255 0-20 s", new[] { 0.175, 0.103, 2.969, 0.166, 0.448, 3.675, 0.147, 1.459, 9.124, 0.168, 2.140, 13.127, 0.154, 1.189, 5.639, 0.337, 0.158, 0.099, 0.088, 0.319, 0.551 }),
            ("downpipe_joedeshon_79255 20-40 s", new[] { 0.179, 0.156, 3.064, 0.164, 0.444, 3.809, 0.148, 1.586, 9.975, 0.168, 1.948, 10.662, 0.163, 1.223, 5.216, 0.353, 0.170, 0.107, 0.090, 0.323, 0.533 }),
            ("downpipe_joedeshon_79255 40-60 s", new[] { 0.178, 0.167, 3.132, 0.166, 0.405, 3.541, 0.141, 1.241, 7.492, 0.158, 1.370, 7.677, 0.175, 0.677, 3.069, 0.332, 0.146, 0.085, 0.094, 0.298, 0.549 }),
        },
        ["overflow"] = new[]
        {
            ("overflow_dobroide_26262 0-20 s", new[] { 0.164, -0.070, 2.793, 0.129, 0.044, 2.956, 0.100, 0.256, 3.461, 0.084, 0.426, 3.609, 0.076, 0.497, 4.119, 0.172, 0.021, 0.004, 0.025, 0.189, 0.745 }),
            ("overflow_alexanderweidner_431451 0-20 s", new[] { 0.210, 0.615, 4.043, 0.169, 0.586, 3.918, 0.171, 0.970, 4.833, 0.172, 1.214, 5.372, 0.176, 1.346, 6.000, 0.404, 0.119, 0.106, 0.058, 0.306, 0.597 }),
            ("overflow_alexanderweidner_431451 20-40 s", new[] { 0.208, 0.531, 3.711, 0.167, 0.572, 3.875, 0.173, 0.992, 4.900, 0.165, 1.176, 5.174, 0.166, 1.293, 5.695, 0.382, 0.106, 0.082, 0.050, 0.321, 0.589 }),
            ("overflow_alexanderweidner_431451 40-60 s", new[] { 0.207, 0.601, 3.962, 0.165, 0.540, 3.756, 0.169, 0.985, 4.957, 0.168, 1.189, 5.189, 0.173, 1.321, 5.622, 0.398, 0.121, 0.110, 0.055, 0.303, 0.594 }),
            ("overflow_zachrau_191879 0-20 s", new[] { 0.159, -0.152, 2.691, 0.125, -0.031, 2.856, 0.093, 0.247, 3.796, 0.075, 0.681, 6.065, 0.057, -0.042, 2.955, 0.090, 0.011, 0.007, 0.055, 0.199, 0.684 }),
            ("overflow_zachrau_191879 20-40 s", new[] { 0.158, -0.153, 2.737, 0.123, -0.037, 2.861, 0.089, 0.061, 3.098, 0.069, 0.236, 3.735, 0.057, 0.039, 3.010, 0.094, 0.008, -0.004, 0.036, 0.189, 0.720 }),
            ("overflow_zachrau_191879 40-60 s", new[] { 0.158, -0.163, 2.730, 0.124, -0.027, 2.907, 0.090, 0.095, 3.161, 0.066, 0.024, 3.062, 0.057, 0.026, 3.006, 0.089, 0.003, 0.010, 0.033, 0.194, 0.724 }),
            ("overflow_astounded_483492 0-20 s", new[] { 0.188, 0.235, 3.235, 0.157, 0.418, 3.605, 0.131, 0.599, 4.231, 0.118, 0.629, 3.999, 0.110, 0.776, 4.555, 0.319, 0.137, 0.074, 0.125, 0.270, 0.571 }),
            ("overflow_astounded_483492 20-40 s", new[] { 0.187, 0.227, 3.187, 0.161, 0.496, 3.870, 0.133, 0.577, 3.882, 0.121, 0.753, 4.482, 0.112, 0.941, 5.687, 0.334, 0.136, 0.083, 0.130, 0.269, 0.565 }),
            ("overflow_astounded_483492 40-60 s", new[] { 0.189, 0.281, 3.233, 0.161, 0.475, 3.745, 0.132, 0.515, 3.712, 0.120, 0.775, 4.828, 0.110, 0.848, 5.149, 0.336, 0.153, 0.104, 0.136, 0.267, 0.562 }),
        },
        ["rain"] = new[]
        {
            ("street 0-20 s", new[] { 0.317, -0.001, 2.946, 0.310, -0.061, 2.785, 0.342, 0.114, 2.522, 0.365, 0.511, 2.468, 0.327, 0.971, 3.184, 0.851, 0.818, 0.768, 0.052, 0.034, 0.095 }),
            ("street 20-40 s", new[] { 0.163, -0.110, 2.762, 0.132, 0.128, 3.185, 0.112, 0.473, 3.996, 0.099, 0.574, 5.514, 0.091, 0.459, 4.412, 0.268, 0.096, 0.065, 0.054, 0.188, 0.649 }),
            ("street 40-60 s", new[] { 0.160, -0.139, 2.753, 0.130, 0.094, 3.115, 0.115, 0.603, 4.209, 0.108, 0.590, 3.830, 0.095, 0.544, 4.123, 0.267, 0.074, 0.058, 0.050, 0.185, 0.663 }),
            ("urban_street_60s 0-20 s", new[] { 0.232, 0.842, 4.275, 0.202, 0.918, 4.139, 0.161, 0.921, 4.029, 0.130, 1.128, 5.567, 0.118, 1.310, 6.181, 0.621, 0.482, 0.326, 0.096, 0.264, 0.368 }),
            ("urban_street_60s 20-40 s", new[] { 0.292, 0.300, 2.399, 0.215, 0.302, 2.629, 0.201, 0.199, 2.245, 0.192, 0.508, 2.867, 0.173, 1.087, 4.423, 0.726, 0.641, 0.647, 0.062, 0.090, 0.207 }),
            ("urban_street_60s 40-60 s", new[] { 0.302, 0.607, 2.673, 0.229, 0.500, 2.738, 0.229, 0.383, 2.262, 0.221, 0.598, 2.569, 0.192, 0.908, 3.361, 0.766, 0.699, 0.735, 0.046, 0.076, 0.175 }),
            ("Garden_rainfall 0-20 s", new[] { 0.165, 0.031, 3.135, 0.135, 0.293, 3.729, 0.103, 0.698, 5.570, 0.089, 1.378, 10.015, 0.113, 1.048, 6.898, 0.257, 0.121, 0.076, 0.041, 0.230, 0.683 }),
            ("Garden_rainfall 20-40 s", new[] { 0.164, -0.018, 3.000, 0.137, 0.641, 6.139, 0.105, 0.985, 8.044, 0.088, 1.447, 11.164, 0.113, 1.039, 6.940, 0.265, 0.138, 0.071, 0.038, 0.231, 0.686 }),
            ("Garden_rainfall 40-60 s", new[] { 0.167, 0.064, 3.230, 0.134, 0.396, 4.525, 0.099, 0.572, 5.179, 0.082, 1.036, 7.630, 0.107, 0.991, 6.360, 0.236, 0.114, 0.071, 0.033, 0.219, 0.706 }),
            ("calm_rain_60s 0-20 s", new[] { 0.189, 0.430, 3.777, 0.148, 0.692, 5.194, 0.115, 0.924, 6.240, 0.105, 1.399, 9.068, 0.107, 1.888, 11.505, 0.385, 0.229, 0.133, 0.074, 0.261, 0.611 }),
            ("calm_rain_60s 20-40 s", new[] { 0.187, 0.461, 3.925, 0.144, 0.569, 4.683, 0.110, 0.787, 5.443, 0.100, 1.174, 6.954, 0.102, 1.857, 11.524, 0.357, 0.194, 0.121, 0.055, 0.269, 0.641 }),
            ("calm_rain_60s 40-60 s", new[] { 0.181, 0.373, 3.637, 0.142, 0.423, 3.731, 0.110, 0.654, 4.795, 0.101, 1.230, 7.053, 0.102, 1.687, 10.095, 0.340, 0.181, 0.114, 0.064, 0.254, 0.644 }),
            ("Sound_of_light_rainfall 0-20 s", new[] { 0.224, 1.303, 8.299, 0.148, 0.604, 4.481, 0.146, 1.337, 7.595, 0.129, 1.249, 6.864, 0.115, 1.275, 7.962, 0.414, 0.115, 0.072, 0.064, 0.316, 0.552 }),
            ("Sound_of_light_rainfall 20-40 s", new[] { 0.256, 1.993, 11.876, 0.162, 0.835, 5.963, 0.163, 0.813, 5.352, 0.140, 0.941, 6.288, 0.125, 1.022, 7.051, 0.500, 0.156, -0.119, 0.081, 0.283, 0.492 }),
            ("Sound_of_light_rainfall 40-60 s", new[] { 0.234, 1.051, 6.248, 0.201, 1.325, 6.662, 0.190, 1.284, 6.104, 0.170, 1.125, 5.392, 0.152, 1.163, 5.930, 0.520, 0.141, -0.028, 0.106, 0.387, 0.429 }),
            ("Bourne_woods_rain_2020-05-10_0757 0-20 s", new[] { 0.165, 0.039, 3.452, 0.132, 0.239, 3.839, 0.110, 0.521, 4.545, 0.094, 0.558, 4.199, 0.089, 1.058, 7.854, 0.253, 0.122, 0.108, 0.044, 0.181, 0.611 }),
            ("Bourne_woods_rain_2020-05-10_0757 20-40 s", new[] { 0.171, 0.052, 2.870, 0.143, 0.392, 3.934, 0.130, 1.236, 9.488, 0.125, 1.588, 11.013, 0.122, 1.551, 10.066, 0.347, 0.244, 0.226, 0.085, 0.160, 0.486 }),
            ("carroof 0-20 s", new[] { 0.270, 0.196, 3.452, 0.262, 0.300, 3.589, 0.191, -0.289, 4.127, 0.136, -1.384, 5.114, 0.111, -1.369, 4.844, 0.737, 0.630, 0.543, 0.081, 0.186, 0.236 }),
            ("carroof 20-40 s", new[] { 0.206, 0.420, 3.421, 0.199, 0.614, 3.451, 0.128, 0.665, 4.265, 0.072, 0.169, 3.503, 0.058, -0.059, 3.182, 0.477, 0.330, 0.103, 0.081, 0.326, 0.520 }),
            ("carroof 40-60 s", new[] { 0.202, 0.286, 3.111, 0.187, 0.471, 3.207, 0.122, 0.523, 3.841, 0.069, 0.146, 3.323, 0.056, -0.007, 2.988, 0.430, 0.271, 0.051, 0.061, 0.297, 0.557 }),
            ("tileroof 0-20 s", new[] { 0.271, -0.466, 3.859, 0.267, -0.297, 4.042, 0.251, -0.295, 3.877, 0.223, -0.699, 3.778, 0.207, -0.949, 3.695, 0.791, 0.737, 0.719, 0.101, 0.074, 0.156 }),
            ("tileroof 20-40 s", new[] { 0.169, 0.105, 3.303, 0.160, 0.627, 4.340, 0.124, 0.884, 5.609, 0.084, 0.839, 6.648, 0.066, 0.345, 4.529, 0.383, 0.238, 0.081, 0.098, 0.207, 0.510 }),
            ("tileroof 40-60 s", new[] { 0.170, 0.040, 2.959, 0.163, 0.563, 3.892, 0.111, 0.714, 4.916, 0.069, 0.210, 3.755, 0.057, 0.143, 3.584, 0.326, 0.192, 0.039, 0.085, 0.199, 0.570 }),
            ("Rain_against_the_window 0-20 s", new[] { 0.256, 0.662, 3.423, 0.248, 0.870, 3.820, 0.184, 1.088, 4.919, 0.109, 1.364, 8.290, 0.056, 0.281, 4.460, 0.697, 0.615, 0.452, 0.133, 0.125, 0.292 }),
            ("Rain_against_the_window 20-40 s", new[] { 0.241, 0.378, 2.911, 0.241, 0.653, 3.431, 0.201, 0.972, 4.248, 0.130, 1.346, 6.398, 0.061, 0.553, 4.880, 0.703, 0.623, 0.463, 0.151, 0.125, 0.296 }),
            ("Rain_against_the_window 40-60 s", new[] { 0.213, 0.407, 3.258, 0.222, 0.902, 4.434, 0.200, 1.450, 6.712, 0.132, 1.776, 8.795, 0.063, 1.310, 15.666, 0.662, 0.575, 0.394, 0.309, 0.137, 0.321 }),
        },
    };
}
