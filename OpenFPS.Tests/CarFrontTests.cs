using System;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// "The front of the car seems to be quiet, like all cars, it can't be just all exhaust" (Cody,
/// 2026-10-05). The engine bay leaked a fixed 0.15 of the engine (-16.5 dB), so an idling hatchback
/// was louder behind than in front; NHTSA measured internal-combustion cars 6 to 10 dB quieter
/// behind than in front (FMVSS 141 final rule, 81 FR 90416, 2016). The bay is now its openings and
/// its lining (EngineBaySpec), its sound leaves by the grille and the open floor (BayRadiation), and
/// a car's electric fan runs on its relay (ElectricFanSpec).
/// </summary>
public class CarFrontTests
{
    private readonly ITestOutputHelper _o;
    public CarFrontTests(ITestOutputHelper o) => _o = o;

    private const int Rate = 44100, Block = 512;

    [Fact]
    public void TheBaysLeakWhatTheirOpeningsAndLiningSay()
    {
        // sqrt(open / (open + absorption)).
        Assert.InRange(EngineBaySpec.Car.Leakage, 0.60f, 0.66f);
        Assert.InRange(EngineBaySpec.ClassicCar.Leakage, 0.86f, 0.92f);
        Assert.InRange(EngineBaySpec.Pickup.Leakage, 0.63f, 0.69f);
        Assert.InRange(EngineBaySpec.Van.Leakage, 0.84f, 0.90f);
        // An undeclared car takes the car's bay; a declared figure still wins.
        Assert.Equal(EngineBaySpec.Car.Leakage, VehicleProfile.ByName("i4_economy").EngineBayLeakage);
        Assert.Equal(0.8f, VehicleProfile.ByName("diesel_truck").EngineBayLeakage);
        Assert.Null(VehicleProfile.ByName("diesel_truck").EngineBay);
        Assert.Equal(1f, VehicleProfile.ByName("sportbike").EngineBayLeakage);
        // A machine built on another keeps its bay.
        Assert.Equal(EngineBaySpec.Pickup, VehicleProfile.ByName("powerstroke73").EngineBay);
        Assert.Equal(EngineBaySpec.Pickup, VehicleProfile.ByName("mail_truck").EngineBay);
        Assert.Equal(EngineBaySpec.ClassicCar, VehicleProfile.ByName("v8_mild").EngineBay);
    }

    /// <summary>The bay is heard straight from in front and from beside it, and only round the
    /// body and along the underside from behind.</summary>
    [Fact]
    public void TheBayIsHeardInFrontAndShadedBehind()
    {
        var v = VehicleProfile.ByName("i4_economy");
        var bay = new BayRadiation(v, Rate);
        Assert.True(bay.Active);
        float half = v.LengthMetres * 0.5f;
        float Db(float g) => 20f * MathF.Log10(g);

        bay.Aim(new Vector3(0f, 1.2f, half + 2f));
        var front = bay.Target;
        bay.Aim(new Vector3(2.5f, 1.2f, v.FrontTapZ));
        var side = bay.Target;
        bay.Aim(new Vector3(0f, 1.2f, -half - 2f));
        var back = bay.Target;
        _o.WriteLine($"front {Db(front.Mid):F1}, beside {Db(side.Mid):F1}, behind {Db(back.Low):F1}/{Db(back.Mid):F1}/{Db(back.High):F1} dB");

        Assert.True(Db(front.Mid) > -1f && Db(front.High) > -1f);
        Assert.True(Db(side.Mid) > -1.5f);
        Assert.True(Db(back.Mid) < -2f, $"behind the car the bay is only {Db(back.Mid):F1} dB down");
        // Nobody outside listening: exactly what leaves the bay.
        bay.Aim(null);
        Assert.Equal((1f, 1f, 1f), bay.Target);
        // A motorcycle, a truck and a rear-engined bus keep leaking evenly.
        Assert.False(new BayRadiation(VehicleProfile.ByName("sportbike"), Rate).Active);
        Assert.False(new BayRadiation(VehicleProfile.ByName("diesel_truck"), Rate).Active);
        Assert.False(new BayRadiation(VehicleProfile.ByName("transit_bus"), Rate).Active);
    }

