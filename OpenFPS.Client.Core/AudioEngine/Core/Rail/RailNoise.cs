using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Signals;

namespace OpenFPS.Client.AudioEngine.Core.Rail;

/// <summary>
/// What the track does with a force put into it, and how much of that reaches the air.
///
/// A rail is a beam on a row of springs, and it has two resonances that matter. Low down, the whole
/// track — rail, sleepers and all — bounces on the ballast, somewhere near sixty or eighty hertz.
/// Higher up, the rail alone bounces on the pads that hold it to the sleepers, at two to five
/// hundred. And above those there is the PINNED-PINNED resonance, where half a bending wave in the
/// rail fits exactly into one sleeper bay and the rail flaps between its supports: it is the peak in
/// the middle of every rolling-noise spectrum a train has ever made, and it is nothing but the rail
/// section and the sleeper spacing.
///
/// Then there is the question of how much of that vibration becomes sound, and the answer below a
/// few hundred hertz is "hardly any". A rail is narrow compared with the wavelength it is trying to
/// radiate; the air just slides round it from the compressed side to the rarefied one. So radiation
/// efficiency rises with the square of frequency until the wavelength stops being the problem.
/// That single term is a large part of why a train gets so much louder with speed: the roughness
/// spectrum slides up with the speed, and the top of it radiates far better than the bottom.
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
        // The pad: a stiff modern pad on concrete puts the rail's own bounce up near five hundred, a
        // soft one on timber leaves it down at two. Slab track is stiffer again.
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
        // Radiation efficiency: rises as the square of frequency until the wavelength is no longer
        // bigger than the radiator. A rail is a narrow thing and gets there late; a row of sleepers
        // or a slab is wide and gets there early.
        _railEffA = OnePole.AlphaFor(600f, rate);
        _sleepEffA = OnePole.AlphaFor(210f, rate);
        // A concrete sleeper is heavy and stiff and presents a big flat face; a timber one is light
        // and lossy and radiates rather less. A slab has no sleepers at all, but the slab itself is
        // an enormous radiator and it makes up for it in the middle.
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
/// One wheelset on one track: rolling noise, the bangs, and the squeal.
///
/// ROLLING. Neither surface is smooth. Run a wheel over a railhead at V metres a second and an
/// irregularity of wavelength lambda is met at V/lambda hertz — so a roughness spectrum that lives
/// in the millimetres-to-decimetres becomes a sound that lives from a hundred hertz to five
/// kilohertz, and the WHOLE of it slides up as the train speeds up. The two surfaces do not touch
/// at a point, though: the contact is an ellipse about a centimetre long, and anything shorter than
/// that is averaged out rather than ridden over, which is a low-pass in WAVELENGTH — so its corner
/// in hertz rises with speed too, and a slow train rumbles where a fast one hisses.
///
/// Then three things radiate it. The rail and the sleepers, which is what <see cref="TrackResponse"/>
/// is. And the WHEEL, which is a steel ring with a loss factor of about one part in ten thousand,
/// and which owns everything above a kilohertz. Its modes are a ring's — n(n^2-1)/sqrt(n^2+1) times
/// its bending stiffness over its mass, over the square of its radius — so they come out at four
/// hundred, twelve hundred, twenty-four hundred and four thousand hertz for a full-size wheel, and a
/// good deal higher for a tram's. Nothing here is a filter chosen to sound right; it is a rim with
/// dimensions.
///
/// BANGS. At a rail joint the wheel drops through the dip angle, so it arrives with a vertical speed
/// of the dip times the train's speed. What stops it is the Hertzian contact spring, about 1.4
/// giganewtons a metre, against the unsprung mass; those two give a contact resonance near two
/// hundred hertz and so a blow two or three milliseconds long, and a peak force of the impact speed
/// times the square root of stiffness times mass — a hundred and fifty kilonewtons for an ordinary
/// joint at line speed. That force goes into the SAME rail and the SAME wheel, which is why a clack
/// sounds like the train it is attached to. A flat spot on the tread does the same thing once a
/// revolution.
///
/// SQUEAL. A wheelset is two wheels rigidly joined, so on a curve the outer one has further to go
/// than the inner and neither can have it: both creep sideways. Past a few milliradians the friction
/// saturates and starts falling with sliding speed, which is negative damping, and the wheel's own
/// axial modes grow into a limit cycle. Whether it squeals is a question about the curve radius and
/// the bogie wheelbase and nothing else — and a resilient or damped wheel does not, because the loss
/// factor beats the negative damping.
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

    // Blows in progress. A six-wheel bogie on staggered joints can easily have two at once.
    private struct Blow { public double T; public float Peak, Tau; public bool Live; }
    private readonly Blow[] _impact = new Blow[6];
    private readonly float _contactHz, _impactScale;

    // Squeal.
    private Mode _squealMode;
    private float _squealState, _squealDrive;
    private readonly float _squealHz;

    // One bogie, not one axle. The axles of a bogie share a rail and share sleepers — the track
    // under them is the same track — so they share the radiators and one roughness process scaled
    // by how many of them there are. What they do NOT share is WHERE they are: each one meets each
    // joint at its own moment, and that is the whole of the rhythm.
    private readonly double[] _axleOffset;
    private readonly double[] _nextJoint, _nextFlat;
    private readonly float _axleGain;
    private double _distance;             // metres the bogie centre has travelled

    /// <summary>Where along the track the bogie centre is, metres.</summary>
    public double Position { get; private set; }
    public int Axles => _axleOffset.Length;

    public IReadOnlyList<float> WheelModeHz => _modeHz;
    public float SquealHz => _squealHz;
    public float ContactResonanceHz => _contactHz;

    public BogieVoice(WheelsetSpec w, TrackSpec t, TrackResponse track, float referenceDb,
                      int axles, float wheelbase, float rate, int seed)
    {
        _w = w; _t = t; _track = track; _rate = rate; _dt = 1f / rate;
        _rng = new Random(seed);
        int na = Math.Max(1, axles);
        _axleOffset = new double[na];
        _nextJoint = new double[na];
        _nextFlat = new double[na];
        for (int i = 0; i < na; i++)
            _axleOffset[i] = na == 1 ? 0.0 : (i - (na - 1) * 0.5) * (wheelbase / (na - 1));
        // Independent roughness under each axle: n axles is n times the energy, not n times the
        // pressure.
        _axleGain = MathF.Sqrt(na);

        // The rim as a ring bending out of its own plane. This is the wheel's voice and it is four
        // numbers: how big it is, how thick, how wide, and what it is made of.
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
        // Rolling and hammering do NOT ring the wheel the way a squeal does: the rail is pressed
        // against it the whole time and loads it. The undamped Q only comes out when the contact is
        // sliding instead of rolling, which is the squeal below.
        float qRoll = MathF.Min(1f / MathF.Max(1e-5f, w.LossFactor), 170f);
        for (int i = 0; i < _modeHz.Length; i++)
        {
            _wheelModes[i] = new Mode(_modeHz[i], qRoll, rate);
            // The high modes radiate better (the wheel is finally big against the wavelength) and
            // are excited less. Net: a gentle tilt up and then away.
            _wheelGain[i] = MathF.Min(1f, _modeHz[i] / 900f) / (1f + 0.35f * i);
        }

        // Hertzian contact against the unsprung mass: what a blow at a joint costs and how long it
        // lasts. 1.4 GN/m is the standard linearised wheel-on-rail contact stiffness.
        const float kHertz = 1.4e9f;
        _contactHz = MathF.Sqrt(kHertz / MathF.Max(50f, w.UnsprungKg)) / MathF.Tau;
        _impactScale = MathF.Sqrt(kHertz * MathF.Max(50f, w.UnsprungKg));

        // The squeal takes the wheel mode with the most to gain: high enough to radiate, low enough
        // that the creep can drive it. In practice that is the third or fourth axial mode.
        int pick = Math.Min(_modeHz.Length - 1, 2);
        _squealHz = _modeHz[pick];
        _squealMode = new Mode(_squealHz, MathF.Min(1f / MathF.Max(1e-5f, w.LossFactor), 900f), rate);

        // Tread brakes corrugate the tread they drag on; a disc brake leaves it alone. Nine decibels,
        // and it is the biggest single difference between a freight train and a passenger train.
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
        // The axle count must SURVIVE the calibration. Normalising the chain to unit RMS with the
        // axle gain already inside it makes a six-wheel bogie exactly as loud as a two-wheel one,
        // which is silently wrong and worth five decibels.
        _chainGain = rms > 1e-9f ? _axleGain / rms : 1f;
        _hp = _contactLp1 = _contactLp2 = 0f;
    }

    /// <summary>Put the axle at a place on the track, and work out when it next meets something.</summary>
    public void Place(double position)
    {
        Position = position;
        _distance = position;
        double period = _t.JointSpacingMetres > 0.1f
            ? (_t.StaggeredJoints ? _t.JointSpacingMetres * 0.5 : _t.JointSpacingMetres)
            : 0.0;
        double circ = Math.PI * _w.DiameterMetres;
        for (int i = 0; i < _axleOffset.Length; i++)
        {
            double at = position + _axleOffset[i];
            _nextJoint[i] = period > 0.0 ? Math.Ceiling(at / period) * period : double.MaxValue;
            _nextFlat[i] = _w.FlatLengthMetres > 1e-3f ? Math.Ceiling(at / circ) * circ : double.MaxValue;
        }
    }

    /// <summary>The rolling part on its own: roughness, contact filter, three radiators.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float Roll(float speed)
    {
        float v = MathF.Max(0.05f, speed);
        // Roughness, met at V over lambda. Taken as white in frequency and scaled by the speed to
        // the power of the roughness spectrum's own slope: the ISO limit curve falls about fourteen
        // decibels a decade toward short wavelengths, which is an exponent near 0.7.
        float n = (float)(_rng.NextDouble() * 2 - 1);
        float excite = n * MathF.Pow(v / 27.78f, 0.7f);

        // The contact patch averages away anything shorter than about twice its length, so this
        // corner walks up the spectrum with the speed. A centimetre of patch at 28 m/s is 2.3 kHz.
        float fc = Math.Clamp(v / 0.012f, 120f, 9000f);
        float a = OnePole.AlphaFor(fc, _rate);
        _contactLp1 += a * (excite - _contactLp1);
        _contactLp2 += a * (_contactLp1 - _contactLp2);
        float force = _contactLp2 * 30f * _axleGain;

        return Radiate(force);
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
        _distance += v * _dt;
        Position = _distance;

        float y = Roll(v);

        // Anything any of the axles is about to hit. Each has its own place on the rail, so a
        // bogie gives the two quick bangs and the next bogie gives them again after the car's
        // length has gone by. Nothing sequences that; it is where the axles are.
        double period = _t.JointSpacingMetres > 0.1f
            ? (_t.StaggeredJoints ? _t.JointSpacingMetres * 0.5 : _t.JointSpacingMetres)
            : 0.0;
        double circ = Math.PI * _w.DiameterMetres;
        for (int i = 0; i < _axleOffset.Length; i++)
        {
            double at = _distance + _axleOffset[i];
            if (at >= _nextJoint[i])
            {
                _nextJoint[i] += period;
                // The wheel falls through the dip angle at the train's speed. That is the impact
                // velocity, and everything else follows from it.
                Strike(_t.JointDipRadians * v * (0.8f + 0.4f * (float)_rng.NextDouble()));
            }
            if (at >= _nextFlat[i])
            {
                _nextFlat[i] += circ;
                // A flat of length L on a wheel of radius R arrives at about V L / 2R — a far
                // harder blow than a joint, which is why one bad wheel in a train is audible over
                // everything else in it.
                Strike(v * _w.FlatLengthMetres / MathF.Max(0.1f, _w.DiameterMetres));
            }
        }

        for (int i = 0; i < _impact.Length; i++)
        {
            ref var im = ref _impact[i];
            if (!im.Live) continue;
            float x = (float)(im.T / im.Tau);
            if (x >= 1f) { im.Live = false; continue; }
            float f = MathF.Sin(MathF.PI * x) * im.Peak;
            im.T += _dt;
            y += Radiate(f);
        }

        // Curve squeal: creep past the threshold turns the friction slope negative and the wheel's
        // own mode grows until the sliding saturates it.
        float creep = MathF.Abs(curveDemand);
        // Lateral creepage saturates at about half a per cent — five milliradians. Below that the
        // contact still grips and there is no negative damping to be had, which is why a wheelset
        // squeals on a hundred-metre curve and says nothing at all on a five-hundred-metre one.
        float excess = creep - 0.005f;
        if (excess > 0f && v > 0.5f)
        {
            // Negative damping, proportional to how far past saturation the creep is; the tanh is
            // the friction falling off with sliding speed, and it is what limits the cycle. The
            // LOOP GAIN has to be this number and nothing else: an extra factor outside the loop
            // (there was a six here) makes every wheel squeal equally hard however well damped it
            // is, which defeats the whole point of asking what the wheel is made of.
            float gain = 1f + 5f * MathF.Min(1f, excess / 0.008f) * MathF.Min(1f, v / 4f);
            // A damped or resilient wheel cannot be driven: its own loss beats the friction, and
            // below a loop gain of one there is no limit cycle at all.
            gain = 1f + (gain - 1f) * MathF.Min(1f, 2.5e-4f / MathF.Max(1e-5f, _w.LossFactor));
            _squealDrive = MathF.Tanh(gain * _squealState) + 0.0015f * ((float)_rng.NextDouble() * 2f - 1f);
            _squealState = _squealMode.Process(_squealDrive);
            // Flanging: the flange grinding on the gauge face is broadband and goes with it.
            float fl = (float)(_rng.NextDouble() * 2 - 1) * 0.3f * MathF.Abs(_squealState);
            y += (_squealState + fl) * 3.2f * MathF.Min(1f, excess / 0.008f);
        }
        else if (_squealState != 0f)
        {
            _squealState *= 0.999f;
            if (MathF.Abs(_squealState) < 1e-6f) _squealState = 0f;
        }

        return y * _refAmp * _chainGain;
    }

    /// <summary>A blow: the unsprung mass meeting the contact spring at this closing speed.</summary>
    private void Strike(float impactMps)
    {
        float peak = impactMps * _impactScale;
        // The contact cannot pull, and past three or four times the static load the rail and the
        // wheel simply separate instead of taking more. Without that cap a flat spot at line speed
        // asks for a meganewton.
        float cap = 3.5f * _w.AxleLoadTonnes * 0.5f * 9810f;
        peak = MathF.Min(peak, cap);
        for (int i = 0; i < _impact.Length; i++)
        {
            if (_impact[i].Live) continue;
            _impact[i] = new Blow
            {
                T = 0,
                // Half of the contact period: a two hundred hertz contact resonance is a two and a
                // half millisecond blow, and that is the corner in its force spectrum.
                Tau = 0.5f / MathF.Max(40f, _contactHz),
                Peak = peak * 3.2e-5f,     // into the same units the roughness force uses
                Live = true,
            };
            return;
        }
    }
}
