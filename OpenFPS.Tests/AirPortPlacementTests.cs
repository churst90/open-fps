using System;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A vehicle is a body with a front and a back, and its valves are on one end or the other. At a
/// stop a bus's door valve, kneeling valve and door beeper are at the front door, beside whoever
/// is waiting; they all used to come out of the tailpipe, eleven metres away.
/// </summary>
public class AirPortPlacementTests
{
    private static bool FrontOf(VehicleProfile v, string port)
    {
        var spec = Array.Find(ModelLibrary.Air(v.AirSystem!).Ports, p => p.Name == port)!;
        return EngineVoiceState.NearerFront(v, v.LengthMetres * 0.5f - spec.AlongMetres);
    }

    [Fact]
    public void ABusDoorAndKneelAreAtTheFrontAndItsBrakeReleaseAtTheBack()
    {
        var bus = VehicleProfile.ByName("school_bus");
        Assert.True(FrontOf(bus, "door"));
        Assert.True(FrontOf(bus, "kneel"));
        Assert.False(FrontOf(bus, "service_release"));
    }

    [Fact]
    public void TheFrontShareIsExactlyTheFrontPorts()
    {
        var air = new OpenFPS.Client.AudioEngine.Core.Pneumatics.AirSystem(ModelLibrary.Air("transit_bus"));
        air.PlaceAtFront(p => p.Name == "door", compressorAtFront: false);
        air.EngineRpm = 0f;                 // no compressor: only the ports
        air.Vent("door");
        double all = 0, front = 0;
        for (int i = 0; i < 44100 / 4; i++) { float y = air.Step(); all += y * y; front += air.FrontOut * air.FrontOut; }
        Assert.True(all > 0, "the door valve made no sound");
        Assert.Equal(all, front, 6);

        air.Vent("service_release");
        double rearOnly = 0;
        for (int i = 0; i < 44100 / 4; i++) { float y = air.Step(); rearOnly += (y - air.FrontOut) * (y - air.FrontOut); }
        Assert.True(rearOnly > 0, "the brake release was counted at the front");
    }
}
