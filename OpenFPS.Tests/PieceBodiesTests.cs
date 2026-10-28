using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Geometry;
using OpenFPS.Materials;
using OpenFPS.Physics;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Arch.Core;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Jolt's copy of the solid world (docs/PHYSICS_BOUNDARY.md 18.2 step 3, 18.7): tiles as static mesh bodies and door
/// leaves as kinematic ones, kept in step with the triangle world by <see cref="PieceBodies"/>. A ball dropped on a
/// map's tiles comes to rest where <c>FloorAt</c> says, within <see cref="Tolerance"/>; a tile dropped takes its ground
/// with it; a leaf moved moves its body. The two maps are loaded once each, as the server loads them.
/// </summary>
public class PieceBodiesTests
{
    /// <summary>How far a resting ball's centre may be from the floor's height plus its radius, metres. Jolt lets a
    /// resting body sink into a surface by up to its penetration slop (2 cm); the measured worst is in 18.7.</summary>
    public const float Tolerance = 0.02f;

    /// <summary>The same on the maps' tiles, where nothing is thicker than a mesh's skin: measured 0.2 mm at worst.</summary>
    public const float MapTolerance = 0.005f;

    private readonly ITestOutputHelper _out;
    public PieceBodiesTests(ITestOutputHelper output) => _out = output;

    private static Surface Plain(string material, Vector3 size, bool moves = false, bool doorLeaf = false)
        => EntityGeometry.SurfaceOf(material, size, 0, 0, false, 0, 0, false, moves, doorLeaf);

    private static SolidSpec Box(int owner, Vector3 at, Vector3 size, string material = "Concrete", bool mover = false)
        => new(owner, at, Quaternion.Identity, size, Plain(material, size, mover, mover));

    private static TriangleWorld WorldOf(out TriangleWorldBuilder builder, IReadOnlyList<SolidSpec> statics, IReadOnlyList<SolidSpec>? movers = null)
    {
        AcousticRegistry.Initialize();
        builder = new TriangleWorldBuilder(250f);
        return builder.Build(statics, movers ?? Array.Empty<SolidSpec>());
    }

    /// <summary>A prepared piece goes into one world once, so each ask makes a new one.</summary>
    private static PreparedPiece? Prepared(Dictionary<ulong, PreparedPiece> unused, GeometryPiece piece) => PieceBodies.Prepare(piece);

    private static void Settle(PieceBodies bodies) { while (bodies.Pump(1000) > 0) { } }

    // ── A floor that is a Jolt mesh is the floor we have ───────────────────────────────────────

    [Fact]
    public void ABallOnABoxFloorStopsAtItsTopAndOnTheStepAtTheStepsTop()
    {
        var world = WorldOf(out _, new[]
        {
            Box(1, new Vector3(125f, -0.1f, 125f), new Vector3(100f, 0.2f, 100f)),
            Box(2, new Vector3(130f, 0.15f, 125f), new Vector3(2f, 0.3f, 2f), "Wood"),
        });
        using var w = new BodyWorld();
        using var bodies = new PieceBodies(w);
        var cache = new Dictionary<ulong, PreparedPiece>();
        Assert.Equal(0, bodies.Follow(world, p => Prepared(cache, p)));
        Settle(bodies);
        int onFloor = w.Add(w.AddSphere(0.2f), BodyMotion.Dynamic, new Vector3(110f, 1f, 125f), Quaternion.Identity, "Metal", 5f);
        int onStep = w.Add(w.AddSphere(0.2f), BodyMotion.Dynamic, new Vector3(130f, 1f, 125f), Quaternion.Identity, "Metal", 5f);
        for (int i = 0; i < 180; i++) w.Step();
        Assert.Equal(0.2f, w.Position(onFloor).Y, Tolerance);
        Assert.Equal(0.3f + 0.2f, w.Position(onStep).Y, Tolerance);
    }

