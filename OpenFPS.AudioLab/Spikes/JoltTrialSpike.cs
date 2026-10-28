using System.Diagnostics;
using System.Numerics;
using JoltPhysicsSharp;
using OpenFPS.Geometry;
using OpenFPS.Instruments;
using OpenFPS.Physics;

namespace OpenFPS.AudioLab;

/// <summary>
/// --jolt-trial [map=city] [place=magnolia_tx] [threads=0] [fleet]
///
/// The Jolt trial of physics stage 0 (docs/PHYSICS_BOUNDARY.md, Decisions 2026-10-11): Jolt Physics through
/// JoltPhysicsSharp, in the lab only, fed our own geometry. One city tile's static triangles as one mesh
/// shape, the whole city as one, and a place tile's terrain as a height field; then the step's cost at 120 Hz
/// with 0, 50 and 500 bodies on the city tile, a stack of ten boxes settling, a bullet-sized sphere at a thin
/// wall with continuous collision off and on, a soft ball and a cloth on the ground, and the memory each
/// takes. With <c>fleet</c> it stops after the city tile and measures a street's movers instead (docs/PHYSICS_BOUNDARY.md 18.3):
/// 40 cars and 200 walkers stepped four times a tick, cars striking standing people, and Steam Audio's commit with
/// car meshes moved as instances. A ball dropped at points of each shape must come to rest on our own floor there (FloorAt), which
/// is the check that our axes, units and windings reach Jolt unchanged. Nothing in the game references Jolt.
/// </summary>
public static class JoltTrialSpike
{
    private static readonly ObjectLayer NonMoving = 0;
    private static readonly ObjectLayer Moving = 1;
    private const float Dt = 1f / 120f;

    private static long RssKb()
    {
        foreach (var line in File.ReadLines("/proc/self/status"))
            if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
        return 0;
    }

    private static string Mb(long kb) => $"{kb / 1024.0:F1} MB";

    /// <summary>A world with the two layers the samples use: still things and moving things.</summary>
    private sealed class World : IDisposable
    {
        public readonly PhysicsSystem System;
        public readonly JobSystem Jobs;
        public readonly List<BodyID> Bodies = new();
        public BodyInterface Bi => System.BodyInterface;

        public World(int threads, int maxBodies = 8192)
        {
            var pairs = new ObjectLayerPairFilterTable(2);
            pairs.EnableCollision(NonMoving, Moving);
            pairs.EnableCollision(Moving, Moving);
            var broad = new BroadPhaseLayerInterfaceTable(2, 2);
            broad.MapObjectToBroadPhaseLayer(NonMoving, 0);
            broad.MapObjectToBroadPhaseLayer(Moving, 1);
            var settings = new PhysicsSystemSettings
            {
                MaxBodies = maxBodies, MaxBodyPairs = 65536, MaxContactConstraints = 65536,
                ObjectLayerPairFilter = pairs, BroadPhaseLayerInterface = broad,
                ObjectVsBroadPhaseLayerFilter = new ObjectVsBroadPhaseLayerFilterTable(broad, 2, pairs, 2),
            };
            System = new PhysicsSystem(settings) { Gravity = new Vector3(0f, -Earth.StandardGravity, 0f) };
            Jobs = new JobSystemThreadPool(new JobSystemThreadPoolConfig { maxJobs = 2048, maxBarriers = 8, numThreads = threads });
        }

        public BodyID Add(Shape shape, Vector3 at, Quaternion rotation, bool moving, MotionQuality quality = MotionQuality.Discrete,
                          float friction = 0.6f, float restitution = 0.1f)
        {
            // Not BodyCreationSettings.MotionQuality: in 2.22.0 its setter passes the enum by reference to a C
            // function that takes it by value, so every body made with it got quality 244 (read back), not
            // what was asked. The body interface's setter passes it by value.
            using var s = new BodyCreationSettings(shape, at, rotation, moving ? MotionType.Dynamic : MotionType.Static, moving ? Moving : NonMoving)
            {
                Friction = friction, Restitution = restitution,
            };
            var id = Bi.CreateAndAddBody(s, moving ? Activation.Activate : Activation.DontActivate);
            if (moving) Bi.SetMotionQuality(id, quality);
            Bodies.Add(id);
            return id;
        }

        /// <summary>Steps <paramref name="n"/> times; each step's milliseconds.</summary>
        public List<double> Step(int n)
        {
            var times = new List<double>(n);
            var clock = new Stopwatch();
            for (int i = 0; i < n; i++)
            {
                clock.Restart();
                var error = System.Update(Dt, 1, Jobs);
                times.Add(clock.Elapsed.TotalMilliseconds);
                if (error != PhysicsUpdateError.None) throw new InvalidOperationException($"Jolt step: {error}");
            }
            return times;
        }

        public void Dispose()
        {
            foreach (var id in Bodies) Bi.RemoveAndDestroyBody(id);
            Jobs.Dispose();
            System.Dispose();
        }
    }

    private static string Times(List<double> t)
    {
        var s = t.OrderBy(x => x).ToList();
        return $"mean {t.Average():F3} ms, p95 {s[(int)(s.Count * 0.95)]:F3} ms, worst {s[^1]:F3} ms";
    }

