using System;
using System.Collections.Generic;
using System.Threading;
using Concentus;
using Concentus.Enums;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// Microphone capture through FMOD's own recording, for heads that have no other capture library
/// (Linux). Sends what the Windows head's NAudio capture sends: 48 kHz mono, 20 ms Opus frames at
/// 24 kbit/s. The device records at its own rate; this resamples to 48 kHz.
/// </summary>
public sealed class FmodMicrophoneCapture : IMicrophoneCapture
{
    private const int OpusRate = 48000;
    private const int FrameSamples = OpusRate / 50;   // 20 ms

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
            var encoder = OpusCodecFactory.CreateEncoder(OpusRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = 24000;
            var resampler = new LinearResampler(deviceRate, OpusRate);
            var read = new List<float>(4096);
            var frame = new short[FrameSamples];
            int filled = 0;
            var packet = new byte[1275];
            var heard = new List<float>(1024);

            while (_capturing)
            {
                read.Clear();
                heard.Clear();
                _audio.ReadRecording(read);
                foreach (float s in resampler.Process(read))
                {
                    heard.Add(s);
                    frame[filled++] = (short)(Math.Clamp(s, -1f, 1f) * short.MaxValue);
                    if (filled < FrameSamples) continue;
                    filled = 0;
                    int len = encoder.Encode(frame.AsSpan(), FrameSamples, packet.AsSpan(), packet.Length);
                    if (len > 0) PacketReady?.Invoke(packet.AsSpan(0, len).ToArray());
                }
                if (heard.Count > 0) SamplesCaptured?.Invoke(heard.ToArray());
                Thread.Sleep(10);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Microphone capture stopped.");
            _capturing = false;
        }
    }

    public void Dispose() => Stop();

    /// <summary>Linear interpolation between rates, carrying its position across calls. Plenty for
    /// speech going to a 24 kbit/s codec.</summary>
    internal sealed class LinearResampler
    {
        private readonly double _step;
        private double _pos;          // position in the input, relative to the first sample of this call
        private float _last;          // the previous call's final sample, index -1
        private bool _primed;

        public LinearResampler(int fromRate, int toRate) => _step = (double)fromRate / toRate;

        public IEnumerable<float> Process(List<float> input)
        {
            if (input.Count == 0) yield break;
            if (!_primed) { _last = input[0]; _primed = true; }
            while (_pos < input.Count - 1)
            {
                int i = (int)Math.Floor(_pos);
                float a = i < 0 ? _last : input[i];
                float b = input[i + 1];
                yield return a + (b - a) * (float)(_pos - i);
                _pos += _step;
            }
            _pos -= input.Count;
            _last = input[^1];
        }
    }
}
