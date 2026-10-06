using System;
using System.Collections.Generic;

namespace OpenFPS.Common;

// Water, fire and the wind in leaves, as parts lists. Like a SmallMachineSpec these say what the
// thing IS — how much water falls how far, how big the fire is and how wet its wood, how big the
// tree is and what its leaves are — and the synthesiser works the sound out from that. Nothing here
// is a level per mechanism or an equaliser setting; the one number that is a level, SourceLevelDb,
// is what the model was MEASURED to make (the lab's --nature levels), so the mixer can place it.

/// <summary>
/// One body of water falling into a pool: a jet coming down, a bowl overflowing, a spout.
///
/// Water arrives at a surface in two ways and they sound different. As DROPS — a jet that has broken
/// up at its top, a thin curtain that has torn into strands and beads — each drop is a click as it
/// strikes, and now and then it traps a bubble that rings. As a COHERENT body — the collapsing
/// column of a jet, a thick sheet, a spout — it drives a line of air down into the pool and makes a
/// cloud of bubbles of every size at once, which is the low gurgle under the patter.
/// </summary>
public sealed record WaterFallSpec
{
    public string Name { get; init; } = "";
    /// <summary>How much water, litres a second.</summary>
    public required float FlowLitresPerSecond { get; init; }
    /// <summary>How far it falls before it meets the surface, m: from a jet's apex, or a lip.</summary>
    public required float FallMetres { get; init; }
    /// <summary>The share of the flow that arrives as separate drops; the rest arrives coherent.</summary>
    public float DropShare { get; init; } = 1f;
    /// <summary>The mean radius of the drops, mm. The radii spread exponentially about it, as a sheet
    /// or a jet breaking up gives (Marshall and Palmer's raindrops are the same law).</summary>
    public float MeanDropRadiusMm { get; init; } = 1.2f;
    /// <summary>The largest drop, mm. Above about 4 mm a falling drop breaks up in the air.</summary>
    public float MaxDropRadiusMm { get; init; } = 4f;
    /// <summary>The size the coherent water arrives in, mm: the lumps a collapsing column or a
    /// wavering sheet hits the pool as. Each strikes like a big drop and opens a crater.</summary>
    public float ChunkRadiusMm { get; init; } = 5f;
    /// <summary>The share of the drops the wind can lift onto the paving round the pool, per metre a
    /// second of wind over two. Small drops falling far go first. Zero for a fall under a canopy or
    /// into a deep basin.</summary>
    public float DriftPerMetrePerSecond { get; init; } = 0.03f;
    /// <summary>How many separate jets or strands this fall stands for. Each one necks and bursts
    /// in its own time, so the bunching of its drops and the wandering of where it breaks up are
    /// independent from one to the next, and the more of them there are the steadier their sum: the
    /// fluctuation of the whole goes down as one over the square root of the count.</summary>
    public int Streams { get; init; } = 1;

    /// <summary>A vertical jet from a nozzle, worked out from the nozzle and how high the water
    /// goes: the exit speed is what lifts it there, √(2 g h), and the flow is that speed through the
    /// nozzle. It falls from its apex, the rise plus how far the nozzle stands above the water.</summary>
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

/// <summary>A fountain, a cascade, a weir: everything falling into one pool.</summary>
public sealed record WaterFeatureSpec
{
    public string Name { get; init; } = "";
    public required WaterFallSpec[] Falls { get; init; }
    /// <summary>Overall level at one metre from the edge of the pool, dB. MEASURED with
    /// <c>--nature levels</c>, not chosen.</summary>
    public required float SourceLevelDb { get; init; }
    /// <summary>How far its loudest moments stand over <see cref="SourceLevelDb"/>, dB: the room its
    /// voice renders with (the 99.9th percentile of its 10 ms peaks, measured with --nature levels).
    /// Never under the fleet's shared 16.</summary>
    public float PeakHeadroomDb { get; init; } = 16f;
    /// <summary>The radius of the pool, m: the sound comes off the whole surface, so inside it the
    /// level is flat.</summary>
    public float ExtentMetres { get; init; } = 2f;
    /// <summary>The height the wind that moves the spray is taken at, m.</summary>
    public float WindHeightMetres { get; init; } = 1.5f;

