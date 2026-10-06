using System;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// Round 2 (docs/RUNNING_WATER.md section 10): water that leaves through a hole, and a tap over a basin.
///
/// THE INLET. Water leaving through a hole — a sink's waste, a roof gutter's outlet into its downpipe —
/// sounds by how deep it stands over the hole. Shallow, it spills over the rim as a weir round an open air
/// core (Q = Cw π D h^1.5) and slides down the pipe quietly. Deeper, the water closes over the hole and the
/// hole runs as an orifice (Q = Cd A √(2 g h)); a free-surface vortex forms over it and, while the water
/// is shallower than the vortex's critical submergence, its air core reaches the hole now and then and
/// air is drawn down in gulps: the gurgle. Deeper still the vortex cannot reach it and the hole runs full
/// and quiet. A gulp is a pocket of air a good fraction of the hole across pinched off at the surface: a
/// large bubble ringing at its Minnaert note with the steep climb of a bubble let go at the surface, a
/// rush of air through the closing gap, and a knock in the air of the pipe below, which rings at its own
/// modes. They come in a rhythm, the column in the hole and the air beneath it pushing each other.
///
/// THE BASIN. A sink's water rises and falls with what the tap puts in and what the inlet lets out (with
/// the plug in, only the overflow). The tap's jet strikes the bare bottom while the water over it is
/// thinner than the jet (the circular hydraulic jump of a tap into a sink: the jet still meets the steel),
/// and plunges into the water once it is deeper. A stainless bottom is a thin plate, struck by every lump
/// of the jet: it rings at its own modes, lowered and damped as water lies on it (its added mass), which is
/// the drumming of a tap into an empty sink going dull as the sink fills. A ceramic basin, thick and stiff,
/// barely rings at all.
///
/// When the tap is shut the basin empties through its inlet: the level falls, the vortex reaches the hole,
/// and the last of it goes in a gurgle and a slurp. That is what a sink sounds like with the plug out, and
/// nothing in it is a recording or a script: the gulps come and go with the level.
/// </summary>
public sealed partial class RunningWaterSynth
{
    // ── The inlet: fitted ─────────────────────────────────────────────────────────────────────────

    /// <summary>The volume of air the vortex draws down, per volume of water through the hole, at the
    /// height of the gurgle. FITTED (2026-10-06, docs/RUNNING_WATER.md 10.8) between seven recorded drains
    /// and bottles, which wanted 0.6 for their weight at 125-500 Hz, and four recorded downpipe and gutter
    /// gurgles, which at 0.6 stood 10 dB too heavy there in a downpour. A bottle lets in as much air as water
    /// leaves; an outlet's vortex less.</summary>
    public const float GulpAirShare = 0.4f;

    /// <summary>A gulp pocket's equivalent radius as a share of the hole's (open) radius, and how it
    /// varies (log). A pocket is pinched off by the hole's own rim, so it is about the hole's size: a
    /// bottle's glug lets in about 17 mL through a 20-40 mm neck, a sphere of 1.6 cm radius (Perez et al.
    /// 2026, from their flow and glug period), about the hole's own radius.</summary>
    private const float GulpPocketShare = 1.3f, GulpPocketSpread = 0.35f, LargestPocketMetres = 0.017f;

    /// <summary>The rhythm of the gulps: one every this many √(D / g), jittered. A bottle glugs about six
    /// times a second through a 20-40 mm hole, a little faster for a wider one (Perez, Monnet, Vidal and
    /// Joubaud 2026; Clanet and Searby 2004 for the gas spring and liquid mass behind it): 3.3 √(D/g) is
    /// 0.15-0.2 s there.</summary>
    private const float GulpPeriodScale = 3.3f;

    /// <summary>The share of a gulp's air that rings as one bubble; the rest breaks into a few smaller.</summary>
    private const float GulpMainShare = 0.6f;

    /// <summary>The climb of a gulp's note: a pocket let go at the surface (van den Doel 2005: ξ 0.5-1 for
    /// bubbles released from a nozzle).</summary>
    private const float GulpRise = 0.8f;

    /// <summary>The rush of air through the closing gap, its share of the pocket's bubble energy. FITTED.</summary>
    private const float SlurpShare = 0.5f;

    // ── The inlet: physical ───────────────────────────────────────────────────────────────────────

