namespace OpenFPS.Client.Core.Input;

/// <summary>Modifier keys held alongside a binding. Shift-F5 is a different binding from F5.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
}
