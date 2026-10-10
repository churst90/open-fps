using System.Collections.Concurrent;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// A fire of any size, from what is going on in it (docs/FIRE.md).
///
/// The flames radiate as a monopole, p = (γ − 1) / (4π r c²) dQ/dt (Strahle 1971; Hurle et al. 1968),
/// and a buoyant plume puffs at f ≈ 1.5 / √D (Cetegen and Ahmed 1993): the roar is a low noise swelling
/// with the puffs, lower and louder the bigger the body of fire. A fire wider than one body is many
/// cells side by side, each puffing in its own time and heard from the places near it
/// (ExtendedSources), so a long front is heard as long, its parts independent.
///
/// The fuel's water and resin burst its cells as crackles, on a power law of sizes, in clusters; the
/// big ones throw an ember. A crowd too dense to draw is a noise of the same power, and only its loud
/// tail is drawn one by one (as the surf's bubbles are, ShoreSynth). Under them is the fizz of gas and
/// steam through the char. Each <see cref="FireFuel"/> adds its own events: steam jets and settling
/// logs, torching and falling branches, windows, collapses, a car's struts and tyres. Falls go through
/// the game's impact law (ImpactAcoustics); glass is GlassFracture's, rendered off the audio threads.
///
/// The wind (WindField, read at each place) fans the flames, and a crown fire's heat release follows
/// it (Cruz et al. 2005). Lit at a known moment (<see cref="Age"/>) a fire grows as t², burns, dies
/// down and smoulders; a map's fire is always burning. Fitted: the roar's share of dQ/dt and its fall,
/// the smallest crackle, the fizz and each fuel's crackle rate (docs/FIRE.md section 8).
/// </summary>
public sealed class FireSynth
{
    /// <summary>
    /// How far a body of fire's heat release swings with its puffing, a share of it: its dQ/dt at a metre
    /// is (γ − 1) / (4π c²) 2π f_puff PuffSwing Q (docs/FIRE.md 1.2). The spectrum of that peaks at the
    /// puffing rate (infrasound), falls as f^−1.4 to ten times it (Marcillo et al. 2025) and as f^−α
    /// (<see cref="RoarTailExponent"/>) above, through everything that is heard. FITTED (section 8).
    /// </summary>
    public static float PuffSwing = 0.35f;

    /// <summary>α, how fast the roar's spectrum falls above ten times the puffing rate: PSD ∝ f^−α. Measured
    /// 2.1-3.4 on turbulent flames (Rajaram and Lieuwen 2009), 5/2 from Kolmogorov turbulence (Clavin and
    /// Siggia 1991), about 2.2-2.9 on vegetation fires (Viegas et al. 2008). FITTED (section 8).</summary>
    public static float RoarTailExponent = 2.5f;

    /// <summary>
    /// How far a body of fire's burning flickers between its puffs, rms over its mean: the heat release's
    /// own spectrum runs on from the puffing rate to ten times it (f^−1.4, Marcillo et al. 2025), and the
    /// roar and the gas let out of the fuel follow it. FITTED to the recordings' 4-16 Hz envelope
    /// modulation (section 8).
    /// </summary>
    public static float Flicker = 0.2f;

    /// <summary>Where the roar's spectrum turns from f^−1.4 to f^−α, over the puffing rate.</summary>
    private const float KneeOverPuff = 10f;
    /// <summary>Under this nothing is rendered, Hz: it is not heard, and a big fire's infrasound would
    /// otherwise be most of its level.</summary>
    public const float RoarFloorHz = 20f;

    /// <summary>The smallest crackle's peak, Pa at a metre. Sizes run up from it on a power law.</summary>
    public const float SmallestCracklePascals = 0.043f;

    /// <summary>
    /// The fizz under the crackles, rms Pa at a metre for 80 kW of seasoned wood burning well: the
    /// volatiles and steam a burning log lets out through the checks in its char all the time. FITTED
    /// 2026-10-06 (texture round 1) to the recorded fires' envelope statistics. Its power follows the
    /// heat release: the burning surface it leaks from.
    /// </summary>
    public const float FizzPascals = 0.0035f;

    /// <summary>The middle of the fizz's band, Hz: gas through cracks a fraction of a millimetre wide.</summary>
    private const float FizzHz = 5000f;

    /// <summary>The power law of crackle sizes: the chance a crackle is over a is a^−(α−1).</summary>
    private const float CrackleExponent = 2.2f;
    /// <summary>The largest crackle over the smallest.</summary>
    private const float CrackleRange = 150f;
    /// <summary>How many crackles a second seasoned wood makes per 100 kW burning well, before clustering.</summary>
    private const float CracklesPer100Kw = 28f;
    /// <summary>A crackle this many times the smallest throws an ember.</summary>
    private const float EmberSize = 60f;
    /// <summary>The most crackles a place draws one by one in a second; the rest are its crackle noise.</summary>
    public const float MaxDrawnCrackles = 240f;
    /// <summary>The most bodies of fire a synth keeps; a bigger area is cut into fewer, bigger ones.</summary>
    public const int MaxCells = 48;
    /// <summary>The most places, the middle included: the voice ids the client gives a source.</summary>
    public const int MaxPlaces = ExtendedSources.MaxPlaces;

    private const float Gamma = 1.4f, SoundSpeed = 343f;
    private const int Block = 128;
    private const int Sub = 32;

    public readonly FireSpec Spec;
    private readonly float _rate;
    private readonly EventSum _sum;
    private readonly EventSum[] _sums;
    private readonly Vector3[] _layout;
    private readonly float _cellDiameter;

    /// <summary>How many places the fire is heard from.</summary>
    public int Places => _sums.Length;
    /// <summary>How many bodies of fire it is.</summary>
    public int Cells => _cells.Length;

    /// <summary>How much of the fire its outer places carry, 0 to 1 (ExtendedSources): 0 hears it all
    /// from the middle, 1 each part from its own place.</summary>
    public float Spread;

    public bool Lit = true;

    /// <summary>Seconds since it was lit, or NaN for a fire that has always been burning.</summary>
    public double Age = double.NaN;

    /// <summary>Each part's share, for the lab to take the fire apart by muting. One in the game.</summary>
    public float RoarPart = 1f, CracklePart = 1f, SteamPart = 1f, SettlePart = 1f, TorchPart = 1f, FallPart = 1f, GlassPart = 1f, BurstPart = 1f;

    /// <summary>The wind at the flames, m/s, everywhere: what a fire of one place is read with.</summary>
    public float Wind
    {
        get => _windAt[0];
        set { for (int i = 0; i < _windAt.Length; i++) _windAt[i] = value; }
    }
    private readonly float[] _windAt;

    /// <summary>Reads the wind field at each place, the fire's middle at (x, z).</summary>
    public void ReadWind(float x, float z, double seconds)
    {
        for (int p = 0; p < _windAt.Length; p++)
            _windAt[p] = WindField.SpeedAt(x + _layout[p].X, Spec.FlameHeightMetres, z + _layout[p].Z, seconds);
    }

    // ── The bodies of fire ───────────────────────────────────────────────────────────────────────

    private struct Cell
    {
        public float X, Z;
        public float Share;          // of the whole heat release
        public float CatchAt;        // a share of the growth time at which it catches
        public float PuffHz, Phase, Jitter, Depth, Env;
        public float FlickHp, FlickX, FlickLo, FlickHi;   // the flicker's filter states
        public float Flick;                       // the burning now over its mean, from the flicker
        public float Fizz;                        // its fizz's power, Pa² at a metre, before the flicker
        public float Vigour, VigourTarget, VigourClock;
        public float Cluster, ClusterClock;
        public float Torch, TorchPeak, TorchAge, TorchLife;
        public float Q;              // its heat release now, kW
        public float Roar;           // its roar now, Pa rms at a metre, before the puff
        public float Wind;
        public int NearestPlace;
    }
    private readonly Cell[] _cells;
    /// <summary>Each cell's weight at each place, cell-major, every cell's summing to one.</summary>
    private readonly float[] _weights;
    /// <summary>The same for the roar: over the flames' places.</summary>
    private readonly float[] _roarWeights;
    /// <summary>How many of the places are the bed's; the rest are the flames'.</summary>
    private readonly int _bedPlaces;

    // Per place: the roar, the fizz and the crackle noise, and how loud each is now.
    private readonly PowerLawNoise[] _roar;
    private readonly Resonator[] _fizz;
    private readonly float[] _bedLp0, _bedLp, _bedHp;
    private readonly float[] _roarAmp, _roarStep;
    private readonly float[] _fizzAmp, _fizzTarget;
    private readonly float[] _bedAmp, _bedTarget;
    private readonly float[] _crackleRateAt;
    private readonly float[] _placeOut;
    private readonly float _roarUnit, _fizzNorm, _bedNorm, _bedA1, _bedA2;
    private readonly float _roarPsdPerKw2;   // the roar's PSD at 100 Hz per kW² of one cell, Pa²/Hz at a metre
    private readonly float _puffHz;
    /// <summary>How much bigger this fire's pockets are than the fire pit's, in pressure: a body of fire's
    /// crackle energy goes as what it burns, its count as that to the β (<see cref="CrackleHeatExponent"/>),
    /// so each is (Q / 80 kW)^((1 − β) / 2) louder. Bigger fuel holds its water in fewer, bigger pockets.</summary>
    private readonly float _pocketScale;
    private readonly float _flickA, _flickB, _flickC, _flickNorm;
    private readonly float _glide;

    private float _burn = 1f;        // how lit: 1 burning, 0 out
    private float _life = 1f;        // the life's share of the full heat release
    private float _flare;            // the whole fire's flare (a log settling, a room going, the tank)
    private float _windGrowth = 1f;  // a crown fire's heat release over its reference's, from the wind
    private int _untilBlock, _untilSub;
    private float _clock;
    private int _samples;

