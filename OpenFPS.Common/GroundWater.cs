using System.Globalization;
using System.Text;
using System.Threading;

namespace OpenFPS.Common;

// Rain running over the ground (docs/RUNNING_WATER.md section 13, docs/MATTER.md 4.2): what each kind of
// surface lets run off, how fast it gets to a drainage line, and the state of one map's water, worked out
// on the server and sent to every client (WorldStateUpdate.GroundWater) so the creek a client hears and the
// wetness a fire is put out by come from the same rain.

/// <summary>What the ground is covered by, as far as rain is concerned: the land-cover rows of the curve
/// number tables (TR-55, Tables 2-2a and 2-2c). One byte per 2 m cell.</summary>
public enum GroundSurface : byte
{
    /// <summary>Open ground the game knows only as dirt: pasture, shrub, crops, rough grass.</summary>
    Open = 0,
    /// <summary>Roofs, roads, drives, paving, anything built over the ground.</summary>
    Impervious = 1,
    /// <summary>A gravel road or yard.</summary>
    Gravel = 2,
    /// <summary>A dirt road or track.</summary>
    DirtRoad = 3,
    /// <summary>A mown lawn, a park, a verge.</summary>
    Lawn = 4,
    /// <summary>Woods: the forest floor under trees.</summary>
    Woods = 5,
    /// <summary>Standing or running water: a pond, a lake, a river the survey shows flat.</summary>
    Water = 6,
}

/// <summary>
/// The laws, as pure functions. Sources: USDA SCS (1986) Technical Release 55, "Urban Hydrology for Small
/// Watersheds", 2nd ed. (TR-55): the curve number, its initial abstraction and the travel times; USDA NRCS
/// National Engineering Handbook part 630 (NEH-630) ch. 7 (soil groups) and ch. 15 (lag); Chow (1959)
/// Open-Channel Hydraulics, Table 5-6 (channel roughness); Woolhiser and Liggett (1967) for sheet flow.
/// </summary>
public static class GroundHydrology
{
    public const int Surfaces = 7;

    /// <summary>The hydrologic soil group, 0..3 for A..D (NEH-630 ch. 7). No soil survey is read yet, so
    /// one group stands for everywhere: B, a moderately fine loam, the commonest in the humid US. An
    /// assumption until a soil layer gives each tile its own.</summary>
    public static int SoilGroup = 1;

    // TR-55 runoff curve numbers by soil group A, B, C, D. Open: "pasture, grassland or range, fair" (2-2c);
    // Impervious: "paved parking lots, roofs, driveways" and "streets and roads, paved, curbs and storm
    // sewers" (2-2a); Gravel and DirtRoad: "streets and roads, gravel / dirt" (2-2a); Lawn: "open space, good
    // condition, grass cover over 75 %" (2-2a); Woods: "woods, good" (2-2c); Water: every drop stays water.
    private static readonly byte[,] Curve =
    {
        { 49, 69, 79, 84 },
        { 98, 98, 98, 98 },
        { 76, 85, 89, 91 },
        { 72, 82, 87, 89 },
        { 39, 61, 74, 80 },
        { 30, 55, 70, 77 },
        { 100, 100, 100, 100 },
    };

    /// <summary>The surface's curve number in this soil group.</summary>
    public static float CurveNumber(GroundSurface s) => Curve[Math.Min((int)s, Surfaces - 1), Math.Clamp(SoilGroup, 0, 3)];

    /// <summary>The potential retention S, mm: 25400 / CN − 254 (TR-55 eq. 2-4 in millimetres).</summary>
    public static float RetentionMm(GroundSurface s)
    {
        float cn = CurveNumber(s);
        return cn >= 100f ? 0f : 25400f / cn - 254f;
    }

    /// <summary>The initial abstraction's share of S (TR-55 eq. 2-2: Ia = 0.2 S): interception, the first
    /// infiltration and the water held in the ground's small hollows, before anything runs.</summary>
    public const float InitialAbstraction = 0.2f;

    /// <summary>
    /// The share of the rain falling now that runs off, when <paramref name="eventMm"/> has fallen since the
    /// ground was last dry: the slope of TR-55's Q = (P − Ia)² / (P − Ia + S), dQ/dP = 1 − S² / (P − Ia + S)²,
    /// nothing until Ia has fallen. A lawn soaks up the first 32 mm, a road all but the first millimetre.
    /// </summary>
    public static float ExcessShare(GroundSurface s, float eventMm)
    {
        if (s == GroundSurface.Water) return 1f;
        float sMm = RetentionMm(s);
        if (sMm <= 0f) return 1f;
        float over = MathF.Max(0f, eventMm) - InitialAbstraction * sMm;
        if (over <= 0f) return 0f;
        float r = sMm / (over + sMm);
        return 1f - r * r;
    }

