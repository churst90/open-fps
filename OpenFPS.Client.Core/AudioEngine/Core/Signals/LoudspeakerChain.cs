using OpenFPS.Common;
using OpenFPS.Common.Hearing;

namespace OpenFPS.Client.AudioEngine.Core.Signals;

/// <summary>A program rendered through a loudspeaker: the buffer, the level it declares, and how it radiates.</summary>
public sealed class LoudspeakerRender
{
    public required string Key { get; init; }
    public required LoudspeakerSpec Spec { get; init; }
    /// <summary>The on-axis pressure at one metre, scaled so its gated RMS is
    /// <see cref="Speech.BufferRmsDbfs"/>, at <see cref="Rate"/>.</summary>
    public required float[] Pcm { get; init; }
    public required int Rate { get; init; }
    /// <summary>The level the buffer's full scale stands for at one metre on the axis, dB: what the
    /// loudness law places it by, as a speech line's (Speech.LevelDb).</summary>
    public required float LevelDb { get; init; }
    /// <summary>The program's gated RMS at one metre on the axis, dB SPL: the speaker's speech level.</summary>
    public required float SplDb { get; init; }
    /// <summary>The loudest sample at one metre on the axis, dB SPL (peak).</summary>
    public required float PeakSplDb { get; init; }
    /// <summary>The program's energy in each of the radiator's bands (Radiator.BandHz), on the axis.</summary>
    public required float[] BandEnergy { get; init; }
    public required Radiator Radiator { get; init; }
    /// <summary>The share of the on-axis pressure the room is fed with: the radiated power, weighted by the program.</summary>
    public required float RadiatedGain { get; init; }
    /// <summary>The share of the amplifier's samples past its clip knee.</summary>
    public required float ClippedShare { get; init; }
    /// <summary>The furthest the diaphragm went, as a share of its travel.</summary>
    public required float MaxExcursion { get; init; }
    /// <summary>How far the compressor turned the program down at most, dB.</summary>
    public required float MaxLimitDb { get; init; }
}

/// <summary>
/// A recording played through a loudspeaker (LoudspeakerSpec), rendered once: the program into the
/// amplifier, which compresses and clips at its rail (and sags on a battery), into a driver that is a
/// mass on a spring under the horn's load, with a travel limit (a suspension that stiffens, a force
/// factor that falls as the coil leaves the gap) and a coil that heats, out through a horn whose mouth
/// will not couple below its cutoff and reflects part of the wave back down its folded path.
///
/// The output is the pressure at one metre on the axis. Its directivity is not in it: that is applied
/// live per listener (Radiator, RadiatorState), since the listener moves and the program does not.
///
/// The nonlinear stages run at four times the output rate, so the clip's and the suspension's harmonics
/// do not fold back into the band before the decimator takes them off. A program is rendered once per
/// speaker, on a worker (LoudspeakerVoices), in well under a second.
///
/// Calibration: the driver's drive is set so a rated-power sine at the horn's cutoff moves the diaphragm
/// to <see cref="LoudspeakerSpec.ExcursionHeadroomDb"/> under its travel; the output so one watt gives
/// <see cref="LoudspeakerSpec.SensitivityDb"/> averaged over the datasheet's band, measured on the
/// small-signal chain. Neither is a fitted gain.
/// </summary>
public static class LoudspeakerChain
{
    public const int Oversample = 4;
    private const double RefPascals = 20e-6;
    /// <summary>Copper's temperature coefficient of resistance, per kelvin (aluminium's is 0.0039 too).</summary>
    private const double CopperAlpha = 0.00393;

