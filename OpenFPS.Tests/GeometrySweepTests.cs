using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Geometry;

namespace OpenFPS.Tests;

/// <summary>
/// Swept shapes against the triangle world (docs/PHYSICS_BOUNDARY.md 18.2, step 5): a ball, an upright capsule and a
/// turned box moved along a segment meet the first triangle at a distance worked out by hand on hand-built
/// triangles, never tunnel, and agree with the ray and box queries beside them.
/// </summary>
public class GeometrySweepTests
{
    private static readonly Surface Stone = EntityGeometry.SurfaceOf("Concrete", Vector3.One, 0, 0, false, 0, 0, false, false, false);

    /// <summary>Triangles (three vertices each, relative to <paramref name="at"/>) as one sheet owned by <paramref name="owner"/>.</summary>
    private static SolidSpec Sheet(int owner, Vector3 at, params Vector3[] corners)
    {
        var indices = new int[corners.Length];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;
        var mesh = new MeshAsset(corners, indices, new byte[corners.Length / 3], closed: false, convex: false);
        return new SolidSpec(owner, at, Quaternion.Identity, Vector3.One, Stone, mesh);
    }

    private static TriangleWorld WorldOf(params SolidSpec[] statics)
        => new TriangleWorldBuilder(250f).Build(statics, Array.Empty<SolidSpec>());

    /// <summary>A floor 40 m square at y = 0, facing up, as two triangles.</summary>
    private static readonly Vector3[] Floor =
    {
        new(-20, 0, -20), new(-20, 0, 20), new(20, 0, -20),
        new(20, 0, -20), new(-20, 0, 20), new(20, 0, 20),
    };

    private static SweepHit Hit(TriangleWorld w, SweptShape shape, Vector3 delta)
    {
        Assert.True(w.Sweep(shape, delta, out var hit), "expected a hit");
        return hit;
    }

    private const float Tol = 2e-3f;

    // ── A ball ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BallLandsOnAFaceAtTheDistanceItsRadiusLeaves()
    {
        var w = WorldOf(Sheet(1, Vector3.Zero, Floor));
        var hit = Hit(w, SweptShape.Sphere(new Vector3(3, 2, 4), 0.5f), new Vector3(0, -3, 0));
        Assert.Equal(1.5f / 3f, hit.Fraction, 3);
        Assert.Equal(1.5f, hit.Distance, 3);
        Assert.Equal(Vector3.UnitY, hit.Normal);
        Assert.Equal(0f, hit.Point.Y, 3);
        Assert.Equal(3f, hit.Point.X, 3);
        Assert.Equal(4f, hit.Point.Z, 3);
        Assert.Equal(1, hit.Owner);
        Assert.False(hit.Embedded);
        Assert.True(hit.Triangle >= 0);
    }

    [Fact]
    public void BallCoveringTheWholeMoveIsNotStoppedShort()
    {
        var w = WorldOf(Sheet(1, Vector3.Zero, Floor));
        Assert.False(w.Sweep(SweptShape.Sphere(new Vector3(0, 2, 0), 0.5f), new Vector3(0, -1.4f, 0), out _));
        Assert.True(w.Sweep(SweptShape.Sphere(new Vector3(0, 2, 0), 0.5f), new Vector3(0, -1.6f, 0), out _));
    }

    [Fact]
    public void BallMeetsAnEdgeAtTheDistanceOfTheEdge()
    {
        // The right triangle (0,0)-(4,0)-(0,4) in the plane z = 0: a ball moving in that plane towards the middle of
        // the hypotenuse meets the edge one radius before the edge's point (2, 2).
        var w = WorldOf(Sheet(1, Vector3.Zero, new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(0, 4, 0)));
        float s = MathF.Sqrt(0.5f);
        var start = new Vector3(2, 2, 0) + new Vector3(s, s, 0) * 5f;
        var hit = Hit(w, SweptShape.Sphere(start, 1f), new Vector3(-s, -s, 0) * 5f);
        Assert.Equal(4f / 5f, hit.Fraction, 3);
        Assert.Equal(s, hit.Normal.X, 3);
        Assert.Equal(s, hit.Normal.Y, 3);
        Assert.Equal(2f, hit.Point.X, 3);
        Assert.Equal(2f, hit.Point.Y, 3);
    }

