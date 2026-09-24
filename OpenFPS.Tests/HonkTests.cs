using System;
using System.Linq;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A honk on the wire and in the hand: the key that carries it, the rhythm the client plays, and
/// the rhythms drivers use. Written against the mutation run of 2026-09-24, where 30 of Honk's
/// mutants survived and 21 were never run: BlowingAt, which the horn voice plays from, had no test.
/// </summary>
public class HonkTests
{
    [Fact]
    public void AKeyCarriesTheHornAndTheRhythmBothWays()
    {
        var pattern = new[] { 0.15f, 0.12f, 0.2f };
        string key = Honk.Key("air:truck_dual", pattern);
        Assert.Equal("horn:air:truck_dual:0.15,0.12,0.2", key);
        Assert.True(Honk.TryParse(key, out var horn, out var back));
        Assert.Equal("air:truck_dual", horn);
        Assert.Equal(pattern, back);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("siren:electric:disc_pair:0.2")]      // not a horn
    [InlineData("horn:0.2")]                          // no horn named
    [InlineData("horn::0.2")]                         // empty horn name
    [InlineData("horn:electric:disc_pair:")]          // no rhythm
    [InlineData("horn:electric:disc_pair:0.2,x")]     // a rhythm that is not numbers
    public void AMalformedKeyIsRefusedAndLeavesNothingBehind(string? key)
    {
        Assert.False(Honk.TryParse(key, out var horn, out var pattern));
        Assert.Equal("", horn);
        Assert.Empty(pattern);
    }

    [Fact]
    public void ARhythmIsClampedToSomethingAHandCouldDo()
    {
        Assert.True(Honk.TryParse("horn:electric:disc_pair:-1,99", out _, out var p));
        Assert.Equal(new[] { 0f, 20f }, p);
    }

    [Fact]
    public void AHornsLevelIsItsOwnModelsAnchor()
    {
        Assert.Equal(ChimeHornSpec.ByName("truck_dual").ReferenceDb, Honk.LevelDb("air:truck_dual"));
        Assert.Equal(ElectricHornSpec.ByName("disc_pair").ReferenceDb, Honk.LevelDb("electric:disc_pair"));
        Assert.Equal(ElectricHornSpec.ByName("moto_disc").ReferenceDb, Honk.LevelDb("ELECTRIC:moto_disc"));
        // Nothing it can name: a plausible horn, not silence and not the ceiling.
        Assert.Equal(110f, Honk.LevelDb("air:no_such_horn"));
        Assert.Equal(110f, Honk.LevelDb("klaxon:disc_pair"));
        Assert.Equal(110f, Honk.LevelDb("disc_pair"));
        Assert.NotEqual(110f, Honk.LevelDb("air:truck_dual"));
    }

    [Fact]
    public void ARhythmLastsAsLongAsItsPartsAddUpTo()
        => Assert.Equal(0.47f, Honk.Duration(new[] { 0.15f, 0.12f, 0.2f }), 4);

    [Fact]
    public void TheHandIsOnTheHornInTheOnPartsOnly()
    {
        var p = new[] { 0.15f, 0.12f, 0.2f };
        Assert.False(Honk.BlowingAt(p, -0.01f));
        Assert.True(Honk.BlowingAt(p, 0f));
        Assert.True(Honk.BlowingAt(p, 0.14f));
        Assert.False(Honk.BlowingAt(p, 0.16f));   // the gap
        Assert.False(Honk.BlowingAt(p, 0.26f));
        Assert.True(Honk.BlowingAt(p, 0.28f));     // the second press
        Assert.True(Honk.BlowingAt(p, 0.46f));
        Assert.False(Honk.BlowingAt(p, 0.48f));    // let go
        Assert.False(Honk.BlowingAt(Array.Empty<float>(), 0f));
    }

    /// <summary>Everyday honks: a tap most often (about 55%), two quick ones next (30%), and now and
    /// then a lean on it (15%). Taps are 0.16 s give or take a fifth; a lean is 0.6 to 1.4 s.</summary>
    [Fact]
    public void EverydayHonksAreMostlyTapsSometimesDoublesAndNowAndThenALean()
    {
        var rng = new Random(7);
        int taps = 0, doubles = 0, leans = 0;
        const int n = 4000;
        for (int i = 0; i < n; i++)
        {
            var p = Honk.Everyday(rng);
            if (p.Length == 3)
            {
                doubles++;
                Assert.InRange(p[0], 0.14f * 0.8f, 0.14f * 1.2f);
                Assert.InRange(p[1], 0.12f * 0.8f, 0.12f * 1.2f);
                Assert.InRange(p[2], 0.18f * 0.8f, 0.18f * 1.2f);
            }
            else if (p[0] < 0.3f) { taps++; Assert.InRange(p[0], 0.16f * 0.8f, 0.16f * 1.2f); }
            else { leans++; Assert.InRange(p[0], 0.6f, 1.4f); }
        }
        Assert.InRange(taps / (double)n, 0.51, 0.59);
        Assert.InRange(doubles / (double)n, 0.26, 0.34);
        Assert.InRange(leans / (double)n, 0.12, 0.18);
    }

    /// <summary>After a fright: a long one (60%), or a long one with a short one after it. Never a tap.</summary>
    [Fact]
    public void AStartledDriverLeansOnItAndNeverTaps()
    {
        var rng = new Random(11);
        int longs = 0;
        const int n = 4000;
        for (int i = 0; i < n; i++)
        {
            var p = Honk.Startled(rng);
            if (p.Length == 1) { longs++; Assert.InRange(p[0], 0.8f, 1.5f); }
            else
            {
                Assert.Equal(3, p.Length);
                Assert.InRange(p[0], 0.5f, 0.8f);
                Assert.Equal(0.15f, p[1]);
                Assert.Equal(0.25f, p[2]);
            }
        }
        Assert.InRange(longs / (double)n, 0.56, 0.64);
    }

    /// <summary>Long, long, short, long, the last held to the crossing and never shorter than the
    /// other longs.</summary>
    [Fact]
    public void ATrainSoundsLongLongShortLongForACrossing()
    {
        var p = Honk.Crossing(8f);
        var on = p.Where((_, i) => i % 2 == 0).ToArray();
        Assert.Equal(new[] { 3f, 3f, 1f, 8f }, on);
        Assert.Equal(3f, Honk.Crossing(1f)[^1]);
    }
}
