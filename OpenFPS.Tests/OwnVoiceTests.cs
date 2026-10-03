using System;
using System.Linq;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>Your own voice read back for your room to answer (OwnVoiceRing, OwnVoiceTap).</summary>
public class OwnVoiceTests
{
    /// <summary>The capture's 20 ms frames, fed at the pace the mixer reads 10 ms blocks at 44.1 kHz.</summary>
    private static float[] Run(float delay, Func<long, float> voice, double seconds)
    {
        var ring = new OwnVoiceRing();
        var tap = new OwnVoiceTap(ring, delay, 44100);
        var heard = new System.Collections.Generic.List<float>();
        long written = 0;
        var block = new float[441];
        for (int step = 0; step < seconds * 100; step++)
        {
            // Every other 10 ms block, a 20 ms frame arrives, as the capture thread delivers them.
            if (step % 2 == 0)
            {
                var frame = new float[960];
                for (int i = 0; i < frame.Length; i++) frame[i] = voice(written++);
                ring.Write(frame);
            }
            tap.Consume(block);
            heard.AddRange(block);
        }
        return heard.ToArray();
    }

    [Fact]
    public void ACopyComesBackAtItsDelay()
    {
        // A click at 0.5 s into the voice: heard at 0.5 s plus the ring's margin and the delay asked for.
        float[] heard = Run(0.05f, n => n == 24000 ? 1f : 0f, 1.5);
        int at = Array.IndexOf(heard, heard.Max());
        double seconds = at / 44100.0;
        Assert.InRange(seconds, 0.5 + OwnVoiceRing.Margin + 0.05 - 0.025, 0.5 + OwnVoiceRing.Margin + 0.05 + 0.025);
    }

    [Fact]
    public void ASteadyVoiceComesBackSteady()
    {
        // A 200 Hz tone: once it is going, no gaps and no wobble in its period (the capture's bursts
        // must not be chased sample by sample).
        float[] heard = Run(0.02f, n => MathF.Sin(2 * MathF.PI * 200 * n / 48000f), 3);
        var tail = heard.Skip(44100).ToArray();
        Assert.True(tail.Max() > 0.9f && tail.Min() < -0.9f, "the tone did not come through");
        var crossings = Enumerable.Range(1, tail.Length - 1).Where(i => tail[i - 1] < 0 && tail[i] >= 0).ToArray();
        var periods = crossings.Zip(crossings.Skip(1), (a, b) => (b - a) / 44100.0).ToArray();
        Assert.InRange(periods.Min(), 1 / 200.0 * 0.98, 1 / 200.0 * 1.02);
        Assert.InRange(periods.Max(), 1 / 200.0 * 0.98, 1 / 200.0 * 1.02);
    }

    [Fact]
    public void ItFallsSilentWhenTheMicrophoneStops()
    {
        var ring = new OwnVoiceRing();
        var tap = new OwnVoiceTap(ring, 0f, 44100);
        ring.Write(Enumerable.Repeat(0.5f, 9600).ToArray());
        var block = new float[441];
        System.Threading.Thread.Sleep(300);   // nothing written for longer than a capture ever pauses
        for (int i = 0; i < 20; i++) tap.Consume(block);
        Assert.True(block.All(v => MathF.Abs(v) < 1e-3f));
    }
}