    /// <summary>Weir round a gutter outlet's rim, Q = Cw π D h^1.5: BS 6367's gutter-outlet form, Q = D
    /// h^1.5 / 7000 (L/s, mm), as back-calculated from HR Wallingford's measured outlets (Escarameia and May,
    /// SR463 1996), is Cw = 1.44 in SI.</summary>
    private const float WeirCoefficient = 1.44f;

    /// <summary>The orifice form of the same, Q = D² h^0.5 / 13200 (L/s, mm): Cd = 0.69. The two meet at
    /// h ≈ 0.53 D, where the outlet chokes and surges between them (SR463: "intermittent surging ...
    /// transition from weir to orifice flow and vice versa"; a drop shaft's transitional regime pulses
    /// between full and weir flow, Water 5:1380, 2013).</summary>
    private const float OrificeCd = 0.69f;

    /// <summary>The depth over the hole, as a share of its open diameter, at which the water first closes
    /// over the air core now and then and the gulps begin, and the depth by which it has closed over it.</summary>
    private const float GulpOnsetDepth = 0.08f, ClosureDepth = 0.35f;

    // ── The basin ─────────────────────────────────────────────────────────────────────────────────

    private float _level;                    // m of water over the basin's bottom
    private float _inletFlow;                // L/s leaving through the inlet
    private float _inletDepth;               // m of water over the inlet
    private float _gulpClock = -1f;
    private float _gulpAir;                  // m³ of air owed to the next gulp
    private Plate? _plate;
    private EventSum? _plateDrive;
    private float _plateLevel = -1f;

    /// <summary>The tap is open (more than its leak is asked for).</summary>
    private bool TapOn => Spec.Tap is { } tap && Flow > tap.LeakLitresPerSecond + 1e-5f;

    /// <summary>How deep the water stands in the basin now, m (for the lab and the tests).</summary>
    public float Level => _level;

    /// <summary>How deep the water stands over the inlet now, m.</summary>
    public float InletDepth => _inletDepth;

    /// <summary>What leaves through the inlet now, L/s.</summary>
    public float InletFlow => _inletFlow;

    /// <summary>The gurgle's share, for the lab. One in the game.</summary>
    public float GurglePart = 1f, PlatePart = 1f;

    private void InitBasin(float sampleRate, int seed)
    {
        if (Spec.Basin is { } basin && IsThinPlate(basin))
        {
            _plate = new Plate(basin, sampleRate);
            _plateDrive = new EventSum(sampleRate, seed * 52361 + 9);
        }
    }

    /// <summary>A basin whose bottom rings: a metal one thin enough to be a plate.</summary>
    private static bool IsThinPlate(FlowBasin basin) => basin.SkinMetres < 0.003f;

    /// <summary>The open area of the inlet, m², and its open diameter, m.</summary>
    private (float Area, float Diameter) InletOpening()
    {
        var inlet = Spec.Inlet!;
        float d = MathF.Max(0.005f, inlet.DiameterMetres);
        float area = MathF.PI * 0.25f * d * d * Math.Clamp(inlet.OpenShare, 0.05f, 1f);
        return (area, d * MathF.Sqrt(Math.Clamp(inlet.OpenShare, 0.05f, 1f)));
    }

    /// <summary>What the inlet passes at this depth over it, L/s: a weir round the rim while shallow, an
    /// orifice once the water closes over it, whichever is less.</summary>
    public static float InletCapacity(FlowInlet inlet, float depth)
    {
        if (depth <= 0f) return 0f;
        float share = Math.Clamp(inlet.OpenShare, 0.05f, 1f);
        float d = MathF.Max(0.005f, inlet.DiameterMetres);
        float rim = inlet.RimMetres > 0f ? inlet.RimMetres : MathF.PI * d * share;
        float weir = WeirCoefficient * rim * MathF.Pow(depth, 1.5f);
        float orifice = OrificeCd * MathF.PI * 0.25f * d * d * share * MathF.Sqrt(2f * Hydraulics.Gravity * depth);
        return 1000f * MathF.Min(weir, orifice);
    }

    /// <summary>The depth over the inlet that passes this flow, m (the inverse of InletCapacity).</summary>
    public static float InletDepthFor(FlowInlet inlet, float litresPerSecond)
    {
        if (litresPerSecond <= 0f) return 0f;
        float lo = 0f, hi = 2f;
        for (int i = 0; i < 40; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (InletCapacity(inlet, mid) < litresPerSecond) lo = mid; else hi = mid;
        }
        return 0.5f * (lo + hi);
    }

