using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Tests;

/// <summary>
/// The walls answering a live engine (EngineReflections), driven through the real facade over a
/// mixer that writes down what it is asked: which walls become mirrors, where an echo is placed and
/// how loud, the path it is given, how it is let go, and how the map-wide budget picks the audible
/// ones. No engine is synthesised; an echo voice only names the engine it reads.
///
/// The scene: a concrete wall 40 m long and 6 m high whose face is 9.75 m north of a car and a
/// listener 10 m apart on an east-west line. The engine's axes are x east, y up, z north, but this
/// scene calls -z north (the wall's centre is at z = -10).
/// </summary>
public class EngineReflectionsTests
{
    private const float C = 343f;
    private const int Car = 1, OtherCar = 2;
    private static readonly Vector3 Engine = new(-5f, 0.5f, 0f);
    private static readonly Vector3 Ear = new(5f, 1.6f, 0f);
    private static readonly Vector3 WallCentre = new(0f, 3f, -10f), WallSize = new(40f, 6f, 0.5f);
    /// <summary>The engine mirrored through the wall's face at z = -9.75.</summary>
    private static readonly Vector3 Image = new(-5f, 0.5f, -19.5f);

    private sealed class Rig
    {
        public readonly RecordingMixer Mixer = new();
        public readonly AudioEngineFacade Audio;
        public readonly EngineReflections Echoes = new();
        public readonly WorldSnapshot World = new();
        private int _nextId = 9000;

        public Rig()
        {
            AcousticRegistry.Initialize();
            Audio = new AudioEngineFacade(Mixer);
            Audio.InitializeForTest();
        }

        public void Box(Vector3 centre, Vector3 size, string material = "Concrete", bool moves = false, string? sound = null)
        {
            int id = _nextId++;
            var def = new EntityDefinition
            {
                EntityId = id,
                Type = EntityType.StaticObject,
                Moves = moves,
                Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
                Material = new MaterialComponent { Material = material },
            };
            if (sound != null) def.SoundEmitter.SoundId = sound;
            World.Entities[id] = new EntitySnapshot { Id = id, Definition = def, Transform = def.Transform };
        }

        public void Update(int car, Vector3 at, float volume = 0.8f, float dt = 1f / 60f, bool traced = false,
                           AcousticPathData? path = null, Vector3? ear = null)
        {
            var direct = new SpatialEmitter
            {
                EntityId = car, Position = at, ApparentPosition = at, Volume = volume, MinDistance = 3f, Range = 600f,
                TargetRegionId = 4,
            };
            Echoes.Update(car, direct, path ?? new AcousticPathData(0f, at, 10f), ear ?? Ear, C, dt, Audio, traced);
        }

        public void Pump() => Audio.PumpForTest();

        public IEnumerable<int> EchoIds => Mixer.Started.Select(e => e.EntityId).Where(EngineReflections.IsEchoVoice).Distinct();
    }

    private static Rig WithWall()
    {
        var r = new Rig();
        r.Box(WallCentre, WallSize);
        r.Echoes.SyncGeometry(r.World);
        return r;
    }

    /// <summary>The wall's front face, as SyncGeometry builds it, and what ImageSource makes of it.</summary>
    private static Reflection Expected()
    {
        var props = AcousticRegistry.GetProperties("Concrete");
        Span<ReflectingSurface> six = stackalloc ReflectingSurface[6];
        ImageSource.FacesOfBox(WallCentre, WallSize, Quaternion.Identity, props.Absorption, 1, six, props.Scattering);
        Span<Reflection> found = stackalloc Reflection[2];
        int n = ImageSource.FirstOrder(six, Engine, Ear, C, found);
        Assert.Equal(1, n);
        return found[0];
    }

