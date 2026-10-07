using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using OpenFPS.Common.Systems;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --path-probe [map=city] ear=x,y,z src=x,y,z [src=...] [open=R] [door=x,y,z ...] [legcost] [root=DIR/]
///
/// What the game's occlusion worker hands the mixer for one source and one ear on a real map: occlusion
/// and the three band gains in dB, with what each part of the answer says on its own — the one-shot
/// path (SpatialAcoustics, which footsteps, birds, speech and beacons use), the routes by the openings
/// (OpeningRoutes) with the openings they run through, and each wall on the straight line.
///
/// The map is loaded by the SERVER's loader and handed over as the client gets it — definitions, the
/// regions with their surveyed surfaces, the portals, each door's opening — so the routes have the
/// same graph they have in the game. Game coordinates (y up), not /tp's.
///
/// open=R measures every source again with every door leaf within R metres of the ear swung a quarter
/// turn, as a rebuild in the game does when one swings; door=x,y,z swings the one leaf nearest that point.
/// swings=N swings them open, shut, open... N times, measuring after each rebuild has been handed over
/// (with tile scenes, each swing lands in the other pair of top scenes). traced also says what the traced
/// reverb at the ear and the first source's traced echoes give the two ears each time (TracedBinaural).
/// legcost times the straight leg from each source to the ear instead: the one-box barrier search and the
/// whole leg, which is what a route query costs when its way to a door is blocked.
/// </summary>
public static class PathProbeSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string mapId = args.FirstOrDefault(a => a.StartsWith("map="))?[4..] ?? "city";
        static Vector3 P(string s) { var f = s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray(); return new Vector3(f[0], f[1], f[2]); }
        var ear = P(args.First(a => a.StartsWith("ear="))[4..]);
        var srcs = args.Where(a => a.StartsWith("src=")).Select(a => P(a[4..])).ToList();
        string root = args.FirstOrDefault(a => a.StartsWith("root="))?[5..] ?? AppContext.BaseDirectory;

        var sw = Stopwatch.StartNew();
        var world = LoadAsClient(root, mapId);
        Console.WriteLine($"  {mapId}: {world.Entities.Count} entities, {world.AcousticMap?.Regions.Count ?? 0} regions, "
                        + $"{world.AcousticMap?.Portals.Count ?? 0} portals ({sw.ElapsedMilliseconds} ms)");

        var acoustics = new SpatialAcoustics();
        using var worker = new AsyncAcousticWorker(acoustics);
        worker.UpdateWorld(world);
        worker.Start();
        Settle(worker, ear);
        Console.WriteLine($"  ear ({ear.X:F1}, {ear.Y:F1}, {ear.Z:F1}) in {RegionName(world, acoustics, ear)}; Steam Audio {(worker.SteamAudioActive ? "on" : "OFF")}");
        DescribeOpenings(acoustics, world, ear);
        if (args.Contains("legcost"))
        {
            var routes = acoustics.RoutesFor(world)!;
            foreach (var src in srcs)
            {
                var t = Stopwatch.StartNew(); int n = 0;
                while (t.ElapsedMilliseconds < 300) { routes.BarrierPathDifference(src, ear, out _, out _); n++; }
                double bar = t.Elapsed.TotalMilliseconds * 1000 / n;
                t.Restart(); n = 0;
                while (t.ElapsedMilliseconds < 300) { routes.LegGains(src, ear, Array.Empty<int>(), Array.Empty<int>()); n++; }
                double leg = t.Elapsed.TotalMilliseconds * 1000 / n;
                float d = routes.BarrierPathDifference(src, ear, out _, out bool v);
                Console.WriteLine($"  leg {src} -> ear: barrier search {bar:F0} µs (detour {d:F1} m, verified {v}), whole leg {leg:F0} µs");
            }
            return 0;
        }
        int id = 1;
        bool traced = args.Contains("traced");
        void Ask(Vector3 at) => worker.EnqueueRequest(new AcousticRequest { EntityId = 997, ListenerPos = at, SourcePos = at + Vector3.UnitX, SourceRadius = 0.1f });
        Measure(worker, acoustics, world, ear, srcs, ref id);
        if (traced && srcs.Count > 0) TracedBinaural.Report(ear, srcs[0], Ask);

        var openArg = args.FirstOrDefault(a => a.StartsWith("open="));
        var doorArgs = args.Where(a => a.StartsWith("door=")).Select(a => P(a[5..])).ToList();
        if (openArg != null || doorArgs.Count > 0)
        {
            float r = openArg != null ? float.Parse(openArg[5..], CultureInfo.InvariantCulture) : -1f;
            var leaves = world.Entities.Values.Where(e => OpeningGraph.IsDoorLeaf(e.Definition)).ToList();
            var chosen = new HashSet<int>();
            if (r > 0f) foreach (var e in leaves) if (Vector3.Distance(e.Transform.Position, ear) <= r) chosen.Add(e.Id);
            foreach (var at in doorArgs)
                if (leaves.Count > 0) chosen.Add(leaves.OrderBy(e => Vector3.Distance(e.Transform.Position, at)).First().Id);
            // swings=N: open, shut, open... N times, each handed over as its own rebuild, so a scene set used
            // in turn (TileSceneSet's two pairs) is measured on every pair and after every kind of change.
            int swings = int.TryParse(args.FirstOrDefault(a => a.StartsWith("swings="))?[7..], out int n) ? Math.Max(1, n) : 1;
            var closed = chosen.ToDictionary(d => d, d => world.Entities[d].Transform);
            if (worker.TileScenesState is { } before) Console.WriteLine($"  tile scenes: {before}");
            for (int s = 1; s <= swings; s++)
            {
                bool open = s % 2 == 1;
                foreach (int doorId in chosen)
                {
                    var e = world.Entities[doorId];
                    var def = e.Definition;
                    var shut = closed[doorId];
                    Console.WriteLine($"  door {e.Id} at ({shut.Position.X:F1}, {shut.Position.Y:F1}, {shut.Position.Z:F1}): {(open ? "opened" : "shut")}");
                    var moved = e;
                    if (open)
                    {
                        // Swung a quarter turn on its hinge, as DoorSystem swings it: about the leaf's +X edge.
                        var rot = shut.Rotation;
                        var hinge = shut.Position + Vector3.Transform(new Vector3(def.Collider.Size.X * 0.5f, 0, 0), rot);
                        var swung = rot * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
                        moved.Transform.Rotation = swung;
                        moved.Transform.Position = hinge + Vector3.Transform(new Vector3(-def.Collider.Size.X * 0.5f, 0, 0), swung);
                    }
                    else moved.Transform = shut;
                    world.Entities[e.Id] = moved;
                }
                int swapsBefore = worker.SceneSwaps;
                worker.UpdateWorld(world);
                // The worker only looks at the doors on a tick with something to answer, and the rebuild
                // runs off it: keep it busy until the new scene has landed, and a little after.
                var until = DateTime.UtcNow.AddSeconds(30);
                while (worker.SceneSwaps == swapsBefore && DateTime.UtcNow < until)
                {
                    worker.EnqueueRequest(new AcousticRequest { EntityId = 998, ListenerPos = ear, SourcePos = ear + Vector3.UnitX, SourceRadius = 0.1f });
                    Thread.Sleep(100);
                }
                for (int i = 0; i < 5; i++)
                {
                    worker.EnqueueRequest(new AcousticRequest { EntityId = 998, ListenerPos = ear, SourcePos = ear + Vector3.UnitX, SourceRadius = 0.1f });
                    Thread.Sleep(100);
                }
                Console.WriteLine($"  --- doors {(open ? "open" : "shut")} (swing {s}; {worker.SceneSwaps} scene swap(s)"
                                + (worker.TileScenesState is { } state ? $"; tile scenes: {state}" : "") + ") ---");
                Measure(worker, acoustics, world, ear, srcs, ref id);
                if (traced && srcs.Count > 0) TracedBinaural.Report(ear, srcs[0], Ask);
            }
        }
        Console.WriteLine($"  worker: {worker.RouteCostSummary}");
        return 0;
    }

    /// <summary>The map as the client gets it: the server's loader, its static definitions, and the acoustic
    /// map the client generates from them (ClientGameSession.GenerateAcoustics).</summary>
    internal static WorldSnapshot LoadAsClient(string root, string mapId)
    {
        var prefabs = new PrefabRepository(Path.Combine(root, "prefabs"));
        var maps = new MapRepository(Path.Combine(root, "maps"));
        var manager = new MapManager(maps, prefabs);
        manager.Initialize();
        if (!manager.TryGetMap(mapId, out var ecs, out Vector3 size, out _, out _) || !manager.TryGetMapData(mapId, out var data))
            throw new InvalidOperationException($"no map '{mapId}' under {root}");
        var defs = OpenFPS.Server.Core.EntityDefinitionFactory.StaticDefinitions(ecs);
        var world = new WorldSnapshot { StaticGrid = new SpatialGrid<int>(10.0f) };
        foreach (var def in defs)
        {
            world.Entities[def.EntityId] = new EntitySnapshot { Id = def.EntityId, Definition = def, Transform = def.Transform };
            if (def.Type == OpenFPS.Common.Components.EntityType.StaticObject && !def.Moves && def.Collider.IsSolid)
                world.StaticGrid.AddOverlapping(def.Transform.Position, def.Collider.Size, def.Transform.Rotation, def.EntityId, isStatic: true);
            if (def.Region.RoomSize.X > 0) world.RegionEntityIds.Add(def.EntityId);
        }
        world.AcousticMap = AcousticVolumeGenerator.GenerateRegions(defs, size, data.MinBound, data.VoxelResolution, data.OcclusionFloor);
        return world;
    }

    private static void Settle(AsyncAcousticWorker worker, Vector3 ear)
    {
        // The first request builds the scene; wait until the worker answers one.
        var until = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < until)
        {
            worker.EnqueueRequest(new AcousticRequest { EntityId = 999, ListenerPos = ear, SourcePos = ear + Vector3.UnitX, SourceRadius = 0.1f });
            Thread.Sleep(200);
            if (worker.TryGetResult(999, out var p) && p.Count > 0) break;
        }
    }

    private static string RegionName(WorldSnapshot world, SpatialAcoustics acoustics, Vector3 at)
    {
        int r = acoustics.GetRegionAt(world, at);
        return world.AcousticMap != null && world.AcousticMap.Regions.TryGetValue(r, out var reg) && reg.FriendlyName.Length > 0
            ? $"'{reg.FriendlyName}'" : $"region {r}";
    }

    /// <summary>The openings near the ear as the geometry has them, and the rooms they join.</summary>
    private static void DescribeOpenings(SpatialAcoustics acoustics, WorldSnapshot world, Vector3 ear)
    {
        var routes = acoustics.RoutesFor(world);
        if (routes == null) { Console.WriteLine("  no opening graph (no acoustic map)"); return; }
        Console.WriteLine($"  {routes.Openings.Count} openings on the map, {routes.Problems.Count} disagreeing with the geometry");
        string Room(int node)
        {
            if (node == OpeningRoutes.Outside) return "outdoors";
            string name = world.AcousticMap!.Regions.TryGetValue(node, out var r) ? r.FriendlyName : node.ToString();
            return routes.TryGetAbsorption(node, out var a) ? $"{name} (A {a.X:F0}/{a.Y:F0}/{a.Z:F0} m²)" : name;
        }
        foreach (var o in routes.Openings.Where(o => Vector3.Distance(o.Centre, ear) < 20f).OrderBy(o => Vector3.Distance(o.Centre, ear)))
        {
            static float TauDb(float t) => 10f * MathF.Log10(MathF.Max(1e-12f, t));
            Console.WriteLine($"    {o.Kind} {o.Id} at ({o.Centre.X:F2}, {o.Centre.Y:F2}, {o.Centre.Z:F2}) facing ({o.Normal.X:F1}, {o.Normal.Y:F1}, {o.Normal.Z:F1}), "
                            + $"{2 * o.HalfWidth:F2} x {2 * o.HalfHeight:F2} m, {2 * o.HalfDepth:F2} deep, passes {TauDb(o.Tau.X):F0}/{TauDb(o.Tau.Y):F0}/{TauDb(o.Tau.Z):F0} dB; "
                            + $"{Room(o.NodeA)} - {Room(o.NodeB)}" + (o.Problem != null ? $"  PROBLEM: {o.Problem}" : ""));
        }
    }

    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-5f, g));
    private static string Bands(float l, float m, float h) => $"{Db(l),6:F1} {Db(m),6:F1} {Db(h),6:F1}";

    private static void Measure(AsyncAcousticWorker worker, SpatialAcoustics acoustics, WorldSnapshot world, Vector3 ear, List<Vector3> srcs, ref int id)
    {
        Console.WriteLine("                                          low    mid   high  (dB)");
        foreach (var src in srcs)
        {
            List<AcousticPathData>? paths = null;
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until)
            {
                worker.EnqueueRequest(new AcousticRequest { EntityId = id, ListenerPos = ear, SourcePos = src, SourceRadius = AudioEmission.MinOcclusionRadius });
                Thread.Sleep(50);
                if (worker.TryGetResult(id, out paths) && paths.Count > 0 && paths[0].SourcePosition == src) break;
            }
            Console.WriteLine($"  src ({src.X:F1}, {src.Y:F1}, {src.Z:F1}) {Vector3.Distance(ear, src):F1} m, in {RegionName(world, acoustics, src)}");
            if (paths == null || paths.Count == 0) { Console.WriteLine("      worker: no answer"); id++; continue; }
            var p = paths[0];
            Console.WriteLine($"      worker (sustained voices)  {Bands(p.EqLow, p.EqMid, p.EqHigh)}  occlusion {p.Occlusion:F2}, heard from {Bearing(ear, p.ApparentPosition)}");
            var h = acoustics.CalculateAcousticPath(world, -1, ear, src);
            Console.WriteLine($"      one-shot path              {Bands(h.EqLow, h.EqMid, h.EqHigh)}  occlusion {h.Occlusion:F2}, heard from {Bearing(ear, h.ApparentPosition)}");

            var routes = acoustics.RoutesFor(world);
            if (routes != null)
            {
                int sr = acoustics.GetRegionAt(world, src), lr = acoustics.GetRegionAt(world, ear);
                if (routes.Route(src, sr, ear, lr, out var a))
                {
                    var t = Stopwatch.StartNew();
                    int n = 0;
                    while (t.ElapsedMilliseconds < 200) { routes.Route(src, sr, ear, lr, out _); n++; }
                    double us = t.Elapsed.TotalMilliseconds * 1000.0 / n;
                    Console.WriteLine($"      by the openings            {Bands(a.Low, a.Mid, a.High)}  {a.Routes} route(s), best {a.Length:F1} m via {a.Via}, "
                                    + $"arrives from ({a.Apparent.X:F1}, {a.Apparent.Y:F1}, {a.Apparent.Z:F1}); {us:F0} µs a query");
                }
                else Console.WriteLine("      by the openings            none (same place, or nothing joins them)");
            }

            Vector3 dir = Vector3.Normalize(src - ear);
            float len = Vector3.Distance(ear, src);
            float sl = 0, sm = 0, sh = 0;
            int walls = 0;
            foreach (var b in SteamAudioScene.BoxesFromWorld(world))
            {
                if (!GeometryUtils.RayIntersectsOBB(ear, dir, b.Center, b.Size, b.Rotation, out float at) || at > len) continue;
                var (gl, gm, gh) = WallTransmission.BandGains(b.Material, b.Size, b.Build);
                sl += Db(gl); sm += Db(gm); sh += Db(gh);
                walls++;
            }
            Console.WriteLine($"      {walls} wall(s) on the line        {sl,6:F1} {sm,6:F1} {sh,6:F1}");
            id++;
        }
    }

    private static string Bearing(Vector3 ear, Vector3 at)
    {
        float deg = MathF.Atan2(at.X - ear.X, at.Z - ear.Z) * 180f / MathF.PI;
        return $"bearing {deg:F0}° ({at.X:F1}, {at.Y:F1}, {at.Z:F1})";
    }
}
