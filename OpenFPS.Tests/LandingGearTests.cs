using OpenFPS.Common;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// How long an aeroplane's wheels slide at touchdown: their inertia against the grip the load on them
/// gives, and the load is what the gear takes in stopping the sink (LandingGearSpec). Resonance found
/// it (2026-10-06): one declared weight-on-wheels for every aeroplane spun a light single's wheels up
/// in 0.31 s, as an airliner's, where its own preset says a twentieth of a second.
/// </summary>
public class LandingGearTests
{
    private readonly ITestOutputHelper _o;
    public LandingGearTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void A_light_singles_wheels_chirp_and_an_airliners_scrub()
    {
        var single = AircraftProfile.ByName("piston_single");
        var airliner = AircraftProfile.ByName("airliner");
        float tSingle = single.Gear!.SpinUpSeconds(single.ApproachSpeedMps);
        float tAirliner = airliner.Gear!.SpinUpSeconds(airliner.ApproachSpeedMps);
        _o.WriteLine($"light single {tSingle * 1000f:F0} ms ({single.Gear.WeightOnWheelsAtTouchdown:P0} of its weight), "
                   + $"airliner {tAirliner * 1000f:F0} ms ({airliner.Gear.WeightOnWheelsAtTouchdown:P0})");
        Assert.InRange(tSingle, 0.03f, 0.08f);
        // As approved by ear before the load was worked out: 297 ms.
        Assert.InRange(tAirliner, 0.25f, 0.35f);
    }

    [Fact]
    public void A_softer_gear_puts_less_on_the_wheels_and_they_slide_longer()
    {
        var gear = AircraftProfile.ByName("airliner").Gear!;
        var soft = gear with { StrokeMetres = gear.StrokeMetres * 2f };
        var harder = gear with { TouchdownSinkMps = gear.TouchdownSinkMps * 1.5f };
        Assert.True(soft.SpinUpSeconds(70f) > gear.SpinUpSeconds(70f) * 1.8f);
        Assert.True(harder.SpinUpSeconds(70f) < gear.SpinUpSeconds(70f) * 0.5f);
        // Faster over the ground is more wheel speed to make up.
        Assert.True(gear.SpinUpSeconds(80f) > gear.SpinUpSeconds(60f));
    }
}