    /// <summary>
    /// The wall answers: one echo voice in the echo band, reading the car's own engine at the delay
    /// the longer path costs, placed at the image behind the wall, carrying the car's placement and
    /// only the surface's share in its gain — the extra spreading is the renderer's, at the image.
    /// </summary>
    [Fact]
    public void AWallAnswersACarWithAnEchoAtItsImage()
    {
        var r = WithWall();
        Assert.True(r.Echoes.SurfaceCount > 0);
        r.Update(Car, Engine);
        r.Pump();

        var id = Assert.Single(r.EchoIds);
        Assert.Equal(EngineReflections.EchoVoiceIdBase, id);
        Assert.Equal(1, r.Echoes.VoiceCount);
        var echo = r.Mixer.Latest[id];
        var want = Expected();
        float direct = Vector3.Distance(Engine, Ear);

        Assert.Equal(Car, echo.EchoOfEntity);
        Assert.Equal("engine-echo", echo.SoundId);
        Assert.True(echo.IsSynth && echo.IsReflection);
        Assert.Equal(Image.X, echo.Position.X, 3);
        Assert.Equal(Image.Y, echo.Position.Y, 3);
        Assert.Equal(Image.Z, echo.Position.Z, 3);
        Assert.Equal(Vector3.Zero, echo.Velocity);
        Assert.Equal((Vector3.Distance(Image, Ear) - direct) / C, echo.EchoDelaySeconds, 5);
        Assert.Equal(want.Gain * want.PathLength / direct, echo.EchoGain, 4);
        Assert.True(echo.EchoGain <= EarlyReflections.Keep(AcousticRegistry.GetProperties("Concrete").Absorption) + 1e-5f,
            "an echo's own gain is what the surface kept, never more");
        Assert.Equal(0.8f, echo.Volume);
        Assert.Equal(3f, echo.MinDistance);
        Assert.Equal(600f, echo.Range);
        Assert.Equal(4, echo.TargetRegionId);
    }

    /// <summary>
    /// An echo's path is its own: the wall that muffles the car is not on the echo's route (both legs
    /// were tested clear), so it carries no occlusion and flat bands; it is as far away as the path it
    /// took; and the air takes as much as that path costs, the car's per metre times its length.
    /// </summary>
    [Fact]
    public void AnEchoIsNotMuffledByWhatMufflesTheCarAndHasItsOwnAir()
    {
        var r = WithWall();
        float direct = Vector3.Distance(Engine, Ear);
        var muffled = new AcousticPathData(0.8f, Engine, direct, eqL: 0.4f, eqM: 0.2f, eqH: 0.05f, bleed: 0.3f)
        {
            AirLowDb = 0.1f, AirMidDb = 0.5f, AirHighDb = 2f,
        };
        r.Update(Car, Engine, path: muffled);
        r.Pump();

        var id = Assert.Single(r.EchoIds);
        var p = r.Mixer.LastPath(id);
        float length = Vector3.Distance(Image, Ear);
        Assert.True(p.IsReflection);
        Assert.Equal(0f, p.Occlusion);
        Assert.Equal(1f, p.EqLow);
        Assert.Equal(1f, p.EqMid);
        Assert.Equal(1f, p.EqHigh);
        Assert.Equal(0f, p.TransmissionBleed);
        Assert.Equal(length, p.EffectiveDistance, 3);
        Assert.Equal(Image.Z, p.ApparentPosition.Z, 3);
        Assert.Equal(2f * length / direct, p.AirHighDb, 3);
        Assert.Equal(0.5f * length / direct, p.AirMidDb, 3);
        Assert.True(p.AirHighDb > muffled.AirHighDb, "an echo that came further is darker than the car");
    }

    /// <summary>
    /// A box too small to be a mirror (a 1 m pillar) is still something standing in the way: put in
    /// the leg from the car to the bounce point, the wall's echo is not heard. The mirrors and the
    /// obstructions are the same list, so they cannot disagree.
    /// </summary>
    [Fact]
    public void APillarInTheMirroredPathSilencesTheEcho()
    {
        var r = new Rig();
        r.Box(WallCentre, WallSize);
        // Half way along the leg from (-5, 0.5, 0) to the bounce point at (0, 1.05, -9.75).
        r.Box(new Vector3(-2.5f, 1.25f, -4.875f), new Vector3(1f, 2.5f, 1f));
        r.Echoes.SyncGeometry(r.World);

        var wallOnly = WithWall();
        Assert.Equal(wallOnly.Echoes.SurfaceCount, r.Echoes.SurfaceCount);   // the pillar is no mirror

        r.Update(Car, Engine);
        r.Pump();
        Assert.Empty(r.EchoIds);
        Assert.Equal(0, r.Echoes.VoiceCount);
    }

