using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Tests;

/// <summary>
/// The acoustic worker without Steam Audio, as every client without phonon runs it: latest request per
/// source wins, answered by the hand-rolled tracer; when the tracer fails the last real answer is kept.
/// The worker is a real thread; tests wait (bounded) for answers and never time it.
/// </summary>
public class AsyncAcousticWorkerTests
{
    private static readonly Vector3 Ear = new(0f, 1.6f, 0f);
    /// <summary>40 m north, behind a 10 m brick wall 10 m north of the listener.</summary>
    private static readonly Vector3 Behind = new(0f, 1f, -40f);

    private static WorldSnapshot Walled()
    {
        AcousticRegistry.Initialize();
        var w = new ClientWorldState();
        w.Clear(new Vector3(400, 100, 400));
        w.RegisterDefinition(new EntityDefinition
        {
            EntityId = 9001,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0f, 5f, -10f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(60f, 10f, 0.35f), IsSolid = true },
            Material = new MaterialComponent { Material = "Brick" },
        });
        return w.GetSnapshot();
    }

    private static AcousticRequest Ask(int id, Vector3 source) => new()
    {
        EntityId = id, ListenerPos = Ear, SourcePos = source, SourceRadius = AudioEmission.MinOcclusionRadius,
    };

    /// <summary>Waits (bounded, never timed) for the worker to have done something.</summary>
    private static void Await(Func<bool> done, string what)
    {
        for (int i = 0; i < 4000; i++)
        {
            if (done()) return;
            Thread.Sleep(5);
        }
        Assert.Fail("the worker never " + what);
    }

    private static List<AcousticPathData> ResultOf(AsyncAcousticWorker w, int id)
    {
        Assert.True(w.TryGetResult(id, out var paths));
        return paths;
    }

    /// <summary>Asked three times about one source, the worker answers once, the tracer's answer for the
    /// last place, each path stamped with that source position (so a pooled voice id can tell its own
    /// answer from a previous occupant's).</summary>
    [Fact]
    public void TheLatestRequestForASourceIsAnsweredByTheTracerAndStampedWithWhereItWas()
    {
        var world = Walled();
        using var worker = new AsyncAcousticWorker(new SpatialAcoustics());
        worker.UpdateWorld(world);
        var last = Behind + new Vector3(4f, 0f, 0f);
        worker.EnqueueRequest(Ask(7, Behind + new Vector3(-8f, 0f, 0f)));
        worker.EnqueueRequest(Ask(7, Behind));
        worker.EnqueueRequest(Ask(7, last));
        worker.Start();
        Await(() => worker.TryGetResult(7, out _), "answered the source");

        var paths = ResultOf(worker, 7);
        Assert.False(worker.SteamAudioActive, "these tests are of the path without Steam Audio");
        Assert.All(paths, p => Assert.Equal(last, p.SourcePosition));

        var expected = new SpatialAcoustics().CalculateAcousticPaths(world, 7, Ear, last);
        Assert.Equal(expected.Count, paths.Count);
        var mine = paths.First(p => !p.IsReflection);
        var theirs = expected.First(p => !p.IsReflection);
        Assert.Equal(theirs.Occlusion, mine.Occlusion);
        Assert.Equal(theirs.EqLow, mine.EqLow);
        Assert.Equal(theirs.EqMid, mine.EqMid);
        Assert.Equal(theirs.EqHigh, mine.EqHigh);
        Assert.Equal(theirs.ApparentPosition, mine.ApparentPosition);
        Assert.True(mine.Occlusion > 0f, "a car behind a brick wall is behind it");
    }

    /// <summary>Without Steam Audio nothing simulated is reported: no reverb, an open even-coloured room,
    /// nothing for a one-shot to borrow, and no "degraded" flag for a simulator that never ran.</summary>
    [Fact]
    public void WithoutSteamAudioNothingSimulatedIsClaimed()
    {
        using var worker = new AsyncAcousticWorker(new SpatialAcoustics());
        worker.UpdateWorld(Walled());
        worker.EnqueueRequest(Ask(7, Behind));
        worker.Start();
        worker.Start();     // a second Start is the same worker
        Await(() => worker.TryGetResult(7, out _), "answered the source");

        Assert.False(worker.SteamAudioActive);
        Assert.False(worker.PathingReady);
        Assert.False(worker.IsDegraded);
        Assert.False(worker.TryGetListenerReverbDecayMs(out float ms));
        Assert.Equal(0f, ms);
        Assert.Equal(0f, worker.ListenerEnclosure);
        Assert.Equal(1f, worker.ListenerHfDecayRatio);
        Assert.Equal(1f, worker.ListenerLfDecayRatio);
        Assert.False(worker.TryGetNearby(Ear, Behind, out _));
        Assert.Equal("no simulation runs yet", worker.RayBudgetSummary);
    }

    /// <summary>A forgotten source's answer is dropped and a new request answered afresh; the worker
    /// holds the last world given.</summary>
    [Fact]
    public void AForgottenSourceHasNoAnswerUntilItIsAskedAgain()
    {
        var world = Walled();
        using var worker = new AsyncAcousticWorker(new SpatialAcoustics());
        Assert.Null(worker.GetLastWorld());
        worker.UpdateWorld(world);
        Assert.Same(world, worker.GetLastWorld());
        worker.EnqueueRequest(Ask(7, Behind));
        worker.Start();
        Await(() => worker.TryGetResult(7, out _), "answered the source");

        worker.Forget(7);
        Assert.False(worker.TryGetResult(7, out _));
        worker.Forget(7);       // and again is harmless

        var moved = Behind + new Vector3(2f, 0f, 0f);
        worker.EnqueueRequest(Ask(7, moved));
        Await(() => worker.TryGetResult(7, out var p) && p[0].SourcePosition == moved, "answered the source again");
    }

    /// <summary>A request made before there is a world is dropped, not held.</summary>
    [Fact]
    public void ARequestWithNoWorldIsDroppedNotHeld()
    {
        using var worker = new AsyncAcousticWorker(new SpatialAcoustics());
        worker.EnqueueRequest(Ask(1, Behind));
        worker.Start();

        var queue = (ConcurrentQueue<AcousticRequest>)Field(worker, "_requestQueue");
        var pending = (Dictionary<int, AcousticRequest>)Field(worker, "_pending");
        Await(() => queue.IsEmpty && pending.Count == 0, "drained the queue");

        worker.UpdateWorld(Walled());
        worker.EnqueueRequest(Ask(2, Behind));
        Await(() => worker.TryGetResult(2, out _), "answered the later source");
        Assert.False(worker.TryGetResult(1, out _), "a request with no world to answer it was kept");
    }

    /// <summary>When the tracer fails a source keeps its last answer (a reset to clear would remove every
    /// wall); only a source that never had one gets a clear path at its own position.</summary>
    [Fact]
    public void WhenTheTracerFailsASourceKeepsItsLastAnswer()
    {
        using var worker = new AsyncAcousticWorker(new SpatialAcoustics());
        worker.UpdateWorld(Walled());
        worker.EnqueueRequest(Ask(7, Behind));
        worker.Start();
        Await(() => worker.TryGetResult(7, out _), "answered the source");
        float occlusion = ResultOf(worker, 7).First(p => !p.IsReflection).Occlusion;
        Assert.True(occlusion > 0f);

        // A world the tracer cannot read: an entity with no definition throws inside it.
        var broken = new WorldSnapshot();
        broken.Entities[123] = new EntitySnapshot { Id = 123, Definition = null!, Transform = new Transform() };
        worker.UpdateWorld(broken);

        var moved = Behind + new Vector3(1f, 0f, 0f);
        var fresh = new Vector3(20f, 1f, -5f);
        worker.EnqueueRequest(Ask(7, moved));
        worker.EnqueueRequest(Ask(8, fresh));
        Await(() => worker.TryGetResult(8, out _) && ResultOf(worker, 7)[0].SourcePosition == moved,
              "answered against the broken world");

        var kept = ResultOf(worker, 7).First(p => !p.IsReflection);
        Assert.Equal(occlusion, kept.Occlusion);

        var clear = Assert.Single(ResultOf(worker, 8));
        Assert.False(clear.IsReflection);
        Assert.Equal(0f, clear.Occlusion);
        Assert.Equal(1f, clear.EqLow);
        Assert.Equal(1f, clear.EqMid);
        Assert.Equal(1f, clear.EqHigh);
        Assert.Equal(fresh, clear.ApparentPosition);
        Assert.Equal(fresh, clear.SourcePosition);
        Assert.Equal(Vector3.Distance(Ear, fresh), clear.EffectiveDistance, 4);
    }

    /// <summary>Disposed without ever being started, the worker has nothing to join and nothing to free.</summary>
    [Fact]
    public void AWorkerNeverStartedDisposesCleanly()
    {
        var worker = new AsyncAcousticWorker(new SpatialAcoustics());
        worker.EnqueueRequest(Ask(1, Behind));
        worker.Dispose();
        Assert.False(worker.TryGetResult(1, out _));
    }

    private static object Field(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
}
