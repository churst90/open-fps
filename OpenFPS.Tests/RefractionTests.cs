using System.Numerics;
using OpenFPS.Sound.Core;
using OpenFPS.Common;
using OpenFPS.Acoustics;
using OpenFPS.Materials;
using OpenFPS.Physics;

namespace OpenFPS.Tests;

/// <summary>
/// Sound bent by the wind and the air's layers (Refraction, AirLayers, Bending): Fleagle's shadow edge, Harmonoise's
/// curvatures, the creeping wave's depth and the stir's floor, the curved-ground analogy's raised bounce, the air's
/// layers by the sun, the cloud, the wind and the water, and the rule as the mixer reads it: nothing near or in still
/// neutral air, a shadow upwind on a sunny afternoon, a lawn cancelling less downwind, the thunder's proxies left alone.
/// </summary>
public class RefractionTests
{
    private const float C = 340f, Ear = 1.7f;

    public RefractionTests() => AcousticRegistry.Initialize();

    // ── The law against its published cases ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fleagle (1949): the lowest ray from a height h in a linear gradient of radius R grazes the ground √(2Rh) off. The
    /// standard lapse (0.0065 K/m at 15 °C) bends with R = 2T/lapse = 88.7 km, and a source 4 km up is in shadow past
    /// about 26.6 km (the thunder's "about 26 km"); a gradient of -0.1 /s at c = 340 (R 3.4 km) puts the edge for a
    /// 5 m source and a 2 m ear √6800 (√5 + √2) = 301 m off.
    /// </summary>
    [Fact]
    public void TheShadowEdgeIsFleaglesRootTwoRH()
    {
        float r = 2f * 288.15f / 0.0065f;
        Assert.Equal(MathF.Sqrt(2f * r) * (MathF.Sqrt(4000f) + MathF.Sqrt(Ear)), Refraction.ShadowEdge(-1f / r, 4000f, Ear), 0);
        Assert.InRange(Refraction.ShadowEdge(-1f / r, 4000f, 0.01f), 26500f, 26800f);
        Assert.InRange(Refraction.ShadowEdge(-0.1f / C, 5f, 2f), 299f, 303f);
        Assert.True(float.IsPositiveInfinity(Refraction.ShadowEdge(0.1f / C, 5f, 2f)), "sound bent down casts no shadow");
    }

    /// <summary>
    /// Harmonoise's two radii (Nota et al., Inter-noise 2004, eq. 6): R_lin = √((c/A)² + (D/2)²) for the linear term A,
    /// R_log = (D/8) √(2π c / |B|) for the log term B, added as curvatures (eq. 7). B = 1 m/s over 200 m is
    /// 25 √(2136.3) = 1155.5 m; A = 0.1 /s over 200 m is √(3400² + 100²) = 3401.5 m.
    /// </summary>
    [Fact]
    public void TheCurvatureIsHarmonoises()
    {
        float log = Refraction.Curvature(new Refraction.Profile(1f, 0f), 200f, 0.5f, Ear, C);
        Assert.Equal(1f / (200f / 8f * MathF.Sqrt(2f * MathF.PI * C / 1f)), log, 7);
        float lin = Refraction.Curvature(new Refraction.Profile(0f, -0.1f), 200f, 0.5f, Ear, C);
        Assert.Equal(-1f / MathF.Sqrt(3400f * 3400f + 100f * 100f), lin, 7);
        Assert.Equal(log + lin, Refraction.Curvature(new Refraction.Profile(1f, -0.1f), 200f, 0.5f, Ear, C), 7);
        // A source higher than the ray would climb reads the log term where it is: thunder kilometres up bends less by it.
        float high = Refraction.Curvature(new Refraction.Profile(1f, 0f), 200f, 500f, Ear, C);
        Assert.Equal(4f / MathF.PI / (C * 500f), high, 7);
    }

