using System;

namespace OpenFPS.Common.Hearing;

/// <summary>
/// Keeping a sound's tone when it plays at a level other than its real one.
///
/// The equal-loudness contours (ISO 226:2023) are steeper at low level: a sound played 20 dB quieter
/// than it is loses its bass (and a little of its top) to the ear, and one lifted toward the pivot gains
/// bass it never had. So a voice heard at loudness level <c>played</c> phon that is really
/// <c>real</c> phon is equalised by the difference between the two contours, normalised at 1 kHz:
///
///     EQ(f) = [L(f, played) - played] - [L(f, real) - real]
///
/// and each band is then as loud, against the others, as at the real level.
///
/// Realised as two second-order shelves (RBJ cookbook, slope 1): a low shelf at 200 Hz and a high
/// shelf at 10 kHz, their gains fitted to EQ(f) at the standard's 29 frequencies by least squares (the
/// two lowest weighted 0.3: they are where the fit is worst and least heard). Corners searched over
/// 60-315 Hz and 4-10 kHz; these fit best, within 1 dB for a 10-phon difference and 2 dB for 25.
///
/// Bounded: both loudness levels are held to 20-90 phon, the range the standard specifies the contours
/// for, so nothing is boosted toward the threshold of hearing and nothing loud is extrapolated; the
/// gains are held to +-<see cref="MaxShelfDb"/>.
/// </summary>
public static class LoudnessCompensation
{
    public const float LowShelfHz = 200f, HighShelfHz = 10000f, MaxShelfDb = 15f;

    /// <summary>The fit's weights per contour frequency.</summary>
    private static readonly float[] FitWeight = BuildWeights();

    /// <summary>The least-squares solve for the two gains, precomputed: gains = Solve * EQ.</summary>
    private static readonly float[,] Solve = BuildSolve();

    private static float[] BuildWeights()
    {
        var f = EqualLoudness.FrequenciesHz;
        var w = new float[f.Length];
        for (int i = 0; i < f.Length; i++) w[i] = f[i] < 31f ? 0.3f : 1f;
        return w;
    }

    private static float[,] BuildSolve()
    {
        var f = EqualLoudness.FrequenciesHz;
        int n = f.Length;
        // Basis: each shelf's response in dB at a gain of 1 dB, near-analogue (a 192 kHz design).
        var bl = new double[n];
        var bh = new double[n];
        for (int i = 0; i < n; i++)
        {
            bl[i] = ShelfResponseDb(f[i], 192000f, LowShelfHz, 1f, high: false);
            bh[i] = ShelfResponseDb(f[i], 192000f, HighShelfHz, 1f, high: true);
        }
        double a11 = 0, a12 = 0, a22 = 0;
        for (int i = 0; i < n; i++)
        {
            double w = FitWeight[i];
            a11 += w * bl[i] * bl[i]; a12 += w * bl[i] * bh[i]; a22 += w * bh[i] * bh[i];
        }
        double det = a11 * a22 - a12 * a12;
        var s = new float[2, n];
        for (int i = 0; i < n; i++)
        {
            double w = FitWeight[i];
            s[0, i] = (float)(w * (a22 * bl[i] - a12 * bh[i]) / det);
            s[1, i] = (float)(w * (-a12 * bl[i] + a11 * bh[i]) / det);
        }
        return s;
    }

    /// <summary>The contour difference EQ(f) at the 29 contour frequencies, dB, for a sound really at
    /// <paramref name="realPhon"/> heard at <paramref name="playedPhon"/>.</summary>
    public static void Target(float realPhon, float playedPhon, Span<float> perFrequencyDb)
    {
        float pr = Math.Clamp(realPhon, EqualLoudness.MinPhon, EqualLoudness.MaxPhon);
        float pp = Math.Clamp(playedPhon, EqualLoudness.MinPhon, EqualLoudness.MaxPhon);
        for (int i = 0; i < perFrequencyDb.Length; i++)
            perFrequencyDb[i] = pr == pp ? 0f : EqualLoudness.ShapeAt(i, pp) - EqualLoudness.ShapeAt(i, pr);
    }

