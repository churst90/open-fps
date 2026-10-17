using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;
using OpenFPS.Geometry;
using OpenFPS.Acoustics;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --barrier-sweep [map=city] [n=4000] [seed=1] [top=12] [range=150] [outdoor|ground] [panels|buried|terrain]
///
/// How the barrier model's ways round one thing and through another (OpeningRoutes.BarrierGains) change
/// what is heard behind walls on a real map: pairs of points in the map's places (an ear 1.6 m over a
/// place's floor, a source 0.5 to 1.6 m over another's within range), each straight leg as the routes
/// charge it (OpeningRoutes.LegGains) with only the ways round clear of everything and with both kinds.
/// Prints the distribution of the change per band over the pairs whose line meets something, the biggest
/// changes with what was gone round and what the legs paid, and what a leg costs each way. The leg model is
/// the routes' own (Constructions.Through for what the line passes through); the simulator's through figure
/// stands in for it in the game's direct path. outdoor takes ears indoors only and sources out of doors, 10 m to
/// range off, 0.5 m over the ground (the street heard inside). pair=x,y,z;x,y,z spells one ear and source out.
/// panels compares against every solid charged as one panel whatever way the line goes through it (before
/// 2026-10-11, Constructions.PanelsOnlyForLab) instead of against the clear ways round only; buried against the
/// burial of an edge as it was before the rules of 2026-10-11 (OpeningRoutes.OldBurialForLab); terrain against the
/// ground as no obstacle to a straight segment (OpeningRoutes.GroundNoObstacleForLab), and then a pair is kept when
/// its line meets a box or goes through the earth. ground takes both ends out of doors (an ear 1.6 m and a source
/// 0.5 m over the ground, 10 m to range apart): a source behind a rise, a bank or a cutting's side. With pair=,
/// debug also prints every box the string over the top is given (OpeningRoutes.DebugOverTheTop). drive=x,z;x,z
/// ear=x,y,z drives a source 0.5 m over the ground between the two points, every half metre, and says how far the
/// straight leg steps between neighbouring positions, before and after (verbose prints each).
/// server=DIR reads the maps and prefabs under DIR in place of the checkout's OpenFPS.Server (a map before a change);
/// dump=FILE writes every pair, tab-separated, so two runs with one seed can be set side by side. Game coordinates (y up).
/// </summary>
public static class BarrierSweepSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string mapId = Arg(args, "map") ?? "city";
        int n = int.Parse(Arg(args, "n") ?? "4000", CultureInfo.InvariantCulture);
        int top = int.Parse(Arg(args, "top") ?? "12", CultureInfo.InvariantCulture);
        float range = float.Parse(Arg(args, "range") ?? "150", CultureInfo.InvariantCulture);
        var rng = new Random(int.Parse(Arg(args, "seed") ?? "1", CultureInfo.InvariantCulture));
        bool outdoor = args.Contains("outdoor");
        // panels: before is every solid one panel whatever way the line goes through it (Constructions,
        // "A solid gone into by its edge"), not the barrier model's clear ways round only.
        bool panels = args.Contains("panels");
        // buried: before is every other box burying an edge, the terrain none, and no way round an edge
        // buried in another box (OpeningRoutes.OldBurialForLab).
        bool buried = args.Contains("buried");
        // terrain: before is the ground in no segment's way (OpeningRoutes.GroundNoObstacleForLab).
        bool terrain = args.Contains("terrain");
        // ground: the ear out of doors too.
        bool bothOut = args.Contains("ground");
        if (bothOut) outdoor = true;
        void Before(bool on)
        {
            if (panels) Constructions.PanelsOnlyForLab = on;
            else if (buried) OpeningRoutes.OldBurialForLab = on;
            else if (terrain) OpeningRoutes.GroundNoObstacleForLab = on;
            else OpeningRoutes.OnlyClearWaysRoundForLab = on;
        }

        var world = PathProbeSpike.LoadAsClient(Arg(args, "server") ?? OpenFPS.AudioLab.LabPaths.Server(), mapId);
        var acoustics = new SpatialAcoustics();
        var routes = acoustics.RoutesFor(world)!;
        var oldAcoustics = new SpatialAcoustics();
        var oldRoutes = oldAcoustics.RoutesFor(world)!;
        Vector3 Heard(SpatialAcoustics a, OpeningRoutes r, Vector3 leg, Vector3 src, Vector3 ear)
            => r.Route(src, a.GetRegionAt(world, src), ear, a.GetRegionAt(world, ear), out var answer)
                ? OpeningRoutes.Better(leg, answer, out _) : leg;
        var map = world.AcousticMap!;
        if (Arg(args, "pair") is { } pair)
        {
            // One ear and source, each model's leg and the routes spelled out.
            var f = pair.Split(';').Select(s => s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray()).ToArray();
            var e = new Vector3(f[0][0], f[0][1], f[0][2]);
            var s = new Vector3(f[1][0], f[1][1], f[1][2]);
            foreach (bool before in new[] { true, false })
            {
                var (a, r) = before ? (oldAcoustics, oldRoutes) : (acoustics, routes);
                Before(before);
                var leg = r.LegGains(s, e, Array.Empty<int>(), Array.Empty<int>());
                bool any = r.Route(s, a.GetRegionAt(world, s), e, a.GetRegionAt(world, e), out var answer);
                Console.WriteLine($"  {(before ? "before" : "after")}: leg {Db3(leg)}, openings {(any ? $"{Db3(new Vector3(answer.Low, answer.Mid, answer.High))} via {answer.Via}" : "none")}");
            }
            Before(false);
            if (args.Contains("debug")) OpeningRoutes.DebugOverTheTop = Console.WriteLine;
            Console.WriteLine(routes.ExplainBarrier(s, e));
            OpeningRoutes.DebugOverTheTop = null;
            Console.Write(routes.ExplainLine(s, e));
            return 0;
        }
        // The outdoors' own box reaches far under the ground: its points are not places anyone stands.
        var places = map.Regions.Where(r => r.Key != AcousticConstants.GlobalRegionId && r.Key != map.GlobalEnvironmentId
                                            && r.Value.RoomSize.X > 0f && r.Value.RoomSize.Y > 1.8f && map.RegionPositions.ContainsKey(r.Key))
                                .Select(r => r.Key).ToList();
        Console.WriteLine($"  {mapId}: {routes.Solids.Count} boxes, {places.Count} places");

        bool InSolid(Vector3 p)
        {
            foreach (var s in routes.Solids)
                if (GeometryUtils.IsPointInOBB(p, s.Center, s.Size, s.Rotation)) return true;
            return false;
        }
        Vector3 PointIn(int id, float lo, float hi)
        {
            var room = map.Regions[id];
            var c = map.RegionPositions[id];
            var q = map.RegionRotations.GetValueOrDefault(id, Quaternion.Identity);
            var h = room.RoomSize * 0.5f;
            float y = -h.Y + lo + (float)rng.NextDouble() * (hi - lo);
            var local = new Vector3((float)(rng.NextDouble() * 2 - 1) * h.X * 0.9f, MathF.Min(y, h.Y - 0.2f),
                                    (float)(rng.NextDouble() * 2 - 1) * h.Z * 0.9f);
            return c + Vector3.Transform(local, q);
        }

        var rows = new List<(Vector3 Ear, Vector3 Src, Vector3 Old, Vector3 New, int EarPlace, int SrcPlace)>();
        double oldUs = 0, newUs = 0, correctionUs = 0;
        int corrected = 0;
        int attempts = 0;
        var clock = new Stopwatch();
        var legRows = new List<(Vector3 Old, Vector3 New)>();
        // The boxes' ground or the terrain's, the higher: the lab's world has no triangle store, so the physics
        // probe sees boxes only, and a lawn laid a little under the terrain put sources in the earth.
        float GroundUnder(Vector3 p)
        {
            float g = PhysicsUtils.GetGroundHeight(world, p, -1, out _);
            float t = routes.GroundAt(p.X, p.Z);
            return float.IsNaN(t) ? g : MathF.Max(g, t);
        }
        if (Arg(args, "drive") is { } drive && Arg(args, "ear") is { } earArg)
        {
            // A source driven along the ground, 0.5 m over it, every half metre, past one ear: how far the
            // straight leg steps from one position to the next, each model.
            var e = earArg.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            var ear = new Vector3(e[0], e[1], e[2]);
            var d = drive.Split(';').Select(s => s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray()).ToArray();
            var from = new Vector3(d[0][0], 0f, d[0][1]);
            var to = new Vector3(d[1][0], 0f, d[1][1]);
            int steps = Math.Max(1, (int)(Vector3.Distance(from, to) / 0.5f));
            foreach (bool before in new[] { true, false })
            {
                var r = before ? oldRoutes : routes;
                Before(before);
                Vector3? last = null;
                var biggest = new float[3];
                int over3 = 0, earth = 0;
                for (int k = 0; k <= steps; k++)
                {
                    var p = Vector3.Lerp(from, to, k / (float)steps);
                    p.Y = GroundUnder(p + new Vector3(0, 200f, 0)) + 0.5f;
                    // Not in a solid: the physics probe sees no water, and a creek's surface is a box over its bed.
                    if (InSolid(p)) continue;
                    var g = Db3V(r.LegGains(p, ear, Array.Empty<int>(), Array.Empty<int>()));
                    if (!before && routes.ThroughTheGround(p, ear)) earth++;
                    if (args.Contains("verbose")) Console.WriteLine($"    {(before ? "before" : "after")} ({p.X:F1}, {p.Y:F2}, {p.Z:F1}): {g.X:F1}/{g.Y:F1}/{g.Z:F1}");
                    if (last is { } l)
                    {
                        var step = Vector3.Abs(g - l);
                        biggest[0] = MathF.Max(biggest[0], step.X); biggest[1] = MathF.Max(biggest[1], step.Y); biggest[2] = MathF.Max(biggest[2], step.Z);
                        if (MathF.Max(step.X, MathF.Max(step.Y, step.Z)) > 3f) over3++;
                    }
                    last = g;
                }
                Console.WriteLine($"  {(before ? "before" : "after")}: {steps + 1} positions, biggest step between neighbours {biggest[0]:F1}/{biggest[1]:F1}/{biggest[2]:F1} dB, "
                                + $"{over3} steps over 3 dB" + (before ? "" : $"; {earth} positions through the earth"));
            }
            Before(false);
            return 0;
        }
        while (rows.Count < n && attempts < n * 50)
        {
            attempts++;
            int ep = places[rng.Next(places.Count)];
            if (outdoor && !bothOut && !map.Regions[ep].IsIndoor) continue;
            var ear = PointIn(ep, 1.6f, 1.6f);
            if (bothOut)
            {
                // Anywhere out of doors near a place: 1.6 m over the ground there.
                double ea = rng.NextDouble() * Math.Tau, er = rng.NextDouble() * range;
                ear = new Vector3(ear.X + (float)(Math.Cos(ea) * er), ear.Y + 50f, ear.Z + (float)(Math.Sin(ea) * er));
                float eg = GroundUnder(ear);
                if (eg <= -999f) continue;
                ear.Y = eg + 1.6f;
                ep = acoustics.GetRegionAt(world, ear);
                if (map.Regions.TryGetValue(ep, out var ein) && ein.IsIndoor) continue;
            }
            if (InSolid(ear)) continue;
            int sp;
            Vector3 src;
            if (outdoor)
            {
                // Out of doors, the ground's height under the ear's floor: the real places' outdoors is no box.
                double angle = rng.NextDouble() * Math.Tau, r = 10 + rng.NextDouble() * (range - 10);
                src = new Vector3(ear.X + (float)(Math.Cos(angle) * r), ear.Y + (bothOut ? 50f : 1f), ear.Z + (float)(Math.Sin(angle) * r));
                float ground = GroundUnder(src);
                if (ground <= -999f) continue;
                src.Y = ground + 0.5f;
                sp = acoustics.GetRegionAt(world, src);
                if (map.Regions.TryGetValue(sp, out var at) && at.IsIndoor) continue;
            }
            else
            {
                var near = places.Where(p => Vector3.Distance(map.RegionPositions[p], ear) < range).ToList();
                if (near.Count == 0) continue;
                sp = near[rng.Next(near.Count)];
                src = PointIn(sp, 0.5f, 1.6f);
            }
            if (InSolid(src) || Vector3.Distance(src, ear) < 2f) continue;
            if (routes.LegBoxes(src, ear).Count == 0 && !(terrain && routes.ThroughTheGround(src, ear))) continue;
            // Each model on a graph of its own, so the legs each keeps are its own.
            Before(true);
            clock.Restart();
            var oldLeg = oldRoutes.LegGains(src, ear, Array.Empty<int>(), Array.Empty<int>());
            oldUs += clock.Elapsed.TotalMilliseconds * 1000;
            var old = Heard(oldAcoustics, oldRoutes, oldLeg, src, ear);
            Before(false);
            clock.Restart();
            var now = routes.LegGains(src, ear, Array.Empty<int>(), Array.Empty<int>());
            newUs += clock.Elapsed.TotalMilliseconds * 1000;
            legRows.Add((oldLeg, now));
            // What the worker adds to Steam Audio's answer for the same line (OpeningRoutes.SimulatorCorrection).
            clock.Restart();
            var k = routes.SimulatorCorrection(src, ear);
            correctionUs += clock.Elapsed.TotalMilliseconds * 1000;
            if (k != Vector3.One) corrected++;
            now = Heard(acoustics, routes, now, src, ear);
            rows.Add((ear, src, old, now, ep, sp));
        }
        Console.WriteLine($"  {rows.Count} pairs whose line meets something; the straight leg {oldUs / rows.Count:F0} µs before, {newUs / rows.Count:F0} µs after; "
                        + $"the simulator's correction {correctionUs / rows.Count:F1} µs, not one on {corrected} ({100.0 * corrected / Math.Max(1, rows.Count):F1} %)");
        for (int b = 0; b < 3; b++)
        {
            var dl = legRows.Select(r => Db(Get(r.New, b)) - Db(Get(r.Old, b))).ToList();
            Console.WriteLine($"  the straight leg alone, {new[] { "low", "mid", "high" }[b]}: {dl.Count(x => x > 0.5f)} louder by more than 0.5 dB, over 10 dB {dl.Count(x => x > 10f)}, quieter {dl.Count(x => x < -0.01f)}");
        }
        if (Arg(args, "dump") is { } dump)
            File.WriteAllLines(dump, rows.Select(r => string.Join('\t', new[] { r.Ear.X, r.Ear.Y, r.Ear.Z, r.Src.X, r.Src.Y, r.Src.Z,
                                                                               Db(r.Old.X), Db(r.Old.Y), Db(r.Old.Z), Db(r.New.X), Db(r.New.Y), Db(r.New.Z) }
                                                                         .Select(v => v.ToString("F3", CultureInfo.InvariantCulture)))
                                                   + $"\t{r.EarPlace}\t{r.SrcPlace}\t{Name(map, r.EarPlace)}\t{Name(map, r.SrcPlace)}"));
        if (terrain)
        {
            // What the lines through the earth go over: the ground's own corners, or only what is laid or built on it.
            int through = 0, overGround = 0, overBoxes = 0, noWay = 0;
            var examples = new List<string>();
            foreach (var r in rows)
            {
                if (!routes.ThroughTheGround(r.Src, r.Ear)) continue;
                through++;
                string why = routes.ExplainBarrier(r.Src, r.Ear);
                if (why.Contains("over the ground at"))
                {
                    overGround++;
                    if (examples.Count < top)
                        examples.Add($"    ear ({r.Ear.X:F2}, {r.Ear.Y:F2}, {r.Ear.Z:F2}), source ({r.Src.X:F2}, {r.Src.Y:F2}, {r.Src.Z:F2}), "
                                   + $"{Vector3.Distance(r.Ear, r.Src):F1} m: {Db3(r.Old)} -> {Db3(r.New)} dB");
                }
                else if (why.Contains("over the top: no way")) noWay++;
                else overBoxes++;
            }
            Console.WriteLine($"  {through} lines through the earth: {overGround} heard over the ground's own corners, {overBoxes} over only what is laid or "
                            + $"built on it, {noWay} with no way over (something over an end)");
            foreach (var e in examples) Console.WriteLine(e);
        }
        Console.WriteLine("  as heard (the straight leg against the openings, per band the better, OpeningRoutes.Better):");

        string[] band = { "low", "mid", "high" };
        for (int b = 0; b < 3; b++)
        {
            var d = rows.Select(r => Db(Get(r.New, b)) - Db(Get(r.Old, b))).OrderBy(x => x).ToList();
            int changed = d.Count(x => x > 0.5f);
            var c = d.Where(x => x > 0.5f).ToList();
            Console.WriteLine($"  {band[b],4}: {changed} of {d.Count} ({100.0 * changed / d.Count:F1} %) louder by more than 0.5 dB; "
                            + $"of those median {Pct(c, 0.5):F1}, 90th {Pct(c, 0.9):F1}, 99th {Pct(c, 0.99):F1}, max {(c.Count > 0 ? c[^1] : 0):F1} dB; "
                            + $"over 3 dB {d.Count(x => x > 3f)}, over 10 dB {d.Count(x => x > 10f)}, over 20 dB {d.Count(x => x > 20f)}; "
                            + $"quieter {d.Count(x => x < -0.01f)}");
            var levelsOld = rows.Select(r => Db(Get(r.Old, b))).OrderBy(x => x).ToList();
            var levelsNew = rows.Select(r => Db(Get(r.New, b))).OrderBy(x => x).ToList();
            Console.WriteLine($"        level median {Pct(levelsOld, 0.5):F1} -> {Pct(levelsNew, 0.5):F1} dB, 90th {Pct(levelsOld, 0.9):F1} -> {Pct(levelsNew, 0.9):F1}");
            var q = d.Where(x => x < -0.5f).Select(x => -x).OrderBy(x => x).ToList();
            if (q.Count > 0)
                Console.WriteLine($"        quieter by more than 0.5 dB: {q.Count} ({100.0 * q.Count / d.Count:F1} %); of those median {Pct(q, 0.5):F1}, "
                                + $"90th {Pct(q, 0.9):F1}, 99th {Pct(q, 0.99):F1}, max {q[^1]:F1} dB; over 3 dB {q.Count(x => x > 3f)}, over 10 dB {q.Count(x => x > 10f)}");
        }
        for (int b = 0; b < 3; b++)
        {
            Console.WriteLine($"  biggest, {band[b]} band:");
            foreach (var r in rows.OrderByDescending(r => MathF.Abs(Db(Get(r.New, b)) - Db(Get(r.Old, b)))).Take(top))
            {
                if (MathF.Abs(Db(Get(r.New, b)) - Db(Get(r.Old, b))) <= 0.5f) break;
                Console.WriteLine($"    ear ({r.Ear.X:F2}, {r.Ear.Y:F2}, {r.Ear.Z:F2}) in {Name(map, r.EarPlace)}, source ({r.Src.X:F2}, {r.Src.Y:F2}, {r.Src.Z:F2}) in {Name(map, r.SrcPlace)}, "
                                + $"{Vector3.Distance(r.Ear, r.Src):F1} m: {Db3(r.Old)} -> {Db3(r.New)} dB");
                if (panels)
                {
                    foreach (var line in routes.ExplainLine(r.Src, r.Ear).Split('\n'))
                        if (line.Contains("by its edge")) Console.WriteLine("    " + line.Trim());
                    continue;
                }
                foreach (var line in routes.ExplainBarrier(r.Src, r.Ear).Split('\n'))
                    if (line.Contains("round box") || line.Contains("both kinds") || (terrain && (line.Contains("over the ground") || line.Contains("over the top"))))
                        Console.WriteLine("    " + line.Trim());
                if (terrain)
                    foreach (var line in routes.ExplainLine(r.Src, r.Ear).Split('\n'))
                        if (line.Contains("through the earth")) Console.WriteLine("    " + line.Trim());
            }
        }
        return 0;
    }

    private static float Get(Vector3 v, int b) => b == 0 ? v.X : b == 1 ? v.Y : v.Z;
    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-5f, g));
    private static string Db3(Vector3 g) => $"{Db(g.X):F0}/{Db(g.Y):F0}/{Db(g.Z):F0}";
    private static Vector3 Db3V(Vector3 g) => new(Db(g.X), Db(g.Y), Db(g.Z));
    private static float Pct(List<float> sorted, double p) => sorted.Count == 0 ? 0f : sorted[Math.Clamp((int)(p * (sorted.Count - 1)), 0, sorted.Count - 1)];
    private static string Name(AcousticMap map, int id) => map.Regions.TryGetValue(id, out var r) ? $"'{r.FriendlyName}' ({id})" : id.ToString();
    private static string? Arg(string[] args, string key) => args.FirstOrDefault(a => a.StartsWith(key + "="))?[(key.Length + 1)..];
}
