using System.Numerics;
using Arch.Core;
using OpenFPS.Acoustics;
using OpenFPS.Audio.Acoustics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Geometry;
using OpenFPS.Materials;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A shut door's gaps (Resonance's 1.1, inbox/2026-10-17-resonance-audio-open-fps-lacks): a shut leaf passed only
/// what its weight let through, a hollow-core door 35 dB down at 1.25 kHz, where a hung interior door passes 15 to 20
/// by its gaps. The gaps are each door's data (PortalComponent, the prefab's DoorGapMetres and DoorUndercutMetres),
/// with defaults for where it hangs, and what they pass is Gomperts' slit (DoorGaps).
/// </summary>
public class DoorGapsTests
{
    private readonly ITestOutputHelper _o;
    public DoorGapsTests(ITestOutputHelper o) => _o = o;

    private static float DbE(float e) => 10f * MathF.Log10(MathF.Max(1e-12f, e));
    private static string Bands(Vector3 e) => $"{DbE(e.X):F1}/{DbE(e.Y):F1}/{DbE(e.Z):F1}";

    // ── The slit ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A door between two rooms, 0.9 by 2.1 m in a 40 mm leaf, with the defaults: 3 mm round and a 10 mm
    /// undercut pass 15 to 20 dB in the middle band and no band passes more than a door opened a crack.</summary>
    [Fact]
    public void AnInteriorDoorsGapsPassFifteenToTwentyDecibels()
    {
        var leak = DoorGaps.For(0f, 0f, toOutdoors: false).Leak(0.9f, 2.1f, 0.04f);
        _o.WriteLine($"interior gaps pass {Bands(leak)} dB");
        Assert.InRange(DbE(leak.Y), -20f, -15f);
        Assert.InRange(DbE(leak.X), -20f, -12f);
        Assert.InRange(DbE(leak.Z), -20f, -12f);
    }

    /// <summary>A door to the outdoors, weatherstripped and swept, 0.3 mm all round: 30 dB and more in every band.</summary>
    [Fact]
    public void AnOutsideDoorsSealsPassThirtyDecibelsAndMore()
    {
        var leak = DoorGaps.For(0f, 0f, toOutdoors: true).Leak(0.9f, 2.1f, 0.04f);
        _o.WriteLine($"outdoor seals pass {Bands(leak)} dB");
        Assert.True(DbE(leak.X) < -30f && DbE(leak.Y) < -30f && DbE(leak.Z) < -30f, Bands(leak));
    }

    /// <summary>A door's own gaps are kept; zero takes the default for where it hangs, each gap on its own.</summary>
    [Fact]
    public void ADoorsOwnGapsAreKeptAndZeroIsTheDefault()
    {
        Assert.Equal(new DoorGaps(0.006f, 0.002f), DoorGaps.For(0.006f, 0.002f, toOutdoors: false));
        Assert.Equal(new DoorGaps(0.006f, 0.002f), DoorGaps.For(0.006f, 0.002f, toOutdoors: true));
        Assert.Equal(new DoorGaps(DoorGaps.InteriorRoundMetres, DoorGaps.InteriorUnderMetres), DoorGaps.For(0f, 0f, toOutdoors: false));
        Assert.Equal(new DoorGaps(DoorGaps.OutdoorMetres, DoorGaps.OutdoorMetres), DoorGaps.For(0f, 0f, toOutdoors: true));
        Assert.Equal(new DoorGaps(DoorGaps.OutdoorMetres, 0.02f), DoorGaps.For(0f, 0.02f, toOutdoors: true));
    }

    /// <summary>The undercut is the slit twice as wide with the floor for its mirror.</summary>
    [Fact]
    public void AnUndercutIsASlitTwiceAsWideMirroredInTheFloor()
    {
        Assert.Equal(DoorGaps.SlitTransmission(0.02f, 0.04f, atFloor: false), DoorGaps.SlitTransmission(0.01f, 0.04f, atFloor: true));
        Assert.NotEqual(DoorGaps.SlitTransmission(0.01f, 0.04f, atFloor: false), DoorGaps.SlitTransmission(0.01f, 0.04f, atFloor: true));
    }