    /// <summary>The depth run off in all from an event of <paramref name="eventMm"/>, mm (TR-55 eq. 2-3).</summary>
    public static float RunoffMm(GroundSurface s, float eventMm)
    {
        if (s == GroundSurface.Water) return MathF.Max(0f, eventMm);
        float sMm = RetentionMm(s);
        float over = MathF.Max(0f, eventMm) - InitialAbstraction * sMm;
        return over <= 0f ? 0f : over * over / (over + sMm);
    }

    /// <summary>
    /// Manning's n for sheet flow over the surface (TR-55 Table 3-1): smooth surfaces (concrete, asphalt,
    /// gravel, bare soil) 0.011; range, natural 0.13; short-grass prairie 0.15 (a mown lawn); woods with light
    /// underbrush 0.40.
    /// </summary>
    public static float SheetManningN(GroundSurface s) => s switch
    {
        GroundSurface.Impervious or GroundSurface.Gravel or GroundSurface.DirtRoad or GroundSurface.Water => 0.011f,
        GroundSurface.Lawn => 0.15f,
        GroundSurface.Woods => 0.40f,
        _ => 0.13f,
    };

    /// <summary>Whether water runs over it as over pavement (TR-55's paved shallow concentrated flow).</summary>
    public static bool Paved(GroundSurface s) => s == GroundSurface.Impervious;

    /// <summary>Whether it is a road, a drive or a yard: what holds a hollow back on its far side is then an
    /// embankment with a culvert through it, not a sill.</summary>
    public static bool Road(GroundSurface s) => s is GroundSurface.Impervious or GroundSurface.Gravel or GroundSurface.DirtRoad;

    /// <summary>How far sheet flow runs before it gathers into rills, m: 100 ft (NEH-630 ch. 15; TR-55 allowed
    /// 300 ft, revised down).</summary>
    public const float SheetFlowMetres = 30.48f;

    /// <summary>The rain the travel times are reckoned at, mm/h: the game's heavy rain. A catchment's timing
    /// is a property of the ground, read once; the kinematic wave's own dependence on the rain is weak (i^-0.4).</summary>
    public const float ReferenceRainMmPerHour = Rainfall.HeavyRate;

    /// <summary>
    /// The time sheet flow takes to run <paramref name="metres"/> down a slope, s: the kinematic-wave time to
    /// equilibrium, t = (n L / √S)^0.6 / i^0.4, i in m/s (Woolhiser and Liggett 1967; HEC-22 eq. 3-4; TR-55's
    /// eq. 3-3 is the same law with the 2-year rain for i).
    /// </summary>
    public static float SheetSeconds(float metres, float slope, GroundSurface s, float rainMmPerHour = ReferenceRainMmPerHour)
    {
        if (!(metres > 0f)) return 0f;
        float i = MathF.Max(0.5f, rainMmPerHour) / 3.6e6f;
        float a = SheetManningN(s) * metres / MathF.Sqrt(MathF.Max(0.001f, slope));
        return MathF.Pow(a, 0.6f) / MathF.Pow(i, 0.4f);
    }

    /// <summary>The speed of shallow concentrated flow, m/s: TR-55 Figure 3-1's V = 16.1345 √s ft/s unpaved and
    /// 20.3282 √s paved, in metres.</summary>
    public static float ShallowSpeed(float slope, bool paved)
        => (paved ? 6.196f : 4.918f) * MathF.Sqrt(MathF.Max(0.001f, slope));

    /// <summary>The share of a catchment's time of concentration that is its lag: NEH-630 ch. 15, L = 0.6 Tc.
    /// A linear reservoir's lag is its time constant, so this is the Runoff ladder's τ.</summary>
    public const float LagShare = 0.6f;

    /// <summary>The slowest the ground soaks water up once wet, mm/h (NEH-630 ch. 7, TR-55 Appendix A: group A
    /// over 7.6, B 3.8 to 7.6, C 1.3 to 3.8, D under 1.3; the middle of each).</summary>
    public static float SoakMmPerHour => Math.Clamp(SoilGroup, 0, 3) switch { 0 => 9.5f, 1 => 5.7f, 2 => 2.5f, _ => 0.6f };

