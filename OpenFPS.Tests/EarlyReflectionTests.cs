using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A room answering with its own surfaces, rather than with a blanket.
///
/// Asked for directly, 2026-09-18, after a session in a map made of walls that produced no reflections
/// at all: "shouldn't it just be the natural reflections off the surfaces of the roof walls and such
/// rather than a blanket reverb? the reflections themselves should cause the reverb naturally right?"
/// — and, from outside the same room, "I don't hear the room reflections when I'm outside the room
/// facing the room coming through the doorway".
///
/// These hold the image-source model to the things that make it a model and not a sound effect: the
/// copy is where the geometry says, it arrives when the distance says, it is missing what the material
/// took, and it is not there at all when there is nothing to come off.
/// </summary>
public class EarlyReflectionTests
{
    private static readonly Quaternion Q = Quaternion.Identity;
    private readonly List<EarlyReflections.Arrival> _found = new();

    private static EarlyReflections.Solid Wall(Vector3 centre, Vector3 size, string material = "Concrete")
        => new(centre, size, Q, material);

    /// <summary>
    /// The mirror. A source 3 m in front of a wall, a listener beside it: the copy stands 3 m BEHIND
    /// the wall, and its path is longer than the direct one by exactly the detour.
    /// </summary>
    [Fact]
    public void AReflectionIsTheSourceMirroredThroughTheWall()
    {
        // A wall in the x = 0 plane, facing +x. Everything lives at positive x.
        var wall = Wall(new Vector3(-0.25f, 2f, 0f), new Vector3(0.5f, 6f, 40f));
        var source = new Vector3(3f, 1.5f, -2f);
        var listener = new Vector3(3f, 1.5f, 2f);

        EarlyReflections.Find(source, listener, new[] { wall }, _found);

        var a = Assert.Single(_found);
        // Mirrored through the face at x = 0: same y and z, x negated.
        Assert.Equal(-source.X, a.ImagePosition.X, 2);
        Assert.Equal(source.Y, a.ImagePosition.Y, 2);
        Assert.Equal(source.Z, a.ImagePosition.Z, 2);

        // The bounce point is on the wall, and the two legs add up to the reported path.
        Assert.Equal(0f, a.HitPoint.X, 2);
        Assert.Equal(Vector3.Distance(source, a.HitPoint) + Vector3.Distance(a.HitPoint, listener),
                     a.PathLength, 3);

        // And it arrives later by the extra distance over the speed of sound.
        float direct = Vector3.Distance(source, listener);
        Assert.Equal((a.PathLength - direct) / 343f, a.ExtraDelaySeconds, 4);
        Assert.True(a.ExtraDelaySeconds > 0f);
    }

    /// <summary>
    /// What the wall is made of is what is missing from the copy. Nothing about this is per-material
    /// code: carpet and concrete differ because the registry says they do.
    /// </summary>
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

