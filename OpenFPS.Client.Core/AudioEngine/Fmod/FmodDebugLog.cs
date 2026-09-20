using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Turns on FMOD'S OWN logging, which is a drop-in library away and was not used for three sessions.
///
/// `libfmodL.so` ships next to `libfmod.so` in the SDK. It validates every call and NAMES what is
/// wrong — the handle that was stale, the object that was still connected, the thread it happened
/// on — where the ordinary build simply faults and leaves a core file to be read by hand.
///
/// Two things about it are easy to get wrong and both cost a run:
///
/// 1. **It must be armed BEFORE System::create.** After that it does nothing at all. So this is
///    called from a program's entry point, not from the audio provider.
/// 2. **FMOD's own FILE mode BUFFERS.** The first attempt at this produced a zero-byte log from a
///    run that ended in SIGSEGV, which is indistinguishable from FMOD having found nothing wrong.
///    Every line goes through the callback below and is flushed to disk immediately instead.
///
/// `RESULT.OK` back from Initialize means the logging build is loaded. `ERR_UNSUPPORTED` means the
/// ordinary one is, and nothing will be written — that is how to tell the swap worked.
/// </summary>
public static class FmodDebugLog
{
    private static FMOD.DEBUG_CALLBACK? _held;   // FMOD keeps the pointer; a collected delegate is a crash
    private static FileStream? _out;
    private static readonly object _gate = new();

    /// <summary>
    /// Arms FMOD debug logging if OPENFPS_FMOD_DEBUG is set. Returns a line describing what happened,
    /// or null if the variable was not set. "all" adds FMOD's own trace, which is enormous —
    /// eleven thousand lines in a forty-second run — and is what you want when a crash is seconds away.
    /// </summary>
    public static string? ArmFromEnvironment()
    {
        string? want = Environment.GetEnvironmentVariable("OPENFPS_FMOD_DEBUG");
        if (string.IsNullOrEmpty(want)) return null;

        var flags = FMOD.DEBUG_FLAGS.ERROR | FMOD.DEBUG_FLAGS.WARNING
                  | FMOD.DEBUG_FLAGS.DISPLAY_TIMESTAMPS | FMOD.DEBUG_FLAGS.DISPLAY_THREAD;
        if (want == "all") flags |= FMOD.DEBUG_FLAGS.LOG | FMOD.DEBUG_FLAGS.TYPE_TRACE;

        string path = Environment.GetEnvironmentVariable("OPENFPS_FMOD_DEBUG_FILE") ?? "/tmp/fmod-debug.log";
        _out = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite,
                              bufferSize: 1, FileOptions.WriteThrough);
        _held = Write;
        var r = FMOD.Debug.Initialize(flags, FMOD.DEBUG_MODE.CALLBACK, _held, null);
        return $"FMOD debug logging: {r} — {flags} to {path}. "
             + "ERR_UNSUPPORTED means the ordinary (non-logging) libfmod.so is loaded.";
    }

    /// <summary>
    /// A diagnostic that marshals strings and flushes a file from inside an FMOD thread — possibly
    /// the mixer thread — which is precisely what the rest of this codebase forbids. It is acceptable
    /// in this one place because the alternative is another session spent reading page-fault
    /// addresses out of core files, and because nothing arms it unless someone asked for it.
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
