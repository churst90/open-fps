using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The box-driven early reflection search, held to the edges Stryker found untested (2026-09-24):
/// the audibility floor, the second leg's line of sight, the copies of copies and their limits, and
/// the scratch state that must not carry one scene into the next.
///
/// The street used throughout: two concrete walls ten metres apart, inner faces at x = -5 and x = +5,
/// a source at the origin and a listener twelve metres down the street. First-order bounces land at
/// z = 6; A-then-B lands on A at 3 and B at 9; the third order A-B-A lands on A at 2 and 10 and on B
/// at 6. The image of an n-th order copy stands 10·n metres across, so its path is sqrt((10n)² + 12²).
/// </summary>
public class EarlyReflectionsMutationTests
{
    private static readonly Quaternion Q = Quaternion.Identity;
    private readonly List<EarlyReflections.Arrival> _found = new();

    // Concrete keeps 1 - absorption per band: 0.99 low, 0.98 mid, 0.98 high.
    private const float KeepLow = 0.99f, KeepMid = 0.98f;

    private static EarlyReflections.Solid Box(Vector3 centre, Vector3 size, string material = "Concrete")
        => new(centre, size, Q, material);

    /// <summary>Wall A: inner face at x = -5, running z = -2..14.</summary>
    private static EarlyReflections.Solid LongA => Box(new Vector3(-5.5f, 0, 6f), new Vector3(1f, 10f, 16f));

    /// <summary>Wall B: inner face at x = +5, only z = 4.8..9.6 — it holds the first-order bounce at 6,
    /// A-then-B's at 9 and A-B-A's at 6, and misses B-then-A's at 3 and B-A-B's at 2.</summary>
    private static EarlyReflections.Solid ShortB => Box(new Vector3(5.5f, 0, 7.2f), new Vector3(1f, 10f, 4.8f));

    private static readonly Vector3 Source = Vector3.Zero;
    private static readonly Vector3 Listener = new(0, 0, 12f);

    /// <summary>No scene is no reflections — including no leftovers from whatever the list held.</summary>
    [Fact]
    public void NoSolidsClearsTheListAndFindsNothing()
    {
        _found.Add(new EarlyReflections.Arrival(Vector3.One, Vector3.One, 1f, 0.1f, 1f, 1f, 1f, 0f, 1));
        EarlyReflections.Find(Source, Listener, null!, _found);
        Assert.Empty(_found);
    }

    /// <summary>
    /// A copy that has travelled forty times as far as the direct sound arrives at 2.5 % of it, under
    /// <see cref="EarlyReflections.MinRelativeAmplitude"/>, and is not reported. The same wall with the
    /// pair four times further apart gives a 10 % copy, which is, at keep × direct/path in each band.
    /// </summary>
    [Fact]
    public void ACopyUnderTheAudibilityFloorIsNotReported()
    {
        var wall = Box(new Vector3(10.5f, 0, 0), new Vector3(1f, 40f, 40f));

        EarlyReflections.Find(new Vector3(0, 0, -0.25f), new Vector3(0, 0, 0.25f), new[] { wall }, _found);
        Assert.Empty(_found);

        EarlyReflections.Find(new Vector3(0, 0, -1f), new Vector3(0, 0, 1f), new[] { wall }, _found);
        var a = Assert.Single(_found);
        float path = MathF.Sqrt(20f * 20f + 2f * 2f);
        Assert.Equal(path, a.PathLength, 3);
        Assert.Equal(KeepLow * 2f / path, a.GainLow, 4);
        Assert.Equal(KeepMid * 2f / path, a.GainMid, 4);
    }

    /// <summary>
    /// A post standing between the wall and the listener — clear of the source's leg to the wall and
    /// of the direct path — hides the reflection. Take the post away and it is back.
    /// </summary>
    [Fact]
    public void ABlockedSecondLegHidesTheReflection()
    {
        var wall = Box(new Vector3(10.5f, 0, 0), new Vector3(1f, 20f, 40f));
        var post = Box(new Vector3(7.5f, 0, 2.5f), new Vector3(1f, 4f, 1f));
        var source = new Vector3(5f, 0, -5f);
        var listener = new Vector3(5f, 0, 5f);

        EarlyReflections.Find(source, listener, new[] { wall }, _found);
        Assert.Single(_found);

        EarlyReflections.Find(source, listener, new[] { wall, post }, _found);
        Assert.Empty(_found);
    }

