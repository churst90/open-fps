using System.Collections.Concurrent;
using Concentus;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Another player's voice off the network, decoded in order into a ring that one voice in the world
/// reads (an <see cref="OwnVoiceTap"/> at their mouth). Packets come in bursts, out of order, late or
/// not at all, so:
/// <list type="bullet">
/// <item>Frames are held by number and decoded in order. One that comes early waits for the missing
/// one as long as the buffer allows; then the missing one is rebuilt from the next frame's forward error
/// correction or concealed by the decoder, and if it turns up later it is dropped. Past five missing it
/// is a gap and nothing is made up.</item>
/// <item>The reader stays a margin (the jitter buffer) behind the newest sample: the spread in how late
/// frames have been over the last few seconds plus room for one lost frame, 70 ms to half a second, and
/// longer for a while each time the reader still ran dry.</item>
/// <item>A pause in talking starts a new run, played from its first word once a margin of it is in.</item>
/// </list>
/// Packets arrive on the network thread, <see cref="Pump"/> runs on the game thread, the ring is read on
/// the mixer thread.
/// </summary>
public sealed class TalkerStream
{
    /// <summary>Longer than this between packets is a pause in talking, not jitter.</summary>
    public const double SpurtGapSeconds = 0.3;
    /// <summary>The least margin: on a perfect connection, still room for one lost frame (the wait for it,
    /// it, and the frame after) and 10 ms.</summary>
    public const double MinMarginSeconds = 0.07, MaxMarginSeconds = 0.5;
    /// <summary>How far back the jitter is measured.</summary>
    public const double WindowSeconds = 8;
    public const int MaxConcealedFrames = 5;
    /// <summary>
    /// The most a voice is sped up or slowed to keep to the margin: 0.2 %, three cents, below what an ear
    /// hears in a voice. Enough to follow two clocks' drift and let an unneeded margin shrink; a margin
    /// grows by the reader waiting (a pause, not a pitch change).
    /// </summary>
    public const double MaxPull = 0.002;
    private const double FrameSeconds = (double)VoiceCodec.FrameSamples / VoiceCodec.Rate;

    public readonly int SenderId;
    public readonly OwnVoiceRing Ring = new() { MarginSeconds = MinMarginSeconds };
    private readonly object _lock = new();
    private readonly IOpusDecoder _decoder = VoiceCodec.CreateDecoder();
    private readonly float[] _pcm = new float[VoiceCodec.MaxFrameSamples];
    private readonly Queue<(double At, double Offset)> _offsets = new();
    /// <summary>Frames waiting for the ones before them, by their place in this run of talking.</summary>
    private readonly SortedDictionary<long, (byte[] Data, double At)> _held = new();
    private long _nextIndex, _lastIndex;
    private ushort _lastSequence;
    private bool _numbered;
    private long _media;
    private double _lastArrival = double.NegativeInfinity;
    private double _floor;
    private int _seenUnderruns;

    /// <summary>Counts for the log. <see cref="RanDry"/> is the reader catching up mid-run (the margin
    /// was too short); the ring's own underrun count also has the end of every sentence in it.</summary>
    public int Received, Lost, Rebuilt, Late, Corrupt, RanDry;

    /// <summary>When the last packet came, seconds on the clock <see cref="Receive"/> was given.</summary>
    public double LastArrival { get { lock (_lock) return _lastArrival; } }

    public TalkerStream(int senderId) => SenderId = senderId;

    /// <param name="sequence">The sender's frame number; zero for a sender that does not number them.</param>
    /// <param name="packet">One Opus frame.</param>
    /// <param name="now">Seconds on any steady clock; the same one <see cref="Pump"/> is given.</param>
    public void Receive(ushort sequence, byte[] packet, double now)
    {
        if (packet.Length == 0) return;
        lock (_lock)
        {
            Received++;
            bool newSpurt = now - _lastArrival > SpurtGapSeconds;
            if (newSpurt)
            {
                Flush(now, force: true);
                _offsets.Clear();
                _seenUnderruns = Ring.Underruns;
                Ring.BeginSpurt();
            }
            double offset;
            if (sequence == 0)
            {
                // Not numbered (an older client): played as it comes.
                Flush(now, force: true);
                _numbered = false;
                Decode(packet);
                offset = now - (double)_media / VoiceCodec.Rate;
            }
            else
            {
                long index;
                if (newSpurt || !_numbered) { index = _nextIndex = _lastIndex = 0; _numbered = true; }
                else index = _lastIndex + Distance(_lastSequence, sequence);
                if (index > _lastIndex || index == 0) { _lastIndex = index; _lastSequence = sequence; }
                if (index < _nextIndex || _held.ContainsKey(index)) { Late++; return; }
                _held[index] = (packet, now);
                offset = now - index * FrameSeconds;
                Flush(now, force: false);
            }

            // The jitter: how late each frame came against its place in the run, the earliest as on time.
            _offsets.Enqueue((now, offset));
            while (_offsets.Count > 0 && now - _offsets.Peek().At > WindowSeconds) _offsets.Dequeue();
            double min = double.MaxValue, max = double.MinValue;
            foreach (var o in _offsets) { min = Math.Min(min, o.Offset); max = Math.Max(max, o.Offset); }
            _spread = max - min;
            // The spread, the wait for a missing frame, the missing frame itself and the one after it.
            double needed = _spread + HoldSeconds + 2 * FrameSeconds + 0.01;

            // Ran dry while they were still talking: the measure was too short for this connection.
            int underruns = Ring.Underruns;
            if (!newSpurt)
            {
                if (underruns != _seenUnderruns)
                {
                    RanDry++;
                    _floor = Math.Min(MaxMarginSeconds, Ring.MarginSeconds + 0.02);
                }
                else _floor = Math.Max(0, _floor - (now - _lastArrival) * 0.002);   // 2 ms a second
            }
            _seenUnderruns = underruns;
            Ring.MarginSeconds = Math.Clamp(Math.Max(needed, _floor), MinMarginSeconds, MaxMarginSeconds);
            _lastArrival = now;
        }
    }

