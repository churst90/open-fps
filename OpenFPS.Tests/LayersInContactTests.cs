using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Tests;

/// <summary>
/// Solids in contact are one construction (Constructions, LayeredFaces, WallTransmission.LayeredBandGains):
/// a floor of two 25 cm slabs, a plaster soffit and a carpet is one layered panel, not four barriers in a
/// row; two walls with air between them stay two. Upstairs was 13 to 17 dB too quiet in the low band
/// (footsteps 67 dB down through the city's floor, a 50 cm slab takes 49) and silent above it.
/// </summary>
public class LayersInContactTests
{
    private static float Db(float gain) => -20f * MathF.Log10(MathF.Max(1e-12f, gain));
    private static float Db(Vector3 g, int band) => Db(band == 0 ? g.X : band == 1 ? g.Y : g.Z);

    private static readonly WallBuild Stud = new(0.0125f, 0.6f);
    private static readonly Vector3 Slab = new(20f, 0.25f, 20f);

    private static MaterialProperties P(string m) { AcousticRegistry.Initialize(); return AcousticRegistry.GetProperties(m); }

    private static WorldSnapshot World(params (Vector3 Pos, Vector3 Size, string Material, WallBuild Build)[] boxes)
    {
        var world = new ClientWorldState();
        world.Clear(new Vector3(200, 40, 200));
        int id = 1;
        foreach (var b in boxes)
            world.RegisterDefinition(new EntityDefinition
            {
                EntityId = id++,
                Type = EntityType.StaticObject,
                Transform = new Transform { Position = b.Pos, Rotation = Quaternion.Identity },
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = b.Size, IsSolid = true },
                Material = new MaterialComponent { Material = b.Material },
                Acoustics = new AcousticComponent { LeafMetres = b.Build.LeafMetres, StudSpacingMetres = b.Build.StudSpacingMetres },
            });
        return world.GetSnapshot();
    }

    /// <summary>Under a floor at y 3 and over it, off the vertical.</summary>
    private static readonly Vector3 Below = new(0.3f, 1.6f, 0.2f), Above = new(0.8f, 5.2f, 1.1f);

    private static Vector3 Tracer(WorldSnapshot world, Vector3 a, Vector3 b)
    {
        new SpatialService().GetOcclusionData(world, a, b, out _, out _, out float l, out float m, out float h);
        return new Vector3(l, m, h);
    }

    private static Vector3 Gains((float L, float M, float H) g) => new(g.L, g.M, g.H);

    private static void Near(Vector3 want, Vector3 got, float tolDb, string what)
    {
        for (int band = 0; band < 3; band++)
            Assert.True(MathF.Abs(Db(want, band) - Db(got, band)) <= tolDb,
                        $"{what}, band {band}: want {Db(want, band):F1} dB, got {Db(got, band):F1} dB");
    }

    // ── The model ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OneLayerIsThatLayersOwnFigure()
    {
        foreach (var (m, size, build) in new[] { ("Concrete", Slab, WallBuild.Solid), ("Plaster", new Vector3(16.86f, 2.73f, 0.35f), Stud), ("Carpet", new Vector3(8f, 0.04f, 8f), WallBuild.Solid) })
        {
            WallTransmission.Faces(size, out float t, out float a, out float b);
            var one = WallTransmission.LayeredBandGains(new[] { new WallTransmission.Layer(P(m), t, build) }, a, b);
            Assert.Equal(WallTransmission.BandGains(m, size, build), one);
        }
    }

    [Fact]
    public void TwoSlabsInContactAreOneSlabOfBothThicknesses()
    {
        var two = Gains(WallTransmission.LayeredBandGains(new[] { new WallTransmission.Layer(P("Concrete"), 0.25f, default), new WallTransmission.Layer(P("Concrete"), 0.25f, default) }, 20f, 20f));
        var one = Gains(WallTransmission.BandGains("Concrete", new Vector3(20f, 0.5f, 20f), WallBuild.Solid));
        Near(one, two, 0.05f, "two 25 cm slabs in contact against one 50 cm slab");
    }

    [Fact]
    public void ACarpetOnASlabAddsItsWeightNotABarrier()
    {
        var slab = Gains(WallTransmission.BandGains("Concrete", Slab, WallBuild.Solid));
        var both = Gains(WallTransmission.LayeredBandGains(new[] { new WallTransmission.Layer(P("Concrete"), 0.25f, default), new WallTransmission.Layer(P("Carpet"), 0.04f, default) }, 20f, 20f));
        Near(slab, both, 0.5f, "a 4 cm carpet on a 25 cm slab against the slab");
    }

    [Fact]
    public void NothingAirtightLetsThroughWhatEachLayersHolesDo()
    {
        var c = P("Carpet");
        var two = WallTransmission.LayeredBandGains(new[] { new WallTransmission.Layer(c, 0.04f, default), new WallTransmission.Layer(c, 0.04f, default) }, 8f, 8f);
        Assert.Equal(c.TransmissionLow * c.TransmissionLow, two.Low, 5);
        Assert.Equal(c.TransmissionHigh * c.TransmissionHigh, two.High, 5);
    }

    // ── The hand-rolled tracer ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTracerHearsTwoSlabsInContactAsOne()
    {
        var world = World((new Vector3(0, 3.125f, 0), Slab, "Concrete", WallBuild.Solid), (new Vector3(0, 3.375f, 0), Slab, "Concrete", WallBuild.Solid));
        Near(Gains(WallTransmission.BandGains("Concrete", new Vector3(20f, 0.5f, 20f), WallBuild.Solid)), Tracer(world, Below, Above), 0.1f,
             "two 25 cm slabs in contact, traced");
    }

    [Fact]
    public void TheTracerHearsTwoSlabsApartAsTwo()
    {
        var world = World((new Vector3(0, 2.625f, 0), Slab, "Concrete", WallBuild.Solid), (new Vector3(0, 3.875f, 0), Slab, "Concrete", WallBuild.Solid));
        var one = Gains(WallTransmission.BandGains("Concrete", Slab, WallBuild.Solid));
        Near(one * one, Tracer(world, Below, Above), 0.1f, "two 25 cm slabs 1 m apart, traced");
    }

    [Fact]
    public void TheTracerHearsTheCityFloorAsOneLayeredPanel()
    {
        // Selby House's floor: a 3 cm plaster soffit, the ceiling slab, the next storey's floor slab, carpet.
        var world = World((new Vector3(0, 2.985f, 0), new Vector3(20f, 0.03f, 20f), "Plaster", Stud),
                          (new Vector3(0, 3.125f, 0), Slab, "Concrete", WallBuild.Solid),
                          (new Vector3(0, 3.375f, 0), Slab, "Concrete", WallBuild.Solid),
                          (new Vector3(0, 3.52f, 0), new Vector3(8f, 0.04f, 8f), "Carpet", WallBuild.Solid));
        var want = Gains(WallTransmission.LayeredBandGains(new[]
        {
            new WallTransmission.Layer(P("Plaster"), 0.03f, Stud), new WallTransmission.Layer(P("Concrete"), 0.25f, default),
            new WallTransmission.Layer(P("Concrete"), 0.25f, default), new WallTransmission.Layer(P("Carpet"), 0.04f, default),
        }, 20f, 20f));
        var got = Tracer(world, Below, Above);
        Near(want, got, 0.1f, "the city floor, traced");
        Assert.True(Db(got.X) < 55f, $"low band {Db(got.X):F1} dB through the floor");
    }

    [Fact]
    public void TwoStudWallsWithACorridorBetweenThemStayTwo()
    {
        var wall = new Vector3(0.35f, 2.73f, 16f);
        var world = World((new Vector3(-1.275f, 1.4f, 0), wall, "Plaster", Stud), (new Vector3(1.275f, 1.4f, 0), wall, "Plaster", Stud));
        var one = Gains(WallTransmission.BandGains("Plaster", wall, Stud));
        Near(one * one, Tracer(world, new Vector3(-4f, 1.6f, 0.3f), new Vector3(4f, 1.5f, -0.2f)), 0.1f, "two stud walls 2.2 m apart");
    }

    // ── The faces Steam Audio is given ─────────────────────────────────────────────────────────

    private static LayeredFaces.Plan Plan(params LayeredFaces.Solid[] solids) { AcousticRegistry.Initialize(); return LayeredFaces.Make(solids); }

    [Fact]
    public void SlabsInContactShowOnlyTheirOuterFacesWithTheWholeFloorsFigure()
    {
        var q = Quaternion.Identity;
        var plan = Plan(new LayeredFaces.Solid(new Vector3(0, 3.125f, 0), Slab, q, "Concrete"),
                        new LayeredFaces.Solid(new Vector3(0, 3.375f, 0), Slab, q, "Concrete"));
        Assert.Equal(1, plan.Constructions);
        var lower = Assert.Single(plan.Members[0].Faces);
        var upper = Assert.Single(plan.Members[1].Faces);
        Assert.True(lower.Outward.Y < -0.99f && MathF.Abs(lower.A.Y - 3.0f) < 1e-4f, "the lower slab shows its underside");
        Assert.True(upper.Outward.Y > 0.99f && MathF.Abs(upper.A.Y - 3.5f) < 1e-4f, "the upper slab shows its top");
        var want = Gains(WallTransmission.BandGains("Concrete", new Vector3(20f, 0.5f, 20f), WallBuild.Solid));
        Near(want, lower.Gains, 0.05f, "underside");
        Near(want, upper.Gains, 0.05f, "top");
    }

    [Fact]
    public void SolidsApartAreNoConstruction()
    {
        var q = Quaternion.Identity;
        var plan = Plan(new LayeredFaces.Solid(new Vector3(0, 2.625f, 0), Slab, q, "Concrete"),
                        new LayeredFaces.Solid(new Vector3(0, 3.875f, 0), Slab, q, "Concrete"),
                        new LayeredFaces.Solid(new Vector3(-1.275f, 1.4f, 0), new Vector3(0.35f, 2.73f, 16f), q, "Plaster", Stud),
                        new LayeredFaces.Solid(new Vector3(1.275f, 1.4f, 0), new Vector3(0.35f, 2.73f, 16f), q, "Plaster", Stud));
        Assert.Equal(0, plan.Constructions);
        Assert.Empty(plan.Members);
    }

    [Fact]
    public void ACarpetOnPartOfASlabCutsTheSlabsTopRoundIt()
    {
        var q = Quaternion.Identity;
        var plan = Plan(new LayeredFaces.Solid(new Vector3(0, 3.125f, 0), Slab, q, "Concrete"),
                        new LayeredFaces.Solid(new Vector3(0, 3.27f, 0), new Vector3(8f, 0.04f, 8f), q, "Carpet"));
        var slab = plan.Members[0].Faces;
        var carpet = plan.Members[1].Faces;
        // The carpet shows its top only; the slab its whole underside and its top round the carpet.
        var top = Assert.Single(carpet);
        Assert.True(top.Outward.Y > 0.99f);
        float Area(LayeredFaces.Face f) => Vector3.Cross(f.B - f.A, f.D - f.A).Length();
        float slabTop = slab.Where(f => f.Outward.Y > 0.99f).Sum(Area), slabUnder = slab.Where(f => f.Outward.Y < -0.99f).Sum(Area);
        Assert.Equal(400f - 64f, slabTop, 1);
        Assert.Equal(400f, slabUnder, 1);
        // Under the carpet the underside carries the slab and carpet, elsewhere the slab alone; the carpet's
        // top carries the same as the underside beneath it.
        var alone = Gains(WallTransmission.BandGains("Concrete", Slab, WallBuild.Solid));
        var under = slab.Single(f => f.Outward.Y < -0.99f && MathF.Abs(0.5f * (f.A.X + f.C.X)) < 1e-3f && MathF.Abs(0.5f * (f.A.Z + f.C.Z)) < 1e-3f);
        Assert.Equal(top.Gains, under.Gains);
        Assert.NotEqual(alone, under.Gains);
        Assert.All(slab.Where(f => f.Outward.Y > 0.99f), f => Assert.Equal(alone, f.Gains));
    }

    [Fact]
    public void ABedOnACarpetIsABlockNotALayer()
    {
        var q = Quaternion.Identity;
        var plan = Plan(new LayeredFaces.Solid(new Vector3(0, 3.125f, 0), Slab, q, "Concrete"),
                        new LayeredFaces.Solid(new Vector3(0, 3.55f, 0), new Vector3(1.8f, 0.6f, 1.8f), q, "Audience"));
        Assert.Empty(plan.Members);
    }

    [Fact]
    public void ADoorLeafLappingItsJambsIsNotALayerOfTheWall()
    {
        var q = Quaternion.Identity;
        var wall = new LayeredFaces.Solid(new Vector3(0, 1.4f, 0), new Vector3(0.35f, 2.73f, 8f), q, "Brick");
        var leaf = new LayeredFaces.Solid(new Vector3(0, 1.05f, 0), new Vector3(0.05f, 2.1f, 1.1f), q, "Wood", Bonds: false);
        Assert.Empty(Plan(wall, leaf).Members);
        var boxes = new List<SteamAudioScene.Box>
        {
            new(wall.Center, wall.Size, q, "Brick"),
            new(leaf.Center, leaf.Size, q, "Wood", EntityId: 7, Hung: true),
        };
        Assert.Empty(SteamAudioScene.PlanOf(boxes).Members);
        Assert.Empty(SteamAudioScene.PlanOf(new List<SteamAudioScene.Box> { boxes[0], boxes[1] with { Hung = false } }, new HashSet<int> { 7 }).Members);
    }

    [Fact]
    public void BoxesFromWorldMarksDoorLeavesAsHung()
    {
        var world = new ClientWorldState();
        world.Clear(new Vector3(200, 40, 200));
        world.RegisterDefinition(new EntityDefinition
        {
            EntityId = 5, Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0, 1.05f, 0), Rotation = Quaternion.Identity },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.05f, 2.1f, 1f), IsSolid = true },
            Material = new MaterialComponent { Material = "Wood" },
            Portal = new PortalComponent { RegionAId = 1, RegionBId = 2 },
        });
        Assert.True(Assert.Single(SteamAudioScene.BoxesFromWorld(world.GetSnapshot())).Hung);
    }

    // ── Steam Audio itself (opt-in: see SteamAudioForTests) ────────────────────────────────────

    private static SteamAudioSimulator.DirectResult TraceWhole(List<SteamAudioScene.Box> boxes)
    {
        IntPtr ctx = SteamAudioForTests.EmbreeContext;
        using var scene = new SteamAudioScene(ctx);
        scene.Build(boxes);
        using var sim = new SteamAudioSimulator(ctx, maxSources: 2);
        sim.SetScene(scene);
        var s = sim.AcquireSource();
        sim.SetListener(Below);
        sim.SetSourceInputs(s, Above, 0.01f);
        sim.Run();
        return sim.GetResult(s);
    }

    [SteamAudioEmbreeFact]
    public void SteamAudioMeetsTheCityFloorAsOnePanel()
    {
        AcousticRegistry.Initialize();
        var q = Quaternion.Identity;
        var floor = new List<SteamAudioScene.Box>
        {
            new(new Vector3(0, 2.985f, 0), new Vector3(20f, 0.03f, 20f), q, "Plaster", Stud),
            new(new Vector3(0, 3.125f, 0), Slab, q, "Concrete"), new(new Vector3(0, 3.375f, 0), Slab, q, "Concrete"),
            new(new Vector3(0, 3.52f, 0), new Vector3(8f, 0.04f, 8f), q, "Carpet"),
        };
        var r = TraceWhole(floor);
        var want = Gains(WallTransmission.LayeredBandGains(new[]
        {
            new WallTransmission.Layer(P("Plaster"), 0.03f, Stud), new WallTransmission.Layer(P("Concrete"), 0.25f, default),
            new WallTransmission.Layer(P("Concrete"), 0.25f, default), new WallTransmission.Layer(P("Carpet"), 0.04f, default),
        }, 20f, 20f));
        Near(want, new Vector3(r.TransLow, r.TransMid, r.TransHigh), 0.5f, "Steam Audio through the city floor");
    }

    [SteamAudioEmbreeFact]
    public void SteamAudioStillPaysTwoSlabsApartAsTwo()
    {
        AcousticRegistry.Initialize();
        var q = Quaternion.Identity;
        var r = TraceWhole(new List<SteamAudioScene.Box> { new(new Vector3(0, 2.625f, 0), Slab, q, "Concrete"), new(new Vector3(0, 3.875f, 0), Slab, q, "Concrete") });
        // Steam Audio counts n boxes in a row as (2n + 1)/3 of one (SteamAudioScene.MaterialIndex): 5/3.
        var one = Gains(WallTransmission.BandGains("Concrete", Slab, WallBuild.Solid));
        Assert.True(MathF.Abs(Db(one.X) * 5f / 3f - Db(r.TransLow)) < 0.5f, $"two slabs apart: {Db(r.TransLow):F1} dB, 5/3 of one is {Db(one.X) * 5f / 3f:F1}");
    }

    [SteamAudioEmbreeFact]
    public void TileScenesShowTheSameConstructionsAsTheWholeScene()
    {
        IntPtr ctx = SteamAudioForTests.EmbreeContext;
        AcousticRegistry.Initialize();
        var q = Quaternion.Identity;
        var boxes = new List<SteamAudioScene.Box>
        {
            new(new Vector3(0, 2.985f, 0), new Vector3(20f, 0.03f, 20f), q, "Plaster", Stud),
            new(new Vector3(0, 3.125f, 0), Slab, q, "Concrete"), new(new Vector3(0, 3.375f, 0), Slab, q, "Concrete"),
            new(new Vector3(0, 3.52f, 0), new Vector3(8f, 0.04f, 8f), q, "Carpet"),
        };
        var set = new TileSceneSet(ctx, 8f);
        var sim = new SteamAudioSimulator(ctx, maxSources: 2);
        try
        {
            set.Update(boxes);
            var (full, _) = set.Assemble();
            sim.SetScene(full);
            var s = sim.AcquireSource();
            sim.SetListener(Below);
            sim.SetSourceInputs(s, Above, 0.01f);
            sim.Run();
            var tiles = sim.GetResult(s);
            var whole = TraceWhole(boxes);
            Assert.True(MathF.Abs(Db(whole.TransLow) - Db(tiles.TransLow)) < 0.5f, $"low: tiles {Db(tiles.TransLow):F1}, whole {Db(whole.TransLow):F1}");
            Assert.True(MathF.Abs(Db(whole.TransMid) - Db(tiles.TransMid)) < 0.5f, $"mid: tiles {Db(tiles.TransMid):F1}, whole {Db(whole.TransMid):F1}");
        }
        finally { sim.Dispose(); set.Dispose(); }
    }
}