    [Fact]
    public void BallMeetsAVertexAtTheDistanceOfTheVertex()
    {
        var w = WorldOf(Sheet(1, Vector3.Zero, new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(0, 4, 0)));
        var hit = Hit(w, SweptShape.Sphere(new Vector3(9, 0, 0), 1f), new Vector3(-6, 0, 0));
        Assert.Equal(4f / 6f, hit.Fraction, 3);
        Assert.Equal(1f, hit.Normal.X, 3);
        Assert.Equal(0f, hit.Normal.Y, 3);
        Assert.Equal(4f, hit.Point.X, 3);
        Assert.Equal(0f, hit.Point.Y, 3);
    }

    [Fact]
    public void BallGrazingASurfaceIsNotStopped()
    {
        var w = WorldOf(Sheet(1, Vector3.Zero, Floor));
        // Resting on it and sliding, and passing just clear of it: neither meets it.
        Assert.False(w.Sweep(SweptShape.Sphere(new Vector3(-10, 1f, 0), 1f), new Vector3(20, 0, 0), out _));
        Assert.False(w.Sweep(SweptShape.Sphere(new Vector3(-10, 1.01f, 0), 1f), new Vector3(20, 0, 0), out _));
        // Sunk into it by a centimetre it is held at once, whichever way it goes.
        var hit = Hit(w, SweptShape.Sphere(new Vector3(-10, 0.99f, 0), 1f), new Vector3(20, 0, 0));
        Assert.Equal(0f, hit.Fraction);
        Assert.True(hit.Embedded);
        Assert.Equal(1f, hit.Normal.Y, 3);
    }

    [Fact]
    public void BallBeginningInContactHitsOnlyIfMovingIn()
    {
        var w = WorldOf(Sheet(1, Vector3.Zero, Floor));
        var touching = SweptShape.Sphere(new Vector3(0, 1f, 0), 1f);
        var hit = Hit(w, touching, new Vector3(0, -1, 0));
        Assert.Equal(0f, hit.Fraction, 3);
        Assert.False(hit.Embedded);
        Assert.False(w.Sweep(touching, new Vector3(0, 1, 0), out _));
    }

    [Fact]
    public void ABallPushedIntoAWallIsHeldWhichWayItIsGoing()
    {
        // A box 4 m across with its face at x = 2: the ball's middle is 0.2 m inside that face, 0.1 m of it through.
        var w = WorldOf(new SolidSpec(3, new Vector3(0, 1, 0), Quaternion.Identity, new Vector3(4, 2, 4), Stone));
        foreach (var delta in new[] { new Vector3(0, 0, 0), new Vector3(0.1f, 0, 0), new Vector3(0, 0, -0.2f), new Vector3(-1, 0, 0) })
        {
            var hit = Hit(w, SweptShape.Sphere(new Vector3(1.8f, 1f, 0), 0.3f), delta);
            Assert.Equal(0f, hit.Fraction);
            Assert.True(hit.Embedded);
            Assert.Equal(3, hit.Owner);
            Assert.Equal(-1f, hit.Normal.X, 3);
        }
    }

    // ── A capsule ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CapsuleStandsOnItsFeetAndStopsAtAWallByItsRadius()
    {
        var wall = new[] { new Vector3(5, 0, -10), new Vector3(5, 5, -10), new Vector3(5, 0, 10) };
        var w = WorldOf(Sheet(1, Vector3.Zero, Floor), Sheet(2, Vector3.Zero, wall));
        var body = SweptShape.Capsule(new Vector3(0, 3, 0), 0.4f, 1.8f);

        var down = Hit(w, body, new Vector3(0, -4, 0));
        Assert.Equal(3f / 4f, down.Fraction, 3);
        Assert.Equal(1, down.Owner);

        var across = Hit(w, SweptShape.Capsule(new Vector3(0, 0.001f, 0), 0.4f, 1.8f), new Vector3(10, 0, 1));
        Assert.Equal(2, across.Owner);
        Assert.Equal(0.46f, across.Fraction, 3);
        Assert.Equal(-1f, across.Normal.X, 3);
    }

