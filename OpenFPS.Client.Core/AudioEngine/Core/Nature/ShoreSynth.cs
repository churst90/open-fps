using System;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// Waves at the water's edge, as the events they are made of (docs/WAVES_AND_SHORES.md).
///
/// THE WAVES. The wind raises a sea over the water upwind of the edge, its height and period set by the
/// wind and the fetch (WindWaves, the fetch-limited JONSWAP relations); the sea's swell arrives from far
/// away whatever the wind; a river's current sheds eddies off the bank's roots and stones that rock the
/// water at the edge. Each is a spectrum, and the water at a place along the edge is a sum of a few
/// dozen of its components with their own phases: so the waves come in groups, a few big ones every
/// five or ten, the way a real sea does, without anything here saying so. Each wave is found as it
/// arrives (a zero up-crossing of the surface at the place), with its own height and period.
///
/// WHAT EACH WAVE DOES depends on the edge:
///  * On a beach (<see cref="ShoreFace.Beach"/>) it shoals and breaks: spilling, plunging or surging by
///    its Iribarren number. A breaker fast enough (its crest over 0.8 m/s, a plunging jet's onset of air)
///    folds air under, a plume of bubbles of the Deane and Stokes sizes; a plunger's jet lands as a crash
///    of spray. Its bore then runs up the beach (Hunt's run-up), the front breaking like the lee jet of a
///    stone in a creek (the running water's law, RunningWaterSynth.BreakingAirShare), and runs back.
///    Sand drinks the swash and lets out the air in its pores as a fizz of bursting bubbles; gravel and
///    shingle are dragged by the backwash and click against each other.
///  * At a wall or a rock (<see cref="ShoreFace.Wall"/>) it is thrown back: it slaps, throws up spray
///    that falls back, and some crests close on a pocket of air in a crack or under an overhang, which
///    rings (the clop).
///  * Against a hull (<see cref="ShoreFace.Hull"/>) the same, and the planking rings: the crest's blow
///    and the pocket's oscillation drive the bay's modes (HullSpec, RainPlate's physics, loaded by the
///    water on its wetted share).
///  * Out on the water, a steep young sea breaks in whitecaps (Banner, Babanin and Young 2000).
///
/// CROWDS AND SINGLES. A surf plume is millions of bubbles; a lake's lap a few. Every population here
/// (bubbles by octave of size, bursting bubbles, stone clicks) is rendered event by event while it is
/// sparse, and as band noise of the same power once there are more than a few to a block in a band:
/// a crowd too dense to tell apart is noise, and its envelope still moves with the waves and their
/// groups.
///
/// EXTENDED (ExtendedSources). A stretch of edge is heard from places along it, each its own column of
/// water with its own waves; a surf beach also from a row on its break line. Every event belongs to one
/// place, heard there with the spread, otherwise at the middle. A swell's crests are long: every column
/// shares the swell, each a little later than the last by the angle its crests come in at, so a break
/// runs along the beach.
///
/// WHAT IS FITTED is named here and nowhere else: <see cref="PlumeShellMetres"/>, <see cref="PopPascalsPerMm"/>,
/// <see cref="Flicker"/>. Every bubble's loudness for its size, the splash's efficiency and the breaking
/// front's air are the fountain's and the creek's, fitted there and not refitted here.
/// </summary>
public sealed class ShoreSynth
{
    // ── The fitted constants ─────────────────────────────────────────────────────────────────────

    /// <summary>How deep the shell of a breaker's plume is that is heard, m: only a thin outer shell of a
    /// plume radiates (Deane 1997); the rest is screened by the bubbly water round it (a bubbly mixture
    /// absorbs far more than water) and lies deep. A plume reaches about Hb down, so a share shell / Hb of
    /// its bubbles is heard: all of a lake wavelet's, a fiftieth of a metre-high breaker's. FITTED
    /// (docs/WAVES_AND_SHORES.md section 8).</summary>
    public static readonly float PlumeShellMetres = Knob("SHELL", 0.05f);

    /// <summary>The share of a breaker's plume heard, for its height.</summary>
    private static float PlumeHeard(float hb) => MathF.Min(1f, PlumeShellMetres / MathF.Max(1e-3f, hb));

    /// <summary>The largest bubble a breaker's air tube breaks into, mm: a tenth of its height, at most a
    /// centimetre (Deane and Stokes saw them to about that).</summary>
    private static float PlumeLargestMm(float hb) => Math.Clamp(100f * hb, 0.5f, 10f);

    /// <summary>A foam bubble bursting at the surface, the first peak of its ring, Pa at a metre for a 1 mm
    /// bubble, going as its radius. FITTED with the swash's foam.</summary>
    public static readonly float PopPascalsPerMm = Knob("POP", 0.01f);

    /// <summary>How unevenly a crowd of events comes, the standard deviation of the log of its rate
    /// from one 40 ms to the next: turbulent intermittency is log-normal (Kolmogorov 1962). FITTED.</summary>
    public static readonly float Flicker = Knob("FLICKER", 0.7f);

    /// <summary>How much of that unevenness every size of bubble shares (the rest each octave of sizes has
    /// on its own). FITTED.</summary>
    public static readonly float FlickerCommon = Knob("FLICKERCOMMON", 0.5f);

    // ── Physical constants and laws taken from elsewhere ─────────────────────────────────────────

    /// <summary>Air a breaker folds under per metre of crest, m³ per m of crest for each m² of Hb²: the
    /// volume Lamarre and Melville (1991) measured under laboratory breakers, 0.02-0.05 m² a metre of
    /// crest for waves breaking at a quarter of a metre.</summary>
    public const float AirPerBreak = 0.5f;

    /// <summary>A spilling breaker's air against a plunger's of the same height: it folds its crest down
    /// its own face over a long run instead of throwing a jet. FITTED.</summary>
    public static readonly float SpillingAirShare = Knob("SPILL", 0.1f);

    /// <summary>How skewed a pocket's loudness is: its depth factor u^β, as the fountain's crater bubbles
    /// (FallingWaterSynth.DepthSkew). FITTED.</summary>
    public static readonly float PocketSkew = Knob("POCKETSKEW", FallingWaterSynth.DepthSkew);

