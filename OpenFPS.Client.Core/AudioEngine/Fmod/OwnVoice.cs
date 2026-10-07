using System.Runtime.InteropServices;
using FMOD;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// The player's own voice as their room answers it, and the ring a talker's voice is played from. Your
/// own voice is never played dry (you hear it already; a late copy is a slap-back), only the surfaces
/// sending it back and the room's reverberation: how a speaker tells a bathroom from a field, and how a
/// player knows the microphone is live. The microphone writes here as it captures (48 kHz mono, before
/// encoding, never through the server) in 20 ms frames on its own thread, so a reader keeps
/// <see cref="Margin"/> behind the newest sample.
/// </summary>
public sealed class OwnVoiceRing
{
    public const int Rate = 48000;
    /// <summary>The one microphone a client has.</summary>
    public static readonly OwnVoiceRing Shared = new();
    private const int Bits = 17;                       // 2.7 s
    private readonly float[] _ring = new float[1 << Bits];
    private long _written;

    /// <summary>How far behind the newest sample a reader stays: one capture frame and its thread's
    /// poll, seconds.</summary>
    public const double Margin = 0.035;

    /// <summary>
    /// How far behind the newest sample a reader stays, seconds: <see cref="Margin"/> for the microphone,
    /// what the jitter needs for a network voice (TalkerStream). The writer sets it; a reader moves toward
    /// a new one at its pull rate, never jumps.
    /// </summary>
    public double MarginSeconds
    {
        get => Volatile.Read(ref _margin);
        set => Volatile.Write(ref _margin, value);
    }
    private double _margin = Margin;

    /// <summary>
    /// Bumped each time somebody talks again after a pause (TalkerStream). A reader that sees it change
    /// starts from <see cref="SpurtStart"/> once a margin's worth has arrived: a margin behind the newest
    /// sample would, on the first packet, be the end of what they said last.
    /// </summary>
    public int Spurt => Volatile.Read(ref _spurt);
    private int _spurt;
    /// <summary>The first sample of the current run of talking. Read after <see cref="Spurt"/>.</summary>
    public long SpurtStart => Volatile.Read(ref _spurtStart);
    private long _spurtStart;

    /// <summary>Writer: what comes next is a new run of talking.</summary>
    public void BeginSpurt()
    {
        Volatile.Write(ref _spurtStart, _written);
        Interlocked.Increment(ref _spurt);
    }

    /// <summary>How many times a reader has caught up with the newest sample while it was still live: a
    /// margin too short for the connection. TalkerStream lengthens it.</summary>
    public int Underruns => Volatile.Read(ref _underruns);
    private int _underruns;
    internal void Starved() => Interlocked.Increment(ref _underruns);

    /// <summary>Samples ever written. Capture thread writes; the mixer only reads.</summary>
    public long Written => Volatile.Read(ref _written);

    /// <summary>When the last samples arrived, for a reader to fall silent when the microphone stops.</summary>
    public long LastWriteTicks => Volatile.Read(ref _lastWrite);
    private long _lastWrite;

    /// <summary>
    /// How loud this player talks into this microphone, dBFS RMS: a slow average while speaking (above a
    /// -50 dBFS gate), from a typical -26. Their usual voice is taken as normal conversation
    /// (Speech.NormalDb at a metre), so a shout fills the room more and the answer follows the voice,
    /// not the microphone's gain.
    /// </summary>
    public float SpeechRmsDbfs => Volatile.Read(ref _speechDb);
    private float _speechDb = -26f;

    /// <summary>Capture thread.</summary>
    public void Write(float[] samples) => Write(samples.AsSpan());

