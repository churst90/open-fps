using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>A map's acoustic regions, portals and voxel grid, built on load by each side; never sent.
/// Region and portal ids are the host's (open-fps: the server's entity ids; generated openings are negative).</summary>
public partial class AcousticMap
{
    public float VoxelResolution { get; set; } = 0.5f;
    public Vector3 MinBound { get; set; }
    /// <summary>Carried from the map file and not read: what gets through a wall is decided by the
    /// wall (WallTransmission), not by a per-map floor under it.</summary>
    public float OcclusionFloor { get; set; } = AcousticConstants.DefaultOcclusionFloor;
    public int GlobalEnvironmentId { get; set; } = -1;
    public SparseAcousticOctree VoxelGrid { get; set; }
    public Dictionary<int, Room> Regions { get; set; } = new();
    public Dictionary<int, Vector3> RegionPositions { get; set; } = new(); 
    public Dictionary<int, Quaternion> RegionRotations { get; set; } = new(); // Capture entity rotation
    public Dictionary<int, (Opening Portal, Vector3 Position)> Portals { get; set; } = new();

    /// <summary>
    /// The frame of each opening found in a room's faces (AcousticVolumeGenerator: the gaps in its walls
    /// and its open sides), by portal id: where the gap is, which way it faces (local X across, Y up, Z
    /// through) and its width, height and the thickness of the wall it is cut through. Derived on load,
    /// never sent.
    /// </summary>
    public Dictionary<int, OpeningFrame> OpeningFrames { get; set; } = new();

    /// <summary>
    /// Where each room is, from the walls (docs/GEOMETRY.md 12.6): the cells of every room's flood. A point a flood
    /// holds is in that room; a flooded room's box says nothing. Null until the rooms have been flooded (the boxes
    /// serve meanwhile).
    /// </summary>
    public OpenFPS.Common.Geometry.RoomCells? RoomCells { get; set; }

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
