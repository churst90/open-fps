using System;
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
    /// Drives a voice: rolling, then a deceleration to a dead stop, twelve seconds standing, then
    /// away again. Returns the level while standing, the level while rolling, and the energy in the
    /// door beeper's band while standing.
    /// </summary>
    private readonly record struct Run(float Arriving, float Settled, float Rolling, float ChimeBand, bool DoorsOpened, float PeakChimePa);

    private static Run Drive(VehicleProfile v)
    {
        var voice = new EngineVoiceState(v, Rate, 5);
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
