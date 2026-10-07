using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A tree, a fire, a fountain's taps and the rain round you heard from several places across them
/// (ExtendedSources, 2026-10-06): each place an independent stream of the one source, never a copy, and
/// the places together exactly as loud as the source from its middle.
/// </summary>
public class WideSourcesTests
{
    private readonly ITestOutputHelper _o;
    public WideSourcesTests(ITestOutputHelper o)
    {
        _o = o;
        AcousticRegistry.Initialize();
    }

    private const int Rate = 48000;

    private static double Db(double meanSquare) => 10 * Math.Log10(Math.Max(1e-30, meanSquare) / 4e-10);

    // ── The shares and the spread ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheSharesAlwaysSumToTheWhole()
    {
        Span<float> shares = stackalloc float[7];
        foreach (float s in new[] { 0f, 0.1f, 0.5f, 0.99f, 1f })
        {
            ExtendedSources.Shares(s, shares);
            float sum = 0f;
            foreach (float x in shares) { Assert.True(x >= 0f); sum += x; }
            Assert.Equal(1f, sum, 5);
        }
        ExtendedSources.Shares(0f, shares);
        Assert.Equal(1f, shares[0]);
        ExtendedSources.Shares(1f, shares);
        Assert.All(shares.ToArray(), x => Assert.Equal(1f / 7f, x, 5));
    }

    /// <summary>Near and wide, the source is all places; far, all middle; and nothing in between jumps.</summary>
    [Fact]
    public void TheSpreadFollowsTheAngleTheSourceFills()
    {
        Assert.Equal(1f, ExtendedSources.SpreadFor(2.7f, 5f));
        Assert.Equal(0f, ExtendedSources.SpreadFor(2.7f, 80f));
        Assert.Equal(0f, ExtendedSources.SpreadFor(0f, 1f));
        float last = 1f;
        for (float d = 1f; d < 100f; d += 0.25f)
        {
            float s = ExtendedSources.SpreadFor(2.7f, d);
            Assert.True(s <= last + 1e-6f);
            Assert.True(last - s < 0.05f, $"the spread jumped {last - s:F3} at {d} m");
            last = s;
        }
        // And it moves at its slew, never faster.
        Assert.Equal(0.07f, ExtendedSources.Slew(0f, 1f, 0.1f), 5);
        Assert.Equal(1f, ExtendedSources.Slew(0.95f, 1f, 0.1f), 5);
    }

    [Fact]
    public void TreesFiresAndFountainTapsHavePlacesAndNothingElseDoes()
    {
        var tree = ExtendedSources.Layout("foliage:park_tree")!;
        Assert.Equal(1 + ExtendedSources.TreePlaces, tree.Length);
        Assert.Equal(Vector3.Zero, tree[0]);
        float reach = ExtendedSources.Reach(tree);
        _o.WriteLine($"tree: {tree.Length} places, reach {reach:F2} m");
        Assert.InRange(reach, 2f, FoliageSpec.ParkTree.CrownRadiusMetres);
        Assert.Equal(1 + ExtendedSources.FirePlaces, ExtendedSources.Layout("fire:fire_pit")!.Length);
        Assert.Equal(1 + ExtendedSources.WaterTapPlaces, ExtendedSources.Layout("water:park_fountain/elm_park/3")!.Length);
        Assert.Null(ExtendedSources.Layout("water:park_fountain"));
        Assert.Null(ExtendedSources.Layout("machine:ac_window"));
        Assert.Null(ExtendedSources.Layout("rain:0"));
    }

