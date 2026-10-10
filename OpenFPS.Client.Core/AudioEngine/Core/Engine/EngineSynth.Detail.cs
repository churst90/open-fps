using System.Numerics;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

/// <summary>
/// How much of an engine is integrated sample by sample. The voice's owner chooses it from how loud the
/// voice is against the loudest engine heard (FmodAudioProvider.ChooseEngineDetail); a quiet one under a
/// loud one is masked by it, and the engine render pool spends most of its time on such voices.
/// </summary>
public enum EngineDetail : byte
{
    /// <summary>Every sample, at the voice's rate.</summary>
    Full,
    /// <summary>
    /// At half the voice's rate, interpolated back up: half the cost. Measured on the street's sixteen
    /// machines at a steady speed (AudioLab --pool-cost render steady): level within 0.9 dB, the bands up
    /// to 8 kHz 0.9 dB apart on average, and nothing above about 10 kHz.
    /// </summary>
    Reduced,
}

public sealed partial class EngineSynth
{
    /// <summary>How this engine is integrated from now on; its owner sets it (see <see cref="EngineDetail"/>).
    /// A change is handed over without a step, over about a quarter of a second.</summary>
    public EngineDetail Detail { get; set; }

    private readonly int _seed;
    private DetailRunner? _runner;

    /// <summary>What the hand-over is doing, for instruments and tests: Outer, ToTwin, Twin or ToOuter.</summary>
    internal string DetailState => _runner?.State ?? "Outer";

    /// <summary>
    /// Takes another engine's state, the same profile at another rate: everything that is a property of
    /// the gas and the crank, not of the sample grid. The pipes start empty and the filters' memories
    /// stay where they were; both settle well inside the hand-over's warm-up, whose output is not heard.
    /// </summary>
    private void FollowFrom(EngineSynth o)
    {
        CopyInputsFrom(o);
        _theta = o._theta; _omega = o._omega; _syncDegrees = o._syncDegrees;
        _idleAir = o._idleAir; _idleIntegral = o._idleIntegral; _pedal = o._pedal;
        _rpmFast = o._rpmFast; _idleSparkTrim = o._idleSparkTrim; _wasRunning = o._wasRunning;
        _rpmSlow = o._rpmSlow; _map = o._map; _spool = o._spool; _portK = o._portK; _intakeK = o._intakeK;
        // The slow tick's accumulator is in this engine's samples: start a tick afresh.
        _massFlowLp = o._massFlowLp; _massFlowAcc = 0f; _gasTick = 1;
        _mixtureWalk = o._mixtureWalk; _knockTemp = o._knockTemp;
        // The tones' phases, so a whine carries on through the hand-over instead of restarting.
        _whinePhase = o._whinePhase; _blowerPhase = o._blowerPhase; _turboPhase = o._turboPhase; _turbinePhase = o._turbinePhase;
        _tcnDrift = o._tcnDrift; _humpPower = o._humpPower;
        _armOmega = o._armOmega; _clutchLocked = o._clutchLocked; _starterWas = o._starterWas;
        _starterLoad = o._starterLoad; _starterLoadLp = o._starterLoadLp;
        _meshPhase = o._meshPhase; _armPhase = o._armPhase; _slotPhase = o._slotPhase; _meshDrift = o._meshDrift;
        Torque = o.Torque; PortPeak = o.PortPeak; LastBurnQuality = o.LastBurnQuality; LastDilution = o.LastDilution;
        Array.Copy(o._cyl, _cyl, _n);
        _intake.CopyLumpedFrom(o._intake);
        _exhaust.UpdateGas(_portK, _massFlowLp);
        if (o._listenerSet)
        {
            _exhaust.SetListener(o._listener);
            _listener = o._listener;
            _listenerSet = true;
        }
        _exhaust.Clear();
        _intake.Clear();
    }

    /// <summary>What the driver, the driveline or the governor set, from the engine they set it on.</summary>
    private void CopyInputsFrom(EngineSynth o)
    {
        Throttle = o.Throttle; Ignition = o.Ignition; Starter = o.Starter;
        LoadTorque = o.LoadTorque; ExternalInertia = o.ExternalInertia; GovernedRpm = o.GovernedRpm;
        StarterMix = o.StarterMix; StarterToneMix = o.StarterToneMix;
    }

    /// <summary>What anyone reads off a running engine, from the engine actually running it.</summary>
    private void TakeRunningFrom(EngineSynth o, double crankLead)
    {
        _theta = Wrap(o._theta - crankLead);
        _omega = o._omega; _syncDegrees = o._syncDegrees;
        _rpmSlow = o._rpmSlow; _rpmFast = o._rpmFast; _map = o._map; _spool = o._spool;
        _idleAir = o._idleAir; _pedal = o._pedal;
        Torque = o.Torque; PortPeak = o.PortPeak; LastBurnQuality = o.LastBurnQuality; LastDilution = o.LastDilution;
    }

    private double Wrap(double deg)
    {
        if (deg >= _cycleDeg) deg -= _cycleDeg;
        else if (deg < 0.0) deg += _cycleDeg;
        return deg;
    }

