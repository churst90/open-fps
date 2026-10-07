using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// The wind in a tree, from the tree.
///
/// Leaves flutter at about the wind speed over their length and strike their neighbours: each a click
/// and a short scratch on a light, damped membrane, in the low kilohertz. Thousands a second merge
/// into the rustle, but stay separate strikes, which is what makes it a rustle and not a hiss. Below a
/// breath of air none touch.
///
/// Boughs swing on their own frequencies (the crown at a fraction of a hertz, the outer boughs at a
/// few) and carry their leaves faster into the wind, so the rustle surges from one part of the crown
/// and then another. The rustle comes at the scale of the twig, a dozen or two leaves: an eddy hits a
/// twig, its leaves clatter for a tenth to a fifth of a second, and it settles; those patches over a
/// sparse scatter of single ticks. A steady rain of strikes sounds like a hiss.
///
/// The air itself sheds vortices at f = St U / d, St ≈ 0.2: a couple of hundred hertz over twigs, the
/// whoosh under a rustle; near a kilohertz over pine needles, nearly all a conifer makes (the sough).
/// Leaves and twigs fold as the wind rises (Vogel 1984: drag as U^(2+V), V −0.5 to −1.2), so the sound
/// grows far more slowly than a rigid cylinder's U³, and the crown keeps to the 30-36 dB a decade
/// Fégeant measured.
///
/// Fitted (2026-10-04): one leaf strike's level and the shedding's, at 5 m/s, so a broadleaf crown of
/// 400 m³ in a 4 m/s breeze makes about 50 dB(A) close to it; Fégeant's model (tabled by Heutschi,
/// Pieren and Müller 2014) puts an oak's 100 m³ at 44 dB(A) from 5 m in 5 m/s. The counts and the laws
/// come from the tree and the wind. Measured levels: docs/COMMON_NOTES.md, "The park tree".
///
/// With more than one place (ExtendedSources, 2026-10-06), each bough's strikes and twig episodes go to
/// the place for its side of the crown, or to the middle by the middle's share, and each place has its
/// own shedding noise: independent streams that add up to the tree.
/// </summary>
public sealed class FoliageSynth
{
    /// <summary>One leaf strike's peak, Pa at a metre from the crown, for a 40 cm² leaf in a 5 m/s wind.</summary>
    public const float LeafStrikePascals = 0.00075f;

