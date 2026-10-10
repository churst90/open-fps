using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Signals;

/// <summary>
/// One resonance, unit peak gain, so a bank of them is a set of levels.
///
/// A bandpass (zeros at nought and Nyquist), not an all-pole resonator: these stand for a pipe's
/// input impedance, resistive at resonance. An all-pole section lags ninety degrees there, and a reed
/// and a column would not oscillate together at all (they did not).
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
    /// Moves the resonance and keeps the state. A steam whistle retunes as its gas warms; a fresh
    /// filter each time clicked, and was heard as "lots of white noise and it sounds crackly".
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
}
