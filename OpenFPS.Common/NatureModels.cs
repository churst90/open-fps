using System.Collections.Generic;
using OpenFPS.Common.Editing;

namespace OpenFPS.Common;

// Water, fire and the wind in leaves, as parts lists: what the thing is, and the synthesiser works the
// sound out from that. No level per mechanism and no equaliser; the one level, SourceLevelDb, is what the
// model was measured to make (AudioLab --nature levels).

/// <summary>
/// One body of water falling into a pool: a jet, a bowl overflowing, a spout. As drops, each is a click
/// that now and then traps a ringing bubble; as a coherent body it drives air down into the pool in a
/// cloud of bubbles of every size, the low gurgle under the patter.
/// </summary>
public sealed record WaterFallSpec
{
    [Tunable("", 0, 0, "What this fall of water is called.")]
    public string Name { get; init; } = "";
    [Tunable("L/s", 0, 200, "How much water this fall carries. More water is more drops and lumps striking.", Label = "flow", Step = 0.01)]
    public required float FlowLitresPerSecond { get; init; }
    [Tunable("m", 0.01, 30, "How far the water falls before it meets the surface, from a jet's apex or a lip. A longer fall strikes harder.", Label = "fall height", Step = 0.05)]
    public required float FallMetres { get; init; }
    [Tunable("", 0, 1, "The share of the flow that arrives as separate drops. The rest arrives as coherent lumps that plunge and gurgle.", Step = 0.05)]
    public float DropShare { get; init; } = 1f;
    /// <summary>Spread about it by <see cref="DropSizeOrder"/>.</summary>
    [Tunable("mm", 0.05, 4, "How far the drops' mean radius stands over the smallest drop, 0.2 mm. Bigger drops click louder and lower.", Label = "mean drop radius", Step = 0.1)]
    public float MeanDropRadiusMm { get; init; } = 1.2f;
    /// <summary>The gamma law's order is set by how corrugated the breakup's ligaments are (Villermaux,
    /// Marmottant and Duplat 2004; Villermaux 2007). Order 1, the exponential, is rain: many breakups
    /// overlapped (Villermaux and Bossa 2009, Nature Physics 5), not one fountain's. Its tail of big drops
    /// was grain in the fountain's hiss: order 4 takes the 8-16 kHz kurtosis over 10 ms windows from 3.76
    /// to 3.52 (recorded fountains 3.45-3.75).</summary>
    [Tunable("", 1, 20, "The order of the gamma law the drop sizes follow: about 4 for a jet or sheet torn up in air, higher for smooth breakup, 1 for rain. A lower order has more big drops.", Label = "drop size order", Step = 1, Source = "Villermaux, Marmottant and Duplat 2004, Phys. Rev. Lett. 92, 074501; Villermaux 2007, Annu. Rev. Fluid Mech. 39")]
    public int DropSizeOrder { get; init; } = 4;
    [Tunable("mm", 0.5, 6, "The largest drop. Above about 4 mm a falling drop breaks up in the air.", Label = "largest drop radius", Step = 0.1)]
    public float MaxDropRadiusMm { get; init; } = 4f;
    [Tunable("mm", 1, 50, "The mean radius of the lumps the coherent water hits the pool as. Each opens a crater like a big drop.", Label = "lump radius", Step = 0.5)]
    public float ChunkRadiusMm { get; init; } = 5f;
    /// <summary>Small drops falling far go first.</summary>
    [Tunable("per m/s", 0, 0.2, "The share of the drops the wind lifts onto the paving, per metre a second of wind over two. Zero for a fall under a canopy or into a deep basin.", Label = "wind drift", Step = 0.005)]
    public float DriftPerMetrePerSecond { get; init; } = 0.03f;
    /// <summary>Each necks and bursts in its own time, so the fluctuation of the sum goes as one over the
    /// square root of the count.</summary>
    [Tunable("", 1, 500, "How many separate jets or strands this fall stands for. More of them make a steadier sum.", Step = 1)]
    public int Streams { get; init; } = 1;
    [Tunable("", 0, 0, "What the water lands on: the pool, or wet rock.", Label = "lands on")]
    public WaterSurface Onto { get; init; } = WaterSurface.Pool;
    /// <summary>Which of <see cref="WaterFeatureSpec.Taps"/>: a map places one emitter per tap.</summary>
    [Tunable("", 0, 31, "Which of the feature's taps this water is heard from, counted from 0.", Step = 1)]
    public int Tap { get; init; }
    /// <summary>Coarse, corrugated fragmentation (a column collapsing at an apex, a sheet tearing off a
    /// lip) gives the broad end of Villermaux's family (Villermaux 2007: order 2-5, lower the more
    /// corrugated). A lump's splash goes as r³, so the few big lumps are a fountain's few loud splashes;
    /// lumps all one size summed to a steady hiss.</summary>
    [Tunable("", 1, 20, "The order of the gamma law the lump sizes follow. Coarse, corrugated breakup is about 2, which gives a few big loud splashes.", Label = "lump size order", Step = 1, Source = "Villermaux 2007, Annu. Rev. Fluid Mech. 39")]
    public int LumpSizeOrder { get; init; } = 2;