    /// <summary>
    /// The places together are as loud as the source from its middle at 1, 5 and 20 m: each place's
    /// voice rendered by the mixer's own law (Loudness.RenderedGain) from where it is, its share of
    /// the source, and the one balance gain. Within 0.5 dB is the requirement; it comes out exact.
    /// </summary>
    [Fact]
    public void ThePlacesTogetherAreTheSourceAt1And5And20Metres()
    {
        foreach (var (key, middle, levelDb, extent) in new[]
        {
            ("foliage:park_tree", new Vector3(0f, 7f, 0f), FoliageSpec.ParkTree.SourceLevelDb, FoliageSpec.ParkTree.ExtentMetres),
            ("fire:fire_pit", new Vector3(0f, 0.6f, 0f), FireSpec.GardenFirePit.SourceLevelDb, FireSpec.GardenFirePit.ExtentMetres),
        })
        {
            var layout = ExtendedSources.Layout(key)!;
            var (_, reference) = Loudness.Place(levelDb, extent);
            float range = Loudness.AudibleRange(levelDb);
            var at = layout.Select(o => middle + o).ToArray();
            var shares = new float[layout.Length];
            foreach (float d in new[] { 1f, 5f, 20f })
                foreach (float spread in new[] { 0.3f, 1f })
                {
                    // At d from the middle, level with it and at ear height.
                    var ear = new Vector3(d * 0.6f, MathF.Max(1.7f, middle.Y - d * 0.8f), d * 0.8f);
                    ear = middle + Vector3.Normalize(ear - middle) * d;
                    ExtendedSources.Shares(spread, shares);
                    float balance = ExtendedSources.Balance(ear, middle, at, shares, reference, range);
                    double power = 0;
                    for (int k = 0; k < at.Length; k++)
                    {
                        float g = Loudness.RenderedGain(balance, reference, range, Vector3.Distance(ear, at[k]));
                        power += shares[k] * (double)g * g;
                    }
                    float one = Loudness.RenderedGain(1f, reference, range, d);
                    double diff = 10 * Math.Log10(power / (one * (double)one));
                    _o.WriteLine($"{key} at {d} m, spread {spread}: places together {diff:F3} dB against the middle alone (balance {20 * MathF.Log10(balance):F2} dB)");
                    Assert.InRange(diff, -0.5, 0.5);
                    Assert.InRange(diff, -0.01, 0.01);
                }
        }
    }

    // ── The synths: independent streams that add up to the source ─────────────────────────────

