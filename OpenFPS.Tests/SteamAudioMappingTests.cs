using System.Numerics;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Tests;

/// <summary>Unit tests for the pure Steam Audio mapping logic added in Phase 4 (no native libs needed).</summary>
public class SteamAudioMappingTests
{
    // --- Pathing SH -> world arrival direction (convention pinned by SimPathDirSpike) ---
    // Raw order-1 SH (ACN): [0]=W, [1]=m-1, [2]=m0, [3]=m+1. Signs below are the measured calibration data.

    [Theory]
    [InlineData(0f, 0f, -0.081f, 0f, 0f, 1f)]   // +z arrival
    [InlineData(0f, 0f, 0.081f, 0f, 0f, -1f)]   // -z arrival
    [InlineData(-0.081f, 0f, 0f, 1f, 0f, 0f)]   // +x arrival
    [InlineData(0.081f, 0f, 0f, -1f, 0f, 0f)]   // -x arrival
    public void PathingWorldDirection_MapsShToExpectedAxis(float sh1, float sh2, float sh3,
        float ex, float ey, float ez)
    {
        var d = SteamAudioSimulator.PathingWorldDirection(0.047f, sh1, sh2, sh3);
        var expected = new Vector3(ex, ey, ez);
        Assert.True(Vector3.Dot(d, expected) > 0.99f, $"got {d}, expected ~{expected}");
        Assert.True(System.Math.Abs(d.Length() - 1f) < 1e-3f, "direction should be unit length");
    }

    [Fact]
    public void PathingWorldDirection_ZeroSh_ReturnsZero()
    {
        Assert.Equal(Vector3.Zero, SteamAudioSimulator.PathingWorldDirection(0f, 0f, 0f, 0f));
    }

    // --- Direct result -> engine acoustic params ---

    [Fact]
    public void ToAcousticParams_ClearLineOfSight_NoOcclusionFullClarity()
    {
        var ap = SteamAudioSimulator.ToAcousticParams(new SteamAudioSimulator.DirectResult(1f, 1f, 1f, 1f));
        Assert.Equal(0f, ap.Occlusion, 3);
        Assert.Equal(1f, ap.EqLow, 3);
        Assert.Equal(1f, ap.EqMid, 3);
        Assert.Equal(1f, ap.EqHigh, 3);
    }

    [Fact]
    public void ToAcousticParams_FullyBlockedNoTransmission_FullOcclusionNoClarity()
    {
        var ap = SteamAudioSimulator.ToAcousticParams(new SteamAudioSimulator.DirectResult(0f, 0f, 0f, 0f));
        Assert.Equal(1f, ap.Occlusion, 3);
        Assert.Equal(0f, ap.EqLow, 3);
        Assert.Equal(0f, ap.EqHigh, 3);
        Assert.Equal(0f, ap.Bleed, 3);
    }

    [Fact]
    public void ToAcousticParams_BlockedWithTransmission_LowPassesMoreThanHigh()
    {
        // Walls transmit low frequencies better than high — eq should reflect that ordering.
        var ap = SteamAudioSimulator.ToAcousticParams(new SteamAudioSimulator.DirectResult(0f, 0.5f, 0.2f, 0.05f));
        Assert.Equal(1f, ap.Occlusion, 3);
        Assert.Equal(0.5f, ap.EqLow, 3);
        Assert.Equal(0.2f, ap.EqMid, 3);
        Assert.Equal(0.05f, ap.EqHigh, 3);
        Assert.Equal(0.5f, ap.Bleed, 3);
        Assert.True(ap.EqLow > ap.EqHigh);
    }

    [Fact]
    public void ToAcousticParams_PartialVisibility_BlendsTowardClarity()
    {
        var ap = SteamAudioSimulator.ToAcousticParams(new SteamAudioSimulator.DirectResult(0.5f, 0f, 0f, 0f));
        Assert.Equal(0.5f, ap.Occlusion, 3);
        Assert.Equal(0.5f, ap.EqMid, 3); // v + (1-v)*0 = 0.5
    }
}
