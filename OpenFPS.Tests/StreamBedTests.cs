using System.Numerics;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Nature;
using OpenFPS.Environment;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A stream's bed along its length (StreamBed, docs/RUNNING_WATER.md 15.6): a gentle natural bed's stones gathered into
/// riffles between pools every five to seven widths, a steep one's into steps; and a long line's synth rendering the
/// stones beside you one by one and the rest of its length from a few that stand for them, as loud together as all.
/// </summary>
public class StreamBedTests
{
    private readonly ITestOutputHelper _o;
    public StreamBedTests(ITestOutputHelper o) => _o = o;

    private const int Rate = 48000;

    /// <summary>A creek of the ground's drainage, as the server declares one: its bed by its slope.</summary>
    private static RunningWaterSpec Creek(float slope, float length, float width = 3f, float headShare = 1f)
        => GroundChannels.SpecFor(GroundChannelKind.Creek, width, slope, length, 400f, new GroundCatchment(new float[GroundHydrology.Surfaces], 600f), headShare);

    /// <summary>The bed's form is its slope's: riffles and pools under 2 %, plane to 3 %, steps over; a rill, a ditch
    /// and a runnel are even.</summary>
    [Fact]
    public void ACreeksBedIsItsSlopes()
    {
        Assert.Equal(BedForm.PoolRiffle, Creek(0.005f, 100f).Bed);
        Assert.Equal(BedForm.Even, Creek(0.025f, 100f).Bed);
        Assert.Equal(BedForm.StepPool, Creek(0.06f, 100f).Bed);
        foreach (var kind in new[] { GroundChannelKind.Rill, GroundChannelKind.Ditch, GroundChannelKind.Runnel })
            Assert.Equal(BedForm.Even, GroundChannels.BedOf(kind, 0.005f));
    }

    /// <summary>
    /// A gentle bed's riffles start every five to seven widths, its steep bed's steps every one to four; over the whole
    /// bed it has the stones it would have evenly, within a few per cent, and the riffles hold most of them.
    /// </summary>
    [Fact]
    public void RifflesComeEveryFiveToSevenWidthsAndHoldTheStones()
    {
        foreach (var (slope, lo, hi) in new[] { (0.005f, 5f, 7f), (0.06f, 1f, 4f) })
        {
            var spec = Creek(slope, 400f);
            var bed = new StreamBed(spec, 11);
            var starts = new List<float>();
            bool was = bed.RoughAt(0f);
            for (float s = 0.05f; s < bed.Length; s += 0.05f)
            {
                bool now = bed.RoughAt(s);
                if (now && !was) starts.Add(s);
                was = now;
            }
            var gaps = starts.Zip(starts.Skip(1), (a, b) => (b - a) / spec.WidthMetres).ToList();
            double stones = bed.CountBetween(0f, bed.Length), even = spec.Obstacles!.PerMetre * (double)bed.Length;
            double inRough = 0;
            for (float s = 0f; s < bed.Length; s += 0.1f) if (bed.RoughAt(s + 0.05f)) inRough += bed.CountBetween(s, s + 0.1f);
            _o.WriteLine($"{spec.Bed} at {slope:P1}: {starts.Count} units, every {gaps.Min():F2} to {gaps.Max():F2} widths; {stones:F0} stones (evenly {even:F0}), {inRough / stones:P0} in the riffles");
            Assert.True(gaps.Count > 10, "too few units to judge");
            Assert.All(gaps, g => Assert.InRange(g, lo - 0.01f, hi + 0.01f));
            Assert.InRange(stones / even, 0.93, 1.07);
            Assert.True(inRough / stones > 0.7, $"the riffles hold {inRough / stones:P0} of the stones");
        }
    }

