using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A mixer with nothing behind it that keeps every emitter it was asked to play, whole, and every
/// buffer it was handed to register. It can be told to refuse one sound, as a mixer that has run out
/// of memory would.
/// </summary>
internal sealed class EmitterRecordingProvider : IAudioProvider
{
    public readonly List<SpatialEmitter> Played = new();
    public readonly Dictionary<string, byte[]> Registered = new();
    public int Registrations;
    public string? Refuse;
    public readonly HashSet<int> Live = new();

    public bool Initialize() => true;
    public void Update() { }
    public void UpdateListener(Vector3 p, Quaternion r, Vector3 v, int region) { }
    public void UpdateShelter(float f) { }
    public void UpdateBoundaries(ReadOnlySpan<BoundaryProbe> probes) { }
    public bool PlayAmbientBed(string id, AmbisonicLayout l, float v, bool loop = true) => true;
    public void SetAmbientBedVolume(string id, float v) { }
    public void StopAmbientBed(string id) { }
    public void SetAcousticMap(AcousticMap map) { }
    public void PlaySpatialSound(SpatialEmitter e) { Played.Add(e); Live.Add(e.EntityId); }
    public void UpdateSpatialAttributes(SpatialEmitter e) { }
    public void SetAcousticPath(int id, AcousticPathData p) { }
    public void SetSimulatedReverbDecay(float ms, float enclosure, float hf, float lf) { }
    public void SetListenerReverbField(Vector3 returnDirection, float anisotropy, float meanFreePathMetres, float surfaceAreaSquareMetres = 0f) { }
    public void SetAirTemperature(float c) { }
    public float MixerLoad => 0f;
    public void ReviveEngine(int id) { }
    public bool FadeOutEngine(int id) => true;
    public int SpatialVoicesFree => 96;
    public bool FadeOutVoice(int id) => true;
    public void CancelVoiceFade(int id) { }
    public void StopSound(int id) => Live.Remove(id);
    /// <summary>Every one-shot here is over by the next frame.</summary>
    public bool IsPlaying(int id) => false;
    public Vector3 GetSoundPosition(int id) => Vector3.Zero;
    public float GetPlaybackProgress(int id) => 0f;
    public IEnumerable<int> GetActiveSpatialSoundIds() => new List<int>(Live);
    public void Preload(string id) { }
    public void PlayVoice(int sender, Vector3 pos, byte[] pcm) { }
    public bool RegisterSynthesisedSound(string soundId, byte[] pcm16Mono, int sampleRate)
    {
        Registrations++;
        if (soundId == Refuse) return false;
        Registered[soundId] = pcm16Mono;
        return true;
    }
    public void PlayUiSound(string id, Func<float[]> render, int sampleRate, float volume) { }
    public IReadOnlyList<VoiceLevel> LoudestVoices(int count) => Array.Empty<VoiceLevel>();
    public IReadOnlyList<string> OutputDevices() => new[] { "Test output" };
    public IReadOnlyList<string> InputDevices() => new[] { "Test input" };
    public bool SetOutputDevice(string name) => true;
    public void StartDiagnosticSound() { }
    public void SetDiagnosticPosition(Vector3 p) { }
    public void StopDiagnosticSound() { }
    public void Dispose() { }

    public static float[] Decode(byte[] pcm16)
    {
        var x = new float[pcm16.Length / 2];
        for (int i = 0; i < x.Length; i++) x[i] = (short)(pcm16[2 * i] | (pcm16[2 * i + 1] << 8)) / 32767f;
        return x;
    }
}

/// <summary>
/// The client's own beacons, held to the edges Stryker found untested (2026-09-24): which things blip
/// and when, the sounds themselves, what reaches the mixer, the /beacons command's words, and the
/// player's switches surviving a restart.
/// </summary>
public class BeaconAidsMutationTests
{
    private static EntitySnapshot Thing(int id, Vector3 at, Vector3 size, string category = "", string material = "Concrete",
                                        Quaternion? rotation = null)
        => new()
        {
            Id = id,
            Definition = new EntityDefinition
            {
                EntityId = id,
                Type = EntityType.StaticObject,
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
                Material = new MaterialComponent { Material = material, Variant = "0" },
                Identity = new IdentityComponent { BeaconCategory = category },
            },
            Transform = new Transform { Position = at, Rotation = rotation ?? Quaternion.Identity, Scale = Vector3.One },
        };