    /// <summary>A vertical jet from a nozzle: exit speed √(2 g h) for the rise, flow that speed through
    /// the nozzle, falling from its apex.</summary>
    public static WaterFallSpec Jet(string name, float nozzleMm, float riseMetres, float nozzleAboveWaterMetres,
                                    float dropShare = 0.55f, float meanDropRadiusMm = 1.4f, float chunkRadiusMm = 5f)
    {
        float v = MathF.Sqrt(2f * 9.81f * riseMetres);
        float area = MathF.PI * MathF.Pow(nozzleMm * 0.5e-3f, 2f);
        return new WaterFallSpec
        {
            Name = name,
            FlowLitresPerSecond = v * area * 1000f,
            FallMetres = riseMetres + nozzleAboveWaterMetres,
            DropShare = dropShare,
            MeanDropRadiusMm = meanDropRadiusMm,
            ChunkRadiusMm = chunkRadiusMm,
        };
    }
}

/// <summary>What falling water lands on.</summary>
public enum WaterSurface
{
    /// <summary>Open water: the drop or lump opens a crater, which may trap a bubble that rings.</summary>
    Pool,
    /// <summary>Wet stone: a rock the water strikes and runs over. Nothing is trapped; the water stops
    /// in its own length and splashes flat, and every lump throws a spray off the stone.</summary>
    Rock,
}

/// <summary>One place on a water feature the sound comes from: where some of its water lands. Each is
/// its own voice at its own place on the map, so a feature metres across is heard as metres across.</summary>
public sealed record WaterTapSpec
{
    [Tunable("", 0, 0, "What this place on the feature is called.")]
    public string Name { get; init; } = "";
    [Tunable("m", 0.1, 20, "How big this landing place is. Inside it the sound is flat.", Step = 0.1)]
    public float ExtentMetres { get; init; } = 1.2f;
}

/// <summary>A fountain, a cascade, a weir: everything falling into one pool.</summary>
public sealed record WaterFeatureSpec
{
    [Tunable("", 0, 0, "What this water feature is called.")]
    public string Name { get; init; } = "";
    public required WaterFallSpec[] Falls { get; init; }
    [Tunable("dB", 20, 120, "Overall level at one metre from the pool's edge, measured with --nature levels. Change it only after measuring the model again.", Step = 0.5, Source = "MEASURED with --nature levels")]
    public required float SourceLevelDb { get; init; }
    /// <summary>How far its loudest moments stand over <see cref="SourceLevelDb"/>, dB (the 99.9th
    /// percentile of its 10 ms peaks, --nature levels). Never under the fleet's shared 16.</summary>
    public float PeakHeadroomDb { get; init; } = 16f;
    [Tunable("m", 0.2, 50, "The radius of the pool. The sound comes off the whole surface, so inside it the level is flat.", Label = "pool radius", Step = 0.1)]
    public float ExtentMetres { get; init; } = 2f;
    [Tunable("m", 0.1, 20, "The height the wind that moves the spray is taken at.", Label = "wind height", Step = 0.1)]
    public float WindHeightMetres { get; init; } = 1.5f;
    /// <summary>Where on the feature its water lands, one voice each (<see cref="WaterFallSpec.Tap"/>).
    /// A map places them as "water:&lt;preset&gt;/&lt;feature&gt;/&lt;tap&gt;" emitters; the whole feature as
    /// one "water:&lt;preset&gt;" emitter plays every tap from one point.</summary>
    public WaterTapSpec[] Taps { get; init; } = { new() { Name = "the whole" } };

