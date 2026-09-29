using System;
using System.Linq;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A parked vehicle switched on is heard to START: the starter turning it over, then it catching.
/// "I hear the doors, but don't hear the starter of the engine" (2026-09-29).
/// </summary>
public class StarterTests
{
    private readonly ITestOutputHelper _out;
    public StarterTests(ITestOutputHelper output) => _out = output;

    private const int Rate = 44100, Block = 441;   // 10 ms

    /// <summary>Key off for a while, then on: what the live voice does, block by block.</summary>
    private static (float OffDb, float CrankDb, float IdleDb, float CrankSeconds, float CatchRpm) KeyOn(VehicleProfile v)
    {
        var voice = new EngineVoiceState(v, Rate, 3) { TargetSpeed = 0f, Running = false };
        voice.PlaceAtSpeed(0f);
        voice.Revive();
        var buf = new float[Block];
        double Rms() => Math.Sqrt(buf.Select(x => (double)x * x).Average());
        double Db(double sum, int n) => n == 0 ? -200 : 10 * Math.Log10(sum / n + 1e-30);

        double off = 0; int nOff = 0;
        for (int b = 0; b < 400; b++) { voice.Render(buf); if (b >= 300) { off += Rms() * Rms(); nOff++; } }

        voice.Running = true;
        double crank = 0; int nCrank = 0; double idle = 0; int nIdle = 0; float catchRpm = 0;
        for (int b = 0; b < 500; b++)
        {
            voice.Render(buf);
            bool starting = voice.Engine.Starter;
            if (starting) { crank += Rms() * Rms(); nCrank++; catchRpm = voice.Engine.Rpm; }
            if (b >= 400) { idle += Rms() * Rms(); nIdle++; }
        }
        return ((float)Db(off, nOff), (float)Db(crank, nCrank), (float)Db(idle, nIdle), nCrank * Block / (float)Rate, catchRpm);
    }

    [Fact]
    public void Every_vehicle_is_heard_to_start()
    {
        foreach (var (key, make) in VehicleProfile.Presets)
        {
            var v = make();
            var r = KeyOn(v);
            _out.WriteLine($"{key,-22} off {r.OffDb,7:F1} dB  crank {r.CrankDb,7:F1} dB for {r.CrankSeconds:F2} s  idle {r.IdleDb,7:F1} dB");
            // Long enough to be heard as a start, and it does start.
            Assert.True(r.CrankSeconds >= 0.25f, $"{key} cranked for only {r.CrankSeconds:F2} s");
            Assert.True(r.CrankSeconds <= 3f, $"{key} was still cranking after {r.CrankSeconds:F2} s");
        }
    }
}

/// <summary>The bus's door beeper against its idling engine, from the seat and from the kerb, above 1 kHz.</summary>
public class BusChimeProbe
{
    private readonly ITestOutputHelper _out;
    public BusChimeProbe(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Chime_against_idle()
    {
        foreach (string key in new[] { "transit_bus", "school_bus" })
        foreach (bool inside in new[] { false, true })
        {
            var v = VehicleProfile.ByName(key);
            var voice = new EngineVoiceState(v, 44100, 5) { TargetSpeed = 0f, Interior = inside };
            voice.PlaceAtSpeed(0f);
            voice.Revive();
            var buf = new float[441];
            double idle = 0; int ni = 0; double worst = 0;
            var series = new System.Collections.Generic.List<string>();
            float a = 1f / (1f + 2f * MathF.PI * 1000f / 44100f), h1 = 0, x1 = 0, h2 = 0, y1 = 0;
            for (int b = 0; b < 1500; b++)
            {
                if (b == 300) voice.ServingStop = true;
                voice.Render(buf);
                // Above about a kilohertz, where the ear compares a beeper with a diesel: two
                // first-order high-passes at 1 kHz.
                for (int i = 0; i < buf.Length; i++)
                {
                    float x = buf[i];
                    h1 = a * (h1 + x - x1); x1 = x;
                    h2 = a * (h2 + h1 - y1); y1 = h1;
                    buf[i] = h2;
                }
                double ms = buf.Select(x => (double)x * x).Average();
                if (b >= 200 && b < 300) { idle += ms; ni++; }
                if (b >= 300) worst = Math.Max(worst, ms);
                if (b >= 300 && b % 50 == 0) series.Add($"{10 * Math.Log10(ms + 1e-30):F0}");
            }
            _out.WriteLine($"{key} {(inside ? "inside " : "outside")}: idle {10 * Math.Log10(idle / ni):F1} dB, loudest 10 ms at the stop {10 * Math.Log10(worst):F1} dB, "
                         + $"chime peak {20 * Math.Log10(voice.PeakChimePa / 20e-6 + 1e-9):F1} dB SPL; every 0.5 s: {string.Join(" ", series)}");
            // Above a kilohertz the beeper stands well clear of the idling engine, inside or out.
            Assert.True(10 * Math.Log10(worst / (idle / ni)) >= 20, $"{key} {(inside ? "inside" : "outside")}: the beeper is not clear of the engine");
        }
    }
}
