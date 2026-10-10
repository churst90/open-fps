using OpenFPS.Common;

namespace OpenFPS.Tests;

public class AcousticRegistryTests
{
    [Fact]
    public void EveryMaterialHasItsOwnResonanceIndex()
    {
        AcousticRegistry.Initialize();
        var seen = new System.Collections.Generic.Dictionary<int, string>();
        foreach (var name in AcousticRegistry.KnownMaterials())
        {
            int index = AcousticRegistry.GetProperties(name).ResonanceIndex;
            Assert.False(seen.TryGetValue(index, out var other), $"'{name}' and '{other}' share resonance index {index}");
            seen[index] = name;
            // And reading the index back gives this material, not another one.
            Assert.Equal(AcousticRegistry.GetProperties(name), AcousticRegistry.GetPropertiesByResonanceIndex(index));
        }
    }

    /// <summary>
    /// Every figure a material had before the table grew (docs/MATTER.md, step 1), exactly: absorption, the
    /// three bands, scattering, transmission, index, density, modulus, loss and porosity. These are what every
    /// sound in the game read, so the table's growth may not move one of them.
    /// </summary>
    [Theory]
    [InlineData("Generic", 0.2f, 0.1f, 0.2f, 0.3f, 0.2f, 0.4f, 0.3f, 0.2f, 22, 1200f, 5f, 0.02f, false)]
    [InlineData("Wood", 0.15f, 0.1f, 0.15f, 0.2f, 0.4f, 0.6f, 0.4f, 0.2f, 21, 650f, 11f, 0.03f, false)]
    [InlineData("Metal", 0.05f, 0.05f, 0.05f, 0.1f, 0.1f, 0.1f, 0.05f, 0.02f, 13, 7850f, 200f, 0.0002f, false)]
    [InlineData("Fence", 0.08f, 0.05f, 0.08f, 0.12f, 0.55f, 0.94f, 0.88f, 0.72f, 29, 7850f, 200f, 0.0004f, true)]
    [InlineData("Concrete", 0.02f, 0.01f, 0.02f, 0.02f, 0.1f, 0.05f, 0.02f, 0.01f, 18, 2400f, 30f, 0.015f, false)]
    [InlineData("Marble", 0.01f, 0.01f, 0.01f, 0.01f, 0.05f, 0.05f, 0.02f, 0.01f, 12, 2700f, 60f, 0.002f, false)]
    [InlineData("Carpet", 0.60f, 0.15f, 0.5f, 0.75f, 0.6f, 0.1f, 0.05f, 0.01f, 6, 200f, 0.01f, 0.4f, true)]
    [InlineData("Glass", 0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.7f, 0.5f, 0.3f, 3, 2500f, 70f, 0.001f, false)]
    [InlineData("None", 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 0, 0f, 0f, 1f, true)]
    [InlineData("Plastic", 0.1f, 0.05f, 0.1f, 0.2f, 0.2f, 0.5f, 0.4f, 0.2f, 15, 1100f, 2.5f, 0.05f, false)]
    [InlineData("Grass", 0.75f, 0.5f, 0.7f, 0.9f, 0.9f, 0.4f, 0.6f, 0.8f, 2, 400f, 0.005f, 0.6f, true)]
    [InlineData("Audience", 0.72f, 0.5f, 0.75f, 0.85f, 0.8f, 0.3f, 0.15f, 0.05f, 5, 300f, 0.01f, 0.5f, true)]
    [InlineData("Dirt", 0.60f, 0.4f, 0.5f, 0.6f, 0.8f, 0.3f, 0.4f, 0.5f, 4, 1600f, 0.05f, 0.5f, true)]
    [InlineData("Gravel", 0.65f, 0.35f, 0.65f, 0.80f, 0.95f, 0.35f, 0.45f, 0.55f, 7, 1700f, 0.35f, 0.55f, true)]
    [InlineData("Brick", 0.04f, 0.03f, 0.04f, 0.07f, 0.45f, 0.06f, 0.03f, 0.015f, 23, 1900f, 15f, 0.02f, false)]
    [InlineData("Asphalt", 0.09f, 0.04f, 0.08f, 0.16f, 0.35f, 0.1f, 0.05f, 0.02f, 24, 2300f, 3f, 0.18f, false)]
    [InlineData("Tile", 0.015f, 0.01f, 0.015f, 0.02f, 0.06f, 0.15f, 0.08f, 0.03f, 25, 2300f, 60f, 0.005f, false)]
    [InlineData("Foliage", 0.55f, 0.2f, 0.5f, 0.8f, 0.92f, 0.85f, 0.6f, 0.3f, 26, 500f, 0.01f, 0.6f, true)]
    [InlineData("Plaster", 0.12f, 0.28f, 0.10f, 0.05f, 0.15f, 0.35f, 0.18f, 0.08f, 27, 800f, 3f, 0.03f, false)]
    [InlineData("AcousticTile", 0.70f, 0.38f, 0.80f, 0.82f, 0.10f, 0.55f, 0.35f, 0.15f, 30, 250f, 0.05f, 0.3f, true)]
    [InlineData("Rubber", 0.20f, 0.10f, 0.20f, 0.35f, 0.35f, 0.5f, 0.35f, 0.2f, 8, 1100f, 0.02f, 0.25f, false)]
    [InlineData("Leather", 0.12f, 0.08f, 0.12f, 0.18f, 0.15f, 0.5f, 0.4f, 0.25f, 9, 900f, 0.45f, 0.12f, false)]
    [InlineData("BootRubber", 0.15f, 0.08f, 0.15f, 0.25f, 0.30f, 0.5f, 0.35f, 0.2f, 10, 1250f, 0.20f, 0.20f, false)]
    [InlineData("Skin", 0.30f, 0.15f, 0.30f, 0.45f, 0.45f, 0.6f, 0.45f, 0.3f, 11, 1050f, 0.0015f, 0.45f, false)]
    [InlineData("Water", 0.015f, 0.01f, 0.015f, 0.02f, 0.05f, 0.01f, 0.005f, 0.002f, 31, 1000f, 2.2f, 0.5f, false)]
    public void TheMaterialsThatSoundedBeforeAreUnchanged(string name, float abs, float low, float mid, float high, float scattering,
                                                          float tLow, float tMid, float tHigh, int index, float density,
                                                          float youngs, float loss, bool porous)
    {
        AcousticRegistry.Initialize();
        Assert.True(AcousticRegistry.IsKnown(name));
        var p = AcousticRegistry.GetProperties(name);
        Assert.Equal(abs, p.Absorption);
        Assert.Equal(low, p.AbsorptionLow);
        Assert.Equal(mid, p.AbsorptionMid);
        Assert.Equal(high, p.AbsorptionHigh);
        Assert.Equal(scattering, p.Scattering);
        Assert.Equal(tLow, p.TransmissionLow);
        Assert.Equal(tMid, p.TransmissionMid);
        Assert.Equal(tHigh, p.TransmissionHigh);
        Assert.Equal(index, p.ResonanceIndex);
        Assert.Equal(density, p.DensityKgM3);
        Assert.Equal(youngs, p.YoungsModulusGPa);
        Assert.Equal(loss, p.LossFactor);
        Assert.Equal(porous, p.Porous);
    }

