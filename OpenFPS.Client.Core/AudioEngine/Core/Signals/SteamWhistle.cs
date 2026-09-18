using System;
using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;

namespace OpenFPS.Client.AudioEngine.Core.Signals;

/// <summary>
/// A steam whistle: a cup with a lid, and an annular sheet of steam blown across the gap under it.
///
/// It is a flue pipe and not a reed, and it is CLOSED at the top, so it sounds c/4L and the odd
/// harmonics — half the frequency of an air horn of the same length, and hollow where the horn is
/// brassy. The sheet of steam flaps in and out of the lip at the pipe's own rate and keeps it going.
///
/// The thing that makes it a steam whistle rather than an organ pipe is the gas. Sound travels at
/// about 510 m/s in steam at 170 C against 343 in room air, so a whistle stands a fifth sharp of a
/// pipe of the same length — and it is why a whistle WAILS UP as it starts. The bell begins full of
/// cold air; the steam displaces it and heats the walls over the best part of a second, the sound
/// speed inside climbs with the square root of temperature, and the note rises two or three
/// semitones to settle. Nothing bends the pitch: the gas in the pipe changes.
///
/// Two more things it does on its own. A chime whistle's bells are deliberately NOT a clean chord,
/// so the close ones beat against each other a few times a second — the "hollow" in a big whistle,
/// which no single bell has. And a great deal of what you hear is not the note at all but the jet
/// tearing itself apart at the lip: the breathiness, which rises with supply pressure and is why a
/// whistle sounds like weather as much as like an instrument.
///
/// The one idealisation, marked because it is one: the jet's transit from gap to lip is taken as the
/// phase the pipe wants (a third of a period) rather than computed from the gap and the jet speed.
/// Doing that properly needs the jet's growth rate, which this model does not carry; what it costs
/// is that the whistle will not overblow to its third harmonic when it is blown too hard, the way a
/// real one does.
/// </summary>
public sealed class SteamWhistle
{
    private readonly WhistleSpec _spec;
    private readonly float _rate;
    private readonly Bell[] _bells;
    private readonly float _refAmp;
    private float _valve, _kelvin;
    private readonly Random _rng;
    private float _wobble, _wobbleLp;

    /// <summary>
    /// What the bell sits at between blasts. NOT ambient: a whistle lives on top of a boiler with
    /// live steam in the pipe under its valve, and the casting is hot to the touch. Starting it cold
    /// made it wail up through seven semitones, which is three times what a real one does — the
    /// first version did exactly that and it sounded like a slide whistle.
    /// </summary>
    private const float IdleKelvin = 372f;

    /// <summary>Whether the cord is pulled.</summary>
    public bool Blowing { get; set; }
    /// <summary>Output, pascals at one metre, valid after Step().</summary>
    public float Out { get; private set; }
    /// <summary>The gas temperature in the bells now — the thing the pitch is riding on.</summary>
    public float GasKelvin => _kelvin;
    public float SoundSpeed => MathF.Sqrt(1.33f * 461.5f * MathF.Max(280f, _kelvin));

