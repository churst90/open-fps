using OpenFPS.Sound;

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

    /// <summary>
    /// A mode left alone comes to rest. It would otherwise decay into subnormal numbers (under 1e-308) and
    /// stay there for the rest of the render, and a processor that works on those in microcode takes about a
    /// hundred times as long over each: the front door above took seven minutes to render on one.
    /// </summary>
    [Fact]
    public void AModeLeftAloneComesToRest()
    {
        // Two lossy modes: the upper one has lost 300 orders of magnitude after 30 ms, the lower after half a second.
        double dt = 1.0 / (48000 * DoorPhysics.Oversample);
        var modes = new DoorPhysics.Modes(new[] { 14000.0, 900.0 }, new[] { 0.5, 0.5 }, new[] { 0.01, 0.01 }, new[] { 1.0, 1.0 }, dt);
        modes.Push(new[] { 1.0, 1.0 }, 100);
        double loudest = 0;
        int steps = 48000 * DoorPhysics.Oversample * 2;
        for (int i = 0; i < steps; i++)
        {
            loudest = Math.Max(loudest, Math.Abs(modes.Step()));
            for (int k = 0; k < modes.N; k++)
                if (double.IsSubnormal(modes.Q[k]) || double.IsSubnormal(modes.V[k]) || double.IsSubnormal(modes.Acc[k]))
                    Assert.Fail($"mode {k} is on subnormal numbers at step {i}: {modes.Q[k]:E3}, {modes.V[k]:E3}");
        }
        Assert.True(loudest > 1, $"the modes rang ({loudest})");
        Assert.All(modes.Q, q => Assert.Equal(0.0, q));
        Assert.All(modes.V, v => Assert.Equal(0.0, v));
        Assert.Equal(0.0, modes.Step());
    }

    /// <summary>
    /// A force too small to move anything is no force. A latch or a seal that has all but stopped goes on
    /// pushing its plate with numbers that small, every step, and each one put every mode of the plate on
    /// subnormal numbers again.
    /// </summary>
    [Fact]
    public void AForceTooSmallToMoveAnythingIsNoForce()
    {
        double dt = 1.0 / (48000 * DoorPhysics.Oversample);
        var modes = new DoorPhysics.Modes(new[] { 440.0, 5000.0 }, new[] { 0.02, 0.02 }, new[] { 0.5, 0.5 }, new[] { 1.0, 1.0 }, dt);
        var shape = new[] { 1.0, 0.25 };
        foreach (double tiny in new[] { double.Epsilon, 1e-310, -1e-310, 1e-250, -1e-201 })
        {
            modes.Push(shape, tiny);
            Assert.All(modes.F, f => Assert.Equal(0.0, f));
            Assert.Equal(0.0, modes.Step());
            Assert.All(modes.Q, q => Assert.Equal(0.0, q));
            Assert.All(modes.V, v => Assert.Equal(0.0, v));
        }
        // One that is not too small is still a force, however small, and is kept whole.
        modes.Push(shape, 1e-150);
        Assert.Equal(1e-150, modes.F[0]);
        Assert.Equal(0.25e-150, modes.F[1]);
        modes.Step();
        Assert.NotEqual(0.0, modes.Q[0]);
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
