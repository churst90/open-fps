using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Client.Services;
using OpenFPS.Client.UI;

namespace OpenFPS.Client.Core;

/// <summary>
/// The Windows head: speech, the WinForms windows behind <see cref="IClientShell"/>, microphone
/// capture, the virtual-key map, and the game thread. Everything else — netcode, prediction,
/// acoustics, bindings, every spoken announcement — is <see cref="ClientGameSession"/> in
/// OpenFPS.Client.Core, shared verbatim with the GTK head. This file is the GTK head's Program.cs
/// said in WinForms, and the two should be kept saying the same thing.
/// </summary>
public class ClientRunner
{
    private readonly NvdaSpeechOutput _speech = new();
    private readonly ClientNetworkService _network = new();
    private readonly AudioEngineFacade _audio;
    private ClientSettings _settings = new();
    private VoiceCapture _microphone = null!;

    private ClientGameSession _session = null!;
    private ClientNavigationService _navigation = null!;

    private volatile bool _isRunning = true;

    /// <summary>The game loop's last tick in UTC ticks; zero until it has ticked once. See <see cref="Watchdog"/>.</summary>
    private long _loopBeat;

    public ClientRunner(AudioEngineFacade audio) => _audio = audio;

    public void Run()
    {
        _speech.Initialize();

        // FMOD (sound at all) AND phonon (HRTF). Without phonon the game still makes noise, which is
        // the dangerous case: it seems to work while the spatial information it is played on is gone.
        var missing = NativeAudioLibraries.FindMissing();
        if (missing.Count > 0)
        {
            string report = NativeAudioLibraries.DescribeMissing(missing);
            string message = "OpenFPS cannot start. " + report +
                             " Extract the whole zip again, keeping every file next to OpenFPS.Client.exe.";
            Serilog.Log.Error("Startup aborted. {Report}", report);
            _speech.Speak(message, interrupt: true);
            MessageBox.Show(message, "Missing files", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        DiagnosticSwitches.LogSet();

        _settings = ClientSettings.Load();
        if (!Loudness.CompressionFromEnvironment)
            Loudness.DynamicRangeCompression = _settings.LevelCompression;
        _microphone = new VoiceCapture(() => _settings.InputDevice);

        // Connecting, logging in, reconnecting and logging out are the session's (shared with Linux);
        // the head only shows what it is told.
        _network.OnMessageReceived += msg => _session.HandleMessage(msg);

        // Built on this (the UI) thread, before Application.Run: it owns the marshal every other
        // thread posts through.
        _navigation = new ClientNavigationService(
            menuFactory: () => new MenuWindow(_speech, _settings, new MenuServices
            {
                Connect = (address, user, pass, register) => _session.Connect(address, user, pass, register),
                Cue = cue => _session?.Ui.Play(cue),
                OutputDevices = () => _session.Audio.OutputDevices(),
                InputDevices = () => _session.Audio.InputDevices(),
                // Saving in Settings may be going BACK to the default, which ApplyAudioSettings alone
                // (only acting on a named device) would not do.
                ApplySettings = () => { _session.Audio.SetOutputDevice(_settings.OutputDevice); ApplyAudioSettings(); },
            }),
            loadingFactory: () => new LoadingWindow(_speech),
            gameFactory: () => new MainWindow(_session.Input, _speech, cue => _session.Ui.Play(cue)));

        var shell = new WinFormsClientShell(_navigation, _speech, cue => _session?.Ui.Play(cue),
                                            quit: () => _navigation.EnqueueUIAction(Application.Exit));

        _session = new ClientGameSession(_network, _speech, shell, _audio, _microphone);
        _session.GameJoined += () => Serilog.Log.Information("Entered the world as entity {Id}.", _session.OwnEntityId);
        // The session speaks the outcome; the menu only moves the connect form out of the way, or puts
        // focus back where the player can correct the mistake.
        _session.LoginSucceeded += _ => _navigation.ReportLoginOutcome("", success: true);
        _session.LoginFailed += reason => _navigation.ReportLoginOutcome($"Login failed. {reason}", success: false);
        _session.ConnectFailed += reason => _navigation.ReportLoginOutcome(reason, success: false);
        // Preloading the sound library is the long pole; it runs on the session's audio thread and the
        // menu is usable meanwhile, as on Linux.
        _session.BeginAudioInit(ApplyAudioSettings);

        _network.Start();

        new Thread(GameLoop) { IsBackground = true, Name = "GameLoop" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "Watchdog", Priority = ThreadPriority.AboveNormal }.Start();

        _navigation.ShowMenu();
        Application.Run(_navigation);

        Serilog.Log.Information("The last window closed. Shutting down after {Sec:F0} s.",
                                (DateTime.UtcNow - Program.StartedUtc).TotalSeconds);
        _isRunning = false;
        _session.Dispose();
        _microphone.Dispose();
        _speech.Dispose();
    }

    /// <summary>Applies what the settings file says once the audio engine is running.</summary>
    private void ApplyAudioSettings()
    {
        _session.Ui.Enabled = _settings.UiSounds;
        _session.Ui.PresenceEnabled = _settings.PresenceSounds;
        _session.Ui.Volume = _settings.UiVolume;
        if (_settings.OutputDevice.Length > 0 && !_session.Audio.SetOutputDevice(_settings.OutputDevice))
            _speech.Speak($"The saved output device, {_settings.OutputDevice}, is not connected. Using the default.", false);
    }

    // ── Game / network loop (background thread) ─────────────────────────────────

    private void GameLoop()
    {
        var lastTime = DateTime.Now;
        double accumulator = 0.0;
        const double targetDt = PhysicsConstants.FixedDeltaTime;
        var lastReport = DateTime.Now;
        int iterations = 0;
        double worstMs = 0;

        while (_isRunning)
        {
            try
            {
                _network.Poll();
                // Reconnects, the world fade and anything a menu handed over: in or out of the world.
                _session.Tick();

                if (_session.IsInGame)
                {
                    var now = DateTime.Now;
                    double elapsed = (now - lastTime).TotalSeconds;
                    lastTime = now;
                    if (elapsed > PhysicsConstants.MaxCatchUpSeconds) elapsed = PhysicsConstants.MaxCatchUpSeconds;
                    accumulator += elapsed;

                    while (accumulator >= targetDt)
                    {
                        _session.SimStep((float)targetDt);
                        accumulator -= targetDt;
                    }
                    _session.ContinuousUpdate();

                    // Every moving sound is placed once per iteration, so this rate is how often a car's
                    // engine moves. Reported beside the audio system's own figure.
                    iterations++;
                    Volatile.Write(ref _loopBeat, DateTime.UtcNow.Ticks);
                    double loopMs = (DateTime.Now - now).TotalMilliseconds;
                    if (loopMs > worstMs) worstMs = loopMs;
                    if ((now - lastReport).TotalSeconds >= 5.0)
                    {
                        double hz = iterations / (now - lastReport).TotalSeconds;
                        if (hz < 45 || worstMs > 100)
                            Serilog.Log.Warning("Game loop: {Hz:F0} Hz, worst iteration {Worst:F0} ms.", hz, worstMs);
                        else
                            Serilog.Log.Information("Game loop: {Hz:F0} Hz, worst iteration {Worst:F0} ms.", hz, worstMs);
                        lastReport = now; iterations = 0; worstMs = 0;
                    }
                }
                else
                {
                    lastTime = DateTime.Now;
                    accumulator = 0; // don't bank elapsed time while not simulating
                }

                PerfProbe.ReportIfDue(TimeSpan.FromSeconds(30), line => Serilog.Log.Information("{Perf}", line));
            }
            catch (Exception ex)
            {
                // A handler or simulation exception must never silently kill the loop that pumps the
                // network: that would freeze the world-load handshake with no diagnostic at all.
                Serilog.Log.Error(ex, "GameLoop iteration failed.");
            }

            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// Notices a HANG — everything stops and the log ends mid-stream, which from the chair is the same
    /// as a crash. Writes a dump of this process (every thread's stack) next to the log and keeps
    /// running. Armed only once the loop has ticked: it does not tick at the menu.
    /// </summary>
    private void Watchdog()
    {
        const int StallSeconds = 8;
        bool dumped = false;
        while (_isRunning)
        {
            Thread.Sleep(1000);
            long beat = Volatile.Read(ref _loopBeat);
            if (beat == 0) continue;
            double since = (DateTime.UtcNow - new DateTime(beat, DateTimeKind.Utc)).TotalSeconds;
            if (since < StallSeconds) { dumped = false; continue; }
            if (dumped) continue;
            dumped = true;

            Serilog.Log.Fatal("GAME LOOP STALLED for {Sec:F0} s — the client is hung, not crashed. Writing a dump.", since);
            try
            {
                string path = Path.Combine(Program.LogDirectory, $"openfps-hang.{Environment.ProcessId}.dmp");
                using var proc = Process.GetCurrentProcess();
                using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                bool ok = MiniDumpWriteDump(proc.Handle, (uint)proc.Id, file.SafeFileHandle, DumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Serilog.Log.Fatal(ok ? "Hang dump written to {Path}. Read it with: dotnet-dump analyze <file>"
                                     : "Hang dump FAILED (error {Err}) at {Path}.", ok ? path : Marshal.GetLastWin32Error(), path);
            }
            catch (Exception ex) { Serilog.Log.Error(ex, "Could not write a hang dump."); }
        }
    }

    // Enough for dotnet-dump to read managed stacks and the heap, without writing every mapped image.
    private const uint DumpType = 0x00000004 /* WithHandleData */ | 0x00000200 /* WithPrivateReadWriteMemory */
                                | 0x00000800 /* WithFullMemoryInfo */ | 0x00001000 /* WithThreadInfo */
                                | 0x00000020 /* WithUnloadedModules */;

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, SafeFileHandle hFile, uint dumpType,
                                                 IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
}
