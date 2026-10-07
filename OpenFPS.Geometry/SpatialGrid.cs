using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// A high-performance spatial partitioning structure used to optimize collision checks and entity queries.
/// It divides the world into a 2D grid of "cells" (buckets) to reduce the search space from O(N) to roughly O(1).
/// </summary>
/// <typeparam name="T">The type of item stored in the grid (usually Entity IDs).</typeparam>
public class SpatialGrid<T>
{
    private readonly float _cellSize;
    private readonly Dictionary<(int, int), List<T>> _staticGrid = new();

    // ── What moves: a tree, not cells (geometry stage 2) ─────────────────────────────────────────────
    //
    // The dynamic half was a dictionary of cells torn down and filled again every tick. What moves is now
    // a list of the cells each thing covers, as it was filed, and a tree over them built the first time
    // it is asked after a change (the triangle world's BVH builder): a question about the cells within a
    // radius hands back exactly the things the cells did, in the order they were filed.
    private readonly List<(T Item, int X0, int Z0, int X1, int Z1)> _dynamic = new();
    private OpenFPS.Common.Geometry.BvhNode[]? _dynamicNodes;
    private int[] _dynamicOrder = Array.Empty<int>();
    private readonly object _dynamicBuild = new();
    [ThreadStatic] private static List<int>? _dynamicHits;

    /// <summary>How many moving things are filed this tick.</summary>
    public int DynamicCount => _dynamic.Count;

    /// <summary>Where each static item was filed: the cells (x0, z0) to (x1, z1), or the oversize list, so
    /// one can be taken out again without rebuilding the grid (<see cref="RemoveStatic"/>).</summary>
    private readonly Dictionary<T, (int X0, int Z0, int X1, int Z1, bool Oversize)> _staticSpans = new();

    /// <summary>
    /// Bumped every time the STATIC half of the grid changes (a static add, or a full clear). The dynamic
    /// half is torn down and rebuilt every tick, so a version that counted it would change every tick and
    /// be useless as a cache key; this one only moves when the world's geometry actually does. Anything
    /// memoizing a query against static geometry — the server's ground probe, for one — keeps the version
    /// it was computed at and recomputes when it no longer matches.
    /// </summary>
    public int StaticVersion { get; private set; }

    /// <summary>
    /// Creates a new spatial grid. It has no bounds: cells exist where something has been added.
    /// </summary>
    /// <param name="cellSize">The size (in meters) of each square cell in the grid.</param>
    public SpatialGrid(float cellSize)
    {
        _cellSize = cellSize;
    }

    /// <summary>
    /// A grid that keeps any STATIC item covering more than <paramref name="oversizeCells"/> cells in a
    /// list of its own, handed back by every query, instead of filing it in each cell. A town's ground is
    /// one slab three kilometres square: ninety thousand cells, three quarters of the work of filling a
    /// client's grid, every time a tile of it arrives (ClientWorldState). Every query then has it, as it
    /// would anyway wherever there is ground; callers test what they are handed against its box.
    /// </summary>
    public SpatialGrid(float cellSize, int oversizeCells) : this(cellSize)
    {
        _oversizeCells = oversizeCells;
    }

    private readonly int _oversizeCells;
    private readonly List<T> _oversize = new();

    /// <summary>Static items too big to file by cell, handed back by every query.</summary>
    public IReadOnlyList<T> Oversize => _oversize;

    /// <summary>
    /// Maps a 3D world position to a 2D grid coordinate (ignoring Y/Height).
    /// </summary>
    private (int, int) GetCell(Vector3 pos)
    {
        return ((int)Math.Floor(pos.X / _cellSize), (int)Math.Floor(pos.Z / _cellSize));
    }

    /// <summary>
    /// Adds an item to the single cell corresponding to its exact center position.
    /// </summary>
    public void Add(Vector3 pos, T item, bool isStatic = false)
    {
        var cell = GetCell(pos);
        if (!isStatic) { AddDynamic(item, cell.Item1, cell.Item2, cell.Item1, cell.Item2); return; }
        var grid = _staticGrid;
        if (!grid.TryGetValue(cell, out var list))
        {
            list = new List<T>();
            grid[cell] = list;
        }
        list.Add(item);
        if (isStatic) { _staticSpans[item] = (cell.Item1, cell.Item2, cell.Item1, cell.Item2, false); StaticVersion++; }
    }

