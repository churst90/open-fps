using System.Numerics;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The directional tail's two-level convolver (SharedInputConvolver) against the one-level one it
/// replaced on 2026-10-09 (<see cref="OneLevelSharedInputConvolver"/>, kept below as it was): the same
/// responses and the same input give the same output to float rounding, with the same delay, through
/// every kind of handover.
/// </summary>
public class SharedInputConvolverTests
{
    private const int Rate = 48000, Block = 256;
    private readonly ITestOutputHelper _out;

    public SharedInputConvolverTests(ITestOutputHelper output) => _out = output;

    private static Vector3[] Dirs()
    {
        var d = new Vector3[DiffuseBranch.Count];
        for (int i = 0; i < d.Length; i++) d[i] = DiffuseTail.Direction(i);
        return d;
    }

    /// <summary>The directional parts the game builds (SmoothTail.DirectionalWindowed) from a synthetic
    /// room 60 dB down in <paramref name="t60"/>: the whole 0.35 s in use, from the first sample.</summary>
    private static float[][] RoomParts(double t60, int seed)
    {
        int len = 2 * Rate;
        var rng = new Random(seed);
        var w = new float[len];
        for (int i = 0; i < len; i++) w[i] = (float)((rng.NextDouble() * 2 - 1) * Math.Pow(10, -3.0 * i / (t60 * Rate)));
        var c = Enumerable.Range(0, 3).Select(_ => w.Select(v => v * (float)(rng.NextDouble() * 2 - 1) * 0.3f).ToArray()).ToArray();
        var tail = new SmoothTail(Rate, len, DiffuseBranch.Count);
        tail.Add(w, c[0], c[1], c[2], Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, Dirs(), null, Vector3.Zero, 1, false);
        var parts = tail.DirectionalWindowed(SdmTailIr.PartitionsFor(Rate, Block) * Block);
        // At a loud room's level: the directional part 10 dB under a direct sound of one.
        double e = parts.Sum(p => p.Sum(v => (double)v * v));
        float g = (float)Math.Sqrt(0.1 / e);
        foreach (var p in parts) for (int i = 0; i < p.Length; i++) p[i] *= g;
        return parts;
    }

    /// <summary>Parts of every length the two levels split differently: none, under one block, under
    /// one long block, just over, ending mid long block, and the whole window.</summary>
    private static float[][] RaggedParts(int seed)
    {
        int max = SdmTailIr.PartitionsFor(Rate, Block) * Block;
        int[] lengths = { 0, 100, 256, 700, 1024, 1025, 1300, 2048, 2049, 5000, 9999, max };
        var rng = new Random(seed);
        var parts = new float[DiffuseBranch.Count][];
        for (int d = 0; d < parts.Length; d++)
        {
            int n = lengths[d % lengths.Length];
            parts[d] = new float[max];
            int first = d % 3 == 0 ? 0 : rng.Next(0, Math.Max(1, n / 2));
            for (int i = first; i < n; i++) parts[d][i] = (float)((rng.NextDouble() * 2 - 1) * Math.Exp(-i / (0.1 * Rate)) * 0.05);
        }
        return parts;
    }

