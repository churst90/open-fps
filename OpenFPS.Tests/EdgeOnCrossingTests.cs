using System.Numerics;
using OpenFPS.Audio.Acoustics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Acoustics;
using OpenFPS.Geometry;

namespace OpenFPS.Tests;

/// <summary>
/// A solid gone into by its edge pays what the line runs through of it (Constructions, "A solid entered by
/// its edge"). In Marlow Tower's floor 1 stairwell a car on Dock Street went in through the 15 cm edge of
/// the floor slab between two storeys of brick and out of its top, and was charged 15 cm of concrete crossed
/// face to face where it ran through 0.8 m of it: -35/-52/-80 dB for 4 m of road, where the brick front
/// beside it lets through -40/-62/-87.
/// </summary>
public class EdgeOnCrossingTests
{
    private static float Db(float gain) => -20f * MathF.Log10(MathF.Max(1e-12f, gain));
    private static float Db(Vector3 g, int band) => Db(band == 0 ? g.X : band == 1 ? g.Y : g.Z);

    private static MaterialProperties P(string m) { AcousticRegistry.Initialize(); return AcousticRegistry.GetProperties(m); }

    /// <summary>A slab 8 by 8 m and 15 cm thick about the origin.</summary>
    private static readonly Vector3 Slab = new(8f, 0.15f, 8f);

    // ── How far a line runs inside ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_line_across_a_sheet_face_to_face_runs_nothing_inside_at_any_angle()
    {
        Assert.Equal(0f, Constructions.RunInsideLocal(new Vector3(0, -2, 0), new Vector3(0, 2, 0), Slab));
        // 80 degrees off the normal: a chord of 0.86 m through 15 cm, still a panel.
        var d = new Vector3(MathF.Sin(80f * MathF.PI / 180f), MathF.Cos(80f * MathF.PI / 180f), 0f);
        Assert.Equal(0f, Constructions.RunInsideLocal(-d * 2f, d * 2f, Slab));
    }

    [Fact]
    public void A_line_into_a_slab_by_its_edge_runs_its_chord()
    {
        // In by the edge at x -4, out of the top at x -1: rising 15 cm over 8 m.
        var a = new Vector3(-5f, 0f, 0f);
        var b = new Vector3(3f, 0.15f, 0f);
        float run = Constructions.RunInsideLocal(a, b, Slab);
        float want = Vector3.Distance(a + (b - a) * (1f / 8f), a + (b - a) * (4f / 8f));
        Assert.InRange(run, want - 0.01f, want + 0.01f);
    }

    [Fact]
    public void A_corner_clipped_or_a_face_touched_is_still_the_panel()
    {
        // In by the edge 1 cm under the top and out of the top 10 cm on: shorter than the slab is thick.
        Assert.Equal(0f, Constructions.RunInsideLocal(new Vector3(-4.5f, 0.055f, 0f), new Vector3(-3.5f, 0.095f, 0f), Slab));
        // Along the top face: touching, not inside (a leg run along a joint pays the panel, as before).
        Assert.Equal(0f, Constructions.RunInsideLocal(new Vector3(-6f, 0.075f, 0f), new Vector3(6f, 0.075f, 0f), Slab));
    }