    private static readonly Vector3 DoorSize = new(1f, 2f, 0.1f);

    /// <summary>A world of things that move: no static grid, so everything is found through the
    /// dynamic list, in the order given.</summary>
    private static WorldSnapshot Moving(params EntitySnapshot[] things)
    {
        var w = new WorldSnapshot();
        foreach (var t in things) { w.Entities[t.Id] = t; w.DynamicEntities.Add(t); }
        return w;
    }

    /// <summary>A world of fixed things, all in a static grid.</summary>
    private static WorldSnapshot Fixed(params EntitySnapshot[] things)
    {
        var w = new WorldSnapshot { StaticGrid = new SpatialGrid<int>(8f) };
        foreach (var t in things)
        {
            w.Entities[t.Id] = t;
            w.StaticGrid.AddOverlapping(t.Transform.Position, t.Definition.Collider.Size, t.Transform.Rotation, t.Id, true);
        }
        return w;
    }

    private sealed class Rig
    {
        public readonly EmitterRecordingProvider Mixer = new();
        public readonly AudioEngineFacade Audio;
        public readonly BeaconAids Aids;
        public Rig(BeaconPreferences? prefs = null, SpatialAcoustics? acoustics = null)
        {
            Audio = new AudioEngineFacade(Mixer);
            Audio.InitializeForTest();
            Aids = new BeaconAids(Audio, prefs ?? BeaconPreferences.InMemory(), acoustics);
        }

        /// <summary>One frame at <paramref name="now"/>; returns what reached the mixer.</summary>
        public List<SpatialEmitter> Step(WorldSnapshot world, Vector3 ear, double now)
        {
            int before = Mixer.Played.Count;
            Audio.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
            Aids.Update(world, ear, now);
            for (int k = 0; k < 3; k++) Audio.PumpForTest();
            return Mixer.Played.Skip(before).ToList();
        }

        /// <summary>Frames every 0.1 s over a stretch; returns every blip.</summary>
        public List<SpatialEmitter> Run(WorldSnapshot world, Vector3 ear, double from, double seconds)
        {
            var all = new List<SpatialEmitter>();
            for (int i = 0; i * 0.1 < seconds; i++) all.AddRange(Step(world, ear, from + i * 0.1));
            return all;
        }
    }

    private static readonly Vector3 Ear = new(0f, 1.6f, 0f);

    private static bool From(SpatialEmitter e, Vector3 at) => Vector3.Distance(e.Position, at) < 1e-3f;

    // ── When ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A beacon found for the first time does not blip on the spot: it starts on a beat of its own,
    /// a sixteenth of the period for each step of its id, and then blips once every 1.6 seconds.
    /// </summary>
    [Fact]
    public void EachBeaconBlipsOnItsOwnBeatEveryPeriod()
    {
        var door = new Vector3(3f, 1f, 0f);
        var world = Moving(Thing(8, door, DoorSize, Beacons.Door));   // 8 of 16: half a period late
        var rig = new Rig();

        const double t0 = 10.0, period = 1.6;
        double first = t0 + 8 / 16.0 * period;
        Assert.Empty(rig.Step(world, Ear, t0));
        Assert.Empty(rig.Step(world, Ear, t0 + 0.4));
        Assert.Empty(rig.Step(world, Ear, first - 0.01));
        Assert.Single(rig.Step(world, Ear, first));                    // exactly on the beat
        Assert.Empty(rig.Step(world, Ear, first + 0.1));
        Assert.Empty(rig.Step(world, Ear, first + period - 0.01));
        Assert.Single(rig.Step(world, Ear, first + period));
        Assert.Empty(rig.Step(world, Ear, first + period + 0.05));
    }

