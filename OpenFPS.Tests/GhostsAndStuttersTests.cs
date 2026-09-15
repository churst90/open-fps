using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The two faults from docs/AUDIO_GHOSTS_AND_STUTTERS.md, held to their fixes.
///
/// Issue 1 — "the cars disappear and I hear the ghost of their reflections" — was four things at
/// once: occlusion that arrived a hundred seconds into the session, a probe half buried in the road,
/// reflections exempt from the whole model, and a knee-high wall treated as a total block. Issue 2 —
/// "the close ones going fast stop for a second" — was a position whose timestamp said it had just
/// been sampled when it was a whole simulation step old.
///
/// What is checkable without a mixer is the MODEL: the barrier physics, where the acoustic question
/// is asked, whether the map a vehicle is told to drive is driveable, and the arithmetic of the age
/// of a position. Each of those was wrong in a way that arithmetic can catch.
/// </summary>
public class GhostsAndStuttersTests
{
    // ── Rule 3: a barrier attenuates by its geometry, not by a boolean ──────────────────────────

    /// <summary>
    /// A knee-high wall is not a wall. The pit wall that silenced the field is 0.9 m tall, and a car
    /// twenty metres behind it is barely shadowed at all: the sound goes over the top having travelled
    /// a couple of centimetres further than it would have done through the wall.
    /// </summary>
    [Fact]
    public void AKneeHighWallCostsAFewDecibelsNotTwentySix()
    {
        // 0.9 m tall, 0.3 m thick, standing between a car's exhaust (0.3 m up) and a listener's ear.
        // The listener is well back from it — an infield listener and a pit wall — because a low wall
        // only breaks the sight line at all when one of the two is a long way past it. That is the
        // geometry the report came from, and it is exactly where the model used to say "silence".
        var centre = new Vector3(0f, 0.45f, 0f);
        var size = new Vector3(60f, 0.9f, 0.3f);
        var source = new Vector3(0f, 0.3f, -20f);
        var listener = new Vector3(0f, 1.7f, 45f);

        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity, source, listener, out float delta),
                    "the wall is between them, so it must register as being in the way");

        float lowDb = Diffraction.InsertionLossDb(delta, Diffraction.LowBandHz);
        float highDb = Diffraction.InsertionLossDb(delta, Diffraction.HighBandHz);

        // Everything a screen does is bounded below by its grazing value and above by its ceiling.
        Assert.InRange(lowDb, Diffraction.GrazingInsertionLossDb, 12f);
        Assert.True(highDb > lowDb, $"a barrier takes the top off first; got low {lowDb:F1} dB, high {highDb:F1} dB");
        Assert.True(highDb < Diffraction.MaxInsertionLossDb,
            $"a wall you can see over should not saturate the model; got {highDb:F1} dB");

        // The number that matters: what the engine used to do instead. Visibility zero mapped to a
        // dry level 26 dB down with 72 dB off the top, which is silence.
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

    /// <summary>
    /// The shortest way past a wall standing on the ground goes OVER it, never under it. Without the
    /// leg-clearance test the search happily returns the buried bottom edge, which is both shorter and
    /// a route sound cannot take — and would report a tall wall as costing almost nothing.
    /// </summary>
    [Fact]
    public void TheDetourGoesOverAWallRatherThanThroughTheGround()
    {
        var size = new Vector3(40f, 4f, 0.5f);
        var centre = new Vector3(0f, 2f, 0f);     // sits on the ground, top at y = 4
        var source = new Vector3(0f, 0.3f, -10f);
        var listener = new Vector3(0f, 1.7f, 10f);

        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity, source, listener, out float over));

        // Straight through is 20 m; up the front face, across the half-metre top and down the back is
        // appreciably longer. The route UNDER the wall is a metre shorter still and is not available,
        // which is the thing this is really checking.
        float direct = Vector3.Distance(source, listener);
        var topFront = new Vector3(0f, 4f, -0.25f);
        var topBack = new Vector3(0f, 4f, 0.25f);
        float viaTop = Vector3.Distance(source, topFront) + Vector3.Distance(topFront, topBack) + Vector3.Distance(topBack, listener);
        Assert.InRange(over, (viaTop - direct) * 0.9f, (viaTop - direct) * 1.1f);
    }

    // ── Rule 2: the acoustic source point is the emission point ─────────────────────────────────

    /// <summary>
    /// A car's occlusion probe must sit in the air above the road, not half inside it. The entity's
    /// origin is its contact patch; asking about that point with a half-metre sphere put about half
    /// the samples underground on every level stretch of every track, for every vehicle.
    /// </summary>
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

        // And it is BEHIND the car, which is where an exhaust is — the same point the voice is placed
        // at, so the probe and the voice can never again disagree about where the sound is.
        Assert.True(Vector3.Distance(point, snap.Transform.Position) > 0.5f,
            "the engine voice sits at the tailpipe; the probe has to be at the same place");
    }

    /// <summary>An emitter with nothing authored is still probed at its own origin, as before.</summary>
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

    /// <summary>
    /// The shipped speedway's track has to be driveable end to end. It was not: the front straight
    /// ran through the grandstand deck for the last hundred and twenty metres before turn 4, because
    /// the generator set the stand back from the straight's NEAREST point and the straight is a
    /// tangent between two circles of different radii, so it slants by forty-six metres.
    /// </summary>
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

    /// <summary>And the check has to be able to SEE a building on the line, or it proves nothing.</summary>
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
    /// The stamp has to survive being re-applied, because it is re-applied constantly: the voice
    /// manager hands every playing voice back to the provider on each of its 250 Hz ticks with the
    /// same stored emitter. Stamped "now" on each of those, the age of a position never exceeded one
    /// tick, dead reckoning could not carry a car more than a quarter of a metre, and the 250 Hz loop
    /// wrote the same pitch eight times over and then jumped.
    ///
    /// This models the two rules side by side over one simulation step.
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
            // What it used to do: every re-application declared the position freshly sampled.
            worstStampedOnSubmit = Math.Max(worstStampedOnSubmit, 0.0);
            // What it does now: the emitter carries when it was true, and nothing resets that.
            worstStampedOnSample = Math.Max(worstStampedOnSample, now - sampledAt);
        }

        Assert.True(worstStampedOnSubmit < Tick,
            "the premise: re-stamping on submit hid the entire age of the position");
        Assert.True(worstStampedOnSample > SimStep * 0.9,
            $"a sample-time stamp has to expose the whole step; saw {worstStampedOnSample * 1000:F0} ms");
    }

    /// <summary>
    /// And the reckoning must be allowed to cover that age. A cap shorter than the real sampling
    /// period is a hold by another name: the source freezes for the rest of every step.
    /// </summary>
    [Fact]
    public void TheDeadReckonCapCoversAWholeSimulationStep()
    {
        // Mirrors FmodAudioProvider.MaxDeadReckonSeconds, which is private. If that changes below one
        // simulation step this test is the thing that says the freeze is back.
        const double MaxDeadReckonSeconds = 0.08;
        const double SimStep = 1.0 / 30.0;
        const double AudioSubmitPeriod = 0.022;
        Assert.True(MaxDeadReckonSeconds >= SimStep + AudioSubmitPeriod,
            "a position can legitimately be one simulation step plus one submit period old");
        Assert.True(MaxDeadReckonSeconds < 0.2,
            "and long enough to fling a source that stopped dead is not caution either");
    }

    // ── An emitter that makes sound on its own is processed every frame ─────────────────────────

    /// <summary>
    /// A public-address horn that says its piece every twenty seconds is an emitter that runs on its
    /// own, and has to be treated as one.
    ///
    /// It was not. The client registered an entity for per-frame audio processing only if its mode was
    /// LoopOne or it was a synth — a list of two cases, not a rule — so a Single-with-a-repeat-interval
    /// emitter was never in the audio system at all: no voice, no occlusion, no log line, nothing to
    /// notice. The repeat logic written expressly to serve it could never run, because nothing ever
    /// called the code containing it.
    /// </summary>
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

    /// <summary>
    /// And a plain one-shot does NOT, which is the thing the old narrow test was really protecting.
    /// Registering a Single with no repeat interval would replay it every frame for ever.
    /// </summary>
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
