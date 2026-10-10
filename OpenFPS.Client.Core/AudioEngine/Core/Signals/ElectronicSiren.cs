using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;   // OnePole

namespace OpenFPS.Client.AudioEngine.Core.Signals;

/// <summary>
/// A police siren, from the chain that makes one: an oscillator, a compression driver pushed past
/// linear, and a horn that passes neither the bottom nor the top. Wail, yelp and phaser are one
/// oscillator sweeping the same range at three rates, as the hardware does, so switching mid-sweep
/// is continuous in phase and frequency. Output is pascals at one metre on the horn's axis.
/// </summary>
public sealed class ElectronicSiren
{
    public readonly SirenSpec Spec;
    private readonly float _rate, _dt;

    /// <summary>What the head is doing. Changing it does not restart anything.</summary>
    public SirenMode Mode { get; set; } = SirenMode.Off;

    /// <summary>The horn's beam toward the listener, for the body of the band and for the top that
    /// it beams narrowly. See SetListener.</summary>
    private float _axisGain = 1f;
    private float _highGain = 1f;
    private float _splitLp, _splitA;

    /// <summary>Pascals at one metre on axis, valid after Step().</summary>
    public float Output { get; private set; }
    /// <summary>The oscillator's instantaneous frequency, Hz.</summary>
    public float Hz { get; private set; }

    private double _phase;        // the oscillator's phase, 0..1
    private double _sweep;        // position within the sweep, 0..1
    private float _amp;           // the reference amplitude, pascals
    private float _gate;          // on/off envelope: an amplifier does not start instantly
    private bool _hiHalf;
    private float _hp1, _hp2, _lp1, _lp2, _hpA, _lpA;

    public ElectronicSiren(SirenSpec spec, float rate = RenderRate.Default) : this(spec, rate, measuring: false) { }

    private ElectronicSiren(SirenSpec spec, float rate, bool measuring)
    {
        Spec = spec;
        _rate = rate;
        _dt = 1f / rate;
        // A measuring probe runs at unit amplitude: it finds out what the chain does, not how loud
        // the head is.
        _amp = measuring ? 1f : 20e-6f * MathF.Pow(10f, spec.SourceLevelDb / 20f);
        _gate = measuring ? 1f : 0f;
        // Two poles each side: a horn's cutoff is a good deal steeper than one.
        _hpA = OnePole.AlphaFor(spec.FlareCutoffHz, rate);
        _lpA = OnePole.AlphaFor(spec.DriverTopHz, rate);
        // Where the beam starts to narrow: over the cutoff, where ka passes about four and the lobe
        // stops being a hemisphere.
        _splitA = OnePole.AlphaFor(MathF.Min(spec.DriverTopHz * 0.8f, spec.FlareCutoffHz * 3f), rate);

        // What the horn costs, measured once across the real sweep (as BladeRow does): the declared
        // level is a type-approved legal figure, and the flare cutoff a quarter octave under the sweep
        // already eats into the fundamental. Uncompensated, the patrol head measured 125.8 dB against
        // the 130 it declares.
        if (!measuring) _amp /= MathF.Max(1e-6f, MeasureChainRms(spec, rate));
    }

    /// <summary>
    /// The RMS the oscillator and horn produce for a unit reference amplitude, averaged across the
    /// whole sweep. Run once per head at construction; a tenth of a second of arithmetic.
    /// </summary>
    private static float MeasureChainRms(SirenSpec spec, float rate)
    {
        var probe = new ElectronicSiren(spec, rate, measuring: true) { Mode = SirenMode.Wail };
        int n = (int)(MathF.Max(0.1f, spec.WailSeconds) * rate);
        for (int i = 0; i < rate / 20; i++) probe.Step();       // settle the gate and the filters
        double sum = 0;
        for (int i = 0; i < n; i++) { probe.Step(); sum += (double)probe.Output * probe.Output; }
        return MathF.Sqrt((float)(sum / n));
    }

    /// <summary>
    /// Where the listener is, in the car's frame: +z is the way the car points.
    ///
    /// The beam is set by ka: at the flare cutoff the mouth is barely a wavelength round and radiates
    /// almost everywhere, at four kilohertz nearly ten and throws a narrow forward lobe. So a siren
    /// coming toward you is bright and hard, and the instant it passes the top falls away. One gain
    /// per band: a gentle front-to-back ratio for the body, the front factor cubed for the top. One
    /// gain for both gave a pass-by that only got quieter, heard as moving away rather than turning.
    /// </summary>
    public void SetListener(System.Numerics.Vector3 carFrame)
    {
        if (carFrame.LengthSquared() < 1e-4f) return;
        float fwd = System.Numerics.Vector3.Normalize(carFrame).Z;
        float front = 0.5f + 0.5f * fwd;                  // 1 dead ahead, 0 dead astern
        _axisGain = 0.45f + 0.55f * front;
        _highGain = 0.06f + 0.94f * front * front * front;
    }

