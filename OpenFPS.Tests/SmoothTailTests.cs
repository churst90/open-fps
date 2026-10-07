using System.Numerics;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Tests;

/// <summary>
/// The smoothed tail (SmoothTail): the trace measured as energy, averaged over traces, played
/// through noise that never changes. It must hold still while you stand, start again in a new room,
/// play the energy it measured, and give the same response for the same energies.
/// </summary>
public class SmoothTailTests
{
    private const int Rate = 44100, Block = 256;
    private static readonly int Length = (int)(0.6 * Rate);

    private static Vector3[] Dirs()
    {
        var d = new Vector3[DiffuseBranch.Count];
        for (int i = 0; i < d.Length; i++) d[i] = DiffuseTail.Direction(i);
        return d;
    }

    /// <summary>A decaying Gaussian noise tail, white, 60 dB down in about 0.7 s.</summary>
    private static float[] Tail(int seed, float gain = 1f)
    {
        var r = new Random(seed); var w = new float[Length];
        for (int i = 0; i < Length; i++)
        {
            double g = Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
            w[i] = (float)(g * gain * Math.Exp(-i / (0.1 * Rate)));
        }
        return w;
    }

    private static SmoothTail Make(int directions = 0) => new(Rate, Length, directions);

    private static void AddOmni(SmoothTail s, float[] w, Vector3 at, int place = 1, bool scene = false)
        => s.Add(w, null, null, null, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, null, null, at, place, scene);

    private static double Total(SmoothTail s)
    {
        double e = 0;
        for (int b = 0; b < SmoothTail.Bands; b++) for (int f = 0; f < s.Frames; f++) e += s.OmniEnergy(b, f);
        return e;
    }

    /// <summary>Each band's energy per frame of <paramref name="x"/>, measured as SmoothTail measures.</summary>
    private static double[,] BandFrames(float[] x)
    {
        var split = new SmoothTail.Splitter(Rate);
        int frames = (x.Length + SmoothTail.Frame - 1) / SmoothTail.Frame;
        var e = new double[SmoothTail.Bands, frames];
        var band = new double[SmoothTail.Bands];
        for (int i = 0; i < x.Length; i++)
        {
            split.Run(x[i], band);
            for (int b = 0; b < band.Length; b++) e[b, i / SmoothTail.Frame] += band[b] * band[b];
        }
        return e;
    }

    [Fact]
    public void TheBandsAddUpToTheWhole()
    {
        // Power-complementary splits: the bands' energies are the signal's, none lost or doubled.
        var w = Tail(1);
        var e = BandFrames(w);
        double bands = 0, whole = 0;
        foreach (var v in e) bands += v;
        foreach (var v in w) whole += v * (double)v;
        Assert.InRange(10 * Math.Log10(bands / whole), -0.1, 0.1);
    }

    [Fact]
    public void StandingStillTheAverageSettlesAndHoldsStill()
    {
        var s = Make();
        // Traces that differ as the tracer's do: the same place, the level wandering trace to trace.
        var levels = new[] { 1.0f, 1.3f, 0.8f, 1.2f, 0.9f, 1.1f, 1.0f, 0.85f, 1.25f, 0.95f, 1.05f, 1.0f };
        double first = 0, maxStep = 0, prev = 0;
        for (int k = 0; k < levels.Length; k++)
        {
            AddOmni(s, Tail(10, levels[k]), new Vector3(0.01f * (k % 3), 0, 0));
            double t = Total(s);
            if (k == 0) { first = t; Assert.Equal(1.0, s.LastWeight); }
            else
            {
                Assert.Equal(SmoothTail.StillWeight, s.LastWeight, 6);
                maxStep = Math.Max(maxStep, Math.Abs(10 * Math.Log10(t / prev)));
            }
            prev = t;
        }
        // A single trace swings up to 2.3 dB here (0.8 to 1.3); the average moves under 1 dB a trace.
        Assert.True(maxStep < 1.0, $"the average stepped {maxStep:F2} dB");
        // And it settles where the traces are, not drifting off: the mean of these is 0.4 dB over the
        // first, and the average after them 0.25 dB.
        double settled = 10 * Math.Log10(prev / first);
        Assert.True(Math.Abs(settled) < 0.5, $"the average ended {settled:F2} dB from the first trace");
        Assert.Equal(1, s.Resets);

        // The same trace again and again: the average converges on it and then does not move.
        var t1 = Tail(11);
        for (int k = 0; k < 40; k++) AddOmni(s, t1, Vector3.Zero);
        var single = Make(); AddOmni(single, t1, Vector3.Zero);
        double a = Total(s), b = Total(single);
        Assert.InRange(10 * Math.Log10(a / b), -0.01, 0.01);
        AddOmni(s, t1, Vector3.Zero);
        Assert.InRange(10 * Math.Log10(Total(s) / a), -0.001, 0.001);
    }

