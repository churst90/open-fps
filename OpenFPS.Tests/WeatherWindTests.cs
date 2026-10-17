using System.Numerics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenFPS.Sound;
using OpenFPS.Sound.Core;
using OpenFPS.Sound.Core.Nature;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;
using OpenFPS.Physics;
using OpenFPS.Environment;

namespace OpenFPS.Tests;

/// <summary>
/// The weather's wind: the server's weather reaching the wind field every tree and fire reads, the
/// wind at the listener's ears, and /weather.
///
/// Every case builds its own <see cref="WindWeather"/> and asks the pure overloads, as each world's
/// voices do with theirs (<see cref="WorldWeather"/>).
/// </summary>
public class WeatherWindTests
{
    /// <summary>The law as a world with the shipped hearing places by.</summary>
    private static readonly OpenFPS.Sound.Hearing.HearingSettings Law = new();

    private readonly ITestOutputHelper _o;
    public WeatherWindTests(ITestOutputHelper o) { _o = o; }

    // ── The server's weather into the field ─────────────────────────────────────────────────────

    [Fact]
    public void ABroadcastBecomesTheWindItDescribes()
    {
        // Moving 3 east and 4 north: 5 m/s from the south west by south.
        var air = WindAir.FromBroadcast(new Vector3(3f, 0f, 4f), 0.5f, 100.0, 10.0, -20.0);
        Assert.Equal(5f, air.Speed, 4);
        Assert.Equal(216.87f, air.FromDegrees, 1);
        var (dx, dz) = air.Downwind;
        Assert.Equal(0.6f, dx, 4);
        Assert.Equal(0.8f, dz, 4);
        // Gustiness to turbulence intensity: steady to a gale's.
        Assert.Equal(0.18f, WindAir.TurbulenceFor(0f), 4);
        Assert.Equal(0.24f, air.Turbulence, 4);
        Assert.Equal(0.30f, WindAir.TurbulenceFor(1f), 4);
        // The pattern's travel is carried on from its anchor at the wind.
        var (te, tn) = air.TravelAt(102.0);
        Assert.Equal(16.0, te, 6);
        Assert.Equal(-12.0, tn, 6);
        // A compass wind and a velocity agree.
        var west = WindAir.FromCompass(8f, 270f, 0.2f);
        Assert.Equal(8f, west.East, 4);
        Assert.Equal(0f, west.North, 4);
    }

    [Fact]
    public void ABroadcastRampsInOverASecondAndTheFirstOneReplacesTheDefault()
    {
        var first = WindWeather.Default.Following(WindAir.FromCompass(10f, 0f, 0.2f, 50.0, 0, 0), 50.0);
        Assert.False(first.IsDefault);
        Assert.Equal(10f, first.At(50.0).Speed, 4);

        var next = first.Following(WindAir.FromCompass(4f, 0f, 0.4f, 60.0, 0, -100), 60.2);
        Assert.Equal(10f, next.At(60.2).Speed, 3);           // where it was when it arrived
        Assert.Equal(7f, next.At(60.7).Speed, 3);            // half way
        Assert.Equal(4f, next.At(61.2).Speed, 3);            // and there a second later
        Assert.Equal(0.3f, next.At(60.7).Turbulence, 3);
    }

    /// <summary>
    /// A broadcast never lands as a step: at the moment it arrives, every place on the map, near or a
    /// kilometre out, reads the same wind it read the instant before.
    /// </summary>
    [Fact]
    public void NoPlaceStepsWhenABroadcastArrives()
    {
        var sim = new Sim(e => e.PinWind(new Vector3(0f, 0f, -6f), 0.4f));
        sim.Run(30);
        var before = sim.Client;
        sim.Env.PinWind(new Vector3(9f, 0f, 3f), 0.8f);
        sim.Env.Update(1f);
        double t = sim.Clock + 0.37;
        var after = before.Following(sim.Broadcast(), t);
        foreach (var (x, z) in new[] { (0f, 0f), (-375f, 195f), (2000f, -1500f) })
        {
            float a = WindField.SpeedAt(before, x, 7f, z, t), b = WindField.SpeedAt(after, x, 7f, z, t);
            Assert.True(MathF.Abs(a - b) < 1e-3f, $"at ({x}, {z}) the wind stepped from {a} to {b}");
        }
    }

    /// <summary>
    /// Two clients that got the same broadcasts at different moments agree exactly once the ramps are
    /// over, so a gust reaches the same tree at the same time for everybody.
    /// </summary>
    [Fact]
    public void TwoClientsHearTheSameWind()
    {
        var sim = new Sim(e => e.PinWind(new Vector3(-4f, 0f, 2f), 0.4f));
        WindWeather a = WindWeather.Default, b = WindWeather.Default;
        for (int s = 0; s < 20; s++)
        {
            if (s == 8) sim.Env.PinScenario(WeatherType.Storm);
            sim.Run(1);
            var air = sim.Broadcast();
            a = a.Following(air, sim.Clock + 0.03);
            b = b.Following(air, sim.Clock + 0.21);
        }
        double t = sim.Clock + 1.5;
        foreach (var (x, z) in new[] { (0f, 0f), (-375f, 195f), (1200f, 800f) })
            Assert.Equal(WindField.SpeedAt(a, x, 7f, z, t), WindField.SpeedAt(b, x, 7f, z, t), 4);
    }

