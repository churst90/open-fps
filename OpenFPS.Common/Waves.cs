using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using OpenFPS.Common.Editing;

namespace OpenFPS.Common;

// Waves at the water's edge: a lake lapping on its beach, the sea's surf, a shingle beach's backwash,
// wavelets slapping a wall or a boat's hull, a big river's bank. Like RunningWaterSpec these say what
// the water and its edge ARE — how far the wind has blown over the water, how deep it is, how steep the
// beach is and what it is made of, what the hull is built of — and ShoreSynth works the sound out from
// that: the waves the wind raises over that fetch (WindWaves), how each one breaks and runs up the edge,
// and the bubbles, splashes, air pockets and stones that make the sound. The one level here,
// SourceLevelDb, is what the model was MEASURED to make (AudioLab --waves levels). See
// docs/WAVES_AND_SHORES.md.

/// <summary>The shape of the water the waves are raised over, which decides how the fetch follows the
/// wind's direction.</summary>
public enum WaterBody
{
    /// <summary>A pond or a lake: about as wide one way as another. A wind at an angle φ to the line
    /// straight out from the shore has the chord of a circle to blow over, F cos φ.</summary>
    Round,
    /// <summary>A river or a canal: a strip, its width straight out from the bank. A wind at an angle
    /// blows over the strip slantwise, F / cos φ, until the reach runs out.</summary>
    Strip,
    /// <summary>The open sea: the fetch is the spec's, whatever the angle, and the swell comes whatever
    /// the wind.</summary>
    Open,
}

/// <summary>What the waves meet at the edge.</summary>
public enum ShoreFace
{
    /// <summary>A sloping beach or bank: the waves shoal, break, run up it and run back.</summary>
    Beach,
    /// <summary>A steep face of rock, stone or concrete: the waves are thrown back, slap it, and close
    /// on air in its cracks and under its overhangs.</summary>
    Wall,
    /// <summary>A boat's side: a wall that rings (<see cref="HullSpec"/>).</summary>
    Hull,
}

/// <summary>What a beach is made of.</summary>
public enum ShoreSediment
{
    /// <summary>Sand: drains the swash into itself and lets out the air it held as a fizz.</summary>
    Sand,
    /// <summary>Mud, clay or a grassed bank: too tight to drain, too soft to rattle.</summary>
    Mud,
    /// <summary>Gravel, a few millimetres to a couple of centimetres: rolls and ticks in the backwash.</summary>
    Gravel,
    /// <summary>Shingle, pebbles of several centimetres: the backwash's rattle and roar.</summary>
    Shingle,
    /// <summary>Boulders and bedrock: nothing moves; the water breaks round them and closes on air.</summary>
    Rock,
}

/// <summary>
/// A boat's side as a plate the slaps ring (RainPlate's physics, with the stuff given here since the
/// material registry has no aluminium or fibreglass): one bay of skin between frames, wetted on its
/// lower part, which loads it with water's mass and lowers its notes.
/// </summary>
public sealed record HullSpec
{
    [Tunable("", 0, 0, "What this hull is called.")]
    public string Name { get; init; } = "";
    /// <summary>The skin, kg/m³, GPa, and its own loss factor before it is fastened to anything.</summary>
    [Tunable("kg/m³", 100, 9000, "The skin material's density.", Label = "density", Step = 10)]
    public float DensityKgM3 { get; init; } = 600f;
    [Tunable("GPa", 0.1, 250, "The skin material's stiffness. Stiffer rings higher.", Label = "Young's modulus", Step = 0.5)]
    public float YoungsModulusGPa { get; init; } = 8f;
    [Tunable("", 0.0001, 0.2, "The skin material's own loss factor, before it is fastened to anything.", Label = "material loss factor", Step = 0.001)]
    public float LossFactor { get; init; } = 0.02f;
    /// <summary>The skin's thickness, m.</summary>
    [Tunable("m", 0.0005, 0.1, "The skin's thickness. Thinner rings lower.", Label = "skin thickness", Step = 0.0005)]
    public float SkinMetres { get; init; } = 0.012f;
    /// <summary>One bay between frames (along the hull) and between chine and gunwale (up it), m.</summary>
    [Tunable("m", 0.05, 3, "One bay of skin between frames, along the hull.", Label = "bay length", Step = 0.01)]
    public float BayAlongMetres { get; init; } = 0.3f;
    [Tunable("m", 0.05, 3, "One bay of skin between chine and gunwale, up the hull.", Label = "bay height", Step = 0.01)]
    public float BayUpMetres { get; init; } = 0.45f;
    /// <summary>The loss factor the frames, fastenings and joints add, as a built panel has
    /// (PanelAcoustics.MountedLoss).</summary>
    [Tunable("", 0, 0.2, "The loss the frames, fastenings and joints add, as a built panel has.", Label = "mounted loss factor", Step = 0.005)]
    public float MountedLossFactor { get; init; } = PanelAcoustics.MountedLoss;
    /// <summary>How much of the bay is under water: the share loaded with the water's mass.</summary>
    [Tunable("", 0, 1, "How much of the bay is under water. The water's mass lowers its notes.", Step = 0.05)]
    public float WettedShare { get; init; } = 0.4f;
    /// <summary>The bays along the waterline that one place's slaps strike.</summary>
    [Tunable("", 1, 20, "The bays along the waterline that one place's slaps strike.", Step = 1)]
    public int Bays { get; init; } = 3;
    /// <summary>How far the hull's flare and the lands of its planks stand out over the water, m: the
    /// pocket of air a crest closes on under them is about this big, whatever the wave (a bow's flare
    /// several centimetres, a clinker plank's land one or two).</summary>
    [Tunable("m", 0.005, 0.3, "How far the hull's flare and the lands of its planks stand out over the water: the size of the air pocket a crest closes on under them.", Label = "flare pocket size", Step = 0.005)]
    public float FlarePocketMetres { get; init; } = 0.04f;

