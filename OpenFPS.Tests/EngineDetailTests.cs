using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Tests;

/// <summary>
/// Far engines at reduced detail (EngineDetail, 2026-10-09). An engine is chaotic, so a reduced one is
/// held to what it promised by measure, against the same voice never changed: its level, the crank's
/// continuity through a hand-over, and no step in level where it is handed over.
/// </summary>
public class EngineDetailTests
{
    private const int Rate = 48000, Block = 1024;

    private static (float[] Audio, float[] Rpm, string[] State) Render(string preset, float speed, double seconds, Func<double, EngineDetail> detail)
    {
        var v = new EngineVoiceState(MachineRegistry.VehicleFor(preset), Rate, 1001) { TargetSpeed = speed, CompensateLevel = true };
        v.PlaceAtSpeed(speed);
        v.SetListener(new Vector3(10f, 1.6f, 0f));
        int blocks = (int)(seconds * Rate / Block);
        var all = new float[blocks * Block];
        var rpm = new float[blocks];
        var state = new string[blocks];
        var buf = new float[Block];
        for (int b = 0; b < blocks; b++)
        {
            v.Detail = detail(b * Block / (double)Rate);
            v.Render(buf);
            buf.CopyTo(all, b * Block);
            rpm[b] = v.Engine.Rpm;
            state[b] = v.Engine.DetailState;
        }
        return (all, rpm, state);
    }

    private static double Db(float[] x, int from, int to)
    {
        double s = 0;
        for (int i = from; i < to; i++) s += (double)x[i] * x[i];
        return 10 * Math.Log10(s / Math.Max(1, to - from) + 1e-30);
    }

    /// <summary>
    /// A car handed to reduced detail at 2 s and back at 6 s plays within a decibel of the same car never
    /// changed, its crank never jumps (the rpm moves no more from one block to the next than the
    /// unchanged car's does), and no 50 ms window around a hand-over steps by more than the unchanged
    /// car's own worst step plus a decibel and a half.
    /// </summary>
    [Fact]
    public void AHandOverKeepsTheLevelTheCrankAndHasNoStep()
    {
        const double seconds = 8.5;
        var full = Render("i4_midsize", 12f, seconds, _ => EngineDetail.Full);
        var hand = Render("i4_midsize", 12f, seconds, t => t >= 2.0 && t < 6.0 ? EngineDetail.Reduced : EngineDetail.Full);

        Assert.Contains("Twin", hand.State);
        Assert.Equal("Outer", hand.State[^1]);

        double reduced = Db(hand.Audio, (int)(2.8 * Rate), (int)(5.8 * Rate)) - Db(full.Audio, (int)(2.8 * Rate), (int)(5.8 * Rate));
        Assert.True(Math.Abs(reduced) < 1.0, $"reduced detail plays {reduced:+0.00;-0.00} dB against full");

        float worstFull = 0f, worstHand = 0f;
        for (int b = 1; b < full.Rpm.Length; b++)
        {
            worstFull = MathF.Max(worstFull, MathF.Abs(full.Rpm[b] - full.Rpm[b - 1]));
            worstHand = MathF.Max(worstHand, MathF.Abs(hand.Rpm[b] - hand.Rpm[b - 1]));
        }
        Assert.True(worstHand <= worstFull * 1.5f + 5f, $"the crank moved {worstHand:F1} rpm in a block through the hand-overs, {worstFull:F1} unchanged");

        int w = Rate / 20;
        double StepIn(float[] x, double from, double to)
        {
            double worst = 0, prev = Db(x, (int)(from * Rate) - w, (int)(from * Rate));
            for (int i = (int)(from * Rate); i + w <= (int)(to * Rate); i += w)
            {
                double now = Db(x, i, i + w);
                worst = Math.Max(worst, Math.Abs(now - prev));
                prev = now;
            }
            return worst;
        }
        double ownWorst = StepIn(full.Audio, 0.5, seconds - 0.1);
        foreach (double at in new[] { 2.0, 6.0 })
        {
            double step = StepIn(hand.Audio, at, at + 1.0);
            Assert.True(step <= ownWorst + 1.5, $"a step of {step:F2} dB in the second after {at} s; the unchanged car's worst is {ownWorst:F2}");
        }
    }

    /// <summary>The law: reduced under the loudest by 15 dB, back to full within 12 (the hysteresis between
    /// is held), full when switched off or with nothing else heard.</summary>
    [Fact]
    public void DetailFollowsHowFarUnderTheLoudestAVoiceIs()
    {
        float Under(float db) => MathF.Pow(10f, -db / 20f);
        Assert.Equal(EngineDetail.Full, FmodAudioProvider.DetailFor(EngineDetail.Full, Under(14f), 1f, true));
        Assert.Equal(EngineDetail.Reduced, FmodAudioProvider.DetailFor(EngineDetail.Full, Under(16f), 1f, true));
        Assert.Equal(EngineDetail.Reduced, FmodAudioProvider.DetailFor(EngineDetail.Reduced, Under(13f), 1f, true));
        Assert.Equal(EngineDetail.Full, FmodAudioProvider.DetailFor(EngineDetail.Reduced, Under(11f), 1f, true));
        Assert.Equal(EngineDetail.Full, FmodAudioProvider.DetailFor(EngineDetail.Reduced, Under(30f), 1f, false));
        Assert.Equal(EngineDetail.Full, FmodAudioProvider.DetailFor(EngineDetail.Full, 0f, 0f, true));
        Assert.Equal(EngineDetail.Reduced, FmodAudioProvider.DetailFor(EngineDetail.Full, 1f, 1f, true, all: true));
    }

    /// <summary>From inside a vehicle its engine is always in full, whatever the provider asked.</summary>
    [Fact]
    public void TheEngineYouRideInIsAlwaysFull()
    {
        var v = new EngineVoiceState(MachineRegistry.VehicleFor("i4_midsize"), Rate, 7) { TargetSpeed = 10f, Interior = true, Detail = EngineDetail.Reduced };
        v.PlaceAtSpeed(10f);
        var buf = new float[Block];
        for (int b = 0; b < 30; b++) v.Render(buf);
        Assert.Equal("Outer", v.Engine.DetailState);
    }
}
