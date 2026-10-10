using System.Numerics;
using OpenFPS.Common.Components;

namespace OpenFPS.Common;

/// <summary>How a fuel's surface takes heat before it gives off enough gas to burn (Drysdale, An
/// Introduction to Fire Dynamics, ch. 6).</summary>
public enum FuelHeating
{
    /// <summary>Thicker than the heat reaches in the time (logs, a stump, a car's panels and tyres): it
    /// ignites when ∫ (q − q_cr)² dt reaches its flux-time product, (π/4) kρc (T_ig − T₀)².</summary>
    Thick,
    /// <summary>Thinner than that (needles, leaves, grass): it ignites when the heat it has kept,
    /// ∫ (q − q_cr) dt, has warmed its mass and boiled its water off.</summary>
    Thin,
}

/// <summary>
/// One part of a thing that can burn, with what it takes to catch and what it gives off once it has
/// (docs/FIRE.md 12.3). A tree is the litter under it, its crown and its trunk; a stump, a pile of logs
/// or a car is one part. Each burns as its own fire (<see cref="Preset"/> over <see cref="Footprint"/>).
/// </summary>
public sealed record FuelPart
{
    public string Name { get; init; } = "";
    /// <summary>The fire it burns as (FireSpec.Presets): its heat release, its life and its sound.</summary>
    public string Preset { get; init; } = "";
    /// <summary>Its ground plan, in the thing's own frame, about <see cref="Offset"/>.</summary>
    public FireShape Footprint { get; init; } = FireShape.Circle(1f);
    /// <summary>Where its middle is from the thing's middle, m (x across, y along: the thing's own frame).</summary>
    public Vector2 Offset { get; init; }
    /// <summary>Its bottom and top above the thing's ground, m.</summary>
    public float Base { get; init; }
    public float Top { get; init; } = 1f;

    public FuelHeating Heating { get; init; } = FuelHeating.Thick;
    /// <summary>Under this flux, kW/m², it loses heat as fast as it gets it and never catches. Wood
    /// piloted 12-13 (Drysdale; Babrauskas 2003, Ignition Handbook).</summary>
    public float CriticalFluxKw { get; init; } = 12.5f;
    /// <summary>Thick: its flux-time product dry, (kW/m²)²·s, (π/4) kρc ΔT² (wood: kρc ≈ 0.2 (kW/m²K)²s,
    /// T_ig 350 °C: about 17 000). Thin: its dry mass per area of surface heated, kg/m².</summary>
    public float DryDose { get; init; } = 17000f;
    /// <summary>Water in it now as a share of its dry mass, at the start.</summary>
    public float Moisture { get; init; } = 0.12f;
    /// <summary>Living (foliage, a standing trunk): its water is held by the plant and the weather does not
    /// move it. Dead: it follows the air and the rain at <see cref="TimelagHours"/>.</summary>
    public bool Live { get; init; }
    /// <summary>A dead fuel's time to come 63 % of the way to the air's moisture: 1 h for needles and grass,
    /// 10 h for twigs, 100 h for branches, 1000 h for logs (Fosberg 1970; the NFDRS classes).</summary>
    public float TimelagHours { get; init; } = 100f;
    /// <summary>The moisture at which it no longer carries fire: about 0.3 for dead fine fuel (Rothermel
    /// 1972), higher for living foliage and solid wood.</summary>
    public float ExtinctionMoisture { get; init; } = 0.35f;
    /// <summary>The chance a glowing brand landing on it sets it going, dry: fine litter takes most, a
    /// green crown or a car's paint almost none (docs/FIRE.md 12.4). ESTIMATES by kind.</summary>
    public float Receptivity { get; init; }
    /// <summary>Glowing brands it sends up per MJ it burns. ESTIMATES by kind (docs/FIRE.md 12.4).</summary>
    public float BrandsPerMJ { get; init; }
    /// <summary>A crown over surface fuel: catches from a surface fire under it when that fire's intensity
    /// reaches Van Wagner's (1977) I₀ for its height above the ground and its foliar moisture. Zero for none.</summary>
    public float CrownBaseMetres { get; init; }
    /// <summary>A bed of surface fuel (litter, grass), whose fire spreads at Rothermel's rate (1972):
    /// its load, kg/m², and its fine fuel's surface-to-volume ratio, 1/m.</summary>
    public float SurfaceLoad { get; init; }
    public float SurfaceToVolume { get; init; } = 8000f;
    /// <summary>Oil or fat: water on it flashes to steam under the burning liquid and throws it, so water
    /// makes it flare, not go out (docs/FIRE.md 12.7).</summary>
    public bool WaterFlares { get; init; }
}

