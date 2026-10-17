using System.Runtime.InteropServices;
using Serilog;

namespace OpenFPS.Native.Platform;

/// <summary>
/// Makes the calling thread treat subnormal numbers as zero: flush-to-zero for results and
/// denormals-are-zero for inputs. Set on the threads that render ahead of the mixer, never on FMOD's.
/// </summary>
/// <remarks>
/// A resonator or a glide ringing down to silence passes below 1.2e-38 (float) or 2.2e-308 (double), and
/// an Intel processor works on numbers that small about a hundred times slower, in microcode: a 1.9 s door
/// took 431 s to render on an i7-13700HX, 14 s on AMD (docs/AUDIO_QUALITY_2026-10-06.md, row 14). Nothing a
/// float sample can hold is that small, so flushing them changes no sound.
///
/// .NET has no managed way to reach the control register (MXCSR on x86-64, FPCR on ARM64), so this goes
/// through the C library: glibc's fegetenv and fesetenv on Linux, whose fenv_t carries the register by
/// name, and the C runtime's _controlfp_s on Windows. Anywhere else it does nothing and says so once.
///
/// A thread inherits the setting from the thread that starts it, on Linux, so a flushed thread that starts
/// one hands it on; Run gives a borrowed thread back as it found it.
/// </remarks>
public static class FlushToZero
{
    // x86-64: MXCSR's flush-to-zero (bit 15) and denormals-are-zero (bit 6).
    private const uint MxcsrFtz = 1u << 15, MxcsrDaz = 1u << 6;
    // ARM64: FPCR's flush-to-zero (bit 24), which flushes inputs and results alike.
    private const uint FpcrFz = 1u << 24;
    // glibc's fenv_t: 32 bytes on x86-64 with __mxcsr at byte 28 (the 28-byte x87 environment first);
    // 8 bytes on ARM64, __fpcr then __fpsr.
    private const int FenvBytes = 32, MxcsrOffset = 28;
    // The C runtime's denormal control: _MCW_DN is the mask, _DN_FLUSH flushes inputs and results.
    private const uint McwDn = 0x03000000, DnFlush = 0x01000000;

    private enum Way { Unknown, GlibcX64, GlibcArm64, WindowsCrt, None }
    private static Way _way = Way.Unknown;
    private static int _saidOn, _saidOff;

    [DllImport("libm.so.6", EntryPoint = "fegetenv")] private static extern unsafe int fegetenv(byte* env);
    [DllImport("libm.so.6", EntryPoint = "fesetenv")] private static extern unsafe int fesetenv(byte* env);
    [DllImport("ucrtbase.dll", EntryPoint = "_controlfp_s")] private static extern int controlfp_s(out uint current, uint newControl, uint mask);

    /// <summary>
    /// Turns the flush on for the calling thread, once at its start. False, with one log line for the
    /// process, when this platform has no way to do it or the call failed; the thread runs on as it was.
    /// </summary>
    public static bool ThisThread(string what)
    {
        bool on = TryRead(out uint state) && TryWrite(Flushed(state)) && TryRead(out state) && IsFlushed(state);
        Say(on, what);
        return on;
    }

    /// <summary>Whether the calling thread flushes now; false where it cannot be read.</summary>
    public static bool IsOn => TryRead(out uint state) && IsFlushed(state);

    /// <summary>
    /// Runs one piece of work flushed on a borrowed thread (the .NET thread pool), and gives the thread back
    /// as it found it, so nothing else that runs there later is changed.
    /// </summary>
    public static T Run<T>(string what, Func<T> work)
    {
        bool had = TryRead(out uint before);
        bool on = had && TryWrite(Flushed(before));
        Say(on, what);
        try { return work(); }
        finally { if (on) TryWrite(before); }
    }

    /// <inheritdoc cref="Run{T}(string, Func{T})"/>
    public static void Run(string what, Action work) => Run<bool>(what, () => { work(); return true; });

    /// <summary>A render made ahead on the thread pool, flushed while it runs (<see cref="Run{T}"/>).</summary>
    public static Task<T> Ahead<T>(string what, Func<T> work) => Task.Run(() => Run(what, work));

    /// <inheritdoc cref="Ahead{T}(string, Func{T})"/>
    public static Task Ahead(string what, Action work) => Task.Run(() => Run(what, work));

    private static uint Flushed(uint state) => WayHere() switch
    {
        Way.GlibcX64 => state | MxcsrFtz | MxcsrDaz,
        Way.GlibcArm64 => state | FpcrFz,
        Way.WindowsCrt => (state & ~McwDn) | DnFlush,
        _ => state,
    };

    private static bool IsFlushed(uint state) => WayHere() switch
    {
        Way.GlibcX64 => (state & (MxcsrFtz | MxcsrDaz)) == (MxcsrFtz | MxcsrDaz),
        Way.GlibcArm64 => (state & FpcrFz) != 0,
        Way.WindowsCrt => (state & McwDn) == DnFlush,
        _ => false,
    };

    private static Way WayHere()
    {
        if (_way != Way.Unknown) return _way;
        var arch = RuntimeInformation.ProcessArchitecture;
        _way = OperatingSystem.IsLinux() && arch == Architecture.X64 ? Way.GlibcX64
             : OperatingSystem.IsLinux() && arch == Architecture.Arm64 ? Way.GlibcArm64
             : OperatingSystem.IsWindows() && (arch == Architecture.X64 || arch == Architecture.Arm64) ? Way.WindowsCrt
             : Way.None;
        return _way;
    }

    /// <summary>The control register's value: MXCSR, FPCR, or the C runtime's control word.</summary>
    internal static unsafe bool TryRead(out uint state)
    {
        state = 0;
        try
        {
            switch (WayHere())
            {
                case Way.GlibcX64:
                case Way.GlibcArm64:
                {
                    byte* env = stackalloc byte[FenvBytes];
                    if (fegetenv(env) != 0) return false;
                    state = *(uint*)(env + (_way == Way.GlibcX64 ? MxcsrOffset : 0));
                    return true;
                }
                case Way.WindowsCrt:
                    return controlfp_s(out state, 0, 0) == 0;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _way = Way.None;
            Log.Information("Subnormal numbers are not flushed on this machine: {Message}", ex.Message);
            return false;
        }
    }

    internal static unsafe bool TryWrite(uint state)
    {
        try
        {
            switch (WayHere())
            {
                case Way.GlibcX64:
                case Way.GlibcArm64:
                {
                    // The rest of the environment (the x87 control word, the status flags) stays as it is.
                    byte* env = stackalloc byte[FenvBytes];
                    if (fegetenv(env) != 0) return false;
                    *(uint*)(env + (_way == Way.GlibcX64 ? MxcsrOffset : 0)) = state;
                    return fesetenv(env) == 0;
                }
                case Way.WindowsCrt:
                    return controlfp_s(out _, state & McwDn, McwDn) == 0;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _way = Way.None;
            Log.Information("Subnormal numbers are not flushed on this machine: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>One line for the process whichever way it went, not one a thread.</summary>
    private static void Say(bool on, string what)
    {
        if (on)
        {
            if (Interlocked.Exchange(ref _saidOn, 1) == 0)
                Log.Information("Render-ahead threads flush subnormal numbers to zero ({What} first; {Way}, {Arch}).",
                                what, _way, RuntimeInformation.ProcessArchitecture);
        }
        else if (Interlocked.Exchange(ref _saidOff, 1) == 0)
            Log.Information("Subnormal numbers are not flushed on {What} ({Os}, {Arch}): a voice ringing down to silence may cost more on an Intel processor.",
                            what, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture);
    }
}
