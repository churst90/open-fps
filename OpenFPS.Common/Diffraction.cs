using System;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// What a barrier actually does to a sound, as opposed to whether it is in the way.
///
/// A line-of-sight test answers a yes/no question, and a yes/no question has no place in the level of
/// anything. Sound does not stop at an edge; it bends round it, and how much of it arrives depends on
/// how far out of its way it had to go — the PATH DIFFERENCE between going round the obstacle and
/// going straight through it — measured against the wavelength. That single number, and nothing about
/// what the obstacle is, decides whether a wall is a wall or a nuisance.
///
/// The fault this exists to fix: a knee-high pit wall between a listener and a car reported "blocked",
/// the engine took that literally, and a 130 dB engine twenty metres away went to twenty-six decibels
/// down with its top three octaves removed — which is to say, gone. Over a 0.9 m wall the real path
/// difference is a few centimetres: five to eight decibels, and mostly at the top end. The wall should
/// have sounded like a wall you can see over, because that is what it is.
///
/// Maekawa's empirical curve is the model, because it is the one that matches measurement across the
/// whole useful range from "barely blocked" to "deep shadow", it needs only the path difference and
/// the frequency, and it has the two properties the fault above lacked: it is CONTINUOUS (a source
/// drifting behind an edge fades rather than switches) and it has a CEILING (a single screen cannot
/// take more than about 24 dB, however tall, because the sound goes round the ends and over the top
/// and comes back off everything else).
///
/// Nothing here knows what a wall, a car, a track or a map is. It takes two points and a box.
/// </summary>
public static class Diffraction
{
    /// <summary>
    /// Most a single barrier may take, dB.
    ///
    /// Not a fudge — a measured limit. Past about this, the energy arriving has stopped coming over
    /// the screen at all and is arriving by paths the screen does not control: round its ends, off the
    /// ground, off everything else in the scene. Barrier design handbooks stop at 24 dB for exactly
    /// this reason, and a model without the ceiling will happily silence a source behind a tall fence.
    /// </summary>
    public const float MaxInsertionLossDb = 24.0f;

    /// <summary>
    /// Least a barrier takes once it blocks the line at all, dB.
    ///
    /// Falls out of the curve rather than being imposed: at a path difference of zero — the source
    /// exactly grazing the edge — Maekawa gives 5 dB, which is the well-known "on the shadow boundary"
    /// value. It is here as a named constant only so the tests can say what they are checking.
    /// </summary>
    public const float GrazingInsertionLossDb = 5.0f;

    /// <summary>
    /// Insertion loss of a single screen, dB, for one frequency.
    ///
    /// <paramref name="pathDifference"/> is metres of extra path the sound had to take to get round the
    /// obstacle. Zero means it just grazes the edge. Negative (the line is clear) means no loss at all.
    /// </summary>
    public static float InsertionLossDb(float pathDifference, float frequencyHz, float speedOfSound = 343.0f)
    {
        if (!float.IsFinite(pathDifference) || pathDifference <= 0f) return 0f;
        if (frequencyHz <= 0f || speedOfSound <= 0f) return 0f;

        // Fresnel number: how many half-wavelengths of detour the barrier cost.
        float lambda = speedOfSound / frequencyHz;
        float n = 2f * pathDifference / lambda;

        double root = Math.Sqrt(2.0 * Math.PI * n);
        // tanh(x)/x -> 1 as x -> 0, so the ratio -> 1 and the loss -> 5 dB. Guarded because tanh(0) is
        // zero and the division is not defined there.
        double ratio = root < 1e-6 ? 1.0 : root / Math.Tanh(root);
        double db = 5.0 + 20.0 * Math.Log10(ratio);
        return (float)Math.Clamp(db, 0.0, MaxInsertionLossDb);
    }

    /// <summary>The same thing as a linear gain, 0..1, which is what a mixer wants.</summary>
    public static float BandGain(float pathDifference, float frequencyHz, float speedOfSound = 343.0f)
        => MathF.Pow(10f, -InsertionLossDb(pathDifference, frequencyHz, speedOfSound) / 20f);

