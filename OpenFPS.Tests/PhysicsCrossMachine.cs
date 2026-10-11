using System.Globalization;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// How far a physics replay run on other maths may stand from its stored copy (docs/PHYSICS_BOUNDARY.md
/// 14.9). Two things part a run from the machine it was stored on, both measured: rounding that drifts slowly
/// (GitHub's runner was 1 to 3 mm off one truck after 57 s; a run with every step's length rounded a unit
/// either way, at random, stayed within the stored 1 mm everywhere for 60 s of traffic and 90 s of trains), and
/// a decision at a threshold taken a tick early or late, which moves a body along its own path by its speed
/// times the ticks (a walker 46 mm, one tick at 1.4 m/s, in the same run). A row within the drift passes; a
/// row past it passes as a slip only if it is where that body would be two ticks along or back its own path,
/// and then its wheels, controls and flags are not compared on that row (one tick turns a slip ratio from
/// nothing to a skid). A car in the next lane, a car stopped or going somewhere else is outside both.
/// </summary>
public sealed class CrossMachine
{
    /// <summary>A decision taken this much early or late: two ticks, twice the slip seen.</summary>
    public const double SlipSeconds = 2.0 / PhysicsConstants.TickRate;
    /// <summary>Drift of a position: 1 mm a simulated second, twenty times the worst seen on GitHub.</summary>
    public const double MetresPerSecond = 1e-3;
    /// <summary>Drift of a speed: 1 mm/s a second.</summary>
    public const double SpeedPerSecond = 1e-3;
    /// <summary>Drift of a heading: 1e-4 rad a second, fifty times the 1e-4 seen in 57 s.</summary>
    public const double RadiansPerSecond = 1e-4;
    /// <summary>The hardest change of speed any mover makes (a stop at about one g): what a slip does to a speed.</summary>
    public const double HardestAcceleration = 10;
    /// <summary>The fastest turn (a car round a corner at walking pace): what a slip does to a heading.</summary>
    public const double FastestTurn = 1.5;
    /// <summary>The tightest turn: how far a body a slip along a curve stands off the stored tangent.</summary>
    public const double TightestRadius = 5;

    public enum Kind
    {
        /// <summary>Within the drift on every row.</summary>
        Drift,
        /// <summary>The east and north positions, judged together along and across the body's heading.</summary>
        Horizontal,
        /// <summary>The height: within the drift, or the slip's distance.</summary>
        Height,
        /// <summary>A speed: within the drift, or what the hardest acceleration does in a slip.</summary>
        Speed,
        /// <summary>A heading, radians, compared round the circle: the drift, or the fastest turn in a slip.</summary>
        Heading,
        /// <summary>What follows the motion (wheels, controls, flags, pose): within the drift unless the row slipped.</summary>
        Follows,
    }

    /// <summary>A column's rule: its kind and its drift, <paramref name="Base"/> plus <paramref name="PerSecond"/>
    /// for every simulated second.</summary>
    public readonly record struct Column(Kind Kind, double Base, double PerSecond = 0);

    /// <summary>A row's simulated time, seconds.</summary>
    public required Func<string[], double> Seconds { get; init; }
    /// <summary>Each column's rule; a column with no rule is compared as text.</summary>
    public required Func<int, Column?> Rule { get; init; }
    /// <summary>The stored row's speed along its heading, m/s (zero where it has none).</summary>
    public Func<string[], double> Speed { get; init; } = _ => 0;
    /// <summary>The stored row's vertical speed, m/s, for how far a slip moves the height; null: the speed.</summary>
    public Func<string[], double>? Climb { get; init; }
    /// <summary>The stored row's heading, radians (forward = sin east, cos north), or null.</summary>
    public Func<string[], double?> HeadingOf { get; init; } = _ => null;
    /// <summary>The east and north position columns, for <see cref="Kind.Horizontal"/>.</summary>
    public (int East, int North) Plane { get; init; } = (-1, -1);

