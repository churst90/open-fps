using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;

namespace OpenFPS.Client.AudioEngine.Core.Signals;

/// <summary>
/// A steam whistle: a cup with a lid, and an annular sheet of steam blown across the gap under it.
///
/// A stopped flue pipe: c/4L and the odd harmonics, half an air horn's note for the length and
/// hollow where the horn is brassy. Sound runs at about 510 m/s in steam at 170 °C against 343 in
/// air, and the bell starts cooler, so the note wails up two or three semitones as the steam fills
/// and heats it.
/// A chime whistle's bells are deliberately not a clean chord and beat against each other; and much of
/// what is heard is the jet's breath, rising with the supply. The jet is locked to the pipe (see
/// Bell), so it will not overblow to its third harmonic as a real one blown too hard does.
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
    private readonly float _wobbleStep;   // 0.0009 a sample at 44.1 kHz: 6.3 Hz

    /// <summary>
    /// What the bell sits at between blasts: not ambient, since it sits on a boiler with live steam
    /// under its valve. Started cold it wailed up seven semitones, three times a real one, like a
    /// slide whistle.
    /// </summary>
    private const float IdleKelvin = 372f;

    /// <summary>Whether the cord is pulled.</summary>
    public bool Blowing { get; set; }
    /// <summary>Output, pascals at one metre, valid after Step().</summary>
    public float Out { get; private set; }
    public float SoundSpeed => MathF.Sqrt(1.33f * 461.5f * MathF.Max(280f, _kelvin));

    public SteamWhistle(WhistleSpec spec, float rate = RenderRate.Default, int seed = 23)
    {
        _spec = spec; _rate = rate; _rng = new Random(seed); _wobbleStep = At44k.Step(0.0009f, rate);
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
        _valve += (target - _valve) * MathF.Min(1f, 1f / MathF.Max(1f, 0.035f * _rate));

        // Filling with steam and warming the metal are one time constant, and both are the wail.
        float want = _valve > 0.05f ? MathHelper.Lerp(IdleKelvin, _spec.SteamKelvin, _valve) : IdleKelvin;
        _kelvin += (want - _kelvin) * MathF.Min(1f, 1f / MathF.Max(1f, _spec.WarmSeconds * _rate));

        // Water carried over and the boiler breathing: a roughness of a few hertz on level and
        // pitch, most of what separates steam from a siren.
        float n = (float)(_rng.NextDouble() * 2 - 1);
        _wobbleLp += _wobbleStep * (n - _wobbleLp);
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
    /// One stopped bell and the sheet of steam under it. The jet is locked to the pipe, as the air
    /// horn's reed is: a flue entrains within a couple of cycles, inaudibly, and a jet model that fails
    /// to start is very audible. The sheet swings across the lip and saturates, a rounded wave, with
    /// the jet's breath on top: a third of what is heard.
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
        private readonly float _jitterStep;   // 0.0006 a sample at 44.1 kHz: 4.2 Hz

        public Bell(WhistleSpec s, WhistleBellSpec b, float rate, int seed)
        {
            _b = b; _rate = rate; _dt = 1f / rate; _rng = new Random(seed); _jitterStep = At44k.Step(0.0006f, rate);
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

        /// <summary>A stopped pipe: odd harmonics only, the high ones losing more at the walls.</summary>
        private void Build(float c)
        {
            float f1 = c / (4f * MathF.Max(0.02f, _b.EffectiveLengthMetres));
            // Sized for the coldest the bell gets, so no mode is created or destroyed under a note.
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
            // Retuned when the sound speed has moved enough to matter: a few dozen times a wail.
            if (MathF.Abs(c - _lastC) > 0.0004f * _lastC) Build(c);

            float f1 = c / (4f * MathF.Max(0.02f, _b.EffectiveLengthMetres));
            // A real whistle wanders, but slightly: steady sounds like a synthesiser, and six per cent
            // was "not like solid notes".
            _jitter += _jitterStep * ((float)_rng.NextDouble() * 2f - 1f - _jitter);
            _phase += f1 * (1f + 0.7f * _jitter) * _dt;
            if (_phase >= 1.0) _phase -= 1.0;

            float jet = MathF.Tanh(2.9f * MathF.Sin(MathF.Tau * (float)_phase)) * valve;

            // The jet's noise is set by its gap and speed (a 3 mm slot at 400 m/s peaks far above
            // hearing): broadband and bright, nothing to do with the note. Banded round the note it
            // sounded, in Cody's words, "like under water".
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
