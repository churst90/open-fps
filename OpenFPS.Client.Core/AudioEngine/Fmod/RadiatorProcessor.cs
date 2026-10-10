using System.Runtime.InteropServices;
using FMOD;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// A loudspeaker's beam toward the listener (Radiator), band by band: the voice split into the
/// radiator's six octave bands (<see cref="RadiatorBands"/>), each at its own gain. Behind a horn the
/// top goes and the low mids and the housing stay, which three bands could not say: the mixer's mid
/// band runs from 400 Hz to 4 kHz, most of what a horn radiates.
///
/// A loudspeaker voice carries two: ahead of its own room's send, the power it radiates all round
/// (Radiator.PowerGains, fixed), so its room is fed band by band with what it really radiates; after
/// the send, its beam toward you over that power (Radiator.BeamOverPower), so the direct sound is the
/// beam. Over unity on the axis: a horn's 8 kHz octave is 22 dB above its all-round power.
///
/// The game thread sets the targets (<see cref="SetTargets"/>); the callback slews to them over 50 ms
/// so a listener walking round the horn hears it turn, not step. Six floats, written whole or not at
/// all: a torn update is one block of a gain half way to its next value. The callback allocates nothing.
/// </summary>
public sealed class RadiatorState
{
    public const int MaxChannels = 2;
    private const float SlewSeconds = 0.05f;
    private readonly RadiatorBands[] _bands = { new(), new() };
    private readonly float[] _target = new float[Radiator.Bands];
    private readonly float[] _gain = new float[Radiator.Bands];
    private float _slew = 1f;
    public int NonFiniteReported;

    /// <summary>The gains being slewed to, for tests and instruments.</summary>
    public ReadOnlySpan<float> Targets => _target;

    /// <summary>Sets the rate and starts at unity. Game thread, while no channel holds the unit.</summary>
    public void Configure(float sampleRate)
    {
        foreach (var b in _bands) b.Configure(sampleRate);
        Array.Fill(_target, 1f);
        Array.Fill(_gain, 1f);
        _slew = MathF.Min(1f, 1f / (sampleRate * SlewSeconds));
    }

    /// <summary>Starts at these gains with no slew: a voice's first block is already aimed.</summary>
    public void Start(ReadOnlySpan<float> gains)
    {
        SetTargets(gains);
        for (int b = 0; b < _gain.Length; b++) _gain[b] = _target[b];
    }

    public void SetTargets(ReadOnlySpan<float> gains)
    {
        for (int b = 0; b < _target.Length && b < gains.Length; b++)
            _target[b] = float.IsFinite(gains[b]) ? Math.Clamp(gains[b], 0f, 100f) : 1f;
    }

    /// <summary>The voice over interleaved spans, block by block. Mixer thread: no allocation.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output, int channels)
    {
        if (channels <= 0) { output.Clear(); return; }
        int frames = Math.Min(input.Length, output.Length) / channels;
        for (int at = 0; at < frames; at += Chunk)
        {
            int n = Math.Min(Chunk, frames - at);
            // Each band's gain, sample by sample across the chunk, shared by the channels.
            for (int b = 0; b < Radiator.Bands; b++)
            {
                float g = _gain[b], t = _target[b], k = _slew;
                var track = _track[b];
                for (int i = 0; i < n; i++) { g += (t - g) * k; track[i] = g; }
                _gain[b] = g;
            }
            for (int c = 0; c < channels; c++)
            {
                if (c >= MaxChannels)
                {
                    for (int i = 0; i < n; i++) output[(at + i) * channels + c] = input[(at + i) * channels + c];
                    continue;
                }
                var rest = _rest.AsSpan(0, n);
                for (int i = 0; i < n; i++) rest[i] = input[(at + i) * channels + c];
                _bands[c].SplitBlock(rest, _band, n);
                _bands[c].MixBlock(rest, _band, _track, _sum, n);
                for (int i = 0; i < n; i++) output[(at + i) * channels + c] = _sum[i];
            }
        }
    }

    // Scratch for a chunk, made once: the mixer hands over 1,024 frames, and a longer block goes in pieces.
    private const int Chunk = 1024;
    private readonly float[] _rest = new float[Chunk];
    private readonly float[] _sum = new float[Chunk];
    private readonly float[][] _band = Enumerable.Range(0, Radiator.Bands - 1).Select(_ => new float[Chunk]).ToArray();
    private readonly float[][] _track = Enumerable.Range(0, Radiator.Bands).Select(_ => new float[Chunk]).ToArray();
}

/// <summary>The FMOD unit for <see cref="RadiatorState"/>, on a loudspeaker's chain.</summary>
public static class RadiatorProcessor
{
    private static readonly DSP_READ_CALLBACK _readCallback = ReadCallback;

    public static RESULT CreateDSP(FMOD.System system, RadiatorState state, out FMOD.DSP dsp, out GCHandle handle)
    {
        var desc = new DSP_DESCRIPTION
        {
            pluginsdkversion = VERSION.number,
            numinputbuffers = 1,
            numoutputbuffers = 1,
            read = _readCallback,
        };
        RESULT res = system.createDSP(ref desc, out dsp);
        if (res == RESULT.OK)
        {
            handle = GCHandle.Alloc(state);
            dsp.setUserData(GCHandle.ToIntPtr(handle));
        }
        else handle = default;
        return res;
    }

    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        // Never throws into the mixer thread: anything wrong passes the voice through as it is.
        long started = MixerProfile.Start();
        try
        {
            if (outchannels == 0) outchannels = inchannels;
            IntPtr userData = DspCallback.UserData(ref dsp_state);
            if (userData == IntPtr.Zero || inbuffer == IntPtr.Zero || outbuffer == IntPtr.Zero || inchannels != outchannels
                || GCHandle.FromIntPtr(userData).Target is not RadiatorState s)
            {
                DspCallback.PassThrough(inbuffer, outbuffer, length, inchannels, outchannels);
                return RESULT.OK;
            }
            unsafe
            {
                int count = (int)length * inchannels;
                var input = new ReadOnlySpan<float>((void*)inbuffer, count);
                var output = new Span<float>((void*)outbuffer, count);
                s.Process(input, output, inchannels);
                NonFinite.Scrub(output, ref s.NonFiniteReported, "a loudspeaker's beam");
            }
        }
        catch (Exception ex)
        {
            DspCallback.PassThrough(inbuffer, outbuffer, length, inchannels, outchannels);
            DspFault.Record("Radiator", ex);
        }
        MixerProfile.Stop(MixerProfile.Kind.Loudspeaker, started);
        return RESULT.OK;
    }
}
