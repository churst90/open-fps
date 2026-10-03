using System;
using System.Numerics;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The tail of the place you stand in, played as what a late field is: noise under the room's
/// energy envelope (Polack), arriving from where the room sends it. Measured from each trace as
/// ENERGY, averaged over traces, and played through noise that never changes.
///
/// Why. The listener's trace is redone four times a second. Its omnidirectional channel is steady
/// from one trace to the next (measured: correlation 1.000 at a standing point, --tail-steady), but
/// its first-order channels are not: the directions are a Monte Carlo estimate from 8,192 rays,
/// and from one trace to the next a direction's share of the 50-350 ms part swung from 0 to 5 %
/// and back. The directional part (SdmTailIr.Build) sent every sample the way its own trace said,
/// so the tail's directions were shuffled four times a second. At the ears a steady hum's harmonics
/// swung 2.4-3.8 dB from one 54 ms window to the next above 500 Hz, where a held trace gives 0: the
/// reflections "bouncing around", a wavy tail instead of one wash.
///
/// Three steps, each trace:
///   1. Measure: the omnidirectional channel split into octave bands, energy per band per frame of
///      <see cref="Frame"/> samples. For the directional part, where the energy comes from: each
///      sample sent the way its intensity points (the SDM rule), its energy summed per direction
///      over a few wide cells of time and frequency (<see cref="SegmentEdges"/>, two band groups),
///      so one trace's estimate is made from thousands of samples, not one.
///   2. Average in energy (<see cref="Add"/>): a long average while you stand, a short one while you
///      walk, a fresh start in a new room.
///   3. Play: per band and per direction its own fixed noise, made once, scaled by the square root of
///      the averaged energy. Two traces that measure the same give the same response.
/// </summary>
internal sealed class SmoothTail
{
    /// <summary>The frame the energy is measured in: the traced block, 5.8 ms at 44.1 kHz.</summary>
    public const int Frame = 256;

    /// <summary>
    /// Band edges, Hz: octaves, centred on 63 Hz to 16 kHz. Measured, not chosen by taste: Steam
    /// Audio traces in three bands, but what it hands back is not flat inside them (a dip of about
    /// 5 dB round 1 kHz, where its 800 Hz crossover is). Five bands (250, 800, 2,500, 8,000) put the
    /// tail up to 4.4 dB off the trace's own spectrum in third octaves (--tail-steady). Octaves
    /// follow it to 1-2 dB above 200 Hz, about what two noise realisations differ by anyway.
    /// </summary>
    public static readonly float[] EdgesHz = { 88f, 177f, 355f, 710f, 1420f, 2840f, 5680f, 11360f };
    public static int Bands => EdgesHz.Length + 1;

    /// <summary>
    /// The first band whose directional part follows the trace's directions (355 Hz up). Below it
    /// the directional part arrives evenly from all the directions, and that never changes. Measured
    /// both other ways: split by the trace, the low end's split moved and a 110 Hz hum's low
    /// harmonics swung 3 dB; played instead through the diffuse late path from 50 ms, the tail lost
    /// 5 dB at 250 Hz in its first 250 ms (that path's low split is not energy-flat) and its early
    /// decay at 250 Hz went from 0.76 to 0.98 s. A head tells little of where a late sound under
    /// about 350 Hz comes from.
    /// </summary>
    public const int DirFromBand = 3;
    /// <summary>The directional part's two band groups: 355 Hz-2.8 kHz and 2.8 kHz up.</summary>
    public const int DirHighBand = 6;
    public const int Groups = 2;
    /// <summary>
    /// The time cells the directions are estimated over, seconds: shorter early, where separate
    /// reflections come from separate walls, longer later, where the trace's directions are mostly
    /// noise. Each holds 1,100-5,100 samples. From the sound itself: the directional part starts at
    /// the first reflection (EarlyCopies), no longer at 50 ms.
    /// </summary>
    public static readonly float[] SegmentEdges = { 0f, 0.025f, 0.05f, 0.075f, 0.1f, 0.133f, 0.175f, 0.233f, 0.35f };
    public static int Segments => SegmentEdges.Length - 1;

