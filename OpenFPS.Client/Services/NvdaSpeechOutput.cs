using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Services;

/// <summary>
/// Windows speech output: NVDA's controller client if NVDA is running, SAPI 5 otherwise.
///
/// Decided per line, not once: NVDA started (or restarted) after the game is heard through NVDA from
/// the next line on, and NVDA quitting falls back to SAPI instead of going silent. The check is an
/// RPC, so its answer is kept for two seconds.
///
/// Needs <c>nvdaControllerClient64.dll</c> next to the executable; the build copies it from lib/.
/// JAWS and Narrator users get SAPI; Tolk (<see cref="TolkSpeechOutput"/>) would reach them, and
/// needs Tolk.dll shipped.
/// </summary>
public sealed class NvdaSpeechOutput : ISpeechOutput
{
    private static class NvdaNative
    {
        [DllImport("nvdaControllerClient64.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvdaController_testIfRunning();

        [DllImport("nvdaControllerClient64.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int nvdaController_speakText(string text);

        [DllImport("nvdaControllerClient64.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvdaController_cancelSpeech();
    }

    private readonly object _gate = new();
    private bool _controllerMissing;
    private bool _nvda;
    private long _checkedAt;
    private SpeechSynthesizer? _sapi;

    public string BackendName => ScreenReaderRunning ? "NVDA" : "SAPI 5";

    /// <summary>
    /// True while NVDA is running. The menus use it: NVDA already announces every control that takes
    /// focus, so the game speaking the same name on top would interrupt it, or be interrupted by it.
    /// </summary>
    public bool ScreenReaderRunning
    {
        get
        {
            lock (_gate)
            {
                if (_controllerMissing) return false;
                long now = Environment.TickCount64;
                if (_checkedAt != 0 && now - _checkedAt < 2000) return _nvda;
                _checkedAt = now;
                try { _nvda = NvdaNative.nvdaController_testIfRunning() == 0; }
                catch (DllNotFoundException)
                {
                    _controllerMissing = true;
                    _nvda = false;
                    Serilog.Log.Warning("nvdaControllerClient64.dll is missing; speech goes through SAPI even when NVDA is running.");
                }
                catch (Exception ex) { _nvda = false; Serilog.Log.Debug(ex, "NVDA check failed."); }
                return _nvda;
            }
        }
    }

    public bool Initialize()
    {
        bool nvda = ScreenReaderRunning;
        Serilog.Log.Information("Speech output: {Backend}", nvda ? "NVDA" : "SAPI 5 (NVDA is not running)");
        return nvda || Sapi() != null;
    }

    private SpeechSynthesizer? Sapi()
    {
        if (_sapi != null) return _sapi;
        try
        {
            _sapi = new SpeechSynthesizer();
            _sapi.SetOutputToDefaultAudioDevice();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "SAPI failed to start: with NVDA not running there is no speech at all.");
            _sapi = null;
        }
        return _sapi;
    }

    public void Speak(string text, bool interrupt = true)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (ScreenReaderRunning)
        {
            if (interrupt) NvdaNative.nvdaController_cancelSpeech();
            NvdaNative.nvdaController_speakText(text);
            return;
        }
        var sapi = Sapi();
        if (sapi == null) return;
        if (interrupt) sapi.SpeakAsyncCancelAll();
        sapi.SpeakAsync(text);
    }

    public void Interrupt()
    {
        if (ScreenReaderRunning) NvdaNative.nvdaController_cancelSpeech();
        else _sapi?.SpeakAsyncCancelAll();
    }

    public void Dispose() => _sapi?.Dispose();
}
