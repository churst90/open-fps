using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// One NaN reaching the mix silences the game for good: the master limiter holds it (Cody,
/// 2026-10-03, "a pop ... and the audio just cut out"; the master's loudness meter read NaN from then
/// on). Nothing that is not a number may get into the mix, and nothing recursive may keep one.
/// </summary>
public class NonFiniteGuardTests
{
    private const int Rate = 44100;
    private static readonly int Length = (int)(0.6 * Rate);

    private static float[] Trace(int seed)
    {
        var r = new Random(seed); var w = new float[Length];
        for (int i = 0; i < Length; i++)
            w[i] = (float)((r.NextDouble() * 2 - 1) * 0.05 * Math.Exp(-i / (0.1 * Rate)));
        return w;
    }

    [Fact]
    public void ANonFiniteTraceDoesNotPoisonTheAveragedTail()
    {
        // Standing still the average is recursive (a quarter of each new trace): a NaN added once
        // would stay in it, and in every tail built from it, until the next fresh start.
        var s = new SmoothTail(Rate, Length, 0);
        var good = Trace(1);
        s.Add(good, null, null, null, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, null, null, Vector3.Zero, 1, false);
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity })
        {
            var w = (float[])good.Clone();
            w[Rate / 20] = bad;
            s.Add(w, null, null, null, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, null, null, Vector3.Zero, 1, false);
        }
        Assert.Equal(2, s.Rejected);
        for (int b = 0; b < SmoothTail.Bands; b++)
            for (int f = 0; f < s.Frames; f++) Assert.True(double.IsFinite(s.OmniEnergy(b, f)), $"band {b} frame {f}: {s.OmniEnergy(b, f)}");
        var late = s.BuildLate(256, Length / 256 + 1, afterDirectional: false);
        Assert.All(late.Re, v => Assert.True(float.IsFinite(v)));
        // And a good trace after it still counts.
        s.Add(Trace(2), null, null, null, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, null, null, Vector3.Zero, 1, false);
        Assert.Equal(2, s.Rejected);
    }

    [Fact]
    public void ABadBlockIsZeroedAndItsUnitNamedOnce()
    {
        var drained = new List<string>();
        NonFinite.Drain((k, n, r) => { });                         // whatever earlier tests left
        int flag = 0;
        var ok = new float[] { 0.1f, -0.2f, 0.3f };
        Assert.False(NonFinite.Scrub(ok.AsSpan(), ref flag, "unit", "a"));
        Assert.Equal(new[] { 0.1f, -0.2f, 0.3f }, ok);
        for (int k = 0; k < 3; k++)
        {
            var bad = new float[] { 0.1f, float.NaN, 0.3f, float.NegativeInfinity };
            Assert.True(NonFinite.Scrub(bad.AsSpan(), ref flag, "unit", "a", 95));
            Assert.All(bad, v => Assert.Equal(0f, v));
        }
        NonFinite.Drain((k, n, r) => drained.Add(NonFinite.Line(k, n, r)));
        Assert.Single(drained);
        Assert.Contains("[NONFINITE] unit a, region 95", drained[0]);
    }

    [Fact]
    public void ABinauralDirectionIsNeverZeroOrNaN()
    {
        // Steam Audio's binaural effect answers a zero, a 1e-20 or a NaN direction with NaN, and
        // keeps it (--early-tail hrtf).
        foreach (var d in new[] { Vector3.Zero, new Vector3(1e-20f, 0, 0), new Vector3(float.NaN, 0, 0), new Vector3(float.PositiveInfinity, 1, 0) })
        {
            var s = Phonon.SafeDirection(d);
            Assert.True(float.IsFinite(s.X) && float.IsFinite(s.Y) && float.IsFinite(s.Z));
            Assert.InRange(s.Length(), 0.999f, 1.001f);
        }
        var v = Phonon.SafeDirection(new Vector3(0, 3, 4));
        Assert.Equal(0.6f, v.Y, 5); Assert.Equal(0.8f, v.Z, 5);
    }

    [Fact]
    public void AStageForgetsWhatItHeldAfterAFault()
    {
        // The pre-delay and the convolution's input ring are recursive in time: after a reset nothing
        // of a NaN that went in comes out again.
        var d = new PreDelay(8);
        d.Process(float.NaN);
        d.Reset();
        for (int i = 0; i < 20; i++) Assert.Equal(0f, d.Process(0f));

        var ir = LateTailIr.FromWindowed(Enumerable.Range(0, 1024).Select(i => i == 3 ? 1f : 0f).ToArray(), 256, 8);
        var c = new LateTailConvolver(256, 8);
        c.SetIr(ir);
        var input = new float[256]; var output = new float[256];
        input[0] = float.NaN;
        c.Process(input, output);
        c.Reset();
        Array.Clear(input);
        for (int b = 0; b < 8; b++) { c.Process(input, output); Assert.All(output, v => Assert.True(float.IsFinite(v))); }
    }
}
