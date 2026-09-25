using System;
using System.Linq;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Brake squeal: some vehicles' brakes sing in the last few metres of an ordinary stop, each at its
/// own pitch, at a modest level; not at speed, not in an emergency stop, and not after it has stopped.
/// </summary>
public class BrakeSquealTests
{
    private const int Rate = 44100;

    private static BrakeSqueal Squealer(bool drums)
    {
        for (int seed = 0; ; seed++)
        {
            var s = new BrakeSqueal(Rate, drums, seed);
            if (s.Squeals) return s;
        }
    }

    /// <summary>A stop from <paramref name="from"/> m/s at <paramref name="decel"/> m/s², then two
    /// seconds standing. Returns the output and the sample it came to rest at.</summary>
    private static (float[] Y, int RestAt) Stop(BrakeSqueal s, float from, float decel)
    {
        float t0 = 1f, tStop = t0 + from / decel, total = tStop + 2f;
        var y = new float[(int)(total * Rate)];
        int rest = (int)(tStop * Rate);
        for (int i = 0; i < y.Length; i++)
        {
            float t = i / (float)Rate;
            float v = t < t0 ? from : MathF.Max(0f, from - decel * (t - t0));
            float a = t >= t0 && v > 0f ? decel : 0f;
            y[i] = s.Step(v, a);
        }
        return (y, rest);
    }

    private static float Db(float[] y, int from, int to)
    {
        double sum = 0; for (int i = from; i < to; i++) sum += (double)y[i] * y[i];
        float rms = (float)Math.Sqrt(sum / Math.Max(1, to - from));
        return 20f * MathF.Log10(MathF.Max(1e-12f, rms) / 20e-6f);
    }

    /// <summary>The seed is an entity id, and a street's ids are consecutive. Neighbours must not
    /// share a pitch: unhashed, ids 5919, 5922 and 5949 all sang within 11 Hz of 6570.</summary>
    [Fact]
    public void NeighbouringVehiclesDoNotShareAPitch()
    {
        var pitches = Enumerable.Range(5900, 400).Select(i => new BrakeSqueal(Rate, false, i))
                                .Where(s => s.Squeals).Select(s => s.Hz).OrderBy(h => h).ToList();
        Assert.True(pitches.Count > 60);
        // Spread over the disc range rather than bunched: no 50 Hz window holds a fifth of them.
        int worst = pitches.Select(p => pitches.Count(q => q >= p && q < p + 50f)).Max();
        Assert.True(worst < pitches.Count / 5, $"{worst} of {pitches.Count} within 50 Hz of each other");
    }

    [Fact]
    public void AboutAQuarterOfCarsAndMostBusesSqueal()
    {
        float cars = Enumerable.Range(0, 2000).Count(i => new BrakeSqueal(Rate, false, i).Squeals) / 2000f;
        float buses = Enumerable.Range(0, 2000).Count(i => new BrakeSqueal(Rate, true, i).Squeals) / 2000f;
        Assert.InRange(cars, 0.21f, 0.29f);
        Assert.InRange(buses, 0.56f, 0.64f);
        // Discs sing high, drums low.
        Assert.InRange(Squealer(false).Hz, 2500f, 7000f);
        Assert.InRange(Squealer(true).Hz, 900f, 2200f);
    }

    /// <summary>Silent at speed; singing in the last few metres of an ordinary stop at its declared
    /// modest level; gone a moment after the wheels stop.</summary>
    [Fact]
    public void ItSingsInTheLastMetresOfAnOrdinaryStop()
    {
        var s = Squealer(false);
        var (y, rest) = Stop(s, from: 10f, decel: 2f);
        // Rolling faster than 4.5 m/s: the first 1 + 2.75 s.
        int slow = (int)((1f + (10f - 4.5f) / 2f) * Rate);
        Assert.True(Db(y, 0, slow) < 20f, $"it squealed at speed: {Db(y, 0, slow):F0} dB");
        // The last second of the stop.
        float singing = Db(y, rest - Rate / 2, rest);
        Assert.InRange(singing, 66f, 75f);
        // A quarter of a second after it stops, it has rung down.
        Assert.True(Db(y, rest + Rate / 4, rest + Rate / 2) < 30f);
    }

    [Fact]
    public void AnEmergencyStopDoesNotSqueal()
    {
        var (y, rest) = Stop(Squealer(false), from: 10f, decel: 6f);
        Assert.True(Db(y, 0, y.Length) < 20f);
    }

    [Fact]
    public void ItsToneIsItsRotorsMode()
    {
        var s = Squealer(false);
        var (y, rest) = Stop(s, from: 8f, decel: 1.5f);
        int from = rest - Rate / 2, crossings = 0;
        for (int i = from + 1; i < rest; i++) if (y[i - 1] < 0f && y[i] >= 0f) crossings++;
        float hz = crossings / 0.5f;
        Assert.InRange(hz, s.Hz * 0.98f, s.Hz * 1.04f);
    }

    [Fact]
    public void BrakesThatDoNotSqueakAreSilent()
    {
        int seed = 0;
        while (new BrakeSqueal(Rate, false, seed).Squeals) seed++;
        var (y, _) = Stop(new BrakeSqueal(Rate, false, seed), from: 10f, decel: 2f);
        Assert.All(y, v => Assert.Equal(0f, v));
    }

    /// <summary>In the vehicle, it is the front tap's: the front brakes do most of the stopping.</summary>
    [Fact]
    public void InAVehicleItComesFromTheFront()
    {
        var car = MachineRegistry.VehicleFor("i4_economy");
        int seed = 0;
        while (!new EngineVoiceState(car, Rate, seed).Squeal.Squeals) seed++;
        var voice = new EngineVoiceState(car, Rate, seed) { SplitVoices = true };
        Assert.False(voice.Squeal.Hz < 2500f);
        voice.PlaceAtSpeed(8f);
        voice.Revive();
        var front = new EngineTapState(voice);
        var rear = new float[1024]; var nose = new float[1024];
        double frontBand = 0, rearBand = 0;
        float hz = voice.Squeal.Hz;
        for (int b = 0; b < (int)(8f * Rate / 1024); b++)
        {
            float t = b * 1024f / Rate;
            voice.TargetSpeed = t < 2f ? 8f : MathF.Max(0f, 8f - 2f * (t - 2f));
            voice.Produce();
            front.Render(nose);
            voice.Consume(rear);
            if (t > 4.6f && t < 6f) { frontBand += Goertzel(nose, hz); rearBand += Goertzel(rear, hz); }
        }
        Assert.True(frontBand > rearBand * 10, $"squeal band front {frontBand:G3} vs rear {rearBand:G3}");
        Assert.True(new EngineVoiceState(MachineRegistry.VehicleFor("school_bus_na"), Rate, 1).Squeal.Hz < 2300f,
                    "a bus has drum brakes");
    }

    private static double Goertzel(float[] x, float hz)
    {
        double w = 2 * Math.PI * hz / Rate, cw = 2 * Math.Cos(w), g1 = 0, g2 = 0;
        foreach (float v in x) { double g0 = v + cw * g1 - g2; g2 = g1; g1 = g0; }
        return g1 * g1 + g2 * g2 - cw * g1 * g2;
    }
}
