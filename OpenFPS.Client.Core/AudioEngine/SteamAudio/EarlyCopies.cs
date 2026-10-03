using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The early energy of the place you stand in that the placed copies already carry, worked out from
/// where the listener is, so the traced response can play the rest of it from the first reflection
/// on without counting any of it twice.
///
/// Why. The traced response used to start at 50 ms, faded in to 100, with the first 80 ms left to the
/// placed copies (WorldAudioPlayer.QueueEarlyEchoes). Those are a few discrete reflections: the walls
/// and the ceiling once, four second orders. Everything else that arrives in a room's first 50 ms
/// was missing: a clap in flat 01F was the clap, a few clicks, then nothing from 26 to 46 ms, then the
/// reverb building to a plateau at 70 ms (Cody, 2026-10-03: "a delay between when I clap and when I
/// hear the reflections").
///
/// How. Steam Audio's trace cannot be split into its specular and its scattered paths, and it has no
/// discrete peaks to gate: it is noise under an energy histogram of 10 ms bins (--early-tail: a lone
/// floor 6 m down answers from exactly 30 ms for a 35 ms arrival, and a lone floor's answer spreads
/// over 0-28 ms for a 10 ms one). So the copies are taken out as ENERGY: each copy's energy, band by
/// band, from the frames the trace put it in, from the start of its bin for <see cref="ReachSeconds"/>,
/// the same share of each, never more than the trace has there. The trace keeps the rest. This is what the hybrid room models do (image sources for the low-order specular paths,
/// rays for the rest, with those paths left out of the rays), done after the fact because the rays
/// are not ours. A copy and the trace then never both carry the same energy: where the trace has
/// less than the copies (it does in flat 01F's first 20 ms, by about 0.4 dB), it plays nothing there.
///
/// What a copy carries is what the room plan places (WorldAudioPlayer.PlanRoomEchoes) for a sound
/// at the listener, which is what the trace stands for: a first-order surface all it returns (its
/// mirror share as the copy, its scattered share as the wash beside it), the floor all it returns
/// (the voice's own ground reflection), a second order its mirror share. And nothing arrives before
/// the nearest surface's reflection: what the trace's first bin holds before then is moved to it.
/// </summary>
internal sealed class EarlyCopies
{
    /// <summary>Steam Audio's energy histogram bin: where a copy's energy can start in the trace.</summary>
    public const float TraceBinSeconds = 0.01f;
    /// <summary>How far past the start of its bin a copy's energy is looked for (--early-tail
    /// room=floor1.7: a 9.9 ms arrival spread over 0-28 ms of the trace).</summary>
    public const float ReachSeconds = 0.03f;

    /// <summary>The sample the first reflection arrives at (the nearest surface, there and back).</summary>
    public readonly int FirstArrival;
    /// <summary>Each copy: when it arrives (sample) and its energy in each of SmoothTail's bands, in
    /// the trace's units (a direct sound at 1 m has energy 1).</summary>
    public readonly List<(int Sample, double[] Band)> Copies = new();

    private EarlyCopies(int first) { FirstArrival = first; }

    /// <summary>The copies' energy summed, per band.</summary>
    public double[] Total()
    {
        var t = new double[SmoothTail.Bands];
        foreach (var (_, band) in Copies) for (int b = 0; b < t.Length; b++) t[b] += band[b];
        return t;
    }

    /// <summary>
    /// The copies placed round a sound at <paramref name="listener"/> in <paramref name="solids"/>, and
    /// where its first reflection lands. Null if nothing answers in the window. Any thread (the search
    /// keeps its scratch per thread).
    /// </summary>
    public static EarlyCopies? From(IReadOnlyList<EarlyReflections.Solid> solids, Vector3 listener, int sampleRate)
    {
        if (solids.Count == 0) return null;
        float c = AudioPhysics.CurrentSpeedOfSound;
        // A sound AT the listener: the search wants the two a little apart.
        var src = listener + new Vector3(0f, 0f, 0.01f);
        float direct = Vector3.Distance(src, listener);
        var found = new List<EarlyReflections.Arrival>();
        EarlyReflections.Find(src, listener, solids, found, c, maxOrder: 2, keep: WorldAudioPlayer.MaxRoomEchoes * 2,
                              maxExtraPathMetres: WorldAudioPlayer.RoomEchoWindowSeconds * c);
        if (found.Count == 0) return null;
        float nearest = float.MaxValue;
        foreach (var a in found) if (a.Order == 1) nearest = MathF.Min(nearest, a.PathLength);
        var plan = new List<WorldAudioPlayer.RoomEcho>();
        WorldAudioPlayer.PlanRoomEchoes(found, src, listener, direct, 1f, 1f, audible: false, plan);
        var copies = new EarlyCopies(nearest < float.MaxValue ? (int)(nearest / c * sampleRate) : 0);
        foreach (var e in plan) copies.Copies.Add(((int)(e.Arrival.PathLength / c * sampleRate), Energy(e, direct)));
        copies.Copies.Sort(static (x, y) => x.Sample.CompareTo(y.Sample));
        return copies;
    }