    /// <summary>What a sealed surface lets through a crack or a joint, mm/h: the puddles' seepage (RoadWater).</summary>
    public const float SealedSeepMmPerHour = 0.3f;

    /// <summary>The water a surface holds on and in its top layer before water poured on it runs on, mm: an
    /// asphalt's texture (0.7, docs/WET_ROADS.md), gravel's voids, litter and thatch soaking up the first
    /// splash. A judgement, for water poured or thrown (a bucket, a hose), not for rain.</summary>
    public static float WettingMm(GroundSurface s) => s switch
    {
        GroundSurface.Impervious => 0.7f,
        GroundSurface.Gravel => 3f,
        GroundSurface.DirtRoad => 2f,
        GroundSurface.Lawn => 5f,
        GroundSurface.Woods => 8f,
        GroundSurface.Water => 0f,
        _ => 4f,
    };

    /// <summary>
    /// The surface a cell is, from the material at the top of whatever covers it: anything built (asphalt,
    /// concrete, brick, roofing, metal, wood decking, glass) is sealed; grass a lawn; foliage the woods; gravel
    /// and water themselves; dirt open ground, or a dirt road when <paramref name="road"/>. The registry's
    /// names (AcousticRegistry); an unknown name is built.
    /// </summary>
    public static GroundSurface OfMaterial(string? material, bool road = false)
    {
        switch (material?.Trim().ToLowerInvariant())
        {
            case "grass": return GroundSurface.Lawn;
            case "foliage": return GroundSurface.Woods;
            case "gravel": return GroundSurface.Gravel;
            case "water": return GroundSurface.Water;
            case "dirt": return road ? GroundSurface.DirtRoad : GroundSurface.Open;
            case null or "": return GroundSurface.Open;
            default: return GroundSurface.Impervious;
        }
    }
}

/// <summary>
/// What drains through one place on a drainage line, as the flow there needs it: the area of each surface
/// upstream (m², <see cref="GroundSurface"/> order) whose water reaches it without passing a pond; how long
/// each surface's water takes to get there on average (s: the mean travel time of its cells, which is the lag
/// of a time-area response, Clark 1945; a road beside a ditch minutes, the woods behind it an hour); the ponds
/// whose overflow reaches it directly; and all the ground draining to it, the ponds' included, whose
/// groundwater comes out into it as base flow.
/// </summary>
public sealed record GroundCatchment(float[] AreaSquareMetres, float[] LagSeconds, int[] Ponds, float BaseSquareMetres)
{
    /// <summary>One lag for every surface, the base flow from the areas themselves: for a place with no ponds above.</summary>
    public GroundCatchment(float[] areas, float lagSeconds)
        : this(areas, Enumerable.Repeat(lagSeconds, GroundHydrology.Surfaces).ToArray(), Array.Empty<int>(), areas.Sum()) { }

    public float TotalSquareMetres
    {
        get { float a = 0f; foreach (float v in AreaSquareMetres) a += v; return a; }
    }

    /// <summary>The lag of one surface's water, s.</summary>
    public float LagOf(int surface) => surface < LagSeconds.Length ? LagSeconds[surface] : LagSeconds.Length > 0 ? LagSeconds[^1] : 0f;

    /// <summary>"a0,...,a6;lag0,...,lag6;p1,p2;base": whole square metres and seconds, the ponds by id. A surface
    /// with no area has its lag left blank.</summary>
    public string Key()
    {
        var sb = new StringBuilder();
        for (int k = 0; k < AreaSquareMetres.Length; k++)
        {
            if (k > 0) sb.Append(',');
            sb.Append(((long)MathF.Round(AreaSquareMetres[k])).ToString(CultureInfo.InvariantCulture));
        }
        sb.Append(';');
        for (int k = 0; k < AreaSquareMetres.Length; k++)
        {
            if (k > 0) sb.Append(',');
            if (AreaSquareMetres[k] >= 0.5f) sb.Append(((int)MathF.Round(LagOf(k))).ToString(CultureInfo.InvariantCulture));
        }
        sb.Append(';');
        for (int k = 0; k < Ponds.Length; k++)
        {
            if (k > 0) sb.Append(',');
            sb.Append(Ponds[k].ToString(CultureInfo.InvariantCulture));
        }
        sb.Append(';').Append(((long)MathF.Round(BaseSquareMetres)).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static bool TryParse(string? key, out GroundCatchment catchment)
    {
        catchment = new GroundCatchment(new float[GroundHydrology.Surfaces], 0f);
        if (string.IsNullOrEmpty(key)) return false;
        var parts = key.Split(';');
        if (parts.Length != 4) return false;
        var areas = new float[GroundHydrology.Surfaces];
        var lags = new float[GroundHydrology.Surfaces];
        var a = parts[0].Split(',');
        var l = parts[1].Split(',');
        for (int k = 0; k < a.Length && k < areas.Length; k++)
            if (!float.TryParse(a[k], NumberStyles.Float, CultureInfo.InvariantCulture, out areas[k])) return false;
        for (int k = 0; k < l.Length && k < lags.Length; k++)
            if (l[k].Length > 0 && !float.TryParse(l[k], NumberStyles.Float, CultureInfo.InvariantCulture, out lags[k])) return false;
        var ponds = new List<int>();
        if (parts[2].Length > 0)
            foreach (var p in parts[2].Split(','))
                if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) ponds.Add(id);
        if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float baseArea)) return false;
        catchment = new GroundCatchment(areas, lags, ponds.ToArray(), MathF.Max(0f, baseArea));
        return true;
    }
}

