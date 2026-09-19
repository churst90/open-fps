using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The walls answering a footfall — the arrivals between the near field and the tail.
///
/// Reported from the chair, walking the city's car park: *"a parking garage is big, this sounds like
/// a box... rather than reflections being emitted from the walls, it's like the whole room is
/// reverby... it surrounds me rather than being directional. I should hear reflections from a wall,
/// from the wall's direction, not an all around reflection from everywhere at once."*
///
/// That is exactly what direct-plus-diffuse-tail with nothing in between sounds like. The near-field
/// probes cover the last three metres and the diffuse bus covers the tail; everything from three
/// metres to the size of the room was missing, and in a 21 by 28 metre garage that is the whole room.
///
/// These hold what has to be true of the arrivals for the room to have a shape: that a low ceiling
/// answers SOON and from ABOVE, that a far wall answers LATER and from ITS OWN DIRECTION, and that
/// neither is louder than the step it is a copy of.
/// </summary>
public class StepReflectionTests
{
    public StepReflectionTests() => AcousticRegistry.Initialize();

    /// <summary>A car park: 21 by 28 metres and 2.5 high, as the six concrete slabs that enclose it.</summary>
    private static List<EarlyReflections.Solid> Garage(float ceiling = 2.5f)
    {
        const float t = 0.3f;
        var c = Quaternion.Identity;
        return new List<EarlyReflections.Solid>
        {
            new(new Vector3(0, -t / 2, 0), new Vector3(21f, t, 28f), c, "Concrete"),              // floor
            new(new Vector3(0, ceiling + t / 2, 0), new Vector3(21f, t, 28f), c, "Concrete"),     // ceiling
            new(new Vector3(-10.5f - t / 2, ceiling / 2, 0), new Vector3(t, ceiling, 28f), c, "Concrete"),
            new(new Vector3(10.5f + t / 2, ceiling / 2, 0), new Vector3(t, ceiling, 28f), c, "Concrete"),
            new(new Vector3(0, ceiling / 2, -14f - t / 2), new Vector3(21f, ceiling, t), c, "Concrete"),
            new(new Vector3(0, ceiling / 2, 14f + t / 2), new Vector3(21f, ceiling, t), c, "Concrete"),
        };
    }

    private static List<EarlyReflections.Arrival> Arrivals(List<EarlyReflections.Solid> room)
    {
        var into = new List<EarlyReflections.Arrival>();
        EarlyReflections.Find(Foot, Ear, room, into, 343.0f);
        return into;
    }

    /// <summary>The ear at head height, the step at the feet — where your own footfalls actually are.</summary>
    private static readonly Vector3 Ear = new(0f, 1.7f, 0f);
    private static readonly Vector3 Foot = new(0.15f, 0.1f, 0f);

    [Fact]
    public void ALowCeilingAnswersSoonAndFromAbove()
    {
        var arrivals = Arrivals(Garage());
        Assert.NotEmpty(arrivals);

        // The ceiling is 0.8 m over the ear and 2.4 m over the foot, so its image is about 3.2 m away
        // against 1.6 m direct — an extra 1.6 m, which is under five milliseconds.
        var above = arrivals.Find(a => (a.ImagePosition.Y - Ear.Y) > 1f);
        Assert.True(above.ExtraDelaySeconds > 0f, "nothing answered from overhead in a room 2.5 m high");
        Assert.InRange(above.ExtraDelaySeconds, 0.002f, 0.025f);
        Assert.True(above.GainMid < 1f, "a reflection cannot be louder than the sound it copies");
    }

    /// <summary>
    /// A wall ten metres off answers from ITS OWN DIRECTION and tens of milliseconds later — which is
    /// the information the diffuse bus cannot carry, because a bus has no direction at all.
    /// </summary>
    [Fact]
    public void AFarWallAnswersLaterAndFromItsOwnSide()
    {
        var arrivals = Arrivals(Garage());

        var west = arrivals.Find(a => a.ImagePosition.X < -5f);
        var east = arrivals.Find(a => a.ImagePosition.X > 5f);
        Assert.True(west.ExtraDelaySeconds > 0f || east.ExtraDelaySeconds > 0f,
            "neither side wall answered in a room 21 m across");

        foreach (var a in arrivals)
        {
            Assert.True(a.GainMid <= 1f, $"an arrival came back at {a.GainMid:F2} of the direct sound");
            Assert.True(a.ExtraDelaySeconds >= 0f);
            // Nothing in a room this size can take longer than the room is wide, twice over.
            Assert.True(a.ExtraDelaySeconds < 0.25f, $"{a.ExtraDelaySeconds * 1000f:F0} ms in a 28 m room");
        }
    }

    /// <summary>
    /// A room with the walls taken away answers with nothing. The floor of the ladder: standing in the
    /// open, a footstep is a footstep and there is nothing to hear after it.
    /// </summary>
    [Fact]
    public void OpenGroundAnswersOnlyWithTheGround()
    {
        var ground = new List<EarlyReflections.Solid>
        {
            new(new Vector3(0, -0.1f, 0), new Vector3(200f, 0.2f, 200f), Quaternion.Identity, "Asphalt"),
        };
        var arrivals = Arrivals(ground);

        foreach (var a in arrivals)
            Assert.True(a.ImagePosition.Y < Foot.Y, "something answered from above, in the open air");
    }

    /// <summary>
    /// The nearer the surface, the sooner and the louder. Held because it is the whole of what makes
    /// a small room read as small, and it is one comparison rather than a number anybody chose.
    /// </summary>
    [Fact]
    public void ALowerCeilingAnswersSoonerAndLouder()
    {
        float DelayOfCeiling(float height, out float gain)
        {
            var above = Arrivals(Garage(height)).Find(a => (a.ImagePosition.Y - Ear.Y) > 0.5f);
            gain = above.GainMid;
            return above.ExtraDelaySeconds;
        }

        float lowDelay = DelayOfCeiling(2.5f, out float lowGain);
        float highDelay = DelayOfCeiling(9f, out float highGain);

        Assert.True(lowDelay > 0f && highDelay > 0f, "a ceiling did not answer");
        Assert.True(lowDelay < highDelay, $"a 2.5 m ceiling answered in {lowDelay * 1000:F1} ms, a 9 m one in {highDelay * 1000:F1}");
        Assert.True(lowGain > highGain, "the nearer ceiling should come back louder");
    }
}
