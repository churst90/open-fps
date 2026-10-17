using System.Numerics;

namespace OpenFPS.Acoustics;

/// <summary>A way through between two rooms (equal ids: not linked yet); the host fills it (open-fps: from
/// PortalComponent, in EntityAcoustics).</summary>
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

/// <summary>An opening where it stands. Its frame's local Z is where the rooms an unlinked opening joins are
/// looked for.</summary>
public readonly record struct PlacedOpening(int Id, Vector3 Position, Quaternion Rotation, Opening Opening);
