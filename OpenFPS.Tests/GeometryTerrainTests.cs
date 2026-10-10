using System.Numerics;
using Arch.Core;
using MemoryPack;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;

namespace OpenFPS.Tests;

/// <summary>
/// Terrain (docs/GEOMETRY.md 2.3, geometry stage 3, step T1 of docs/WORLD_STREAMING.md "Stage 2 with
/// terrain"): a tile of ground as a heightfield in the triangle world, met by the ground probe, rays, a
/// walking body and the acoustic layer, on made-up ground.
/// </summary>
public class GeometryTerrainTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _o;
    public GeometryTerrainTests(Xunit.Abstractions.ITestOutputHelper o) => _o = o;

    private const int Posts = 126;
    private const float Spacing = 2f;

    private static readonly Surface Ground = EntityGeometry.SurfaceOf("Dirt", Vector3.One, 0, 0, false, 0, 0, false, false, false, "Ground");

    private static float[] Heights(Func<float, float, float> h, float cornerX = 0f, float cornerZ = 0f)
    {
        var a = new float[Posts * Posts];
        for (int j = 0; j < Posts; j++)
            for (int i = 0; i < Posts; i++)
                a[j * Posts + i] = h(cornerX + i * Spacing, cornerZ + j * Spacing);
        return a;
    }

    /// <summary>A tile of ground at (cornerX, cornerZ), its heights given by <paramref name="h"/> in world x
    /// and z, as the wire carries it (centimetres over a base), so its floats are what every machine has.</summary>
    private static SolidSpec Tile(int owner, float cornerX, float cornerZ, Func<float, float, float> h, byte[]? cells = null, string[]? materials = null)
    {
        var c = TerrainTiles.Component(Posts, Spacing, Heights(h, cornerX, cornerZ), out float baseY, cells, materials);
        var centre = new Vector3(cornerX + c.Size / 2f, baseY, cornerZ + c.Size / 2f);
        return EntityGeometry.TerrainSpec(owner, centre, c, Ground);
    }

    private static TriangleWorld WorldOf(params SolidSpec[] statics)
        => new TriangleWorldBuilder(250f).Build(statics, Array.Empty<SolidSpec>());

    private static Surface Concrete(Vector3 size) => EntityGeometry.SurfaceOf("Concrete", size, 0, 0, false, 0, 0, false, false, false, null);

    [Fact]
    public void HeightsAreThePostsAndPlanesBetweenThem()
    {
        var f = Heightfield.FromCentimetres(3, 2f, 10f, new short[] { 0, 100, 200, 50, 150, 250, 100, 200, 300 }, null, null);
        Assert.Equal(10f, f.HeightAt(0f, 0f), 5);
        Assert.Equal(11f, f.HeightAt(2f, 0f), 5);
        Assert.Equal(13f, f.HeightAt(4f, 4f), 5);
        // On the plane of the triangle under the point: (1, 1) is on the diagonal of cell (0, 0), halfway.
        Assert.Equal(10f + 0.5f * (11.5f - 10f), f.HeightAt(1f, 1f), 4);
        Assert.Equal(10f + 0.5f + 0.125f, f.HeightAt(1f, 0.5f), 4);   // the field is the plane 10 + x/2 + z/4
        Assert.True(float.IsNaN(f.HeightAt(-0.1f, 1f)));
        Assert.Equal(2 * 2 * 2, f.TriangleCount);
    }

    [Fact]
    public void CentimetresComeBackAsTheSameFloats()
    {
        var heights = Heights((x, z) => 61.37f + 0.013f * x - 0.021f * z + 0.4f * MathF.Sin(x / 17f));
        var c = TerrainTiles.Component(Posts, Spacing, heights, out float baseY);
        var a = c.Field(baseY);
        var bytes = MemoryPackSerializer.Serialize(c);
        var back = MemoryPackSerializer.Deserialize<TerrainTileComponent>(bytes)!;
        var b = back.Field(baseY);
        Assert.Equal(a.Hash, b.Hash);
        for (int k = 0; k < heights.Length; k++) Assert.True(MathF.Abs(a.Heights[k] - heights[k]) <= 0.0051f, $"post {k}: {a.Heights[k]} for {heights[k]}");
    }

    /// <summary>The ground probe stands on the terrain: the highest of its five points, exactly on the plane
    /// under each, with the slope's normal.</summary>
    [Fact]
    public void TheGroundProbeStandsOnTheTerrain()
    {
        Func<float, float, float> h = (x, z) => 5f + 0.10f * x + 0.05f * z;
        var spec = Tile(1, 0f, 0f, h);
        var world = WorldOf(spec);
        var field = spec.Terrain!;
        var all = new AcceptAll();
        foreach (var (x, z) in new[] { (10.3f, 20.7f), (125f, 125f), (3.1f, 240.2f), (200.9f, 7.77f) })
        {
            float r = PhysicsConstants.PlayerRadius;
            float expected = new[] { field.HeightAt(x, z), field.HeightAt(x + r, z), field.HeightAt(x - r, z), field.HeightAt(x, z + r), field.HeightAt(x, z - r) }.Max();
            var feet = new Vector3(x, h(x, z) + 0.2f, z);
            float y = world.Ground(feet, r, PhysicsConstants.StepHeight, GeometryLayers.Ground, ref all, out var solid, out int owner, out var normal);
            Assert.Equal(expected, y, 4);
            Assert.Equal(1, owner);
            Assert.True(world.IsTerrain(solid));
            var n = Vector3.Normalize(new Vector3(-0.10f, 1f, -0.05f));
            Assert.True(Vector3.Dot(normal, n) > 0.9999f, $"normal {normal}");
        }
    }

    /// <summary>A foot comes down on the material of the cell under it.</summary>
    [Fact]
    public void TheCellsMaterialIsUnderfoot()
    {
        var cells = new byte[(Posts - 1) * (Posts - 1)];
        for (int j = 0; j < Posts - 1; j++)
            for (int i = 60; i < Posts - 1; i++) cells[j * (Posts - 1) + i] = 1;    // grass east of x = 120
        var world = WorldOf(Tile(1, 0f, 0f, (x, z) => 0f, cells, new[] { "Dirt", "Grass" }));
        var all = new AcceptAll();
        world.Ground(new Vector3(50f, 0.1f, 50f), 0f, 0.4f, GeometryLayers.Ground, ref all, out var dirt, out _);
        world.Ground(new Vector3(180f, 0.1f, 50f), 0f, 0.4f, GeometryLayers.Ground, ref all, out var grass, out _);
        Assert.Equal("Dirt", world.SurfaceOf(dirt).Material);
        Assert.Equal("Grass", world.SurfaceOf(grass).Material);
    }

    private sealed record Walked(List<Vector3> Path, List<bool> Grounded, Vector3 End);

    /// <summary>A body walking as the server walks one: the ground under it, the grade, the body, the solids
    /// within reach.</summary>
    private static Walked Walk(TriangleWorld world, Vector3 from, Vector3 direction, int ticks)
    {
        var path = new List<Vector3>();
        var groundedAt = new List<bool>();
        Vector3 pos = from, vel = Vector3.Zero;
        var all = new AcceptAll();
        var solids = new List<SolidRef>();
        for (int t = 0; t < ticks; t++)
        {
            float ground = world.Ground(pos, PhysicsConstants.PlayerRadius, PhysicsConstants.StepHeight, GeometryLayers.Ground, ref all, out _, out _);
            var ctx = new SharedMovementEngine.MovementContext
            {
                Position = pos, Velocity = vel, InputDirection = direction, DeltaTime = PhysicsConstants.FixedDeltaTime, GroundHeight = ground,
                Gravity = PhysicsConstants.Gravity, JumpForce = PhysicsConstants.JumpPower, Speed = PhysicsConstants.FootSpeed(false, float.MaxValue),
                PlayerRadius = PhysicsConstants.PlayerRadius, PlayerHeight = PhysicsConstants.PlayerHeight, StepHeight = PhysicsConstants.StepHeight,
                MapMin = new Vector3(-1000, -1000, -1000), MapMax = new Vector3(1000, 1000, 1000), Body = BodyShape.Capsule,
            };
            ctx.Grade = SharedMovementEngine.GradeAlong(world, ref all, pos, direction);
            SharedMovementEngine.GatherSolids(ctx, world, ref all, solids);
            var obstacles = new SharedMovementEngine.Obstacles(ReadOnlySpan<SharedMovementEngine.Collider>.Empty, world,
                                                               System.Runtime.InteropServices.CollectionsMarshal.AsSpan(solids));
            (pos, vel, bool grounded) = SharedMovementEngine.Step(ctx, obstacles, out _);
            path.Add(pos);
            groundedAt.Add(grounded);
        }
        return new Walked(path, groundedAt, pos);
    }

    /// <summary>Up a slope of 15 % and back down it: on the ground every tick, the feet on the terrain, slower
    /// going up than coming down, and no landing anywhere (a kerb that landed you was a bang every step).</summary>
    [Fact]
    public void AWalkUpAndDownASlopeStaysOnTheGround()
    {
        Func<float, float, float> h = (x, z) => 0.15f * z;
        var spec = Tile(1, 0f, 0f, h);
        var world = WorldOf(spec);
        var field = spec.Terrain!;
        var start = new Vector3(100f, h(100f, 20f), 20f);
        var up = Walk(world, start, Vector3.UnitZ, 90);
        var down = Walk(world, up.End, -Vector3.UnitZ, 90);
        foreach (var w in new[] { up, down })
            for (int t = 1; t < w.Path.Count; t++)
            {
                var p = w.Path[t];
                Assert.True(w.Grounded[t], $"in the air at tick {t}, {p}");
                // The feet stand on the highest of the probe's points: on a plane of 15 %, a body's radius up the
                // slope. Going down they trail it by a tick's drop, as on a ramp: the ground is read before the step.
                float under = field.HeightAt(p.X, p.Z + PhysicsConstants.PlayerRadius);
                Assert.True(MathF.Abs(p.Y - under) < 0.05f, $"tick {t}: feet at {p.Y}, ground {under}");
            }
        float upward = up.End.Z - start.Z, downward = up.End.Z - down.End.Z;
        Assert.True(upward > 8f, $"went {upward} m up");
        Assert.True(downward > upward, $"up {upward} m, down {downward} m in the same time");
    }

    /// <summary>A bank too steep to walk (60 degrees) is a wall: walked at, it stops the body, which does not
    /// climb it and does not fall through the ground.</summary>
    [Fact]
    public void ABankTooSteepToWalkIsAWall()
    {
        // Level at 0 up to z = 100, then rising 1.7 m every metre for 6 m, then level at the top.
        Func<float, float, float> h = (x, z) => z <= 100f ? 0f : z >= 106f ? 10.2f : 1.7f * (z - 100f);
        var world = WorldOf(Tile(1, 0f, 0f, h));
        var walked = Walk(world, new Vector3(50f, 0f, 90f), Vector3.UnitZ, 120);
        Assert.True(walked.End.Z < 101f, $"climbed to {walked.End}");
        Assert.True(walked.End.Z > 98f, $"stopped short at {walked.End}");
        foreach (var p in walked.Path) Assert.True(p.Y > -0.05f && p.Y < 1.5f, $"at {p}");
    }

    /// <summary>Across the edge between two tiles of ground on one slope: on the ground throughout, and the
    /// feet move smoothly from one tile to the next.</summary>
    [Fact]
    public void WalkingOverATileEdgeIsSeamless()
    {
        Func<float, float, float> h = (x, z) => 3f + 0.08f * x + 1.5f * MathF.Sin(z / 40f);
        var world = WorldOf(Tile(1, 0f, 0f, h), Tile(2, 250f, 0f, h));
        var start = new Vector3(240f, h(240f, 120f) + 0.3f, 120f);
        var w = Walk(world, start, Vector3.UnitX, 150);
        Assert.True(w.End.X > 255f, $"ended at {w.End}");
        for (int t = 2; t < w.Path.Count; t++)
        {
            Assert.True(w.Grounded[t], $"in the air at tick {t}, {w.Path[t]}");
            Assert.True(MathF.Abs(w.Path[t].Y - w.Path[t - 1].Y) < 0.05f, $"a step of {w.Path[t].Y - w.Path[t - 1].Y} m at {w.Path[t]}");
        }
        var all = new AcceptAll();
        world.Ground(new Vector3(251f, 50f, 120f), 0f, 0.4f, GeometryLayers.Ground, ref all, out _, out int owner);
        Assert.Equal(2, owner);
    }

    /// <summary>A hill between two points is in the way of sound and sight: a ray into it meets its face, and
    /// the crossings through it go in and come out.</summary>
    [Fact]
    public void AHillStandsInTheWay()
    {
        Func<float, float, float> h = (x, z) => 8f * MathF.Exp(-((x - 125f) * (x - 125f) + (z - 125f) * (z - 125f)) / (2f * 20f * 20f));
        var world = WorldOf(Tile(1, 0f, 0f, h));
        var all = new AcceptAll();
        var from = new Vector3(60f, 1.6f, 125f);
        var to = new Vector3(190f, 1.6f, 125f);
        var d = Vector3.Normalize(to - from);
        float len = Vector3.Distance(from, to);
        Assert.True(world.Any(from, d, len, GeometryLayers.Acoustics, RayFaces.Both, ref all));
        Assert.True(world.Closest(from, d, len, GeometryLayers.Acoustics, RayFaces.Front, ref all, out var hit));
        var at = from + d * hit.T;
        Assert.True(at.X > 60f && at.X < 125f, $"met at {at}");
        Assert.True(hit.Normal.X < 0f, $"faces {hit.Normal}");
        var crossings = new List<GeometryCrossing>();
        world.All(from, d, len, GeometryLayers.Acoustics, ref all, crossings);
        Assert.True(crossings.Count >= 2);
        Assert.True(crossings[0].Front);
        Assert.False(crossings[^1].Front);
        // Over the top, nothing is in the way.
        var high = new Vector3(60f, 12f, 125f);
        Assert.False(world.Any(high, Vector3.UnitX, 130f, GeometryLayers.Acoustics, RayFaces.Both, ref all));
    }

    /// <summary>Under the ground is inside it; over it is not.</summary>
    [Fact]
    public void UnderTheGroundIsInsideIt()
    {
        var world = WorldOf(Tile(1, 0f, 0f, (x, z) => 2f + 0.01f * x));
        var all = new AcceptAll();
        var inside = new List<SolidRef>();
        world.Containing(new Vector3(100f, 2.5f, 100f), GeometryLayers.Physical, ref all, inside);
        Assert.Single(inside);
        Assert.Equal(1, world.OwnerOf(inside[0]));
        inside.Clear();
        world.Containing(new Vector3(100f, 3.5f, 100f), GeometryLayers.Physical, ref all, inside);
        Assert.Empty(inside);
    }

    /// <summary>A kerb on sloping ground: a 12 cm slab laid on the terrain is stepped onto and off, on the
    /// ground all the way, its concrete underfoot while on it.</summary>
    [Fact]
    public void AKerbOnSlopingGroundIsSteppedOnAndOff()
    {
        Func<float, float, float> h = (x, z) => 0.04f * x;
        var size = new Vector3(4f, 0.12f, 30f);
        var kerb = new SolidSpec(5, new Vector3(110f, h(110f, 0f) + 0.06f, 100f), Quaternion.Identity, size, Concrete(size));
        var world = WorldOf(Tile(1, 0f, 0f, h), kerb);
        var w = Walk(world, new Vector3(100f, h(100f, 0f), 100f), Vector3.UnitX, 120);
        Assert.True(w.End.X > 115f, $"ended at {w.End}");
        for (int t = 1; t < w.Path.Count; t++) Assert.True(w.Grounded[t], $"in the air at tick {t}, {w.Path[t]}");
        var all = new AcceptAll();
        world.Ground(new Vector3(110f, h(110f, 0f) + 0.2f, 100f), 0f, 0.4f, GeometryLayers.Ground, ref all, out var on, out _);
        Assert.Equal("Concrete", world.SurfaceOf(on).Material);
    }

    /// <summary>
    /// The acoustic scene on raised ground: the ground is open ground, a road lying on it 8 m up is open
    /// ground too (it was "a thin slab under a metre", which on a hill is nothing), and a roof over the
    /// ground is not. The listener's trace leaves out the first two and keeps the roof.
    /// </summary>
    [Fact]
    public void OnRaisedGroundWhatLiesOnItIsOpenGround()
    {
        var c = TerrainTiles.Component(Posts, Spacing, Heights((x, z) => 8f), out float baseY);
        var terrain = SolidSpec.OfTerrain(1, new Vector3(125f, baseY, 125f), c.Field(baseY), Client.Core.AudioEngine.SteamAudio.SceneTerrain.Surface);
        var road = new Client.Core.AudioEngine.SteamAudio.SteamAudioScene.Box(new Vector3(100f, 8.05f, 100f), new Vector3(6f, 0.1f, 20f), Quaternion.Identity, "Asphalt", default, 10);
        var roof = new Client.Core.AudioEngine.SteamAudio.SteamAudioScene.Box(new Vector3(160f, 13f, 100f), new Vector3(10f, 0.2f, 10f), Quaternion.Identity, "Concrete", default, 11);
        var boxes = new List<Client.Core.AudioEngine.SteamAudio.SteamAudioScene.Box> { road, roof };
        var world = new Client.Core.AudioEngine.SteamAudio.AcousticGeometry(250f).Update(boxes, null, new[] { terrain });
        var all = new AcceptAll();
        Surface Down(float x, float z)
        {
            Assert.True(world.Closest(new Vector3(x, 30f, z), -Vector3.UnitY, 50f, GeometryLayers.Acoustics, RayFaces.Front, ref all, out var hit));
            return world.SurfaceOf(hit);
        }
        Assert.True(Down(100f, 100f).Is(SurfaceFlags.OpenGround), "the road");
        Assert.False(Down(160f, 100f).Is(SurfaceFlags.OpenGround), "the roof");
        var bare = Down(40f, 40f);
        Assert.True(bare.Is(SurfaceFlags.OpenGround), "the ground");
        Assert.Equal("Dirt", bare.Material);

        var ground = new Client.Core.AudioEngine.SteamAudio.GroundHeights(new[] { terrain });
        var kept = Client.Core.AudioEngine.SteamAudio.SteamAudioScene.WithoutOpenGround(boxes, ground);
        Assert.Equal(new[] { 11 }, kept.Select(b => b.EntityId).ToArray());
    }

    /// <summary>The server's tile of ground and a client's, sent it in a definition, are the same bits: the
    /// same piece, the same heights under every probe.</summary>
    [Fact]
    public void TheServerAndAClientMakeTheSameGround()
    {
        var ecs = World.Create();
        try
        {
            var c = TerrainTiles.Component(Posts, Spacing, Heights((x, z) => 12.34f + 0.031f * x + 0.7f * MathF.Cos(z / 9f), 500f, -250f), out float baseY,
                                           null, new[] { "Dirt" });
            var e = TerrainTiles.Spawn(ecs, 500f, -250f, baseY, c);
            var statics = new List<SolidSpec>(); var movers = new List<SolidSpec>(); var unindexed = new List<Entity>();
            ServerGeometry.Collect(ecs, statics, movers, unindexed, null);
            var server = new TriangleWorldBuilder(250f).Build(statics, movers);

            var def = EntityDefinitionFactory.From(ecs, e);
            var sent = (EntityDefinition)MemoryPackSerializer.Deserialize<IMessage>(MemoryPackSerializer.Serialize<IMessage>(def))!;
            Assert.Equal(GeometryRole.Static, EntityGeometry.RoleOf(sent));
            var client = new TriangleWorldBuilder(250f).Build(new[] { EntityGeometry.SpecOf(sent, sent.Transform, GeometryRole.Static) }, Array.Empty<SolidSpec>());

            Assert.Equal(1, server.InstanceCount);
            Assert.Equal(server.Instance(0).Piece.Signature, client.Instance(0).Piece.Signature);
            var all = new AcceptAll();
            var rng = new Random(3);
            for (int k = 0; k < 500; k++)
            {
                var p = new Vector3(500f + (float)rng.NextDouble() * 250f, 60f, -250f + (float)rng.NextDouble() * 250f);
                float a = server.Ground(p, 0.3f, 0.4f, GeometryLayers.Ground, ref all, out _, out _);
                float b = client.Ground(p, 0.3f, 0.4f, GeometryLayers.Ground, ref all, out _, out _);
                Assert.Equal(BitConverter.SingleToInt32Bits(a), BitConverter.SingleToInt32Bits(b));
                Assert.True(a > 0f);
            }
        }
        finally { World.Destroy(ecs); }
    }

    // ═══ Where coarse ground meets full ground ═════════════════════════════════════════════════════

    /// <summary>The far ring's ground as it was made before the skirt (2026-10-09): every post read off the
    /// fine triangles, edges included, and no skirt. Kept to measure the crack it left.</summary>
    internal static Heightfield UnskirtedCoarse(TerrainTileComponent t, float baseY)
    {
        var fine = t.Field(0f);
        int posts = TerrainTileComponent.CoarseCells + 1;
        float spacing = t.Size / TerrainTileComponent.CoarseCells;
        var cm = new short[posts * posts];
        for (int j = 0; j < posts; j++)
            for (int i = 0; i < posts; i++)
                cm[j * posts + i] = (short)Math.Clamp(MathF.Round(fine.HeightAt(i * spacing, j * spacing) * 100f), short.MinValue, short.MaxValue);
        return Heightfield.FromCentimetres(posts, spacing, baseY, cm, null, null);
    }

    /// <summary>What crossed a seam between two tiles of ground (<see cref="AcrossSeam"/>).</summary>
    internal sealed record SeamRays(int Rays, int Leaks, int Lines, int LineLeaks, bool[] Blocked);

    /// <summary>
    /// Rays across the seam between two tiles of ground in <paramref name="world"/>, at u = <paramref name="seamU"/>
    /// (u is x, or z when <paramref name="alongX"/>; v the other) from v0 to v1. <paramref name="low"/> and
    /// <paramref name="high"/> are the ground of the tile on the low and the high side of u, in world (x, z);
    /// <paramref name="truth"/> the full ground both sides, which places the lines of sight.
    /// <list type="bullet">
    /// <item>Grazing rays: 12 m, level to 0.15 rising or falling, up to 60 degrees off square to the seam, both
    /// ends over the ground they are in, through the seam under the ground of one side or the other there.
    /// Each must meet a front face: one that leaves the ground unseen got through the crack.</item>
    /// <item>Lines of sight from 1 to 1.6 m over the ground within 30 m of the seam on one side to the same on
    /// the other: each that passes the seam under either side's ground must meet a front face; and whether each
    /// is blocked at all, to compare one world's ground with another's.</item>
    /// </list>
    /// Front faces only, the strictest a query asks (the reverb's enclosure rays): a ray that enters the ground
    /// unseen and comes up through its underside is a leak too.
    /// </summary>
    internal static SeamRays AcrossSeam(TriangleWorld world, Func<float, float, float> low, Func<float, float, float> high,
                                        Func<float, float, float> truth, bool alongX, float seamU, float v0, float v1,
                                        int seed, int rays, int lines)
    {
        var rng = new Random(seed);
        var all = new AcceptAll();
        const GeometryLayers Layers = GeometryLayers.Sight | GeometryLayers.Acoustics;
        Vector3 W(float u, float y, float v) => alongX ? new Vector3(v, y, u) : new Vector3(u, y, v);
        float G(Func<float, float, float> f, float u, float v) => alongX ? f(v, u) : f(u, v);
        float U(Vector3 p) => alongX ? p.Z : p.X;
        float V(Vector3 p) => alongX ? p.X : p.Z;
        float Over(Vector3 p) => U(p) < seamU ? G(low, U(p), V(p)) : G(high, U(p), V(p));
        int made = 0, leaks = 0;
        for (int tries = 0; made < rays && tries < rays * 20; tries++)
        {
            float v = v0 + (float)rng.NextDouble() * (v1 - v0);
            float a = G(low, seamU, v), b = G(high, seamU, v);
            float top = MathF.Max(a, b), bottom = MathF.Min(a, b) - 0.5f;
            float h = bottom + (float)rng.NextDouble() * (top - bottom - 0.002f);
            float th = ((float)rng.NextDouble() * 2f - 1f) * MathF.PI / 3f;
            float sign = rng.NextDouble() < 0.5 ? 1f : -1f;
            float rise = ((float)rng.NextDouble() * 2f - 1f) * 0.15f;
            var d = Vector3.Normalize(W(sign * MathF.Cos(th), rise, MathF.Sin(th)));
            var x = W(seamU, h, v);
            const float L = 6f;
            Vector3 p = x - d * L, q = x + d * L;
            if (!(p.Y > Over(p) + 0.001f) || !(q.Y > Over(q) + 0.001f)) continue;
            made++;
            if (!world.Any(p, d, 2f * L, Layers, RayFaces.Front, ref all)) leaks++;
        }
        int sights = 0, lineLeaks = 0;
        var blocked = new bool[lines];
        for (int k = 0; k < lines; k++)
        {
            float pu = seamU - 0.5f - (float)rng.NextDouble() * 29.5f, qu = seamU + 0.5f + (float)rng.NextDouble() * 29.5f;
            float pv = v0 + (float)rng.NextDouble() * (v1 - v0), qv = v0 + (float)rng.NextDouble() * (v1 - v0);
            var p = W(pu, G(truth, pu, pv) + 1f + 0.6f * (float)rng.NextDouble(), pv);
            var q = W(qu, G(truth, qu, qv) + 1f + 0.6f * (float)rng.NextDouble(), qv);
            if (rng.NextDouble() < 0.5) (p, q) = (q, p);
            var d = q - p;
            float len = d.Length();
            d /= len;
            blocked[k] = world.Any(p, d, len, Layers, RayFaces.Front, ref all);
            var at = p + (q - p) * ((seamU - U(p)) / (U(q) - U(p)));
            bool under = at.Y < MathF.Max(G(low, seamU, V(at)), G(high, seamU, V(at)));
            if (!under || !(p.Y > Over(p)) || !(q.Y > Over(q))) continue;
            sights++;
            if (!blocked[k]) lineLeaks++;
        }
        return new SeamRays(made, leaks, sights, lineLeaks, blocked);
    }

    /// <summary>A tile's ground as a function of world (x, z), from its own heightfield.</summary>
    internal static Func<float, float, float> GroundOf(Heightfield f, Vector3 corner) => (x, z) => f.HeightAt(x - corner.X, z - corner.Z);

    /// <summary>
    /// Coarse ground beside full ground leaves no crack (docs/WORLD_STREAMING.md, Coarse ground): rough made-up
    /// ground in two tiles, one sent at 7.8 m and the other at 2 m, either way round; grazing rays and lines of
    /// sight across the seam all meet the ground where they pass under it. The coarse ground as it was let rays
    /// through; full ground has no skirt, so a 2 m seam is what it was.
    /// </summary>
    [Fact]
    public void CoarseGroundBesideFullGroundLeavesNoCrack()
    {
        Func<float, float, float> h = (x, z) => 20f + 3f * MathF.Sin(x / 13f) * MathF.Cos(z / 7f) + 1.5f * MathF.Sin(z / 3.1f + x / 5f)
                                              + 0.8f * MathF.Sin(z / 1.7f) + 0.5f * MathF.Cos(x / 2.3f + z / 2.9f);
        var west = TerrainTiles.Component(Posts, Spacing, Heights(h, 0f, 0f), out float wBase);
        var east = TerrainTiles.Component(Posts, Spacing, Heights(h, 250f, 0f), out float eBase);
        var wAt = new Vector3(125f, wBase, 125f);
        var eAt = new Vector3(375f, eBase, 125f);
        var wFine = EntityGeometry.TerrainSpec(1, wAt, west, Ground);
        var eFine = EntityGeometry.TerrainSpec(2, eAt, east, Ground);
        var wCoarse = EntityGeometry.TerrainSpec(1, wAt, west.Coarse(), Ground);
        var eCoarse = EntityGeometry.TerrainSpec(2, eAt, east.Coarse(), Ground);
        var wOld = SolidSpec.OfTerrain(1, wAt, UnskirtedCoarse(west, wBase), Ground);
        var eOld = SolidSpec.OfTerrain(2, eAt, UnskirtedCoarse(east, eBase), Ground);

        // Full ground is never skirted: the server's, and every 2 m seam, are what they were.
        Assert.False(west.IsCoarse);
        Assert.False(wFine.Terrain!.Skirted);
        Assert.Equal(wFine.Terrain.SurfaceTriangleCount, wFine.Terrain.TriangleCount);
        Assert.Equal(Heightfield.ContentHash(Posts, Spacing, wFine.Terrain.Heights, wFine.Terrain.Cells, wFine.Terrain.Materials), wFine.Terrain.Hash);
        // Coarse ground is skirted, told from the wire's own fields after a trip through MemoryPack.
        var sent = MemoryPackSerializer.Deserialize<TerrainTileComponent>(MemoryPackSerializer.Serialize(west.Coarse()))!;
        Assert.True(sent.IsCoarse);
        Assert.True(sent.Field(wBase).Skirted);
        Assert.Equal(wCoarse.Terrain!.Hash, sent.Field(wBase).Hash);
        var cf = wCoarse.Terrain;
        Assert.Equal(cf.SurfaceTriangleCount + 8 * TerrainTileComponent.CoarseCells, cf.TriangleCount);
        // The acoustic scene takes the skirt too; the full ground's triangles are as many as ever.
        int SceneTriangles(SolidSpec spec)
        {
            var verts = new List<Client.Core.AudioEngine.SteamAudio.Phonon.IPLVector3>();
            var tris = new List<Client.Core.AudioEngine.SteamAudio.Phonon.IPLTriangle>();
            var mats = new List<int>();
            Client.Core.AudioEngine.SteamAudio.SceneTerrain.Append(spec, Vector3.Zero, verts, tris, mats, (_, _) => 0);
            Assert.Equal(tris.Count, mats.Count);
            Assert.All(tris, t => Assert.True(t.i0 < verts.Count && t.i1 < verts.Count && t.i2 < verts.Count));
            return tris.Count;
        }
        Assert.Equal(cf.TriangleCount, SceneTriangles(wCoarse));
        Assert.Equal(2 * (Posts - 1) * (Posts - 1), SceneTriangles(wFine));

        // Each skirt triangle stands on the tile's edge, upright, facing out of the tile, down to the floor.
        for (int k = cf.SurfaceTriangleCount; k < cf.TriangleCount; k++)
        {
            var t = cf.Triangle(k);
            var n = Vector3.Normalize(t.Normal);
            Vector3 b = t.V0 + t.E1, c = t.V0 + t.E2;
            Assert.Equal(cf.FloorY, MathF.Min(t.V0.Y, MathF.Min(b.Y, c.Y)), 3);
            var mid = (t.V0 + b + c) / 3f;
            Vector3 outward = mid.X < 1e-3f ? -Vector3.UnitX : mid.X > cf.Size - 1e-3f ? Vector3.UnitX : mid.Z < 1e-3f ? -Vector3.UnitZ : Vector3.UnitZ;
            Assert.True(Vector3.Dot(n, outward) > 0.9999f, $"skirt triangle {k}: normal {n}, edge {outward}");
        }

        // The coarse edge is nowhere under the fine edge, and its corners are the fine corners.
        var wf = wFine.Terrain;
        float worstLift = 0f;
        for (float z = 0f; z <= 250f; z += 0.25f)
        {
            float under = wf.HeightAt(250f, z) - cf.HeightAt(250f, z);
            Assert.True(under <= 1e-4f, $"coarse edge {under * 100:F2} cm under the fine at z {z}");
            worstLift = MathF.Max(worstLift, -under);
        }
        Assert.Equal(wf.HeightAt(250f, 0f), cf.HeightAt(250f, 0f), 4);
        Assert.Equal(wf.HeightAt(250f, 250f), cf.HeightAt(250f, 250f), 4);

        SeamRays Probe(SolidSpec a, SolidSpec b)
            => AcrossSeam(WorldOf(a, b), GroundOf(a.Terrain!, a.TerrainCorner), GroundOf(b.Terrain!, b.TerrainCorner),
                          (x, z) => x < 250f ? wf.HeightAt(x, z) : eFine.Terrain!.HeightAt(x - 250f, z), false, 250f, 2f, 248f, 5, 4000, 2000);
        var full = Probe(wFine, eFine);
        var coarseWest = Probe(wCoarse, eFine);
        var coarseEast = Probe(wFine, eCoarse);
        var oldWest = Probe(wOld, eFine);
        var oldEast = Probe(wFine, eOld);
        int changed = 0, changedOld = 0;
        for (int k = 0; k < full.Blocked.Length; k++)
        {
            if (full.Blocked[k] != coarseWest.Blocked[k]) changed++;
            if (full.Blocked[k] != oldWest.Blocked[k]) changedOld++;
        }
        _o.WriteLine($"coarse edge over the fine edge by at most {worstLift * 100:F1} cm");
        foreach (var (name, r) in new[] { ("2 m | 2 m", full), ("7.8 m | 2 m", coarseWest), ("2 m | 7.8 m", coarseEast),
                                          ("7.8 m unskirted | 2 m", oldWest), ("2 m | 7.8 m unskirted", oldEast) })
            _o.WriteLine($"{name}: {r.Leaks} of {r.Rays} grazing rays through, {r.LineLeaks} of {r.Lines} lines of sight under the seam through");
        _o.WriteLine($"lines of sight across the seam blocked otherwise than by the 2 m ground, of {full.Blocked.Length}: {changed} skirted, {changedOld} unskirted");
        Assert.Equal(0, full.Leaks + full.LineLeaks);
        Assert.Equal(0, coarseWest.Leaks + coarseWest.LineLeaks);
        Assert.Equal(0, coarseEast.Leaks + coarseEast.LineLeaks);
        Assert.True(oldWest.Leaks + oldEast.Leaks > 0, "the unskirted coarse ground should let some rays through, or the probe cannot see a crack");
        Assert.True(full.Rays > 3000 && full.Lines > 100, $"{full.Rays} rays, {full.Lines} lines under the seam");
    }
}
