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

        Assert.Contains(opened, s => s.Character == SoundCharacter.Knock);           // the latch
        Assert.Contains(closed, s => s.Character == SoundCharacter.Knock);           // the blow and the latch
        // The leaf rings, at the note a HOLLOW steel door of its size has: two 1.2 mm skins over a
        // core bend as a sandwich, stiffer for their mass than an 80 mm slab of solid steel — which
        // is what it was reckoned as, at 2.4 tonnes, before the prefab said what it is made of.
        var ring = Assert.Single(closed, s => s.Character == SoundCharacter.Ring);
        var size = world.Get<ColliderComponent>(door).Size;
        float skin = world.Get<DoorComponent>(door).SkinMetres;
        Assert.Equal(0.0012f, skin, 4);
        float perArea = 2f * skin * 7850f + (size.Z - 2f * skin) * 150f;
        float equivalent = MathF.Sqrt(6f * 7850f * skin * (size.Z - skin) * (size.Z - skin) / perArea);
        Assert.Equal(DoorAcoustics.PanelHz(AcousticRegistry.GetProperties("Metal"), size.X, size.Y, equivalent), ring.Hz, 1);
        Assert.True(ring.Hz > DoorAcoustics.PanelHz(AcousticRegistry.GetProperties("Metal"), size.X, size.Y, size.Z));

        // A client that has heard nothing yet, standing in front of the door.
        var client = new ClientWorldState();
        client.Clear(data.Size, data.MinBound, data.MaxBound);
        foreach (var def in EntityDefinitionFactory.StaticDefinitions(world)) client.RegisterDefinition(def);
        var provider = new CapturingProvider();
        var facade = new AudioEngineFacade(provider);
        facade.InitializeForTest();
        var player = new WorldAudioPlayer(facade, new OpenFPS.Client.AudioEngine.Acoustics.SpatialAcoustics(new SpatialService()));

        var t = world.Get<Transform>(door);
        var ear = t.Position + Vector3.Transform(Vector3.UnitZ, t.Rotation) * 1.5f + Vector3.UnitY * 0.5f;
        facade.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);

        var clock = Stopwatch.StartNew();
        // Which of the sounds sent were actually played, by the identity the player gives each one.
        List<TransientSound> Missing(List<TransientSound> sounds, int seed)
        {
            provider.Emitters.Clear();
            player.Receive(new WorldAudioEvent { SourceEntityId = door.Id, Label = "Steel Door", Sounds = sounds, Seed = seed },
                           clock.Elapsed.TotalSeconds);
            // A second of real time, ticking as the game does, so the worker's render races the clock
            // exactly as it does in play.
            double until = clock.Elapsed.TotalSeconds + 1.0;
            while (clock.Elapsed.TotalSeconds < until)
            {
                player.Update(client.GetSnapshot(), ear, clock.Elapsed.TotalSeconds);
                facade.PumpForTest();
                System.Threading.Thread.Sleep(5);
            }
            return sounds.Where(s => !provider.Emitters.Any(e => e.SoundId.StartsWith(
                $"synth:{s.Character}:{(int)MathF.Round(s.Hz)}:{(int)MathF.Round(s.LevelDb)}:"))).ToList();
        }

        var silentOpening = Missing(opened, seed: 1);
        var silentClosing = Missing(closed, seed: 2);
        _o.WriteLine($"first opening: {opened.Count - silentOpening.Count} of {opened.Count} sounds played; "
                   + $"first closing: {closed.Count - silentClosing.Count} of {closed.Count}");

        // Every part, the first time — the latch, the blow, the seal and the ring.
        Assert.Empty(silentOpening);
        Assert.Empty(silentClosing);
    }

    /// <summary>A mixer that records what it was asked to play, and nothing else.</summary>
    private sealed class CapturingProvider : IAudioProvider
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
        public void PlayUiBeep(float hz, float ms) { }
        public void StartDiagnosticSound() { }
        public void SetDiagnosticPosition(Vector3 p) { }
        public void StopDiagnosticSound() { }
        public void Dispose() { }
    }
}