/// <summary>
/// One map's rain over its ground: how much of the rain each surface is shedding now (the curve number,
/// TR-55, fed continuously), passed through the linear reservoirs of <see cref="Runoff.Rungs"/> one ladder
/// per surface, so a place on a drainage line reads the flow its catchment sends it at its own lag (Nash
/// 1957); the groundwater that keeps a creek running in dry weather; and the overflow of the ponds that reach
/// a drainage line. The server advances it; clients get it whole (<see cref="Save"/>, <see cref="Load"/>).
/// </summary>
public sealed class GroundWater
{
    /// <summary>The client's: the last state the server sent, read by the running water's voices.</summary>
    public static readonly GroundWater Shared = new();

    /// <summary>For the lab: <see cref="Shared"/> stays where it was put, whatever the server sends.</summary>
    public static volatile bool Held;

    /// <summary>How long the ground takes to give back what a storm soaked into it, s: the event depth falls
    /// by e every two days without rain. An assumption: NEH-630 ch. 10 judges a soil's wetness by the last five
    /// days' rain.</summary>
    public const float RecoverySeconds = 2f * 86400f;

    /// <summary>Groundwater: the share of the water soaking in that reaches it (the rest is taken by the plants
    /// and the air), and how long it takes to come out into the channels again, s (a 30-day recession).
    /// Assumptions of the right size for a humid lowland.</summary>
    public const float RechargeShare = 0.3f, GroundwaterSeconds = 30f * 86400f;

    /// <summary>The recharge the world's climate gives the groundwater between the game's storms, mm/h: 80 mm a
    /// year, the size of the base flow of humid lowland catchments in the eastern US (Santhi et al. 2008, J.
    /// Hydrol. 351, 139, recalled). An assumption until the weather has a climate.</summary>
    public const float ClimateRechargeMmPerHour = 80f / 8766f;

    /// <summary>Base flow comes out where a channel has cut down to the water table: catchments over about a
    /// square kilometre carry it, small ones are dry between storms. A ramp from a quarter of that to the whole,
    /// m². An assumption.</summary>
    public const float PerennialSquareMetres = 1e6f, PerennialFromSquareMetres = 2.5e5f;

    private static int Rungs => Runoff.Rungs.Length;

    // Written by one thread (the server's tick, or the client's network thread), read by others (the client's
    // render threads): every float is written and read whole, as Runoff does.
    private readonly float[] _held = new float[GroundHydrology.Surfaces * Runoff.Rungs.Length];
    private float _rain, _eventMm, _groundMm = ClimateRechargeMmPerHour * GroundwaterSeconds / 3600f;
    private bool _settled;
    private volatile Spills _spills = Spills.None;

    private sealed class Spills
    {
        public static readonly Spills None = new(Array.Empty<int>(), Array.Empty<float>());
        public readonly int[] Ids;
        public readonly float[] LitresPerSecond;
        public Spills(int[] ids, float[] flows) { Ids = ids; LitresPerSecond = flows; }
    }

    /// <summary>The rain falling now, mm/h.</summary>
    public float RainMmPerHour => Volatile.Read(ref _rain);

    /// <summary>How much rain the ground has taken since it was last dry, mm (decays when it is dry).</summary>
    public float EventMm => Volatile.Read(ref _eventMm);

    /// <summary>The base flow per square metre of catchment that carries it, mm/h.</summary>
    public float BaseflowMmPerHour => Volatile.Read(ref _groundMm) * 3600f / GroundwaterSeconds;

