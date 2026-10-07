using System.Numerics;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A thing you put down is heard where it lies ("when I drop items I still don't hear them",
/// 2026-10-04), end to end from the server's give and drop to the client's item beacon. The broadcast
/// chose only from entities with a collider, and no item has one, so a dropped gun never reached a client.
/// </summary>
public class DroppedItemBeaconTests
{
    private readonly ITestOutputHelper _o;
    public DroppedItemBeaconTests(ITestOutputHelper o) => _o = o;

    /// <summary>Where Cody stood when he put down the torch: the carpet of a room on the city.</summary>
    private static readonly Vector3 Feet = new(-19.15f, 0.06f, -99.3f);

    [Fact]
    public void AGunPutDownOnTheCityRingsItsItemBeaconFromTheFloor()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));

        var sessions = new SessionManager();
        var hands = new HandsService(maps);
        var server = new GameServer(new NoUsers());
        server.Attach(maps, sessions, new OccupancyService(maps), hands);
        hands.Carried = server.SyncAudioComponent;   // as Start() wires it

        var body = world.Create(
            new PlayerComponent { ConnectionId = 1, Username = "cody" },
            EntityType.Player,
            new Transform { Position = Feet, Rotation = Quaternion.Identity },
            new Velocity(), new MaterialComponent { Material = "Generic" },
            new NameComponent { Name = "cody" },
            new HealthComponent { Current = 100, Max = 100 },
            new ColliderComponent
            {
                Shape = ColliderShape.Cylinder,
                Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight, PhysicsConstants.PlayerRadius * 2),
                IsSolid = true,
            });
        maps.IndexEntity("city", body);
        var cody = new UserSession { ConnectionId = 1, Username = "cody", Entity = body, CurrentMapId = "city", Welcomed = true };
        sessions.AddSession(1, cody);

        // The client as it stands after arriving: the map streamed, and the server's record of what it
        // was sent the same set (SendMapData).
        var client = new ClientWorldState();
        client.Clear(data.Size);
        foreach (var e in EntityDefinitionFactory.StaticEntities(world))
        {
            client.RegisterDefinition(EntityDefinitionFactory.From(world, e));
            cody.KnownEntities.Add(e.Id);
        }

        // ...and from then on, only what the broadcast sends it.
        var definitionsSent = new List<int>();
        server.Broadcasted = (to, message) =>
        {
            if (to != cody) return;
            if (message is EntityDefinition def) { client.RegisterDefinition(def); definitionsSent.Add(def.EntityId); }
            else if (message is EntityRemoved gone) client.RemoveEntities(gone.EntityIds);
        };
        long tick = 1;
        server.BroadcastForTest(tick++);

        Assert.True(hands.Give(cody, "glock_pistol", 1, out _, out _, out _));
        int gun = hands.List(cody).Ids[0];
        server.BroadcastForTest(tick++);
        // In his hand the client knows it, and it is neither a beacon nor announced as something nearby.
        Assert.True(client.GetSnapshot().Entities.TryGetValue(gun, out var held), "the client was never told about the gun in his hand");
        Assert.Equal("", held!.Definition.Identity.BeaconCategory);
        Assert.False(held.Definition.Identity.Announce);
        Assert.True(hands.Drop(cody, "", out string dropped));
        _o.WriteLine(dropped);
        server.BroadcastForTest(tick++);
        server.BroadcastForTest(tick++);

        _o.WriteLine($"definitions sent for the gun: {definitionsSent.Count(id => id == gun)}");
        var snap = client.GetSnapshot();
        Assert.True(snap.Entities.TryGetValue(gun, out var seen), "the client was never told about the gun");
        Assert.Equal(Beacons.Item, seen!.Definition.Identity.BeaconCategory);
        Assert.True(Vector3.Distance(seen.Transform.Position, Feet) < 1.5f,
                    $"the client has the gun at {seen.Transform.Position}, not at Cody's feet {Feet}");

        // And it rings, through the real acoustics of that room, at the ear of the man who put it down.
        var provider = new VoiceLifecycleTests.RecordingProvider();
        var facade = new AudioEngineFacade(provider);
        facade.InitializeForTest();
        var ear = Feet + new Vector3(0f, 1.6f, 0f);
        facade.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
        var acoustics = new OpenFPS.Client.AudioEngine.Acoustics.SpatialAcoustics(new OpenFPS.Client.Core.SpatialService());
        var aids = new BeaconAids(facade, BeaconPreferences.InMemory(), acoustics);
        for (int i = 0; i < 6; i++)
        {
            aids.Update(client.GetSnapshot(), ear, 10.0 + i, selfId: body.Id);
            for (int k = 0; k < 3; k++) facade.PumpForTest();
        }
        _o.WriteLine("played: " + string.Join(", ", provider.PlayedSounds));
        Assert.Contains(provider.PlayedSounds, s => s.Contains("beacon_item"));
    }

    /// <summary>What the broadcast chooses from takes in an item, collider or none, wherever it is.</summary>
    [Fact]
    public void TheBroadcastChoosesFromEveryItem()
    {
        var world = World.Create();
        try
        {
            var wall = world.Create(new Transform(), new ColliderComponent { Size = Vector3.One, IsSolid = true });
            var gun = world.Create(new Transform(), new ItemComponent { MassKg = 0.9f, Hands = 1 });
            var sound = world.Create(new Transform(), new SoundEmitterComponent());
            var into = new List<(Entity Entity, bool Dynamic, bool Dirty)>();
            GameServer.GatherBroadcastCandidates(world, into);
            Assert.Contains(into, c => c.Entity == wall);
            Assert.Contains(into, c => c.Entity == gun);
            Assert.DoesNotContain(into, c => c.Entity == sound);
            Assert.Equal(2, into.Count);
        }
        finally { World.Destroy(world); }
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }
}