    [Fact]
    public void CapsuleHitsAnOverheadEdgeWithItsTopCap()
    {
        var beam = new[] { new Vector3(-5, 2, 0), new Vector3(5, 2, 0), new Vector3(-5, 2, 3) };
        var w = WorldOf(Sheet(1, Vector3.Zero, beam));
        // A 1.8 m body with a 0.4 radius under a ledge at 2 m, rising: the top of its upper ball meets the underside.
        var body = SweptShape.Capsule(new Vector3(0, 0, 1), 0.4f, 1.8f);
        var hit = Hit(w, body, new Vector3(0, 1, 0));
        Assert.Equal(0.2f, hit.Distance, 3);
        Assert.Equal(-1f, hit.Normal.Y, 3);
    }

    // ── A box ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BoxLandsFlatOnAFace()
    {
        var w = WorldOf(Sheet(1, Vector3.Zero, Floor));
        var box = SweptShape.Box(new Vector3(2, 3, 1), new Vector3(1, 1, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f));
        var hit = Hit(w, box, new Vector3(0, -3, 0));
        Assert.Equal(2f / 3f, hit.Fraction, 3);
        Assert.Equal(1f, hit.Normal.Y, 3);
    }

    [Fact]
    public void BoxOnAnEdgeAndOnACornerLandsOnThemByTheirHeights()
    {
        var w = WorldOf(Sheet(1, Vector3.Zero, Floor));
        // Turned a quarter-turn about z, a unit cube hangs by an edge: that edge is sqrt 2 below its middle.
        var onEdge = SweptShape.Box(new Vector3(0, 4, 0), new Vector3(1, 1, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f));
        var edge = Hit(w, onEdge, new Vector3(0, -4, 0));
        Assert.Equal((4f - MathF.Sqrt(2f)) / 4f, edge.Fraction, 3);
        Assert.Equal(1f, edge.Normal.Y, 3);

        // Stood on a corner, turned so its long diagonal is upright: sqrt 3 below its middle.
        var axis = Vector3.Normalize(Vector3.Cross(Vector3.Normalize(new Vector3(1, 1, 1)), Vector3.UnitY));
        float angle = MathF.Acos(Vector3.Dot(Vector3.Normalize(new Vector3(1, 1, 1)), Vector3.UnitY));
        var onCorner = SweptShape.Box(new Vector3(0, 4, 0), new Vector3(1, 1, 1), Quaternion.CreateFromAxisAngle(axis, angle));
        var corner = Hit(w, onCorner, new Vector3(0, -4, 0));
        Assert.Equal((4f - MathF.Sqrt(3f)) / 4f, corner.Fraction, 3);
        Assert.Equal(1f, corner.Normal.Y, 3);
    }

    [Fact]
    public void BoxMeetsTheEdgeOfATriangleWithItsOwnEdge()
    {
        // A wall triangle in the plane x = 5 whose upright edge is the line z = 0; a unit cube turned an eighth of a turn,
        // its upright corner pointing along its way, passing at z = -1. Its face runs from the corner back towards the
        // wall's edge, and the edge touches that face where the face is 1 m off the line of travel.
        var wall = new[] { new Vector3(5, 0, 0), new Vector3(5, 10, 0), new Vector3(5, 0, 10) };
        var w = WorldOf(Sheet(1, Vector3.Zero, wall));
        var box = SweptShape.Box(new Vector3(0, 5, -1), new Vector3(1, 1, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f));
        var hit = Hit(w, box, new Vector3(10, 0, 0));
        Assert.Equal((5f - (MathF.Sqrt(2f) - 1f)) / 10f, hit.Fraction, 3);
        float s = MathF.Sqrt(0.5f);
        Assert.Equal(-s, hit.Normal.X, 2);
        Assert.Equal(-s, hit.Normal.Z, 2);
        Assert.Equal(0f, hit.Point.Z, 2);
    }

