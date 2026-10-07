using System.Collections.Generic;
using OpenFPS.Common.Editing;

namespace OpenFPS.Common;

// Water that runs: a creek, a gutter, a drain, a downpipe, a basin's overflow, a sink. The specs say what
// the water and its channel are; RunningWaterSynth works the sound out through Hydraulics. The one level,
// SourceLevelDb, is measured (AudioLab --running-water levels). See docs/RUNNING_WATER.md.

/// <summary>What carries the water, which decides how its depth and speed follow from its flow.</summary>
public enum FlowChannel
{
    /// <summary>A stream bed: wide against its depth, rough (stones, gravel), Manning's law.</summary>
    Stream,
    /// <summary>A street gutter: the road's cross-fall against a kerb, so the water is a triangle that
    /// spreads out into the road as it rises (Izzard's form of Manning's law, as road drainage is
    /// designed: FHWA HEC-22).</summary>
    KerbGutter,
    /// <summary>Nothing runs along: the source is only where the water falls (a drain grate, a
    /// downpipe's outlet, a weir).</summary>
    None,
}

/// <summary>How a source's places are laid out (ExtendedSources): along its length, or round its middle.</summary>
public enum FlowLayout
{
    /// <summary>Along the source's own x axis, the length of the channel (turn the entity to lay it).</summary>
    Line,
    /// <summary>Round the middle at half the extent: a grate, a downpipe's splash.</summary>
    Ring,
}

/// <summary>
/// What stands in the flow and breaks its surface: stones, grit, a leaf dam, a joint in a gutter. Water
/// drops into each one's lee as a little plunging jet, and a fast enough jet drives air under and the
/// bubbles ring. That is where a stream's sound comes from: a smooth reach is nearly silent.
/// </summary>
public sealed record FlowObstacles
{
    [Tunable("per m", 0, 50, "How many obstacles a metre of channel break the surface. Each is a small plunging jet, which is where a stream's sound comes from.", Label = "obstacles per metre", Step = 0.5)]
    public float PerMetre { get; init; } = 2f;
    [Tunable("m", 0.001, 2, "The median drop into an obstacle's lee: how far the water falls over it.", Label = "median drop", Step = 0.005)]
    public float MedianDropMetres { get; init; } = 0.05f;
    [Tunable("", 0, 2, "How the drops vary, the standard deviation of their natural log. Larger means a few big drops among many small, so some places are louder than others.", Step = 0.05)]
    public float DropSpread { get; init; } = 0.6f;
    [Tunable("m", 0.01, 2, "How wide a strip of the flow each obstacle gathers: the width of a stone.", Label = "obstacle width", Step = 0.01)]
    public float WidthMetres { get; init; } = 0.15f;
}

/// <summary>
/// Water leaving over a lip and falling: a weir, the bars of a drain grate, a downpipe's shoe. Rendered
/// through the falling-water physics the fountain is made of (FallingWaterSynth): its drops, its lumps,
/// their splashes and the bubbles the plunge drives under.
/// </summary>
public sealed record FlowFall
{
    [Tunable("", 0, 0, "What this fall is called.")]
    public string Name { get; init; } = "";
    [Tunable("", 0, 1, "The share of the source's flow that goes this way.", Step = 0.05)]
    public float FlowShare { get; init; } = 1f;
    [Tunable("m", 0.005, 30, "How far it falls.", Label = "fall height", Step = 0.01)]
    public required float DropMetres { get; init; }
    [Tunable("", 0, 0, "What it lands on: a pool, or a wet hard surface.", Label = "lands on")]
    public WaterSurface Onto { get; init; } = WaterSurface.Pool;
    [Tunable("m", 0.01, 20, "The width of the lip it leaves over. With the flow it sets the sheet's thickness: thin sheets break into drops, thick ones fall coherent and plunge.", Label = "lip width", Step = 0.01)]
    public float LipWidthMetres { get; init; } = 0.3f;
    [Tunable("", 1, 500, "How many separate strands it falls as: the gaps of a grate, a weir's fingers.", Step = 1)]
    public int Streams { get; init; } = 1;
    /// <summary>Heard through the source's <see cref="FlowCavity"/>.</summary>
    [Tunable("", 0, 1, "It lands inside the source's cavity, a gully pot or the foot of a downpipe, and is heard through it.", Label = "lands inside")]
    public bool Inside { get; init; }
    /// <summary>A sheet that has had nowhere to tear into drops.</summary>
    [Tunable("", 0, 1, "It arrives as a film held to a wall, however thin, as the water down a pipe's wall does.", Label = "film")]
    public bool Film { get; init; }
    [Tunable("", 0, 1, "It leaves the foot of the source's pipe, already moving at the film's terminal speed before it falls.", Label = "from a pipe")]
    public bool FromPipe { get; init; }
    /// <summary>A tap's and a drain's flows the synth works out as the basin fills and empties.</summary>
    [Tunable("", 0, 0, "Where its water comes from: a share of the source's flow, a tap's jet onto the basin or into its water, or the drain.")]
    public FallFeed Feed { get; init; } = FallFeed.Source;
}

/// <summary>What feeds a fall.</summary>
public enum FallFeed
{
    /// <summary><see cref="FlowFall.FlowShare"/> of the source's flow.</summary>
    Source,
    /// <summary>A tap's jet where it strikes the basin's bare face (<see cref="FlowBasin"/>): all of it
    /// while the basin is dry, less as water gathers over the face.</summary>
    TapOntoBasin,
    /// <summary>A tap's jet where it plunges into the water standing in the basin.</summary>
    TapIntoWater,
    /// <summary>What leaves through the inlet (<see cref="FlowInlet"/>) while it is not running full: the
    /// water spilling over the rim of a strainer, a gutter's outlet, into the pipe below.</summary>
    Drain,
}

