using System;
using System.Linq;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>Knocking on a door, the beacon tones, and the terminal's ceiling material (2026-09-29).</summary>
public class KnockAndBeaconToneTests
{
    [Fact]
    public void AKnockKeyRoundTripsAndRendersThatManyKnocks()
    {
        Assert.True(DoorKnock.TryParseKey(DoorKnock.Key(3), out int n));
        Assert.Equal(3, n);
        Assert.False(DoorKnock.TryParseKey("knock:0", out _));
        Assert.False(DoorKnock.TryParseKey("clap", out _));

        const int sr = 44100;
        var pcm = DoorKnock.Render(3, sr, 7);
        // Three bursts: count the 5 ms windows that rise past half the peak from below a tenth of it.
        int hop = sr / 200, bursts = 0; bool quiet = true;
        for (int i = 0; i + hop < pcm.Length; i += hop)
        {
            float m = 0f; for (int j = i; j < i + hop; j++) m = MathF.Max(m, MathF.Abs(pcm[j]));
            if (quiet && m > 0.5f) { bursts++; quiet = false; }
            else if (m < 0.1f) quiet = true;
        }
        Assert.Equal(3, bursts);
    }

    [Theory]
    [InlineData("door")] [InlineData("vehicle")] [InlineData("item")]
    [InlineData("exit")] [InlineData("stairs")] [InlineData("waypoint")]
    public void ABeaconIsSoftLowEnoughAndStartsWithoutAClick(string category)
    {
        const int sr = 44100;
        var pcm = BeaconAids.Tone(category, sr);
        Assert.True(pcm.Length > sr / 10 && pcm.Length < sr);
        // No click: the first two milliseconds stay small.
        Assert.True(pcm.Take(sr / 500).Max(MathF.Abs) < 0.25f, "starts with a click");
        // Nothing crossing-chirp high: zero crossings put the note under about 1.2 kHz.
        int zc = 0; for (int i = 1; i < pcm.Length; i++) if ((pcm[i - 1] < 0) != (pcm[i] < 0)) zc++;
        float busiest = zc / 2f / (pcm.Length / (float)sr);
        Assert.True(busiest < 1200f, $"{category} averages {busiest:F0} Hz");
    }

    [Fact]
    public void TheCeilingTileIsARealMaterial()
    {
        AcousticRegistry.Initialize();
        var tile = AcousticRegistry.GetProperties("AcousticTile");
        var generic = AcousticRegistry.GetProperties("Generic");
        Assert.NotEqual(generic.AbsorptionMid, tile.AbsorptionMid);       // not the silent fallback
        Assert.True(tile.AbsorptionMid >= 0.7f && tile.AbsorptionLow < tile.AbsorptionMid);
    }
}
