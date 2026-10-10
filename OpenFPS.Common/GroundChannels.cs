using System.Collections.Concurrent;
using System.Globalization;

namespace OpenFPS.Common;

/// <summary>What a drainage line of the ground is, where it is heard: what its bed is made of.</summary>
public enum GroundChannelKind
{
    /// <summary>A rivulet down open ground, a lawn or the woods: litter, roots and tufts in it.</summary>
    Rill,
    /// <summary>A ditch beside a road or a drive: a grassed bed.</summary>
    Ditch,
    /// <summary>A creek: a bed of its own, sand and stones, where enough ground drains through.</summary>
    Creek,
    /// <summary>Water running along a paved surface with no kerb: grit and joints.</summary>
    Runnel,
}

/// <summary>
/// The running water of a drainage line of the ground (docs/RUNNING_WATER.md section 13): the creek, the
/// ditch, the rivulet that the ground's shape and the rain make, heard through the running-water model
/// (RunningWaterSynth) with its flow from the map's state (GroundWater). The server places one wherever the
/// line could carry enough to be heard (OpenFPS.Server.Water.DrainageNetwork); everything about it is in its
/// sound id, "flow:ground/KIND/WIDTH_CM/SLOPE_PERMILLE/LENGTH_M/REFERENCE_ML_PER_S/CATCHMENT", so a client
/// makes the same spec.
/// </summary>
public static class GroundChannels
{
    public const string Prefix = "ground/";

    /// <summary>Under this a drainage line is dry, L/s: a film this thin beads and soaks in along the way rather
    /// than running as a stream, and makes no sound. A judgement (5 mL/s is a dribble from a jug).</summary>
    public const float DryLitresPerSecond = 0.002f;

    /// <summary>The length of line one voice is, m, heard from five places along it.</summary>
    public const float SegmentMetres = 20f;

    /// <summary>The rain a line's bed is shaped by and its level is declared at, as a depth over two hours,
    /// mm: two hours of the game's heavy rain. Its share run off per surface is TR-55's (GroundHydrology).</summary>
    public const float ReferenceStormMm = 2f * Rainfall.HeavyRate;

    /// <summary>The flow a line's bed is shaped by, L/s: its catchment's run-off in the reference storm, spread
    /// over the storm's two hours.</summary>
    public static float ReferenceFlow(GroundCatchment c)
    {
        float q = 0f;
        for (int s = 0; s < c.AreaSquareMetres.Length && s < GroundHydrology.Surfaces; s++)
            q += c.AreaSquareMetres[s] * GroundHydrology.RunoffMm((GroundSurface)s, ReferenceStormMm) / (2f * 3600f);
        return q;
    }

    /// <summary>
    /// How wide the bed is, m: downstream hydraulic geometry, w = a Q^0.5 (Leopold and Maddock 1953, USGS PP
    /// 252; a ≈ 3 with Q in m³/s, recalled), held to what each kind can be: a rill a hand to half a metre, a
    /// ditch's bed 0.3 to 1.5 m, a creek 0.8 to 8 m. A runnel's width is its spread (Izzard) and not used.
    /// </summary>
    public static float WidthFor(GroundChannelKind kind, float referenceLitresPerSecond)
    {
        float w = 3f * MathF.Sqrt(MathF.Max(0f, referenceLitresPerSecond) * 1e-3f);
        return kind switch
        {
            GroundChannelKind.Creek => Math.Clamp(w, 0.8f, 8f),
            GroundChannelKind.Ditch => Math.Clamp(w, 0.3f, 1.5f),
            GroundChannelKind.Runnel => 0.3f,
            _ => Math.Clamp(w, 0.1f, 0.5f),
        };
    }

    /// <summary>Manning's n of the bed (Chow 1959, Table 5-6): a natural stream with stones and weeds 0.035 to
    /// 0.05; an excavated channel with short grass 0.022 to 0.033; a rill full of litter as the stream; asphalt
    /// 0.013 to 0.016.</summary>
    public static float ManningN(GroundChannelKind kind) => kind switch
    {
        GroundChannelKind.Creek => 0.045f,
        GroundChannelKind.Ditch => 0.033f,
        GroundChannelKind.Runnel => 0.016f,
        _ => 0.045f,
    };

