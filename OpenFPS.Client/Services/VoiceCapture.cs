using System;
using System.Collections.Concurrent;
using Concentus;
using Concentus.Enums;
using NAudio.Wave;

namespace OpenFPS.Client.Services;

/// <summary>
/// Responsibility: Captures microphone audio, encodes it to Opus frames,
/// and raises them via OnPacketReady for network transmission.
/// Uses 48kHz mono 16-bit PCM → Opus at 20ms frame intervals.
/// </summary>
public sealed class VoiceCapture : IDisposable
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

    /// <summary>
    /// Raised on the NAudio capture thread each time a complete 20ms Opus packet is ready.
    /// The callback must be thread-safe.
    /// </summary>
    public event Action<byte[]>? OnPacketReady;

    public bool IsCapturing => _capturing;

    public void StartCapture()
    {
        if (_capturing) return;

        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 24000;
        _framePos = 0;

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = FrameSizeMs
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
        _capturing = true;
    }

    public void StopCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
        _encoder = null;
        _framePos = 0;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_capturing || _encoder == null) return;

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
                OnPacketReady?.Invoke(packet);
            }

            _framePos = 0;
        }
    }

    public void Dispose() => StopCapture();
}