/// <summary>A thing that can burn: its parts.</summary>
public sealed record FuelSpec
{
    public string Name { get; init; } = "";
    public FuelPart[] Parts { get; init; } = Array.Empty<FuelPart>();
    /// <summary>Already burning and always has been (a map's fire pit): its first part, with no lighting
    /// time.</summary>
    public bool AlwaysBurning { get; init; }
}

/// <summary>
/// Fuel as a property of things (docs/FIRE.md 12.3): what a thing is made of and how big it is says what
/// it burns as. Every rule here is by material, kind and size, never by map or by name; land cover
/// (<see cref="ForLandCover"/>) is the hook the world's tiles will use.
/// </summary>
public static class FuelCatalog
{
    // Wood (Drysdale; Babrauskas 2003): k ≈ 0.14 W/mK, ρ ≈ 500 kg/m³, c ≈ 2.8 kJ/kgK, so kρc ≈ 0.2
    // (kW/m²K)²s; T_ig ≈ 350 °C piloted: (π/4) 0.2 × 330² ≈ 17 000 (kW/m²)²s.
    private const float WoodFtp = 17000f;

    /// <summary>A living conifer: the needles under it, its crown and its trunk. Its crown from
    /// <paramref name="crownBase"/> to <paramref name="height"/> and as wide as <paramref name="crownDiameter"/>.</summary>
    public static FuelSpec Tree(float crownDiameter, float crownBase, float height, string name = "tree")
    {
        var crown = FireShape.Circle(MathF.Max(1f, crownDiameter));
        return new FuelSpec
        {
            Name = name,
            Parts = new[]
            {
                // Needles shed under it: dead, a one-hour fuel, 0.5 kg/m² (conifer litter 0.5-1.5 kg/m²).
                new FuelPart
                {
                    Name = "litter", Preset = "litter", Footprint = crown, Base = 0f, Top = 0.05f,
                    Heating = FuelHeating.Thin, CriticalFluxKw = 8f, DryDose = 0.15f, Moisture = 0.1f, TimelagHours = 1f,
                    ExtinctionMoisture = 0.3f, Receptivity = 0.4f, BrandsPerMJ = 0.5f,
                    SurfaceLoad = 0.5f, SurfaceToVolume = 6500f,
                },
                // The crown: living needles at about their own weight of water (foliar moisture 100 %).
                new FuelPart
                {
                    Name = "crown", Preset = "tree_crown", Footprint = crown, Base = crownBase, Top = height,
                    Heating = FuelHeating.Thin, CriticalFluxKw = 10f, DryDose = 0.3f, Moisture = 1f, Live = true,
                    ExtinctionMoisture = 2.5f, Receptivity = 0.01f, BrandsPerMJ = 3f, CrownBaseMetres = crownBase,
                },
                // Its branches and the trunk among them: living wood, thick, in the crown. They burn on once
                // the needles have gone; a living trunk's bark below the crown is scorched by a litter fire,
                // not set burning (its own part would be stage 2's snags and dead trees).
                new FuelPart
                {
                    Name = "branches", Preset = "tree_trunk", Footprint = FireShape.Circle(MathF.Min(2f, 0.3f * crownDiameter)),
                    Base = crownBase, Top = height, Heating = FuelHeating.Thick, CriticalFluxKw = 12.5f, DryDose = WoodFtp, Moisture = 0.5f,
                    Live = true, ExtinctionMoisture = 1.5f, Receptivity = 0.02f, BrandsPerMJ = 0.5f,
                },
            },
        };
    }