    /// <summary>As a RainPlate, so the modes, the radiation efficiency and the loss are the rain's.</summary>
    [JsonIgnore]
    public RainPlate Plate => new(Name, SkinMetres, BayAlongMetres, BayUpMetres, MountedLossFactor + LossFactor)
    {
        Custom = new MaterialProperties { DensityKgM3 = DensityKgM3, YoungsModulusGPa = YoungsModulusGPa, LossFactor = LossFactor },
    };

    /// <summary>The added mass per square metre of one face wetted by water, for a bending mode of
    /// wavenumber k: ρw / k (a plate in a fluid half-space; Lamb 1920, Junger and Feit 1986).</summary>
    public static float AddedMass(float wavenumber) => 1000f / MathF.Max(1f, wavenumber);

    /// <summary>A bay's (m, n) mode in place, Hz: the dry plate's note lowered by the water's mass on
    /// the wetted share, f √(m″ / (m″ + w ρw / k)).</summary>
    public float WetModeHz(int m, int n)
    {
        var plate = Plate;
        float k = MathF.PI * MathF.Sqrt(m * m / (BayAlongMetres * BayAlongMetres) + n * n / (BayUpMetres * BayUpMetres));
        float m2 = plate.SurfaceDensity;
        return plate.ModeHz(m, n) * MathF.Sqrt(m2 / (m2 + Math.Clamp(WettedShare, 0f, 1f) * AddedMass(k)));
    }
}

/// <summary>
/// The geometry one shore source stands for on the map: how far the water reaches straight out from it
/// (the fetch at a wind blowing straight onshore), which way the water lies from it, and how long a
/// stretch of the edge it is. tools/gen_osm.py works these out from the real outline of the water and
/// writes them as the source's box: X its length along the edge, Z the fetch, its +Z turned to the water.
/// </summary>
public readonly record struct ShoreGeometry(float FetchMetres, float WaterBearingDegrees, float LengthMetres);

/// <summary>Waves at an edge as a parts list. See the file's head and docs/WAVES_AND_SHORES.md.</summary>
public sealed record ShoreSpec
{
    [Tunable("", 0, 0, "What this shore is called.")]
    public string Name { get; init; } = "";

    // ── The water ───────────────────────────────────────────────────────────────────────────────

    [Tunable("", 0, 0, "The shape of the water: a round pond or lake, a strip such as a river, or the open sea. It decides how the fetch follows the wind's direction.", Label = "water body")]
    public WaterBody Body { get; init; } = WaterBody.Round;
    /// <summary>How far the water reaches straight out from this edge, m: the fetch of an onshore
    /// wind. The map's own (ShoreGeometry) wins where the map has one; this is the lab's.</summary>
    [Tunable("m", 1, 200000, "How far the water reaches straight out from this edge: the fetch of an onshore wind. A longer fetch raises bigger waves. The map's own geometry wins where it has one.", Step = 10)]
    public float FetchMetres { get; init; } = 300f;
    /// <summary>A strip's length along the wind, m: how far a wind blowing along a river has to work.</summary>
    [Tunable("m", 10, 200000, "A strip's length along the wind: how far a wind blowing along a river has to work.", Step = 100)]
    public float ReachMetres { get; init; } = 2000f;
    /// <summary>How deep the water is offshore, m: shallow water holds the waves down.</summary>
    [Tunable("m", 0.1, 1000, "How deep the water is offshore. Shallow water holds the waves down.", Label = "water depth", Step = 0.5)]
    public float DepthMetres { get; init; } = 3f;
    /// <summary>A river's current past the bank, m/s.</summary>
    [Tunable("m/s", 0, 5, "A river's current past the bank.", Label = "current", Step = 0.05)]
    public float CurrentMetresPerSecond { get; init; }
    /// <summary>The size of what stands out of the bank into the current (roots, snags, riprap), m:
    /// the eddies it sheds rock the water at the bank (a Strouhal number of 0.2).</summary>
    [Tunable("m", 0.05, 10, "The size of what stands out of the bank into the current: roots, snags, riprap. The eddies it sheds rock the water at the bank.", Label = "bank feature size", Step = 0.05)]
    public float BankFeatureMetres { get; init; } = 0.5f;
    /// <summary>Swell from far away: its significant height (m) and peak period (s) as it arrives, and
    /// the angle its crests make with the shore (degrees off straight onshore). Zero: none.</summary>
    [Tunable("m", 0, 10, "The significant height of swell arriving from far away. Zero: none.", Label = "swell height", Step = 0.05)]
    public float SwellHeightMetres { get; init; }
    [Tunable("s", 2, 25, "The swell's peak period as it arrives.", Label = "swell period", Step = 0.5)]
    public float SwellPeriodSeconds { get; init; } = 9f;
    [Tunable("degrees", -85, 85, "The angle the swell's crests make with the shore, off straight onshore.", Label = "swell angle", Step = 1)]
    public float SwellAngleDegrees { get; init; } = 10f;

