using System;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A tailpipe throws its top end the way it points, and the car stands in the way of the rest:
/// behind a car you hear the crackle, in front of it the bass and the reflections. Reported: "the
/// exhaust itself is being contained ... you can hear a hot rod from a block away normally".
/// </summary>
public class ExhaustRadiationTests
{
    private readonly ITestOutputHelper _o;
    public ExhaustRadiationTests(ITestOutputHelper o) => _o = o;
    private const int Rate = 44100;

    private static ExhaustRadiation Car(string preset = "v8_muscle") => new(VehicleProfile.ByName(preset), Rate);

    [Fact]
    public void ThePipeEndsJustBehindTheBumper()
    {
        var v = VehicleProfile.ByName("i4_economy");
        var r = new ExhaustRadiation(v, Rate);
        Assert.InRange(r.Exit.Z, -v.LengthMetres / 2f - 0.06f, -v.LengthMetres / 2f - 0.04f);
        Assert.Equal(v.ExhaustHeight, r.Exit.Y, 3);
    }

    [Fact]
    public void BehindThePipeEverythingComesThrough()
    {
        var r = Car();
        r.Aim(new Vector3(0f, 0.5f, -12f));
        var (l, m, h) = r.Target;
        Assert.InRange(l, 0.99f, 1f); Assert.InRange(m, 0.97f, 1f); Assert.InRange(h, 0.9f, 1f);
    }

    [Fact]
    public void InFrontTheCrackleIsGoneAndTheBassIsNot()
    {
        var r = Car();
        r.Aim(new Vector3(0f, 1.7f, 12f));
        var (l, m, h) = r.Target;
        _o.WriteLine($"in front: low {20 * MathF.Log10(l):F1} dB, mid {20 * MathF.Log10(m):F1} dB, high {20 * MathF.Log10(h):F1} dB");
        Assert.True(l > 0.5f, $"the bass was cut to {l}");
        Assert.True(h < 0.05f, $"the top came through at {h}");
        Assert.True(h < m && m < l);
    }

    [Fact]
    public void AbeamIsBetween()
    {
        var front = Car(); front.Aim(new Vector3(0f, 0.5f, 12f));
        var side = Car(); side.Aim(new Vector3(12f, 0.5f, -2.35f));
        var back = Car(); back.Aim(new Vector3(0f, 0.5f, -12f));
        Assert.True(side.Target.High < back.Target.High && side.Target.High > front.Target.High,
                    $"high: behind {back.Target.High}, abeam {side.Target.High}, in front {front.Target.High}");
    }

    [Fact]
    public void AStockCarIsBrightestOnItsLeft()
    {
        var left = Car("nascar_v8"); left.Aim(new Vector3(-12f, 0.5f, 0f));
        var right = Car("nascar_v8"); right.Aim(new Vector3(12f, 0.5f, 0f));
        Assert.True(left.Target.High > 4f * right.Target.High, $"left {left.Target.High}, right {right.Target.High}");
    }

    [Fact]
    public void WithNobodyListeningItIsTheSignal()
    {
        var r = Car();
        r.Aim(null);
        Assert.Equal((1f, 1f, 1f), r.Target);
        var rng = new Random(3);
        for (int i = 0; i < 4096; i++)
        {
            float x = (float)rng.NextDouble() * 2f - 1f;
            Assert.Equal(x, r.Process(x), 4);
        }
    }

    /// <summary>
    /// In the whole voice: a V8 heard from behind against from in front keeps its bass and loses its
    /// top; and the idle lift does not read the pipe facing away as a quiet engine and turn it up.
    /// </summary>
    [Fact]
    public void AnEngineHeardFromBehindAndInFront()
    {
        var (backLow, backHigh) = Bands(new Vector3(0f, 1.2f, -12f));
        var (frontLow, frontHigh) = Bands(new Vector3(0f, 1.2f, 12f));
        _o.WriteLine($"low: behind {backLow:F1} dB, in front {frontLow:F1} dB; high: behind {backHigh:F1} dB, in front {frontHigh:F1} dB");
        // Not all of it: the body's panels ring, driven by the exhaust, and radiate from the whole car.
        Assert.True(backHigh - frontHigh > 6f, $"the top is only {backHigh - frontHigh:F1} dB brighter behind");
        Assert.True(MathF.Abs(backLow - frontLow) < 6f, $"the bass moved {backLow - frontLow:F1} dB");
        Assert.True(frontLow <= backLow + 0.5f, "the front view was lifted above the back");
    }

    /// <summary>The rear voice's energy below 300 Hz and above 5 kHz, dB, with the listener placed in
    /// the vehicle's frame relative to the true tailpipe (the voice is split).</summary>
    private static (float Low, float High) Bands(Vector3 listenerFromOrigin)
    {
        var v = VehicleProfile.ByName("v8_muscle");
        var voice = new EngineVoiceState(v, Rate, 5) { TargetSpeed = 0f, SplitVoices = true, CompensateLevel = true };
        voice.PlaceAtSpeed(0f);
        voice.Revive();
        voice.SetListener(listenerFromOrigin - new Vector3(0f, v.ExhaustHeight, v.ExhaustOffsetZ));
        var buf = new float[1024];
        double low = 0, high = 0;
        float lp = 0f, hp = 0f, prev = 0f;
        float aLow = 1f - MathF.Exp(-2f * MathF.PI * 300f / Rate);
        float aHigh = MathF.Exp(-2f * MathF.PI * 5000f / Rate);
        for (int b = 0; b < Rate * 3 / 1024; b++)
        {
            voice.Render(buf);
            if (b < Rate / 1024) continue;
            foreach (float x in buf)
            {
                lp += (x - lp) * aLow;
                hp = aHigh * (hp + x - prev); prev = x;
                low += lp * (double)lp; high += hp * (double)hp;
            }
        }
        return (10f * MathF.Log10((float)low + 1e-20f), 10f * MathF.Log10((float)high + 1e-20f));
    }
}