    /// <summary>
    /// How much of the gurgle the inlet makes at this depth, 0 to 1. Nothing while the water is too shallow
    /// to close over the hole (it spills in round an open air core); rising as it does; falling away toward
    /// the vortex's critical submergence, past which no air reaches the hole: Gordon's S = C v √D, C the
    /// inlet's <see cref="FlowInlet.Swirl"/>, v the speed through the hole, D its open diameter. A gutter's
    /// outlet, fed from one side, gurgles round the weir-orifice switch (about half its diameter deep, where
    /// HR Wallingford's outlets surged); a basin draining from rest, only as the last of it goes.
    /// </summary>
    public static float GurgleShare(FlowInlet inlet, float depth, float litresPerSecond)
    {
        float share = Math.Clamp(inlet.OpenShare, 0.05f, 1f);
        float d = MathF.Max(0.005f, inlet.DiameterMetres) * MathF.Sqrt(share);
        float x = depth / d;
        if (x <= GulpOnsetDepth || litresPerSecond <= 0f) return 0f;
        float v = litresPerSecond * 1e-3f / (MathF.PI * 0.25f * d * d);
        float critical = MathF.Max(2f * ClosureDepth, inlet.Swirl * v * MathF.Sqrt(d) / d);
        float rise = Math.Clamp((x - GulpOnsetDepth) / (ClosureDepth - GulpOnsetDepth), 0f, 1f);
        float fall = Math.Clamp((critical - x) / (0.4f * critical), 0f, 1f);
        return rise * rise * (3f - 2f * rise) * fall;
    }

    /// <summary>The basin's water and what leaves through the inlet, for this block.</summary>
    private void BasinControl(float dt)
    {
        if (Spec.Inlet is not { } inlet) return;
        if (Spec.Basin is { } basin)
        {
            float full = MathF.Max(0.01f, basin.LengthMetres * basin.WidthMetres);
            float sump = Math.Clamp(basin.SumpSquareMetres, 0.001f, full);
            // What the tap puts in is this source's flow; what leaves is the inlet's, at this depth.
            float inflow = _flow * 1e-3f;
            float level = _level;
            bool plugIn = basin.PlugIn || (basin.PlugWhileRunning && TapOn);
            // Sub-steps: a shallow basin drains in seconds, a block is 5 ms.
            for (int k = 0; k < 4; k++)
            {
                float h = level;
                // The first water stands in the dish over the waste; the bottom is covered once it is
                // deeper than the bottom falls.
                float cover = Math.Clamp(h / MathF.Max(0.001f, basin.FallToWasteMetres), 0f, 1f);
                float area = sump + (full - sump) * cover * cover;
                // With the plug in only the overflow slot lets water go: about 0.2 L/s a centimetre over it.
                float outflow = plugIn
                    ? (h > basin.OverflowMetres ? 0.0002f * (h - basin.OverflowMetres) / 0.01f : 0f)
                    : InletCapacity(inlet, h) * 1e-3f;
                level = MathF.Max(0f, level + (inflow - outflow) * (dt / 4f) / area);
                level = MathF.Min(level, basin.DepthMetres);
            }
            _level = level;
            _inletDepth = plugIn ? 0f : level;
            _inletFlow = plugIn ? 0f : InletCapacity(inlet, level);
        }
        else
        {
            // A gutter's outlet passes what the gutter brings, at the depth that takes.
            _inletFlow = _flow;
            _inletDepth = InletDepthFor(inlet, _flow);
        }
    }

    /// <summary>The flow into fall <paramref name="i"/> now, L/s, before its own share.</summary>
    private float FallFlow(int i)
    {
        var fall = Spec.Falls[i];
        switch (fall.Feed)
        {
            case FallFeed.TapOntoBasin: return _flow * BareShare();
            case FallFeed.TapIntoWater: return _flow * (1f - BareShare());
            case FallFeed.Drain:
            {
                // What spills over the rim into the pipe while the hole is a weir; once the water closes
                // over it the hole runs as an orifice and what goes down is the gulps' business.
                if (Spec.Inlet is not { } inlet) return 0f;
                // A trickle too small to leave the rim as a stream slides down the pipe's wall unheard.
                if (_inletFlow < JetOnsetLitresPerSecond) return 0f;
                float x = _inletDepth / MathF.Max(0.005f, inlet.DiameterMetres);
                return _inletFlow * Math.Clamp(1f - x / (2f * ClosureDepth), 0f, 1f);
            }
            default: return _flow;
        }
    }