    /// <summary>Even a beacon whose beat falls right now waits for the next frame: the frame that
    /// finds it only sets its beat.</summary>
    [Fact]
    public void TheFrameThatFindsABeaconOnlySetsItsBeat()
    {
        var world = Moving(Thing(32, new Vector3(3f, 1f, 0f), DoorSize, Beacons.Door));   // 32 of 16: on the beat
        var rig = new Rig();
        Assert.Empty(rig.Step(world, Ear, 10.0));
        Assert.Single(rig.Step(world, Ear, 10.1));
    }

    // ── Which ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Only the nearest three doors are heard, nearest first however the world lists them — and
    /// things that move, which are not in the static grid, are found as well.
    /// </summary>
    [Fact]
    public void OnlyTheNearestThreeDoorsAreHeard()
    {
        var at = new[] { new Vector3(8f, 1f, 0f), new Vector3(6f, 1f, 0f), new Vector3(4f, 1f, 0f), new Vector3(2f, 1f, 0f) };
        var world = Moving(at.Select((p, i) => Thing(100 + i, p, DoorSize, Beacons.Door)).ToArray());
        var blips = new Rig().Run(world, Ear, 10, 6);
        Assert.DoesNotContain(blips, e => From(e, at[0]));
        for (int i = 1; i < 4; i++) Assert.Contains(blips, e => From(e, at[i]));
    }

    /// <summary>
    /// A door is heard out to twelve metres, and not past it; a vehicle out to twenty-five.
    /// </summary>
    [Fact]
    public void EachKindIsHeardOutToItsOwnRange()
    {
        var inside = new Vector3(11.9f, 1.6f, 0f);
        var edge = new Vector3(0f, 1.6f, 12f);
        var beyond = new Vector3(-12.5f, 1.6f, 0f);
        var car = new Vector3(0f, 1.6f, -24f);
        var farCar = new Vector3(18f, 1.6f, -18f);   // 25.5 m
        var world = Moving(Thing(1, inside, DoorSize, Beacons.Door), Thing(2, edge, DoorSize, Beacons.Door),
                           Thing(3, beyond, DoorSize, Beacons.Door), Thing(4, car, new Vector3(1.8f, 1.5f, 4.5f), Beacons.Vehicle),
                           Thing(5, farCar, new Vector3(1.8f, 1.5f, 4.5f), Beacons.Vehicle));
        var blips = new Rig().Run(world, Ear, 10, 6);
        Assert.Contains(blips, e => From(e, inside));
        Assert.Contains(blips, e => From(e, edge));
        Assert.DoesNotContain(blips, e => From(e, beyond));
        Assert.Contains(blips, e => From(e, car));
        Assert.DoesNotContain(blips, e => From(e, farCar));
    }

    /// <summary>
    /// A thing found twice — in the static grid and among the moving things — is one beacon, and
    /// does not take two of the three places.
    /// </summary>
    [Fact]
    public void AThingFoundTwiceIsOneBeacon()
    {
        var near = Thing(5, new Vector3(1f, 1f, 0f), DoorSize, Beacons.Door);
        var others = new[] { Thing(6, new Vector3(3f, 1f, 0f), DoorSize, Beacons.Door),
                             Thing(7, new Vector3(5f, 1f, 0f), DoorSize, Beacons.Door) };
        var world = Fixed(new[] { near }.Concat(others).ToArray());
        world.DynamicEntities.Add(near);
        var blips = new Rig().Run(world, Ear, 10, 6);
        Assert.Contains(blips, e => From(e, others[1].Transform.Position));
    }

    /// <summary>A category switched off is not heard; the others carry on.</summary>
    [Fact]
    public void ACategorySwitchedOffIsNotHeard()
    {
        var prefs = BeaconPreferences.InMemory();
        prefs.Set(Beacons.Door, false);
        var door = new Vector3(3f, 1f, 0f);
        var item = new Vector3(-3f, 1f, 0f);
        var world = Moving(Thing(1, door, DoorSize, Beacons.Door), Thing(2, item, new Vector3(0.3f, 0.3f, 0.3f), Beacons.Item));
        var blips = new Rig(prefs).Run(world, Ear, 10, 6);
        Assert.DoesNotContain(blips, e => From(e, door));
        Assert.Contains(blips, e => From(e, item));
    }

