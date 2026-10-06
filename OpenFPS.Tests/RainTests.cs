using System;
using System.Linq;
using System.Numerics;
using MemoryPack;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Rain: the rate the weather becomes, the drops that rate is made of, what a drop does on each kind
/// of surface, where the survey finds the surfaces, and the voices that render them. The lab
/// (--rain levels / physics) measured the model against published figures and recordings; these hold
/// what must not drift once it was.
/// </summary>
public class RainTests
{
    private readonly ITestOutputHelper _o;
    private const int Rate = 48000;

    public RainTests(ITestOutputHelper o)
    {
        _o = o;
        AcousticRegistry.Initialize();
    }

    // ── The rate ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRateRisesWithTheIntensityFromNothing()
    {
        Assert.Equal(0f, Rainfall.RateFromIntensity(0f));
        Assert.Equal(0f, Rainfall.RateFromIntensity(-1f));
        Assert.Equal(Rainfall.DrizzleRate, Rainfall.RateFromIntensity(Rainfall.DrizzleIntensity), 3);
        Assert.Equal(Rainfall.FullIntensityRate, Rainfall.RateFromIntensity(1f), 2);
        Assert.Equal(Rainfall.FullIntensityRate, Rainfall.RateFromIntensity(3f), 2);
        float last = 0f;
        for (float i = 0.01f; i <= 1f; i += 0.01f)
        {
            float r = Rainfall.RateFromIntensity(i);
            Assert.True(r > last, $"rate at {i:F2} is {r}, not above {last}");
            last = r;
        }
    }

    [Fact]
    public void TheFrontsAreTheClassesTheyShouldBe()
    {
        // The Rain front's target intensity is 0.6 and the Storm's 1.0 (WorldEnvironmentSystem).
        Assert.Equal(RainCategory.Moderate, Rainfall.Category(Rainfall.RateFromIntensity(0.6f)));
        Assert.Equal(RainCategory.Violent, Rainfall.Category(Rainfall.RateFromIntensity(1.0f)));
        Assert.Equal(RainCategory.Light, Rainfall.Category(Rainfall.RateFromIntensity(0.3f)));
        Assert.Equal(RainCategory.Heavy, Rainfall.Category(Rainfall.RateFromIntensity(0.8f)));
        Assert.Equal(RainCategory.None, Rainfall.Category(0f));
        Assert.Equal(RainCategory.Light, Rainfall.Category(2.4f));
        Assert.Equal(RainCategory.Moderate, Rainfall.Category(2.5f));
        Assert.Equal(RainCategory.Heavy, Rainfall.Category(10f));
        Assert.Equal(RainCategory.Violent, Rainfall.Category(50f));
    }

    [Fact]
    public void SnowMakesNoRain()
    {
        Assert.Equal(0f, Rainfall.RateFor(0.6f, -3f));
        Assert.Equal(0f, Rainfall.RateFor(0.6f, Rainfall.SnowBelowCelsius - 0.1f));
        Assert.True(Rainfall.RateFor(0.6f, 12f) > 2.5f);
    }

    [Fact]
    public void TheServerSendsTheRateOfItsWeather()
    {
        var env = new WorldEnvironmentSystem(new Random(3)) { FrontProbabilityPerTick = 0 };
        env.SetDate(12f, 172);
        env.SetScenario(WeatherType.Rain);
        for (int i = 0; i < 4000 * 4; i++) env.Update(0.25f);
        var state = env.GetStateForMap(MapAtmosphere.Default);
        float rate = WorldEnvironmentSystem.RainRateFor(state);
        _o.WriteLine($"Rain front, midsummer: intensity {state.PrecipitationIntensity:F2}, {state.Temperature:F1} °C, {rate:F1} mm/h");
        Assert.Equal(RainCategory.Moderate, Rainfall.Category(rate));

        var winter = new WorldEnvironmentSystem(new Random(3)) { FrontProbabilityPerTick = 0 };
        winter.SetDate(12f, 1);
        winter.SetScenario(WeatherType.Snow);
        for (int i = 0; i < 4000 * 4; i++) winter.Update(0.25f);
        Assert.Equal(0f, WorldEnvironmentSystem.RainRateFor(winter.GetStateForMap(MapAtmosphere.Default)));
    }

