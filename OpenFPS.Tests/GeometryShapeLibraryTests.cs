using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Geometry;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Geometry stage 4's shape library (docs/GEOMETRY.md 2.6 and 12): round columns and trunks, cones, balls and
/// domes, outlines stood up (with holes), roofs from the straight skeleton (hip, gable, shed, flat), profiles
/// swept along paths. Each is closed and faces out, its convex pieces fill it, it fills its box, it has its
/// surfaces and its facets, and the same numbers make the same bits.
/// </summary>
public class GeometryShapeLibraryTests
{
    private readonly ITestOutputHelper _o;
    public GeometryShapeLibraryTests(ITestOutputHelper o) => _o = o;

    private static float[] Pairs(params (double X, double Z)[] p) => p.SelectMany(q => new[] { (float)q.X, (float)q.Z }).ToArray();

    /// <summary>An L: 12 by 8 with a 6 by 4 bite out of one corner.</summary>
    internal static float[] LShape => Pairs((0, 0), (12, 0), (12, 4), (6, 4), (6, 8), (0, 8));

    public static IEnumerable<object[]> Forms() => new[]
    {
        new object[] { "ramp", new ShapeSpec { Kind = ShapeKind.Wedge }, new Vector3(1.5f, 0.5f, 6f) },
        new object[] { "stairs", new ShapeSpec { Kind = ShapeKind.Stairs, Steps = 16 }, new Vector3(1.2f, 2.8f, 4.48f) },
        new object[] { "stairs to a landing", new ShapeSpec { Kind = ShapeKind.Stairs, Steps = 10, Landing = 1.2f }, new Vector3(1.0f, 1.75f, 4.0f) },
        new object[] { "arch", new ShapeSpec { Kind = ShapeKind.Arch, Thickness = 0.5f }, new Vector3(3f, 3.5f, 0.6f) },
        new object[] { "column", new ShapeSpec { Kind = ShapeKind.Cylinder }, new Vector3(0.6f, 3f, 0.6f) },
        new object[] { "tank", new ShapeSpec { Kind = ShapeKind.Cylinder }, new Vector3(10f, 6f, 10f) },
        new object[] { "oval column", new ShapeSpec { Kind = ShapeKind.Cylinder, Segments = 9 }, new Vector3(0.8f, 2f, 0.4f) },
        new object[] { "cone", new ShapeSpec { Kind = ShapeKind.Cone }, new Vector3(1f, 1.5f, 1f) },
        new object[] { "frustum", new ShapeSpec { Kind = ShapeKind.Cone, Top = 0.4f }, new Vector3(2f, 1f, 2f) },
        new object[] { "ball", new ShapeSpec { Kind = ShapeKind.Sphere }, new Vector3(1f, 1f, 1f) },
        new object[] { "dome", new ShapeSpec { Kind = ShapeKind.Dome }, new Vector3(6f, 3f, 6f) },
        new object[] { "square prism", new ShapeSpec { Kind = ShapeKind.Prism, Outline = Pairs((-1, -1), (1, -1), (1, 1), (-1, 1)) }, new Vector3(2f, 0.3f, 2f) },
        new object[] { "L prism", new ShapeSpec { Kind = ShapeKind.Prism, Outline = LShape }, new Vector3(12f, 0.25f, 8f) },
        new object[] { "yard with a hole", new ShapeSpec { Kind = ShapeKind.Prism, Outline = Pairs((0, 0), (10, 0), (10, 10), (0, 10)),
            Holes = new[] { Pairs((3, 3), (3, 6), (6, 6), (6, 3)) } }, new Vector3(10f, 2f, 10f) },
        new object[] { "hip roof", new ShapeSpec { Kind = ShapeKind.Roof, Outline = Pairs((0, 0), (12, 0), (12, 8), (0, 8)) }, new Vector3(12f, 2f, 8f) },
        new object[] { "hip L", new ShapeSpec { Kind = ShapeKind.Roof, Outline = LShape }, new Vector3(12f, 2f, 8f) },
        new object[] { "gable roof", new ShapeSpec { Kind = ShapeKind.Roof, Style = RoofStyle.Gable, Outline = Pairs((0, 0), (12, 0), (12, 8), (0, 8)) }, new Vector3(12f, 2f, 8f) },
        new object[] { "gable L", new ShapeSpec { Kind = ShapeKind.Roof, Style = RoofStyle.Gable, Outline = LShape }, new Vector3(12f, 2f, 8f) },
        new object[] { "pyramid", new ShapeSpec { Kind = ShapeKind.Roof, Outline = Pairs((0, 0), (8, 0), (8, 8), (0, 8)) }, new Vector3(8f, 2f, 8f) },
        new object[] { "shed L", new ShapeSpec { Kind = ShapeKind.Roof, Style = RoofStyle.Shed, Outline = LShape }, new Vector3(12f, 1f, 8f) },
        new object[] { "flat roof", new ShapeSpec { Kind = ShapeKind.Roof, Style = RoofStyle.Flat, Outline = LShape }, new Vector3(12f, 0.2f, 8f) },
        new object[] { "skewed hip", new ShapeSpec { Kind = ShapeKind.Roof, Outline = Pairs((0, 0), (11, 1), (12.5f, 9), (-0.5f, 7.6f)) }, new Vector3(13f, 2f, 9f) },
        new object[] { "kerb", new ShapeSpec { Kind = ShapeKind.Swept, Profile = Pairs((0, 0), (0.15, 0), (0.15, 0.13), (0.13, 0.15), (0, 0.15)),
            Path = new float[] { 0, 0, 0, 10, 0, 0 } }, new Vector3(10f, 0.15f, 0.15f) },
        new object[] { "kerb round a bend", new ShapeSpec { Kind = ShapeKind.Swept, Profile = Pairs((0, 0), (0.15, 0), (0.15, 0.15), (0, 0.15)),
            Path = new float[] { 0, 0, 0, 5, 0, 0, 8, 0.2f, 3, 8, 0.4f, 8 } }, new Vector3(8.2f, 0.55f, 8.1f) },
        new object[] { "moulding", new ShapeSpec { Kind = ShapeKind.Swept, Profile = Pairs((0, 0), (0.1, 0), (0.1, 0.04), (0.04, 0.04), (0.04, 0.12), (0, 0.12)),
            Path = new float[] { 0, 0, 0, 3, 0, 0, 3, 0, 3 } }, new Vector3(3.1f, 0.12f, 3.1f) },
    };

