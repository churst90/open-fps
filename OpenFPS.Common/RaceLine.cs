using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// A driveable line round a closed circuit, and the fastest a given car can take every metre of it.
/// The waypoints are resampled evenly, offset onto this car's lane, and each node's limit is the
/// lower of the bend's grip limit, sqrt(g 9.81 R), and what the brakes can still shed before the
/// slower node after it (v^2 = u^2 + 2 a s, worked backwards, twice round because the loop is
/// closed). The backward pass is why a car brakes before a corner (docs/ENGINE_SYNTHESIS.md).
/// </summary>
public sealed class RaceLine
{
    /// <summary>Spacing of the resampled nodes, metres. Fine enough to resolve a banked turn's
    /// radius, coarse enough that a two-kilometre circuit is a few hundred nodes.</summary>
    public const float NodeSpacing = 4f;

    /// <summary>
    /// How many nodes either side the radius is measured across: longer than the waypoint spacing, so
    /// the joins between chords fall inside the arc. Across adjacent nodes the few triples at a join
    /// read as hairpins, and stock cars lapped at 113 km/h a turn they should take at 235
    /// (docs/ENGINE_SYNTHESIS.md).
    /// </summary>
    private const int CurvatureSpan = 5;

    /// <summary>Passes of three-point smoothing before anything is measured: a few centimetres of radius
    /// on a 150 m turn, and no step in curvature to take at 300 km/h.</summary>
    private const int SmoothingPasses = 3;

    private readonly Vector3[] _points;        // the line itself, evenly spaced, closed
    private readonly float[] _limit;           // m/s allowed at each node
    /// <summary>The grip-limited speed at each node, infinite on a straight: how fast the tyres could
    /// hold the car, not how fast it may go (<see cref="_limit"/>). Read as one, every car flat out on
    /// a straight was reported on the edge of a slide.</summary>
    private readonly float[] _corner;
    private readonly float[] _heading;         // radians, the way the line points at each node
    /// <summary>Signed curvature at each node, 1/m, positive turning right (the way heading rises):
    /// the heading's change across the neighbouring segments over the distance between their middles.</summary>
    private readonly float[] _curvature;

    /// <summary>
    /// Arc length from node 0 to each node; <c>_arc[n] == Length</c>. The nodes are not evenly spaced:
    /// smoothing shortens the curves, and the lane offset lengthens an outside lane's turns and
    /// shortens an inside one's. Dividing by the nominal spacing stopped a car dead at one point of
    /// every lap (docs/COMMON_NOTES.md, The racing line's arc length).
    /// </summary>
    private readonly float[] _arc;

    /// <summary>The bank of the turns, degrees, as the line was built with: the surface tilts toward
    /// the inside of every curve by this much.</summary>
    public float BankingDegrees { get; }

    /// <summary>Total length of the lap, metres.</summary>
    public float Length { get; }
    public int NodeCount => _points.Length;

    /// <summary>Slowest and fastest the profile allows anywhere on the lap, m/s — for logging.</summary>
    public float MinSpeed { get; }
    public float MaxSpeed { get; }

    /// <param name="speedCapAt">An upper limit on the speed at a place, m/s: a road's speed limit, which
    /// changes along a route through a town. Null for none.</param>
    public RaceLine(IReadOnlyList<Vector3> centreline, float lateralOffset, float topSpeed,
                    float corneringG, float brake, float bankingDegrees = 0f,
                    Func<Vector3, float>? speedCapAt = null)
    {
        if (centreline == null || centreline.Count < 3)
            throw new ArgumentException("A circuit needs at least three waypoints.", nameof(centreline));
        // A non-finite waypoint makes the perimeter infinite and the resampled array the size of the
        // address space.
        for (int i = 0; i < centreline.Count; i++)
            if (!float.IsFinite(centreline[i].X) || !float.IsFinite(centreline[i].Y) || !float.IsFinite(centreline[i].Z))
                throw new ArgumentException($"Waypoint {i} of the circuit is not a finite position.", nameof(centreline));

        var resampled = Resample(centreline, NodeSpacing, out _);
        Smooth(resampled, SmoothingPasses);
        int n = resampled.Count;
        _points = new Vector3[n];
        _heading = new float[n];
        _limit = new float[n];
        _corner = new float[n];
        _curvature = new float[n];
        BankingDegrees = bankingDegrees;

        // A positive offset is to the car's right.
        for (int i = 0; i < n; i++)
        {
            Vector3 tangent = Tangent(resampled, i);
            var right = new Vector3(tangent.Z, 0f, -tangent.X);
            _points[i] = resampled[i] + right * lateralOffset;
        }

        _arc = new float[n + 1];
        for (int i = 0; i < n; i++) _arc[i + 1] = _arc[i] + Vector3.Distance(_points[i], _points[(i + 1) % n]);
        Length = _arc[n];

        for (int i = 0; i < n; i++)
        {
            Vector3 d = _points[(i + 1) % n] - _points[i];
            _heading[i] = MathF.Atan2(d.X, d.Z);

            // A straight's radius is infinite: no cornering limit at all, not the top speed (see _corner).
            int span = Math.Clamp(CurvatureSpan, 1, Math.Max(1, n / 3));
            float radius = Radius(_points[(i - span + n) % n], _points[i], _points[(i + span) % n]);
            _corner[i] = float.IsInfinity(radius) ? float.PositiveInfinity
                                                  : CorneringSpeed(radius, corneringG, bankingDegrees);
            _limit[i] = MathF.Min(topSpeed, float.IsInfinity(radius) ? topSpeed : _corner[i]);
            if (speedCapAt != null) _limit[i] = MathF.Min(_limit[i], MathF.Max(0.5f, speedCapAt(_points[i])));
        }

        for (int i = 0; i < n; i++)
        {
            int p = (i - 1 + n) % n;
            float turn = MathF.IEEERemainder(_heading[i] - _heading[p], 2f * MathF.PI);
            float span = 0.5f * (_arc[i + 1] - _arc[i] + (p == n - 1 ? _arc[n] - _arc[n - 1] : _arc[p + 1] - _arc[p]));
            _curvature[i] = span > 1e-4f ? turn / span : 0f;
        }

        // Braking, backwards, twice round: the second lap carries the wrap-around back to the start.
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

        Locate(s, out int i, out float f);

        int j = (i + 1) % _points.Length;
        position = Vector3.Lerp(_points[i], _points[j], f);
        heading = LerpAngle(_heading[i], _heading[j], f);
        speedLimit = _limit[i] + (_limit[j] - _limit[i]) * f;
    }