    /// <summary>A stump: weathered dead wood, a thousand-hour fuel, its cracks and rotten top taking a
    /// brand now and then.</summary>
    public static FuelSpec Stump(float diameter, float height, string name = "stump") => new()
    {
        Name = name,
        Parts = new[]
        {
            new FuelPart
            {
                Name = "stump", Preset = "stump", Footprint = FireShape.Circle(diameter), Base = 0f, Top = height,
                Heating = FuelHeating.Thick, CriticalFluxKw = 12.5f, DryDose = WoodFtp, Moisture = 0.15f, TimelagHours = 1000f,
                ExtinctionMoisture = 0.4f, Receptivity = 0.08f, BrandsPerMJ = 0.3f,
            },
        },
    };

    /// <summary>A stack of split logs with bark and splinters in it: seasoned, a hundred-hour fuel, and the
    /// fines in it take brands well.</summary>
    public static FuelSpec WoodPile(float width, float depth, float height, string name = "pile of logs") => new()
    {
        Name = name,
        Parts = new[]
        {
            new FuelPart
            {
                Name = "pile", Preset = "wood_pile", Footprint = FireShape.Rectangle(width, depth), Base = 0f, Top = height,
                Heating = FuelHeating.Thick, CriticalFluxKw = 12.5f, DryDose = WoodFtp, Moisture = 0.15f, TimelagHours = 100f,
                ExtinctionMoisture = 0.4f, Receptivity = 0.25f, BrandsPerMJ = 1f,
            },
        },
    };

    /// <summary>A car: its tyres, seals and plastics catch first. Polymers ignite at 10-20 kW/m² (PMMA
    /// about 11, rubber about 15-20: Babrauskas 2003); thermally thick at about 8 000 (kW/m²)²s
    /// [estimate]; dry by nature, and painted steel takes no brand.</summary>
    public static FuelSpec Car(float width, float length, string name = "car") => new()
    {
        Name = name,
        Parts = new[]
        {
            new FuelPart
            {
                Name = "car", Preset = "burning_car", Footprint = FireShape.Rectangle(width, length), Base = 0f, Top = 1.5f,
                Heating = FuelHeating.Thick, CriticalFluxKw = 15f, DryDose = 8000f, Moisture = 0f, Live = true,
                ExtinctionMoisture = 1f, Receptivity = 0.005f, BrandsPerMJ = 0.05f,
            },
        },
    };

    /// <summary>
    /// A fire lit as a preset (/spawn fire): one part burning over the preset's own ground, sending up
    /// brands as its fuel does (ESTIMATES, docs/FIRE.md 12.4): foliage and a building's timber many, logs
    /// some, a car's few.
    /// </summary>
    public static FuelSpec ForFire(string preset)
    {
        var spec = FireSpec.ByName(preset);
        float brands = spec.Fuel switch
        {
            FireFuel.Trees or FireFuel.Crown => 3f,
            FireFuel.Structure => 2f,
            FireFuel.Vehicle => 0.05f,
            _ => 0.5f,
        };
        return new FuelSpec
        {
            Name = spec.Name,
            Parts = new[]
            {
                new FuelPart
                {
                    Name = "fire", Preset = preset, Footprint = spec.Outline, Base = 0f, Top = MathF.Max(0.1f, spec.FuelHeightMetres),
                    BrandsPerMJ = brands, Live = true, Receptivity = 0f,
                },
            },
        };
    }

    /// <summary>A fire somebody keeps (a map's fire pit): burning, and the thing it is in does not burn.</summary>
    public static FuelSpec Hearth(string fireKey, FireShape shape) => new()
    {
        Name = "fire",
        AlwaysBurning = true,
        Parts = new[]
        {
            new FuelPart { Name = "fire", Preset = fireKey, Footprint = shape, Base = 0f, Top = 0.3f, BrandsPerMJ = 1f, Live = true },
        },
    };

