using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;
using Xunit.Abstractions;
using OpenFPS.Geometry;
using Heightfield = OpenFPS.Geometry.Heightfield;
using SolidSpec = OpenFPS.Geometry.SolidSpec;
using TriangleWorldBuilder = OpenFPS.Geometry.TriangleWorldBuilder;

namespace OpenFPS.Tests;

/// <summary>
/// The barrier search (OpeningRoutes.BarrierPathDifference) as a car drives past: the answer changes only
/// as fast as the geometry does. Guards "a siren pop through for an instant" (Cody, 2026-10-02): cars
/// 150-300 m off went -80 to -20 dB and back in a third of a second, because the route over an obstacle
/// was believed only when no other box touched it (a pier, the next storey), leaving only what came through.
/// </summary>
public class OpeningRoutesBarrierTests
{
    private readonly ITestOutputHelper _o;
    public OpeningRoutesBarrierTests(ITestOutputHelper o) => _o = o;

    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-6f, g));

    /// <summary>The level of a route this much out of its way over this distance, mid band, as the
    /// worker charges it (AsyncAcousticWorker.BuildSimPath): Maekawa, and spreading over the longer way.</summary>
    private static float MidDb(float delta, float dist)
    {
        var (_, m, _) = Diffraction.BandGains(delta);
        return Db(m * dist / (dist + delta));
    }

    private static OpeningRoutes Routes(List<(Vector3 Centre, Vector3 Size, string Material)> boxes)
    {
        AcousticRegistry.EnsureInitialized();
        var defs = new List<EntityDefinition>();
        int id = 1;
        foreach (var (c, s, m) in boxes)
            defs.Add(new EntityDefinition
            {
                EntityId = id++,
                Type = EntityType.StaticObject,
                Transform = new Transform { Position = c, Rotation = Quaternion.Identity, Scale = Vector3.One },
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = s, IsSolid = true },
                Material = new MaterialComponent { Material = m },
            });
        var world = new WorldSnapshot();
        foreach (var d in defs)
            world.Entities[d.EntityId] = new EntitySnapshot { Id = d.EntityId, Definition = d, Transform = d.Transform };
        world.AcousticMap = EntityAcoustics.GenerateRegions(defs, new Vector3(600, 60, 600), new Vector3(-300, -5, -300));
        return new SpatialAcoustics().RoutesFor(world)!;
    }

    /// <summary>
    /// A park's 1.1 m wall with a 0.5 m pier every 7.5 m, the listener 70 m in front, a car passing 80 m
    /// behind, the line grazing the wall top. A route crossing at a pier was thrown out, and the car went
    /// from -7 dB to the wall's full transmission, -61, and back.
    /// </summary>
    [Fact]
    public void A_pier_on_a_wall_the_sound_goes_over_does_not_take_the_route_away()
    {
        var boxes = new List<(Vector3, Vector3, string)>
        {
            (new Vector3(0, -0.1f, 0), new Vector3(400, 0.2f, 400), "Concrete"),       // the ground
            (new Vector3(0, 0.55f, 0), new Vector3(110, 1.1f, 0.3f), "Concrete"),      // the wall
        };
        for (float x = -52.5f; x <= 52.5f; x += 7.5f)
            boxes.Add((new Vector3(x, 1.8f, 0), new Vector3(0.5f, 1.4f, 0.3f), "Concrete"));   // its piers
        var routes = Routes(boxes);

        var ear = new Vector3(0, 1.65f, 70);
        float worstStep = 0f, previous = float.NaN;
        int unverified = 0, steps = 0;
        for (float x = -20f; x <= 20f; x += 0.1f, steps++)
        {
            var car = new Vector3(x, 0.45f, -80);
            float delta = routes.BarrierPathDifference(car, ear, out _, out bool verified);
            if (!verified) { unverified++; _o.WriteLine($"x {x:F1}: no route over the wall (worst box {delta:F3} m)"); continue; }
            float mid = MidDb(delta, Vector3.Distance(car, ear));
            if (!float.IsNaN(previous) && MathF.Abs(mid - previous) > 1f) _o.WriteLine($"x {x:F1}: {previous:F1} -> {mid:F1} dB ({delta:F4} m)");
            if (!float.IsNaN(previous)) worstStep = MathF.Max(worstStep, MathF.Abs(mid - previous));
            previous = mid;
        }
        _o.WriteLine($"{steps} positions, {unverified} without a route; largest step between neighbours {worstStep:F2} dB");
        Assert.Equal(0, unverified);
        // Within 3 dB: at a pier the string goes over it (8 dB) rather than round its side (5), which the
        // search cannot find against the wall the pier stands on.
        Assert.True(worstStep < 3.5f, $"the mid band stepped {worstStep:F2} dB between positions 10 cm apart");
    }

    /// <summary>
    /// A six-storey building as the city builds them (a wall box per side per storey, a slab over each)
    /// between a listener and a passing car. No route round one box clears the rest; over the roof is
    /// always there, the string pulled tight over every storey in the vertical plane (ISO 9613-2, CNOSSOS-EU).
    /// </summary>
    [Fact]
    public void Over_a_building_of_many_boxes_there_is_always_a_way()
    {
        var boxes = new List<(Vector3, Vector3, string)>
        {
            (new Vector3(0, -0.1f, 0), new Vector3(400, 0.2f, 400), "Concrete"),
        };
        const float storey = 3f, w = 40f, d = 20f;
        for (int f = 0; f < 6; f++)
        {
            float y0 = f * storey;
            boxes.Add((new Vector3(0, y0 + 1.375f, -d / 2), new Vector3(w, 2.75f, 0.35f), "Brick"));
            boxes.Add((new Vector3(0, y0 + 1.375f, d / 2), new Vector3(w, 2.75f, 0.35f), "Brick"));
            boxes.Add((new Vector3(-w / 2, y0 + 1.375f, 0), new Vector3(0.35f, 2.75f, d), "Brick"));
            boxes.Add((new Vector3(w / 2, y0 + 1.375f, 0), new Vector3(0.35f, 2.75f, d), "Brick"));
            boxes.Add((new Vector3(0, y0 + 2.875f, 0), new Vector3(w, 0.25f, d), "Concrete"));
        }
        var routes = Routes(boxes);

        var ear = new Vector3(0, 1.65f, 60);
        float worstStep = 0f, previous = float.NaN, quietest = 0f;
        int unverified = 0;
        for (float x = -10f; x <= 10f; x += 0.25f)
        {
            var car = new Vector3(x, 0.45f, -50);
            float delta = routes.BarrierPathDifference(car, ear, out _, out bool verified);
            if (!verified) { unverified++; continue; }
            float mid = MidDb(delta, Vector3.Distance(car, ear));
            quietest = MathF.Min(quietest, mid);
            if (!float.IsNaN(previous)) worstStep = MathF.Max(worstStep, MathF.Abs(mid - previous));
            previous = mid;
        }
        _o.WriteLine($"{unverified} positions without a route; mid band down to {quietest:F1} dB; largest step {worstStep:F2} dB");
        Assert.Equal(0, unverified);
        Assert.True(worstStep < 1f, $"the mid band stepped {worstStep:F2} dB between positions 25 cm apart");
        // Over an 18 m building is a long way round: the barrier's ceiling, not a grazing loss.
        Assert.True(quietest < -20f, $"over the roof came out at {quietest:F1} dB");
    }

    /// <summary>A closed brick room round the listener (roofed, so there is no way over the top), the ground,
    /// and whatever else is asked for inside it.</summary>
    private static List<(Vector3, Vector3, string)> ClosedRoom(params (Vector3, Vector3, string)[] inside)
    {
        var boxes = new List<(Vector3, Vector3, string)>
        {
            (new Vector3(0, -0.1f, 0), new Vector3(400, 0.2f, 400), "Concrete"),
            (new Vector3(0, 1.375f, -5), new Vector3(10, 2.75f, 0.35f), "Brick"),
            (new Vector3(0, 1.375f, 5), new Vector3(10, 2.75f, 0.35f), "Brick"),
            (new Vector3(-5, 1.375f, 0), new Vector3(0.35f, 2.75f, 10), "Brick"),
            (new Vector3(5, 1.375f, 0), new Vector3(0.35f, 2.75f, 10), "Brick"),
            (new Vector3(0, 2.875f, 0), new Vector3(10.4f, 0.25f, 10.4f), "Concrete"),
        };
        boxes.AddRange(inside);
        return boxes;
    }

    private static Vector3 Db3(Vector3 g) => new(Db(g.X), Db(g.Y), Db(g.Z));

    /// <summary>
    /// Marlow Tower's floor 1 stairwell (2026-10-11): a 1.07 m concrete guard just inside a brick front, a car
    /// on the street beyond. The way over the guard is a few centimetres, but its leg runs on through the
    /// front, so it is not clear of everything, and the guard was charged as a whole wall. Paid as the
    /// diffraction over the guard times what the front lets through, the guard costs a few decibels, and
    /// the answer is never more than the front alone lets through.
    /// </summary>
    [Fact]
    public void A_low_wall_before_a_heavier_one_costs_its_diffraction_not_its_transmission()
    {
        var guard = (new Vector3(0, 0.535f, -2), new Vector3(6, 1.07f, 0.1f), "Concrete");
        var routes = Routes(ClosedRoom(guard));
        var frontOnly = Routes(ClosedRoom());
        var ear = new Vector3(0, 1.0f, 0);
        var none = Array.Empty<int>();
        for (float x = -2f; x <= 2f; x += 0.5f)
        {
            var car = new Vector3(x, 0.5f, -30);
            OpeningRoutes.OnlyClearWaysRoundForLab = true;
            Vector3 before;
            try { before = Db3(routes.LegGains(car, ear, none, none)); }
            finally { OpeningRoutes.OnlyClearWaysRoundForLab = false; }
            var after = Db3(routes.LegGains(car, ear, none, none));
            var front = Db3(frontOnly.LegGains(car, ear, none, none));
            _o.WriteLine($"x {x:F1}: guard as a wall {before}, over the guard and through the front {after}, the front alone {front}");
            Assert.True(after.Y > before.Y + 10f, $"x {x:F1}: the guard still cost its transmission ({after.Y:F1} against {before.Y:F1} dB)");
            for (int b = 0; b < 3; b++)
            {
                float a = b == 0 ? after.X : b == 1 ? after.Y : after.Z, f = b == 0 ? front.X : b == 1 ? front.Y : front.Z;
                Assert.True(a <= f + 0.01f, $"x {x:F1}, band {b}: louder ({a:F1} dB) than the front alone lets through ({f:F1})");
                // Maekawa for a few centimetres: 5 to 20 dB, the top band the most.
                Assert.True(a >= f - 20f, $"x {x:F1}, band {b}: {a:F1} dB, the front alone {f:F1}");
            }
        }
    }

    /// <summary>A guard in two pieces, one lower than the other where the line crosses them both (a stringer
    /// under a balustrade): the way over the taller clears the lower one, and the answer is the one-piece
    /// guard's within a decibel.</summary>
    [Fact]
    public void A_guard_in_two_pieces_is_gone_over_as_one()
    {
        var guard = (new Vector3(0, 0.535f, -2), new Vector3(6, 1.07f, 0.1f), "Concrete");
        var stringer = (new Vector3(0, 0.45f, -2.02f), new Vector3(1.2f, 0.9f, 0.1f), "Concrete");
        var one = Routes(ClosedRoom(guard));
        var two = Routes(ClosedRoom(guard, stringer));
        var car = new Vector3(0.3f, 0.5f, -30);
        var ear = new Vector3(0, 1.0f, 0);
        var none = Array.Empty<int>();
        var a = Db3(one.LegGains(car, ear, none, none));
        var b = Db3(two.LegGains(car, ear, none, none));
        _o.WriteLine($"one piece {a}, two pieces {b}");
        Assert.InRange(b.X, a.X - 1f, a.X + 0.01f);
        Assert.InRange(b.Y, a.Y - 1f, a.Y + 0.01f);
        Assert.InRange(b.Z, a.Z - 1f, a.Z + 0.01f);
    }

    /// <summary>Ways round one thing and through another only ever add a path: nothing is quieter than it was
    /// with the ways round clear of everything alone, whatever stands where.</summary>
    [Fact]
    public void A_way_round_one_thing_never_takes_anything_away()
    {
        var routes = Routes(ClosedRoom(
            (new Vector3(0, 0.535f, -2), new Vector3(6, 1.07f, 0.1f), "Concrete"),
            (new Vector3(2, 1.0f, 2), new Vector3(0.1f, 2.0f, 3f), "Plasterboard"),
            (new Vector3(0, 0.9f, -12), new Vector3(8, 1.8f, 0.05f), "Wood")));
        var none = Array.Empty<int>();
        var rng = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            var ear = new Vector3((float)rng.NextDouble() * 8 - 4, 0.5f + (float)rng.NextDouble() * 1.8f, (float)rng.NextDouble() * 8 - 4);
            var src = new Vector3((float)rng.NextDouble() * 60 - 30, 0.3f + (float)rng.NextDouble() * 2f, -8 - (float)rng.NextDouble() * 30);
            OpeningRoutes.OnlyClearWaysRoundForLab = true;
            Vector3 before;
            try { before = routes.LegGains(src, ear, none, none); }
            finally { OpeningRoutes.OnlyClearWaysRoundForLab = false; }
            var after = routes.LegGains(src, ear, none, none);
            Assert.True(after.X >= before.X && after.Y >= before.Y && after.Z >= before.Z, $"{src} -> {ear}: {Db3(before)} became {Db3(after)}");
            Assert.True(after.X <= 1f && after.Y <= 1f && after.Z <= 1f);
        }
    }

    /// <summary>From inside a room there is no string over the top: the way out is through its openings or walls.</summary>
    [Fact]
    public void No_way_over_the_top_from_under_a_roof()
    {
        var boxes = new List<(Vector3, Vector3, string)>
        {
            (new Vector3(0, -0.1f, 0), new Vector3(400, 0.2f, 400), "Concrete"),
            // A closed room round the listener: four walls and a roof.
            (new Vector3(0, 1.375f, -5), new Vector3(10, 2.75f, 0.35f), "Brick"),
            (new Vector3(0, 1.375f, 5), new Vector3(10, 2.75f, 0.35f), "Brick"),
            (new Vector3(-5, 1.375f, 0), new Vector3(0.35f, 2.75f, 10), "Brick"),
            (new Vector3(5, 1.375f, 0), new Vector3(0.35f, 2.75f, 10), "Brick"),
            (new Vector3(0, 2.875f, 0), new Vector3(10.4f, 0.25f, 10.4f), "Concrete"),
        };
        var routes = Routes(boxes);
        float delta = routes.BarrierPathDifference(new Vector3(0, 0.45f, -60), new Vector3(0, 1.6f, 0), out _, out bool verified);
        _o.WriteLine($"verified {verified}, {delta:F2} m");
        Assert.False(verified);
    }

    // ═══ When an edge is buried (docs/GEOMETRY.md 12.6, "When an edge is buried") ═══════════════════════

    private static OpeningRoutes Boxes(IEnumerable<(Vector3 Centre, Vector3 Size, string Material)> boxes,
                                       IReadOnlyList<SolidSpec>? terrains = null)
    {
        AcousticRegistry.EnsureInitialized();
        var solids = boxes.Select(b => new OpeningRoutes.Solid(b.Centre, b.Size, Quaternion.Identity, b.Material)).ToList();
        return OpeningRoutes.Build(solids, null, Array.Empty<OpeningRoutes.Declared>(), null, terrains);
    }

    private static Vector3 Old(Func<Vector3> leg)
    {
        OpeningRoutes.OldBurialForLab = true;
        try { return leg(); }
        finally { OpeningRoutes.OldBurialForLab = false; }
    }

    /// <summary>
    /// A timber room on the ground, a patio slab before it with a 2 cm deck laid on it to its outline, and a
    /// source down the slope beyond (1303 Tamarack Court Southwest from the street below): the deck is a layer
    /// of the slab's construction, so the way round the patio's edge goes over its top, as it would with no
    /// deck, and its legs pay the room's wall. Before, the deck buried the slab's top edges and the slab the
    /// deck's, and the way went under the foot of the room's wall, through the earth, for nothing.
    /// </summary>
    [Fact]
    public void A_finish_cut_to_its_slab_leaves_the_slab_s_edge_to_go_round()
    {
        var room = new List<(Vector3, Vector3, string)>
        {
            (new Vector3(0, 1.375f, 4.125f), new Vector3(6.5f, 2.75f, 0.25f), "Wood"),
            (new Vector3(0, 1.375f, 10.125f), new Vector3(6.5f, 2.75f, 0.25f), "Wood"),
            (new Vector3(-3.125f, 1.375f, 7.125f), new Vector3(0.25f, 2.75f, 6.25f), "Wood"),
            (new Vector3(3.125f, 1.375f, 7.125f), new Vector3(0.25f, 2.75f, 6.25f), "Wood"),
            (new Vector3(0, 2.875f, 7.125f), new Vector3(6.5f, 0.25f, 6.5f), "Wood"),
        };
        var slab = (new Vector3(0, 0.125f, 1.5f), new Vector3(6, 0.25f, 5), "Concrete");
        var deck = (new Vector3(0, 0.26f, 1.5f), new Vector3(6, 0.02f, 5), "Wood");
        var ground = new[] { Hillside() };
        var bare = Boxes(room.Append(slab), ground);
        var decked = Boxes(room.Append(slab).Append(deck), ground);
        var none = Array.Empty<int>();
        var ear = new Vector3(0, 1.6f, 6);
        for (float x = -1f; x <= 1f; x += 1f)
        {
            // 0.5 m over the slope: the line goes down through the wall, the deck and the slab, then the earth.
            var src = new Vector3(x, -2.0f, -6);
            var a = Db3(bare.LegGains(src, ear, none, none));
            var b = Db3(decked.LegGains(src, ear, none, none));
            var before = Db3(Old(() => decked.LegGains(src, ear, none, none)));
            decked.BarrierGains(src, ear, WallTransmission.SoundSpeed, out _, out _, out var edge, out _);
            var edgeBefore = Vector3.Zero;
            Old(() => { decked.BarrierGains(src, ear, WallTransmission.SoundSpeed, out _, out _, out edgeBefore, out _); return Vector3.Zero; });
            _o.WriteLine($"x {x:F0}: slab alone {a}, with its deck {b} round {edge}; the deck burying the edge {before} round {edgeBefore}");
            for (int k = 0; k < 3; k++)
            {
                float ak = k == 0 ? a.X : k == 1 ? a.Y : a.Z, bk = k == 0 ? b.X : k == 1 ? b.Y : b.Z;
                Assert.InRange(bk, ak - 1f, ak + 0.01f);
            }
            Assert.True(edge.Y > 0.2f, $"x {x:F0}: the way round goes at {edge.Y:F2} m, under the patio");
            Assert.True(edgeBefore.Y < 0.05f, $"x {x:F0}: before, the way went at {edgeBefore.Y:F2} m");
        }
    }

    /// <summary>
    /// A floor slab built into a brick front, its edge flush with the outer face and the next storey's brick on
    /// it, in a closed building (Marlow Tower's floor 1 stairwell over Dock Street): a line from the street that
    /// goes into the slab by its edge pays half a metre of concrete, 10 dB under the brick a metre higher, where
    /// the brick a few centimetres over the edge radiates into the room over its whole face. The way round the
    /// slab's edge, through the brick its edge is buried in, the brick paid once, leaves Maekawa's grazing loss
    /// of the dip (5 dB), never louder than the brick alone lets through.
    /// </summary>
    [Fact]
    public void A_slab_built_into_a_front_is_heard_through_the_front_beside_its_edge()
    {
        var above = (new Vector3(0, 4.65f, 0.175f), new Vector3(20, 3.0f, 0.35f), "Brick");
        var routes = Boxes(new[]
        {
            (new Vector3(0, -0.125f, 0), new Vector3(200, 0.25f, 200), "Concrete"),
            (new Vector3(0, 1.5f, 0.175f), new Vector3(20, 3.0f, 0.35f), "Brick"),
            (new Vector3(0, 3.075f, 5), new Vector3(20, 0.15f, 10), "Concrete"),
            above,
            (new Vector3(-10.175f, 3.075f, 5), new Vector3(0.35f, 6.15f, 10.7f), "Brick"),
            (new Vector3(10.175f, 3.075f, 5), new Vector3(0.35f, 6.15f, 10.7f), "Brick"),
            (new Vector3(0, 3.075f, 10.175f), new Vector3(20, 6.15f, 0.35f), "Brick"),
            (new Vector3(0, 6.275f, 5), new Vector3(21, 0.25f, 11), "Concrete"),
        });
        var none = Array.Empty<int>();
        var ear = new Vector3(0, 3.6f, 4);
        var (bl, bm, bh) = WallTransmission.BandGains("Brick", above.Item2, default);
        var brick = Db3(new Vector3(bl, bm, bh));
        for (float x = -4f; x <= 4f; x += 2f)
        {
            // The line meets the front at 3.08 m, in the slab's 15 cm edge, and comes out of its top 0.5 m in.
            var car = new Vector3(x, 0.5f, -20);
            var before = Db3(Old(() => routes.LegGains(car, ear, none, none)));
            var after = Db3(routes.LegGains(car, ear, none, none));
            // A metre higher, the line crosses the brick over the slab.
            var beside = Db3(routes.LegGains(car + new Vector3(0, 1f, 0), ear, none, none));
            _o.WriteLine($"x {x:F0}: into the slab's edge {before}, now {after}; through the brick beside it {beside}; the brick alone {brick}");
            Assert.True(after.X > before.X + 4f, $"x {x:F0}: the dip is still there ({after.X:F1} against {before.X:F1} dB)");
            for (int k = 0; k < 3; k++)
            {
                float a = k == 0 ? after.X : k == 1 ? after.Y : after.Z, w = k == 0 ? brick.X : k == 1 ? brick.Y : brick.Z;
                float s = k == 0 ? beside.X : k == 1 ? beside.Y : beside.Z;
                Assert.True(a <= w + 0.01f, $"x {x:F0}, band {k}: louder ({a:F1} dB) than the brick alone lets through ({w:F1})");
                Assert.True(a >= s - 7f, $"x {x:F0}, band {k}: {a:F1} dB, more than Maekawa's grazing loss under the brick beside it ({s:F1})");
            }
        }
    }

    /// <summary>
    /// The storey below's brick front bears the slab, whose edge is flush with it (Kestrel House flat 50B from
    /// the street): round the top of that brick is into the slab, and the slab's underside there lies on the
    /// brick, so the only face the sound can go in by is the slab's 15 cm edge. That is the slab gone into by
    /// its edge, and it pays the run: the way is no louder than the line through the brick and the floor. Paid
    /// as the slab crossed face to face, it came out 40 dB louder. The storey above's front is a metre of
    /// concrete here, so no other way through a front competes.
    /// </summary>
    [Fact]
    public void Round_a_wall_s_top_into_the_slab_it_bears_is_into_the_slab_s_edge()
    {
        var routes = Boxes(new[]
        {
            (new Vector3(0, -0.125f, 0), new Vector3(200, 0.25f, 200), "Concrete"),
            (new Vector3(0, 1.5f, 0.175f), new Vector3(20, 3.0f, 0.35f), "Brick"),
            (new Vector3(0, 3.075f, 5), new Vector3(20, 0.15f, 10), "Concrete"),
            (new Vector3(0, 4.65f, 1.0f), new Vector3(20, 3.0f, 2.0f), "Concrete"),
            (new Vector3(-10.175f, 3.075f, 5), new Vector3(0.35f, 6.15f, 10.7f), "Brick"),
            (new Vector3(10.175f, 3.075f, 5), new Vector3(0.35f, 6.15f, 10.7f), "Brick"),
            (new Vector3(0, 3.075f, 10.175f), new Vector3(20, 6.15f, 0.35f), "Brick"),
            (new Vector3(0, 6.275f, 5), new Vector3(21, 0.25f, 11), "Concrete"),
        });
        var none = Array.Empty<int>();
        var ear = new Vector3(0, 3.4f, 8);
        for (float x = -4f; x <= 4f; x += 2f)
        {
            // The line goes through the brick below the slab, at 2.1 m, and up through the slab 5.5 m in.
            var car = new Vector3(x, 0.5f, -10);
            // Under -100 dB is nothing the mixer plays.
            var before = Vector3.Max(Db3(Old(() => routes.LegGains(car, ear, none, none))), new Vector3(-100f));
            var after = Vector3.Max(Db3(routes.LegGains(car, ear, none, none)), new Vector3(-100f));
            _o.WriteLine($"x {x:F0}: before {before}, now {after}");
            Assert.True(after.X <= before.X + 1f && after.Y <= before.Y + 1f && after.Z <= before.Z + 1f,
                        $"x {x:F0}: {after}, louder than the line through the brick and the floor ({before})");
        }
    }

    /// <summary>The ground on a slope, from 0 m at z = -1 down to -3 m at z = -7, flat each side, as a tile of terrain.</summary>
    private static SolidSpec Hillside()
    {
        const int posts = 126;
        const float spacing = 2f;
        var heights = new float[posts * posts];
        for (int j = 0; j < posts; j++)
            for (int i = 0; i < posts; i++)
            {
                float z = -125f + j * spacing;
                heights[j * posts + i] = z >= -1f ? 0f : z <= -7f ? -3f : (z + 1f) * 0.5f;
            }
        return SolidSpec.OfTerrain(1, Vector3.Zero, new Heightfield(posts, spacing, heights, null, null),
                                   OpenFPS.Client.Core.AudioEngine.SteamAudio.SceneTerrain.Surface);
    }

    /// <summary>A wall standing on the ground, the ear behind it and a source down the slope in front: the wall's
    /// foot is in the earth, which is no box, so there is no way under it. Before, the way round its bottom
    /// edge went through the hillside for nothing. The same from the box list and from the triangle store.</summary>
    [Fact]
    public void A_wall_on_the_ground_is_not_gone_under()
    {
        var wall = (new Vector3(0, 1.25f, 0), new Vector3(40, 2.5f, 0.35f), "Brick");
        var ground = new[] { Hillside() };
        var routes = Boxes(new[] { wall }, ground);
        var builder = new TriangleWorldBuilder(250f);
        var spec = new SolidSpec(2, wall.Item1, Quaternion.Identity, wall.Item2, EntityGeometry.SurfaceOf("Brick", wall.Item2, 0, 0, false, 0, 0, false, false, false));
        var tiled = OpeningRoutes.Build(builder.Build(new[] { spec, ground[0] }, Array.Empty<SolidSpec>()), null,
                                        Array.Empty<OpeningRoutes.Declared>(), null, new OpeningRoutes.TileCache());
        var none = Array.Empty<int>();
        var ear = new Vector3(0, 1.6f, 3);
        var (wl, wm, wh) = WallTransmission.BandGains("Brick", wall.Item2, default);
        for (float z = -6f; z <= -4f; z += 1f)
        {
            // 0.5 m over the slope: the line meets the wall at its foot.
            var src = new Vector3(0.5f, (z + 1f) * 0.5f + 0.5f, z);
            var before = Db3(Old(() => routes.LegGains(src, ear, none, none)));
            var after = Db3(routes.LegGains(src, ear, none, none));
            var fromTiles = Db3(tiled.LegGains(src, ear, none, none));
            float over = routes.BarrierPathDifference(src, ear, out _, out bool verified);
            _o.WriteLine($"z {z:F0}: under the wall's foot {before}, now {after} (from the tiles {fromTiles}); the way round {over:F2} m, verified {verified}");
            Assert.True(after.Y < before.Y - 10f, $"z {z:F0}: still under the wall ({after.Y:F1} against {before.Y:F1} dB)");
            Assert.True(over > 0.5f, $"z {z:F0}: the way round is {over:F2} m, under the foot");
            Assert.True(Vector3.Distance(after, fromTiles) < 0.1f, $"z {z:F0}: the box list {after}, the triangle store {fromTiles}");
            Assert.True(after.Y >= Db(wm) - 0.01f, $"z {z:F0}: quieter ({after.Y:F1} dB) than the wall lets through ({Db(wm):F1})");
        }
    }

    /// <summary>The rules only ever take away a way through the earth: on a scene with no terrain and no
    /// layers in contact, the barrier model is what it was, or louder by a way round an edge in another box.</summary>
    [Fact]
    public void Without_ground_or_finishes_the_new_rules_only_add()
    {
        var routes = Routes(ClosedRoom(
            (new Vector3(0, 0.535f, -2), new Vector3(6, 1.07f, 0.1f), "Concrete"),
            (new Vector3(2, 1.0f, 2), new Vector3(0.1f, 2.0f, 3f), "Plasterboard"),
            (new Vector3(0, 0.9f, -12), new Vector3(8, 1.8f, 0.05f), "Wood")));
        var none = Array.Empty<int>();
        var rng = new Random(11);
        for (int i = 0; i < 200; i++)
        {
            var ear = new Vector3((float)rng.NextDouble() * 8 - 4, 0.5f + (float)rng.NextDouble() * 1.8f, (float)rng.NextDouble() * 8 - 4);
            var src = new Vector3((float)rng.NextDouble() * 60 - 30, 0.3f + (float)rng.NextDouble() * 2f, -8 - (float)rng.NextDouble() * 30);
            var before = Old(() => routes.LegGains(src, ear, none, none));
            var after = routes.LegGains(src, ear, none, none);
            Assert.True(after.X >= before.X && after.Y >= before.Y && after.Z >= before.Z, $"{src} -> {ear}: {Db3(before)} became {Db3(after)}");
        }
    }
}
