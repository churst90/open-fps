using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;
using OpenFPS.Common.Geometry;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A voice's kept route answer holds the graph it was asked of, and the graph its scene: a new graph must
/// let the old one go, or a drive across a streamed map keeps every graph it ever built (the Resonance
/// team's 400 MB a kilometre, 2026-10-06).
/// </summary>
public class RouteAnswersTests
{
    private static OpeningRoutes Graph(int seed)
    {
        var builder = new TriangleWorldBuilder(250f);
        var solids = new List<SolidSpec>();
        var rng = new Random(seed);
        for (int i = 0; i < 50; i++)
        {
            var size = new Vector3(1 + (float)rng.NextDouble() * 5, 3, 0.2f);
            solids.Add(new SolidSpec(i + 1, new Vector3((float)rng.NextDouble() * 400, 1.5f, (float)rng.NextDouble() * 400), Quaternion.Identity, size,
                                     EntityGeometry.SurfaceOf("Brick", size, 0, 0, false, 0, 0, false, false, false, null)));
        }
        var world = builder.Build(solids, Array.Empty<SolidSpec>());
        return OpeningRoutes.Build(world, null, Array.Empty<OpeningRoutes.Declared>(), null, new OpeningRoutes.TileCache());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Graph, OpeningRoutes Next) AnswerThenSupersede(RouteAnswers answers)
    {
        var old = Graph(1);
        answers.Published(old);
        for (int id = 0; id < 20; id++)
            answers.Put(id, new RouteAnswers.Held(old, Vector3.Zero, Vector3.One, null, 0));
        var next = Graph(2);
        answers.Published(next);
        return (new WeakReference(old), next);
    }

    [Fact]
    public void ANewGraphLetsTheOldOneGo()
    {
        var answers = new RouteAnswers();
        var (weak, next) = AnswerThenSupersede(answers);
        for (int i = 0; i < 3 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive, "a superseded graph is still held by the kept answers");
        Assert.Equal(0, answers.Count);
        // And the cache still works on the new one.
        answers.Put(7, new RouteAnswers.Held(next, Vector3.Zero, Vector3.One, null, 0));
        Assert.True(answers.TryGet(7, next, out _));
    }

    [Fact]
    public void AnAnswerAboutAnOldGraphIsNotKept()
    {
        var answers = new RouteAnswers();
        var a = Graph(1); var b = Graph(2);
        answers.Published(a);
        answers.Published(b);
        answers.Put(1, new RouteAnswers.Held(a, Vector3.Zero, Vector3.One, null, 0));
        Assert.Equal(0, answers.Count);
        answers.Put(2, new RouteAnswers.Held(b, Vector3.Zero, Vector3.One, null, 0));
        answers.Forget(2);
        Assert.Equal(0, answers.Count);
    }
}