    /// <summary>The two shelf gains, dB, for a sound really at <paramref name="realPhon"/> heard at
    /// <paramref name="playedPhon"/>.</summary>
    public static (float LowDb, float HighDb) Shelves(float realPhon, float playedPhon)
    {
        Span<float> d = stackalloc float[29];
        Target(realPhon, playedPhon, d);
        float low = 0, high = 0;
        for (int i = 0; i < d.Length; i++) { low += Solve[0, i] * d[i]; high += Solve[1, i] * d[i]; }
        return (Math.Clamp(low, -MaxShelfDb, MaxShelfDb), Math.Clamp(high, -MaxShelfDb, MaxShelfDb));
    }

    /// <summary>
    /// A shelf's coefficients, normalised (a0 = 1): RBJ "Audio EQ Cookbook", shelf slope S = 1.
    /// </summary>
    public static (float B0, float B1, float B2, float A1, float A2) Shelf(float sampleRate, float cornerHz, float gainDb, bool high)
    {
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w0 = 2.0 * Math.PI * Math.Min(cornerHz, 0.45 * sampleRate) / sampleRate;
        double c = Math.Cos(w0), sn = Math.Sin(w0);
        double alpha = sn / 2.0 * Math.Sqrt(2.0);     // S = 1: sqrt((A + 1/A)(1/S - 1) + 2) = sqrt(2)
        double sq = 2.0 * Math.Sqrt(a) * alpha;
        double b0, b1, b2, a0, a1, a2;
        if (!high)
        {
            b0 = a * ((a + 1) - (a - 1) * c + sq);
            b1 = 2 * a * ((a - 1) - (a + 1) * c);
            b2 = a * ((a + 1) - (a - 1) * c - sq);
            a0 = (a + 1) + (a - 1) * c + sq;
            a1 = -2 * ((a - 1) + (a + 1) * c);
            a2 = (a + 1) + (a - 1) * c - sq;
        }
        else
        {
            b0 = a * ((a + 1) + (a - 1) * c + sq);
            b1 = -2 * a * ((a - 1) + (a + 1) * c);
            b2 = a * ((a + 1) + (a - 1) * c - sq);
            a0 = (a + 1) - (a - 1) * c + sq;
            a1 = 2 * ((a - 1) - (a + 1) * c);
            a2 = (a + 1) - (a - 1) * c - sq;
        }
        return ((float)(b0 / a0), (float)(b1 / a0), (float)(b2 / a0), (float)(a1 / a0), (float)(a2 / a0));
    }

    /// <summary>A shelf's response at <paramref name="f"/>, dB.</summary>
    public static float ShelfResponseDb(float f, float sampleRate, float cornerHz, float gainDb, bool high)
    {
        var (b0, b1, b2, a1, a2) = Shelf(sampleRate, cornerHz, gainDb, high);
        return ResponseDb(f, sampleRate, b0, b1, b2, a1, a2);
    }

    /// <summary>The two shelves together at <paramref name="f"/>, dB: what a voice's compensation does there.</summary>
    public static float ResponseDb(float f, float sampleRate, float lowDb, float highDb)
        => ShelfResponseDb(f, sampleRate, LowShelfHz, lowDb, false) + ShelfResponseDb(f, sampleRate, HighShelfHz, highDb, true);

    private static float ResponseDb(float f, float sampleRate, float b0, float b1, float b2, float a1, float a2)
    {
        double w = 2 * Math.PI * f / sampleRate;
        var z1 = System.Numerics.Complex.FromPolarCoordinates(1, -w);
        var z2 = z1 * z1;
        var h = (b0 + b1 * z1 + b2 * z2) / (1 + a1 * z1 + a2 * z2);
        return (float)(20 * Math.Log10(Math.Max(h.Magnitude, 1e-12)));
    }
}
