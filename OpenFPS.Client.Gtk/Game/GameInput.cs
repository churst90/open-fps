using System.Collections.Generic;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Gtk.Game;

/// <summary>
/// Thread-safe keyboard state for the in-game window, keyed on the neutral <see cref="GameKey"/>.
/// The GTK main thread writes transitions via <see cref="SetKey"/> (from the window's key
/// controller); the game-loop thread drains a snapshot via <see cref="GetSnapshot"/> each step.
/// Mirrors the Windows head's <c>InputStateBuffer</c> but free of <c>System.Windows.Forms.Keys</c>.
/// </summary>
public sealed class GameInput
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

    /// <summary>Clears all held state (e.g. on focus loss) so keys don't "stick" down.</summary>
    public void Clear()
    {
        lock (_lock) { _held.Clear(); _justPressed.Clear(); }
    }

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
}
