using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Geometry;
using OpenFPS.Acoustics;

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

    /// <summary>A worker that never starts Steam Audio, even in a run that has loaded it (the opt-in Steam
    /// Audio tests): these tests are of the hand-rolled tracer.</summary>
    private static AsyncAcousticWorker Unsimulated()
        => new(new SpatialAcoustics()) { Lab = new LabLevers { WithoutSteamAudio = true } };

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
        using var worker = Unsimulated();
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
        using var worker = Unsimulated();
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
        using var worker = Unsimulated();
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

    /// <summary>A request made before there is a world is dropped, not held. Stepped by hand: watched from
    /// another thread, an empty queue and an empty pending list were also what the worker shows between taking
    /// a request off the queue and filing it, and a world given then answered the request (GitHub, 2026-10-11).</summary>
    [Fact]
    public void ARequestWithNoWorldIsDroppedNotHeld()
    {
        using var worker = new AsyncAcousticWorker(new SpatialAcoustics()) { Manual = true };
        worker.EnqueueRequest(Ask(1, Behind));
        worker.StepForTest();

        var queue = (ConcurrentQueue<AcousticRequest>)Field(worker, "_requestQueue");
        var pending = (Dictionary<int, AcousticRequest>)Field(worker, "_pending");
        Assert.True(queue.IsEmpty && pending.Count == 0, "the pass with no world kept the request");

        worker.UpdateWorld(Walled());
        worker.EnqueueRequest(Ask(2, Behind));
        worker.StepForTest();
        Assert.True(worker.TryGetResult(2, out _), "the later source was not answered");
        Assert.False(worker.TryGetResult(1, out _), "a request with no world to answer it was kept");
    }

    /// <summary>When the tracer fails a source keeps its last answer (a reset to clear would remove every
    /// wall); only a source that never had one gets a clear path at its own position.</summary>
    [Fact]
    public void WhenTheTracerFailsASourceKeepsItsLastAnswer()
    {
        using var worker = Unsimulated();
        worker.UpdateWorld(Walled());
        worker.EnqueueRequest(Ask(7, Behind));
        worker.Start();
        Await(() => worker.TryGetResult(7, out _), "answered the source");
        float occlusion = ResultOf(worker, 7).First(p => !p.IsReflection).Occlusion;
        Assert.True(occlusion > 0f);

        // A world the tracer cannot read: every question of it throws inside the tracer.
        worker.UpdateWorld(new UnreadableWorld());

        var moved = Behind + new Vector3(1f, 0f, 0f);
        var fresh = new Vector3(20f, 1f, -5f);
        worker.EnqueueRequest(Ask(7, moved));
        worker.EnqueueRequest(Ask(8, fresh));
        // Asked after 7, so answered no sooner.
        Await(() => worker.TryGetResult(8, out _), "answered against the broken world");

        // Kept as it was, still saying where it was traced: not passed off as an answer for where 7 is now.
        var kept = ResultOf(worker, 7).First(p => !p.IsReflection);
        Assert.Equal(occlusion, kept.Occlusion);
        Assert.Equal(Behind, kept.SimulatedFrom);
        Assert.False(kept.Answers(moved + new Vector3(2f, 0f, 0f), 0f, Ear, 0f));

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

    /// <summary>An answer says what traced it, for which source position and from which ear, so an asker can
    /// refuse one that is not for here (AcousticPathData.Answers).</summary>
    [Fact]
    public void AnAnswerSaysWhatTracedItAndFromWhere()
    {
        using var worker = new AsyncAcousticWorker(new SpatialAcoustics()) { Manual = true };
        worker.UpdateWorld(Walled());
        worker.EnqueueRequest(Ask(7, Behind));
        worker.StepForTest();

        Assert.All(ResultOf(worker, 7), p =>
        {
            Assert.Equal(Behind, p.SimulatedFrom);
            Assert.Equal(Ear, p.ListenerPosition);
            Assert.Equal(PathProvenance.HandRolled, p.Provenance);
        });
        Assert.True(worker.TryGetResultFor(7, Behind, 0f, Ear, 0f, out _));
        Assert.False(worker.TryGetResultFor(7, Behind + new Vector3(30f, 0f, 0f), 0f, Ear, 0f, out _), "another place's source");
        Assert.False(worker.TryGetResultFor(7, Behind, 0f, Ear + new Vector3(0f, 0f, 30f), 0f, out _), "another place's ear");
    }

    /// <summary>
    /// One ear a tick, the newest: the simulator traces every source against one listener, so requests
    /// drained together from frames either side of a teleport are all answered, and stamped, for where the
    /// ear is now. Taken from whichever came first, a tick traced them all from the old place.
    /// </summary>
    [Fact]
    public void RequestsDrainedTogetherAreAnsweredForTheNewestEar()
    {
        using var worker = new AsyncAcousticWorker(new SpatialAcoustics()) { Manual = true };
        worker.UpdateWorld(Walled());
        var before = Ear + new Vector3(0f, 0f, -60f);   // beyond the wall, beside the source
        worker.EnqueueRequest(Ask(7, Behind) with { ListenerPos = before });
        worker.EnqueueRequest(Ask(8, Behind + new Vector3(2f, 0f, 0f)));
        worker.StepForTest();

        Assert.Equal(Ear, ResultOf(worker, 7)[0].ListenerPosition);
        Assert.Equal(Ear, ResultOf(worker, 8)[0].ListenerPosition);
        Assert.True(ResultOf(worker, 7).First(p => !p.IsReflection).Occlusion > 0f, "traced from the ear's side of the wall");
    }

    /// <summary>Disposed without ever being started, the worker has nothing to join and nothing to free.</summary>
    [Fact]
    public void AWorkerNeverStartedDisposesCleanly()
    {
        var worker = Unsimulated();
        worker.EnqueueRequest(Ask(1, Behind));
        worker.Dispose();
        Assert.False(worker.TryGetResult(1, out _));
    }

    private static object Field(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    /// <summary>A world input whose every answer throws: what the tracer meets in a world it cannot read.</summary>
    private sealed class UnreadableWorld : IAcousticWorld
    {
        private static T No<T>() => throw new InvalidOperationException("an unreadable world");
        public OpenFPS.Geometry.TriangleWorld? Geometry => No<OpenFPS.Geometry.TriangleWorld?>();
        public IReadOnlySet<int>? GeometryStale => No<IReadOnlySet<int>?>();
        public long GeometryVersion => No<long>();
        public float TileMetres => No<float>();
        public AcousticMap? Rooms => No<AcousticMap?>();
        public AirState Air => No<AirState>();
        public int BodyCount => No<int>();
        public bool TryGetBody(int id, out AcousticBody body) { body = default; return No<bool>(); }
        public void AllBodies(List<AcousticBody> into) => No<bool>();
        public int MovingCount => No<int>();
        public AcousticBody MovingAt(int index) => No<AcousticBody>();
        public bool Near(Vector3 centre, float radius, bool staticOnly, List<AcousticBody> into) => No<bool>();
        public void Unindexed(bool staticOnly, List<AcousticBody> into) => No<bool>();
        public void DoorLeaves(List<DoorLeaf> into) => No<bool>();
        public List<OpenFPS.Geometry.SolidSpec> TerrainSolids() => No<List<OpenFPS.Geometry.SolidSpec>>();
    }
}