    /// <summary>
    /// Past the edge the creeping wave dies as exp(-Im τ1 (k/2R²)^(1/3) x), Im τ1 = 1.01879 sin 60° for a rigid ground
    /// (Keller; Pierce 1981 ch. 9; Berry and Daigle 1988): R = 3.4 km, 50 m past the edge, at the middle band's centre.
    /// The treble goes deeper than the bass, and nothing is taken short of the edge.
    /// </summary>
    [Fact]
    public void PastTheEdgeTheCreepingWaveDies()
    {
        float k = -1f / 3400f, edge = Refraction.ShadowEdge(k, 0.5f, Ear);
        Assert.Equal(Refraction.Bands.None, Refraction.Shadow(edge - 1f, 0.5f, Ear, k, Refraction.CalmStirVariance, C));
        var s = Refraction.Shadow(edge + 50f, 0.5f, Ear, k, Refraction.CalmStirVariance, C);
        float kMid = 2f * MathF.PI * AcousticBands.GeometricCentresHz.Mid / C;
        float expected = 8.685889f * 1.01879f * MathF.Sin(MathF.PI / 3f) * MathF.Cbrt(kMid / (2f * 3400f * 3400f)) * 50f;
        Assert.Equal(expected, s.Mid, 3);
        Assert.True(s.Low < s.Mid && s.Mid < s.High, $"bass {s.Low:F1}, middle {s.Mid:F1}, treble {s.High:F1} dB");
    }

    /// <summary>
    /// The stir scatters sound into a shadow and holds it 20 to 30 dB down (Embleton 1996): 30 in a calm, ten less for a
    /// tenfold stir, never under 20; and the thunder keeps its own judged stir.
    /// </summary>
    [Fact]
    public void TheStirHoldsAShadowTwentyToThirtyDecibelsDown()
    {
        float k = -1f / 500f;
        var calm = Refraction.Shadow(5000f, 0.5f, Ear, k, Refraction.CalmStirVariance, C);
        Assert.Equal(30f, calm.High, 3);
        var stirred = Refraction.Shadow(5000f, 0.5f, Ear, k, 10f * Refraction.CalmStirVariance, C);
        Assert.Equal(20f, stirred.High, 3);
        Assert.Equal(20f, Refraction.ShadowFloorDb(1000f * Refraction.CalmStirVariance), 3);
        var storm = new OutdoorAir(15f, 0.95f, 1013.25f, new Vector3(15f, 0f, -10f), 0.8f);
        // The thunder's own law, as it was written before it was shared: a storm's 18 m/s gusting at 0.8.
        float sv = 0.2f + 0.03f * MathF.Sqrt(325f) * 1.8f, st = 0.5f / (2f * 288.15f);
        Assert.Equal(sv * sv / (C * C) + st * st, Thunder.TurbulenceVariance(storm, C), 9);
    }

    /// <summary>
    /// Bent down, the curved-ground analogy (Almgren 1987) lifts each end by d²/2R, d its leg's run from the bounce; bent
    /// up or straight, nothing (the shadow is the whole of it there); and never more than 30 m.
    /// </summary>
    [Fact]
    public void BentDownTheBounceComesInAsIfBothEndsStoodHigher()
    {
        var (s, e) = Refraction.Raise(0.5f, Ear, 300f, 1f / 2000f);
        float ds = 300f * 0.5f / (0.5f + Ear), dr = 300f - ds;
        Assert.Equal(ds * ds / 4000f, s, 4);
        Assert.Equal(dr * dr / 4000f, e, 4);
        Assert.Equal((0f, 0f), Refraction.Raise(0.5f, Ear, 300f, -1f / 2000f));
        Assert.Equal(Refraction.MostRaiseMetres, Refraction.Raise(0.5f, Ear, 3000f, 1f / 500f).Ear);
    }

