using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// A room answering with its own surfaces: the copy is where the geometry says, arrives when the
/// distance says, is missing what the material took, and is absent when there is nothing to come off.
/// docs/TEST_NOTES.md, "Early reflections from the surfaces".
/// </summary>
public class EarlyReflectionTests
{
    private static readonly Quaternion Q = Quaternion.Identity;
    private readonly List<EarlyReflections.Arrival> _found = new();

    private static EarlyReflections.Solid Wall(Vector3 centre, Vector3 size, string material = "Concrete")
        => new(centre, size, Q, material);

    /// <summary>The copy stands as far behind the wall as the source is in front, late by the detour.</summary>
    [Fact]
    public void AReflectionIsTheSourceMirroredThroughTheWall()
    {
        // A wall in the x = 0 plane, facing +x.
        var wall = Wall(new Vector3(-0.25f, 2f, 0f), new Vector3(0.5f, 6f, 40f));
        var source = new Vector3(3f, 1.5f, -2f);
        var listener = new Vector3(3f, 1.5f, 2f);

        EarlyReflections.Find(source, listener, new[] { wall }, _found);

        var a = Assert.Single(_found);
        Assert.Equal(-source.X, a.ImagePosition.X, 2);
        Assert.Equal(source.Y, a.ImagePosition.Y, 2);
        Assert.Equal(source.Z, a.ImagePosition.Z, 2);

        Assert.Equal(0f, a.HitPoint.X, 2);
        Assert.Equal(Vector3.Distance(source, a.HitPoint) + Vector3.Distance(a.HitPoint, listener),
                     a.PathLength, 3);

        float direct = Vector3.Distance(source, listener);
        Assert.Equal((a.PathLength - direct) / 343f, a.ExtraDelaySeconds, 4);
        Assert.True(a.ExtraDelaySeconds > 0f);
    }

    /// <summary>The copy is missing what the surface absorbed, by the registry's figures, with no
    /// per-material code.</summary>
    [Fact]
    public void TheCopyIsMissingWhatTheSurfaceAbsorbed()
    {
        var source = new Vector3(3f, 1.5f, -2f);
        var listener = new Vector3(3f, 1.5f, 2f);
        var at = new Vector3(-0.25f, 2f, 0f);
        var size = new Vector3(0.5f, 6f, 40f);

        EarlyReflections.Find(source, listener, new[] { Wall(at, size, "Concrete") }, _found);
        var hard = Assert.Single(_found);

        var soft = new List<EarlyReflections.Arrival>();
        EarlyReflections.Find(source, listener, new[] { Wall(at, size, "Carpet") }, soft);
        var dull = Assert.Single(soft);

        // Carpet takes half the mid band's energy and concrete 2 %: 2.9 dB (10·log10(0.5 / 0.98)). It
        // read 5.9 while absorption was applied as an amplitude (EarlyReflections.Keep).
        float downDb = 20f * MathF.Log10(dull.GainMid / hard.GainMid);
        Assert.True(downDb < -2.5f,
            $"carpet returned {dull.GainMid:F3} against concrete's {hard.GainMid:F3} ({downDb:F1} dB)");
        // Duller, not merely quieter: carpet takes the top off hardest.
        Assert.True(dull.GainHigh / dull.GainLow < hard.GainHigh / hard.GainLow);
    }

    /// <summary>An open field has nothing to answer: a blanket reverb made a field sound like a room.</summary>
    [Fact]
    public void NothingToReflectOffIsNoReflections()
    {
        EarlyReflections.Find(new Vector3(0, 1.5f, -5f), new Vector3(0, 1.7f, 5f),
                              Array.Empty<EarlyReflections.Solid>(), _found);
        Assert.Empty(_found);
    }