/// <summary>
/// Where the water leaves: a sink's waste, a gutter's outlet. Shallow, it spills over the rim, a weir
/// round a hole; deeper, it closes over the hole and air goes down in gulps round a vortex (the gurgle);
/// deep enough, the vortex no longer reaches it and it runs full and quiet (docs/RUNNING_WATER.md, round 2).
/// </summary>
public sealed record FlowInlet
{
    [Tunable("m", 0.01, 0.5, "The hole's diameter: a sink's waste 32 to 40 mm, a roof gutter's outlet 50 to 68 mm.", Label = "hole diameter", Step = 0.001)]
    public required float DiameterMetres { get; init; }
    [Tunable("", 0.05, 1, "The share of the hole left open by a strainer's bars or a leaf guard.", Step = 0.05)]
    public float OpenShare { get; init; } = 1f;
    [Tunable("m", 0, 5, "The length of edge the water spills over while it is shallow. Zero takes the hole's circumference; a basket strainer's slots give several times that.", Label = "spill edge length", Step = 0.05)]
    public float RimMetres { get; init; }
    /// <summary>Gordon's C in the vortex's critical submergence S = C v √D (SI; Gordon 1970). A basin
    /// draining from rest has almost no circulation (a bathtub vortex needs rotation to reach the drain:
    /// Andersen, Lautrup and Bohr 2003), hence the judged 0.25.</summary>
    [Tunable("", 0, 1.5, "How strongly the water swirls as it comes to the hole, Gordon's C: 0.54 for a symmetric approach, 0.72 for water arriving along a gutter, about 0.25 for a basin draining from rest. More swirl lets the vortex reach the hole from deeper water.", Step = 0.01, Source = "Gordon 1970; 0.25 for a basin draining from rest is a judgement")]
    public float Swirl { get; init; } = 0.72f;
    /// <summary>With <see cref="RunningWaterSpec.Cavity"/> if the source has one.</summary>
    [Tunable("m", 0.05, 20, "How long the pipe below the hole is before the water meets standing water or turns: the air a gulp is heard through.", Label = "pipe length", Step = 0.05)]
    public float PipeMetres { get; init; } = 0.4f;
}

/// <summary>
/// A basin the water gathers in before it leaves: a sink, a washbasin, a shower tray. Its water rises and
/// falls with what comes in and what its inlet lets out (an orifice: Q = Cd A √(2 g h)), and where the
/// tap's jet lands depends on how deep it is.
/// </summary>
public sealed record FlowBasin
{
    /// <summary>A material name (AcousticRegistry).</summary>
    [Tunable("", 0, 0, "What the basin is made of. Metal for a stainless sink, a thin plate that rings; Tile for a ceramic basin, which barely rings.", Choices = "materials")]
    public string Material { get; init; } = "Metal";
    [Tunable("m", 0.0003, 0.03, "The thickness of the bottom: 0.7 to 1 mm for a steel sink, 6 to 10 mm for a ceramic basin. Thinner rings lower.", Label = "bottom thickness", Step = 0.0001)]
    public float SkinMetres { get; init; } = 0.0009f;
    [Tunable("m", 0.1, 3, "The length of the basin's bottom.", Step = 0.01)]
    public float LengthMetres { get; init; } = 0.45f;
    [Tunable("m", 0.1, 3, "The width of the basin's bottom.", Step = 0.01)]
    public float WidthMetres { get; init; } = 0.38f;
    [Tunable("m", 0.01, 1, "The basin's depth.", Step = 0.01)]
    public float DepthMetres { get; init; } = 0.18f;
    [Tunable("", 0.0001, 0.5, "How fast the bottom's ringing dies in place: about 0.03 to 0.05 for a stainless sink with its anti-drum pads, 0.002 for bare steel.", Step = 0.001)]
    public float LossFactor { get; init; } = 0.04f;
    /// <summary>A pressed bowl is stiffened by its radiused sides and the strainer's boss: what drums is
    /// the flat between them, not the whole bottom.</summary>
    [Tunable("m", 0.02, 2, "The size of the flat of the bottom that rings, between the radiused sides and the dished boss over the waste.", Label = "ringing panel size", Step = 0.01)]
    public float PanelMetres { get; init; } = 0.2f;
    [Tunable("m²", 0, 0.5, "The area of the dished middle over the waste where the first water gathers.", Label = "sump area", Step = 0.001)]
    public float SumpSquareMetres { get; init; } = 0.012f;
    [Tunable("m", 0, 0.05, "How far the bottom falls to the waste. Below that depth the water stands in the dish, not over the whole bottom.", Label = "fall to the waste", Step = 0.001)]
    public float FallToWasteMetres { get; init; } = 0.006f;
    [Tunable("", 0, 1, "The plug is in: nothing leaves but over the overflow.", Label = "plug in")]
    public bool PlugIn { get; init; }
    /// <summary>One key does both: the game's interact key on the tap.</summary>
    [Tunable("", 0, 1, "The plug goes in while the tap runs and comes out when it is shut, as when someone fills the basin and lets it go after.", Label = "plug in while running")]
    public bool PlugWhileRunning { get; init; }
    [Tunable("m", 0.01, 1, "How high the overflow slot is above the bottom.", Label = "overflow height", Step = 0.01)]
    public float OverflowMetres { get; init; } = 0.15f;
}

