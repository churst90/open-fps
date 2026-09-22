using System;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The inside of a car: the same machine, heard through the body.
///
/// Nothing is synthesised for the interior that the outside does not already have; what changes is
/// the path. So what these hold is the path's three facts — a steel body stops the top end far more
/// than the bottom (the mass law), the seals let a little of everything in, and the cabin booms —
/// and the one source that is new from in here, the wind, which rises steeply with speed.
/// </summary>
public class CabinTests
{
    private const int Rate = 44100;
    private readonly ITestOutputHelper _o;
    public CabinTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }

    private readonly record struct Heard(float Db, float HighShareDb);

    /// <summary>Two seconds at a steady speed, after one to settle. Level in dB SPL, and how much of
    /// it is above two kilohertz, in dB against the whole.</summary>
    private static Heard Listen(string preset, float speed, bool inside)
    {
        var v = MachineRegistry.VehicleFor(preset);
        var voice = new EngineVoiceState(v, Rate, 7) { Interior = inside };
        voice.PlaceAtSpeed(speed);
        voice.TargetSpeed = speed;
        voice.Revive();
        var buf = new float[1024];
        for (int i = 0; i < Rate / 1024; i++) voice.Render(buf);

        double all = 0, high = 0; long n = 0;
        float hpA = MathF.Exp(-2f * MathF.PI * 2000f / Rate), hp = 0f, prev = 0f;
        for (int b = 0; b < 2 * Rate / 1024; b++)
        {
            voice.Render(buf);
            foreach (float s in buf)
            {
                float pa = s * voice.PascalsAtFullScale;
                hp = hpA * (hp + pa - prev); prev = pa;
                all += (double)pa * pa; high += (double)hp * hp; n++;
            }
        }
        float db = 10f * MathF.Log10((float)(all / n) / (20e-6f * 20e-6f));
        return new Heard(db, 10f * MathF.Log10((float)(high / Math.Max(1e-30, all))));
    }

    [Fact]
    public void InsideIsQuieterAndDarkerThanOutside()
    {
        var outside = Listen("i4_economy", 20f, inside: false);
        var inside = Listen("i4_economy", 20f, inside: true);
        _o.WriteLine($"72 km/h: outside at 1 m {outside.Db:F1} dB (top end {outside.HighShareDb:F1}), "
                   + $"inside {inside.Db:F1} dB (top end {inside.HighShareDb:F1})");
        Assert.True(inside.Db < outside.Db - 12f, $"inside {inside.Db:F1} against outside {outside.Db:F1}");
        Assert.True(inside.HighShareDb < outside.HighShareDb - 6f, "the body took nothing off the top");
    }

    /// <summary>
    /// A small car inside, measured UNWEIGHTED — which is not the figure usually quoted. The quoted
    /// ones are A-weighted (40-50 dBA idling, 65-70 at 110 km/h) and A-weighting throws away most of
    /// what a cabin is: its boom sits under 100 Hz, where A takes off twenty decibels and more.
    /// Unweighted, a small car is around 60 idling and the mid-seventies at motorway speed, and the
    /// rise with speed is smaller than the A-weighted one for the same reason: the wind and the tyres
    /// that grow fastest are the mid-band sources A keeps. Wide bands — this catches a path out by a
    /// factor of ten, not a tuning.
    /// </summary>
    [Fact]
    public void AHatchbackInsideIsAsLoudAsOne()
    {
        float idle = Listen("i4_economy", 0f, inside: true).Db;
        float town = Listen("i4_economy", 14f, inside: true).Db;
        float motorway = Listen("i4_economy", 30.6f, inside: true).Db;
        _o.WriteLine($"inside: idle {idle:F1}, 50 km/h {town:F1}, 110 km/h {motorway:F1} dB SPL");
        Assert.InRange(idle, 45f, 70f);
        Assert.InRange(motorway, 65f, 85f);
        Assert.True(motorway > town + 3f, "going twice as fast inside was not louder");
    }
}