    /// <summary>
    /// Adds an item to every cell that overlaps with its 3D bounding box (size).
    /// This is essential for large static objects like walls that span multiple cells.
    /// </summary>
    /// <summary>
    /// A box that is TURNED: indexed by the square footprint that contains it.
    ///
    /// The plain overload takes the size as if the box were lined up with the grid, and every caller
    /// handed it a rotated box's own local size. A bus heading east is 10.9 m along X; indexed by its
    /// local size it was filed as 10.9 m along Z, so the cells beside its front and back never knew
    /// it was there and a player standing at its bumper was never tested against it. A wall turned a
    /// few degrees off the grid is wrong by less, and wrong the same way.
    /// </summary>
    public void AddOverlapping(Vector3 pos, Vector3 size, Quaternion rotation, T item, bool isStatic = false)
    {
        // An unset rotation is all zeros, not the identity, and would squash the box to a point.
        if (rotation == Quaternion.Identity || rotation.LengthSquared() < 1e-6f)
        { AddOverlapping(pos, size, item, isStatic); return; }
        var half = size * 0.5f;
        var x = Vector3.Transform(new Vector3(half.X, 0f, 0f), rotation);
        var y = Vector3.Transform(new Vector3(0f, half.Y, 0f), rotation);
        var z = Vector3.Transform(new Vector3(0f, 0f, half.Z), rotation);
        var bounds = 2f * new Vector3(
            MathF.Abs(x.X) + MathF.Abs(y.X) + MathF.Abs(z.X),
            MathF.Abs(x.Y) + MathF.Abs(y.Y) + MathF.Abs(z.Y),
            MathF.Abs(x.Z) + MathF.Abs(y.Z) + MathF.Abs(z.Z));
        AddOverlapping(pos, bounds, item, isStatic);
    }

