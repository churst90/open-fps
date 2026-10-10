using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// Water falling into water, and onto stone, as the events it is made of (docs/RUNNING_WATER.md
/// section 12: the physics, the sources and how each constant was fitted).
///
/// A drop striking a pool clicks and sometimes traps a bubble, which rings at its Minnaert note as it
/// rises; a lump throws a crown that tears into spray and pinches off a burst of bubbles of every size;
/// on stone there is no crater, only a sharper click and a splash. A bubble's loudness is skewed by the
/// depth it was made at: a few plinks over a bed of faint ones is the difference between water and a hiss.
///
/// Each fall lands at one of the spec's <see cref="WaterFeatureSpec.Taps"/>, and with more than one place
/// a tap each event lands at one place of its tap (<see cref="SetSpread"/>): the places add up to the tap.
/// <see cref="NextPlaces"/> hands out every place, <see cref="NextTaps"/> each tap, <see cref="Next"/>
/// the whole at one point.
///
/// Fitted, here and nowhere else: <see cref="BubblePascalsPerMm"/>, <see cref="ImpactPascals"/>,
/// <see cref="SplashEfficiency"/> and the plunge's air share, against measured and recorded fountains.
/// Their laws are physical; re-fit them, never nudge them. The rain (RainSynth) shares the first two.
/// </summary>
public sealed class FallingWaterSynth
{
    // ── The fitted constants ─────────────────────────────────────────────────────────────────────

    /// <summary>A 1 mm bubble's first peak at its deepest, Pa at a metre in air. Goes as R, times the
    /// depth factor: van den Doel's R^1.5 put so much energy in the largest bubbles that they rang as a
    /// steady note.</summary>
    public const float BubblePascalsPerMm = 0.015f;

    /// <summary>An impact's spike into a pool, Pa at a metre in air, for a 1 mm radius drop at 5 m/s.
    /// Its energy goes as m v³ (Franz 1959) and a drop's rise is fixed, so its peak goes as (r v)^1.5.
    /// Quiet: a drip's plink is its trapped bubble, not its impact (Phillips, Agarwal and Jordan 2018),
    /// and at 0.0142 the drop clicks summed to Gaussian noise. On stone, <see cref="HardImpactPascals"/>.</summary>
    public const float ImpactPascals = 0.003f;

    /// <summary>A drop's click on a hard surface (stone, a road, a roof's top face), Pa at a metre for
    /// 1 mm at 5 m/s: it stops in its own length on the film and splashes flat. 1.2 times the pool's old
    /// 0.0142, which the rain on streets and roofs was fitted with (RainSynth).</summary>
    public const float HardImpactPascals = 0.01704f;

    /// <summary>
    /// The share of the energy a lump's crown (or, on stone, its lamella) takes that leaves as sound.
    /// FITTED so the band envelopes above 1 kHz move as the recordings' do. For scale: a drop's whole
    /// impact radiates 10⁻⁶ to 10⁻⁵ of its kinetic energy in water (Franz 1959; Nystuen 1986), and only a
    /// small part of that crosses into air.
    /// </summary>
    public const float SplashEfficiency = 1.5e-5f;

    /// <summary>How fast a drop's impact force arrives, s: the first contact, microseconds.</summary>
    private const float ImpactRise = 16e-6f;

    /// <summary>A coherent lump's force rise as a share of its r / v: it lands in the froth of the one
    /// before it, not on still water. Measured, not set by ear: lumps striking as sharply as drops were
    /// the static in the hiss (a 2-8 kHz kurtosis of 5.8 over 10 ms); at 0.07 it is 3.06, the recordings'
    /// own, and 0.05-0.12 measure the same. Small lumps still strike in a drop's rise.</summary>
    private const float LumpCushion = 0.07f;

    /// <summary>β in the depth factor u^β: how skewed the bubbles' loudness is.</summary>
    public const float DepthSkew = 4f;

    // ── The splash ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The share of a lump's kinetic energy that goes into its crown on a pool. Most of it goes
    /// into the crater: Engel (1966) found the crater's potential energy at its deepest to be about
    /// half of the drop's kinetic energy, the rest spread over the crown, the surface waves and the
    /// jet; a quarter for the crown is an estimate inside that.</summary>
    public const float PoolCrownShare = 0.25f;

    /// <summary>The same on wet stone: no crater, so the lamella takes nearly all of it (the
    /// estimate is the rest less what the film's viscosity takes).</summary>
    public const float RockCrownShare = 0.8f;

    /// <summary>How long a crown sheds its spray, in units of the lump's r / v, plus a floor: the rim
    /// breaks up while the crown rises, a couple of r / v (Deegan et al. 2008), 1.5-4 ms for a fountain's
    /// lumps, as the recordings' loud moments last (3-4 ms wide at half height). Fitted within that.</summary>
    private const float SplashDurations = 1.5f;
    private const float SplashFloorSeconds = 0.0004f;

