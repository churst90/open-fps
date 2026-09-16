using System;
using OpenFPS.Common;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

/// <summary>
/// An internal combustion engine, integrated sample by sample: every cylinder is a volume of gas
/// with a mass, an energy and two valves; the crank is a rotating mass driven by the pressure on the
/// pistons; the pipes on both sides are waveguides. The sound is whatever leaves the tailpipes,
/// the snorkel and the block.
///
/// What this buys over drawing pulses and pushing them into a filter:
///
///   * The exhaust pulse has the shape the valve and the cylinder give it. Blowdown is choked
///     orifice flow from a cylinder at four or five bar through a curtain area that follows the cam;
///     the cylinder empties, the flow unchokes, and the piston then sweeps the rest out. Its
///     duration is cylinder volume over valve area, so a big slow cylinder blows down longer than a
///     small quick one and the whole spectral envelope moves with the geometry.
///   * Load is not a volume knob. Manifold pressure sets the charge, the charge sets the pressure at
///     exhaust valve opening, that pressure sets the pulse — ten times the amplitude at full throttle
///     as at idle, and that amplitude then decides how hard the wave fronts steepen in the primaries.
///     Which is why a worked engine barks and an idling one burbles.
///   * The valve is a real boundary. An open valve looks into a cylinder, which absorbs or feeds the
///     wave depending on the pressures either side; a shut valve is a wall. Waves returning from the
///     collector during overlap push exhaust back INTO the cylinder — reversion — and the burnt gas
///     that stays dilutes the next charge. That dilution is what makes a big cam lope: it does not
///     have to be modelled as a pattern, because it happens.
///   * The crank speeds up on every power stroke and slows on every compression, by the amount the
///     gas torque and the inertia say, so firings are uneven in time at idle and even at speed, the
///     engine hunts when combustion is unstable, and a free rev climbs at the rate the flywheel allows.
///
/// Nothing here is a tone control. The cost is a few hundred operations per sample per cylinder,
/// which at 44.1 kHz is a few per cent of a core for a V8.
/// </summary>
public sealed class EngineSynth
{
    // ── Inputs, set by whoever is driving ───────────────────────────────────────────────────────

    /// <summary>The pedal, 0..1.</summary>
    public float Throttle { get; set; }
    /// <summary>Whether the ignition is on. Off, it turns over without firing.</summary>
    public bool Ignition { get; set; } = true;
    /// <summary>Whether the starter is engaged.</summary>
    public bool Starter { get; set; }
    /// <summary>Torque resisting the crank from whatever it is connected to, Nm. Positive resists.</summary>
    public float LoadTorque { get; set; }
    /// <summary>Extra rotating inertia the crank has to carry, kg m^2 — the car, reflected through
    /// the gearing. Zero with the clutch down.</summary>
    public float ExternalInertia { get; set; }

    // ── Outputs, valid after Step() ─────────────────────────────────────────────────────────────

    /// <summary>Pressure at one metre from the tailpipes, pascals.</summary>
    public float Exhaust { get; private set; }

    /// <summary>The part of <see cref="Exhaust"/> that came off the muffler case rather than out of
    /// the pipe, pascals at one metre. Diagnostic.</summary>
    public float ExhaustShell { get; private set; }

    /// <summary>...and the part that came out of the pipes. Diagnostic.</summary>
    public float ExhaustPipe { get; private set; }
    /// <summary>Pressure at one metre from the intake mouth, pascals.</summary>
    public float Intake { get; private set; }
    /// <summary>Pressure at one metre from the block: valvetrain, combustion through the metal, accessories.</summary>
    public float Block { get; private set; }

    public float Rpm => _omega * 60f / (2f * MathF.PI);

    /// <summary>Crank angle, degrees through the cycle. For instruments: an artefact that recurs at
    /// the same angle every cycle is a different bug from one that recurs at the same time.</summary>
    public float CrankDegrees => (float)(_theta % Profile.CycleDegrees);

    /// <summary>
    /// Sets the crank turning at a given speed without having driven it there.
    ///
    /// For placing a voice, not for driving one. A car that comes into earshot already doing three
    /// hundred kilometres an hour has an engine at nine thousand rpm, and starting that engine from
    /// rest and letting the driver chase the speed means a full spin-up — throttle wide, clutch
    /// slipping, the whole rev range — crammed into the eighty milliseconds it takes the speed
    /// filter to catch up. That is a loud swoop every time a car enters the mix, and it is heard as
    /// a pop.
    /// </summary>
    public void SpinTo(float rpm)
    {
        _omega = MathF.Max(0f, rpm) * 2f * MathF.PI / 60f;
        _rpmSlow = Rpm;
        _rpmFast = Rpm;
    }
    /// <summary>Crank angle within the cycle, degrees.</summary>
    public float CrankAngle => (float)_theta;
    /// <summary>Net torque from the gas on the crank this sample, Nm, scaled so full throttle at
    /// the torque peak gives the profile's peak torque.</summary>
    public float Torque { get; private set; }
    /// <summary>Mean exhaust mass flow, kg/s, smoothed over a few cycles.</summary>
    public float MassFlow => _massFlowLp;
    /// <summary>Manifold pressure, bar absolute.</summary>
    public float ManifoldBar => _map / Gas.Atmosphere;
    /// <summary>Whether combustion is happening — for the caller's log, not the physics.</summary>
    public bool Running => _omega > 20f && Ignition;
    /// <summary>Peak pressure in the primary this cycle, pascals, for the instruments.</summary>
    public float PortPeak { get; private set; }
    /// <summary>Combustion quality of the last event that fired, 0..1.</summary>
    public float LastBurnQuality { get; private set; } = 1f;
    /// <summary>Dilution of the last charge that fired, burnt fraction 0..1.</summary>
    public float LastDilution { get; private set; }
    /// <summary>Summed pressure at the valve ends this sample, pascals: the source before the pipes.</summary>
    public float PortSum => _exhaust.PortSum;
    /// <summary>What the idle governor is adding: bypass area as a fraction of the bore on a petrol
    /// engine (0..0.04), fuel on a diesel (0..1).</summary>
    public float IdleAir => _idleAir;
    /// <summary>The torque calibration applied, so the console can say what the model made.</summary>
    public float TorqueScale => _torqueScale;
    public float EvoPressureCalibratedBar { get; private set; }

    public readonly EngineProfile Profile;
    private readonly float _rate, _dt;
    private readonly Random _rng;
    private readonly ExhaustNetwork _exhaust;
    private readonly IntakeNetwork _intake;
    private readonly Cylinder[] _cyl;
    private readonly int _n;

    // Geometry
    private readonly float _crankRadius, _rodLength, _pistonArea, _clearanceVolume, _cycleDeg;
    private readonly float _gammaCyl = 1.33f;
    private readonly float _cv, _cp;
    private readonly float _heatScale;
    private readonly float _torqueScale;
    private readonly float _exhaustAreaMax, _intakeAreaMax;

