using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Everybody else's feet.
///
/// The only body in the world that made any noise walking used to be your own: another player could
/// run past you, round you and into you in silence. These hold the derivation that fixed it to the
/// two things that make it trustworthy — that a body which is WALKING is heard, from where it
/// actually is and off the floor it is actually on, and that a body which is merely being MOVED is
/// not, because the difference between those two is most of what a listener is being asked to
/// believe.
/// </summary>
public class OtherBodiesTests
{
    private const int Listener = 99;
    private const int Walker = 2;

    /// <summary>One frame of a world: a floor, and one body standing on it.</summary>
    private static WorldSnapshot Frame(Vector3 bodyPosition, Vector3 bodyVelocity,
                                       string floorMaterial = "Concrete",
                                       EntityType type = EntityType.Player,
                                       bool includeBody = true)
    {
        var world = new WorldSnapshot();

        var floor = new EntityDefinition
        {
            EntityId = 1,
            Type = EntityType.StaticObject,
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(200f, 0.5f, 200f), IsSolid = true },
            Material = new MaterialComponent { Material = floorMaterial, Variant = "0" },
        };
        world.Entities[1] = new EntitySnapshot
        {
            Id = 1,
            Definition = floor,
            Transform = new Transform { Position = new Vector3(0, -0.25f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
        };

        if (includeBody)
        {
            var body = new EntityDefinition
            {
                EntityId = Walker,
                Type = type,
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.6f, 1.8f, 0.6f), IsSolid = true },
                Material = new MaterialComponent { Material = "Generic", Variant = "0" },
            };
            var snap = new EntitySnapshot
            {
                Id = Walker,
                Definition = body,
                Transform = new Transform { Position = bodyPosition, Rotation = Quaternion.Identity, Scale = Vector3.One },
                Velocity = bodyVelocity,
            };
            world.Entities[Walker] = snap;
            world.DynamicEntities.Add(snap);
        }