    /// <summary>How much a new trace counts while you stand still. A quarter: a step change is
    /// 90 % in after eight traces (2 s), and the average holds about seven traces' worth.</summary>
    public const double StillWeight = 0.25;
    /// <summary>Within this of where the average stands, it is the same place.</summary>
    public const float StillMetres = 0.5f;
    /// <summary>Past this from it, the average starts again from the new trace.</summary>
    public const float MovedMetres = 1.0f;
    /// <summary>After the scene changes (a door moved), a trace counts at least this much.</summary>
    public const double SceneWeight = 0.5;
    /// <summary>A trace whose late energy is this far from the average is another room: start again.</summary>
    public const double JumpDb = 6.0;

    public readonly int SampleRate, Length, Frames, DirFrames, DirCount;
    private readonly int _dirLength;

    // The average, in energy. _omni[b * Frames + f]; _share[(g * Segments + s) * DirCount + d].
    private readonly double[] _omni, _share, _cov;
    private Vector3 _where;
    private int _place = int.MinValue;
    private bool _empty = true;
    /// <summary>The sample the response starts at: the first reflection (EarlyCopies.FirstArrival), or
    /// the trace's own first sample when the geometry is not known.</summary>
    private int _start;
    public int Start => _start;
    /// <summary>The lab's A/B: the response as it was before 2026-10-03, faded in at 50-100 ms with
    /// nothing taken out for the copies. Never set in the game.</summary>
    public static bool FromFiftyMs;
    /// <summary>Where the played response starts, and where it is all the way in.</summary>
    private (int From, int In) EarlyWindow()
        => FromFiftyMs ? ((int)(LateTailIr.FadeInStartSeconds * SampleRate), (int)(LateTailIr.FadeInEndSeconds * SampleRate))
                       : (_start, _start);
    private static float FadeIn(int i, (int From, int In) w)
        => i < w.From ? 0f : i >= w.In ? 1f : 0.5f - 0.5f * MathF.Cos(MathF.PI * (i - w.From) / Math.Max(1, w.In - w.From));
    /// <summary>The last trace's early energy per band: what the placed copies carry, and how much of
    /// it the trace had and gave up to them (EarlyCopies.TakeFrom). For the lab and the tests.</summary>
    public double[] LastTook { get; } = new double[EdgesHz.Length + 1];
    public double[] LastCopies { get; private set; } = new double[EdgesHz.Length + 1];

    /// <summary>What the last trace counted for (1: a fresh start). For the log and the lab.</summary>
    public double LastWeight { get; private set; }
    /// <summary>Fresh starts so far.</summary>
    public int Resets { get; private set; }

    // The fixed carriers: unit-variance noise per band, band-limited by the same filters.
    private readonly float[][] _omniCarrier;       // [b][i], i < Length
    private readonly float[][] _dirCarrier;        // [d * Bands + b][i], i < _dirLength

    // Scratch for one trace's measurement.
    private readonly int[] _doa;
    private readonly double[] _mOmni, _mShare;

    /// <param name="length">Samples per trace (TracedReverb.IrSize).</param>
    /// <param name="directions">How many directions the directional part is split into; 0 for none.</param>
    /// <param name="seed">Fixes the carriers. One per tracer, made once.</param>
    public SmoothTail(int sampleRate, int length, int directions, int seed = 4242)
    {
        SampleRate = sampleRate; Length = length; DirCount = Math.Max(0, directions);
        Frames = (length + Frame - 1) / Frame;
        _dirLength = Math.Min(length, (int)(SdmTailIr.EndFadeEnd * sampleRate) + 1);
        DirFrames = (_dirLength + Frame - 1) / Frame;
        int nb = Bands;
        _omni = new double[nb * Frames];
        _share = new double[Groups * Segments * DirCount];
        _cov = new double[TracedReverb.Channels];
        _mOmni = new double[_omni.Length];
        _mShare = new double[_share.Length];
        _doa = new int[_dirLength];

        var rng = new Random(seed);
        _omniCarrier = new float[nb][];
        for (int k = 0; k < nb; k++) _omniCarrier[k] = Carrier(rng, k, length, sampleRate);
        _dirCarrier = new float[DirCount * nb][];
        for (int d = 0; d < DirCount; d++)
            for (int k = 0; k < nb; k++) _dirCarrier[d * nb + k] = Carrier(rng, k, _dirLength, sampleRate);
    }

