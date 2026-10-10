using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;

namespace OpenFPS.Client.AudioEngine.Core.Signals;

/// <summary>
/// A struck bell: a crossing gong, a locomotive bell, a tram's foot gong.
///
/// A flat disc's modes go as thickness over radius squared. Curving it adds membrane stiffness that
/// raises only the modes that must stretch the metal; those with several nodal diameters flex
/// inextensionally. So the low modes climb, the spacing closes and a plate becomes a bell.
///
/// How long it rings is a loss, not a declared decay: the metal's friction (bronze about 5e-5, cast
/// iron twenty times more), the air (rising with frequency, so a bell rounds as it dies) and the
/// mounting (gripping the modes that move at the crown); a crossing gong's 2.5 s comes out of those.
/// How bright it is is the clapper's Hertz contact time, about a tenth of a millisecond for steel on
/// bronze, a corner at four kilohertz. Which modes answer is where it is struck: (r/a)^m, a thud at
/// the crown and a bell at the rim.
/// </summary>
public sealed class StruckBell
{
    /// <summary>The mode families of a free circular plate, as (nodal diameters, nodal circles) and
    /// the eigenvalue that goes with them. The first two rigid-body cases are not modes and are not
    /// here.</summary>
    private static readonly (int M, int N, float Lambda2)[] Plate =
    {
        (2, 0, 5.253f), (0, 1, 9.084f), (3, 0, 12.23f), (1, 1, 20.52f), (4, 0, 21.60f),
        (5, 0, 33.05f), (2, 1, 35.25f), (0, 2, 38.55f), (6, 0, 46.68f), (3, 1, 52.9f),
        (7, 0, 62.79f), (4, 1, 73.0f), (8, 0, 81.0f), (5, 1, 96.0f),
    };

    private readonly StruckBellSpec _spec;
    private readonly float _rate;
    private readonly DampedMode[] _modes;
    private readonly float[] _weight, _hz, _t60;
    private readonly float _contactSeconds, _refAmp;
    private float _gain = 1f;
    private readonly Random _rng;
    private double _strikeT = -1;         // seconds into the current contact, negative when clear
    private float _strikeAmp;
    private double _sinceStrike = 1e9;
    private float _hp;

    /// <summary>Struck over and over at the spec's rate, the way a crossing signal is.</summary>
    public bool Ringing { get; set; }
    /// <summary>Output, pascals at one metre, valid after Step().</summary>
    public float Out { get; private set; }

    public StruckBell(StruckBellSpec spec, float rate = RenderRate.Default, int seed = 31)
    {
        _spec = spec; _rate = rate; _rng = new Random(seed);
        float a = 0.5f * spec.DiameterMetres, h = spec.ThicknessMetres;
        float nu = 0.33f;
        float d = spec.YoungsPa * h * h * h / (12f * (1f - nu * nu));
        float rhoH = spec.DensityKgM3 * h;
        float plateK = MathF.Sqrt(d / rhoH) / (MathF.Tau * a * a);
        float cL = spec.PlateWaveSpeed;
        // A sphere of the dome's radius breathes at c_L / 2πR: where the curvature pushes the low
        // modes.
        float rise = MathF.Max(1e-4f, spec.RiseMetres);
        float rCurv = (a * a + rise * rise) / (2f * rise);
        float fRing = cL / (MathF.Tau * rCurv);
        // Below the critical frequency front and back short-circuit; the floor on the radiation
        // efficiency below stands for the curvature, which lets a shell radiate far better than a
        // flat plate.
        float fCrit = 343f * 343f / (1.8f * cL * h);

        int n = Plate.Length;
        _modes = new DampedMode[n]; _weight = new float[n]; _hz = new float[n]; _t60 = new float[n];
        for (int i = 0; i < n; i++)
        {
            var (m, circles, lambda2) = Plate[i];
            float fPlate = lambda2 * plateK;
            // Only the modes that have to stretch the shell feel the curvature.
            float kappa = 1f / (1f + 0.25f * m * m);
            float f = MathF.Sqrt(fPlate * fPlate + kappa * kappa * fRing * fRing);
            _hz[i] = f;

            float sigma = Math.Clamp(f / fCrit + 0.25f, 0.05f, 1f);
            float etaRad = 1.2f * 343f * sigma / (MathF.Tau * MathF.Max(20f, f) * rhoH);
            // The mounting grips what moves at the crown and cannot reach what does not.
            float etaMount = spec.MountLossFactor * (m == 0 ? 1f : m == 1 ? 0.45f : 0.05f / (m - 1f));
            float eta = spec.LossFactor + etaRad + etaMount;
            float q = 1f / MathF.Max(1e-6f, eta);
            _modes[i] = new DampedMode(f, q, rate);
            _t60[i] = 6.91f * q / (MathF.PI * MathF.Max(20f, f));

            // Struck at a radius, a mode with m nodal diameters answers with (r/a)^m; one with a
            // nodal circle answers less wherever that circle happens to be.
            float shape = MathF.Pow(Math.Clamp(spec.StrikeRadiusFraction, 0.05f, 1f), m) * (1f - 0.3f * circles);
            _weight[i] = MathF.Max(0f, shape) * sigma;
        }

        // Hertzian contact, steel clapper on bronze: its length is the brightness of the blow.
        float eStar = 1f / ((1f - 0.09f) / 210e9f + (1f - nu * nu) / spec.YoungsPa);
        float rBall = 0.012f + 0.02f * spec.ClapperKg;
        float kHertz = 4f / 3f * eStar * MathF.Sqrt(rBall);
        _contactSeconds = 2.94f * MathF.Pow(5f * spec.ClapperKg / (4f * kHertz), 0.4f)
                        * MathF.Pow(MathF.Max(0.2f, spec.ClapperMps), -0.2f);

        _refAmp = 20e-6f * MathF.Pow(10f, spec.ReferenceDb / 20f);
        Calibrate();
    }

