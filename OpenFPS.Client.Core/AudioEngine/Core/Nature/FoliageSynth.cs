using System;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// The wind in a tree, from the tree.
///
/// LEAVES. A leaf on a stalk in moving air flutters, at a rate of the order of the wind speed over its
/// own length, and fluttering leaves touch their neighbours. Each touch is a strike on a light,
/// heavily damped membrane: a click with a short scratch after it, in the low kilohertz. A crown
/// holds tens of thousands of leaves, so the strikes run to thousands a second and merge into the
/// rustle — but they are still separate strikes, which is what makes a rustle a rustle and not a hiss.
/// How many touch, and how hard, both grow with the wind, and below a breath of air none do.
///
/// BRANCHES. The leaves ride on branches, and branches swing on their own frequencies, the crown at
/// a fraction of a hertz and the outer boughs at a few. A branch swinging into the wind carries its
/// leaves through the air faster, so the rustle comes in surges from one part of the crown and then
/// another, never as a steady wash.
///
/// TWIGS. Between the bough and the leaf is the twig, a cluster of a dozen or two leaves, and that is
/// the scale the rustle actually comes in: an eddy a few tens of centimetres across hits a twig, its
/// leaves clatter against each other for a tenth or a fifth of a second, and it settles. A recording
/// of leaves in wind is made of those patches, each a little higher or lower than the last for the
/// size of its leaves, over a sparse scatter of single ticks — not of a steady rain of strikes, which
/// was the first version of this and sounded like a hiss.
///
/// THE AIR ITSELF. Flow past anything round sheds vortices at f = St U / d, St ≈ 0.2. Over twigs a
/// few millimetres thick at a few metres a second that is a couple of hundred hertz: the low whoosh
/// under a rustle. Over pine needles a millimetre and a half thick it is near a kilohertz, and with
/// no leaves to flutter it is nearly all a conifer makes: the sough. Over a rigid cylinder that is a
/// dipole whose pressure goes as the cube of the speed; a crown is not rigid. Leaves and twigs fold
/// and streamline as the wind rises (Vogel 1984: the drag on a plant goes as U^(2+V), V about −0.7),
/// so the force on them — and the sound of it, and the push on the boughs — grows far more slowly,
/// and the whole crown keeps to the 30-36 dB a decade Fégeant measured.
///
/// WHAT IS FITTED: how loud one leaf strike is, and how loud the shedding is, both at 5 m/s, set on
/// 2026-10-04 so a broadleaf crown of 400 m³ in a 4 m/s breeze makes about 50 dB(A) close to it —
/// Fégeant's model (as tabled by Heutschi, Pieren and Müller 2014) puts an oak's 100 m³ at 44 dB(A)
/// from 5 m in 5 m/s. The counts and the laws come from the tree and the wind.
/// </summary>
public sealed class FoliageSynth
{
    // ── The fitted constants ─────────────────────────────────────────────────────────────────────

    /// <summary>One leaf strike's peak, Pa at a metre from the crown, for a 40 cm² leaf in a 5 m/s wind.</summary>
    public const float LeafStrikePascals = 0.00075f;

    /// <summary>The shedding noise, rms Pa at a metre, for a crown of 50 m² of leaf at 5 m/s.</summary>
    public const float SheddingPascals = 0.00225f;

    // ── The tree's laws ──────────────────────────────────────────────────────────────────────────

    /// <summary>Vogel's exponent: the drag on a crown as U^(2+V). Leaves reconfigure.</summary>
    private const float Vogel = -0.7f;
    /// <summary>Strouhal number of a cylinder in cross-flow.</summary>
    private const float Strouhal = 0.2f;
    /// <summary>A leaf flutters at about this many times the wind speed over its length.</summary>
    private const float FlutterPerSpeedOverLength = 0.12f;
    /// <summary>The share of a crown's leaves that strike a neighbour in one flutter cycle.</summary>
    private const float TouchShare = 0.04f;
    /// <summary>How the strike's force grows with the speed the leaf meets its neighbour at:
    /// impact pressure as the closing speed to this power. With the strike rate going as the speed
    /// too, the power goes as U^3.4 — 34 dB a decade, inside Fégeant's 30 (oak) to 36 (birch).</summary>
    private const float StrikeSpeedPower = 1.2f;
    /// <summary>How many separately swinging boughs a crown is split into.</summary>
    public const int Boughs = 6;

