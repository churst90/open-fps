using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;

namespace OpenFPS.Client.AudioEngine.Core.Signals;

/// <summary>
/// An electric car horn, as the self-interrupting buzzer it is.
///
/// A coil around an iron pole; an armature riveted to the middle of a steel diaphragm, a millimetre
/// or so off the pole; and a pair of contact points in series with the coil that the armature pushes
/// open as it moves. Close the relay and the current rises through the coil's inductance (about a
/// millisecond), the pole pulls the armature in, the points open, the current collapses, the
/// diaphragm springs back past rest, the points close, and it goes again — four or five hundred times
/// a second, near the diaphragm's own resonance.
///
/// Three things in it are what the ear uses, and they are each a part:
///
///   THE STRIKE. The points are set so the armature does not stop short of the pole: it HITS it,
///   steel on steel, every cycle, with almost no bounce. A velocity reversed in a tenth of a
///   millisecond is an acceleration spike, and a spike every cycle is a buzz with every harmonic in
///   it. The stop is a very stiff, lossy spring (contact lasting ~0.25 ms, restitution ~0.3) rather
///   than an instantaneous reflection, because a real contact has a duration and that duration is
///   what rolls the buzz off before it becomes a hiss. (At 0.12 ms the 4-8 kHz band sat 7 dB below
///   the total and the centroid at 3.4 kHz — a buzz saw, measured before it was ever played.)
///
///   THE PULL. Magnetic force goes as the current squared over the gap squared, so the pull
///   snatches harder the closer the armature gets — the snap that drives it into the pole.
///
///   THE RADIATOR. Sound is the ACCELERATION of what moves. A disc horn has a flat tone disc on the
///   armature that rings at its own mode (2-4 kHz) every time the armature hits: the brassy, nasal
///   formant. A trumpet horn couples the diaphragm into a coiled exponential column that passes only
///   what lies near n·c/2L and nothing below its flare cutoff: rounder and louder.
///
/// Onset is immediate — the first pull is a full pull, there is no pressure to build — and release
/// is the diaphragm ringing down on its own damping plus the tone disc's ring, a few tens of
/// milliseconds to silence. No bend up and no bend down: that is an air horn's, and its absence is
/// half of why an electric horn sounds electric.
/// </summary>
public sealed class ElectricHorn
{
    private readonly ElectricHornSpec _spec;
    private readonly float _rate;
    private readonly int _seed;
    private readonly Unit[] _units;
    private readonly float _refAmp;
    private float _lowGain = 1f, _highGain = 1f;
    private float _splitLp;
    private readonly float _splitAlpha;
    private bool _wasBlowing;

    /// <summary>Whether the horn button is pressed.</summary>
    public bool Blowing { get; set; }
    /// <summary>Output, pascals at one metre on the horn's axis, valid after Step().</summary>
    public float Out { get; private set; }

    public ElectricHornSpec Spec => _spec;

    public ElectricHorn(ElectricHornSpec spec, float rate = 44100f, int seed = 11)
    {
        if (spec.Units == null || spec.Units.Length == 0) throw new ArgumentException("An electric horn needs at least one unit.", nameof(spec));
        _spec = spec; _rate = rate; _seed = seed;
        _units = new Unit[spec.Units.Length];
        for (int i = 0; i < _units.Length; i++) _units[i] = new Unit(spec, spec.Units[i], rate, seed + i * 7);
        // Each unit is calibrated to unit RMS on its own; the anchor is the whole set, so divide by
        // what they add up to (different notes: their powers add).
        float sum = 0f;
        foreach (var u in spec.Units) sum += MathF.Pow(10f, u.LevelTrimDb / 10f);
        _refAmp = 20e-6f * MathF.Pow(10f, spec.ReferenceDb / 20f) / MathF.Sqrt(MathF.Max(1e-3f, sum));
        float d = spec.Kind == ElectricHornKind.Trumpet ? spec.Units[0].MouthDiameterMetres : spec.Units[0].DiaphragmDiameterMetres;
        float a = 0.5f * MathF.Max(0.02f, d);
        _splitAlpha = OnePole.AlphaFor(Math.Clamp(343f / (2f * MathF.PI * a), 300f, 4000f), rate);
    }

