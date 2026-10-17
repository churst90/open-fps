using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Geometry;
using OpenFPS.Sound;

namespace OpenFPS.Tests;

/// <summary>
/// Gravity is a property of the world (Cody, 2026-10-11; docs/PHYSICS_BOUNDARY.md 16.6): a map's
/// <c>Gravity</c>, standard unless it says otherwise, is what everything on it that falls, rolls or flies
/// reads. Every map is standard today, so these try the Moon's, 1.62 m/s².
/// </summary>
public class MapGravityTests
{
    private const float Moon = 1.62f;

    private static World MapWith(float gravity)
    {
        var world = World.Create();
        world.Create(new ZoneComponent { MapId = "moon", Gravity = gravity });
        return world;
    }

    [Fact]
    public void A_map_says_its_gravity_and_a_world_without_one_is_standard()
    {
        using var moon = MapWith(Moon);
        using var bare = World.Create();
        Assert.Equal(Moon, PhysicsConstants.GravityOf(moon));
        Assert.Equal(Earth.StandardGravity, PhysicsConstants.GravityOf(bare));
        Assert.Equal(Earth.StandardGravity, new ZoneComponent().Gravity);
    }

    [Fact]
    public void A_body_falls_softer_on_the_moon()
    {
        using var earth = MapWith(Earth.StandardGravity);
        using var moon = MapWith(Moon);
        var grid = new SpatialGrid<Entity>(10f);
        var here = CombatService.BodyFall(earth, grid, Vector3.Zero);
        var there = CombatService.BodyFall(moon, grid, Vector3.Zero);
        Assert.Equal(here.Count, there.Count);
        // It lands at sqrt(2 g h): sqrt(1.62 / 9.81) of the speed, so quieter.
        Assert.True(there.Max(s => s.LevelDb) < here.Max(s => s.LevelDb) - 3f,
            $"on the moon {there.Max(s => s.LevelDb):F1} dB, on the earth {here.Max(s => s.LevelDb):F1} dB");
    }

    [Fact]
    public void A_bullet_and_a_tumbling_slug_drop_by_the_gravity_they_fly_in()
    {
        float Drop(float g, bool tumbling)
        {
            var s = new BulletState { Velocity = new Vector3(0f, 0f, 400f) };
            if (tumbling) RicochetLaw.Advance(ref s, 0.5f, WeaponRegistry.Glock.Slug, Air.Standard, Vector3.Zero, g);
            else ExternalBallistics.Advance(ref s, 0.5f, 0.3f, Air.Standard, Vector3.Zero, g);
            return -s.Position.Y;
        }
        foreach (bool tumbling in new[] { false, true })
        {
            // Half a second: g t² / 2 is 1.23 m, less what the drag takes (a tumbling slug's is large).
            Assert.InRange(Drop(Earth.StandardGravity, tumbling), tumbling ? 0.6f : 1.15f, 1.23f);
            Assert.InRange(Drop(Moon, tumbling) / Drop(Earth.StandardGravity, tumbling), 0.15f, 0.19f);
        }
    }

    [Fact]
    public void A_car_takes_a_bend_and_presses_its_tyres_by_its_maps_gravity()
    {
        var bend = new List<Vector3>();
        for (int i = 0; i < 72; i++)
            bend.Add(new Vector3(40f * MathF.Sin(i * MathF.Tau / 72f), 0f, 40f * MathF.Cos(i * MathF.Tau / 72f)));
        var earth = new RaceLine(bend, 0f, 60f, 0.8f, 5f);
        var moon = new RaceLine(bend, 0f, 60f, 0.8f, 5f, gravity: Moon);
        // sqrt(mu g R): the speed goes as the root of the gravity.
        Assert.Equal(MathF.Sqrt(Moon / Earth.StandardGravity), moon.MinSpeed / earth.MinSpeed, 2);

        var mechanics = VehicleProfile.ByName("i4_midsize").Mechanics;
        var body = new WheelDynamics(mechanics) { Gravity = Moon };
        body.Loads(0f, 0f);
        float load = body.Wheels.Sum(w => w.Load);
        Assert.Equal(body.Mass * Moon, load, 0);
        Assert.InRange(TyreGrip.Demand(3f, 4f, 0.8f, Moon) / TyreGrip.Demand(3f, 4f, 0.8f), 6.0f, 6.1f);
    }
}