    // Crank
    private double _theta;                     // degrees, [0, cycle)
    private float _omega;                      // rad/s
    private float _idleAir, _idleIntegral, _pedal, _rpmFast, _idleSparkTrim;
    private readonly float _idleAreaFrac;
    private bool _wasRunning;
    private float _rpmSlow;
    private float _map = Gas.Atmosphere;
    private float _spool;                      // turbo/blower, 0..1
    private float _portK, _intakeK = 305f;
    private float _massFlowAcc, _massFlowLp;
    private int _gasTick;
    private const int GasEvery = 64;
    /// <summary>1/tau for the running mean of valve flow: about four idle cycles.</summary>
    private const float MeanFlowRate = 1.5f;
    private readonly float _runnerVolume, _runnerArea, _primaryArea;

    // Combustion instability shared by the engine: see Weakness().
    private float _mixtureWalk;
    private float _lastLoad;

    // Block noise
    private float _knockHp, _knockPrev, _blockLp;
    private readonly ClickVoice _click;
    private double _whinePhase, _blowerPhase, _turboPhase;
    private float _turboNoiseLp;

    private struct Cylinder
    {
        public float Mass, Energy, BurntMass;  // kg, J, kg
        public float Volume, Pressure, Temp;
        public float Phase;                    // degrees after this cylinder's firing TDC
        public float LiftE, LiftI;             // metres
        public float ExhaustB, IntakeB;        // last outgoing wave, for the Newton start
        public float RunnerBurnt;              // burnt fraction of the gas in this cylinder's runner, 0..1
        public float ExhaustUMean, IntakeUMean; // slow mean volume velocity through each valve, m^3/s
        public float PortE, PortI, FlowE, FlowI; // diagnostics: port acoustic pressure, valve mass flow
        // This cycle's combustion, decided at intake valve closing.
        public float HeatTotal, SparkDeg, BurnDeg, BurnPrev, Quality, Dilution;
        public bool Burning, ChargeDecided, Misfired;
        public float PopAmp; public int PopLeft, PopLength;
        public float Trim;
        public bool EOpenWas, IOpenWas;
        public float PressurePrev;
    }

    public EngineSynth(EngineProfile e, float rate = 44100f, int seed = 11)
    {
        Profile = e;
        _rate = rate;
        _dt = 1f / rate;
        _rng = new Random(seed);
        _n = e.Cylinders;
        _cycleDeg = e.CycleDegrees;

        _crankRadius = e.StrokeMm * 0.5e-3f;
        _rodLength = _crankRadius * e.RodRatio;
        float bore = e.BoreMm * 1e-3f;
        _pistonArea = MathF.PI * 0.25f * bore * bore;
        float swept = _pistonArea * 2f * _crankRadius;
        _clearanceVolume = swept / MathF.Max(1.5f, e.CompressionRatio - 1f);
        _cv = Gas.R / (_gammaCyl - 1f);
        _cp = _cv + Gas.R;
        _exhaustAreaMax = ValveArea(e.ExhaustValve, e.ExhaustCam.MaxLiftMm * 1e-3f);
        _intakeAreaMax = ValveArea(e.IntakeValve, e.IntakeCam.MaxLiftMm * 1e-3f);

        _exhaust = new ExhaustNetwork(e, rate, seed);
        _intake = new IntakeNetwork(e, rate);
        {
            // The bypass area that passes the engine's idle air, as a fraction of the throttle bore.
            float idleFlow = e.IdleMapBar * 1.17f * e.DisplacementLitres * 1e-3f * e.IdleRpm / (e.Strokes == 2 ? 60f : 120f) * 0.85f;
            float perArea = Gas.Atmosphere * 0.685f / MathF.Sqrt(Gas.R * 305f);
            float rt = e.Intake.ThrottleDiameterMm * 0.5e-3f;
            _idleAreaFrac = Math.Clamp(idleFlow / perArea / (MathF.PI * rt * rt * 0.8f), 0.001f, 0.05f);
        }
        {
            float rr = e.Intake.RunnerDiameterMm * 0.5e-3f;
            _runnerArea = MathF.PI * rr * rr;
            _runnerVolume = _runnerArea * e.Intake.RunnerLengthMetres;
            float rp = e.Exhaust.PrimaryDiameterMm * 0.5e-3f;
            _primaryArea = MathF.PI * rp * rp;
        }
        _click = new ClickVoice(rate, seed + 5);

        _cyl = new Cylinder[_n];
        for (int c = 0; c < _n; c++)
        {
            ref var cy = ref _cyl[c];
            // At rest, every cylinder is at ambient at whatever volume its crank angle gives it.
            float phase0 = (float)(0.0 - e.FiringAngles[c]);
            if (phase0 < 0f) phase0 += _cycleDeg;
            cy.Phase = phase0;
            cy.Volume = VolumeAt(phase0);
            cy.Temp = 300f;
            cy.Mass = Gas.Density(Gas.Atmosphere, cy.Temp) * cy.Volume;
            cy.BurntMass = 0f;                            // it is air, until it has burnt once
            cy.Energy = cy.Mass * _cv * cy.Temp;
            cy.Pressure = Gas.Atmosphere;
            cy.PressurePrev = cy.Pressure;
            // Real cylinders are not identical: a few per cent, fixed for the life of the engine.
            cy.Trim = 1f + 0.04f * MathF.Sin(c * 2.1f + 0.7f);
            cy.Quality = 1f;
        }

        // Calibrate the heat released per kilogram of charge so that a full-load closed cycle at the
        // torque peak produces the profile's peak torque. Everything else — the pressure at exhaust
        // valve opening, and so the pulse — follows from the geometry and the cam timing, and is
        // reported rather than asked for. (Scaling the torque afterwards was tried, and it scaled the
        // compression resistance too, so the starter could not get a big block over top dead centre.)
        _heatScale = CalibrateHeat();
        EvoPressureCalibratedBar = ClosedCycleEvoBar(_heatScale, e.PeakTorqueRpm, out _);
        _torqueScale = 1f;

        _portK = e.Exhaust.GasCelsiusIdle + 273.15f;
        _rpmSlow = 0f;
    }

    // ── Geometry helpers ────────────────────────────────────────────────────────────────────────

    /// <summary>Cylinder volume at a crank angle, degrees from TDC.</summary>
    private float VolumeAt(float deg)
    {
        float phi = deg * (MathF.PI / 180f);
        float s = MathF.Sin(phi);
        float x = _crankRadius * (1f - MathF.Cos(phi)) + _rodLength - MathF.Sqrt(MathF.Max(0f, _rodLength * _rodLength - _crankRadius * _crankRadius * s * s));
        return _clearanceVolume + _pistonArea * x;
    }

    /// <summary>dx/dphi of the piston, metres per radian: the lever the gas pushes on.</summary>
    private float Lever(float deg)
    {
        float phi = deg * (MathF.PI / 180f);
        float s = MathF.Sin(phi), c = MathF.Cos(phi);
        float root = MathF.Sqrt(MathF.Max(1e-9f, _rodLength * _rodLength - _crankRadius * _crankRadius * s * s));
        return _crankRadius * s + _crankRadius * _crankRadius * s * c / root;
    }