    /// <summary>
    /// As <see cref="Sample(float, out Vector3, out float, out float)"/>, and how fast the tyres could
    /// hold the car here (infinite on a straight). The square of speed over this is exactly how hard
    /// the tyres work laterally, since acceleration is v²/R either way.
    /// </summary>
    public void Sample(float distance, out Vector3 position, out float heading, out float speedLimit,
                       out float corneringLimit)
    {
        Sample(distance, out position, out heading, out speedLimit);

        float s = distance % Length;
        if (s < 0f) s += Length;
        Locate(s, out int i, out float f);
        int j = (i + 1) % _points.Length;

        // Between a finite node and an infinite one, the finite answer, not a NaN.
        float a = _corner[i], b = _corner[j];
        corneringLimit = float.IsInfinity(a) ? b : float.IsInfinity(b) ? a : a + (b - a) * f;
    }

    /// <summary>Signed curvature at a distance round the lap, 1/m, positive turning right.</summary>
    public float CurvatureAt(float distance)
    {
        float s = distance % Length;
        if (s < 0f) s += Length;
        Locate(s, out int i, out float f);
        int j = (i + 1) % _points.Length;
        // A node's curvature belongs to the joint at it; each joint's turn is spread over the segments
        // either side.
        return _curvature[i] + (_curvature[j] - _curvature[i]) * f;
    }

    /// <summary>
    /// The fastest a car may be going now to take every bend in the next <paramref name="span"/> metres
    /// with no more than <paramref name="corneringG"/> on its tyres, braking at up to
    /// <paramref name="brake"/>, from the line's own curvature node by node. The g is one budget for
    /// braking and cornering (the friction circle, Milliken and Milliken, Race Car Vehicle Dynamics,
    /// 1995, chapter 2): v_c = sqrt(mu g / |k|) at each node, and back toward the car the speed may rise
    /// by the braking the circle leaves, sqrt((mu g)^2 - (v^2 k)^2), so the braking is done before the
    /// bend. Not the speed profile: its long <see cref="CurvatureSpan"/> reads a few-metre junction
    /// turn as a gentle bend, and a car steering on its own tyres must take the bend the line makes.
    /// </summary>
    /// <param name="cornerSpeed">The vehicle's own fastest speed round a steady turn of a curvature,
    /// if it knows it (WheelDynamics.SteadyTurnSpeed); the lower of that and the cornering budget
    /// holds at each node.</param>
    /// <param name="comfortG">The side friction an ordinary driver finds comfortable at a speed, g
    /// (DriverSteering.ComfortSideFriction). Braking and cornering then share an ellipse of the brake
    /// and that side friction (Milliken and Milliken 1995, chapter 2, at comfort rather than the
    /// limit), so the slowing is done on the way in.</param>
    public float BendSpeedWithin(float distance, float span, float corneringG, float brake, Func<float, float>? cornerSpeed = null,
                                 Func<float, float>? comfortG = null)
    {
        float s = distance % Length;
        if (s < 0f) s += Length;
        Locate(s, out int i, out _);
        int n = _points.Length;
        float muG = MathF.Max(0.01f, corneringG) * 9.81f;

        // The nodes ahead within the span, and how far each is.
        Span<int> nodes = stackalloc int[64];
        Span<float> at = stackalloc float[64];
        int count = 0;
        float ahead = _arc[i + 1] - s;
        for (int k = 1; k <= n && ahead <= span && count < nodes.Length; k++)
        {
            int node = (i + k) % n;
            nodes[count] = node; at[count] = ahead; count++;
            int after = node + 1 <= n ? node + 1 : n;
            ahead += _arc[after] - _arc[node];
        }

        // Backwards from the furthest node to the car. The braking room over a segment is what the
        // circle leaves at its tighter end: read at the nearer end, a tightening bend was entered on
        // the full brake while the cornering built up, the two together past the circle.
        float v = float.PositiveInfinity, beyond = 0f, beyondCurve = 0f;
        for (int c = count - 1; c >= -1; c--)
        {
            int node = c >= 0 ? nodes[c] : i;
            float here = c >= 0 ? at[c] : 0f;
            float curve = MathF.Abs(_curvature[node]);
            if (float.IsFinite(v))
            {
                float lateral = v * v * MathF.Max(curve, beyondCurve);
                float room = MathF.Min(brake, MathF.Sqrt(MathF.Max(0f, muG * muG - lateral * lateral)));
                if (comfortG != null)
                {
                    // (b_x / brake)^2 + (a_y / side)^2 = 1.
                    float side = MathF.Min(muG, MathF.Max(0.01f, comfortG(v)) * 9.81f);
                    float used = lateral / side;
                    room *= MathF.Sqrt(MathF.Max(0f, 1f - used * used));
                }
                v = MathF.Sqrt(v * v + 2f * room * (beyond - here));
            }
            if (curve > 1e-5f)
            {
                v = MathF.Min(v, MathF.Sqrt(muG / curve));
                if (cornerSpeed != null) v = MathF.Min(v, cornerSpeed(curve));
            }
            beyond = here;
            beyondCurve = curve;
        }
        return v;
    }