    /// <summary>
    /// Renders <paramref name="program"/> (any rate, mono) through <paramref name="spec"/>. A loop is
    /// rendered twice and its second pass kept, so the compressor, the coil and the horn are in the state
    /// the end of the loop leaves them in when it starts again.
    /// </summary>
    public static LoudspeakerRender Render(string key, LoudspeakerSpec spec, float[] program, int programRate, int outRate, bool loop = false)
    {
        float[] pascals = RenderPascals(spec, program, programRate, outRate, loop, out var stats);
        float gated = pascals.Length > 0 ? Timbre.GatedRms(pascals, outRate) : float.NaN;
        if (!float.IsFinite(gated)) gated = -200f;
        float spl = gated - 20f * MathF.Log10((float)RefPascals);
        float peak = 0f;
        foreach (float p in pascals) peak = MathF.Max(peak, MathF.Abs(p));
        float peakSpl = 20f * MathF.Log10(MathF.Max(1e-12f, peak) / (float)RefPascals);

        // As a speech line is kept: its gated RMS at -28 dBFS, its full scale declared.
        float scale = MathF.Pow(10f, (Speech.BufferRmsDbfs - gated) / 20f);
        if (peak * scale > 0.99f) scale = 0.99f / MathF.Max(1e-12f, peak);
        var pcm = new float[pascals.Length];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = pascals[i] * scale;
        float levelDb = 20f * MathF.Log10(1f / (scale * (float)RefPascals));

        var energy = BandEnergy(pascals, outRate);
        var radiator = Radiator.For(spec);
        return new LoudspeakerRender
        {
            Key = key, Spec = spec, Pcm = pcm, Rate = outRate,
            LevelDb = levelDb, SplDb = spl, PeakSplDb = peakSpl,
            BandEnergy = energy, Radiator = radiator, RadiatedGain = radiator.RadiatedGain(energy),
            ClippedShare = stats.ClippedShare, MaxExcursion = stats.MaxExcursion, MaxLimitDb = stats.MaxLimitDb,
        };
    }

    public readonly record struct Stats(float ClippedShare, float MaxExcursion, float MaxLimitDb);

    /// <summary>The on-axis pressure at one metre, pascals, at <paramref name="outRate"/>.</summary>
    public static float[] RenderPascals(LoudspeakerSpec spec, float[] program, int programRate, int outRate, bool loop, out Stats stats)
    {
        stats = default;
        if (program.Length == 0 || programRate <= 0 || outRate <= 0) return Array.Empty<float>();
        int rate = outRate * Oversample;

        // The program as it reaches the compressor: its peaks (the 99.9th percentile, so one stray
        // sample does not set them) at unity.
        double peak = RobustPeak(program);
        double gain = peak > 1e-9 ? 1.0 / peak : 0.0;

        float[] input = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.Resample(program, programRate, rate);
        int n = input.Length;
        var chain = new Chain(spec, rate);
        var y = new float[n];
        int passes = loop ? 2 : 1;
        for (int pass = 0; pass < passes; pass++)
        {
            bool keep = pass == passes - 1;
            if (keep) chain.ResetStats();
            for (int i = 0; i < n; i++)
            {
                double p = chain.Step(input[i] * gain);
                if (keep) y[i] = (float)p;
            }
        }
        stats = new Stats(chain.Clipped / (float)Math.Max(1, n), (float)chain.MaxX, (float)chain.MaxLimitDb);
        return OpenFPS.Client.AudioEngine.Fmod.MixerQuality.Resample(y, rate, outRate);
    }

    /// <summary>
    /// The small-signal response, RMS pascals at one metre on the axis per RMS volt at the voice coil,
    /// at each frequency: the chain with its nonlinear terms and its heating off. For tests and the lab.
    /// </summary>
    public static double[] Response(LoudspeakerSpec spec, int outRate, ReadOnlySpan<double> hz)
        => new Chain(spec, outRate * Oversample).Response(hz);

    /// <summary>The on-axis SPL at one metre a sine at this frequency and power makes, small-signal.</summary>
    public static double SineSplDb(LoudspeakerSpec spec, int outRate, double hz, double watts)
    {
        double h = Response(spec, outRate, new[] { hz })[0];
        return 20.0 * Math.Log10(Math.Max(1e-15, h * Math.Sqrt(watts * spec.LoadOhms)) / RefPascals);
    }

    /// <summary>The program's energy in each of the radiator's six bands, split as the mixer splits them
    /// (RadiatorBands).</summary>
    public static float[] BandEnergy(float[] x, int rate)
    {
        var split = new OpenFPS.Client.AudioEngine.Fmod.RadiatorBands();
        split.Configure(rate);
        var e = new double[Radiator.Bands];
        Span<float> band = stackalloc float[Radiator.Bands];
        foreach (float s in x)
        {
            split.Split(s, band);
            for (int k = 0; k < Radiator.Bands; k++) e[k] += (double)band[k] * band[k];
        }
        var f = new float[e.Length];
        for (int k = 0; k < e.Length; k++) f[k] = (float)e[k];
        return f;
    }