    /// <summary>The share of the rain falling now that runs off a surface.</summary>
    public float ExcessShare(GroundSurface s) => GroundHydrology.ExcessShare(s, EventMm);

    /// <summary>
    /// Advances the water by <paramref name="dt"/> seconds of <paramref name="rainMmPerHour"/>. The first call
    /// settles it as if it had been raining like this for an hour (or dry for days): a player who arrives in a
    /// downpour finds the ditches running.
    /// </summary>
    public void Step(float rainMmPerHour, float dt)
    {
        float i = float.IsFinite(rainMmPerHour) ? MathF.Max(0f, rainMmPerHour) : 0f;
        if (!_settled) { Settle(i); return; }
        if (!(dt > 0f)) return;
        dt = MathF.Min(dt, 600f);
        Volatile.Write(ref _rain, i);
        float ev = _eventMm + i * dt / 3600f;
        if (i <= 0f) ev *= MathF.Exp(-dt / RecoverySeconds);
        Volatile.Write(ref _eventMm, ev);
        for (int s = 0; s < GroundHydrology.Surfaces; s++)
        {
            float e = i * GroundHydrology.ExcessShare((GroundSurface)s, ev);
            for (int k = 0; k < Rungs; k++)
            {
                int at = s * Rungs + k;
                float held = _held[at];
                held += (e - held) * (1f - MathF.Exp(-dt / Runoff.Rungs[k]));
                Volatile.Write(ref _held[at], held);
            }
        }
        float soaked = i * (1f - GroundHydrology.ExcessShare(GroundSurface.Open, ev));
        float g = _groundMm + (ClimateRechargeMmPerHour + RechargeShare * soaked - _groundMm * 3600f / GroundwaterSeconds) * dt / 3600f;
        Volatile.Write(ref _groundMm, MathF.Max(0f, g));
    }

    /// <summary>A steady state: raining like this for an hour, or dry for days.</summary>
    public void Settle(float rainMmPerHour)
    {
        float i = MathF.Max(0f, rainMmPerHour);
        Volatile.Write(ref _rain, i);
        float ev = i;   // an hour of it
        Volatile.Write(ref _eventMm, ev);
        for (int s = 0; s < GroundHydrology.Surfaces; s++)
        {
            float e = i * GroundHydrology.ExcessShare((GroundSurface)s, ev);
            for (int k = 0; k < Rungs; k++) Volatile.Write(ref _held[s * Rungs + k], e);
        }
        Volatile.Write(ref _groundMm, ClimateRechargeMmPerHour * GroundwaterSeconds / 3600f);
        _settled = true;
    }

    /// <summary>What a surface sheds through a catchment of lag <paramref name="seconds"/>, mm/h (as
    /// Runoff.Through, interpolated in log τ between the rungs either side).</summary>
    public float Through(GroundSurface surface, float seconds)
    {
        int b = Math.Min((int)surface, GroundHydrology.Surfaces - 1) * Rungs;
        var rungs = Runoff.Rungs;
        if (!(seconds > rungs[0])) return Volatile.Read(ref _held[b]);
        for (int k = 1; k < rungs.Length; k++)
        {
            if (seconds > rungs[k]) continue;
            float t = MathF.Log(seconds / rungs[k - 1]) / MathF.Log(rungs[k] / rungs[k - 1]);
            float lo = Volatile.Read(ref _held[b + k - 1]), hi = Volatile.Read(ref _held[b + k]);
            return lo + (hi - lo) * t;
        }
        return Volatile.Read(ref _held[b + rungs.Length - 1]);
    }

    /// <summary>The base flow a catchment of this size carries, mm/h over its whole area.</summary>
    public float BaseflowFor(float squareMetres)
    {
        float ramp = Math.Clamp((squareMetres - PerennialFromSquareMetres) / (PerennialSquareMetres - PerennialFromSquareMetres), 0f, 1f);
        return ramp * BaseflowMmPerHour;
    }

    /// <summary>The rain-fed flow of a catchment now, L/s: each surface's area through its own ladder at the
    /// catchment's lag (1 mm/h on a square metre is 1/3600 L/s), its base flow, and its ponds' overflow.</summary>
    public float FlowLitresPerSecond(GroundCatchment c) => RainFedLitresPerSecond(c) + BaseLitresPerSecond(c.BaseSquareMetres) + SpillLitresPerSecond(c);