    /// <summary>
    /// The wind veers and strengthens without the gusts racing: the eddy pattern is carried by how far the
    /// air has travelled. Fixed to a direction, it swung round the map's origin as the wind turned, and a
    /// tree 2 km out heard minutes of gusts in seconds.
    /// </summary>
    [Fact]
    public void AVeeringWindDoesNotRaceTheGustsFarFromTheOrigin()
    {
        double Busy(Action<WorldEnvironmentSystem> set, Action<WorldEnvironmentSystem>? then)
        {
            var sim = new Sim(set);
            sim.Run(60);
            var client = sim.Client;
            double sum = 0; int n = 0;
            float last = float.NaN;
            for (int s = 0; s < 40; s++)
            {
                if (s == 0 && then != null) then(sim.Env);
                double start = sim.Clock;
                sim.Run(1);
                client = client.Following(sim.Broadcast(), sim.Clock);
                for (double t = start; t < sim.Clock; t += 0.05)
                {
                    float g = WindField.Gust(client, 1800f, -900f, t);
                    if (!float.IsNaN(last)) { sum += (g - last) * (g - last); n++; }
                    last = g;
                }
            }
            return Math.Sqrt(sum / n) / 0.05;
        }
        // Ten m/s from the north, steady; and the same wind veering to the east and doubling.
        double steady = Busy(e => e.PinWind(new Vector3(0f, 0f, -10f), 0.4f), null);
        double veering = Busy(e => e.PinWind(new Vector3(0f, 0f, -10f), 0.4f), e => e.PinWind(new Vector3(-20f, 0f, 0f), 0.4f));
        _o.WriteLine($"gust rate of change 2 km out: steady {steady:F3}/s, veering and doubling {veering:F3}/s");
        // Twice the wind speed, twice the gust rate, and no faster.
        Assert.True(veering < steady * 2.5, $"the gusts raced: {veering:F3}/s against {steady:F3}/s steady");
    }

    [Fact]
    public void TheServerCarriesThePatternAtTheWind()
    {
        var env = new WorldEnvironmentSystem(new Random(1)) { FrontProbabilityPerTick = 0 };
        env.PinWind(new Vector3(6f, 0f, -2f), 0.3f);
        for (int i = 0; i < 60 * 60; i++) env.Update(1f / 60f);
        var (e0, n0) = env.WindTravel;
        for (int i = 0; i < 60 * 10; i++) env.Update(1f / 60f);
        var (e1, n1) = env.WindTravel;
        Assert.Equal(60.0, e1 - e0, 1);
        Assert.Equal(-20.0, n1 - n0, 1);
        // A dead calm is still carried, slowly.
        env.PinWind(Vector3.Zero, 0f);
        for (int i = 0; i < 60 * 120; i++) env.Update(1f / 60f);
        var (e2, n2) = env.WindTravel;
        for (int i = 0; i < 60 * 10; i++) env.Update(1f / 60f);
        var (e3, n3) = env.WindTravel;
        double carried = Math.Sqrt((e3 - e2) * (e3 - e2) + (n3 - n2) * (n3 - n2));
        Assert.InRange(carried, 4.9, 5.1);
    }

    [Fact]
    public void SetByHandTheWeatherArrivesInSecondsNotAMinute()
    {
        var env = new WorldEnvironmentSystem(new Random(1)) { FrontProbabilityPerTick = 0 };
        env.PinWind(new Vector3(1f, 0f, 0f), 0.1f);
        for (int i = 0; i < 60 * 60; i++) env.Update(1f / 60f);
        env.PinScenario(WeatherType.Storm);
        Assert.True(env.Pinned);
        for (int i = 0; i < 60 * 15; i++) env.Update(1f / 60f);
        float speed = new Vector2(env.GetCurrentState().WindVelocity.X, env.GetCurrentState().WindVelocity.Z).Length();
        Assert.True(speed > 15f, $"fifteen seconds after /weather storm the wind is only {speed:F1} m/s");
        env.Unpin();
        Assert.False(env.Pinned);
    }

    // ── The ears ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheLevelFollowsThePublishedLaw()
    {
        // Seidman's 84.9 dB at 10 mph is a microphone in the flow; the ear canal is 14 to 19 dB under that.
        Assert.Equal(85f + EarWind.OpenEarDb, EarWind.GrazingDb(4.47f), 2);
        Assert.InRange(EarWind.OpenEarDb, -19f, -14f);
        // 43 dB a decade: about 13 a doubling, between flow-noise theory's 12 and the measured 13.7.
        Assert.Equal(12.94f, EarWind.GrazingDb(8.94f) - EarWind.GrazingDb(4.47f), 1);
        // Seidman's 60 mph point, 120 dB at the worst microphone, is 101 to 106 at the eardrum.
        Assert.InRange(EarWind.GrazingDb(26.8f) + EarWind.LeeDb - EarWind.OpenEarDb, 117f, 121f);
        // A real walk, 1.4 m/s, is under 50 dB, nearly all of it under 100 Hz: about nothing.
        Assert.True(EarWind.GrazingDb(1.4f) < 50f);
        // The knee rides the speed.
        Assert.Equal(300f, EarWind.KneeHz(5f), 1);
        Assert.True(EarWind.KneeHz(15f) > EarWind.KneeHz(5f) * 2.9f);
    }

    [Fact]
    public void TheEarFacingTheWindGetsLessAndTheOneInItsWakeMore()
    {
        // Facing north, wind from the north: both ears grazing, alike.
        var facing = EarWind.Ears(8f, new Vector2(0f, -8f), 0f);
        Assert.Equal(0f, facing.FromDegrees, 2);
        Assert.Equal(facing.LeftDb, facing.RightDb, 3);
        // Wind from the east while facing north: from the right. The right ear takes it straight in.
        var right = EarWind.Ears(8f, new Vector2(-8f, 0f), 0f);
        Assert.Equal(90f, right.FromDegrees, 2);
        Assert.Equal(facing.RightDb - EarWind.WindwardDb, right.RightDb, 3);
        Assert.Equal(facing.LeftDb + EarWind.LeeDb, right.LeftDb, 3);
        Assert.Equal(9.5f, right.LeftDb - right.RightDb, 3);
        // Turn to face east: the same wind is now in your face.
        var turned = EarWind.Ears(8f, new Vector2(-8f, 0f), 90f);
        Assert.Equal(0f, turned.FromDegrees, 2);
        Assert.Equal(turned.LeftDb, turned.RightDb, 3);
        // Back to it: both ears a little louder, still alike.
        var back = EarWind.Ears(8f, new Vector2(0f, -8f), 180f);
        Assert.Equal(180f, back.FromDegrees, 2);
        Assert.Equal(facing.LeftDb + EarWind.FromBehindDb, back.LeftDb, 3);
        Assert.Equal(back.LeftDb, back.RightDb, 3);
        // Facing the wind is louder in sum than taking it side-on (Korhonen 2021).
        double Energy(EarWindAtEars e) => Math.Pow(10, e.LeftDb / 10) + Math.Pow(10, e.RightDb / 10);
        Assert.True(Energy(facing) > Energy(right));
    }