/// <summary>A tap over a basin: how much it lets through open, and what its stream is like.</summary>
public sealed record FlowTap
{
    [Tunable("L/s", 0.01, 1, "The flow fully open: a kitchen tap 0.1 to 0.15, a washbasin 0.07 to 0.1, a shower 0.13 to 0.2.", Label = "open flow", Step = 0.01)]
    public float OpenLitresPerSecond { get; init; } = 0.12f;
    [Tunable("mm", 0.5, 50, "The spout's bore.", Label = "spout bore", Step = 0.5)]
    public float NozzleMm { get; init; } = 16f;
    [Tunable("", 0, 1, "It has an aerator: the stream comes out white, air and water mixed, and lands soft and hissing instead of as a glassy column.")]
    public bool Aerated { get; init; } = true;
    [Tunable("", 1, 300, "How many jets the water leaves as: one for a spout, many for a shower rose.", Step = 1)]
    public int Jets { get; init; } = 1;
    [Tunable("m", 0.02, 3, "How far the spout stands over the basin's bottom.", Label = "spout height", Step = 0.01)]
    public float HeightMetres { get; init; } = 0.3f;
    [Tunable("L/s", 0, 0.01, "What a worn washer lets past when the tap is shut. A drip every second or two is about 0.0001.", Label = "leak when shut", Step = 0.00001)]
    public float LeakLitresPerSecond { get; init; }
}

/// <summary>
/// An air space the falling water is heard through: a gully pot, a downpipe. A tube open at the top and
/// closed by the water or open at the far end, ringing at its own modes.
/// </summary>
public sealed record FlowCavity
{
    [Tunable("m", 0.05, 30, "The air column's length: the grate to the water in the pot, or the pipe's length. Longer rings lower.", Label = "air column length", Step = 0.05)]
    public required float LengthMetres { get; init; }
    [Tunable("m", 0.01, 2, "The air column's diameter.", Label = "air column diameter", Step = 0.001)]
    public required float DiameterMetres { get; init; }
    [Tunable("", 0, 1, "The far end is open, as a downpipe open at the top to its gutter, rather than closed by water, as a gully pot is.", Label = "far end open")]
    public bool FarEndOpen { get; init; }
}

/// <summary>Running water as a parts list (docs/RUNNING_WATER.md).</summary>
public sealed record RunningWaterSpec
{
    [Tunable("", 0, 0, "What this running water is called.")]
    public string Name { get; init; } = "";

    // ── How much water ──────────────────────────────────────────────────────────────────────────

    [Tunable("L/s", 0, 5000, "Flow whatever the weather: a creek's base flow, a fountain's pump.", Label = "base flow", Step = 0.5)]
    public float BaseFlowLitresPerSecond { get; init; }
    [Tunable("m²", 0, 100000, "The plan area of ground or roof whose rain it carries. Zero for a source the rain does not feed.", Label = "catchment area", Step = 5)]
    public float CatchmentSquareMetres { get; init; }
    [Tunable("", 0, 1, "The share of the rain that runs off: about 0.95 for a roof, 0.9 for asphalt.", Step = 0.01)]
    public float RunoffCoefficient { get; init; } = 0.9f;
    /// <summary>The time constant of <see cref="Runoff"/>.</summary>
    [Tunable("s", 5, 7200, "The catchment's time constant: how long it takes to come up to the rain and how long it runs on after.", Label = "catchment time", Step = 10)]
    public float CatchmentSeconds { get; init; } = 120f;
    [Tunable("mm/h", 0.1, 150, "The rain the source level was measured at, for a rain-fed source. Change it only with a new measurement.", Label = "reference rain", Step = 0.5)]
    public float ReferenceRainMmPerHour { get; init; } = Rainfall.ModerateRate;
    /// <summary>Drains over <see cref="SlowSeconds"/> (docs/RUNNING_WATER.md, round 2: two-stage drainage).</summary>
    [Tunable("", 0, 1, "The share of the run-off held back in a slow store, water standing in a gutter's sags or soaked into moss and grit, that drains over the slow store's time. It keeps a downpipe dripping long after the rain.", Label = "slow store share", Step = 0.01)]
    public float SlowShare { get; init; }
    [Tunable("s", 60, 14400, "The slow store's time constant.", Label = "slow store time", Step = 60)]
    public float SlowSeconds { get; init; } = 1800f;

    // ── The channel ─────────────────────────────────────────────────────────────────────────────

    [Tunable("", 0, 0, "What carries the water: a stream bed, a street gutter against a kerb, or nothing, where the source is only where the water falls.")]
    public FlowChannel Channel { get; init; } = FlowChannel.None;
    [Tunable("m", 0.05, 100, "A stream bed's width.", Label = "bed width", Step = 0.1)]
    public float WidthMetres { get; init; } = 2f;
    [Tunable("", 0.0001, 0.5, "The slope along the flow, a fraction: 0.02 is 2 per cent. Steeper runs faster and shallower.", Step = 0.001)]
    public float Slope { get; init; } = 0.01f;
    [Tunable("", 0.005, 0.2, "A gutter's cross-fall into the kerb, a fraction.", Label = "cross-fall", Step = 0.005)]
    public float CrossSlope { get; init; } = 0.03f;
    [Tunable("", 0.008, 0.2, "Manning's roughness: 0.013 smooth concrete, 0.016 asphalt, 0.035 to 0.05 a cobble bed. Rougher runs slower and deeper.", Label = "Manning's n", Step = 0.001, Source = "Manning 1891")]
    public float ManningN { get; init; } = 0.035f;
    [Tunable("m", 0.5, 500, "The length of channel this source is.", Label = "channel length", Step = 0.5)]
    public float LengthMetres { get; init; } = 10f;
    [Tunable("", 0, 1, "The share of the catchment's water that has reached this stretch: half way down the gutter that feeds a grate it has gathered half.", Step = 0.05)]
    public float ShareOfCatchment { get; init; } = 1f;