    // Crackle sizes: the energy of a crackle of each size, for the share of the law drawn as noise.
    private readonly float[] _energyLogSize;   // Pa²·s at a metre, at sizes 1..CrackleRange on a log grid
    private const int EnergyGrid = 24;

    // Steam jets (logs), at the middle.
    private struct Jet
    {
        public float Life, Age, Strength, Whistle, WhistleHz, DriftPhase, DriftHz;
        public Resonator Hiss, Tone;
        public bool Live;
    }
    private readonly Jet[] _jets = new Jet[3];

    // Embers in the air, landing later.
    private readonly float[] _emberAt = new float[24];
    private readonly float[] _emberSize = new float[24];
    private readonly int[] _emberPlace = new int[24];

    private float _settleClock;

    // Falls: what an impact of a piece of burning wood is, from the impact law, at sizes and heights.
    private readonly ImpactTable _fallOnGround, _fallOnWood;
    private float _fallClock;
    private float _collapseClock;
    private int _collapses;
    private bool _primed;

    // Strikes still to come: a branch takes a second or two to come down, longer than the event rings hold.
    private struct Pending { public float In; public int Place; public ImpactTable.Entry Hit; public float Scale; public bool Live; }
    private readonly Pending[] _pending = new Pending[48];

    // Glass and bursts.
    private readonly float[] _paneCrackAt, _paneFallAt;
    private readonly int[] _paneCell;
    private readonly float[] _paneModeHz = new float[6];
    private readonly float[] _paneModeDecay = new float[6];
    private readonly float[] _paneModeAmp = new float[6];
    private readonly float[] _burstAt;
    private readonly float[] _burstPascals;
    private readonly float[] _burstSeconds;
    private readonly bool[] _burstHiss;
    private float _tankAt = float.NaN;
    private readonly Task<float[]?>? _paneFallClip, _diceClip;

    private struct Clip { public float[]? Data; public int Pos; public int Place; public float Gain; public int Delay; }
    private readonly Clip[] _clips = new Clip[6];

    public FireSynth(FireSpec spec, float sampleRate, int seed, int places = 1)
    {
        Spec = spec;
        _rate = sampleRate;
        _sum = new EventSum(sampleRate, seed);
        places = Math.Clamp(places, 1, MaxPlaces);
        var layout = Layout(spec);
        _layout = new Vector3[places];
        for (int p = 0; p < places; p++) _layout[p] = p < layout.Length ? layout[p] : Vector3.Zero;
        _sums = new EventSum[places];
        _sums[0] = _sum;
        for (int k = 1; k < places; k++) _sums[k] = new EventSum(sampleRate, seed * 7919 + k * 104729 + 1);
        _windAt = new float[places];

        // The bodies of fire over the area: a grid over its bounds, jittered, those inside its shape kept.
        var (nx, nz, d) = CellGrid(spec);
        _cellDiameter = d;
        var outline = spec.Outline;
        var grid = new Cell[nx * nz];
        _puffHz = 1.5f / MathF.Sqrt(MathF.Max(0.1f, d));
        float w = spec.AreaWidth, dep = spec.AreaDepth;
        int kept = 0;
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                var c = new Cell();
                float jx = nx > 1 ? 0.2f * _sum.Signed() : 0f, jz = nz > 1 ? 0.2f * _sum.Signed() : 0f;
                c.X = -0.5f * w + w * (i + 0.5f + jx) / nx;
                c.Z = -0.5f * dep + dep * (j + 0.5f + jz) / nz;
                c.Share = nx * nz > 1 ? 0.7f + 0.6f * _sum.Uniform() : 1f;
                c.PuffHz = _puffHz * (1f + 0.08f * _sum.Signed());
                c.Phase = _sum.Uniform();
                c.Depth = 0.2f;
                c.Vigour = c.VigourTarget = 1f;
                c.Cluster = 1f;
                c.ClusterClock = 0f;
                // Catching: from one end of the area to the other, as fire spreads; a crown fire's front is
                // already running, and catches across its depth.
                float r = MathF.Sqrt(MathF.Pow((c.X + 0.5f * w) / MathF.Max(1f, w), 2f) + MathF.Pow((c.Z + 0.5f * dep) / MathF.Max(1f, dep), 2f)) / MathF.Sqrt(2f);
                c.CatchAt = nx * nz > 1 ? 0.75f * Math.Clamp(r + 0.1f * _sum.Signed(), 0f, 1f) : 0f;
                // A body whose middle is outside the shape (a round pit's corners, a bent hedge's inside) is not there.
                if (outline.Contains(c.X, c.Z)) grid[kept++] = c;
            }
        if (kept == 0) { grid[0].X = 0f; grid[0].Z = 0f; kept = 1; }
        _cells = grid.AsSpan(0, kept).ToArray();
        if (_cells.Length == 1) { _cells[0].Share = 1f; _cells[0].CatchAt = 0f; }
        float shareSum = 0f;
        for (int i = 0; i < _cells.Length; i++) shareSum += _cells[i].Share;
        for (int i = 0; i < _cells.Length; i++) _cells[i].Share /= shareSum;

        // The places: the bed's (the crackle, the fizz, every event) and, above them, the flames' (the roar).
        _bedPlaces = Math.Min(places, BedPlaces(spec));
        // Each cell's weight at each place: near ones by distance, wide enough that a cell between two
        // places is heard from both, and a single body (a fire pit) from all of its places at once. The bed's
        // weights are over the bed's places, the roar's over the flames' (or the bed's, with none above).
        _weights = new float[_cells.Length * places];
        _roarWeights = new float[_cells.Length * places];
        Weigh(_weights, 0, _bedPlaces, places, d, nearest: true);
        if (places > _bedPlaces) Weigh(_roarWeights, _bedPlaces, places, places, d, nearest: false);
        else Array.Copy(_weights, _roarWeights, _weights.Length);

        // The roar: the turbulent tail of dQ/dt, heard from 20 Hz up.
        _roar = new PowerLawNoise[places];
        _fizz = new Resonator[places];
        for (int p = 0; p < places; p++)
        {
            _roar[p].Tune(RoarTailExponent, RoarFloorHz, sampleRate);
            _fizz[p].Tune(MathF.Min(FizzHz, 0.4f * sampleRate), 0.5f, sampleRate);
        }
        _roarUnit = _roar[0].UnitAtReference;
        _roarPsdPerKw2 = RoarPsdPerKw2(_puffHz);
        _fizzNorm = 1f / Resonator.NoiseGain(MathF.Min(FizzHz, 0.4f * sampleRate), 0.5f, sampleRate);
        // The crackle noise: a crackle's own band (EventSum.Burst's, 1.5-12 kHz on one-pole edges).
        _bedA1 = MathF.Exp(-MathF.Tau * MathF.Min(12000f, 0.45f * sampleRate) / sampleRate);
        _bedA2 = MathF.Exp(-MathF.Tau * 1500f / sampleRate);
        {
            float l0 = 0f, l1 = 0f, h = 0f;
            var rng = new Random(2);
            double e = 0;
            for (int i = 0; i < 48000; i++)
            {
                float x = ((float)rng.NextDouble() * 2f - 1f) * 1.7320508f;
                l0 = (1f - _bedA1) * x + _bedA1 * l0;
                l1 = (1f - _bedA1) * l0 + _bedA1 * l1;
                h = (1f - _bedA2) * l1 + _bedA2 * h;
                e += (l1 - h) * (l1 - h);
            }
            _bedNorm = 1f / MathF.Sqrt((float)(e / 48000));
        }
        _bedLp0 = new float[places]; _bedLp = new float[places]; _bedHp = new float[places];
        _roarAmp = new float[places]; _roarStep = new float[places];
        _fizzAmp = new float[places]; _fizzTarget = new float[places];
        _bedAmp = new float[places]; _bedTarget = new float[places];
        _crackleRateAt = new float[places];
        _placeOut = new float[places];

        _energyLogSize = CrackleEnergies(sampleRate);
        _pocketScale = MathF.Pow(MathF.Max(1f, spec.HeatReleaseKw / _cells.Length / 80f), 0.5f * (1f - CrackleHeatExponent));
        // The flicker: noise above the puffing rate, flat to three times it and falling as f^−2 from there
        // to ten times it (about the f^−1.4 measured over that decade), sampled every Sub samples.
        {
            float step = Sub / sampleRate;
            _flickA = MathF.Exp(-MathF.Tau * _puffHz * step);
            _flickB = MathF.Exp(-MathF.Tau * 3f * _puffHz * step);
            _flickC = MathF.Exp(-MathF.Tau * MathF.Min(KneeOverPuff * _puffHz, 0.2f / step) * step);
            float hp = 0f, lo = 0f, hi = 0f, xPrev = 0f;
            var rng = new Random(5);
            double e = 0;
            int n = 200000;
            for (int i = 0; i < n; i++)
            {
                float x = (float)rng.NextDouble() * 2f - 1f;
                hp = _flickA * (hp + x - xPrev); xPrev = x;
                lo = (1f - _flickB) * hp + _flickB * lo;
                hi = (1f - _flickC) * lo + _flickC * hi;
                e += hi * hi;
            }
            _flickNorm = 1f / MathF.Sqrt((float)(e / n));
        }
        _glide = 1f - MathF.Exp(-1f / (0.005f * sampleRate));

        _settleClock = 30f + 60f * _sum.Uniform();
        _fallClock = 5f + 10f * _sum.Uniform();
        _collapseClock = float.NaN;
        for (int i = 0; i < _emberAt.Length; i++) _emberAt[i] = -1f;

        // Falls strike the ground round the fire, or another branch on the way down.
        var wood = AcousticRegistry.GetProperties("Wood");
        _fallOnGround = new ImpactTable(wood, AcousticRegistry.GetProperties(spec.Surround));
        _fallOnWood = new ImpactTable(wood, wood);