    [Fact]
    public void TheRateCrossesTheWireAfterEverythingElse()
    {
        var update = new WorldStateUpdate { Temperature = 11f, PrecipitationIntensity = 0.6f, RainRateMmPerHour = 7.1f };
        var back = (WorldStateUpdate)MemoryPackSerializer.Deserialize<IMessage>(MemoryPackSerializer.Serialize<IMessage>(update))!;
        Assert.Equal(7.1f, back.RainRateMmPerHour);
        Assert.Equal(0.6f, back.PrecipitationIntensity);
        Assert.Equal(11f, back.Temperature);

        // The client keeps it and puts it in every snapshot.
        var world = new ClientWorldState();
        world.UpdateAtmosphere(update);
        Assert.Equal(7.1f, world.CurrentRainRate);
        Assert.Equal(7.1f, world.GetSnapshot().RainRateMmPerHour);
    }

    // ── The drops ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    // Gunn and Kinzer (1949), terminal speed at sea level, m/s.
    [InlineData(0.5f, 2.06f)]
    [InlineData(1.0f, 4.03f)]
    [InlineData(2.0f, 6.49f)]
    [InlineData(3.0f, 8.06f)]
    [InlineData(4.0f, 8.83f)]
    [InlineData(5.0f, 9.09f)]
    public void DropsFallAtGunnAndKinzersSpeeds(float diameterMm, float measured)
        => Assert.InRange(Rainfall.TerminalSpeed(diameterMm), measured * 0.92f, measured * 1.08f);

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1.5f)]
    [InlineData(5f)]
    [InlineData(25f)]
    [InlineData(70f)]
    public void TheDropsCarryTheRate(float rate)
    {
        // Marshall-Palmer as published is a fit; between these sizes at these speeds it is within a
        // fifth of the rate it was asked for, and the closure makes it exact.
        Assert.InRange(Rainfall.UnclosedRate(rate), rate * 0.8f, rate * 1.25f);
        Assert.InRange(Rainfall.CarriedRate(rate), rate * 0.99f, rate * 1.01f);
    }

    [Fact]
    public void HarderRainIsMoreDropsAndBiggerOnes()
    {
        float light = Rainfall.DropsPerSquareMetreSecond(Rainfall.LightRate);
        float moderate = Rainfall.DropsPerSquareMetreSecond(Rainfall.ModerateRate);
        float heavy = Rainfall.DropsPerSquareMetreSecond(Rainfall.HeavyRate);
        float violent = Rainfall.DropsPerSquareMetreSecond(Rainfall.ViolentRate);
        _o.WriteLine($"drops per m² per s: light {light:F0}, moderate {moderate:F0}, heavy {heavy:F0}, violent {violent:F0}");
        Assert.True(light < moderate && moderate < heavy && heavy < violent);
        // A few hundred to a few thousand a square metre a second, as the literature has it.
        Assert.InRange(light, 500f, 3000f);
        Assert.InRange(violent, 4000f, 20000f);

        var a = new DropSizeTable(); a.Build(Rainfall.LightRate);
        var b = new DropSizeTable(); b.Build(Rainfall.ViolentRate);
        var rng = new Random(5);
        double ma = 0, mb = 0;
        for (int i = 0; i < 20000; i++)
        {
            float da = a.Draw((float)rng.NextDouble()), db = b.Draw((float)rng.NextDouble());
            Assert.InRange(da, Rainfall.SmallestDropMm, Rainfall.LargestDropMm);
            ma += da; mb += db;
        }
        _o.WriteLine($"mean arriving drop: light {ma / 20000:F2} mm, violent {mb / 20000:F2} mm");
        Assert.True(mb > ma * 1.2);
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(10f)]
    [InlineData(25f)]
    [InlineData(70f)]
    public void TheKineticEnergyIsWhatTheErosionLiteratureMeasures(float rate)
    {
        // van Dijk, Bruijnzeel and Rosewell (2002): e = 28.3 (1 − 0.52 exp(−0.042 R)) J per m² per mm.
        // It checks the drop sizes and the speeds together.
        float model = Rainfall.KineticPower(rate) * 3600f / rate;
        float measured = 28.3f * (1f - 0.52f * MathF.Exp(-0.042f * rate));
        Assert.InRange(model, measured * 0.8f, measured * 1.2f);
    }

    // ── The surfaces ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Asphalt", 0.1f, RainSurfaceKind.Hard)]
    [InlineData("Concrete", 0.3f, RainSurfaceKind.Hard)]
    [InlineData("Brick", 0.3f, RainSurfaceKind.Hard)]
    [InlineData("Tile", 0.02f, RainSurfaceKind.Plate)]
    [InlineData("Metal", 0.0007f, RainSurfaceKind.Plate)]
    [InlineData("Metal", 0.05f, RainSurfaceKind.Hard)]
    [InlineData("Glass", 0.006f, RainSurfaceKind.Plate)]
    [InlineData("Wood", 0.018f, RainSurfaceKind.Plate)]
    [InlineData("Grass", 0.1f, RainSurfaceKind.Soft)]
    [InlineData("Dirt", 0.1f, RainSurfaceKind.Soft)]
    [InlineData("Gravel", 0.1f, RainSurfaceKind.Soft)]
    [InlineData("Water", 0.1f, RainSurfaceKind.Pool)]
    [InlineData("Foliage", 0.5f, RainSurfaceKind.Canopy)]
    [InlineData("None", 0.1f, RainSurfaceKind.None)]
    public void EachMaterialIsTheSurfaceItIs(string material, float skin, RainSurfaceKind kind)
        => Assert.Equal(kind, RainSurfaces.KindOf(material, skin));

    [Fact]
    public void AMaterialTheRegistryDoesNotKnowIsGeneric()
    {
        // Steel is "Metal"; "Steel" falls back, quietly, to Generic.
        Assert.Equal(RainSurfaces.KindOf("Generic", 0.001f), RainSurfaces.KindOf("Steel", 0.001f));
        Assert.Equal(RainSurfaces.KindOf("Generic", 0.3f), RainSurfaces.KindOf("Steel", 0.3f));
    }

    [Fact]
    public void SoftGroundTakesTheTopOffTheClick()
    {
        Assert.Equal(1f, RainSurfaces.ContactStretch(AcousticRegistry.GetProperties("Asphalt")));
        float grass = RainSurfaces.ContactStretch(AcousticRegistry.GetProperties("Grass"));
        float dirt = RainSurfaces.ContactStretch(AcousticRegistry.GetProperties("Dirt"));
        Assert.True(grass > dirt && dirt > 1f);
        Assert.Equal(0f, RainSurfaces.PuddleShare(0f));
        Assert.True(RainSurfaces.PuddleShare(50f) > RainSurfaces.PuddleShare(2f));
        Assert.True(RainSurfaces.PuddleShare(1000f) <= RainSurfaces.PuddleMaxShare);
        Assert.InRange(RainSurfaces.CanopyCatch(4.5f), 0.85f, 0.95f);
    }

    [Fact]
    public void ABlowCarriesTheDropsMomentum()
    {
        // The pulse's area is m v and its peak 0.8 ρ v² D²: the shape is built to both.
        float d = 2f, v = Rainfall.TerminalSpeed(2f);
        float tau = RainPlate.BlowSeconds(d, v);
        double area = 0;
        for (int i = 0; i < 20000; i++) area += RainPlate.BlowShape((i + 0.5f) / 1000f) * tau / 1000f;
        Assert.InRange(area * RainPlate.PeakForce(d, v), RainPlate.Impulse(d, v) * 0.97, RainPlate.Impulse(d, v) * 1.03);
        Assert.InRange(RainPlate.BlowShare(tau, 1e-3f, 1e6f), 0.97f, 1.01f);
        // A smaller faster blow has more of its energy high.
        Assert.True(RainPlate.BlowShare(RainPlate.BlowSeconds(1f, 4f), 4000f, 16000f)
                  > RainPlate.BlowShare(RainPlate.BlowSeconds(5f, 9f), 4000f, 16000f));
    }

    [Fact]
    public void AThinSheetDrumsAndASlabIsSilent()
    {
        float g = MathF.PI * MathF.Log(26f);     // a metre under the middle of a roof ten metres across
        var steel = new RainPlate("Metal", 0.0007f, 1.2f, 1.2f);
        var glass = new RainPlate("Glass", 0.006f, 1.2f, 1.2f);
        var slab = new RainPlate("Concrete", 0.15f, 1.2f, 1.2f);
        float Db(RainPlate p, float r) => 10f * MathF.Log10(p.MeanSquarePressure(r, g) / 4e-10f);
        _o.WriteLine($"heavy rain under: steel {Db(steel, 25f):F1} dB, glass {Db(glass, 25f):F1} dB, 150 mm slab {Db(slab, 25f):F1} dB");
        Assert.True(Db(steel, 25f) - Db(slab, 25f) > 20f);
        Assert.True(Db(slab, 25f) < RainSurvey.SilentRoofDb + 5f);
        Assert.InRange(Db(steel, 25f), 45f, 70f);
        // Harder rain is louder, by about the rate's own step and a little more for its bigger drops.
        Assert.InRange(Db(steel, 25f) - Db(steel, 5f), 7f, 15f);
        // Glass meets the air at a couple of kilohertz; thin steel not till 17.
        Assert.InRange(glass.CriticalHz, 1500f, 2500f);
        Assert.True(steel.CriticalHz > 10000f);
    }

    [Fact]
    public void TheSynthesiserRendersWhatThePlateLawSays()
    {
        // The roof over the ear, from below: rendered event by event, measured, and set against the
        // analytic law it is the same physics as.
        var layer = new RainLayer { Kind = RainSurfaceKind.Plate, Material = "Metal", FromBelow = true,
                                    Plate = new RainPlate("Metal", 0.0007f, 1.2f, 1.2f) };
        layer.Add(0, 3f, 1.0f, 1f);
        layer.Add(1, 9f, 1.8f, 1f);
        var patch = new RainPatch { Layers = new[] { layer }, ReferenceDistance = 1f };
        foreach (float rate in new[] { 5f, 25f })
        {
            var synth = new RainSynth(Rate, 9) { Patch = patch, RainRate = rate };
            for (int i = 0; i < Rate; i++) synth.Next();
            double e = 0;
            int n = 6 * Rate;
            for (int i = 0; i < n; i++) { float x = synth.Next(); Assert.True(float.IsFinite(x)); e += x * (double)x; }
            double rendered = 10 * Math.Log10(e / n / 4e-10);
            double law = 10 * Math.Log10(layer.Plate.MeanSquarePressure(rate, layer.ViewFactor) / 4e-10);
            _o.WriteLine($"{rate} mm/h: rendered {rendered:F1} dB, plate law {law:F1} dB");
            Assert.InRange(rendered, law - 3, law + 3);
        }
    }

    [Fact]
    public void HarderRainIsLouderOnTheStreet()
    {
        var world = World(); Ground(world, "Asphalt");
        var survey = new RainSurvey().Run(world, new Vector3(0f, 1.6f, 0f), -1, -1);
        double Leq(float rate)
        {
            double e = 0;
            foreach (var p in survey.Patches.Where(p => p != null))
            {
                var synth = new RainSynth(Rate, 3) { Patch = p, RainRate = rate };
                for (int i = 0; i < Rate / 2; i++) synth.Next();
                for (int i = 0; i < 3 * Rate; i++) { float x = synth.Next() / p!.ReferenceDistance; e += x * (double)x; }
            }
            return 10 * Math.Log10(e / (3 * Rate) / 4e-10);
        }
        double light = Leq(Rainfall.LightRate), heavy = Leq(Rainfall.HeavyRate), violent = Leq(Rainfall.ViolentRate);
        _o.WriteLine($"open street: light {light:F1} dB, heavy {heavy:F1} dB, violent {violent:F1} dB");
        Assert.True(heavy > light + 10 && violent > heavy + 3);
    }

    // ── The survey ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnAnOpenStreetTheRainIsAllRoundAndNothingIsOverhead()
    {
        var world = World(); Ground(world, "Asphalt");
        var r = new RainSurvey().Run(world, new Vector3(0f, 1.6f, 0f), -1, -1);
        _o.WriteLine(r.Describe());
        Assert.Null(r.Patches[RainSurvey.OverheadSlot]);
        for (int s = 1; s < RainFeeds.Slots; s++)
        {
            Assert.NotNull(r.Patches[s]);
            Assert.True(r.Direct[s]);
            Assert.All(r.Patches[s]!.Layers, l => Assert.Equal(RainSurfaceKind.Hard, l.Kind));
        }
        // East is east: the near-east patch is placed east of the ear.
        Assert.True(r.Centres[1].X > 1f && MathF.Abs(r.Centres[1].Z) < 0.5f);
        Assert.True(r.Centres[2].Z > 1f);
    }

    [Fact]
    public void UnderAShelterTheSheetDrumsOverhead()
    {
        var world = World(); Ground(world, "Asphalt");
        Box(world, "Metal", new Vector3(0f, 2.5f, 0f), new Vector3(3.2f, 0.0007f, 4.4f));
        Box(world, "Glass", new Vector3(0f, 1.2f, -2.17f), new Vector3(3.2f, 2.4f, 0.06f), leaf: 0.006f);
        Box(world, "Glass", new Vector3(0f, 1.2f, 2.17f), new Vector3(3.2f, 2.4f, 0.06f), leaf: 0.006f);
        var r = new RainSurvey().Run(world, new Vector3(0f, 1.6f, 0f), -1, -1);
        _o.WriteLine(r.Describe());
        var over = r.Patches[RainSurvey.OverheadSlot];
        Assert.NotNull(over);
        var layer = Assert.Single(over!.Layers);
        Assert.Equal(RainSurfaceKind.Plate, layer.Kind);
        Assert.True(layer.FromBelow);
        Assert.Equal("Metal", layer.Material);
        // Behind the glass ends the street is heard round their edges, not through them.
        Assert.True(r.Direct[2] && r.Direct[4]);
        Assert.True(r.Patches[2]!.Layers.Sum(l => l.ViewFactor) < r.Patches[1]!.Layers.Sum(l => l.ViewFactor) * 0.5f);
    }

    [Fact]
    public void UnderAConcreteRoofTheRoofIsSilentAndTheStreetIsBehindTheWalls()
    {
        var world = World(); Ground(world, "Asphalt");
        float f = 9f;
        Box(world, "Concrete", new Vector3(0f, f - 0.15f, 0f), new Vector3(6.6f, 0.3f, 6.6f));
        Box(world, "Concrete", new Vector3(0f, f + 3.15f, 0f), new Vector3(6.6f, 0.3f, 6.6f));
        Box(world, "Brick", new Vector3(-3.15f, f + 1.5f, 0f), new Vector3(0.3f, 3f, 6.6f));
        Box(world, "Brick", new Vector3(3.15f, f + 1.5f, 0f), new Vector3(0.3f, 3f, 6.6f));
        Box(world, "Brick", new Vector3(0f, f + 1.5f, -3.15f), new Vector3(6.6f, 3f, 0.3f));
        Box(world, "Brick", new Vector3(0f, f + 1.5f, 3.15f), new Vector3(6.6f, 3f, 0.3f));
        Box(world, "Brick", new Vector3(0f, (f - 0.3f) / 2f, 0f), new Vector3(6.6f, f - 0.3f, 6.6f));
        var r = new RainSurvey().Run(world, new Vector3(0f, f + 1.6f, 0f), -1, -1);
        _o.WriteLine(r.Describe());
        Assert.Null(r.Patches[RainSurvey.OverheadSlot]);
        for (int s = 1; s < RainFeeds.Slots; s++)
            if (r.Patches[s] != null) Assert.False(r.Direct[s], $"{RainSurvey.SlotName(s)} is heard in plain view from inside a closed room");
    }

    [Fact]
    public void UnderASheetRoofTheRoofIsTheVoice()
    {
        var world = World(); Ground(world, "Asphalt");
        float f = 9f;
        Box(world, "Concrete", new Vector3(0f, f - 0.15f, 0f), new Vector3(6.6f, 0.3f, 6.6f));
        Box(world, "Metal", new Vector3(0f, f + 3f, 0f), new Vector3(6.6f, 0.0007f, 6.6f));
        var r = new RainSurvey().Run(world, new Vector3(0f, f + 1.6f, 0f), -1, -1);
        Assert.NotNull(r.Patches[RainSurvey.OverheadSlot]);
        Assert.Equal((1f, 1f, 1f), r.OverheadEq);
    }

    [Fact]
    public void BesideACarItsSteelAndGlassAreInThePatchOnItsSide()
    {
        var world = World(); Ground(world, "Asphalt");
        var p = MachineRegistry.VehicleFor("v6");
        int id = 900;
        var def = new EntityDefinition
        {
            EntityId = id, Type = EntityType.StaticObject, Moves = true,
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(p.WidthMetres, p.HeightMetres, p.LengthMetres), IsSolid = true },
            Material = new MaterialComponent { Material = "Metal" },
            SoundEmitter = new SoundEmitterComponent { SoundId = "engine:v6" },
            Transform = new Transform { Position = new Vector3(1.8f, 0f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        var snap = new EntitySnapshot { Id = id, Definition = def, Transform = def.Transform };
        world.Entities[id] = snap;
        world.DynamicEntities.Add(snap);
        var r = new RainSurvey().Run(world, new Vector3(0f, 1.6f, 0f), -1, -1);
        _o.WriteLine(r.Describe());
        var east = r.Patches[1]!;
        Assert.Contains(east.Layers, l => l.Kind == RainSurfaceKind.Plate && l.Material == p.Body.PanelMaterial);
        Assert.Contains(east.Layers, l => l.Kind == RainSurfaceKind.Plate && l.Material == "Glass");
        Assert.DoesNotContain(r.Patches[3]!.Layers, l => l.Kind == RainSurfaceKind.Plate);
        // Sitting in it, its roof is the roof over the ear.
        var inside = new RainSurvey().Run(world, new Vector3(1.8f, 1.1f, 0f), -1, id);
        Assert.True(inside.OverheadIsVehicle);
        Assert.NotNull(inside.Patches[RainSurvey.OverheadSlot]);
    }

    [Fact]
    public void UnderATreeTheLeavesAndTheirDripsAreInThePatches()
    {
        var world = World(); Ground(world, "Grass");
        int id = 901;
        var def = new EntityDefinition
        {
            EntityId = id, Type = EntityType.StaticObject,
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.5f), IsSolid = false },
            Material = new MaterialComponent { Material = "Foliage" },
            SoundEmitter = new SoundEmitterComponent { SoundId = "foliage:park_tree" },
            Transform = new Transform { Position = new Vector3(0f, 7f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        world.Entities[id] = new EntitySnapshot { Id = id, Definition = def, Transform = def.Transform };
        var r = new RainSurvey().Run(world, new Vector3(1.5f, 1.6f, 0f), -1, -1);
        _o.WriteLine(r.Describe());
        Assert.Null(r.Patches[RainSurvey.OverheadSlot]);
        var canopy = r.Patches.Where(p => p != null).SelectMany(p => p!.Layers).Where(l => l.Kind == RainSurfaceKind.Canopy).ToList();
        Assert.NotEmpty(canopy);
        Assert.All(canopy, l => Assert.Equal(RainSurfaceKind.Soft, l.UnderKind));
        Assert.All(canopy, l => Assert.InRange(l.DripFallMetres, 1f, 5f));
    }

    // ── The voices ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheKeysNameTheSlots()
    {
        for (int s = 0; s < RainFeeds.Slots; s++)
        {
            Assert.True(RainFeeds.TryParse(RainFeeds.Key(s), out int back));
            Assert.Equal(s, back);
        }
        Assert.False(RainFeeds.TryParse("rain:" + RainFeeds.Slots, out _));
        Assert.False(RainFeeds.TryParse("rain:x", out _));
        Assert.False(RainFeeds.TryParse("water:park_fountain", out _));
    }

    [Fact]
    public void AVoiceMeasuresItsPatchAndRendersAtItsReference()
    {
        var world = World(); Ground(world, "Asphalt");
        var survey = new RainSurvey().Run(world, new Vector3(0f, 1.6f, 0f), -1, -1);
        var feed = new RainFeed { Patch = survey.Patches[1], Rate = Rainfall.HeavyRate };
        var voice = new RainVoiceState(feed, Rate, 7);
        var buf = new float[512];
        double e = 0; long n = 0;
        for (int b = 0; b < 4 * Rate / 512; b++)
        {
            voice.Render(buf);
            if (b < Rate / 512) continue;          // the first second is it finding its level
            foreach (float x in buf) { Assert.True(float.IsFinite(x)); e += x * (double)x; n++; }
        }
        double dbfs = 10 * Math.Log10(e / n);
        _o.WriteLine($"rendered {dbfs:F1} dBFS, measured level {feed.LevelDb:F1} dB at the patch's reference");
        // Its level is rendered at the reference, which is the shared headroom under full scale.
        Assert.InRange(dbfs, -RainVoiceState.HeadroomDb - 3, -RainVoiceState.HeadroomDb + 3);
        Assert.False(float.IsNaN(feed.LevelDb));
        Assert.InRange(feed.LevelDb, 40f, 90f);
    }

    // ── Worlds ──────────────────────────────────────────────────────────────────────────────────

    private static int _id = 100;

    private static WorldSnapshot World() => new() { StaticGrid = new SpatialGrid<int>(10.0f) };

    private static void Ground(WorldSnapshot w, string material)
        => Box(w, material, new Vector3(0f, -0.05f, 0f), new Vector3(200f, 0.1f, 200f));

    private static void Box(WorldSnapshot w, string material, Vector3 centre, Vector3 size, float leaf = 0f)
    {
        int id = System.Threading.Interlocked.Increment(ref _id);
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
            Material = new MaterialComponent { Material = material },
            Acoustics = new AcousticComponent { LeafMetres = leaf },
            Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        w.Entities[id] = new EntitySnapshot { Id = id, Definition = def, Transform = def.Transform };
        w.StaticGrid!.AddOverlapping(centre, size, Quaternion.Identity, id, isStatic: true);
    }
}
