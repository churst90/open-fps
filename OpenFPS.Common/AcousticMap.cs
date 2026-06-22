using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common.Components;
using MemoryPack;

namespace OpenFPS.Common;

[MemoryPackable]
public partial class AcousticMap
{
    public float VoxelResolution { get; set; } = 0.5f;
    public Vector3 MinBound { get; set; }
    public float OcclusionFloor { get; set; } = 0.05f; // Occlusion Floor: sounds never drop below 5% volume through walls
    public int GlobalEnvironmentId { get; set; } = -1;
    public SparseAcousticOctree VoxelGrid { get; set; }
    public Dictionary<int, RegionComponent> Regions { get; set; } = new();
    public Dictionary<int, Vector3> RegionPositions { get; set; } = new(); 
    public Dictionary<int, Quaternion> RegionRotations { get; set; } = new(); // Capture entity rotation
    public Dictionary<int, (PortalComponent Portal, Vector3 Position)> Portals { get; set; } = new();

    [MemoryPackConstructor]
    public AcousticMap()
    {
        VoxelGrid = new SparseAcousticOctree();
    }

    public AcousticMap(Vector3 mapSize, Vector3 offset, float voxelSize = 0.5f)
    {
        MinBound = offset;
        VoxelResolution = voxelSize;
        VoxelGrid = new SparseAcousticOctree(offset, mapSize, voxelSize);
    }
}
