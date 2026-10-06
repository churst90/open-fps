using System;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// Rain on the surfaces of one patch, as the drops it is made of, at the listener.
///
/// Every drop is an event: a size drawn from the rain's own spread (Rainfall, DropSizeTable), the
/// speed that size falls at, a place in one of the patch's distance bins, and the surface it lands
/// on. What it makes depends on the surface (RainSurfaces):
///
///   POOL — water. The click of the drop meeting the surface, and for some sizes a bubble that
///   rings: drops of 0.8-1.1 mm trap the "regular" bubble every time, the 14-16 kHz ring of rain on
///   a lake (Pumphrey and Crum 1990, Pumphrey and Elmore 1990; Prosperetti and Oguz 1993 for why that
///   window); drops over 2.2 mm make a large irregular "type II" bubble at 2-10 kHz, lower for a
///   bigger drop (Medwin et al. 1992, J. Acoust. Soc. Am. 92, 1613). The same physics, and the same
///   two fitted constants, as the fountain (FallingWaterSynth): how loud a bubble is for its size, and
///   how loud a drop's click is for its size and speed.
///
///   HARD — a road, a pavement, a slab. The drop lands on a film of water far thinner than itself,
///   splashes flat and traps nothing: a click, the rate of change of its force as it stops, on the
///   drop's own time scale (see Click). A share of the area is puddle (RainSurfaces.PuddleShare) and
///   behaves as a pool.
///
///   PLATE — sheet metal, glass, a car. The click on its top face, and the sheet itself: the blow
///   puts energy into its bending field through its point mobility, the field rings and radiates
///   (RainPlate). A bay's sparse low modes ring at their own notes — the drumming of a car roof or
///   a pane — and above where the modes overlap the field is noise in octave bands whose energy jumps
///   with each blow and decays at the sheet's loss factor. And the struck spot's own thud, ρ0 F(t) /
///   (2π m″ r). Heard from below (the roof over you) only the sheet and the thud come through.
///
///   SOFT — grass, soil, gravel. The ground yields, the blow lasts longer, and its click is lower
///   and softer (RainSurfaces.ContactStretch).
///
///   CANOPY — leaves. A crown catches most of the rain (RainSurfaces.CanopyCatch): each caught drop
///   strikes a leaf, which yields like soft ground. What it catches leaves again as drips of 4-6 mm
///   that fall from the crown to what is beneath it — the heavy, slow "plop" under a tree. What it
///   does not catch falls straight through.
///
/// HOW MANY. Thousands of drops land on a few square metres a second. Each bin renders, in each size
/// class, at most a few of its drops in a block (<see cref="PerClass"/>) and lets each stand for
/// √(real / rendered) of them, so the energy is kept while the cost is bounded — the fountain's rule,
/// taken a size class at a time so the rare big drops, which carry most of the sound, are each
/// rendered. The near bins have few drops a block and are rendered drop for drop; the far ones are a
/// wash, which is what they are.
///
/// Every sound is placed by the direction its surface faces the listener (RainLayer.Aim) and its
/// distance; the patch's voice is then placed at the patch's reference distance by the mixer.
/// </summary>
public sealed class RainSynth
{
    // ── Constants shared with the fountain's water ───────────────────────────────────────────────

    /// <summary>How fast a click's force arrives, s: first contact. The fountain's figure.</summary>
    public const float ImpactRise = 16e-6f;

    /// <summary>On a hard surface the drop stops in its own length and splashes flat, and its click
    /// is this much stronger than into a pool: the fountain's two figures (FallingWaterSynth). The
    /// hard click is what the street and roof rain were fitted with and is held; the pool's was
    /// refitted lower on 2026-10-06 (texture round 1), so the ratio grew from 1.2 to 5.7.</summary>
    public const float HardClickGain = FallingWaterSynth.HardImpactPascals / FallingWaterSynth.ImpactPascals;

    /// <summary>The share of drops of 0.8-1.1 mm that trap the regular bubble (Pumphrey and Elmore
    /// 1990 found nearly all of them, at near-terminal speed); of drops over 2.2 mm that make a type II
    /// bubble (Medwin et al. call it the dominant sound of a large drop); and of those that shed a
    /// second, smaller one. The fountain's figures.</summary>
    public const float RegularShare = 0.9f, IrregularShare = 0.8f, SecondaryShare = 0.3f;

    /// <summary>The regular bubble's radius, mm: 0.18-0.26, so 12.5-18 kHz.</summary>
    public const float RegularBubbleMm = 0.18f, RegularBubbleSpreadMm = 0.08f;

    /// <summary>The smallest bubble worth ringing, mm: under it the note is over 20 kHz.</summary>
    private const float SmallestBubbleMm = 0.16f;

    /// <summary>The most drops of one bin rendered in a block (see the summary): of the drips, and of
    /// each size class of the rain (<see cref="PerClass"/>).</summary>
    public const int MaxPerBin = 3;

    /// <summary>The most drops of each size class (DropSizeTable.ClassEdgesMm) of one bin rendered in
    /// a block: the smallest drops are most of the count and least of the sound, and one of them a
    /// block stands for the rest as well as three would.</summary>
    private static readonly int[] PerClass = { 1, 2, 3, 3, 3, 3 };

    /// <summary>The most separate plates a patch rings: a roof, a car's steel and its glass, a pane.</summary>
    public const int MaxPlates = 4;

    private const int Block = 128;

    private readonly float _rate;
    private readonly EventSum _sum;
    private readonly ParticleSpectrum _main = new(), _stones = new();
    private ParticleSpectrum _spec = null!;
    private PrecipitationKind _particle;
    private readonly PlateState[] _plates = new PlateState[MaxPlates];
    private readonly float[] _plateOut = new float[Block];
    private int _untilBlock, _blockAt;
    private RainPatch? _bound;
    private readonly PlateState?[] _layerPlate = new PlateState?[16];

