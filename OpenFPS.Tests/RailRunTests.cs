using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Trains running and crossings closing, ticked the way the server ticks them.
///
/// Everything else about rail is checked as sound: the bogies, the horn, the bell. These run
/// RailSystem and CrossingSystem themselves on a small loop laid onto the default map, so what is
/// held is where a train goes, where it stops, where its sources are, and when the road may cross.
/// </summary>
public class RailRunTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-rail-{Guid.NewGuid():N}");
    public RailRunTests(ITestOutputHelper o) => _o = o;
    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    private const string Track = "test_loop";
    private const float Dt = PhysicsConstants.FixedDeltaTime;

    // ── Crossings ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The road reopens when the BACK of the train is clear, not the front. It used to measure the
    /// head against the clearance, so a 55 m tram with 30 m of clearance still had its rear on the
    /// road when the bells stopped; only the six seconds a crossing stays shut hid it, and only for a
    /// train doing more than about 3.3 m/s.
    /// </summary>
    [Fact]
    public void A_crossing_stays_closed_until_the_tail_is_clear()
    {
        var f = new Fixture(_dir, topSpeedKmh: 8f, startMetres: 150f, crossingAtMetres: 300f, clearMetres: 30f);
        float length = TrainProfile.ByName("light_rail").LengthMetres;
        Assert.True(length > 40f, $"the light rail set is {length:F0} m");

        bool closedOnce = false, reopened = false;
        float tailPastWhenReopened = float.NaN;
        for (int i = 0; i < (int)(400 / Dt) && !reopened; i++)
        {
            f.Tick();
            float head = f.Head();
            float tailPast = head - length - f.CrossingAt;
            bool closed = f.Crossings.IsClosedAt(f.MapId, f.CrossingPosition);
            if (closed) closedOnce = true;
            else if (closedOnce) { reopened = true; tailPastWhenReopened = tailPast; }
            // From the moment the front reaches the crossing until the back is the clearance past it,
            // the road is held. Two metres of slack for where the crossing system finds itself round
            // the line, which it samples every two metres.
            if (head > f.CrossingAt && tailPast < 30f - 2f)
                Assert.True(closed, $"open with the tail {tailPast:F1} m past the crossing (head {head:F0} m)");
        }
        Assert.True(reopened, "the crossing never reopened");
        _o.WriteLine($"reopened with the tail {tailPastWhenReopened:F1} m past");
        Assert.True(tailPastWhenReopened >= 28f, $"reopened with the tail only {tailPastWhenReopened:F1} m past");
    }

    /// <summary>
    /// A train blows for a crossing on its OWN horn and rings its own bell: the signal sources are
    /// placed on the train with the rest, and what is sent names the train and comes from its horn.
    /// Resonance found it (2026-10-06): the horn, whistle and bell sources were never spawned and the
    /// crossing was sounded with Honk, the road vehicle's horn, on the leading bogie.
    /// </summary>
    [Fact]
    public void A_train_sounds_its_own_horn_and_bell_for_a_crossing()
    {
        var f = new Fixture(_dir, topSpeedKmh: 40f, startMetres: 50f, crossingAtMetres: 400f, clearMetres: 30f);
        var layout = TrainLayout.Sources(TrainProfile.ByName("light_rail"));
        int horn = TrainSignal.WarningSource(layout), bell = TrainSignal.BellSource(layout);
        Assert.True(horn >= 0 && bell >= 0, "the light rail set has a horn and a bell");

        var placed = f.Sources().Select(s => s.Index).ToHashSet();
        Assert.Contains(horn, placed);
        Assert.Contains(bell, placed);

        for (int i = 0; i < (int)(60 / Dt) && f.Heard.Count == 0; i++) f.Tick();
        Assert.Single(f.Heard);
        var (source, label, sounds) = f.Heard[0];
        Assert.True(TrainSignal.TryParse(sounds[0].SynthKey, out string train, out var warning, out float bellSeconds),
                    $"sent '{sounds[0].SynthKey}', not the train's own signal");
        Assert.Equal("light_rail/Test_Tram", train);
        Assert.Equal(7, warning.Length);                          // long, long, short, long
        Assert.InRange(bellSeconds, 15f, 19f);                    // rung from the first blast to the crossing
        Assert.False(Honk.TryParse(sounds[0].SynthKey, out _, out _));

        // From the horn itself, where it is on the train.
        string? sid = null;
        Vector3 at = default;
        f.World.Query(new QueryDescription().WithAll<Transform, SoundEmitterComponent>(), (Entity e, ref Transform t, ref SoundEmitterComponent em) =>
        {
            if (e.Id == source) { sid = em.SoundId; at = t.Position; }
        });
        Assert.EndsWith("/" + horn, sid);
        Assert.True(Vector3.Distance(at, sounds[0].Position) < 0.01f);
        _o.WriteLine($"{label}: {sounds[0].SynthKey} from {sid}");
    }

    // ── Sources ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every source rides at its own height above the rail, wherever the rail goes. The old
    /// placement worked out to "keep the old absolute height", so on a slope a bogie stayed at the
    /// height it was spawned at until it was six metres out, then snapped to half a metre.
    /// </summary>
    [Fact]
    public void Every_source_keeps_its_height_above_a_sloping_rail()
    {
        // The rail climbs one in twenty eastward: fifteen metres between the two sides of the loop.
        var f = new Fixture(_dir, topSpeedKmh: 40f, startMetres: 0f, slope: 0.05f);
        var layout = TrainLayout.Sources(TrainProfile.ByName("light_rail"));

        for (int s = 0; s < 60; s++)
        {
            for (int i = 0; i < (int)(1f / Dt); i++) f.Tick();
            foreach (var (index, at) in f.Sources())
            {
                float rail = 0.05f * at.X;
                Assert.True(MathF.Abs(at.Y - rail - layout[index].HeightMetres) < 0.05f,
                    $"after {s + 1} s, source {index} ({layout[index].Label}) is {at.Y - rail:F2} m above the rail, "
                  + $"not {layout[index].HeightMetres:F2}");
            }
        }
    }

    // ── Stops ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A line with one platform: the train stops, stands, and goes on round. With one stop the next
    /// stop after it is itself, and the train was still standing at it at zero speed, so it began
    /// dwelling again and never left.
    /// </summary>
    [Fact]
    public void A_train_leaves_the_only_platform_on_its_line()
    {
        var f = new Fixture(_dir, topSpeedKmh: 30f, startMetres: 50f,
                            stops: new[] { new TrackStopData { AtMetres = 150f, DwellSeconds = 5f, Kind = "platform" } });
        var stands = f.RunAndRecordStands(240f);
        foreach (var (at, seconds) in stands) _o.WriteLine($"stood {seconds:F1} s at {at:F0} m");
        Assert.NotEmpty(stands);
        Assert.InRange(stands[0].At, 147f, 152f);
        Assert.True(stands[0].Seconds < 10f, $"stood {stands[0].Seconds:F0} s at a 5 s platform");
        Assert.True(f.Distance > 300f, $"went {f.Distance:F0} m in four minutes");
    }

    /// <summary>
    /// A train placed past the first platform stops at the next one ahead of it, rather than running
    /// a lap to the first one in the list and passing everything on the way.
    /// </summary>
    [Fact]
    public void A_train_stops_first_at_the_platform_ahead_of_it()
    {
        var f = new Fixture(_dir, topSpeedKmh: 30f, startMetres: 200f,
                            stops: new[]
                            {
                                new TrackStopData { AtMetres = 100f, DwellSeconds = 5f, Kind = "platform" },
                                new TrackStopData { AtMetres = 400f, DwellSeconds = 5f, Kind = "platform" },
                            });
        var stands = f.RunAndRecordStands(120f);
        foreach (var (at, seconds) in stands) _o.WriteLine($"stood {seconds:F1} s at {at:F0} m");
        Assert.NotEmpty(stands);
        Assert.InRange(stands[0].At, 397f, 402f);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The default map with a loop of track laid onto it: a circle 150 m across the middle, 942 m
    /// round, one light rail set on it, and a crossing if asked for. Built through the map data the
    /// way a map file would declare it, then spawned and ticked in the server's order.
    /// </summary>
    private sealed class Fixture
    {
        public readonly MapManager Maps;
        public readonly RailSystem Rail = new();
        public readonly CrossingSystem Crossings;
        public readonly string MapId = "default";
        public readonly World World;
        public readonly float CrossingAt;
        public readonly Vector3 CrossingPosition;
        public float Distance;
        private float _lastHead;
        /// <summary>What the trains sent to be heard: the source entity, the label and the sounds.</summary>
        public readonly List<(int Source, string Label, IReadOnlyList<TransientSound> Sounds)> Heard = new();

        public Fixture(string dir, float topSpeedKmh, float startMetres, float slope = 0f,
                       float crossingAtMetres = -1f, float clearMetres = 30f, TrackStopData[]? stops = null)
        {
            string mapDir = Path.Combine(dir, "maps");
            Directory.CreateDirectory(mapDir);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "default.json"),
                      Path.Combine(mapDir, "default.json"), overwrite: true);
            Maps = new MapManager(new MapRepository(mapDir),
                                  new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
            Maps.Initialize();
            Assert.True(Maps.TryGetMap(MapId, out World, out _, out _, out _));
            Assert.True(Maps.TryGetMapData(MapId, out var data));

            const float radius = 150f;
            var waypoints = new List<Vector3>();
            for (int i = 0; i < 48; i++)
            {
                float a = i * MathF.Tau / 48f;
                float x = radius * MathF.Cos(a), z = radius * MathF.Sin(a);
                waypoints.Add(new Vector3(x, slope * x, z));
            }
            data.Tracks = new List<TrackData>
            {
                new() { Id = Track, Waypoints = waypoints, Stops = stops?.ToList() ?? new() },
            };
            data.Trains = new List<TrainData>
            {
                new() { Name = "Test Tram", Preset = "light_rail", Track = Track, TopSpeedKmh = topSpeedKmh,
                        StartOffsetMetres = startMetres },
            };
            Rail.Spawn(Maps);
            Rail.Heard = (_, source, label, sounds) => Heard.Add((source, label, sounds));
            var line = Rail.Lines(MapId).Single().Line;

            if (crossingAtMetres >= 0f)
            {
                line.Sample(crossingAtMetres, out var at, out _, out _);
                CrossingAt = crossingAtMetres;
                CrossingPosition = at;
                data.Crossings = new List<LevelCrossingData>
                {
                    new() { Name = "Test Crossing", Position = at, ClearMetres = clearMetres },
                };
            }
            Crossings = new CrossingSystem(Rail);
            Rail.CrossingsOn = Crossings.PositionsOn;
            Crossings.Spawn(Maps);
            _lastHead = Head();
        }

        public float Head() => Rail.HeadsOn(MapId, Track, out _).Single();

        public void Tick()
        {
            Rail.Update(MapId, World, Dt);
            Crossings.Update(MapId, World, Dt);
            Rail.HeadsOn(MapId, Track, out float lap);
            float head = Head();
            float moved = head - _lastHead;
            if (moved < -lap * 0.5f) moved += lap;
            Distance += moved;
            _lastHead = head;
        }

        /// <summary>Runs for a while and says where the train stood still, and for how long.</summary>
        public List<(float At, float Seconds)> RunAndRecordStands(float seconds)
        {
            var stands = new List<(float At, float Seconds)>();
            float still = 0f;
            for (int i = 0; i < (int)(seconds / Dt); i++)
            {
                float before = Distance;
                Tick();
                if (Distance - before < 1e-4f) still += Dt;
                else
                {
                    if (still > 1f) stands.Add((_lastHead, still));
                    still = 0f;
                }
            }
            if (still > 1f) stands.Add((Head(), still));
            return stands;
        }

        /// <summary>Every spawned source of the train: its index in the layout, and where it is.</summary>
        public List<(int Index, Vector3 At)> Sources()
        {
            var found = new List<(int, Vector3)>();
            World.Query(new QueryDescription().WithAll<Transform, SoundEmitterComponent>(),
                (ref Transform t, ref SoundEmitterComponent em) =>
                {
                    if (em.SoundId == null || !em.SoundId.StartsWith("rail:")) return;
                    found.Add((int.Parse(em.SoundId[(em.SoundId.LastIndexOf('/') + 1)..]), t.Position));
                });
            Assert.NotEmpty(found);
            return found;
        }
    }
}
