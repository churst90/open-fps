using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace OpenFPS.Common.Geometry;

/// <summary>
/// One node of a flattened bounding volume hierarchy, 32 bytes: its bounds, and either its first child
/// (<see cref="Count"/> 0; the second child follows the first) or its first primitive and how many.
/// </summary>
public struct BvhNode
{
    public Vector3 Min;
    public int LeftFirst;
    public Vector3 Max;
    public int Count;

    public readonly bool IsLeaf => Count > 0;
}

/// <summary>
/// Builds a binary BVH over primitives given by their bounds: binned surface area heuristic (12 bins),
/// leaves of up to <c>maxLeaf</c>, nodes flattened into one array (docs/GEOMETRY.md 2.2). Single-threaded
/// and free of anything that depends on the machine, so the same primitives in the same order give the
/// same tree, bit for bit, on the server and every client.
/// </summary>
public static class BvhBuilder
{
    private const int Bins = 12;

    /// <summary>Below this depth the tree only halves its lists, so no tree is deeper than this plus
    /// log2 of its size and every traversal fits a fixed stack (<see cref="MaxDepth"/>).</summary>
    private const int MaxSahDepth = 64;

    /// <summary>The deepest any tree built here can be: the traversal stacks are this big.</summary>
    public const int MaxDepth = MaxSahDepth + 32;

    /// <summary>
    /// The tree over <paramref name="count"/> primitives. <paramref name="order"/> comes back as the
    /// primitives in leaf order: a leaf's primitives are order[LeftFirst .. LeftFirst + Count).
    /// </summary>
    public static BvhNode[] Build(ReadOnlySpan<Vector3> bmin, ReadOnlySpan<Vector3> bmax, int count, int maxLeaf, out int[] order)
    {
        order = new int[count];
        for (int i = 0; i < count; i++) order[i] = i;
        if (count == 0)
            return new[] { new BvhNode { Min = Vector3.Zero, Max = Vector3.Zero, LeftFirst = 0, Count = 0 } };

        var cen = new Vector3[count];
        for (int i = 0; i < count; i++) cen[i] = (bmin[i] + bmax[i]) * 0.5f;
        var nodes = new BvhNode[Math.Max(1, 2 * count - 1)];
        int nodeCount = 1;
        nodes[0] = new BvhNode { LeftFirst = 0, Count = count };
        Bounds(ref nodes[0], order, bmin, bmax);

        var stack = new int[128];
        var depthOf = new int[128];
        int sp = 0;
        depthOf[sp] = 0; stack[sp++] = 0;
        Span<int> binCount = stackalloc int[Bins];
        Span<Vector3> binMin = stackalloc Vector3[Bins];
        Span<Vector3> binMax = stackalloc Vector3[Bins];
        Span<float> leftArea = stackalloc float[Bins - 1];
        Span<int> leftCount = stackalloc int[Bins - 1];

        while (sp > 0)
        {
            int depth = depthOf[--sp];
            int ni = stack[sp];
            int first = nodes[ni].LeftFirst, n = nodes[ni].Count;
            if (n <= maxLeaf) continue;

            Vector3 cmin = new(float.MaxValue), cmax = new(float.MinValue);
            for (int i = first; i < first + n; i++) { cmin = Vector3.Min(cmin, cen[order[i]]); cmax = Vector3.Max(cmax, cen[order[i]]); }

            float bestCost = float.MaxValue; int bestAxis = -1, bestSplit = -1;
            for (int axis = 0; axis < 3; axis++)
            {
                float lo = Get(cmin, axis), hi = Get(cmax, axis);
                if (hi - lo < 1e-6f) continue;
                float scale = Bins / (hi - lo);
                binCount.Clear();
                for (int k = 0; k < Bins; k++) { binMin[k] = new Vector3(float.MaxValue); binMax[k] = new Vector3(float.MinValue); }
                for (int i = first; i < first + n; i++)
                {
                    int t = order[i];
                    int k = Math.Min(Bins - 1, (int)((Get(cen[t], axis) - lo) * scale));
                    binCount[k]++; binMin[k] = Vector3.Min(binMin[k], bmin[t]); binMax[k] = Vector3.Max(binMax[k], bmax[t]);
                }
                Vector3 lmin = new(float.MaxValue), lmax = new(float.MinValue); int lc = 0;
                for (int k = 0; k < Bins - 1; k++)
                {
                    lc += binCount[k]; lmin = Vector3.Min(lmin, binMin[k]); lmax = Vector3.Max(lmax, binMax[k]);
                    leftCount[k] = lc; leftArea[k] = lc > 0 ? Area(lmin, lmax) : 0f;
                }
                Vector3 rmin = new(float.MaxValue), rmax = new(float.MinValue); int rc = 0;
                for (int k = Bins - 1; k > 0; k--)
                {
                    rc += binCount[k]; rmin = Vector3.Min(rmin, binMin[k]); rmax = Vector3.Max(rmax, binMax[k]);
                    float cost = leftArea[k - 1] * leftCount[k - 1] + (rc > 0 ? Area(rmin, rmax) * rc : 0f);
                    if (leftCount[k - 1] > 0 && rc > 0 && cost < bestCost) { bestCost = cost; bestAxis = axis; bestSplit = k; }
                }
            }

            float leafCost = Area(nodes[ni].Min, nodes[ni].Max) * n;
            int mid;
            if (bestAxis < 0 || depth >= MaxSahDepth)
                mid = first + n / 2;                        // every centroid in one place, or deep: halve the list
            else
            {
                if (bestCost >= leafCost && n <= 16) continue;
                float lo = Get(cmin, bestAxis), scale = Bins / (Get(cmax, bestAxis) - lo);
                int i = first, j = first + n - 1;
                while (i <= j)
                {
                    int k = Math.Min(Bins - 1, (int)((Get(cen[order[i]], bestAxis) - lo) * scale));
                    if (k < bestSplit) i++; else { (order[i], order[j]) = (order[j], order[i]); j--; }
                }
                mid = i;
                if (mid == first || mid == first + n) mid = first + n / 2;
            }

            int l = nodeCount++, r = nodeCount++;
            nodes[l] = new BvhNode { LeftFirst = first, Count = mid - first };
            nodes[r] = new BvhNode { LeftFirst = mid, Count = first + n - mid };
            Bounds(ref nodes[l], order, bmin, bmax);
            Bounds(ref nodes[r], order, bmin, bmax);
            nodes[ni].LeftFirst = l; nodes[ni].Count = 0;
            if (sp + 2 > stack.Length) { Array.Resize(ref stack, stack.Length * 2); Array.Resize(ref depthOf, depthOf.Length * 2); }
            depthOf[sp] = depth + 1; stack[sp++] = r;
            depthOf[sp] = depth + 1; stack[sp++] = l;
        }
        if (nodeCount < nodes.Length) Array.Resize(ref nodes, nodeCount);
        return nodes;
    }