    public void Step()
    {
        // The amplifier comes up in a few tens of milliseconds: instant to the ear, but no click.
        float want = Mode == SirenMode.Off ? 0f : 1f;
        _gate += Math.Clamp(want - _gate, -_dt / 0.04f, _dt / 0.03f);
        if (_gate <= 1e-5f && want == 0f) { Output = 0f; return; }

        float f;
        if (Mode == SirenMode.HiLo)
        {
            // Two fixed notes, alternating: the step is the whole character of a European siren.
            _sweep += _dt / MathF.Max(0.05f, Spec.HiLoHoldSeconds);
            if (_sweep >= 1.0) { _sweep -= 1.0; _hiHalf = !_hiHalf; }
            f = _hiHalf ? Spec.HiLoLowHz * Spec.HiLoRatio : Spec.HiLoLowHz;
        }
        else
        {
            float period = MathF.Max(0.02f, Spec.PeriodFor(Mode == SirenMode.Off ? SirenMode.Wail : Mode));
            _sweep += _dt / period;
            if (_sweep >= 1.0) _sweep -= 1.0;
            // Up fast, down slow, 40/60: the electromechanical rotor spun up and coasted down, and
            // the electronic heads kept the asymmetry because that is what people recognise.
            float x = (float)_sweep;
            float t = x < 0.4f ? x / 0.4f : 1f - (x - 0.4f) / 0.6f;
            // Smooth at both turning points: a corner clicks once a sweep, a buzz at ten a second.
            t = 0.5f - 0.5f * MathF.Cos(MathF.PI * t);
            // Swept in log frequency: linear hertz spends most of the sweep up top.
            f = Spec.SweepLowHz * MathF.Pow(Spec.SweepHighHz / Spec.SweepLowHz, t);
        }
        Hz = f;

        _phase += f * _dt;
        if (_phase >= 1.0) _phase -= 1.0;

        // ── The oscillator, summed harmonic by harmonic ────────────────────────────────────────
        // Its harmonics are what carry a siren through traffic. They are summed directly, and only
        // those under the driver's top (three to seven at 650-1450 Hz against 4.5 kHz). A shape
        // through a waveshaper aliased: the folded partials moved down as the real ones moved up,
        // and the phaser was heard "stepping, not sweeping". Compression is a flatter rolloff, the
        // series falling toward 1/n^0.4 at the limit as a squashed diaphragm pushes energy up it.
        float c = Math.Clamp(Spec.Compression, 0f, 0.95f);
        float rolloff = 1f - 0.6f * c;
        float top = MathF.Min(Spec.DriverTopHz * 1.5f, _rate * 0.45f);
        int n = Math.Clamp((int)(top / MathF.Max(1f, f)), 1, 24);

        // A rectangular wave of duty d: a_k = sin(π k d) / (π k), odd harmonics only at d = 0.5, as
        // the rotary chopper these heads imitate makes. A duty a little off a half puts some of the
        // even set back, as a real machined rotor does. See SirenSpec.Duty.
        float d = Math.Clamp(Spec.Duty, 0.05f, 0.95f);
        float saw = 0f, norm = 0f;
        for (int k = 1; k <= n; k++)
        {
            float a = MathF.Sin(MathF.PI * k * d) / (MathF.PI * k);
            a *= MathF.Pow(k, 1f - rolloff);   // compression
            saw += a * MathF.Sin((float)(_phase * k * 2.0 * Math.PI));
            norm += a * a;
        }
        // Unit RMS however many partials survive, so the level does not jump as one leaves the sum.
        saw *= norm > 1e-9f ? 0.7071f / MathF.Sqrt(norm * 0.5f) : 0f;

        // The horn: below the flare cutoff the mouth cannot couple to the air, above the driver's top
        // the diaphragm's mass takes over (SirenSpec.FlareCutoffHz).
        _hp1 += _hpA * (saw - _hp1);
        _hp2 += _hpA * (_hp1 - _hp2);
        float y = saw - _hp2;
        _lp1 += _lpA * (y - _lp1);
        _lp2 += _lpA * (_lp1 - _lp2);

        _splitLp += _splitA * (_lp2 - _splitLp);
        float high = _lp2 - _splitLp;
        Output = (_splitLp * _axisGain + high * _highGain) * _amp * _gate;
    }

    public System.Collections.Generic.IEnumerable<string> Describe()
    {
        yield return $"{Spec.Name}: {Spec.ReferenceDbAt3m:F0} dB at 10 ft = {Spec.SourceLevelDb:F0} dB at 1 m";
        yield return $"horn: {Spec.HornMouthMetres * 1000f:F0} mm mouth -> flare cutoff {Spec.FlareCutoffHz:F0} Hz; "
                   + $"driver to {Spec.DriverTopHz:F0} Hz; compression {Spec.Compression:F2}";
        yield return $"sweep {Spec.SweepLowHz:F0}-{Spec.SweepHighHz:F0} Hz: "
                   + $"wail {Spec.WailSeconds:F1} s ({60f / Spec.WailSeconds:F0}/min), "
                   + $"yelp {Spec.YelpSeconds:F2} s ({60f / Spec.YelpSeconds:F0}/min), "
                   + $"phaser {Spec.PhaserSeconds:F2} s ({60f / Spec.PhaserSeconds:F0}/min)";
        yield return $"hi-lo: {Spec.HiLoLowHz:F0} / {Spec.HiLoLowHz * Spec.HiLoRatio:F0} Hz, "
                   + $"{Spec.HiLoHoldSeconds:F2} s each";
    }
}
