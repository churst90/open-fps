using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// Where a stream's stones lie along its bed, and how steep each part of it is (docs/RUNNING_WATER.md 15.6).
///
/// A natural bed is not the same all along. A gentle one (under about 2 %) gathers its coarse stones into riffles
/// between pools, a riffle and a pool every five to seven widths of the channel (Leopold, Wolman and Miller 1964,
/// "Fluvial Processes in Geomorphology", ch. 7; Keller and Melhorn 1978, GSA Bulletin 89, 723); a steep one (over
/// about 3 %) into steps of stones a pool apart every one to four widths; between the two the bed is plane
/// (Montgomery and Buffington 1997, GSA Bulletin 109(5), 596). The water's surface falls over the riffle or the step
/// and lies nearly flat over the pool, so the stones stand in shallow fast water there and the pool runs deep, slow
/// and quiet. Over a whole unit the bed has the stones and the fall the reach has. A gutter, a ditch, a rill and a
/// plane bed are even all along.
///
/// Every stone is where it is: its place and size come from a hash of the bed's seed and its number, so the stretch
/// beside you has the same stones however the line is cut and recut as you walk. Distances are from the line's first
/// end, its upstream one.
/// </summary>
public sealed class StreamBed
{
    /// <summary>A gentle bed: each unit's riffle is this share of it, and holds this share of the unit's stones and of
    /// its fall. Riffles are commonly about as long as their pools or a little shorter; the stones and the fall are
    /// gathered into them. Judgements within those observations.</summary>
    public const float RiffleShare = 0.4f, RiffleStones = 0.8f, RiffleFall = 0.7f;

    /// <summary>A steep bed: each unit's step is this share of it, and holds this share of its stones and fall.</summary>
    public const float StepShare = 0.25f, StepStones = 0.85f, StepFall = 0.85f;

    /// <summary>An even bed is enumerated in sections this long: only the stones near a point are ever listed.</summary>
    private const float EvenSectionMetres = 8f;

    public readonly float Length;
    public readonly float PerMetre;
    public readonly BedForm Form;

    private readonly ulong _seed;
    // Each section's start (one more: the line's end), stones per metre, slope against the reach's, and whether it
    // is a riffle or a step.
    private readonly float[] _starts;
    private readonly float[] _density;
    private readonly float[] _slope;
    private readonly bool[] _rough;

    public StreamBed(RunningWaterSpec spec, int seed)
    {
        Length = MathF.Max(0.5f, spec.LengthMetres);
        PerMetre = MathF.Max(0f, spec.Obstacles?.PerMetre ?? 0f);
        Form = spec.Bed;
        _seed = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;
        var starts = new List<float>();
        var density = new List<float>();
        var slope = new List<float>();
        var rough = new List<bool>();
        if (Form == BedForm.Even)
        {
            for (float at = 0f; at < Length; at += EvenSectionMetres)
            {
                starts.Add(at); density.Add(PerMetre); slope.Add(1f); rough.Add(false);
            }
        }
        else
        {
            bool steps = Form == BedForm.StepPool;
            float w = MathF.Max(0.3f, spec.WidthMetres);
            float share = steps ? StepShare : RiffleShare;
            float stones = steps ? StepStones : RiffleStones, fall = steps ? StepFall : RiffleFall;
            float roughDensity = PerMetre * stones / share, poolDensity = PerMetre * (1f - stones) / (1f - share);
            float roughSlope = fall / share, poolSlope = (1f - fall) / (1f - share);
            // Each unit five to seven widths (one to four for steps), the line's first end anywhere in the first.
            float Unit(int u) => w * (steps ? 1f + 3f * Uniform(0xB0D1u, u) : 5f + 2f * Uniform(0xB0D1u, u));
            float at = -Uniform(0xF1A5u, 0) * Unit(0);
            for (int u = 0; at < Length; u++)
            {
                float unit = Unit(u), split = at + share * unit;
                foreach (var (from, rougher) in new[] { (at, true), (split, false) })
                {
                    float to = rougher ? split : at + unit;
                    if (to <= 0f || from >= Length) continue;
                    starts.Add(MathF.Max(0f, from));
                    density.Add(rougher ? roughDensity : poolDensity);
                    slope.Add(rougher ? roughSlope : poolSlope);
                    rough.Add(rougher);
                }
                at += unit;
            }
        }
        if (starts.Count == 0) { starts.Add(0f); density.Add(PerMetre); slope.Add(1f); rough.Add(false); }
        starts.Add(Length);
        _starts = starts.ToArray();
        _density = density.ToArray();
        _slope = slope.ToArray();
        _rough = rough.ToArray();
    }

