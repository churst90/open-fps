using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Signals;

namespace OpenFPS.Client.AudioEngine.Core.Rail;

/// <summary>
/// What the track does with a force put into it, and how much of that reaches the air.
///
/// The whole track bounces on the ballast near sixty or eighty hertz, the rail on its pads at two to
/// five hundred, and above them the pinned-pinned resonance, half a bending wave per sleeper bay: the
/// peak in the middle of every rolling-noise spectrum. A narrow rail radiates hardly anything below a
/// few hundred hertz, its efficiency rising as frequency squared, which is much of why a train gets
/// louder with speed: the roughness spectrum slides up into where it radiates.
/// </summary>
internal sealed class TrackResponse
{
    private Mode _pad, _pinned, _ballast;
    private float _sleeperLp1, _sleeperLp2;
    private float _railEff1, _railEff2, _sleepEff1, _sleepEff2;
    private readonly float _railEffA, _sleepEffA;
    private readonly float _sleeperA, _sleeperLevel, _railLevel;

    public float PinnedPinnedHz { get; }
    public float PadHz { get; }

    public TrackResponse(TrackSpec t, float rate)
    {
        PinnedPinnedHz = Math.Clamp(t.PinnedPinnedHz, 400f, 2500f);
        // A stiff pad on concrete puts the rail's bounce near five hundred, a soft one on timber near
        // two; slab track is stiffer again.
        PadHz = t.Sleepers switch
        {
            SleeperKind.Timber => 260f,
            SleeperKind.SlabTrack => 520f,
            _ => 400f,
        };
        _pad = new Mode(PadHz, 2.4f, rate);
        _pinned = new Mode(PinnedPinnedHz, 9f, rate);
        _ballast = new Mode(t.Sleepers == SleeperKind.SlabTrack ? 110f : 72f, 2.0f, rate);
        _sleeperA = OnePole.AlphaFor(380f, rate);
        // Radiation efficiency rises as frequency squared until the radiator is big against the
        // wavelength: late for a narrow rail, early for a row of sleepers or a slab.
        _railEffA = OnePole.AlphaFor(600f, rate);
        _sleepEffA = OnePole.AlphaFor(210f, rate);
        // A concrete sleeper presents a big stiff face, a timber one is light and lossy; a slab has no
        // sleepers but is itself an enormous radiator.
        _sleeperLevel = t.Sleepers switch
        {
            SleeperKind.Timber => 0.55f,
            SleeperKind.SlabTrack => 0.35f,
            _ => 1.0f,
        };
        _railLevel = t.Sleepers == SleeperKind.SlabTrack ? 1.25f : 1.0f;
    }

    /// <summary>A force at the contact point: what the rail radiates and what the sleepers do.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public (float Rail, float Sleeper) Step(float force)
    {
        float rail = (_pinned.Process(force) * 1.0f + _pad.Process(force) * 0.8f) * _railLevel;
        // High-pass twice: the (ka)^2 of a narrow radiator.
        _railEff1 += _railEffA * (rail - _railEff1);
        _railEff2 += _railEffA * (_railEff1 - _railEff2);
        float railOut = rail - _railEff2;

        float sl = _ballast.Process(force) * 0.7f;
        _sleeperLp1 += _sleeperA * (force - _sleeperLp1);
        _sleeperLp2 += _sleeperA * (_sleeperLp1 - _sleeperLp2);
        float sleeper = (_sleeperLp2 * 1.4f + sl) * _sleeperLevel;
        _sleepEff1 += _sleepEffA * (sleeper - _sleepEff1);
        _sleepEff2 += _sleepEffA * (_sleepEff1 - _sleepEff2);
        return (railOut, sleeper - _sleepEff2);
    }
}

/// <summary>
/// One bogie on one track: rolling noise, the bangs, and the squeal.
///
/// Rolling: roughness of wavelength λ is met at V/λ hertz, so millimetres to decimetres become a
/// hundred hertz to five kilohertz, all of it sliding up with speed. The centimetre-long contact
/// averages away shorter wavelengths, a low-pass whose corner also rises with speed: a slow train
/// rumbles, a fast one hisses. The rail and sleepers (<see cref="TrackResponse"/>) and the wheel
/// radiate it; the wheel, a steel ring with a loss factor near 1e-4, owns everything above a
/// kilohertz, its modes a ring's (n(n²−1)/√(n²+1)): 400, 1200, 2400 and 4000 Hz for a full-size wheel.
///
/// Bangs: at a joint the wheel drops through the dip angle at the train's speed, and the Hertz
/// contact spring (1.4 GN/m) against the unsprung mass makes a blow of two or three milliseconds,
/// about 150 kN at line speed, into the same rail and wheel. A flat spot does it once a revolution.
///
/// Squeal: on a curve both wheels of a rigid wheelset creep sideways; past a few milliradians the
/// friction falls with sliding speed, negative damping, and the wheel's axial mode grows into a limit
/// cycle. A damped or resilient wheel's loss beats it.
/// </summary>
internal sealed class BogieVoice
{
    private readonly WheelsetSpec _w;
    private readonly TrackSpec _t;
    private readonly TrackResponse _track;
    private readonly float _rate, _dt;
    private readonly Random _rng;

