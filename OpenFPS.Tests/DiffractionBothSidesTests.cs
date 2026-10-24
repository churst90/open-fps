using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Acoustics;
using OpenFPS.Materials;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A source under the middle of a wide deck, the listener walking across it 1 m above: the shortest way
/// round the deck changes from one side to the other over the middle, so a single chosen edge makes the
/// heard direction jump a hundred degrees between two 10 cm steps. A real listener over a footbridge hears
/// the water from both sides at once (inbox/2026-10-18-resonance-host-needs, the footbridge).
/// </summary>
public class DiffractionBothSidesTests
{
    private readonly ITestOutputHelper _o;
    public DiffractionBothSidesTests(ITestOutputHelper o) => _o = o;

    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-6f, g));

    /// <summary>Bearing from straight down towards +X, degrees: the direction the voice is given.</summary>
    private static float BearingDegrees(Vector3 ear, Vector3 edge)
        => MathF.Atan2(edge.X - ear.X, ear.Y - edge.Y) * 180f / MathF.PI;

    private static OpeningRoutes Deck()
    {
        AcousticRegistry.EnsureInitialized();
        var solids = new List<OpeningRoutes.Solid>
        {
            new(new Vector3(0f, 1f, 0f), new Vector3(3f, 0.2f, 10f), Quaternion.Identity, "Concrete"),
        };
        return OpeningRoutes.Build(solids, null, Array.Empty<OpeningRoutes.Declared>());
    }

    [Fact]
    public void ASourceUnderAWideDeckIsHeardFromBothSides()
    {
        var routes = Deck();
        var source = new Vector3(0f, 0.4f, 0f);
        float previous = float.NaN, worstStep = 0f;
        float worstAt = 0f, worstDb = 0f;
        var previousGains = Vector3.Zero;
        bool left = false, right = false;

        for (float x = -1.4f; x <= 1.4f + 1e-4f; x += 0.1f)
        {
            var ear = new Vector3(x, 2.1f, 0f);
            var gains = routes.BarrierGains(source, ear, WallTransmission.SoundSpeed,
                                            out float delta, out bool verified, out Vector3 edge, out bool found);
            float bearing = BearingDegrees(ear, edge);
            _o.WriteLine($"x {x,5:F1}  {Db(gains.X),6:F1}/{Db(gains.Y),6:F1}/{Db(gains.Z),6:F1} dB  "
                        + $"delta {delta:F3} verified {verified}  "
                        + $"edge ({edge.X:F2}, {edge.Y:F2}, {edge.Z:F2})  bearing {bearing,7:F2}");
            if (x <= -0.5f && bearing < 0f) left = true;
            if (x >= 0.5f && bearing > 0f) right = true;
            // Near an edge it is heard from that edge: compare with the shortest way's own crossing.
            if (MathF.Abs(x) >= 1.3f)
            {
                routes.BarrierPathDifference(source, ear, out var sideEdge, out _);
                float edgeBearing = BearingDegrees(ear, sideEdge);
                Assert.True(MathF.Abs(bearing - edgeBearing) < 15f,
                    $"at x {x:F1} the way round leaves from bearing {edgeBearing:F1}, heard at {bearing:F1}");
            }
            if (!float.IsNaN(previous))
            {
                float step = MathF.Abs(bearing - previous);
                if (step > worstStep) { worstStep = step; worstAt = x; }
            }
            if (!float.IsNaN(previous))
                worstDb = MathF.Max(worstDb, MathF.Max(MathF.Abs(Db(gains.X) - Db(previousGains.X)),
                    MathF.Max(MathF.Abs(Db(gains.Y) - Db(previousGains.Y)), MathF.Abs(Db(gains.Z) - Db(previousGains.Z)))));
            previousGains = gains;
            previous = bearing;
        }

        _o.WriteLine($"largest step {worstStep:F1} degrees at x {worstAt:F1}; left {left}, right {right}");
        Assert.True(worstStep < 20f,
            $"the heard direction stepped {worstStep:F1} degrees between 10 cm steps, near x {worstAt:F1}");
        Assert.True(worstDb < 1.5f, $"a band stepped {worstDb:F1} dB between 10 cm steps");
        Assert.True(left && right, "the direction must span both sides of the deck");
    }

    /// <summary>Walking away from the deck the second side fades out; it must not be cut off in a step.</summary>
    [Fact]
    public void WalkingAwayTheSecondSideFadesWithoutAStep()
    {
        var routes = Deck();
        var source = new Vector3(0f, 0.4f, 0f);
        float previous = float.NaN;
        for (float r = 4f; r <= 60f; r += 0.5f)
        {
            // 30 degrees up from the source, so the line goes through the deck at every distance.
            var ear = source + new Vector3(0.866f, 0.5f, 0f) * r;
            var gains = routes.BarrierGains(source, ear, WallTransmission.SoundSpeed, out _, out _, out _, out bool found);
            float db = Db(gains.Y);
            _o.WriteLine($"r {r,5:F1}  {Db(gains.X),6:F1}/{db,6:F1}/{Db(gains.Z),6:F1} dB found {found}");
            Assert.True(found, $"no way round at {r:F1} m");
            // A 0.5 m step is 0.8 dB at 4 m, so a step above 2 dB is a switch.
            if (!float.IsNaN(previous)) Assert.True(MathF.Abs(db - previous) < 2f, $"mid gain stepped {db - previous:F1} dB at {r:F1} m");
            previous = db;
        }
    }
}
