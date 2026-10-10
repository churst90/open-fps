using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// What a barrier does to a sound, by Maekawa's curve on the path difference round it against the
/// wavelength: continuous (a source drifting behind an edge fades) and capped (a single screen takes at
/// most about 24 dB). A yes/no line of sight once took a 130 dB engine 20 m beyond a knee-high pit wall
/// 26 dB down with its top three octaves gone; over a 0.9 m wall the path difference is a few
/// centimetres, five to eight decibels, mostly at the top.
/// </summary>
public static class Diffraction
{
    /// <summary>Most a single barrier may take, dB: past it the energy arrives round the ends and off
    /// everything else. Barrier design handbooks stop at 24 dB for that reason.</summary>
    public const float MaxInsertionLossDb = 24.0f;

    /// <summary>Least a barrier takes once it blocks the line, dB: Maekawa at a path difference of zero,
    /// the shadow boundary. Named for the tests; the curve gives it.</summary>
    public const float GrazingInsertionLossDb = 5.0f;

    /// <summary>
    /// Insertion loss of a single screen, dB, at one frequency. <paramref name="pathDifference"/> is the
    /// extra path round the obstacle, metres: zero grazes the edge, negative is a clear line and no loss.
    /// </summary>
    public static float InsertionLossDb(float pathDifference, float frequencyHz, float speedOfSound = 343.0f)
    {
        if (!float.IsFinite(pathDifference) || pathDifference <= 0f) return 0f;
        if (frequencyHz <= 0f || speedOfSound <= 0f) return 0f;

        // Fresnel number: how many half-wavelengths of detour the barrier cost.
        float lambda = speedOfSound / frequencyHz;
        float n = 2f * pathDifference / lambda;

        double root = Math.Sqrt(2.0 * Math.PI * n);
        // x/tanh(x) -> 1 as x -> 0 (the loss -> 5 dB); guarded, tanh(0) is zero.
        double ratio = root < 1e-6 ? 1.0 : root / Math.Tanh(root);
        double db = 5.0 + 20.0 * Math.Log10(ratio);
        return (float)Math.Clamp(db, 0.0, MaxInsertionLossDb);
    }

    /// <summary>The same as a linear gain, 0..1.</summary>
    public static float BandGain(float pathDifference, float frequencyHz, float speedOfSound = 343.0f)
        => MathF.Pow(10f, -InsertionLossDb(pathDifference, frequencyHz, speedOfSound) / 20f);

    // Where each of the mixer's three bands (FMOD's THREE_EQ, split at 400 Hz and 4 kHz) is evaluated:
    // the geometric middles of what each covers for this game's sounds. The same path difference at
    // 200 Hz and at 8 kHz is what takes a barrier's top off and leaves its bottom.
    public const float LowBandHz = 200.0f;
    public const float MidBandHz = 1250.0f;
    public const float HighBandHz = 8000.0f;

    /// <summary>The three band gains a barrier of this path difference leaves behind, 0..1 each.</summary>
    public static (float Low, float Mid, float High) BandGains(float pathDifference, float speedOfSound = 343.0f)
        => (BandGain(pathDifference, LowBandHz, speedOfSound),
            BandGain(pathDifference, MidBandHz, speedOfSound),
            BandGain(pathDifference, HighBandHz, speedOfSound));

    /// <summary>
    /// How far out of its way sound had to go to get past one box, metres, or false if the box is not
    /// in the way. The route crosses one edge (a thin wall) or two (over a face and down the far side):
    /// with one only, every route over a 16 m deep grandstand went through it. Legs through the box are
    /// thrown out, or a wall's buried bottom edge always won.
    /// </summary>
    public static bool PathDifferenceAroundBox(Vector3 centre, Vector3 size, Quaternion rotation,
                                               Vector3 source, Vector3 listener, out float pathDifference)
        => PathDifferenceAroundBox(centre, size, rotation, source, listener, out pathDifference, out _);

    /// <summary>
    /// The same search, also reporting the listener-side crossing: the edge the sound is heard from (a
    /// voice behind a doorway comes from the jamb; a car behind a kerb-high wall barely moves). The
    /// listener side, because the last leg arrives at the ear: the other end of a grandstand's run would
    /// place it at the corner the sound entered.
    /// </summary>
    public static bool PathDifferenceAroundBox(Vector3 centre, Vector3 size, Quaternion rotation,
                                               Vector3 source, Vector3 listener, out float pathDifference,
                                               out Vector3 listenerSideEdge)
        => PathDifferenceAroundBox(centre, size, rotation, source, listener, out pathDifference, out listenerSideEdge, null);