    // ── The band filters ─────────────────────────────────────────────────────────────────────
    //
    // A tree of fourth-order Butterworth splits: low-pass at each edge gives a band, the high-pass
    // goes on to the next edge. A Butterworth low-pass and high-pass of the same order at the same
    // frequency are power complementary (|L|^2 + |H|^2 = 1), so the bands' energies add up to the
    // whole, and independent noise shaped by the same filters adds back to a flat spectrum.

    private struct Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;
        public static Biquad Make(bool high, double hz, double q, int rate)
        {
            double w = 2 * Math.PI * hz / rate, c = Math.Cos(w), al = Math.Sin(w) / (2 * q), a0 = 1 + al;
            var f = new Biquad();
            if (high) { f._b0 = (1 + c) / 2 / a0; f._b1 = -(1 + c) / a0; f._b2 = (1 + c) / 2 / a0; }
            else { f._b0 = (1 - c) / 2 / a0; f._b1 = (1 - c) / a0; f._b2 = (1 - c) / 2 / a0; }
            f._a1 = -2 * c / a0; f._a2 = (1 - al) / a0;
            return f;
        }
        public double Run(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
        /// <summary>|H|^2 at <paramref name="w"/> radians per sample.</summary>
        public double Power(double w)
        {
            double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
            double nr = _b0 + _b1 * c1 + _b2 * c2, ni = -_b1 * s1 - _b2 * s2;
            double dr = 1 + _a1 * c1 + _a2 * c2, di = -_a1 * s1 - _a2 * s2;
            return (nr * nr + ni * ni) / (dr * dr + di * di);
        }
    }

    /// <summary>Band <paramref name="band"/>'s power gain at <paramref name="hz"/>: the filters a
    /// band's measurement and its carrier go through (see <see cref="Carrier"/>).</summary>
    public static double BandPower(int band, double hz, int rate)
    {
        double w = 2 * Math.PI * hz / rate, g = 1;
        for (int e = 0; e < band; e++) g *= Biquad.Make(true, EdgesHz[e], Q1, rate).Power(w) * Biquad.Make(true, EdgesHz[e], Q2, rate).Power(w);
        if (band < EdgesHz.Length) g *= Biquad.Make(false, EdgesHz[band], Q1, rate).Power(w) * Biquad.Make(false, EdgesHz[band], Q2, rate).Power(w);
        return g;
    }

    // The two sections of a fourth-order Butterworth.
    private static readonly double Q1 = 1 / (2 * Math.Cos(Math.PI / 8)), Q2 = 1 / (2 * Math.Cos(3 * Math.PI / 8));

    /// <summary>The band split, one sample at a time.</summary>
    public sealed class Splitter
    {
        private readonly Biquad[] _lo, _hi;
        public Splitter(int rate)
        {
            _lo = new Biquad[EdgesHz.Length * 2]; _hi = new Biquad[EdgesHz.Length * 2];
            for (int e = 0; e < EdgesHz.Length; e++)
            {
                _lo[2 * e] = Biquad.Make(false, EdgesHz[e], Q1, rate); _lo[2 * e + 1] = Biquad.Make(false, EdgesHz[e], Q2, rate);
                _hi[2 * e] = Biquad.Make(true, EdgesHz[e], Q1, rate); _hi[2 * e + 1] = Biquad.Make(true, EdgesHz[e], Q2, rate);
            }
        }
        /// <summary>One sample into <paramref name="bands"/>[b].</summary>
        public void Run(double x, Span<double> bands)
        {
            for (int e = 0; e < EdgesHz.Length; e++)
            {
                bands[e] = _lo[2 * e + 1].Run(_lo[2 * e].Run(x));
                x = _hi[2 * e + 1].Run(_hi[2 * e].Run(x));
            }
            bands[EdgesHz.Length] = x;
        }
    }

