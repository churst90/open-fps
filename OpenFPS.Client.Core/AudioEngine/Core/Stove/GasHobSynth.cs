using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Stove;

/// <summary>
/// A gas hob (GasHobSpec, docs/GAS_HOB.md), pascals at a metre. A hand works the knobs from a queue of
/// actions; under each knob a plug valve feeds an injector, whose jet draws air down a mixing tube into
/// the burner head and out of its ports; one spark module sparks every electrode while any knob is held in
/// (or, a re-ignition module, while a burner that is on has no flame);
/// a spark lights a burner when the mixture it crosses is rich enough for its energy, and whatever gas has
/// gathered round the burner burns at once; a thermocouple in the flame holds the gas on where the hob has
/// flame safety, and a re-ignition module stops when it senses the flame. Nothing is
/// random but the turbulence and a spark module's jitter: whether a spark fails is the gas's doing.
///
/// The sound is three things. Heat released unsteadily, p = (γ-1)/(4π r c²) dQ/dt: each spark's crack
/// (a millijoule in tens of microseconds), the light-up's thump and whoosh, the flames' roar, the pop as a
/// flame goes out. The gas jets' mixing noise, by Lighthill. The parts struck: the switch behind a knob,
/// its detent and stops, the cap the spark hits, the module, the safety valve's armature.
///
/// Physics runs a step a millisecond; the audio a sample at a time. Allocation-free once built.
/// </summary>
public sealed class GasHobSynth
{
    public readonly GasHobSpec Spec;
    private readonly FuelGas _gas;
    private readonly float _fs, _dt;
    private readonly int _stepSamples;
    private readonly float _stepDt;
    private int _untilStep;
    private uint _rng;

    // ── Lab switches: 1 plays a part, 0 mutes it. ──
    public float SparkPart = 1f, ClickPart = 1f, HissPart = 1f, FlamePart = 1f, LightUpPart = 1f;

    private const float Rho0 = 1.2f, C0 = 343f;

    // ── The burners ──────────────────────────────────────────────────────────────────────────────

    private sealed class Burner
    {
        public GasBurnerSpec Spec = null!;
        public float FullFlow, FullHeat, FullPortVelocity, PortArea, HeadVolume, CloudVolume, InjectorD, Radius;
        // The knob and the valve.
        public float Degrees;
        public bool Pushed, MagnetHeld;
        public float Thermocouple;
        public float FlameSeen;         // how long the flame has been on the electrode, s (a re-ignition module senses it)
        // The gas.
        public float PortFlow;          // mixture out of the ports, m³/s
        public float HeadFraction;      // gas share of the mixture in the head
        public float Cloud;             // unlit gas gathered round the burner, m³
        public float Share;             // valve flow share now
        // The flame.
        public bool Lit;
        public float HeatTarget;        // W, the flow's heat now
        public float Heat1, Heat2;      // the flame's heat release, lagged (two poles)
        public float Envelope;          // how much of the ring is burning, 0..1
        public float EnvelopeStep;      // per sample while lighting
        public bool Quenching;
        public float QuenchPhase, QuenchStep, QuenchFrom;
        // The light-up: the gas in the ports' mixing layers burning as the flame runs round the ring, and the
        // leaner gas gathered round the burner burning after it at its own slower pace.
        public float FastEnergy, FastPhase, FastStep;
        public float SlowEnergy, SlowPhase, SlowStep;
        public int SparksFailed, LastSparksFailed;
        public float LastFlashJoules, LastLightDelay, GasSince;
        // The sound.
        public PowerLawNoise Roar, Whoosh;
        public float RoarUnit, WhooshUnit, FlickerLp, Flicker;
        public float InjLp1, InjLp2, ValveLp1, ValveLp2;
        public float InjAmp, InjAlpha, ValveAmp, ValveAlpha;
        public Modes Cap;
        public float DirectDelay, ImageDelay;
    }

    private readonly Burner[] _b;

    /// <summary>A small bank of decaying modes, struck.</summary>
    private struct Modes
    {
        public float A10, A20, A11, A21, A12, A22, A13, A23;   // 2 r cos w, r²
        public float Y10, Y20, Y11, Y21, Y12, Y22, Y13, Y23;
        public float S0, S1, S2, S3;                             // sin w, for a peak of one
        public float W0, W1, W2, W3;                             // shares

        public void Tune(float fs, float f0, float f1, float f2, float f3, float t60, float w0 = 1f, float w1 = 0.7f, float w2 = 0.5f, float w3 = 0.35f)
        {
            float r = MathF.Exp(-6.9078f / (t60 * fs));
            Set(fs, f0, r, out A10, out A20, out S0);
            Set(fs, f1, r, out A11, out A21, out S1);
            Set(fs, f2, r, out A12, out A22, out S2);
            Set(fs, f3, r, out A13, out A23, out S3);
            W0 = w0; W1 = w1; W2 = w2; W3 = w3;
            float sum = w0 + w1 + w2 + w3;
            W0 /= sum; W1 /= sum; W2 /= sum; W3 /= sum;
        }

        private static void Set(float fs, float f, float r, out float a1, out float a2, out float s)
        {
            float w = MathF.Tau * MathF.Min(f, 0.45f * fs) / fs;
            a1 = 2f * r * MathF.Cos(w);
            a2 = r * r;
            s = MathF.Sin(w);
        }