    /// <summary>
    /// The whole voice, both taps, idling, heard 2 m beyond each bumper with spherical spreading from
    /// each tap: above 400 Hz (where the A-weighting puts nearly all of an idle's level) a stock
    /// hatchback is louder in front than behind by NHTSA's margin. Before the bay was opened up it
    /// was 2 dB(A) LOUDER behind (`--car-fronts`).
    /// </summary>
    [Fact]
    public void AnIdlingHatchbackIsLouderInFrontThanBehind()
    {
        var v = VehicleProfile.ByName("i4_economy");
        double front = HeardAbove400(v, inFront: true), back = HeardAbove400(v, inFront: false);
        double diff = 10 * Math.Log10(front / back);
        _o.WriteLine($"above 400 Hz: front minus behind {diff:F1} dB");
        Assert.InRange(diff, 6.0, 14.0);
    }

    private static double HeardAbove400(VehicleProfile v, bool inFront)
    {
        var voice = new EngineVoiceState(v, Rate, 11) { TargetSpeed = 0f, SplitVoices = true, AmbientCelsius = 15f };
        voice.PlaceAtSpeed(0f);
        voice.Revive();
        var tap = new EngineTapState(voice);
        float half = v.LengthMetres * 0.5f;
        var listener = new Vector3(0f, 1.2f, inFront ? half + 2f : -half - 2f);
        voice.SetListener(listener - v.ExhaustSlot);
        float gF = 1f / Vector3.Distance(listener, new Vector3(0f, v.FrontTapHeight, v.FrontTapZ));
        float gR = 1f / Vector3.Distance(listener, v.ExhaustSlot);
        var fb = new float[Block]; var rb = new float[Block];
        // Fourth-order highpass at 400 Hz on each tap: an idling exhaust's firing order is 20 dB
        // over its top, and a gentle filter lets it through.
        var hf = new[] { new HighPass(400f), new HighPass(400f) };
        var hr = new[] { new HighPass(400f), new HighPass(400f) };
        double e = 0;
        for (int b = 0; b < Rate * 5 / Block; b++)
        {
            voice.Produce(); tap.Render(fb); voice.Consume(rb);
            if (b < Rate / Block) continue;
            for (int i = 0; i < Block; i++)
            {
                // Each tap spread from a metre; summed as energies, since the two ends are metres
                // apart and do not cohere.
                double fh = hf[1].Step(hf[0].Step(fb[i] * gF)), rh = hr[1].Step(hr[0].Step(rb[i] * gR));
                e += fh * fh + rh * rh;
            }
        }
        return e;
    }

    /// <summary>A second-order Butterworth highpass (RBJ).</summary>
    private sealed class HighPass
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;
        public HighPass(float hz)
        {
            double w = 2 * Math.PI * hz / Rate, alpha = Math.Sin(w) / (2 * 0.7071), c = Math.Cos(w), a0 = 1 + alpha;
            _b0 = (1 + c) / 2 / a0; _b1 = -(1 + c) / a0; _b2 = (1 + c) / 2 / a0;
            _a1 = -2 * c / a0; _a2 = (1 - alpha) / a0;
        }
        public double Step(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
    }

    // ── The electric fan ──────────────────────────────────────────────────────────────────────

    private static CoolingSystem Relay(int seed = 11)
        => new(ElectricFanSpec.TwoSpeed, EngineProfile.Inline4Economy, seed);

    private static void Run(CoolingSystem c, float seconds, float rpm, float speed)
    {
        for (float t = 0f; t < seconds; t += 0.05f) c.Step(0.05f, rpm, 0f, speed);
    }

