namespace OpenFPS.Client.Core.Platform;

/// <summary>Keys as the game knows them; each head maps its native key codes to these.</summary>
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

    /// <summary>The keypad's divide: the command console without a modifier on layouts where slash
    /// needs one. With the scope up it is a trigger.</summary>
    NumpadDivide,

    // The keypad with Num Lock on: the scope's keys. With Num Lock off it is NVDA's and Orca's review
    // keys, reported as navigation keys, and those stay unmapped so the game never acts on a screen
    // reader's command. The keypad's Enter is Enter in both heads.
    Numpad0, Numpad1, Numpad2, Numpad3, Numpad4, Numpad5, Numpad6, Numpad7, Numpad8, Numpad9,
    NumpadMultiply, NumpadAdd, NumpadSubtract, NumpadDecimal,
}
