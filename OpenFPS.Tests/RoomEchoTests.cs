using System.Numerics;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// In a room, a clap's first answers are placed voices, one off each wall (WorldAudioPlayer.
/// QueueRoomEchoes), because the traced response is nearly all omnidirectional and a room made of it
/// sits in the middle of the head and does not move when the head turns (2026-09-29). These check
/// the geometry that feeds them, in Marlow flat 01F as the city builds it: the walls either side
/// must both answer, from their own sides, inside the window before the tail.
/// </summary>
public class RoomEchoTests
{
    private readonly ITestOutputHelper _o;
    public RoomEchoTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }

    private static List<EarlyReflections.Solid> Flat()
    {
        var q = Quaternion.Identity;
        return new()
        {
            new(new Vector3(0, 0.04f, 0), new Vector3(8.65f, 0.04f, 17.86f), q, "Carpet"),
            new(new Vector3(0, 2.735f, 0), new Vector3(20.5f, 0.03f, 89.3f), q, "Plaster"),
            new(new Vector3(4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, 80f), q, "Brick"),
            new(new Vector3(-4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, 17.86f), q, "Plaster"),
            new(new Vector3(0, 1.4f, -9.0f), new Vector3(8.65f, 2.73f, 0.2f), q, "Plaster"),
            new(new Vector3(0, 1.4f, 9.0f), new Vector3(8.65f, 2.73f, 0.2f), q, "Plaster"),
        };
    }

    [Fact]
    public void BothSideWallsAnswerAClapFromTheirOwnSides()
    {
        var ear = new Vector3(0.175f, 1.7f, 0.16f);
        var hands = ear + new Vector3(0f, -0.39f, 0.3f);
        var into = new List<EarlyReflections.Arrival>();
        EarlyReflections.Find(hands, ear, Flat(), into, 343f, maxOrder: EarlyReflections.MaxOrder, keep: 24);
        var early = into.Where(a => a.ExtraDelaySeconds <= 0.08f).ToList();
        foreach (var a in early.OrderBy(a => a.ExtraDelaySeconds))
            _o.WriteLine($"order {a.Order} at {a.ExtraDelaySeconds * 1000,5:F1} ms, {20 * MathF.Log10(a.GainMid),6:F1} dB, image ({a.ImagePosition.X:F1}, {a.ImagePosition.Y:F1}, {a.ImagePosition.Z:F1})");
        Assert.Contains(early, a => a.Order == 1 && a.ImagePosition.X > ear.X + 3f);   // the brick wall, to the right
        Assert.Contains(early, a => a.Order == 1 && a.ImagePosition.X < ear.X - 3f);   // the plaster wall, to the left
        Assert.Contains(early, a => a.Order == 1 && a.ImagePosition.Y > 3f);           // the ceiling
        Assert.True(early.Count >= 5, $"only {early.Count} arrivals inside 80 ms");
    }

    /// <summary>
    /// Where Cody stands in the flat (/tp -14 -85 0.5 is 2.6 m from its middle along its length), a
    /// clap half a metre from the ear must have the nearer END wall placed, 6.4 m off, 36 ms after the
    /// clap — the arrival that says how long the room is. Audibility used to be judged against the
    /// direct sound at its true half metre, which dropped every wall past a twelve-metre round trip,
    /// and the room's length was left to the omnidirectional tail (EarlyReflections.HeardReference).
    /// </summary>
    [Fact]
    public void TheEndWallOfALongRoomAnswersAClapAtArmsLength()
    {
        var ear = new Vector3(0.475f, 1.8f, 2.56f);
        var hands = ear + new Vector3(0f, -0.49f, 0.3f);
        var into = new List<EarlyReflections.Arrival>();
        EarlyReflections.Find(hands, ear, Flat(), into, 343f, maxOrder: EarlyReflections.MaxOrder, keep: 24);
        var early = into.Where(a => a.ExtraDelaySeconds <= 0.08f).ToList();
        foreach (var a in early.OrderBy(a => a.ExtraDelaySeconds))
            _o.WriteLine($"order {a.Order} at {a.ExtraDelaySeconds * 1000,5:F1} ms, {20 * MathF.Log10(a.GainMid),6:F1} dB, image ({a.ImagePosition.X:F1}, {a.ImagePosition.Y:F1}, {a.ImagePosition.Z:F1})");
        var end = early.Where(a => a.Order == 1 && a.ImagePosition.Z > ear.Z + 5f).ToList();
        Assert.True(end.Count == 1, $"the end wall ahead was placed {end.Count} times");
        Assert.InRange(end[0].ExtraDelaySeconds, 0.030f, 0.045f);
        // ...and between the side walls' answers and the tail there is no longer a hole.
        Assert.Contains(early, a => a.ExtraDelaySeconds is > 0.02f and < 0.05f);
        // The gains themselves are still spread from the true direct distance: nothing got louder.
        Assert.All(early, a => Assert.True(a.GainMid <= 1f));
    }
}
