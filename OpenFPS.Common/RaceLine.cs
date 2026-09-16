using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// A driveable line round a closed circuit, and the fastest a given car can take every metre of it.
///
/// The centreline arrives as a handful of waypoints. This resamples it at a fixed spacing so
/// curvature can be measured consistently, offsets it sideways onto the line this particular car is
/// using, and then computes a speed limit at every node from two things and nothing else:
///
///   * the local radius of the bend, which caps the speed at sqrt(g * 9.81 * R) — the point at which
///     the tyres run out of lateral grip;
///   * the brakes, applied BACKWARDS round the loop, so a node's limit also cannot exceed what can
///     still be shed before the slower node after it (v^2 = u^2 + 2 a s, solved for the entry speed).
///
/// That second pass is what makes a car start braking a hundred metres before the corner instead of
/// arriving at it and stopping dead, and it is why the engine note drops on the approach. It runs
/// round the loop twice because the profile is circular: the limit at the last node feeds the first.
///
/// Nothing here knows about racing, ovals or engines. It is geometry and two equations of motion.
/// </summary>
public sealed class RaceLine
{
    /// <summary>Spacing of the resampled nodes, metres. Fine enough to resolve a banked turn's
    /// radius, coarse enough that a two-kilometre circuit is a few hundred nodes.</summary>
    public const float NodeSpacing = 4f;

    /// <summary>
    /// How many nodes either side the radius is measured across.
    ///
    /// Not a smoothing fudge — a correction for what a polyline IS. A map draws its turns as a few
    /// dozen waypoints, so resampling them gives a run of straight chords meeting at corners: measure
    /// the radius across three ADJACENT nodes and almost every triple is dead straight (infinite
    /// radius, no limit at all) while the few that straddle a join are a hairpin. The first field
    /// built this way had stock cars limited to 113 km/h round a turn they should have taken at 235,
    /// because one kink anywhere in the turn drags the whole braking profile down to it.
    ///
    /// Measuring across a baseline longer than the join spacing puts the joins INSIDE the arc being
    /// fitted, where they belong, and what comes back is the radius of the turn rather than the
    /// radius of the draughtsman's corner. It is also what a car does: a wheelbase cannot follow a
    /// kink either.
    /// </summary>
    private const int CurvatureSpan = 5;

    /// <summary>
    /// Passes of three-point smoothing over the resampled line before anything is measured.
    ///
    /// A racing line is a curve. Rounding the joins costs a few centimetres of radius on a 150 m
    /// turn and removes the discontinuity in curvature that a car would otherwise be asked to take
    /// at three hundred kilometres an hour.
    /// </summary>
    private const int SmoothingPasses = 3;

    private readonly Vector3[] _points;        // the line itself, evenly spaced, closed
    private readonly float[] _limit;           // m/s allowed at each node
    /// <summary>The GRIP-limited speed at each node, uncapped by the car's top speed and untouched by
    /// the braking pass — infinite on a straight, where nothing but drag holds the car back.
    ///
    /// Kept apart from <see cref="_limit"/> because they answer different questions and conflating
    /// them put every car on the circuit permanently on the edge of a slide. `_limit` is "how fast
    /// may this car go here", which on a straight is its top speed and into a corner is whatever the
    /// braking pass allows. This is "how fast could its TYRES hold it here", which is the only one of
    /// the two that says anything about the tyres. A car flat out on a straight is using none of its
    /// cornering grip, and a formula that cannot tell those apart reports it as sliding.</summary>
    private readonly float[] _corner;
    private readonly float[] _heading;         // radians, the way the line points at each node
    private readonly float _spacing;           // actual node spacing, metres

    /// <summary>Total length of the lap, metres.</summary>
    public float Length { get; }
    public int NodeCount => _points.Length;

    /// <summary>Slowest and fastest the profile allows anywhere on the lap, m/s — for logging.</summary>
    public float MinSpeed { get; }
    public float MaxSpeed { get; }

