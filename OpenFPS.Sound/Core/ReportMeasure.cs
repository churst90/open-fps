using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>What one shot measured: the numbers docs/GUNFIRE.md compares.</summary>
/// <param name="PositivePhaseMs">From the onset to the waveform's first crossing back through zero.</param>
/// <param name="Down10Ms">From the loudest 0.25 ms window to the first one 10 dB under it.</param>
/// <param name="Down20Ms">The same, 20 dB under.</param>
/// <param name="Down30Ms">The same, 30 dB under.</param>
/// <param name="BandsDb">Octave bands 125 Hz to 16 kHz over the event, dB re the loudest band.</param>
/// <param name="CentroidHz">The spectral centroid below 20 kHz over the event.</param>
/// <param name="NegativeRatio">The deepest point of the negative phase against the positive peak.</param>
public sealed record ReportMeasurement(float PositivePhaseMs, float Down10Ms, float Down20Ms, float Down30Ms,
                                       float[] BandsDb, float CentroidHz, float NegativeRatio = 0f);

/// <summary>
/// Measures a gunshot as the NIJ recordings were measured (inbox/gunfire-357-2026-10-02/nij.py), so a
/// render and a recording are read by the same ruler. The band window is flat-topped, 1 ms before the
/// onset to 20 ms after: a Hann window from the onset is nearly zero over the first 3 ms, where almost
/// all of a shot is, and once measured its tail instead (docs/GUNFIRE.md).
/// </summary>
public static class ReportMeasure
{
    /// <summary>The octave centres the recordings were measured at, Hz.</summary>
    public static readonly float[] OctaveCentres = { 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };

    public static ReportMeasurement Measure(ReadOnlySpan<float> x, int sampleRate)
    {
        int sr = sampleRate;
        float pk = 0f;
        foreach (float v in x) pk = MathF.Max(pk, MathF.Abs(v));
        if (pk <= 0f) return new ReportMeasurement(0, 0, 0, 0, new float[OctaveCentres.Length], 0);

        int o = 0;
        while (o < x.Length && MathF.Abs(x[o]) <= 0.1f * pk) o++;
        int k = o;
        while (k > Math.Max(0, o - (int)(0.003 * sr)) && MathF.Abs(x[k]) > 0.01f * pk) k--;

        // Envelope in 0.25 ms windows over 60 ms.
        int h = Math.Max(1, (int)(0.00025 * sr));
        int nw = Math.Min((int)(0.06 * sr) / h, (x.Length - k) / h);
        var env = new float[Math.Max(1, nw)];
        for (int i = 0; i < nw; i++)
        {
            double e = 0;
            for (int j = 0; j < h; j++) { float v = x[k + i * h + j]; e += v * v; }
            env[i] = (float)(10 * Math.Log10(e / h + 1e-24));
        }
        int p = 0;
        for (int i = 1; i < Math.Min(40, env.Length); i++) if (env[i] > env[p]) p = i;
        float Down(float db)
        {
            for (int i = p; i < env.Length; i++) if (env[i] < env[p] - db) return (i - p) * 0.25f;
            return float.NaN;
        }

        // Positive phase: the first big excursion's sign, held until it crosses zero.
        int s0 = MathF.Sign(x[o]), q = o;
        while (q < Math.Min(x.Length, o + (int)(0.005 * sr)) && MathF.Sign(x[q]) == s0) q++;
        float pos = (q - k) * 1000f / sr;
        float up = 0f, down = 0f;
        for (int i = o; i < Math.Min(x.Length, o + (int)(0.003 * sr)); i++)
        {
            up = MathF.Max(up, x[i] * s0);
            if (i >= q) down = MathF.Max(down, -x[i] * s0);
        }

        var (bands, centroid) = Bands(x, k, sr);
        return new ReportMeasurement(pos, Down(10f), Down(20f), Down(30f), bands, centroid, down / MathF.Max(1e-12f, up));
    }

