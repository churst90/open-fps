using System;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// What the mixer makes of a path: each band's gain as a level, once, less the air. It used to read
/// the gains as interpolation weights between 0 and a -20/-30/-40 dB floor (a gain of 0.2, -14 dB,
/// came out at -32 in the high band), then take the occlusion off the volume and off the top again.
/// </summary>
public class PathEqTests
{
    [Fact]
    public void AGainIsItsLevel()
    {
        var (low, mid, high) = FmodAudioProvider.PathEqDb(1f, 0.5f, 0.2f, 0f, 0f, 0f);
        Assert.Equal(0f, low, 3);
        Assert.Equal(-6.02f, mid, 2);
        Assert.Equal(-13.98f, high, 2);
    }

    [Fact]
    public void TheAirComesOffEachBand()
    {
        var (low, mid, high) = FmodAudioProvider.PathEqDb(1f, 1f, 0.5f, 0.1f, 0.8f, 14f);
        Assert.Equal(-0.1f, low, 3);
        Assert.Equal(-0.8f, mid, 3);
        Assert.Equal(-6.02f - 14f, high, 2);
    }

    [Fact]
    public void ASilentBandIsFloored()
    {
        var (_, _, high) = FmodAudioProvider.PathEqDb(1f, 1f, 0f, 0f, 0f, 0f);
        Assert.Equal(-80f, high, 3);
    }
}