    /// <summary>The bed's stones are where they are: listed in two spans or in one, the same stones.</summary>
    [Fact]
    public void TheStonesAreTheSameHoweverAskedFor()
    {
        var bed = new StreamBed(Creek(0.005f, 200f), 3);
        var whole = new List<StreamBed.Stone>();
        var parts = new List<StreamBed.Stone>();
        // Every lane of it: a 3 m creek's middle and its two banks (BedSection).
        for (int lane = 0; lane < bed.Lanes; lane++)
        {
            bed.StonesBetween(40f, 90f, whole, lane);
            bed.StonesBetween(40f, 61.3f, parts, lane);
            bed.StonesBetween(61.3f, 90f, parts, lane);
        }
        Assert.Equal(whole.Select(s => s.Id).OrderBy(i => i), parts.Select(s => s.Id).OrderBy(i => i));
        double mean = bed.CountBetween(40f, 90f);
        _o.WriteLine($"{whole.Count} stones listed, {mean:F1} on the mean");
        Assert.InRange(whole.Count, mean - 3 * Math.Sqrt(mean) - 2, mean + 3 * Math.Sqrt(mean) + 2);
    }

    /// <summary>
    /// A 300 m creek cut beside the listener: the pieces at its feet render their own stones, each one stone; the far
    /// pieces a few sites that stand for all of theirs. Every stone of the bed is stood for once, whichever way it is cut.
    /// </summary>
    [Fact]
    public void ALongCreekRendersTheStonesBesideYouAndStandsInForTheRest()
    {
        var spec = Creek(0.005f, 300f);
        var path = LinePath.Straight(Vector3.Zero, Vector3.UnitX, spec.LengthMetres, spec.WidthMetres);
        var synth = new RunningWaterSynth(spec, Rate, 9, ExtendedSources.MaxPlaces) { Flow = 200f };
        double stones = new StreamBed(spec, 9).CountBetween(0f, spec.LengthMetres);
        foreach (var ear in new[] { new Vector3(40f, 1.7f, 3f), new Vector3(-140f, 1.7f, 1.5f), new Vector3(0f, 1.7f, 200f) })
        {
            var cut = ExtendedSources.Cut(path, ear, ExtendedSources.MaxPlaces);
            synth.Cut = cut;
            synth.Control(0.01f);
            var sites = synth.Sites;
            double stood = sites.Sum(s => (double)s.Weight);
            path.Nearest(ear, out float along);
            int near = cut.PieceAt(along);
            var beside = sites.Where(s => cut.PieceAt(s.Along) == near).ToList();
            _o.WriteLine($"ear at {ear}: {sites.Count} sites stand for {stood:F0} of {stones:F0} stones; beside it {beside.Count} sites, weights {string.Join(",", beside.Select(s => s.Weight.ToString("F1")))}");
            Assert.True(sites.Count <= 40);
            Assert.InRange(stood / stones, 0.97, 1.03);
            if (ear.Z < 10f) Assert.All(beside, s => Assert.Equal(1f, s.Weight));
        }
    }

    /// <summary>The power of a long creek: the energy of a synth's places over a few seconds, pascals squared.</summary>
    private static double[] Render(RunningWaterSynth synth, int seconds)
    {
        int n = synth.Places;
        var energy = new double[n];
        var buf = new float[n];
        for (int i = 0; i < Rate * (seconds + 1); i++)
        {
            if (i % 256 == 0) synth.Control(256f / Rate);
            synth.NextPlaces(buf);
            if (i < Rate) continue;
            for (int k = 0; k < n; k++) energy[k] += buf[k] * (double)buf[k];
        }
        return energy;
    }

