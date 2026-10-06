using System;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The band-limited resampler the synthesised one-shots go through on their way to the mixer
/// (MixerQuality.Resample; docs/AUDIO_QUALITY_2026-10-06.md). FMOD's own resampler, even its spline,
/// imaged a 48 kHz buffer's top octave 3.9 kHz down at only 24-28 dB under itself; these pin what the
/// replacement has to do instead.
/// </summary>
public class MixerQualityTests
{
    private static float[] Tone(int rate, double hz, double seconds)
    {
        var x = new float[(int)(rate * seconds)];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate));
        return x;
    }

    /// <summary>Level of one frequency in the middle of a buffer, dB re a full-scale sine's 0.5 amplitude,
    /// through a Blackman window (a single-bin DFT).</summary>
    private static double LevelDb(float[] y, int rate, double hz)
    {
        int a = y.Length / 4, n = y.Length / 2;
        double re = 0, im = 0, wsum = 0;
        for (int i = 0; i < n; i++)
        {
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (n - 1));
            double ph = 2 * Math.PI * hz * (a + i) / rate;
            re += w * y[a + i] * Math.Cos(ph);
            im += w * y[a + i] * Math.Sin(ph);
            wsum += w;
        }
        double amp = 2 * Math.Sqrt(re * re + im * im) / wsum;
        return 20 * Math.Log10(Math.Max(1e-12, amp / 0.5));
    }

    [Fact]
    public void ADownsampledToneKeepsItsLevelAndPitch()
    {
        var y = MixerQuality.Resample(Tone(48000, 1000, 1.0), 48000, 44100);
        Assert.InRange(y.Length, 44099, 44101);
        Assert.InRange(LevelDb(y, 44100, 1000), -0.05, 0.05);
        // And nothing of it where it would be if the rate had been taken wrong.
        Assert.True(LevelDb(y, 44100, 1088.4) < -60);
    }

    [Fact]
    public void TheTopOfTheBandIsKept()
    {
        var y = MixerQuality.Resample(Tone(48000, 19000, 1.0), 48000, 44100);
        Assert.InRange(LevelDb(y, 44100, 19000), -0.5, 0.5);
    }

    [Fact]
    public void WhatIsAboveTheNewNyquistDoesNotFoldBack()
    {
        // 23 kHz exists at 48 kHz and not at 44.1 kHz; a polynomial resampler folds it to 21.1 kHz.
        var y = MixerQuality.Resample(Tone(48000, 23000, 1.0), 48000, 44100);
        Assert.True(LevelDb(y, 44100, 21100) < -70, $"23 kHz folded to 21.1 kHz at {LevelDb(y, 44100, 21100):F1} dB");
    }

    [Fact]
    public void TheTopOctaveIsNotImagedDown()
    {
        // FMOD's spline put a 15 kHz tone's image at 11.1 kHz, -28 dB.
        var y = MixerQuality.Resample(Tone(48000, 15000, 1.0), 48000, 44100);
        Assert.True(LevelDb(y, 44100, 11100) < -80, $"image at {LevelDb(y, 44100, 11100):F1} dB");
    }

    [Fact]
    public void UpsamplingLeavesNoImageAboveTheOldNyquist()
    {
        // Thunder renders at 24 kHz. A 4 kHz component's image sits at 20 kHz.
        var y = MixerQuality.Resample(Tone(24000, 4000, 1.0), 24000, 44100);
        Assert.InRange(LevelDb(y, 44100, 4000), -0.1, 0.1);
        Assert.True(LevelDb(y, 44100, 20000) < -70, $"image at {LevelDb(y, 44100, 20000):F1} dB");
    }

    [Fact]
    public void TheSameRateIsLeftAlone()
    {
        var x = Tone(44100, 440, 0.1);
        Assert.Same(x, MixerQuality.Resample(x, 44100, 44100));
    }

    [Theory]
    [InlineData("linear", FMOD.DSP_RESAMPLER.LINEAR)]
    [InlineData("Cubic", FMOD.DSP_RESAMPLER.CUBIC)]
    [InlineData("spline", FMOD.DSP_RESAMPLER.SPLINE)]
    [InlineData(null, FMOD.DSP_RESAMPLER.SPLINE)]
    [InlineData("nonsense", FMOD.DSP_RESAMPLER.SPLINE)]
    public void TheResamplerIsSplineUnlessAskedOtherwise(string? name, FMOD.DSP_RESAMPLER expected)
        => Assert.Equal(expected, MixerQuality.Parse(name));
}
