
namespace OpenFPS.Sound.Core.Nature;

/// <summary>A strip of a bed across its width: its share of the width and of the flow, how deep and fast its water is
/// against the channel's mean, and how big its stones are against the bed's (BedSection).</summary>
public readonly record struct BedStrip(float WidthShare, float FlowShare, float DepthRatio, float SpeedRatio, float SizeRatio)
{
    /// <summary>The whole bed as one strip: a channel heard without its banks apart.</summary>
    public static readonly BedStrip Whole = new(1f, 1f, 1f, 1f, 1f);
    public bool IsWhole => this == Whole;
}

/// <summary>
/// A wide natural bed across itself (docs/RUNNING_WATER.md 15.10): its middle and its two banks, each a strip of the same
/// water. Running water's hydraulics take a channel as a rectangle at its mean depth and speed; a natural channel is
/// deepest in its middle and shoals to its banks, so the water there is shallower, slower and carries less, over smaller
/// stones. Each bank is heard as its own flow.
///
/// The section is a power law, depth h(x) = H (1 - |2x / W|^r), with r = 2, a parabola: Dingman (2007, "Analytical
/// derivation of at-a-station hydraulic-geometry relations", J. Hydrol. 334, 17-27) writes natural sections this way and
/// finds r of about 1 to 4, 2 the common middle. The speed down each vertical is Manning's law on its own depth, u =
/// h^(2/3) S^(1/2) / n, so the flow across it goes as h^(5/3): the lateral distribution of depth-averaged velocity without
/// its lateral shear, which Shiono and Knight (1991, J. Fluid Mech. 222, 617-646) add and which slows the banks further.
/// A bank is rougher than the bed by its vegetation: Cowan's (1956) adjustment for a small degree of it, 0.002 to 0.010,
/// added to n (Arcement and Schneider 1989, USGS Water-Supply Paper 2339, table 3), the middle of it taken.
///
/// Each bank is the outer fifth of the width (as Resonance's 2.2 has it), where the water is under half the mean depth.
/// With r = 2 and a cobble bed's n of 0.045, a bank carries 6 % of the flow at 0.52 of the mean depth and 0.60 of the
/// mean speed; the middle three fifths 87 %, 1.32 times as deep and 1.10 times as fast. The stones are sorted by the water
/// that laid them: the largest a flow moves goes as its bed shear, rho g h S (Shields 1936), so at one slope a strip's
/// stones go as its depth, the share of it a bed's stones stand (GroundChannels.ObstacleShareOfDepth) kept; and they lie
/// as densely as the bed's do by the cover they make (a metre of a strip holds as many as its width over their size), the
/// bed's count shared between the strips so the whole bed keeps the stones it had.
/// </summary>
public static class BedSection
{
    /// <summary>Each bank's share of the wetted width.</summary>
    public const float BankShare = 0.2f;

    /// <summary>The section's exponent r (Dingman 2007): 2, a parabola.</summary>
    public const float ShapeExponent = 2f;

    /// <summary>What a bank's vegetation adds to Manning's n: Cowan's small degree, 0.002 to 0.010, its middle.</summary>
    public const float BankVegetationN = 0.006f;

    /// <summary>The lanes of a banked bed: its middle, the bank on the +across side of its line (LinePath.AcrossOf: the left
    /// bank of a line laid downstream), the one on the other, and a stand-in for both banks at once (far off, where neither
    /// is heard apart).</summary>
    public const int Middle = 0, LeftBank = 1, RightBank = 2, BothBanks = 3;

    /// <summary>Whether a bed is heard as its middle and two banks: a natural stream bed at least
    /// <see cref="LinePath.WideBedMetres"/> wide, whose synth takes its sites from its bed's stones (RunningWaterSynth.FromTheBed).</summary>
    public static bool Banked(RunningWaterSpec spec)
        => spec.Channel == FlowChannel.Stream && spec.WidthMetres >= LinePath.WideBedMetres && RunningWaterSynth.FromTheBed(spec);

    /// <summary>The middle strip and one bank of a bed of Manning's <paramref name="manningN"/>, by the section above.</summary>
    public static (BedStrip Middle, BedStrip Bank) Strips(float manningN)
    {
        double n = Math.Max(0.008, manningN), edge = 1.0 - 2.0 * BankShare;
        const int Steps = 400;
        // Over half the section, x from the middle (0) to a bank (1): depth, flow, in the middle strip and in the bank.
        double depthMid = 0, depthBank = 0, flowMid = 0, flowBank = 0;
        for (int i = 0; i < Steps; i++)
        {
            double x = (i + 0.5) / Steps, h = 1.0 - Math.Pow(x, ShapeExponent);
            bool bank = x >= edge;
            double q = Math.Pow(h, 5.0 / 3.0) / (bank ? n + BankVegetationN : n);
            if (bank) { depthBank += h; flowBank += q; } else { depthMid += h; flowMid += q; }
        }
        // Means over each part of the half: the middle is `edge` of it, a bank the rest.
        double mean = (depthMid + depthBank) / Steps, flow = flowMid + flowBank;
        double hMid = depthMid / Steps / edge, hBank = depthBank / Steps / (1.0 - edge);
        // A strip's speed against the mean: its share of the flow over its share of the width and of the depth.
        BedStrip Of(double width, double share, double depth)
            => new((float)width, (float)share, (float)(depth / mean), (float)(share / width / (depth / mean)), (float)(depth / mean));
        // The middle is both halves' middles; a bank one half's outer part, so half of the bed's bank water.
        return (Of(edge, flowMid / flow, hMid), Of(BankShare, 0.5 * flowBank / flow, hBank));
    }