    /// <summary>
    /// A metre of a long line is as loud as a metre of a stretch rendered as it always was (each of its sites a few
    /// stones, none bursting faster than its stones would): 500 m of ditch, 3,000 stones, heard far off as one piece
    /// (every site a stand-in for seventy-odd, bursting no oftener than forty times a second) and cut beside the
    /// listener (its near stones each its own), against 20 m of the same ditch; within 1.5 dB a metre.
    /// </summary>
    [Fact]
    public void StandInsAreAsLoudAsTheStonesTheyStandFor()
    {
        var empty = new GroundCatchment(new float[GroundHydrology.Surfaces], 600f);
        var stretch = GroundChannels.SpecFor(GroundChannelKind.Ditch, 1f, 0.01f, 20f, 20f, empty);
        var line = GroundChannels.SpecFor(GroundChannelKind.Ditch, 1f, 0.01f, 500f, 20f, empty);
        Assert.False(RunningWaterSynth.FromTheBed(stretch));
        Assert.True(RunningWaterSynth.FromTheBed(line));
        var path = LinePath.Straight(Vector3.Zero, Vector3.UnitX, line.LengthMetres);
        // Pa² a second for each metre of it.
        double PerMetre(RunningWaterSpec spec, ExtendedSources.LineCut? cut, int seed)
        {
            var synth = new RunningWaterSynth(spec, Rate, seed, ExtendedSources.MaxPlaces) { Flow = 15f, Cut = cut };
            return Render(synth, 4).Sum() / 4.0 / spec.LengthMetres;
        }
        // Near its onset only its bigger stones break, so a stretch's forty are one draw of its spread's tail, a few
        // decibels either way: the stretch's level is the mean of a dozen.
        var stretches = Enumerable.Range(0, 12).Select(seed => PerMetre(stretch, null, 100 + seed)).ToList();
        double reference = stretches.Average();
        _o.WriteLine($"twelve 20 m stretches of ditch: {10 * Math.Log10(stretches.Min() / reference):F1} to {10 * Math.Log10(stretches.Max() / reference):+0.0} dB about their mean");
        double whole = 10 * Math.Log10(PerMetre(line, ExtendedSources.Whole(line.LengthMetres), 21) / reference);
        double beside = 10 * Math.Log10(PerMetre(line, ExtendedSources.Cut(path, new Vector3(0f, 1.7f, 1.5f), ExtendedSources.MaxPlaces), 21) / reference);
        _o.WriteLine($"a metre of 500 m of ditch against a metre of 20 m: heard as one piece {whole:+0.00;-0.00} dB, cut beside the ear {beside:+0.00;-0.00} dB");
        Assert.InRange(whole, -1.5, 1.5);
        Assert.InRange(beside, -1.5, 1.5);
    }

    /// <summary>
    /// Each piece plays what is in it: a gentle creek 60 m long cut into twelve from far off, its pieces over riffles
    /// are louder than those over pools, by 4 dB and more on the mean; an even bed's pieces are within 6 dB of each other's.
    /// </summary>
    [Fact]
    public void ARiffleIsLouderThanAPool()
    {
        foreach (var slope in new[] { 0.005f, 0.025f })
        {
            var spec = Creek(slope, 60f, width: 1.5f);
            var path = LinePath.Straight(Vector3.Zero, Vector3.UnitX, spec.LengthMetres, spec.WidthMetres);
            var cut = ExtendedSources.Cut(path, new Vector3(0f, 1.7f, 400f), ExtendedSources.MaxPlaces);
            var synth = new RunningWaterSynth(spec, Rate, 5, ExtendedSources.MaxPlaces) { Flow = 100f, Cut = cut };
            var energy = Render(synth, 10);
            var bed = new StreamBed(spec, 5);
            var rough = new List<double>();
            var pool = new List<double>();
            for (int k = 0; k < cut.Count; k++)
            {
                int i = cut.PieceOfPlace(k);
                float from = cut.Edges[i] + 0.5f * spec.LengthMetres, to = cut.Edges[i + 1] + 0.5f * spec.LengthMetres;
                double roughShare = 0;
                for (float s = from; s < to; s += 0.05f) if (bed.RoughAt(s)) roughShare += 0.05;
                roughShare /= to - from;
                double db = 10 * Math.Log10(Math.Max(1e-30, energy[k] / (to - from)));
                if (roughShare > 0.65) rough.Add(db); else if (roughShare < 0.15) pool.Add(db);
                _o.WriteLine($"  {spec.Bed} piece {i,2}, {from,5:F1} to {to,5:F1} m, {roughShare:P0} riffle: {db,6:F1} dB a metre");
            }
            if (spec.Bed == BedForm.PoolRiffle)
            {
                Assert.True(rough.Count > 0 && pool.Count > 0, "no piece mostly over a riffle or a pool");
                Assert.True(rough.Average() - pool.Average() > 4.0, $"riffles {rough.Average():F1} dB, pools {pool.Average():F1} dB a metre");
            }
            else
            {
                var all = rough.Concat(pool).ToList();
                Assert.True(all.Max() - all.Min() < 6.0, $"an even bed's pieces from {all.Min():F1} to {all.Max():F1} dB a metre");
            }
        }
    }