    /// <summary>
    /// The same search, adding every way round that clears this box to <paramref name="routes"/> (path
    /// difference, source-side and listener-side crossing), shortest or not: the shortest can run into
    /// the next box (over a storey-high wall into the slab above) while the jamb beside it is clear, and
    /// only the caller has the rest of the scene.
    /// </summary>
    public static bool PathDifferenceAroundBox(Vector3 centre, Vector3 size, Quaternion rotation,
                                               Vector3 source, Vector3 listener, out float pathDifference,
                                               out Vector3 listenerSideEdge, List<(float D, Vector3 SourceSide, Vector3 Edge)>? routes)
    {
        pathDifference = 0f;
        listenerSideEdge = listener;
        if (!GeometryUtils.LineIntersectsOBB(source, listener, centre, size, rotation)) return false;

        Vector3 h = size * 0.5f;
        float direct = Vector3.Distance(source, listener);
        float best = float.MaxValue;

        Span<Vector3> corner = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var local = new Vector3((i & 1) == 0 ? -h.X : h.X,
                                    (i & 2) == 0 ? -h.Y : h.Y,
                                    (i & 4) == 0 ? -h.Z : h.Z);
            corner[i] = centre + Vector3.Transform(local, rotation);
        }

        // The twelve edges as corner-index pairs, grouped four to an axis. Within a group the four
        // edges are indexed by the signs of the other two axes, in the order (-,-) (+,-) (-,+) (+,+).
        ReadOnlySpan<byte> edges = stackalloc byte[24]
        {
            0,1, 2,3, 4,5, 6,7,   // along local X, by (y,z) sign
            0,2, 1,3, 4,6, 5,7,   // along local Y, by (x,z) sign
            0,4, 1,5, 2,6, 3,7,   // along local Z, by (x,y) sign
        };

        Span<Vector3> a = stackalloc Vector3[12];
        Span<Vector3> b = stackalloc Vector3[12];
        for (int e = 0; e < 12; e++) { a[e] = corner[edges[e * 2]]; b[e] = corner[edges[e * 2 + 1]]; }

        // Below both endpoints is under something standing on the ground: without this a 36 m tower
        // cost nothing by its buried bottom edge. Below one is fine: under a bridge.
        float floorY = MathF.Min(source.Y, listener.Y) - 0.05f;

        // ── One edge: a thin barrier, bent over ────────────────────────────────────────────
        for (int e = 0; e < 12; e++)
        {
            float t = MinimiseOnEdge(a[e], b[e], source, listener);
            Vector3 p = Vector3.Lerp(a[e], b[e], t);
            float around = Vector3.Distance(source, p) + Vector3.Distance(p, listener);
            if (around >= best && routes == null) continue;
            if (p.Y < floorY) continue;
            if (!LegIsClear(source, p, centre, size, rotation)) continue;
            if (!LegIsClear(listener, p, centre, size, rotation)) continue;
            routes?.Add((MathF.Max(0f, around - direct), p, p));
            if (around >= best) continue;
            best = around;
            listenerSideEdge = p;
        }

        // ── Two edges: over a face and down the other side ─────────────────────────────────
        // The parallel edges bounding a common face: within each axis group, the two whose signs
        // differ in exactly one place.
        ReadOnlySpan<byte> pairs = stackalloc byte[24]
        {
            0,1, 0,2, 1,3, 2,3,
            4,5, 4,6, 5,7, 6,7,
            8,9, 8,10, 9,11, 10,11,
        };
        // Each pair both ways round: in one order only, a grandstand measured 5.1 m one way and 46.4 m
        // the other, a doorway's jamb 0.81 m and its top corner 5.44 m.
        for (int k = 0; k < 24; k++)
        {
            int e1 = pairs[(k >> 1) * 2 + (k & 1)], e2 = pairs[(k >> 1) * 2 + 1 - (k & 1)];
            // Nested, not alternating (the length is jointly convex): alternating stalled on an 11 cm
            // wall at 5.6 m of detour where the answer is 0.77.
            MinimiseOnPair(a[e1], b[e1], a[e2], b[e2], source, listener, out float t, out float u);
            Vector3 p = Vector3.Lerp(a[e1], b[e1], t);
            Vector3 r = Vector3.Lerp(a[e2], b[e2], u);
            float around = Vector3.Distance(source, p) + Vector3.Distance(p, r) + Vector3.Distance(r, listener);
            if (around >= best && routes == null) continue;
            if (p.Y < floorY || r.Y < floorY) continue;
            if (!LegIsClear(source, p, centre, size, rotation)) continue;
            if (!LegIsClear(listener, r, centre, size, rotation)) continue;
            routes?.Add((MathF.Max(0f, around - direct), p, r));
            if (around >= best) continue;
            // The run between the crossings lies on a face and needs no test (it would always fail).
            best = around;
            listenerSideEdge = r;   // r is the crossing the last leg leaves from
        }

