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
    private const int Size = 2048;                 // room for MaxDelaySeconds at up to 88 kHz
    /// <summary>The longest ground path difference held, seconds: 23 ms, a 7.9 m path difference (what
    /// 1,020 samples held at 44.1 kHz, kept as a time at any rate).</summary>
    private const float MaxDelaySeconds = 1020f / 44100f;
    private const int Mask = Size - 1;
    private readonly float[] _line = new float[Size];
    private int _w;
    private float _delay = -1f, _low, _high, _lp;
    private float _texLp, _texInc, _texCut = -1f, _texDelay = -1f;
    private readonly float _lpA, _glide, _rate;

    /// <summary>The extra path, in samples. Game thread writes.</summary>
    public volatile float TargetDelaySamples;
    /// <summary>Pressure the ground hands back below about a kilohertz, and above it, including the
    /// extra spreading of the longer path. Zero is no ground at all. Game thread writes.</summary>
    public volatile float TargetLowGain, TargetHighGain;

    /// <summary>
    /// How much of what this voice carries comes from right on the road — tyres, two centimetres up
    /// — as a share of its pressure, 0..1. The producer writes it from what it synthesized.
    ///
    /// A car is not one point. The tyres' reflection arrives with them, in phase, a broadband lift
    /// with no notch below 8 kHz; only the pipe and the block, a third of a metre up, make the comb.
    /// Rendering the whole car at the exhaust's height gave it a single source's 20 dB notches where a
    /// real car at 10 m shows 3-8 (Harmonoise puts road sources at 0.01-0.05 and 0.3 m). So that share
    /// of the reflection is added without the delay.
    /// </summary>
    public volatile float NearGroundShare;

    /// <summary>
    /// The voice is a texture: thousands of independent events a second, spread over the water that
    /// makes them (a downpipe's splash, a gutter outlet's gulps, a sink's stream). Set once by the voice.
    ///
    /// Its reflection is then not a copy. Below a quarter of the period of the extra path, c / 4Δ, the
    /// two paths are within a quarter-cycle of each other wherever on the source an event happens, and
    /// the ground lifts the bass by its full pressure, as for any source. Above it, where the events'
    /// own places, the water's movement and the listener's head move the reflection's phase by more
    /// than that, what the ground adds is its power, not its waveform: Nord2000's incoherent term
    /// (|p_d + F p_r|² + (1 − F²) |p_r|², with F → 0). Without this a gutter outlet 2.8 m up and
    /// 1.8 m away was one stream heard twice 8 ms apart, a comb at 125 Hz that Cody heard as a fast
    /// flanging repeat (2026-10-06), as he had heard speech through the same copy flange
    /// (docs/RUNNING_WATER.md section 11).
    /// </summary>
    public volatile bool Texture;

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
        TargetDelaySamples = Math.Clamp(delaySeconds * _rate, 0f, MathF.Min(Size - 4, MaxDelaySeconds * _rate));
        TargetLowGain = Math.Clamp(lowGain, 0f, 1f);
        TargetHighGain = Math.Clamp(highGain, 0f, 1f);
    }

    /// <summary>Producer side: the tyre share, see <see cref="NearGroundShare"/>.</summary>
    public void SetNear(float share) => NearGroundShare = Math.Clamp(share, 0f, 1f);

    /// <summary>Whether there is any ground to hear, now or still gliding away.</summary>
    public bool Active => TargetLowGain > 1e-4f || TargetHighGain > 1e-4f || _low > 1e-4f || _high > 1e-4f;

    /// <summary>Forgets the last sound: an empty line, no ground, and the next target taken at once
    /// rather than glided to. For a pooled stage handed to a new sound, which must not start with the
    /// previous one's delay or its tail. Only while nothing is processing.</summary>
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
            // The read point is _w - _delay. Worked out as a FLOAT it was exact for 6.3 minutes:
            // past 2^24 samples a float has no fraction left, the point snapped to every second
            // sample, then every fourth, and each car's reflection came out stepped — the "high bit
            // crushy frequencies" that came on "after a while". The whole samples stay an integer;
            // only the fraction is a float.
            int whole = (int)_delay;
            float f = _delay - whole;                 // how far past the whole-sample delay
            int iNear = _w - whole;                   // _delay == whole: this sample
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
        // The corner follows the delay as it glides; worked out again only when it has moved by a
        // hundredth, not every sample.
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
