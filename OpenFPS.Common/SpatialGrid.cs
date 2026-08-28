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
    private readonly Vector2 _min;
    private readonly Vector2 _max;
    private readonly Dictionary<(int, int), List<T>> _staticGrid = new();
    private readonly Dictionary<(int, int), List<T>> _dynamicGrid = new();

    /// <summary>
    /// Bumped every time the STATIC half of the grid changes (a static add, or a full clear). The dynamic
    /// half is torn down and rebuilt every tick, so a version that counted it would change every tick and
    /// be useless as a cache key; this one only moves when the world's geometry actually does. Anything
    /// memoizing a query against static geometry — the server's ground probe, for one — keeps the version
    /// it was computed at and recomputes when it no longer matches.
    /// </summary>
    public int StaticVersion { get; private set; }

    /// <summary>
    /// Creates a new spatial grid with defined boundaries and cell resolution.
    /// </summary>
    /// <param name="min">The minimum world coordinates (X, Z).</param>
    /// <param name="max">The maximum world coordinates (X, Z).</param>
    /// <param name="cellSize">The size (in meters) of each square cell in the grid.</param>
    public SpatialGrid(Vector2 min, Vector2 max, float cellSize)
    {
        _min = min;
        _max = max;
        _cellSize = cellSize;
    }

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
        var grid = isStatic ? _staticGrid : _dynamicGrid;
        if (!grid.TryGetValue(cell, out var list))
        {
            list = new List<T>();
            grid[cell] = list;
        }
        list.Add(item);
        if (isStatic) StaticVersion++;
    }

    /// <summary>
    /// Adds an item to every cell that overlaps with its 3D bounding box (size).
    /// This is essential for large static objects like walls that span multiple cells.
    /// </summary>
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

        var grid = isStatic ? _staticGrid : _dynamicGrid;

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

        if (isStatic) StaticVersion++;
    }

    /// <summary>
    /// Returns all items located in cells that overlap with the specified radius around a position.
    /// This is an approximation; result may contain items outside the exact radius but within the same cells.
    /// </summary>
    public IEnumerable<T> GetItemsInRadius(Vector3 pos, float radius)
    {
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
                if (_dynamicGrid.TryGetValue(cell, out var dynamicList))
                {
                    foreach (var item in dynamicList) yield return item;
                }
            }
        }
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
                if (_dynamicGrid.TryGetValue(cell, out var dynamicList))
                {
                    for (int i = 0; i < dynamicList.Count; i++)
                        if (seen.Add(dynamicList[i])) into.Add(dynamicList[i]);
                }
            }
        }
    }

    /// <summary>
    /// Clears only the dynamic items from the grid. 
    /// Should be called every tick before re-populating if tracking dynamic entities.
    /// </summary>
    public void Clear()
    {
        _dynamicGrid.Clear();
    }

    /// <summary>
    /// Clears everything, including static geometry.
    /// </summary>
    public void ClearAll()
    {
        _staticGrid.Clear();
        _dynamicGrid.Clear();
        StaticVersion++;
    }
}
