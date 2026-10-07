using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Geometry stage 1 (docs/GEOMETRY.md): the triangle world answers what the box tests answered, on the
/// server and every client alike, and builds again only what changed. The parity harness (AudioLab
/// --geometry-parity) runs the same comparisons on the shipped maps; these hold the rules on small
/// scenes made to hit them.
/// </summary>
public class GeometryStage1Tests
{
    private static Surface Plain(string material = "Concrete")
        => EntityGeometry.SurfaceOf(material, Vector3.One, 0, 0, false, 0, 0, emitter: false, moves: false, doorLeaf: false, name: null);

    private static SolidSpec Box(int owner, Vector3 at, Vector3 size, float yaw = 0f, string material = "Concrete")
        => new(owner, at, Quaternion.CreateFromYawPitchRoll(yaw, 0, 0), size,
               EntityGeometry.SurfaceOf(material, size, 0, 0, false, 0, 0, false, false, false, null));

    /// <summary>A scattered town of boxes, turned about the vertical, seeded.</summary>
    private static List<SolidSpec> Town(int seed, int count = 300, float extent = 600f)
    {
        var rng = new Random(seed);
        var list = new List<SolidSpec> { Box(1, new Vector3(0, -0.25f, 0), new Vector3(2000, 0.5f, 2000), 0f, "Dirt") };
        for (int i = 0; i < count; i++)
        {
            var size = new Vector3(0.2f + (float)rng.NextDouble() * 20f, 0.1f + (float)rng.NextDouble() * 8f, 0.2f + (float)rng.NextDouble() * 20f);
            var at = new Vector3((float)(rng.NextDouble() * extent - extent / 2), size.Y / 2 + (float)rng.NextDouble() * 6f, (float)(rng.NextDouble() * extent - extent / 2));
            list.Add(Box(100 + i, at, size, (float)(rng.NextDouble() * Math.PI * 2), i % 3 == 0 ? "Brick" : "Wood"));
        }
        return list;
    }

    private static Vector3 Dir(Random rng)
    {
        float z = (float)(rng.NextDouble() * 2 - 1), a = (float)(rng.NextDouble() * Math.PI * 2), r = MathF.Sqrt(1 - z * z);
        return new Vector3(r * MathF.Cos(a), z, r * MathF.Sin(a));
    }