        /// <summary>Strikes the bank so its modes together peak at about <paramref name="pascals"/>.</summary>
        public void Strike(float pascals)
        {
            Y10 += pascals * W0 * S0; Y11 += pascals * W1 * S1; Y12 += pascals * W2 * S2; Y13 += pascals * W3 * S3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Step()
        {
            float a = A10 * Y10 - A20 * Y20; Y20 = Y10; Y10 = a;
            float b = A11 * Y11 - A21 * Y21; Y21 = Y11; Y11 = b;
            float c = A12 * Y12 - A22 * Y22; Y22 = Y12; Y12 = c;
            float d = A13 * Y13 - A23 * Y23; Y23 = Y13; Y13 = d;
            return a + b + c + d;
        }

        public readonly bool Quiet => MathF.Abs(Y10) + MathF.Abs(Y11) + MathF.Abs(Y12) + MathF.Abs(Y13) < 1e-7f;
    }

    // ── The struck parts of the knobs and the module ─────────────────────────────────────────────

    private Modes _switch, _detent, _stop, _magnet, _module;

    // ── The sparks' cracks: band-limited kernels, one per spark duration and sub-sample phase ────

    private const int KernelPhases = 16, KernelLead = 16, KernelLength = 64, KernelDurations = 3;
    private readonly float[][] _kernels;        // [duration * phases + phase][tap], Pa per joule of heat
    private struct Crack { public int Kernel, Pos, Delay; public float Gain; }
    private readonly Crack[] _cracks = new Crack[48];
    private int _crackCount;

    // ── The spark module ─────────────────────────────────────────────────────────────────────────

    private float _charge, _chargeStep, _breakover, _mainsPhase;
    private float _moduleLp;
    private readonly float _moduleAlpha;
    private bool _moduleOn;
    private readonly int _cyclesPerSpark;
    private float _mainsClock;

    // ── The hand ─────────────────────────────────────────────────────────────────────────────────

    public enum Act : byte { Move, Push, Release, Turn, WaitLit, WaitAngleLit, Wait }
    public struct HandAction
    {
        public Act Act;
        public int Burner;
        public float Degrees, Seconds, Hold;
    }
    private readonly HandAction[] _queue = new HandAction[64];
    private int _qHead, _qCount;
    private bool _acting;
    private HandAction _now;
    private float _actElapsed, _turnFrom, _litFor;
    private int _handAt = -1;

    // ── The flames' low-frequency pressure ───────────────────────────────────────────────────────

    private float _heatPrev, _heatAlpha, _thumpLp, _thumpAlpha;
    private readonly float _flickerAlpha, _flickerScale, _jetTrim, _roarScale;

    // ── The listener ─────────────────────────────────────────────────────────────────────────────

    private Vector3 _listener = new(0f, 0.45f, -0.9f);
    private bool _listenerChanged = true;

    /// <summary>Whether everything is quiet and off: nothing burning, no gas, no hand at work, nothing ringing.</summary>
    public bool Idle { get; private set; }

    /// <summary>Sparks made since the start, for the lab.</summary>
    public int Sparks { get; private set; }

    private bool _silent;

    public GasHobSynth(GasHobSpec spec, float sampleRate, int seed)
    {
        Spec = spec;
        _gas = spec.Fuel;
        _fs = sampleRate;
        _dt = 1f / sampleRate;
        _stepSamples = Math.Max(1, (int)MathF.Round(sampleRate / 1000f));
        _stepDt = _stepSamples / sampleRate;
        _rng = (uint)(seed * 2654435761u) | 1u;

        _b = new Burner[spec.Burners.Length];
        for (int i = 0; i < _b.Length; i++)
        {
            var bs = spec.Burners[i];
            var b = new Burner
            {
                Spec = bs,
                FullFlow = spec.FullFlow(bs),
                FullHeat = spec.FullHeatWatts(bs),
                FullPortVelocity = spec.PortVelocity(bs),
                PortArea = bs.PortAreaMm2 * 1e-6f,
                HeadVolume = bs.HeadVolumeCm3 * 1e-6f,
                CloudVolume = GasHobSpec.CloudVolume(bs),
                InjectorD = bs.InjectorMm * 1e-3f,
                Radius = 0.5f * bs.CrownMm * 1e-3f,
            };
            b.Roar.Tune(spec.FlameSlope, spec.FlamePeakHz(bs), sampleRate);
            b.RoarUnit = UnitRms(ref b.Roar);
            // The light-up's front is stirred by the same jets: the same spectrum, its own stream.
            b.Whoosh.Tune(spec.FlameSlope, spec.FlamePeakHz(bs), sampleRate);
            b.WhooshUnit = UnitRms(ref b.Whoosh);
            // The cap: a free disc of enamelled steel, its first four modes (Leissa's λ² for a free circular
            // plate, ν = 0.3: 5.253, 9.084, 12.23, 20.52), its ring cut short where it sits on the crown.
            float a = 0.5f * bs.CapMm * 1e-3f, h = bs.CapThicknessMm * 1e-3f;
            float plate = h * MathF.Sqrt(200e9f / (12f * 7850f * (1f - 0.09f)));
            float f1 = 5.253f / (MathF.Tau * a * a) * plate;
            b.Cap.Tune(sampleRate, f1, f1 * 9.084f / 5.253f, f1 * 12.23f / 5.253f, f1 * 20.52f / 5.253f, 0.04f);
            _b[i] = b;
        }

        // The knob's parts [estimate]: a snap-action switch in a plastic case, a plastic knob dropping into a
        // spring detent, the tap's spindle on its stop, a brass armature on its seat; and the module's
        // pulse transformer.
        _switch.Tune(sampleRate, 3800f, 6100f, 8900f, 11200f, 0.008f);
        _detent.Tune(sampleRate, 1900f, 3400f, 5200f, 7400f, 0.012f);
        _stop.Tune(sampleRate, 1300f, 2700f, 4600f, 6900f, 0.018f);
        _magnet.Tune(sampleRate, 2600f, 4900f, 7300f, 9800f, 0.010f);
        _module.Tune(sampleRate, 1600f, 3300f, 5600f, 8100f, 0.015f, 1f, 0.6f, 0.3f, 0.15f);
        // Heard through the hob's steel tray: its gaps, and a mass law's 6 dB an octave from 1 kHz.
        _moduleAlpha = OnePole.AlphaFor(1000f, sampleRate);

        _kernels = KernelsFor(sampleRate, spec.SparkMicroseconds);

        // The module: a capacitor charged a step each mains cycle through a resistor, fired by a breakover
        // device. The step is set so the threshold falls half way between two cycles' charges, so the
        // rate is the mains over a whole number of cycles and a slip of one is rare.
        _cyclesPerSpark = Math.Max(2, (int)MathF.Round(spec.MainsHz / MathF.Max(0.5f, spec.SparkRateHz)));
        _breakover = 0.8f;
        _chargeStep = 1f - MathF.Pow(1f - _breakover, 1f / (_cyclesPerSpark - 0.5f));

        _heatAlpha = OnePole.AlphaFor(25f, sampleRate);
        _thumpAlpha = OnePole.AlphaFor(3000f, sampleRate);
        _flickerAlpha = OnePole.AlphaFor(8f, sampleRate);
        // A one-pole's output variance is a / (2 - a) of its input's: scaled back to one.
        _flickerScale = 1f / MathF.Sqrt(_flickerAlpha / (2f - _flickerAlpha));
        _jetTrim = MathF.Pow(10f, spec.JetTrimDb / 20f);
        // Free-field pressure from acoustic power at a metre: p² = W ρc / 4π.
        _roarScale = MathF.Sqrt(Rho0 * C0 / (4f * MathF.PI));
        _mainsPhase = (float)((seed & 0xffff) / 65536.0);
        Idle = true;
    }

