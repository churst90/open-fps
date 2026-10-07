using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The water on the roads (RoadWater, PuddleField), the grip it leaves (RoadWaterLaw, WheelDynamics),
/// what it does to drivers, and its sound (WetTyres through the engine voice). docs/WET_ROADS.md.
/// </summary>
public class WetRoadTests
{
    private readonly ITestOutputHelper _o;
    public WetRoadTests(ITestOutputHelper o) => _o = o;

    private static readonly byte Asphalt = RoadSurfaces.IndexOf("Asphalt");

    private static RoadWater Settled(float rain)
    {
        var w = new RoadWater();
        w.Step(rain, 0.05f, 1f);
        return w;
    }

    // ── The water ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Gallaway's equation 16 (0.00338 in inches, feet and in/h; 0.01485 in mm, m and mm/h):
    /// over a 0.5 mm texture on a 2 % cross-fall, one lane (3.65 m) in 25 mm/h stands 0.33 mm above it,
    /// in 50 mm/h 0.75, and two lanes (7.3 m) in 25 mm/h 0.62 (worked through from the report).</summary>
    [Fact]
    public void The_sheet_is_gallaways_film()
    {
        Assert.Equal(0.33f, RoadWaterLaw.SheetDepthMm(25f, 3.65f, 0.5f, 0.02f), 2);
        Assert.Equal(0.75f, RoadWaterLaw.SheetDepthMm(50f, 3.65f, 0.5f, 0.02f), 2);
        Assert.Equal(0.62f, RoadWaterLaw.SheetDepthMm(25f, 7.3f, 0.5f, 0.02f), 2);
        Assert.Equal(0f, RoadWaterLaw.SheetDepthMm(0f, 2.5f, 0.7f, 0.02f));
        // Deeper further down the cross-fall, and in heavier rain.
        Assert.True(RoadWaterLaw.SheetDepthMm(25f, 3.3f, 0.7f, 0.02f) > RoadWaterLaw.SheetDepthMm(25f, 1f, 0.7f, 0.02f));
        Assert.True(RoadWaterLaw.SheetDepthMm(70f, 2.5f, 0.7f, 0.02f) > RoadWaterLaw.SheetDepthMm(25f, 2.5f, 0.7f, 0.02f));
    }

    [Fact]
    public void A_dry_road_has_no_water_and_full_grip()
    {
        var w = Settled(0f);
        Assert.Equal(0f, w.WaterMm(Asphalt, 2.5f, 1f, 3.5f));
        Assert.Equal(0f, w.PuddleFill);
        Assert.Equal(1f, RoadWaterLaw.GripFactor(Asphalt, 0f, 20f, 220f, 5f));
    }

    [Fact]
    public void Rain_fills_the_texture_then_runs_as_a_sheet()
    {
        var light = Settled(Rainfall.LightRate);
        var heavy = Settled(Rainfall.HeavyRate);
        float holds = RoadWaterLaw.HoldsMm(Asphalt);
        Assert.Equal(holds, light.TextureMm(Asphalt), 3);
        Assert.True(Settled(Rainfall.ViolentRate).WaterMm(Asphalt, 3f, 0.5f, 3.5f) > holds + 0.2f, "violent rain stands above the texture by the kerb");
        // In heavy rain the gutter's flow reaches into the road: the kerb is wetter than the crown.
        Assert.True(heavy.WaterMm(Asphalt, 3.4f, 0.1f, 3.5f) > heavy.WaterMm(Asphalt, 1f, 2.5f, 3.5f) + 2f);
    }

    [Fact]
    public void The_sheet_drains_in_minutes_and_the_texture_dries_by_evaporation()
    {
        var w = Settled(Rainfall.HeavyRate);
        float holds = RoadWaterLaw.HoldsMm(Asphalt);
        for (int k = 0; k < 60; k++) w.Step(0f, 0f, 10f);              // ten minutes, no evaporation
        float afterTen = w.WaterMm(Asphalt, 2.5f, 1f, 3.5f);
        _o.WriteLine($"ten minutes after heavy rain, no evaporation: {afterTen:F2} mm");
        Assert.InRange(afterTen, holds - 0.01f, holds + 0.25f);
        // A humid still night: nothing takes it.
        for (int k = 0; k < 360; k++) w.Step(0f, 0f, 10f);
        Assert.Equal(holds, w.TextureMm(Asphalt), 3);
        // Half a millimetre an hour of evaporation dries the 0.7 mm texture in about an hour and a half.
        for (int k = 0; k < 6 * 100; k++) w.Step(0f, 0.5f, 10f);
        Assert.Equal(0f, w.TextureMm(Asphalt), 3);
    }

