using System;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A vehicle between you and a sound, held to the edges Stryker found untested (2026-09-24): the
/// detour over a roof worked out by hand, the barrier being the same whichever way the sound goes
/// and wherever the scene stands, what is and is not a barrier, and how two of them add up.
/// </summary>
public class VehicleShadowMutationTests
{
    private static readonly Quaternion Straight = Quaternion.Identity;
    private static readonly Vector3 Bus = new(2.5f, 3.2f, 12f);

    private static float D(Vector3 rest, Quaternion rot, Vector3 size, Vector3 a, Vector3 b)
        => VehicleShadow.Detour(rest, rot, size, a, b);

    /// <summary>
    /// Over the roof of a low box the detour is exactly the extra length of going up to the roof line
    /// where the straight line enters the box, across, and down again where it leaves. A box 2 m wide,
    /// 6 m long and 1.6 m tall (its body starts 0.2 m off the ground), and a line at 0.9 m cutting
    /// across it at a slant: it enters the near side at (-1, -0.5) and leaves the far side at (1, 0.5).
    /// Round an end or a side is further, so the roof is the answer.
    /// </summary>
    [Fact]
    public void TheDetourOverALowRoofIsTheExtraLengthUpAndDownAgain()
    {
        var size = new Vector3(2f, 1.6f, 6f);
        var a = new Vector3(-5f, 0.9f, -2.5f);
        var b = new Vector3(5f, 0.9f, 2.5f);
        const float rise = 1.6f - 0.9f;
        float up = MathF.Sqrt(4f * 4f + rise * rise + 2f * 2f);     // a to the roof edge over (-1, -0.5)
        float across = MathF.Sqrt(2f * 2f + 1f * 1f);                // along the roof to over (1, 0.5)
        float direct = MathF.Sqrt(10f * 10f + 5f * 5f);
        float expected = up + across + up - direct;

        Assert.Equal(expected, D(Vector3.Zero, Straight, size, a, b), 4);
    }

    /// <summary>
    /// Round the near end of a bus: a line at ear height that clips the corner of its end is
    /// cheapest going round that corner and straight on — about 8 cm, where the roof and the other
    /// side are over half a metre. Sound bending round an edge takes the shortest path past it; the
    /// first version of this test expected the route to run along the end face to where the line
    /// would have come out (15 cm), which was the old geometry's answer, not the shortest.
    /// </summary>
    [Fact]
    public void TheDetourRoundTheNearEndIsRoundTheCorner()
    {
        var a = new Vector3(-8f, 1.6f, 3f);
        var b = new Vector3(4f, 1.6f, 7f);
        var corner = new Vector3(-1.25f, 1.6f, 6f);     // the line enters the side at z = 5.25
        float expected = Vector3.Distance(a, corner) + Vector3.Distance(corner, b) - Vector3.Distance(a, b);
        Assert.Equal(expected, D(Vector3.Zero, Straight, Bus, a, b), 4);
    }

    /// <summary>
    /// The same scene picked up and put down somewhere else, turned, is the same barrier. Position and
    /// heading only say where the box is; the detour is a property of the three things together.
    /// </summary>
    [Fact]
    public void MovingAndTurningTheWholeSceneChangesNothing()
    {
        var size = new Vector3(2f, 1.6f, 6f);
        var a = new Vector3(-5f, 0.9f, -2.5f);
        var b = new Vector3(5f, 0.9f, 2.5f);
        float here = D(Vector3.Zero, Straight, size, a, b);

        var turn = Quaternion.CreateFromYawPitchRoll(0.7f, 0f, 0f);
        var shift = new Vector3(30f, 2f, -12f);
        Vector3 Move(Vector3 p) => Vector3.Transform(p, turn) + shift;
        float there = D(Move(Vector3.Zero), turn, size, Move(a), Move(b));

        Assert.True(here > 0.05f);
        Assert.Equal(here, there, 3);

        // ...and the bus lines used below, moved the same way.
        foreach (var (p, q) in Lines)
            Assert.Equal(D(Vector3.Zero, Straight, Bus, p, q), D(Move(Vector3.Zero), turn, Bus, Move(p), Move(q)), 3);
    }

