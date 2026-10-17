using System.Numerics;
using OpenFPS.Astronomy;
using OpenFPS.Client.Core;
using OpenFPS.Physics;
using OpenFPS.Server.Core;
using OpenFPS.Server.Editor;
using OpenFPS.Words;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Every place that turned a bearing into compass words did it its own way (2026-10-11, seven copies);
/// they all call CompassWords now. The old copies are kept here, verbatim, as the record: each caller's
/// inputs go through the caller as it is now and through its old copy, and the words must agree.
/// </summary>
public class CompassWordsTests
{
    private static class Old
    {
        public static readonly string[] Points8 =
            { "north", "north east", "east", "south east", "south", "south west", "west", "north west" };

        // CommandHandler.CompassWord (/weather and /wind).
        public static string Wind(float degrees) => Points8[(int)MathF.Round(((degrees % 360f) + 360f) % 360f / 45f) % 8];

        // The three that put an exact midpoint on the cardinal (MathF.Round's even rule) now put it on the
        // point clockwise, as the rest did (Cody, 2026-10-11: one rule). Nothing else they say changed.
        static int Clockwise(float deg) => (int)MathF.Round(deg / 45f, MidpointRounding.AwayFromZero) % 8;
        public static bool OnAMidpoint(float deg) => MathF.Abs(deg / 45f - MathF.Floor(deg / 45f) - 0.5f) < 1e-6f;
        public static string WindNow(float degrees) => Points8[Clockwise(((degrees % 360f) + 360f) % 360f)];
        public static float EditorDegrees(float yaw) { float deg = yaw * 180f / MathF.PI; return ((deg % 360f) + 360f) % 360f; }
        public static string EditorNow(float yaw) => Points8[Clockwise(EditorDegrees(yaw))];
        public static float DrivingDegrees(Vector3 f) { float deg = MathF.Atan2(f.X, f.Z) * 180f / MathF.PI; return deg < 0 ? deg + 360f : deg; }
        public static string DrivingNow(Vector3 f) => Points8[Clockwise(DrivingDegrees(f))];

        // CommandHandler.Building's Compass (/build).
        public static string Building(float yaw)
        {
            float degrees = yaw * (180f / MathF.PI);
            while (degrees < 0) degrees += 360f;
            while (degrees >= 360f) degrees -= 360f;
            return degrees switch
            {
                < 22.5f or >= 337.5f => "north", < 67.5f => "north east", < 112.5f => "east",
                < 157.5f => "south east", < 202.5f => "south", < 247.5f => "south west",
                < 292.5f => "west", _ => "north west",
            };
        }

        // WorldEditor.CompassOf.
        public static string Editor(float yaw)
        {
            float deg = yaw * 180f / MathF.PI;
            deg = ((deg % 360f) + 360f) % 360f;
            return Points8[(int)MathF.Round(deg / 45f) % 8];
        }

        // WorldMaps.Describe's Compass8, on its bearing (already 0 to 360).
        public static string WorldMaps(float bearing) => Points8[(int)((bearing + 22.5f) / 45f) % 8];

        // DrivingAids.Compass.
        public static string Driving(Vector3 f)
        {
            float deg = MathF.Atan2(f.X, f.Z) * 180f / MathF.PI;
            if (deg < 0) deg += 360f;
            string[] names = { "north", "north east", "east", "south east", "south", "south west", "west", "north west" };
            return names[(int)MathF.Round(deg / 45f) % 8];
        }

        // LocalPlayerState.GetCardinal and the same switch in GetCompassDirection.
        public static string Cardinal(Quaternion rotation)
        {
            Vector3 forward = Vector3.Transform(new Vector3(0, 0, 1), rotation);
            float angle = MathF.Atan2(forward.X, forward.Z) * (180.0f / MathF.PI);
            if (angle < 0) angle += 360.0f;
            return angle switch
            {
                < 22.5f or >= 337.5f => "North",
                < 67.5f => "North East",
                < 112.5f => "East",
                < 157.5f => "South East",
                < 202.5f => "South",
                < 247.5f => "South West",
                < 292.5f => "West",
                _ => "North West"
            };
        }

        // SkyWords.Compass, sixteen points.
        public static readonly string[] Points16 =
        {
            "north", "north north east", "north east", "east north east",
            "east", "east south east", "south east", "south south east",
            "south", "south south west", "south west", "west south west",
            "west", "west north west", "north west", "north north west",
        };