    // ── A wide bed's banks as their own flow (BedSection, docs/RUNNING_WATER.md 15.10) ──────────────────────────────

    /// <summary>
    /// A parabolic section with Manning's law down each vertical: each bank, the outer fifth, carries a few per cent of
    /// the flow in water about half the mean depth and well under the mean speed, over stones as much smaller; the middle
    /// the rest, deeper and faster than the mean. The strips' water adds up to the channel's: their widths to one, their
    /// flows to one, and each strip's flow is its width times its depth times its speed.
    /// </summary>
    [Fact]
    public void ABanksWaterIsShallowerSlowerAndLess()
    {
        foreach (float n in new[] { 0.03f, 0.045f, 0.07f })
        {
            var (mid, bank) = BedSection.Strips(n);
            _o.WriteLine($"n {n:F3}: a bank {bank.FlowShare:P1} of the flow, depth x{bank.DepthRatio:F2}, speed x{bank.SpeedRatio:F2}; the middle {mid.FlowShare:P1}, x{mid.DepthRatio:F2}, x{mid.SpeedRatio:F2}");
            Assert.Equal(1.0, mid.WidthShare + 2.0 * bank.WidthShare, 4);
            Assert.Equal(1.0, mid.FlowShare + 2.0 * bank.FlowShare, 3);
            foreach (var s in new[] { mid, bank })
                Assert.Equal(s.FlowShare, s.WidthShare * s.DepthRatio * s.SpeedRatio, 3);
            Assert.InRange(bank.FlowShare, 0.03f, 0.10f);
            Assert.InRange(bank.DepthRatio, 0.4f, 0.65f);
            Assert.InRange(bank.SpeedRatio, 0.45f, 0.75f);
            Assert.True(mid.DepthRatio > 1.2f && mid.SpeedRatio > 1.05f, "the middle is deeper and faster than the mean");
            Assert.True(bank.SizeRatio < 0.5f * mid.SizeRatio, "a bank's stones are under half the middle's");
        }
    }

    /// <summary>
    /// A wide creek's bed lies in three lanes, its middle and its two banks, each with stones of its own: together as many
    /// as the bed had in one, each bank's under a third of them, the riffles holding most of each lane's, and no stone in
    /// two lanes. A bed under 1.5 m, or a short even one, keeps one lane and its stones as they were.
    /// </summary>
    [Fact]
    public void AWideCreeksBanksHaveTheirOwnStones()
    {
        var spec = Creek(0.005f, 400f, width: 3f);
        var bed = new StreamBed(spec, 11);
        var flat = new StreamBed(spec, 11, banksApart: false);
        Assert.Equal(3, bed.Lanes);
        Assert.Equal(1, flat.Lanes);
        double all = flat.CountBetween(0f, bed.Length);
        double mid = bed.CountBetween(0f, bed.Length, BedSection.Middle), left = bed.CountBetween(0f, bed.Length, BedSection.LeftBank);
        double right = bed.CountBetween(0f, bed.Length, BedSection.RightBank);
        _o.WriteLine($"{all:F0} stones in one lane; {mid:F0} in the middle, {left:F0} and {right:F0} on the banks");
        Assert.Equal(all, mid + left + right, 3);
        Assert.InRange(left / all, 0.15, 0.33);
        Assert.Equal(left, right, 6);
        var ids = new HashSet<long>();
        for (int lane = 0; lane < 3; lane++)
        {
            var stones = new List<StreamBed.Stone>();
            bed.StonesBetween(0f, bed.Length, stones, lane);
            Assert.All(stones, s => Assert.Equal(lane, s.Lane));
            Assert.All(stones, s => Assert.True(ids.Add(s.Id), $"stone {s.Id} in two lanes"));
            double inRough = stones.Count(s => bed.RoughAt(s.At));
            Assert.True(inRough / stones.Count > 0.7, $"lane {lane}: the riffles hold {inRough / stones.Count:P0} of its stones");
        }
        // Narrow, or a short even stretch fed at its head (its sites spread along it, not taken from its bed): one lane.
        Assert.Equal(1, new StreamBed(Creek(0.005f, 400f, width: 1.4f), 11).Lanes);
        Assert.False(BedSection.Banked(Creek(0.025f, 20f, width: 3f)));
        Assert.False(BedSection.Banked(RunningWaterSpec.Creek));
    }

