using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;

namespace OpenFPS.Tests;

/// <summary>
/// Routes through openings (OpeningRoutes): the way in from the street by a front door, a stairwell and
/// a doorway — two corners, through a room — that the one-obstacle barrier search cannot find, and the
/// flat leak the old portal branch put in its place.
///
/// The building, in plan (x across, z along; the street is x &lt; 0):
///
///   x=0 front wall, a 1.8 m doorway at z 2..3.8 with a steel leaf in it
///   x 0.3..4.7, z 0.3..6     the stairwell (tile, brick)
///   x=4.7..5.0 inner wall, a 1.0 m doorway at z 2..3
///   x 5.0..7.0, z 0.3..19.7  the corridor (carpet, plaster), running away from the doorway
///   x 0.3..4.7, z 6.3..19.7  a flat with no way in from here
/// </summary>
public class OpeningRoutesTests
{
    private static int Index(string material) => AcousticRegistry.GetProperties(material).ResonanceIndex;
    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-6f, g));

    private const int Stair = 501, Corridor = 502, Flat = 503, FrontDoor = 601, InnerDoorway = 602;

    /// <summary>Where the corridor's listener stands: well along it, out of sight of both doorways.</summary>
    private static readonly Vector3 InCorridor = new(6.0f, 1.6f, 12f);
    /// <summary>A car on the street, round the corner from the front door.</summary>
    private static readonly Vector3 OnStreet = new(-8f, 0.6f, 9f);

    private static readonly Vector3 LeafSize = new(1.9f, 2.1f, 0.05f);
    private static readonly Quaternion Across = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
    private static readonly Vector3 LeafShut = new(0.15f, 1.05f, 2.9f);

    private static WorldSnapshot Building(bool frontDoorOpen, float doorwayHeight = 2.1f)
    {
        AcousticRegistry.EnsureInitialized();
        var defs = new List<EntityDefinition>();
        int id = 1;
        void Box(float x0, float x1, float y0, float y1, float z0, float z1, string material)
            => defs.Add(Solid(id++, new Vector3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2),
                              new Vector3(x1 - x0, y1 - y0, z1 - z0), Quaternion.Identity, material, WallBuild.Solid));

        Box(-30, 40, -0.2f, 0, -20, 40, "Concrete");          // the ground
        Box(0, 7.3f, 2.7f, 2.9f, 0, 20, "Concrete");           // the roof slab
        // Front wall, with its doorway.
        Box(0, 0.3f, 0, 2.7f, 0, 2, "Brick");
        Box(0, 0.3f, 0, 2.7f, 3.8f, 20, "Brick");
        Box(0, 0.3f, doorwayHeight, 2.7f, 2, 3.8f, "Brick");
        Box(7.0f, 7.3f, 0, 2.7f, 0, 20, "Brick");               // back wall
        Box(0, 7.3f, 0, 2.7f, 0, 0.3f, "Brick");                // end walls
        Box(0, 7.3f, 0, 2.7f, 19.7f, 20, "Brick");
        // The inner wall, with its doorway.
        Box(4.7f, 5.0f, 0, 2.7f, 0.3f, 2, "Plaster");
        Box(4.7f, 5.0f, 0, 2.7f, 3, 19.7f, "Plaster");
        Box(4.7f, 5.0f, 2.1f, 2.7f, 2, 3, "Plaster");
        Box(0.3f, 4.7f, 0, 2.7f, 6, 6.3f, "Brick");              // stairwell from the flat

        // The steel front door: shut in its doorway, or swung a quarter turn about its hinge.
        var leafAt = LeafShut;
        var leafRot = Across;
        if (frontDoorOpen)
        {
            Vector3 hinge = LeafShut + Vector3.Transform(new Vector3(LeafSize.X / 2, 0, 0), Across);
            leafRot = Across * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
            leafAt = hinge + Vector3.Transform(new Vector3(-LeafSize.X / 2, 0, 0), leafRot);
        }
        var leaf = Solid(FrontDoor, leafAt, LeafSize, leafRot, "Metal", new WallBuild(0.0012f, 0f));
        leaf.Portal = new PortalComponent { RegionAId = Stair, RegionBId = AcousticConstants.GlobalRegionId,
                                            OpeningCentre = LeafShut, OpeningRotation = Across };
        defs.Add(leaf);

        // The inner doorway: an authored portal, wider than the gap it was put in.
        defs.Add(new EntityDefinition
        {
            EntityId = InnerDoorway,
            Transform = new Transform { Position = new Vector3(4.85f, 1.05f, 2.5f), Rotation = Quaternion.Identity },
            Portal = new PortalComponent { RegionAId = Stair, RegionBId = Corridor, ApertureSize = 1.4f },
        });

        int tile = Index("Tile"), brick = Index("Brick"), plaster = Index("Plaster"), carpet = Index("Carpet"), concrete = Index("Concrete");
        defs.Add(Region(Stair, new Vector3(2.5f, 1.35f, 3.15f), new Vector3(4.4f, 2.7f, 5.7f), new[] { tile, concrete, brick, brick, plaster, brick }));
        defs.Add(Region(Corridor, new Vector3(6.0f, 1.35f, 10f), new Vector3(2.0f, 2.7f, 19.4f), new[] { carpet, plaster, brick, brick, brick, plaster }));
        defs.Add(Region(Flat, new Vector3(2.5f, 1.35f, 13f), new Vector3(4.4f, 2.7f, 13.4f), new[] { carpet, plaster, brick, brick, plaster, brick }));

        var world = new WorldSnapshot();
        foreach (var d in defs)
            world.Entities[d.EntityId] = new EntitySnapshot { Id = d.EntityId, Definition = d, Transform = d.Transform };
        world.AcousticMap = AcousticVolumeGenerator.GenerateRegions(defs, new Vector3(100, 20, 100), new Vector3(-50, -5, -50));
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

    private static OpeningRoutes.Answer Ask(WorldSnapshot world, Vector3 source, Vector3 listener)
    {
        var (model, acoustics) = Graph(world);
        Assert.True(model.Route(source, acoustics.GetRegionAt(world, source), listener, acoustics.GetRegionAt(world, listener), out var a),
                    "no route found");
        return a;
    }

    /// <summary>What the walls on the straight line let through, per band, dB.</summary>
    private static (float L, float M, float H) ThroughWalls(WorldSnapshot world, Vector3 source, Vector3 listener)
    {
        new OpenFPS.Client.Core.SpatialService().GetOcclusionData(world, listener, source, out _, out _, out float l, out float m, out float h);
        return (Db(l), Db(m), Db(h));
    }

    // ── Standing in a doorway ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A doorway, the wall's own thickness, is the room on your side for sound and the doorway for
    /// naming. Read by the boxes alone it was the outdoors: "bursts of outside noise when I step into
    /// different zones" (2026-10-03), the outdoor bus going from 1 % to 100 % in the doorway.
    /// </summary>
    [Fact]
    public void ADoorwayBetweenRoomsIsNeverTheOutdoors()
    {
        var world = Building(frontDoorOpen: true);
        var (_, acoustics) = Graph(world);
        var stairSide = new Vector3(4.75f, 1.6f, 2.5f);
        var corridorSide = new Vector3(4.95f, 1.6f, 2.5f);
        Assert.Equal(AcousticConstants.GlobalRegionId, acoustics.GetZoneAt(world, stairSide));
        Assert.Equal(AcousticConstants.GlobalRegionId, acoustics.GetZoneAt(world, corridorSide));
        Assert.Equal(Stair, acoustics.GetRegionAt(world, stairSide));
        Assert.Equal(Corridor, acoustics.GetRegionAt(world, corridorSide));
    }

    /// <summary>The front door's inner half is the stairwell, its outer half the street; a wall with no
    /// opening is not a doorway.</summary>
    [Fact]
    public void TheFrontDoorwayIsHalfInAndAWallIsNotADoorway()
    {
        var world = Building(frontDoorOpen: true);
        var (_, acoustics) = Graph(world);
        Assert.Equal(Stair, acoustics.GetRegionAt(world, new Vector3(0.25f, 1.6f, 2.9f)));
        Assert.Equal(AcousticConstants.GlobalRegionId, acoustics.GetRegionAt(world, new Vector3(0.05f, 1.6f, 2.9f)));
        Assert.Equal(AcousticConstants.GlobalRegionId, acoustics.GetRegionAt(world, new Vector3(-0.1f, 1.6f, 10f)));
    }

    // ── The geometry, read ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheOpeningsAreWhereTheWallsSayAndAsBig()
    {
        var (model, _) = Graph(Building(frontDoorOpen: false));
        var inner = model.Openings.Single(o => o.Id == InnerDoorway);
        // Authored 1.4 m wide in a 1.0 m gap: the gap is the opening.
        Assert.Equal(1.0f, 2f * inner.HalfWidth, 2);
        Assert.Equal(2.1f, 2f * inner.HalfHeight, 1);
        Assert.Equal(2.5f, inner.Centre.Z, 2);
        Assert.Equal(0.3f, 2f * inner.HalfDepth, 2);           // as deep as the wall it is cut through
        Assert.Equal(1f, MathF.Abs(inner.Normal.X), 3);
        Assert.Equal(Vector3.One, inner.Tau);                  // nothing stands in it

        var front = model.Openings.Single(o => o.Id == FrontDoor);
        Assert.Equal(1.8f, 2f * front.HalfWidth, 2);            // the leaf laps the jambs; the gap is 1.8
        Assert.Equal(OpeningRoutes.Outside, front.NodeB == Stair ? front.NodeA : front.NodeB);
        Assert.Empty(model.Problems);
    }

    [Fact]
    public void AnOpenLeafLeavesItsDoorwayWhereItWas()
    {
        var shut = Graph(Building(frontDoorOpen: false)).Model.Openings.Single(o => o.Id == FrontDoor);
        var open = Graph(Building(frontDoorOpen: true)).Model.Openings.Single(o => o.Id == FrontDoor);
        Assert.True(Vector3.Distance(shut.Centre, open.Centre) < 0.01f, $"shut {shut.Centre}, open {open.Centre}");
        // Shut, the leaf's own construction; swung aside, nothing.
        var (l, m, h) = WallTransmission.BandGains("Metal", LeafSize, new WallBuild(0.0012f, 0f));
        Assert.Equal(10f * MathF.Log10(l * l), 10f * MathF.Log10(shut.Tau.X), 1);
        Assert.Equal(10f * MathF.Log10(m * m), 10f * MathF.Log10(shut.Tau.Y), 1);
        Assert.Equal(10f * MathF.Log10(h * h), 10f * MathF.Log10(shut.Tau.Z), 1);
        Assert.Equal(Vector3.One, open.Tau);
    }

    [Fact]
    public void AnOpeningInsideAWallIsReportedAsNone()
    {
        var world = Building(frontDoorOpen: false);
        var map = world.AcousticMap!;
        var portals = new Dictionary<int, (PortalComponent Portal, Vector3 Position)>(map.Portals)
        {
            [-1500] = (new PortalComponent { RegionAId = Corridor, RegionBId = Flat, ApertureSize = 1f }, new Vector3(4.85f, 1.05f, 12f)),
        };
        map.Portals = portals;
        var (model, _) = Graph(world);
        Assert.Contains(model.Problems, p => p.Contains("-1500") && p.Contains("wall stands in it"));
    }

    // ── Routes ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATwoBendRouteThroughAnOpenDoorIsFoundAndBeatsTheWall()
    {
        var world = Building(frontDoorOpen: true);
        var a = Ask(world, OnStreet, InCorridor);
        Assert.Contains("door 601", a.Via);
        Assert.Contains("doorway 602", a.Via);
        var wall = ThroughWalls(world, OnStreet, InCorridor);
        Assert.True(Db(a.Low) > wall.L + 10f, $"low: route {Db(a.Low):F1}, wall {wall.L:F1}");
        Assert.True(Db(a.Mid) > wall.M + 10f, $"mid: route {Db(a.Mid):F1}, wall {wall.M:F1}");
        Assert.True(Db(a.High) > wall.H + 10f, $"high: route {Db(a.High):F1}, wall {wall.H:F1}");
        Assert.True(Vector3.Distance(a.Apparent, new Vector3(4.85f, 1.05f, 2.5f)) < 1.2f, $"arrives from {a.Apparent}");

        // The one-shots' path takes it too: louder than through the wall, from the doorway's side.
        var p = new SpatialAcoustics().CalculateAcousticPath(world, -1, InCorridor, OnStreet);
        Assert.True(Db(p.EqMid) > wall.M + 10f, $"one-shot mid {Db(p.EqMid):F1}");
        Vector3 toDoorway = Vector3.Normalize(new Vector3(4.85f, 1.05f, 2.5f) - InCorridor);
        Assert.True(Vector3.Dot(Vector3.Normalize(p.ApparentPosition - InCorridor), toDoorway) > 0.95f, $"heard from {p.ApparentPosition}");
    }

    [Fact]
    public void AShutDoorOnTheRouteChargesItsTransmission()
    {
        var open = Ask(Building(frontDoorOpen: true), OnStreet, InCorridor);
        var shutWorld = Building(frontDoorOpen: false);
        var shut = Ask(shutWorld, OnStreet, InCorridor);
        var tau = Graph(shutWorld).Model.Openings.Single(o => o.Id == FrontDoor).Tau;
        // The route passes the leaf once: shutting it takes its transmission, give or take the
        // stairwell's absorption.
        float Loss(float o, float s) => Db(s) - Db(o);
        Assert.InRange(Loss(open.Low, shut.Low), 10f * MathF.Log10(tau.X) - 3f, 10f * MathF.Log10(tau.X) + 3f);
        Assert.InRange(Loss(open.Mid, shut.Mid), 10f * MathF.Log10(tau.Y) - 3f, 10f * MathF.Log10(tau.Y) + 3f);
        Assert.InRange(Loss(open.High, shut.High), 10f * MathF.Log10(tau.Z) - 3f, 10f * MathF.Log10(tau.Z) + 3f);
    }

    /// <summary>
    /// A leaf a few centimetres short of its lintel, as the city's glass front doors are (a 2.10 m leaf in
    /// a 2.15 m doorway): the way over its top edge is through the doorway, which the opening's own
    /// transmission charges. Bent over the edge as if the leaf stood alone, it let a PA on the street into
    /// Selby House's lobby at -7/-12/-20 dB against the leaf's -18/-28/-45 (2026-10-09).
    /// </summary>
    [Fact]
    public void TheWayRoundAShutLeafIsThroughItsDoorway()
    {
        var world = Building(frontDoorOpen: false, doorwayHeight: 2.17f);
        var (model, _) = Graph(world);
        var door = model.Openings.Single(o => o.Id == FrontDoor);
        Assert.Null(door.Problem);
        var street = new Vector3(-10f, 0.45f, 2.9f);
        var inside = new Vector3(2.2f, 1.7f, 2.9f);

        model.BarrierPathDifference(street, inside, out _, out bool verified);
        Assert.False(verified, "a way round the shut leaf, through the gap over it, was believed");

        // The straight leg pays the leaf, not the bend over its edge.
        var leg = model.LegGains(street, inside, Array.Empty<int>(), Array.Empty<int>());
        Assert.True(Db(leg.Y) <= 10f * MathF.Log10(door.Tau.Y) + 1f,
                    $"mid {Db(leg.Y):F1} dB through a leaf that passes {10f * MathF.Log10(door.Tau.Y):F1}");
    }

    [Fact]
    public void BehindTwoWallsAndAShutDoorTheTopGoesFirst()
    {
        // No floor under the bands: street to corridor, door shut, falls with frequency on both paths.
        var world = Building(frontDoorOpen: false);
        var p = new SpatialAcoustics().CalculateAcousticPath(world, -1, InCorridor, OnStreet);
        float l = Db(p.EqLow), m = Db(p.EqMid), h = Db(p.EqHigh);
        Assert.True(l > m + 6f && m > h + 6f, $"low {l:F1}, mid {m:F1}, high {h:F1}");
        Assert.True(h < -40f, $"high {h:F1}");
    }

    [Fact]
    public void ADistantSourceBehindTheBuildingDoesNotLeakInFlat()
    {
        // The old portal branch floored every band, and a horn far behind the building came in nearly whole.
        var world = Building(frontDoorOpen: false);
        var horn = new Vector3(60f, 1f, 150f);
        var p = new SpatialAcoustics().CalculateAcousticPath(world, -1, InCorridor, horn);
        float l = Db(p.EqLow), m = Db(p.EqMid), h = Db(p.EqHigh);
        Assert.True(m < -30f && h < m, $"low {l:F1}, mid {m:F1}, high {h:F1}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoutesAreReciprocal(bool open)
    {
        var world = Building(open);
        var there = Ask(world, OnStreet, InCorridor);
        var back = Ask(world, InCorridor, OnStreet);
        Assert.Equal(Db(there.Low), Db(back.Low), 0);
        Assert.Equal(Db(there.Mid), Db(back.Mid), 0);
        Assert.Equal(Db(there.High), Db(back.High), 0);
    }

    [Fact]
    public void TheOneShotPathAndTheSustainedPathAgree()
    {
        // The worker and the one-shots' path ask the same graph (without the simulator the worker uses the
        // one-shots' path itself): one source and one ear get one answer.
        var world = Building(frontDoorOpen: true);
        var acoustics = new SpatialAcoustics();
        using var worker = new AsyncAcousticWorker(acoustics);
        worker.UpdateWorld(world);
        worker.Start();
        List<AcousticPathData>? paths = null;
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            worker.EnqueueRequest(new AcousticRequest { EntityId = 7, ListenerPos = InCorridor, SourcePos = OnStreet, SourceRadius = AudioEmission.MinOcclusionRadius });
            Thread.Sleep(50);
            if (worker.TryGetResult(7, out paths) && paths.Count > 0 && paths[0].SourcePosition == OnStreet) break;
        }
        Assert.NotNull(paths);
        var sustained = paths!.First(p => !p.IsReflection);
        var oneShot = acoustics.CalculateAcousticPath(world, -1, InCorridor, OnStreet);
        Assert.Equal(Db(oneShot.EqLow), Db(sustained.EqLow), 0);
        Assert.Equal(Db(oneShot.EqMid), Db(sustained.EqMid), 0);
        Assert.Equal(Db(oneShot.EqHigh), Db(sustained.EqHigh), 0);
    }

    [Fact]
    public void TheMergeIsPerBandAndTheBearingFollowsTheEnergy()
    {
        var route = new OpeningRoutes.Answer(0.1f, 0.2f, 0.05f, new Vector3(1, 0, 0), 10f, 1, "");
        var g = OpeningRoutes.Better(new Vector3(0.3f, 0.01f, 0.001f), route, out bool wins);
        Assert.Equal(new Vector3(0.3f, 0.2f, 0.05f), g);
        Assert.False(wins);   // the wall's low band carries more than everything by the route
        OpeningRoutes.Better(new Vector3(0.01f, 0.01f, 0.001f), route, out wins);
        Assert.True(wins);
    }

    [Fact]
    public void ARectangularApertureIsTheFresnelKirchhoffResult()
    {
        // Behind a half-plane, on its shadow line: a quarter of the energy (-6 dB), the textbook value.
        Assert.Equal(0.25f, HalfPlaneOnTheBoundary(), 2);
        // A doorway much wider than the Fresnel zone, looked straight through, passes everything but the
        // ripple of its far edges (the Fresnel integrals' own, about 1/(πν)).
        var wide = new OpeningRoutes.Opening { Centre = Vector3.Zero, Normal = Vector3.UnitX, Across = Vector3.UnitZ, Up = Vector3.UnitY,
                                               HalfWidth = 20f, HalfHeight = 20f, HalfDepth = 0.1f };
        var e = OpeningRoutes.Aperture(wide, new Vector3(-5, 0, 0), new Vector3(5, 0, 0));
        Assert.True(e.X > 0.8f && e.Y > 0.9f && e.Z > 0.95f, $"{e}");
        // Round its corner, the top goes first.
        var door = new OpeningRoutes.Opening { Centre = Vector3.Zero, Normal = Vector3.UnitX, Across = Vector3.UnitZ, Up = Vector3.UnitY,
                                               HalfWidth = 0.5f, HalfHeight = 1.05f, HalfDepth = 0.15f };
        var round = OpeningRoutes.Aperture(door, new Vector3(-5, 0, 0), new Vector3(1, 0, 6));
        Assert.True(round.X > round.Y && round.Y > round.Z, $"{round}");
    }

    // ── The street's reverberant field, indoors ─────────────────────────────────────────────────

    /// <summary>In the stairwell, 3 m in from the open front door, the street's field is what a
    /// diffuse field radiates through that opening: S tau / (8 pi r^2) of it, in pressure squared.</summary>
    [Fact]
    public void BesideTheDoorTheStreetsFieldIsWhatTheOpeningRadiates()
    {
        var world = Building(frontDoorOpen: true);
        var (model, _) = Graph(world);
        var door = model.Openings.Single(o => o.Id == FrontDoor);
        var at = door.Centre + new Vector3(3f, 0.6f, 0f);
        float field = model.FieldAt(AcousticConstants.GlobalRegionId, at, Stair, 60f, out var via);
        float r = Vector3.Distance(at, door.Centre);
        // Radiated straight from the doorway, plus what came in builds the stairwell's own field.
        Assert.True(model.TryGetAbsorption(model.NodeOf(Stair), out var a));
        float expected = MathF.Sqrt(door.Area * door.Tau.Y / (8f * MathF.PI * r * r) + door.Area * door.Tau.Y / a.Y);
        Assert.Equal(expected, field, 2);
        Assert.True(Vector3.Distance(via, door.Centre) < 0.01f, "the field should arrive from the doorway");
    }

    /// <summary>"The city does not wash into the lobby through an open door" (2026-10-02). Down the
    /// corridor, two openings in from the street, the old rule gave the street's reverb nothing: it
    /// only looked for an opening joining the listener's room straight to the outdoors.</summary>
    [Fact]
    public void TheStreetsFieldReachesTheCorridorTwoOpeningsIn()
    {
        var world = Building(frontDoorOpen: true);
        var (model, _) = Graph(world);
        float field = model.FieldAt(AcousticConstants.GlobalRegionId, InCorridor, Corridor, 60f, out _);
        // The street's energy into the stairwell's field and on into the corridor's: about 25-35 dB down
        // by the transmission-room equation with these rooms' absorption.
        Assert.True(Db(field) > -45f && Db(field) < -15f, $"{Db(field):F1} dB");
    }

    [Fact]
    public void AShutSteelDoorKeepsTheStreetsFieldOut()
    {
        var open = Graph(Building(frontDoorOpen: true)).Item1;
        var shut = Graph(Building(frontDoorOpen: false)).Item1;
        var at = new Vector3(2.5f, 1.6f, 3f);
        float o = open.FieldAt(AcousticConstants.GlobalRegionId, at, Stair, 60f, out _);
        float c = shut.FieldAt(AcousticConstants.GlobalRegionId, at, Stair, 60f, out _);
        Assert.True(Db(o) - Db(c) > 15f, $"open {Db(o):F1} dB, shut {Db(c):F1} dB");
    }

    private static float HalfPlaneOnTheBoundary()
    {
        OpeningRoutes.Fresnel(0f, out float c0, out float s0);
        OpeningRoutes.Fresnel(1e6f, out float c1, out float s1);
        return 0.5f * ((c1 - c0) * (c1 - c0) + (s1 - s0) * (s1 - s0));
    }
}