        // Glass: each pane cracks when its part of the fire is going, and in a building falls out later.
        int panes = Math.Max(0, spec.Panes);
        _paneCrackAt = new float[panes];
        _paneFallAt = new float[panes];
        _paneCell = new int[panes];
        for (int i = 0; i < panes; i++)
        {
            _paneCell[i] = (int)(_sum.Uniform() * _cells.Length) % Math.Max(1, _cells.Length);
            // Seconds after its cell is fully going: a pane cracks in the first minutes of full heat
            // (docs/FIRE.md 6.2), and falls out minutes after cracking.
            _paneCrackAt[i] = 30f + 240f * _sum.Uniform();
            _paneFallAt[i] = spec.Fuel == FireFuel.Structure ? 60f + 300f * _sum.Uniform() : float.NaN;
        }
        PaneModes(spec);
        int bursts = Math.Max(0, spec.Struts) + Math.Max(0, spec.Tyres);
        _burstAt = new float[bursts];
        _burstPascals = new float[bursts];
        _burstSeconds = new float[bursts];
        _burstHiss = new bool[bursts];
        for (int i = 0; i < bursts; i++)
        {
            bool strut = i < spec.Struts;
            // A strut vents in the first quarter hour of a car fire; a tyre bursts, if it does, later.
            _burstAt[i] = strut ? 120f + 900f * _sum.Uniform() : (_sum.Uniform() < 0.35f ? 300f + 1200f * _sum.Uniform() : float.NaN);
            var (pa, sec) = strut ? Blast(150e5f, 6e-5f) : Blast(3.3e5f, 0.025f);
            _burstPascals[i] = pa;
            _burstSeconds[i] = sec;
            _burstHiss[i] = strut;
        }
        if (spec.Fuel == FireFuel.Vehicle) _tankAt = 400f + 600f * _sum.Uniform();
        if (spec.Fuel == FireFuel.Structure) _collapseClock = 600f + 900f * _sum.Uniform();

