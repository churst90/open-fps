using System;
using Serilog;

namespace OpenFPS.Client.Core;

/// <summary>
/// Says in the log why the client stopped. A clean quit, an unhandled exception on a background
/// thread and a native crash all look the same from the chair: the sound stops. A clean exit says
/// so, an exception says what it was and on which thread, and a native crash says nothing here,
/// which is itself the answer. Both heads install it first thing, once the log is open.
/// </summary>
public static class ProcessLifeLog
{
    public static void Install(DateTime startedUtc)
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Log.Information("Client process exiting normally (ran {Sec:F0} s).", (DateTime.UtcNow - startedUtc).TotalSeconds);
            Log.CloseAndFlush();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "UNHANDLED EXCEPTION on a background thread — terminating={T}.", e.IsTerminating);
            Log.CloseAndFlush();
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception (the task was collected without anyone reading it).");
            e.SetObserved();
        };
    }
}