    private static double RobustPeak(float[] x)
    {
        var a = new float[x.Length];
        for (int i = 0; i < a.Length; i++) a[i] = MathF.Abs(x[i]);
        Array.Sort(a);
        return a[Math.Clamp((int)(a.Length * 0.999), 0, a.Length - 1)];
    }

    /// <summary>The amplifier and the coil's heating, in front of the driver and horn.</summary>
    private sealed class Chain
    {
        private readonly LoudspeakerSpec _s;
        private readonly double _dt, _vClip, _r, _sagShare, _knee;
        private readonly double _aAtt, _aRel, _aSag, _thr, _slope, _makeup, _drive;
        private readonly bool _lowCutOn;
        private readonly Biquad _lowCut;
        private readonly Acoustic _acoustic;
        private readonly int _rate;
        /// <summary>Volts at the coil to the driver's drive: x = 1 is the diaphragm's travel.</summary>
        private readonly double _ke;

        private double _env, _pAvg, _rise;
        public int Clipped;
        public double MaxLimitDb;
        public double MaxX => _acoustic.MaxX;

        public Chain(LoudspeakerSpec s, int rate)
        {
            _s = s;
            _rate = rate;
            _dt = 1.0 / rate;
            _r = Math.Max(0.5, s.LoadOhms);
            _vClip = Math.Sqrt(2.0 * Math.Max(0.01, s.MaxWatts) * _r);
            // The share of the rail lost across the supply's own resistance at full current.
            _sagShare = s.SupplyOhms / (s.SupplyOhms + _r);
            _knee = Math.Clamp(s.ClipKnee, 0.5, 1.0);

            _lowCutOn = s.LowCutHz > 0f;
            _lowCut = Biquad.HighPass(Math.Max(1.0, s.LowCutHz), 0.7071, rate);
            _thr = s.CompressorThresholdDb;
            _slope = 1.0 - 1.0 / Math.Max(1.0, s.CompressorRatio);
            _aAtt = 1.0 - Math.Exp(-_dt / (Math.Max(0.05, s.CompressorAttackMs) * 1e-3));
            _aRel = 1.0 - Math.Exp(-_dt / (Math.Max(5.0, s.CompressorReleaseMs) * 1e-3));
            // The make-up gain: a peak at unity comes out at unity.
            _makeup = Math.Pow(10.0, _slope * Math.Max(0.0, -_thr) / 20.0);
            // The volume control: those peaks PeakOverClipDb past the clip.
            _drive = _vClip * Math.Pow(10.0, s.PeakOverClipDb / 20.0);
            // A battery's reservoir and chemistry follow the load over tens of milliseconds.
            _aSag = 1.0 - Math.Exp(-_dt / 0.03);

            // The drive: a rated-power sine at the horn's cutoff takes the diaphragm to the headroom
            // under its travel, from the small-signal displacement x/e = 1 / |1 - r² + j r / Q|.
            double vRated = Math.Sqrt(2.0 * Math.Max(0.01, s.RatedWatts) * _r);
            double ratio = s.FlareCutoffHz / s.DriverResonanceHz, q = Math.Max(0.05, s.DriverQ);
            double xPerE = 1.0 / Math.Sqrt(Math.Pow(1.0 - ratio * ratio, 2) + Math.Pow(ratio / q, 2));
            _ke = Math.Pow(10.0, -s.ExcursionHeadroomDb / 20.0) / (vRated * xPerE);

            // The output: one watt gives the sensitivity, averaged in power over the datasheet's band.
            var probe = new Acoustic(s, rate, 1.0);
            var band = Spaced(s.SensitivityLowHz, s.SensitivityHighHz);
            var h = probe.Response(band, rate);
            double mean = 0;
            foreach (double m in h) mean += m * m;
            mean /= Math.Max(1, h.Length);
            double perVolt = Math.Sqrt(mean) * _ke;                                   // RMS Pa per RMS volt
            double want = RefPascals * Math.Pow(10.0, s.SensitivityDb / 20.0);       // RMS Pa for 1 W
            double gOut = perVolt > 0 ? want / (perVolt * Math.Sqrt(_r)) : 0.0;
            _acoustic = new Acoustic(s, rate, gOut);
        }

