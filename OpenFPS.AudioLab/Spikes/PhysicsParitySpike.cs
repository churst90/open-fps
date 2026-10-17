using System.Globalization;
using System.Numerics;
using Arch.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Instruments;
using OpenFPS.Server.Core;

namespace OpenFPS.AudioLab;

/// <summary>
/// --physics-parity [map=city] [n=4000] [movers=30] [seed=1]
///
/// The physics stages' parity harness (docs/PHYSICS_BOUNDARY.md, stage 0), after --geometry-parity: over
/// thousands of standing points on a real map (on floors near every kind of solid, indoors and out, with
/// parked boxes about), every question the later stages move is asked of the old path and of the new, and
/// every answer that differs past float rounding is counted and the first few listed. At stage 0 the new
/// path is the old one (<see cref="Paths"/>): the harness proves itself, and each later stage puts its new
/// code in the "new" column. Beside them it sweeps the three cross-side risks of section 3.7 on the same
/// points: the server's ground against the client's, what the ground memo's 5 cm tolerance could hand back,
/// and one step on each side from the same state with each side's gather.
/// </summary>
public static class PhysicsParitySpike
{
    /// <summary>How far away the server's ground memo handed back a floor before physics stage 3, metres.</summary>
    private const float OldMemoToleranceMetres = 0.05f;

    /// <summary>One question asked two ways: today's code and the code a stage moves it to.</summary>
    private sealed record Path(string Name, Func<Probe, float[]> Old, Func<Probe, float[]> New);

    /// <summary>One standing point and what is asked there.</summary>
    private sealed record Probe(Vector3 At, Vector3 Direction, bool Sprint, bool Jump);

    private sealed class Tally
    {
        public int Probes, Over;
        public double Worst;
        public readonly List<string> Examples = new();
        public void Add(double difference, Func<string> what, double over = 0.001)
        {
            Probes++;
            Worst = Math.Max(Worst, difference);
            if (difference <= over) return;
            Over++;
            if (Examples.Count < 5) Examples.Add(what());
        }
    }

