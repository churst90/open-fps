using OpenFPS.Common;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

// One-dimensional gas acoustics in real units: pascals about the pipe's mean, metres, and a gas with a
// temperature. Real units let the steepening of a half-bar front into a shock be computed from the
// pulse the cylinder made, and make the radiated level come out in decibels.

/// <summary>Properties of the gas in a pipe, from its temperature.</summary>
internal static class Gas
{
    /// <summary>Gas constant of burnt petrol-air and of air, J/kg/K. Close enough to share.</summary>
    public const float R = 288f;
    public const float GammaExhaust = 1.33f;
    public const float GammaAir = 1.40f;
    public const float Atmosphere = 101325f;

    public static float SoundSpeed(float kelvin, float gamma) => MathF.Sqrt(gamma * R * kelvin);
    public static float Density(float pascalsAbs, float kelvin) => pascalsAbs / (R * kelvin);

    /// <summary>
    /// Kinematic viscosity of hot combustion gas, m^2/s. Sutherland for air scaled by density;
    /// 1.5e-5 at 20 C, 8e-5 near 700 C.
    /// </summary>
    public static float KinematicViscosity(float kelvin)
    {
        float mu = 1.716e-5f * MathF.Pow(kelvin / 273.15f, 1.5f) * (273.15f + 110.4f) / (kelvin + 110.4f);
        return mu / Density(Atmosphere, kelvin);
    }
}

/// <summary>
/// A travelling wave along one direction of one pipe: a delay line whose delay depends on the
/// amplitude of what is travelling through it.
///
/// A crest travels at c0 (1 + beta p'/(gamma p0)), beta = (gamma+1)/2, so the front steepens: a
/// 0.5 bar pulse in 600 C gas forms a shock within about 0.7 m, inside any header's primary
/// (Matsumura et al., JSME 1990). Idle pulses are a tenth of that and never steepen: load is raspy,
/// idle soft, which no linear delay line can do.
///
/// Steepening is done by writing at arrival time: each sample gets the arrival its amplitude implies
/// and the slots between are interpolated. When a crest overtakes the foot, the crossed samples merge
/// into one jump at the mean of their wanted arrivals (Whitham's equal-area rule), one sample thick at
/// any rate. That dissipation is the only thing that stops a near-lossless network of steepening pipes
/// pumping itself up: without it the F1's `structure` fell from 51 dB to 15 above 12,200 rpm
/// (docs/ENGINE_SYNTHESIS.md, "The shock is an equal-area jump").
/// </summary>
internal sealed class WaveLine
{
    private readonly float[] _buf;
    private readonly int _len;
    private long _time;
    private double _tauPrev;
    private float _vPrev;
    private float _d0;
    private float _kappa;                      // 1/Pa: relative speed-up per pascal
    private float _gain = 1f, _alpha = 1f, _lp;
    private const double MinDelay = 1.05;

    // The slot the most recent arrivals have been landing in, and the running mean of them. See Write.
    private long _accSlot = long.MinValue;
    private double _accSum;
    private int _accN;

    // The shock being built: how many samples have merged, the sum of their wanted arrivals (the
    // jump sits at the mean), and the last sample before anything crossed, which the jump rises from.
    private int _frontN;
    private double _frontSumTau;
    private double _preTau;
    private float _preV;

    /// <summary>Arrivals closer than this are a crossing: the crest has caught the foot. A quarter of
    /// a sample rather than zero so that a wave steepening right at the limit of what the grid can
    /// hold is merged rather than left as a one-sample impulse of arbitrary height.</summary>
    private const double CrossingSamples = 0.25;

    /// <param name="maxDelaySamples">Longest delay this line will ever be asked for.</param>
    public WaveLine(int maxDelaySamples)
    {
        _len = Math.Max(8, maxDelaySamples + 8);
        _buf = new float[_len];
        _d0 = Math.Max((float)MinDelay, maxDelaySamples - 2);
        _tauPrev = _d0 - 1;
        _preTau = _tauPrev - 1;
    }

