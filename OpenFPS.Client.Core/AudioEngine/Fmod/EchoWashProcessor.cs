using System.Runtime.InteropServices;
using FMOD;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// A recorded sound's copy off a wall, split as the wall splits it: the mirror share clean, and the share
/// a rough surface scatters through the engines' <see cref="EchoDiffuser"/>. Output is
/// mirror · x + √(1 − mirror²) · D(x), so the copy's energy is kept. Reconfigured only while detached;
/// the callback allocates nothing.
/// </summary>
public sealed class EchoWashState
{
    /// <summary>A surface this smooth or smoother returns a mirror and nothing to smear (glass, marble,
    /// still water), as WorldAudioPlayer.RoomEcho.WashGain has it for one-off sounds.</summary>
    public const float SmoothScattering = 0.05f;

    /// <summary>Recordings are mono or stereo; a channel past these passes through clean.</summary>
    public const int MaxChannels = 2;

    private readonly EchoDiffuser?[] _diffusers = new EchoDiffuser?[MaxChannels];
    public float Mirror { get; private set; } = 1f;
    public float Wash { get; private set; }
    public float Scattering { get; private set; }
    public int NonFiniteReported;

    /// <summary>Whether a copy off this surface has anything to smear.</summary>
    public static bool Applies(float scattering) => scattering > SmoothScattering;

    /// <summary>Sets the surface and the mirror's share of the pressure, 0..1 (0: all of it smeared).
    /// Game thread, while no channel holds the unit.</summary>
    public void Configure(float scattering, float mirrorShare, int seed, float sampleRate)
    {
        Scattering = Math.Clamp(scattering, 0f, 1f);
        Mirror = Math.Clamp(mirrorShare, 0f, 1f);
        Wash = MathF.Sqrt(1f - Mirror * Mirror);
        for (int c = 0; c < MaxChannels; c++) _diffusers[c] = new EchoDiffuser(Scattering, seed + 101 * c, sampleRate);
    }

    /// <summary>The copy over interleaved spans. Mixer thread: no allocation.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output, int channels)
    {
        if (channels <= 0) { output.Clear(); return; }
        int n = Math.Min(input.Length, output.Length) / channels;
        float mirror = Mirror, wash = Wash;
        for (int c = 0; c < channels; c++)
        {
            var d = c < MaxChannels ? _diffusers[c] : null;
            for (int i = 0; i < n; i++)
            {
                float x = input[i * channels + c];
                output[i * channels + c] = d == null ? x : mirror * x + wash * d.Process(x);
            }
        }
    }
}

/// <summary>The FMOD unit for <see cref="EchoWashState"/>, at the input end of a copy's chain.</summary>
public static class EchoWashProcessor
{
    private static readonly DSP_READ_CALLBACK _readCallback = ReadCallback;

    public static RESULT CreateDSP(FMOD.System system, EchoWashState state, out FMOD.DSP dsp, out GCHandle handle)
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
        // Never throws into the mixer thread: anything wrong passes the copy through clean.
        try
        {
            if (outchannels == 0) outchannels = inchannels;
            IntPtr userData = DspCallback.UserData(ref dsp_state);
            if (userData == IntPtr.Zero || inbuffer == IntPtr.Zero || outbuffer == IntPtr.Zero || inchannels != outchannels
                || GCHandle.FromIntPtr(userData).Target is not EchoWashState s)
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
                NonFinite.Scrub(output, ref s.NonFiniteReported, "a wall's smeared copy");
            }
        }
        catch (Exception ex)
        {
            DspCallback.PassThrough(inbuffer, outbuffer, length, inchannels, outchannels);
            DspFault.Record("EchoWash", ex);
        }
        return RESULT.OK;
    }
}
