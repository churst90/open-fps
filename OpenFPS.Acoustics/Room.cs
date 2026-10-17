using System.Numerics;

namespace OpenFPS.Common;

/// <summary>A named box and what its six faces are made of; the host fills it (open-fps: from RegionComponent,
/// in EntityAcoustics). Material "None" is no face at all: see <see cref="RoomAcoustics"/>. A room flooded from
/// the walls carries what the flood measured (<see cref="RoomAcoustics.Measured(in Room, OpenFPS.Geometry.RoomFlood.Lining)"/>),
/// and its box is then only where it was seeded from.</summary>
public struct Room
{
    public string FriendlyName { get; set; } = "";
    public bool IsIndoor { get; set; }
    public Vector3 RoomSize { get; set; }
    public float ReverbTimeScale { get; set; } = 1.0f;
    /// <summary>Resonance indices of the six faces (AcousticRegistry), in <see cref="RoomAcoustics.FaceNames"/>' order.</summary>
    public int[] Materials { get; set; } = new int[6];
    /// <summary>Its air's volume as the flood measured it, m³; 0 where it did not flood (the box serves).</summary>
    public float Volume { get; set; }
    /// <summary>Each face's area as the flood measured it (what lines it, what is open, what is another room's air), m²; null where it did not flood.</summary>
    public float[]? FaceAreas { get; set; }
    /// <summary>Each face's Sabine absorption area, low, mid, high and broadband (W), m², from the materials lining it
    /// (an open face absorbs all of its area); null where it did not flood.</summary>
    public Vector4[]? FaceAbsorption { get; set; }
    /// <summary>The size of the bounds of its air in the world, m; zero where it did not flood.</summary>
    public Vector3 Extent { get; set; }
    public Room() { }
}

/// <summary>A room where it stands. The id is the host's (open-fps: the server's entity id, which logs, place
/// names and tests know), passed through unchanged.</summary>
public readonly record struct PlacedRoom(int Id, Vector3 Position, Quaternion Rotation, Room Room);