    // ── What the lab and the voice ask of it ─────────────────────────────────────────────────────

    public int BurnerCount => _b.Length;
    public bool IsLit(int burner) => _b[burner].Lit;
    public float KnobDegrees(int burner) => _b[burner].Degrees;
    public int FailedSparksBeforeLight(int burner) => _b[burner].LastSparksFailed;
    public float LastLightUpJoules(int burner) => _b[burner].LastFlashJoules;
    public float LastLightDelay(int burner) => _b[burner].LastLightDelay;
    public bool HandBusy => _acting || _qCount > 0;

    /// <summary>The listener in the hob's own frame, m (x right as you face it, y up from the hob top, z away).</summary>
    public void SetListener(Vector3 frame)
    {
        if (Vector3.DistanceSquared(frame, _listener) < 1e-4f) return;
        _listener = frame;
        _listenerChanged = true;
    }

    /// <summary>
    /// Sets the burners as they stood before the last change (one digit a burner, 0 off to 3 high), queues
    /// the hand to make the change, and runs <paramref name="secondsAgo"/> of it silently: a client hearing a
    /// change late hears the rest of it, a client arriving long after hears the burners as they are.
    /// </summary>
    public void Begin(string from, string to, float secondsAgo)
    {
        for (int i = 0; i < _b.Length; i++) SetSteady(_b[i], Digit(from, i));
        // Burning all along, not lit this instant: a flame's heat from nothing is a step in dQ/dt.
        _heatPrev = TotalHeatNow();
        Change(from, to);
        if (secondsAgo > 0.05f) Advance(MathF.Min(secondsAgo, 90f));
    }

    /// <summary>Queues the hand to turn every knob that differs from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public void Change(string from, string to)
    {
        for (int i = 0; i < _b.Length; i++)
        {
            int a = Digit(from, i), z = Digit(to, i);
            if (a != z) Turn(i, a, z);
        }
        Idle = false;
    }

    /// <summary>The hand's actions for one knob going from one setting to another, as a cook does it: push in and
    /// turn to the full mark, hold it in until the flame catches, let go a moment later (or, with flame
    /// safety, once its thermocouple holds the gas on), then turn to the setting; or turn it back to off.</summary>
    public void Turn(int burner, int from, int to)
    {
        if (burner < 0 || burner >= _b.Length || from == to) return;
        Enqueue(new HandAction { Act = Act.Move, Burner = burner, Seconds = 0.45f });
        if (to == 0)
        {
            Enqueue(new HandAction { Act = Act.Turn, Burner = burner, Degrees = 0f, Seconds = 0.35f });
            return;
        }
        if (from == 0)
        {
            Enqueue(new HandAction { Act = Act.Push, Burner = burner, Seconds = 0.12f });
            Enqueue(new HandAction { Act = Act.Turn, Burner = burner, Degrees = GasHobSpec.FullDegrees, Seconds = 0.35f });
            Enqueue(new HandAction { Act = Act.WaitLit, Burner = burner, Hold = HoldAfterCatching(), Seconds = 15f });
            Enqueue(new HandAction { Act = Act.Release, Burner = burner, Seconds = 0.15f });
            if (to != 3) Enqueue(new HandAction { Act = Act.Wait, Seconds = 0.4f });
        }
        if (to != 3 || from != 0)
            Enqueue(new HandAction { Act = Act.Turn, Burner = burner, Degrees = GasHobSpec.AngleFor(to), Seconds = 0.5f });
    }