    /// <summary>The splash band's middle for a 5 mm lump at 4 m/s, Hz (<see cref="SplashCentreHz"/>):
    /// secondary droplets of 0.05-0.5 mm striking in 10-250 µs and trapping bubbles of a few tenths of
    /// a millimetre. FITTED with the efficiency to the recordings' 4-12 kHz balance.</summary>
    private const float SplashCentreReferenceHz = 2700f;

    /// <summary>How far one splash's band middle scatters about that, the standard deviation of its
    /// natural log, and how far its band reaches either side of its middle (a factor; EventSum.Burst
    /// steep). Fitted so bands an octave apart move apart, as the recordings' do.</summary>
    private const float SplashScatter = 0.7f, SplashBandHalfWidth = 1.6f;

    /// <summary>ρ c of air, Pa s/m: the impedance a radiated power meets.</summary>
    private const float RhoC = 413f;

    // ── Physical constants ───────────────────────────────────────────────────────────────────────

    /// <summary>Minnaert: f R = 3.26 m/s for an air bubble near the surface of water.</summary>
    public const float MinnaertHzMetres = 3.26f;

    /// <summary>The bubble's note climbs as it rises: ξ in f = f0 (1 + ξ d t) (van den Doel 2005).</summary>
    public const float BubbleRise = 0.1f;

    /// <summary>The smallest bubble worth ringing: under 0.16 mm it sings above 20 kHz.</summary>
    private const float SmallestBubbleMm = 0.16f;

    /// <summary>Hinze scale, mm: the bubble size spectrum under a plunge steepens above it.</summary>
    private const float HinzeMm = 1.0f;

    /// <summary>The share of the regular-window drops that trap their bubble.</summary>
    private const float RegularShare = 0.9f;

    /// <summary>The share of drops over 2.2 mm diameter that make their type II bubble. Medwin et al.
    /// call it the dominant sound of a large drop, so most of them.</summary>
    private const float IrregularShare = 0.8f;

    /// <summary>The chance a large drop's crater also sheds a weaker second bubble.</summary>
    private const float SecondaryShare = 0.3f;

    /// <summary>The share of a coherent lump's craters that trap one. Fitted: a crater in the froth of the
    /// lumps before it closes on a cloud more often than on one big bubble, and at 0.6 the big lumps'
    /// glugs stood 5-8 dB over every recorded fountain at 250-500 Hz.</summary>
    private const float ChunkShare = 0.2f;

    /// <summary>How much air a plunge drives under, per litre of water, at 3 m/s over the 1 m/s
    /// below which a falling sheet enters without entraining: about one per cent, fitted with
    /// <see cref="BubblePascalsPerMm"/> and <see cref="ImpactPascals"/>.</summary>
    private const float PlungeAirShare = 0.012f;

    /// <summary>How long a lump's cavity takes to close and pinch off its bubbles, s, for a 5 mm lump:
    /// the burst the plunge's bubbles come in. Scales with the lump's size.</summary>
    private const float CavitySeconds = 0.012f;

    /// <summary>The most bubbles of one lump's burst rendered; more are stood for, each carrying
    /// √(real / rendered) of them.</summary>
    private const int MaxPerBurst = 4;

    /// <summary>The most bubbles of one kind a fall renders per block; more are stood for, each
    /// carrying √(real / rendered) of them so the energy is kept. Bubbles ring for milliseconds and
    /// are most of the cost; their notes are spread in pitch and time, so a dozen a block of each
    /// kind already sum to a wash.</summary>
    private const int MaxPerBlock = 12;

    /// <summary>The most impacts a fall renders per block: in practice every one. They are what the hiss
    /// is made of and are not thinned as the bubbles are: a few large clicks standing for many were
    /// grain in the whoosh, where many of their own size merge into it.</summary>
    private const int MaxImpactsPerBlock = 256;

    /// <summary>How unevenly ONE jet sheds its drops: the standard deviation of the log of its rate
    /// from slug to slug. A column necks and bursts in slugs tens of milliseconds long.</summary>
    private const float ClumpSigma = 0.7f;

    private const int Block = 128;

    public readonly WaterFeatureSpec Spec;
    private readonly float _rate;
    private readonly EventSum[] _sums;
    private readonly EventSum _rng;
    private readonly FallState[] _falls;
    private int _untilBlock;

    /// <summary>The water is running. When it stops, what is in the air still lands.</summary>
    public bool Running = true;
    private float _flow = 1f;
    private float _wind = float.NaN;

    /// <summary>The wind at the spray, m/s.</summary>
    public float Wind;

    /// <summary>Each part's share, for the lab to take the sound apart by muting: the impacts, the
    /// drops' bubbles, the lumps' bubbles, the plunge's, and the lumps' splashes. One in the game.</summary>
    public float ImpactPart = 1f, DropBubblePart = 1f, LumpBubblePart = 1f, PlungePart = 1f, SplashPart = 1f;

    /// <summary>How many taps the feature is heard from (<see cref="WaterFeatureSpec.Taps"/>).</summary>
    public int TapCount => _taps;