    // ── The edge ────────────────────────────────────────────────────────────────────────────────

    [Tunable("", 0, 0, "What the waves meet: a sloping beach, a steep face of rock or concrete, or a boat's side.")]
    public ShoreFace Face { get; init; } = ShoreFace.Beach;
    [Tunable("", 0, 0, "What a beach is made of: sand, mud, gravel, shingle, or rock.")]
    public ShoreSediment Sediment { get; init; } = ShoreSediment.Sand;
    /// <summary>The beach's slope, tan β: about 0.02 for a wide sandy sea beach, 0.1 for a lake's, 0.15
    /// for shingle, 0.3 for a steep bank.</summary>
    [Tunable("", 0.005, 1, "The beach's slope, rise over run: about 0.02 for a wide sandy sea beach, 0.1 for a lake's, 0.15 for shingle, 0.3 for a steep bank. On a steeper beach waves plunge rather than spill.", Step = 0.005)]
    public float BeachSlope { get; init; } = 0.08f;
    /// <summary>The middling stone's diameter, mm (gravel, shingle; the boulders of a rocky edge).</summary>
    [Tunable("mm", 0.5, 3000, "The middling stone's diameter: gravel, shingle, or the boulders of a rocky edge.", Label = "stone size", Step = 1)]
    public float StoneMm { get; init; } = 30f;
    /// <summary>How wide a belt of reeds stands in the water before the edge, m: the stems take the
    /// waves' energy before they arrive.</summary>
    [Tunable("m", 0, 100, "How wide a belt of reeds stands in the water before the edge. The stems take the waves' energy before they arrive.", Label = "reed belt width", Step = 0.5)]
    public float ReedBeltMetres { get; init; }
    /// <summary>The share of crests that close on a pocket of air where they meet the edge: cracks,
    /// gaps between boulders, an overhang, the flare of a hull. A smooth wall a few in a hundred; a
    /// jumble of rocks or a boat's bow most of them.</summary>
    [Tunable("", 0, 1, "The share of crests that close on a pocket of air where they meet the edge: a few in a hundred for a smooth wall, most of them for a jumble of rocks or a boat's bow.", Label = "air trap share", Step = 0.01)]
    public float TrapShare { get; init; } = 0.1f;
    /// <summary>The boat's side, for <see cref="ShoreFace.Hull"/>.</summary>
    public HullSpec? Hull { get; init; }

    // ── How it is heard ─────────────────────────────────────────────────────────────────────────

    /// <summary>The wind its level was measured at, m/s at 10 m, blowing straight onshore over
    /// <see cref="FetchMetres"/>.</summary>
    [Tunable("m/s", 1.5, 30, "The wind the source level was measured at, at 10 m, blowing straight onshore over the fetch. Change it only with a new measurement.", Label = "reference wind", Step = 0.5)]
    public float ReferenceWind { get; init; } = 5f;
    /// <summary>Overall level at one metre, dB, the whole source as if at one point: MEASURED with
    /// <c>--waves levels</c> at <see cref="ReferenceWind"/>.</summary>
    [Tunable("dB", 10, 130, "Overall level at one metre, the whole source as if at one point, measured with --waves levels at the reference wind. Change it only after measuring the model again.", Step = 0.5, Source = "MEASURED with --waves levels")]
    public required float SourceLevelDb { get; init; }
    /// <summary>How far its loudest moments stand over the level, dB (99.9th percentile of 10 ms peaks).</summary>
    public float PeakHeadroomDb { get; init; } = 16f;
    /// <summary>How long a stretch of the edge one source is, m (the map's own wins).</summary>
    [Tunable("m", 1, 1000, "How long a stretch of the edge one source is. The map's own geometry wins where it has one.", Label = "edge length", Step = 1)]
    public float LengthMetres { get; init; } = 20f;
    /// <summary>How many places along it it is heard from (ExtendedSources), its middle included.</summary>
    public int Places { get; init; } = 5;
    /// <summary>Where the waves break, m out from the edge, at the reference sea: surf breaking that far
    /// out is heard from a second row of places out there. Zero: they break at the edge.</summary>
    [Tunable("m", 0, 500, "Where the waves break, out from the edge, at the reference sea. Surf breaking that far out is also heard from a row of places out there. Zero: they break at the edge.", Label = "break line distance", Step = 1)]
    public float BreakRowMetres { get; init; }
    /// <summary>How big it is, m: the voice is flat inside it.</summary>
    [Tunable("m", 0.2, 50, "How big it is. Inside it the sound is flat.", Step = 0.5)]
    public float ExtentMetres { get; init; } = 4f;

