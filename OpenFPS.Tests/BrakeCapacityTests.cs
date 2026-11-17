using OpenFPS.Physics;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Engine;

namespace OpenFPS.Tests;

/// <summary>
/// The sound's car stops at its own tyres' limit (docs/PHYSICS_BOUNDARY.md 16.8, change A): one car,
/// one brake. The drivers' requests stay the decelerations they were below the old 7.5 m/s².
/// </summary>
public class BrakeCapacityTests
{
    private const int Rate = 48000;
    private const float Dt = 1f / Rate;

    [Theory]
    [InlineData("i4_midsize")]   // a car's 0.95 g
    [InlineData("sportbike")]    // a sports bike's 1.1 g
    [InlineData("diesel_truck")] // a loaded truck's 0.75 g
    public void TheSoundCarReportsItsOwnTyresCapacity(string preset)
    {
        var v = VehicleProfile.ByName(preset);
        var dl = new Driveline(v);
        Assert.Equal(Brakes.Capacity(v.Tyres.Mechanics, Earth.StandardGravity), dl.MaxBrake);
    }

    /// <summary>
    /// The lab's Driver asked 0.7 of 7.5 m/s² (5.25) for a Braking order. It still asks for that
    /// deceleration, now as a share of the tyres' limit. Tolerance 1e-3 m/s².
    /// </summary>
    [Fact]
    public void TheLabsBrakingRequestIsTheSameDecelerationAsBefore()
    {
        var v = VehicleProfile.ByName("i4_midsize");
        var engine = new EngineSynth(v.Engine, Rate, 4);
        var dl = new Driveline(v);
        var driver = new Driver(dl, engine);
        driver.Apply(new DriveOrder(DriverAction.Braking, 1f), 0f, Dt);
        Assert.Equal(0.7f * Driveline.TunedBrakeMps2, dl.Brake * dl.MaxBrake, 3);
    }

    /// <summary>
    /// A VirtualDriver asking for 3 m/s² asks for the same deceleration as the old
    /// <c>-wantAccel / 6</c> of 7.5: a wantAccel of −2.4 was 2.4/6 of 7.5, 3 m/s². Tolerance 1e-3.
    /// </summary>
    [Fact]
    public void AVirtualDriversThreeMetreRequestIsTheSameDecelerationAsBefore()
    {
        var v = VehicleProfile.ByName("i4_midsize");
        var engine = new EngineSynth(v.Engine, Rate, 4);
        var dl = new Driveline(v);
        // Target 0 while rolling at 2.4 / 0.9: err * 0.9 is the whole wantAccel on the first call.
        dl.Teleport(2.4f / 0.9f);
        var driver = new VirtualDriver(dl, engine) { TargetSpeed = 0f };
        driver.Apply(Dt);
        Assert.Equal(3f, dl.Brake * dl.MaxBrake, 3);
    }

    /// <summary>
    /// A full brake on the bike, road load subtracted (the profile's drag and rolling zeroed), stops in
    /// the closed-form v / 10.8: 1.1 g is 10.7873 m/s², so the 10.8 form is 3 ms long at 100 km/h. The
    /// test allows one sample either way (20.8 µs at 48 kHz).
    /// </summary>
    [Fact]
    public void AFullBrakeOnTheBikeStopsInTheClosedFormTime()
    {
        var v = VehicleProfile.SportBike with { DragArea = 0f, RollingResistance = 0f };
        var dl = new Driveline(v);
        var engine = new EngineSynth(v.Engine, Rate, 4);
        const float from = 100f / 3.6f;
        dl.Teleport(from);
        dl.Brake = 1f;
        float t = 0f;
        while (dl.Speed > 0f && t < 10f)
        {
            dl.Step(engine, Dt);
            t += Dt;
        }
        Assert.Equal(10.8f, dl.MaxBrake, 1);
        float closedForm = from / 10.8f;
        Assert.InRange(t, closedForm - Dt, closedForm + Dt);
    }
}