    /// <summary>Effective flow area of a valve set at a lift: the curtain, capped by the port.</summary>
    private static float ValveArea(ValveSpec v, float lift)
    {
        float d = v.DiameterMm * 1e-3f;
        float curtain = MathF.PI * d * MathF.Min(lift, d * 0.25f);
        return v.DischargeCoefficient * v.Count * curtain;
    }

    /// <summary>Valve lift from a cam lobe at some crank angle relative to its centreline, metres.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float Lift(CamLobe lobe, float degFromCentre)
    {
        float d = degFromCentre;
        float half = _cycleDeg * 0.5f;
        if (d > half) d -= _cycleDeg; else if (d < -half) d += _cycleDeg;
        float u = MathF.Abs(d) / (lobe.DurationDegrees * 0.5f);
        if (u >= 1f) return 0f;
        float w = MathF.Cos(MathF.PI * 0.5f * u);
        float q = 0.6f + 2f * lobe.RampFraction;
        return lobe.MaxLiftMm * 1e-3f * MathF.Pow(w * w, q);
    }

    // ── Calibration ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs one closed cycle from intake closing to exhaust opening at full load, at a
    /// coarse angular step, and returns the pressure at EVO in bar.</summary>
    private float ClosedCycleEvoBar(float heatScale, float rpm, out float imep)
    {
        var e = Profile;
        float ivc = e.IntakeCam.ClosesDegrees - _cycleDeg;     // degrees ATDC-firing, negative
        float evo = e.ExhaustCam.OpensDegrees;
        float V = VolumeAt(ivc);
        float T = 330f;
        float m = Gas.Density(Gas.Atmosphere, T) * V;
        float E = m * _cv * T;
        float p = m * Gas.R * T / V;
        float Q = m * FuelEnergyPerKg * heatScale;
        float spark = SparkAngle(rpm, 1f);
        float burn = BurnDuration(rpm, 0f, 1f);
        float work = 0f;
        float xbPrev = 0f;
        const float step = 0.5f;
        for (float a = ivc; a < evo; a += step)
        {
            float Vn = VolumeAt(a + step);
            float dV = Vn - V;
            float xb = Wiebe((a + step) - spark, burn, e.Fuel);
            float dQ = Q * (xb - xbPrev);
            xbPrev = xb;
            E += dQ - p * dV;
            T = E / (m * _cv);
            V = Vn;
            p = m * Gas.R * T / V;
            work += p * dV;
        }
        // Pumping loop, roughly: the exhaust and intake strokes at (p_exh - MAP) over the swept volume.
        float swept = _pistonArea * 2f * _crankRadius;
        imep = work / swept;
        return p / 1e5f;
    }

    private float CalibrateHeat()
    {
        // Secant on the scale for the target torque; the relation is close to linear.
        float target = MathF.Max(5f, Profile.PeakTorqueNm);
        float s0 = 0.5f, s1 = 1.0f;
        float f0 = ClosedCycleTorque(Profile.PeakTorqueRpm, s0) - target;
        float f1 = ClosedCycleTorque(Profile.PeakTorqueRpm, s1) - target;
        for (int i = 0; i < 12 && MathF.Abs(f1) > 0.2f; i++)
        {
            float s2 = s1 - f1 * (s1 - s0) / MathF.Max(1e-6f, MathF.Abs(f1 - f0)) * MathF.Sign(f1 - f0);
            s2 = Math.Clamp(s2, 0.2f, 1.6f);
            if (MathF.Abs(s2 - s1) < 1e-5f) break;
            s0 = s1; f0 = f1;
            s1 = s2; f1 = ClosedCycleTorque(Profile.PeakTorqueRpm, s1) - target;
        }
        return s1;
    }

    private float ClosedCycleTorque(float rpm, float heatScale)
    {
        ClosedCycleEvoBar(heatScale, rpm, out float imep);
        float swept = _pistonArea * 2f * _crankRadius;
        float perCycle = imep * swept * _n;               // J per engine cycle
        float torque = perCycle / (_cycleDeg * MathF.PI / 180f);
        return torque - Friction(rpm);
    }

    private float Friction(float rpm) => Profile.FrictionNm + Profile.FrictionNmPerKrpm * rpm * 1e-3f;

    /// <summary>Lower heating value of the charge per kilogram of it, J/kg: petrol at 14.7:1 gives
    /// 44 MJ/kg of fuel over 15.7 kg of mixture, times the fraction a real burn releases.</summary>
    private const float FuelEnergyPerKg = 2.8e6f * 0.92f;

    // ── Combustion timing ───────────────────────────────────────────────────────────────────────

    /// <summary>Ignition angle, degrees ATDC (negative is advance), from speed and load.</summary>
    private float SparkAngle(float rpm, float load)
    {
        if (Profile.Fuel == FuelType.Diesel) return -(4f + 6f * MathF.Min(1f, rpm / 2500f));
        float adv = 8f + 22f * MathF.Min(1f, rpm / 3200f) + 6f * (1f - load) + _idleSparkTrim;
        return -MathF.Max(-5f, adv);
    }

    /// <summary>Burn duration in crank degrees: longer when diluted, a little longer at speed.</summary>
    private float BurnDuration(float rpm, float dilution, float load)
    {
        if (Profile.Fuel == FuelType.Diesel) return 42f + 12f * MathF.Min(1f, rpm / 3000f);
        return 42f + 45f * Math.Clamp(dilution * 3f, 0f, 1f) + 12f * MathF.Min(1f, rpm / 6000f) + 10f * (1f - load);
    }

    /// <summary>Burnt mass fraction at an angle after ignition: Wiebe, a=5, m=2. A diesel gets a
    /// premixed spike over the first few degrees before the diffusion burn — the knock.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float Wiebe(float degAfterSpark, float duration, FuelType fuel)
    {
        if (degAfterSpark <= 0f) return 0f;
        if (fuel == FuelType.Diesel)
        {
            float pre = 1f - MathF.Exp(-5f * MathF.Pow(Math.Clamp(degAfterSpark / 7f, 0f, 1f), 2.5f));
            float dif = 1f - MathF.Exp(-5f * MathF.Pow(Math.Clamp(degAfterSpark / duration, 0f, 1f), 1.6f));
            return 0.22f * pre + 0.78f * dif;
        }
        float u = Math.Clamp(degAfterSpark / duration, 0f, 1f);
        return 1f - MathF.Exp(-5f * u * u * u);
    }

    // ── The step ────────────────────────────────────────────────────────────────────────────────