    private readonly Mode[] _wheelModes;
    private readonly float[] _wheelGain;
    private readonly float[] _modeHz;
    private float _contactLp1, _contactLp2;
    private float _hp;
    private readonly float _refAmp;
    private float _chainGain = 1f;

    // Blows in progress: a six-wheel bogie on staggered joints can have two at once.
    private struct Blow { public double T; public float Peak, Tau; public bool Live; }
    private readonly Blow[] _impact;
    private readonly float _contactHz, _impactScale;

    // Squeal.
    private Mode _squealMode;
    private float _squealState, _squealDrive;
    private readonly float _squealRelease;   // 0.999 a sample at 44.1 kHz: 23 ms
    private readonly float _squealHz;

    // The axles share the track's radiators and one roughness process scaled by their count; each
    // meets each joint at its own moment, and that is the whole of the rhythm.
    private readonly AxleSchedule _schedule;
    private readonly float _axleGain;
    private readonly float[] _struck = new float[AxleSchedule.MaxPerSample];

    public IReadOnlyList<float> WheelModeHz => _modeHz;
    public float SquealHz => _squealHz;
    public float ContactResonanceHz => _contactHz;

    /// <param name="blows">How many blows may ring at once: six for one bogie (a six-wheel bogie on
    /// staggered joints can have two at once), more for a chain carrying many (StepShared).</param>
    public BogieVoice(WheelsetSpec w, TrackSpec t, TrackResponse track, float referenceDb,
                      int axles, float wheelbase, float rate, int seed, int blows = 6)
    {
        _w = w; _t = t; _track = track; _rate = rate; _dt = 1f / rate; _squealRelease = At44k.Decay(0.999f, rate);
        _rng = new Random(seed);
        int na = Math.Max(1, axles);
        _schedule = new AxleSchedule(w, t, na, wheelbase, _rng);
        _impact = new Blow[blows];
        // Independent roughness under each axle: n times the energy, not the pressure.
        _axleGain = MathF.Sqrt(na);

        // The rim as a ring bending out of its own plane.
        float radius = 0.5f * MathF.Max(0.2f, w.DiameterMetres) - 0.5f * w.RimThicknessMetres;
        float inertia = w.RimThicknessMetres * MathF.Pow(w.RimWidthMetres, 3f) / 12f;
        float area = w.RimThicknessMetres * w.RimWidthMetres;
        const float steelE = 210e9f, steelRho = 7850f;
        float c = MathF.Sqrt(steelE * inertia / (steelRho * area)) / (MathF.Tau * radius * radius);

        var hz = new List<float>();
        for (int n = 2; n <= 8; n++)
        {
            float f = c * n * (n * n - 1f) / MathF.Sqrt(n * n + 1f);
            if (f > rate * 0.44f) break;
            hz.Add(f);
        }
        if (hz.Count == 0) hz.Add(800f);
        _modeHz = hz.ToArray();
        _wheelModes = new Mode[_modeHz.Length];
        _wheelGain = new float[_modeHz.Length];
        // Rolling and hammering do not ring the wheel as a squeal does: the rail loads it. The
        // undamped Q is the squeal's alone.
        float qRoll = MathF.Min(1f / MathF.Max(1e-5f, w.LossFactor), 170f);
        for (int i = 0; i < _modeHz.Length; i++)
        {
            _wheelModes[i] = new Mode(_modeHz[i], qRoll, rate);
            // The high modes radiate better and are excited less: a gentle tilt up and then away.
            _wheelGain[i] = MathF.Min(1f, _modeHz[i] / 900f) / (1f + 0.35f * i);
        }

        // Hertzian contact against the unsprung mass: 1.4 GN/m is the standard linearised
        // wheel-on-rail contact stiffness.
        const float kHertz = 1.4e9f;
        _contactHz = MathF.Sqrt(kHertz / MathF.Max(50f, w.UnsprungKg)) / MathF.Tau;
        _impactScale = MathF.Sqrt(kHertz * MathF.Max(50f, w.UnsprungKg));

        // The squeal takes a mode high enough to radiate and low enough for the creep to drive.
        int pick = Math.Min(_modeHz.Length - 1, 2);
        _squealHz = _modeHz[pick];
        _squealMode = new Mode(_squealHz, MathF.Min(1f / MathF.Max(1e-5f, w.LossFactor), 900f), rate);

        // Tread brakes corrugate the tread, a disc brake does not: nine decibels, the biggest single
        // difference between a freight train and a passenger train.
        float roughDb = t.RoughnessDb + (w.TreadBraked ? 9f : 0f) + t.StructureDb;
        _refAmp = 20e-6f * MathF.Pow(10f, (referenceDb + roughDb) / 20f);
        Calibrate();
    }

