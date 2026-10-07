using System.Collections.Concurrent;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// One wheel meeting a step in the road — a rail at a level crossing: which wheel, when (seconds on
/// <see cref="OpenFPS.Common.AudioClock"/>), how hard (peak pascals at a metre) and how long the tyre's
/// contact patch takes to roll onto it (seconds).
/// </summary>
public readonly record struct WheelStrike(int Wheel, double At, float Pascals, float ContactSeconds);

/// <summary>
/// A tyre struck by a step, wheel by wheel, inside a vehicle's voice. Three things happen, on three
/// time scales:
/// <list type="bullet">
/// <item>The clack: the tread's leading edge meets the rail head's edge. Rubber blocks against steel
/// make a contact of a millisecond or two, whatever the speed, a broadband slap centred near 1.4 kHz
/// that the tread and sidewall radiate well. Outside the car this is most of what is heard.</item>
/// <item>The rail and its panel answer: steel held in rubber, ringing a few milliseconds near a
/// kilohertz and its second mode.</item>
/// <item>The thump: the patch rolling on (15 ms at 36 km/h) drives the tread band's first radial mode
/// (about 90 Hz on a car, heavily damped), and a little of the air cavity's (c / pi D, about 200 Hz).
/// A force that slow has almost nothing at 200 Hz, and what the cavity does is carried into the car by
/// the wheel, not out of the tyre.</item>
/// </list>
/// The slap must not be put under the patch's onset: there it is 30 dB down, and the two modes alone
/// are a hollow note, not a clack (--wheel-strike measures it).
///
/// Strikes are queued ahead with their time; the render thread places each at the sample that will be
/// played then, so the rhythm over two rails comes out as it happened however far ahead the voice is
/// rendered. No allocation once built.
/// </summary>
public sealed class WheelStrikes
{
    private readonly ConcurrentQueue<WheelStrike> _incoming = new();
    private const int MaxPending = 32;
    private readonly long[] _pendingAt = new long[MaxPending];
    private readonly int[] _pendingWheel = new int[MaxPending];
    private readonly float[] _pendingAmp = new float[MaxPending];
    private readonly float[] _pendingTc = new float[MaxPending];
    private int _pending;

    private readonly float _dt;
    private readonly float[] _t, _amp, _onset, _f1, _f2;
    /// <summary>Each wheel's slap band-pass (a biquad, direct form II transposed).</summary>
    private readonly float[] _z1, _z2;
    private readonly float _b0, _b2, _a1, _a2;
    /// <summary>The strike's peak per wheel, for contact times from 2 ms up by half-octaves
    /// (<see cref="StrikePeak"/>), so a strike's peak is about the pascals it was asked for.</summary>
    private readonly float[][] _peaks;
    private const int PeakSteps = 16;
    private uint _noise = 0x9E3779B9u;
    private int _active;

    /// <summary>Damping of the tread band's mode and the cavity's, seconds (Q about 4 and 9).</summary>
    private const float TreadTau = 0.014f, CavityTau = 0.015f;
    private const float StrikeSeconds = 0.25f;
    /// <summary>The slap's middle and the rail's two modes, Hz.</summary>
    private const float SlapHz = 1400f, RailHz = 950f, RailHz2 = 2400f;
    /// <summary>
    /// How much of the strike's peak each part brings: the slap most, the thump a fifth. A tyre radiates
    /// its 90 Hz mode poorly (a third of a metre is a sixth of that wavelength: (ka)² is about a third of
    /// what it radiates at a kilohertz), so outside the car the thump is under the clack.
    /// </summary>
    private const float SlapShare = 0.8f, ThumpShare = 0.2f, RailShare = 0.35f, CavityShare = 0.06f;

