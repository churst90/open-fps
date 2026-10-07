using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// The door models of 2026-10-05 (<see cref="GlassDoor"/>, <see cref="LockCylinder"/>, <see cref="ElevatorDoor"/>,
/// and the push and pull openings of <see cref="KnobDoor"/> and <see cref="PushBarDoor"/>): each renders the same
/// every time, finite, at a level in the range its own survey measured, and does what its script says.
/// </summary>
public class DoorModelTests
{
    private static double PeakDb(float[] pcm, double pascalsAtFullScale)
        => 20 * Math.Log10(Math.Max(1e-12, pcm.Max(v => Math.Abs((double)v))) * pascalsAtFullScale / 2e-5);

    private static void Finite(float[] pcm) => Assert.All(pcm, v => Assert.True(float.IsFinite(v)));

    [Fact]
    public void TheKeyUnlocksTheSameEveryTimeAndDrawsTheLatchWhenTheDoorSaysItDoes()
    {
        var r1 = new LockCylinder.Report();
        var a = LockCylinder.RenderUnlock(LockCylinder.Host.AluminiumStile, 1, 48000, r1);
        var b = LockCylinder.RenderUnlock(LockCylinder.Host.AluminiumStile, 1, 48000);
        Assert.Equal(a, b);
        Finite(a);
        Assert.InRange(PeakDb(a, LockCylinder.PascalsAtFullScale), 85, 120);
        // The server sends the opening UnlockSeconds after the key: the latch must be drawn by then, near it.
        Assert.InRange(r1.DrawnAt, LockCylinder.UnlockSeconds - 0.25, LockCylinder.UnlockSeconds + 0.05);
        Assert.Contains(r1.Events, e => e.Contains("the shoulder on the plug's face"));
    }

    [Fact]
    public void KeysNameTheirModels()
    {
        foreach (var how in new[] { GlassDoor.Opening.Push, GlassDoor.Opening.Pull, GlassDoor.Opening.Key })
        {
            string k = GlassDoor.Key(GlassDoor.Kind.PushBar, false, how, GlassDoor.Glazing.Laminated, 6, 1.1f, 1.0f, 2.1f);
            Assert.True(GlassDoor.TryParseKey(k, out bool closing, out var parsed, out var door, out float swing));
            Assert.False(closing); Assert.Equal(how, parsed); Assert.Equal(GlassDoor.Glazing.Laminated, door.Glass);
            Assert.Equal(2, door.Variant); Assert.Equal(1.1f, swing, 2);
        }
        Assert.True(LockCylinder.TryParseKey(LockCylinder.Key(LockCylinder.Host.SteelDoor, 3), out var host, out int v));
        Assert.Equal(LockCylinder.Host.SteelDoor, host); Assert.Equal(3, v);
        Assert.True(ElevatorDoor.TryParseKey(ElevatorDoor.Key(true, 5, 2.5f, 0.55f, 2.1f, false), out bool c, out var lift, out float t));
        Assert.True(c); Assert.False(lift.Operator); Assert.Equal(1, lift.Variant); Assert.Equal(2.5f, t, 2);

        // The approved openings' keys are as they were; the new side's carries a last field.
        string pull = KnobDoor.Key(false, KnobDoor.Construction.HollowCore, 1, 0.9f, KnobDoor.Shut.Normal, 0.9f, 2.1f);
        Assert.Equal("knobdoor:open:hollow:1:90:1:90:210", pull);
        string push = KnobDoor.Key(false, KnobDoor.Construction.HollowCore, 1, 0.9f, KnobDoor.Shut.Normal, 0.9f, 2.1f, push: true);
        Assert.True(KnobDoor.TryParseKey(push, out _, out _, out _, out _, out bool isPush) && isPush);
        Assert.True(KnobDoor.TryParseKey(pull, out _, out _, out _, out _, out isPush) && !isPush);
        Assert.Equal("pushbardoor:open:1:140:100:210", PushBarDoor.Key(false, 1, 1.4f, 1.0f, 2.1f));
        Assert.True(PushBarDoor.TryParseKey(PushBarDoor.Key(false, 1, 1.4f, 1.0f, 2.1f, pull: true), out _, out _, out _, out bool isPull) && isPull);
        // A shutting has no side.
        Assert.DoesNotContain(":push", KnobDoor.Key(true, KnobDoor.Construction.HollowCore, 1, 0.9f, KnobDoor.Shut.Normal, 0.9f, 2.1f, push: true));
    }

    [Fact]
    public void TheGlassPullDoorShutsOnItsCloserTheSameEveryTime()
    {
        var door = new GlassDoor.Door { Kind = GlassDoor.Kind.Pull, Variant = 1, Seed = 2 };
        var a = GlassDoor.RenderClose(door, 48000);
        var b = GlassDoor.RenderClose(door, 48000);
        Assert.Equal(a, b);
        Finite(a);
        Assert.InRange(PeakDb(a, GlassDoor.PascalsAtFullScale), 105, 135);
    }

    [Fact]
    public void TheFrontDoorOpensFromOutsideWithTheKeyHeldAndLetsItGo()
    {
        var rep = new GlassDoor.Report();
        var pcm = GlassDoor.RenderOpen(new GlassDoor.Door { Kind = GlassDoor.Kind.PushBar, Variant = 1, Seed = 2 }, GlassDoor.Opening.Key, 48000, 1.1, rep);
        Finite(pcm);
        Assert.Contains(rep.Events, e => e.Contains("leaf off its stop"));
        Assert.Contains(rep.Events, e => e.Contains("key let go"));
        Assert.DoesNotContain(rep.Events, e => e.Contains("never left"));
        // Opened, it is far quieter than a slam: a pull and a latch let back out.
        Assert.InRange(PeakDb(pcm, GlassDoor.PascalsAtFullScale), 80, 115);
    }

    [Fact]
    public void TheLiftDoorRunsInTheServersTime()
    {
        var rep = new ElevatorDoor.Report();
        var pcm = ElevatorDoor.RenderOpen(new ElevatorDoor.Door { Variant = 1, Seed = 2 }, 48000, 1.8, rep);
        Finite(pcm);
        var at = rep.Events.First(e => e.Contains("the operator at its end"));
        double travel = double.Parse(at.Split('(')[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(travel, 1.65, 1.95);
        Assert.InRange(PeakDb(pcm, ElevatorDoor.PascalsAtFullScale), 70, 110);
    }
}