    private static float[] Noise(int n, int seed)
    {
        var r = new Random(seed); var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)(r.NextDouble() * 2 - 1);
        return x;
    }

    private static float[] Clicks(int n, int seed)
    {
        var r = new Random(seed); var x = new float[n];
        for (int i = r.Next(0, 500); i < n; i += r.Next(700, 5000)) x[i] = r.Next(2) == 0 ? 1f : -1f;
        return x;
    }

    /// <summary>A recording at the level it is played (the welcome announcement), looped to length.</summary>
    private static float[] Recording(int n)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, ".."));
        string file = Path.Combine(root, "OpenFPS.Client", "ASSETS", "SOUNDS", "ANNOUNCE", "st_louis_welcome.wav");
        var src = OpenFPS.Client.AudioEngine.Core.WeaponSynth.ReadWav16Mono(File.ReadAllBytes(file));
        Assert.NotEmpty(src);
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = src[i % src.Length];
        return x;
    }

    private static string Here([System.Runtime.CompilerServices.CallerFilePath] string here = "") => here;

    /// <summary>Both convolvers through <paramref name="x"/>, handed <paramref name="schedule"/>(piece)
    /// before each piece (null: no change); the largest difference between them and the largest output.</summary>
    private static (double MaxDiff, double Peak) Compare(float[] x, Func<int, (SdmTailIr? Uniform, SdmTailIr? TwoLevel, bool Set, bool Reset)> schedule)
    {
        int k = DiffuseBranch.Count, maxP = SdmTailIr.PartitionsFor(Rate, Block);
        var oldConv = new OneLevelSharedInputConvolver(Block, maxP, k);
        var newConv = new SharedInputConvolver(Block, maxP, k);
        var a = new float[k][]; var b = new float[k][];
        for (int d = 0; d < k; d++) { a[d] = new float[Block]; b[d] = new float[Block]; }
        double maxDiff = 0, peak = 0;
        for (int piece = 0, at = 0; at + Block <= x.Length; piece++, at += Block)
        {
            var (u, t, set, reset) = schedule(piece);
            if (set) { oldConv.Set(u); newConv.Set(t); }
            if (reset) { oldConv.Reset(); newConv.Reset(); }
            oldConv.Process(x.AsSpan(at, Block), a);
            newConv.Process(x.AsSpan(at, Block), b);
            for (int d = 0; d < k; d++)
                for (int i = 0; i < Block; i++)
                {
                    maxDiff = Math.Max(maxDiff, Math.Abs(a[d][i] - (double)b[d][i]));
                    peak = Math.Max(peak, Math.Abs(a[d][i]));
                }
        }
        return (maxDiff, peak);
    }

    private static double Db(double v) => 20 * Math.Log10(Math.Max(v, 1e-30));

    [Fact]
    public void ARoomsDirectionalTailMatchesTheOneLevelConvolverToRounding()
    {
        int maxP = SdmTailIr.PartitionsFor(Rate, Block);
        var room = RoomParts(2.0, 3); var dead = RoomParts(0.6, 4);
        var u1 = SdmTailIr.FromParts(room, Block, maxP, longBlock: 0); var t1 = SdmTailIr.FromParts(room, Block, maxP);
        var u2 = SdmTailIr.FromParts(dead, Block, maxP, longBlock: 0); var t2 = SdmTailIr.FromParts(dead, Block, maxP);
        Assert.Contains(t1.PerDirection, d => d is { LongPartitions: > 0 });
        Assert.Contains(t1.PerDirection, d => d is { LongPartitions: > 0 });
        int n = 4 * Rate;
        foreach (var (name, x) in new[] { ("noise", Noise(n, 5)), ("clicks", Clicks(n, 6)), ("recording", Recording(n)) })
        {
            // A new trace every 47 pieces (about every 250 ms, as the tracer), so the handover lands at
            // every place in a long block; and once to nothing and back.
            var (diff, peak) = Compare(x, piece =>
            {
                if (piece == 0) return (u1, t1, true, false);
                if (piece == 400) return (null, null, true, false);
                if (piece == 420) return (u2, t2, true, false);
                if (piece % 47 != 0) return (null, null, false, false);
                bool first = piece / 47 % 2 == 0;
                return (first ? u1 : u2, first ? t1 : t2, true, false);
            });
            _out.WriteLine($"{name}: largest difference {Db(diff):F1} dBFS, largest output {Db(peak):F1} dBFS ({Db(diff / peak):F1} dB under it)");
            Assert.True(peak > 1e-3, $"{name}: the test played nothing ({Db(peak):F1} dBFS)");
            Assert.True(Db(diff) < -100, $"{name}: {Db(diff):F1} dBFS apart");
            Assert.True(Db(diff / peak) < -100, $"{name}: {Db(diff / peak):F1} dB under the output");
        }
    }

    [Fact]
    public void ResponsesOfEveryLengthMatchTheOneLevelConvolverToRounding()
    {
        int maxP = SdmTailIr.PartitionsFor(Rate, Block);
        var p1 = RaggedParts(7); var p2 = RaggedParts(8);
        var u1 = SdmTailIr.FromParts(p1, Block, maxP, longBlock: 0); var t1 = SdmTailIr.FromParts(p1, Block, maxP);
        var u2 = SdmTailIr.FromParts(p2, Block, maxP, longBlock: 0); var t2 = SdmTailIr.FromParts(p2, Block, maxP);
        Assert.Contains(t1.PerDirection, d => d == null);
        Assert.Contains(t1.PerDirection, d => d is { LongPartitions: 0 });
        var x = Noise(3 * Rate, 9);
        var (diff, peak) = Compare(x, piece => piece switch
        {
            0 => (u1, t1, true, false),
            101 => (u2, t2, true, false),
            202 => (u1, t1, true, false),
            // Forgotten mid long block, as after a block that was not finite.
            303 => (null, null, false, true),
            405 => (u2, t2, true, false),
            _ => (null, null, false, false),
        });
        _out.WriteLine($"ragged: largest difference {Db(diff):F1} dBFS, largest output {Db(peak):F1} dBFS");
        Assert.True(peak > 1e-3);
        Assert.True(Db(diff) < -100, $"{Db(diff):F1} dBFS apart");
    }

    /// <summary>An impulse comes out where the one-level convolver puts it: the delay is unchanged.</summary>
    [Fact]
    public void TheDelayIsUnchanged()
    {
        int k = DiffuseBranch.Count, maxP = SdmTailIr.PartitionsFor(Rate, Block);
        var parts = new float[k][];
        int[] taps = { 0, 255, 256, 1023, 1024, 1025, 2047, 5000, 16000 };
        for (int d = 0; d < k; d++) { parts[d] = new float[maxP * Block]; parts[d][taps[d % taps.Length]] = 1f; }
        var conv = new SharedInputConvolver(Block, maxP, k);
        conv.Set(SdmTailIr.FromParts(parts, Block, maxP));
        int n = 80 * Block, click = 3 * Block + 17;
        var x = new float[n]; x[click] = 1f;
        var y = new float[k][];
        for (int d = 0; d < k; d++) y[d] = new float[n];
        var o = new float[k][];
        for (int d = 0; d < k; d++) o[d] = new float[Block];
        for (int at = 0; at < n; at += Block)
        {
            conv.Process(x.AsSpan(at, Block), o);
            for (int d = 0; d < k; d++) Array.Copy(o[d], 0, y[d], at, Block);
        }
        for (int d = 0; d < k; d++)
        {
            int peak = 0;
            for (int i = 1; i < n; i++) if (MathF.Abs(y[d][i]) > MathF.Abs(y[d][peak])) peak = i;
            Assert.Equal(click + taps[d % taps.Length], peak);
            Assert.InRange(y[d][peak], 1f - 1e-5f, 1f + 1e-5f);
        }
    }

    /// <summary>The mixer thread's rule: nothing allocates once it is made, handovers included.</summary>
    [Fact]
    public void ProcessingAllocatesNothing()
    {
        int k = DiffuseBranch.Count, maxP = SdmTailIr.PartitionsFor(Rate, Block);
        var room = RoomParts(1.5, 10);
        var t1 = SdmTailIr.FromParts(room, Block, maxP); var t2 = SdmTailIr.FromParts(RaggedParts(11), Block, maxP);
        var conv = new SharedInputConvolver(Block, maxP, k);
        var o = new float[k][];
        for (int d = 0; d < k; d++) o[d] = new float[Block];
        var x = Noise(Block, 12);
        conv.Set(t1);
        for (int i = 0; i < 8; i++) conv.Process(x, o);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            if (i % 9 == 0) conv.Set(i % 2 == 0 ? t2 : t1);
            if (i == 150) conv.Reset();
            conv.Process(x, o);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>A response split for another long block is refused, not misread.</summary>
    [Fact]
    public void AResponseSplitForAnotherLongBlockIsRefused()
    {
        int k = DiffuseBranch.Count, maxP = SdmTailIr.PartitionsFor(Rate, Block);
        var room = RoomParts(1.0, 13);
        var conv = new SharedInputConvolver(Block, maxP, k);
        var o = new float[k][];
        for (int d = 0; d < k; d++) o[d] = new float[Block];
        conv.Set(SdmTailIr.FromParts(room, Block, maxP, longBlock: 2048));
        conv.Set(SdmTailIr.FromParts(room, Block, maxP, longBlock: 0));
        conv.Process(Noise(Block, 14), o);
        Assert.All(o, y => Assert.All(y, v => Assert.Equal(0f, v)));
    }
}