    public void SetDelay(float samples)
    {
        _d0 = Math.Clamp(samples, (float)MinDelay, _len - 4f);
        // A line never read is empty: its next arrival is the new delay away, not the construction delay.
        if (_time == 0) { _tauPrev = _d0 - 1; _preTau = _tauPrev - 1; }
    }

    /// <summary>Steepening coefficient: the fractional increase in wave speed per pascal.
    /// Physically beta / (gamma p0); 0 turns the line into an ordinary fractional delay.</summary>
    public void SetSteepening(float perPascal) => _kappa = MathF.Max(0f, perPascal);

    /// <summary>Per-traverse loss: a flat gain and a one-pole low-pass, applied at the far end.</summary>
    public void SetLoss(float gain, float onePoleAlpha)
    {
        _gain = Math.Clamp(gain, 0f, 1f);
        _alpha = Math.Clamp(onePoleAlpha, 1e-4f, 1f);
    }

    /// <summary>Injects a sample at the near end, now.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Write(float v)
    {
        if (!float.IsFinite(v)) v = 0f;
        // Clamped: beyond about +150 % the acoustics this rides on means nothing.
        float speedUp = 1f + Math.Clamp(_kappa * v, -0.4f, 1.5f);
        double d = _d0 / speedUp;
        if (d < MinDelay) d = MinDelay;
        // A pipe is read and then written within one step, so "now" for the write is the sample just
        // read: the delay is then exactly what SetDelay asked for.
        double tau = (_time - 1) + d;
        long kMax = _time + _len - 2;

        if (tau < _tauPrev + CrossingSamples)
        {
            // The crest has caught the foot: merge into the shock, move the jump to the mean of
            // everything crossed, and fill the slots behind it with this sample's value.
            if (_frontN == 0)
            {
                // The previous sample is the first crossed; the jump rises from the wave before it.
                _frontSumTau = _tauPrev;
                _frontN = 1;
            }
            _frontSumTau += tau;
            _frontN++;
            double tauS = _frontSumTau / _frontN;
            // The jump cannot land before the wave ahead of it, nor in a slot already read.
            double floor = Math.Max(_preTau + 1.0, _time + 0.001);
            if (tauS < floor) tauS = floor;
            if (tauS > _tauPrev) tauS = _tauPrev;

            long kS = (long)Math.Floor(tauS);
            float frac = (float)(tauS - kS);
            long kEnd = Math.Min((long)Math.Floor(_tauPrev), kMax);
            if (kS <= kMax)
            {
                // Split by where in its slot the jump falls: a first-order band-limited step.
                _buf[Slot(kS)] = _preV + (v - _preV) * (1f - frac);
                for (long k = kS + 1; k <= kEnd; k++) _buf[Slot(k)] = v;
            }
            _accSlot = long.MinValue;
            _tauPrev = tauS;
            _vPrev = v;
            return;
        }

        if (_frontN > 0)
        {
            // The wave behind is stretching away again: the shock is finished where it is.
            _frontN = 0;
        }
        _preTau = _tauPrev;
        _preV = _vPrev;
        {
            double span = tau - _tauPrev;
            long k0 = (long)Math.Floor(_tauPrev) + 1;
            long k1 = (long)Math.Floor(tau);
            if (k1 > kMax) k1 = kMax;
            for (long k = k0; k <= k1; k++)
            {
                float f = (float)((k - _tauPrev) / span);
                Deposit(k, _vPrev + (v - _vPrev) * f);
            }
            // Compression short of a crossing: this arrival fell in the last one's slot and the loop
            // wrote nothing, so it is averaged in: a box filter over the compressed samples, the right
            // anti-aliasing for squeezing a signal in time.
            if (k1 < k0)
            {
                long k = (long)Math.Floor(tau);
                if (k <= kMax && k >= _time) Deposit(k, v);
            }
        }
        _tauPrev = tau;
        _vPrev = v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Slot(long k) => (int)(((k % _len) + _len) % _len);

    /// <summary>
    /// Puts a value into a slot, averaging with anything already deposited there this pass: the mean,
    /// not the last writer, stops a compressed front turning into an impulse.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Deposit(long k, float value)
    {
        int idx = Slot(k);
        if (k == _accSlot) { _accSum += value; _accN++; _buf[idx] = (float)(_accSum / _accN); }
        else { _accSlot = k; _accSum = value; _accN = 1; _buf[idx] = value; }
    }

    /// <summary>What arrives at the far end now, after the traverse loss. Advances time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float Read()
    {
        int i = (int)(_time % _len);
        float v = _buf[i];
        _buf[i] = 0f;
        _time++;
        _lp += _alpha * (v - _lp);
        return _gain * _lp;
    }

}

/// <summary>
/// A length of pipe carrying waves both ways, with the gas inside it at some temperature.
///
/// The forward line steepens (that is where the big pulses travel); the backward line is linear,
/// because what comes back from a collector or an open end is a fraction of what went down.
/// </summary>
internal sealed class Pipe
{
    public readonly float Length, Area, Radius;
    private readonly WaveLine _fwd, _bwd;
    private readonly float _rate;
    private float _c = 500f, _rho = 0.5f;
    private readonly float _wallLoss;
    private float _steepScale;
    private float _extraLossGain = 1f, _extraLossAlpha = 1f;

