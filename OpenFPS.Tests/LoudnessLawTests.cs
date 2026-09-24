using System.Linq;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The parts of the loudness law the mutation run of 2026-09-24 found unchecked: where the fade at
/// the edge of a voice's range starts and how far it has got, and which blast level each cartridge
/// is given.
/// </summary>
public class LoudnessLawTests
{
    /// <summary>
    /// Inverse distance out from the reference, then a fade over the last quarter of the span between
    /// the reference and the range: whole at the start of that quarter, half way down in its middle,
    /// nothing at the range.
    /// </summary>
    [Fact]
    public void TheEdgeFadeTakesTheLastQuarterOfTheSpan()
    {
        const float reference = 2f, range = 102f;          // span 100: the fade runs 77..102
        float Fade(float d) => Loudness.RenderedGain(1f, reference, range, d) / (reference / d);
        Assert.Equal(1f, Fade(77f), 3);
        Assert.Equal(0.5f, Fade(89.5f), 3);
        Assert.Equal(0f, Loudness.RenderedGain(1f, reference, range, range), 5);
        // Inside the reference distance a source is fully itself.
        Assert.Equal(0.7f, Loudness.RenderedGain(0.7f, reference, range, 1f), 5);
        Assert.Equal(0.35f, Loudness.RenderedGain(0.7f, reference, range, 4f), 5);
    }

    [Fact]
    public void EachCartridgeHasItsOwnBlastAndAnUnknownOneIsARifle()
    {
        var any = WeaponRegistry.All.First();
        float For(string cartridge) => Loudness.MuzzleBlastDb(any with { Cartridge = cartridge });
        Assert.Equal(Loudness.Rifle556Db, For("5.56x45mm"));
        Assert.Equal(Loudness.Rifle762Db, For("7.62x39mm"));
        Assert.Equal(Loudness.Pistol9mmDb, For("9x19mm"));
        Assert.Equal(Loudness.Pistol45Db, For(".45 ACP"));
        Assert.Equal(Loudness.Shotgun12GaugeDb, For("12 gauge 00 buck"));
        Assert.Equal(Loudness.Rifle762Db, For("a cartridge nobody has heard of"));
        Assert.NotEqual(Loudness.Rifle762Db, For("5.56x45mm"));
        Assert.NotEqual(Loudness.Rifle762Db, For(".45 ACP"));
    }
}