    /// <summary>Null for a source with no channel.</summary>
    public FlowObstacles? Obstacles { get; init; }
    public FlowFall[] Falls { get; init; } = Array.Empty<FlowFall>();
    /// <summary>The air space the inside falls are heard through.</summary>
    public FlowCavity? Cavity { get; init; }
    [Tunable("mm", 0, 10, "The radius of the lip it drips from when the flow is too small to run, such as a downpipe's shoe or a grate's bars. Zero: no drips.", Label = "drip lip radius", Step = 0.5)]
    public float DripLipMm { get; init; }
    [Tunable("m", 0.01, 10, "How far the drips fall.", Label = "drip fall", Step = 0.01)]
    public float DripFallMetres { get; init; } = 0.2f;
    [Tunable("", 0, 0, "What the drips land on: a pool, or a wet hard surface.", Label = "drips land on")]
    public WaterSurface DripOnto { get; init; } = WaterSurface.Pool;
    [Tunable("", 0, 1, "The drips land inside the cavity, as off a grate's bars into its pot, not outside, as off a downpipe's shoe.", Label = "drips inside")]
    public bool DripsInside { get; init; }

    /// <summary>Where the water leaves, if its leaving is heard (a strainer, a gutter's outlet).</summary>
    public FlowInlet? Inlet { get; init; }
    /// <summary>The basin it gathers in, for a sink, a washbasin or a shower tray.</summary>
    public FlowBasin? Basin { get; init; }
    /// <summary>With a tap, the source runs when somebody turns it on (SoundEmitterComponent.SynthRunning),
    /// not with the rain.</summary>
    public FlowTap? Tap { get; init; }

    // ── How it is heard ─────────────────────────────────────────────────────────────────────────

    /// <summary>Measured with <c>--running-water levels</c> at its base flow, or at
    /// <see cref="ReferenceRainMmPerHour"/>.</summary>
    [Tunable("dB", 10, 120, "Overall level at one metre, the whole source as if at one point, measured with --running-water levels at its base flow or at the reference rain. Change it only after measuring the model again.", Step = 0.5, Source = "MEASURED with --running-water levels")]
    public required float SourceLevelDb { get; init; }
    /// <summary>How far its loudest moments stand over the level, dB (the 99.9th percentile of its
    /// 10 ms peaks). Never under the fleet's shared 16.</summary>
    public float PeakHeadroomDb { get; init; } = 16f;
    [Tunable("m", 0.1, 50, "How big it is. Inside it the sound is flat.", Step = 0.05)]
    public float ExtentMetres { get; init; } = 2f;
    /// <summary>How many places it is heard from (ExtendedSources), its middle included.</summary>
    public int Places { get; init; } = 1;
    [Tunable("", 0, 0, "How its places are laid out: along its length for a channel, or round its middle for a grate or a downpipe's splash.")]
    public FlowLayout Layout { get; init; } = FlowLayout.Line;

    /// <summary>Under this a rain-fed source is dry, L/s: a drop every few seconds off a downpipe's shoe.</summary>
    public const float DryLitresPerSecond = 2e-5f;

    /// <summary>The flow now, L/s, for this much run-off through its catchment (Runoff.Through).</summary>
    public float FlowFor(float runoffMmPerHour)
        => BaseFlowLitresPerSecond + ShareOfCatchment * MathF.Max(0f, CatchmentSquareMetres)
           * Math.Clamp(RunoffCoefficient, 0f, 1f) * MathF.Max(0f, runoffMmPerHour) / 3600f;

    /// <summary>The flow the declared level was measured at.</summary>
    public float ReferenceFlow => Tap is { } tap ? tap.OpenLitresPerSecond : FlowFor(CatchmentSquareMetres > 0f ? ReferenceRainMmPerHour : 0f);

    /// <summary>The flow now, L/s, from the world's rain through both of the catchment's stores (Runoff),
    /// or a tap's (on, or leaking).</summary>
    public float FlowNow(bool tapOn = true)
    {
        if (Tap is { } tap) return tapOn ? tap.OpenLitresPerSecond : tap.LeakLitresPerSecond;
        if (CatchmentSquareMetres <= 0f) return BaseFlowLitresPerSecond;
        float slow = Math.Clamp(SlowShare, 0f, 1f);
        float through = (1f - slow) * Runoff.Through(CatchmentSeconds) + (slow > 0f ? slow * Runoff.Through(SlowSeconds) : 0f);
        return FlowFor(through);
    }

