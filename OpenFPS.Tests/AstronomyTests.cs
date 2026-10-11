using OpenFPS.Astronomy;
using OpenFPS.Server.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>The sun and the moon (docs/SKY.md): the series as Meeus prints it, the polar cases, and the
/// sentence. The published values are in AstronomyTests.References.cs.</summary>
public partial class AstronomyTests
{
    private readonly ITestOutputHelper Output;

    public AstronomyTests(ITestOutputHelper output) => Output = output;

    private const double MagnoliaLat = 30.21, MagnoliaLon = -95.75;
    private const double TromsoLat = 69.65, TromsoLon = 18.96;

    private static readonly TimeZoneInfo Central = TimeZoneInfo.CreateCustomTimeZone("CDT", TimeSpan.FromHours(-5), "CDT", "CDT");

    /// <summary>Meeus, Astronomical Algorithms (2nd ed.), example 47.a: 1992 April 12 at 0h TD. Catches a
    /// mistyped term of the series, which the almanac comparisons might only show as a few hundredths.</summary>
    [Fact]
    public void TheLunarSeriesGivesMeeusExample47a()
    {
        var g = Moon.Geocentric(2448724.5);
        // The book's apparent longitude adds its full nutation (+0.004610); the short form is within 0.5".
        Assert.Equal(133.167265, g.Longitude, 0.0003);
        Assert.Equal(-3.229126, g.Latitude, 0.000002);
        Assert.Equal(368409.7, g.DistanceKm, 0.1);
        Assert.Equal(0.991990, g.Parallax, 0.000002);
        Assert.Equal(134.688470, g.RightAscension, 0.0004);
        Assert.Equal(13.768368, g.Declination, 0.0002);
    }

    [Fact]
    public void PolarDayAndNightHaveNoRiseOrSetAndNoNaN()
    {
        var summer = Sun.Day(new DateOnly(2026, 6, 21), TromsoLat, TromsoLon, TimeSpan.FromHours(1));
        Assert.Null(summer.Rise);
        Assert.Null(summer.Set);
        Assert.Null(summer.CivilDusk);
        var winter = Sun.Day(new DateOnly(2026, 12, 21), TromsoLat, TromsoLon, TimeSpan.FromHours(1));
        Assert.Null(winter.Rise);
        Assert.Null(winter.Set);
        // The polar night has a civil twilight at noon (USNO: 09:31 to 13:53 that day).
        Assert.NotNull(winter.CivilDawn);
        Assert.NotNull(winter.CivilDusk);

        foreach (double lat in new[] { 90.0, 89.99, 69.65, -69.65, -90.0 })
            for (int h = 0; h < 48; h += 5)
            {
                var at = new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc).AddHours(h);
                var s = Sun.Position(at, lat, TromsoLon);
                var m = Moon.Position(at, lat, TromsoLon);
                Assert.False(double.IsNaN(s.Azimuth + s.Elevation + m.Azimuth + m.Elevation + m.Illuminated), $"NaN at {lat} {at}");
            }