    /// <summary>How many places each tap is heard from (its middle first): one unless made with more.</summary>
    public int PlacesPerTap => _placesPerTap;

    private readonly int _taps, _placesPerTap;
    private readonly float[] _tapSpread, _tapMiddle;

    /// <summary>How much of tap <paramref name="tap"/> its outer places carry, 0 to 1
    /// (ExtendedSources.Shares). Takes effect at the next control call.</summary>
    public void SetSpread(int tap, float spread)
    {
        if (tap >= 0 && tap < _taps) _tapSpread[tap] = Math.Clamp(spread, 0f, 1f);
    }

    /// <summary>How much of the spec's flow is falling now, as a share (1: as the spec says): every rate
    /// goes as it, the sizes do not. Running water holds it at 0 until its first control call and at 1
    /// after, and follows its flow with <see cref="Retune"/>.</summary>
    public float FlowScale = 1f;

    /// <summary>For running water: where an event of the fall feeding tap <c>tap</c> is written, if not
    /// to this synth's own sums. Set once, before the first sample; the sums it hands back must advance
    /// with <see cref="Advance"/>, one sample per call, so the events land at the sample they are meant
    /// to. Null for a fountain.</summary>
    public Func<int, EventSum>? Placer;

    /// <summary>How long a drop's blow on stone takes to build, as a share of its r / v. Zero for the
    /// fountain (its first contact, ImpactRise). Running water sets it: its stone is paving or a pipe's
    /// bend under a film of the same water, which the drop meets first, and its blow builds the way the
    /// rain's does on a wet street (RainSurfaces.BlowPeakAt, the peak at 0.2 D / v, a Gaussian of about
    /// 0.2 r / v).</summary>
    public float HardCushion;

    /// <summary>The share of a drop's blow on wet stone heard as its spray: the rain's on a wet street
    /// (RainSynth.SprayShare, fitted to recorded rain), only with a cushioned blow (<see cref="HardCushion"/>).
    /// The cushion takes the top octaves out of the click; the droplets thrown off the film put them back.</summary>
    private const float WetSprayShare = RainSynth.SprayShare;

    /// <summary>The most wet sprays a fall renders a block; more are stood for by energy.</summary>
    private const int MaxSpraysPerBlock = 6;
    private int _spraysThisBlock;
    private float _sprayCarry;

    /// <summary>A drop's spray off wet stone: band noise rising over a millisecond and dying over a few,
    /// carrying <see cref="WetSprayShare"/> of the energy its blow would have had as a bare spike.</summary>
    private void WetSpray(EventSum place, int at, float peakPascals)
    {
        float energy = peakPascals * peakPascals * ImpactRise * 1.7724539f * WetSprayShare + _sprayCarry;
        // Thinned: past the block's few, the energy is carried to the next one rendered.
        if (_spraysThisBlock >= MaxSpraysPerBlock) { _sprayCarry = energy; return; }
        _sprayCarry = 0f;
        _spraysThisBlock++;
        const float rise = RainSynth.SprayRiseSeconds, decay = RainSynth.SprayDecaySeconds;
        float pascals = MathF.Sqrt(energy / (rise / 3f + decay / 2f));
        place.Burst(at, rise, decay, pascals * MathF.Sqrt(SplashPart), RainSynth.SprayLowHz, RainSynth.SprayHighHz);
    }

    /// <summary>Schedules the next sample's events without reading this synth's own sums: for a caller
    /// whose <see cref="Placer"/> takes every event.</summary>
    public void Advance() => Step();

    /// <summary>The place one event of fall <paramref name="f"/> lands at: its tap's middle by the
    /// middle's share, otherwise one of the places round it.</summary>
    private EventSum PlaceFor(FallState f)
    {
        if (Placer != null) return Placer(f.Tap);
        if (_placesPerTap == 1) return f.Sum;
        float middle = _tapMiddle[f.Tap];
        float u = f.Sum.Uniform();
        if (u >= middle && middle < 1f)
        {
            int outer = _placesPerTap - 1;
            int k = 1 + Math.Min(outer - 1, (int)((u - middle) / (1f - middle) * outer));
            return _sums[f.Tap * _placesPerTap + k];
        }
        return f.Sum;
    }

    private sealed class FallState
    {
        public WaterFallSpec Spec = null!;
        public EventSum Sum = null!;                      // the tap it lands at (its middle)
        public int Tap;
        public bool Rock;
        public float DropRate, ChunkRate, BubbleRate;   // per second, at full flow
        public float DropShare, FallMetres;               // the spec's, or what Retune last set
        public float DropMean, DropMax;                  // m
        public float ChunkMean, ChunkMax;                // m
        public float ChunkMeanVolume;                     // m^3
        public float ChunkSpeed;                          // m/s
        public float BubbleMeanVolume;                    // m^3
        public float Wander, WanderTarget, WanderClock;  // the column's breakup moving about
        public float Clump = 1f, ClumpFrom = 1f, ClumpTo = 1f, ClumpLength = 1f, ClumpClock; // drops arriving in bunches
        public float Spread;                              // 1/√streams: how much of one jet's wobble is left in the sum
        public int Order;                                 // the drop sizes' gamma order
        public int LumpOrder;                             // the lumps' gamma order
    }

