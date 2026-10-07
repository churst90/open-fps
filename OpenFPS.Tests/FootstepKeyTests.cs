using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// A footstep's key keeps its speed to the half metre a second. It doubled it: a walk at 1.4 m/s
/// named itself "3" and read back at 3 m/s, more than twice its speed (found by the Client.Core
/// housekeeping pass, 2026-10-07; the model is reached only from AudioLab --footsteps).
/// </summary>
public class FootstepKeyTests
{
    [Theory]
    [InlineData(1.4f, 1.5f)]
    [InlineData(1.0f, 1.0f)]
    [InlineData(3.5f, 3.5f)]
    [InlineData(5.2f, 5.0f)]
    public void AStepsKeyKeepsItsSpeed(float speed, float expected)
    {
        var step = new Footstep { Surface = "Concrete", Shoe = Shoe.DressShoe, BodyMassKg = 78f, SpeedMps = speed, Seed = 1 };
        Assert.True(Footsteps.TryParseKey(Footsteps.Key(step), out var back));
        Assert.Equal(expected, back.SpeedMps);
        Assert.Equal(80f, back.BodyMassKg);
    }
}