    /// <summary>
    /// How long the cook keeps the knob in once the flame has caught, s. With flame safety, counted from when
    /// the thermocouple can hold the gas on (the hand waits for that first, in <see cref="Act.WaitLit"/>): the
    /// hold margin. Without, the time to see the flame and let go, a little different each light.
    /// </summary>
    public float HoldAfterCatching()
        => Spec.FlameSafety ? Spec.HoldMarginSeconds : Spec.ReleaseAfterLightSeconds * (0.6f + 0.8f * Uniform());

    /// <summary>Adds an action to the hand's queue (the lab's scripts: a slow light, a half-turned knob).</summary>
    public void Enqueue(HandAction action)
    {
        if (_qCount >= _queue.Length) return;
        _queue[(_qHead + _qCount) % _queue.Length] = action;
        _qCount++;
        Idle = false;
    }

    private static int Digit(string s, int i) => s != null && i < s.Length && s[i] >= '0' && s[i] <= '3' ? s[i] - '0' : 0;

    /// <summary>A burner as it stands after a long time at a setting.</summary>
    private void SetSteady(Burner b, int setting)
    {
        b.Degrees = GasHobSpec.AngleFor(setting);
        b.Pushed = false;
        b.Share = setting > 0 ? GasHobSpec.FlowShare(b.Degrees, b.Spec.ReducedShare) : 0f;
        b.MagnetHeld = setting > 0 && Spec.FlameSafety;
        b.FlameSeen = setting > 0 ? 10f : 0f;
        b.Thermocouple = setting > 0 ? 1f : 0f;
        b.HeadFraction = setting > 0 ? Spec.PortFraction : 0f;
        b.PortFlow = b.Share * b.FullFlow / Spec.PortFraction;
        b.Lit = setting > 0;
        b.Envelope = b.Lit ? 1f : 0f;
        b.HeatTarget = b.Lit ? b.Share * b.FullHeat : 0f;
        b.Heat1 = b.Heat2 = b.HeatTarget;
        b.Cloud = 0f;
        b.Quenching = false;
        b.FastEnergy = b.SlowEnergy = 0f;
    }

    /// <summary>Runs the physics silently: nothing sounds, but the hand, the gas and the flames go on.</summary>
    public void Advance(float seconds)
    {
        _silent = true;
        int steps = (int)(seconds / _stepDt);
        for (int i = 0; i < steps; i++) Physics(_stepDt);
        _silent = false;
        Settle();
        _crackCount = 0;
        _heatPrev = TotalHeatNow();
    }

    /// <summary>What the audio loop would have moved on, moved on at once while nothing is heard: a ring
    /// lighting is lit, a flame going out is out, a light-up is over.</summary>
    private void Settle()
    {
        foreach (var b in _b)
        {
            if (b.Quenching) { b.Quenching = false; b.Lit = false; b.Envelope = 0f; }
            if (b.Lit) { b.Envelope = 1f; b.EnvelopeStep = 0f; }
            b.FastEnergy = b.SlowEnergy = 0f;
            b.Heat1 = b.Heat2 = b.HeatTarget;
        }
    }

    private float TotalHeatNow()
    {
        float q = 0f;
        foreach (var b in _b) q += b.Heat2 * b.Envelope;
        return q;
    }

    // ── Physics, a step a millisecond ────────────────────────────────────────────────────────────