    /// <summary>
    /// A park fountain of the ordinary kind: a round basin with a pedestal in the middle carrying a
    /// bowl, a jet rising out of the bowl and falling back into it, the bowl overflowing all round
    /// its lip into the basin, and a ring of small jets arching in from the basin's rim.
    ///
    /// The central jet is a 14 mm nozzle throwing 1.6 m (5.6 m/s, 0.86 L/s); the bowl is 2.4 m across
    /// and 1.1 m over the basin, so its 7.5 m of lip carries 0.11 L/s a metre — too thin to stay a
    /// sheet for long, so most of it reaches the basin as a fringe of strands and beads. Eight rim
    /// jets of 8 mm arch 0.9 m up and come down inside the basin.
    /// </summary>
    public static WaterFeatureSpec ParkFountain => new()
    {
        Name = "Park fountain, tiered, with rim jets",
        Falls = new[]
        {
            WaterFallSpec.Jet("central jet into the bowl", 14f, 1.6f, 0.05f, dropShare: 0.55f, meanDropRadiusMm: 1.5f, chunkRadiusMm: 6f),
            new WaterFallSpec
            {
                Name = "bowl overflow",
                FlowLitresPerSecond = 0.86f, FallMetres = 1.1f,
                DropShare = 0.8f, MeanDropRadiusMm = 1.8f, ChunkRadiusMm = 3f,
                DriftPerMetrePerSecond = 0.01f,
                // A thin sheet falling off a rim fingers into strands about 2π√3 capillary lengths
                // apart (the Rayleigh-Taylor wavelength of a liquid rim; water's capillary length is
                // 2.7 mm, so about 3 cm): 7.5 m of lip is some 250 strands.
                Streams = 250,
            },
            WaterFallSpec.Jet("rim jets", 8f, 0.9f, 0.15f, dropShare: 0.35f, meanDropRadiusMm: 1.2f, chunkRadiusMm: 4f) with
            {
                // Eight of them.
                FlowLitresPerSecond = 8f * WaterFallSpec.Jet("", 8f, 0.9f, 0.15f).FlowLitresPerSecond,
                Streams = 8,
            },
        },
        // MEASURED with `--nature levels water`, 2026-10-04: Leq 71.9 dB, 71.4 dB(A), at a metre with
        // the field's mean wind; its 10 ms peaks' 99.9th percentile 21.2 dB over that. Re-measured
        // 2026-10-05 after the grain was taken out (every impact rendered, lumps cushioned): Leq
        // 72.3 dB, 72.3 dB(A), peaks 19.1 dB over, so the 22 dB of room is now to spare.
        SourceLevelDb = 72f,
        PeakHeadroomDb = 22f,
        ExtentMetres = 3f,
        WindHeightMetres = 1.5f,
    };

    public static IReadOnlyDictionary<string, Func<WaterFeatureSpec>> Presets { get; } =
        new Dictionary<string, Func<WaterFeatureSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["park_fountain"] = () => ParkFountain,
        };

    /// <summary>A preset by name, through the <see cref="ModelLibrary"/> so a map's own wins.</summary>
    public static WaterFeatureSpec ByName(string key) => ModelLibrary.Water(key);
}

/// <summary>
/// A wood fire: a hearth of burning logs.
///
/// What it is made of decides what it sounds like. The FLAMES are a column of burning gas that
/// rises, necks and breaks off as a puff at a rate set only by its width (about 1.5 / √D Hz; a
/// metre-wide fire puffs twice a second), and their heat release is unsteady, which is a monopole —
/// the low, fluttering roar. The WOOD is wet inside: moisture and resin trapped in its cells boil,
/// the pressure builds until a cell wall gives, and the pocket bursts — the crackle, a few a second
/// to dozens, of every size, the big ones throwing an ember. Steam finding a crack in a log end hisses
/// and sometimes whistles. And every minute or two a burnt log gives way and the fire settles: a
/// thud, a rattle of charcoal, and a flare.
/// </summary>
public sealed record FireSpec
{
    public string Name { get; init; } = "";
    /// <summary>The width of the burning bed, m. Sets the puffing rate and the roar's pitch.</summary>
    public required float BaseDiameterMetres { get; init; }
    /// <summary>How hard it is burning, kW. A garden fire pit with a full load of logs is 50-150.</summary>
    public required float HeatReleaseKw { get; init; }
    /// <summary>Moisture in the wood, a fraction of its dry mass. Seasoned firewood is 0.15-0.20,
    /// green wood 0.4 and over. More water is more crackle and more hiss.</summary>
    public float Moisture { get; init; } = 0.18f;
    /// <summary>How resinous the wood is, 0 (a dense hardwood: oak, ash) to 1 (pine, spruce). Resin
    /// pockets are what pop loudest.</summary>
    public float Resin { get; init; } = 0.3f;
    /// <summary>How tall the flames stand, m: where the wind that fans them is taken.</summary>
    public float FlameHeightMetres { get; init; } = 0.8f;
    /// <summary>What the embers land on round it, by material name: they tick on stone.</summary>
    public string Surround { get; init; } = "Brick";
    /// <summary>Overall level at one metre, dB. MEASURED with <c>--nature levels</c>.</summary>
    public required float SourceLevelDb { get; init; }
    /// <summary>How far its loudest moments stand over <see cref="SourceLevelDb"/>, dB: the room its
    /// voice renders with (the 99.9th percentile of its 10 ms peaks, measured with --nature levels).
    /// Never under the fleet's shared 16.</summary>
    public float PeakHeadroomDb { get; init; } = 16f;
    /// <summary>The hearth's radius, m.</summary>
    public float ExtentMetres { get; init; } = 0.6f;