    // ── What reaches the mixer ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A blip is a one-off sound in the world, with reverb, and with no acoustics to ask it goes
    /// straight: from where the thing is, at its real distance, unblocked, in no particular region.
    /// </summary>
    [Fact]
    public void ABlipIsAOneOffInTheWorld()
    {
        var door = new Vector3(3f, 1f, 4f);
        var blips = new Rig().Run(Moving(Thing(1, door, DoorSize, Beacons.Door)), Ear, 10, 4);
        Assert.NotEmpty(blips);
        foreach (var e in blips)
        {
            Assert.True(e.IsEvent);
            Assert.True(e.EnableReverb);
            Assert.Equal(PlaybackMode.Single, e.Mode);
            Assert.Equal("SYNTH/beacon_door_knock", e.SoundId);
            Assert.Equal(door, e.ApparentPosition);
            Assert.Equal(Vector3.Distance(Ear, door), e.EffectiveDistance, 4);
            Assert.Equal(0f, e.Occlusion);
            Assert.Equal(1f, e.ApertureFactor);
            Assert.Equal(0f, e.TransmissionBleed);
            Assert.Equal(-1, e.TargetRegionId);
        }
    }

    /// <summary>
    /// Blips take their voices from a pool of their own: twenty-four ids, counting down from the
    /// first, and round again — so a blip never takes over another sound's voice, and they never
    /// run out.
    /// </summary>
    [Fact]
    public void BlipsCycleThroughAPoolOfTwentyFourVoices()
    {
        var world = Moving(Thing(1, new Vector3(2f, 1f, 0f), DoorSize, Beacons.Door),
                           Thing(2, new Vector3(-2f, 1f, 0f), DoorSize, Beacons.Door),
                           Thing(3, new Vector3(0f, 1f, 2f), DoorSize, Beacons.Door));
        var ids = new Rig().Run(world, Ear, 10, 30).Select(e => e.EntityId).ToList();
        Assert.True(ids.Count > 48, $"only {ids.Count} blips");
        Assert.Equal(24, ids.Distinct().Count());
        int top = ids[0];
        Assert.True(top < 0);
        // In the order they were handed out — until two blips fall in one frame and the mixer takes
        // them in its own order, which the first two rounds do not.
        for (int i = 0; i < 48; i++) Assert.Equal(top - i % 24, ids[i]);
        Assert.All(ids, id => Assert.InRange(id, top - 23, top));
    }

    /// <summary>
    /// The three sounds are made once, not every frame; and if the mixer will not take one of them,
    /// no beacon is heard until it does — they are asked for again on a later frame.
    /// </summary>
    [Fact]
    public void TheSoundsAreMadeOnceAndAskedForAgainIfRefused()
    {
        var world = Moving(Thing(1, new Vector3(2f, 1f, 0f), DoorSize, Beacons.Door));
        var rig = new Rig();
        rig.Mixer.Refuse = "SYNTH/beacon_item_bell";
        Assert.Empty(rig.Run(world, Ear, 10, 4));
        Assert.True(rig.Mixer.Registrations > 3, "a refused sound was never asked for again");

        rig.Mixer.Refuse = null;
        Assert.NotEmpty(rig.Run(world, Ear, 14, 4));
        int made = rig.Mixer.Registrations;
        rig.Run(world, Ear, 18, 4);
        Assert.Equal(made, rig.Mixer.Registrations);

        var fresh = new Rig();
        fresh.Run(world, Ear, 10, 4);
        Assert.Equal(3, fresh.Mixer.Registrations);
    }

    // ── Through walls, and round corners ────────────────────────────────────────────────────