    /// <summary>The patch to render. Read once a block; written by the owner.</summary>
    public volatile RainPatch? Patch;

    /// <summary>What is falling: its kind, its water-equivalent rate, its sizes. Read once a block.</summary>
    public Precipitation Falling;

    /// <summary>The rain rate, mm/h: the rate of <see cref="Falling"/>, as rain if nothing else was said.</summary>
    public float RainRate
    {
        get => Falling.RateMmPerHour;
        set => Falling = Falling with { RateMmPerHour = value };
    }

    /// <summary>The bins whose biggest drops are played one by one elsewhere (RainPatch.DiscreteFromMm):
    /// drops of this size and over are not rendered here.</summary>
    private float _skipFromMm = float.MaxValue;

    /// <summary>Each part's share, for the lab to take the sound apart by muting: the drops' clicks,
    /// the bubbles, the plates' ringing and thuds, and the drips. One in the game.</summary>
    public float ClickPart = 1f, BubblePart = 1f, PlatePart = 1f, DripPart = 1f;

    /// <summary>Seconds on the clock every rain voice shares (WindField.Now in the game, from zero in
    /// the lab): the patches round a listener read the same clustering at the same moment.</summary>
    public double Clock;

    /// <summary>
    /// How much more or less rain than its mean is arriving now, a factor about one.
    ///
    /// Raindrops do not arrive as a Poisson stream at a fixed rate. Counted over a few square metres,
    /// drops of one size come in clusters: their counts over seconds are far more variable than
    /// Poisson's, the excess growing with the counting time (Kostinski and Jameson 1997, J. Atmos. Sci.
    /// 54, "Fluctuation properties of precipitation. Part I"; Jameson and Kostinski 1999-2002, Parts
    /// II-VI: the pair-correlation of drop arrivals is positive out to seconds and metres). That is the
    /// slow swell a rain recording has and the model, fed one steady rate, did not: its band envelopes'
    /// modulation power at 0.5-2 Hz was 1.5 per cent of their variance, the recordings' 2.6-19
    /// (texture round 1, 2026-10-06).
    ///
    /// A lognormal factor on the flux, its log a smooth noise with knots every 0.6, 2.4 and 9.6
    /// seconds, the same for every patch at one moment. Its spread, <see cref="ClusterSigma"/>, is
    /// fitted to the recordings' slow modulation; the scales are Kostinski and Jameson's seconds.
    /// </summary>
    public static float Intermittency(double seconds)
    {
        float g = 0f;
        for (int k = 0; k < ClusterScales.Length; k++)
        {
            double u = seconds / ClusterScales[k] + 17.3 * k;
            long i = (long)Math.Floor(u);
            float f = (float)(u - i);
            float s = 0.5f - 0.5f * MathF.Cos(MathF.PI * f);
            g += (Knot(i, k) * (1f - s) + Knot(i + 1, k) * s);
        }
        // Each scale's knots have unit variance; the cosine glide between two independent knots keeps
        // three quarters of it on average.
        g /= MathF.Sqrt(0.75f * ClusterScales.Length);
        float sigma = FitTune.T("rsig", ClusterSigma);
        return MathF.Exp(sigma * g - 0.5f * sigma * sigma);
    }

    /// <summary>The spread of the log of the clustering factor: a standard deviation of about 45 per
    /// cent in the flux over seconds. FITTED 2026-10-06 on the street and in a car at moderate and heavy
    /// rain: 0.35 left the slow modulation at the recordings' lowest, 0.6 moved the bands together more
    /// than all but the gustiest recording; 0.45 puts the slow share at 0.055-0.06 (recordings' median
    /// 0.07) and the fast at 0.54-0.55 (median 0.52), every statistic inside the recordings' spread.</summary>
    public const float ClusterSigma = 0.45f;

    private static readonly double[] ClusterScales = { 0.6, 2.4, 9.6 };

    /// <summary>A unit-variance value at a knot: the sum of four hashed uniforms, centred.</summary>
    private static float Knot(long i, int scale)
    {
        ulong h = (ulong)i * 0x9E3779B97F4A7C15UL ^ (ulong)(scale + 1) * 0xC2B2AE3D27D4EB4FUL;
        float sum = 0f;
        for (int j = 0; j < 4; j++)
        {
            h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33;
            sum += (h >> 40) * (1f / 16777216f);
        }
        return (sum - 2f) * 1.7320508f;   // four uniforms: variance 1/3
    }

    /// <summary>Drops rendered and drops stood for since the last call, for the lab.</summary>
    public long Rendered, Represented;

    public RainSynth(float sampleRate, int seed)
    {
        _rate = sampleRate;
        _sum = new EventSum(sampleRate, seed);
        for (int i = 0; i < MaxPlates; i++) _plates[i] = new PlateState();
    }

    /// <summary>The next sample: pressure at the listener times the patch's reference distance, Pa.</summary>
    public float Next()
    {
        if (--_untilBlock <= 0)
        {
            _untilBlock = Block;
            _blockAt = 0;
            Schedule();
        }
        int c = (int)(_clickNow++ & (ClickRing - 1));
        float click = _clicks[c];
        _clicks[c] = 0f;
        return _sum.Next() + _plateOut[_blockAt++] + click;
    }

