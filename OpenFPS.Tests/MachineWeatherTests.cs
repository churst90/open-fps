using OpenFPS.Client.AudioEngine.Core.Yard;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// What drives a small machine that nothing on the wire tells it (MachineVoiceState): an air
/// conditioner's thermostat and its compressor's load answer to the weather, and two of one model are
/// two machines, each turning at its own speed. Resonance found its port without any of it
/// (2026-10-06); here the thermostat was a clock of 100-220 s on and 60-140 s off whatever the
/// weather, and two window units' fans and pumps turned at exactly the same speed.
/// </summary>
public class MachineWeatherTests
{
    private readonly ITestOutputHelper _o;
    public MachineWeatherTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }

    private const float Rate = 48000f;

    [Fact]
    public void Two_window_units_turn_at_their_own_speeds()
    {
        var spec = SmallMachineSpec.ByName("ac_window");
        var a = new MachineVoiceState(spec, Rate, entityId: 1001, seed: 5) { AmbientCelsius = 36f };
        var b = new MachineVoiceState(spec, Rate, entityId: 1002, seed: 5) { AmbientCelsius = 36f };
        var buf = new float[(int)(Rate * 6f)];
        a.Render(buf);
        b.Render(buf);
        _o.WriteLine($"fans {a.Machine.BladeRpm:F1} and {b.Machine.BladeRpm:F1} rpm, pumps {a.Machine.CompressorHz:F3} and {b.Machine.CompressorHz:F3} Hz");
        Assert.True(MathF.Abs(a.Machine.BladeRpm - b.Machine.BladeRpm) > 0.5f, "two fans turn at one speed");
        Assert.True(a.CompressorCalled && b.CompressorCalled, "a 36 °C day calls for both compressors");
        Assert.True(MathF.Abs(a.Machine.CompressorHz - b.Machine.CompressorHz) > 0.005f, "two pumps turn at one speed");
    }

    /// <summary>
    /// A blade row is the same machine whatever its seed. A blade tracking a little ahead of its slot
    /// was found again on every sample of the sliver it led by, so a condenser whose blade 0 drew a
    /// negative scatter (half of all seeds, and the seed is the entity id) ran its fan 11 dB over the
    /// level it declares.
    /// </summary>
    [Fact]
    public void A_fan_is_as_loud_whatever_its_seed()
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (int seed in new[] { 192, 100, 4769, 440, 11, 17, 5, 3 })
        {
            var s = new SmallMachineSynth(SmallMachineSpec.ByName("ac_condenser"), 44100f, seed) { CompressorOn = false };
            double e = 0; int n = 0;
            for (int i = 0; i < 88200; i++) { s.Step(); if (i < 22050) continue; e += s.Blades * (double)s.Blades; n++; }
            float db = (float)(10 * Math.Log10(e / n / 4e-10));
            lo = MathF.Min(lo, db); hi = MathF.Max(hi, db);
        }
        _o.WriteLine($"condenser fan {lo:F1} to {hi:F1} dB at a metre over eight seeds");
        Assert.True(hi - lo < 1.5f, $"the fan is {lo:F1} to {hi:F1} dB depending on its seed");
    }

    [Theory]
    [InlineData(12f, 0.0, 0.0)]
    [InlineData(26.5f, 0.3, 0.7)]
    [InlineData(37f, 1.0, 1.0)]
    public void A_thermostat_runs_the_compressor_for_the_share_of_the_time_the_weather_asks(float celsius, double low, double high)
    {
        // Twenty houses, eight hours each.
        double on = 0, total = 0;
        int switches = 0;
        for (int house = 0; house < 20; house++)
        {
            var t = new Thermostat(new ThermostatSpec(), house * 7919 + 1);
            bool was = t.Step(0f, celsius);
            for (int s = 0; s < 8 * 3600; s++)
            {
                bool now = t.Step(1f, celsius);
                if (now) on++;
                if (now != was) switches++;
                was = now;
                total++;
            }
        }
        double duty = on / total, cyclesPerHour = switches / 2.0 / (20 * 8);
        _o.WriteLine($"{celsius} °C: on {duty:P0} of the time, {cyclesPerHour:F1} cycles an hour");
        Assert.InRange(duty, low, high);
        if (low > 0.2 && high < 0.8) Assert.InRange(cyclesPerHour, 1.5, 3.2);   // NEMA DC 3: three an hour at most
    }

    [Fact]
    public void A_hotter_day_slows_the_compressor_and_a_cool_one_stops_it()
    {
        var comp = SmallMachineSpec.AirConditionerWindow.Compressor!;
        Assert.True(comp.PulsationHzAt(CompressorSpec.LoadAt(38f)) < comp.PulsationHzAt(CompressorSpec.LoadAt(22f)) - 0.3f);
        Assert.Equal(comp.PulsationHz, comp.PulsationHzAt(CompressorSpec.LoadAt(35f)), 2);   // the rating point

        var cool = new MachineVoiceState(SmallMachineSpec.ByName("ac_window"), Rate, entityId: 77, seed: 1) { AmbientCelsius = 10f };
        cool.Render(new float[(int)(Rate * 1f)]);
        Assert.False(cool.CompressorCalled, "a 10 °C day called for the compressor");
        Assert.False(cool.Machine.CompressorOn);
        Assert.True(cool.Machine.BladeRpm > 900f, "the fan runs on");
    }
}
