using System;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The shot's timing and geometry as numbers, not directions: sound over distance, the bullet's
/// flight plus the crack's hop, the crack-to-report gap and back, the Mach cone, and the crack's
/// length. Written for the survivors of the 2026-09-24 mutation run over Ballistics.
/// </summary>
public class BallisticsValueTests
{
    private const float C = 343f;

    [Fact]
    public void TheReportArrivesAtTheSpeedOfSound()
    {
        Assert.Equal(1f, Ballistics.ReportArrival(343f, C), 5);
        Assert.Equal(0f, Ballistics.ReportArrival(-10f, C));
        Assert.Equal(0f, Ballistics.ReportArrival(100f, 1f));
        Assert.Equal(100f / 1.5f, Ballistics.ReportArrival(100f, 1.5f), 3);
    }

    [Fact]
    public void TheCrackArrivesAfterTheBulletsFlightAndTheSoundsHop()
    {
        // 900 m/s over 90 m is 0.1 s; then 34.3 m of air is another 0.1 s.
        Assert.Equal(0.2f, Ballistics.CrackArrival(90f, 34.3f, 900f, C), 5);
        Assert.Equal(0.1f, Ballistics.CrackArrival(90f, -5f, 900f, C), 5);
        Assert.Equal(0.1f, Ballistics.CrackArrival(-5f, 34.3f, 900f, C), 5);
        Assert.Equal(0f, Ballistics.CrackArrival(90f, 34.3f, 1f, C));
        Assert.Equal(0f, Ballistics.CrackArrival(90f, 34.3f, 900f, 1f));
    }

    [Fact]
    public void OnlyASupersonicBulletPassingNearCracks()
    {
        Assert.True(Ballistics.MakesCrack(900f, 0f, C));
        Assert.True(Ballistics.MakesCrack(900f, Ballistics.MaxCrackMissDistance, C));
        Assert.False(Ballistics.MakesCrack(900f, Ballistics.MaxCrackMissDistance + 0.01f, C));
        Assert.False(Ballistics.MakesCrack(900f, -0.01f, C));
        // Within the subsonic margin is not a crack.
        Assert.False(Ballistics.MakesCrack(C + Ballistics.SubsonicMarginMetresPerSecond, 1f, C));
        Assert.True(Ballistics.MakesCrack(C + Ballistics.SubsonicMarginMetresPerSecond + 0.1f, 1f, C));
    }

    [Fact]
    public void TheCrackToReportGapAndBackAreOneAnother()
    {
        float gap = Ballistics.CrackToReportSeconds(100f, 900f, C);
        Assert.Equal(100f * (1f / C - 1f / 900f), gap, 6);
        Assert.Equal(100f, Ballistics.DistanceFromCrackToReport(gap, 900f, C), 2);
        Assert.Equal(0f, Ballistics.CrackToReportSeconds(100f, 1f, C));
        Assert.Equal(0f, Ballistics.CrackToReportSeconds(100f, 900f, 1f));
        Assert.Equal(0f, Ballistics.CrackToReportSeconds(-1f, 900f, C));
        Assert.Equal(0f, Ballistics.DistanceFromCrackToReport(0.1f, C, C));        // no gap at Mach 1
        Assert.Equal(0f, Ballistics.DistanceFromCrackToReport(-0.1f, 900f, C));
    }

    [Fact]
    public void TheMachConeIsAsinOfTheSpeedRatio()
    {
        Assert.Equal(MathF.Asin(C / 900f), Ballistics.MachConeAngle(900f, C), 5);
        Assert.Equal(MathF.PI / 2f, Ballistics.MachConeAngle(C, C), 5);
        Assert.Equal(MathF.PI / 2f, Ballistics.MachConeAngle(300f, C), 5);
    }

    /// <summary>A fifth of a millisecond at a quarter metre, widening as the cube root of the miss,
    /// narrower the faster the bullet.</summary>
    [Fact]
    public void TheCracksLength()
    {
        float mach = 900f / C;
        Assert.Equal(0.0002f / mach, Ballistics.CrackDurationSeconds(0.25f, 900f, C), 7);
        Assert.Equal(0.0002f * 2f / mach, Ballistics.CrackDurationSeconds(2f, 900f, C), 7);        // 8x the miss, 2x the width
        Assert.Equal(0.0002f / mach, Ballistics.CrackDurationSeconds(0.01f, 900f, C), 7);        // held at a quarter metre
        Assert.Equal(0f, Ballistics.CrackDurationSeconds(1f, C, C));
        Assert.True(Ballistics.CrackDurationSeconds(1f, 1200f, C) < Ballistics.CrackDurationSeconds(1f, 900f, C));
    }
}
