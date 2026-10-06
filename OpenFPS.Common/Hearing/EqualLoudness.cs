using System;

namespace OpenFPS.Common.Hearing;

/// <summary>
/// The normal equal-loudness-level contours, ISO 226:2023: the level a pure tone needs at each
/// frequency to sound as loud as a 1 kHz tone of a given level (its loudness level, phon), and the
/// threshold of hearing.
///
/// Formula (1) of the standard, with the parameters of its table 1 at the 29 preferred frequencies
/// 20 Hz to 12.5 kHz (alpha_f the exponent of loudness perception, L_U the magnitude of the linear
/// transfer function normalised at 1 kHz, T_f the threshold). The values were taken from two
/// independent implementations that agree to the last digit (jmrplens/phonometry and
/// ronitsingh10/FineTune). The contours are specified from 20 to 90 phon below 4 kHz and to 80 phon
/// from 5 to 12.5 kHz; outside that they are extrapolations, which is why the compensation that uses
/// them holds its loudness levels to <see cref="MinPhon"/>..<see cref="MaxPhon"/>.
///
/// The standard defines no interpolation between its frequencies; this one interpolates the result
/// linearly in log frequency, for a filter fit that is read at those 29 frequencies anyway.
/// </summary>
public static class EqualLoudness
{
    /// <summary>The 29 preferred frequencies of table 1, Hz.</summary>
    public static ReadOnlySpan<float> FrequenciesHz => F;

    /// <summary>The range of loudness levels the standard specifies the contours for, phon.</summary>
    public const float MinPhon = 20f, MaxPhon = 90f;

    private static readonly float[] F =
    {
        20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f, 200f, 250f, 315f, 400f, 500f, 630f, 800f,
        1000f, 1250f, 1600f, 2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f,
    };
    private static readonly float[] AlphaF =
    {
        0.635f, 0.602f, 0.569f, 0.537f, 0.509f, 0.482f, 0.456f, 0.433f, 0.412f, 0.391f, 0.373f, 0.357f,
        0.343f, 0.330f, 0.320f, 0.311f, 0.303f, 0.300f, 0.295f, 0.292f, 0.290f, 0.290f, 0.289f, 0.289f,
        0.289f, 0.293f, 0.303f, 0.323f, 0.354f,
    };
    private static readonly float[] Lu =
    {
        -31.5f, -27.2f, -23.1f, -19.3f, -16.1f, -13.1f, -10.4f, -8.2f, -6.3f, -4.6f, -3.2f, -2.1f, -1.2f,
        -0.5f, 0.0f, 0.4f, 0.5f, 0.0f, -2.7f, -4.2f, -1.2f, 1.4f, 2.3f, 1.0f, -2.3f, -7.2f, -11.2f, -10.9f, -3.5f,
    };
    private static readonly float[] Tf =
    {
        78.1f, 68.7f, 59.5f, 51.1f, 44.0f, 37.5f, 31.5f, 26.5f, 22.1f, 17.9f, 14.4f, 11.4f, 8.6f, 6.2f,
        4.4f, 3.0f, 2.2f, 2.4f, 3.5f, 1.7f, -1.3f, -4.2f, -6.0f, -5.4f, -1.5f, 6.0f, 12.6f, 13.9f, 12.3f,
    };

    /// <summary>The reference exponent and the squared reference pressure as formula (1) prints them.</summary>
    private const double AlphaR = 0.300, P0Squared = 4.0e-10;

    /// <summary>Formula (1): the level, dB SPL, of a tone at table frequency <paramref name="index"/>
    /// that has loudness level <paramref name="phon"/>.</summary>
    public static float SplAt(int index, float phon)
    {
        double a = AlphaF[index], lu = Lu[index], tf = Tf[index];
        double term = Math.Pow(P0Squared, AlphaR - a) * (Math.Pow(10.0, AlphaR * phon / 10.0) - Math.Pow(10.0, AlphaR * 2.4 / 10.0))
                    + Math.Pow(10.0, a * (tf + lu) / 10.0);
        return (float)(10.0 / a * Math.Log10(term) - lu);
    }

    /// <summary>Formula (2): the loudness level, phon, of a tone at table frequency
    /// <paramref name="index"/> and level <paramref name="spl"/>.</summary>
    public static float PhonAt(int index, float spl)
    {
        double a = AlphaF[index], lu = Lu[index], tf = Tf[index];
        double b = (Math.Pow(10.0, a * (spl + lu) / 10.0) - Math.Pow(10.0, a * (tf + lu) / 10.0))
                   / Math.Pow(P0Squared, AlphaR - a) + Math.Pow(10.0, AlphaR * 2.4 / 10.0);
        return (float)(100.0 / 3.0 * Math.Log10(Math.Max(b, 1e-30)));
    }

    /// <summary>The threshold of hearing at table frequency <paramref name="index"/>, dB SPL.</summary>
    public static float ThresholdAt(int index) => Tf[index];

    /// <summary>
    /// How far above the 1 kHz level a tone at table frequency <paramref name="index"/> must be to be
    /// as loud, at loudness level <paramref name="phon"/>: the contour's shape, dB. Zero at 1 kHz.
    /// </summary>
    public static float ShapeAt(int index, float phon) => SplAt(index, phon) - phon;

    /// <summary>The level of a tone of any frequency in 20 Hz-12.5 kHz with this loudness level,
    /// interpolated in log frequency between the table's frequencies.</summary>
    public static float Spl(float frequencyHz, float phon)
    {
        float f = Math.Clamp(frequencyHz, F[0], F[^1]);
        int i = 0;
        while (i < F.Length - 2 && F[i + 1] < f) i++;
        float t = MathF.Log(f / F[i]) / MathF.Log(F[i + 1] / F[i]);
        return SplAt(i, phon) * (1f - t) + SplAt(i + 1, phon) * t;
    }
}
