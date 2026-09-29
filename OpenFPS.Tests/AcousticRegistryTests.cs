using System.IO;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Verifies that materials.json overrides MERGE into the hardcoded material table rather than
/// replacing entries wholesale — a partial override (the real file only carries Absorption,
/// Scattering, ResonanceIndex) must NOT zero the frequency bands that drive occlusion EQ and
/// wall transmission.
/// </summary>
public class AcousticRegistryTests
{
    [Fact]
    public void MaterialsJson_PartialOverride_PreservesHardcodedBands()
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), "materials.json");
        File.WriteAllText(path, "{\"Wood\":{\"Absorption\":0.35,\"Scattering\":0.3,\"ResonanceIndex\":21}}");
        try
        {
            AcousticRegistry.Initialize();
            var wood = AcousticRegistry.GetProperties("Wood");

            // The provided scalar field is overridden...
            Assert.Equal(0.35, (double)wood.Absorption, 2);
            // ...but the omitted frequency bands keep their hardcoded values (not zeroed).
            Assert.Equal(0.6, (double)wood.TransmissionLow, 2);
            Assert.Equal(0.2, (double)wood.AbsorptionHigh, 2);
            Assert.True(wood.TransmissionMid > 0f, "TransmissionMid must not be zeroed by a partial override.");
        }
        finally
        {
            File.Delete(path);
            AcousticRegistry.Initialize(); // restore clean hardcoded state for other tests
        }
    }

    /// <summary>
    /// A region's faces are stored as resonance indices and read back by index, so two materials on
    /// one index are one material: the terminal's new acoustic ceiling shared Carpet's 6 and came back
    /// as Carpet (2026-09-29). The registry only printed it at startup.
    /// </summary>
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
}
