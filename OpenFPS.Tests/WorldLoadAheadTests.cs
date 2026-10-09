using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.OneWorld;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Nobody waits at an edge that is not built (docs/WORLD_STREAMING.md, Building ahead): arriving waits on
/// the loading screen for the ring round you, and moving, the tiles you could reach soonest are made first.
/// The survey here answers on a clock the test turns, as slowly as 3DEP does on its slow runs, so minutes of
/// travel take a second or two.
/// </summary>
public class WorldLoadAheadTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-world-ahead-" + Guid.NewGuid().ToString("N"));

    public WorldLoadAheadTests(ITestOutputHelper o) => _o = o;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }

    /// <summary>A survey that answers each tile <see cref="Latency"/> seconds after it is asked, on a clock
    /// the test turns: gentle hills, like the other tests'.</summary>
    private sealed class SlowSurvey : IElevationSource
    {
        public string Name => "a slow survey";
        public double Now;
        public double Latency;
        public int Asked;
        private readonly List<(double Due, WorldTileKey Key, TaskCompletionSource<float[]?> Answer)> _waiting = new();

        public Task<float[]?> HeightsAsync(WorldTileKey key, int posts, double spacing, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<float[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiting) { _waiting.Add((Now + Latency, key, answer)); Asked++; }
            return answer.Task;
        }

        /// <summary>Answers everything due by now; how many.</summary>
        public int Answer()
        {
            List<(double Due, WorldTileKey Key, TaskCompletionSource<float[]?> Answer)> due;
            lock (_waiting)
            {
                due = _waiting.Where(w => w.Due <= Now + 1e-9).ToList();
                foreach (var d in due) _waiting.Remove(d);
            }
            foreach (var (_, key, answer) in due) answer.SetResult(Hills(key));
            return due.Count;
        }

        private static float[] Hills(WorldTileKey key)
        {
            int posts = WorldTileService.Posts;
            var h = new float[posts * posts];
            for (int j = 0; j < posts; j++)
                for (int i = 0; i < posts; i++)
                {
                    double e = key.Easting + i * WorldTileService.Spacing, n = key.Northing + j * WorldTileService.Spacing;
                    h[j * posts + i] = (float)(62 + 2 * Math.Sin(e / 90.0) + 1.5 * Math.Cos(n / 70.0));
                }
            return h;
        }
    }

    private sealed class Run
    {
        public required GameServer Server;
        public required MapManager Maps;
        public required SlowSurvey Survey;
        public required UserSession Player;
        public int Answered;

        /// <summary>One quarter of a second: the world's pass, then the clock, the survey's answers and the
        /// store's writes of them.</summary>
        public void Step(double dt = 0.25)
        {
            Server.WorldTickForTest();
            Survey.Now += dt;
            Answered += Survey.Answer();
            var service = Server.World!.Service;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (service.MadeCount + service.FailedCount < Answered)
            {
                Assert.True(clock.ElapsedMilliseconds < 10_000, "the store did not take the survey's answers");
                Thread.Sleep(1);
            }
        }
    }

    private static readonly WorldPlace Bobcat = new("bobcat", "31907 Bobcat Lane, Magnolia, Texas", 30.123703, -95.740935);

    private Run Arrive(double latency, StreamRadii radii, out double waited, Action<UserSession, OpenFPS.Common.Networking.IMessage>? sent = null)
    {
        string dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        string mapDir = Path.Combine(dir, "maps");
        Directory.CreateDirectory(mapDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "speedway.json"), Path.Combine(mapDir, "speedway.json"));
        var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        var sessions = new SessionManager();
        var server = new GameServer(new NoUsers());
        server.Attach(maps, sessions);
        if (sent != null) server.Sent = sent;
        var survey = new SlowSurvey { Latency = latency };
        server.StartWorld(new WorldSettings { StorePath = Path.Combine(dir, "world"), CapGigabytes = 1, Prebuild = false }, survey);
        var alice = new UserSession { ConnectionId = 1, Username = "alice", CurrentMapId = "speedway", Welcomed = true };
        alice.Tiles.Radii = radii;
        sessions.AddSession(1, alice);
        var run = new Run { Server = server, Maps = maps, Survey = survey, Player = alice };
        var epoch = DateTime.UtcNow;
        server.World!.Clock = () => epoch.AddSeconds(survey.Now);
        server.World.Arrive(alice, Bobcat, _ => { });
        double t0 = survey.Now;
        while (alice.Entity == Entity.Null)
        {
            Assert.True(survey.Now - t0 < 600, "never arrived");
            run.Step();
        }
        waited = survey.Now - t0;
        return run;
    }

    /// <summary>
    /// Arriving waits for the whole ring round where you will stand, out to the far radius, and then you
    /// stand in it: every tile within the far radius is there when the body is made.
    /// </summary>
    [Fact]
    public void Arriving_waits_for_the_ring_out_to_the_far_radius()
    {
        var progress = new List<OpenFPS.Common.Networking.WorldLoading>();
        var run = Arrive(latency: 3.0, StreamRadii.Medium, out double waited,
                         (s, m) => { if (m is OpenFPS.Common.Networking.WorldLoading w) progress.Add(w); });
        // Told on the loading screen how it goes: said at the start and every few seconds, shown every second.
        Assert.True(progress.Count >= 10, $"{progress.Count} progress messages");
        Assert.True(progress[0].Speak);
        Assert.StartsWith("Building the world: ", progress[0].Text);
        Assert.True(progress.Count(p => p.Speak) is > 3 and < 30);
        for (int i = 1; i < progress.Count; i++) Assert.True(progress[i].Done >= progress[i - 1].Done);
        Assert.Equal(progress[^1].Total, progress[^1].Done);
        Assert.True(run.Server.World!.TryGetFrame(run.Player.CurrentMapId, out var frame));
        Assert.True(run.Maps.TryGetTiles(frame.Id, out var tiles));
        Assert.True(run.Maps.TryGetMap(frame.Id, out var world, out _, out _, out _));
        var at = world.Get<Transform>(run.Player.Entity).Position;
        int ring = 0;
        for (int x = -6; x <= 6; x++)
            for (int z = -6; z <= 6; z++)
            {
                var k = new TileKey(x, z);
                if (k.DistanceFrom(at, tiles.TileMetres) > StreamRadii.Medium.FarMetres) continue;
                ring++;
                Assert.True(tiles.IsReady(k), $"tile {k} round the arrival is not there");
            }
        _o.WriteLine($"3 s a tile, two at a time: waited {waited:F1} s on the loading screen for {ring} tiles");
        Assert.InRange(ring, 35, 50);
    }

    /// <summary>
    /// The measurement for docs/WORLD_STREAMING.md: walking, running and driving in a straight line east from
    /// the arrival, with the survey answering in 3 s a tile (3DEP's slow run: 41 tiles in 60 s, two at a
    /// time), nobody is ever in a tile that is not there. Faster, the edge can still be met: the fastest
    /// speed that never meets it is reported, for medium and low detail.
    /// </summary>
    [Fact]
    public void A_walker_a_runner_and_a_car_never_reach_an_unbuilt_tile()
    {
        foreach (var (what, speed) in new[] { ("walker", PhysicsConstants.WalkSpeed), ("runner", PhysicsConstants.SprintSpeed), ("car", 30f) })
        {
            var met = Travel(3.0, StreamRadii.Medium, speed, 5000f, out var log);
            _o.WriteLine($"{what} at {speed:F1} m/s, 3 s a tile: {log}");
            Assert.Null(met);
        }
    }

    [Fact]
    public void The_speed_at_which_the_edge_can_be_met()
    {
        foreach (var (latency, radii, name) in new[] { (3.0, StreamRadii.Medium, "medium"), (3.0, StreamRadii.Low, "low"), (6.0, StreamRadii.Medium, "medium") })
        {
            float fastestSafe = 0f;
            foreach (float speed in new[] { 30f, 40f, 50f, 60f, 70f, 80f, 100f, 130f, 160f })
            {
                var met = Travel(latency, radii, speed, 5500f, out var log);
                _o.WriteLine($"{latency:F0} s a tile, {name} detail, {speed:F0} m/s ({speed * 3.6f:F0} km/h): {log}");
                if (met != null) break;
                fastestSafe = speed;
            }
            _o.WriteLine($"=> {latency:F0} s a tile at {name} detail: never met up to {fastestSafe:F0} m/s ({fastestSafe * 3.6f:F0} km/h)");
            Assert.True(fastestSafe >= 30f);
        }
    }

    /// <summary>Arrives, then goes east at <paramref name="speed"/> for <paramref name="metres"/>: the distance
    /// at which a tile not built was first met, or null.</summary>
    private float? Travel(double latency, StreamRadii radii, float speed, float metres, out string log)
    {
        var run = Arrive(latency, radii, out double waited);
        Assert.True(run.Maps.TryGetMap(run.Player.CurrentMapId, out var world, out _, out _, out _));
        Assert.True(run.Maps.TryGetTiles(run.Player.CurrentMapId, out var tiles));
        var e = run.Player.Entity;
        var start = world.Get<Transform>(e).Position;
        float gone = 0f, minAhead = float.MaxValue;
        const double dt = 0.25;
        while (gone < metres)
        {
            gone += speed * (float)dt;
            var at = start + new Vector3(gone, 0f, 0f);
            world.Get<Transform>(e).Position = at;
            world.Get<Velocity>(e).Linear = new Vector3(speed, 0f, 0f);
            run.Step(dt);
            if (!tiles.IsReady(TileKey.Of(at + new Vector3(PhysicsConstants.PlayerRadius, 0, 0), tiles.TileMetres)))
            {
                log = $"waited {waited:F0} s to arrive; MET AN UNBUILT TILE after {gone:F0} m ({gone / speed:F0} s), {run.Survey.Asked} tiles asked";
                return gone;
            }
            // How far the built ground reached ahead, at its nearest.
            float ahead = 0f;
            while (ahead < 3000f && tiles.IsReady(TileKey.Of(at + new Vector3(ahead, 0, 0), tiles.TileMetres))) ahead += 25f;
            minAhead = MathF.Min(minAhead, ahead);
        }
        log = $"waited {waited:F0} s to arrive; {metres:F0} m in {metres / speed:F0} s, built ground ahead never under {minAhead:F0} m, "
            + $"{run.Survey.Asked} tiles asked, {run.Server.World!.Service.QueuedCount} still queued";
        return null;
    }
}
