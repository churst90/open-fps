using OpenFPS.Common;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

/// <summary>
/// The intake tract, valve to open air: a runner per cylinder into a plenum, the throttle plate,
/// the airbox, and the snorkel that radiates.
///
/// The runners are waveguides like the exhaust's pipes; the plenum is a lumped volume filled through
/// the throttle and emptied by the runners. Manifold pressure is that mass balance, not a formula: a
/// shut plate pulls a deep vacuum at speed, a turbo raises the manifold with the airbox, a single on a
/// tiny plenum pulses. A hand-written manifold pressure could be pumped by the network's own resonance
/// into charges no throttle could pass.
///
/// The throttle flow's fluctuation is the sound that reaches the airbox and the snorkel: small at idle,
/// the full induction roar at wide open.
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
    private float _valveFlow, _valveFlowMean, _mouthFlowMean;
    /// <summary>Two hertz: under the firing rate of anything that runs, so only the mean is corrected.</summary>
    private readonly float _dcAlpha;
    private readonly float _meanAlpha;
    private float _runnerLossGain;

    // The plate's turbulence.
    private readonly Random _rng = new(20260916);
    private float _tnLp1, _tnLp2;

    /// <summary>
    /// How much of the engine's own pulsation gets past the compressor to the airbox: a bladed rotor
    /// reflects most of a plane wave, which is why a turbo intake is a whoosh and a whistle, not an
    /// induction honk. Without it a diesel (no plate, always wide open) sent the runners' 386 Hz
    /// quarter-wave straight out of the airbox as a note, worst on the overrun. Twenty decibels, the
    /// order of a centrifugal stage's plane-wave insertion loss; the mean flow is untouched.
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
        // rather than arriving as one gulp; what is left is the pulsation.
        _meanAlpha = OnePole.AlphaFor(12f, rate);
        _dcAlpha = OnePole.AlphaFor(2f, rate);
        // A turbo's compressor and a blower's rotors are both in the intake path: both get the barrier.
        _compressorBarrier = e.Induction == Induction.NaturallyAspirated ? 1f : 0.1f;
        SetThrottle(0f);
        UpdateGas(305f, Gas.Atmosphere);
    }

    private static float Circle(float diameterMm)
    {
        float r = diameterMm * 0.5e-3f;
        return MathF.PI * r * r;
    }

    /// <summary>Empties the runners, the airbox and the snorkel (ExhaustNetwork.Clear); the plenum keeps its air.</summary>
    public void Clear()
    {
        foreach (var r in _runner) r.Clear();
        _airbox.Clear();
        _snorkel.Clear();
        _end.Clear();
        _tnLp1 = _tnLp2 = 0f;
    }

    /// <summary>
    /// The plenum and its means from another tract of the same engine at another rate: the air in the
    /// manifold is what the next charges are drawn from, and an engine handed over with an empty or a
    /// full plenum gulps or starves for a fifth of a second.
    /// </summary>
    public void CopyLumpedFrom(IntakeNetwork o)
    {
        _plenumMass = o._plenumMass;
        _throttleOpen = o._throttleOpen;
        _throttleFlowMean = o._throttleFlowMean;
        _plenumMean = o._plenumMean;
        _valveFlow = o._valveFlow;
        _valveFlowMean = o._valveFlowMean;
        _mouthFlowMean = o._mouthFlowMean;
        UpdateGas(o._plenumK, o._airboxPressure);
    }

    public float RunnerImpedance(int cyl) => _runner[cyl].Impedance;

    /// <summary>Manifold pressure, pascals absolute: the state of the plenum.</summary>
    public float PlenumPressure => _plenumMass * Gas.R * _plenumK / _plenumVolume;

    /// <summary>Open fraction of the throttle plate, 0..1. A plate never quite shuts: about one per
    /// cent of the bore leaks past it, which is why an engine can idle at all with the pedal up.</summary>
    /// <param name="open">The pedal, 0..1.</param>
    /// <param name="bypass">Idle bypass as a fraction of the bore area — the idle control's air,
    /// 0 to about 0.04. Real idle air is one to three per cent of the bore.</param>
    public void SetThrottle(float open, float bypass = 0f)
        => _throttleOpen = 0.0025f + Math.Clamp(bypass, 0f, 0.06f) + 0.9975f * MathF.Pow(Math.Clamp(open, 0f, 1f), 1.6f);

    /// <summary>Retunes the tract for the intake air.</summary>
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

    /// <summary>
    /// The mass the cylinders pushed through their intake valves this sample, kg/s (positive into the
    /// runners, negative drawn from them). The plenum's mean flow is held to this: see Step.
    /// </summary>
    public void SetValveFlow(float kgPerSecond) => _valveFlow = kgPerSecond;

    public float ArrivedAtValve(int cyl) => _runner[cyl].ArriveNear();
    public void PushFromValve(int cyl, float p) => _runner[cyl].PushForward(p);

    /// <summary>Radiated pressure at one metre from the snorkel, pascals, this sample.</summary>
    public float Radiated { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Step()
    {
        // ── The plenum end of every runner ───────────────────────────────────────────────────
        // The mouth is a pressure release (the plenum is the waves' reference): what arrives comes
        // back inverted, and the flow across it is what the plenum gains or loses.
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
        // The waves carry the pulsation, not the mean: the runners' losses take the steady flow down
        // too, and charged by the waves alone the cylinders breathed 18 times what passed a shut
        // plate (torque on the overrun). So the mean across the mouths is held to the mean through the
        // valves, which is conservation of mass; the waves keep everything above it.
        _mouthFlowMean += _dcAlpha * (massIn / _dt - _mouthFlowMean);
        _valveFlowMean += _dcAlpha * (_valveFlow - _valveFlowMean);
        massIn += (_valveFlowMean - _mouthFlowMean) * _dt;

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
        // The throttle flow's fluctuation, plus the plenum's pulsation leaking through the plate's
        // gap: at idle the choked flow carries none of its own, but the pressure behind the plate does.
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
    /// A sharp-edged orifice in a duct is a dipole radiating into the plane wave only, so its power goes
    /// as U^4, not U^6 (Nelson and Morfey, 1981): the one Mach number in the term below. A power of U
    /// wrong decides whether a car is loudest under your foot or off it. The band is the gap's Strouhal
    /// number: fifty hertz through an open plate, ten kilohertz through a shut one.
    /// </summary>
    /// <param name="massFlow">Mass through the plate now, kg/s.</param>
    /// <param name="area">Open area of the plate now, m^2.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float ThrottleTurbulence(float massFlow, float area)
    {
        float level = _spec.FlowNoiseLevel;
        if (level <= 0f || area <= 1e-9f) return 0f;

        // A plate that is not in the way is not an orifice: the jet needs a pressure drop. Scaled by
        // duct velocity alone, a diesel (always wide open) got the turbulence of a throttle it does
        // not have, and its tract's 300-460 Hz modes turned it into a note ("a frequency, like a phone
        // interfering with a speaker"). The drop is about 0.6 of upstream at petrol idle, a few per
        // cent wide open, nothing on a diesel: induction hiss is a part-throttle sound.
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

        // Turbulence intensity at a sharp orifice is about ten per cent of the mean; the Mach number
        // carries the U^4 law.
        const float intensity = 0.10f;
        float mach = u / MathF.Max(1f, _airbox.SoundSpeed);
        return intensity * area * u * mach * band * level * 8f * restriction;
    }

    // The law's constants for air, worked out once rather than every sample (see EngineSynth.OrificeGas).
    private const float GammaAir = Gas.GammaAir;
    private static readonly float AirCrit = MathF.Pow(2f / (GammaAir + 1f), GammaAir / (GammaAir - 1f));
    private static readonly float AirRootGamma = MathF.Sqrt(GammaAir);
    private static readonly float AirChoked = MathF.Pow(2f / (GammaAir + 1f), (GammaAir + 1f) / (2f * (GammaAir - 1f)));

    /// <summary>Choked/subsonic orifice flow for air, kg/s.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float Orifice(float pUp, float tUp, float pDown, float area)
    {
        if (area <= 0f || pUp <= pDown) return 0f;
        float pr = pDown / pUp;
        float rt = MathF.Sqrt(Gas.R * tUp);
        if (pr <= AirCrit)
            return area * pUp * AirRootGamma * AirChoked / rt;
        const float gamma = Gas.GammaAir;
        float t1 = MathF.Pow(pr, 2f / gamma) - MathF.Pow(pr, (gamma + 1f) / gamma);
        return t1 <= 0f ? 0f : area * pUp * MathF.Sqrt(2f * gamma / (gamma - 1f) * t1) / rt;
    }
}
