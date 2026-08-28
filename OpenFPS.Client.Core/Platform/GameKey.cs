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
    /// console is reachable without a modifier on layouts where slash needs one.</summary>
    NumpadDivide
}
