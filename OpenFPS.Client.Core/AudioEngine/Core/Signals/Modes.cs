using System;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Signals;

/// <summary>
/// One resonance, at a sample rate it is told rather than one it assumes. Unit peak gain, so a bank
/// of them is a set of levels and not a set of accidents.
///
/// It is a BANDPASS — zeros at nought and at Nyquist — and not an all-pole resonator, because these
/// stand for the input impedance of a pipe and a pipe's impedance is RESISTIVE at its resonance:
/// pressure and flow in phase. An all-pole section lags ninety degrees there, which is enough to
/// stop a reed and a column from oscillating together at all. (It did. That is how this comment
/// came to be here.)
/// </summary>
internal struct Mode
{
    private float _a1, _a2, _gain;
    private float _z1, _z2, _x1, _x2;

    public Mode(float hz, float q, float rate)
    {
        _a1 = _a2 = _gain = _z1 = _z2 = _x1 = _x2 = 0f;
        Retune(hz, q, rate);
    }

    /// <summary>
    /// Move the resonance without dropping what it is already doing.
    ///
    /// A steam whistle's pitch rides the temperature of the gas inside it, so its modes have to move
    /// while it is sounding. Building a fresh filter to do that throws away the state and puts a
    /// discontinuity in the output — a click — and a whistle that retunes forty times through its
    /// wail crackles all the way up. (It did, and the report was "lots of white noise and it sounds
    /// crackly", which is what several dozen clicks a second under a tone sounds like.) Keeping the
    /// two delayed samples and changing only the coefficients moves the pitch silently.
    /// </summary>
    public void Retune(float hz, float q, float rate)
    {
        float f = Math.Clamp(hz, 2f, rate * 0.46f);
        float r = MathF.Exp(-MathF.PI * MathF.Max(0.05f, f / MathF.Max(0.2f, q)) / rate);
        float theta = MathF.Tau * f / rate;
        _a1 = 2f * r * MathF.Cos(theta);
        _a2 = -r * r;
        _gain = 0.5f * (1f - r * r);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float Process(float x)
    {
        float y = _gain * (x - _x2) + _a1 * _z1 + _a2 * _z2;
        _x2 = _x1; _x1 = x;
        _z2 = _z1; _z1 = y;
        return y;
    }

    /// <summary>The state, for a mode that is struck once and then left to ring.</summary>
    public float Value => _z1;
}