        if (spec.Panes > 0 && spec.Fuel == FireFuel.Structure)
            _paneFallClip = GlassClips.Get(new GlassFracture.Spec(GlassFracture.Part.Land, GlassType.Annealed, spec.PaneWidthMetres, spec.PaneHeightMetres,
                                                                   spec.PaneThicknessMm * 1e-3f, 0.004f, 25f, 1, spec.PaneDropMetres, spec.Surround, seed & 3), (int)sampleRate);
        if (spec.Panes > 0 && spec.Fuel == FireFuel.Vehicle)
            _diceClip = GlassClips.Get(new GlassFracture.Spec(GlassFracture.Part.Break, GlassType.Tempered, spec.PaneWidthMetres, spec.PaneHeightMetres,
                                                               spec.PaneThicknessMm * 1e-3f, 0.004f, 25f, 1, spec.PaneDropMetres, spec.Surround, seed & 3), (int)sampleRate);
    }

    /// <summary>Normalised Gaussian weights of every cell over places [from, to), into a cell-major table.</summary>
    private void Weigh(float[] table, int from, int to, int places, float d, bool nearest)
    {
        float spacing = float.MaxValue;
        for (int a = from; a < to; a++)
            for (int b = a + 1; b < to; b++)
                spacing = MathF.Min(spacing, Vector2.Distance(new(_layout[a].X, _layout[a].Z), new(_layout[b].X, _layout[b].Z)));
        float sigma = MathF.Max(0.5f * (spacing == float.MaxValue ? 1f : spacing), 0.5f * d);
        for (int c = 0; c < _cells.Length; c++)
        {
            float sum = 0f, best = float.MaxValue;
            for (int p = from; p < to; p++)
            {
                float dx = _cells[c].X - _layout[p].X, dz = _cells[c].Z - _layout[p].Z;
                float dd = dx * dx + dz * dz;
                // One body of fire (a hearth, a pile) burns all over its bed, so it is heard from every place
                // alike; a fire of many bodies, each from the places near it.
                float wgt = _cells.Length == 1 ? 1f : MathF.Exp(-0.5f * dd / (sigma * sigma));
                table[c * places + p] = wgt;
                sum += wgt;
                if (nearest && dd < best) { best = dd; _cells[c].NearestPlace = p; }
            }
            for (int p = from; p < to; p++) table[c * places + p] /= MathF.Max(1e-9f, sum);
        }
    }

    /// <summary>How many places above the bed carry the roar (docs/FIRE.md 12.2), when the source has room.</summary>
    public const int FlamePlaces = 3;

    /// <summary>The bed's places of a fire heard from its spec's places.</summary>
    public static int BedPlaces(FireSpec spec) => Math.Clamp(spec.Places, 1, MaxPlaces);

    /// <summary>
    /// The roar's PSD at 100 Hz per kW² of one body of fire puffing at <paramref name="puffHz"/>, Pa²/Hz at a
    /// metre: its dQ/dt's variance (the monopole's, at the puffing rate) spread over the puffing peak, down
    /// f^−1.4 to the knee and f^−α from there.
    /// </summary>
    public static float RoarPsdPerKw2(float puffHz)
    {
        float perKw = (Gamma - 1f) / (4f * MathF.PI * SoundSpeed * SoundSpeed) * MathF.Tau * puffHz * PuffSwing * 1000f;
        float peak = perKw * perKw / puffHz;
        float knee = KneeOverPuff * puffHz;
        float at = PowerLawNoise.ReferenceHz;
        float shape = at <= knee ? MathF.Pow(at / puffHz, -1.4f) : MathF.Pow(KneeOverPuff, -1.4f) * MathF.Pow(at / knee, -RoarTailExponent);
        return peak * shape;
    }

    /// <summary>The bodies of fire an area is cut into: how many across, how many along, and their width.</summary>
    public static (int Across, int Along, float Diameter) CellGrid(FireSpec spec)
    {
        float d = MathF.Max(0.1f, spec.BaseDiameterMetres);
        float w = spec.AreaWidth, dep = spec.AreaDepth;
        while (true)
        {
            int nx = Math.Max(1, (int)MathF.Round(w / d)), nz = Math.Max(1, (int)MathF.Round(dep / d));
            if (nx * nz <= MaxCells) return (nx, nz, d);
            d *= 1.1f;
        }
    }

    /// <summary>
    /// Where a fire's places are, m from its middle (x across, y up, z along), place 0 the middle. First
    /// the bed's (<see cref="FireSpec.Places"/>): along a long front, every place a stretch of it; round
    /// anything else, an ellipse that spreads as its shape does. Then, if there is room, the flames'
    /// (<see cref="FlamePlaces"/>), <see cref="FireSpec.RoarRiseMetres"/> above, spread the same way:
    /// the crackle comes from the fuel, the roar from the flames over it (docs/FIRE.md 12.2).
    /// </summary>
    public static Vector3[] Layout(FireSpec spec)
    {
        int n = BedPlaces(spec);
        int flames = n > 1 ? Math.Min(FlamePlaces, MaxPlaces - n) : 0;
        var places = new Vector3[n + flames];
        int outer = n - 1;
        if (outer <= 0) return places;
        // The shape's spread: a uniform w x d rectangle has variances w²/12 and d²/12, so its "width" each
        // way is √(12 var). A shape whose spread is not along x and z (an outline) is laid along its own
        // principal axes and turned back.
        var (vx, vz, cxz) = spec.Outline.Moments();
        float turn = 0f;
        if (MathF.Abs(cxz) > 1e-4f * (vx + vz))
        {
            turn = 0.5f * MathF.Atan2(2f * cxz, vx - vz);
            float mid = 0.5f * (vx + vz), half = MathF.Sqrt(0.25f * (vx - vz) * (vx - vz) + cxz * cxz);
            vx = mid + half;
            vz = mid - half;
        }
        float w = MathF.Sqrt(12f * MathF.Max(0f, vx)), dep = MathF.Sqrt(12f * MathF.Max(0f, vz));
        float rise = spec.RoarRiseMetres;
        if (w >= 2.5f * dep)
        {
            // A front: the middle and its places in pairs out to either side, evenly along it.
            float step = w / (outer + 1);
            for (int j = 0; j < outer; j++)
            {
                int k = j / 2 + 1;
                places[1 + j] = new Vector3((j % 2 == 0 ? -1f : 1f) * k * step, 0f, 0f);
            }
            // The flames over it: the middle and a pair out at ±w/√8, which spread as the front does.
            if (flames > 0) places[n] = new Vector3(0f, rise, 0f);
            for (int j = 1; j < flames; j++)
                places[n + j] = new Vector3((j % 2 == 1 ? -1f : 1f) * ((j + 1) / 2) * w / MathF.Sqrt(8f), rise, 0f);
        }
        else
        {
            // An ellipse whose places spread as the area does: a uniform w x d area has variance w²/12 and
            // d²/12, and the middle and m places on half-axes k w, k d have m k² w² / 2(m + 1), so
            // k = √((m + 1) / 6m), 0.87-0.94 of the half-widths. At 0.75 the ears were 0.1-0.2 more alike
            // at 1-4 kHz than the area makes them (docs/FIRE.md 7.2).
            float spreadK = MathF.Sqrt((outer + 1f) / (6f * outer));
            for (int j = 0; j < outer; j++)
            {
                float a = MathF.Tau * (j + 0.25f) / outer;
                places[1 + j] = new Vector3(MathF.Cos(a) * spreadK * w, 0f, MathF.Sin(a) * spreadK * dep);
            }
            // The flames': m places on a ring with no middle spread as the area at k = 1/√6, set between the
            // bed's in angle.
            float flameK = 1f / MathF.Sqrt(6f);
            for (int j = 0; j < flames; j++)
            {
                float a = MathF.Tau * (j + 0.75f) / flames;
                places[n + j] = new Vector3(MathF.Cos(a) * flameK * w, rise, MathF.Sin(a) * flameK * dep);
            }
        }
        if (turn != 0f)
        {
            float c = MathF.Cos(turn), sn = MathF.Sin(turn);
            for (int i = 0; i < places.Length; i++)
                places[i] = new Vector3(c * places[i].X - sn * places[i].Z, places[i].Y, sn * places[i].X + c * places[i].Z);
        }
        return places;
    }

    /// <summary>Crackles a second at this moment for the whole fire, before clustering, for the lab and tests.</summary>
    public float CrackleRate => _crackleNow >= 0f ? _crackleNow
        : _cells.Length * CracklesPer100Kw * 0.8f * MathF.Pow(Spec.HeatReleaseKw / _cells.Length / 80f, CrackleHeatExponent) * FuelCrackle * _life * _burn;
    private float _crackleNow = -1f;

    /// <summary>How a fuel's moisture and resin, and what it is, set its crackles per kW (docs/FIRE.md 4).</summary>
    private float FuelCrackle => (0.4f + 3f * MathF.Min(0.4f, Spec.Moisture)) * (0.6f + 0.8f * Spec.Resin) * Spec.Fuel switch
    {
        FireFuel.Trees => TreeCrackle,
        FireFuel.Crown => CrownCrackle,
        FireFuel.Structure => StructureCrackle,
        FireFuel.Vehicle => VehicleCrackle,
        FireFuel.Litter => LitterCrackle,
        _ => 1f,
    };

    /// <summary>Crackles per kW of burning foliage and twigs, over seasoned logs'. FITTED (section 8).</summary>
    public static float TreeCrackle = 0.12f;
    /// <summary>The same for a crown fire's front. FITTED (section 8).</summary>
    public static float CrownCrackle = 0.03f;
    /// <summary>
    /// How a body of fire's crackles grow with its heat release: as (Q / 80 kW)^β, which leaves the fire
    /// pit as fitted. FITTED (section 8): heard crackles come from the fuel's outer layer, a smaller
    /// share of what burns in a big pile or a crown.
    /// </summary>
    public static float CrackleHeatExponent = 0.5f;
    /// <summary>Crackles per kW of a burning building's timber, over seasoned logs'. FITTED (section 8).</summary>
    public static float StructureCrackle = 2f;
    /// <summary>Crackles (spitting plastics and the car's own timber-free fuel) per kW, over logs'. FITTED.</summary>
    public static float VehicleCrackle = 0.3f;
    /// <summary>A car's fizz over wood's, in power: molten plastics boiling and their gas jetting, where wood
    /// has its water. FITTED (section 8).</summary>
    public static float VehicleFizz = 10f;
    /// <summary>Crackles per kW of burning grass and litter, over seasoned logs': straw and litter fires carry
    /// almost no crackle, shrubs a strong 1-10 kHz one (Viegas et al. 2008). ESTIMATE, not yet fitted to
    /// the prescribed burns (docs/FIRE.md 12.9).</summary>
    public static float LitterCrackle = 0.35f;
    /// <summary>Drops bursting on hot fuel, pops a second per 100 kW the water is taking. ESTIMATE.</summary>
    public static float SizzlePopsPer100Kw = 25f;

    /// <summary>
    /// After it is out, the char cools and checks: ticks, at first a few a second for the coals of
    /// 100 kW, falling away over a minute or two. ESTIMATE (docs/FIRE.md 12.7). Drawn as crackles of the
    /// smallest sizes at the bed.
    /// </summary>
    private void Cooling(float dt)
    {
        float burning = Spec.HeatReleaseKw * _life;
        if (Lit && _burn > 0.5f) { _hotQ = burning; _cooling = 0f; return; }
        if (_hotQ <= 0f) return;
        _cooling += dt;
        float rate = 3f * MathF.Sqrt(_hotQ / 100f) * MathF.Exp(-_cooling / 60f) * CracklePart;
        if (rate < 0.01f) { _hotQ = 0f; return; }
        _tickClock -= dt * rate;
        Span<float> hz = stackalloc float[2];
        Span<float> tau = stackalloc float[] { 0.002f, 0.001f };
        Span<float> amp = stackalloc float[2];
        while (_tickClock <= 0f)
        {
            _tickClock += -MathF.Log(MathF.Max(1e-6f, _sum.Uniform()));
            var at = _sums[PlaceOf(PickCell())];
            int when = (int)(_sum.Uniform() * Block);
            float p = SmallestCracklePascals * (0.3f + 0.7f * _sum.Uniform());
            hz[0] = 4000f + 3000f * _sum.Uniform();
            hz[1] = 9000f + 2000f * _sum.Signed();
            amp[0] = 0.5f * p;
            amp[1] = 0.3f * p;
            at.Pulse(when, 30e-6f, p);
            at.Ring(when, hz, tau, amp);
        }
    }

    /// <summary>The share of the full heat release a fire lit <paramref name="age"/> seconds ago burns at:
    /// t² growth, full, linear decay, then a smoulder.</summary>
    public static float LifeShare(FireSpec spec, double age) => FireSpec.LifeShare(spec, age);

    /// <summary>
    /// How much of its heat release water is taking from it now, 0 to 1 (the server's
    /// FireSpread: rain or a hose cooling the fuel below where it gives off gas). Its flames lose that
    /// share, and the water flashing to steam on the hot fuel sizzles. Out (<see cref="Lit"/> false) the
    /// flames die over twenty seconds and the char ticks as it cools.
    /// </summary>
    public float Quench;
    private float _quench;
    private float _sizzleTarget, _sizzleAmp;
    private float _cooling;      // seconds since it went out, while its char is still hot
    private float _hotQ;         // the heat release it had when it went out, kW: how much char is cooling
    private float _tickClock;

    /// <summary>The steam's power over the fizz's for the same heat taken by water: water flashing on hot
    /// char is a louder gas jet than the char's own volatiles. ESTIMATE (docs/FIRE.md 12.7).</summary>
    public static float SizzleOverFizz = 4f;

    public void Control(float dt)
    {
        _clock += dt;
        _burn += Math.Clamp((Lit ? 1f : 0f) - _burn, -dt / 20f, dt / 5f);
        // Water takes the flames' heat over the fuel surface's own time, seconds, not at once.
        _quench += (Math.Clamp(Quench, 0f, 1f) - _quench) * MathF.Min(1f, dt / 4f);
        _life = LifeShare(Spec, Age);
        Cooling(dt);
        _flare *= MathF.Exp(-dt / 8f);
        float growth = double.IsNaN(Age) ? 1f : (float)Math.Clamp(Age / MathF.Max(1f, Spec.GrowthSeconds), 0, 1);
        int places = _sums.Length;

        // A crown fire's heat release follows the wind: its front's rate of spread, about U10^0.9 (Cruz,
        // Alexander and Wakimoto 2005), against the 10 m/s its heat release is declared at.
        if (Spec.Fuel == FireFuel.Crown)
        {
            float u = 0f;
            for (int p = 0; p < places; p++) u += _windAt[p];
            u = MathF.Max(2f, u / places / MathF.Max(0.3f, WindField.HeightShare(Spec.FlameHeightMetres)));
            float target = MathF.Pow(u / 10f, 0.9f);
            _windGrowth += (target - _windGrowth) * MathF.Min(1f, dt / 20f);
        }

        // The places' share of what is not theirs: merged, everything is heard from the middle.
        float s = Math.Clamp(Spread, 0f, 1f);
        for (int p = 0; p < places; p++) _crackleRateAt[p] = 0f;

        float total = 0f;
        float rateTotal = 0f;
        for (int c = 0; c < _cells.Length; c++)
        {
            ref var cell = ref _cells[c];
            cell.Wind = _windAt[cell.NearestPlace];
            // A body of fire breathes on its own: a log catching, another burning down. Tens of seconds.
            cell.VigourClock -= dt;
            if (cell.VigourClock <= 0f)
            {
                cell.VigourTarget = 0.75f + 0.5f * _sum.Uniform();
                cell.VigourClock = 8f + 20f * _sum.Uniform();
            }
            cell.Vigour += (cell.VigourTarget - cell.Vigour) * MathF.Min(1f, dt / 6f);
            // Crackles cluster: fuel reaching temperature fires off a run of them, then goes quiet.
            cell.ClusterClock -= dt;
            if (cell.ClusterClock <= 0f)
            {
                bool busy = cell.Cluster < 1f;
                cell.Cluster = busy ? 2.2f + 1.5f * _sum.Uniform() : 0.25f + 0.3f * _sum.Uniform();
                cell.ClusterClock = busy ? 0.6f + 2f * _sum.Uniform() : 1.5f + 4f * _sum.Uniform();
            }
            // Torching: a tree's foliage catching all at once (docs/FIRE.md 5.2), in turn across a stand.
            if (Spec.Fuel is FireFuel.Trees or FireFuel.Crown && Spec.TorchesInTurn)
            {
                if (cell.TorchLife > 0f)
                {
                    cell.TorchAge += dt;
                    float x = cell.TorchAge / cell.TorchLife;
                    // Up in a few seconds, down over the rest.
                    float rise = MathF.Min(1f, cell.TorchAge / 3f);
                    cell.Torch = cell.TorchPeak * rise * MathF.Max(0f, 1f - x) * TorchPart;
                    if (x >= 1f) { cell.TorchLife = 0f; cell.Torch = 0f; }
                }
                else if (_sum.Uniform() < TorchRate * dt * _life * _burn)
                {
                    cell.TorchAge = 0f;
                    cell.TorchLife = 10f + 30f * _sum.Uniform();
                    Torches++;
                    // A tree's foliage going up: four to twelve times its steady share; a crown fire's front
                    // surging as it crowns: one and a half to four times (docs/FIRE.md 5.2).
                    cell.TorchPeak = Spec.Fuel == FireFuel.Trees ? 3f + 8f * _sum.Uniform() : 0.5f + 2.5f * _sum.Uniform();
                }
            }
            // Catching, in its turn, while the fire grows.
            float caught = 1f;
            if (growth < 1f && _cells.Length > 1)
                caught = Math.Clamp((growth - cell.CatchAt) / MathF.Max(0.05f, 1f - cell.CatchAt), 0f, 1f);
            float life = _cells.Length > 1 && growth < 1f ? caught * caught : _life;
            // What is burning: its share, how far into its life, how lit, a tree torching, the crown's wind.
            float heat = Spec.HeatReleaseKw * cell.Share * life * _burn * (1f + cell.Torch) * (Spec.Fuel == FireFuel.Crown ? _windGrowth : 1f);
            // Water on it takes its share of the heat: what is left burns; what was taken is steam.
            float taken = heat * _quench;
            heat -= taken;
            cell.Q = heat * cell.Vigour * (1f + _flare);
            total += cell.Q;

            // The flames: dQ/dt at the puffing rate, fanned by a gust (the slow wander and a flare in power).
            float gust = Spec.Fuel == FireFuel.Crown ? 1f : 1f + 0.35f * MathF.Max(0f, cell.Wind - 1f) * GustReach;
            // Its roar as the square root of its PSD at 100 Hz, Pa/√Hz at a metre.
            cell.Roar = heat * MathF.Sqrt(_roarPsdPerKw2) * MathF.Sqrt(cell.Vigour * (1f + _flare)) * gust * RoarPart;
            // The fizz follows how hard the fuel is gassing: what is burning, how wet, and the clusters of
            // pockets reaching temperature together that the crackles come in.
            float fizz = FizzPascals * FizzPascals * (heat / 80f) * MathF.Min(2f, Spec.Moisture / 0.2f) * cell.Cluster
                       * (Spec.Fuel == FireFuel.Vehicle ? VehicleFizz : 1f);
            // The steam the water makes as it flashes on the hot fuel: a gas jet through the char like the
            // fizz, its power the heat it is taking.
            fizz += FizzPascals * FizzPascals * (taken / 80f) * SizzleOverFizz;
            cell.Fizz = fizz;
            float crackles = CracklesPer100Kw * 0.8f * MathF.Pow(MathF.Max(0f, heat) / 80f, CrackleHeatExponent) * FuelCrackle * cell.Vigour * cell.Cluster * (1f + 1.5f * _flare)
                           * (1f + 2f * cell.Torch) * (1f + 0.2f * MathF.Max(0f, cell.Wind - 2f))
                           // Drops bursting into steam on the hot surface pop like small crackles.
                           + SizzlePopsPer100Kw * taken / 100f;
            rateTotal += crackles / MathF.Max(1e-3f, cell.Cluster);
            for (int p = 0; p < places; p++)
            {
                float wgt = _weights[c * places + p];
                float here = p == 0 ? (1f - s) + s * wgt : s * wgt;

                _crackleRateAt[p] += here * crackles;
            }
        }
        _crackleNow = rateTotal;
        HeatNowKw = total;
        for (int p = 0; p < places; p++)
        {
            // The share of this place's crackles too many to draw is its crackle noise.
            float rate = _crackleRateAt[p];
            float cut = DrawnFrom(rate);
            _bedTarget[p] = MathF.Sqrt(rate * EnergyBelow(cut)) * _pocketScale * _burn * CracklePart;
        }

        // Steam jets come and go, more of them for wetter wood (logs).
        if (Spec.Fuel == FireFuel.Logs)
        {
            float births = 0.05f * Spec.Moisture / 0.2f * _burn * _life * MathF.Min(4f, MathF.Sqrt(Spec.HeatReleaseKw / 80f));
            for (int i = 0; i < _jets.Length; i++)
            {
                ref var j = ref _jets[i];
                if (j.Live)
                {
                    j.Age += dt;
                    if (j.Age >= j.Life) j.Live = false;
                    continue;
                }
                if (_sum.Uniform() < births * dt)
                {
                    j.Live = true;
                    j.Age = 0f;
                    j.Life = 3f + 15f * _sum.Uniform();
                    j.Strength = 0.3f + 0.7f * _sum.Uniform();
                    j.Whistle = _sum.Uniform() < 0.3f ? 0.4f + 0.6f * _sum.Uniform() : 0f;
                    j.WhistleHz = 1500f + 3000f * _sum.Uniform();
                    j.DriftHz = 0.2f + 0.8f * _sum.Uniform();
                    j.DriftPhase = MathF.Tau * _sum.Uniform();
                    j.Hiss.Tune(2500f + 3000f * _sum.Uniform(), 0.8f, _rate);
                    j.Tone.Tune(j.WhistleHz, 60f, _rate);
                }
            }

            // A log giving way: more logs in a bigger fire, each a bigger log.
            _settleClock -= dt * MathF.Sqrt(MathF.Max(0.25f, Spec.HeatReleaseKw / 80f)) * _life;
            if (_settleClock <= 0f && _burn > 0.5f)
            {
                Settle();
                _settleClock = 40f + 90f * _sum.Uniform();
            }
        }

        // Falls: burnt branches through the crown; in a building, burnt pieces of it.
        if (Spec.Fuel is FireFuel.Trees or FireFuel.Crown or FireFuel.Structure && FallPart > 0f)
        {
            _fallClock -= dt * FallRate * _life * _burn * _cells.Length;
            if (_fallClock <= 0f)
            {
                Fall(PickCell(), Spec.Fuel == FireFuel.Structure ? 2f : 1f);
                _fallClock = -MathF.Log(MathF.Max(1e-6f, _sum.Uniform()));
            }
        }

        if (!double.IsNaN(Age)) Timeline(dt);
        else if (Spec.Fuel == FireFuel.Structure && FallPart > 0f && !float.IsNaN(_collapseClock))
        {
            // A building burning with no known start: now and then a part of it comes down.
            _collapseClock -= dt;
            if (_collapseClock <= 0f) { Collapse(PickCell(), 0.4f + 0.6f * _sum.Uniform()); _collapseClock = 300f + 900f * _sum.Uniform(); }
        }
    }

    /// <summary>The fire's heat release now, kW.</summary>
    public float HeatNowKw { get; private set; }

    /// <summary>How far a gust's push on the flames reaches into a fire, by how fast its own plume rises
    /// against the 80 kW pit's (McCaffrey's plume velocity, about Q^1/5): a gust fans a campfire and
    /// barely moves a house fire's plume.</summary>
    private float GustReach => MathF.Pow(80f / MathF.Max(1f, Spec.HeatReleaseKw * (_cells.Length > 0 ? _cells[0].Share : 1f)), 0.2f);

    /// <summary>Torching episodes a second per body of foliage. ESTIMATE (docs/FIRE.md 5.2).</summary>
    private const float TorchRate = 1f / 90f;
    /// <summary>Falls a second per body of fire, burning fully. ESTIMATE (docs/FIRE.md 6.1).</summary>
    private const float FallRate = 1f / 40f;

    /// <summary>Events that happen once in a fire's life, by its age: windows, struts and tyres, the tank,
    /// a building's collapses.</summary>
    private void Timeline(float dt)
    {
        float age = (float)Age;
        float full = MathF.Max(1f, Spec.GrowthSeconds);
        if (!_primed)
        {
            // Heard first part way through its life: what has already happened is past, not all at once now.
            _primed = true;
            for (int i = 0; i < _paneCrackAt.Length; i++)
            {
                float since = age - full * (_cells.Length > 1 ? _cells[_paneCell[i]].CatchAt : 0.6f);
                if (since >= _paneCrackAt[i]) { _paneFallAt[i] += _paneCrackAt[i]; _paneCrackAt[i] = -1f; }
                if (_paneCrackAt[i] < 0f && since >= _paneFallAt[i]) _paneFallAt[i] = float.NaN;
            }
            for (int i = 0; i < _burstAt.Length; i++) if (age >= _burstAt[i]) _burstAt[i] = float.NaN;
            if (age >= _tankAt) _tankAt = float.NaN;
        }
        for (int i = 0; i < _paneCrackAt.Length; i++)
        {
            // Time since this pane's part of the fire was fully going.
            float since = age - full * (_cells.Length > 1 ? _cells[_paneCell[i]].CatchAt : 0.6f);
            if (_paneCrackAt[i] > 0f && since >= _paneCrackAt[i])
            {
                PaneCrack(_cells[_paneCell[i]].NearestPlace);
                if (Spec.Fuel == FireFuel.Vehicle) Dice(_cells[_paneCell[i]].NearestPlace);
                _paneFallAt[i] += _paneCrackAt[i];
                _paneCrackAt[i] = -1f;
                // Air in through the hole: the room flares.
                if (Spec.Fuel == FireFuel.Structure) _cells[_paneCell[i]].Vigour = MathF.Min(2.5f, _cells[_paneCell[i]].Vigour + 0.6f);
            }
            else if (_paneCrackAt[i] < 0f && !float.IsNaN(_paneFallAt[i]) && since >= _paneFallAt[i])
            {
                PaneFall(_cells[_paneCell[i]].NearestPlace);
                _paneFallAt[i] = float.NaN;
            }
        }
        for (int i = 0; i < _burstAt.Length; i++)
        {
            if (float.IsNaN(_burstAt[i]) || age < _burstAt[i]) continue;
            Burst(PickCell(), _burstPascals[i], _burstSeconds[i], _burstHiss[i]);
            _burstAt[i] = float.NaN;
        }
        if (!float.IsNaN(_tankAt) && age >= _tankAt)
        {
            _tankAt = float.NaN;
            _flare = MathF.Min(3f, _flare + 2f);
        }
        if (Spec.Fuel == FireFuel.Structure && !float.IsNaN(_collapseClock) && age >= full)
        {
            _collapseClock -= dt;
            if (_collapseClock <= 0f && _collapses < 4)
            {
                // The ceilings first, the roof last: each bigger than the one before.
                Collapse(PickCell(), 0.4f + 0.2f * _collapses);
                _collapses++;
                _collapseClock = 120f + 400f * _sum.Uniform();
            }
        }
    }

    private int PickCell() => Math.Min(_cells.Length - 1, (int)(_sum.Uniform() * _cells.Length));

    /// <summary>The place an event of cell <paramref name="c"/> is heard from: by the cell's weights,
    /// spread or merged to the middle.</summary>
    private int PlaceOf(int c)
    {
        int places = _sums.Length;
        if (places == 1) return 0;
        float s = Math.Clamp(Spread, 0f, 1f);
        if (_sum.Uniform() >= s) return 0;
        float u = _sum.Uniform(), acc = 0f;
        for (int p = 0; p < places; p++)
        {
            acc += _weights[c * places + p];
            if (u < acc) return p;
        }
        return places - 1;
    }

    /// <summary>The next sample at each place, pascals at a metre from it: <paramref name="places"/>
    /// holds <see cref="Places"/> of them. Their sum is the fire.</summary>
    public void NextPlaces(Span<float> places)
    {
        if (--_untilBlock <= 0)
        {
            _untilBlock = Block;
            Schedule(Block / _rate);
        }
        if (--_untilSub <= 0)
        {
            _untilSub = Sub;
            Puff();
        }
        _samples++;
        int n = _sums.Length;
        for (int p = 0; p < n; p++)
        {
            var sum = _sums[p];
            _roarAmp[p] += _roarStep[p];
            _fizzAmp[p] += (_fizzTarget[p] - _fizzAmp[p]) * _glide;
            _bedAmp[p] += (_bedTarget[p] - _bedAmp[p]) * _glide;
            float y = sum.Next();
            if (_roarAmp[p] > 0f)
                y += _roar[p].Process(sum.Signed() * 1.7320508f) * _roarUnit * _roarAmp[p];
            if (_fizzAmp[p] > 1e-9f)
                y += _fizz[p].Process(sum.Signed() * 1.7320508f) * _fizzNorm * _fizzAmp[p];
            if (_bedAmp[p] > 1e-9f)
            {
                float x = sum.Signed() * 1.7320508f;
                _bedLp0[p] = (1f - _bedA1) * x + _bedA1 * _bedLp0[p];
                _bedLp[p] = (1f - _bedA1) * _bedLp0[p] + _bedA1 * _bedLp[p];
                _bedHp[p] = (1f - _bedA2) * _bedLp[p] + _bedA2 * _bedHp[p];
                y += (_bedLp[p] - _bedHp[p]) * _bedNorm * _bedAmp[p];
            }
            if (p == 0 && Spec.Fuel == FireFuel.Logs) y += Steam();
            _placeOut[p] = y;
        }
        for (int i = 0; i < _clips.Length; i++)
        {
            ref var c = ref _clips[i];
            if (c.Data == null) continue;
            if (c.Delay > 0) { c.Delay--; continue; }
            if (c.Place < n) _placeOut[c.Place] += c.Data[c.Pos] * c.Gain;
            if (++c.Pos >= c.Data.Length) c.Data = null;
        }
        for (int p = 0; p < n && p < places.Length; p++) places[p] = _placeOut[p];
    }

    /// <summary>The puffs, every <see cref="Sub"/> samples: each body's own, and each place's roar the sum
    /// of its bodies' powers, ramped to over the next stretch.</summary>
    private void Puff()
    {
        int places = _sums.Length;
        float s = Math.Clamp(Spread, 0f, 1f);
        Span<float> power = stackalloc float[places];
        Span<float> fizzPower = stackalloc float[places];
        for (int c = 0; c < _cells.Length; c++)
        {
            ref var cell = ref _cells[c];
            cell.Phase += cell.PuffHz * (1f + 0.35f * cell.Jitter) * Sub / _rate;
            if (cell.Phase >= 1f)
            {
                cell.Phase -= 1f;
                cell.Jitter = _sum.Signed();               // no two puffs alike, in length...
                cell.Depth = 0.1f + 0.25f * _sum.Uniform(); // ...or in strength
            }
            // Between the puffs the burning flickers: noise, high-passed at the puffing rate and
            // low-passed at three and ten times it.
            float x = _sum.Signed();
            float hp = _flickA * (cell.FlickHp + x - cell.FlickX);
            cell.FlickHp = hp;
            cell.FlickX = x;
            cell.FlickLo = (1f - _flickB) * hp + _flickB * cell.FlickLo;
            cell.FlickHi = (1f - _flickC) * cell.FlickLo + _flickC * cell.FlickHi;
            float flick = Flicker * cell.FlickHi * _flickNorm;
            // A puff swells the roar rather than switching it: the flames never stop burning between them.
            cell.Env = MathF.Max(0.05f, 1f + cell.Depth * MathF.Sin(MathF.Tau * cell.Phase) + flick);
            cell.Flick = MathF.Max(0.05f, 1f + flick);
            float pr = cell.Roar * cell.Env;
            float pw = pr * pr;
            // The gas let out of the fuel follows the flames' flicker: the heat on it does.
            float fz = cell.Fizz * cell.Flick * cell.Flick;
            for (int p = 0; p < places; p++)
            {
                // The roar from the flames' places, the gas out of the fuel from the bed's.
                float wgt = _weights[c * places + p], rw = _roarWeights[c * places + p];
                power[p] += (p == 0 ? (1f - s) + s * rw : s * rw) * pw;
                fizzPower[p] += (p == 0 ? (1f - s) + s * wgt : s * wgt) * fz;
            }
        }
        for (int p = 0; p < places; p++)
        {
            _roarStep[p] = (MathF.Sqrt(power[p]) - _roarAmp[p]) / Sub;
            _fizzTarget[p] = MathF.Sqrt(fizzPower[p]) * CracklePart;
        }
    }

    /// <summary>The steam jets, at the middle.</summary>
    private float Steam()
    {
        float y = 0f;
        for (int i = 0; i < _jets.Length; i++)
        {
            ref var j = ref _jets[i];
            if (!j.Live) continue;
            float life = MathF.Sin(MathF.PI * j.Age / j.Life);
            float level = j.Strength * life * _burn * SmallestCracklePascals * 0.06f;
            float hn = _sum.Signed() * 1.7320508f;
            y += j.Hiss.Process(hn) * level * SteamPart;
            if (j.Whistle > 0f)
            {
                // The tone wanders as the pressure behind it does.
                if ((_samples & 255) == 0)
                {
                    float drift = 1f + 0.06f * MathF.Sin(j.DriftPhase + MathF.Tau * j.DriftHz * _clock);
                    j.Tone.Tune(j.WhistleHz * drift, 60f, _rate);
                }
                y += j.Tone.Process(hn) / Resonator.NoiseGain(j.WhistleHz, 60f, _rate) * level * j.Whistle * 0.3f * SteamPart;
            }
        }
        return y;
    }

    /// <summary>The whole fire, every place summed.</summary>
    public float Next()
    {
        NextPlaces(_placeOut);
        float all = 0f;
        for (int k = 0; k < _placeOut.Length; k++) all += _placeOut[k];
        return all;
    }

    private void Schedule(float dt)
    {
        int places = _sums.Length;
        // The crackles drawn one by one at each place: the loud end of the law, the rest are its noise.
        if (CracklePart > 0f)
        {
            for (int p = 0; p < places; p++)
            {
                float rate = _crackleRateAt[p];
                if (rate <= 0f) continue;
                float cut = DrawnFrom(rate);
                float drawn = rate * TailShare(cut);
                int count = _sum.Poisson(drawn * dt);
                for (int k = 0; k < count; k++) Crackle(_sums[p], p, (int)(_sum.Uniform() * Block), cut, 0f);
            }
        }

        // Strikes due in this block.
        for (int i = 0; i < _pending.Length; i++)
        {
            ref var k = ref _pending[i];
            if (!k.Live) continue;
            k.In -= dt;
            if (k.In > 0f) continue;
            k.Live = false;
            Knock(_sums[Math.Min(k.Place, places - 1)], (int)(_sum.Uniform() * Block), k.Hit, k.Scale);
        }

        // Embers coming down.
        for (int i = 0; i < _emberAt.Length; i++)
        {
            if (_emberAt[i] < 0f) continue;
            _emberAt[i] -= dt;
            if (_emberAt[i] > 0f) continue;
            _emberAt[i] = -1f;
            int at = (int)(_sum.Uniform() * Block);
            float p = SmallestCracklePascals * 0.15f * _emberSize[i] * CracklePart;
            var lands = _sums[Math.Min(_emberPlace[i], places - 1)];
            lands.Pulse(at, 25e-6f, p);
            Span<float> hz = stackalloc float[] { 5200f + 2000f * _sum.Signed(), 9000f + 2500f * _sum.Signed() };
            Span<float> tau = stackalloc float[] { 0.0015f, 0.0008f };
            Span<float> amp = stackalloc float[] { 0.5f * p, 0.3f * p };
            lands.Ring(at, hz, tau, amp);
        }
    }

    // ── Crackle sizes ────────────────────────────────────────────────────────────────────────────

    /// <summary>The share of crackles over size <paramref name="s"/> on the truncated power law.</summary>
    private static float TailShare(float s)
    {
        float a = CrackleExponent - 1f;
        float top = MathF.Pow(CrackleRange, -a);
        return Math.Clamp((MathF.Pow(MathF.Max(1f, s), -a) - top) / (1f - top), 0f, 1f);
    }

    /// <summary>The size from which a place's crackles are drawn one by one: all of them while there are
    /// few, only the loud tail of a crowd.</summary>
    private static float DrawnFrom(float rate)
    {
        if (rate <= MaxDrawnCrackles) return 1f;
        // TailShare(s) = Max / rate.
        float a = CrackleExponent - 1f;
        float top = MathF.Pow(CrackleRange, -a);
        float target = MaxDrawnCrackles / rate * (1f - top) + top;
        return Math.Clamp(MathF.Pow(target, -1f / a), 1f, CrackleRange);
    }

    /// <summary>The mean energy, Pa²·s at a metre, of a crackle on the law that is UNDER size <paramref name="s"/>,
    /// per crackle of the whole law: what the crackles not drawn carry.</summary>
    private float EnergyBelow(float s)
    {
        if (s <= 1f) return 0f;
        // ∫ E(x) pdf(x) dx from 1 to s, on the log grid.
        float a = CrackleExponent - 1f;
        float norm = a / (1f - MathF.Pow(CrackleRange, -a));
        float lnR = MathF.Log(CrackleRange), lnS = MathF.Log(s);
        double sum = 0;
        for (int i = 0; i < EnergyGrid; i++)
        {
            float l0 = lnR * i / EnergyGrid, l1 = lnR * (i + 1) / EnergyGrid;
            if (l0 >= lnS) break;
            float hi = MathF.Min(l1, lnS);
            float mid = 0.5f * (l0 + hi), x = MathF.Exp(mid);
            // pdf(x) dx = norm x^-(a+1) x dln x
            sum += _energyLogSize[i] * norm * MathF.Pow(x, -a) * (hi - l0);
        }
        return (float)sum;
    }

    /// <summary>A crackle's energy at each size on the log grid, measured by rendering one.</summary>
    private static float[] CrackleEnergies(float rate)
    {
        var table = new float[EnergyGrid];
        float lnR = MathF.Log(CrackleRange);
        for (int i = 0; i < EnergyGrid; i++)
        {
            float size = MathF.Exp(lnR * (i + 0.5f) / EnergyGrid);
            var sum = new EventSum(rate, 11 + i);
            double e = 0;
            const int tries = 8;
            for (int t = 0; t < tries; t++)
            {
                float p = SmallestCracklePascals * size;
                sum.Impact(0, 25e-6f, 0.15e-3f * MathF.Pow(size, 0.4f), p);
                sum.Burst(2, 0.0001f, 0.0015f + 0.002f * sum.Uniform(), 0.2f * p, 1500f, 12000f);
                for (int k = 0; k < (int)(0.05f * rate); k++) { float v = sum.Next(); e += v * v; }
            }
            table[i] = (float)(e / tries / rate);
        }
        return table;
    }

    private void Crackle(EventSum place, int placeIndex, int at, float from, float given)
    {
        DrawnCrackles++;
        // Size on the power law over `from`: P(size > s) ∝ s^-(α-1), cut at the range.
        float a = CrackleExponent - 1f;
        float size = given;
        if (size <= 0f)
        {
            float lo = MathF.Pow(MathF.Max(1f, from), -a), hi = MathF.Pow(CrackleRange, -a);
            float u = lo + (hi - lo) * _sum.Uniform();
            size = Math.Clamp(MathF.Pow(u, -1f / a), 1f, CrackleRange);
        }
        float p = SmallestCracklePascals * _pocketScale * size * _burn * CracklePart;
        // A pocket bursting is a volume of gas let out at once: the pressure is the rate of change of
        // the outflow, a spike as the wall gives and a tail as the pocket empties. A bigger pocket
        // empties for longer, so a big pop has a body under its crack.
        float empty = 0.15e-3f * MathF.Pow(size, 0.4f);
        place.Impact(at, 25e-6f, empty, p);
        // The char round it: a scatter of fragments, a few milliseconds of rattle.
        place.Burst(at + 2, 0.0001f, 0.0015f + 0.002f * _sum.Uniform(), 0.2f * p, 1500f, 12000f);
        if (size >= EmberSize)
        {
            for (int i = 0; i < _emberAt.Length; i++)
            {
                if (_emberAt[i] >= 0f) continue;
                // Thrown up and out: it lands in a third of a second to a second.
                _emberAt[i] = 0.3f + 0.7f * _sum.Uniform();
                _emberSize[i] = 0.5f + _sum.Uniform();
                _emberPlace[i] = placeIndex;
                break;
            }
        }
    }

    // ── Logs ─────────────────────────────────────────────────────────────────────────────────────

    private void Settle()
    {
        Settles++;
        // A log giving way: a soft thump of a few kilos dropping a few centimetres onto the bed; a bigger
        // fire's logs are bigger.
        int cell = PickCell();
        var log = _sums[PlaceOf(cell)];
        float scale = MathF.Min(4f, MathF.Pow(MathF.Max(1f, Spec.HeatReleaseKw / 80f), 0.25f));
        int at = (int)(_sum.Uniform() * Block);
        log.Pulse(at, 0.004f * scale, SmallestCracklePascals * 6f * scale * SettlePart);
        // Charcoal pieces tumbling: a dozen or so brittle little rings over half a second.
        int pieces = (int)((6 + (int)(14 * _sum.Uniform())) * scale);
        Span<float> hz = stackalloc float[3];
        Span<float> tau = stackalloc float[3];
        Span<float> amp = stackalloc float[3];
        for (int k = 0; k < pieces; k++)
        {
            int when = at + (int)(_sum.Uniform() * _sum.Uniform() * 0.4f * scale * _rate);
            float p = SmallestCracklePascals * (0.4f + 1.6f * _sum.Uniform()) * SettlePart;
            float f0 = 2200f + 4000f * _sum.Uniform();
            hz[0] = f0; hz[1] = f0 * 2.7f; hz[2] = f0 * 5.1f;
            tau[0] = 0.004f; tau[1] = 0.002f; tau[2] = 0.001f;
            amp[0] = 0.5f * p; amp[1] = 0.3f * p; amp[2] = 0.2f * p;
            log.Pulse(when, 40e-6f, p);
            log.Ring(when, hz, tau, amp);
        }
        _flare = MathF.Min(1.5f, _flare + 0.8f);
    }

    // ── Falls and collapses ──────────────────────────────────────────────────────────────────────

    /// <summary>A piece of burnt wood coming down from somewhere in the fuel: a branch through a crown,
    /// striking the ones under it on the way, or a burnt piece of a building. Masses on a power law,
    /// 0.2-20 kg (a building's twice that).</summary>
    private void Fall(int cell, float heavier)
    {
        Falls++;
        int place = PlaceOf(cell);
        float u = MathF.Max(0.01f, _sum.Uniform());
        float mass = MathF.Min(20f, 0.2f * MathF.Pow(u, -1f / 0.8f)) * heavier;
        float height = MathF.Max(0.5f, Spec.FuelHeightMetres * (0.3f + 0.7f * _sum.Uniform()));
        float when = 0f;
        // Through the branches under it, in a crown: each strike takes most of its speed, and it falls on
        // from there.
        float fallen = 0f;
        if (Spec.Fuel is FireFuel.Trees or FireFuel.Crown)
        {
            int strikes = (int)(_sum.Uniform() * 3.5f);
            for (int k = 0; k < strikes; k++)
            {
                float drop = (height - fallen) * (0.2f + 0.4f * _sum.Uniform());
                fallen += drop;
                when += MathF.Sqrt(2f * drop / 9.81f);
                Later(when, place, _fallOnWood.At(mass * 0.5f, MathF.Sqrt(2f * 9.81f * drop)), 0.6f);
            }
        }
        float rest = MathF.Max(0.3f, height - fallen);
        when += MathF.Sqrt(2f * rest / 9.81f);
        Later(when, place, _fallOnGround.At(mass, MathF.Sqrt(2f * 9.81f * rest)), 1f);
    }

    /// <summary>A strike <paramref name="seconds"/> from now.</summary>
    private void Later(float seconds, int place, ImpactTable.Entry hit, float scale)
    {
        for (int i = 0; i < _pending.Length; i++)
        {
            if (_pending[i].Live) continue;
            _pending[i] = new Pending { In = seconds, Place = place, Hit = hit, Scale = scale, Live = true };
            return;
        }
    }

    /// <summary>A part of a building coming down: the beams and boards first, the rest after, over a
    /// second or two, and the fire flaring with the air it lets in.</summary>
    private void Collapse(int cell, float size)
    {
        Collapses++;
        int place = PlaceOf(cell);
        int pieces = 6 + (int)(20 * size);
        float span = MathF.Min(2.5f, 0.6f + 1.5f * size);
        for (int k = 0; k < pieces; k++)
        {
            float mass = 5f + 60f * size * _sum.Uniform() * _sum.Uniform();
            float speed = MathF.Sqrt(2f * 9.81f * MathF.Max(1f, Spec.FuelHeightMetres * (0.4f + 0.6f * _sum.Uniform())));
            Later(_sum.Uniform() * _sum.Uniform() * span, place, _fallOnGround.At(mass, speed), 1f);
        }
        _sums[place].Burst((int)(0.2f * _rate), 0.05f, 0.4f * size + 0.2f, SmallestCracklePascals * 4f * size, 800f, 8000f);
        _cells[cell].Vigour = MathF.Min(3f, _cells[cell].Vigour + 1f + size);
        _flare = MathF.Min(3f, _flare + 0.8f * size);
    }

    /// <summary>One strike as the impact law says it: the blow (a pulse as long as the contact) and the
    /// struck wood's modes, inharmonic, the high ones dying first.</summary>
    private void Knock(EventSum sum, int at, ImpactTable.Entry e, float scale)
    {
        if (e.Pascals <= 0f) return;
        float p = e.Pascals * scale * FallPart;
        sum.Pulse(at, MathF.Max(25e-6f, 0.16f / e.Hz), p);
        Span<float> hz = stackalloc float[4];
        Span<float> tau = stackalloc float[4];
        Span<float> amp = stackalloc float[4];
        ReadOnlySpan<float> ratios = stackalloc float[] { 1f, 1.71f, 2.63f, 4.07f };
        ReadOnlySpan<float> gains = stackalloc float[] { 0.5f, 0.36f, 0.25f, 0.15f };
        float detune = 0.94f + 0.12f * _sum.Uniform();
        for (int m = 0; m < 4; m++)
        {
            hz[m] = e.Hz * ratios[m] * detune;
            tau[m] = MathF.Max(0.003f, e.DecaySeconds / 6.9f / ratios[m]);
            amp[m] = gains[m] * p;
        }
        sum.Ring(at, hz, tau, amp);
        sum.Burst(at, 0.0005f, MathF.Max(0.004f, e.DecaySeconds * 0.3f), 0.25f * p, 300f, 6000f);
        // Burning wood landing throws up sparks and breaks char off itself.
        if (scale >= 1f) sum.Burst(at + 4, 0.002f, 0.05f, 0.05f * p, 1500f, 9000f);
    }

    // ── Glass and bursts ─────────────────────────────────────────────────────────────────────────

    private void PaneModes(FireSpec spec)
    {
        var glass = AcousticRegistry.GetProperties("Glass");
        var modes = PanelAcoustics.Modes(glass, spec.PaneWidthMetres, spec.PaneHeightMetres, spec.PaneThicknessMm * 1e-3f, 8000f, 5);
        for (int m = 0; m < _paneModeHz.Length; m++)
        {
            if (m < modes.Count)
            {
                _paneModeHz[m] = modes[m].Hz;
                _paneModeAmp[m] = modes[m].Weight;
                _paneModeDecay[m] = PanelAcoustics.RingSeconds(glass, modes[m].Hz) / 6.9f;
            }
        }
    }

    /// <summary>A pane cracking in the heat: the crack runs across it in a millisecond (about 1500 m/s)
    /// and the pane rings as its stress lets go. ESTIMATE: about 90 dB peak at a metre (docs/FIRE.md 6.2).</summary>
    private void PaneCrack(int place)
    {
        PaneCracks++;
        if (GlassPart <= 0f) return;
        var sum = _sums[place];
        float p = 0.6f * GlassPart * (0.5f + _sum.Uniform());
        int at = (int)(_sum.Uniform() * Block);
        sum.Impact(at, 15e-6f, 0.4e-3f, p);
        Span<float> amp = stackalloc float[_paneModeHz.Length];
        for (int m = 0; m < amp.Length; m++) amp[m] = 0.3f * p * _paneModeAmp[m];
        sum.Ring(at, _paneModeHz, _paneModeDecay, amp);
        // Often a second crack runs off the first a moment later.
        if (_sum.Uniform() < 0.5f) sum.Impact(at + (int)((0.05f + 0.5f * _sum.Uniform()) * _rate), 15e-6f, 0.3e-3f, 0.5f * p);
    }

    /// <summary>A cracked pane falling out of its frame onto what is under it: the glass model's landing.</summary>
    private void PaneFall(int place)
    {
        PaneFalls++;
        if (GlassPart <= 0f || _paneFallClip is not { IsCompletedSuccessfully: true } t || t.Result == null) return;
        StartClip(t.Result, place, GlassPart);
    }

    /// <summary>A car's tempered side window dicing in the heat: the glass model's own break.</summary>
    private void Dice(int place)
    {
        if (GlassPart <= 0f || _diceClip is not { IsCompletedSuccessfully: true } t || t.Result == null) return;
        StartClip(t.Result, place, GlassPart);
    }

    private void StartClip(float[] data, int place, float gain)
    {
        for (int i = 0; i < _clips.Length; i++)
        {
            if (_clips[i].Data != null) continue;
            _clips[i] = new Clip { Data = data, Pos = 0, Place = place, Gain = gain, Delay = (int)(_sum.Uniform() * Block) };
            return;
        }
    }

    /// <summary>A sealed vessel bursting: a blast, its peak from the gas's stored energy, as a gram or so of
    /// TNT (Kinney and Graham), and the loudness law's compression applied to its excess over the fire's
    /// own level, since inside one source the mixer cannot place it on its own (docs/FIRE.md 6.4).</summary>
    private void Burst(int cell, float pascals, float seconds, bool hiss)
    {
        Bursts++;
        if (BurstPart <= 0f) return;
        int place = PlaceOf(cell);
        var sum = _sums[place];
        float levelPa = 2e-5f * MathF.Pow(10f, Spec.SourceLevelDb / 20f);
        float excessDb = 20f * MathF.Log10(MathF.Max(1e-6f, pascals) / MathF.Max(1e-6f, levelPa));
        float placed = levelPa * MathF.Pow(10f, MathF.Max(0f, excessDb) * Loudness.DynamicRangeCompression / 20f);
        int at = (int)(_sum.Uniform() * Block);
        sum.Impact(at, 15e-6f, seconds, placed * BurstPart);
        if (hiss) sum.Burst(at + 20, 0.01f, 0.4f + 0.8f * _sum.Uniform(), 0.03f * placed * BurstPart, 1500f, 9000f);
        _flare = MathF.Min(3f, _flare + 0.3f);
    }

    /// <summary>
    /// The blast of a gas at <paramref name="pressurePa"/> absolute in <paramref name="volumeM3"/> let go at
    /// once: its isentropic expansion energy as TNT (4.6 MJ/kg), and Kinney and Graham's peak overpressure
    /// and positive phase at a metre. A car's gas strut (150 bar, 60 cm³): about 160 dB peak; a hot tyre
    /// (3.3 bar, 25 L): about 175.
    /// </summary>
    public static (float Pascals, float Seconds) Blast(float pressurePa, float volumeM3)
    {
        const float p0 = 101325f, g = 1.4f;
        float e = pressurePa * volumeM3 / (g - 1f) * (1f - MathF.Pow(p0 / pressurePa, (g - 1f) / g));
        float tnt = MathF.Max(1e-7f, e / 4.6e6f);
        float z = 1f / MathF.Pow(tnt, 1f / 3f);
        float ratio = 808f * (1f + (z / 4.5f) * (z / 4.5f))
                    / MathF.Sqrt(1f + (z / 0.048f) * (z / 0.048f))
                    / MathF.Sqrt(1f + (z / 0.32f) * (z / 0.32f))
                    / MathF.Sqrt(1f + (z / 1.35f) * (z / 1.35f));
        // Positive phase: about 1.6 ms per kg^1/3 at these scaled distances (Kinney and Graham's td/W^1/3).
        float td = 1.6e-3f * MathF.Pow(tnt, 1f / 3f) * MathF.Min(4f, MathF.Pow(z / 10f, 0.4f));
        return (ratio * p0, MathF.Max(5e-5f, td));
    }

    /// <summary>What has happened so far, for the lab and the tests.</summary>
    public int PaneCracks, PaneFalls, Bursts, Collapses, Falls, Torches, Settles;
    public long DrawnCrackles;

    /// <summary>Whether the glass this fire breaks has been rendered (or there is none).</summary>
    public bool GlassReady => (_paneFallClip?.IsCompleted ?? true) && (_diceClip?.IsCompleted ?? true);

    /// <summary>For the lab: how many of each kind of event have been and how many cells are going.</summary>
    public string Census()
        => $"  {Cells} bodies of fire {_cellDiameter:F1} m across, puffing at {_puffHz:F2} Hz, roar f^-{RoarTailExponent:F1} from {KneeOverPuff * _puffHz:F0} Hz; " +
           $"{Places} places; heat {HeatNowKw / 1000f:F2} MW; crackles {CrackleRate:F0}/s";
}

