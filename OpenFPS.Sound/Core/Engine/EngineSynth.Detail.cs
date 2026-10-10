using OpenFPS.Common;
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
    /// to 8 kHz 0.9 dB apart on average, and nothing above about 10 kHz. While it runs steadily it replays
    /// its own last cycles instead (CycleCache), within 0.05 dB of itself live.
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

    /// <summary>Whether a reduced engine may replay its own cycles while steady (CycleCache);
    /// OPENFPS_CYCLE_CACHE=0 keeps it running at half rate, for an A/B.</summary>
    internal static volatile bool CycleCacheOn = Environment.GetEnvironmentVariable("OPENFPS_CYCLE_CACHE") != "0";

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

        private enum Phase : byte { Outer, ToTwin, Twin, ToOuter, ToReplay, Replay, FromReplay }

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

        /// <summary>The twin's own last cycles (CycleCache), made the first time the twin runs.</summary>
        private CycleCache? _cycles;
        /// <summary>Samples the twin has run since it last took over; its first cycles are its pipes filling.</summary>
        private int _twinRun;
        private int _settle;
        private double _twinTheta;
        /// <summary>The replay has been asked to give the engine back.</summary>
        private bool _leaving;
        /// <summary>Samples replayed since the set was taken, and the most before a fresh one.</summary>
        private int _replayed;
        private readonly int _refresh;
        private const float RefreshSeconds = 4f;

        public DetailRunner(EngineSynth o)
        {
            _o = o;
            _warm = (int)(WarmSeconds * o._rate);
            _fade = Math.Max(1, (int)(FadeSeconds * o._rate));
            _settle = (int)(SettleSeconds * o._rate);
            _refresh = (int)(RefreshSeconds * o._rate);
        }

        /// <summary>How long the twin runs before its cycles are recorded: the hand-over well behind it.</summary>
        private const float SettleSeconds = 0.5f;

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
                    StepTwinRecording(out bool boundary);
                    if (boundary && CycleCacheOn && _twinRun > _settle && _cycles!.TrySet(_twin!)) { _phase = Phase.ToReplay; _n = 0; }
                    return;
                case Phase.ToOuter:
                    StepToOuter();
                    return;
                case Phase.ToReplay:
                    StepToReplay();
                    return;
                case Phase.Replay:
                    StepReplay();
                    return;
                case Phase.FromReplay:
                    StepFromReplay();
                    return;
            }
        }

        /// <summary>The twin as it runs at reduced detail, its output recorded for the cycle cache.</summary>
        private void StepTwinRecording(out bool boundary)
        {
            var t = _twin!;
            bool stepping = !_odd;
            StepTwin(Crank.Twin);
            Emit(1f);
            boundary = stepping && t._theta < _twinTheta;
            if (stepping) _twinTheta = t._theta;
            _twinRun++;
            if (_twinRun > _settle)
                (_cycles ??= new CycleCache(_o.Profile, _o._rate, _o._seed)).Record(t, _o.Exhaust, _o.Intake, _o.Block, _o.StarterOut, boundary);
        }

        /// <summary>
        /// Into the replay at a cycle boundary: the twin plays on for one join's length, recording what
        /// follows the set's last cycle, while the replay's first cycle fades in from its head; then the
        /// twin stops, at the same distance into its cycle as the replay is into its own.
        /// </summary>
        private void StepToReplay()
        {
            var c = _cycles!;
            if (_o.Detail == EngineDetail.Full || !c.StillSteady(_o))
            {
                // Taken back before it started: the twin never stopped.
                _phase = Phase.Twin;
                c.Reset();
                StepTwinRecording(out _);
                return;
            }
            StepTwinRecording(out _);
            c.Next(out float ex, out float inn, out float bl);
            float g = Gain(++_n / (float)c.Tail, c.Rho);
            float own = Gain(1f - _n / (float)c.Tail, c.Rho);
            _o.Exhaust = own * _o.Exhaust + g * ex;
            _o.Intake = own * _o.Intake + g * inn;
            _o.Block = own * _o.Block + g * bl;
            _o.StarterOut *= own;
            if (_n >= c.Tail)
            {
                _phase = Phase.Replay;
                _leaving = false;
                _replayed = 0;
                _o._omega = c.SetOmega;
                _o.Torque = c.SetTorque;
            }
        }

        /// <summary>
        /// The replay alone: the twin stands still, the outer reports the set's speed (a steady crank, so the
        /// driveline and the governor hold what they were doing) and the replay's angle. When the inputs
        /// leave the set's, or the detail goes back to full, the twin is taken up again where it stopped,
        /// at the next point the replay is the same distance into a cycle.
        /// </summary>
        private void StepReplay()
        {
            var c = _cycles!;
            // Taken up again after a few seconds whatever happens: what the set cannot see (the driver's
            // pedal creeping, the gas still warming) has had time to matter, and a fresh set is recorded.
            if (!_leaving && (_o.Detail == EngineDetail.Full || !c.StillSteady(_o) || ++_replayed > _refresh)) _leaving = true;
            if (_leaving && c.AtResumePoint)
            {
                _phase = Phase.FromReplay;
                _n = 0;
                StepFromReplay();
                return;
            }
            c.Next(out float ex, out float inn, out float bl);
            _o.Exhaust = ex; _o.Intake = inn; _o.Block = bl;
            _o.StarterOut = 0f;
            _o.ExhaustShell = _o.ExhaustPipe = 0f;
            _o._omega = c.SetOmega;
            _o._theta = c.Angle(_o._cycleDeg);
        }

        /// <summary>Out of the replay: the twin runs again from where it stopped, faded in over one join.</summary>
        private void StepFromReplay()
        {
            var c = _cycles!;
            StepTwin(Crank.Twin);
            Emit(1f);
            c.Next(out float ex, out float inn, out float bl);
            float g = Gain(++_n / (float)c.Tail, c.Rho);
            float old = Gain(1f - _n / (float)c.Tail, c.Rho);
            _o.Exhaust = old * ex + g * _o.Exhaust;
            _o.Intake = old * inn + g * _o.Intake;
            _o.Block = old * bl + g * _o.Block;
            _o.StarterOut *= g;
            if (_n >= c.Tail)
            {
                _phase = Phase.Twin;
                c.Reset();
                _twinRun = 0;
                _twinTheta = _twin!._theta;
            }
        }

        /// <summary>The raised-cosine crossfade's gain at <paramref name="u"/> (0 to 1) toward the incoming,
        /// normalised for two signals of correlation <paramref name="rho"/> (see Blend).</summary>
        private static float Gain(float u, float rho)
        {
            float g = 0.5f - 0.5f * MathF.Cos(MathF.PI * Math.Clamp(u, 0f, 1f));
            float h = 0.5f - 0.5f * MathF.Cos(MathF.PI * Math.Clamp(1f - u, 0f, 1f));
            return g / MathF.Sqrt(g * g + h * h + 2f * rho * g * h);
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

        /// <summary>The twin's interpolated sample at <paramref name="twinGain"/>, mixed with the outer's own
        /// at <paramref name="ownGain"/>, onto the outer's outputs.</summary>
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
            if (++_n >= _warm + _fade)
            {
                _phase = Phase.Twin;
                _twinRun = 0;
                _twinTheta = _twin!._theta;
                _cycles?.Reset();
            }
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
    /// The far engine's own last cycles, recorded as the twin plays them and replayed while nothing
    /// changes (todo.md, "Cars in full detail": the distant-car cycle cache). A cycle is the stretch
    /// between two turns of the crank through the cycle's zero, so every one holds each cylinder's
    /// firing once and the firing order is kept. They are played back in a random order with no cycle
    /// twice running, each joined to the next by crossfading the head of the next into what followed
    /// the last in the recording, over a quarter of the shortest cycle: the joins fall where the
    /// recording itself had a cycle boundary, so the crank angle is the same either side. Played at the
    /// speed they were recorded at, each its own length, so the cycle-to-cycle variation of the firing
    /// is kept and nothing is resampled.
    ///
    /// Steady means the last <see cref="Cycles"/> cycles came from the same inputs within a tolerance
    /// (throttle, load, the inertia the driveline puts on the crank, ignition, starter, governor) and
    /// at the same speed within 2 %, with the starter silent. An idle that hunts by hundreds of rpm is
    /// not steady, and stays live: shuffled, its cycles would jump in pitch.
    /// </summary>
    private sealed class CycleCache
    {
        /// <summary>Cycles in a set: enough that the order is not heard to repeat, few enough to be
        /// steady for.</summary>
        public const int Cycles = 6;
        /// <summary>A cycle's mean speed may differ from the set's by this, as a share.</summary>
        private const float SpeedSpread = 0.02f;

        private readonly float[] _ex, _in, _bl;
        private readonly int _cap;
        private readonly int _maxCycle;
        private long _at;                       // outer samples recorded so far

        // The cycles recorded, a ring of the last Cycles + 1: where each starts, its length, and what
        // drove it.
        private readonly long[] _start = new long[Cycles + 1];
        private readonly int[] _len = new int[Cycles + 1];
        private readonly float[] _omega = new float[Cycles + 1], _tMin = new float[Cycles + 1], _tMax = new float[Cycles + 1];
        private readonly float[] _lMin = new float[Cycles + 1], _lMax = new float[Cycles + 1], _inertia = new float[Cycles + 1];
        private readonly float[] _governed = new float[Cycles + 1];
        // The engine's slow state over each cycle (gas temperature, manifold, boost): what drifts for
        // seconds after the inputs settle, and freezes with the engine.
        private readonly float[] _portK = new float[Cycles + 1], _map = new float[Cycles + 1], _spool = new float[Cycles + 1];
        private readonly bool[] _clean = new bool[Cycles + 1];
        private int _count;                     // complete cycles in the ring
        private long _cycleStart = -1;          // the cycle being recorded; -1 before the first boundary
        private double _omegaSum;
        private float _ctMin, _ctMax, _clMin, _clMax, _cInertia, _cGoverned;
        private double _portSum, _mapSum, _spoolSum, _tSum, _lSum;
        // Each cycle's mean pedal and load: a governor moves the pedal within every cycle, chasing the
        // firing, and only the cycle's mean says whether the engine is where it was.
        private readonly float[] _tMean = new float[Cycles + 1], _lMean = new float[Cycles + 1];
        private bool _cClean, _cPopped;
        private float _cPeak;
        private readonly float[] _peak = new float[Cycles + 1];

        // The set being played: copies of the ring's entries at the start, so recording can go on
        // while the set is entered.
        private readonly long[] _setStart = new long[Cycles];
        private readonly int[] _setLen = new int[Cycles];
        public float SetOmega, SetTorque;
        private float _tLo, _tHi, _lLo, _lHi, _inertiaSet, _governedSet;
        private int _tail;
        private float _rho = 0.5f;
        /// <summary>How alike the cycles' heads are (see Likeness).</summary>
        public float Rho => _rho;

        // Playback: the cycle, the position in it, and the one being faded out of.
        private int _cur, _prev = -1;
        private int _pos;
        private uint _rng;

        public CycleCache(EngineProfile e, float rate, int seed)
        {
            // The slowest an engine is steady at: four-fifths of its idle.
            float cyclesPerSecond = 0.8f * MathF.Max(200f, e.IdleRpm) / 60f / (e.CycleDegrees / 360f);
            _maxCycle = (int)(rate / cyclesPerSecond) + 1;
            _cap = (Cycles + 2) * _maxCycle;
            _ex = new float[_cap]; _in = new float[_cap]; _bl = new float[_cap];
            _rng = (uint)seed * 2654435761u | 1u;
        }

        /// <summary>Forgets every cycle: what follows is not steady with what went before.</summary>
        public void Reset()
        {
            _count = 0;
            _cycleStart = -1;
        }

        /// <summary>One sample of the twin as heard, and what drove it. <paramref name="boundary"/>: the
        /// crank passed the cycle's zero on this sample, which starts a cycle here.</summary>
        public void Record(EngineSynth o, float ex, float inn, float bl, float starter, bool boundary)
        {
            if (boundary)
            {
                if (_cycleStart >= 0) Close();
                _cycleStart = _at;
                _omegaSum = 0;
                _portSum = _mapSum = _spoolSum = _tSum = _lSum = 0;
                _ctMin = _ctMax = o.Throttle;
                _clMin = _clMax = o.LoadTorque;
                _cInertia = o.ExternalInertia;
                _cGoverned = o.GovernedRpm;
                _cClean = o.Ignition && !o.Starter;
                _cPopped = false;
                _cPeak = 0f;
            }
            int i = (int)(_at % _cap);
            _ex[i] = ex; _in[i] = inn; _bl[i] = bl;
            _at++;
            if (_cycleStart < 0) return;
            _omegaSum += o._omega;
            _portSum += o._portK; _mapSum += o._map; _spoolSum += o._spool;
            float t = o.Throttle, l = o.LoadTorque;
            _tSum += t; _lSum += l;
            if (t < _ctMin) _ctMin = t; else if (t > _ctMax) _ctMax = t;
            if (l < _clMin) _clMin = l; else if (l > _clMax) _clMax = l;
            if (starter != 0f || !o.Ignition || o.Starter || o.ExternalInertia != _cInertia || o.GovernedRpm != _cGoverned) _cClean = false;
            // A pop in the pipe is an event, not the engine's running sound: replayed, it would come back
            // every few cycles.
            if (!_cPopped)
                for (int c = 0; c < o._n; c++) if (o._cyl[c].PopLeft > 0) { _cPopped = true; break; }
            float peak = MathF.Abs(ex) + MathF.Abs(inn) + MathF.Abs(bl);
            if (peak > _cPeak) _cPeak = peak;
            // Slower than anything steady, or stalled: start again.
            if (_at - _cycleStart > _maxCycle) Reset();
        }

        private void Close()
        {
            int len = (int)(_at - _cycleStart);
            int k = Cycles;
            // The ring keeps the newest Cycles + 1, oldest first.
            if (_count == Cycles + 1)
                for (int j = 0; j < k; j++) Shift(j);
            int n = Math.Min(_count, Cycles);
            _start[n] = _cycleStart; _len[n] = len;
            _omega[n] = (float)(_omegaSum / Math.Max(1, len));
            _portK[n] = (float)(_portSum / Math.Max(1, len));
            _map[n] = (float)(_mapSum / Math.Max(1, len));
            _spool[n] = (float)(_spoolSum / Math.Max(1, len));
            _tMean[n] = (float)(_tSum / Math.Max(1, len));
            _lMean[n] = (float)(_lSum / Math.Max(1, len));
            _tMin[n] = _ctMin; _tMax[n] = _ctMax; _lMin[n] = _clMin; _lMax[n] = _clMax;
            _inertia[n] = _cInertia; _governed[n] = _cGoverned; _clean[n] = _cClean && !_cPopped;
            _peak[n] = _cPeak;
            _count = Math.Min(Cycles + 1, _count + 1);
        }

        private void Shift(int j)
        {
            _start[j] = _start[j + 1]; _len[j] = _len[j + 1]; _omega[j] = _omega[j + 1];
            _tMin[j] = _tMin[j + 1]; _tMax[j] = _tMax[j + 1]; _lMin[j] = _lMin[j + 1]; _lMax[j] = _lMax[j + 1];
            _inertia[j] = _inertia[j + 1]; _governed[j] = _governed[j + 1]; _clean[j] = _clean[j + 1];
            _portK[j] = _portK[j + 1]; _map[j] = _map[j + 1]; _spool[j] = _spool[j + 1]; _peak[j] = _peak[j + 1];
            _tMean[j] = _tMean[j + 1]; _lMean[j] = _lMean[j + 1];
        }

        /// <summary>How far an input may move from what the set was recorded at before the engine must
        /// run again: a hundredth of the pedal and a tenth of where it is; two per cent of the engine's
        /// peak torque and a twentieth of the load.</summary>
        private static float ThrottleTolerance(float t) => 0.01f + 0.1f * t;
        private static float LoadTolerance(EngineSynth o, float l) => 0.02f * o.Profile.PeakTorqueNm + 0.05f * MathF.Abs(l);

        /// <summary>At a boundary: the newest <see cref="Cycles"/> complete cycles are steady, and the set
        /// is taken from them.</summary>
        public bool TrySet(EngineSynth o)
        {
            if (_count < Cycles) return false;
            int first = _count - Cycles;
            double om = 0;
            float tLo = float.MaxValue, tHi = float.MinValue, lLo = float.MaxValue, lHi = float.MinValue;
            int shortest = int.MaxValue;
            for (int j = first; j < _count; j++)
            {
                if (!_clean[j] || _inertia[j] != _inertia[first] || _governed[j] != _governed[first]) return false;
                om += _omega[j];
                tLo = MathF.Min(tLo, _tMin[j]); tHi = MathF.Max(tHi, _tMax[j]);
                lLo = MathF.Min(lLo, _lMin[j]); lHi = MathF.Max(lHi, _lMax[j]);
                shortest = Math.Min(shortest, _len[j]);
            }
            float mean = (float)(om / Cycles);
            if (mean <= 1f) return false;
            // No cycle with a peak twice the set's quietest: a misfire's bang or a rare spike, which a
            // replay would bring back every few cycles.
            float quiet = float.MaxValue;
            for (int j = first; j < _count; j++) quiet = MathF.Min(quiet, _peak[j]);
            for (int j = first; j < _count; j++) if (_peak[j] > 2f * quiet) return false;
            for (int j = first; j < _count; j++)
                if (MathF.Abs(_omega[j] - mean) > SpeedSpread * mean) return false;
            float tTol = ThrottleTolerance(0.5f * (tLo + tHi)), lTol = LoadTolerance(o, 0.5f * (lLo + lHi));
            float tmLo = float.MaxValue, tmHi = float.MinValue, lmLo = float.MaxValue, lmHi = float.MinValue;
            for (int j = first; j < _count; j++)
            {
                tmLo = MathF.Min(tmLo, _tMean[j]); tmHi = MathF.Max(tmHi, _tMean[j]);
                lmLo = MathF.Min(lmLo, _lMean[j]); lmHi = MathF.Max(lmHi, _lMean[j]);
            }
            if (tmHi - tmLo > tTol || lmHi - lmLo > lTol) return false;
            // The slow state settled too, since it stops with the engine: across the set, a tenth of a per
            // cent of the gas temperature, half a per cent of the manifold, a hundredth of the boost.
            int last = _count - 1;
            if (MathF.Abs(_portK[last] - _portK[first]) > 0.001f * _portK[first]
                || MathF.Abs(_map[last] - _map[first]) > 0.005f * _map[first]
                || MathF.Abs(_spool[last] - _spool[first]) > 0.01f) return false;

            for (int j = 0; j < Cycles; j++) { _setStart[j] = _start[first + j]; _setLen[j] = _len[first + j]; }
            SetOmega = mean;
            SetTorque = o.Torque;
            // Let go at half the entry's tolerance past what the set was recorded through: a load that drifts (a
            // mower into longer grass) is taken up live before the replay has held it far from where it went.
            _tLo = tLo - 0.5f * tTol; _tHi = tHi + 0.5f * tTol; _lLo = lLo - 0.5f * lTol; _lHi = lHi + 0.5f * lTol;
            _inertiaSet = _inertia[first]; _governedSet = _governed[first];
            _tail = Math.Max(16, shortest / 4);
            _rho = Likeness();
            _prev = -1;
            _cur = Pick(-1);
            _pos = 0;
            return true;
        }

        /// <summary>How alike two cycles' heads are, on average over the set's neighbours, for the joins'
        /// crossfade.</summary>
        private float Likeness()
        {
            double sxy = 0, sxx = 0, syy = 0;
            for (int j = 0; j + 1 < Cycles; j++)
                for (int p = 0; p < _tail; p++)
                {
                    float x = Sum(_setStart[j] + p), y = Sum(_setStart[j + 1] + p);
                    sxy += (double)x * y; sxx += (double)x * x; syy += (double)y * y;
                }
            return sxx > 1e-20 && syy > 1e-20 ? (float)Math.Clamp(sxy / Math.Sqrt(sxx * syy), 0.0, 1.0) : 0.5f;
        }

        private float Sum(long at) { int i = (int)(at % _cap); return _ex[i] + _in[i] + _bl[i]; }

        /// <summary>Whether the engine's inputs still belong to the set.</summary>
        public bool StillSteady(EngineSynth o)
            => o.Ignition && !o.Starter && o.Throttle >= _tLo && o.Throttle <= _tHi
               && o.LoadTorque >= _lLo && o.LoadTorque <= _lHi
               && MathF.Abs(o.ExternalInertia - _inertiaSet) <= 0.01f * MathF.Abs(_inertiaSet) + 1e-6f
               && o.GovernedRpm == _governedSet;

        private int Pick(int not)
        {
            _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
            int c = (int)(_rng % (uint)(not < 0 ? Cycles : Cycles - 1));
            return not >= 0 && c >= not ? c + 1 : c;
        }

        /// <summary>The position in the cycle being played, and its length.</summary>
        public int Position => _pos;
        public int Length => _setLen[_cur];
        /// <summary>The samples a join is crossfaded over, and where the engine stopped: the same.</summary>
        public int Tail => _tail;
        /// <summary>A join is not under way: the engine may be taken up here.</summary>
        public bool AtResumePoint => _pos == _tail;

        /// <summary>The next sample of the replay.</summary>
        public void Next(out float ex, out float inn, out float bl)
        {
            if (_pos >= _setLen[_cur])
            {
                _prev = _cur;
                _cur = Pick(_cur);
                _pos = 0;
            }
            int i = (int)((_setStart[_cur] + _pos) % _cap);
            ex = _ex[i]; inn = _in[i]; bl = _bl[i];
            if (_prev >= 0 && _pos < _tail)
            {
                // The last cycle's continuation as recorded, into this one's head.
                int k = (int)((_setStart[_prev] + _setLen[_prev] + _pos) % _cap);
                float u = (_pos + 1) / (float)_tail;
                float g = 0.5f - 0.5f * MathF.Cos(MathF.PI * u);
                float norm = 1f / MathF.Sqrt((1f - g) * (1f - g) + g * g + 2f * _rho * g * (1f - g));
                float a = (1f - g) * norm, b = g * norm;
                ex = a * _ex[k] + b * ex; inn = a * _in[k] + b * inn; bl = a * _bl[k] + b * bl;
            }
            _pos++;
        }

        /// <summary>The crank angle the replay is at, degrees through the cycle.</summary>
        public double Angle(float cycleDeg) => cycleDeg * (_pos / (double)Math.Max(1, _setLen[_cur]));
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

        /// <summary>Zeros pushed in a row: past the taps the output is silence, worked out for nothing
        /// (the starter's sound, most of an engine's life).</summary>
        private int _zeros = Taps;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(float v)
        {
            if (v == 0f && _zeros >= Taps) { Even = Odd = 0f; return; }
            _zeros = v == 0f ? _zeros + 1 : 0;
            _at = _at == 0 ? Taps - 1 : _at - 1;
            _x[_at] = v;
            _x[_at + Taps] = v;
            float acc;
            if (Vector.IsHardwareAccelerated && Taps % Vector<float>.Count == 0)
            {
                var sum = Vector<float>.Zero;
                for (int k = 0; k < Taps; k += Vector<float>.Count)
                    sum += new Vector<float>(H, k) * new Vector<float>(_x, _at + k);
                acc = Vector.Sum(sum);
            }
            else
            {
                acc = 0f;
                for (int k = 0; k < Taps; k++) acc += H[k] * _x[_at + k];
            }
            // Newest at _at: the even output is the one Taps/2 old, the odd one half a sample newer.
            Even = _x[_at + Taps / 2];
            Odd = acc;
        }

        public void Clear()
        {
            Array.Clear(_x);
            Even = Odd = 0f;
            _zeros = Taps;
        }
    }
}