    /// <summary>Every place: along the edge, and as many again on the break line if it has one.</summary>
    [JsonIgnore]
    public int TotalPlaces => Math.Max(1, Places) * (BreakRowMetres > 0f ? 2 : 1);

    // ── The map's key ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The voice's key for one source on a map: the prefab's "shore:&lt;preset&gt;" with the source's own
    /// geometry, "shore:&lt;preset&gt;/&lt;fetch m&gt;/&lt;bearing of the water, degrees&gt;/&lt;length m&gt;". The
    /// source's box is its length (x) and its fetch (z), its +Z turned toward the water.
    /// </summary>
    public static string KeyFor(string soundId, Vector3 boxSize, Quaternion rotation)
    {
        var water = Vector3.Transform(Vector3.UnitZ, rotation);
        float bearing = MathF.Atan2(water.X, water.Z) * (180f / MathF.PI);
        if (bearing < 0f) bearing += 360f;
        return string.Create(CultureInfo.InvariantCulture,
            $"{soundId}/{MathF.Round(MathF.Max(1f, boxSize.Z))}/{MathF.Round(bearing) % 360}/{MathF.Round(MathF.Max(1f, boxSize.X))}");
    }

    /// <summary>The preset and, if the key carries them, the source's own geometry (else the spec's,
    /// facing north).</summary>
    public static bool ParseKey(string key, out string preset, out ShoreGeometry? geometry)
    {
        geometry = null;
        preset = "";
        if (string.IsNullOrEmpty(key)) return false;
        string rest = key.StartsWith("shore:", StringComparison.OrdinalIgnoreCase) ? key[6..] : key;
        var parts = rest.Split('/');
        preset = parts[0];
        if (parts.Length == 4
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float fetch)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float bearing)
            && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float length))
            geometry = new ShoreGeometry(fetch, bearing, length);
        return preset.Length > 0;
    }

    /// <summary>The spec's own geometry: its fetch, the water to the north, its length.</summary>
    [JsonIgnore]
    public ShoreGeometry DefaultGeometry => new(FetchMetres, 0f, LengthMetres);

    // ── The sea now ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The wind sea arriving at this edge for a wind of <paramref name="windSpeed"/> (m/s at 10 m) blowing
    /// FROM <paramref name="windFromDegrees"/>: its significant height and peak period as they reach the
    /// edge, and the share of the wind that is onshore (cos φ). The fetch follows the angle (see
    /// <see cref="WaterBody"/>); the height is scaled by √cos φ, the energy a wave train carries across a
    /// shore it meets at φ (refraction turns it toward the shore and keeps that flux). A reed belt takes
    /// its share (<see cref="WindWaves.ReedDamping"/>).
    /// </summary>
    public (float Hs, float Tp, float Onshore) WindSea(ShoreGeometry g, float windSpeed, float windFromDegrees)
    {
        float phi = (windFromDegrees - g.WaterBearingDegrees) * (MathF.PI / 180f);
        float cos = MathF.Cos(phi);
        if (cos <= 0.02f || windSpeed <= WindWaves.OnsetWind) return (0f, 0f, MathF.Max(0f, cos));
        float fetch = Body switch
        {
            WaterBody.Round => g.FetchMetres * cos,
            WaterBody.Strip => MathF.Min(ReachMetres, g.FetchMetres / cos),
            _ => g.FetchMetres,
        };
        var (hs, tp) = WindWaves.FetchLimited(windSpeed, MathF.Max(1f, fetch), DepthMetres);
        hs *= MathF.Sqrt(cos) * WindWaves.ReedDamping(ReedBeltMetres, hs);
        return (hs, tp, cos);
    }

    /// <summary>Calm water: no wind to raise waves, no swell and no current. Gives no voice.</summary>
    public bool CalmAt(float windSpeed) => windSpeed <= WindWaves.OnsetWind && SwellHeightMetres <= 0f && CurrentMetresPerSecond <= 0.05f;

    // ── The presets ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A lake's sandy beach: a kilometre of open water in front of it, three metres deep, a beach of 1 in
    /// 12. A 5 m/s breeze straight onshore raises 7 cm waves at 0.8 s; on that slope they spill (an
    /// Iribarren number of 0.3) in the last decimetre, the bigger ones of each group folding a little air
    /// under, and run up the sand a few centimetres; the sand drinks the swash and lets out its air.
    /// </summary>
    public static ShoreSpec LakeSand => new()
    {
        Name = "Lake, sandy beach",
        Body = WaterBody.Round,
        FetchMetres = 1000f,
        DepthMetres = 3f,
        Face = ShoreFace.Beach,
        Sediment = ShoreSediment.Sand,
        BeachSlope = 1f / 12f,
        TrapShare = 0.03f,
        // MEASURED with `--waves levels sec=60`, 2026-10-06: Leq 66.9 dB at a metre at 5 m/s onshore; 10 ms peaks' 99.9th percentile 18.2 dB over.
        SourceLevelDb = 67f,
        PeakHeadroomDb = 18.5f,
        LengthMetres = 20f,
        Places = 5,
        ExtentMetres = 4f,
    };

    /// <summary>
    /// A lake's rocky edge: boulders and a broken stone wall at the water, a kilometre of fetch, the water
    /// deep against it. The waves do not break; they are thrown back off the stone and slap it, and
    /// half of them close on air in the gaps between the stones: the clop.
    /// </summary>
    public static ShoreSpec LakeRock => new()
    {
        Name = "Lake, rocky edge",
        Body = WaterBody.Round,
        FetchMetres = 1000f,
        DepthMetres = 3f,
        Face = ShoreFace.Wall,
        Sediment = ShoreSediment.Rock,
        StoneMm = 400f,
        TrapShare = 0.5f,
        // MEASURED with `--waves levels sec=60`, 2026-10-06: Leq 63.9 dB at a metre at 5 m/s onshore; 10 ms peaks' 99.9th percentile 20.1 dB over.
        SourceLevelDb = 64f,
        PeakHeadroomDb = 20.5f,
        LengthMetres = 20f,
        Places = 5,
        ExtentMetres = 4f,
    };

    /// <summary>
    /// A pond's bank: 150 m of water, a metre and a half deep, a grassed muddy bank of 1 in 5. The kind
    /// of water a park or a housing estate has: small wavelets in a breeze, lapping and soaking into the
    /// grass, almost silent in a light air.
    /// </summary>
    public static ShoreSpec PondBank => new()
    {
        Name = "Pond, grassy bank",
        Body = WaterBody.Round,
        FetchMetres = 150f,
        DepthMetres = 1.5f,
        Face = ShoreFace.Beach,
        Sediment = ShoreSediment.Mud,
        BeachSlope = 0.2f,
        TrapShare = 0.1f,
        // MEASURED with `--waves levels sec=60`, 2026-10-06: Leq 51.4 dB at a metre at 5 m/s onshore; 10 ms peaks' 99.9th percentile 20.5 dB over.
        SourceLevelDb = 51.5f,
        PeakHeadroomDb = 20.5f,
        LengthMetres = 20f,
        Places = 5,
        ExtentMetres = 4f,
    };

    /// <summary>
    /// A pond's edge through a belt of reeds or rushes three metres wide: the stems take most of the
    /// waves before they reach the mud.
    /// </summary>
    public static ShoreSpec ReedShore => PondBank with
    {
        Name = "Pond, reedy edge",
        ReedBeltMetres = 3f,
        // MEASURED with `--waves levels sec=60`, 2026-10-06: Leq 46.2 dB at a metre at 5 m/s onshore; 10 ms peaks' 99.9th percentile 22.0 dB over.
        SourceLevelDb = 46f,
        PeakHeadroomDb = 22f,
    };

    /// <summary>
    /// A big lowland river's bank: 120 m across, three metres deep, the current 0.8 m/s past a muddy
    /// bank of 1 in 3 with roots and riprap standing into it. The current's eddies off the bank's
    /// features rock the water at the edge every few seconds whatever the wind; a wind over the river
    /// raises a chop that laps it, more when it blows along the reach.
    /// </summary>
    public static ShoreSpec RiverBank => new()
    {
        Name = "River bank",
        Body = WaterBody.Strip,
        FetchMetres = 120f,
        ReachMetres = 2000f,
        DepthMetres = 3f,
        CurrentMetresPerSecond = 0.8f,
        BankFeatureMetres = 0.5f,
        Face = ShoreFace.Beach,
        Sediment = ShoreSediment.Mud,
        BeachSlope = 0.33f,
        TrapShare = 0.3f,
        // MEASURED with `--waves levels sec=60`, 2026-10-06: Leq 48.6 dB at a metre at 5 m/s onshore; 10 ms peaks' 99.9th percentile 19.7 dB over.
        SourceLevelDb = 48.5f,
        PeakHeadroomDb = 20f,
        LengthMetres = 20f,
        Places = 5,
        ExtentMetres = 4f,
    };

    /// <summary>
    /// Sea surf on a sandy beach: a metre of swell at 9 s from far out, a 5 m/s onshore breeze over open
    /// sea on top, a beach of 1 in 30. The swell breaks 35 m out, spilling and plunging (an Iribarren
    /// number near 0.4), and its bore runs in and up the sand. Heard from places along the edge and as
    /// many on the break line.
    /// </summary>
    public static ShoreSpec SeaSand => new()
    {
        Name = "Sea surf, sandy beach",
        Body = WaterBody.Open,
        FetchMetres = 50000f,
        DepthMetres = 20f,
        SwellHeightMetres = 1.0f,
        SwellPeriodSeconds = 9f,
        SwellAngleDegrees = 10f,
        Face = ShoreFace.Beach,
        Sediment = ShoreSediment.Sand,
        BeachSlope = 1f / 30f,
        TrapShare = 0.02f,
        // MEASURED with `--waves levels sec=60`, 2026-10-06: Leq 84.9 dB at a metre at 5 m/s onshore; 10 ms peaks' 99.9th percentile 22.5 dB over.
        SourceLevelDb = 85f,
        PeakHeadroomDb = 22.5f,
        LengthMetres = 30f,
        Places = 5,
        BreakRowMetres = 35f,
        ExtentMetres = 8f,
    };

    /// <summary>
    /// A shingle beach: 0.6 m of swell at 7 s onto a steep bank of 1 in 6 of pebbles about 3 cm across.
    /// The waves plunge and collapse at its foot and the backwash drags the pebbles down in a rattle.
    /// </summary>
    public static ShoreSpec Shingle => new()
    {
        Name = "Sea, shingle beach",
        Body = WaterBody.Open,
        FetchMetres = 50000f,
        DepthMetres = 10f,
        SwellHeightMetres = 0.6f,
        SwellPeriodSeconds = 7f,
        SwellAngleDegrees = 10f,
        Face = ShoreFace.Beach,
        Sediment = ShoreSediment.Shingle,
        BeachSlope = 1f / 6f,
        StoneMm = 30f,
        TrapShare = 0.05f,
        // MEASURED with `--waves levels sec=60`, 2026-10-06: Leq 93.3 dB at a metre at 5 m/s onshore; 10 ms peaks' 99.9th percentile 25.7 dB over.
        // Again once the wind sea's waves were found on their own (ShoreSynth.Seas): Leq 92.3 dB, peaks 19.9 dB over.
        SourceLevelDb = 92.5f,
        PeakHeadroomDb = 26f,
        LengthMetres = 20f,
        Places = 5,
        ExtentMetres = 6f,
    };

    /// <summary>
    /// A harbour wall on its sea side: a breakwater's face of stone blocks in five metres of water, the
    /// open sea in front (20 km to windward) and a 40 cm swell at 7 s coming in at 20 degrees. The waves
    /// do not break; they are thrown back off the face, standing up to twice their height against it,
    /// thrown up as sheets that fall back, and caught on air in the joints between the blocks.
    /// </summary>
    public static ShoreSpec HarbourWall => new()
    {
        Name = "Harbour wall",
        Body = WaterBody.Open,
        FetchMetres = 20000f,
        DepthMetres = 5f,
        SwellHeightMetres = 0.4f,
        SwellPeriodSeconds = 7f,
        SwellAngleDegrees = 20f,
        Face = ShoreFace.Wall,
        Sediment = ShoreSediment.Rock,
        StoneMm = 600f,
        TrapShare = 0.25f,
        // MEASURED with `--waves levels sec=60`, 2026-10-06: Leq 64.7 dB at a metre at 5 m/s onshore; 10 ms peaks' 99.9th percentile 22.4 dB over.
        // Again once the wind sea's waves were found on their own (ShoreSynth.Seas): Leq 65.8 dB, peaks 21.9 dB over.
        SourceLevelDb = 66f,
        PeakHeadroomDb = 22.5f,
        LengthMetres = 20f,
        Places = 5,
        ExtentMetres = 4f,
    };

    /// <summary>
    /// Wavelets against a moored wooden boat: a clinker-built rowing boat of 12 mm planks between ribs a
    /// foot apart, on a lake with 500 m of water to windward. Each crest strikes the planking at the
    /// waterline and, under the flare of the bow and the lands of the planks, closes on air; the pocket
    /// rings and drives the planking at its own note. Lab only: there are no boats yet.
    /// </summary>
    public static ShoreSpec HullWood => new()
    {
        Name = "Moored boat, wooden hull",
        Body = WaterBody.Round,
        FetchMetres = 500f,
        DepthMetres = 3f,
        Face = ShoreFace.Hull,
        TrapShare = 0.45f,
        Hull = new HullSpec
        {
            Name = "Wooden planking",
            DensityKgM3 = 500f, YoungsModulusGPa = 8f, LossFactor = 0.02f,
            SkinMetres = 0.012f, BayAlongMetres = 0.3f, BayUpMetres = 0.4f,
            MountedLossFactor = 0.03f, WettedShare = 0.4f, Bays = 3,
        },
        // MEASURED with `--waves levels sec=60`, 2026-10-06: Leq 60.1 dB at a metre at 5 m/s onshore; 10 ms peaks' 99.9th percentile 23.8 dB over.
        SourceLevelDb = 60f,
        PeakHeadroomDb = 24f,
        LengthMetres = 4f,
        Places = 3,
        ExtentMetres = 2f,
    };

    /// <summary>The same boat in aluminium: a 2 mm welded skin (5052 alloy) between frames 0.4 m apart.</summary>
    public static ShoreSpec HullAluminium => HullWood with
    {
        Name = "Moored boat, aluminium hull",
        Hull = new HullSpec
        {
            Name = "Aluminium skin",
            DensityKgM3 = 2680f, YoungsModulusGPa = 70f, LossFactor = 0.002f,
            SkinMetres = 0.002f, BayAlongMetres = 0.4f, BayUpMetres = 0.45f,
            MountedLossFactor = 0.02f, WettedShare = 0.4f, Bays = 3,
        },
        // MEASURED with `--waves levels sec=60`, 2026-10-06: Leq 60.8 dB at a metre at 5 m/s onshore; 10 ms peaks' 99.9th percentile 22.9 dB over.
        SourceLevelDb = 61f,
        PeakHeadroomDb = 23f,
    };

    public static IReadOnlyDictionary<string, Func<ShoreSpec>> Presets { get; } =
        new Dictionary<string, Func<ShoreSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["lake_sand"] = () => LakeSand,
            ["lake_rock"] = () => LakeRock,
            ["pond_bank"] = () => PondBank,
            ["reed_shore"] = () => ReedShore,
            ["river_bank"] = () => RiverBank,
            ["sea_sand"] = () => SeaSand,
            ["shingle"] = () => Shingle,
            ["harbour_wall"] = () => HarbourWall,
            ["hull_wood"] = () => HullWood,
            ["hull_aluminium"] = () => HullAluminium,
        };

    /// <summary>A preset by name, through the <see cref="ModelLibrary"/> so a map's own wins. A map's
    /// key with its geometry ("shore:lake_sand/300/90/20") names its preset.</summary>
    public static ShoreSpec ByName(string key)
    {
        ParseKey(key, out string preset, out _);
        return ModelLibrary.Shore(preset);
    }
}

