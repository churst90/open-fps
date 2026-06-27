using System;

namespace OpenFPS.Client.Core.Platform;

/// <summary>A key transition: a <see cref="GameKey"/> going down (Pressed) or up.</summary>
public readonly record struct KeyEvent(GameKey Key, bool Pressed);

/// <summary>
/// Platform-agnostic source of keyboard input for the focused game window. Each head raises
/// <see cref="KeyChanged"/> from its native focused-window key events:
///   Windows -> WinForms KeyDown/KeyUp
///   Linux   -> GTK key-press-event / key-release-event
/// This deliberately replaces the Win32 global keyboard hook (intrusive, unportable, fights the
/// screen reader, forbidden on Wayland) with focused-window input.
/// </summary>
public interface IInputSource
{
    /// <summary>Raised when a mapped key changes state.</summary>
    event Action<KeyEvent>? KeyChanged;

    /// <summary>True when the game window currently has focus.</summary>
    bool IsWindowFocused { get; }
}
