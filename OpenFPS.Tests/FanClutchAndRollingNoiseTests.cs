using System;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// "Those trucks are so loud, when they're blocks away I can still hear the wooshing of the engine
/// fan ... the trucks are all fan." Two causes, measured: the fan was bolted solid to the crank and
/// never stopped, and the tyres of every vehicle were ten to fifteen decibels under a real one's, so
/// the fans were the only broadband sound in the city. A fan clutch on coolant temperature, and the
/// rolling noise anchored in pascals.
/// </summary>
public class FanClutchAndRollingNoiseTests
{
    private static CoolingSystem Truck(int seed = 2)
        => new(FanClutchSpec.OnOff, EngineProfile.DieselTruckI6, seed);

    private static void Run(CoolingSystem c, float seconds, float rpm, float torqueNm, float speed)
    {
        for (float t = 0f; t < seconds; t += 0.05f) c.Step(0.05f, rpm, torqueNm, speed);
    }

    /// <summary>At a city cruise the road's air carries the heat: ten minutes and the fan never
    /// comes in, and it is slipping at its disengaged fraction.</summary>
    [Fact]
    public void AtACruiseTheFanStaysOut()
    {
        var c = Truck();
        for (float t = 0f; t < 600f; t += 0.05f)
        {
            c.Step(0.05f, 1500f, 130f, 12f);
            Assert.False(c.Engaged, $"engaged at {t:F0} s, {c.Celsius:F1} C");
        }
        Assert.InRange(c.Celsius, 80f, 90f);
        Assert.Equal(FanClutchSpec.OnOff.DisengagedFraction, c.FanSpeedFraction, 3);
    }

    [Fact]
    public void IdlingItStaysOut()
    {
        var c = Truck();
        Run(c, 600f, 600f, 0f, 0f);
        Assert.False(c.Engaged);
    }

    /// <summary>Hauling at full load with little air through the core, the heat outruns the radiator
    /// and the clutch goes in within a minute or two; then it comes up to speed over its engagement
    /// time, and cruising again it cools and lets go.</summary>
    [Fact]
    public void AHardPullEngagesItAndACruiseReleasesIt()
    {
        var c = Truck();
        float t = 0f;
        while (!c.Engaged && t < 300f) { c.Step(0.05f, 1700f, 2000f, 4f); t += 0.05f; }
        Assert.True(c.Engaged, $"never engaged; {c.Celsius:F1} C after {t:F0} s");
        Assert.InRange(t, 10f, 180f);
        Run(c, 0.5f, 1700f, 2000f, 4f);
        Assert.InRange(c.FanSpeedFraction, 0.4f, 0.8f);                // half way up a one-second lock-up
        Run(c, 1f, 1700f, 2000f, 4f);
        Assert.Equal(1f, c.FanSpeedFraction, 3);

        float release = 0f;
        while (c.Engaged && release < 600f) { c.Step(0.05f, 1500f, 130f, 12f); release += 0.05f; }
        Assert.False(c.Engaged, $"never released; {c.Celsius:F1} C");
        Assert.True(c.Celsius <= FanClutchSpec.OnOff.ReleaseCelsius + 0.01f);
    }

    /// <summary>The design point: at rated power, standing, with the fan locked up, the radiator
    /// holds the coolant near 100 C rather than running away or freezing it.</summary>
    [Fact]
    public void AtRatedPowerWithTheFanInItHoldsNearTheDesignTemperature()
    {
        var e = EngineProfile.DieselTruckI6;
        var c = Truck();
        c.SetCelsius(96f);
        float rated = 0.7f * e.PeakTorqueNm;
        Run(c, 1200f, e.RedlineRpm, rated, 0f);
        Assert.True(c.Engaged);
        Assert.InRange(c.Celsius, 97f, 103f);
    }

    [Fact]
    public void AVehicleWithoutAClutchHasNoCoolingModel()
    {
        var bus = VehicleProfile.SchoolBus;
        Assert.NotNull(bus.FanClutch);
        Assert.NotNull(VehicleProfile.ByName("diesel_truck").FanClutch);
        Assert.Null(VehicleProfile.ByName("v6").FanClutch);
    }

    // ── Rolling noise ─────────────────────────────────────────────────────────────────────────

    private static double RollingDb(TyreProfile t, float speed, float pa)
    {
        var v = new VehicleSynth.TyreVoice();
        var rng = new Random(3);
        double sum = 0; int n = 0;
        for (int i = 0; i < VehicleSynth.SampleRate * 3; i++)
        {
            float y = VehicleSynth.Tyre(t, speed, 0f, rng, ref v, pa);
            if (i < VehicleSynth.SampleRate / 2) continue;
            sum += y * (double)y; n++;
        }
        return 10 * Math.Log10(sum / n / (20e-6 * 20e-6));
    }