    /// <summary>
    /// The copies of copies in the street, to second order: the two first-order bounces and A-then-B,
    /// whose image stands 20 m across at (10, 0, 0) mirrored twice — (-10, 0, 0) through A, (20, 0, 0)
    /// through B — at keep² × 12/sqrt(20² + 12²) and a last bounce on B at (5, 0, 9). B-then-A would
    /// have to bounce off B at z = 3, where the short B is not, so it is not there; and asking for
    /// second order brings no third.
    /// </summary>
    [Fact]
    public void SecondOrderFindsOnlyTheChainsWhoseBouncesAllLandOnTheirFaces()
    {
        EarlyReflections.Find(Source, Listener, new[] { LongA, ShortB }, _found, maxOrder: 2);

        Assert.Equal(3, _found.Count);
        Assert.Equal(2, _found.Count(a => a.Order == 1));
        var second = Assert.Single(_found, a => a.Order == 2);

        float path = MathF.Sqrt(20f * 20f + 12f * 12f);
        Assert.Equal(path, second.PathLength, 3);
        Assert.Equal((path - 12f) / 343f, second.ExtraDelaySeconds, 5);
        Assert.Equal(KeepLow * KeepLow * 12f / path, second.GainLow, 4);
        Assert.Equal(KeepMid * KeepMid * 12f / path, second.GainMid, 4);
        Assert.True(Vector3.Distance(new Vector3(20f, 0, 0), second.ImagePosition) < 1e-3f);
        Assert.True(Vector3.Distance(new Vector3(5f, 0, 9f), second.HitPoint) < 1e-3f);
    }

    /// <summary>
    /// And to third order: A-B-A lands on A at 2 and 10 and on B at 6, all on their faces, so it is
    /// there — image 30 m across at (-30, 0, 0), keep³ × 12/sqrt(30² + 12²), last bounce on A at
    /// (-5, 0, 10). B-A-B would need B at 2 and is not.
    /// </summary>
    [Fact]
    public void ThirdOrderAddsTheThreeBounceChainAndOnlyIt()
    {
        EarlyReflections.Find(Source, Listener, new[] { LongA, ShortB }, _found, maxOrder: 3);

        Assert.Equal(4, _found.Count);
        Assert.Single(_found, a => a.Order == 2);
        var third = Assert.Single(_found, a => a.Order == 3);

        float path = MathF.Sqrt(30f * 30f + 12f * 12f);
        Assert.Equal(path, third.PathLength, 3);
        Assert.Equal(KeepMid * KeepMid * KeepMid * 12f / path, third.GainMid, 4);
        Assert.True(Vector3.Distance(new Vector3(-30f, 0, 0), third.ImagePosition) < 1e-3f);
        Assert.True(Vector3.Distance(new Vector3(-5f, 0, 10f), third.HitPoint) < 1e-3f);
    }

    /// <summary>
    /// A copy of a copy that has gone past <see cref="EarlyReflections.RangeMetres"/> is not reported
    /// even when it would be loud enough: walls 110 m apart and a listener 30 m down the street give
    /// first-order paths of 114 m and second-order ones of 222 m.
    /// </summary>
    [Fact]
    public void ACopyOfACopyPastTheSearchRangeIsNotReported()
    {
        var a = Box(new Vector3(-55.5f, 0, 15f), new Vector3(1f, 20f, 200f));
        var b = Box(new Vector3(55.5f, 0, 15f), new Vector3(1f, 20f, 200f));
        var listener = new Vector3(0, 0, 30f);
        // Loud enough to keep if it were in range: 30/222 of the direct sound, twice through concrete.
        Assert.True(KeepMid * KeepMid * 30f / MathF.Sqrt(220f * 220f + 900f) > EarlyReflections.MinRelativeAmplitude);

        EarlyReflections.Find(Source, listener, new[] { a, b }, _found, maxOrder: 2);
        Assert.Equal(2, _found.Count);
        Assert.All(_found, x => Assert.Equal(1, x.Order));
    }

    /// <summary>
    /// A copy of a copy under the audibility floor is not reported: walls 40 m apart and a listener
    /// 3 m down the street. First order is 3/40 of the direct sound — kept; second order is 3/80 of it
    /// — not.
    /// </summary>
    [Fact]
    public void AQuietCopyOfACopyIsNotReported()
    {
        var a = Box(new Vector3(-20.5f, 0, 1.5f), new Vector3(1f, 10f, 20f));
        var b = Box(new Vector3(20.5f, 0, 1.5f), new Vector3(1f, 10f, 20f));

        EarlyReflections.Find(Source, new Vector3(0, 0, 3f), new[] { a, b }, _found, maxOrder: 2);
        Assert.Equal(2, _found.Count);
        Assert.All(_found, x => Assert.Equal(1, x.Order));
    }

    /// <summary>
    /// One wall cannot hand a sound back to itself, and what the last scene's walls could do is not
    /// this scene's business: after a street that produces second-order copies, a search of a single
    /// wall produces none.
    /// </summary>
    [Fact]
    public void TheMirrorsOfTheLastSearchDoNotLeakIntoTheNext()
    {
        EarlyReflections.Find(Source, Listener, new[] { LongA, ShortB }, _found, maxOrder: 3);
        Assert.Contains(_found, a => a.Order >= 2);

        EarlyReflections.Find(Source, Listener, new[] { LongA }, _found, maxOrder: 3);
        var only = Assert.Single(_found);
        Assert.Equal(1, only.Order);
    }
}
