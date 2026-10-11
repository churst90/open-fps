using System.Numerics;
using OpenFPS.Client.AudioEngine.Data;

namespace OpenFPS.Tests;

/// <summary>
/// The acoustic worker answers for a place but files the answer under a voice id, and one-shot ids are
/// pooled: a fresh footstep took the last occupant's result for its first frames, heard as "pop pop pop
/// click" as you walk. Every asker takes an answer only if it was traced for about where the source is and
/// where the ear is (AcousticPathData.Answers).
/// </summary>
public class StaleAcousticResultTests
{
    private static AcousticPathData TracedAt(Vector3 source, Vector3 ear) => new()
    {
        Occlusion = 0.5f, SourcePosition = source, SimulatedFrom = source, ListenerPosition = ear,
        ApparentPosition = source, Provenance = PathProvenance.Simulated,
    };

    private static readonly Vector3 Ear = new(0f, 1.65f, 0f);

    [Fact]
    public void APooledOneShotDoesNotInheritThePreviousOccupantsResult()
    {
        var stepNow = new Vector3(8f, 0.1f, 17f);
        var stepSecondsAgo = new Vector3(3f, 0.1f, 12f);  // where the id's last occupant was

        Assert.False(TracedAt(stepSecondsAgo, Ear).Answers(stepNow, 0f, Ear, 0f),
            "a result computed for a footstep taken across the room must not be applied to this one");
    }

    [Fact]
    public void AOneShotTakesAResultComputedForWhereItIs()
    {
        var here = new Vector3(8f, 0.1f, 17f);
        var nearlyHere = here + new Vector3(0.3f, 0f, 0.2f);   // the worker's request lagged a frame

        Assert.True(TracedAt(nearlyHere, Ear).Answers(here, 0f, Ear, 0f));
    }

    [Fact]
    public void AMovingSourceKeepsAnAnswerFromAMomentAgo()
    {
        // A car at 20 m/s asked every tenth frame, answered by a worker a third of a second behind: 13 m
        // back is still its own answer, the best it has until the next arrives.
        var carNow = new Vector3(40f, 0.6f, 4.5f);
        var carAtLastTick = new Vector3(27f, 0.6f, 4.5f);

        Assert.True(TracedAt(carAtLastTick, Ear).Answers(carNow, 20f, Ear, 0f));
    }

    [Fact]
    public void AnAnswerFromLongAgoIsNot()
    {
        // A train's horn is asked about only while it sounds: the next horn began on the answer the last one
        // left, minutes and a street away.
        var hornNow = new Vector3(400f, 3f, 20f);
        var lastHorn = new Vector3(40f, 3f, 20f);

        Assert.False(TracedAt(lastHorn, Ear).Answers(hornNow, 20f, Ear, 0f));
    }

    [Fact]
    public void AnAnswerAskedFromAnotherPlaceIsNot()
    {
        // The source has not moved; the ear has (a teleport, a door gone through). What was in the way from
        // there is not what is in the way from here.
        var source = new Vector3(30f, 0.6f, 4.5f);
        var earBefore = new Vector3(-40f, 1.65f, 0f);

        Assert.False(TracedAt(source, earBefore).Answers(source, 0f, Ear, 0f));
        Assert.True(TracedAt(source, Ear + new Vector3(1f, 0f, 0f)).Answers(source, 0f, Ear, 0f),
            "a step taken while the answer was on its way leaves it the ear's own");
    }

    [Fact]
    public void ADrivingEarKeepsItsAnswers()
    {
        // Riding at 25 m/s, the ear is metres from where it asked by the time any answer lands.
        var source = new Vector3(60f, 0.6f, 4.5f);
        var askedFrom = Ear - new Vector3(9f, 0f, 0f);

        Assert.True(TracedAt(source, askedFrom).Answers(source, 0f, Ear, 25f));
    }

    [Fact]
    public void AHeldAnswerIsJudgedByWhereItWasTraced()
    {
        // Held over a full pool and moved with the source, it says it is for here; it was traced 30 m back.
        var now = new Vector3(40f, 0.6f, 4.5f);
        var held = TracedAt(new Vector3(10f, 0.6f, 4.5f), Ear) with { SourcePosition = now, Provenance = PathProvenance.Held };

        Assert.False(held.Answers(now, 5f, Ear, 0f));
    }

    [Fact]
    public void TheToleranceIsAboutAStride()
    {
        // Wide enough that a request answered a frame late is still the voice's own; tight enough
        // that a step taken elsewhere in a room is not.
        Assert.InRange(AcousticPathData.SourceSlackMetres, 0.5f, 1.5f);
    }
}