    [Fact]
    public void OnAHotDayTheFanRunsForTheAirConWhileTheCarIsSlow()
    {
        var c = Relay();
        c.AmbientCelsius = 32f;
        Run(c, 5f, 800f, 0f);
        Assert.True(c.AirConditioning);
        Assert.Equal(ElectricFanSpec.TwoSpeed.LowSpeedFraction, c.FanSpeedFraction, 2);
        // Up to a cruise: the road pushes the air through the condenser and the fan stops.
        Run(c, 5f, 2500f, 15f);
        Assert.False(c.AirConditioning);
        Assert.Equal(0f, c.FanSpeedFraction, 2);
        // Crawling again: back on.
        Run(c, 5f, 900f, 3f);
        Assert.True(c.AirConditioning);
        // Engine off: the relay is off.
        Run(c, 5f, 0f, 0f);
        Assert.Equal(0f, c.FanSpeedFraction, 2);
    }

    [Fact]
    public void OnAMildDayItStaysOffUntilTheCoolantIsHot()
    {
        var c = Relay();
        c.AmbientCelsius = 15f;
        Run(c, 60f, 800f, 0f);
        Assert.False(c.AirConditioning);
        Assert.Equal(0f, c.FanSpeedFraction, 2);
        c.SetCelsius(97f);
        Run(c, 3f, 800f, 0f);
        Assert.Equal(ElectricFanSpec.TwoSpeed.LowSpeedFraction, c.FanSpeedFraction, 2);
        c.SetCelsius(104f);
        Run(c, 3f, 800f, 0f);
        Assert.Equal(1f, c.FanSpeedFraction, 2);
    }

    /// <summary>Whether a driver has the air conditioning on is fixed per car and differs between
    /// cars: a street on a warm day is some fans, not all of them or none.</summary>
    [Fact]
    public void DriversDifferAndEachIsTheSameEveryTime()
    {
        var spec = ElectricFanSpec.TwoSpeed;
        float lo = float.MaxValue, hi = float.MinValue;
        for (int seed = 1; seed < 200; seed++)
        {
            float t = Relay(seed).AirConFromCelsius;
            Assert.Equal(t, Relay(seed).AirConFromCelsius);
            Assert.InRange(t, spec.AirConAmbientCelsius - spec.AirConAmbientSpread, spec.AirConAmbientCelsius + spec.AirConAmbientSpread);
            lo = MathF.Min(lo, t); hi = MathF.Max(hi, t);
        }
        Assert.True(hi - lo > spec.AirConAmbientSpread, "every car decides alike");
    }

    /// <summary>In the voice: an idling hatchback's front on a hot day has its fan in it.</summary>
    [Fact]
    public void TheFanIsHeardFromTheFrontOnAHotDay()
    {
        var v = VehicleProfile.ByName("i4_economy");
        double mild = FrontTap(v, 15f), hot = FrontTap(v, 32f);
        double diff = 10 * Math.Log10(hot / mild);
        _o.WriteLine($"front tap, hot day over mild: {diff:F1} dB");
        Assert.True(diff > 4.0, $"the fan adds only {diff:F1} dB");
        // A truck's fan is on its crank and its clutch, as before.
        Assert.Null(VehicleProfile.ByName("diesel_truck").ElectricFan);
    }

    private static double FrontTap(VehicleProfile v, float ambient)
    {
        var voice = new EngineVoiceState(v, Rate, 11) { TargetSpeed = 0f, SplitVoices = true, AmbientCelsius = ambient };
        voice.PlaceAtSpeed(0f);
        voice.Revive();
        var tap = new EngineTapState(voice);
        voice.SetListener(new Vector3(0f, 1.2f, v.LengthMetres * 0.5f + 2f) - v.ExhaustSlot);
        var fb = new float[Block]; var rb = new float[Block];
        double e = 0;
        for (int b = 0; b < Rate * 6 / Block; b++)
        {
            voice.Produce(); tap.Render(fb); voice.Consume(rb);
            if (b < Rate * 3 / Block) continue;
            foreach (var x in fb) e += x * (double)x;
        }
        return e;
    }
}