    private const int Block = 128;
    private const int MaxPerBlock = 40;

    /// <summary>The share of the strikes that come in twig episodes rather than singly.</summary>
    private const float EpisodeShare = 0.9f;
    /// <summary>How many leaves a twig carries: "a dozen or two".</summary>
    private const float LeavesPerTwig = 16f;
    /// <summary>How often a fluttering leaf touches a neighbour, per flutter cycle: once each way.</summary>
    private const float TouchesPerFlutter = 2f;

    private struct Episode
    {
        public float Left, Rate, Scale, Tone;
    }
    private readonly Episode[] _episodes = new Episode[128];

    public readonly FoliageSpec Spec;
    private readonly float _rate;
    private readonly EventSum _sum;
    private readonly float _leaves;          // how many leaves on the tree
    private readonly float _leafLength;      // m
    private readonly float _leafScale;       // strike size for this leaf
    private readonly float _shedScale;       // shedding size for this crown

    /// <summary>The wind at the crown, m/s: setting it sets every bough's. A voice that can read the
    /// field at each bough's own place uses <see cref="ReadWind"/> instead.</summary>
    public float Wind
    {
        get => _boughWindIn[0];
        set { for (int b = 0; b < Boughs; b++) _boughWindIn[b] = value; }
    }

    /// <summary>Reads the wind field at each bough's own place, for a tree whose crown's middle is at
    /// (x, z) on the map. A crown is metres across, and a gust crosses it in a second or two (its
    /// diameter over the wind speed), so the boughs on its upwind side take the gust up before the
    /// ones on the far side and the crown as a whole takes it up over that crossing — not in the
    /// fifth of a second one leaf does. Reading the wind at the crown's middle for all of them made
    /// every gust arrive everywhere at once, and its swell was heard as a change of setting rather
    /// than as air moving through a tree.</summary>
    public void ReadWind(float x, float z, double seconds)
    {
        var (dx, dz) = WindField.Downwind;
        for (int b = 0; b < Boughs; b++)
        {
            float along = BoughAlongMetres(Spec, b);
            _boughWindIn[b] = WindField.SpeedAt(x + dx * along, Spec.CrownHeightMetres, z + dz * along, seconds);
        }
    }

    /// <summary>The wind last handed to bough <paramref name="b"/>, m/s.</summary>
    public float BoughWind(int b) => _boughWindIn[b];

    /// <summary>Where bough <paramref name="b"/> sits along the wind, m from the crown's middle:
    /// spread evenly over the crown's diameter, upwind first.</summary>
    public static float BoughAlongMetres(FoliageSpec spec, int b)
        => spec.CrownRadiusMetres * (2f * (b + 0.5f) / Boughs - 1f);

    /// <summary>Each part's share, for the lab to take the tree apart by muting. One in the game.</summary>
    public float LeafPart = 1f, ShedPart = 1f;
    private readonly float[] _boughWindIn = new float[Boughs];
    private readonly float[] _wind = new float[Boughs];

    // A bough: a damped oscillator driven by the wind's buffeting.
    private readonly float[] _x = new float[Boughs], _v = new float[Boughs], _hz = new float[Boughs];
    private readonly float[] _push = new float[Boughs], _pushTarget = new float[Boughs], _pushClock = new float[Boughs];
    private readonly float[] _agitation = new float[Boughs];

    private Resonator _shed, _shedHigh;
    private float _shedHz, _shedTargetHz, _shedLevel, _shedGain, _shedNorm, _shedHighNorm;
    private readonly float _shedGlide;
    private int _untilBlock, _samples;