    /// <summary>Run it at the reference speed and set the gain that makes it hit the anchor.</summary>
    private void Calibrate()
    {
        const float vRef = 27.78f;    // 100 km/h
        int n = (int)(0.6f * _rate);
        double e = 0;
        for (int i = 0; i < n; i++) { float y = Roll(vRef); if (i > n / 3) e += y * (double)y; }
        float rms = (float)Math.Sqrt(e / Math.Max(1, n - n / 3));
        // The axle count must survive the calibration: normalised with the axle gain inside, a
        // six-wheel bogie was as loud as a two-wheel one, five decibels wrong.
        _chainGain = rms > 1e-9f ? _axleGain / rms : 1f;
        _hp = _contactLp1 = _contactLp2 = 0f;
    }

    /// <summary>Puts the bogie at a place on the track and works out when each axle next meets something.</summary>
    public void Place(double position) => _schedule.Place(position);

    /// <summary>The rolling part on its own: roughness, contact filter, three radiators.</summary>
    private float Roll(float speed) => Radiate(RollForce(speed));

    /// <summary>The force the roughness puts into the contact, before anything radiates it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float RollForce(float speed)
    {
        float v = MathF.Max(0.05f, speed);
        // Roughness, white in frequency, scaled by the speed to the roughness spectrum's slope: the
        // ISO limit curve falls about fourteen decibels a decade toward short wavelengths, 0.7.
        float n = (float)(_rng.NextDouble() * 2 - 1);
        float excite = n * MathF.Pow(v / 27.78f, 0.7f);

        // The contact patch averages away anything shorter than about twice its length.
        float fc = Math.Clamp(v / 0.012f, 120f, 9000f);
        float a = OnePole.AlphaFor(fc, _rate);
        _contactLp1 += a * (excite - _contactLp1);
        _contactLp2 += a * (_contactLp1 - _contactLp2);
        return _contactLp2 * 30f * _axleGain;
    }

    /// <summary>A force at the contact: the rail, the sleepers and the wheel, and what each of them
    /// does with it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float Radiate(float force)
    {
        var (rail, sleeper) = _track.Step(force);
        float wheel = 0f;
        for (int i = 0; i < _wheelModes.Length; i++) wheel += _wheelModes[i].Process(force) * _wheelGain[i];
        float y = rail * 1.0f + sleeper * 0.85f + wheel * 1.15f;
        _hp += OnePole.AlphaFor(35f, _rate) * (y - _hp);
        return y - _hp;
    }

    /// <summary>
    /// Advance by one sample at this speed and return pascals at one metre.
    /// <paramref name="curveDemand"/> is how hard this axle is being asked to go round the corner,
    /// as creep angle in radians.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float Step(float speed, float curveDemand)
    {
        float v = MathF.Max(0f, speed);

        // Every force at the contact this sample, summed, and radiated once. The rail, the sleepers and
        // the wheel are filters with state: radiating the roll and each live blow separately stepped
        // them two or three times a sample while a blow lasted, which put every mode an octave or more
        // up for those milliseconds.
        float force = RollForce(v);

        // Each axle has its own place on the rail: two quick bangs, and the next bogie's after the
        // car's length.
        int n = _schedule.Advance(v, _dt, _struck);
        for (int i = 0; i < n; i++) Strike(_struck[i], 1f);

        float y = Radiate(Blows(force));
        return (y + Squeal(v, curveDemand, 1f)) * _refAmp * _chainGain;
    }

