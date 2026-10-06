using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A wall's copy of a recorded loop arrives as much under the loop as the wall sends back: the arrival's gain against
/// the direct sound, once. It came out some 40 dB under that, placed with no reference distance, its spreading counted
/// at the image as well as in its gain, and its middle band in both its volume and its EQ.
/// </summary>
public class LoopEchoLevelTests
{
    [Theory]
    [InlineData(10f, 30f, 3f)]
    [InlineData(4f, 25f, 1.2f)]
    [InlineData(20f, 26f, 8f)]
    public void ACopyArrivesAsMuchUnderItsLoopAsTheWallSendsBack(float direct, float pathLength, float minDistance)
    {
        const float range = 200f, volume = 0.7f;
        // A concrete wall: what it keeps, over the longer way.
        float relative = EarlyReflections.Keep(0.02f) * direct / pathLength;
        var path = new AcousticPathData
        {
            IsReflection = true, EffectiveDistance = pathLength,
            EqLow = relative * 0.9f, EqMid = relative, EqHigh = relative * 0.8f,
        };
        var echo = ClientAudioSystem.LoopEchoLevel(path, volume, minDistance, direct);
        float heard = echo.Volume * Loudness.RenderedGain(1f, echo.MinDistance, range * 0.8f, pathLength) * echo.EqMid;
        float loop = volume * Loudness.RenderedGain(1f, minDistance, range, direct);
        Assert.Equal(relative, heard / loop, 3);
        // Its colour is the wall's, against its middle.
        Assert.Equal(0.9f, echo.EqLow, 4);
        Assert.Equal(1f, echo.EqMid);
        Assert.Equal(0.8f, echo.EqHigh, 4);
    }
}
