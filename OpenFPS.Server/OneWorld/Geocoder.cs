using System.Text.Json;

namespace OpenFPS.Server.OneWorld;

/// <summary>
/// An address to a place in the world: the US Census Bureau's geocoder (public, no key), the street
/// address ranges of TIGER/Line. Requests carry a generic User-Agent and the address only.
/// </summary>
public static class Geocoder
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("openfps-world/0.1");
        return c;
    }

    /// <summary>The place an address is, named as the geocoder matched it; null when it found none.
    /// Throws when it cannot be asked.</summary>
    public static async Task<WorldPlace?> FindAsync(string address, CancellationToken ct = default)
    {
        string url = "https://geocoding.geo.census.gov/geocoder/locations/onelineaddress?address="
                     + Uri.EscapeDataString(address) + "&benchmark=Public_AR_Current&format=json";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("addressMatches", out var matches)
            || matches.GetArrayLength() == 0) return null;
        var m = matches[0];
        var c = m.GetProperty("coordinates");
        string matched = m.GetProperty("matchedAddress").GetString() ?? address;
        return new WorldPlace("address", Spoken(matched), c.GetProperty("y").GetDouble(), c.GetProperty("x").GetDouble());
    }

    /// <summary>"1042 BELMONT AVE SW, ALBANY, OR, 97321" as it is written: words in title case, a compass
    /// direction and the state in capitals, the ZIP left off.</summary>
    public static string Spoken(string matched)
    {
        var parts = matched.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 1 && parts[^1].All(char.IsDigit)) parts.RemoveAt(parts.Count - 1);
        string Word(string w) => Directions.Contains(w) ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
        string Title(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Word));
        // The last part is the state, written as its two letters.
        return string.Join(", ", parts.Select((p, i) => i == parts.Count - 1 && p.Length == 2 && parts.Count > 1 ? p : Title(p)));
    }

    private static readonly HashSet<string> Directions = new(StringComparer.Ordinal) { "N", "S", "E", "W", "NE", "NW", "SE", "SW" };
}
