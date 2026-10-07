using System.Collections.Concurrent;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// The wind in your ears, rendered: one noise per ear, shaped as <see cref="EarWind"/> says.
///
/// Air separating off the pinna rolls past the ear canal as eddies, and the ear hears the pressure
/// under them. Turbulent pressure decorrelates within a centimetre, so each ear has its own noise. It
/// is flat below a knee set by the speed over the ear's size and falls steeply above it (EarWind:
/// −26 dB an octave measured; here a fourth-order fall, −24).
///
/// Eddies a head or two across buffet it over tenths of a second: a lognormal envelope at the speed
/// over half a metre, ±4 dB deep, half shared by the two ears and half each ear's own. The field's
/// gusts (WindField) move the speed, and with it the level and the knee.
/// </summary>
public sealed class EarWindSynth
{
    /// <summary>Depth of the buffeting, nepers of amplitude: ±4.3 dB at one standard deviation.</summary>
    public const float BuffetDepth = 0.5f;
    /// <summary>The size of the eddies that buffet, m: about a head and shoulders.</summary>
    public const float BuffetEddyMetres = 0.5f;
    /// <summary>Below this, nobody hears the pressure and it costs headroom, Hz.</summary>
    public const float HighPassHz = 20f;

    private const float GainSeconds = 0.05f;
    private const float KneeSeconds = 0.1f;

    public readonly float SampleRate;
    private ulong _rng;

    private readonly Svf _hpL = new(), _hpR = new(), _lp1L = new(), _lp1R = new(), _lp2L = new(), _lp2R = new();
    private float _knee = 300f, _norm = 1f;
    private int _sinceGlide;
    private float _gainL, _gainR, _targetL, _targetR, _gainStep;

    /// <summary>
    /// The highest the ears' wind may peak before the master, full scale: −11 dBFS, which the master's
    /// trim and makeup (+9 dB) bring to its limiter's −2 dB ceiling exactly. Buffets stand some 20 dB
    /// over their mean, and left alone a jog or a gale ducked the whole world on every gust. So the
    /// wind rides its own gain: a peak follower one look-ahead (about 6 ms) ahead of the signal,
    /// released over 200 ms, the same gain on both ears so their difference stands. Below the ceiling
    /// it does nothing.
    /// </summary>
    public const float PeakCeiling = 0.2818f;
    private const int LookAhead = 256;
    private readonly float[] _delayL = new float[LookAhead], _delayR = new float[LookAhead];
    private int _delayAt;
    private float _peakEnv, _attack, _release, _held;
    private int _holdLeft;

    // Buffeting: three lowpassed noises (shared, left, right), each two one-poles in cascade.
    private float _mA, _mS1, _mS2, _mL1, _mL2, _mR1, _mR2, _mNorm = 1f;

    /// <summary>What the ears were last placed at, dBFS RMS.</summary>
    public float RenderedLeftDb { get; private set; } = -120f;
    public float RenderedRightDb { get; private set; } = -120f;
    public EarWindAtEars Last { get; private set; }

    public EarWindSynth(float sampleRate, int seed)
    {
        SampleRate = sampleRate;
        _rng = 0x9E3779B97F4A7C15ul ^ (ulong)(uint)seed * 0xBF58476D1CE4E5B9ul;
        if (_rng == 0) _rng = 1;
        _gainStep = 1f - MathF.Exp(-1f / (GainSeconds * sampleRate));
        _attack = 1f - MathF.Exp(-1f / (0.0012f * sampleRate));
        _release = 1f - MathF.Exp(-1f / (0.2f * sampleRate));
        _hpL.Set(HighPassHz, 0.7071f, sampleRate); _hpR.Set(HighPassHz, 0.7071f, sampleRate);
        SetKnee(_knee);
        SetBuffetRate(EarWind.ReferenceSpeed / BuffetEddyMetres);
    }

    /// <summary>
    /// Takes what the ears hear now (<see cref="EarWind.Hear"/>), once a block. Levels glide over
    /// 50 ms and the knee over 100 ms, so a block boundary is never a step.
    /// </summary>
    public void Control(in EarWindAtEars ears)
    {
        Last = ears;
        float l = EarWind.RenderedDb(ears.DeclaredDb, ears.LeftDb);
        float r = EarWind.RenderedDb(ears.DeclaredDb, ears.RightDb);
        if (ears.Speed < EarWind.StillAir || !float.IsFinite(l) || !float.IsFinite(r)) { l = -150f; r = -150f; }
        RenderedLeftDb = l; RenderedRightDb = r;
        _targetL = MathF.Pow(10f, l / 20f);
        _targetR = MathF.Pow(10f, r / 20f);

        // Targets only: Render glides to them every few samples. Set here once a block, the knee
        // stepped at 43 Hz, a staircase in the wind's colour and level on every turn of the head.
        if (float.IsFinite(ears.KneeHz)) _kneeTarget = ears.KneeHz;
        _buffetTarget = Math.Clamp(ears.Speed / BuffetEddyMetres, 0.5f, 30f);
    }