    public int Sections => _density.Length;

    /// <summary>The section holding the point <paramref name="s"/> m from the line's first end.</summary>
    public int SectionAt(float s)
    {
        int lo = 0, hi = Sections - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (s < _starts[mid + 1]) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>How steep the water is at <paramref name="s"/> against the reach's mean slope.</summary>
    public float SlopeAt(float s) => _slope[SectionAt(Math.Clamp(s, 0f, Length))];

    /// <summary>Stones per metre at <paramref name="s"/>.</summary>
    public float DensityAt(float s) => _density[SectionAt(Math.Clamp(s, 0f, Length))];

    /// <summary>Whether <paramref name="s"/> is in a riffle or on a step.</summary>
    public bool RoughAt(float s) => _rough[SectionAt(Math.Clamp(s, 0f, Length))];

    /// <summary>How many stones the bed holds between <paramref name="a"/> and <paramref name="b"/> on the mean, m from
    /// its first end.</summary>
    public double CountBetween(float a, float b)
    {
        a = Math.Clamp(a, 0f, Length);
        b = Math.Clamp(b, 0f, Length);
        if (b <= a) return 0;
        double n = 0;
        for (int k = SectionAt(a); k < Sections && _starts[k] < b; k++)
            n += _density[k] * (double)(MathF.Min(b, _starts[k + 1]) - MathF.Max(a, _starts[k]));
        return n;
    }

    /// <summary>The point where the bed's stones from <paramref name="a"/> on reach <paramref name="count"/>, held at
    /// <paramref name="b"/>.</summary>
    public float PointAfter(float a, double count, float b)
    {
        a = Math.Clamp(a, 0f, Length);
        b = Math.Clamp(b, a, Length);
        double left = count;
        for (int k = SectionAt(a); k < Sections && _starts[k] < b; k++)
        {
            float from = MathF.Max(a, _starts[k]), to = MathF.Min(b, _starts[k + 1]);
            double here = _density[k] * (double)(to - from);
            if (here >= left && _density[k] > 0f) return from + (float)(left / _density[k]);
            left -= here;
        }
        return b;
    }

    /// <summary>A stone of the bed: where it is (m from the first end), its number, and two draws of its own in [0, 1)
    /// for its size and its width.</summary>
    public readonly record struct Stone(float At, long Id, float SizeDraw, float WidthDraw);

    /// <summary>
    /// Every stone between <paramref name="a"/> and <paramref name="b"/>: in each section its mean count rounded by a
    /// draw of its own, spread through it with a jitter of their own. The same stones whatever the span asked for.
    /// </summary>
    public void StonesBetween(float a, float b, List<Stone> into)
    {
        a = Math.Clamp(a, 0f, Length);
        b = Math.Clamp(b, 0f, Length);
        if (b <= a) return;
        for (int k = SectionAt(a); k < Sections && _starts[k] < b; k++)
        {
            float from = _starts[k], len = _starts[k + 1] - from;
            int n = (int)MathF.Floor(_density[k] * len + Uniform(0x5701u, k));
            for (int j = 0; j < n; j++)
            {
                long id = (long)k * 65536 + j;
                float at = from + len * (j + 0.5f + 0.8f * (Uniform(0xA7u, id) - 0.5f)) / n;
                if (at < a || at >= b) continue;
                into.Add(new Stone(at, id, Uniform(0x512Eu, id), Uniform(0x3D7Au, id)));
            }
        }
    }

    /// <summary>A draw in [0, 1) of its own for a thing of this bed (splitmix64).</summary>
    private float Uniform(ulong what, long n)
    {
        ulong z = _seed ^ (what * 0xD1B54A32D192ED03UL) ^ ((ulong)n * 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 40) / (float)(1UL << 24);
    }
}
