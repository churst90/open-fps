using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Aircraft;
using OpenFPS.Client.AudioEngine.Core.Signals;

namespace OpenFPS.Client.AudioEngine.Core.Yard;

/// <summary>
/// A machine that stands in one place and runs: a lawn mower, an air-conditioning condenser.
///
/// Assembled from parts that already existed, which is the point of it. The engine is
/// <see cref="EngineSynth"/> — the same solver that runs a V8 — with a governor on the throttle
/// instead of a driver. The blade and the fan are <see cref="BladeRow"/>, the aircraft's propeller,
/// because a mower blade and a condenser fan and a propeller are one mechanism at three sizes. The
/// deck and the cabinet are resonances taken from their own dimensions. Nothing here is a sample and
/// nothing here is an equaliser curve.
///
/// Outputs are pascals at one metre in the machine's frame, exactly like an engine's or an
/// aircraft's, so distance, Doppler, occlusion and the room are somebody else's business.
/// </summary>
public sealed class SmallMachineSynth
{
    public readonly SmallMachineSpec Spec;
    private readonly float _rate, _dt;
    private readonly Random _rng;

    // ── What the machine is being asked to do ───────────────────────────────────────────────────

    /// <summary>How hard the working part is being loaded, 0..1. For a mower this is how thick the
    /// grass is; it is the ONLY input a governed engine has, because the throttle is not yours.</summary>
    public float Load { get; set; } = 0.25f;

    /// <summary>How fast the machine is being walked or driven over the ground, m/s. Decides how much
    /// grass arrives per second, and therefore whether there is any cutting sound at all.</summary>
    public float GroundSpeed { get; set; }

    /// <summary>Running at all. Switching this off lets a petrol engine die the way it dies — the
    /// blade keeps turning for a while on its own inertia — and drops an air conditioner to its fan.</summary>
    public bool Running { get; set; } = true;

    /// <summary>Whether the compressor is being called for. An air conditioner's fan runs on while
    /// the thermostat is satisfied and the compressor is not, which is a sound everybody knows and
    /// nobody could name.</summary>
    public bool CompressorOn { get; set; } = true;

    // ── What it is doing, after Step() ──────────────────────────────────────────────────────────

    public float Engine { get; private set; }
    public float Blades { get; private set; }
    public float Cutting { get; private set; }
    public float Compressor { get; private set; }
    public float Casing { get; private set; }
    public float Total { get; private set; }

    /// <summary>Crank speed, rpm. A governed engine's rpm IS the load readout.</summary>
    public float Rpm { get; private set; }
    /// <summary>What the governor is asking for, 0..1.</summary>
    public float Throttle { get; private set; }
    /// <summary>Blade or fan speed, rpm.</summary>
    public float BladeRpm { get; private set; }

    private readonly EngineSynth? _engine;
    private readonly BladeRow[] _blades = Array.Empty<BladeRow>();
    private Mode _deckDepth, _deckWidth, _cutBand, _shell;
    private readonly Mode[] _panel = Array.Empty<Mode>();
    private readonly float[] _panelWeight = Array.Empty<float>();
    private readonly float[] _panelHz = Array.Empty<float>();
    private readonly float _deckDry = 1f, _deckWet;
    private readonly float _cutAmp, _humAmp, _pulseAmp, _flowAmp, _casingAmp;
    private readonly float _swathMetres;

    private float _bladeRpm;
    private double _humPhase, _pulsePhase;
    private float _compressorUp;
    private float _flowLp1, _flowLp2;
    private float _casingDrive;
    private Vector3 _listener = new(0f, 1.6f, -3f);
    private int _slowTick;
    private const int SlowEvery = 64;

