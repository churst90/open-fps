using System;
using System.Numerics;
using MemoryPack;

namespace OpenFPS.Common;

/// <summary>
/// A high-performance Sparse Voxel Octree (SVO) for acoustic regions.
/// Compresses large empty or solid volumes to save RAM and speed up lookups.
/// </summary>
[MemoryPackable]
public partial class SparseAcousticOctree
{
    [MemoryPackable]
    public partial class OctreeNode
    {
        public int RegionId = -1; // -1 = Mixed or None
        public OctreeNode[]? Children;

        [MemoryPackIgnore]
        public bool IsLeaf => Children == null;
    }

    private OctreeNode _root;
    private Vector3 _min;
    private float _size;
    private float _minVoxel;

    [MemoryPackConstructor]
    public SparseAcousticOctree()
    {
        _root = new OctreeNode();
    }

    public SparseAcousticOctree(Vector3 min, Vector3 size, float minVoxelSize = 0.5f)
    {
        _min = min;
        // The Octree must be a cube for simplicity, so find the largest dimension
        _size = Math.Max(size.X, Math.Max(size.Y, size.Z));
        // Round up to power of two to avoid precision issues
        _size = MathF.Pow(2, MathF.Ceiling(MathF.Log2(_size)));
        _minVoxel = minVoxelSize;
        _root = new OctreeNode();
    }

    // Properties for serialization
    public OctreeNode Root { get => _root; set => _root = value; }
    public Vector3 Min { get => _min; set => _min = value; }
    public float Size { get => _size; set => _size = value; }
    public float MinVoxel { get => _minVoxel; set => _minVoxel = value; }

    public void SetRegion(Vector3 pos, int regionId)
    {
        SetRegionRecursive(_root, _min, _size, pos, regionId);
    }

    private void SetRegionRecursive(OctreeNode node, Vector3 nodeMin, float nodeSize, Vector3 targetPos, int regionId)
    {
        if (nodeSize <= _minVoxel)
        {
            node.RegionId = regionId;
            node.Children = null;
            return;
        }

        if (node.IsLeaf)
        {
            if (node.RegionId == regionId) return; // Already set
            node.Children = new OctreeNode[8];
            for (int i = 0; i < 8; i++) node.Children[i] = new OctreeNode { RegionId = node.RegionId };
        }

        float halfSize = nodeSize / 2f;
        int index = 0;
        Vector3 childMin = nodeMin;

        if (targetPos.X >= nodeMin.X + halfSize) { index |= 1; childMin.X += halfSize; }
        if (targetPos.Y >= nodeMin.Y + halfSize) { index |= 2; childMin.Y += halfSize; }
        if (targetPos.Z >= nodeMin.Z + halfSize) { index |= 4; childMin.Z += halfSize; }

        SetRegionRecursive(node.Children![index], childMin, halfSize, targetPos, regionId);
        
        // Post-process: If all 8 children are the same, collapse them
        TryCollapse(node);
    }

    public void SetRegionOBB(Vector3 center, Vector3 size, Quaternion rotation, int regionId)
    {
        SetRegionOBBRecursive(_root, _min, _size, center, size, rotation, regionId);
    }

    private void SetRegionOBBRecursive(OctreeNode node, Vector3 nodeMin, float nodeSize, Vector3 obbCenter, Vector3 obbSize, Quaternion obbRot, int regionId)
    {
        Vector3 nodeMax = nodeMin + new Vector3(nodeSize);
        Vector3 nodeCenter = nodeMin + new Vector3(nodeSize / 2f);

        // Check node AABB vs OBB intersection
        // Approximation: if node is fully inside, set all. If outside, return. If partial, split.
        var containment = GeometryUtils.GetBoxContainmentInOBB(nodeCenter, new Vector3(nodeSize), obbCenter, obbSize, obbRot);

        if (containment == BoxContainment.Outside) return;

        if (containment == BoxContainment.FullyInside || nodeSize <= _minVoxel)
        {
            node.RegionId = regionId;
            node.Children = null;
            return;
        }

        if (node.IsLeaf)
        {
            node.Children = new OctreeNode[8];
            for (int i = 0; i < 8; i++) node.Children[i] = new OctreeNode { RegionId = node.RegionId };
        }

        float halfSize = nodeSize / 2f;
        for (int i = 0; i < 8; i++)
        {
            Vector3 childMin = nodeMin;
            if ((i & 1) != 0) childMin.X += halfSize;
            if ((i & 2) != 0) childMin.Y += halfSize;
            if ((i & 4) != 0) childMin.Z += halfSize;
            SetRegionOBBRecursive(node.Children![i], childMin, halfSize, obbCenter, obbSize, obbRot, regionId);
        }
        TryCollapse(node);
    }

    private void TryCollapse(OctreeNode node)
    {
        if (node.IsLeaf) return;
        int firstId = node.Children![0].RegionId;
        if (firstId == -1) return;
        for (int i = 1; i < 8; i++)
        {
            if (!node.Children[i].IsLeaf || node.Children[i].RegionId != firstId) return;
        }
        node.RegionId = firstId;
        node.Children = null;
    }

    public int GetRegionAt(Vector3 pos)
    {
        if (!IsInBounds(pos)) return _root.RegionId; // Return root RegionId (usually -1) if outside
        return GetRegionRecursive(_root, _min, _size, pos);
    }

    private int GetRegionRecursive(OctreeNode node, Vector3 nodeMin, float nodeSize, Vector3 targetPos)
    {
        if (node.IsLeaf) return node.RegionId;

        float halfSize = nodeSize / 2f;
        int index = 0;
        Vector3 childMin = nodeMin;

        if (targetPos.X >= nodeMin.X + halfSize) { index |= 1; childMin.X += halfSize; }
        if (targetPos.Y >= nodeMin.Y + halfSize) { index |= 2; childMin.Y += halfSize; }
        if (targetPos.Z >= nodeMin.Z + halfSize) { index |= 4; childMin.Z += halfSize; }

        return GetRegionRecursive(node.Children![index], childMin, halfSize, targetPos);
    }

    public bool IsInBounds(Vector3 pos)
    {
        return pos.X >= _min.X && pos.X < _min.X + _size &&
               pos.Y >= _min.Y && pos.Y < _min.Y + _size &&
               pos.Z >= _min.Z && pos.Z < _min.Z + _size;
    }
}

public enum BoxContainment { Outside, Partial, FullyInside }
