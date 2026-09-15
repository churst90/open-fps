using System;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// Cover for the two things that turn geometry into sound a player can navigate by: a facade that
/// answers a shot, and a window that falls to the ground after you break it.
///
/// Both encode a distance in a DELAY, which is the pattern this whole engine keeps returning to — the
/// crack-to-report gap, the reflection's extra path, the time glass takes to fall. Each is a real
/// physical quantity that a sighted game throws away and a blind player can read directly. So these
/// hold the timings to the physics rather than to anyone's taste.
/// </summary>
public class ReflectionAndGlassTests
{
    private const float C = 343f;

    /// <summary>A single wall in the x = 0 plane facing +X, ten metres square.</summary>
    private static ReflectingSurface Wall(float x = 0f, float half = 10f) => new(
        Centre: new Vector3(x, 0, 0),
        Normal: Vector3.UnitX,
        HalfU: new Vector3(0, half, 0),
        HalfV: new Vector3(0, 0, half),
        Absorption: 0.02f,
        SurfaceId: 1);

    // ── Reflections ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AReflectionArrivesLateAndFromTheMirroredDirection()
    {
        // Ten metres off the wall, not five: at five the reflected path is only 3.6 m longer than the
        // direct one, which is 10.6 ms — inside MinDelaySeconds, where the ear fuses the two. The
        // solver is right to drop it and the first version of this test was wrong to expect it.
        var surfaces = new[] { Wall() };
        var source = new Vector3(10f, 0f, -6f);
        var listener = new Vector3(10f, 0f, 6f);

        Span<Reflection> into = stackalloc Reflection[4];
        int n = ImageSource.FirstOrder(surfaces, source, listener, C, into);

        Assert.Equal(1, n);
        // The image is the source mirrored through the wall: same y and z, x negated about the plane.
        Assert.Equal(-10f, into[0].ApparentPosition.X, 3);
        Assert.Equal(source.Z, into[0].ApparentPosition.Z, 3);
        // It bounced ON the wall, and the path is longer than the direct one, so it arrives later.
        Assert.Equal(0f, into[0].BouncePoint.X, 3);
        Assert.True(into[0].PathLength > Vector3.Distance(source, listener));
        Assert.True(into[0].DelaySeconds > 0f);

        // And the delay is exactly the extra distance at the speed of sound.
        float extra = into[0].PathLength - Vector3.Distance(source, listener);
        Assert.Equal(extra / C, into[0].DelaySeconds, 4);
    }

    [Fact]
    public void AGapInTheWallRemovesItsAnswer()
    {
        // The bounce point has to be somewhere the wall ISN'T. Placed symmetrically the bounce lands
        // dead centre, so shrinking the wall about its centre never moves off it — which is what the
        // first version of this test did, and it proved nothing. Offsetting the source puts the bounce
        // at z = -7, so a wall spanning +/-10 contains it and one spanning +/-3 does not.
        var listener = new Vector3(10f, 0f, 6f);
        var source = new Vector3(10f, 0f, -20f);

        Span<Reflection> into = stackalloc Reflection[4];
        int wide = ImageSource.FirstOrder(new[] { Wall(half: 10f) }, source, listener, C, into);
        Assert.Equal(1, wide);
        Assert.InRange(into[0].BouncePoint.Z, -9f, -5f);

        // Same wall, same shot, a gap where the bounce would have been: silence rather than a quieter
        // reflection. That absence is what a side street sounds like.
        Assert.Equal(0, ImageSource.FirstOrder(new[] { Wall(half: 3f) }, source, listener, C, into));
    }

    [Fact]
    public void NothingReflectsOffTheBackOfAWall()
    {
        // A listener behind the wall is not hearing a reflection off it; mirroring through a face
        // neither party can see invents an arrival out of nothing.
        var surfaces = new[] { Wall() };
        Span<Reflection> into = stackalloc Reflection[4];
        Assert.Equal(0, ImageSource.FirstOrder(surfaces, new Vector3(10f, 0f, -6f),
                                               new Vector3(-10f, 0f, 6f), C, into));
    }

    [Fact]
    public void CoincidentReflectionsDoNotGetTheirOwnVoice()
    {
        // The ground under a long shot: a path a few centimetres longer than the direct one. The ear
        // fuses that with the direct sound rather than hearing an echo, and rendering it separately
        // just doubles the level and comb-filters it.
        var ground = new ReflectingSurface(
            Centre: new Vector3(0, 0, 0), Normal: Vector3.UnitY,
            HalfU: new Vector3(400, 0, 0), HalfV: new Vector3(0, 0, 400),
            Absorption: 0.02f, SurfaceId: 9);

        Span<Reflection> into = stackalloc Reflection[4];
        int n = ImageSource.FirstOrder(new[] { ground },
                                       new Vector3(0, 1.5f, 178f), new Vector3(0, 1.7f, 0f), C, into);
        Assert.Equal(0, n);
    }

