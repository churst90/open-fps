using System.Numerics;
using OpenFPS.Materials;
using OpenFPS.Acoustics;
using Complex = System.Numerics.Complex;

namespace OpenFPS.Tests;

/// <summary>
/// The ground by its impedance (GroundAnswer): the Faddeeva function against its known values, a lawn's dip, the copy
/// Cody approved left whole on hard ground, the floor, and a car driven away across a lawn moving smoothly.
/// </summary>
public class GroundAnswerTests
{
    private const float C = 343f, Ear = 1.7f;

    public GroundAnswerTests() => AcousticRegistry.Initialize();

    private static GroundAnswer.Bounce At(MaterialProperties ground, float source, float distance, float ear = Ear)
        => GroundAnswer.Reflect(0f, ground, new Vector3(0f, source, 0f), new Vector3(distance, ear, 0f), 0f, C)
           ?? throw new InvalidOperationException("no bounce");

    private static GroundAnswer.Bounce At(string material, float source, float distance, float ear = Ear)
        => At(AcousticRegistry.GetProperties(material), source, distance, ear);

    private static float Db(float gain) => 20f * MathF.Log10(gain);

    /// <summary>Sound and real ground, and sound and copy, per voice band, as the law judges them.</summary>
    private static (double[] Real, double[] Copy) Powers(string material, float source, float distance)
    {
        double[] real = new double[3], copy = new double[3];
        Assert.True(GroundAnswer.Powers(0f, AcousticRegistry.GetProperties(material), new Vector3(0f, source, 0f),
                                        new Vector3(distance, Ear, 0f), C, real, copy));
        return (real, copy);
    }

    /// <summary>
    /// Humlicek's W4 against W(z) = e^(-z²) erfc(-iz) where it is known exactly: W(i) = e erfc(1); W(1) = 1/e + i 2F(1)/√π
    /// with Dawson's F(1) = 0.5380795069; W(1+i) from the tables (and the integral i/π ∫ e^(-t²)/(z-t) dt); and below the
    /// axis W(1-i) = 2e^(2i) - conj W(1+i). Further out, W(3+2i), W(6+i) and W(0.3+0.1i) from the integral.
    /// </summary>
    [Theory]
    [InlineData(0.0, 1.0, 0.4275835762, 0.0)]
    [InlineData(1.0, 0.0, 0.3678794412, 0.6071577058)]
    [InlineData(1.0, 1.0, 0.3047442053, 0.2082189382)]
    [InlineData(1.0, -1.0, -1.1370378784, 2.0268137919)]
    [InlineData(3.0, 2.0, 0.0927107664, 0.1283169622)]
    [InlineData(6.0, 1.0, 0.0158851282, 0.0926287463)]
    [InlineData(0.3, 0.1, 0.8272460069, 0.2695998870)]
    public void FaddeevaMatchesItsKnownValues(double x, double y, double re, double im)
    {
        var w = GroundAnswer.Faddeeva(new Complex(x, y));
        Assert.True(Complex.Abs(w - new Complex(re, im)) < 2e-5, $"W({x}{y:+0.0;-0.0}i) = {w}, not ({re}, {im})");
    }

    /// <summary>Delany and Bazley's impedance at a lawn's 200 kPa s/m² and 500 Hz: 1 + 9.08 (2.5)^-0.75 + i 11.9 (2.5)^-0.73, 5.57 + 6.10i.</summary>
    [Fact]
    public void ALawnsImpedanceIsDelanyAndBazleys()
    {
        var z = GroundAnswer.Impedance(500f, 200f);
        Assert.Equal(1.0 + 9.08 * Math.Pow(2.5, -0.75), z.Real, 9);
        Assert.Equal(11.9 * Math.Pow(2.5, -0.73), z.Imaginary, 9);
        Assert.InRange(z.Real, 5.5, 5.6);
        Assert.InRange(z.Imaginary, 6.0, 6.2);
    }

