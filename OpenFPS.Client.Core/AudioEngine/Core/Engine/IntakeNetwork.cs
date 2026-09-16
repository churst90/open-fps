using System;
using OpenFPS.Common;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

/// <summary>
/// The intake tract, valve to open air: a runner per cylinder into a plenum, the throttle plate,
/// the airbox, and the snorkel that radiates.
///
/// The runners are waveguides, the same as the exhaust's pipes. The plenum is a LUMPED volume: a
/// mass of air at a pressure, filled through the throttle and emptied by whatever the runners draw.
/// Manifold pressure is therefore not a formula in here — it is the balance between what the
/// throttle passes and what the engine breathes, so a shut plate pulls a deep vacuum at speed, a big
/// cam that cannot pull a vacuum does not, a turbo that raises the airbox pressure raises the
/// manifold with it, and a single cylinder on a tiny plenum makes the whole intake pulse because
/// there is nothing there to smooth it. All of that is mass conservation, and the earlier version,
/// which wrote the manifold pressure by hand and let an acoustic network breathe under it, could be
/// pumped by its own resonance into charges no throttle could have passed.
///
/// What crosses the throttle is a flow, and its fluctuation is the sound that reaches the airbox
/// and the snorkel — small at idle because the plate is almost shut, the full induction roar at
/// wide open, without anyone writing that rule down.
/// </summary>
internal sealed class IntakeNetwork
{
    private readonly IntakeSpec _spec;
    private readonly int _n;
    private readonly Pipe[] _runner;
    private readonly Pipe _airbox, _snorkel;
    private readonly OpenEnd _end;
    private readonly float _rate, _dt;
    private float _airDensity = 1.2f;

    // The plenum
    private readonly float _plenumVolume;
    private float _plenumMass;
    private float _plenumK = 305f;
    private readonly float _throttleArea;
    private float _throttleOpen;
    private float _airboxPressure = Gas.Atmosphere;
    private float _throttleFlowMean, _plenumMean = Gas.Atmosphere;
    private readonly float _meanAlpha;
    private float _runnerLossGain;

    // The plate's turbulence.
    private readonly Random _rng = new(20260916);
    private float _tnLp1, _tnLp2;

    /// <summary>
    /// How much of the engine's own pulsation gets past the COMPRESSOR to the airbox and out.
    ///
    /// On a turbocharged or blown engine there is a wheel in the way. Everything the cylinders do —
    /// the runner's quarter-wave, the plenum's breathing, the plate's turbulence — has to cross a
    /// bladed rotor spinning at a hundred thousand rpm before it can reach the snorkel, and a rotor
    /// is a wall to a plane wave: it reflects and scatters most of it. That is why a turbo engine's
    /// intake is a WHOOSH and a whistle rather than the induction honk of a naturally aspirated one,
    /// and the whistle is the compressor's own noise, generated downstream of the barrier.
    ///
    /// Leaving it out was audible and diesel-specific. The leak term below is scaled by the OPEN
    /// AREA of the plate, so on a petrol engine at part throttle almost nothing crosses — but a
    /// diesel has no plate at all and runs wide open for ever, so the runners' fixed 386 Hz
    /// quarter-wave went straight out of the airbox. A listener heard it as a note, worst on the
    /// overrun where no combustion covers it, and it survived silencing the exhaust, the valvetrain,
    /// the turbo whistle, the knock, the alternator and the throttle turbulence one at a time —
    /// because it was none of those, it was the engine breathing out through a hole that should
    /// have had a compressor in it.
    ///
    /// Twenty decibels, which is the order of a centrifugal stage's insertion loss for plane waves
    /// well below blade-passing. The MEAN flow is untouched: a compressor passes air, it is only
    /// pulsation it stops.
    /// </summary>
    private readonly float _compressorBarrier;

