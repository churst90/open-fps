using System;
using System.Runtime.InteropServices;
using Serilog;

namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// Linux speech output via speech-dispatcher (libspeechd) — the same TTS stack Orca drives
/// (espeak-ng, etc.). The Linux counterpart to <see cref="TolkSpeechOutput"/> on Windows.
/// </summary>
public sealed class SpeechDispatcherOutput : ISpeechOutput
{
    private const string Lib = "libspeechd.so.2";

    // SPDConnectionMode
    private const int SPD_MODE_SINGLE = 0;
    // SPDPriority (1=IMPORTANT .. 5=PROGRESS); game announcements use TEXT.
    private const int SPD_TEXT = 3;

    [DllImport(Lib)] private static extern IntPtr spd_open(string clientName, string connectionName, string? userName, int mode);
    [DllImport(Lib)] private static extern void spd_close(IntPtr connection);
    [DllImport(Lib)] private static extern int spd_say(IntPtr connection, int priority, string text);
    [DllImport(Lib)] private static extern int spd_cancel(IntPtr connection);

    private IntPtr _conn = IntPtr.Zero;

    public string BackendName => "speech-dispatcher";

    public bool Initialize()
    {
        try
        {
            _conn = spd_open("OpenFPS", "main", null, SPD_MODE_SINGLE);
            if (_conn == IntPtr.Zero)
            {
                Log.Warning("speech-dispatcher: spd_open returned null (is the daemon available?).");
                return false;
            }
            Log.Information("Speech output: speech-dispatcher connected.");
            return true;
        }
        catch (DllNotFoundException)
        {
            Log.Warning("speech-dispatcher: {Lib} not found; Linux speech unavailable.", Lib);
            return false;
        }
    }

    public void Speak(string text, bool interrupt = true)
    {
        if (_conn == IntPtr.Zero || string.IsNullOrEmpty(text)) return;
        if (interrupt) spd_cancel(_conn);
        spd_say(_conn, SPD_TEXT, text);
    }

    public void Interrupt()
    {
        if (_conn != IntPtr.Zero) spd_cancel(_conn);
    }

    public void Dispose()
    {
        if (_conn != IntPtr.Zero) { spd_close(_conn); _conn = IntPtr.Zero; }
    }
}