    /// <summary>
    /// Grazing a lawn from 200 m, a 500 Hz wave comes back turned over, the ground wave keeping it short of -1; the bass
    /// comes back nearly whole and in step; and a rigid ground, or one stiff past any real one, hands the wave back whole.
    /// </summary>
    [Fact]
    public void ALawnTurnsTheMiddleOverAndHandsTheBassBack()
    {
        float mirrored = MathF.Sqrt(200f * 200f + 2.2f * 2.2f), grazing = 2.2f / mirrored;
        var middle = GroundAnswer.SphericalReflection(500f, 200f, grazing, mirrored, C);
        Assert.True(middle.Real < -0.5 && Complex.Abs(middle) < 1.2, $"Q(500 Hz) = {middle}");
        var bass = GroundAnswer.SphericalReflection(50f, 200f, grazing, mirrored, C);
        Assert.True(bass.Real > 0.5, $"Q(50 Hz) = {bass}");
        Assert.True(Complex.Abs(GroundAnswer.SphericalReflection(500f, 1e9f, grazing, mirrored, C) - 1) < 0.01);
        Assert.Equal(Complex.One, GroundAnswer.SphericalReflection(500f, float.PositiveInfinity, grazing, mirrored, C));
    }

    /// <summary>
    /// The lawn's dip, as still air would have it: a car's exhaust 0.5 m up heard by a standing ear 100 m off across grass
    /// is more than 8 dB under free field at 466 Hz (sound and ground together), and 3 dB or more over it at 71 Hz, where
    /// the ground still doubles the pressure.
    /// </summary>
    [Fact]
    public void ALawnsDipSitsInTheMiddle()
    {
        float r1 = MathF.Sqrt(100f * 100f + 1.2f * 1.2f), r2 = MathF.Sqrt(100f * 100f + 2.2f * 2.2f);
        double Excess(float hz)
        {
            var q = GroundAnswer.SphericalReflection(hz, 200f, 2.2f / r2, r2, C) * (r1 / r2);
            var p = Complex.One + q * Complex.FromPolarCoordinates(1.0, 2.0 * Math.PI * hz * (r2 - r1) / C);
            return 20.0 * Math.Log10(Complex.Abs(p));
        }
        Assert.True(Excess(466f) < -8.0, $"466 Hz: {Excess(466f):F1} dB");
        Assert.True(Excess(71f) > 3.0, $"71 Hz: {Excess(71f):F1} dB");
    }

    /// <summary>
    /// Across a lawn a far car loses its middle: more than 4 dB at 100 and 200 m, its bass within 2 dB of what it had.
    /// Near, the copy Cody approved says it all and nothing is taken.
    /// </summary>
    [Fact]
    public void AFarCarAcrossALawnLosesItsMiddleANearOneNothing()
    {
        foreach (float d in new[] { 100f, 200f })
        {
            var far = At("Grass", 0.5f, d);
            Assert.True(Db(far.ExcessMid) < -4f, $"{d} m: middle {Db(far.ExcessMid):F1} dB");
            Assert.True(Db(far.ExcessLow) > -2f, $"{d} m: bass {Db(far.ExcessLow):F1} dB");
        }
        foreach (float d in new[] { 2f, 5f, 10f })
        {
            var near = At("Grass", 0.5f, d);
            Assert.True(Db(near.ExcessLow) > -0.2f && Db(near.ExcessMid) > -0.2f && Db(near.ExcessHigh) > -0.2f,
                        $"{d} m: {Db(near.ExcessLow):F1}/{Db(near.ExcessMid):F1}/{Db(near.ExcessHigh):F1} dB");
        }
    }

    /// <summary>
    /// Water is rigid, and a rigid ground carries at least what the approved copy does: its answer is the old one, copy and
    /// all, for sources from 0.1 to 2 m and ears from 1 to 3 m, from 1 to 500 m away.
    /// </summary>
    [Fact]
    public void ARigidGroundGivesTheOldAnswer()
    {
        var water = AcousticRegistry.GetProperties("Water");
        var old = water;
        old.FlowResistivityKPa = null;
        foreach (float hs in new[] { 0.1f, 0.3f, 0.5f, 1f, 2f })
            foreach (float hr in new[] { 1f, 1.7f, 3f })
                foreach (float d in new[] { 1f, 2f, 5f, 10f, 20f, 50f, 100f, 200f, 500f })
                {
                    var now = At(water, hs, d, hr);
                    var was = At(old, hs, d, hr);
                    Assert.Equal(was.LowGain, now.LowGain);
                    Assert.Equal(was.HighGain, now.HighGain);
                    Assert.Equal(was.DelaySeconds, now.DelaySeconds);
                    Assert.True(now.ExcessLow == 1f && now.ExcessMid == 1f && now.ExcessHigh == 1f,
                                $"hs {hs} hr {hr} d {d}: {now.ExcessLow}/{now.ExcessMid}/{now.ExcessHigh}");
                }
    }

