using OpenFPS.Audio.Fmod;

namespace OpenFPS.Tests;

/// <summary>
/// "I walk inside Selby House lobby and I clap and the reflections/reverb is mono while everything else seems to be
/// binaural" (Cody, 2026-10-17). The lobby's reverb bus was made while the street's traffic held every binaural voice
/// but the direct sounds' reserve, so it had no binaural head and stayed 3D; FMOD's panner folds a 3D bus's two
/// channels into one even at a 3D level of 0, and the room's binaural tail came out the same in both ears (the
/// capture: 0.98 to 0.99 between the ears from 80 ms on; AudioLab --clap-city busy reproduces it at 0.99 to 1.00).
/// </summary>
public class ReverbBusPanningTests
{
    [Fact]
    public void YourOwnRoomsTailIsNeverFoldedByFmodsPanner()
    {
        Assert.False(FmodAudioProvider.ReverbBusPannedByFmod(hasHead: false, outside: false));
        Assert.False(FmodAudioProvider.ReverbBusPannedByFmod(hasHead: true, outside: false));
    }

    [Fact]
    public void OnlyARoomHeardFromOutsideWithNoHeadIsPlacedByFmod()
    {
        Assert.True(FmodAudioProvider.ReverbBusPannedByFmod(hasHead: false, outside: true));
        Assert.False(FmodAudioProvider.ReverbBusPannedByFmod(hasHead: true, outside: true));
    }
}
