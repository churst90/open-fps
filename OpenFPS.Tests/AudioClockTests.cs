using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The audio world's clock is an instance its host shares out (docs/SOUND_LIBRARY_BOUNDARY.md 4.1 and stage 5),
/// where it was a static, one per process. What matters is that there is one per world: a position is stamped
/// on the clock it is aged on, and a train's signal is timed on the clock its voices run on.
/// </summary>
public class AudioClockTests
{
    private readonly ITestOutputHelper _o;
    public AudioClockTests(ITestOutputHelper o) => _o = o;

    /// <summary>A stopwatch clock runs on and is never behind itself; a stepped one reads what it is set to, or
    /// its holder's own time, and both read the one shared world time unless the stepped one is given another.</summary>
    [Fact]
    public void The_clocks_read_as_they_say()
    {
        var running = new StopwatchAudioClock();
        double a = running.Now;
        Thread.Sleep(20);
        double b = running.Now;
        Assert.True(a >= 0 && b > a, $"{a} then {b}");
        Assert.InRange(running.WorldTime - WindField.Now(), -1.0, 1.0);

        var stepped = new SteppedAudioClock();
        Assert.Equal(0.0, stepped.Now);
        stepped.Now = 12.5;
        Assert.Equal(12.5, stepped.Now);
        Assert.InRange(stepped.WorldTime - WindField.Now(), -1.0, 1.0);
        stepped.WorldTime = 99.0;
        Assert.Equal(99.0, stepped.WorldTime);

        double held = 3.0;
        var followed = new SteppedAudioClock(() => held);
        Assert.Equal(3.0, followed.Now);
        held = 4.25;
        Assert.Equal(4.25, followed.Now);
    }

    /// <summary>Two clocks are two timebases: each made is its own, so nothing reads a process's clock by
    /// reaching for a type.</summary>
    [Fact]
    public void Each_clock_is_its_own()
    {
        var first = new StopwatchAudioClock();
        Thread.Sleep(30);
        var second = new StopwatchAudioClock();
        Assert.True(first.Now - second.Now > 0.02, $"{first.Now} against {second.Now}");
    }

    /// <summary>
    /// The facade's clock is the one it was given when its mixer has none of its own (a test's), and the replay's
    /// world is stamped on it: after a sync, the positions' stamp is the stepped clock's reading, to the bit, as
    /// it must be for a stream to be the same on every run.
    /// </summary>
    [Fact]
    public void A_replays_world_is_stamped_on_the_replays_clock()
    {
        Replay.RunScript(r =>
        {
            Assert.Same(r.Facade.Clock, r.World.Clock);
            r.Car(7, "v8_muscle", new Vector3(0f, 0.5f, 30f), new Vector3(0f, 0f, 10f));
            for (int f = 0; f < 5; f++)
            {
                r.Listener(Vector3.Zero, Vector3.Zero, 0f);
                r.Move(7, new Vector3(0f, 0.5f, 30f + f), new Vector3(0f, 0f, 10f));
                Assert.Equal(r.Now, r.Facade.Clock.Now);
                Assert.Equal(r.Now, r.World.GetSnapshot().PositionsSampledAt);
                r.Tick();
                Assert.False(r.Audio.WorldOnAnotherClock);
            }
        });
    }

    /// <summary>
    /// The test harness shares one clock too, and a world left on a clock of its own is found out: its stamps
    /// read after the audio's now, which on one clock cannot be, and the audio system says so (once).
    /// </summary>
    [Fact]
    public void A_world_on_another_clock_is_found_out()
    {
        var h = new ClientAudioHarness();
        Assert.Same(h.Facade.Clock, h.World.Clock);
        h.StandAt(Vector3.Zero);
        h.AddCar(1, "v8_muscle", new Vector3(0f, 0f, 20f));
        h.Tick(3);
        Assert.False(h.Audio.WorldOnAnotherClock);

        // The mistake: a world made on its own, a while before the mixer.
        h.World.Clock = new SteppedAudioClock { Now = h.Facade.Clock.Now + 30.0 };
        h.AddCar(2, "v8_muscle", new Vector3(0f, 0f, 40f));
        h.Tick(2);
        Assert.True(h.Audio.WorldOnAnotherClock);
    }

    /// <summary>
    /// A train's voices run on their train's clock, which is the one its mixer gave it: the timeline's sample
    /// now follows that clock and no other, and a voice of it counts as rendering by it.
    /// </summary>
    [Fact]
    public void A_trains_timeline_runs_on_the_clock_it_was_given()
    {
        var clock = new SteppedAudioClock { Now = 100.0 };
        const int Rate = 48000;
        var train = new TrainVoiceState("light_rail/clock", TrainProfile.ByName("light_rail"), Rate, 7, clock) { Offline = true };
        Assert.Same(clock, train.Clock);
        Assert.Equal(0, train.NowSample);
        clock.Now = 100.5;
        Assert.Equal(Rate / 2, train.NowSample);
        // Another clock moving does not move it.
        var other = new SteppedAudioClock { Now = 500.0 };
        Assert.Equal(Rate / 2, train.NowSample);
        Assert.NotSame(other, train.Clock);

        // Made without one, it has one of its own, as an offline render does.
        var alone = new TrainVoiceState("light_rail/alone", TrainProfile.ByName("light_rail"), Rate, 7) { Offline = true };
        Assert.NotNull(alone.Clock);
        Assert.NotSame(clock, alone.Clock);
    }

    /// <summary>The server keeps its own clock, and what stamps a death reads the one that later asks how long
    /// ago it was: the combat service's clock is the server's.</summary>
    [Fact]
    public void The_servers_combat_reads_the_servers_clock()
    {
        // Nobody logs in here: the server is made for its clock alone.
        var server = new OpenFPS.Server.GameServer(null!);
        var combat = new OpenFPS.Server.Core.CombatService(null!, server, null!);
        double a = server.Clock.Now, b = combat.Clock(), c = server.Clock.Now;
        Assert.InRange(b, a, c);
    }
}