    /// <summary>
    /// A gurgling creek: a riffle a couple of metres wide over cobbles and stones, a couple of hand's
    /// breadths deep, the kind of stream a path follows through a park or a wood. 40 L/s down a 2 %
    /// bed (n 0.045, cobbles) is 4 cm deep at 0.4 m/s; two or three stones a metre stand up through it.
    /// Sixteen metres of it, heard from five places along its length.
    /// </summary>
    public static RunningWaterSpec Creek => new()
    {
        Name = "Creek, a cobble riffle",
        BaseFlowLitresPerSecond = 40f,
        Channel = FlowChannel.Stream,
        WidthMetres = 2.5f,
        Slope = 0.02f,
        ManningN = 0.045f,
        LengthMetres = 16f,
        Obstacles = new FlowObstacles { PerMetre = 2.5f, MedianDropMetres = 0.06f, DropSpread = 0.6f, WidthMetres = 0.15f },
        // MEASURED with `--running-water levels sec=60`, 2026-10-06: creek: Leq 67.3 dB, LAeq 67.7 dB(A); 10 ms peaks' 99.9th percentile 16.8 dB over.
        SourceLevelDb = 67.5f,
        PeakHeadroomDb = 17f,
        ExtentMetres = 2f,
        Places = 5,
        Layout = FlowLayout.Line,
    };

    /// <summary>
    /// A street gutter in the rain: the fifteen metres of kerb above a drain, carrying half the road and
    /// its pavement (30 m of 6 m asphalt and 3.5 m paving, 285 m² at 0.9), 3 % cross-fall, 1 % fall. At
    /// 5 mm/h 0.36 L/s reaches the drain, a strip a third of a metre wide and a centimetre deep at 0.2 m/s,
    /// tripping on grit, leaves and joints, ten a metre. Heavier rain drowns the small ones (their lee jets
    /// fall under the breaking Froude number), so a gutter does not simply grow louder with the rain.
    /// </summary>
    public static RunningWaterSpec KerbGutter => new()
    {
        Name = "Street gutter in the rain",
        CatchmentSquareMetres = 285f,
        RunoffCoefficient = 0.9f,
        CatchmentSeconds = 180f,
        // The fifteen metres above the drain hold, on average, three quarters of its water.
        ShareOfCatchment = 0.75f,
        Channel = FlowChannel.KerbGutter,
        Slope = 0.01f,
        CrossSlope = 0.03f,
        ManningN = 0.016f,
        LengthMetres = 15f,
        Obstacles = new FlowObstacles { PerMetre = 10f, MedianDropMetres = 0.008f, DropSpread = 1.0f, WidthMetres = 0.05f },
        // MEASURED with `--running-water levels sec=60`, 2026-10-06: gutter at 5 mm/h with its rain on it: Leq 51.0 dB, LAeq 51.9 dB(A); peaks 17.5 dB over.
        SourceLevelDb = 51f,
        PeakHeadroomDb = 18f,
        ExtentMetres = 2f,
        Places = 5,
        Layout = FlowLayout.Line,
    };

    /// <summary>
    /// The drain that gutter runs into: a road gully, a 0.45 m grate in the gutter over a pot 0.45 m
    /// across with its water seal 0.45 m below the bars. The gutter's water pours between the bars and
    /// falls into the pot; a little strikes the bars and splashes on them. Heard through the pot's air
    /// column and out of the grate. Fed by the same 285 m² as the gutter.
    /// </summary>
    public static RunningWaterSpec DrainGrate => new()
    {
        Name = "Road drain, gully grate",
        CatchmentSquareMetres = 285f,
        RunoffCoefficient = 0.9f,
        CatchmentSeconds = 180f,
        Channel = FlowChannel.None,
        Falls = new[]
        {
            new FlowFall { Name = "between the bars into the pot", FlowShare = 0.85f, DropMetres = 0.45f, Onto = WaterSurface.Pool,
                           LipWidthMetres = 0.3f, Streams = 10, Inside = true },
            new FlowFall { Name = "onto the bars", FlowShare = 0.15f, DropMetres = 0.03f, Onto = WaterSurface.Rock,
                           LipWidthMetres = 0.3f, Streams = 10 },
        },
        Cavity = new FlowCavity { LengthMetres = 0.45f, DiameterMetres = 0.45f },
        DripLipMm = 2f,
        DripFallMetres = 0.45f,
        DripOnto = WaterSurface.Pool,
        DripsInside = true,
        // MEASURED with `--running-water levels sec=60`, 2026-10-06: drain at 5 mm/h: Leq 58.3 dB, LAeq 58.4 dB(A); peaks 19.7 dB over.
        SourceLevelDb = 58.5f,
        PeakHeadroomDb = 20f,
        ExtentMetres = 0.5f,
        Places = 3,
        Layout = FlowLayout.Ring,
    };

    /// <summary>
    /// A downpipe off a house roof: half a 90 m² roof (45 m² at 0.95, a minute to run off) down 5.5 m of
    /// 68 mm pipe to a shoe 0.15 m over the paving. Inside, the water runs down the wall as a film and
    /// strikes the shoe's bend at the film's own terminal speed (1.5 m/s in moderate rain, as if it had
    /// fallen 12 cm: RunningWaterSynth.FallFor); that is heard through the pipe, a tube open at both ends.
    /// Out of the shoe it falls onto the paving. When the rain stops it runs on for a few minutes and
    /// then drips into the puddle it made.
    /// </summary>
    public static RunningWaterSpec Downpipe => new()
    {
        Name = "Downpipe off a house roof",
        CatchmentSquareMetres = 45f,
        RunoffCoefficient = 0.95f,
        CatchmentSeconds = 60f,
        // An eighth of the roof's water lingers in the gutter's sags and the grit and moss on the roof and
        // comes away over half an hour: the drips long after the rain (round 2, two-stage drainage).
        SlowShare = 0.12f,
        SlowSeconds = 1800f,
        Channel = FlowChannel.None,
        Falls = new[]
        {
            new FlowFall { Name = "the film striking the shoe's bend", DropMetres = 5.5f, Onto = WaterSurface.Rock,
                           LipWidthMetres = 0.21f, Inside = true, Film = true },
            new FlowFall { Name = "out of the shoe onto the paving", DropMetres = 0.15f, Onto = WaterSurface.Rock,
                           LipWidthMetres = 0.06f, FromPipe = true },
        },
        Cavity = new FlowCavity { LengthMetres = 5.5f, DiameterMetres = 0.068f, FarEndOpen = true },
        DripLipMm = 3f,
        DripFallMetres = 0.15f,
        DripOnto = WaterSurface.Pool,
        // MEASURED with `--running-water levels sec=60`, 2026-10-06 (round 2): moderate rain, its shoe now carrying the film's speed: Leq 53.3 dB, LAeq 53.1 dB(A); peaks 23.0 dB over.
        SourceLevelDb = 53.5f,
        PeakHeadroomDb = 23f,
        ExtentMetres = 0.6f,
        Places = 3,
        Layout = FlowLayout.Ring,
    };