    [Fact]
    public void WhatYouFeelIsTheAirLessYourOwnMovement()
    {
        var north = new Vector2(0f, 5f);
        // The game's walk (4.5 m/s, a jog) counts as the real walk it stands for, 1.4 m/s.
        float walk = PhysicsConstants.WalkSpeed;
        Assert.Equal(1.4f, walk * EarWind.OnFootShare, 3);
        // Walking north into air moving south at 4.5: 5.9 past the face.
        var into = EarWind.Relative(new Vector2(0f, -4.5f), new EarWindListener(default, 1.7f, new Vector2(0f, walk), 0f, 1f));
        Assert.Equal(5.9f, into.Length(), 3);
        // Walking with air moving north at 1.4: still air at the ears.
        var with = EarWind.Relative(new Vector2(0f, 1.4f), new EarWindListener(default, 1.7f, new Vector2(0f, walk), 0f, 1f));
        Assert.True(with.Length() < 1e-3f);
        // Indoors nothing reaches the ears: not the weather, and not your own running.
        var indoors = EarWind.Relative(north, new EarWindListener(default, 1.7f, Vector2.Zero, 0f, 0f));
        Assert.True(indoors.Length() < 1e-4f);
        var running = EarWind.Relative(north, new EarWindListener(default, 1.7f, new Vector2(3f, 0f), 0f, 0f));
        Assert.True(running.Length() < 1e-4f);
        // Outdoors in still air your own movement is all of it, and walking is faint: under 50 dB at
        // the eardrum, nearly all of it under 100 Hz. Sprinting is under 57.
        var jog = EarWind.Relative(Vector2.Zero, new EarWindListener(default, 1.7f, new Vector2(walk, 0f), 0f, 1f));
        Assert.Equal(1.4f, jog.Length(), 3);
        Assert.True(EarWind.GrazingDb(jog.Length()) < 50f);
        var sprint = EarWind.Relative(Vector2.Zero, new EarWindListener(default, 1.7f, new Vector2(PhysicsConstants.SprintSpeed, 0f), 0f, 1f));
        Assert.True(EarWind.GrazingDb(sprint.Length()) < 57f);
        // A street between tall buildings: what its enclosure lets through.
        var street = EarWind.Relative(north, new EarWindListener(default, 1.7f, Vector2.Zero, 0f, 0.6f));
        Assert.Equal(3f, street.Length(), 3);
        // In a car at 20 m/s with the windows shut, nothing; right down, a quarter of it.
        var car = new EarWindListener(default, 1.3f, new Vector2(20f, 0f), 90f, 1f, EarCover.Cabin, 0f);
        Assert.True(EarWind.Relative(Vector2.Zero, car).Length() < 1e-4f);
        Assert.Equal(5f, EarWind.Relative(Vector2.Zero, car with { WindowsOpen = 1f }).Length(), 3);
        // A helmet takes twelve decibels off both ears.
        var bare = EarWind.Ears(20f, new Vector2(-20f, 0f), 90f);
        var helmet = EarWind.Ears(20f, new Vector2(-20f, 0f), 90f, EarCover.Helmet);
        Assert.Equal(bare.LeftDb + EarWind.HelmetDb, helmet.LeftDb, 3);
        Assert.Equal(bare.DeclaredDb + EarWind.HelmetDb, helmet.DeclaredDb, 3);
    }

    [Fact]
    public void TheWeathersWindReachesTheEarsFromWhereItBlows()
    {
        // Ten m/s from the north at ten metres, steady; standing still facing east.
        var weather = WindWeather.Steady(WindAir.FromCompass(10f, 0f, 0f));
        var l = new EarWindListener(new Vector3(-300f, 1.7f, 200f), 1.7f, Vector2.Zero, 90f, 1f);
        var ears = EarWind.Hear(weather, l, 1000.0);
        // The log law at 1.7 m, and from the left.
        Assert.Equal(10f * WindField.HeightShare(1.7f), ears.MeanSpeed, 3);
        Assert.Equal(270f, ears.FromDegrees, 1);
        Assert.True(ears.LeftDb < ears.RightDb - 9f);
        // Indoors, standing still: nothing.
        var inside = EarWind.Hear(weather, l with { Exposure = 0f }, 1000.0);
        Assert.True(inside.Speed < EarWind.StillAir);
    }

    [Fact]
    public void TheLevelIsPlacedByTheLawAndTheEarsKeepTheirDifferenceUnderTheCeiling()
    {
        float before = Law.Compression;
        // The law places the declared level, the shared headroom under it like every synthesized voice.
        float placed = EarWind.PlacedDb(85f, Law);
        Assert.Equal((85f - Law.RenderCeilingDb) * Law.Compression - VehicleProfile.PeakHeadroomDb, placed, 3);
        // A gale is held under the ceiling...
        var gale = EarWind.Ears(25f, new Vector2(-25f, 0f), 0f);
        float l = EarWind.RenderedDb(gale.DeclaredDb, gale.LeftDb, Law), r = EarWind.RenderedDb(gale.DeclaredDb, gale.RightDb, Law);
        Assert.True(EarWind.HeldDb(EarWind.PlacedDb(gale.DeclaredDb, Law)) < EarWind.CeilingDb);
        // Side-on its two ears are still 9.5 dB apart.
        Assert.Equal(9.5f, l - r, 2);
        // Below the knee the ceiling does nothing.
        Assert.Equal(-40f, EarWind.HeldDb(-40f), 4);
        Assert.Equal(before, Law.Compression);
    }