    public RaceLine(IReadOnlyList<Vector3> centreline, float lateralOffset, float topSpeed,
                    float corneringG, float brake, float bankingDegrees = 0f)
    {
        if (centreline == null || centreline.Count < 3)
            throw new ArgumentException("A circuit needs at least three waypoints.", nameof(centreline));

        var resampled = Resample(centreline, NodeSpacing, out float spacing);
        Smooth(resampled, SmoothingPasses);
        _spacing = spacing;
        int n = resampled.Count;
        _points = new Vector3[n];
        _heading = new float[n];
        _limit = new float[n];
        _corner = new float[n];

        // Offset sideways onto this car's line. The normal is the tangent turned 90 degrees in the
        // ground plane, so a positive offset is to the car's right.
        for (int i = 0; i < n; i++)
        {
            Vector3 tangent = Tangent(resampled, i);
            var right = new Vector3(tangent.Z, 0f, -tangent.X);
            _points[i] = resampled[i] + right * lateralOffset;
        }

        Length = 0f;
        for (int i = 0; i < n; i++) Length += Vector3.Distance(_points[i], _points[(i + 1) % n]);

        for (int i = 0; i < n; i++)
        {
            Vector3 d = _points[(i + 1) % n] - _points[i];
            _heading[i] = MathF.Atan2(d.X, d.Z);

            // Menger curvature of three points spanning this one: the radius of the circle through
            // them. A straight gives an infinite radius and therefore no limit at all.
            int span = Math.Clamp(CurvatureSpan, 1, Math.Max(1, n / 3));
            float radius = Radius(_points[(i - span + n) % n], _points[i], _points[(i + span) % n]);
            // A straight has no cornering limit at all — not "the top speed", which is what it used
            // to be recorded as and which made a car doing its top speed in a straight line read as
            // though it were at the limit of its grip.
            _corner[i] = float.IsInfinity(radius) ? float.PositiveInfinity
                                                  : CorneringSpeed(radius, corneringG, bankingDegrees);
            _limit[i] = MathF.Min(topSpeed, float.IsInfinity(radius) ? topSpeed : _corner[i]);
        }

        // Braking, backwards, twice round — the second lap carries the wrap-around back to the start.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int k = n - 1; k >= 0; k--)
            {
                int next = (k + 1) % n;
                float ds = Vector3.Distance(_points[k], _points[next]);
                float entry = MathF.Sqrt(_limit[next] * _limit[next] + 2f * brake * ds);
                if (entry < _limit[k]) _limit[k] = entry;
            }
        }