    private static double Corr(float[] a, float[] b)
    {
        double ab = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Length; i++) { ab += a[i] * (double)b[i]; aa += a[i] * (double)a[i]; bb += b[i] * (double)b[i]; }
        return ab / Math.Sqrt(aa * bb + 1e-30);
    }

    private static float[][] Places(int places, int seconds, Action<Span<float>> next, Action control)
    {
        var x = new float[places][];
        for (int k = 0; k < places; k++) x[k] = new float[Rate * seconds];
        var buf = new float[places];
        for (int i = 0; i < Rate * seconds; i++)
        {
            if (i % 256 == 0) control();
            next(buf);
            for (int k = 0; k < places; k++) x[k][i] = buf[k];
        }
        return x;
    }

    private static double MeanSquare(float[] x) { double e = 0; foreach (float v in x) e += v * (double)v; return e / x.Length; }

    /// <summary>Every pair of places of a tree is uncorrelated (copies of one stream would be near one),
    /// and their sum is as loud as the tree from one place.</summary>
    [Fact]
    public void ATreesPlacesAreIndependentAndAddUpToTheTree()
    {
        var spec = FoliageSpec.ParkTree;
        int n = 1 + FoliageSynth.Boughs;
        var wide = new FoliageSynth(spec, Rate, 5, n) { Wind = 4.5f, Spread = 1f };
        var x = Places(n, 30, s => wide.NextPlaces(s), () => wide.Control(256f / Rate));
        double worst = 0;
        for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++) worst = Math.Max(worst, Math.Abs(Corr(x[a], x[b])));
        double sumPower = MeanSquare(x.Aggregate(new float[x[0].Length], (acc, p) => { for (int i = 0; i < acc.Length; i++) acc[i] += p[i]; return acc; }));
        double partPowers = x.Sum(MeanSquare);
        var one = new FoliageSynth(spec, Rate, 5) { Wind = 4.5f };
        var y = Places(1, 30, s => s[0] = one.Next(), () => one.Control(256f / Rate));
        double whole = MeanSquare(y[0]);
        _o.WriteLine($"tree: worst correlation between places {worst:F3}; sum {Db(sumPower):F2} dB, places' powers {Db(partPowers):F2} dB, one place {Db(whole):F2} dB");
        Assert.True(worst < 0.05, $"two places of the tree correlate {worst:F3}");
        Assert.InRange(Db(sumPower) - Db(partPowers), -0.2, 0.2);
        Assert.InRange(Db(sumPower) - Db(whole), -0.5, 0.5);
        // Each place has its share: equal at full spread.
        foreach (var p in x) Assert.InRange(Db(MeanSquare(p)) - Db(partPowers / n), -1.5, 1.5);
    }

    [Fact]
    public void MergedATreeIsAllMiddle()
    {
        int n = 1 + FoliageSynth.Boughs;
        var tree = new FoliageSynth(FoliageSpec.ParkTree, Rate, 5, n) { Wind = 4.5f, Spread = 0f };
        var x = Places(n, 5, s => tree.NextPlaces(s), () => tree.Control(256f / Rate));
        for (int k = 1; k < n; k++) Assert.True(MeanSquare(x[k]) < 1e-12, $"place {k} is not silent merged");
        Assert.True(MeanSquare(x[0]) > 1e-9);
    }

    [Fact]
    public void AFiresPlacesAreIndependentAndAddUpToTheFire()
    {
        var spec = FireSpec.GardenFirePit;
        int n = 1 + ExtendedSources.FirePlaces;
        double sumPower = 0, partPowers = 0, whole = 0, worst = 0;
        foreach (int seed in new[] { 3, 7, 11 })
        {
            var wide = new FireSynth(spec, Rate, seed, n) { Wind = 1f, Spread = 1f };
            var x = Places(n, 40, s => wide.NextPlaces(s), () => wide.Control(256f / Rate));
            for (int a = 0; a < n; a++)
                for (int b = a + 1; b < n; b++) worst = Math.Max(worst, Math.Abs(Corr(x[a], x[b])));
            var sum = new float[x[0].Length];
            foreach (var p in x) for (int i = 0; i < sum.Length; i++) sum[i] += p[i];
            sumPower += MeanSquare(sum);
            partPowers += x.Sum(MeanSquare);
            var one = new FireSynth(spec, Rate, seed) { Wind = 1f };
            var y = Places(1, 40, s => s[0] = one.Next(), () => one.Control(256f / Rate));
            whole += MeanSquare(y[0]);
        }
        _o.WriteLine($"fire: worst correlation between places {worst:F3}; sum {Db(sumPower / 3):F2} dB, places' powers {Db(partPowers / 3):F2} dB, one place {Db(whole / 3):F2} dB");
        Assert.True(worst < 0.05, $"two places of the fire correlate {worst:F3}");
        Assert.InRange(Db(sumPower) - Db(partPowers), -0.2, 0.2);
        Assert.InRange(Db(sumPower) - Db(whole), -0.5, 0.5);
    }

    /// <summary>
    /// The fountain's five map taps were already five streams, not copies of one (each fall lands at one
    /// tap and writes its own events there): the taps measure uncorrelated. Each tap's places are too,
    /// and with them the fountain is as loud as it was.
    /// </summary>
    [Fact]
    public void AFountainsTapsAndTheirPlacesAreIndependent()
    {
        var spec = WaterFeatureSpec.ParkFountain;
        int taps = spec.Taps.Length;
        var plain = new FallingWaterSynth(spec, Rate, 4) { Wind = 2f };
        var t = Places(taps, 10, s => plain.NextTaps(s), () => plain.Control(256f / Rate));
        double worstTap = 0;
        for (int a = 0; a < taps; a++)
            for (int b = a + 1; b < taps; b++) worstTap = Math.Max(worstTap, Math.Abs(Corr(t[a], t[b])));
        double whole = t.Sum(MeanSquare);

        int per = 1 + ExtendedSources.WaterTapPlaces;
        var wide = new FallingWaterSynth(spec, Rate, 4, per) { Wind = 2f };
        for (int k = 0; k < taps; k++) wide.SetSpread(k, 1f);
        var x = Places(taps * per, 10, s => wide.NextPlaces(s), () => wide.Control(256f / Rate));
        double worstPlace = 0;
        for (int a = 0; a < x.Length; a++)
            for (int b = a + 1; b < x.Length; b++) worstPlace = Math.Max(worstPlace, Math.Abs(Corr(x[a], x[b])));
        double places = x.Sum(MeanSquare);
        _o.WriteLine($"fountain: worst correlation between taps {worstTap:F3}, between places {worstPlace:F3}; taps {Db(whole):F2} dB, places {Db(places):F2} dB");
        Assert.True(worstTap < 0.05);
        Assert.True(worstPlace < 0.05);
        Assert.InRange(Db(places) - Db(whole), -0.5, 0.5);
    }

    // ── The voices: one synth, read from several places ─────────────────────────────────────────

    private static double Render(PhysicalVoiceState voice, float seconds, float[]? into = null)
    {
        var warm = new float[Rate / 2];
        for (int i = 0; i < warm.Length; i += 1024) voice.Render(warm.AsSpan(i, Math.Min(1024, warm.Length - i)));
        var buf = into ?? new float[(int)(Rate * seconds)];
        for (int i = 0; i < buf.Length; i += 1024) voice.Render(buf.AsSpan(i, Math.Min(1024, buf.Length - i)));
        double e = 0;
        foreach (float v in buf) { double pa = v * (double)voice.PascalsAtFullScale; e += pa * pa; }
        return e / buf.Length;
    }

    /// <summary>A tree's middle and its places, as voices, render against the whole tree's full scale and
    /// together are as loud as the tree's one voice was.</summary>
    [Fact]
    public void ATreesPlaceVoicesAddUpToItsOneVoice()
    {
        // A steady wind held for this test. Gusts follow the clock, and the places and the one voice are
        // rendered one after the other, so a gusty wind gave them different gusts (1.45 dB apart under
        // load, 2026-10-06); and a session test running beside it writes the shared weather.
        using var held = WindField.Hold(WindWeather.Steady(4.5f, 270f, 0f));
        var spec = FoliageSpec.ParkTree;
        var at = new Vector3(400f, 7f, -300f);
        int n = 1 + FoliageSynth.Boughs;
        var shared = new PlacedNatureVoice("foliage:park_tree", spec, n, Rate, 9, at) { TargetSpread = 1f };
        var voices = Enumerable.Range(0, n).Select(k => new NaturePlaceState(shared, k, Rate, at)).ToArray();
        var old = new FoliageVoiceState(spec, Rate, 9, at);
        Assert.All(voices, v => Assert.Equal(old.PascalsAtFullScale, v.PascalsAtFullScale));
        // Rendered side by side, as the mixer takes them, block by block.
        double[] e = new double[n];
        var block = new float[1024];
        for (int b = 0; b < Rate * 20 / 1024; b++)
            for (int k = 0; k < n; k++)
            {
                voices[k].Render(block);
                if (b < Rate / 1024) continue;
                foreach (float v in block) { double pa = v * (double)voices[k].PascalsAtFullScale; e[k] += pa * pa; }
            }
        double together = e.Sum() / (Rate * 19.0);
        double alone = Render(old, 19f);
        _o.WriteLine($"tree as {n} voices {Db(together):F2} dB, as one voice {Db(alone):F2} dB");
        // The one voice reads the field's gusts, the places a steady... both read the field: a few tenths.
        Assert.InRange(Db(together) - Db(alone), -1.0, 1.0);
    }

    // ── The rain: the roof over you and the near quarters in parts ───────────────────────────────

    [Fact]
    public void ARainKeyNamesItsSlotAndPart()
    {
        Assert.True(RainFeeds.TryParse("rain:0", out int s, out int p));
        Assert.Equal((0, 0), (s, p));
        Assert.True(RainFeeds.TryParse("rain:0/3", out s, out p));
        Assert.Equal((0, 3), (s, p));
        Assert.True(RainFeeds.TryParse("rain:2/1", out s, out p));
        Assert.Equal((2, 1), (s, p));
        Assert.False(RainFeeds.TryParse("rain:9", out _, out _));
        Assert.False(RainFeeds.TryParse("rain:0/x", out _, out _));
        Assert.Equal("rain:0/2", RainFeeds.Key(0, 2));
        Assert.Equal("rain:4", RainFeeds.Key(4, 0));
        Assert.Equal(RainFeeds.RoofParts, RainFeeds.PartsFor(RainSurvey.OverheadSlot));
        Assert.Equal(RainFeeds.NearParts, RainFeeds.PartsFor(1));
        Assert.Equal(1, RainFeeds.PartsFor(6));
    }

    private static RainPatch SteelRoof()
    {
        var layer = new RainLayer { Kind = RainSurfaceKind.Plate, Material = "Metal", FromBelow = true,
                                    Plate = new RainPlate("Metal", 0.0007f, 1.2f, 1.2f) };
        layer.Add(0, 3f, 1.0f, 1f);
        layer.Add(1, 9f, 1.8f, 1f);
        return new RainPatch { Layers = new[] { layer }, ReferenceDistance = 1f };
    }

    /// <summary>A share of a patch is that share of its drops: a quarter of the roof renders a quarter of
    /// its power.</summary>
    [Fact]
    public void AShareOfAPatchIsThatShareOfItsRain()
    {
        var patch = SteelRoof();
        var quarter = patch.Share(0.25f);
        Assert.Equal(patch.Layers[0].TotalArea * 0.25f, quarter.Layers[0].TotalArea, 4);
        Assert.Equal(patch.Layers[0].Bins, quarter.Layers[0].Bins);
        Assert.Equal(patch.Layers[0].Key, quarter.Layers[0].Key);
        double Power(RainPatch p, int seed)
        {
            var synth = new RainSynth(Rate, seed) { Patch = p, RainRate = 25f };
            for (int i = 0; i < Rate; i++) synth.Next();
            double e = 0;
            for (int i = 0; i < 12 * Rate; i++) { float x = synth.Next(); e += x * (double)x; }
            return e / (12 * Rate);
        }
        double whole = Power(patch, 9), parts = 0;
        for (int k = 0; k < 4; k++) parts += Power(quarter, 9 + k * 7919);
        _o.WriteLine($"the roof {Db(whole):F2} dB, its four quarters together {Db(parts):F2} dB");
        Assert.InRange(Db(parts) - Db(whole), -0.5, 0.5);
    }

    /// <summary>The roof's part voices are rendered against the whole roof's measured level: each plays its
    /// share, together they play the roof, and the level they publish is the roof's.</summary>
    [Fact]
    public void TheRoofsPartsTogetherAreTheRoof()
    {
        var falling = new Precipitation(PrecipitationKind.Rain, 10f);
        var wholeFeed = new RainFeed { Patch = SteelRoof(), Falling = falling };
        // Every voice on one clock from zero, as every rain voice in the game shares one: each takes
        // the wall clock when it is made, so the whole roof and its parts, made one after the other,
        // started in different swells of the rain's clustering and published levels up to 1.2 dB
        // apart (GitHub run 37553074003). And 24 s, not 8, for the heavy-tailed drops.
        var wholeVoice = new RainVoiceState(wholeFeed, Rate, 9);
        wholeVoice.Synth.Clock = 0;
        double whole = Render(wholeVoice, 24f);
        var partFeed = new RainFeed { Patch = SteelRoof(), Falling = falling };
        double parts = 0;
        for (int k = 0; k < RainFeeds.RoofParts; k++)
        {
            var part = new RainVoiceState(partFeed, Rate, 9 + k * 7919, k, RainFeeds.RoofParts);
            part.Synth.Clock = 0;
            parts += Render(part, 24f);
        }
        _o.WriteLine($"roof as one voice {Db(whole):F2} dB ({wholeFeed.LevelDb:F1} published), as {RainFeeds.RoofParts} parts {Db(parts):F2} dB ({partFeed.LevelDb:F1} published)");
        Assert.InRange(Db(parts) - Db(whole), -0.5, 0.5);
        Assert.InRange(partFeed.LevelDb - wholeFeed.LevelDb, -1f, 1f);
    }

    /// <summary>The parts of the roof are round the point over the ear, and a near quarter's are either
    /// side of its middle, as far from the ear as its middle.</summary>
    [Fact]
    public void ThePartsArePlacedAcrossThePatch()
    {
        var ear = new Vector3(10f, 1.6f, -4f);
        var middle = new Vector3(10f, 2.5f, -4f);
        for (int k = 0; k < 4; k++)
        {
            var at = RainField.RoofPartAt(middle, 0.8f, k, 4);
            Assert.Equal(middle.Y, at.Y);
            Assert.Equal(0.8f, new Vector2(at.X - middle.X, at.Z - middle.Z).Length(), 3);
        }
        var east = new Vector3(16f, 0f, -4f);
        var a = RainField.QuarterPartAt(ear, east, 0, 2);
        var b = RainField.QuarterPartAt(ear, east, 1, 2);
        float da = new Vector2(a.X - ear.X, a.Z - ear.Z).Length(), dm = new Vector2(east.X - ear.X, east.Z - ear.Z).Length();
        Assert.Equal(dm, da, 3);
        float angle = MathF.Acos(Vector2.Dot(Vector2.Normalize(new Vector2(a.X - ear.X, a.Z - ear.Z)),
                                             Vector2.Normalize(new Vector2(b.X - ear.X, b.Z - ear.Z)))) * 180f / MathF.PI;
        Assert.Equal(45f, angle, 2);
        Assert.True(RainField.RoofReach(SteelRoof()) >= 0.3f && RainField.RoofReach(SteelRoof()) <= 2.5f);
    }

    /// <summary>The voice ids of the places stay clear of every other range the client hands out.</summary>
    [Fact]
    public void PlaceVoiceIdsDoNotCollide()
    {
        int lo = ClientAudioSystem.PlaceVoiceId(200_000, ExtendedSources.MaxPlaces - 1), hi = ClientAudioSystem.PlaceVoiceId(1, 1);
        Assert.True(hi < -3_100_000 && lo > -7_900_000, $"{lo}..{hi}");
        Assert.NotEqual(ClientAudioSystem.PlaceVoiceId(5, 1), ClientAudioSystem.PlaceVoiceId(4, 7));
        int rainParts = RainField.PartVoiceId(8, 7);
        Assert.True(rainParts > RainField.NearVoiceBase && rainParts < RainField.VoiceBase - RainFeeds.Slots);
    }

    /// <summary>
    /// Every extended source's places fit the voice ids a source is given, so two sources side by side (the
    /// stretches of a beach, the trees of a park) never share a voice. A surf beach has ten places, and with
    /// eight ids a source its ninth and tenth were the next stretch's first two.
    /// </summary>
    [Fact]
    public void EveryLayoutFitsItsSourcesVoiceIds()
    {
        var keys = new List<string>();
        keys.AddRange(FoliageSpec.Presets.Keys.Select(k => "foliage:" + k));
        keys.AddRange(FireSpec.Presets.Keys.Select(k => "fire:" + k));
        keys.AddRange(RunningWaterSpec.Presets.Keys.Select(k => "flow:" + k));
        keys.AddRange(ShoreSpec.Presets.Keys.Select(k => "shore:" + k));
        int widest = 0;
        foreach (string key in keys)
        {
            var layout = ExtendedSources.Layout(key);
            if (layout == null) continue;
            widest = Math.Max(widest, layout.Length);
            // Source 41 and its neighbour 42, each with this layout: no id of one is an id of the other.
            var mine = Enumerable.Range(1, layout.Length - 1).Select(k => ClientAudioSystem.PlaceVoiceId(41, k)).ToHashSet();
            var next = Enumerable.Range(1, layout.Length - 1).Select(k => ClientAudioSystem.PlaceVoiceId(42, k)).ToHashSet();
            Assert.True(!mine.Overlaps(next), $"{key} has {layout.Length} places and shares voice ids with the source beside it");
            // Nor with the next source's own middle-relative ids, whatever its layout.
            Assert.DoesNotContain(ClientAudioSystem.PlaceVoiceId(42, 1), mine);
        }
        _o.WriteLine($"the widest layout has {widest} places; a source has {ExtendedSources.MaxPlaces} ids");
        Assert.True(widest >= 10, "the surf beach's ten places were not checked");
        Assert.Equal(ExtendedSources.MaxPlaces, ClientAudioSystem.PlaceIdsPerSource);
    }
}