    public Pipe(float lengthMetres, float areaM2, float rate, float wallLossMultiplier, float steepening)
    {
        Length = MathF.Max(0.02f, lengthMetres);
        Area = MathF.Max(1e-5f, areaM2);
        Radius = MathF.Sqrt(Area / MathF.PI);
        _rate = rate;
        _wallLoss = wallLossMultiplier;
        _steepScale = steepening;
        // Sized for the slowest gas it will ever hold: 250 m/s is below any exhaust or intake air.
        int longest = (int)(Length / 250f * rate) + 4;
        _fwd = new WaveLine(longest);
        _bwd = new WaveLine(longest);
    }

    /// <summary>Speed of sound, m/s, and density, kg/m^3, of the gas now in the pipe.</summary>
    public float SoundSpeed => _c;
    public float Density => _rho;
    /// <summary>Characteristic impedance, Pa s / m^3: pressure per unit volume velocity.</summary>
    public float Impedance => _rho * _c / Area;
    /// <summary>Acoustic admittance, the reciprocal — what junctions weight by. Scaled by
    /// <see cref="AreaScale"/> so a throttle plate can shut a pipe without rebuilding it.</summary>
    public float Admittance => Area * AreaScale / (_rho * _c);
    /// <summary>Fraction of the cross-section actually open, 0..1. Only a throttle changes it.</summary>
    public float AreaScale { get; set; } = 1f;

    /// <summary>Extra loss beyond the wall — a muffler's packing, a baffle. Gain and one-pole alpha per traverse.</summary>
    public void SetExtraLoss(float gain, float alpha) { _extraLossGain = gain; _extraLossAlpha = alpha; }

    /// <summary>
    /// Retunes the pipe for the gas now in it. Mean flow shortens the forward delay and lengthens the
    /// backward one — a small effect at exhaust Mach numbers, but it is free.
    /// </summary>
    public void SetGas(float kelvin, float gamma, float meanFlowMach)
    {
        _c = Gas.SoundSpeed(kelvin, gamma);
        _rho = Gas.Density(Gas.Atmosphere, kelvin);
        float m = Math.Clamp(meanFlowMach, 0f, 0.4f);
        _fwd.SetDelay(Length / (_c * (1f + m)) * _rate);
        _bwd.SetDelay(Length / (_c * (1f - m)) * _rate);

        // Finite-amplitude steepening: beta/(gamma p0), scaled by the profile's setting.
        float beta = (gamma + 1f) * 0.5f;
        _fwd.SetSteepening(_steepScale * beta / (gamma * Gas.Atmosphere));
        _bwd.SetSteepening(0f);

        // Viscothermal wall loss (Kirchhoff): alpha = (1/(r c)) sqrt(pi f nu) (1 + (gamma-1)/sqrt(Pr))
        // nepers per metre, rising as sqrt(f): a flat gain matched at 150 Hz and a one-pole matched at
        // 4 kHz per traverse. Turbulent mean flow roughly doubles it (the profile's multiplier).
        float nu = Gas.KinematicViscosity(kelvin);
        float k = 1f / (Radius * _c) * MathF.Sqrt(MathF.PI * nu) * (1f + (gamma - 1f) / MathF.Sqrt(0.71f));
        k *= _wallLoss * (1f + 2f * m);
        float g1 = MathF.Exp(-k * Length * MathF.Sqrt(150f));
        float g2 = MathF.Exp(-k * Length * MathF.Sqrt(4000f));
        float ratio = MathF.Max(1e-3f, g2 / g1);
        float fc = ratio >= 0.999f ? _rate : 4000f / MathF.Sqrt(MathF.Max(1e-6f, 1f / (ratio * ratio) - 1f));
        float alpha = OnePole.AlphaFor(fc, _rate);
        float gain = g1 * _extraLossGain;
        float a = MathF.Min(alpha, _extraLossAlpha);
        _fwd.SetLoss(gain, a);
        _bwd.SetLoss(gain, a);
    }