        if (best == float.MaxValue) return false;   // wholly enclosed: no route round this box at all
        pathDifference = MathF.Max(0f, best - direct);
        return true;
    }

    /// <summary>
    /// Where on the edge the detour from one point to the other is shortest, as a fraction along it.
    /// Exact by unfolding: rotate one point about the edge's line into the other's plane and the route
    /// is straight, dividing the along-line positions in the ratio of the off-line distances; convex,
    /// so clamped to the segment. The two-edge search calls it twice in each of its 24 steps.
    /// </summary>
    internal static float MinimiseOnEdge(Vector3 a, Vector3 b, Vector3 from, Vector3 to)
    {
        Vector3 ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-12f) return 0.5f;
        float sF = Vector3.Dot(from - a, ab) / len2;
        float sT = Vector3.Dot(to - a, ab) / len2;
        float rF = Vector3.Distance(from, a + ab * sF);
        float rT = Vector3.Distance(to, a + ab * sT);
        float s = rF + rT > 1e-9f ? sF + (sT - sF) * rF / (rF + rT) : 0.5f * (sF + sT);
        return Math.Clamp(s, 0f, 1f);
    }

    /// <summary>Where on two edges the route over one, across the face and over the other is shortest.</summary>
    private static void MinimiseOnPair(Vector3 a1, Vector3 b1, Vector3 a2, Vector3 b2, Vector3 from, Vector3 to,
                                       out float t, out float u)
    {
        float lo = 0f, hi = 1f;
        for (int i = 0; i < 24; i++)
        {
            float m1 = lo + (hi - lo) / 3f, m2 = hi - (hi - lo) / 3f;
            if (BestVia(a1, b1, a2, b2, m1, from, to, out _) <= BestVia(a1, b1, a2, b2, m2, from, to, out _)) hi = m2;
            else lo = m1;
        }
        t = (lo + hi) * 0.5f;
        BestVia(a1, b1, a2, b2, t, from, to, out u);
    }

    private static float BestVia(Vector3 a1, Vector3 b1, Vector3 a2, Vector3 b2, float t, Vector3 from, Vector3 to, out float u)
    {
        Vector3 p = Vector3.Lerp(a1, b1, t);
        u = MinimiseOnEdge(a2, b2, p, to);
        return Vector3.Distance(from, p) + Detour(a2, b2, u, p, to);
    }

    private static float Detour(Vector3 a, Vector3 b, float t, Vector3 from, Vector3 to)
    {
        Vector3 p = Vector3.Lerp(a, b, t);
        return Vector3.Distance(from, p) + Vector3.Distance(p, to);
    }

    /// <summary>How far short of a surface point a leg stops before it is tested, metres. Not a shrunk
    /// box: that lifts its base off the ground, and a route under a 36 m tower read clear.</summary>
    private const float EdgeSkin = 0.02f;

    /// <summary>Whether the run from a point to a point on the box's surface stays outside it, the
    /// surface end pulled back by <see cref="EdgeSkin"/> so a leg arriving at its edge is not inside.</summary>
    private static bool LegIsClear(Vector3 from, Vector3 surfacePoint, Vector3 centre, Vector3 size, Quaternion rotation)
    {
        Vector3 d = surfacePoint - from;
        float len = d.Length();
        if (len <= EdgeSkin) return true;
        Vector3 stop = surfacePoint - d / len * EdgeSkin;
        return !GeometryUtils.LineIntersectsOBB(from, stop, centre, size, rotation);
    }

}