    private void Physics(float dt)
    {
        if (_silent) Settle();
        Hand(dt);
        Module(dt);
        bool active = _acting || _qCount > 0 || _moduleOn;
        float pc = Spec.PortFraction;
        foreach (var b in _b)
        {
            // Without flame safety the plug alone decides; with it, the valve behind it is held open by the
            // pushed knob or by the thermocouple's magnet.
            bool gasOn = !Spec.FlameSafety || b.Pushed || b.MagnetHeld;
            b.Share = gasOn ? GasHobSpec.FlowShare(b.Degrees, b.Spec.ReducedShare) : 0f;
            float gasIn = b.Share * b.FullFlow;
            float mixIn = gasIn / pc;
            // The mixture leaving the ports follows the jet with the tube's column of mixture to move [estimate 30 ms].
            b.PortFlow += (mixIn - b.PortFlow) * MathF.Min(1f, dt / 0.03f);
            // The first gas sweeps the air out of the head.
            if (mixIn > 0f) b.HeadFraction += (pc - b.HeadFraction) * MathF.Min(1f, mixIn * dt / b.HeadVolume);
            float gasOut = b.PortFlow * b.HeadFraction;
            float uPort = b.PortFlow / b.PortArea;
            if (gasOut > 1e-9f) b.GasSince += dt; else b.GasSince = 0f;

            if (!b.Lit)
            {
                b.Cloud += (gasOut - b.Cloud / Spec.CloudSeconds) * dt;
                b.HeatTarget = 0f;
            }
            else
            {
                b.Cloud = MathF.Max(0f, b.Cloud - b.Cloud * dt / 0.05f);
                b.HeatTarget = gasOut * _gas.NetHeatJm3;
                // A flame stays on its ports while the mixture comes faster than about a third of its burning
                // velocity; slower than that it sinks into them and is quenched [estimate].
                if (!b.Quenching && b.Envelope >= 0.999f && uPort < 0.3f * _gas.MaxBurningVelocity)
                {
                    b.Quenching = true;
                    b.QuenchPhase = 0f;
                    b.QuenchFrom = b.Envelope;
                    b.QuenchStep = 1f / (MathF.Max(0.001f, b.Spec.PortMm * 1e-3f / _gas.MaxBurningVelocity) * _fs);
                }
            }

            // The thermocouple, and the safety magnet it holds.
            float target = b.Lit && b.Envelope > 0.5f ? 1f : 0f;
            b.FlameSeen = target > 0f ? b.FlameSeen + dt : 0f;
            float tau = target > b.Thermocouple ? Spec.ThermocoupleHeatSeconds : Spec.ThermocoupleCoolSeconds;
            b.Thermocouple += (target - b.Thermocouple) * MathF.Min(1f, dt / tau);
            if (!Spec.FlameSafety) b.MagnetHeld = false;
            else if (b.Pushed) b.MagnetHeld = true;
            else if (b.MagnetHeld && b.Thermocouple < Spec.DropShare)
            {
                b.MagnetHeld = false;
                Strike(ref _magnet, Spec.MagnetClickDb, 1f);
            }

            // The jets' hiss: the injector's, and the valve's where it throttles (inside the hob, through its case [estimate -10 dB]).
            float u = Spec.InjectorVelocity() * b.Share;
            b.InjAmp = u > 1f ? JetNoise.LighthillPressure(b.InjectorD, u, 293f) : 0f;
            b.InjAlpha = OnePole.AlphaFor(MathF.Min(0.2f * u / b.InjectorD, 0.45f * _fs), _fs);
            if (b.Share > 0f && b.Share < 0.995f)
            {
                float dp = Spec.SupplyKPa * 1000f * (1f - b.Share * b.Share);
                float uv = Spec.DischargeCoefficient * MathF.Sqrt(2f * dp / _gas.DensityKgM3);
                float dv = MathF.Sqrt(4f * gasIn / MathF.Max(1f, uv) / MathF.PI);
                b.ValveAmp = JetNoise.LighthillPressure(dv, uv, 293f) * 0.316f;
                b.ValveAlpha = OnePole.AlphaFor(MathF.Min(0.2f * uv / MathF.Max(1e-5f, dv), 0.45f * _fs), _fs);
            }
            else b.ValveAmp = 0f;

            if (b.Lit || b.Cloud > 1e-9f || gasOut > 1e-9f || b.MagnetHeld) active = true;
        }
        if (!active && !_magnet.Quiet) active = true;
        Idle = !active && _crackCount == 0;
    }

    private void Hand(float dt)
    {
        if (!_acting)
        {
            if (_qCount == 0) return;
            _now = _queue[_qHead];
            _qHead = (_qHead + 1) % _queue.Length;
            _qCount--;
            _acting = true;
            _actElapsed = 0f;
            _litFor = 0f;
            if (_now.Act == Act.Turn && (uint)_now.Burner < (uint)_b.Length) _turnFrom = _b[_now.Burner].Degrees;
            if (_now.Act == Act.Move && _now.Burner == _handAt) { _acting = false; return; }
            if (_now.Act == Act.Push && (uint)_now.Burner < (uint)_b.Length)
            {
                // The knob goes in against the safety valve's spring; at the bottom of its travel the
                // ignition switch snaps over.
                _b[_now.Burner].Pushed = true;
                Strike(ref _switch, Spec.SwitchClickDb, 1f);
            }
            if (_now.Act == Act.Release && (uint)_now.Burner < (uint)_b.Length)
            {
                var b = _b[_now.Burner];
                b.Pushed = false;
                Strike(ref _switch, Spec.SwitchClickDb - 3f, 1.06f);
                Strike(ref _stop, Spec.StopClickDb - 8f, 1.1f);
                // Let go too soon, the thermocouple cannot hold the magnet and the spring shuts the gas.
                if (Spec.FlameSafety && b.Thermocouple < Spec.HoldShare && b.MagnetHeld)
                {
                    b.MagnetHeld = false;
                    Strike(ref _magnet, Spec.MagnetClickDb, 1f);
                }
            }
        }
        _actElapsed += dt;
        var act = _now;
        switch (act.Act)
        {
            case Act.Move:
                if (_actElapsed >= act.Seconds) { _handAt = act.Burner; _acting = false; }
                break;
            case Act.Push:
            case Act.Release:
            case Act.Wait:
                if (_actElapsed >= act.Seconds) _acting = false;
                break;
            case Act.Turn:
            {
                if ((uint)act.Burner >= (uint)_b.Length) { _acting = false; break; }
                var b = _b[act.Burner];
                float x = MathF.Min(1f, _actElapsed / MathF.Max(0.01f, act.Seconds));
                float before = b.Degrees;
                b.Degrees = _turnFrom + (act.Degrees - _turnFrom) * x * x * (3f - 2f * x);
                // The detent at the full mark, passed or reached; the stops at off and at the small flame.
                if ((before - GasHobSpec.FullDegrees) * (b.Degrees - GasHobSpec.FullDegrees) <= 0f && before != b.Degrees)
                    Strike(ref _detent, Spec.DetentClickDb, 1f);
                if (x >= 1f)
                {
                    if (act.Degrees <= 0.5f || act.Degrees >= GasHobSpec.LowDegrees - 0.5f) Strike(ref _stop, Spec.StopClickDb, 1f);
                    _acting = false;
                }
                break;
            }
            case Act.WaitLit:
            case Act.WaitAngleLit:
            {
                if ((uint)act.Burner >= (uint)_b.Length) { _acting = false; break; }
                var b = _b[act.Burner];
                // The flame has caught; with flame safety the hand also waits until the thermocouple can hold it.
                if (b.Lit && b.Envelope > 0.5f && (!Spec.FlameSafety || b.Thermocouple >= Spec.HoldShare)) _litFor += dt;
                if (_litFor >= act.Hold || _actElapsed >= act.Seconds) _acting = false;
                break;
            }
        }
    }

