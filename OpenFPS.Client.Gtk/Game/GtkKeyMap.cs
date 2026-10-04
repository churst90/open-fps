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
    // What comma and period report with Shift held on a US layout. Shift-comma and shift-period change
    // what comma and period step through (MapTracker), so like the braces they must arrive as the key.
    private const uint GDK_less = 0x03c;
    private const uint GDK_greater = 0x03e;
    private const uint GDK_slash = 0x02f;
    private const uint GDK_semicolon = 0x03b;
    private const uint GDK_bracketleft = 0x05b;
    private const uint GDK_bracketright = 0x05d;
    // What the bracket keys report with Shift held on a US layout. Shift-[ and shift-] switch chat
    // buffers, and with only the unshifted names mapped they arrived as nothing at all.
    private const uint GDK_braceleft = 0x07b;
    private const uint GDK_braceright = 0x07d;
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
    // The keypad with Num Lock on: the scope's keys.
    private const uint GDK_KP_Enter = 0xff8d;
    private const uint GDK_KP_Multiply = 0xffaa;
    private const uint GDK_KP_Add = 0xffab;
    private const uint GDK_KP_Subtract = 0xffad;
    private const uint GDK_KP_Decimal = 0xffae;
    private const uint GDK_KP_0 = 0xffb0; // KP_0..KP_9 are contiguous
    // The keypad with Num Lock off reports navigation keys, KP_Home (0xff95) to KP_Delete (0xff9f).
    // Those are Orca's review keys and are deliberately not mapped.
    private const uint GDK_KP_Home = 0xff95;
    private const uint GDK_KP_Delete = 0xff9f;

    /// <summary>What a keypad keyval says about Num Lock: 1 on (a keypad digit or point), 0 off (a
    /// keypad navigation key), -1 nothing (any other key).</summary>
    public static int NumLockFromKeyval(uint keyval)
        => keyval >= GDK_KP_0 && keyval <= GDK_KP_0 + 9 || keyval == GDK_KP_Decimal ? 1
         : keyval >= GDK_KP_Home && keyval <= GDK_KP_Delete ? 0
         : -1;

    /// <summary>
    /// The key, from what GDK reported pressed (<paramref name="keyval"/>) and what the same physical
    /// key gives with no modifiers held (<paramref name="unshifted"/>, 0 if not known).
    ///
    /// GDK reports a CHARACTER, and Shift changes it: shift-comma is "&lt;" on a US layout, ";" on a
    /// German one. A binding is on the key, so for punctuation the key's own unshifted character is
    /// asked first. Letters, digits and the keypad keep what was reported: the keypad's unshifted
    /// meaning depends on Num Lock, which is a modifier, and a digit row's unshifted character on a
    /// French layout is not a digit at all.
    /// </summary>
    public static GameKey Map(uint keyval, uint unshifted)
    {
        if (unshifted != 0 && IsPunctuation(keyval) && Map(unshifted) is var key && key != GameKey.None) return key;
        return Map(keyval);
    }

    /// <summary>Printable ASCII that is neither a letter nor a digit nor a space.</summary>
    private static bool IsPunctuation(uint keyval)
        => keyval > 0x020 && keyval < 0x07f
           && !(keyval >= 0x030 && keyval <= 0x039)
           && !(keyval >= 0x041 && keyval <= 0x05a)
           && !(keyval >= 0x061 && keyval <= 0x07a);

    public static GameKey Map(uint keyval)
    {
        if (keyval >= GDK_KP_0 && keyval <= GDK_KP_0 + 9) return GameKey.Numpad0 + (int)(keyval - GDK_KP_0);
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
            GDK_comma or GDK_less => GameKey.Comma,
            GDK_period or GDK_greater => GameKey.Period,
            GDK_slash => GameKey.Slash,
            GDK_semicolon => GameKey.Semicolon,
            GDK_bracketleft or GDK_braceleft => GameKey.BracketLeft,
            GDK_bracketright or GDK_braceright => GameKey.BracketRight,
            GDK_KP_Divide => GameKey.NumpadDivide,
            GDK_KP_Multiply => GameKey.NumpadMultiply,
            GDK_KP_Add => GameKey.NumpadAdd,
            GDK_KP_Subtract => GameKey.NumpadSubtract,
            GDK_KP_Decimal => GameKey.NumpadDecimal,
            // The keypad's Enter is Enter: it fires a gun in your hands and interacts otherwise.
            GDK_KP_Enter => GameKey.Enter,
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