    private float _kneeTarget = 300f, _buffetTarget = EarWind.ReferenceSpeed / BuffetEddyMetres, _buffetHz = EarWind.ReferenceSpeed / BuffetEddyMetres;

    /// <summary>Samples between steps of the knee's and the buffeting's glides.</summary>
    private const int GlideStride = 32;

    private void Glide()
    {
        float k = 1f - MathF.Exp(-GlideStride / (KneeSeconds * SampleRate));
        if (MathF.Abs(_kneeTarget - _knee) > 0.01f) SetKnee(_knee + (_kneeTarget - _knee) * k);
        if (MathF.Abs(_buffetTarget - _buffetHz) > 1e-4f)
        {
            _buffetHz += (_buffetTarget - _buffetHz) * k;
            SetBuffetRate(_buffetHz);
        }
    }

    /// <summary>Forgets every filter and envelope: after a non-finite block. No allocation.</summary>
    public void Reset()
    {
        _hpL.Reset(); _hpR.Reset(); _lp1L.Reset(); _lp1R.Reset(); _lp2L.Reset(); _lp2R.Reset();
        _mS1 = _mS2 = _mL1 = _mL2 = _mR1 = _mR2 = 0f;
        _gainL = _gainR = 0f;
        Array.Clear(_delayL); Array.Clear(_delayR); _peakEnv = 0f; _held = 0f; _holdLeft = 0;
        if (!float.IsFinite(_knee)) SetKnee(300f);
        if (!float.IsFinite(_kneeTarget)) _kneeTarget = _knee;
        if (!float.IsFinite(_buffetHz) || !float.IsFinite(_buffetTarget)) { _buffetHz = _buffetTarget = EarWind.ReferenceSpeed / BuffetEddyMetres; SetBuffetRate(_buffetHz); }
    }

    /// <summary>Whether there is anything to hear: lets a caller skip a silent block.</summary>
    public bool Silent => _gainL < 1e-6f && _gainR < 1e-6f && _targetL < 1e-6f && _targetR < 1e-6f;

    /// <summary>Renders a block into two channels.</summary>
    public void Render(Span<float> left, Span<float> right)
    {
        int n = Math.Min(left.Length, right.Length);
        if (Silent) { left[..n].Clear(); right[..n].Clear(); return; }
        for (int i = 0; i < n; i++)
        {
            if (++_sinceGlide >= GlideStride) { _sinceGlide = 0; Glide(); }
            Next(out left[i], out right[i]);
        }
    }

    /// <summary>One sample of each ear.</summary>
    public void Next(out float left, out float right)
    {
        Gaussian2(out float nl, out float nr);
        float cl = _lp2L.Low(_lp1L.Low(_hpL.High(nl)));
        float cr = _lp2R.Low(_lp1R.Low(_hpR.High(nr)));

        Gaussian2(out float bs, out float bl);
        Gaussian2(out float br, out _);
        _mS1 += _mA * (bs - _mS1); _mS2 += _mA * (_mS1 - _mS2);
        _mL1 += _mA * (bl - _mL1); _mL2 += _mA * (_mL1 - _mL2);
        _mR1 += _mA * (br - _mR1); _mR2 += _mA * (_mR1 - _mR2);
        const float shared = 0.70710678f; // half shared, half each ear's own; √0.5 keeps unit variance
        float ml = (shared * _mS2 + shared * _mL2) * _mNorm;
        float mr = (shared * _mS2 + shared * _mR2) * _mNorm;
        // Lognormal with unit mean square: exp(s m - s^2).
        float el = MathF.Exp(BuffetDepth * ml - BuffetDepth * BuffetDepth);
        float er = MathF.Exp(BuffetDepth * mr - BuffetDepth * BuffetDepth);

        _gainL += _gainStep * (_targetL - _gainL);
        _gainR += _gainStep * (_targetR - _gainR);
        float l = cl * _norm * el * _gainL;
        float r = cr * _norm * er * _gainR;

        // The governor (PeakCeiling): a peak is held for the look-ahead, so the follower has all of
        // it to rise to before it plays.
        float peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
        if (peak >= _held) { _held = peak; _holdLeft = LookAhead; }
        else if (_holdLeft > 0) _holdLeft--;
        else _held = peak;
        _peakEnv += (_held > _peakEnv ? _attack : _release) * (_held - _peakEnv);
        float g = _peakEnv > PeakCeiling ? PeakCeiling / _peakEnv : 1f;
        int at = _delayAt;
        left = _delayL[at] * g;
        right = _delayR[at] * g;
        _delayL[at] = l; _delayR[at] = r;
        _delayAt = at + 1 == LookAhead ? 0 : at + 1;
    }