    private static string V(Vector3 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";

    /// <summary>
    /// The pairs. Stage 0: old and new are the same calls. Stage 1 (moves) and stage 2 (splits) must show none
    /// of them differing; stage 3 changes the ground and the gather on purpose and reports how.
    /// </summary>
    private static List<Path> Paths(PhysicsReplays.LoadedMap map, Entity body, WorldSnapshot snap)
    {
        float ServerGround(Probe p) => PhysicsUtils.GetGroundHeight(map.World, map.Grid, p.At, out _);
        float ClientGround(Probe p) => PhysicsUtils.GetGroundHeight(snap, p.At, body.Id, out _);
        float[] ServerStep(Probe p)
        {
            var step = new PhysicsReplays.Step(p.Direction, p.Sprint, p.Jump, "");
            var ctx = PhysicsReplays.ServerContext(map, body, p.At, Vector3.Zero, step, ServerGround(p), out var geometry, out var solids);
            var boxes = PhysicsReplays.ServerGather(map, body, p.At).Boxes.ToArray();
            var r = SharedMovementEngine.Step(ctx, new SharedMovementEngine.Obstacles(boxes, geometry, solids.ToArray()), out _);
            return new[] { r.NewPosition.X, r.NewPosition.Y, r.NewPosition.Z, r.NewVelocity.Y, r.IsGrounded ? 1f : 0f };
        }
        return new List<Path>
        {
            new("server ground probe", p => new[] { ServerGround(p) }, p => new[] { ServerGround(p) }),
            new("client ground probe", p => new[] { ClientGround(p) }, p => new[] { ClientGround(p) }),
            new("server step", ServerStep, ServerStep),
        };
    }

    public static int Run(string[] args)
    {
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string Arg(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..] ?? fallback;
        string mapId = Arg("map", "city");
        int n = int.Parse(Arg("n", "4000"), CultureInfo.InvariantCulture);
        int moverCount = int.Parse(Arg("movers", "30"), CultureInfo.InvariantCulture);
        var rng = new Random(int.Parse(Arg("seed", "1"), CultureInfo.InvariantCulture));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var map = PhysicsReplays.Load(mapId, PhysicsReplaySpike.Folders);
        var ecs = map.World;
        var geo = map.Grid.Geometry ?? throw new InvalidOperationException("no triangle world");
        Console.WriteLine($"  {mapId} loaded in {clock.Elapsed.TotalSeconds:F1} s");

        // Floors to stand on, near the solids of every kind: a random solid, a point in or beside its
        // footprint, the floor under a random height up to a little over its top.
        var solids = new List<(Vector3 Position, Vector3 Size)>();
        ecs.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithNone<Velocity>(), (Entity e, ref Transform t, ref ColliderComponent c) =>
        {
            if (c.IsSolid && c.Size.X * c.Size.Z < 4000f) solids.Add((t.Position, c.Size));
        });
        var any = new OpenFPS.Common.Geometry.AcceptAll();
        Vector3? Standing()
        {
            for (int tries = 0; tries < 20; tries++)
            {
                var s = solids[rng.Next(solids.Count)];
                float x = s.Position.X + (float)(rng.NextDouble() - 0.5) * (s.Size.X + 4f);
                float z = s.Position.Z + (float)(rng.NextDouble() - 0.5) * (s.Size.Z + 4f);
                float top = s.Position.Y + s.Size.Y * 0.5f + 0.5f;
                float floor = geo.FloorAt(x, z, (float)(rng.NextDouble() * (top - map.Data.WalkMin.Y)) + map.Data.WalkMin.Y, OpenFPS.Common.Geometry.GeometryLayers.Ground, ref any, out _);
                if (floor > -999f) return new Vector3(x, floor, z);
            }
            return null;
        }

        Vector3 OnTop(Transform t)
        {
            var local = new Vector3((float)(rng.NextDouble() - 0.5) * 1.2f, 0.75f, (float)(rng.NextDouble() - 0.5) * 4f);
            return t.Position + Vector3.Transform(local, t.Rotation);
        }

        // Parked boxes, moving things to both sides, standing on floors.
        var movers = new List<Entity>();
        for (int i = 0; i < moverCount; i++)
            if (Standing() is { } at)
            {
                var e = ecs.Create(new Transform { Position = at + new Vector3(0f, 0.75f, 0f), Rotation = Quaternion.CreateFromYawPitchRoll((float)(rng.NextDouble() * Math.PI), 0, 0), Scale = Vector3.One },
                    new Velocity(), new MaterialComponent { Material = "Metal" }, new IdentityComponent { Name = "parked box" },
                    new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(1.8f, 1.5f, 4.6f), IsSolid = true });
                map.Maps.IndexEntity(mapId, e);
                movers.Add(e);
            }
        var body = ecs.Create(new PlayerComponent { Username = "parity", ConnectionId = 1 }, EntityType.Player,
            new Transform { Position = new Vector3(map.Data.WalkMin.X, 0f, map.Data.WalkMin.Z), Rotation = Quaternion.Identity, Scale = Vector3.One },
            new Velocity(), new MaterialComponent { Material = PhysicsConstants.PersonMaterial },
            new ColliderComponent { Shape = ColliderShape.Cylinder, Size = new Vector3(0.6f, PhysicsConstants.PlayerHeight, 0.6f), IsSolid = true });
        map.Maps.IndexEntity(mapId, body);
        map.Grid.Clear();
        ecs.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(),
            (Entity e, ref Transform t, ref ColliderComponent c) => map.Grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, false));

        clock.Restart();
        var snap = PhysicsReplays.ClientWorld(map, map.Maps.GetSpawnPoint(mapId).Position, movers, full: true);
        Console.WriteLine($"  client world (every static and {movers.Count} parked boxes) built in {clock.Elapsed.TotalSeconds:F1} s; "
                          + $"{snap.Geometry?.TriangleCount ?? 0:N0} triangles");
        var state = new LocalPlayerState();
        var physics = new ClientPhysicsSystem(state, new SpatialService()) { OwnEntityId = body.Id, MapMin = map.Data.WalkMin, MapMax = map.Data.WalkMax };

        var paths = Paths(map, body, snap);
        var pathTallies = paths.Select(_ => new Tally()).ToList();
        var ground = new Tally();
        var groundMovers = new Tally();
        var memo = new Tally();
        var memoStep = new Tally();
        var gather = new Tally();
        var step = new Tally();
        var stepOnBoxes = new Tally();
        int gatherOrderOnly = 0;
        clock.Restart();
        for (int i = 0; i < n; i++)
        {
            // One point in ten on a parked box's roof, where the two sides' floors part (risk 1).
            Vector3? where = i % 10 == 9 && movers.Count > 0 ? OnTop(ecs.Get<Transform>(movers[rng.Next(movers.Count)])) : Standing();
            if (where is not { } at) continue;
            float a = (float)(rng.NextDouble() * Math.PI * 2);
            var probe = new Probe(at, new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)), rng.Next(4) == 0, rng.Next(10) == 0);

            for (int k = 0; k < paths.Count; k++)
            {
                var o = paths[k].Old(probe);
                var w = paths[k].New(probe);
                double d = o.Zip(w, (x, y) => (double)MathF.Abs(x - y)).Max();
                bool bits = o.Zip(w, (x, y) => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y)).All(b => b);
                pathTallies[k].Add(bits ? 0 : Math.Max(d, 1e-9), () => $"{V(at)}: old [{string.Join(", ", o)}] new [{string.Join(", ", w)}]", over: 0);
            }

            // Risk 1: the two sides' floors at the same point, and whether the moving things made the difference.
            float gs = PhysicsUtils.GetGroundHeight(ecs, map.Grid, at, out _);
            float gc = PhysicsUtils.GetGroundHeight(snap, at, body.Id, out _);
            ground.Add(MathF.Abs(gs - gc), () => $"{V(at)}: server {gs:F3}, client {gc:F3}");
            var near = new List<Entity>();
            map.Grid.CollectDynamicInRadius(at, 50f, near, new HashSet<Entity>());
            float gsBare = PhysicsUtils.GetGroundHeight(ecs, map.Grid, at, near, out _);
            if (MathF.Abs(gs - gc) > 0.001f) groundMovers.Add(MathF.Abs(gs - gsBare), () => $"{V(at)}: server {gs:F3} without movers {gsBare:F3}");

            // Risk 2: a remembered floor handed back for a point up to 5 cm away, as the memo did before stage 3
            // (it hands back only the same point now); and the step it would give.
            float r = (float)rng.NextDouble() * OldMemoToleranceMetres, ra = (float)(rng.NextDouble() * Math.PI * 2);
            var nudged = at + new Vector3(r * MathF.Cos(ra), 0f, r * MathF.Sin(ra));
            float gn = PhysicsUtils.GetGroundHeight(ecs, map.Grid, nudged, out _);
            memo.Add(MathF.Abs(gn - gs), () => $"{V(nudged)}: fresh {gn:F3}, remembered from {r * 100f:F1} cm away {gs:F3}");
            if (MathF.Abs(gn - gs) > 0.001f)
            {
                var st = new PhysicsReplays.Step(probe.Direction, probe.Sprint, probe.Jump, "");
                var c1 = PhysicsReplays.ServerContext(map, body, nudged, Vector3.Zero, st, gn, out var g1, out var s1);
                var c2 = PhysicsReplays.ServerContext(map, body, nudged, Vector3.Zero, st, gs, out _, out _);
                var bx = PhysicsReplays.ServerGather(map, body, nudged).Boxes.ToArray();
                var fresh = SharedMovementEngine.Step(c1, new SharedMovementEngine.Obstacles(bx, g1, s1.ToArray()), out _);
                var stale = SharedMovementEngine.Step(c2, new SharedMovementEngine.Obstacles(bx, g1, s1.ToArray()), out _);
                memoStep.Add(Vector3.Distance(fresh.NewPosition, stale.NewPosition), () => $"{V(nudged)}: fresh {V(fresh.NewPosition)}, stale {V(stale.NewPosition)}");
            }

            // Risk 3 and the whole step: each side's gather, and one step each from rest.
            var sg = PhysicsReplays.ServerGather(map, body, at);
            var cg = PhysicsReplays.ClientGather(snap, at, body.Id);
            bool sameSet = sg.Ids.OrderBy(x => x).SequenceEqual(cg.Ids.OrderBy(x => x));
            if (sameSet && !sg.Ids.SequenceEqual(cg.Ids)) gatherOrderOnly++;
            var stp = new PhysicsReplays.Step(probe.Direction, probe.Sprint, probe.Jump, "");
            var ctx = PhysicsReplays.ServerContext(map, body, at, Vector3.Zero, stp, gs, out var geometry, out var sol);
            var withServer = SharedMovementEngine.Step(ctx, new SharedMovementEngine.Obstacles(sg.Boxes.ToArray(), geometry, sol.ToArray()), out _);
            var withClient = SharedMovementEngine.Step(ctx, new SharedMovementEngine.Obstacles(cg.Boxes.ToArray(), geometry, sol.ToArray()), out _);
            gather.Add(Vector3.Distance(withServer.NewPosition, withClient.NewPosition),
                () => $"{V(at)}: server [{string.Join(' ', sg.Ids)}] client [{string.Join(' ', cg.Ids)}]");

            state.Position = at;
            state.Velocity = Vector3.Zero;
            state.Yaw = 0f;
            physics.Predict(new ClientInputUpdate { DeltaTime = PhysicsConstants.FixedDeltaTime, MoveDirection = probe.Direction, Sprint = probe.Sprint, Jump = probe.Jump },
                            snap, PhysicsConstants.FixedDeltaTime);
            (i % 10 == 9 && movers.Count > 0 ? stepOnBoxes : step).Add(Vector3.Distance(withServer.NewPosition, state.Position),
                () => $"{V(at)} toward {V(probe.Direction)}: server {V(withServer.NewPosition)}, client {V(state.Position)}");
        }
        Console.WriteLine($"  {ground.Probes:N0} standing points in {clock.Elapsed.TotalSeconds:F1} s\n");

        void Report(string title, Tally t, string unit = "mm", double scale = 1000)
        {
            Console.WriteLine($"  {title}: {t.Over:N0} of {t.Probes:N0} over 1 mm, worst {t.Worst * scale:F1} {unit}");
            foreach (var e in t.Examples) Console.WriteLine($"      {e}");
        }
        Console.WriteLine("  Old path against new (stage 0: the same code, so every answer the same bits):");
        for (int k = 0; k < paths.Count; k++)
            Console.WriteLine($"    {paths[k].Name}: {pathTallies[k].Over:N0} of {pathTallies[k].Probes:N0} differ"
                              + (pathTallies[k].Examples.Count > 0 ? "\n      " + string.Join("\n      ", pathTallies[k].Examples) : ""));
        Console.WriteLine("\n  The cross-side risks (docs/PHYSICS_BOUNDARY.md 3.7):");
        Report("1. server ground against client ground", ground);
        Report("   of those, made by the moving things", groundMovers);
        Report("2. the floor 0 to 5 cm away against this one (what the memo handed back before stage 3; now only the same point)", memo);
        Report("   the step that stale floor gives, against the fresh", memoStep);
        Report("3. one step with the client's gather against the server's", gather);
        Console.WriteLine($"     the two gathers held the same boxes in another order at {gatherOrderOnly:N0} points");
        Report("   one step on each side from the same state (server step, client Predict), on floors", step);
        Report("   the same, standing on a parked box", stepOnBoxes);
        return 0;
    }
}