        public static string Sky(double azimuth)
        {
            double a = ((azimuth % 360.0) + 360.0) % 360.0;
            return Points16[(int)Math.Round(a / 22.5, MidpointRounding.AwayFromZero) % 16];
        }

        // /weather wind's names for the sixteen.
        public static readonly Dictionary<string, float> Bearings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["north"] = 0, ["n"] = 0, ["northnortheast"] = 22.5f, ["nne"] = 22.5f,
            ["northeast"] = 45, ["ne"] = 45, ["eastnortheast"] = 67.5f, ["ene"] = 67.5f,
            ["east"] = 90, ["e"] = 90, ["eastsoutheast"] = 112.5f, ["ese"] = 112.5f,
            ["southeast"] = 135, ["se"] = 135, ["southsoutheast"] = 157.5f, ["sse"] = 157.5f,
            ["south"] = 180, ["s"] = 180, ["southsouthwest"] = 202.5f, ["ssw"] = 202.5f,
            ["southwest"] = 225, ["sw"] = 225, ["westsouthwest"] = 247.5f, ["wsw"] = 247.5f,
            ["west"] = 270, ["w"] = 270, ["westnorthwest"] = 292.5f, ["wnw"] = 292.5f,
            ["northwest"] = 315, ["nw"] = 315, ["northnorthwest"] = 337.5f, ["nnw"] = 337.5f,
        };
    }

    /// <summary>Degrees to try: a grid over two turns either way, and every float within 3000 of each
    /// sixteenth of a turn, where a word changes and where rounding differs.</summary>
    private static IEnumerable<float> Degrees()
    {
        for (float d = -720f; d <= 720f; d += 0.37f) yield return d;
        for (int k = -32; k <= 32; k++)
        {
            float at = k * 22.5f;
            yield return at;
            float up = at, down = at;
            for (int n = 0; n < 3000; n++)
            {
                up = MathF.BitIncrement(up);
                down = MathF.BitDecrement(down);
                yield return up;
                yield return down;
            }
        }
    }

    /// <summary>Headings in radians whose degrees fall at and about each sixteenth of a turn.</summary>
    private static IEnumerable<float> Yaws()
    {
        for (float y = -4f * MathF.PI; y <= 4f * MathF.PI; y += 0.0061f) yield return y;
        for (int k = -32; k <= 32; k++)
        {
            float at = k * 22.5f * MathF.PI / 180f, up = at, down = at;
            yield return at;
            for (int n = 0; n < 300; n++)
            {
                up = MathF.BitIncrement(up);
                down = MathF.BitDecrement(down);
                yield return up;
                yield return down;
            }
        }
    }

    /// <summary>The words each caller says between and exactly at the points. Three round a bearing exactly
    /// between two points to the cardinal (the wind, the editor, the driving aids); the rest clockwise.</summary>
    [Theory]
    [InlineData(22.5, 8, "north east")]
    [InlineData(67.5, 8, "east")]
    [InlineData(112.5, 8, "south east")]
    [InlineData(157.5, 8, "south")]
    [InlineData(202.5, 8, "south west")]
    [InlineData(247.5, 8, "west")]
    [InlineData(292.5, 8, "north west")]
    [InlineData(337.5, 8, "north")]
    [InlineData(360, 8, "north")]
    [InlineData(100, 8, "east")]
    [InlineData(-100, 8, "west")]
    [InlineData(11.25, 16, "north north east")]
    [InlineData(247.5, 16, "west south west")]
    [InlineData(44, 4, "north")]
    [InlineData(46, 4, "east")]
    public void TheWords(double degrees, int points, string word)
        => Assert.Equal(word, CompassWords.Of(degrees, points));

    [Fact]
    public void TheListsAreTheOldLists()
    {
        Assert.Equal(Old.Points16, CompassWords.Sixteen.ToArray());
        Assert.Equal(Old.Points8, CompassWords.Eight.ToArray());
        Assert.Equal(new[] { "north", "east", "south", "west" }, CompassWords.Four.ToArray());
        Assert.Equal(Old.Bearings.OrderBy(kv => kv.Key), CommandHandler.Bearings.OrderBy(kv => kv.Key));
    }

    [Fact]
    public void TheWindSaysWhatItSaid()
    {
        foreach (float d in Degrees())
        {
            float r = d * MathF.PI / 180f;
            var velocity = new Vector3(-MathF.Sin(r) * 8f, 0f, -MathF.Cos(r) * 8f);
            float from = new WindAir(velocity.X, velocity.Z, 0f, 0, 0, 0).FromDegrees;
            Assert.Equal($"Wind 8 metres a second from the {Old.WindNow(from)}, steady.", CommandHandler.DescribeWind(velocity, 0f));
            Assert.True(Old.WindNow(from) == Old.Wind(from) || Old.OnAMidpoint(((from % 360f) + 360f) % 360f), $"{from}");
        }
    }

    [Fact]
    public void BuildAndTheEditorSayWhatTheySaid()
    {
        foreach (float yaw in Yaws())
        {
            Assert.Equal(Old.Building(yaw), CommandHandler.Compass(yaw));
            Assert.Equal(Old.EditorNow(yaw), WorldEditor.CompassOf(yaw));
            Assert.True(Old.EditorNow(yaw) == Old.Editor(yaw) || Old.OnAMidpoint(Old.EditorDegrees(yaw)), $"{yaw}");
        }
        // The editor's /edit face takes degrees from -360 to 360.
        foreach (float d in Degrees())
        {
            float to = d * MathF.PI / 180f;
            Assert.Equal(Old.EditorNow(to), WorldEditor.CompassOf(to));
            Assert.True(Old.EditorNow(to) == Old.Editor(to) || Old.OnAMidpoint(Old.EditorDegrees(to)), $"{to}");
        }
    }

    [Fact]
    public void TheDrivingAidsAndTheFacingKeySayWhatTheySaid()
    {
        var state = new LocalPlayerState();
        foreach (float yaw in Yaws())
        {
            var rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f);
            state.Rotation = rotation;
            Assert.Equal(Old.Cardinal(rotation), state.GetCardinal());
            Assert.StartsWith(Old.Cardinal(rotation) + ", ", state.GetCompassDirection());
            var forward = Vector3.Transform(Vector3.UnitZ, rotation);
            Assert.Equal(Old.DrivingNow(forward), DrivingAids.Compass(forward));
            Assert.True(Old.DrivingNow(forward) == Old.Driving(forward) || Old.OnAMidpoint(Old.DrivingDegrees(forward)), $"{yaw}");
        }
        foreach (float d in Degrees())
        {
            float r = d * MathF.PI / 180f;
            var f = new Vector3(MathF.Sin(r), 0f, MathF.Cos(r));
            Assert.Equal(Old.DrivingNow(f), DrivingAids.Compass(f));
            Assert.True(Old.DrivingNow(f) == Old.Driving(f) || Old.OnAMidpoint(Old.DrivingDegrees(f)), $"{d}");
        }
    }

    /// <summary>WorldMaps added a half-point in float before dividing, which carried three bearings, each
    /// one float short of a midpoint, into the next point. They say the point they are nearest now.</summary>
    [Fact]
    public void TheWorldSaysWhatItSaidButThreeFloats()
    {
        var moved = new Dictionary<float, (string Old, string Now)>
        {
            [22.499998f] = ("north east", "north"),
            [112.49999f] = ("south east", "east"),
            [247.49998f] = ("west", "south west"),
        };
        foreach (float d in Degrees())
        {
            if (d < 0f || d >= 360f) continue;
            if (moved.TryGetValue(d, out var m))
            {
                Assert.Equal(m.Old, Old.WorldMaps(d));
                Assert.Equal(m.Now, CompassWords.Of(d, 8));
            }
            else Assert.Equal(Old.WorldMaps(d), CompassWords.Of(d, 8));
        }
    }

    [Fact]
    public void TheSkySaysWhatItSaid()
    {
        foreach (float d in Degrees())
            Assert.Equal(Old.Sky(d), SkyWords.Compass(d));
        for (int k = -32; k <= 32; k++)
        {
            double at = k * 11.25, up = at, down = at;
            for (int n = 0; n < 3000; n++)
            {
                up = Math.BitIncrement(up);
                down = Math.BitDecrement(down);
                Assert.Equal(Old.Sky(up), SkyWords.Compass(up));
                Assert.Equal(Old.Sky(down), SkyWords.Compass(down));
            }
        }
    }
}
