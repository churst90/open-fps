using System;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;   // OnePole

namespace OpenFPS.Client.AudioEngine.Core.Signals;

/// <summary>
/// A police siren, integrated from the chain that makes one: an oscillator, a compression driver
/// pushed past linear, and a horn that will not pass the bottom or the top.
///
/// Nothing in here is a recording and nothing is a loop. The three sounds on a siren head — wail,
/// yelp, phaser — are ONE oscillator sweeping the same range at three different rates, which is
/// what the hardware does, so they cannot drift apart in timbre the way three sample files would.
/// Switching between them mid-sweep is continuous in phase and in frequency, which is what a real
/// head does too: the sound does not restart, it just starts sweeping faster.
///
/// Output is pascals at one metre on the horn's axis. Distance, Doppler and the air are the
/// renderer's business, as everywhere else.
/// </summary>
public sealed class ElectronicSiren
{
    public readonly SirenSpec Spec;
    private readonly float _rate, _dt;

    /// <summary>What the head is doing. Changing it does not restart anything.</summary>
    public SirenMode Mode { get; set; } = SirenMode.Off;

    /// <summary>Where the listener is in the car's frame, for the horn's beam. Set by the voice.</summary>
    private float _axisGain = 1f;
    /// <summary>The same, for the part of the band the horn actually beams. See SetListener.</summary>
    private float _highGain = 1f;
    private float _splitLp, _splitA;

    /// <summary>Pascals at one metre on axis, valid after Step().</summary>
    public float Output { get; private set; }
    /// <summary>The oscillator's instantaneous frequency, Hz. Diagnostic.</summary>
    public float Hz { get; private set; }

    private double _phase;        // the sawtooth's own phase, 0..1
    private double _sweep;        // position within the sweep, 0..1
    private float _amp;           // the reference amplitude, pascals
    private float _gate;          // on/off envelope: an amplifier does not start instantly
    private bool _hiHalf;
    private float _hp1, _hp2, _lp1, _lp2, _hpA, _lpA;

    public ElectronicSiren(SirenSpec spec, float rate = 44100f) : this(spec, rate, measuring: false) { }

    private ElectronicSiren(SirenSpec spec, float rate, bool measuring)
    {
        Spec = spec;
        _rate = rate;
        _dt = 1f / rate;
        // The spec figure is measured at ten feet; the model works at one metre. A measuring probe
        // runs at unit amplitude — it is finding out what the chain does, not how loud the head is.
        _amp = measuring ? 1f : 20e-6f * MathF.Pow(10f, spec.SourceLevelDb / 20f);
        _gate = measuring ? 1f : 0f;
        // The horn: nothing below the flare cutoff, nothing above the driver. Two poles each,
        // because a horn's cutoff is a good deal steeper than one.
        _hpA = OnePole.AlphaFor(spec.FlareCutoffHz, rate);
        _lpA = OnePole.AlphaFor(spec.DriverTopHz, rate);
        // Where the beam starts to narrow: a couple of octaves over the cutoff, which is where ka
        // passes about four and the lobe stops being a hemisphere.
        _splitA = OnePole.AlphaFor(MathF.Min(spec.DriverTopHz * 0.8f, spec.FlareCutoffHz * 3f), rate);

        // What the horn costs, MEASURED — the same rule BladeRow follows for its broadband band.
        //
        // A siren's declared figure is a legal one: 120 dB at ten feet, type-approved. The model
        // has to MAKE that, and the chain between the oscillator and the air takes an unknown
        // amount out of it: the flare cutoff sits a quarter of an octave under the bottom of the
        // sweep, so the two-pole high-pass is already working on the fundamental, and how much it
        // takes depends on where the sweep and the cutoff happen to land for this horn. Left
        // uncompensated the patrol head measured 125.8 dB against the 130 it declares, and a
        // four-decibel over-declaration is a siren placed two decibels quieter than the law says
        // it is — which is the wrong direction for the one sound on the vehicle that exists to be
        // heard over everything else.
        //
        // So it is measured here, once, by running the real oscillator and the real filters across
        // the real sweep, rather than estimated.
        if (!measuring) _amp /= MathF.Max(1e-6f, MeasureChainRms(spec, rate));
    }

