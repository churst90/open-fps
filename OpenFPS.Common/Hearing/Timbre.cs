using System;
using System.Collections.Concurrent;

namespace OpenFPS.Common.Hearing;

/// <summary>
/// The shape of a sound's spectrum, 28 one-third-octave bands from 25 Hz to 12.5 kHz in dB relative
/// to its overall level, and what that shape makes of a level: its loudness (ISO 532-1).
///
/// Measured from the sound itself (<see cref="BandAnalyser"/>), never authored. Immutable, so one can be
/// shared by every voice of the same sound and read from any thread; its answers are cached.
///
/// <see cref="Speech"/> is the reference sound the loudness law is conjugated through (docs/EAR_MODEL.md):
/// ANSI S3.5-1997 table 3, normal vocal effort.
/// </summary>
public sealed class Timbre
{
    public const int Bands = ZwickerLoudness.BandCount;

    private readonly float[] _shape;
    public string Name { get; }

    /// <summary>The band levels relative to the overall level, dB (they sum in power to 0 dB).</summary>
    public ReadOnlySpan<float> ShapeDb => _shape;

    private Timbre(float[] shape, string name, bool live)
    {
        _shape = shape;
        Name = name;
        Live = live;
    }

    /// <summary>A live voice's measurement, replaced every fraction of a second: its answers are not
    /// worth caching, and it carries no cache.</summary>
    public bool Live { get; }

    /// <summary>
    /// For a recording or a rendered buffer: where the sound sits under its buffer's full scale, dBFS,
    /// as a gated RMS (the mean square of its 125 ms blocks within 20 dB of the loudest: the sound while
    /// it sounds, its pauses and its silent tail left out). A world sound declares its level as its
    /// buffer's full scale at a metre (Speech.LevelDb), so its real level is the declared level plus
    /// this. NaN for a live voice, whose declared level is its RMS level and which plays it
    /// <see cref="Loudness.PhysicalRmsDbfs"/> under full scale.
    /// </summary>
    public float GatedRmsDbfs { get; private init; } = float.NaN;

    /// <summary>This shape, for a buffer whose gated RMS is <paramref name="gatedRmsDbfs"/>.</summary>
    public Timbre WithGatedRms(float gatedRmsDbfs) => new(_shape, Name, Live) { GatedRmsDbfs = gatedRmsDbfs };

    /// <summary>How far the real level is over the declared one, dB: the gated RMS for a buffer, 0 for a
    /// live voice (its declared level is its RMS).</summary>
    public float RealOffsetDb => float.IsFinite(GatedRmsDbfs) ? GatedRmsDbfs : 0f;

    /// <summary>Where the voice's RMS sits under its digital full scale when played at unit gain, dB.</summary>
    public float DigitalRmsDb => float.IsFinite(GatedRmsDbfs) ? GatedRmsDbfs : Loudness.PhysicalRmsDbfs;

    /// <summary>
    /// The gated RMS of a buffer, dBFS: the mean square of its 125 ms blocks within 20 dB of its loudest
    /// block. NaN for silence.
    /// </summary>
    public static float GatedRms(ReadOnlySpan<float> pcm, int sampleRate)
    {
        int block = Math.Max(1, sampleRate / 8);
        int n = pcm.Length / block + (pcm.Length % block > block / 4 ? 1 : 0);
        if (n == 0) n = 1;
        Span<double> ms = n <= 4096 ? stackalloc double[n] : new double[n];
        double loudest = 0;
        for (int b = 0; b < n; b++)
        {
            int from = b * block, to = Math.Min(pcm.Length, from + block);
            double s = 0;
            for (int i = from; i < to; i++) s += (double)pcm[i] * pcm[i];
            ms[b] = to > from ? s / (to - from) : 0;
            loudest = Math.Max(loudest, ms[b]);
        }
        if (!(loudest > 0)) return float.NaN;
        double gate = loudest * 0.01, sum = 0;
        int count = 0;
        foreach (double v in ms) if (v >= gate) { sum += v; count++; }
        return (float)(10.0 * Math.Log10(sum / Math.Max(1, count)));
    }

