using OpenFPS.Audio.SteamAudio;

namespace OpenFPS.Tests;

/// <summary>The pure Steam Audio mapping logic, without the native libraries.</summary>
public class SteamAudioMappingTests
{
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

    // --- Reflection RT60 -> FMOD reverb decay (ms) ---

    [Fact]
    public void ReverbDecayMs_UsesLongestBandInMilliseconds()
    {
        var r = new SteamAudioSimulator.ReverbResult(0.62f, 0.61f, 0.61f);
        Assert.Equal(620f, SteamAudioSimulator.ReverbDecayMs(r), 1);
    }

    [Fact]
    public void ReverbDecayMs_ClampsToFloor()
    {
        Assert.Equal(100f, SteamAudioSimulator.ReverbDecayMs(SteamAudioSimulator.ReverbResult.None), 1);
    }

    [Fact]
    public void ReverbDecayMs_ClampsToCeiling()
    {
        var r = new SteamAudioSimulator.ReverbResult(50f, 50f, 50f);
        Assert.Equal(20000f, SteamAudioSimulator.ReverbDecayMs(r), 1);
    }
}