    /// <summary>
    /// Gomperts' slit, written out here on its own at a 10 mm gap and 1 kHz, where the walls' friction is under a per
    /// cent: twice τ = 4 Re z / |2 z cos kd + j (1 + z²) sin kd|², z = kw/2 + j kw (ln(8/kw) - γ)/π.
    /// </summary>
    [Fact]
    public void AWideGapIsGompertsSlit()
    {
        const double w = 0.01, d = 0.04, hz = 1000;
        double k = 2 * Math.PI * hz / AcousticConstants.SpeedOfSound, kw = k * w;
        var z = new System.Numerics.Complex(kw / 2, kw * (Math.Log(8 / kw) - 0.5772156649) / Math.PI);
        var drive = 2 * z * Math.Cos(k * d) + System.Numerics.Complex.ImaginaryOne * (1 + z * z) * Math.Sin(k * d);
        double gomperts = 2 * 4 * z.Real / (drive.Magnitude * drive.Magnitude);
        double model = DoorGaps.Diffuse(hz, w, d);
        _o.WriteLine($"10 mm at 1 kHz: Gomperts {gomperts:F4}, the model {model:F4}");
        Assert.InRange(model / gomperts, 0.95, 1.05);
    }

    /// <summary>
    /// A 0.1 mm gap at 100 Hz is Poiseuille's flow between plates: a resistance 12 η d / w³ a metre of it against the
    /// sound, and six fifths of the air's mass in it. The slit's air must come to that by itself.
    /// </summary>
    [Fact]
    public void AHairlineGapIsHeldBackByItsWalls()
    {
        const double w = 1e-4, d = 0.04, hz = 100;
        var air = Air.Reference;
        double rhoC = air.Density * AcousticConstants.SpeedOfSound;
        double k = 2 * Math.PI * hz / AcousticConstants.SpeedOfSound, kw = k * w;
        double e = (Math.Log(8 / kw) - 0.5772156649) / Math.PI;
        double resistance = 12 * air.Viscosity * d / (w * w * rhoC);           // against ρc/w
        double poiseuille = 2 * 4 * (kw / 2) / (Math.Pow(kw + resistance, 2) + Math.Pow(1.2 * k * d + 2 * kw * e, 2));
        double model = DoorGaps.Diffuse(hz, w, d);
        _o.WriteLine($"0.1 mm at 100 Hz: Poiseuille {poiseuille:E3}, the model {model:E3}, lossless {2 * 4 * (kw / 2) / (kw * kw + Math.Pow(k * d + 2 * kw * e, 2)):E3}");
        Assert.InRange(model / poiseuille, 0.8, 1.25);
    }

    // ── A door between two rooms ─────────────────────────────────────────────────────────────

    private const int RoomA = 711, RoomB = 712, Door = 811;
    private static readonly Vector3 LeafSize = new(0.9f, 2.1f, 0.04f);
    private static readonly Quaternion Across = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

    /// <summary>Two rooms, A (x &lt; 0) and B (x &gt; 0.11), either side of a brick wall with a 0.9 m doorway at z 2.05
    /// to 2.95 and a hollow-core leaf in it, shut or swung into A; or room A and the outdoors (<paramref name="outdoors"/>,
    /// no room B and no roof over where it was); or room A and a roof terrace (<paramref name="terrace"/>: B walled
    /// round but open to the sky, a place with surfaces that is not indoors). <paramref name="gap"/> and
    /// <paramref name="under"/> are the door's own, zero for the default.</summary>
    private static WorldSnapshot Rooms(bool open, bool outdoors = false, float gap = 0f, float under = 0f, bool terrace = false)
    {
        AcousticRegistry.EnsureInitialized();
        const float t = 0.11f;
        var defs = new List<EntityDefinition>();
        int id = 1;
        void Box(float x0, float x1, float y0, float y1, float z0, float z1, string material)
            => defs.Add(Solid(id++, new Vector3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2),
                              new Vector3(x1 - x0, y1 - y0, z1 - z0), Quaternion.Identity, material, WallBuild.Solid));
        float xb = outdoors ? t : t + 4f;
        Box(-30, 30, -0.2f, 0, -30, 30, "Concrete");                 // the ground
        Box(-4.3f, terrace ? t : xb, 2.7f, 2.9f, -0.3f, 6.3f, "Concrete");   // the roof, over room A alone by a terrace
        Box(-4.3f, -4f, 0, 2.7f, -0.3f, 6.3f, "Brick");               // the outer walls
        Box(-4.3f, xb, 0, 2.7f, -0.3f, 0, "Brick");
        Box(-4.3f, xb, 0, 2.7f, 6f, 6.3f, "Brick");
        if (!outdoors) Box(xb, xb + 0.3f, 0, 2.7f, -0.3f, 6.3f, "Brick");
        Box(0, t, 0, 2.7f, 0, 2.05f, "Brick");                         // the wall between, round its doorway
        Box(0, t, 0, 2.7f, 2.95f, 6, "Brick");
        Box(0, t, 2.1f, 2.7f, 2.05f, 2.95f, "Brick");

