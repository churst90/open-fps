using System;
using System.Collections.Generic;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Core.Input;

/// <summary>Which set of bindings is live. A binding in the active context wins; otherwise Global applies.</summary>
public enum InputContext
{
    /// <summary>Bindings that apply everywhere (chat navigation, the console key, quit).</summary>
    Global,
    /// <summary>Bindings that only make sense with a body in the world (look ahead, interact, scan).</summary>
    Gameplay,
    /// <summary>A modal text entry has focus; gameplay bindings must not fire.</summary>
    UI,
    /// <summary>Reserved for a future in-game chat mode with its own bindings.</summary>
    Chat
}

/// <summary>
/// Maps a key press to an action, per context, with rebinding as a first-class operation.
/// Keyed on the neutral <see cref="GameKey"/>, so one binding table serves both heads.
/// </summary>
public sealed class InputCommandMapper
{
    private readonly Dictionary<InputContext, Dictionary<GameKey, Action>> _keyMap = new();

    public InputCommandMapper()
    {
        foreach (InputContext ctx in Enum.GetValues<InputContext>())
            _keyMap[ctx] = new Dictionary<GameKey, Action>();
    }

    /// <summary>Binds a key to an action within a context, replacing any existing binding.</summary>
    public void Bind(InputContext context, GameKey key, Action action) => _keyMap[context][key] = action;

    /// <summary>Binds a key in the Global context.</summary>
    public void Bind(GameKey key, Action action) => Bind(InputContext.Global, key, action);

    /// <summary>Removes a binding.</summary>
    public void Unbind(InputContext context, GameKey key) => _keyMap[context].Remove(key);

    /// <summary>True when the key is bound in the given context or globally.</summary>
    public bool IsBound(InputContext context, GameKey key) =>
        _keyMap[context].ContainsKey(key) || _keyMap[InputContext.Global].ContainsKey(key);

    /// <summary>
    /// Runs the action bound to <paramref name="key"/> in <paramref name="context"/>, falling back to
    /// the Global context. Returns whether anything ran.
    /// </summary>
    public bool Execute(InputContext context, GameKey key)
    {
        if (_keyMap[context].TryGetValue(key, out var action))
        {
            action();
            return true;
        }

        if (context != InputContext.Global && _keyMap[InputContext.Global].TryGetValue(key, out var globalAction))
        {
            globalAction();
            return true;
        }

        return false;
    }
}
