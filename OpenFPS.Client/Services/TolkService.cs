using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System;

namespace OpenFPS.Client.Services;

public class TolkService : IDisposable
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

    private bool _nvdaActive = false;
    private SpeechSynthesizer? _sapi;

    public void Initialize()
    {
        try {
            _nvdaActive = NvdaNative.nvdaController_testIfRunning() == 0;
        } catch {
            _nvdaActive = false;
        }

        if (!_nvdaActive)
        {
            _sapi = new SpeechSynthesizer();
            _sapi.SetOutputToDefaultAudioDevice();
        }
        
        Speak("Accessibility bridge restored.");
    }

    public void Speak(string text, bool interrupt = false)
    {
        if (interrupt)
        {
            if (_nvdaActive) NvdaNative.nvdaController_cancelSpeech();
            else _sapi?.SpeakAsyncCancelAll();
        }

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
