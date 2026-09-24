using System;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Open ground answers a shot. A shot fired in open desert still has a short wash and a trail after
/// it: the rough ground round the bounce point scattering it back, later the further out. The
/// ground's scatter taps used to be spread across the whole face — for a kilometre of ground, taps
/// hundreds of metres away — and returned nothing, so open ground was silent after the direct sound
/// and its mirror.
/// </summary>
public class GroundScatterTests
{
    private readonly ITestOutputHelper _o;
    public GroundScatterTests(ITestOutputHelper o) => _o = o;

    private static ReflectingSurface Ground(float scattering) =>
        new(new Vector3(0f, 0f, 0f), Vector3.UnitY, new Vector3(500f, 0f, 0f), new Vector3(0f, 0f, 500f),
            0.3f, 1, scattering);

    /// <summary>
    /// Twenty metres apart, a metre and a half up, over dirt: the diffuse returns arrive about 20 ms
    /// and 60 ms after the direct sound, 20 to 40 dB down — the desert recordings fall to about
    /// -30 dB within a few milliseconds and trail on to -40 over the next hundred.
    /// </summary>
    [Fact]
    public void RoughGroundReturnsAShortWashAndATrail()
    {
        var src = new Vector3(0f, 1.5f, 0f);
        var ear = new Vector3(20f, 1.5f, 0f);
        Span<ReflectingSurface> faces = stackalloc ReflectingSurface[] { Ground(0.8f) };
        Span<Reflection> found = stackalloc Reflection[8];
        int n = ImageSource.FirstOrder(faces, src, ear, 343f, found, null, 2);
        var diffuse = found[..n].ToArray().Where(r => r.IsDiffuse).OrderBy(r => r.DelaySeconds).ToArray();
        foreach (var r in diffuse)
            _o.WriteLine($"diffuse: {r.DelaySeconds * 1000f:F0} ms, {20 * MathF.Log10(r.Gain):F1} dB, from {r.BouncePoint}");
        Assert.Equal(2, diffuse.Length);
        Assert.InRange(diffuse[0].DelaySeconds, 0.018f, 0.022f);
        Assert.InRange(diffuse[1].DelaySeconds, 0.055f, 0.065f);
        foreach (var r in diffuse) Assert.InRange(20 * MathF.Log10(r.Gain), -40f, -20f);
        // Later is quieter: the trail falls away.
        Assert.True(diffuse[1].Gain < diffuse[0].Gain);
        // And it comes from the ground near the pair, not from hundreds of metres off.
        Assert.All(diffuse, r => Assert.True(Vector3.Distance(r.BouncePoint, new Vector3(10f, 0f, 0f)) < 40f));
    }

    /// <summary>Smooth ground returns less wash than rough ground: the share that scatters is the
    /// material's, not a constant.</summary>
    [Fact]
    public void SmoothGroundWashesLessThanRough()
    {
        var src = new Vector3(0f, 1.5f, 0f);
        var ear = new Vector3(20f, 1.5f, 0f);
        float Wash(float scattering)
        {
            Span<ReflectingSurface> faces = stackalloc ReflectingSurface[] { Ground(scattering) };
            Span<Reflection> found = stackalloc Reflection[8];
            int n = ImageSource.FirstOrder(faces, src, ear, 343f, found, null, 2);
            return found[..n].ToArray().Where(r => r.IsDiffuse).Sum(r => r.Gain * r.Gain);
        }
        Assert.True(Wash(0.8f) > 2f * Wash(0.2f));
    }

    /// <summary>
    /// A face is near if any part of it is near. Measured from its centre, the city's ground — one
    /// box a kilometre across centred on the middle of the city — was out of reach for anyone more
    /// than 260 m from the middle, and the ground there answered nothing.
    /// </summary>
    [Fact]
    public void AFaceIsAsNearAsItsNearestPart()
    {
        var ground = Ground(0.8f);
        var farOut = new Vector3(450f, 1.5f, 450f);
        Assert.Equal(1.5f * 1.5f, OpenFPS.Client.AudioEngine.Acoustics.EngineReflections.DistanceSquaredToFace(ground, farOut), 3);
        var beyond = new Vector3(510f, 1.5f, 0f);            // 10 m past the edge, 1.5 m up
        Assert.Equal(10f * 10f + 1.5f * 1.5f, OpenFPS.Client.AudioEngine.Acoustics.EngineReflections.DistanceSquaredToFace(ground, beyond), 2);
    }
}