    /// <param name="radius">Each wheel's rolling radius, metres, in the order the wire sends the wheels.</param>
    /// <param name="sampleRate">The voice's rate, Hz.</param>
    public WheelStrikes(IReadOnlyList<float> radius, float sampleRate)
    {
        int n = radius.Count;
        _dt = 1f / sampleRate;
        _t = new float[n]; _amp = new float[n]; _onset = new float[n]; _f1 = new float[n]; _f2 = new float[n];
        _z1 = new float[n]; _z2 = new float[n]; _thump = new float[n];
        // A band-pass a little under an octave and a half wide (Q 0.8) round the slap's middle.
        float w0 = MathF.Tau * SlapHz / sampleRate, alpha = MathF.Sin(w0) / (2f * 0.8f), a0 = 1f + alpha;
        _b0 = alpha / a0; _b2 = -alpha / a0; _a1 = -2f * MathF.Cos(w0) / a0; _a2 = (1f - alpha) / a0;
        for (int i = 0; i < n; i++)
        {
            _t[i] = -1f;
            float r = MathF.Max(0.15f, radius[i]);
            // The tread band's mode falls as the tyre grows: about 90 Hz on a 0.33 m car tyre, 60 on a
            // truck's.
            _f1[i] = 90f * MathF.Sqrt(0.33f / r);
            // One wavelength round the torus's mean circumference, a little inside the rolling radius.
            _f2[i] = 343f / (MathF.PI * 2f * r * 0.85f);
        }
        _peaks = new float[n][];
        _thumpPeaks = new float[n][];
        for (int i = 0; i < n; i++)
        {
            _peaks[i] = new float[PeakSteps];
            _thumpPeaks[i] = new float[PeakSteps];
            for (int k = 0; k < PeakSteps; k++)
            {
                float onset = 0.002f * MathF.Pow(2f, k * 0.5f) / 3f;
                _thumpPeaks[i][k] = ThumpPeak(_f1[i], onset, sampleRate);
                _peaks[i][k] = StrikePeak(i, onset, _thumpPeaks[i][k], sampleRate);
            }
        }
    }

    private readonly float[][] _thumpPeaks;
    /// <summary>Each wheel's thump peak for the strike ringing now.</summary>
    private readonly float[] _thump;

    /// <summary>The largest value the thump reaches, found by stepping it, so its share is a share of the peak.</summary>
    private static float ThumpPeak(float f1, float onset, float rate)
    {
        float peak = 0f;
        for (float t = 0f; t < 0.06f; t += 1f / rate)
            peak = MathF.Max(peak, MathF.Abs((1f - MathF.Exp(-t / onset)) * MathF.Sin(MathF.Tau * f1 * t) * MathF.Exp(-t / TreadTau)));
        return MathF.Max(0.05f, peak);
    }

    /// <summary>The whole strike's peak for this onset, stepped through with the noise from its seed: what
    /// the asked-for pascals are divided by. A strike's own noise differs, so its peak is this within a few
    /// tens of per cent.</summary>
    private float StrikePeak(int wheel, float onset, float thumpPeak, float rate)
    {
        uint noise = 0x9E3779B9u;
        float z1 = 0f, z2 = 0f, peak = 0f;
        for (float t = 0f; t < 0.06f; t += 1f / rate)
        {
            noise = noise * 1664525u + 1013904223u;
            float s = Shape(wheel, t, (noise >> 8) / 8388608f - 1f, onset, thumpPeak, ref z1, ref z2);
            peak = MathF.Max(peak, MathF.Abs(s));
        }
        return MathF.Max(0.05f, peak);
    }

    /// <summary>One sample of a strike's shape, unscaled: the slap, the rail, the thump and the cavity.</summary>
    private float Shape(int wheel, float t, float noise, float onset, float thumpPeak, ref float z1, ref float z2)
    {
        // The slap: band-passed noise, on in a third of a millisecond. The tread blocks meet the edge one
        // after another as the patch rolls over it, so it lasts a quarter of the patch's time, three to six
        // milliseconds.
        float bp = _b0 * noise + z1;
        z1 = -_a1 * bp + z2;
        z2 = _b2 * noise - _a2 * bp;
        float slapTau = Math.Clamp(onset * 3f / 4f, 0.003f, 0.006f);
        float slap = bp * 2.2f * (1f - MathF.Exp(-t / 0.0003f)) * MathF.Exp(-t / slapTau);
        // The rail head and its panel.
        float rail = (MathF.Sin(MathF.Tau * RailHz * t) * MathF.Exp(-t / 0.006f)
                    + 0.6f * MathF.Sin(MathF.Tau * RailHz2 * t) * MathF.Exp(-t / 0.003f)) * (1f - MathF.Exp(-t / 0.0002f));
        // The thump, on as the patch rolls on, and the little of the cavity it reaches.
        float rise = 1f - MathF.Exp(-t / onset);
        float thump = rise * MathF.Sin(MathF.Tau * _f1[wheel] * t) * MathF.Exp(-t / TreadTau) / thumpPeak;
        float cavity = rise * MathF.Sin(MathF.Tau * _f2[wheel] * t) * MathF.Exp(-t / CavityTau);
        return SlapShare * slap + RailShare * rail + ThumpShare * thump + CavityShare * cavity;
    }

    private float PeakFor(int wheel, float contactSeconds) => Lerp(_peaks[wheel], contactSeconds);

    private float ThumpPeakFor(int wheel, float contactSeconds) => Lerp(_thumpPeaks[wheel], contactSeconds);

