using System.Runtime.InteropServices;
using System.Text;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// FMOD's own logging, from its validating build (`libfmodL.so`), which names the stale handle or the
/// still-connected unit where the ordinary build only faults. Armed before System::create or it does
/// nothing, so from a program's entry point; written through the callback, flushed per line, because
/// FMOD's file mode buffers and left a zero-byte log from a run that ended in SIGSEGV. See
/// docs/THE_MIXER_THREAD_CRASH.md, "FMOD ships a validating build".
/// </summary>
public static class FmodDebugLog
{
    private static FMOD.DEBUG_CALLBACK? _held;   // FMOD keeps the pointer; a collected delegate is a crash
    private static FileStream? _out;
    private static readonly object _gate = new();

    /// <summary>
    /// Arms FMOD debug logging if OPENFPS_FMOD_DEBUG is set, and says what happened (null if not set).
    /// "all" adds FMOD's trace: eleven thousand lines in a forty-second run, for a crash seconds away.
    /// RESULT.OK means the logging build is loaded; ERR_UNSUPPORTED means the ordinary one is.
    /// </summary>
    public static string? ArmFromEnvironment()
    {
        string? want = Environment.GetEnvironmentVariable("OPENFPS_FMOD_DEBUG");
        if (string.IsNullOrEmpty(want)) return null;

        var flags = FMOD.DEBUG_FLAGS.ERROR | FMOD.DEBUG_FLAGS.WARNING
                  | FMOD.DEBUG_FLAGS.DISPLAY_TIMESTAMPS | FMOD.DEBUG_FLAGS.DISPLAY_THREAD;
        if (want == "all") flags |= FMOD.DEBUG_FLAGS.LOG | FMOD.DEBUG_FLAGS.TYPE_TRACE;

        string path = Environment.GetEnvironmentVariable("OPENFPS_FMOD_DEBUG_FILE") ?? Path.Combine(Path.GetTempPath(), "fmod-debug.log");
        _out = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite,
                              bufferSize: 1, FileOptions.WriteThrough);
        _held = Write;
        var r = FMOD.Debug.Initialize(flags, FMOD.DEBUG_MODE.CALLBACK, _held, null);
        return $"FMOD debug logging: {r} — {flags} to {path}. "
             + "ERR_UNSUPPORTED means the ordinary (non-logging) libfmod.so is loaded.";
    }

    /// <summary>
    /// Marshals strings and flushes a file on an FMOD thread, possibly the mixer's, which everything
    /// else here forbids: accepted for a diagnostic that nothing arms unless asked.
    /// </summary>
    private static FMOD.RESULT Write(FMOD.DEBUG_FLAGS flags, IntPtr file, int line, IntPtr func, IntPtr message)
    {
        try
        {
            string f = Marshal.PtrToStringAnsi(file) ?? "";
            string fn = Marshal.PtrToStringAnsi(func) ?? "";
            string m = (Marshal.PtrToStringAnsi(message) ?? "").TrimEnd();
            byte[] bytes = Encoding.UTF8.GetBytes($"[{flags}] {f}:{line} {fn} {m}\n");
            lock (_gate) { _out?.Write(bytes, 0, bytes.Length); _out?.Flush(true); }
        }
        catch { /* a diagnostic that throws inside FMOD would be worse than a missing line */ }
        return FMOD.RESULT.OK;
    }
}