    /// <summary>
    /// Roads, car parks and concrete floors as before: for a source from 0.3 to 2 m up and a standing ear, from 2 to 200 m,
    /// no band of asphalt or concrete loses more than 0.75 dB.
    /// </summary>
    [Theory]
    [InlineData("Asphalt")]
    [InlineData("Concrete")]
    public void HardGroundIsAsBefore(string material)
    {
        foreach (float hs in new[] { 0.3f, 0.5f, 1f, 2f })
            foreach (float d in new[] { 2f, 3f, 5f, 7f, 10f, 15f, 20f, 30f, 50f, 70f, 100f, 150f, 200f })
            {
                var b = At(material, hs, d);
                float least = MathF.Min(Db(b.ExcessLow), MathF.Min(Db(b.ExcessMid), Db(b.ExcessHigh)));
                Assert.True(least > -0.75f, $"{material} hs {hs} d {d}: {Db(b.ExcessLow):F2}/{Db(b.ExcessMid):F2}/{Db(b.ExcessHigh):F2} dB");
            }
    }

    /// <summary>
    /// Water at a road's face (a gutter, a drain's grate, a runnel, 1 to 10 cm up) heard by a standing ear 10 to 200 m
    /// along the road: the city's roads are dense asphalt (Nord2000's H, Cody 2026-10-16), which takes at most a decibel
    /// from any band. Class G, "most normal asphalt", took 2 to 10 dB off the top.
    /// </summary>
    [Fact]
    public void ARoadKeepsTheTopOfWaterRunningInIt()
    {
        foreach (float hs in new[] { 0.01f, 0.03f, 0.05f, 0.1f })
            foreach (float d in new[] { 10f, 25f, 50f, 100f, 150f, 200f })
            {
                var b = At("Asphalt", hs, d);
                float least = MathF.Min(Db(b.ExcessLow), MathF.Min(Db(b.ExcessMid), Db(b.ExcessHigh)));
                Assert.True(least > -1f, $"hs {hs} d {d}: {Db(b.ExcessLow):F2}/{Db(b.ExcessMid):F2}/{Db(b.ExcessHigh):F2} dB");
            }
    }

    /// <summary>A source 5 cm up over snow, from 300 m to 3 km: sound and ground never fall more than 20 dB under free
    /// field in any band, the copy and what is taken together.</summary>
    [Fact]
    public void NoBandLosesMoreThanTwentyDecibels()
    {
        foreach (float d in new[] { 300f, 1000f, 3000f })
        {
            var b = At("Snow", 0.05f, d);
            var (real, copy) = Powers("Snow", 0.05f, d);
            float[] excess = { b.ExcessLow, b.ExcessMid, b.ExcessHigh };
            for (int i = 0; i < 3; i++)
            {
                Assert.True(real[i] >= GroundAnswer.LeastBandPower, $"{d} m band {i}: real {real[i]}");
                Assert.True(copy[i] * excess[i] * excess[i] >= GroundAnswer.LeastBandPower * 0.999,
                            $"{d} m band {i}: carried {copy[i] * excess[i] * excess[i]}");
            }
        }
    }

    /// <summary>Where the ground takes, sound and copy carry what sound and the real ground do.</summary>
    [Fact]
    public void WhatIsTakenLeavesTheRealGround()
    {
        var b = At("Grass", 0.5f, 100f);
        var (real, copy) = Powers("Grass", 0.5f, 100f);
        Assert.True(real[1] < copy[1]);
        Assert.Equal(real[1], copy[1] * b.ExcessMid * b.ExcessMid, 4);
    }

