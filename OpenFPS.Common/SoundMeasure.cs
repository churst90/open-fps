using System;
using System.Collections.Generic;

namespace OpenFPS.Common;

/// <summary>
/// The measurements docs/DOOR_TYPES.md was made with, in C#, so a render can be held to the same
/// numbers its recordings gave: octave band balance in the first 30 ms of an event, the level of the
/// loudest 2 ms frame, d20 (time to fall 20 dB from the peak), spectral peaks with their prominence
/// over the median of the 800 Hz around them, and a narrow-band T60. The Python tools in
/// inbox/door-types-2026-10-02/tools/ do the same on the recordings.
/// </summary>
public static class SoundMeasure
{
    /// <summary>The octave centres every band list here uses: 63 Hz to 16 kHz.</summary>
    public static readonly float[] Centres = { 63f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };

    /// <summary>Level of each 2 ms frame, dB (RMS, full scale 0).</summary>
    public static float[] Envelope(float[] x, int sr, float frame = 0.002f)
    {
        int n = Math.Max(1, (int)(frame * sr));
        var e = new float[x.Length / n];
        for (int f = 0; f < e.Length; f++)
        {
            double s = 0;
            for (int i = f * n; i < (f + 1) * n; i++) s += (double)x[i] * x[i];
            e[f] = (float)(10.0 * Math.Log10(s / n + 1e-20));
        }
        return e;
    }

    /// <summary>The loudest 2 ms frame between two times, dB.</summary>
    public static float PeakFrameDb(float[] x, int sr, float from = 0f, float to = float.MaxValue)
    {
        var e = Envelope(x, sr);
        int a = Math.Max(0, (int)(from / 0.002f)), b = Math.Min(e.Length, to >= 1e6f ? e.Length : (int)(to / 0.002f) + 1);
        float m = -200f;
        for (int i = a; i < b; i++) m = MathF.Max(m, e[i]);
        return m;
    }

    /// <summary>One octave of the signal: a third-order Butterworth band-pass an octave wide.</summary>
    public static float[] Band(float[] x, int sr, float centre)
    {
        var y = (float[])x.Clone();
        float lo = centre / MathF.Sqrt(2f), hi = MathF.Min(centre * MathF.Sqrt(2f), 0.47f * sr);
        CarDoor.ButterworthBandPass(y, lo, hi, sr);
        return y;
    }

    /// <summary>The signal between two edges, Hz (a third-order Butterworth band-pass).</summary>
    public static float[] BandPass(float[] x, int sr, float lo, float hi)
    {
        var y = (float[])x.Clone();
        CarDoor.ButterworthBandPass(y, lo, MathF.Min(hi, 0.47f * sr), sr);
        return y;
    }

    /// <summary>
    /// Band balance of an event: the energy in each octave from <paramref name="t0"/> for
    /// <paramref name="window"/> seconds, dB relative to the loudest band. A synth has no noise floor,
    /// so nothing is subtracted.
    /// </summary>
    public static float[] Bands(float[] x, int sr, float t0, float window = 0.03f)
    {
        var db = new float[Centres.Length];
        int a = Math.Max(0, (int)(t0 * sr)), b = Math.Min(x.Length, a + (int)(window * sr));
        for (int k = 0; k < Centres.Length; k++)
        {
            var y = Band(x, sr, Centres[k]);
            double s = 0;
            for (int i = a; i < b; i++) s += (double)y[i] * y[i];
            db[k] = (float)(10.0 * Math.Log10(s + 1e-20));
        }
        float m = float.MinValue;
        foreach (float v in db) m = MathF.Max(m, v);
        for (int k = 0; k < db.Length; k++) db[k] -= m;
        return db;
    }

    /// <summary>Band levels (energy, dB, not relative) over a span.</summary>
    public static float[] BandLevels(float[] x, int sr, float t0, float t1)
    {
        var db = new float[Centres.Length];
        int a = Math.Max(0, (int)(t0 * sr)), b = Math.Min(x.Length, (int)(t1 * sr));
        for (int k = 0; k < Centres.Length; k++)
        {
            var y = Band(x, sr, Centres[k]);
            double s = 0;
            for (int i = a; i < b; i++) s += (double)y[i] * y[i];
            db[k] = (float)(10.0 * Math.Log10(s / Math.Max(1, b - a) + 1e-20));
        }
        return db;
    }