    private void SetKnee(float hz)
    {
        hz = Math.Clamp(hz, EarWind.MinKneeHz, MathF.Min(EarWind.MaxKneeHz, SampleRate * 0.4f));
        _knee = hz;
        // A fourth-order Butterworth: two sections at the same frequency, Q 0.541 and 1.307.
        _lp1L.Set(hz, 0.5412f, SampleRate); _lp1R.Set(hz, 0.5412f, SampleRate);
        _lp2L.Set(hz, 1.3066f, SampleRate); _lp2R.Set(hz, 1.3066f, SampleRate);
        _norm = 1f / NoiseRms(hz, SampleRate);
    }

    private void SetBuffetRate(float hz)
    {
        float a = 1f - MathF.Exp(-2f * MathF.PI * hz / SampleRate);
        _mA = a;
        // Two identical one-poles in cascade on unit white noise: variance a^4 (1 + q) / (1 - q)^3, q = (1 - a)^2.
        double q = (1.0 - a) * (1.0 - a);
        double v = Math.Pow(a, 4) * (1 + q) / Math.Pow(1 - q, 3);
        _mNorm = (float)(1.0 / Math.Sqrt(Math.Max(1e-12, v)));
    }


    private static readonly ConcurrentDictionary<int, float[]> NoiseTables = new();
    private const int TableSize = 48;

    /// <summary>The RMS of unit white noise through the carrier's filters at a knee, from a table of
    /// the filters' impulse responses, so the carrier is unit RMS whatever the knee.</summary>
    internal static float NoiseRms(float kneeHz, float sampleRate)
    {
        var table = NoiseTables.GetOrAdd((int)sampleRate, r => BuildTable(r));
        float u = MathF.Log(kneeHz / EarWind.MinKneeHz) / MathF.Log(EarWind.MaxKneeHz / EarWind.MinKneeHz) * (TableSize - 1);
        u = Math.Clamp(u, 0f, TableSize - 1.001f);
        int i = (int)u;
        float f = u - i;
        return table[i] + (table[i + 1] - table[i]) * f;
    }

    private static float[] BuildTable(int rate)
    {
        var t = new float[TableSize];
        for (int i = 0; i < TableSize; i++)
        {
            float hz = EarWind.MinKneeHz * MathF.Pow(EarWind.MaxKneeHz / EarWind.MinKneeHz, i / (float)(TableSize - 1));
            hz = MathF.Min(hz, rate * 0.4f);
            var hp = new Svf(); var a = new Svf(); var b = new Svf();
            hp.Set(HighPassHz, 0.7071f, rate); a.Set(hz, 0.5412f, rate); b.Set(hz, 1.3066f, rate);
            double sum = 0;
            int n = rate * 2;
            for (int k = 0; k < n; k++)
            {
                float y = b.Low(a.Low(hp.High(k == 0 ? 1f : 0f)));
                sum += y * (double)y;
            }
            t[i] = (float)Math.Sqrt(sum);
        }
        return t;
    }


    private ulong NextBits()
    {
        ulong x = _rng;
        x ^= x << 13; x ^= x >> 7; x ^= x << 17;
        _rng = x;
        return x;
    }

    /// <summary>Two independent unit Gaussians (Box-Muller).</summary>
    private void Gaussian2(out float a, out float b)
    {
        double u1 = ((NextBits() >> 11) + 1) * (1.0 / 9007199254740993.0);
        double u2 = (NextBits() >> 11) * (1.0 / 9007199254740992.0);
        float r = MathF.Sqrt(-2f * MathF.Log((float)u1));
        float th = 2f * MathF.PI * (float)u2;
        a = r * MathF.Cos(th);
        b = r * MathF.Sin(th);
    }

    /// <summary>A state-variable filter, topology-preserving (Zavalishin), so its frequency can move
    /// every block without a click.</summary>
    private sealed class Svf
    {
        private float _a1, _a2, _a3, _k, _ic1, _ic2;

        public void Reset() { _ic1 = 0f; _ic2 = 0f; }

        public void Set(float hz, float q, float rate)
        {
            float g = MathF.Tan(MathF.PI * Math.Clamp(hz, 1f, rate * 0.45f) / rate);
            _k = 1f / q;
            _a1 = 1f / (1f + g * (g + _k));
            _a2 = g * _a1;
            _a3 = g * _a2;
        }

        private (float Band, float Low) Tick(float x)
        {
            float v3 = x - _ic2;
            float v1 = _a1 * _ic1 + _a2 * v3;
            float v2 = _ic2 + _a2 * _ic1 + _a3 * v3;
            _ic1 = 2f * v1 - _ic1;
            _ic2 = 2f * v2 - _ic2;
            return (v1, v2);
        }

        public float Low(float x) => Tick(x).Low;

        public float High(float x)
        {
            var (band, low) = Tick(x);
            return x - _k * band - low;
        }
    }
}
