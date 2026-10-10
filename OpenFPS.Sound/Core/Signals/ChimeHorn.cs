using System.Numerics;
using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;

namespace OpenFPS.Client.AudioEngine.Core.Signals;

/// <summary>
/// An air horn, as the reed-and-column instrument it is.
///
/// A Nathan or Leslie chime is a steel diaphragm over a port with a tapered pipe screwed to it.
/// Reservoir air lifts the diaphragm, the rush drops the pressure behind it and it slams back, and
/// within a few milliseconds the column decides the note: five bells on five identical reeds play
/// five notes, each c/2L. The reed beats on its seat, and a valve shut for part of each cycle makes
/// a pulse with every harmonic in it. The column is bandpass modes at n·c/2L, all the harmonics (a
/// flaring horn behaves like a full cone), because a pipe's input impedance is resistive at its
/// resonances. The reed is locked to the column's note (see Bell).
///
/// The attack bends up slightly as the supply builds, and back down at the end; kept under 1 %
/// (ChimeHornSpec.PitchBend), or the harmonics leave the column's resonances. The bells start in
/// order along the manifold, so a five-chime swells into its chord. The mouth beams its upper
/// harmonics, so it is bright coming and dull going, before Doppler has done anything.
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

    public ChimeHorn(ChimeHornSpec spec, float rate = RenderRate.Default, int seed = 11)
    {
        _spec = spec; _rate = rate;
        _bells = new Bell[spec.Bells.Length];
        for (int i = 0; i < _bells.Length; i++) _bells[i] = new Bell(spec, spec.Bells[i], rate, seed + i * 7);
        // Each bell is unit RMS on its own; the anchor is the whole horn.
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
    /// A real diaphragm (an outward-striking valve driven above its own resonance) finds the column's
    /// note for itself; this one is locked there, a deliberate simplification. A free oscillator here
    /// failed to start, jumped to the third harmonic at high supply, or sat making a sine: that is why
    /// the note, the crest factor and the shut fraction are measured and printed. The note, the
    /// harmonics, the missing fundamental (the flare's cutoff), the bend and the chord still come
    /// from the parts.
    /// </summary>
    internal sealed class Bell
    {
        private readonly ChimeBellSpec _b;
        private readonly float _rate, _dt, _trim, _startDelay, _openScale, _leak, _bend, _ragged;
        private Mode[] _modes;
        private readonly Random _rng;
        private readonly float _hpA;
        private double _phase;
        private float _hp1, _hp2;
        private double _t;
        private float _gain = 1f;
        private float _jitter;
        private readonly float _jitterStep;   // 0.002 a sample at 44.1 kHz: 14 Hz
        private int _silentSamples = int.MaxValue;

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
            _b = b; _rate = rate; _dt = 1f / rate; _rng = new Random(seed); _jitterStep = At44k.Step(0.002f, rate);
            _startDelay = b.StartDelaySeconds;
            // The raggedness at low pressure is the same reed failing to lift cleanly: it scales with
            // the bend, 8 at a bend of 6.5 %.
            _bend = Math.Clamp(spec.PitchBend, 0f, 0.2f);
            _ragged = 8f * (_bend / 0.065f);
            // The chime's reed (0.46 open at full blow) is the reference the duty law was measured on.
            _openScale = Math.Clamp(spec.ReedOpenFraction, 0.05f, 0.95f) / 0.46f;
            // A shut reed never quite seals, and its leak is a share of how far it lifts: the chime's
            // leak held constant under a shorter pulse added five decibels of air.
            _leak = 0.25f * (1f - MathF.Cos(MathF.PI * 0.46f * _openScale)) / (1f - MathF.Cos(MathF.PI * 0.46f));
            _trim = MathF.Pow(10f, b.LevelTrimDb / 20f);
            _hpA = OnePole.AlphaFor(CutoffHz(b), rate);
            Build();
            Calibrate();
        }

        /// <summary>
        /// The column: bandpass sections at every harmonic of c/2L. The higher peaks are lower and
        /// broader (the wall takes more and the mouth lets more go): the difference between a horn
        /// and a buzzer.
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
        /// The frequency the horn settled on, by autocorrelation round the expected note. Zero crossings
        /// count the noise, not the note: they once read fourteen kilohertz off a horn that hissed.
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
        /// How far the reed is off its seat at this point in the cycle. It rests shut, so it is open
        /// for rather less than half the cycle, and harder blowing holds it open longer: the pulse.
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
            // The start delay restarts whenever the manifold empties, so every blast swells into its
            // chord, and the reed starts each from rest.
            if (valve < 1e-3f) { _t = 0.0; _phase = 0.0; _jitter = 0f; }
            else _t += _dt;
            float supply = _t < _startDelay ? 0f : valve;
            if (supply < 1e-3f) return RingOut();

            // The note rides the pressure: flat until the line comes up, and flat and ragged again at
            // the end, where the swing can no longer lift the reed cleanly.
            float f = _b.Hz * ((1f - _bend) + _bend * supply);
            _jitter += _jitterStep * ((float)_rng.NextDouble() * 2f - 1f - _jitter);
            f *= 1f + _jitter * (supply < 0.5f ? _ragged * (0.5f - supply) : 0.2f);

            _phase += f * _dt;
            if (_phase >= 1.0) _phase -= 1.0;

            float x = Opening((float)_phase, supply, _openScale);
            // Flow through the slit: the open area times the root of the pressure across it.
            float u = x * MathF.Sqrt(supply);
            // ...and the turbulence in it: most of a horn's first few milliseconds.
            float hiss = (float)(_rng.NextDouble() * 2 - 1) * 0.09f * MathF.Sqrt(supply) * (_leak + x);

            float p = 0f;
            for (int k = 0; k < _modes.Length; k++) p += _modes[k].Process(u + hiss * 0.35f);

            // What leaves the mouth is what is in the column, less what the flare will not carry.
            _hp1 += _hpA * (p - _hp1);
            _hp2 += _hpA * (_hp1 - _hp2);
            _silentSamples = 0;
            return (p - _hp2 + hiss * 0.25f) * _trim * _gain;
        }

        /// <summary>
        /// The air has stopped but the column rings down on its resonances, a few tens of milliseconds.
        /// Cut off when the supply ran out, the horn stopped dead 35 dB down, heard as a cut. Left
        /// alone once under a millionth of full scale for a whole cycle.
        /// </summary>
        private float RingOut()
        {
            int period = (int)(_rate / _b.Hz) + 1;
            if (_silentSamples > period) return 0f;
            float p = 0f;
            for (int k = 0; k < _modes.Length; k++) p += _modes[k].Process(0f);
            _hp1 += _hpA * (p - _hp1);
            _hp2 += _hpA * (_hp1 - _hp2);
            float y = (p - _hp2) * _trim * _gain;
            _silentSamples = MathF.Abs(y) < 1e-6f ? _silentSamples + 1 : 0;
            return y;
        }
    }
}
