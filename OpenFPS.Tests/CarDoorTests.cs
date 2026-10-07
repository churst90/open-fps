using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>The car door model (CarDoor), approved by ear on 2026-09-28.</summary>
public class CarDoorTests
{
    [Fact]
    public void TheKeysNameOpeningAndShutting()
    {
        Assert.True(CarDoor.TryParseKey(CarDoor.Key(closing: true), out bool c) && c);
        Assert.True(CarDoor.TryParseKey(CarDoor.Key(closing: false), out bool o) && !o);
        Assert.False(CarDoor.TryParseKey("cardoor:slam", out _));
        Assert.False(CarDoor.TryParseKey("weapon:akm", out _));
        Assert.False(CarDoor.TryParseKey(null, out _));
    }

    [Fact]
    public void ItRendersFiniteAtFullScaleAndVariesWithTheSeed()
    {
        foreach (bool closing in new[] { false, true })
        {
            var a = CarDoor.Render(closing, 48000, 3);
            Assert.All(a, v => Assert.True(float.IsFinite(v)));
            Assert.Equal(1f, a.Max(MathF.Abs), 4);
            Assert.Equal(a, CarDoor.Render(closing, 48000, 3));
            Assert.NotEqual(a, CarDoor.Render(closing, 48000, 4));
        }
    }

    /// <summary>
    /// Shutting is loudest in the first 50 ms and the cabin keeps the bottom going after it: from 50 to
    /// 140 ms, the band below 120 Hz is within 10 dB of the whole, as in the recording (-7 and -5 dB
    /// against the impact), where a ringing panel with nothing below it would be 20 dB down.
    /// </summary>
    [Fact]
    public void ShuttingKeepsItsLowEndAfterTheSlam()
    {
        var x = CarDoor.Render(closing: true, 48000, 3);
        double all = 0, low = 0;
        float lp = 0f, lp2 = 0f, a = 1f - MathF.Exp(-2f * MathF.PI * 120f / 48000f);
        for (int i = 0; i < x.Length; i++)
        {
            lp += a * (x[i] - lp); lp2 += a * (lp - lp2);
            if (i >= 0.055 * 48000 && i < 0.145 * 48000) { all += x[i] * x[i]; low += lp2 * lp2; }
        }
        Assert.InRange(10 * Math.Log10(low / all), -10.0, 0.5);
    }
}