    /// <summary>The families docs/MATTER.md 2.3 asks for are in the table, each a different stuff.</summary>
    [Theory]
    [InlineData("Aluminium", "metal")] [InlineData("Metal", "metal")] [InlineData("StainlessSteel", "metal")]
    [InlineData("CastIron", "metal")] [InlineData("Copper", "metal")] [InlineData("Brass", "metal")]
    [InlineData("Bronze", "metal")] [InlineData("Lead", "metal")] [InlineData("Titanium", "metal")]
    [InlineData("Granite", "stone")] [InlineData("Marble", "stone")] [InlineData("Sandstone", "stone")]
    [InlineData("Concrete", "stone")] [InlineData("Brick", "stone")] [InlineData("Glass", "glass")]
    [InlineData("LaminatedGlass", "glass")] [InlineData("Tile", "stone")]
    [InlineData("Oak", "wood")] [InlineData("Pine", "wood")] [InlineData("Maple", "wood")]
    [InlineData("Plywood", "wood")] [InlineData("MDF", "wood")]
    [InlineData("PVC", "polymer")] [InlineData("Acrylic", "polymer")] [InlineData("Polycarbonate", "polymer")]
    [InlineData("Nylon", "polymer")] [InlineData("Rubber", "polymer")] [InlineData("Foam", "polymer")]
    [InlineData("Dirt", "ground")] [InlineData("Sand", "ground")] [InlineData("Clay", "ground")]
    [InlineData("Ice", "ground")] [InlineData("Snow", "ground")] [InlineData("Leather", "soft")] [InlineData("Fabric", "soft")]
    public void TheFamiliesAreThere(string name, string family)
    {
        AcousticRegistry.Initialize();
        Assert.True(AcousticRegistry.IsKnown(name), $"'{name}' is not in the table");
        var p = AcousticRegistry.GetProperties(name);
        Assert.Equal(family, p.Family);
        Assert.True(p.DensityKgM3 > 0f && p.YoungsModulusGPa > 0f && p.LossFactor > 0f, name);
        Assert.InRange(p.PoissonRatio, 0.1f, 0.5f);
        Assert.True(p.SpecificHeatJKgK > 0f && p.ThermalConductivityWmK > 0f, name);
        Assert.True(p.HardnessMPa > 0f, name);
    }