    public SmallMachineSynth(SmallMachineSpec spec, float rate = 44100f, int seed = 17)
    {
        Spec = spec;
        _rate = rate;
        _dt = 1f / rate;
        _rng = new Random(seed);

        if (spec.EngineKey != null)
        {
            _engine = new EngineSynth(EngineProfile.ByName(spec.EngineKey), rate, seed + 1);
            _engine.ExternalInertia = spec.DrivenInertiaKgM2;
            _engine.Ignition = true;
        }

        if (spec.Blade is { } b)
        {
            int rows = Math.Max(1, spec.BladeRows);
            _blades = new BladeRow[rows];
            // A blade row's axis is vertical: a mower's blade lies flat and a condenser fan blows
            // straight up, so the listener standing beside either of them is IN the disc plane,
            // where the thickness noise is loudest. That is not a detail — it is why you hear a
            // mower's roar from across a garden and an aircraft's propeller mostly as it turns.
            for (int i = 0; i < rows; i++) _blades[i] = new BladeRow(b, rate, Vector3.UnitY, seed + 10 + i * 7);
            _bladeRpm = b.RpmIdle;
        }

        if (spec.Deck is { } d)
        {
            _deckDepth = new Mode(d.DepthModeHz, d.CavityQ, rate);
            _deckWidth = new Mode(d.WidthModeHz, d.CavityQ * 0.8f, rate);
            _deckWet = Math.Clamp(d.PanShare, 0f, 1f);
            _deckDry = 1f - _deckWet;
            _swathMetres = d.DiameterMetres * Math.Max(1, spec.BladeRows);
        }

        if (spec.Cutting is { } c)
        {
            _cutBand = new Mode(c.CentreHz, c.Q, rate);
            // One clipping at the blade's tip speed: half m v squared into the pan, through the
            // impact constant every other struck thing in this engine uses.
            _cutAmp = Db(c.ImpactDb(spec.Blade?.TipSpeed(spec.Blade.RpmMax) ?? 80f));
        }

        if (spec.Compressor is { } comp)
        {
            _shell = new Mode(comp.ShellHz, comp.ShellQ, rate);
            _humAmp = Db(comp.HumDb);
            _pulseAmp = Db(comp.PulsationDb);
            _flowAmp = Db(comp.FlowDb);
        }

        if (spec.Casing is { } cas)
        {
            var mat = AcousticRegistry.GetProperties(cas.Material);
            var modes = PanelAcoustics.Modes(mat, cas.WidthMetres, cas.HeightMetres, cas.ThicknessMm / 1000f, 5000f, 5);
            int n = Math.Min(5, modes.Count);
            _panel = new Mode[n];
            _panelWeight = new float[n];
            _panelHz = new float[n];
            float total = 0f;
            for (int i = 0; i < n; i++)
            {
                // How long a panel rings at that note is its Q: T60 = 2.2/(eta f), and Q = pi f T60 / ln(1000).
                float t60 = PanelAcoustics.RingSeconds(mat, modes[i].Hz);
                float q = Math.Clamp(MathF.PI * modes[i].Hz * t60 / 6.908f, 1.5f, 60f);
                _panel[i] = new Mode(modes[i].Hz, q, rate);
                _panelHz[i] = modes[i].Hz;
                _panelWeight[i] = modes[i].Weight;
                total += modes[i].Weight;
            }
            for (int i = 0; i < n; i++) _panelWeight[i] /= MathF.Max(1e-6f, total);
            _casingAmp = Math.Clamp(cas.Coupling, 0f, 1f);
        }
    }

    private static float Db(float db) => 20e-6f * MathF.Pow(10f, db / 20f);

    /// <summary>Where the listener stands in the machine's frame: x right, y up, z forward.</summary>
    public void SetListener(Vector3 machineFrame)
    {
        if (machineFrame.LengthSquared() < 1e-4f) return;
        _listener = machineFrame;
    }