    /// <summary>
    /// A park fountain, tiered, over rocks (Cody, 2026-10-06: "a little more lifelike, a little bigger,
    /// with maybe some rocks where the water splashes over"): an 11 m square basin with a pedestal in
    /// the middle carrying a bowl, a jet rising out of the bowl and falling back into it, the bowl
    /// overflowing all round its lip onto a ring of boulders heaped round the pedestal's foot, the water
    /// running over the stone and off it into the pool, and twelve small jets arching in from the
    /// kerb, three a side.
    ///
    /// The central jet is a 16 mm nozzle throwing 2 m (6.3 m/s, 1.26 L/s). The bowl is 2.8 m across and
    /// 1.25 m over the water, its lip 11.2 m round, so 0.11 L/s a metre: too thin to stay a sheet, it
    /// fingers into strands about 3 cm apart (the Rayleigh-Taylor wavelength of a liquid rim, 2π√3
    /// capillary lengths; water's capillary length is 2.7 mm) and falls 0.75 m onto the rocks as strands
    /// and beads. Off the rocks it leaves over their outer edges as short sheets and strands, 0.45 m down
    /// into the pool. The rim jets are 8 mm, rising a metre: 0.22 L/s each.
    ///
    /// It is heard from five places (<see cref="Taps"/>): the bowl, and the four sides of the rock heap,
    /// where each side's overflow strikes the stone and runs off it and that side's three rim jets come
    /// down. The map (tools/gen_city.py, Elm Park) puts an emitter at each.
    /// </summary>
    public static WaterFeatureSpec ParkFountain => new()
    {
        Name = "Park fountain, tiered, over rocks, with rim jets",
        Falls = ParkFountainFalls(),
        Taps = new[]
        {
            new WaterTapSpec { Name = "the bowl", ExtentMetres = 1.4f },
            new WaterTapSpec { Name = "north side of the rocks", ExtentMetres = 1.5f },
            new WaterTapSpec { Name = "east side of the rocks", ExtentMetres = 1.5f },
            new WaterTapSpec { Name = "south side of the rocks", ExtentMetres = 1.5f },
            new WaterTapSpec { Name = "west side of the rocks", ExtentMetres = 1.5f },
        },
        // MEASURED with `--nature levels water sec=60`, 2026-10-06 (texture round 1: the bigger fountain
        // over rocks, every tap at one point, the field's mean wind): Leq 74.5 dB, 74.7 dB(A) at a
        // metre; its 10 ms peaks' 99.9th percentile 17.8 dB over that. (The round-3 fountain, 2.6 L/s
        // into one basin, was 71.9 dB; this one moves 3.9 L/s.)
        SourceLevelDb = 75f,
        PeakHeadroomDb = 22f,
        ExtentMetres = 5.5f,
        WindHeightMetres = 1.5f,
    };

    /// <summary>
    /// The coherent water arrives in the sizes its breakup gives it. A jet necks and pinches into slugs
    /// about 1.9 jet diameters across (Rayleigh-Plateau; Rayleigh 1878), so an 8 mm rim jet comes down
    /// as lumps of about 7.5 mm radius, a hundred-odd a second a jet. The central jet's column spreads
    /// at its apex and falls back in fragments of every size about a centimetre. The overflow's sheet,
    /// 30 µm thick, fingers into strands a millimetre thick that bead into drops of about a millimetre,
    /// so it reaches the rocks nearly all as drops. On the stone it gathers into rivulets that leave the
    /// rocks' edges a few to a side, each pinching into slugs of about 5 mm.
    /// </summary>
    private static WaterFallSpec[] ParkFountainFalls()
    {
        var falls = new List<WaterFallSpec>
        {
            WaterFallSpec.Jet("central jet into the bowl", 16f, 2.0f, 0.05f, dropShare: 0.4f, meanDropRadiusMm: 2.0f, chunkRadiusMm: 7f)
                with { Tap = 0 },
        };
        float overflow = falls[0].FlowLitresPerSecond / 4f;
        var rim = WaterFallSpec.Jet("", 8f, 1.0f, 0.15f, dropShare: 0.2f, meanDropRadiusMm: 1.2f, chunkRadiusMm: 6f);
        string[] sides = { "north", "east", "south", "west" };
        for (int q = 0; q < 4; q++)
        {
            falls.Add(new WaterFallSpec
            {
                Name = $"bowl overflow onto the {sides[q]} rocks",
                FlowLitresPerSecond = overflow, FallMetres = 0.75f,
                DropShare = 0.9f, MeanDropRadiusMm = 1.2f, ChunkRadiusMm = 3f,
                DriftPerMetrePerSecond = 0.01f,
                // 2.8 m of lip a side, strands about 3 cm apart.
                Streams = 90,
                Onto = WaterSurface.Rock,
                Tap = 1 + q,
            });
            falls.Add(new WaterFallSpec
            {
                Name = $"off the {sides[q]} rocks into the pool",
                FlowLitresPerSecond = overflow, FallMetres = 0.45f,
                DropShare = 0.15f, MeanDropRadiusMm = 1.5f, ChunkRadiusMm = 5f,
                DriftPerMetrePerSecond = 0f,
                Streams = 8,
                Tap = 1 + q,
            });
            falls.Add(rim with
            {
                Name = $"{sides[q]} rim jets",
                FlowLitresPerSecond = 3f * rim.FlowLitresPerSecond,
                Streams = 3,
                Tap = 1 + q,
            });
        }
        return falls.ToArray();
    }

