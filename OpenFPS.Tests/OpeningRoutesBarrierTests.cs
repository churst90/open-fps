using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The barrier search (OpeningRoutes.BarrierPathDifference) as a car drives past: the answer may only
/// change as fast as the geometry along the way does.
///
/// "Now and then I hear sounds like a siren pop through for an instant" (Cody, 2026-10-02). From the
/// Main Street pavement the game's [POP] lines were cars 150-300 m off whose mid band went from -80 dB
/// to -20 and back in a third of a second. --pop-hunt found it: the route over the thing in the way was
/// believed only when no other box touched it, so it came and went with whatever the route happened
/// to brush past — a pier on a park wall, the next storey of a building — and while it was gone the
/// answer was what comes THROUGH every wall on the line.
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
        world.AcousticMap = AcousticVolumeGenerator.GenerateRegions(defs, new Vector3(600, 60, 600), new Vector3(-300, -5, -300));
        return new SpatialAcoustics().RoutesFor(world)!;
    }

    /// <summary>
    /// A park's 1.1 m wall with a 0.5 m pier every 7.5 m along its top, as the city's are; the listener
    /// 70 m in front of it, a car driving past 80 m behind, the line between them grazing the wall top.
    /// Over the wall is a few millimetres out of the way wherever the car is. Before, the route was
    /// thrown out whenever its leg to the ear crossed the wall at a pier, and the car went from -7 dB to
    /// "not verified" (the worker then charged it the wall's full transmission, -61) and back.
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
        // Within 3 dB: where the line meets a pier the string goes over the pier (a few centimetres, 8 dB)
        // rather than round its side (grazing, 5), which the search cannot find against the wall it stands on.
        Assert.True(worstStep < 3.5f, $"the mid band stepped {worstStep:F2} dB between positions 10 cm apart");
    }

    /// <summary>
    /// A six-storey building built as the city builds them — a wall box per side per storey, a slab
    /// over each — between a listener in the street and a car driving past behind it. No route round one
    /// box is clear of the rest (over a wall's top is into the slab above, round its end is into the
    /// next wall), and before, that left only what comes through the building: the car at -80 dB until
    /// some position let a route through, then back. Over the roof is always there; it is the string
    /// pulled tight over every storey in the vertical plane through the two (ISO 9613-2, CNOSSOS-EU).
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

    /// <summary>
    /// The same string over the top does not exist when an end has a roof over it: from inside a room,
    /// the way out is through its openings or its walls, never up through the ceiling.
    /// </summary>
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
}
