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

    private static int NextPowerOfTwo(int v)
    {
        int n = 1;
        while (n < v) n <<= 1;
        return n;
    }

    /// <summary>
    /// How much of a sound is tone and how much noise, over <paramref name="loHz"/>-<paramref name="hiHz"/>:
    /// its spectral FLATNESS (the geometric over the arithmetic mean of the power spectrum, the Wiener
    /// entropy: 0 for a pure tone, 0.56 for white noise's periodogram, lower for coloured noise), and its
    /// TONE, how far the strongest bin stands over the median of its own third of an octave either side,
    /// dB (about 8-10 dB for noise, which has chance peaks; 25 dB and more for a line). Both are taken
    /// frame by frame (Hann, 1024 points, half overlapped) over the frames within 20 dB of the loudest,
    /// flatness weighted by the frame's energy, tone averaged in dB.
    /// </summary>
    public static (float Flatness, float ToneDb) Tonality(ReadOnlySpan<float> pcm, int sampleRate, float loHz = 500f, float hiHz = 16000f, int frame = 1024)
    {
        int hop = frame / 2;
        if (pcm.Length < frame) return (0f, 0f);
        int lo = Math.Max(1, (int)(loHz * frame / sampleRate)), hi = Math.Min(frame / 2 - 1, (int)(hiHz * frame / sampleRate));
        var frames = new System.Collections.Generic.List<(double Energy, double Flat, double Tone)>();
        var buf = new Complex[frame];
        var power = new double[frame / 2];
        var window = new double[frame];
        for (int i = 0; i < frame; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / frame);
        var scratch = new double[256];
        for (int start = 0; start + frame <= pcm.Length; start += hop)
        {
            for (int i = 0; i < frame; i++) buf[i] = new Complex(pcm[start + i] * window[i], 0);
            Fft(buf);
            double energy = 0, logSum = 0;
            for (int k = lo; k <= hi; k++)
            {
                power[k] = buf[k].Real * buf[k].Real + buf[k].Imaginary * buf[k].Imaginary + 1e-30;
                energy += power[k];
                logSum += Math.Log(power[k]);
            }
            int count = hi - lo + 1;
            double flat = Math.Exp(logSum / count) / (energy / count);
            double tone = 0;
            for (int k = lo; k <= hi; k++)
            {
                int a = Math.Max(lo, (int)(k / 1.26)), b = Math.Min(hi, (int)Math.Ceiling(k * 1.26));
                if (b - a < 8) { a = Math.Max(lo, k - 4); b = Math.Min(hi, k + 4); }
                int m = Math.Min(scratch.Length, b - a + 1);
                for (int j = 0; j < m; j++) scratch[j] = power[a + j];
                Array.Sort(scratch, 0, m);
                double median = scratch[m / 2];
                tone = Math.Max(tone, power[k] / median);
            }
            frames.Add((energy, flat, tone));
        }
        double loudest = 0;
        foreach (var f in frames) loudest = Math.Max(loudest, f.Energy);
        double wSum = 0, flatSum = 0, toneSum = 0; int kept = 0;
        foreach (var f in frames)
        {
            if (f.Energy < loudest * 0.01) continue;
            wSum += f.Energy; flatSum += f.Energy * f.Flat;
            toneSum += 10 * Math.Log10(f.Tone); kept++;
        }
        return kept == 0 ? (0f, 0f) : ((float)(flatSum / wSum), (float)(toneSum / kept));
    }

    /// <summary>Iterative radix-2 Cooley-Tukey, in place. Length must be a power of two.</summary>
    public static void Fft(Complex[] a)
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