    [Fact]
    public void BoxBeginningOverlappingIsHeldAndOneBeginningTouchingMovingAwayIsNot()
    {
        var w = WorldOf(Sheet(1, Vector3.Zero, Floor));
        var sunk = SweptShape.Box(new Vector3(0, 0.9f, 0), new Vector3(1, 1, 1), Quaternion.Identity);
        var hit = Hit(w, sunk, new Vector3(3, 0, 0));
        Assert.True(hit.Embedded);
        Assert.Equal(0f, hit.Fraction);
        Assert.Equal(1f, hit.Normal.Y, 3);

        var resting = SweptShape.Box(new Vector3(0, 1f, 0), new Vector3(1, 1, 1), Quaternion.Identity);
        Assert.False(w.Sweep(resting, new Vector3(3, 0.5f, 0), out _));
        Assert.False(w.Sweep(resting, new Vector3(3, 0, 0), out _));
        var down = Hit(w, resting, new Vector3(0, -1, 0));
        Assert.Equal(0f, down.Fraction, 3);
        Assert.False(down.Embedded);
    }

    // ── Long sweeps, many tiles, thin things ───────────────────────────────────────────────────────

    [Fact]
    public void ALongSweepFindsTheFirstOfManyTilesWalls()
    {
        var specs = new List<SolidSpec>();
        for (int k = 0; k < 40; k++)
        {
            float x = 250f * k;
            // A thin upright triangle (no thickness at all) 100 m into each tile, owned by the tile's number.
            specs.Add(Sheet(100 + k, new Vector3(x + 100f, 0, 0), new Vector3(0, -5, -5), new Vector3(0, 5, -5), new Vector3(0, 0, 5)));
        }
        var w = WorldOf(specs.ToArray());
        Assert.True(w.InstanceCount >= 40);
        var ball = SweptShape.Sphere(new Vector3(-50, 0, 0), 0.25f);
        var hit = Hit(w, ball, new Vector3(10000f, 0, 0));
        Assert.Equal(100, hit.Owner);
        Assert.Equal(149.75f, hit.Distance, 2);
        Assert.Equal(-1f, hit.Normal.X, 3);

        // The walls behind are met in turn when the nearer owners are let through.
        var rest = new ExceptOwners(100, 101);
        Assert.True(w.Sweep(ball, new Vector3(10000f, 0, 0), GeometryLayers.Physical, ref rest, out var later));
        Assert.Equal(102, later.Owner);
        Assert.InRange(later.Distance, 649.74f, 649.76f);

        // The last one is found at the end of a sweep that long, and not past it.
        var far = SweptShape.Sphere(new Vector3(250f * 39 + 50f, 0, 0), 0.25f);
        Assert.Equal(139, Hit(w, far, new Vector3(1000f, 0, 0)).Owner);
        Assert.False(w.Sweep(far, new Vector3(49.5f, 0, 0), out _));
    }

    [Fact]
    public void AFastSweepDoesNotTunnelThroughATriangleWithNoThickness()
    {
        var w = WorldOf(Sheet(1, Vector3.Zero, new Vector3(0, -2, -2), new Vector3(0, 2, -2), new Vector3(0, 0, 2)));
        foreach (var shape in new[]
                 {
                     SweptShape.Sphere(new Vector3(-500, 0, 0), 0.01f),
                     SweptShape.Capsule(new Vector3(-500, -0.9f, 0), 0.01f, 1.8f),
                     SweptShape.Box(new Vector3(-500, 0, 0), new Vector3(0.01f, 0.01f, 0.01f), Quaternion.CreateFromYawPitchRoll(1f, 2f, 3f)),
                 })
        {
            var hit = Hit(w, shape, new Vector3(1000, 0, 0));
            Assert.InRange(hit.Distance, 499.9f, 500.0f);
        }
    }

