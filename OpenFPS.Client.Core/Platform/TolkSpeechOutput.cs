using System;
using System.Runtime.InteropServices;
using Serilog;

namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// Windows speech output via Tolk (https://github.com/dkager/tolk). Tolk abstracts the installed
/// Windows screen reader — NVDA, JAWS, Window-Eyes, System Access, ZoomText, SuperNova — with a
/// SAPI 5 fallback, replacing the hand-rolled NVDA+SAPI path.
///
/// Requires Tolk.dll plus its screen-reader client DLLs (nvdaControllerClient*.dll, SAAPI32.dll,
/// ...) next to the executable. Tolk is Windows-only; on Linux use <see cref="SpeechDispatcherOutput"/>.
/// The P/Invoke below compiles on any platform (only primitives + strings) — it simply won't
/// resolve Tolk.dll at runtime off Windows, which is fine because the Linux head never selects it.
/// </summary>
public sealed class TolkSpeechOutput : ISpeechOutput
{
    private const string Lib = "Tolk.dll";

    [DllImport(Lib, CharSet = CharSet.Unicode)] private static extern void Tolk_Load();
    [DllImport(Lib, CharSet = CharSet.Unicode)] private static extern void Tolk_Unload();
    [DllImport(Lib, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool Tolk_Output(string str, [MarshalAs(UnmanagedType.I1)] bool interrupt);
    [DllImport(Lib, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool Tolk_Silence();
    [DllImport(Lib, CharSet = CharSet.Unicode)] private static extern IntPtr Tolk_DetectScreenReader();

    private bool _loaded;

    public string BackendName { get; private set; } = "Tolk";

    public bool Initialize()
    {
        try
        {
            Tolk_Load();
            _loaded = true;
            IntPtr namePtr = Tolk_DetectScreenReader();
            if (namePtr != IntPtr.Zero)
                BackendName = "Tolk: " + (Marshal.PtrToStringUni(namePtr) ?? "unknown");
            Log.Information("Speech output: {Backend}", BackendName);
            return true;
        }
        catch (DllNotFoundException)
        {
            Log.Warning("Tolk.dll not found; Windows speech unavailable.");
            return false;
        }
    }

    public void Speak(string text, bool interrupt = true)
    {
        if (!_loaded || string.IsNullOrEmpty(text)) return;
        Tolk_Output(text, interrupt);
    }

    public void Interrupt()
    {
        if (_loaded) Tolk_Silence();
    }

    public void Dispose()
    {
        if (_loaded) { Tolk_Unload(); _loaded = false; }
    }
}