    public IntakeNetwork(EngineProfile e, float rate)
    {
        _spec = e.Intake;
        _n = e.Cylinders;
        _rate = rate;
        _dt = 1f / rate;
        var s = _spec;
        float runnerArea = Circle(s.RunnerDiameterMm);
        _runner = new Pipe[_n];
        for (int c = 0; c < _n; c++)
            _runner[c] = new Pipe(s.RunnerLengthMetres * (1f + 0.03f * MathF.Sin(c * 1.7f)), runnerArea, rate, 1.5f, 0.2f);

        _plenumVolume = MathF.Max(0.1e-3f, s.PlenumLitres * 1e-3f);
        _plenumMass = Gas.Density(Gas.Atmosphere, _plenumK) * _plenumVolume;
        _throttleArea = Circle(s.ThrottleDiameterMm);

        float airboxArea = _throttleArea * 5f;
        _airbox = new Pipe(MathF.Max(0.08f, s.AirboxLitres * 1e-3f / airboxArea), airboxArea, rate, 2f, 0f);
        float ab = Math.Clamp(s.Absorption, 0f, 1f);
        _airbox.SetExtraLoss(1f - 0.4f * ab, OnePole.AlphaFor(MathHelper.Lerp(8000f, 900f, ab), rate));
        _snorkel = new Pipe(s.SnorkelLengthMetres, Circle(s.SnorkelDiameterMm), rate, 2f, 0f);
        _end = new OpenEnd(rate);
        // The mean follows the throttle within about 15 ms, so a snapped pedal moves the mean flow
        // rather than arriving as one enormous gulp; what is left is the pulsation.
        _meanAlpha = OnePole.AlphaFor(12f, rate);
        // Both kinds of forced induction put a rotor in the INTAKE path — a turbo's compressor and a
        // blower's rotors alike — so both get the barrier. Only the exhaust side distinguishes them.
        _compressorBarrier = e.Induction == Induction.NaturallyAspirated ? 1f : 0.1f;
        SetThrottle(0f);
        UpdateGas(305f, Gas.Atmosphere);
    }

    private static float Circle(float diameterMm)
    {
        float r = diameterMm * 0.5e-3f;
        return MathF.PI * r * r;
    }

    public float RunnerImpedance(int cyl) => _runner[cyl].Impedance;

    /// <summary>Manifold pressure, pascals absolute: the state of the plenum.</summary>
    public float PlenumPressure => _plenumMass * Gas.R * _plenumK / _plenumVolume;
    /// <summary>Mass flow through the throttle, kg/s, smoothed.</summary>
    public float ThrottleFlow => _throttleFlowMean;

    /// <summary>Open fraction of the throttle plate, 0..1. A plate never quite shuts: about one per
    /// cent of the bore leaks past it, which is why an engine can idle at all with the pedal up.</summary>
    /// <param name="open">The pedal, 0..1.</param>
    /// <param name="bypass">Idle bypass as a fraction of the bore area — the idle control's air,
    /// 0 to about 0.04. Real idle air is one to three per cent of the bore.</param>
    public void SetThrottle(float open, float bypass = 0f)
        => _throttleOpen = 0.0025f + Math.Clamp(bypass, 0f, 0.06f) + 0.9975f * MathF.Pow(Math.Clamp(open, 0f, 1f), 1.6f);

    /// <param name="kelvin">Intake air temperature.</param>
    /// <param name="airboxPressure">Pressure upstream of the throttle, pascals — atmospheric, or
    /// more with a turbo or blower on it.</param>
    public void UpdateGas(float kelvin, float airboxPressure)
    {
        _plenumK = kelvin;
        _airboxPressure = airboxPressure;
        for (int c = 0; c < _n; c++) _runner[c].SetGas(kelvin, Gas.GammaAir, 0.03f);
        _airbox.SetGas(kelvin - 10f, Gas.GammaAir, 0f);
        _snorkel.SetGas(kelvin - 12f, Gas.GammaAir, 0.02f);
        _airDensity = Gas.Density(Gas.Atmosphere, 293f);
        _end.Configure(_snorkel.Radius, _snorkel.SoundSpeed, _snorkel.Density, _snorkel.Area, 0.02f);
        // A runner mouth is not a perfect pressure release: a little of what arrives is lost to the
        // plenum's walls and turbulence at the bell.
        _runnerLossGain = 0.94f;
    }