    /// <summary>The share of a tap's jet that still strikes the basin's bottom: all of it while the water
    /// over the bottom is thinner than the jet, little once it is several jets deep.</summary>
    private float BareShare()
    {
        if (Spec.Basin == null || Spec.Tap is not { } tap) return 1f;
        float jet = JetDiameter(tap, _flow);
        return MathF.Exp(-_level / MathF.Max(0.002f, jet));
    }

    /// <summary>A tap's stream across where it lands, m: the nozzle's bore narrowing as the stream speeds
    /// up in its fall (continuity: d ∝ v^-1/2). An aerated stream is wider than the water in it.</summary>
    public static float JetDiameter(FlowTap tap, float litresPerSecond)
    {
        float d0 = MathF.Max(0.002f, tap.NozzleMm * 1e-3f / MathF.Sqrt(MathF.Max(1, tap.Jets)));
        float q = MathF.Max(1e-7f, litresPerSecond * 1e-3f / MathF.Max(1, tap.Jets));
        float v0 = q / (MathF.PI * 0.25f * d0 * d0);
        float v = MathF.Sqrt(v0 * v0 + 2f * Hydraulics.Gravity * MathF.Max(0f, tap.HeightMetres));
        return d0 * MathF.Sqrt(v0 / v) * (tap.Aerated ? 1.4f : 1f);
    }

    /// <summary>
    /// How a tap's stream arrives (FallFeed.Tap...): from the spout's height, less the water standing in
    /// the basin, already moving at the spout's own speed. A plain spout's stream is a glassy column that
    /// lands whole, in lumps the size of the stream (it has not fallen far enough to break up: a jet's
    /// breakup length is many diameters, Rayleigh-Plateau); an aerated stream is air and water mixed and
    /// lands as a soft spray of millimetre drops round a core; a shower rose's jets break into drops.
    /// </summary>
    public static FallShape TapShape(RunningWaterSpec spec, FlowTap tap, float litresPerSecond, float level)
    {
        int jets = Math.Max(1, tap.Jets);
        float d0 = MathF.Max(0.002f, tap.NozzleMm * 1e-3f / MathF.Sqrt(jets));
        float q = MathF.Max(1e-7f, litresPerSecond * 1e-3f / jets);
        float v0 = q / (MathF.PI * 0.25f * d0 * d0);
        float height = MathF.Max(0.01f, tap.HeightMetres - level) + v0 * v0 / (2f * Hydraulics.Gravity);
        float jetMm = 1e3f * JetDiameter(tap, litresPerSecond) / (tap.Aerated ? 1.4f : 1f);
        if (jets > 1)
            return new FallShape(litresPerSecond, height, 1f, Math.Clamp(0.95f * jetMm, 0.4f, 2f), 2f);
        if (tap.Aerated)
            return new FallShape(litresPerSecond, height, 0.6f, 0.9f, Math.Clamp(0.5f * jetMm, 1.5f, 6f));
        return new FallShape(litresPerSecond, height, 0.1f, 1.2f, Math.Clamp(0.95f * jetMm, 1.5f, 8f));
    }

    // ── The gurgle ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Air owed and gulps let go, once a block.</summary>
    private void Gulps(float dt)
    {
        if (Spec.Inlet is not { } inlet || GurglePart <= 0f) return;
        float g = GurgleShare(inlet, _inletDepth, _inletFlow);
        if (g <= 0f || _inletFlow <= 0f) { _gulpClock = -1f; _gulpAir = 0f; return; }
        float drawn = GulpAirShare * g * _inletFlow * 1e-3f * dt;
        _gulpAir += (1f - VortexSheddingShare) * drawn;
        var (_, d) = InletOpening();
        // Between the gulps the vortex's tip sheds small bubbles steadily (Andersen et al. 2003: bubbles
        // detach from the tip of the dip and are dragged down before the air core reaches through).
        Shed(VortexSheddingShare * drawn, d);
        float period = GulpPeriodScale * MathF.Sqrt(d / Hydraulics.Gravity);
        if (_gulpClock < 0f) _gulpClock = period * _rng.Uniform();
        _gulpClock -= dt;
        while (_gulpClock <= 0f)
        {
            int at = Math.Clamp((int)((dt + _gulpClock) * _rate), 0, Block - 1);
            // The column and the air under it push each other in a rhythm, not a clock: ±70 %.
            _gulpClock += period * (0.3f + 1.4f * _rng.Uniform());
            Gulp(at, d);
        }
    }

