using System.Numerics;

namespace OpenFPS.Common;

/// <summary>A named box and what its six faces are made of; the host fills it (open-fps: from RegionComponent,
/// in EntityAcoustics). Material "None" is no face at all: see <see cref="RoomAcoustics"/>.</summary>
public struct Room
{
    public string FriendlyName { get; set; } = "";
    public bool IsIndoor { get; set; }
    public Vector3 RoomSize { get; set; }
    public float ReverbTimeScale { get; set; } = 1.0f;
    /// <summary>Resonance indices of the six faces (AcousticRegistry), in <see cref="RoomAcoustics.FaceNames"/>' order.</summary>
    public int[] Materials { get; set; } = new int[6];
    public string AmbienceId { get; set; } = "";
    public Room() { }
}

/// <summary>A room where it stands. The id is the host's (open-fps: the server's entity id, which logs, place
/// names and tests know), passed through unchanged.</summary>
public readonly record struct PlacedRoom(int Id, Vector3 Position, Quaternion Rotation, Room Room);