    /// <summary>
    /// A door set into a wall, seen from off to one side: the straight line to its middle clips the
    /// wall beside it, so the path model calls it well over half blocked — but its face is in plain
    /// view, and it blips. Posts stand a little further out from the face than the sight line starts,
    /// so a sight line started from anywhere else than just off the face is blocked. Built three times,
    /// with the door's thin side along each axis.
    /// </summary>
    [Theory]
    [InlineData('z')]
    [InlineData('x')]
    [InlineData('y')]
    public void ADoorWhoseFaceIsInViewReachesYou(char thin)
    {
        Func<Vector3, Vector3> P = thin switch
        {
            'x' => v => new Vector3(v.Z, v.Y, v.X),
            'y' => v => new Vector3(v.X, v.Z, v.Y),
            _ => v => v,
        };
        var things = new List<EntitySnapshot>
        {
            Thing(1, P(new Vector3(-2.75f, 1.5f, 0f)), P(new Vector3(4.5f, 3f, 0.4f))),     // the wall either side
            Thing(2, P(new Vector3(2.75f, 1.5f, 0f)), P(new Vector3(4.5f, 3f, 0.4f))),
            Thing(10, P(new Vector3(0f, 1f, 0f)), P(DoorSize), Beacons.Door, "Wood"),
        };
        foreach (float d in new[] { 0.85f, 1.35f, 2.5f })   // posts out from the face
            things.Add(Thing(20 + things.Count, P(new Vector3(0f, 1.5f, d)), P(new Vector3(0.2f, 3f, 0.2f))));
        // ...and a wall close behind the listener, 3 cm past where the sight line would end at the ear.
        things.Add(Thing(40, P(new Vector3(4.53f, 1.5f, 1f)), P(new Vector3(1f, 3f, 4f))));
        var world = Fixed(things.ToArray());
        var acoustics = new SpatialAcoustics(new SpatialService());
        var aids = new BeaconAids(new AudioEngineFacade(new EmitterRecordingProvider()), BeaconPreferences.InMemory(), acoustics);

        var door = P(new Vector3(0f, 1f, 0f));
        foreach (var ear in new[] { P(new Vector3(4f, 1f, 1f)), P(new Vector3(3.9f, 1f, 0.8f)) })
        {
            Assert.True(acoustics.CalculateAcousticPath(world, 10, ear, door).Occlusion > 0.5f, "not half blocked, so sight is never asked");
            Assert.True(aids.Reaches(world, 10, ear, door, out var path));
            Assert.NotNull(path);
        }
    }

    /// <summary>
    /// A door behind a wall, whose face you cannot see, does not reach you — and nor does a beacon
    /// the world has no fixed record of, since there is no face to look for.
    /// </summary>
    [Fact]
    public void ADoorBehindAWallDoesNotReachYou()
    {
        var wall = Thing(1, new Vector3(0f, 5f, 5f), new Vector3(200f, 10f, 0.3f));
        var door = Thing(10, new Vector3(0f, 1f, 10f), DoorSize, Beacons.Door, "Wood");
        var acoustics = new SpatialAcoustics(new SpatialService());
        var ear = new Vector3(0f, 1.6f, 0f);

        var world = Fixed(wall, door);
        var aids = new BeaconAids(new AudioEngineFacade(new EmitterRecordingProvider()), BeaconPreferences.InMemory(), acoustics);
        Assert.True(acoustics.CalculateAcousticPath(world, 10, ear, door.Transform.Position).Occlusion > 0.5f);
        Assert.False(aids.Reaches(world, 10, ear, door.Transform.Position, out _));

        // A moving thing behind the same wall, known only among the moving things.
        var moving = Fixed(wall);
        moving.DynamicEntities.Add(door);
        var rig = new Rig(acoustics: acoustics);
        Assert.Empty(rig.Run(moving, ear, 10, 4));
    }

    /// <summary>
    /// A blip that reaches you partly blocked carries what the path found — its occlusion and what
    /// bleeds through — to the mixer, rather than being played as if nothing were there.
    /// </summary>
    [Fact]
    public void ABlipCarriesItsPathToTheMixer()
    {
        var things = new[]
        {
            Thing(1, new Vector3(-2.75f, 1.5f, 0f), new Vector3(4.5f, 3f, 0.4f)),
            Thing(2, new Vector3(2.75f, 1.5f, 0f), new Vector3(4.5f, 3f, 0.4f)),
            Thing(10, new Vector3(0f, 1f, 0f), DoorSize, Beacons.Door, "Wood"),
        };
        var world = Fixed(things);
        var acoustics = new SpatialAcoustics(new SpatialService());
        var ear = new Vector3(3f, 1.6f, 3f);
        var expected = acoustics.CalculateAcousticPath(world, 10, ear, new Vector3(0f, 1f, 0f));
        Assert.InRange(expected.Occlusion, 0.05f, 0.5f);

        var blips = new Rig(acoustics: acoustics).Run(world, ear, 10, 4);
        Assert.NotEmpty(blips);
        foreach (var e in blips)
        {
            Assert.Equal(expected.Occlusion, e.Occlusion, 4);
            Assert.Equal(expected.TransmissionBleed, e.TransmissionBleed, 4);
            Assert.Equal(expected.ApertureFactor, e.ApertureFactor, 4);
            Assert.Equal(expected.EffectiveDistance, e.EffectiveDistance, 3);
            Assert.Equal(expected.RegionId, e.TargetRegionId);
        }
    }