    [Fact]
    public void EachMaterialIsItsOwnBodyWithItsOwnFriction()
    {
        var world = WorldOf(out _, new[]
        {
            Box(1, new Vector3(125f, -0.1f, 125f), new Vector3(40f, 0.2f, 40f), "Concrete"),
            Box(2, new Vector3(125f, -0.1f, 175f), new Vector3(40f, 0.2f, 40f), "Ice"),
        });
        using var w = new BodyWorld();
        using var bodies = new PieceBodies(w);
        var cache = new Dictionary<ulong, PreparedPiece>();
        bodies.Follow(world, p => Prepared(cache, p));
        Settle(bodies);
        var ids = bodies.TileBodies(new TileKey(0, 0));
        Assert.Equal(2, ids.Count);
        var frictions = ids.Select(w.Friction).OrderBy(f => f).ToArray();
        Assert.Equal(ContactMaterial.Of("Ice").Friction, frictions[0], 1e-4f);
        Assert.Equal(ContactMaterial.Of("Concrete").Friction, frictions[1], 1e-4f);
        Assert.NotEqual(frictions[0], frictions[1]);
    }

    [Fact]
    public void ABallRollsAcrossAGroundTilesSeamsWithoutCatching()
    {
        // Every triangle of a flat tile is coplanar. A ball that catches on the diagonals (corners found along different
        // edges, unwelded, so Jolt thinks each edge is a rim) loses speed or jumps.
        const int posts = 126;
        var comp = TerrainTiles.Component(posts, 2f, new float[posts * posts], out float baseY, null, null);
        var spec = EntityGeometry.TerrainSpec(1, new Vector3(125f, baseY, 125f), comp, Plain("Dirt", Vector3.One));
        var world = WorldOf(out _, new[] { spec });
        using var w = new BodyWorld();
        using var bodies = new PieceBodies(w);
        var cache = new Dictionary<ulong, PreparedPiece>();
        bodies.Follow(world, p => Prepared(cache, p));
        Settle(bodies);
        int ball = w.Add(w.AddSphere(0.15f), BodyMotion.Dynamic, new Vector3(100f, 0.151f, 110f), Quaternion.Identity, "Metal", 2f);
        w.SetVelocity(ball, new Vector3(3f, 0f, 1.3f));
        w.SetAngularVelocity(ball, new Vector3(1.3f, 0f, -3f) / 0.15f);   // already rolling, so only the seams can slow it
        float highest = 0f;
        for (int i = 0; i < 240; i++) { w.Step(); highest = MathF.Max(highest, MathF.Abs(w.Position(ball).Y - 0.15f)); }
        _out.WriteLine($"rolled {w.Position(ball) - new Vector3(100f, 0.151f, 110f)}, speed {w.Velocity(ball)}, worst height error {highest * 1000:F2} mm");
        Assert.True(highest < 0.002f, $"the ball left the floor by {highest * 1000:F2} mm");
        Assert.True(w.Velocity(ball).X > 2.9f, $"the ball slowed to {w.Velocity(ball)}");
        Assert.True(w.Velocity(ball).Z > 1.25f, $"the ball veered to {w.Velocity(ball)}");
    }