    [Fact]
    public void TheSynthPlaysEachEarAtItsLevelWithUnrelatedNoise()
    {
        var ears = EarWind.Ears(6f, new Vector2(-6f, 0f), 0f);
        var s = new EarWindSynth(44100, 3);
        int n = 44100 * 6;
        var l = new float[n]; var r = new float[n];
        for (int i = 0; i < 44100; i += 1024) { s.Control(ears); s.Render(new float[1024], new float[1024]); }
        for (int i = 0; i < n; i += 1024)
        {
            int m = Math.Min(1024, n - i);
            s.Control(ears);
            s.Render(l.AsSpan(i, m), r.AsSpan(i, m));
        }
        double el = 0, er = 0, lr = 0;
        for (int i = 0; i < n; i++) { el += l[i] * (double)l[i]; er += r[i] * (double)r[i]; lr += l[i] * (double)r[i]; }
        double dl = 10 * Math.Log10(el / n), dr = 10 * Math.Log10(er / n);
        _o.WriteLine($"left {dl:F2} dBFS (law {EarWind.RenderedDb(ears.DeclaredDb, ears.LeftDb, Law):F2}), right {dr:F2} (law {EarWind.RenderedDb(ears.DeclaredDb, ears.RightDb, Law):F2}), correlation {lr / Math.Sqrt(el * er):F3}");
        Assert.InRange(dl, EarWind.RenderedDb(ears.DeclaredDb, ears.LeftDb, Law) - 1.5, EarWind.RenderedDb(ears.DeclaredDb, ears.LeftDb, Law) + 1.5);
        Assert.InRange(dr, EarWind.RenderedDb(ears.DeclaredDb, ears.RightDb, Law) - 1.5, EarWind.RenderedDb(ears.DeclaredDb, ears.RightDb, Law) + 1.5);
        Assert.InRange(lr / Math.Sqrt(el * er), -0.1, 0.1);
        // Still air makes nothing.
        s.Control(EarWind.Ears(0f, Vector2.Zero, 0f));
        for (int k = 0; k < 100; k++) s.Render(l.AsSpan(0, 1024), r.AsSpan(0, 1024));
        Assert.True(s.Silent);
    }