    public SteamWhistle(WhistleSpec spec, float rate = 44100f, int seed = 23)
    {
        _spec = spec; _rate = rate; _rng = new Random(seed);
        _kelvin = IdleKelvin;
        _bells = new Bell[spec.Bells.Length];
        for (int i = 0; i < _bells.Length; i++) _bells[i] = new Bell(spec, spec.Bells[i], rate, seed + i * 13);
        float sum = 0f;
        foreach (var b in spec.Bells) sum += MathF.Pow(10f, b.LevelTrimDb / 10f);
        _refAmp = 20e-6f * MathF.Pow(10f, spec.ReferenceDb / 20f) / MathF.Sqrt(MathF.Max(1e-3f, sum));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Step()
    {
        float target = Blowing ? 1f : 0f;
        // The valve is a lever on a pipe: it opens fast and the steam arrives behind it.
        _valve += (target - _valve) * MathF.Min(1f, 1f / MathF.Max(1f, 0.035f * _rate));

        // The bell fills with steam and warms. Cold air on the way in, cold metal taking heat out
        // of it; both are one time constant and both are the wail.
        float want = _valve > 0.05f ? MathHelper.Lerp(IdleKelvin, _spec.SteamKelvin, _valve) : IdleKelvin;
        _kelvin += (want - _kelvin) * MathF.Min(1f, 1f / MathF.Max(1f, _spec.WarmSeconds * _rate));

        // Water carried over with the steam, and the boiler breathing: a slow roughness on both the
        // level and the pitch, a few hertz, which is most of what separates steam from a siren.
        float n = (float)(_rng.NextDouble() * 2 - 1);
        _wobbleLp += 0.0009f * (n - _wobbleLp);
        _wobble = _wobbleLp * 26f;

        float c = SoundSpeed * (1f + 0.004f * _wobble);
        float y = 0f;
        for (int i = 0; i < _bells.Length; i++) y += _bells[i].Step(_valve, c, 1f + 0.10f * _wobble);
        Out = y * _refAmp;
    }

    public System.Collections.Generic.IEnumerable<string> Describe()
    {
        float c = MathF.Sqrt(1.33f * 461.5f * _spec.SteamKelvin);
        yield return $"{_spec.Name}: {_spec.Bells.Length} bell(s), {_spec.SupplyKPa:F0} kPa, steam at {_spec.SteamKelvin:F0} K -> sound speed {c:F0} m/s ({c / 343f:F2}x air)";
        foreach (var b in _spec.Bells)
            yield return $"  bell {b.LengthMetres * 1000f:F0} mm stopped -> {b.HzAt(c):F0} Hz hot, {b.HzAt(MathF.Sqrt(1.33f * 461.5f * IdleKelvin)):F0} Hz at the first instant";
        yield return $"  breathiness {_spec.Breathiness:F2}, warms in {_spec.WarmSeconds:F2} s from {IdleKelvin:F0} K, "
                   + $"so it wails up {12f * MathF.Log2(MathF.Sqrt(_spec.SteamKelvin / IdleKelvin)):F1} semitones";
    }

    /// <summary>
    /// One stopped bell and the sheet of steam under it.
    ///
    /// The jet is locked to the pipe rather than left to find it, for the same reason and with the
    /// same honesty as the horn's reed: a flue pipe entrains within a couple of cycles and the
    /// finding is not audible, while a jet model that fails to start is extremely audible. What that
    /// costs is the overblow — a real whistle blown far too hard jumps to its third harmonic and
    /// this one will not.
    ///
    /// The waveform is a flue's and not a reed's: the sheet of steam swings smoothly across the lip
    /// and saturates at the ends of its travel, so it makes a rounded wave with the ODD harmonics of
    /// a stopped pipe and nothing like a horn's pulse. On top of it is the jet tearing itself apart,
    /// which in a whistle is not a detail — it is a third of what you hear, and it is why a whistle
    /// sounds like weather.
    /// </summary>
    internal sealed class Bell
    {
        private readonly WhistleBellSpec _b;
        private readonly float _rate, _dt, _trim, _breath;
        private readonly Random _rng;
        private Mode[]? _modes;
        private double _phase;
        private float _hp, _noiseLp1, _noiseLp2;
        private float _lastC;
        private float _amp = 1f;
        private float _jitter;

        public Bell(WhistleSpec s, WhistleBellSpec b, float rate, int seed)
        {
            _b = b; _rate = rate; _dt = 1f / rate; _rng = new Random(seed);
            _trim = MathF.Pow(10f, b.LevelTrimDb / 20f);
            _breath = Math.Clamp(s.Breathiness, 0f, 1f);
            _lastC = MathF.Sqrt(1.33f * 461.5f * s.SteamKelvin);
            Build(_lastC);
            // Blow it once in silence and set the gain that makes the bank add up to the anchor.
            int nw = (int)(0.5f * rate), nm = (int)(0.4f * rate);
            for (int i = 0; i < nw; i++) Step(1f, _lastC, 1f);
            double e = 0;
            for (int i = 0; i < nm; i++) { float y = Step(1f, _lastC, 1f); e += y * (double)y; }
            float rms = (float)Math.Sqrt(e / nm);
            _amp = rms > 1e-9f ? 1f / rms : 1f;
            Build(_lastC);
            _hp = _noiseLp1 = _noiseLp2 = 0f; _phase = 0;
        }

        /// <summary>A stopped pipe: only the odd harmonics exist in it, and the high ones lose more
        /// at the walls, so it is a rounded wave and not a square one.</summary>
        private void Build(float c)
        {
            float f1 = c / (4f * MathF.Max(0.02f, _b.EffectiveLengthMetres));
            // Sized for the COLDEST the bell gets, so the count never changes and a mode is never
            // created or destroyed under a sounding note.
            if (_modes == null)
            {
                float fCold = MathF.Sqrt(1.33f * 461.5f * IdleKelvin) / (4f * MathF.Max(0.02f, _b.EffectiveLengthMetres));
                int n = Math.Max(1, (int)MathF.Min(7f, 0.45f * _rate / (2f * fCold)));
                _modes = new Mode[n];
                for (int k = 0; k < n; k++) _modes[k] = new Mode(f1 * (2 * k + 1), 28f / (1f + 0.6f * k), _rate);
            }
            else
            {
                for (int k = 0; k < _modes.Length; k++) _modes[k].Retune(f1 * (2 * k + 1), 28f / (1f + 0.6f * k), _rate);
            }
            _lastC = c;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public float Step(float valve, float c, float breathMod)
        {
            if (valve < 1e-3f) return 0f;
            // The pitch rides the gas. Rebuild the modes when the sound speed has moved enough to
            // matter, which is a few dozen times over the wail and never again after it.
            if (MathF.Abs(c - _lastC) > 0.0004f * _lastC) Build(c);

            float f1 = c / (4f * MathF.Max(0.02f, _b.EffectiveLengthMetres));
            // Water carried over, and the jet wandering on the lip: a real whistle is never quite
            // steady, and a steady one sounds like a synthesiser.
            // A real whistle wanders, but only slightly: the first version wobbled six per cent,
            // which is most of a semitone, continuously — and a note that is never in one place is
            // not a note. ("Not like solid notes.")
            _jitter += 0.0006f * ((float)_rng.NextDouble() * 2f - 1f - _jitter);
            _phase += f1 * (1f + 0.7f * _jitter) * _dt;
            if (_phase >= 1.0) _phase -= 1.0;

            // The sheet of steam swinging across the lip, saturating at the ends of its travel.
            float jet = MathF.Tanh(2.9f * MathF.Sin(MathF.Tau * (float)_phase)) * valve;

            // The steam tearing itself apart at the lip. It is a JET, and a jet's noise is set by
            // the gap it comes through and how fast it is going — a three millimetre slot at four
            // hundred metres a second peaks far above hearing — so it is broadband and BRIGHT, and
            // it has nothing to do with the note. Banding it around the note instead (which is what
            // this did first) takes all the air out of a whistle and leaves it sounding, in the
            // owner's words, "like under water".
            float nz = (float)(_rng.NextDouble() * 2 - 1);
            _noiseLp1 += OnePole.AlphaFor(7000f, _rate) * (nz - _noiseLp1);
            _noiseLp2 += OnePole.AlphaFor(320f, _rate) * (_noiseLp1 - _noiseLp2);
            float breath = (_noiseLp1 - _noiseLp2) * 1.9f * _breath * valve * breathMod;

            float p = 0f;
            for (int k = 0; k < _modes!.Length; k++) p += _modes[k].Process(jet + breath * 0.22f) / (1f + 0.42f * k);

            float y = p + breath * 0.5f;
            _hp += OnePole.AlphaFor(60f, _rate) * (y - _hp);
            return (y - _hp) * _trim * _amp;
        }
    }
}
