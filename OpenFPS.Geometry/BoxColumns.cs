using System.Numerics;
using OpenFPS.Common.Geometry;

namespace OpenFPS.Common;

/// <summary>
/// Boxes asked by extent, for finding the ones near another box without asking about every one. What it
/// returns is in the order the boxes were added, so code that reads them in order reads them exactly as a
/// scan of the whole list would, and gets the same answer.
///
/// For the passes that ask every room about every wall: a city is thousands of each, a town tens of
/// thousands, and a scan per room grows with the square of the map (a 3 km map of a real place took
/// 18 s in the server's survey and 16 s in the client's openings before boxes were filed at all).
///
/// A tree over the boxes' extents (the triangle world's BVH builder, geometry stage 2), built once, the
/// first time it is asked: every box whose extent meets the one asked about, no more. It was a grid of
/// ground-plane columns, which handed back every box in every column touched, at every height, and kept
/// the ground under a map on a list every question included.
/// </summary>
public sealed class BoxColumns
{
    private readonly List<(Vector3 Lo, Vector3 Hi, int Index)> _boxes = new();
    private BvhNode[]? _nodes;
    private int[] _order = System.Array.Empty<int>();

    /// <summary><paramref name="cellMetres"/> is kept for the callers that name it; the tree needs none.</summary>
    public BoxColumns(float cellMetres) { }

    /// <summary>Files box number <paramref name="index"/>, given its extent. Add them in order.</summary>
    public void Add(int index, Vector3 lo, Vector3 hi)
    {
        _boxes.Add((Vector3.Min(lo, hi), Vector3.Max(lo, hi), index));
        _nodes = null;
    }

    /// <summary>Every box whose extent meets this one, once each, in the order they were added: the boxes
    /// that overlap it, which each caller tests as the scan did.</summary>
    public void Collect(Vector3 lo, Vector3 hi, List<int> into)
    {
        into.Clear();
        if (_boxes.Count == 0) return;
        if (_nodes == null)
        {
            var bmin = new Vector3[_boxes.Count]; var bmax = new Vector3[_boxes.Count];
            for (int i = 0; i < _boxes.Count; i++) { bmin[i] = _boxes[i].Lo; bmax[i] = _boxes[i].Hi; }
            _nodes = BvhBuilder.Build(bmin, bmax, _boxes.Count, 2, out _order);
        }
        var min = Vector3.Min(lo, hi); var max = Vector3.Max(lo, hi);
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref _nodes[stack[--sp]];
            if (!BvhBuilder.Overlaps(n, min, max)) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    var b = _boxes[_order[i]];
                    if (b.Hi.X < min.X || b.Lo.X > max.X || b.Hi.Y < min.Y || b.Lo.Y > max.Y || b.Hi.Z < min.Z || b.Lo.Z > max.Z) continue;
                    into.Add(b.Index);
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
        into.Sort();
        int m = 0;
        for (int i = 0; i < into.Count; i++)
            if (i == 0 || into[i] != into[i - 1]) into[m++] = into[i];
        into.RemoveRange(m, into.Count - m);
    }
}
