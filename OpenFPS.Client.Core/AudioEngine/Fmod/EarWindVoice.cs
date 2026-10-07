using System.Runtime.InteropServices;
using FMOD;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Nature;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// The wind at the listener's ears: one stereo generator played flat on the master, with no reverb
/// send and no binaural placement, because it is made at the ears rather than arriving from anywhere
/// (<see cref="EarWind"/>). The game thread sets the listener (<see cref="SetListener"/>); the mixer
/// reads the wind field itself once a block, so the gusts keep moving while the game thread is busy.
/// </summary>
public sealed class EarWindState : IGuardedUnit
{
    public NonFiniteUnit Guard { get; } = new();

    public readonly EarWindSynth Synth;

    /// <summary>The listener, or null with no world loaded. Game thread writes, mixer reads; the box is
    /// swapped whole so the mixer never reads half of one frame's listener.</summary>
    public void SetListener(EarWindListener? listener)
        => System.Threading.Volatile.Write(ref _listener, listener is { } l ? new Box(l) : null);
    private Box? _listener;
    private sealed class Box { public readonly EarWindListener L; public Box(EarWindListener l) { L = l; } }

    /// <summary>Off with OPENFPS_EAR_WIND=0: a lever for listening without it, not a setting.</summary>
    public volatile bool Enabled = true;

    /// <summary>Glided over 0.4 s so a doorway fades the wind rather than cutting it.</summary>
    private float _exposure = -1f;
    private const float ExposureSeconds = 0.4f;

    /// <summary>Mixer-thread scratch so the callback never allocates.</summary>
    internal readonly float[] Left = new float[DspCallback.MaxBlock], Right = new float[DspCallback.MaxBlock];

    public EarWindState(float sampleRate) { Synth = new EarWindSynth(sampleRate, 0x5EED); }

    public void ResetAfterFault() => Synth.Reset();

    /// <summary>One block's control: the field at the ears now. Mixer thread.</summary>
    internal void Control(int frames)
    {
        float dt = frames / Synth.SampleRate;
        var box = System.Threading.Volatile.Read(ref _listener);
        if (!Enabled || box == null)
        {
            Synth.Control(default);
            return;
        }
        var listener = box.L;
        float target = Math.Clamp(listener.Exposure, 0f, 1f);
        if (_exposure < 0f) _exposure = target;
        _exposure += (target - _exposure) * (1f - MathF.Exp(-dt / ExposureSeconds));
        var ears = EarWind.Hear(WindField.Weather, listener with { Exposure = _exposure }, WindField.Now());
        Synth.Control(ears);
    }
}

/// <summary>The FMOD generator for <see cref="EarWindState"/>.</summary>
internal static class EarWindProcessor
{
    private static int _nonFiniteOther;
    private static readonly FMOD.DSP_READ_CALLBACK _readCallback = ReadCallback;

    public static RESULT CreateDSP(FMOD.System system, EarWindState state, out FMOD.DSP dsp, out GCHandle handle)
    {
        var desc = new FMOD.DSP_DESCRIPTION
        {
            pluginsdkversion = FMOD.VERSION.number,
            numinputbuffers = 0,
            numoutputbuffers = 1,
            read = _readCallback
        };
        RESULT res = system.createDSP(ref desc, out dsp);
        if (res == RESULT.OK)
        {
            handle = GCHandle.Alloc(state);
            dsp.setUserData(GCHandle.ToIntPtr(handle));
            dsp.setChannelFormat(0, 0, SPEAKERMODE.STEREO);
        }
        else handle = default;
        return res;
    }

    /// <summary>A managed DSP callback must not throw: a fault is one silent block and a line in the
    /// log from the game thread (DspFault).</summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        try
        {
            Render(ref dsp_state, outbuffer, length, ref outchannels);
            NonFinite.After(ref dsp_state, outbuffer, length, inchannels, outchannels, "ear wind", ref _nonFiniteOther);
        }
        catch (Exception ex)
        {
            DspCallback.Silence(outbuffer, length, outchannels > 0 ? outchannels : 2);
            DspFault.Record("EarWind", ex);
        }
        return RESULT.OK;
    }

    private static unsafe void Render(ref DSP_STATE dsp_state, IntPtr outbuffer, uint length, ref int outchannels)
    {
        if (outchannels == 0) outchannels = 2;
        int ch = outchannels;
        IntPtr userData = DspCallback.UserData(ref dsp_state);
        if (userData == IntPtr.Zero || outbuffer == IntPtr.Zero || length > DspCallback.MaxBlock
            || GCHandle.FromIntPtr(userData).Target is not EarWindState s)
        {
            DspCallback.Silence(outbuffer, length, ch);
            return;
        }
        int n = (int)length;
        s.Control(n);
        var left = s.Left.AsSpan(0, n);
        var right = s.Right.AsSpan(0, n);
        s.Synth.Render(left, right);
        float* o = (float*)outbuffer;
        for (int i = 0; i < n; i++)
        {
            o[i * ch] = left[i];
            if (ch > 1) o[i * ch + 1] = right[i];
            for (int c = 2; c < ch; c++) o[i * ch + c] = 0f;
        }
    }
}