    /// <summary>Lines across a 12 m bus standing at the origin along z, at ear height, each one
    /// cutting it off-centre: two that are cheapest round an end, one cheapest round a side.</summary>
    private static readonly (Vector3 A, Vector3 B)[] Lines =
    {
        (new Vector3(-8f, 1.6f, 3f), new Vector3(4f, 1.6f, 7f)),     // near the +z end
        (new Vector3(-6f, 1.6f, 3f), new Vector3(4f, 1.6f, 7f)),
        (new Vector3(-6f, 1.6f, -8f), new Vector3(0f, 1.6f, 8f)),    // a long slant along it, on the -x side
    };

    /// <summary>
    /// A barrier costs the same whichever way the sound crosses it: swapping the listener and the
    /// source cannot change how far round the box the sound must go.
    /// </summary>
    [Fact]
    public void ABarrierIsTheSameInBothDirections()
    {
        foreach (var (a, b) in Lines)
        {
            float forward = D(Vector3.Zero, Straight, Bus, a, b);
            Assert.True(forward > 0.05f, $"{a} to {b} should cross the bus");
            Assert.Equal(forward, D(Vector3.Zero, Straight, Bus, b, a), 4);
        }
    }

    /// <summary>
    /// A bus is the same from either end and either side. The same crossing mirrored end for end, or
    /// side for side, goes round the mirrored end or side and is the same detour — which only holds if
    /// the way round is taken off whichever end or side is NEARER the crossing.
    /// </summary>
    [Fact]
    public void TheWayRoundIsOffTheNearerEndOrSide()
    {
        static Vector3 FlipZ(Vector3 p) => new(p.X, p.Y, -p.Z);
        static Vector3 FlipX(Vector3 p) => new(-p.X, p.Y, p.Z);
        foreach (var (a, b) in Lines)
        {
            float d = D(Vector3.Zero, Straight, Bus, a, b);
            Assert.Equal(d, D(Vector3.Zero, Straight, Bus, FlipZ(a), FlipZ(b)), 4);
            Assert.Equal(d, D(Vector3.Zero, Straight, Bus, FlipX(a), FlipX(b)), 4);
        }

        // And going round the nearer end of the bus is less than going over its 3.2 m roof.
        var (na, nb) = Lines[0];
        var over = new Vector3(0f, 0f, 0f);
        float roofOnly = D(over, Straight, new Vector3(Bus.X, Bus.Y, 200f), na, nb);   // no end to go round
        Assert.True(D(Vector3.Zero, Straight, Bus, na, nb) < roofOnly);
    }

    /// <summary>
    /// A source inside the box is that vehicle's business, and so is a listener in it: neither end of
    /// the line may be inside the barrier. A car's own engine is not shadowed by the car.
    /// </summary>
    [Fact]
    public void NeitherEndMayBeInsideTheBarrier()
    {
        var inside = new Vector3(0f, 1.6f, 0f);
        var outside = new Vector3(-10f, 1.6f, 3f);
        Assert.Equal(0f, D(Vector3.Zero, Straight, Bus, outside, inside));
        Assert.Equal(0f, D(Vector3.Zero, Straight, Bus, inside, outside));
    }

    /// <summary>
    /// A line that misses the box on any axis costs nothing: beside it, past its end, over its roof,
    /// and under it through the gap between the body and the road.
    /// </summary>
    [Fact]
    public void ALineThatMissesTheBoxOnAnyAxisCostsNothing()
    {
        // Beside it: the line runs along z at x = 3, and the bus is 1.25 m either side of x = 0.
        Assert.Equal(0f, D(Vector3.Zero, Straight, Bus, new Vector3(3f, 1.6f, -10f), new Vector3(3f, 1.6f, 10f)));
        // Past its end: across x at z = 8, and the bus ends at z = 6.
        Assert.Equal(0f, D(Vector3.Zero, Straight, Bus, new Vector3(-10f, 1.6f, 8f), new Vector3(10f, 1.6f, 8f)));
        // A slant that crosses the bus's width and its height band but only after the end.
        Assert.Equal(0f, D(Vector3.Zero, Straight, Bus, new Vector3(-10f, 1.6f, 7f), new Vector3(10f, 1.6f, 9f)));
        // Over the roof at 3.5 m, the roof being at 3.2.
        Assert.Equal(0f, D(Vector3.Zero, Straight, Bus, new Vector3(-10f, 3.5f, 1f), new Vector3(10f, 3.5f, 2f)));
        // Under the body, 10 cm off the road, where the body starts at 20.
        Assert.Equal(0f, D(Vector3.Zero, Straight, Bus, new Vector3(-10f, 0.1f, 1f), new Vector3(10f, 0.1f, 2f)));
        // ...and the same line at ear height does cross it, so the misses above are misses.
        Assert.True(D(Vector3.Zero, Straight, Bus, new Vector3(-10f, 1.6f, 1f), new Vector3(10f, 1.6f, 2.5f)) > 0f);
    }