    [Fact]
    public void AStopMeansTheShapeIsClearAndNothingBeforeItIsInTheWay()
    {
        var rng = new Random(7);
        var specs = new List<SolidSpec>();
        for (int i = 0; i < 25; i++)
        {
            var size = new Vector3(0.5f + (float)rng.NextDouble() * 4f, 0.5f + (float)rng.NextDouble() * 4f, 0.5f + (float)rng.NextDouble() * 4f);
            var at = new Vector3((float)rng.NextDouble() * 40f - 20f, size.Y / 2f, (float)rng.NextDouble() * 40f - 20f);
            specs.Add(new SolidSpec(10 + i, at, Quaternion.CreateFromYawPitchRoll((float)rng.NextDouble() * 6f, 0, 0), size, Stone));
        }
        specs.Add(Sheet(1, Vector3.Zero, Floor));
        var w = WorldOf(specs.ToArray());

        int hits = 0, misses = 0;
        for (int n = 0; n < 300; n++)
        {
            var from = new Vector3((float)rng.NextDouble() * 40f - 20f, 0.5f + (float)rng.NextDouble() * 6f, (float)rng.NextDouble() * 40f - 20f);
            var delta = new Vector3((float)rng.NextDouble() * 30f - 15f, (float)rng.NextDouble() * 10f - 5f, (float)rng.NextDouble() * 30f - 15f);
            SweptShape At(Vector3 p) => (n % 3) switch
            {
                0 => SweptShape.Sphere(p, 0.3f),
                1 => SweptShape.Capsule(p - new Vector3(0, 0.9f, 0), 0.35f, 1.8f),
                _ => SweptShape.Box(p, new Vector3(0.4f, 0.3f, 0.5f), Quaternion.CreateFromYawPitchRoll(n * 0.37f, n * 0.11f, 0f)),
            };
            if (w.Sweep(At(from), Vector3.Zero, out _)) continue;       // began inside something: another test's business

            bool hit = w.Sweep(At(from), delta, out var h);
            float last = hit ? h.Fraction : 1f;
            // Along the way, up to where it stopped, it is never inside anything.
            for (float f = 0f; f <= last; f += 0.01f)
                Assert.False(w.Sweep(At(from + delta * f), Vector3.Zero, out var inside) && inside.Embedded,
                             $"run {n}: inside something at {f} of a move that stopped at {last}");
            if (hit)
            {
                hits++;
                Assert.False(w.Sweep(At(from + delta * h.Fraction), Vector3.Zero, out var stop) && stop.Embedded, $"run {n}: stopped inside");
                Assert.True(h.Normal.Length() > 0.99f && h.Normal.Length() < 1.01f);
            }
            else misses++;
        }
        Assert.True(hits > 40 && misses > 20, $"{hits} hits and {misses} misses: the run should have both");
    }

