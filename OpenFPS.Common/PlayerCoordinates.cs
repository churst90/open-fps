using System.Globalization;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// Coordinates as a PLAYER reads and types them: x east-west, y north-south, z height.
///
/// The engine is Y-up — +X east, +Y up, +Z north — which is FMOD's native layout and the one every
/// map, prefab and system is written in, so it stays. But players think of the ground as x and y and
/// of z as height, the way a map, a surveyor or Blender does, and hearing "150, 0.1, 27" they took
/// the middle number for north. So the swap happens here, at the edge, in one place: everything that
/// SPEAKS a position formats it through this, and everything that READS one from a player parses it
/// through this, so what C says can be typed straight back into /tp.
/// </summary>
public static class PlayerCoordinates
{
    /// <summary>"150.0, 27.0, 0.1" — east, north, height.</summary>
    public static string Format(Vector3 world) =>
        string.Format(CultureInfo.InvariantCulture, "{0:F1}, {1:F1}, {2:F1}", world.X, world.Z, world.Y);

    /// <summary>A position a player typed, in their order, as an engine position.</summary>
    public static Vector3 ToWorld(float x, float y, float z) => new(x, z, y);
}