    /// <summary>The shedding noise, rms Pa at a metre, for a crown of 50 m² of leaf at 5 m/s.</summary>
    public const float SheddingPascals = 0.00225f;

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
    /// <summary>The scale on (cos θ)^StrikeSpeedPower that gives a strike the mean energy the fit of
    /// <see cref="LeafStrikePascals"/> was made with: √(1.533 (2 · 1.2 + 1)).</summary>
    private const float StrikeAngleScale = 2.283f;
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
        public int Bough;
    }
    private readonly Episode[] _episodes = new Episode[128];

    public readonly FoliageSpec Spec;
    private readonly float _rate;
    private readonly EventSum _sum;
    /// <summary>Each place's events; place 0 (the middle) is <see cref="_sum"/>, which also draws every
    /// random number the scheduling needs, so a tree of one place renders as it did without places.</summary>
    private readonly EventSum[] _sums;
    private readonly float _leaves;          // how many leaves on the tree
    private readonly float _leafLength;      // m
    private readonly float _leafScale;       // strike size for this leaf
    private readonly float _shedScale;       // shedding size for this crown
    private readonly float Vogel;            // the crown's reconfiguration exponent, FoliageSpec.VogelExponent

    /// <summary>The wind at the crown, m/s: setting it sets every bough's. A voice that can read the
    /// field at each bough's own place uses <see cref="ReadWind"/> instead.</summary>
    public float Wind
    {
        get => _boughWindIn[0];
        set { for (int b = 0; b < Boughs; b++) _boughWindIn[b] = value; }
    }

    /// <summary>Reads the wind field at each bough's own place, for a crown whose middle is at (x, z).
    /// A gust crosses a crown in a second or two, upwind boughs first. Read at the middle for all of
    /// them, every gust arrived everywhere at once and was heard as a change of setting rather than as
    /// air moving through a tree.</summary>
    public void ReadWind(float x, float z, double seconds)
    {
        for (int b = 0; b < Boughs; b++)
        {
            var at = BoughOffset(Spec, b);
            _boughWindIn[b] = WindField.SpeedAt(x + at.X, Spec.CrownHeightMetres, z + at.Z, seconds);
        }
    }

    /// <summary>
    /// Reads the wind field at the given offsets from (x, z), one per bough: for a wood heard as one
    /// source (WoodChorus), whose "boughs" stand across the whole wood, so a gust crosses it from its
    /// upwind side to its downwind side as it crosses the trees.
    /// </summary>
    public void ReadWindAt(float x, float z, double seconds, ReadOnlySpan<Vector3> boughOffsets)
    {
        for (int b = 0; b < Boughs; b++)
        {
            var at = boughOffsets.Length > 0 ? boughOffsets[b % boughOffsets.Length] : Vector3.Zero;
            _boughWindIn[b] = WindField.SpeedAt(x + at.X, Spec.CrownHeightMetres, z + at.Z, seconds);
        }
    }

    /// <summary>
    /// How many trees this synth is: 1 for a tree; for a wood heard as one source (WoodChorus), how many
    /// of its trees it stands for now. N independent trees sum to one stream at N times the rate and
    /// power: rendered so up to <see cref="MaxDensityTrees"/>, and past it scaled in amplitude to N
    /// trees' power. Under one, a share of a tree.
    /// </summary>
    public float Trees = 1f;

    /// <summary>The most trees' worth of strikes a synth schedules; past it the power is made up in
    /// amplitude. At three a wood's rustle is already dense enough to merge, and twig episodes stay
    /// within the synth's slots.</summary>
    public const float MaxDensityTrees = 3f;

    private float _density = 1f, _ampTarget = 1f, _amp = 1f;

    /// <summary>The wind last handed to bough <paramref name="b"/>, m/s.</summary>
    public float BoughWind(int b) => _boughWindIn[b];

    /// <summary>
    /// Where bough <paramref name="b"/> is in the crown, m from its middle (x east, y up, z north): round
    /// the crown at two-thirds of its radius (the mean distance from the middle of a disc), every sixty
    /// degrees, alternately a quarter of the radius above and below. The wind is read there
    /// (<see cref="ReadWind"/>) and the bough's strikes are heard from there (ExtendedSources).
    /// </summary>
    public static Vector3 BoughOffset(FoliageSpec spec, int b)
    {
        float r = spec.CrownRadiusMetres;
        float angle = MathF.Tau * (b + 0.5f) / Boughs;
        float up = (b & 1) == 0 ? 0.25f * r : -0.25f * r;
        return new Vector3(MathF.Cos(angle) * r * 2f / 3f, up, MathF.Sin(angle) * r * 2f / 3f);
    }

    /// <summary>Which of <paramref name="places"/> outer places bough <paramref name="b"/> is heard from:
    /// neighbouring boughs together, so each place is one side of the crown.</summary>
    public static int PlaceOfBough(int b, int places) => places <= 0 ? 0 : Math.Clamp(b * places / Boughs, 0, places - 1);

    /// <summary>How much of the tree its outer places carry, 0 (all from the middle) to 1 (an equal share
    /// each): ExtendedSources.Shares. Set between control calls; the shedding glides to it.</summary>
    public float Spread;
    private float _middleShare = 1f;

    /// <summary>Each part's share, for the lab to take the tree apart by muting. One in the game.</summary>
    public float LeafPart = 1f, ShedPart = 1f;
    private readonly float[] _boughWindIn = new float[Boughs];
    private readonly float[] _wind = new float[Boughs];

    // Each bough: a damped oscillator driven by the wind's buffeting.
    private readonly float[] _x = new float[Boughs], _v = new float[Boughs], _hz = new float[Boughs];
    private readonly float[] _push = new float[Boughs], _pushTarget = new float[Boughs], _pushClock = new float[Boughs];
    private readonly float[] _agitation = new float[Boughs];

    private Resonator _shed, _shedHigh;
    // The outer places' own shedding (index 0 unused: the middle's is _shed, _shedHigh).
    private readonly Resonator[] _shedAt, _shedHighAt;
    private readonly float[] _placeGain, _placeGainTarget, _placeOut;
    private float _shedHz, _shedTargetHz, _shedLevel, _shedGain, _shedNorm, _shedHighNorm;
    private readonly float _shedGlide;
    private int _untilBlock, _samples;

    public FoliageSynth(FoliageSpec spec, float sampleRate, int seed, int places = 1)
    {
        Spec = spec;
        _rate = sampleRate;
        _sum = new EventSum(sampleRate, seed);
        places = Math.Clamp(places, 1, 1 + Boughs);
        _sums = new EventSum[places];
        _sums[0] = _sum;
        for (int k = 1; k < places; k++) _sums[k] = new EventSum(sampleRate, seed * 7919 + k * 104729 + 1);
        _shedAt = new Resonator[places];
        _shedHighAt = new Resonator[places];
        _placeGain = new float[places];
        _placeGainTarget = new float[places];
        _placeOut = new float[places];
        _placeGain[0] = _placeGainTarget[0] = 1f;
        Vogel = Math.Clamp(spec.VogelExponent, -1.5f, 0f);
        // Normalised to the mean energy LeafStrikePascals was fitted with: a strike's goes as
        // (u e)^(2 · 1.2), an episode's scale as the earlier 0.4 + 1.2 u (mean square 1.12).
        _incrementOrder = IncrementOrder;
        _incrementNorm = MathF.Sqrt(GammaMoment(_incrementOrder, 2 * StrikeSpeedPower));
        _episodeOrder = EpisodeOrder;
        _episodeNorm = MathF.Sqrt(GammaMoment(_episodeOrder, 2 * EpisodePower) / 1.12f);
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
        for (int k = 1; k < places; k++) { _shedAt[k].Tune(_shedHz, 0.7f, sampleRate); _shedHighAt[k].Tune(_shedHz * 3f, 0.6f, sampleRate); }
        _shedNorm = 0.8f / Resonator.NoiseGain(_shedHz, 0.7f, sampleRate);
        _shedHighNorm = 0.35f / Resonator.NoiseGain(_shedHz * 3f, 0.6f, sampleRate);
        _shedGlide = 1f - MathF.Exp(-1f / (0.005f * sampleRate));
    }

    /// <summary>
    /// How hard the air hits one leaf, or one twig, against the mean: a velocity increment across a
    /// few centimetres of turbulent air, mean one, from a gamma law of order <paramref name="order"/>
    /// (1 the exponential). Small-scale increments have exponential tails, fatter the smaller the scale
    /// (Kailasnath, Sreenivasan and Stolovitzky 1992, Phys. Rev. Lett. 68, 2766; Frisch 1995,
    /// "Turbulence", ch. 8), so most strikes are glancing and a few are the hard knocks a recording has.
    /// Driven by the mean wind alone (texture round 1, 2026-10-06) the band envelopes were as steady as
    /// noise: spread 0.075 at 6-12 kHz and skew below zero, against recordings' 0.08-0.20 and 0.35-1.4.
    /// </summary>
    private float Increment(int order)
    {
        float x = 0f;
        for (int j = 0; j < order; j++) x -= MathF.Log(MathF.Max(1e-7f, 1f - _sum.Uniform()));
        return x / order;
    }

    /// <summary>The order of the increments' gamma law: 1, the exponential tails of the smallest
    /// scales, which a leaf a few centimetres long is. FITTED 2026-10-06 against the three wind-in-leaves
    /// recordings in a steady wind: order 2 left the top bands' skew under the recordings', 8 (near
    /// Gaussian) under zero.</summary>
    private const int IncrementOrder = 1;

    /// <summary>An episode's loudness goes as the eddy's increment to this power: its strikes go as the
    /// closing speed to <see cref="StrikeSpeedPower"/> and come as often as its leaves flutter, which goes
    /// as the speed too, so its power goes as e^(2 · 1.2 + 1) and its amplitude as e^1.7.</summary>
    private const float EpisodePower = StrikeSpeedPower + 0.5f;
    private readonly int _incrementOrder, _episodeOrder;

    /// <summary>The order for a twig's eddy, tens of centimetres across: increments over larger
    /// separations are nearer Gaussian (Kailasnath et al. 1992), so a twig's knock is less often
    /// extreme than a leaf's. FITTED 2026-10-06 in steady 3 and 4.5 m/s winds: at order 1 a 3 m/s
    /// rustle's 6-12 kHz kurtosis was 10.5 (recordings 2.4-8.7), at 4 the top bands' skew fell under
    /// the recordings'.</summary>
    private const int EpisodeOrder = 2;
    private readonly float _incrementNorm, _episodeNorm;

    /// <summary>E[x^q] for x the mean-one gamma law of order k, by summing its density.</summary>
    private static float GammaMoment(int k, double q)
    {
        double num = 0, den = 0;
        for (int i = 1; i <= 6000; i++)
        {
            double x = i * 0.005;
            double w = Math.Pow(x, k - 1) * Math.Exp(-k * x);
            num += w * Math.Pow(x, q);
            den += w;
        }
        return (float)(num / den);
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
        // The wind pushes each bough with its drag, and the buffeting (the gusts inside the gust)
        // changes that force every fraction of a second.
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

        // The shedding follows the crown's mean motion through the air.
        float mean = 0f;
        for (int b = 0; b < Boughs; b++) mean += _agitation[b];
        mean /= Boughs;
        // Glides and is retuned every call: retuned in 3 % jumps, each was a step in the hiss's colour.
        _shedTargetHz = Math.Clamp(Strouhal * mean / (Spec.ShedDiameterMm * 1e-3f), 20f, 6000f);
        _shedHz += (_shedTargetHz - _shedHz) * MathF.Min(1f, dt / 0.25f);
        _shed.Tune(_shedHz, 0.7f, _rate);
        _shedHigh.Tune(_shedHz * 3f, 0.6f, _rate);
        for (int k = 1; k < _sums.Length; k++) { _shedAt[k].Tune(_shedHz, 0.7f, _rate); _shedHighAt[k].Tune(_shedHz * 3f, 0.6f, _rate); }
        _shedNorm = 0.8f / Resonator.NoiseGain(_shedHz, 0.7f, _rate);
        _shedHighNorm = 0.35f / Resonator.NoiseGain(_shedHz * 3f, 0.6f, _rate);
        // The fluctuating force's dipole: pressure as the force times the speed it changes at,
        // U^(2+V) times U^(1/2) for the flutter's share — U^1.8, power U^3.6, Fégeant's birch.
        float u = mean / 5f;
        float trees = MathF.Max(0f, Trees);
        _density = MathF.Min(trees, MaxDensityTrees);
        _ampTarget = _density > 0f ? MathF.Sqrt(trees / _density) : 0f;
        _shedLevel = SheddingPascals * _shedScale * MathF.Pow(u, 2.5f + Vogel) * MathF.Sqrt(_density);

        // The places' shares: events by probability, the shedding's power by gain.
        if (_sums.Length > 1)
        {
            Span<float> shares = stackalloc float[_sums.Length];
            ExtendedSources.Shares(Spread, shares);
            _middleShare = shares[0];
            for (int k = 0; k < shares.Length; k++) _placeGainTarget[k] = MathF.Sqrt(shares[k]);
        }
    }

    public float Next()
    {
        if (_sums.Length > 1)
        {
            NextPlaces(_placeOut);
            float all = 0f;
            for (int k = 0; k < _placeOut.Length; k++) all += _placeOut[k];
            return all;
        }
        if (--_untilBlock <= 0)
        {
            _untilBlock = Block;
            Schedule(Block / _rate);
        }
        _samples++;
        // Set once a control call; glided over a few milliseconds so it never steps.
        _shedGain += (_shedLevel - _shedGain) * _shedGlide;
        _amp += (_ampTarget - _amp) * _shedGlide;
        float n = _sum.Signed() * 1.7320508f;
        float shed = (_shed.Process(n) * _shedNorm + _shedHigh.Process(n) * _shedHighNorm) * _shedGain;
        return (shed * ShedPart + _sum.Next()) * _amp;
    }

    /// <summary>The next sample at each place, pascals at a metre from it, one per place in
    /// <paramref name="places"/>. Their sum is the tree.</summary>
    public void NextPlaces(Span<float> places)
    {
        if (--_untilBlock <= 0)
        {
            _untilBlock = Block;
            Schedule(Block / _rate);
        }
        _samples++;
        _shedGain += (_shedLevel - _shedGain) * _shedGlide;
        _amp += (_ampTarget - _amp) * _shedGlide;
        for (int k = 0; k < _sums.Length; k++)
        {
            _placeGain[k] += (_placeGainTarget[k] - _placeGain[k]) * _shedGlide;
            var sum = _sums[k];
            float n = sum.Signed() * 1.7320508f;
            float shed = k == 0
                ? _shed.Process(n) * _shedNorm + _shedHigh.Process(n) * _shedHighNorm
                : _shedAt[k].Process(n) * _shedNorm + _shedHighAt[k].Process(n) * _shedHighNorm;
            float y = (shed * _shedGain * _placeGain[k] * ShedPart + sum.Next()) * _amp;
            if (k < places.Length) places[k] = y;
        }
    }

    /// <summary>The place a strike of bough <paramref name="bough"/> is heard from: the middle by its
    /// share, otherwise the bough's own side of the crown.</summary>
    private EventSum PlaceFor(int bough)
    {
        if (_sums.Length == 1) return _sum;
        if (_sum.Uniform() < _middleShare) return _sum;
        return _sums[1 + PlaceOfBough(bough, _sums.Length - 1)];
    }

    private void Schedule(float dt)
    {
        if (_leaves <= 0f) return;
        for (int b = 0; b < Boughs; b++)
        {
            float speed = _agitation[b];
            float strikes = StrikesPerSecond(speed) / Boughs * _density;
            // The closing speed of two fluttering leaves is a fraction of the wind through them.
            float closing = MathF.Pow(MathF.Max(0f, speed) / 5f, StrikeSpeedPower);

            // Twigs set going. While an eddy holds a twig each leaf touches a neighbour once each way
            // a flutter cycle: about 160 strikes a second in a breeze, some thirty-five an episode.
            // At 400 an episode (ten times what a twig can make) the rustle came as a few loud
            // patches a second, each heard arriving.
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
                        // How hard this eddy hit (Increment), and how big this twig's leaves are.
                        Scale = closing * MathF.Pow(Increment(_episodeOrder), EpisodePower) / _episodeNorm,
                        Tone = 0.6f + 1.0f * _sum.Uniform(),
                        Bough = b,
                    };
                    break;
                }
            }

            // The single ticks between.
            Strikes(strikes * (1f - EpisodeShare) * dt, closing, 1f, b);
        }
        for (int e = 0; e < _episodes.Length; e++)
        {
            ref var ep = ref _episodes[e];
            if (ep.Left <= 0f) continue;
            Strikes(ep.Rate * dt, ep.Scale, ep.Tone, ep.Bough);
            ep.Left -= dt;
        }
    }

    private void Strikes(float mean, float scale, float tone, int bough)
    {
        int real = _sum.Poisson(mean);
        if (real == 0) return;
        int n = Math.Min(real, MaxPerBlock / 4);
        float weight = MathF.Sqrt(real / (float)n);
        for (int k = 0; k < n; k++)
        {
            int at = (int)(_sum.Uniform() * Block);
            var place = PlaceFor(bough);
            // Two leaves meet at any angle, and over a sphere the cosine to the normal is uniform, so
            // the closing speed's normal part is the speed times a uniform number. The earlier
            // 0.15 + 3u³ poked out of the rustle as scratches; this has its mean energy (1.53).
            float u = _sum.Uniform() * Increment(_incrementOrder);
            float p = LeafStrikePascals * _leafScale * scale * weight * StrikeAngleScale * MathF.Pow(u, StrikeSpeedPower) * LeafPart / _incrementNorm;
            // The tap: a light plate stopped over a few tenths of a millisecond...
            place.Pulse(at, 70e-6f * (0.7f + 0.6f * _sum.Uniform()) * _leafScale / tone, p);
            // ...and the leaf's own membrane ringing, damped almost at once, with the scrape of
            // edge over edge as they part.
            place.Burst(at + 3, 0.0002f, 0.0006f + 0.0012f * _sum.Uniform(), 0.35f * p,
                       900f * tone / _leafScale, 7000f * tone / MathF.Sqrt(_leafScale));
        }
    }
}