    /// <summary>
    /// d20, seconds: from the loudest 2 ms frame in the 30 ms after <paramref name="t0"/> to the
    /// first frame 20 dB under it. Full band, or one octave when <paramref name="centre"/> is given.
    /// </summary>
    public static float D20(float[] x, int sr, float t0, float centre = 0f, float drop = 20f)
    {
        var e = Envelope(centre > 0f ? Band(x, sr, centre) : x, sr);
        int a = Math.Max(0, (int)(t0 / 0.002f));
        int j = a;
        for (int i = a; i < Math.Min(e.Length, a + 15); i++) if (e[i] > e[j]) j = i;
        for (int i = j; i < e.Length; i++) if (e[i] < e[j] - drop) return (i - j) * 0.002f;
        return (e.Length - j) * 0.002f;
    }

    /// <summary>A spectral peak: where, how far over the median of the 800 Hz around it, and its
    /// level against the spectrum's maximum.</summary>
    public readonly record struct Peak(float Hz, float ProminenceDb, float LevelDb);

    /// <summary>
    /// Peaks of the spectrum of <paramref name="window"/> seconds from <paramref name="t0"/> (Hann,
    /// 8192-point FFT), each at least <paramref name="over"/> dB over the median of the 800 Hz around
    /// it, strongest first. The doc's modes.py, at this sample rate.
    /// </summary>
    public static List<Peak> Peaks(float[] x, int sr, float t0, float window = 0.186f, float fmin = 60f,
                                   float fmax = 16000f, float over = 6f, int top = 15)
    {
        var p = Spectrum(x, sr, t0, window, out int nfft);
        var db = new float[p.Length];
        float max = float.MinValue;
        for (int k = 0; k < p.Length; k++) { db[k] = (float)(10.0 * Math.Log10(p[k] + 1e-20)); max = MathF.Max(max, db[k]); }
        float df = (float)sr / nfft;
        int w = Math.Max(3, (int)(400f / df));
        var res = new List<Peak>();
        var loc = new List<float>();
        for (int k = 2; k < db.Length - 2; k++)
        {
            float f = k * df;
            if (f < fmin || f > fmax) continue;
            if (!(db[k] >= db[k - 1] && db[k] >= db[k + 1] && db[k] >= db[k - 2] && db[k] >= db[k + 2])) continue;
            loc.Clear();
            for (int q = Math.Max(0, k - w); q <= Math.Min(db.Length - 1, k + w); q++) loc.Add(db[q]);
            loc.Sort();
            float med = loc[loc.Count / 2];
            if (db[k] - med >= over) res.Add(new Peak(f, db[k] - med, db[k] - max));
        }
        res.Sort((a, b) => b.ProminenceDb.CompareTo(a.ProminenceDb));
        if (res.Count > top) res.RemoveRange(top, res.Count - top);
        return res;
    }