    /// <summary>
    /// The RMS the oscillator and horn produce for a unit reference amplitude, averaged across the
    /// whole sweep. Run once per head at construction; a tenth of a second of arithmetic.
    /// </summary>
    private static float MeasureChainRms(SirenSpec spec, float rate)
    {
        var probe = new ElectronicSiren(spec, rate, measuring: true) { Mode = SirenMode.Wail };
        // A whole sweep, so the average is over the band the head actually uses rather than over
        // whatever frequency a fixed probe happened to sit at.
        int n = (int)(MathF.Max(0.1f, spec.WailSeconds) * rate);
        for (int i = 0; i < rate / 20; i++) probe.Step();       // settle the gate and the filters
        double sum = 0;
        for (int i = 0; i < n; i++) { probe.Step(); sum += (double)probe.Output * probe.Output; }
        return MathF.Sqrt((float)(sum / n));
    }

    /// <summary>
    /// Where the listener is, in the car's frame: +z is the way the car points.
    ///
    /// A horn does not beam by the same amount at every frequency, and that is the whole of what a
    /// siren pass-by sounds like. The beam is set by ka — the mouth circumference against the
    /// wavelength — so at the flare cutoff the mouth is barely a wavelength across and radiates
    /// almost everywhere, while at four kilohertz it is nearly ten wavelengths and throws a narrow
    /// forward lobe. A siren coming towards you is bright and hard; the instant it passes, the top
    /// falls away and what is left is the body of it, and it sounds further away than it is.
    ///
    /// Modelled as one gain per band rather than one gain: the low band keeps the gentle
    /// front-to-back ratio a small horn really has, and the high band is squared twice over, which
    /// is what a beam four times narrower comes to. Level alone — which is what this used to be —
    /// gives a pass-by that gets quieter and never changes colour, and the ear reads that as the
    /// source moving away rather than turning.
    /// </summary>
    public void SetListener(System.Numerics.Vector3 carFrame)
    {
        if (carFrame.LengthSquared() < 1e-4f) return;
        float fwd = System.Numerics.Vector3.Normalize(carFrame).Z;
        float front = 0.5f + 0.5f * fwd;                  // 1 dead ahead, 0 dead astern
        // The body of the sound: a small horn at its own cutoff is nearly omnidirectional.
        _axisGain = 0.45f + 0.55f * front;
        // The top: ka is several times larger up here, so the lobe is much tighter. The extra
        // ratio is the same front factor raised again — a beam that narrows with frequency.
        _highGain = 0.06f + 0.94f * front * front * front;
    }