    /// <summary>
    /// A fountain basin's overflow: a basin kept brim-full by its pump, skimmed continuously over a 1.2 m
    /// weir in the kerb's inside face, the water falling 0.35 m into a sump under a grate and back to the
    /// pump. Two litres a second: 1.6 cm over the lip, a sheet that falls coherent, in strands (thirty
    /// along the lip). At half a litre a second (a basin only topped up) it measured sparser
    /// than every recorded overflow, which are all bigger flows.
    /// </summary>
    public static RunningWaterSpec BasinOverflow => new()
    {
        Name = "Fountain basin overflow",
        BaseFlowLitresPerSecond = 2f,
        Channel = FlowChannel.None,
        Falls = new[]
        {
            new FlowFall { Name = "over the weir into the sump", DropMetres = 0.35f, Onto = WaterSurface.Pool,
                           LipWidthMetres = 1.2f, Streams = 30, Inside = true },
        },
        Cavity = new FlowCavity { LengthMetres = 0.35f, DiameterMetres = 0.3f },
        // MEASURED with `--running-water levels sec=60`, 2026-10-06: overflow at 2 L/s: Leq 66.7 dB, LAeq 65.8 dB(A); peaks 19-21 dB over.
        SourceLevelDb = 67f,
        PeakHeadroomDb = 21f,
        ExtentMetres = 0.6f,
        Places = 3,
        Layout = FlowLayout.Ring,
    };

    /// <summary>
    /// Where a house's roof gutter empties into its downpipe: a 63 mm outlet in the bottom of a half-round
    /// gutter, a swan-neck offset 0.3 m below it, then the pipe (the same 45 m² of roof as
    /// <see cref="Downpipe"/>; place it at the eaves over the downpipe's shoe). In light rain the water
    /// spills over the outlet's rim and slides down the pipe's wall to the swan neck. In a downpour it stands
    /// deep enough over the outlet to close over it: a vortex forms, and air goes down with the water in
    /// gulps, the gurgle a downpipe makes at the top in heavy rain.
    /// </summary>
    public static RunningWaterSpec GutterOutlet => new()
    {
        Name = "Roof gutter outlet into a downpipe",
        CatchmentSquareMetres = 45f,
        RunoffCoefficient = 0.95f,
        CatchmentSeconds = 60f,
        SlowShare = 0.12f,
        SlowSeconds = 1800f,
        Channel = FlowChannel.None,
        Inlet = new FlowInlet { DiameterMetres = 0.063f, OpenShare = 1f, PipeMetres = 0.3f },
        Falls = new[]
        {
            new FlowFall { Name = "over the outlet's rim onto the swan neck", Feed = FallFeed.Drain, DropMetres = 0.3f,
                           Onto = WaterSurface.Rock, LipWidthMetres = 0.2f, Inside = true, Film = true },
        },
        Cavity = new FlowCavity { LengthMetres = 5.5f, DiameterMetres = 0.068f, FarEndOpen = true },
        // MEASURED with `--running-water levels sec=60`, 2026-10-06 (round 2): moderate rain, spilling over the rim: Leq 48.0 dB, LAeq 48.0 dB(A); peaks 19.1 dB over.
        SourceLevelDb = 48f,
        PeakHeadroomDb = 20f,
        ExtentMetres = 0.3f,
        Places = 3,
        Layout = FlowLayout.Ring,
    };

    /// <summary>
    /// A kitchen sink: a stainless bowl 0.45 by 0.38 m and 0.18 deep (0.9 mm steel with its anti-drum
    /// pads), an aerated mixer tap 0.28 m over its bottom giving 0.12 L/s fully open, and a 40 mm basket
    /// strainer (its bars leave about half of it open) over 0.35 m of waste down to the trap. The tap is
    /// turned on and off by a person (SoundEmitterComponent.SynthRunning): on, the plug goes in and the bowl
    /// fills; off, the plug comes out and it drains, gurgling as it empties.
    /// </summary>
    public static RunningWaterSpec KitchenSink => new()
    {
        Name = "Kitchen sink, stainless, mixer tap",
        Channel = FlowChannel.None,
        Tap = new FlowTap { OpenLitresPerSecond = 0.12f, NozzleMm = 18f, Aerated = true, HeightMetres = 0.28f },
        Basin = new FlowBasin { Material = "Metal", SkinMetres = 0.0009f, LengthMetres = 0.45f, WidthMetres = 0.38f, DepthMetres = 0.18f, LossFactor = 0.04f,
                                PlugWhileRunning = true },
        Inlet = new FlowInlet { DiameterMetres = 0.04f, OpenShare = 0.45f, RimMetres = 0.5f, Swirl = 0.25f, PipeMetres = 0.35f },
        Falls = new[]
        {
            new FlowFall { Name = "the tap onto the bottom", Feed = FallFeed.TapOntoBasin, DropMetres = 0.28f, Onto = WaterSurface.Rock },
            new FlowFall { Name = "the tap into the water", Feed = FallFeed.TapIntoWater, DropMetres = 0.28f, Onto = WaterSurface.Pool },
            new FlowFall { Name = "through the strainer down the waste", Feed = FallFeed.Drain, DropMetres = 0.35f, Onto = WaterSurface.Pool,
                           LipWidthMetres = 0.12f, Inside = true, Film = true },
        },
        Cavity = new FlowCavity { LengthMetres = 0.35f, DiameterMetres = 0.04f },
        DripLipMm = 3f,
        DripFallMetres = 0.28f,
        DripOnto = WaterSurface.Rock,
        // MEASURED with `--running-water levels sec=60`, 2026-10-06 (round 2): the tap running a minute, filling: Leq 56.8 dB, LAeq 56.0 dB(A); peaks 26.2 dB over.
        SourceLevelDb = 57f,
        PeakHeadroomDb = 26.5f,
        ExtentMetres = 0.5f,
        Places = 3,
        Layout = FlowLayout.Ring,
    };