    /// <summary>The one writer: the capture thread, or the network thread for somebody else's voice.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return;
        double sum = 0;
        foreach (float s in samples) if (float.IsFinite(s)) sum += s * s;
        float db = 10f * MathF.Log10((float)(sum / samples.Length) + 1e-12f);
        // About ten seconds of talking to settle.
        if (db > -50f) Volatile.Write(ref _speechDb, _speechDb + (db - _speechDb) * Math.Min(1f, samples.Length / (10f * Rate)));
        long w = _written;
        int mask = _ring.Length - 1;
        foreach (float s in samples) _ring[(int)(w++ & mask)] = float.IsFinite(s) ? s : 0f;
        Volatile.Write(ref _written, w);
        Volatile.Write(ref _lastWrite, DateTime.UtcNow.Ticks);
    }

    /// <summary>The ring at a fractional sample position, linearly between its neighbours.</summary>
    public float At(double position)
    {
        long i = (long)Math.Floor(position);
        if (i < 0 || i + 1 >= Written || Written - i > _ring.Length - 4800) return 0f;
        int mask = _ring.Length - 1;
        float t = (float)(position - i);
        return _ring[(int)(i & mask)] * (1 - t) + _ring[(int)((i + 1) & mask)] * t;
    }
}

/// <summary>
/// One voice reading the ring at a delay, at the mixer's rate. The read keeps its own clock, pulled
/// toward its target by at most its pull rate, averaged over a couple of seconds: the capture arrives in
/// 10-20 ms bursts and chasing each would wobble the pitch. A delay that changes as you walk arrives the
/// same way (the small pitch shift a reflection really has), never stepped (a click).
/// </summary>
public sealed class OwnVoiceTap : IGuardedUnit
{
    public NonFiniteUnit Guard { get; } = new();
    public readonly OwnVoiceRing Ring;
    /// <summary>The callback's buffer, made with the voice so the mixer thread never allocates.</summary>
    internal readonly float[] Scratch = new float[DspCallback.MaxBlock];
    private readonly double _step, _blockShare, _maxPull;
    /// <summary>A starved sample's fade (0.995, 4.5 ms) and the envelope's glide (0.002, 11 ms), chosen at
    /// 44.1 kHz, at the mixer's rate.</summary>
    private readonly float _starveDecay, _envelopeStep;
    private readonly bool _measures;
    /// <summary>Where the delay is heading, seconds. Game thread writes.</summary>
    public volatile float TargetDelay;
    private double _position = -1, _error;
    private float _envelope, _last;
    private int _spurt;
    private bool _priming, _starved;

    /// <param name="ring">The ring to read.</param>
    /// <param name="delaySeconds">The delay to start at, behind the ring's margin.</param>
    /// <param name="mixerRate">The rate the mixer asks for samples at.</param>
    /// <param name="maxPull">The most the read rate may be pulled off true, as a fraction: 1 % for the room
    /// answering you; far less for somebody talking (TalkerStream.MaxPull).</param>
    /// <param name="measures">Whether running dry is reported to the ring as a margin too short. The voice
    /// does; a surface answering it reads behind it, and reporting twice would lengthen the margin twice.</param>
    public OwnVoiceTap(OwnVoiceRing ring, float delaySeconds, int mixerRate, double maxPull = 0.01, bool measures = true)
    {
        Ring = ring;
        TargetDelay = delaySeconds;
        _maxPull = maxPull;
        _measures = measures;
        _step = (double)OwnVoiceRing.Rate / mixerRate;
        _starveDecay = OpenFPS.Client.AudioEngine.Core.At44k.Decay(0.995f, mixerRate);
        _envelopeStep = OpenFPS.Client.AudioEngine.Core.At44k.Step(0.002f, mixerRate);
        _blockShare = 1.0 / (2.0 * mixerRate);
    }

