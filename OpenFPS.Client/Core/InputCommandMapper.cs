using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace OpenFPS.Client.Core;

public enum InputContext { Global, Gameplay, UI, Chat }

/// <summary>
/// Service responsible for mapping physical key presses to high-level game actions.
/// Supports dynamic rebinding of keys and contextual bindings (e.g. Gameplay vs UI).
/// </summary>
public class InputCommandMapper
{
    private readonly Dictionary<InputContext, Dictionary<Keys, Action>> _keyMap = new();

    public InputCommandMapper()
    {
        foreach (InputContext ctx in Enum.GetValues(typeof(InputContext)))
            _keyMap[ctx] = new Dictionary<Keys, Action>();
    }

    /// <summary>
    /// Binds a physical key to an action within a specific context. 
    /// </summary>
    public void Bind(InputContext context, Keys key, Action action)
    {
        _keyMap[context][key] = action;
    }

    /// <summary>
    /// Binds a physical key to an action in the Global context.
    /// </summary>
    public void Bind(Keys key, Action action) => Bind(InputContext.Global, key, action);

    /// <summary>
    /// Removes the binding for a specific key in a context.
    /// </summary>
    public void Unbind(InputContext context, Keys key)
    {
        _keyMap[context].Remove(key);
    }

    /// <summary>
    /// Executes the action associated with a key in the given context.
    /// Falls back to Global context if no specific binding exists.
    /// </summary>
    public bool Execute(InputContext context, Keys key)
    {
        var cleanKey = key & Keys.KeyCode;
        
        if (_keyMap[context].TryGetValue(cleanKey, out var action))
        {
            action();
            return true;
        }
        
        if (context != InputContext.Global && _keyMap[InputContext.Global].TryGetValue(cleanKey, out var globalAction))
        {
            globalAction();
            return true;
        }

        return false;
    }
}