    /// <summary>
    /// What is not part of the static scene is not a mirror: a box that moves (a parked bus can drive
    /// off), a box that is itself a sound source (it would mirror its own sound), and anything too small
    /// in both directions. An empty map has nothing to answer and Update does nothing at all.
    /// </summary>
    [Fact]
    public void OnlyTheStandingSolidGeometryReflects()
    {
        var r = new Rig();
        r.Box(WallCentre, WallSize, moves: true);
        r.Box(new Vector3(0f, 3f, 10f), WallSize, sound: "beacon:bell");
        r.Box(new Vector3(0f, 0.5f, -5f), new Vector3(2f, 1f, 2f));
        r.Echoes.SyncGeometry(r.World);
        Assert.Equal(0, r.Echoes.SurfaceCount);

        r.Update(Car, Engine);
        r.Pump();
        Assert.Empty(r.Mixer.Started);
        Assert.Equal(0, r.Echoes.VoiceCount);

        // A real wall arriving with the next map changes the count, and the surfaces are rebuilt.
        r.Box(WallCentre, WallSize);
        r.Echoes.SyncGeometry(r.World);
        Assert.True(r.Echoes.SurfaceCount > 0);
    }

    /// <summary>
    /// The budget's lever: with no reflections per engine nothing is started, and a source whose
    /// echoes are traced from where it is (traced) is given none of these either.
    /// </summary>
    [Fact]
    public void NoEchoesPerEngineAndATracedSourceStartNothing()
    {
        var r = WithWall();
        r.Echoes.EchoesPerEngine = 0;
        r.Update(Car, Engine);
        r.Echoes.EchoesPerEngine = EngineReflections.MaxEchoesPerEngine;
        r.Update(OtherCar, Engine, traced: true);
        r.Pump();
        Assert.Empty(r.Mixer.Started);
        Assert.Equal(0, r.Echoes.VoiceCount);
    }

    /// <summary>
    /// A wall that stops answering is faded, not cut: its gain ramps down across 0.8 s at the last
    /// place it was heard from (moving a dying echo would Doppler it), and the voice is stopped only
    /// once the ramp has run out.
    /// </summary>
    [Fact]
    public void AWallThatStopsAnsweringFadesWhereItWasAndIsThenLetGo()
    {
        var r = WithWall();
        r.Update(Car, Engine);
        r.Pump();
        int id = Assert.Single(r.EchoIds);
        float gain = r.Mixer.Latest[id].EchoGain;
        var at = r.Mixer.Latest[id].Position;

        // Traced from now on, and the car has moved on: nothing of this wall is found again.
        var moved = Engine + new Vector3(3f, 0f, 0f);
        foreach (var (dt, fade) in new[] { (0.25f, 0.6875f), (0.25f, 0.375f), (0.25f, 0.0625f) })
        {
            r.Update(Car, moved, dt: dt, traced: true);
            r.Pump();
            Assert.Equal(gain * fade, r.Mixer.Latest[id].EchoGain, 5);
            Assert.Equal(at, r.Mixer.Latest[id].Position);
            Assert.DoesNotContain(id, r.Mixer.Stopped);
        }
        r.Update(Car, moved, dt: 0.25f, traced: true);
        r.Pump();
        Assert.Contains(id, r.Mixer.Stopped);
        Assert.Equal(0, r.Echoes.VoiceCount);
    }

    /// <summary>
    /// The echo reads the engine's ring, which may not exist yet on the frame the car comes into
    /// earshot, so the start can fail. Asking the mixer whether the voice is playing — not remembering
    /// that it was asked for — is what makes the next frame try again; once it plays it is only moved.
    /// </summary>
    [Fact]
    public void AnEchoThatDidNotStartIsAskedForAgain()
    {
        var r = WithWall();
        r.Update(Car, Engine);
        r.Update(Car, Engine);      // the first start has not reached the mixer
        r.Pump();
        int id = Assert.Single(r.EchoIds);
        Assert.Equal(2, r.Mixer.Started.Count(e => e.EntityId == id));

        r.Update(Car, Engine);
        r.Pump();
        Assert.Equal(2, r.Mixer.Started.Count(e => e.EntityId == id));
        Assert.Equal(1, r.Echoes.VoiceCount);
    }

