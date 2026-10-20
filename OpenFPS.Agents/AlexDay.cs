using System.Numerics;
using OpenFPS.Environment;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Alex's day, as a law: which place draws him at this hour and in this weather, how long he stays, when
/// he rides the bus rather than walks, and which way a draw's sequence starts. The host keeps his body,
/// the state of the day and every door and seat he uses.
/// </summary>
public static class AlexDay
{
    /// <summary>Where a draw at a bus stop sends him: a bus to ride, or the place he walks to.</summary>
    public readonly record struct NextMove(Haunt? RideTo, Haunt? WalkTo);

    /// <summary>Night, cold or wet: when somebody with nowhere to live looks for a roof.</summary>
    public static bool Sheltering(SpeechConditions c)
        => c.Hour >= 21f || c.Hour < 6f || c.TemperatureC <= 8f
           || (c.Weather is WeatherType.Rain or WeatherType.Storm or WeatherType.Snow && c.Precipitation >= 0.2f);

    /// <summary>How much a kind of place draws him, at this hour and in this weather.</summary>
    public static double KindWeight(HauntKind kind, SpeechConditions c)
    {
        if (Sheltering(c))
            return kind switch { HauntKind.Lobby => 6, HauntKind.BusStop => 2, HauntKind.Doorway => 0.6, _ => 0.15 };
        if (c.Hour >= 17f)
            return kind switch { HauntKind.Lobby => 1.5, HauntKind.BusStop => 3, HauntKind.Doorway => 2.5, _ => 1 };
        return kind switch { HauntKind.Lobby => 0.5, HauntKind.BusStop => 3, HauntKind.Doorway => 3, _ => 2 };
    }

    /// <summary>Of riding the bus from a bus stop rather than walking on: more in the cold and wet.</summary>
    public static double RideChance(SpeechConditions c) => Sheltering(c) ? 0.6 : 0.4;

    /// <summary>
    /// The next place, never the one he is at: weighted by kind for the hour and weather, and by
    /// distance (half as likely 150 m off).
    /// </summary>
    public static Haunt Choose(IReadOnlyList<Haunt> haunts, Haunt? current, Vector2? from, SpeechConditions cond, Random rng)
    {
        var weights = new double[haunts.Count];
        double total = 0;
        for (int i = 0; i < haunts.Count; i++)
        {
            var h = haunts[i];
            if (h == current && haunts.Count > 1) continue;
            double w = KindWeight(h.Kind, cond);
            if (from is { } f) w /= 1.0 + Vector2.Distance(f, new Vector2(h.Stand.X, h.Stand.Z)) / 150.0;
            weights[i] = w;
            total += w;
        }
        double pick = rng.NextDouble() * total;
        for (int i = 0; i < haunts.Count; i++)
        {
            pick -= weights[i];
            if (weights[i] > 0 && pick <= 0) return haunts[i];
        }
        return haunts[^1];
    }

    /// <summary>
    /// Time to go from <paramref name="at"/>: from a bus stop, now and then a bus to another stop; else
    /// the next place, walked to. Draws exactly as it always has, so the day stays the same.
    /// </summary>
    public static NextMove Leave(IReadOnlyList<Haunt> haunts, Haunt at, Vector2 from, SpeechConditions cond, Random rng)
    {
        var others = haunts.Where(h => h.Kind == HauntKind.BusStop && h != at).ToList();
        if (at.Kind == HauntKind.BusStop && others.Count > 0 && rng.NextDouble() < RideChance(cond))
            return new NextMove(others[rng.Next(others.Count)], null);
        return new NextMove(null, Choose(haunts, at, from, cond, rng));
    }

    /// <summary>How long he stays, seconds: minutes, longer in a lobby when it is night or cold.</summary>
    public static double LingerSeconds(HauntKind kind, SpeechConditions c, Random rng)
    {
        bool shelter = Sheltering(c);
        var (lo, hi) = kind switch
        {
            HauntKind.Lobby => shelter ? (8.0, 20.0) : (3.0, 8.0),
            HauntKind.BusStop => (3.0, 9.0),
            HauntKind.Doorway => shelter ? (2.0, 5.0) : (3.0, 8.0),
            _ => shelter ? (2.0, 4.0) : (4.0, 10.0),
        };
        return 60.0 * (lo + (hi - lo) * rng.NextDouble());
    }

    /// <summary>Where a character's day starts: seeded by the map, their name, the day and how many
    /// choices they have made, so the same day goes the same way.</summary>
    public static int Seed(int seed, string mapId, string name, int day, int decision)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char ch in mapId + "/" + name) { h ^= ch; h *= 16777619; }
            h ^= (uint)seed; h *= 16777619;
            h ^= (uint)day; h *= 16777619;
            h ^= (uint)decision; h *= 16777619;
            return (int)(h & 0x7fffffff);
        }
    }
}
