using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A door, which is two things at once and only one of them is obvious.
///
/// The leaf SWINGS ASIDE. It is solid the whole time; what opening changes is where it is. A door
/// that went insubstantial instead would be one you could walk through while it was shut and standing
/// in front of you, and one whose open leaf was in the way of nothing.
///
/// And the OPENING appears. A door is not really a thing you hear, it is a thing that changes what
/// you can hear through it — so the aperture on its portal is driven by how far the leaf has swung,
/// and the room beyond opens up gradually as it moves. That half needed no new acoustics at all, only
/// something to move the number, and these tests follow that number the whole way to the client's
/// acoustic map because nowhere else does it mean anything.
/// </summary>
public class DoorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-doors-{Guid.NewGuid():N}");

    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    // ── The leaf ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADoorStartsShutWhereItWasPut()
    {
        var f = new Fixture(_dir);
        var door = f.Door(new Vector3(0, 0, 0));
        f.Tick(1);

        Assert.Equal(0f, f.World.Get<DoorComponent>(door).Openness);
        Assert.Equal(0f, f.World.Get<PortalComponent>(door).ApertureSize);
        Assert.Equal(new Vector3(0, 0, 0), f.World.Get<Transform>(door).Position, Near);
    }

    /// <summary>
    /// It takes a moment. A door that teleported between two states could not be heard opening, and
    /// hearing it open is most of what tells you it did.
    /// </summary>
    [Fact]
    public void ItTakesAMomentToSwing()
    {
        var f = new Fixture(_dir);
        var door = f.Door(new Vector3(0, 0, 0));
        Assert.True(DoorSystem.Set(f.World, door, open: true));

        f.Tick(1);
        float justStarted = f.World.Get<DoorComponent>(door).Openness;
        Assert.InRange(justStarted, 0.001f, 0.5f);

        f.Tick(60);                                    // two seconds, comfortably past the swing
        Assert.Equal(1f, f.World.Get<DoorComponent>(door).Openness, 3);
    }

    /// <summary>
    /// The hinge is an EDGE. Swinging about the centre would sweep the leaf through the doorway in
    /// both directions and leave half of it still in the way at ninety degrees.
    /// </summary>
    [Fact]
    public void ItSwingsAboutItsHingedEdgeAndNotItsMiddle()
    {
        var f = new Fixture(_dir);
        var door = f.Door(new Vector3(0, 0, 0));
        float halfWidth = f.World.Get<ColliderComponent>(door).Size.X * 0.5f;

        DoorSystem.Set(f.World, door, open: true);
        f.Tick(60);

        var open = f.World.Get<Transform>(door);
        // The hinged edge has not moved...
        var hinge = new Vector3(halfWidth, 0, 0);
        MathHelper.ToYawPitch(open.Rotation, out float yaw, out _);
        var edgeNow = open.Position + Vector3.Transform(new Vector3(halfWidth, 0, 0),
                                                        Quaternion.CreateFromYawPitchRoll(yaw, 0, 0));
        Assert.Equal(hinge, edgeNow, Near);

        // ...and the leaf has turned a quarter and is no longer across the doorway.
        Assert.Equal(MathF.PI / 2f, MathF.Abs(MathHelper.WrapAngle(yaw)), 2);
        Assert.True(MathF.Abs(open.Position.X) > 0.1f, "the leaf never moved out of the opening");
    }

    /// <summary>The leaf is solid the whole time. What opening does is move it.</summary>
    [Fact]
    public void TheLeafIsSolidOpenOrShut()
    {
        var f = new Fixture(_dir);
        var door = f.Door(new Vector3(0, 0, 0));
        Assert.True(f.World.Get<ColliderComponent>(door).IsSolid);

        DoorSystem.Set(f.World, door, open: true);
        f.Tick(60);
        Assert.True(f.World.Get<ColliderComponent>(door).IsSolid,
                    "an open door you can walk through is a doorway, not a door");
    }

    [Fact]
    public void ItSwingsBackShut()
    {
        var f = new Fixture(_dir);
        var door = f.Door(new Vector3(0, 0, 0));
        DoorSystem.Set(f.World, door, open: true);
        f.Tick(60);

        DoorSystem.Set(f.World, door, open: false);
        f.Tick(60);
        Assert.Equal(0f, f.World.Get<DoorComponent>(door).Openness, 3);
        Assert.Equal(new Vector3(0, 0, 0), f.World.Get<Transform>(door).Position, Near);
    }

    /// <summary>Which edge it hangs on decides which way it sweeps — and for somebody navigating by
    /// ear, an open door on your left is a different fact from one on your right.</summary>
    [Fact]
    public void WhichEdgeItHangsOnDecidesWhichWayItSweeps()
    {
        var f = new Fixture(_dir);
        var right = f.Door(new Vector3(0, 0, 0));
        var left = f.Door(new Vector3(40, 0, 0), hingeSide: -1f);

        DoorSystem.Set(f.World, right, open: true);
        DoorSystem.Set(f.World, left, open: true);
        f.Tick(60);

        MathHelper.ToYawPitch(f.World.Get<Transform>(right).Rotation, out float rightYaw, out _);
        MathHelper.ToYawPitch(f.World.Get<Transform>(left).Rotation, out float leftYaw, out _);
        Assert.True(rightYaw * leftYaw < 0f, "both leaves swung the same way, so the hinge side does nothing");
    }

    // ── The opening ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The half that matters. The aperture follows the leaf, so the room beyond opens up gradually
    /// rather than appearing the instant somebody says "open".
    /// </summary>
    [Fact]
    public void TheOpeningFollowsTheLeaf()
    {
        var f = new Fixture(_dir);
        var door = f.Door(new Vector3(0, 0, 0));
        DoorSystem.Set(f.World, door, open: true);

        var seen = new List<float> { f.World.Get<PortalComponent>(door).ApertureSize };
        for (int i = 0; i < 40; i++) { f.Tick(1); seen.Add(f.World.Get<PortalComponent>(door).ApertureSize); }

        Assert.Equal(0f, seen.First(), 3);             // shut before the first tick of the swing
        Assert.True(seen.Last() > 0.5f, "it never opened");
        // Monotonic: it opens, it does not flicker.
        for (int i = 1; i < seen.Count; i++)
            Assert.True(seen[i] >= seen[i - 1] - 0.001f, $"the opening went backwards at step {i}");
        // ...and it ends up the size of the leaf, because a door makes a hole exactly its own size.
        Assert.Equal(f.World.Get<ColliderComponent>(door).Size.X, seen.Last(), 2);
    }

    /// <summary>
    /// The client is told, and not thirty times a second. An aperture nobody hears about is a door
    /// that opens and changes nothing; one announced every tick is thirty reliable messages for a
    /// thing that takes a second.
    /// </summary>
    [Fact]
    public void TheClientIsToldAsItMovesButNotOnEveryTick()
    {
        var f = new Fixture(_dir);
        var door = f.Door(new Vector3(0, 0, 0));
        DoorSystem.Set(f.World, door, open: true);

        f.Announced.Clear();
        f.Tick(45);                                    // a second and a half: the whole swing

        Assert.True(f.Announced.Count >= 3, $"the client heard about it {f.Announced.Count} time(s); it would not have moved");
        Assert.True(f.Announced.Count <= 12, $"the client heard about it {f.Announced.Count} times in 45 ticks");

        int before = f.Announced.Count;
        f.Tick(60);                                    // sitting still, wide open
        Assert.Equal(before, f.Announced.Count);
    }

    // ── In a building ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A door in a building leads out of that building. Which room a doorway joins is a property of
    /// WHERE IT IS, and until the room existed there was nothing for it to be a doorway into — so the
    /// link is made when the room is derived, and remade whenever the shape changes.
    /// </summary>
    [Fact]
    public void ADoorInABuildingLeadsOutOfIt()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(Fixture.Clear, withDoor: true);

        var door = CompositeService.PartsOf(f.World, root).Single(e => f.World.Has<DoorComponent>(e));
        var room = CompositeService.MembersOf(f.World, root).Single(e => f.World.Has<DerivedRoomComponent>(e));

        var portal = f.World.Get<PortalComponent>(door);
        Assert.Equal(room.Id, portal.RegionAId);
        Assert.Equal(AcousticConstants.GlobalRegionId, portal.RegionBId);
    }

    /// <summary>
    /// A door is a part like a wall, so it is carried by the building — and the swing has to be
    /// written into the part's LOCAL pose, because ParentSystem rewrites every part's world transform
    /// from that every tick and would otherwise overwrite the swing before anybody saw it.
    /// </summary>
    [Fact]
    public void ADoorSwingsCorrectlyOnABuildingThatHasMoved()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(Fixture.Clear, withDoor: true, anchored: false);
        var door = CompositeService.PartsOf(f.World, root).Single(e => f.World.Has<DoorComponent>(e));

        var moved = new Vector3(25, 0, 9);
        f.World.Get<Transform>(f.Entity(root)).Position += moved;

        // Shut, it lies flat in the wall it is part of.
        f.Tick(2);
        MathHelper.ToYawPitch(f.World.Get<Transform>(door).Rotation, out float shutYaw, out _);
        Assert.Equal(0f, MathF.Abs(MathHelper.WrapAngle(shutYaw)), 2);

        DoorSystem.Set(f.World, door, open: true);
        f.Tick(60);

        Assert.Equal(1f, f.World.Get<DoorComponent>(door).Openness, 3);

        // The assertion that matters, and it has to be on the WORLD transform after ParentSystem has
        // run. ParentSystem rewrites every part's world pose from its local one every tick, so a
        // swing written to the transform is overwritten before anyone sees it — and the door would
        // still be near the building and still say it was open while standing flat in the wall.
        MathHelper.ToYawPitch(f.World.Get<Transform>(door).Rotation, out float openYaw, out _);
        Assert.Equal(MathF.PI / 2f, MathF.Abs(MathHelper.WrapAngle(openYaw)), 2);

        // ...and it went with the building rather than being left at the old site.
        float toRoot = Vector3.Distance(f.World.Get<Transform>(door).Position, f.RootPosition(root));
        Assert.True(toRoot < 12f, $"the door ended up {toRoot:F1} m from the building it belongs to");
        Assert.True(Vector3.Distance(f.World.Get<Transform>(door).Position, Fixture.Clear) > 5f,
                    "the door stayed at the old site while the building drove off");
    }

    /// <summary>
    /// Grouping a building CHANGES the frame its doors live in — world becomes parent-local. A door
    /// that kept its old record of where "shut" was would compute its local pose from a world one
    /// and fling the leaf out of the world, which is exactly what happened the first time a shed with
    /// a door in it was grouped: the door opened perfectly, then vanished.
    /// </summary>
    [Fact]
    public void ADoorStillWorksAfterTheBuildingAroundItIsGrouped()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(Fixture.Clear, withDoor: true);
        var door = CompositeService.PartsOf(f.World, root).Single(e => f.World.Has<DoorComponent>(e));

        var whereItWas = f.World.Get<Transform>(door).Position;
        DoorSystem.Set(f.World, door, open: true);
        f.Tick(60);

        float moved = Vector3.Distance(f.World.Get<Transform>(door).Position, whereItWas);
        Assert.True(moved < 2f, $"the leaf travelled {moved:F1} m to open, so it was flung out of the building");
        Assert.True(f.World.Get<PortalComponent>(door).ApertureSize > 0.5f);
    }

    /// <summary>...and taking the building apart puts its frame back, so the door still works loose.</summary>
    [Fact]
    public void ADoorStillWorksAfterTheBuildingIsTakenApart()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(Fixture.Clear, withDoor: true);
        var door = CompositeService.PartsOf(f.World, root).Single(e => f.World.Has<DoorComponent>(e));

        Assert.True(f.Composites.Ungroup(f.MapId, root, "builder", false, out _, out _, out string error), error);
        f.Tick(2);

        var whereItWas = f.World.Get<Transform>(door).Position;
        DoorSystem.Set(f.World, door, open: true);
        f.Tick(60);

        float moved = Vector3.Distance(f.World.Get<Transform>(door).Position, whereItWas);
        Assert.True(moved < 2f, $"the leaf travelled {moved:F1} m to open once it was loose again");
    }

    // ── All the way to the client ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The only place an aperture means anything is the client's acoustic map, so the test goes
    /// there: swing the door on the server, put its definition on the wire as the broadcast would,
    /// and check the opening turns up — and goes again when it shuts.
    /// </summary>
    [Fact]
    public void TheOpeningReachesTheClientsAcousticMapAndLeavesWhenItShuts()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(Fixture.Clear, withDoor: true);
        var door = CompositeService.PartsOf(f.World, root).Single(e => f.World.Has<DoorComponent>(e));

        var client = new ClientWorldState();
        client.SetAcousticMap(AcousticVolumeGenerator.GenerateRegions(
            new List<EntityDefinition>(), new Vector3(400, 60, 400), new Vector3(-200, -10, -200), 0.5f, 0.15f));

        // Shut, it is not an opening at all.
        client.RegisterDefinition(EntityDefinitionFactory.From(f.World, door));
        Assert.False(client.AcousticMap!.Portals.ContainsKey(door.Id));

        DoorSystem.Set(f.World, door, open: true);
        f.Tick(60);
        client.RegisterDefinition(EntityDefinitionFactory.From(f.World, door));
        Assert.True(client.AcousticMap!.Portals.ContainsKey(door.Id), "the door opened and the acoustics never heard");
        Assert.True(client.AcousticMap.Portals[door.Id].Portal.ApertureSize > 0.5f);

        DoorSystem.Set(f.World, door, open: false);
        f.Tick(60);
        client.RegisterDefinition(EntityDefinitionFactory.From(f.World, door));
        Assert.False(client.AcousticMap!.Portals.ContainsKey(door.Id), "it shut and the opening stayed");
    }

    /// <summary>
    /// A refusal names the distance. "No door within five metres" while the door listing cheerfully
    /// reports one at five and a half is the tool contradicting itself, and a player who cannot see
    /// the door has no way to tell which of the two is lying.
    /// </summary>
    [Fact]
    public void BeingOutOfReachSaysHowFarAwayItIs()
    {
        var f = new Fixture(_dir);
        f.Door(new Vector3(0, 0, 12));                 // well beyond arm's length
        var session = f.Player(new Vector3(0, 0, 0));

        f.Command(session, "open");
        Assert.Contains("metres away", f.LastReply);
        Assert.Contains("Get closer", f.LastReply);

        f.Command(session, "doors");
        Assert.Contains("12.0 metres", f.LastReply);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private static readonly VectorComparer Near = new();

    private sealed class VectorComparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.05f;
        public int GetHashCode(Vector3 v) => 0;
    }

    private sealed class Fixture
    {
        public static readonly Vector3 Clear = new(140, 0, 140);

        public readonly MapManager Maps;
        public readonly CompositeService Composites;
        public readonly DoorSystem Doors = new();
        public readonly List<int> Announced = new();
        public readonly string MapId = "default";

        public World World = null!;
        public Dictionary<int, Entity> Lookup = null!;

        private readonly PrefabRepository _prefabs;
        private CommandHandler _commands = null!;
        private GameServer _server = null!;
        private SessionManager _sessions = null!;
        private readonly List<string> _replies = new();

        public string LastReply = "";

        public Fixture(string dir)
        {
            string mapDir = Path.Combine(dir, "maps");
            Directory.CreateDirectory(mapDir);
            foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "maps"), "*.json"))
                File.Copy(file, Path.Combine(mapDir, Path.GetFileName(file)), overwrite: true);

            _prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            Maps = new MapManager(new MapRepository(mapDir), _prefabs);
            Maps.Initialize();
            Composites = new CompositeService(Maps, _prefabs, new CompositeRepository(Path.Combine(dir, "composites")));
            Assert.True(Maps.TryGetMap(MapId, out World, out _, out _, out Lookup));
            AcousticRegistry.Initialize();

            _sessions = new SessionManager();
            _server = new GameServer(new StubUserRepository());
            _commands = new CommandHandler(_sessions, Maps, _server, Composites, new OccupancyService(Maps));
        }

        private sealed class StubUserRepository : IUserRepository
        {
            public UserData? GetUser(string username) => null;
            public bool AddUser(string username, string password, UserRole role) => true;
            public bool VerifyPassword(string username, string password) => false;
        }

        /// <summary>A player standing somewhere, able to type.</summary>
        public UserSession Player(Vector3 at)
        {
            var entity = World.Create(
                new PlayerComponent { ConnectionId = 7, Username = "tester", Role = UserRole.Dev },
                EntityType.Player,
                new Transform { Position = at, Rotation = Quaternion.Identity },
                new Velocity(), new MaterialComponent { Material = "Generic" });
            Maps.IndexEntity(MapId, entity);
            var session = new UserSession { ConnectionId = 7, Username = "tester", Role = UserRole.Dev, Entity = entity, CurrentMapId = MapId };
            _sessions.AddSession(7, session);
            return session;
        }

        /// <summary>One typed command, the whole way through the real dispatch path.</summary>
        public void Command(UserSession session, string command, params string[] args)
        {
            _replies.Clear();
            _commands.HandleTextCommand(session.ConnectionId,
                new TextCommand { Command = command, Args = args },
                m => { if (m is TextEvent t) _replies.Add(t.Text); });
            _server.DrainCommandBuffer();
            LastReply = string.Join(" ", _replies);
        }

        public Entity Entity(int id) => Lookup[id];
        public Vector3 RootPosition(int rootId) => World.Get<Transform>(Entity(rootId)).Position;

        public void Tick(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                Doors.Update(World, PhysicsConstants.FixedDeltaTime, Announced.Add);
                ParentSystem.Update(World, Lookup);
            }
        }

        /// <summary>A door standing on its own, hinged on whichever edge.</summary>
        public Entity Door(Vector3 at, float hingeSide = 1f)
        {
            var e = Maps.SpawnEntity(MapId, w => _prefabs.Spawn(w, "door", at, Quaternion.Identity, Vector3.One));
            Assert.NotEqual(Arch.Core.Entity.Null, e);
            if (hingeSide < 0f) World.Get<DoorComponent>(e).HingeSide = -1f;
            return e;
        }

        /// <summary>A shed with walls all round, and optionally a door in one of them.</summary>
        public int Shed(Vector3 where, bool withDoor, bool anchored = true)
        {
            const float half = 5f, height = 3f;
            Spawn("concrete_floor", where + new Vector3(0, 0.05f, 0), Quaternion.Identity);
            Spawn("concrete_floor", where + new Vector3(0, height, 0), Quaternion.Identity);

            var acrossZ = Quaternion.Identity;
            var acrossX = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f);
            for (int i = 0; i < 5; i++)
            {
                float along = -half + 1f + i * 2f;
                Spawn("concrete_wall", where + new Vector3(along, height / 2f, +half), acrossZ);
                Spawn("concrete_wall", where + new Vector3(along, height / 2f, -half), acrossZ);
                Spawn("concrete_wall", where + new Vector3(+half, height / 2f, along), acrossX);
                Spawn("concrete_wall", where + new Vector3(-half, height / 2f, along), acrossX);
            }
            if (withDoor) Spawn("door", where + new Vector3(0, 1.05f, -half - 0.4f), acrossZ);

            // Let it stand for a moment before grouping, the way a real one does. A door records
            // where "shut" is the first time it is looked at, so a door that is grouped in the same
            // instant it is placed never records a LOOSE pose and never has one to go stale — which
            // is how the frame bug survived a test suite and turned up on a live server.
            Tick(2);

            int root = Composites.Group(MapId, where, half * 2f, "shed", anchored, "builder", out _);
            Tick(2);
            Assert.True(root >= 0, "the shed could not be grouped");
            return root;
        }

        private void Spawn(string prefabId, Vector3 at, Quaternion rotation)
            => Assert.NotEqual(Arch.Core.Entity.Null,
                               Maps.SpawnEntity(MapId, w => _prefabs.Spawn(w, prefabId, at, rotation, Vector3.One)));
    }
}
