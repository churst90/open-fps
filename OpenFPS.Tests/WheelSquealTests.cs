using System;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Each tyre squealing for itself (VehicleSynth.WheelSqueal, played through the engine voice's taps):
/// from its own demand, slip velocity and load, out of its own end of the vehicle.
/// </summary>
public class WheelSquealTests
{
    private readonly ITestOutputHelper _o;
    public WheelSquealTests(ITestOutputHelper o) => _o = o;

    private const int Rate = VehicleSynth.SampleRate;

    private static double Rms(float demand, float slipVelocity, float load, int seed = 3)
    {
        var tyre = VehicleProfile.ByName("i4_midsize").Tyres;
        var v = new VehicleSynth.WheelSquealVoice { Demand = demand, SlipVelocity = slipVelocity };
        var rng = new Random(seed);
        double e = 0;
        for (int i = 0; i < Rate; i++)
        {
            float y = VehicleSynth.WheelSqueal(tyre, demand, slipVelocity, load, 1.4f, 4, rng, ref v);
            if (i >= Rate / 4) e += y * y;
        }
        return Math.Sqrt(e / (Rate * 3 / 4));
    }

    private static double Db(double a, double b) => 20 * Math.Log10(a / Math.Max(1e-30, b));

    [Fact]
    public void A_tyre_below_the_onset_is_silent()
        => Assert.Equal(0.0, Rms(TyreFriction.SquealOnset - 0.05f, 1.4f, 1f));

    [Fact]
    public void A_tyre_not_sliding_over_the_road_is_silent()
        => Assert.Equal(0.0, Rms(0.95f, 0f, 1f));

    /// <summary>The squeal follows the frictional power in the sliding part of the patch: twice the
    /// load or twice the slip velocity is 3 dB, and a harder-worked tyre (more of its patch sliding)
    /// is louder.</summary>
    [Fact]
    public void Level_follows_load_slip_velocity_and_demand()
    {
        double baseline = Rms(0.95f, 1.4f, 1f);
        double loaded = Rms(0.95f, 1.4f, 2f);
        double faster = Rms(0.95f, 2.8f, 1f);
        double harder = Rms(1.0f, 1.4f, 1f);
        _o.WriteLine($"twice the load {Db(loaded, baseline):F1} dB, twice the slip velocity {Db(faster, baseline):F1} dB, demand 1.00 against 0.95 {Db(harder, baseline):F1} dB");
        Assert.InRange(Db(loaded, baseline), 2.5, 3.5);
        Assert.InRange(Db(faster, baseline), 2.5, 3.5);
        Assert.True(harder > baseline);
    }

    /// <summary>The wheels as the server would send them for a car with only its right-hand tyres
    /// starting to squeal, at 15 m/s: a light squeal, so the voice is nowhere near its ceiling and
    /// the levels compare.</summary>
    private static WheelState[] RightSideSquealing(WheelDynamics body)
    {
        var wire = new WheelState[body.Wheels.Length];
        for (int i = 0; i < wire.Length; i++)
        {
            var w = body.Wheels[i];
            bool right = w.Y > 0f;
            wire[i] = WheelState.Encode(w.StaticLoad * (right ? 1.4f : 0.6f), 15f / w.Radius, 0f, right ? -0.06f : -0.02f, w.Surface,
                                        right ? 0.86f : 0.3f);
        }
        return wire;
    }

    private static double SquealBand(float[] x)
    {
        var bands = Spectrum.BandEnergy(x, Rate);
        return bands[5] + bands[6];                 // 1-4 kHz
    }

    /// <summary>
    /// A listener off the car's right side hears its squealing right-hand tyres louder than a
    /// listener the same distance off its left: each wheel is weighted by how much nearer the listener
    /// it is than the tap it goes out through.
    /// </summary>
    [Fact]
    public void The_squealing_side_is_louder_on_its_own_side()
    {
        var profile = VehicleProfile.ByName("i4_midsize");
        var body = new WheelDynamics(profile, 0.85f);
        var wire = RightSideSquealing(body);
        var quiet = new WheelState[wire.Length];
        for (int i = 0; i < quiet.Length; i++)
            quiet[i] = WheelState.Encode(wire[i].LoadNewtons, wire[i].AngularSpeedRadPerSec, 0f, 0f, wire[i].Surface, 0.3f);
        double Band(Vector3 ear, WheelState[] wheels)
        {
            var voice = new EngineVoiceState(profile, Rate, 7) { TargetSpeed = 15f, Wheels = wheels };
            voice.PlaceAtSpeed(15f);
            voice.SetListener(ear - profile.ExhaustOffset);
            var buf = new float[Rate];
            voice.Render(buf);
            return SquealBand(buf.AsSpan(Rate / 2).ToArray());
        }
        // The squeal's own energy at each ear: the car with its right side squealing, less the same
        // car with every tyre quiet, heard from the same place (engine and rolling noise are the same
        // in both, and independent of the squeal).
        double Heard(Vector3 ear) => Band(ear, wire) - Band(ear, quiet);
        double right = Heard(new Vector3(2.5f, 1.6f, 0f)), left = Heard(new Vector3(-2.5f, 1.6f, 0f));
        _o.WriteLine($"1-4 kHz: right side {10 * Math.Log10(right / left):F1} dB over the left");
        // The geometry gives about 3 dB: each right-hand wheel is nearer the right ear than the voice's
        // own point is, and further from the left one.
        Assert.True(right > left * 1.6, $"right only {10 * Math.Log10(right / left):F1} dB over left");
    }

    /// <summary>
    /// With the two ends of the car split into their own voices, squeal from the front wheels goes
    /// out of the front tap, and a car whose wheels are all rolling quietly sends none.
    /// </summary>
    [Fact]
    public void Front_wheel_squeal_goes_out_of_the_front()
    {
        var profile = VehicleProfile.ByName("i4_midsize");
        var body = new WheelDynamics(profile, 0.85f);
        var wire = new WheelState[body.Wheels.Length];
        for (int i = 0; i < wire.Length; i++)
        {
            var w = body.Wheels[i];
            wire[i] = WheelState.Encode(w.StaticLoad, 15f / w.Radius, 0f, w.Front ? -0.09f : 0f, w.Surface, w.Front ? 0.97f : 0.2f);
        }
        (double Front, double Rear) Split(WheelState[] wheels)
        {
            var voice = new EngineVoiceState(profile, Rate, 7) { TargetSpeed = 15f, Wheels = wheels, SplitVoices = true };
            voice.PlaceAtSpeed(15f);
            var front = new EngineTapState(voice);
            var f = new float[512];
            var r = new float[512];
            var fAll = new float[Rate];
            var rAll = new float[Rate];
            for (int at = 0; at + 512 <= Rate; at += 512)
            {
                voice.Produce();
                front.Render(f);
                voice.Consume(r);
                f.CopyTo(fAll, at);
                r.CopyTo(rAll, at);
            }
            return (SquealBand(fAll.AsSpan(Rate / 2).ToArray()), SquealBand(rAll.AsSpan(Rate / 2).ToArray()));
        }
        var squealing = Split(wire);
        var quiet = new WheelState[wire.Length];
        for (int i = 0; i < quiet.Length; i++)
        {
            var w = body.Wheels[i];
            quiet[i] = WheelState.Encode(w.StaticLoad, 15f / w.Radius, 0f, 0f, w.Surface, 0.2f);
        }
        var rolling = Split(quiet);
        _o.WriteLine($"1-4 kHz, front tap: {10 * Math.Log10(squealing.Front / rolling.Front):F1} dB with the fronts squealing; rear tap {10 * Math.Log10(squealing.Rear / rolling.Rear):F1} dB");
        Assert.True(squealing.Front > rolling.Front * 10.0, "the front tap did not carry the front tyres' squeal");
        Assert.True(squealing.Rear < rolling.Rear * 2.0, "the rear tap carried the front tyres' squeal");
    }
}