    /// <summary>One sample of engine.</summary>
    /// <remarks>
    /// AggressiveOptimization, here and on the rest of the per-sample path, because of what a MAP
    /// LOAD does to the JIT. .NET starts every method at unoptimized tier-0 and promotes it after
    /// thirty calls PLUS a hundred-millisecond quiet period that RESTARTS whenever other methods are
    /// being jitted — and a map load jits continuously for seconds. So the engine loop stays at
    /// tier-0 for exactly as long as the load lasts. Measured on nascar_v8 with the cost spike:
    /// 7.8x realtime settled, 4.0x with tier-0 pinned (DOTNET_TC_CallCountingDelayMs=100000), which
    /// is 12.8% of a core per voice against 25%. Half the machine's engine capacity, during the one
    /// second it is most needed.
    ///
    /// Targeted rather than TieredCompilation=false for the whole client: turning tiering off would
    /// also make the JIT do MORE work during the load, which is the thing competing for the cores.
    /// Small helpers do not need their own attribute — an optimized caller inlines them. The ones
    /// that are too big to inline and run per sample carry it themselves.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Step()
    {
        var e = Profile;
        float rpm = Rpm;

        // ── Slow state: gas temperatures, manifold pressure, governor ────────────────────────
        if (_gasTick == 0) UpdateSlow(rpm);
        _gasTick = _gasTick + 1 == GasEvery ? 0 : _gasTick + 1;

        // ── Crank ────────────────────────────────────────────────────────────────────────────
        double before = _theta;
        _theta += _omega * (180.0 / Math.PI) * _dt;
        if (_theta >= _cycleDeg) _theta -= _cycleDeg;

        float gasTorque = 0f;
        float portPeak = 0f;
        float knock = 0f;
        float blockLow = 0f;
        float massOut = 0f;
        _map = _intake.PlenumPressure;
        float load = Math.Clamp((_map / Gas.Atmosphere - 0.3f) / 0.7f, 0f, 1f);
        if (e.Fuel == FuelType.Diesel) load = _pedal;
        float intakeK = _intakeK;
        bool firing = Ignition && _omega > 5f;

        for (int c = 0; c < _n; c++)
        {
            ref var cy = ref _cyl[c];
            float phase = (float)(_theta - e.FiringAngles[c]);
            if (phase < 0f) phase += _cycleDeg;
            float phasePrev = cy.Phase;
            cy.Phase = phase;
            bool wrapped = phase < phasePrev;

            // Volume and the work of moving the piston.
            float V = VolumeAt(phase);
            float dV = V - cy.Volume;
            cy.Volume = V;

            // Valves.
            float liftE = Lift(e.ExhaustCam, phase - e.ExhaustCam.CentrelineDegrees);
            float liftI = Lift(e.IntakeCam, phase - e.IntakeCam.CentrelineDegrees);
            cy.LiftE = liftE; cy.LiftI = liftI;

            // ── Decide this cycle's burn at intake valve closing ─────────────────────────
            bool intakeOpen = liftI > 1e-5f;
            if (cy.IOpenWas && !intakeOpen)
            {
                DecideCharge(ref cy, rpm, load, firing);
            }
            // Ignite. Spark angles are BTDC, so negative; put them on the cycle's own scale.
            float sparkAbs = cy.SparkDeg < 0f ? cy.SparkDeg + _cycleDeg : cy.SparkDeg;
            if (cy.ChargeDecided && !cy.Burning && Crossed(phasePrev, phase, wrapped, sparkAbs))
            {
                cy.Burning = true;
                cy.BurnPrev = 0f;
                cy.ChargeDecided = false;
            }
            float dQ = 0f;
            if (cy.Burning)
            {
                float since = phase - sparkAbs;
                if (since < 0f) since += _cycleDeg;
                float xb = Wiebe(since, cy.BurnDeg, e.Fuel);
                dQ = cy.HeatTotal * (xb - cy.BurnPrev);
                cy.BurnPrev = xb;
                if (xb >= 0.999f || since > cy.BurnDeg + 5f)
                {
                    cy.Burning = false;
                    cy.BurntMass = cy.Mass;
                }
            }

            // ── Energy: work and heat ────────────────────────────────────────────────────
            cy.Energy += dQ - cy.Pressure * dV;

            // ── Exhaust valve: the boundary between cylinder and primary ─────────────────
            float aE = _exhaust.ArrivedAtValve(c);
            float bE;
            if (liftE > 1e-5f)
            {
                float area = ValveArea(e.ExhaustValve, liftE);
                float Z = _exhaust.PrimaryImpedance(c);
                float rhoPipeK = _portK;
                float capE = 0.6f * _exhaust.PrimarySoundSpeed(c) * _primaryArea;
                bE = SolveValve(aE, cy.ExhaustB, Z, area, cy.Pressure, cy.Temp, rhoPipeK, Gas.GammaExhaust, cy.Mass, _dt, 0f, capE, Gas.Atmosphere, out float mdot);
                cy.ExhaustB = bE;
                cy.ExhaustUMean += (mdot / Gas.Density(Gas.Atmosphere, rhoPipeK) - cy.ExhaustUMean) * _dt * MeanFlowRate;
                cy.PortE = aE + bE; cy.FlowE = mdot;
                // mdot > 0 leaves the cylinder.
                float dm = mdot * _dt;
                if (dm > 0f)
                {
                    float frac = MathF.Min(0.5f, dm / MathF.Max(1e-9f, cy.Mass));
                    float leaving = cy.Mass * frac;
                    cy.Energy -= leaving * _cp * cy.Temp;
                    cy.BurntMass -= leaving * (cy.BurntMass / MathF.Max(1e-9f, cy.Mass));
                    cy.Mass -= leaving;
                    massOut += leaving;
                }
                else if (dm < 0f)
                {
                    // Reversion: hot burnt gas from the pipe comes back in.
                    float entering = -dm;
                    cy.Energy += entering * _cp * rhoPipeK;
                    cy.Mass += entering;
                    cy.BurntMass += entering;
                }
                portPeak = MathF.Max(portPeak, MathF.Abs(aE + bE));
            }
            else
            {
                bE = aE;                                   // shut: a wall
                cy.ExhaustB = aE;
                cy.ExhaustUMean -= cy.ExhaustUMean * _dt * MeanFlowRate;
            }
            // Overrun pops: unburnt charge from a misfire or a closed-throttle high-rpm cycle lighting
            // off in the hot pipe. Enters at the port because that is where it happens — as a pulse a
            // couple of milliseconds long, since a single-sample step was a click, not a bang.
            if (cy.Misfired && liftE > 1e-4f && cy.PopLeft <= 0 && _rng.NextDouble() < 0.6 * _dt * 40.0 * e.Exhaust.OverrunPopRate / 6f)
            {
                cy.PopAmp = (0.12f + 0.2f * (float)_rng.NextDouble()) * 1e5f;
                cy.PopLeft = cy.PopLength = (int)(_rate * (0.0015f + 0.002f * (float)_rng.NextDouble()));
                cy.Misfired = false;
            }
            if (cy.PopLeft > 0)
            {
                float u = 1f - cy.PopLeft / (float)cy.PopLength;
                bE += cy.PopAmp * 0.5f * (1f - MathF.Cos(2f * MathF.PI * u));
                cy.PopLeft--;
            }
            _exhaust.PushFromValve(c, bE);

            // ── Intake valve: the boundary between runner and cylinder ───────────────────
            float aI = _intake.ArrivedAtValve(c);
            float bI;
            if (intakeOpen)
            {
                float area = ValveArea(e.IntakeValve, liftI);
                float Z = _intake.RunnerImpedance(c);
                // The runner sits at manifold pressure; the wave rides on top of that.
                float capI = 0.3f * 350f * _runnerArea;
                bI = SolveValve(aI, cy.IntakeB, Z, area, cy.Pressure, cy.Temp, intakeK, Gas.GammaAir, cy.Mass, _dt, 0f, capI, _map, out float mdotOut);
                cy.IntakeB = bI;
                cy.IntakeUMean += (mdotOut / Gas.Density(_map, intakeK) - cy.IntakeUMean) * _dt * MeanFlowRate;
                cy.PortI = aI + bI; cy.FlowI = mdotOut;
                float dm = mdotOut * _dt;                  // positive = cylinder to runner
                // The runner is a mixed reservoir of fixed mass: what the cylinder pushes into it
                // raises its burnt fraction and displaces the same mass on toward the plenum; what
                // the cylinder draws from it carries that fraction and is replaced from the plenum
                // with clean air. That is the reversion that dilutes a big cam's idle, bounded the
                // way a real runner bounds it — a plug model marked whole charges as burnt.
                float runnerMass = MathF.Max(1e-6f, _runnerVolume * Gas.Density(_map, intakeK));
                if (dm < 0f)
                {
                    float entering = -dm;
                    float burntIn = entering * cy.RunnerBurnt;
                    cy.Energy += entering * _cp * MathHelper.Lerp(intakeK, 0.6f * _portK, cy.RunnerBurnt);
                    cy.Mass += entering;
                    cy.BurntMass += burntIn;
                    cy.RunnerBurnt = MathF.Max(0f, cy.RunnerBurnt - burntIn / runnerMass);
                }
                else if (dm > 0f)
                {
                    float frac = MathF.Min(0.5f, dm / MathF.Max(1e-9f, cy.Mass));
                    float leaving = cy.Mass * frac;
                    float burntShare = cy.BurntMass / MathF.Max(1e-9f, cy.Mass);
                    cy.Energy -= leaving * _cp * cy.Temp;
                    cy.BurntMass -= leaving * burntShare;
                    cy.Mass -= leaving;
                    cy.RunnerBurnt = Math.Clamp(cy.RunnerBurnt + leaving * (burntShare - cy.RunnerBurnt) / runnerMass, 0f, 1f);
                }
            }
            else
            {
                bI = aI;
                cy.IntakeB = aI;
                cy.IntakeUMean -= cy.IntakeUMean * _dt * MeanFlowRate;
                // What was pushed into the runner mixes on into the plenum and is gone.
                cy.RunnerBurnt *= 1f - 2.5f * _dt;
            }
            _intake.PushFromValve(c, bI);

            // ── State ────────────────────────────────────────────────────────────────────
            cy.Mass = MathF.Max(1e-7f, cy.Mass);
            cy.BurntMass = Math.Clamp(cy.BurntMass, 0f, cy.Mass);
            cy.Temp = Math.Clamp(cy.Energy / (cy.Mass * _cv), 200f, 3200f);
            cy.Energy = cy.Mass * _cv * cy.Temp;
            cy.PressurePrev = cy.Pressure;
            cy.Pressure = cy.Mass * Gas.R * cy.Temp / cy.Volume;

            // ── Torque on the crank ──────────────────────────────────────────────────────
            gasTorque += (cy.Pressure - Gas.Atmosphere) * _pistonArea * Lever(phase);

            // ── What the block radiates ──────────────────────────────────────────────────
            // Combustion through the metal: the rate of pressure rise, which for a petrol engine
            // is modest and for a diesel is the sound.
            if (cy.Burning) knock += (cy.Pressure - cy.PressurePrev) * _rate;
            blockLow += cy.Pressure - Gas.Atmosphere;

            // Valvetrain: a tick as each valve leaves its seat and a louder one as it lands.
            bool eOpen = liftE > 1e-5f;
            if (eOpen != cy.EOpenWas) _click.Trigger(eOpen ? 0.5f : 1f, 3600f + 400f * (c % 3));
            if (intakeOpen != cy.IOpenWas) _click.Trigger(intakeOpen ? 0.45f : 0.9f, 3100f + 300f * (c % 4));
            cy.EOpenWas = eOpen; cy.IOpenWas = intakeOpen;
        }

        // ── Crank dynamics ───────────────────────────────────────────────────────────────────
        float friction = Friction(rpm) * (_omega > 0.5f ? 1f : 0f);
        float starter = 0f;
        if (Starter)
        {
            // A DC motor: full torque stalled, none at twice the cranking speed.
            float free = e.CrankingRpm * 2.2f;
            starter = StarterTorque() * MathF.Max(0f, 1f - rpm / free);
        }
        float net = gasTorque * _torqueScale + starter - friction - LoadTorque;
        float J = e.InertiaKgM2 + MathF.Max(0f, ExternalInertia);
        _omega += net / J * _dt;
        if (_omega < 0f) _omega = 0f;
        Torque = gasTorque * _torqueScale - friction;
        _rpmSlow += (Rpm - _rpmSlow) * MathF.Min(1f, _dt * 12f);

        _massFlowAcc += massOut;
        PortPeak = MathF.Max(PortPeak * 0.9995f, portPeak);

        // ── Pipes ────────────────────────────────────────────────────────────────────────────
        _exhaust.Step();
        _intake.Step();
        Exhaust = _exhaust.Radiated;
        ExhaustShell = _exhaust.ShellRadiated;
        ExhaustPipe = _exhaust.PipeRadiated;
        Intake = _intake.Radiated * e.Intake.Level;

        // ── The block ────────────────────────────────────────────────────────────────────────
        // Knock: the pressure-rise rate, high-passed, through the metal.
        float kh = OnePole.AlphaFor(600f, _rate);
        _knockHp += kh * (knock - _knockHp);
        // Scaled so a truck diesel under load radiates about 95 dB of knock at a metre and a petrol
        // engine's is buried; soft-limited because a misfire's pressure jump is not the block's sound.
        float knockRaw = (knock - _knockHp) * e.Mechanical.CombustionKnock * 4.0e-9f;
        float knockOut = 2f * MathF.Tanh(knockRaw * 0.5f);

        // The low thud of the cylinders reaching the air through the block and the mounts — and it
        // used to be most of what a block radiated, which had the diesels backwards.
        //
        // Measured, a truck six's block came out 94 per cent below 200 Hz with 1.7 per cent between
        // 800 Hz and 2.5 kHz. That is not what a big diesel sounds like: injector knock and
        // valvetrain clatter live between about 500 Hz and 4 kHz and they are the whole of what makes
        // one recognisable AS a diesel. Heard on the track it was a formless low roar with no engine
        // in it — the listener's words were "loud white noise ... I expected to hear more of the
        // engine", and the arithmetic agreed with him.
        //
        // The two paths are not alike and should not have been scaled alike. The thud is
        // STRUCTURE-BORNE: cylinder pressure into the block, through rubber mounts, into a chassis,
        // and every one of those junctions is a mismatch that reflects most of the energy back. The
        // knock and the clatter radiate straight off the block's own surfaces into the air. Weighting
        // the indirect path above the direct one is what buried the engine.
        float bl = OnePole.AlphaFor(180f, _rate);
        _blockLp += bl * (blockLow - _blockLp);
        float thud = _blockLp * 5.0e-8f;
        float mech = _click.Process() * e.Mechanical.ValvetrainLevel * 3.0f
                   * (0.6f + 0.4f * MathF.Min(1f, rpm / 3000f));
        float whine = 0f;
        var m = e.Mechanical;
        if (m.AccessoryWhineLevel > 0f && _omega > 1f)
        {
            _whinePhase += m.AccessoryWhineOrder * rpm / 60.0 / _rate;
            if (_whinePhase > 1.0) _whinePhase -= 1.0;
            whine += (float)(Math.Sin(_whinePhase * 2 * Math.PI) + 0.3 * Math.Sin(_whinePhase * 4 * Math.PI))
                   * m.AccessoryWhineLevel * 0.045f * MathF.Min(1f, rpm / 2500f);
        }
        if (m.BlowerWhineLevel > 0f && m.BlowerWhineOrder > 0f && _omega > 1f)
        {
            _blowerPhase += m.BlowerWhineOrder * rpm / 60.0 / _rate;
            if (_blowerPhase > 1.0) _blowerPhase -= 1.0;
            whine += (float)(Math.Sin(_blowerPhase * 2 * Math.PI) + 0.5 * Math.Sin(_blowerPhase * 4 * Math.PI))
                   * m.BlowerWhineLevel * 0.12f * (0.3f + 0.7f * load);
        }
        if (m.TurboWhistleLevel > 0f)
        {
            float shaftHz = 2000f + 9000f * _spool;
            _turboPhase += shaftHz / _rate;
            if (_turboPhase > 1.0) _turboPhase -= 1.0;
            float n = (float)(_rng.NextDouble() * 2 - 1);
            _turboNoiseLp += 0.08f * (n - _turboNoiseLp);
            whine += ((float)Math.Sin(_turboPhase * 2 * Math.PI) * 0.4f + _turboNoiseLp * 2.5f)
                   // A big truck turbo is not a detail you strain for: at 2.2 bar it is the loudest
                   // single thing about the engine on the way out of a corner. It was rendering
                   // about fifteen decibels under the block's thud, which is inaudible beside it.
                   * m.TurboWhistleLevel * 0.28f * _spool * _spool;
        }
        Block = knockOut + thud + mech + whine;
    }