    /// <summary>
    /// The lawn's dip fills downwind: a car's exhaust 300 m off across grass, its ends raised as a 4.5 m/s tailwind on a
    /// clear night raises them, keeps more of its middle band than the same bounce unbent. And raised by nothing, the
    /// bounce is the one Cody approved, bit for bit.
    /// </summary>
    [Fact]
    public void DownwindALawnCancelsLess()
    {
        var grass = AcousticRegistry.GetProperties("Grass");
        var src = new Vector3(0f, 0.5f, 0f);
        var ear = new Vector3(300f, Ear, 0f);
        var weather = Weather(AirLayers.From(3f, 172, 45f, 0f, 0.4f, 12f, 1013.25f, 4.5f, 0.1f), 4.5f);
        var bent = Bending.Along(weather, src, ear, 0f, false, 1000.0, C);
        Assert.True(bent.EarRaise > 5f, $"ear raised {bent.EarRaise:F1} m");
        double[] real = new double[3], copy = new double[3], realUp = new double[3], copyUp = new double[3];
        Assert.True(GroundAnswer.Powers(0f, grass, src, ear, C, real, copy));
        Assert.True(GroundAnswer.Powers(0f, grass, src, ear, C, realUp, copyUp, bent.SourceRaise, bent.EarRaise));
        Assert.True(10.0 * Math.Log10(realUp[1] / real[1]) > 3.0, $"the middle band's real ground {10 * Math.Log10(real[1]):F1} dB, raised {10 * Math.Log10(realUp[1]):F1}");
        Assert.Equal(GroundAnswer.Reflect(0f, grass, src, ear, 0f, C), GroundAnswer.Reflect(0f, grass, src, ear, 0f, C, 0f, 0f));
    }

    // ── The air's layers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A clear summer afternoon heats the air from below (unstable, L negative, the temperature's log term bending sound
    /// up); a clear night cools it (stable, bending down); wind stirs either toward neutral; cloud damps the night's
    /// layering (θ* = 0.09 (1 − 0.5 N²)).
    /// </summary>
    [Fact]
    public void TheSunLiftsAndTheNightLaysTheAir()
    {
        var day = AirLayers.From(15f, 172, 45f, 0f, 0.4f, 25f, 1013.25f, 2f, 0.1f).Land;
        Assert.True(day.ObukhovMetres < 0f && day.ThermalLog < -0.1f, $"afternoon: L {day.ObukhovMetres:F0}, log {day.ThermalLog:F3}");
        var night = AirLayers.From(3f, 172, 45f, 0f, 0.4f, 15f, 1013.25f, 2f, 0.1f).Land;
        Assert.True(night.ObukhovMetres > 0f && night.ThermalLog > 0f && night.ThermalLinear > 0f,
                    $"night: L {night.ObukhovMetres:F0}, log {night.ThermalLog:F3}, linear {night.ThermalLinear:F4}");
        var windyDay = AirLayers.From(15f, 172, 45f, 0f, 0.4f, 25f, 1013.25f, 10f, 0.1f).Land;
        Assert.True(MathF.Abs(windyDay.ObukhovMetres) > MathF.Abs(day.ObukhovMetres), "wind stirs the day toward neutral");
        var cloudyNight = AirLayers.From(3f, 172, 45f, 1f, 0.95f, 15f, 1013.25f, 2f, 0.1f).Land;
        Assert.True(cloudyNight.ThermalLinear < night.ThermalLinear && cloudyNight.ThermalLog < night.ThermalLog, "cloud damps the night");
    }

    /// <summary>Air warmer than the water lies on it and bends sound down; water warmer than the air stirs it and bends
    /// sound up (the bulk formula, Large and Pond 1982). The lake follows the season a month behind.</summary>
    [Fact]
    public void WarmAirOverColdWaterBendsSoundDown()
    {
        var warmAir = AirLayers.OverWater(10f, 25f, 1013.25f, 3f);
        Assert.True(warmAir.ObukhovMetres > 0f && warmAir.ThermalLog > 0f, $"warm air over a cold lake: L {warmAir.ObukhovMetres:F0}");
        var coldAir = AirLayers.OverWater(20f, 5f, 1013.25f, 3f);
        Assert.True(coldAir.ObukhovMetres < 0f && coldAir.ThermalLog < 0f, $"cold air over a warm lake: L {coldAir.ObukhovMetres:F0}");
        Assert.True(AirLayers.WaterTemperatureC(200) > AirLayers.WaterTemperatureC(172), "the lake is warmest after the air");
    }

