using System.Runtime.InteropServices;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Tests;

/// <summary>
/// What a mixer callback writes when it has nothing to render, and what a pooled voice forgets
/// before it plays again.
/// </summary>
public class MixerCallbackSafetyTests
{
    private static void WithBuffer(float[] data, Action<IntPtr> use)
    {
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { use(pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
    }

    [Fact]
    public void SilenceClearsTheWholeBlock()
    {
        var buf = new float[256 * 2];
        Array.Fill(buf, 0.7f);
        WithBuffer(buf, p => DspCallback.Silence(p, 256, 2));
        Assert.All(buf, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void SilenceWritesNothingWhenTheChannelCountIsUnknown()
    {
        // FMOD has not said how wide the buffer is: clearing a guessed width could run past its end.
        var buf = new float[16];
        Array.Fill(buf, 0.7f);
        WithBuffer(buf, p => DspCallback.Silence(p, 8, 0));
        Assert.All(buf, v => Assert.Equal(0.7f, v));
    }

    [Fact]
    public void PassThroughCopiesTheInputAndSilencesAMismatch()
    {
        var input = new float[64 * 2];
        for (int i = 0; i < input.Length; i++) input[i] = i * 0.01f;
        var output = new float[64 * 2];
        Array.Fill(output, 9f);
        WithBuffer(input, ip => WithBuffer(output, op => DspCallback.PassThrough(ip, op, 64, 2, 2)));
        Assert.Equal(input, output);

        Array.Fill(output, 9f);
        WithBuffer(input, ip => WithBuffer(output, op => DspCallback.PassThrough(ip, op, 64, 1, 2)));
        Assert.All(output, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void APooledGranularVoiceForgetsThePreviousSoundsGrains()
    {
        var state = new GranularVoiceState(new float[1000], 1, 44100);
        state.Grains[3].IsActive = true;
        state.Grains[3].CurrentSample = 120f;
        state.SamplesSinceLastGrain = 400f;
        state.Reset();
        Assert.All(state.Grains, g => Assert.False(g.IsActive));
        Assert.Equal(0f, state.SamplesSinceLastGrain);
    }

    [Fact]
    public void APooledSynthVoiceStartsFromRest()
    {
        var state = new SynthVoiceState { Phase = 0.4f, LfoPhase = 0.9f, EnvValue = 0.1f, Filter_v0 = 0.3f, Filter_v1 = -0.2f };
        state.Reset();
        Assert.Equal(0f, state.Phase);
        Assert.Equal(0f, state.LfoPhase);
        Assert.Equal(1f, state.EnvValue);
        Assert.Equal(0f, state.Filter_v0);
        Assert.Equal(0f, state.Filter_v1);
    }
}