    [Fact]
    public void A_box_is_twelve_outward_triangles_and_closed()
    {
        var mesh = ShapeLibrary.Box(new Vector3(2, 4, 6));
        Assert.Equal(12, mesh.TriangleCount);
        Assert.True(mesh.Closed && mesh.Convex);
        for (int t = 0; t < 12; t++)
        {
            Vector3 a = mesh.Vertices[mesh.Indices[3 * t]], b = mesh.Vertices[mesh.Indices[3 * t + 1]], c = mesh.Vertices[mesh.Indices[3 * t + 2]];
            var n = Vector3.Cross(b - a, c - a);
            Assert.True(Vector3.Dot(n, (a + b + c) / 3f) > 0f, $"triangle {t} faces in");
        }
        // Every edge shared by exactly two triangles, once each way: watertight.
        var edges = new Dictionary<(int, int), int>();
        for (int t = 0; t < 12; t++)
            for (int k = 0; k < 3; k++)
            {
                var e = (mesh.Indices[3 * t + k], mesh.Indices[3 * t + (k + 1) % 3]);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        foreach (var ((a, b), count) in edges)
        {
            Assert.Equal(1, count);
            Assert.True(edges.ContainsKey((b, a)));
        }
    }

    [Fact]
    public void Rays_meet_what_the_box_tests_meet()
    {
        var specs = Town(3);
        var world = new TriangleWorldBuilder(250f).Build(specs, Array.Empty<SolidSpec>());
        var rng = new Random(9);
        var all = new AcceptAll();
        for (int i = 0; i < 3000; i++)
        {
            var o = new Vector3((float)(rng.NextDouble() * 600 - 300), (float)(rng.NextDouble() * 12), (float)(rng.NextDouble() * 600 - 300));
            var d = Dir(rng);
            float best = float.MaxValue; int owner = -1;
            foreach (var s in specs)
                if (GeometryUtils.RayIntersectsOBB(o, d, s.Position, s.BoxSize, s.Rotation, out float t) && t < best && t <= 80f) { best = t; owner = s.Owner; }
            bool hit = world.Enter(o, d, 80f, GeometryLayers.Physical, ref all, out var h);
            Assert.Equal(owner >= 0, hit);
            if (hit) Assert.True(MathF.Abs(h.T - best) < 1e-3f + 1e-5f * best, $"ray {i}: box {best} triangles {h.T}");
        }
    }

    [Fact]
    public void The_ground_is_the_box_tests_top_exactly()
    {
        var specs = Town(4);
        var world = new TriangleWorldBuilder(250f).Build(specs, Array.Empty<SolidSpec>());
        var rng = new Random(2);
        var all = new AcceptAll();
        for (int i = 0; i < 2000; i++)
        {
            var p = new Vector3((float)(rng.NextDouble() * 600 - 300), (float)(rng.NextDouble() * 15), (float)(rng.NextDouble() * 600 - 300));
            float best = -1000f;
            var probes = new[] { p, p + new Vector3(0.3f, 0, 0), p - new Vector3(0.3f, 0, 0), p + new Vector3(0, 0, 0.3f), p - new Vector3(0, 0, 0.3f) };
            foreach (var s in specs)
            {
                float top = s.Position.Y + s.BoxSize.Y / 2f;
                if (top > p.Y + 0.4f) continue;
                if (probes.Any(q => GeometryUtils.IsPointInOBB(q, s.Position, new Vector3(s.BoxSize.X, 40000f, s.BoxSize.Z), s.Rotation)))
                    best = MathF.Max(best, top);
            }
            float y = world.Ground(p, 0.3f, 0.4f, GeometryLayers.Ground, ref all, out _, out _);
            Assert.True(MathF.Abs(y - best) < 1e-4f, $"at {p}: box {best} triangles {y}");
        }
    }

    [Fact]
    public void A_body_meets_a_solid_as_it_met_the_box()
    {
        var specs = Town(5, 120, 200);
        var world = new TriangleWorldBuilder(250f).Build(specs, Array.Empty<SolidSpec>());
        var rng = new Random(7);
        var near = new List<SolidRef>();
        var all = new AcceptAll();
        int touching = 0;
        for (int i = 0; i < 3000; i++)
        {
            var s = specs[1 + rng.Next(specs.Count - 1)];
            var centre = s.Position + new Vector3((float)(rng.NextDouble() * 2 - 1) * (s.BoxSize.X / 2 + 0.5f), (float)(rng.NextDouble() * 2 - 1) * s.BoxSize.Y,
                                                  (float)(rng.NextDouble() * 2 - 1) * (s.BoxSize.Z / 2 + 0.5f));
            near.Clear();
            world.Overlapping(centre - new Vector3(1, 2, 1), centre + new Vector3(1, 2, 1), GeometryLayers.Movement, ref all, near);
            foreach (var r in near)
            {
                var (bc, bs, br) = world.BoxOf(r);
                foreach (bool down in new[] { false, true })
                {
                    var m = Matrix4x4.CreateTranslation(-bc) * Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(br));
                    var box = GeometryUtils.GetCylinderAABBOverlap(-bs / 2, bs / 2, Vector3.Transform(centre, m), 0.3f, 1.65f, down);
                    if (box.IsColliding) box.Normal = Vector3.TransformNormal(box.Normal, Matrix4x4.CreateFromQuaternion(br));
                    var tri = SolidContact.CylinderOverlap(world, r, centre, 0.3f, 1.65f, down);
                    if (MathF.Max(box.Penetration, tri.Penetration) < 1e-4f && box.IsColliding != tri.IsColliding) continue;   // touching, to a hair
                    Assert.Equal(box.IsColliding, tri.IsColliding);
                    if (!box.IsColliding) continue;
                    touching++;
                    Assert.True(MathF.Abs(box.Penetration - tri.Penetration) < 1e-3f, $"depth {box.Penetration} / {tri.Penetration}");
                    // An axis exactly between two edges may go out by either: only the depth is held then.
                    var local = Vector3.Transform(centre - bc, Quaternion.Inverse(br));
                    var ways = new[] { bs.X / 2 - local.X, local.X + bs.X / 2, bs.Z / 2 - local.Z, local.Z + bs.Z / 2 }.OrderBy(x => x).ToArray();
                    if (ways[1] - ways[0] > 1e-3f) Assert.True(Vector3.Distance(box.Normal, tri.Normal) < 2e-3f, $"way out {box.Normal} / {tri.Normal}");
                }
            }
        }
        Assert.True(touching > 500);
    }

