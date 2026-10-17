using System.Numerics;
using OpenFPS.Audio.Acoustics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Acoustics;
using Xunit.Abstractions;
using OpenFPS.Geometry;
using OpenFPS.Materials;

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

    // ── A doorway route leaves from the wall's face (Resonance's 1.2) ───────────────────────────

    /// <summary>Where a route is heard round the doorway's corners: a source in A off to one side and the ear in B
    /// off to the other, both ways round and at both jambs. The first two are beyond the hinge of a leaf swung open
    /// into A, the last two behind it.</summary>
    private static (Vector3 Source, Vector3 Listener)[] Slanting(float t) => new[]
    {
        (new Vector3(-2f, 1.5f, 4.5f), new Vector3(t + 0.3f, 1.5f, 5.5f)),
        (new Vector3(-0.6f, 1.5f, 5.5f), new Vector3(t + 2f, 1.5f, 4.5f)),
        (new Vector3(-2f, 1.5f, 0.5f), new Vector3(t + 0.6f, 1.5f, 0.4f)),
        (new Vector3(-1.5f, 1.5f, 1.2f), new Vector3(t + 1.5f, 1.5f, 4.2f)),
    };

    private static Vector3 RouteDb(WorldSnapshot world, OpeningRoutes model, SpatialAcoustics acoustics, Vector3 src, Vector3 lis)
    {
        Assert.True(model.Route(src, acoustics.GetRegionAt(world, src), lis, acoustics.GetRegionAt(world, lis), out var a), $"no route {src} to {lis}");
        return new Vector3(Db(a.Low), Db(a.Mid), Db(a.High));
    }

    private static string Bands(Vector3 db) => $"{db.X:F1}/{db.Y:F1}/{db.Z:F1}";

    /// <summary>
    /// A leaf hung at either face of a 0.11 m and a 0.3 m wall, shut, and at A's face swung open into A: what the route
    /// delivers round the doorway's corners is what it delivers with the leaf at the wall's middle, within a decibel
    /// (open, beyond its hinge: behind it the open leaf itself stands in the way, a little further out). The opening is
    /// the wall's depth from face to face, so a leg into either room leaves from that room's face of the wall. It ran
    /// from the leaf's plane as deep as half the wall, ended inside the wall short of the far face and paid the jamb's
    /// brick (0.3 m, open: -44.0/-45.3/-58.7 dB with the leaf at a face against -37.4/-40.1/-42.0 at the middle).
    /// </summary>
    [Theory]
    [InlineData(0.11f, true)]
    [InlineData(0.11f, false)]
    [InlineData(0.3f, true)]
    [InlineData(0.3f, false)]
    public void ALeafHungAtAFaceIsHeardAsOneAtTheWallsMiddle(float t, bool open)
    {
        var mid = Rooms(t, t / 2, open);
        var (midModel, midAcoustics) = Graph(mid);
        foreach (float leafX in open ? new[] { 0.02f } : new[] { 0.02f, t - 0.02f })
        {
            var face = Rooms(t, leafX, open);
            var (faceModel, faceAcoustics) = Graph(face);
            foreach (var (src, lis) in open ? Slanting(t).Take(2) : Slanting(t))
            {
                var m = RouteDb(mid, midModel, midAcoustics, src, lis);
                var f = RouteDb(face, faceModel, faceAcoustics, src, lis);
                _o.WriteLine($"t {t} {(open ? "open" : "shut")} leaf x {leafX:F2}: {src} to {lis}: at the face {Bands(f)}, at the middle {Bands(m)}");
                Assert.True(MathF.Abs(f.X - m.X) <= 1f && MathF.Abs(f.Y - m.Y) <= 1f && MathF.Abs(f.Z - m.Z) <= 1f,
                            $"leaf at x {leafX:F2} in a {t} m wall, {(open ? "open" : "shut")}: {Bands(f)} at the face against {Bands(m)} at the middle");
            }
        }
    }

    /// <summary>
    /// A sound beside a small room, reached round its corner and in at its open door, comes in at the door and not
    /// through the small room's wall: the way leaves the doorway from the face on the big room's side, though the sound
    /// lies beyond the doorway's plane on the small room's. Resonance's case (its 11f9886b): a telephone in a car lot's
    /// office heard from its restroom at -43 dB through the open door, the leaf hung flush with the wall's inner face.
    /// </summary>
    [Fact]
    public void ASoundBesideASmallRoomComesInAtItsDoorNotThroughItsWall()
    {
        AcousticRegistry.EnsureInitialized();
        const float H = 2.7f, T = 0.3f, W = 0.114f, half = W / 2f;
        var defs = new List<EntityDefinition>();
        int id = 1;
        void Box(float x0, float x1, float y0, float y1, float z0, float z1, string material)
            => defs.Add(Solid(id++, new Vector3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2),
                              new Vector3(x1 - x0, y1 - y0, z1 - z0), Quaternion.Identity, material, WallBuild.Solid));
        Box(-T, 6f + T, -T, 0f, -T, 5f + T, "Concrete");
        Box(-T, 6f + T, H, H + T, -T, 5f + T, "Concrete");
        Box(-T, 0f, 0f, H, -T, 5f + T, "Brick");
        Box(6f, 6f + T, 0f, H, -T, 5f + T, "Brick");
        Box(0f, 6f, 0f, H, -T, 0f, "Brick");
        Box(0f, 6f, 0f, H, 5f, 5f + T, "Brick");
        // The small room in the big one's north-east corner, its door in its south wall at x 3.5 to 4.4.
        Box(3f - half, 3f + half, 0f, H, 2f - half, 5f, "Plaster");
        Box(3f + half, 3.5f, 0f, H, 2f - half, 2f + half, "Plaster");
        Box(4.4f, 6f, 0f, H, 2f - half, 2f + half, "Plaster");
        Box(3.5f, 4.4f, 2.1f, H, 2f - half, 2f + half, "Plaster");
        // The leaf hung flush with the wall's north face, swung open into the small room.
        var size = new Vector3(1.0f, 2.1f, 0.036f);
        var shut = new Vector3(3.95f, 1.05f, 2f + half - 0.018f);
        var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        var at = shut + new Vector3(size.X / 2, 0, 0) + Vector3.Transform(new Vector3(-size.X / 2, 0, 0), rot);
        var leaf = Solid(Door, at, size, rot, "Wood", new WallBuild(0.036f, 0f));
        leaf.Portal = new PortalComponent { RegionAId = RoomA, RegionBId = RoomB, OpeningCentre = shut, OpeningRotation = Quaternion.Identity };
        defs.Add(leaf);
        int brick = Index("Brick"), concrete = Index("Concrete"), plaster = Index("Plaster");
        defs.Add(Region(RoomA, new Vector3(3f, H / 2f, 2.5f), new Vector3(6f, H, 5f), new[] { concrete, concrete, brick, brick, brick, brick }));
        defs.Add(Region(RoomB, new Vector3(4.5f + half / 2f, H / 2f, 3.5f + half / 2f), new Vector3(3f - half, H, 3f - half),
                        new[] { concrete, concrete, brick, plaster, brick, plaster }));
        var world = new WorldSnapshot();
        foreach (var d in defs)
            world.Entities[d.EntityId] = new EntitySnapshot { Id = d.EntityId, Definition = d, Transform = d.Transform };
        world.AcousticMap = EntityAcoustics.GenerateRegions(defs, new Vector3(100, 20, 100), new Vector3(-50, -5, -50));

        var (model, acoustics) = Graph(world);
        var source = new Vector3(2f, 1.2f, 3.5f);
        var listener = new Vector3(4f, 1.6f, 2.5f);
        Assert.Equal(RoomA, acoustics.GetRegionAt(world, source));
        Assert.Equal(RoomB, acoustics.GetRegionAt(world, listener));
        var a = RouteDb(world, model, acoustics, source, listener);
        var back = RouteDb(world, model, acoustics, listener, source);
        _o.WriteLine($"beside the small room to inside it: {Bands(a)} dB, the other way {Bands(back)}");
        Assert.True(a.Y > -20f && back.Y > -20f, $"mid {a.Y:F1} and {back.Y:F1} dB: through the small room's wall");
    }

    /// <summary>
    /// A shut leaf at a face is still charged: the opening is measured through the wall from face to face, so the leaf
    /// stands in it wherever in the wall it hangs, and what the route delivers round the corners shut is what it delivers open less the leaf and its gaps
    /// (the opening's Tau), within three decibels. Only where the open leaf, swung into A, is not in the way: from beyond
    /// its hinge.
    /// </summary>
    [Theory]
    [InlineData(0.11f)]
    [InlineData(0.3f)]
    public void AShutLeafAtAFaceIsChargedItsLeafAndGaps(float t)
    {
        foreach (float leafX in new[] { 0.02f, t / 2, t - 0.02f })
        {
            var shut = Rooms(t, leafX, open: false);
            var opened = Rooms(t, leafX, open: true);
            var (shutModel, shutAcoustics) = Graph(shut);
            var (openModel, openAcoustics) = Graph(opened);
            var door = shutModel.Openings.Single(x => x.Id == Door);
            Assert.True(door.LeafCover > 0.9f && door.Gaps.Y > 0f, $"leaf cover {door.LeafCover}, gaps {door.Gaps}");
            var tau = new Vector3(DbE(door.Tau.X), DbE(door.Tau.Y), DbE(door.Tau.Z));
            foreach (var (src, lis) in Slanting(t).Take(2))
            {
                var s = RouteDb(shut, shutModel, shutAcoustics, src, lis);
                var o = RouteDb(opened, openModel, openAcoustics, src, lis);
                var less = s - o;
                _o.WriteLine($"t {t} leaf x {leafX:F2}: {src} to {lis}: shut {Bands(s)}, open {Bands(o)}, shut less open {Bands(less)}, leaf and gaps {Bands(tau)}");
                Assert.True(MathF.Abs(less.X - tau.X) <= 3f && MathF.Abs(less.Y - tau.Y) <= 3f && MathF.Abs(less.Z - tau.Z) <= 3f,
                            $"shut less open {Bands(less)} against the leaf and gaps' {Bands(tau)}");
            }
        }
    }
}
