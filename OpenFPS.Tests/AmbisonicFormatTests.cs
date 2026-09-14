using System;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Tests;

/// <summary>
/// Cover for the ambisonic format layer — the one part of the soundfield path that cannot be checked
/// by ear, because getting it wrong does not sound broken. It sounds *vague*.
///
/// Steam Audio decodes N3D (ACN order, orthonormal harmonics). Recordings in the wild are AmbiX
/// (ACN/SN3D) or, if older, FuMa (WXYZ order, W attenuated). Feed AmbiX to an N3D decoder and the
/// directional channels are 1.73x too quiet — the field renders over-wide and badly localized, with
/// nothing to say why. Feed FuMa to an ACN decoder and the axes are permuted outright: front becomes up.
/// Neither mistake produces an error, so it gets asserted here instead.
///
/// The rotation and decode themselves need the native library and are verified by
/// `OpenFPS.AudioLab --ambisonic`.
/// </summary>
public class AmbisonicFormatTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 4)]
    [InlineData(2, 9)]
    [InlineData(3, 16)]
    public void AFullSphereSignalHasOrderPlusOneSquaredChannels(int order, int channels)
    {
        Assert.Equal(channels, AmbisonicFormat.ChannelsForOrder(order));
        Assert.Equal(order, AmbisonicFormat.OrderForChannels(channels));
    }

    [Theory]
    [InlineData(2)]   // stereo is not ambisonics, however much it has two channels
    [InlineData(3)]
    [InlineData(6)]   // 5.1 is not ambisonics either
    [InlineData(0)]
    public void AChannelCountThatIsNotAFullSphereIsRejected(int channels)
    {
        Assert.Equal(-1, AmbisonicFormat.OrderForChannels(channels));
    }

    [Fact]
    public void TheAcnChannelsMapToTheRightHarmonicOrders()
    {
        Assert.Equal(0, AmbisonicFormat.OrderOfAcnChannel(0));                  // W
        for (int c = 1; c <= 3; c++) Assert.Equal(1, AmbisonicFormat.OrderOfAcnChannel(c));
        for (int c = 4; c <= 8; c++) Assert.Equal(2, AmbisonicFormat.OrderOfAcnChannel(c));
        for (int c = 9; c <= 15; c++) Assert.Equal(3, AmbisonicFormat.OrderOfAcnChannel(c));
    }

    [Fact]
    public void Sn3dToN3dLeavesTheOmniChannelAloneAndLiftsTheRest()
    {
        // √(2n+1): 1, √3, √5, √7. If this is skipped, every directional component arrives 4.8 dB down.
        Assert.Equal(1f, AmbisonicFormat.Sn3dToN3dGain(0), 5);
        Assert.Equal(MathF.Sqrt(3f), AmbisonicFormat.Sn3dToN3dGain(2), 5);
        Assert.Equal(MathF.Sqrt(5f), AmbisonicFormat.Sn3dToN3dGain(5), 5);
        Assert.Equal(MathF.Sqrt(7f), AmbisonicFormat.Sn3dToN3dGain(10), 5);
    }

    [Fact]
    public void ConvertingAmbiXScalesTheDirectionalChannelsAndNotTheOmni()
    {
        // Two frames, four channels, all ones — so every change is visible as the gain itself.
        var samples = new float[] { 1, 1, 1, 1, 1, 1, 1, 1 };
        AmbisonicFormat.ConvertToN3d(samples, 4, AmbisonicLayout.AmbiX);

        float d = MathF.Sqrt(3f);
        Assert.Equal(new float[] { 1, d, d, d, 1, d, d, d }, samples, new FloatComparer(1e-5f));
    }

    [Fact]
    public void ConvertingN3dIsANoOp()
    {
        var samples = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        var before = (float[])samples.Clone();
        AmbisonicFormat.ConvertToN3d(samples, 4, AmbisonicLayout.N3D);
        Assert.Equal(before, samples);
    }

    [Fact]
    public void ConvertingFuMaReordersTheAxesAsWellAsRescalingThem()
    {
        // FuMa is W X Y Z; ACN wants W Y Z X. Distinct values so a permutation cannot hide.
        var samples = new float[] { 1f, 2f, 3f, 4f };  // W=1, X=2, Y=3, Z=4
        AmbisonicFormat.ConvertToN3d(samples, 4, AmbisonicLayout.FuMa);

        float d = MathF.Sqrt(3f);
        Assert.Equal(1f * MathF.Sqrt(2f), samples[0], 4);  // W, un-attenuated
        Assert.Equal(3f * d, samples[1], 4);               // Y -> ACN 1
        Assert.Equal(4f * d, samples[2], 4);               // Z -> ACN 2
        Assert.Equal(2f * d, samples[3], 4);               // X -> ACN 3
    }

    [Fact]
    public void FuMaAndAmbiXDoNotProduceTheSameThing()
    {
        // The point of having both: if these agreed, the layout field would be decoration.
        var asAmbix = new float[] { 1f, 2f, 3f, 4f };
        var asFuma = new float[] { 1f, 2f, 3f, 4f };
        AmbisonicFormat.ConvertToN3d(asAmbix, 4, AmbisonicLayout.AmbiX);
        AmbisonicFormat.ConvertToN3d(asFuma, 4, AmbisonicLayout.FuMa);
        Assert.NotEqual(asAmbix, asFuma);
    }

    [Theory]
    [InlineData("forest_ambix.wav", AmbisonicLayout.AmbiX)]
    [InlineData("forest_ACN_SN3D.wav", AmbisonicLayout.AmbiX)]
    [InlineData("street_FuMa.wav", AmbisonicLayout.FuMa)]
    [InlineData("street_fuma_foa.wav", AmbisonicLayout.FuMa)]
    [InlineData("rain_n3d.wav", AmbisonicLayout.N3D)]
    [InlineData("anything_at_all.wav", AmbisonicLayout.AmbiX)]   // the sane default
    public void TheLayoutIsGuessedFromTheFileName(string name, AmbisonicLayout expected)
    {
        Assert.Equal(expected, AmbisonicFormat.GuessLayout(name));
    }

    [Fact]
    public void AConversionNeverWalksOffTheEndOfARaggedBuffer()
    {
        // A truncated final frame must not throw — a half-written file should be quiet, not fatal.
        var samples = new float[] { 1, 1, 1, 1, 1, 1 }; // 1.5 frames of 4 channels
        var exception = Record.Exception(() => AmbisonicFormat.ConvertToN3d(samples, 4, AmbisonicLayout.AmbiX));
        Assert.Null(exception);

        AmbisonicFormat.ConvertToN3d(Array.Empty<float>(), 4, AmbisonicLayout.FuMa);
        AmbisonicFormat.ConvertToN3d(new float[] { 1, 2 }, 0, AmbisonicLayout.AmbiX);
    }

    private sealed class FloatComparer : IEqualityComparer<float>
    {
        private readonly float _tolerance;
        public FloatComparer(float tolerance) => _tolerance = tolerance;
        public bool Equals(float a, float b) => Math.Abs(a - b) <= _tolerance;
        public int GetHashCode(float v) => 0;
    }
}