    [Fact]
    public void ANewRoomOrAMetreAwayStartsAgain()
    {
        var a = Tail(20); var b = Tail(21, 0.9f);
        var fresh = Make(); AddOmni(fresh, b, new Vector3(5, 0, 0), place: 2);

        // A new region: the new trace alone, nothing of the last room left in it.
        var s = Make();
        for (int k = 0; k < 6; k++) AddOmni(s, a, Vector3.Zero, place: 1);
        AddOmni(s, b, Vector3.Zero, place: 2);
        Assert.Equal(1.0, s.LastWeight);
        Assert.InRange(10 * Math.Log10(Total(s) / Total(fresh)), -0.001, 0.001);

        // More than a metre on, the same region: the same.
        var m = Make();
        for (int k = 0; k < 6; k++) AddOmni(m, a, Vector3.Zero);
        AddOmni(m, b, new Vector3(1.2f, 0, 0));
        Assert.Equal(1.0, m.LastWeight);

        // Within half a metre: the long average. Between: more weight the further.
        var n = Make();
        AddOmni(n, a, Vector3.Zero);
        AddOmni(n, a, new Vector3(0.3f, 0, 0));
        Assert.Equal(SmoothTail.StillWeight, n.LastWeight, 6);
        var o = Make();
        AddOmni(o, a, Vector3.Zero);
        AddOmni(o, a, new Vector3(0.75f, 0, 0));
        Assert.InRange(o.LastWeight, SmoothTail.StillWeight + 0.1, 0.9);

        // The geometry changed (a door): it follows faster, without starting again.
        var d = Make();
        AddOmni(d, a, Vector3.Zero);
        AddOmni(d, a, Vector3.Zero, scene: true);
        Assert.Equal(SmoothTail.SceneWeight, d.LastWeight, 6);

        // 6 dB louder at the same spot is somewhere else: a backstop for a room change the region misses.
        var j = Make();
        AddOmni(j, a, Vector3.Zero);
        AddOmni(j, Tail(20, 2.2f), Vector3.Zero);
        Assert.Equal(1.0, j.LastWeight);
    }

    [Fact]
    public void ThePlayedTailCarriesTheAveragedEnergyPerBandPerFrame()
    {
        var s = Make();
        AddOmni(s, Tail(30), Vector3.Zero);
        var x = s.LateWindowed(afterDirectional: false);
        var got = BandFrames(x);
        int f0 = (int)(LateTailIr.FadeInEndSeconds * Rate) / SmoothTail.Frame + 1;   // after the fade-in
        int f1 = (int)(0.45 * Rate) / SmoothTail.Frame;
        const int group = 16;                                                      // 93 ms
        for (int b = 0; b < SmoothTail.Bands; b++)
        {
            double totalWant = 0, totalGot = 0;
            for (int g = f0; g + group <= f1; g += group)
            {
                double want = 0, have = 0;
                for (int f = g; f < g + group; f++) { want += s.OmniEnergy(b, f); have += got[b, f]; }
                totalWant += want; totalGot += have;
                // Band noise B wide over T seconds has its energy known to about 4.3/sqrt(BT) dB; three of
                // those, never tighter than 1 dB.
                double lo = b == 0 ? 0 : SmoothTail.EdgesHz[b - 1], hi = b < SmoothTail.EdgesHz.Length ? SmoothTail.EdgesHz[b] : Rate / 2.0;
                double tol = Math.Max(1.0, 3 * 4.34 / Math.Sqrt((hi - lo) * group * SmoothTail.Frame / (double)Rate));
                double d = 10 * Math.Log10(have / want);
                Assert.True(Math.Abs(d) <= tol, $"band {b}, frames {g}-{g + group}: {d:F2} dB off (allowed {tol:F1})");
            }
            // All of it: the tail's energy lasts about 0.1 s (its decay constant).
            double lo2 = b == 0 ? 0 : SmoothTail.EdgesHz[b - 1], hi2 = b < SmoothTail.EdgesHz.Length ? SmoothTail.EdgesHz[b] : Rate / 2.0;
            double tolAll = Math.Max(0.6, 3 * 4.34 / Math.Sqrt((hi2 - lo2) * 0.1));
            double dt = 10 * Math.Log10(totalGot / totalWant);
            Assert.True(Math.Abs(dt) <= tolAll, $"band {b} in all: {dt:F2} dB off (allowed {tolAll:F1})");
        }
    }

