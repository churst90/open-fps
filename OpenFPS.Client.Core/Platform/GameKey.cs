namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// Platform-neutral key identifiers. Replaces <c>System.Windows.Forms.Keys</c> leaking into game
/// logic — each head maps its native key codes (WinForms <c>Keys</c> / GTK keyvals) to/from these
/// at the OS boundary, so the input/command layer stays portable.
/// </summary>
public enum GameKey
{
    None = 0,

    // Letters
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    // Top-row digits
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,

    // Arrows
    Left, Right, Up, Down,

    // Whitespace / editing
    Space, Enter, Escape, Tab, Backspace, Delete,

    // Modifiers
    ShiftLeft, ShiftRight, ControlLeft, ControlRight, AltLeft, AltRight,

    // Function keys
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,

    // Punctuation the game binds
    Comma, Period, Slash, Semicolon,

    // Chat buffer navigation
    BracketLeft, BracketRight,

    /// <summary>The numeric keypad's divide key — a second binding for the command console, so the
    /// console is reachable without a modifier on layouts where slash needs one. With the scope up it
    /// is a trigger.</summary>
    NumpadDivide,

    // The rest of the numeric keypad, as it reports with Num Lock ON: the scope's keys. With Num Lock
    // off the keypad is NVDA's and Orca's review keys and reports as navigation keys instead, which
    // are left unmapped so the game never acts on a screen reader's command. The keypad's Enter is
    // reported as Enter by both heads.
    Numpad0, Numpad1, Numpad2, Numpad3, Numpad4, Numpad5, Numpad6, Numpad7, Numpad8, Numpad9,
    NumpadMultiply, NumpadAdd, NumpadSubtract, NumpadDecimal,
}