    /// <summary>
    /// The lowest speed limit from <paramref name="distance"/> to <paramref name="span"/> metres on,
    /// every node and both ends. The far end alone read a bend's faster exit past its tightest part:
    /// +2.2 then -2.2 m/s^2 within half a second on the city's corners, a diesel pickup flooring it,
    /// lifting and flooring it again.
    /// </summary>
    public float SlowestWithin(float distance, float span)
    {
        Sample(distance, out _, out _, out float slowest);
        Sample(distance + MathF.Max(0f, span), out _, out _, out float end);
        slowest = MathF.Min(slowest, end);
        if (span <= 0f) return slowest;

        float s = distance % Length;
        if (s < 0f) s += Length;
        Locate(s, out int i, out _);
        int n = _points.Length;
        float covered = _arc[i + 1] - s;          // road to the next node
        for (int k = 1; k <= n && covered <= span; k++)
        {
            int node = (i + k) % n;
            slowest = MathF.Min(slowest, _limit[node]);
            int after = node + 1 <= n ? node + 1 : n;
            covered += _arc[after] - _arc[node];
        }
        return slowest;
    }

    /// <summary>Which segment a distance round the lap falls in, and how far along it: a binary search
    /// of <see cref="_arc"/>, because the nodes are not evenly spaced.</summary>
    private void Locate(float s, out int index, out float fraction)
    {
        int n = _points.Length;
        int lo = 0, hi = n;                     // find the last i with _arc[i] <= s
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (_arc[mid] <= s) lo = mid; else hi = mid;
        }
        index = Math.Clamp(lo, 0, n - 1);
        float seg = _arc[index + 1] - _arc[index];
        fraction = seg > 1e-6f ? Math.Clamp((s - _arc[index]) / seg, 0f, 1f) : 0f;
    }

    /// <summary>Rounds the joins out of a closed polyline: each point moved half way toward the average
    /// of its neighbours, which leaves a circle a circle and a corner an arc.</summary>
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

    /// <summary>Longer than any map will be (the city is 10 km across); a bound on what Resample allocates.</summary>
    private const float MaxPerimeterMetres = 1_000_000f;

    /// <summary>
    /// Evenly spaced points along the closed polyline, so curvature has a consistent baseline however
    /// the map was drawn. The spacing divides the perimeter a whole number of times, so the loop closes
    /// with no short segment at the join.
    /// </summary>
    private static List<Vector3> Resample(IReadOnlyList<Vector3> loop, float wanted, out float spacing)
    {
        int n = loop.Count;
        float perimeter = 0f;
        for (int i = 0; i < n; i++) perimeter += Vector3.Distance(loop[i], loop[(i + 1) % n]);

        if (perimeter > MaxPerimeterMetres)
            throw new ArgumentException($"A circuit {perimeter / 1000f:F0} km round is not a map.", nameof(loop));
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
    /// The fastest a car can go round a bend of this radius, with the banking:
    /// v^2 = R g (mu + tan theta) / (1 - mu tan theta). Past mu tan theta = 1 the bank alone holds the
    /// car and only power limits it, like a straight. Without the banking every car lifted 16-23 %
    /// twice a lap at the speedway (docs/AUDIO_LOAD_DROPOUTS.md, session 5, third pass).
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