/// <summary>
/// Wind waves: how high and how long the waves a wind raises over a fetch are, and what they do at a
/// beach. The engineering relations every coastal and lake study uses (docs/WAVES_AND_SHORES.md
/// section 1).
/// </summary>
public static class WindWaves
{
    public const float Gravity = 9.81f;

    /// <summary>Under this wind (m/s at 10 m) the water stays glassy: wind waves need a friction velocity
    /// of a few centimetres a second to start (Kahma and Donelan 1988).</summary>
    public const float OnsetWind = 1.5f;

    /// <summary>The ten-metre drag coefficient, C_D = 0.001 (1.1 + 0.035 U10) (CEM II-2, after Wu 1980).</summary>
    public static float DragCoefficient(float u10) => 0.001f * (1.1f + 0.035f * MathF.Max(0f, u10));

    /// <summary>u* = √C_D U10, m/s.</summary>
    public static float FrictionVelocity(float u10) => MathF.Sqrt(DragCoefficient(u10)) * MathF.Max(0f, u10);

    /// <summary>
    /// Fetch-limited growth in deep water, JONSWAP's own laws in the ten-metre wind (Hasselmann et al.
    /// 1973, eqs. 2.4.3 and 2.4.7, χ = g F / U²): g Hm0 / U² = 1.6e-3 χ^½ and g Tp / U = 0.286 χ^0.33, held
    /// to the fully developed sea of the Coastal Engineering Manual (Part II-2, eq. II-2-37: g Hm0 / u*² =
    /// 211.5, g Tp / u* = 239.8). The CEM's own growth laws in u* (II-2-36) give a period a quarter
    /// shorter at a lake's fetch, so waves twice as steep, and steepness decides how they break; the
    /// original is kept. In shallow water held to Young and Verhagen's (1996) depth limits as well. Below
    /// the onset the water is flat; the height ramps in over the metre a second above it.
    /// </summary>
    public static (float Hm0, float Tp) FetchLimited(float u10, float fetchMetres, float depthMetres = float.PositiveInfinity)
    {
        if (u10 <= OnsetWind || fetchMetres <= 0f) return (0f, 0f);
        float us = FrictionVelocity(u10);
        float g = Gravity, u2 = u10 * u10;
        float chi = g * MathF.Max(1f, fetchMetres) / u2;
        float h = MathF.Min(1.6e-3f * MathF.Sqrt(chi) * u2 / g, 211.5f * us * us / g);
        float t = MathF.Min(0.286f * MathF.Pow(chi, 0.33f) * u10 / g, 239.8f * us / g);
        if (float.IsFinite(depthMetres) && depthMetres > 0f)
        {
            float d = g * depthMetres / (u10 * u10);
            float hMax = 0.241f * MathF.Pow(MathF.Tanh(0.493f * MathF.Pow(d, 0.75f)), 0.87f) * u10 * u10 / g;
            float tMax = 7.519f * MathF.Pow(MathF.Tanh(0.331f * MathF.Pow(d, 1.01f)), 0.37f) * u10 / g;
            h = MathF.Min(h, hMax);
            t = MathF.Min(t, tMax);
        }
        float ramp = Math.Clamp(u10 - OnsetWind, 0f, 1f);
        return (h * ramp, t);
    }