    private void Schedule()
    {
        var patch = Patch;
        if (!ReferenceEquals(patch, _bound)) Bind(patch);
        var falling = Falling;
        float rate = falling.RateMmPerHour;
        float dt = Block / _rate;
        Clock += dt;
        if (patch != null && falling.Falling)
        {
            // Rain does not fall at a steady rate onto a few square metres: it comes in clusters over
            // seconds (see Intermittency), all the patches round a listener together.
            float clustered = Intermittency(Clock);
            // The main particles: the rain (the rain hail falls in, for hail), the sleet, the snow.
            var mainKind = falling.Kind == PrecipitationKind.Hail ? PrecipitationKind.Rain : falling.Kind;
            _main.Build(mainKind, rate, falling.Kind == PrecipitationKind.Hail ? Hydrometeors.MarshallPalmerMedianMm(rate) : falling.EffectiveMedianMm);
            if (falling.Kind == PrecipitationKind.Hail) _stones.Build(falling);
            for (int pass = 0; pass < (falling.Kind == PrecipitationKind.Hail ? 2 : 1); pass++)
            {
                _spec = pass == 0 ? _main : _stones;
                _particle = pass == 0 ? mainKind : PrecipitationKind.Hail;
                Surfaces(patch, dt, rate, _spec.PerSquareMetreSecond * clustered);
            }
        }
        Array.Clear(_plateOut);
        for (int i = 0; i < MaxPlates; i++)
            if (_plates[i].Active) _plates[i].Render(_plateOut, _sum, PlatePart);
    }

