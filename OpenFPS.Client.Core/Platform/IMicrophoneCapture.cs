namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// Microphone capture for voice chat as Opus 20 ms frames, 48 kHz mono: NAudio in the Windows head,
/// <see cref="FmodMicrophoneCapture"/> on Linux. Without a device <see cref="NullMicrophoneCapture"/>
/// lets the session say so when the transmit key is pressed, rather than seem to transmit.
/// </summary>
public interface IMicrophoneCapture : IDisposable
{
    /// <summary>False when this platform has no capture backend; the session explains that aloud.</summary>
    bool IsAvailable { get; }

    bool IsCapturing { get; }

    /// <summary>Raised on the capture thread with one encoded Opus packet. Handlers must be thread-safe.</summary>
    event Action<byte[]>? PacketReady;

    /// <summary>Raised on the capture thread with what the microphone heard, 48 kHz mono, before it is
    /// encoded: what the player's own room answers with (OwnVoiceRing). Handlers must be thread-safe.</summary>
    event Action<float[]>? SamplesCaptured;

    /// <summary>Begins capturing. A no-op when unavailable or already capturing.</summary>
    void Start();

    /// <summary>Stops capturing and releases the device.</summary>
    void Stop();

    /// <summary>What to say when the player asks for voice and <see cref="IsAvailable"/> is false.</summary>
    string UnavailableReason { get; }
}

/// <summary>A capture device that isn't there. Says so instead of pretending.</summary>
public sealed class NullMicrophoneCapture : IMicrophoneCapture
{
    public NullMicrophoneCapture(string reason = "Voice chat is not available on this platform yet.")
        => UnavailableReason = reason;

    public bool IsAvailable => false;
    public bool IsCapturing => false;
    public string UnavailableReason { get; }

#pragma warning disable CS0067 // Never raised: that is the point of this implementation.
    public event Action<byte[]>? PacketReady;
    public event Action<float[]>? SamplesCaptured;
#pragma warning restore CS0067

    public void Start() { }
    public void Stop() { }
    public void Dispose() { }
}
