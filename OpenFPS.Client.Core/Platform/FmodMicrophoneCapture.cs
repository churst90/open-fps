using System;
using System.Collections.Generic;
using System.Threading;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// Microphone capture through FMOD's own recording, for heads that have no other capture library
/// (Linux). Sends what the Windows head's NAudio capture sends (VoiceCodec): 48 kHz mono, 20 ms Opus
/// frames. The device records at its own rate; anything else is resampled to 48 kHz (SincResampler).
/// </summary>
public sealed class FmodMicrophoneCapture : IMicrophoneCapture
{
    private readonly AudioEngineFacade _audio;
    private readonly Func<string> _preferredDevice;
    private Thread? _thread;
    private volatile bool _capturing;

    public FmodMicrophoneCapture(AudioEngineFacade audio, Func<string>? preferredDevice = null)
    {
        _audio = audio;
        _preferredDevice = preferredDevice ?? (() => "");
    }

    /// <summary>Raised on the capture thread with each 20 ms Opus packet.</summary>
    public event Action<byte[]>? PacketReady;
    public event Action<float[]>? SamplesCaptured;

    public bool IsCapturing => _capturing;

    public bool IsAvailable => _audio.HasRecordingDevice;

    public string UnavailableReason => _audio.IsInitialized
        ? "No microphone was found, so voice chat is unavailable."
        : "The audio engine is not running, so voice chat is unavailable.";

    public void Start()
    {
        if (_capturing || !IsAvailable) return;
        // A device that exists can still refuse to open; failing loudly matters, or the player
        // would believe they were transmitting.
        if (!_audio.StartRecording(_preferredDevice(), out int rate) || rate <= 0)
        {
            Serilog.Log.Error("Microphone capture failed to start.");
            return;
        }
        _capturing = true;
        _thread = new Thread(() => Run(rate)) { IsBackground = true, Name = "Microphone" };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_capturing) return;
        _capturing = false;
        _thread?.Join(500);
        _thread = null;
        _audio.StopRecording();
    }

    private void Run(int deviceRate)
    {
        try
        {
            var encoder = new VoiceFrameEncoder();
            var resampler = deviceRate == VoiceCodec.Rate ? null : new SincResampler(deviceRate, VoiceCodec.Rate);
            var read = new List<float>(4096);
            var heard = new List<float>(4096);
            Action<byte[]> send = p => PacketReady?.Invoke(p);

            while (_capturing)
            {
                read.Clear();
                heard.Clear();
                _audio.ReadRecording(read);
                if (resampler != null) resampler.Process(read, heard);
                else heard.AddRange(read);
                if (heard.Count > 0)
                {
                    var samples = heard.ToArray();
                    encoder.Push(samples, send);
                    SamplesCaptured?.Invoke(samples);
                }
                Thread.Sleep(5);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Microphone capture stopped.");
            _capturing = false;
        }
    }

    public void Dispose() => Stop();
}
