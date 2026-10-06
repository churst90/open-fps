using System;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

/// <summary>
/// A gain per octave, 63 Hz to 8 kHz: a low shelf, six one-octave peaking sections and a high shelf
/// (the RBJ cookbook's), fitted so the cascade's mean power over each octave is the gain asked for.
/// Immutable once designed: a voice swaps one in whole. Each user keeps its own <see cref="State"/>.
/// </summary>
public sealed class OctaveEq
{
    public static readonly float[] Centres = { 63f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f };
    public const int Bands = 8;

    private readonly float[] _b0 = new float[Bands], _b1 = new float[Bands], _b2 = new float[Bands], _a1 = new float[Bands], _a2 = new float[Bands];

    /// <summary>What the design asked for, dB per octave.</summary>
    public readonly float[] TargetDb;

    /// <summary>Two delay elements per section.</summary>
    public static float[] State() => new float[2 * Bands];

    private OctaveEq(float[] target) { TargetDb = target; }

    /// <summary>The equaliser that puts <paramref name="gainsDb"/> (one per octave of
    /// <see cref="Centres"/>) on a signal at <paramref name="rate"/>.</summary>
    public static OctaveEq Design(ReadOnlySpan<float> gainsDb, float rate)
    {
        var target = gainsDb.ToArray();
        var eq = new OctaveEq(target);
        // The sections overlap, so each is set, the whole is measured over each octave, and each section
        // is moved by what is still missing in its own octave. A few rounds settle it to hundredths.
        var g = (float[])target.Clone();
        for (int round = 0; round < 8; round++)
        {
            eq.Set(g, rate);
            for (int k = 0; k < Bands; k++)
            {
                float miss = target[k] - eq.OctaveDb(k, rate);
                g[k] = Math.Clamp(g[k] + 0.9f * miss, -24f, 24f);
            }
        }
        eq.Set(g, rate);
        return eq;
    }

    private void Set(float[] g, float rate)
    {
        for (int k = 0; k < Bands; k++)
        {
            float A = MathF.Pow(10f, g[k] / 40f);
            double b0, b1, b2, a0, a1, a2;
            if (k == 0 || k == Bands - 1)
            {
                // Shelves at the outer octaves' inner edges, slope 1.
                float f0 = k == 0 ? Centres[0] * 1.41421356f : Centres[Bands - 2] * 1.41421356f;
                double w0 = 2 * Math.PI * Math.Min(f0, 0.45 * rate) / rate, cw = Math.Cos(w0);
                double alpha = Math.Sin(w0) / 2 * Math.Sqrt(2), sa = 2 * Math.Sqrt(A) * alpha;
                if (k == 0)
                {
                    b0 = A * ((A + 1) - (A - 1) * cw + sa); b1 = 2 * A * ((A - 1) - (A + 1) * cw); b2 = A * ((A + 1) - (A - 1) * cw - sa);
                    a0 = (A + 1) + (A - 1) * cw + sa; a1 = -2 * ((A - 1) + (A + 1) * cw); a2 = (A + 1) + (A - 1) * cw - sa;
                }
                else
                {
                    b0 = A * ((A + 1) + (A - 1) * cw + sa); b1 = -2 * A * ((A - 1) + (A + 1) * cw); b2 = A * ((A + 1) + (A - 1) * cw - sa);
                    a0 = (A + 1) - (A - 1) * cw + sa; a1 = 2 * ((A - 1) - (A + 1) * cw); a2 = (A + 1) - (A - 1) * cw - sa;
                }
            }
            else
            {
                // An octave wide: Q = 1 / (2 sinh(ln 2 / 2)).
                const double Q = 1.41421356;
                double w0 = 2 * Math.PI * Math.Min(Centres[k], 0.45 * rate) / rate, cw = Math.Cos(w0);
                double alpha = Math.Sin(w0) / (2 * Q);
                b0 = 1 + alpha * A; b1 = -2 * cw; b2 = 1 - alpha * A;
                a0 = 1 + alpha / A; a1 = -2 * cw; a2 = 1 - alpha / A;
            }
            _b0[k] = (float)(b0 / a0); _b1[k] = (float)(b1 / a0); _b2[k] = (float)(b2 / a0);
            _a1[k] = (float)(a1 / a0); _a2[k] = (float)(a2 / a0);
        }
    }

    /// <summary>The cascade's mean power gain over octave <paramref name="band"/>, dB: what an octave-band
    /// level measures, which is what the gains are asked for as (HrtfOctaves measures the same way).</summary>
    public float OctaveDb(int band, float rate)
    {
        const int Points = 24;
        double lo = Centres[band] / Math.Sqrt(2), hi = Math.Min(Centres[band] * Math.Sqrt(2), 0.49 * rate), sum = 0;
        for (int j = 0; j < Points; j++)
        {
            float db = ResponseDb((float)(lo + (hi - lo) * (j + 0.5) / Points), rate);
            sum += Math.Pow(10, db / 10);
        }
        return (float)(10 * Math.Log10(sum / Points));
    }

    /// <summary>The cascade's gain at <paramref name="hz"/>, dB.</summary>
    public float ResponseDb(float hz, float rate)
    {
        double w = 2 * Math.PI * hz / rate;
        var z1 = new System.Numerics.Complex(Math.Cos(-w), Math.Sin(-w));
        var z2 = z1 * z1;
        double mag = 1;
        for (int k = 0; k < Bands; k++)
        {
            var num = _b0[k] + _b1[k] * z1 + _b2[k] * z2;
            var den = 1 + _a1[k] * z1 + _a2[k] * z2;
            mag *= (num / den).Magnitude;
        }
        return (float)(20 * Math.Log10(Math.Max(mag, 1e-12)));
    }

    /// <summary>One sample through the cascade (transposed direct form II).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x, float[] z)
    {
        for (int k = 0; k < Bands; k++)
        {
            int s = 2 * k;
            float y = _b0[k] * x + z[s];
            z[s] = _b1[k] * x - _a1[k] * y + z[s + 1];
            z[s + 1] = _b2[k] * x - _a2[k] * y;
            x = y;
        }
        return x;
    }
}
