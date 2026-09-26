using System;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Acoustics;

/// <summary>
/// The road answering a source that stands on it: the same sound arriving a second time, off the
/// ground between the source and the listener, a fraction of a millisecond after the first.
///
/// Every real vehicle is heard twice. The reflected path is longer by about 2 h_s h_r / d, so a car
/// exhaust 0.3 m up heard at ear height ten metres off arrives again 0.4 ms later, and the two add as
/// a comb: a lift in the bass, where they are in phase, and a dip at c / (2 Δ) — 1.3 kHz there — that
/// slides as the car comes and goes. It is the colour of a real pass-by, and on a hard surface it is
/// most of the difference between a car "on the road" and a car in the air. Further off the delay
/// shrinks below a sample and what is left is the bass lift.
///
/// It cannot be a reflection voice. Those read their source's ring at least two mixer blocks behind
/// it (EngineEchoState.MinDelayBlocks, about 46 ms), because the order FMOD calls the two DSPs in is
/// not fixed; a ground path is a hundred times shorter than that. So it is folded into the voice
/// itself: the voice's own output, run through a few milliseconds of delay line and the surface's
/// reflection, and added back. The direction of the second arrival — a little below the first — is
/// not rendered separately; at the distances where the comb matters it is within a few degrees.
///
/// The surface decides the rest. A hard road reflects almost everything; grass takes the top off.
/// Two bands, from the material's own absorption (the low band below a kilohertz, the high band
/// above), are all the renderer is given — the game thread does the geometry.
///
/// Mixer-thread object: allocated once, no locks, targets written by the game thread and glided to.
/// </summary>
public sealed class GroundReflection
{
    private const int Size = 1024;                 // 23 ms at 44.1 kHz: a 7 m path difference
    private const int Mask = Size - 1;
    private readonly float[] _line = new float[Size];
    private int _w;
    private float _delay = -1f, _low, _high, _lp;
    private readonly float _lpA, _glide, _rate;

    /// <summary>The extra path, in samples. Game thread writes.</summary>
    public volatile float TargetDelaySamples;
    /// <summary>Pressure the ground hands back below about a kilohertz, and above it, including the
    /// extra spreading of the longer path. Zero is no ground at all. Game thread writes.</summary>
    public volatile float TargetLowGain, TargetHighGain;

    public GroundReflection(float sampleRate)
    {
        _rate = sampleRate;
        _lpA = 1f - MathF.Exp(-2f * MathF.PI * 1000f / sampleRate);
        // Thirty milliseconds: a delay that steps tears the waveform, one that lags a pass smears it.
        _glide = 1f - MathF.Exp(-1f / (0.03f * sampleRate));
    }

    /// <summary>Game thread: where the ground is for this voice right now. The extra path in seconds.</summary>
    public void Set(float delaySeconds, float lowGain, float highGain)
    {
        TargetDelaySamples = Math.Clamp(delaySeconds * _rate, 0f, Size - 4);
        TargetLowGain = Math.Clamp(lowGain, 0f, 1f);
        TargetHighGain = Math.Clamp(highGain, 0f, 1f);
    }

    /// <summary>The direct sample in; the direct sample plus what the ground sends back out.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float Process(float x)
    {
        _line[_w & Mask] = x;
        float td = TargetDelaySamples;
        if (_delay < 0f) _delay = td;
        _delay += (td - _delay) * _glide;
        _low += (TargetLowGain - _low) * _glide;
        _high += (TargetHighGain - _high) * _glide;

        float y = x;
        if (_low > 1e-4f || _high > 1e-4f)
        {
            float pos = _w - _delay;
            int i0 = (int)MathF.Floor(pos);
            float f = pos - i0;
            float a = _line[i0 & Mask], b = _line[(i0 + 1) & Mask];
            float r = a + (b - a) * f;
            _lp += _lpA * (r - _lp);
            y += _high * r + (_low - _high) * _lp;
        }
        _w++;
        return y;
    }
}
