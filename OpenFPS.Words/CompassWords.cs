using System.Collections.Immutable;

namespace OpenFPS.Words;

/// <summary>A bearing said as a compass point, spelt one way everywhere ("north east", "west south west").</summary>
public static class CompassWords
{
    /// <summary>The sixteen points clockwise from north; the half-points by the same rule as the eight.</summary>
    public static readonly ImmutableArray<string> Sixteen = ImmutableArray.Create(
        "north", "north north east", "north east", "east north east",
        "east", "east south east", "south east", "south south east",
        "south", "south south west", "south west", "west south west",
        "west", "west north west", "north west", "north north west");

    /// <summary>The eight main points clockwise from north.</summary>
    public static readonly ImmutableArray<string> Eight = Every(2);

    /// <summary>North, east, south and west.</summary>
    public static readonly ImmutableArray<string> Four = Every(4);

    /// <summary>
    /// The nearest of <paramref name="points"/> (4, 8 or 16) to a bearing in degrees clockwise from north.
    /// <paramref name="midpoint"/> is Math.Round's rule on the point's index, for a bearing exactly between
    /// two: AwayFromZero takes the one clockwise, ToEven the even one (on eight, the cardinal).
    /// </summary>
    public static string Of(double degrees, int points = 8, MidpointRounding midpoint = MidpointRounding.AwayFromZero)
    {
        if (points is not (4 or 8 or 16)) throw new ArgumentOutOfRangeException(nameof(points), points, "4, 8 or 16");
        double a = ((degrees % 360.0) + 360.0) % 360.0;
        int i = (int)Math.Round(a / (360.0 / points), midpoint) % points;
        return Sixteen[i * (16 / points)];
    }

    private static ImmutableArray<string> Every(int step)
    {
        var b = ImmutableArray.CreateBuilder<string>(16 / step);
        for (int i = 0; i < 16; i += step) b.Add(Sixteen[i]);
        return b.MoveToImmutable();
    }
}
