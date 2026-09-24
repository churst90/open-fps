using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;

namespace OpenFPS.Client.AudioEngine.Core.Signals;

/// <summary>
/// An air horn, as the reed-and-column instrument it is.
///
/// A Nathan or Leslie chime is a steel diaphragm lying over a port with a tapered pipe screwed to
/// it. Main reservoir air lifts the diaphragm off its seat; the air that rushes through drops the
/// pressure behind it and the diaphragm slams back down; the column of air in the pipe pushes back
/// on the next cycle, and within a few milliseconds the diaphragm has stopped doing what the
/// diaphragm wants and started doing what the PIPE wants. That is why one horn body with five bells
/// screwed into it plays five notes on five identical reeds, and why the note is c/2L rather than
/// anything about steel.
///
/// So this is a coupled oscillator and not a sawtooth with a filter on it. The column is a bank of
/// modes at n·c/2L — ALL the harmonics, because a flaring horn behaves like a full cone and not
/// like a cylinder with a lid on it — and they are BANDPASS sections, because a pipe's input
/// impedance is resistive at its resonances. The reed is a mass on a spring tuned BELOW the column
/// and therefore driven above its own resonance, which is the only way an outward-striking valve
/// pumps energy into a pipe instead of damping it (and is why a brass player's lips buzz below the
/// note). It BEATS on its seat, and that clip at zero is where the harmonics come from: a valve that
/// shuts completely for part of each cycle makes a pulse, and a pulse has everything in it.
///
/// Three things fall out without being asked for:
///
///   THE ATTACK BENDS UP, because while the supply is still building the reed is driven weakly and
///   the loop settles flat, coming up to pitch over about a tenth of a second. Letting go does it in
///   reverse, and adds the growl at the end where the reed can no longer beat cleanly.
///
///   THE BELLS DO NOT START TOGETHER. They are spread along a manifold and the air reaches them in
///   order, so a five-chime swells into its chord instead of arriving in it.
///
///   IT IS BRIGHT COMING AND DULL GOING, because a horn mouth is a directional radiator: at the
///   fundamental it is barely directional and by the fourth harmonic it is a searchlight. That is
///   the "opening up" people hear from an approaching horn before Doppler has done anything at all.
/// </summary>
public sealed class ChimeHorn
{
    private readonly ChimeHornSpec _spec;
    private readonly float _rate;
    private readonly Bell[] _bells;
    private readonly float _refAmp;
    private float _valve;
    private float _lowGain = 1f, _highGain = 1f;
    private float _splitLp;
    private readonly float _splitAlpha;

    /// <summary>Whether the handle is pulled.</summary>
    public bool Blowing { get; set; }
    /// <summary>Output, pascals at one metre on the horn's axis, valid after Step().</summary>
    public float Out { get; private set; }
    /// <summary>How much air is reaching the manifold, 0..1.</summary>
    public float Valve => _valve;

    public ChimeHorn(ChimeHornSpec spec, float rate = 44100f, int seed = 11)
    {
        _spec = spec; _rate = rate;
        _bells = new Bell[spec.Bells.Length];
        for (int i = 0; i < _bells.Length; i++) _bells[i] = new Bell(spec, spec.Bells[i], rate, seed + i * 7);
        // Each bell is calibrated to unit RMS on its own, so a bank of them would be louder for
        // being many. The anchor is the whole horn, so divide by what they add up to.
        float sum = 0f;
        foreach (var b in spec.Bells) sum += MathF.Pow(10f, b.LevelTrimDb / 10f);
        _refAmp = 20e-6f * MathF.Pow(10f, spec.ReferenceDb / 20f) / MathF.Sqrt(MathF.Max(1e-3f, sum));
        float a = 0.5f * spec.Bells[0].MouthDiameterMetres;
        _splitAlpha = OnePole.AlphaFor(Math.Clamp(343f / (2f * MathF.PI * MathF.Max(0.02f, a)), 300f, 4000f), rate);
    }