    private float StarterTorque()
    {
        // Sized to the engine, as a starter is: it has to push a cylinder over compression from a
        // standstill, so it is rated above the peak static compression torque — the slow-cranking
        // compression pressure on the piston times the crank lever near its worst — with margin.
        float e = Profile.CompressionRatio;
        float pComp = Gas.Atmosphere * MathF.Pow(e, 1.2f);
        float peakStatic = pComp * _pistonArea * 0.6f * _crankRadius;
        return MathF.Max(Profile.FrictionNm * 6f + Profile.DisplacementLitres * 60f, peakStatic * 1.4f);
    }

    private static bool Crossed(float before, float after, bool wrapped, float angle)
        => wrapped ? (angle <= after || angle > before) : (angle > before && angle <= after);

    /// <summary>
    /// The state of the engine that changes slowly: gas temperature, manifold pressure, boost, and
    /// the idle governor. A few hundred times a second is plenty.
    /// </summary>
    private void UpdateSlow(float rpm)
    {
        var e = Profile;
        float dtSlow = _dt * GasEvery;

        // Mean exhaust mass flow, over a few cycles.
        float flowNow = _massFlowAcc / dtSlow;
        _massFlowAcc = 0f;
        _massFlowLp += (flowNow - _massFlowLp) * MathF.Min(1f, dtSlow * 8f);

        // Governor. On a petrol engine it is an idle bypass — a small hole beside the plate, one to
        // three per cent of the bore — opened and closed slowly to hold the idle speed; on a diesel it
        // is fuel. It is slow on purpose: the plenum takes a fifth of a second to fill and combustion
        // two cycles to answer, and a loop quicker than that hunts by hundreds of rpm, which a badly
        // set-up idle does — but the profile should decide that through its gain, not the arithmetic.
        bool pedalUp = Throttle < 0.04f;
        bool diesel = e.Fuel == FuelType.Diesel;
        // Authority is sized from what THIS engine needs to idle: a 1.6 needs half a per cent of its
        // throttle bore, a 7-litre one per cent. Gains are fractions of that, so the same profile
        // gain means the same thing on both.
        float unit = diesel ? 0.3f : _idleAreaFrac;
        float idleCap = diesel ? 1f : 3f * unit;
        float kp = 0.5f * unit, ki = 0.7f * unit, kd = 1.4f * unit;
        if (!_wasRunning && Ignition && _omega > 5f) { _idleIntegral = MathF.Max(_idleIntegral, 0.9f * unit); _idleAir = _idleIntegral; }
        _wasRunning = Ignition && _omega > 5f;
        _rpmFast += (Rpm - _rpmFast) * MathF.Min(1f, dtSlow * 30f);
        if (pedalUp && Ignition && _omega > 10f)
        {
            float err = (e.IdleRpm - _rpmSlow) / e.IdleRpm;
            float rising = (_rpmFast - _rpmSlow) / e.IdleRpm;         // where it is heading
            _idleIntegral = Math.Clamp(_idleIntegral + err * e.IdleGovernorGain * ki * dtSlow, 0f, idleCap);
            _idleAir = Math.Clamp(_idleIntegral + e.IdleGovernorGain * (err * kp - rising * kd), 0f, idleCap);
            // The fast half of idle control is the spark: an ECU pulls timing when the idle runs
            // high and adds it when it sags, which changes torque within a cycle where air takes a
            // fifth of a second to arrive. It is what stops the plenum's lag turning the idle into a
            // slow surge. A carburetted engine has none of it, and its low gain says so.
            _idleSparkTrim = diesel ? 0f : Math.Clamp(e.IdleGovernorGain * (err * 18f - rising * 36f), -12f, 8f);
        }
        else
        {
            if (!pedalUp) _idleIntegral *= 1f - dtSlow * 0.5f;
            _idleSparkTrim *= 1f - dtSlow * 4f;
        }
        float pedalRaw = diesel ? MathF.Max(Throttle, _idleAir) : Throttle;
        _pedal += (pedalRaw - _pedal) * MathF.Min(1f, dtSlow / 0.04f);
        float pedal = _pedal;

        // Boost follows load with the lag of the turbine, or the crank for a blower, and raises the
        // pressure upstream of the throttle; the plenum then decides what the manifold sees.
        float wantSpool = e.Induction switch
        {
            Induction.Turbocharged => Math.Clamp(Throttle * MathF.Min(1f, (rpm - e.IdleRpm) / (0.35f * e.RedlineRpm)), 0f, 1f),
            Induction.Supercharged => Math.Clamp(rpm / e.RedlineRpm, 0f, 1f) * (0.3f + 0.7f * Throttle),
            _ => 0f,
        };
        float lag = e.Induction == Induction.Turbocharged ? MathF.Max(0.1f, e.Mechanical.TurboLagSeconds) : 0.15f;
        _spool += (wantSpool - _spool) * MathF.Min(1f, dtSlow / (wantSpool > _spool ? lag : lag * 0.5f));
        float airbox = Gas.Atmosphere * (1f + e.BoostBar / 1.01325f * _spool * _spool);
        // A diesel has no plate: the pedal is fuel.
        _intake.SetThrottle(diesel ? 1f : pedal, diesel ? 0f : _idleAir);
        _intake.UpdateGas(_intakeK, airbox);
        float open = Math.Clamp((_map / Gas.Atmosphere - 0.3f) / 0.7f, 0f, 1f);

        // Gas temperature follows the work, with the thermal inertia of steel: quick to heat,
        // slow to cool.
        float work = Math.Clamp(0.3f * rpm / e.RedlineRpm + 0.7f * (e.Fuel == FuelType.Diesel ? pedal : open), 0f, 1f);
        float wantK = (Ignition && _omega > 10f)
            ? MathHelper.Lerp(e.Exhaust.GasCelsiusIdle, e.Exhaust.GasCelsiusFull, work) + 273.15f
            : 293f;
        float tau = wantK > _portK ? 1.2f : 3.5f;
        _portK += (wantK - _portK) * MathF.Min(1f, dtSlow / tau);
        _exhaust.UpdateGas(_portK, _massFlowLp);
        _lastLoad = open;
    }