    private void Module(float dt)
    {
        bool on = false;
        if (Spec.Module == HobSparkModule.Reignition)
        {
            // Its switches close at any on position of a knob; it sparks until each burner that is on has had
            // a flame on its electrode for the sense time.
            foreach (var b in _b) on |= b.Degrees > 15f && b.FlameSeen < Spec.FlameSenseSeconds;
        }
        else foreach (var b in _b) on |= b.Pushed;
        if (on && !_moduleOn) { _charge = 0f; _mainsClock = _mainsPhase; }
        _moduleOn = on;
        if (!on) return;
        // A cycle's charge goes in on its rising half; the device breaks over near the crest.
        float period = 1f / Spec.MainsHz;
        _mainsClock += dt;
        while (_mainsClock >= period)
        {
            _mainsClock -= period;
            _charge += (1f - _charge) * _chargeStep;
            float threshold = _breakover * (1f + 0.01f * Gaussian());
            if (_charge >= threshold)
            {
                float v = _charge;
                _charge = 0.02f;
                // The crest is a quarter cycle in; the firing a little before or after it.
                float at = 0.25f * period + 0.0006f * (Uniform() - 0.5f) - _mainsClock;
                Spark(MathF.Max(0f, at), v / _breakover);
            }
        }
    }

    /// <summary>One firing: every electrode sparks at once, each to its own cap.</summary>
    private void Spark(float inSeconds, float voltage)
    {
        Sparks++;
        float energy = Spec.SparkEnergyMj * voltage * voltage;
        float heat = Spec.SparkHeatMj * 1e-3f * voltage * voltage;
        if (_listenerChanged) Delays();
        int at = (int)(inSeconds * _fs);
        if (!_silent)
        {
            // The module's own tick, under the hob behind its tray.
            Strike(ref _module, Spec.ModuleTickDb - Spec.ModuleCaseLossDb + 20f * MathF.Log10(voltage), 1f);
        }
        int duration = Math.Clamp((int)(Uniform() * KernelDurations), 0, KernelDurations - 1);
        foreach (var b in _b)
        {
            if (!_silent)
            {
                // The crack, and its image in the hob's steel a little later (a steel plate gives back
                // nearly all of it).
                AddCrack(duration, at + KernelLead, b.DirectDelay, heat);
                AddCrack(duration, at + KernelLead, b.ImageDelay, heat * 0.95f);
                float peak = heat * PeakPerJoule;
                b.Cap.Strike(peak * MathF.Pow(10f, Spec.CapRingDb / 20f) * (0.8f + 0.4f * Uniform()));
            }
            // Does it light? The richest mixture the spark's channel crosses: where the ports' jets reach
            // the electrode, or the gas gathered round the burner, whichever is richer.
            if (b.Lit) continue;
            float u = b.PortFlow / b.PortArea;
            float reach = u * u / (u * u + Spec.ReachVelocity * Spec.ReachVelocity);
            float jet = b.HeadFraction * reach;
            float cloud = b.Cloud / b.CloudVolume;
            float richest = MathF.Max(jet, cloud);
            if (energy >= _gas.IgnitionEnergyMj(richest)) Light(b, cloud);
            else if (b.GasSince > 0f) b.SparksFailed++;
        }
    }

    /// <summary>
    /// A burner catches: the flame runs round its ring of ports both ways from the electrode at its
    /// burning velocity times the burnt gas's expansion, burning the gas in the ports' mixing layers as it
    /// goes (the last fifty milliseconds of flow, near stoichiometric); whatever leaner gas had gathered
    /// round the burner burns after it at the lean mixture's own pace, at about the lean limit where it is
    /// leaner than that on average [estimate: it is stratified, richest by the ports].
    /// </summary>
    private void Light(Burner b, float cloudFraction)
    {
        b.Lit = true;
        b.Quenching = false;
        b.LastSparksFailed = b.SparksFailed;
        b.SparksFailed = 0;
        b.LastLightDelay = b.GasSince;
        float stoich = _gas.FractionAt(1f);
        float ringSpeed = _gas.ExpansionRatio(stoich) * _gas.MaxBurningVelocity;
        float ringSeconds = MathF.PI * b.Radius / ringSpeed;
        b.EnvelopeStep = 1f / (ringSeconds * _fs);
        b.Envelope = 0f;
        b.LastFlashJoules = b.Cloud * _gas.NetHeatJm3;

        float near = MathF.Min(b.Cloud, b.PortFlow * b.HeadFraction * 0.05f);
        float far = b.Cloud - near;
        b.FastEnergy = near * _gas.NetHeatJm3;
        b.FastPhase = 0f;
        b.FastStep = 1f / (ringSeconds * _fs);

        float c = Math.Clamp(cloudFraction, _gas.LowerLimit * 1.05f, stoich);
        float volume = far / c;
        float radius = MathF.Max(0.01f, MathF.Cbrt(3f * volume / MathF.Tau));
        float front = _gas.ExpansionRatio(c) * MathF.Max(0.03f, _gas.BurningVelocity(c));
        b.SlowEnergy = far * _gas.NetHeatJm3;
        b.SlowPhase = 0f;
        b.SlowStep = 1f / (MathF.Max(ringSeconds, radius / front) * _fs);
        b.Cloud = 0f;
    }

