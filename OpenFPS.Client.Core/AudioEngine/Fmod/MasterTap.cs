using System.Runtime.InteropServices;
using FMOD;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Writes what the mixer makes to a WAV file while it still plays: a pass-through DSP on the master,
/// where FMOD's WAVWRITER would replace the sound card. A clipped waveform, a starved buffer, a stepped
/// voice and aliasing all sound like "it crackles" and look different in thirty seconds of samples.
/// </summary>
public sealed class MasterTap : IDisposable
{
    private readonly FileStream _file;
    private readonly BinaryWriter _writer;
    private readonly int _rate;
    /// <summary>IEEE float, unclamped: over full scale and below the sixteenth bit as the mixer made
    /// it (OPENFPS_AUDIO_CAPTURE_FLOAT, and the lab's measurements).</summary>
    private readonly bool _float;
    private int _channels;
    private long _frames;
    private readonly object _lock = new();
    private bool _closed;

    private static DSP_READ_CALLBACK? _callback;
    private FMOD.DSP _dsp;
    private ChannelGroup _group;   // the group it was added to, so it can be taken off again
    private GCHandle _handle;

    private MasterTap(string path, int rate, bool asFloat)
    {
        _rate = rate;
        _float = asFloat;
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(_file);
        // The header's sizes are filled in on close.
        _writer.Write(new byte[44]);
    }

    /// <summary>
    /// Attaches a tap to the master group at <paramref name="index"/> (the head, what leaves the mixer,
    /// by default; a number puts it there, e.g. just before the limiter). Null if the DSP could not be
    /// made; never throws, because a diagnostic must not be able to break playback.
    /// </summary>
    public static MasterTap? Attach(FMOD.System system, ChannelGroup master, string path, int rate,
                                    int index = CHANNELCONTROL_DSP_INDEX.HEAD, bool asFloat = false)
    {
        try
        {
            var tap = new MasterTap(path, rate, asFloat);
            _callback ??= ReadCallback;
            var desc = new DSP_DESCRIPTION
            {
                pluginsdkversion = VERSION.number,
                numinputbuffers = 1,
                numoutputbuffers = 1,
                read = _callback,
            };
            if (system.createDSP(ref desc, out tap._dsp) != RESULT.OK) { tap.Dispose(); return null; }
            tap._handle = GCHandle.Alloc(tap);
            tap._dsp.setUserData(GCHandle.ToIntPtr(tap._handle));
            master.addDSP(index, tap._dsp);
            tap._group = master;
            return tap;
        }
        catch { return null; }
    }

    /// <summary>
    /// A managed DSP callback must not throw: FMOD calls it on its native mixer thread, and an exception
    /// unwinding across that boundary kills the process (it did, from an index slip in the boundary DSP).
    /// A fault costs one silent block and one line in the log.
    /// </summary>
    private static RESULT ReadCallback(ref DSP_STATE state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        try { return ReadCallbackCore(ref state, inbuffer, outbuffer, length, inchannels, ref outchannels); }
        catch (Exception ex)
        {
            unsafe
            {
                int ch = outchannels > 0 ? outchannels : (inchannels > 0 ? inchannels : 2);
                if (outbuffer != IntPtr.Zero)
                    new Span<float>((void*)outbuffer, (int)length * ch).Clear();
            }
            DspFault.Record("MasterTap", ex);
            return RESULT.OK;
        }
    }


    private static RESULT ReadCallbackCore(ref DSP_STATE state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        IntPtr user = DspCallback.UserData(ref state);
        int n = (int)length, ch = inchannels;
        if (outchannels == 0) outchannels = ch;

        // Pass through first and unconditionally: the capture must never be able to silence the game.
        unsafe
        {
            float* src = (float*)inbuffer, dst = (float*)outbuffer;
            int total = n * ch;
            for (int i = 0; i < total; i++) dst[i] = src[i];

            if (user != IntPtr.Zero && GCHandle.FromIntPtr(user).Target is MasterTap tap)
                tap.Write(src, total, ch);
        }
        return RESULT.OK;
    }

    private unsafe void Write(float* src, int total, int channels)
    {
        lock (_lock)
        {
            if (_closed) return;
            _channels = channels;
            if (_float)
                for (int i = 0; i < total; i++) _writer.Write(src[i]);
            else
                for (int i = 0; i < total; i++)
                {
                    float v = src[i];
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    _writer.Write((short)(v * 32767f));
                }
            _frames += total / Math.Max(1, channels);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
            try
            {
                // Off the group first: FMOD will not release an attached unit ("Failed to release
                // because unit is still attached"), and the tap stayed in the chain for the run.
                if (_dsp.hasHandle() && _group.hasHandle()) _group.removeDSP(_dsp);
                if (_dsp.hasHandle()) { _dsp.release(); }
                if (_handle.IsAllocated) _handle.Free();

                int ch = Math.Max(1, _channels);
                int bytes = _float ? 4 : 2;
                long dataBytes = _frames * ch * bytes;
                _writer.Flush();
                _file.Seek(0, SeekOrigin.Begin);
                _writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                _writer.Write((int)(36 + dataBytes));
                _writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
                _writer.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                _writer.Write(16);
                _writer.Write((short)(_float ? 3 : 1));   // IEEE float or PCM
                _writer.Write((short)ch);
                _writer.Write(_rate);
                _writer.Write(_rate * ch * bytes);
                _writer.Write((short)(ch * bytes));
                _writer.Write((short)(bytes * 8));
                _writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                _writer.Write((int)dataBytes);
                _writer.Flush();
            }
            catch { /* a diagnostic that cannot be written is not worth an exception on shutdown */ }
            finally { _writer.Dispose(); _file.Dispose(); }
        }
    }
}
