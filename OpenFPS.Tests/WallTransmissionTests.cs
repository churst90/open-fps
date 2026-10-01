using System;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// What gets through a wall (WallTransmission): the single-panel and double-leaf models of Sharp 1978,
/// with EN 12354-1 losses and flanking, and the hand-rolled tracer that must answer with the same model.
/// </summary>
public class WallTransmissionTests
{
    private static float Db(float gain) => -20f * MathF.Log10(MathF.Max(1e-12f, gain));

    private static readonly Vector3 StoreyWall = new(16.86f, 2.73f, 0.35f);

    [Fact]
    public void BelowCoincidenceThePanelFollowsTheMassLawAtSixDecibelsAnOctave()
    {
        // 35 cm brick's coincidence is near 66 Hz, so pick a panel whose is high: 12.5 mm plasterboard,
        // about 2.7 kHz. Below half of it the mass law rules: 20 log10(2) = 6.02 dB per octave.
        var p = AcousticRegistry.GetProperties("Plaster");
        float m = p.DensityKgM3 * 0.0125f;
        float fc = WallTransmission.CriticalHz(p.YoungsModulusGPa * 1e9f, p.DensityKgM3, 0.0125f);
        float r1 = WallTransmission.SinglePanelDb(m, fc, p.LossFactor, 200f);
        float r2 = WallTransmission.SinglePanelDb(m, fc, p.LossFactor, 400f);
        Assert.Equal(6.02f, r2 - r1, 2);
        // And doubling the mass is the same six decibels.
        Assert.Equal(6.02f, WallTransmission.SinglePanelDb(2f * m, fc, p.LossFactor, 200f) - r1, 2);
    }

    [Fact]
    public void TheCoincidenceDipSitsWhereStiffnessDensityAndThicknessPutIt()
    {
        // fc = c² / (1.8 cL t), cL = sqrt(E/ρ). A 6 mm pane of glass (70 GPa, 2500 kg/m³) is the
        // textbook case: about 2 kHz. Brick 35 cm (15 GPa, 1900 kg/m³): about 66 Hz.
        var glass = AcousticRegistry.GetProperties("Glass");
        float fcGlass = WallTransmission.CriticalHz(glass.YoungsModulusGPa * 1e9f, glass.DensityKgM3, 0.006f);
        Assert.InRange(fcGlass, 1900f, 2200f);
        var brick = AcousticRegistry.GetProperties("Brick");
        Assert.InRange(WallTransmission.CriticalHz(brick.YoungsModulusGPa * 1e9f, brick.DensityKgM3, 0.35f), 60f, 72f);
        // Halving the thickness doubles it.
        Assert.Equal(2f * fcGlass, WallTransmission.CriticalHz(glass.YoungsModulusGPa * 1e9f, glass.DensityKgM3, 0.003f), 0);

        // And the dip — how far the panel falls under its own mass law — is deepest at fc.
        float m = glass.DensityKgM3 * 0.006f;
        float deepest = 0f, at = 0f;
        for (float f = 250f; f <= 16000f; f *= MathF.Pow(2f, 1f / 12f))
        {
            float dip = WallTransmission.MassLawDb(m, f) - WallTransmission.SinglePanelDb(m, fcGlass, glass.LossFactor, f);
            if (dip > deepest) { deepest = dip; at = f; }
        }
        Assert.InRange(at, fcGlass / 1.07f, fcGlass * 1.07f);
        Assert.True(deepest > 10f, $"a lightly damped pane should dip well under its mass law at coincidence, dipped {deepest:F1} dB");
    }

    [Fact]
    public void BrickMatchesSharpWithTheRegistrysOwnStiffnessAndDamping()
    {
        // Sharp 1978 with EN 12354-1's edge losses, for 35 cm brick: about 43 / 58 / 75 / 83 dB at
        // 125 / 500 / 2000 / 4000 Hz. Flanking takes about two decibels off a storey-high wall.
        var p = AcousticRegistry.GetProperties("Brick");
        float[] hz = { 125f, 500f, 2000f, 4000f };
        float[] sharp = { 43f, 58f, 75f, 83f };
        for (int i = 0; i < hz.Length; i++)
        {
            float r = WallTransmission.BoxLossDb(p, StoreyWall, WallBuild.Solid, hz[i]);
            Assert.InRange(r, sharp[i] - 4f, sharp[i] + 1f);
        }
    }

    [Fact]
    public void ThereIsNoFlatCeilingOnWhatAWallTakes()
    {
        // A flat 55 dB cap made every heavy wall the same filter in every band. Physically the loss
        // keeps rising with frequency: the top band must lose far more than the middle, and the middle
        // than the bottom.
        var (l, m, h) = WallTransmission.BandGains("Brick", StoreyWall, WallBuild.Solid);
        Assert.True(Db(m) - Db(l) > 15f, $"brick: low {Db(l):F1} mid {Db(m):F1}");
        Assert.True(Db(h) - Db(m) > 15f, $"brick: mid {Db(m):F1} high {Db(h):F1}");
        Assert.True(Db(h) > 70f, $"brick's top band should be well past any 55 dB cap, got {Db(h):F1}");
        var c = AcousticRegistry.GetProperties("Concrete");
        Assert.True(WallTransmission.BoxLossDb(c, new Vector3(10f, 3f, 0.3f), WallBuild.Solid, 8000f)
                    > WallTransmission.BoxLossDb(c, new Vector3(10f, 3f, 0.3f), WallBuild.Solid, 2000f) + 10f);
    }

