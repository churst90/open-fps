using System.Numerics;

namespace OpenFPS.Server.Editor;

/// <summary>Where a thing is, which way it is turned, and how big: everything a move changes.</summary>
public readonly record struct Pose(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public bool Near(Pose o)
        => Vector3.Distance(Position, o.Position) < 1e-3f
           && MathF.Abs(Quaternion.Dot(Rotation, o.Rotation)) > 1f - 1e-5f
           && Vector3.Distance(Scale, o.Scale) < 1e-4f;
}
