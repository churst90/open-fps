using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Resonance's ec6b4e4f, asked of open-fps (inbox/2026-10-17-resonance-audio-open-fps-lacks, 1.3): its way round
/// believed the hair-wide joint between pieces standing flush and between a shut leaf and its jamb. Here the
/// barrier model buries an edge within the joint of another box (OpeningRoutes.Buried), checks a clear way
/// round against the whole scene, and does not go round a leaf standing in its doorway, so neither leaks.
/// </summary>
public class ResonanceMaybesGeometryTests
{
    private readonly ITestOutputHelper _o;
    public ResonanceMaybesGeometryTests(ITestOutputHelper o) => _o = o;

    private static int Index(string material) => AcousticRegistry.GetProperties(material).ResonanceIndex;
    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-6f, g));
    private static float DbE(float e) => 10f * MathF.Log10(MathF.Max(1e-12f, e));

    private const int RoomA = 701, RoomB = 702, Door = 801;
    private static readonly Vector3 LeafSize = new(1.1f, 2.1f, 0.04f);
    private static readonly Quaternion Across = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

    /// <summary>Two rooms, A (x &lt; 0) and B (x &gt; t), either side of a brick wall t thick with a 1.0 m doorway
    /// at z 2..3 under a lintel standing flush on its jambs; a wooden leaf lapping the jambs, its middle at
    /// x = leafX, shut or swung into A. The wall beyond the doorway is two pieces standing flush at z 4.5, or one
    /// (<paramref name="onePiece"/>).</summary>
    private static WorldSnapshot Rooms(float t, float leafX, bool open, bool onePiece = false)
    {
        AcousticRegistry.EnsureInitialized();
        var defs = new List<EntityDefinition>();
        int id = 1;
        void Box(float x0, float x1, float y0, float y1, float z0, float z1, string material)
            => defs.Add(Solid(id++, new Vector3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2),
                              new Vector3(x1 - x0, y1 - y0, z1 - z0), Quaternion.Identity, material, WallBuild.Solid));
        float xb = t + 4f;
        Box(-30, 30, -0.2f, 0, -30, 30, "Concrete");                // the ground
        Box(-4.3f, xb + 0.3f, 2.7f, 2.9f, -0.3f, 6.3f, "Concrete"); // the roof over both
        Box(-4.3f, -4f, 0, 2.7f, -0.3f, 6.3f, "Brick");              // the outer walls
        Box(xb, xb + 0.3f, 0, 2.7f, -0.3f, 6.3f, "Brick");
        Box(-4.3f, xb + 0.3f, 0, 2.7f, -0.3f, 0, "Brick");
        Box(-4.3f, xb + 0.3f, 0, 2.7f, 6f, 6.3f, "Brick");
        // The wall between, in pieces.
        Box(0, t, 0, 2.7f, 0, 2, "Brick");
        if (onePiece) Box(0, t, 0, 2.7f, 3, 6, "Brick");
        else { Box(0, t, 0, 2.7f, 3, 4.5f, "Brick"); Box(0, t, 0, 2.7f, 4.5f, 6, "Brick"); }
        Box(0, t, 2.1f, 2.7f, 2, 3, "Brick");

        var shut = new Vector3(leafX, 1.05f, 2.5f);
        var at = shut;
        var rot = Across;
        if (open)
        {
            Vector3 hinge = shut + Vector3.Transform(new Vector3(LeafSize.X / 2, 0, 0), Across);
            rot = Across * Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2f);
            at = hinge + Vector3.Transform(new Vector3(-LeafSize.X / 2, 0, 0), rot);
        }
        var leaf = Solid(Door, at, LeafSize, rot, "Wood", new WallBuild(0.04f, 0f));
        leaf.Portal = new PortalComponent { RegionAId = RoomA, RegionBId = RoomB, OpeningCentre = shut, OpeningRotation = Across };
        defs.Add(leaf);

        int brick = Index("Brick"), concrete = Index("Concrete"), plaster = Index("Plaster");
        defs.Add(Region(RoomA, new Vector3(-2f, 1.35f, 3f), new Vector3(4f, 2.7f, 6f), new[] { concrete, concrete, brick, brick, plaster, brick }));
        defs.Add(Region(RoomB, new Vector3(t + 2f, 1.35f, 3f), new Vector3(4f, 2.7f, 6f), new[] { concrete, concrete, brick, brick, plaster, brick }));

        var world = new WorldSnapshot();
        foreach (var d in defs)
            world.Entities[d.EntityId] = new EntitySnapshot { Id = d.EntityId, Definition = d, Transform = d.Transform };
        world.AcousticMap = EntityAcoustics.GenerateRegions(defs, new Vector3(100, 20, 100), new Vector3(-50, -5, -50));
        return world;
    }

    private static EntityDefinition Solid(int id, Vector3 at, Vector3 size, Quaternion rot, string material, WallBuild build) => new()
    {
        EntityId = id,
        Type = EntityType.StaticObject,
        Transform = new Transform { Position = at, Rotation = rot, Scale = Vector3.One },
        Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
        Material = new MaterialComponent { Material = material },
        Acoustics = new AcousticComponent { LeafMetres = build.LeafMetres, StudSpacingMetres = build.StudSpacingMetres },
    };

    private static EntityDefinition Region(int id, Vector3 at, Vector3 size, int[] materials) => new()
    {
        EntityId = id,
        Transform = new Transform { Position = at, Rotation = Quaternion.Identity },
        Region = new RegionComponent { FriendlyName = $"r{id}", RoomSize = size, Materials = materials, IsIndoor = true },
    };

    private static (OpeningRoutes Model, SpatialAcoustics Acoustics) Graph(WorldSnapshot world)
    {
        var acoustics = new SpatialAcoustics();
        return (acoustics.RoutesFor(world)!, acoustics);
    }

    /// <summary>
    /// A shut leaf in its doorway, a metre either side, straight and slantwise, in a 0.11 m and a 0.3 m wall,
    /// the leaf at the wall's middle and at its face: what reaches the ear by the route and by the one-shots'
    /// path is the leaf's own transmission at most. The leaf laps its jambs and the lintel stands flush on
    /// them, and neither hair-wide joint is a way round (Resonance's way round believed both: -10/-16/-24 dB
    /// behind a leaf passing -19/-35/-51).
    /// </summary>
    [Theory]
    [InlineData(0.11f, false)]
    [InlineData(0.11f, true)]
    [InlineData(0.3f, false)]
    [InlineData(0.3f, true)]
    public void AShutLeafPassesNoMoreThanItsOwnTransmission(float t, bool atFace)
    {
        var world = Rooms(t, atFace ? 0.02f : t / 2, open: false);
        var (model, acoustics) = Graph(world);
        var tau = model.Openings.Single(o => o.Id == Door).Tau;
        var leaf = new Vector3(DbE(tau.X), DbE(tau.Y), DbE(tau.Z));
        Assert.True(leaf.Y < -15f, $"the leaf passes {leaf}");
        foreach (var (src, lis) in new[]
                 {
                     (new Vector3(-1f, 1.2f, 2.5f), new Vector3(t + 1f, 1.2f, 2.5f)),
                     (new Vector3(-1f, 1.2f, 3.4f), new Vector3(t + 1f, 1.2f, 1.6f)),
                     (new Vector3(-0.4f, 1.9f, 2.9f), new Vector3(t + 0.4f, 1.9f, 2.9f)),
                 })
        {
            Assert.True(model.Route(src, acoustics.GetRegionAt(world, src), lis, acoustics.GetRegionAt(world, lis), out var a), "no route");
            var p = acoustics.CalculateAcousticPath(world, -1, lis, src);
            var g = model.BarrierGains(src, lis, WallTransmission.SoundSpeed, out _, out _, out var edge, out bool found);
            _o.WriteLine($"{src} to {lis}: leaf {leaf.X:F1}/{leaf.Y:F1}/{leaf.Z:F1}, route {Db(a.Low):F1}/{Db(a.Mid):F1}/{Db(a.High):F1}, " +
                         $"path {Db(p.EqLow):F1}/{Db(p.EqMid):F1}/{Db(p.EqHigh):F1}, round {(found ? $"{Db(g.X):F1}/{Db(g.Y):F1}/{Db(g.Z):F1} at {edge}" : "none")}");
            Assert.True(Db(a.Low) <= leaf.X + 1f && Db(a.Mid) <= leaf.Y + 1f && Db(a.High) <= leaf.Z + 1f,
                        $"route {Db(a.Low):F1}/{Db(a.Mid):F1}/{Db(a.High):F1} past a leaf passing {leaf}");
            Assert.True(Db(p.EqLow) <= leaf.X + 1f && Db(p.EqMid) <= leaf.Y + 1f && Db(p.EqHigh) <= leaf.Z + 1f,
                        $"path {Db(p.EqLow):F1}/{Db(p.EqMid):F1}/{Db(p.EqHigh):F1} past a leaf passing {leaf}");
            if (found)
                Assert.True(Db(g.X) <= leaf.X + 1f && Db(g.Y) <= leaf.Y + 1f && Db(g.Z) <= leaf.Z + 1f,
                            $"round {Db(g.X):F1}/{Db(g.Y):F1}/{Db(g.Z):F1} at {edge}, past a leaf passing {leaf}");
        }
    }

    /// <summary>
    /// A wall of two pieces standing flush, a metre either side of their joint: heard as the same wall in one
    /// piece is, and nothing bent round the joint's edge beats the line through the wall.
    /// </summary>
    [Theory]
    [InlineData(0.11f)]
    [InlineData(0.3f)]
    public void AFlushJointIsNoWayRound(float t)
    {
        var src = new Vector3(-1f, 1.2f, 4.5f);
        var lis = new Vector3(t + 1f, 1.2f, 4.5f);
        var pieces = Rooms(t, t / 2, open: false);
        var whole = Rooms(t, t / 2, open: false, onePiece: true);
        var (model, _) = Graph(pieces);
        var p2 = new SpatialAcoustics().CalculateAcousticPath(pieces, -1, lis, src);
        var p1 = new SpatialAcoustics().CalculateAcousticPath(whole, -1, lis, src);
        new SpatialService().GetOcclusionData(pieces, lis, src, out _, out _, out float l, out float m, out float h);
        var g = model.BarrierGains(src, lis, WallTransmission.SoundSpeed, out _, out _, out var edge, out bool found);
        _o.WriteLine($"two pieces {Db(p2.EqLow):F1}/{Db(p2.EqMid):F1}/{Db(p2.EqHigh):F1}, one {Db(p1.EqLow):F1}/{Db(p1.EqMid):F1}/{Db(p1.EqHigh):F1}, " +
                     $"through {Db(l):F1}/{Db(m):F1}/{Db(h):F1}, round {(found ? $"{Db(g.X):F1}/{Db(g.Y):F1}/{Db(g.Z):F1} at {edge}" : "none")}");
        // Each piece is half the panel the whole is: about a decibel more through it, no more.
        Assert.InRange(Db(p2.EqLow) - Db(p1.EqLow), -1.5f, 1.5f);
        Assert.InRange(Db(p2.EqMid) - Db(p1.EqMid), -1.5f, 1.5f);
        Assert.InRange(Db(p2.EqHigh) - Db(p1.EqHigh), -1.5f, 1.5f);
        Assert.True(Db(p2.EqMid) < -30f, $"mid {Db(p2.EqMid):F1} through brick");
        // A way bent round the joint's own edge (z 4.5) passes no more than the wall; one round the doorway's
        // jamb, through the shut leaf, is the doorway's and the whole wall has it too.
        if (found && MathF.Abs(edge.Z - 4.5f) < 0.2f)
            Assert.True(Db(g.X) <= Db(l) + 0.5f && Db(g.Y) <= Db(m) + 0.5f && Db(g.Z) <= Db(h) + 0.5f,
                        $"round the joint {Db(g.X):F1}/{Db(g.Y):F1}/{Db(g.Z):F1} at {edge}, through {Db(l):F1}/{Db(m):F1}/{Db(h):F1}");
        var g1 = Graph(whole).Model.BarrierGains(src, lis, WallTransmission.SoundSpeed, out _, out _, out _, out bool found1);
        Vector3 best1 = found1 ? g1 : Vector3.Zero;
        if (found)
            Assert.True(Db(g.X) <= Db(best1.X) + 0.5f || Db(g.X) <= Db(l) + 0.5f, $"low round {Db(g.X):F1} against the whole wall's {Db(best1.X):F1}");
        if (found)
            Assert.True(Db(g.Y) <= Db(best1.Y) + 0.5f || Db(g.Y) <= Db(m) + 0.5f, $"mid round {Db(g.Y):F1} against the whole wall's {Db(best1.Y):F1}");
        if (found)
            Assert.True(Db(g.Z) <= Db(best1.Z) + 0.5f || Db(g.Z) <= Db(h) + 0.5f, $"high round {Db(g.Z):F1} against the whole wall's {Db(best1.Z):F1}");
    }
}
