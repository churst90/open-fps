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
    /// <summary>The mean radius of the drops over the smallest (0.2 mm), mm. How they spread about it
    /// is <see cref="DropSizeOrder"/>.</summary>
    public float MeanDropRadiusMm { get; init; } = 1.2f;
    /// <summary>The order n of the gamma law the drop sizes follow. A jet or a sheet breaks up through
    /// ligaments, and the drops one ligament makes follow a gamma law whose order is set by how
    /// corrugated the ligament is: about 4 for the ligaments off a jet or sheet torn up in air,
    /// higher for smooth ones (Villermaux, Marmottant and Duplat 2004, Phys. Rev. Lett. 92, 074501;
    /// Villermaux 2007, Annu. Rev. Fluid Mech. 39). The exponential, order 1, is what RAIN looks
    /// like: the overlap of many breakups of many sizes (Villermaux and Bossa 2009, Nature Physics
    /// 5), not one fountain's. The exponential's long tail of big drops, each a click carrying its
    /// r³ of energy, was part of the grain in the fountain's hiss: order 4 takes the 8-16 kHz
    /// kurtosis over 10 ms windows from 3.76 to 3.52 (recorded fountains 3.45-3.75).</summary>
    public int DropSizeOrder { get; init; } = 4;
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
    /// <summary>What it lands on: the pool, or wet stone (<see cref="WaterSurface"/>).</summary>
    public WaterSurface Onto { get; init; } = WaterSurface.Pool;
    /// <summary>Which of the feature's <see cref="WaterFeatureSpec.Taps"/> it is heard from: where on the
    /// feature this water lands. A map places one emitter per tap at that place.</summary>
    public int Tap { get; init; }
    /// <summary>The order of the gamma law the coherent lumps' radii follow about
    /// <see cref="ChunkRadiusMm"/>. A column collapsing at a jet's apex, or a sheet tearing off a lip,
    /// comes apart into irregular fragments, and coarse, corrugated fragmentation gives the broad end of
    /// Villermaux's family (Villermaux 2007, Annu. Rev. Fluid Mech. 39: order 2-5, lower the more
    /// corrugated): order 2. Its tail matters: a lump's splash goes as its volume, r³, so the few big
    /// lumps are the few loud splashes a real fountain has, where lumps all one size (the first model:
    /// uniform 0.5-1.5 of the mean) summed to a steady hiss.</summary>
    public int LumpSizeOrder { get; init; } = 2;

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
    public string Name { get; init; } = "";
    /// <summary>How big this landing place is, m: the voice is flat inside it.</summary>
    public float ExtentMetres { get; init; } = 1.2f;
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
    /// <summary>Vogel's exponent V: the crown's leaves and twigs fold and streamline as the wind
    /// rises, so its drag goes as U^(2+V) rather than U². Measured from about −0.5 to −1.2 for
    /// broad leaves and their clusters (Vogel 1989, J. Exp. Bot. 40) and in the same range for
    /// whole plants (de Langre 2008, Annu. Rev. Fluid Mech. 40). It sets how fast the sound grows
    /// with the wind: the shedding's power goes as U^(5+2V).</summary>
    public float VogelExponent { get; init; } = -0.7f;
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
        // Big soft leaves on long stalks fold further than a stiff leaf does: the strong end of
        // Vogel's range. At −0.7 (taken before for every tree) the crown grew 10.8 dB from 3 to
        // 6 m/s, Fégeant's birch, and the gusts in an ordinary breeze swung it 4.4 dB (the standard
        // deviation of its 400 ms level within a minute, over ten minutes) against 1.3-4.2 dB in
        // recordings of leaves in wind; Cody heard the swings as too obvious. At −0.9 it grows 9.5 dB
        // (32 dB a decade, between Fégeant's oak at 30 and birch at 36) and swings 4.0 dB.
        VogelExponent = -0.9f,
        // MEASURED with `--nature levels park_tree sec=600`, 2026-10-04: over ten minutes of the field
        // (4.1 m/s mean at the crown) Leq 48.2 dB, 46.7 dB(A); the gustiest second 7.5 dB over that. A
        // minute is not enough to measure it by — a minute of gusts read 2.3 dB high. Re-measured
        // 2026-10-05 with the boughs reading the wind across the crown and the field's turbulence at
        // 0.25: Leq 47.9 dB, 46.3 dB(A), the gustiest second 6.6 dB over. Round 3 (Vogel −0.9, strikes
        // by contact angle): 47.8 dB, 46.2 dB(A), the gustiest second 6.7 dB over.
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