        return world;
    }

    /// <summary>Walks a body forward and returns every footstep it made.</summary>
    private static List<(Vector3 Position, string Material)> Walk(
        OtherBodies others, int listenerId, Vector3 from, float metresPerUpdate, int updates,
        string floorMaterial = "Concrete", EntityType type = EntityType.Player, bool underItsOwnPower = true)
    {
        var steps = new List<(Vector3, string)>();
        others.OnStepTriggered += (p, m, _) => steps.Add((p, m));

        // A body that is walking has the velocity to show for it; one that is being moved does not.
        var velocity = underItsOwnPower ? new Vector3(0, 0, metresPerUpdate * 10f) : Vector3.Zero;

        var at = from;
        for (int i = 0; i < updates; i++)
        {
            at += new Vector3(0, 0, metresPerUpdate);
            others.Update(Frame(at, velocity, floorMaterial, type), listenerId);
            // Real time, because the cadence floor is real: no body puts a foot down five times a
            // second, however fast a test loop can call this.
            Thread.Sleep(50);
        }
        return steps;
    }

    [Fact]
    public void AnotherPlayerWalkingPastYouIsHeard()
    {
        var others = new OtherBodies();
        var steps = Walk(others, Listener, new Vector3(0, 0, -4f), metresPerUpdate: 0.4f, updates: 20);

        Assert.True(steps.Count >= 3, $"eight metres of walking produced {steps.Count} footstep(s)");
    }

    /// <summary>A step is heard from the body's feet — and from one foot or the other, not from a
    /// point down the middle of it.</summary>
    [Fact]
    public void TheStepIsWhereTheBodyIsAndNotWhereTheListenerIs()
    {
        var others = new OtherBodies();
        var steps = Walk(others, Listener, new Vector3(0, 0, -4f), metresPerUpdate: 0.4f, updates: 20);

        Assert.NotEmpty(steps);
        foreach (var (position, _) in steps)
        {
            Assert.InRange(position.Z, -4f, 4.1f);
            Assert.Equal(StrideAccumulator.StepWidth, MathF.Abs(position.X), 3);
        }
        // Feet alternate, so a walker is two sources rather than one.
        if (steps.Count >= 2) Assert.NotEqual(steps[0].Position.X, steps[1].Position.X);
    }

    /// <summary>The floor a body is standing on decides what it sounds like, and nobody had to send
    /// that — the listener's own copy of the world already knows what is under everyone's feet.</summary>
    [Fact]
    public void ABodyOnGrassSoundsLikeGrass()
    {
        var others = new OtherBodies();
        var steps = Walk(others, Listener, new Vector3(0, 0, -4f), metresPerUpdate: 0.4f, updates: 20,
                         floorMaterial: "Grass");

        Assert.NotEmpty(steps);
        Assert.All(steps, s => Assert.Equal("Grass", s.Material));
    }

    /// <summary>
    /// Being MOVED is not walking, for somebody else's body exactly as for your own.
    ///
    /// This is the rule that makes a passenger silent without anything here knowing what a vehicle is.
    /// The server zeroes an occupant's velocity and leaves their body to the seat, so a rider is a
    /// body at rest whose position is changing — which is the same shape as a teleport, a spawn and a
    /// reconciliation, and is refused for the same reason. Without it a car at sixty miles an hour
    /// would be a footstep every half metre of road.
    /// </summary>
    [Fact]
    public void ABodyThatIsBeingCarriedDoesNotWalk()
    {
        var others = new OtherBodies();
        var steps = Walk(others, Listener, new Vector3(0, 0, -4f), metresPerUpdate: 0.4f, updates: 20,
                         underItsOwnPower: false);

        Assert.Empty(steps);
    }

    /// <summary>Your own feet are already heard, from the position this client predicts rather than
    /// the one that had to travel. Hearing them a second time from the server's copy would be a
    /// phantom walking a tenth of a second behind you.</summary>
    [Fact]
    public void YourOwnBodyIsNotHeardTwice()
    {
        var others = new OtherBodies();
        var steps = Walk(others, listenerId: Walker, from: new Vector3(0, 0, -4f), metresPerUpdate: 0.4f, updates: 20);

        Assert.Empty(steps);
    }

    /// <summary>A thing is not a body. A crate sliding across a floor at walking pace makes whatever
    /// noise a crate makes; it does not make footsteps.</summary>
    [Fact]
    public void SomethingThatIsNotABodyDoesNotWalk()
    {
        var others = new OtherBodies();
        var steps = Walk(others, Listener, new Vector3(0, 0, -4f), metresPerUpdate: 0.4f, updates: 20,
                         type: EntityType.Item);

        Assert.Empty(steps);
    }

    /// <summary>A body off the ground banks no distance, and arrives with a landing rather than a
    /// step. Vertical velocity is the signal, because the server clamps a grounded body's to zero.</summary>
    [Fact]
    public void ABodyInTheAirLandsRatherThanStepping()
    {
        var others = new OtherBodies();
        int landings = 0, steps = 0;
        others.OnLandTriggered += (_, _, _) => landings++;
        others.OnStepTriggered += (_, _, _) => steps++;

        var at = new Vector3(0, 2f, 0);

        // Falling: moving, and plainly not on anything.
        for (int i = 0; i < 6; i++)
        {
            at -= new Vector3(0, 0.3f, 0);
            others.Update(Frame(at, new Vector3(0, -5f, 0)), Listener);
        }
        Assert.Equal(0, steps);
        Assert.Equal(0, landings);

        // ...and arriving.
        at.Y = 0f;
        others.Update(Frame(at, Vector3.Zero), Listener);

        Assert.Equal(1, landings);
        Assert.Equal(0, steps);
    }

    /// <summary>Somebody who walks out of earshot takes their half-finished stride with them. They
    /// may be back, and when they are they will be somewhere else entirely — the distance between
    /// here and there is not something they walked.</summary>
    [Fact]
    public void SomebodyWhoLeavesIsForgotten()
    {
        var others = new OtherBodies();

        others.Update(Frame(new Vector3(0, 0, 0), new Vector3(0, 0, 4f)), Listener);
        Assert.Equal(1, others.Count);

        others.Update(Frame(Vector3.Zero, Vector3.Zero, includeBody: false), Listener);
        Assert.Equal(0, others.Count);
    }
}