    public void AddOverlapping(Vector3 pos, Vector3 size, T item, bool isStatic = false)
    {
        // Add tiny epsilon to ensure boundary-aligned objects are indexed in the edge cells
        const float epsilon = 0.001f;
        Vector3 min = pos - (size / 2f) + new Vector3(epsilon, 0, epsilon);
        Vector3 max = pos + (size / 2f) - new Vector3(epsilon, 0, epsilon);

        int minX = (int)Math.Floor(min.X / _cellSize);
        int minZ = (int)Math.Floor(min.Z / _cellSize);
        int maxX = (int)Math.Floor(max.X / _cellSize);
        int maxZ = (int)Math.Floor(max.Z / _cellSize);

        if (!isStatic) { AddDynamic(item, minX, minZ, maxX, maxZ); return; }
        var grid = _staticGrid;

        if (isStatic && _oversizeCells > 0 && (long)(maxX - minX + 1) * (maxZ - minZ + 1) > _oversizeCells)
        {
            _oversize.Add(item);
            _staticSpans[item] = (0, 0, -1, -1, true);
            StaticVersion++;
            return;
        }

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                var cell = (x, z);
                if (!grid.TryGetValue(cell, out var list))
                {
                    list = new List<T>();
                    grid[cell] = list;
                }
                list.Add(item);
            }
        }

        if (isStatic) { _staticSpans[item] = (minX, minZ, maxX, maxZ, false); StaticVersion++; }
    }

    private void AddDynamic(T item, int x0, int z0, int x1, int z1)
    {
        _dynamic.Add((item, x0, z0, x1, z1));
        _dynamicNodes = null;
    }

    /// <summary>The moving things filed in any of the cells (x0..x1, z0..z1), in the order they were filed,
    /// each once, into <paramref name="into"/> (and <paramref name="seen"/>) after what is there.</summary>
    private void DynamicIn(int x0, int z0, int x1, int z1, List<T> into, HashSet<T> seen)
    {
        if (_dynamic.Count == 0) return;
        var nodes = _dynamicNodes;
        if (nodes == null)
            lock (_dynamicBuild)
            {
                nodes = _dynamicNodes;
                if (nodes == null)
                {
                    var lo = new Vector3[_dynamic.Count]; var hi = new Vector3[_dynamic.Count];
                    for (int i = 0; i < _dynamic.Count; i++)
                    {
                        var d = _dynamic[i];
                        lo[i] = new Vector3(d.X0, 0f, d.Z0); hi[i] = new Vector3(d.X1, 0f, d.Z1);
                    }
                    nodes = OpenFPS.Common.Geometry.BvhBuilder.Build(lo, hi, _dynamic.Count, 4, out _dynamicOrder);
                    _dynamicNodes = nodes;
                }
            }
        var order = _dynamicOrder;
        var hits = _dynamicHits ??= new List<int>(32);
        hits.Clear();
        Span<int> stack = stackalloc int[OpenFPS.Common.Geometry.BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref nodes[stack[--sp]];
            if (n.Max.X < x0 || n.Min.X > x1 || n.Max.Z < z0 || n.Min.Z > z1) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    var d = _dynamic[order[i]];
                    if (d.X1 < x0 || d.X0 > x1 || d.Z1 < z0 || d.Z0 > z1) continue;
                    hits.Add(order[i]);
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
        hits.Sort();
        foreach (int i in hits)
        {
            var item = _dynamic[i].Item;
            if (seen.Add(item)) into.Add(item);
        }
    }

    /// <summary>
    /// Takes a static item out of the cells it was filed in (or the oversize list). False if it was not
    /// filed. The order of what is left in each cell is kept.
    /// </summary>
    public bool RemoveStatic(T item)
    {
        if (!_staticSpans.Remove(item, out var span)) return false;
        if (span.Oversize) _oversize.Remove(item);
        else
            for (int x = span.X0; x <= span.X1; x++)
                for (int z = span.Z0; z <= span.Z1; z++)
                    if (_staticGrid.TryGetValue((x, z), out var list))
                    {
                        list.Remove(item);
                        if (list.Count == 0) _staticGrid.Remove((x, z));
                    }
        StaticVersion++;
        return true;
    }

    /// <summary>
    /// Returns all items located in cells that overlap with the specified radius around a position.
    /// This is an approximation; result may contain items outside the exact radius but within the same cells.
    /// </summary>
    public IEnumerable<T> GetItemsInRadius(Vector3 pos, float radius)
    {
        for (int i = 0; i < _oversize.Count; i++) yield return _oversize[i];
        int cellRadius = (int)Math.Ceiling(radius / _cellSize);
        var centerCell = GetCell(pos);

        for (int x = -cellRadius; x <= cellRadius; x++)
        {
            for (int z = -cellRadius; z <= cellRadius; z++)
            {
                var cell = (centerCell.Item1 + x, centerCell.Item2 + z);
                if (_staticGrid.TryGetValue(cell, out var staticList))
                {
                    foreach (var item in staticList) yield return item;
                }
            }
        }
        var moving = new List<T>(); var seen = new HashSet<T>();
        DynamicIn(centerCell.Item1 - cellRadius, centerCell.Item2 - cellRadius, centerCell.Item1 + cellRadius, centerCell.Item2 + cellRadius, moving, seen);
        foreach (var item in moving) yield return item;
    }

    /// <summary>
    /// The allocation-free, repeat-free form of <see cref="GetItemsInRadius"/>: fills <paramref name="into"/>
    /// with every distinct item in the overlapping cells.
    ///
    /// Two things make this the form the hot paths want. <see cref="GetItemsInRadius"/> is an iterator, so
    /// asking it for a count and then walking it — which both the server's collision gather and the client's
    /// candidate query did — walks every cell TWICE. And a wall wide enough to span cells is filed in each
    /// one, so a plain walk hands the same wall back four or nine times and every caller then ray-tests it
    /// four or nine times. Deduplicating here fixes that once, for everyone.
    ///
    /// The caller owns both buffers (they are cleared on entry), which is what keeps the grid itself free of
    /// per-call state and therefore safe to read from several threads at once.
    /// </summary>
    public void CollectInRadius(Vector3 pos, float radius, List<T> into, HashSet<T> seen)
    {
        into.Clear();
        seen.Clear();
        for (int i = 0; i < _oversize.Count; i++)
            if (seen.Add(_oversize[i])) into.Add(_oversize[i]);

        int cellRadius = (int)Math.Ceiling(radius / _cellSize);
        var centerCell = GetCell(pos);

        for (int x = -cellRadius; x <= cellRadius; x++)
        {
            for (int z = -cellRadius; z <= cellRadius; z++)
            {
                var cell = (centerCell.Item1 + x, centerCell.Item2 + z);
                if (_staticGrid.TryGetValue(cell, out var staticList))
                {
                    for (int i = 0; i < staticList.Count; i++)
                        if (seen.Add(staticList[i])) into.Add(staticList[i]);
                }
            }
        }
        DynamicIn(centerCell.Item1 - cellRadius, centerCell.Item2 - cellRadius, centerCell.Item1 + cellRadius, centerCell.Item2 + cellRadius, into, seen);
    }

    /// <summary>
    /// Clears only the dynamic items from the grid.
    /// Should be called every tick before re-populating if tracking dynamic entities.
    /// </summary>
    public void Clear()
    {
        _dynamic.Clear();
        _dynamicNodes = null;
    }

    /// <summary>
    /// Clears everything, including static geometry.
    /// </summary>
    public void ClearAll()
    {
        _staticGrid.Clear();
        _dynamic.Clear();
        _dynamicNodes = null;
        _oversize.Clear();
        _unindexed.Clear();
        _staticSpans.Clear();
        StaticVersion++;
    }

    // ── The static geometry as triangles (docs/GEOMETRY.md, stage 1) ───────────────────────────────
    //
    // The queries that have moved to the triangle world ask it about static geometry and ask this grid
    // only about what moves (its dynamic half) and about the few static things the triangle world does
    // not hold (Unindexed): a shape that is not a box, and anything added since the world was last built.

    private readonly List<T> _unindexed = new();

    /// <summary>The static solid boxes of this grid as a triangle world, or null where nothing has built one
    /// (every query then answers from the grid, as before). Its movers (door leaves) are brought up to
    /// where they stand first (<see cref="BeforeGeometry"/>).</summary>
    public OpenFPS.Common.Geometry.TriangleWorld? Geometry
    {
        get { BeforeGeometry?.Invoke(); return _geometry; }
        private set => _geometry = value;
    }
    private OpenFPS.Common.Geometry.TriangleWorld? _geometry;

    /// <summary>Run before <see cref="Geometry"/> is handed out: the owner places the movers again if any
    /// has moved since it last did (ServerGeometry, MoverPoses).</summary>
    public Action? BeforeGeometry { get; set; }

    /// <summary>Static items <see cref="Geometry"/> does not hold: test them as the grid always did.</summary>
    public IReadOnlyList<T> Unindexed => _unindexed;

    /// <summary>Puts a newly built triangle world in place, with the static items it does not hold.</summary>
    public void SetGeometry(OpenFPS.Common.Geometry.TriangleWorld? geometry, IEnumerable<T> unindexed)
    {
        _unindexed.Clear();
        _unindexed.AddRange(unindexed);
        Geometry = geometry;
        StaticVersion++;
    }

    /// <summary>The same geometry with movers in new poses (door leaves): no static item changed.</summary>
    public void MoveGeometry(OpenFPS.Common.Geometry.TriangleWorld geometry)
    {
        if (ReferenceEquals(geometry, _geometry)) return;
        Geometry = geometry;
        StaticVersion++;
    }

    /// <summary>A static item added after <see cref="Geometry"/> was built: tested the old way until the
    /// next build takes it in.</summary>
    public void AddUnindexed(T item)
    {
        if (_geometry != null) _unindexed.Add(item);
    }

    /// <summary>
    /// What the triangle world does not answer for near a point: every item of the dynamic half in the
    /// cells round it, and every <see cref="Unindexed"/> static item, each once.
    /// </summary>
    public void CollectDynamicInRadius(Vector3 pos, float radius, List<T> into, HashSet<T> seen)
    {
        into.Clear();
        seen.Clear();
        for (int i = 0; i < _unindexed.Count; i++)
            if (seen.Add(_unindexed[i])) into.Add(_unindexed[i]);
        int cellRadius = (int)Math.Ceiling(radius / _cellSize);
        var centerCell = GetCell(pos);
        DynamicIn(centerCell.Item1 - cellRadius, centerCell.Item2 - cellRadius, centerCell.Item1 + cellRadius, centerCell.Item2 + cellRadius, into, seen);
    }
}