    /// <summary>Game thread: gives up on a frame that has not come in time, so the ones after it play.</summary>
    public void Pump(double now)
    {
        lock (_lock) Flush(now, force: false);
    }

    /// <summary>
    /// How long a frame waits for a missing one before it: as long as frames have been arriving out of
    /// step, at least a frame. The margin includes it, so the decision is made before the reader gets there.
    /// </summary>
    private double HoldSeconds => Math.Max(FrameSeconds, _spread);
    private double _spread;

    private void Flush(double now, bool force)
    {
        while (_held.Count > 0)
        {
            var (index, (data, at)) = First();
            long missing = index - _nextIndex;
            if (missing > 0)
            {
                if (!force && now - at < HoldSeconds) return;   // the missing one may still come
                Lost += (int)missing;
                if (missing <= MaxConcealedFrames)
                {
                    try
                    {
                        for (long k = 0; k < missing - 1; k++)
                            Write(_decoder.Decode(ReadOnlySpan<byte>.Empty, _pcm, VoiceCodec.FrameSamples, false));
                        Write(_decoder.Decode(data, _pcm, VoiceCodec.FrameSamples, true));
                        Rebuilt++;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { Corrupt++; }
                }
            }
            _held.Remove(index);
            Decode(data);
            _nextIndex = index + 1;
        }
    }

    private KeyValuePair<long, (byte[] Data, double At)> First()
    {
        foreach (var kv in _held) return kv;
        throw new InvalidOperationException();
    }

    private void Decode(byte[] packet)
    {
        try { Write(_decoder.Decode(packet, _pcm, VoiceCodec.MaxFrameSamples, false)); }
        // A packet the decoder will not take. Somebody else's bytes must not stop this voice.
        catch (Exception ex) when (ex is not OutOfMemoryException) { Corrupt++; }
    }

    private void Write(int samples)
    {
        if (samples <= 0) return;
        Ring.Write(_pcm.AsSpan(0, samples));
        _media += samples;
    }

    /// <summary>Frames from one number to the next, either way. Senders count 1 to 65535 and skip zero.</summary>
    private static long Distance(ushort from, ushort to)
    {
        int d = to - from;
        if (d > 32767) d -= 65535;
        else if (d < -32767) d += 65535;
        return d;
    }
}

/// <summary>Everybody whose voice this client has heard, by entity id.</summary>
public static class Talkers
{
    /// <summary>The emitter key that names a talker's voice: <c>talker:42</c>.</summary>
    public const string KeyPrefix = "talker:";

    private static readonly ConcurrentDictionary<int, TalkerStream> _all = new();

    public static TalkerStream For(int senderId) => _all.GetOrAdd(senderId, id => new TalkerStream(id));
    public static bool TryGet(int senderId, out TalkerStream stream) => _all.TryGetValue(senderId, out stream!);
    public static void Remove(int senderId) => _all.TryRemove(senderId, out _);
    public static void Clear() => _all.Clear();
    public static ICollection<TalkerStream> All => _all.Values;

    public static string Key(int senderId) => KeyPrefix + senderId;

    public static bool TryParseKey(string? key, out int senderId)
    {
        senderId = 0;
        return key != null && key.StartsWith(KeyPrefix, StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(KeyPrefix.Length), out senderId);
    }

    /// <summary>
    /// The emitter key of one surface answering a talker, <c>talkercopy:42</c>: their voice read back at
    /// the copy's extra delay, as your room answers your own (FmodAudioProvider.OwnVoiceCopyKey). Not a
    /// <see cref="Key"/>: a copy reads behind the voice, so running dry is the voice's to report.
    /// </summary>
    public const string CopyKeyPrefix = "talkercopy:";

    public static string CopyKey(int senderId) => CopyKeyPrefix + senderId;

    public static bool TryParseCopyKey(string? key, out int senderId)
    {
        senderId = 0;
        return key != null && key.StartsWith(CopyKeyPrefix, StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(CopyKeyPrefix.Length), out senderId);
    }
}
