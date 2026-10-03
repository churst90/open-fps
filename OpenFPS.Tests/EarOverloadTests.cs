using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// What the rest of the world gives way by when a sound arrives louder than the output can go
/// (Loudness.OverloadDb, used by the mixer). "Guns are just a click" (Cody, 2026-10-02): a pistol at
/// a metre and one at thirty both sat at full scale, and nothing told them apart from a hand clap.
/// </summary>
[Collection(nameof(LevelCompressionSetting))]
public class EarOverloadTests
{
    private static float At(float compression, System.Func<float> f)
    {
        float was = Loudness.DynamicRangeCompression;
        try { Loudness.DynamicRangeCompression = compression; return f(); }
        finally { Loudness.DynamicRangeCompression = was; }
    }

    [Fact]
    public void APistolBesideYouOverloadsTheEarAndOneDownTheStreetBarely()
    {
        float c = Loudness.DefaultCompression;
        Assert.Equal(20.25f, At(c, () => Loudness.OverloadDb(157f, 1f)), 1);
        Assert.Equal(11.25f, At(c, () => Loudness.OverloadDb(157f, 10f)), 1);
        Assert.Equal(2.25f, At(c, () => Loudness.OverloadDb(157f, 100f)), 1);
        // Inside a metre is a metre: the level is declared there.
        Assert.Equal(At(c, () => Loudness.OverloadDb(157f, 1f)), At(c, () => Loudness.OverloadDb(157f, 0.3f)));
    }

    [Fact]
    public void EverydayLoudThingsDoNotOverload()
    {
        float c = Loudness.DefaultCompression;
        Assert.Equal(0f, At(c, () => Loudness.OverloadDb(110f, 1f)));   // a jackhammer at a metre
        Assert.Equal(0f, At(c, () => Loudness.OverloadDb(92f, 0.5f)));  // a hand clap
        Assert.Equal(0f, At(c, () => Loudness.OverloadDb(0f, 1f)));     // no level declared
    }

    [Fact]
    public void AWallBetweenTakesItsShare()
    {
        float c = Loudness.DefaultCompression;
        float open = At(c, () => Loudness.OverloadDb(157f, 10f));
        float walled = At(c, () => Loudness.OverloadDb(157f, 10f, pathGain: 0.1f));   // 20 dB through the wall
        Assert.Equal(open - 20f * c, walled, 1);
    }
}