    /// <summary>For the fit: a constant's value from OPENFPS_WAVES_&lt;NAME&gt;, else its own.</summary>
    private static float Knob(string name, float value)
        => float.TryParse(Environment.GetEnvironmentVariable("OPENFPS_WAVES_" + name), System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : value;

    /// <summary>A crest entrains air from this speed, m/s: a plunging jet's onset (Ervine, McKeogh and
    /// Elsawy 1980), the running water's spray onset.</summary>
    public const float AirOnset = 0.8f;

    /// <summary>How much of a fully entraining breaker's air one at this crest speed folds under:
    /// ((c − c0) / c)², c0 the onset. A plunging jet's air grows as the square of its speed over the
    /// onset's (Chanson's re-analysis of plunging jets, Fr = (V − V0) / √(g d), air ∝ Fr²; the running
    /// water's docs/RUNNING_WATER.md 1.3), so a lake's wavelets, their crests barely over the onset, break
    /// almost without air (microbreaking) and a metre of surf with all of it.</summary>
    private static float Entrains(float crestSpeed)
    {
        if (crestSpeed <= AirOnset) return 0f;
        float x = 1f - AirOnset / crestSpeed;
        return x * x;
    }

    /// <summary>The share of the swash's front that breaks as a creek's lee jet does: only its tip, where it
    /// runs into the last wave's backwash or over the bed. FITTED.</summary>
    public static readonly float FrontShare = Knob("FRONT", 0.03f);

    /// <summary>How many drops run off a metre of wetted face after a crest that stood 10 cm up it. FITTED.</summary>
    public static readonly float RunOffDrops = Knob("DRIPS", 20f);

    /// <summary>The lip's bubbles against the front's thickness: the tube it closes on is about half the front
    /// across, and breaks into bubbles of about a third of that radius. An estimate.</summary>
    private const float LipNote = 0.3f;

    /// <summary>The share of a breaker's dissipated energy its bubble cloud radiates as a whole, in its
    /// collective oscillation: 10-300 Hz under breakers (Lamarre and Melville 1994), the low roar that
    /// plunging breakers add and spilling ones barely do (Loewen and Melville 1994). FITTED to the
    /// recorded surf's low octaves.</summary>
    public static readonly float CloudEfficiency = Knob("CLOUD", 1e-6f);

    /// <summary>The void fraction of a fresh plume, 0.3-0.4 (Deane 1997).</summary>
    private const float PlumeVoid = 0.3f;

    /// <summary>The share of a breaker's air that rides into the swash as foam and bursts there.</summary>
    private const float FoamShare = 0.1f;

    /// <summary>The air sand lets out per volume of swash soaking into it: the pore air it held, its
    /// porosity of about 0.35, for the share of the swash that soaks in before it runs back.</summary>
    private static readonly float SandVentShare = Knob("VENT", 0.0003f);

    /// <summary>The sea surface's Hinze scale, mm: the plume's size spectrum steepens above it (Deane and
    /// Stokes 2002).</summary>
    private const float HinzeMm = 1f;

    /// <summary>The smallest bubble worth ringing, mm.</summary>
    private const float SmallestMm = 0.16f;

    /// <summary>A pocket of air against a face is not a free bubble: it vents and breaks up within a few
    /// cycles (Topliss, Cooker and Peregrine 1993). Its damping ratio is at least this.</summary>
    private const float PocketDamping = 0.08f;

    /// <summary>A bubble bursting at the surface rings as a Helmholtz resonator as its film's hole opens,
    /// its note climbing to about c / 10R (Poujol et al. 2021): 34 kHz for a millimetre bubble, so only
    /// foam bubbles of a few millimetres are heard, as a sizzle. It rings a few cycles.</summary>
    private const float PopDamping = 0.15f;

    /// <summary>The note a bursting bubble ends on, Hz, for its radius in mm: c / 10R.</summary>
    private static float PopHz(float mm) => 343f / (10f * mm * 1e-3f);

    /// <summary>The share of the surface layer of stones the backwash keeps moving for each unit its
    /// speed squared stands over the threshold's, and the speed they close on each other at, as a share
    /// of the backwash's: stones rolled in a sheet mostly nudge. FITTED to the shingle recordings.</summary>
    public static readonly float StoneShare = Knob("STONESHARE", 0.001f), StoneClosing = Knob("STONECLOSE", 0.1f);

    /// <summary>The density of stone, kg/m³, and its Young's modulus, Pa (granite, flint).</summary>
    private const float StoneDensity = 2650f, StoneModulus = 5e10f;

    /// <summary>Whitecaps are counted over a strip of water this wide in front of the edge, m: what a
    /// listener at the edge hears of the open water.</summary>
    private const float WhitecapStripMetres = 10f;

    // ── The rendering ────────────────────────────────────────────────────────────────────────────

    private const int Block = 128;
    private const int Bands = 7;                 // octaves 250 Hz .. 16 kHz
    private const int MaxProcs = 128;
    /// <summary>More than this many events of one band in one block (about 1,100 a second) and the band
    /// is a crowd: rendered as noise of the same power.</summary>
    private const float SparseLimit = 3f;
    private const float FlickerSeconds = 0.04f;

    public readonly ShoreSpec Spec;
    public readonly ShoreGeometry Geometry;
    private readonly float _rate;
    private readonly int _along, _rows;
    private readonly EventSum[] _places;
    private readonly EventSum _rng;
    private readonly Column[] _columns;
    private readonly Train? _swell;
    private readonly Proc[] _procs = new Proc[MaxProcs];
    private readonly float[,] _bandFlicker = new float[MaxProcs, Bands];
    private readonly float[,] _power;            // per place, per band: Pa² this block
    private readonly float[,] _gain;              // per place, per band: the noise's gain now
    private readonly Resonator[,] _band, _band2;
    private readonly float[] _bandNorm = new float[Bands];
    private readonly HullPlate? _hull;
    private int _untilBlock;
    private double _time;

    /// <summary>The wind, m/s at 10 m, and where it blows FROM (degrees from north). The voice sets
    /// them from the weather; the sea follows them over minutes.</summary>
    public float WindSpeed, WindFromDegrees;
    /// <summary>How much of it its outer places carry, 0 to 1 (ExtendedSources.Shares).</summary>
    public float Spread;
    private float _toHome;

    /// <summary>The sea now: what the wind has raised, gliding toward what the wind would raise.</summary>
    private float _hs = float.NaN, _tp, _windSeaTarget, _tpTarget;

    /// <summary>Each part's share, for the lab to take the sound apart. One in the game.</summary>
    public float CloudPart = 1f, PlumePart = 1f, CrashPart = 1f, FrontPart = 1f, SprayPart = 1f, FoamPart = 1f, VentPart = 1f,
                 StonePart = 1f, PocketPart = 1f, SlapPart = 1f, HullPart = 1f, WhitecapPart = 1f;

    /// <summary>For the lab: the waves found and broken since the start.</summary>
    public int Waves, Breakers, Pockets;

    public int Places => _places.Length;

    public ShoreSynth(ShoreSpec spec, float sampleRate, int seed, ShoreGeometry? geometry = null)
    {
        Spec = spec;
        Geometry = geometry ?? spec.DefaultGeometry;
        _rate = sampleRate;
        _along = Math.Max(1, spec.Places);
        _rows = spec.BreakRowMetres > 0f ? 2 : 1;
        _places = new EventSum[_along * _rows];
        for (int k = 0; k < _places.Length; k++) _places[k] = new EventSum(sampleRate, seed * 7919 + 5 + k * 15485863);
        _rng = new EventSum(sampleRate, seed * 31 + 77);
        _power = new float[_places.Length, Bands];
        _gain = new float[_places.Length, Bands];
        _band = new Resonator[_places.Length, Bands];
        _band2 = new Resonator[_places.Length, Bands];
        for (int b = 0; b < Bands; b++)
        {
            float hz = BandHz(b);
            // Two resonators in cascade: an octave's band falling 12 dB an octave outside it, so a crowd of
            // bubbles of one size lights its own octave and not the ones three away (a single two-pole
            // band leaks 6 dB an octave, which made a plume of big bubbles as bright as a fizz).
            for (int k = 0; k < _places.Length; k++) { _band[k, b].Tune(hz, BandQ, sampleRate); _band2[k, b].Tune(hz, BandQ, sampleRate); }
            _bandNorm[b] = 1f / CascadeGain(hz, sampleRate);
        }

        float length = MathF.Max(0.5f, Geometry.LengthMetres);
        _columns = new Column[_along];
        for (int j = 0; j < _along; j++)
        {
            var c = new Column
            {
                Width = length / _along,
                Swash = j,
                Break = _rows > 1 ? _along + j : j,
                Along = LineOffset(j, _along, length),
                Wind = Train.Jonswap(16, _rng),
            };
            if (spec.CurrentMetresPerSecond > 0.05f) c.Eddy = Train.Narrow(8, 0.12f, _rng);
            _columns[j] = c;
        }
        if (spec.SwellHeightMetres > 0f)
        {
            _swell = Train.Narrow(12, 0.06f, _rng);
            // Long crests coming in at an angle reach each column a little after the last: the break runs
            // along the beach at the breaking wave's speed over the sine of its angle.
            float tp = spec.SwellPeriodSeconds;
            float hb = WindWaves.BreakerDepth(WindWaves.BreakerHeight(spec.SwellHeightMetres, tp));
            float c = MathF.Sqrt(WindWaves.Gravity * MathF.Max(0.2f, hb));
            float s = MathF.Sin(spec.SwellAngleDegrees * MathF.PI / 180f);
            foreach (var col in _columns) col.SwellDelay = (col.Along + 0.5f * length) * s / c;
        }
        if (spec.Face == ShoreFace.Hull && spec.Hull != null) _hull = new HullPlate(spec.Hull, sampleRate, _places.Length);
    }

    /// <summary>Where place <paramref name="k"/> of <paramref name="places"/> sits along a line source
    /// of this length, m from its middle: the line cut into equal pieces, each place in the middle of its
    /// own, nearest the middle first (RunningWaterSynth.LineOffset's rule).</summary>
    public static float LineOffset(int k, int places, float lengthMetres)
    {
        if (places <= 1 || k <= 0) return 0f;
        float piece = lengthMetres / places;
        Span<float> centres = stackalloc float[places];
        for (int i = 0; i < places; i++) centres[i] = (i + 0.5f) * piece - 0.5f * lengthMetres;
        centres.Sort((a, b) => MathF.Abs(a) != MathF.Abs(b) ? MathF.Abs(a).CompareTo(MathF.Abs(b)) : b.CompareTo(a));
        return centres[Math.Min(k, places - 1)];
    }

    /// <summary>The places of a source, as offsets from its middle in its own frame: x along the edge,
    /// z out over the water (the break row).</summary>
    public static Vector3[] Layout(ShoreSpec spec, float lengthMetres)
    {
        int n = Math.Max(1, spec.Places), rows = spec.BreakRowMetres > 0f ? 2 : 1;
        var at = new Vector3[n * rows];
        for (int r = 0; r < rows; r++)
            for (int k = 0; k < n; k++)
                at[r * n + k] = new Vector3(LineOffset(k, n, lengthMetres), 0f, r * spec.BreakRowMetres);
        return at;
    }

    private static float BandHz(int b) => 250f * MathF.Pow(2f, b);

    /// <summary>Each of the two resonators' Q: together an octave wide at their half-power points.</summary>
    private const float BandQ = 2.2f;

    /// <summary>The cascade's rms for unit-rms white noise in, measured once.</summary>
    private static float CascadeGain(float hz, float rate)
    {
        Resonator a = default, b = default;
        a.Tune(hz, BandQ, rate); b.Tune(hz, BandQ, rate);
        var noise = new EventSum(rate, 1234);
        double sum = 0;
        const int n = 1 << 16;
        for (int i = 0; i < n + 4096; i++)
        {
            float y = b.Process(a.Process(1.7320508f * noise.Signed()));
            if (i >= 4096) sum += (double)y * y;
        }
        return (float)Math.Sqrt(sum / n);
    }

    /// <summary>For the lab: the sea now.</summary>
    public string Census()
        => $"  sea: wind sea Hs {_hs * 100:F1} cm Tp {_tp:F2} s (onshore {_onshore:F2}); swell {Spec.SwellHeightMetres * 100:F0} cm at {Spec.SwellPeriodSeconds:F0} s; " +
           $"waves {Waves}, breakers {Breakers}, pockets {Pockets}";

    private float _onshore;

    // ── Seconds-scale ────────────────────────────────────────────────────────────────────────────

    /// <summary>The sea following the wind, and the places' shares.</summary>
    public void Control(float dt)
    {
        var (hs, tp, onshore) = Spec.WindSea(Geometry, float.IsFinite(WindSpeed) ? WindSpeed : 0f, WindFromDegrees);
        _windSeaTarget = hs;
        _onshore = onshore;
        if (tp > 0f) _tpTarget = tp;
        if (float.IsNaN(_hs)) { _hs = hs; _tp = tp > 0f ? tp : 1f; }
        else
        {
            // A sea grows and dies over the time a wind takes to raise it (a third of the CEM's
            // duration), never faster than in ten seconds.
            float tau = MathF.Max(10f, WindWaves.GrowthSeconds(MathF.Max(WindSpeed, 2f), Geometry.FetchMetres) / 3f);
            float a = MathF.Min(1f, dt / tau);
            _hs += (_windSeaTarget - _hs) * a;
            if (_tpTarget > 0f) _tp += (_tpTarget - _tp) * a;
        }
        _toHome = Math.Clamp(Spread, 0f, 1f);
    }

    // ── Samples ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The next sample at each place, pascals at a metre from it. Their sum is the source.</summary>
    public void NextPlaces(Span<float> places)
    {
        Step();
        for (int k = 0; k < _places.Length; k++)
        {
            float y = _places[k].Next() + Noise(k);
            if (_hull != null) y += _hull.Next(k) * HullPart;
            if (k < places.Length) places[k] = y;
        }
        _blockAt++;
    }

    /// <summary>The next sample of the whole source at one point, pascals at a metre.</summary>
    public float Next()
    {
        Step();
        float y = 0f;
        for (int k = 0; k < _places.Length; k++)
        {
            y += _places[k].Next() + Noise(k);
            if (_hull != null) y += _hull.Next(k) * HullPart;
        }
        _blockAt++;
        return y;
    }

    private int _blockAt;

    private float Noise(int k)
    {
        float y = 0f;
        float x = 1.7320508f * _rng.Signed();
        float t = _blockAt / (float)Block;
        for (int b = 0; b < Bands; b++)
        {
            float g0 = _gain[k, b], g1 = _power[k, b];
            if (g0 <= 0f && g1 <= 0f) continue;
            float g = g0 + (g1 - g0) * t;
            y += _band2[k, b].Process(_band[k, b].Process(x)) * _bandNorm[b] * g;
        }
        return y;
    }

    private void Step()
    {
        if (--_untilBlock > 0) return;
        _untilBlock = Block;
        // The noise gains glide from last block's to this block's over the block.
        for (int k = 0; k < _places.Length; k++)
            for (int b = 0; b < Bands; b++)
            {
                _gain[k, b] = _power[k, b];
                _power[k, b] = 0f;
            }
        _blockAt = 0;
        float dt = Block / _rate;
        _time += dt;
        _hull?.BeginBlock();
        Seas(dt);
        RunProcs(dt);
        // Power to amplitude: what the noise is driven with this block.
        for (int k = 0; k < _places.Length; k++)
            for (int b = 0; b < Bands; b++)
                _power[k, b] = _power[k, b] > 0f ? MathF.Sqrt(_power[k, b]) : 0f;
    }

    // ── The water at each column ─────────────────────────────────────────────────────────────────

    private sealed class Column
    {
        public float Width, Along, SwellDelay;
        public int Swash, Break;
        public Train Wind = null!;
        public Train? Eddy;
        public float Prev, Crest = float.MinValue, Trough = float.MaxValue;
        public double LastUp = double.NaN;
        public float EddyPrev, EddyCrest = float.MinValue, EddyTrough = float.MaxValue;
        public double EddyLastUp = double.NaN;
    }

    /// <summary>
    /// A sea as a few dozen of its spectrum's components, each at its own frequency relative to the peak
    /// and its own phase: JONSWAP's shape for a wind sea (γ 3.3), a narrow Gaussian for a swell or an
    /// eddy train. Each component's frequency wanders a little inside its own slice of the spectrum, so
    /// the sum never repeats.
    /// </summary>
    private sealed class Train
    {
        public readonly float[] Rel, Amp, Re, Im, Detune;
        private readonly float[] _width;

        private Train(int n)
        {
            Rel = new float[n]; Amp = new float[n]; Re = new float[n]; Im = new float[n]; Detune = new float[n]; _width = new float[n];
        }

        public static Train Jonswap(int n, EventSum rng)
        {
            var t = new Train(n);
            float lo = MathF.Log(0.75f), hi = MathF.Log(2.6f);
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                float a = MathF.Exp(lo + (hi - lo) * i / n), b = MathF.Exp(lo + (hi - lo) * (i + 1) / n);
                float r = MathF.Sqrt(a * b);
                float sigma = r <= 1f ? 0.07f : 0.09f;
                float s = MathF.Pow(r, -5f) * MathF.Exp(-1.25f / (r * r * r * r))
                          * MathF.Pow(3.3f, MathF.Exp(-(r - 1f) * (r - 1f) / (2f * sigma * sigma)));
                t.Rel[i] = r; t._width[i] = b - a;
                t.Amp[i] = MathF.Sqrt(s * (b - a));
                sum += t.Amp[i] * t.Amp[i] / 2.0;
            }
            t.Normalise(sum, rng);
            return t;
        }

        public static Train Narrow(int n, float width, EventSum rng)
        {
            var t = new Train(n);
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                float x = -2.5f + 5f * (i + 0.5f) / n;
                t.Rel[i] = 1f + width * x;
                t._width[i] = width * 5f / n;
                t.Amp[i] = MathF.Exp(-0.25f * x * x);      // √ of a Gaussian
                sum += t.Amp[i] * t.Amp[i] / 2.0;
            }
            t.Normalise(sum, rng);
            return t;
        }