    /// <summary>
    /// Where the listener is in the horn's frame: +z out of the grille, +y up. A 90 mm diaphragm or
    /// an 80 mm trumpet mouth is barely directional at its note and noticeably so by the tone disc's
    /// formant — so mild, and more so in the top.
    /// </summary>
    public void SetListener(Vector3 hornFrame)
    {
        if (hornFrame.LengthSquared() < 1e-4f) return;
        float cos = Math.Clamp(Vector3.Dot(Vector3.Normalize(hornFrame), Vector3.UnitZ), -1f, 1f);
        float front = 0.5f * (1f + cos);
        _lowGain = 0.70f + 0.30f * front;
        _highGain = 0.25f + 0.75f * front * front;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Step()
    {
        bool blow = Blowing;
        // The relay closes and the first pull starts NOW, from the top of a cycle: no supply to build.
        if (blow && !_wasBlowing) foreach (var u in _units) u.Press();
        _wasBlowing = blow;

        float y = 0f;
        for (int i = 0; i < _units.Length; i++) y += _units[i].Step(blow);

        _splitLp += _splitAlpha * (y - _splitLp);
        float low = _splitLp, high = y - _splitLp;
        Out = (low * _lowGain + high * _highGain) * _refAmp;
    }

    /// <summary>
    /// What the model actually does, measured on a fresh copy of it: each unit's note by
    /// autocorrelation, how often it strikes, and the whole set's crest factor, spectral centroid
    /// and level at a metre.
    /// </summary>
    public IEnumerable<string> Describe()
    {
        yield return $"{_spec.Name}: {_spec.Kind}, {_spec.Units.Length} unit(s), {_spec.ReferenceDb:F0} dB at 1 m on axis";
        for (int i = 0; i < _units.Length; i++)
        {
            var s = _spec.Units[i]; var u = _units[i];
            string radiator = _spec.Kind == ElectricHornKind.Disc
                ? $"tone disc {s.ToneDiscHz:F0} Hz"
                : $"column {s.ColumnLengthMetres * 1000f:F0} mm, flare cutoff {u.FlareCutoffHz:F0} Hz";
            yield return $"  unit design {s.Hz:F0} Hz, measured {u.MeasuredHz:F1} Hz, crest {u.CrestFactor:F1}, "
                       + $"strikes/cycle {u.StrikesPerCycle:F2}, rebound {u.Restitution:F2}, strike spread {u.StrikeSpread * 100f:F0}%; {radiator}";
        }
        var m = Measure(_spec, _rate, _seed);
        yield return $"  whole set: rms {m.RmsDb:F1} dB at 1 m, crest {m.Crest:F1}, centroid {m.CentroidHz:F0} Hz";
    }

    /// <summary>Steady-state figures for the whole set, on a fresh copy.</summary>
    internal static (float RmsDb, float Crest, float CentroidHz) Measure(ElectricHornSpec spec, float rate, int seed)
    {
        var h = new ElectricHorn(spec, rate, seed) { Blowing = true };
        for (int i = 0; i < (int)(0.1f * rate); i++) h.Step();
        var buf = new float[8192];
        double e = 0; float peak = 0f;
        for (int i = 0; i < buf.Length; i++) { h.Step(); buf[i] = h.Out; e += h.Out * (double)h.Out; peak = MathF.Max(peak, MathF.Abs(h.Out)); }
        float rms = (float)Math.Sqrt(e / buf.Length);
        return (20f * MathF.Log10(MathF.Max(1e-12f, rms) / 2e-5f), rms > 0 ? peak / rms : 0f, CentroidHz(buf, rate));
    }

    /// <summary>
    /// The note in a buffer, by autocorrelation: the SHORTEST lag whose normalised correlation is
    /// within a few per cent of the best, refined between samples with a parabola.
    ///
    /// Not the longest-best lag, which is what ChimeHorn's measure takes. A car horn's waveform is a
    /// spike a cycle, and when its period is not a whole number of samples (410 Hz at 44.1 kHz is
    /// 107.56 of them) the spike lines up with itself better two cycles on than one — 215.12 is
    /// nearer a whole number — and a longest-best search reports the note an octave low. It did, for
    /// three of the six horns, while the impact speeds printed strike by strike showed no period
    /// doubling at all: the measurement was wrong, not the horn.
    /// </summary>
    internal static float NoteHz(float[] x, float rate, float expect)
    {
        int lo = Math.Max(2, (int)(rate / (expect * 1.6f)));
        int hi = Math.Min(x.Length / 2, (int)(rate / (expect * 0.4f)));
        if (hi <= lo + 2) return 0f;
        var r = new double[hi + 2];
        double e0 = 0; for (int i = 0; i < x.Length; i++) e0 += x[i] * (double)x[i];
        if (e0 <= 0) return 0f;
        double best = 0;
        for (int lag = lo - 1; lag <= hi + 1; lag++)
        {
            double s = 0;
            for (int i = 0; i + lag < x.Length; i++) s += x[i] * (double)x[i + lag];
            r[lag] = s / e0 * x.Length / (x.Length - lag);
            if (lag >= lo && lag <= hi) best = Math.Max(best, r[lag]);
        }
        for (int lag = lo; lag <= hi; lag++)
        {
            if (r[lag] < 0.93 * best || r[lag] < r[lag - 1] || r[lag] < r[lag + 1]) continue;
            double a = r[lag - 1], b = r[lag], c = r[lag + 1];
            double den = a - 2 * b + c;
            double shift = Math.Abs(den) > 1e-12 ? 0.5 * (a - c) / den : 0;
            return (float)(rate / (lag + Math.Clamp(shift, -0.5, 0.5)));
        }
        return 0f;
    }

    /// <summary>Power-weighted mean frequency of a Hann-windowed power-of-two buffer.</summary>
    internal static float CentroidHz(float[] x, float rate)
    {
        int n = 1; while (n * 2 <= x.Length) n *= 2;
        var a = new Complex[n];
        for (int i = 0; i < n; i++) a[i] = x[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
        Fft(a);
        double num = 0, den = 0;
        for (int k = 1; k < n / 2; k++) { double p = a[k].Real * a[k].Real + a[k].Imaginary * a[k].Imaginary; num += p * k * rate / n; den += p; }
        return den > 0 ? (float)(num / den) : 0f;
    }

    private static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    Complex u = a[i + k], v = a[i + k + len / 2] * w;
                    a[i + k] = u + v; a[i + k + len / 2] = u - v;
                    w *= wl;
                }
            }
        }
    }

    /// <summary>
    /// One buzzer: coil, armature on a diaphragm, contact points, and whatever it radiates through.
    ///
    /// The contact points CLOSE on a clock locked to the declared note, rather than when the
    /// diaphragm's return lets them. (They still OPEN where the armature knocks them open, 70% of
    /// the way to the pole, so the current is cut by the motion, as in the real thing.) A real
    /// horn's loop settles near the diaphragm's resonance wherever the adjusting screw puts it; this
    /// model takes the note the screw was set to as a fact and restarts the pull on a clock at that
    /// note. It is a deliberate
    /// simplification, marked as one, for the same reason as the air horn's lock: a self-timed
    /// relay oscillator can stall, double-strike or chatter at a subharmonic depending on the
    /// integration step, and none of that is anything a car horn does. Everything the ear uses — the
    /// strike, the snap of the pull, the rounded current, the disc's ring, the column's filtering,
    /// the ring-down — is still the parts.
    /// </summary>
    internal sealed class Unit
    {
        private const int Over = 4;                  // the strike is ~0.12 ms; resolve it
        private const float Gap = 1f;                // displacement is in units of the air gap
        private const float PullAtRest = 1.8f;       // static pull at full current, in gaps: it WILL reach the pole
        private const float Zeta = 0.18f;            // diaphragm damping: air load and the rim gasket. Below
                                                     // ~0.15 the impact loop can period-double.
        private const float ContactSeconds = 0.00025f; // pole face, armature and rivet are not rigid
        private const float ReboundTarget = 0.3f;    // steel on steel, a hardened pole: little bounce
        private const float BreakAt = 0.7f;          // where on its travel the armature knocks the points open

                private readonly ElectricHornSpec _spec;
        private readonly ElectricHornUnitSpec _u;
        private readonly float _rate, _dt, _trim;
        private readonly float _w0sq, _damp, _pull, _kc, _cc, _riseK, _fallK, _duty;
        private readonly Random _rng;
        private Mode[] _disc = Array.Empty<Mode>();
        private Mode[] _column = Array.Empty<Mode>();
        private float[] _columnGain = Array.Empty<float>();
        private readonly float _hpA, _discCouple;
        private double _phase;
        private bool _open;
        private float _x, _v, _i, _jitter, _hp1, _hp2;
        private float _gain = 1f;
        private int _strikes;
        private float _vIn, _vOutSum; private int _bounces;
        private float _vMin = float.MaxValue, _vMax;

        public float MeasuredHz { get; private set; }
        public float CrestFactor { get; private set; }
        /// <summary>Impacts per cycle of the note. A horn that is working reads 1.00: one hit a
        /// cycle. Nought is a doorbell (it never reaches the pole); two is a chatter.</summary>
        public float StrikesPerCycle { get; private set; }
        /// <summary>Measured coefficient of restitution at the pole: outgoing over incoming speed.</summary>
        public float Restitution { get; private set; }
        /// <summary>How much the impact speed varies from strike to strike, (max − min)/mean. An
        /// impact oscillator can period-double — a hard hit, a soft hit, a hard hit — and the ear
        /// hears that as a note an octave down. A working horn reads a few per cent.</summary>
        public float StrikeSpread { get; private set; }
        public float FlareCutoffHz { get; }

        public Unit(ElectricHornSpec spec, ElectricHornUnitSpec u, float rate, int seed)
        {
            _spec = spec; _u = u; _rate = rate; _dt = 1f / (rate * Over);
            _rng = new Random(seed);
            _trim = MathF.Pow(10f, u.LevelTrimDb / 20f);
            _duty = Math.Clamp(spec.ContactDuty, 0.2f, 0.8f);

            // The diaphragm and armature are tuned to the note: that is what the adjusting screw is
            // for. (Tuned 25% above it, the loop period-doubles — a hard strike, a soft one — and
            // plays an octave low with a growl; the strike spread in Describe() is what shows it.)
            float w0 = MathF.Tau * u.Hz;
            _w0sq = w0 * w0;
            _damp = 2f * Zeta * w0;
            // F = pull · i² / (1.25 − x)², scaled so the static deflection at rest is PullAtRest.
            _pull = _w0sq * PullAtRest * 1.25f * 1.25f;

            float wc = MathF.PI / ContactSeconds;
            _kc = wc * wc;
            float ln = MathF.Log(ReboundTarget);
            float zc = -ln / MathF.Sqrt(MathF.PI * MathF.PI + ln * ln);
            _cc = 2f * zc * wc;

            _riseK = 1f - MathF.Exp(-_dt / MathF.Max(1e-5f, spec.CoilRiseSeconds));
            _fallK = 1f - MathF.Exp(-_dt / 0.00008f);   // points open: the current collapses into the spark

            FlareCutoffHz = CutoffHz(u);
            _hpA = OnePole.AlphaFor(spec.Kind == ElectricHornKind.Trumpet ? FlareCutoffHz : 80f, rate);
            _discCouple = 1.4f;
            Build();
            Calibrate();
        }

        /// <summary>An exponential horn passes nothing below m·c/4π, m = ln(area ratio)/length.</summary>
        public static float CutoffHz(ElectricHornUnitSpec u)
        {
            float areaRatio = MathF.Pow(u.MouthDiameterMetres / MathF.Max(0.003f, u.ThroatDiameterMetres), 2f);
            float m = MathF.Log(MathF.Max(1.2f, areaRatio)) / u.ColumnLengthMetres;
            return m * 343f / (4f * MathF.PI);
        }

        private void Build()
        {
            if (_spec.Kind == ElectricHornKind.Disc)
            {
                // Centre-clamped, free rim: the first ringing mode and the next axisymmetric one,
                // ~6.3x up. Only axisymmetric modes: the armature drives the disc at its centre, and a
                // centred push cannot excite a mode with a nodal line through the centre.
                var list = new List<Mode> { new Mode(_u.ToneDiscHz, 28f, _rate) };
                if (_u.ToneDiscHz * 6.27f < 0.45f * _rate) list.Add(new Mode(_u.ToneDiscHz * 6.27f, 40f, _rate));
                _disc = list.ToArray();
            }
            else
            {
                // The column: n·c/2L with the end correction, cut to the note, so its modes sit on the
                // note's harmonics. Low Q — a horn's whole purpose is to let go of what is in it —
                // and falling with order, as the wall and the mouth take more of the higher ones.
                int n = Math.Max(1, (int)MathF.Min(14f, 0.45f * _rate / _u.Hz));
                _column = new Mode[n]; _columnGain = new float[n];
                for (int k = 0; k < n; k++)
                {
                    _column[k] = new Mode(_u.Hz * (k + 1), 6f / (1f + 0.08f * k), _rate);
                    _columnGain[k] = 1f / (1f + 0.6f * k);
                }
            }
        }

        public void Press() { _phase = 0.0; _open = false; }

        private void Calibrate()
        {
            int warm = (int)(0.3f * _rate), meas = (int)(0.25f * _rate);
            Press();
            for (int i = 0; i < warm; i++) Step(true);
            _strikes = 0; _bounces = 0; _vIn = 0; _vOutSum = 0; _vMin = float.MaxValue; _vMax = 0f;
            var buf = new float[meas];
            double sum = 0; float peak = 0f;
            for (int i = 0; i < meas; i++)
            {
                float y = Step(true);
                buf[i] = y; sum += y * (double)y; peak = MathF.Max(peak, MathF.Abs(y));
            }
            float rms = (float)Math.Sqrt(sum / Math.Max(1, meas));
            MeasuredHz = NoteHz(buf, _rate, _u.Hz);
            CrestFactor = rms > 1e-12f ? peak / rms : 0f;
            StrikesPerCycle = _strikes / (_u.Hz * meas / _rate);
            Restitution = _bounces > 0 && _vIn > 0 ? _vOutSum / _vIn : 0f;
            StrikeSpread = _strikes > 1 ? (_vMax - _vMin) / (_vIn / _strikes) : 0f;
            _gain = rms > 1e-12f ? 1f / rms : 1f;
            Build();
            _x = _v = _i = _jitter = _hp1 = _hp2 = 0f; _phase = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public float Step(bool blowing)
        {
            // The loop wanders a little: the points wear, the voltage sags. It is what makes a pair
            // of horns a third apart roll against each other instead of standing still.
            _jitter += 0.003f * ((float)_rng.NextDouble() * 2f - 1f - 0.02f * _jitter);
            float f = _u.Hz * (1f + 0.0015f * Math.Clamp(_jitter, -1f, 1f));

            float acc = 0f;
            for (int s = 0; s < Over; s++)
            {
                if (blowing)
                {
                    _phase += f * _dt;
                    if (_phase >= 1.0) { _phase -= 1.0; _open = false; }
                }
                // The moving contact rides on the armature: the points close on the clock at the
                // top of each cycle and are knocked open when the armature has travelled far enough
                // toward the pole (or, at the latest, when the clock says so).
                if (_x > BreakAt || _phase >= _duty) _open = true;
                bool closed = blowing && !_open;
                _i += closed ? _riseK * (1f - _i) : -_fallK * _i;

                float gap = 1.25f * Gap - MathF.Min(_x, Gap);
                float force = _pull * _i * _i / (gap * gap);
                float stop = 0f;
                if (_x > Gap)
                {
                    stop = -_kc * (_x - Gap) - _cc * _v;
                    if (stop > 0f) stop = 0f;           // a stop pushes; it never pulls
                }
                float a = -_w0sq * _x - _damp * _v + force + stop;
                float xPrev = _x, vPrev = _v;
                _v += a * _dt;
                _x += _v * _dt;
                if (xPrev <= Gap && _x > Gap) { _strikes++; float sp = MathF.Abs(vPrev); _vIn += sp; _vMin = MathF.Min(_vMin, sp); _vMax = MathF.Max(_vMax, sp); }
                if (xPrev > Gap && _x <= Gap) { _bounces++; _vOutSum += MathF.Abs(_v); }
                acc += a;
            }
            acc *= 1f / Over;

            float y;
            if (_spec.Kind == ElectricHornKind.Disc)
            {
                // The diaphragm and the disc move with the armature and radiate its acceleration;
                // the disc also rings at its own mode each time the armature hits.
                float ring = 0f;
                for (int k = 0; k < _disc.Length; k++) ring += _disc[k].Process(acc) * (k == 0 ? 1f : 0.4f);
                y = acc + _discCouple * ring;
            }
            else
            {
                y = 0f;
                for (int k = 0; k < _column.Length; k++) y += _column[k].Process(acc) * _columnGain[k];
            }
            // Below the flare (trumpet), or below where a 9 cm piston stops radiating anything at all
            // (disc), nothing leaves.
            _hp1 += _hpA * (y - _hp1);
            _hp2 += _hpA * (_hp1 - _hp2);
            return (y - _hp2) * _trim * _gain;
        }
    }
}
