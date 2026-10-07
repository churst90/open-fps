using OpenFPS.Client.Core.AudioEngine.SteamAudio;

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
    public void ASilentLatePartIsOneEmptyPartition()
    {
        // Outdoors a trace has nothing late; convolving the longest IR of zeros every block costs the
        // mixer the whole budget for silence.
        var ir = LateTailIr.FromWindowed(new float[Rate * 2], Block, 64);
        Assert.Equal(1, ir.Partitions);
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

/// <summary>The directional part of the tail (SdmTailIr): sample by sample, by where it came from.</summary>
public class SdmTailTests
{
    private const int Rate = 44100, Block = 256;

    private static System.Numerics.Vector3[] Dirs()
    {
        var d = new System.Numerics.Vector3[DiffuseBranch.Count];
        for (int i = 0; i < d.Length; i++) d[i] = DiffuseTail.Direction(i);
        return d;
    }

    /// <summary>A field arriving from one way (a plane wave: the first-order channels are the
    /// pressure times the direction's components) all lands in the direction nearest it.</summary>
    [Fact]
    public void ASoundFromOneWayLandsThere()
    {
        var r = new Random(3); int n = Rate / 2;
        var w = new float[n]; var y = new float[n]; var z = new float[n]; var x = new float[n];
        var u = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(1f, 0.2f, 0f));   // z = 0: no mirror question
        for (int i = 0; i < n; i++) { w[i] = (float)(r.NextDouble() * 2 - 1); y[i] = w[i] * u.Y; z[i] = w[i] * u.Z; x[i] = w[i] * u.X; }
        var dirs = Dirs();
        var sdm = SdmTailIr.Build(w, y, z, x, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitX, dirs, Rate, Block);
        int nearest = 0; float best = float.MinValue;
        for (int d = 0; d < dirs.Length; d++) { float dot = System.Numerics.Vector3.Dot(u, dirs[d]); if (dot > best) { best = dot; nearest = d; } }
        Assert.True(sdm.Share[nearest] > 0.99f, $"share {sdm.Share[nearest]:F3} in the nearest direction");
    }

    /// <summary>The parts split the directional window's energy between them and lose none of it.</summary>
    [Fact]
    public void ThePartsShareAllTheEnergy()
    {
        var r = new Random(4); int n = Rate / 2;
        var w = new float[n]; var y = new float[n]; var z = new float[n]; var x = new float[n];
        for (int i = 0; i < n; i++) { w[i] = (float)(r.NextDouble() * 2 - 1); y[i] = (float)(r.NextDouble() * 2 - 1); z[i] = (float)(r.NextDouble() * 2 - 1); x[i] = (float)(r.NextDouble() * 2 - 1); }
        var sdm = SdmTailIr.Build(w, y, z, x, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitX, Dirs(), Rate, Block);
        Assert.Equal(1.0, sdm.Share.Sum(), 3);
        // Random directions: spread over many, not piled on one.
        Assert.True(sdm.Share.Count(s => s > 0.01f) >= 12, $"only {sdm.Share.Count(s => s > 0.01f)} directions used");
    }
}
