using System.Runtime.InteropServices;
using FMOD;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Tests;

/// <summary>
/// How the binaural stage (SteamAudioDsp) asks FMOD for its channels, without the native libraries.
///
/// The stage takes one channel in and puts two out. Done with a read callback it had to have its input
/// made stereo, and FMOD did that by panning the mono voice to the middle, 3.01 dB down on each side:
/// every voice reached the HRTF 3.01 dB under its placed level. The level itself is measured through
/// FMOD and Steam Audio by the AudioLab (--binaural-input); these hold the contract that gives it.
/// </summary>
public class BinauralStageInputTests
{
    [Fact]
    public void TheStageDeclaresItsOutputRatherThanHavingItsInputConverted()
    {
        var desc = SteamAudioDsp.Description();
        Assert.NotNull(desc.process);
        Assert.Null(desc.read);
        Assert.Equal(1, desc.numinputbuffers);
        Assert.Equal(1, desc.numoutputbuffers);
    }

    [Fact]
    public void TheQueryAnswersAStereoPairAndLeavesAMonoVoiceMono()
    {
        using var input = new BufferArray(channels: 1);
        using var output = new BufferArray(channels: 0);
        var state = new DSP_STATE();
        var inArray = input.Array;
        var outArray = output.Array;

        var result = SteamAudioDsp.ProcessCallback(ref state, 1024, ref inArray, ref outArray, false,
                                                   DSP_PROCESS_OPERATION.PROCESS_QUERY);

        Assert.Equal(RESULT.OK, result);
        Assert.Equal(SteamAudioDsp.OutputChannels, outArray.numchannels);
        Assert.Equal(SPEAKERMODE.STEREO, outArray.speakermode);
        Assert.Equal(1, inArray.numchannels);
    }

    /// <summary>One FMOD buffer array as the mixer hands it over: a channel count, a mask and a buffer
    /// pointer, in unmanaged memory.</summary>
    private sealed class BufferArray : IDisposable
    {
        private readonly IntPtr _channels = Marshal.AllocHGlobal(sizeof(int));
        private readonly IntPtr _mask = Marshal.AllocHGlobal(sizeof(int));
        private readonly IntPtr _buffers = Marshal.AllocHGlobal(IntPtr.Size);

        public BufferArray(int channels)
        {
            Marshal.WriteInt32(_channels, channels);
            Marshal.WriteInt32(_mask, 0);
            Marshal.WriteIntPtr(_buffers, IntPtr.Zero);
        }

        public DSP_BUFFER_ARRAY Array => new()
        {
            numbuffers = 1, buffernumchannels = _channels, bufferchannelmask = _mask, buffers = _buffers,
            speakermode = SPEAKERMODE.DEFAULT,
        };

        public void Dispose()
        {
            Marshal.FreeHGlobal(_channels);
            Marshal.FreeHGlobal(_mask);
            Marshal.FreeHGlobal(_buffers);
        }
    }
}