    /// <summary>The share of the drawn air that goes down as small bubbles shed steadily from the vortex's
    /// tip, rather than in the gulps. FITTED: with all of it in the gulps, the gurgle's band envelopes
    /// moved at the gulps' few hertz far more than any recorded drain's (modulation at 4-16 Hz 0.82
    /// against 0.20-0.47).</summary>
    private const float VortexSheddingShare = 0.6f;

    private const int MaxShedPerBlock = 16;

    /// <summary>This much air shed as small bubbles, a tenth to a third of the hole across, this block.</summary>
    private void Shed(float air, float holeDiameter)
    {
        if (air <= 0f) return;
        float lo = 0.05f * holeDiameter, hi = 0.18f * holeDiameter;
        float meanCube = (hi * hi * hi - lo * lo * lo) / (3f * MathF.Log(hi / lo));
        int real = _rng.Poisson(air / (4f / 3f * MathF.PI * meanCube));
        if (real <= 0) return;
        int n = Math.Min(real, MaxShedPerBlock);
        float w = MathF.Sqrt(real / (float)n) * GurglePart;
        for (int b = 0; b < n; b++)
        {
            float r = lo * MathF.Pow(hi / lo, _rng.Uniform());
            int k = _open.Length > 1 && _rng.Uniform() < _toHome ? 1 + (int)(_rng.Uniform() * (_open.Length - 1)) % (_open.Length - 1) : 0;
            FallingWaterSynth.Ring(_open[k], (int)(_rng.Uniform() * Block), r * 1e3f, w);
        }
    }

    /// <summary>One gulp: what air is owed goes down as one large pocket and a few smaller ones.</summary>
    private void Gulp(int at, float holeDiameter)
    {
        float air = _gulpAir;
        _gulpAir = 0f;
        if (air <= 0f) return;
        // The pocket the rim pinches off: about the hole's size; more air owed is more pockets, not a
        // bigger one.
        // No bigger than a bottle's glug, though: 17 mL through a 40 mm neck as through a 20 mm one (Perez et al.
        // 2026), a sphere of 1.6 cm. A wide outlet lets more pockets down, not bigger ones.
        float pocketR = MathF.Min(GulpPocketShare * 0.5f * holeDiameter, LargestPocketMetres) * MathF.Exp(GulpPocketSpread * Gauss(_rng));
        float pocketV = 4f / 3f * MathF.PI * pocketR * pocketR * pocketR;
        int pockets = Math.Clamp((int)MathF.Round(air / pocketV), 1, 4);
        float each = air / pockets;
        int k = _open.Length > 1 && _rng.Uniform() < _toHome ? 1 + (int)(_rng.Uniform() * (_open.Length - 1)) % (_open.Length - 1) : 0;
        var open = _open[k];
        for (int p = 0; p < pockets; p++)
        {
            int when = at + (int)(p * 0.012f * _rate * (0.5f + _rng.Uniform()));
            float main = MathF.Cbrt(3f * GulpMainShare * each / (4f * MathF.PI));
            float mm = main * 1e3f;
            // The pocket rings as it pinches off at the surface: the fountain's law for a crater's bubble
            // (it drives the surface over the hole like a piston), its note climbing.
            float hz = FallingWaterSynth.MinnaertHzMetres / main;
            if (hz < 0.45f * _rate)
                open.Bubble(when, hz, FallingWaterSynth.BubbleDamping(mm), FallingWaterSynth.BubblePascalsPerMm * mm * GurglePart, GulpRise);
            // The rest of its air breaks into a few smaller bubbles.
            for (int b = 0; b < 3; b++)
            {
                float small = mm * (0.25f + 0.25f * _rng.Uniform());
                float depth = MathF.Pow(_rng.Uniform(), FallingWaterSynth.DepthSkew);
                if (depth < 0.01f) continue;
                FallingWaterSynth.Ring(open, when + (int)(0.004f * _rate * _rng.Uniform()), small, depth * GurglePart);
            }
            // The air rushing through the closing gap: a short burst of flow noise as loud as a share of
            // the pocket's ringing, its band from the gap's own size (a few kHz for a centimetre gap).
            float ring = FallingWaterSynth.BubblePascalsPerMm * mm;
            float slurp = ring * MathF.Sqrt(SlurpShare) * 0.3f * GurglePart;
            float centre = Math.Clamp(343f / (2f * MathF.PI * MathF.Max(0.003f, 0.5f * holeDiameter)) * 0.5f, 400f, 4000f);
            open.Burst(when, 0.004f, 0.025f, slurp, centre / 2f, MathF.Min(centre * 2f, 0.45f * _rate));
            // And the pipe below takes the knock: its air rings at its own modes (the Tube).
            if (_inside != null)
                _inside[k].Pulse(when, 0.0015f, ring * 0.5f * GurglePart);
        }
    }

