using System;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// What ClientAudioSystem tells the mixer about cars: which ones get a voice, which kind, at what
/// level, and with which acoustic path. Driven through <see cref="ClientAudioHarness"/>, so the
/// budget, the facade and the acoustic worker are the real ones.
/// </summary>
public class ClientAudioSystemTests
{
    private readonly ITestOutputHelper _o;
    public ClientAudioSystemTests(ITestOutputHelper o) => _o = o;

    private const string Preset = "v6";
    private const int NearCar = 1;
    private const int DistantNear = 1001;    // voiced from afar, 45 m out
    private const int DistantFar = 1002;     // voiced from afar, 110 m out

    /// <summary>
    /// A full live budget of cars in a ring 20 m round the listener, and two more of the same kind
    /// further out, which can only be voiced by borrowing. Air absorption saturates a little past
    /// 130 m, so the two outliers sit inside that for their paths to be told apart.
    /// </summary>
    private static ClientAudioHarness Field()
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        int budget = ClientAudioSystem.EngineVoiceBudget;
        for (int i = 0; i < budget; i++)
        {
            float a = i * 2f * MathF.PI / budget;
            h.AddCar(NearCar + i, Preset, new Vector3(20f * MathF.Cos(a), 0f, 20f * MathF.Sin(a)));
        }
        h.AddCar(DistantNear, Preset, new Vector3(0f, 0f, -45f));
        h.AddCar(DistantFar, Preset, new Vector3(0f, 0f, -110f));
        return h;
    }

    /// <summary>
    /// Every voice started for a vehicle is given an acoustic path: its live engine, its borrowed
    /// voice when it is voiced from afar, and its horn. A voice with no path is never occluded and
    /// never darkened by distance — before d538b01 that was every car voiced from afar, heard as a
    /// white-noise wash from the edge of the city.
    /// </summary>
    [Fact]
    public void EveryVoiceOfEveryCarIsGivenAnAcousticPath()
    {
        var h = Field();
        h.Honk(NearCar);
        h.Honk(DistantNear);

        int[] voices =
        {
            NearCar,
            ClientAudioHarness.HornVoice(NearCar),
            ClientAudioHarness.DistantVoice(DistantNear),
            ClientAudioHarness.HornVoice(DistantNear),
            ClientAudioHarness.DistantVoice(DistantFar),
        };
        bool allStarted = h.TickUntil(() => voices.All(h.Mixer.WasStarted), 120);
        Assert.True(allStarted, "not started: " + string.Join(", ", voices.Where(v => !h.Mixer.WasStarted(v))));

        bool allPathed = h.TickUntil(() => voices.All(h.Mixer.HasPath), 300);
        _o.WriteLine(string.Join("\n", voices.Select(v => $"{v}: started {h.Mixer.WasStarted(v)}, paths {h.Mixer.Paths.GetValueOrDefault(v)?.Count ?? 0}")));
        Assert.True(allPathed, "no acoustic path for: " + string.Join(", ", voices.Where(v => !h.Mixer.HasPath(v))));

        // A borrowed voice and a horn are behind whatever their car is behind: the car's own path.
        Assert.Equal(h.Mixer.LastPath(DistantNear).Occlusion, h.Mixer.LastPath(ClientAudioHarness.DistantVoice(DistantNear)).Occlusion);
        Assert.Equal(h.Mixer.LastPath(DistantNear).AirAbsorption, h.Mixer.LastPath(ClientAudioHarness.DistantVoice(DistantNear)).AirAbsorption);
    }

    /// <summary>
    /// A car voiced from afar is darkened by the air in proportion to how far away it is: the one at
    /// 110 m carries more air absorption than the one at 45 m, and both more than a live engine 20 m
    /// away. The failure this guards is a borrowed voice arriving with its high band intact however
    /// far off it is.
    /// </summary>
    [Fact]
    public void ADistantVoiceIsDarkenedByTheAirInProportionToItsDistance()
    {
        var h = Field();
        int nearVoice = ClientAudioHarness.DistantVoice(DistantNear), farVoice = ClientAudioHarness.DistantVoice(DistantFar);
        Assert.True(h.TickUntil(() => h.Mixer.HasPath(nearVoice) && h.Mixer.HasPath(farVoice) && h.Mixer.HasPath(NearCar), 300),
            "the borrowed voices were never given a path");

        float live = h.Mixer.LastPath(NearCar).AirAbsorption;
        float at45 = h.Mixer.LastPath(nearVoice).AirAbsorption;
        float at110 = h.Mixer.LastPath(farVoice).AirAbsorption;
        _o.WriteLine($"air absorption: live at 20 m {live:F3}, borrowed at 45 m {at45:F3}, borrowed at 110 m {at110:F3}");

        Assert.True(at45 > live, $"45 m ({at45}) should be darker than 20 m ({live})");
        Assert.True(at110 > at45 + 0.1f, $"110 m ({at110}) should be clearly darker than 45 m ({at45})");
    }

    /// <summary>
    /// A car's voice is placed by its own declared level and its own size: Volume and MinDistance are
    /// exactly Loudness.Place(SourceLevelDb, outlet separation), for a live engine and for a borrowed
    /// one. Presets twenty-odd decibels apart must not come out the same, and neither may fall back
    /// to the one-number-for-every-car level.
    /// </summary>
    [Theory]
    [InlineData("v6")]
    [InlineData("school_bus")]
    [InlineData("nascar_v8")]
    public void AnEngineIsPlacedAtItsOwnDeclaredLevel(string preset)
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        h.AddCar(NearCar, preset, new Vector3(30f, 0f, 0f));
        // Enough of the same kind that the last one has to borrow.
        for (int i = 1; i <= ClientAudioSystem.EngineVoiceBudget; i++)
            h.AddCar(NearCar + i, preset, new Vector3(30f + i * 0.5f, 0f, 40f + i));
        int last = NearCar + ClientAudioSystem.EngineVoiceBudget;
        int borrowed = ClientAudioHarness.DistantVoice(last);
        Assert.True(h.TickUntil(() => h.Mixer.WasStarted(NearCar) && h.Mixer.WasStarted(borrowed), 120));

        var profile = MachineRegistry.VehicleFor(preset);
        Assert.True(profile.SourceLevelDb > 0f, "every preset declares its level");
        float extent = Vector3.Distance(new Vector3(0f, profile.ExhaustHeight, profile.ExhaustOffsetZ),
                                        new Vector3(0f, profile.IntakeHeight, profile.IntakeOffsetZ));
        var (gain, reference) = Loudness.Place(profile.SourceLevelDb, extent);

        var live = h.Mixer.Latest[NearCar];
        _o.WriteLine($"{preset}: {profile.SourceLevelDb} dB, extent {extent:F2} m -> gain {gain:F4}, reference {reference:F2} m; "
                   + $"live {live.Volume:F4}/{live.MinDistance:F2}, borrowed {h.Mixer.Latest[borrowed].Volume:F4}/{h.Mixer.Latest[borrowed].MinDistance:F2}");
        Assert.Equal(preset, live.EngineKey);
        Assert.Equal(gain, live.Volume, 5);
        Assert.Equal(reference, live.MinDistance, 4);
        Assert.Equal(extent, live.ExtentMetres, 4);
        Assert.Equal(Loudness.AudibleRange(profile.SourceLevelDb), live.Range, 2);

        var far = h.Mixer.Latest[borrowed];
        Assert.Equal(gain, far.Volume, 5);
        Assert.Equal(reference, far.MinDistance, 4);

        if (MathF.Abs(profile.SourceLevelDb - ClientAudioSystem.EngineSourceLevelDb) > 3f)
        {
            var (fallbackGain, fallbackRef) = Loudness.Place(ClientAudioSystem.EngineSourceLevelDb, extent);
            Assert.False(MathF.Abs(fallbackGain * fallbackRef - live.Volume * live.MinDistance) < 1e-4f,
                "placed at the fallback level, not the preset's own");
        }
    }

    /// <summary>
    /// With more cars than the live budget, the nearest get live engines and the next ones are voiced
    /// from afar; walk to the other end of the line and the two sets swap — the cars now nearest are
    /// synthesized and give their borrowed voices back, the ones left behind lose their engines and
    /// borrow. A budget that ranked by anything but nearness, or never re-ranked, fails here.
    /// </summary>
    [Fact]
    public void TheNearestCarsAreSynthesizedAndTheRestBorrowAndMovingSwapsThem()
    {
        var h = new ClientAudioHarness();
        int budget = ClientAudioSystem.EngineVoiceBudget;
        const int extra = 8;
        int n = budget + extra;
        for (int i = 0; i < n; i++) h.AddCar(i + 1, Preset, new Vector3(10f + i * 10f, 0f, 0f));
        int CarAt(int index) => index + 1;

        h.StandAt(Vector3.Zero);
        h.Tick(budget);      // engines are admitted two an update
        for (int i = 0; i < budget; i++)
            Assert.True(h.Mixer.Live.Contains(CarAt(i)), $"car {i} ({10 + i * 10} m) should have a live engine");
        for (int i = budget; i < n; i++)
        {
            Assert.False(h.Mixer.Live.Contains(CarAt(i)), $"car {i} ({10 + i * 10} m) is outside the budget but has a live engine");
            Assert.True(h.Mixer.Live.Contains(ClientAudioHarness.DistantVoice(CarAt(i))), $"car {i} should be voiced from afar");
        }

        // To the far end, past the hold that stops an engine being dropped the moment it started.
        h.StandAt(new Vector3(10f + n * 10f, 0f, 0f));
        h.Wait(3.0);
        h.Tick(budget);

        for (int i = budget; i < n; i++)
        {
            Assert.True(h.Mixer.Live.Contains(CarAt(i)), $"car {i}, now among the nearest, should have a live engine");
            Assert.False(h.Mixer.Live.Contains(ClientAudioHarness.DistantVoice(CarAt(i))), $"car {i} kept its borrowed voice as well as its engine");
        }
        for (int i = 0; i < extra; i++)
        {
            Assert.False(h.Mixer.Live.Contains(CarAt(i)), $"car {i}, now the furthest, kept its live engine");
            Assert.True(h.Mixer.Live.Contains(ClientAudioHarness.DistantVoice(CarAt(i))), $"car {i}, now the furthest, should borrow");
        }
        int liveEngines = Enumerable.Range(0, n).Count(i => h.Mixer.Live.Contains(CarAt(i)));
        Assert.Equal(budget, liveEngines);
    }

    /// <summary>
    /// When the server says a car is gone, every voice it had stops: its live engine and its borrowed
    /// voice at once, from ForgetEntity itself, and a horn that was still blowing on the next update —
    /// and none of them is started again. A voice keyed by an id the world no longer has plays for
    /// the rest of the session.
    /// </summary>
    [Fact]
    public void AForgottenCarLosesEveryVoiceItHad()
    {
        var h = Field();
        h.Honk(DistantNear);
        int borrowed = ClientAudioHarness.DistantVoice(DistantNear), horn = ClientAudioHarness.HornVoice(DistantNear);
        Assert.True(h.TickUntil(() => h.Mixer.Live.Contains(NearCar) && h.Mixer.Live.Contains(borrowed) && h.Mixer.Live.Contains(horn), 120));

        foreach (int id in h.World.RemoveEntities(new[] { NearCar, DistantNear })) h.Audio.ForgetEntity(id);
        int startedBefore = h.Mixer.Started.Count;
        // The mixer's own thread runs before the next audio update does.
        h.Facade.PumpForTest();
        h.Facade.PumpForTest();
        Assert.DoesNotContain(NearCar, h.Mixer.Live);
        Assert.DoesNotContain(borrowed, h.Mixer.Live);

        h.Tick(30);

        foreach (int v in new[] { NearCar, borrowed, horn })
        {
            Assert.Contains(v, h.Mixer.Stopped);
            Assert.DoesNotContain(v, h.Mixer.Live);
            Assert.DoesNotContain(h.Mixer.Started.Skip(startedBefore), e => e.EntityId == v);
        }
    }
}