    /// <summary>
    /// Runs an engine as its <see cref="Detail"/> asks: itself (the outer engine, the one its owner
    /// holds), or a twin of it at half the rate whose outputs are interpolated back up and whose state the
    /// outer reports. A change of detail is a hand-over, never a cut: the new engine takes the old one's
    /// state, runs alongside it with its crank held to the old one's for a warm-up while its pipes fill,
    /// then the two are crossfaded at a level that keeps their measured power. The crank is shared, so the
    /// firing does not move and the pitch cannot step.
    /// </summary>
    private sealed class DetailRunner
    {
        /// <summary>The new engine running alongside, unheard, while its pipes fill: several round trips of
        /// the longest exhaust and a cycle at a fast idle.</summary>
        private const float WarmSeconds = 0.15f;
        /// <summary>The crossfade, long enough that two engines a decibel apart do not step.</summary>
        private const float FadeSeconds = 0.1f;

        private enum Phase : byte { Outer, ToTwin, Twin, ToOuter }

        private readonly EngineSynth _o;
        private EngineSynth? _twin;
        private Phase _phase = Phase.Outer;
        private int _n;
        private readonly int _warm, _fade;
        /// <summary>The outer's samples alternate: the twin steps on the even ones.</summary>
        private bool _odd;
        private readonly HalfbandUp _ex = new(), _in = new(), _bl = new(), _st = new();
        private float _shell, _pipe;
        private double _sxy, _sxx, _syy;
        private float _rho;

        public DetailRunner(EngineSynth o)
        {
            _o = o;
            _warm = (int)(WarmSeconds * o._rate);
            _fade = Math.Max(1, (int)(FadeSeconds * o._rate));
        }

        public string State => _phase.ToString();

        public void SetListener(Vector3 v) => _twin?.SetListener(v);

        /// <summary>The interpolator's delay, in the outer's degrees of crank at its speed now: the twin's
        /// crank leads the outer's by this, so the two are heard at the same angle.</summary>
        private double CrankLead => _o._omega * (180.0 / Math.PI) * HalfbandUp.Latency * _o._dt;

        /// <summary>Half-rate twins only where the half is still well above anything an engine radiates.</summary>
        private bool CanHalve => _o._rate >= 32000f;

        public void Step()
        {
            switch (_phase)
            {
                case Phase.Outer:
                    if (_o.Detail == EngineDetail.Reduced && CanHalve) { BeginToTwin(); StepToTwin(); return; }
                    _o.StepLive();
                    return;
                case Phase.ToTwin:
                    StepToTwin();
                    return;
                case Phase.Twin:
                    if (_o.Detail == EngineDetail.Full) { BeginToOuter(); StepToOuter(); return; }
                    StepTwin(Crank.Twin);
                    Emit(1f);
                    return;
                case Phase.ToOuter:
                    StepToOuter();
                    return;
            }
        }

        private void BeginToTwin()
        {
            _twin ??= new EngineSynth(_o.Profile, _o._rate * 0.5f, _o._seed);
            _twin.FollowFrom(_o);
            _twin._theta = _twin.Wrap(_o._theta + CrankLead);
            Restart(Phase.ToTwin);
        }

        private void BeginToOuter()
        {
            _o.FollowFrom(_twin!);
            _o._theta = _o.Wrap(_twin!._theta - CrankLead);
            Restart(Phase.ToOuter);
        }

        private void Restart(Phase phase)
        {
            _phase = phase;
            _n = 0;
            _sxy = _sxx = _syy = 0.0;
            if (phase == Phase.ToTwin)
            {
                _ex.Clear(); _in.Clear(); _bl.Clear(); _st.Clear();
                _odd = false;
            }
        }

        /// <summary>Whose crank turns while the twin steps: the outer's (the twin is held to it), the
        /// twin's (the outer reports it), or the twin's with the outer integrating beside it.</summary>
        private enum Crank : byte { Outer, Twin, Both }

        /// <summary>
        /// One of the outer's samples of the twin: on an even sample it steps, with the outer's inputs,
        /// and the interpolators take its outputs.
        /// </summary>
        private void StepTwin(Crank crank)
        {
            var t = _twin!;
            if (!_odd)
            {
                t.CopyInputsFrom(_o);
                if (crank == Crank.Outer)
                {
                    t._theta = t.Wrap(_o._theta + CrankLead);
                    t._omega = _o._omega;
                    t._syncDegrees = _o._syncDegrees;
                }
                t.StepLive();
                if (crank == Crank.Twin) _o.TakeRunningFrom(t, CrankLead);
                _ex.Push(t.Exhaust); _in.Push(t.Intake); _bl.Push(t.Block); _st.Push(t.StarterOut);
                _shell = t.ExhaustShell; _pipe = t.ExhaustPipe;
            }
            _odd = !_odd;
        }