    public void Step()
    {
        // The amplifier's gate. A siren head comes up in a few tens of milliseconds — fast enough
        // to sound instant, slow enough that switching it on is not a click.
        float want = Mode == SirenMode.Off ? 0f : 1f;
        _gate += Math.Clamp(want - _gate, -_dt / 0.04f, _dt / 0.03f);
        if (_gate <= 1e-5f && want == 0f) { Output = 0f; return; }

        float f;
        if (Mode == SirenMode.HiLo)
        {
            // Two fixed notes, alternating. No sweep at all: the tone steps, and the step is the
            // whole character of a European siren.
            _sweep += _dt / MathF.Max(0.05f, Spec.HiLoHoldSeconds);
            if (_sweep >= 1.0) { _sweep -= 1.0; _hiHalf = !_hiHalf; }
            f = _hiHalf ? Spec.HiLoLowHz * Spec.HiLoRatio : Spec.HiLoLowHz;
        }
        else
        {
            float period = MathF.Max(0.02f, Spec.PeriodFor(Mode == SirenMode.Off ? SirenMode.Wail : Mode));
            _sweep += _dt / period;
            if (_sweep >= 1.0) _sweep -= 1.0;
            // Up fast, down slow — 40/60. The electromechanical siren this imitates was a motor
            // spinning a rotor up against its own inertia and then coasting down, and the coast is
            // always the longer half. The electronic heads kept the asymmetry because that is what
            // people recognise.
            float x = (float)_sweep;
            float t = x < 0.4f ? x / 0.4f : 1f - (x - 0.4f) / 0.6f;
            // Smooth at both turning points: a sweep with a corner in it clicks once per sweep,
            // and at ten sweeps a second that is a buzz on top of the siren.
            t = 0.5f - 0.5f * MathF.Cos(MathF.PI * t);
            // Swept in LOG frequency, not in hertz. A siren sweeping an octave sounds like it is
            // sweeping evenly; one sweeping 800 linear hertz spends most of its time up top.
            f = Spec.SweepLowHz * MathF.Pow(Spec.SweepHighHz / Spec.SweepLowHz, t);
        }
        Hz = f;

        _phase += f * _dt;
        if (_phase >= 1.0) _phase -= 1.0;

        // ── The oscillator, summed harmonic by harmonic ────────────────────────────────────────
        //
        // A siren amplifier puts out a sawtooth, and that is the reason one carries through traffic
        // where a sine would not: every harmonic is present, so whatever the road is masking,
        // something of the siren is above it. But it must be built from the harmonics that EXIST,
        // not from the shape and a correction.
        //
        // The first version made a naive sawtooth, corrected its step with PolyBLEP, and then ran
        // it through a tanh for the driver's compression. PolyBLEP fixes the sawtooth's OWN
        // discontinuity; the waveshaper after it then manufactures a fresh set of harmonics, most
        // of them above Nyquist, and those fold back. Aliased partials move DOWN the spectrum while
        // the real ones move up, so the faster the sweep the worse it gets: on a five-second wail
        // it is a slight roughness and on a ten-a-second phaser it is a stutter. Reported exactly
        // that way — "the fast siren sounds like it is stepping, not sweeping".
        //
        // So the harmonics are summed directly, and only the ones the horn will radiate at all:
        // above the driver's top there is nothing to hear and nothing to alias. At 650-1450 Hz
        // against a 4.5 kHz driver that is three to seven partials, which is cheaper than the
        // waveshaper it replaces.
        //
        // COMPRESSION becomes what it physically is — a flatter harmonic rolloff. An undriven
        // sawtooth falls as 1/n; a diaphragm being squashed pushes energy up the series, towards
        // 1/n^0.4 at the limit. That is the same statement as the tanh made, without inventing
        // partials above Nyquist to make it.
        float c = Math.Clamp(Spec.Compression, 0f, 0.95f);
        float rolloff = 1f - 0.6f * c;
        float top = MathF.Min(Spec.DriverTopHz * 1.5f, _rate * 0.45f);
        int n = Math.Clamp((int)(top / MathF.Max(1f, f)), 1, 24);

        // A SQUARE at the declared duty, summed partial by partial — not a sawtooth.
        //
        // The Fourier series of a rectangular wave of duty d is  a_k = sin(pi k d) / (pi k),
        // which at d = 0.5 vanishes for every even k: odd harmonics only, 1/n. That is the siren
        // sound, and it is what the rotary chopper these heads imitate actually makes — ports and
        // lands cut equally wide, airflow switched fully on and fully off. See SirenSpec.Duty.
        //
        // The same series with d nudged off a half puts a little of the even set back, which is
        // what a real machined rotor does and what keeps it from sounding synthetic. And because
        // it is a SERIES rather than a shape put through a filter, nothing aliases: the partials
        // that exist are the ones below the driver's top, by construction.
        float d = Math.Clamp(Spec.Duty, 0.05f, 0.95f);
        float saw = 0f, norm = 0f;
        for (int k = 1; k <= n; k++)
        {
            float a = MathF.Sin(MathF.PI * k * d) / (MathF.PI * k);
            // Compression flattens the series, as squashing a wave against a diaphragm's limit
            // does — the same statement as before, one waveform over.
            a *= MathF.Pow(k, 1f - rolloff);
            saw += a * MathF.Sin((float)(_phase * k * 2.0 * Math.PI));
            norm += a * a;
        }
        // Held to unit RMS however many partials survive, so the level does not jump as the sweep
        // carries a harmonic up past the driver's top and out of the sum.
        saw *= norm > 1e-9f ? 0.7071f / MathF.Sqrt(norm * 0.5f) : 0f;

        // The horn. Below the flare cutoff the mouth cannot couple to the air at all; above the
        // driver's top the diaphragm's own mass takes over. What is left is the band a siren lives
        // in, and it is geometry rather than taste — see SirenSpec.FlareCutoffHz.
        _hp1 += _hpA * (saw - _hp1);
        _hp2 += _hpA * (_hp1 - _hp2);
        float y = saw - _hp2;
        _lp1 += _lpA * (y - _lp1);
        _lp2 += _lpA * (_lp1 - _lp2);

        // Split the radiated band and aim the two halves separately.
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