    public float ArrivedAtValve(int cyl) => _runner[cyl].ArriveNear();
    public void PushFromValve(int cyl, float p) => _runner[cyl].PushForward(p);

    /// <summary>Radiated pressure at one metre from the snorkel, pascals, this sample.</summary>
    public float Radiated { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Step()
    {
        // ── The plenum end of every runner ───────────────────────────────────────────────────
        // The plenum is large compared with a runner and its pressure is the reference the runner's
        // waves ride on, so the mouth is a pressure release: what arrives comes back inverted, and
        // the flow that crosses the mouth is what the plenum gains or loses.
        float rho = Gas.Density(PlenumPressure, _plenumK);
        float massIn = 0f;
        for (int c = 0; c < _n; c++)
        {
            var r = _runner[c];
            float a = r.ArriveFar();
            r.PushBackward(-a * _runnerLossGain);
            float u = 2f * a / r.Impedance;             // volume flow into the plenum
            massIn += rho * u * _dt;
        }

        // ── The throttle: an orifice between the airbox and the plenum ───────────────────────
        float pPlenum = PlenumPressure;
        float area = _throttleArea * _throttleOpen * 0.8f;
        float flow;
        if (_airboxPressure >= pPlenum) flow = Orifice(_airboxPressure, _plenumK, pPlenum, area);
        else flow = -Orifice(pPlenum, _plenumK, _airboxPressure, area);
        // Keep the plenum from overshooting the airbox within one sample.
        float equalise = 0.5f * MathF.Abs(_airboxPressure - pPlenum) / MathF.Max(pPlenum, 1e3f) * _plenumMass / _dt;
        flow = Math.Clamp(flow, -equalise, equalise);
        _plenumMass = MathF.Max(1e-6f, _plenumMass + massIn + flow * _dt);
        _throttleFlowMean += _meanAlpha * (flow - _throttleFlowMean);

        // ── What gets past the plate is what the airbox hears ────────────────────────────────
        // The fluctuating part of the throttle flow, drawn from the airbox, sent down the snorkel.
        // Plus the plenum's pulsation leaking acoustically through whatever gap the plate leaves —
        // the choked mean flow at idle carries no fluctuation of its own, but the pressure behind
        // the plate does, and some of that gets through.
        _plenumMean += _meanAlpha * (pPlenum - _plenumMean);
        float acoustic = (pPlenum - _plenumMean) * area / (Gas.Density(_airboxPressure, _plenumK) * 340f) * 0.5f;
        float uAb = ((flow - _throttleFlowMean) / Gas.Density(_airboxPressure, _plenumK) + acoustic
                  + ThrottleTurbulence(flow, area)) * _compressorBarrier;
        float aAb = _airbox.ArriveNear();
        _airbox.PushForward(aAb - _airbox.Impedance * uAb);   // drawing air is a rarefaction into the box
        {
            var (back, on) = Junction.Two(_airbox.ArriveFar(), _snorkel.ArriveNear(), _airbox.Admittance, _snorkel.Admittance, 0f);
            _airbox.PushBackward(back);
            _snorkel.PushForward(on);
        }
        float arriving = _snorkel.ArriveFar();
        var (reflected, uOut) = _end.Process(arriving);
        _snorkel.PushBackward(reflected);
        Radiated = _end.Radiate(uOut, _airDensity);
    }

    /// <summary>
    /// The broadband the plate makes, as a fluctuating volume flow into the airbox.
    ///
    /// A sharp-edged orifice in a duct is a dipole: the jet through it beats on the plate, and the
    /// plate pushes back on the air. Below the duct's cut-on frequency there is only the plane wave
    /// to radiate into, and none of the inefficiency a dipole suffers in free air — so the power goes
    /// as the fourth power of the velocity through the gap rather than the sixth (Nelson and Morfey,
    /// 1981), which is the second power in pressure, and that is the one Mach number in the term
    /// below. Getting this wrong by a power of U does not change a level, it changes whether a car is
    /// loudest under your foot or off it.
    ///
    /// The BAND is the Strouhal number of the gap: turbulence peaks where the velocity divided by the
    /// size of the hole puts it, so the same source is fifty hertz through an open plate and ten
    /// kilohertz through a shut one. Nothing here knows what kind of engine it is on.
    /// </summary>
    /// <param name="massFlow">Mass through the plate now, kg/s.</param>
    /// <param name="area">Open area of the plate now, m^2.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float ThrottleTurbulence(float massFlow, float area)
    {
        float level = _spec.FlowNoiseLevel;
        if (level <= 0f || area <= 1e-9f) return 0f;

        // A PLATE THAT IS NOT IN THE WAY IS NOT AN ORIFICE.
        //
        // This whole term is the dipole of a separated jet beating on a sharp edge, and a jet only
        // exists where there is a pressure drop to drive one. Scaling it by the duct velocity alone
        // was wrong in a way that shows up hardest on the engine that has no plate at all: a diesel
        // is pedal-is-fuel and runs its intake WIDE OPEN for ever, so it was being given the
        // turbulence of a throttle it does not have — and fed to a tract with modes at 300-460 Hz,
        // broadband comes back out as a NOTE. A listener heard it as "a frequency, like a phone
        // interfering with a speaker", loudest on the overrun where no combustion masks it.
        //
        // The drop across the plate is the honest measure of how much of an orifice it is: about
        // 0.6 of the upstream pressure on a petrol engine at idle, a few per cent at wide open, and
        // essentially nothing on a diesel at any time. It is also why induction HISS is a
        // part-throttle sound — at full throttle what you hear is the pulsation, not the plate.
        float pUp = MathF.Max(1e3f, _airboxPressure);
        float restriction = Math.Clamp((pUp - PlenumPressure) / pUp, 0f, 1f);
        if (restriction < 0.02f) return 0f;

        float rho = Gas.Density(MathF.Min(_airboxPressure, PlenumPressure), _plenumK);
        float u = MathF.Abs(massFlow) / MathF.Max(1e-6f, rho * area);
        if (u < 0.5f) return 0f;

        // Strouhal 0.2 on the hydraulic diameter of the gap.
        float d = MathF.Sqrt(4f * area / MathF.PI);
        float fc = Math.Clamp(0.2f * u / d, 20f, _rate * 0.4f);
        float a = OnePole.AlphaFor(fc, _rate);
        float n = (float)(_rng.NextDouble() * 2 - 1);
        _tnLp1 += a * (n - _tnLp1);
        _tnLp2 += a * (_tnLp1 - _tnLp2);
        float band = _tnLp1 - _tnLp2;

        // Turbulence intensity at a sharp orifice is ten per cent or so of the mean; the Mach number
        // is what makes it a SOUND rather than a fluctuation, and it is what carries the U^4 law.
        const float intensity = 0.10f;
        float mach = u / MathF.Max(1f, _airbox.SoundSpeed);
        return intensity * area * u * mach * band * level * 8f * restriction;
    }

    /// <summary>Choked/subsonic orifice flow for air, kg/s.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float Orifice(float pUp, float tUp, float pDown, float area)
    {
        if (area <= 0f || pUp <= pDown) return 0f;
        const float gamma = Gas.GammaAir;
        float pr = pDown / pUp;
        float crit = MathF.Pow(2f / (gamma + 1f), gamma / (gamma - 1f));
        float rt = MathF.Sqrt(Gas.R * tUp);
        if (pr <= crit)
            return area * pUp * MathF.Sqrt(gamma) * MathF.Pow(2f / (gamma + 1f), (gamma + 1f) / (2f * (gamma - 1f))) / rt;
        float t1 = MathF.Pow(pr, 2f / gamma) - MathF.Pow(pr, (gamma + 1f) / gamma);
        return t1 <= 0f ? 0f : area * pUp * MathF.Sqrt(2f * gamma / (gamma - 1f) * t1) / rt;
    }
}
