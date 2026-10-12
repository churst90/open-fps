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
    // ── The world time (the second slice) ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A world's weather reads its world's time: a wind that is rising is read where the clock says the world
    /// is, at the ramp's start, its middle and its end, whatever the machine's calendar says.
    /// </summary>
    [Fact]
    public void A_weather_reads_the_wind_at_its_worlds_time()
    {
        var clock = new SteppedAudioClock { WorldTime = 5000.0 };
        var weather = new WorldWeather { Clock = clock };
        Assert.Equal(5000.0, weather.Now);
        // From 2 m/s to 12 m/s over ten seconds, starting at 5000.
        weather.Wind = WindWeather.Steady(2f, 250f, 0.2f).Following(WindAir.FromCompass(12f, 250f, 0.2f), 5000.0, 10.0);
        Assert.Equal(2f, weather.MeanSpeed, 2);
        clock.WorldTime = 5005.0;
        Assert.Equal(7f, weather.MeanSpeed, 2);
        clock.WorldTime = 5010.0;
        Assert.Equal(12f, weather.MeanSpeed, 2);
        clock.WorldTime = 9000.0;
        Assert.Equal(12f, weather.MeanSpeed, 2);
    }

    /// <summary>
    /// The mixer's weather and a facade's own are on the world's clock, the same object, so the gusts are read at
    /// the moment the positions are stamped on.
    /// </summary>
    [Fact]
    public void A_worlds_weather_is_on_its_worlds_clock()
    {
        var given = new SteppedAudioClock();
        var mixer = new FmodAudioProvider { Clock = given };
        Assert.Same(given, mixer.Weather.Clock);
        Assert.Same(given, mixer.Traced.Clock);
        var plain = new FmodAudioProvider();
        Assert.Same(plain.Clock, plain.Weather.Clock);
        Assert.Same(plain.Clock, plain.Traced.Clock);

        var facade = new AudioEngineFacade(new VoiceLifecycleTests.RecordingProvider(), given);
        Assert.Same(given, facade.Clock);
        Assert.Same(given, facade.Weather.Clock);
    }

    /// <summary>
    /// A fire's age is told on its world's time: lit at 1,000 s and heard when the world is at 1,090, it has
    /// burned ninety seconds; a minute on, a hundred and fifty.
    /// </summary>
    [Fact]
    public void A_fire_ages_on_its_worlds_time()
    {
        var clock = new SteppedAudioClock { WorldTime = 1090.0 };
        var weather = new WorldWeather { Clock = clock };
        var fire = new FireVoiceState(FireSpec.Campfire, 48000f, 3, Vector3.Zero, weather) { LitAt = 1000.0, Running = true };
        var block = new float[1024];
        fire.Render(block);
        Assert.Equal(90.0, fire.Fire.Age, 6);
        clock.WorldTime = 1150.0;
        fire.Render(block);
        Assert.Equal(150.0, fire.Fire.Age, 6);
    }

    /// <summary>
    /// Rain's swell starts on its world's time, so every patch round a listener swells together and two clients
    /// agree; a rain voice made alone reads the shared world time.
    /// </summary>
    [Fact]
    public void Rain_starts_on_its_worlds_time()
    {
        var clock = new SteppedAudioClock { WorldTime = 777.25 };
        var given = new RainVoiceState(RainFeeds.Feed[0], 48000f, 5, clock: clock);
        Assert.Equal(777.25, given.Synth.Clock);
        var alone = new RainVoiceState(RainFeeds.Feed[0], 48000f, 5);
        Assert.InRange(alone.Synth.Clock - WindField.Now(), -1.0, 1.0);
    }

    /// <summary>
    /// A hob's knob is aged on its world's time. Turned half a minute before the world's now, the burner has
    /// long been alight when the voice is made; turned at the world's now, it is still lighting.
    /// </summary>
    [Fact]
    public void A_hobs_knob_is_aged_on_its_worlds_time()
    {
        var spec = GasHobSpec.FourBurnerNatural;
        string key = new HobKey("hob4", "0000", "3000", 2000.0).Format();
        var later = new StoveVoiceState(spec, key, 48000f, 3, new SteppedAudioClock { WorldTime = 2030.0 });
        var now = new StoveVoiceState(spec, key, 48000f, 3, new SteppedAudioClock { WorldTime = 2000.0 });
        var block = new float[256];
        later.Render(block);
        now.Render(block);
        Assert.True(later.Hob.IsLit(0), "half a minute on, the burner is alight");
        Assert.False(now.Hob.IsLit(0), "the moment the knob is turned, it is not");
    }

    /// <summary>
    /// The wind a client is sent is ramped to from its world's time: the ramp starts at the clock's world time,
    /// not the machine's.
    /// </summary>
    [Fact]
    public void A_clients_wind_ramps_from_its_worlds_time()
    {
        var clock = new SteppedAudioClock { WorldTime = 4000.0 };
        var world = new ClientWorldState { Clock = clock };
        world.Clear(new Vector3(100f, 20f, 100f));
        world.UpdateAtmosphere(new OpenFPS.Common.Networking.WorldStateUpdate { WindVelocity = new Vector3(9f, 0f, 0f), WindGustiness = 0.2f });
        Assert.Equal(4000.0, world.Wind.RampStart);
    }

    /// <summary>
    /// A stamp of a world's clock is never zero or near it, whatever the clock reads, so the holders that read
    /// zero as "never" (a recent answer's slot, a late answer) are right at a stepped clock's nought; and two
    /// stamps are apart by what the clock moved.
    /// </summary>
    [Fact]
    public void A_stamp_is_never_nought_and_measures_the_clock()
    {
        var clock = new SteppedAudioClock();
        long ms0 = clock.StampMs(), ticks0 = clock.StampTicks();
        Assert.True(ms0 > 3_600_000, $"{ms0}");
        Assert.True(ticks0 > 3600L * TimeSpan.TicksPerSecond, $"{ticks0}");
        clock.Now = 1.5;
        Assert.Equal(1500, clock.StampMs() - ms0);
        Assert.Equal((long)(1.5 * TimeSpan.TicksPerSecond), clock.StampTicks() - ticks0);
    }

    /// <summary>Real time only goes forward, and its two counters agree about how much.</summary>
    [Fact]
    public void Real_time_goes_forward()
    {
        long ms = RealTime.Milliseconds, ticks = RealTime.Ticks;
        Thread.Sleep(60);
        long dMs = RealTime.Milliseconds - ms, dTicks = RealTime.Ticks - ticks;
        Assert.InRange(dMs, 30, 5000);
        Assert.InRange(dTicks / (double)TimeSpan.TicksPerMillisecond, 45, 5000);
    }

}