    /// <summary>
    /// Decides how this cycle will burn, at intake valve closing when the charge is what it is.
    ///
    /// The mass and the burnt fraction are real: they came through the valves. What is added here is
    /// what the model cannot integrate — the turbulence and mixing luck that make two identical
    /// charges burn differently — and it is added the way it is measured, as a coefficient of
    /// variation of the work per cycle. A healthy engine at load measures 2-4%; at idle with a lot of
    /// residual it climbs past 10%, which is where combustion becomes bimodal: the charge lights, or
    /// it barely does, or it does not. Above about 25% burnt fraction the flame slows badly and past
    /// 35% it fails, and a failed cycle sends its charge into the exhaust to pop later.
    ///
    /// The randomness is CORRELATED between consecutive firings, whichever cylinder they are in,
    /// through a walk the whole engine shares. This came from a measured failure: independent
    /// per-cylinder randomness averages smooth and a fixed pattern repeats like a tremolo, while what
    /// a lopey engine does is stagger — runs of weak cycles and runs of strong ones that never line
    /// up with the crank. Conditions the whole engine sees at once (manifold depression, residual in
    /// the plenum, the crank surging) are the physical reason.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void DecideCharge(ref Cylinder cy, float rpm, float load, bool firing)
    {
        var e = Profile;
        float fresh = MathF.Max(0f, cy.Mass - cy.BurntMass);
        float dilution = cy.Mass > 1e-9f ? cy.BurntMass / cy.Mass : 1f;
        cy.Dilution = dilution;

        if (!firing) { cy.ChargeDecided = false; cy.HeatTotal = 0f; return; }
        if (rpm > e.RedlineRpm + 120f) { cy.ChargeDecided = false; cy.HeatTotal = 0f; return; } // limiter

        // The walk, stepped once per firing event. Memory 0.72 spans about three events.
        _mixtureWalk = _mixtureWalk * 0.72f + 0.69f * Gaussian();

        // A stock engine idles at 15-20% residual and burns cleanly; instability sets in past about
        // 25% (the EGR tolerance of a spark-ignition engine) and the flame fails around 40%.
        float cov = e.CombustionVariation
                  + 0.30f * MathF.Pow(Math.Clamp((dilution - 0.22f) / 0.30f, 0f, 1f), 2f)
                  + 0.10f * e.IdleRoughness * (1f - load) * MathF.Max(0f, 1f - rpm / 2200f);
        float mean = 1f - 0.9f * MathF.Pow(Math.Clamp((dilution - 0.30f) / 0.25f, 0f, 1f), 1.5f);
        float q = mean * (1f + cov * (0.75f * _mixtureWalk + 0.65f * Gaussian())) * cy.Trim;
        // Bimodal at the bottom: a weak charge either catches or it does not.
        if (q < 0.45f) q *= 0.55f;
        cy.Misfired = q < 0.18f;
        if (cy.Misfired) q = 0.02f;
        q = Math.Clamp(q, 0f, 1.15f);
        cy.Quality = q;
        LastBurnQuality = q;
        LastDilution = dilution;

        float fuelFrac = e.Fuel == FuelType.Diesel ? Math.Clamp(0.04f + 0.96f * _pedal, 0f, 1f) : 1f;
        cy.HeatTotal = fresh * FuelEnergyPerKg * _heatScale * q * fuelFrac;
        cy.SparkDeg = SparkAngle(rpm, load);
        cy.BurnDeg = BurnDuration(rpm, dilution, load);
        cy.ChargeDecided = true;
    }