    [Theory]
    [InlineData("Brick")]
    [InlineData("Concrete")]
    [InlineData("Plaster")]
    [InlineData("Wood")]
    public void AThickerWallNeverLetsMoreThrough(string material)
    {
        float last = 0f;
        for (float t = 0.05f; t <= 0.5f; t += 0.025f)
        {
            var (l, m, h) = WallTransmission.BandGains(material, new Vector3(10f, 3f, t), WallBuild.Solid);
            float total = Db(l) + Db(m) + Db(h);
            Assert.True(total >= last - 0.01f, $"{material} {t:F3} m: {total:F1} dB summed over the bands, after {last:F1}");
            last = total;
        }
        var p = AcousticRegistry.GetProperties(material);
        Assert.True(WallTransmission.BoxLossDb(p, new Vector3(10f, 3f, 0.3f), WallBuild.Solid, 1000f)
                    > WallTransmission.BoxLossDb(p, new Vector3(10f, 3f, 0.15f), WallBuild.Solid, 1000f));
    }

    [Fact]
    public void TwoLeavesResonateAtTheMassAirMassFrequency()
    {
        // f0 = 60 sqrt((m1 + m2) / (m1 m2 d)), the building-acoustics rule of thumb (d in m, m in kg/m²).
        float f0 = WallTransmission.MassAirMassHz(10f, 10f, 0.1f);
        Assert.InRange(f0, 60f * MathF.Sqrt(20f / (100f * 0.1f)) * 0.97f, 60f * MathF.Sqrt(20f / (100f * 0.1f)) * 1.03f);
        // Below it the wall is the mass law of its whole weight; well above, far better than that.
        var p = AcousticRegistry.GetProperties("Plaster");
        float m = p.DensityKgM3 * 0.0125f;
        float fc = WallTransmission.CriticalHz(p.YoungsModulusGPa * 1e9f, p.DensityKgM3, 0.0125f);
        float cavity = 0.1f;
        float below = WallTransmission.MassAirMassHz(m, m, cavity) * 0.7f;
        Assert.Equal(WallTransmission.SinglePanelDb(2f * m, fc, p.LossFactor, below),
                     WallTransmission.DoubleLeafDb(m, fc, p.LossFactor, cavity, 0.6f, below), 3);
        Assert.True(WallTransmission.DoubleLeafDb(m, fc, p.LossFactor, cavity, 100f, 500f)
                    > WallTransmission.SinglePanelDb(2f * m, fc, p.LossFactor, 500f) + 15f);
        // Studs bridge the leaves and cost the wall most of that.
        Assert.True(WallTransmission.DoubleLeafDb(m, fc, p.LossFactor, cavity, 0.6f, 500f)
                    < WallTransmission.DoubleLeafDb(m, fc, p.LossFactor, cavity, 100f, 500f) - 5f);
    }

    [Fact]
    public void AStudPartitionLetsTheBottomThroughAndStopsTheTop()
    {
        // Plasterboard on studs: published ~17 / 36 / 45 dB at 125 / 500 / 2000 Hz. Rumble comes
        // through, a voice's consonants do not.
        var stud = new WallBuild(0.0125f, 0.6f);
        var (l, m, h) = WallTransmission.BandGains("Plaster", StoreyWall, stud);
        Assert.True(Db(h) - Db(l) > 20f, $"stud wall: low {Db(l):F1} high {Db(h):F1}");
        Assert.InRange(Db(l), 10f, 30f);
        // And it is a far lighter barrier at the bottom than the solid 280 kg/m² slab the same box was.
        var (sl, _, _) = WallTransmission.BandGains("Plaster", StoreyWall, WallBuild.Solid);
        Assert.True(Db(sl) - Db(l) > 8f, $"solid {Db(sl):F1} against studs {Db(l):F1}");
        var p = AcousticRegistry.GetProperties("Plaster");
        Assert.InRange(WallTransmission.BoxLossDb(p, new Vector3(16.86f, 2.73f, 0.1f), stud, 125f), 12f, 26f);
        Assert.InRange(WallTransmission.BoxLossDb(p, new Vector3(16.86f, 2.73f, 0.1f), stud, 500f), 33f, 44f);
    }

    [Fact]
    public void ABoxTooThinForTwoLeavesIsOneSheet()
    {
        var p = AcousticRegistry.GetProperties("Plaster");
        var thin = new Vector3(20f, 0.03f, 80f);
        Assert.Equal(WallTransmission.BoxLossDb(p, thin, WallBuild.Solid, 500f),
                     WallTransmission.BoxLossDb(p, thin, new WallBuild(0.0125f, 0.6f), 500f), 3);
    }

    [Fact]
    public void FlankingAddsTheSameShareAtEveryFrequency()
    {
        // EN 12354-1, same construction all round: about two decibels on a storey-high wall, and the
        // larger the wall the less its edges matter.
        float share = WallTransmission.FlankingShare(16.86f, 2.73f);
        Assert.InRange(10f * MathF.Log10(1f + share), 1.2f, 3f);
        Assert.True(WallTransmission.FlankingShare(90f, 21f) < share);
    }

    [Fact]
    public void ThePorousKeepTheirTable()
    {
        var p = AcousticRegistry.GetProperties("Foliage");
        var (l, m, h) = WallTransmission.BandGains("Foliage", new Vector3(10f, 3f, 1f), WallBuild.Solid);
        Assert.Equal((p.TransmissionLow, p.TransmissionMid, p.TransmissionHigh), (l, m, h));
    }
}
