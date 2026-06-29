using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Tests;

/// <summary>Tests for the pure Doppler helper used by Steam Audio (2D-channel) voices.</summary>
public class AudioPhysicsTests
{
    private static readonly Vector3 SrcPos = Vector3.Zero;
    private static readonly Vector3 ListenerPos = new(10, 0, 0); // unit vector source->listener = +x

    [Fact]
    public void Doppler_BothStationary_IsUnity()
    {
        float f = AudioPhysics.DopplerFactor(ListenerPos, Vector3.Zero, SrcPos, Vector3.Zero);
        Assert.Equal(1f, f, 4);
    }

    [Fact]
    public void Doppler_SourceApproaching_PitchesUp()
    {
        // Source moving toward the listener (+x).
        float f = AudioPhysics.DopplerFactor(ListenerPos, Vector3.Zero, SrcPos, new Vector3(20, 0, 0));
        Assert.True(f > 1f, $"expected >1, got {f}");
    }

    [Fact]
    public void Doppler_SourceReceding_PitchesDown()
    {
        float f = AudioPhysics.DopplerFactor(ListenerPos, Vector3.Zero, SrcPos, new Vector3(-20, 0, 0));
        Assert.True(f < 1f, $"expected <1, got {f}");
    }

    [Fact]
    public void Doppler_ListenerApproaching_PitchesUp()
    {
        // Listener moving toward the source (−x).
        float f = AudioPhysics.DopplerFactor(ListenerPos, new Vector3(-20, 0, 0), SrcPos, Vector3.Zero);
        Assert.True(f > 1f, $"expected >1, got {f}");
    }

    [Fact]
    public void Doppler_PerpendicularMotion_IsUnity()
    {
        // Source moving along +z has no radial component along the +x source->listener axis.
        float f = AudioPhysics.DopplerFactor(ListenerPos, Vector3.Zero, SrcPos, new Vector3(0, 0, 30));
        Assert.Equal(1f, f, 4);
    }

    [Fact]
    public void Doppler_CoincidentPositions_IsUnity()
    {
        float f = AudioPhysics.DopplerFactor(Vector3.Zero, new Vector3(5, 0, 0), Vector3.Zero, Vector3.Zero);
        Assert.Equal(1f, f, 4);
    }

    [Fact]
    public void Doppler_ExtremeClosing_IsClampedToMax()
    {
        float f = AudioPhysics.DopplerFactor(ListenerPos, new Vector3(-1000, 0, 0), SrcPos, new Vector3(1000, 0, 0));
        Assert.Equal(2.0f, f, 4); // clamped to the default max
    }
}
