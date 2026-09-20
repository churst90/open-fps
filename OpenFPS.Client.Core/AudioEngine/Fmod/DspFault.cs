using System;
using System.Threading;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// What a DSP read callback does instead of logging.
///
/// A MIXER CALLBACK MUST NOT DO I/O. It runs on FMOD's mixer thread with the deadline of the whole
/// mix running down, and the thread that tears a voice down calls `removeDSP`, which BLOCKS until
/// any in-flight callback has finished — while holding the provider's lock. So a callback that
/// stops to write a log line stops the mixer, which stops the teardown, which stops everything that
/// wants that lock: the game loop, the audio update, the acoustic worker. The whole client freezes,
/// the log ends mid-stream, and from the chair that is indistinguishable from a crash.
///
/// Serilog's console sink writes to stdout. If whatever is reading stdout is slow or has stopped —
/// a terminal you alt-tabbed away from — that write blocks, and the freeze is permanent.
///
/// So a faulting callback records the fault in two interlocked writes and returns. The AUDIO UPDATE,
/// on the game thread, notices and logs it once. Nothing in here allocates, takes a lock, or waits.
/// </summary>
internal static class DspCallback
{
    /// <summary>
    /// A DSP callback's userdata, fetched THROUGH THE CALLBACK'S OWN FUNCTION TABLE.
    ///
    /// THIS IS THE RULE FMOD STATES AND EVERY CALLBACK IN THIS ENGINE BROKE. The general API — the
    /// one you use from the game thread — must NOT be called from inside a DSP callback; the
    /// callback is handed a table of accessors in DSP_STATE precisely so it does not have to.
    /// Every processor here did the forbidden thing:
    ///
    ///     var dsp = new FMOD.DSP(dsp_state.instance);
    ///     dsp.getUserData(out userData);          // the general API, on the mixer thread
    ///
    /// which re-enters FMOD and takes its system locks from inside its own mix, once per DSP per
    /// block. It does not fail every time; it fails as a function of how many DSPs are running,
    /// which is exactly the shape the crash had:
    ///
    ///   * 100 s to crash, then 16 s once the broadcast radius went from 1,200 m to 3,000 and far
    ///     more voices were live;
    ///   * turning OFF any single subsystem — machines, reflections, HRTF, the Steam Audio
    ///     simulator — still crashed, because none of them is the cause, each is just some of the
    ///     DSPs;
    ///   * turning off ALL of them at once survived 131 s, because that is the configuration with
    ///     the fewest callbacks per block.
    ///
    /// The table's getuserdata is the supported accessor and touches none of FMOD's locks.
    ///
    /// The delegate is cached per functions-table pointer, per thread: marshalling the struct on
    /// every block for every voice would be its own problem, and FMOD hands out the same table for
    /// the life of a system. ThreadStatic because there is more than one mixer thread.
    /// </summary>
    [ThreadStatic] private static IntPtr _cachedTable;
    [ThreadStatic] private static FMOD.DSP_GETUSERDATA_FUNC? _cachedGet;

    public static IntPtr UserData(ref FMOD.DSP_STATE state)
    {
        IntPtr table = state.functionsPtr;
        if (table == IntPtr.Zero) return IntPtr.Zero;
        if (table != _cachedTable || _cachedGet == null)
        {
            var fns = System.Runtime.InteropServices.Marshal
                .PtrToStructure<FMOD.DSP_STATE_FUNCTIONS>(table);
            _cachedGet = fns.getuserdata;
            _cachedTable = table;
        }
        var get = _cachedGet;
        if (get == null) return IntPtr.Zero;
        return get(ref state, out IntPtr ud) == FMOD.RESULT.OK ? ud : IntPtr.Zero;
    }
}

internal static class DspFault
{
    private static int _count;
    private static string? _first;

    /// <summary>Called from a DSP callback. Must stay allocation-free and lock-free.</summary>
    public static void Record(string processor, Exception ex)
    {
        Interlocked.Increment(ref _count);
        // First writer wins; every later one is just counted. ToString() would allocate, so the
        // message is taken as-is — it is already an interned literal plus the exception's own text,
        // which the runtime has built by the time this is called.
        Interlocked.CompareExchange(ref _first, processor + ": " + ex.Message, null);
    }

    /// <summary>Called from the game thread. Returns true and clears if there is anything to say.</summary>
    public static bool TryDrain(out int count, out string? first)
    {
        count = Interlocked.Exchange(ref _count, 0);
        first = Interlocked.Exchange(ref _first, null);
        return count > 0;
    }
}
