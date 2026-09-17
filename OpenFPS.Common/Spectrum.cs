using System;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// What is in a buffer, by frequency band.
///
/// It exists because a listening test cannot be automated and a band balance can. "It does not sound
/// right" is where every audio fault starts and it is not something a test can hold; "it has eighteen
/// decibels too much between thirty and sixty hertz" is, and it is usually the same fault said
/// precisely.
///
/// The bands are the ones the ear roughly works in — octave-ish, from where hearing starts to where
/// it stops — and the result is NORMALISED, so what is compared is the SHAPE of a sound and not how
/// loud somebody recorded it. Loudness is a separate question with its own answer (see
/// <see cref="Loudness"/>); this is about whether a thing has the right amount of bass.
///
/// A warning worth writing down, because it cost a wrong conclusion once: a measurement that has not
/// itself been checked is not evidence. The first version of this analysis decimated inside its own
/// transform to go faster, which aliases everything above an eighth of the sample rate back down the
/// spectrum, and it produced a confident and completely wrong diagnosis of the footstep synthesiser.
/// Hence a real FFT, a window, and no shortcuts.
/// </summary>
public static class Spectrum
{
    /// <summary>The band edges, Hz. Nine bands from the bottom of hearing to the top.</summary>
    public static readonly float[] BandEdges =
        { 30f, 60f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };

    public static int BandCount => BandEdges.Length - 1;

    /// <summary>A readable name for each band, for a report or an assertion message.</summary>
    public static string BandName(int i) => $"{BandEdges[i]:F0}-{BandEdges[i + 1]:F0} Hz";

    /// <summary>
    /// The energy in each band, as decibels relative to the TOTAL across all of them.
    ///
    /// Relative, so two recordings made at different levels can be compared at all, and so the
    /// question being asked is "does this have the right shape" rather than "is this the right
    /// volume". The two are genuinely separate and conflating them is how a correct level ends up
    /// being blamed for a wrong timbre.
    /// </summary>
    public static float[] BandsDb(ReadOnlySpan<float> samples, int sampleRate, int fftSize = 4096)
    {
        var energy = BandEnergy(samples, sampleRate, fftSize);
        double total = 0;
        foreach (float e in energy) total += e;
        if (total <= 0) total = 1e-12;

        var db = new float[energy.Length];
        for (int i = 0; i < energy.Length; i++)
            db[i] = 10f * MathF.Log10(MathF.Max(energy[i] / (float)total, 1e-9f));
        return db;
    }

    /// <summary>Raw energy per band, un-normalised.</summary>
    public static float[] BandEnergy(ReadOnlySpan<float> samples, int sampleRate, int fftSize = 4096)
    {
        int n = NextPowerOfTwo(fftSize);
        var buf = new Complex[n];
        int take = Math.Min(n, samples.Length);

        // Hann. Without a window, a transient sitting anywhere but the exact centre smears across
        // every bin and the answer is the window's spectrum rather than the sound's.
        for (int i = 0; i < take; i++)
        {
            float w = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (n - 1));
            buf[i] = new Complex(samples[i] * w, 0);
        }
        for (int i = take; i < n; i++) buf[i] = Complex.Zero;

        Fft(buf);

        var energy = new float[BandCount];
        for (int b = 0; b < BandCount; b++)
        {
            int k0 = (int)(BandEdges[b] * n / sampleRate);
            int k1 = Math.Min(n / 2, Math.Max(k0 + 1, (int)(BandEdges[b + 1] * n / sampleRate)));
            double acc = 0;
            for (int k = k0; k < k1; k++)
            {
                double re = buf[k].Real, im = buf[k].Imaginary;
                acc += re * re + im * im;
            }
            energy[b] = (float)acc;
        }
        return energy;
    }

    /// <summary>
    /// The average band shape over several windows — for a sound made of repeated events.
    ///
    /// One footstep is one sample of a random process: the grit it happens to land on varies, and a
    /// spectrum taken from a single step says as much about that step's luck as about the model.
    /// Averaging several is the difference between measuring the thing and measuring one instance
    /// of it.
    /// </summary>
    public static float[] AverageBandsDb(ReadOnlySpan<float> samples, int sampleRate,
                                         ReadOnlySpan<int> startOffsets, int fftSize = 4096)
    {
        var acc = new double[BandCount];
        int used = 0;
        foreach (int start in startOffsets)
        {
            if (start < 0 || start >= samples.Length) continue;
            int len = Math.Min(fftSize, samples.Length - start);
            var e = BandEnergy(samples.Slice(start, len), sampleRate, fftSize);
            for (int i = 0; i < BandCount; i++) acc[i] += e[i];
            used++;
        }
        if (used == 0) return new float[BandCount];

        double total = 0;
        foreach (double v in acc) total += v;
        if (total <= 0) total = 1e-12;

        var db = new float[BandCount];
        for (int i = 0; i < BandCount; i++)
            db[i] = 10f * MathF.Log10(MathF.Max((float)(acc[i] / total), 1e-9f));
        return db;
    }

    /// <summary>
    /// Where the events are in a buffer, by short-term energy — the onsets a band average wants.
    ///
    /// The same rule the footstep splitter uses on a recording: rising through a fraction of the
    /// loudest thing present, with a refractory gap so one event is not counted as several.
    /// </summary>
    public static int[] Onsets(ReadOnlySpan<float> samples, int sampleRate,
                               float threshold = 0.25f, float minGapSeconds = 0.12f, int max = 32)
    {
        int hop = Math.Max(1, sampleRate / 200);           // 5 ms
        int win = Math.Max(hop, sampleRate / 100);         // 10 ms
        int frames = Math.Max(0, (samples.Length - win) / hop);
        if (frames <= 1) return Array.Empty<int>();

        var env = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double acc = 0;
            int at = f * hop;
            for (int i = 0; i < win; i++) { float v = samples[at + i]; acc += v * v; }
            env[f] = (float)Math.Sqrt(acc / win);
        }

        float peak = 0f;
        foreach (float v in env) if (v > peak) peak = v;
        if (peak <= 0f) return Array.Empty<int>();

        float level = peak * threshold;
        int minGap = (int)(minGapSeconds * sampleRate / hop);
        var found = new System.Collections.Generic.List<int>();
        int last = -minGap * 2;
        for (int f = 1; f < frames && found.Count < max; f++)
        {
            if (env[f] > level && env[f - 1] <= level && f - last >= minGap)
            {
                found.Add(f * hop);
                last = f;
            }
        }
        return found.ToArray();
    }

    private static int NextPowerOfTwo(int v)
    {
        int n = 1;
        while (n < v) n <<= 1;
        return n;
    }

    /// <summary>Iterative radix-2 Cooley-Tukey, in place. Length must be a power of two.</summary>
    private static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            var step = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[i + k];
                    var t = a[i + k + len / 2] * w;
                    a[i + k] = u + t;
                    a[i + k + len / 2] = u - t;
                    w *= step;
                }
            }
        }
    }
}