    // ── Streaming ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATileDroppedTakesItsGroundWithIt()
    {
        var world = WorldOf(out _, new[]
        {
            Box(1, new Vector3(125f, -0.1f, 125f), new Vector3(100f, 0.2f, 100f)),
            Box(2, new Vector3(375f, -0.1f, 125f), new Vector3(100f, 0.2f, 100f)),
        });
        using var w = new BodyWorld();
        using var bodies = new PieceBodies(w);
        var cache = new Dictionary<ulong, PreparedPiece>();
        bodies.Follow(world, p => Prepared(cache, p));
        Settle(bodies);
        Assert.Equal(2, bodies.TileCount);
        int east = w.Add(w.AddSphere(0.2f), BodyMotion.Dynamic, new Vector3(375f, 1f, 125f), Quaternion.Identity, "Metal", 5f);
        int west = w.Add(w.AddSphere(0.2f), BodyMotion.Dynamic, new Vector3(125f, 1f, 125f), Quaternion.Identity, "Metal", 5f);
        int shapeCount = w.ShapeCount;
        for (int i = 0; i < 120; i++) w.Step();
        Assert.Equal(0.2f, w.Position(east).Y, Tolerance);

        // Streaming takes the east tile out: the world without it.
        var without = new TriangleWorld(world.Instances.ToArray().Where(i => i.Piece.Key != new TileKey(1, 0)).ToArray());
        Assert.Equal(0, bodies.Follow(without, p => Prepared(cache, p)));
        Assert.False(bodies.HasTile(new TileKey(1, 0)));
        Assert.Equal(1, bodies.TileCount);
        Assert.Equal(shapeCount - 1, w.ShapeCount);
        w.SetPose(east, new Vector3(375f, 1f, 125f), Quaternion.Identity);
        w.SetVelocity(east, Vector3.Zero);
        for (int i = 0; i < 120; i++) w.Step();
        Assert.True(w.Position(east).Y < -3f, $"the ball stands on a tile that is gone: {w.Position(east)}");
        Assert.Equal(0.2f, w.Position(west).Y, Tolerance);

        // And streaming brings it back: the ground is there again.
        Assert.Equal(0, bodies.Follow(world, p => Prepared(cache, p)));
        Settle(bodies);
        w.SetPose(east, new Vector3(375f, 1f, 125f), Quaternion.Identity);
        w.SetVelocity(east, Vector3.Zero);
        for (int i = 0; i < 120; i++) w.Step();
        Assert.Equal(0.2f, w.Position(east).Y, Tolerance);
    }

    [Fact]
    public void ATileChangedUnderTheSameKeyIsReplaced()
    {
        var one = WorldOf(out var builder, new[] { Box(1, new Vector3(125f, -0.1f, 125f), new Vector3(100f, 0.2f, 100f)) });
        var two = builder.Build(new[] { Box(1, new Vector3(125f, 0.4f, 125f), new Vector3(100f, 0.2f, 100f)) }, Array.Empty<SolidSpec>());
        using var w = new BodyWorld();
        using var bodies = new PieceBodies(w);
        var cache = new Dictionary<ulong, PreparedPiece>();
        bodies.Follow(one, p => Prepared(cache, p));
        Settle(bodies);
        int ball = w.Add(w.AddSphere(0.2f), BodyMotion.Dynamic, new Vector3(125f, 1f, 125f), Quaternion.Identity, "Metal", 5f);
        for (int i = 0; i < 120; i++) w.Step();
        Assert.Equal(0.2f, w.Position(ball).Y, Tolerance);
        bodies.Follow(two, p => Prepared(cache, p));
        Settle(bodies);
        Assert.Equal(1, bodies.TileCount);
        w.SetPose(ball, new Vector3(125f, 1.5f, 125f), Quaternion.Identity);
        for (int i = 0; i < 120; i++) w.Step();
        Assert.Equal(0.5f + 0.2f, w.Position(ball).Y, Tolerance);
    }

    [Fact]
    public void ATileNotPreparedIsAskedForAndTheNextCallTakesIt()
    {
        var world = WorldOf(out _, new[] { Box(1, new Vector3(125f, -0.1f, 125f), new Vector3(100f, 0.2f, 100f)) });
        using var w = new BodyWorld();
        using var bodies = new PieceBodies(w);
        var missing = new List<GeometryPiece>();
        Assert.Equal(1, bodies.Follow(world, _ => null, missing));
        Assert.Single(missing);
        Assert.Equal(0, bodies.TileCount);
        Assert.Equal(0, w.BodyCount);
        // A worker makes it; the tick that comes after takes it in.
        var piece = missing[0];
        PreparedPiece made = null!;
        var worker = new Thread(() => made = PieceBodies.Prepare(piece));
        worker.Start(); worker.Join();
        Assert.Equal(0, bodies.Follow(world, _ => made));
        Assert.Equal(1, bodies.PendingBodies);
        Assert.Equal(1, bodies.Pump(10));
        Assert.True(bodies.TileIsWhole(missing[0].Key));
    }

