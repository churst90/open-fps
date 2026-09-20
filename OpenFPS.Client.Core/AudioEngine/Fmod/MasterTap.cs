using System;
using System.IO;
using System.Runtime.InteropServices;
using FMOD;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Writes everything the mixer produces to a WAV file WHILE IT STILL PLAYS.
///
/// FMOD's own WAVWRITER output does this by replacing the sound card, which is fine for a rig and
/// useless for a person: reproducing an artefact usually means playing normally until you hear it.
/// This is a pass-through DSP at the head of the master chain instead — the samples go to the
/// speakers exactly as before and a copy goes to disk.
///
/// It exists because "it crackles" cannot be diagnosed from a description or a CPU percentage. The
/// difference between a clipped waveform, a starved buffer, a stepped voice and an aliasing
/// synthesis is obvious in thirty seconds of samples and invisible from the listening chair, and
/// every one of them is fixed somewhere different.
/// </summary>
public sealed class MasterTap : IDisposable
{
    private readonly FileStream _file;
    private readonly BinaryWriter _writer;
    private readonly int _rate;
    private int _channels;
    private long _frames;
    private readonly object _lock = new();
    private bool _closed;

    private static DSP_READ_CALLBACK? _callback;
    private FMOD.DSP _dsp;
    private ChannelGroup _group;   // the group it was added to, so it can be taken off again
    private GCHandle _handle;

    private MasterTap(string path, int rate)
    {
        _rate = rate;
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(_file);
        // Header is written now with placeholder sizes and rewritten on close.
        _writer.Write(new byte[44]);
    }

    /// <summary>
    /// Attaches a tap to the master channel group. Returns null when no capture was asked for or the
    /// DSP could not be made — never throws, because a diagnostic must not be able to break playback.
    /// </summary>
    public static MasterTap? Attach(FMOD.System system, ChannelGroup master, string path, int rate)
    {
        try
        {
            var tap = new MasterTap(path, rate);
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
            master.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, tap._dsp);
            tap._group = master;
            return tap;
        }
        catch { return null; }
    }

    /// <summary>
    /// The guard, and the reason it is a separate method: a managed DSP callback MUST NOT THROW.
    ///
    /// FMOD calls this from its own native mixer thread, and an exception that unwinds across that
    /// boundary does not fault a voice — it takes the whole process down. The client was killed
    /// exactly that way by an index slip in the boundary DSP, which had no guard either. Everything
    /// below stays as it was; a fault now costs one silent block and one line in the log.
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

        // Pass through FIRST and unconditionally: the capture must never be able to silence the game.
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
            for (int i = 0; i < total; i++)
            {
                // 16-bit is plenty to see a discontinuity and keeps the file small enough to pass around.
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
                // OFF THE GROUP FIRST. FMOD refuses to release an attached unit and says so in its
                // logging build — `Failed to release because unit is still attached` — so a release
                // here freed nothing and left the tap in the master chain for the rest of the run.
                if (_dsp.hasHandle() && _group.hasHandle()) _group.removeDSP(_dsp);
                if (_dsp.hasHandle()) { _dsp.release(); }
                if (_handle.IsAllocated) _handle.Free();

                int ch = Math.Max(1, _channels);
                long dataBytes = _frames * ch * 2;
                _writer.Flush();
                _file.Seek(0, SeekOrigin.Begin);
                _writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                _writer.Write((int)(36 + dataBytes));
                _writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
                _writer.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                _writer.Write(16);
                _writer.Write((short)1);
                _writer.Write((short)ch);
                _writer.Write(_rate);
                _writer.Write(_rate * ch * 2);
                _writer.Write((short)(ch * 2));
                _writer.Write((short)16);
                _writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                _writer.Write((int)dataBytes);
                _writer.Flush();
            }
            catch { /* a diagnostic that cannot be written is not worth an exception on shutdown */ }
            finally { _writer.Dispose(); _file.Dispose(); }
        }
    }
}
