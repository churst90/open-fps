using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The room answers from its first reflection on (2026-10-03, "a delay between when I clap and when I
/// hear the reflections"). The traced response used to start at 50 ms with the first 80 ms left to a
/// few placed copies, so a clap in flat 01F had nothing at all from 26 to 46 ms. Now the trace plays
/// from the nearest surface's reflection, less the energy the placed copies carry (EarlyCopies), and
/// the bus waits for the voices' binaural rendering (TracedReverbDsp.StagePreDelay).
/// </summary>
public class EarlyTailTests
{
    private const int Rate = 44100, Block = 256;
    private static readonly int Length = (int)(0.6 * Rate);

    public EarlyTailTests() { AcousticRegistry.Initialize(); }

    /// <summary>Marlow flat 01F, as the clap-room lab builds it.</summary>
    private static List<EarlyReflections.Solid> Flat()
    {
        var q = Quaternion.Identity;
        return new()
        {
            new(new Vector3(0, 0.04f, 0), new Vector3(8.65f, 0.04f, 17.86f), q, "Carpet"),
            new(new Vector3(0, -0.07f, 0), new Vector3(21f, 0.17f, 90f), q, "Concrete"),
            new(new Vector3(0, 2.735f, 0), new Vector3(20.5f, 0.03f, 89.3f), q, "Plaster"),
            new(new Vector3(4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, 80f), q, "Brick"),
            new(new Vector3(-4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, 17.86f), q, "Plaster"),
            new(new Vector3(0, 1.4f, -9.0f), new Vector3(8.65f, 2.73f, 0.2f), q, "Plaster"),
            new(new Vector3(0, 1.4f, 9.0f), new Vector3(8.65f, 2.73f, 0.2f), q, "Plaster"),
        };
    }

    private static readonly Vector3 Ear = new(0.175f, 1.7f, 0.16f);

    /// <summary>A decaying white noise tail from the very first sample, as Steam Audio's first 10 ms
    /// bin hands one back: 60 dB down in about 0.7 s.</summary>
    private static float[] Trace(int seed, float gain = 1f)
    {
        var r = new Random(seed); var w = new float[Length];
        for (int i = 0; i < Length; i++)
        {
            double g = Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
            w[i] = (float)(g * gain * Math.Exp(-i / (0.1 * Rate)));
        }
        return w;
    }

    private static Vector3[] Dirs()
    {
        var d = new Vector3[DiffuseBranch.Count];
        for (int i = 0; i < d.Length; i++) d[i] = DiffuseTail.Direction(i);
        return d;
    }

    private static SmoothTail WithDirections(float[] w, EarlyCopies? copies)
    {
        var s = new SmoothTail(Rate, Length, DiffuseBranch.Count);
        var u = Vector3.Normalize(new Vector3(1f, 0.2f, 0.3f));
        var y = w.Select(v => v * u.Y).ToArray(); var z = w.Select(v => v * u.Z).ToArray(); var x = w.Select(v => v * u.X).ToArray();
        s.Add(w, y, z, x, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, Dirs(), null, Vector3.Zero, 1, false, copies);
        return s;
    }

    /// <summary>Energy per 2 ms of the directional part (every direction's energy summed) and the
    /// late part after it.</summary>
    private static double[] TwoMs(SmoothTail s, int bins)
    {
        var e = new double[bins];
        void Add(float[] x) { for (int i = 0; i < x.Length; i++) { int k = i / (Rate / 500); if (k < bins) e[k] += x[i] * (double)x[i]; } }
        foreach (var p in s.DirectionalWindowed(Length)) Add(p);
        Add(s.LateWindowed(afterDirectional: true));
        return e;
    }

    [Fact]
    public void TheFirstReflectionIsTheNearestSurfaceThereAndBack()
    {
        // In the flat the nearest surface is the ceiling, 1.02 m over the ear: 2.04 m, 5.9 ms.
        var c = EarlyCopies.From(Flat(), Ear, Rate)!;
        Assert.NotNull(c);
        double ms = c.FirstArrival * 1000.0 / Rate;
        Assert.InRange(ms, 5.7, 6.1);
        // The copies are the room plan's for a sound at the ear: the six surfaces and the second orders,
        // nothing past the placed window.
        Assert.InRange(c.Copies.Count, 6, OpenFPS.Client.Core.WorldAudioPlayer.MaxRoomEchoes + 1);
        Assert.All(c.Copies, k => Assert.True(k.Sample <= (OpenFPS.Client.Core.WorldAudioPlayer.RoomEchoWindowSeconds + 0.002) * Rate));
        // Their energy sums to about what the image sources say: the ceiling alone (plaster, 2.04 m)
        // is (1 - 0.1) / 2.04^2 = -6.7 dB of a 1 m impulse; all of them a little more.
        double total = c.Total().Sum();
        Assert.InRange(10 * Math.Log10(total), -7.5, -3.0);
        Assert.InRange(EarlyCopies.BandShare.Sum(), 0.999, 1.001);
    }

    [Fact]
    public void TheResponseStartsAtTheFirstReflectionWithNoGap()
    {
        // At the flat's own level: its trace has about -8 dB of a 1 m impulse in its first 10 ms, and
        // the copies take most of the first 20 ms of it.
        var copies = EarlyCopies.From(Flat(), Ear, Rate)!;
        var s = WithDirections(Trace(3, 0.02f), copies);
        Assert.Equal(copies.FirstArrival, s.Start);
        var e = TwoMs(s, 60);
        int first = s.Start / (Rate / 500);
        // Nothing before the nearest surface can answer.
        for (int k = 0; k < first; k++) Assert.True(e[k] == 0, $"energy at {k * 2}-{k * 2 + 2} ms, before the first reflection");
        // And from the bin after it on, every 2 ms has some of the room: nothing more than 30 dB under
        // its neighbourhood (a gap is hundreds of decibels down; noise and the copies' share are a few).
        double peak = e.Max();
        for (int k = first + 1; k < 60; k++)
        {
            double local = e.Skip(Math.Max(first + 1, k - 5)).Take(11).Average();
            Assert.True(e[k] > local * 1e-3, $"{k * 2}-{k * 2 + 2} ms: {10 * Math.Log10(e[k] / peak + 1e-30):F1} dB, a gap");
        }
    }

    [Fact]
    public void NothingIsCountedTwice()
    {
        // The copies' energy comes out of the trace's frames, band by band, from where the trace put
        // it: what is left plus what was taken is what the trace had, and nothing is taken that the
        // copies do not carry.
        var copies = EarlyCopies.From(Flat(), Ear, Rate)!;
        var s = new SmoothTail(Rate, Length, 0);
        int frames = s.Frames;
        foreach (float gain in new[] { 1f, 0.3f, 0.05f })          // the trace well over, near and under the copies
        {
            var w = Trace(5, gain);
            var before = Measure(w, frames);
            var after = (double[])before.Clone();
            var took = new double[SmoothTail.Bands];
            copies.TakeFrom(after, frames, SmoothTail.Frame, Rate, took);
            var carry = copies.Total();
            for (int b = 0; b < SmoothTail.Bands; b++)
            {
                double had = 0, left = 0;
                for (int f = 0; f < frames; f++) { had += before[b * frames + f]; left += after[b * frames + f]; }
                Assert.Equal(had, left + took[b], 6);
                Assert.True(took[b] <= carry[b] * (1 + 1e-9), $"band {b}: took {took[b]:E3} where the copies carry {carry[b]:E3}");
                Assert.All(Enumerable.Range(0, frames), f => Assert.True(after[b * frames + f] >= 0));
                // Where the trace has more than enough, the copies take exactly theirs.
                double inReach = 0;
                for (int f = 0; f < (int)(0.11 * Rate) / SmoothTail.Frame; f++) inReach += before[b * frames + f];
                if (inReach > 4 * carry[b]) Assert.Equal(carry[b], took[b], 9);
            }
            // Nothing left before the first reflection's frame: it was moved there, not lost.
            int ff = copies.FirstArrival / SmoothTail.Frame;
            for (int b = 0; b < SmoothTail.Bands; b++)
                for (int f = 0; f < ff; f++) Assert.Equal(0.0, after[b * frames + f]);
        }

        // Through SmoothTail, at the flat's level: what the copies took plus the played early part is
        // what the trace played without them (the carriers are the same, so this is nearly exact).
        var t = Trace(7, 0.02f);
        var plain = WithDirections(t, null);
        var less = WithDirections(t, copies);
        double copiesE = less.LastCopies.Sum(), took2 = less.LastTook.Sum();
        Assert.True(took2 <= copiesE * (1 + 1e-9));
        Assert.True(took2 > 0.5 * copiesE, $"the trace gave up only {took2 / copiesE:P0} of the copies' energy");
        var ePlain = TwoMs(plain, 55); var eLess = TwoMs(less, 55);     // 0-110 ms, the copies' reach
        double sumPlain = ePlain.Sum(), sumLess = eLess.Sum();
        double db = 10 * Math.Log10((sumLess + took2) / sumPlain);
        Assert.InRange(db, -0.5, 0.5);
        Assert.True(took2 / sumPlain > 0.1, "the copies are a real share of this early part");
    }

    [Fact]
    public void TheLateTailIsUntouched()
    {
        // From where the copies' reach and the smoothing end, the response is what it was: the late
        // field sample for sample, the directional part sample for sample.
        var copies = EarlyCopies.From(Flat(), Ear, Rate)!;
        var t = Trace(11);
        var a = WithDirections(t, null);
        var b = WithDirections(t, copies);
        var noise = DiffuseLateNoise.Shared(Rate, Length, DiffuseBranch.Count);
        var fa = a.BuildDiffuseLate(noise); var fb = b.BuildDiffuseLate(noise);
        Assert.Equal(fa.C0, fb.C0); Assert.Equal(fa.C1, fb.C1);
        var la = a.LateWindowed(true); var lb = b.LateWindowed(true);
        Assert.Equal(la, lb);
        var pa = a.DirectionalWindowed(Length); var pb = b.DirectionalWindowed(Length);
        int from = (int)(0.15 * Rate);
        for (int d = 0; d < pa.Length; d++)
            for (int i = from; i < pa[d].Length; i++) Assert.Equal(pa[d][i], pb[d][i]);
    }

    [Fact]
    public void TheBusWaitsForTheVoices()
    {
        // Measured (--early-tail hrtf): an impulse comes out of the voices' binaural effect (1,024)
        // 289 samples late and out of the traced stage's (256) 97: the stage waits 192.
        Assert.Equal(192, TracedReverbDsp.StagePreDelay(289, 97));
        Assert.Equal(0, TracedReverbDsp.StagePreDelay(97, 289));     // never earlier than it came
        Assert.Equal(0, TracedReverbDsp.StagePreDelay(-1, 97));      // not known: no wait
        var d = new PreDelay(192);
        var y = new float[600];
        for (int i = 0; i < y.Length; i++) y[i] = d.Process(i == 10 ? 1f : i == 11 ? -0.5f : 0f);
        for (int i = 0; i < y.Length; i++)
            Assert.Equal(i == 202 ? 1f : i == 203 ? -0.5f : 0f, y[i]);
        var none = new PreDelay(0);
        Assert.Equal(0.7f, none.Process(0.7f));
    }

    /// <summary>A trace's per-band frame energies, as SmoothTail measures them.</summary>
    private static double[] Measure(float[] w, int frames)
    {
        var split = new SmoothTail.Splitter(Rate);
        var e = new double[SmoothTail.Bands * frames];
        var band = new double[SmoothTail.Bands];
        for (int i = 0; i < w.Length; i++)
        {
            split.Run(w[i], band);
            for (int b = 0; b < band.Length; b++) e[b * frames + i / SmoothTail.Frame] += band[b] * band[b];
        }
        return e;
    }
}