    /// <summary>What the rain is bringing a catchment now, L/s: each surface's area through its own ladder at its
    /// own lag (1 mm/h on a square metre is 1/3600 L/s), nothing through a pond.</summary>
    public float RainFedLitresPerSecond(GroundCatchment c)
    {
        float q = 0f;
        var a = c.AreaSquareMetres;
        for (int s = 0; s < a.Length && s < GroundHydrology.Surfaces; s++)
            if (a[s] > 0f) q += a[s] * Through((GroundSurface)s, c.LagOf(s));
        return q / 3600f;
    }

    /// <summary>The groundwater coming out into a channel draining this much ground, L/s.</summary>
    public float BaseLitresPerSecond(float squareMetres) => squareMetres * BaseflowFor(squareMetres) / 3600f;

    /// <summary>What the ponds a catchment names are overflowing into it now, L/s.</summary>
    public float SpillLitresPerSecond(GroundCatchment c)
    {
        float q = 0f;
        if (c.Ponds.Length > 0)
        {
            var sp = _spills;
            foreach (int id in c.Ponds)
            {
                int k = Array.BinarySearch(sp.Ids, id);
                if (k >= 0) q += sp.LitresPerSecond[k];
            }
        }
        return q;
    }

    /// <summary>The ponds overflowing now and how much, L/s (the server's, for the ponds a drainage line's
    /// voice names). Replaces the last list whole, so a reader never sees half of one.</summary>
    public void SetSpills(IReadOnlyCollection<KeyValuePair<int, float>> spills)
    {
        var sorted = spills.Where(kv => kv.Value > 0f && float.IsFinite(kv.Value)).OrderBy(kv => kv.Key).ToArray();
        _spills = new Spills(sorted.Select(kv => kv.Key).ToArray(), sorted.Select(kv => kv.Value).ToArray());
    }

    /// <summary>A pond's overflow now, L/s.</summary>
    public float SpillOf(int pond)
    {
        var sp = _spills;
        int k = Array.BinarySearch(sp.Ids, pond);
        return k >= 0 ? sp.LitresPerSecond[k] : 0f;
    }

    // ── On the wire ─────────────────────────────────────────────────────────────────────────────

    private const float WireVersion = 1f;

    /// <summary>The whole state, for WorldStateUpdate.GroundWater: a version, the rain, the event depth, the
    /// groundwater, each surface's ladder, then the overflowing ponds as id and L/s pairs.</summary>
    public float[] Save()
    {
        var sp = _spills;
        int head = 4, ladder = _held.Length;
        var a = new float[head + ladder + 1 + 2 * sp.Ids.Length];
        a[0] = WireVersion; a[1] = _rain; a[2] = _eventMm; a[3] = _groundMm;
        Array.Copy(_held, 0, a, head, ladder);
        a[head + ladder] = sp.Ids.Length;
        for (int k = 0; k < sp.Ids.Length; k++)
        {
            a[head + ladder + 1 + 2 * k] = sp.Ids[k];
            a[head + ladder + 2 + 2 * k] = sp.LitresPerSecond[k];
        }
        return a;
    }

    /// <summary>The state as a server sent it. Missing or short (an older server, a map without ground):
    /// dry, nothing running.</summary>
    public void Load(float[]? a)
    {
        if (Held) return;
        int head = 4, ladder = _held.Length;
        if (a == null || a.Length < head + ladder + 1 || a[0] != WireVersion)
        {
            Volatile.Write(ref _rain, 0f);
            Volatile.Write(ref _eventMm, 0f);
            for (int k = 0; k < _held.Length; k++) Volatile.Write(ref _held[k], 0f);
            Volatile.Write(ref _groundMm, 0f);
            _spills = Spills.None;
            return;
        }
        Volatile.Write(ref _rain, Sane(a[1]));
        Volatile.Write(ref _eventMm, Sane(a[2]));
        Volatile.Write(ref _groundMm, Sane(a[3]));
        for (int k = 0; k < ladder; k++) Volatile.Write(ref _held[k], Sane(a[head + k]));
        int n = (int)Math.Clamp(a[head + ladder], 0f, (a.Length - head - ladder - 1) / 2);
        var spills = new KeyValuePair<int, float>[n];
        for (int k = 0; k < n; k++)
            spills[k] = new((int)a[head + ladder + 1 + 2 * k], Sane(a[head + ladder + 2 + 2 * k]));
        SetSpills(spills);
        _settled = true;
    }

    private static float Sane(float v) => float.IsFinite(v) ? MathF.Max(0f, v) : 0f;
}