    /// <summary>Mixer thread.</summary>
    public void Consume(Span<float> mono)
    {
        long written = Ring.Written;
        int n = mono.Length;
        double margin = Ring.MarginSeconds;
        // The microphone stopped (or never started), or the talker did: nothing to play.
        bool live = written > 0
                 && DateTime.UtcNow.Ticks - Ring.LastWriteTicks < (long)((margin + 0.15) * TimeSpan.TicksPerSecond);
        // Where this block should start: the margin and the delay behind the newest sample, less the block.
        double target = written - (margin + TargetDelay) * OwnVoiceRing.Rate - n * _step;
        int spurt = Ring.Spurt;
        if (spurt != _spurt)
        {
            // Talking again after a pause: from the first word, once enough of it has arrived.
            _spurt = spurt;
            // A voice made after they started (it was out of earshot) joins them where they are.
            _position = Math.Max(Ring.SpurtStart - TargetDelay * OwnVoiceRing.Rate, target);
            _priming = true;
            _error = 0; _envelope = 0; _last = 0;
        }
        else if (!_priming && (_position < 0 || Math.Abs(_position - target) > Math.Max(0.08, margin) * OwnVoiceRing.Rate))
        {
            // Far from it (first block, or the capture stalled): start again there, from silence.
            _position = target; _error = 0; _envelope = 0; _last = 0;
        }
        if (_priming)
        {
            if (_position > target) { mono.Clear(); return; }
            _priming = false;
        }
        _error += (target - _position - _error) * Math.Min(1.0, n * _blockShare);
        double rate = _step * (1 + Math.Clamp(_error / (0.5 * OwnVoiceRing.Rate), -_maxPull, _maxPull));
        for (int i = 0; i < n; i++)
        {
            if (_position + 1 >= written)
            {
                // Caught up: wait here rather than run on and skip what is still to arrive. The last
                // sample dies away instead of stopping dead.
                if (live && !_starved) { if (_measures) Ring.Starved(); _starved = true; }
                _last *= _starveDecay;
                mono[i] = _last * _envelope;
                continue;
            }
            _starved = false;
            _envelope += ((live ? 1f : 0f) - _envelope) * _envelopeStep;
            _last = Ring.At(_position);
            mono[i] = _last * _envelope;
            _position += rate;
        }
    }
}

/// <summary>The FMOD side of <see cref="OwnVoiceTap"/>: a read callback that copies out and nothing else.</summary>
public static class OwnVoiceProcessor
{
    private static int _nonFiniteOther;
    private static readonly DSP_READ_CALLBACK _readCallback = ReadCallback;

    public static RESULT CreateDSP(FMOD.System system, OwnVoiceTap tap, out FMOD.DSP dsp, out GCHandle handle)
    {
        var desc = new DSP_DESCRIPTION
        {
            pluginsdkversion = VERSION.number,
            numinputbuffers = 0,
            numoutputbuffers = 1,
            read = _readCallback,
        };
        RESULT res = system.createDSP(ref desc, out dsp);
        if (res == RESULT.OK)
        {
            handle = GCHandle.Alloc(tap);
            dsp.setUserData(GCHandle.ToIntPtr(handle));
        }
        else handle = default;
        return res;
    }

    /// <summary>Nothing may escape a DSP callback (see MachineProcessor).</summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        try
        {
            var r = Core(ref dsp_state, outbuffer, length, ref outchannels);
            NonFinite.After(ref dsp_state, outbuffer, length, inchannels, outchannels, "own voice", ref _nonFiniteOther);
            return r;
        }
        catch (Exception ex)
        {
            DspFault.Record("OwnVoiceProcessor", ex);
            if (outchannels == 0) outchannels = 1;
            DspCallback.Silence(outbuffer, length, outchannels);
            return RESULT.OK;
        }
    }

    private static RESULT Core(ref DSP_STATE dsp_state, IntPtr outbuffer, uint length, ref int outchannels)
    {
        if (outchannels == 0) outchannels = 1;
        IntPtr userData = DspCallback.UserData(ref dsp_state);
        if (userData == IntPtr.Zero) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }
        var tap = (OwnVoiceTap?)GCHandle.FromIntPtr(userData).Target;
        int n = (int)length;
        if (tap == null || n > DspCallback.MaxBlock) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }
        var mono = tap.Scratch.AsSpan(0, n);
        tap.Consume(mono);
        unsafe
        {
            float* outBuf = (float*)outbuffer;
            int ch = outchannels;
            for (int i = 0; i < n; i++)
                for (int c = 0; c < ch; c++) outBuf[i * ch + c] = mono[i];
        }
        return RESULT.OK;
    }
}