    // ── The rule as the mixer reads it ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Nothing near moves: within 100 m in still neutral air nothing at all, and nowhere in still neutral air within the
    /// adiabatic lapse's edge (about 700 m for a car's exhaust) any shadow. Past 100 m the stir begins to swing a sound.
    /// </summary>
    [Fact]
    public void InStillNeutralAirNothingNearIsBent()
    {
        var weather = Weather(AirLayers.Neutral(15f), 0f);
        var src = new Vector3(0f, 0.5f, 0f);
        foreach (float d in new[] { 10f, 50f, 99f })
        {
            var bent = Bending.Along(weather, src, new Vector3(d, Ear, 0f), 0f, false, 500.0, C);
            Assert.Equal(Refraction.Bands.None, bent.TakenDb);
            Assert.Equal((0f, 0f), (bent.SourceRaise, bent.EarRaise));
        }
        var far = Bending.Along(weather, src, new Vector3(600f, Ear, 0f), 0f, false, 500.0, C);
        Assert.Equal(Refraction.Bands.None, far.ShadowDb);
        Assert.True(far.EdgeMetres > 650f, $"still neutral edge {far.EdgeMetres:F0} m");
    }

    /// <summary>
    /// The wind is the world's: on a sunny afternoon in a 4.5 m/s west wind, a car 300 m east of the ear (the sound going
    /// upwind) is in shadow, its treble deepest; the same car 300 m west (downwind) is not; turned round, the wind turns
    /// the shadow with it.
    /// </summary>
    [Fact]
    public void UpwindOnASunnyAfternoonAFarCarIsInShadow()
    {
        var layers = AirLayers.From(15f, 172, 45f, 0f, 0.4f, 25f, 1013.25f, 4.5f, 0.1f);
        var west = Weather(layers, 4.5f, fromDegrees: 270f);
        var src = new Vector3(0f, 0.5f, 0f);
        var upwind = Bending.Along(west, src, new Vector3(-300f, Ear, 0f), 0f, false, 1000.0, C);
        Assert.True(upwind.ShadowDb.Mid > 10f && upwind.ShadowDb.High >= upwind.ShadowDb.Mid && upwind.ShadowDb.Low < upwind.ShadowDb.Mid,
                    $"upwind 300 m: {upwind.ShadowDb}");
        var downwind = Bending.Along(west, src, new Vector3(300f, Ear, 0f), 0f, false, 1000.0, C);
        Assert.Equal(Refraction.Bands.None, downwind.ShadowDb);
        var east = Weather(layers, 4.5f, fromDegrees: 90f);
        Assert.Equal(Refraction.Bands.None, Bending.Along(east, src, new Vector3(-300f, Ear, 0f), 0f, false, 1000.0, C).ShadowDb);
    }

    /// <summary>
    /// A far sound rises and falls with the stir, a near one not at all: one standard deviation of the middle band's swing
    /// over ten minutes 300 m off in a breeze is between 1 and 3.5 dB, and with the mean power kept.
    /// </summary>
    [Fact]
    public void AFarSoundSwingsWithTheStirAndANearOneKeepsSteady()
    {
        var weather = Weather(Breezy, 4.5f, fromDegrees: 0f);
        var src = new Vector3(13.7f, 0.5f, 21.3f);
        double sum = 0, sq = 0, power = 0;
        const int N = 2000;
        for (int i = 0; i < N; i++)
        {
            float near = Bending.Along(weather, src, src + new Vector3(60f, 1.2f, 0f), 0f, false, i, C).SwingDb.Mid;
            Assert.Equal(0f, near);
            float far = Bending.Along(weather, src, src + new Vector3(300f, 1.2f, 0f), 0f, false, i * 1.7, C).SwingDb.Mid;
            sum += far; sq += far * far; power += Math.Pow(10.0, far / 10.0);
        }
        double sd = Math.Sqrt(sq / N - (sum / N) * (sum / N));
        Assert.InRange(sd, 1.0, 3.5);
        Assert.InRange(10.0 * Math.Log10(power / N), -1.5, 1.5);
    }