    [Fact]
    public void TwoParallelWallsFlutter()
    {
        // The sound of a street. One bounce off each wall gives two arrivals; the wall-to-wall paths
        // give the repeating slap that a reverb decay cannot, because each of these has a direction.
        var left = new ReflectingSurface(new Vector3(-12, 0, 0), Vector3.UnitX,
                                         new Vector3(0, 13, 0), new Vector3(0, 0, 60), 0.02f, 1);
        var right = new ReflectingSurface(new Vector3(12, 0, 0), -Vector3.UnitX,
                                          new Vector3(0, 13, 0), new Vector3(0, 0, 60), 0.02f, 2);
        var walls = new[] { left, right };
        var source = new Vector3(0f, 1.5f, 40f);
        var listener = new Vector3(0f, 1.7f, 0f);

        Span<Reflection> first = stackalloc Reflection[8];
        Span<Reflection> second = stackalloc Reflection[8];
        int f = ImageSource.FirstOrder(walls, source, listener, C, first);
        int s = ImageSource.SecondOrder(walls, source, listener, C, second);

        Assert.Equal(2, f);
        Assert.True(s > 0, "two parallel walls must produce wall-to-wall paths");
        // Every second-order path is longer, later and quieter than the first-order ones.
        for (int i = 0; i < s; i++)
        {
            Assert.True(second[i].DelaySeconds > first[0].DelaySeconds);
            Assert.True(second[i].Gain < first[0].Gain);
        }
    }

    [Fact]
    public void TheStrongestReflectionsAreKeptWhenThereAreMoreThanVoices()
    {
        var walls = new ReflectingSurface[6];
        for (int i = 0; i < walls.Length; i++)
            walls[i] = new ReflectingSurface(new Vector3(-(8 + i * 6), 0, 0), Vector3.UnitX,
                                             new Vector3(0, 20, 0), new Vector3(0, 0, 60), 0.02f, i);

        Span<Reflection> into = stackalloc Reflection[2];
        int n = ImageSource.FirstOrder(walls, new Vector3(0, 1.5f, 30f), new Vector3(0, 1.7f, 0f), C, into);
        Assert.True(n <= 2);
        if (n == 2) Assert.True(into[0].Gain >= into[1].Gain, "strongest first");
    }

    // ── Glass ───────────────────────────────────────────────────────────────────────────────────

    private static GlassPane Pane(GlassType type, float height) => new(
        Centre: new Vector3(0f, height + 1f, 20f),
        Size: new Vector2(1.4f, 1.2f),
        Normal: -Vector3.UnitZ,
        Type: type,
        HeightAboveGround: height);

    [Fact]
    public void TheFallDelayEncodesTheHeightItFellFrom()
    {
        // The whole reason to model this: the gap between the break and the glass landing is a direct
        // readout of which floor the window was on.
        foreach (float h in new[] { 3f, 9f, 15f, 24f })
        {
            float t = GlassBreak.FallSeconds(h);
            Assert.True(t > 0f);
            Assert.Equal(h, GlassBreak.HeightFromFallDelay(t), 2);
        }
        // Higher takes longer, and a pane at ground level does not fall at all.
        Assert.True(GlassBreak.FallSeconds(20f) > GlassBreak.FallSeconds(5f));
        Assert.Equal(0f, GlassBreak.FallSeconds(0f));
    }

    [Fact]
    public void GlassLandsAtTheFootOfTheWallNotAtTheWindow()
    {
        var pane = Pane(GlassType.Tempered, 15f);
        Span<GlassEvent> events = stackalloc GlassEvent[GlassBreak.MaxEventsPerBreak];
        int n = GlassBreak.Resolve(pane, pane.Centre, WeaponRegistry.Akm, seed: 3, events);

        bool sawLanding = false;
        for (int i = 0; i < n; i++)
        {
            if (events[i].Kind != GlassEventKind.Landing) continue;
            sawLanding = true;
            // Down at the pavement, not up at the window: that height difference is the cue.
            Assert.True(events[i].Position.Y < pane.Centre.Y - 10f,
                        $"landing at y={events[i].Position.Y:F1}, window at {pane.Centre.Y:F1}");
            Assert.True(events[i].DelaySeconds >= GlassBreak.FallSeconds(15f));
        }
        Assert.True(sawLanding, "a pane fifteen metres up must reach the ground");
    }

