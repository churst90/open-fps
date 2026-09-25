using System;
using System.Collections.Generic;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// What a bus does when it actually stops.
///
/// Every one of these sounds was already in the model and none of them could ever fire, because
/// they are read off the vehicle's own speed going to zero and STAYING there, and nothing on a
/// city map ever stopped — the buses were on racing lines. So this drives the real voice through a
/// real stop-and-go and listens for them, which is the only way to know the chain works end to
/// end: the speed history, the air system, the ports, and the beeper.
/// </summary>
public class BusStopTests
{
    private const int Rate = 44100, Block = 1024;
    private readonly ITestOutputHelper _o;
    public BusStopTests(ITestOutputHelper o) => _o = o;

    /// <summary>
    /// Pulls up, stands for twelve seconds, pulls away — and the standing part is much louder than
    /// the idling part would be on its own, because the brakes, the kneel and the doors all vent
    /// into it.
    /// </summary>
    [Fact]
    public void StoppingMakesTheAirSystemSpeak()
    {
        var bus = MachineRegistry.VehicleFor("school_bus_na");
        Assert.False(string.IsNullOrEmpty(bus.AirSystem));       // it carries air at all
        Assert.True(bus.DoorChime);                              // and a door beeper

        var r = Drive(bus);
        _o.WriteLine($"arriving {r.Arriving:F1} dB, settled {r.Settled:F1} dB, rolling {r.Rolling:F1} dB, "
                   + $"chime band {r.ChimeBand:F1} dB, doors opened: {r.DoorsOpened}");

        // Measured as a TRANSIENT against the same bus a few seconds later, not as a level.
        // "Louder than sixty decibels" is a test an idling bus passes on its own and proves
        // nothing — which is what the first version of this did.
        Assert.True(r.DoorsOpened, "the bus never opened its doors, so nothing pneumatic fired");
        Assert.True(r.Arriving > r.Settled + 3f,
            $"arriving at the stop made {r.Arriving:F1} dB against {r.Settled:F1} settled — "
          + "the brakes, the kneel and the doors did not speak");
    }

    /// <summary>
    /// And the beeper: there is energy at the piezo's own resonance while the doors are open and
    /// next to none while the bus is moving. A tone test rather than a level test, because the
    /// beeper is 80 dB against an engine of 95 and would not move the total.
    /// </summary>
    [Fact]
    public void TheDoorBeeperSoundsOnlyAtTheStop()
    {
        var bus = MachineRegistry.VehicleFor("school_bus_na");
        var busRun = Drive(bus);
        float chimeWhileStopped = busRun.ChimeBand;
        var quiet = MachineRegistry.VehicleFor("diesel_truck");   // same air system, no beeper
        Assert.False(quiet.DoorChime);
        float chimeOnATruck = Drive(quiet).ChimeBand;

        var probe = new EngineVoiceState(bus, Rate, 5);
        _o.WriteLine($"bus has a chime: {probe.HasDoorChime}, peak beeper {busRun.PeakChimePa:F4} Pa "
                   + $"(80 dB would be about 0.20)");
        _o.WriteLine($"bus {chimeWhileStopped:F1} dB at the piezo band, truck {chimeOnATruck:F1} dB");
        Assert.True(chimeWhileStopped > chimeOnATruck + 6f,
            $"the bus's door beeper is not audible against a vehicle that has none "
          + $"({chimeWhileStopped:F1} vs {chimeOnATruck:F1} dB)");
    }

    /// <summary>
    /// "When I'm on the bus and it stops I should hear the beeping from inside too." The beeper hangs
    /// over the doorway, inside; it was mixed into the part of the voice the interior path replaces
    /// with what gets through the body, and a body's mass law takes a 2.7 kHz beep to nothing.
    /// </summary>
    [Fact]
    public void TheDoorBeeperIsHeardFromInsideTheBus()
    {
        var bus = MachineRegistry.VehicleFor("school_bus_na");
        var outside = Drive(bus).ChimeBand;
        var inside = Drive(bus, inside: true);
        var truck = Drive(MachineRegistry.VehicleFor("diesel_truck"), inside: true).ChimeBand;
        _o.WriteLine($"beeper band: outside {outside:F1} dB, inside {inside.ChimeBand:F1} dB, a truck cab with none {truck:F1} dB");
        Assert.True(inside.DoorsOpened);
        Assert.True(inside.ChimeBand > truck + 6f, "inside the bus the door beeper is no louder than a cab that has none");
    }