    /// <summary>A shape from band powers in any units. Null when there is nothing in it.</summary>
    public static Timbre? FromBandPowers(ReadOnlySpan<double> powers, string name = "", bool live = false)
    {
        if (powers.Length != Bands) throw new ArgumentException($"{Bands} bands expected", nameof(powers));
        double total = 0;
        foreach (double p in powers) if (p > 0 && double.IsFinite(p)) total += p;
        if (!(total > 0)) return null;
        var shape = new float[Bands];
        for (int b = 0; b < Bands; b++)
        {
            double p = powers[b] > 0 && double.IsFinite(powers[b]) ? powers[b] : 0;
            shape[b] = (float)Math.Max(-120.0, 10.0 * Math.Log10(p / total + 1e-30));
        }
        return new Timbre(shape, name, live);
    }

    /// <summary>A shape from band levels, dB (any overall level; it is normalised).</summary>
    public static Timbre FromBandLevels(ReadOnlySpan<float> levelsDb, string name = "")
    {
        Span<double> p = stackalloc double[Bands];
        for (int b = 0; b < Bands; b++) p[b] = Math.Pow(10.0, levelsDb[b] / 10.0);
        return FromBandPowers(p, name)!;
    }

    // ── The reference sound ─────────────────────────────────────────────────────────────────────

    /// <summary>ANSI S3.5-1997 table 3, the standard speech spectrum level at normal effort, dB per Hz,
    /// for the bands 160 Hz to 8 kHz. 62.35 dB overall at a metre.</summary>
    private static readonly float[] AnsiNormalSpectrumLevel =
    {
        32.41f, 34.48f, 34.75f, 33.98f, 34.59f, 34.27f, 32.06f, 28.30f, 25.01f,
        23.00f, 20.15f, 17.32f, 13.18f, 11.55f, 9.33f, 5.31f, 2.59f, 1.13f,
    };

    /// <summary>
    /// Speech at normal effort, the loudness law's reference sound. Table 3 covers 160 Hz to 8 kHz; the
    /// bands either side fall away at 12 dB an octave (4 dB a band), which puts nothing there that
    /// matters to its loudness.
    /// </summary>
    public static Timbre Speech { get; } = BuildSpeech();

    private static Timbre BuildSpeech()
    {
        var levels = new float[Bands];
        var fc = ZwickerLoudness.CentresHz;
        for (int i = 0; i < AnsiNormalSpectrumLevel.Length; i++)
        {
            float f = fc[i + 8];
            float bandwidth = f * (MathF.Pow(2f, 1f / 6f) - MathF.Pow(2f, -1f / 6f));
            levels[i + 8] = AnsiNormalSpectrumLevel[i] + 10f * MathF.Log10(bandwidth);
        }
        for (int i = 7; i >= 0; i--) levels[i] = levels[i + 1] - 4f;
        for (int i = 26; i < Bands; i++) levels[i] = levels[i - 1] - 4f;
        // A speech line in the game sits 28 dB under its full scale (Speech.BufferRmsDbfs).
        var t = FromBandLevels(levels, "speech (ANSI S3.5 normal)").WithGatedRms(Loudness.ReferenceRmsDbfs);
        t.BuildTable();
        return t;
    }

    public bool IsReference => ReferenceEquals(this, Speech);

    // ── Loudness at a level ─────────────────────────────────────────────────────────────────────

    /// <summary>Loudness, sone, of this sound at <paramref name="levelDb"/> dB SPL overall, free field.</summary>
    public float Sones(float levelDb)
    {
        Span<float> bands = stackalloc float[Bands];
        for (int b = 0; b < Bands; b++) bands[b] = Math.Clamp(_shape[b] + levelDb, -100f, 160f);
        return ZwickerLoudness.Sones(bands);
    }

    /// <summary>Loudness level, phon, of this sound at <paramref name="levelDb"/>.</summary>
    public float Phons(float levelDb)
    {
        if (_table != null) return TableLookup(levelDb);
        return ZwickerLoudness.Phons(Sones(levelDb));
    }

    /// <summary>The loudness level below which this method calls a sound inaudible (zero sone).</summary>
    public static readonly float SilentPhons = ZwickerLoudness.Phons(0f);

