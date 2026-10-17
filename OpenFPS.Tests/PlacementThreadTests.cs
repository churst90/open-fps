using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;
using OpenFPS.Geometry;

namespace OpenFPS.Tests;

/// <summary>
/// Nothing that takes longer than a frame runs on the threads that place sounds (PlacementThread). A
/// train's first horn for a crossing once made the train's model, a 440 ms calibration, where every
/// source is placed, and the street stood still for 0.8 s; a siren's head, a bell, a horn and an engine
/// each held it 10-100 ms the same way, and a new map's routes through its openings 200 ms more.
/// </summary>
public class PlacementThreadTests
{
    private readonly ITestOutputHelper _o;
    public PlacementThreadTests(ITestOutputHelper o) => _o = o;

    private const int Rate = 48000;

    private static SpatialEmitter Voice(int id, string physicalKey, int seed)
        => new() { EntityId = id, IsSynth = true, PhysicalKey = physicalKey, Seed = seed, EngineRunning = true };

    [Fact]
    public void AFirstHornBellSirenEngineAndTrainAreMadeOffThePlacementThread()
    {
        var builds = new VoiceBuilds(new StopwatchAudioClock());
        var voices = new (SpatialEmitter E, ModelSynth Kind)[]
        {
            (Voice(1, "siren:patrol", 11), ModelSynth.Siren),
            (Voice(2, "bell:crossing_gong", 12), ModelSynth.Bell),
            (Voice(3, Honk.Key("air:bus_horn", new[] { 0.4f, 0.2f, 0.4f }), 13), ModelSynth.Horn),
        };
        var engine = new SpatialEmitter { EntityId = 4, IsSynth = true, EngineKey = "i4_economy", Seed = 14 };
        var (before, _) = PlacementThread.HeavyOnThisThread;
        using (PlacementThread.Enter())
        {
            // Asked for where sounds are placed: each is being made, and asking took no time to speak of.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var (e, kind) in voices)
                Assert.Equal(VoiceBuilds.Taken.Making, builds.Take(e, kind, Rate, out _, out _));
            Assert.Equal(VoiceBuilds.Taken.Making, builds.TakeEngine(engine, Rate, out _, out _));
            Assert.Null(builds.Train("freight", "Guard_train", Rate));
            double askedMs = clock.Elapsed.TotalMilliseconds;
            _o.WriteLine($"asked for five models in {askedMs:F1} ms");

            builds.WaitAll();
            foreach (var (e, kind) in voices)
            {
                Assert.Equal(VoiceBuilds.Taken.Made, builds.Take(e, kind, Rate, out var state, out _));
                Assert.NotNull(state);
            }
            Assert.Equal(VoiceBuilds.Taken.Made, builds.TakeEngine(engine, Rate, out var made, out _));
            Assert.NotNull(made);
            Assert.NotNull(builds.Train("freight", "Guard_train", Rate));
        }
        var (after, last) = PlacementThread.HeavyOnThisThread;
        Assert.True(after == before, $"{after - before} model(s) made on the placement thread, the last: {last}");
    }

    [Fact]
    public void AModelMadeAheadIsThereTheMomentItsSignalComes()
    {
        var builds = new VoiceBuilds(new StopwatchAudioClock());
        var bell = Voice(7, "bell:crossing_gong", 21);
        builds.Prepare(ModelSynth.Bell, bell.PhysicalKey, bell.Seed, Rate);
        builds.WaitAll();
        using (PlacementThread.Enter())
            Assert.Equal(VoiceBuilds.Taken.Made, builds.Take(bell, ModelSynth.Bell, Rate, out var state, out _));
    }