    /// <summary>Where the listener is in the horn's frame: +z out of the mouths, +y up.</summary>
    public void SetListener(Vector3 hornFrame)
    {
        if (hornFrame.LengthSquared() < 1e-4f) return;
        float cos = Math.Clamp(Vector3.Dot(Vector3.Normalize(hornFrame), Vector3.UnitZ), -1f, 1f);
        float front = 0.5f * (1f + cos);
        _lowGain = 0.55f + 0.45f * front;
        _highGain = 0.10f + 0.90f * front * front * front;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Step()
    {
        float target = Blowing ? 1f : 0f;
        float tau = Blowing ? _spec.RiseSeconds : _spec.FallSeconds;
        _valve += (target - _valve) * MathF.Min(1f, 1f / MathF.Max(1e-4f, tau * _rate));

        float y = 0f;
        for (int i = 0; i < _bells.Length; i++) y += _bells[i].Step(_valve);

        _splitLp += _splitAlpha * (y - _splitLp);
        float low = _splitLp, high = y - _splitLp;
        Out = (low * _lowGain + high * _highGain) * _refAmp;
    }

    public System.Collections.Generic.IEnumerable<string> Describe()
    {
        yield return $"{_spec.Name}: {_spec.Bells.Length} bell(s), {_spec.SupplyKPa:F0} kPa, {_spec.ReferenceDb:F0} dB at 1 m on axis";
        for (int i = 0; i < _spec.Bells.Length; i++)
        {
            var b = _spec.Bells[i];
            yield return $"  bell {b.LengthMetres * 1000f:F0} mm -> design {b.Hz:F0} Hz, measured {_bells[i].MeasuredHz:F0} Hz, crest {_bells[i].CrestFactor:F1}, shut {_bells[i].ShutFraction * 100f:F0}%; "
                       + $"mouth {b.MouthDiameterMetres * 1000f:F0} mm -> flare cutoff {Bell.CutoffHz(b):F0} Hz, beams above {343f / (MathF.PI * b.MouthDiameterMetres):F0} Hz";
        }
    }

    /// <summary>
    /// One bell: a beating reed, locked to the column it is screwed to.
    ///
    /// A real diaphragm finds the column's note for itself — it is an outward-striking valve driven
    /// above its own resonance, and within a few cycles it is doing what the pipe wants rather than
    /// what the steel wants. This model LOCKS it there instead of letting it find it, and that is a
    /// deliberate simplification, marked as one. Entrainment is a fact about air horns, not a
    /// discovery to be made every time one is blown; modelling it as a fact costs nothing anybody
    /// can hear and buys an oscillator that cannot fail to start, cannot jump to the third harmonic
    /// when the supply is high, and cannot sit in a small-signal regime making a sine instead of a
    /// blast. (All three of those happened here first, which is why the note, the crest factor and
    /// the shut fraction are measured and printed.)
    ///
    /// Everything the ear actually uses is still a consequence of the parts: the NOTE is c/2L of
    /// this bell, the HARMONICS are a beating valve's pulse train through the column's resonances,
    /// the MISSING FUNDAMENTAL is the flare's cutoff, the BEND at each end is the pressure moving,
    /// and the CHORD is five lengths of brass.
    /// </summary>
    internal sealed class Bell
    {
        private readonly ChimeBellSpec _b;
        private readonly float _rate, _dt, _trim, _startDelay, _openScale, _leak;
        private Mode[] _modes;
        private readonly Random _rng;
        private readonly float _hpA;
        private double _phase;
        private float _hp1, _hp2;
        private double _t;
        private float _gain = 1f;
        private float _jitter;

        /// <summary>What the loop settled on, hertz.</summary>
        public float MeasuredHz { get; private set; }
        /// <summary>Peak to RMS. A beating valve makes a pulse and reads three or four; a reed that
        /// never shuts makes a sine and reads 1.4.</summary>
        public float CrestFactor { get; private set; }
        /// <summary>What fraction of each cycle the reed spends shut on its seat. A valve only makes
        /// a pulse if it shuts, and only a pulse has harmonics in it.</summary>
        public float ShutFraction { get; private set; }

        /// <summary>The flare's cutoff: below it an exponential horn hands the sound back to the
        /// reed instead of letting it out, which is why an air horn has so little fundamental in it
        /// and why the ear supplies the missing one and hears the chord an octave low.</summary>
        public static float CutoffHz(ChimeBellSpec b)
        {
            float areaRatio = MathF.Pow(b.MouthDiameterMetres / MathF.Max(0.004f, b.ThroatDiameterMetres), 2f);
            float m = MathF.Log(MathF.Max(1.2f, areaRatio)) / MathF.Max(0.05f, b.LengthMetres);
            return m * 343f / (4f * MathF.PI);
        }

        public Bell(ChimeHornSpec spec, ChimeBellSpec b, float rate, int seed)
        {
            _b = b; _rate = rate; _dt = 1f / rate; _rng = new Random(seed);
            _startDelay = b.StartDelaySeconds;
            // The chime's reed (0.46 open at full blow) is the reference the duty law was measured on.
            _openScale = Math.Clamp(spec.ReedOpenFraction, 0.05f, 0.95f) / 0.46f;
            // Air gets past a reed even while it is "shut" — it never quite seals on its seat — and
            // that leak is a share of how far the reed lifts. A stiff reed that lifts a third as far
            // leaks a third as much; holding the chime's leak constant under a shorter pulse would
            // add five decibels of air to a horn for no reason but the arithmetic.
            _leak = 0.25f * (1f - MathF.Cos(MathF.PI * 0.46f * _openScale)) / (1f - MathF.Cos(MathF.PI * 0.46f));
            _trim = MathF.Pow(10f, b.LevelTrimDb / 20f);
            _hpA = OnePole.AlphaFor(CutoffHz(b), rate);
            Build();
            Calibrate();
        }

        /// <summary>
        /// The column: every harmonic of c/2L, because a flaring horn behaves like a full cone and
        /// not like a cylinder with a lid on it. Bandpass sections, because a pipe's input impedance
        /// is resistive at its resonances. The higher peaks are lower and broader — the wall takes
        /// more out of them and the mouth lets more of them go — and that falling envelope is the
        /// difference between a horn and a buzzer.
        /// </summary>
        private void Build()
        {
            float f1 = _b.Hz;
            int n = Math.Max(1, (int)MathF.Min(16f, 0.45f * _rate / f1));
            _modes = new Mode[n];
            for (int k = 0; k < n; k++) _modes[k] = new Mode(f1 * (k + 1), 30f / (1f + 0.15f * k), _rate);
        }

        /// <summary>Blow it once in silence: measure what it settled on and how loud, and set the
        /// gain that makes the bank of bells add up to the spec's anchor.</summary>
        private void Calibrate()
        {
            int warm = (int)(0.4f * _rate), meas = (int)(0.25f * _rate);
            for (int i = 0; i < warm; i++) Step(1f);
            var buf = new float[meas];
            double sum = 0; float peak = 0f; int shut = 0;
            for (int i = 0; i < meas; i++)
            {
                float y = Step(1f);
                buf[i] = y; sum += y * (double)y; peak = MathF.Max(peak, MathF.Abs(y));
                if (Opening((float)_phase, 1f, _openScale) <= 0f) shut++;
            }
            ShutFraction = shut / (float)meas;
            float rms = (float)Math.Sqrt(sum / Math.Max(1, meas));
            MeasuredHz = DominantHz(buf, _rate, _b.Hz);
            CrestFactor = rms > 1e-9f ? peak / rms : 0f;
            _gain = rms > 1e-9f ? 1f / rms : 1f;
            Build();
            _hp1 = _hp2 = 0f; _phase = 0; _t = 0;
        }

        /// <summary>
        /// The frequency the horn actually settled on, by autocorrelation around the expected note.
        /// Counting zero crossings measures the noise in a signal and not the note in it, which is
        /// how the first version of this horn reported fourteen kilohertz while sounding like a hiss.
        /// </summary>
        internal static float DominantHz(float[] x, float rate, float expect)
        {
            int lo = Math.Max(2, (int)(rate / (expect * 3.2f)));
            int hi = Math.Min(x.Length / 2, (int)(rate / (expect * 0.32f)));
            double best = 0; int bestLag = 0;
            for (int lag = lo; lag <= hi; lag++)
            {
                double s = 0;
                for (int i = 0; i + lag < x.Length; i += 2) s += x[i] * (double)x[i + lag];
                if (s > best) { best = s; bestLag = lag; }
            }
            return bestLag > 0 ? rate / bestLag : 0f;
        }

        /// <summary>
        /// How far the reed is off its seat at this point in the cycle. It rests SHUT — the spring
        /// holds it down and the supply has to lift it — so it is open for rather less than half the
        /// cycle, and harder blowing holds it open longer. That is the pulse, and the pulse is where
        /// every harmonic above the first comes from.
        /// </summary>
        private static float Opening(float phase, float supply, float openScale)
        {
            float open = (0.30f + 0.16f * Math.Clamp(supply, 0f, 1f)) * openScale;     // duty cycle
            float x = MathF.Sin(MathF.Tau * phase) - MathF.Cos(MathF.PI * open);
            return x > 0f ? x : 0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public float Step(float valve)
        {
            _t += _dt;
            float supply = _t < _startDelay ? 0f : valve;
            if (supply < 1e-3f) return 0f;

            // The note rides the pressure. A reed's stiffness is what the air has to overcome, so a
            // horn on a line that has not come up yet plays flat and climbs into pitch — the bend at
            // the start of every blast, and its mirror at the end, where it also goes ragged because
            // the swing can no longer lift the reed cleanly off its seat.
            float f = _b.Hz * (0.935f + 0.065f * supply);
            _jitter += 0.002f * ((float)_rng.NextDouble() * 2f - 1f - _jitter);
            f *= 1f + _jitter * (supply < 0.5f ? 8f * (0.5f - supply) : 0.2f);

            _phase += f * _dt;
            if (_phase >= 1.0) _phase -= 1.0;

            float x = Opening((float)_phase, supply, _openScale);
            // Flow through the slit: the open area times the root of the pressure across it.
            float u = x * MathF.Sqrt(supply);
            // ...and the air tearing itself apart going through it. Most of the first few
            // milliseconds of a horn is this and nothing else.
            float hiss = (float)(_rng.NextDouble() * 2 - 1) * 0.09f * MathF.Sqrt(supply) * (_leak + x);

            float p = 0f;
            for (int k = 0; k < _modes.Length; k++) p += _modes[k].Process(u + hiss * 0.35f);

            // What leaves the mouth is what is in the column, less what the flare will not carry.
            _hp1 += _hpA * (p - _hp1);
            _hp2 += _hpA * (_hp1 - _hp2);
            return (p - _hp2 + hiss * 0.25f) * _trim * _gain;
        }
    }
}