    /// <summary>
    /// One sample of a chain carrying many bogies of this make at once (TrainSlotState). The wheel, the
    /// rail and the sleepers are linear, so many bogies through one chain are the sum of each through its
    /// own: the roughness of each is independent noise, so together they are one noise <paramref
    /// name="rollWeight"/> (the root of the sum of their squared weights) times as strong, and each
    /// one's blows come in through <see cref="Inject"/> at its own moment and weight. Its own schedule
    /// is not used.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float StepShared(float speed, float curveDemand, float rollWeight)
    {
        float v = MathF.Max(0f, speed);
        float y = Radiate(Blows(RollForce(v) * rollWeight));
        return (y + Squeal(v, curveDemand, rollWeight)) * _refAmp * _chainGain;
    }

    /// <summary>A blow from a bogie this chain carries, at this impact speed and weight.</summary>
    public void Inject(float impactMps, float weight) => Strike(impactMps, weight);

    /// <summary><paramref name="force"/> with the force of the blows in progress added, advanced a sample.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float Blows(float force)
    {
        for (int i = 0; i < _impact.Length; i++)
        {
            ref var im = ref _impact[i];
            if (!im.Live) continue;
            float x = (float)(im.T / im.Tau);
            if (x >= 1f) { im.Live = false; continue; }
            force += MathF.Sin(MathF.PI * x) * im.Peak;
            im.T += _dt;
        }
        return force;
    }

    /// <summary>The squeal, before the bogie's gain: <paramref name="weight"/> is how many like it,
    /// as for the roll.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float Squeal(float v, float curveDemand, float weight)
    {
        float y = 0f;
        float creep = MathF.Abs(curveDemand);
        // Lateral creepage saturates at about five milliradians; below it the contact grips, which
        // is why a wheelset squeals on a hundred-metre curve and not on a five-hundred-metre one.
        float excess = creep - 0.005f;
        if (excess > 0f && v > 0.5f)
        {
            // Negative damping as far past saturation as the creep is; the tanh (friction falling
            // with sliding speed) limits the cycle. No factor outside the loop: one made every
            // wheel squeal equally hard however well damped.
            float gain = 1f + 5f * MathF.Min(1f, excess / 0.008f) * MathF.Min(1f, v / 4f);
            // A damped wheel's loss beats the friction: below a loop gain of one, no limit cycle.
            gain = 1f + (gain - 1f) * MathF.Min(1f, 2.5e-4f / MathF.Max(1e-5f, _w.LossFactor));
            _squealDrive = MathF.Tanh(gain * _squealState) + 0.0015f * ((float)_rng.NextDouble() * 2f - 1f);
            _squealState = _squealMode.Process(_squealDrive);
            // Flanging: the flange grinding on the gauge face, broadband.
            float fl = (float)(_rng.NextDouble() * 2 - 1) * 0.3f * MathF.Abs(_squealState);
            y += (_squealState + fl) * 3.2f * MathF.Min(1f, excess / 0.008f) * weight;
        }
        else if (_squealState != 0f)
        {
            _squealState *= _squealRelease;
            if (MathF.Abs(_squealState) < 1e-6f) _squealState = 0f;
        }
        return y;
    }

    /// <summary>A blow: the unsprung mass meeting the contact spring at this closing speed, at this
    /// weight (how loud this bogie is in the chain carrying it: one for its own).</summary>
    private void Strike(float impactMps, float weight)
    {
        float peak = impactMps * _impactScale;
        // Past three or four times the static load the wheel and rail separate: uncapped, a flat at
        // line speed asked for a meganewton.
        float cap = 3.5f * _w.AxleLoadTonnes * 0.5f * 9810f;
        peak = MathF.Min(peak, cap);
        for (int i = 0; i < _impact.Length; i++)
        {
            if (_impact[i].Live) continue;
            _impact[i] = new Blow
            {
                T = 0,
                // Half the contact period: 200 Hz is a 2.5 ms blow.
                Tau = 0.5f / MathF.Max(40f, _contactHz),
                Peak = peak * 3.2e-5f * weight,     // into the same units the roughness force uses
                Live = true,
            };
            return;
        }
    }
}

/// <summary>
/// When each axle of one bogie next meets a joint or brings a flat round: the rhythm of a bogie, apart
/// from the chain that radiates it, so a bogie can be heard through a chain it shares with others
/// (BogieVoice.StepShared).
/// </summary>
internal sealed class AxleSchedule
{
    /// <summary>The most impacts one bogie can make in one sample: a joint and a flat on each of three axles.</summary>
    public const int MaxPerSample = 6;