    [Fact]
    public void A_turned_box_runs_what_the_same_box_unturned_does()
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
        var centre = new Vector3(3f, 2f, -7f);
        var a = new Vector3(-5f, 0f, 0.5f);
        var b = new Vector3(3f, 0.15f, 0.2f);
        float plain = Constructions.RunInsideLocal(a, b, Slab);
        float turned = Constructions.RunInside(centre + Vector3.Transform(a, q), centre + Vector3.Transform(b, q), centre, Slab, q);
        Assert.True(plain > 0.5f);
        Assert.InRange(turned, plain - 1e-3f, plain + 1e-3f);
    }

    // ── What it is charged ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_run_pays_what_that_much_of_the_stuff_lets_through_and_never_less_than_the_panel()
    {
        var concrete = P("Concrete");
        var panel = Constructions.Of(1f, 1.8f, concrete, Slab, WallBuild.Solid, 1, Vector3.UnitY);
        var run = Constructions.Of(1f, 1.8f, concrete, Slab, WallBuild.Solid, 1, Vector3.UnitY, 0.8f);
        var thick = WallTransmission.BandGains(concrete, new Vector3(8f, 0.8f, 8f), WallBuild.Solid);
        for (int band = 0; band < 3; band++)
        {
            Assert.True(Db(run.Gains, band) >= Db(panel.Gains, band), $"band {band}: a run of 0.8 m let through more than the 15 cm panel");
            Assert.InRange(Db(run.Gains, band), Db(band == 0 ? thick.Low : band == 1 ? thick.Mid : thick.High) - 0.2f,
                           Db(band == 0 ? thick.Low : band == 1 ? thick.Mid : thick.High) + 0.2f);
        }
        Assert.Equal(panel.Gains, run.AsPanel);
    }

    [Fact]
    public void A_two_leaf_build_run_along_keeps_its_panel()
    {
        var stud = new WallBuild(0.0125f, 0.6f);
        var wall = new Vector3(0.1f, 2.7f, 6f);
        var c = Constructions.Of(1f, 3f, P("Plaster"), wall, stud, 1, Vector3.UnitX, 2f);
        Assert.Equal(0f, c.Run);
        Assert.Equal(c.AsPanel, c.Gains);
    }

    [Fact]
    public void The_correction_is_the_run_over_the_panel_and_one_where_everything_is_crossed_face_to_face()
    {
        var concrete = P("Concrete");
        var brick = Constructions.Of(0f, 0.35f, P("Brick"), new Vector3(0.35f, 3f, 8f), WallBuild.Solid, 1, Vector3.UnitX);
        var edge = Constructions.Of(5f, 5.8f, concrete, Slab, WallBuild.Solid, 1, Vector3.UnitY, 0.8f);
        Assert.Equal(Vector3.One, Constructions.InPlaneCorrection(new List<Constructions.Crossing> { brick }, Vector3.UnitX));
        var k = Constructions.InPlaneCorrection(new List<Constructions.Crossing> { brick, edge }, Vector3.UnitX);
        for (int band = 0; band < 3; band++)
        {
            float want = (band == 0 ? edge.Gains.X / edge.AsPanel.X : band == 1 ? edge.Gains.Y / edge.AsPanel.Y : edge.Gains.Z / edge.AsPanel.Z);
            float got = band == 0 ? k.X : band == 1 ? k.Y : k.Z;
            Assert.InRange(got, want * 0.99f, want * 1.01f);
        }
    }

    // ── A slab's edge in a facade ───────────────────────────────────────────────────────────────

    /// <summary>A brick front 35 cm thick at z 0, a storey below and one above a 15 cm slab whose edge is
    /// flush with the front's outer face and which runs 10 m into the building.</summary>
    private static List<EntityDefinition> FacadeBoxes()
    {
        AcousticRegistry.EnsureInitialized();
        var defs = new List<EntityDefinition>();
        int id = 1;
        void Box(Vector3 pos, Vector3 size, string material) => defs.Add(new EntityDefinition
        {
            EntityId = id++,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = pos, Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
            Material = new MaterialComponent { Material = material },
        });
        Box(new Vector3(0, 1.5f, 0), new Vector3(40f, 3f, 0.35f), "Brick");
        Box(new Vector3(0, 3.075f, 4.825f), new Vector3(40f, 0.15f, 10f), "Concrete");
        Box(new Vector3(0, 4.575f, 0), new Vector3(40f, 2.85f, 0.35f), "Brick");
        return defs;
    }

    /// <summary>The facade as the client's world holds it (the hand-rolled tracer's).</summary>
    private static WorldSnapshot Facade()
    {
        var world = new ClientWorldState();
        world.Clear(new Vector3(200, 40, 200));
        foreach (var d in FacadeBoxes()) world.RegisterDefinition(d);
        return world.GetSnapshot();
    }

    /// <summary>The facade's routes (OpeningRoutesBarrierTests' way of making them).</summary>
    private static OpeningRoutes FacadeRoutes()
    {
        var defs = FacadeBoxes();
        var world = new WorldSnapshot();
        foreach (var d in defs)
            world.Entities[d.EntityId] = new EntitySnapshot { Id = d.EntityId, Definition = d, Transform = d.Transform };
        world.AcousticMap = EntityAcoustics.GenerateRegions(defs, new Vector3(200, 40, 200), new Vector3(-100, -5, -100));
        return new SpatialAcoustics().RoutesFor(world)!;
    }

    /// <summary>From the street 25 m out, 0.5 m up, to an ear in the room over the slab: the line meets the
    /// front at <paramref name="atFront"/> metres up.</summary>
    private static (Vector3 Source, Vector3 Ear) Line(float atFront)
    {
        var source = new Vector3(0, 0.5f, -25f);
        float slope = (atFront - 0.5f) / (25f - 0.175f);
        float earZ = 6f;
        return (source, new Vector3(0, 0.5f + slope * (earZ + 25f), earZ));
    }

    private static Vector3 Tracer(WorldSnapshot world, Vector3 a, Vector3 b)
    {
        new SpatialService().GetOcclusionData(world, a, b, out _, out _, out float l, out float m, out float h);
        return new Vector3(l, m, h);
    }

    [Fact]
    public void A_slab_edge_in_a_facade_lets_through_no_more_than_the_brick_above_it()
    {
        var world = Facade();
        var (s1, e1) = Line(3.07f);   // into the slab's edge, out of its top
        var (s2, e2) = Line(3.4f);    // through the brick over it
        var edge = Tracer(world, s1, e1);
        var brick = Tracer(world, s2, e2);
        for (int band = 0; band < 3; band++)
            Assert.True(Db(edge, band) >= Db(brick, band) - 0.5f,
                        $"band {band}: the slab's edge {Db(edge, band):F1} dB against the brick over it {Db(brick, band):F1} dB");
        // And it pays more than the slab's own panel.
        var panel = WallTransmission.BandGains("Concrete", new Vector3(40f, 0.15f, 10f), WallBuild.Solid);
        Assert.True(Db(edge, 0) > Db(panel.Low) + 6f, $"low band {Db(edge, 0):F1} dB, the slab's panel {Db(panel.Low):F1}");
    }

    [Fact]
    public void The_routes_correct_the_simulators_panels_only_where_the_line_goes_in_by_an_edge()
    {
        var world = Facade();
        var routes = FacadeRoutes();
        var (s1, e1) = Line(3.07f);
        var (s2, e2) = Line(3.4f);
        Assert.Equal(Vector3.One, routes.InPlaneCorrection(s2, e2));
        var k = routes.InPlaneCorrection(s1, e1);
        Assert.True(k.X < 0.5f && k.Y < 0.5f, $"correction {Db(k.X):F1}/{Db(k.Y):F1}/{Db(k.Z):F1} dB");
        // The slab's panel figure times the correction is what the tracer charges the same line.
        var panel = WallTransmission.BandGains("Concrete", new Vector3(40f, 0.15f, 10f), WallBuild.Solid);
        var traced = Tracer(world, s1, e1);
        Assert.InRange(Db(panel.Mid * k.Y), Db(traced, 1) - 0.5f, Db(traced, 1) + 0.5f);
    }
}