    /// <summary>Every triangle of the instances' boxes (not their terrain), in the world; with a square,
    /// only those that reach into it (a tile cut out of a map built in bigger pieces).</summary>
    private static (List<Vector3> Vertices, List<IndexedTriangle> Triangles) BoxTriangles(IEnumerable<GeometryInstance> instances,
                                                                                         Vector2? lo = null, Vector2? hi = null)
    {
        var verts = new List<Vector3>();
        var tris = new List<IndexedTriangle>();
        foreach (var inst in instances)
        {
            var piece = inst.Piece;
            int boxes = piece.TriangleCount - (piece.Terrain?.TriangleCount ?? 0);
            for (int i = 0; i < boxes; i++)
            {
                var t = piece.Triangle(i);
                Vector3 W(Vector3 local) => inst.Position + (inst.Rotated ? Vector3.Transform(local, inst.Rotation) : local);
                Vector3 a = W(t.V0), b = W(t.V0 + t.E1), c = W(t.V0 + t.E2);
                var min = Vector3.Min(a, Vector3.Min(b, c));
                var max = Vector3.Max(a, Vector3.Max(b, c));
                if (lo is { } l && hi is { } h && (max.X < l.X || min.X > h.X || max.Z < l.Y || min.Z > h.Y)) continue;
                int n = verts.Count;
                verts.Add(a);
                verts.Add(b);
                verts.Add(c);
                tris.Add(new IndexedTriangle(n, n + 1, n + 2));
            }
        }
        return (verts, tris);
    }

    /// <summary>The side of a streamed tile, m: the places' and the world's (docs/WORLD_STREAMING.md).</summary>
    private const float TileMetres = 250f;

