using System.Numerics;
using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Materials;
using OpenFPS.Physics;
using OpenFPS.Sound;

namespace OpenFPS.Server.Systems;

/// <summary>
/// What an item becomes once it is let go (docs/PHYSICS_BOUNDARY.md 18.2 step 7, 18.11): its shape, mass and
/// material for a body, the speed a person throws it at, and the face a thing it struck presents to the impact
/// law. Nothing here knows a prefab by name: an item's own dimensions, or its mass and its material's density,
/// make the box, so a thing authored tomorrow falls like everything else.
/// </summary>
public static class DroppedThings
{
    /// <summary>An item no heavier than this can be thrown; the one carrying limit, because a person cannot
    /// throw what they cannot hold (HandsService.CarryCapacityKg).</summary>
    public const float ThrowCapacityKg = Core.HandsService.CarryCapacityKg;

    /// <summary>
    /// The mass a fixed thing (a tile, a door leaf) is taken as in the impact law. The law reads the struck mass
    /// only through the reduced mass, m1 m2 / (m1 + m2); a million kilograms is the planet to within 0.01 % of a
    /// 100 kg item, where 500 times the item's mass (the old scripted guess) was 0.2 % low for every item.
    /// </summary>
    public const float FixedMassKg = 1_000_000f;

    /// <summary>
    /// The face a mesh body (a tile's floor, a door leaf) is struck as. A contact gives a point, a normal and a
    /// material, but a mesh body carries no panel size; this is the 2 m by 2 m by 0.2 m slab the old scripted drop
    /// rang, kept only where no better figure exists.
    /// </summary>
    public const float FloorPanelWidthMetres = 2f, FloorPanelHeightMetres = 2f, FloorPanelThicknessMetres = 0.2f;

    /// <summary>The smallest side a density-made box may have, metres: a body with no thickness cannot be hit.</summary>
    public const float MinimumSideMetres = 0.02f;

    /// <summary>
    /// An item's body: a box of the dimensions it declares (its collider, when it has one), else a cube of its
    /// declared mass at its material's density. Mass and material are what it carries for being carried and heard
    /// (ItemComponent, MaterialComponent). False for something that is not an item at all.
    /// </summary>
    public static bool TryBody(World world, Entity item, out Vector3 size, out float massKg, out string material)
    {
        size = default;
        massKg = 1f;
        material = "Generic";
        if (!world.Has<ItemComponent>(item)) return false;
        massKg = MathF.Max(0.01f, world.Get<ItemComponent>(item).MassKg);
        material = world.Has<MaterialComponent>(item) ? world.Get<MaterialComponent>(item).Material ?? "Generic" : "Generic";

        if (world.Has<ColliderComponent>(item))
        {
            var declared = world.Get<ColliderComponent>(item).Size;
            if (declared.X > 0f && declared.Y > 0f && declared.Z > 0f) { size = declared; return true; }
        }

        // No dimensions of its own: a cube of its mass at its material's density. One rule for every item, not a
        // table of sizes by name; a crowbar is then a lump of steel of the crowbar's weight, not a metre of bar.
        float density = AcousticRegistry.GetProperties(material).DensityKgM3;
        if (!(density > 0f)) density = AcousticRegistry.GetProperties("Generic").DensityKgM3;
        float side = MathF.Cbrt(massKg / density);
        size = new Vector3(MathF.Max(MinimumSideMetres, side));
        return true;
    }

    /// <summary>
    /// The speed a person releases a thrown object at, metres a second, by its mass: v = 11.0 m^-0.2.
    /// Rod Cross, "Physics of overarm throwing", American Journal of Physics 72(3), 305-312 (2004), threw 57 g to
    /// 3.4 kg and fitted v = k m^-n (his five throwers' n was 0.12 to 0.20 below 0.73 kg and 0.33 to 0.42 above;
    /// over 60 times the mass the speed fell by only 2.4). This one power law runs through his two measured ends:
    /// 19.45 m/s at 0.057 kg and 8.62 m/s at 3.4 kg, against his 19.5 and 8.5. Below 0.05 kg there is no
    /// measurement, so the lightest is held there; above 25 kg the item is too heavy to throw.
    /// </summary>
    public static float ThrowSpeed(float massKg)
        => 11.0f * MathF.Pow(Math.Clamp(massKg, 0.05f, ThrowCapacityKg), -0.2f);

    /// <summary>Whether a person can throw this much, by the one carrying limit in words: too heavy says so.</summary>
    public static bool CanThrow(float massKg) => massKg <= ThrowCapacityKg;

    /// <summary>
    /// The face a box presents to a blow: the largest of its three dimensions is the width, the middle the height
    /// and the smallest the thickness (a panel's thickness is what sets its note). A contact gives the point and
    /// the normal, not which face was met, so the box's own proportions are the best a body can offer.
    /// </summary>
    public static (float Width, float Height, float Thickness) Face(Vector3 size)
    {
        float a = MathF.Abs(size.X), b = MathF.Abs(size.Y), c = MathF.Abs(size.Z);
        float width = MathF.Max(a, MathF.Max(b, c));
        float thickness = MathF.Min(a, MathF.Min(b, c));
        float height = a + b + c - width - thickness;
        return (MathF.Max(0.01f, width), MathF.Max(0.01f, height), MathF.Max(0.01f, thickness));
    }

    /// <summary>
    /// One contact's impact through the one impact law (ImpactAcoustics.Between): the moving body is the hitter,
    /// the other the struck thing, at the contact's point and closing speed, both materials from the contact.
    /// <paramref name="struckSize"/> is the struck body's box, or a floor panel's when it is a mesh; a fixed thing
    /// has its ring held by its mounting (a shorter ring), a loose one rings free. Empty below the law's own
    /// threshold of hearing (<see cref="ImpactAcoustics.MinimumSpeed"/>), which is also its own level floor: a
    /// scuff is not an event.
    /// </summary>
    public static List<TransientSound> Impact(ContactEvent contact, float hitterMassKg, float struckMassKg,
                                              Vector3 struckSize, bool struckFixed)
    {
        if (contact.ClosingSpeed < ImpactAcoustics.MinimumSpeed) return new List<TransientSound>();
        var hitter = AcousticRegistry.GetProperties(contact.Surface);
        var struck = AcousticRegistry.GetProperties(contact.OtherSurface);
        var face = Face(struckSize);
        return ImpactAcoustics.Between(hitter, struck, contact.Point, contact.ClosingSpeed,
                                       hitterMassKg, struckMassKg, face.Width, face.Height, face.Thickness, struckFixed);
    }
}