/// <summary>
/// Reference: SharedInputConvolver as it was until 2026-10-09, one level of uniform partitions, for
/// the null tests above. Needs one-level responses (SdmTailIr.FromParts with longBlock 0).
/// </summary>
internal sealed class OneLevelSharedInputConvolver
{
    private readonly int _block, _bins, _maxPartitions, _k;
    private readonly Fft _fft;
    private readonly float[] _prev, _fdlRe, _fdlIm, _re, _im, _accRe, _accIm, _tmp;
    private int _head;
    private SdmTailIr? _cur;
    private volatile SdmTailIr? _next;

    public OneLevelSharedInputConvolver(int block, int maxPartitions, int k)
    {
        _block = block; _bins = block + 1; _maxPartitions = maxPartitions; _k = k;
        _fft = new Fft(2 * block);
        _prev = new float[block];
        _fdlRe = new float[maxPartitions * _bins]; _fdlIm = new float[maxPartitions * _bins];
        _re = new float[2 * block]; _im = new float[2 * block];
        _accRe = new float[_bins]; _accIm = new float[_bins];
        _tmp = new float[block];
    }

    public void Set(SdmTailIr? ir)
    {
        if (ir != null && (ir.Block != _block || ir.MaxPartitions > _maxPartitions || ir.PerDirection.Length != _k)) return;
        if (ir != null && ir.PerDirection.Any(d => d is { LongPartitions: > 0 })) throw new ArgumentException("one level only");
        _next = ir;
    }