    public FoliageSynth(FoliageSpec spec, float sampleRate, int seed)
    {
        Spec = spec;
        _rate = sampleRate;
        _sum = new EventSum(sampleRate, seed);
        float crownArea = MathF.PI * spec.CrownRadiusMetres * spec.CrownRadiusMetres;
        float leafArea = MathF.Max(0.01f, spec.LeafAreaCm2) * 1e-4f;
        _leaves = spec.Leaves == LeafKind.Broadleaf ? spec.LeafAreaIndex * crownArea / leafArea : 0f;
        _leafLength = MathF.Sqrt(leafArea * 1.6f);
        // A bigger leaf is a bigger plate, heavier and a better radiator: the strike goes as its size.
        _leafScale = MathF.Sqrt(spec.LeafAreaCm2 / 40f);
        // The shedding is a sum of incoherent dipoles over everything in the crown: its power goes as
        // the leaf area that stands for how much there is.
        _shedScale = MathF.Sqrt(spec.LeafAreaIndex * crownArea / 50f);
        for (int b = 0; b < Boughs; b++)
            _hz[b] = spec.SwayHz * (1f + b * 0.9f) * (0.85f + 0.3f * _sum.Uniform());
        _shedHz = _shedTargetHz = Strouhal * 4f / (spec.ShedDiameterMm * 1e-3f);
        _shed.Tune(_shedHz, 0.7f, sampleRate);
        _shedHigh.Tune(_shedHz * 3f, 0.6f, sampleRate);
        _shedNorm = 0.8f / Resonator.NoiseGain(_shedHz, 0.7f, sampleRate);
        _shedHighNorm = 0.35f / Resonator.NoiseGain(_shedHz * 3f, 0.6f, sampleRate);
        _shedGlide = 1f - MathF.Exp(-1f / (0.005f * sampleRate));
    }

    /// <summary>How long an eddy holds a twig: a tenth to a third of a second, this on average.</summary>
    private const float MeanEpisodeSeconds = 0.225f;

    /// <summary>How fast a leaf flutters at this wind, Hz.</summary>
    private float Flutter(float wind) => FlutterPerSpeedOverLength * MathF.Max(0f, wind) / _leafLength;

    /// <summary>Leaf strikes a second at this wind, for the lab.</summary>
    public float StrikesPerSecond(float wind)
    {
        float flutter = Flutter(wind);
        float moving = Math.Clamp((wind - Spec.StillSpeed) / MathF.Max(0.5f, Spec.StillSpeed * 2f), 0f, 1f);
        return _leaves * TouchShare * flutter * moving;
    }

    public void Control(float dt)
    {
        // The boughs. The wind pushes each with a drag that goes as the speed squared, and the
        // buffeting — the gusts inside the gust — is a force that changes every fraction of a second.
        for (int b = 0; b < Boughs; b++)
        {
            // A bough's leaves take up a gust fast and let it go slowly: Heutschi, Pieren and Müller
            // (2014) shape the envelope with a 0.2 s rise and a 1 s fall.
            float tau = _boughWindIn[b] > _wind[b] ? 0.2f : 1.0f;
            _wind[b] += (_boughWindIn[b] - _wind[b]) * MathF.Min(1f, dt / tau);

            _pushClock[b] -= dt;
            if (_pushClock[b] <= 0f)
            {
                _pushTarget[b] = _sum.Signed();
                _pushClock[b] = 0.15f + 0.6f * _sum.Uniform();
            }
            _push[b] += (_pushTarget[b] - _push[b]) * MathF.Min(1f, dt * 5f);
            float w = MathF.Tau * _hz[b];
            // Drag on a crown that folds as it is pushed: U^1.3, set to what U² gave at 5 m/s.
            float force = 8.75f * MathF.Pow(_wind[b] / 5f, 2f + Vogel) * _push[b];
            // A bough is heavily damped by its own leaves: ζ about 0.15.
            int steps = Math.Max(1, (int)(dt * 200f));
            float h = dt / steps;
            for (int k = 0; k < steps; k++)
            {
                float acc = force - 2f * 0.15f * w * _v[b] - w * w * _x[b];
                _v[b] += acc * h;
                _x[b] += _v[b] * h;
            }
            // How fast this bough's leaves are going through the air: the wind plus its own swing.
            _agitation[b] = MathF.Max(0f, _wind[b] + 1.5f * MathF.Abs(_v[b]));
        }

        // The shedding follows the mean of the crown's motion through the air.
        float mean = 0f;
        for (int b = 0; b < Boughs; b++) mean += _agitation[b];
        mean /= Boughs;
        // It glides to the note the crown's speed sets, retuned every call: the resonators had been
        // retuned in jumps of 3 % at a time, each a small step in the colour of the hiss.
        _shedTargetHz = Math.Clamp(Strouhal * mean / (Spec.ShedDiameterMm * 1e-3f), 20f, 6000f);
        _shedHz += (_shedTargetHz - _shedHz) * MathF.Min(1f, dt / 0.25f);
        _shed.Tune(_shedHz, 0.7f, _rate);
        _shedHigh.Tune(_shedHz * 3f, 0.6f, _rate);
        _shedNorm = 0.8f / Resonator.NoiseGain(_shedHz, 0.7f, _rate);
        _shedHighNorm = 0.35f / Resonator.NoiseGain(_shedHz * 3f, 0.6f, _rate);
        // The fluctuating force's dipole: pressure as the force times the speed it changes at,
        // U^(2+V) times U^(1/2) for the flutter's share — U^1.8, power U^3.6, Fégeant's birch.
        float u = mean / 5f;
        _shedLevel = SheddingPascals * _shedScale * MathF.Pow(u, 2.5f + Vogel);
    }