    public static IReadOnlyDictionary<string, Func<WaterFeatureSpec>> Presets { get; } =
        new Dictionary<string, Func<WaterFeatureSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["park_fountain"] = () => ParkFountain,
        };

    /// <summary>A preset by name, through the <see cref="ModelLibrary"/> so a map's own wins.</summary>
    public static WaterFeatureSpec ByName(string key) => ModelLibrary.Water(key);
}

/// <summary>What a plant's leaves are, which decides which of the two sounds of wind in it you hear.</summary>
public enum LeafKind
{
    /// <summary>Flat leaves on stalks: they flutter and strike each other, and the strikes are the rustle.</summary>
    Broadleaf,
    /// <summary>Needles: they do not flutter, and the sound is the air shedding vortices off them, the sough.</summary>
    Needle,
}

/// <summary>
/// A tree or a hedge the wind blows through. Leaves on stalks flutter and touch, each touch a click in
/// the low kilohertz, thousands a second the rustle; number and force both grow with the wind, so the
/// rustle grows much faster than it. Air past twigs and needles sheds vortices at St = f d / U ≈ 0.2: a
/// low whoosh off twigs, the higher sigh of a pine wood off needles. A swinging branch moves its leaves
/// faster through the air, which is why a rustle comes in surges.
/// </summary>
public sealed record FoliageSpec
{
    [Tunable("", 0, 0, "What this plant is called.")]
    public string Name { get; init; } = "";
    [Tunable("", 0, 0, "Broad leaves on stalks flutter and strike each other, the rustle. Needles do not; the sound is the air shedding vortices off them, the sough.")]
    public LeafKind Leaves { get; init; } = LeafKind.Broadleaf;
    [Tunable("m", 0.2, 20, "The crown's radius.", Step = 0.1)]
    public required float CrownRadiusMetres { get; init; }
    [Tunable("m", 0.3, 60, "The height of the middle of the crown, where the wind it feels is taken.", Step = 0.5)]
    public required float CrownHeightMetres { get; init; }
    [Tunable("", 0.1, 12, "Leaf area over the ground the crown covers: 3 to 6 for a tree in leaf. More leaves, more strikes.", Step = 0.1)]
    public float LeafAreaIndex { get; init; } = 4f;
    [Tunable("cm²", 0.05, 1000, "One leaf's area. A lime or maple leaf is 50 to 100, a birch leaf 15.", Label = "leaf area", Step = 0.5)]
    public float LeafAreaCm2 { get; init; } = 40f;
    [Tunable("mm", 0.3, 50, "The typical thickness of what the air sheds vortices off: twigs for a broadleaf, needles for a conifer. Thinner sheds at a higher pitch.", Label = "twig or needle thickness", Step = 0.1)]
    public float ShedDiameterMm { get; init; } = 5f;
    [Tunable("m/s", 0, 5, "The wind speed below which the leaves do not touch.", Label = "still wind speed", Step = 0.1)]
    public float StillSpeed { get; init; } = 1f;
    [Tunable("Hz", 0.05, 5, "How fast the main branches swing. A big tree's crown sways at 0.3 to 0.6 Hz, its outer branches at a few hertz. The rustle surges with it.", Label = "sway", Step = 0.05)]
    public float SwayHz { get; init; } = 0.5f;
    /// <summary>Vogel's exponent V: drag goes as U^(2+V) as leaves fold and streamline, measured about
    /// −0.5 to −1.2 for broad leaves (Vogel 1989, J. Exp. Bot. 40) and whole plants (de Langre 2008,
    /// Annu. Rev. Fluid Mech. 40). The shedding's power goes as U^(5+2V).</summary>
    [Tunable("", -1.5, 0, "How the crown folds and streamlines as the wind rises: its drag goes as the wind speed to the power 2 plus this. Measured from -0.5 to -1.2. More negative, the sound grows more slowly with the wind.", Label = "Vogel exponent", Step = 0.05, Source = "Vogel 1989, J. Exp. Bot. 40; de Langre 2008, Annu. Rev. Fluid Mech. 40")]
    public float VogelExponent { get; init; } = -0.7f;
    [Tunable("dB", 10, 100, "Level at one metre from the crown with the wind at the field's mean, measured with --nature levels. Change it only after measuring the model again.", Step = 0.5, Source = "MEASURED with --nature levels")]
    public required float SourceLevelDb { get; init; }
    /// <summary>How far its loudest moments stand over <see cref="SourceLevelDb"/>, dB (the 99.9th
    /// percentile of its 10 ms peaks, --nature levels). Never under the fleet's shared 16.</summary>
    public float PeakHeadroomDb { get; init; } = 16f;
    [Tunable("m", 0.2, 30, "How big the source is: the crown. Inside it the level is flat.", Step = 0.1)]
    public float ExtentMetres { get; init; } = 3f;