        float lo = float.MaxValue, hi = 0f;
        foreach (float v in _limit) { if (v < lo) lo = v; if (v > hi) hi = v; }
        MinSpeed = lo; MaxSpeed = hi;
    }

    /// <summary>Where the car is, which way it points, and how fast it is allowed to be, at a
    /// distance round the lap. The distance wraps, so a caller can simply keep adding to it.</summary>
    public void Sample(float distance, out Vector3 position, out float heading, out float speedLimit)
    {
        float s = distance % Length;
        if (s < 0f) s += Length;

        // Nodes are evenly spaced by construction and the loop closes exactly, so finding the one
        // the distance falls in is a division rather than a search.
        int n = _points.Length;
        int i = Math.Clamp((int)(s / _spacing), 0, n - 1);
        float f = Math.Clamp(s / _spacing - i, 0f, 1f);

        int j = (i + 1) % n;
        position = Vector3.Lerp(_points[i], _points[j], f);
        heading = LerpAngle(_heading[i], _heading[j], f);
        speedLimit = _limit[i] + (_limit[j] - _limit[i]) * f;
    }

    /// <summary>
    /// As <see cref="Sample(float, out Vector3, out float, out float)"/>, and also how fast this car's
    /// TYRES could hold it here — which is not the same question as how fast it may go.
    ///
    /// Infinite on a straight. The ratio of the actual speed to this is what says how hard the tyres
    /// are working laterally, and it is exact rather than approximate: the cornering limit is where
    /// lateral acceleration equals available grip, and acceleration is v²/R either way, so the ratio
    /// of accelerations is the square of the ratio of speeds.
    /// </summary>
    public void Sample(float distance, out Vector3 position, out float heading, out float speedLimit,
                       out float corneringLimit)
    {
        Sample(distance, out position, out heading, out speedLimit);

        float s = distance % Length;
        if (s < 0f) s += Length;
        int n = _points.Length;
        int i = Math.Clamp((int)(s / _spacing), 0, n - 1);
        float f = Math.Clamp(s / _spacing - i, 0f, 1f);
        int j = (i + 1) % n;

        // Straights are infinite, so interpolating between a finite node and an infinite one has to
        // take the finite answer rather than produce a NaN.
        float a = _corner[i], b = _corner[j];
        corneringLimit = float.IsInfinity(a) ? b : float.IsInfinity(b) ? a : a + (b - a) * f;
    }

    /// <summary>Rounds the joins out of a closed polyline: each point moved a quarter of the way
    /// toward the average of its neighbours, which leaves a circle a circle and a corner an arc.</summary>
    private static void Smooth(List<Vector3> loop, int passes)
    {
        int n = loop.Count;
        if (n < 5 || passes <= 0) return;
        var work = new Vector3[n];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int i = 0; i < n; i++)
            {
                Vector3 avg = (loop[(i - 1 + n) % n] + loop[(i + 1) % n]) * 0.5f;
                work[i] = Vector3.Lerp(loop[i], avg, 0.5f);
            }
            for (int i = 0; i < n; i++) loop[i] = work[i];
        }
    }

    private static Vector3 Tangent(IReadOnlyList<Vector3> p, int i)
    {
        int n = p.Count;
        Vector3 d = p[(i + 1) % n] - p[(i - 1 + n) % n];
        d.Y = 0f;
        return d.LengthSquared() > 1e-8f ? Vector3.Normalize(d) : Vector3.UnitZ;
    }

    /// <summary>Radius of the circle through three points, infinite when they are collinear.</summary>
    private static float Radius(Vector3 a, Vector3 b, Vector3 c)
    {
        float ab = Vector3.Distance(a, b), bc = Vector3.Distance(b, c), ca = Vector3.Distance(c, a);
        // Twice the triangle's area, in the ground plane.
        float cross = MathF.Abs((b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X));
        if (cross < 1e-6f) return float.PositiveInfinity;
        return ab * bc * ca / (2f * cross);
    }

    private static float LerpAngle(float a, float b, float f)
    {
        float d = MathF.IEEERemainder(b - a, 2f * MathF.PI);
        return a + d * f;
    }

    /// <summary>
    /// Walks the closed polyline laying down evenly spaced points, so curvature is measured over a
    /// consistent baseline however the map was authored.
    ///
    /// The spacing is rounded to divide the perimeter a whole number of times rather than being
    /// taken literally, so the loop closes EXACTLY — the last node is one step from the first, with
    /// no short segment at the join. That is what lets Sample find a node by dividing instead of
    /// walking, and it removes the one place a lap could gain or lose a few centimetres a lap.
    /// </summary>
    private static List<Vector3> Resample(IReadOnlyList<Vector3> loop, float wanted, out float spacing)
    {
        int n = loop.Count;
        float perimeter = 0f;
        for (int i = 0; i < n; i++) perimeter += Vector3.Distance(loop[i], loop[(i + 1) % n]);

        int count = Math.Max(3, (int)MathF.Round(perimeter / MathF.Max(0.5f, wanted)));
        spacing = perimeter / count;

        var outp = new List<Vector3>(count);
        int seg = 0;
        float segStart = 0f;
        float segLen = Vector3.Distance(loop[0], loop[1 % n]);
        for (int k = 0; k < count; k++)
        {
            float target = k * spacing;
            while (seg < n - 1 && target > segStart + segLen)
            {
                segStart += segLen;
                seg++;
                segLen = Vector3.Distance(loop[seg], loop[(seg + 1) % n]);
            }
            float f = segLen > 1e-4f ? Math.Clamp((target - segStart) / segLen, 0f, 1f) : 0f;
            outp.Add(Vector3.Lerp(loop[seg], loop[(seg + 1) % n], f));
        }
        return outp;
    }

    /// <summary>
    /// The fastest a car can go round a bend of this radius — WITH THE BANKING, which is the whole
    /// difference between a road course and a superspeedway.
    ///
    /// On the flat, all that holds a car in is friction: v = sqrt(mu*g*R). Bank the surface by theta
    /// and a component of the car's own weight points into the turn, so the tyres are asked for less
    /// and can be asked for more at once:
    ///
    ///     v^2 = R * g * (mu + tan(theta)) / (1 - mu * tan(theta))
    ///
    /// Past mu*tan(theta) = 1 the denominator goes to zero and then negative, which is not a
    /// singularity to guard against so much as the physical answer: the banking alone holds the car,
    /// and there is no cornering speed limit at all — it is limited by power, like a straight.
    /// A car on a steep enough bank does not lift.
    ///
    /// This was missing, and it was audible. The speedway's geometry HAS its banking — the generator
    /// raises the turns seven metres and the centreline carries it — but the racing line read the
    /// curvature and ignored the elevation, so it worked out the corner speed for a flat track. The
    /// cars therefore lifted 16-23 % twice a lap, which is three to four and a half SEMITONES of rev
    /// drop, for every car, right in front of the grandstand. Heard, correctly, as "the cars sound
    /// like they are slowing down" — because they were.
    /// </summary>
    private static float CorneringSpeed(float radius, float corneringG, float bankingDegrees)
    {
        float mu = MathF.Max(0f, corneringG);
        if (bankingDegrees <= 0f) return MathF.Sqrt(MathF.Max(0f, mu * 9.81f * radius));
        float tan = MathF.Tan(bankingDegrees * MathF.PI / 180f);
        float denom = 1f - mu * tan;
        if (denom <= 1e-3f) return float.PositiveInfinity;   // the bank holds it; power is the only limit
        return MathF.Sqrt(MathF.Max(0f, radius * 9.81f * (mu + tan) / denom));
    }

}
