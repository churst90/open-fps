using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The parity harness for geometry stage 1 (docs/GEOMETRY.md 7): the old box path and the triangle world
/// run side by side on a real map over thousands of probes, and every answer that differs by more than
/// float rounding is listed, with what it was. The gate for switching each consumer over.
///
///   --geometry-parity [map=city] [n=4000] [seed=1] [only=rays,ground,overlap,steps,enclosure,occlusion,bullets,collision,determinism]
///                     [show=12] [times=1]
///
/// Both paths are the game's own code: the server's grid with and without its triangle world, and the
/// client's SpatialService over a snapshot with and without one. Old and new are asked the same thing
/// from the same state, one probe at a time, so a difference is a difference in the answer and never in
/// what happened before it. Tolerances: distances and heights 1 mm (plus 1e-5 of the distance), normals
/// 0.001 rad, band gains 1e-4 relative.
/// </summary>
public static class GeometryParitySpike
{
    private sealed class Tally
    {
        public readonly string Name;
        public int Probes, Same, Ties;
        public readonly Dictionary<string, int> Kinds = new();
        /// <summary>For ties that differ in what they say (a material), which way round and how often.</summary>
        public readonly Dictionary<string, int> TiePairs = new();
        public readonly List<string> Shown = new();
        public double OldMs, NewMs;
        public double MaxError;
        public Tally(string name) => Name = name;
        public void Differ(string kind, string detail, int show)
        {
            Kinds[kind] = Kinds.GetValueOrDefault(kind) + 1;
            if (Shown.Count < show) Shown.Add($"[{kind}] {detail}");
        }
        public int Differences => Kinds.Values.Sum();
    }

