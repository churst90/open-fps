using System;
using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Tests;

/// <summary>
/// Cover for step 6 of the engineering audit — "profile, then cut the hot paths".
///
/// The theme is repeated work: the same answer computed several times over, because nothing remembered it
/// and nothing enforced how often it was allowed to be asked for. A frame built three to six identical
/// copies of the world. The audio system ran at whatever rate the network poll happened to spin at, three
/// times faster than anything it produces can be heard. The server probed for the floor once per input
/// rather than once per position. Every grid query walked its cells twice and handed back a wall spanning
/// four cells four times over. Every per-voice audio call scanned the whole list of playing voices.
///
/// What is tested here is each of those rules, at the level the rule lives at. Two things are verified by
/// inspection rather than by test: the FMOD active-voice index (which cannot be built without the native
/// library) and the measured Steam Audio ray budget (which needs a real simulation run). The arithmetic
/// under both — an id-keyed index, and a timing report — is straightforward; what could not be checked
/// mechanically is called out here rather than left implied.
/// </summary>
public class HotPathTests
{
    // ── One snapshot per version of the world ───────────────────────────────────────────────────

    [Fact]
    public void RepeatedSnapshotRequestsShareOneBuild()
    {
        var world = LoadedWorld();
        long before = world.SnapshotBuilds;

        var first = world.GetSnapshot();
        var second = world.GetSnapshot();
        var third = world.GetSnapshot();

        // Not merely equal — the SAME object. Prediction, the shelter raycast, the proximity scan and the
        // audio system all read the world within one frame; each of those was a full copy of every
        // definition and transform in the map.
        Assert.Same(first, second);
        Assert.Same(second, third);
        Assert.Equal(1, world.SnapshotBuilds - before);
    }

    [Fact]
    public void ChangingTheWorldRebuildsTheSnapshot()
    {
        var world = LoadedWorld();
        var before = world.GetSnapshot();
        long builds = world.SnapshotBuilds;

        world.RegisterDefinition(Wall(id: 99, new Vector3(9, 1, 9)));
        var after = world.GetSnapshot();

        Assert.NotSame(before, after);
        Assert.Equal(1, world.SnapshotBuilds - builds);
        Assert.Contains(99, after.Entities.Keys);
        // The stale copy still describes the world as it was — which is exactly why it had to be replaced
        // rather than edited: the audio worker may still be reading it on its own thread.
        Assert.DoesNotContain(99, before.Entities.Keys);
    }

    [Fact]
    public void EveryKindOfChangeInvalidatesTheSnapshot()
    {
        // A mutation that forgot to bump the version would serve a snapshot that silently never updates —
        // a far worse failure than the copying it was meant to save. One case per mutation path.
        var world = LoadedWorld();

        long version = world.Version;
        world.RegisterDefinition(Wall(id: 51, new Vector3(1, 1, 1)));
        Assert.NotEqual(version, world.Version);

        version = world.Version;
        world.SyncState(new[] { new EntityState { EntityId = 51, Transform = new QuantizedTransform() } });
        Assert.NotEqual(version, world.Version);

        version = world.Version;
        world.RemoveEntities(new[] { 51 });
        Assert.NotEqual(version, world.Version);

        version = world.Version;
        world.UpdateAtmosphere(new WorldStateUpdate { Temperature = 3f });
        Assert.NotEqual(version, world.Version);

        version = world.Version;
        world.SetAcousticMap(new AcousticMap());
        Assert.NotEqual(version, world.Version);
    }

    [Fact]
    public void RemovingNothingDoesNotInvalidateTheSnapshot()
    {
        var world = LoadedWorld();
        var before = world.GetSnapshot();

        world.RemoveEntities(new[] { 4242 }); // never existed

        Assert.Same(before, world.GetSnapshot());
    }

    // ── Grid queries: one walk, one copy of each item ───────────────────────────────────────────

    [Fact]
    public void CollectInRadiusReturnsAMultiCellObjectOnce()
    {
        var grid = new SpatialGrid<int>(new Vector2(-50, -50), new Vector2(50, 50), cellSize: 10f);

        // A 35 m wall spans four cells, so it is filed in four of them. The old iterator handed it back
        // four times and every caller then ray-tested it four times.
        grid.AddOverlapping(new Vector3(0, 1, 0), new Vector3(35, 4, 2), item: 7, isStatic: true);

        int repeats = 0;
        foreach (int id in grid.GetItemsInRadius(new Vector3(0, 1, 0), 20f))
            if (id == 7) repeats++;
        Assert.True(repeats > 1, "precondition: the wall should be filed in several cells");

        var into = new List<int>();
        var seen = new HashSet<int>();
        grid.CollectInRadius(new Vector3(0, 1, 0), 20f, into, seen);

        Assert.Equal(new[] { 7 }, into);
    }