    /// <summary>A wall the sound must pass through something to reach does not answer; both legs are
    /// checked, the source's as well as the ear's.</summary>
    [Fact]
    public void AWallBehindSomethingElseDoesNotAnswer()
    {
        var wall = Wall(new Vector3(-0.25f, 2f, 0f), new Vector3(0.5f, 6f, 40f));
        var blocker = Wall(new Vector3(1.5f, 2f, 0f), new Vector3(0.5f, 6f, 40f));
        var source = new Vector3(3f, 1.5f, -2f);
        var listener = new Vector3(3f, 1.5f, 2f);

        EarlyReflections.Find(source, listener, new[] { wall }, _found);
        Assert.Single(_found);           // on its own, it answers

        EarlyReflections.Find(source, listener, new[] { wall, blocker }, _found);
        Assert.DoesNotContain(_found, a => MathF.Abs(a.ImagePosition.X + source.X) < 0.1f);
    }

    /// <summary>A surface is a mirror only on the listener's side; nothing comes through it.</summary>
    [Fact]
    public void AMirrorHasNoBack()
    {
        var slab = Wall(new Vector3(0f, 2f, 0f), new Vector3(0.5f, 6f, 40f));
        EarlyReflections.Find(new Vector3(-3f, 1.5f, 0f), new Vector3(3f, 1.5f, 0f), new[] { slab }, _found);

        foreach (var a in _found)
            Assert.True(Vector3.Distance(a.HitPoint, new Vector3(3f, 1.5f, 0f)) < 60f);
        Assert.DoesNotContain(_found, a => a.HitPoint.X < 0f);
    }

    /// <summary>
    /// A copy at its image arrives, after the engine's distance law, at exactly the share the surfaces
    /// kept, inside the source's reference distance as well as past it. L/d was right only past it, and
    /// with a loud source's reference at 40 m a gunshot's copies came out 8-11 dB hot.
    /// </summary>
    [Theory]
    [InlineData(40f, 2f, 5f)]     // a gunshot, both inside the reference
    [InlineData(40f, 0.5f, 60f)]  // the copy past it, the direct inside
    [InlineData(1.2f, 3f, 7f)]    // a clap, both past it
    [InlineData(1.2f, 0.8f, 3f)]  // a step, the direct inside
    public void ACopyArrivesAtTheShareTheSurfacesKept(float reference, float direct, float path)
    {
        float kept = 0.6f;
        float relative = kept * direct / path;              // what Find reports
        float placed = EarlyReflections.PlacedCopyGain(relative, path, direct, reference);
        float atEarCopy = placed * Loudness.RenderedGain(1f, reference, 1000f, path);
        float atEarDirect = Loudness.RenderedGain(1f, reference, 1000f, direct);
        Assert.Equal(relative, atEarCopy / atEarDirect, 3);
    }

    /// <summary>A listener just behind a thin wall hears nothing off its front face; only the
    /// listener-side plane test guards this case.</summary>
    [Fact]
    public void AListenerBehindAWallHearsNoReflectionOffItsFront()
    {
        var slab = Wall(new Vector3(0f, 2f, 0f), new Vector3(0.5f, 6f, 40f));
        EarlyReflections.Find(new Vector3(5f, 1.5f, -2f), new Vector3(-1f, 1.5f, 2f), new[] { slab }, _found);
        Assert.Empty(_found);
    }

