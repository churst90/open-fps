using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenFPS.Common;

/// <summary>What is burning, which decides what besides the flames and the crackle is heard
/// (docs/FIRE.md section 6).</summary>
public enum FireFuel
{
    /// <summary>Logs on a hearth or in a pile: steam jets out of the log ends, logs giving way and
    /// settling, embers ticking on what is round it.</summary>
    Logs,
    /// <summary>Standing trees: foliage and twigs torching in flares, burnt branches falling through the
    /// crown to the ground.</summary>
    Trees,
    /// <summary>A crown fire's front running through a forest's canopy: as trees, its heat release
    /// following the wind (the front's rate of spread), dead trees coming down behind it.</summary>
    Crown,
    /// <summary>A building: timber cracking, windows cracking in the heat and falling out, rooms
    /// flaring as the air gets in, ceilings and the roof coming down.</summary>
    Structure,
    /// <summary>A car: plastics spitting, tyres and gas struts and bumper absorbers bursting, the side
    /// windows dicing, the fuel tank giving way in a flare.</summary>
    Vehicle,
}

/// <summary>
/// A fire, from a campfire to a forest's crown fire (docs/FIRE.md).
///
/// What it is made of decides what it sounds like. The FLAMES are buoyant plumes over the burning area:
/// each body of fire as wide as <see cref="BaseDiameterMetres"/> necks and breaks off a puff at a rate
/// set only by its width (about 1.5 / √D Hz), and its heat release is unsteady, which radiates as a
/// monopole: the low, fluttering roar. A fire much wider than one body is many of them side by side,
/// each puffing in its own time. The FUEL is wet inside: water and resin trapped in its cells boil, the
/// pressure builds until a cell wall gives, and the pocket bursts: the crackle, of every size, the big
/// ones throwing an ember. And each kind of fuel has its own events (<see cref="FireFuel"/>).
///
/// The fields say what the fire IS; <see cref="SourceLevelDb"/> and <see cref="PeakHeadroomDb"/> are
/// what the model was MEASURED to make (AudioLab --fire levels), so the mixer can place it.
/// </summary>
public sealed record FireSpec
{
    public string Name { get; init; } = "";
    /// <summary>What is burning.</summary>
    public FireFuel Fuel { get; init; } = FireFuel.Logs;
    /// <summary>The width of one body of fire that puffs as one, m: a hearth's bed, a bonfire's pile, a
    /// car, a tree's crown, the depth of a crown fire's flaming zone. Sets the puffing rate and the roar's
    /// pitch.</summary>
    public required float BaseDiameterMetres { get; init; }
    /// <summary>How wide the burning area is, m, across (x). Zero is one body, <see cref="BaseDiameterMetres"/>.</summary>
    public float WidthMetres { get; init; }
    /// <summary>How deep the burning area is, m, along (z). Zero is one body, <see cref="BaseDiameterMetres"/>.</summary>
    public float DepthMetres { get; init; }
    /// <summary>How hard it is burning when fully developed, kW: the whole fire.</summary>
    public required float HeatReleaseKw { get; init; }
    /// <summary>Moisture in the fuel, a fraction of its dry mass. Seasoned firewood is 0.15-0.20, green
    /// wood 0.4 and over; living foliage 0.8-1.2. More water is more crackle and more hiss.</summary>
    public float Moisture { get; init; } = 0.18f;
    /// <summary>How resinous the fuel is, 0 (a dense hardwood: oak, ash) to 1 (pine, spruce). Resin
    /// pockets are what pop loudest.</summary>
    public float Resin { get; init; } = 0.3f;
    /// <summary>How tall the flames stand, m: where the wind that fans them is taken.</summary>
    public float FlameHeightMetres { get; init; } = 0.8f;
    /// <summary>How high the burning fuel stands, m: the top of a pile, a crown, a roof. What falls,
    /// falls from under it.</summary>
    public float FuelHeightMetres { get; init; } = 0.3f;
    /// <summary>What is round it and under it, by material name: what embers tick on and what falls
    /// lands on.</summary>
    public string Surround { get; init; } = "Brick";
    /// <summary>How many places it is heard from, the middle included (ExtendedSources): at most 12. Nine
    /// stand for an area's spread at 1-4 kHz within 0.03 of a continuous one (docs/FIRE.md 7.2).</summary>
    public int Places { get; init; } = 9;
    /// <summary>Panes of glass in it (a building's windows, a car's side and rear windows): each cracks
    /// in the heat and, in a building, falls out later.</summary>
    public int Panes { get; init; }
    /// <summary>A pane's width and height, m, and its thickness, mm.</summary>
    public float PaneWidthMetres { get; init; } = 1f;
    public float PaneHeightMetres { get; init; } = 1.2f;
    public float PaneThicknessMm { get; init; } = 4f;
    /// <summary>How far a pane's bottom edge is above the ground, m.</summary>
    public float PaneDropMetres { get; init; } = 1f;
    /// <summary>Sealed gas containers that burst in the heat: a car's gas struts and bumper absorbers, its
    /// tyres. Each bursts at most once.</summary>
    public int Struts { get; init; }
    public int Tyres { get; init; }