    /// <summary>Maekawa's law, from its own formula: 0 dB at grazing, rising with the Fresnel number
    /// N = 2 delta f / c as 10 log10(3 + 20 N), and capped at 15 dB for a body with ends.</summary>
    [Fact]
    public void TheLossIsMaekawasLawCapped()
    {
        Assert.Equal(0f, VehicleShadow.Loss(0f, 1000f), 2);
        float n = 2f * 0.1f * 1000f / 343f;
        Assert.Equal(10f * MathF.Log10(3f + 20f * n) - 4.77f, VehicleShadow.Loss(0.1f, 1000f), 3);
        Assert.Equal(15f, VehicleShadow.Loss(5f, 4000f), 3);
    }

    // ── Apply: the whole path, through a world of moving bodies ───────────────────────────────

    private static EntitySnapshot Body(int id, Vector3 restsAt, Vector3 size, bool solid = true) => new()
    {
        Id = id,
        Definition = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = solid },
        },
        Transform = new Transform { Position = restsAt, Rotation = Quaternion.Identity, Scale = Vector3.One },
    };

    private static WorldSnapshot World(params EntitySnapshot[] bodies)
    {
        var w = new WorldSnapshot();
        foreach (var b in bodies) { w.Entities[b.Id] = b; w.DynamicEntities.Add(b); }
        return w;
    }

    private static AcousticPathData OpenPath() => new(0f, Vector3.Zero, 10f, eqL: 1f, eqM: 1f, eqH: 1f);

    // The listener and the source of the first line above: a bus at the origin is between them.
    private static readonly Vector3 Ear = new(-8f, 1.6f, 3f), Source = new(4f, 1.6f, 7f);

    private static (float Low, float Mid, float High) Losses(float detour)
        => (VehicleShadow.Loss(detour, 150f), VehicleShadow.Loss(detour, 1000f), VehicleShadow.Loss(detour, 4000f));

    private static float Gain(float db) => MathF.Pow(10f, -db / 20f);

    /// <summary>
    /// A bus between you and a car takes its Maekawa loss off each of the three bands the mixer's EQ
    /// has — most off the top, least off the bottom — and reports the top band's loss.
    /// </summary>
    [Fact]
    public void ABusBetweenTakesItsLossOffEachBand()
    {
        var path = OpenPath();
        float worst = VehicleShadow.Apply(ref path, World(Body(5, Vector3.Zero, Bus)), 99, Source, Ear, -1);
        var (low, mid, high) = Losses(D(Vector3.Zero, Straight, Bus, Ear, Source));

        Assert.True(high > mid && mid > low && low > 0f);
        Assert.Equal(high, worst, 4);
        Assert.Equal(Gain(low), path.EqLow, 4);
        Assert.Equal(Gain(mid), path.EqMid, 4);
        Assert.Equal(Gain(high), path.EqHigh, 4);
    }

    /// <summary>
    /// A second vehicle in the line takes a little more, not as much again: the worst one plus a
    /// quarter of the other, band by band.
    /// </summary>
    [Fact]
    public void ASecondVehicleAddsAQuarterOfItsLoss()
    {
        // A van further along the same line, where it crosses x = -5.
        var vanAt = new Vector3(-5f, 0f, 4f);
        var van = new Vector3(2f, 2.5f, 5f);
        float busDetour = D(Vector3.Zero, Straight, Bus, Ear, Source);
        float vanDetour = D(vanAt, Straight, van, Ear, Source);
        Assert.True(vanDetour > 0f && MathF.Abs(vanDetour - busDetour) > 0.01f);

        var path = OpenPath();
        float worst = VehicleShadow.Apply(ref path, World(Body(5, Vector3.Zero, Bus), Body(6, vanAt, van)), 99, Source, Ear, -1);

        var b = Losses(busDetour);
        var v = Losses(vanDetour);
        static float Both(float x, float y) => MathF.Max(x, y) + 0.25f * MathF.Min(x, y);
        Assert.Equal(Both(b.High, v.High), worst, 3);
        Assert.Equal(Gain(Both(b.Low, v.Low)), path.EqLow, 4);
        Assert.Equal(Gain(Both(b.Mid, v.Mid)), path.EqMid, 4);
        Assert.Equal(Gain(Both(b.High, v.High)), path.EqHigh, 4);
    }

    /// <summary>
    /// Some bodies are not barriers: the source itself, the vehicle you are riding in, anything not
    /// solid, anything smaller than 2.5 cubic metres (a person, a mower), and anything beside the path.
    /// None of them touches the path or reports a loss.
    /// </summary>
    [Fact]
    public void WhatIsNotABarrierLeavesThePathAlone()
    {
        void None(WorldSnapshot w, int sourceId, int ridingId)
        {
            var path = OpenPath();
            Assert.Equal(0f, VehicleShadow.Apply(ref path, w, sourceId, Source, Ear, ridingId));
            Assert.Equal(1f, path.EqLow);
            Assert.Equal(1f, path.EqMid);
            Assert.Equal(1f, path.EqHigh);
        }

        None(World(Body(5, Vector3.Zero, Bus)), sourceId: 5, ridingId: -1);            // the source's own body
        None(World(Body(5, Vector3.Zero, Bus)), sourceId: 99, ridingId: 5);            // the bus you are on
        None(World(Body(5, Vector3.Zero, Bus, solid: false)), 99, -1);                 // not solid
        None(World(Body(5, Vector3.Zero, new Vector3(0.6f, 1.8f, 0.6f))), 99, -1);     // a person
        None(World(Body(5, Vector3.Zero, new Vector3(0.6f, 1.0f, 1.5f))), 99, -1);     // a mower
        None(World(Body(5, new Vector3(0f, 0f, 30f), Bus)), 99, -1);                   // a bus well beside the line
        None(World(), 99, -1);
    }

    /// <summary>
    /// Anything of 2.5 cubic metres or more is a barrier, whatever its proportions — a tall narrow box
    /// as much as a long low one, and one of exactly the threshold.
    /// </summary>
    [Fact]
    public void AnyBodyOfTheThresholdVolumeIsABarrier()
    {
        foreach (var size in new[] { new Vector3(1f, 2.5f, 1.2f), new Vector3(1f, 2.5f, 1f), new Vector3(2f, 2.5f, 5f) })
        {
            // Stood on the line, where it crosses x = -5.
            var at = new Vector3(-5f, 0f, 4f);
            float detour = D(at, Straight, size, Ear, Source);
            Assert.True(detour > 0f, $"{size} is not across the line, so this proves nothing");
            var path = OpenPath();
            float worst = VehicleShadow.Apply(ref path, World(Body(7, at, size)), 99, Source, Ear, -1);
            Assert.Equal(VehicleShadow.Loss(detour, 4000f), worst, 4);
            Assert.True(path.EqHigh < 1f);
        }
    }

    /// <summary>
    /// KNOWN DEFECT, not a mutant: a bus standing broadside across a LEVEL line between two points
    /// at the same height casts no shadow at all. The "round its side" route is built from the points
    /// where the line enters and leaves, moved to one of the two faces it crosses — so for a line that
    /// crosses the two long faces square-on, it is the straight line itself, through the bus, and
    /// the least of the three routes is zero. The roof route (about 0.29 m here) is what should win.
    /// The existing bus test passes only because its ear and source are at different heights, which
    /// gives the degenerate route a few centimetres of vertical kink.
    /// </summary>
    [Fact]
    public void ABusBroadsideShadowsALevelLine()
    {
        var rot = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f);
        float detour = D(new Vector3(0, 0, 10), rot, Bus, new Vector3(0, 1.6f, 0), new Vector3(0, 1.6f, 20));
        Assert.True(detour > 0.25f, $"detour {detour:F3} m");
    }
}
