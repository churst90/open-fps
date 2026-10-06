using System;
using System.Collections.Generic;
using System.Linq;

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
    /// The summary features of the reference recordings, measured with tools/texture_stats.py json
    /// (30 s from the start of each file; the files are in ~/openfps-scratch-archive, listed with their
    /// sources in each refs/SOURCES.txt). Recordings are a yardstick and are never played in the game;
    /// their numbers are written here so the tests can hold a model inside their spread without them.
    /// </summary>
    public static IReadOnlyDictionary<string, double>[] References(string texture) => texture switch
    {
        _ => throw new ArgumentException(texture),
    };
}