    // ── The aerated stream ────────────────────────────────────────────────────────────────────────

    /// <summary>The volume of air an aerator mixes into the stream, per volume of water. FITTED to the
    /// recordings of taps into sinks (section 10.8): without it the model's tap was 8-15 dB under them
    /// at 4-16 kHz. An aerator draws in about as much air as water (a judgement; no measurement found).</summary>
    private const float AeratorAirShare = 0.5f;

    /// <summary>The aerated stream's bubbles: radii from this to that, mm, evenly on a log scale. An
    /// aerator's mesh makes bubbles of a few tenths of a millimetre.</summary>
    private const float FizzSmallestMm = 0.12f, FizzLargestMm = 1.0f;

    /// <summary>The share of them that ring as they are let go at the surface and break up there: most
    /// rise and burst quietly. FITTED with the air share.</summary>
    private const float FizzRingShare = 0.25f;

    private const int MaxFizzPerBlock = 24;

    /// <summary>A drop's blow on the wet (not flooded) steel of a sink keeps its first contact
    /// (<see cref="WetCushion"/> is for stone under running water).</summary>
    private const float PlateCushion = 0.05f;

    /// <summary>
    /// An aerated tap's stream is white: air and water mixed through the aerator's mesh. Where it lands
    /// the air comes out as a cloud of small bubbles ringing high (a few tenths of a millimetre: 5-25 kHz),
    /// the hiss of a running tap that a glassy stream from a plain spout does not have.
    /// </summary>
    private void Fizz(float dt)
    {
        if (Spec.Tap is not { Aerated: true } || !(_flow >= JetOnsetLitresPerSecond) || FallPart <= 0f) return;
        float air = AeratorAirShare * FizzRingShare * _flow * 1e-3f;                // m³/s
        float lo = FizzSmallestMm * 1e-3f, hi = FizzLargestMm * 1e-3f;
        float meanCube = (hi * hi * hi - lo * lo * lo) / (3f * MathF.Log(hi / lo));
        float meanVolume = 4f / 3f * MathF.PI * meanCube;
        int real = _rng.Poisson(air / meanVolume * dt);
        if (real <= 0) return;
        int n = Math.Min(real, MaxFizzPerBlock);
        float w = MathF.Sqrt(real / (float)n) * FallPart;
        // Into the water if there is any, else onto the wet bottom where they burst in the film.
        for (int b = 0; b < n; b++)
        {
            float mm = FizzSmallestMm * MathF.Pow(FizzLargestMm / FizzSmallestMm, _rng.Uniform());
            int k = _open.Length > 1 && _rng.Uniform() < _toHome ? 1 + (int)(_rng.Uniform() * (_open.Length - 1)) % (_open.Length - 1) : 0;
            FallingWaterSynth.Ring(_open[k], (int)(_rng.Uniform() * Block), mm, w);
        }
    }

    // ── The plate ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Where a basin's jet events are written: onto the plate's drive while it rings.</summary>
    private EventSum? PlateDriveFor(int fall)
        => _plateDrive != null && fall >= 0 && fall < Spec.Falls.Length && Spec.Falls[fall].Feed == FallFeed.TapOntoBasin ? _plateDrive : null;

    /// <summary>The plate's sample: the blows themselves and the bottom ringing under them.</summary>
    private float PlateSample()
    {
        if (_plateDrive == null) return 0f;
        float x = _plateDrive.Next();
        return x + PlatePart * _plate!.Process(x);
    }

    private void PlateControl()
    {
        if (_plate == null) return;
        if (MathF.Abs(_level - _plateLevel) > 0.0005f || _plateLevel < 0f)
        {
            _plateLevel = _level;
            _plate.Load(_level);
        }
    }