    /// <summary>One placed copy's energy per band, a sound at the listener: what the surfaces keep
    /// over the path's spreading, its mirror share and the mirror's loss at the top, plus a first
    /// order's wash.</summary>
    internal static double[] Energy(in WorldAudioPlayer.RoomEcho e, float direct)
    {
        var a = e.Arrival;
        // The arrival's gains are the surfaces' keep times direct / path: back to the keep.
        float back = a.PathLength / MathF.Max(1e-4f, direct);
        float kl = a.GainLow * back, km = a.GainMid * back, kh = a.GainHigh * back;
        float s = e.Scattering;
        var loss = ImageSource.SpecularBandLossDb(s, a.Order);
        double l2 = a.PathLength * (double)a.PathLength;
        var band = new double[SmoothTail.Bands];
        for (int b = 0; b < band.Length; b++)
        {
            float hz = BandCentre(b);
            double keep = AcousticRegistry.AtFrequency(kl, km, kh, hz);
            double share;
            if (e.InVoice) share = 1.0;                                        // the voice's ground: all of it
            else
            {
                double mirror = WorldAudioPlayer.MirrorShare(s, a.Order) * Math.Pow(10, AcousticRegistry.AtFrequency(loss.LowDb, 0f, loss.HighDb, hz) / 20);
                double wash = a.Order == 1 && s > 0.05f ? Math.Sqrt(s) : 0.0;
                share = mirror * mirror + wash * wash;
            }
            band[b] = BandShare[b] * keep * keep * share / l2;
        }
        return band;
    }

    /// <summary>Each of SmoothTail's bands' share of a flat spectrum's energy (they sum to one).</summary>
    internal static readonly double[] BandShare = MakeBandShare(44100);

    private static double[] MakeBandShare(int rate)
    {
        var share = new double[SmoothTail.Bands];
        const int n = 8192;
        double sum = 0;
        for (int k = 0; k < n; k++)
        {
            double hz = (k + 0.5) * rate / 2.0 / n;
            for (int b = 0; b < share.Length; b++) { double p = SmoothTail.BandPower(b, hz, rate); share[b] += p; sum += p; }
        }
        for (int b = 0; b < share.Length; b++) share[b] /= sum;
        return share;
    }

    /// <summary>Band <paramref name="b"/>'s middle, Hz (geometric; the outer bands half and twice
    /// their one edge).</summary>
    internal static float BandCentre(int b)
    {
        var e = SmoothTail.EdgesHz;
        if (b == 0) return e[0] / 2f;
        if (b >= e.Length) return e[^1] * 2f;
        return MathF.Sqrt(e[b - 1] * e[b]);
    }

    /// <summary>
    /// Takes the copies' energy out of a trace's per-band frame energies <paramref name="e"/>
    /// ([band * frames + frame], frames of <paramref name="frame"/> samples), in place; adds what was
    /// taken, per band, to <paramref name="took"/>. Then moves whatever is left before the first
    /// reflection's frame into that frame. Energy is kept: the trace before = what is left plus what
    /// was taken.
    /// </summary>
    public void TakeFrom(double[] e, int frames, int frame, int sampleRate, double[]? took = null)
    {
        int bands = SmoothTail.Bands;
        int bin = Math.Max(1, (int)(TraceBinSeconds * sampleRate)), reach = (int)(ReachSeconds * sampleRate);
        foreach (var (sample, band) in Copies)
        {
            int from = sample / bin * bin;
            int f0 = Math.Min(frames - 1, from / frame), f1 = Math.Min(frames - 1, (from + reach) / frame);
            for (int b = 0; b < bands; b++)
            {
                // The same share of every frame in reach: the trace's own envelope there is its best
                // guess of when the energy arrives, so it keeps its shape, only less of it. Taken from
                // the first frames on, the copies dug a hole where they ran out of reach.
                double have = 0;
                for (int f = f0; f <= f1; f++) have += e[b * frames + f];
                if (have <= 0) continue;
                double take = Math.Min(band[b], have), keep = 1.0 - take / have;
                for (int f = f0; f <= f1; f++) e[b * frames + f] *= keep;
                if (took != null) took[b] += take;
            }
        }
        int ff = Math.Min(frames - 1, FirstArrival / frame);
        for (int b = 0; b < bands; b++)
            for (int f = 0; f < ff; f++) { e[b * frames + ff] += e[b * frames + f]; e[b * frames + f] = 0; }
    }
}