    /// <summary>A room answers from several surfaces at once, each surface once.</summary>
    [Fact]
    public void ARoomAnswersFromMoreThanOneDirection()
    {
        var room = new List<EarlyReflections.Solid>
        {
            Wall(new Vector3(0, -0.25f, 0), new Vector3(10, 0.5f, 10)),     // floor
            Wall(new Vector3(0, 4.25f, 0), new Vector3(10, 0.5f, 10)),      // ceiling
            Wall(new Vector3(0, 2, 5.25f), new Vector3(10, 4, 0.5f)),       // north
            Wall(new Vector3(0, 2, -5.25f), new Vector3(10, 4, 0.5f)),      // south
            Wall(new Vector3(5.25f, 2, 0), new Vector3(0.5f, 4, 10)),       // east
            Wall(new Vector3(-5.25f, 2, 0), new Vector3(0.5f, 4, 10)),      // west
        };

        EarlyReflections.Find(new Vector3(-2f, 1.5f, -2f), new Vector3(2f, 1.7f, 2f), room, _found);

        Assert.True(_found.Count >= 3, $"a hard six-sided room produced {_found.Count} arrival(s)");
        Assert.True(_found.Count <= EarlyReflections.MaxArrivals);

        var seen = new HashSet<int>();
        foreach (var a in _found) Assert.True(seen.Add(a.SurfaceId), "one surface produced two arrivals");

        // Ordered by surface, not loudness, so a slot is the same wall tick to tick: two near-equal
        // arrivals would otherwise swap on the smallest movement and each voice get the other's.
        for (int i = 1; i < _found.Count; i++)
            Assert.True(_found[i - 1].SurfaceId < _found[i].SurfaceId);
    }

    /// <summary>A surface keeps its identity as the listener moves, so its reflection keeps one voice
    /// rather than restarting (a click) every frame.</summary>
    [Fact]
    public void ASurfaceKeepsItsIdentityAsYouMove()
    {
        var wall = Wall(new Vector3(-0.25f, 2f, 0f), new Vector3(0.5f, 6f, 40f));
        var source = new Vector3(3f, 1.5f, -2f);

        EarlyReflections.Find(source, new Vector3(3f, 1.5f, 2f), new[] { wall }, _found);
        int first = Assert.Single(_found).SurfaceId;

        var later = new List<EarlyReflections.Arrival>();
        EarlyReflections.Find(source, new Vector3(3.5f, 1.5f, 2.5f), new[] { wall }, later);
        Assert.Equal(first, Assert.Single(later).SurfaceId);
    }

    /// <summary>The same geometry measures the same every time; the generator this replaced jittered the
    /// normals per call, and a wall's reflection moved every frame.</summary>
    [Fact]
    public void TheSameGeometryAnswersTheSameTwice()
    {
        var room = new List<EarlyReflections.Solid>
        {
            Wall(new Vector3(0, -0.25f, 0), new Vector3(10, 0.5f, 10)),
            Wall(new Vector3(5.25f, 2, 0), new Vector3(0.5f, 4, 10)),
        };
        var s = new Vector3(-2f, 1.5f, -2f);
        var l = new Vector3(2f, 1.7f, 2f);

        EarlyReflections.Find(s, l, room, _found);
        var again = new List<EarlyReflections.Arrival>();
        EarlyReflections.Find(s, l, room, again);

        Assert.Equal(_found.Count, again.Count);
        for (int i = 0; i < _found.Count; i++)
        {
            Assert.Equal(_found[i].SurfaceId, again[i].SurfaceId);
            Assert.Equal(_found[i].ImagePosition.X, again[i].ImagePosition.X, 6);
            Assert.Equal(_found[i].PathLength, again[i].PathLength, 6);
        }
    }

    /// <summary>The budget keeps the loudest arrivals, though the survivors come back in surface order.</summary>
    [Fact]
    public void TheBudgetDropsTheQuietestNotTheNearest()
    {
        // Six hard faces and a distant carpet wall: more candidates than slots, and the carpet goes.
        var room = new List<EarlyReflections.Solid>
        {
            Wall(new Vector3(0, -0.25f, 0), new Vector3(10, 0.5f, 10)),
            Wall(new Vector3(0, 4.25f, 0), new Vector3(10, 0.5f, 10)),
            Wall(new Vector3(0, 2, 5.25f), new Vector3(10, 4, 0.5f)),
            Wall(new Vector3(0, 2, -5.25f), new Vector3(10, 4, 0.5f)),
            Wall(new Vector3(5.25f, 2, 0), new Vector3(0.5f, 4, 10)),
            Wall(new Vector3(-5.25f, 2, 0), new Vector3(0.5f, 4, 10)),
        };
        int softWall = room.Count;
        room.Add(Wall(new Vector3(0, 2, -24f), new Vector3(40, 6, 0.5f), "Carpet"));

        EarlyReflections.Find(new Vector3(-2f, 1.5f, -2f), new Vector3(2f, 1.7f, 2f), room, _found);

        Assert.Equal(EarlyReflections.MaxArrivals, _found.Count);
        foreach (var a in _found)
            Assert.NotInRange(a.SurfaceId, EarlyReflections.SurfaceId(softWall, 0),
                                           EarlyReflections.SurfaceId(softWall, 5));
    }

