using System;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// Brake squeal: a friction-excited instability, not a sample.
///
/// A pad dragged over a rotor is a sliding contact whose friction falls as the sliding speed rises,
/// and at LOW speed, with the pad pressed moderately, that negative slope feeds energy into one of
/// the rotor's bending modes faster than the rotor's own damping takes it out. The mode grows until
/// something nonlinear limits it, and it dies away again when the wheel stops, the driver presses
/// harder or lets go. So squeal is heard in the last few metres of an ordinary stop and not at speed,
/// and not in an emergency stop; it is a single tone that wanders a little with the speed and the
/// temperature; and whether a given car's brakes do it at all is a property of that car — its pads,
/// its rotors, their wear — not of the moment.
///
/// Modelled as the envelope of that instability, the Stuart–Landau equation
///     da/dt = (g - d) a - (g / A^2) a^3
/// where g is the friction's gain (nought outside the squeal window), d the mode's damping and A
/// the level it saturates at. It grows from a trace of noise, levels off at A, and rings down at
/// d when the conditions go. The tone is the mode, pulled slightly by the sliding speed.
/// </summary>
public sealed class BrakeSqueal
{
    private readonly float _rate, _f0, _peakPa, _gain, _damping, _settle;
    private readonly Random _rng;
    private float _a;
    private double _phase, _wander;

    /// <summary>False for a vehicle whose brakes do not squeal, which is most of them.</summary>
    public bool Squeals { get; }

    /// <summary>The rotor mode it squeals at, Hz.</summary>
    public float Hz => _f0;

    /// <param name="drums">Heavy vehicles: drum brakes, a lower and heavier mode.</param>
    /// <param name="seed">Per vehicle: whether it squeals, and at what, is the vehicle's.</param>
    public BrakeSqueal(float sampleRate, bool drums, int seed)
    {
        _rate = sampleRate;
        _rng = new Random(seed * 7349 + 11);
        // About a quarter of cars on a street have brakes that sing, and rather more of the buses.
        Squeals = _rng.NextDouble() < (drums ? 0.35 : 0.25);
        _f0 = drums ? 900f + 1300f * (float)_rng.NextDouble()       // drum: 0.9-2.2 kHz
                    : 2500f + 4500f * (float)_rng.NextDouble();     // disc: 2.5-7 kHz
        // A squeal is loud for its size; "nothing exaggerated" is the low end of what one measures.
        float db = drums ? 76f : 70f + 4f * (float)_rng.NextDouble();
        _peakPa = 20e-6f * MathF.Pow(10f, db / 20f);
        // It grows only where the friction's gain beats the mode's damping; the difference is how fast.
        _gain = 60f;                     // 1/s: net growth 40/s, a trace to full in about a quarter second
        _damping = 20f;                  // 1/s: rings down to a tenth in about 0.1 s
        // ...and it settles where the cubic term balances the net gain, sqrt((g - d) / g) of full.
        _settle = MathF.Sqrt((_gain - _damping) / _gain);
    }

    /// <summary>
    /// One sample, pascals at one metre.
    /// </summary>
    /// <param name="speed">Road speed, m/s.</param>
    /// <param name="decel">How hard it is braking, m/s², positive.</param>
    public float Step(float speed, float decel)
    {
        if (!Squeals) return 0f;
        float dt = 1f / _rate;
        // The window: rolling slowly, on a moderate pedal. At speed the sliding velocity is too high
        // for the friction's slope to matter; at a standstill nothing slides; hard braking clamps
        // the pad and kills the mode.
        bool window = speed > 0.15f && speed < 4.5f && decel > 0.4f && decel < 3.5f;
        float g = window ? _gain : 0f;
        float a = _a + dt * ((g - _damping) * _a - g * _a * _a * _a);
        // A trace to grow from: the contact is never perfectly quiet.
        if (window) a += dt * 1e-3f * (float)_rng.NextDouble();
        _a = Math.Clamp(a, 0f, 1.2f);
        if (_a < 1e-5f) return 0f;

        // The mode, pulled up slightly as the rotor slows and it cools, and wandering.
        _wander += dt * 0.7;
        float f = _f0 * (1f + 0.012f * (1f - Math.Clamp(speed / 4.5f, 0f, 1f)) + 0.003f * (float)Math.Sin(_wander * 2 * Math.PI));
        _phase += f * dt;
        if (_phase >= 1.0) _phase -= 1.0;
        double w = 2 * Math.PI * _phase;
        // Limited by a stiffening contact, so a little of the second harmonic.
        float y = (float)(Math.Sin(w) + 0.12 * Math.Sin(2 * w));
        return y * (_a / _settle) * _peakPa;
    }
}
