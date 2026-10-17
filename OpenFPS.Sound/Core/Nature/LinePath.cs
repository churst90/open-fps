using System.Numerics;

namespace OpenFPS.Sound.Core.Nature;

/// <summary>
/// A source laid along a line of its own: a creek's bed as the map or the ground draws it, bends and all (2026-10-17,
/// docs/RUNNING_WATER.md 15.6). Its points in the world from one end to the other, and how wide its bed is.
///
/// Distances along it are from its middle, as a straight line's are (ExtendedSources.LineView), so a synth's sites and
/// the client's cut agree on where along it a thing is. The cut and the pieces' places on it are ExtendedSources' laws
/// for a straight line taken segment by segment, which is exact: every segment is straight.
/// </summary>
public sealed class LinePath
{
    /// <summary>Its points in the world, end to end; never two the same in a row.</summary>
    public readonly Vector3[] Points;

    /// <summary>How wide its bed is, m: a bed this wide or wider (<see cref="WideBedMetres"/>) is heard across itself,
    /// its near side nearer than its far.</summary>
    public readonly float Width;

    private readonly float[] _at;

    /// <summary>From this width a bed is heard as three lines across it, at its middle and a third of its width either
    /// side: under it the three are within a few per cent of one at any distance over the ear's height.</summary>
    public const float WideBedMetres = 1.5f;

    public LinePath(ReadOnlySpan<Vector3> points, float width = 0f)
    {
        var kept = new List<Vector3>(points.Length);
        foreach (var p in points)
            if (kept.Count == 0 || Vector3.DistanceSquared(kept[^1], p) > 1e-8f) kept.Add(p);
        if (kept.Count == 1) kept.Add(kept[0] + new Vector3(1e-3f, 0f, 0f));
        if (kept.Count == 0) { kept.Add(Vector3.Zero); kept.Add(new Vector3(1e-3f, 0f, 0f)); }
        Points = kept.ToArray();
        _at = new float[Points.Length];
        for (int k = 1; k < Points.Length; k++) _at[k] = _at[k - 1] + Vector3.Distance(Points[k - 1], Points[k]);
        Width = MathF.Max(0f, width);
    }

    /// <summary>A straight line through <paramref name="middle"/> along <paramref name="axis"/> (a unit vector).</summary>
    public static LinePath Straight(Vector3 middle, Vector3 axis, float length, float width = 0f)
        => new(new[] { middle - axis * (0.5f * length), middle + axis * (0.5f * length) }, width);

    /// <summary>A line given in an entity's own frame (SoundEmitterComponent.Line), placed by its transform.</summary>
    public static LinePath Of(Vector3 position, Quaternion rotation, ReadOnlySpan<Vector3> local, float width = 0f)
    {
        var world = new Vector3[local.Length];
        var turn = rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : rotation;
        for (int i = 0; i < local.Length; i++) world[i] = position + Vector3.Transform(local[i], turn);
        return new LinePath(world, width);
    }

    public float Length => _at[^1];
    public float HalfLength => 0.5f * _at[^1];
    public int Segments => Points.Length - 1;

    /// <summary>How far along the path point <paramref name="k"/> is, m from its first point.</summary>
    public float StartOf(int k) => _at[k];

    /// <summary>The point <paramref name="along"/> m from its middle, held at its ends.</summary>
    public Vector3 PointAt(float along)
    {
        float s = Math.Clamp(along + HalfLength, 0f, Length);
        int k = SegmentAt(s);
        float len = _at[k + 1] - _at[k];
        return Vector3.Lerp(Points[k], Points[k + 1], len > 0f ? (s - _at[k]) / len : 0f);
    }

    /// <summary>The way it runs at <paramref name="along"/> m from its middle (a unit vector).</summary>
    public Vector3 DirectionAt(float along)
    {
        int k = SegmentAt(Math.Clamp(along + HalfLength, 0f, Length));
        return Vector3.Normalize(Points[k + 1] - Points[k]);
    }

    /// <summary>The segment that holds the point <paramref name="s"/> m from the first point.</summary>
    private int SegmentAt(float s)
    {
        int lo = 0, hi = Segments - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (s < _at[mid + 1]) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>Its point nearest <paramref name="p"/>, and how far along from its middle that is.</summary>
    public Vector3 Nearest(Vector3 p, out float along)
    {
        float best = float.MaxValue, bestS = 0f;
        Vector3 at = Points[0];
        for (int k = 0; k < Segments; k++)
        {
            var a = Points[k];
            var d = Points[k + 1] - a;
            float len2 = d.LengthSquared();
            float t = len2 > 0f ? Math.Clamp(Vector3.Dot(p - a, d) / len2, 0f, 1f) : 0f;
            var q = a + d * t;
            float dist = Vector3.DistanceSquared(p, q);
            if (dist < best) { best = dist; at = q; bestS = _at[k] + t * (_at[k + 1] - _at[k]); }
        }
        along = bestS - HalfLength;
        return at;
    }

    /// <summary>
    /// Segment <paramref name="k"/> as the ear sees it, a straight line: its way, where the ear's foot falls on it (m
    /// from its first point), and how far off it the ear is (never under 5 cm, as ExtendedSources.View).
    /// </summary>
    internal (Vector3 Start, Vector3 Way, float Foot, float Off) ViewOf(int k, Vector3 ear)
    {
        var a = Points[k];
        var d = Points[k + 1] - a;
        float len = d.Length();
        var way = len > 0f ? d / len : Vector3.UnitX;
        var to = ear - a;
        float foot = Vector3.Dot(to, way);
        float off = MathF.Max(0.05f, (to - foot * way).Length());
        return (a, way, foot, off);
    }

    /// <summary>The way across segment <paramref name="k"/> on the flat (its bank to bank), or zero for a segment
    /// that stands straight up.</summary>
    internal Vector3 AcrossOf(int k)
    {
        var d = Points[k + 1] - Points[k];
        var across = new Vector3(-d.Z, 0f, d.X);
        float l = across.Length();
        return l > 1e-6f ? across / l : Vector3.Zero;
    }
}
