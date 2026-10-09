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
}