    public FallingWaterSynth(WaterFeatureSpec spec, float sampleRate, int seed, int placesPerTap = 1)
    {
        Spec = spec;
        _rate = sampleRate;
        int taps = Math.Max(1, spec.Taps?.Length ?? 1);
        _taps = taps;
        _placesPerTap = Math.Max(1, placesPerTap);
        _tapSpread = new float[taps];
        _tapMiddle = new float[taps];
        Array.Fill(_tapMiddle, 1f);
        _sums = new EventSum[taps * _placesPerTap];
        // A tap's middle has the seed a one-place tap has, so adding places leaves it as it was.
        for (int t = 0; t < taps; t++)
            for (int k = 0; k < _placesPerTap; k++)
                _sums[t * _placesPerTap + k] = new EventSum(sampleRate, seed * 7919 + t * 104729 + 1 + k * 15485863);
        _rng = _sums[0];
        _falls = new FallState[spec.Falls.Length];
        for (int i = 0; i < _falls.Length; i++)
        {
            var f = spec.Falls[i];
            var s = new FallState
            {
                Spec = f,
                Sum = _sums[Math.Clamp(f.Tap, 0, taps - 1) * _placesPerTap],
                Tap = Math.Clamp(f.Tap, 0, taps - 1),
                Rock = f.Onto == WaterSurface.Rock,
                Spread = 1f / MathF.Sqrt(Math.Max(1, f.Streams)),
                Order = Math.Clamp(f.DropSizeOrder, 1, 16),
                LumpOrder = Math.Clamp(f.LumpSizeOrder, 1, 16),
            };
            s.BubbleMeanVolume = PlungeMeanVolume();
            Tune(s, f.FlowLitresPerSecond, f.DropShare, f.MeanDropRadiusMm, f.ChunkRadiusMm, f.FallMetres);
            _falls[i] = s;
        }
    }

    /// <summary>A fall's rates from its flow, how it arrives and how far it falls.</summary>
    private static void Tune(FallState s, float flowLitresPerSecond, float dropShare, float meanDropMm, float chunkMm, float fallMetres)
    {
        float q = MathF.Max(0f, flowLitresPerSecond) * 1e-3f;                     // m^3/s
        s.DropShare = Math.Clamp(dropShare, 0f, 1f);
        s.FallMetres = MathF.Max(0f, fallMetres);
        s.DropMean = MathF.Max(0.1f, meanDropMm) * 1e-3f;
        s.DropMax = MathF.Max(meanDropMm, s.Spec.MaxDropRadiusMm) * 1e-3f;
        float meanVolume = 4f / 3f * MathF.PI * MeanCube(s.DropMean, MinDropRadius, s.DropMax, s.Order);
        s.DropRate = s.DropShare * q / meanVolume;
        // The lumps: a gamma law of their own order about the stated size, up to three times it.
        s.ChunkMean = MathF.Max(0.3f, chunkMm) * 1e-3f;
        s.ChunkMax = 3f * s.ChunkMean;
        s.ChunkMeanVolume = 4f / 3f * MathF.PI * MeanCube(s.ChunkMean, MinDropRadius, s.ChunkMax, s.LumpOrder);
        s.ChunkRate = (1f - s.DropShare) * q / s.ChunkMeanVolume;
        s.ChunkSpeed = MathF.Sqrt(2f * 9.81f * s.FallMetres);
        float entrain = s.Rock ? 0f : PlungeAirShare * MathF.Max(0f, s.ChunkSpeed - 1f) / 2f;
        s.BubbleRate = (1f - s.DropShare) * q * entrain / s.BubbleMeanVolume;
    }

    /// <summary>
    /// For running water: fall <paramref name="fall"/> now carries this much, arriving this way, from this
    /// high. The sheet a lip lets go of thickens with the flow and comes down coherent in bigger lumps
    /// (RunningWaterSynth.FallFor), and a thicker plunge drives more air: so a fall grows faster than its
    /// flow. Rates only; nothing allocates.
    /// </summary>
    public void Retune(int fall, float flowLitresPerSecond, float dropShare, float meanDropMm, float chunkMm, float fallMetres)
    {
        if (fall < 0 || fall >= _falls.Length) return;
        Tune(_falls[fall], flowLitresPerSecond, dropShare, meanDropMm, chunkMm, fallMetres);
    }

    /// <summary>How many drops, lumps and plunge bubbles a second each fall makes, for the lab.</summary>
    public (string Name, float Drops, float Lumps, float Bubbles)[] Census()
    {
        var c = new (string, float, float, float)[_falls.Length];
        for (int i = 0; i < _falls.Length; i++) c[i] = (_falls[i].Spec.Name, _falls[i].DropRate, _falls[i].ChunkRate, _falls[i].BubbleRate);
        return c;
    }

