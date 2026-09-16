using System;
using System.Linq;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A crowd clapping, and the three things that make it worth synthesizing rather than recording.
/// </summary>
public class ApplauseTests
{
    private const int Sr = 44100;

    /// <summary>
    /// Density is a parameter, not a crossfade between two samples. Polite clapping and an ovation
    /// are one sound at two arrival rates, and everything in between exists.
    /// </summary>
    [Fact]
    public void AnOvationIsDenserThanPoliteClapping()
    {
        // Crest factor: the peak against the RMS. A handful of separate claps is spiky — long
        // stretches of nothing with events sticking out of it — while a roar approaches noise, whose
        // crest is fixed and low. It is the same statistic that says whether you can pick individual
        // people out, which is exactly the difference being asserted.
        float Crest(float intensity)
        {
            var buf = Applause.Render(new CrowdApplause(200, intensity, 3f), Sr, 7);
            float peak = buf.Max(MathF.Abs);
            float rms = MathF.Sqrt(buf.Sum(v => v * v) / buf.Length);
            return rms < 1e-9f ? 0f : peak / rms;
        }

        Assert.True(Crest(0.05f) > Crest(1f),
            "polite clapping should be spikier than an ovation, because you can still hear the claps");
    }

    /// <summary>
    /// A crowd twice the size is three decibels louder, not twice as loud. Independent sources have
    /// unrelated phases, so they add in POWER — which is why a stadium is loud and not deafening.
    /// </summary>
    [Fact]
    public void TwiceTheCrowdIsThreeDecibelsAndNotSixty()
    {
        float five = Applause.LevelDb(500, 0.5f);
        float ten = Applause.LevelDb(1000, 0.5f);
        Assert.InRange(ten - five, 2.5f, 3.5f);
    }

    /// <summary>It never repeats. The same crowd at the same intensity rendered twice with different
    /// seeds is two different sounds, which is the whole reason not to use a loop.</summary>
    [Fact]
    public void TwoRendersAreNotTheSameSound()
    {
        var a = Applause.Render(new CrowdApplause(300, 0.6f, 2f), Sr, 1);
        var b = Applause.Render(new CrowdApplause(300, 0.6f, 2f), Sr, 2);

        int same = a.Zip(b, (x, y) => MathF.Abs(x - y) < 1e-6f ? 1 : 0).Sum();
        Assert.True(same < a.Length / 10, "two seeds produced substantially the same buffer");
    }

    /// <summary>...but the same seed is the same sound, so two players standing together hear one
    /// crowd rather than two.</summary>
    [Fact]
    public void TheSameSeedIsTheSameSound()
    {
        var a = Applause.Render(new CrowdApplause(300, 0.6f, 1f), Sr, 11);
        var b = Applause.Render(new CrowdApplause(300, 0.6f, 1f), Sr, 11);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ItSwellsAndDiesRatherThanSwitchingOnAndOff()
    {
        var buf = Applause.Render(new CrowdApplause(400, 0.8f, 4f), Sr, 3);
        float Energy(int from, int len) => buf.Skip(from).Take(len).Sum(v => v * v) / len;

        float start = Energy(0, Sr / 10);
        float middle = Energy(buf.Length / 2, Sr / 10);
        float end = Energy(buf.Length - Sr / 10, Sr / 10);

        Assert.True(middle > start * 2f, "it should swell rather than start at full");
        Assert.True(middle > end * 2f, "...and die away rather than stop dead");
    }

    /// <summary>A clap is a few milliseconds of contact and a small resonance, so the energy sits
    /// where hands are: hundreds of hertz to a few kilohertz, not down in the bass.</summary>
    [Fact]
    public void ItSoundsLikeHandsAndNotLikeRumble()
    {
        var buf = Applause.Render(new CrowdApplause(300, 0.7f, 2f), Sr, 5);
        var bands = VehicleBody.Bands(buf, Sr);

        Assert.True(bands.Low < 0.25f, $"{bands.Low:P0} of a crowd's energy was below 200 Hz");
        Assert.True(bands.Mid + bands.High > 0.7f, "a clap lives between a few hundred hertz and a few kilohertz");
    }

    [Theory]
    [InlineData(400, 0.5f, 3f)]
    [InlineData(1, 0f, 0.2f)]
    [InlineData(20000, 1f, 12f)]
    public void TheKeySurvivesARoundTrip(int people, float intensity, float seconds)
    {
        var spec = new CrowdApplause(people, intensity, seconds);
        Assert.True(Applause.TryParseKey(Applause.Key(spec), out var back));
        Assert.Equal(spec.Clappers, back.Clappers);
        Assert.Equal(spec.Intensity, back.Intensity, 2);
        Assert.Equal(spec.Seconds, back.Seconds, 2);
    }

    [Fact]
    public void SomethingThatIsNotApplauseIsNotParsedAsApplause()
    {
        Assert.False(Applause.TryParseKey("weapon:akm", out _));
        Assert.False(Applause.TryParseKey("", out _));
        Assert.False(Applause.TryParseKey("applause:nonsense", out _));
    }
}
