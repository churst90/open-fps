using System.Linq;
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

    /// <summary>
    /// The touchdown is heard, and for as long as the wheels slide. The tyre model is asked how much
    /// of its grip is in use; the slip ratio was handed to it as that, so a slip of one sat in its
    /// squeal window for the first fifth of the spin-up, and a light single's 57 ms went through the
    /// window faster than the tyre's own attack: its touchdown made no sound at all.
    /// </summary>
    [Theory]
    [InlineData("piston_single", 0.03, 0.09)]
    [InlineData("airliner", 0.22, 0.36)]
    public void A_touchdown_slides_for_as_long_as_the_wheels_take_to_spin_up(string preset, double atLeast, double atMost)
    {
        var p = AircraftProfile.ByName(preset);
        var s = new OpenFPS.Client.AudioEngine.Core.Aircraft.AircraftSynth(p, 48000f, 5);
        s.SetListener(new System.Numerics.Vector3(-40f, 0f, 0f));
        s.PlaceAtLever(0.12f);
        for (int i = 0; i < 48000; i++) s.Step();
        s.Touchdown(p.ApproachSpeedMps);
        var frames = new System.Collections.Generic.List<double>();
        double e = 0; int n = 0;
        for (int i = 0; i < 48000; i++)
        {
            s.GroundSpeed = p.ApproachSpeedMps;
            s.Step();
            e += s.Gear * (double)s.Gear;
            if (++n == 480) { frames.Add(10 * System.Math.Log10(e / n / 4e-10)); e = 0; n = 0; }
        }
        double rolling = frames.Skip(70).Average();
        int sliding = frames.TakeWhile(f => f > rolling + 10).Count();
        _o.WriteLine($"{preset}: {frames.Take(sliding).DefaultIfEmpty(0).Max():F0} dB at a metre for {sliding * 10} ms, then rolling at {rolling:F0}");
        Assert.InRange(sliding * 0.01, atLeast, atMost);
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