    private static Surface Concrete(Vector3 size) => EntityGeometry.SurfaceOf("Concrete", size, 0, 0, false, 0, 0, false, false, false, null);

    private static TriangleWorld WorldOf(params SolidSpec[] solids) => new TriangleWorldBuilder(250f).Build(solids, Array.Empty<SolidSpec>());

    /// <summary>Closed, facing out, no triangle without area, no sliver, its pieces each closed and convex, and
    /// it fills its box exactly.</summary>
    [Theory]
    [MemberData(nameof(Forms))]
    public void A_shape_is_a_clean_solid_that_fills_its_box(string what, ShapeSpec form, Vector3 size)
    {
        Assert.Null(Shapes.Problem(form, size));
        var mesh = Shapes.Make(form, size);
        Assert.NotNull(mesh);
        var report = MeshCheck.Check(mesh!.Outer);
        _o.WriteLine($"{what}: {report}; {mesh.Parts?.Length ?? 1} convex pieces, {mesh.Outer.Facets.Items.Length} facets");
        Assert.True(report.Closed, $"{what}: {report}");
        Assert.Equal(0, report.Degenerate);
        Assert.Equal(0, report.Slivers);
        Assert.Equal(mesh.Parts == null, mesh.Outer.Convex);
        double partsVolume = 0;
        foreach (var part in mesh.Parts ?? new[] { mesh.Outer })
        {
            var pr = MeshCheck.Check(part);
            Assert.True(pr.Closed, $"{what}, a piece: {pr}");
            Assert.True(part.Convex);
            partsVolume += pr.Volume;
        }
        Assert.Equal(report.Volume, partsVolume, 3);
        var h = size * 0.5f;
        Assert.True(Vector3.Distance(mesh.Outer.BoundsMin, -h) < 1e-4f && Vector3.Distance(mesh.Outer.BoundsMax, h) < 1e-4f,
                    $"{what}: bounds {mesh.Outer.BoundsMin} to {mesh.Outer.BoundsMax}, box {size}");
    }