    [Fact]
    public void Server_and_client_build_the_same_bits_whatever_order_they_hold_things_in()
    {
        var specs = Town(6);
        var shuffled = specs.OrderBy(s => (s.Owner * 7919) % 1000).ToList();
        var a = new TriangleWorldBuilder(250f) { Parallel = false }.Build(specs, Array.Empty<SolidSpec>());
        var b = new TriangleWorldBuilder(250f) { Parallel = true }.Build(shuffled, Array.Empty<SolidSpec>());
        Assert.Equal(a.InstanceCount, b.InstanceCount);
        for (int i = 0; i < a.InstanceCount; i++) Assert.Equal(a.Instance(i).Piece.Signature, b.Instance(i).Piece.Signature);
        var rng = new Random(11);
        var all = new AcceptAll();
        for (int i = 0; i < 2000; i++)
        {
            var o = new Vector3((float)(rng.NextDouble() * 600 - 300), (float)(rng.NextDouble() * 12), (float)(rng.NextDouble() * 600 - 300));
            var d = Dir(rng);
            bool ha = a.Closest(o, d, 100f, GeometryLayers.Physical, RayFaces.Both, ref all, out var x);
            bool hb = b.Closest(o, d, 100f, GeometryLayers.Physical, RayFaces.Both, ref all, out var y);
            Assert.Equal(ha, hb);
            if (ha) { Assert.Equal(BitConverter.SingleToInt32Bits(x.T), BitConverter.SingleToInt32Bits(y.T)); Assert.Equal(x.Owner, y.Owner); Assert.Equal(x.Normal, y.Normal); }
            Assert.Equal(BitConverter.SingleToInt32Bits(a.Ground(o, 0.3f, 0.4f, GeometryLayers.Ground, ref all, out _, out int oa)),
                         BitConverter.SingleToInt32Bits(b.Ground(o, 0.3f, 0.4f, GeometryLayers.Ground, ref all, out _, out int ob)));
            Assert.Equal(oa, ob);
        }
    }

    [Fact]
    public void Of_two_surfaces_in_the_same_place_the_smaller_thing_is_met()
    {
        // A drive laid flush on the ground: the same top, Concrete over Dirt, whichever has the lower id.
        foreach (int driveId in new[] { 5, 500 })
        {
            var specs = new List<SolidSpec>
            {
                Box(50, new Vector3(0, -0.25f, 0), new Vector3(100, 0.5f, 100), 0f, "Dirt"),
                Box(driveId, new Vector3(3, -0.25f, 0), new Vector3(4, 0.5f, 10), 0f, "Concrete"),
            };
            var world = new TriangleWorldBuilder(250f).Build(specs, Array.Empty<SolidSpec>());
            var all = new AcceptAll();
            world.Ground(new Vector3(3, 0, 0), 0.3f, 0.4f, GeometryLayers.Ground, ref all, out var solid, out int owner);
            Assert.Equal(driveId, owner);
            Assert.Equal("Concrete", world.SurfaceOf(solid).Material);
        }
    }