        private void Normalise(double sum, EventSum rng)
        {
            // Σ a²/2 = 1/16: the surface's variance for a significant height of one (Hm0 = 4 √m0).
            float k = (float)Math.Sqrt(1.0 / 16.0 / Math.Max(1e-12, sum));
            for (int i = 0; i < Amp.Length; i++)
            {
                Amp[i] *= k;
                float ph = MathF.Tau * rng.Uniform();
                Re[i] = MathF.Cos(ph); Im[i] = MathF.Sin(ph);
                Detune[i] = rng.Signed() * 0.4f;
            }
        }

        /// <summary>Advances every component by dt at a peak frequency, wandering, and returns the surface
        /// for a significant height of one.</summary>
        public float Advance(float peakHz, float dt, EventSum rng)
        {
            float eta = 0f;
            for (int i = 0; i < Amp.Length; i++)
            {
                Detune[i] = Math.Clamp(Detune[i] + 0.05f * rng.Signed(), -0.45f, 0.45f);
                float w = MathF.Tau * (Rel[i] + Detune[i] * _width[i]) * peakHz * dt;
                float c = MathF.Cos(w), s = MathF.Sin(w);
                float re = Re[i] * c - Im[i] * s;
                Im[i] = Re[i] * s + Im[i] * c;
                Re[i] = re;
                eta += Amp[i] * re;
            }
            return eta;
        }

        /// <summary>The surface a delay later than the train's own phase, without advancing it (a swell
        /// read by each column at its own lag): Σ a cos(φ − ω δ).</summary>
        public float At(float peakHz, float delay)
        {
            float eta = 0f;
            for (int i = 0; i < Amp.Length; i++)
            {
                float w = MathF.Tau * Rel[i] * peakHz * delay;
                eta += Amp[i] * (Re[i] * MathF.Cos(w) + Im[i] * MathF.Sin(w));
            }
            return eta;
        }