    [Fact]
    public void EveryMaterialHasTheWholeRow()
    {
        AcousticRegistry.Initialize();
        foreach (var name in AcousticRegistry.KnownMaterials())
        {
            if (name == "None") continue;
            var p = AcousticRegistry.GetProperties(name);
            Assert.False(string.IsNullOrEmpty(p.Family), $"{name} has no family");
            Assert.True(p.PoissonRatio > 0f && p.PoissonRatio <= 0.5f, $"{name}: Poisson {p.PoissonRatio}");
            Assert.True(p.SpecificHeatJKgK > 0f, $"{name}: no specific heat");
            Assert.True(p.ThermalConductivityWmK > 0f, $"{name}: no conductivity");
            Assert.True(p.LossExponent >= 0f && p.LossExponent <= 1f, $"{name}: loss exponent {p.LossExponent}");
        }
    }

    /// <summary>MATTER.md 7.1: aluminium and steel have almost the same speed of sound, so the same cube rings
    /// at almost the same pitch in either; lead's is far lower.</summary>
    [Fact]
    public void AluminiumAndSteelRingAlikeAndLeadDoesNot()
    {
        AcousticRegistry.Initialize();
        static float C(string m) { var p = AcousticRegistry.GetProperties(m); return MathF.Sqrt(p.YoungsModulusGPa * 1e9f / p.DensityKgM3); }
        Assert.InRange(C("Aluminium") / C("Metal"), 0.95f, 1.08f);
        Assert.True(C("Lead") < 0.25f * C("Metal"), $"lead {C("Lead"):F0} m/s, steel {C("Metal"):F0}");
    }

    [Fact]
    public void WoodIsStifferAlongTheGrainAndDampsItsHighModesFaster()
    {
        AcousticRegistry.Initialize();
        foreach (var name in new[] { "Wood", "Oak", "Pine", "Maple", "Plywood" })
        {
            var p = AcousticRegistry.GetProperties(name);
            Assert.True(p.YoungsModulusGPa > 1.5f * p.AcrossGrainGPa, name);
            Assert.True(p.LossAt(4000f) > 2f * p.LossAt(1000f), name);
        }
        var steel = AcousticRegistry.GetProperties("Metal");
        Assert.Equal(steel.LossAt(1000f), steel.LossAt(4000f));
        Assert.Equal(steel.YoungsModulusGPa, steel.AcrossGrainGPa);
    }

    [Fact]
    public void AliasesReadTheirMaterial()
    {
        AcousticRegistry.Initialize();
        Assert.Equal(AcousticRegistry.GetProperties("Metal"), AcousticRegistry.GetProperties("Steel"));
        Assert.Equal(AcousticRegistry.GetProperties("Metal"), AcousticRegistry.GetProperties("mildsteel"));
        Assert.Equal(AcousticRegistry.GetProperties("Aluminium"), AcousticRegistry.GetProperties("Aluminum"));
        Assert.True(AcousticRegistry.TryGetResonanceIndex("Soil", out int soil));
        Assert.Equal(AcousticRegistry.GetProperties("Dirt").ResonanceIndex, soil);
        Assert.DoesNotContain("Steel", AcousticRegistry.KnownMaterials());
        Assert.Equal("Metal", AcousticRegistry.Canonical("steel"));
    }

    [Fact]
    public void AnUnknownNameIsGenericAndSaysSo()
    {
        AcousticRegistry.Initialize();
        var p = AcousticRegistry.GetProperties("Unobtainium");
        Assert.Equal(AcousticRegistry.GetProperties("Generic"), p);
        Assert.Contains("Unobtainium", AcousticRegistry.UnknownNamesAsked);
        Assert.False(AcousticRegistry.IsKnown("Unobtainium"));
    }
}
