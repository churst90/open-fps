using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Serilog;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;

namespace OpenFPS.Client;

public static class Program
{
    /// <summary>When this process started, so every "why did it stop" line can say how long it ran.</summary>
    public static readonly DateTime StartedUtc = DateTime.UtcNow;

    /// <summary>Where the log is written: a logs folder next to the game, or under Local AppData when
    /// the game folder cannot be written to. The main menu can open it.</summary>
    public static string LogDirectory { get; private set; } = "";

    [STAThread]
    public static void Main(string[] args)
    {
        // Machines, materials and prefabs are found relative to the working directory, as they are on
        // Linux, where the launcher sets it. A shortcut or a double-click from another folder would
        // otherwise start the game with no engines and no materials.
        Environment.CurrentDirectory = AppContext.BaseDirectory;

        LogDirectory = ChooseLogDirectory();
        string? logPath = Environment.GetEnvironmentVariable("OPENFPS_LOG");
        var cfg = new LoggerConfiguration().MinimumLevel.Information();
        cfg = string.IsNullOrWhiteSpace(logPath)
            ? cfg.WriteTo.File(Path.Combine(LogDirectory, "client-.log"), rollingInterval: RollingInterval.Day,
                               retainedFileCountLimit: 10, shared: true, flushToDiskInterval: TimeSpan.FromSeconds(2))
            : cfg.WriteTo.File(logPath, shared: true, flushToDiskInterval: TimeSpan.FromSeconds(2));
        Log.Logger = cfg.CreateLogger();
        Log.Information("OpenFPS Windows client starting (PID {Pid}, build {Build}). Log folder: {Dir}",
                        Environment.ProcessId, OpenFPS.Common.WireContract.Hash, LogDirectory);

        // FMOD's logging build (fmodL.dll) names API misuse by handle and thread. Armed before
        // System::create or not at all; a no-op unless OPENFPS_FMOD_DEBUG is set.
        string? fmodArmed = OpenFPS.Client.Core.AudioEngine.Fmod.FmodDebugLog.ArmFromEnvironment();
        if (fmodArmed != null) Log.Information("{Line}", fmodArmed);

        ProcessLifeLog.Install(StartedUtc);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error(e.Exception, "Exception on the UI thread.");

        // Audio diagnostic harness: isolate the renderer from the rest of the app.
        if (args.Contains("--audio-test"))
        {
            AudioDiagnostics.RunOrbitTest();
            Log.CloseAndFlush();
            return;
        }

        ApplicationConfiguration.Initialize();
        new ClientRunner(new AudioEngineFacade()).Run();
        Log.Information("Client shut down cleanly after {Sec:F0} s.", (DateTime.UtcNow - StartedUtc).TotalSeconds);
        Log.CloseAndFlush();
    }

    private static string ChooseLogDirectory()
    {
        foreach (string dir in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "logs"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openfps", "logs"),
                 })
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return Path.GetTempPath();
    }
}
