using System;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The room's tail must reach the two ears as two signals. The traced reverb hands back the same one
/// to both (its diffuse field is all omni), which is the room in the middle of the head; one chain of
/// all-passes per ear must pull them apart without changing how loud they are (2026-09-29).
/// </summary>
public class EarDecorrelatorTests
{
    private readonly ITestOutputHelper _o;
    public EarDecorrelatorTests(ITestOutputHelper o) => _o = o;

    private static double Iacc(float[] a, float[] b, int from)
    {
        double best = 0, ea = 0, eb = 0;
        for (int i = from; i < a.Length; i++) { ea += a[i] * (double)a[i]; eb += b[i] * (double)b[i]; }
        for (int lag = -44; lag <= 44; lag++)          // +-1 ms, as the ears allow
        {
            double s = 0;
            for (int i = from + 44; i < a.Length - 44; i++) s += a[i] * (double)b[i + lag];
            best = Math.Max(best, Math.Abs(s));
        }
        return best / Math.Sqrt(ea * eb);
    }

    private static float[] HighPass(float[] x, float hz)
    {
        var y = new float[x.Length];
        float a = MathF.Exp(-2f * MathF.PI * hz / 44100f), prev = 0f, lp = 0f;
        for (int i = 0; i < x.Length; i++) { lp = (1 - a) * x[i] + a * lp; y[i] = x[i] - lp; prev = x[i]; }
        return y;
    }

    private static float[] LowPass(float[] x, float hz)
    {
        var y = new float[x.Length];
        float a = MathF.Exp(-2f * MathF.PI * hz / 44100f), p1 = 0f, p2 = 0f, p3 = 0f;
        for (int i = 0; i < x.Length; i++)
        {
            p1 = (1 - a) * x[i] + a * p1; p2 = (1 - a) * p1 + a * p2; p3 = (1 - a) * p2 + a * p3;
            y[i] = p3;
        }
        return y;
    }

    [Fact]
    public void OneSignalBecomesTwoAtTheSameLevel()
    {
        var rng = new Random(5);
        int n = 44100;
        var mono = new float[n];
        // A room's tail: noise dying away over half a second.
        for (int i = 0; i < n; i++) mono[i] = ((float)rng.NextDouble() * 2 - 1) * MathF.Exp(-i / (0.5f * 44100 / 6.9f));
        var l = new EarDecorrelator(0); var r = new EarDecorrelator(1);
        var L = new float[n]; var R = new float[n];
        for (int i = 0; i < n; i++) { L[i] = l.Process(mono[i]); R[i] = r.Process(mono[i]); }

        double broad = Iacc(L, R, 2000), high = Iacc(HighPass(L, 1000f), HighPass(R, 1000f), 2000);
        double el = 0, em = 0; for (int i = 0; i < n; i++) { el += L[i] * (double)L[i]; em += mono[i] * (double)mono[i]; }
        _o.WriteLine($"interaural correlation: broadband {broad:F2}, above 1 kHz {high:F2}; level {10 * Math.Log10(el / em):+0.0;-0.0} dB");
        Assert.True(broad < 0.6, $"still one signal: {broad:F2}");
        double low = Iacc(LowPass(L, 150f), LowPass(R, 150f), 2000);
        _o.WriteLine($"below 150 Hz: {low:F2}");
        Assert.True(low > 0.8, $"the bass was pulled apart too: {low:F2}");
        Assert.True(high < 0.35, $"still one signal above 1 kHz: {high:F2}");
        Assert.InRange(10 * Math.Log10(el / em), -1.0, 1.0);
    }

    /// <summary>
    /// And it must not become a room of its own. The first set of delays ran to 13 ms each, six deep,
    /// at a feedback of 0.6: a click came out as a hundred milliseconds of build-up peaking 20-45 ms
    /// late, laid over every reflection the trace handed back. Every flat sounded like a stadium and
    /// the lobby "like I've got my ears cupped" (2026-09-29, capture of 52 claps).
    /// </summary>
    [Fact]
    public void AClickStaysAClick()
    {
        foreach (int ear in new[] { 0, 1 })
        {
            var d = new EarDecorrelator(ear);
            int n = 4410;
            var y = new float[n];
            for (int i = 0; i < n; i++) y[i] = d.Process(i == 0 ? 1f : 0f);
            double total = 0; foreach (var v in y) total += v * (double)v;
            double acc = 0; int at = 0;
            for (; at < n; at++) { acc += y[at] * (double)y[at]; if (acc >= 0.9 * total) break; }
            double ms = at / 44.1;
            _o.WriteLine($"ear {ear}: 90 % of a click back by {ms:F1} ms");
            Assert.True(ms < 12, $"ear {ear}: a click is smeared over {ms:F1} ms");
        }
    }

    /// <summary>
    /// Neither ear first. The room's tail comes from all round, so it must not reach one ear before the
    /// other: the right chain's delays summed to 32 samples more than the left's, the tail arrived
    /// 0.7 ms earlier on the left, and the whole room sat on the left whichever way the listener
    /// turned (2026-09-29).
    /// </summary>
    [Fact]
    public void NeitherEarHearsTheRoomFirst()
    {
        var rng = new Random(9);
        int n = 44100;
        var mono = new float[n];
        for (int i = 0; i < n; i++) mono[i] = ((float)rng.NextDouble() * 2 - 1) * MathF.Exp(-i / (0.5f * 44100 / 6.9f));
        var l = new EarDecorrelator(0); var r = new EarDecorrelator(1);
        var L = new float[n]; var R = new float[n];
        for (int i = 0; i < n; i++) { L[i] = l.Process(mono[i]); R[i] = r.Process(mono[i]); }
        int bestLag = 0; double best = double.MinValue;
        for (int lag = -60; lag <= 60; lag++)
        {
            double s = 0;
            for (int i = 2000 + 60; i < 8000; i++) s += L[i] * (double)R[i + lag];
            if (s > best) { best = s; bestLag = lag; }
        }
        _o.WriteLine($"right ear against left: {bestLag} samples ({bestLag / 44.1:F2} ms)");
        Assert.InRange(bestLag, -5, 5);
    }
}