    // ── The queries beside it ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATinyBallFollowsTheRayAndTheSolidItMeetsIsOneThatOverlapsItsPath()
    {
        var rng = new Random(11);
        var specs = new List<SolidSpec>();
        for (int i = 0; i < 20; i++)
        {
            var size = new Vector3(1f + (float)rng.NextDouble() * 4f, 1f + (float)rng.NextDouble() * 5f, 1f + (float)rng.NextDouble() * 4f);
            var at = new Vector3((float)rng.NextDouble() * 60f - 30f, size.Y / 2f, (float)rng.NextDouble() * 60f - 30f);
            specs.Add(new SolidSpec(10 + i, at, Quaternion.CreateFromYawPitchRoll((float)rng.NextDouble() * 6f, 0, 0), size, Stone));
        }
        specs.Add(Sheet(1, Vector3.Zero, Floor));
        var w = WorldOf(specs.ToArray());
        var all = new AcceptAll();
        const float radius = 0.002f;

        int agreed = 0;
        for (int n = 0; n < 400; n++)
        {
            var o = new Vector3((float)rng.NextDouble() * 60f - 30f, 0.2f + (float)rng.NextDouble() * 5f, (float)rng.NextDouble() * 60f - 30f);
            var d = Vector3.Normalize(new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.7f, (float)rng.NextDouble() - 0.5f));
            const float reach = 60f;
            bool ray = w.Closest(o, d, reach, GeometryLayers.Physical, RayFaces.Both, ref all, out var r);
            bool swept = w.Sweep(SweptShape.Sphere(o, radius), d * reach, GeometryLayers.Physical, ref all, out var s);
            if (swept && s.Embedded) continue;
            // A ball a few millimetres across never misses what a ray along its middle finds, and stops no later.
            if (ray)
            {
                Assert.True(swept, $"run {n}: the ray hit at {r.T} and the ball found nothing");
                float cos = MathF.Abs(Vector3.Dot(d, r.Normal));
                Assert.True(s.Distance <= r.T + 2e-3f, $"run {n}: the ball stopped at {s.Distance}, past the ray's {r.T}");
                if (cos > 0.2f)
                {
                    Assert.True(s.Distance >= r.T - radius / cos - 2e-3f, $"run {n}: the ball stopped at {s.Distance}, long before the ray's {r.T}");
                    Assert.Equal(r.Solid, s.Solid);
                }
                agreed++;
            }
            if (swept)
            {
                var found = new List<SolidRef>();
                var lo = Vector3.Min(o, o + d * s.Distance) - new Vector3(0.01f);
                var hi = Vector3.Max(o, o + d * s.Distance) + new Vector3(0.01f);
                w.Overlapping(lo, hi, GeometryLayers.Physical, ref all, found);
                Assert.Contains(s.Solid, found);
            }
        }
        Assert.True(agreed > 150, $"{agreed} rays agreed");
    }

    [Fact]
    public void AMovedPieceIsSweptInItsOwnFrame()
    {
        var w = WorldOf(Sheet(1, Vector3.Zero, new Vector3(0, -2, -3), new Vector3(0, 2, -3), new Vector3(0, 0, 3)));
        // A tile's piece turned a quarter-turn about y, set so the world's origin goes to (10, 0, 0).
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        var turned = w.WithMoved(new (int, Vector3, Quaternion)[]
        {
            (0, new Vector3(10, 0, 0) + Vector3.Transform(w.Instance(0).Position, turn), turn),
        });
        // The triangle was in the plane x = 0 facing along x; turned a quarter-turn it lies in the plane z = 0 at x = 10.
        var hit = Hit(turned, SweptShape.Sphere(new Vector3(10, 0, 5), 0.5f), new Vector3(0, 0, -5));
        Assert.Equal(4.5f / 5f, hit.Fraction, 3);
        Assert.Equal(1f, MathF.Abs(hit.Normal.Z), 3);
        Assert.Equal(10f, hit.Point.X, 2);
    }

    [Fact]
    public void GroundIsSweptLikeAnyOtherTriangles()
    {
        // Two cells of ground, 4 m across, rising half a metre for each metre east from 10 m.
        var field = Heightfield.FromCentimetres(3, 2f, 10f, new short[] { 0, 100, 200, 0, 100, 200, 0, 100, 200 }, null, null);
        var w = WorldOf(SolidSpec.OfTerrain(5, new Vector3(2, 0, 2), field, Stone));
        var hit = Hit(w, SweptShape.Sphere(new Vector3(1, 20, 1), 0.5f), new Vector3(0, -20, 0));
        float down = 20f - (10.5f + 0.5f * MathF.Sqrt(1.25f));
        Assert.Equal(down, hit.Distance, 2);
        Assert.Equal(5, hit.Owner);
        var n = Vector3.Normalize(new Vector3(-0.5f, 1f, 0f));
        Assert.Equal(n.X, hit.Normal.X, 3);
        Assert.Equal(n.Y, hit.Normal.Y, 3);
        // Beside the tile there is nothing, and a long way over it nothing either.
        Assert.False(w.Sweep(SweptShape.Sphere(new Vector3(8, 20, 1), 0.5f), new Vector3(0, -20, 0), out _));
        Assert.False(w.Sweep(SweptShape.Sphere(new Vector3(1, 40, 1), 0.5f), new Vector3(0, -2, 0), out _));
        // A ball rolling over a seam between cells, lying on the ground, is not stopped by it.
        Assert.False(w.Sweep(SweptShape.Sphere(new Vector3(0.6f, 10.3f + 0.5f * MathF.Sqrt(1.25f), 1), 0.5f), new Vector3(0, 0, 2), out _));
    }

    [Fact]
    public void ASweepThroughNothingFindsNothing()
    {
        Assert.False(TriangleWorld.Empty.Sweep(SweptShape.Sphere(Vector3.Zero, 1f), new Vector3(100, 0, 0), out var hit));
        Assert.Equal(-1, hit.Triangle);
        var w = WorldOf(Sheet(1, Vector3.Zero, Floor));
        Assert.False(w.Sweep(SweptShape.Sphere(new Vector3(0, 5, 0), 1f), new Vector3(100, 0, 0), out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => SweptShape.Sphere(Vector3.Zero, 0f));
    }
}
