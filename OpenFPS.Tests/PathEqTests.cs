using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Tests;

/// <summary>
/// What the mixer makes of a path: each band's gain as a level, once, less the air. Read as weights
/// toward a -20/-30/-40 dB floor, a 0.2 gain (-14 dB) once came out at -32 in the high band.
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