    /// <summary>How long a wind must blow for its sea to reach the fetch-limited state, s: t = 77.23
    /// F^0.67 / (U^0.34 g^0.33), U the wind speed (CEM II-2-35). The sea follows the wind with about a
    /// third of this.</summary>
    public static float GrowthSeconds(float u10, float fetchMetres)
        => 77.23f * MathF.Pow(MathF.Max(1f, fetchMetres), 0.67f) / (MathF.Pow(MathF.Max(u10, OnsetWind + 0.5f), 0.34f) * MathF.Pow(Gravity, 0.33f));

    /// <summary>The deep-water wavelength for a period, m: g T² / 2π.</summary>
    public static float DeepWavelength(float periodSeconds) => Gravity * periodSeconds * periodSeconds / (2f * MathF.PI);

    /// <summary>The Iribarren (surf similarity) number ξ0 = tan β / √(H0 / L0) (Battjes 1974): under 0.5
    /// a wave spills, 0.5-3.3 it plunges, over 3.3 it collapses or surges up the slope unbroken.</summary>
    public static float Iribarren(float slope, float heightMetres, float periodSeconds)
    {
        float l0 = DeepWavelength(MathF.Max(0.1f, periodSeconds));
        return MathF.Max(0f, slope) / MathF.Sqrt(MathF.Max(1e-5f, heightMetres) / l0);
    }