    [Fact]
    public void PiecesAreMadeOnWorkersAndTheWorldTakesThemOnItsOwnThread()
    {
        var solids = new List<SolidSpec>();
        for (int k = 0; k < 6; k++) solids.Add(Box(k + 1, new Vector3(250f * k + 125f, -0.1f, 125f), new Vector3(100f, 0.2f, 100f)));
        var world = WorldOf(out _, solids);
        var made = new ConcurrentDictionary<TileKey, PreparedPiece>();
        Parallel.ForEach(Enumerable.Range(0, world.InstanceCount), i => made[world.Instance(i).Piece.Key] = PieceBodies.Prepare(world.Instance(i).Piece));
        using var w = new BodyWorld();
        using var bodies = new PieceBodies(w);
        Assert.Equal(0, bodies.Follow(world, p => made[p.Key]));
        Settle(bodies);
        Assert.Equal(6, bodies.TileCount);
        Assert.Equal(6, w.BodyCount);
    }

    [Fact]
    public void BodiesComeInAFewAtATimeAndATileIsWholeWhenTheLastIsIn()
    {
        // One material a body: a tile of six materials is six bodies, added two a tick.
        var solids = new List<SolidSpec>();
        string[] materials = { "Concrete", "Wood", "Brick", "Steel", "Glass", "Dirt" };
        for (int k = 0; k < materials.Length; k++) solids.Add(Box(k + 1, new Vector3(20f * k + 10f, -0.1f, 10f), new Vector3(10f, 0.2f, 10f), materials[k]));
        var world = WorldOf(out _, solids);
        using var w = new BodyWorld();
        using var bodies = new PieceBodies(w);
        var cache = new Dictionary<ulong, PreparedPiece>();
        bodies.Follow(world, p => Prepared(cache, p));
        int ticks = 0;
        while (bodies.PendingBodies > 0)
        {
            Assert.False(bodies.TileIsWhole(new TileKey(0, 0)));
            Assert.True(bodies.Pump(2) <= 2);
            ticks++;
            w.Tick();
        }
        Assert.Equal(3, ticks);
        Assert.True(bodies.TileIsWhole(new TileKey(0, 0)));
        Assert.Equal(6, w.BodyCount);
    }

    // ── Door leaves ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALeafMovedMovesItsBodyAndCarriesWhatStandsOnIt()
    {
        // A leaf laid flat as a platform: kinematic, so it pushes what is on it and nothing pushes it.
        var floor = Box(1, new Vector3(125f, -0.1f, 125f), new Vector3(100f, 0.2f, 100f));
        var leaf = Box(77, new Vector3(110f, 0.5f, 125f), new Vector3(2f, 0.1f, 2f), "Wood", mover: true);
        var world = WorldOf(out var builder, new[] { floor }, new[] { leaf });
        using var w = new BodyWorld();
        using var bodies = new PieceBodies(w);
        var cache = new Dictionary<ulong, PreparedPiece>();
        Assert.Equal(0, bodies.Follow(world, p => Prepared(cache, p)));
        Settle(bodies);
        Assert.True(bodies.HasLeaf(77));
        int body = bodies.LeafBodies(77).Single();
        Assert.Equal(new Vector3(110f, 0.5f, 125f), w.Position(body));
        Assert.Equal(BodyMotion.Kinematic, BodyMotionOf(w, body));

        int ball = w.Add(w.AddSphere(0.2f), BodyMotion.Dynamic, new Vector3(110f, 0.9f, 125f), Quaternion.Identity, "Metal", 5f);
        for (int i = 0; i < 120; i++) w.Step();
        Assert.Equal(0.55f + 0.2f, w.Position(ball).Y, Tolerance);

        // The host moves the leaf up 1 cm a tick for a second; each tick's world is handed over.
        for (int tick = 1; tick <= 30; tick++)
        {
            world = builder.Move(new[] { (77, new Vector3(110f, 0.5f + 0.01f * tick, 125f), Quaternion.Identity) });
            bodies.Follow(world, p => Prepared(cache, p));
            w.Tick();
        }
        Assert.Equal(0.8f, w.Position(body).Y, 1e-3f);
        Assert.Equal(0.3f + 0.55f + 0.2f, w.Position(ball).Y, Tolerance);

        // Once the leaf is told to stay, it does: a kinematic body keeps the speed it was last given.
        for (int tick = 0; tick < 30; tick++) { bodies.Follow(world, p => Prepared(cache, p)); w.Tick(); }
        Assert.Equal(0.8f, w.Position(body).Y, 1e-3f);
        Assert.Equal(Vector3.Zero, w.Velocity(body));
    }