    /// <summary>The strip a lane's water is (<see cref="BothBanks"/> one bank's, as each of its stones stands in one).</summary>
    public static BedStrip Of(int lane, (BedStrip Middle, BedStrip Bank) strips) => lane == Middle ? strips.Middle : strips.Bank;
}

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

    /// <summary>How many lanes across it its stones lie in: one, or a wide natural bed's middle and its two banks
    /// (BedSection, its lanes numbered as BedSection.Middle, LeftBank and RightBank).</summary>
    public readonly int Lanes = 1;

    /// <summary>A banked bed's middle strip and one bank (BedSection.Strips); the whole bed for one lane.</summary>
    public readonly (BedStrip Middle, BedStrip Bank) Strips = (BedStrip.Whole, BedStrip.Whole);

    /// <summary>The share of the bed's stones each lane holds: as many as its width over the size of its stones, the
    /// bed's count shared out. One lane holds them all.</summary>
    private readonly float[] _laneShare = { 1f };

    private readonly ulong _seed;
    // Each section's start (one more: the line's end), stones per metre, slope against the reach's, and whether it
    // is a riffle or a step.
    private readonly float[] _starts;
    private readonly float[] _density;
    private readonly float[] _slope;
    private readonly bool[] _rough;

    public StreamBed(RunningWaterSpec spec, int seed, bool banksApart = true)
    {
        Length = MathF.Max(0.5f, spec.LengthMetres);
        PerMetre = MathF.Max(0f, spec.Obstacles?.PerMetre ?? 0f);
        Form = spec.Bed;
        _seed = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;
        if (banksApart && BedSection.Banked(spec))
        {
            Lanes = 3;
            Strips = BedSection.Strips(spec.ManningN);
            float mid = Strips.Middle.WidthShare / Strips.Middle.SizeRatio, bank = Strips.Bank.WidthShare / Strips.Bank.SizeRatio;
            float all = mid + 2f * bank;
            _laneShare = new[] { mid / all, bank / all, bank / all };
        }
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

    /// <summary>The share of the bed's stones lane <paramref name="lane"/> holds (both banks' for BedSection.BothBanks).</summary>
    public float LaneShare(int lane)
        => lane == BedSection.BothBanks ? (Lanes > 1 ? _laneShare[1] + _laneShare[2] : 0f) : lane >= 0 && lane < Lanes ? _laneShare[lane] : 0f;

    /// <summary>How many stones the bed holds between <paramref name="a"/> and <paramref name="b"/> on the mean, m from
    /// its first end: in one lane, or in all of them (a negative lane).</summary>
    public double CountBetween(float a, float b, int lane = -1)
    {
        a = Math.Clamp(a, 0f, Length);
        b = Math.Clamp(b, 0f, Length);
        if (b <= a) return 0;
        double n = 0;
        for (int k = SectionAt(a); k < Sections && _starts[k] < b; k++)
            n += _density[k] * (double)(MathF.Min(b, _starts[k + 1]) - MathF.Max(a, _starts[k]));
        return lane < 0 ? n : n * LaneShare(lane);
    }

    /// <summary>The point where the stones of lane <paramref name="lane"/> (all of them for a negative lane) from
    /// <paramref name="a"/> on reach <paramref name="count"/>, held at <paramref name="b"/>. Every lane lies along the bed
    /// as its sections do, so it is the whole bed's point for the count it stands for.</summary>
    public float PointAfter(float a, double count, float b, int lane = -1)
    {
        a = Math.Clamp(a, 0f, Length);
        b = Math.Clamp(b, a, Length);
        double left = lane < 0 ? count : count / Math.Max(1e-6, LaneShare(lane));
        for (int k = SectionAt(a); k < Sections && _starts[k] < b; k++)
        {
            float from = MathF.Max(a, _starts[k]), to = MathF.Min(b, _starts[k + 1]);
            double here = _density[k] * (double)(to - from);
            if (here >= left && _density[k] > 0f) return from + (float)(left / _density[k]);
            left -= here;
        }
        return b;
    }

    /// <summary>A stone of the bed: where it is (m from the first end), its number, two draws of its own in [0, 1) for its
    /// size and its width, and the lane across the bed it lies in.</summary>
    public readonly record struct Stone(float At, long Id, float SizeDraw, float WidthDraw, int Lane = 0);

    /// <summary>
    /// Every stone of lane <paramref name="lane"/> between <paramref name="a"/> and <paramref name="b"/>: in each section
    /// its mean count rounded by a draw of its own, spread through it with a jitter of their own. The same stones whatever
    /// the span asked for. A bed of one lane has the stones it always had; a bank's are numbered from 2^40 times its lane.
    /// </summary>
    public void StonesBetween(float a, float b, List<Stone> into, int lane = 0)
    {
        a = Math.Clamp(a, 0f, Length);
        b = Math.Clamp(b, 0f, Length);
        if (b <= a || lane < 0 || lane >= Lanes) return;
        float share = _laneShare[lane];
        long offset = (long)lane << 40;
        for (int k = SectionAt(a); k < Sections && _starts[k] < b; k++)
        {
            float from = _starts[k], len = _starts[k + 1] - from;
            int n = (int)MathF.Floor(_density[k] * share * len + Uniform(0x5701u + (ulong)lane * 0x10000u, k));
            for (int j = 0; j < n; j++)
            {
                long id = offset + (long)k * 65536 + j;
                float at = from + len * (j + 0.5f + 0.8f * (Uniform(0xA7u, id) - 0.5f)) / n;
                if (at < a || at >= b) continue;
                into.Add(new Stone(at, id, Uniform(0x512Eu, id), Uniform(0x3D7Au, id), lane));
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
