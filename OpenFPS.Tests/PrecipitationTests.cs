using System.Numerics;
using MemoryPack;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// What falls (rain of any drop size, freezing rain, sleet, snow, hail) as the radar reads it, and the
/// near drops played one by one; measured in the lab (--rain levels), held here.
/// </summary>
public class PrecipitationTests
{
    private readonly ITestOutputHelper _o;
    public PrecipitationTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }

    [Fact]
    public void KindDropSizeAndHailCrossTheWireAfterTheRate()
    {
        var update = new WorldStateUpdate
        {
            Temperature = 4f, PrecipitationIntensity = 0.7f, RainRateMmPerHour = 25f,
            PrecipitationKind = (int)PrecipitationKind.Hail, RainMedianDropMm = 2.2f, HailDiameterMm = 44f,
        };
        var back = (WorldStateUpdate)MemoryPackSerializer.Deserialize<IMessage>(MemoryPackSerializer.Serialize<IMessage>(update))!;
        Assert.Equal((int)PrecipitationKind.Hail, back.PrecipitationKind);
        Assert.Equal(2.2f, back.RainMedianDropMm);
        Assert.Equal(44f, back.HailDiameterMm);
        Assert.Equal(25f, back.RainRateMmPerHour);

        var world = new ClientWorldState();
        world.UpdateAtmosphere(update);
        var p = world.GetSnapshot().Precipitation;
        Assert.Equal(PrecipitationKind.Hail, p.Kind);
        Assert.Equal(44f, p.HailMm);
        Assert.True(p.Falling);
    }

    [Theory]
    [InlineData(1.5f, 0f)]
    [InlineData(5f, 0f)]
    [InlineData(25f, 0f)]
    [InlineData(5f, 1f)]
    [InlineData(5f, 3f)]
    public void TheDropsCarryTheRateWhateverTheirSize(float rate, float median)
    {
        var s = new ParticleSpectrum();
        s.Build(new Precipitation(PrecipitationKind.Rain, rate, median));
        // Water carried: the drops arriving a second, each its own volume, as mm an hour.
        var rng = new Random(3);
        double volume = 0;
        const int N = 40000;
        for (int i = 0; i < N; i++)
        {
            double d = s.Draw((float)rng.NextDouble()) * 1e-3;
            volume += Math.PI / 6 * d * d * d;
        }
        double mmh = volume / N * s.PerSquareMetreSecond * 3.6e6;
        _o.WriteLine($"{rate} mm/h, median {median}: {s.PerSquareMetreSecond:F0} drops/m²/s carry {mmh:F2} mm/h, {s.Dbz:F1} dBZ");
        Assert.InRange(mmh, rate * 0.9, rate * 1.1);
    }

    [Fact]
    public void BiggerDropsAtTheSameRateAreFewerAndLouderOnTheRadar()
    {
        var small = new ParticleSpectrum(); small.Build(new Precipitation(PrecipitationKind.Rain, 5f, 1f));
        var big = new ParticleSpectrum(); big.Build(new Precipitation(PrecipitationKind.Rain, 5f, 3f));
        Assert.True(big.PerSquareMetreSecond < small.PerSquareMetreSecond / 5f);
        Assert.True(big.Dbz > small.Dbz + 10f);
    }

    [Theory]
    [InlineData(0.3f, "pale green")]
    [InlineData(1.5f, "green")]
    [InlineData(5f, "yellow")]
    [InlineData(25f, "orange")]
    [InlineData(70f, "red")]
    public void TheRateReadsAsTheRadarColourItIs(float rate, string colour)
    {
        float dbz = Hydrometeors.DbzFromRate(rate);
        Assert.Equal(colour, Hydrometeors.RadarColour(dbz, PrecipitationKind.Rain));
        Assert.Equal(rate, Hydrometeors.RateFromDbz(dbz), 2);
        Assert.Equal("blue", Hydrometeors.RadarColour(dbz, PrecipitationKind.Snow));
    }

    [Fact]
    public void AReflectivityGivesARateAndADropSizeWithThatReflectivity()
    {
        float rate = Hydrometeors.RateFromDbz(45f);
        float median = Hydrometeors.MedianForDbz(PrecipitationKind.Rain, rate, 45f);
        var s = new ParticleSpectrum(); s.Build(new Precipitation(PrecipitationKind.Rain, rate, median));
        _o.WriteLine($"45 dBZ: {rate:F1} mm/h, median {median:F2} mm, spectrum {s.Dbz:F1} dBZ");
        Assert.InRange(rate, 25f, 32f);
        Assert.InRange(s.Dbz, 44.5f, 45.5f);
    }

    [Theory]
    // Hail falls about 12 √D m/s with D in cm (a drag coefficient of 0.6 on a sphere of 800 kg/m³:
    // Knight and Heymsfield 1983; Heymsfield et al. 2014 find 0.5-0.8 for natural stones).
    [InlineData(6f)]
    [InlineData(25f)]
    [InlineData(44f)]
    [InlineData(70f)]
    public void HailFallsAsADragLawSays(float mm)
    {
        float v = Hydrometeors.FallSpeed(PrecipitationKind.Hail, mm);
        float expected = 12f * MathF.Sqrt(mm / 10f);
        _o.WriteLine($"{mm} mm hail: {v:F1} m/s (12 √D: {expected:F1})");
        Assert.InRange(v, expected * 0.8f, expected * 1.25f);
    }

    [Fact]
    public void AStoneTouchesSteelForLessTimeThanAsphaltAndABigStoneForLonger()
    {
        float onSteel = Hydrometeors.HertzSeconds(PrecipitationKind.Hail, 20f, 20f, 200f);
        float onAsphalt = Hydrometeors.HertzSeconds(PrecipitationKind.Hail, 20f, 20f, 3f);
        float bigger = Hydrometeors.HertzSeconds(PrecipitationKind.Hail, 44f, 20f, 200f);
        _o.WriteLine($"20 mm on steel {onSteel * 1e6:F0} µs, on asphalt {onAsphalt * 1e6:F0} µs; 44 mm on steel {bigger * 1e6:F0} µs");
        Assert.True(onSteel < onAsphalt);
        Assert.True(bigger > onSteel);
        Assert.InRange(onSteel, 5e-6f, 5e-4f);
    }

    [Fact]
    public void SnowOnTheStreetIsFarQuieterThanRainOfTheSameWater()
    {
        var world = World(); Ground(world, "Asphalt");
        var survey = new RainSurvey().Run(world, new Vector3(0f, 1.6f, 0f), -1, -1);
        double Level(Precipitation p)
        {
            var synth = new RainSynth(48000, 5) { Patch = survey.Patches[1], Falling = p };
            double e = 0;
            for (int i = 0; i < 48000; i++) synth.Next();
            for (int i = 0; i < 48000; i++) { float x = synth.Next(); e += x * (double)x; }
            return 10 * Math.Log10(Math.Max(1e-20, e / 48000) / 4e-10);
        }
        double rain = Level(new Precipitation(PrecipitationKind.Rain, 1.5f));
        double snow = Level(new Precipitation(PrecipitationKind.Snow, 1.5f));
        double sleet = Level(new Precipitation(PrecipitationKind.Sleet, 1.5f));
        _o.WriteLine($"near east patch: rain {rain:F1} dB, sleet {sleet:F1} dB, snow {snow:F1} dB");
        Assert.True(snow < rain - 30);
        Assert.True(sleet > rain);
    }

    // ── The listener and the near drops ─────────────────────────────────────────────────────────

    [Fact]
    public void UnderTheOpenSkyTheRainLandsOnTheListener()
    {
        var world = World(); Ground(world, "Asphalt");
        var r = new RainSurvey().Run(world, new Vector3(0f, 1.6f, 0f), -1, -1);
        var over = r.Patches[RainSurvey.OverheadSlot];
        Assert.NotNull(over);
        var body = Assert.Single(over!.Layers);
        Assert.Equal(RainSurfaceKind.Soft, body.Kind);
        Assert.Equal(RainSurfaces.BodyMaterial, body.Material);
        Assert.Equal(0, r.OverheadEntity);
        Assert.Contains(r.Near, c => c.Surface == body && c.Ring == RainSurvey.HeadRing);

        // Under a roof, it does not.
        Box(world, "Metal", new Vector3(0f, 2.5f, 0f), new Vector3(3.2f, 0.0007f, 4.4f));
        var sheltered = new RainSurvey().Run(world, new Vector3(0f, 1.6f, 0f), -1, -1);
        Assert.DoesNotContain(sheltered.Patches[RainSurvey.OverheadSlot]!.Layers, l => l.Material == RainSurfaces.BodyMaterial);
    }

    [Fact]
    public void AboutAsManyDropsAreHeardOneByOneAsAnEarCanTellApart()
    {
        var world = World(); Ground(world, "Asphalt");
        var ear = new Vector3(0f, 1.6f, 0f);
        var survey = new RainSurvey().Run(world, ear, -1, -1);
        var near = new NearDrops(9);
        var bank = new DropBank();
        var impacts = new List<NearDrops.Impact>();
        const double Seconds = 20;
        for (double t = 0; t < Seconds; t += 0.05) near.Plan(survey, new Precipitation(PrecipitationKind.Rain, 5f), ear, t, 0.05f, impacts, bank);
        double perSecond = impacts.Count / Seconds;
        _o.WriteLine($"moderate rain, open street: {perSecond:F1} a second one by one, from {near.RainFromMm:F2} mm");
        Assert.InRange(perSecond, NearDrops.ResolvableImpactsPerSecond * 0.7, NearDrops.ResolvableImpactsPerSecond * 1.3);
        // The patches no longer render those drops themselves.
        Assert.Contains(survey.Near, c => c.Surface.DiscreteRainFromMm.Any(d => d < 10f));
        Assert.All(impacts, i => Assert.True(Vector3.Distance(i.Position, ear) < 3.5f));
    }

    [Fact]
    public void HailIsPlayedStoneByStoneAndBouncesOffTheStreet()
    {
        var world = World(); Ground(world, "Asphalt");
        var ear = new Vector3(0f, 1.6f, 0f);
        var survey = new RainSurvey().Run(world, ear, -1, -1);
        var near = new NearDrops(4);
        var bank = new DropBank();
        var impacts = new List<NearDrops.Impact>();
        var hail = new Precipitation(PrecipitationKind.Hail, Hydrometeors.HailRainRate, 0f, 25f);
        for (double t = 0; t < 10; t += 0.05) near.Plan(survey, hail, ear, t, 0.05f, impacts, bank);
        int stones = impacts.Count(i => i.Kind == PrecipitationKind.Hail && !i.Bounce);
        int bounces = impacts.Count(i => i.Bounce);
        _o.WriteLine($"hail 25 mm: {stones} stones and {bounces} bounces in 10 s, from {near.HailFromMm:F1} mm");
        Assert.True(stones > 20);
        Assert.True(bounces > 0);
        // Each one a finite sound with a level.
        var one = impacts.First(i => i.Kind == PrecipitationKind.Hail);
        var sound = bank.Get(one, 0)!;
        Assert.All(sound.Pcm, x => Assert.True(float.IsFinite(x)));
        Assert.InRange(DropBank.LevelDb(sound, one, 1f), 60f, 150f);
    }

    // ── Worlds ──────────────────────────────────────────────────────────────────────────────────

    private static int _id = 5000;

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
