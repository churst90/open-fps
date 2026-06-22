using System.Collections.Generic;
using System.Windows.Forms;
using System.Linq;

namespace OpenFPS.Client.Core;

public class InputStateBuffer
{
    private readonly HashSet<Keys> _heldKeys = new();
    private readonly HashSet<Keys> _justPressed = new();
    private readonly object _lock = new();

    public void SetKeyState(Keys key, bool isDown)
    {
        lock (_lock)
        {
            if (isDown)
            {
                if (!_heldKeys.Contains(key))
                {
                    _heldKeys.Add(key);
                    _justPressed.Add(key);
                }
            }
            else
            {
                _heldKeys.Remove(key);
            }
        }
    }

    public (HashSet<Keys> held, HashSet<Keys> justPressed) GetSnapshot()
    {
        lock (_lock)
        {
            var held = new HashSet<Keys>(_heldKeys);
            var pressed = new HashSet<Keys>(_justPressed);
            _justPressed.Clear();
            return (held, pressed);
        }
    }

    public HashSet<Keys> GetHeldKeysSnapshot()
    {
        lock (_lock)
        {
            return new HashSet<Keys>(_heldKeys);
        }
    }
}