    // ── The audio, a sample at a time ────────────────────────────────────────────────────────────

    public float Next()
    {
        if (--_untilStep <= 0)
        {
            _untilStep = _stepSamples;
            Physics(_stepDt);
        }
        float y = 0f;

        // The cracks.
        for (int i = 0; i < _crackCount; i++)
        {
            ref var c = ref _cracks[i];
            if (c.Delay > 0) { c.Delay--; continue; }
            y += _kernels[c.Kernel][c.Pos] * c.Gain * SparkPart;
            if (++c.Pos >= KernelLength) { _cracks[i] = _cracks[--_crackCount]; i--; }
        }

        // The struck parts.
        y += (_switch.Step() + _detent.Step() + _stop.Step() + _magnet.Step()) * ClickPart;
        _moduleLp += _moduleAlpha * (_module.Step() - _moduleLp);
        y += _moduleLp * SparkPart;

        float heat = 0f;
        foreach (var b in _b)
        {
            y += b.Cap.Step() * SparkPart;

            // The hiss.
            if (b.InjAmp > 0f)
            {
                float n = Noise();
                b.InjLp1 += b.InjAlpha * (n - b.InjLp1); b.InjLp2 += b.InjAlpha * (b.InjLp1 - b.InjLp2);
                y += (b.InjLp1 - b.InjLp2) / JetNoise.BandNormaliser(b.InjAlpha) * b.InjAmp * HissPart * _jetTrim;
            }
            if (b.ValveAmp > 0f)
            {
                float n = Noise();
                b.ValveLp1 += b.ValveAlpha * (n - b.ValveLp1); b.ValveLp2 += b.ValveAlpha * (b.ValveLp1 - b.ValveLp2);
                y += (b.ValveLp1 - b.ValveLp2) / JetNoise.BandNormaliser(b.ValveAlpha) * b.ValveAmp * HissPart * _jetTrim;
            }

            // The flame's heat release, following the gas with the flame's own lag, times how much of the ring burns.
            b.Heat1 += _heatAlpha * (b.HeatTarget - b.Heat1);
            b.Heat2 += _heatAlpha * (b.Heat1 - b.Heat2);
            if (b.Lit && b.EnvelopeStep > 0f && !b.Quenching)
            {
                b.Envelope = MathF.Min(1f, b.Envelope + b.EnvelopeStep);
                if (b.Envelope >= 1f) b.EnvelopeStep = 0f;
            }
            if (b.Quenching)
            {
                b.QuenchPhase += b.QuenchStep;
                if (b.QuenchPhase >= 1f)
                {
                    b.Quenching = false; b.Lit = false; b.Envelope = 0f; b.EnvelopeStep = 0f;
                }
                else b.Envelope = b.QuenchFrom * 0.5f * (1f + MathF.Cos(MathF.PI * b.QuenchPhase));
            }
            float q = b.Heat2 * b.Envelope;

            // The roar: acoustic power η (u/1.5)² Q, wavering with the room's air.
            if (q > 0.5f)
            {
                float u = b.PortFlow / b.PortArea / 1.5f;
                float w = Spec.FlameEfficiency * u * u * q;
                b.FlickerLp += _flickerAlpha * (Noise() - b.FlickerLp);
                float flick = MathF.Max(0f, 1f + Spec.Flicker * b.FlickerLp * _flickerScale);
                y += b.Roar.Process(Noise()) * b.RoarUnit * MathF.Sqrt(w) * _roarScale * flick * FlamePart;
            }

            // The light-up: each part's heat as one smooth pulse, and the turbulence of its front.
            if (b.FastEnergy > 0f) q += Pulse(b, ref b.FastEnergy, ref b.FastPhase, b.FastStep, ref y);
            if (b.SlowEnergy > 0f) q += Pulse(b, ref b.SlowEnergy, ref b.SlowPhase, b.SlowStep, ref y);
            heat += q;
        }

        // Heat released unsteadily: p = (γ-1)/(4π r c²) dQ/dt. The thump of a light-up, the pop of a flame going out.
        float dq = (heat - _heatPrev) * _fs;
        _heatPrev = heat;
        _thumpLp += _thumpAlpha * (dq * GasHobSpec.MonopolePerWattPerSecond - _thumpLp);
        y += _thumpLp * FlamePart;
        return y;
    }

    /// <summary>One part of a light-up: its heat release, E (2/T) sin²(πt/T), and its turbulent front's roar.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Pulse(Burner b, ref float energy, ref float phase, float step, ref float y)
    {
        phase += step;
        if (phase >= 1f) { energy = 0f; return 0f; }
        float s = MathF.Sin(MathF.PI * phase);
        float q = energy * 2f * step * _fs * s * s;
        float w = Spec.LightUpEfficiency * q;
        y += b.Whoosh.Process(Noise()) * b.WhooshUnit * MathF.Sqrt(w) * _roarScale * LightUpPart;
        return q * LightUpPart;
    }

