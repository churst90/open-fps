using System.Globalization;
using OpenFPS.Words;

namespace OpenFPS.Astronomy;

/// <summary>
/// The sky said in words: "The sun west south west, twenty degrees up, sets in an hour and ten minutes; the
/// moon waxing gibbous, three quarters lit, south east, thirty-five degrees up, sets in five hours." Heights
/// and times to come in words, clock times in figures, which a screen reader says as a time.
/// </summary>
public static class SkyWords
{
    /// <summary>The nearest of the sixteen points to an azimuth in degrees from true north.</summary>
    public static string Compass(double azimuth) => CompassWords.Of(azimuth, 16);

    public static string Readout(SkyNow sky, TimeZoneInfo zone, Func<double, string>? bearing = null)
    {
        bearing ??= Compass;
        string sun = "the sun " + Body(sky.Sun, sky.Utc, zone, bearing);
        var m = sky.MoonDetail;
        string phase = PhaseWords(m.Phase);
        bool principal = m.Phase is MoonPhase.New or MoonPhase.FirstQuarter or MoonPhase.Full or MoonPhase.LastQuarter;
        string lit = principal ? "" : $", {LitWords(m.Illuminated)}";
        string moon = $"the moon {phase}{lit}, {Body(sky.Moon, sky.Utc, zone, bearing)}";
        string line = $"{sun}; {moon}";
        if (!line.EndsWith('.')) line += ".";
        return char.ToUpperInvariant(line[0]) + line[1..];
    }

    /// <summary>"west south west, twenty degrees up, sets in an hour" or "down, rises at 6:42 a.m.".</summary>
    internal static string Body(BodyNow b, DateTime now, TimeZoneInfo zone, Func<double, string> bearing)
    {
        string verb = b.IsUp ? "sets" : "rises";
        string when = b.Next is not { } next ? $"does not {(b.IsUp ? "set" : "rise")} for weeks"
            : next - now < TimeSpan.FromDays(1)
                ? b.IsUp ? $"{verb} {In(next - now)}" : $"{verb} at {Clock(next, zone)}"
                : $"{verb} on {Date(next, zone)} at {Clock(next, zone)}";
        return b.IsUp ? $"{bearing(b.Azimuth)}, {Height(b.Elevation)}, {when}" : $"down, {when}";
    }

    /// <summary>"twenty degrees up", "one degree up", "on the horizon", "straight overhead".</summary>
    public static string Height(double elevation)
    {
        int d = (int)Math.Round(elevation, MidpointRounding.AwayFromZero);
        if (d <= 0) return "on the horizon";
        if (d >= 90) return "straight overhead";
        return d == 1 ? "one degree up" : $"{Number(d)} degrees up";
    }

    /// <summary>"in an hour and ten minutes", "in five minutes", "in less than a minute".</summary>
    public static string In(TimeSpan span)
    {
        int total = (int)Math.Round(Math.Max(0, span.TotalMinutes), MidpointRounding.AwayFromZero);
        if (total == 0) return "in less than a minute";
        int h = total / 60, m = total % 60;
        string hours = h == 1 ? "an hour" : $"{Number(h)} hours";
        string minutes = m == 1 ? "a minute" : $"{Number(m)} minutes";
        return h == 0 ? $"in {minutes}" : m == 0 ? $"in {hours}" : $"in {hours} and {minutes}";
    }

    /// <summary>"6:42 a.m.", "12:05 p.m.".</summary>
    public static string Clock(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        local = local.AddSeconds(30);
        int h12 = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        return $"{h12}:{local.Minute:00} {(local.Hour < 12 ? "a.m." : "p.m.")}";
    }

    private static string Date(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).AddSeconds(30);
        return local.ToString("MMMM d", CultureInfo.InvariantCulture);
    }

    public static string PhaseWords(MoonPhase phase) => phase switch
    {
        MoonPhase.New => "new",
        MoonPhase.WaxingCrescent => "waxing crescent",
        MoonPhase.FirstQuarter => "first quarter",
        MoonPhase.WaxingGibbous => "waxing gibbous",
        MoonPhase.Full => "full",
        MoonPhase.WaningGibbous => "waning gibbous",
        MoonPhase.LastQuarter => "last quarter",
        _ => "waning crescent",
    };

    private static readonly string[] Eighths =
        { "", "an eighth lit", "a quarter lit", "three eighths lit", "half lit", "five eighths lit", "three quarters lit", "seven eighths lit" };

    /// <summary>The lit share to the nearest eighth: "three quarters lit"; "barely lit" and "fully lit" at the ends.</summary>
    public static string LitWords(double fraction)
    {
        if (fraction < 0.03) return "barely lit";
        if (fraction > 0.97) return "fully lit";
        int e = Math.Clamp((int)Math.Round(fraction * 8, MidpointRounding.AwayFromZero), 1, 7);
        return Eighths[e];
    }

    private static readonly string[] Ones =
    {
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
        "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    };

    private static readonly string[] Tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };

    /// <summary>A whole number under a hundred in words: "thirty-five".</summary>
    public static string Number(int n)
    {
        if (n < 0 || n > 99) return n.ToString(CultureInfo.InvariantCulture);
        if (n < 20) return Ones[n];
        return n % 10 == 0 ? Tens[n / 10] : $"{Tens[n / 10]}-{Ones[n % 10]}";
    }
}