    /// <summary>
    /// The level at which this sound has loudness level <paramref name="phons"/>: the smallest level that
    /// reaches it. Loudness only grows with level, so this is a bracketed search.
    /// </summary>
    public float LevelForPhons(float phons)
    {
        if (_table != null) return TableInverse(phons);
        float lo = -40f, hi = 180f;
        // A good first bracket: loudness level grows about as fast as level.
        float guess = phons + (phons - Phons(phons));
        float a = Math.Clamp(guess - 6f, lo, hi), b = Math.Clamp(guess + 6f, lo, hi);
        float pa = Phons(a), pb = Phons(b);
        int widen = 0;
        while (pa > phons && a > lo && widen++ < 12) { a = MathF.Max(lo, a - 12f); pa = Phons(a); }
        widen = 0;
        while (pb < phons && b < hi && widen++ < 12) { b = MathF.Min(hi, b + 12f); pb = Phons(b); }
        if (pa > phons) return a;
        if (pb < phons) return b;
        // Illinois false position: a few evaluations to a hundredth of a phon.
        int side = 0;
        for (int i = 0; i < 24 && b - a > 0.005f; i++)
        {
            float m = pb - pa > 1e-6f ? a + (phons - pa) * (b - a) / (pb - pa) : 0.5f * (a + b);
            m = Math.Clamp(m, a, b);
            float pm = Phons(m);
            if (MathF.Abs(pm - phons) < 0.01f) return m;
            if (pm < phons)
            {
                a = m; pa = pm;
                if (side == -1) pb = phons + (pb - phons) * 0.5f;
                side = -1;
            }
            else
            {
                b = m; pb = pm;
                if (side == 1) pa = phons + (pa - phons) * 0.5f;
                side = 1;
            }
        }
        return 0.5f * (a + b);
    }

    // ── A table, for a sound asked about often (the reference) ──────────────────────────────────

    private const float TableLow = -30f, TableStep = 0.25f;
    private const int TableCount = 761;   // -30 to 160 dB
    private float[]? _table;

    private void BuildTable()
    {
        var t = new float[TableCount];
        for (int i = 0; i < TableCount; i++) t[i] = ZwickerLoudness.Phons(Sones(TableLow + i * TableStep));
        // Loudness never falls with level; make the table say so exactly, for the inverse.
        for (int i = 1; i < TableCount; i++) t[i] = MathF.Max(t[i], t[i - 1]);
        _table = t;
    }

    private float TableLookup(float levelDb)
    {
        var t = _table!;
        float x = (levelDb - TableLow) / TableStep;
        if (x <= 0) return t[0];
        if (x >= TableCount - 1)
            return t[^1] + (levelDb - (TableLow + (TableCount - 1) * TableStep)) * (t[^1] - t[^5]) / (4 * TableStep);
        int i = (int)x;
        float f = x - i;
        return t[i] + (t[i + 1] - t[i]) * f;
    }

    private float TableInverse(float phons)
    {
        var t = _table!;
        if (phons <= t[0]) return TableLow;
        if (phons >= t[^1])
        {
            float slope = (t[^1] - t[^5]) / (4 * TableStep);
            return TableLow + (TableCount - 1) * TableStep + (phons - t[^1]) / MathF.Max(0.1f, slope);
        }
        int lo = 0, hi = TableCount - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (t[mid] < phons) lo = mid; else hi = mid;
        }
        float span = t[hi] - t[lo];
        float f = span > 1e-6f ? (phons - t[lo]) / span : 0f;
        return TableLow + (lo + f) * TableStep;
    }

    // ── The law's answers, cached per level and compression ─────────────────────────────────────

    // Made on first use: a live voice's timbre is replaced every quarter second and is asked once or
    // twice, so it should not carry a dictionary it never fills.
    private ConcurrentDictionary<long, (float Gain, float Reference)>? _placed;

    internal (float Gain, float Reference) Placed(float levelDb, float compression, Func<float, Timbre, float, (float, float)> compute)
    {
        if (Live) return compute(levelDb, this, compression);
        var cache = _placed ?? System.Threading.Interlocked.CompareExchange(ref _placed, new(), null) ?? _placed!;
        long key = ((long)MathF.Round(levelDb * 20f) << 20) | (long)MathF.Round(compression * 10000f);
        if (cache.TryGetValue(key, out var hit)) return hit;
        var value = compute(MathF.Round(levelDb * 20f) / 20f, this, compression);
        if (cache.Count > 8192) cache.Clear();
        cache[key] = value;
        return value;
    }

    public override string ToString() => string.IsNullOrEmpty(Name) ? "timbre" : Name;
}