        // The polar night's first sunrise is in January (USNO's year table: 15 January, 11:29 local).
        DateTime? first = Sun.Next(new DateTime(2026, 12, 21, 12, 0, 0, DateTimeKind.Utc), TromsoLat, TromsoLon, rising: true);
        Assert.NotNull(first);
        Assert.Equal(1, first!.Value.Month);
    }

    [Fact]
    public void ThePolarNightIsSaidAsARiseWeeksAway()
    {
        var oslo = TimeZoneInfo.CreateCustomTimeZone("CET", TimeSpan.FromHours(1), "CET", "CET");
        string line = Sky.Readout(new DateTime(2026, 12, 21, 12, 0, 0, DateTimeKind.Utc), TromsoLat, TromsoLon, oslo);
        Output.WriteLine(line);
        Assert.StartsWith("The sun down, rises on January ", line);
    }

    [Theory]
    [InlineData(0, "north")]
    [InlineData(11.2, "north")]
    [InlineData(11.25, "north north east")]
    [InlineData(22.5, "north north east")]
    [InlineData(247.5, "west south west")]
    [InlineData(135, "south east")]
    [InlineData(348.75, "north")]
    [InlineData(-90, "west")]
    [InlineData(720 + 90, "east")]
    public void SixteenPoints(double azimuth, string word) => Assert.Equal(word, SkyWords.Compass(azimuth));

    /// <summary>The eight main points are the words the game already says (the weather's /wind, the
    /// editor and the driving aids spell them alike).</summary>
    [Fact]
    public void TheEightMainPointsAreTheGamesWords()
    {
        for (int k = 0; k < 8; k++)
            Assert.Equal(CommandHandler.CompassWord(k * 45f), SkyWords.Compass(k * 45));
    }

    [Theory]
    [InlineData(20.2, "twenty degrees up")]
    [InlineData(34.6, "thirty-five degrees up")]
    [InlineData(1.2, "one degree up")]
    [InlineData(0.3, "on the horizon")]
    [InlineData(-0.5, "on the horizon")]
    [InlineData(89.7, "straight overhead")]
    public void Heights(double elevation, string words) => Assert.Equal(words, SkyWords.Height(elevation));

    [Theory]
    [InlineData(70, "in an hour and ten minutes")]
    [InlineData(60, "in an hour")]
    [InlineData(5, "in five minutes")]
    [InlineData(1, "in a minute")]
    [InlineData(0.2, "in less than a minute")]
    [InlineData(121, "in two hours and a minute")]
    [InlineData(23 * 60 + 59, "in twenty-three hours and fifty-nine minutes")]
    public void TimesToCome(double minutes, string words) => Assert.Equal(words, SkyWords.In(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData(0.01, "barely lit")]
    [InlineData(0.13, "an eighth lit")]
    [InlineData(0.5, "half lit")]
    [InlineData(0.74, "three quarters lit")]
    [InlineData(0.99, "fully lit")]
    public void LitShares(double fraction, string words) => Assert.Equal(words, SkyWords.LitWords(fraction));

    [Fact]
    public void ClockTimesAreLocal()
    {
        var at = new DateTime(2026, 10, 11, 11, 42, 10, DateTimeKind.Utc);
        Assert.Equal("6:42 a.m.", SkyWords.Clock(at, Central));
        Assert.Equal("12:05 p.m.", SkyWords.Clock(new DateTime(2026, 10, 11, 17, 5, 0, DateTimeKind.Utc), Central));
        Assert.Equal("12:00 a.m.", SkyWords.Clock(new DateTime(2026, 10, 12, 4, 59, 50, DateTimeKind.Utc), Central));
    }

    /// <summary>The decision's own example (docs/REAL_WEATHER.md, "Decided (Cody, 2026-10-11)"), built from
    /// its numbers.</summary>
    [Fact]
    public void TheDecisionsExampleSentence()
    {
        var now = new DateTime(2026, 10, 11, 21, 0, 0, DateTimeKind.Utc);
        var moon = new MoonPosition(135, 35, 35, 384000, 0.95, 130, 50, 0.75, true, MoonPhase.WaxingGibbous);
        var sky = new SkyNow(now, new BodyNow(247.5, 20, true, now.AddMinutes(70)),
                             new BodyNow(135, 35, true, now.AddHours(5)), moon);
        Assert.Equal("The sun west south west, twenty degrees up, sets in an hour and ten minutes; "
                   + "the moon waxing gibbous, three quarters lit, south east, thirty-five degrees up, sets in five hours.",
                     SkyWords.Readout(sky, Central));

        var night = new SkyNow(now, new BodyNow(300, -20, false, now.AddHours(14).AddMinutes(42)),
                               new BodyNow(90, -5, false, now.AddHours(3)), moon with { Phase = MoonPhase.Full, Illuminated = 1 });
        Assert.Equal("The sun down, rises at 6:42 a.m.; the moon full, down, rises at 7:00 p.m.", SkyWords.Readout(night, Central));
    }

    [Fact]
    public void ABearingCanBeSaidFromWhereYouFace()
    {
        var now = new DateTime(2026, 10, 11, 21, 0, 0, DateTimeKind.Utc);
        var moon = new MoonPosition(135, 35, 35, 384000, 0.95, 130, 50, 0.75, true, MoonPhase.WaxingGibbous);
        var sky = new SkyNow(now, new BodyNow(247.5, 20, true, now.AddMinutes(70)), new BodyNow(135, 35, true, now.AddHours(5)), moon);
        string line = SkyWords.Readout(sky, Central, az => az > 180 ? "left" : "right");
        Assert.StartsWith("The sun left, twenty degrees up", line);
        Assert.Contains("three quarters lit, right, thirty-five degrees up", line);
    }

    /// <summary>
    /// Magnolia on the day this was written (05:30 CDT, before dawn; 1 p.m.; 8:30 p.m.), as a whole-path
    /// check: the readout reads the computed sky. USNO that day: sunrise 7:23, sunset 6:56, moonrise 8:20,
    /// moonset 7:21 p.m., CDT.
    /// </summary>
    [Fact]
    public void MagnoliaToday()
    {
        foreach (var at in new[] { new DateTime(2026, 10, 11, 10, 30, 0, DateTimeKind.Utc),
                                   new DateTime(2026, 10, 11, 18, 0, 0, DateTimeKind.Utc),
                                   new DateTime(2026, 10, 12, 1, 30, 0, DateTimeKind.Utc) })
        {
            var sky = Sky.At(at, MagnoliaLat, MagnoliaLon);
            string line = SkyWords.Readout(sky, Central);
            Output.WriteLine($"{at:yyyy-MM-dd HH:mm} UTC: {line}");
            Assert.Equal(sky.Sun.IsUp, !line.StartsWith("The sun down"));
            Assert.NotNull(sky.Sun.Next);
            Assert.NotNull(sky.Moon.Next);
            if (at.Hour == 10)
                Assert.Matches(@"^The sun down, rises at 7:2[2-4] a\.m\.; the moon waxing crescent, barely lit, down, rises at 8:(19|20|21) a\.m\.$", line);
        }
    }
}
