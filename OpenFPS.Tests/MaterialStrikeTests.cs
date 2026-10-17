using OpenFPS.Physics;
using OpenFPS.Sound;
using OpenFPS.Materials;

namespace OpenFPS.Tests;

/// <summary>
/// A round's strike and ricochet come from the one material table (AcousticRegistry), aliases and all: BulletImpact
/// and Ricochet kept tables of their own without the aliases, so steel, stainless and aluminium struck as Generic,
/// chipping like plaster (docs/SPECIAL_CASES_AUDIT.md S22).
/// </summary>
public class MaterialStrikeTests
{
    public MaterialStrikeTests() { AcousticRegistry.Initialize(); }

    /// <summary>The two tables as they were before they moved into the material table: every material struck
    /// before strikes and skips exactly as it did.</summary>
    [Theory]
    [InlineData("Concrete", StrikeKind.Brittle, 250e6f, 2400f, 0.4f, 30e-6f)]
    [InlineData("Brick", StrikeKind.Brittle, 120e6f, 1900f, 0.6f, 40e-6f)]
    [InlineData("Marble", StrikeKind.Brittle, 400e6f, 2700f, 0.3f, 25e-6f)]
    [InlineData("Tile", StrikeKind.Brittle, 300e6f, 2300f, 0.2f, 25e-6f)]
    [InlineData("Glass", StrikeKind.Brittle, 300e6f, 2500f, 0.2f, 25e-6f)]
    [InlineData("Asphalt", StrikeKind.Brittle, 60e6f, 2300f, 0.5f, 50e-6f)]
    [InlineData("Generic", StrikeKind.Brittle, 150e6f, 1200f, 0.3f, 50e-6f)]
    [InlineData("Metal", StrikeKind.Metal, 0f, 7850f, 0f, 20e-6f)]
    [InlineData("Fence", StrikeKind.Metal, 0f, 7850f, 0f, 20e-6f)]
    [InlineData("Wood", StrikeKind.Wood, 30e6f, 650f, 0.3f, 150e-6f)]
    [InlineData("Plaster", StrikeKind.Panel, 20e6f, 800f, 0.4f, 250e-6f)]
    [InlineData("AcousticTile", StrikeKind.Panel, 5e6f, 250f, 0.2f, 400e-6f)]
    [InlineData("Plastic", StrikeKind.Panel, 40e6f, 1100f, 0.2f, 120e-6f)]
    [InlineData("Dirt", StrikeKind.Soil, 5e6f, 1600f, 0.6f, 500e-6f)]
    [InlineData("Grass", StrikeKind.Soil, 6e6f, 1500f, 0.6f, 700e-6f)]
    [InlineData("Gravel", StrikeKind.Soil, 8e6f, 1700f, 2.0f, 60e-6f)]
    [InlineData("Water", StrikeKind.Water, 0f, 1000f, 0f, 0f)]
    [InlineData("Carpet", StrikeKind.Soft, 0f, 200f, 0f, 1e-3f)]
    [InlineData("Audience", StrikeKind.Soft, 0f, 300f, 0f, 1e-3f)]
    [InlineData("Foliage", StrikeKind.Soft, 0f, 500f, 0f, 1e-3f)]
    [InlineData("Rubber", StrikeKind.Soft, 0f, 1100f, 0f, 1e-3f)]
    [InlineData("Leather", StrikeKind.Soft, 0f, 900f, 0f, 1e-3f)]
    [InlineData("BootRubber", StrikeKind.Soft, 0f, 1250f, 0f, 1e-3f)]
    [InlineData("Skin", StrikeKind.Soft, 0f, 1050f, 0f, 1e-3f)]
    [InlineData("None", StrikeKind.Soft, 0f, 1f, 0f, 1e-3f)]
    public void WhatWasStruckBeforeStrikesAsItDid(string material, StrikeKind kind, float crater, float debris, float chip, float contact)
    {
        var s = BulletImpact.StuffOf(material);
        Assert.Equal(kind, s.Kind);
        Assert.Equal(crater, s.CraterJoulesPerM3);
        Assert.Equal(debris, s.DebrisDensity);
        Assert.Equal(chip, s.ChipGrams);
        Assert.Equal(contact, s.ChipContactSeconds);
        Assert.Equal(material == "Grass" ? 1500f : 400f, s.SprayLowHz);
        Assert.Equal(material == "Foliage", s.Rustles);
    }

    [Theory]
    [InlineData("Metal", 25f, 0.25f, 0.35f, 0.92f, 0.7f)]
    [InlineData("Concrete", 15f, 0.35f, 0.35f, 0.85f, 1f)]
    [InlineData("Marble", 18f, 0.3f, 0.3f, 0.88f, 1f)]
    [InlineData("Tile", 15f, 0.3f, 0.35f, 0.88f, 1f)]
    [InlineData("Brick", 12f, 0.4f, 0.4f, 0.85f, 0.9f)]
    [InlineData("Asphalt", 10f, 0.6f, 0.4f, 0.95f, 0.6f)]
    [InlineData("Gravel", 8f, 1.0f, 0.5f, 0.95f, 0.4f)]
    [InlineData("Dirt", 5f, 1.4f, 0.5f, 1f, 0.2f)]
    [InlineData("Water", -1f, 1.3f, 0.4f, 1f, 0.05f)]
    public void WhatSkippedBeforeSkipsAsItDid(string material, float critical, float departure, float loss, float kept, float flatten)
    {
        Assert.True(RicochetLaw.TryFace(material, out var f));
        Assert.Equal(new RicochetFace(critical, departure, loss, kept, flatten), f);
    }

    [Theory]
    [InlineData("Fence")] [InlineData("Glass")] [InlineData("Wood")] [InlineData("Grass")] [InlineData("Carpet")] [InlineData("Plaster")]
    public void ARoundGoesIntoWhatNeverSkipped(string material) => Assert.False(RicochetLaw.TryFace(material, out _));

