using System;
using Concentus;
using Concentus.Enums;
using NAudio.Wave;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Services;

/// <summary>
/// Windows microphone capture (NAudio): 48 kHz mono 16-bit PCM encoded to Opus at 20 ms frames.
/// The Windows implementation of <see cref="IMicrophoneCapture"/>; Linux records through FMOD
/// (<see cref="FmodMicrophoneCapture"/>) and sends the same packets.
/// </summary>
public sealed class VoiceCapture : IMicrophoneCapture
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameSizeMs = 20;
    private const int FrameSizeSamples = SampleRate * FrameSizeMs / 1000; // 960 samples
    private const int FrameSizeBytes = FrameSizeSamples * sizeof(short) * Channels;

    private WaveInEvent? _waveIn;
    private IOpusEncoder? _encoder;
    private readonly byte[] _frameBuffer = new byte[FrameSizeBytes];
    private int _framePos;
    private bool _capturing;
    private readonly Func<string> _preferredDevice;

    /// <param name="preferredDevice">The microphone chosen in Settings, by the name FMOD lists it
    /// under; empty for the system default.</param>
    public VoiceCapture(Func<string>? preferredDevice = null) => _preferredDevice = preferredDevice ?? (() => "");

    /// <summary>
    /// The NAudio device number for the chosen microphone, or -1 (the default device, WAVE_MAPPER).
    /// Settings lists devices by FMOD's names, and the old waveIn API truncates a name to 31
    /// characters, so a device matches when one name begins with the other.
    /// </summary>
    private int DeviceNumber()
    {
        string want = _preferredDevice();
        if (want.Length == 0) return -1;
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            string name = WaveInEvent.GetCapabilities(i).ProductName;
            if (name.Length > 0 && (want.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                                    || name.StartsWith(want, StringComparison.OrdinalIgnoreCase)))
                return i;
        }
        Serilog.Log.Warning("The chosen microphone, {Name}, is not connected; using the default.", want);
        return -1;
    }

    /// <summary>
    /// Raised on the NAudio capture thread each time a complete 20ms Opus packet is ready.
    /// The callback must be thread-safe.
    /// </summary>
    public event Action<byte[]>? PacketReady;
    public event Action<float[]>? SamplesCaptured;

    public bool IsCapturing => _capturing;

    /// <summary>True when a recording device is present. Asked before every transmit, because a
    /// machine with no microphone must be told so rather than appear to transmit.</summary>
    public bool IsAvailable
    {
        get
        {
            try { return WaveInEvent.DeviceCount > 0; }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Could not enumerate recording devices."); return false; }
        }
    }

    public string UnavailableReason => "No microphone was found, so voice chat is unavailable.";

    public void Start()
    {
        if (_capturing || !IsAvailable) return;

        // A device that exists can still refuse to open (in use, driver fault). Failing loudly here
        // matters more than most: the player would otherwise believe they were transmitting.
        try
        {
            _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
            _encoder.Bitrate = 24000;
            _framePos = 0;

            _waveIn = new WaveInEvent
            {
                DeviceNumber = DeviceNumber(),
                WaveFormat = new WaveFormat(SampleRate, 16, Channels),
                BufferMilliseconds = FrameSizeMs
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
            _capturing = true;
        }
        catch (Exception ex)
        {
            // Clean up directly rather than through Stop(), which early-returns while _capturing is
            // false — which it is on every path that lands here.
            Serilog.Log.Error(ex, "Microphone capture failed to start.");
            _capturing = false;
            if (_waveIn != null) { _waveIn.DataAvailable -= OnDataAvailable; _waveIn.Dispose(); _waveIn = null; }
            _encoder = null;
            _framePos = 0;
        }
    }

    public void Stop()
    {
        if (!_capturing) return;
        _capturing = false;
        if (_waveIn != null) _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
        _encoder = null;
        _framePos = 0;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_capturing || _encoder == null) return;
        // What was heard, as it was heard, for the player's own room to answer (OwnVoiceRing).
        if (SamplesCaptured != null && e.BytesRecorded >= 2)
        {
            var heard = new float[e.BytesRecorded / 2];
            for (int i = 0; i < heard.Length; i++) heard[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            SamplesCaptured(heard);
        }

        int offset = 0;
        while (offset < e.BytesRecorded)
        {
            int toCopy = Math.Min(FrameSizeBytes - _framePos, e.BytesRecorded - offset);
            Buffer.BlockCopy(e.Buffer, offset, _frameBuffer, _framePos, toCopy);
            _framePos += toCopy;
            offset += toCopy;

            if (_framePos < FrameSizeBytes) continue;

            // Full frame ready — encode to Opus (Concentus 2.x Span-based API)
            var pcmShort = new short[FrameSizeSamples];
            Buffer.BlockCopy(_frameBuffer, 0, pcmShort, 0, FrameSizeBytes);

            var opusOut = new byte[1275]; // max Opus packet size
            int encodedLen = _encoder.Encode(
                pcmShort.AsSpan(),
                FrameSizeSamples,
                opusOut.AsSpan(),
                opusOut.Length);

            if (encodedLen > 0)
            {
                var packet = new byte[encodedLen];
                Buffer.BlockCopy(opusOut, 0, packet, 0, encodedLen);
                PacketReady?.Invoke(packet);
            }

            _framePos = 0;
        }
    }

    public void Dispose() => Stop();
}
