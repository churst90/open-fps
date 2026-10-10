using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// A named volume as the acoustics hear it: a box, what each of its six faces is made of (floor, ceiling,
/// north, south, east, west; material "None" is no face at all), how long it rings and what plays in it.
/// The host fills it from whatever it keeps its places in (open-fps: RegionComponent, through
/// EntityAcoustics). A region is not a room: see <see cref="RoomAcoustics"/>.
/// </summary>
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

/// <summary>A room where it stands: its id (open-fps: the server's entity id, passed through unchanged, as
/// logs, place names and tests know it), its centre and its frame.</summary>
public readonly record struct PlacedRoom(int Id, Vector3 Position, Quaternion Rotation, Room Room);