    /// <summary>Compares line by line. Null when every row is within its bounds; else the first that is not.</summary>
    public string? Compare(string stored, string now, out int slipped)
    {
        slipped = 0;
        var a = stored.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var b = now.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= a.Length) return $"line {i + 1} is new: {b[i]}";
            if (i >= b.Length) return $"line {i + 1} is gone: {a[i]}";
            if (a[i] == b[i]) continue;
            string? why = Row(a[i], b[i], ref slipped);
            if (why != null) return $"line {i + 1}: {why}\n  stored {a[i]}\n  now    {b[i]}";
        }
        return null;
    }

    private string? Row(string storedLine, string nowLine, ref int slipped)
    {
        if (storedLine.StartsWith('#')) return "a comment line differs";
        var s = storedLine.Split('\t');
        var n = nowLine.Split('\t');
        if (s.Length != n.Length) return $"{n.Length} columns, {s.Length} stored";
        double t = Seconds(s);
        var d = new double[s.Length];
        var drift = new double[s.Length];
        var rules = new Column?[s.Length];
        bool within = true;
        for (int c = 0; c < s.Length; c++)
        {
            if (s[c] == n[c]) continue;
            rules[c] = Rule(c);
            if (rules[c] is not { } rule || !Num(s[c], out double x) || !Num(n[c], out double y)) return $"column {c} is {n[c]}, {s[c]} stored";
            d[c] = rule.Kind == Kind.Heading ? Math.Abs(Math.IEEERemainder(x - y, 2 * Math.PI)) : Math.Abs(x - y);
            drift[c] = rule.Base + rule.PerSecond * t;
            if (d[c] > drift[c]) within = false;
        }
        if (within) return null;

        // Past the drift: only a slip along the body's own path explains it, and only the motion shows one.
        double v = Math.Abs(Speed(s)), along = v * SlipSeconds, up = Math.Abs(Climb?.Invoke(s) ?? v) * SlipSeconds;
        bool moved = false;
        for (int c = 0; c < s.Length; c++)
        {
            if (rules[c] is not { } rule || d[c] <= drift[c]) continue;
            moved |= rule.Kind is Kind.Horizontal or Kind.Height or Kind.Speed or Kind.Heading;
            switch (rule.Kind)
            {
                case Kind.Drift:
                    return $"column {c} is {d[c]:G3} off, past its drift of {drift[c]:G3} at {t:F2} s";
                case Kind.Height when d[c] > drift[c] + up:
                    return $"the height is {d[c]:G3} m off, past {drift[c] + up:G3} (drift and a {SlipSeconds * PhysicsConstants.TickRate:F0}-tick slip)";
                case Kind.Speed when d[c] > drift[c] + HardestAcceleration * SlipSeconds:
                    return $"the speed is {d[c]:G3} m/s off, past {drift[c] + HardestAcceleration * SlipSeconds:G3}";
                case Kind.Heading when d[c] > drift[c] + FastestTurn * SlipSeconds:
                    return $"the heading is {d[c]:G3} rad off, past {drift[c] + FastestTurn * SlipSeconds:G3}";
            }
        }
        if (!moved)
        {
            int c = Enumerable.Range(0, s.Length).First(k => rules[k] != null && d[k] > drift[k]);
            return $"column {c} is {d[c]:G3} off, past its drift of {drift[c]:G3} at {t:F2} s, and the body is where it was stored";
        }
        var (e, no) = Plane;
        if (e >= 0)
        {
            double de = Val(n[e]) - Val(s[e]), dn = Val(n[no]) - Val(s[no]);
            double posDrift = Math.Max(Bound(e, t), Bound(no, t));
            double? h = HeadingOf(s);
            if (h is double heading && v > 0.1)
            {
                double fe = Math.Sin(heading), fn = Math.Cos(heading);
                double ahead = Math.Abs(de * fe + dn * fn), across = Math.Abs(de * fn - dn * fe);
                double acrossBound = posDrift + along * along / (2 * TightestRadius);
                if (ahead > posDrift + along)
                    return $"{ahead:G3} m along its path, past {posDrift + along:G3} (drift and a {SlipSeconds * PhysicsConstants.TickRate:F0}-tick slip at {v:F2} m/s)";
                if (across > acrossBound)
                    return $"{across:G3} m across its path, past {acrossBound:G3}: another lane or another line";
            }
            else if (Math.Sqrt(de * de + dn * dn) > posDrift + along)
                return $"{Math.Sqrt(de * de + dn * dn):G3} m off, past {posDrift + along:G3} (drift and a slip at {v:F2} m/s)";
        }
        slipped++;
        return null;
    }

    private double Bound(int c, double t) => Rule(c) is { } r ? r.Base + r.PerSecond * t : 0;

    private static double Val(string s) => Num(s, out double x) ? x : 0;

    private static bool Num(string s, out double x) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out x);

    /// <summary>
    /// A fingerprint of this machine's physics maths: the sound guards' libm probe
    /// (<see cref="RenderFingerprintTests.MathsProbe"/>), the instruction sets the JIT may use for the vector
    /// types, and the bits of the System.Numerics operations the steps call. Two machines with the same one
    /// step to the same bits.
    /// </summary>
    public static string MathsFingerprint()
    {
        var isa = $"{(Avx512F.IsSupported ? "avx512" : Avx2.IsSupported ? "avx2" : Sse41.IsSupported ? "sse41" : "base")}"
                  + $"{(Fma.IsSupported ? "+fma" : "")}{(Vector512.IsHardwareAccelerated ? "+v512" : "")}";
        var bytes = new List<byte>();
        for (int i = 0; i < 2048; i++)
        {
            float x = -50f + i * (100f / 2048f) + 0.0137f;
            var v = new Vector3(x, 0.3f * x - 1.1f, 2.5f - 0.7f * x);
            var q = Quaternion.CreateFromYawPitchRoll(x / 9f, x / 31f, x / 57f);
            var w = Vector3.Transform(Vector3.UnitZ * x, q);
            var m = Matrix4x4.CreateFromQuaternion(q);
            foreach (float f in new[]
            {
                Vector3.Normalize(v).X, Vector3.Normalize(v).Z, v.Length(), Vector3.Dot(v, w), Vector3.Cross(v, w).Y, w.X, w.Y, w.Z,
                Quaternion.Normalize(q * q).W, Quaternion.Slerp(Quaternion.Identity, q, 0.37f).Y, Vector3.Transform(v, m).X,
                Vector3.Lerp(v, w, 0.61f).Z, Vector3.Distance(v, w), MathF.Atan2(w.X, w.Z),
            })
                bytes.AddRange(BitConverter.GetBytes(f));
        }
        return $"{RenderFingerprintTests.MathsProbe()};{isa};{Convert.ToHexString(SHA256.HashData(bytes.ToArray()), 0, 8).ToLowerInvariant()}";
    }
}
