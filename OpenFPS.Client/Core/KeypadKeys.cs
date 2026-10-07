namespace OpenFPS.Client.Core;

/// <summary>
/// Tells the keypad's navigation keys from the cluster's on Windows. With Num Lock off, keypad 8 is
/// reported as Up, keypad 2 as Down, keypad period as Delete and so on, with the same virtual key
/// codes as the arrow and editing keys; only the extended-key flag (bit 24 of the key message) says
/// which was pressed. Those keypad keys are NVDA's and JAWS's review keys, and GDK names them apart
/// (KP_Up, KP_Delete), so the GTK head never maps them; this gives the Windows head the same answer.
/// Plain virtual key codes, so the tests can run it on Linux.
/// </summary>
public static class KeypadKeys
{
    private const int VK_CLEAR = 0x0C, VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24,
                      VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_INSERT = 0x2D, VK_DELETE = 0x2E;

    /// <summary>True for a keypad key pressed with Num Lock off: a navigation key without the
    /// extended flag. Keypad 5 (Clear) has no twin and is always the keypad's.</summary>
    public static bool IsNumLockOffKeypad(int virtualKey, bool extended) => virtualKey switch
    {
        VK_CLEAR => true,
        VK_PRIOR or VK_NEXT or VK_END or VK_HOME or VK_LEFT or VK_UP or VK_RIGHT or VK_DOWN or VK_INSERT or VK_DELETE => !extended,
        _ => false,
    };

    /// <summary>Whether a WM_KEYDOWN or WM_KEYUP's lParam has the extended-key flag.</summary>
    public static bool IsExtended(long lParam) => (lParam & (1L << 24)) != 0;
}
