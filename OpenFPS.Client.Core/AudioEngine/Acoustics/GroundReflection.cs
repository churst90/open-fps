using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Acoustics;

/// <summary>
/// The ground answering a source that stands on it: the same sound a second time, off the ground
/// between source and listener, a fraction of a millisecond later.
/// </summary>
/// <remarks>
/// The extra path is about 2 h_s h_r / d: an exhaust 0.3 m up heard at ear height 10 m off arrives again
/// 0.4 ms later, a bass lift and a dip at c / (2 Δ), 1.3 kHz there, that slides as the car passes. On a
/// hard road that is most of the difference between a car on the road and one in the air.
///
/// It cannot be a reflection voice: those read their source's ring at least two mixer blocks behind
/// (EngineEchoState.MinDelayBlocks, about 46 ms), because FMOD's order of DSPs is not fixed, and a ground
/// path is a hundred times shorter. So the voice's own output goes through a short delay line and the
/// surface's two bands (from its absorption, split at a kilohertz) and back in. The second arrival's
/// direction, a few degrees lower, is not rendered.
///
/// Mixer-thread object: allocated once, no locks; the game thread writes targets and they are glided to.
/// </remarks>
public sealed class GroundReflection
{
    private const int Size = 2048;                 // room for MaxDelaySeconds at up to 88 kHz
    /// <summary>23 ms, a 7.9 m path difference (1,020 samples at 44.1 kHz, kept as a time at any rate).</summary>
    private const float MaxDelaySeconds = 1020f / 44100f;
    private const int Mask = Size - 1;
    private readonly float[] _line = new float[Size];
    private int _w;
    private float _delay = -1f, _low, _high, _lp;
    private float _texLp, _texInc, _texCut = -1f, _texDelay = -1f;
    private readonly float _lpA, _glide, _rate;

    /// <summary>Samples. Game thread writes.</summary>
    public volatile float TargetDelaySamples;
    /// <summary>Pressure the ground hands back below about a kilohertz, and above it, including the
    /// extra spreading of the longer path. Zero is no ground at all. Game thread writes.</summary>
    public volatile float TargetLowGain, TargetHighGain;

    /// <summary>
    /// The share of the voice's pressure that comes from right on the road (tyres, two centimetres up),
    /// 0..1, written by the producer. That share's reflection is added without the delay: the tyres'
    /// arrives in phase with no notch below 8 kHz, and the whole car at the exhaust's height gave 20 dB
    /// notches where a real car at 10 m shows 3-8 (Harmonoise: road sources at 0.01-0.05 and 0.3 m).
    /// </summary>
    public volatile float NearGroundShare;

    /// <summary>
    /// The voice is a texture of thousands of independent events a second over the water that makes them
    /// (a downpipe, a gutter outlet, a sink); set once by the voice. Its ground is then in phase only below
    /// c / 4Δ, and above that adds its power, not its waveform (Nord2000's incoherent term, F → 0). As a
    /// copy, a gutter outlet 2.8 m up and 1.8 m away combed at 125 Hz, which Cody heard as a fast flanging
    /// repeat (2026-10-06; docs/RUNNING_WATER.md section 11).
    /// </summary>
    public volatile bool Texture;

    public GroundReflection(float sampleRate)
    {
        _rate = sampleRate;
        _lpA = 1f - MathF.Exp(-2f * MathF.PI * 1000f / sampleRate);
        // Thirty milliseconds: a delay that steps tears the waveform, one that lags a pass smears it.
        _glide = 1f - MathF.Exp(-1f / (0.03f * sampleRate));
    }

    /// <summary>Game thread: where the ground is for this voice now; the extra path in seconds.</summary>
    public void Set(float delaySeconds, float lowGain, float highGain)
    {
        TargetDelaySamples = Math.Clamp(delaySeconds * _rate, 0f, MathF.Min(Size - 4, MaxDelaySeconds * _rate));
        TargetLowGain = Math.Clamp(lowGain, 0f, 1f);
        TargetHighGain = Math.Clamp(highGain, 0f, 1f);
    }

    /// <summary>Producer side: the tyre share, see <see cref="NearGroundShare"/>.</summary>
    public void SetNear(float share) => NearGroundShare = Math.Clamp(share, 0f, 1f);

    /// <summary>Whether there is any ground to hear, now or still gliding away.</summary>
    public bool Active => TargetLowGain > 1e-4f || TargetHighGain > 1e-4f || _low > 1e-4f || _high > 1e-4f;

    /// <summary>For a pooled stage handed to a new sound: an empty line, no ground, and the next target taken
    /// at once rather than glided to. Only while nothing is processing.</summary>
    public void Reset()
    {
        Array.Clear(_line);
        _w = 0; _delay = -1f; _low = _high = _lp = 0f;
        _texLp = _texInc = 0f; _texCut = _texDelay = -1f;
        TargetDelaySamples = 0f; TargetLowGain = 0f; TargetHighGain = 0f; NearGroundShare = 0f; Texture = false;
    }

    /// <summary>The direct sample in; the direct sample plus what the ground sends back out.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float Process(float x)
    {
        _line[_w & Mask] = x;
        float td = TargetDelaySamples;
        if (_delay < 0f) { _delay = td; _low = TargetLowGain; _high = TargetHighGain; }   // a new sound starts where it is
        _delay += (td - _delay) * _glide;
        _low += (TargetLowGain - _low) * _glide;
        _high += (TargetHighGain - _high) * _glide;

        float y = x;
        if (Texture && (_low > 1e-4f || _high > 1e-4f)) y = TextureGround(x);
        else if (_low > 1e-4f || _high > 1e-4f)
        {
            // The whole samples stay an integer, only the fraction a float: as one float, _w - _delay
            // lost its fraction past 2^24 samples (6.3 minutes) and every reflection came out stepped,
            // the "high bit crushy frequencies" that came on "after a while".
            int whole = (int)_delay;
            float f = _delay - whole;
            int iNear = _w - whole;
            float a = _line[iNear & Mask], b = _line[(iNear - 1) & Mask];
            float near = NearGroundShare;
            float r = near * x + (1f - near) * (a + (b - a) * f);
            _lp += _lpA * (r - _lp);
            y += _high * r + (_low - _high) * _lp;
        }
        _w++;
        return y;
    }

    /// <summary>A texture's ground (see <see cref="Texture"/>): in phase below c / 4Δ, its power above.</summary>
    private float TextureGround(float x)
    {
        // The corner follows the delay, worked out again when it has moved by a hundredth.
        float delay = MathF.Max(0.25f, _delay);
        if (_texDelay < 0f || MathF.Abs(delay - _texDelay) > 0.01f * _texDelay)
        {
            _texDelay = delay;
            // A one-pole at f = rate / 4Δ: 1 − e^(−2π f / rate) = 1 − e^(−π / 2Δ).
            _texCut = 1f - MathF.Exp(-MathF.PI / (2f * delay));
        }
        _texLp += _texCut * (x - _texLp);
        float near = NearGroundShare;
        // Below the corner: the reflection in phase, through the surface's two bands as a copy would be.
        float r = near * x + (1f - near) * _texLp;
        _lp += _lpA * (r - _lp);
        float y = x + _high * r + (_low - _high) * _lp;
        // Above it: the reflection's power, sqrt(1 + g²) on the direct, per band.
        float inc = (1f - near) * (x - _texLp);
        _texInc += _lpA * (inc - _texInc);
        float kHigh = MathF.Sqrt(1f + _high * _high) - 1f, kLow = MathF.Sqrt(1f + _low * _low) - 1f;
        return y + kHigh * inc + (kLow - kHigh) * _texInc;
    }
}