    /// <summary>
    /// At a junction a bus holds its service brake and goes again: no spring brakes, no kneel, no
    /// doors — those are for a stop that takes passengers — and one short puff as the pedal comes up
    /// to pull away. Reported: "at an intersection the air brakes are long bursts".
    /// </summary>
    [Fact]
    public void AtAJunctionABusHoldsItsBrakeAndGoes()
    {
        var bus = MachineRegistry.VehicleFor("school_bus_na");
        var ports = Vents(bus, busStop: false, stopFrom: 12f, brakeSeconds: 3f);
        Assert.Equal(0, ports.GetValueOrDefault("parking"));
        Assert.Equal(0, ports.GetValueOrDefault("kneel"));
        Assert.Equal(0, ports.GetValueOrDefault("door"));
        Assert.Equal(1, ports.GetValueOrDefault("service_release"));

        var atStop = Vents(bus, busStop: true, stopFrom: 12f, brakeSeconds: 3f);
        Assert.Equal(2, atStop.GetValueOrDefault("parking"));       // set, and released pulling away
        Assert.Equal(1, atStop.GetValueOrDefault("kneel"));
        Assert.Equal(2, atStop.GetValueOrDefault("door"));          // open, and shut
    }

    /// <summary>
    /// A release vents what the chambers held, and they held what the braking asked for: a gentle
    /// stop is a short puff, a hard one a longer, louder one. It used to go by how long the pedal
    /// was down, so a slow stop dumped the full volume.
    /// </summary>
    [Fact]
    public void AGentleStopsReleaseIsAPuff()
    {
        var bus = MachineRegistry.VehicleFor("school_bus_na");
        float gentle = PeakServicePressure(bus, stopFrom: 12f, brakeSeconds: 8f);    // 1.5 m/s^2
        float hard = PeakServicePressure(bus, stopFrom: 12f, brakeSeconds: 2.4f);    // 5 m/s^2
        Assert.True(gentle > 0f && hard > 0f);
        Assert.True(gentle < hard * 0.45f, $"a gentle stop vented {gentle:F0} kPa against a hard one's {hard:F0}");
        Assert.True(gentle < 0.35f * AirSystemSpec.TransitBus.CutOutKPa, $"a gentle stop vented {gentle:F0} kPa");
    }

    /// <summary>Slowing for a corner is not a brake application: nothing vents.</summary>
    [Fact]
    public void EasingOffForACornerVentsNothing()
    {
        var bus = MachineRegistry.VehicleFor("school_bus_na");
        var voice = new EngineVoiceState(bus, Rate, 5);
        voice.PlaceAtSpeed(12f);
        voice.Revive();
        var buf = new float[Block];

        for (int b = 0; b < (int)(12f * Rate / Block); b++)
        {
            float t = b * Block / (float)Rate;
            // 12 -> 9 m/s over six seconds, 0.5 m/s^2, and back up.
            voice.TargetSpeed = t < 2f ? 12f : t < 8f ? 12f - (t - 2f) * 0.5f : MathF.Min(12f, 9f + (t - 8f));
            voice.Render(buf);
        }
        Assert.Equal(0, voice.Air!.Ports["service_release"].Opened);
    }

    /// <summary>Every port that started venting during a stop-and-go, and how many times.</summary>
    private static Dictionary<string, int> Vents(VehicleProfile v, bool busStop, float stopFrom, float brakeSeconds)
    {
        var voice = new EngineVoiceState(v, Rate, 5) { ServingStop = busStop };
        voice.PlaceAtSpeed(stopFrom);
        voice.Revive();
        var buf = new float[Block];

        float stopAt = 3f + brakeSeconds;
        for (int b = 0; b < (int)((stopAt + 18f) * Rate / Block); b++)
        {
            float t = b * Block / (float)Rate;
            voice.TargetSpeed = t < 3f ? stopFrom
                              : t < stopAt ? MathF.Max(0f, stopFrom * (1f - (t - 3f) / brakeSeconds))
                              : t < stopAt + 12f ? 0f
                              : MathF.Min(stopFrom, (t - stopAt - 12f) * 2f);
            voice.Render(buf);
        }
        var counts = new Dictionary<string, int>();
        foreach (var (name, port) in voice.Air!.Ports) counts[name] = port.Opened;
        return counts;
    }

