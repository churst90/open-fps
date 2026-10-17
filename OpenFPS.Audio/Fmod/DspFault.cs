namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>Helpers every DSP read callback uses. Mixer thread: no locks, no waits, and no allocation after
/// a thread's first look at FMOD's function table.</summary>
internal static class DspCallback
{
    /// <summary>
    /// The largest block a callback renders into its voice's own scratch buffer. The mixer runs 1,024
    /// (FmodAudioProvider, setDSPBufferSize); a longer block is silenced rather than allocated for,
    /// since an allocation on the mixer thread can start a collection there.
    /// </summary>
    public const int MaxBlock = 4096;

    /// <summary>
    /// Writes silence into a callback's output. A callback that returns without writing leaves
    /// whatever the buffer last held, which FMOD then mixes: a stale block heard as a click or a
    /// repeat. Clears only when FMOD has said how many channels the buffer holds.
    /// </summary>
    public static unsafe void Silence(IntPtr outbuffer, uint length, int outchannels)
    {
        if (outbuffer != IntPtr.Zero && outchannels > 0)
            new Span<float>((void*)outbuffer, (int)length * outchannels).Clear();
    }

    /// <summary>
    /// Copies the input to the output unchanged, for a unit on a bus that carries other sounds:
    /// silencing it would silence them. Silence when the channel counts differ.
    /// </summary>
    public static unsafe void PassThrough(IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, int outchannels)
    {
        if (inbuffer != IntPtr.Zero && outbuffer != IntPtr.Zero && inchannels > 0 && inchannels == outchannels)
            new ReadOnlySpan<float>((void*)inbuffer, (int)length * inchannels)
                .CopyTo(new Span<float>((void*)outbuffer, (int)length * outchannels));
        else Silence(outbuffer, length, outchannels);
    }

    // Cached per function table, per thread: there is more than one mixer thread, and FMOD hands out
    // one table for the life of a system.
    [ThreadStatic] private static IntPtr _cachedTable;
    [ThreadStatic] private static FMOD.DSP_GETUSERDATA_FUNC? _cachedGet;

    [ThreadStatic] private static IntPtr _cachedClockTable;
    [ThreadStatic] private static FMOD.DSP_GETCLOCK_FUNC? _cachedClock;

    /// <summary>
    /// The clock of the block this callback is rendering, through the callback's own function table (as
    /// <see cref="UserData"/>). It is the channel's clock, counted from when that channel started, and
    /// FMOD starts a channel part way into a block, so two channels disagree by any number of samples
    /// (AudioLab --cabin probe=align: 239 one run, 785 another). Add the channel's offset to its parent's
    /// clock (Channel.getDSPClock, game thread) to put two channels on one time line.
    /// </summary>
    public static bool Clock(ref FMOD.DSP_STATE state, out ulong clock)
    {
        clock = 0;
        IntPtr table = state.functionsPtr;
        if (table == IntPtr.Zero) return false;
        if (table != _cachedClockTable || _cachedClock == null)
        {
            var fns = System.Runtime.InteropServices.Marshal
                .PtrToStructure<FMOD.DSP_STATE_FUNCTIONS>(table);
            _cachedClock = fns.getclock;
            _cachedClockTable = table;
        }
        var get = _cachedClock;
        if (get == null) return false;
        return get(ref state, out clock, out _, out _) == FMOD.RESULT.OK;
    }

    /// <summary>
    /// A callback's userdata, through the callback's own function table. Never the general API
    /// (`new FMOD.DSP(dsp_state.instance).getUserData`) on the mixer thread: it takes FMOD's system locks
    /// inside its own mix, and crashed the client in proportion to how many DSPs ran. See
    /// docs/THE_MIXER_THREAD_CRASH.md, "A callback's userdata through its own function table".
    /// </summary>
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

/// <summary>
/// What a DSP read callback does instead of logging. A mixer callback must not do I/O: `removeDSP`
/// blocks on an in-flight callback while holding the provider's lock, so a log line written from the
/// mixer (to a stdout nobody reads) froze the whole client. A fault is two interlocked writes here, and
/// the audio update logs it once from the game thread. See docs/THE_MIXER_THREAD_CRASH.md, "A mixer
/// callback does no I/O".
/// </summary>
internal static class DspFault
{
    private static int _count;
    private static string? _first;

    /// <summary>Called from a DSP callback's catch. Lock-free; the message string is its one allocation,
    /// made only for the first fault.</summary>
    public static void Record(string processor, Exception ex)
    {
        Interlocked.Increment(ref _count);
        // First writer wins; every later one is only counted.
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