    public void PushForward(float p) => _fwd.Write(p);
    public void PushBackward(float p) => _bwd.Write(p);
    /// <summary>The forward wave arriving at the far end. Call exactly once per sample.</summary>
    public float ArriveFar() => _fwd.Read();
    /// <summary>The backward wave arriving at the near end. Call exactly once per sample.</summary>
    public float ArriveNear() => _bwd.Read();

}

/// <summary>
/// The scattering junction where N pipes meet: pressure is common, volume flows sum to zero.
///
///     p_J = 2 sum(Y_k a_k) / (sum(Y_k) + Y_loss)
///     b_k = p_J - a_k
///
/// Lossless for Y_loss = 0 (Kelly-Lochbaum). Y_loss is a resistive sink for the vortices a mean flow
/// sheds at an abrupt junction; without it the network rings.
/// </summary>
internal static class Junction
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Scatter(ReadOnlySpan<float> a, ReadOnlySpan<float> y, Span<float> b, float lossAdmittance)
    {
        float num = 0f, den = lossAdmittance;
        for (int i = 0; i < a.Length; i++) { num += y[i] * a[i]; den += y[i]; }
        float pj = 2f * num / MathF.Max(1e-12f, den);
        for (int i = 0; i < a.Length; i++) b[i] = pj - a[i];
    }

    /// <summary>Two pipes end to end: the reflection at an area change.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static (float BackIntoA, float OnIntoB) Two(float aFromA, float aFromB, float yA, float yB, float loss)
    {
        float pj = 2f * (yA * aFromA + yB * aFromB) / MathF.Max(1e-12f, yA + yB + loss);
        return (pj - aFromA, pj - aFromB);
    }
}

/// <summary>
/// The open end of a pipe: it reflects most of the low frequencies (so the pipe resonates) and
/// radiates what it does not reflect.
///
/// Levine and Schwinger's unflanged pipe: |R| is 1 at low ka, 0.89 at ka=0.5, 0.69 at ka=1, 0.35 at
/// ka=2, with an end correction of 0.61 radii shrinking to about 0.2 under strong mean flow. For a
/// 63 mm tailpipe in 200 C gas ka=1 is near 2.2 kHz, so the pipe is a mirror across the engine
/// orders. Modelled as an inverted one-pole low-pass on the reflection (corner at ka ~ 0.9) plus the
/// end correction as delay; mean flow scales the reflection by (1-M)/(1+M).
///
/// The radiated pressure is the monopole field of the volume velocity leaving the end,
/// p(r, t) = rho_air / (4 pi r) * dU/dt: the derivative is why the sound outside a pipe is far
/// brighter than the pressure inside it.
/// </summary>
internal sealed class OpenEnd
{
    private readonly float _rate;
    private float _reflAlpha, _reflLp;
    private float _reflGain;
    private float _dcLp;
    private readonly float _dcAlpha;
    private float _uPrev;
    private float _radLp, _radAlpha;
    private float _endDelay;                 // end correction, samples — folded into a tiny line
    private readonly float[] _endBuf = new float[16];
    private int _endAt;
    private float _z = 1f;