    private static string Arg(string[] args, string name, string fallback)
        => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..] ?? fallback;

    private static string V(Vector3 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string mapId = Arg(args, "map", "city");
        int n = int.Parse(Arg(args, "n", "4000"));
        int seed = int.Parse(Arg(args, "seed", "1"));
        int show = int.Parse(Arg(args, "show", "12"));
        var only = Arg(args, "only", "rays,ground,overlap,steps,enclosure,occlusion,bullets,collision,determinism").Split(',').ToHashSet();

        // ── The map, as the server loads it ──────────────────────────────────────────────────────
        var clock = Stopwatch.StartNew();
        string dir = Path.Combine(Path.GetTempPath(), "openfps-geometry-parity-" + Guid.NewGuid().ToString("N"));
        string src = OpenFPS.AudioLab.LabPaths.Server("maps", "places", mapId + ".json");
        bool place = File.Exists(src);
        if (!place) src = OpenFPS.AudioLab.LabPaths.Server("maps", mapId + ".json");
        Directory.CreateDirectory(Path.Combine(dir, "maps", "places"));
        File.Copy(src, place ? Path.Combine(dir, "maps", "places", mapId + ".json") : Path.Combine(dir, "maps", mapId + ".json"));
        var maps = new MapManager(new MapRepository(Path.Combine(dir, "maps")), new PrefabRepository(OpenFPS.AudioLab.LabPaths.Server("prefabs")));
        maps.Initialize();
        Directory.Delete(dir, true);
        if (!maps.TryGetMap(mapId, out var ecs, out var mapSize, out var grid, out var lookup) || !maps.TryGetMapData(mapId, out var data))
        { Console.WriteLine($"FAIL: no map {mapId}"); return 1; }
        if (!maps.TryGetGeometry(mapId, out var serverGeometry)) { Console.WriteLine("FAIL: the server built no triangle world (OPENFPS_TRIANGLES=0?)"); return 1; }
        var serverWorld = serverGeometry.World;
        var serverUnindexed = grid.Unindexed.ToList();
        double loadMs = clock.Elapsed.TotalMilliseconds;

        // The server's triangle world, rebuilt from scratch to time it.
        var statics = new List<SolidSpec>(); var movers = new List<SolidSpec>(); var unindexedEntities = new List<Entity>();
        ServerGeometry.Collect(ecs, statics, movers, unindexedEntities, null);
        var timeBuilder = new TriangleWorldBuilder(data.TileMetres > 0 ? data.TileMetres : 250f) { Parallel = false };
        clock.Restart();
        var rebuilt = timeBuilder.Build(statics, movers);
        double buildOneThread = clock.Elapsed.TotalMilliseconds;
        var parBuilder = new TriangleWorldBuilder(data.TileMetres > 0 ? data.TileMetres : 250f) { Parallel = true };
        clock.Restart();
        parBuilder.Build(statics, movers);
        double buildParallel = clock.Elapsed.TotalMilliseconds;
        var pieceTimes = new List<(double Ms, int Tris)>();
        foreach (var inst in rebuilt.Instances.ToArray().Where(i => i.Owner < 0).Select(i => i.Piece).Distinct())
        {
            var group = statics.Where(s => timeBuilder.KeyOf(s) == inst.Key).OrderBy(s => s.Owner).ToList();
            var c = Stopwatch.StartNew();
            GeometryPiece.Build(inst.Key, inst.Origin, group, 0);
            pieceTimes.Add((c.Elapsed.TotalMilliseconds, inst.TriangleCount));
        }
        pieceTimes.Sort();
        long bytes = rebuilt.Instances.ToArray().Select(i => i.Piece).Distinct().Sum(p => p.Bytes);
        Console.WriteLine($"{mapId}: {statics.Count:N0} static solid boxes, {movers.Count:N0} door leaves, {unindexedEntities.Count} other statics; "
                        + $"{rebuilt.TriangleCount:N0} triangles in {timeBuilder.TileCount} tile(s) (map loaded in {loadMs:F0} ms)");
        Console.WriteLine($"  build: {buildOneThread:F0} ms on one thread, {buildParallel:F0} ms on {Environment.ProcessorCount}; per tile median "
                        + $"{(pieceTimes.Count > 0 ? pieceTimes[pieceTimes.Count / 2].Ms : 0):F2} ms, max {(pieceTimes.Count > 0 ? pieceTimes[^1].Ms : 0):F1} ms "
                        + $"({(pieceTimes.Count > 0 ? pieceTimes[^1].Tris : 0):N0} triangles); memory {bytes / 1e6:F1} MB");

        // What the server pays when its static geometry changes (MapManager.RefreshGrid: a pick-up, a
        // drop, a composite placed): the grid refilled, every tile's solids hashed, the changed tile built.
        {
            var refresh = Stopwatch.StartNew();
            maps.RefreshGrid(mapId);
            double same = refresh.Elapsed.TotalMilliseconds;
            Entity some = Entity.Null;
            ecs.Query(new Arch.Core.QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e, ref Transform t, ref ColliderComponent c) =>
            {
                if (some == Entity.Null && c.IsSolid && c.Shape == ColliderShape.Box && !ecs.Has<Velocity>(e) && !ecs.Has<PortalComponent>(e) && c.Size.X < 5f && c.Size.Y > 1f) some = e;
            });
            if (some != Entity.Null)
            {
                ref var tt = ref ecs.Get<Transform>(some);
                tt.Position += new Vector3(0.2f, 0, 0);
                refresh.Restart();
                maps.RefreshGrid(mapId);
                double one = refresh.Elapsed.TotalMilliseconds;
                int oneBuilt = serverGeometry.LastBuilt; double oneCollect = serverGeometry.LastCollectMs, oneTiles = serverGeometry.LastBuildMs;
                tt.Position -= new Vector3(0.2f, 0, 0);
                maps.RefreshGrid(mapId);
                OpenFPS.Common.Geometry.TriangleGeometry.Enabled = false;
                refresh.Restart();
                maps.RefreshGrid(mapId);
                double gridOnly = refresh.Elapsed.TotalMilliseconds;
                OpenFPS.Common.Geometry.TriangleGeometry.Enabled = true;
                maps.RefreshGrid(mapId);
                serverWorld = serverGeometry.World;
                Console.WriteLine($"  server RefreshGrid: {gridOnly:F0} ms the grid alone; with the triangles {same:F0} ms when nothing changed, {one:F0} ms with one box moved ({oneBuilt} tile built; reading the solids {oneCollect:F0} ms, the tiles {oneTiles:F0} ms)");
            }
        }

        // ── The client: every static definition, as a whole map arrives ──────────────────────────
        var defs = EntityDefinitionFactory.StaticDefinitions(ecs);
        var (oldSnap, newSnap) = ClientSnapshots(defs, data.TileMetres);
        Console.WriteLine($"  client: {defs.Count:N0} definitions; triangle world {newSnap.Geometry!.TriangleCount:N0} triangles, "
                        + $"{newSnap.UnindexedStatics.Count} statics tested the old way");

        var rng = new Random(seed);
        var solidBoxes = statics.Where(s => s.BoxSize.X * s.BoxSize.Z < 4000f).ToList();
        Vector3 NearSomething(float margin)
        {
            var s = solidBoxes[rng.Next(solidBoxes.Count)];
            var h = s.BoxSize * 0.5f;
            var local = new Vector3((float)(rng.NextDouble() * 2 - 1) * (h.X + margin),
                                    (float)(rng.NextDouble() * 2 - 1) * (h.Y + margin),
                                    (float)(rng.NextDouble() * 2 - 1) * (h.Z + margin));
            return s.Position + Vector3.Transform(local, s.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : s.Rotation);
        }
        Vector3 Direction()
        {
            float z = (float)(rng.NextDouble() * 2 - 1), a = (float)(rng.NextDouble() * Math.PI * 2);
            float r = MathF.Sqrt(1 - z * z);
            return new Vector3(r * MathF.Cos(a), z, r * MathF.Sin(a));
        }

        var tallies = new List<Tally>();
        var spatial = new SpatialService();

        // raydebug=x,y,z,dx,dy,dz: every face a ray crosses within 50 m, with what it belongs to.
        if (args.FirstOrDefault(a => a.StartsWith("raydebug=")) is { } rayArg)
        {
            var f = rayArg[9..].Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            var o = new Vector3(f[0], f[1], f[2]); var d = Vector3.Normalize(new Vector3(f[3], f[4], f[5]));
            var crossings = new List<GeometryCrossing>(); var any = new AcceptAll();
            serverWorld.All(o, d, 50f, GeometryLayers.All, ref any, crossings);
            foreach (var c in crossings)
            {
                var (bc, bs, _) = serverWorld.BoxOf(c.Solid);
                var e = lookup.TryGetValue(c.Owner, out var en) ? en : Entity.Null;
                string name = e != Entity.Null && ecs.Has<IdentityComponent>(e) ? ecs.Get<IdentityComponent>(e).Name : "";
                Console.WriteLine($"  t {c.T:R} {(c.Front ? "in" : "out")} #{c.Owner} '{name}' {serverWorld.SurfaceOf(c.Solid).Material} centre {V(bc)} size {V(bs)} volume {serverWorld.FootprintOf(c.Solid):G4}");
            }
            return 0;
        }

        // stepdebug=x,y,z,ix,iz: one step on both paths, with every obstacle's answer at the moved and
        // the stepped-up position.
        if (args.FirstOrDefault(a => a.StartsWith("stepdebug=")) is { } stepArg)
        {
            var f = stepArg[10..].Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            DebugStep(ecs, grid, serverWorld, serverUnindexed, new Vector3(f[0], f[1], f[2]), Vector3.Normalize(new Vector3(f[3], 0, f[4])), data,
                      f.Length >= 8 ? new Vector3(f[5], f[6], f[7]) : Vector3.Zero, f.Length >= 9 ? f[8] != 0 : null);
            return 0;
        }

        // ── Rays ─────────────────────────────────────────────────────────────────────────────────
        if (only.Contains("rays"))
        {
            var single = new Tally("RaycastSingle (60 m)"); var material = new Tally("RaycastMaterial (60 m)");
            var sight = new Tally("CastSight (600 m, glass seen through)"); var all = new Tally("RaycastAll (8 rays, 60 m)");
            tallies.AddRange(new[] { single, material, sight, all });
            var dirs = new Vector3[8]; var od = new float[8]; var oa = new float[8]; var om = new string[8];
            var nd = new float[8]; var na = new float[8]; var nm = new string[8];
            for (int i = 0; i < n; i++)
            {
                var o = NearSomething(3f);
                var d = Direction();
                if (i % 3 == 0) { d.Y *= 0.1f; d = Vector3.Normalize(d); }
                var c = Stopwatch.StartNew();
                bool h0 = spatial.RaycastSingle(oldSnap, o, d, 60f, out var e0, out float t0);
                single.OldMs += c.Elapsed.TotalMilliseconds; c.Restart();
                bool h1 = spatial.RaycastSingle(newSnap, o, d, 60f, out var e1, out float t1);
                single.NewMs += c.Elapsed.TotalMilliseconds;
                CompareHit(single, h0, e0.Id, t0, h1, e1.Id, t1, o, d, newSnap, show);

                c.Restart();
                bool m0 = spatial.RaycastMaterial(oldSnap, o, d, 60f, out float mt0, out var mn0, out var mm0);
                material.OldMs += c.Elapsed.TotalMilliseconds; c.Restart();
                bool m1 = spatial.RaycastMaterial(newSnap, o, d, 60f, out float mt1, out var mn1, out var mm1);
                material.NewMs += c.Elapsed.TotalMilliseconds;
                material.Probes++;
                if (m0 != m1) material.Differ(m0 ? "old hit, new missed" : "new hit, old missed", $"from {V(o)} along {V(d)}: old {(m0 ? mt0.ToString("F4") : "-")} new {(m1 ? mt1.ToString("F4") : "-")}", show);
                else if (!m0) material.Same++;
                else
                {
                    float tol = 1e-3f + 1e-5f * mt0;
                    float ang = Vector3.Distance(Vector3.Normalize(mn0), Vector3.Normalize(mn1));   // the angle, for small ones
                    material.MaxError = Math.Max(material.MaxError, Math.Abs(mt0 - mt1));
                    if (MathF.Abs(mt0 - mt1) > tol && MathF.Abs(mt0 - mt1) * MathF.Abs(Vector3.Dot(Vector3.Normalize(mn1), d)) < 2e-4f)
                    {
                        material.Ties++; material.Same++;
                        material.TiePairs["glancing (the same within 0.2 mm square to the face)"] = material.TiePairs.GetValueOrDefault("glancing (the same within 0.2 mm square to the face)") + 1;
                    }
                    else if (MathF.Abs(mt0 - mt1) > tol) material.Differ("distance", $"from {V(o)} along {V(d)}: old {mt0:F4} new {mt1:F4}", show);
                    else if (mt0 == 0f && mt1 == 0f && ang > 1e-3f)
                    {
                        // Inside two solids at once (a slab laid in another): which one it is "in" is a tie.
                        var inside = new List<SolidRef>(); var everything = new AcceptAll();
                        newSnap.Geometry!.Containing(o, GeometryLayers.Physical, ref everything, inside);
                        if (inside.Count > 1) { material.Ties++; material.Same++; }
                        else material.Differ("normal when the ray starts inside", $"from {V(o)}: old {V(mn0)} new {V(mn1)}", show);
                    }
                    else if (ang > 1e-3f) material.Differ("normal", $"from {V(o)} along {V(d)} at {mt0:F3}: old {V(mn0)} new {V(mn1)}", show);
                    else if (mm0 != mm1) { material.Ties++; material.Same++; }
                    else material.Same++;
                }

                var so = i % 2 == 0 ? o : o + new Vector3(0, 1.6f, 0);
                var sd = Vector3.Normalize(new Vector3(d.X, d.Y * 0.2f, d.Z));
                c.Restart();
                bool s0 = spatial.CastSight(oldSnap, so, sd, 600f, out var se0, out float st0);
                sight.OldMs += c.Elapsed.TotalMilliseconds; c.Restart();
                bool s1 = spatial.CastSight(newSnap, so, sd, 600f, out var se1, out float st1);
                sight.NewMs += c.Elapsed.TotalMilliseconds;
                CompareHit(sight, s0, se0.Id, st0, s1, se1.Id, st1, so, sd, newSnap, show);

                if (i % 4 == 0)
                {
                    for (int k = 0; k < 8; k++) dirs[k] = Direction();
                    c.Restart();
                    spatial.RaycastAll(oldSnap, o, dirs, 60f, od, oa, om);
                    all.OldMs += c.Elapsed.TotalMilliseconds; c.Restart();
                    spatial.RaycastAll(newSnap, o, dirs, 60f, nd, na, nm);
                    all.NewMs += c.Elapsed.TotalMilliseconds;
                    for (int k = 0; k < 8; k++)
                    {
                        all.Probes++;
                        float tol = 1e-3f + 1e-5f * od[k];
                        all.MaxError = Math.Max(all.MaxError, od[k] < 60f || nd[k] < 60f ? Math.Abs(od[k] - nd[k]) : 0);
                        if (MathF.Abs(od[k] - nd[k]) > tol) all.Differ("distance", $"from {V(o)} along {V(dirs[k])}: old {od[k]:F4} ({om[k]}) new {nd[k]:F4} ({nm[k]})", show);
                        else if (om[k] != nm[k] || MathF.Abs(oa[k] - na[k]) > 1e-6f) { all.Ties++; all.Same++; }
                        else all.Same++;
                    }
                }
            }
        }

        // ── Ground heights ──────────────────────────────────────────────────────────────────────
        if (only.Contains("ground"))
        {
            var server = new Tally("Ground height, server (5 probes, step 0.4 m)");
            var client = new Tally("Ground height, client");
            tallies.Add(server); tallies.Add(client);
            var geoSaved = grid.Geometry;
            for (int i = 0; i < n; i++)
            {
                // Along walking routes: a point near something, then its floor, then a little above it.
                var p = NearSomething(2f);
                if (i % 2 == 0)
                {
                    SetGrid(grid, null, serverUnindexed);
                    float f = PhysicsUtils.GetGroundHeight(ecs, grid, p + new Vector3(0, 40f, 0), out _);
                    if (f > -900f) p.Y = f + (float)rng.NextDouble() * 0.45f;
                }
                SetGrid(grid, null, serverUnindexed);
                var c = Stopwatch.StartNew();
                float g0 = PhysicsUtils.GetGroundHeight(ecs, grid, p, out string gm0);
                server.OldMs += c.Elapsed.TotalMilliseconds;
                SetGrid(grid, geoSaved, serverUnindexed);
                c.Restart();
                float g1 = PhysicsUtils.GetGroundHeight(ecs, grid, p, out string gm1);
                server.NewMs += c.Elapsed.TotalMilliseconds;
                CompareGround(server, p, g0, gm0, g1, gm1, show);

                c.Restart();
                float c0 = PhysicsUtils.GetGroundHeight(oldSnap, p, -1, out string cm0);
                client.OldMs += c.Elapsed.TotalMilliseconds; c.Restart();
                float c1 = PhysicsUtils.GetGroundHeight(newSnap, p, -1, out string cm1);
                client.NewMs += c.Elapsed.TotalMilliseconds;
                CompareGround(client, p, c0, cm0, c1, cm1, show);
                if ((gm1 != cm1) && args.Contains("tiedebug") && server.Ties < 6)
                {
                    var all = new AcceptAll();
                    serverWorld.Ground(p, 0.3f, 0.4f, GeometryLayers.Ground, ref all, out var ss, out int so);
                    newSnap.Geometry!.Ground(p, 0.3f, 0.4f, GeometryLayers.Ground, ref all, out var cs, out int co);
                    Console.WriteLine($"  tie at {V(p)}: server new {gm1} (#{so} '{serverWorld.SurfaceOf(ss).Material}'), client new {cm1} (#{co} '{newSnap.Geometry.SurfaceOf(cs).Material}'); old server {gm0}, old client {cm0}");
                }
            }
            SetGrid(grid, geoSaved, serverUnindexed);
        }

        // ── Body overlaps: the cylinder against each solid, box test against triangles ──────────
        if (only.Contains("overlap"))
        {
            var overlap = new Tally("Body overlap per solid (cylinder r 0.3, 1.65 m), both ways out");
            var intersect = new Tally("Body intersects per solid (step test)");
            tallies.Add(overlap); tallies.Add(intersect);
            var near = new List<SolidRef>();
            var all = new AcceptAll();
            for (int i = 0; i < n; i++)
            {
                var centre = NearSomething(0.6f);
                float r = PhysicsConstants.PlayerRadius, h = PhysicsConstants.PlayerHeight - 0.15f;
                near.Clear();
                serverWorld.Overlapping(centre - new Vector3(r + 0.1f, h, r + 0.1f), centre + new Vector3(r + 0.1f, h, r + 0.1f), GeometryLayers.Movement, ref all, near);
                foreach (var s in near)
                {
                    var (bc, bs, br) = serverWorld.BoxOf(s);
                    foreach (bool down in new[] { false, true })
                    {
                        var c = Stopwatch.StartNew();
                        Matrix4x4 worldToLocal = Matrix4x4.CreateTranslation(-bc) * Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(br));
                        var hit0 = GeometryUtils.GetCylinderAABBOverlap(-bs / 2f, bs / 2f, Vector3.Transform(centre, worldToLocal), r, h, down);
                        if (hit0.IsColliding) hit0.Normal = Vector3.TransformNormal(hit0.Normal, Matrix4x4.CreateFromQuaternion(br));
                        overlap.OldMs += c.Elapsed.TotalMilliseconds; c.Restart();
                        var hit1 = SolidContact.CylinderOverlap(serverWorld, s, centre, r, h, down);
                        overlap.NewMs += c.Elapsed.TotalMilliseconds;
                        overlap.Probes++;
                        if (hit0.IsColliding != hit1.IsColliding && MathF.Max(hit0.Penetration, hit1.Penetration) < 1e-4f)
                        {
                            overlap.Ties++; overlap.Same++;
                            overlap.TiePairs["touching within 0.1 mm"] = overlap.TiePairs.GetValueOrDefault("touching within 0.1 mm") + 1;
                            continue;
                        }
                        if (hit0.IsColliding != hit1.IsColliding)
                        {
                            overlap.Differ("touching on one side only", $"body at {V(centre)}, box {V(bc)} size {V(bs)}: old {hit0.IsColliding} ({hit0.Penetration:F5}) new {hit1.IsColliding} ({hit1.Penetration:F5})", show);
                            continue;
                        }
                        if (!hit0.IsColliding) { overlap.Same++; continue; }
                        float ang = Vector3.Distance(hit0.Normal, hit1.Normal);   // the angle, for small ones
                        overlap.MaxError = Math.Max(overlap.MaxError, Math.Abs(hit0.Penetration - hit1.Penetration));
                        if (MathF.Abs(hit0.Penetration - hit1.Penetration) > 1e-3f || ang > 1e-3f)
                        {
                            bool tie = TiedWayOut(centre, bc, bs, br, r);
                            if (tie && MathF.Abs(hit0.Penetration - hit1.Penetration) <= 1e-3f) { overlap.Ties++; overlap.Same++; continue; }
                            overlap.Differ(tie ? "the way out of the middle of a box (tied)" : "way out",
                                $"body at {V(centre)} (down {down}), box {V(bc)} size {V(bs)}: old ({hit0.Normal.X:F5}, {hit0.Normal.Z:F5}) {hit0.Penetration:F6}, "
                                + $"new ({hit1.Normal.X:F5}, {hit1.Normal.Z:F5}) {hit1.Penetration:F6}", show);
                        }
                        else overlap.Same++;
                    }
                    var lc = Vector3.Transform(centre - bc, Quaternion.Inverse(br));
                    bool i0 = GeometryUtils.AABBIntersectsCylinder(-bs / 2f, bs / 2f, lc, r, h);
                    bool i1 = SolidContact.CylinderIntersects(serverWorld, s, centre, r, h);
                    intersect.Probes++;
                    if (i0 == i1) intersect.Same++;
                    else intersect.Differ("touching on one side only", $"body at {V(centre)}, box {V(bc)} size {V(bs)}: old {i0} new {i1}", show);
                }
            }
        }

        // ── Movement steps: one step from the same state, old and new ─────────────────────────────
        if (only.Contains("steps"))
        {
            int walksControlParted = 0;
            var steps = new Tally("Movement step (server gather, one step from the same state)");
            var walks = new Tally("Walks of 90 steps (2 s at 45 Hz) on the server");
            tallies.Add(steps); tallies.Add(walks);
            var geoSaved = grid.Geometry;
            for (int i = 0; i < n; i++)
            {
                var p = NearSomething(1.5f);
                SetGrid(grid, null, serverUnindexed);
                float f = PhysicsUtils.GetGroundHeight(ecs, grid, p + new Vector3(0, 3f, 0), out _);
                if (f < -900f) continue;
                p.Y = f;
                var input = Vector3.Normalize(new Vector3((float)(rng.NextDouble() * 2 - 1), 0, (float)(rng.NextDouble() * 2 - 1)));
                var vel = (i % 5) switch
                {
                    0 => new Vector3(0, -6f, 0),                 // falling onto it
                    1 => new Vector3(0, 4.5f, 0),                // a jump
                    _ => Vector3.Zero,
                };
                if (i % 5 == 0) p.Y += 0.3f;
                bool sprint = i % 3 == 0;
                var c = Stopwatch.StartNew();
                var (p0, v0, g0) = StepOnServer(ecs, grid, null, serverUnindexed, p, vel, input, sprint, data);
                steps.OldMs += c.Elapsed.TotalMilliseconds; c.Restart();
                var (p1, v1, g1) = StepOnServer(ecs, grid, geoSaved, serverUnindexed, p, vel, input, sprint, data);
                steps.NewMs += c.Elapsed.TotalMilliseconds;
                steps.Probes++;
                float err = Vector3.Distance(p0, p1);
                steps.MaxError = Math.Max(steps.MaxError, err);
                if (err > 1e-3f || g0 != g1 || Vector3.Distance(v0, v1) > 1e-2f)
                    steps.Differ(g0 != g1 ? "grounded differs" : "position", $"from {V(p)} vel {V(vel)} input {V(input)}: old {V(p0)} {g0} new {V(p1)} {g1} ({err * 1000:F2} mm)"
                                 + (steps.Shown.Count < show ? WhatTouches(serverWorld, p) : ""), show);
                else steps.Same++;

                if (i % 10 == 0)
                {
                    // Two bodies walking the same inputs, each on its own path: where do they part? And, for
                    // scale, a third on the OLD path started a tenth of a millimetre aside: how often does
                    // the box path part from itself over the same walk?
                    Vector3 a = p, av = Vector3.Zero, b = p, bv = Vector3.Zero, cc = p + new Vector3(1e-4f, 0, 0), cv = Vector3.Zero;
                    var dirNow = input;
                    float worst = 0f, worstControl = 0f; int partedAt = -1;
                    for (int k = 0; k < 90; k++)
                    {
                        if (k % 20 == 0) dirNow = Vector3.Normalize(new Vector3((float)(rng.NextDouble() * 2 - 1), 0, (float)(rng.NextDouble() * 2 - 1)));
                        var (pa, pav, pb, pbv) = (a, av, b, bv);
                        (a, av, _) = StepOnServer(ecs, grid, null, serverUnindexed, a, av, dirNow, sprint, data);
                        (cc, cv, _) = StepOnServer(ecs, grid, null, serverUnindexed, cc, cv, dirNow, sprint, data);
                        worstControl = MathF.Max(worstControl, Vector3.Distance(a, cc) - 1e-4f);
                        (b, bv, _) = StepOnServer(ecs, grid, geoSaved, serverUnindexed, b, bv, dirNow, sprint, data);
                        float e = Vector3.Distance(a, b);
                        if (args.Contains("walkdebug") && e > 1e-5f && e > worst * 1.5f)
                            Console.WriteLine($"  walk from {V(p)} step {k}: old {V(a)} new {V(b)} apart {e * 1000:F3} mm{WhatTouches(serverWorld, a)}");
                        if (e > worst) worst = e;
                        if (args.Contains("walkdebug") && e > 1e-3f && partedAt < 0)
                            Console.WriteLine($"  walk from {V(p)} parted at step {k}; turns near it (length - 1): {TurnsNear(serverWorld, oldSnap, a)}\n"
                                            + $"    before it: old {pa.X:R},{pa.Y:R},{pa.Z:R} v {pav.X:R},{pav.Y:R},{pav.Z:R}; new {pb.X:R},{pb.Y:R},{pb.Z:R} v {pbv.X:R},{pbv.Y:R},{pbv.Z:R}; "
                                            + $"stepdebug={pa.X:R},{pa.Y:R},{pa.Z:R},{dirNow.X:R},{dirNow.Z:R},{pav.X:R},{pav.Y:R},{pav.Z:R},{(sprint ? 1 : 0)}");
                        if (e > 1e-3f && partedAt < 0) partedAt = k;
                    }
                    walks.Probes++;
                    walks.MaxError = Math.Max(walks.MaxError, worst);
                    if (worstControl > 1e-3f) walksControlParted++;
                    if (partedAt >= 0) walks.Differ("parted", $"from {V(p)}: apart by more than 1 mm at step {partedAt}, worst {worst * 1000:F2} mm"
                                                    + $" (the box path from 0.1 mm aside: {worstControl * 1000:F2} mm)", show);
                    else walks.Same++;
                }
            }
            SetGrid(grid, geoSaved, serverUnindexed);
            Console.WriteLine($"  walks: the box path started 0.1 mm aside parted from itself by over 1 mm in {walksControlParted} of {walks.Probes}");
        }

        // ── Enclosure surveys ────────────────────────────────────────────────────────────────────
        if (only.Contains("enclosure"))
        {
            var survey = new Tally("Enclosure.Look survey (192 rays, a bounce, openness)");
            tallies.Add(survey);
            var boxes = SteamAudioScene.BoxesFromWorld(newSnap);
            var solids = boxes.Select(b => new Enclosure.Solid(b.Center, b.Size, b.Rotation, b.Material)).ToList();
            var acoustic = AcousticGeometry.FromBoxes(boxes, data.TileMetres);
            int surveys = Math.Max(20, n / 50);
            for (int i = 0; i < surveys; i++)
            {
                var p = NearSomething(2f);
                float f = PhysicsUtils.GetGroundHeight(oldSnap, p + new Vector3(0, 3f, 0), -1, out _);
                if (f > -900f) p.Y = f + 1.6f;
                var c = Stopwatch.StartNew();
                var s0 = Enclosure.Look(p, solids);
                survey.OldMs += c.Elapsed.TotalMilliseconds; c.Restart();
                var s1 = Enclosure.Look(p, acoustic);
                survey.NewMs += c.Elapsed.TotalMilliseconds;
                survey.Probes++;
                float worst = new[] { Math.Abs(s0.Enclosure - s1.Enclosure), Math.Abs(s0.OpenFraction - s1.OpenFraction),
                                      Math.Abs(s0.AbsorptionLow - s1.AbsorptionLow), Math.Abs(s0.AbsorptionMid - s1.AbsorptionMid),
                                      Math.Abs(s0.AbsorptionHigh - s1.AbsorptionHigh) }.Max();
                float mfp = Math.Abs(s0.MeanFreePathMetres - s1.MeanFreePathMetres) / Math.Max(0.1f, s0.MeanFreePathMetres);
                float area = Math.Abs(s0.SurfaceAreaSquareMetres - s1.SurfaceAreaSquareMetres) / Math.Max(1f, s0.SurfaceAreaSquareMetres);
                survey.MaxError = Math.Max(survey.MaxError, worst);
                bool sameShape = Math.Abs(s0.OpenFraction - s1.OpenFraction) < 1e-6f && mfp < 1e-4f && area < 1e-4f;
                if ((worst > 1e-4f) && sameShape)
                {
                    // Every ray met a surface at the same distance both ways and only what it was made of
                    // differs: two surfaces in the same place (a lawn laid flush on the ground), of which
                    // the box path took whichever its list had first and the triangles take the lower id.
                    survey.Ties++; survey.Same++;
                    survey.TiePairs["coincident surfaces"] = survey.TiePairs.GetValueOrDefault("coincident surfaces") + 1;
                }
                else if (worst > 1e-4f || mfp > 1e-4f || area > 1e-4f)
                    survey.Differ(worst > 0.011f ? "survey (more than two rays' worth)" : "survey (a ray or two)",
                        $"at {V(p)}: enclosure {s0.Enclosure:F4}/{s1.Enclosure:F4}, open {s0.OpenFraction:F4}/{s1.OpenFraction:F4}, "
                        + $"mfp {s0.MeanFreePathMetres:F3}/{s1.MeanFreePathMetres:F3}, abs mid {s0.AbsorptionMid:F4}/{s1.AbsorptionMid:F4}, area {s0.SurfaceAreaSquareMetres:F1}/{s1.SurfaceAreaSquareMetres:F1}", show);
                else survey.Same++;
            }
        }

        // ── Occlusion and transmission ───────────────────────────────────────────────────────────
        if (only.Contains("occlusion"))
        {
            var occl = new Tally("GetOcclusionData (band gains through walls)");
            var multi = new Tally("GetMultiPointOcclusionData (five rays)");
            tallies.Add(occl); tallies.Add(multi);
            for (int i = 0; i < n; i++)
            {
                var a = NearSomething(4f);
                var b = i % 2 == 0 ? NearSomething(4f) : a + Direction() * (float)(rng.NextDouble() * 60);
                if (Vector3.Distance(a, b) > 120f) b = a + Vector3.Normalize(b - a) * 120f;
                var c = Stopwatch.StartNew();
                spatial.GetOcclusionData(oldSnap, a, b, out float k0, out float bl0, out float l0, out float m0, out float h0);
                occl.OldMs += c.Elapsed.TotalMilliseconds; c.Restart();
                spatial.GetOcclusionData(newSnap, a, b, out float k1, out float bl1, out float l1, out float m1, out float h1);
                occl.NewMs += c.Elapsed.TotalMilliseconds;
                CompareBands(occl, a, b, (l0, m0, h0), (l1, m1, h1), show);
                if (i % 4 == 0)
                {
                    c.Restart();
                    spatial.GetMultiPointOcclusionData(oldSnap, a, b, out _, out _, out l0, out m0, out h0);
                    multi.OldMs += c.Elapsed.TotalMilliseconds; c.Restart();
                    spatial.GetMultiPointOcclusionData(newSnap, a, b, out _, out _, out l1, out m1, out h1);
                    multi.NewMs += c.Elapsed.TotalMilliseconds;
                    CompareBands(multi, a, b, (l0, m0, h0), (l1, m1, h1), show);
                }
            }
        }

        // ── Bullets: the static solid a round's segment enters first ─────────────────────────────
        if (only.Contains("bullets"))
        {
            var bullets = new Tally("Bullet segment, first static solid (10 ms at 400-900 m/s)");
            tallies.Add(bullets);
            var cand = new List<Entity>(); var seen = new HashSet<Entity>();
            for (int i = 0; i < n; i++)
            {
                var a = NearSomething(3f);
                var d = Direction(); d.Y *= 0.3f; d = Vector3.Normalize(d);
                float len = 4f + (float)rng.NextDouble() * 5f;
                var c = Stopwatch.StartNew();
                // The box path as CombatService.FirstHit has it, for static solids.
                grid.CollectInRadius(a + d * len * 0.5f, len * 0.5f + 3f, cand, seen);
                int oldOwner = -1; float oldT = float.MaxValue;
                foreach (var e in cand)
                {
                    if (!ecs.IsAlive(e) || !ecs.Has<Transform>(e) || !ecs.Has<ColliderComponent>(e) || ecs.Has<ItemComponent>(e)) continue;
                    if (ecs.Has<Velocity>(e) || ecs.Has<PlayerComponent>(e)) continue;
                    var col = ecs.Get<ColliderComponent>(e);
                    if (!col.IsSolid || col.Shape != ColliderShape.Box) continue;
                    var t = ecs.Get<Transform>(e);
                    if (GeometryUtils.RayIntersectsOBB(a, d, t.Position, col.Size, t.Rotation, out float dd) && dd >= 0f && dd <= len && dd < oldT)
                    { oldT = dd; oldOwner = e.Id; }
                }
                bullets.OldMs += c.Elapsed.TotalMilliseconds; c.Restart();
                var filter = new AcceptAll();
                bool hit1 = CombatService.StaticFirstHit(serverWorld, a, d, len, ref filter, out int newOwner, out float newT);
                bullets.NewMs += c.Elapsed.TotalMilliseconds;
                CompareHit(bullets, oldOwner >= 0, oldOwner, oldT, hit1, newOwner, newT, a, d, newSnap, show);
            }
        }

        // ── CheckCollision (teleports, spawns) ───────────────────────────────────────────────────
        if (only.Contains("collision"))
        {
            var check = new Tally("CheckCollision (a body standing at a point)");
            tallies.Add(check);
            var geoSaved = grid.Geometry;
            for (int i = 0; i < n; i++)
            {
                var p = NearSomething(0.8f);
                SetGrid(grid, null, serverUnindexed);
                var c = Stopwatch.StartNew();
                bool o0 = OpenFPS.Server.Systems.MovementSystem.CheckCollision(ecs, grid, p, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight);
                check.OldMs += c.Elapsed.TotalMilliseconds;
                SetGrid(grid, geoSaved, serverUnindexed);
                c.Restart();
                bool o1 = OpenFPS.Server.Systems.MovementSystem.CheckCollision(ecs, grid, p, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight);
                check.NewMs += c.Elapsed.TotalMilliseconds;
                check.Probes++;
                if (o0 == o1) check.Same++;
                else check.Differ("blocked on one side only", $"at {V(p)}: old {o0} new {o1}", show);
            }
            SetGrid(grid, geoSaved, serverUnindexed);
        }

        // ── Determinism: the server's world and the client's answer with the same bits ───────────
        if (only.Contains("determinism"))
        {
            var det = new Tally("Server and client triangle worlds, same bits (closest hit, ground)");
            tallies.Add(det);
            var cw = newSnap.Geometry!;
            var all = new AcceptAll();
            for (int i = 0; i < n; i++)
            {
                var o = NearSomething(3f); var d = Direction();
                bool a = serverWorld.Closest(o, d, 200f, GeometryLayers.Physical, RayFaces.Both, ref all, out var ha);
                bool b = cw.Closest(o, d, 200f, GeometryLayers.Physical, RayFaces.Both, ref all, out var hb);
                float ga = serverWorld.Ground(o, 0.3f, 0.4f, GeometryLayers.Ground, ref all, out _, out int oa);
                float gb = cw.Ground(o, 0.3f, 0.4f, GeometryLayers.Ground, ref all, out _, out int ob);
                det.Probes++;
                bool same = a == b && (!a || (BitConverter.SingleToInt32Bits(ha.T) == BitConverter.SingleToInt32Bits(hb.T) && ha.Owner == hb.Owner && ha.Normal == hb.Normal))
                            && BitConverter.SingleToInt32Bits(ga) == BitConverter.SingleToInt32Bits(gb) && oa == ob;
                if (same) det.Same++;
                else det.Differ("bits", $"from {V(o)} along {V(d)}: server {(a ? $"{ha.T:R} #{ha.Owner}" : "-")} ground {ga:R} #{oa}; client {(b ? $"{hb.T:R} #{hb.Owner}" : "-")} ground {gb:R} #{ob}", show);
            }
            // A client holding only the tiles round the spawn builds the tiles it has as the server does.
            if (data.TileMetres > 0 && maps.TryGetTiles(mapId, out var tiles))
            {
                var interest = new TileInterest { Radii = StreamRadii.Default };
                var spawn = maps.GetSpawnPoint(mapId).Position;
                var streamed = new List<EntityDefinition>();
                foreach (int id in TileStreamer.Begin(interest, tiles, spawn))
                    if (lookup.TryGetValue(id, out var e) || tiles.TryGetGlobal(id, out e)) streamed.Add(EntityDefinitionFactory.From(ecs, e));
                var (_, streamedSnap) = ClientSnapshots(streamed, data.TileMetres);
                var sw = streamedSnap.Geometry!;
                var serverPieces = serverWorld.Instances.ToArray().Where(x => x.Owner < 0).ToDictionary(x => x.Piece.Key, x => x.Piece.Signature);
                int samePieces = 0, otherPieces = 0;
                foreach (var inst in sw.Instances.ToArray().Where(x => x.Owner < 0))
                    if (serverPieces.TryGetValue(inst.Piece.Key, out var sig) && sig == inst.Piece.Signature) samePieces++; else otherPieces++;
                var stream = new Tally($"Streamed client at the spawn ({streamed.Count:N0} definitions): rays within 250 m, same bits");
                tallies.Add(stream);
                for (int i = 0; i < n; i++)
                {
                    var o = spawn + new Vector3((float)(rng.NextDouble() * 400 - 200), (float)(rng.NextDouble() * 20), (float)(rng.NextDouble() * 400 - 200));
                    var d = Direction();
                    bool a = serverWorld.Closest(o, d, 40f, GeometryLayers.Physical, RayFaces.Both, ref all, out var ha);
                    bool b = sw.Closest(o, d, 40f, GeometryLayers.Physical, RayFaces.Both, ref all, out var hb);
                    stream.Probes++;
                    if (a == b && (!a || (BitConverter.SingleToInt32Bits(ha.T) == BitConverter.SingleToInt32Bits(hb.T) && ha.Owner == hb.Owner))) stream.Same++;
                    else
                    {
                        // A hit on something a coarse tile does not carry is what streaming means, not a difference.
                        bool held = a && streamedSnap.Entities.ContainsKey(ha.Owner);
                        if (a && !held) { stream.Ties++; stream.Same++; }
                        else
                        {
                            string Who(int id) => streamedSnap.Entities.TryGetValue(id, out var es)
                                ? $"#{id} {es.Definition.Identity.Name} {EntityGeometry.RoleOf(es.Definition)} size {V(es.Definition.Collider.Size)}" : $"#{id} not held";
                            stream.Differ("bits", $"from {V(o)} along {V(d)}: server {(a ? $"{ha.T:R} {Who(ha.Owner)}" : "-")} client {(b ? $"{hb.T:R} {Who(hb.Owner)}" : "-")}", show);
                        }
                    }
                }
                Console.WriteLine($"  streamed client: {samePieces} tile piece(s) with the server's own signature, {otherPieces} different (a coarse tile holds less)");
            }
        }

        // ── Track clearance (at load) ────────────────────────────────────────────────────────────
        if (only.Contains("tracks") && data.Tracks is { Count: > 0 })
        {
            var tracks = new Tally("TrackClearance (every track, every sampled point)");
            tallies.Add(tracks);
            var solidsList = new List<TrackClearance.Solid>();
            ecs.Query(new Arch.Core.QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e, ref Transform t, ref ColliderComponent c) =>
            {
                if (!c.IsSolid || c.Shape != ColliderShape.Box || c.Size.X <= 0 || c.Size.Y <= 0 || c.Size.Z <= 0) return;
                solidsList.Add(new TrackClearance.Solid(t.Position, c.Size, t.Rotation));
            });
            foreach (var track in data.Tracks)
            {
                var c = Stopwatch.StartNew();
                var a = OldTrackCheck(track.Waypoints, track.WidthMetres, solidsList);
                tracks.OldMs += c.Elapsed.TotalMilliseconds; c.Restart();
                var b = TrackClearance.Check(track.Waypoints, track.WidthMetres, solidsList);
                tracks.NewMs += c.Elapsed.TotalMilliseconds;
                tracks.Probes++;
                if (a.SequenceEqual(b)) tracks.Same++;
                else tracks.Differ("obstructions", $"track {track.Id}: old {a.Count}, new {b.Count}", show);
            }
            // Per track, not per point: the timing columns are per track here.
        }

        // ── Routes through the openings ──────────────────────────────────────────────────────────
        if (only.Contains("routes"))
            RoutesParity(maps, mapId, ecs, lookup, data, mapSize, n, show, rng, tallies, args);

        // ── The report ───────────────────────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"{"what",-62} {"probes",8} {"same",8} {"ties",6} {"differ",7} {"max err",9} {"old us",9} {"new us",9}");
        foreach (var t in tallies)
        {
            double perOld = t.Probes > 0 ? t.OldMs * 1000 / t.Probes : 0, perNew = t.Probes > 0 ? t.NewMs * 1000 / t.Probes : 0;
            Console.WriteLine($"{t.Name,-62} {t.Probes,8} {t.Same,8} {t.Ties,6} {t.Differences,7} {t.MaxError,9:G3} {perOld,9:F2} {perNew,9:F2}");
        }
        foreach (var t in tallies.Where(t => t.Differences > 0))
        {
            Console.WriteLine();
            Console.WriteLine($"{t.Name}: " + string.Join(", ", t.Kinds.Select(k => $"{k.Value} {k.Key}")));
            foreach (var s in t.Shown) Console.WriteLine("    " + s);
        }
        foreach (var t in tallies.Where(t => t.TiePairs.Count > 0))
        {
            Console.WriteLine();
            Console.WriteLine($"{t.Name}, ties (two surfaces at the same place), old material -> new: "
                              + string.Join(", ", t.TiePairs.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key} x{k.Value}")));
        }
        return 0;
    }

    // ═══ Routes ════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The routes through openings as the acoustic worker builds them for a client at the spawn (the
    /// tiles a medium detail setting holds, or the whole of a map sent whole): built whole from the box
    /// grid (main's OpeningRoutes) and from the triangle store tile by tile, the same questions asked of
    /// both, and what a tile changing costs each.
    /// </summary>
    private static void RoutesParity(MapManager maps, string mapId, World ecs, Dictionary<int, Entity> lookup, MapData data,
                                     Vector3 mapSize, int n, int show, Random rng, List<Tally> tallies, string[] args)
    {
        var world = new WorldSnapshot();
        var defs = new List<EntityDefinition>();
        var spawn = maps.GetSpawnPoint(mapId).Position;
        if (data.TileMetres > 0 && maps.TryGetTiles(mapId, out var tiles))
        {
            var interest = new TileInterest { Radii = StreamRadii.Default };
            foreach (int id in TileStreamer.Begin(interest, tiles, spawn))
                if (lookup.TryGetValue(id, out var e) || tiles.TryGetGlobal(id, out e)) defs.Add(EntityDefinitionFactory.From(ecs, e));
        }
        else defs = EntityDefinitionFactory.StaticDefinitions(ecs);
        foreach (var def in defs)
        {
            world.Entities[def.EntityId] = new EntitySnapshot { Id = def.EntityId, Definition = def, Transform = def.Transform };
            if (def.Region.RoomSize.X > 0) world.RegionEntityIds.Add(def.EntityId);
        }
        world.TileMetres = data.TileMetres;
        world.AcousticMap = ClientWorldState.BuildAcousticMap(defs, mapSize, data.MinBound, data.VoxelResolution, data.OcclusionFloor,
                                                              streamed: data.TileMetres > 0, report: false);
        var acoustics = new OpenFPS.Client.AudioEngine.Acoustics.SpatialAcoustics();
        Func<Vector3, int> regionAt = p => acoustics.GetRegionAt(world, p);
        var boxes = SteamAudioScene.BoxesFromWorld(world);

        // Warm both paths once (the first build of anything pays for the JIT).
        OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph.Build(world, boxes, regionAt);
        var leaves = OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph.LeavesOf(world);
        OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph.Build(world, AcousticGeometry.FromBoxes(boxes, data.TileMetres, leaves), null, new OpeningRoutes.TileCache());

        var clock = Stopwatch.StartNew();
        var old = OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph.Build(world, boxes, regionAt);
        double oldMs = clock.Elapsed.TotalMilliseconds;
        var t = old.BuildTimes;
        Console.WriteLine($"  routes, whole from the box list: {old.Openings.Count} openings over {boxes.Count} boxes in {oldMs:F0} ms: index {t.IndexMs:F0}, "
                        + $"boxes {t.SolidsMs:F0}, openings derived {t.DeriveMs:F0}, sides checked {t.SidesMs:F0} ms");

        var store = new AcousticGeometry(data.TileMetres);
        var cache = new OpeningRoutes.TileCache();
        clock.Restart();
        var scene = store.Update(boxes, leaves);
        double storeMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var tiled = OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph.Build(world, scene, regionAt, cache);
        double newMs = clock.Elapsed.TotalMilliseconds;
        t = tiled.BuildTimes;
        Console.WriteLine($"  routes, tile by tile from the store: {tiled.Openings.Count} openings in {newMs:F0} ms (the store itself {storeMs:F0} ms, "
                        + $"shared with the Steam Audio tiles): boxes {t.SolidsMs:F0}, openings derived {t.DeriveMs:F0}, sides checked {t.SidesMs:F0} ms");

        if (args.FirstOrDefault(a => a.StartsWith("explainpair=")) is { } pairArg)
        {
            var f = pairArg[12..].Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            var ps = new Vector3(f[0], f[1], f[2]); var pl = new Vector3(f[3], f[4], f[5]);
            if (f.Length > 6)
            {
                // A route's legs: to the opening named, and on from it.
                var door = old.Openings.First(o => o.Id == (int)f[6]);
                Console.WriteLine($"  opening {door.Id} at {V(door.Centre)}");
                foreach (var (x, y) in new[] { (ps, door.Centre), (door.Centre, pl) })
                {
                    Console.WriteLine($"  leg {V(x)} -> {V(y)}: old {V(old.LegGains(x, y, door.Contents, Array.Empty<int>()))}, "
                                    + $"tiled {V(tiled.LegGains(x, y, tiled.Openings.First(o => o.Id == door.Id).Contents, Array.Empty<int>()))}");
                    OpeningRoutes.ReverseGridOrderForParity = true;
                    var oldRev = OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph.Build(world, boxes, regionAt);
                    Console.WriteLine($"    old, its boxes met in reverse: {V(oldRev.LegGains(x, y, oldRev.Openings.First(o => o.Id == door.Id).Contents, Array.Empty<int>()))}");
                    OpeningRoutes.ReverseGridOrderForParity = false;
                    var unit = boxes.Select(b => b with { Rotation = b.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(b.Rotation) }).ToList();
                    var oldUnit = OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph.Build(world, unit, regionAt);
                    Console.WriteLine($"    old, its turns made unit length: {V(oldUnit.LegGains(x, y, oldUnit.Openings.First(o => o.Id == door.Id).Contents, Array.Empty<int>()))}");
                    foreach (var (b, g) in old.LegBoxes(x, y)) Console.WriteLine($"    old through {V(b.Center)} {V(b.Size)} {b.Material} leaf {b.IsLeaf} {V(g)}");
                    foreach (var (b, g) in tiled.LegBoxes(x, y)) Console.WriteLine($"    tiled through {V(b.Center)} {V(b.Size)} {b.Material} leaf {b.IsLeaf} {V(g)}");
                }
                return;
            }
            OpeningRoutes.DebugOverTheTop = m => Console.WriteLine("      " + m);
            Console.WriteLine("  OLD:\n" + old.ExplainBarrier(ps, pl));
            Console.WriteLine("  TILED:\n" + tiled.ExplainBarrier(ps, pl));
            float fy = MathF.Min(ps.Y, pl.Y) - 1f;
            var c0 = old.ColumnCandidates(ps, pl, fy); var c1 = tiled.ColumnCandidates(ps, pl, fy);
            Console.WriteLine($"  column: old {c0.Count} boxes, tiled {c1.Count}");
            foreach (var b in c0.Where(x => !c1.Any(y => Vector3.Distance(x.Center, y.Center) < 1e-3f)))
                Console.WriteLine($"    only old: {V(b.Center)} size {V(b.Size)} rot {b.Rotation} {b.Material}");
            foreach (var b in c1.Where(x => !c0.Any(y => Vector3.Distance(x.Center, y.Center) < 1e-3f)))
                Console.WriteLine($"    only tiled: {V(b.Center)} size {V(b.Size)} {b.Material}");
            return;
        }
        CompareRoutes("Routes through openings (a whole build each way)", old, tiled, world, regionAt, new Random(5), n, show, tallies);
        // The map writes a turn in six digits and it is not quite unit length; the box path's corners take
        // it as it is (scaled by its length squared), the triangles make it unit first. With the box list's
        // turns made unit, the box grid should answer as the tiles do.
        var unitBoxes = boxes.Select(b => b with { Rotation = b.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(b.Rotation) }).ToList();
        var gridUnit = OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph.Build(world, unitBoxes, regionAt);
        CompareRoutes("Box grid, turns made unit, against the tiles", gridUnit, tiled, world, regionAt, new Random(5), n, show, tallies);
        // For scale: the box grid against itself with its boxes met the other way round.
        OpeningRoutes.ReverseGridOrderForParity = true;
        var reversed = OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph.Build(world, boxes, regionAt);
        OpeningRoutes.ReverseGridOrderForParity = false;
        CompareRoutes("Control: the box grid against itself, boxes met in reverse", old, reversed, world, regionAt, new Random(5), n, show, tallies);

        // One tile changes, as a door swinging or a tile arriving does: what each way costs, and the
        // answers after it.
        var changed = boxes.ToList();
        int moved = changed.FindIndex(b => TileKey.Of(b.Center, store.TileMetres) == TileKey.Of(spawn, store.TileMetres) && b.Size.Y > 2f);
        if (moved < 0) moved = 0;
        changed[moved] = changed[moved] with { Center = changed[moved].Center + new Vector3(0.3f, 0, 0) };
        clock.Restart();
        var oldAfter = OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph.Build(world, changed, null);
        double oldOne = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var sceneAfter = store.Update(changed, leaves);
        double storeOne = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var tiledAfter = OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph.Build(world, sceneAfter, null, cache);
        double newOne = clock.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  one tile changed: whole build {oldOne:F0} ms (sides not checked, as the worker does after a change); tile build {newOne:F0} ms "
                        + $"({cache.Derived} opening(s) derived again, {cache.Kept} kept) after the store's own {storeOne:F0} ms");
        clock.Restart();
        OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph.Build(world, sceneAfter, null, cache);
        Console.WriteLine($"  nothing changed: tile build {clock.Elapsed.TotalMilliseconds:F0} ms ({cache.Derived} derived, {cache.Kept} kept)");
        CompareRoutes("Routes through openings (after one tile changed)", oldAfter, tiledAfter, world, regionAt, new Random(6), n, show, tallies);
        // The point of building tile by tile: kept and rebuilt, it answers as a build from nothing does.
        // Both unasked: a graph keeps the legs it has worked out, coarsely by place, so one that has
        // answered other questions first can answer this one from a neighbour's leg.
        var fresh = OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph.Build(world, AcousticGeometry.FromBoxes(changed, data.TileMetres, leaves), null, new OpeningRoutes.TileCache());
        var kept = OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph.Build(world, sceneAfter, null, cache);
        CompareRoutes("Tile build after a change against a tile build from nothing", fresh, kept, world, regionAt, new Random(7), n, show, tallies, exact: true);
    }

    private static void CompareRoutes(string name, OpeningRoutes old, OpeningRoutes tiled, WorldSnapshot world, Func<Vector3, int> regionAt,
                                      Random rng, int n, int show, List<Tally> tallies, bool exact = false)
    {
        var openings = new Tally(name + ": openings");
        var routes = new Tally(name + ": Route()");
        var legs = new Tally(name + ": LegGains()");
        var barriers = new Tally(name + ": BarrierPathDifference()");
        tallies.Add(openings); tallies.Add(routes); tallies.Add(legs); tallies.Add(barriers);

        var byId = tiled.Openings.ToDictionary(o => o.Id);
        foreach (var o in old.Openings)
        {
            openings.Probes++;
            if (!byId.TryGetValue(o.Id, out var p)) { openings.Differ("missing", $"{o.Kind} {o.Id}", show); continue; }
            var oc = o.Contents.Select(i => (old.Solids[i].Center, old.Solids[i].Size)).OrderBy(x => x.Center.X).ThenBy(x => x.Center.Z).ToList();
            var pc = p.Contents.Select(i => (tiled.Solids[i].Center, tiled.Solids[i].Size)).OrderBy(x => x.Center.X).ThenBy(x => x.Center.Z).ToList();
            bool sameContents = oc.Count == pc.Count && oc.Zip(pc).All(z => Vector3.Distance(z.First.Center, z.Second.Center) < 1e-3f && Vector3.Distance(z.First.Size, z.Second.Size) < 1e-3f);
            float geo = MathF.Max(Vector3.Distance(o.Centre, p.Centre), MathF.Max(MathF.Abs(o.HalfWidth - p.HalfWidth), MathF.Max(MathF.Abs(o.HalfHeight - p.HalfHeight), MathF.Abs(o.HalfDepth - p.HalfDepth))));
            float tau = Vector3.Distance(o.Tau, p.Tau);
            openings.MaxError = Math.Max(openings.MaxError, geo);
            if (geo > 1e-3f || tau > 1e-4f || !sameContents || o.NodeA != p.NodeA || o.NodeB != p.NodeB)
                openings.Differ("opening", $"{o.Kind} {o.Id} at {V(o.Centre)}: frame {geo * 1000:F2} mm apart, tau {V(o.Tau)} / {V(p.Tau)}, contents {oc.Count}/{pc.Count}", show);
            else openings.Same++;
        }
        if (tiled.Openings.Count != old.Openings.Count) openings.Differ("count", $"old {old.Openings.Count}, tiled {tiled.Openings.Count}", show);

        var near = old.Openings.Where(o => o.NodeA != OpeningRoutes.Outside || o.NodeB != OpeningRoutes.Outside).ToList();
        if (near.Count == 0) near = old.Openings.ToList();
        for (int i = 0; i < n / 4 && near.Count > 0; i++)
        {
            var o1 = near[rng.Next(near.Count)];
            var o2 = near[rng.Next(near.Count)];
            Vector3 Around(OpeningRoutes.Opening o) => o.Centre + o.Normal * ((float)rng.NextDouble() * 12f - 6f)
                                                       + o.Across * ((float)rng.NextDouble() * 8f - 4f) + new Vector3(0, (float)rng.NextDouble() * 1.2f, 0);
            var l = Around(o1);
            var s = i % 3 == 0 ? Around(o1) : Around(o2);
            if (Vector3.Distance(l, s) > 80f) s = l + Vector3.Normalize(s - l) * 80f;
            int lr = regionAt(l), sr = regionAt(s);

            bool r0 = old.Route(s, sr, l, lr, out var a0);
            bool r1 = tiled.Route(s, sr, l, lr, out var a1);
            routes.Probes++;
            float diff = MathF.Max(Rel(a0.Low, a1.Low), MathF.Max(Rel(a0.Mid, a1.Mid), Rel(a0.High, a1.High)));
            float lenDiff = MathF.Abs(a0.Length - a1.Length);
            routes.MaxError = Math.Max(routes.MaxError, lenDiff);
            if (exact)
            {
                bool same = r0 == r1 && a0.Low == a1.Low && a0.Mid == a1.Mid && a0.High == a1.High && a0.Length == a1.Length && a0.Via == a1.Via;
                if (same) routes.Same++; else routes.Differ("bits", $"{V(s)} to {V(l)}: {a0.Mid:R} {a0.Length:R} via {a0.Via} / {a1.Mid:R} {a1.Length:R} via {a1.Via}", show);
            }
            else if (r0 == r1 && r0 && a0.Via == a1.Via && lenDiff <= 1e-3f && diff > 1e-3f && diff < 0.035f)
            {
                // The same openings and length, the bands within 0.3 dB: the boxes' corners placed from a
                // unit quaternion a few hundredths of a millimetre from where the box test put them,
                // through Fresnel integrals deep in a shadow.
                routes.Ties++; routes.Same++;
                routes.TiePairs["the same openings, within 0.3 dB"] = routes.TiePairs.GetValueOrDefault("the same openings, within 0.3 dB") + 1;
            }
            else if (r0 != r1 || (r0 && (diff > 1e-3f || lenDiff > 1e-3f || a0.Routes != a1.Routes)))
                routes.Differ(a0.Via == a1.Via && lenDiff <= 1e-3f ? "the same openings, gains apart" : "other openings",
                    $"{V(s)} (region {sr}) to {V(l)} (region {lr}): old {r0} {a0.Low:G4}/{a0.Mid:G4}/{a0.High:G4} {a0.Length:F3} m x{a0.Routes} via {a0.Via}; "
                    + $"tiled {r1} {a1.Low:G4}/{a1.Mid:G4}/{a1.High:G4} {a1.Length:F3} m x{a1.Routes} via {a1.Via} (bands {diff:G3} apart)", show);
            else routes.Same++;

            var g0 = old.LegGains(s, l, Array.Empty<int>(), Array.Empty<int>());
            var g1 = tiled.LegGains(s, l, Array.Empty<int>(), Array.Empty<int>());
            legs.Probes++;
            float gd = MathF.Max(Rel(g0.X, g1.X), MathF.Max(Rel(g0.Y, g1.Y), Rel(g0.Z, g1.Z)));
            legs.MaxError = Math.Max(legs.MaxError, gd);
            if (gd > 1e-3f) legs.Differ("leg", $"{V(s)} to {V(l)}: old {V(g0)} tiled {V(g1)}", show);
            else legs.Same++;

            float b0 = old.BarrierPathDifference(s, l, out var e0, out bool v0);
            float b1 = tiled.BarrierPathDifference(s, l, out var e1, out bool v1);
            barriers.Probes++;
            barriers.MaxError = Math.Max(barriers.MaxError, MathF.Abs(b0 - b1));
            if (MathF.Abs(b0 - b1) > 1e-3f || v0 != v1) barriers.Differ("barrier", $"{V(s)} to {V(l)}: old {b0:F4} {v0} at {V(e0)}; tiled {b1:F4} {v1} at {V(e1)}", show);
            else barriers.Same++;
        }

        static float Rel(float x, float y) => MathF.Abs(x - y) / MathF.Max(1e-6f, MathF.Max(MathF.Abs(x), MathF.Abs(y)));
    }

    /// <summary>TrackClearance.Check as main had it: every solid tested at every point.</summary>
    private static List<TrackClearance.Obstruction> OldTrackCheck(IReadOnlyList<Vector3> waypoints, float widthMetres, IReadOnlyList<TrackClearance.Solid> solids)
    {
        const float vehicleHalfWidth = TrackClearance.DefaultVehicleHalfWidth;
        var found = new List<TrackClearance.Obstruction>();
        if (waypoints == null || waypoints.Count < 3 || solids.Count == 0) return found;
        float halfLane = MathF.Max(0f, widthMetres * 0.5f - vehicleHalfWidth);
        int n = waypoints.Count;
        for (int i = 0; i < n; i++)
        {
            Vector3 here = waypoints[i], next = waypoints[(i + 1) % n], along = next - here;
            float span = along.Length();
            if (span < 1e-3f) continue;
            along /= span;
            var lateral = new Vector3(-along.Z, 0f, along.X);
            if (lateral.LengthSquared() < 1e-6f) continue;
            lateral = Vector3.Normalize(lateral);
            int steps = Math.Max(1, (int)MathF.Ceiling(span / TrackClearance.SampleSpacing));
            for (int s = 0; s < steps; s++)
            {
                Vector3 centre = here + along * (span * s / steps);
                for (int lane = -1; lane <= 1; lane++)
                {
                    float offset = lane * halfLane;
                    Vector3 side = centre + lateral * offset;
                    bool hit = false;
                    for (int b = 0; b < solids.Count && !hit; b++)
                    {
                        var solid = solids[b];
                        var grown = new Vector3(solid.Size.X + vehicleHalfWidth * 2f, solid.Size.Y, solid.Size.Z + vehicleHalfWidth * 2f);
                        for (float y = TrackClearance.BodyBottom; y <= TrackClearance.BodyTop + 1e-3f; y += 0.4f)
                        {
                            Vector3 p = side + new Vector3(0f, y, 0f);
                            if (!GeometryUtils.IsPointInOBB(p, solid.Centre, grown, solid.Rotation)) continue;
                            found.Add(new TrackClearance.Obstruction(p, offset, solid.Centre, solid.Size));
                            hit = true;
                            break;
                        }
                    }
                }
            }
        }
        return found;
    }

    // ═══ Helpers ═══════════════════════════════════════════════════════════════════════════════

    private static void SetGrid(SpatialGrid<Entity> grid, TriangleWorld? geometry, List<Entity> unindexed)
        => grid.SetGeometry(geometry, geometry == null ? Array.Empty<Entity>() : unindexed);

    /// <summary>Two client snapshots of the same definitions: one answering from the static grid, one
    /// from a triangle world built from them.</summary>
    internal static (WorldSnapshot Old, WorldSnapshot New) ClientSnapshots(List<EntityDefinition> defs, float tileMetres)
    {
        var state = new ClientWorldState();
        state.Geometry.Runner = work => work();
        state.Clear(new Vector3(4000f, 400f, 4000f));          // as a map's manifest does: the static grid exists
        state.ConfigureAcoustics(Vector3.Zero, 0.5f, 0.2f, tileMetres);
        foreach (var def in defs) state.RegisterDefinition(def);
        var built = state.GetSnapshot();
        var fresh = state.GetSnapshot();
        // The first snapshot started the build (in place); the next one carries it.
        var withGeometry = fresh.Geometry != null ? fresh : built;
        var old = new WorldSnapshot { StaticGrid = withGeometry.StaticGrid };
        foreach (var kv in withGeometry.Entities) old.Entities[kv.Key] = kv.Value;
        old.DynamicEntities.AddRange(withGeometry.DynamicEntities);
        return (old, withGeometry);
    }

    private static (Vector3, Vector3, bool) StepOnServer(World ecs, SpatialGrid<Entity> grid, TriangleWorld? geometry, List<Entity> unindexed,
                                                         Vector3 pos, Vector3 vel, Vector3 input, bool sprint, MapData data)
    {
        SetGrid(grid, geometry, unindexed);
        float ground = PhysicsUtils.GetGroundHeight(ecs, grid, pos, out _);
        var near = new List<Entity>(); var seen = new HashSet<Entity>();
        if (geometry != null) grid.CollectDynamicInRadius(pos, PhysicsConstants.CollisionSearchRadius, near, seen);
        else grid.CollectInRadius(pos, PhysicsConstants.CollisionSearchRadius, near, seen);
        var boxes = new List<SharedMovementEngine.Collider>();
        foreach (var e in near)
        {
            if (!ecs.Has<ColliderComponent>(e)) continue;
            var t = ecs.Get<Transform>(e); var c = ecs.Get<ColliderComponent>(e);
            if (!c.IsSolid) continue;
            boxes.Add(new SharedMovementEngine.Collider { Position = t.Position, Size = c.Size, Rotation = SharedMovementEngine.StandingRotation(c.Shape, t.Rotation),
                                                          Material = ecs.Has<MaterialComponent>(e) ? ecs.Get<MaterialComponent>(e).Material : "Generic" });
        }
        var ctx = new SharedMovementEngine.MovementContext
        {
            Position = pos, Velocity = vel, InputDirection = input, DeltaTime = PhysicsConstants.FixedDeltaTime, GroundHeight = ground,
            Gravity = PhysicsConstants.Gravity, JumpForce = PhysicsConstants.JumpPower, Speed = PhysicsConstants.FootSpeed(sprint, float.MaxValue),
            PlayerRadius = PhysicsConstants.PlayerRadius, PlayerHeight = PhysicsConstants.PlayerHeight, StepHeight = PhysicsConstants.StepHeight,
            IsJumpRequested = false, MapMin = data.WalkMin, MapMax = data.WalkMax,
        };
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(boxes);
        var obstacles = new SharedMovementEngine.Obstacles(span);
        var solids = new List<SolidRef>();
        if (geometry != null)
        {
            var all = new AcceptAll();
            SharedMovementEngine.GatherSolids(ctx, geometry, ref all, solids);
            obstacles = new SharedMovementEngine.Obstacles(span, geometry, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(solids));
        }
        return SharedMovementEngine.Step(ctx, obstacles, out _);
    }

    private static void CompareHit(Tally t, bool h0, int e0, float t0, bool h1, int e1, float t1, Vector3 o, Vector3 d, WorldSnapshot snap, int show)
    {
        t.Probes++;
        if (h0 != h1)
        {
            string what = h0 ? $"old #{e0} at {t0:F4}" : $"new #{e1} at {t1:F4}";
            t.Differ(h0 ? "old hit, new missed" : "new hit, old missed", $"from {V(o)} along {V(d)}: {what}{Describe(snap, h0 ? e0 : e1, o + d * (h0 ? t0 : t1))}", show);
            return;
        }
        if (!h0) { t.Same++; return; }
        float tol = 1e-3f + 1e-5f * t0;
        t.MaxError = Math.Max(t.MaxError, Math.Abs(t0 - t1));
        if (MathF.Abs(t0 - t1) > tol)
        {
            // A ray that meets a face at a glancing angle turns a hair's difference in where the face is
            // into a long one along the ray: measured square to the face, is it still float rounding?
            var all = new AcceptAll();
            if (snap.Geometry != null && snap.Geometry.Enter(o, d, t1 + 1f, GeometryLayers.Physical, ref all, out var h) && !h.Inside)
            {
                float cos = MathF.Abs(Vector3.Dot(h.Normal, d));
                if (MathF.Abs(t0 - t1) * cos < 2e-4f)
                {
                    t.Ties++; t.Same++;
                    t.TiePairs["glancing (the same within 0.2 mm square to the face)"] = t.TiePairs.GetValueOrDefault("glancing (the same within 0.2 mm square to the face)") + 1;
                    return;
                }
            }
            t.Differ("distance", $"from {V(o)} along {V(d)}: old #{e0} {t0:F4}, new #{e1} {t1:F4}{Describe(snap, e0, o + d * t0)}", show);
            return;
        }
        if (e0 != e1) { t.Ties++; t.Same++; return; }
        t.Same++;
    }

    /// <summary>Where on its box a point is: near an edge (two faces within 1 mm) or a corner.</summary>
    private static string Describe(WorldSnapshot snap, int id, Vector3 p)
    {
        if (!snap.Entities.TryGetValue(id, out var e)) return "";
        var def = e.Definition;
        var local = Vector3.Transform(p - e.Transform.Position, Quaternion.Inverse(e.Transform.Rotation));
        var h = def.Collider.Size * 0.5f;
        int faces = (MathF.Abs(MathF.Abs(local.X) - h.X) < 1e-3f ? 1 : 0) + (MathF.Abs(MathF.Abs(local.Y) - h.Y) < 1e-3f ? 1 : 0) + (MathF.Abs(MathF.Abs(local.Z) - h.Z) < 1e-3f ? 1 : 0);
        string where = faces >= 3 ? "a corner" : faces == 2 ? "an edge" : faces == 1 ? "a face" : "inside or off it";
        return $" [{def.Identity.Name} {def.Material.Material} size {V(def.Collider.Size)} {def.Type} solid {def.Collider.IsSolid} "
             + $"{def.Collider.Shape} {EntityGeometry.RoleOf(def)}, the point on {where}]";
    }

    private static void CompareGround(Tally t, Vector3 p, float g0, string m0, float g1, string m1, int show)
    {
        t.Probes++;
        t.MaxError = Math.Max(t.MaxError, g0 > -900f && g1 > -900f ? Math.Abs(g0 - g1) : 0);
        if (MathF.Abs(g0 - g1) > 1e-4f) { t.Differ("height", $"at {V(p)}: old {g0:F5} ({m0}) new {g1:F5} ({m1})", show); return; }
        if (m0 != m1)
        {
            t.Ties++; t.Same++;
            string pair = $"{m0} -> {m1}";
            t.TiePairs[pair] = t.TiePairs.GetValueOrDefault(pair) + 1;
            return;
        }
        t.Same++;
    }

    private static void CompareBands(Tally t, Vector3 a, Vector3 b, (float L, float M, float H) o, (float L, float M, float H) n, int show)
    {
        t.Probes++;
        float worst = MathF.Max(Rel(o.L, n.L), MathF.Max(Rel(o.M, n.M), Rel(o.H, n.H)));
        t.MaxError = Math.Max(t.MaxError, worst);
        if (worst > 1e-4f) t.Differ("band gains", $"{V(a)} to {V(b)}: old {o.L:G4}/{o.M:G4}/{o.H:G4} new {n.L:G4}/{n.M:G4}/{n.H:G4}", show);
        else t.Same++;
        static float Rel(float x, float y) => MathF.Abs(x - y) / MathF.Max(1e-6f, MathF.Max(MathF.Abs(x), MathF.Abs(y)));
    }

    private static void DebugStep(World ecs, SpatialGrid<Entity> grid, TriangleWorld world, List<Entity> unindexed, Vector3 p, Vector3 input, MapData data,
                                  Vector3 vel = default, bool? onlySprint = null)
    {
        foreach (bool sprint in onlySprint is { } only ? new[] { only } : new[] { false, true })
        {
            var (p0, v0, g0) = StepOnServer(ecs, grid, null, unindexed, p, vel, input, sprint, data);
            var (p1, v1, g1) = StepOnServer(ecs, grid, world, unindexed, p, vel, input, sprint, data);
            Console.WriteLine($"sprint {sprint}: old {p0.X:R},{p0.Y:R},{p0.Z:R} v {V(v0)} {g0}, new {p1.X:R},{p1.Y:R},{p1.Z:R} v {V(v1)} {g1}");
            SetGrid(grid, null, unindexed);
            float gOld = PhysicsUtils.GetGroundHeight(ecs, grid, p, out string mOld);
            SetGrid(grid, world, unindexed);
            float gNew = PhysicsUtils.GetGroundHeight(ecs, grid, p, out string mNew);
            Console.WriteLine($"  ground old {gOld:R} ({mOld}), new {gNew:R} ({mNew})");
            var any = new AcceptAll();
            world.Ground(p, 0.3f, 0.4f, GeometryLayers.Ground, ref any, out var gs, out int go);
            var (gc, gsz, gr) = world.BoxOf(gs);
            Console.WriteLine($"  ground solid #{go}: centre {gc.X:R},{gc.Y:R},{gc.Z:R} size {gsz.X:R},{gsz.Y:R},{gsz.Z:R} rot {gr.X:R},{gr.Y:R},{gr.Z:R},{gr.W:R}; "
                              + $"top by the box {gc.Y + gsz.Y / 2f:R}; instance at {world.Instance(gs.Instance).Position}");
            for (int k = 0; k < 5; k++)
            {
                var off = k switch { 1 => new Vector3(0.3f, 0, 0), 2 => new Vector3(-0.3f, 0, 0), 3 => new Vector3(0, 0, 0.3f), 4 => new Vector3(0, 0, -0.3f), _ => Vector3.Zero };
                var from = new Vector3(p.X + off.X, p.Y + 0.4f, p.Z + off.Z);
                if (!world.Closest(from, -Vector3.UnitY, 100f, GeometryLayers.Ground, RayFaces.Front, ref any, out var hh, -1e-5f)) continue;
                var tr = world.Instance(hh.Solid.Instance).Piece.Triangle(hh.Triangle);
                Console.WriteLine($"    probe {k}: #{hh.Owner} t {hh.T:R} height {world.HeightOn(hh, from.X, from.Z):R}; V0 {tr.V0.X:R},{tr.V0.Y:R},{tr.V0.Z:R} E1 {tr.E1.X:R},{tr.E1.Y:R},{tr.E1.Z:R} E2 {tr.E2.X:R},{tr.E2.Y:R},{tr.E2.Z:R}");
            }
            float speed = PhysicsConstants.FootSpeed(sprint, float.MaxValue);
            var next = p + input * speed * PhysicsConstants.FixedDeltaTime;
            float h = PhysicsConstants.PlayerHeight - 0.15f, r = PhysicsConstants.PlayerRadius;
            var offset = new Vector3(0, 0.15f + h / 2f, 0);
            foreach (var (label, at) in new[] { ("moved", next + offset), ("stepped up", next + new Vector3(0, PhysicsConstants.StepHeight, 0) + offset) })
            {
                var near = new List<SolidRef>(); var all = new AcceptAll();
                world.Overlapping(at - new Vector3(2, 3, 2), at + new Vector3(2, 3, 2), GeometryLayers.Movement, ref all, near);
                foreach (var s in near)
                {
                    var (bc, bs, br) = world.BoxOf(s);
                    var lc = Vector3.Transform(at - bc, Quaternion.Inverse(br));
                    var o = GeometryUtils.GetCylinderAABBOverlap(-bs / 2f, bs / 2f, lc, r, h);
                    bool oi = GeometryUtils.AABBIntersectsCylinder(-bs / 2f, bs / 2f, lc, r, h);
                    var t = SolidContact.CylinderOverlap(world, s, at, r, h);
                    if (!o.IsColliding && !t.IsColliding && !oi) continue;
                    Console.WriteLine($"  {label}: #{world.OwnerOf(s)} box {V(bc)} size {V(bs)} rot {br}: box {o.IsColliding}/{oi} {V(o.Normal)} {o.Penetration:F6}; triangles {t.IsColliding} {V(t.Normal)} {t.Penetration:F6}");
                }
            }
        }
    }

    /// <summary>The solids within a metre of a body, with how far each one's turn is from unit length.</summary>
    private static string TurnsNear(TriangleWorld world, WorldSnapshot snap, Vector3 feet)
    {
        var near = new List<SolidRef>(); var all = new AcceptAll();
        world.Overlapping(feet - new Vector3(1, 0.5f, 1), feet + new Vector3(1, 2.5f, 1), GeometryLayers.Movement, ref all, near);
        return string.Join(", ", near.Select(s =>
        {
            int id = world.OwnerOf(s);
            var (_, bs, _) = world.BoxOf(s);
            string turn = snap.Entities.TryGetValue(id, out var e) ? (e.Transform.Rotation.Length() - 1f).ToString("G3") : "?";
            return $"#{id} {V(bs)} {turn}";
        }));
    }

    /// <summary>The solids a body standing at <paramref name="feet"/> overlaps, with the box test's answer and the triangles'.</summary>
    private static string WhatTouches(TriangleWorld world, Vector3 feet)
    {
        var centre = feet + new Vector3(0, 0.15f + (PhysicsConstants.PlayerHeight - 0.15f) / 2f, 0);
        float r = PhysicsConstants.PlayerRadius, h = PhysicsConstants.PlayerHeight - 0.15f;
        var near = new List<SolidRef>();
        var all = new AcceptAll();
        world.Overlapping(centre - new Vector3(1, 2, 1), centre + new Vector3(1, 2, 1), GeometryLayers.Movement, ref all, near);
        var sb = new System.Text.StringBuilder();
        foreach (var s in near)
        {
            var (bc, bs, br) = world.BoxOf(s);
            var lc = Vector3.Transform(centre - bc, Quaternion.Inverse(br));
            var o = GeometryUtils.GetCylinderAABBOverlap(-bs / 2f, bs / 2f, lc, r, h);
            var t = SolidContact.CylinderOverlap(world, s, centre, r, h);
            if (!o.IsColliding && !t.IsColliding) continue;
            sb.Append($"\n        #{world.OwnerOf(s)} box {V(bc)} size {V(bs)}: box test {o.IsColliding} {o.Penetration:F4}, triangles {t.IsColliding} {V(t.Normal)} {t.Penetration:F4}");
        }
        return sb.ToString();
    }

    /// <summary>Whether a body's axis is (within 1 mm) equally far from two edges of a box's footprint, so
    /// either is the nearest way out.</summary>
    private static bool TiedWayOut(Vector3 centre, Vector3 bc, Vector3 bs, Quaternion br, float r)
    {
        var l = Vector3.Transform(centre - bc, Quaternion.Inverse(br));
        var h = bs * 0.5f;
        var d = new[] { h.X - l.X, l.X + h.X, h.Z - l.Z, l.Z + h.Z }.OrderBy(x => x).ToArray();
        return d[1] - d[0] < 1e-3f;
    }
}