/// <summary>
/// What a piece of burning wood striking something sounds like, from the game's impact law
/// (ImpactAcoustics.Between), at a grid of masses and speeds worked out once: its peak at a metre, its
/// note and how long it rings.
/// </summary>
internal sealed class ImpactTable
{
    public readonly record struct Entry(float Pascals, float Hz, float DecaySeconds);

    private static readonly float[] Masses = { 0.1f, 0.3f, 1f, 3f, 10f, 30f, 100f };
    private static readonly float[] Speeds = { 1f, 2f, 4f, 8f, 16f, 24f };
    private readonly Entry[] _grid = new Entry[Masses.Length * Speeds.Length];

    public ImpactTable(MaterialProperties hitter, MaterialProperties struck)
    {
        for (int m = 0; m < Masses.Length; m++)
            for (int v = 0; v < Speeds.Length; v++)
            {
                // A branch: about a metre per 3 kg at these sizes, struck along its side.
                float size = MathF.Min(3f, 0.3f + MathF.Pow(Masses[m], 0.5f) * 0.4f);
                var sounds = ImpactAcoustics.Between(hitter, struck, Vector3.Zero, Speeds[v], Masses[m], 500f, size, 0.1f, 0.05f, struckIsFixed: true);
                if (sounds.Count == 0) continue;
                var s = sounds[0];
                _grid[m * Speeds.Length + v] = new Entry(2e-5f * MathF.Pow(10f, s.LevelDb / 20f), s.Hz, s.DecaySeconds);
            }
    }

