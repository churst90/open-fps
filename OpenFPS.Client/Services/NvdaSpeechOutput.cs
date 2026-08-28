using System;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Services;

/// <summary>
/// Windows speech output: NVDA's controller client if NVDA is running, SAPI 5 otherwise.
///
/// This is the Windows implementation of <see cref="ISpeechOutput"/> — the same code that used to be
/// <c>TolkService</c>, now behind the interface the shared session speaks through. It is kept in
/// preference to <see cref="TolkSpeechOutput"/> (which covers JAWS, Window-Eyes, ZoomText and the
/// rest) for one practical reason: <c>nvdaControllerClient64.dll</c> ships in this repo's <c>lib/</c>
/// and <c>Tolk.dll</c> does not, so switching backends would trade a working screen reader for
/// silence. Ship Tolk.dll and the swap is a one-line change in ClientRunner.
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

    private bool _nvdaActive;
    private SpeechSynthesizer? _sapi;

    public string BackendName { get; private set; } = "none";

    public bool Initialize()
    {
        try
        {
            _nvdaActive = NvdaNative.nvdaController_testIfRunning() == 0;
        }
        catch
        {
            _nvdaActive = false;
        }

        if (_nvdaActive)
        {
            BackendName = "NVDA";
        }
        else
        {
            try
            {
                _sapi = new SpeechSynthesizer();
                _sapi.SetOutputToDefaultAudioDevice();
                BackendName = "SAPI 5";
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "No speech backend available: NVDA is not running and SAPI failed to start.");
                return false;
            }
        }

        Serilog.Log.Information("Speech output: {Backend}", BackendName);
        Speak("Accessibility bridge ready.", interrupt: true);
        return true;
    }

    public void Speak(string text, bool interrupt = true)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (interrupt) Interrupt();

        if (_nvdaActive) NvdaNative.nvdaController_speakText(text);
        else _sapi?.SpeakAsync(text);

        Console.WriteLine($"[TTS] {text}");
    }

    public void Interrupt()
    {
        if (_nvdaActive) NvdaNative.nvdaController_cancelSpeech();
        else _sapi?.SpeakAsyncCancelAll();
    }

    public void Dispose() => _sapi?.Dispose();
}
