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
}