    /// <summary>
    /// When the car goes, its echoes go with it, at once: every voice stopped, none left to fade, and
    /// another car's echoes untouched. Each car's echoes have ids of their own in the echo band.
    /// </summary>
    [Fact]
    public void ForgettingACarStopsItsEchoesAndOnlyItsEchoes()
    {
        var r = WithWall();
        r.Update(Car, Engine);
        r.Update(OtherCar, Engine + new Vector3(1f, 0f, 0f));
        r.Pump();
        var ids = r.EchoIds.ToList();
        Assert.Equal(2, ids.Count);
        Assert.All(ids, id => Assert.True(EngineReflections.IsEchoVoice(id)));
        int mine = r.Mixer.Started.First(e => e.EchoOfEntity == Car).EntityId;
        int theirs = r.Mixer.Started.First(e => e.EchoOfEntity == OtherCar).EntityId;
        Assert.NotEqual(mine, theirs);

        r.Echoes.Forget(Car, r.Audio);
        r.Pump();
        Assert.Contains(mine, r.Mixer.Stopped);
        Assert.DoesNotContain(theirs, r.Mixer.Stopped);
        Assert.Equal(1, r.Echoes.VoiceCount);

        r.Echoes.Forget(Car, r.Audio);      // twice is harmless
        Assert.Equal(1, r.Echoes.VoiceCount);
    }

    /// <summary>
    /// One budget for the map, spent on what is audible. With room for one reflection voice and two
    /// cars at the same wall, a loud one and a quiet one, the frame's close sets the floor at the
    /// loud car's echo: from then on it keeps its voice and the quiet car's echo fades away. With room
    /// for both, the floor is zero.
    /// </summary>
    [Fact]
    public void TheBudgetGoesToTheLoudestReflectionsOnTheMap()
    {
        var r = WithWall();
        r.Echoes.MaxReflectionVoices = 1;
        var quietAt = Engine + new Vector3(1f, 0f, 0f);

        r.Update(Car, Engine, volume: 0.9f);
        r.Update(OtherCar, quietAt, volume: 0.1f);
        r.Echoes.EndFrame();
        r.Pump();
        Assert.Equal(2, r.Echoes.VoiceCount);       // the first frame had no floor yet
        Assert.True(r.Echoes.AudibilityFloor > 0f);

        int loud = r.Mixer.Started.First(e => e.EchoOfEntity == Car).EntityId;
        int quiet = r.Mixer.Started.First(e => e.EchoOfEntity == OtherCar).EntityId;
        for (int frame = 0; frame < 4; frame++)
        {
            r.Update(Car, Engine, volume: 0.9f, dt: 0.25f);
            r.Update(OtherCar, quietAt, volume: 0.1f, dt: 0.25f);
            r.Echoes.EndFrame();
            r.Pump();
        }
        Assert.Contains(quiet, r.Mixer.Stopped);
        Assert.DoesNotContain(loud, r.Mixer.Stopped);
        Assert.Equal(1, r.Echoes.VoiceCount);

        r.Echoes.MaxReflectionVoices = 16;
        r.Update(Car, Engine, volume: 0.9f);
        r.Update(OtherCar, quietAt, volume: 0.1f);
        r.Echoes.EndFrame();
        Assert.Equal(0f, r.Echoes.AudibilityFloor);
    }

    /// <summary>
    /// A long wall built as a row of blocks is one mirror. A car driving 24 m along it sweeps the
    /// bounce point across three seams, and one echo voice follows it the whole way: keyed by block,
    /// each seam was a voice torn down and another started, several times a second per car.
    /// </summary>
    [Fact]
    public void ARowOfBlocksIsOneWallAndOneVoiceFollowsTheBounceAlongIt()
    {
        var r = new Rig();
        for (int k = 0; k < 4; k++)
            r.Box(new Vector3(-15f + 10f * k, 3f, -10f), new Vector3(10f, 6f, 0.5f));
        r.Echoes.SyncGeometry(r.World);

        var ear = new Vector3(0f, 1.6f, -2f);
        for (float x = -12f; x <= 12f; x += 0.5f)
        {
            r.Update(Car, new Vector3(x, 0.5f, 0f), ear: ear);
            r.Pump();
        }
        Assert.Single(r.EchoIds);
        Assert.Empty(r.Mixer.Stopped);
        Assert.Equal(1, r.Echoes.VoiceCount);
    }
}
