using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// An echo's mirror copy loses its top to the surface's roughness, which grows with frequency — more
/// off brick than off glass, and again at every bounce — and keeps its bottom.
/// </summary>
public class EchoDullingTests
{
    [Fact]
    public void BrickTakesMoreTopThanGlassAndEveryBounceTakesMore()
    {
        var glass = ImageSource.SpecularBandLossDb(AcousticRegistry.GetProperties("Glass").Scattering, 1);
        var brick = ImageSource.SpecularBandLossDb(AcousticRegistry.GetProperties("Brick").Scattering, 1);
        var brick3 = ImageSource.SpecularBandLossDb(AcousticRegistry.GetProperties("Brick").Scattering, 3);

        Assert.True(glass.HighDb > -1.5f, $"glass lost {glass.HighDb:F1} dB of top");
        Assert.True(brick.HighDb < -6f, $"brick lost only {brick.HighDb:F1} dB of top");
        Assert.True(brick3.HighDb < brick.HighDb * 2.5f, "three bounces are not duller than one");
        Assert.Equal(0f, brick.LowDb);                     // the bass comes back whole
        Assert.Equal((0f, 0f), ImageSource.SpecularBandLossDb(0f, 2));
    }
}