    // ── The sounds ──────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, float[]> Sounds()
    {
        var rig = new Rig();
        rig.Step(Moving(), Ear, 1.0);
        return rig.Mixer.Registered.ToDictionary(kv => kv.Key, kv => EmitterRecordingProvider.Decode(kv.Value));
    }

    /// <summary>Energy of <paramref name="x"/> at <paramref name="hz"/> (Goertzel), per sample.</summary>
    private static double At(float[] x, double hz, int from = 0, int to = -1)
    {
        if (to < 0) to = x.Length;
        double w = 2 * Math.PI * hz / 48000, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        for (int i = from; i < to; i++) { double s = x[i] + c * s1 - s2; s2 = s1; s1 = s; }
        return (s1 * s1 + s2 * s2 - c * s1 * s2) / ((to - from) * (double)(to - from));
    }

    private static double Rms(float[] x, int from, int to)
    {
        double e = 0;
        for (int i = from; i < to; i++) e += x[i] * x[i];
        return Math.Sqrt(e / (to - from));
    }

    /// <summary>
    /// A door is a knuckle on a wooden panel: 90 ms, two damped modes — 420 Hz the stronger, 1150 Hz
    /// the other — dying away, not ringing on, and never clipping.
    /// </summary>
    [Fact]
    public void TheDoorIsAShortWoodenKnock()
    {
        var x = Sounds()["SYNTH/beacon_door_knock"];
        Assert.Equal(48000 * 90 / 1000, x.Length);
        Assert.True(x.Max(MathF.Abs) < 0.99f, "clipped");
        Assert.True(x.Max(MathF.Abs) > 0.3f, "barely there");
        Assert.Equal(0f, x[0]);

        int third = x.Length / 3;
        Assert.True(Rms(x, 2 * third, x.Length) < 0.1 * Rms(x, 0, third), "it rings on rather than dying away");

        double low = At(x, 420), high = At(x, 1150);
        foreach (double other in new[] { 250.0, 700.0, 2000.0, 3000.0 })
        {
            Assert.True(low > 4 * At(x, other), $"420 Hz is not the knock's note against {other} Hz");
            Assert.True(high > 2 * At(x, other), $"the 1150 Hz mode is missing against {other} Hz");
        }
        Assert.True(low > high);
        // The upper mode dies faster: over the first 20 ms it is there, by the last 30 it is gone.
        Assert.True(At(x, 1150, 0, 960) > 10 * At(x, 1150, x.Length - 1440, x.Length));
    }

    /// <summary>
    /// An item is a small bell: 250 ms, a 1318 Hz partial with an inharmonic one at 3350 Hz, a 2 ms
    /// strike rather than a click, and a ring that dies away.
    /// </summary>
    [Fact]
    public void TheItemIsASmallBell()
    {
        var x = Sounds()["SYNTH/beacon_item_bell"];
        Assert.Equal(48000 * 250 / 1000, x.Length);
        Assert.True(x.Max(MathF.Abs) < 0.99f, "clipped");
        Assert.True(x.Max(MathF.Abs) > 0.3f, "barely there");
        // The strike ramps in over two milliseconds: the first sample is silent and the tenth small.
        Assert.Equal(0f, x[0]);
        Assert.True(MathF.Abs(x[10]) < 0.1f);

        int quarter = x.Length / 4;
        Assert.True(Rms(x, 3 * quarter, x.Length) < 0.3 * Rms(x, 0, quarter), "the bell does not die away");

        double note = At(x, 1318), partial = At(x, 3350);
        foreach (double other in new[] { 600.0, 900.0, 2200.0, 5000.0 })
        {
            Assert.True(note > 4 * At(x, other), $"1318 Hz is not the bell's note against {other} Hz");
            Assert.True(partial > 2 * At(x, other), $"the 3350 Hz partial is missing against {other} Hz");
        }
        Assert.True(note > partial);
    }