        // Carpet takes half the mid band and concrete takes two percent of it, which is 5.9 dB between
        // them — the registry's numbers, not a threshold picked to make this pass.
        float downDb = 20f * MathF.Log10(dull.GainMid / hard.GainMid);
        Assert.True(downDb < -5f,
            $"carpet returned {dull.GainMid:F3} against concrete's {hard.GainMid:F3} ({downDb:F1} dB)");
        // And it is duller, not merely quieter: carpet takes the top off hardest.
        Assert.True(dull.GainHigh / dull.GainLow < hard.GainHigh / hard.GainLow);
    }

    /// <summary>An open field has nothing to be heard off. This is the case a blanket reverb could not
    /// express and the reason a field used to sound like a room.</summary>
    [Fact]
    public void NothingToReflectOffIsNoReflections()
    {
        EarlyReflections.Find(new Vector3(0, 1.5f, -5f), new Vector3(0, 1.7f, 5f),
                              Array.Empty<EarlyReflections.Solid>(), _found);
        Assert.Empty(_found);
    }

    /// <summary>
    /// A wall the sound would have to pass through something to reach does not answer.
    ///
    /// Both legs are checked, not just the one arriving at the ear: a facade behind a building is not
    /// reflecting anything at you, and neither is one whose view of the SOURCE is blocked.
    /// </summary>
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

    /// <summary>
    /// A surface is only a mirror on the side you are standing on. A listener inside a box hears its
    /// inner faces; one outside hears the outer ones, and neither hears through it.
    /// </summary>
    [Fact]
    public void AMirrorHasNoBack()
    {
        // A slab with the source on one side and the listener on the other.
        var slab = Wall(new Vector3(0f, 2f, 0f), new Vector3(0.5f, 6f, 40f));
        EarlyReflections.Find(new Vector3(-3f, 1.5f, 0f), new Vector3(3f, 1.5f, 0f), new[] { slab }, _found);

        foreach (var a in _found)
            Assert.True(Vector3.Distance(a.HitPoint, new Vector3(3f, 1.5f, 0f)) < 60f);
        // Nothing may come off the face the listener cannot see.
        Assert.DoesNotContain(_found, a => a.HitPoint.X < 0f);
    }

    /// <summary>
    /// A room answers from several directions at once, and a corridor from its two sides — the property
    /// that makes a space legible by ear rather than merely reverberant.
    /// </summary>
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

        // Every arrival is a distinct surface — the same wall must not answer twice.
        var seen = new HashSet<int>();
        foreach (var a in _found) Assert.True(seen.Add(a.SurfaceId), "one surface produced two arrivals");

        // What survives is ordered by SURFACE, not by loudness, so a slot means the same wall from
        // one tick to the next. Two arrivals of nearly equal strength would otherwise swap places on
        // the smallest movement, and a swap hands each voice the other one's reflection.
        for (int i = 1; i < _found.Count; i++)
            Assert.True(_found[i - 1].SurfaceId < _found[i].SurfaceId);
    }

    /// <summary>
    /// A surface keeps its identity as the listener moves, so its reflection keeps one voice instead of
    /// being torn down and started again every frame — which is a click per frame.
    /// </summary>
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

    /// <summary>
    /// The same geometry measures the same every time. The generator this replaced jittered each
    /// surface normal with a fresh random seed per call, so a wall's reflection moved every frame.
    /// </summary>
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

    /// <summary>
    /// The budget keeps the LOUDEST arrivals, even though the survivors come back in surface order —
    /// the two orderings are separate steps and a reader should not have to take that on trust.
    /// </summary>
    [Fact]
    public void TheBudgetDropsTheQuietestNotTheNearest()
    {
        // The six hard faces of a room, plus one soft wall a long way off. There are more candidates
        // than slots, so something has to go, and the distant carpet is what nobody would have heard.
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
    /// A reflection inside a small room is NOT an event of its own, and must never become a voice.
    ///
    /// This is the fault that made a megaphone repeat itself: "the megaphone is like repeating echoing,
    /// not an environmental reverb... if I stand by the megaphone, I hear it repeat softer but in the
    /// same place". Every arrival in a ten-metre room is inside thirty milliseconds, which is well
    /// under the window in which the ear fuses a copy with the sound it is a copy of — so a listener
    /// should hear one wider, slightly coloured event, and instead heard a second playback of the
    /// announcement. The geometry is right and it is the RENDERING decision that was missing.
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

        Assert.NotEmpty(_found);                       // the room still answers — they are real
        foreach (var a in _found)
        {
            Assert.True(a.ExtraDelaySeconds < EarlyReflections.FusionSeconds,
                $"a ten-metre room produced a {a.ExtraDelaySeconds * 1000:F0} ms arrival");
            Assert.False(EarlyReflections.IsSeparateEvent(a), "nothing in a small room may become a voice");
        }
    }

    /// <summary>
    /// And a wall a long way off IS an event of its own — the slapback a stand across a racetrack or a
    /// facade down a street gives back, which is the case discrete voices exist for.
    /// </summary>
    [Fact]
    public void ADistantWallIsASeparateEvent()
    {
        // A stand 20 m behind the listener: the copy travels some 40 m further than the direct sound,
        // which is over a tenth of a second and unmistakably its own arrival.
        var stand = Wall(new Vector3(0f, 6f, 20f), new Vector3(200f, 12f, 4f));
        EarlyReflections.Find(new Vector3(0f, 0.5f, -5f), new Vector3(0f, 1.7f, 0f), new[] { stand }, _found);

        var a = Assert.Single(_found);
        Assert.True(EarlyReflections.IsSeparateEvent(a),
            $"a stand thirty metres away answered {a.ExtraDelaySeconds * 1000:F0} ms later and was called fused");
    }

    /// <summary>A copy that travelled further arrives quieter, in the proportion the extra distance
    /// costs — the same inverse law the direct sound obeys, applied to a longer path.</summary>
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