    /// <summary>The pressure behind the service release when it vented, kPa.</summary>
    private static float PeakServicePressure(VehicleProfile v, float stopFrom, float brakeSeconds)
    {
        var voice = new EngineVoiceState(v, Rate, 5);
        voice.PlaceAtSpeed(stopFrom);
        voice.Revive();
        var buf = new float[Block];
        float stopAt = 3f + brakeSeconds, peak = 0f;
        for (int b = 0; b < (int)((stopAt + 8f) * Rate / Block); b++)
        {
            float t = b * Block / (float)Rate;
            voice.TargetSpeed = t < 3f ? stopFrom
                              : t < stopAt ? MathF.Max(0f, stopFrom * (1f - (t - 3f) / brakeSeconds))
                              : t < stopAt + 3f ? 0f
                              : MathF.Min(stopFrom, (t - stopAt - 3f) * 2f);
            voice.Render(buf);
            var port = voice.Air!.Ports["service_release"];
            if (port.Venting) peak = MathF.Max(peak, port.PressureKPa);
        }
        return peak;
    }

    /// <summary>
    /// Drives a voice: rolling, then a deceleration to a dead stop, twelve seconds standing, then
    /// away again. Returns the level while standing, the level while rolling, and the energy in the
    /// door beeper's band while standing.
    /// </summary>
    private readonly record struct Run(float Arriving, float Settled, float Rolling, float ChimeBand, bool DoorsOpened, float PeakChimePa);

    private static Run Drive(VehicleProfile v, bool inside = false, bool busStop = true)
    {
        var voice = new EngineVoiceState(v, Rate, 5) { Interior = inside, ServingStop = busStop };
        voice.PlaceAtSpeed(12f);
        voice.Revive();
        var buf = new float[Block];

        double rollSum = 0, arriveSum = 0, settleSum = 0; long rollN = 0, arriveN = 0, settleN = 0;
        var chime = new System.Collections.Generic.List<float>();
        float t = 0f;
        bool doorsSeen = false;
        int blocks = (int)(26f * Rate / Block);
        for (int b = 0; b < blocks; b++)
        {
            t = b * Block / (float)Rate;
            // 0-5 s rolling, 5-8 s braking, 8-20 s stopped, 20-26 s away.
            voice.TargetSpeed = t < 5f ? 12f
                              : t < 8f ? MathF.Max(0f, 12f * (1f - (t - 5f) / 3f))
                              : t < 20f ? 0f
                              : MathF.Min(12f, (t - 20f) * 4f);
            voice.Render(buf);
            if (t is > 1f and < 4.5f) { foreach (float x in buf) { rollSum += (double)x * x; rollN++; } }
            // Arriving: the service release at the end of the braking, then the spring brakes,
            // the kneel and the doors a couple of seconds after it has stopped.
            if (t is > 7.5f and < 13f) { foreach (float x in buf) { arriveSum += (double)x * x; arriveN++; } }
            // Settled: the same bus, standing, with all of that over.
            if (t is > 15f and < 19.5f) { foreach (float x in buf) { settleSum += (double)x * x; settleN++; } }
            if (t is > 10.5f and < 19.5f) { chime.AddRange(buf); doorsSeen |= voice.DoorsOpen; }
        }

        float scale = voice.PascalsAtFullScale;
        static float Db(double sum, long n, float sc) =>
            n == 0 ? 0f : 20f * MathF.Log10(MathF.Max(1e-9f, MathF.Sqrt((float)(sum / n)) * sc) / 20e-6f);

        // The piezo's band, in SHORT windows, taking the loudest.
        //
        // Not one Goertzel over the whole stop: the beeper is PULSED, so over ten seconds the
        // carrier is present under half the time and its energy is spread into sidebands two hertz
        // apart — and a Goertzel run over four hundred thousand samples is numerically soft
        // besides. Measured that way the beeper read 33 dB while the synthesis was demonstrably
        // producing its full 0.218 Pa. A tenth of a second lands inside one beep, which is what
        // there is to measure.
        float chimeDb = 0f;
        float hz = DoorChimeSpec.TransitBus.ToneHz;
        int win = Rate / 10;
        var all = chime.ToArray();
        for (int start = 0; start + win <= all.Length; start += win / 2)
        {
            double w = 2 * Math.PI * hz / Rate, cw = 2 * Math.Cos(w);
            double g1 = 0, g2 = 0;
            for (int i = start; i < start + win; i++) { double g0 = all[i] + cw * g1 - g2; g2 = g1; g1 = g0; }
            double mag = Math.Sqrt(g1 * g1 + g2 * g2 - cw * g1 * g2) * 2.0 / win;
            float db = 20f * MathF.Log10(MathF.Max(1e-12f, (float)(mag / Math.Sqrt(2)) * scale) / 20e-6f);
            if (db > chimeDb) chimeDb = db;
        }
        return new Run(Db(arriveSum, arriveN, scale), Db(settleSum, settleN, scale),
                       Db(rollSum, rollN, scale), chimeDb, doorsSeen, voice.PeakChimePa);
    }
}
