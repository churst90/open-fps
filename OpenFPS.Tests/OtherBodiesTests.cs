using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Geometry;

namespace OpenFPS.Tests;

/// <summary>
/// Everybody else's feet: a body that is walking is heard, from where it is and off the floor it is on,
/// and a body that is only being moved is not.
/// </summary>
public class OtherBodiesTests
{
    private const int Listener = 99;
    private const int Walker = 2;

    /// <summary>One frame of a world: a floor, and one body standing on it.</summary>
    private static WorldSnapshot Frame(Vector3 bodyPosition, Vector3 bodyVelocity,
                                       string floorMaterial = "Concrete",
                                       EntityType type = EntityType.Player,
                                       bool includeBody = true,
                                       string soundId = "")
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
                SoundEmitter = new SoundEmitterComponent { SoundId = soundId },
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
        string floorMaterial = "Concrete", EntityType type = EntityType.Player, bool underItsOwnPower = true,
        string soundId = "")
    {
        var steps = new List<(Vector3, string)>();
        others.OnStepTriggered += (p, m, _, _, _) => steps.Add((p, m));

        // A body that is walking has the velocity to show for it; one that is being moved does not.
        var velocity = underItsOwnPower ? new Vector3(0, 0, metresPerUpdate * 10f) : Vector3.Zero;

        var at = from;
        for (int i = 0; i < updates; i++)
        {
            at += new Vector3(0, 0, metresPerUpdate);
            others.Update(Frame(at, velocity, floorMaterial, type, includeBody: true, soundId: soundId), listenerId);
            // Real time: the cadence floor is wall-clock, so a fast loop would be refused steps.
            Thread.Sleep(50);
        }
        return steps;
    }

    /// <summary>
    /// A car does not walk, though <c>VehicleSystem</c> spawns it as an <see cref="EntityType.NPC"/>
    /// ("the car driving by sounds like footsteps are being drug behind it", 2026-09-18). The first fix
    /// matched "ENGINE/", the client's resolved spelling, where a snapshot carries "engine:"; hence the
    /// real prefix here.
    /// </summary>
    [Theory]
    [InlineData("engine:v8_muscle")]
    [InlineData("ENGINE:v8_muscle")]
    public void AThingWithAnEngineHasNoLegs(string soundId)
    {
        var others = new OtherBodies();
        var steps = Walk(others, Listener, new Vector3(0, 0, -4f), metresPerUpdate: 0.4f, updates: 20,
                         type: EntityType.NPC, soundId: soundId);

        Assert.Empty(steps);
    }

    /// <summary>An NPC that is not a machine still walks.</summary>
    [Fact]
    public void AnNpcOnFootIsStillHeard()
    {
        var others = new OtherBodies();
        var steps = Walk(others, Listener, new Vector3(0, 0, -4f), metresPerUpdate: 0.4f, updates: 20,
                         type: EntityType.NPC);

        Assert.True(steps.Count >= 3, $"eight metres of walking produced {steps.Count} footstep(s)");
    }

    [Fact]
    public void AnotherPlayerWalkingPastYouIsHeard()
    {
        var others = new OtherBodies();
        var steps = Walk(others, Listener, new Vector3(0, 0, -4f), metresPerUpdate: 0.4f, updates: 20);

        Assert.True(steps.Count >= 3, $"eight metres of walking produced {steps.Count} footstep(s)");
    }

    /// <summary>
    /// Somebody first heard already walking is part way through a step, not starting one: at a spawn on
    /// the city every walker in sight put a foot down on the first frame, 83 in one audio update, and
    /// walked in step after it. Their first step comes as the walk brings it, at a phase from the id.
    /// </summary>
    [Fact]
    public void SomebodyFirstHeardWalkingIsMidStride()
    {
        var others = new OtherBodies();
        var steps = new List<Vector3>();
        others.OnStepTriggered += (p, _, _, _, _) => steps.Add(p);
        var velocity = new Vector3(0, 0, 1.4f);

        others.Update(Frame(new Vector3(0, 0, -4f), velocity), Listener);
        Assert.Empty(steps);

        var at = new Vector3(0, 0, -4f);
        for (int i = 0; i < 20 && steps.Count == 0; i++)
        {
            at += new Vector3(0, 0, 0.14f);
            others.Update(Frame(at, velocity), Listener);
        }
        Assert.NotEmpty(steps);
        Assert.True(at.Z + 4f <= StrideAccumulator.StepLength(1.4f) + 0.15f,
                    $"the first step came {at.Z + 4f:F2} m in, past a whole step");
    }

    /// <summary>Somebody first heard standing still starts a walk with a foot down, as a tap does.</summary>
    [Fact]
    public void SomebodyFirstHeardStandingStepsWhenTheyStart()
    {
        var others = new OtherBodies();
        int steps = 0;
        others.OnStepTriggered += (_, _, _, _, _) => steps++;

        others.Update(Frame(new Vector3(0, 0, 0), Vector3.Zero), Listener);
        others.Update(Frame(new Vector3(0, 0, 0.14f), new Vector3(0, 0, 1.4f)), Listener);

        Assert.Equal(1, steps);
    }

    /// <summary>A crowd's phases are spread over the step, so a crowd heard at once does not march.</summary>
    [Fact]
    public void ACrowdDoesNotMarchInStep()
    {
        var tenths = new int[10];
        for (int id = 1; id <= 83; id++)
        {
            float phase = OtherBodies.StridePhase(id);
            Assert.InRange(phase, 0f, 1f);
            tenths[Math.Min(9, (int)(phase * 10f))]++;
        }
        Assert.All(tenths, n => Assert.InRange(n, 4, 13));
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

    /// <summary>The floor under a body decides its step, from the client's own copy of the world.</summary>
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
    /// A body at rest whose position changes (a teleport, a correction) is being moved, not walking.
    /// Riders are kept out by their definition, not by this (docs/COMMON_NOTES.md, Walking).
    /// </summary>
    [Fact]
    public void ABodyThatIsBeingCarriedDoesNotWalk()
    {
        var others = new OtherBodies();
        var steps = Walk(others, Listener, new Vector3(0, 0, -4f), metresPerUpdate: 0.4f, updates: 20,
                         underItsOwnPower: false);

        Assert.Empty(steps);
    }

    /// <summary>Your own feet are heard from the predicted position only; the server's copy would be a
    /// phantom a tenth of a second behind you.</summary>
    [Fact]
    public void YourOwnBodyIsNotHeardTwice()
    {
        var others = new OtherBodies();
        var steps = Walk(others, listenerId: Walker, from: new Vector3(0, 0, -4f), metresPerUpdate: 0.4f, updates: 20);

        Assert.Empty(steps);
    }

    /// <summary>A crate sliding at walking pace makes no footsteps.</summary>
    [Fact]
    public void SomethingThatIsNotABodyDoesNotWalk()
    {
        var others = new OtherBodies();
        var steps = Walk(others, Listener, new Vector3(0, 0, -4f), metresPerUpdate: 0.4f, updates: 20,
                         type: EntityType.Item);

        Assert.Empty(steps);
    }

    /// <summary>A body off the ground banks no distance and arrives with a landing. Vertical velocity is
    /// the signal: the server clamps a grounded body's to zero.</summary>
    [Fact]
    public void ABodyInTheAirLandsRatherThanStepping()
    {
        var others = new OtherBodies();
        int landings = 0, steps = 0;
        others.OnLandTriggered += (_, _, _) => landings++;
        others.OnStepTriggered += (_, _, _, _, _) => steps++;

        var at = new Vector3(0, 2f, 0);

        // Falling: moving, and plainly not on anything.
        for (int i = 0; i < 6; i++)
        {
            at -= new Vector3(0, 0.3f, 0);
            others.Update(Frame(at, new Vector3(0, -5f, 0)), Listener);
        }
        Assert.Equal(0, steps);
        Assert.Equal(0, landings);

        // Arriving.
        at.Y = 0f;
        others.Update(Frame(at, Vector3.Zero), Listener);

        Assert.Equal(1, landings);
        Assert.Equal(0, steps);
    }

    /// <summary>Somebody who walks out of earshot takes their half-finished stride with them: where they
    /// come back is not a distance they walked.</summary>
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
