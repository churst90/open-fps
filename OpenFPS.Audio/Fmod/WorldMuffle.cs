using System.Runtime.InteropServices;
using FMOD;

namespace OpenFPS.Audio.Fmod;

/// <summary>
/// A low-pass over the whole world's mix, set by the host (a head under water): one filter on the world group, which
/// every world voice, reflection, reverb bus and the ear wind reach, and the interface and the host's own flat sounds
/// do not. Off by default, and off it is a copy of the input, so the game's mix is as it was.
/// </summary>
/// <remarks>
/// Two poles (12 dB an octave) in the trapezoidal state-variable form, which stays stable while its cutoff moves. The
/// cutoff glides in octaves, not in hertz, so a drop from the open mix to 400 Hz sweeps at an even rate and never
/// steps; on off it glides back up to the open end and then passes the mix through untouched. The game thread writes
/// the target alone; the mixer reads it. Nothing here allocates or locks on the mixer thread.
/// </remarks>
public sealed class WorldMuffle : IDisposable
{
    private const int MaxChannels = 8;
    private const float GlideSeconds = 0.040f;
    private const int CoefficientEvery = 8;

    private static DSP_READ_CALLBACK? _callback;

    private readonly float _rate, _openHz;
    private readonly float _glide;
    private FMOD.DSP _dsp;
    private ChannelGroup _group;
    private GCHandle _handle;

    // The game thread's: the cutoff wanted, Hz, or 0 for none.
    private volatile float _targetHz;
    // The mixer's: where the cutoff is now (log2 Hz), and the filter's two memories for each channel.
    private float _logHz;
    private bool _passing = true;
    private readonly float[] _ic1 = new float[MaxChannels], _ic2 = new float[MaxChannels];

    /// <summary>Where the filter is open: it is passed through from here, and a muffle's sweep starts from here.</summary>
    public float OpenHz => _openHz;

    /// <summary>The cutoff the host asked for, Hz; 0 when off.</summary>
    public float TargetHz => _targetHz;

    private WorldMuffle(int sampleRate)
    {
        _rate = sampleRate > 0 ? sampleRate : MixerQuality.DefaultRate;
        // Short of the Nyquist rate so the sweep ends where the filter is flat to the ear.
        _openHz = MathF.Min(20000f, _rate * 0.45f);
        _glide = 1f - MathF.Exp(-1f / (GlideSeconds * _rate));
        _logHz = MathF.Log2(_openHz);
    }

    /// <summary>Puts the filter at the head of the group, after its fader. Null when FMOD would not make it.</summary>
    public static WorldMuffle? Attach(FMOD.System system, ChannelGroup group, int sampleRate)
    {
        try
        {
            var m = new WorldMuffle(sampleRate);
            _callback ??= ReadCallback;
            var desc = new DSP_DESCRIPTION
            {
                pluginsdkversion = VERSION.number,
                numinputbuffers = 1,
                numoutputbuffers = 1,
                read = _callback,
            };
            if (system.createDSP(ref desc, out m._dsp) != RESULT.OK) return null;
            m._handle = GCHandle.Alloc(m);
            m._dsp.setUserData(GCHandle.ToIntPtr(m._handle));
            group.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, m._dsp);
            m._group = group;
            return m;
        }
        catch { return null; }
    }

    /// <summary>The cutoff to sweep to, Hz; 0, a negative, a non-finite number or one at the open end for none.</summary>
    public void Set(float cutoffHz)
        => _targetHz = float.IsFinite(cutoffHz) && cutoffHz > 0f && cutoffHz < _openHz ? MathF.Max(cutoffHz, 20f) : 0f;

    private static RESULT ReadCallback(ref DSP_STATE state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        long profiled = MixerProfile.Start();
        try
        {
            if (outchannels == 0) outchannels = inchannels;
            try
            {
                IntPtr user = DspCallback.UserData(ref state);
                var self = user != IntPtr.Zero ? GCHandle.FromIntPtr(user).Target as WorldMuffle : null;
                if (self == null || inbuffer == IntPtr.Zero || outbuffer == IntPtr.Zero || inchannels != outchannels
                    || inchannels <= 0 || inchannels > MaxChannels)
                {
                    DspCallback.PassThrough(inbuffer, outbuffer, length, inchannels, outchannels);
                    return RESULT.OK;
                }
                self.Process(inbuffer, outbuffer, (int)length, inchannels);
                return RESULT.OK;
            }
            catch (Exception ex)
            {
                DspCallback.PassThrough(inbuffer, outbuffer, length, inchannels, outchannels);
                DspFault.Record("WorldMuffle", ex);
                return RESULT.OK;
            }
        }
        finally { MixerProfile.Stop(MixerProfile.Kind.Master, profiled); }
    }

    private unsafe void Process(IntPtr inbuffer, IntPtr outbuffer, int n, int ch)
    {
        float* src = (float*)inbuffer, dst = (float*)outbuffer;
        float wanted = _targetHz;
        float logOpen = MathF.Log2(_openHz);
        float logTarget = wanted > 0f ? MathF.Log2(wanted) : logOpen;

        // Open and settled: the mix as it came, and the filter forgotten so the next muffle starts clean.
        if (wanted == 0f && _passing)
        {
            for (int i = 0; i < n * ch; i++) dst[i] = src[i];
            return;
        }
        if (_passing)
        {
            _passing = false;
            Array.Clear(_ic1); Array.Clear(_ic2);
        }

        float a1 = 0, a2 = 0, a3 = 0;
        for (int i = 0; i < n; i++)
        {
            _logHz += (logTarget - _logHz) * _glide;
            if (i % CoefficientEvery == 0)
            {
                float g = MathF.Tan(MathF.PI * MathF.Pow(2f, _logHz) / _rate);
                const float k = 1.41421356f;   // Q of one over root two: flat, no peak
                a1 = 1f / (1f + g * (g + k)); a2 = g * a1; a3 = g * a2;
            }
            for (int c = 0; c < ch; c++)
            {
                float x = src[i * ch + c];
                float v3 = x - _ic2[c];
                float v1 = a1 * _ic1[c] + a2 * v3;
                float v2 = _ic2[c] + a2 * _ic1[c] + a3 * v3;
                _ic1[c] = 2f * v1 - _ic1[c];
                _ic2[c] = 2f * v2 - _ic2[c];
                dst[i * ch + c] = v2;
            }
        }
        bool finite = true;
        for (int c = 0; c < ch; c++) finite &= float.IsFinite(_ic1[c]) && float.IsFinite(_ic2[c]);
        if (!finite)
        {
            // A bad number in the world's mix: this block is the input, and the filter starts again.
            for (int i = 0; i < n * ch; i++) dst[i] = float.IsFinite(src[i]) ? src[i] : 0f;
            Array.Clear(_ic1); Array.Clear(_ic2);
        }
        if (wanted == 0f && _logHz > logOpen - 0.01f) _passing = true;
    }

    /// <summary>Takes the unit off its group and releases it. The callback's handle is kept until
    /// <see cref="FreeHandle"/>, after the system is closed.</summary>
    public void Dispose()
    {
        try
        {
            if (_dsp.hasHandle() && _group.hasHandle()) _group.removeDSP(_dsp);
            if (_dsp.hasHandle()) _dsp.release();
        }
        catch { }
    }

    /// <summary>Frees the callback's handle, once no callback can be in flight (System.close).</summary>
    public void FreeHandle()
    {
        if (_handle.IsAllocated) _handle.Free();
    }
}