    private const float MinDropRadius = 0.2e-3f;

    /// <summary>The mean of r³ for radii over <paramref name="lo"/> following a gamma law of this
    /// order with mean <paramref name="mean"/> (see <see cref="WaterFallSpec.DropSizeOrder"/>), cut
    /// to [lo, hi]. Order 1 is the exponential.</summary>
    private static float MeanCube(float mean, float lo, float hi, int order)
    {
        double num = 0, den = 0;
        for (int k = 0; k < 400; k++)
        {
            double r = lo + (hi - lo) * (k + 0.5) / 400.0;
            double x = (r - lo) / mean;
            double w = Math.Pow(x, order - 1) * Math.Exp(-order * x);
            num += w * r * r * r;
            den += w;
        }
        return (float)(num / den);
    }

    /// <summary>A radius, m: over the smallest, a gamma law of this order about its mean (the sum of
    /// that many exponentials); the few over the largest are drawn again.</summary>
    private float DrawRadius(EventSum rng, float mean, float max, int order)
    {
        for (int tries = 0; tries < 32; tries++)
        {
            float x = 0f;
            for (int j = 0; j < order; j++) x -= MathF.Log(MathF.Max(1e-7f, 1f - rng.Uniform()));
            float r = MinDropRadius + mean / order * x;
            if (r <= max) return r;
        }
        return MinDropRadius + mean;
    }

    /// <summary>The mean volume of a plunge bubble under the Deane-Stokes spectrum, m³.</summary>
    private static float PlungeMeanVolume()
    {
        double num = 0, den = 0;
        for (int k = 0; k < 600; k++)
        {
            double rmm = SmallestBubbleMm * Math.Pow(6.0 / SmallestBubbleMm, (k + 0.5) / 600.0);
            double dr = rmm * Math.Log(6.0 / SmallestBubbleMm) / 600.0;
            double n = PlungeDensity(rmm) * dr;
            double r = rmm * 1e-3;
            num += n * 4.0 / 3.0 * Math.PI * r * r * r;
            den += n;
        }
        return (float)(num / den);
    }

    /// <summary>The Deane-Stokes size spectrum, unnormalised, radius in mm, with the large end let go
    /// gently: a plunge this size cannot hold a bubble much over the depth it drives air to, and a
    /// hard cut at the top piled every large bubble onto one note (a steady line at 550 Hz).</summary>
    private static double PlungeDensity(double rmm)
        => (rmm < HinzeMm ? Math.Pow(rmm, -1.5) : Math.Pow(HinzeMm, -1.5) * Math.Pow(rmm / HinzeMm, -10.0 / 3.0))
           * Math.Exp(-rmm / LargestPlungeMm);

    /// <summary>The scale over which large plunge bubbles thin out, mm.</summary>
    private const double LargestPlungeMm = 2.5;

    /// <summary>A plunge bubble's radius, mm, drawn from the Deane-Stokes spectrum by rejection on a
    /// log-uniform proposal.</summary>
    private static float DrawPlungeBubbleMm(EventSum rng)
    {
        double lo = Math.Log(SmallestBubbleMm), hi = Math.Log(6.0);
        double peak = PlungeDensity(SmallestBubbleMm) * SmallestBubbleMm;
        for (int tries = 0; tries < 64; tries++)
        {
            double rmm = Math.Exp(lo + (hi - lo) * rng.Uniform());
            if (rng.Uniform() * peak <= PlungeDensity(rmm) * rmm) return (float)rmm;
        }
        return SmallestBubbleMm;
    }

    /// <summary>The terminal speed of a raindrop of radius r (m), m/s (Atlas et al. 1973).</summary>
    public static float TerminalSpeed(float r)
        => MathF.Max(0.27f, 9.65f - 10.3f * MathF.Exp(-0.6f * 2000f * r));

    /// <summary>How fast a drop of radius r arrives after falling h from rest, m/s: gravity against
    /// a drag that holds it to its terminal speed.</summary>
    public static float ArrivalSpeed(float r, float h)
    {
        float vt = TerminalSpeed(r);
        return vt * MathF.Sqrt(1f - MathF.Exp(-2f * 9.81f * h / (vt * vt)));
    }

    /// <summary>A bubble's decay rate, 1/s, for a radius in mm: d = 0.13 / R + 0.0072 R^−3/2, R in
    /// metres (van den Doel 2005, after Devin's radiation, thermal and viscous losses). About 360/s at
    /// a millimetre, which is a damping ratio of 0.04.</summary>
    public static float BubbleDecay(float rmm)
    {
        float r = MathF.Max(0.05f, rmm) * 1e-3f;
        return 0.13f / r + 0.0072f * MathF.Pow(r, -1.5f);
    }

    /// <summary>The damping ratio that decay rate is at the bubble's own note.</summary>
    public static float BubbleDamping(float rmm) => BubbleDecay(rmm) / (MathF.PI * MinnaertHzMetres / (rmm * 1e-3f));

