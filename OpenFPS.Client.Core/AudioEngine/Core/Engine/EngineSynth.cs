using System.Numerics;
using OpenFPS.Common;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

/// <summary>
/// An internal combustion engine, integrated sample by sample: every cylinder is a volume of gas
/// with a mass, an energy and two valves; the crank is a rotating mass driven by the pressure on the
/// pistons; the pipes on both sides are waveguides. The sound is whatever leaves the tailpipes,
/// the snorkel and the block.
///
/// So the blowdown pulse has the shape the valve and cylinder give it (choked orifice flow through a
/// curtain that follows the cam, its length cylinder volume over valve area); load sets the pulse's
/// amplitude (ten times idle at full throttle) and so how hard the fronts steepen; reversion through
/// an open valve dilutes the next charge, which is what makes a big cam lope; and the crank speeds and
/// slows on every stroke, so idle firings are uneven and an unstable engine hunts. Nothing here is a
/// tone control. A few hundred operations per sample per cylinder: a few per cent of a core for a V8
/// at 44.1 kHz. Design and history: docs/ENGINE_SYNTHESIS.md.
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
    /// <summary>Crank degrees turned with the ignition on since the engine last stood still; firing
    /// waits for <see cref="EngineProfile.FiringAfterRevolutions"/>. Infinite once placed running.</summary>
    private float _syncDegrees = float.PositiveInfinity;
    /// <summary>Whether the engine computer has synchronised and the cylinders are being fuelled —
    /// what a driver holding the key is listening for.</summary>
    public bool Firing => Ignition && _syncDegrees >= 360f * Profile.FiringAfterRevolutions;
    /// <summary>Torque resisting the crank from whatever it is connected to, Nm. Positive resists.</summary>
    public float LoadTorque { get; set; }
    /// <summary>Extra rotating inertia the crank has to carry, kg m^2 — the car, reflected through
    /// the gearing. Zero with the clutch down.</summary>
    public float ExternalInertia { get; set; }
    /// <summary>The speed the governor holds with the pedal up, rpm; zero holds the profile's idle.
    /// A locomotive's notches move it, and its turbo then follows the governor's fuel.</summary>
    public float GovernedRpm { get; set; }

    // ── Outputs, valid after Step() ─────────────────────────────────────────────────────────────

    /// <summary>Pressure at one metre from the tailpipes, pascals.</summary>
    public float Exhaust { get; private set; }

    /// <summary>The part of <see cref="Exhaust"/> that came off the muffler case rather than out of
    /// the pipe, pascals at one metre. Diagnostic.</summary>
    public float ExhaustShell { get; private set; }

    /// <summary>The part of <see cref="Exhaust"/> that came out of the pipes. Diagnostic.</summary>
    public float ExhaustPipe { get; private set; }
    /// <summary>Pressure at one metre from the intake mouth, pascals.</summary>
    public float Intake { get; private set; }
    /// <summary>Pressure at one metre from the block: valvetrain, combustion through the metal, accessories.</summary>
    public float Block { get; private set; }
    /// <summary>The starter's own sound, Pa, already summed into <see cref="Block"/>. Apart because it
    /// reaches a cabin by its own path: it is bolted to the bellhousing, not radiating off the block.</summary>
    public float StarterOut { get; private set; }
    /// <summary>The starter's sound, 0..1 — normally one. Writable so an instrument can mute it and
    /// hear what of a start is the starter.</summary>
    public float StarterMix = 1f;

    /// <summary>The block's absolute-level anchor, +7.5 dB as a gain. See where Block is assembled.</summary>
    private const float BlockRadiationGain = 2.371f;   // 10^(7.5/20)
    /// <summary>The speed the block's anchor was measured at (a car's rated speed); above it a petrol
    /// engine's block grows by Anderton's 40 log N more than the mechanisms give.</summary>
    private const float BlockLawReferenceRpm = 6000f;

    /// <summary>The airbox's own transmission loss as a gain, from its geometry. Built once: it is
    /// a property of the box, not of what the engine is doing. See IntakeSpec.AirboxLossDb.</summary>
    private readonly float _airboxLoss;

    public float Rpm => _omega * 60f / (2f * MathF.PI);
    /// <summary>Where a turbo's spool is heading: its idle freewheel, plus the throttle's share of
    /// what is left above it.</summary>
    internal static float TurboTarget(float throttle, float rpm, EngineProfile e)
    {
        float freewheel = Math.Clamp(e.Mechanical.TurboIdleSpool * Math.Clamp(rpm / MathF.Max(1f, e.IdleRpm), 0f, 1.5f), 0f, 1f);
        float driven = throttle * Math.Clamp((rpm - e.IdleRpm) / (0.35f * e.RedlineRpm), 0f, 1f);
        return Math.Clamp(freewheel + (1f - freewheel) * driven, 0f, 1f);
    }

    /// <summary>The turbo or blower's spool, 0..1: how far up to full boost its shaft is. For tests.</summary>
    internal float Spool => _spool;

    /// <summary>Crank angle, degrees through the cycle. For instruments: an artefact that recurs at
    /// the same angle every cycle is a different bug from one that recurs at the same time.</summary>
    public float CrankDegrees => (float)(_theta % Profile.CycleDegrees);

    /// <summary>
    /// Sets the crank turning at a given speed without having driven it there: for placing a voice.
    /// Started from rest, a car entering earshot at speed spun up through its whole range in the
    /// speed filter's 80 ms, heard as a pop.
    /// </summary>
    public void SpinTo(float rpm)
    {
        _omega = MathF.Max(0f, rpm) * 2f * MathF.PI / 60f;
        // Already synchronised, or a car first heard at speed goes quiet for three revolutions.
        _syncDegrees = rpm > 0f ? float.PositiveInfinity : 0f;
        _rpmSlow = Rpm;
        _rpmFast = Rpm;
    }
    /// <summary>Crank angle within the cycle, degrees.</summary>
    public float CrankAngle => (float)_theta;
    /// <summary>Net torque from the gas on the crank this sample, Nm, scaled so full throttle at
    /// the torque peak gives the profile's peak torque.</summary>
    public float Torque { get; private set; }
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

    // Combustion instability shared by the engine: see DecideCharge.
    private float _mixtureWalk;

    // Block noise
    private float _blockLp, _knockHp;
    private float _structLpK1, _structLpK2, _structLpV1, _structLpV2, _valveLp;
    private readonly float _structLpA, _valveLpA, _knockHpA, _blockLpA;
    /// <summary>
    /// Calibration of the structure's ringing (--tap-balance knock): the knock is held to its anchor at
    /// full load, the declared levels DeclaredSourceLevelMatchesTheLiveVoice holds every preset to. At
    /// idle and cruise it comes out about 5 dB under a gas-mode-only model; the valves sit under the
    /// combustion knock at idle, as a diesel's valvetrain does.
    /// </summary>
    private const float StructureKnockGain = 0.50f, StructureValveGain = 5.5f;

    /// <summary>
    /// One mode of the gas in the cylinder: a two-pole resonator with its zeros at DC and Nyquist,
    /// so it passes a band and nothing else.
    /// </summary>
    private struct Mode
    {
        public float A, C, R2, Weight;
        public float X2, Y1, Y2;

        public float Q;

        public void Set(float hz, float q, float rate, float weight)
        {
            Q = q;
            Weight = weight;
            Retune(hz, rate);
        }

        /// <summary>Move the note without disturbing what is already ringing in it.</summary>
        public void Retune(float hz, float rate)
        {
            float w = 2f * MathF.PI * MathF.Min(hz, rate * 0.45f) / rate;
            float r = MathF.Exp(-w / (2f * Q));
            A = (1f - r * r) * 0.5f;
            C = 2f * r * MathF.Cos(w);
            R2 = r * r;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Step(float x)
        {
            float y = A * (x - X2) + C * Y1 - R2 * Y2;
            X2 = x; Y2 = Y1; Y1 = y;
            return y * Weight;
        }
    }

    private Mode[] _knockModes = Array.Empty<Mode>();
    /// <summary>The block and head, as the knock and the valves reach the air through them: one set of
    /// structural modes, two copies because the two drives are in different units.</summary>
    private Mode[] _structKnock = Array.Empty<Mode>(), _structValves = Array.Empty<Mode>();
    private float _knockTemp = 1100f, _knockBore = 0.1f;
    /// <summary>Per-sample coefficients chosen at 44.1 kHz, at this engine's rate (At44k): the knock gas's
    /// relaxation (0.001, 23 ms) and Kellet's three pinking poles (0.99765, 0.963, 0.57).</summary>
    private float _knockRelax, _kp0 = 0.99765f, _kp1 = 0.963f, _kp2 = 0.57f;
    private int _knockRetune;
    private readonly ClickVoice _click;
    private double _whinePhase, _blowerPhase, _turboPhase, _turbinePhase;
    private float _tcnDrift;
    private float _humpS1, _humpS2, _humpPower;
    private float _turbineTone;                 // the turbine's tone at a metre from the tailpipe, Pa
    private float _whooshNorm = 1f, _wb1, _wb2, _wx1, _wx2;
    private float _wB0, _wB2, _wA1, _wA2;         // the whoosh band's biquad

    private struct Cylinder
    {
        public float Mass, Energy, BurntMass;  // kg, J, kg
        public float Volume, Pressure, Temp;
        public float Phase;                    // degrees after this cylinder's firing TDC
        public float LiftE, LiftI;             // metres
        public float ExhaustB, IntakeB;        // last outgoing wave
        // Each valve's last two volume velocities and the residual's slope its last solve ended on: the
        // next solve's first guess and first step (SolveValve).
        public float ExhaustU1, ExhaustU2, IntakeU1, IntakeU2, ExhaustSlope, IntakeSlope;
        public float RunnerBurnt;              // burnt fraction of the gas in this cylinder's runner, 0..1
        public float ExhaustUMean, IntakeUMean; // slow mean volume velocity through each valve, m^3/s
        public float PortE, PortI, FlowE, FlowI; // diagnostics: port acoustic pressure, valve mass flow
        // This cycle's combustion, decided at intake valve closing.
        public float HeatTotal, SparkDeg, BurnDeg, BurnPrev, Quality;
        public float Premix;                   // diesel: fraction of the charge that burns premixed
        public bool Burning, ChargeDecided, Misfired;
        public float PopAmp; public int PopLeft, PopLength;
        public float Pk0, Pk1, Pk2, PkHp, PkLp; // the valve flow noise's band (BlowdownJet)
        public float Trim;
        public bool EOpenWas, IOpenWas;
        public float PressurePrev;
    }

    public EngineSynth(EngineProfile e, float rate = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.DefaultRate, int seed = 11)
    {
        Profile = e;
        _rate = rate;
        _knockRelax = At44k.Step(0.001f, rate);
        _kp0 = At44k.Decay(0.99765f, rate); _kp1 = At44k.Decay(0.96300f, rate); _kp2 = At44k.Decay(0.57000f, rate);
        _dt = 1f / rate;
        _rng = new Random(seed);
        SetUpPinkBand();
        SetUpWhooshBand();
        _n = e.Cylinders;
        _cycleDeg = e.CycleDegrees;
        _airboxLoss = MathF.Pow(10f, -e.Intake.AirboxLossDb / 20f);

        _crankRadius = e.StrokeMm * 0.5e-3f;
        _rodLength = _crankRadius * e.RodRatio;
        float bore = e.BoreMm * 1e-3f;
        _pistonArea = MathF.PI * 0.25f * bore * bore;
        float swept = _pistonArea * 2f * _crankRadius;
        _clearanceVolume = swept / MathF.Max(1.5f, e.CompressionRatio - 1f);
        _cv = Gas.R / (_gammaCyl - 1f);
        _cp = _cv + Gas.R;

        // The gas in the cylinder at TDC is a shallow disc with a family of transverse modes (Draper:
        // 1.841, 3.054 and 3.832 times c/(pi D)). One mode struck at the firing rate is a note, not a
        // knock; three at incommensurate ratios never settle on a pitch. The frequency falls out of the
        // bore alone: a 102 mm Cummins knocks at 5.2 kHz, a 116 mm bus at 4.5.
        _knockBore = MathF.Max(0.04f, bore);
        float baseHz = 900f / (MathF.PI * _knockBore);
        bool ci = e.Fuel == FuelType.Diesel;
        _knockModes = new Mode[3];
        // Damped hard: they die in about a millisecond; ringing for five they are a chime.
        _knockModes[0].Set(1.841f * baseHz, ci ? 6f : 5f, rate, 1.00f);
        _knockModes[1].Set(3.054f * baseHz, ci ? 5f : 4f, rate, 0.55f);
        _knockModes[2].Set(3.832f * baseHz, ci ? 4.5f : 3.5f, rate, 0.40f);

        // The structure the knock gets out through. A premixed burn's pressure rise is an impulse, and
        // the air hears it ringing the block and head: modes between about 0.6 and 3 kHz dying in a few
        // milliseconds (a measured heavy engine peaked at 1.03, 1.29 and 2.72 kHz). The block's
        // "structure attenuation" (Austen and Priede) loses least near 2 kHz, 5 dB by 4 kHz, 17 by 6 and
        // 27 by 8; the gas ringing at 4-5 kHz adds half a decibel through the metal even in hard knock
        // (Sandia, OSTI 1123537). Without it the knock and the 3.1-4.4 kHz valve rings were zippy and
        // fake. Knock and valve seats both drive these modes: placed for a 125 mm bore, up gently on a
        // smaller engine, 6 ms decay each (cast iron's damping).
        float sizeScale = MathF.Sqrt(0.125f / MathF.Max(0.05f, bore));
        _structLpA = OnePole.AlphaFor(5500f, rate);
        _knockHpA = OnePole.AlphaFor(700f, rate);
        _blockLpA = OnePole.AlphaFor(180f, rate);
        _valveLpA = OnePole.AlphaFor(2000f, rate);
        // The 3.6 kHz mode is the head and valve covers: without it the clatter measured 24-28 dB down
        // at 4 kHz where the research puts it at 18, and a diesel sounded choked.
        float[] structHz = { 620f, 800f, 1300f, 1800f, 2700f, 3600f };
        float[] structWeight = { 0.70f, 1.00f, 0.95f, 0.95f, 0.95f, 0.70f };
        _structKnock = new Mode[structHz.Length];
        _structValves = new Mode[structHz.Length];
        for (int i = 0; i < structHz.Length; i++)
        {
            float hz = structHz[i] * sizeScale;
            float q = MathF.PI * hz * 0.006f;
            _structKnock[i].Set(hz, q, rate, structWeight[i]);
            _structValves[i].Set(hz, q, rate, structWeight[i]);
        }

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
        // reported rather than asked for. (Not scaling the torque afterwards: that scales the
        // compression resistance too, and the starter cannot get a big block over top dead centre.)
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

    /// <summary>
    /// <see cref="VolumeAt"/> and dx/dphi of the piston (metres per radian, the lever the gas pushes on)
    /// at one angle, the sine and cosine taken once: the step wants both, every cylinder, every sample.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void VolumeAndLever(float deg, out float volume, out float lever)
    {
        float phi = deg * (MathF.PI / 180f);
        float s = MathF.Sin(phi), c = MathF.Cos(phi);
        float inner = _rodLength * _rodLength - _crankRadius * _crankRadius * s * s;
        float x = _crankRadius * (1f - c) + _rodLength - MathF.Sqrt(MathF.Max(0f, inner));
        volume = _clearanceVolume + _pistonArea * x;
        float root = MathF.Sqrt(MathF.Max(1e-9f, inner));
        lever = _crankRadius * s + _crankRadius * _crankRadius * s * c / root;
    }

    /// <summary>Switch for <see cref="BlowdownJet"/>: the lab's A/B, and /valveflow in game.
    /// Volatile because the engines render on the pool's threads.</summary>
    internal static volatile bool ValveJetNoise = true;

    /// <summary>
    /// The rush of gas through the exhaust valve's gap, as broadband noise in the port: sonic flow
    /// through a fraction of a millimetre at several atmospheres. Without it a diesel's tailpipe was
    /// 40-50 dB down at 1 kHz from its firing octave ("like it's got its lips tightly shut").
    ///
    /// The DOT's Noise Control Handbook for Diesel-Powered Vehicles (Damkevala 1974, s. 4.2): valve
    /// flow "may be the dominant source of high frequency exhaust noise", a hump flat per proportional
    /// band from about 400 Hz to 3 kHz, 13-18 dB under the firing line in tenth-octaves. Flow past an
    /// obstruction in a duct is a dipole (Gordon, NASA 1969): power K rho U^6 A / c^3 at the throat,
    /// half of it down the primary as a plane wave. <see cref="ValveFlowNoiseK"/> is set against the
    /// handbook's unmuffled NA Cummins (Fig 4.3); the pipes and muffler then give a muffled truck its
    /// flat, lower top (Donaldson US 6,082,487).
    /// </summary>
    private float BlowdownJet(ref Cylinder cy, float mdot, float area)
    {
        float rhoT = 0.63f * MathF.Max(1e-3f, cy.Mass / MathF.Max(1e-7f, cy.Volume));
        float cCyl = Gas.SoundSpeed(cy.Temp, Gas.GammaExhaust);
        float u = MathF.Min(mdot / (rhoT * MathF.Max(1e-7f, area)), 0.91f * cCyl);
        // The port gas moves on the slow tick; its properties are worked out when it does.
        if (_portK != _jetPortK)
        {
            _jetPortK = _portK;
            _jetRhoP = Gas.Density(Gas.Atmosphere, _portK);
            _jetCP = Gas.SoundSpeed(_portK, Gas.GammaExhaust);
            _jetCP3 = Math.Pow(_jetCP, 3);
        }
        float rhoP = _jetRhoP, cP = _jetCP;
        double w = ValveFlowNoiseK * rhoT * Math.Pow(u, 6) * area / _jetCP3;
        float p = MathF.Sqrt((float)(0.5 * w * rhoP * cP / MathF.Max(1e-6f, _primaryArea)));
        return p * PinkBand(ref cy, (float)(_rng.NextDouble() * 2 - 1));
    }

    private float _jetPortK = float.NaN, _jetRhoP, _jetCP;
    private double _jetCP3;

    /// <summary>The valve flow noise's dipole constant. See <see cref="BlowdownJet"/>.</summary>
    internal const float ValveFlowNoiseK = 1e-3f;

    /// <summary>
    /// Unit-RMS noise with equal energy per octave from about 300 Hz to 5 kHz, falling outside:
    /// Kellet's three-pole pinking, a pole's high-pass below and a pole's low-pass above.
    /// </summary>
    private float PinkBand(ref Cylinder cy, float n)
    {
        cy.Pk0 = _kp0 * cy.Pk0 + n * 0.0990460f;
        cy.Pk1 = _kp1 * cy.Pk1 + n * 0.2965164f;
        cy.Pk2 = _kp2 * cy.Pk2 + n * 1.0526913f;
        float pink = cy.Pk0 + cy.Pk1 + cy.Pk2 + n * 0.1848f;
        cy.PkHp += _pinkHpA * (pink - cy.PkHp);
        cy.PkLp += _pinkLpA * ((pink - cy.PkHp) - cy.PkLp);
        return cy.PkLp * _pinkNorm;
    }

    private float _pinkHpA, _pinkLpA, _pinkNorm = 1f;

    private void SetUpPinkBand()
    {
        _pinkHpA = OnePole.AlphaFor(300f, _rate);
        _noiseHpA = MathF.Exp(-MathF.Tau * 250f / _rate);
        _noiseLpA = 1f - MathF.Exp(-MathF.Tau * 7000f / _rate);
        _pinkLpA = OnePole.AlphaFor(5000f, _rate);
        // Normalised by measuring two seconds, on its own generator so the engine's stream is untouched.
        var probe = new Cylinder();
        var rng = new Random(1);
        _pinkNorm = 1f;
        double e = 0; int n = (int)(_rate * 2f), skip = (int)(_rate * 0.2f);
        for (int i = 0; i < n; i++)
        {
            float y = PinkBand(ref probe, (float)(rng.NextDouble() * 2 - 1));
            if (i >= skip) e += y * (double)y;
        }
        _pinkNorm = 1f / MathF.Sqrt((float)(e / (n - skip)));
    }

    /// <summary>Effective flow area of a valve set at a lift: the curtain, capped at a quarter of the
    /// diameter's lift.</summary>
    private static float ValveArea(ValveSpec v, float lift)
    {
        float d = v.DiameterMm * 1e-3f;
        float curtain = MathF.PI * d * MathF.Min(lift, d * 0.25f);
        return v.DischargeCoefficient * v.Count * curtain;
    }

    /// <summary>Valve lift from a cam lobe at some crank angle relative to its centreline, metres.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
            float xb = Wiebe((a + step) - spark, burn, e.Fuel, 0.35f);
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

    /// <summary>
    /// How long a diesel waits between the injector opening and the charge lighting, in crank degrees,
    /// and so how much of it clatters. Everything injected during the wait burns at once when it
    /// lights (the premixed spike the block radiates); what comes after burns as fast as it mixes,
    /// smoothly. At idle the delay outlasts the injection and nearly all of it is premixed; under load
    /// the injection outlasts a shorter delay. So a diesel rattles standing and goes smooth pulling,
    /// from the two timescales alone; a constant split stopped it sounding like a diesel. The delay is
    /// Arrhenius in milliseconds, so in degrees it grows as the engine revs.
    /// </summary>
    /// <param name="rpm">Crank speed.</param>
    /// <param name="pressureBar">Cylinder pressure at injection, bar absolute.</param>
    /// <param name="kelvin">Charge temperature at injection.</param>
    private static float IgnitionDelayDegrees(float rpm, float pressureBar, float kelvin)
    {
        float ms = 0.40f * MathF.Pow(MathF.Max(1f, pressureBar), -1.02f) * MathF.Exp(2100f / MathF.Max(400f, kelvin));
        ms = Math.Clamp(ms, 0.25f, 4f);
        return Math.Clamp(6f * rpm * ms / 1000f, 1.5f, 30f);
    }

    /// <summary>Burn duration in crank degrees: longer when diluted, a little longer at speed.</summary>
    private float BurnDuration(float rpm, float dilution, float load)
    {
        if (Profile.Fuel == FuelType.Diesel) return 42f + 12f * MathF.Min(1f, rpm / 3000f);
        return 42f + 45f * Math.Clamp(dilution * 3f, 0f, 1f) + 12f * MathF.Min(1f, rpm / 6000f) + 10f * (1f - load);
    }

    /// <summary>Burnt mass fraction at an angle after ignition: Wiebe, a=5, m=2. A diesel burns in
    /// two parts split by <paramref name="premix"/> (see <see cref="IgnitionDelayDegrees"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float Wiebe(float degAfterSpark, float duration, FuelType fuel, float premix)
    {
        if (degAfterSpark <= 0f) return 0f;
        if (fuel == FuelType.Diesel)
        {
            // The premixed burn goes off nearly at once: a few degrees, not a fraction of the duration.
            float pre = 1f - MathF.Exp(-5f * MathF.Pow(Math.Clamp(degAfterSpark / 7f, 0f, 1f), 2.5f));
            float dif = 1f - MathF.Exp(-5f * MathF.Pow(Math.Clamp(degAfterSpark / duration, 0f, 1f), 1.6f));
            return premix * pre + (1f - premix) * dif;
        }
        float u = Math.Clamp(degAfterSpark / duration, 0f, 1f);
        return 1f - MathF.Exp(-5f * u * u * u);
    }

    // ── The step ────────────────────────────────────────────────────────────────────────────────

    /// <summary>One sample of engine.</summary>
    /// <remarks>
    /// AggressiveOptimization here and on every per-sample method too big to inline: during a map load
    /// the JIT keeps this at tier-0, which measured 4.0x realtime against 7.8x settled on nascar_v8
    /// (docs/AUDIO_LOAD_DROPOUTS.md, section 2). Targeted rather than tiering off for the whole client,
    /// which would make the JIT do more work during the load.
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
        _theta += _omega * (180.0 / Math.PI) * _dt;
        if (_theta >= _cycleDeg) _theta -= _cycleDeg;
        // The engine computer's count, lost with the ignition or a stopped crank: a stall resynchronises.
        if (!Ignition || _omega < 0.5f) _syncDegrees = 0f;
        else if (_syncDegrees < float.PositiveInfinity) _syncDegrees += _omega * (180f / MathF.PI) * _dt;

        float gasTorque = 0f;
        float portPeak = 0f;
        float knock = 0f;
        float blockLow = 0f;
        float knockTempAcc = 0f, knockTempWgt = 0f;
        float massOut = 0f;
        float intakeFlow = 0f;
        _map = _intake.PlenumPressure;
        float load = Math.Clamp((_map / Gas.Atmosphere - 0.3f) / 0.7f, 0f, 1f);
        if (e.Fuel == FuelType.Diesel) load = _pedal;
        float intakeK = _intakeK;
        bool firing = Ignition && _omega > 5f && _syncDegrees >= 360f * e.FiringAfterRevolutions;

        for (int c = 0; c < _n; c++)
        {
            ref var cy = ref _cyl[c];
            float phase = (float)(_theta - e.FiringAngles[c]);
            if (phase < 0f) phase += _cycleDeg;
            float phasePrev = cy.Phase;
            cy.Phase = phase;
            bool wrapped = phase < phasePrev;

            // Volume and the work of moving the piston.
            VolumeAndLever(phase, out float V, out float lever);
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
                float xb = Wiebe(since, cy.BurnDeg, e.Fuel, cy.Premix);
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
                // The flow through the valve moves smoothly where the arriving wave may jump (a shock
                // front), so the guess carries the flow on, not the outgoing wave.
                float guessE = aE + Z * (2f * cy.ExhaustU1 - cy.ExhaustU2);
                bE = SolveValve(aE, guessE, Z, area, cy.Pressure, cy.Temp, rhoPipeK, Gas.GammaExhaust, cy.Mass, _dt, 0f, capE, Gas.Atmosphere, out float mdot, ref cy.ExhaustSlope);
                cy.ExhaustB = bE;
                cy.ExhaustU2 = cy.ExhaustU1;
                cy.ExhaustU1 = (bE - aE) / Z;
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
                if (ValveJetNoise && mdot > 0f) bE += BlowdownJet(ref cy, mdot, area);
            }
            else
            {
                bE = aE;                                   // shut: a wall
                cy.ExhaustB = aE;
                cy.ExhaustU1 = cy.ExhaustU2 = cy.ExhaustSlope = 0f;
                cy.ExhaustUMean -= cy.ExhaustUMean * _dt * MeanFlowRate;
            }
            // Overrun pops: unburnt charge lighting off in the hot pipe, entered at the port as a pulse
            // a couple of milliseconds long (a single-sample step was a click, not a bang).
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
                float capI = 0.3f * 350f * _runnerArea;
                float guessI = aI + Z * (2f * cy.IntakeU1 - cy.IntakeU2);
                bI = SolveValve(aI, guessI, Z, area, cy.Pressure, cy.Temp, intakeK, Gas.GammaAir, cy.Mass, _dt, 0f, capI, _map, out float mdotOut, ref cy.IntakeSlope);
                cy.IntakeB = bI;
                cy.IntakeU2 = cy.IntakeU1;
                cy.IntakeU1 = (bI - aI) / Z;
                cy.IntakeUMean += (mdotOut / Gas.Density(_map, intakeK) - cy.IntakeUMean) * _dt * MeanFlowRate;
                cy.PortI = aI + bI; cy.FlowI = mdotOut;
                intakeFlow += mdotOut;
                float dm = mdotOut * _dt;                  // positive = cylinder to runner
                // The runner is a mixed reservoir of fixed mass: reversion raises its burnt fraction,
                // and a draw carries that fraction and is replaced with clean air. That bounds the
                // dilution of a big cam's idle; a plug model marked whole charges as burnt.
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
                cy.IntakeU1 = cy.IntakeU2 = cy.IntakeSlope = 0f;
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
            gasTorque += (cy.Pressure - Gas.Atmosphere) * _pistonArea * lever;

            // ── What the block radiates ──────────────────────────────────────────────────
            // Combustion through the metal: the rate of pressure rise, modest for petrol, for a
            // diesel the sound.
            if (cy.Burning)
            {
                float dp = (cy.Pressure - cy.PressurePrev) * _rate;
                knock += dp;
                // The modes move with the gas temperature, weighted by how hard each cylinder drives them.
                float wgt = MathF.Abs(dp);
                knockTempAcc += cy.Temp * wgt;
                knockTempWgt += wgt;
            }
            blockLow += cy.Pressure - Gas.Atmosphere;

            // Valvetrain: a tick as each valve leaves its seat and a louder one as it lands.
            bool eOpen = liftE > 1e-5f;
            if (eOpen != cy.EOpenWas) _click.Trigger(eOpen ? 0.5f : 1f);
            if (intakeOpen != cy.IOpenWas) _click.Trigger(intakeOpen ? 0.45f : 0.9f);
            cy.EOpenWas = eOpen; cy.IOpenWas = intakeOpen;
        }

        // ── Crank dynamics ───────────────────────────────────────────────────────────────────
        float friction = Friction(rpm) * (_omega > 0.5f ? 1f : 0f);
        float net = gasTorque * _torqueScale - friction - LoadTorque;
        float J = e.InertiaKgM2 + MathF.Max(0f, ExternalInertia);
        if (Starter) CrankWithStarter(net, J);
        else _omega += net / J * _dt;
        if (_omega < 0f) _omega = 0f;
        Torque = gasTorque * _torqueScale - friction;
        _rpmSlow += (Rpm - _rpmSlow) * MathF.Min(1f, _dt * 12f);

        _massFlowAcc += massOut;
        PortPeak = MathF.Max(PortPeak * 0.9995f, portPeak);

        // ── Pipes ────────────────────────────────────────────────────────────────────────────
        _exhaust.Step();
        _intake.SetValveFlow(intakeFlow);
        _intake.Step();
        // The turbine's tone leaves by the tailpipe (see Turbo): last sample's, the turbo being worked
        // out after the pipes.
        Exhaust = _exhaust.Radiated + _turbineTone;
        ExhaustShell = _exhaust.ShellRadiated;
        ExhaustPipe = _exhaust.PipeRadiated;
        // The intake's silencer. Level is an escape fraction and cannot also be the silencer: alone,
        // every road car radiated more from its airbox than its tailpipe. The airbox silences as an
        // expansion chamber (IntakeSpec.AirboxLossDb): 13 dB on an economy four, 4 on an open big-block.
        Intake = _intake.Radiated * e.Intake.Level * _airboxLoss;

        // ── The block ────────────────────────────────────────────────────────────────────────
        // Knock: the pressure-rise rate rung through the gas (modes across the bore, so a 130 mm truck
        // bore knocks near 4 kHz and an 80 mm car's near 6.6, deeper for the big engine), then
        // through the metal. Only the sharp part of the rise drives the modes; the smooth part is the
        // thud below, and fed to the resonators it only pumps them.
        _knockHp += _knockHpA * (knock - _knockHp);
        float knockDrive = knock - _knockHp;

        // The modes move while they ring: the gas cools from 2,500 K to 1,200 within a few degrees, so
        // every knock chirps down by about a third. Held still, the right frequency is heard as a note.
        // Retuned every 32 samples (0.7 ms), 1/32 of the per-sample cost.
        float knockRing;
        if (DebugLegacyDiesel) { knockRing = knockDrive; goto knockDone; }   // Stryker disable once all : lab A/B switch
        if (knockTempWgt > 1e-6f) _knockTemp = knockTempAcc / knockTempWgt;
        else _knockTemp += (1100f - _knockTemp) * _knockRelax;
        if (++_knockRetune >= 32)
        {
            _knockRetune = 0;
            float c = MathF.Sqrt(1.33f * Gas.R * Math.Clamp(_knockTemp, 500f, 3000f));
            float b = c / (MathF.PI * _knockBore);
            _knockModes[0].Retune(1.841f * b, _rate);
            _knockModes[1].Retune(3.054f * b, _rate);
            _knockModes[2].Retune(3.832f * b, _rate);
        }
        float gasRing = 0f;
        for (int i = 0; i < _knockModes.Length; i++) gasRing += _knockModes[i].Step(knockDrive);
        float structRing = 0f;
        for (int i = 0; i < _structKnock.Length; i++) structRing += _structKnock[i].Step(knockDrive);
        // Above its modes the block falls 17 dB by 6 kHz and 27 by 8 (the AVL curve), steeper than a
        // resonator's skirt: two poles at 5.5 kHz, above the highest mode.
        _structLpK1 += _structLpA * (structRing - _structLpK1);
        _structLpK2 += _structLpA * (_structLpK1 - _structLpK2);
        // Plus the gas's ring at a twentieth in pressure, the zing, well under the 20 dB below the peak
        // it is measured at.
        knockRing = _structLpK2 * StructureKnockGain + gasRing * 0.05f;
        knockDone:
        // Scaled so a truck diesel under load radiates about 95 dB of knock at a metre and a petrol
        // engine's is buried; soft-limited because a misfire's pressure jump is not the block's sound.
        float knockRaw = knockRing * e.Mechanical.CombustionKnock * 4.0e-9f;
        float knockOut = 2f * MathF.Tanh(knockRaw * 0.5f);

        // The low thud of the cylinders through the block and the mounts: structure-borne, through
        // junctions that reflect most of it, so it must not outweigh the knock and clatter that radiate
        // straight off the block. Weighted up, a truck six's block was 94 % below 200 Hz and 1.7 %
        // between 800 Hz and 2.5 kHz, where a diesel's identity lives: a formless low roar.
        _blockLp += _blockLpA * (blockLow - _blockLp);
        float thud = _blockLp * 5.0e-8f;
        // A seat's contact lasts a fraction of a millisecond, so its force has little above a couple
        // of kilohertz; then the head rings, and the same steep top lets it out.
        _valveLp += _valveLpA * (_click.Process() - _valveLp);
        float valveRing = 0f;
        for (int i = 0; i < _structValves.Length; i++) valveRing += _structValves[i].Step(_valveLp);
        _structLpV1 += _structLpA * (valveRing - _structLpV1);
        _structLpV2 += _structLpA * (_structLpV1 - _structLpV2);
        valveRing = _structLpV2;
        float mech = valveRing * StructureValveGain * e.Mechanical.ValvetrainLevel * 3.0f
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
        // The block's anchor. The mechanisms above have shapes but no absolute level; BlockRadiationGain
        // is one anchor for every engine. Without it (`--voice-levels parts`) the block was 7.5 dB low
        // at both ends: a 13 litre truck six 90.6 dB at a metre against published 97-100, a 1.6 litre
        // four 76.1 against 82-86, with the displacement scaling right (+3.7 dB over 4.6x, against +4.4
        // for a surface law). Worth nothing on a petrol car, most of a bus.
        // Past 6,000 rpm a petrol block keeps climbing: Anderton's 50 log N + 30 log B against the
        // mechanisms' 10 log N, the 40 log N being valves, piston slap, gears and chain. A litre bike's
        // block is as loud as its stock exhaust (49 % against 43 % at 5,000 rpm; Lu & Jen, Inter-noise
        // 2014); without this the bike was all pipe. Nothing at or below 6,000 changes.
        float overRated = e.Fuel == FuelType.Diesel ? 1f : MathF.Max(1f, rpm / BlockLawReferenceRpm);
        Block = (knockOut + thud + mech + whine) * BlockRadiationGain * overRated * overRated
              + (StarterOut = StarterSound() * StarterMix) + Turbo();
    }

    // ── The turbocharger ──────────────────────────────────────────────────────────────────────
    // Three sources, each at its measured level, each out of the place it leaves by: the compressor's
    // tone (81 dB at a metre at 60,000 rpm, as the fourth power of shaft speed; through the bay), the
    // whoosh (1.5-3.5 kHz, 68 dB at a metre near full speed; the bay) and the turbine's blade-pass tone
    // out of the tailpipe (in the duct 10 dB under the compressor outlet, radiated from the pipe's
    // mouth past the waveguide, whose losses above 5 kHz take it to nothing; 15 dB off with a muffler).
    // At part speed a compressor sings tip-clearance noise, a narrow hump at half its blade-passing
    // frequency; the shaft idles at 12-15,000 rpm and makes 120-130,000 at full boost. A cruise comes
    // out at 2.3-2.9 kHz. Sources and reasoning: docs/ENGINE_SYNTHESIS.md, "The turbocharger's three
    // sources". A hand-set whistle through the bay vanished floored, where a straight-piped compound
    // Cummins is 112 dB at its tailpipe.
    private const float TurboShaftIdleRpm = 12000f, TurboShaftFullRpm = 125000f;
    private const int CompressorBlades = 7, TurbineBlades = 11;
    /// <summary>Tip-clearance noise sits at this share of the blade-passing frequency.</summary>
    private const float TipClearanceShare = 0.5f;
    /// <summary>How wide the tip-clearance hump is, as a share of its centre frequency (the filter's
    /// 1/Q). Narrow enough to have a pitch, wide enough not to be a line.</summary>
    private const float HumpWidth = 0.06f;
    private const float CompressorToneDb = 81f, CompressorAtRpm = 60000f;     // at a metre
    /// <summary>The blade-passing tone against the tip-clearance hump: under it at part speed, over
    /// it at full boost, crossing between these shaft speeds.</summary>
    private const float BpfUnderDb = -6f, BpfOverDb = 6f, BpfCrossLowRpm = 60000f, BpfCrossHighRpm = 120000f;
    private const float WhooshDb = 68f, WhooshAtRpm = 110000f;                // at a metre
    /// <summary>In the duct, ten decibels under the compressor outlet (Tiikoja and Abom: the turbine
    /// is an attenuator, significant only at very high blade-passing frequencies).</summary>
    private const float TurbineDuctDb = 100f, TurbineAtRpm = 110000f;
    // The three as pascals once, not three powers a sample.
    private static readonly float CompressorPa = Pa(CompressorToneDb), WhooshPa = Pa(WhooshDb), TurbineDuctPa = Pa(TurbineDuctDb);

    private static float Pa(float db) => 20e-6f * MathF.Pow(10f, db / 20f);

    private float _tailRadius = -1f, _turbineMuffler;

    private float Turbo()
    {
        if (_tailRadius < 0f)
        {
            var x = Profile.Exhaust;
            _tailRadius = MathF.Max(0.01f, x.TailpipeDiameterMm * 0.5e-3f);
            _turbineMuffler = x.Muffler.Kind == MufflerKind.None ? 1f : Pa(-15f) / 20e-6f;
        }
        var m = Profile.Mechanical;
        if (m.TurboWhistleLevel <= 0f || Profile.Induction != Induction.Turbocharged || _omega <= 1f)
        {
            _turbineTone = 0f;
            return 0f;
        }
        // Shaft speed rises as the square of the spool from the idle speed (a freewheeling spool at
        // 0.35 is a shaft at 26,000, not 44,000): 40,000 at 0.5, 125,000 flat out.
        float sp = Math.Clamp(_spool, 0f, 1f);
        float shaft = TurboShaftIdleRpm + (TurboShaftFullRpm - TurboShaftIdleRpm) * sp * sp;
        float rev = shaft / 60f;
        // The fourth power of the shaft speed, as a pressure: the square of it.
        float Scale(float atRpm) => (shaft / atRpm) * (shaft / atRpm);
        float lvl = m.TurboWhistleLevel;

        float nyq = _rate * 0.45f;
        float comp = 0f, turb = 0f;
        float fc = CompressorBlades * rev;
        float compPa = 1.41421356f * CompressorPa * Scale(CompressorAtRpm) * lvl;
        // The tip-clearance hump: noise through a band HumpWidth wide (a sine sounds thin), at the power
        // a sine of the same amplitude would have.
        _tcnDrift += (((float)_rng.NextDouble() * 2f - 1f) - _tcnDrift) * (40f / _rate);
        float ft0 = TipClearanceShare * fc * (1f + 0.015f * _tcnDrift);
        if (ft0 < nyq)
        {
            // Zavalishin's state-variable filter: stable at any centre frequency, retuned each sample.
            float g = MathF.Tan(MathF.PI * ft0 / _rate), k = HumpWidth;
            float a1 = 1f / (1f + g * (g + k)), a2 = g * a1, a3 = g * a2;
            float x = (float)_rng.NextDouble() * 2f - 1f;
            float v3 = x - _humpS2;
            float v1 = a1 * _humpS1 + a2 * v3;
            float v2 = _humpS2 + a2 * _humpS1 + a3 * v3;
            _humpS1 = 2f * v1 - _humpS1;
            _humpS2 = 2f * v2 - _humpS2;
            // Its own power, tracked over 50 ms, sets how much to scale it by to have the sine's.
            _humpPower += (v1 * v1 - _humpPower) * (20f / _rate);
            comp += v1 * (compPa * 0.70710678f) / MathF.Sqrt(_humpPower + 1e-12f);
        }
        if (fc < nyq)
        {
            _turboPhase += fc / _rate;
            if (_turboPhase > 1.0) _turboPhase -= 1.0;
            float x = Math.Clamp((shaft - BpfCrossLowRpm) / (BpfCrossHighRpm - BpfCrossLowRpm), 0f, 1f);
            float bpfDb = BpfUnderDb + (BpfOverDb - BpfUnderDb) * x;
            comp += (float)Math.Sin(_turboPhase * 2 * Math.PI) * compPa * MathF.Pow(10f, bpfDb / 20f);
        }
        float ft = TurbineBlades * rev;
        if (ft < nyq)
        {
            _turbinePhase += ft / _rate;
            if (_turbinePhase > 1.0) _turbinePhase -= 1.0;
            turb = (float)Math.Sin(_turbinePhase * 2 * Math.PI) * 1.41421356f * TurbineDuctPa * Scale(TurbineAtRpm) * lvl
                 * _tailRadius * 0.70710678f * _turbineMuffler;
        }
        _turbineTone = turb;

        float n = (float)(_rng.NextDouble() * 2 - 1);
        float band = _wB0 * n + _wB2 * _wx2 - _wA1 * _wb1 - _wA2 * _wb2;
        _wx2 = _wx1; _wx1 = n; _wb2 = _wb1; _wb1 = band;
        float whoosh = band * _whooshNorm * WhooshPa * Scale(WhooshAtRpm) * lvl;
        return comp + whoosh;
    }

    /// <summary>The whoosh band: a band-pass biquad centred on 2.3 kHz spanning 1.5-3.5, normalised
    /// to unit RMS by measurement on its own noise.</summary>
    private void SetUpWhooshBand()
    {
        float f0 = 2300f, q = 1.15f;
        float w0 = 2f * MathF.PI * f0 / _rate, alpha = MathF.Sin(w0) / (2f * q), a0 = 1f + alpha;
        _wB0 = alpha / a0; _wB2 = -alpha / a0;
        _wA1 = -2f * MathF.Cos(w0) / a0; _wA2 = (1f - alpha) / a0;
        var rng = new Random(2);
        double e = 0; float x1 = 0, x2 = 0, y1 = 0, y2 = 0; int count = (int)(_rate * 2f), skip = (int)(_rate * 0.1f);
        for (int i = 0; i < count; i++)
        {
            float x = (float)(rng.NextDouble() * 2 - 1);
            float y = _wB0 * x + _wB2 * x2 - _wA1 * y1 - _wA2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            if (i >= skip) e += y * (double)y;
        }
        _whooshNorm = 1f / MathF.Sqrt((float)(e / (count - skip)));
    }

    // ── The starter motor ─────────────────────────────────────────────────────────────────────
    // The chug of cranking falls out of the cylinders; this is the starter itself, as a machine: a
    // solenoid, a DC motor, a 4.5:1 planetary set and a 10-tooth pinion on a 130-tooth ring. Brush and
    // gear noise (broadband 500 Hz-4 kHz in six recorded starts), weak gear lines, and a clack each
    // time the one-way clutch picks the crank up after a compression. When the engine catches the
    // clutch overruns and the motor winds down: nothing in a starter climbs in pitch. Recordings and
    // sources: docs/ENGINE_SYNTHESIS.md, "The starter motor".
    private const int RingTeeth = 130, PinionTeeth = 10, CommutatorBars = 24, ArmatureSlots = 11;
    private const float PlanetaryRatio = 4.5f;
    private const float StarterReduction = (float)RingTeeth / PinionTeeth * PlanetaryRatio;
    /// <summary>The armature's inertia, kg m^2, for the reference starter. Reflected through the whole
    /// reduction it is about half a kilogram square metre at the crank: more than the flywheel, which
    /// is why a cranking engine's speed ripples so much less than a free one's would.</summary>
    private const float ArmatureKgM2 = 1.5e-4f;
    /// <summary>How long the armature takes to wind down once the pinion is thrown out, s.</summary>
    private const float ArmatureCoastSeconds = 0.4f;
    /// <summary>A small four's starter at one metre, dB — a loud whirr, under a running engine and over
    /// an idle one's valvetrain.</summary>
    private const float StarterDbAtOneMetre = 82f;
    /// <summary>The displacement <see cref="StarterDbAtOneMetre"/> is for, litres. A starter is sized to
    /// its engine (about 1 kW for a small four, 2 for a big V8, 6 or more for a bus diesel), so its noise
    /// and armature go with the swept volume.</summary>
    private const float StarterReferenceLitres = 1.6f;
    private float StarterSize => MathF.Max(0.25f, Profile.DisplacementLitres / StarterReferenceLitres);
    /// <summary>The armature's speed as the crank would see it through the reduction, rad/s.</summary>
    private float _armOmega;
    private bool _clutchLocked, _starterWas;
    /// <summary>What the starter is pushing with this sample, as a fraction of its stall torque.</summary>
    private float _starterLoad, _starterLoadLp;
    /// <summary>For instruments: the starter's load now, 0..1.</summary>
    internal float StarterLoadNow => _starterLoadLp;
    private float _meshPhase, _armPhase, _slotPhase, _meshDrift;
    /// <summary>The starter's whine lines against its noise, a gain — normally one. Writable so an
    /// instrument can bracket it.</summary>
    public float StarterToneMix = 1f;
    private float _pink0, _pink1, _pink2, _noiseHp, _noiseHpIn, _noiseLp, _noiseLp2;
    private float _noiseHpA, _noiseLpA;
    private float _clackKick, _engageKick, _clunkKick, _clunkDelay;
    private float _clack1, _clack1b, _clack2, _clack2b, _clack3, _clack3b, _clunk, _clunkB;

    /// <summary>The engine speed at which the starter has no torque left, rad/s, through the reduction.
    /// A DC motor's torque falls linearly to its free speed and settles where it meets the engine's
    /// friction; the free speed is placed to make that the declared cranking speed.</summary>
    private float StarterFreeOmega
        => Profile.CrankingRpm * MathF.Tau / 60f / MathF.Max(0.2f, 1f - Friction(Profile.CrankingRpm) / StarterTorque());

    /// <summary>
    /// One sample of the crank while the starter is in: motor and crank locked through the one-way
    /// clutch, or apart while the crank runs ahead. Returns the crank's new speed.
    /// </summary>
    private float CrankWithStarter(float engineNet, float J)
    {
        float stall = StarterTorque();
        float ja = ArmatureKgM2 * StarterSize * StarterReduction * StarterReduction;
        if (!_starterWas)
        {
            // The pinion goes in against a standing or coasting motor; the clutch takes up when it
            // catches the crank.
            _clutchLocked = false;
            _engageKick = 1f;
            _clunkDelay = 0.02f;
        }
        float motor = stall * MathF.Max(0f, 1f - _armOmega / StarterFreeOmega);
        if (_clutchLocked)
        {
            float together = (motor + engineNet) / (J + ja);
            // The crank springing off a compression faster than the motor can follow: the clutch lets go.
            if (engineNet / J > together) _clutchLocked = false;
            else
            {
                _omega += together * _dt;
                _armOmega = _omega;
                _starterLoad = motor / stall;
                return _omega;
            }
        }
        _omega += engineNet / J * _dt;
        _armOmega += motor / ja * _dt;
        _starterLoad = 0f;
        if (_armOmega >= _omega)
        {
            // The clutch takes up again: what the two had between them is shared, and the jolt is the clack.
            float gap = _armOmega - _omega;
            float shared = (J * _omega + ja * _armOmega) / (J + ja);
            _omega = _armOmega = shared;
            _clutchLocked = true;
            _clackKick = MathF.Min(1f, _clackKick + gap / MathF.Max(1f, StarterFreeOmega) * 4f);
        }
        return _omega;
    }

    private float StarterSound()
    {
        if (!Starter && _starterWas) { _clunkKick = 0.5f; _clunkDelay = 0f; }
        bool engaged = Starter;
        if (!engaged) _armOmega -= _armOmega * _dt / ArmatureCoastSeconds;   // thrown out: it winds down
        _starterWas = Starter;
        float free = StarterFreeOmega;
        float spin = MathF.Min(1f, _armOmega / free);
        if (spin < 1e-3f && _engageKick == 0f && _clunkKick == 0f && _clackKick == 0f && MathF.Abs(_clunk) < 1e-6f) return 0f;

        // RMS pressure at a metre while it cranks steadily; everything below comes to about one there.
        float amp = 20e-6f * MathF.Pow(10f, StarterDbAtOneMetre / 20f) * MathF.Sqrt(StarterSize);
        _starterLoadLp += (_starterLoad - _starterLoadLp) * MathF.Min(1f, _dt * 200f);
        // Load against the steady cranking load: one cranking, more coming up on a compression,
        // nothing while the clutch overruns.
        float load = engaged ? MathF.Min(3f, _starterLoadLp * StarterTorque() / MathF.Max(1f, Friction(Profile.CrankingRpm))) : 0f;

        // Brushes and sliding teeth: pink from 250 Hz to about 6 kHz, as loud as the load makes it,
        // rippling at the commutator.
        float w = (float)(_rng.NextDouble() * 2 - 1);
        _pink0 = _kp0 * _pink0 + w * 0.0990460f;
        _pink1 = _kp1 * _pink1 + w * 0.2965164f;
        _pink2 = _kp2 * _pink2 + w * 1.0526913f;
        float pink = _pink0 + _pink1 + _pink2 + w * 0.1848f;
        _noiseHp = _noiseHpA * (_noiseHp + pink - _noiseHpIn); _noiseHpIn = pink;
        _noiseLp += (_noiseHp - _noiseLp) * _noiseLpA;
        _noiseLp2 += (_noiseLp - _noiseLp2) * _noiseLpA;
        float armHz = _armOmega / MathF.Tau * StarterReduction;
        _armPhase += armHz * _dt; _armPhase -= MathF.Floor(_armPhase);
        float ripple = 1f + 0.2f * MathF.Sin(MathF.Tau * CommutatorBars * _armPhase);
        // Thrown out, no current flows: what is left is the armature's bearings and windage.
        float noise = _noiseLp2 * ripple * (0.55f * spin * (engaged ? 1f : 0.3f) + 0.45f * load);

        // The ring mesh (380-830 Hz) and the armature's slots (near 2-2.5 kHz on a geared starter):
        // 8-14 dB over the noise in a 5 Hz bin in the recordings, louder under load, never quite steady.
        // They make it a whine and not a hiss.
        _meshDrift += ((float)(_rng.NextDouble() * 2 - 1) * 0.006f - _meshDrift) * _dt * 30f;
        float meshHz = engaged ? _omega / MathF.Tau * RingTeeth * (1f + _meshDrift) : 0f;
        _meshPhase += meshHz * _dt; _meshPhase -= MathF.Floor(_meshPhase);
        float mesh = (MathF.Sin(MathF.Tau * _meshPhase) + 0.3f * MathF.Sin(MathF.Tau * 2f * _meshPhase))
                   * 1.6f * MathF.Min(2f, 0.4f + 0.6f * load) * StarterToneMix;
        _slotPhase += armHz * ArmatureSlots * (1f + _meshDrift) * _dt; _slotPhase -= MathF.Floor(_slotPhase);
        // The slot whine is magnetic: it rides on speed and current, and goes on as the armature winds down.
        float slot = (MathF.Sin(MathF.Tau * _slotPhase) + 0.35f * MathF.Sin(MathF.Tau * 2f * _slotPhase))
                   * 0.8f * spin * (engaged ? MathF.Min(2f, 0.5f + 0.5f * load) : 0.5f) * StarterToneMix;
        float whine = (MathF.Sin(MathF.Tau * _armPhase) + 0.25f * MathF.Sin(MathF.Tau * 2f * _armPhase)) * 0.12f * spin * spin + slot;

        // The clutch taking up after each compression: a short knock through the housing's modes.
        float k = _clackKick; _clackKick = 0f;
        float clack = StruckMode(ref _clack1, ref _clack1b, k, 900f, 8f) + 0.6f * StruckMode(ref _clack2, ref _clack2b, k, 2300f, 8f)
                    + 0.35f * StruckMode(ref _clack3, ref _clack3b, k, 4000f, 8f);
        // Engaging: the plunger strikes (a click), and twenty milliseconds later the pinion lands on
        // the ring gear (a clunk with a body). Thrown out: a softer clunk.
        float click = _engageKick > 0f ? (float)(_rng.NextDouble() * 2 - 1) * _engageKick : 0f;
        _engageKick = _engageKick > 0.02f ? _engageKick * MathF.Exp(-_dt / 0.002f) : 0f;
        float thump = 0f;
        if (_clunkDelay > 0f) { _clunkDelay -= _dt; if (_clunkDelay <= 0f) _clunkKick = 1f; }
        else if (_clunkKick > 0f) { thump = _clunkKick; _clunkKick = 0f; }
        float clunk = StruckMode(ref _clunk, ref _clunkB, thump, 220f, 4f);

        // The noise 5 dB under what the recordings were fitted to: "still hear some white noise" (Cody, round 5).
        return amp * (0.5f * noise + mesh + whine + 0.7f * clack + 0.6f * click + 1.2f * clunk);
    }

    /// <summary>A resonant mode struck by <paramref name="kick"/>: frequency and Q.</summary>
    private float StruckMode(ref float y1, ref float y2, float kick, float hz, float q)
    {
        float r = MathF.Exp(-MathF.PI * hz / (q * _rate));
        float y = kick * (1f - r) * 4f + 2f * r * MathF.Cos(MathF.Tau * hz * _dt) * y1 - r * r * y2;
        y2 = y1; y1 = y;
        return y;
    }

    private float StarterTorque()
    {
        // Rated above the peak static compression torque (slow-cranking compression pressure on the
        // piston times the crank lever near its worst), with margin: it must push a cylinder over.
        float e = Profile.CompressionRatio;
        float pComp = Gas.Atmosphere * MathF.Pow(e, 1.2f);
        float peakStatic = pComp * _pistonArea * 0.6f * _crankRadius;
        return MathF.Max(Profile.FrictionNm * 6f + Profile.DisplacementLitres * 60f, peakStatic * 1.4f);
    }

    private static bool Crossed(float before, float after, bool wrapped, float angle)
        => wrapped ? (angle <= after || angle > before) : (angle > before && angle <= after);

    /// <summary>
    /// The state of the engine that changes slowly: gas temperature, manifold pressure, boost, and
    /// the idle governor. Every <see cref="GasEvery"/> samples.
    /// </summary>
    private void UpdateSlow(float rpm)
    {
        var e = Profile;
        float dtSlow = _dt * GasEvery;

        float flowNow = _massFlowAcc / dtSlow;
        _massFlowAcc = 0f;
        _massFlowLp += (flowNow - _massFlowLp) * MathF.Min(1f, dtSlow * 8f);

        // Governor: an idle bypass beside the plate on petrol (1-3 % of the bore), fuel on a diesel.
        // Slow on purpose: the plenum takes a fifth of a second to fill, and a quicker loop hunts by
        // hundreds of rpm; whether an idle hunts is the profile's gain to decide.
        bool pedalUp = Throttle < 0.04f;
        bool diesel = e.Fuel == FuelType.Diesel;
        // Authority is sized from what this engine needs to idle (half a per cent of the bore for a
        // 1.6, one per cent for a 7-litre), so a profile gain means the same on both.
        float unit = diesel ? 0.3f : _idleAreaFrac;
        float idleCap = diesel ? 1f : 3f * unit;
        float kp = 0.5f * unit, ki = 0.7f * unit, kd = 1.4f * unit;
        if (!_wasRunning && Ignition && _omega > 5f) { _idleIntegral = MathF.Max(_idleIntegral, 0.9f * unit); _idleAir = _idleIntegral; }
        _wasRunning = Ignition && _omega > 5f;
        _rpmFast += (Rpm - _rpmFast) * MathF.Min(1f, dtSlow * 30f);
        if (pedalUp && Ignition && _omega > 10f)
        {
            float err = ((GovernedRpm > 0f ? GovernedRpm : e.IdleRpm) - _rpmSlow) / e.IdleRpm;
            float rising = (_rpmFast - _rpmSlow) / e.IdleRpm;         // where it is heading
            _idleIntegral = Math.Clamp(_idleIntegral + err * e.IdleGovernorGain * ki * dtSlow, 0f, idleCap);
            _idleAir = Math.Clamp(_idleIntegral + e.IdleGovernorGain * (err * kp - rising * kd), 0f, idleCap);
            // The fast half of idle control is the spark, which changes torque within a cycle and stops
            // the plenum's lag making a slow surge (a carburetted engine has none; its low gain says so).
            // Twelve degrees of retard steadies an idle without flattening a big cam's lope; in a start
            // flare (over 1.5x idle) the ECU pulls timing to about TDC, as a 12-degree limit left the
            // flare burning efficiently for seconds.
            float retardLimit = _rpmSlow > 1.5f * e.IdleRpm ? -30f : -12f;
            _idleSparkTrim = diesel ? 0f : Math.Clamp(e.IdleGovernorGain * (err * 18f - rising * 36f), retardLimit, 8f);
        }
        else
        {
            if (!pedalUp) _idleIntegral *= 1f - dtSlow * 0.5f;
            _idleSparkTrim *= 1f - dtSlow * 4f;
        }
        float pedalRaw = diesel ? MathF.Max(Throttle, _idleAir) : Throttle;
        _pedal += (pedalRaw - _pedal) * MathF.Min(1f, dtSlow / 0.04f);
        float pedal = _pedal;

        // Boost follows load with the turbine's lag (the crank's for a blower) and raises the pressure
        // upstream of the throttle; the plenum decides what the manifold sees.
        float wantSpool = e.Induction switch
        {
            // The throttle's share is on top of the idle freewheel (MechanicalSpec.TurboIdleSpool), not
            // the larger of the two: as the larger, a compound truck pulling away gently held its
            // whistle flat to 1,300-1,700 rpm ("the turbo doesn't spin up right away"). On an all-speed
            // governor the pedal is up, so the fuel drives it.
            Induction.Turbocharged => TurboTarget(GovernedRpm > 0f ? pedal : Throttle, rpm, e),
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

        // Gas temperature follows the work: quick to heat, slow to cool.
        float work = Math.Clamp(0.3f * rpm / e.RedlineRpm + 0.7f * (e.Fuel == FuelType.Diesel ? pedal : open), 0f, 1f);
        float wantK = (Ignition && _omega > 10f)
            ? MathHelper.Lerp(e.Exhaust.GasCelsiusIdle, e.Exhaust.GasCelsiusFull, work) + 273.15f
            : 293f;
        float tau = wantK > _portK ? 1.2f : 3.5f;
        _portK += (wantK - _portK) * MathF.Min(1f, dtSlow / tau);
        _exhaust.UpdateGas(_portK, _massFlowLp);
    }

    /// <summary>
    /// Decides how this cycle will burn, at intake valve closing. The mass and burnt fraction came
    /// through the valves; added here is the mixing luck the model cannot integrate, as a coefficient of
    /// variation of the work per cycle: 2-4 % at load, past 10 % at a diluted idle, where combustion
    /// turns bimodal. The flame slows badly past about 25 % burnt fraction and fails past 35-40 %, and a
    /// failed charge pops in the exhaust later. The randomness is correlated between consecutive
    /// firings through a walk the whole engine shares: independent per-cylinder luck averaged smooth and
    /// a fixed pattern was a tremolo, where a lopey engine staggers in runs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void DecideCharge(ref Cylinder cy, float rpm, float load, bool firing)
    {
        var e = Profile;
        float fresh = MathF.Max(0f, cy.Mass - cy.BurntMass);
        float dilution = cy.Mass > 1e-9f ? cy.BurntMass / cy.Mass : 1f;

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
        cy.BurnDeg = BurnDuration(rpm, dilution, load);
        if (e.Fuel == FuelType.Diesel && DebugLegacyDiesel)
        {
            cy.SparkDeg = SparkAngle(rpm, load);
            cy.Premix = 0.22f;
        }
        else if (e.Fuel == FuelType.Diesel)
        {
            // Injection starts where SparkAngle says; combustion starts a delay later.
            float inject = SparkAngle(rpm, load);
            float pBar = MathF.Max(1f, cy.Pressure / 1e5f);
            float delay = IgnitionDelayDegrees(rpm, pBar, cy.Temp);
            cy.SparkDeg = inject + delay;
            // The injector's open time in crank degrees: a few at idle, most of the burn at full load.
            float injectDeg = 3f + 45f * fuelFrac;
            // What is in the cylinder when it lights burns premixed: roughly half at idle and under a
            // tenth at full load, as real engines run.
            cy.Premix = Math.Clamp(delay / MathF.Max(1e-3f, delay + injectDeg), 0.04f, 0.6f);
        }
        else
        {
            cy.SparkDeg = SparkAngle(rpm, load);
            cy.Premix = 0f;
        }
        cy.ChargeDecided = true;
    }

    private float Gaussian()
    {
        double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
    }

    // ── The valve boundary ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The orifice law's constants for one gas, worked out once: they depend on gamma alone, and the
    /// valve solver, a third of an engine's cost, evaluates the law about twenty times a sample on a V8.
    /// </summary>
    private readonly struct OrificeGas
    {
        public readonly float Crit, Choked, InvGamma, Subsonic;
        public OrificeGas(float gamma)
        {
            Crit = MathF.Pow(2f / (gamma + 1f), gamma / (gamma - 1f));
            Choked = MathF.Sqrt(gamma) * MathF.Pow(2f / (gamma + 1f), (gamma + 1f) / (2f * (gamma - 1f)));
            InvGamma = 1f / gamma;
            Subsonic = 2f * gamma / (gamma - 1f);
        }
    }
    private static readonly OrificeGas ExhaustOrifice = new(Gas.GammaExhaust), AirOrifice = new(Gas.GammaAir);
    private static OrificeGas OrificeFor(float gamma)
        => gamma == Gas.GammaExhaust ? ExhaustOrifice : gamma == Gas.GammaAir ? AirOrifice : new OrificeGas(gamma);

    /// <summary>Choked/subsonic orifice flow, kg/s, from up to down. Both pressures absolute;
    /// <paramref name="rt"/> is sqrt(R T) upstream.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float OrificeFlow(float pUp, float rt, float pDown, float area, in OrificeGas g)
    {
        if (area <= 0f || pUp <= pDown) return 0f;
        float pr = pDown / pUp;
        if (pr <= g.Crit) return area * pUp * g.Choked / rt;
        // pr^(2/g) - pr^((g+1)/g) as q(q - pr) with q = pr^(1/g): one power, not two.
        float q = MathF.Pow(pr, g.InvGamma);
        float t1 = q * (q - pr);
        if (t1 <= 0f) return 0f;
        return area * pUp * MathF.Sqrt(g.Subsonic * t1) / rt;
    }

    /// <summary>
    /// The valve solver's stopping rule: the residual worth this much pressure, pascals. Waves at the
    /// valve run from hundreds of pascals (an idling intake) to a hundred thousand (a blowdown), so a
    /// pascal is 50-100 dB under them. The rule until 2026-10-09 was 0.2 Pa, which cost about half an
    /// evaluation more a solve and changed nothing measurable (inbox/engine-cpu-2026-10-09/1-valve-solver).
    /// </summary>
    internal const float ValveTolerancePa = 1f;

    /// <summary>The solver with no slope carried from a last sample: for the lab and the tests.</summary>
    internal static float SolveValve(float a, float bStart, float Z, float area, float pCyl, float tCyl,
                                    float pipeK, float gamma, float cylMass, float dt, float uMean, float uCap, float pMean, out float mdot)
    {
        float slope = 0f;
        return SolveValve(a, bStart, Z, area, pCyl, tCyl, pipeK, gamma, cylMass, dt, uMean, uCap, pMean, out mdot, ref slope);
    }

    /// <summary>
    /// Solves the valve as a boundary between the cylinder and a waveguide.
    ///
    /// The pipe end sees an incoming wave a and returns b; its pressure is p0 + a + b and its volume
    /// velocity into the pipe (b - a)/Z, which must equal the valve's orifice flow over the port
    /// density. One monotone nonlinear equation in b. Returns b; mdot is positive out of the cylinder.
    ///
    /// The residual g(b) = (b - a)/Z - u(b) rises with a slope of at least 1/Z, because the valve's flow
    /// only falls as the port pressure rises. So from any b0 the step b0 - g(b0) Z lands on the root or
    /// past it: two evaluations bracket the root. Bracketing by the largest flow either way, as until
    /// 2026-10-09, took two evaluations of its own and left a bracket thousands of pascals wide. First
    /// comes a Newton step on the slope this valve's last solve ended with, which usually lands inside
    /// the tolerance; regula falsi (Illinois) finishes. About 2.3 evaluations a solve on a street,
    /// against 5.8; the solver was a third of an engine's cost.
    /// <paramref name="slope"/> is the valve's own, carried between samples; zero means unknown.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static float SolveValve(float a, float bStart, float Z, float area, float pCyl, float tCyl,
                                    float pipeK, float gamma, float cylMass, float dt, float uMean, float uCap, float pMean,
                                    out float mdot, ref float slope)
    {
        if (DebugRigidValves) { mdot = 0f; return a; }   // Stryker disable once all : lab switch
        // uMean is kept at zero: subtracting a running mean of the valve flow injected a false ram
        // effect worth a quarter of the charge on a big single. The pipes bleed their own DC.
        // uCap: a pipe passes about Mach 0.3 of quasi-steady flow at most; past it a choked valve into an
        // empty cylinder leaves the equation without a root.
        // Only the fluctuating flow is a wave: the mean breathing, left in, piled up as nearly half a bar
        // of DC suction in the intake runners and starved every cylinder.
        // The most the cylinder can exchange in a sample brings it to the port pressure: otherwise a
        // small cylinder at TDC overshoots every sample and rings the pipe up from nothing. Inside the
        // residual so the pipe and the cylinder see the same flow.
        float equalise = 0.5f * cylMass / dt;
        var gas = OrificeFor(gamma);
        float rtCyl = MathF.Sqrt(Gas.R * tCyl), rtPipe = MathF.Sqrt(Gas.R * pipeK);
        // The answers the flow can reach. With uMean zero the root is always inside, u being capped
        // below uMax; with a mean it may lie past an end, and then that end is the answer.
        float uMax = uCap * 1.05f;
        float lo = a - Z * uMax, hi = a + Z * uMax;
        float tol = ValveTolerancePa / Z, minSlope = 1f / Z;

        float b0 = Math.Clamp(bStart, lo, hi);
        float g0 = Residual(b0, out mdot);
        if (MathF.Abs(g0) < tol) return b0;
        float b1, g1;
        if (slope > minSlope)
        {
            // Shorter than the bracketing step, so it stays on the near side of the root's far bound.
            b1 = Math.Clamp(b0 - g0 / slope, lo, hi);
            g1 = Residual(b1, out mdot);
            if (MathF.Abs(g1) < tol) { slope = MathF.Max(minSlope, (g1 - g0) / (b1 - b0)); return b1; }
            if ((g1 > 0f) == (g0 > 0f))
            {
                // Still short of the root: bracket it from here.
                b0 = b1; g0 = g1;
                b1 = Math.Clamp(b0 - g0 * Z, lo, hi);
                g1 = Residual(b1, out mdot);
                if (MathF.Abs(g1) < tol) { slope = MathF.Max(minSlope, (g1 - g0) / (b1 - b0)); return b1; }
            }
        }
        else
        {
            b1 = Math.Clamp(b0 - g0 * Z, lo, hi);
            g1 = Residual(b1, out mdot);
            if (MathF.Abs(g1) < tol) { slope = MathF.Max(minSlope, (g1 - g0) / (b1 - b0)); return b1; }
        }
        // No change of sign only where the step was held at an end: the root is past it.
        if ((g1 > 0f) == (g0 > 0f)) { slope = 0f; return b1; }

        float bLo, gLo, bHi, gHi;
        if (g0 > 0f) { bHi = b0; gHi = g0; bLo = b1; gLo = g1; } else { bLo = b0; gLo = g0; bHi = b1; gHi = g1; }
        float b = b1;
        int side = 0;
        float width1 = float.MaxValue, width2 = float.MaxValue;
        for (int it = 0; it < 40 && bHi - bLo > 2.5f * ValveTolerancePa; it++)
        {
            float width = bHi - bLo;
            // Regula falsi crawls along the orifice law's square-root corner (a nearly empty cylinder
            // against a port at its pressure floor): halve whenever two steps have not halved the bracket.
            bool halve = width > 0.5f * width2;
            width2 = width1; width1 = width;
            b = halve ? 0.5f * (bLo + bHi) : (gLo * bHi - gHi * bLo) / (gLo - gHi);
            if (!float.IsFinite(b)) b = 0.5f * (bLo + bHi);
            float gB = Residual(b, out mdot);
            if (MathF.Abs(gB) < tol) break;
            if (halve) side = 0;
            if (gB > 0f) { bHi = b; gHi = gB; if (side == 1) gLo *= 0.5f; side = 1; }
            else { bLo = b; gLo = gB; if (side == -1) gHi *= 0.5f; side = -1; }
        }
        // The secant across what is left of the bracket. The Illinois halving makes it a little low,
        // which only shortens the next Newton step.
        slope = MathF.Max(minSlope, (gHi - gLo) / MathF.Max(1e-20f, bHi - bLo));
        return b;

        float Residual(float bb, out float m)
        {
            // Floored at a share of the port's mean pressure, not the atmosphere: against an atmospheric
            // floor a manifold below 0.05 bar still offered air from nowhere (torque on the overrun).
            float pPort = MathF.Max(0.05f * MathF.Max(pMean, 100f), pMean + a + bb);
            float rhoPort = Gas.Density(pPort, pipeK);
            if (pCyl >= pPort)
            {
                m = OrificeFlow(pCyl, rtCyl, pPort, area, gas);
                m = MathF.Min(m, equalise * (pCyl - pPort) / pCyl);
            }
            else
            {
                m = -OrificeFlow(pPort, rtPipe, pCyl, area, gas);
                m = MathF.Max(m, -equalise * (pPort - pCyl) / pCyl);
            }
            float u = Math.Clamp(m / rhoPort, -uCap, uCap);
            m = u * rhoPort;
            return (bb - a) / Z - (u - uMean);
        }
    }

    /// <summary>Diagnostic: the diesel combustion model without the ignition delay and the chamber
    /// modes, for an A/B against the full model. Set by the `legacydiesel` knob on any of the
    /// vehicle render commands.</summary>
    public static bool DebugLegacyDiesel;

    /// <summary>Diagnostic: treat every valve as shut for the pipes, so the network can be tested
    /// on its own. Never set in a game.</summary>
    public static bool DebugRigidValves;

    /// <summary>Diagnostic: hear ONE tailpipe on its own (0-based branch index), scaled up by the
    /// branch count so the level is comparable. -1 is every pipe. Set by the `pipe=` knob in the lab.
    /// Never set in a game.</summary>
    public static int DebugSoloTailpipe = -1;

    /// <summary>Where the listener stands, in the machine's frame (x across, y up, z forward, origin
    /// at the exhaust part), so each tailpipe radiates from its own place; untold, the pipes sum at one
    /// point. See <see cref="ExhaustNetwork.SetListener"/>.</summary>
    public void SetListener(Vector3 machineFrame) => _exhaust.SetListener(machineFrame);

    // Stryker disable all : diagnostic text for the lab, nothing audible depends on it
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
    // Stryker restore all

    /// <summary>Console lines about the built engine.</summary>
    public System.Collections.Generic.IEnumerable<string> Describe()
    {
        var e = Profile;
        yield return $"{e.Name}: {e.Cylinders} cyl {e.DisplacementLitres:F1} L, bore {e.BoreMm:F0} x stroke {e.StrokeMm:F0}, CR {e.CompressionRatio:F1}";
        yield return $"firing order {e.FiringOrder}, overlap {e.OverlapDegrees:F0} deg (lope {e.CamLope:F2})";
        yield return $"calibrated: heat scale {_heatScale:F2} -> EVO pressure {EvoPressureCalibratedBar:F1} bar at full load (real engines: 4-6, race cams higher)";
        foreach (var line in _exhaust.Describe()) yield return line;
    }

    /// <summary>A short mechanical tick: a puff of noise, rung by the head's structure.</summary>
    private sealed class ClickVoice
    {
        private readonly Random _rng;
        private float _env;
        private readonly float _decay;   // 0.9 a sample at 44.1 kHz: 0.2 ms
        public ClickVoice(float rate, int seed) { _rng = new Random(seed); _decay = At44k.Decay(0.9f, rate); }
        public void Trigger(float amp)
            => _env = MathF.Min(1.5f, _env + amp * (0.7f + 0.6f * (float)_rng.NextDouble()));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Process()
        {
            // The impact alone: the head's structure (_structValves) does the ringing. A pole of its own
            // at 3.1-4.4 kHz made the valvetrain zippy.
            if (_env < 1e-5f) return 0f;
            float x = _env * ((float)_rng.NextDouble() * 2f - 1f);
            _env *= _decay;
            return x;
        }
    }
}
