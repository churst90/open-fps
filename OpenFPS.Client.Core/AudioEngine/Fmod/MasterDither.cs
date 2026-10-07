using System.Runtime.InteropServices;
using FMOD;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Triangular dither, one 16-bit step tall, on the very last thing the mixer does.
///
/// The mix is float to the end, and then FMOD hands it to the sound server as SIXTEEN BITS: on Linux
/// its PulseAudio stream is s16le at 44.1 kHz (read off `pactl list sink-inputs` while the lab ran),
/// and the conversion has no dither. A 1 kHz tone two steps tall came out as three values with its
/// third harmonic 16 dB under it (the lab's --quality lsb). That is fine for anything loud. It is not
/// fine for this game's mix, which sits low by design (a median of -28 LUFS in play, a quiet park
/// -48 dBFS RMS) and is listened to turned up: a distant sound, a decaying tail, a reverb return, light
/// rain at -60 to -70 dBFS is only 25-40 dB over the last bit, and undithered, what is left of it is
/// not a little hiss but the sound itself turned into steps — grain that follows the sound.
///
/// Dither turns that error into a steady, signal-independent hiss at about -101 dBFS RMS, under any
/// sound card's own floor, and leaves nothing that follows the signal. Where the output is float
/// already (WASAPI in shared mode usually is) it costs a hiss nobody can hear. See Enabled for when it
/// is left out.
/// </summary>
public sealed class MasterDither : IDisposable
{
    /// <summary>One sixteen-bit step, full scale being 1.</summary>
    private const float Step = 1f / 32768f;

    private static DSP_READ_CALLBACK? _callback;
    private FMOD.DSP _dsp;
    private ChannelGroup _group;
    private GCHandle _handle;
    private uint _a = 0x9E3779B9u, _b = 0x85EBCA6Bu;

    /// <summary>
    /// On for a sound card, off for the WAV writer (OPENFPS_FMOD_WAV): the lab's instruments read that
    /// file for exact silence and two-millisecond holes, which a step of dither would fill.
    /// OPENFPS_DITHER=1 or 0 decides either way.
    /// </summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("OPENFPS_DITHER") switch
    {
        "0" => false,
        "1" => true,
        _ => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENFPS_FMOD_WAV")),
    };

    /// <summary>Puts the dither at the head of the master group: added last, it is the last thing run.</summary>
    public static MasterDither? Attach(FMOD.System system, ChannelGroup master)
    {
        if (!Enabled) return null;
        try
        {
            var d = new MasterDither();
            _callback ??= ReadCallback;
            var desc = new DSP_DESCRIPTION
            {
                pluginsdkversion = VERSION.number,
                numinputbuffers = 1,
                numoutputbuffers = 1,
                read = _callback,
            };
            if (system.createDSP(ref desc, out d._dsp) != RESULT.OK) return null;
            d._handle = GCHandle.Alloc(d);
            d._dsp.setUserData(GCHandle.ToIntPtr(d._handle));
            master.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, d._dsp);
            d._group = master;
            return d;
        }
        catch { return null; }
    }

    private static RESULT ReadCallback(ref DSP_STATE state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        try
        {
            if (outchannels == 0) outchannels = inchannels;
            int total = (int)length * inchannels;
            IntPtr user = DspCallback.UserData(ref state);
            var self = user != IntPtr.Zero ? GCHandle.FromIntPtr(user).Target as MasterDither : null;
            unsafe
            {
                float* src = (float*)inbuffer, dst = (float*)outbuffer;
                if (self == null) { for (int i = 0; i < total; i++) dst[i] = src[i]; return RESULT.OK; }
                uint a = self._a, b = self._b;
                for (int i = 0; i < total; i++)
                {
                    // Two independent uniforms, differenced: triangular, one step either way.
                    a ^= a << 13; a ^= a >> 17; a ^= a << 5;
                    b ^= b << 13; b ^= b >> 17; b ^= b << 5;
                    float tpdf = ((a >> 8) - (float)(b >> 8)) * (Step / 16777216f);
                    dst[i] = src[i] + tpdf;
                }
                self._a = a; self._b = b;
            }
            return RESULT.OK;
        }
        catch (Exception ex)
        {
            unsafe
            {
                int ch = outchannels > 0 ? outchannels : (inchannels > 0 ? inchannels : 2);
                if (outbuffer != IntPtr.Zero && inbuffer != IntPtr.Zero)
                    new Span<float>((void*)inbuffer, (int)length * ch).CopyTo(new Span<float>((void*)outbuffer, (int)length * ch));
            }
            DspFault.Record("MasterDither", ex);
            return RESULT.OK;
        }
    }

    /// <summary>Takes the unit off the master and releases it. The handle the callback resolves is kept
    /// until <see cref="FreeHandle"/>, after the system is closed: FMOD may call a released unit once more.</summary>
    public void Dispose()
    {
        try
        {
            if (_dsp.hasHandle() && _group.hasHandle()) _group.removeDSP(_dsp);
            if (_dsp.hasHandle()) _dsp.release();
        }
        catch { }
    }

    /// <summary>Frees the callback's handle. Only once no callback can be in flight (System.close).</summary>
    public void FreeHandle()
    {
        if (_handle.IsAllocated) _handle.Free();
    }
}