    // ── /weather ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(new[] { "8", "northwest" }, 8f, 315f, -1f)]
    [InlineData(new[] { "8", "north", "west", "gusty" }, 8f, 315f, 0.4f)]
    [InlineData(new[] { "12", "270", "very", "gusty" }, 12f, 270f, 0.8f)]
    [InlineData(new[] { "3.5", "from", "the", "south" }, 3.5f, 180f, -1f)]
    [InlineData(new[] { "5", "SW", "steady" }, 5f, 225f, 0.1f)]
    [InlineData(new[] { "calm" }, 0f, -1f, -1f)]
    public void WindIsReadTheWayItIsSaid(string[] args, float speed, float from, float gust)
    {
        var env = new WorldEnvironmentSystem(new Random(1)) { FrontProbabilityPerTick = 0 };
        Assert.True(CommandHandler.TryParseWind(args, env, out var v, out float g, out string? error), error);
        Assert.Equal(speed, new Vector2(v.X, v.Z).Length(), 3);
        if (from >= 0) Assert.Equal(from, new WindAir(v.X, v.Z, 0f, 0, 0, 0).FromDegrees, 1);
        Assert.Equal(gust >= 0 ? gust : env.TargetGustiness, g, 3);
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("8 sideways")]
    [InlineData("99 north")]
    [InlineData("-3 north")]
    public void WindThatCannotBeReadSaysWhy(string typed)
    {
        var env = new WorldEnvironmentSystem(new Random(1));
        Assert.False(CommandHandler.TryParseWind(typed.Split(' '), env, out _, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void WindIsSaidPlainly()
    {
        Assert.Equal("Wind 8 metres a second from the north west, gusty.",
                     CommandHandler.DescribeWind(new Vector3(5.657f, 0f, -5.657f), 0.4f));
        Assert.Equal("Wind 1.5 metres a second from the south, steady.", CommandHandler.DescribeWind(new Vector3(0f, 0f, 1.5f), 0.1f));
        Assert.Equal("Calm.", CommandHandler.DescribeWind(new Vector3(0.1f, 0f, 0f), 0.9f));
        Assert.Equal("Wind 18 metres a second from the north west, very gusty.", CommandHandler.DescribeWind(new Vector3(15f, 0f, -10f), 0.8f));
    }

    [Fact]
    public void WeatherCommandSetsTellsAndSendsTheWeather()
    {
        using var rig = new CommandRig();
        var dev = rig.Online("dev", 1, UserRole.Dev);
        var player = rig.Online("player", 2, UserRole.Player);

        Assert.Equal("You do not have permission to execute this command.", rig.Run(player, "weather", "storm"));
        Assert.Empty(rig.Sent);
        Assert.False(rig.Server.WorldEnvironment.Pinned && rig.Server.WorldEnvironment.CurrentScenario == WeatherType.Storm);

        string said = rig.Run(dev, "weather", "storm");
        Assert.Equal("Storm coming in. Wind 18 metres a second from the north west, very gusty.", said);
        Assert.Equal(WeatherType.Storm, rig.Server.WorldEnvironment.CurrentScenario);
        Assert.True(rig.Server.WorldEnvironment.Pinned);
        // Everybody on the map is sent the weather at once, not at the next second.
        Assert.Contains(rig.Sent, s => s.To.Username == "player" && s.What is WorldStateUpdate);

        Assert.Equal("Wind 8 metres a second from the north west, gusty.", rig.Run(dev, "weather", "wind", "8", "north", "west", "gusty"));
        var target = rig.Server.WorldEnvironment.TargetWind;
        Assert.Equal(8f, new Vector2(target.X, target.Z).Length(), 3);

        string now = rig.Run(dev, "weather");
        Assert.StartsWith("Storm. Wind ", now);
        Assert.EndsWith("Held until /weather auto.", now);

        Assert.Equal("The weather changes on its own again.", rig.Run(dev, "weather", "auto"));
        Assert.False(rig.Server.WorldEnvironment.Pinned);
        Assert.StartsWith("Usage: /weather", rig.Run(dev, "weather", "sunny"));
    }

    [Fact]
    public void AFrontThatClearsSaysSoWhileItsRainIsStillFallingAndTheRainStops()
    {
        // Cody, 2026-10-08: /weather said "Clear" with moderate rain falling. A front that clears leaves
        // its rain easing off for a minute or two, and the easing never reached nothing.
        using var rig = new CommandRig();
        var dev = rig.Online("dev", 1, UserRole.Dev);
        var env = rig.Server.WorldEnvironment;
        env.FrontProbabilityPerTick = 0;
        env.SetDate(14f, 172);                       // a summer afternoon: rain, not snow
        env.SetScenario(WeatherType.Rain);
        Assert.StartsWith("Rain coming in. ", rig.Run(dev, "weather"));
        for (int i = 0; i < 30 * 600; i++) env.Update(1f / 30f);
        string raining = rig.Run(dev, "weather");
        Assert.StartsWith("Rain. ", raining);
        Assert.Contains("millimetres an hour", raining);

        env.SetScenario(WeatherType.Clear);
        env.Update(1f / 30f);
        string clearing = rig.Run(dev, "weather");
        _o.WriteLine(clearing);
        Assert.StartsWith("Clearing. ", clearing);
        Assert.Contains("millimetres an hour", clearing);

        for (int i = 0; i < 30 * 600; i++) env.Update(1f / 30f);
        Assert.Equal(0f, env.GetCurrentState().PrecipitationIntensity);
        Assert.False(env.PrecipitationFor(env.GetCurrentState()).Falling);
        string clear = rig.Run(dev, "weather");
        Assert.StartsWith("Clear. ", clear);
        Assert.DoesNotContain("millimetres", clear);
    }

    [Fact]
    public void WeatherRainTakesARateADropSizeAndARadarReading()
    {
        using var rig = new CommandRig();
        var dev = rig.Online("dev", 1, UserRole.Dev);
        var env = rig.Server.WorldEnvironment;

        string said = rig.Run(dev, "weather", "rain", "heavy");
        _o.WriteLine(said);
        Assert.StartsWith("Rain, heavy, 25 millimetres an hour, drops ", said);
        Assert.EndsWith("on the radar.", said);
        Assert.Equal(25f, env.HeldRainRate);
        Assert.Equal(WeatherType.Rain, env.CurrentScenario);
        Assert.Contains(rig.Sent, s => s.What is WorldStateUpdate);
        for (int i = 0; i < 60 * 90; i++) env.Update(1f / 60f);
        Assert.InRange(env.RainRate(env.GetCurrentState()), 22f, 27f);

        Assert.StartsWith("Rain, heavy, 12 millimetres an hour", rig.Run(dev, "weather", "rain", "12"));
        Assert.Contains("drops 3 millimetres", rig.Run(dev, "weather", "rain", "heavy", "drops", "3", "mm"));
        Assert.Equal(3f, env.HeldPrecipitation!.Value.MedianDropMm);
        Assert.StartsWith("Drizzle, 0.3 millimetres an hour, drops 0.", rig.Run(dev, "weather", "drizzle"));
        Assert.Contains("pale green", rig.Run(dev, "weather", "drizzle"));
        Assert.Contains("orange", rig.Run(dev, "weather", "rain", "45", "dBZ"));
        Assert.StartsWith("Rain, extreme, 70", rig.Run(dev, "weather", "rain", "extreme"));
        Assert.StartsWith("Freezing rain, moderate", rig.Run(dev, "weather", "freezing", "rain"));
        Assert.StartsWith("Sleet, 3 millimetres an hour", rig.Run(dev, "weather", "sleet"));
        Assert.StartsWith("Snow, 4 millimetres of water an hour, blue", rig.Run(dev, "weather", "snow", "heavy"));
        Assert.Null(env.HeldRainRate);
        Assert.Equal(PrecipitationKind.Snow, env.HeldPrecipitation!.Value.Kind);
        Assert.StartsWith("Hail, 44 millimetres, with heavy rain", rig.Run(dev, "weather", "hail", "golf"));
        Assert.StartsWith("Hail, 70 millimetres", rig.Run(dev, "weather", "hail", "baseball"));
        Assert.StartsWith("I do not know lots", rig.Run(dev, "weather", "rain", "lots"));
        Assert.StartsWith("Say a rate up to 60", rig.Run(dev, "weather", "rain", "400"));

        // Plain rain and a storm are rain at their fronts' own rates, whatever the season.
        Assert.StartsWith("Rain coming in.", rig.Run(dev, "weather", "rain"));
        Assert.InRange(env.HeldRainRate ?? 0f, 6f, 8f);
        rig.Run(dev, "weather", "storm");
        Assert.InRange(env.HeldRainRate ?? 0f, 55f, 61f);
        rig.Run(dev, "weather", "snow");
        Assert.Null(env.HeldRainRate);
        Assert.Contains("Held until /weather auto.", rig.Run(dev, "weather"));
    }

    /// <summary>The message carries the wind's travel, appended after the fields that were there.</summary>
    [Fact]
    public void TheBroadcastCarriesTheWindsTravel()
    {
        using var rig = new CommandRig();
        rig.Online("player", 2, UserRole.Player);
        for (int i = 0; i < 600; i++) rig.Server.WorldEnvironment.Update(1f / 60f);
        rig.Server.BroadcastEnvironment();
        var update = Assert.IsType<WorldStateUpdate>(Assert.Single(rig.Sent).What);
        Assert.True(update.WindClock > 0);
        var (e, n) = rig.Server.WorldEnvironment.WindTravel;
        Assert.Equal(e, update.WindTravelEast, 6);
        Assert.Equal(n, update.WindTravelNorth, 6);
        // Round trip on the wire, and the old fields where they were.
        var bytes = MemoryPack.MemoryPackSerializer.Serialize<IMessage>(update);
        var back = Assert.IsType<WorldStateUpdate>(MemoryPack.MemoryPackSerializer.Deserialize<IMessage>(bytes));
        Assert.Equal(update.WindTravelEast, back.WindTravelEast);
        Assert.Equal(update.WindVelocity, back.WindVelocity);
        Assert.Equal(update.PrecipitationIntensity, back.PrecipitationIntensity);
    }

    // ── Where the wind blows from ──────────────────────────────────────────────────────────────

    /// <summary>
    /// At a place the wind swings either side of where the weather says it blows from, by the field's own eddies: the
    /// sideways push is four fifths of the lengthwise turbulence (Panofsky and Dutton), about the mean, and not the gust.
    /// </summary>
    [Fact]
    public void TheWindSwingsAcrossItsMeanByTheFieldsOwnEddies()
    {
        var weather = WindWeather.Steady(5f, 250f, 0.25f);
        var (dx, dz) = weather.At(0).Downwind;
        double along = 0, across = 0, across2 = 0, gustAcross = 0, gust2 = 0, east = 0, north = 0;
        int n = 0;
        var rng = new Random(3);
        for (int i = 0; i < 40000; i++)
        {
            float x = (float)(rng.NextDouble() * 4000 - 2000), z = (float)(rng.NextDouble() * 4000 - 2000);
            double t = rng.NextDouble() * 20000;
            var v = WindField.VelocityAt(weather, x, 10f, z, t);
            float a = v.X * dx + v.Y * dz, c = -v.X * dz + v.Y * dx;
            float g = WindField.Gust(weather, x, z, t);
            along += a; across += c; across2 += c * c; gustAcross += g * c; gust2 += g * g;
            east += v.X; north += v.Y; n++;
        }
        double meanAlong = along / n, meanAcross = across / n, sdAcross = Math.Sqrt(across2 / n - meanAcross * meanAcross);
        double from = (Math.Atan2(-east, -north) * 180 / Math.PI + 360) % 360;
        double r = gustAcross / Math.Sqrt(gust2 * across2);
        _o.WriteLine($"along {meanAlong:F2} m/s, across mean {meanAcross:F3} sd {sdAcross:F3} ({sdAcross / meanAlong:F3} of the mean, " +
                     $"{Math.Atan(sdAcross / meanAlong) * 180 / Math.PI:F1} degrees); mean from {from:F1}; gust and swing correlate {r:F3}");
        Assert.InRange(sdAcross / meanAlong, 0.8 * 0.25 * 0.8, 0.8 * 0.25 * 1.2);
        Assert.InRange(meanAcross / meanAlong, -0.02, 0.02);
        Assert.InRange(from, 248.5, 251.5);
        Assert.InRange(r, -0.1, 0.1);
        // The speed is the vector's: what a leaf feels whichever way it is pushed.
        Assert.Equal(WindField.VelocityAt(weather, 30f, 7f, -12f, 500.0).Length(), WindField.SpeedAt(weather, 30f, 7f, -12f, 500.0), 5);
    }

    /// <summary>The swing is carried by the wind as the gust is: a place downwind swings as a place upwind did.</summary>
    [Fact]
    public void TheSwingTravelsDownwind()
    {
        var weather = WindWeather.Steady(4.5f, 250f, 0.25f);
        var (dx, dz) = weather.At(0).Downwind;
        const float metres = 90f;
        float lag = metres / 4.5f;
        double carried = 0, aa = 0, cc = 0;
        for (double t = 0; t < 6000; t += 0.5)
        {
            float a = WindField.Swing(weather, 0f, 0f, t);
            float c = WindField.Swing(weather, dx * metres, dz * metres, t + lag);
            carried += a * c; aa += a * a; cc += c * c;
        }
        double rCarried = carried / Math.Sqrt(aa * cc);
        _o.WriteLine($"swing 90 m downwind {lag:F0} s later correlates {rCarried:F3}");
        Assert.True(rCarried > 0.98, $"{rCarried:F3}");
    }

    /// <summary>
    /// In still air nothing blows and nothing swings: no light-air meander is laid over the field. Without turbulence
    /// the wind blows exactly from where the weather says.
    /// </summary>
    [Fact]
    public void StillAirIsStillAndASteadyWindDoesNotSwing()
    {
        var still = WindWeather.Steady(0f, 250f, 0.3f);
        var smooth = WindWeather.Steady(6f, 300f, 0f);
        for (double t = 100; t < 5000; t += 37.1)
            foreach (var (x, z) in new[] { (0f, 0f), (-375f, 195f), (1800f, -900f) })
            {
                Assert.Equal(Vector2.Zero, WindField.VelocityAt(still, x, 1.7f, z, t));
                var ears = EarWind.Hear(still, new EarWindListener(new Vector3(x, 1.7f, z), 1.7f, Vector2.Zero, 0f, 1f), t);
                Assert.True(ears.Speed < EarWind.StillAir, $"still air at the ears blew {ears.Speed} m/s");
                var v = WindField.VelocityAt(smooth, x, 10f, z, t);
                Assert.Equal(300f, new WindAir(v.X, v.Y, 0f, 0, 0, 0).FromDegrees, 2);
                Assert.Equal(6f, v.Length(), 3);
            }
    }

    /// <summary>
    /// The ears hear the wind come from where the field says it comes from at the head now: as it swings, the wind
    /// moves from ear to ear. Read off the mean direction, it never did.
    /// </summary>
    [Fact]
    public void TheEarsHearTheWindComeFromWhereItIsComingFromNow()
    {
        var weather = WindWeather.Steady(8f, 0f, 0.25f);
        var head = new Vector3(-375f, 1.7f, 195f);
        var l = new EarWindListener(head, 1.7f, Vector2.Zero, 90f, 1f);
        double sum = 0, sq = 0; int n = 0;
        for (double t = 1000; t < 4600; t += 0.5)
        {
            var ears = EarWind.Hear(weather, l, t);
            var v = WindField.VelocityAt(weather, head.X, 1.7f, head.Z, t);
            float from = (new WindAir(v.X, v.Y, 0f, 0, 0, 0).FromDegrees - 90f + 360f) % 360f;
            Assert.InRange(MathF.Abs(((from - ears.FromDegrees) % 360f + 540f) % 360f - 180f), 0f, 0.05f);
            double d = ears.FromDegrees - 270.0;
            sum += d; sq += d * d; n++;
        }
        double sd = Math.Sqrt(sq / n - (sum / n) * (sum / n));
        _o.WriteLine($"from the left (270 re the face) by {sum / n:F1}, swinging {sd:F1} degrees");
        Assert.InRange(sum / n, -3, 3);
        Assert.InRange(sd, 6, 20);
    }

    /// <summary>
    /// The way to a far sound feels the same swing: a way straight across the mean wind has the wind blowing along it
    /// now one way and now the other, so its bending comes and goes about none.
    /// </summary>
    [Fact]
    public void AWayAcrossTheWindIsBentByItsSwing()
    {
        var w = new WorldWeather { Wind = WindWeather.Steady(8f, 270f, 0.25f), Layers = AirLayers.Neutral() };
        // From the west, so a way from south to north lies across it.
        var source = new Vector3(-375f, 0.5f, 0f);
        var ear = new Vector3(-375f, 1.7f, 300f);
        // And the same length of way downwind, west to east.
        var downwindEar = new Vector3(-75f, 1.7f, 0f);
        double sum = 0, sq = 0, down = 0; int n = 0;
        for (double t = 1000; t < 4600; t += 1.0)
        {
            var bent = Bending.Along(w, source, ear, 0f, false, t, 343f);
            sum += bent.Curvature; sq += bent.Curvature * (double)bent.Curvature; n++;
            down += Bending.Along(w, source, downwindEar, 0f, false, t, 343f).Curvature;
        }
        double mean = sum / n, sd = Math.Sqrt(sq / n - mean * mean);
        _o.WriteLine($"curvature across the wind: mean {mean:E2}, sd {sd:E2} per metre; downwind {down / n:E2}");
        // A fifth of the downwind bending, either way: the swing is four fifths of a quarter of the wind.
        Assert.True(sd > 0.1 * Math.Abs(down / n), $"the way across the wind hardly bent ({sd:E2} against {down / n:E2} downwind): the refraction read the mean direction");
        Assert.True(Math.Abs(mean) < sd * 0.5, $"{mean:E2} against {sd:E2}");
    }

    /// <summary>
    /// The server's fire spread and a client's sound read the same wind at the same place and moment, swing and all:
    /// the spread carries the pattern as far as the server has (it read a pattern standing still at the origin).
    /// </summary>
    [Fact]
    public void TheServerAndEveryClientFeelTheSameWindAtTheSamePlace()
    {
        var sim = new Sim(e => e.PinScenario(WeatherType.Storm));
        WindWeather a = WindWeather.Default, b = WindWeather.Default;
        for (int s = 0; s < 30; s++)
        {
            sim.Run(1);
            var air = sim.Broadcast();
            a = a.Following(air, sim.Clock);
            b = b.Following(air, sim.Clock + 0.4);
        }
        double t = sim.Clock + 1.5;
        var server = FireSystem.WindFor(sim.Env.GetCurrentState(), sim.Clock, sim.Env.WindTravel);
        foreach (var (x, z) in new[] { (0f, 0f), (-375f, 195f), (1800f, -900f) })
        {
            var va = WindField.VelocityAt(a, x, 7f, z, t);
            Assert.Equal(va, WindField.VelocityAt(b, x, 7f, z, t));
            var vs = WindField.VelocityAt(server, x, 7f, z, t);
            Assert.True(Vector2.Distance(va, vs) < 1e-3f, $"at ({x}, {z}) the server's fire felt {vs}, the clients {va}");
        }
    }

    /// <summary>
    /// Each front's wind blows from round the prevailing wind, turned as the front turns it, and turns with the day:
    /// veered most at two in the afternoon, backed most at two in the morning. Nothing about it is drawn: two worlds with
    /// different luck have the same direction at the same front and hour.
    /// </summary>
    [Theory]
    [InlineData(WeatherType.Clear, 8f, 250f)]
    [InlineData(WeatherType.Clear, 14f, 260f)]
    [InlineData(WeatherType.Clear, 2f, 240f)]
    [InlineData(WeatherType.Rain, 8f, 190f)]
    [InlineData(WeatherType.Storm, 8f, 295f)]
    [InlineData(WeatherType.Storm, 20f, 295f)]
    [InlineData(WeatherType.Snow, 14f, 320f)]
    public void AFrontsWindComesFromRoundThePrevailingWindAndTurnsWithTheDay(WeatherType front, float hour, float from)
    {
        foreach (int seed in new[] { 1, 2, 77 })
        {
            var env = new WorldEnvironmentSystem(new Random(seed)) { FrontProbabilityPerTick = 0 };
            env.SetDate(hour, 172);
            env.PinScenario(front);
            for (int i = 0; i < 60 * 20; i++) env.Update(1f / 60f);
            var v = env.GetCurrentState().WindVelocity;
            float now = new WindAir(v.X, v.Z, 0f, 0, 0, 0).FromDegrees;
            _o.WriteLine($"{front} at {env.GetCurrentState().GameTime:F2} h, seed {seed}: from {now:F1}");
            // Twenty seconds is twenty game minutes: the day's turn has moved by under a degree since the hour.
            Assert.InRange(now, from - 1.5f, from + 1.5f);
        }
    }

    /// <summary>
    /// Over a game hour with the weather held, the direction moves only by the day's turn: a few degrees, smoothly, the
    /// same in every world; and a wind set by hand does not move at all.
    /// </summary>
    [Fact]
    public void OverAnHourTheWindTurnsOnlyAsTheDayTurnsIt()
    {
        float[] Hour(int seed, Action<WorldEnvironmentSystem> set)
        {
            var env = new WorldEnvironmentSystem(new Random(seed)) { FrontProbabilityPerTick = 0 };
            env.SetDate(9f, 100);
            set(env);
            for (int i = 0; i < 60 * 60; i++) env.Update(1f / 60f);
            var froms = new List<float>();
            // A game hour is a minute: a reading every game minute.
            for (int m = 0; m <= 60; m++)
            {
                var v = env.GetCurrentState().WindVelocity;
                froms.Add(new WindAir(v.X, v.Z, 0f, 0, 0, 0).FromDegrees);
                for (int i = 0; i < 60; i++) env.Update(1f / 60f);
            }
            return froms.ToArray();
        }
        var one = Hour(1, e => e.PinScenario(WeatherType.Clear));
        var other = Hour(5, e => e.PinScenario(WeatherType.Clear));
        _o.WriteLine($"from {one[0]:F2} to {one[^1]:F2} over the hour from ten");
        for (int m = 0; m < one.Length; m++) Assert.Equal(one[m], other[m], 2);
        for (int m = 1; m < one.Length; m++) Assert.InRange(one[m] - one[m - 1], 0f, 0.1f);
        float turned = one[^1] - one[0];
        // The day's turn between ten and eleven: 10 (sin(3/24 · 2π) − sin(2/24 · 2π)), about 2.1 degrees.
        Assert.InRange(turned, 1.5f, 3.5f);

        var byHand = Hour(1, e => e.PinWind(new Vector3(5f, 0f, 0f), 0.4f));
        foreach (float f in byHand) Assert.Equal(270f, f, 2);
    }

    [Fact]
    public void WeatherSaysWhereTheWindIsTurningTo()
    {
        var west = new Vector3(5f, 0f, 0f);
        Assert.Equal("", CommandHandler.DescribeTurning(west, west));
        Assert.Equal("", CommandHandler.DescribeTurning(west, WorldEnvironmentSystem.Veered(west, 10f)));
        Assert.Equal(" Turning to come from the north west.", CommandHandler.DescribeTurning(west, WorldEnvironmentSystem.Veered(west, 45f)));
        Assert.Equal(" Turning to come from the south.", CommandHandler.DescribeTurning(west, WorldEnvironmentSystem.Veered(west, -90f)));
        Assert.Equal("", CommandHandler.DescribeTurning(Vector3.Zero, west));
    }

    // ── Rigs ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A server weather system and a client following it, a second at a time.</summary>
    private sealed class Sim
    {
        public readonly WorldEnvironmentSystem Env = new(new Random(1)) { FrontProbabilityPerTick = 0 };
        public double Clock = 10_000.0;
        public WindWeather Client = WindWeather.Default;

        public Sim(Action<WorldEnvironmentSystem> set) { set(Env); Client = Client.Following(Broadcast(), Clock); }

        public void Run(int seconds)
        {
            for (int s = 0; s < seconds; s++)
            {
                for (int i = 0; i < 60; i++) Env.Update(1f / 60f);
                Clock += 1.0;
                Client = Client.Following(Broadcast(), Clock);
            }
        }

        public WindAir Broadcast()
        {
            var st = Env.GetCurrentState();
            var (e, n) = Env.WindTravel;
            return WindAir.FromBroadcast(st.WindVelocity, st.WindGustiness, Clock, e, n);
        }
    }

    private sealed class CommandRig : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-weather-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteUserRepository _users;
        private readonly MapManager _maps;
        private readonly SessionManager _sessions = new();
        public readonly GameServer Server;
        private readonly CommandHandler _commands;
        public readonly List<(UserSession To, IMessage What)> Sent = new();

        public CommandRig()
        {
            Directory.CreateDirectory(Path.Combine(_dir, "maps"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "default.json"), Path.Combine(_dir, "maps", "default.json"));
            _maps = new MapManager(new MapRepository(Path.Combine(_dir, "maps")),
                                   new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
            _maps.Initialize();
            _users = new SqliteUserRepository(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "accounts.db")}").Options,
                workFactor: 4);
            Server = new GameServer(_users);
            Server.Attach(_maps, _sessions);
            Server.Sent = (to, what) => Sent.Add((to, what));
            _commands = new CommandHandler(_sessions, _maps, Server, users: _users);
        }

        public UserSession Online(string name, int id, UserRole role)
        {
            _users.AddUser(name, "long enough", role);
            Assert.True(_maps.TryGetMap("default", out var world, out _, out _, out _));
            var entity = world.Create(new Transform { Position = new Vector3(id * 3, 2, 0), Rotation = Quaternion.Identity },
                                      new Velocity(), new PlayerComponent { ConnectionId = id, Username = name, Role = role }, EntityType.Player);
            _maps.IndexEntity("default", entity);
            var session = new UserSession { ConnectionId = id, Username = name, Role = role, Entity = entity, CurrentMapId = "default", Welcomed = true };
            _sessions.AddSession(id, session);
            return session;
        }

        public string Run(UserSession who, string command, params string[] args)
        {
            var replies = new List<string>();
            _commands.HandleTextCommand(who.ConnectionId, new TextCommand { Command = command, Args = args },
                m => { if (m is TextEvent t) replies.Add(t.Text); });
            Server.DrainCommandBuffer();
            return string.Join(" | ", replies);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch { }
        }
    }
}
