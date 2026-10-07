using System;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// A two-pole band-pass at unit peak gain (the RBJ constant-peak form), for the continuous parts of
/// a texture: the flames' roar, a steam jet, the air past twigs. Retuned in place, so a resonance can
/// wander without restarting.
/// </summary>
public struct Resonator
{
    private float _b0, _a1, _a2, _x1, _x2, _y1, _y2;

    public void Tune(float hz, float q, float sampleRate)
    {
        float w = MathF.Tau * Math.Clamp(hz, 5f, 0.45f * sampleRate) / sampleRate;
        float alpha = MathF.Sin(w) / (2f * MathF.Max(0.1f, q));
        float a0 = 1f + alpha;
        _b0 = alpha / a0;
        _a1 = -2f * MathF.Cos(w) / a0;
        _a2 = (1f - alpha) / a0;
    }

    /// <summary>The coefficients <see cref="Process"/> uses (b1 is 0 and b2 is −b0), for a filter that runs
    /// many of these side by side.</summary>
    public readonly (float B0, float A1, float A2) Coefficients => (_b0, _a1, _a2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        float y = _b0 * (x - _x2) - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1; _x1 = x;
        _y2 = _y1; _y1 = y;
        return y;
    }

    /// <summary>The rms of the output for unit-rms white noise in: the band's share of the noise.</summary>
    public static float NoiseGain(float hz, float q, float sampleRate)
        => MathF.Sqrt(MathF.PI * hz / MathF.Max(0.1f, q) / (0.5f * sampleRate) * 0.5f);
}