    /// <summary>What breaks the surface. The creek's are the creek preset's (cobbles, fitted 2026-10-06); the
    /// gutter's grit for a runnel; for a ditch grass tufts and for a rill twigs, roots and leaf dams, a judgement
    /// of their size and spacing.</summary>
    public static FlowObstacles ObstaclesOf(GroundChannelKind kind) => kind switch
    {
        GroundChannelKind.Creek => new FlowObstacles { PerMetre = 2.5f, MedianDropMetres = 0.06f, DropSpread = 0.6f, WidthMetres = 0.15f },
        GroundChannelKind.Ditch => new FlowObstacles { PerMetre = 6f, MedianDropMetres = 0.015f, DropSpread = 0.8f, WidthMetres = 0.06f },
        GroundChannelKind.Runnel => new FlowObstacles { PerMetre = 10f, MedianDropMetres = 0.008f, DropSpread = 1.0f, WidthMetres = 0.05f },
        _ => new FlowObstacles { PerMetre = 5f, MedianDropMetres = 0.02f, DropSpread = 0.8f, WidthMetres = 0.05f },
    };

    /// <summary>
    /// What breaks the surface of a natural bed (a creek, a rill) is sized by the bed: a channel carrying more
    /// water has bigger stones, roots and snags in it, the median standing this share of the depth its reference
    /// flow runs at (gravel and cobble beds run at a relative submergence of a few stone heights at bankfull,
    /// Bathurst 1985, recalled; a judgement within it), spaced further apart in step. Never smaller than the
    /// kind's own. A ditch's grass and a runnel's grit do not grow with the water.
    /// </summary>
    public const float ObstacleShareOfDepth = 0.4f;

    /// <summary>
    /// The level at a metre the voice is declared at, dB, at <paramref name="litresPerSecond"/> (its reference flow,
    /// in the bed made for it) and its slope: measured with AudioLab --ground-water levels (2026-10-10,
    /// docs/RUNNING_WATER.md 13.6) over flows and slopes and interpolated in log flow and log slope, the loudest at
    /// or under this flow (deep water drowns its obstacles, and a voice is declared by what it can do). Only the
    /// mixer's ranking reads it, and the voice's headroom is over it: what is heard is the model at its flow.
    /// </summary>
    public static float LevelDb(GroundChannelKind kind, float litresPerSecond, float slope)
    {
        var (flows, slopes, levels) = Levels(kind);
        float ls = MathF.Log(Math.Clamp(slope, slopes[0], slopes[^1]));
        int sj = 0;
        while (sj < slopes.Length - 2 && ls > MathF.Log(slopes[sj + 1])) sj++;
        float ts = Math.Clamp((ls - MathF.Log(slopes[sj])) / (MathF.Log(slopes[sj + 1]) - MathF.Log(slopes[sj])), 0f, 1f);
        float At(int fi) => levels[fi, sj] + (levels[fi, sj + 1] - levels[fi, sj]) * ts;
        float q = Math.Clamp(litresPerSecond, flows[0], flows[^1]);
        float best = At(0);
        for (int k = 1; k < flows.Length; k++)
        {
            if (q >= flows[k]) { best = MathF.Max(best, At(k)); continue; }
            float t = (MathF.Log(q) - MathF.Log(flows[k - 1])) / (MathF.Log(flows[k]) - MathF.Log(flows[k - 1]));
            best = MathF.Max(best, At(k - 1) + (At(k) - At(k - 1)) * t);
            break;
        }
        return best;
    }

    /// <summary>How far a line's loudest moments can stand over its declared level, dB: the running water's own 18,
    /// and twelve more because its flow is not its reference flow (a flood carries more, and a shallower stage can
    /// break the surface more) and the voice must never be squared off.</summary>
    public const float HeadroomDb = 30f;

    // MEASURED with `--ground-water levels`: see LevelDb. Leq at a metre, dB, each flow in its own bed, by slope.
    private static readonly float[] TableFlows = { 0.05f, 0.3f, 2f, 15f, 100f, 600f };
    private static readonly float[] TableSlopes = { 0.003f, 0.01f, 0.03f, 0.08f };

    private static (float[] Flows, float[] Slopes, float[,] Levels) Levels(GroundChannelKind kind) => kind switch
    {
        GroundChannelKind.Creek => (TableFlows, TableSlopes, CreekLevels),
        GroundChannelKind.Ditch => (TableFlows, TableSlopes, DitchLevels),
        GroundChannelKind.Runnel => (TableFlows, TableSlopes, RunnelLevels),
        _ => (TableFlows, TableSlopes, RillLevels),
    };

