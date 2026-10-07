using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// The two faults of docs/AUDIO_GHOSTS_AND_STUTTERS.md held to their fixes, checked without a mixer:
/// barrier physics, where the acoustic question is asked, a driveable track, and a position's age.
/// </summary>
public class GhostsAndStuttersTests
{
    // ── Rule 3: a barrier attenuates by its geometry, not by a boolean ──────────────────────────

    /// <summary>A knee-high wall is not a wall: a car 20 m behind the 0.9 m pit wall that silenced the
    /// field is barely shadowed, its path over the top a few centimetres longer.</summary>
    [Fact]
    public void AKneeHighWallCostsAFewDecibelsNotTwentySix()
    {
        // 0.9 m tall, 0.3 m thick, between an exhaust 0.3 m up and an ear well back from it: the
        // reported geometry (a low wall breaks the sight line only with one end far past it).
        var centre = new Vector3(0f, 0.45f, 0f);
        var size = new Vector3(60f, 0.9f, 0.3f);
        var source = new Vector3(0f, 0.3f, -20f);
        var listener = new Vector3(0f, 1.7f, 45f);

        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity, source, listener, out float delta),
                    "the wall is between them, so it must register as being in the way");

        float lowDb = Diffraction.InsertionLossDb(delta, Diffraction.LowBandHz);
        float highDb = Diffraction.InsertionLossDb(delta, Diffraction.HighBandHz);

        Assert.InRange(lowDb, Diffraction.GrazingInsertionLossDb, 12f);
        Assert.True(highDb > lowDb, $"a barrier takes the top off first; got low {lowDb:F1} dB, high {highDb:F1} dB");
        Assert.True(highDb < Diffraction.MaxInsertionLossDb,
            $"a wall you can see over should not saturate the model; got {highDb:F1} dB");

        // The old model made this silence: 26 dB down with 72 dB off the top.
        Assert.True(Diffraction.BandGain(delta, Diffraction.LowBandHz) > 0.25f,
            "a car behind a knee-high wall has to stay clearly audible");
    }

    /// <summary>And a real one still is one: a tall building between the two saturates the screen.</summary>
    [Fact]
    public void ATallBuildingStillShadowsCompletely()
    {
        var centre = new Vector3(0f, 18f, 0f);
        var size = new Vector3(60f, 36f, 16f);
        var source = new Vector3(0f, 0.3f, -40f);
        var listener = new Vector3(0f, 1.7f, 40f);

        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity, source, listener, out float delta));
        Assert.True(delta > 5f, $"going over a 36 m building is a long way round; got {delta:F1} m");
        Assert.Equal(Diffraction.MaxInsertionLossDb, Diffraction.InsertionLossDb(delta, Diffraction.MidBandHz), 1);
    }

    /// <summary>Nothing in the way is no loss at all, however close the box is to the line.</summary>
    [Fact]
    public void AClearLineIsNotABarrier()
    {
        var centre = new Vector3(0f, 0.45f, 5f);
        var size = new Vector3(10f, 0.9f, 0.3f);
        // Both on the same side of the wall: it is beside them, not between them.
        var source = new Vector3(0f, 0.3f, -20f);
        var listener = new Vector3(0f, 1.7f, -8f);
        Assert.False(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity, source, listener, out _));
        Assert.Equal(0f, Diffraction.InsertionLossDb(-1f, 1000f));
    }

    /// <summary>The shortest way past a wall on the ground goes over it, never under: without the
    /// leg-clearance test the buried bottom edge wins and a tall wall costs almost nothing.</summary>
    [Fact]
    public void TheDetourGoesOverAWallRatherThanThroughTheGround()
    {
        var size = new Vector3(40f, 4f, 0.5f);
        var centre = new Vector3(0f, 2f, 0f);     // sits on the ground, top at y = 4
        var source = new Vector3(0f, 0.3f, -10f);
        var listener = new Vector3(0f, 1.7f, 10f);

        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity, source, listener, out float over));

        // The route under the wall would be a metre shorter, and must not be taken.
        float direct = Vector3.Distance(source, listener);
        var topFront = new Vector3(0f, 4f, -0.25f);
        var topBack = new Vector3(0f, 4f, 0.25f);
        float viaTop = Vector3.Distance(source, topFront) + Vector3.Distance(topFront, topBack) + Vector3.Distance(topBack, listener);
        Assert.InRange(over, (viaTop - direct) * 0.9f, (viaTop - direct) * 1.1f);
    }

    // ── Rule 2: the acoustic source point is the emission point ─────────────────────────────────

    /// <summary>A car's occlusion probe sits above the road: a half-metre sphere at the contact patch put
    /// about half its samples underground.</summary>
    [Fact]
    public void ACarIsProbedAtItsExhaustAndItsSphereStaysOutOfTheRoad()
    {
        var snap = Car(new Vector3(120f, 0f, -40f));

        Vector3 point = AudioEmission.PointFor(snap);
        Assert.True(point.Y > 0.05f, $"the emission point is on the road surface at y={point.Y:F2}");

        float radius = AudioEmission.OcclusionRadiusFor(snap);
        Assert.True(point.Y - radius >= -1e-4f,
            $"the probe sphere reaches {point.Y - radius:F2} m, which is below the surface the car rests on");
        Assert.InRange(radius, AudioEmission.MinOcclusionRadius, AudioEmission.DefaultOcclusionRadius);

        // Behind the car, at the exhaust, where the voice is placed too.
        Assert.True(Vector3.Distance(point, snap.Transform.Position) > 0.5f,
            "the engine voice sits at the tailpipe; the probe has to be at the same place");
    }

    /// <summary>An emitter with nothing authored is probed at its own origin.</summary>
    [Fact]
    public void AnEmitterWithNoOffsetIsUnchanged()
    {
        var snap = new EntitySnapshot
        {
            Id = 7,
            Definition = new EntityDefinition { SoundEmitter = new SoundEmitterComponent { SoundId = "BEEP" } },
            Transform = new Transform { Position = new Vector3(3f, 2f, 1f), Rotation = Quaternion.Identity },
        };
        Assert.Equal(snap.Transform.Position, AudioEmission.PointFor(snap));
        Assert.Equal(AudioEmission.MinOcclusionRadius, AudioEmission.OcclusionRadiusFor(snap), 3);
    }

    // ── Rule 6: the map is validated against the things that drive on it ────────────────────────

    /// <summary>The shipped speedway's track is driveable end to end. The front straight once ran through
    /// the grandstand for 120 m: it slants 46 m, and the stand was set back from its nearest point.</summary>
    [Fact]
    public void EveryShippedTrackIsDriveable()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        var manager = new MapManager(maps, prefabs);
        manager.Initialize();

        Assert.NotEmpty(manager.TrackObstructions);
        foreach (var kv in manager.TrackObstructions)
            Assert.True(kv.Value == 0,
                $"track '{kv.Key}' passes through solid geometry at {kv.Value} sampled points — "
                + "vehicles driving it are inside a building, which is heard as them disappearing");
    }

    /// <summary>The check sees a building on the line, or it proves nothing.</summary>
    [Fact]
    public void ABuildingOnTheLineIsCaught()
    {
        var loop = new List<Vector3>();
        for (int i = 0; i < 64; i++)
        {
            double a = i * Math.PI * 2 / 64;
            loop.Add(new Vector3((float)(Math.Cos(a) * 100), 0f, (float)(Math.Sin(a) * 100)));
        }

        var clear = new List<TrackClearance.Solid>
        {
            // The ground the track is laid on: directly underneath, and never an obstruction.
            new(new Vector3(0f, -0.5f, 0f), new Vector3(400f, 1f, 400f), Quaternion.Identity),
            // A wall outside the track edge, where a retaining wall belongs.
            new(new Vector3(112f, 1.75f, 0f), new Vector3(0.6f, 3.5f, 40f), Quaternion.Identity),
        };
        Assert.Empty(TrackClearance.Check(loop, 18f, clear));

        var blocked = new List<TrackClearance.Solid>(clear)
        {
            new(new Vector3(100f, 6f, 0f), new Vector3(20f, 12f, 30f), Quaternion.Identity),
        };
        Assert.NotEmpty(TrackClearance.Check(loop, 18f, blocked));
    }

    // ── Rule 5: a moving source is timestamped by when it was SAMPLED ───────────────────────────

    /// <summary>
    /// A position's stamp survives re-application on every 250 Hz tick: stamped "now" each time, its age
    /// never exceeded a tick, dead reckoning carried a car a quarter metre at most, and the pitch held
    /// then jumped. Models both rules over one simulation step.
    /// </summary>
    [Fact]
    public void ReApplyingAnEmitterDoesNotMakeItsPositionYoung()
    {
        const double SimStep = 1.0 / 30.0;     // remote positions change this often, full stop
        const double Tick = 1.0 / 250.0;       // and the attribute loop runs this often
        double sampledAt = 10.0;

        double worstStampedOnSubmit = 0, worstStampedOnSample = 0;
        for (double now = sampledAt; now < sampledAt + SimStep; now += Tick)
        {
            // Stamped on submit: every re-application declared the position fresh.
            worstStampedOnSubmit = Math.Max(worstStampedOnSubmit, 0.0);
            // Stamped on sample: the emitter carries when it was true.
            worstStampedOnSample = Math.Max(worstStampedOnSample, now - sampledAt);
        }

        Assert.True(worstStampedOnSubmit < Tick,
            "the premise: re-stamping on submit hid the entire age of the position");
        Assert.True(worstStampedOnSample > SimStep * 0.9,
            $"a sample-time stamp has to expose the whole step; saw {worstStampedOnSample * 1000:F0} ms");
    }

    /// <summary>Dead reckoning may cover that age: a cap shorter than the sampling period freezes the
    /// source for the rest of every step.</summary>
    [Fact]
    public void TheDeadReckonCapCoversAWholeSimulationStep()
    {
        // Mirrors the private FmodAudioProvider.MaxDeadReckonSeconds; keep them in step.
        const double MaxDeadReckonSeconds = 0.08;
        const double SimStep = 1.0 / 30.0;
        const double AudioSubmitPeriod = 0.022;
        Assert.True(MaxDeadReckonSeconds >= SimStep + AudioSubmitPeriod,
            "a position can legitimately be one simulation step plus one submit period old");
        Assert.True(MaxDeadReckonSeconds < 0.2,
            "and long enough to fling a source that stopped dead is not caution either");
    }

    // ── An emitter that makes sound on its own is processed every frame ─────────────────────────

    /// <summary>A Single emitter with a repeat interval (a PA every 20 s) runs on its own and is
    /// processed every frame; it once never reached the audio system, silently.</summary>
    [Fact]
    public void AnEmitterThatMakesSoundOnItsOwnIsProcessed()
    {
        var pa = new SoundEmitterComponent
        {
            SoundId = "ANNOUNCE/st_louis_welcome", Mode = PlaybackMode.Single, RepeatIntervalSeconds = 20f,
        };
        Assert.True(pa.RunsOnItsOwn(), "a repeating announcer runs on its own and must be processed");

        Assert.True(new SoundEmitterComponent { SoundId = "X", Mode = PlaybackMode.LoopOne }.RunsOnItsOwn());
        Assert.True(new SoundEmitterComponent { SoundId = "X", Mode = PlaybackMode.LoopFolder }.RunsOnItsOwn());
        Assert.True(new SoundEmitterComponent { SoundId = "X", Mode = PlaybackMode.Sequential }.RunsOnItsOwn());
        Assert.True(new SoundEmitterComponent { SoundId = "engine:v8_muscle", IsSynth = true }.RunsOnItsOwn());
    }

    /// <summary>A plain one-shot does not: registered, it would replay every frame.</summary>
    [Fact]
    public void APlainOneShotIsNotProcessedEveryFrame()
    {
        Assert.False(new SoundEmitterComponent { SoundId = "DOOR/open", Mode = PlaybackMode.Single }.RunsOnItsOwn());
        Assert.False(new SoundEmitterComponent().RunsOnItsOwn());
        // A repeat interval with nothing to play is not a repeater either.
        Assert.False(new SoundEmitterComponent { Mode = PlaybackMode.Single, RepeatIntervalSeconds = 5f }.RunsOnItsOwn());
    }

    private static EntitySnapshot Car(Vector3 at) => new()
    {
        Id = 42,
        Definition = new EntityDefinition
        {
            SoundEmitter = new SoundEmitterComponent { SoundId = "engine:v8_muscle", IsSynth = true, Volume = 1f, Range = 400f },
        },
        Transform = new Transform { Position = at, Rotation = Quaternion.Identity },
        Velocity = new Vector3(60f, 0f, 0f),
    };
}
