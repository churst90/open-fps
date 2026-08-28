using System;

namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// Microphone capture for voice chat, as Opus-encoded 20 ms frames (48 kHz mono).
///
/// Windows implements this over NAudio today; Linux has no implementation yet, which is exactly why
/// the seam exists — <see cref="NullMicrophoneCapture"/> reports itself unavailable so the session
/// can SAY so when the player presses the transmit key, rather than appearing to transmit into
/// nothing. See <c>docs/CROSS_PLATFORM_PLAN.md</c>: the intended convergence is FMOD's own
/// <c>recordStart</c>, which is cross-platform and would let both heads share one implementation.
/// </summary>
public interface IMicrophoneCapture : IDisposable
{
    /// <summary>False when this platform has no capture backend; the session explains that aloud.</summary>
    bool IsAvailable { get; }

    /// <summary>True between a successful <see cref="Start"/> and <see cref="Stop"/>.</summary>
    bool IsCapturing { get; }

    /// <summary>Raised on the capture thread with one encoded Opus packet. Handlers must be thread-safe.</summary>
    event Action<byte[]>? PacketReady;

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
#pragma warning restore CS0067

    public void Start() { }
    public void Stop() { }
    public void Dispose() { }
}
