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
        float uAb = (flow - _throttleFlowMean) / Gas.Density(_airboxPressure, _plenumK) + acoustic;
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