    /// <summary>The height a wave breaks at, m: Komar and Gaughan (1972), Hb = 0.39 g^⅕ (T H0²)^⅖.</summary>
    public static float BreakerHeight(float heightMetres, float periodSeconds)
        => 0.39f * MathF.Pow(Gravity, 0.2f) * MathF.Pow(MathF.Max(1e-6f, periodSeconds * heightMetres * heightMetres), 0.4f);

    /// <summary>The depth it breaks in, m: Hb / 0.78 (McCowan 1894).</summary>
    public static float BreakerDepth(float breakerHeight) => breakerHeight / 0.78f;

    /// <summary>How far up the slope the swash runs, m (vertically): Hunt (1959), R = ξ0 H0 while the wave
    /// breaks, and no more than about 2.3 H0 when it surges unbroken (Battjes 1974).</summary>
    public static float RunUp(float slope, float heightMetres, float periodSeconds)
        => heightMetres * MathF.Min(Iribarren(slope, heightMetres, periodSeconds), 2.3f);

    /// <summary>
    /// The share of the dominant waves that break in open water (whitecaps), from the peak's steepness
    /// ε = Hp kp / 2, Hp four times the root of the variance between 0.7 and 1.3 fp: 0.87 Hs for a JONSWAP
    /// sea of γ 3.3. b = 22 (ε − 0.055)^2.01 over the threshold of 0.055 (Banner, Babanin and Young 2000;
    /// the threshold read in the paper, the fit's coefficients as commonly quoted). No whitecaps under
    /// 3 m/s (Monahan and Ó Muircheartaigh 1980).
    /// </summary>
    public static float BreakingProbability(float hs, float tp, float u10)
    {
        if (hs <= 0f || tp <= 0f || u10 < 3f) return 0f;
        float kp = 2f * MathF.PI / DeepWavelength(tp);
        float eps = 0.87f * hs * kp / 2f;
        return eps <= 0.055f ? 0f : MathF.Min(0.5f, 22f * MathF.Pow(eps - 0.055f, 2.01f));
    }

    /// <summary>
    /// What a belt of reeds leaves of a wave, H / H0, after <paramref name="beltMetres"/>: Dalrymple et
    /// al.'s (1984) vegetation decay H/H0 = 1 / (1 + β̃ x), its coefficient β̃ going with the wave height
    /// (drag on the stems goes as the orbital velocity squared). Taken as β̃ = 4 H0 per m² for a dense
    /// emergent stand of rushes, an estimate within the range Mendez and Losada (2004) fit.
    /// </summary>
    public static float ReedDamping(float beltMetres, float heightMetres)
        => beltMetres <= 0f ? 1f : 1f / (1f + 4f * MathF.Max(0f, heightMetres) * beltMetres);
}
