using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common.Components;

namespace OpenFPS.Common;

/// <summary>A map's acoustic regions, portals and voxel grid, built on load by each side; never sent.</summary>
public partial class AcousticMap
{
    public float VoxelResolution { get; set; } = 0.5f;
    public Vector3 MinBound { get; set; }
    /// <summary>Carried from the map file and not read: what gets through a wall is decided by the
    /// wall (WallTransmission), not by a per-map floor under it.</summary>
    public float OcclusionFloor { get; set; } = 0.05f;
    public int GlobalEnvironmentId { get; set; } = -1;
    public SparseAcousticOctree VoxelGrid { get; set; }
    public Dictionary<int, RegionComponent> Regions { get; set; } = new();
    public Dictionary<int, Vector3> RegionPositions { get; set; } = new(); 
    public Dictionary<int, Quaternion> RegionRotations { get; set; } = new(); // Capture entity rotation
    public Dictionary<int, (PortalComponent Portal, Vector3 Position)> Portals { get; set; } = new();

    /// <summary>
    /// The frame of each opening found in a room's faces (AcousticVolumeGenerator: the gaps in its walls
    /// and its open sides), by portal id: where the gap is, which way it faces (local X across, Y up, Z
    /// through) and its width, height and the thickness of the wall it is cut through. Derived on load,
    /// never sent.
    /// </summary>
    public Dictionary<int, OpeningFrame> OpeningFrames { get; set; } = new();

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

/// <summary>An opening's rectangle: the room whose face it is in, its centre, its frame (local X across,
/// Y up, Z through) and its width, height and depth in that frame.</summary>
public readonly record struct OpeningFrame(int Room, Vector3 Centre, Quaternion Rotation, Vector3 Size);
