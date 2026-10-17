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
        bed.StonesBetween(40f, 90f, whole);
        var parts = new List<StreamBed.Stone>();
        bed.StonesBetween(40f, 61.3f, parts);
        bed.StonesBetween(61.3f, 90f, parts);
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
}