    [Fact]
    public void ALeafThatIsGoneTakesItsBodyAndTheShapeStaysForTheNext()
    {
        var floor = Box(1, new Vector3(125f, -0.1f, 125f), new Vector3(100f, 0.2f, 100f));
        var a = Box(77, new Vector3(110f, 0.5f, 125f), new Vector3(2f, 0.1f, 2f), "Wood", mover: true);
        var b = Box(78, new Vector3(120f, 0.5f, 125f), new Vector3(2f, 0.1f, 2f), "Wood", mover: true);
        var world = WorldOf(out var builder, new[] { floor }, new[] { a, b });
        using var w = new BodyWorld();
        using var bodies = new PieceBodies(w);
        var cache = new Dictionary<ulong, PreparedPiece>();
        bodies.Follow(world, p => Prepared(cache, p));
        Settle(bodies);
        Assert.Equal(2, bodies.LeafCount);
        int shapes = w.ShapeCount;
        // Leaves of one build share one shape.
        Assert.Equal(2, shapes);   // the floor's and the leaf build's
        var onlyB = builder.Rebuild(new Dictionary<TileKey, List<SolidSpec>>(), Array.Empty<TileKey>(), new[] { b });
        bodies.Follow(onlyB, p => Prepared(cache, p));
        Assert.Equal(1, bodies.LeafCount);
        Assert.False(bodies.HasLeaf(77));
        Assert.Equal(shapes, w.ShapeCount);
        Assert.Equal(2, w.BodyCount);
    }

    private static BodyMotion BodyMotionOf(BodyWorld w, int body)
        => w.Mass(body) == 0f && w.State(body).Sleeping == false ? BodyMotion.Kinematic : BodyMotion.Static;

    // ── The maps ───────────────────────────────────────────────────────────────────────────────

    /// <summary>One map held in a Jolt world: loaded as the server loads it, every piece prepared on workers, then added.</summary>
    private sealed record LoadedMap(MapManager Maps, World Ecs, ServerGeometry Geometry);

    private sealed class Held : IDisposable
    {
        public required LoadedMap Map;
        public required TriangleWorld Geometry;
        public required BodyWorld World;
        public required PieceBodies Bodies;
        public double LoadMs, PrepareMs, AddMs, WorstPumpMs, FirstTickMs, SecondTickMs;
        public int Pieces, Triangles, Vertices, PumpCalls, Leaves;
        public double WorstPrepareMs;
        public void Dispose() { Bodies.Dispose(); World.Dispose(); }
    }