    /// <summary>
    /// What a placed thing burns as, from what it is: its sound (a fire, a vehicle), its material and its
    /// size (collider, scaled). Null for what does not burn here. A tree is its foliage over the ground and
    /// its wood: a Foliage box high off the ground is a crown with litter under it; a Wood box stands as a
    /// trunk, a stump or a pile by its proportions. Thin wood (a floor, a fence board) and buildings burn as
    /// structures, which are zones: stage 2 (docs/FIRE.md 12.10).
    /// </summary>
    public static FuelSpec? ForThing(string soundId, string material, ColliderShape shape, Vector3 size, float baseAboveGround)
    {
        float w = MathF.Max(0f, size.X), h = MathF.Max(0f, size.Y), d = MathF.Max(0f, size.Z);
        if (soundId.StartsWith("fire:", StringComparison.OrdinalIgnoreCase))
        {
            string key = FireSpec.KeyForPlaced(soundId, shape, size);
            FireSpec.ParseKey(key, out _, out _, out var fs);
            return Hearth(key, fs ?? FireSpec.ByName(key).Outline);
        }
        if (soundId.StartsWith("engine:", StringComparison.OrdinalIgnoreCase) && w > 0.5f && d > 0.5f)
            return Car(MathF.Min(w, d), MathF.Max(w, d));
        if (material.Equals("Foliage", StringComparison.OrdinalIgnoreCase) && baseAboveGround >= 1f && w > 0.5f && d > 0.5f)
            return Tree(MathF.Sqrt(w * d), baseAboveGround, baseAboveGround + h, "tree");
        if (material.Equals("Wood", StringComparison.OrdinalIgnoreCase) && baseAboveGround < 0.5f)
        {
            float wide = MathF.Max(w, d), narrow = MathF.Min(w, d);
            if (narrow < 0.1f || h < 0.25f) return null;                       // a board, a floor, a fence: stage 2
            if (h > 2f * wide && h > 1.5f) return new FuelSpec
            {
                Name = "trunk",
                Parts = new[]
                {
                    new FuelPart
                    {
                        Name = "trunk", Preset = "tree_trunk", Footprint = FireShape.Circle(MathF.Sqrt(w * d)), Base = 0f, Top = h,
                        Heating = FuelHeating.Thick, CriticalFluxKw = 12.5f, DryDose = WoodFtp, Moisture = 0.5f, Live = true,
                        ExtinctionMoisture = 1.5f, Receptivity = 0.02f, BrandsPerMJ = 0.5f,
                    },
                },
            };
            if (h <= 1.2f && wide <= 1.2f) return Stump(MathF.Sqrt(w * d), h);
            return WoodPile(wide, narrow, h);
        }
        return null;
    }

    /// <summary>
    /// The surface fuel a kind of ground carries, the hook the world's tiles give their land cover
    /// through (docs/FIRE.md 12.10): null for ground that does not burn. Stage 2 spreads fire over it.
    /// </summary>
    public static FuelPart? ForLandCover(string landCover) => landCover.ToLowerInvariant() switch
    {
        "grass" or "meadow" or "grassland" => new FuelPart
        {
            Name = "grass", Preset = "litter", Footprint = FireShape.Rectangle(1f, 1f), Top = 0.3f, Heating = FuelHeating.Thin,
            CriticalFluxKw = 8f, DryDose = 0.05f, Moisture = 0.1f, TimelagHours = 1f, ExtinctionMoisture = 0.15f,
            Receptivity = 0.5f, BrandsPerMJ = 0.2f, SurfaceLoad = 0.3f, SurfaceToVolume = 11500f,
        },
        "forest" or "wood" or "woodland" => Tree(5f, 3f, 12f).Parts[0],
        _ => null,
    };
}