    /// <summary>The fire's life, s, when it is lit at a known moment (<see cref="KeyFor"/>): growing as
    /// t² to its full heat release over <see cref="GrowthSeconds"/>, burning fully for
    /// <see cref="SteadySeconds"/>, dying over <see cref="DecaySeconds"/>, then smouldering. A fire with
    /// no known lighting (a map's) is always fully developed.</summary>
    public float GrowthSeconds { get; init; } = 120f;
    public float SteadySeconds { get; init; } = 3600f;
    public float DecaySeconds { get; init; } = 600f;

    /// <summary>Overall level at one metre, dB, fully developed, every place summed. MEASURED with
    /// <c>--fire levels</c>.</summary>
    public required float SourceLevelDb { get; init; }
    /// <summary>How far its loudest moments stand over <see cref="SourceLevelDb"/>, dB: the room its
    /// voice renders with (the 99.9th percentile of its 10 ms peaks, measured with --fire levels).
    /// Never under the fleet's shared 16.</summary>
    public float PeakHeadroomDb { get; init; } = 16f;
    /// <summary>How big the source is, m: inside it the level is flat.</summary>
    public float ExtentMetres { get; init; } = 0.6f;

    /// <summary>The burning area's width and depth, m.</summary>
    public float AreaWidth => WidthMetres > 0f ? WidthMetres : BaseDiameterMetres;
    public float AreaDepth => DepthMetres > 0f ? DepthMetres : BaseDiameterMetres;