    /// <summary>
    /// A basin's bottom as a thin plate: its lowest modes (simply supported, (π/2)√(B/m″)((m/a)² + (n/b)²)),
    /// each a two-pole resonator, struck at a point near the middle (the strainer is in the middle, the tap
    /// over it, a little off). How much of a blow goes into it is the plate's point mobility, 1/(8√(B m″)),
    /// and how much of its motion leaves as sound its radiation efficiency below coincidence
    /// (RainPlate.RadiationEfficiency, Maidanik's edge radiation). Water on it adds its mass (ρ_w h per
    /// square metre, no more than a mode's own half-wavelength's worth) and some damping: the notes drop
    /// and dull as the sink fills.
    /// </summary>
    private sealed class Plate
    {
        private const int MaxModes = 20;
        private readonly int _modes;
        private readonly float[] _hz0 = new float[MaxModes], _weight = new float[MaxModes];
        private readonly float[] _a1 = new float[MaxModes], _a2 = new float[MaxModes], _g = new float[MaxModes];
        private readonly float[] _y1 = new float[MaxModes], _y2 = new float[MaxModes];
        private readonly float _rate, _surfaceDensity, _loss, _coupling;
        private readonly RainPlate _shape;

        public Plate(FlowBasin basin, float rate)
        {
            _rate = rate;
            _shape = new RainPlate(basin.Material, basin.SkinMetres, basin.PanelMetres, 0.85f * basin.PanelMetres, basin.LossFactor);
            _surfaceDensity = _shape.SurfaceDensity;
            _loss = basin.LossFactor;
            // Struck a little off the middle, where the stream lands beside the strainer.
            float x0 = 0.42f, y0 = 0.38f;
            int count = 0;
            for (int m = 1; m <= 6 && count < MaxModes; m++)
                for (int n = 1; n <= 6 && count < MaxModes; n++)
                {
                    float hz = _shape.ModeHz(m, n);
                    if (hz > 0.4f * rate || hz > 8000f) continue;
                    float phi = MathF.Sin(m * MathF.PI * x0) * MathF.Sin(n * MathF.PI * y0);
                    _hz0[count] = hz;
                    _weight[count] = phi * phi * MathF.Sqrt(_shape.RadiationEfficiency(hz));
                    count++;
                }
            _modes = count;
            // The blow's share that goes into the plate (its mobility against that of 0.9 mm steel, the
            // sink the coupling was fitted on) times the fitted coupling.
            var reference = new RainPlate("Metal", 0.0009f, basin.PanelMetres, 0.85f * basin.PanelMetres, basin.LossFactor);
            _coupling = PlateCoupling * MathF.Sqrt(_shape.Mobility / reference.Mobility);
            Load(0f);
        }

        /// <summary>Re-tunes the modes for this much water on the plate.</summary>
        public void Load(float level)
        {
            for (int i = 0; i < _modes; i++)
            {
                float hz0 = _hz0[i];
                // A mode's half-wavelength in bending: the water further from the plate than that does not
                // move with it.
                float wave = MathF.Sqrt(MathF.Sqrt(_shape.Bending / _surfaceDensity) * MathF.PI / hz0);
                float added = 1000f * MathF.Min(level, 0.5f * wave);
                float hz = hz0 / MathF.Sqrt(1f + added / _surfaceDensity);
                float eta = _loss + _shape.Loss(hz) - _shape.StructuralLoss + WaterLoss * level / (level + 0.005f);
                float r = MathF.Exp(-MathF.PI * hz * eta / _rate);
                float w = MathF.Tau * hz / _rate;
                _a1[i] = 2f * r * MathF.Cos(w);
                _a2[i] = -r * r;
                // Unit-energy impulse response per mode, scaled by its weight; a damped mode rings less.
                _g[i] = _coupling * _weight[i] * (1f - r) * 4f;
            }
        }

        public float Process(float x)
        {
            float y = 0f;
            for (int i = 0; i < _modes; i++)
            {
                float v = _a1[i] * _y1[i] + _a2[i] * _y2[i] + _g[i] * x;
                _y2[i] = _y1[i];
                _y1[i] = v;
                y += v;
            }
            return y;
        }
    }

    /// <summary>How hard a stainless sink's bottom rings for a blow, against the blow's own sound. FITTED to
    /// recordings of a tap into a steel sink (section 10).</summary>
    private const float PlateCoupling = 3f;

    /// <summary>The loss water lying on a plate adds (its viscosity and the sound it radiates into the
    /// water), at a few millimetres and over.</summary>
    private const float WaterLoss = 0.03f;
}
