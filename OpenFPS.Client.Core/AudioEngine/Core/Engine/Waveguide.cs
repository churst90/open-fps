using System;
using OpenFPS.Common;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  One-dimensional gas acoustics in REAL UNITS.
//
//  Pressures are pascals of acoustic pressure about the pipe's mean; lengths are metres; the gas has
//  a temperature and therefore a speed of sound and a density. Working in real units is not
//  pedantry: it is what lets the finite-amplitude behaviour — the steepening of a half-bar wave
//  front into a shock over a metre of header — be computed from the pulse the cylinder actually
//  produced, rather than dialled in. It is also what makes the radiated level come out in
//  decibels, so "how loud is this engine against that gunshot" is arithmetic and not taste.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

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
/// That amplitude dependence is finite-amplitude acoustics. A pressure crest travels at
/// c0 (1 + beta p'/(gamma p0)), beta = (gamma+1)/2, so the crests of a wave run ahead of its troughs
/// and the front steepens; for a 0.5 bar pulse in 600 C gas the shock forms within about 0.7 m —
/// inside the primary pipe of any header. (Matsumura et al., JSME 1990: "compression waves develop
/// into shock waves ... and generate large and jarring exhaust noises".) At idle the pulses are a
/// tenth of that and never steepen, which is why an engine under load is raspy and the same engine
/// idling is soft, and why a lossless linear delay line could never reproduce the difference.
///
/// The steepening is done by WRITING AT ARRIVAL TIME rather than reading at a fixed offset: each
/// injected sample is given the arrival time its own amplitude implies, the slots between the
/// previous arrival and this one are filled by interpolation, and if this sample arrives EARLIER
/// than the last one did it simply overwrites — the crest has overtaken the foot, and the profile
/// that results is the weak-shock solution with the overtaken part dissipated. Compressions
/// steepen, rarefactions stretch, and it costs a division per sample.
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

    /// <summary>
    /// The shortest a steepening front may be compressed to, in SAMPLES.
    ///
    /// A real shock is not a mathematical discontinuity: it has a thickness set by viscosity, and the
    /// top of it is absorbed before it leaves the pipe. Rendering it as one it was — arrivals a
    /// thousandth of a sample apart — put the whole front into a single sample, which is broadband
    /// energy this sample rate cannot carry, so it folded back down as crackle that got worse the
    /// harder the engine worked.
    ///
    /// Half a sample is measured, not chosen. Swept against the two things that trade off, on a stock
    /// car at 8000 rpm (steps) and a big block at 3500 WOT (the 800 Hz - 3 kHz band, which IS the
    /// rasp), with the averaging in Deposit also in place:
    ///
    /// | floor | largest step | steps over 0.15 | rasp    |
    /// |-------|--------------|-----------------|---------|
    /// | 0.001 | 0.48         | 170 /s          | -5.6 dB |
    /// | 0.5   | 0.24         | 11 /s           | -5.7 dB |
    /// | 0.75  | 0.23         | 39 /s           | -8.5 dB |
    /// | 1.0   | 0.22         | 0.25 /s         | -41 dB  |
    ///
    /// Half keeps the rasp exactly and takes twenty-five times the impulses out. A whole sample takes
    /// the rest of them and the rasp with it, which is the wrong trade: the rasp was never the last
    /// microsecond of the rise, but at one sample the front stops steepening at all.
    /// </summary>
    private static readonly double MinFrontSamples =
        double.TryParse(Environment.GetEnvironmentVariable("OPENFPS_SHOCK_FRONT"), out double f)
            ? Math.Clamp(f, 0.0, 4.0) : 0.5;

    /// <param name="maxDelaySamples">Longest delay this line will ever be asked for.</param>
    public WaveLine(int maxDelaySamples)
    {
        _len = Math.Max(8, maxDelaySamples + 8);
        _buf = new float[_len];
        _d0 = Math.Max((float)MinDelay, maxDelaySamples - 2);
        _tauPrev = _d0 - 1;
    }

    public void SetDelay(float samples)
    {
        _d0 = Math.Clamp(samples, (float)MinDelay, _len - 4f);
        // A line that has never been read is empty: its next arrival is simply the new delay away.
        // Without this the construction-time delay held every early write back until time caught up.
        if (_time == 0) _tauPrev = _d0 - 1;
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
        // A rarefaction slows the wave; a compression hurries it. Clamped, because beyond about
        // +150% the linear acoustics this rides on has long since stopped meaning anything.
        float speedUp = 1f + Math.Clamp(_kappa * v, -0.4f, 1.5f);
        double d = _d0 / speedUp;
        if (d < MinDelay) d = MinDelay;
        // A pipe is read and then written within one sample step, so "now" for the write is the
        // sample that was just read: the delay is then exactly what SetDelay asked for.
        double tau = (_time - 1) + d;

        // A crest that would overtake the foot is held to arrive just after it: the front compresses
        // to a shock and nothing already laid down is overwritten.
        if (tau < _tauPrev + MinFrontSamples) tau = _tauPrev + MinFrontSamples;
        {
            double span = tau - _tauPrev;
            long k0 = (long)Math.Floor(_tauPrev) + 1;
            long k1 = (long)Math.Floor(tau);
            // Never fill further ahead than the buffer holds.
            long kMax = _time + _len - 2;
            if (k1 > kMax) k1 = kMax;
            for (long k = k0; k <= k1; k++)
            {
                float f = (float)((k - _tauPrev) / span);
                Deposit(k, _vPrev + (v - _vPrev) * f);
            }
            // Compression: this sample's arrival fell INSIDE the slot the last one landed in, so the
            // loop above wrote nothing for it.
            //
            // It used to be dropped, and that is where the crackle came from. Several input samples
            // collapse into one slot at a steep front, and whichever happened to cross the boundary
            // last became the slot's value while the rest were thrown away — so the front arrived as
            // a single sample carrying an arbitrary crest, three times the amplitude of the waveform
            // around it, at the same crank angle every cycle. Measured on a stock car at 8000 rpm
            // that is a jump from -0.15 to +0.64 between adjacent samples, and a one-sample spike is
            // broadband: most of its energy is above Nyquist, where it folds back down as crackle
            // that gets worse the harder the engine works.
            //
            // Averaging them into the slot instead is the right answer twice over. It is the correct
            // anti-aliasing for compressing a signal in time — a box filter over exactly the samples
            // being compressed — and it is also what the physics says happens to them: the weak-shock
            // solution dissipates the overtaken part of the wave rather than letting one arbitrary
            // sample of it survive.
            if (k1 < k0)
            {
                long k = (long)Math.Floor(tau);
                if (k <= kMax && k >= _time) Deposit(k, v);
            }
        }
        _tauPrev = tau;
        _vPrev = v;
    }

    /// <summary>
    /// Puts a value into a slot, averaging with anything already deposited there this pass.
    ///
    /// Assignment would let the last writer win; the mean keeps every sample's contribution, which is
    /// what stops a compressed front turning into an impulse. The running count resets whenever the
    /// arrivals move on to a new slot.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Deposit(long k, float value)
    {
        int idx = (int)(((k % _len) + _len) % _len);
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

    public void Clear()
    {
        Array.Clear(_buf);
        _lp = 0f; _vPrev = 0f;
        _tauPrev = _time - 1 + _d0;
        _accSlot = long.MinValue; _accSum = 0; _accN = 0;
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
        // Sized for the coldest, slowest gas this pipe will ever hold (250 m/s is far below any
        // exhaust; the intake runs on ambient air at ~343).
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
        // nepers per metre. Small — a fraction of a decibel per metre — and rising as sqrt(f), so it
        // is a flat gain (matched at 150 Hz) and a one-pole (matched at 4 kHz) per traverse. Turbulent
        // mean flow roughly doubles it, which the multiplier from the profile is expected to include.
        float nu = Gas.KinematicViscosity(kelvin);
        float k = 1f / (Radius * _c) * MathF.Sqrt(MathF.PI * nu) * (1f + (gamma - 1f) / MathF.Sqrt(0.71f));
        k *= _wallLoss * (1f + 2f * m);
        float g1 = MathF.Exp(-k * Length * MathF.Sqrt(150f));
        float g2 = MathF.Exp(-k * Length * MathF.Sqrt(4000f));
        float ratio = MathF.Max(1e-3f, g2 / g1);
        float fc = ratio >= 0.999f ? _rate : 4000f / MathF.Sqrt(MathF.Max(1e-6f, 1f / (ratio * ratio) - 1f));
        float alpha = OnePole.AlphaFor(fc, _rate);
        // Fold the extra (muffler) loss in.
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

    public void Clear() { _fwd.Clear(); _bwd.Clear(); }
}

/// <summary>
/// The scattering junction where N pipes meet: pressure is common, volume flows sum to zero.
///
///     p_J = 2 sum(Y_k a_k) / (sum(Y_k) + Y_loss)
///     b_k = p_J - a_k
///
/// Lossless for Y_loss = 0 (Kelly-Lochbaum), which gives every reflection and all the cross-talk
/// between branches out of two lines of arithmetic. Y_loss is a resistive sink standing in for the
/// vortices a mean flow sheds at an abrupt junction — a real muffler inlet or collector at full load
/// loses a noticeable fraction of the acoustic energy that way, and without it the network rings.
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
/// 63 mm tailpipe in 200 C gas ka=1 lands near 2.2 kHz — the pipe is a mirror across the whole
/// engine-order range and only becomes an efficient radiator above that. Modelled as a one-pole
/// low-pass on the reflected wave with its corner at ka ~ 0.9, inverted, plus the end correction as
/// delay. Mean flow reduces the reflection by (1-M)/(1+M) — the tailpipe is brighter and less
/// resonant at full throttle, as it should be.
///
/// The radiated pressure is the monopole field of the volume velocity leaving the end:
///     p(r, t) = rho_air / (4 pi r) * dU/dt
/// and that time derivative is not a detail — it is the reason the sound outside a pipe is far
/// brighter than the pressure inside it, and it is what the previous model lacked.
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
        _endDelay = Math.Clamp(corr / soundSpeed * _rate, 0f, 12f);
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
    /// The monopole term is rho/(4 pi r) times dU/dt, which in the frequency domain is proportional
    /// to omega U: six decibels an octave, rising without limit. That is correct only while the
    /// opening is small against the wavelength. Past ka = 1 the pipe stops being a point source —
    /// the radiation resistance has already reached its asymptote of rho c per unit area, all of the
    /// wave leaves, and the radiated pressure is just the travelling wave rho c u, flat with
    /// frequency. Without the cap the derivative kept climbing and every engine that revs hard
    /// arrived top-heavy: a V10 at seventeen thousand put its spectral centroid at eight kilohertz,
    /// which is not a sound any engine makes.
    ///
    /// One pole at ka = 1 is exactly the correction: transparent below it, minus six an octave above,
    /// which cancels the derivative's plus six and leaves the asymptote flat. The corner is
    /// c/(2 pi a) — about 2 kHz for a three-inch tailpipe in hot gas — so a big lazy V8 whose orders
    /// all live under a kilohertz is untouched, and a race engine is not.
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
/// Broadband noise of the exhaust jet leaving the tailpipe.
///
/// Gordon (NASA SP-207): a plain pipe's radiated power goes as U^8 at high velocity (free jet mixing)
/// and U^6 at lower (edge dipoles at the lip and at internal obstructions); a muffled car tailpipe at
/// Mach 0.05-0.3 with baffles upstream is in the U^6 regime. Peak Strouhal number St = fD/U ~ 0.2.
/// So: pressure amplitude goes as U^3, the spectrum is a band that slides up with velocity, and the
/// whole thing is negligible at idle (2-3 m/s exit) and a real part of the roar at 150 m/s. It is
/// modulated by the instantaneous velocity because the flow is pulsating — the rush comes in slugs.
/// </summary>
internal sealed class JetNoise
{
    private readonly float _rate;
    private readonly Random _rng;
    private float _lp1, _lp2, _hp;
    private readonly float _diameter;

    public JetNoise(float rate, float diameterMetres, int seed)
    {
        _rate = rate;
        _diameter = diameterMetres;
        _rng = new Random(seed);
    }

    /// <summary>Pressure at one metre, pascals, given the exit velocity now (m/s) and the mean.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float Process(float exitVelocity, float meanVelocity, float level)
    {
        float uAbs = MathF.Abs(exitVelocity);
        float uRef = MathF.Max(8f, 0.5f * meanVelocity + 0.5f * uAbs);
        // Band centred on St = 0.2, two poles wide.
        float fc = Math.Clamp(0.2f * uRef / _diameter, 60f, 6000f);
        float a = OnePole.AlphaFor(fc, _rate);
        float n = (float)(_rng.NextDouble() * 2 - 1);
        _lp1 += a * (n - _lp1);
        _lp2 += a * (_lp1 - _lp2);
        float bp = _lp1 - _lp2;                 // one-pole band-pass-ish
        // A steady floor from the mean flow, plus the pulsating part: the slugs.
        float amp = 0.7f * meanVelocity + 0.6f * uAbs;
        // U^3 in pressure. Reference: 120 m/s at 1 m gives about 2 Pa (100 dB) for a plain 63 mm pipe.
        float r = amp / 120f;
        float p = 2.0f * r * r * r * level * bp * 3.5f;
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
