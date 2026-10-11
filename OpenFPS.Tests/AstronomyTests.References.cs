using System.Globalization;
using OpenFPS.Astronomy;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The sun and the moon against published values (OpenFPS.Tests/Astronomy/References, fetched by
/// tools/astronomy/fetch_references.py; each file names its URLs): the US Naval Observatory for rise, set,
/// twilight, transit, phase, fraction and the moon's place, NOAA's solar calculator and the Naval Observatory
/// for the sun's.
/// </summary>
public partial class AstronomyTests
{
    private const double SunDegrees = 0.05, MoonDegrees = 0.2, FractionPercent = 1.0;
    private static readonly TimeSpan EventTolerance = TimeSpan.FromMinutes(2);

    private static string Refs([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "Astronomy", "References");

    /// <summary>The rows of a reference table by column name, comments and the header skipped.</summary>
    private static List<Dictionary<string, string>> Table(string file)
    {
        var lines = File.ReadAllLines(Path.Combine(Refs(), file)).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        var head = lines[0].Split('\t');
        return lines.Skip(1).Select(l => l.Split('\t'))
                    .Select(c => head.Select((h, i) => (h, v: i < c.Length ? c[i] : "")).ToDictionary(p => p.h, p => p.v))
                    .ToList();
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static DateTime Utc(string iso) => DateTime.SpecifyKind(DateTime.Parse(iso, CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private static double AngleBetween(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);

    [Fact]
    public void SunMatchesNoaasSolarCalculator()
    {
        var rows = Table("noaa-sun.tsv");
        Assert.True(rows.Count >= 40, $"only {rows.Count} NOAA rows");
        double worstAz = 0, worstEl = 0;
        foreach (var r in rows)
        {
            var p = Sun.Position(Utc(r["utc"]), D(r["lat"]), D(r["lon"]));
            double el = D(r["elevation"]);
            // NOAA's page reports the azimuth whatever the height; its own code is near-singular at the zenith only.
            worstAz = Math.Max(worstAz, AngleBetween(p.Azimuth, D(r["azimuth"])));
            worstEl = Math.Max(worstEl, Math.Abs(p.Elevation - el));
        }
        Output.WriteLine($"NOAA: {rows.Count} instants, worst azimuth {worstAz:F4} deg, elevation {worstEl:F4} deg");
        Assert.True(worstAz <= SunDegrees, $"azimuth off by {worstAz:F4} deg");
        Assert.True(worstEl <= SunDegrees, $"elevation off by {worstEl:F4} deg");
    }

    [Fact]
    public void SunMatchesTheNavalObservatory()
    {
        var rows = Table("usno-celnav.tsv").Where(r => r["body"] == "Sun").ToList();
        Assert.True(rows.Count >= 40, $"only {rows.Count} sun rows");
        double worstAz = 0, worstEl = 0;
        foreach (var r in rows)
        {
            var p = Sun.Position(Utc(r["utc"]), D(r["lat"]), D(r["lon"]));
            // hc is from the earth's centre and airless; the sun's parallax is 0.0024 degree at most.
            double pa = r["pa"] == "---" ? 0 : D(r["pa"]);
            worstAz = Math.Max(worstAz, AngleBetween(p.Azimuth, D(r["zn"])));
            worstEl = Math.Max(worstEl, Math.Abs(p.TrueElevation - (D(r["hc"]) - pa)));
        }
        Output.WriteLine($"USNO sun: {rows.Count} instants, worst azimuth {worstAz:F4} deg, airless elevation {worstEl:F4} deg");
        Assert.True(worstAz <= SunDegrees, $"azimuth off by {worstAz:F4} deg");
        Assert.True(worstEl <= SunDegrees, $"elevation off by {worstEl:F4} deg");
    }

    [Fact]
    public void MoonMatchesTheNavalObservatory()
    {
        // Rows with a parallax are the moon above the horizon; below it the service gives none.
        var rows = Table("usno-celnav.tsv").Where(r => r["body"] == "Moon" && r["pa"] != "---").ToList();
        Assert.True(rows.Count >= 30, $"only {rows.Count} moon rows");
        double worstAz = 0, worstEl = 0, worstApparent = 0;
        foreach (var r in rows)
        {
            var p = Moon.Position(Utc(r["utc"]), D(r["lat"]), D(r["lon"]));
            double topocentric = D(r["hc"]) - D(r["pa"]);
            worstAz = Math.Max(worstAz, AngleBetween(p.Azimuth, D(r["zn"])));
            worstEl = Math.Max(worstEl, Math.Abs(p.TrueElevation - topocentric));
            // refr is a sextant's correction, negative: the seen moon stands that much higher.
            worstApparent = Math.Max(worstApparent, Math.Abs(p.Elevation - (topocentric - D(r["refr"]))));
        }
        Output.WriteLine($"USNO moon: {rows.Count} instants, worst azimuth {worstAz:F4} deg, topocentric elevation {worstEl:F4} deg, "
                       + $"refracted {worstApparent:F4} deg");
        Assert.True(worstAz <= MoonDegrees, $"azimuth off by {worstAz:F4} deg");
        Assert.True(worstEl <= MoonDegrees, $"elevation off by {worstEl:F4} deg");
        Assert.True(worstApparent <= MoonDegrees, $"refracted elevation off by {worstApparent:F4} deg");
    }

    [Fact]
    public void PhasesFallWhenTheNavalObservatorySays()
    {
        var rows = Table("usno-phases-2026.tsv");
        Assert.Equal(50, rows.Count);
        double worst = 0;
        foreach (var r in rows)
        {
            DateTime at = Utc(r["utc"]);
            var phase = r["phase"] switch
            {
                "New Moon" => MoonPhase.New, "First Quarter" => MoonPhase.FirstQuarter,
                "Full Moon" => MoonPhase.Full, "Last Quarter" => MoonPhase.LastQuarter,
                var other => throw new InvalidDataException(other),
            };
            DateTime mine = Moon.NextPhase(at.AddDays(-3), phase);
            worst = Math.Max(worst, Math.Abs((mine - at).TotalMinutes));
        }
        Output.WriteLine($"USNO phases 2026: 50 instants, worst {worst:F2} min");
        Assert.True(worst <= EventTolerance.TotalMinutes, $"a phase off by {worst:F2} min");
    }

    [Fact]
    public void FractionAndPhaseNameMatchTheNavalObservatory()
    {
        var rows = Table("usno-oneday.tsv");
        Assert.Equal(24, rows.Count);
        double worst = 0;
        var names = new List<string>();
        foreach (var r in rows)
        {
            var date = DateOnly.Parse(r["date"], CultureInfo.InvariantCulture);
            // The service gives the fraction at noon in the zone asked for.
            DateTime noon = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc).AddHours(-D(r["tz"]));
            var p = Moon.Position(noon, D(r["lat"]), D(r["lon"]));
            double usno = D(r["fracillum"].TrimEnd('%'));
            worst = Math.Max(worst, Math.Abs(p.Illuminated * 100 - usno));
            string expected = r["curphase"];
            string mine = p.Phase switch
            {
                MoonPhase.New => "New Moon", MoonPhase.Full => "Full Moon",
                var ph => System.Text.RegularExpressions.Regex.Replace(ph.ToString(), "(?<=[a-z])(?=[A-Z])", " "),
            };
            if (mine != expected) names.Add($"{r["place"]} {r["date"]}: {mine}, USNO {expected} ({p.Illuminated * 100:F2} %)");
        }
        Output.WriteLine($"USNO fraction: {rows.Count} days, worst {worst:F2} percentage points");
        Assert.True(worst <= FractionPercent, $"fraction off by {worst:F2} points");
        Assert.True(names.Count == 0, string.Join("\n", names));
    }

    [Fact]
    public void OneDayMatchesTheNavalObservatory()
    {
        var rows = Table("usno-oneday.tsv");
        var problems = new List<string>();
        double worst = 0;
        foreach (var r in rows)
        {
            var date = DateOnly.Parse(r["date"], CultureInfo.InvariantCulture);
            double lat = D(r["lat"]), lon = D(r["lon"]);
            var zone = TimeSpan.FromHours(D(r["tz"]));
            var sun = Sun.Day(date, lat, lon, zone);
            var moon = Moon.Day(date, lat, lon, zone);
            var sunEvents = Events(r["sun"]);
            var moonEvents = Events(r["moon"]);

            void Check(string what, DateTime? mine, Dictionary<string, string> usno, string key)
            {
                DateTime? theirs = usno.TryGetValue(key, out var hm) && hm != "None"
                    ? date.ToDateTime(TimeOnly.ParseExact(hm, "HH:mm", CultureInfo.InvariantCulture), DateTimeKind.Utc) - zone
                    : null;
                if (mine is null && theirs is null) return;
                if (mine is null || theirs is null)
                {
                    problems.Add($"{r["place"]} {r["date"]} {what}: mine {mine:HH:mm}, USNO {theirs:HH:mm}");
                    return;
                }
                double off = Math.Abs((mine.Value - theirs.Value).TotalMinutes);
                worst = Math.Max(worst, off);
                if (off > EventTolerance.TotalMinutes) problems.Add($"{r["place"]} {r["date"]} {what}: off {off:F1} min");
            }
            Check("sunrise", sun.Rise, sunEvents, "Rise");
            Check("sunset", sun.Set, sunEvents, "Set");
            Check("civil dawn", sun.CivilDawn, sunEvents, "Begin Civil Twilight");
            Check("civil dusk", sun.CivilDusk, sunEvents, "End Civil Twilight");
            // The service gives no transit on a day the sun stays below the horizon.
            if (sunEvents.ContainsKey("Upper Transit")) Check("noon", sun.Noon, sunEvents, "Upper Transit");
            Check("moonrise", moon.Rise, moonEvents, "Rise");
            Check("moonset", moon.Set, moonEvents, "Set");
        }
        Output.WriteLine($"USNO one day: {rows.Count} days, worst {worst:F2} min");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static Dictionary<string, string> Events(string field)
    {
        var d = new Dictionary<string, string>();
        foreach (var part in field.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=');
            d.TryAdd(kv[0], kv[1]);
        }
        return d;
    }

    public static IEnumerable<object[]> YearTables()
    {
        foreach (var place in new[] { "magnolia", "albany", "tromso" })
            foreach (var kind in new[] { "sun", "civil", "nautical", "astronomical", "moon" })
                yield return new object[] { place, kind };
    }

    /// <summary>
    /// Every rise and set of a whole year, both ways: each of the Naval Observatory's has one of ours within
    /// two minutes, and each of ours one of theirs, so a missed or an extra event fails too.
    /// </summary>
    [Theory]
    [MemberData(nameof(YearTables))]
    public void AYearOfRisesAndSetsMatchesTheNavalObservatory(string place, string kind)
    {
        var table = YearTable.Read(Path.Combine(Refs(), $"usno-year-{place}-{kind}.txt"));
        DateTime start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) - table.Zone;
        DateTime end = start.AddYears(1);
        double level = kind switch
        {
            "sun" => Sun.RiseAltitude, "civil" => Sun.CivilAltitude, "nautical" => Sun.NauticalAltitude,
            "astronomical" => Sun.AstronomicalAltitude, _ => double.NaN,
        };
        var problems = new List<string>();
        double worst = 0;
        int grazes = 0;
        var ours = new Dictionary<bool, List<DateTime>>();
        foreach (bool rising in new[] { true, false })
        {
            var mine = ours[rising] = new List<DateTime>();
            DateTime from = start;
            while (true)
            {
                double days = (end - from).TotalDays;
                DateTime? next = kind == "moon"
                    ? Moon.Next(from, table.Latitude, table.Longitude, rising, days)
                    : Sun.Next(from, table.Latitude, table.Longitude, rising, level, days);
                if (next is not { } n) break;
                mine.Add(n);
                from = n.AddMinutes(1);
            }
        }
        var allTheirs = table.Events.Select(e => e.Utc).ToList();
        var allOurs = ours[true].Concat(ours[false]).ToList();
        foreach (bool rising in new[] { true, false })
        {
            var mine = ours[rising];
            var theirs = table.Events.Where(e => e.Rising == rising).Select(e => e.Utc).ToList();
            string name = rising ? "rise" : "set";
            foreach (var t in theirs)
            {
                if (Graze(t, allTheirs)) { grazes++; continue; }
                double off = mine.Count == 0 ? double.MaxValue : mine.Min(m => Math.Abs((m - t).TotalMinutes));
                if (off <= EventTolerance.TotalMinutes) worst = Math.Max(worst, off);
                else problems.Add($"USNO {name} {t + table.Zone:MM-dd HH:mm} local: nearest of ours {off:F1} min away");
            }
            foreach (var m in mine)
            {
                // An event within the tolerance of the year's ends may be in the neighbouring year's table.
                if (m - start < EventTolerance || end - m < EventTolerance) continue;
                if (Graze(m, allOurs)) { grazes++; continue; }
                double off = theirs.Count == 0 ? double.MaxValue : theirs.Min(t => Math.Abs((m - t).TotalMinutes));
                if (off > EventTolerance.TotalMinutes)
                    problems.Add($"our {name} {m + table.Zone:MM-dd HH:mm} local: nearest of USNO's {off:F1} min away");
            }
        }
        Output.WriteLine($"{place} {kind}: {table.Events.Count} USNO events, worst {worst:F2} min, {grazes} in grazes left out");
        Assert.True(problems.Count == 0, $"{place} {kind} ({table.Url}):\n" + string.Join("\n", problems.Take(40)));
    }

    /// <summary>
    /// An event with another within ten minutes of it: the body grazing the level. Tromsø's astronomical night
    /// of 16 September 2026 lasts five minutes in the Naval Observatory's table, a dip of 0.001 degree at the
    /// sun's lowest, a tenth of either model's accuracy, so whether it happens at all is not a test of either.
    /// </summary>
    private static bool Graze(DateTime at, IEnumerable<DateTime> all)
        => all.Any(o => o != at && Math.Abs((o - at).TotalMinutes) < 10);

    /// <summary>A Naval Observatory year table: its place, its zone, and every event in UTC.</summary>
    private sealed record YearTable(string Url, double Latitude, double Longitude, TimeSpan Zone,
                                    List<(DateTime Utc, bool Rising)> Events)
    {
        public static YearTable Read(string path)
        {
            var lines = File.ReadAllLines(path);
            string url = lines[0].TrimStart('#', ' ');
            string loc = lines.First(l => l.StartsWith("Location:"));
            // "Location: W095 45, N30 13": the table's place, to the arcminute.
            var m = System.Text.RegularExpressions.Regex.Match(loc, @"([EW])(\d{3}) (\d{2}), ([NS])(\d{2}) (\d{2})");
            double lon = (int.Parse(m.Groups[2].Value) + int.Parse(m.Groups[3].Value) / 60.0) * (m.Groups[1].Value == "W" ? -1 : 1);
            double lat = (int.Parse(m.Groups[5].Value) + int.Parse(m.Groups[6].Value) / 60.0) * (m.Groups[4].Value == "S" ? -1 : 1);
            var z = System.Text.RegularExpressions.Regex.Match(lines.First(l => l.Contains("Zone:")), @"Zone:\s+(\d+)h (East|West)");
            var zone = TimeSpan.FromHours(int.Parse(z.Groups[1].Value) * (z.Groups[2].Value == "West" ? -1 : 1));
            var events = new List<(DateTime, bool)>();
            foreach (var line in lines)
            {
                if (line.Length < 2 || !char.IsDigit(line[0]) || !char.IsDigit(line[1])) continue;
                int day = int.Parse(line[..2]);
                for (int month = 1; month <= 12; month++)
                {
                    for (int col = 0; col < 2; col++)
                    {
                        int at = 4 + 11 * (month - 1) + 5 * col;
                        if (line.Length < at + 4) continue;
                        string cell = line.Substring(at, 4);
                        if (!cell.All(char.IsDigit)) continue;
                        var local = new DateTime(2026, month, day, int.Parse(cell[..2]), int.Parse(cell[2..]), 0, DateTimeKind.Utc);
                        events.Add((local - zone, col == 0));
                    }
                }
            }
            return new YearTable(url, lat, lon, zone, events);
        }
    }
}
