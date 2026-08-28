using System.Collections.Generic;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Core.Input;

/// <summary>
/// Thread-safe keyboard state for the focused game window, keyed on the neutral
/// <see cref="GameKey"/>.
///
/// Each head's UI thread writes transitions through <see cref="SetKey"/> from its native key events
/// (WinForms <c>Keys</c> / GDK keyvals, both mapped at the boundary); the game-loop thread drains a
/// snapshot per fixed step through <see cref="GetSnapshot"/>. Held keys persist across the drain;
/// just-pressed keys are consumed by it, so a tap is acted on exactly once no matter how the two
/// thread rates line up.
///
/// This replaces the two near-identical buffers the heads used to keep — one typed on
/// <c>System.Windows.Forms.Keys</c>, one on <c>GameKey</c>.
/// </summary>
public sealed class InputStateBuffer
{
    private readonly HashSet<GameKey> _held = new();
    private readonly HashSet<GameKey> _justPressed = new();
    private readonly object _lock = new();

    public void SetKey(GameKey key, bool isDown)
    {
        if (key == GameKey.None) return;
        lock (_lock)
        {
            if (isDown)
            {
                if (_held.Add(key)) _justPressed.Add(key);
            }
            else
            {
                _held.Remove(key);
            }
        }
    }

    /// <summary>
    /// Drops all held state. Called on focus loss and on window re-activation: the Alt of an Alt+Tab
    /// registers its key-down while focused and its key-up while not, which would otherwise leave Alt
    /// stuck "held" — and a held modifier suppresses movement, so the player would come back to a
    /// character that only responds to tap keys.
    /// </summary>
    public void Clear()
    {
        lock (_lock) { _held.Clear(); _justPressed.Clear(); }
    }

    /// <summary>Held keys, plus the keys pressed since the last call. Consumes the just-pressed set.</summary>
    public (HashSet<GameKey> Held, HashSet<GameKey> JustPressed) GetSnapshot()
    {
        lock (_lock)
        {
            var held = new HashSet<GameKey>(_held);
            var pressed = new HashSet<GameKey>(_justPressed);
            _justPressed.Clear();
            return (held, pressed);
        }
    }

    /// <summary>Held keys only, without consuming the just-pressed set.</summary>
    public HashSet<GameKey> GetHeldSnapshot()
    {
        lock (_lock) return new HashSet<GameKey>(_held);
    }

    /// <summary>True when any modifier is currently held. Movement is suppressed while one is, so
    /// window-manager and screen-reader chords never walk the player across the map.</summary>
    public static bool HasModifier(HashSet<GameKey> held) =>
        held.Contains(GameKey.ShiftLeft) || held.Contains(GameKey.ShiftRight) ||
        held.Contains(GameKey.ControlLeft) || held.Contains(GameKey.ControlRight) ||
        held.Contains(GameKey.AltLeft) || held.Contains(GameKey.AltRight);

    /// <summary>True when either shift is held — the chat bindings' modifier.</summary>
    public static bool HasShift(HashSet<GameKey> held) =>
        held.Contains(GameKey.ShiftLeft) || held.Contains(GameKey.ShiftRight);
}