    // ── Presets ──────────────────────────────────────────────────────────────────────────────────
    //
    // The sizes and heat releases are the fire science's (docs/FIRE.md section 3); the levels are the
    // model's own, measured.

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
        // MEASURED with `--fire levels` 2026-10-06, fully developed, every place summed: Leq 68.6 dB, 60.0 dB(A)
        // over ten minutes at 3 m/s, 66.0 dB in the field's wind (NatureTests); its 10 ms peaks' 99.9th percentile
        // 37 dB over, crest 42. Was 59.5 dB (57.2 dB(A)) with the roar a band at 70 / √D Hz; the roar is now dQ/dt's
        // tail from 20 Hz up, whose bottom octaves carry most of the difference.
        SourceLevelDb = 67f,
        PeakHeadroomDb = 45f,
        ExtentMetres = 0.5f,
    };

    /// <summary>A campfire: a few logs on the ground in a ring of stones, half a metre of flame.</summary>
    public static FireSpec Campfire => new()
    {
        Name = "Campfire, a few logs on bare ground",
        BaseDiameterMetres = 0.7f,
        HeatReleaseKw = 30f,
        Moisture = 0.2f,
        Resin = 0.4f,
        FlameHeightMetres = 0.6f,
        FuelHeightMetres = 0.3f,
        Surround = "Dirt",
        GrowthSeconds = 300f,
        SteadySeconds = 3600f,
        DecaySeconds = 1200f,
        // MEASURED with `--fire levels` 2026-10-06, fully developed, every place summed (wind 3 m/s): Leq 63.1 dB,
        // 56.4 dB(A); peaks 39 dB over, crest 44.
        SourceLevelDb = 63f,
        PeakHeadroomDb = 45f,
        ExtentMetres = 0.4f,
    };

    /// <summary>A bonfire: a pile of logs and brush three and a half metres across, burning hard.</summary>
    public static FireSpec Bonfire => new()
    {
        Name = "Bonfire, a pile of logs and brush",
        BaseDiameterMetres = 3.5f,
        HeatReleaseKw = 4000f,
        Moisture = 0.25f,
        Resin = 0.4f,
        FlameHeightMetres = 5f,
        FuelHeightMetres = 2f,
        Surround = "Dirt",
        GrowthSeconds = 600f,
        SteadySeconds = 3600f,
        DecaySeconds = 1800f,
        // MEASURED with `--fire levels` 2026-10-06, fully developed, every place summed (wind 3 m/s): Leq 90.7 dB,
        // 79.4 dB(A); peaks 28 dB over, crest 28.
        SourceLevelDb = 91f,
        PeakHeadroomDb = 30f,
        ExtentMetres = 1.8f,
    };

    /// <summary>A car on fire: its cabin and engine bay burning through, about 5 MW at its peak a quarter
    /// of an hour in.</summary>
    public static FireSpec BurningCar => new()
    {
        Name = "A car on fire",
        Fuel = FireFuel.Vehicle,
        BaseDiameterMetres = 1.8f,
        WidthMetres = 1.8f,
        DepthMetres = 4.5f,
        HeatReleaseKw = 5000f,
        Moisture = 0.1f,
        Resin = 0.6f,
        FlameHeightMetres = 3f,
        FuelHeightMetres = 1.4f,
        Surround = "Asphalt",
        Panes = 4,
        PaneWidthMetres = 0.8f,
        PaneHeightMetres = 0.45f,
        PaneThicknessMm = 4f,
        PaneDropMetres = 0.9f,
        Struts = 4,
        Tyres = 4,
        GrowthSeconds = 1200f,
        SteadySeconds = 600f,
        DecaySeconds = 4200f,
        // MEASURED with `--fire levels` 2026-10-06, fully developed, every place summed (wind 3 m/s): Leq 93.2 dB,
        // 77.8 dB(A);
        // ten minutes from 700 s after lighting, its struts, tyres and windows going, crest 34 dB.
        SourceLevelDb = 93f,
        PeakHeadroomDb = 36f,
        ExtentMetres = 2.2f,
    };

    /// <summary>A house fully alight: a two-storey timber house ten by twelve metres, every room going.</summary>
    public static FireSpec HouseFire => new()
    {
        Name = "A house fully alight",
        Fuel = FireFuel.Structure,
        BaseDiameterMetres = 3f,
        WidthMetres = 10f,
        DepthMetres = 12f,
        HeatReleaseKw = 30000f,
        Moisture = 0.12f,
        Resin = 0.4f,
        FlameHeightMetres = 10f,
        FuelHeightMetres = 7f,
        Surround = "Grass",
        Panes = 16,
        PaneWidthMetres = 1f,
        PaneHeightMetres = 1.2f,
        PaneThicknessMm = 4f,
        PaneDropMetres = 2.5f,
        GrowthSeconds = 900f,
        SteadySeconds = 3600f,
        DecaySeconds = 7200f,
        // MEASURED with `--fire levels` 2026-10-06, fully developed, every place summed (wind 3 m/s): Leq 97.8 dB,
        // 87.4 dB(A);
        // ten minutes from 1200 s after lighting, windows and a collapse, crest 23 dB.
        SourceLevelDb = 98f,
        PeakHeadroomDb = 24f,
        ExtentMetres = 6f,
    };

    /// <summary>A group of trees alight: a stand of conifers fifteen metres across, their crowns torching
    /// one after another.</summary>
    public static FireSpec BurningTrees => new()
    {
        Name = "A stand of trees burning",
        Fuel = FireFuel.Trees,
        BaseDiameterMetres = 5f,
        WidthMetres = 15f,
        DepthMetres = 15f,
        HeatReleaseKw = 10000f,
        Moisture = 1f,
        Resin = 0.8f,
        FlameHeightMetres = 20f,
        FuelHeightMetres = 14f,
        Surround = "Dirt",
        GrowthSeconds = 300f,
        SteadySeconds = 1800f,
        DecaySeconds = 1800f,
        // MEASURED with `--fire levels` 2026-10-06, fully developed, every place summed (wind 3 m/s), over ten
        // minutes (its trees torch in turn): Leq 92.7 dB, 81.3 dB(A); peaks 24 dB over, crest 26.
        SourceLevelDb = 93f,
        PeakHeadroomDb = 27f,
        ExtentMetres = 7.5f,
    };

    /// <summary>A crown fire in a conifer forest: its front running through the canopy, three hundred metres
    /// of it, 30 MW a metre of front at a 10 m/s wind.</summary>
    public static FireSpec CrownFire => new()
    {
        Name = "A crown fire's front",
        Fuel = FireFuel.Crown,
        BaseDiameterMetres = 25f,
        WidthMetres = 300f,
        DepthMetres = 50f,
        HeatReleaseKw = 9e6f,
        Moisture = 1f,
        Resin = 0.8f,
        FlameHeightMetres = 45f,
        FuelHeightMetres = 20f,
        Surround = "Dirt",
        // MEASURED with `--fire levels` 2026-10-06, fully developed, every place summed, in the field's own
        // wind (4.5 m/s at 10 m, so 5.8 GW): Leq 123.0 dB, 107.3 dB(A); peaks 13 dB over, crest 15.
        SourceLevelDb = 123f,
        PeakHeadroomDb = 17f,
        ExtentMetres = 150f,
    };

    public static IReadOnlyDictionary<string, Func<FireSpec>> Presets { get; } =
        new Dictionary<string, Func<FireSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["campfire"] = () => Campfire,
            ["fire_pit"] = () => GardenFirePit,
            ["bonfire"] = () => Bonfire,
            ["burning_car"] = () => BurningCar,
            ["house_fire"] = () => HouseFire,
            ["burning_trees"] = () => BurningTrees,
            ["crown_fire"] = () => CrownFire,
        };

    // ── Keys ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fire's sound key: "fire:&lt;preset&gt;" for a fire that has always been burning (a map's), or
    /// "fire:&lt;preset&gt;/lit=&lt;seconds&gt;" for one lit at a known moment on the shared clock
    /// (<see cref="WindField.Now"/>), which every client then hears at the same point of its life.
    /// </summary>
    public static string KeyFor(string preset, double? litAt = null)
        => litAt is double t ? string.Create(CultureInfo.InvariantCulture, $"fire:{preset}/lit={t:F1}") : "fire:" + preset;

    /// <summary>A key (with or without its "fire:") taken apart: the preset, and when it was lit if it says.</summary>
    public static void ParseKey(string key, out string preset, out double? litAt)
    {
        string s = key.StartsWith("fire:", StringComparison.OrdinalIgnoreCase) ? key[5..] : key;
        litAt = null;
        int slash = s.IndexOf('/');
        if (slash < 0) { preset = s; return; }
        preset = s[..slash];
        foreach (var part in s[(slash + 1)..].Split('/'))
            if (part.StartsWith("lit=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(part[4..], NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
                litAt = t;
    }

    /// <summary>A preset by name or by key, through the <see cref="ModelLibrary"/> so a map's own wins.</summary>
    public static FireSpec ByName(string key)
    {
        ParseKey(key, out string preset, out _);
        return ModelLibrary.Fire(preset);
    }
}