    /// <summary>Unit-variance Gaussian noise in band <paramref name="band"/>, through the same filters
    /// as the measurement (the high-passes of the edges below it, the low-pass of the edge above).
    /// Run in from 0.2 s before, so the filters have settled.</summary>
    private static float[] Carrier(Random rng, int band, int n, int rate)
    {
        const int pre = 8192;
        var path = new System.Collections.Generic.List<Biquad>();
        for (int e = 0; e < band; e++) { path.Add(Biquad.Make(true, EdgesHz[e], Q1, rate)); path.Add(Biquad.Make(true, EdgesHz[e], Q2, rate)); }
        if (band < EdgesHz.Length) { path.Add(Biquad.Make(false, EdgesHz[band], Q1, rate)); path.Add(Biquad.Make(false, EdgesHz[band], Q2, rate)); }
        var f = path.ToArray();
        var c = new float[n];
        double e2 = 0;
        for (int i = -pre; i < n; i++)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double v = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            for (int k = 0; k < f.Length; k++) v = f[k].Run(v);
            if (i >= 0) { c[i] = (float)v; e2 += v * v; }
        }
        float g = e2 > 0 ? (float)Math.Sqrt(n / e2) : 0f;
        for (int i = 0; i < n; i++) c[i] *= g;
        return c;
    }

    // ── 1 and 2: measure a trace and add it to the average ──────────────────────────────────

    /// <summary>
    /// Measures one trace and adds it to the average. <paramref name="w"/> is the omnidirectional
    /// channel, time zero at the direct sound; <paramref name="c1"/>..<paramref name="c3"/> the
    /// first-order ones with their world axes <paramref name="a1"/>..<paramref name="a3"/> (null
    /// directions: no directional part). <paramref name="cov"/> is the remainder's channel
    /// covariance (TracedReverb's), averaged with the rest. <paramref name="at"/> is where the
    /// trace was made from, <paramref name="place"/> the region it was in; <paramref name="sceneChanged"/>
    /// says the geometry moved since the last trace. <paramref name="copies"/>: the early energy the
    /// placed copies carry from where the trace was made; it is taken out of this trace's energies, and
    /// the response starts at its first reflection. Null: nothing is taken out and the response starts
    /// where the trace does. Tracer thread.
    /// </summary>
    public void Add(float[] w, float[]? c1, float[]? c2, float[]? c3, Vector3 a1, Vector3 a2, Vector3 a3,
                    Vector3[]? directions, double[]? cov, Vector3 at, int place, bool sceneChanged,
                    EarlyCopies? copies = null)
    {
        Measure(w, c1, c2, c3, a1, a2, a3, directions);
        Array.Clear(LastTook);
        if (FromFiftyMs) { copies = null; _start = 0; Array.Clear(LastCopies); }
        else if (copies != null)
        {
            copies.TakeFrom(_mOmni, Frames, Frame, SampleRate, LastTook);
            LastCopies = copies.Total();
            _start = Math.Min(copies.FirstArrival, Length - 1);
        }
        else
        {
            Array.Clear(LastCopies);
            int first = 0;
            while (first < w.Length - 1 && w[first] == 0f) first++;
            _start = Math.Min(first, Length - 1);
        }

        // How much this trace counts.
        double weight;
        float moved = Vector3.Distance(at, _where);
        if (_empty || place != _place || moved >= MovedMetres || IsAnotherRoom()) weight = 1.0;
        else
        {
            double t = Math.Clamp((moved - StillMetres) / (MovedMetres - StillMetres), 0.0, 1.0);
            weight = StillWeight + (1.0 - StillWeight) * t;
            if (sceneChanged) weight = Math.Max(weight, SceneWeight);
        }
        if (weight >= 1.0) Resets++;
        LastWeight = weight;

        Blend(_omni, _mOmni, weight);
        Blend(_share, _mShare, weight);
        if (cov != null)
            for (int c = 0; c < _cov.Length && c < cov.Length; c++) _cov[c] += weight * (cov[c] - _cov[c]);
        _where = weight >= 1.0 ? at : Vector3.Lerp(_where, at, (float)weight);
        _place = place;
        _empty = false;
    }

    private static void Blend(double[] avg, double[] m, double weight)
    {
        for (int i = 0; i < avg.Length; i++) avg[i] += weight * (m[i] - avg[i]);
    }

    /// <summary>The new trace's late energy (50 ms to 1 s) against the average's, past <see cref="JumpDb"/>:
    /// the trace is of somewhere else. A backstop for a room change the region does not show.</summary>
    private bool IsAnotherRoom()
    {
        int f0 = (int)(LateTailIr.FadeInStartSeconds * SampleRate) / Frame, f1 = Math.Min(Frames, SampleRate / Frame);
        double a = 0, m = 0;
        for (int b = 0; b < Bands; b++)
            for (int f = f0; f < f1; f++) { a += _omni[b * Frames + f]; m += _mOmni[b * Frames + f]; }
        if (a <= 0 && m <= 0) return false;
        if (a <= 0 || m <= 0) return true;
        return Math.Abs(10 * Math.Log10(m / a)) > JumpDb;
    }

    private static int GroupOf(int band) => band >= DirHighBand ? 1 : 0;

    private int SegmentOf(int sample)
    {
        double t = sample / (double)SampleRate;
        for (int s = 0; s < Segments; s++) if (t < SegmentEdges[s + 1]) return s;
        return Segments - 1;
    }

    /// <summary>One trace's energies into the scratch (_mOmni, _mShare).</summary>
    private void Measure(float[] w, float[]? c1, float[]? c2, float[]? c3, Vector3 a1, Vector3 a2, Vector3 a3, Vector3[]? directions)
    {
        int n = Math.Min(w.Length, Length), nb = Bands;
        Array.Clear(_mOmni);
        Array.Clear(_mShare);

        // The direction each sample of the directional window comes from: the intensity over
        // DoaWindow samples, centred, snapped to the nearest direction. The same rule as SdmTailIr.
        int m = 0, s0 = (int)(SegmentEdges[0] * SampleRate);
        if (directions != null && DirCount > 0 && c1 != null && c2 != null && c3 != null)
        {
            int k = Math.Min(DirCount, directions.Length);
            m = Math.Min(Math.Min(n, _dirLength), Math.Min(c1.Length, Math.Min(c2.Length, c3.Length)));
            int half = SdmTailIr.DoaWindow / 2;
            double iy = 0, iz = 0, ix = 0;
            for (int i = 0; i < Math.Min(half, m); i++) { iy += w[i] * (double)c1[i]; iz += w[i] * (double)c2[i]; ix += w[i] * (double)c3[i]; }
            for (int i = 0; i < m; i++)
            {
                int add = i + half, drop = i - half - 1;
                if (add < m) { iy += w[add] * (double)c1[add]; iz += w[add] * (double)c2[add]; ix += w[add] * (double)c3[add]; }
                if (drop >= 0) { iy -= w[drop] * (double)c1[drop]; iz -= w[drop] * (double)c2[drop]; ix -= w[drop] * (double)c3[drop]; }
                if (i < s0) { _doa[i] = -1; continue; }
                var dir = AmbiAxes.ToGame((float)iy * a1 + (float)iz * a2 + (float)ix * a3);
                int best = 0; float bestDot = float.MinValue;
                if (dir.LengthSquared() > 1e-20f)
                    for (int d = 0; d < k; d++) { float dot = Vector3.Dot(dir, directions[d]); if (dot > bestDot) { bestDot = dot; best = d; } }
                else best = i % k;                    // no direction at all: spread, not piled on one
                _doa[i] = best;
            }
        }

        // Each band's energy per frame; and in the directional window, each band group's energy per
        // time cell, to the direction its sample came from.
        var split = new Splitter(SampleRate);
        Span<double> band = stackalloc double[nb];
        for (int i = 0; i < n; i++)
        {
            split.Run(w[i], band);
            int f = i / Frame;
            for (int b = 0; b < nb; b++) _mOmni[b * Frames + f] += band[b] * band[b];
            if (i < s0 || i >= m) continue;
            int o = SegmentOf(i) * DirCount + _doa[i];
            for (int b = DirFromBand; b < nb; b++) _mShare[GroupOf(b) * Segments * DirCount + o] += band[b] * band[b];
        }
    }

    // ── 3: the responses to play ────────────────────────────────────────────────────────────

    /// <summary>
    /// How many frames a band's energy is smoothed over before it is played: about four periods of
    /// the band's width. A band 90 Hz wide cannot say what its energy is in less than about 45 ms;
    /// measured in 5.8 ms frames it is noise, and that noise imposed on the carrier would be a
    /// granular low end. Odd, centred; one frame from 355 Hz up.
    /// </summary>
    public static int SmoothFrames(int band, int sampleRate)
    {
        double lo = band == 0 ? 0 : EdgesHz[band - 1];
        double hi = band < EdgesHz.Length ? EdgesHz[band] : sampleRate / 2.0;
        double frames = 4.0 / (hi - lo) / ((double)Frame / sampleRate);
        return 2 * (int)(frames / 2) + 1;
    }

    /// <summary>The averaged energy, band <paramref name="band"/>, frame <paramref name="frame"/>.</summary>
    public double OmniEnergy(int band, int frame) => _omni[band * Frames + frame];
    /// <summary>The averaged share of direction <paramref name="dir"/> in band group
    /// <paramref name="group"/>, time cell <paramref name="segment"/>. Shares in a cell sum to one.</summary>
    public double Share(int group, int segment, int dir)
    {
        int o = (group * Segments + segment) * DirCount;
        double sum = 0; for (int d = 0; d < DirCount; d++) sum += _share[o + d];
        return sum > 0 ? _share[o + dir] / sum : 1.0 / Math.Max(1, DirCount);
    }
    /// <summary>The averaged remainder covariance, for TracedReverb's late shares.</summary>
    public double[] Cov => _cov;
    public bool IsEmpty => _empty;

    /// <summary>
    /// The late tail to convolve, windowed. Without a directional part (<paramref name="afterDirectional"/>
    /// false) it is the whole response, from the first reflection (<see cref="Start"/>) on, with the
    /// placed copies' energy already out of it. With one, it takes over from it over SdmTailIr's end
    /// fade, in energy: the two are independent noise, so their windows' squares add to one, where the
    /// old windows (the same samples) added in amplitude.
    /// </summary>
    public float[] LateWindowed(bool afterDirectional)
    {
        bool late = afterDirectional && DirCount > 0;
        var early = EarlyWindow();
        int i0 = late ? (int)(SdmTailIr.EndFadeStart * SampleRate) : early.From;
        int i1 = late ? (int)(SdmTailIr.EndFadeEnd * SampleRate) : early.In;
        var x = new float[Length];
        int f0 = _start / Frame;
        for (int b = 0; b < Bands; b++) Shape(_omniCarrier[b], Smoothed(_omni, b * Frames, Frames, b), x, i0, f0);
        for (int i = i0; i < i1 && i < Length; i++)
        {
            float u = (i - i0) / (float)Math.Max(1, i1 - i0);
            x[i] *= late ? MathF.Sin(0.5f * MathF.PI * u) : FadeIn(i, early);
        }
        return x;
    }

    /// <summary>
    /// The late tail as a field (DiffuseLate): what <see cref="LateWindowed"/> plays after the
    /// directional part, given as an envelope over <paramref name="noise"/>'s fixed per-direction
    /// noise. Per band and per block of DiffuseLateNoise.Block samples from the late part's start, the
    /// amplitude at the block's start and end: the straight line nearest the windowed envelope, then
    /// scaled so the block carries the band's energy exactly. Cut where what remains is 70 dB under
    /// the whole.
    /// </summary>
    public DiffuseLateIr BuildDiffuseLate(DiffuseLateNoise noise)
    {
        int nb = Bands, B = DiffuseLateNoise.Block, start = noise.Start;
        int maxP = Math.Min(noise.Partitions, Math.Max(1, (Length - start + B - 1) / B));
        int i1 = (int)(SdmTailIr.EndFadeEnd * SampleRate);
        double g00 = 0, g01 = 0, g11 = 0;
        for (int t = 0; t < B; t++) { double u = (t + 0.5) / B; g00 += (1 - u) * (1 - u); g01 += (1 - u) * u; g11 += u * u; }
        double det = g00 * g11 - g01 * g01;
        var c0 = new float[nb * maxP]; var c1 = new float[nb * maxP];
        var energy = new double[maxP];
        for (int b = 0; b < nb; b++)
        {
            var e = Smoothed(_omni, b * Frames, Frames, b);
            for (int p = 0; p < maxP; p++)
            {
                double r0 = 0, r1 = 0, tot = 0;
                for (int t = 0; t < B; t++)
                {
                    int i = start + p * B + t;
                    if (i >= Length) break;
                    double a = Amp(e, i);
                    if (i < i1) a *= Math.Sin(0.5 * Math.PI * (i - start) / Math.Max(1, i1 - start));
                    double u = (t + 0.5) / B;
                    r0 += a * (1 - u); r1 += a * u; tot += a * a;
                }
                double x0 = (g11 * r0 - g01 * r1) / det, x1 = (g00 * r1 - g01 * r0) / det;
                if (x0 < 0) { x0 = 0; x1 = Math.Max(0, r1 / g11); }
                else if (x1 < 0) { x1 = 0; x0 = Math.Max(0, r0 / g00); }
                double fit = x0 * x0 * g00 + 2 * x0 * x1 * g01 + x1 * x1 * g11;
                double k = fit > 0 ? Math.Sqrt(tot / fit) : 0;
                c0[b * maxP + p] = (float)(x0 * k); c1[b * maxP + p] = (float)(x1 * k);
                energy[p] += tot;
            }
        }
        double total = 0; foreach (var v in energy) total += v;
        int used = 1; double rest = 0;
        for (int p = maxP - 1; p >= 0 && total > 0; p--) { rest += energy[p]; if (rest > total * 1e-7) { used = p + 1; break; } }
        if (used < maxP)
        {
            var d0 = new float[nb * used]; var d1 = new float[nb * used];
            for (int b = 0; b < nb; b++) { Array.Copy(c0, b * maxP, d0, b * used, used); Array.Copy(c1, b * maxP, d1, b * used, used); }
            c0 = d0; c1 = d1;
        }
        return new DiffuseLateIr(noise, used, c0, c1, total);
    }

    /// <summary>One band's amplitude per sample at sample <paramref name="i"/>, as <see cref="Shape"/>
    /// lays it: sqrt(energy per sample) at frame centres, straight lines between.</summary>
    private static double Amp(double[] energy, int i)
    {
        const int half = Frame / 2;
        int frames = energy.Length;
        int f = i < half ? -1 : (i - half) / Frame;
        double t = (i - (f * Frame + half)) / (double)Frame;
        double ga = Math.Sqrt(Math.Max(0.0, energy[Math.Clamp(f, 0, frames - 1)]) / Frame);
        double gb = Math.Sqrt(Math.Max(0.0, energy[Math.Clamp(f + 1, 0, frames - 1)]) / Frame);
        return ga + (gb - ga) * t;
    }

    /// <summary>The late tail to convolve, in partitions; see <see cref="LateWindowed"/>.</summary>
    public LateTailIr BuildLate(int block, int maxPartitions, bool afterDirectional)
        => LateTailIr.FromWindowed(LateWindowed(afterDirectional), block, maxPartitions);

    /// <summary>
    /// The directional part's windowed responses, one per direction, each on its own noise: per band,
    /// the band's averaged energy times the direction's averaged share (straight lines between the
    /// time cells' middles); below <see cref="DirFromBand"/> an even share. From the first reflection
    /// (<see cref="Start"/>), out over SdmTailIr's end fade.
    /// </summary>
    public float[][] DirectionalWindowed(int length)
    {
        int nb = Bands, n = Math.Min(length, _dirLength);
        var early = EarlyWindow();
        int s0 = early.From, f0 = _start / Frame;
        int e0 = (int)(SdmTailIr.EndFadeStart * SampleRate), e1 = (int)(SdmTailIr.EndFadeEnd * SampleRate);
        // Each frame's place between the time cells' middles: the cell before it and how far on.
        var cell = new int[DirFrames]; var along = new double[DirFrames];
        var mid = new double[Segments];
        for (int s = 0; s < Segments; s++) mid[s] = (SegmentEdges[s] + SegmentEdges[s + 1]) / 2 * SampleRate / Frame - 0.5;
        for (int f = 0; f < DirFrames; f++)
        {
            int s = 0; while (s < Segments - 2 && mid[s + 1] <= f) s++;
            cell[f] = s;
            along[f] = Math.Clamp((f - mid[s]) / (mid[s + 1] - mid[s]), 0.0, 1.0);
        }
        var omni = new double[nb][];
        for (int b = 0; b < nb; b++)
        {
            var full = Smoothed(_omni, b * Frames, Frames, b);
            omni[b] = new double[DirFrames];
            Array.Copy(full, omni[b], Math.Min(DirFrames, full.Length));
        }
        var shares = new double[Groups * Segments * DirCount];
        for (int g = 0; g < Groups; g++)
            for (int s = 0; s < Segments; s++)
                for (int d = 0; d < DirCount; d++) shares[(g * Segments + s) * DirCount + d] = Share(g, s, d);
        var env = new double[DirFrames];
        var parts = new float[DirCount][];
        for (int d = 0; d < DirCount; d++)
        {
            var x = new float[n];
            for (int b = 0; b < nb; b++)
            {
                int o = GroupOf(b) * Segments * DirCount + d;
                for (int f = 0; f < DirFrames; f++)
                {
                    double sh = b < DirFromBand ? 1.0 / DirCount
                              : shares[o + cell[f] * DirCount] * (1 - along[f]) + shares[o + (cell[f] + 1) * DirCount] * along[f];
                    env[f] = omni[b][f] * sh;
                }
                Shape(_dirCarrier[d * nb + b], env, x, s0, f0);
            }
            for (int i = 0; i < n; i++)
            {
                // From the first reflection, at once: the room answers when its nearest surface does.
                float win;
                if (i < s0 || i >= e1) win = 0f;
                else win = FadeIn(i, early) * (i >= e0 ? MathF.Cos(0.5f * MathF.PI * (i - e0) / Math.Max(1, e1 - e0)) : 1f);
                x[i] *= win;
            }
            parts[d] = x;
        }
        return parts;
    }

    /// <summary>The directional part to convolve (SdmTailIr), from the averaged energies.</summary>
    public SdmTailIr BuildDirectional(int block)
    {
        int maxP = SdmTailIr.PartitionsFor(SampleRate, block);
        return SdmTailIr.FromParts(DirectionalWindowed(maxP * block), block, maxP);
    }

    /// <summary>One band's frame energies, smoothed over <see cref="SmoothFrames"/> (a centred box,
    /// so the total is kept; at the ends, the mean of what there is). Nothing before the first
    /// reflection's frame: the smoothing does not reach back past it, or a low band would answer
    /// before any surface could.</summary>
    private double[] Smoothed(double[] energy, int offset, int frames, int band)
    {
        int k = SmoothFrames(band, SampleRate) / 2;
        int first = Math.Min(frames - 1, _start / Frame);
        var y = new double[frames];
        for (int f = first; f < frames; f++)
        {
            double s = 0; int c = 0;
            for (int j = Math.Max(first, f - k); j <= Math.Min(frames - 1, f + k); j++) { s += energy[offset + j]; c++; }
            y[f] = s / c;
        }
        return y;
    }

    /// <summary>Adds carrier times sqrt(energy per sample), the energy at frame centres and straight
    /// lines between, into <paramref name="x"/> from sample <paramref name="from"/> on. Before the
    /// centre of frame <paramref name="first"/> (the first reflection's) it holds that frame's level:
    /// the response starts at once, not on a ramp up from the silence before it.</summary>
    private static void Shape(float[] carrier, double[] energy, float[] x, int from, int first = 0)
    {
        int n = Math.Min(x.Length, carrier.Length), frames = energy.Length;
        const int half = Frame / 2;
        first = Math.Clamp(first, 0, frames - 1);
        float Amp(int f) => (float)Math.Sqrt(Math.Max(0.0, energy[Math.Clamp(f, first, frames - 1)]) / Frame);
        for (int i = Math.Max(0, from); i < n;)
        {
            // Between the centres of frames f and f + 1. Before the first centre and after the last,
            // the clamp makes the two ends equal: flat.
            int f = i < half ? -1 : (i - half) / Frame;
            int start = f * Frame + half, end = Math.Min(n, start + Frame);
            float ga = Amp(f), gb = Amp(f + 1);
            for (; i < end; i++)
            {
                float t = (i - start) / (float)Frame;
                x[i] += carrier[i] * (ga + (gb - ga) * t);
            }
        }
    }
}