        private static (MapManager Maps, World Ecs, ServerGeometry Geometry) Load(string mapId)
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-piecebodies-" + Guid.NewGuid().ToString("N"));
        try
        {
            string place = Path.Combine(AppContext.BaseDirectory, "places", mapId + ".json");
            bool isPlace = File.Exists(place);
            if (isPlace)
            {
                Directory.CreateDirectory(Path.Combine(dir, "places"));
                File.Copy(place, Path.Combine(dir, "places", mapId + ".json"));
                RealPlaceMapTests.CopyGround(mapId, Path.Combine(dir, "places"));
            }
            else
            {
                Directory.CreateDirectory(dir);
                File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", mapId + ".json"), Path.Combine(dir, mapId + ".json"));
            }
            AcousticRegistry.Initialize();
            var maps = new MapManager(new MapRepository(dir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
            maps.Initialize();
            Assert.True(maps.TryGetMap(mapId, out var ecs, out _, out _, out _));
            Assert.True(maps.TryGetGeometry(mapId, out var geometry));
            return (maps, ecs, geometry);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private Held Hold(string mapId)
    {
        var clock = Stopwatch.StartNew();
        var (maps, ecs, serverGeo) = Load(mapId);
        var map = new LoadedMap(maps, ecs, serverGeo);
        double load = clock.Elapsed.TotalMilliseconds;
        var geo = serverGeo.World;
        clock.Restart();
        var made = new ConcurrentDictionary<ulong, PreparedPiece>();
        Parallel.ForEach(Enumerable.Range(0, geo.InstanceCount).Where(i => !geo.Instance(i).Piece.IsEmpty), i =>
        {
            var piece = geo.Instance(i).Piece;
            made.GetOrAdd(piece.Signature, _ => PieceBodies.Prepare(piece));
        });
        double prepare = clock.Elapsed.TotalMilliseconds;
        var w = new BodyWorld(maxBodies: 1 << 16);
        var bodies = new PieceBodies(w);
        Assert.Equal(0, bodies.Follow(geo, p => made[p.Signature]));
        clock.Restart();
        double worst = 0; int calls = 0;
        while (bodies.PendingBodies > 0)
        {
            var one = Stopwatch.StartNew();
            bodies.Pump(4);
            worst = Math.Max(worst, one.Elapsed.TotalMilliseconds);
            calls++;
        }
        double add = clock.Elapsed.TotalMilliseconds;
        // The first step after the tiles are in is where an unoptimised broad phase would show itself (18.4).
        int probe = w.Add(w.AddSphere(0.2f), BodyMotion.Dynamic, new Vector3(0f, 500f, 0f), Quaternion.Identity, "Metal", 1f);
        var step = Stopwatch.StartNew(); w.Tick(); double first = step.Elapsed.TotalMilliseconds;
        step.Restart(); w.Tick(); double second = step.Elapsed.TotalMilliseconds;
        w.Remove(probe);
        return new Held
        {
            Map = map, Geometry = geo, World = w, Bodies = bodies, LoadMs = load, PrepareMs = prepare, AddMs = add, WorstPumpMs = worst, FirstTickMs = first, SecondTickMs = second,
            Pieces = made.Count, Triangles = made.Values.Sum(p => p.Triangles), Vertices = made.Values.Sum(p => p.Vertices), PumpCalls = calls,
            WorstPrepareMs = made.Values.Max(p => p.BuildMs), Leaves = bodies.LeafCount,
        };
    }

    /// <summary>
    /// Balls dropped from 0.6 m on level-enough ground (five floor heights, the centre's and four 0.6 m about it, on one
    /// plane to 1 cm), all at once with 1.2 m between. Each comes to rest where <c>FloorAt</c> at its place says, a
    /// radius along the floor's normal. Returns how many were dropped, the worst miss, and the worst ball's place.
    /// </summary>
    private (int Dropped, float Worst, Vector3 At, int Rolling) Drop(Held h, Func<float, float, bool> inside, int want, int seed, float radius = 0.2f)
    {
        var any = new AcceptAll();
        var rng = new Random(seed);
        var geo = h.Geometry;
        var taken = new List<Vector2>();
        var balls = new List<(int Id, Vector3 Start)>();
        var shape = h.World.AddSphere(radius);
        float lo = float.MaxValue, hiX = float.MinValue, loZ = float.MaxValue, hiZ = float.MinValue;
        for (int i = 0; i < geo.InstanceCount; i++)
        {
            var inst = geo.Instance(i);
            if (inst.Owner >= 0 || inst.Piece.IsEmpty) continue;
            lo = MathF.Min(lo, inst.Min.X); hiX = MathF.Max(hiX, inst.Max.X); loZ = MathF.Min(loZ, inst.Min.Z); hiZ = MathF.Max(hiZ, inst.Max.Z);
        }
        for (int tries = 0; tries < want * 400 && balls.Count < want; tries++)
        {
            float x = lo + (float)rng.NextDouble() * (hiX - lo), z = loZ + (float)rng.NextDouble() * (hiZ - loZ);
            if (!inside(x, z)) continue;
            float y = geo.FloorAt(x, z, 2000f, GeometryLayers.Ground, ref any, out var hit);
            if (y <= -1000f || hit.Normal.Y < 0.9f) continue;
            // Level enough: the four floors about it lie on the centre's plane.
            bool flat = true;
            foreach (var (dx, dz) in new[] { (0.6f, 0f), (-0.6f, 0f), (0f, 0.6f), (0f, -0.6f) })
            {
                float yn = geo.FloorAt(x + dx, z + dz, y + 0.5f, GeometryLayers.Ground, ref any, out _);
                float plane = y - (hit.Normal.X * dx + hit.Normal.Z * dz) / hit.Normal.Y;
                if (MathF.Abs(yn - plane) > 0.01f) { flat = false; break; }
            }
            if (!flat) continue;
            var p = new Vector2(x, z);
            if (taken.Any(q => Vector2.DistanceSquared(p, q) < 1.44f)) continue;
            taken.Add(p);
            var start = new Vector3(x, y + 0.6f, z);
            balls.Add((h.World.Add(shape, BodyMotion.Dynamic, start, Quaternion.Identity, "Metal", 4f), start));
        }
        for (int i = 0; i < 180; i++) h.World.Step();
        float worst = 0f; var worstAt = Vector3.Zero; int shown = 0, rolling = 0;
        foreach (var (id, start) in balls)
        {
            var at = h.World.Position(id);
            float y = geo.FloorAt(at.X, at.Z, at.Y + 0.5f, GeometryLayers.Ground, ref any, out var hit);
            float expected = y + radius / hit.Normal.Y;
            float miss = MathF.Abs(at.Y - expected);
            // A ball that rolled off a ledge or down a bank is still falling or rolling and has left the floor it started on: not a miss of the floor.
            if (miss > Tolerance && h.World.Velocity(id).Length() > 0.05f) { rolling++; continue; }
            if (miss > worst) { worst = miss; worstAt = start; }
            if (miss > Tolerance && ++shown <= 12) _out.WriteLine($"miss {miss * 1000:F0} mm: dropped at {start} (floor {start.Y - 0.6f}), ended {at} (floor {y}, normal {hit.Normal.Y:F3}), speed {h.World.Velocity(id).Length():F2}");
        }
        foreach (var (id, _) in balls) h.World.Remove(id);
        h.World.RemoveShape(shape);
        return (balls.Count, worst, worstAt, rolling);
    }

    private void Report(string mapId, Held h, (int Dropped, float Worst, Vector3 At, int Rolling) r)
        => _out.WriteLine($"{mapId}: map loaded {h.LoadMs:F0} ms; {h.Pieces} pieces ({h.Triangles:N0} triangles, {h.Vertices:N0} welded vertices) "
                          + $"made on workers in {h.PrepareMs:F0} ms (slowest piece {h.WorstPrepareMs:F0} ms); added {h.World.BodyCount} bodies in {h.PumpCalls} calls "
                          + $"of 4 in {h.AddMs:F0} ms (worst call {h.WorstPumpMs:F1} ms); first tick after {h.FirstTickMs:F1} ms, second {h.SecondTickMs:F1} ms; {h.Leaves} leaves; {r.Dropped} balls dropped ({r.Rolling} rolled off a ledge or bank, left out), worst miss "
                          + $"{r.Worst * 1000:F1} mm at {r.At.X:F1}, {r.At.Z:F1}, {r.At.Y:F2}");

    [Fact]
    public void BallsDroppedOnTheCityComeToRestOnItsFloor()
    {
        using var h = Hold("city");
        var r = Drop(h, (_, _) => true, 600, 1);
        Report("city", h, r);
        Assert.True(r.Dropped >= 400, $"only {r.Dropped} places were level enough to test");
        Assert.True(r.Rolling <= r.Dropped / 50, $"{r.Rolling} of {r.Dropped} balls rolled off");
        Assert.True(r.Worst <= MapTolerance, $"a ball came to rest {r.Worst * 1000:F1} mm off the floor, dropped at {r.At}");
    }

    [Fact]
    public void BallsDroppedOnMagnoliaComeToRestOnItsGround()
    {
        using var h = Hold("magnolia_tx");
        var r = Drop(h, (_, _) => true, 600, 2);
        Report("magnolia_tx", h, r);
        Assert.True(r.Dropped >= 400, $"only {r.Dropped} places were level enough to test");
        Assert.True(r.Rolling <= r.Dropped / 50, $"{r.Rolling} of {r.Dropped} balls rolled off");
        Assert.True(r.Worst <= MapTolerance, $"a ball came to rest {r.Worst * 1000:F1} mm off the floor, dropped at {r.At}");
    }

    [Fact]
    public void TheCitysDoorLeavesAreKinematicBodiesAtTheirPoses()
    {
        using var h = Hold("city");
        Assert.True(h.Bodies.LeafCount > 20, $"{h.Bodies.LeafCount} leaves");
        int checkedLeaves = 0;
        for (int i = 0; i < h.Geometry.InstanceCount; i++)
        {
            var inst = h.Geometry.Instance(i);
            if (inst.Owner < 0) continue;
            foreach (int body in h.Bodies.LeafBodies(inst.Owner))
            {
                Assert.Equal(inst.Position.X, h.World.Position(body).X, 1e-4f);
                Assert.Equal(inst.Position.Y, h.World.Position(body).Y, 1e-4f);
                Assert.Equal(inst.Position.Z, h.World.Position(body).Z, 1e-4f);
                Assert.True(MathF.Abs(Quaternion.Dot(h.World.Orientation(body), inst.Rotation)) > 0.99999f);
                checkedLeaves++;
            }
        }
        Assert.True(checkedLeaves > 20);

        // Every door's leaf stands where DoorSystem.LeafAt puts it at the door's own openness.
        int doors = 0;
        h.Map.Ecs.Query(new QueryDescription().WithAll<DoorComponent>(), (Entity door, ref DoorComponent leaf) =>
        {
            var bodies = h.Bodies.LeafBodies(door.Id);
            if (bodies.Count == 0) return;
            var (centre, rotation) = DoorSystem.LeafAt(h.Map.Ecs, door, leaf, leaf.Openness);
            Assert.True(Vector3.Distance(centre, h.World.Position(bodies[0])) < 2e-3f, $"door #{door.Id}: LeafAt {centre}, body {h.World.Position(bodies[0])}");
            Assert.True(MathF.Abs(Quaternion.Dot(rotation, h.World.Orientation(bodies[0]))) > 0.9999f);
            doors++;
        });
        _out.WriteLine($"{doors} doors checked against LeafAt");
        Assert.True(doors > 20);
    }
}