    private void Surfaces(RainPatch patch, float dt, float rate, float flux)
    {
        {
            float scale = patch.ReferenceDistance;
            for (int l = 0; l < patch.Layers.Length; l++)
            {
                var layer = patch.Layers[l];
                var plate = l < _layerPlate.Length ? _layerPlate[l] : null;
                _modulus = layer.ModulusGPa;
                for (int b = 0; b < layer.Bins; b++)
                {
                    float near = layer.Distance[b];
                    _aim = layer.Aim[b];
                    _skipFromMm = layer.Discrete[b] ? layer.DiscreteFrom(_particle, b) : float.MaxValue;
                    float mean = flux * layer.Area[b] * dt;
                    switch (layer.Kind)
                    {
                        case RainSurfaceKind.Pool:
                            Drops(mean, near, scale, RainSurfaceKind.Pool, 1f, null, false);
                            break;
                        case RainSurfaceKind.Hard:
                        {
                            float puddle = _particle is PrecipitationKind.Rain or PrecipitationKind.FreezingRain ? RainSurfaces.PuddleShare(rate) : 0f;
                            Drops(mean * (1f - puddle), near, scale, RainSurfaceKind.Hard, 1f, null, false);
                            Drops(mean * puddle, near, scale, RainSurfaceKind.Pool, 1f, null, false);
                            break;
                        }
                        case RainSurfaceKind.Plate:
                            Drops(mean, near, scale, RainSurfaceKind.Plate, 1f, plate, layer.FromBelow);
                            break;
                        case RainSurfaceKind.Soft:
                            Drops(mean, near, scale, RainSurfaceKind.Soft, layer.Stretch, null, false);
                            break;
                        case RainSurfaceKind.Canopy:
                        {
                            float caught = RainSurfaces.CanopyCatch(layer.LeafAreaIndex);
                            _leaf = true;
                            Drops(mean * caught, near, scale, RainSurfaceKind.Soft, layer.Stretch, null, false);
                            _leaf = false;
                            Drops(mean * (1f - caught), near, scale, layer.UnderKind, layer.UnderStretch, null, false);
                            // What the leaves catch leaves them as drips: the same water, in big drops.
                            // Rain only: snow and ice do not drip off leaves.
                            if (_particle is PrecipitationKind.Rain or PrecipitationKind.FreezingRain)
                            {
                                float dripVolume = MathF.PI / 6f * MathF.Pow(RainSurfaces.DripDiameterMm * 1e-3f, 3f);
                                float drips = caught * rate / 3.6e6f / dripVolume * layer.Area[b] * dt;
                                Drips(drips, near, scale, layer);
                            }
                            break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Configures the plates for a new patch, keeping the ringing of any plate that is
    /// still in it (by its key), so a survey that moves a patch a metre does not cut a roof dead.</summary>
    private void Bind(RainPatch? patch)
    {
        _bound = patch;
        Array.Clear(_layerPlate);
        foreach (var p in _plates) p.Keep = false;
        if (patch == null) { foreach (var p in _plates) p.Active = false; return; }
        // First the plates still in the patch keep their state...
        for (int l = 0; l < patch.Layers.Length && l < _layerPlate.Length; l++)
        {
            var layer = patch.Layers[l];
            if (layer.Kind != RainSurfaceKind.Plate) continue;
            string key = layer.Key;
            foreach (var p in _plates)
                if (p.Active && !p.Keep && p.Key == key)
                {
                    p.Configure(key, layer, _rate, patch.ReferenceDistance);
                    p.Keep = true;
                    _layerPlate[l] = p;
                    break;
                }
        }
        foreach (var p in _plates) if (!p.Keep) p.Active = false;
        // ...then the new ones take what is free, from rest.
        for (int l = 0; l < patch.Layers.Length && l < _layerPlate.Length; l++)
        {
            var layer = patch.Layers[l];
            if (layer.Kind != RainSurfaceKind.Plate || _layerPlate[l] != null) continue;
            foreach (var p in _plates)
                if (!p.Active)
                {
                    p.Reset();
                    p.Configure(layer.Key, layer, _rate, patch.ReferenceDistance);
                    p.Keep = p.Active = true;
                    _layerPlate[l] = p;
                    break;
                }
            // More plates than there is room for: the rest are heard by their clicks alone.
        }
    }

    /// <summary>The bin being rendered: how squarely it faces the ear, and whether its drops are
    /// striking leaves (which face every way).</summary>
    private float _aim = 1f;
    private bool _leaf, _single;

    /// <summary>
    /// One particle landing on <paramref name="surface"/> a metre from the ear, square on, rendered
    /// alone for <paramref name="samples"/> samples: what NearDrops plays one by one, with the same
    /// physics as the patches render by the thousand. Pressure at a metre, Pa. A hailstone's bounce is
    /// not in it (NearDrops places the bounce where and when it lands).
    /// </summary>
    public float[] RenderOne(RainLayer surface, PrecipitationKind kind, float diameterMm, float speed, int samples)
    {
        var layer = surface.Single();
        Patch = new RainPatch { Layers = new[] { layer }, ReferenceDistance = 1f };
        Falling = Precipitation.None;
        var out_ = new float[samples];
        // Bind the plate, then strike once at the start of the first block.
        _untilBlock = 0;
        Bind(Patch);
        _untilBlock = Block;
        _blockAt = 0;
        _single = true;
        _particle = kind;
        _modulus = layer.ModulusGPa;
        _aim = 1f;
        _leaf = layer.Kind == RainSurfaceKind.Canopy;
        var plate = _layerPlate[0];
        var landsOn = layer.Kind switch
        {
            RainSurfaceKind.Canopy => RainSurfaceKind.Soft,
            _ => layer.Kind,
        };
        Land(0, diameterMm, speed, 1f, 1f, 1f, landsOn, layer.Stretch, plate, layer.FromBelow);
        _leaf = false;
        Array.Clear(_plateOut);
        for (int i = 0; i < MaxPlates; i++) if (_plates[i].Active) _plates[i].Render(_plateOut, _sum, PlatePart);
        for (int i = 0; i < samples; i++) out_[i] = Next();
        _single = false;
        return out_;
    }

    private void Drops(float mean, float distance, float scale, RainSurfaceKind kind, float stretch,
                       PlateState? plate, bool fromBelow)
    {
        if (mean <= 0f) return;
        // One size class at a time (ParticleSpectrum): the big drops, few and loud, are rendered every
        // one; only the swarm of small ones is stood for by a few.
        for (int c = 0; c < ParticleSpectrum.Classes; c++)
        {
            if (_spec.ClassFloorMm(c) >= _skipFromMm) break;      // played one by one, elsewhere
            int real = _sum.Poisson(mean * _spec.ClassShare(c));
            if (real == 0) continue;
            int n = Math.Min(real, PerClass[c]);
            float weight = MathF.Sqrt(real / (float)n);
            Rendered += n; Represented += real;
            for (int k = 0; k < n; k++)
            {
                int at = (int)(_sum.Uniform() * Block);
                float d = _spec.DrawIn(c, _sum.Uniform());
                if (d >= _skipFromMm) continue;
                float v = Hydrometeors.FallSpeed(_particle, d);
                // Somewhere in the bin's ring: the bin's distance holds its 1/r², so the jitter is
                // around it, not added to it.
                float r = distance * (0.8f + 0.4f * _sum.Uniform());
                Land(at, d, v, r, weight, scale, kind, stretch, plate, fromBelow);
            }
        }
    }

    private void Drips(float mean, float distance, float scale, RainLayer layer)
    {
        int real = _sum.Poisson(mean);
        if (real == 0) return;
        int n = Math.Min(real, MaxPerBin);
        float weight = MathF.Sqrt(real / (float)n) * DripPart;
        for (int k = 0; k < n; k++)
        {
            int at = (int)(_sum.Uniform() * Block);
            float d = RainSurfaces.DripDiameterMm * (0.8f + 0.4f * _sum.Uniform());
            float vt = Rainfall.TerminalSpeed(d);
            float h = layer.DripFallMetres * (0.5f + _sum.Uniform());
            float v = vt * MathF.Sqrt(1f - MathF.Exp(-2f * 9.81f * h / (vt * vt)));
            float r = distance * (0.8f + 0.4f * _sum.Uniform());
            var was = _particle;
            _particle = PrecipitationKind.Rain;
            Land(at, d, v, r, weight, scale, layer.UnderKind, layer.UnderStretch, null, false);
            _particle = was;
        }
    }

    /// <summary>One drop of diameter <paramref name="d"/> mm at <paramref name="v"/> m/s landing
    /// <paramref name="r"/> m from the listener.</summary>
    private void Land(int at, float d, float v, float r, float weight, float scale, RainSurfaceKind kind,
                      float stretch, PlateState? plate, bool fromBelow)
    {
        float rmm = 0.5f * d;
        float tau = rmm * 1e-3f / v;
        // Clicks and bubbles radiate along the surface's normal (RainLayer.Aim); a leaf faces every way.
        float atEar = scale / MathF.Max(0.3f, r) * (_leaf ? 1f : _aim);
        if (_particle is not (PrecipitationKind.Rain or PrecipitationKind.FreezingRain))
        {
            Solid(at, d, v, weight, atEar, kind, stretch, plate, fromBelow);
            return;
        }
        float law = FallingWaterSynth.ImpactPascals * MathF.Pow(rmm * v / 5f, 1.5f) * weight * atEar;
        switch (kind)
        {
            case RainSurfaceKind.Pool:
            {
                _sum.Impact(at, ImpactRise, tau, law * ClickPart);
                int later = at + (int)(0.003f * _rate * (0.5f + _sum.Uniform()));
                if (d >= 0.8f && d <= 1.1f)
                {
                    if (_sum.Uniform() < RegularShare)
                        Bubble(later, RegularBubbleMm + RegularBubbleSpreadMm * _sum.Uniform(), weight * atEar);
                }
                else if (d >= 2.2f && _sum.Uniform() < IrregularShare)
                {
                    float b = FallingWaterSynth.TypeTwoBubbleMm(rmm) * (0.8f + 0.4f * _sum.Uniform());
                    Bubble(later, b, weight * atEar);
                    if (_sum.Uniform() < SecondaryShare)
                        Bubble(later + (int)(0.004f * _rate * _sum.Uniform()), b * (0.3f + 0.6f * _sum.Uniform()), 0.4f * weight * atEar);
                }
                break;
            }
            case RainSurfaceKind.Hard:
                Click(at, d, v, HardClickGain * law, 1f);
                break;
            case RainSurfaceKind.Soft:
                Click(at, d, v, HardClickGain * law, MathF.Max(1f, stretch));
                break;
            case RainSurfaceKind.Plate:
            {
                if (!fromBelow) Click(at, d, v, HardClickGain * law, 1f);
                if (plate == null) break;
                Strike(at, plate, RainPlate.Impulse(d, v), RainPlate.BlowSeconds(d, v), weight, atEar,
                       0.5f * Hydrometeors.Mass(PrecipitationKind.Rain, d) * v * v);
                break;
            }
        }
    }

    /// <summary>A blow of this momentum (N·s) over this time (s) into a plate: its ringing, and the
    /// struck spot's thud, ρ0 F(t) / (2π m″ r), the shape of the blow itself.</summary>
    private void Strike(int at, PlateState plate, float impulse, float blow, float weight, float atEar, float kineticJoules)
    {
        // The infinite plate's mobility hands a sheet more energy than a hard enough blow ever had: a
        // golf-ball hailstone on 0.7 mm steel would put in forty times its own kinetic energy. What
        // the stone loses is all there is, and the sheet takes no more than half of it (the rest is
        // the dent, the heat and the stone's own break-up).
        float peak = impulse / (blow * RainPlate.BlowShapeArea);
        float energy = plate.Plate.Mobility * peak * peak * blow * RainPlate.BlowShapeEnergy;
        float cap = MaxPlateShare * kineticJoules;
        if (energy > cap && energy > 0f)
        {
            float k = MathF.Sqrt(cap / energy);
            impulse *= k;
            peak *= k;
        }
        plate.Inject(at, impulse, blow, weight * weight, _sum.Uniform(), _sum.Uniform());
        float thud = WallTransmission.AirDensity * peak / (2f * MathF.PI * plate.Plate.SurfaceDensity) * weight * atEar * PlatePart;
        _sum.Impact(at, RainPlate.BlowPeakAt * blow, 2f * blow, thud);
    }

    /// <summary>The most of an impact's lost kinetic energy a sheet takes into its ringing.</summary>
    public const float MaxPlateShare = 0.5f;

    /// <summary>The surface's Young's modulus, GPa: what an ice sphere's contact time depends on.</summary>
    private float _modulus = 30f;

    /// <summary>
    /// A particle that is not a drop: an ice pellet or a hailstone, which strikes and bounces, or a
    /// snowflake, which crushes.
    ///
    /// Ice is a hard sphere, and the air hears it stop: the acceleration noise of a rigid body, the
    /// dipole of its own deceleration, p ≈ 3 ρ0 V Δv / (4π c r τ²) at its peak with the ground's image
    /// doubling it (Koss and Alfredson 1973, J. Sound Vib. 27, 59-75, for spheres in collision), over
    /// the Hertz contact time τ against whatever it hits (Hydrometeors.HertzSeconds) — tens of
    /// microseconds on a road, so a sharp, bright tick, where a drop of the same size splats over a
    /// millimetre of time. It keeps some of its speed (Hydrometeors.Restitution) and lands again: a
    /// bounce, as hail does, heard when it comes down within the half second this can look ahead.
    /// Into water it is a plunge: a click and the bubble its cavity closes on. On leaves and grass the
    /// contact is soft and long: a thud.
    ///
    /// A snowflake is mostly air: it stops over its own size at a metre a second, and its dipole is
    /// thousands of times weaker. Snow falling is nearly silent, as it is.
    /// </summary>
    private void Solid(int at, float d, float v, float weight, float atEar, RainSurfaceKind kind, float stretch,
                       PlateState? plate, bool fromBelow)
    {
        bool ice = Hydrometeors.IsIce(_particle);
        float mass = Hydrometeors.Mass(_particle, d);
        float size = ice ? d : d * Hydrometeors.FlakeSwell;
        float volume = MathF.PI / 6f * MathF.Pow(size * 1e-3f, 3f);
        if (kind == RainSurfaceKind.Pool)
        {
            if (!ice) return;                                        // snow on water melts in silence
            float rmm = 0.5f * d;
            float law = FallingWaterSynth.ImpactPascals * MathF.Pow(rmm * v / 5f, 1.5f) * weight * atEar;
            _sum.Impact(at, ImpactRise, rmm * 1e-3f / v, law * ClickPart);
            // The cavity a solid sphere drags down closes on a bubble about its own size.
            Bubble(at + (int)(0.01f * _rate * (0.5f + _sum.Uniform())), rmm * (0.5f + 0.5f * _sum.Uniform()), weight * atEar);
            return;
        }
        float modulus = kind == RainSurfaceKind.Plate ? plate?.Plate.Properties.YoungsModulusGPa ?? _modulus
                      : kind == RainSurfaceKind.Soft ? MathF.Min(_modulus, 0.05f) : _modulus;
        float e = ice ? Hydrometeors.Restitution(modulus) : 0f;
        float contact = ice ? Hydrometeors.HertzSeconds(_particle, d, v, modulus) : Hydrometeors.FlakeCrushSeconds(d, v);
        contact *= kind == RainSurfaceKind.Soft ? MathF.Max(1f, stretch) : 1f;
        float impulse = mass * v * (1f + e);
        if (!(kind == RainSurfaceKind.Plate && fromBelow))
        {
            float peak = 3f * WallTransmission.AirDensity * volume * v * (1f + e)
                         / (4f * MathF.PI * WallTransmission.SoundSpeed * contact * contact) * weight * atEar;
            // The click's shape (the blow's derivative) at the contact's length; its energy is the peak
            // held over about a quarter of the contact.
            float tauSamples = MathF.Max(ShapeMinSamples, 0.8f * contact * _rate);
            ClickEnergy(at, tauSamples, peak * peak * 0.25f * contact);
        }
        if (kind == RainSurfaceKind.Plate && plate != null) Strike(at, plate, impulse, contact, weight, atEar, 0.5f * mass * v * v * (1f - e * e));
        // The bounce, if it lands inside what can be looked ahead.
        if (ice && e > 0.2f && !_single)
        {
            float up = e * v;
            int later = at + (int)(2f * up / 9.81f * _rate);
            if (later < Block + _sum.Horizon / 2)
            {
                float rebound = up;
                float c2 = Hydrometeors.HertzSeconds(_particle, d, rebound, modulus);
                float peak2 = 3f * WallTransmission.AirDensity * volume * rebound * (1f + e)
                              / (4f * MathF.PI * WallTransmission.SoundSpeed * c2 * c2) * weight * atEar;
                if (!(kind == RainSurfaceKind.Plate && fromBelow))
                    ClickEnergy(later, MathF.Max(ShapeMinSamples, 0.8f * c2 * _rate), peak2 * peak2 * 0.25f * c2);
                if (kind == RainSurfaceKind.Plate && plate != null && later < Block) Strike(later, plate, mass * rebound * (1f + e), c2, weight, atEar, 0.5f * mass * rebound * rebound * (1f - e * e));
            }
        }
    }

    /// <summary>
    /// A drop's click on something solid, at the listener.
    ///
    /// The air hears the rate of change of the drop's push on it: a dipole at a rigid boundary
    /// radiates dF/dt. It has the blow's shape (RainPlate.BlowShape: √t to its peak, then its fall),
    /// but not the blow's length. What moves the air is the drop's water going from a falling sphere
    /// to a sheet spreading over the wet ground, and that takes the spreading time, about 8/3 D / v
    /// (RainSurfaces.SplashSeconds) — two and a half times the time the drop takes to stop. So the
    /// click rises to a broad top near 1 / (2π · 0.2 · 8/3 D / v), 0.5-1.5 kHz for the drops that carry
    /// the energy, and falls gently above. Two earlier time scales were measured against recordings of
    /// rain on streets, a garden and a wood (the lab's --rain levels and compare=): the fountain's pool
    /// click, a 16 µs spike, was 15 dB too bright above 4 kHz; the drop's stopping time, D / v, still
    /// 8-13 dB too bright there and 7-20 dB short at 250-500 Hz, and its 10 ms windows 2.4 dB too
    /// peaky (the "grain" figure). The spreading time brings both within a few dB.
    ///
    /// Its ENERGY is the fountain's, which was fitted against measured falling water (Watts et al.
    /// 2009): the energy of the spike that law gives a drop of that size and speed. Only where in the
    /// spectrum it sits is the drop's own.
    ///
    /// On soft ground the same momentum is handed over <paramref name="stretch"/> times more slowly:
    /// dF/dt falls as its square, so the energy as its cube, and the click is that much lower.
    /// </summary>
    private void Click(int at, float d, float v, float spikePascals, float stretch)
    {
        if (spikePascals <= 0f || ClickPart <= 0f) return;
        float energy = spikePascals * spikePascals * ImpactRise * 1.7724539f / (stretch * stretch * stretch);
        ClickEnergy(at, RainSurfaces.SplashSeconds(d, v) * stretch * _rate, energy);
    }

    /// <summary>A click of this time scale (samples) carrying this energy (Pa²·s at the ear).</summary>
    private void ClickEnergy(int at, float tauSamples, float energy)
    {
        if (!(energy > 0f) || ClickPart <= 0f) return;
        if (at < 0 || at >= ClickRing - MaxShape - Block) return;
        var shape = ClickShape(tauSamples);
        float gain = MathF.Sqrt(energy * _rate) * ClickPart;
        long start = _clickNow + at + 1;
        for (int i = 0; i < shape.Length; i++)
            _clicks[(int)((start + i) & (ClickRing - 1))] += gain * shape[i];
    }

    // The click's shape depends only on its time scale, so the shapes are made once, a ninth of an
    // octave apart, and a click is a scaled copy of the nearest.
    private const float ShapeStepsPerOctave = 9f, ShapeMinSamples = 2f;
    private readonly float[]?[] _shapes = new float[]?[96];

    /// <summary>The click of time scale <paramref name="tauSamples"/>, at unit energy (Σ y² = 1).</summary>
    private float[] ClickShape(float tauSamples)
    {
        int index = Math.Clamp((int)MathF.Round(MathF.Log2(MathF.Max(ShapeMinSamples, tauSamples) / ShapeMinSamples) * ShapeStepsPerOctave),
                               0, _shapes.Length - 1);
        if (_shapes[index] is { } made) return made;
        float tau = ShapeMinSamples * MathF.Pow(2f, index / ShapeStepsPerOctave);
        int length = Math.Min(MaxShape - 2 * OnsetHalf, (int)((RainPlate.BlowPeakAt + 7f * RainPlate.BlowFall) * tau) + 2);
        // The blow's sample-to-sample steps (see Click).
        var raw = new float[length];
        float prev = 0f;
        float peakAt = RainPlate.BlowPeakAt * tau;
        for (int i = 0; i < length; i++)
        {
            float t = i + 1;
            float g = t < peakAt ? MathF.Sqrt(t / peakAt) : MathF.Exp(-(t - peakAt) / (RainPlate.BlowFall * tau));
            raw[i] = g - prev;
            prev = g;
        }
        // The √t rise has no bottom to it: taken literally its first step is a single sample, which
        // is energy flat to the top of hearing. A real first contact is not a point: the air under the
        // drop is squeezed out and a thin disc of it trapped (Thoroddsen et al. 2005, J. Fluid Mech.
        // 545, 203-212; Mandre, Mani and Brenner 2009, Phys. Rev. Lett. 102, 134502), and the contact
        // spreads over the drop's tip in some microseconds. So the onset is smoothed over the
        // fountain's own first-contact time, ImpactRise.
        var kernel = OnsetKernel(_rate);
        var y = new float[length + 2 * OnsetHalf];
        double e = 0;
        for (int i = 0; i < y.Length; i++)
        {
            float sum = 0f;
            for (int k = -OnsetHalf; k <= OnsetHalf; k++)
            {
                int j = i - OnsetHalf - k;
                if ((uint)j < (uint)length) sum += kernel[k + OnsetHalf] * raw[j];
            }
            y[i] = sum;
            e += sum * (double)sum;
        }
        float norm = e > 0 ? (float)(1.0 / Math.Sqrt(e)) : 0f;
        for (int i = 0; i < y.Length; i++) y[i] *= norm;
        return _shapes[index] = y;
    }

    private const int OnsetHalf = 3;
    private float[]? _onsetKernel;

    /// <summary>A Gaussian of standard deviation ImpactRise, sampled, summing to one.</summary>
    private float[] OnsetKernel(float rate)
    {
        if (_onsetKernel != null) return _onsetKernel;
        var k = new float[2 * OnsetHalf + 1];
        float s = MathF.Max(0.3f, ImpactRise * rate), sum = 0f;
        for (int i = -OnsetHalf; i <= OnsetHalf; i++) sum += k[i + OnsetHalf] = MathF.Exp(-0.5f * i * i / (s * s));
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        return _onsetKernel = k;
    }

    private const int ClickRing = 1 << 15;          // 0.68 s: a hailstone's bounce lands inside it
    /// <summary>The longest click, samples: a tenth of a second.</summary>
    private const int MaxShape = 4800;
    private readonly float[] _clicks = new float[ClickRing];
    private long _clickNow;

    private void Bubble(int at, float bubbleMm, float gain)
    {
        if (bubbleMm < SmallestBubbleMm || gain <= 0f || BubblePart <= 0f) return;
        float hz = FallingWaterSynth.MinnaertHzMetres / (bubbleMm * 1e-3f);
        // Most bubbles are made shallow and are faint, a few deep and loud (van den Doel 2005).
        float depth = MathF.Pow(_sum.Uniform(), FallingWaterSynth.DepthSkew);
        if (depth < 0.01f) return;
        _sum.Bubble(at, hz, FallingWaterSynth.BubbleDamping(bubbleMm),
                    FallingWaterSynth.BubblePascalsPerMm * bubbleMm * depth * gain * BubblePart,
                    FallingWaterSynth.BubbleRise);
    }

    /// <summary>
    /// One sheet's ringing: its sparse low modes as resonators, its dense upper field as octave
    /// bands of noise whose energy each blow raises. See RainPlate for the physics.
    /// </summary>
    private sealed class PlateState
    {
        public const int MaxModes = 24;
        private const int Bands = 10;            // octaves from 31.5 Hz to 16 kHz
        private const int MaxPending = 64;

        public string Key = "";
        public bool Active, Keep;
        public RainPlate Plate;

        private int _modes;
        private readonly int[] _m = new int[MaxModes], _n = new int[MaxModes];
        private readonly float[] _hz = new float[MaxModes], _re = new float[MaxModes], _im = new float[MaxModes];
        private readonly float[] _cos = new float[MaxModes], _sin = new float[MaxModes], _decay = new float[MaxModes];
        private readonly float[] _gain = new float[MaxModes];

        private readonly float[] _lo = new float[Bands], _hi = new float[Bands];
        private readonly float[] _amp = new float[Bands], _ampDecay = new float[Bands], _bandGain = new float[Bands];
        private readonly float[] _norm = new float[Bands];
        private readonly Resonator[] _res = new Resonator[Bands];
        private int _bands;

        private float _mobility, _modalMass;
        private int _pending;
        private readonly int[] _pAt = new int[MaxPending];
        private readonly float[] _pD = new float[MaxPending], _pV = new float[MaxPending];
        private readonly float[] _pW = new float[MaxPending], _pX = new float[MaxPending], _pY = new float[MaxPending];

        public void Reset()
        {
            Array.Clear(_re); Array.Clear(_im); Array.Clear(_amp);
            for (int b = 0; b < Bands; b++) _res[b] = default;
            _pending = 0;
        }

        public void Configure(string key, RainLayer layer, float rate, float scale)
        {
            Key = key;
            var plate = layer.Plate;
            Plate = plate;
            float view = layer.ViewFactor, area = MathF.Max(0.01f, layer.TotalArea);
            float rc = RainPlate.AirImpedance, m2 = plate.SurfaceDensity, bay = plate.BayArea;
            _mobility = plate.Mobility;
            _modalMass = m2 * bay / 4f;

            // The sparse low modes, up to where they overlap (or the bank is full).
            float overlap = plate.OverlapHz;
            int count = 0;
            for (int m = 1; m <= 12; m++)
                for (int n = 1; n <= 12; n++)
                {
                    float hz = plate.ModeHz(m, n);
                    if (hz < 30f || hz >= overlap || hz > 0.45f * rate) continue;
                    // Insert in order, keeping the lowest MaxModes.
                    int at = count;
                    while (at > 0 && _hz[at - 1] > hz) at--;
                    if (at >= MaxModes) continue;
                    int last = Math.Min(count, MaxModes - 1);
                    for (int j = last; j > at; j--) { _hz[j] = _hz[j - 1]; _m[j] = _m[j - 1]; _n[j] = _n[j - 1]; }
                    _hz[at] = hz; _m[at] = m; _n[at] = n;
                    if (count < MaxModes) count++;
                }
            _modes = count;
            float split = count == MaxModes ? _hz[count - 1] * 1.03f : overlap;
            for (int i = 0; i < _modes; i++)
            {
                float hz = _hz[i];
                float w = MathF.Tau * hz / rate;
                _cos[i] = MathF.Cos(w); _sin[i] = MathF.Sin(w);
                _decay[i] = MathF.Exp(-MathF.PI * hz * plate.Loss(hz) / rate);
                // p = ρc √(σ G S_bay / (8π S)) q̇: a mode's velocity amplitude as pressure at the ear,
                // with the whole sheet's bays heard at once (see RainPlate).
                _gain[i] = rc * MathF.Sqrt(plate.RadiationEfficiency(hz) * view * bay / (8f * MathF.PI * area)) * scale;
            }

            // The continuum above.
            _bands = 0;
            for (float f = 31.5f; f < 20000f && _bands < Bands; f *= 2f)
            {
                float lo = MathF.Max(f / MathF.Sqrt(2f), split), hi = MathF.Min(f * MathF.Sqrt(2f), 0.45f * rate);
                if (hi <= lo * 1.05f) continue;
                int b = _bands++;
                float centre = MathF.Sqrt(lo * hi);
                _lo[b] = lo; _hi[b] = hi;
                // Amplitude √E decays at half the energy's rate ω η.
                _ampDecay[b] = MathF.Exp(-0.5f * MathF.Tau * centre * plate.Loss(centre) / rate);
                // p² = (ρc)² σ E G / (2π m″ S).
                _bandGain[b] = rc * MathF.Sqrt(plate.RadiationEfficiency(centre) * view / (2f * MathF.PI * m2 * area)) * scale;
                float q = centre / (hi - lo);
                _res[b].Tune(centre, q, rate);
                _norm[b] = 1f / MathF.Max(1e-6f, Resonator.NoiseGain(centre, q, rate));
            }
        }

        /// <summary>A blow of this momentum (N·s) lasting this long (s), at a block offset.</summary>
        public void Inject(int at, float impulse, float seconds, float energyWeight, float x, float y)
        {
            if (_pending >= MaxPending || at < 0 || at >= 128) return;
            int i = _pending++;
            _pAt[i] = at; _pD[i] = impulse; _pV[i] = seconds; _pW[i] = energyWeight; _pX[i] = x; _pY[i] = y;
        }

        public void Render(float[] output, EventSum rng, float part)
        {
            // In time order, so each blow lands at its own sample.
            for (int i = 1; i < _pending; i++)
                for (int j = i; j > 0 && _pAt[j] < _pAt[j - 1]; j--)
                {
                    (_pAt[j], _pAt[j - 1]) = (_pAt[j - 1], _pAt[j]);
                    (_pD[j], _pD[j - 1]) = (_pD[j - 1], _pD[j]);
                    (_pV[j], _pV[j - 1]) = (_pV[j - 1], _pV[j]);
                    (_pW[j], _pW[j - 1]) = (_pW[j - 1], _pW[j]);
                    (_pX[j], _pX[j - 1]) = (_pX[j - 1], _pX[j]);
                    (_pY[j], _pY[j - 1]) = (_pY[j - 1], _pY[j]);
                }
            int next = 0;
            for (int s = 0; s < output.Length; s++)
            {
                while (next < _pending && _pAt[next] <= s) { Blow(next); next++; }
                float y = 0f;
                for (int i = 0; i < _modes; i++)
                {
                    float re = _re[i], im = _im[i];
                    y += _gain[i] * re;
                    float nr = (re * _cos[i] - im * _sin[i]) * _decay[i];
                    _im[i] = (re * _sin[i] + im * _cos[i]) * _decay[i];
                    _re[i] = nr;
                }
                if (_bands > 0)
                {
                    float noise = 1.7320508f * rng.Signed();
                    for (int b = 0; b < _bands; b++)
                    {
                        float a = _amp[b];
                        float band = _res[b].Process(noise);
                        y += _bandGain[b] * a * band * _norm[b];
                        _amp[b] = a * _ampDecay[b];
                    }
                }
                output[s] += y * part;
            }
            _pending = 0;
        }

        private void Blow(int i)
        {
            float momentum = _pD[i], tau = _pV[i], w = _pW[i];
            // Modes: the blow's momentum at each mode's frequency, through the mode's shape where it
            // landed, into the bay's modal mass. Weighted in amplitude by √ of the energy weight.
            if (_modes > 0)
            {
                float impulse = momentum * MathF.Sqrt(w) / _modalMass;
                float px = MathF.PI * _pX[i], py = MathF.PI * _pY[i];
                for (int k = 0; k < _modes; k++)
                {
                    float shape = MathF.Sin(_m[k] * px) * MathF.Sin(_n[k] * py);
                    _re[k] += impulse * shape * RainPlate.BlowMagnitude(tau, _hz[k]);
                }
            }
            if (_bands > 0)
            {
                float peak = momentum / (tau * RainPlate.BlowShapeArea);
                float energy = _mobility * peak * peak * tau * RainPlate.BlowShapeEnergy * w;
                for (int b = 0; b < _bands; b++)
                {
                    float e = energy * RainPlate.BlowShare(tau, _lo[b], _hi[b]);
                    float a = _amp[b];
                    _amp[b] = MathF.Sqrt(a * a + e);
                }
            }
        }
    }
}