    // ── Representative frequencies for the engine's three bands ─────────────────────────────────
    //
    // The mixer filters in three bands split at 400 Hz and 4 kHz (FMOD's THREE_EQ crossovers), so a
    // per-band diffraction gain has to be evaluated SOMEWHERE in each band. These are the geometric
    // middles of what each band actually covers for the sounds this engine makes. They matter: the
    // whole character of a barrier is that it takes the top off and leaves the bottom, and that is
    // entirely a consequence of evaluating the same path difference at 200 Hz and at 8 kHz.
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
    /// in the way at all.
    ///
    /// The shortest route past a convex obstacle runs over its silhouette, and on a box that means it
    /// crosses either ONE edge — the case of a thin wall, where the sound simply bends over the top —
    /// or TWO, when the obstacle has depth and the route has to climb one edge, run across the face
    /// and drop off the far one. Both are searched, because a model with only the first cannot answer
    /// for anything thicker than a fence: every over-the-top candidate for a sixteen-metre-deep
    /// grandstand passes through the building itself and is correctly thrown out, leaving no route at
    /// all and a barrier that is once again a boolean.
    ///
    /// The distance along an edge is convex, which is what makes a ternary search exact rather than a
    /// guess; the two-edge case alternates between them until both settle. Candidates whose legs would
    /// pass THROUGH the box are thrown out — without that test the shortest answer for a wall standing
    /// on the ground is always its buried bottom edge, which is a route sound cannot take.
    /// </summary>
    public static bool PathDifferenceAroundBox(Vector3 centre, Vector3 size, Quaternion rotation,
                                               Vector3 source, Vector3 listener, out float pathDifference)
    {
        pathDifference = 0f;
        if (!GeometryUtils.LineIntersectsOBB(source, listener, centre, size, rotation)) return false;

        Vector3 h = size * 0.5f;
        float direct = Vector3.Distance(source, listener);
        float best = float.MaxValue;

        // The eight corners, in the box's own frame, rotated back into the world once each.
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

        // Sound does not tunnel. A candidate that dips below BOTH endpoints is a route underneath
        // something that is standing on the ground, and the ground is not part of this box's search —
        // so without this an obstacle's buried bottom edge always wins, and a thirty-six-metre tower
        // measures as costing nothing. Below one endpoint is fine: that is going under a bridge.
        float floorY = MathF.Min(source.Y, listener.Y) - 0.05f;

        // ── One edge: a thin barrier, bent over ────────────────────────────────────────────
        for (int e = 0; e < 12; e++)
        {
            float t = MinimiseOnEdge(a[e], b[e], source, listener);
            Vector3 p = Vector3.Lerp(a[e], b[e], t);
            float around = Vector3.Distance(source, p) + Vector3.Distance(p, listener);
            if (around >= best) continue;
            if (p.Y < floorY) continue;
            if (!LegIsClear(source, p, centre, size, rotation)) continue;
            if (!LegIsClear(listener, p, centre, size, rotation)) continue;
            best = around;
        }

        // ── Two edges: over a face and down the other side ─────────────────────────────────
        //
        // The pairs are the parallel edges that bound a common face — within each axis group, the
        // two whose signs differ in exactly one place. Four per axis, twelve in all.
        ReadOnlySpan<byte> pairs = stackalloc byte[24]
        {
            0,1, 0,2, 1,3, 2,3,
            4,5, 4,6, 5,7, 6,7,
            8,9, 8,10, 9,11, 10,11,
        };
        for (int k = 0; k < 12; k++)
        {
            int e1 = pairs[k * 2], e2 = pairs[k * 2 + 1];
            float t = 0.5f, u = 0.5f;
            // Alternating: hold one crossing still and solve the other, until both stop moving.
            for (int round = 0; round < 8; round++)
            {
                Vector3 q = Vector3.Lerp(a[e2], b[e2], u);
                t = MinimiseOnEdge(a[e1], b[e1], source, q);
                Vector3 p0 = Vector3.Lerp(a[e1], b[e1], t);
                u = MinimiseOnEdge(a[e2], b[e2], p0, listener);
            }
            Vector3 p = Vector3.Lerp(a[e1], b[e1], t);
            Vector3 r = Vector3.Lerp(a[e2], b[e2], u);
            float around = Vector3.Distance(source, p) + Vector3.Distance(p, r) + Vector3.Distance(r, listener);
            if (around >= best) continue;
            if (p.Y < floorY || r.Y < floorY) continue;
            if (!LegIsClear(source, p, centre, size, rotation)) continue;
            if (!LegIsClear(listener, r, centre, size, rotation)) continue;
            // The run between the two crossings needs no test: the pairs are the edges that bound a
            // common face, a face is planar and convex, and the straight line between two points on
            // one stays on it. Testing it would fail every time for exactly that reason.
            best = around;
        }

        if (best == float.MaxValue) return false;   // wholly enclosed: no route round this box at all
        pathDifference = MathF.Max(0f, best - direct);
        return true;
    }

    /// <summary>Where on the edge the detour is shortest. Convex in t, so ternary search converges.</summary>
    private static float MinimiseOnEdge(Vector3 a, Vector3 b, Vector3 from, Vector3 to)
    {
        float lo = 0f, hi = 1f;
        // Twenty-four iterations narrow a hundred-metre edge to a hundredth of a millimetre, which is
        // several orders finer than anything downstream can tell apart.
        for (int i = 0; i < 24; i++)
        {
            float m1 = lo + (hi - lo) / 3f;
            float m2 = hi - (hi - lo) / 3f;
            if (Detour(a, b, m1, from, to) <= Detour(a, b, m2, from, to)) hi = m2;
            else lo = m1;
        }
        return (lo + hi) * 0.5f;
    }

    private static float Detour(Vector3 a, Vector3 b, float t, Vector3 from, Vector3 to)
    {
        Vector3 p = Vector3.Lerp(a, b, t);
        return Vector3.Distance(from, p) + Vector3.Distance(p, to);
    }

    /// <summary>
    /// How far short of a surface point a leg stops before it is tested, metres.
    ///
    /// Absolute, and deliberately not a percentage of the box. Shrinking the BOX instead lifts its
    /// base off the ground, and a route that dives under a building then reports itself clear — which
    /// is how a thirty-six-metre tower first measured as costing nothing at all.
    /// </summary>
    private const float EdgeSkin = 0.02f;

    /// <summary>True if the straight run from a point to a point ON the box's surface stays outside
    /// it. The surface end is pulled back by <see cref="EdgeSkin"/> so that a leg which correctly
    /// arrives at the edge it is bending around is not reported as passing through it.</summary>
    private static bool LegIsClear(Vector3 from, Vector3 surfacePoint, Vector3 centre, Vector3 size, Quaternion rotation)
    {
        Vector3 d = surfacePoint - from;
        float len = d.Length();
        if (len <= EdgeSkin) return true;
        Vector3 stop = surfacePoint - d / len * EdgeSkin;
        return !GeometryUtils.LineIntersectsOBB(from, stop, centre, size, rotation);
    }

}