    /// <summary>
    /// The copy's power is reckoned as the voice's own stage plays it: sines swept through GroundReflection (its one-pole
    /// split, its delay line) carry, band by band, what the law says sound and copy carry, within half a decibel.
    /// </summary>
    [Theory]
    [InlineData("Grass", 0.5f, 100f)]
    [InlineData("Grass", 0.3f, 25f)]
    [InlineData("Asphalt", 0.3f, 10f)]
    [InlineData("Dirt", 1f, 4f)]
    public void TheCopyIsReckonedAsTheStagePlaysIt(string material, float source, float distance)
    {
        const float Rate = 48000f;
        var b = At(material, source, distance);
        var (_, copy) = Powers(material, source, distance);
        (double From, double To)[] bands =
        {
            (AcousticBands.LowThirdsHz[0] / Math.Pow(2.0, 1.0 / 6.0), AcousticBands.LowCrossoverHz),
            (AcousticBands.LowCrossoverHz, AcousticBands.HighCrossoverHz),
            (AcousticBands.HighCrossoverHz, AcousticBands.HighThirdsHz[^1] * Math.Pow(2.0, 1.0 / 6.0)),
        };
        for (int band = 0; band < 3; band++)
        {
            const int Sines = 48;
            double sum = 0.0;
            for (int i = 0; i < Sines; i++)
            {
                double hz = bands[band].From * Math.Pow(bands[band].To / bands[band].From, (i + 0.5) / Sines);
                var g = new OpenFPS.Audio.Acoustics.GroundReflection(Rate);
                g.Set(b.DelaySeconds, b.LowGain, b.HighGain);
                double inE = 0.0, outE = 0.0;
                for (int n = 0; n < 12000; n++)
                {
                    float x = (float)Math.Sin(2.0 * Math.PI * hz * n / Rate);
                    float y = g.Process(x);
                    if (n < 4000) continue;
                    inE += x * (double)x; outE += y * (double)y;
                }
                sum += outE / inE;
            }
            double played = 10.0 * Math.Log10(sum / Sines), reckoned = 10.0 * Math.Log10(copy[band]);
            Assert.True(Math.Abs(played - reckoned) < 0.5, $"band {band}: the stage plays {played:F2} dB, the law reckons {reckoned:F2} dB");
        }
    }

    /// <summary>A car driven away across a lawn from 2 m to 200 m in quarter-metre steps: nothing taken jumps by more
    /// than a decibel a step.</summary>
    [Fact]
    public void ACarDrivenAwayAcrossALawnMovesSmoothly()
    {
        var last = At("Grass", 0.5f, 2f);
        for (float d = 2.25f; d <= 200f; d += 0.25f)
        {
            var now = At("Grass", 0.5f, d);
            Assert.True(MathF.Abs(Db(now.ExcessLow) - Db(last.ExcessLow)) < 1f
                        && MathF.Abs(Db(now.ExcessMid) - Db(last.ExcessMid)) < 1f
                        && MathF.Abs(Db(now.ExcessHigh) - Db(last.ExcessHigh)) < 1f, $"step to {d} m");
            last = now;
        }
    }

    /// <summary>The column: Nord2000's classes on the grounds, water and ice rigid, and nothing on a floor or a wall,
    /// which answers by its absorption as before (no excess, the copy as it was).</summary>
    [Fact]
    public void TheGroundsCarryTheirFlowResistivitiesAndNothingElseDoes()
    {
        (string Name, float Kpa)[] grounds =
        {
            ("Grass", 200f), ("Foliage", 31.5f), ("Dirt", 500f), ("Gravel", 500f), ("Sand", 300f), ("Snow", 12.5f),
            ("Clay", 2000f), ("Asphalt", 200000f), ("Concrete", 200000f),
            ("Water", float.PositiveInfinity), ("Ice", float.PositiveInfinity),
        };
        foreach (var (name, kpa) in grounds) Assert.Equal(kpa, AcousticRegistry.GetProperties(name).FlowResistivityKPa);
        foreach (var name in new[] { "Tile", "Marble", "Wood", "Brick", "Carpet", "Metal", "Generic" })
        {
            Assert.Null(AcousticRegistry.GetProperties(name).FlowResistivityKPa);
            var b = At(name, 0.5f, 100f);
            Assert.True(b.ExcessLow == 1f && b.ExcessMid == 1f && b.ExcessHigh == 1f);
        }
    }
}