    [Fact]
    public void TemperedAlwaysGoesLaminatedNeverDoes()
    {
        // Not a balance decision — it is what the materials do. Tempered glass is held in compression
        // and fails entirely once breached; laminated is bonded to a plastic layer and keeps its
        // pieces. A player can learn to tell a car window from a windscreen by whether glass arrives.
        Assert.True(GlassBreak.Shatters(GlassType.Tempered, WeaponRegistry.Ar15));
        Assert.True(GlassBreak.Shatters(GlassType.Tempered, WeaponRegistry.Glock));
        Assert.False(GlassBreak.Shatters(GlassType.Laminated, WeaponRegistry.Shotgun));

        // Annealed depends on what hit it: buckshot destroys it, a fast rifle round drills it.
        Assert.True(GlassBreak.Shatters(GlassType.Annealed, WeaponRegistry.Shotgun));
        Assert.False(GlassBreak.Shatters(GlassType.Annealed, WeaponRegistry.Ar15));
        Assert.True(GlassBreak.Shatters(GlassType.Annealed, WeaponRegistry.ServicePistol));
    }

    [Fact]
    public void APaneThatDoesNotShatterMakesOneSoundAndNoFall()
    {
        var pane = Pane(GlassType.Laminated, 12f);
        Span<GlassEvent> events = stackalloc GlassEvent[GlassBreak.MaxEventsPerBreak];
        int n = GlassBreak.Resolve(pane, pane.Centre, WeaponRegistry.Akm, seed: 1, events);

        Assert.Equal(1, n);
        Assert.Equal(GlassEventKind.Puncture, events[0].Kind);
        // The SILENCE where a listener expected glass to arrive is the information.
        for (int i = 0; i < n; i++) Assert.NotEqual(GlassEventKind.Landing, events[i].Kind);
    }

    [Fact]
    public void TheBreakComesBeforeTheShowerWhichComesBeforeTheLanding()
    {
        var pane = Pane(GlassType.Annealed, 18f);
        Span<GlassEvent> events = stackalloc GlassEvent[GlassBreak.MaxEventsPerBreak];
        int n = GlassBreak.Resolve(pane, pane.Centre, WeaponRegistry.Shotgun, seed: 11, events);

        float shatter = -1f, firstShard = float.MaxValue, firstLanding = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            switch (events[i].Kind)
            {
                case GlassEventKind.Shatter: shatter = events[i].DelaySeconds; break;
                case GlassEventKind.Shard: firstShard = MathF.Min(firstShard, events[i].DelaySeconds); break;
                case GlassEventKind.Landing: firstLanding = MathF.Min(firstLanding, events[i].DelaySeconds); break;
            }
        }
        Assert.Equal(0f, shatter);
        Assert.True(firstShard > shatter);
        Assert.True(firstLanding > firstShard, "glass cannot land before it has left the frame");
        Assert.True(firstLanding >= GlassBreak.FallSeconds(18f));
    }

    [Fact]
    public void BreakingIsDeterministic()
    {
        // The server and every client must agree about where the glass went.
        var pane = Pane(GlassType.Tempered, 9f);
        Span<GlassEvent> a = stackalloc GlassEvent[GlassBreak.MaxEventsPerBreak];
        Span<GlassEvent> b = stackalloc GlassEvent[GlassBreak.MaxEventsPerBreak];
        int na = GlassBreak.Resolve(pane, pane.Centre, WeaponRegistry.Glock, 7, a);
        int nb = GlassBreak.Resolve(pane, pane.Centre, WeaponRegistry.Glock, 7, b);
        Assert.Equal(na, nb);
        for (int i = 0; i < na; i++) Assert.Equal(a[i], b[i]);
    }

    /// <summary>
    /// A long wall authored as overlapping segments must not answer twice. Both segments' faces
    /// contain the bounce point at the join, so without the duplicate check the same arrival takes
    /// two voices and reads six decibels louder than the wall is.
    /// </summary>
    [Fact]
    public void OverlappingSegmentsOfOneWallAnswerOnce()
    {
        // Two 20 m panels of the same wall at x = -12, overlapping by a metre across x = 0.
        var left = new ReflectingSurface(new Vector3(-12, 3, -4.75f), Vector3.UnitX,
                                         new Vector3(0, 3, 0), new Vector3(0, 0, 10.25f), 0.02f, 1);
        var right = new ReflectingSurface(new Vector3(-12, 3, 4.75f), Vector3.UnitX,
                                          new Vector3(0, 3, 0), new Vector3(0, 0, 10.25f), 0.02f, 2);

        Span<Reflection> into = stackalloc Reflection[6];
        // Source and listener placed so the specular point lands on z = 0 — inside both panels.
        int n = ImageSource.FirstOrder(new[] { left, right },
                                       new Vector3(0, 1.5f, -30f), new Vector3(0, 1.7f, 30f), C, into);
        Assert.Equal(1, n);
    }

    [Fact]
    public void EveryGlassEventHasSomethingToPlay()
    {
        foreach (GlassEventKind k in Enum.GetValues<GlassEventKind>())
            Assert.False(string.IsNullOrWhiteSpace(GlassBreak.SoundFolderFor(k)), $"{k} has no folder");
    }
}
