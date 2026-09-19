using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The acoustic worker answers for a PLACE, and files the answer under a voice id. A one-shot's id
/// comes from a small pool and is reused every few seconds, so without this rule a fresh footstep was
/// handed the previous occupant's result — the occlusion, EQ and apparent position of a step taken
/// seconds ago somewhere else in the room — for its first frames, and then snapped to its own. Heard
/// as a quiet click from the wrong place, then the step: "pop pop pop click" as you walk.
/// </summary>
public class StaleAcousticResultTests
{
    private static WorldSnapshot WorldWithEntity(int id)
    {
        var world = new WorldSnapshot();
        world.Entities[id] = new EntitySnapshot
        {
            Id = id,
            Definition = new EntityDefinition { EntityId = id, Type = EntityType.StaticObject },
            Transform = new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        return world;
    }

    private static List<AcousticPathData> ResultComputedAt(Vector3 sourcePos) => new()
    {
        new AcousticPathData { Occlusion = 0.5f, SourcePosition = sourcePos, ApparentPosition = sourcePos }
    };

    [Fact]
    public void APooledOneShotDoesNotInheritThePreviousOccupantsResult()
    {
        var world = WorldWithEntity(5);
        const int footstep = -107;                        // not an entity: a pooled one-shot id
        var stepNow = new Vector3(8f, 0.1f, 17f);
        var stepSecondsAgo = new Vector3(3f, 0.1f, 12f);  // where the id's last occupant was

        Assert.False(ClientAudioSystem.ResultIsForThisVoice(world, footstep, stepNow, ResultComputedAt(stepSecondsAgo)),
            "a result computed for a footstep taken across the room must not be applied to this one");
    }

    [Fact]
    public void AOneShotTakesAResultComputedForWhereItIs()
    {
        var world = WorldWithEntity(5);
        const int footstep = -107;
        var here = new Vector3(8f, 0.1f, 17f);
        var nearlyHere = here + new Vector3(0.3f, 0f, 0.2f);   // the worker's request lagged a frame

        Assert.True(ClientAudioSystem.ResultIsForThisVoice(world, footstep, here, ResultComputedAt(nearlyHere)));
    }

    [Fact]
    public void AnEntityKeepsItsLastResultBetweenTicks()
    {
        // A car forty metres down the road between two acoustic ticks is still the same car; its last
        // answer is the best one it has until the next arrives.
        var world = WorldWithEntity(5);
        var carNow = new Vector3(40f, 0.6f, 4.5f);
        var carAtLastTick = new Vector3(10f, 0.6f, 4.5f);

        Assert.True(ClientAudioSystem.ResultIsForThisVoice(world, 5, carNow, ResultComputedAt(carAtLastTick)));
    }

    [Fact]
    public void TheToleranceIsAboutAStride()
    {
        // Wide enough that a request answered a frame late is still the voice's own; tight enough
        // that a step taken elsewhere in a room is not.
        Assert.InRange(ClientAudioSystem.StaleResultMetres, 0.5f, 1.5f);
    }
}
