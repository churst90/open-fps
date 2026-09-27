using System;
using System.Collections.Generic;
using System.Linq;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Common;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A turbo spools as soon as the throttle opens. Reported 2026-09-27 on the compound-turbo pickup:
/// "when it hits the gas the turbo doesn't spin up right away, the whine stays constant until a
/// certain point". The spool's target was the LARGER of the idle freewheel and the throttle's share,
/// and the throttle's share only passed the freewheel at 1,300-1,700 rpm.
/// </summary>
public class TurboSpoolTests
{
    private const int Sr = 44100;
    private readonly ITestOutputHelper _o;
    public TurboSpoolTests(ITestOutputHelper o) => _o = o;

    [Theory]
    [InlineData("duramax_compound")]
    [InlineData("cummins_compound")]
    public void A_compound_turbo_starts_spooling_the_moment_the_pedal_goes_down(string preset)
    {
        var v = VehicleProfile.Presets[preset]();
        var engine = new EngineSynth(v.Engine, Sr, 4);
        var dl = new Driveline(v);
        var drv = new VirtualDriver(dl, engine) { TargetSpeed = 0f };
        float dt = 1f / Sr;
        for (int i = 0; i < Sr * 3; i++) { drv.Apply(dt); dl.Step(engine, dt); }
        float idle = engine.Spool;

        drv.TargetSpeed = 25f;
        var trace = new List<(double T, float Rpm, float Spool, float Throttle)>();
        for (int i = 0; i < Sr * 3; i++)
        {
            drv.Apply(dt); dl.Step(engine, dt);
            if (i % (Sr / 10) == 0) trace.Add((i / (double)Sr, engine.Rpm, engine.Spool, engine.Throttle));
        }
        _o.WriteLine($"idle spool {idle:F2}");
        foreach (var t in trace) _o.WriteLine($"{t.T:F1}s  {t.Rpm,5:F0} rpm  throttle {t.Throttle:F2}  spool {t.Spool:F2}");

        // Half a second in, with the pedal down, the shaft is on its way up — not still freewheeling.
        var half = trace.First(t => t.T >= 0.5);
        Assert.True(half.Throttle > 0.2f, "the driver did not put the pedal down");
        Assert.True(half.Spool > idle + 0.05f, $"spool {half.Spool:F2} at 0.5 s, idle {idle:F2}: it has not started");
        // ...and it keeps climbing toward full boost over the pull, lifts at the limiter and all.
        Assert.True(trace.Max(t => t.Spool) > half.Spool + 0.1f, $"it stopped at {trace.Max(t => t.Spool):F2}");
    }

    [Theory]
    [InlineData("duramax_compound")]
    [InlineData("cummins_compound")]
    public void Pulling_away_gently_raises_the_target_above_the_freewheel_at_once(string preset)
    {
        // Traffic: a quarter throttle, the revs only just off idle.
        var e = VehicleProfile.Presets[preset]().Engine;
        float rpm = e.IdleRpm * 1.2f;
        float idleTarget = EngineSynth.TurboTarget(0f, rpm, e);
        float gentle = EngineSynth.TurboTarget(0.25f, rpm, e);
        _o.WriteLine($"{preset}: at {rpm:F0} rpm the target is {idleTarget:F2} off the pedal, {gentle:F2} at a quarter throttle");
        Assert.True(gentle > idleTarget + 0.005f, "a touch of throttle does nothing to the turbo");
        // And it keeps climbing with the revs, all the way.
        float prev = gentle;
        for (float r = rpm; r <= e.RedlineRpm; r += 200f)
        {
            float t = EngineSynth.TurboTarget(0.25f, r, e);
            Assert.True(t >= prev - 1e-4f, $"the target fell at {r:F0} rpm");
            prev = t;
        }
    }

    [Fact]
    public void A_turbo_with_no_freewheel_is_unchanged()
    {
        var e = VehicleProfile.Presets.Values.Select(p => p().Engine)
            .First(x => x.Induction == Induction.Turbocharged && x.Mechanical.TurboIdleSpool == 0f);
        foreach (float thr in new[] { 0f, 0.3f, 1f })
            for (float r = e.IdleRpm; r <= e.RedlineRpm; r += 250f)
                Assert.Equal(Math.Clamp(thr * Math.Min(1f, (r - e.IdleRpm) / (0.35f * e.RedlineRpm)), 0f, 1f),
                             EngineSynth.TurboTarget(thr, r, e), 4);
    }
}
