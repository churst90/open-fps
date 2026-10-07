using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Core;

/// <summary>
/// The world editor's direct keys (docs/WORLD_EDITOR.md section 11.8): while an editor list is open, a
/// few Shift keys act on the selected thing without going through the list. OFF unless a player turns
/// them on with /editorkeys on, and to stay off for everybody until they have been tried with Orca and
/// NVDA. Shift only: never Control, Alt, Insert or the numeric keypad, which screen readers own.
/// </summary>
public static class EditorKeys
{
    /// <summary>Whether the direct keys act. Off by default; saved (ClientSettings.EditorDirectKeys).</summary>
    public static bool Enabled { get; set; }

    /// <summary>The /edit command a Shift key sends while an editor list is open, or null for a key that
    /// is the list's own.</summary>
    public static string? CommandFor(GameKey key) => key switch
    {
        GameKey.Up => "edit nudge forward",
        GameKey.Down => "edit nudge back",
        GameKey.Left => "edit nudge left",
        GameKey.Right => "edit nudge right",
        GameKey.BracketRight => "edit nudge up",
        GameKey.BracketLeft => "edit nudge down",
        GameKey.Period => "edit turn 15",
        GameKey.Comma => "edit turn -15",
        GameKey.Slash => "edit selected",
        GameKey.D => "edit duplicate",
        GameKey.Delete => "edit menu delete",
        GameKey.Z => "edit undo",
        GameKey.Y => "edit redo",
        _ => null,
    };

    /// <summary>The keys, as said by /editorkeys.</summary>
    public const string Said =
        "Shift with an arrow nudges forward, back, left or right; Shift with a bracket nudges up or down; "
        + "Shift with comma or period turns 15 degrees; Shift slash says where it is; Shift D duplicates; "
        + "Shift Delete asks to delete; Shift Z undoes and Shift Y redoes.";
}