    /// <summary>Every material in the table says what a round does to it.</summary>
    [Fact]
    public void EveryMaterialHasAStrike()
    {
        foreach (var name in AcousticRegistry.KnownMaterials())
            Assert.True(AcousticRegistry.GetProperties(name).Strike != null, $"'{name}' has no strike behaviour");
    }

    /// <summary>An alias strikes as its material, and a material made like another strikes as its family does,
    /// with its own density: steel, stainless and aluminium ring and skip as metal, oak splinters as wood.</summary>
    [Theory]
    [InlineData("Steel", StrikeKind.Metal, true)]
    [InlineData("MildSteel", StrikeKind.Metal, true)]
    [InlineData("Stainless", StrikeKind.Metal, true)]
    [InlineData("StainlessSteel", StrikeKind.Metal, true)]
    [InlineData("Aluminium", StrikeKind.Metal, true)]
    [InlineData("Aluminum", StrikeKind.Metal, true)]
    [InlineData("CastIron", StrikeKind.Metal, true)]
    [InlineData("Oak", StrikeKind.Wood, false)]
    [InlineData("Plywood", StrikeKind.Wood, false)]
    [InlineData("Granite", StrikeKind.Brittle, true)]
    [InlineData("Sand", StrikeKind.Soil, true)]
    [InlineData("PVC", StrikeKind.Panel, false)]
    [InlineData("Ice", StrikeKind.Brittle, true)]
    [InlineData("Snow", StrikeKind.Soil, false)]
    public void AnAliasOrAKinStrikesAsItsMaterialDoes(string material, StrikeKind kind, bool skips)
    {
        Assert.True(AcousticRegistry.IsKnown(material));
        var s = BulletImpact.StuffOf(material);
        Assert.Equal(kind, s.Kind);
        Assert.Equal(MathF.Max(1f, AcousticRegistry.GetProperties(material).DensityKgM3), s.DebrisDensity);
        Assert.Equal(skips, RicochetLaw.TryFace(material, out _));
        Assert.DoesNotContain(material, AcousticRegistry.UnknownNamesAsked);
    }

    /// <summary>What each material is underfoot, as Footsteps' own tables had it before they moved into the material
    /// table; anything they did not name is a slab with an ordinary grit.</summary>
    [Theory]
    [InlineData("Wood", 0.40f, 0.021f, 0.12f, 0.45f, 0, 0f)]
    [InlineData("Metal", 0.60f, 0.005f, 0.08f, 0.35f, 0, 0f)]
    [InlineData("Glass", 0.80f, 0.012f, 0.02f, 0.15f, 0, 0f)]
    [InlineData("Concrete", 0f, 0f, 0.45f, 0.80f, 0, 0f)]
    [InlineData("Marble", 0f, 0f, 0.03f, 0.25f, 0, 0f)]
    [InlineData("Carpet", 0f, 0f, 0.05f, 0.20f, 0, 0f)]
    [InlineData("Grass", 0f, 0f, 0.30f, 0.35f, 30, 1.5f)]
    [InlineData("Dirt", 0f, 0f, 0.60f, 0.70f, 22, 4f)]
    [InlineData("Gravel", 0f, 0f, 2.00f, 0.95f, 70, 12f)]
    [InlineData("Tile", 0f, 0f, 0.25f, 0.50f, 0, 0f)]
    [InlineData("Asphalt", 0f, 0f, 0.25f, 0.50f, 0, 0f)]
    [InlineData("Brick", 0f, 0f, 0.25f, 0.50f, 0, 0f)]
    public void WhatWasUnderfootBeforeIsUnderfootAsItWas(string material, float span, float deck, float grain, float coverage, int loose, float looseMm)
    {
        Assert.Equal(span, Footsteps.FreeSpanM(material));
        Assert.Equal(deck, Footsteps.DeckThicknessM(material));
        Assert.Equal((grain, coverage), Footsteps.ContactTexture(material));
        Assert.Equal((loose, looseMm), Footsteps.LooseMaterial(material));
        Assert.Equal(Footsteps.ContactTexture(material.ToLowerInvariant()), Footsteps.ContactTexture(material));
    }

    /// <summary>Leaves are a property of the material, and only foliage has them; a pool is what falls into a liquid.</summary>
    [Fact]
    public void LeavesAndPoolsAreWhatTheMaterialIs()
    {
        Assert.Equal(new[] { "Foliage" }, AcousticRegistry.KnownMaterials().Where(m => AcousticRegistry.GetProperties(m).Leaves));
        Assert.Equal(RainSurfaceKind.Canopy, RainSurfaces.KindOf("Foliage", 0f));
        Assert.Equal(RainSurfaceKind.Pool, RainSurfaces.KindOf("Water", 0f));
        Assert.NotEqual(RainSurfaceKind.Pool, RainSurfaces.KindOf("Ice", 0f));
        Assert.True(BulletImpact.StuffOf("Foliage").Rustles);
        Assert.True(AcousticRegistry.IsWood("Oak") && AcousticRegistry.IsWood("wood") && !AcousticRegistry.IsWood("Metal"));
        Assert.True(AcousticRegistry.IsGlass("LaminatedGlass") && AcousticRegistry.IsGlass("SodaLimeGlass") && !AcousticRegistry.IsGlass("Plastic"));
    }

    [Fact]
    public void AnUnknownMaterialStrikesAsGenericAndSaysSo()
    {
        Assert.Equal(BulletImpact.StuffOf("Generic"), BulletImpact.StuffOf("Adamantium"));
        Assert.Contains("Adamantium", AcousticRegistry.UnknownNamesAsked);
    }
}
