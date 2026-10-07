using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Tests;

/// <summary>
/// What the traced reverb stage takes from its bus. The bus is stereo in the game (the stage reports
/// two input channels with only mono claps sending), and FMOD upmixes a mono send to it at -3.01 dB a
/// channel (AudioLab --probable-bugs scene=upmix). Averaging the two channels gave the tracer every mono
/// source 3 dB low, so the tail sat 3 dB under the trim Cody set; summed at constant power it gets the
/// source whole (probable bug found by the todo audit, 2026-10-07).
/// </summary>
public class TracedReverbInputTests
{
    [Fact]
    public void AMonoSendUpmixedToStereoReachesTheTracerWhole()
    {
        float upmix = MathF.Pow(10f, -3.0103f / 20f);   // each channel, as FMOD measured
        float x = 0.5f;
        float mono = (upmix * x + upmix * x) * TracedReverbDsp.DownmixGain(2);
        Assert.Equal(x, mono, 4);
    }

    [Fact]
    public void AMonoBusIsTakenAsItIs() => Assert.Equal(1f, TracedReverbDsp.DownmixGain(1));
}