    [Fact]
    public void Evaporation_follows_the_sun_the_wind_and_the_air()
    {
        float still = RoadWaterLaw.EvaporationMmPerHour(15f, 0.85f, 0.5f, 0f);
        float windy = RoadWaterLaw.EvaporationMmPerHour(15f, 0.85f, 6f, 0f);
        float sunny = RoadWaterLaw.EvaporationMmPerHour(15f, 0.85f, 0.5f, 400f);
        float saturated = RoadWaterLaw.EvaporationMmPerHour(15f, 1f, 6f, 0f);
        _o.WriteLine($"still {still:F3}, windy {windy:F3}, sunny {sunny:F3}, saturated {saturated:F3} mm/h");
        Assert.True(windy > still && sunny > windy && saturated == 0f);
        // Noon sun on a clear day nets several hundred W/m²; midnight none.
        Assert.True(RoadWaterLaw.NetRadiationWm2(12f, 172, 0f, 45f) > 500f);
        Assert.True(RoadWaterLaw.NetRadiationWm2(0f, 172, 0f, 45f) < 0f);
    }

    [Fact]
    public void The_state_crosses_the_wire_whole()
    {
        var a = Settled(Rainfall.HeavyRate);
        for (int k = 0; k < 30; k++) a.Step(0f, 0.3f, 10f);
        var b = new RoadWater();
        b.Load(a.Save());
        foreach (float x in new[] { 0.5f, 1.5f, 2.5f, 3.3f })
            Assert.Equal(a.WaterMm(Asphalt, x, 3.5f - x, 3.5f), b.WaterMm(Asphalt, x, 3.5f - x, 3.5f), 5);
        Assert.Equal(a.PuddleFill, b.PuddleFill, 5);
        // An older server sends nothing: dry.
        var c = new RoadWater();
        c.Load(null);
        Assert.Equal(0f, c.WaterMm(Asphalt, 2f, 1f, 3.5f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.01f)]
    [InlineData(0.7f)]
    [InlineData(3.2f)]
    [InlineData(20f)]
    public void A_wheels_water_survives_its_byte(float mm)
    {
        var w = WheelState.Encode(3000f, 40f, 0f, 0f, Asphalt, 0.1f, mm);
        Assert.InRange(w.WaterMm, mm * 0.94f - 0.001f, mm * 1.06f + 0.001f);
    }

    /// <summary>The state packer copies wheels as their bytes: it must take the struct's own size, which
    /// the water byte made ten, and bring the water back.</summary>
    [Fact]
    public void Wheels_with_their_water_survive_the_state_packer()
    {
        var wheels = new[] { WheelState.Encode(3000f, 40f, 0.01f, 0.02f, Asphalt, 0.3f, 0.7f), WheelState.Encode(3100f, 41f, 0f, 0f, Asphalt, 0.2f, 12f) };
        var states = new List<EntityState>
        {
            new() { EntityId = 7, LinearVelocity = new Vector3(1f, 0f, 2f), Wheels = wheels, TyreDemand = 9 },
            new() { EntityId = 8, LinearVelocity = new Vector3(0f, 0f, 1f) },
        };
        var back = new List<EntityState>();
        StatePacking.Unpack(StatePacking.Pack(states, 0, 2), back);
        Assert.Equal(2, back.Count);
        Assert.Equal(8, back[1].EntityId);
        Assert.Equal(wheels, back[0].Wheels);
        Assert.Equal(12f, back[0].Wheels![1].WaterMm, 0);
    }

    // ── Puddles ───────────────────────────────────────────────────────────────────────────────

    private static List<RoadData> Street() => new()
    {
        new RoadData
        {
            Id = "test_street", WidthMetres = 7f,
            Centreline = new List<Vector3> { new(0f, 0f, 0f), new(600f, 0f, 0f) },
            Lanes = new List<LaneData> { new() { OffsetMetres = 1.75f, Direction = 1 }, new() { OffsetMetres = -1.75f, Direction = -1 } },
        },
    };

    [Fact]
    public void Puddles_are_the_same_wherever_the_roads_are_loaded()
    {
        var a = new PuddleField(Street());
        var b = new PuddleField(Street());
        Assert.NotEmpty(a.PuddlesOn(0));
        Assert.Equal(a.PuddlesOn(0), b.PuddlesOn(0));
        _o.WriteLine($"{a.PuddlesOn(0).Count} puddles along 600 m of kerbs");
    }

    [Fact]
    public void A_puddle_is_at_the_kerb_and_fills_and_spreads()
    {
        var field = new PuddleField(Street());
        var p = field.PuddlesOn(0)[0];
        float lateral = p.Side * 3.5f;                                   // at the kerb face
        Assert.Equal(p.DepthMm, field.PuddleMm(0, p.Along, lateral * 0.9999f, 1f), 0);
        Assert.Equal(0f, field.PuddleMm(0, p.Along, lateral, 0f));
        // Half full it is shallower and narrower: reaching half way to its rim it is dry.
        float half = field.PuddleMm(0, p.Along, p.Side * (3.5f - 0.75f * p.Reach), 0.5f);
        Assert.Equal(0f, half);
        Assert.True(field.PuddleMm(0, p.Along, p.Side * (3.5f - 0.75f * p.Reach), 1f) > 0f);
        // Found by position too.
        Assert.True(field.Locate(new Vector3(p.Along, 0f, -lateral * 0.999f), out int road, out float along, out float lat));
        Assert.Equal(0, road);
        Assert.Equal(p.Along, along, 1);
        Assert.Equal(lateral * 0.999f, lat, 1);
    }

    // ── Grip ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gallaways_aquaplaning_speed_sits_by_hornes()
    {
        float car = RoadWaterLaw.AquaplaningKmh(2f, 0.7f, 220f, 5f);
        float horne = 6.34f * MathF.Sqrt(220f);
        _o.WriteLine($"car tyre, 2 mm film: {car:F0} km/h; Horne {horne:F0}");
        Assert.InRange(car, 0.85f * horne, 1.05f * horne);
        Assert.True(RoadWaterLaw.AquaplaningKmh(2f, 0.7f, 760f, 12f) > car + 30f, "a truck tyre at 7.6 bar holds on far longer");
        Assert.True(RoadWaterLaw.AquaplaningKmh(2f, 0.7f, 160f, 0f) < car, "a slick goes first");
    }

    [Fact]
    public void Wet_grip_is_wongs_at_town_speed_and_falls_with_speed_and_film()
    {
        float holds = RoadWaterLaw.HoldsMm(Asphalt);
        float wet50 = RoadWaterLaw.GripFactor(Asphalt, holds, 50f / 3.6f, 220f, 5f);
        Assert.Equal(0.6f / 0.85f, wet50, 2);
        float damp = RoadWaterLaw.GripFactor(Asphalt, holds * 0.5f, 50f / 3.6f, 220f, 5f);
        Assert.True(damp > wet50 && damp < 1f);
        float wet100 = RoadWaterLaw.GripFactor(Asphalt, holds, 100f / 3.6f, 220f, 5f);
        float film100 = RoadWaterLaw.GripFactor(Asphalt, holds + 3f, 100f / 3.6f, 220f, 5f);
        float slick100 = RoadWaterLaw.GripFactor(Asphalt, holds + 3f, 100f / 3.6f, 160f, 0f);
        _o.WriteLine($"50 wet {wet50:F2}, damp {damp:F2}; 100 wet {wet100:F2}, 3 mm film {film100:F2}, slick {slick100:F2}");
        Assert.True(wet100 < wet50 && film100 < wet100 && slick100 < film100);
        // Gravel drains; ice is not the water model's.
        Assert.Equal(1f, RoadWaterLaw.GripFactor(RoadSurfaces.IndexOf("Gravel"), 2f, 20f, 220f, 5f));
        Assert.Equal(1f, RoadWaterLaw.GripFactor(RoadSurfaces.IndexOf("Ice"), 2f, 20f, 220f, 5f));
    }

    [Fact]
    public void A_wet_road_stops_a_car_later_and_lets_it_slide_sooner()
    {
        var car = MachineRegistry.VehicleFor("i4_midsize");
        float Stop(float water)
        {
            var body = new WheelDynamics(car) { Vx = 50f / 3.6f };
            for (int i = 0; i < body.Wheels.Length; i++) body.SetWater(i, water);
            float s = 0f;
            for (int k = 0; k < 600 && body.Vx > 0.1f; k++)
            {
                body.Step(1f / 60f, 0f, -12f);                 // standing on it
                s += body.TickForward;
            }
            return s;
        }
        float dry = Stop(0f), wet = Stop(1.2f), flooded = Stop(4f);
        _o.WriteLine($"stopping from 50 km/h: dry {dry:F1} m, wet {wet:F1} m, 4 mm of water {flooded:F1} m");
        Assert.True(wet > dry * 1.25f && flooded > wet);
        var b = new WheelDynamics(car) { Vx = 15f };
        float dryLimit = b.SteadyTurn(15f, 1f / 30f);
        for (int i = 0; i < b.Wheels.Length; i++) b.SetWater(i, 1.2f);
        b.Step(1f / 60f, 0f, 0f);                              // works out the wet grip
        float wetDemand = b.SteadyTurn(15f, 1f / 30f);
        _o.WriteLine($"a 30 m turn at 54 km/h: demand dry {dryLimit:F2}, wet {wetDemand:F2}");
        Assert.True(wetDemand > dryLimit * 1.25f);
    }

    // ── Drivers ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Drivers_slow_and_hang_back_in_the_rain()
    {
        var env = new OpenFPS.Common.Components.WorldEnvironmentComponent { Temperature = 14f, Humidity = 0.9f, GameTime = 14f, DayOfYear = 120 };
        var life = new OpenFPS.Server.Repositories.StreetLifeData();
        OpenFPS.Server.Systems.RoadWaterSystem.Update("wet_test_dry", env, 0f, null, 1f);
        OpenFPS.Server.Systems.RoadWaterSystem.Update("wet_test_heavy", env, Rainfall.HeavyRate, null, 1f);
        var dry = OpenFPS.Server.Systems.VehicleSystem.RainCaution("wet_test_dry", life);
        var heavy = OpenFPS.Server.Systems.VehicleSystem.RainCaution("wet_test_heavy", life);
        _o.WriteLine($"dry {dry}, heavy rain {heavy}");
        Assert.Equal((0f, 0f), dry);
        Assert.Equal(life.HeavyRainSpeedReduction, heavy.Speed, 3);
        Assert.Equal(life.HeavyRainHeadwayIncrease, heavy.Headway, 3);
        // The water under a wheel on the server, on a street with puddles.
        Assert.True(OpenFPS.Server.Systems.RoadWaterSystem.WaterAt("wet_test_heavy", new Vector3(0f, 0f, 0f), Asphalt) >= RoadWaterLaw.HoldsMm(Asphalt) - 1e-3f);
        Assert.Equal(0f, OpenFPS.Server.Systems.RoadWaterSystem.WaterAt("wet_test_dry", new Vector3(0f, 0f, 0f), Asphalt));
    }

    [Fact]
    public void Squeal_is_lubricated_away_by_water()
    {
        Assert.Equal(1f, RoadWaterLaw.SquealFactor(Asphalt, 0f));
        float damp = RoadWaterLaw.SquealFactor(Asphalt, 0.35f), wet = RoadWaterLaw.SquealFactor(Asphalt, 0.7f), film = RoadWaterLaw.SquealFactor(Asphalt, 1.5f);
        Assert.True(damp < 1f && wet < damp && film < wet);
        Assert.Equal(0f, RoadWaterLaw.SquealFactor(Asphalt, 2f));
    }

    // ── The sound ─────────────────────────────────────────────────────────────────────────────

    /// <summary>One car's whole voice, alone, held at a speed with this water under every wheel: its RMS
    /// above 2 kHz and below 1 kHz, pascals at a metre.</summary>
    private static (double High, double Low, bool Finite) Voice(string preset, float kmh, float water, bool inside = false)
    {
        const int rate = 48000;
        var v = MachineRegistry.VehicleFor(preset);
        float speed = kmh / 3.6f;
        var body = new WheelDynamics(v);
        body.Hold(speed, 0f, 0f);
        var wire = body.Wheels.Select(w => WheelState.Encode(w.Load, w.AngularSpeed, w.SlipRatio, w.SlipAngle, w.Surface, w.Demand, water)).ToArray();
        var voice = new EngineVoiceState(v, rate, 7) { TargetSpeed = speed, CompensateLevel = false, Interior = inside };
        voice.PlaceAtSpeed(speed);
        voice.Revive();
        voice.Wheels = wire;
        var buf = new float[512];
        for (int b = 0; b < rate / 512; b++) voice.Render(buf);
        double hi = 0, lo = 0; bool finite = true;
        float hp1 = 0, hpIn = 0, hp2 = 0, hpIn2 = 0, lp = 0;
        float aHi = MathF.Exp(-2f * MathF.PI * 2000f / rate), aLo = 1f - MathF.Exp(-2f * MathF.PI * 1000f / rate);
        int n = 0;
        for (int b = 0; b < 2 * rate / 512; b++)
        {
            voice.Render(buf);
            foreach (float s in buf)
            {
                float x = s * voice.PascalsAtFullScale;
                finite &= float.IsFinite(x);
                hp1 = aHi * (hp1 + x - hpIn); hpIn = x;
                hp2 = aHi * (hp2 + hp1 - hpIn2); hpIn2 = hp1;
                lp += aLo * (x - lp);
                hi += hp2 * hp2; lo += lp * lp; n++;
            }
        }
        return (Math.Sqrt(hi / n), Math.Sqrt(lo / n), finite);
    }

    private static double Db(double a, double b) => 20 * Math.Log10(a / b);

    [Fact]
    public void A_wet_road_hisses_above_two_kilohertz_and_leaves_the_bottom_alone()
    {
        var dry = Voice("i4_midsize", 50f, 0f);
        var wet = Voice("i4_midsize", 50f, 1.0f);
        var heavy = Voice("i4_midsize", 50f, 3.0f);
        _o.WriteLine($"50 km/h above 2 kHz: wet {Db(wet.High, dry.High):+0.0;-0.0} dB, heavy {Db(heavy.High, dry.High):+0.0;-0.0} dB;"
                     + $" below 1 kHz: wet {Db(wet.Low, dry.Low):+0.0;-0.0} dB");
        Assert.True(dry.Finite && wet.Finite && heavy.Finite);
        Assert.InRange(Db(wet.High, dry.High), 4.0, 15.0);
        Assert.True(Db(heavy.High, wet.High) > 2.0);
        Assert.InRange(Db(wet.Low, dry.Low), -0.5, 1.5);
    }

    [Fact]
    public void The_hiss_grows_with_speed()
    {
        var w30 = Voice("i4_midsize", 30f, 1.0f);
        var w80 = Voice("i4_midsize", 80f, 1.0f);
        _o.WriteLine($"wet above 2 kHz, 80 against 30 km/h: {Db(w80.High, w30.High):+0.0} dB");
        Assert.True(Db(w80.High, w30.High) > 8.0);
    }

    [Fact]
    public void The_spray_is_heard_in_the_cabin()
    {
        var dry = Voice("i4_midsize", 50f, 0f, inside: true);
        var wet = Voice("i4_midsize", 50f, 3.0f, inside: true);
        _o.WriteLine($"in the cabin at 50 km/h, heavy rain against dry, above 2 kHz: {Db(wet.High, dry.High):+0.0} dB");
        Assert.True(wet.Finite);
        Assert.True(Db(wet.High, dry.High) > 3.0);
    }

    [Fact]
    public void A_dry_road_is_unchanged_by_the_water_model()
    {
        // Every wheel at no water: WetTyres stays silent.
        var v = MachineRegistry.VehicleFor("i4_midsize");
        var wet = new WetTyres(v, new[] { true, true, false, false }, new[] { 0, 0, 1, 1 }, 48000f, 3);
        wet.Block(new float[4], new[] { 1f, 1f, 1f, 1f }, new[] { 0.7f, 0.7f, 0.7f, 0.7f }, 14f, 512);
        for (int i = 0; i < 512; i++) { wet.Step(); Assert.Equal(0f, wet.Front + wet.Rear + wet.Cabin); }
        Assert.False(wet.Active);
    }

    [Fact]
    public void A_puddle_splashes()
    {
        var v = MachineRegistry.VehicleFor("i4_midsize");
        var wet = new WetTyres(v, new[] { true, true, false, false }, new[] { 0, 0, 1, 1 }, 48000f, 3);
        var water = new[] { 0.7f, 0.7f, 0.7f, 0.7f };
        var gain = new[] { 1f, 1f, 1f, 1f };
        var tex = new[] { 0.7f, 0.7f, 0.7f, 0.7f };
        double Energy(int blocks)
        {
            double e = 0;
            for (int b = 0; b < blocks; b++)
            {
                wet.Block(water, gain, tex, 12f, 512);
                for (int i = 0; i < 512; i++) { wet.Step(); e += wet.Front * wet.Front; }
            }
            return e / (blocks * 512);
        }
        double before = Energy(20);
        water[1] = 12f;                                          // the right front into 12 mm of water
        double splash = Energy(10);
        _o.WriteLine($"front tap: before {10 * Math.Log10(before / 4e-10):F1} dB, splashing {10 * Math.Log10(splash / 4e-10):F1} dB at a metre");
        Assert.True(splash > before * 10);
    }
}
