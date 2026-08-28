using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Gtk.Game;

/// <summary>
/// Maps GDK key values (as delivered by GTK's <c>EventControllerKey</c>) to the neutral
/// <see cref="GameKey"/>. This is the Linux side of the input boundary; the Windows head maps
/// WinForms <c>Keys</c> to the same enum. Only the keys the game actually binds are mapped.
/// </summary>
internal static class GtkKeyMap
{
    // GDK keyval constants (see gdk/gdkkeysyms.h). Listed rather than pulled from Gdk.Constants
    // so the mapping is explicit and self-documenting.
    private const uint GDK_space = 0x020;
    private const uint GDK_comma = 0x02c;
    private const uint GDK_period = 0x02e;
    private const uint GDK_slash = 0x02f;
    private const uint GDK_semicolon = 0x03b;
    private const uint GDK_bracketleft = 0x05b;
    private const uint GDK_bracketright = 0x05d;
    private const uint GDK_KP_Divide = 0xffaf;
    private const uint GDK_Return = 0xff0d;
    private const uint GDK_Escape = 0xff1b;
    private const uint GDK_Tab = 0xff09;
    private const uint GDK_BackSpace = 0xff08;
    private const uint GDK_Delete = 0xffff;
    private const uint GDK_Left = 0xff51;
    private const uint GDK_Up = 0xff52;
    private const uint GDK_Right = 0xff53;
    private const uint GDK_Down = 0xff54;
    private const uint GDK_Shift_L = 0xffe1;
    private const uint GDK_Shift_R = 0xffe2;
    private const uint GDK_Control_L = 0xffe3;
    private const uint GDK_Control_R = 0xffe4;
    private const uint GDK_Alt_L = 0xffe9;
    private const uint GDK_Alt_R = 0xffea;
    private const uint GDK_F1 = 0xffbe; // F1..F12 are contiguous

    public static GameKey Map(uint keyval)
    {
        // Letters: normalize upper-case (Shift) to lower, then offset from GameKey.A.
        if (keyval >= 0x041 && keyval <= 0x05a) keyval += 0x20; // A-Z -> a-z
        if (keyval >= 0x061 && keyval <= 0x07a) return GameKey.A + (int)(keyval - 0x061);

        // Top-row digits 0-9.
        if (keyval >= 0x030 && keyval <= 0x039) return GameKey.D0 + (int)(keyval - 0x030);

        // Function keys F1-F12.
        if (keyval >= GDK_F1 && keyval <= GDK_F1 + 11) return GameKey.F1 + (int)(keyval - GDK_F1);

        return keyval switch
        {
            GDK_space => GameKey.Space,
            GDK_Return => GameKey.Enter,
            GDK_Escape => GameKey.Escape,
            GDK_Tab => GameKey.Tab,
            GDK_BackSpace => GameKey.Backspace,
            GDK_Delete => GameKey.Delete,
            GDK_comma => GameKey.Comma,
            GDK_period => GameKey.Period,
            GDK_slash => GameKey.Slash,
            GDK_semicolon => GameKey.Semicolon,
            GDK_bracketleft => GameKey.BracketLeft,
            GDK_bracketright => GameKey.BracketRight,
            GDK_KP_Divide => GameKey.NumpadDivide,
            GDK_Left => GameKey.Left,
            GDK_Right => GameKey.Right,
            GDK_Up => GameKey.Up,
            GDK_Down => GameKey.Down,
            GDK_Shift_L => GameKey.ShiftLeft,
            GDK_Shift_R => GameKey.ShiftRight,
            GDK_Control_L => GameKey.ControlLeft,
            GDK_Control_R => GameKey.ControlRight,
            GDK_Alt_L => GameKey.AltLeft,
            GDK_Alt_R => GameKey.AltRight,
            _ => GameKey.None
        };
    }
}