        /// <summary>Advances without wandering (a swell's phases are read at lags, so they keep time).</summary>
        public void Turn(float peakHz, float dt)
        {
            for (int i = 0; i < Amp.Length; i++)
            {
                float w = MathF.Tau * Rel[i] * peakHz * dt;
                float c = MathF.Cos(w), s = MathF.Sin(w);
                float re = Re[i] * c - Im[i] * s;
                Im[i] = Re[i] * s + Im[i] * c;
                Re[i] = re;
            }
        }
    }

    private void Seas(float dt)
    {
        float windHs = MathF.Max(0f, _hs), windHz = _tp > 0f ? 1f / _tp : 1f;
        float swellHz = 1f / MathF.Max(1f, Spec.SwellPeriodSeconds);
        _swell?.Turn(swellHz, dt);
        float eddyHz = Spec.CurrentMetresPerSecond > 0.05f
            ? 0.2f * Spec.CurrentMetresPerSecond / MathF.Max(0.05f, Spec.BankFeatureMetres) : 0f;
        // The surface rocked at the bank by an eddy: of the order of its velocity head, U² / 2g.
        float eddyHs = Spec.CurrentMetresPerSecond * Spec.CurrentMetresPerSecond / (2f * WindWaves.Gravity);
        foreach (var c in _columns)
        {
            float eta = windHs > 0f ? windHs * c.Wind.Advance(windHz, dt, _rng) : 0f;
            if (_swell != null) eta += Spec.SwellHeightMetres * _swell.At(swellHz, c.SwellDelay);
            Find(c, eta, ref c.Prev, ref c.Crest, ref c.Trough, ref c.LastUp, eddy: false);
            if (c.Eddy != null)
            {
                float e = eddyHs * c.Eddy.Advance(eddyHz, dt, _rng);
                Find(c, e, ref c.EddyPrev, ref c.EddyCrest, ref c.EddyTrough, ref c.EddyLastUp, eddy: true);
            }
        }
    }

    /// <summary>A wave is the water from one up-crossing to the next: its height crest to trough.</summary>
    private void Find(Column c, float eta, ref float prev, ref float crest, ref float trough, ref double lastUp, bool eddy)
    {
        crest = MathF.Max(crest, eta);
        trough = MathF.Min(trough, eta);
        if (prev < 0f && eta >= 0f)
        {
            if (!double.IsNaN(lastUp))
            {
                float period = (float)(_time - lastUp);
                float height = crest - trough;
                if (period > 0.15f && height > 0.002f) Arrive(c, height, period, eddy);
            }
            lastUp = _time;
            crest = float.MinValue;
            trough = float.MaxValue;
        }
        prev = eta;
    }

    // ── A wave arriving ──────────────────────────────────────────────────────────────────────────

    private void Arrive(Column c, float h0, float period, bool eddy)
    {
        Waves++;
        int at = (int)(_rng.Uniform() * Block);
        float w = c.Width;

        // Out on the water: a steep young sea breaks in whitecaps over the strip in front of the edge. A
        // young sea is short-crested: about one crest a wavelength across and a wavelength apart, each
        // breaking along a third of its length.
        if (!eddy && _hs > 0f && WhitecapPart > 0f)
        {
            float b = WindWaves.BreakingProbability(_hs, _tp, WindSpeed);
            float l = MathF.Max(0.2f, WindWaves.DeepWavelength(period));
            float crestSpeed = WindWaves.Gravity * period / (2f * MathF.PI);
            int n = crestSpeed > AirOnset ? _rng.Poisson(b * (WhitecapStripMetres / l) * (w / l)) : 0;
            for (int i = 0; i < n; i++)
            {
                float hb = h0 * (0.6f + 0.6f * _rng.Uniform());
                float air = SpillingAirShare * AirPerBreak * hb * hb * (0.3f * l) * Entrains(crestSpeed);
                StartCloud(c.Break, (int)(_rng.Uniform() * period * _rate), 0.5f * period, air, PlumeLargestMm(hb),
                           PlumeHeard(hb) * WhitecapPart);
            }
        }

        switch (Spec.Face)
        {
            case ShoreFace.Beach: Beach(c, h0, period, at, eddy); break;
            default: Face(c, h0, period, at); break;
        }
    }

    private void Beach(Column c, float h0, float period, int at, bool eddy)
    {
        float g = WindWaves.Gravity, w = c.Width;
        float slope = MathF.Max(0.005f, Spec.BeachSlope);
        float xi = WindWaves.Iribarren(slope, h0, period);
        float runUp = WindWaves.RunUp(slope, h0, period);
        // The bore at the still-water line runs up as a thrown body of water, u0 = √(2 g R).
        float swashSpeed = MathF.Sqrt(2f * g * MathF.Max(0f, runUp));
        float hb = Math.Clamp(WindWaves.BreakerHeight(h0, period), 0.5f * h0, 2f * h0);
        float breaksAt = WindWaves.BreakerDepth(hb) / slope;          // m out from the edge
        float crestSpeed = MathF.Sqrt(g * WindWaves.BreakerDepth(hb));
        bool breaks = !eddy && xi < 3.3f;
        // The bore runs in from the break at the shallow-water speed; it reaches the edge that much later.
        float travel = breaks && _rows > 1 ? breaksAt / MathF.Max(0.3f, MathF.Sqrt(g * WindWaves.BreakerDepth(hb) * 0.5f)) : 0f;
        int swashDelay = at + (int)(travel * _rate);
        float uprush = MathF.Max(0.1f, 0.35f * period), backwash = MathF.Max(0.15f, 0.65f * period);

        float plumeAir = 0f;
        if (breaks && crestSpeed > AirOnset)
        {
            Breakers++;
            bool plunging = xi >= 0.5f;
            plumeAir = (plunging ? 1f : SpillingAirShare) * AirPerBreak * hb * hb * w * Entrains(crestSpeed);
            // A plunger's jet folds a share of its air all at once, over the jet's fall √(2 Hb / g) and the
            // cavity's collapse after it. The rest, and all of a spiller's, is the roller: the broken crest
            // tumbling down the bore's face as it runs in across the surf zone, at the shallow-water speed
            // √(g h / 2) of its mid-depth, breaking all the way (a spilling surf zone holds two or more
            // broken waves at once, Battjes 1974). Out on the break line for the first half of its run,
            // at the edge for the second.
            float crossing = MathF.Max(0.8f * period, breaksAt / MathF.Max(0.3f, MathF.Sqrt(g * WindWaves.BreakerDepth(hb) * 0.5f)));
            float jetShare = plunging ? 0.5f : 0f;
            if (jetShare > 0f)
                StartCloud(c.Break, at, 2f * MathF.Sqrt(2f * hb / g) + 0.15f, jetShare * plumeAir, PlumeLargestMm(hb), PlumeHeard(hb) * PlumePart);
            float roller = (1f - jetShare) * plumeAir;
            StartCloud(c.Break, at, 0.5f * crossing, 0.5f * roller, PlumeLargestMm(hb), PlumeHeard(hb) * PlumePart);
            StartCloud(c.Swash, at + (int)(0.5f * crossing * _rate), 0.5f * crossing, 0.5f * roller, PlumeLargestMm(0.5f * hb), PlumeHeard(0.5f * hb) * PlumePart);
            CloudOscillation(c.Break, at, h0, period, hb, plunging, plumeAir, w);
            if (plunging && CrashPart > 0f)
            {
                // The jet lands: its water (a tenth of Hb² a metre of crest, the lip that is thrown) at the
                // speed it fell from the crest, as lumps up to a centimetre across.
                float jetSpeed = MathF.Sqrt(2f * g * hb);
                float lump = Math.Clamp(0.05f * hb, 0.002f, 0.01f);
                float water = 0.1f * hb * hb * w;
                float lumps = water / (4f / 3f * MathF.PI * lump * lump * lump);
                StartSpray(c.Break, at + (int)(MathF.Sqrt(2f * hb / g) * _rate), 0.12f + 0.3f * hb, lumps, lump, jetSpeed,
                           FallingWaterSynth.PoolCrownShare, CrashPart);
                // The air tube the jet closes on: its pulsation, as a large bubble that breaks up within
                // a few cycles.
                float tubeMm = 1000f * 0.08f * hb * (0.6f + 0.8f * _rng.Uniform());
                Pocket(c.Break, at + (int)((MathF.Sqrt(2f * hb / g) + 0.02f) * _rate), tubeMm, CrashPart * MathF.Sqrt(w), 0.3f);
            }
        }

        if (runUp <= 0f) return;
        // The swash's front: thin and fast, it breaks like the lee jet of a stone in a creek, folding air
        // under by how far its Froude number stands over 1.4 (RunningWaterSynth.BreakingAirShare). A
        // surging wave rises smoothly and does not.
        float front = MathF.Max(0.002f, 0.3f * (breaks ? MathF.Min(hb, h0) : h0) * 0.5f);
        if (breaks)
        {
            float fr = swashSpeed / MathF.Sqrt(g * front);
            float water = swashSpeed * front * w * uprush * 0.5f;
            float air = FrontShare * RunningWaterSynth.BreakingAirShare * water * MathF.Max(0f, fr - RunningWaterSynth.JumpOnsetFroude);
            if (air > 0f)
            {
                // As at a creek's stone, a share of the air closes in the front's own lip, as bubbles of about
                // its own size (RunningWaterSynth.SiteNoteShare): the plop of a lapping wave. The rest
                // breaks up into the Deane-Stokes sizes.
                StartCloud(c.Swash, swashDelay, uprush, RunningWaterSynth.SiteNoteShare * air, 0f, FrontPart, 1000f * front * LipNote);
                StartCloud(c.Swash, swashDelay, uprush, (1f - RunningWaterSynth.SiteNoteShare) * air, MathF.Min(4f, 500f * front), FrontPart);
            }
            if (swashSpeed > AirOnset)
            {
                float lump = Math.Clamp(0.5f * front, 0.001f, 0.01f);
                float lumps = w / (4f * lump);
                StartSpray(c.Swash, swashDelay, uprush, lumps, lump, swashSpeed,
                           Spec.Sediment == ShoreSediment.Rock ? FallingWaterSynth.RockCrownShare : FallingWaterSynth.PoolCrownShare, SprayPart);
            }
        }
        // The foam the bore carries bursts as it runs up and back.
        if (plumeAir > 0f)
            StartPops(c.Swash, swashDelay, uprush + backwash, FoamShare * plumeAir, 1f, 5f, FoamPart);
        // A rough edge (roots, stones, a bank's lip) catches some crests on air: the clop.
        if (_rng.Uniform() < Spec.TrapShare) TrapPocket(c, h0, period, swashDelay, PocketPart);

        float lens = 0.5f * runUp / MathF.Max(0.005f, slope) * front * w;   // the swash's water, m³
        switch (Spec.Sediment)
        {
            case ShoreSediment.Sand:
                // The swash soaks into the sand above the water table and drives its pore air out (Emery
                // 1945): bubbles pinching off at the sand's face under a few millimetres of water, each
                // ringing as it leaves, through the backwash: the fizz.
                StartCloud(c.Swash, swashDelay + (int)(0.5f * uprush * _rate), uprush + backwash, SandVentShare * lens, 1.5f, VentPart);
                break;
            case ShoreSediment.Gravel:
            case ShoreSediment.Shingle:
                Stones(c, runUp, slope, swashSpeed, swashDelay + (int)(uprush * _rate), backwash);
                break;
        }
    }

    /// <summary>
    /// A wave against a steep face. It is thrown back, and at the face the incident and reflected crests
    /// stand up to twice the incident amplitude, the surface rising and falling at 2π H / T. Where the
    /// face is rough (joints, a ladder, weed, the lands of a hull's planks) the rising crest is thrown up
    /// as a sheet that tears into lumps, and what was thrown falls back from the height it reached; a
    /// crest that is steep enough to break against the face strikes it at its own speed. Some crests close
    /// on air in a crack or under an overhang (<see cref="ShoreSpec.TrapShare"/>); a hull rings.
    /// </summary>
    private void Face(Column c, float h0, float period, int at)
    {
        float g = WindWaves.Gravity, w = c.Width;
        float rise = MathF.Tau * h0 / MathF.Max(0.2f, period);
        float steep = h0 / WindWaves.DeepWavelength(period);
        float speed = steep > 0.1f ? MathF.Max(rise, MathF.Sqrt(g * h0)) : rise;
        float sheet = Math.Clamp(0.1f * h0, 0.001f, 0.01f);
        float lumps = MathF.Min(400f, w * h0 / (sheet * sheet) * 0.05f);
        if (SprayPart > 0f && speed > 0.1f)
        {
            float crown = Spec.Face == ShoreFace.Hull ? FallingWaterSynth.PoolCrownShare : FallingWaterSynth.RockCrownShare;
            StartSpray(c.Swash, at, 0.15f + 0.2f * period, lumps, sheet, speed, crown, SprayPart);
        }
        if (SlapPart > 0f)
        {
            // What was thrown up falls back into the water from as high as the crest stood.
            float fall = MathF.Sqrt(2f * h0 / g);
            StartSpray(c.Swash, at + (int)((0.25f * period + 0.5f * fall) * _rate), 0.2f + fall + 0.2f * period, lumps, sheet,
                       MathF.Sqrt(g * h0), FallingWaterSynth.PoolCrownShare, SlapPart);
        }
        if (_rng.Uniform() < Spec.TrapShare) TrapPocket(c, h0, period, at, PocketPart);
        // The face the crest wetted runs off in drops for a while after: a few dozen a metre of face for
        // each ten centimetres the crest stood up it.
        StartDrips(c.Swash, at + (int)(0.5f * period * _rate), 1.5f * period, RunOffDrops * w * h0 / 0.1f, h0, SlapPart);
        if (_hull != null && HullPart > 0f)
        {
            // The crest's blow on the planking: its momentum over the bays it strikes, delivered over the
            // time the crest takes to pass its own thickness.
            float crestArea = MathF.PI / 8f * h0 * h0 * 0.25f;
            float impulse = 1000f * crestArea * MathF.Min(w, 0.5f) * speed;
            float tau = Math.Clamp(0.25f * h0 / MathF.Max(0.05f, speed), 0.002f, 0.05f);
            _hull.Blow(c.Swash, at, impulse, tau);
        }
    }

    /// <summary>
    /// The plume ringing as a whole. A bubble cloud of void fraction β and radius r rings at
    /// (1 / 2π r) √(3 γ p0 / (ρ β)), the cloud's own Minnaert note (Lu, Prosperetti and Yoon 1990; Carey
    /// and Fitzgerald 1993), heavily damped. A plume is not one cloud but several, broken up by the
    /// turbulence: sub-clouds of a twentieth to a quarter of Hb across, void fractions of a tenth to a
    /// third (Deane 1997: 0.3-0.4 fresh), so their notes fill 10-300 Hz (Lamarre and Melville 1994). Their
    /// energy is a share of the wave energy the breaker dissipates (ρ g H² L / 16 a metre of crest), less
    /// for a spiller, heard after the plume has formed (the low sound lags the breaking by up to a third of
    /// a period, Loewen and Melville 1994). Each radiated as a band of noise an octave wide.
    /// </summary>
    private void CloudOscillation(int place, int at, float h0, float period, float hb, bool plunging, float air, float width)
    {
        if (CloudPart <= 0f || air <= 0f) return;
        float energy = (plunging ? 1f : SpillingAirShare) * CloudEfficiency
                       * 1000f * WindWaves.Gravity * hb * hb * WindWaves.DeepWavelength(period) / 16f * width;
        const int clouds = 4;
        for (int i = 0; i < clouds; i++)
        {
            float radius = MathF.Max(0.005f, hb * (0.05f + 0.2f * _rng.Uniform()));
            float beta = 0.1f + 0.2f * _rng.Uniform();
            float hz = MathF.Sqrt(3f * 1.4f * 101325f / (1000f * beta)) / (MathF.Tau * radius);
            if (hz < 15f || hz > 0.4f * _rate) continue;
            float decay = MathF.Max(0.03f, 3f / hz), rise = MathF.Min(0.05f, 0.5f * decay);
            float t = rise / 3f + decay / 2f;
            float pascals = MathF.Sqrt(energy / clouds * 413f / (2f * MathF.PI * t)) * CloudPart;
            int delay = at + (int)((MathF.Min(0.5f, period / 6f) + 0.3f * period * _rng.Uniform()) * _rate);
            var sum = Place(place);
            if (delay >= sum.Horizon) delay = sum.Horizon - 1;
            sum.Burst(delay, rise, decay, pascals, hz / 1.41f, hz * 1.41f, steep: true);
        }
    }

    /// <summary>A crest closing on air against the edge: a pocket of a twentieth to a quarter of the wave's
    /// height across, ringing at its own note (Minnaert) as a cavity closing at the surface rings (the
    /// fountain's law), venting within a few cycles. Against a hull its pressure drives the planking.</summary>
    private void TrapPocket(Column c, float h0, float period, int at, float part)
    {
        if (part <= 0f && (_hull == null || HullPart <= 0f)) return;
        Pockets++;
        float u = _rng.Uniform();
        // Under a hull's flare the pocket is the flare's size, whatever the wave; against a rough face a
        // twentieth to a quarter of the wave's height.
        float mm = Spec.Hull is { } hull && h0 > 0.3f * hull.FlarePocketMetres
            ? 1000f * hull.FlarePocketMetres * (0.3f + 0.7f * u)
            : Math.Clamp(1000f * h0 * (0.05f + 0.2f * u * u), 1f, 150f);
        Pocket(c.Swash, at, mm, part, PocketDamping);
        if (_hull != null && HullPart > 0f)
        {
            // The pocket's pressure, the crest's dynamic pressure ρ v² / 2 at the rising speed, over its
            // own area.
            float speed = MathF.Max(0.1f, MathF.Tau * h0 / MathF.Max(0.2f, period));
            float pressure = 0.5f * 1000f * speed * speed;
            float area = MathF.PI * (mm * 1e-3f) * (mm * 1e-3f);
            _hull.Ring(c.Swash, at, FallingWaterSynth.MinnaertHzMetres / (mm * 1e-3f), MathF.Max(PocketDamping, FallingWaterSynth.BubbleDamping(mm)),
                       pressure * area * MathF.Pow(_rng.Uniform(), FallingWaterSynth.DepthSkew / 2f));
        }
    }

    /// <summary>A big bubble or pocket ringing, by the fountain's law for a cavity closing at the surface
    /// (its depth factor skewed), its note climbing as it rises.</summary>
    private void Pocket(int place, int at, float mm, float weight, float damping)
    {
        if (weight <= 0f) return;
        float depth = MathF.Pow(_rng.Uniform(), PocketSkew);
        if (depth < 0.01f) return;
        float hz = FallingWaterSynth.MinnaertHzMetres / (mm * 1e-3f);
        var sum = Place(place);
        sum.Bubble(Math.Min(at, sum.Horizon - 1), hz, MathF.Max(damping, FallingWaterSynth.BubbleDamping(mm)),
                   FallingWaterSynth.BubblePascalsPerMm * mm * depth * weight, 0.35f);
    }

    /// <summary>The backwash dragging the stones: those it can move (by how far its speed stands over
    /// the speed that lifts a stone of that size) roll down, a share of the surface layer at a time,
    /// each touching another once for every diameter it travels, at a fraction of its speed.</summary>
    private void Stones(Column c, float runUp, float slope, float swashSpeed, int delay, float duration)
    {
        if (StonePart <= 0f) return;
        float d = Spec.StoneMm * 1e-3f;
        // Shields' threshold for a stone in a thin fast sheet, u_c ≈ 1.4 √(g (s − 1) D) (an estimate of the
        // critical depth-averaged speed at a Shields number of 0.05).
        float uc = 1.4f * MathF.Sqrt(WindWaves.Gravity * 1.65f * d);
        float u = 0.7f * swashSpeed;
        if (u <= uc) return;
        float moving = MathF.Min(1f, StoneShare * ((u / uc) * (u / uc) - 1f));
        float area = runUp / MathF.Max(0.005f, slope) * c.Width;
        float stones = moving * area / (d * d);
        float collisions = stones * (0.5f * u / d) * duration * 0.5f;
        StartStones(c.Swash, delay, duration, collisions, 0.5f * d, StoneClosing * u, StonePart);
    }

    // ── Processes: a population of events over a stretch of time ─────────────────────────────────

    private enum Kind : byte { None, Cloud, Spray, Pops, Stones, Drips }

    private struct Proc
    {
        public Kind Kind;
        public int Place;
        public double Start, Length;      // s
        public float Total;               // events
        public float A, B, C, D;          // kind's own
        public float Weight;
        public float Flicker;
        public double NextFlicker;
        public float BinShare0, BinShare1, BinShare2, BinShare3, BinShare4, BinShare5, BinShare6;
    }

    private ref Proc NewProc(Kind kind, int place, int delaySamples, float seconds)
    {
        for (int i = 0; i < MaxProcs; i++)
        {
            if (_procs[i].Kind != Kind.None) continue;
            ref var p = ref _procs[i];
            p = default;
            p.Kind = kind;
            p.Place = place;
            p.Start = _time + delaySamples / (double)_rate;
            p.Length = MathF.Max(0.01f, seconds);
            p.Flicker = 1f;
            for (int b = 0; b < Bands; b++) _bandFlicker[i, b] = 1f;
            return ref p;
        }
        return ref _dropped;
    }

    private Proc _dropped;

    /// <summary>A cloud of bubbles: <paramref name="air"/> m³ of air broken into the Deane-Stokes sizes up
    /// to <paramref name="maxMm"/>, of which <paramref name="heard"/> is heard.</summary>
    private void StartCloud(int place, int delay, float seconds, float air, float maxMm, float heard, float noteMm = 0f)
    {
        if (air <= 0f || heard <= 0f) return;
        ref var p = ref NewProc(Kind.Cloud, place, delay, seconds);
        if (p.Kind == Kind.None) return;
        maxMm = MathF.Max(SmallestMm * 2f, maxMm);
        Span<float> share = stackalloc float[Bands];
        float meanVolume = noteMm > 0f ? NoteShares(noteMm, share) : CloudShares(maxMm, share);
        if (noteMm > 0f) maxMm = noteMm * 2.5f;
        p.Total = air / meanVolume;
        p.Weight = heard;
        p.A = maxMm;
        p.BinShare0 = share[0]; p.BinShare1 = share[1]; p.BinShare2 = share[2]; p.BinShare3 = share[3];
        p.BinShare4 = share[4]; p.BinShare5 = share[5]; p.BinShare6 = share[6];
    }

    /// <summary>Spray: lumps of radius <paramref name="radius"/> striking at <paramref name="speed"/>, each
    /// a splash by the fountain's law.</summary>
    private void StartSpray(int place, int delay, float seconds, float lumps, float radius, float speed, float crown, float part)
    {
        if (lumps <= 0f || part <= 0f || speed <= 0.3f) return;
        ref var p = ref NewProc(Kind.Spray, place, delay, seconds);
        if (p.Kind == Kind.None) return;
        p.Total = lumps;
        p.A = radius; p.B = speed; p.C = crown;
        p.Weight = part;
    }

    /// <summary>Bubbles bursting at the surface: <paramref name="air"/> m³ of them, radii between the two
    /// sizes (mm).</summary>
    private void StartPops(int place, int delay, float seconds, float air, float loMm, float hiMm, float part)
    {
        if (air <= 0f || part <= 0f) return;
        ref var p = ref NewProc(Kind.Pops, place, delay, seconds);
        if (p.Kind == Kind.None) return;
        float r = MathF.Sqrt(loMm * hiMm) * 1e-3f;
        p.Total = air / (4f / 3f * MathF.PI * r * r * r);
        p.A = loMm; p.B = hiMm;
        p.Weight = part;
    }

    /// <summary>Drops running off a wetted face and falling back into the water, from up to
    /// <paramref name="height"/> m.</summary>
    private void StartDrips(int place, int delay, float seconds, float drops, float height, float part)
    {
        if (drops <= 0f || part <= 0f) return;
        ref var p = ref NewProc(Kind.Drips, place, delay, seconds);
        if (p.Kind == Kind.None) return;
        p.Total = drops;
        p.A = MathF.Max(0.01f, height);
        p.Weight = part;
    }

    private void StartStones(int place, int delay, float seconds, float collisions, float radius, float closing, float part)
    {
        if (collisions <= 0f || part <= 0f) return;
        ref var p = ref NewProc(Kind.Stones, place, delay, seconds);
        if (p.Kind == Kind.None) return;
        p.Total = collisions;
        p.A = radius; p.B = closing;
        p.Weight = part;
    }

    /// <summary>The share of a process's events in [t0, t1): a quick rise over the first sixth, then a
    /// fall of three time constants over the rest.</summary>
    private static float Share(in Proc p, double t0, double t1)
    {
        double a = Math.Max(0, t0 - p.Start), b = Math.Min(p.Length, t1 - p.Start);
        if (b <= a) return 0f;
        double rise = p.Length / 6, tau = (p.Length - rise) / 3;
        double norm = rise / 2 + tau * (1 - Math.Exp(-3));
        double Cum(double x) => x <= rise ? x * x / (2 * rise) : rise / 2 + tau * (1 - Math.Exp(-(x - rise) / tau));
        return (float)((Cum(b) - Cum(a)) / norm);
    }

    private void RunProcs(float dt)
    {
        double t0 = _time - dt, t1 = _time;
        for (int i = 0; i < MaxProcs; i++)
        {
            ref var p = ref _procs[i];
            if (p.Kind == Kind.None) continue;
            if (t0 >= p.Start + p.Length) { p.Kind = Kind.None; continue; }
            if (t1 <= p.Start) continue;
            if (t0 >= p.NextFlicker)
            {
                // A burst of turbulence makes bubbles of every size at once, and each size also comes and
                // goes on its own: a common factor and one per band, log-normal, mean one.
                float sc = Flicker * MathF.Sqrt(FlickerCommon), sb = Flicker * MathF.Sqrt(1f - FlickerCommon);
                p.Flicker = MathF.Exp(sc * Gauss() - 0.5f * sc * sc);
                for (int b = 0; b < Bands; b++) _bandFlicker[i, b] = MathF.Exp(sb * Gauss() - 0.5f * sb * sb);
                p.NextFlicker = t0 + FlickerSeconds * (0.5 + _rng.Uniform());
            }
            float n = p.Total * Share(p, t0, t1) * p.Flicker;
            if (n <= 0f) continue;
            // Where in the block the process is running.
            int from = (int)Math.Clamp((p.Start - t0) * _rate, 0, Block - 1);
            switch (p.Kind)
            {
                case Kind.Cloud: Cloud(ref p, n, from, i); break;
                case Kind.Spray: Spray(ref p, n, from, i); break;
                case Kind.Pops: Pops(ref p, n, from, i); break;
                case Kind.Stones: Clicks(ref p, n, from, i); break;
                case Kind.Drips: Drips(ref p, n, from); break;
            }
        }
    }


    private EventSum Place(int place) => _places[HomePlace(place)];

    /// <summary>Where an event of a place is heard: there with the spread, otherwise at the middle
    /// (the middle of the edge, place 0; the break row's own middle is heard there too).</summary>
    private int HomePlace(int place)
    {
        if (place == 0) return 0;
        return _rng.Uniform() < _toHome ? place : 0;
    }

    private void AddPower(int place, int band, float pa2, int slot = -1)
    {
        if (pa2 <= 0f || band < 0 || band >= Bands) return;
        // Each band of a crowd comes and goes on its own as well as with the rest (Flicker).
        if (slot >= 0) pa2 *= _bandFlicker[slot, band];
        _power[HomePlace(place), band] += pa2;
    }

    private float BlockSeconds => Block / _rate;

    private void Cloud(ref Proc p, float n, int from, int slot)
    {
        Span<float> share = stackalloc float[Bands] { p.BinShare0, p.BinShare1, p.BinShare2, p.BinShare3, p.BinShare4, p.BinShare5, p.BinShare6 };
        for (int b = 0; b < Bands; b++)
        {
            float count = n * share[b] * p.Weight * _bandFlicker[slot, b];
            if (count <= 0f) continue;
            float mm = FallingWaterSynth.MinnaertHzMetres / BandHz(b) * 1e3f;
            if (mm > p.A * 1.42f) continue;
            if (count > SparseLimit)
                AddPower(p.Place, b, count * BubbleEnergy(mm) / BlockSeconds);
            else
            {
                int k = _rng.Poisson(count);
                for (int i = 0; i < k; i++)
                {
                    float r = mm * MathF.Pow(2f, _rng.Signed() * 0.5f);
                    int at = from + (int)(_rng.Uniform() * (Block - from));
                    FallingWaterSynth.Ring(Place(p.Place), at, r, 1f);
                }
            }
        }
    }

    private void Spray(ref Proc p, float n, int from, int slot)
    {
        float radius = p.A, speed = p.B, crown = p.C;
        var (rise, decay, pascals) = FallingWaterSynth.Splash(radius, speed, crown);
        float fc0 = FallingWaterSynth.SplashCentreHz(radius, speed);
        float count = n;
        if (count > SparseLimit)
        {
            // A crowd of splashes: their energy (p² over the burst, rise/3 + decay/2) in the bands round
            // their middle.
            float energy = count * pascals * pascals * (rise / 3f + decay / 2f) * p.Weight * p.Weight;
            int b = BandOf(fc0);
            AddPower(p.Place, b, 0.6f * energy / BlockSeconds, slot);
            AddPower(p.Place, b - 1, 0.2f * energy / BlockSeconds, slot);
            AddPower(p.Place, b + 1, 0.2f * energy / BlockSeconds, slot);
            return;
        }
        int k = _rng.Poisson(count);
        for (int i = 0; i < k; i++)
        {
            float fc = Math.Clamp(fc0 * MathF.Exp(0.7f * Gauss()), 400f, 0.4f * _rate);
            int at = from + (int)(_rng.Uniform() * (Block - from));
            Place(p.Place).Burst(at, rise, decay, pascals * p.Weight, fc / 1.6f, MathF.Min(fc * 1.6f, 0.45f * _rate), steep: true);
        }
    }

    private void Pops(ref Proc p, float n, int from, int slot)
    {
        float lo = MathF.Log(p.A), hi = MathF.Log(p.B);
        float count = n * p.Weight;
        if (count / 3f > SparseLimit)
        {
            float mid = MathF.Exp(0.5f * (lo + hi));
            float hz = PopHz(mid);
            if (hz > 0.45f * _rate) return;
            float amp = PopPascalsPerMm * mid;
            float energy = amp * amp / (4f * MathF.PI * PopDamping * hz);
            int b = BandOf(hz);
            AddPower(p.Place, b, 0.5f * count * energy / BlockSeconds, slot);
            AddPower(p.Place, b - 1, 0.25f * count * energy / BlockSeconds, slot);
            AddPower(p.Place, b + 1, 0.25f * count * energy / BlockSeconds, slot);
            return;
        }
        int k = _rng.Poisson(count);
        for (int i = 0; i < k; i++)
        {
            float mm = MathF.Exp(lo + (hi - lo) * _rng.Uniform());
            float hz = PopHz(mm);
            if (hz > 0.45f * _rate) continue;
            int at = from + (int)(_rng.Uniform() * (Block - from));
            // The note climbs as the hole opens (f ∝ √t): from about half its last note.
            Place(p.Place).Bubble(at, 0.5f * hz, PopDamping, PopPascalsPerMm * mm * (0.3f + 0.7f * _rng.Uniform()), 3f);
        }
    }

    /// <summary>
    /// Two stones meeting: a rigid sphere stopped in its Hertz contact time radiates its acceleration as
    /// a dipole, p ≈ ρ0 a³ Δv / (2 c r τ²) at r, a pulse τ wide (Koss and Alfredson 1973). For 3 cm
    /// flint closing at 0.3 m/s, τ is about 130 µs and the click 0.1 Pa at a metre.
    /// </summary>
    private void Clicks(ref Proc p, float n, int from, int slot)
    {
        float a = p.A, dv = p.B;
        float mass = StoneDensity * 4f / 3f * MathF.PI * a * a * a;
        float mEff = 0.5f * mass, rEff = 0.5f * a, eEff = StoneModulus / (2f * (1f - 0.25f * 0.25f));
        float tau = 2.87f * MathF.Pow(mEff * mEff / (rEff * eEff * eEff * MathF.Max(0.02f, dv)), 0.2f);
        float peak = 1.2f * a * a * a * dv / (2f * 343f * tau * tau);
        float sigma = tau / 4f;
        float count = n * p.Weight;
        if (count > SparseLimit)
        {
            // A derivative-of-Gaussian pulse: ∫p² = e √π σ / 2 · peak², its power at 1 / (2π σ) and either side.
            float energy = count * 2.41f * peak * peak * sigma;
            int b = BandOf(1f / (MathF.Tau * sigma));
            AddPower(p.Place, b, 0.5f * energy / BlockSeconds, slot);
            AddPower(p.Place, b - 1, 0.25f * energy / BlockSeconds, slot);
            AddPower(p.Place, b + 1, 0.25f * energy / BlockSeconds, slot);
            return;
        }
        int k = _rng.Poisson(count);
        for (int i = 0; i < k; i++)
        {
            int at = from + (int)(_rng.Uniform() * (Block - from));
            float v = MathF.Exp(0.5f * Gauss());
            Place(p.Place).Pulse(at, sigma, peak * v * v);
        }
    }

    /// <summary>
    /// A drop off the wet face into the water below: its blow on the water, built over a fifth of its r / v
    /// as the running water's drips are (no sample-sharp ticks), and usually the bubble its crater closes
    /// on, about a fifth of its radius (Phillips, Agarwal and Jordan 2018; RunningWaterSynth.Drop).
    /// </summary>
    private void Drips(ref Proc p, float n, int from)
    {
        int k = _rng.Poisson(n);
        for (int i = 0; i < k; i++)
        {
            float r = (1.2f + 1.3f * _rng.Uniform()) * 1e-3f;
            float v = FallingWaterSynth.ArrivalSpeed(r, p.A * (0.2f + 0.8f * _rng.Uniform()));
            int at = from + (int)(_rng.Uniform() * (Block - from));
            var place = Place(p.Place);
            float impact = FallingWaterSynth.ImpactPascals * MathF.Pow(r / 1e-3f * v / 5f, 1.5f) * p.Weight;
            float rise = MathF.Max(16e-6f, 0.2f * r / v);
            place.Impact(at, rise, r / v, impact * MathF.Sqrt(16e-6f / rise));
            if (_rng.Uniform() < 0.6f)
                FallingWaterSynth.Ring(place, at + (int)(0.004f * _rate), r * 1e3f * (0.25f + 0.3f * _rng.Uniform()), p.Weight);
        }
    }

    private static int BandOf(float hz) => Math.Clamp((int)MathF.Round(MathF.Log2(MathF.Max(1f, hz) / 250f)), 0, Bands - 1);

    /// <summary>A plume bubble's energy at a metre, Pa²·s: its first peak by the fountain's law with the
    /// depth skew's mean square (E u^8 = 1/9), over 4 d.</summary>
    private static float BubbleEnergy(float mm)
    {
        float a = FallingWaterSynth.BubblePascalsPerMm * mm;
        return a * a / 9f / (4f * FallingWaterSynth.BubbleDecay(mm));
    }

    /// <summary>The Deane-Stokes spectrum (R^-3/2 under the Hinze scale, R^-10/3 over it) from the smallest
    /// bubble to <paramref name="maxMm"/>: each octave band's share of the count, and the mean volume, m³.</summary>
    /// <summary>Bubbles about one size (log-normal, σ 0.35): each octave band's share of the count, and the
    /// mean volume, m³.</summary>
    private static float NoteShares(float noteMm, Span<float> share)
    {
        double total = 0, volume = 0;
        Span<double> counts = stackalloc double[Bands];
        const double sigma = 0.35;
        for (int b = 0; b < Bands; b++)
        {
            double mid = 3.26 / (250.0 * Math.Pow(2, b)) * 1e3;
            double x = Math.Log(mid / noteMm) / sigma;
            double c = Math.Exp(-0.5 * x * x);
            counts[b] = c;
            total += c;
            volume += c * 4.0 / 3.0 * Math.PI * Math.Pow(mid * 1e-3, 3);
        }
        for (int b = 0; b < Bands; b++) share[b] = total > 0 ? (float)(counts[b] / total) : 0f;
        return total > 0 ? (float)(volume / total) : 1e-9f;
    }

    private static float CloudShares(float maxMm, Span<float> share)
    {
        double total = 0, volume = 0;
        Span<double> counts = stackalloc double[Bands];
        for (int b = 0; b < Bands; b++)
        {
            double mid = 3.26 / (250.0 * Math.Pow(2, b)) * 1e3;
            double lo = Math.Max(SmallestMm, mid / Math.Sqrt(2)), hi = Math.Min(maxMm, mid * Math.Sqrt(2));
            if (hi <= lo) { counts[b] = 0; continue; }
            double c = 0;
            const int steps = 8;
            for (int s = 0; s < steps; s++)
            {
                double r = lo * Math.Pow(hi / lo, (s + 0.5) / steps);
                double dens = r < HinzeMm ? Math.Pow(r, -1.5) : Math.Pow(HinzeMm, -1.5) * Math.Pow(r / HinzeMm, -10.0 / 3.0);
                double dr = r * Math.Log(hi / lo) / steps;
                c += dens * dr;
                volume += dens * dr * 4.0 / 3.0 * Math.PI * Math.Pow(r * 1e-3, 3);
            }
            counts[b] = c;
            total += c;
        }
        for (int b = 0; b < Bands; b++) share[b] = total > 0 ? (float)(counts[b] / total) : 0f;
        return total > 0 ? (float)(volume / total) : 1e-9f;
    }

    private float Gauss()
        => MathF.Sqrt(-2f * MathF.Log(MathF.Max(1e-7f, _rng.Uniform()))) * MathF.Cos(MathF.Tau * _rng.Uniform());

    // ── The hull ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One bay of planking per place, ringing at its modes (HullSpec.WetModeHz: RainPlate's modes,
    /// lowered by the water on the wetted share), each mode a resonator driven by the force on the bay.
    /// A blow hands each mode the share of its momentum its spectrum has at the mode's note
    /// (RainPlate.BlowMagnitude); a pocket's pressure rings at its own note and drives the modes near it.
    /// The bay's velocity radiates as a baffled plate, p = ρc √(σ S / 8π) v at a metre.
    /// </summary>
    private sealed class HullPlate
    {
        private const int MaxModes = 16;
        private readonly int _modes;
        private readonly float[] _hz = new float[MaxModes], _gain = new float[MaxModes], _shape = new float[MaxModes];
        private readonly float[] _cw = new float[MaxModes], _sw = new float[MaxModes], _decay = new float[MaxModes];
        private readonly float[] _mass = new float[MaxModes];
        private readonly float[,] _re, _im;
        private readonly float[,] _force;
        private readonly float _rate;
        private int _at;
        // Pending pocket drives: place, start offset, note, decay, amplitude (N).
        private const int MaxDrives = 32;
        private readonly int[] _dPlace = new int[MaxDrives];
        private readonly float[] _dPhase = new float[MaxDrives], _dStep = new float[MaxDrives], _dAmp = new float[MaxDrives], _dDecay = new float[MaxDrives];
        private readonly int[] _dWait = new int[MaxDrives];
        private int _drives;

        public HullPlate(HullSpec spec, float rate, int places)
        {
            _rate = rate;
            var plate = spec.Plate;
            float s = spec.BayAlongMetres * spec.BayUpMetres;
            int count = 0;
            for (int m = 1; m <= 6; m++)
                for (int n = 1; n <= 6; n++)
                {
                    float hz = spec.WetModeHz(m, n);
                    if (hz < 30f || hz > 0.4f * rate) continue;
                    int at = count;
                    while (at > 0 && _hz[at - 1] > hz) at--;
                    if (at >= MaxModes) continue;
                    for (int j = Math.Min(count, MaxModes - 1); j > at; j--) { _hz[j] = _hz[j - 1]; _shape[j] = _shape[j - 1]; _mass[j] = _mass[j - 1]; }
                    float k = MathF.PI * MathF.Sqrt(m * m / (spec.BayAlongMetres * spec.BayAlongMetres) + n * n / (spec.BayUpMetres * spec.BayUpMetres));
                    _hz[at] = hz;
                    // Struck low on the bay, near the waterline: a third of the way along, a quarter up.
                    _shape[at] = MathF.Sin(m * MathF.PI / 3f) * MathF.Sin(n * MathF.PI / 4f);
                    _mass[at] = (plate.SurfaceDensity + spec.WettedShare * HullSpec.AddedMass(k)) * s / 4f;
                    if (count < MaxModes) count++;
                }
            _modes = count;
            for (int i = 0; i < _modes; i++)
            {
                float w = MathF.Tau * _hz[i] / rate;
                _cw[i] = MathF.Cos(w); _sw[i] = MathF.Sin(w);
                _decay[i] = MathF.Exp(-MathF.PI * _hz[i] * plate.Loss(_hz[i]) / rate);
                _gain[i] = 413f * MathF.Sqrt(plate.RadiationEfficiency(_hz[i]) * s / (8f * MathF.PI));
            }
            _re = new float[places, MaxModes];
            _im = new float[places, MaxModes];
            _force = new float[places, Block];
        }

        public void BeginBlock()
        {
            _at = 0;
            Array.Clear(_force);
            // The pockets' pressure as force, sample by sample, into this block.
            for (int d = 0; d < _drives; d++)
            {
                for (int i = 0; i < Block; i++)
                {
                    if (_dWait[d] > 0) { _dWait[d]--; continue; }
                    _force[_dPlace[d], i] += _dAmp[d] * MathF.Sin(_dPhase[d]);
                    _dPhase[d] += _dStep[d];
                    _dAmp[d] *= _dDecay[d];
                }
            }
            int keep = 0;
            for (int d = 0; d < _drives; d++)
            {
                if (_dAmp[d] < 1e-6f) continue;
                _dPlace[keep] = _dPlace[d]; _dPhase[keep] = _dPhase[d]; _dStep[keep] = _dStep[d];
                _dAmp[keep] = _dAmp[d]; _dDecay[keep] = _dDecay[d]; _dWait[keep] = _dWait[d];
                keep++;
            }
            _drives = keep;
        }

        /// <summary>A blow of this momentum (N·s) lasting about τ, at a sample of the coming block.</summary>
        public void Blow(int place, int at, float impulse, float tau)
        {
            if (place < 0 || place >= _re.GetLength(0)) return;
            for (int i = 0; i < _modes; i++)
                _re[place, i] += impulse * _shape[i] * RainPlate.BlowMagnitude(tau, _hz[i]) / _mass[i];
        }

        /// <summary>A pocket's pressure ringing at its note, as a force of this amplitude (N) on the bay.</summary>
        public void Ring(int place, int at, float hz, float damping, float newtons)
        {
            if (_drives >= MaxDrives || hz <= 0f || hz > 0.4f * _rate) return;
            int d = _drives++;
            _dPlace[d] = place; _dPhase[d] = 0f; _dStep[d] = MathF.Tau * hz / _rate;
            _dAmp[d] = newtons; _dDecay[d] = MathF.Exp(-MathF.PI * damping * hz / _rate);
            _dWait[d] = Math.Max(0, at);
        }

        /// <summary>The bay's sound at this place, Pa at a metre; called once per place per sample.</summary>
        public float Next(int place)
        {
            float f = _force[place, Math.Min(_at, Block - 1)];
            if (place == _re.GetLength(0) - 1) _at++;
            float y = 0f;
            for (int i = 0; i < _modes; i++)
            {
                // Velocity of the mode: a damped rotation, kicked by the force over its modal mass.
                float re = _re[place, i] + f * _shape[i] / (_mass[i] * _rate);
                float im = _im[place, i];
                y += _gain[i] * re;
                float nr = (re * _cw[i] - im * _sw[i]) * _decay[i];
                _im[place, i] = (re * _sw[i] + im * _cw[i]) * _decay[i];
                _re[place, i] = nr;
            }
            return y;
        }
    }
}