    [Fact]
    public void WithDirectionsTheTwoPartsTogetherCarryTheEnergy()
    {
        // A field from one way: the directional part goes that way above 355 Hz, evenly below; the
        // late part takes over at 250-350 ms in energy, so the sum is the averaged energy throughout.
        var s = Make(DiffuseBranch.Count);
        var w = Tail(40);
        var u = Vector3.Normalize(new Vector3(1f, 0.2f, 0f));
        var y = w.Select(v => v * u.Y).ToArray(); var z = w.Select(v => v * u.Z).ToArray(); var xx = w.Select(v => v * u.X).ToArray();
        s.Add(w, y, z, xx, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, Dirs(), null, Vector3.Zero, 1, false);

        var dirs = Dirs();
        int nearest = 0; float best = float.MinValue;
        for (int d = 0; d < dirs.Length; d++) { float dot = Vector3.Dot(u, dirs[d]); if (dot > best) { best = dot; nearest = d; } }
        for (int g = 0; g < SmoothTail.Groups; g++)
            for (int seg = 0; seg < SmoothTail.Segments; seg++)
                Assert.True(s.Share(g, seg, nearest) > 0.99, $"group {g} cell {seg}: {s.Share(g, seg, nearest):F3} in the nearest direction");

        var parts = s.DirectionalWindowed(Length);
        var late = s.LateWindowed(afterDirectional: true);
        var eLate = BandFrames(late);
        var eParts = parts.Select(BandFrames).ToArray();
        // From 100 ms (after the fade-in) to 450 ms, through the handover.
        int f0 = (int)(0.1 * Rate) / SmoothTail.Frame + 1, f1 = (int)(0.45 * Rate) / SmoothTail.Frame;
        for (int b = SmoothTail.DirFromBand; b < SmoothTail.Bands; b++)
        {
            double want = 0, have = 0;
            for (int f = f0; f < f1; f++)
            {
                want += s.OmniEnergy(b, f);
                have += eLate[b, f];
                foreach (var e in eParts) if (f < e.GetLength(1)) have += e[b, f];
            }
            Assert.InRange(10 * Math.Log10(have / want), -0.6, 0.6);
        }
        // Below 355 Hz every direction gets the same. The nearest is left out: everything above 355 Hz
        // went its way, and the band split's skirts would count some of that here.
        var low = eParts.Where((_, d) => d != nearest)
                        .Select(e => { double t = 0; for (int f = f0; f < e.GetLength(1); f++) t += e[2, f]; return t; }).ToArray();
        double mean = low.Average();
        Assert.All(low, v => Assert.InRange(10 * Math.Log10(v / mean), -3.0, 3.0));
    }

    [Fact]
    public void TheSameEnergiesGiveTheSameResponse()
    {
        // The carriers are made once: two builds from one average are the same response, sample for
        // sample, and so are two tracers fed the same traces.
        var a = Make(DiffuseBranch.Count); var b = Make(DiffuseBranch.Count);
        var w = Tail(50);
        var r = new Random(51);
        var c = new float[3][];
        for (int k = 0; k < 3; k++) c[k] = w.Select(v => v * (float)(r.NextDouble() * 2 - 1)).ToArray();
        foreach (var s in new[] { a, b })
            s.Add(w, c[0], c[1], c[2], Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, Dirs(), null, Vector3.Zero, 1, false);
        var l1 = a.BuildLate(Block, 200, true); var l2 = a.BuildLate(Block, 200, true); var l3 = b.BuildLate(Block, 200, true);
        Assert.Equal(l1.Re, l2.Re); Assert.Equal(l1.Im, l2.Im); Assert.Equal(l1.Re, l3.Re);
        var d1 = a.BuildDirectional(Block); var d2 = a.BuildDirectional(Block);
        for (int d = 0; d < d1.PerDirection.Length; d++)
        {
            Assert.Equal(d1.PerDirection[d] == null, d2.PerDirection[d] == null);
            if (d1.PerDirection[d] is { } p) Assert.Equal(p.Re, d2.PerDirection[d]!.Re);
        }

        // A trace a little louder scales the response by that gain, sample for sample, not its noise.
        var e = Make();
        AddOmni(e, w, Vector3.Zero);
        var before = e.LateWindowed(false);
        for (int k = 0; k < 30; k++) AddOmni(e, w.Select(v => v * 1.1f).ToArray(), Vector3.Zero);
        var after = e.LateWindowed(false);
        double num = 0, den = 0, err = 0;
        for (int i = 0; i < before.Length; i++) { num += after[i] * (double)before[i]; den += before[i] * (double)before[i]; }
        double gain = num / den;
        for (int i = 0; i < before.Length; i++) { double dd = after[i] - gain * before[i]; err += dd * dd; }
        Assert.InRange(gain, 1.09, 1.11);
        Assert.True(err / (gain * gain * den) < 1e-6, $"the response changed shape: {err / (gain * gain * den):E2}");
    }
}