    private readonly WheelsetSpec _w;
    private readonly TrackSpec _t;
    private readonly Random _rng;
    private readonly double[] _axleOffset, _nextJoint, _nextFlat;
    private readonly double _period, _circ;
    private double _distance;             // metres the bogie centre has travelled

    public AxleSchedule(WheelsetSpec w, TrackSpec t, int axles, float wheelbase, Random rng)
    {
        _w = w; _t = t; _rng = rng;
        int na = Math.Clamp(axles, 1, 3);
        _axleOffset = new double[na];
        _nextJoint = new double[na];
        _nextFlat = new double[na];
        for (int i = 0; i < na; i++)
            _axleOffset[i] = na == 1 ? 0.0 : (i - (na - 1) * 0.5) * (wheelbase / (na - 1));
        _period = t.JointSpacingMetres > 0.1f ? (t.StaggeredJoints ? t.JointSpacingMetres * 0.5 : t.JointSpacingMetres) : 0.0;
        _circ = Math.PI * w.DiameterMetres;
    }

    /// <summary>Where the bogie centre is along the track, metres.</summary>
    public double Distance => _distance;

    /// <summary>Puts the bogie at a place on the track and works out when each axle next meets something.</summary>
    public void Place(double position)
    {
        _distance = position;
        for (int i = 0; i < _axleOffset.Length; i++)
        {
            double at = position + _axleOffset[i];
            _nextJoint[i] = _period > 0.0 ? Math.Ceiling(at / _period) * _period : double.MaxValue;
            _nextFlat[i] = _w.FlatLengthMetres > 1e-3f ? Math.Ceiling(at / _circ) * _circ : double.MaxValue;
        }
    }

    /// <summary>Moves on one sample at <paramref name="v"/> m/s and writes the impact speed of each axle
    /// that met something; returns how many did.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Advance(float v, float dt, Span<float> impacts)
    {
        _distance += v * dt;
        int n = 0;
        for (int i = 0; i < _axleOffset.Length; i++)
        {
            double at = _distance + _axleOffset[i];
            if (at >= _nextJoint[i])
            {
                _nextJoint[i] += _period;
                // The impact speed: the dip angle times the train's speed.
                impacts[n++] = _t.JointDipRadians * v * (0.8f + 0.4f * (float)_rng.NextDouble());
            }
            if (at >= _nextFlat[i])
            {
                _nextFlat[i] += _circ;
                // A flat of length L arrives at about V L / D: far harder than a joint, which is why
                // one bad wheel is audible over the whole train.
                impacts[n++] = v * _w.FlatLengthMetres / MathF.Max(0.1f, _w.DiameterMetres);
            }
        }
        return n;
    }

    /// <summary>
    /// Moves on <paramref name="count"/> samples at a steady <paramref name="v"/> m/s and writes the
    /// sample within them and the impact speed of every axle that met something: the same moments
    /// <see cref="Advance"/> finds sample by sample, worked out once for the block. Returns how many. A
    /// voice carrying a hundred bogies asked each one every sample whether it had reached a joint.
    /// </summary>
    public int AdvanceBlock(float v, float dt, int count, Span<int> at, Span<float> impact)
    {
        double step = v * (double)dt;
        double start = _distance;
        _distance += step * count;
        int n = 0;
        if (step <= 0.0) return 0;
        for (int i = 0; i < _axleOffset.Length; i++)
        {
            // Sample k (0-based) leaves the bogie at start + step·(k+1): the first k at which an axle is
            // at or past the joint is the sample it strikes, as in Advance.
            while (true)
            {
                double k = Math.Ceiling((_nextJoint[i] - _axleOffset[i] - start) / step) - 1.0;
                if (k >= count || n >= at.Length) break;
                _nextJoint[i] += _period;
                at[n] = Math.Max(0, (int)k);
                impact[n++] = _t.JointDipRadians * v * (0.8f + 0.4f * (float)_rng.NextDouble());
            }
            while (true)
            {
                double k = Math.Ceiling((_nextFlat[i] - _axleOffset[i] - start) / step) - 1.0;
                if (k >= count || n >= at.Length) break;
                _nextFlat[i] += _circ;
                at[n] = Math.Max(0, (int)k);
                impact[n++] = v * _w.FlatLengthMetres / MathF.Max(0.1f, _w.DiameterMetres);
            }
        }
        return n;
    }
}