        /// <summary>Frequencies a twelfth of an octave apart across a band.</summary>
        private static double[] Spaced(double lo, double hi)
        {
            var list = new List<double>();
            for (double f = lo; f <= hi * 1.0001; f *= Math.Pow(2.0, 1.0 / 12.0)) list.Add(f);
            return list.ToArray();
        }

        public double[] Response(ReadOnlySpan<double> hz)
        {
            var h = new Acoustic(_s, _rate, _acoustic.Gain).Response(hz, _rate);
            for (int i = 0; i < h.Length; i++) h[i] *= _ke;
            return h;
        }

        public void ResetStats() { Clipped = 0; MaxLimitDb = 0; _acoustic.MaxX = 0; }

        /// <summary>One sample: the program (its peaks at unity) in, pascals at a metre out.</summary>
        public double Step(double u)
        {
            if (_lowCutOn) u = _lowCut.Process(u);

            // The voice compressor: a peak follower, the gain over its threshold squeezed by the ratio,
            // and made up again.
            double level = Math.Abs(u);
            _env += (level > _env ? _aAtt : _aRel) * (level - _env);
            double overDb = 20.0 * Math.Log10(Math.Max(1e-12, _env)) - _thr;
            double g = _makeup;
            if (overDb > 0 && _slope > 0)
            {
                double cut = _slope * overDb;
                g *= Math.Pow(10.0, -cut / 20.0);
                if (cut > MaxLimitDb) MaxLimitDb = cut;
            }
            double v = u * g * _drive;

            // The rail, sagging across the supply's resistance with the load's recent power.
            double rail = _vClip * (1.0 - _sagShare * Math.Min(1.0, _pAvg / Math.Max(1e-6, _s.MaxWatts)));
            double x = v / rail, ax = Math.Abs(x);
            if (ax > _knee)
            {
                Clipped++;
                double over = (ax - _knee) / (1.0 - _knee + 1e-9);
                x = Math.Sign(x) * (_knee + (1.0 - _knee) * Math.Tanh(over));
            }
            v = x * rail;
            double watts = v * v / _r;
            _pAvg += _aSag * (watts - _pAvg);

            // The coil heats with the power in it; its resistance rises and the current falls.
            _rise += _dt / Math.Max(0.1, _s.CoilSeconds) * (watts / Math.Max(0.01, _s.RatedWatts) * _s.CoilRiseAtRatedK - _rise);
            double current = 1.0 / (1.0 + CopperAlpha * _rise);

            return _acoustic.Step(v * _ke * current, linear: false);
        }
    }

    /// <summary>The driver, the coil's and diaphragm's poles, the horn's mouth and its fold. Double
    /// throughout: a biquad at 500 Hz at 192 kHz has poles too near the unit circle for single precision.</summary>
    private sealed class Acoustic
    {
        private readonly LoudspeakerSpec _s;
        private readonly double _dt, _w0, _q, _hard, _bLoss, _aCoil, _aDiaphragm, _aReflect;
        private readonly bool _diaphragmOn, _foldOn;
        private readonly Biquad _hornHp, _fold;
        private readonly double[] _delay;
        private int _at;
        private double _x, _v, _coil, _diaphragm, _reflect;
        public readonly double Gain;
        public double MaxX;

        public Acoustic(LoudspeakerSpec s, int rate, double gain)
        {
            _s = s;
            _dt = 1.0 / rate;
            Gain = gain;
            _w0 = 2.0 * Math.PI * s.DriverResonanceHz;
            _q = Math.Max(0.05, s.DriverQ);
            _hard = Math.Max(0.0, s.SuspensionHardening);
            double loss = Math.Clamp(s.ForceFactorLoss, 0.0, 0.9);
            _bLoss = loss / (1.0 - loss);                // beta(1) = 1 - loss
            _aCoil = OnePole(s.CoilTopHz, rate);
            _diaphragmOn = s.DiaphragmTopHz > 0f;
            _aDiaphragm = OnePole(Math.Max(1.0, s.DiaphragmTopHz), rate);
            double fc = s.FlareCutoffHz;
            _hornHp = Biquad.HighPass(fc, 0.7071, rate);
            // Only what is long against the mouth is sent back: the reflection falls away over twice the cutoff.
            _aReflect = OnePole(2.0 * fc, rate);
            _delay = new double[Math.Max(1, (int)Math.Round(2.0 * Math.Max(0.0, s.PathMetres) / LoudspeakerSpec.SpeedOfSound * rate))];
            _foldOn = s.FoldResonanceHz > 0f && s.FoldResonanceDb > 0f;
            _fold = Biquad.Peak(Math.Max(20.0, s.FoldResonanceHz), Math.Max(0.3, s.FoldResonanceQ), s.FoldResonanceDb, rate);
        }

