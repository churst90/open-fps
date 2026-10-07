using System.Diagnostics;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Rail;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A train is a handful of voices however long it is (TrainVoicing, TrainSlotState; docs/TRAINS.md,
/// "Voicing a train"). A fifty-wagon freight once took 130 voices, emptied the binaural pool and needed two
/// and a half cores on one thread (session 2026-10-07).
/// </summary>
public class TrainVoicingTests
{
    private readonly ITestOutputHelper _o;
    public TrainVoicingTests(ITestOutputHelper o) => _o = o;

    private const float Rate = 48000f;

    /// <summary>A train's non-signal sources laid along a straight track down the x axis, its head at
    /// <paramref name="head"/>.</summary>
    private static List<TrainVoicing.Source> Along(string preset, float head)
    {
        var layout = TrainLayout.Sources(TrainProfile.ByName(preset));
        return layout.Where(e => !e.IsSignal)
                     .Select(e => new TrainVoicing.Source(e.Index, e.AlongMetres, new Vector3(head - e.AlongMetres, e.HeightMetres, 0f)))
                     .ToList();
    }

    [Fact]
    public void A_freight_beside_you_is_a_few_voices_near_you_and_one_for_its_far_end()
    {
        // The head 300 m past, the listener 12 m off the track: a kilometre of train from 300 m one way
        // to 610 m the other.
        var sources = Along("freight", 300f);
        var ear = new Vector3(0f, 1.6f, 12f);
        var groups = TrainVoicing.Group(sources, ear, TrainVoicing.MaxFieldVoices, 0);
        var byIndex = sources.ToDictionary(s => s.Index);
        foreach (var g in groups)
        {
            var span = g.Select(i => byIndex[i].Position.X).ToArray();
            _o.WriteLine($"{g.Count,4} sources, x {span.Min(),7:F0} to {span.Max(),7:F0} m");
        }
        Assert.Equal(sources.Count, groups.Sum(g => g.Count));
        Assert.True(groups.Count <= TrainVoicing.MaxFieldVoices, $"{groups.Count} voices for one train");
        // The group abeam is short (what passes you is heard from where it is); the long ones are far.
        var nearest = groups.OrderBy(g => g.Min(i => Math.Abs(byIndex[i].Position.X))).First();
        float nearSpan = nearest.Max(i => byIndex[i].Position.X) - nearest.Min(i => byIndex[i].Position.X);
        var longest = groups.OrderByDescending(g => g.Count).First();
        Assert.True(nearSpan < 60f, $"the voice abeam spans {nearSpan:F0} m");
        Assert.True(longest.Count > sources.Count / 3, "the far end of the train is one voice");
    }

    [Fact]
    public void A_train_a_kilometre_off_is_one_voice_and_the_count_does_not_flicker()
    {
        var far = TrainVoicing.Group(Along("amtrak", 0f), new Vector3(-80f, 1.6f, 1500f), TrainVoicing.MaxFieldVoices, 0);
        Assert.Single(far);

        // Beside the line, moved a metre at a time with what it had: the count holds.
        var sources = Along("amtrak", 90f);
        int previous = 0, changes = 0;
        for (int step = 0; step < 40; step++)
        {
            var g = TrainVoicing.Group(sources, new Vector3(step * 0.5f, 1.6f, 15f), TrainVoicing.MaxFieldVoices, previous);
            if (previous != 0 && g.Count != previous) changes++;
            previous = g.Count;
        }
        Assert.True(changes <= 1, $"the number of voices changed {changes} times over 20 m of walking");
    }

    [Fact]
    public void A_group_keeps_its_voice_as_the_train_moves()
    {
        var previous = new Dictionary<int, int>();
        int handedOn = 0, total = 0;
        for (int step = 0; step < 30; step++)
        {
            var sources = Along("freight", 100f + step * 2f);     // 2 m a half second: 4 m/s
            var groups = TrainVoicing.Group(sources, new Vector3(0f, 1.6f, 12f), TrainVoicing.MaxFieldVoices, previous.Count == 0 ? 0 : previous.Values.Distinct().Count());
            var slots = TrainVoicing.AssignSlots(groups.Select(g => (IReadOnlyList<int>)g).ToList(), previous);
            var now = new Dictionary<int, int>();
            for (int g = 0; g < groups.Count; g++) foreach (int i in groups[g]) now[i] = slots[g];
            foreach (var (i, s) in now)
            {
                if (!previous.TryGetValue(i, out int was)) continue;
                total++;
                if (was != s) handedOn++;
            }
            Assert.Equal(groups.Count, slots.Distinct().Count());
            previous = now;
        }
        _o.WriteLine($"{handedOn} of {total} source-steps handed to another voice");
        Assert.True(handedOn < total / 20, $"{handedOn} of {total} source-steps moved voice: the voices are reshuffled, not kept");
    }

    [Theory]
    [InlineData(100f, 0f, 3f)]
    [InlineData(95f, 2.2f, 40f)]
    [InlineData(112f, 1f, 7f)]
    public void A_source_played_through_another_voice_is_as_loud_as_through_its_own(float level, float extent, float distance)
    {
        // Its own voice: declared at its level, played at its gain. Another declared at the train's level,
        // a few metres further, at that one's gain.
        float own = TrainVoicing.LawGain(level, extent, distance);
        float slotLevel = 112f;
        float slotGain = TrainVoicing.LawGain(slotLevel, 0f, distance + 4f);
        float w = TrainVoicing.Weight(level, own, slotLevel, slotGain);
        // Every physical voice renders its declared level at one point under full scale; the slot's
        // headroom is given back by the channel. So the played RMS of the source alone is
        // (its pressure / the slot's reference pressure) times the slot's gain, against own gain alone.
        float pSource = MathF.Pow(10f, level / 20f), pSlot = MathF.Pow(10f, slotLevel / 20f);
        float through = w * pSource / pSlot * slotGain;
        Assert.Equal(own, through, own * 1e-4f);
    }

    /// <summary>A voice carrying a train's rolling stock (offline: lanes rendered inline).</summary>
    private static (TrainVoiceState Train, TrainSlotState Slot) Voice(string preset, Func<TrainLayout.Entry, bool> carry, float weight)
    {
        var t = new TrainVoiceState(preset + "/t", TrainProfile.ByName(preset), Rate, 5) { Offline = true };
        var picked = t.Layout.Where(carry).Select(e => e.Index).ToArray();
        t.SetPlan(0, new TrainSlotPlan(picked, picked.Select(_ => weight).ToArray()));
        return (t, new TrainSlotState(t, 0, TrainVoicing.SlotLevelDb(t.Layout), Rate, startSample: 0));
    }

    private static double RmsDbfs(TrainSlotState s, float seconds, float skip = 1f)
    {
        var buf = new float[512];
        int skipBlocks = (int)(skip * Rate / buf.Length), blocks = (int)(seconds * Rate / buf.Length);
        for (int b = 0; b < skipBlocks; b++) s.Render(buf);
        double sum = 0;
        for (int b = 0; b < blocks; b++)
        {
            s.Render(buf);
            foreach (var x in buf) sum += x * (double)x;
        }
        return 10 * Math.Log10(sum / (blocks * buf.Length) + 1e-30);
    }

    [Fact]
    public void Forty_bogies_through_one_chain_are_forty_bogies_loud()
    {
        // The model is linear: n bogies' independent roughness through one chain at weight w is the root
        // of n times one bogie's, as n separate bogies would be.
        var (_, one) = Voice("freight", e => e.Kind == TrainLayout.Kind.Bogie && e.Label.Contains("#10 bogie 1"), 0.5f);
        var (_, forty) = Voice("freight", e => e.Kind == TrainLayout.Kind.Bogie && e.Label.Contains("wagon") && e.Label.EndsWith("bogie 1") && e.Index < 130, 0.5f);
        int n = TrainLayout.Sources(TrainProfile.ByName("freight")).Count(e => e.Kind == TrainLayout.Kind.Bogie && e.Label.Contains("wagon") && e.Label.EndsWith("bogie 1") && e.Index < 130);
        double a = RmsDbfs(one, 4f), b = RmsDbfs(forty, 4f);
        double expected = 10 * Math.Log10(n);
        _o.WriteLine($"one bogie {a:F1} dBFS, {n} bogies {b:F1} dBFS: +{b - a:F1} dB, independent sources +{expected:F1} dB");
        Assert.InRange(b - a, expected - 1.5, expected + 1.5);
    }

    [Fact]
    public void A_whole_freight_on_one_voice_costs_a_handful_of_bogies()
    {
        // Structural, so it holds on any machine: one chain per kind of bogie and per kind of body, however
        // many it carries. The timing is printed for the record.
        var (t, all) = Voice("freight", e => TrainVoiceState.IsRollingStock(e.Kind), 0.2f);
        var sw = Stopwatch.StartNew();
        RmsDbfs(all, 2f, skip: 0.5f);
        double share = sw.Elapsed.TotalSeconds / 2.5;
        var chains = typeof(TrainSlotState).GetField("_chains", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                                           .GetValue(all) as System.Collections.ICollection;
        _o.WriteLine($"freight, {t.Layout.Count} sources rolling stock on one voice: {chains!.Count} chains, {share * 100:F0} % of a core");
        Assert.True(chains.Count <= 4, $"{chains.Count} chains for one voice");
    }

    [Fact]
    public void A_voice_whose_lane_is_behind_renders_nothing_and_does_not_wait()
    {
        // Not offline: the lane is the render pool's. A voice ahead of it renders nothing this pass and
        // returns at once (the old taps took the train's lock and every worker that reached one waited).
        var t = new TrainVoiceState("amtrak/t", TrainProfile.ByName("amtrak"), Rate, 5);
        int stack = t.Layout.First(e => e.Kind == TrainLayout.Kind.ExhaustStack).Index;
        t.SetPlan(0, new TrainSlotPlan(new[] { stack }, new[] { 1f }));
        var slot = new TrainSlotState(t, 0, TrainVoicing.SlotLevelDb(t.Layout), Rate, startSample: 48000);
        var sw = Stopwatch.StartNew();
        slot.Produce();
        Assert.True(sw.ElapsedMilliseconds < 50, $"Produce took {sw.ElapsedMilliseconds} ms with its lane behind");
        // The lane, asked by the pool, renders ahead of the voice; then the voice can.
        t.Lanes.First(l => l.Sources.Contains(stack)).Produce();
        Assert.True(t.Lanes.First(l => l.Sources.Contains(stack)).Rendered > 48000, "the lane rendered toward the voice");
    }

    [Fact]
    public void A_voice_whose_lane_never_comes_still_plays_its_wagons()
    {
        // The diesel's lane is never rendered (its worker held up). Once the voice has nothing in hand it
        // renders anyway, the engine faded out of it, rather than starving whole: wagons and all.
        var t = new TrainVoiceState("freight/t", TrainProfile.ByName("freight"), Rate, 5);
        var carry = t.Layout.Where(e => e.Kind == TrainLayout.Kind.ExhaustStack || e.Kind == TrainLayout.Kind.Bogie)
                            .Select(e => e.Index).Take(12).ToArray();
        t.SetPlan(0, new TrainSlotPlan(carry, carry.Select(_ => 0.5f).ToArray()));
        var slot = new TrainSlotState(t, 0, TrainVoicing.SlotLevelDb(t.Layout), Rate, startSample: 0);
        var block = new float[1024];
        double energy = 0;
        for (int b = 0; b < 40; b++)
        {
            slot.Produce();
            slot.Consume(block);
            foreach (var x in block) energy += x * (double)x;
        }
        Assert.All(t.Lanes, l => Assert.Equal(0, l.Rendered));
        Assert.True(energy > 0, "the voice gave nothing while its engine's lane was behind");
    }

    [Fact]
    public void A_source_handed_between_voices_keeps_its_power()
    {
        // Two voices of one train; one bogie moves from the first to the second. Over the hand-over the two
        // together stay within a decibel or two of the bogie alone (equal power: each fades as the other rises).
        var t = new TrainVoiceState("freight/t", TrainProfile.ByName("freight"), Rate, 5) { Offline = true };
        int[] bogies = t.Layout.Where(e => e.Kind == TrainLayout.Kind.Bogie).Select(e => e.Index).Skip(10).Take(6).ToArray();
        float lvl = TrainVoicing.SlotLevelDb(t.Layout);
        t.SetPlan(0, new TrainSlotPlan(bogies, bogies.Select(_ => 0.5f).ToArray()));
        t.SetPlan(1, new TrainSlotPlan(Array.Empty<int>(), Array.Empty<float>()));
        var a = new TrainSlotState(t, 0, lvl, Rate, startSample: 0);
        var b = new TrainSlotState(t, 1, lvl, Rate, startSample: 0);
        var bufA = new float[480]; var bufB = new float[480];
        double before = 0, during = 0;
        int nBefore = 0, nDuring = 0;
        for (int k = 0; k < 400; k++)   // 4 s in 10 ms blocks
        {
            if (k == 200)
            {
                t.SetPlan(0, new TrainSlotPlan(Array.Empty<int>(), Array.Empty<float>()));
                t.SetPlan(1, new TrainSlotPlan(bogies, bogies.Select(_ => 0.5f).ToArray()));
            }
            a.Render(bufA); b.Render(bufB);
            double e = 0;
            for (int i = 0; i < bufA.Length; i++) e += bufA[i] * (double)bufA[i] + bufB[i] * (double)bufB[i];
            if (k >= 100 && k < 200) { before += e; nBefore++; }
            if (k >= 200 && k < 260) { during += e; nDuring++; }
        }
        double change = 10 * Math.Log10((during / nDuring) / (before / nBefore));
        _o.WriteLine($"power over the hand-over against before: {change:+0.0;-0.0} dB");
        Assert.InRange(change, -3.0, 1.5);
    }
}
