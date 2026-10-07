using System.Runtime.InteropServices;
using Serilog;

namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// Puts a loader thread below everything that has to meet a deadline: on Linux, the only scheduling
/// lever an unprivileged game has.
///
/// <see cref="Thread.Priority"/> is a placebo on Unix: CoreCLR accepts it and silently does not apply
/// it (raising a priority needs CAP_SYS_NICE), and FMOD's mixer gets no real-time scheduling either.
/// Lowering your own threads needs no privilege: nice the loader to +10 and the mixer and the engine
/// producers win every contention against it during a map load.
/// </summary>
public static class BackgroundPriority
{
    private const int PRIO_PROCESS = 0;

    [DllImport("libc", SetLastError = true)] private static extern int setpriority(int which, int who, int prio);
    [DllImport("libc", SetLastError = true)] private static extern int gettid();

    /// <summary>
    /// Nices the calling thread down. It must run on the thread it is meant to slow: on Linux
    /// setpriority's PRIO_PROCESS acts on a task, not the thread group.
    /// </summary>
    public static void LowerThisThread(string what, int nice = 10)
    {
        if (OperatingSystem.IsWindows())
        {
            // On Windows the managed priority is real. BelowNormal loses to the mixer without starving.
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; }
            catch (Exception ex) { Log.Debug(ex, "Could not lower {What}; the loader will compete with the mixer.", what); }
            return;
        }
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
    /// Runs <paramref name="work"/> on a dedicated niced thread. Not the thread pool: the sample decodes
    /// and the scene build are there, and seconds of blocking work on a pool that grows a thread or two
    /// a second starves them.
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