    /// <summary>What comes out is what was declared, at 20 m/s, on a treaded tyre, a slick and a
    /// truck tyre alike: the roar's filters and the tread tone are normalised, not guessed.</summary>
    [Theory]
    [InlineData("sports_asphalt")]
    [InlineData("race_slick")]
    [InlineData("truck_asphalt")]
    public void TheRollingNoiseIsTheDeclaredLevel(string key)
    {
        var t = TyreProfile.ByName(key);
        float pa = 20e-6f * MathF.Pow(10f, t.ReferenceDb / 20f);
        Assert.InRange(RollingDb(t, 20f, pa), t.ReferenceDb - 1.0, t.ReferenceDb + 1.0);
    }

    /// <summary>About 30 log10 of speed: half the speed, nine decibels down.</summary>
    [Fact]
    public void ItFallsAbout30Log10OfSpeed()
    {
        var t = TyreProfile.SportsOnAsphalt;
        float pa = 20e-6f * MathF.Pow(10f, t.ReferenceDb / 20f);
        double drop = RollingDb(t, 20f, pa) - RollingDb(t, 10f, pa);
        Assert.InRange(drop, 7.5, 10.5);
    }

    /// <summary>The band a real tyre makes: well under the peak at 250 Hz, and a long way under it
    /// at 8 kHz. Either end left in was a rumble or a hiss.</summary>
    [Fact]
    public void TheRoarIsABandRoundAKilohertz()
    {
        Assert.True(VehicleSynth.RollingBandGain(0.2f) < VehicleSynth.RollingBandGain(0.3f));
        var v = new VehicleSynth.TyreVoice();
        var rng = new Random(5);
        var t = TyreProfile.SportsOnAsphalt with { TreadBlocks = 0 };
        int n = 1 << 16;
        var y = new float[n];
        for (int i = 0; i < n + 4410; i++)
        {
            float s = VehicleSynth.Tyre(t, 15f, 0f, rng, ref v, 1f);
            if (i >= 4410) y[i - 4410] = s;
        }
        double Band(double lo, double hi)
        {
            // Goertzel across the band, a bin every 20 Hz.
            double e = 0;
            for (double f = lo; f < hi; f += 20)
            {
                double w = 2 * Math.PI * f / VehicleSynth.SampleRate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
                for (int i = 0; i < n; i++) { double s0 = y[i] + c * s1 - s2; s2 = s1; s1 = s0; }
                e += s1 * s1 + s2 * s2 - c * s1 * s2;
            }
            return 10 * Math.Log10(e);
        }
        double peak = Band(710, 1410), low = Band(177, 354), top = Band(5660, 11300);
        Assert.True(peak - low > 5, $"250 Hz octave only {peak - low:F1} dB under the peak");
        Assert.True(peak - top > 12, $"8 kHz octave only {peak - top:F1} dB under the peak");
    }

    /// <summary>A car's four tyres at 72 km/h: the pass-by anchor, about 90 dB at a metre.</summary>
    [Fact]
    public void FourCarTyresAreThePassByAnchor()
    {
        var v = VehicleProfile.ByName("v6");
        double total = v.Tyres.ReferenceDb + 10 * Math.Log10(v.TyreCount);
        Assert.InRange(total, 89, 92);
        Assert.Equal(18, VehicleProfile.ByName("diesel_truck").TyreCount);
        Assert.Equal(2, VehicleProfile.ByName("sportbike").TyreCount);
    }
}

/// <summary>
/// "One of the trucks steps the pitch when it goes by me ... the pitch steps hard, not smooth." The
/// truck's automatic changed up at a light cruise at 1,020 rpm and down at 1,100, so it hunted
/// between gears for good. Every vehicle, held at a steady city speed, must settle in a gear.
/// </summary>
public class GearHuntingTests
{
    public static TheoryData<string> Keys()
    {
        var d = new TheoryData<string>();
        foreach (var k in OpenFPS.Common.VehicleProfile.Presets.Keys) d.Add(k);
        return d;
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void AtASteadyCruiseItSettlesInAGear(string key)
    {
        var v = OpenFPS.Common.VehicleProfile.ByName(key);
        foreach (float kmh in new[] { 30f, 45f })
        {
            float speed = kmh / 3.6f;
            var voice = new OpenFPS.Client.AudioEngine.Fmod.EngineVoiceState(v, 44100, 7) { TargetSpeed = speed };
            voice.PlaceAtSpeed(speed);
            voice.Revive();
            var buf = new float[1024];
            int changes = 0, last = -1;
            for (int b = 0; b < 44100 * 30 / 1024; b++)
            {
                voice.Render(buf);
                if (b < 44100 * 10 / 1024) continue;              // ten seconds to settle
                int g = voice.Driveline.Gear;
                if (g != last && last >= 0) changes++;
                last = g;
            }
            // Twenty seconds at a steady speed: nothing to change gear for. A change up and its
            // landing are two readings; allow one.
            Assert.True(changes <= 2, $"{key} at {kmh} km/h changed gear {changes} times in 20 s");
        }
    }
}