    /// <summary>Recomputes every node's bounds from new primitive bounds, keeping the tree's shape: the
    /// cheap update when things move but nothing is added or taken away.</summary>
    public static void Refit(BvhNode[] nodes, int[] order, ReadOnlySpan<Vector3> bmin, ReadOnlySpan<Vector3> bmax)
    {
        // Children are always written after their parent, so walking backwards visits children first.
        for (int ni = nodes.Length - 1; ni >= 0; ni--)
        {
            ref var n = ref nodes[ni];
            if (n.Count > 0) { Bounds(ref n, order, bmin, bmax); continue; }
            if (n.LeftFirst == 0 && ni == 0 && nodes.Length == 1) continue;
            ref var a = ref nodes[n.LeftFirst];
            ref var b = ref nodes[n.LeftFirst + 1];
            n.Min = Vector3.Min(a.Min, b.Min); n.Max = Vector3.Max(a.Max, b.Max);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Get(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;

    private static float Area(Vector3 mn, Vector3 mx) { var e = mx - mn; return e.X * e.Y + e.Y * e.Z + e.Z * e.X; }

    private static void Bounds(ref BvhNode n, int[] order, ReadOnlySpan<Vector3> bmin, ReadOnlySpan<Vector3> bmax)
    {
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++) { mn = Vector3.Min(mn, bmin[order[i]]); mx = Vector3.Max(mx, bmax[order[i]]); }
        n.Min = mn; n.Max = mx;
    }

    /// <summary>
    /// The entry distance of a ray into a node's box, or <see cref="float.MaxValue"/> if it misses it
    /// within [tMin, tMax]. <paramref name="inv"/> is 1/direction with zeros replaced by a huge number.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Slab(in BvhNode n, Vector3 o, Vector3 inv, float tMin, float tMax)
    {
        var t1 = (n.Min - o) * inv; var t2 = (n.Max - o) * inv;
        var lo = Vector3.Min(t1, t2); var hi = Vector3.Max(t1, t2);
        float tn = MathF.Max(MathF.Max(lo.X, lo.Y), MathF.Max(lo.Z, tMin));
        float tf = MathF.Min(MathF.Min(hi.X, hi.Y), MathF.Min(hi.Z, tMax));
        return tn <= tf ? tn : float.MaxValue;
    }

    /// <summary>1/d per axis, a direction parallel to an axis kept finite and of the right sign.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector3 Inverse(Vector3 d)
        => new(1f / (MathF.Abs(d.X) < 1e-12f ? (d.X < 0f ? -1e-12f : 1e-12f) : d.X),
               1f / (MathF.Abs(d.Y) < 1e-12f ? (d.Y < 0f ? -1e-12f : 1e-12f) : d.Y),
               1f / (MathF.Abs(d.Z) < 1e-12f ? (d.Z < 0f ? -1e-12f : 1e-12f) : d.Z));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Overlaps(in BvhNode n, Vector3 min, Vector3 max)
        => n.Max.X >= min.X && n.Min.X <= max.X && n.Max.Y >= min.Y && n.Min.Y <= max.Y && n.Max.Z >= min.Z && n.Min.Z <= max.Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Contains(in BvhNode n, Vector3 p)
        => p.X >= n.Min.X && p.X <= n.Max.X && p.Y >= n.Min.Y && p.Y <= n.Max.Y && p.Z >= n.Min.Z && p.Z <= n.Max.Z;
}