    /// <summary>Made off the thread or on it, a model is the same model: its renders are bit for bit.</summary>
    [Fact]
    public void AModelMadeOffTheThreadRendersAsOneMadeInPlace()
    {
        var builds = new VoiceBuilds(new StopwatchAudioClock());
        var cases = new (SpatialEmitter E, ModelSynth Kind)[]
        {
            (Voice(1, "siren:patrol", 31), ModelSynth.Siren),
            (Voice(2, "bell:crossing_gong", 32), ModelSynth.Bell),
            (Voice(3, Honk.Key("electric:disc_single", new[] { 0.3f, 0.1f, 0.3f }), 33), ModelSynth.Horn),
        };
        foreach (var (e, kind) in cases) builds.Take(e, kind, Rate, out _, out _);
        builds.WaitAll();
        foreach (var (e, kind) in cases)
        {
            builds.Take(e, kind, Rate, out var off, out _);
            var inPlace = VoiceBuilds.Make(kind, e.PhysicalKey, e.Seed, 0f, Rate, new StopwatchAudioClock())!;
            if (off is SirenVoiceState s1 && inPlace is SirenVoiceState s2) s1.TargetMode = s2.TargetMode = (int)SirenMode.Wail;
            var a = new float[Rate / 2];
            var b = new float[Rate / 2];
            off!.Render(a);
            inPlace.Render(b);
            Assert.True(a.AsSpan().SequenceEqual(b), $"{e.PhysicalKey}: made off the thread it renders otherwise");
            Assert.Contains(a, x => x != 0f);
        }
    }

    /// <summary>The tripwire itself: a model made where sounds are placed is counted. Without it the guards
    /// above would pass whatever the provider did.</summary>
    [Fact]
    public void AModelMadeOnThePlacementThreadIsCounted()
    {
        var (before, _) = PlacementThread.HeavyOnThisThread;
        using (PlacementThread.Enter())
            _ = new SirenVoiceState(SirenSpec.ByName("patrol"), Rate);
        var (after, last) = PlacementThread.HeavyOnThisThread;
        Assert.Equal(before + 1, after);
        Assert.Equal("A siren's model", last);
        // Off it, nothing is counted.
        _ = new SirenVoiceState(SirenSpec.ByName("patrol"), Rate);
        Assert.Equal(after, PlacementThread.HeavyOnThisThread.Count);
    }

    /// <summary>
    /// The first path asked on a new map (a car's horn, a crossing's bell, before the worker has answered):
    /// where sounds are placed it makes no routes through the openings and waits for none. They are made
    /// in the background and the voice is heard without them for that moment; anywhere else the asker
    /// waits for them, as before.
    /// </summary>
    [Fact]
    public void AFirstPathOnANewMapMakesNoRoutesWhereSoundsArePlaced()
    {
        var client = new OpenFPS.Client.Core.ClientWorldState();
        client.Clear(new Vector3(400, 40, 400));
        client.SetAcousticMap(new AcousticMap(new Vector3(400, 40, 400), new Vector3(-200, -10, -200)) { GlobalEnvironmentId = AcousticConstants.GlobalRegionId });
        client.RegisterDefinition(new EntityDefinition
        {
            EntityId = 90, Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0f, 1.5f, 10f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(20f, 3f, 0.3f), IsSolid = true },
            Material = new MaterialComponent { Material = "Brick" },
        });
        var world = client.GetSnapshot();
        var acoustics = new OpenFPS.Client.AudioEngine.Acoustics.SpatialAcoustics();
        var ear = new Vector3(0f, 1.7f, 20f);
        var (before, _) = PlacementThread.HeavyOnThisThread;
        using (PlacementThread.Enter())
        {
            acoustics.CalculateAcousticPath(world, 701, ear, new Vector3(0f, 0.5f, 0f));
            // Null while the graph is being made, or the graph if the worker has already finished it: one wall
            // takes under a millisecond, so which it is depends on the machine. What matters is the count below.
            acoustics.RoutesFor(world, ear);
        }
        var (after, last) = PlacementThread.HeavyOnThisThread;
        Assert.True(after == before, $"{after - before} piece(s) of heavy work where sounds are placed, the last: {last}");
        // Off that thread the asker waits for the graph being made, and has it.
        Assert.NotNull(acoustics.RoutesFor(world, ear));
    }
}
