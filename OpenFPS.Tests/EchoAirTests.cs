using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// An echo has lost as much of its top to the air as its own path costs. It used to take the car's
/// air absorption, so an echo that came 120 m round a facade was as bright as a car 40 m away, and a
/// passing car was heard on the far side of the street — "inside out".
/// </summary>
public class EchoAirTests
{
    [Fact]
    public void AnEchoIsDarkerThanTheCarByItsLongerPath()
    {
        // A car 40 m away with the air absorption the game gives that distance at the reference rate.
        float direct = (40f - AcousticConstants.AirAbsorptionMinDist) / AcousticConstants.AirAbsorptionReferenceDist;
        float echo = EngineReflections.EchoAir(direct, 40f, 120f);
        Assert.Equal((120f - AcousticConstants.AirAbsorptionMinDist) / AcousticConstants.AirAbsorptionReferenceDist, echo, 4);
        Assert.True(echo > 4f * direct);
    }

    [Fact]
    public void ANearCarsEchoStillPaysForItsPath()
    {
        // Under 15 m the car itself has no air loss; its echo off a wall 60 m round does.
        float echo = EngineReflections.EchoAir(0f, 8f, 60f);
        Assert.Equal(45f / AcousticConstants.AirAbsorptionReferenceDist, echo, 4);
    }

    [Fact]
    public void NeverMoreThanTheAirCanTake()
        => Assert.Equal(AcousticConstants.AirAbsorptionMaxMuffle, EngineReflections.EchoAir(0.5f, 100f, 2000f), 4);
}
