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
    UI
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
/// Key to action, per context, on the neutral <see cref="GameKey"/> so both heads share one table. A
/// binding that names modifiers is tried first; one that names none runs whatever is held, so a key
/// that reads its own modifiers (the chat brackets ask about Shift) still works.
/// </summary>
public sealed class InputCommandMapper
{
    private readonly Dictionary<InputContext, Dictionary<(GameKey Key, KeyModifiers Modifiers), Action>> _keyMap = new();

    public InputCommandMapper()
    {
        foreach (InputContext ctx in Enum.GetValues<InputContext>())
            _keyMap[ctx] = new Dictionary<(GameKey, KeyModifiers), Action>();
    }

    /// <summary>Replaces any binding of the key. It runs whatever modifiers are held unless a binding
    /// with those modifiers exists.</summary>
    public void Bind(InputContext context, GameKey key, Action action) => _keyMap[context][(key, KeyModifiers.None)] = action;

    /// <summary>Binds a key held with modifiers, which wins over the same key alone.</summary>
    public void Bind(InputContext context, GameKey key, KeyModifiers modifiers, Action action) =>
        _keyMap[context][(key, modifiers)] = action;

    public void Bind(GameKey key, Action action) => Bind(InputContext.Global, key, action);

    public void Bind(GameKey key, KeyModifiers modifiers, Action action) => Bind(InputContext.Global, key, modifiers, action);

    /// <summary>True when the key is bound in the context or globally.</summary>
    public bool IsBound(InputContext context, GameKey key) => IsBound(context, key, KeyModifiers.None);

    /// <summary>True when the key, with those modifiers, resolves to anything at all.</summary>
    public bool IsBound(InputContext context, GameKey key, KeyModifiers modifiers) =>
        Resolve(context, key, modifiers) != null;

    /// <summary>Runs the key's action in the context, else the Global one. Returns whether anything ran.</summary>
    public bool Execute(InputContext context, GameKey key) => Execute(context, key, KeyModifiers.None);

    /// <summary>Runs the key's action, most specific first: this context with the modifiers, Global with
    /// them, this context bare, Global bare.</summary>
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