        var shut = new Vector3(t / 2, 1.05f, 2.5f);
        var at = shut;
        var rot = Across;
        if (open)
        {
            Vector3 hinge = shut + Vector3.Transform(new Vector3(LeafSize.X / 2, 0, 0), Across);
            rot = Across * Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2f);
            at = hinge + Vector3.Transform(new Vector3(-LeafSize.X / 2, 0, 0), rot);
        }
        var leaf = Solid(Door, at, LeafSize, rot, "Wood", new WallBuild(0.004f, 0f));
        leaf.Portal = new PortalComponent
        {
            RegionAId = RoomA, RegionBId = outdoors ? AcousticConstants.GlobalRegionId : RoomB,
            OpeningCentre = shut, OpeningRotation = Across, GapMetres = gap, UndercutMetres = under,
        };
        defs.Add(leaf);

        int brick = AcousticRegistry.GetProperties("Brick").ResonanceIndex, concrete = AcousticRegistry.GetProperties("Concrete").ResonanceIndex;
        defs.Add(Region(RoomA, new Vector3(-2f, 1.35f, 3f), new Vector3(4f, 2.7f, 6f), new[] { concrete, concrete, brick, brick, brick, brick }));
        if (!outdoors)
            defs.Add(Region(RoomB, new Vector3(t + 2f, 1.35f, 3f), new Vector3(4f, 2.7f, 6f),
                            new[] { concrete, terrace ? RoomAcoustics.OpenFaceMaterial : concrete, brick, brick, brick, brick }, indoor: !terrace));

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

    private static EntityDefinition Region(int id, Vector3 at, Vector3 size, int[] materials, bool indoor = true) => new()
    {
        EntityId = id,
        Transform = new Transform { Position = at, Rotation = Quaternion.Identity },
        Region = new RegionComponent { FriendlyName = $"r{id}", RoomSize = size, Materials = materials, IsIndoor = indoor },
    };

    private static OpeningRoutes.Opening DoorOf(WorldSnapshot world, out OpeningRoutes model, out SpatialAcoustics acoustics)
    {
        acoustics = new SpatialAcoustics();
        model = acoustics.RoutesFor(world)!;
        return model.Openings.Single(o => o.Id == Door);
    }

    /// <summary>
    /// The shut hollow-core door between two rooms passes 14 to 20 dB in the middle band, more by its gaps than by its
    /// leaf in the middle and high bands (the leaf alone -23/-37 dB); and a sound a metre from it on one side is heard a metre from it on the other by
    /// the route through it at that.
    /// </summary>
    [Fact]
    public void AShutDoorBetweenRoomsPassesItsGaps()
    {
        var world = Rooms(open: false);
        var door = DoorOf(world, out var model, out var acoustics);
        var leafAlone = door.Tau - door.Gaps;
        _o.WriteLine($"shut: passes {Bands(door.Tau)} dB, its gaps {Bands(door.Gaps)}, its leaf {Bands(leafAlone)}; covered {door.LeafCover:F2}");
        Assert.Equal(1f, door.LeafCover);
        Assert.True(door.Gaps.Y > leafAlone.Y && door.Gaps.Z > leafAlone.Z, $"gaps {Bands(door.Gaps)}, the leaf alone {Bands(leafAlone)}");
        Assert.InRange(DbE(door.Tau.Y), -20f, -14f);
        Assert.InRange(DbE(door.Gaps.Y), -20f, -15f);

        var src = new Vector3(-1f, 1.2f, 2.5f);
        var lis = new Vector3(1.11f, 1.2f, 2.5f);
        Assert.True(model.Route(src, acoustics.GetRegionAt(world, src), lis, acoustics.GetRegionAt(world, lis), out var a), "no route");
        float mid = 20f * MathF.Log10(a.Mid);
        _o.WriteLine($"a metre either side: the route {20f * MathF.Log10(a.Low):F1}/{mid:F1}/{20f * MathF.Log10(a.High):F1} dB via {a.Via}");
        Assert.InRange(mid, -22f, -10f);
    }

    /// <summary>A shut door to the outdoors keeps its weatherstripped 0.3 mm: its gaps pass less than -30 dB.</summary>
    [Fact]
    public void AShutDoorToTheOutdoorsIsWeatherstripped()
    {
        var door = DoorOf(Rooms(open: false, outdoors: true), out _, out _);
        _o.WriteLine($"to the outdoors: passes {Bands(door.Tau)} dB, its gaps {Bands(door.Gaps)}");
        Assert.True(door.NodeA == OpeningRoutes.Outside || door.NodeB == OpeningRoutes.Outside);
        Assert.True(door.Gaps.Y > 0f, "no gaps round an outside door");
        Assert.True(DbE(door.Gaps.X) < -30f && DbE(door.Gaps.Y) < -30f && DbE(door.Gaps.Z) < -30f, Bands(door.Gaps));
    }

    /// <summary>
    /// A door from a room to a place in the open air that has surfaces of its own (a roof terrace, the city's roofs: a
    /// node of the graph, not the outdoors) is hung against the weather all the same: weatherstripped, its gaps the
    /// outdoor door's to within a decibel and under -30 dB. Until 2026-10-17 such a door took a room door's 3 mm and
    /// 10 mm undercut (-17 dB in the middle band through the city's roof access doors).
    /// </summary>
    [Fact]
    public void AShutDoorToAPlaceInTheOpenAirIsWeatherstripped()
    {
        var door = DoorOf(Rooms(open: false, terrace: true), out var model, out _);
        var outdoors = DoorOf(Rooms(open: false, outdoors: true), out _, out _);
        _o.WriteLine($"to a terrace: passes {Bands(door.Tau)} dB, its gaps {Bands(door.Gaps)}; to the outdoors, its gaps {Bands(outdoors.Gaps)}");
        Assert.True(door.NodeA != OpeningRoutes.Outside && door.NodeB != OpeningRoutes.Outside, "the terrace is a place of its own");
        Assert.True(model.IsOpenAir(RoomB) && !model.IsOpenAir(RoomA), "the terrace is open air, the room is not");
        Assert.True(door.Gaps.Y > 0f, "no gaps round the terrace door");
        Assert.True(DbE(door.Gaps.X) < -30f && DbE(door.Gaps.Y) < -30f && DbE(door.Gaps.Z) < -30f, Bands(door.Gaps));
        Assert.InRange(DbE(door.Gaps.Y) - DbE(outdoors.Gaps.Y), -1f, 1f);
    }

    /// <summary>A door's own gaps win over the open air's default: the terrace door declared 3 mm round and under
    /// leaks by those, as the city's stair doors do.</summary>
    [Fact]
    public void ADoorsOwnGapsWinOverTheOpenAir()
    {
        var declared = DoorOf(Rooms(open: false, terrace: true, gap: 0.003f, under: 0.003f), out _, out _);
        var byDefault = DoorOf(Rooms(open: false, terrace: true), out _, out _);
        var expected = new DoorGaps(0.003f, 0.003f).Leak(2f * declared.HalfWidth, 2f * declared.HalfHeight, LeafSize.Z) * declared.LeafCover;
        _o.WriteLine($"terrace door: declared 3 mm {Bands(declared.Gaps)}, by default {Bands(byDefault.Gaps)}, 3 mm reckoned on its own {Bands(expected)} dB");
        Assert.True(DbE(declared.Gaps.Y) > DbE(byDefault.Gaps.Y) + 10f, $"declared {Bands(declared.Gaps)} against {Bands(byDefault.Gaps)}");
        Assert.InRange(DbE(declared.Gaps.Y) - DbE(expected.Y), -0.1f, 0.1f);
    }

    /// <summary>A door swung open has no gaps to add: its doorway is open.</summary>
    [Fact]
    public void AnOpenDoorAddsNoGaps()
    {
        var door = DoorOf(Rooms(open: true), out _, out _);
        _o.WriteLine($"open: passes {Bands(door.Tau)} dB, covered {door.LeafCover:F2}, gaps {Bands(door.Gaps)}");
        Assert.Equal(0f, door.LeafCover);
        Assert.Equal(Vector3.Zero, door.Gaps);
    }

    /// <summary>A door's own gaps, from its portal, are what it leaks by: sealed to a tenth of a millimetre it passes
    /// 15 dB and more less than the default; a lift door's 6 mm, more.</summary>
    [Fact]
    public void ADoorLeaksByItsOwnGaps()
    {
        var byDefault = DoorOf(Rooms(open: false), out _, out _).Gaps;
        var sealedDoor = DoorOf(Rooms(open: false, gap: 0.0001f, under: 0.0001f), out _, out _).Gaps;
        var lift = DoorOf(Rooms(open: false, gap: 0.006f, under: 0.006f), out _, out _).Gaps;
        _o.WriteLine($"gaps: default {Bands(byDefault)}, sealed {Bands(sealedDoor)}, 6 mm {Bands(lift)} dB");
        Assert.True(DbE(sealedDoor.Y) < DbE(byDefault.Y) - 15f, $"sealed {Bands(sealedDoor)} against {Bands(byDefault)}");
        Assert.True(lift.Y > byDefault.Y, $"6 mm {Bands(lift)} against {Bands(byDefault)}");
    }

    /// <summary>The lift door's prefab declares the 6 mm EN 81-20 allows a landing door round its frame and sill; a
    /// plain door declares none and takes the default where it hangs.</summary>
    [Fact]
    public void PrefabsCarryTheirDoorsGaps()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        using var world = World.Create();
        var lift = world.Get<PortalComponent>(prefabs.Spawn(world, "elevator_door", Vector3.Zero));
        Assert.Equal(0.006f, lift.GapMetres);
        Assert.Equal(0.006f, lift.UndercutMetres);
        var plain = world.Get<PortalComponent>(prefabs.Spawn(world, "door", new Vector3(3f, 0f, 0f)));
        Assert.Equal(0f, plain.GapMetres);
        Assert.Equal(0f, plain.UndercutMetres);
    }

    /// <summary>
    /// A map sets a door's gaps where it places it, over its prefab's: the city's stair doors are fire doors with smoke
    /// seals, 3 mm round and 3 mm under (gen_city's FIRE_DOOR_GAPS), and the same steel door at a roof access, the
    /// terminal's front entrance and the hangar's back door keeps the default, weatherstripped where it opens to the
    /// open air (the outdoors, or a roof).
    /// </summary>
    [Fact]
    public void TheCitysStairDoorsAreSealedAgainstSmoke()
    {
        AcousticRegistry.Initialize();
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new OpenFPS.Server.Core.MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var ecs, out _, out _, out _));
        int stairs = 0, others = 0;
        ecs.Query(new QueryDescription().WithAll<PortalComponent, DoorComponent, IdentityComponent>(),
            (ref PortalComponent p, ref DoorComponent _, ref IdentityComponent who) =>
            {
                if (who.Name.Contains("stairwell door", StringComparison.Ordinal))
                {
                    stairs++;
                    Assert.True(p.GapMetres == 0.003f && p.UndercutMetres == 0.003f, $"{who.Name}: {p.GapMetres} round, {p.UndercutMetres} under");
                }
                else if (who.Name.Contains("roof access door", StringComparison.Ordinal) || who.Name is "Terminal front entrance" or "Hangar back door")
                {
                    others++;
                    Assert.True(p.GapMetres == 0f && p.UndercutMetres == 0f, $"{who.Name}: {p.GapMetres} round, {p.UndercutMetres} under");
                }
            });
        _o.WriteLine($"{stairs} stair doors sealed, {others} other steel doors at the default");
        Assert.Equal(37, stairs);
        Assert.Equal(7, others);
    }
}