    [Fact]
    public void CollectInRadiusFindsBothStaticAndDynamicItems()
    {
        var grid = new SpatialGrid<int>(new Vector2(-50, -50), new Vector2(50, 50), cellSize: 10f);
        grid.AddOverlapping(new Vector3(0, 1, 0), new Vector3(2, 2, 2), item: 1, isStatic: true);
        grid.Add(new Vector3(3, 1, 3), item: 2, isStatic: false);

        var into = new List<int>();
        grid.CollectInRadius(new Vector3(0, 1, 0), 12f, into, new HashSet<int>());

        Assert.Contains(1, into);
        Assert.Contains(2, into);
    }

    [Fact]
    public void StaticVersionMovesOnlyWithStaticGeometry()
    {
        var grid = new SpatialGrid<int>(new Vector2(-50, -50), new Vector2(50, 50), cellSize: 10f);

        int version = grid.StaticVersion;
        grid.Add(new Vector3(1, 1, 1), item: 1, isStatic: false);
        grid.Clear();
        // The dynamic half is torn down and rebuilt every server tick. A version that counted it would
        // change every tick, and every cache keyed on it would never hit.
        Assert.Equal(version, grid.StaticVersion);

        grid.AddOverlapping(new Vector3(1, 1, 1), new Vector3(2, 2, 2), item: 2, isStatic: true);
        Assert.NotEqual(version, grid.StaticVersion);

        version = grid.StaticVersion;
        grid.ClearAll();
        Assert.NotEqual(version, grid.StaticVersion);
    }