    [Fact]
    public void Only_the_tile_that_changed_is_built_again_and_the_answers_are_a_whole_builds()
    {
        var specs = Town(8);
        var builder = new TriangleWorldBuilder(250f);
        builder.Build(specs, Array.Empty<SolidSpec>());
        int tiles = builder.TileCount;
        var moved = specs.ToList();
        int k = moved.FindIndex(s => s.Owner == 150);
        moved[k] = moved[k] with { Position = moved[k].Position + new Vector3(0.5f, 0, 0) };
        var after = builder.Build(moved, Array.Empty<SolidSpec>());
        Assert.Equal(1, builder.LastBuilt);
        Assert.Equal(tiles - 1, builder.LastKept);
        var whole = new TriangleWorldBuilder(250f).Build(moved, Array.Empty<SolidSpec>());
        var rng = new Random(1);
        var all = new AcceptAll();
        for (int i = 0; i < 1000; i++)
        {
            var o = new Vector3((float)(rng.NextDouble() * 600 - 300), (float)(rng.NextDouble() * 12), (float)(rng.NextDouble() * 600 - 300));
            var d = Dir(rng);
            bool ha = after.Closest(o, d, 100f, GeometryLayers.Physical, RayFaces.Both, ref all, out var x);
            bool hb = whole.Closest(o, d, 100f, GeometryLayers.Physical, RayFaces.Both, ref all, out var y);
            Assert.Equal(ha, hb);
            if (ha) Assert.Equal(BitConverter.SingleToInt32Bits(x.T), BitConverter.SingleToInt32Bits(y.T));
        }
    }

