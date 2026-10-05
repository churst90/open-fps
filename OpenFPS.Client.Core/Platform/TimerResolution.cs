using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Serilog;

namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// Asks Windows for a one-millisecond timer for the life of the process.
///
/// Windows wakes a sleeping thread on its timer tick, and the tick is 15.6 ms unless a process asks
/// for finer. So every Thread.Sleep(1) to Sleep(5) in the client is really a sleep of 15.6 ms there:
/// the audio thread, which sleeps up to 4 ms between passes to run at 250 Hz, ran at 59 Hz, and every
/// pass-by stepped its pitch by 2.7 % instead of 0.65 %; the game loop, which places every moving
/// sound once an iteration and sleeps 5 ms, ran at 63 Hz, so voices were placed at positions 110 ms
/// old (Sean's log, 2026-10-05, build f1a0421123d3). Linux sleeps to the microsecond and needs none
/// of this; there it does nothing.
///
/// Windows 11 also stops honouring a process's timer request when it decides the process is not
/// being watched or heard, so the process opts out of that throttling as well — a game for people
/// who do not look at its window must not be judged by whether its window is in view.
/// </summary>
public sealed class TimerResolution : IDisposable
{
    /// <summary>The period asked for, ms; 0 when nothing was asked (not Windows, or it refused).</summary>
    public uint PeriodMs { get; }
    public bool Active => PeriodMs > 0;
    private int _ended;

    private TimerResolution(uint periodMs) => PeriodMs = periodMs;

    /// <summary>
    /// Raises the timer to <paramref name="wantedMs"/> (or the finest the machine offers, if that is
    /// coarser) and logs what it was, what it is now, and how long a one-millisecond sleep now takes.
    /// Dispose at exit to give it back; Windows also gives it back when the process ends.
    /// </summary>
    public static TimerResolution Raise(uint wantedMs = 1)
    {
        if (!OperatingSystem.IsWindows()) return new TimerResolution(0);
        try
        {
            double before = CurrentResolutionMs();
            var caps = new TimeCaps();
            uint period = timeGetDevCaps(ref caps, (uint)Marshal.SizeOf<TimeCaps>()) == 0
                ? Choose(wantedMs, caps.PeriodMin, caps.PeriodMax) : wantedMs;
            bool opted = KeepTimerWhenUnseen();
            if (timeBeginPeriod(period) != 0)
            {
                Log.Warning("Timer resolution: Windows refused {Period} ms; sleeps stay at {Before:F1} ms, and the audio "
                          + "thread and game loop run near 60 Hz.", period, before);
                return new TimerResolution(0);
            }
            Log.Information("Timer resolution: {Period} ms for this process (was {Before:F1} ms, now {After:F1} ms){Opt}. "
                          + "A 1 ms sleep takes {Sleep:F1} ms.",
                            period, before, CurrentResolutionMs(), opted ? "" : "; could not opt out of Windows 11 timer throttling",
                            MeasureSleepMs(1, 10));
            return new TimerResolution(period);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Timer resolution: could not be raised; sleeps stay at the system tick.");
            return new TimerResolution(0);
        }
    }

    /// <summary>The period to ask for: what was wanted, within what the machine can do.</summary>
    internal static uint Choose(uint wantedMs, uint minMs, uint maxMs)
        => minMs == 0 || maxMs < minMs ? Math.Max(1u, wantedMs) : Math.Clamp(Math.Max(1u, wantedMs), minMs, maxMs);

    /// <summary>How long Thread.Sleep(<paramref name="ms"/>) really takes on this thread, mean of
    /// <paramref name="samples"/>, ms. The figure that says whether the request took.</summary>
    public static double MeasureSleepMs(int ms = 1, int samples = 10)
    {
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < samples; i++) Thread.Sleep(ms);
        return clock.Elapsed.TotalMilliseconds / Math.Max(1, samples);
    }

    public void Dispose()
    {
        if (PeriodMs == 0 || Interlocked.Exchange(ref _ended, 1) != 0) return;
        if (OperatingSystem.IsWindows())
            try { timeEndPeriod(PeriodMs); } catch { /* the process is ending; Windows takes it back anyway */ }
    }

    /// <summary>The system timer's current period, ms, or NaN where it cannot be read.</summary>
    private static double CurrentResolutionMs()
    {
        try { return NtQueryTimerResolution(out _, out _, out uint current) == 0 ? current / 10_000.0 : double.NaN; }
        catch { return double.NaN; }
    }

    /// <summary>Windows 11: keep honouring the timer request when the window is hidden, minimised or
    /// covered. Not an error on older Windows, which never throttled it.</summary>
    private static bool KeepTimerWhenUnseen()
    {
        try
        {
            var state = new PowerThrottlingState
            {
                Version = PowerThrottlingCurrentVersion,
                ControlMask = PowerThrottlingIgnoreTimerResolution,
                StateMask = 0,   // control it, and turn it off: the request is honoured
            };
            return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeCaps { public uint PeriodMin; public uint PeriodMax; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version; public uint ControlMask; public uint StateMask; }

    private const int ProcessPowerThrottling = 4;            // PROCESS_INFORMATION_CLASS
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint PowerThrottlingIgnoreTimerResolution = 0x4;

    [DllImport("winmm.dll")] private static extern uint timeGetDevCaps(ref TimeCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);
    [DllImport("ntdll.dll")] private static extern int NtQueryTimerResolution(out uint coarsest, out uint finest, out uint current);
}