    // Rows are TableFlows, columns TableSlopes. MEASURED 2026-10-10 (20 s at each, seed 7); 20 marks a bed so deep
    // for its stones that nothing breaks the surface (silent: the envelope in LevelDb steps over it).
    private static readonly float[,] RillLevels = { { 54, 50, 47, 45 }, { 59, 57, 56, 55 }, { 68, 64, 62, 64 }, { 75, 69, 65, 68 }, { 81, 78, 76, 81 }, { 86, 84, 20, 82 } };
    private static readonly float[,] DitchLevels = { { 44, 41, 39, 39 }, { 54, 51, 49, 49 }, { 60, 59, 61, 62 }, { 55, 55, 61, 73 }, { 20, 20, 64, 82 }, { 20, 20, 20, 90 } };
    private static readonly float[,] RunnelLevels = { { 47, 46, 46, 48 }, { 51, 51, 54, 56 }, { 56, 58, 63, 65 }, { 55, 59, 71, 75 }, { 20, 61, 80, 86 }, { 20, 60, 88, 94 } };
    private static readonly float[,] CreekLevels = { { 40, 39, 37, 36 }, { 55, 51, 48, 45 }, { 64, 60, 57, 55 }, { 72, 70, 69, 70 }, { 79, 75, 75, 82 }, { 82, 79, 80, 87 } };

    /// <summary>The sound id of a line's voice.</summary>
    public static string Key(GroundChannelKind kind, float widthMetres, float slope, float lengthMetres, float referenceLitresPerSecond, GroundCatchment catchment)
        => string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}{kind.ToString().ToLowerInvariant()}/{(int)MathF.Round(widthMetres * 100f)}/{(int)MathF.Round(Math.Clamp(slope, 0f, 1f) * 1000f)}/{(int)MathF.Round(lengthMetres)}/{(long)MathF.Round(referenceLitresPerSecond * 1000f)}/{catchment.Key()}");

    private static readonly ConcurrentDictionary<string, RunningWaterSpec?> _parsed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The spec a line's key describes; false for anything else.</summary>
    public static bool TryParse(string? key, out RunningWaterSpec spec)
    {
        spec = null!;
        if (key == null || !key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var made = _parsed.GetOrAdd(key, static k => Parse(k));
        if (made == null) return false;
        spec = made;
        return true;
    }

    private static RunningWaterSpec? Parse(string key)
    {
        var parts = key[Prefix.Length..].Split('/', 6);
        if (parts.Length != 6) return null;
        if (!Enum.TryParse<GroundChannelKind>(parts[0], ignoreCase: true, out var kind)) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int widthCm)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int permille)
            || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int length)
            || !long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long refMl)) return null;
        if (!GroundCatchment.TryParse(parts[5], out var catchment)) return null;
        return SpecFor(kind, widthCm / 100f, permille / 1000f, length, refMl / 1000f, catchment);
    }

    /// <summary>A line's running water: its bed, its obstacles, its level at its reference flow, and its
    /// catchment, through which it takes its flow from the map's state.</summary>
    public static RunningWaterSpec SpecFor(GroundChannelKind kind, float widthMetres, float slope, float lengthMetres,
                                           float referenceLitresPerSecond, GroundCatchment catchment)
    {
        float s = Math.Clamp(slope, 0.001f, 0.5f);
        var bed = new RunningWaterSpec
        {
            Channel = kind == GroundChannelKind.Runnel ? FlowChannel.KerbGutter : FlowChannel.Stream,
            WidthMetres = Math.Clamp(widthMetres, 0.05f, 100f), Slope = s, CrossSlope = 0.02f, ManningN = ManningN(kind), SourceLevelDb = 0f,
        };
        var obstacles = ObstaclesOf(kind);
        if (kind is GroundChannelKind.Creek or GroundChannelKind.Rill)
        {
            float depth = Hydraulics.Of(bed, MathF.Max(0.001f, referenceLitresPerSecond)).DepthMetres;
            float drop = MathF.Max(obstacles.MedianDropMetres, ObstacleShareOfDepth * depth);
            float grow = drop / obstacles.MedianDropMetres;
            obstacles = obstacles with
            {
                MedianDropMetres = drop,
                PerMetre = obstacles.PerMetre / grow,
                WidthMetres = MathF.Min(obstacles.WidthMetres * grow, 0.5f * bed.WidthMetres),
            };
        }
        return bed with
        {
            Name = kind switch
            {
                GroundChannelKind.Creek => "Creek",
                GroundChannelKind.Ditch => "Ditch",
                GroundChannelKind.Runnel => "Water running over paving",
                _ => "Rivulet",
            },
            LengthMetres = Math.Clamp(lengthMetres, 0.5f, 500f),
            Obstacles = obstacles,
            // The fields the dry test reads: fed by rain over this much ground, no flow of its own.
            CatchmentSquareMetres = MathF.Max(1f, catchment.TotalSquareMetres),
            Ground = catchment,
            GroundReferenceLitresPerSecond = MathF.Max(0.001f, referenceLitresPerSecond),
            SourceLevelDb = LevelDb(kind, referenceLitresPerSecond, s),
            PeakHeadroomDb = HeadroomDb,
            ExtentMetres = 2f,
            Places = 5,
            Layout = FlowLayout.Line,
        };
    }
}
