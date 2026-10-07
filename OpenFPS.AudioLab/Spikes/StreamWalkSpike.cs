using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using MemoryPack;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// A streamed map walked through the real server and the client's world and occlusion worker, with
/// Steam Audio, in real time (docs/WORLD_STREAMING.md, stage 1). The server streams tiles as the body
/// moves; the client files them, rebuilds its acoustic map in the background, and the worker rebuilds
/// its scene in the background. Reports what each costs, and how long the worker left its sources
/// without a fresh answer, before and while tiles change: the worker's own "placement stalled".
///
///   --stream-walk [map=magnolia_tx] [speed=15] [seconds=60] [detail=medium] [heading=east|north|west|south]
///                 [churn=N] [keep=old]
///
/// churn=N asks about N one-off sources a frame (a fraction: one every so many frames) besides the twenty that stay, each once and never again,
/// as footsteps and birds come and go; the managed heap after a full collection is said every ten
/// seconds. keep=old keeps route answers as they were kept before 2026-10-06 (RouteAnswers), for the
/// before-and-after of a stopped voice holding the graph it was asked of.
/// </summary>
public static class StreamWalkSpike
{
    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }

    public static int Run(string[] args)
    {
        string Arg(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..] ?? fallback;
        string mapId = Arg("map", "magnolia_tx");
        float speed = float.Parse(Arg("speed", "15"), System.Globalization.CultureInfo.InvariantCulture);
        double seconds = double.Parse(Arg("seconds", "60"), System.Globalization.CultureInfo.InvariantCulture);
        var radii = StreamRadii.Named(Arg("detail", "medium")) ?? StreamRadii.Default;
        float churn = float.Parse(Arg("churn", "0"), System.Globalization.CultureInfo.InvariantCulture);
        float churnDue = 0f;
        RouteAnswers.RetainSuperseded = Arg("keep", "") == "old";
        var heading = Arg("heading", "east") switch
        {
            "north" => new Vector3(0, 0, 1), "west" => new Vector3(-1, 0, 0), "south" => new Vector3(0, 0, -1), _ => new Vector3(1, 0, 0),
        };

        AcousticRegistry.Initialize();
        string dir = Path.Combine(Path.GetTempPath(), "openfps-stream-walk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "maps", "places"));
        File.Copy(OpenFPS.AudioLab.LabPaths.Server("maps", "places", mapId + ".json"), Path.Combine(dir, "maps", "places", mapId + ".json"));
        var maps = new MapManager(new MapRepository(Path.Combine(dir, "maps")), new PrefabRepository(OpenFPS.AudioLab.LabPaths.Server("prefabs")));
        maps.Initialize();
        Directory.Delete(dir, true);
        if (!maps.TryGetMap(mapId, out var world, out var size, out _, out _) || !maps.TryGetMapData(mapId, out var data))
        { Console.WriteLine($"FAIL: no map {mapId}"); return 1; }

        var sessions = new SessionManager();
        var server = new GameServer(new NoUsers());
        server.Attach(maps, sessions);
        var spawn = maps.GetSpawnPoint(mapId).Position;
        var body = world.Create(new PlayerComponent { ConnectionId = 1, Username = "walker" }, EntityType.Player,
            new Transform { Position = spawn, Rotation = Quaternion.Identity }, new Velocity(),
            new ColliderComponent { Shape = ColliderShape.Cylinder, Size = new Vector3(0.6f, 1.8f, 0.6f), IsSolid = true });
        maps.IndexEntity(mapId, body);
        var session = new UserSession { ConnectionId = 1, Username = "walker", Entity = body, CurrentMapId = mapId, Welcomed = true };
        sessions.AddSession(1, session);

        // The wire, both hooks: serialised and counted, as a socket would.
        var inbox = new ConcurrentQueue<IMessage>();
        long bytes = 0;
        void Wire(UserSession s, IMessage m)
        {
            var b = MemoryPackSerializer.Serialize(m);
            bytes += b.Length;
            inbox.Enqueue(MemoryPackSerializer.Deserialize<IMessage>(b)!);
        }
        server.Sent = Wire;
        server.Broadcasted = Wire;

        var client = new ClientWorldState();   // its refreshes on a niced thread of their own, as in the game
        client.Clear(size);
        client.ConfigureAcoustics(data.MinBound, data.VoxelResolution, data.OcclusionFloor, data.TileMetres);

        var clock = Stopwatch.StartNew();
        server.SendMapData(session, new MapDataRequest { MapName = mapId, FullDetailMetres = radii.FullMetres, FarMetres = radii.FarMetres });
        bool loaded = false;
        void Take(IMessage m)
        {
            switch (m)
            {
                case EntityDefinitionPack pack:
                    foreach (var d in pack.Unpack()!.Definitions) client.RegisterDefinition(d, deferAcoustics: loaded);
                    break;
                case EntityDefinition d: client.RegisterDefinition(d); break;
                case EntityRemoved r: client.RemoveEntities(r.EntityIds); break;
                case ServerStateUpdate u: client.SyncState(u); break;
                case TileStreamUpdate t:
                    client.NoteTiles(t.Tiles);
                    if (loaded) client.RequestAcousticRefresh();
                    break;
                case MapLoadComplete: loaded = true; break;
            }
        }
        while (inbox.TryDequeue(out var m)) Take(m);
        long joinBytes = bytes;
        var defsAtJoin = client.GetSnapshot().Entities.Values.Select(e => e.Definition).ToList();
        var map = ClientWorldState.BuildAcousticMap(defsAtJoin, size, data.MinBound, data.VoxelResolution, data.OcclusionFloor, streamed: true, report: false);
        client.SetAcousticMap(map);
        Console.WriteLine($"  join: {client.EntityCount} entities, {client.Tiles.Count} tiles, {joinBytes / 1024.0:F0} KB, {clock.ElapsedMilliseconds} ms to the acoustic map");

        var acoustics = new SpatialAcoustics();
        using var worker = new AsyncAcousticWorker(acoustics);
        worker.UpdateWorld(client.GetSnapshot());
        worker.Start();

        // Twenty sources round the walker, a few metres to forty away, asked about every frame as the
        // audio system asks: the worker's answers for them are what the mixer would place them by.
        var offsets = Enumerable.Range(0, 20).Select(i =>
        {
            float a = i * 2.4f, r = 4f + 2f * i;
            return new Vector3(MathF.Cos(a) * r, 1.2f, MathF.Sin(a) * r);
        }).ToArray();
        var lastResult = new object?[offsets.Length];
        var lastFresh = new double[offsets.Length];

        // Wait for the first scene.
        var settle = Stopwatch.StartNew();
        while (!worker.SteamAudioActive && settle.ElapsedMilliseconds < 5000) Thread.Sleep(20);
        Console.WriteLine($"  Steam Audio {(worker.SteamAudioActive ? "on" : "OFF (hand-rolled tracer)")}");

        var gameMs = new List<double>();
        var gapsQuiet = new List<double>();
        var gapsTiles = new List<double>();
        int tick = 0, refreshesSeen = 0, scenesSeen = 0;
        double tileChangeUntil = 0;
        var cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;
        double sceneMsAtStart = worker.SceneBuildMsTotal, onlyAtStart = worker.SceneOnlyMsTotal, routesAtStart = worker.RoutesMsTotal;
        var loop = Stopwatch.StartNew();
        double frame = 1.0 / 30.0;
        Vector3 at = spawn;
        int oneOff = 2_000_000;
        double nextHeap = 0;
        var heap = new List<(double T, double Mb)>();
        double HeapMb() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true) / 1048576.0; }
        while (loop.Elapsed.TotalSeconds < seconds + 3)
        {
            double now = loop.Elapsed.TotalSeconds;
            if (now < seconds) at = spawn + heading * (float)(speed * now);
            world.Get<Transform>(body).Position = at;
            server.BroadcastForTest(tick++);

            var g = Stopwatch.StartNew();
            bool tiles = false;
            while (inbox.TryDequeue(out var m)) { tiles |= m is EntityDefinitionPack or EntityRemoved or TileStreamUpdate; Take(m); }
            var snap = client.GetSnapshot();
            if (tiles) gameMs.Add(g.Elapsed.TotalMilliseconds);
            worker.UpdateWorld(snap);
            for (int i = 0; i < offsets.Length; i++)
                worker.EnqueueRequest(new AcousticRequest { EntityId = 900000 + i, ListenerPos = at + new Vector3(0, 1.7f, 0), SourcePos = at + offsets[i], SourceRadius = 0.3f });
            churnDue += churn;
            for (; churnDue >= 1f; churnDue -= 1f)
            {
                var o = offsets[oneOff % offsets.Length] * 1.5f;
                worker.EnqueueRequest(new AcousticRequest { EntityId = oneOff++, ListenerPos = at + new Vector3(0, 1.7f, 0), SourcePos = at + o, SourceRadius = 0.3f });
            }
            if (now >= nextHeap)
            {
                double mb = HeapMb();
                heap.Add((now, mb));
                Console.WriteLine($"  heap at t={now:F0}s ({(at - spawn).Length():F0} m): {mb:F0} MB after a full collection");
                nextHeap = now + 10;
            }

            if (client.AcousticRefreshes != refreshesSeen || worker.TileSceneBuilds != scenesSeen || client.AcousticRefreshPending)
                tileChangeUntil = now + 1.0;
            if (worker.TileSceneBuilds != scenesSeen)
                Console.WriteLine($"  t={now:F1}s at ({at.X:F0}, {at.Z:F0}): {client.EntityCount} entities, {client.Tiles.Count} tiles; acoustic refresh {client.LastAcousticRefreshMs:F0} ms, scene {worker.LastTileSceneBuildMs:F0} ms");
            refreshesSeen = client.AcousticRefreshes;
            scenesSeen = worker.TileSceneBuilds;

            for (int i = 0; i < offsets.Length; i++)
            {
                if (!worker.TryGetResult(900000 + i, out var paths)) continue;
                if (!ReferenceEquals(paths, lastResult[i]))
                {
                    if (lastResult[i] != null) (now < tileChangeUntil ? gapsTiles : gapsQuiet).Add((now - lastFresh[i]) * 1000);
                    lastResult[i] = paths; lastFresh[i] = now;
                }
            }
            double spent = loop.Elapsed.TotalSeconds - now;
            if (spent < frame) Thread.Sleep(TimeSpan.FromSeconds(frame - spent));
        }

        heap.Add((loop.Elapsed.TotalSeconds, HeapMb()));
        Console.WriteLine($"  heap: {heap[0].Mb:F0} MB at the start, {heap[^1].Mb:F0} MB at the end, most {heap.Max(h => h.Mb):F0} MB"
                          + $" ({(RouteAnswers.RetainSuperseded ? "answers kept as before" : "answers let go with their graph")}, {churn} one-off sources a frame)");
        string Stats(List<double> v) { if (v.Count == 0) return "none"; v.Sort(); return $"median {v[v.Count / 2]:F0} ms, 99th {v[(int)(v.Count * 0.99)]:F0} ms, worst {v[^1]:F0} ms ({v.Count})"; }
        Console.WriteLine($"  walked {(at - spawn).Length():F0} m at {speed} m/s: {(bytes - joinBytes) / 1024.0:F0} KB after the join, " +
                          $"{client.AcousticRefreshes} acoustic refreshes (last {client.LastAcousticRefreshMs:F0} ms), {worker.TileSceneBuilds} scene rebuilds (last {worker.LastTileSceneBuildMs:F0} ms)");
        Console.WriteLine($"  game thread, frames with tile messages: {Stats(gameMs)}");
        Console.WriteLine($"  worker answer gaps, quiet: {Stats(gapsQuiet)}");
        Console.WriteLine($"  worker answer gaps, while tiles change: {Stats(gapsTiles)}");
        double wall = loop.Elapsed.TotalSeconds;
        double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuAtStart).TotalSeconds;
        Console.WriteLine($"  scene work {(worker.SceneBuildMsTotal - sceneMsAtStart) / wall:F0} ms a second ({worker.TileSceneBuilds} builds): " +
                          $"Steam Audio scenes {(worker.SceneOnlyMsTotal - onlyAtStart) / wall:F0} ms a second, routes {(worker.RoutesMsTotal - routesAtStart) / wall:F0}; " +
                          $"process CPU {cpu / wall * 100:F0} % of one core over {wall:F0} s");
        return 0;
    }
}