    public OpenEnd(float rate)
    {
        _rate = rate;
        _dcAlpha = OnePole.AlphaFor(4f, rate);
    }

    public void Configure(float radius, float soundSpeed, float density, float area, float mach)
    {
        float m = Math.Clamp(mach, 0f, 0.35f);
        float fKa1 = soundSpeed / (2f * MathF.PI * radius);
        _reflAlpha = OnePole.AlphaFor(0.9f * fKa1, _rate);
        // Radiation efficiency saturates at the same ka = 1. See Radiate.
        _radAlpha = OnePole.AlphaFor(fKa1, _rate);
        _reflGain = (1f - m) / (1f + m);
        float corr = radius * MathHelper.Lerp(0.61f, 0.2f, m / 0.35f);
        _endDelay = Math.Clamp(corr / soundSpeed * _rate, 0f, 12f * _rate / At44k.Rate);   // 12 samples at 44.1 kHz: 0.27 ms
        _z = density * soundSpeed / area;
    }

    /// <summary>Feeds the wave arriving at the end; returns (reflected wave, volume velocity leaving).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public (float Reflected, float VolumeVelocity) Process(float arriving)
    {
        // The reflection: low frequencies come back inverted, the top leaves.
        _reflLp += _reflAlpha * (arriving - _reflLp);
        float r = -_reflLp * _reflGain;
        // Bleed DC: a mean flow is not an acoustic wave and must not bounce forever.
        _dcLp += _dcAlpha * (r - _dcLp);
        r -= _dcLp;
        // End correction as a short fractional delay on the reflection.
        _endBuf[_endAt] = r;
        float read = _endAt - _endDelay;
        if (read < 0f) read += _endBuf.Length;
        int i0 = (int)read; if (i0 >= _endBuf.Length) i0 -= _endBuf.Length;
        int i1 = i0 + 1 == _endBuf.Length ? 0 : i0 + 1;
        float rd = _endBuf[i0] + (_endBuf[i1] - _endBuf[i0]) * (read - i0);
        _endAt = _endAt + 1 == _endBuf.Length ? 0 : _endAt + 1;

        float u = (arriving - rd) / _z;
        return (rd, u);
    }

    /// <summary>
    /// Far-field pressure at one metre from a volume velocity history, pascals.
    ///
    /// The derivative rises six decibels an octave, true only while the opening is small: past ka = 1
    /// the radiated pressure is the travelling wave, flat with frequency. Uncapped, a V10 at 17,000 rpm
    /// put its spectral centroid at 8 kHz. One pole at ka = 1 (c/(2 pi a), about 2 kHz for a
    /// three-inch tailpipe in hot gas) cancels the derivative above it and leaves a V8's orders alone.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float Radiate(float u, float airDensity)
    {
        float du = (u - _uPrev) * _rate;
        _uPrev = u;
        float p = airDensity / (4f * MathF.PI) * du;
        _radLp += _radAlpha * (p - _radLp);
        return _radLp;
    }
}

/// <summary>
/// Broadband noise of the exhaust jet leaving the tailpipe — Lighthill's law, and nothing else.
///
/// Acoustic power W = K rho0 (rho_jet/rho0) U^8 D^2 / c^5 (K about 1e-4 subsonic): the pressure at a
/// metre goes as U^4 and linearly with the nozzle diameter, peaking at Strouhal 0.2. Modulated by the
/// pulsating flow, which comes in slugs. One law for every jet in the game, this and the aircraft's;
/// a hand calibration 17 dB above it put a hiss over every small muffled engine
/// (docs/ENGINE_SYNTHESIS.md, "Every jet is Lighthill's").
/// </summary>
internal sealed class JetNoise
{
    private readonly float _rate;
    private readonly Random _rng;
    private float _lp1, _lp2, _hp, _uSlow;
    private readonly float _diameter;

    public JetNoise(float rate, float diameterMetres, int seed)
    {
        _rate = rate;
        _diameter = MathF.Max(0.01f, diameterMetres);
        _rng = new Random(seed);
    }

