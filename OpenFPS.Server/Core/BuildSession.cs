using System.Numerics;

namespace OpenFPS.Server.Core;

/// <summary>
/// A player's build cursor: a review cursor for a building site, moved in metres from an origin at their
/// feet, for the places you cannot walk to (a roof) or cannot walk to straight (a run of wall). Its axes
/// are the builder's, fixed when the origin is set: axes that turned with you would move what you placed.
/// </summary>
public sealed class BuildSession
{
    /// <summary>World position of the origin — the player's feet when they set it.</summary>
    public Vector3 Origin;

    /// <summary>Which way "forward" points, radians. The player's heading when they set the origin.</summary>
    public float Yaw;

    /// <summary>The cursor, in origin-relative metres: right, up, forward.</summary>
    public Vector3 Cursor;

    /// <summary>Whether an origin has been set at all.</summary>
    public bool Placed;

    /// <summary>What this player has put down, most recent last, for /undo.</summary>
    public readonly List<int> Placed_Entities = new();

    /// <summary>The way the cursor last travelled, for a run of parts to follow.</summary>
    public Vector3 LastStep = Vector3.UnitZ;

    public void SetOrigin(Vector3 where, float yaw)
    {
        Origin = where;
        Yaw = yaw;
        Cursor = Vector3.Zero;
        LastStep = Vector3.UnitZ;
        Placed = true;
    }

    /// <summary>
    /// Forgets the origin and what was placed. For a change of map: the ids in
    /// <see cref="Placed_Entities"/> name entities on the map just left, and /undo on the new one
    /// would destroy whatever there happened to share an id.
    /// </summary>
    public void Reset()
    {
        Placed = false;
        Cursor = Vector3.Zero;
        LastStep = Vector3.UnitZ;
        Placed_Entities.Clear();
    }

    /// <summary>Turns a cursor position into a world one.</summary>
    public Vector3 ToWorld(Vector3 local)
        => Origin + Vector3.Transform(local, Quaternion.CreateFromYawPitchRoll(Yaw, 0f, 0f));

    /// <summary>Where the cursor is, in the world.</summary>
    public Vector3 WorldCursor => ToWorld(Cursor);

    /// <summary>The rotation a part placed now should have: the build heading, plus any turn.</summary>
    public Quaternion Facing(float turnDegrees)
        => Quaternion.CreateFromYawPitchRoll(Yaw + turnDegrees * (MathF.PI / 180f), 0f, 0f);

    /// <summary>How the cursor reads out: small numbers and named directions ("two right, three forward,
    /// one up"), never a world coordinate.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        Say(parts, Cursor.Z, "forward", "back");
        Say(parts, Cursor.X, "right", "left");
        Say(parts, Cursor.Y, "up", "down");
        return parts.Count == 0 ? "at the origin" : string.Join(", ", parts);
    }

    private static void Say(List<string> into, float value, string positive, string negative)
    {
        if (MathF.Abs(value) < 0.05f) return;
        into.Add($"{MathF.Abs(value):0.##} {(value > 0 ? positive : negative)}");
    }

    /// <summary>The named directions a cursor move or a run can take, in builder's axes.</summary>
    public static bool TryDirection(string word, out Vector3 step)
    {
        step = word.ToLowerInvariant() switch
        {
            "forward" or "forwards" or "ahead" or "f" => Vector3.UnitZ,
            "back" or "backward" or "backwards" or "b" => -Vector3.UnitZ,
            "right" or "r" => Vector3.UnitX,
            "left" or "l" => -Vector3.UnitX,
            "up" or "u" => Vector3.UnitY,
            "down" or "d" => -Vector3.UnitY,
            _ => Vector3.Zero,
        };
        return step != Vector3.Zero;
    }
}
