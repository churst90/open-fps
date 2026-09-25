using OpenFPS.Client.AudioEngine.Core;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The air's absorption is ISO 9613-1's, checked against the standard's own table at 20 °C, 50 %
/// relative humidity and one atmosphere. It replaced a straight-line muffle that took six times too
/// much of the top off anything a block away.
/// </summary>
public class AirAbsorptionIsoTests
{
    [Theory]
    [InlineData(250f, 1.3f)]     // dB/km, ISO 9613-1 Table 1 (20 C, 50 %)
    [InlineData(1000f, 4.7f)]
    [InlineData(4000f, 29.7f)]
    [InlineData(8000f, 105f)]
    public void TheStandardsTable(float hz, float dbPerKm)
    {
        float got = AudioPhysics.AirAttenuationDbPerMetre(hz, 20f, 0.5f) * 1000f;
        Assert.InRange(got, dbPerKm * 0.9f, dbPerKm * 1.1f);
    }

    [Fact]
    public void ABlockAwayTheTopIsDimmedNotGone()
    {
        var (low, mid, high) = AudioPhysics.AirLossDb(135f, 0.5f, 20f, 1013.25f);
        Assert.InRange(low, 0f, 0.3f);
        Assert.InRange(mid, 0.5f, 1.0f);
        Assert.InRange(high, 12f, 16f);          // was 32 dB under the old muffle
        Assert.Equal(0f, AudioPhysics.AirLossDb(0f, 0.5f, 20f, 1013.25f).High);
        // Twice the distance, twice the loss.
        Assert.Equal(2f * high, AudioPhysics.AirLossDb(270f, 0.5f, 20f, 1013.25f).High, 3);
        // An unset map multiplier is one.
        Assert.Equal(high, AudioPhysics.AirLossDb(135f, 0.5f, 20f, 1013.25f, 0f).High, 4);
    }
}
