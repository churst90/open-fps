using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>The shift chirp's dead band, which Stryker found untested (2026-09-24).</summary>
public class TyreFrictionMutationTests
{
    /// <summary>
    /// A ratio step of a tenth of a per cent is no step: the engine and wheels already agree, and a
    /// full-torque shift through it puts nothing through the tyres. Just past it, the chirp is
    /// (step - 1) × 2.6 × torque.
    /// </summary>
    [Fact]
    public void ARatioStepInsideTheDeadBandDoesNotChirp()
    {
        Assert.Equal(0f, TyreFriction.ShiftChirp(1.001f, 1f));
        Assert.Equal(0.5f * 2.6f, TyreFriction.ShiftChirp(1.5f, 1f), 5);
    }
}
