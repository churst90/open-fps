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
