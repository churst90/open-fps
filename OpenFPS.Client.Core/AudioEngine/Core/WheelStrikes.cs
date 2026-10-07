using System.Collections.Concurrent;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// One wheel meeting a step in the road — a rail at a level crossing: which wheel, when (seconds on
/// <see cref="OpenFPS.Common.AudioClock"/>), how hard (peak pascals at a metre) and how long the tyre's
/// contact patch takes to roll onto it (seconds).
/// </summary>
public readonly record struct WheelStrike(int Wheel, double At, float Pascals, float ContactSeconds);

/// <summary>
/// A tyre struck by a step, wheel by wheel, inside a vehicle's voice.
///
/// The strike is a force on the tread for as long as the contact patch takes to roll onto the step
/// (patch length over speed: 15 ms at 36 km/h). What it rings is the tyre: the tread band's first
/// radial mode (about 90 Hz on a car, lower on a big tyre, heavily damped), the air cavity's mode
/// (c / pi D, about 200 Hz on a car, lightly damped — the "thunk" with a note in it) and a couple of
/// milliseconds of tread slap. A slow tyre rolls on gently and the onset is soft; a fast one meets the
/// step all at once.
///
/// The client schedules strikes ahead with the time they happen; the render thread places each at the
/// sample that will be PLAYED at that time, so the rhythm of a car's wheels over two rails — a gauge
/// apart, then a wheelbase later the back wheels — comes out as it happened however far ahead the
/// voice is rendered. Nothing here allocates once built: the queue's dequeue does not, and the pending
/// list is a fixed array.
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
    /// <summary>The tonal part's peak per wheel, for contact times from 2 ms up by half-octaves
    /// (<see cref="ShapePeak"/>), so a strike's peak is the pascals it was asked for.</summary>
    private readonly float[][] _peaks;
    private const int PeakSteps = 16;
    private uint _noise = 0x9E3779B9u;
    private int _active;

    /// <summary>Damping of the tread band's mode and the cavity's, seconds (Q about 4 and 20).</summary>
    private const float TreadTau = 0.014f, CavityTau = 0.032f, SlapTau = 0.002f;
    private const float StrikeSeconds = 0.25f;

    /// <param name="radius">Each wheel's rolling radius, metres, in the order the wire sends the wheels.</param>
    public WheelStrikes(IReadOnlyList<float> radius, float sampleRate)
    {
        int n = radius.Count;
        _dt = 1f / sampleRate;
        _t = new float[n]; _amp = new float[n]; _onset = new float[n]; _f1 = new float[n]; _f2 = new float[n];
        for (int i = 0; i < n; i++)
        {
            _t[i] = -1f;
            float r = MathF.Max(0.15f, radius[i]);
            // The tread band's first radial mode falls as the tyre grows (a ring's stiffness over its
            // mass): about 90 Hz on a 0.33 m car tyre, 60 on a truck's.
            _f1[i] = 90f * MathF.Sqrt(0.33f / r);
            // The air in the torus: one wavelength round its mean circumference, a little inside the
            // rolling radius (the tyre's section is about 30 % of it): c / (pi D).
            _f2[i] = 343f / (MathF.PI * 2f * r * 0.85f);
        }
        _peaks = new float[n][];
        for (int i = 0; i < n; i++)
        {
            _peaks[i] = new float[PeakSteps];
            for (int k = 0; k < PeakSteps; k++)
                _peaks[i][k] = ShapePeak(_f1[i], _f2[i], 0.002f * MathF.Pow(2f, k * 0.5f) / 3f, sampleRate);
        }
    }

    /// <summary>The largest value the strike's tonal part reaches (noise aside), worked out once by
    /// stepping it: the onset ramp and the two modes' phases decide it.</summary>
    private static float ShapePeak(float f1, float f2, float onset, float rate)
    {
        float peak = 0f;
        for (float t = 0f; t < 0.06f; t += 1f / rate)
        {
            float s = (1f - MathF.Exp(-t / onset))
                    * (0.6f * MathF.Sin(MathF.Tau * f1 * t) * MathF.Exp(-t / TreadTau)
                     + 0.35f * MathF.Sin(MathF.Tau * f2 * t) * MathF.Exp(-t / CavityTau));
            peak = MathF.Max(peak, MathF.Abs(s));
        }
        return MathF.Max(0.05f, peak);
    }

    private float PeakFor(int wheel, float contactSeconds)
    {
        float k = 2f * MathF.Log2(MathF.Max(0.002f, contactSeconds) / 0.002f);
        int lo = Math.Clamp((int)k, 0, PeakSteps - 1), hi = Math.Min(lo + 1, PeakSteps - 1);
        float f = Math.Clamp(k - lo, 0f, 1f);
        return _peaks[wheel][lo] + (_peaks[wheel][hi] - _peaks[wheel][lo]) * f;
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
        float onset = 1f - MathF.Exp(-t / _onset[wheel]);
        float s = 0.6f * MathF.Sin(MathF.Tau * _f1[wheel] * t) * MathF.Exp(-t / TreadTau)
                + 0.35f * MathF.Sin(MathF.Tau * _f2[wheel] * t) * MathF.Exp(-t / CavityTau)
                + 0.25f * noise * MathF.Exp(-t / SlapTau);
        return _amp[wheel] * onset * s;
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