    /// <summary>
    /// Heard from beside the creek, the near bank is near and the far bank far: in the piece beside you the near bank's
    /// lane is louder than the piece's mean and the far bank's quieter, the other way round from the other side, and all
    /// three alike from far off. Their mean over the bed's fifths is the piece's own, so the piece's voice keeps its level.
    /// </summary>
    [Fact]
    public void TheNearBankIsHeardNearer()
    {
        var path = LinePath.Straight(Vector3.Zero, Vector3.UnitX, 100f, 3f, banked: true);
        foreach (var (ear, near, far) in new[] { (new Vector3(0f, 1.7f, 3f), BedSection.LeftBank, BedSection.RightBank),
                                                 (new Vector3(0f, 1.7f, -3f), BedSection.RightBank, BedSection.LeftBank) })
        {
            var cut = ExtendedSources.Cut(path, ear, ExtendedSources.MaxPlaces);
            path.Nearest(ear, out float along);
            int i = cut.PieceAt(along);
            float gNear = cut.LaneGain(i, near), gMid = cut.LaneGain(i, BedSection.Middle), gFar = cut.LaneGain(i, far);
            _o.WriteLine($"ear at {ear}: near bank x{gNear:F2}, middle x{gMid:F2}, far bank x{gFar:F2}");
            Assert.True(gNear > 1.15f && gFar < 0.85f, $"near x{gNear:F2}, far x{gFar:F2}");
            for (int p = 0; p < cut.Count; p++)
            {
                float m = cut.LaneGain(p, 0), l = cut.LaneGain(p, 1), r = cut.LaneGain(p, 2);
                Assert.Equal(1f, 0.6f * m * m + 0.2f * l * l + 0.2f * r * r, 3);
            }
        }
        var distant = ExtendedSources.Cut(path, new Vector3(0f, 1.7f, 300f), 4);
        for (int p = 0; p < distant.Count; p++)
            for (int lane = 0; lane < 3; lane++) Assert.InRange(distant.LaneGain(p, lane), 0.97f, 1.03f);
        Assert.Null(ExtendedSources.Cut(LinePath.Straight(Vector3.Zero, Vector3.UnitX, 100f, 3f), Vector3.UnitZ * 3f, 6).LaneGains);
    }