    [Fact]
    public void OcclusionStillSeesAWallThatSpansSeveralCells()
    {
        // The candidate query was rewritten; the thing it exists to answer must not have changed. A long
        // wall between the listener and the source still blocks.
        var world = new ClientWorldState();
        world.Clear(new Vector3(100, 20, 100), new Vector3(-50, 0, -50), new Vector3(50, 20, 50));
        world.RegisterDefinition(new EntityDefinition
        {
            EntityId = 1,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0, 2, 0), Rotation = Quaternion.Identity },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(40, 6, 1), IsSolid = true },
            Material = new MaterialComponent { Material = "Concrete" }
        });

        var spatial = new SpatialService();
        var snapshot = world.GetSnapshot();

        float blocked = spatial.GetOcclusionFactor(snapshot, new Vector3(0, 2, -6), new Vector3(0, 2, 6), out _);
        float clear = spatial.GetOcclusionFactor(snapshot, new Vector3(0, 2, 6), new Vector3(0, 2, 14), out _);

        Assert.True(blocked > 0.1f, $"a wall across the line of sight should occlude, got {blocked}");
        Assert.True(clear < blocked, $"an unobstructed line should occlude less than a blocked one ({clear} vs {blocked})");
    }

    // ── The server ground probe ─────────────────────────────────────────────────────────────────

    [Fact]
    public void GroundProbeIsRecomputedOnlyWhenTheAnswerCanHaveChanged()
    {
        var memo = new GroundProbeMemo();
        var pos = new Vector3(10, 1, 10);

        Assert.False(memo.TryGet(pos, staticVersion: 1, nowMs: 1000, out _, out _));
        memo.Store(pos, staticVersion: 1, nowMs: 1000, height: 2.5f, material: "Wood");

        // A sub-tick input later: same tick, a centimetre of movement, same geometry.
        Assert.True(memo.TryGet(pos + new Vector3(0.01f, 0, 0.01f), 1, 1005, out float height, out string material));
        Assert.Equal(2.5f, height);
        Assert.Equal("Wood", material);

        // Moved off the memo's footprint.
        Assert.False(memo.TryGet(pos + new Vector3(0.5f, 0, 0), 1, 1005, out _, out _));

        // The map changed under them — a wall spawned, a platform destroyed.
        Assert.False(memo.TryGet(pos, staticVersion: 2, nowMs: 1005, out _, out _));

        // Aged out. Dynamic colliders are not in the static version, so a remembered floor is only ever
        // allowed to be this stale.
        Assert.False(memo.TryGet(pos, 1, 1000 + GroundProbeMemo.MaxAgeMs + 1, out _, out _));
    }

    [Fact]
    public void TheMemoizedGroundProbeGivesTheSameAnswerAsTheFullOne()
    {
        // The point of the memo is fewer probes, not different answers.
        var world = World.Create();
        try
        {
            var grid = new SpatialGrid<Entity>(new Vector2(-50, -50), new Vector2(50, 50), 10f);
            var floor = world.Create(
                new Transform { Position = new Vector3(0, 0, 0), Rotation = Quaternion.Identity },
                new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(40, 1, 40), IsSolid = true },
                new MaterialComponent { Material = "Concrete" });
            grid.AddOverlapping(new Vector3(0, 0, 0), new Vector3(40, 1, 40), floor, isStatic: true);

            var standing = new Vector3(3, 1, 3);
            float direct = PhysicsUtils.GetGroundHeight(world, grid, standing, out string directMaterial);

            var memo = new GroundProbeMemo();
            float first = PhysicsUtils.GetGroundHeight(world, grid, standing, ref memo, out string firstMaterial);
            float second = PhysicsUtils.GetGroundHeight(world, grid, standing, ref memo, out string secondMaterial);

            Assert.Equal(direct, first);
            Assert.Equal(direct, second);
            Assert.Equal(directMaterial, firstMaterial);
            Assert.Equal(directMaterial, secondMaterial);
            Assert.Equal(1, memo.Hits);   // the second call cost nothing
            Assert.Equal(1, memo.Misses); // the first one did the work
        }
        finally
        {
            World.Destroy(world);
        }
    }

    // ── The audio rate cap ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheAudioUpdateIsCappedAtSixtyHertz()
    {
        var throttle = new UpdateThrottle(ClientAudioSystem.UpdateHz);

        // One second of the game loop as it actually spins: a 5 ms sleep between iterations, so 200 calls.
        int ran = 0;
        for (int i = 0; i < 200; i++)
            if (throttle.ShouldRun(i * 0.005)) ran++;

        // The cap is a ceiling, not a target. Re-arming from the moment the update ran quantizes the rate
        // to the loop's own 5 ms step, so a 60 Hz cap on a 200 Hz loop lands at 50 — three quarters of the
        // updates gone, and none of the remaining ones bunched. What matters is that it never exceeds 60.
        Assert.InRange(ran, 45, 60);
        Assert.Equal(200 - ran, throttle.Skipped);
    }

    [Fact]
    public void AStalledLoopDoesNotRunCatchUpAudioFrames()
    {
        var throttle = new UpdateThrottle(60.0);
        Assert.True(throttle.ShouldRun(0.0));

        // Half a second passes in one step — a GC pause, a stalled thread. There is nothing to repay:
        // the audio those frames would have produced is in the past and inaudible now.
        Assert.True(throttle.ShouldRun(0.5));
        Assert.False(throttle.ShouldRun(0.5));
        Assert.Equal(2, throttle.Runs);
    }

    // ── The profiler itself ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheProfilerCostsNothingUntilItIsTurnedOn()
    {
        PerfProbe.Reset();
        PerfProbe.Enabled = false;
        try
        {
            PerfProbe.Count("test.counter");
            using (PerfProbe.Measure("test.timer")) { }
            Assert.Empty(PerfProbe.Drain());

            PerfProbe.Enabled = true;
            PerfProbe.Count("test.counter", 3);
            using (PerfProbe.Measure("test.timer")) { }

            var lines = PerfProbe.Drain();
            Assert.Contains(lines, l => l.StartsWith("test.counter") && l.Contains("n=3"));
            Assert.Contains(lines, l => l.StartsWith("test.timer") && l.Contains("avg="));

            // Each report covers one window: an average over a whole session hides the frame that hitched.
            Assert.Empty(PerfProbe.Drain());
        }
        finally
        {
            PerfProbe.Enabled = false;
            PerfProbe.Reset();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static ClientWorldState LoadedWorld()
    {
        var world = new ClientWorldState();
        world.Clear(new Vector3(100, 20, 100), new Vector3(-50, 0, -50), new Vector3(50, 20, 50));
        world.RegisterDefinition(Wall(id: 1, new Vector3(5, 1, 5)));
        world.RegisterDefinition(Wall(id: 2, new Vector3(-5, 1, -5)));
        return world;
    }

    private static EntityDefinition Wall(int id, Vector3 position) => new()
    {
        EntityId = id,
        Type = EntityType.StaticObject,
        Transform = new Transform { Position = position, Rotation = Quaternion.Identity },
        Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(2, 3, 2), IsSolid = true }
    };
}