    /// <summary>
    /// A reflection in a small room is inside the fusion window (every arrival in a ten-metre room is
    /// under 30 ms) and must never become a voice of its own: one that did made the megaphone repeat
    /// itself (docs/REPEATS_AND_POPS.md).
    /// </summary>
    [Fact]
    public void ReflectionsInARoomAreNotSeparateEvents()
    {
        var room = new List<EarlyReflections.Solid>
        {
            Wall(new Vector3(0, -0.25f, 0), new Vector3(10, 0.5f, 10)),
            Wall(new Vector3(0, 4.25f, 0), new Vector3(10, 0.5f, 10)),
            Wall(new Vector3(0, 2, 5.25f), new Vector3(10, 4, 0.5f)),
            Wall(new Vector3(0, 2, -5.25f), new Vector3(10, 4, 0.5f)),
            Wall(new Vector3(5.25f, 2, 0), new Vector3(0.5f, 4, 10)),
            Wall(new Vector3(-5.25f, 2, 0), new Vector3(0.5f, 4, 10)),
        };
        EarlyReflections.Find(new Vector3(-2f, 1.5f, -2f), new Vector3(2f, 1.7f, 2f), room, _found);

        Assert.NotEmpty(_found);                       // the room still answers
        foreach (var a in _found)
        {
            Assert.True(a.ExtraDelaySeconds < EarlyReflections.FusionSeconds,
                $"a ten-metre room produced a {a.ExtraDelaySeconds * 1000:F0} ms arrival");
            Assert.False(EarlyReflections.IsSeparateEvent(a), "nothing in a small room may become a voice");
        }
    }

    /// <summary>A distant wall is an event of its own: the slapback of a stand across a racetrack or a
    /// facade down a street, which discrete voices exist for.</summary>
    [Fact]
    public void ADistantWallIsASeparateEvent()
    {
        // A stand 20 m behind the listener: some 40 m further, over a tenth of a second late.
        var stand = Wall(new Vector3(0f, 6f, 20f), new Vector3(200f, 12f, 4f));
        EarlyReflections.Find(new Vector3(0f, 0.5f, -5f), new Vector3(0f, 1.7f, 0f), new[] { stand }, _found);

        var a = Assert.Single(_found);
        Assert.True(EarlyReflections.IsSeparateEvent(a),
            $"a stand thirty metres away answered {a.ExtraDelaySeconds * 1000:F0} ms later and was called fused");
    }

    /// <summary>A copy that travelled further arrives quieter, by the inverse law over its longer path.</summary>
    [Fact]
    public void AFurtherBounceIsAQuieterOne()
    {
        var near = Wall(new Vector3(-0.25f, 2f, 0f), new Vector3(0.5f, 6f, 40f));
        var far = Wall(new Vector3(-20.25f, 2f, 0f), new Vector3(0.5f, 6f, 40f));
        var source = new Vector3(3f, 1.5f, -2f);
        var listener = new Vector3(3f, 1.5f, 2f);

        EarlyReflections.Find(source, listener, new[] { near }, _found);
        float close = Assert.Single(_found).GainMid;

        var distant = new List<EarlyReflections.Arrival>();
        EarlyReflections.Find(source, listener, new[] { far }, distant);
        Assert.True(Assert.Single(distant).GainMid < close,
            "a wall twenty metres away cannot answer as loudly as one three metres away");
    }
}
