using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Geometry;

namespace OpenFPS.Tests;

/// <summary>
/// Physics stage 3 (docs/PHYSICS_BOUNDARY.md 17): the state the physics leaves and the wire carries, a body's
/// grounding sent rather than guessed, and the contacts of a tick as a stream.
/// </summary>
public class PhysicsStateTests
{
    private static EntityState Packed(EntityState s)
    {
        var back = new List<EntityState>();
        StatePacking.Unpack(StatePacking.Pack(new List<EntityState> { s }, 0, 1), back);
        return Assert.Single(back);
    }

    /// <summary>A body's grounding goes on the wire in the flags it already sends, so it costs no byte.</summary>
    [Theory]
    [InlineData(Grounding.Unknown)]
    [InlineData(Grounding.Grounded)]
    [InlineData(Grounding.Airborne)]
    public void GroundingGoesOnTheWireForNothing(Grounding grounding)
    {
        var walker = new EntityState
        {
            EntityId = 300,
            Transform = QuantizedTransform.FromTransform(new Transform { Position = new Vector3(3f, 1f, 4f), Rotation = Quaternion.Identity }),
            LinearVelocity = new Vector3(1.4f, -2f, 0f),
        };
        int bare = StatePacking.Pack(new List<EntityState> { walker }, 0, 1).Length;
        walker.Grounding = grounding;
        Assert.Equal(bare, StatePacking.Pack(new List<EntityState> { walker }, 0, 1).Length);
        var back = Packed(walker);
        Assert.Equal(grounding, back.Grounding);
        Assert.Equal(walker.LinearVelocity, back.LinearVelocity);
    }

    private const int Listener = 99, Walker = 2;

    /// <summary>A floor, and a player walking on it at <paramref name="at"/> with the grounding the server sent.</summary>
    private static WorldSnapshot Frame(Vector3 at, Vector3 velocity, Grounding grounding)
    {
        var world = new WorldSnapshot();
        var floor = new EntityDefinition
        {
            EntityId = 1,
            Type = EntityType.StaticObject,
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(200f, 0.5f, 200f), IsSolid = true },
            Material = new MaterialComponent { Material = "Concrete", Variant = "0" },
        };
        world.Entities[1] = new EntitySnapshot
        {
            Id = 1, Definition = floor,
            Transform = new Transform { Position = new Vector3(0, -0.25f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        var body = new EntitySnapshot
        {
            Id = Walker,
            Definition = new EntityDefinition
            {
                EntityId = Walker,
                Type = EntityType.Player,
                Collider = new ColliderComponent { Shape = ColliderShape.Cylinder, Size = new Vector3(0.6f, 1.8f, 0.6f), IsSolid = true },
                Material = new MaterialComponent { Material = "Generic", Variant = "0" },
            },
            Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
            Velocity = velocity,
            Grounding = grounding,
        };
        world.Entities[Walker] = body;
        world.DynamicEntities.Add(body);
        return world;
    }

    /// <summary>
    /// A body moving at a walk with no vertical speed: standing, or at the top of a long jump. Until stage 3 a
    /// client read both as standing (the vertical speed under 0.15 m/s) and a jump's top stepped; now the
    /// server says which.
    /// </summary>
    [Theory]
    [InlineData(Grounding.Grounded, true)]
    [InlineData(Grounding.Airborne, false)]
    [InlineData(Grounding.Unknown, true)]
    public void ABodyStepsOnlyWhereItsStepSaysItStands(Grounding grounding, bool steps)
    {
        var others = new OtherBodies();
        int count = 0, landed = 0;
        others.OnStepTriggered += (_, _, _, _, _) => count++;
        others.OnLandTriggered += (_, _, _) => landed++;
        var at = new Vector3(0, 0, -4f);
        for (int i = 0; i < 20; i++)
        {
            at += new Vector3(0, 0, 0.4f);
            others.Update(Frame(at, new Vector3(0, 0, 4f), grounding), Listener);
            Thread.Sleep(50);   // the cadence floor is wall-clock
        }
        if (steps) Assert.True(count >= 3, $"eight metres of walking, {grounding}, made {count} footstep(s)");
        else Assert.True(count == 0 && landed == 0, $"a body in the air made {count} step(s) and {landed} landing(s)");
    }

    /// <summary>The landing is heard on the update the server says the body stands again.</summary>
    [Fact]
    public void ABodyLandsWhenItsStepSaysItStands()
    {
        var others = new OtherBodies();
        int landed = 0;
        others.OnLandTriggered += (_, _, _) => landed++;
        var at = new Vector3(0, 0.5f, 0f);
        others.Update(Frame(at, Vector3.Zero, Grounding.Grounded), Listener);
        Thread.Sleep(50);
        for (int i = 0; i < 5; i++)
        {
            others.Update(Frame(at, new Vector3(0, i < 2 ? 3f : -4f, 0), Grounding.Airborne), Listener);
            Thread.Sleep(50);
        }
        Assert.Equal(0, landed);
        others.Update(Frame(at with { Y = 0f }, Vector3.Zero, Grounding.Grounded), Listener);
        Assert.Equal(1, landed);
    }

    private static ContactEvent Touch(int body, int other) =>
        new(body, other, Vector3.Zero, Vector3.UnitX, 0f, 1f, 0f, "Flesh", "Brick", ContactPhase.Began);

    /// <summary>A contact begins, continues while it is made again each tick, and ends the tick it is not.</summary>
    [Fact]
    public void AContactBeginsContinuesAndEnds()
    {
        var s = new ContactStream();
        s.Begin();
        Assert.Equal(ContactPhase.Began, s.Add(Touch(1, 7)).Phase);
        Assert.Equal(ContactPhase.Continued, s.Add(Touch(1, 7)).Phase);   // a second input in the same tick
        s.Close();
        s.Begin();
        Assert.Equal(ContactPhase.Continued, s.Add(Touch(1, 7) with { Phase = ContactPhase.Began }).Phase);
        Assert.Equal(ContactPhase.Began, s.Add(Touch(1, 8)).Phase);
        s.Close();
        s.Begin();
        s.Add(Touch(1, 8));
        s.Close();
        var ended = Assert.Single(s.Events, e => e.Phase == ContactPhase.Ended);
        Assert.Equal((1, 7), (ended.Body, ended.Other));
        Assert.Equal(0f, ended.ClosingSpeed);
        s.Begin();
        s.Close();
        Assert.Equal((1, 8), (Assert.Single(s.Events).Body, s.Events[0].Other));
    }

    /// <summary>A tick nobody closes does not grow without end.</summary>
    [Fact]
    public void AStreamNobodyReadsIsBounded()
    {
        var s = new ContactStream();
        for (int i = 0; i < ContactStream.MaxEvents + 10; i++) s.Add(Touch(i, 1));
        Assert.Equal(ContactStream.MaxEvents, s.Events.Count);
        Assert.Equal(10, s.Dropped);
    }

    /// <summary>The weather's air as a sample: horizontal, the field's own velocity at that height.</summary>
    [Fact]
    public void TheAirIsSampledFromTheWeather()
    {
        var weather = WindWeather.Steady(6f, 250f, 0.3f);
        var at = new Vector3(40f, 2f, -10f);
        var sample = AirSample.Of(weather, at, 10f, 1000.0);
        var field = WindField.VelocityAt(weather, at.X, 10f, at.Z, 1000.0);
        Assert.Equal(new Vector3(field.X, 0f, field.Y), sample.Velocity);
        Assert.Equal(0.3f, sample.Turbulence, 4);
        Assert.Equal(0f, sample.PressureDeviationPa);
    }
}