    /// <summary>The type II bubble's radius, mm, for a drop of radius r (mm): 10 kHz from a 2.2 mm
    /// drop down to 2 kHz from a 6 mm one, the note falling as D^−1.6 (Medwin et al. 1992).</summary>
    public static float TypeTwoBubbleMm(float rmm)
    {
        float hz = 10000f * MathF.Pow(2f * rmm / 2.2f, -1.6f);
        return MinnaertHzMetres / hz * 1e3f;
    }

    /// <summary>
    /// A splash's spray, as a burst of band noise at the rms its energy gives it: the lump's kinetic
    /// energy E = ½ m v², the crown's share of it, and the splash's acoustic efficiency, radiated as
    /// a monopole over the half-space above the surface for the burst's effective duration T
    /// (p² = η E ρc / (2π T) at a metre). Rises in a fifth of a millisecond, decays over a few r / v.
    /// </summary>
    public static (float Rise, float Decay, float Pascals) Splash(float radius, float speed, float crownShare)
    {
        float mass = 1000f * 4f / 3f * MathF.PI * radius * radius * radius;
        float energy = 0.5f * mass * speed * speed * crownShare * SplashEfficiency;
        float rise = 0.0002f;
        float decay = SplashFloorSeconds + SplashDurations * radius / MathF.Max(0.3f, speed);
        float t = rise / 3f + decay / 2f;
        return (rise, decay, MathF.Sqrt(energy * RhoC / (2f * MathF.PI * t)));
    }

    /// <summary>The middle of a splash's band, Hz. The crown's rim sheds droplets about as thick as the
    /// rim, which thins as the impact's Reynolds number grows (r_s ∝ r Re^−½; Thoroddsen 2002, Deegan et
    /// al. 2008), so the droplets' strikes and bubbles sit higher for a faster lump and lower for a
    /// bigger one: f ∝ v / r_s ∝ v^1.5 r^−0.5. Which droplets a given crown throws varies from splash
    /// to splash (the scatter drawn round this), so each splash lights its own octave or two, not all of
    /// them at once.</summary>
    public static float SplashCentreHz(float radius, float speed)
        => SplashCentreReferenceHz * MathF.Pow(MathF.Max(0.3f, speed) / 4f, 1.5f) * MathF.Pow(radius / 5e-3f, -0.5f);

    private static float Gauss(EventSum s)
        => MathF.Sqrt(-2f * MathF.Log(MathF.Max(1e-7f, s.Uniform()))) * MathF.Cos(MathF.Tau * s.Uniform());

    /// <summary>Things that move on the scale of seconds: the jet's top breaking up now more, now
    /// less; the water being turned on or off.</summary>
    public void Control(float dt)
    {
        // A pump coming up to pressure or running down takes a couple of seconds.
        float target = Running ? 1f : 0f;
        _flow += Math.Clamp(target - _flow, -dt / 2f, dt / 2f);
        if (_placesPerTap > 1)
        {
            Span<float> shares = stackalloc float[_placesPerTap];
            for (int t = 0; t < _taps; t++)
            {
                ExtendedSources.Shares(_tapSpread[t], shares);
                _tapMiddle[t] = shares[0];
            }
        }
        foreach (var f in _falls)
        {
            f.WanderClock -= dt;
            if (f.WanderClock <= 0f)
            {
                // The top of a jet does not break up the same way twice: sometimes it holds together
                // and comes down as a column, sometimes it bursts into spray. Seconds at a time. Each
                // jet or strand of a fall does it on its own, so the fall as a whole wanders by the
                // square root of their count less.
                f.WanderTarget = 0.18f * f.Spread * _rng.Signed();
                f.WanderClock = 0.4f + 1.6f * _rng.Uniform();
            }
            f.Wander += (f.WanderTarget - f.Wander) * MathF.Min(1f, dt * 2f);
        }
    }

    /// <summary>The next sample of the whole feature at one point, pascals at a metre.</summary>
    public float Next()
    {
        Step();
        float sum = 0f;
        for (int t = 0; t < _sums.Length; t++) sum += _sums[t].Next();
        return sum;
    }

    /// <summary>The next sample of each tap, pascals at a metre from that tap: its places summed.</summary>
    public void NextTaps(Span<float> taps)
    {
        Step();
        for (int t = 0; t < _taps; t++)
        {
            float v = 0f;
            for (int k = 0; k < _placesPerTap; k++) v += _sums[t * _placesPerTap + k].Next();
            if (t < taps.Length) taps[t] = v;
        }
    }

    /// <summary>The next sample of every place of every tap, tap by tap, its middle first:
    /// <paramref name="places"/>[tap · PlacesPerTap + place].</summary>
    public void NextPlaces(Span<float> places)
    {
        Step();
        for (int i = 0; i < _sums.Length; i++)
        {
            float v = _sums[i].Next();
            if (i < places.Length) places[i] = v;
        }
    }

