using NAudio.Wave;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Services;

/// <summary>
/// Windows microphone capture (NAudio): 48 kHz mono 16-bit PCM, encoded as <see cref="VoiceCodec"/> says.
/// The Windows implementation of <see cref="IMicrophoneCapture"/>; Linux records through FMOD
/// (<see cref="FmodMicrophoneCapture"/>) and sends the same packets.
/// </summary>
public sealed class VoiceCapture : IMicrophoneCapture
{
    private const int Channels = 1;
    private const int BufferMs = 20;
    /// <summary>Buffers queued with the driver. NAudio's default is three, 60 ms of slack: a capture
    /// thread held up longer than that (a busy machine, a screen reader speaking) loses audio. More
    /// buffers add no delay, since each is handed back the moment it is full.</summary>
    private const int Buffers = 8;
    private long _samplesIn;
    private DateTime _startedAt;

    private WaveInEvent? _waveIn;
    private VoiceFrameEncoder? _encoder;
    private Action<byte[]>? _send;
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
            _encoder = new VoiceFrameEncoder();
            _send = p => PacketReady?.Invoke(p);

            _waveIn = new WaveInEvent
            {
                DeviceNumber = DeviceNumber(),
                WaveFormat = new WaveFormat(VoiceCodec.Rate, 16, Channels),
                BufferMilliseconds = BufferMs,
                NumberOfBuffers = Buffers,
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _samplesIn = 0;
            _startedAt = DateTime.UtcNow;
            _waveIn.StartRecording();
            _capturing = true;
            Serilog.Log.Information("Microphone: recording from {Device}.", DeviceName(_waveIn.DeviceNumber));
        }
        catch (Exception ex)
        {
            // Clean up directly rather than through Stop(), which early-returns while _capturing is
            // false — which it is on every path that lands here.
            Serilog.Log.Error(ex, "Microphone capture failed to start.");
            _capturing = false;
            if (_waveIn != null) { _waveIn.DataAvailable -= OnDataAvailable; _waveIn.RecordingStopped -= OnRecordingStopped; _waveIn.Dispose(); _waveIn = null; }
            _encoder = null;
        }
    }

    public void Stop()
    {
        if (!_capturing) return;
        _capturing = false;
        Serilog.Log.Information("Microphone: stopped after {Seconds:F1} s, {Heard:F1} s of audio heard.",
                                (DateTime.UtcNow - _startedAt).TotalSeconds, _samplesIn / (double)VoiceCodec.Rate);
        if (_waveIn != null) { _waveIn.DataAvailable -= OnDataAvailable; _waveIn.RecordingStopped -= OnRecordingStopped; }
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
        _encoder = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_capturing || _encoder == null || _send == null || e.BytesRecorded < 2) return;
        var heard = new float[e.BytesRecorded / 2];
        if (_samplesIn == 0)
            Serilog.Log.Information("Microphone: first audio {Ms:F0} ms after starting.", (DateTime.UtcNow - _startedAt).TotalMilliseconds);
        _samplesIn += heard.Length;
        for (int i = 0; i < heard.Length; i++) heard[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        _encoder.Push(heard, _send);
        // What was heard, as it was heard, for the player's own room to answer (OwnVoiceRing).
        SamplesCaptured?.Invoke(heard);
    }

    /// <summary>
    /// The device stopped on its own: unplugged, taken by another program, or a driver fault. Without
    /// this the player hears nothing wrong and believes they are still talking. Logged, and one attempt
    /// made to start again on whatever device is there now (Start logs if that fails too).
    /// </summary>
    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (!_capturing) return;
        Serilog.Log.Warning(e.Exception, "Microphone: recording stopped by itself after {Heard:F1} s of audio; starting again.",
                            _samplesIn / (double)VoiceCodec.Rate);
        _capturing = false;
        if (_waveIn != null) { _waveIn.DataAvailable -= OnDataAvailable; _waveIn.RecordingStopped -= OnRecordingStopped; _waveIn.Dispose(); _waveIn = null; }
        _encoder = null;
        Start();
    }

    private static string DeviceName(int number)
    {
        try { return number < 0 ? "the default microphone" : WaveInEvent.GetCapabilities(number).ProductName; }
        catch { return $"device {number}"; }
    }

    public void Dispose() => Stop();
}