        private static double OnePole(double hz, int rate) => 1.0 - Math.Exp(-2.0 * Math.PI * Math.Min(hz, rate * 0.45) / rate);

        public double Step(double e, bool linear)
        {
            // A mass on a spring under the horn's resistive load, x in units of its travel:
            // x'' = w0² (beta(x) e - x (1 + h x²)) - (w0 / Q) x'. Semi-implicit Euler, stable and close at
            // w0 dt under 0.1.
            double x = _x;
            double beta = linear ? 1.0 : 1.0 / (1.0 + _bLoss * x * x);
            double spring = linear ? x : x * (1.0 + _hard * x * x);
            double acc = _w0 * _w0 * (beta * e - spring) - _w0 / _q * _v;
            _v += acc * _dt;
            _x += _v * _dt;
            if (!double.IsFinite(_x) || Math.Abs(_x) > 50.0) { _x = 0; _v = 0; }
            if (Math.Abs(_x) > MaxX) MaxX = Math.Abs(_x);

            // Under the horn's load the pressure follows the diaphragm's velocity.
            double y = _v / _w0;
            _coil += _aCoil * (y - _coil);
            y = _coil;
            if (_diaphragmOn) { _diaphragm += _aDiaphragm * (y - _diaphragm); y = _diaphragm; }

            // The mouth: no coupling below its cutoff, and near it part of the wave goes back down the
            // folded path and returns 2L/c later: the horn's ripple.
            y = _hornHp.Process(y);
            _reflect += _aReflect * (_delay[_at] - _reflect);
            double outp = y + _s.MouthReflection * _reflect;
            _delay[_at] = outp;
            if (++_at >= _delay.Length) _at = 0;

            if (_foldOn) outp = _fold.Process(outp);
            return outp * Gain;
        }

        /// <summary>|H| at each frequency per unit of drive: a unit sample through the linear path and
        /// its DFT. Leaves this path's state used: call on a fresh one.</summary>
        public double[] Response(ReadOnlySpan<double> hz, int rate)
        {
            int len = rate / 5;
            var ir = new double[len];
            for (int i = 0; i < len; i++) ir[i] = Step(i == 0 ? 1.0 : 0.0, linear: true);
            var mag = new double[hz.Length];
            for (int k = 0; k < hz.Length; k++)
            {
                double w = 2.0 * Math.PI * hz[k] / rate, re = 0, im = 0;
                for (int i = 0; i < len; i++) { re += ir[i] * Math.Cos(w * i); im -= ir[i] * Math.Sin(w * i); }
                mag[k] = Math.Sqrt(re * re + im * im);
            }
            return mag;
        }
    }

    /// <summary>A direct-form-I biquad in double precision (RBJ cookbook coefficients).</summary>
    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
        }

        public static Biquad HighPass(double hz, double q, int rate)
        {
            double w = 2.0 * Math.PI * Math.Min(hz, rate * 0.45) / rate, c = Math.Cos(w), al = Math.Sin(w) / (2.0 * q);
            return new Biquad((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al);
        }

        public static Biquad Peak(double hz, double q, double db, int rate)
        {
            double a = Math.Pow(10.0, db / 40.0);
            double w = 2.0 * Math.PI * Math.Min(hz, rate * 0.45) / rate, c = Math.Cos(w), al = Math.Sin(w) / (2.0 * q);
            return new Biquad(1 + al * a, -2 * c, 1 - al * a, 1 + al / a, -2 * c, 1 - al / a);
        }

        public double Process(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
    }
}
