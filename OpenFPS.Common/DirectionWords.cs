using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// Where something is from the way you face, in words rather than a clock face (Cody, 2026-10-04: he
/// had to work out that 5 o'clock was behind him). Shared by the server's /scan and /where and the
/// client's comma and period, so the two never word one place differently.
/// </summary>
public static class DirectionWords
{
    /// <summary>The eight, clockwise from straight ahead, each 45 degrees wide.</summary>
    public static readonly string[] Words =
        { "in front", "right in front", "right", "right behind", "behind", "left behind", "left", "left in front" };

    /// <summary>
    /// The word for <paramref name="targetDir"/> (world space, any length) seen by a body turned by
    /// <paramref name="rotation"/>. Only the heading counts: up and down are not a direction here.
    /// </summary>
    public static string Relative(Quaternion rotation, Vector3 targetDir)
    {
        Matrix4x4 rotMat = Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(rotation));
        Vector3 localDir = Vector3.Transform(targetDir, rotMat);
        float angle = MathF.Atan2(localDir.X, localDir.Z) * (180.0f / MathF.PI);
        if (angle < 0) angle += 360.0f;
        return angle switch
        {
            < 22.5f or >= 337.5f => Words[0], < 67.5f => Words[1], < 112.5f => Words[2],
            < 157.5f => Words[3], < 202.5f => Words[4], < 247.5f => Words[5],
            < 292.5f => Words[6], _ => Words[7]
        };
    }

    /// <summary>The same, for a body facing <paramref name="yaw"/> radians (the client's heading).</summary>
    public static string Relative(float yaw, Vector3 targetDir)
        => Relative(Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f), new Vector3(targetDir.X, 0f, targetDir.Z));
}