    /// <summary>
    /// The stir is the air's own: in still neutral air nothing swings a far sound at all, and on a calm clear night (0.5 m/s)
    /// the middle band 300 m off swings less than a decibel (one standard deviation), where a 10 m/s wind swings it the
    /// full 3 dB the law allows.
    /// </summary>
    [Fact]
    public void StillAirSwingsNothingAndACalmNightLittle()
    {
        var src = new Vector3(13.7f, 0.5f, 21.3f);
        var ear = src + new Vector3(300f, 1.2f, 0f);
        var still = Weather(AirLayers.Neutral(15f), 0f, fromDegrees: 0f);
        for (int i = 0; i < 50; i++)
            Assert.Equal(Refraction.Bands.None, Bending.Along(still, src, src + new Vector3(600f, 1.2f, 0f), 0f, false, i * 3.1, C).SwingDb);
        double Sd(WorldWeather weather)
        {
            double sum = 0, sq = 0;
            const int N = 2000;
            for (int i = 0; i < N; i++)
            {
                float swing = Bending.Along(weather, src, ear, 0f, false, i * 1.7, C).SwingDb.Mid;
                sum += swing; sq += swing * swing;
            }
            return Math.Sqrt(sq / N - (sum / N) * (sum / N));
        }
        double calm = Sd(Weather(AirLayers.From(3f, 172, 45f, 0f, 0.4f, 12f, 1013.25f, 0.5f, 0.1f), 0.5f, fromDegrees: 0f));
        double windy = Sd(Weather(AirLayers.From(3f, 172, 45f, 0f, 0.4f, 12f, 1013.25f, 10f, 0.5f), 10f, fromDegrees: 0f));
        Assert.True(calm < 1.0, $"calm night: {calm:F2} dB");
        Assert.True(windy > 2.5, $"windy: {windy:F2} dB");
    }

    // ── The stir from the surface layer ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The surface layer's eddies by similarity: in neutral air the wind swings 2.4 u* along the mean, 1.9 u* across it and
    /// 1.25 u* up (Panofsky and Dutton 1984), the temperature 2 |θ*|; in unstable air the vertical grows as
    /// (1 − 3 z/L)^(1/3) (Panofsky et al. 1977) and the temperature's falls toward Tillman's free-convection
    /// 0.95 |θ*| (−z/L)^(-1/3) (Kaimal and Finnigan 1994).
    /// </summary>
    [Fact]
    public void TheSurfaceLayersEddiesAreTheMeasuredOnes()
    {
        var neutral = new AirLayers.Layer(0f, 0f, 0f, 0f, float.PositiveInfinity, 0.5f, 0f).EddiesAt(2f);
        Assert.Equal(1.2f, neutral.Along, 4);
        Assert.InRange(neutral.Across / 0.5f, 1.85f, 1.95f);
        Assert.Equal(0.625f, neutral.Up, 4);
        Assert.Equal(0f, neutral.TemperatureK);
        var stable = new AirLayers.Layer(0f, 0f, 0f, 0f, 50f, 0.2f, 0.1f).EddiesAt(2f);
        Assert.Equal(0.2f, stable.TemperatureK, 4);
        Assert.Equal(0.25f, stable.Up, 4);
        // Unstable, z/L = -1: the vertical by Panofsky's law, the temperature within 5 % of the free-convection limit.
        var lifted = new AirLayers.Layer(0f, 0f, 0f, 0f, -10f, 0.2f, -0.5f).EddiesAt(10f);
        Assert.Equal(1.25f * 0.2f * MathF.Cbrt(4f), lifted.Up, 4);
        Assert.InRange(lifted.TemperatureK / (0.95f * 0.5f), 0.95f, 1.0f);
        // z/L = -10: within 3 % of it.
        var free = new AirLayers.Layer(0f, 0f, 0f, 0f, -1f, 0.2f, -0.5f).EddiesAt(10f);
        Assert.InRange(free.TemperatureK / (0.95f * 0.5f * MathF.Cbrt(0.1f)), 0.97f, 1.03f);
    }

