using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Data;
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
    private static AcousticPathData Direct(float low, float mid, float high)
        => new() { AirLowDb = low, AirMidDb = mid, AirHighDb = high };

    [Fact]
    public void AnEchoIsDarkerThanTheCarByItsLongerPath()
    {
        // The air's loss in dB is proportional to distance: an echo three times the car's distance
        // has lost three times as much, in every band.
        var (low, mid, high) = EngineReflections.EchoAir(Direct(0.04f, 0.23f, 4.2f), 40f, 120f);
        Assert.Equal(0.12f, low, 4);
        Assert.Equal(0.69f, mid, 4);
        Assert.Equal(12.6f, high, 4);
    }

    [Fact]
    public void ANearCarsEchoStillPaysForItsPath()
    {
        // Too close for a rate: the echo off a wall 60 m round takes the standard atmosphere's loss.
        var (_, _, high) = EngineReflections.EchoAir(Direct(0f, 0f, 0f), 0.5f, 60f);
        var (_, _, expected) = OpenFPS.Client.AudioEngine.Core.AudioPhysics.AirLossDb(60f, 0.5f, 20f, 1013.25f);
        Assert.Equal(expected, high, 4);
        Assert.True(high > 5f);
    }
}