    public Entry At(float mass, float speed)
    {
        int m = Nearest(Masses, mass), v = Nearest(Speeds, speed);
        var e = _grid[m * Speeds.Length + v];
        // The level scales with the energy between grid points.
        float scale = MathF.Sqrt(MathF.Max(0.01f, mass / Masses[m]) * MathF.Max(0.01f, speed * speed / (Speeds[v] * Speeds[v])));
        return e with { Pascals = e.Pascals * scale };
    }

    private static int Nearest(float[] grid, float x)
    {
        int best = 0;
        for (int i = 1; i < grid.Length; i++) if (MathF.Abs(MathF.Log(grid[i] / x)) < MathF.Abs(MathF.Log(grid[best] / x))) best = i;
        return best;
    }
}

/// <summary>
/// The glass a fire breaks, rendered by the glass model (GlassFracture) once per kind of pane and kept: off
/// the audio threads, since one render takes a good part of a second. Pascals at a metre.
/// </summary>
internal static class GlassClips
{
    private static readonly ConcurrentDictionary<string, Task<float[]?>> _clips = new();

    public static Task<float[]?> Get(GlassFracture.Spec spec, int rate)
        => _clips.GetOrAdd(GlassFracture.Key(spec) + "@" + rate, _ => Task.Run(() =>
        {
            try
            {
                var pa = GlassFracture.Render(spec, rate);
                var clip = new float[pa.Length];
                for (int i = 0; i < pa.Length; i++) clip[i] = (float)pa[i];
                return clip;
            }
            catch (Exception) { return (float[]?)null; }
        }));
}