    private void Step()
    {
        if (--_untilBlock <= 0)
        {
            _untilBlock = Block;
            Schedule(Block / _rate);
        }
    }

    private void Schedule(float dt)
    {
        _spraysThisBlock = 0;
        // The wind is handed in once a control call, eleven milliseconds apart; glide to it over a
        // few blocks so the spray's share never steps.
        _wind = float.IsNaN(_wind) ? Wind : _wind + (Wind - _wind) * MathF.Min(1f, dt / 0.05f);
        if (_flow <= 0f) return;
        float wind = _wind;
        foreach (var f in _falls)
        {
            // Wind breaks more of a column into spray, and the spray is what it carries away.
            float share = Math.Clamp(f.DropShare + f.Wander + 0.03f * MathF.Max(0f, wind - 2f), 0.05f, 1f);
            float drift = Math.Clamp(f.Spec.DriftPerMetrePerSecond * (wind - 2f), 0f, 0.5f);
            float dropScale = f.DropShare > 0f ? share / f.DropShare : 0f;
            float coherentScale = f.DropShare < 1f ? (1f - share) / (1f - f.DropShare) : 0f;

            // A jet's column necks and bursts in slugs whose drops arrive together, tens of milliseconds
            // at a time: the flicker of real spray. Many jets flicker by the root of their count less.
            // The rate glides from slug to slug: stepped, tens a second, it was a crackle in the hiss.
            var rng = f.Sum;
            f.ClumpClock -= dt;
            if (f.ClumpClock <= 0f)
            {
                float g = MathF.Sqrt(-2f * MathF.Log(MathF.Max(1e-6f, rng.Uniform()))) * MathF.Cos(MathF.Tau * rng.Uniform());
                float sigma = ClumpSigma * f.Spread;
                f.ClumpFrom = f.Clump;
                f.ClumpTo = MathF.Exp(sigma * g - 0.5f * sigma * sigma);
                f.ClumpLength = 0.015f + 0.05f * rng.Uniform();
                f.ClumpClock = f.ClumpLength;
            }
            f.Clump = f.ClumpTo + (f.ClumpFrom - f.ClumpTo) * MathF.Max(0f, f.ClumpClock / f.ClumpLength);
            float flow = _flow * MathF.Max(0f, FlowScale);
            Drops(f, f.DropRate * dropScale * flow * dt * f.Clump, drift);
            Chunks(f, f.ChunkRate * coherentScale * flow * dt * f.Clump);
        }
    }

    private void Drops(FallState f, float mean, float drift)
    {
        var sum = f.Sum;
        int real = sum.Poisson(mean);
        if (real == 0) return;
        int n = Math.Min(real, MaxImpactsPerBlock);
        float weight = MathF.Sqrt(real / (float)n);
        // Every drop's impact is rendered; one drop in `stride` also traps its bubble, for the rest.
        int stride = (n + MaxPerBlock - 1) / MaxPerBlock;
        float ringWeight = weight * MathF.Sqrt(stride);
        for (int k = 0; k < n; k++)
        {
            int at = (int)(sum.Uniform() * Block);
            float r = DrawRadius(sum, f.DropMean, f.DropMax, f.Order);
            float v = ArrivalSpeed(r, f.FallMetres);
            // The force arrives as the drop's front meets the surface and lasts while it buries itself.
            float tau = r / v;
            float impact = ImpactPascals * MathF.Pow(r / 1e-3f * v / 5f, 1.5f) * weight * ImpactPart;
            var place = PlaceFor(f);
            // On stone the drop stops in its own length and splashes flat: a shorter force, and
            // nothing trapped. Small drops falling far are also the ones the wind takes to the
            // paving round a pool.
            if (f.Rock || (drift > 0f && r < 1e-3f && sum.Uniform() < drift * (1f - r / 1e-3f)))
            {
                // Onto a film of running water the blow builds over a share of r / v (HardCushion); the
                // same energy over the longer rise, so the peak comes down as its root.
                float hardRise = MathF.Max(ImpactRise, HardCushion * tau);
                float hard = impact * (HardImpactPascals / ImpactPascals);
                place.Impact(at, hardRise, 0.4f * tau, hard * MathF.Sqrt((1f - (HardCushion > 0f ? WetSprayShare : 0f)) * ImpactRise / hardRise));
                if (HardCushion > 0f) WetSpray(place, at, hard);
                continue;
            }
            place.Impact(at, ImpactRise, tau, impact);
            if (k % stride != 0) continue;

            float rmm = r * 1e3f;
            int later = at + (int)(0.003f * _rate * (0.5f + sum.Uniform()));
            if (rmm >= 0.4f && rmm <= 0.55f && v > 0.8f * TerminalSpeed(r))
            {
                // The regular bubble: always about the same size, rain on a lake.
                if (sum.Uniform() < RegularShare) Ring(place, later, 0.18f + 0.08f * sum.Uniform(), ringWeight * DropBubblePart);
            }
            else if (rmm >= 1.1f)
            {
                if (sum.Uniform() > IrregularShare) continue;
                float b = TypeTwoBubbleMm(rmm) * (0.8f + 0.4f * sum.Uniform());
                Ring(place, later, b, ringWeight * DropBubblePart);
                if (sum.Uniform() < SecondaryShare)
                    Ring(place, later + (int)(0.004f * _rate * sum.Uniform()), b * (0.3f + 0.6f * sum.Uniform()), 0.4f * ringWeight * DropBubblePart);
            }
        }
    }