    private static float Lerp(float[] table, float contactSeconds)
    {
        float k = 2f * MathF.Log2(MathF.Max(0.002f, contactSeconds) / 0.002f);
        int lo = Math.Clamp((int)k, 0, PeakSteps - 1), hi = Math.Min(lo + 1, PeakSteps - 1);
        float f = Math.Clamp(k - lo, 0f, 1f);
        return table[lo] + (table[hi] - table[lo]) * f;
    }


    /// <summary>Strikes to come (game thread).</summary>
    public void Queue(IReadOnlyList<WheelStrike> batch)
    {
        foreach (var s in batch) _incoming.Enqueue(s);
    }

    /// <summary>
    /// Takes in what was queued (render thread, once a block): each strike goes at the sample that will
    /// be played at its time — <paramref name="played"/> is playing now, at <paramref name="consumeRate"/>
    /// samples a sample — and no earlier than <paramref name="written"/>, the next to be made.
    /// </summary>
    public void Drain(long written, long played, double now, float sampleRate, float consumeRate)
    {
        while (_pending < MaxPending && _incoming.TryDequeue(out var s))
        {
            if ((uint)s.Wheel >= (uint)_t.Length) continue;
            long at = played + (long)((s.At - now) * sampleRate * MathF.Max(0.1f, consumeRate));
            _pendingAt[_pending] = Math.Max(written, at);
            _pendingWheel[_pending] = s.Wheel;
            _pendingAmp[_pending] = s.Pascals;
            _pendingTc[_pending] = MathF.Max(0.002f, s.ContactSeconds);
            _pending++;
        }
    }

    /// <summary>Whether anything is ringing or to come: the per-wheel work can be skipped when not.</summary>
    public bool Busy => _active > 0 || _pending > 0;

    /// <summary>Starts the strikes due at this sample. Call once a sample, before <see cref="Out"/>.</summary>
    public void Advance(long sample)
    {
        for (int k = 0; k < _pending; k++)
        {
            if (_pendingAt[k] > sample) continue;
            int w = _pendingWheel[k];
            if (_t[w] < 0f) _active++;
            _t[w] = 0f;
            _amp[w] = _pendingAmp[k] / PeakFor(w, _pendingTc[k]);
            _onset[w] = _pendingTc[k] / 3f;
            _thump[w] = ThumpPeakFor(w, _pendingTc[k]);
            _z1[w] = _z2[w] = 0f;
            _pending--;
            _pendingAt[k] = _pendingAt[_pending];
            _pendingWheel[k] = _pendingWheel[_pending];
            _pendingAmp[k] = _pendingAmp[_pending];
            _pendingTc[k] = _pendingTc[_pending];
            k--;
        }
    }

    /// <summary>This sample of one wheel's strike, pascals at a metre; zero when it is not ringing.</summary>
    public float Out(int wheel)
    {
        float t = _t[wheel];
        if (t < 0f) return 0f;
        _t[wheel] = t + _dt;
        if (t > StrikeSeconds)
        {
            _t[wheel] = -1f;
            _active--;
            return 0f;
        }
        _noise = _noise * 1664525u + 1013904223u;
        float noise = (_noise >> 8) / 8388608f - 1f;
        return _amp[wheel] * Shape(wheel, t, noise, _onset[wheel], _thump[wheel], ref _z1[wheel], ref _z2[wheel]);
    }

    // ── How hard ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The peak pressure at a metre of one wheel over a step of <paramref name="stepMetres"/>, pascals.
    /// Anchored at 2 Pa (100 dB) for a car wheel carrying 4 kN over 6 mm at 30 km/h, and growing with
    /// the step, the square root of the speed (the force rises as the contact time shortens, the energy
    /// in the tyre's modes with the speed) and the square root of the load. An assumption to be judged
    /// by ear: no measured figure for a tyre over a crossing's rails was found.
    /// </summary>
    public static float PeakPascals(float speed, float loadNewtons, float stepMetres)
        => 2f * (stepMetres / 0.006f) * MathF.Sqrt(Math.Clamp(speed / 8.33f, 0.1f, 6f))
              * MathF.Sqrt(Math.Clamp(loadNewtons / 4000f, 0.1f, 20f));

    /// <summary>How long a tyre's contact patch takes to roll onto a step: its length (about 45 % of the
    /// rolling radius on a car tyre) over the speed, seconds.</summary>
    public static float ContactSeconds(float speed, float radius)
        => 0.45f * radius / MathF.Max(0.3f, speed);

    /// <summary>
    /// What a planked level crossing is to a tyre, metres: the panels and the rail head are never quite
    /// flush, and the 65 mm flangeway beside each head is a gap the patch dips into. Six millimetres,
    /// the middle of what track standards let a crossing settle to before it is re-levelled.
    /// </summary>
    public const float CrossingStepMetres = 0.006f;
}