    /// <summary>Lighthill coefficient for a subsonic jet, the textbook 1e-4.</summary>
    public const float Lighthill = 1e-4f;

    /// <summary>
    /// RMS pressure at one metre, pascals, of a jet of this diameter and velocity at this gas
    /// temperature. Velocity is taken no higher than a high subsonic value: past the speed of sound
    /// the eighth power gives way to the third, and this is not a model of a supersonic jet.
    /// </summary>
    public static float LighthillPressure(float diameterMetres, float velocity, float gasKelvin)
    {
        const float rho0 = 1.2f, c = 343f;
        float rhoRatio = 293f / MathF.Max(293f, gasKelvin);
        double u = Math.Min(Math.Abs(velocity), 600.0);
        double w = Lighthill * rho0 * rhoRatio * Math.Pow(u, 8) * diameterMetres * diameterMetres / Math.Pow(c, 5);
        double p2 = w * rho0 * c / (4 * Math.PI);
        return (float)Math.Sqrt(Math.Max(0.0, p2));
    }

    /// <summary>The same, in dB SPL at one metre.</summary>
    public static float LighthillDb(float diameterMetres, float velocity, float gasKelvin)
        => 20f * MathF.Log10(MathF.Max(1e-9f, LighthillPressure(diameterMetres, velocity, gasKelvin)) / 2e-5f);

    /// <summary>
    /// A two-pole band of unit white noise has an RMS of about 0.285 times the square root of the
    /// one-pole coefficient (measured over 60 Hz - 3 kHz at 44.1 kHz); dividing by that makes the
    /// band unit-RMS whatever its corner and whatever the sample rate, so a level is a level.
    /// </summary>
    public static float BandNormaliser(float alpha) => 0.285f * MathF.Sqrt(MathF.Max(1e-6f, alpha));

    /// <summary>Pressure at one metre, pascals, given the exit velocity now (m/s), the mean, and
    /// the gas temperature at the tailpipe.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float Process(float exitVelocity, float meanVelocity, float level, float gasKelvin)
    {
        // Mixing noise follows the velocity over the mixing length (5D/U), not at the lip: a big slow
        // engine's slugs still breathe at its firing rate, and a small fast one's spikes no longer
        // pass for a jet ten times faster.
        float uInst = MathF.Abs(exitVelocity);
        float mixTime = 5f * _diameter / MathF.Max(5f, 0.5f * meanVelocity + 0.5f * _uSlow);
        _uSlow += (uInst - _uSlow) * MathF.Min(1f, 1f / (mixTime * _rate));
        float uAbs = _uSlow;
        float uRef = MathF.Max(8f, 0.5f * meanVelocity + 0.5f * uAbs);
        // Band centred on St = 0.2, two poles wide.
        float fc = Math.Clamp(0.2f * uRef / _diameter, 60f, 6000f);
        float a = OnePole.AlphaFor(fc, _rate);
        float n = (float)(_rng.NextDouble() * 2 - 1);
        _lp1 += a * (n - _lp1);
        _lp2 += a * (_lp1 - _lp2);
        float bp = (_lp1 - _lp2) / BandNormaliser(a);
        // A steady floor from the mean flow, plus the pulsating part: the slugs.
        float amp = 0.7f * meanVelocity + 0.6f * uAbs;
        float p = LighthillPressure(_diameter, amp, gasKelvin) * level * bp;
        // Nothing below 40 Hz belongs to a jet.
        float hpA = OnePole.AlphaFor(40f, _rate);
        _hp += hpA * (p - _hp);
        return p - _hp;
    }
}

internal static class OnePole
{
    /// <summary>One-pole coefficient for a corner frequency: y += a (x - y).</summary>
    public static float AlphaFor(float cornerHz, float rate)
    {
        float w = 2f * MathF.PI * Math.Clamp(cornerHz, 0.1f, rate * 0.49f) / rate;
        return Math.Clamp(1f - MathF.Exp(-w), 1e-5f, 1f);
    }
}
