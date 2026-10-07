using System;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// White noise shaped to a power spectral density falling as f^−α above a floor frequency: the
/// turbulent tail of a fire's combustion noise (docs/FIRE.md 1.3; Clavin and Siggia 1991 derive f^−5/2,
/// measured 2.1-3.4). Under the floor it falls away at 12 dB an octave, so what cannot be heard is not
/// rendered. Built as a two-pole high-pass and a one-pole low-pass at the floor (f^−2 above it) and a
/// cascade of first-order shelves an octave apart for the rest of the slope, to 20 kHz.
/// </summary>
public struct PowerLawNoise
{
    private const int Shelves = 10;
    // Two-pole high-pass.
    private float _hb0, _hb1, _hb2, _ha1, _ha2, _hx1, _hx2, _hy1, _hy2;
    // One-pole low-pass.
    private float _la, _ly;
    // Shelves: y = b0 x + b1 x1 - a1 y1.
    private ShelfBank _shelves;
    private int _count;

    [InlineArray(Shelves)] private struct Coefficients { private float _e; }
    private struct ShelfBank
    {
        public Coefficients B0, B1, A1, X1, Y1;
    }

    /// <summary>The gain that makes unit-variance white noise in come out with a one-sided PSD of 1 Pa²/Hz at
    /// <see cref="ReferenceHz"/>.</summary>
    public float UnitAtReference;

    public const float ReferenceHz = 100f;

    public void Tune(float alpha, float floorHz, float sampleRate)
    {
        // The high-pass (Butterworth) and the low-pass, both at the floor.
        float w = MathF.Tau * floorHz / sampleRate;
        float cw = MathF.Cos(w), sw = MathF.Sin(w), q = 0.7071f, al = sw / (2f * q);
        float a0 = 1f + al;
        _hb0 = (1f + cw) / 2f / a0; _hb1 = -(1f + cw) / a0; _hb2 = (1f + cw) / 2f / a0;
        _ha1 = -2f * cw / a0; _ha2 = (1f - al) / a0;
        _la = MathF.Exp(-w);
        // The shelves: what the slope needs past the low-pass's 6 dB an octave, one step an octave.
        float rest = MathF.Max(0f, 3.0103f * alpha - 6.0206f);
        float ratio = MathF.Pow(10f, rest / 20f);
        _count = 0;
        float k = 2f * sampleRate;
        for (int i = 0; i < Shelves; i++)
        {
            float p = floorHz * MathF.Pow(2f, i + 0.5f);
            float z = p * ratio;
            if (z > 0.45f * sampleRate || rest <= 0f) break;
            // Prewarped bilinear: H(s) = (1 + s/z) / (1 + s/p).
            float wp = 2f * sampleRate * MathF.Tan(MathF.PI * p / sampleRate);
            float wz = 2f * sampleRate * MathF.Tan(MathF.PI * z / sampleRate);
            float d0 = 1f + k / wp, d1 = 1f - k / wp;
            _shelves.B0[i] = (1f + k / wz) / d0;
            _shelves.B1[i] = (1f - k / wz) / d0;
            _shelves.A1[i] = d1 / d0;
            _count++;
        }
        float mag = Magnitude(ReferenceHz, sampleRate);
        // White noise of variance 1 has a one-sided PSD of 2 / fs.
        UnitAtReference = 1f / (mag * MathF.Sqrt(2f / sampleRate));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        float y = _hb0 * x + _hb1 * _hx1 + _hb2 * _hx2 - _ha1 * _hy1 - _ha2 * _hy2;
        _hx2 = _hx1; _hx1 = x; _hy2 = _hy1; _hy1 = y;
        _ly = (1f - _la) * y + _la * _ly;
        float v = _ly;
        for (int i = 0; i < _count; i++)
        {
            float o = _shelves.B0[i] * v + _shelves.B1[i] * _shelves.X1[i] - _shelves.A1[i] * _shelves.Y1[i];
            _shelves.X1[i] = v;
            _shelves.Y1[i] = o;
            v = o;
        }
        return v;
    }

    /// <summary>The filter's gain at <paramref name="hz"/>.</summary>
    public float Magnitude(float hz, float sampleRate)
    {
        double w = 2 * Math.PI * hz / sampleRate;
        var z1 = System.Numerics.Complex.FromPolarCoordinates(1, -w);
        var z2 = z1 * z1;
        var h = (_hb0 + _hb1 * z1 + _hb2 * z2) / (1 + _ha1 * z1 + _ha2 * z2);
        h *= (1 - _la) / (1 - _la * z1);
        for (int i = 0; i < _count; i++) h *= (_shelves.B0[i] + _shelves.B1[i] * z1) / (1 + _shelves.A1[i] * z1);
        return (float)h.Magnitude;
    }
}