    /// <summary>
    /// Rings it in silence and sets the gain that makes its RMS the anchor. RMS, not peak: a bell's
    /// published level is a meter reading of it ringing. Anchored by the peak it sat about 25 dB low,
    /// and the locomotive bell could not be heard under its own train.
    /// </summary>
    private void Calibrate()
    {
        Ringing = true;
        int warm = (int)(1.2f * _rate), meas = (int)(2.0f * _rate);
        for (int i = 0; i < warm; i++) Step();
        double e = 0;
        for (int i = 0; i < meas; i++) { Step(); e += Out * (double)Out; }
        float rms = (float)Math.Sqrt(e / Math.Max(1, meas));
        _gain = rms > 1e-12f ? _refAmp / rms : 1f;
        for (int i = 0; i < _modes.Length; i++) _modes[i].Reset();
        _strikeT = -1; _sinceStrike = 1e9; _hp = 0f; Out = 0f; Ringing = false;
    }

    /// <summary>Strike it now, at a fraction of the clapper's usual blow.</summary>
    public void Strike(float force = 1f)
    {
        _strikeT = 0;
        _strikeAmp = MathF.Max(0.02f, force);
        _sinceStrike = 0;
        // While the clapper is on the bell it is extra loss, and the ring is held back until it
        // comes off (Step, 12 ms), not when the blow's contact ends a fifth of a millisecond in.
        for (int i = 0; i < _modes.Length; i++) _modes[i].Multiply(1f + 40f * _spec.ClapperDamping);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Step()
    {
        float dt = 1f / _rate;
        if (Ringing && _sinceStrike > 1.0 / MathF.Max(0.2f, _spec.StrikesPerSecond))
            Strike(0.92f + 0.16f * (float)_rng.NextDouble());
        _sinceStrike += dt;

        // The blow: a half sine as long as the contact.
        float f = 0f;
        if (_strikeT >= 0)
        {
            float x = (float)(_strikeT / _contactSeconds);
            if (x >= 1f) _strikeT = -1;
            else { f = MathF.Sin(MathF.PI * x) * _strikeAmp; _strikeT += dt; }
        }
        // The clapper is clear again a few milliseconds after it landed.
        if (_sinceStrike > 0.012 && _sinceStrike - dt <= 0.012)
            for (int i = 0; i < _modes.Length; i++) _modes[i].Restore();

        float y = 0f;
        for (int i = 0; i < _modes.Length; i++) y += _modes[i].Process(f) * _weight[i];
        _hp += OnePole.AlphaFor(40f, _rate) * (y - _hp);
        Out = (y - _hp) * _gain;
    }

    public IEnumerable<string> Describe()
    {
        yield return $"{_spec.Name}: {_spec.DiameterMetres * 1000f:F0} mm x {_spec.ThicknessMetres * 1000f:F1} mm, "
                   + $"dome {_spec.RiseMetres * 1000f:F0} mm, {_spec.ReferenceDb:F0} dB at 1 m, {_spec.StrikesPerSecond:F1} strikes/s";
        yield return $"  clapper {_spec.ClapperKg:F2} kg at {_spec.ClapperMps:F1} m/s -> contact {_contactSeconds * 1000f:F2} ms, so the blow rolls off above {1f / (2f * _contactSeconds):F0} Hz";
        for (int i = 0; i < _modes.Length && i < 8; i++)
            yield return $"  mode ({Plate[i].M},{Plate[i].N}) {_hz[i]:F0} Hz, rings {_t60[i]:F2} s, weight {_weight[i]:F2}";
    }

    /// <summary>A resonance whose damping can be changed while it is still ringing.</summary>
    private struct DampedMode
    {
        private readonly float _f, _rate, _q;
        private float _a1, _a2, _g, _z1, _z2;

        public DampedMode(float hz, float q, float rate)
        {
            _f = Math.Clamp(hz, 2f, rate * 0.46f); _rate = rate; _q = q;
            _a1 = _a2 = _g = _z1 = _z2 = 0f;
            Set(q);
        }

        private void Set(float q)
        {
            float r = MathF.Exp(-MathF.PI * MathF.Max(0.02f, _f / MathF.Max(0.2f, q)) / _rate);
            float theta = MathF.Tau * _f / _rate;
            _a1 = 2f * r * MathF.Cos(theta);
            _a2 = -r * r;
            _g = (1f - r) * MathF.Sqrt(MathF.Max(1e-9f, 1f - 2f * r * MathF.Cos(2f * theta) + r * r));
        }

        /// <summary>Damp it by a factor, keeping whatever it is already doing.</summary>
        public void Multiply(float lossFactor) => Set(_q / MathF.Max(1f, lossFactor));
        public void Restore() => Set(_q);
        public void Reset() { _z1 = _z2 = 0f; Set(_q); }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public float Process(float x)
        {
            float y = _g * x + _a1 * _z1 + _a2 * _z2;
            _z2 = _z1; _z1 = y;
            return y;
        }
    }
}