    public void Process(ReadOnlySpan<float> input, float[][] outputs)
    {
        for (int k = 0; k < _block; k++) { _re[k] = _prev[k]; _re[_block + k] = input[k]; _im[k] = 0f; _im[_block + k] = 0f; }
        input.Slice(0, _block).CopyTo(_prev);
        _fft.Forward(_re, _im);
        _head = (_head + 1) % _maxPartitions;
        Array.Copy(_re, 0, _fdlRe, _head * _bins, _bins);
        Array.Copy(_im, 0, _fdlIm, _head * _bins, _bins);

        var cur = _cur; var next = _next;
        bool swap = !ReferenceEquals(cur, next);
        for (int d = 0; d < _k; d++)
        {
            var o = outputs[d];
            var a = cur?.PerDirection[d];
            if (a != null) Convolve(a, o); else Array.Clear(o, 0, _block);
            if (swap)
            {
                var b = next?.PerDirection[d];
                if (b != null) Convolve(b, _tmp); else Array.Clear(_tmp);
                for (int i = 0; i < _block; i++)
                {
                    float up = 0.5f - 0.5f * MathF.Cos(MathF.PI * (i + 0.5f) / _block);
                    o[i] = o[i] * (1f - up) + _tmp[i] * up;
                }
            }
        }
        if (swap) _cur = next;
    }

    public void Reset()
    {
        Array.Clear(_prev); Array.Clear(_fdlRe); Array.Clear(_fdlIm);
    }

    private void Convolve(LateTailIr ir, float[] y)
    {
        Array.Clear(_accRe); Array.Clear(_accIm);
        int bins = _bins, vec = Vector<float>.Count;
        for (int p = 0; p < ir.Partitions; p++)
        {
            int slot = _head - p; if (slot < 0) slot += _maxPartitions;
            int xo = slot * bins, ho = p * bins, k = 0;
            for (; k + vec <= bins; k += vec)
            {
                var xr = new Vector<float>(_fdlRe, xo + k); var xi = new Vector<float>(_fdlIm, xo + k);
                var hr = new Vector<float>(ir.Re, ho + k); var hi = new Vector<float>(ir.Im, ho + k);
                (new Vector<float>(_accRe, k) + xr * hr - xi * hi).CopyTo(_accRe, k);
                (new Vector<float>(_accIm, k) + xr * hi + xi * hr).CopyTo(_accIm, k);
            }
            for (; k < bins; k++)
            {
                float xr = _fdlRe[xo + k], xi = _fdlIm[xo + k], hr = ir.Re[ho + k], hi = ir.Im[ho + k];
                _accRe[k] += xr * hr - xi * hi; _accIm[k] += xr * hi + xi * hr;
            }
        }
        int n = 2 * _block;
        for (int k = 0; k < bins; k++) { _re[k] = _accRe[k]; _im[k] = _accIm[k]; }
        for (int k = 1; k < _block; k++) { _re[n - k] = _accRe[k]; _im[n - k] = -_accIm[k]; }
        _fft.Inverse(_re, _im);
        for (int k = 0; k < _block; k++) y[k] = _re[_block + k];
    }
}