    /// <summary>
    /// A vehicle is a low double tone: the same 60 ms beep twice, with 50 ms of silence between.
    /// </summary>
    [Fact]
    public void TheVehicleIsALowToneTwice()
    {
        var x = Sounds()["SYNTH/beacon_vehicle_low"];
        var one = DrivingAids.Beep(48000, 330f, 0.06f, 0.2f);
        int gap = 48000 * 50 / 1000;
        Assert.Equal(2 * one.Length + gap, x.Length);
        var expected = TransientSynthRoundTrip(one);
        for (int i = 0; i < one.Length; i++)
        {
            Assert.Equal(expected[i], x[i]);
            Assert.Equal(expected[i], x[one.Length + gap + i]);
        }
        for (int i = one.Length; i < one.Length + gap; i++) Assert.Equal(0f, x[i]);
        Assert.True(Rms(x, 0, one.Length) > 0.1);
    }

    private static float[] TransientSynthRoundTrip(float[] x)
        => EmitterRecordingProvider.Decode(OpenFPS.Client.AudioEngine.Core.TransientSynth.ToPcm16(x));

    // ── What the player says ────────────────────────────────────────────────────────────────

    /// <summary>/beacons lists every category, whether it is on, and why when the map decided.</summary>
    [Fact]
    public void BeaconsListsEveryCategoryAndWhy()
    {
        var prefs = BeaconPreferences.InMemory();
        prefs.Set(Beacons.Vehicle, false);
        var aids = new BeaconAids(new AudioEngineFacade(new EmitterRecordingProvider()), prefs);
        aids.SetMapPolicy(new[] { "door=forced_on", "item=forbidden" });
        Assert.Equal("Beacons: door on, always on for this map. exit on. stairs on. item off, not allowed on this map. "
                   + "vehicle off. waypoint on. Say slash beacons and a name to switch one.",
                     aids.Command(Array.Empty<string>()));
    }

    /// <summary>
    /// Switching one: "stairs" and "stair" are the stairs; "on" and "off" say which way and leave it
    /// there if it is already that way; a name that is not a category is answered with the list.
    /// </summary>
    [Fact]
    public void SwitchingOneSaysWhichWay()
    {
        var aids = new BeaconAids(new AudioEngineFacade(new EmitterRecordingProvider()), BeaconPreferences.InMemory());

        Assert.Equal("Stairs beacons off.", aids.Command(new[] { "stairs", "off" }));
        Assert.False(aids.IsOn(Beacons.Stairs));
        Assert.Equal("Stairs beacons on.", aids.Command(new[] { "stair", "on" }));
        Assert.True(aids.IsOn(Beacons.Stairs));

        Assert.Equal("Vehicle beacons on.", aids.Command(new[] { "vehicles", "on" }));   // already on: stays on
        Assert.True(aids.IsOn(Beacons.Vehicle));
        Assert.Equal("Vehicle beacons off.", aids.Command(new[] { "vehicle", "OFF" }));
        Assert.Equal("Vehicle beacons off.", aids.Command(new[] { "vehicle", "off" }));  // already off: stays off
        Assert.Equal("Vehicle beacons on.", aids.Command(new[] { "vehicle" }));          // no word: the other way
        Assert.True(aids.IsOn(Beacons.Vehicle));

        Assert.Equal("There is no beacon called frogs. There are door, exit, stairs, item, vehicle, waypoint.",
                     aids.Command(new[] { "frogs" }));
    }

    /// <summary>The preferences a player hands in are the ones used, not whatever is on disk.</summary>
    [Fact]
    public void ThePreferencesHandedInAreTheOnesUsed()
    {
        var prefs = BeaconPreferences.InMemory();
        prefs.Set(Beacons.Door, false);
        var aids = new BeaconAids(new AudioEngineFacade(new EmitterRecordingProvider()), prefs);
        Assert.False(aids.IsOn(Beacons.Door));
        aids.Command(new[] { "item", "off" });
        Assert.False(prefs.Choice(Beacons.Item));
    }
}

