using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Services;
using OpenFPS.Server.Systems;

namespace OpenFPS.Tests;

/// <summary>
/// The 2026-10-05 role table (docs/PLAN_2026-10-05.md sections 1 and 2): permissions with a scope (the
/// building verbs on maps you own for everybody, on any map for staff), a developer granting no more than
/// they have, maps of your own, /tp needing a teleporter and /move being the staff tool, premium items,
/// and spawning walkers, vehicles and trains.
/// </summary>
public class RolesScopeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-roles-" + Guid.NewGuid().ToString("N"));
    private const string Denied = "You do not have permission to execute this command.";

    public RolesScopeTests() => Teleporter.ChargeSeconds = 0f;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── Scope ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APlayerBuildsOnTheirOwnMapAndNowhereElse()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        Assert.StartsWith("Spawned Metal Box", rig.Run("spawn", "Box", "Metal", "1", "1", "1"));
        Assert.StartsWith("Build origin set", rig.Run("origin"));
        Assert.StartsWith("Map 'mine' saved.", rig.Run("savemap"));
        Assert.StartsWith("Moved to", rig.Run("move", "10", "10", "0.05"));

        rig.On("theirs");
        Assert.Equal(Denied, rig.Run("spawn", "Box", "Metal", "1", "1", "1"));
        Assert.Equal(Denied, rig.Run("savemap"));
        Assert.Equal(Denied, rig.Run("move", "10", "10", "0.05"));
        rig.On("open");
        Assert.Equal(Denied, rig.Run("put", "concrete_wall"));
    }

    [Fact]
    public void ADeveloperBuildsOnAnyMap()
    {
        var rig = new Rig(_dir, UserRole.Dev);
        rig.On("theirs");
        Assert.StartsWith("Spawned Metal Box", rig.Run("spawn", "Box", "Metal", "1", "1", "1"));
        Assert.StartsWith("Moved to", rig.Run("move", "10", "10", "0.05"));
    }

    [Fact]
    public void OnTheirOwnMapAPlayerMayChangeWhatOthersBuilt()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        rig.Run("origin");
        Assert.Contains("placed", rig.Run("put", "concrete_wall"));
        Assert.Contains("Grouped", rig.Run("group", "shed", "4", "free"));
        Assert.True(rig.Maps.TryGetMap("mine", out var world, out _, out _, out _));
        world.Query(new Arch.Core.QueryDescription().WithAll<CompositeComponent>(), (ref CompositeComponent c) => c.Owner = "someone");
        Assert.Contains("loose part", rig.Run("ungroup"));
    }

    // ── Granting ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADeveloperGrantsAPlayerOnlyWhatTheyHave()
    {
        var rig = new Rig(_dir, UserRole.Dev);
        Assert.Equal("Granted give to other.", rig.Run("grant", "other", "give"));
        Assert.True(rig.Other.Can("give"));
        Assert.Equal("You do not have kick yourself, so you cannot grant or revoke it.", rig.Run("grant", "other", "kick"));
        Assert.Equal("You do not have give-premium yourself, so you cannot grant or revoke it.", rig.Run("grant", "other", "give-premium"));
        Assert.StartsWith("Roles and permissions stay with administrators", rig.Run("grant", "other", "grant"));
        Assert.Equal("Revoked give from other.", rig.Run("revoke", "other", "give"));
        Assert.False(rig.Other.Can("give"));
    }

    [Fact]
    public void ADeveloperCannotChangeStaffOrABiggerCustomRole()
    {
        var rig = new Rig(_dir, UserRole.Dev);
        rig.Users.Add("mod", UserRole.Moderator);
        rig.Users.Add("boss", UserRole.Admin);
        Assert.Equal("You can only change a player's permissions; mod is a moderator.", rig.Run("grant", "mod", "give"));
        Assert.Equal("You can only change a player's permissions; boss is an administrator.", rig.Run("revoke", "boss", "spawn"));

        rig.Server.Roles.Create("keeper");
        rig.Server.Roles.Change("keeper", "kick", add: true);
        rig.Users.SetCustomRole("other", "keeper");
        Assert.StartsWith("other's role can do things you cannot", rig.Run("grant", "other", "give"));
    }

    [Fact]
    public void AnAdministratorGrantsAnythingToAnybody()
    {
        var rig = new Rig(_dir, UserRole.Admin);
        rig.Users.Add("mod", UserRole.Moderator);
        Assert.StartsWith("Granted give-premium to mod.", rig.Run("grant", "mod", "give-premium"));
    }

    // ── The admin checks are permissions now ────────────────────────────────────────────────────

    [Fact]
    public void KickAndMuteRespectProtectedAndPermsNameNeedsPermsAny()
    {
        var rig = new Rig(_dir, UserRole.Moderator, otherRole: UserRole.Admin);
        Assert.Equal("You cannot kick other.", rig.Run("kick", "other"));
        Assert.Equal("You cannot mute other.", rig.Run("mute", "other"));
        Assert.Equal(Denied, rig.Run("perms", "other"));

        var dev = new Rig(_dir, UserRole.Dev);
        Assert.StartsWith("other is a player", dev.Run("perms", "other"));
    }

    // ── The teleporter ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TpWithoutATeleporterSaysSoExactly()
    {
        var rig = new Rig(_dir, UserRole.Dev);
        Assert.Equal("You don't have a teleporter.", rig.Run("tp", "other"));
        Assert.Equal("You don't have a teleporter.", rig.Run("tp", "10", "10", "0.05"));
        Assert.Equal("You don't have a teleporter.", rig.Run("goto", "other"));
    }

    [Fact]
    public void TpWithATeleporterChargesLeavesAndArrives()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.GiveTester("teleporter");
        rig.Sent.Clear();
        string said = rig.Run("tp", "20", "-10", "0.05");
        Assert.Contains("The teleporter charges.", said);
        Assert.Contains("You are at 20.0, -10.0", said);
        var at = rig.Position(rig.Tester);
        Assert.Equal(20f, at.X, 2);
        Assert.Equal(-10f, at.Z, 2);
        var labels = rig.Sent.OfType<WorldAudioEvent>().Select(e => e.Label).ToList();
        Assert.Equal(new[] { Teleporter.Charge, Teleporter.Leave, Teleporter.Arrive },
                     labels.Where(l => l.StartsWith("teleporter:")).Distinct().ToArray());
        Assert.All(rig.Sent.OfType<WorldAudioEvent>().Where(e => e.Label.StartsWith("teleporter:")),
                   e => Assert.Equal(e.Label, e.Sounds[0].SynthKey));
    }

    [Fact]
    public void ATeleporterOnYourBackWorksAndTakesYouToAPlayer()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.GiveTester("teleporter");
        rig.Run("stow");
        string said = rig.Run("tp", "other");
        Assert.Contains("You are beside other.", said);
        Assert.True(Vector3.Distance(rig.Position(rig.Tester), rig.Position(rig.Other)) < 2f);
    }

    [Fact]
    public void AnAdministratorTeleportsWithoutOne()
    {
        var rig = new Rig(_dir, UserRole.Admin);
        Assert.Contains("You are beside other.", rig.Run("tp", "other"));
    }

    [Fact]
    public void ATeleportToAnotherMapFollowsWhoMayEnter()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.GiveTester("teleporter");
        Assert.Equal("You are already on mine.", rig.Run("tp", "mine"));
        Assert.Equal("There is no player, place or map called theirs that you can go to.", rig.Run("tp", "theirs"));
        Assert.Contains("You are on open.", rig.Run("tp", "open"));
        Assert.Equal("open", rig.Tester.CurrentMapId);
        Assert.NotEqual(Entity.Null, rig.Tester.Entity);
        Assert.Contains(rig.Sent.OfType<WorldAudioEvent>(), e => e.Label == Teleporter.Arrive);
    }

    // ── Giving ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADeveloperGivesOrdinaryItemsButNotPremiumOnes()
    {
        var rig = new Rig(_dir, UserRole.Dev);
        Assert.StartsWith("You gave other 1 Torch.", rig.Run("give", "other", "torch"));
        Assert.Equal("Teleporter is a premium item; giving one needs give-premium.", rig.Run("give", "other", "teleporter"));
        Assert.Equal("Vehicles are premium; giving one needs give-premium.", rig.Run("give", "other", "vehicle", "v8_muscle"));
    }

    [Fact]
    public void TheAdministratorGivesATeleporterAndItIsHeardHandedOver()
    {
        var rig = new Rig(_dir, UserRole.Admin, tester: "admin");
        Assert.StartsWith("You gave other 1 Teleporter.", rig.Run("give", "other", "teleporter"));
        var told = rig.SentTo("other");
        Assert.Contains(told.OfType<TextEvent>(), t => t.Text.StartsWith("admin gave you 1 Teleporter."));
        Assert.Contains(told.OfType<WorldAudioEvent>(), e => e.Label == "give:teleporter" && e.Sounds[0].SynthKey == "give:teleporter");
    }

    [Fact]
    public void AGivenVehicleIsParkedBesideThemAndIsTheirs()
    {
        var rig = new Rig(_dir, UserRole.Admin, tester: "admin");
        string said = rig.Run("give", "other", "vehicle", "v8_muscle");
        Assert.StartsWith("You gave other a ", said);
        Assert.Contains(rig.SentTo("other").OfType<WorldAudioEvent>(), e => e.Label == "give:vehicle:v8_muscle");
        var (owner, at) = rig.Composite("mine", c => c.TemplateId == "vehicle:v8_muscle");
        Assert.Equal("other", owner);
        Assert.InRange(Vector3.Distance(at, rig.Position(rig.Other)), 1.5f, 12f);
    }

    [Fact]
    public void NoJetsAndAHelicopterCannotBeFlownYet()
    {
        var rig = new Rig(_dir, UserRole.Admin);
        Assert.Equal("No jets: the airliner cannot be given or spawned.", rig.Run("give", "vehicle", "airliner"));
        string said = rig.Run("give", "other", "vehicle", "helicopter");
        Assert.StartsWith("You gave other a helicopter.", said);
        Assert.Contains(rig.SentTo("other").OfType<TextEvent>(), t => t.Text.Contains("Nobody can fly one yet"));
    }

    // ── Maps ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMapOfYourOwnIsPrivateKeptAndOpenedByInvitation()
    {
        var rig = new Rig(_dir, UserRole.Player);
        string said = rig.Run("map", "new", "yard");
        Assert.StartsWith("Made your map yard", said);
        Assert.Equal("yard", rig.Tester.CurrentMapId);
        Assert.True(rig.Maps.TryGetMapData("yard", out var yard));
        Assert.Equal("tester", yard.OwnerId);
        Assert.False(yard.IsPublic);
        Assert.True(File.Exists(Path.Combine(rig.MapDir, "players", "yard.json")));
        Assert.False(DiscoveryService.CanEnter(rig.Maps, "yard", rig.Other));

        Assert.Equal("other may come into yard.", rig.Run("map", "invite", "other"));
        Assert.True(DiscoveryService.CanEnter(rig.Maps, "yard", rig.Other));
        Assert.Contains(rig.SentTo("other").OfType<TextEvent>(), t => t.Text.Contains("invited you to the map yard"));
        Assert.Equal("other is no longer invited to yard.", rig.Run("map", "uninvite", "other"));
        Assert.False(DiscoveryService.CanEnter(rig.Maps, "yard", rig.Other));
        Assert.StartsWith("yard is public", rig.Run("map", "public"));
        Assert.True(DiscoveryService.CanEnter(rig.Maps, "yard", rig.Other));
        Assert.Contains("yard, public", rig.Run("maps", "mine"));

        // Kept: a server started again on the same folders has the map, its owner and its door.
        var again = new MapManager(new MapRepository(rig.MapDir), rig.Prefabs) { Access = new MapAccessRepository(rig.AccessPath) };
        again.Initialize();
        Assert.True(again.TryGetMapData("yard", out var back));
        Assert.Equal("tester", back.OwnerId);
        Assert.True(back.IsPublic);
    }

    [Fact]
    public void SomebodyElsesMapIsNotYoursToOpenAndTheNumberOfMapsIsCapped()
    {
        var rig = new Rig(_dir, UserRole.Player);
        Assert.Equal("theirs is not yours.", rig.Run("map", "public", "theirs"));
        Assert.StartsWith("A map name is", rig.Run("map", "new", "../x"));
        Assert.Contains("there is already a map called open", rig.Run("map", "new", "open"));
        rig.Run("map", "new", "two");
        rig.Run("map", "new", "three");
        Assert.StartsWith("You have 3 maps", rig.Run("map", "new", "four"));
    }

    // ── Spawning ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APlayerSpawnsAWalkerAndAParkedCarOnTheirOwnMap()
    {
        var rig = new Rig(_dir, UserRole.Player);
        Assert.Contains("walks back and forth here now", rig.Run("spawn", "walker", "Pat"));
        Assert.True(rig.Maps.TryGetMap("mine", out var world, out _, out _, out _));
        int walkers = 0;
        world.Query(new Arch.Core.QueryDescription().WithAll<Pedestrian, NameComponent>(), (ref NameComponent n) => { if (n.Name == "Pat") walkers++; });
        Assert.Equal(1, walkers);
        Assert.True(rig.Maps.TryGetMapData("mine", out var data));
        Assert.Contains(data.Vehicles!, v => v.Name == "Pat" && v.Preset == "walker");

        Assert.Contains("parked beside you, yours", rig.Run("spawn", "vehicle", "i4_economy"));
        Assert.Equal("tester", rig.Composite("mine", c => c.TemplateId == "vehicle:i4_economy").Owner);

        rig.On("theirs");
        Assert.Equal(Denied, rig.Run("spawn", "walker"));
    }

    [Fact]
    public void ATrainGoesOnlyOnATrackThatIsThere()
    {
        var rig = new Rig(_dir, UserRole.Admin);
        Assert.Equal("There is no railway on this map. A train can only go on a track that is already there.", rig.Run("spawn", "train", "metro"));
        Assert.StartsWith("There is no train called zeppelin", rig.Run("spawn", "train", "zeppelin"));

        var railway = MapTemplates.Flat("railway", "");
        railway.IsPublic = true;
        railway.Tracks = new List<TrackData>
        {
            new() { Id = "loop", Waypoints = Enumerable.Range(0, 32).Select(i => new Vector3(40f * MathF.Cos(i * MathF.Tau / 32), 0.05f, 40f * MathF.Sin(i * MathF.Tau / 32))).ToList() },
        };
        railway.Trains = new List<TrainData> { new() { Preset = "light_rail", Track = "loop" } };
        Assert.True(rig.Maps.CreateMap(railway, out string error), error);
        rig.On("railway");
        int before = rig.Server.Rail.CountOn("railway");
        Assert.Contains("on the loop track", rig.Run("spawn", "train", "metro"));
        Assert.Equal(before + 1, rig.Server.Rail.CountOn("railway"));
    }

    /// <summary>/spawn train out takes off a train put on with /spawn train, nearest or named, and every
    /// entity of it goes; the map's own train stays (Cody's freight, 2026-10-07, had no way off).</summary>
    [Fact]
    public void ASpawnedTrainComesOffAndTheMapsOwnStays()
    {
        var rig = new Rig(_dir, UserRole.Admin);
        var railway = MapTemplates.Flat("railway", "");
        railway.IsPublic = true;
        railway.Tracks = new List<TrackData>
        {
            new() { Id = "loop", Waypoints = Enumerable.Range(0, 32).Select(i => new Vector3(80f * MathF.Cos(i * MathF.Tau / 32), 0.05f, 80f * MathF.Sin(i * MathF.Tau / 32))).ToList() },
        };
        railway.Trains = new List<TrainData> { new() { Preset = "light_rail", Track = "loop" } };
        Assert.True(rig.Maps.CreateMap(railway, out string error), error);
        rig.On("railway");
        int own = rig.Server.Rail.CountOn("railway");
        Assert.True(rig.Maps.TryGetMap("railway", out var world, out _, out _, out _));
        int Sources()
        {
            int n = 0;
            world.Query(new Arch.Core.QueryDescription().WithAll<SoundEmitterComponent>(), (ref SoundEmitterComponent em) =>
            {
                if (em.SoundId != null && em.SoundId.StartsWith("rail:", StringComparison.OrdinalIgnoreCase)) n++;
            });
            return n;
        }
        int ownSources = Sources();

        Assert.Equal("There is no train put on with /spawn train on this map.", rig.Run("spawn", "train", "out"));
        Assert.Contains("on the loop track", rig.Run("spawn", "train", "metro"));
        Assert.Contains("on the loop track", rig.Run("spawn", "train", "amtrak"));
        Assert.Equal(own + 2, rig.Server.Rail.CountOn("railway"));

        string named = rig.Run("spawn", "train", "out", "amtrak");
        Assert.Contains("Amtrak", named);
        Assert.EndsWith("is off the track.", named);
        Assert.EndsWith("is off the track.", rig.Run("spawn", "train", "out"));
        Assert.Equal(own, rig.Server.Rail.CountOn("railway"));
        Assert.Equal(ownSources, Sources());
        Assert.Equal("There is no train put on with /spawn train on this map.", rig.Run("spawn", "train", "out"));

        // Gated as /spawn is: a player on somebody else's map may not.
        var player = new Rig(_dir, UserRole.Player);
        player.On("theirs");
        Assert.Equal(Denied, player.Run("spawn", "train", "out"));
    }

    [Fact]
    public void AnAeroplaneIsParkedButTheAirlinerIsRefused()
    {
        var rig = new Rig(_dir, UserRole.Player);
        Assert.Equal("No jets: the airliner cannot be given or spawned.", rig.Run("spawn", "aircraft", "airliner"));
        Assert.Contains("Nobody can fly one yet", rig.Run("spawn", "helicopter"));
    }

    /// <summary>A parked aircraft on your own map is still there after /savemap and a restart, as a
    /// parked car is.</summary>
    [Fact]
    public void AnAeroplaneParkedOnYourOwnMapIsKeptBySavemap()
    {
        var rig = new Rig(_dir, UserRole.Player);
        Assert.Contains("Nobody can fly one yet", rig.Run("spawn", "helicopter"));
        Assert.StartsWith("Map 'mine' saved.", rig.Run("savemap"));

        var maps = new MapManager(new MapRepository(rig.MapDir), rig.Prefabs);
        maps.Initialize();
        new CompositeService(maps, rig.Prefabs, new CompositeRepository(Path.Combine(_dir, "composites-again"))).PlaceRecorded(maps);
        Assert.True(maps.TryGetMap("mine", out var world, out _, out _, out _));
        var parked = new List<string>();
        world.Query(new Arch.Core.QueryDescription().WithAll<VehicleComponent, CompositeComponent>(),
            (ref VehicleComponent v, ref CompositeComponent c) => parked.Add($"{v.VehicleType} {c.Owner}"));
        Assert.Equal(new[] { "helicopter tester" }, parked);
    }

    /// <summary>
    /// The server's own maps are generated (tools/gen_city.py and the rest) and their files must stay
    /// as the generator wrote them. A walker, a train or a parked car spawned on one lasts until a
    /// restart, and /savemap does not write it into the file.
    /// </summary>
    [Fact]
    public void ThingsSpawnedOnAShippedMapAreNotWrittenIntoIt()
    {
        var rig = new Rig(_dir, UserRole.Admin);
        var railway = MapTemplates.Flat("railway", "");
        railway.IsPublic = true;
        railway.Tracks = new List<TrackData>
        {
            new() { Id = "loop", Waypoints = Enumerable.Range(0, 32).Select(i => new Vector3(40f * MathF.Cos(i * MathF.Tau / 32), 0.05f, 40f * MathF.Sin(i * MathF.Tau / 32))).ToList() },
        };
        railway.Trains = new List<TrainData> { new() { Preset = "light_rail", Track = "loop" } };
        Assert.True(rig.Maps.CreateMap(railway, out string error), error);
        rig.On("railway");

        string walker = rig.Run("spawn", "walker", "Pat");
        Assert.Contains("walks back and forth here now", walker);
        Assert.DoesNotContain("/savemap keeps", walker);
        Assert.Contains("on the loop track", rig.Run("spawn", "train", "metro"));
        Assert.Contains("parked beside you", rig.Run("spawn", "vehicle", "i4_economy"));
        Assert.Contains("Nobody can fly one yet", rig.Run("spawn", "helicopter"));
        rig.Run("savemap");

        string file = File.ReadAllText(Path.Combine(rig.MapDir, "railway.json"));
        Assert.DoesNotContain("Pat", file);
        Assert.DoesNotContain("metro", file);
        Assert.DoesNotContain("vehicle:i4_economy", file);
        Assert.DoesNotContain("helicopter", file);
    }

    // ── The rig ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Three flat maps — "mine" (the tester's), "theirs" (other's, private) and "open" (the server's,
    /// public) — and two players standing on "mine": the tester, in the role under test, and "other".
    /// </summary>
    private sealed class Rig
    {
        public readonly List<IMessage> Sent = new();
        private readonly Dictionary<string, List<IMessage>> _sentTo = new();
        public readonly MapManager Maps;
        public readonly PrefabRepository Prefabs;
        public readonly GameServer Server;
        public readonly FakeUsers Users = new();
        public readonly UserSession Tester, Other;
        public readonly string MapDir, AccessPath;
        private readonly CommandHandler _commands;
        private readonly HandsService _hands;
        private readonly SessionManager _sessions = new();

        public Rig(string root, UserRole role, UserRole otherRole = UserRole.Player, string tester = "tester")
        {
            string dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
            MapDir = Path.Combine(dir, "maps");
            AccessPath = Path.Combine(dir, "map_access.json");
            Directory.CreateDirectory(MapDir);
            Prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            Maps = new MapManager(new MapRepository(MapDir), Prefabs) { Access = new MapAccessRepository(AccessPath) };
            Maps.Initialize();
            Assert.True(Maps.CreateMap(MapTemplates.Flat("mine", tester), out string e1), e1);
            Assert.True(Maps.CreateMap(MapTemplates.Flat("theirs", "other"), out string e2), e2);
            var open = MapTemplates.Flat("open", "");
            open.IsPublic = true;
            Assert.True(Maps.CreateMap(open, out string e3), e3);

            var composites = new CompositeService(Maps, Prefabs, new CompositeRepository(Path.Combine(dir, "composites")));
            _hands = new HandsService(Maps);
            Server = new GameServer(Users);
            Server.Attach(Maps, _sessions, new OccupancyService(Maps), _hands);
            Server.Sent = (to, message) =>
            {
                Sent.Add(message);
                if (!_sentTo.TryGetValue(to.Username, out var list)) _sentTo[to.Username] = list = new List<IMessage>();
                list.Add(message);
            };
            _commands = new CommandHandler(_sessions, Maps, Server, composites, new OccupancyService(Maps), _hands, Users);

            Users.Add(tester, role);
            Users.Add("other", otherRole);
            Tester = Body(1, tester, role, new Vector3(5, 0.05f, 5));
            Other = Body(2, "other", otherRole, new Vector3(5, 0.05f, 15));
            Sent.Clear();
        }

        private UserSession Body(int id, string name, UserRole role, Vector3 at)
        {
            var session = new UserSession { ConnectionId = id, Username = name, Role = role, CurrentMapId = "mine", Welcomed = true };
            _sessions.AddSession(id, session);
            Place(session, "mine", at);
            return session;
        }

        /// <summary>Puts a session's body on a map, at a point (its old body, if any, gone).</summary>
        private void Place(UserSession session, string mapId, Vector3 at)
        {
            if (session.Entity != Entity.Null && Maps.TryGetMap(session.CurrentMapId, out var old, out _, out _, out _) && old.IsAlive(session.Entity))
                Maps.DestroyEntity(session.CurrentMapId, session.Entity);
            Assert.True(Maps.TryGetMap(mapId, out var world, out _, out _, out _));
            var entity = world.Create(
                new PlayerComponent { ConnectionId = session.ConnectionId, Username = session.Username, Role = session.Role },
                EntityType.Player,
                new Transform { Position = at, Rotation = Quaternion.Identity },
                new Velocity(), new MaterialComponent { Material = "Generic" },
                new InventoryComponent { ItemEntityIds = new List<int>() },
                new ColliderComponent { Shape = ColliderShape.Cylinder, Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight, PhysicsConstants.PlayerRadius * 2), IsSolid = true });
            Maps.IndexEntity(mapId, entity);
            session.Entity = entity;
            session.CurrentMapId = mapId;
        }

        /// <summary>Moves the tester's body to another map, at the same spot.</summary>
        public void On(string mapId) => Place(Tester, mapId, new Vector3(5, 0.05f, 5));

        public void GiveTester(string prefab)
            => Assert.True(_hands.Give(Tester, prefab, 1, out _, out _, out string why), why);

        public Vector3 Position(UserSession s)
        {
            Assert.True(Maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _));
            return world.Get<Transform>(s.Entity).Position;
        }

        public (string Owner, Vector3 At) Composite(string mapId, Func<CompositeComponent, bool> which)
        {
            Assert.True(Maps.TryGetMap(mapId, out var world, out _, out _, out _));
            (string, Vector3)? found = null;
            world.Query(new Arch.Core.QueryDescription().WithAll<CompositeComponent, Transform>(), (ref CompositeComponent c, ref Transform t) =>
            {
                if (which(c)) found = (c.Owner, t.Position);
            });
            Assert.NotNull(found);
            return found!.Value;
        }

        public List<IMessage> SentTo(string username) => _sentTo.TryGetValue(username, out var l) ? l : new List<IMessage>();

        public string Run(string command, params string[] args)
        {
            var replies = new List<string>();
            _commands.HandleTextCommand(Tester.ConnectionId, new TextCommand { Command = command, Args = args },
                m => { if (m is TextEvent t) replies.Add(t.Text); else Sent.Add(m); });
            Server.DrainCommandBuffer();
            return string.Join(" | ", replies);
        }
    }

    private sealed class FakeUsers : IUserRepository
    {
        private readonly Dictionary<string, UserRole> _roles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _grants = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _custom = new(StringComparer.OrdinalIgnoreCase);
        public void Add(string name, UserRole role) => _roles[name] = role;
        public bool SetCustomRole(string username, string? role) { if (!_roles.ContainsKey(username)) return false; _custom[username] = role; return true; }
        public bool SetGrants(string username, string grants) { if (!_roles.ContainsKey(username)) return false; _grants[username] = grants; return true; }
        public UserData? GetUser(string username) =>
            _roles.TryGetValue(username.Trim(), out var role)
                ? new UserData
                {
                    Username = username.Trim().ToLowerInvariant(), Role = role,
                    Permissions = _grants.TryGetValue(username.Trim(), out var g) ? g : "",
                    CustomRole = _custom.TryGetValue(username.Trim(), out var c) ? c : null,
                }
                : null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
        public bool SetRole(string username, UserRole role) { if (!_roles.ContainsKey(username)) return false; _roles[username] = role; return true; }
    }
}
