using System;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The diffuse field has a level relative to the direct sound, and it depends on how far the source
/// is — the room equation, with the room's surface and absorption replaced by what the rays measured
/// (Enclosure.ReverberantToDirectPower). Before this, one constant send for every source at every
/// distance put a footstep's reverberation over the step itself.
/// </summary>
public class CriticalDistanceTests
{
    private static float Db(float power) => 10f * MathF.Log10(MathF.Max(1e-12f, power));

    [Fact]
    public void OpenGroundHasNoDiffuseField()
    {
        Assert.Equal(0f, Enclosure.ReverberantToDirectPower(0f, 1.7f, 1.6f));
    }

    [Fact]
    public void AStepInARooflessHallSitsUnderItsDirectSound()
    {
        // The wood room as surveyed: 46 % enclosed, mean free path 4.1 m; a footstep 1.6 m below the ear.
        float db = Db(Enclosure.ReverberantToDirectPower(0.46f, 4.1f, 1.6f));
        Assert.InRange(db, -8f, 0f);
    }

    [Fact]
    public void TheSameStepInASealedConcreteCellIsOverIt()
    {
        float db = Db(Enclosure.ReverberantToDirectPower(0.97f, 4.1f, 1.6f));
        Assert.True(db > 6f, $"a bare concrete cell reverberates a step well over the step; read {db:F1} dB");
    }

    [Fact]
    public void FurtherAwayMeansMoreOfTheRoom()
    {
        float near = Enclosure.ReverberantToDirectPower(0.46f, 4.1f, 1f);
        float far = Enclosure.ReverberantToDirectPower(0.46f, 4.1f, 4f);
        Assert.True(far > near * 8f, "the ratio grows with the square of the distance");
    }

    [Fact]
    public void ASourceBeyondTheRoomsScaleStopsGrowing()
    {
        float atThree = Enclosure.ReverberantToDirectPower(0.46f, 4.1f, 3f * 4.1f);
        float atTwenty = Enclosure.ReverberantToDirectPower(0.46f, 4.1f, 20f * 4.1f);
        Assert.Equal(atThree, atTwenty, 3);
    }

    [Fact]
    public void ABiggerHallHasAQuieterFieldForTheSameSource()
    {
        // Same enclosure, twice the mean free path: four times the surface to spread the energy over.
        float small = Enclosure.ReverberantToDirectPower(0.6f, 4f, 2f);
        float big = Enclosure.ReverberantToDirectPower(0.6f, 8f, 2f);
        Assert.True(big < small * 0.3f);
    }
}