    /// <summary>
    /// μ² is the Gaussian model's σ_T²/(4T²) + σ_v²/c², σ_v² the mean of the three components' variances (Ostashev and
    /// Wilson 2015): 1 K and 1 m/s each way at 20 °C and 343 m/s is 2.9e-6 + 8.5e-6. Off the weather, a calm clear night
    /// stirs the air less than a third of the least Daigle et al. (1983) measured near the ground (1e-6 to 1e-5), a breeze
    /// within that, a sunny calm afternoon ten times the night by its heat, a 10 m/s wind more than either, and still air
    /// not at all. The eddies' size is the way's mean height: 1.1 m for a car's exhaust heard standing, Daigle's figure.
    /// </summary>
    [Fact]
    public void CalmAirIsStirredLittleAndWindOrSunMuch()
    {
        float t = 293.15f;
        Assert.Equal(1f / (4f * t * t) + 1f / (343f * 343f), Refraction.StirVariance(1f, 1f, 1f, 1f, 20f, 343f), 9);
        Assert.Equal(1f / 3f / (343f * 343f), Refraction.StirVariance(1f, 0f, 0f, 0f, 20f, 343f), 9);
        float Mu2(float hour, float wind, float temperature)
            => Bending.StirVariance(AirLayers.From(hour, 172, 45f, 0f, 0.4f, temperature, 1013.25f, wind, 0.1f), false, 1.1f, C);
        float calmNight = Mu2(3f, 0.5f, 12f), calmDay = Mu2(15f, 0.5f, 20f), breezy = Mu2(15f, 4.5f, 20f), windy = Mu2(15f, 10f, 20f);
        Assert.True(calmNight < 3e-7f, $"calm night {calmNight:E2}");
        Assert.InRange(breezy, 1e-6f, 1e-5f);
        Assert.True(calmDay > 10f * calmNight, $"calm afternoon {calmDay:E2} against the night's {calmNight:E2}");
        Assert.True(windy > breezy && windy > calmDay, $"windy {windy:E2}");
        Assert.Equal(0f, Bending.StirVariance(AirLayers.Neutral(15f), false, 1.1f, C));
        Assert.Equal(Refraction.OuterScaleMetres, Refraction.OuterScale(0.5f * (0.5f + Ear)), 4);
        Assert.Equal(Refraction.MostOuterScaleMetres, Refraction.OuterScale(300f));
    }

    /// <summary>
    /// The thunder carries its own bending (Thunder.ShadowGain) and its parts are placed 40 m off in their directions
    /// (OneOffSounds.SkyProxyMetres): there the mixer's rule takes nothing, however the air lies.
    /// </summary>
    [Fact]
    public void TheThundersPlacedPartsAreNotBentAgain()
    {
        float proxy = OpenFPS.Audio.Core.OneOffSounds.SkyProxyMetres;
        Assert.True(proxy < Refraction.FlickerFromMetres);
        foreach (var layers in new[] { AirLayers.From(15f, 172, 45f, 0f, 0.4f, 30f, 1013.25f, 18f, 0.8f), AirLayers.Neutral() })
            foreach (float elevation in new[] { 5f, 20f, 60f })
            foreach (float side in new[] { -1f, 1f })
            {
                float a = elevation * MathF.PI / 180f;
                var ear = new Vector3(0f, Ear, 0f);
                var part = ear + new Vector3(side * MathF.Cos(a), MathF.Sin(a), 0f) * proxy;
                var bent = Bending.Along(Weather(layers, 18f, 270f), part, ear, 0f, false, 100.0, C);
                Assert.Equal(Refraction.Bands.None, bent.TakenDb);
            }
    }

    /// <summary>The stir signal is zero-mean and of unit spread over the map, as the swing's law assumes.</summary>
    [Fact]
    public void TheStirIsUnitNoise()
    {
        var wind = WindWeather.Steady(4.5f, 250f, 0.25f);
        var rng = new Random(5);
        double sum = 0, sq = 0;
        const int N = 20000;
        for (int i = 0; i < N; i++)
        {
            float n = WindField.Stir(wind, (float)(rng.NextDouble() * 4000 - 2000), (float)(rng.NextDouble() * 4000 - 2000), rng.NextDouble() * 3600);
            sum += n; sq += n * n;
        }
        Assert.InRange(sum / N, -0.05, 0.05);
        Assert.InRange(Math.Sqrt(sq / N - (sum / N) * (sum / N)), 0.85, 1.15);
    }

    /// <summary>A clear summer afternoon in a 4.5 m/s breeze.</summary>
    private static readonly AirLayers Breezy = AirLayers.From(15f, 172, 45f, 0f, 0.4f, 20f, 1013.25f, 4.5f, 0.1f);

    private static WorldWeather Weather(AirLayers layers, float windSpeed, float fromDegrees = 270f)
        => new() { Layers = layers, Wind = WindWeather.Steady(windSpeed, fromDegrees, WindAir.TurbulenceFor(0.1f)) };
}