    /// <summary>
    /// A garden fire pit: a ring of brick a metre across with a load of seasoned mixed logs burning
    /// well — about 80 kW, the flames knee to waist high.
    /// </summary>
    public static FireSpec GardenFirePit => new()
    {
        Name = "Garden fire pit, seasoned mixed logs",
        BaseDiameterMetres = 0.9f,
        HeatReleaseKw = 80f,
        Moisture = 0.2f,
        Resin = 0.35f,
        FlameHeightMetres = 0.8f,
        Surround = "Brick",
        // MEASURED with `--nature levels fire`, 2026-10-04: Leq 59.4 dB, 57.2 dB(A). Its loud crackles
        // stand 43 dB over that (the 99.9th percentile of its 10 ms peaks), and the voice renders with
        // that room so they are not squared off.
        SourceLevelDb = 59.5f,
        PeakHeadroomDb = 45f,
        ExtentMetres = 0.5f,
    };

    public static IReadOnlyDictionary<string, Func<FireSpec>> Presets { get; } =
        new Dictionary<string, Func<FireSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["fire_pit"] = () => GardenFirePit,
        };

    public static FireSpec ByName(string key) => ModelLibrary.Fire(key);
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
/// A tree, or a hedge, that the wind blows through.
///
/// Two mechanisms, both set by the plant's own parts. Leaves on stalks FLUTTER, and fluttering
/// leaves touch: each touch is a tiny strike on a light membrane, a click in the low kilohertz, and
/// thousands of them a second are the rustle. How many, and how hard, both grow with the wind, so
/// the rustle grows much faster than the wind does. And air flowing past anything round sheds
/// vortices off it at a frequency set by the speed over the thickness (St = f d / U ≈ 0.2): over
/// twigs a few millimetres thick that is a low whoosh, over needles a millimetre thick it is the
/// higher sigh a pine wood makes. The branches SWAY at their own frequencies, and the leaves on a
/// branch that is swinging are moving faster through the air, which is why a rustle comes in surges.
/// </summary>
public sealed record FoliageSpec
{
    public string Name { get; init; } = "";
    public LeafKind Leaves { get; init; } = LeafKind.Broadleaf;
    /// <summary>The crown's radius, m.</summary>
    public required float CrownRadiusMetres { get; init; }
    /// <summary>The height of the middle of the crown, m: where the wind it feels is taken.</summary>
    public required float CrownHeightMetres { get; init; }
    /// <summary>The leaf area index: leaf area over the ground the crown covers. 3-6 for a tree in leaf.</summary>
    public float LeafAreaIndex { get; init; } = 4f;
    /// <summary>One leaf's area, cm². A lime or a maple leaf is 50-100; a birch leaf 15.</summary>
    public float LeafAreaCm2 { get; init; } = 40f;
    /// <summary>The typical thickness of what the air sheds vortices off, mm: twigs for a broadleaf,
    /// needles for a conifer.</summary>
    public float ShedDiameterMm { get; init; } = 5f;
    /// <summary>The wind speed below which leaves do not touch, m/s.</summary>
    public float StillSpeed { get; init; } = 1f;
    /// <summary>How fast the main branches swing, Hz. A big tree's crown sways at 0.3-0.6 Hz and
    /// its outer branches at a few hertz.</summary>
    public float SwayHz { get; init; } = 0.5f;
    /// <summary>Level at one metre from the crown with the wind at the field's mean, dB. MEASURED.</summary>
    public required float SourceLevelDb { get; init; }
    /// <summary>How far its loudest moments stand over <see cref="SourceLevelDb"/>, dB: the room its
    /// voice renders with (the 99.9th percentile of its 10 ms peaks, measured with --nature levels).
    /// Never under the fleet's shared 16.</summary>
    public float PeakHeadroomDb { get; init; } = 16f;
    /// <summary>How big the source is, m: the crown.</summary>
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
        // MEASURED with `--nature levels park_tree sec=600`, 2026-10-04: over ten minutes of the field
        // (4.1 m/s mean at the crown) Leq 48.2 dB, 46.7 dB(A); the gustiest second 7.5 dB over that. A
        // minute is not enough to measure it by — a minute of gusts read 2.3 dB high. Re-measured
        // 2026-10-05 with the boughs reading the wind across the crown and the field's turbulence at
        // 0.25: Leq 47.9 dB, 46.3 dB(A), the gustiest second 6.6 dB over.
        SourceLevelDb = 48f,
        PeakHeadroomDb = 20f,
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