/// <summary>Tests that set XDG_CONFIG_HOME or HOME, which are the whole process's, run alone.</summary>
[CollectionDefinition(nameof(BeaconPreferenceFiles), DisableParallelization = true)]
public class BeaconPreferenceFiles { }

/// <summary>
/// The player's beacon switches, kept between sessions in $XDG_CONFIG_HOME/openfps/beacons.json, or
/// ~/.config/openfps/beacons.json without it.
/// </summary>
[Collection(nameof(BeaconPreferenceFiles))]
public class BeaconPreferenceFileTests
{
    private static void With(string? xdg, string? home, Action act)
    {
        string? oldXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? oldHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdg);
            if (home != null) Environment.SetEnvironmentVariable("HOME", home);
            act();
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldXdg);
            Environment.SetEnvironmentVariable("HOME", oldHome);
        }
    }

    private static string Scratch() => Path.Combine(Path.GetTempPath(), "openfps-beacons-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A switch set in one session is there in the next: written to openfps/beacons.json under
    /// XDG_CONFIG_HOME — the folder made if it is not there — and read back.
    /// </summary>
    [Fact]
    public void ASwitchSurvivesARestart()
    {
        string root = Scratch();
        try
        {
            With(root, null, () =>
            {
                var first = BeaconPreferences.Load();
                Assert.Null(first.Choice(Beacons.Door));
                first.Set(Beacons.Door, false);
                first.Set(Beacons.Vehicle, true);
                Assert.True(File.Exists(Path.Combine(root, "openfps", "beacons.json")));

                var second = BeaconPreferences.Load();
                Assert.False(second.Choice(Beacons.Door));
                Assert.True(second.Choice(Beacons.Vehicle));
                Assert.Null(second.Choice(Beacons.Item));
            });
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>Without XDG_CONFIG_HOME the file lives in .config/openfps under the home folder.</summary>
    [Fact]
    public void WithoutXdgTheFileIsUnderHome()
    {
        string home = Scratch();
        try
        {
            Directory.CreateDirectory(home);
            With("", home, () =>
            {
                BeaconPreferences.Load().Set(Beacons.Exit, false);
                Assert.True(File.Exists(Path.Combine(home, ".config", "openfps", "beacons.json")));
                Assert.False(BeaconPreferences.Load().Choice(Beacons.Exit));
            });
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    /// <summary>A file that says nothing, or nonsense, is no switches at all — not a crash.</summary>
    [Fact]
    public void AnEmptyOrBrokenFileIsNoSwitches()
    {
        string root = Scratch();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "openfps"));
            With(root, null, () =>
            {
                File.WriteAllText(Path.Combine(root, "openfps", "beacons.json"), "null");
                Assert.Null(BeaconPreferences.Load().Choice(Beacons.Door));
                File.WriteAllText(Path.Combine(root, "openfps", "beacons.json"), "{ not json");
                Assert.Null(BeaconPreferences.Load().Choice(Beacons.Door));
            });
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>An in-memory store is never written anywhere.</summary>
    [Fact]
    public void AnInMemoryStoreWritesNothing()
    {
        string root = Scratch();
        try
        {
            With(root, null, () =>
            {
                var prefs = BeaconPreferences.InMemory();
                prefs.Set(Beacons.Door, false);
                Assert.False(prefs.Choice(Beacons.Door));
                Assert.False(Directory.Exists(root));
            });
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>With no preferences handed in, BeaconAids loads the player's own from disk.</summary>
    [Fact]
    public void WithNoneHandedInThePlayersOwnAreLoaded()
    {
        string root = Scratch();
        try
        {
            With(root, null, () =>
            {
                BeaconPreferences.Load().Set(Beacons.Item, false);
                var aids = new BeaconAids(new AudioEngineFacade(new EmitterRecordingProvider()));
                Assert.False(aids.IsOn(Beacons.Item));
                Assert.True(aids.IsOn(Beacons.Door));
            });
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
