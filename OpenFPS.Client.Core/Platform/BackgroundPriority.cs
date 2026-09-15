using System;
using System.Runtime.InteropServices;
using System.Threading;
using Serilog;

namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// Puts a loader thread BELOW everything that has to meet a deadline, which on Linux is the only
/// scheduling lever a game actually has.
///
/// <see cref="Thread.Priority"/> looks like the obvious tool and is a placebo here: CoreCLR on Unix
/// accepts the assignment and silently does not apply it, because raising a thread's priority needs
/// CAP_SYS_NICE and an unprivileged process does not have it. So on a map load the acoustic bake,
/// the Steam Audio scene build, the sample decodes, the JIT's own threads, the engine render pool
/// and FMOD's mixer thread — which also fails to get real-time scheduling unprivileged — all run at
/// exactly the same priority, and the scheduler hands the audio no more than its share of a machine
/// that is briefly oversubscribed. That is what "buffer depth, thread priority and dedicated threads
/// all helped and none fixed it" was describing.
///
/// What IS allowed without privilege is LOWERING your own threads, and it is the same lever seen
/// from the other end: nice the loader to +10 and the mixer and the engine producers win every
/// contention against it. The load takes slightly longer and is heard rather than heard through.
/// </summary>
public static class BackgroundPriority
{
    private const int PRIO_PROCESS = 0;

    [DllImport("libc", SetLastError = true)] private static extern int setpriority(int which, int who, int prio);
    [DllImport("libc", SetLastError = true)] private static extern int gettid();

    /// <summary>
    /// Nices the CALLING thread down. Call it as the first thing a loader thread does — the value is
    /// per-thread on Linux (setpriority's PRIO_PROCESS acts on a task, not the thread group), so it
    /// must run on the thread it is meant to slow, not on the one that started it.
    /// </summary>
    public static void LowerThisThread(string what, int nice = 10)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            if (setpriority(PRIO_PROCESS, gettid(), nice) != 0)
                Log.Debug("Could not nice {What} to +{Nice}: errno {Errno}", what, nice, Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not nice {What}; the loader will compete with the mixer.", what);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a dedicated niced thread rather than the shared pool.
    ///
    /// Two separate reasons, and both of them were live bugs. The nice is above. The dedicated thread
    /// is because the .NET thread pool is where the sample decodes and the scene build already are:
    /// long blocking work on a pool that grows by a thread or two per second starves everything else
    /// queued on it, and a bake that takes seconds has no business there.
    /// </summary>
    public static Thread RunLowered(string name, Action work, int nice = 10)
    {
        var t = new Thread(() => { LowerThisThread(name, nice); work(); })
        {
            IsBackground = true,
            Name = name,
        };
        t.Start();
        return t;
    }
}
