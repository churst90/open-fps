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

/// <summary>Modifier keys held alongside a binding. Shift-F5 is a different binding from F5.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
}

/// <summary>
/// Maps a key press to an action, per context, with rebinding as a first-class operation.
/// Keyed on the neutral <see cref="GameKey"/>, so one binding table serves both heads.
///
/// A binding may name modifiers, and one that does is tried FIRST. A binding that does not name any
/// then runs whatever is held — which is what keeps a key that reads its own modifiers working (the
/// chat brackets decide between stepping messages and stepping buffers by asking about shift
/// themselves) while still letting shift-F5 mean something different from F5.
/// </summary>
public sealed class InputCommandMapper
{
    private readonly Dictionary<InputContext, Dictionary<(GameKey Key, KeyModifiers Modifiers), Action>> _keyMap = new();

    public InputCommandMapper()
    {
        foreach (InputContext ctx in Enum.GetValues<InputContext>())
            _keyMap[ctx] = new Dictionary<(GameKey, KeyModifiers), Action>();
    }

    /// <summary>Binds a key to an action within a context, replacing any existing binding. The
    /// binding runs whatever modifiers are held unless a more specific one exists.</summary>
    public void Bind(InputContext context, GameKey key, Action action) => _keyMap[context][(key, KeyModifiers.None)] = action;

    /// <summary>Binds a key HELD WITH modifiers, which takes precedence over the same key alone.</summary>
    public void Bind(InputContext context, GameKey key, KeyModifiers modifiers, Action action) =>
        _keyMap[context][(key, modifiers)] = action;

    /// <summary>Binds a key in the Global context.</summary>
    public void Bind(GameKey key, Action action) => Bind(InputContext.Global, key, action);

    /// <summary>Binds a key with modifiers in the Global context.</summary>
    public void Bind(GameKey key, KeyModifiers modifiers, Action action) => Bind(InputContext.Global, key, modifiers, action);

    /// <summary>Removes a binding.</summary>
    public void Unbind(InputContext context, GameKey key) => _keyMap[context].Remove((key, KeyModifiers.None));

    /// <summary>Removes a modified binding.</summary>
    public void Unbind(InputContext context, GameKey key, KeyModifiers modifiers) => _keyMap[context].Remove((key, modifiers));

    /// <summary>True when the key is bound in the given context or globally.</summary>
    public bool IsBound(InputContext context, GameKey key) => IsBound(context, key, KeyModifiers.None);

    /// <summary>True when the key, with those modifiers, resolves to anything at all.</summary>
    public bool IsBound(InputContext context, GameKey key, KeyModifiers modifiers) =>
        Resolve(context, key, modifiers) != null;

    /// <summary>
    /// Runs the action bound to <paramref name="key"/> in <paramref name="context"/>, falling back to
    /// the Global context. Returns whether anything ran.
    /// </summary>
    public bool Execute(InputContext context, GameKey key) => Execute(context, key, KeyModifiers.None);

    /// <summary>
    /// Runs the action bound to <paramref name="key"/> held with <paramref name="modifiers"/>.
    ///
    /// Order is most specific first: this context with those modifiers, then Global with those
    /// modifiers, then this context unmodified, then Global unmodified.
    /// </summary>
    public bool Execute(InputContext context, GameKey key, KeyModifiers modifiers)
    {
        var action = Resolve(context, key, modifiers);
        if (action == null) return false;
        action();
        return true;
    }

    private Action? Resolve(InputContext context, GameKey key, KeyModifiers modifiers)
    {
        if (modifiers != KeyModifiers.None)
        {
            if (_keyMap[context].TryGetValue((key, modifiers), out var chord)) return chord;
            if (context != InputContext.Global && _keyMap[InputContext.Global].TryGetValue((key, modifiers), out var globalChord))
                return globalChord;
        }

        if (_keyMap[context].TryGetValue((key, KeyModifiers.None), out var bare)) return bare;
        if (context != InputContext.Global && _keyMap[InputContext.Global].TryGetValue((key, KeyModifiers.None), out var globalBare))
            return globalBare;

        return null;
    }
}
