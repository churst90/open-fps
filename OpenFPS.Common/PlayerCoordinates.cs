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
        $"{Tenths(world.X)}, {Tenths(world.Z)}, {Tenths(world.Y)}";

    /// <summary>To the tenth, with no minus sign on zero. Walking due south moves x by sin(180 degrees),
    /// which in floats is -8.7e-8 of a step, so an x of 0 drifts a hair below it and "F1" says "-0.0".</summary>
    private static string Tenths(float v)
    {
        string s = v.ToString("F1", CultureInfo.InvariantCulture);
        return s == "-0.0" ? "0.0" : s;
    }

    /// <summary>A position a player typed, in their order, as an engine position.</summary>
    public static Vector3 ToWorld(float x, float y, float z) => new(x, z, y);
}