    /// <summary>Power spectrum of one Hann window, zero-padded to 8192 points (or more if longer).</summary>
    public static double[] Spectrum(float[] x, int sr, float t0, float window, out int nfft)
    {
        int a = Math.Max(0, (int)(t0 * sr));
        int n = Math.Min((int)(window * sr), Math.Max(0, x.Length - a));
        nfft = 8192;
        while (nfft < n) nfft *= 2;
        var re = new double[nfft];
        var im = new double[nfft];
        for (int i = 0; i < n; i++) re[i] = x[a + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Math.Max(1, n - 1)));
        Fft(re, im);
        var p = new double[nfft / 2 + 1];
        for (int k = 0; k < p.Length; k++) p[k] = re[k] * re[k] + im[k] * im[k];
        return p;
    }

    /// <summary>T60 of one narrow band (f ± f/40), fitted from 3 to 25 dB under its peak. Seconds.</summary>
    public static float NarrowT60(float[] x, int sr, float t0, float hz)
    {
        float half = MathF.Max(15f, hz / 40f);
        var y = new float[Math.Min(x.Length, (int)((t0 + 2.5f) * sr)) - Math.Max(0, (int)((t0 - 0.01f) * sr))];
        Array.Copy(x, Math.Max(0, (int)((t0 - 0.01f) * sr)), y, 0, y.Length);
        CarDoor.ButterworthBandPass(y, hz - half, hz + half, sr);
        var e = Envelope(y, sr);
        int j = 0;
        for (int i = 0; i < Math.Min(e.Length, 60); i++) if (e[i] > e[j]) j = i;
        var pts = new List<(float T, float Db)>();
        for (int k = j; k < e.Length; k++)
        {
            if (e[k] < e[j] - 25f) break;
            if (e[k] <= e[j] - 3f) pts.Add((k * 0.002f, e[k]));
        }
        if (pts.Count < 3) return float.NaN;
        double mx = 0, my = 0;
        foreach (var (t, d) in pts) { mx += t; my += d; }
        mx /= pts.Count; my /= pts.Count;
        double sxy = 0, sxx = 0;
        foreach (var (t, d) in pts) { sxy += (t - mx) * (d - my); sxx += (t - mx) * (t - mx); }
        double slope = sxy / Math.Max(1e-12, sxx);
        return slope < 0 ? (float)(-60.0 / slope) : float.PositiveInfinity;
    }

    /// <summary>The strongest spectral line in a band over a span (Hann, 4096 points): frequency, Hz.</summary>
    public static float StrongestHz(float[] x, int sr, float t0, float fmin, float fmax, float window = 0.085f)
    {
        var p = Spectrum(x, sr, t0, window, out int nfft);
        float df = (float)sr / nfft;
        int k0 = (int)(fmin / df), k1 = Math.Min(p.Length - 1, (int)(fmax / df));
        int best = k0;
        for (int k = k0; k <= k1; k++) if (p[k] > p[best]) best = k;
        return best * df;
    }

    /// <summary>
    /// A dry render as a measuring microphone in a small room would have it: the direct sound and a
    /// diffuse tail of the given T60 at the given C50 (energy 0-50 ms over 50-400 ms). A recording's
    /// lines are read over its room's tail; a dry render's would be read over nothing, and every line
    /// in it would look tens of decibels more prominent than the same line in a room. Deterministic.
    /// </summary>
    public static float[] ThroughRoom(float[] x, int sr, float t60, float c50Db)
    {
        static double E(double a, double b, double t60) => t60 / 13.8 * (Math.Exp(-13.8 * a / t60) - Math.Exp(-13.8 * b / t60));
        double g2 = 1.0 / (Math.Pow(10, c50Db / 10) * E(0.05, 0.4, t60) - E(0.003, 0.05, t60));
        int n = (int)(t60 * 1.2f * sr);
        var ir = new double[n];
        var rng = new Random(5);
        ir[0] = 1.0;
        for (int i = (int)(0.003 * sr); i < n; i++)
            ir[i] = (rng.NextDouble() * 2 - 1) * Math.Sqrt(3.0 * g2 / sr) * Math.Exp(-6.9 * i / (double)sr / t60);
        int size = 1;
        while (size < x.Length + n) size <<= 1;
        var ar = new double[size]; var ai = new double[size]; var br = new double[size]; var bi = new double[size];
        for (int i = 0; i < x.Length; i++) ar[i] = x[i];
        for (int i = 0; i < n; i++) br[i] = ir[i];
        Fft(ar, ai); Fft(br, bi);
        for (int i = 0; i < size; i++)
        {
            double r = ar[i] * br[i] - ai[i] * bi[i], im = ar[i] * bi[i] + ai[i] * br[i];
            ar[i] = r; ai[i] = -im;           // conjugated, so a forward transform inverts
        }
        Fft(ar, ai);
        var y = new float[x.Length + n];
        for (int i = 0; i < y.Length; i++) y[i] = (float)(ar[i] / size);
        return y;
    }

    /// <summary>In-place radix-2 complex FFT.</summary>
    public static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j |= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int u = i + k, v = i + k + len / 2;
                    double tr = re[v] * cr - im[v] * ci, ti = re[v] * ci + im[v] * cr;
                    re[v] = re[u] - tr; im[v] = im[u] - ti;
                    re[u] += tr; im[u] += ti;
                    double nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }
}