        /// <summary>The twin's interpolated sample, mixed with the outer's own at the outer's weight
        /// <paramref name="own"/>, onto the outer's outputs.</summary>
        private void Emit(float twinGain, float ownGain = 0f)
        {
            bool even = _odd;   // StepTwin has already flipped the parity for this sample
            float ex = even ? _ex.Even : _ex.Odd, inn = even ? _in.Even : _in.Odd;
            float bl = even ? _bl.Even : _bl.Odd, st = even ? _st.Even : _st.Odd;
            var o = _o;
            o.Exhaust = twinGain * ex + ownGain * o.Exhaust;
            o.Intake = twinGain * inn + ownGain * o.Intake;
            o.Block = twinGain * bl + ownGain * o.Block;
            o.StarterOut = twinGain * st + ownGain * o.StarterOut;
            o.ExhaustShell = twinGain * _shell + ownGain * o.ExhaustShell;
            o.ExhaustPipe = twinGain * _pipe + ownGain * o.ExhaustPipe;
        }

        private float TwinSum()
        {
            bool even = _odd;
            return even ? _ex.Even + _in.Even + _bl.Even : _ex.Odd + _in.Odd + _bl.Odd;
        }

        private void StepToTwin()
        {
            _o.StepLive();
            StepTwin(Crank.Outer);
            Blend(towardTwin: true);
            if (++_n >= _warm + _fade) _phase = Phase.Twin;
        }

        private void StepToOuter()
        {
            if (!_odd)
            {
                // The twin leads: the outer's crank is put where the twin's is before either steps.
                _o._theta = _o.Wrap(_twin!._theta - CrankLead);
                _o._omega = _twin._omega;
            }
            StepTwin(Crank.Both);
            _o.StepLive();
            Blend(towardTwin: false);
            if (++_n >= _warm + _fade) _phase = Phase.Outer;
        }

        /// <summary>
        /// The hand-over's output: the old engine alone through the warm-up, which measures how alike the
        /// two are (their correlation, rho), then a raised-cosine crossfade normalised so that two signals
        /// of that correlation keep their power: two copies of one waveform would cross linearly, two
        /// unrelated ones at equal power. Engines on one crank are alike in the low orders and not in the
        /// noise, so neither law alone would hold the level.
        /// </summary>
        private void Blend(bool towardTwin)
        {
            float own = _o.Exhaust + _o.Intake + _o.Block;
            float twin = TwinSum();
            if (_n < _warm)
            {
                if (_n >= _warm / 2) { _sxy += (double)own * twin; _sxx += (double)own * own; _syy += (double)twin * twin; }
                if (towardTwin) return;          // the outer's own outputs stand
                Emit(1f);                        // the twin alone until the outer has warmed
                return;
            }
            if (_n == _warm)
                _rho = _sxx > 1e-20 && _syy > 1e-20 ? (float)Math.Clamp(_sxy / Math.Sqrt(_sxx * _syy), 0.0, 1.0) : 0.5f;
            float u = (_n - _warm + 1) / (float)_fade;
            float g = 0.5f - 0.5f * MathF.Cos(MathF.PI * Math.Clamp(u, 0f, 1f));   // toward the new engine
            float norm = 1f / MathF.Sqrt((1f - g) * (1f - g) + g * g + 2f * _rho * g * (1f - g));
            float newGain = g * norm, oldGain = (1f - g) * norm;
            if (towardTwin) Emit(newGain, oldGain);
            else Emit(oldGain, newGain);
        }
    }

    /// <summary>
    /// Doubles a signal's rate: the even outputs are the input delayed, the odd ones a 16-tap windowed
    /// sinc half-way between, so what the half-rate engine leaves above its own Nyquist is not folded
    /// back in as images. Push one input, read Even then Odd.
    /// </summary>
    private sealed class HalfbandUp
    {
        private const int Taps = 16;
        /// <summary>How far behind its input the output is, in output samples.</summary>
        public const int Latency = Taps;
        private static readonly float[] H = Design();
        private readonly float[] _x = new float[Taps * 2];
        private int _at;
        public float Even, Odd;

        private static float[] Design()
        {
            var h = new float[Taps];
            double sum = 0;
            for (int k = 0; k < Taps; k++)
            {
                double x = k - (Taps - 1) * 0.5;                    // half-integer offsets from the middle
                double sinc = Math.Sin(Math.PI * x) / (Math.PI * x);
                double w = (k + 0.5) / Taps;                        // Blackman over the taps
                double win = 0.42 - 0.5 * Math.Cos(2 * Math.PI * w) + 0.08 * Math.Cos(4 * Math.PI * w);
                h[k] = (float)(sinc * win);
                sum += h[k];
            }
            for (int k = 0; k < Taps; k++) h[k] = (float)(h[k] / sum);
            return h;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(float v)
        {
            _at = _at == 0 ? Taps - 1 : _at - 1;
            _x[_at] = v;
            _x[_at + Taps] = v;
            float acc = 0f;
            for (int k = 0; k < Taps; k++) acc += H[k] * _x[_at + k];
            // Newest at _at: the even output is the one Taps/2 old, the odd one half a sample newer.
            Even = _x[_at + Taps / 2];
            Odd = acc;
        }

        public void Clear()
        {
            Array.Clear(_x);
            Even = Odd = 0f;
        }
    }
}