    private static Shape Mesh(List<Vector3> verts, List<IndexedTriangle> tris)
    {
        using var settings = new MeshShapeSettings(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(verts),
                                                   System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tris));
        return settings.Create();
    }

    /// <summary>The solids of one owner only: a tile's terrain.</summary>
    private struct OnlyOwner : IGeometryFilter
    {
        public int Owner;
        public readonly bool Accept(int owner, in Surface surface) => owner == Owner;
    }

    /// <summary>A ball let fall at each point comes to rest where our floor is: the difference, worst.
    /// <paramref name="owner"/>: only that owner's floor (a terrain); null, every floor.</summary>
    private static (double Worst, int Resting, int Count) RestsOnOurFloor(Shape ground, Vector3 groundAt, TriangleWorld ours, IEnumerable<(float X, float Z)> points,
                                                                          int threads, int? owner = null)
    {
        using var w = new World(threads);
        w.Add(ground, groundAt, Quaternion.Identity, moving: false);
        var any = new AcceptAll();
        var only = new OnlyOwner { Owner = owner ?? 0 };
        var balls = new List<(BodyID Id, float Floor)>();
        using var sphere = new SphereShape(0.1f);
        foreach (var (x, z) in points)
        {
            float floor = owner == null ? ours.FloorAt(x, z, 300f, GeometryLayers.Ground, ref any, out _)
                                        : ours.FloorAt(x, z, 300f, GeometryLayers.All, ref only, out _);
            if (floor < -999f) continue;
            balls.Add((w.Add(sphere, new Vector3(x, floor + 0.5f, z), Quaternion.Identity, moving: true, friction: 1f, restitution: 0f), floor));
        }
        w.System.OptimizeBroadPhase();
        w.Step(360);
        double worst = 0;
        int resting = 0, shown = 0;
        foreach (var (id, _) in balls)
        {
            // Our floor where the ball ended: on a slope it rolls.
            var at = w.Bi.GetCenterOfMassPosition(id);
            float floor = owner == null ? ours.FloorAt(at.X, at.Z, 300f, GeometryLayers.Ground, ref any, out _)
                                        : ours.FloorAt(at.X, at.Z, 300f, GeometryLayers.All, ref only, out _);
            float bottom = at.Y - 0.1f;
            worst = Math.Max(worst, Math.Abs(bottom - floor));
            if (Math.Abs(bottom - floor) < 0.02f) resting++;
            else if (shown++ < 3) Console.WriteLine($"      a ball came to rest at ({at.X:F2}, {bottom:F3}, {at.Z:F2}); our floor there {floor:F3}");
        }
        return (worst, resting, balls.Count);
    }

    public static int Run(string[] args)
    {
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string Arg(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..] ?? fallback;
        int threads = int.Parse(Arg("threads", "0"));
        long rss0 = RssKb();
        if (!Foundation.Init(false)) { Console.WriteLine("FAIL: Jolt's Foundation.Init returned false"); return 1; }
        long rssInit = RssKb();
        Console.WriteLine($"  Jolt up: native libjoltc from JoltPhysics.Native, {System.Environment.OSVersion}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; "
                          + $"{threads} worker thread(s); RSS {Mb(rss0)} -> {Mb(rssInit)}");

        // ── The city: one tile's static triangles, and the whole city, as mesh shapes ────────────────
        var city = PhysicsReplays.Load(Arg("map", "city"), PhysicsReplaySpike.Folders);
        var geo = city.Grid.Geometry!;
        var spawn = city.Maps.GetSpawnPoint(city.Id).Position;
        var statics = geo.Instances.ToArray().Where(i => i.Owner < 0 && !i.Piece.IsEmpty).ToList();
        // The city is built in a few large pieces, not streamed tiles: the tile is the 250 m square about
        // the spawn cut out of them, every triangle that reaches into it.
        var lo = new Vector2(spawn.X - TileMetres / 2f, spawn.Z - TileMetres / 2f);
        var hi = new Vector2(spawn.X + TileMetres / 2f, spawn.Z + TileMetres / 2f);
        var (tv, tt) = BoxTriangles(statics, lo, hi);
        long rssBefore = RssKb();
        var clock = Stopwatch.StartNew();
        var tileMesh = Mesh(tv, tt);
        double tileBuild = clock.Elapsed.TotalMilliseconds;
        long rssTile = RssKb();
        Console.WriteLine($"\n  City tile about the spawn ({lo.X:F0}..{hi.X:F0} east, {lo.Y:F0}..{hi.Y:F0} north), from the city's {statics.Count} static pieces: "
                          + $"{tt.Count:N0} triangles as one mesh shape in {tileBuild:F0} ms, RSS +{Mb(rssTile - rssBefore)}");
        var (cv, ct) = BoxTriangles(statics);
        clock.Restart();
        rssBefore = RssKb();
        var cityMesh = Mesh(cv, ct);
        Console.WriteLine($"  The whole city ({statics.Count} static pieces): {ct.Count:N0} triangles as one mesh shape in {clock.Elapsed.TotalMilliseconds:F0} ms, "
                          + $"RSS +{Mb(RssKb() - rssBefore)}");

        if (args.Contains("fleet")) { Fleet(tileMesh, geo, spawn, statics, lo, hi, threads); Foundation.Shutdown(); return 0; }

        var rng = new Random(1);
        var points = Enumerable.Range(0, 64).Select(_ => (lo.X + 2f + (float)rng.NextDouble() * (TileMetres - 4f), lo.Y + 2f + (float)rng.NextDouble() * (TileMetres - 4f))).ToList();
        var (worst, resting, count) = RestsOnOurFloor(tileMesh, Vector3.Zero, geo, points, threads);
        Console.WriteLine($"  balls dropped on the tile's mesh: {resting} of {count} at rest on our floor to 2 cm, worst {worst * 1000:F1} mm off it");

        // ── The step's cost at 120 Hz over the city tile ────────────────────────────────────────────
        Console.WriteLine("\n  Step cost at 120 Hz on the city tile (one collision step; boxes 0.5 m and balls 0.3 m dropped from 2 to 12 m):");
        foreach (int n in new[] { 0, 50, 500 })
        {
            rssBefore = RssKb();
            using var w = new World(threads);
            w.Add(tileMesh, Vector3.Zero, Quaternion.Identity, moving: false);
            using var box = new BoxShape(new Vector3(0.25f));
            using var ball = new SphereShape(0.15f);
            var any = new AcceptAll();
            for (int i = 0; i < n; i++)
            {
                float x = spawn.X + (float)(rng.NextDouble() - 0.5) * 30f, z = spawn.Z + (float)(rng.NextDouble() - 0.5) * 30f;
                float floor = MathF.Max(geo.FloorAt(x, z, 300f, GeometryLayers.Ground, ref any, out _), spawn.Y);
                w.Add(i % 2 == 0 ? box : ball, new Vector3(x, floor + 2f + (float)rng.NextDouble() * 10f, z),
                      Quaternion.CreateFromYawPitchRoll((float)rng.NextDouble() * 6f, (float)rng.NextDouble(), 0f), moving: true);
            }
            w.System.OptimizeBroadPhase();
            var falling = w.Step(240);
            var later = w.Step(720);
            uint awake = w.System.GetNumActiveBodies(BodyType.Rigid);
            Console.WriteLine($"    {n,4} bodies: first 2 s {Times(falling)}; next 6 s {Times(later)}; {awake} awake at 8 s; RSS +{Mb(RssKb() - rssBefore)}");
        }

        // ── A stack of ten boxes ────────────────────────────────────────────────────────────────────
        {
            using var w = new World(threads);
            using var floorShape = new BoxShape(new Vector3(50f, 0.5f, 50f));
            w.Add(floorShape, new Vector3(0f, -0.5f, 0f), Quaternion.Identity, moving: false);
            using var box = new BoxShape(new Vector3(0.25f));
            var stack = new List<BodyID>();
            for (int i = 0; i < 10; i++) stack.Add(w.Add(box, new Vector3(0f, 0.25f + i * 0.501f, 0f), Quaternion.Identity, moving: true, friction: 0.6f, restitution: 0f));
            var t = w.Step(1200);
            var top = w.Bi.GetCenterOfMassPosition(stack[^1]);
            float drift = stack.Max(id => { var p = w.Bi.GetCenterOfMassPosition(id); return MathF.Sqrt(p.X * p.X + p.Z * p.Z); });
            int asleep = stack.Count(id => !w.Bi.IsActive(id));
            Console.WriteLine($"\n  Ten 0.5 m boxes stacked: after 10 s the top box's centre is at {top.Y:F4} m (4.75 m resting exactly), "
                              + $"the most any drifted sideways {drift * 1000:F2} mm, {asleep} of 10 asleep; step {Times(t)}");
        }

        // ── A bullet at a thin wall ─────────────────────────────────────────────────────────────────
        Console.WriteLine();
        foreach (var (radius, speed, what) in new[] { (0.0039f, 800f, "a 7.8 mm ball (a .308's diameter)"), (0.0039f, 100f, "the same ball"),
                                                      (0.03f, 15f, "a 6 cm ball (a thrown stone)") })
            foreach (var quality in new[] { MotionQuality.Discrete, MotionQuality.LinearCast })
            {
                using var w = new World(threads);
                using var wall = new BoxShape(new Vector3(2f, 2f, 0.05f));      // 10 cm thick
                w.Add(wall, new Vector3(0f, 1.5f, 20f), Quaternion.Identity, moving: false);
                using var bullet = new SphereShape(radius);
                // From 0.3 m, so no discrete step of any of these speeds happens to land inside the wall.
                using var s = new BodyCreationSettings(bullet, new Vector3(0f, 1.5f, 0.3f), Quaternion.Identity, MotionType.Dynamic, Moving)
                {
                    Restitution = 0f, MaxLinearVelocity = 2000f, GravityFactor = 0f,
                };
                var id = w.Bi.CreateAndAddBody(s, Activation.Activate);
                w.Bi.SetMotionQuality(id, quality);
                w.Bodies.Add(id);
                w.Bi.SetLinearVelocity(id, new Vector3(0f, 0f, speed));
                w.Step((int)MathF.Ceiling(40f / speed / Dt));                   // long enough to go 40 m
                var p = w.Bi.GetCenterOfMassPosition(id);
                Console.WriteLine($"  {what} at {speed:F0} m/s, 20 m from a 10 cm wall, {w.Bi.GetMotionQuality(id)}: ends {p.Z:F2} m north, "
                                  + $"{(p.Z > 20.05f ? "through the wall" : "stopped by it")} ({speed * Dt * 100f:F0} cm a step)");
            }

        // ── Soft bodies: a ball and a cloth ─────────────────────────────────────────────────────────
        {
            using var w = new World(threads);
            using var floorShape = new BoxShape(new Vector3(50f, 0.5f, 50f));
            w.Add(floorShape, new Vector3(0f, -0.5f, 0f), Quaternion.Identity, moving: false);
            using var table = new BoxShape(new Vector3(0.5f, 0.4f, 0.5f));
            w.Add(table, new Vector3(4f, 0.4f, 0f), Quaternion.Identity, moving: false);

            var ballSettings = SoftBall(0.5f, 12, 24, out int ballVertices);
            using var ballCreate = new SoftBodyCreationSettings(ballSettings, new Vector3(0f, 1.5f, 0f), Quaternion.Identity, Moving)
            {
                Pressure = 2000f, Friction = 0.6f, Restitution = 0f,
            };
            var softBall = w.Bi.CreateSoftBody(ballCreate);
            w.Bi.AddBody(softBall, Activation.Activate);
            w.Bodies.Add(softBall.ID);

            var clothSettings = Cloth(21, 0.1f, out int clothVertices);
            using var clothCreate = new SoftBodyCreationSettings(clothSettings, new Vector3(4f, 1.5f, 0f), Quaternion.Identity, Moving) { Friction = 0.6f };
            var cloth = w.Bi.CreateSoftBody(clothCreate);
            w.Bi.AddBody(cloth, Activation.Activate);
            w.Bodies.Add(cloth.ID);

            var t = w.Step(480);
            var b = softBall.WorldSpaceBounds;
            var c = cloth.WorldSpaceBounds;
            Console.WriteLine($"\n  A soft ball, 1 m across, {ballVertices} vertices, pressure 2 kPa, dropped from 1 m: after 4 s it spans "
                              + $"{b.Min.Y:F3} to {b.Max.Y:F3} m high ({(b.Max.Y - b.Min.Y) * 100:F1} cm tall, {(b.Max.X - b.Min.X) * 100:F1} cm wide)");
            Console.WriteLine($"  A 2 m cloth, {clothVertices} vertices, dropped on a 1 m table 0.8 m high: after 4 s it spans {c.Min.Y:F3} to {c.Max.Y:F3} m high, "
                              + $"{c.Min.X - 4f:F2} to {c.Max.X - 4f:F2} m across the table's middle");
            Console.WriteLine($"  the two soft bodies' step: {Times(t)}");
        }

        // ── A place's terrain as a height field ─────────────────────────────────────────────────────
        string placeId = Arg("place", "magnolia_tx");
        var place = PhysicsReplays.Load(placeId, PhysicsReplaySpike.Folders);
        var pgeo = place.Grid.Geometry!;
        var pspawn = place.Maps.GetSpawnPoint(placeId).Position;
        var withTerrain = pgeo.Instances.ToArray().Where(i => i.Owner < 0 && i.Piece.Terrain != null).ToList();
        var ground = withTerrain.FirstOrDefault(i => pspawn.X >= i.Min.X && pspawn.X <= i.Max.X && pspawn.Z >= i.Min.Z && pspawn.Z <= i.Max.Z);
        if (ground.Piece == null) ground = withTerrain[0];
        var hf = ground.Piece.Terrain!;
        var corner = ground.Position + ground.Piece.TerrainOffset;
        var samples = (float[])hf.Heights.Clone();
        clock.Restart();
        rssBefore = RssKb();
        // Jolt's sample (x, y) is at offset + scale * (x, height, y): x east along our posts' i, y north along j.
        using var hfSettings = new HeightFieldShapeSettings(samples, corner, new Vector3(hf.Spacing, 1f, hf.Spacing), (uint)hf.Posts);
        Shape? hfShape = null;
        string hfNote;
        try
        {
            hfShape = new HeightFieldShape(hfSettings);
            if (hfShape is HeightFieldShape hs)
            {
                double worstPost = 0;
                for (uint j = 0; j < hf.Posts; j += 5)
                    for (uint i = 0; i < hf.Posts; i += 5)
                        worstPost = Math.Max(worstPost, Math.Abs(hs.GetPosition(i, j).Y + corner.Y - (corner.Y + hf.HeightOfPost((int)i, (int)j))));
                Console.WriteLine($"    Jolt's posts against ours: worst {worstPost * 1000:F1} mm (its samples are stored in 8 bits a block); "
                                  + $"post (0,0) at {hs.GetPosition(0, 0)}, ours {new Vector3(0f, hf.HeightOfPost(0, 0), 0f)}");
            }
            hfNote = $"{hf.Posts} x {hf.Posts} posts {hf.Spacing:F2} m apart as a height field in {clock.Elapsed.TotalMilliseconds:F0} ms, RSS +{Mb(RssKb() - rssBefore)}";
        }
        catch (Exception e) { hfNote = $"refused: {e.Message}"; }
        Console.WriteLine($"\n  {placeId}'s ground at the spawn (tile corner {corner.X:F0}, {corner.Z:F0}): {hfNote}");
        if (hfShape != null)
        {
            float size = hf.Spacing * (hf.Posts - 1);
            var hpoints = Enumerable.Range(0, 64).Select(_ => (corner.X + 1f + (float)rng.NextDouble() * (size - 2f), corner.Z + 1f + (float)rng.NextDouble() * (size - 2f))).ToList();
            var (hw, hr, hc) = RestsOnOurFloor(hfShape, Vector3.Zero, pgeo, hpoints, threads, ground.Piece.TerrainOwner);
            Console.WriteLine($"  balls dropped on it: {hr} of {hc} at rest on our floor to 2 cm, worst {hw * 1000:F1} mm off it "
                              + "(our floor here is the tile's terrain alone)");
        }
        var pstatics = pgeo.Instances.ToArray().Where(i => i.Owner < 0 && !i.Piece.IsEmpty).ToList();
        var (pv, pt) = BoxTriangles(pstatics);
        clock.Restart();
        rssBefore = RssKb();
        try
        {
            var placeMesh = Mesh(pv, pt);
            Console.WriteLine($"  {placeId}'s buildings and boxes, every tile ({pstatics.Count} pieces): {pt.Count:N0} triangles as one mesh shape in "
                              + $"{clock.Elapsed.TotalMilliseconds:F0} ms, RSS +{Mb(RssKb() - rssBefore)}");
        }
        catch (Exception e) { Console.WriteLine($"  {placeId}'s buildings as one mesh: refused ({e.Message})"); }

        Console.WriteLine($"\n  RSS at the end {Mb(RssKb())}; managed heap {GC.GetTotalMemory(false) / 1048576.0:F1} MB");
        Foundation.Shutdown();
        return 0;
    }

    // ── The fleet: a street's worth of movers (physics stage 4 survey, 2026-10-27) ───────────────────

    [System.Runtime.InteropServices.DllImport("phonon", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern void iplInstancedMeshUpdateTransform(IntPtr mesh, IntPtr scene, OpenFPS.Native.SteamAudio.Phonon.IPLMatrix4x4 transform);

    /// <summary>Row-major, translation in the last column, z mirrored as Phonon.World does.</summary>
    private static unsafe OpenFPS.Native.SteamAudio.Phonon.IPLMatrix4x4 Translate(Vector3 t)
    {
        var m = new OpenFPS.Native.SteamAudio.Phonon.IPLMatrix4x4();
        m.elements[0] = 1; m.elements[5] = 1; m.elements[10] = 1; m.elements[15] = 1;
        m.elements[3] = t.X; m.elements[7] = t.Y; m.elements[11] = -t.Z;
        return m;
    }

    private static void Fleet(Shape tileMesh, TriangleWorld geo, Vector3 spawn, List<GeometryInstance> statics, Vector2 lo, Vector2 hi, int threads)
    {
        var rng = new Random(3);
        var any = new AcceptAll();
        // Street level: where our floor is the spawn's, so cars and people stand on the road, not a roof.
        Vector3 Spot(float yUp)
        {
            for (int tries = 0; tries < 2000; tries++)
            {
                float x = spawn.X + (float)(rng.NextDouble() - 0.5) * 200f, z = spawn.Z + (float)(rng.NextDouble() - 0.5) * 200f;
                float floor = geo.FloorAt(x, z, 300f, GeometryLayers.Ground, ref any, out _);
                if (MathF.Abs(floor - spawn.Y) < 0.3f) return new Vector3(x, floor + yUp, z);
            }
            return spawn + new Vector3(0f, yUp, 0f);
        }

        Console.WriteLine("\n  Fleet on the city tile: 40 cars (kinematic boxes 4.5 x 1.4 x 1.8 m, 8 to 15 m/s), 200 walkers (capsules, 1.4 m/s), 120 Hz x 4 a tick,");
        Console.WriteLine("  300 ticks (10 s). A tick = every mover's velocity set, four steps, every mover's pose read back. Single thread.");
        foreach (var (label, debris, standing) in new[] { ("movers only", 0, false), ("+ 100 dynamic boxes on the street", 100, false),
                                                          ("walkers dynamic and standing in the cars' way (cars strike people)", 0, true),
                                                          ("the same again, same seed", 0, true) })
        {
            rng = new Random(3);
            using var w = new World(threads);
            w.Add(tileMesh, Vector3.Zero, Quaternion.Identity, moving: false);
            using var carShape = new BoxShape(new Vector3(2.25f, 0.7f, 0.9f));
            using var personShape = new CapsuleShape(0.55f, 0.3f);
            var movers = new List<(BodyID Id, Vector3 Velocity, bool Kinematic)>();
            BodyID Make(Shape shape, Vector3 at, MotionType type, float mass = 0f)
            {
                using var s = new BodyCreationSettings(shape, at, Quaternion.Identity, type, type == MotionType.Static ? NonMoving : Moving) { Friction = 0.6f, Restitution = 0.1f };
                if (mass > 0f) { s.OverrideMassProperties = OverrideMassProperties.CalculateInertia; s.MassPropertiesOverride = new MassProperties { Mass = mass }; }
                var id = w.Bi.CreateAndAddBody(s, type == MotionType.Static ? Activation.DontActivate : Activation.Activate);
                w.Bodies.Add(id);
                return id;
            }
            for (int i = 0; i < 40; i++)
            {
                float speed = 8f + (float)rng.NextDouble() * 7f;
                var dir = i % 2 == 0 ? Vector3.UnitX : Vector3.UnitZ;
                movers.Add((Make(carShape, Spot(0.7f), MotionType.Kinematic), dir * speed * (i % 4 < 2 ? 1f : -1f), true));
            }
            for (int i = 0; i < 200; i++)
            {
                var at = Spot(0.85f);
                if (standing)
                {
                    // Ahead of one of the cars, a second or so down its road.
                    var car = movers[i % 40];
                    at = w.Bi.GetCenterOfMassPosition(car.Id) + car.Velocity * (0.5f + (float)rng.NextDouble() * 6f);
                    at.Y = spawn.Y + 0.85f;
                    movers.Add((Make(personShape, at, MotionType.Dynamic, 75f), Vector3.Zero, false));
                }
                else
                {
                    var dir = new Vector3(MathF.Cos(i), 0f, MathF.Sin(i));
                    movers.Add((Make(personShape, at, MotionType.Kinematic), dir * 1.4f, true));
                }
            }
            using var box = new BoxShape(new Vector3(0.25f));
            for (int i = 0; i < debris; i++) Make(box, Spot(0.3f), MotionType.Dynamic, 5f);
            w.System.OptimizeBroadPhase();
            var ticks = new List<double>(300);
            var sw = new Stopwatch();
            double read = 0;
            for (int t = 0; t < 300; t++)
            {
                sw.Restart();
                for (int i = 0; i < movers.Count; i++)
                {
                    var (id, v, kin) = movers[i];
                    if (!kin) continue;
                    var p = w.Bi.GetCenterOfMassPosition(id);
                    if (MathF.Abs(p.X - spawn.X) > 110f || MathF.Abs(p.Z - spawn.Z) > 110f) { v = -v; movers[i] = (id, v, kin); }
                    w.Bi.SetLinearVelocity(id, v);
                }
                for (int k = 0; k < 4; k++)
                {
                    var e = w.System.Update(Dt, 1, w.Jobs);
                    if (e != PhysicsUpdateError.None) throw new InvalidOperationException($"Jolt step: {e}");
                }
                long r0 = Stopwatch.GetTimestamp();
                float sink = 0f;
                foreach (var (id, _, _) in movers) { var p = w.Bi.GetCenterOfMassPosition(id); var q = w.Bi.GetRotation(id); sink += p.X + q.W; }
                read += Stopwatch.GetElapsedTime(r0).TotalMilliseconds + (sink == float.MaxValue ? 1 : 0);
                ticks.Add(sw.Elapsed.TotalMilliseconds);
            }
            uint awake = w.System.GetNumActiveBodies(BodyType.Rigid);
            float fastest = 0f; int thrown = 0;
            if (standing)
                foreach (var (id, _, kin) in movers)
                    if (!kin)
                    {
                        var p = w.Bi.GetCenterOfMassPosition(id); var v = w.Bi.GetLinearVelocity(id);
                        if (v.Length() > 0.5f || p.Y > spawn.Y + 1.2f) thrown++;
                        fastest = MathF.Max(fastest, v.Length());
                    }
            var worstAt = ticks.IndexOf(ticks.Max());
            // Run-to-run: the same scene twice in one process lands every person in the same place to the bit.
            var hash = new HashCode();
            foreach (var (id, _, kin) in movers) { var p = w.Bi.GetCenterOfMassPosition(id); hash.Add(p.X); hash.Add(p.Y); hash.Add(p.Z); }
            Console.WriteLine($"    {label}: final positions hash {hash.ToHashCode():x8}");
            Console.WriteLine($"    {label}: tick {Times(ticks)} (worst was tick {worstAt}; the other 299 average {(ticks.Sum() - ticks.Max()) / 299:F3} ms); pose read-back {read / 300:F3} ms of it; {awake} awake at the end"
                              + (standing ? $"; {thrown} of 200 people moving or lifted at the end, fastest {fastest:F1} m/s" : ""));
        }

        // ── Steam Audio: the cost of moving vehicle meshes (instances in an Embree top scene) ────────
        Console.WriteLine("\n  Steam Audio top scene, Embree: the city tile's boxes as one sub-scene instanced 13 times (the streamed tiles of a view),");
        Console.WriteLine("  plus N car sub-scene instances (a saloon of 3 boxes, 36 triangles), each given a new transform and the scene committed:");
        var cs = OpenFPS.Native.SteamAudio.Phonon.DefaultContextSettings();
        if (OpenFPS.Native.SteamAudio.Phonon.iplContextCreate(ref cs, out IntPtr ctx) != OpenFPS.Native.SteamAudio.Phonon.IPL_STATUS_SUCCESS)
        { Console.WriteLine("    no Steam Audio context here (libphonon not found): not measured"); return; }
        bool embree = OpenFPS.Audio.SteamAudio.SteamAudioScene.UseEmbree(ctx);
        Console.WriteLine($"    Embree {(embree ? "on" : "NOT available: default tracer")}");
        var cityBoxes = new List<OpenFPS.Audio.SteamAudio.SteamAudioScene.Box>();
        foreach (var inst in statics)
        {
            var piece = inst.Piece;
            if (inst.Position.X < lo.X - 100 || inst.Position.X > hi.X + 100 || inst.Position.Z < lo.Y - 100 || inst.Position.Z > hi.Y + 100) continue;
            for (int s = 0; s < piece.SolidCount; s++)
            {
                ref readonly var rec = ref piece.Solid(s);
                if (rec.BoxSize.X <= 0 || rec.BoxSize.Y <= 0 || rec.BoxSize.Z <= 0) continue;
                cityBoxes.Add(new(rec.PlacedAt, rec.BoxSize, rec.BoxRotation, piece.Surfaces[rec.Surface].Material));
            }
        }
        using var citySub = new OpenFPS.Audio.SteamAudio.SteamAudioScene(ctx);
        citySub.Build(cityBoxes);
        using var carSub = new OpenFPS.Audio.SteamAudio.SteamAudioScene(ctx);
        carSub.Build(new List<OpenFPS.Audio.SteamAudio.SteamAudioScene.Box>
        {
            new(new Vector3(0f, 0.6f, 0f), new Vector3(1.8f, 0.6f, 4.5f), Quaternion.Identity, "Metal"),
            new(new Vector3(0f, 1.15f, -0.2f), new Vector3(1.6f, 0.5f, 2.2f), Quaternion.Identity, "Glass"),
            new(new Vector3(0f, 0.3f, 0f), new Vector3(1.7f, 0.2f, 4.2f), Quaternion.Identity, "Metal"),
        });
        Console.WriteLine($"    city sub-scene: {cityBoxes.Count} boxes ({cityBoxes.Count * 12:N0} triangles); car sub-scene: 36 triangles");
        foreach (int cars in new[] { 0, 1, 10, 40, 100 })
        foreach (int moving in new[] { cars, cars / 2 }.Distinct())
        {
            var top = OpenFPS.Audio.SteamAudio.SteamAudioScene.CreateScene(ctx);
            var tiles = new IntPtr[13];
            for (int i = 0; i < 13; i++)
            {
                var ts = new OpenFPS.Native.SteamAudio.Phonon.IPLInstancedMeshSettings { subScene = citySub.Handle, transform = Translate(new Vector3((i % 4) * 250f, 0f, (i / 4) * 250f)) };
                OpenFPS.Native.SteamAudio.Phonon.iplInstancedMeshCreate(top, ref ts, out tiles[i]);
                OpenFPS.Native.SteamAudio.Phonon.iplInstancedMeshAdd(tiles[i], top);
            }
            var inst = new IntPtr[cars];
            var at = Enumerable.Range(0, cars).Select(_ => Spot(0f)).ToArray();
            for (int i = 0; i < cars; i++)
            {
                var cst = new OpenFPS.Native.SteamAudio.Phonon.IPLInstancedMeshSettings { subScene = carSub.Handle, transform = Translate(at[i]) };
                OpenFPS.Native.SteamAudio.Phonon.iplInstancedMeshCreate(top, ref cst, out inst[i]);
                OpenFPS.Native.SteamAudio.Phonon.iplInstancedMeshAdd(inst[i], top);
            }
            var clock = Stopwatch.StartNew();
            OpenFPS.Native.SteamAudio.Phonon.iplSceneCommit(top);
            double first = clock.Elapsed.TotalMilliseconds;
            var times = new List<double>();
            for (int f = 0; f < 200; f++)
            {
                clock.Restart();
                for (int i = 0; i < moving; i++)
                {
                    at[i] += new Vector3(0.4f, 0f, 0f);
                    iplInstancedMeshUpdateTransform(inst[i], top, Translate(at[i]));
                }
                OpenFPS.Native.SteamAudio.Phonon.iplSceneCommit(top);
                times.Add(clock.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"    {cars,3} cars, {moving,3} moved a commit: first commit {first:F2} ms; later {Times(times)}");
            for (int i = 0; i < cars; i++) OpenFPS.Native.SteamAudio.Phonon.iplInstancedMeshRelease(ref inst[i]);
            for (int i = 0; i < 13; i++) OpenFPS.Native.SteamAudio.Phonon.iplInstancedMeshRelease(ref tiles[i]);
            OpenFPS.Native.SteamAudio.Phonon.iplSceneRelease(ref top);
        }
    }

    /// <summary>A sphere of latitude rings, its faces wound outward, with edge constraints for a soft body.</summary>
    private static SoftBodySharedSettings SoftBall(float radius, int rings, int segments, out int vertices)
    {
        var s = new SoftBodySharedSettings();
        var points = new List<Vector3> { new(0f, radius, 0f) };
        for (int r = 1; r < rings; r++)
        {
            float a = MathF.PI * r / rings;
            for (int k = 0; k < segments; k++)
            {
                float b = 2f * MathF.PI * k / segments;
                points.Add(new Vector3(radius * MathF.Sin(a) * MathF.Cos(b), radius * MathF.Cos(a), radius * MathF.Sin(a) * MathF.Sin(b)));
            }
        }
        points.Add(new Vector3(0f, -radius, 0f));
        foreach (var p in points) s.AddVertex(new SoftBodySharedSettings.Vertex(p, Vector3.Zero, 1f / 0.02f));
        int Ring(int r, int k) => 1 + (r - 1) * segments + ((k % segments) + segments) % segments;
        void Face(int a, int b, int c)
        {
            // Outward: the face's normal points away from the centre.
            var n = Vector3.Cross(points[b] - points[a], points[c] - points[a]);
            if (Vector3.Dot(n, points[a] + points[b] + points[c]) < 0f) (b, c) = (c, b);
            s.AddFace(new SoftBodySharedSettings.Face((uint)a, (uint)b, (uint)c));
        }
        for (int k = 0; k < segments; k++) Face(0, Ring(1, k), Ring(1, k + 1));
        for (int r = 1; r < rings - 1; r++)
            for (int k = 0; k < segments; k++)
            {
                Face(Ring(r, k), Ring(r + 1, k), Ring(r + 1, k + 1));
                Face(Ring(r, k), Ring(r + 1, k + 1), Ring(r, k + 1));
            }
        int bottom = points.Count - 1;
        for (int k = 0; k < segments; k++) Face(bottom, Ring(rings - 1, k + 1), Ring(rings - 1, k));
        s.CreateConstraints(1e-5f, SoftBodyBendType.Distance);
        s.Optimize();
        vertices = points.Count;
        return s;
    }

    /// <summary>A square cloth of <paramref name="side"/> by <paramref name="side"/> vertices.</summary>
    private static SoftBodySharedSettings Cloth(int side, float spacing, out int vertices)
    {
        var s = new SoftBodySharedSettings();
        float half = (side - 1) * spacing * 0.5f;
        for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
                s.AddVertex(new SoftBodySharedSettings.Vertex(new Vector3(i * spacing - half, 0f, j * spacing - half), Vector3.Zero, 1f / 0.005f));
        for (int j = 0; j < side - 1; j++)
            for (int i = 0; i < side - 1; i++)
            {
                uint a = (uint)(j * side + i), b = a + 1, c = a + (uint)side, d = c + 1;
                s.AddFace(new SoftBodySharedSettings.Face(a, c, d));
                s.AddFace(new SoftBodySharedSettings.Face(a, d, b));
            }
        s.CreateConstraints(1e-5f, SoftBodyBendType.Distance);
        s.Optimize();
        vertices = side * side;
        return s;
    }
}