    private float Gaussian()
    {
        double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
    }

    // ── The valve boundary ──────────────────────────────────────────────────────────────────────

    /// <summary>Choked/subsonic orifice flow, kg/s, from up to down. Both pressures absolute.</summary>
    private static float OrificeFlow(float pUp, float tUp, float pDown, float area, float gamma)
    {
        if (area <= 0f || pUp <= pDown) return 0f;
        float pr = pDown / pUp;
        float crit = MathF.Pow(2f / (gamma + 1f), gamma / (gamma - 1f));
        float rt = MathF.Sqrt(Gas.R * tUp);
        if (pr <= crit)
        {
            float k = MathF.Sqrt(gamma) * MathF.Pow(2f / (gamma + 1f), (gamma + 1f) / (2f * (gamma - 1f)));
            return area * pUp * k / rt;
        }
        float t1 = MathF.Pow(pr, 2f / gamma) - MathF.Pow(pr, (gamma + 1f) / gamma);
        if (t1 <= 0f) return 0f;
        return area * pUp * MathF.Sqrt(2f * gamma / (gamma - 1f) * t1) / rt;
    }

    /// <summary>
    /// Solves the valve as a boundary between the cylinder and a waveguide.
    ///
    /// The pipe end sees an incoming wave a and returns b; its pressure is p0 + a + b and its volume
    /// velocity into the pipe is (b - a)/Z. The valve passes a mass flow that depends on the pressure
    /// ratio across it, and that flow divided by the gas density at the port must equal the volume
    /// velocity. One nonlinear equation in b, monotone, solved by Newton from last sample's answer.
    /// Returns b; mdot is positive OUT of the cylinder.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static float SolveValve(float a, float bStart, float Z, float area, float pCyl, float tCyl,
                                    float pipeK, float gamma, float cylMass, float dt, float uMean, float uCap, float pMean, out float mdot)
    {
        if (DebugRigidValves) { mdot = 0f; return a; }
        // uMean is kept at zero: subtracting a running mean of the flow at the valve was tried to keep
        // the mean breathing out of the pipes, and it injects a spurious compression whenever the
        // instantaneous flow is below the mean — a false ram effect worth a quarter of the charge on
        // a big single. The pipes bleed their own DC instead (plenum stub, open ends).
        // uCap: the pipe cannot supply or accept more than about Mach 0.3 of quasi-steady flow. A
        // choked valve into an empty cylinder asks a linear pipe for more than that, the pipe's
        // pressure goes to nothing, and the equation has no root; the cap keeps it physical.
        // Only the FLUCTUATING flow is a wave. The mean flow through the valve — the engine's
        // breathing — is carried by the pipe at no acoustic pressure, its pressure drop being what
        // the manifold-pressure model already accounts for. Left in, it piled up as a standing DC
        // suction of nearly half a bar in the intake runners and starved every cylinder.
        // The most the cylinder can exchange in one sample is what brings it to the port pressure.
        // Without this cap a small cylinder at TDC with a wide-open valve overshoots the port
        // pressure every sample and rings the pipe up from nothing: an explicit step on a stiff
        // coupling. Inside the residual so the pipe and the cylinder always see the same flow.
        float equalise = 0.5f * cylMass / dt;
        // The residual is monotone increasing in b, so bracket it by the largest volume velocity
        // the valve could possibly pass in either direction and close in with regula falsi. Newton
        // was tried first and could not be trusted near p_port = p_cyl, where the orifice law has an
        // infinite slope and a bad step throws energy into the pipe.
        float uMax = uCap * 1.05f;
        float lo = a - Z * uMax, hi = a + Z * uMax;
        float gLo = Residual(lo, out _), gHi = Residual(hi, out _);
        if (gLo >= 0f) { Residual(lo, out mdot); return lo; }
        if (gHi <= 0f) { Residual(hi, out mdot); return hi; }
        float b = Math.Clamp(bStart, lo, hi);
        float gB = Residual(b, out mdot);
        if (gB > 0f) { hi = b; gHi = gB; } else { lo = b; gLo = gB; }
        int side = 0;
        for (int it = 0; it < 10; it++)
        {
            b = (gLo * hi - gHi * lo) / (gLo - gHi);
            if (!float.IsFinite(b)) b = 0.5f * (lo + hi);
            gB = Residual(b, out mdot);
            if (MathF.Abs(gB) * Z < 0.2f || hi - lo < 0.5f) break;
            if (gB > 0f) { hi = b; gHi = gB; if (side == 1) gLo *= 0.5f; side = 1; }
            else { lo = b; gLo = gB; if (side == -1) gHi *= 0.5f; side = -1; }
        }
        return b;

        float Residual(float bb, out float m)
        {
            float pPort = MathF.Max(0.05f * Gas.Atmosphere, pMean + a + bb);
            float rhoPort = Gas.Density(pPort, pipeK);
            if (pCyl >= pPort)
            {
                m = OrificeFlow(pCyl, tCyl, pPort, area, gamma);
                m = MathF.Min(m, equalise * (pCyl - pPort) / pCyl);
            }
            else
            {
                m = -OrificeFlow(pPort, pipeK, pCyl, area, gamma);
                m = MathF.Max(m, -equalise * (pPort - pCyl) / pCyl);
            }
            float u = Math.Clamp(m / rhoPort, -uCap, uCap);
            m = u * rhoPort;
            return (bb - a) / Z - (u - uMean);
        }
    }

    /// <summary>Diagnostic: evaluates the valve-boundary residual at a given outgoing wave.</summary>
    internal static float ValveResidual(float a, float bb, float Z, float area, float pCyl, float tCyl, float pipeK, float gamma, float uMean, float uCap, float pMean, out float m)
    {
        float pPort = MathF.Max(0.05f * Gas.Atmosphere, pMean + a + bb);
        float rhoPort = Gas.Density(pPort, pipeK);
        if (pCyl >= pPort) m = OrificeFlow(pCyl, tCyl, pPort, area, gamma);
        else m = -OrificeFlow(pPort, pipeK, pCyl, area, gamma);
        float u = Math.Clamp(m / rhoPort, -uCap, uCap);
        m = u * rhoPort;
        return (bb - a) / Z - (u - uMean);
    }

    /// <summary>Diagnostic: treat every valve as shut for the pipes, so the network can be tested
    /// on its own. Never set in a game.</summary>
    public static bool DebugRigidValves;

    /// <summary>Diagnostic: one line per cylinder.</summary>
    public System.Collections.Generic.IEnumerable<string> DescribeCylinders()
    {
        for (int c = 0; c < _n; c++)
        {
            var cy = _cyl[c];
            yield return $"cyl {c + 1}: ph {cy.Phase,6:F1}  p {cy.Pressure / 1e5f,6:F3} bar  T {cy.Temp,5:F0} K  m {cy.Mass * 1e6f,7:F1} mg  burnt {cy.BurntMass * 1e6f,6:F1}  runnerburnt {cy.RunnerBurnt,4:F2}  V {cy.Volume * 1e6f,5:F0} cc  lE {cy.LiftE * 1e3f,5:F2} lI {cy.LiftI * 1e3f,5:F2}  portE {cy.PortE / 1e5f,6:F3} flowE {cy.FlowE * 1e3f,6:F1} g/s  portI {cy.PortI / 1e5f,6:F3} flowI {cy.FlowI * 1e3f,6:F1} g/s uMeanI {cy.IntakeUMean * 1e3f,5:F1} L/s q {cy.Quality:F2} {(cy.Burning ? "BURN" : "")}{(cy.ChargeDecided ? "ARMED" : "")}";
        }
        yield return $"omega {_omega:F3} rad/s  starter {(Starter ? StarterTorque() : 0f):F0} Nm  theta {_theta:F2}";
    }

    /// <summary>Resets the engine to cold and still.</summary>
    public void Reset()
    {
        _omega = 0f; _theta = 0; _idleAir = 0f; _spool = 0f; _massFlowLp = 0f;
    }

    /// <summary>Console lines about the built engine.</summary>
    public System.Collections.Generic.IEnumerable<string> Describe()
    {
        var e = Profile;
        yield return $"{e.Name}: {e.Cylinders} cyl {e.DisplacementLitres:F1} L, bore {e.BoreMm:F0} x stroke {e.StrokeMm:F0}, CR {e.CompressionRatio:F1}";
        yield return $"firing order {e.FiringOrder}, overlap {e.OverlapDegrees:F0} deg (lope {e.CamLope:F2})";
        yield return $"calibrated: heat scale {_heatScale:F2} -> EVO pressure {EvoPressureCalibratedBar:F1} bar at full load (real engines: 4-6, race cams higher)";
        foreach (var line in _exhaust.Describe()) yield return line;
    }

    /// <summary>A short mechanical tick: a two-pole ring plus a puff of noise.</summary>
    private sealed class ClickVoice
    {
        private readonly float _rate;
        private readonly Random _rng;
        private float _y1, _y2, _env, _freq = 3500f;
        public ClickVoice(float rate, int seed) { _rate = rate; _rng = new Random(seed); }
        public void Trigger(float amp, float freq)
        {
            _env = MathF.Min(1.5f, _env + amp * (0.7f + 0.6f * (float)_rng.NextDouble()));
            _freq = freq;
        }
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public float Process()
        {
            if (_env < 1e-5f) return 0f;
            float w = 2f * MathF.PI * _freq / _rate;
            float r = 0.985f;
            float x = _env * ((float)_rng.NextDouble() * 2f - 1f);
            float y = x + 2f * r * MathF.Cos(w) * _y1 - r * r * _y2;
            _y2 = _y1; _y1 = y;
            _env *= 0.994f;
            return y * 0.012f;
        }
    }
}
