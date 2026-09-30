using System;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>The late-tail convolver is exactly a convolution, and the IR it plays is the late part.</summary>
public class LateTailTests
{
    private const int Rate = 44100, Block = 256;

    private static float[] Noise(int n, int seed)
    {
        var r = new Random(seed); var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)(r.NextDouble() * 2 - 1);
        return x;
    }

    /// <summary>The windowed IR Build keeps, for the direct convolution to compare against.</summary>
    private static float[] Windowed(float[] w)
    {
        int a = (int)(LateTailIr.FadeInStartSeconds * Rate), b = (int)(LateTailIr.FadeInEndSeconds * Rate);
        var x = new float[w.Length];
        for (int i = a; i < w.Length; i++)
            x[i] = w[i] * (i >= b ? 1f : 0.5f - 0.5f * MathF.Cos(MathF.PI * (i - a) / (b - a)));
        return x;
    }

    [Fact]
    public void ItIsTheConvolutionOfTheInputWithTheLatePart()
    {
        // A decaying noise IR, a quarter second, and five hundred milliseconds of noise through it.
        var w = Noise(Rate / 4, 1);
        for (int i = 0; i < w.Length; i++) w[i] *= MathF.Exp(-i / (0.08f * Rate));
        var ir = LateTailIr.Build(w, Rate, Block, 64);
        var conv = new LateTailConvolver(Block, 64);
        conv.SetIr(ir);

        var x = Noise(Rate / 2, 2);
        var y = new float[x.Length];
        for (int at = 0; at + Block <= x.Length; at += Block) conv.Process(x.AsSpan(at, Block), y.AsSpan(at, Block));

        var h = Windowed(w);
        double err = 0, sig = 0;
        for (int n = Block; n < x.Length - Block; n += 37)
        {
            double s = 0; for (int k = 0; k < h.Length && k <= n; k++) s += h[k] * (double)x[n - k];
            err += (y[n] - s) * (y[n] - s); sig += s * s;
        }
        Assert.True(err / sig < 1e-8, $"relative error {err / sig:E2}");
    }

    [Fact]
    public void NothingComesOutBeforeTheTailBegins()
    {
        var w = new float[Rate / 4]; w[0] = 1f; w[(int)(0.02f * Rate)] = 0.5f;   // direct and an early tap only
        var ir = LateTailIr.Build(w, Rate, Block, 64);
        Assert.Equal(0.0, ir.Energy, 12);
    }

    [Fact]
    public void ANewTraceTakesOverWithoutAStep()
    {
        var w1 = Noise(Rate / 4, 3); var w2 = Noise(Rate / 4, 4);
        for (int i = 0; i < w1.Length; i++) { float e = MathF.Exp(-i / (0.08f * Rate)); w1[i] *= e; w2[i] *= e * 0.5f; }
        var conv = new LateTailConvolver(Block, 64);
        conv.SetIr(LateTailIr.Build(w1, Rate, Block, 64));
        var x = Noise(Rate, 5); var y = new float[x.Length];
        for (int at = 0; at + Block <= x.Length; at += Block)
        {
            if (at == 100 * Block) conv.SetIr(LateTailIr.Build(w2, Rate, Block, 64));
            conv.Process(x.AsSpan(at, Block), y.AsSpan(at, Block));
        }
        // No sample-to-sample jump at the handover larger than the signal's own.
        float maxStep = 0f, around = 0f;
        for (int n = 90 * Block; n < 110 * Block; n++)
        {
            float d = MathF.Abs(y[n] - y[n - 1]);
            if (n >= 99 * Block && n <= 102 * Block) maxStep = MathF.Max(maxStep, d); else around = MathF.Max(around, d);
        }
        Assert.True(maxStep <= around * 1.2f, $"step {maxStep:F4} at the handover against {around:F4} elsewhere");
    }
}
