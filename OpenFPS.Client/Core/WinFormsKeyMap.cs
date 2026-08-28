using System.Windows.Forms;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Core;

/// <summary>
/// Maps WinForms virtual key codes to the neutral <see cref="GameKey"/>. This is the Windows side of
/// the input boundary; the Linux head maps GDK keyvals to the same enum. Only the keys the game
/// actually binds are mapped — everything else becomes <see cref="GameKey.None"/> and is dropped by
/// the input buffer.
///
/// The modifier bits are stripped first: a hook reports <c>Keys.W | Keys.Shift</c> for shift-W, and
/// the game binds the physical key, not the chord. Left/right modifiers are reported by their own
/// virtual key codes (LShiftKey / RShiftKey / ...), so they survive the strip and reach the enum.
/// </summary>
public static class WinFormsKeyMap
{
    public static GameKey Map(Keys key)
    {
        Keys code = key & Keys.KeyCode;

        // Letters A-Z are contiguous in both enums.
        if (code >= Keys.A && code <= Keys.Z) return GameKey.A + (int)(code - Keys.A);

        // Top-row digits 0-9 (Keys.D0..Keys.D9) and the numeric keypad's digits map to the same slots.
        if (code >= Keys.D0 && code <= Keys.D9) return GameKey.D0 + (int)(code - Keys.D0);
        if (code >= Keys.NumPad0 && code <= Keys.NumPad9) return GameKey.D0 + (int)(code - Keys.NumPad0);

        // Function keys F1-F12.
        if (code >= Keys.F1 && code <= Keys.F12) return GameKey.F1 + (int)(code - Keys.F1);

        return code switch
        {
            Keys.Space => GameKey.Space,
            Keys.Enter => GameKey.Enter,     // == Keys.Return
            Keys.Escape => GameKey.Escape,
            Keys.Tab => GameKey.Tab,
            Keys.Back => GameKey.Backspace,
            Keys.Delete => GameKey.Delete,

            Keys.Left => GameKey.Left,
            Keys.Right => GameKey.Right,
            Keys.Up => GameKey.Up,
            Keys.Down => GameKey.Down,

            Keys.LShiftKey => GameKey.ShiftLeft,
            Keys.RShiftKey => GameKey.ShiftRight,
            Keys.ShiftKey => GameKey.ShiftLeft,       // generic report: treat as left
            Keys.LControlKey => GameKey.ControlLeft,
            Keys.RControlKey => GameKey.ControlRight,
            Keys.ControlKey => GameKey.ControlLeft,
            Keys.LMenu => GameKey.AltLeft,
            Keys.RMenu => GameKey.AltRight,
            Keys.Menu => GameKey.AltLeft,

            Keys.Oemcomma => GameKey.Comma,
            Keys.OemPeriod => GameKey.Period,
            Keys.OemQuestion => GameKey.Slash,        // the '/?' key
            Keys.Divide => GameKey.NumpadDivide,      // the keypad's '/'
            Keys.OemSemicolon => GameKey.Semicolon,
            Keys.OemOpenBrackets => GameKey.BracketLeft,
            Keys.OemCloseBrackets => GameKey.BracketRight,

            _ => GameKey.None
        };
    }
}
