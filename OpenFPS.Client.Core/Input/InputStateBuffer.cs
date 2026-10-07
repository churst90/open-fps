using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Core.Input;

/// <summary>
/// Keyboard state for the game window, shared between threads. The head's UI thread writes through
/// <see cref="SetKey"/>; the game loop takes a snapshot per fixed step through <see cref="GetSnapshot"/>,
/// which consumes the just-pressed keys, so a tap is acted on exactly once whatever the two rates.
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
    /// Drops all held state, on focus loss and re-activation: Alt+Tab's Alt goes down while focused and
    /// up while not, and a stuck modifier suppresses movement.
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

    /// <summary>Any modifier held. Movement is suppressed while one is, so window-manager and
    /// screen-reader chords never walk the player.</summary>
    public static bool HasModifier(HashSet<GameKey> held) =>
        held.Contains(GameKey.ShiftLeft) || held.Contains(GameKey.ShiftRight) ||
        held.Contains(GameKey.ControlLeft) || held.Contains(GameKey.ControlRight) ||
        held.Contains(GameKey.AltLeft) || held.Contains(GameKey.AltRight);

    public static bool HasShift(HashSet<GameKey> held) =>
        held.Contains(GameKey.ShiftLeft) || held.Contains(GameKey.ShiftRight);

    public static bool HasControl(HashSet<GameKey> held) =>
        held.Contains(GameKey.ControlLeft) || held.Contains(GameKey.ControlRight);

    public static bool HasAlt(HashSet<GameKey> held) =>
        held.Contains(GameKey.AltLeft) || held.Contains(GameKey.AltRight);

    /// <summary>Which modifiers are down, as one value a binding can be keyed on.</summary>
    public static KeyModifiers ModifiersIn(HashSet<GameKey> held)
    {
        var mods = KeyModifiers.None;
        if (HasShift(held)) mods |= KeyModifiers.Shift;
        if (HasControl(held)) mods |= KeyModifiers.Control;
        if (HasAlt(held)) mods |= KeyModifiers.Alt;
        return mods;
    }
}
