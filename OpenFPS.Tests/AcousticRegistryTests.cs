using OpenFPS.Common;
using Xunit;

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
}
