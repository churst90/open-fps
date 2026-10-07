using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// What ClientAudioSystem decides about everything that is not a single car's level: how many engines
/// are built an update, which preset keeps a donor, how many cars may borrow, which machines win a
/// voice and on what measure (what each would sound like here, through the ear model, not how near it
/// is), what is given up first when the mixer runs short, the cabin you are sitting in, every kind of
/// physical source placed at its own declared level, and the woods heard as one past the hand-over.
///
/// Driven through <see cref="ClientAudioHarness"/>: the budget, the facade and the acoustic worker are
/// the real ones, and the clock is the test's.
/// </summary>
[Collection(nameof(LevelCompressionSetting))]
public class ClientAudioSelectionTests
{
    private readonly ITestOutputHelper _o;
    public ClientAudioSelectionTests(ITestOutputHelper o) => _o = o;

    private const string Preset = "v6";

    // ── Engines ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A map load presents every car at once, and building an engine is an allocation spike: they are
    /// let in two an update, nearest first, until the budget is full.
    /// </summary>
    [Fact]
    public void EnginesAreBuiltTwoAnUpdateNearestFirst()
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        for (int i = 0; i < 9; i++) h.AddCar(i + 1, Preset, new Vector3(10f + i * 5f, 0f, 0f));

        int LiveCars() => Enumerable.Range(1, 9).Count(h.Mixer.Live.Contains);
        h.Tick();
        Assert.Equal(2, LiveCars());
        Assert.Contains(1, h.Mixer.Live);
        Assert.Contains(2, h.Mixer.Live);
        h.Tick();
        Assert.Equal(4, LiveCars());
        h.Tick(3);
        Assert.Equal(9, LiveCars());
        // Nobody was voiced from afar on the way: there was room for every car.
        Assert.DoesNotContain(h.Mixer.Started, e => e.EntityId <= ClientAudioSystem.DistantVoiceBase
                                                  && e.EntityId > ClientAudioSystem.DistantVoiceBase - 1000);
    }

    /// <summary>
    /// A car outside the budget borrows the ring of a live car of its own preset; one whose preset has
    /// no live car would be silent. So the nearest car of every preset on the map is given an engine
    /// whatever the budget says, and it is a real engine, not a borrowed voice.
    /// </summary>
    [Fact]
    public void EveryPresetOnTheMapKeepsOneLiveEngine()
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        int budget = ClientAudioSystem.EngineVoiceBudget;
        for (int i = 0; i < budget + 4; i++) h.AddCar(i + 1, Preset, new Vector3(10f + i * 2f, 0f, 0f));
        const int Lone = 5000;
        h.AddCar(Lone, "school_bus", new Vector3(0f, 0f, 200f));

        Assert.True(h.TickUntil(() => h.Mixer.Live.Contains(Lone), 60), "the only bus on the map was never given an engine");
        h.Tick(budget);
        h.Wait(3.0);                       // past the hold on a newly built engine
        h.Tick(budget);
        Assert.Equal("school_bus", h.Mixer.Latest[Lone].EngineKey);
        Assert.False(h.Mixer.WasStarted(ClientAudioHarness.DistantVoice(Lone)), "the bus borrowed a voice it could not have had");
        // ...and on top of the budget, not out of it: the budget's own cars are the nearest of the rest.
        Assert.Equal(budget, Enumerable.Range(1, budget).Count(h.Mixer.Live.Contains));
    }

    /// <summary>
    /// The preset's donor sits outside the budget, and it must stay there. It used to be stopped and
    /// re-admitted every time its hold ran out, and every re-admission stamped it as newly built: held
    /// again, ranked first, and given a slot inside the budget, which turned out the car at the
    /// budget's edge. That car's engine was faded and rebuilt from nothing, the donor's echoes were
    /// forgotten, and the whole cycle came round every 2.5 s for as long as nothing moved. Ten seconds
    /// of a field standing still must stop and start nothing.
    /// </summary>
    [Fact]
    public void ADonorOutsideTheBudgetDoesNotChurnTheCarsInIt()
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        int budget = ClientAudioSystem.EngineVoiceBudget;
        int n = budget + 4;
        for (int i = 0; i < n; i++) h.AddCar(i + 1, Preset, new Vector3(10f + i * 2f, 0f, 0f));
        const int Lone = 5000;
        h.AddCar(Lone, "school_bus", new Vector3(0f, 0f, 200f));
        h.Tick(budget);
        h.Wait(3.0);
        h.Tick(budget);

        int starts = h.Mixer.Started.Count, stops = h.Mixer.Stopped.Count;
        for (int s = 0; s < 100; s++) { h.Wait(0.1); h.Tick(); }
        var started = h.Mixer.Started.Skip(starts).Select(e => e.EntityId).ToList();
        var stopped = h.Mixer.Stopped.Skip(stops).ToList();
        _o.WriteLine($"in 10 s standing still: started {string.Join(", ", started)}; stopped {string.Join(", ", stopped)}");
        Assert.Empty(started.Where(id => id >= 1 && id <= n || id == Lone));
        Assert.Empty(stopped.Where(id => id >= 1 && id <= n || id == Lone));
        Assert.Contains(Lone, h.Mixer.Live);
        Assert.Equal(budget, Enumerable.Range(1, budget).Count(h.Mixer.Live.Contains));
    }

    /// <summary>
    /// Borrowing makes a car cheap but not free: a borrowed voice is still a placed, filtered voice. At
    /// most twelve are voiced, the nearest twelve outside the live budget; past them a car is too far
    /// away to pick out of the pack and has no voice at all.
    /// </summary>
    [Fact]
    public void AtMostTwelveCarsBorrowAndTheyAreTheNearestOfTheRest()
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        int budget = ClientAudioSystem.EngineVoiceBudget;
        int n = budget + 20;
        for (int i = 0; i < n; i++) h.AddCar(i + 1, Preset, new Vector3(10f + i * 3f, 0f, 0f));
        h.Tick(budget);

        var borrowing = Enumerable.Range(1, n).Where(id => h.Mixer.Live.Contains(ClientAudioHarness.DistantVoice(id))).ToList();
        _o.WriteLine($"borrowing: {string.Join(", ", borrowing)}");
        Assert.Equal(12, borrowing.Count);
        Assert.Equal(Enumerable.Range(budget + 1, 12), borrowing);
        for (int id = budget + 13; id <= n; id++)
        {
            Assert.DoesNotContain(id, h.Mixer.Live);
            Assert.False(h.Mixer.WasStarted(ClientAudioHarness.DistantVoice(id)), $"car {id} is past both budgets and was voiced");
        }
    }

    /// <summary>
    /// A borrowed voice reads a live engine of its own preset from a point in the ring of its own
    /// (so a field of them is traffic, not one car summed), moves as itself (its own velocity, so it
    /// Dopplers as itself) and is reverberated in the open, not in whatever room its donor is in.
    /// </summary>
    [Fact]
    public void ABorrowedVoiceReadsADonorOfItsPresetAtAnOffsetOfItsOwn()
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        int budget = ClientAudioSystem.EngineVoiceBudget;
        for (int i = 0; i < budget; i++) h.AddCar(i + 1, Preset, new Vector3(10f + i, 0f, 0f));
        int a = 3001, b = 3005;
        var velocity = new Vector3(0f, 0f, 22f);
        h.AddCar(a, Preset, new Vector3(0f, 0f, -80f), velocity);
        h.AddCar(b, Preset, new Vector3(0f, 0f, -90f), -velocity);
        int va = ClientAudioHarness.DistantVoice(a), vb = ClientAudioHarness.DistantVoice(b);
        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(va) && h.Mixer.Latest.ContainsKey(vb), 60));

        foreach (var (car, voice, v) in new[] { (a, va, velocity), (b, vb, -velocity) })
        {
            var e = h.Mixer.Latest[voice];
            Assert.True(e.EchoOfEntity is >= 1 and <= 32, $"car {car} borrows from {e.EchoOfEntity}, which is not a live {Preset}");
            Assert.Equal(Preset, h.Mixer.Latest[e.EchoOfEntity].EngineKey);
            Assert.Equal(0.08f + (car % 17) * 0.045f, e.EchoDelaySeconds, 5);
            Assert.Equal(v, e.Velocity);
            Assert.Equal(AcousticConstants.GlobalRegionId, e.TargetRegionId);
            Assert.Equal("", e.EngineKey);
        }
        Assert.NotEqual(h.Mixer.Latest[va].EchoDelaySeconds, h.Mixer.Latest[vb].EchoDelaySeconds);
    }

    /// <summary>
    /// The car you are sitting in is heard from inside: no distance, no direction to speak of, no path
    /// (the body is the occluder, and the voice has rendered what gets through it), riding with your
    /// head, in your room, at the gain its reference distance would have had. Every other way into the
    /// cabin is a voice of its own on the same engine, turned to where it comes in; none of it is split
    /// into a front voice. Stepping out lets every cabin path go.
    /// </summary>
    [Fact]
    public void TheCarYouAreSittingInIsHeardFromInsideByEveryPathIn()
    {
        const int Car = 41;
        var h = new ClientAudioHarness();
        var at = new Vector3(5f, 0f, 5f);
        h.AddCar(Car, Preset, at);
        h.StandAt(at);
        h.Player.RidingEntityId = Car;
        var profile = MachineRegistry.VehicleFor(Preset);
        var layout = OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.For(profile);
        Assert.NotNull(layout);
        Assert.True(layout!.Count > 1, "a saloon has more than one way in");

        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(Car), 30));
        h.Tick(3);
        var engine = h.Mixer.Latest[Car];
        var (g0, r0) = Loudness.Place(profile.SourceLevelDb);
        Assert.True(engine.Interior);
        Assert.True(engine.FollowsListener);
        Assert.Equal(MathF.Min(1f, g0 * r0), engine.Volume, 5);
        Assert.Equal(1f, engine.MinDistance);
        Assert.Equal(0f, engine.ExtentMetres);
        Assert.Equal(0f, engine.Occlusion);
        Assert.Equal((1f, 1f, 1f), (engine.EqLow, engine.EqMid, engine.EqHigh));
        Assert.Equal((0f, 0f, 0f), (engine.AirLowDb, engine.AirMidDb, engine.AirHighDb));
        Assert.Equal(0.7f, engine.EffectiveDistance);
        Assert.NotEqual(Vector3.Zero, engine.ListenerOffset);
        // No front voice from inside: both ends come through the body.
        Assert.False(h.Mixer.WasStarted(ClientAudioSystem.IntakeVoiceBase - Car));

        var eye = h.Player.VisualPosition + new Vector3(0f, h.Player.EyeHeight, 0f);
        var cabinIds = Enumerable.Range(1, layout.Count - 1).Select(p => ClientAudioSystem.CabinVoiceBase - p).ToArray();
        for (int p = 1; p < layout.Count; p++)
        {
            int id = ClientAudioSystem.CabinVoiceBase - p;
            Assert.True(h.Mixer.Live.Contains(id), $"cabin path {p} ({layout.Paths[p].Kind}) has no voice");
            var e = h.Mixer.Latest[id];
            Assert.Equal(Car, e.CabinOfEntity);
            Assert.Equal(p, e.CabinPath);
            Assert.True(e.FollowsListener);
            Assert.False(e.Interior);
            Assert.Equal("", e.EngineKey);
            Assert.Equal(engine.Volume, e.Volume);
            Assert.Equal(engine.MinDistance, e.MinDistance);
            Assert.Equal(eye + e.ListenerOffset, e.Position);
        }
        Assert.Equal(cabinIds.Length, cabinIds.Select(id => h.Mixer.Latest[id].ListenerOffset).Distinct().Count());

        // Out of the car: every path goes, and the car is heard from outside again.
        h.Player.RidingEntityId = -1;
        h.StandAt(at + new Vector3(4f, 0f, 0f));
        h.Tick(3);
        foreach (int id in cabinIds)
        {
            Assert.Contains(id, h.Mixer.Stopped);
            Assert.DoesNotContain(id, h.Mixer.Live);
        }
        Assert.False(h.Mixer.Latest[Car].Interior);
        Assert.False(h.Mixer.Latest[Car].FollowsListener);
    }

    /// <summary>
    /// Close enough for its two ends to be told apart, a car gets a second voice at its front; at most
    /// six cars at once, and the six nearest. Further off, it is one voice.
    /// </summary>
    [Fact]
    public void AtMostSixCarsAreSplitAndTheyAreTheNearest()
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        for (int i = 0; i < 9; i++) h.AddCar(i + 1, Preset, new Vector3(4f + i * 0.5f, 0f, 3f));
        h.AddCar(20, Preset, new Vector3(0f, 0f, 250f));
        h.Tick(8);
        var split = Enumerable.Range(1, 9).Where(id => h.Mixer.Live.Contains(ClientAudioSystem.IntakeVoiceBase - id)).ToList();
        _o.WriteLine("split: " + string.Join(", ", split));
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, split);
        Assert.False(h.Mixer.WasStarted(ClientAudioSystem.IntakeVoiceBase - 20), "a car 250 m off was split");

        // The front voice is the same machine: the same level reference, read off the car's engine.
        var front = h.Mixer.Latest[ClientAudioSystem.IntakeVoiceBase - 1];
        var back = h.Mixer.Latest[1];
        Assert.Equal(1, front.IntakeOfEntity);
        Assert.Equal(back.Volume, front.Volume);
        Assert.Equal(back.MinDistance, front.MinDistance);
        Assert.NotEqual(back.Position, front.Position);
    }

    // ── Physical sources: every kind placed at its own declared level ──────────────────────────

    /// <summary>Puts a synthesised source on the map as the server does: a static entity with an
    /// emitter whose sound id names its model.</summary>
    private static void AddSource(ClientAudioHarness h, int id, string soundId, Vector3 at, Vector3? size = null)
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        if (size is { } s) def.Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = s, IsSolid = false };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = soundId;
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.MinDistance = 1f;
        def.SoundEmitter.Range = 50f;
        h.World.RegisterDefinition(def);
    }

    /// <summary>One source of each kind the lookup knows, by a preset that ships.</summary>
    public static IEnumerable<object[]> Kinds()
    {
        yield return new object[] { "machine:ac_condenser" };
        yield return new object[] { "bell:" + StruckBellSpec.Presets.Keys.First() };
        yield return new object[] { "rail:" + TrainProfile.Presets.Keys.First() + "/t1/0" };
        yield return new object[] { "water:" + WaterFeatureSpec.Presets.Keys.First() };
        yield return new object[] { "fire:" + FireSpec.Presets.Keys.First() };
        yield return new object[] { "foliage:park_tree" };
        yield return new object[] { "flow:creek" };
        yield return new object[] { "shore:lake_sand" };
        yield return new object[] { "aircraft:" + AircraftProfile.Presets.Keys.First() };
    }

    /// <summary>
    /// Every kind of physical source is placed by its own declared level and its own size, from the
    /// same lookup the ranking used: the reference distance Loudness.Place gives that level and size,
    /// the size as its extent, the level handed on for the ear model, and the gain (for a source that is
    /// not spread over places of its own) the placed gain times its headroom's return. A kind the lookup
    /// did not know would not be voiced at all, which is how the crossing bell once went silent.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void EveryKindOfPhysicalSourceIsPlacedAtItsOwnDeclaredLevel(string soundId)
    {
        using var wind = WindField.Hold(WindWeather.Steady(6f, 270f, 0f));
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        const int Id = 77;
        AddSource(h, Id, soundId, new Vector3(0f, 1f, 12f), new Vector3(4f, 1f, 4f));
        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(Id), 30), $"{soundId} was never voiced");

        var (level, extent) = Declared(soundId);
        var (gain, reference) = Loudness.Place(level, extent);
        var e = h.Mixer.Latest[Id];
        _o.WriteLine($"{soundId}: {level:F1} dB, extent {extent:F2} m -> {gain:F4} at {reference:F2} m; voiced {e.Volume:F4} at {e.MinDistance:F2} m, key {e.PhysicalKey}");
        Assert.StartsWith(soundId.Split(':')[0] + ":", e.PhysicalKey);
        Assert.Equal(level, e.EarLevelDb, 3);
        Assert.Equal(reference, e.MinDistance, 4);
        Assert.Equal(extent, e.ExtentMetres, 4);
        Assert.True(e.Range >= Loudness.AudibleRange(level) - 1e-3f, $"range {e.Range} under the level's audible range");
        Assert.Equal("", e.EngineKey);
        if (OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Layout(e.PhysicalKey) == null)
            Assert.Equal(gain * PhysicalVoiceState.HeadroomGain(Headroom(soundId)), e.Volume, 5);
        else
            Assert.True(e.Volume > 0f && float.IsFinite(e.Volume));
    }

    private static (float Level, float Extent) Declared(string soundId)
    {
        string kind = soundId[..soundId.IndexOf(':')], name = soundId[(soundId.IndexOf(':') + 1)..];
        switch (kind)
        {
            case "machine": { var s = SmallMachineSpec.ByName(name); return (s.SourceLevelDb, s.ExtentMetres); }
            case "bell": { var b = ModelLibrary.Bell(name); return (b.ReferenceDb, MathF.Max(0.5f, b.DiameterMetres)); }
            case "rail":
            {
                var layout = TrainLayout.Sources(TrainProfile.ByName(name.Split('/')[0]));
                return (layout[0].LevelDb, layout[0].ExtentMetres);
            }
            case "water": { var w = WaterFeatureSpec.ByName(name); return (w.SourceLevelDb, w.ExtentMetres); }
            case "fire": { var f = FireSpec.ByName(name); return (f.SourceLevelDb, f.ExtentMetres); }
            case "foliage": { var t = FoliageSpec.ByName(name); return (t.SourceLevelDb, t.ExtentMetres); }
            case "flow": { var r = RunningWaterSpec.ByName(name); return (r.SourceLevelDb, r.ExtentMetres); }
            case "shore": { var s = ShoreSpec.ByName(soundId); return (s.SourceLevelDb, s.ExtentMetres); }
            case "aircraft":
            {
                var p = AircraftProfile.ByName(name);
                float span = p.EngineSpanMetres > 0f ? p.EngineSpanMetres
                    : MathF.Max(2f, p.Propeller?.DiameterMetres ?? p.Turbine?.Fan?.DiameterMetres ?? p.Turbine?.BypassNozzleDiameterMetres ?? 2f);
                return (p.SourceLevelDb, span);
            }
        }
        throw new ArgumentException(soundId);
    }

    private static float Headroom(string soundId) => soundId.Split(':')[0] switch
    {
        "bell" => ModelLibrary.Bell(soundId[5..]).PeakHeadroomDb,
        "flow" => RunningWaterSpec.ByName(soundId[5..]).PeakHeadroomDb,
        "shore" => ShoreSpec.ByName(soundId).PeakHeadroomDb,
        "water" => WaterFeatureSpec.ByName(soundId[6..]).PeakHeadroomDb,
        "fire" => FireSpec.ByName(soundId[5..]).PeakHeadroomDb,
        "foliage" => FoliageSpec.ByName(soundId[8..]).PeakHeadroomDb,
        _ => VehicleProfile.PeakHeadroomDb,
    };

    // ── Standing machines: the budget and the ear ───────────────────────────────────────────────

    /// <summary>
    /// The machine budget ranks by what each machine would sound like HERE, not by how near it is. Ten
    /// window units (59 dB) a few metres off fill the budget on distance alone; a rooftop condenser
    /// (65 dB) a little further away is louder where you stand than the furthest of them, and takes
    /// its place. Nothing outside the budget is voiced.
    /// </summary>
    [Fact]
    public void MachinesAreRankedByWhatTheyWouldSoundLikeHereNotByDistance()
    {
        int budget = ClientAudioSystem.MachineVoiceBudget;
        Assert.True(budget >= 2, "the test needs a machine budget");
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        var windows = new List<int>();
        for (int i = 0; i < budget; i++)
        {
            float a = i * MathF.Tau / budget;
            AddSource(h, 100 + i, "machine:ac_window", new Vector3(MathF.Cos(a) * (8f + i * 0.2f), 1f, MathF.Sin(a) * (8f + i * 0.2f)));
            windows.Add(100 + i);
        }
        const int Condenser = 200;
        AddSource(h, Condenser, "machine:ac_condenser", new Vector3(0f, 1f, 12f));
        h.Tick(budget);

        Assert.Contains(Condenser, h.Mixer.Live);
        Assert.Equal(budget, windows.Count(h.Mixer.Live.Contains) + 1);
        // The furthest window unit is the one given up.
        Assert.DoesNotContain(windows[^1], h.Mixer.Live);
        Assert.False(h.Mixer.WasStarted(windows[^1]), "a machine outside the budget was voiced");
    }

    /// <summary>
    /// The machine budget ranks a machine on the gain the mixer's law will play it at, the ear model's
    /// correction for what it is made of included (EarTimbres, as VoiceManager.Audibility does), so the
    /// two never disagree about a source: one that wins a voice on one set of numbers and is played at
    /// another is picked out of a crowd and then cannot be heard. A condenser 30 m off loses the last
    /// slot to the window units; give it a measured timbre the law plays 20 dB up (all of it in the
    /// lowest third-octave) and the same machine at the same place takes the slot.
    ///
    /// Whether a timbre the ear barely hears SHOULD rank higher is a question for the ear model (see
    /// docs/COVERAGE_2026-10-06.md): the law plays such a sound louder so that it is heard at its real
    /// loudness, and the ranking follows the law's gain, not the loudness.
    /// </summary>
    [Fact]
    public void TheMachineBudgetRanksOnTheGainTheLawPlaysItAt()
    {
        int budget = ClientAudioSystem.MachineVoiceBudget;
        bool wasOn = OpenFPS.Common.Hearing.EarModel.Enabled;
        const int Condenser = 200;
        bool CondenserWins()
        {
            var h = new ClientAudioHarness();
            h.StandAt(Vector3.Zero);
            for (int i = 0; i < budget; i++)
            {
                float a = i * MathF.Tau / budget;
                AddSource(h, 100 + i, "machine:ac_window", new Vector3(MathF.Cos(a) * (8f + i * 0.2f), 1f, MathF.Sin(a) * (8f + i * 0.2f)));
            }
            AddSource(h, Condenser, "machine:ac_condenser", new Vector3(0f, 1f, 30f));
            h.Tick(budget);
            Assert.Equal(budget, Enumerable.Range(100, budget).Count(h.Mixer.Live.Contains) + (h.Mixer.Live.Contains(Condenser) ? 1 : 0));
            return h.Mixer.Live.Contains(Condenser);
        }
        try
        {
            OpenFPS.Common.Hearing.EarModel.Enabled = true;
            OpenFPS.Client.AudioEngine.Core.EarTimbres.Clear();
            Assert.False(CondenserWins(), "unmeasured, a 65 dB condenser at 30 m should not beat a 59 dB window unit at 10 m");

            var levels = Enumerable.Repeat(-60f, OpenFPS.Common.Hearing.Timbre.Bands).ToArray();
            levels[0] = 0f;
            OpenFPS.Client.AudioEngine.Core.EarTimbres.Set("machine:ac_condenser",
                OpenFPS.Common.Hearing.Timbre.FromBandLevels(levels, "rumble"));
            float correction = OpenFPS.Client.AudioEngine.Core.EarTimbres.CorrectionDb("machine:ac_condenser", 65f);
            _o.WriteLine($"the law's correction for a 65 dB rumble at 25 Hz: {correction:+0.0;-0.0} dB");
            Assert.True(correction > 10f, $"the law plays a 25 Hz rumble well up: {correction:F1} dB");
            Assert.True(CondenserWins(), "the ranking did not read the law's correction");
        }
        finally
        {
            OpenFPS.Client.AudioEngine.Core.EarTimbres.Clear();
            OpenFPS.Common.Hearing.EarModel.Enabled = wasOn;
        }
    }

    /// <summary>
    /// A machine that holds a voice keeps it until a challenger is decisively louder, and none is let
    /// go within its first moments: walking past a row of equal machines does not swap voices at
    /// every step. Once the hold is over and the listener has walked to the other end, the set follows.
    /// </summary>
    [Fact]
    public void AMachineKeepsItsVoiceUntilItIsDecisivelyBeaten()
    {
        int budget = ClientAudioSystem.MachineVoiceBudget;
        var h = new ClientAudioHarness();
        int n = budget + 4;
        for (int i = 0; i < n; i++) AddSource(h, 100 + i, "machine:ac_window", new Vector3(i * 3f, 1f, 4f));
        h.StandAt(Vector3.Zero);
        h.Tick(n);
        var first = Enumerable.Range(100, n).Where(h.Mixer.Live.Contains).ToHashSet();
        Assert.Equal(Enumerable.Range(100, budget).ToHashSet(), first);

        // Two windows' spacing on, inside the hold: nothing changes.
        h.StandAt(new Vector3(6f, 0f, 0f));
        h.Tick(5);
        Assert.Equal(first, Enumerable.Range(100, n).Where(h.Mixer.Live.Contains).ToHashSet());

        // To the far end, past the hold: the far machines take over, and those let go are stopped.
        h.StandAt(new Vector3((n - 1) * 3f, 0f, 0f));
        h.Wait(3.0);
        h.Tick(n);
        var last = Enumerable.Range(100, n).Where(h.Mixer.Live.Contains).ToHashSet();
        Assert.Equal(Enumerable.Range(100 + n - budget, budget).ToHashSet(), last);
        foreach (int id in first.Except(last)) Assert.Contains(id, h.Mixer.Stopped);
    }

    // ── The budget under load ───────────────────────────────────────────────────────────────────

    /// <summary>The counts the budget controls, read off what the mixer was told.</summary>
    private static (int Engines, int Borrowed, int Fronts, int Machines) Counts(ClientAudioHarness h, int cars, int machines)
        => (Enumerable.Range(1, cars).Count(h.Mixer.Live.Contains),
            Enumerable.Range(1, cars).Count(id => h.Mixer.Live.Contains(ClientAudioHarness.DistantVoice(id))),
            Enumerable.Range(1, cars).Count(id => h.Mixer.Live.Contains(ClientAudioSystem.IntakeVoiceBase - id)),
            Enumerable.Range(1000, machines).Count(h.Mixer.Live.Contains));

    private static ClientAudioHarness LoadedField(out int cars, out int machines)
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        cars = ClientAudioSystem.EngineVoiceBudget + 14;
        for (int i = 0; i < cars; i++) h.AddCar(i + 1, Preset, new Vector3(3f + i * 0.6f, 0f, 3f + (i % 2) * 2f));
        machines = ClientAudioSystem.MachineVoiceBudget;
        for (int i = 0; i < machines; i++) AddSource(h, 1000 + i, "machine:ac_window", new Vector3(-6f - i, 1f, -4f));
        h.Tick(ClientAudioSystem.EngineVoiceBudget);
        return h;
    }

    /// <summary>
    /// A mixer over its ceiling gives things up IN ORDER, one a second: first the voices whose loss
    /// costs nothing but geometry (a machine's second outlet), then standing machines down to one, then
    /// reflections, then borrowed voices down to four, and only then cars, never below two. A car
    /// vanishing is noticed; a wall that stops answering is not.
    /// </summary>
    [Fact]
    public void AnOverloadedMixerGivesUpFrontVoicesThenMachinesThenBorrowedVoicesThenCars()
    {
        var h = LoadedField(out int cars, out int machines);
        var start = Counts(h, cars, machines);
        _o.WriteLine($"start: {start}");
        Assert.Equal((ClientAudioSystem.EngineVoiceBudget, 12, 6, machines), start);

        h.Wait(4.0);                       // past the hold a scene load starts with
        h.Mixer.Load = 0.95f;
        var seen = new List<(int Engines, int Borrowed, int Fronts, int Machines)>();
        for (int s = 0; s < 80; s++)
        {
            h.Tick();
            h.Wait(1.05);
            h.Tick();
            seen.Add(Counts(h, cars, machines));
        }
        foreach (var c in seen.Distinct()) _o.WriteLine(c.ToString());

        int FirstWhen(Func<(int Engines, int Borrowed, int Fronts, int Machines), bool> p) => seen.FindIndex(c => p(c));
        int frontsGone = FirstWhen(c => c.Fronts == 0);
        int machinesDown = FirstWhen(c => c.Machines < machines);
        int borrowedDown = FirstWhen(c => c.Borrowed < 12);
        int enginesDown = FirstWhen(c => c.Engines < ClientAudioSystem.EngineVoiceBudget);
        _o.WriteLine($"fronts gone at {frontsGone}, machines first down at {machinesDown}, borrowed at {borrowedDown}, engines at {enginesDown}");
        Assert.True(frontsGone >= 0 && machinesDown > frontsGone, "machines were given up before the front voices");
        Assert.True(borrowedDown > machinesDown, "borrowed voices were given up before the machines");
        Assert.True(enginesDown > borrowedDown, "cars were given up before the borrowed voices");
        var end = seen[^1];
        Assert.Equal(1, end.Machines);
        Assert.Equal(4, end.Borrowed);
        Assert.Equal(2, end.Engines);

        // The load falls: the cars come back first.
        h.Mixer.Load = 0.2f;
        var before = seen[^1];
        for (int s = 0; s < 3; s++) { h.Tick(); h.Wait(1.05); h.Tick(); }
        var after = Counts(h, cars, machines);
        _o.WriteLine($"recovering: {after}");
        Assert.True(after.Engines > before.Engines, "the cars were not the first thing taken back");
        Assert.Equal(before.Machines, after.Machines);
        Assert.Equal(before.Fronts, after.Fronts);
    }

    /// <summary>
    /// During a load the mixer's reading is a lie (it pins while the thread is stalled), so for the
    /// first seconds after NoteSceneLoading nothing is given up; and after that one bad reading is not
    /// an overload: the ceiling has to be exceeded for three quarters of a second together.
    /// </summary>
    [Fact]
    public void NothingIsGivenUpWhileASceneLoadsOrForOneBadReading()
    {
        var h = LoadedField(out int cars, out int machines);
        var start = Counts(h, cars, machines);

        h.Audio.NoteSceneLoading();
        h.Mixer.Load = 1f;
        for (int s = 0; s < 5; s++) { h.Wait(0.5); h.Tick(); }      // 2.5 s of the 3 s hold
        Assert.Equal(start, Counts(h, cars, machines));

        h.Wait(2.0);
        // Spikes, each shorter than the 0.75 s it takes to count, with normal readings in between.
        for (int s = 0; s < 10; s++)
        {
            h.Mixer.Load = 1f; h.Tick(); h.Wait(0.3); h.Tick();
            h.Mixer.Load = 0.5f; h.Tick(); h.Wait(0.9); h.Tick();
        }
        Assert.Equal(start, Counts(h, cars, machines));
    }

    // ── Woods heard as one ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Past the hand-over a tree is not a voice of its own: a wood of them is one source whose synth
    /// renders as many trees as it stands for. Walk into the wood and the nearest trees take voices of
    /// their own, and leave the wood: it then stands for fewer trees, and the wood's voice is still
    /// placed as the wood (its key, its middle, its size).
    /// </summary>
    [Fact]
    public void ADistantWoodIsOneVoiceAndItsNearTreesLeaveItAsYouWalkIn()
    {
        using var wind = WindField.Hold(WindWeather.Steady(5f, 270f, 0f));
        var h = new ClientAudioHarness();
        var trees = new List<int>();
        const int Side = 5;
        for (int i = 0; i < Side * Side; i++)
        {
            int id = 400 + i;
            AddSource(h, id, "foliage:park_tree", new Vector3(1000f + (i % Side) * 8f, 7f, 1000f + (i / Side) * 8f));
            trees.Add(id);
        }
        Assert.Empty(h.World.RefreshWoods());
        var snapshot = h.World.GetSnapshot();
        Assert.NotNull(snapshot.Woods);
        var wood = Assert.Single(snapshot.Woods!.Woods);
        Assert.True(ClientWorldState.IsWood(wood.Id));

        // 250 m off: the wood, and no tree.
        h.StandAt(new Vector3(1016f, 0f, 1016f - 250f));
        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(wood.Id), 30), "the wood was never voiced");
        h.Tick(5);
        Assert.DoesNotContain(trees, h.Mixer.WasStarted);
        var far = h.Mixer.Latest[wood.Id];
        _o.WriteLine($"from 250 m: the wood stands for {far.Trees:F2} trees, gain {far.Volume:F4}, key {far.PhysicalKey}");
        Assert.Equal(trees.Count, far.Trees, 2);
        Assert.Equal(wood.Key, far.PhysicalKey);
        Assert.Equal(wood.Extent, far.ExtentMetres, 3);

        // In the middle of it: the nearest trees have their own voices, and the wood stands for the rest.
        h.StandAt(new Vector3(1016f, 0f, 1016f));
        h.Wait(3.0);
        h.Tick(30);
        int voiced = trees.Count(h.Mixer.Live.Contains);
        var near = h.Mixer.Latest[wood.Id];
        _o.WriteLine($"inside: {voiced} trees voiced, the wood stands for {near.Trees:F2}");
        // The wood holds a voice of the machine budget itself; its nearest trees have the rest.
        Assert.Contains(wood.Id, h.Mixer.Live);
        Assert.Equal(Math.Min(ClientAudioSystem.MachineVoiceBudget - 1, trees.Count), voiced);
        Assert.Equal(trees.Count - voiced, near.Trees, 1);
        // A tree within the hand-over has the whole of itself in its own voice.
        int nearest = trees.OrderBy(id => Vector3.Distance(h.World.GetSnapshot().Entities[id].Transform.Position, new Vector3(1016f, 1.7f, 1016f))).First();
        Assert.Contains(nearest, h.Mixer.Live);
    }

    // ── Rain ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rain on an open street: each far patch is a voice where its patch is, on its own side of the
    /// listener, carrying its own path; and the drops close by are played one by one where they land
    /// (on the ground within the survey's near rings, or on the listener's own head and shoulders),
    /// each placed by the drop law (DropBank.Placement: with no measured field level, Loudness.Place
    /// of the drop's own level).
    /// </summary>
    [Fact]
    public void RainIsPlacedWhereItLandsPatchesOnTheirSidesAndDropsOneByOne()
    {
        var h = new ClientAudioHarness();
        try
        {
            h.AddWall(9100, new Vector3(0f, -0.05f, 0f), new Vector3(200f, 0.1f, 200f), "Asphalt");
            h.StandAt(Vector3.Zero);
            h.World.UpdateAtmosphere(new WorldStateUpdate
            {
                Temperature = 12f, Humidity = 0.9f, AirPressure = 1013.25f, AirAbsorptionMultiplier = 1f,
                PrecipitationKind = (int)PrecipitationKind.Rain, RainRateMmPerHour = Rainfall.HeavyRate,
                PrecipitationIntensity = 0.8f,
            });
            int[] far = { 5, 6, 7, 8 };
            bool NearDrop(SpatialEmitter e) => e.EntityId <= RainField.NearVoiceBase && e.EntityId > RainField.NearVoiceBase - RainField.NearVoicePool;
            Assert.True(h.TickUntil(() => far.All(s => h.Mixer.Latest.ContainsKey(RainField.VoiceBase - s))
                                       && h.Mixer.Started.Count(NearDrop) >= 5, 5000, TimeSpan.FromSeconds(90)),
                        "the rain was never voiced");

            var ear = new Vector3(0f, h.Player.EyeHeight, 0f);
            // East, north, west, south: x east, z north.
            Func<Vector3, bool>[] side = { p => p.X > MathF.Abs(p.Z), p => p.Z > MathF.Abs(p.X), p => -p.X > MathF.Abs(p.Z), p => -p.Z > MathF.Abs(p.X) };
            for (int k = 0; k < far.Length; k++)
            {
                var e = h.Mixer.Latest[RainField.VoiceBase - far[k]];
                float d = new Vector2(e.Position.X, e.Position.Z).Length();
                _o.WriteLine($"slot {far[k]}: at {e.Position}, {d:F1} m, {e.Volume:F4} at {e.MinDistance:F2} m");
                Assert.True(side[k](e.Position), $"slot {far[k]} is not on its own side: {e.Position}");
                Assert.InRange(d, RainSurvey.RingEdges[RainSurvey.NearRings], RainSurvey.RingEdges[^1]);
                Assert.Equal(RainFeeds.Key(far[k]), e.PhysicalKey);
                Assert.True(e.CarriesPath);
                Assert.True(e.Volume > 0f && float.IsFinite(e.Volume));
            }

            var drops = h.Mixer.Started.Where(NearDrop).ToList();
            _o.WriteLine($"{drops.Count} near drops");
            foreach (var e in drops)
            {
                float horizontal = new Vector2(e.Position.X, e.Position.Z).Length();
                bool onGround = MathF.Abs(e.Position.Y) < 0.2f && horizontal <= RainSurvey.RingEdges[NearDrops.NearRings] + 0.1f;
                bool onYou = horizontal < 0.5f && e.Position.Y > 1f && e.Position.Y < ear.Y + 0.4f;
                Assert.True(onGround || onYou, $"a drop landed at {e.Position}, neither on the ground near you nor on you");
                Assert.True(e.IsEvent);
                Assert.True(float.IsFinite(e.EarLevelDb) && e.EarLevelDb > 0f);
                var (gain, reference) = Loudness.Place(e.EarLevelDb);
                Assert.Equal(MathF.Min(1f, gain), e.Volume, 5);
                Assert.Equal(reference, e.MinDistance, 4);
                Assert.InRange(e.DelayMs, 0f, 600f);
            }
        }
        finally
        {
            h.Audio.LeaveWorld(Array.Empty<int>());
            Runoff.Reset();
        }
    }

    // ── Other emitters ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A repeating one-shot (a PA, a foghorn) is handed over only when its interval comes round: its
    /// first firing staggered by its own id, then once an interval, and never over itself while the
    /// last one is still playing.
    /// </summary>
    [Fact]
    public void ARepeatingEmitterSpeaksOnceAnIntervalAndNeverOverItself()
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        const int Pa = 23;                 // 23 % 7 = 2: the first firing 1.8 s after first sight
        var def = new EntityDefinition
        {
            EntityId = Pa,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0f, 4f, 10f), Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        def.SoundEmitter.SoundId = "announcements/test_line";
        def.SoundEmitter.Mode = PlaybackMode.Single;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.MinDistance = 2f;
        def.SoundEmitter.Range = 80f;
        def.SoundEmitter.RepeatIntervalSeconds = 10f;
        h.World.RegisterDefinition(def);

        int Starts() => h.Mixer.Started.Count(e => e.EntityId == Pa);
        h.Tick();
        Assert.Equal(0, Starts());
        h.Wait(1.0); h.Tick();
        Assert.Equal(0, Starts());
        h.Wait(1.0); h.Tick();
        Assert.Equal(1, Starts());

        // Ten seconds on, but the last one is still playing: not again.
        h.Wait(10.0); h.Tick();
        Assert.Equal(1, Starts());
        // It finished (and the budget has seen it finish); at the next interval it speaks again, once.
        h.Mixer.Live.Remove(Pa);
        h.Tick(2);
        h.Wait(10.0); h.Tick();
        h.Tick(5);
        Assert.Equal(2, Starts());
    }

    /// <summary>
    /// An authored beacon whose category the map forbids is not heard, and one already playing is
    /// stopped. Through the map's policy, not the player's switches (which are written to disk).
    /// </summary>
    [Fact]
    public void ABeaconWhoseCategoryIsForbiddenIsStopped()
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        const int Beacon = 31;
        var def = new EntityDefinition
        {
            EntityId = Beacon,
            Type = EntityType.Beacon,
            Transform = new Transform { Position = new Vector3(3f, 1f, 3f), Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        def.Identity.BeaconCategory = Beacons.Waypoint;
        def.SoundEmitter.SoundId = "beacons/waypoint";
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.MinDistance = 1f;
        def.SoundEmitter.Range = 40f;
        h.World.RegisterDefinition(def);
        h.Audio.Beacons.SetMapPolicy(new[] { Beacons.Waypoint + "=forced_on" });
        Assert.True(h.TickUntil(() => h.Mixer.Live.Contains(Beacon), 10), "the beacon never played");

        h.Audio.Beacons.SetMapPolicy(new[] { Beacons.Waypoint + "=forbidden" });
        h.Tick(2);
        Assert.Contains(Beacon, h.Mixer.Stopped);
        Assert.DoesNotContain(Beacon, h.Mixer.Live);
        int starts = h.Mixer.Started.Count(e => e.EntityId == Beacon);
        h.Tick(5);
        Assert.Equal(starts, h.Mixer.Started.Count(e => e.EntityId == Beacon));
    }

    /// <summary>A car id whose siren controller starts a call promptly (as SirenApparentTests).</summary>
    private static int PromptCallId()
    {
        for (int id = 1; id < 20000; id++)
        {
            var c = new SirenController(id);
            for (int k = 0; k < 400; k++)
                if (c.Update(17f, 1f / 240f) != SirenMode.Off) return id;
        }
        throw new InvalidOperationException("no car id starts a call promptly");
    }

    /// <summary>
    /// A siren is placed every frame whatever its car's engine is doing, and when the car leaves the
    /// world its siren stops with it, rather than wailing on where the car was.
    /// </summary>
    [Fact]
    public void ASirenStopsWhenItsCarLeavesTheWorld()
    {
        int car = PromptCallId();
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        h.AddCar(car, "police_interceptor", new Vector3(0f, 0f, 30f), new Vector3(17f, 0f, 0f));
        int siren = ClientAudioSystem.SirenVoiceBase - car;
        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(siren), 1200), "the siren never sounded");
        var spec = SirenSpec.ByName(MachineRegistry.VehicleFor("police_interceptor").Siren!);
        var (gain, reference) = Loudness.Place(spec.SourceLevelDb, spec.HornMouthMetres);
        Assert.Equal(reference, h.Mixer.Latest[siren].MinDistance, 4);
        Assert.Equal(gain, h.Mixer.Latest[siren].Volume, 5);

        foreach (int id in h.World.RemoveEntities(new[] { car })) h.Audio.ForgetEntity(id);
        h.Tick(3);
        Assert.Contains(siren, h.Mixer.Stopped);
        Assert.DoesNotContain(siren, h.Mixer.Live);
    }
}
