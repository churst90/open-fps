namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>A DSP's state the non-finite guard can name and reset (NonFinite.After).</summary>
public interface IGuardedUnit
{
    NonFiniteUnit Guard { get; }
    /// <summary>Forget whatever could hold a NaN. Mixer thread: no allocation, no locks.</summary>
    void ResetAfterFault() { }
}

/// <summary>A unit's flag (reported once) and the name and region its [NONFINITE] line carries.
/// The name and region are written by the game thread when the unit is handed to a sound.</summary>
public sealed class NonFiniteUnit
{
    public int Reported;
    public volatile string? Name;
    public int Region = int.MinValue;
}

/// <summary>
/// The guard on every custom DSP that writes into the mix: a block with a NaN or an infinity is zeroed
/// before it leaves the unit, the unit is reset, and it is named once in the log ([NONFINITE]).
/// </summary>
/// <remarks>
/// One NaN reaching a bus is summed into everything after it: the master limiter's state stays NaN
/// and the game is silent until restarted, with nothing in the log (Cody, 2026-10-03: "a pop while
/// inside one of the buildings and the audio just cut out"). A unit with recursive state holds a NaN
/// for ever, hence the reset.
///
/// Mixer thread: nothing here allocates, locks or throws. A report is two strings the unit already
/// holds, put into a fixed ring that the game thread logs (<see cref="Drain"/>).
/// </remarks>
internal static class NonFinite
{
    private const int Slots = 64;
    private static readonly string?[] _kinds = new string?[Slots];
    private static readonly string?[] _names = new string?[Slots];
    private static readonly int[] _regions = new int[Slots];
    private static int _head, _read;
    /// <summary>Blocks zeroed so far, every unit.</summary>
    public static int Blocks;

    public static unsafe bool AllFinite(float* buffer, int count)
    {
        for (int i = 0; i < count; i++) if (!float.IsFinite(buffer[i])) return false;
        return true;
    }

    public static bool AllFinite(ReadOnlySpan<float> x)
    {
        foreach (float v in x) if (!float.IsFinite(v)) return false;
        return true;
    }

    /// <summary>
    /// Zeroes <paramref name="buffer"/> if any of its <paramref name="count"/> samples is not finite,
    /// and names the unit the first time (<paramref name="reported"/> is the unit's own flag). True if
    /// it zeroed: the caller then resets the unit's state.
    /// </summary>
    public static unsafe bool Scrub(float* buffer, int count, ref int reported, string kind, string? name = null, int region = int.MinValue)
    {
        if (buffer == null || count <= 0 || AllFinite(buffer, count)) return false;
        new Span<float>(buffer, count).Clear();
        Report(ref reported, kind, name, region);
        return true;
    }

    /// <summary>As the pointer form of Scrub, for a managed buffer.</summary>
    public static bool Scrub(Span<float> buffer, ref int reported, string kind, string? name = null, int region = int.MinValue)
    {
        if (AllFinite(buffer)) return false;
        buffer.Clear();
        Report(ref reported, kind, name, region);
        return true;
    }

    /// <summary>Counts a bad block, and names the unit the first time.</summary>
    public static void Report(ref int reported, string kind, string? name = null, int region = int.MinValue)
    {
        Interlocked.Increment(ref Blocks);
        if (Interlocked.Exchange(ref reported, 1) != 0) return;
        int slot = (Interlocked.Increment(ref _head) - 1) & (Slots - 1);
        _regions[slot] = region;
        _names[slot] = name;
        Volatile.Write(ref _kinds[slot], kind);
    }

    /// <summary>Game thread: each unit reported since the last call, once: what it is, which one (or
    /// null), its region (int.MinValue: none).</summary>
    public static void Drain(Action<string, string?, int> log)
    {
        int head = Volatile.Read(ref _head);
        if (head - _read > Slots) _read = head - Slots;
        for (; _read < head; _read++)
        {
            int slot = _read & (Slots - 1);
            string? kind = Volatile.Read(ref _kinds[slot]);
            if (kind == null) break;            // written a moment from now; next time
            string? name = _names[slot];
            _kinds[slot] = null;
            log(kind, name, _regions[slot]);
        }
    }

    /// <summary>
    /// The guard for a callback whose state is an <see cref="IGuardedUnit"/>: after the callback has
    /// written its block, a non-finite one is zeroed, the unit named once as <paramref name="kind"/>,
    /// and its state reset. A state that is not one is still scrubbed, reported under
    /// <paramref name="fallback"/>. Mixer thread; never throws.
    /// </summary>
    public static unsafe void After(ref FMOD.DSP_STATE dsp_state, IntPtr outbuffer, uint length, int inchannels, int outchannels,
                                    string kind, ref int fallback)
    {
        if (outbuffer == IntPtr.Zero) return;
        int ch = outchannels > 0 ? outchannels : (inchannels > 0 ? inchannels : 2);
        int count = (int)length * ch;
        if (AllFinite((float*)outbuffer, count)) return;
        IntPtr userData = DspCallback.UserData(ref dsp_state);
        var unit = userData != IntPtr.Zero ? System.Runtime.InteropServices.GCHandle.FromIntPtr(userData).Target as IGuardedUnit : null;
        if (unit == null) { Scrub((float*)outbuffer, count, ref fallback, kind); return; }
        Scrub((float*)outbuffer, count, ref unit.Guard.Reported, kind, unit.Guard.Name, unit.Guard.Region);
        try { unit.ResetAfterFault(); } catch { }
    }

    public static string Line(string kind, string? name, int region)
        => $"[NONFINITE] {kind}{(name != null ? " " + name : "")}{(region != int.MinValue ? $", region {region}" : "")}: "
         + "a block with NaN or infinity was zeroed before the mix and the unit reset. Once per unit.";
}