    /// <summary>A park tree in leaf: a lime or a maple, twelve metres tall with a crown eight across.</summary>
    public static FoliageSpec ParkTree => new()
    {
        Name = "Broadleaf park tree in leaf",
        Leaves = LeafKind.Broadleaf,
        CrownRadiusMetres = 4f,
        CrownHeightMetres = 7f,
        LeafAreaIndex = 4.5f,
        LeafAreaCm2 = 60f,
        ShedDiameterMm = 5f,
        StillSpeed = 1f,
        SwayHz = 0.45f,
        // Big soft leaves on long stalks fold further: the strong end of Vogel's range. At −0.9 the crown
        // grows 9.5 dB from 3 to 6 m/s (32 dB a decade, between Fégeant's oak at 30 and birch at 36) and
        // its gusts swing it 4.0 dB; at −0.7 Cody heard the swings as too obvious (docs/COMMON_NOTES.md,
        // The park tree).
        VogelExponent = -0.9f,
        // MEASURED with `--nature levels park_tree`, texture round 1 (2026-10-06), five minutes of the
        // field: Leq 47.4 dB, 45.7 dB(A), 10 ms peaks' 99.9th percentile 21.1 dB over. Measure over ten
        // minutes, not one: a minute of gusts read 2.3 dB high.
        SourceLevelDb = 48f,
        PeakHeadroomDb = 22f,
        ExtentMetres = 4f,
    };

    /// <summary>A pine: needles, so it sighs rather than rustles.</summary>
    public static FoliageSpec Pine => new()
    {
        Name = "Scots pine",
        Leaves = LeafKind.Needle,
        CrownRadiusMetres = 3f,
        CrownHeightMetres = 9f,
        LeafAreaIndex = 3f,
        LeafAreaCm2 = 0.3f,
        ShedDiameterMm = 1.5f,
        StillSpeed = 0.5f,
        SwayHz = 0.35f,
        // MEASURED with `--nature levels pine sec=600`, 2026-10-04: Leq 44.8 dB over ten minutes;
        // 44.4 dB on 2026-10-05 with the field's turbulence at 0.25.
        SourceLevelDb = 45f,
        PeakHeadroomDb = 20f,
        ExtentMetres = 3f,
    };

    public static IReadOnlyDictionary<string, Func<FoliageSpec>> Presets { get; } =
        new Dictionary<string, Func<FoliageSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["park_tree"] = () => ParkTree,
            ["pine"] = () => Pine,
        };

    public static FoliageSpec ByName(string key) => ModelLibrary.Foliage(key);
}