    // ── Pieces ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Strikes a bank of the knob's parts at its declared peak, dB at a metre, a little different each time.</summary>
    private void Strike(ref Modes m, float peakDb, float scale)
    {
        if (_silent) return;
        // Struck at the step: a millisecond early or late is below what the ear can order.
        float pa = 20e-6f * MathF.Pow(10f, (peakDb + 1.5f * (Uniform() - 0.5f)) / 20f);
        m.Strike(pa * scale);
    }

    private void AddCrack(int duration, int baseSamples, float delaySeconds, float heatJoules)
    {
        if (_crackCount >= _cracks.Length) return;
        float d = baseSamples + delaySeconds * _fs;
        int whole = (int)MathF.Floor(d);
        int phase = Math.Clamp((int)((d - whole) * KernelPhases), 0, KernelPhases - 1);
        _cracks[_crackCount++] = new Crack { Kernel = duration * KernelPhases + phase, Pos = 0, Delay = Math.Max(0, whole - KernelLead), Gain = heatJoules };
    }

    /// <summary>How each burner's crack and its image reach the listener, later than the hob's middle.</summary>
    private void Delays()
    {
        _listenerChanged = false;
        float h = Spec.SparkHeightMm * 1e-3f;
        float centre = _listener.Length();
        foreach (var b in _b)
        {
            var p = new Vector3(b.Spec.RightMetres, h, b.Spec.BackMetres);
            var image = new Vector3(b.Spec.RightMetres, -h, b.Spec.BackMetres);
            b.DirectDelay = Math.Clamp((Vector3.Distance(_listener, p) - centre) / C0 + 0.002f, 0f, 0.004f);
            // Below the hob's edge the steel hides the image [estimate: the listener under the worktop].
            b.ImageDelay = Math.Clamp((Vector3.Distance(_listener, image) - centre) / C0 + 0.002f, 0f, 0.006f);
        }
    }

    /// <summary>The peak of a crack's band-limited pressure, Pa per joule of heat, for the cap's share.</summary>
    private float PeakPerJoule
    {
        get
        {
            if (_peakPerJoule > 0f) return _peakPerJoule;
            float m = 0f;
            foreach (float v in _kernels[0]) m = MathF.Max(m, MathF.Abs(v));
            return _peakPerJoule = m;
        }
    }
    private float _peakPerJoule;

    private static readonly ConcurrentDictionary<(float, float), float[][]> KernelCache = new();

    /// <summary>
    /// The crack of one joule of heat put into the gap, Pa at a metre, as the mixer's rate can carry it:
    /// p = (γ-1)/(4π c²) dQ/dt with Q(t) = E t/τ² e^(-t/τ), integrated against a windowed sinc cut at 0.45 of
    /// the rate. Three durations (τ and 15 % either side, as each spark's path along the cap's edge differs)
    /// and sixteen sub-sample phases; the kernel starts <see cref="KernelLead"/> samples early for the sinc's
    /// leading half.
    /// </summary>
    private static float[][] KernelsFor(float fs, float microseconds) => KernelCache.GetOrAdd((fs, microseconds), static key =>
    {
        var (rate, us) = key;
        var k = new float[KernelDurations * KernelPhases][];
        double T = 1.0 / rate, fc = 0.45 * rate;
        for (int d = 0; d < KernelDurations; d++)
        {
            double tau = us * 1e-6 * (d == 0 ? 1.0 : d == 1 ? 0.85 : 1.15);
            double span = 14 * tau;
            for (int ph = 0; ph < KernelPhases; ph++)
            {
                var kernel = new float[KernelLength];
                double offset = ph / (double)KernelPhases * T;
                double h = Math.Min(T / 32, tau / 16);
                for (int n = 0; n < KernelLength; n++)
                {
                    double tn = (n - KernelLead) * T;
                    double sum = 0;
                    for (double s = 0; s < span; s += h)
                    {
                        double dq = (1.0 - s / tau) * Math.Exp(-s / tau) / (tau * tau);
                        double x = tn - (s + offset);
                        double sinc = Math.Abs(x) < 1e-12 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
                        double win = Math.Abs(x) >= KernelLead * T ? 0 : 0.42 + 0.5 * Math.Cos(Math.PI * x / (KernelLead * T)) + 0.08 * Math.Cos(2 * Math.PI * x / (KernelLead * T));
                        sum += dq * sinc * win * h;
                    }
                    kernel[n] = (float)(sum * GasHobSpec.MonopolePerWattPerSecond);
                }
                k[d * KernelPhases + ph] = kernel;
            }
        }
        return k;
    });

    /// <summary>The output RMS of a power-law noise per unit white input, from its response.</summary>
    private float UnitRms(ref PowerLawNoise n)
    {
        double sum = 0;
        const int N = 600;
        double lo = Math.Log(10), hi = Math.Log(0.49 * _fs);
        for (int i = 0; i < N; i++)
        {
            double f0 = Math.Exp(lo + (hi - lo) * i / N), f1 = Math.Exp(lo + (hi - lo) * (i + 1) / N);
            double m = n.Magnitude((float)Math.Sqrt(f0 * f1), _fs);
            sum += m * m * (f1 - f0) * 2.0 / _fs;
        }
        return (float)(1.0 / Math.Sqrt(Math.Max(1e-30, sum)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Noise()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng * (1f / 4294967296f)) * 3.4641f - 1.7320f;   // unit variance
    }

    private float Uniform()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return _rng * (1f / 4294967296f);
    }

    private float Gaussian() => (Uniform() + Uniform() + Uniform() + Uniform() - 2f) * 1.732f;
}
