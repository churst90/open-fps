namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// Brake squeal as a friction-excited instability. At low speed on a moderate pedal, friction falling
/// with sliding speed feeds a rotor bending mode faster than its damping takes it out: heard in the last
/// metres of an ordinary stop, not at speed or in an emergency stop, and whether a car's brakes do it at
/// all is the car's. The envelope is the Stuart-Landau equation
///     da/dt = (g - d) a - (g / A^2) a^3
/// g the friction's gain (nought outside the window), d the mode's damping, A the level it saturates at.
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

    /// <param name="sampleRate">The voice's rate, Hz.</param>
    /// <param name="drums">Heavy vehicles: drum brakes, a lower and heavier mode.</param>
    /// <param name="seed">Per vehicle: whether it squeals, and at what, is the vehicle's.</param>
    public BrakeSqueal(float sampleRate, bool drums, int seed)
    {
        _rate = sampleRate;
        // Hashed: the seed is the entity id, and System.Random's first draws from consecutive seeds
        // are nearly the same (a street's cars sang at 6567, 6568 and 6578 Hz).
        _rng = new Random(Mix(seed));
        // About a quarter of cars; most city buses, on drums, squeal coming in to a stop.
        Squeals = _rng.NextDouble() < (drums ? 0.6 : 0.25);
        _f0 = drums ? 900f + 1300f * (float)_rng.NextDouble()       // drum: 0.9-2.2 kHz
                    : 2500f + 4500f * (float)_rng.NextDouble();     // disc: 2.5-7 kHz
        // A squeal is loud for its size; "nothing exaggerated" is the low end of what one measures.
        float db = drums ? 76f : 70f + 4f * (float)_rng.NextDouble();
        _peakPa = 20e-6f * MathF.Pow(10f, db / 20f);
        _gain = 60f;                     // 1/s: net growth 40/s, a trace to full in about a quarter second
        _damping = 20f;                  // 1/s: rings down to a tenth in about 0.1 s
        // Where the cubic term balances the net gain.
        _settle = MathF.Sqrt((_gain - _damping) / _gain);
    }

    /// <summary>A 32-bit integer hash (Murmur3's finaliser): neighbouring seeds, unrelated results.</summary>
    internal static int Mix(int seed)
    {
        uint h = (uint)seed * 0x9E3779B1u + 0x7F4A7C15u;
        h ^= h >> 16; h *= 0x85EBCA6Bu;
        h ^= h >> 13; h *= 0xC2B2AE35u;
        h ^= h >> 16;
        return (int)(h & 0x7FFFFFFF);
    }

    /// <summary>One sample, pascals at one metre.</summary>
    /// <param name="speed">Road speed, m/s.</param>
    /// <param name="decel">How hard it is braking, m/s², positive.</param>
    public float Step(float speed, float decel)
    {
        if (!Squeals) return 0f;
        float dt = 1f / _rate;
        // Rolling slowly on a moderate pedal: at speed the friction's slope does not matter, and hard
        // braking clamps the pad and kills the mode.
        bool window = speed > 0.15f && speed < 4.5f && decel > 0.4f && decel < 3.5f;
        float g = window ? _gain : 0f;
        float a = _a + dt * ((g - _damping) * _a - g * _a * _a * _a);
        // A trace to grow from: the contact is never perfectly quiet.
        if (window) a += dt * 1e-3f * (float)_rng.NextDouble();
        _a = Math.Clamp(a, 0f, 1.2f);
        if (_a < 1e-5f) return 0f;

        // Pulled up slightly as the rotor slows, and wandering.
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
