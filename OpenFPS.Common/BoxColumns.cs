using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// Boxes filed by the ground-plane cells they cover, for finding the ones near another box without
/// asking about every one. What it returns is in the order the boxes were added, so code that reads
/// them in order reads them exactly as a scan of the whole list would, and gets the same answer.
///
/// For the passes that ask every room about every wall: a city is thousands of each, a town tens of
/// thousands, and a scan per room grows with the square of the map (a 3 km map of a real place took
/// 18 s in the server's survey and 16 s in the client's openings before this).
///
/// A box covering more than a few hundred cells (the ground under a whole map) is kept on a list of
/// its own that every question includes, rather than filed in tens of thousands of cells.
/// </summary>
public sealed class BoxColumns
{
    private readonly float _cell;
    private readonly Dictionary<(int, int), List<int>> _cells = new();
    private readonly List<int> _big = new();
    private const int MaxCells = 400;

    public BoxColumns(float cellMetres) => _cell = cellMetres;

    /// <summary>Files box number <paramref name="index"/>, given its extent. Add them in order.</summary>
    public void Add(int index, Vector3 lo, Vector3 hi)
    {
        int x0 = Floor(lo.X), x1 = Floor(hi.X), z0 = Floor(lo.Z), z1 = Floor(hi.Z);
        if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > MaxCells) { _big.Add(index); return; }
        for (int x = x0; x <= x1; x++)
        for (int z = z0; z <= z1; z++)
        {
            if (!_cells.TryGetValue((x, z), out var list)) _cells[(x, z)] = list = new List<int>();
            list.Add(index);
        }
    }

    /// <summary>Every box filed in a cell this extent touches, once each, in the order they were
    /// added. A superset of the boxes that overlap it: test each one as the scan did.</summary>
    public void Collect(Vector3 lo, Vector3 hi, List<int> into)
    {
        into.Clear();
        into.AddRange(_big);
        int x0 = Floor(lo.X), x1 = Floor(hi.X), z0 = Floor(lo.Z), z1 = Floor(hi.Z);
        for (int x = x0; x <= x1; x++)
        for (int z = z0; z <= z1; z++)
            if (_cells.TryGetValue((x, z), out var list)) into.AddRange(list);
        into.Sort();
        int n = 0;
        for (int i = 0; i < into.Count; i++)
            if (i == 0 || into[i] != into[i - 1]) into[n++] = into[i];
        into.RemoveRange(n, into.Count - n);
    }

    private int Floor(float v) => (int)MathF.Floor(Math.Clamp(v / _cell, -1e6f, 1e6f));
}