    public float Next()
    {
        if (--_untilBlock <= 0)
        {
            _untilBlock = Block;
            Schedule(Block / _rate);
        }
        _samples++;
        // The level is set once a control call; glide to it over a few milliseconds so it never steps.
        _shedGain += (_shedLevel - _shedGain) * _shedGlide;
        float n = _sum.Signed() * 1.7320508f;
        float shed = (_shed.Process(n) * _shedNorm + _shedHigh.Process(n) * _shedHighNorm) * _shedGain;
        return shed * ShedPart + _sum.Next();
    }

    private void Schedule(float dt)
    {
        if (_leaves <= 0f) return;
        for (int b = 0; b < Boughs; b++)
        {
            float speed = _agitation[b];
            float strikes = StrikesPerSecond(speed) / Boughs;
            // The closing speed of two fluttering leaves is a fraction of the wind through them.
            float closing = MathF.Pow(MathF.Max(0f, speed) / 5f, StrikeSpeedPower);

            // Twigs set going. While an eddy holds a twig, each of its leaves touches a neighbour
            // once each way every flutter cycle, so an episode strikes at the twig's leaves times
            // twice the flutter rate — about 160 a second in a breeze, some thirty-five strikes in
            // all. It had been 400 strikes an episode, ten times what a twig's leaves can make, so
            // the rustle came as a few loud patches a second that could each be heard arriving.
            float episodeRate = LeavesPerTwig * TouchesPerFlutter * Flutter(speed);
            int starts = episodeRate > 0f ? _sum.Poisson(strikes * EpisodeShare / (episodeRate * MeanEpisodeSeconds) * dt) : 0;
            for (int k = 0; k < starts; k++)
            {
                for (int e = 0; e < _episodes.Length; e++)
                {
                    if (_episodes[e].Left > 0f) continue;
                    float length = 0.1f + 0.25f * _sum.Uniform();
                    _episodes[e] = new Episode
                    {
                        Left = length,
                        Rate = episodeRate,
                        // How hard this eddy hit, and how big this twig's leaves are.
                        Scale = closing * (0.4f + 1.2f * _sum.Uniform()),
                        Tone = 0.6f + 1.0f * _sum.Uniform(),
                    };
                    break;
                }
            }

            // And the single ticks between.
            Strikes(strikes * (1f - EpisodeShare) * dt, closing, 1f);
        }
        for (int e = 0; e < _episodes.Length; e++)
        {
            ref var ep = ref _episodes[e];
            if (ep.Left <= 0f) continue;
            // An episode rises and dies away over its length.
            Strikes(ep.Rate * dt, ep.Scale, ep.Tone);
            ep.Left -= dt;
        }
    }

    private void Strikes(float mean, float scale, float tone)
    {
        int real = _sum.Poisson(mean);
        if (real == 0) return;
        int n = Math.Min(real, MaxPerBlock / 4);
        float weight = MathF.Sqrt(real / (float)n);
        for (int k = 0; k < n; k++)
        {
            int at = (int)(_sum.Uniform() * Block);
            // Most touches are glancing and a few are square: the strike sizes are skewed.
            float u = _sum.Uniform();
            float p = LeafStrikePascals * _leafScale * scale * weight * (0.15f + 3f * u * u * u) * LeafPart;
            // The tap: a light plate stopped over a few tenths of a millisecond...
            _sum.Pulse(at, 70e-6f * (0.7f + 0.6f * _sum.Uniform()) * _leafScale / tone, p);
            // ...and the leaf's own membrane ringing, damped almost at once, with the scrape of
            // edge over edge as they part.
            _sum.Burst(at + 3, 0.0002f, 0.0006f + 0.0012f * _sum.Uniform(), 0.35f * p,
                       900f * tone / _leafScale, 7000f * tone / MathF.Sqrt(_leafScale));
        }
    }
}
