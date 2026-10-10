using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// A way through between two rooms: the rooms on each side (equal ids: not linked yet), how wide it is,
/// and where the hole is and which way it faces when it has a leaf. The host fills it from whatever it
/// keeps its doorways in (open-fps: PortalComponent, through EntityAcoustics).
/// </summary>
public struct Opening
{
    public int RegionAId { get; set; }
    public int RegionBId { get; set; }
    /// <summary>The side of a square of the opening's area, metres; zero or less is no opening.</summary>
    public float ApertureSize { get; set; }
    /// <summary>
    /// Where the doorway is in the world and which way it faces: the leaf's pose when shut (X across the
    /// opening, Y up). An all-zero rotation means not known: an opening with no leaf, placed where it stands.
    /// </summary>
    public Vector3 OpeningCentre { get; set; }
    /// <inheritdoc cref="OpeningCentre"/>
    public Quaternion OpeningRotation { get; set; }
}

/// <summary>An opening where it stands: its id (open-fps: the server's entity id), its position and its frame
/// (forward is local Z, the way probed for the rooms an unlinked opening joins).</summary>
public readonly record struct PlacedOpening(int Id, Vector3 Position, Quaternion Rotation, Opening Opening);