    private void UpdateSlow()
    {
        Vector3 dir = Vector3.Normalize(_listener);
        float load = Math.Clamp(Load, 0f, 1f);

        if (_engine != null)
        {
            float rpm = _engine.Rpm;
            // ── The governor, and nothing else, moves this throttle ──────────────────────────────
            //
            // A proportional controller with a droop: see GovernorSpec. Its output is slewed at its
            // own response rate, because a pair of flyweights and a spring have mass, and the lag
            // between the load arriving and the throttle answering is the *bog* — the half second a
            // mower spends sounding like it is about to stall before it picks up again.
            var gov = Spec.Governor;
            float want = gov != null && Running ? gov.Throttle(rpm) : (Running ? 0.3f : 0f);
            float a = gov != null ? MathF.Min(1f, _dt * SlowEvery * MathF.Tau * gov.ResponseHz) : 1f;
            Throttle += (want - Throttle) * a;
            _engine.Throttle = Math.Clamp(Throttle, 0f, 1f);
            _engine.Ignition = Running;
            _engine.Starter = Running && rpm < 400f;

            // What the crank is dragging. A blade in air is a fan law — torque with the square of
            // speed — and the grass on top of it is proportional to how much grass arrives, which is
            // ground speed times swath. The two are different shapes, which is why a mower bogs when
            // you push it into long grass and not when you rev it in the driveway.
            float rated = _engine.Profile.PeakTorqueNm;
            float fan = 0.55f * rated / (Spec.Governor?.SettingRpm ?? 3000f) / (Spec.Governor?.SettingRpm ?? 3000f);
            float torque = fan * rpm * rpm;
            if (Spec.Cutting != null)
                torque += rated * 0.45f * load * Math.Clamp(GroundSpeed / 1.4f, 0f, 1.5f);
            _engine.LoadTorque = torque;
            _engine.SetListener(_listener);

            Rpm = rpm;
            _bladeRpm = rpm * Spec.BladeGearRatio;
        }
        else if (Spec.Blade is { } fanSpec)
        {
            // An electric fan: it is either on at its one speed or spinning down.
            float want = Running ? fanSpec.RpmMax : 0f;
            _bladeRpm += (want - _bladeRpm) * MathF.Min(1f, _dt * SlowEvery / 2.5f);
            Rpm = 0f;
        }

        BladeRpm = _bladeRpm;
        // How hard the blades are working. A mower's blade is loaded by the grass it is cutting; a
        // fan's loading is whatever the coil in front of it makes it, and that does not change.
        float blading = Spec.Cutting != null
            ? Math.Clamp(0.3f + 0.7f * load * Math.Clamp(GroundSpeed / 1.2f, 0f, 1.3f), 0f, 1.2f)
            : 0.8f;
        for (int i = 0; i < _blades.Length; i++)
        {
            // Two blades on one deck are not synchronised; a few per cent between them is what makes
            // a wide deck beat slowly instead of ringing on one note.
            float trim = _blades.Length == 1 ? 1f : 1f + (i - (_blades.Length - 1) * 0.5f) * 0.03f;
            _blades[i].SetSpeed(_bladeRpm * trim, blading, dir);
        }

        if (Spec.Compressor != null)
        {
            float want = CompressorOn && Running ? 1f : 0f;
            float tau = MathF.Max(0.05f, Spec.Compressor.StartSeconds);
            _compressorUp += (want - _compressorUp) * MathF.Min(1f, _dt * SlowEvery / tau);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Step()
    {
        if (_slowTick == 0) UpdateSlow();
        _slowTick = _slowTick + 1 == SlowEvery ? 0 : _slowTick + 1;

        float engine = 0f, blades = 0f, cutting = 0f, compressor = 0f, casing = 0f;

        if (_engine != null)
        {
            _engine.Step();
            engine = _engine.Exhaust + _engine.Intake + _engine.Block;
        }

        for (int i = 0; i < _blades.Length; i++) blades += _blades[i].Step();

        // ── The deck ────────────────────────────────────────────────────────────────────────────
        //
        // Some of the blade's noise leaves straight out of the open bottom and is heard as it is;
        // the rest goes round inside a shallow steel pan and is heard through the pan's two
        // resonances, the quarter wave over its depth and the half wave across it.
        if (Spec.Deck != null)
        {
            // The pan ADDS. Sound made under a deck leaves through the open bottom whatever happens,
            // and what the pan does is send some of it round again and give it back at the cavity's
            // own two frequencies. Modelling that as a blend — part direct, part filtered — made the
            // deck a LOSS of five decibels, which is the opposite of what a resonator does and the
            // opposite of why mower decks are loud.
            blades += _deckWet * (_deckDepth.Process(blades) + 0.7f * _deckWidth.Process(blades)) * 2.4f;
        }

        // ── The grass ───────────────────────────────────────────────────────────────────────────
        //
        // A rate of stalks, not a texture: stalks per square metre times the swath times how fast the
        // machine is walking. Nothing is cut while a tip is not in standing grass, so the whole train
        // is gated by the blade passing — and a mower standing still cuts nothing at all, which is
        // the difference you hear when somebody stops to turn round.
        if (Spec.Cutting is { } cut && _bladeRpm > 100f)
        {
            float perSecond = cut.StalksPerSquareMetre * _swathMetres * MathF.Max(0f, GroundSpeed)
                            * Math.Clamp(Load, 0f, 1f);
            if (perSecond > 1f)
            {
                // The blade-passing gate: a raised cosine open for about a third of each passage.
                float bpf = Spec.Blade!.Blades * _bladeRpm / 60f * MathF.Max(1, Spec.BladeRows);
                float gatePhase = (float)((_gate += bpf * _dt) % 1.0);
                float gate = gatePhase < 0.35f ? 0.5f - 0.5f * MathF.Cos(gatePhase / 0.35f * MathF.Tau) : 0f;
                if (_gate > 1e7) _gate = 0;

                float expected = perSecond * _dt * gate * 2.9f;   // 2.9 = 1/mean(gate)
                // Poisson: at these rates it is many per sample, so the count is the rate and the
                // randomness is in the amplitude. Below one per sample it is a Bernoulli trial.
                float hit;
                if (expected >= 1f) hit = expected + MathF.Sqrt(expected) * (float)(_rng.NextDouble() * 2 - 1);
                else hit = _rng.NextDouble() < expected ? 1f : 0f;
                cutting = _cutBand.Process(hit * _cutAmp * (float)(_rng.NextDouble() * 2 - 1)) * 6f;
            }
        }

        // ── The compressor ──────────────────────────────────────────────────────────────────────
        if (Spec.Compressor is { } comp && _compressorUp > 1e-3f)
        {
            float up = _compressorUp;
            // The hum is the MAINS: twice the line frequency, and it does not move with anything.
            _humPhase += comp.HumHz / _rate;
            if (_humPhase > 1.0) _humPhase -= 1.0;
            double h = _humPhase * Math.Tau;
            float hum = (float)(Math.Sin(h) + 0.42 * Math.Sin(2 * h) + 0.20 * Math.Sin(3 * h)) * _humAmp * up;

            // The pump is the SHAFT, which is a few per cent slower than synchronous under load and
            // slower still while it is coming up to speed. The two series beat, and that beat is the
            // whole difference between a compressor and a mains transformer.
            _pulsePhase += comp.PulsationHz * (0.55f + 0.45f * up) / _rate;
            if (_pulsePhase > 1.0) _pulsePhase -= 1.0;
            double g = _pulsePhase * Math.Tau;
            float pump = (float)(Math.Sin(g) + 0.55 * Math.Sin(2 * g) + 0.30 * Math.Sin(3 * g)) * _pulseAmp * up * up;

            // Gas in the discharge line: the only broadband part of a compressor.
            float n = (float)(_rng.NextDouble() * 2 - 1);
            float a = OnePole.AlphaFor(1400f, _rate);
            _flowLp1 += a * (n - _flowLp1);
            _flowLp2 += a * (_flowLp1 - _flowLp2);
            float flow = (_flowLp1 - _flowLp2) * _flowAmp * up * 8f;

            // ...and all of it is inside a steel can. At 120 Hz the can is far smaller than a
            // wavelength and stiff, so it passes the hum essentially untouched; what it adds is its
            // own ring where it rings. A can that ATTENUATED what is inside it would be a silencer,
            // and a compressor is not quiet.
            float inside = hum + pump + flow;
            compressor = inside + 0.5f * _shell.Process(inside) * 2.2f;
            _casingDrive = compressor;
        }
        else _casingDrive *= 0.999f;

        // ── The box it is all bolted into ───────────────────────────────────────────────────────
        if (_panel.Length > 0 && _casingAmp > 0f)
        {
            float drive = _casingDrive + 0.15f * blades;
            for (int i = 0; i < _panel.Length; i++) casing += _panel[i].Process(drive) * _panelWeight[i];
            casing *= _casingAmp * 1.8f;
        }

        Engine = engine; Blades = blades; Cutting = cutting; Compressor = compressor; Casing = casing;
        Total = engine + blades + cutting + compressor + casing;
    }

    private double _gate;

    /// <summary>Console lines about what was built, in the units the parts are written in.</summary>
    public IEnumerable<string> Describe()
    {
        var s = Spec;
        yield return $"{s.Name}: {s.SourceLevelDb:F0} dB at 1 m, {s.ExtentMetres:F1} m across";
        if (s.Governor is { } g)
            yield return $"governor: set {g.SettingRpm:F0} rpm, droop {g.Droop * 100f:F0} % "
                       + $"({g.SettingRpm * (1f - g.Droop):F0} rpm at full throttle), responds at {g.ResponseHz:F0} Hz";
        if (s.Blade is { } b)
        {
            float bpf = b.BladePassHz(b.RpmMax) * MathF.Max(1, s.BladeRows);
            yield return $"blade: {s.BladeRows} x {b.Blades} tips x {b.DiameterMetres:F2} m at {b.RpmMax:F0} rpm -> "
                       + $"blade-pass {bpf:F0} Hz, tip {b.TipSpeed(b.RpmMax):F0} m/s (Mach {b.TipSpeed(b.RpmMax) / 340f:F2}), {b.ReferenceDb:F0} dB at 1 m";
        }
        if (s.Deck is { } d)
            yield return $"deck: {d.DiameterMetres:F2} m x {d.DepthMetres * 100f:F0} cm pan -> depth mode {d.DepthModeHz:F0} Hz, "
                       + $"across {d.WidthModeHz:F0} Hz, Q {d.CavityQ:F1}, {d.PanShare * 100f:F0} % through the pan";
        if (s.Cutting is { } c)
            yield return $"cutting: {c.StalksPerSquareMetre:F0} shoots/m^2 over {_swathMetres:F2} m of swath -> "
                       + $"{c.StalksPerSquareMetre * _swathMetres:F0} a second at 1 m/s, "
                       + $"{c.ClippingMilligrams:F0} mg at {s.Blade?.TipSpeed(s.Blade.RpmMax) ?? 0f:F0} m/s = "
                       + $"{c.ImpactDb(s.Blade?.TipSpeed(s.Blade.RpmMax) ?? 80f):F0} dB each at {c.CentreHz:F0} Hz";
        if (s.Compressor is { } comp)
            yield return $"compressor: hum {comp.HumHz:F0} Hz (2 x {comp.LineHz:F0} Hz mains), shaft {comp.ShaftRpm:F0} rpm -> "
                       + $"pumping {comp.PulsationHz:F0} Hz, can rings {comp.ShellHz:F0} Hz, {comp.HumDb:F0}/{comp.PulsationDb:F0}/{comp.FlowDb:F0} dB";
        if (s.Casing is { } cas)
            yield return $"casing: {cas.WidthMetres:F2} x {cas.HeightMetres:F2} m of {cas.ThicknessMm:F1} mm {cas.Material} -> "
                       + $"rings {cas.RingHz:F0} Hz, driven at {string.Join(", ", _panelHz.Select(h => h.ToString("F0")))} Hz, coupling {cas.Coupling:F2}";
        if (_engine != null) foreach (var line in _engine.Describe()) yield return "engine: " + line;
    }
}