    /// <summary>
    /// Walking beside 40 m of a banked creek and away from it to 200 m in 5 cm steps, its pieces merging as it narrows at
    /// the ear, cut again as the client cuts it: the line heard whole never steps (under 0.3 dB a step), and when it is cut
    /// again no lane of the piece beside you jumps (each lane's gain within 1 dB of what it was), so neither bank cuts in
    /// or out, nor changes sides.
    /// </summary>
    [Fact]
    public void WalkingBesideTheBanksNothingSteps()
    {
        var path = LinePath.Straight(Vector3.Zero, Vector3.UnitX, 40f, 3f, banked: true);
        ExtendedSources.LineCut? cut = null;
        Vector3 cutAt = default;
        int wanted = 0, cuts = 0;
        var counts = new SortedSet<int>();
        double lastTotal = double.NaN, worstTotal = 0, worstLane = 0;
        var walk = new List<Vector3>();
        for (float x = -15f; x <= 15f; x += 0.05f) walk.Add(new Vector3(x, 1.7f, 3f));
        for (float z = 3f; z <= 200f; z += 0.05f) walk.Add(new Vector3(15f, 1.7f, z));
        foreach (var ear in walk)
        {
            wanted = ExtendedSources.WantedPieces(path, ear, wanted);
            counts.Add(wanted);
            if (cut == null || cut.Count != wanted || Vector3.Distance(ear, cutAt) > cut.RecutMetres)
            {
                var next = ExtendedSources.Cut(path, ear, wanted, 2f);
                if (cut != null)
                {
                    path.Nearest(ear, out float along);
                    int a = cut.PieceAt(along), b = next.PieceAt(along);
                    for (int lane = 0; lane < 3; lane++)
                        worstLane = Math.Max(worstLane, Math.Abs(20 * Math.Log10(next.LaneGain(b, lane) / cut.LaneGain(a, lane))));
                }
                cut = next;
                cutAt = ear;
                cuts++;
            }
            double total = 0;
            for (int i = 0; i < cut.Count; i++)
            {
                var p = ExtendedSources.Place(path, ear, cut.Edges[i], cut.Edges[i + 1], 2f);
                float g = p.Gain * MathF.Min(1f, 2f / Vector3.Distance(ear, p.At));
                total += cut.ShareOfPlace(cut.PlaceOfPiece(i)) * (double)g * g;
            }
            if (!double.IsNaN(lastTotal)) worstTotal = Math.Max(worstTotal, Math.Abs(10 * Math.Log10(total / lastTotal)));
            lastTotal = total;
        }
        _o.WriteLine($"{cuts} cuts, {counts.Count} counts of pieces ({string.Join(",", counts)}); the line's largest step {worstTotal:F3} dB; a lane beside you at a cut {worstLane:F2} dB");
        Assert.True(cuts > 20 && counts.Count > 2, "the walk never merged pieces");
        Assert.True(worstTotal < 0.3, $"the line stepped {worstTotal:F2} dB");
        Assert.True(worstLane < 1.0, $"a lane jumped {worstLane:F2} dB at a cut");
    }

    /// <summary>
    /// The banks sound unlike the middle: their shallow water breaks over small stones into small bubbles, so the banks
    /// alone are brighter than the middle alone (their top octaves, 4 to 16 kHz, stand higher over their 125 to 500 Hz
    /// by 6 dB and more) and quieter (a fraction of the water). Oak Creek's bed after an hour of heavy rain, 92 L/s.
    /// </summary>
    [Fact]
    public void TheBanksAreBrighterAndQuieterThanTheMiddle()
    {
        var spec = GroundChannels.SpecFor(GroundChannelKind.Creek, 3f, 0.005f, 300f, 943f, new GroundCatchment(new float[GroundHydrology.Surfaces], 600f));
        float[] Bands(float middle, float bank)
        {
            var synth = new RunningWaterSynth(spec, Rate, 7, ExtendedSources.MaxPlaces) { Flow = 92f, MiddlePart = middle, BankPart = bank };
            var buf = new float[synth.Places];
            var mono = new float[Rate * 6];
            for (int i = 0; i < Rate * 8; i++)
            {
                if (i % 256 == 0) synth.Control(256f / Rate);
                synth.NextPlaces(buf);
                if (i >= Rate * 2) mono[i - Rate * 2] = buf.Sum();
            }
            var e = new float[Spectrum.BandCount];
            for (int s = 0; s + 4096 <= mono.Length; s += 4096)
            {
                var b = Spectrum.BandEnergy(mono.AsSpan(s, 4096), Rate);
                for (int k = 0; k < e.Length; k++) e[k] += b[k];
            }
            return e;
        }
        var mid = Bands(1f, 0f);
        var banks = Bands(0f, 1f);
        // Bands: 125-250, 250-500 against 4-8k, 8-16k.
        double Tilt(float[] e) => 10 * Math.Log10((e[7] + e[8]) / (e[2] + e[3]));
        double tilt = Tilt(banks) - Tilt(mid), level = 10 * Math.Log10(banks.Sum() / mid.Sum());
        _o.WriteLine($"the banks' top octaves {tilt:+0.0;-0.0} dB more over their low ones than the middle's; the banks {level:+0.0;-0.0} dB on the middle");
        Assert.True(tilt > 6.0, $"the banks only {tilt:F1} dB brighter");
        Assert.InRange(level, -25.0, -4.0);
    }
}