    /// <summary>The triangle world agrees: a ray from inside leaves one more face than it enters, from outside as
    /// many, and a point is in one of the convex pieces exactly when the crossings say it is inside.</summary>
    [Theory]
    [MemberData(nameof(Forms))]
    public void Rays_and_pieces_agree_on_inside(string what, ShapeSpec form, Vector3 size)
    {
        var at = new Vector3(3, size.Y / 2f + 1f, 4);
        var world = WorldOf(SolidSpec.Of(7, at, Quaternion.CreateFromYawPitchRoll(0.4f, 0, 0), size, Concrete(size), Shapes.Make(form, size)));
        var all = new AcceptAll();
        var crossings = new List<GeometryCrossing>();
        var inside = new List<SolidRef>();
        var rng = new Random(11);
        int insideCount = 0;
        for (int i = 0; i < 2000; i++)
        {
            var p = at + new Vector3((float)(rng.NextDouble() * 2 - 1) * size.X * 0.6f, (float)(rng.NextDouble() * 2 - 1) * size.Y * 0.6f,
                                     (float)(rng.NextDouble() * 2 - 1) * size.Z * 0.6f);
            var d = Vector3.Normalize(new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f));
            world.All(p, d, 50f, GeometryLayers.Physical, ref all, crossings);
            int front = crossings.Count(c => c.Front), back = crossings.Count - front;
            inside.Clear();
            world.Containing(p, GeometryLayers.Physical, ref all, inside);
            if (inside.Count > 0) insideCount++;
            Assert.True((inside.Count > 0 ? 1 : 0) == back - front, $"{what}: from {p} toward {d}, {front} in and {back} out, inside {inside.Count > 0}");
        }
        Assert.True(insideCount > 4, $"{what}: only {insideCount} of 2000 points fell inside");
    }

    /// <summary>The same numbers give the same triangles, bit for bit.</summary>
    [Theory]
    [MemberData(nameof(Forms))]
    public void A_shape_is_the_same_every_time(string what, ShapeSpec form, Vector3 size)
    {
        var a = Shapes.Make(form, size)!;
        var b = Shapes.Make(form.Copy(), size)!;
        Assert.True(a.Outer.Hash == b.Outer.Hash, what);
        Assert.Equal(a.Parts?.Select(p => p.Hash), b.Parts?.Select(p => p.Hash));
        Assert.Equal(form, form.Copy());
    }

    [Fact]
    public void Facets_are_the_flat_faces()
    {
        int Facets(ShapeSpec? f, Vector3 size) => f == null ? ShapeLibrary.Box(size).Facets.Items.Length : Shapes.Make(f, size)!.Outer.Facets.Items.Length;
        Assert.Equal(6, Facets(null, new Vector3(2, 3, 4)));
        Assert.Equal(5, Facets(new ShapeSpec { Kind = ShapeKind.Wedge }, new Vector3(1.5f, 0.5f, 6f)));
        // Sixteen treads and risers, the back, the bottom and two stepped sides.
        Assert.Equal(36, Facets(new ShapeSpec { Kind = ShapeKind.Stairs, Steps = 16 }, new Vector3(1.2f, 2.8f, 4.48f)));
        Assert.Equal(14, Facets(new ShapeSpec { Kind = ShapeKind.Cylinder, Segments = 12 }, new Vector3(0.6f, 3, 0.6f)));
        // Hip: two long slopes, two ends, the underside. Gable: the same count, the ends standing.
        var rect = Pairs((0, 0), (12, 0), (12, 8), (0, 8));
        Assert.Equal(5, Facets(new ShapeSpec { Kind = ShapeKind.Roof, Outline = rect }, new Vector3(12, 2, 8)));
        var gable = Shapes.Make(new ShapeSpec { Kind = ShapeKind.Roof, Style = RoofStyle.Gable, Outline = rect }, new Vector3(12, 2, 8))!.Outer.Facets.Items;
        Assert.Equal(5, gable.Length);
        Assert.Equal(2, gable.Count(f => MathF.Abs(f.Normal.Y) < 1e-5f));
        // A rectangle's facets fill their rectangles; a triangle fills half.
        var box = ShapeLibrary.Box(new Vector3(2, 3, 4)).Facets;
        Assert.All(box.Items, f => Assert.InRange(f.Fill, 0.999f, 1.001f));
        Assert.True(box.Contains(0, box.Items[0].Centre));
        Assert.False(box.Contains(0, box.Items[0].Centre + box.Items[0].HalfU * 1.1f));
    }

    /// <summary>A hip roof over a rectangle: its ridge runs along the middle, the box's height up, and is as long
    /// as the length less the width; the slopes are one pitch.</summary>
    [Fact]
    public void A_hip_roof_over_a_rectangle()
    {
        var roof = StraightSkeleton.Of(Pairs((0, 0), (12, 0), (12, 8), (0, 8)).Chunk(2).Select(p => new Point2(p[0], p[1])).ToList());
        Assert.NotNull(roof);
        Assert.Equal(4.0, roof!.Top, 9);
        var ridge = Enumerable.Range(0, roof.Plan.Count).Where(i => Math.Abs(roof.Height[i] - 4.0) < 1e-9).Select(i => roof.Plan[i]).Distinct().ToList();
        Assert.Equal(2, ridge.Count);
        Assert.Equal(4.0, (ridge[0] - ridge[1]).Length, 9);
        Assert.All(ridge, p => Assert.Equal(4.0, p.Y, 9));
        Assert.Equal(4, roof.Faces[0].Count);   // a long side: a trapezoid
        Assert.Equal(3, roof.Faces[1].Count);   // an end: a triangle
    }

    /// <summary>Over an L the roof is the distance to the nearest edge, far from the corner inside it, and every
    /// face is a plane through its own edge.</summary>
    [Fact]
    public void A_hip_roof_over_an_L()
    {
        var ring = LShape.Chunk(2).Select(p => new Point2(p[0], p[1])).ToList();
        var roof = StraightSkeleton.Of(ring)!;
        Assert.NotNull(roof);
        for (int k = 0; k < ring.Count; k++)
        {
            var a = ring[k]; var b = ring[(k + 1) % ring.Count];
            var d = (b - a) * (1 / (b - a).Length);
            var nrm = new Point2(-d.Y, d.X);
            foreach (int node in roof.Faces[k])
                Assert.Equal(Point2.Dot(nrm, roof.Plan[node] - a), roof.Height[node], 6);
        }
        // The long wing's far end, 2 m in from its end: a hip at distance 2 from the end edge.
        var mesh = Shapes.Make(new ShapeSpec { Kind = ShapeKind.Roof, Outline = LShape }, new Vector3(12f, (float)roof.Top, 8f))!;
        var world = WorldOf(SolidSpec.Of(7, new Vector3(6, (float)roof.Top / 2f, 4), Quaternion.Identity, new Vector3(12f, (float)roof.Top, 8f),
                                         Concrete(Vector3.One), mesh));
        var all = new AcceptAll();
        // The L's corner (0, 0) is the box's (-6, -4); (10, 2) in the outline is (4, -2) from the middle.
        Assert.True(world.Closest(new Vector3(10, 50, 2), -Vector3.UnitY, 100f, GeometryLayers.Physical, RayFaces.Front, ref all, out var hit));
        Assert.Equal(2f, 50f - hit.T, 3);
    }

    [Fact]
    public void Ragged_outlines_make_roofs()
    {
        // Orthogonal outlines from random rectangles' unions, and star-shaped ones: the skeleton makes a roof of
        // them all or says it cannot (and the shape lies flat).
        var rng = new Random(5);
        int made = 0, tried = 0;
        for (int i = 0; i < 300; i++)
        {
            var ring = new List<Point2>();
            int n = 5 + rng.Next(12);
            for (int k = 0; k < n; k++)
            {
                double a = DetMath.Tau * (k + 0.3 * rng.NextDouble()) / n, r = 5 + 4 * rng.NextDouble();
                ring.Add(new Point2(r * Math.Cos(a), r * Math.Sin(a)));
            }
            tried++;
            var roof = StraightSkeleton.Of(ring, gables: i % 2 == 0);
            if (roof == null) continue;
            made++;
            var spec = new ShapeSpec { Kind = ShapeKind.Roof, Style = i % 2 == 0 ? RoofStyle.Gable : RoofStyle.Hip,
                                       Outline = ring.SelectMany(p => new[] { (float)p.X, (float)p.Y }).ToArray() };
            var mesh = Shapes.Make(spec, new Vector3(18, 3, 18))!;
            var report = MeshCheck.Check(mesh.Outer);
            Assert.True(report.Closed, $"star {i}: {report}");
        }
        _o.WriteLine($"{made} of {tried} star outlines made a roof");
        Assert.True(made >= tried * 0.98, $"{made} of {tried}");

        // Traced footprints are a hair off square: rectangles and L's with their corners moved by a hundredth of a
        // millimetre to two centimetres, whose ridges kink by micrometres. Every one is a closed roof.
        int squareish = 0;
        for (int i = 0; i < 400; i++)
        {
            double j = Math.Pow(10, -5 + 3.3 * rng.NextDouble());
            var basis = i % 2 == 0 ? new[] { (0.0, 0.0), (16.2, 0.0), (16.2, 8.5), (0.0, 8.5) }
                                   : new[] { (0.0, 0.0), (12.0, 0.0), (12.0, 4.0), (6.0, 4.0), (6.0, 8.0), (0.0, 8.0) };
            var outline = basis.SelectMany(p => new[] { (float)(p.Item1 + j * (rng.NextDouble() * 2 - 1)), (float)(p.Item2 + j * (rng.NextDouble() * 2 - 1)) }).ToArray();
            var spec = new ShapeSpec { Kind = ShapeKind.Roof, Style = i % 4 < 2 ? RoofStyle.Hip : RoofStyle.Gable, Outline = outline };
            var mesh = Shapes.Make(spec, new Vector3(16, 4, 8.5f))!;
            var report = MeshCheck.Check(mesh.Outer);
            Assert.True(report.Closed && report.Degenerate == 0, $"near-square {i} (moved {j:E1} m): {report}");
            if (mesh.Outer.Facets.Items.Length > 2) squareish++;
        }
        Assert.Equal(400, squareish);
    }

    [Fact]
    public void Outlines_with_holes_triangulate_to_their_area()
    {
        var outline = new List<Point2> { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };
        var holes = new List<IReadOnlyList<Point2>>
        {
            new List<Point2> { new(2, 2), new(2, 4), new(4, 4), new(4, 2) },
            new List<Point2> { new(6, 6), new(6, 8), new(8, 8), new(8, 6) },
        };
        var tris = Polygons.Triangulate(outline, holes);
        var pts = outline.Concat(holes.SelectMany(h => h)).ToList();
        double area = 0;
        for (int t = 0; t < tris.Count; t += 3)
        {
            double a = Point2.Cross(pts[tris[t + 1]] - pts[tris[t]], pts[tris[t + 2]] - pts[tris[t]]) / 2;
            Assert.True(a > 0);
            area += a;
        }
        Assert.Equal(92.0, area, 9);
        var pieces = Polygons.ConvexPieces(pts, tris);
        Assert.All(pieces, p => Assert.True(Polygons.IsConvex(pts, p)));
        Assert.True(pieces.Count < tris.Count / 3);
    }

    [Fact]
    public void Sines_from_arithmetic_match_the_library()
    {
        for (int n = 3; n <= 64; n++)
            for (int k = 0; k < n; k++)
            {
                var (s, c) = DetMath.SinCosOfTurn(k, n);
                Assert.True(Math.Abs(Math.Sin(DetMath.Tau * k / n) - s) < 1e-14);
                Assert.True(Math.Abs(Math.Cos(DetMath.Tau * k / n) - c) < 1e-14);
            }
        foreach (double a in new[] { -7.5, -3.2, -1.0, 0.3, 2.9, 6.1, 12.0 })
        {
            var (s, c) = DetMath.SinCos(a);
            Assert.True(Math.Abs(Math.Sin(a) - s) < 1e-14);
            Assert.True(Math.Abs(Math.Cos(a) - c) < 1e-14);
        }
    }

    /// <summary>A shape's surfaces each have their material: carpet on a flight's treads is met by a foot coming
    /// down on one, concrete by a ray at a riser; a roof's slopes are roof, and sound goes through its deck.</summary>
    [Fact]
    public void A_shape_s_surfaces_have_their_own_materials()
    {
        var size = new Vector3(1.2f, 2.8f, 4.48f);
        var stairs = new ShapeSpec { Kind = ShapeKind.Stairs, Steps = 16, Materials = new[] { "", "Carpet" } };
        var world = WorldOf(SolidSpec.OfShape(7, new Vector3(0, 1.4f, 0), Quaternion.Identity, size, Concrete(size), stairs));
        var all = new AcceptAll();
        Assert.True(world.Closest(new Vector3(0, 5, 0), -Vector3.UnitY, 10f, GeometryLayers.Physical, RayFaces.Front, ref all, out var down));
        Assert.Equal("Carpet", world.SurfaceOf(down).Material);
        Assert.True(world.Closest(new Vector3(0, 0.05f, -5), Vector3.UnitZ, 10f, GeometryLayers.Physical, RayFaces.Front, ref all, out var riser));
        Assert.Equal("Concrete", world.SurfaceOf(riser).Material);
        Assert.Equal("Concrete", world.SurfaceOf(riser.Solid).Material);

        var roofSize = new Vector3(12, 2, 8);
        var roof = new ShapeSpec { Kind = ShapeKind.Roof, Outline = Pairs((0, 0), (12, 0), (12, 8), (0, 8)) };
        var spec = SolidSpec.OfShape(8, new Vector3(0, 4, 0), Quaternion.Identity, roofSize, Concrete(roofSize), roof);
        Assert.Equal(new Vector3(12, Shapes.RoofDeckMetres, 8), spec.Surface.Construction.PanelSize);
        var rw = WorldOf(spec);
        Assert.True(rw.Closest(new Vector3(0, 9, 0), -Vector3.UnitY, 10f, GeometryLayers.Physical, RayFaces.Front, ref all, out var slope));
        Assert.True(rw.SurfaceOf(slope).Is(SurfaceFlags.Roof));
        Assert.True(rw.Closest(new Vector3(0, 0, 0), Vector3.UnitY, 10f, GeometryLayers.Physical, RayFaces.Front, ref all, out var under));
        Assert.False(rw.SurfaceOf(under).Is(SurfaceFlags.Roof));
    }

    /// <summary>The first answer off a ramp comes off its slope, where the mirror is, not off the top of the box
    /// round it.</summary>
    [Fact]
    public void Echoes_come_off_a_shape_s_facets()
    {
        var size = new Vector3(6f, 2f, 6f);
        var ramp = SolidSpec.OfShape(9, new Vector3(0, 1f, 0), Quaternion.Identity, size, Concrete(size), new ShapeSpec { Kind = ShapeKind.Wedge });
        var world = WorldOf(ramp);
        var arrivals = new List<EarlyReflections.Arrival>();
        var source = new Vector3(-1f, 3f, 0f); var listener = new Vector3(1f, 3f, 0f);
        EarlyReflections.Find(source, listener, world, arrivals);
        var off = Assert.Single(arrivals);
        // The slope rises 2 m over 6 m toward +Z: its plane holds the hit, and the box's top (y = 2) does not.
        var n = Vector3.Normalize(new Vector3(0, 3, -1));
        Assert.Equal(0f, Vector3.Dot(off.HitPoint - new Vector3(0, 0, -3), n), 3);
        Assert.True(off.HitPoint.Y < 1.9f);
    }

    /// <summary>A shape is struck as the part it has: a pitched roof its deck, a flight one step, not the box round it.</summary>
    [Fact]
    public void A_shape_is_struck_as_the_part_it_has()
    {
        var roofSize = new Vector3(12, 2, 8);
        var roof = StruckThings.Describe("Wood", roofSize, Vector3.UnitY, 0, 0, false, false, out _,
                                         form: new ShapeSpec { Kind = ShapeKind.Roof, Outline = Pairs((0, 0), (12, 0), (12, 8), (0, 8)) });
        Assert.Equal(StruckShape.Plate, roof.Shape);
        Assert.Equal(Shapes.RoofDeckMetres, roof.Thickness, 4);
        var asBox = StruckThings.Describe("Wood", roofSize, Vector3.UnitY, 0, 0, false, false, out _);
        Assert.NotEqual(StruckShape.Plate, asBox.Shape);
        var flight = StruckThings.Describe("Concrete", new Vector3(1.2f, 2.8f, 4.48f), Vector3.UnitY, 0, 0, false, false, out _,
                                           form: new ShapeSpec { Kind = ShapeKind.Stairs, Steps = 16 });
        Assert.Equal(StruckShape.Block, flight.Shape);
        Assert.Equal(0.175f, flight.Thickness, 3);
        Assert.Equal(0.28f, flight.Width, 3);
    }

    [Fact]
    public void Bad_numbers_are_refused()
    {
        var size = new Vector3(2, 2, 2);
        Assert.NotNull(Shapes.Problem(new ShapeSpec { Kind = ShapeKind.Prism, Outline = Pairs((0, 0), (1, 0)) }, size));
        Assert.NotNull(Shapes.Problem(new ShapeSpec { Kind = ShapeKind.Prism, Outline = Pairs((0, 0), (2, 2), (2, 0), (0, 2)) }, size));
        Assert.NotNull(Shapes.Problem(new ShapeSpec { Kind = ShapeKind.Prism, Outline = Pairs((0, 0), (2, 0), (2, 2), (0, 2)),
            Holes = new[] { Pairs((1, 1), (3, 1), (3, 3)) } }, size));
        Assert.NotNull(Shapes.Problem(new ShapeSpec { Kind = ShapeKind.Cone, Top = 1.5f }, size));
        Assert.NotNull(Shapes.Problem(new ShapeSpec { Kind = ShapeKind.Cylinder, Segments = 2 }, size));
        Assert.NotNull(Shapes.Problem(new ShapeSpec { Kind = ShapeKind.Swept, Profile = Pairs((0, 0), (1, 0), (0, 1)), Path = new float[] { 0, 0, 0, 0, 5, 0 } }, size));
        Assert.NotNull(Shapes.Problem(new ShapeSpec { Kind = ShapeKind.Swept, Profile = Pairs((0, 0), (1, 0), (0, 1)), Path = new float[] { 0, 0, 0, 5, 0, 0, 0, 0, 0.1f } }, size));
        Assert.NotNull(Shapes.Problem(new ShapeSpec { Kind = ShapeKind.Cylinder, Materials = new[] { "Wood", "Steel", "Glass" } }, size));
        Assert.Null(Shapes.Problem(new ShapeSpec { Kind = ShapeKind.Cylinder, Materials = new[] { "Wood", "Steel" } }, size));
        Assert.Equal(new[] { "slopes", "ends", "underside" }, Shapes.SurfaceNames(ShapeKind.Roof));
    }
}