    /// <summary>Octave-band energies from <paramref name="onset"/> - 1 ms to + 20 ms, dB re the loudest.</summary>
    public static (float[] BandsDb, float CentroidHz) Bands(ReadOnlySpan<float> x, int onset, int sampleRate)
    {
        int sr = sampleRate;
        int a = Math.Max(0, onset - (int)(0.001 * sr));
        int b = Math.Min(x.Length, onset + (int)(0.020 * sr));
        int len = b - a, t = (int)(0.0005 * sr);
        int n = 1;
        while (n < Math.Max(len, sr / 3)) n <<= 1;       // 32768 at 96 kHz, 16384 at 48: about 3 Hz a bin
        var buf = new Complex[n];
        for (int i = 0; i < len; i++)
        {
            double w = 1.0;
            if (i < t) w = 0.5 - 0.5 * Math.Cos(Math.PI * i / t);
            else if (i >= len - t) w = 0.5 - 0.5 * Math.Cos(Math.PI * (len - 1 - i) / t);
            buf[i] = new Complex(x[a + i] * w, 0);
        }
        Spectrum.Fft(buf);
        var e = new double[OctaveCentres.Length];
        double num = 0, den = 0;
        for (int kk = 1; kk < n / 2; kk++)
        {
            double f = kk * (double)sr / n;
            double m = buf[kk].Real * buf[kk].Real + buf[kk].Imaginary * buf[kk].Imaginary;
            if (f < 20000) { num += f * m; den += m; }
            for (int c = 0; c < OctaveCentres.Length; c++)
                if (f >= OctaveCentres[c] / Math.Sqrt(2) && f < OctaveCentres[c] * Math.Sqrt(2)) e[c] += m;
        }
        double top = 1e-30;
        foreach (double v in e) top = Math.Max(top, v);
        var db = new float[e.Length];
        for (int c = 0; c < e.Length; c++) db[c] = (float)(10 * Math.Log10(e[c] / top + 1e-30));
        return (db, (float)(num / Math.Max(1e-30, den)));
    }

    /// <summary>
    /// A dry source carried to a listener the way the NIJ recordings were made: across open hard
    /// ground, so 1/r, ISO 9613-1 air (20 C, 50 %) and one ground reflection with source and
    /// microphone 1.5 m up and a coefficient of 0.8. The same path as the lab's --gun-spec.
    /// </summary>
    public static float[] Propagate(float[] source, int sampleRate, float metres)
    {
        int sr = sampleRate;
        int pad = sr / 25;                                  // the DFT is circular: pad, then trim
        int n = 1;
        while (n < source.Length + 2 * pad) n <<= 1;
        var buf = new Complex[n];
        for (int i = 0; i < source.Length; i++) buf[pad + i] = new Complex(source[i], 0);
        Spectrum.Fft(buf);
        for (int k = 0; k <= n / 2; k++)
        {
            float f = k * (float)sr / n;
            double g = Math.Pow(10, -AudioPhysics.AirAttenuationDbPerMetre(f, 20f, 0.5f) * metres / 20);
            buf[k] *= g;
            if (k > 0 && k < n / 2) buf[n - k] *= g;
        }
        // Inverse by conjugation.
        for (int k = 0; k < n; k++) buf[k] = Complex.Conjugate(buf[k]);
        Spectrum.Fft(buf);
        var direct = new float[source.Length];
        for (int i = 0; i < source.Length; i++) direct[i] = (float)(buf[pad + i].Real / n);

        float hs = 1.5f, hr = 1.5f;
        float reflected = MathF.Sqrt(metres * metres + (hs + hr) * (hs + hr));
        int lag = (int)MathF.Round((reflected - metres) / 343f * sr);
        float gr = 0.8f * metres / reflected;
        var y = new float[source.Length];
        for (int i = 0; i < y.Length; i++)
            y[i] = (direct[i] + (i >= lag ? gr * direct[i - lag] : 0f)) / metres;
        return y;
    }

    /// <summary>Energy of a buffer, dB re one second of a full-scale square wave.</summary>
    public static float EnergyDb(ReadOnlySpan<float> x, int sampleRate)
    {
        double e = 0;
        foreach (float v in x) e += (double)v * v;
        return (float)(10 * Math.Log10(e / sampleRate + 1e-30));
    }
}
