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
        double loudest = frames.Take(sliding).DefaultIfEmpty(0).Max();
        // Honest level: the tyre's declared squeal at its reference slip velocity (12 m/s at the peak
        // slip), times the friction work at contact (the load on it, the speed it is dragged at), one
        // per wheel in power. The loudest 10 ms of a narrow-band noise stands a few dB over its mean.
        var gear = p.Gear!;
        double expected = gear.Tyre.SquealDb
                        + 10 * System.Math.Log10(gear.WeightOnWheelsAtTouchdown * p.ApproachSpeedMps / (12 * RoadWaterLaw.PeakSlip))
                        + 10 * System.Math.Log10(gear.Wheels);
        _o.WriteLine($"{preset}: {loudest:F0} dB at a metre for {sliding * 10} ms (the friction work says {expected:F0}), then rolling at {rolling:F0}");
        Assert.InRange(sliding * 0.01, atLeast, atMost);
        Assert.InRange(loudest, expected - 4, expected + 5);
    }

    /// <summary>
    /// A touchdown on a runway screeches: the slide is the tread's stick-slip note, not a broadband
    /// skid. Cody heard the 2026-10-06 airliner as "white noise, not screeching like tires hitting
    /// blacktop": the axle model gives the note up to a broadband slide past the limit, as a locked
    /// wheel on gravel does, and a touchdown is past the limit for its whole length. Held on the gear
    /// alone, over the first half of the slide: one strong line (the squeal at the tread's note or its
    /// octave), well over the median of the spectrum, and a spectrum far from flat.
    /// </summary>
    [Theory]
    [InlineData("piston_single")]
    [InlineData("airliner")]
    public void A_touchdown_screeches(string preset)
    {
        var p = AircraftProfile.ByName(preset);
        var s = new OpenFPS.Client.AudioEngine.Core.Aircraft.AircraftSynth(p, 48000f, 5);
        s.SetListener(new System.Numerics.Vector3(-40f, 0f, 0f));
        s.PlaceAtLever(0.12f);
        for (int i = 0; i < 48000; i++) s.Step();
        s.Touchdown(p.ApproachSpeedMps);
        int n = 2048;
        var x = new double[n];
        for (int i = 0; i < 240; i++) { s.GroundSpeed = p.ApproachSpeedMps; s.Step(); }   // 5 ms in
        for (int i = 0; i < n; i++) { s.GroundSpeed = p.ApproachSpeedMps; s.Step(); x[i] = s.Gear * (0.5 - 0.5 * System.Math.Cos(2 * System.Math.PI * i / n)); }
        var (peakHz, peakOverMedianDb, flatness) = Spectrum(x, 48000, 150, 8000);
        float squeal = p.Gear!.Tyre.SquealHz;
        _o.WriteLine($"{preset}: strongest line {peakHz:F0} Hz (tread note {squeal:F0} Hz), {peakOverMedianDb:F0} dB over the median, flatness {flatness:F3}");
        Assert.True(peakOverMedianDb > 20, $"no squeal line: the strongest is {peakOverMedianDb:F0} dB over the median");
        // The broadband slide this replaced measured 0.19 and 0.32 here, its strongest line at 320 and
        // 400 Hz, the low end of a lowpassed noise; the screech 0.11, at the tread's note.
        Assert.True(flatness < 0.15, $"the slide is broadband: flatness {flatness:F3}");
        double ratio = peakHz / squeal;
        Assert.True((ratio > 0.95 && ratio < 1.5) || (ratio > 1.9 && ratio < 3.0), $"the line is at {peakHz:F0} Hz, not the tread's note");
    }

    /// <summary>Power spectrum by direct transform, 10 Hz apart: the strongest line, its height over the
    /// median, and the spectral flatness (geometric over arithmetic mean of the power).</summary>
    private static (double PeakHz, double PeakOverMedianDb, double Flatness) Spectrum(double[] x, double rate, double lo, double hi)
    {
        var power = new System.Collections.Generic.List<(double Hz, double P)>();
        for (double f = lo; f <= hi; f += 10)
        {
            double re = 0, im = 0, w = 2 * System.Math.PI * f / rate;
            for (int i = 0; i < x.Length; i++) { re += x[i] * System.Math.Cos(w * i); im -= x[i] * System.Math.Sin(w * i); }
            power.Add((f, re * re + im * im + 1e-30));
        }
        var peak = power.MaxBy(q => q.P);
        var sorted = power.Select(q => q.P).OrderBy(v => v).ToList();
        double median = sorted[sorted.Count / 2];
        double geo = System.Math.Exp(power.Average(q => System.Math.Log(q.P)));
        return (peak.Hz, 10 * System.Math.Log10(peak.P / median), geo / power.Average(q => q.P));
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