    /// <summary>The same kitchen sink with a worn washer: shut, it drips about once every one and a half
    /// seconds onto the steel (a drop a lip of 3 mm lets go is about 0.08 mL).</summary>
    public static RunningWaterSpec DrippingKitchenSink => KitchenSink with
    {
        Name = "Kitchen sink, dripping tap",
        Tap = KitchenSink.Tap! with { LeakLitresPerSecond = 0.00005f },
        // Declared at the drip (MEASURED, `--running-water cycle dripping_sink on=0 off=60`: Leq 21.4 dB,
        // peaks 36.8 dB over), so the mixer gives it the law's lift; at the running tap's 57 dB it played 36 dB
        // under that, unheard. Turned on it runs 35 dB over the drip: the headroom is room for that.
        SourceLevelDb = 21.5f,
        PeakHeadroomDb = 62f,
    };

    /// <summary>
    /// A bathroom washbasin: vitreous china (stiff and thick, so it does not ring like steel: a hard wet
    /// surface), 0.45 by 0.35 m and 0.15 deep, an aerated tap 0.2 m over the bottom at 0.08 L/s, a 32 mm
    /// waste with a pop-up plug standing open (about half the hole), 0.3 m to its trap.
    /// </summary>
    public static RunningWaterSpec Washbasin => new()
    {
        Name = "Bathroom washbasin, ceramic",
        Channel = FlowChannel.None,
        Tap = new FlowTap { OpenLitresPerSecond = 0.08f, NozzleMm = 16f, Aerated = true, HeightMetres = 0.2f },
        Basin = new FlowBasin { Material = "Tile", SkinMetres = 0.008f, LengthMetres = 0.45f, WidthMetres = 0.35f, DepthMetres = 0.15f, LossFactor = 0.02f,
                                OverflowMetres = 0.12f, PlugWhileRunning = true },
        Inlet = new FlowInlet { DiameterMetres = 0.032f, OpenShare = 0.5f, Swirl = 0.25f, PipeMetres = 0.3f },
        Falls = new[]
        {
            new FlowFall { Name = "the tap onto the china", Feed = FallFeed.TapOntoBasin, DropMetres = 0.2f, Onto = WaterSurface.Rock },
            new FlowFall { Name = "the tap into the water", Feed = FallFeed.TapIntoWater, DropMetres = 0.2f, Onto = WaterSurface.Pool },
            new FlowFall { Name = "down the waste", Feed = FallFeed.Drain, DropMetres = 0.3f, Onto = WaterSurface.Pool,
                           LipWidthMetres = 0.1f, Inside = true, Film = true },
        },
        Cavity = new FlowCavity { LengthMetres = 0.3f, DiameterMetres = 0.032f },
        DripLipMm = 3f,
        DripFallMetres = 0.2f,
        DripOnto = WaterSurface.Rock,
        // MEASURED with `--running-water levels sec=60`, 2026-10-06 (round 2): the tap running a minute, filling: Leq 51.9 dB, LAeq 52.1 dB(A); peaks 20.8 dB over.
        SourceLevelDb = 52f,
        PeakHeadroomDb = 21f,
        ExtentMetres = 0.45f,
        Places = 3,
        Layout = FlowLayout.Ring,
    };

