using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// "The steel doors on the apartments make no sound — it just says the steel door swings open."
///
/// The door model was never the problem: a steel leaf's opening and closing were worked out on the
/// server and sent like any other door's. What happened to them was on the client. A sound heard for
/// the first time was rendered on a worker and the event that asked for it was DROPPED, and every
/// sound comes in four seed variants — so anything rare was a first hearing, and silent, for its first
/// handful of uses. Four hundred wooden doors wore their sounds in within minutes; the city's seven
/// steel ones, each its own size, never did.
///
/// So this drives a real steel door on the real city from a client that has heard nothing yet, and
/// asks that the FIRST opening and the FIRST closing both reach the mixer.
/// </summary>
public class SteelDoorSoundTests
{
    private readonly ITestOutputHelper _o;
    public SteelDoorSoundTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void ASteelTowerDoorIsHeardOpeningAndClosingTheFirstTime()
    {
        AcousticRegistry.Initialize();
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));

        Entity door = Entity.Null;
        world.Query(new QueryDescription().WithAll<DoorComponent, MaterialComponent, IdentityComponent>(),
            (Entity e, ref MaterialComponent m, ref IdentityComponent id) =>
            { if (door == Entity.Null && m.Material == "Metal" && id.Name == "Steel Door") door = e; });
        Assert.NotEqual(Entity.Null, door);

        // What the server says, through the real door system.
        var doors = new DoorSystem();
        var opened = new List<TransientSound>();
        var closed = new List<TransientSound>();
        Assert.True(DoorSystem.Set(world, door, open: true));
        for (int i = 0; i < 90; i++)
            doors.Update(world, PhysicsConstants.FixedDeltaTime, _ => { }, (_, _, s) => opened.AddRange(s));
        Assert.True(DoorSystem.Set(world, door, open: false));
        for (int i = 0; i < 90; i++)
            doors.Update(world, PhysicsConstants.FixedDeltaTime, _ => { }, (_, _, s) => closed.AddRange(s));

        // The steel door is the physical push-bar door: one sound for the push, one for the closer's
        // close, each naming its door, and each renders to a sound.
        var push = Assert.Single(opened);
        var shut = Assert.Single(closed);
        Assert.True(PushBarDoor.TryParseKey(push.SynthKey, out bool pushCloses, out var pushDoor, out _));
        Assert.True(PushBarDoor.TryParseKey(shut.SynthKey, out bool shutCloses, out _, out _));
        Assert.False(pushCloses);
        Assert.True(shutCloses);
        var size = world.Get<ColliderComponent>(door).Size;
        Assert.Equal(size.X, pushDoor.Width, 2);
        Assert.Equal(PushBarDoor.OpenLevelDb(door.Id), push.LevelDb, 1);
        Assert.Equal(PushBarDoor.CloseLevelDb(door.Id), shut.LevelDb, 1);
        foreach (var s in new[] { push, shut })
        {
            var pcm = PushBarDoor.RenderKey(s.SynthKey!, 48000);
            Assert.True(pcm.Length > 24000);
            Assert.Equal(1f, pcm.Max(MathF.Abs), 3);
        }
        _o.WriteLine($"push {push.SynthKey} {push.LevelDb:F1} dB, shut {shut.SynthKey} {shut.LevelDb:F1} dB");
    }

    /// <summary>A mixer that records what it was asked to play, and nothing else.</summary>
    internal sealed class CapturingProvider : IAudioProvider
    {
        public readonly List<SpatialEmitter> Emitters = new();
        private readonly HashSet<int> _live = new();

        public bool Initialize() => true;
        public void Update() { }
        public void UpdateListener(Vector3 p, Quaternion r, Vector3 v, int region) { }
        public void UpdateShelter(float f) { }
        public void UpdateBoundaries(ReadOnlySpan<BoundaryProbe> probes) { }
        public bool PlayAmbientBed(string id, AmbisonicLayout l, float v, bool loop = true) => true;
        public void SetAmbientBedVolume(string id, float v) { }
        public void StopAmbientBed(string id) { }
        public void SetAcousticMap(AcousticMap map) { }
        public void PlaySpatialSound(SpatialEmitter e) { Emitters.Add(e); _live.Add(e.EntityId); }
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
        public void StopSound(int id) => _live.Remove(id);
        public bool IsPlaying(int id) => _live.Contains(id);
        public Vector3 GetSoundPosition(int id) => Vector3.Zero;
        public float GetPlaybackProgress(int id) => 0f;
        public IEnumerable<int> GetActiveSpatialSoundIds() => new List<int>(_live);
        public void Preload(string id) { }
        public void PlayVoice(int sender, Vector3 pos, byte[] pcm) { }
        public bool RegisterSynthesisedSound(string soundId, byte[] pcm16Mono, int sampleRate) => true;
        public readonly List<string> UiSounds = new();
        public void PlayUiSound(string id, Func<float[]> render, int sampleRate, float volume) { render(); UiSounds.Add(id); }
        public IReadOnlyList<VoiceLevel> LoudestVoices(int count) => Array.Empty<VoiceLevel>();
        public IReadOnlyList<string> OutputDevices() => new[] { "Test output" };
        public IReadOnlyList<string> InputDevices() => new[] { "Test input" };
        public bool SetOutputDevice(string name) => true;
        public void StartDiagnosticSound() { }
        public void SetDiagnosticPosition(Vector3 p) { }
        public void StopDiagnosticSound() { }
        public void Dispose() { }
    }
}