    private void Chunks(FallState f, float mean)
    {
        var sum = f.Sum;
        int real = sum.Poisson(mean);
        if (real == 0) return;
        int n = Math.Min(real, MaxImpactsPerBlock);
        float weight = MathF.Sqrt(real / (float)n);
        int stride = (n + MaxPerBlock - 1) / MaxPerBlock;
        float ringWeight = weight * MathF.Sqrt(stride);
        float crown = f.Rock ? RockCrownShare : PoolCrownShare;
        // A lump's share of the plunge's bubbles goes as its volume; its mean count, for a lump of
        // the mean volume, is the plunge's rate over the lumps'.
        float bubblesPerVolume = f.ChunkRate > 0f ? f.BubbleRate / (f.ChunkRate * f.ChunkMeanVolume) : 0f;
        for (int k = 0; k < n; k++)
        {
            int at = (int)(sum.Uniform() * Block);
            float r = DrawRadius(sum, f.ChunkMean, f.ChunkMax, f.LumpOrder);
            float v = f.ChunkSpeed * (0.9f + 0.2f * sum.Uniform());
            // Into the froth of the lump before it, its force builds over a share of r / v (LumpCushion):
            // the same energy (m v³) over a longer rise, so the peak comes down as the root of it.
            float rise = MathF.Max(ImpactRise, LumpCushion * r / v);
            var place = PlaceFor(f);
            place.Impact(at, rise, (f.Rock ? 0.4f : 1f) * r / v,
                       MathF.Sqrt(ImpactRise / rise) * ImpactPascals * MathF.Pow(r / 1e-3f * v / 5f, 1.5f) * weight * ImpactPart);

            // Its crown, or on stone its lamella, tearing into spray: every lump, as loud as it is big.
            var (sr, sd, sp) = Splash(r, v, crown);
            float fc = SplashCentreHz(r, v) * MathF.Exp(SplashScatter * Gauss(sum));
            float spread = SplashBandHalfWidth;
            place.Burst(at, sr, sd, sp * weight * SplashPart, fc / spread, MathF.Min(fc * spread, 0.45f * _rate), steep: true);

            // On a film of running water a lump spreads into a thin sheet that throws fine droplets, as
            // a raindrop on a wet street does (running water only, HardCushion).
            if (f.Rock && HardCushion > 0f)
                WetSpray(place, at, HardImpactPascals * MathF.Pow(r / 1e-3f * v / 5f, 1.5f) * weight * ImpactPart);

            if (f.Rock) continue;

            // The plunge's bubbles: pinched off in a burst as this lump's cavity closes.
            float volume = 4f / 3f * MathF.PI * r * r * r;
            int count = sum.Poisson(bubblesPerVolume * volume * weight * weight);
            if (count > 0)
            {
                int rendered = Math.Min(count, MaxPerBurst);
                float bw = MathF.Sqrt(count / (float)rendered) * PlungePart;
                float cavity = CavitySeconds * r / 5e-3f * _rate;
                for (int b = 0; b < rendered; b++)
                    Ring(place, at + (int)(cavity * (0.2f + sum.Uniform())), DrawPlungeBubbleMm(sum), bw);
            }

            if (k % stride != 0 || sum.Uniform() > ChunkShare) continue;
            // A lump opens a crater too big to close in one: the bubble it traps is a large one, up to
            // the lump's own size — the low "glug" under a fountain. Smaller ones far more often.
            float u = sum.Uniform();
            float bubbleMm = r * 1e3f * (0.15f + 0.85f * u * u);
            Ring(place, at + (int)(0.006f * _rate * (0.5f + sum.Uniform())), bubbleMm, ringWeight * LumpBubblePart);
        }
    }

    /// <summary>A bubble of this radius ringing, made at a random depth (the u^β loudness skew), as
    /// every bubble in water here is: the fountain's, the rain's and running water's.</summary>
    internal static void Ring(EventSum sum, int at, float bubbleMm, float weight)
    {
        if (bubbleMm < SmallestBubbleMm || weight <= 0f) return;
        float hz = MinnaertHzMetres / (bubbleMm * 1e-3f);
        float depth = MathF.Pow(sum.Uniform(), DepthSkew);
        // A bubble made at the surface is 40 dB under one made deep, and there are a lot of them:
        // their share of the power is under a ten-thousandth, and rendering them was most of the cost.
        if (depth < 0.01f) return;
        sum.Bubble(at, hz, BubbleDamping(bubbleMm), BubblePascalsPerMm * bubbleMm * depth * weight, BubbleRise);
    }
}