    /// <summary>
    /// A shower with nobody in it: a rose of sixty 1 mm jets 1.9 m over a ceramic tray 0.8 m square, 0.15
    /// L/s; the jets break into millimetre drops long before they land, so it is rain onto a wet hard floor
    /// from a couple of metres, gathering on the tray and leaving through a 40 mm grated waste.
    /// </summary>
    public static RunningWaterSpec Shower => new()
    {
        Name = "Shower onto a ceramic tray",
        Channel = FlowChannel.None,
        Tap = new FlowTap { OpenLitresPerSecond = 0.15f, NozzleMm = 7.75f, Jets = 60, Aerated = false, HeightMetres = 1.9f },
        Basin = new FlowBasin { Material = "Tile", SkinMetres = 0.008f, LengthMetres = 0.8f, WidthMetres = 0.8f, DepthMetres = 0.04f, LossFactor = 0.02f },
        Inlet = new FlowInlet { DiameterMetres = 0.04f, OpenShare = 0.5f, RimMetres = 0.4f, Swirl = 0.25f, PipeMetres = 0.3f },
        Falls = new[]
        {
            new FlowFall { Name = "the spray onto the tray", Feed = FallFeed.TapOntoBasin, DropMetres = 1.9f, Onto = WaterSurface.Rock, Streams = 60 },
            new FlowFall { Name = "the spray into the water on the tray", Feed = FallFeed.TapIntoWater, DropMetres = 1.9f, Onto = WaterSurface.Pool, Streams = 60 },
            new FlowFall { Name = "down the waste", Feed = FallFeed.Drain, DropMetres = 0.3f, Onto = WaterSurface.Pool,
                           LipWidthMetres = 0.12f, Inside = true, Film = true },
        },
        Cavity = new FlowCavity { LengthMetres = 0.3f, DiameterMetres = 0.04f },
        // MEASURED with `--running-water levels sec=60`, 2026-10-06 (round 2): running: Leq 59.8 dB, LAeq 59.4 dB(A); peaks 17.6 dB over.
        SourceLevelDb = 60f,
        PeakHeadroomDb = 18f,
        ExtentMetres = 0.8f,
        Places = 3,
        Layout = FlowLayout.Ring,
    };

    public static IReadOnlyDictionary<string, Func<RunningWaterSpec>> Presets { get; } =
        new Dictionary<string, Func<RunningWaterSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["creek"] = () => Creek,
            ["gutter"] = () => KerbGutter,
            ["drain_grate"] = () => DrainGrate,
            ["downpipe"] = () => Downpipe,
            ["basin_overflow"] = () => BasinOverflow,
            ["gutter_outlet"] = () => GutterOutlet,
            ["kitchen_sink"] = () => KitchenSink,
            ["dripping_sink"] = () => DrippingKitchenSink,
            ["washbasin"] = () => Washbasin,
            ["shower"] = () => Shower,
        };

    /// <summary>A preset by name, through the <see cref="ModelLibrary"/> so a map's own wins.</summary>
    public static RunningWaterSpec ByName(string key) => ModelLibrary.Flow(key);
}

/// <summary>
/// Depth and speed from flow: the open-channel hydraulics every drainage engineer uses, so a channel's
/// sound follows from what it is. Manning (1891): v = R^(2/3) S^(1/2) / n, R the hydraulic radius.
/// </summary>
public static class Hydraulics
{
    public const float Gravity = 9.81f;

    /// <summary>Depth (m), mean speed (m/s) and Froude number of a flow in a channel.</summary>
    public readonly record struct State(float DepthMetres, float SpeedMetresPerSecond, float WettedWidthMetres)
    {
        public float Froude => DepthMetres > 0f ? SpeedMetresPerSecond / MathF.Sqrt(Gravity * DepthMetres) : 0f;
    }

    /// <summary>The state of <paramref name="litresPerSecond"/> in this channel.</summary>
    public static State Of(RunningWaterSpec spec, float litresPerSecond)
    {
        float q = MathF.Max(0f, litresPerSecond) * 1e-3f;
        if (q <= 0f) return default;
        float s = MathF.Max(1e-5f, spec.Slope), n = MathF.Max(0.008f, spec.ManningN);
        switch (spec.Channel)
        {
            case FlowChannel.Stream:
            {
                // A rectangle of width w: Q = (1/n) w y (w y / (w + 2y))^(2/3) S^(1/2). Solved for y by
                // bisection on the log (monotonic in y).
                float w = MathF.Max(0.05f, spec.WidthMetres);
                float lo = 1e-5f, hi = 20f;
                for (int i = 0; i < 60; i++)
                {
                    float y = MathF.Sqrt(lo * hi);
                    float r = w * y / (w + 2f * y);
                    float qq = w * y * MathF.Pow(r, 2f / 3f) * MathF.Sqrt(s) / n;
                    if (qq > q) hi = y; else lo = y;
                }
                float depth = MathF.Sqrt(lo * hi);
                return new State(depth, q / (w * depth), w);
            }
            case FlowChannel.KerbGutter:
            {
                // A triangle against the kerb, cross-fall Sx: Q = (Ku/n) Sx^(5/3) S^(1/2) T^(8/3), Ku = 0.376
                // in SI, T the spread from the kerb (Izzard 1946; FHWA HEC-22, 4th ed., eq. 4-2). Depth at
                // the kerb y = T Sx, and the mean speed is Q over the triangle's area T y / 2.
                float sx = Math.Clamp(spec.CrossSlope, 0.005f, 0.2f);
                float spread = MathF.Pow(q * n / (0.376f * MathF.Pow(sx, 5f / 3f) * MathF.Sqrt(s)), 3f / 8f);
                float depth = spread * sx;
                return new State(depth, q / (0.5f * spread * depth), spread);
            }
            default:
                return default;
        }
    }

    /// <summary>The head over a sharp-crested weir passing this flow, m: Q = Cd (2/3) √(2g) b h^(3/2)
    /// with Cd ≈ 0.62 (Rehbock), so Q ≈ 1.83 b h^1.5.</summary>
    public static float WeirHead(float litresPerSecond, float lipWidthMetres)
        => MathF.Pow(MathF.Max(0f, litresPerSecond) * 1e-3f / (1.83f * MathF.Max(0.01f, lipWidthMetres)), 2f / 3f);

    /// <summary>The speed water leaves a lip at under this head, m/s: critical flow, √(g · 2h/3).</summary>
    public static float LipSpeed(float headMetres) => MathF.Sqrt(Gravity * 2f / 3f * MathF.Max(0f, headMetres));
}
