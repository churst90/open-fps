using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Server.Core;

/// <summary>
/// Where a player is currently building, and what they have put there.
///
/// The problem this exists to solve is the one the whole command set keeps running into: a player
/// here cannot point at anything, so "put it there" has to mean something without a there. Walking
/// to every spot works and is what `/group` and `/addseat` do, but you cannot walk to a roof, and
/// walking to twenty wall positions in a row does not reliably produce a straight wall.
///
/// So: a CURSOR, moved in metres from an origin the player chose, that announces itself every time
/// it moves and says what is already at it. That is a review cursor, and a screen-reader user has
/// been navigating documents with one for years — the same idea applied to a building site. The
/// coordinates stay small and memorable because they are relative to an origin at your own feet
/// ("three forward, two up") rather than absolute world positions nobody can hold in their head.
///
/// Axes are the BUILDER'S, fixed at the moment the origin is set: right, up and forward from where
/// they were standing and the way they were facing. Fixed rather than live, because a coordinate
/// system that rotates when you turn round is one where the wall you placed a moment ago has moved.
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

    /// <summary>
    /// What this player has put down, most recent last.
    ///
    /// There is no undo without it, and there has to be an undo: a wall in the wrong place is
    /// invisible to somebody who cannot see it, so the mistake is not merely unfixed, it is
    /// undetectable until they walk into it.
    /// </summary>
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

    /// <summary>Turns a cursor position into a world one.</summary>
    public Vector3 ToWorld(Vector3 local)
        => Origin + Vector3.Transform(local, Quaternion.CreateFromYawPitchRoll(Yaw, 0f, 0f));

    /// <summary>Where the cursor is, in the world.</summary>
    public Vector3 WorldCursor => ToWorld(Cursor);

    /// <summary>The rotation a part placed now should have: the build heading, plus any turn.</summary>
    public Quaternion Facing(float turnDegrees)
        => Quaternion.CreateFromYawPitchRoll(Yaw + turnDegrees * (MathF.PI / 180f), 0f, 0f);

    /// <summary>
    /// How the cursor reads out: small numbers, named directions, never a world coordinate.
    ///
    /// "Two right, three forward, one up" is a place a person can hold in their head and walk back
    /// to. "204.7, 12.3, -88.1" is not, and it is what every one of these commands would be reduced
    /// to if the origin were the map's rather than the builder's.
    /// </summary>
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
