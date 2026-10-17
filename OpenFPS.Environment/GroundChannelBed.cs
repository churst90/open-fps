namespace OpenFPS.Environment;

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
/// The bed of a drainage line of the ground (docs/RUNNING_WATER.md section 13): how wide and how rough it is,
/// and the flow it was shaped by, which is the run-off of the line's catchment in the reference storm. The
/// drainage reads it to place the lines' voices and to run their water; the running water's sound
/// (GroundChannels) makes its spec from the same numbers.
/// </summary>
public static class GroundChannelBed
{
    /// <summary>The length of line one voice is, m, heard from five places along it.</summary>
    public const float SegmentMetres = 20f;

    /// <summary>The rain a line's bed is shaped by and its level is declared at, as a depth over two hours,
    /// mm: two hours of the game's heavy rain. Its share run off per surface is TR-55's (GroundHydrology).</summary>
    public const float ReferenceStormMm = 2f * Rainfall.HeavyRate;

    /// <summary>The flow a line's bed is shaped by, L/s: its catchment's run-off in the reference storm, spread
    /// over the storm's two hours, on its world's soil.</summary>
    public static float ReferenceFlow(GroundCatchment c, int soilGroup = GroundHydrology.DefaultSoilGroup)
    {
        float q = 0f;
        for (int s = 0; s < c.AreaSquareMetres.Length && s < GroundHydrology.Surfaces; s++)
            q += c.AreaSquareMetres[s] * GroundHydrology.RunoffMm((GroundSurface)s, ReferenceStormMm, soilGroup) / (2f * 3600f);
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
}