    [Fact]
    public void A_door_leaf_moves_with_a_new_pose_and_no_build()
    {
        var leaf = Box(77, new Vector3(0, 1, 0), new Vector3(1, 2, 0.05f));
        var builder = new TriangleWorldBuilder(250f);
        var shut = builder.Build(new[] { Box(1, new Vector3(0, -0.25f, 0), new Vector3(50, 0.5f, 50)) }, new[] { leaf });
        var all = new AcceptAll();
        // Shut, the leaf is in the way of a ray through the doorway.
        Assert.True(shut.Closest(new Vector3(0, 1, -2), Vector3.UnitZ, 4f, GeometryLayers.Physical, RayFaces.Front, ref all, out var h));
        Assert.Equal(77, h.Owner);
        // Swung a quarter turn about its hinge (at x = -0.5): out of the way.
        var swung = builder.Move(new[] { (77, new Vector3(-0.5f, 1, 0.5f), Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0, 0)) });
        Assert.Same(shut.Instance(0).Piece, swung.Instance(0).Piece);   // the tile itself untouched
        Assert.False(swung.Closest(new Vector3(0, 1, -2), Vector3.UnitZ, 4f, GeometryLayers.Physical, RayFaces.Front, ref all, out _));
        Assert.True(swung.Closest(new Vector3(-0.5f, 1, -2), Vector3.UnitZ, 4f, GeometryLayers.Physical, RayFaces.Front, ref all, out var on));
        Assert.Equal(77, on.Owner);
        Assert.True(MathF.Abs(on.T - 2f) < 0.03f);
    }

    [Fact]
    public void A_ray_that_starts_inside_meets_it_at_once_and_reports_the_face_it_came_in_by()
    {
        var world = new TriangleWorldBuilder(250f).Build(new[] { Box(9, Vector3.Zero, new Vector3(4, 4, 4)) }, Array.Empty<SolidSpec>());
        var all = new AcceptAll();
        Assert.True(world.Enter(new Vector3(0.5f, 0, 0), Vector3.UnitX, 10f, GeometryLayers.Physical, ref all, out var h));
        Assert.True(h.Inside);
        Assert.Equal(0f, h.T);
        Assert.True(Vector3.Distance(h.Normal, -Vector3.UnitX) < 1e-5f);   // in by the -X face
        // A survey ray from inside passes out unseen, as RayHitsOBB did.
        Assert.False(world.Closest(new Vector3(0.5f, 0, 0), Vector3.UnitX, 10f, GeometryLayers.Physical, RayFaces.Front, ref all, out _));
    }

    [Fact]
    public void A_mover_and_a_surface_layer_filter_what_queries_see()
    {
        var emitter = new SolidSpec(5, new Vector3(0, 1, 5), Quaternion.Identity, Vector3.One,
                                    EntityGeometry.SurfaceOf("Metal", Vector3.One, 0, 0, false, 0, 0, emitter: true, moves: false, doorLeaf: false, name: null));
        var world = new TriangleWorldBuilder(250f).Build(new[] { emitter }, Array.Empty<SolidSpec>());
        var all = new AcceptAll();
        // A sound source's own box is in the way of a body, not of sound.
        Assert.True(world.Any(new Vector3(0, 1, 0), Vector3.UnitZ, 10f, GeometryLayers.Movement, RayFaces.Both, ref all));
        Assert.False(world.Any(new Vector3(0, 1, 0), Vector3.UnitZ, 10f, GeometryLayers.Acoustics, RayFaces.Both, ref all));
    }

    [Fact]
    public void The_enclosure_survey_is_the_same_through_the_triangles()
    {
        var specs = new List<SolidSpec>
        {
            Box(1, new Vector3(0, -0.25f, 0), new Vector3(60, 0.5f, 60)),
            Box(2, new Vector3(0, 1.5f, 5), new Vector3(10, 3, 0.3f), 0f, "Brick"),
            Box(3, new Vector3(0, 1.5f, -5), new Vector3(10, 3, 0.3f), 0f, "Brick"),
            Box(4, new Vector3(5, 1.5f, 0), new Vector3(0.3f, 3, 10), 0f, "Wood"),
            Box(5, new Vector3(0, 3.1f, 0), new Vector3(10, 0.2f, 10), 0f, "Concrete"),
        };
        var acoustic = specs.Select(s => s with { Surface = s.Surface with { Layers = GeometryLayers.Acoustics } }).ToList();
        var world = new TriangleWorldBuilder(250f).Build(acoustic, Array.Empty<SolidSpec>());
        var boxes = specs.Select(s => new Enclosure.Solid(s.Position, s.BoxSize, s.Rotation, s.Surface.Material)).ToList();
        foreach (var at in new[] { new Vector3(0, 1.6f, 0), new Vector3(-2, 1.2f, 1), new Vector3(8, 1.6f, 8) })
        {
            var a = Enclosure.Look(at, boxes);
            var b = Enclosure.Look(at, world);
            Assert.Equal(a.OpenFraction, b.OpenFraction, 4);
            Assert.Equal(a.Enclosure, b.Enclosure, 3);
            Assert.Equal(a.MeanFreePathMetres, b.MeanFreePathMetres, 2);
        }
    }

    [Fact]
    public void A_box_turned_by_a_six_digit_quaternion_keeps_its_top_exactly()
    {
        // 0.707082/0.707131 is not unit length; Vector3.Transform scales the height by its length squared.
        var q = new Quaternion(0, 0.707082f, 0, 0.707131f);
        var spec = new SolidSpec(1, new Vector3(386.5728f, 0.15f, 791.6138f), q, new Vector3(792.179f, 0.3f, 3.4f), Plain("Gravel"));
        var world = new TriangleWorldBuilder(250f).Build(new[] { spec }, Array.Empty<SolidSpec>());
        var all = new AcceptAll();
        float y = world.Ground(new Vector3(385.744f, 0.3f, 922.255f), 0.3f, 0.4f, GeometryLayers.Ground, ref all, out _, out _);
        Assert.Equal(0.15f + 0.3f / 2f, y);
    }
}
