using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// Every command that needs a role, run on a fresh world as a Player (refused, nothing changed or sent)
/// and as staff (something changes, so the refusal proves something). The 2026-10-01 mutation run could
/// delete eighteen role checks unnoticed. <see cref="EveryGatedCommandIsListedHere"/> keeps the list
/// in step with <see cref="CommandHandler"/>.
/// </summary>
public class StaffGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-staffgate-" + Guid.NewGuid().ToString("N"));
    private readonly string? _motdBefore = File.Exists(GameServer.MotdPath) ? File.ReadAllText(GameServer.MotdPath) : null;

    public void Dispose()
    {
        // /setmotd as staff writes the real file in the working folder; put it back as it was.
        try
        {
            if (_motdBefore == null) { if (File.Exists(GameServer.MotdPath)) File.Delete(GameServer.MotdPath); }
            else File.WriteAllText(GameServer.MotdPath, _motdBefore);
        }
        catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Command, arguments, and a role below Admin that has it (Admin alone where none does).</summary>
    public static TheoryData<string, string[], UserRole> GatedCommands => new()
    {
        { "setmotd", new[] { "Closed", "for", "repairs." }, UserRole.Moderator },
        { "announce", new[] { "Everybody", "out." }, UserRole.Moderator },
        { "where", new[] { "other" }, UserRole.Moderator },
        { "locate", new[] { "other" }, UserRole.Moderator },
        { "bring", new[] { "other" }, UserRole.Moderator },
        { "kick", new[] { "other" }, UserRole.Moderator },
        { "mute", new[] { "other", "5" }, UserRole.Moderator },
        { "unmute", new[] { "other" }, UserRole.Moderator },
        { "move", new[] { "150", "150", "6" }, UserRole.Dev },
        { "give", new[] { "torch" }, UserRole.Dev },
        { "spawn", new[] { "Box", "Metal", "1", "1", "1" }, UserRole.Dev },
        { "set_sound", new[] { "BEACONS/low_osc", "1" }, UserRole.Dev },
        { "set_audio_mode", new[] { "Sequential" }, UserRole.Dev },
        { "play_folder", new[] { "BEACONS" }, UserRole.Dev },
        { "start_state", new[] { "BEACONS/low_osc", "BEACONS/low_osc" }, UserRole.Dev },
        { "group", new[] { "shed", "4" }, UserRole.Dev },
        { "ungroup", Array.Empty<string>(), UserRole.Dev },
        { "saveas", new[] { "shed_copy" }, UserRole.Dev },
        { "place", new[] { "shed" }, UserRole.Dev },
        { "origin", Array.Empty<string>(), UserRole.Dev },
        { "at", new[] { "forward", "3" }, UserRole.Dev },
        { "put", new[] { "concrete_wall" }, UserRole.Dev },
        { "undo", Array.Empty<string>(), UserRole.Dev },
        { "addseat", new[] { "driver", "drive" }, UserRole.Dev },
        { "removeseat", new[] { "driver" }, UserRole.Dev },
        { "drivable", new[] { "v8_sports" }, UserRole.Dev },
        { "savemap", Array.Empty<string>(), UserRole.Dev },
        { "edit", new[] { "place", "concrete_wall" }, UserRole.Dev },
        { "weather", new[] { "storm" }, UserRole.Dev },
        { "sessions", Array.Empty<string>(), UserRole.Admin },
        { "user", new[] { "other" }, UserRole.Admin },
        { "account", new[] { "other" }, UserRole.Admin },
        { "throttled", Array.Empty<string>(), UserRole.Admin },
        { "ratelimit", Array.Empty<string>(), UserRole.Admin },
        { "unlock", new[] { "other" }, UserRole.Admin },
        { "setrole", new[] { "other", "dev" }, UserRole.Admin },
        { "grant", new[] { "other", "give" }, UserRole.Dev },
        { "revoke", new[] { "other", "give" }, UserRole.Dev },
        { "role", new[] { "create", "builder", "tp" }, UserRole.Admin },
    };

    private const string Denied = "You do not have permission to execute this command.";

    [Theory]
    [MemberData(nameof(GatedCommands))]
    public void APlayerIsRefusedAndNothingChanges(string command, string[] args, UserRole needs)
    {
        _ = needs;
        var rig = new Rig(_dir, UserRole.Player, command);
        string before = rig.Fingerprint();

        string said = rig.Run(command, args);

        Assert.Equal(Denied, said);
        Assert.Equal(before, rig.Fingerprint());
        Assert.Empty(rig.Sent);
    }

    [Theory]
    [MemberData(nameof(GatedCommands))]
    public void TheRoleItNeedsIsNotRefusedAndDoesSomething(string command, string[] args, UserRole needs)
    {
        var rig = new Rig(_dir, needs, command);
        string before = rig.Fingerprint();

        string said = rig.Run(command, args);

        Assert.NotEqual(Denied, said);
        // For the read-only admin commands the answer itself is the effect.
        Assert.True(rig.Sent.Count > 0 || rig.Fingerprint() != before || ReadOnly(command),
                    $"/{command} as {needs} changed nothing, so its refusal to a Player proves nothing. It said: {said}");
    }

    [Theory]
    [InlineData("sessions", UserRole.Dev)]
    [InlineData("user", UserRole.Dev)]
    [InlineData("throttled", UserRole.Dev)]
    [InlineData("unlock", UserRole.Dev)]
    [InlineData("setrole", UserRole.Dev)]
    [InlineData("role", UserRole.Dev)]
    [InlineData("sessions", UserRole.Moderator)]
    [InlineData("user", UserRole.Moderator)]
    [InlineData("setrole", UserRole.Moderator)]
    [InlineData("grant", UserRole.Moderator)]
    [InlineData("revoke", UserRole.Moderator)]
    public void TheAccountCommandsAreAdminsAlone(string command, UserRole role)
    {
        var rig = new Rig(_dir, role, command);
        string before = rig.Fingerprint();
        Assert.Equal(Denied, rig.Run(command, "other", "dev"));
        Assert.Equal(before, rig.Fingerprint());
    }

    /// <summary>A moderator looks after people and cannot change the world; a developer builds it and
    /// has no power over people.</summary>
    [Theory]
    [InlineData("spawn", UserRole.Moderator, new[] { "Box", "Metal", "1", "1", "1" })]
    [InlineData("put", UserRole.Moderator, new[] { "concrete_wall" })]
    [InlineData("savemap", UserRole.Moderator, new string[0])]
    [InlineData("give", UserRole.Moderator, new[] { "torch" })]
    [InlineData("kick", UserRole.Dev, new[] { "other" })]
    [InlineData("mute", UserRole.Dev, new[] { "other" })]
    [InlineData("bring", UserRole.Dev, new[] { "other" })]
    public void ModeratorsDoNotBuildAndDevelopersDoNotModerate(string command, UserRole role, string[] args)
    {
        var rig = new Rig(_dir, role, command);
        string before = rig.Fingerprint();
        Assert.Equal(Denied, rig.Run(command, args));
        Assert.Equal(before, rig.Fingerprint());
        Assert.Empty(rig.Sent);
    }

    /// <summary>One command given to one player: they can use it, and only it, until it is taken back.</summary>
    [Fact]
    public void AGrantedCommandWorksForThatPlayerAloneUntilRevoked()
    {
        var rig = new Rig(_dir, UserRole.Player, "move");
        rig.Grant("move");
        Assert.NotEqual(Denied, rig.Run("move", "150", "150", "6"));
        Assert.Equal(Denied, rig.Run("spawn", "Box", "Metal", "1", "1", "1"));
        rig.Revoke("move");
        Assert.Equal(Denied, rig.Run("move", "150", "150", "6"));
    }

    /// <summary>A role an administrator makes and gives is announced to its holder (who, what, and what
    /// they can do), and grants exactly that.</summary>
    [Fact]
    public void ACustomRoleIsAnnouncedAndAllowsItsCommandsOnly()
    {
        var rig = new Rig(_dir, UserRole.Admin, "role");
        Assert.StartsWith("Made the role builder", rig.Run("role", "create", "builder", "move", "spawn"));
        Assert.Equal("other is now a builder.", rig.Run("setrole", "other", "builder"));
        var told = rig.SentTo("other").OfType<TextEvent>().Select(t => t.Text).ToList();
        Assert.Contains(told, t => t.StartsWith("tester made you a builder.") && t.Contains("spawn") && t.Contains("move"));
        Assert.True(rig.Other.Can("move"));
        Assert.False(rig.Other.Can("kick"));
        rig.Run("role", "delete", "builder");
        Assert.False(rig.Other.Can("move"));
    }

    /// <summary>The words Cody asked for: "You gave sean 1 AKM." and "cody gave you 1 AKM."</summary>
    [Fact]
    public void GivingSaysWhoGaveHowManyOfWhat()
    {
        var rig = new Rig(_dir, UserRole.Admin, "give");
        string said = rig.Run("give", "other", "torch", "2");
        Assert.StartsWith("You gave other 2 ", said);
        Assert.Contains(rig.SentTo("other").OfType<TextEvent>(), t => t.Text.StartsWith("tester gave you 2 "));
        Assert.StartsWith("You gave other 1 ", rig.Run("give", "other", "torch"));
    }

    /// <summary>Moving somebody else is an administrator's: a developer may move themselves, not people.</summary>
    [Fact]
    public void MovingAnotherPlayerIsAdminsAlone()
    {
        var dev = new Rig(_dir, UserRole.Dev, "move");
        string before = dev.Fingerprint();
        Assert.Equal(Denied, dev.Run("move", "other", "150", "150", "6"));
        Assert.Equal(before, dev.Fingerprint());

        var admin = new Rig(_dir, UserRole.Admin, "move");
        before = admin.Fingerprint();
        Assert.StartsWith("Moved other", admin.Run("move", "other", "150", "150", "6"));
        Assert.NotEqual(before, admin.Fingerprint());
    }

    private static bool ReadOnly(string command) => command is "sessions" or "user" or "account" or "throttled" or "ratelimit" or "where" or "locate";

    [Fact]
    public void APlayerWithEmptyHandsNamingAWeaponFiresNothing()
    {
        var rig = new Rig(_dir, UserRole.Player, "fire");
        string before = rig.Fingerprint();

        string said = rig.Run("fire", "akm");

        Assert.Equal("You are not holding anything you can fire.", said);
        Assert.Equal(before, rig.Fingerprint());
        Assert.Empty(rig.Sent);
    }

    [Fact]
    public void StaffNamingAWeaponFiresIt()
    {
        // Staff can, so the refusal above is the gate.
        var rig = new Rig(_dir, UserRole.Dev, "fire");
        rig.Run("fire", "akm");
        Assert.Contains(rig.Sent, m => m is WorldAudioEvent);
    }

    /// <summary>The gated commands and this list are the same set. Powers within a command
    /// (Permissions.Powers: fire-any, join-private and the rest) have tests of their own.</summary>
    [Fact]
    public void EveryGatedCommandIsListedHere()
    {
        var gated = Permissions.All.Except(Permissions.Powers).ToHashSet();
        var listed = GatedCommands.Select(row => Permissions.Canonical((string)row[0])).ToHashSet();
        Assert.Empty(gated.Except(listed));
        Assert.Empty(listed.Except(gated));
        string source = File.ReadAllText(FindSource("OpenFPS.Server", "Core", "CommandHandler.cs"));
        foreach (var row in GatedCommands)
            Assert.Contains($"case \"{(string)row[0]}\":", source);
    }

    private static string FindSource(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
        }
        // The build output is outside the repository (--artifacts-path): use this file's source tree.
        string here = Path.GetDirectoryName(ThisFile())!;
        return Path.Combine(new[] { here, ".." }.Concat(parts).ToArray());
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    /// <summary>The default map in a temporary folder (so /savemap writes there), the real command handler,
    /// and a player in an empty corner with what the command needs set out.</summary>
    private sealed class Rig
    {
        public readonly List<IMessage> Sent = new();
        private readonly MapManager _maps;
        private readonly CompositeService _composites;
        private readonly GameServer _server;
        private readonly CommandHandler _commands;
        private readonly UserSession _session;
        private readonly UserSession _other;
        private readonly Users _users = new();
        private SessionManager _sessions = null!;
        private string _sessionsOnline() => string.Join(",", _sessions.GetAllSessions().Select(x => x.Username).OrderBy(x => x));
        private readonly string _mapDir;
        private readonly string _compositeDir;
        private const string MapId = "default";
        private static readonly Vector3 Feet = new(140, 0, 140);

        public Rig(string root, UserRole role, string command)
        {
            string dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
            _mapDir = Path.Combine(dir, "maps");
            _compositeDir = Path.Combine(dir, "composites");
            Directory.CreateDirectory(_mapDir);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "default.json"), Path.Combine(_mapDir, "default.json"));

            var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            _maps = new MapManager(new MapRepository(_mapDir), prefabs);
            _maps.Initialize();
            _composites = new CompositeService(_maps, prefabs, new CompositeRepository(_compositeDir));

            var sessions = new SessionManager();
            _sessions = sessions;
            _server = new GameServer(_users);
            _server.Attach(_maps, sessions, new OccupancyService(_maps), new HandsService(_maps));
            _server.Sent = (to, message) =>
            {
                Sent.Add(message);
                if (!_sentTo.TryGetValue(to.Username, out var list)) _sentTo[to.Username] = list = new List<IMessage>();
                list.Add(message);
            };
            _commands = new CommandHandler(sessions, _maps, _server, _composites, new OccupancyService(_maps),
                                           new HandsService(_maps), _users);

            _session = Body(sessions, 1, "tester", role, Feet);
            _other = Body(sessions, 2, "other", UserRole.Player, Feet + new Vector3(0, 0, 8));
            _users.Add("tester", role);
            _users.Add("other", UserRole.Player);
            Prepare(command, prefabs);
            Sent.Clear();
        }

        private UserSession Body(SessionManager sessions, int id, string name, UserRole role, Vector3 at)
        {
            Assert.True(_maps.TryGetMap(MapId, out var world, out _, out _, out _));
            var entity = world.Create(
                new PlayerComponent { ConnectionId = id, Username = name, Role = role },
                EntityType.Player,
                new Transform { Position = at, Rotation = Quaternion.Identity },
                new Velocity(), new MaterialComponent { Material = "Generic" });
            _maps.IndexEntity(MapId, entity);
            var session = new UserSession { ConnectionId = id, Username = name, Role = role, Entity = entity, CurrentMapId = MapId, Welcomed = true };
            sessions.AddSession(id, session);
            return session;
        }

        private void Prepare(string command, PrefabRepository prefabs)
        {
            Entity Spawn(string prefab, Vector3 at) =>
                _maps.SpawnEntity(MapId, w => prefabs.Spawn(w, prefab, at, Quaternion.Identity, Vector3.One));

            void Walls(Vector3 centre)
            {
                for (int i = 0; i < 4; i++) Spawn("concrete_wall", centre + new Vector3((i - 1.5f) * 1.4f, 0f, 0f));
            }

            int Composite()
            {
                Walls(Feet + new Vector3(0, 0, 2));
                int root = _composites.Group(MapId, Feet + new Vector3(0, 0, 2), 4f, "shed", anchored: false, "someone", out _);
                Assert.True(root >= 0);
                return root;
            }

            switch (command)
            {
                case "group":
                    Walls(Feet + new Vector3(0, 0, 2));
                    break;
                case "ungroup": case "saveas": case "addseat":
                    Composite();
                    break;
                case "removeseat": case "drivable":
                {
                    int root = Composite();
                    Assert.True(_composites.AddSeat(MapId, root, "driver", true, Feet + new Vector3(0, 0, 1), 0f, "someone", true, out string error), error);
                    break;
                }
                case "place":
                {
                    int root = Composite();
                    Assert.True(_composites.SaveAsTemplate(MapId, root, "shed", "someone", true, out _, out string error), error);
                    break;
                }
                case "at": case "put":
                    _session.Build.SetOrigin(Feet, 0f);
                    _session.Build.Cursor = new Vector3(0, 1.5f, 4f);
                    break;
                case "undo":
                {
                    var e = Spawn("concrete_wall", Feet + new Vector3(3, 0, 3));
                    _session.Build.SetOrigin(Feet, 0f);
                    _session.Build.Placed_Entities.Add(e.Id);
                    break;
                }
                case "set_sound": case "set_audio_mode": case "play_folder": case "start_state":
                    Spawn("sword", Feet + new Vector3(1, 0, 0));
                    break;
                case "unmute":
                    _other.MutedUntilUtc = DateTime.UtcNow.AddMinutes(5);
                    break;
                case "revoke":
                    _users.SetGrants("other", "give");
                    break;
                case "unlock":
                    // A name with a lock to lift.
                    for (int i = 0; i < AuthService.LockoutAfterFailures; i++) _server.Auth.Check("192.0.2.9", "other", "wrong-password");
                    break;
            }
        }

        public UserSession Other => _other;

        /// <summary>What the server sent to one player (not the replies to the one running commands).</summary>
        public List<IMessage> SentTo(string username) => _sentTo.TryGetValue(username, out var l) ? l : new List<IMessage>();
        private readonly Dictionary<string, List<IMessage>> _sentTo = new();

        public void Grant(string permission)
        {
            var grants = Permissions.Parse(_users.GrantsOf("tester"));
            grants.Add(permission);
            _users.SetGrants("tester", Permissions.Format(grants));
            _session.Grants = grants;
        }

        public void Revoke(string permission)
        {
            var grants = Permissions.Parse(_users.GrantsOf("tester"));
            grants.Remove(permission);
            _users.SetGrants("tester", Permissions.Format(grants));
            _session.Grants = grants;
        }

        public string Run(string command, params string[] args)
        {
            var replies = new List<string>();
            _commands.HandleTextCommand(_session.ConnectionId, new TextCommand { Command = command, Args = args },
                m => { if (m is TextEvent t) replies.Add(t.Text); else Sent.Add(m); });
            _server.DrainCommandBuffer();
            return string.Join(" | ", replies);
        }

        /// <summary>Everything a command could change, as one string: every entity's components, the build
        /// cursor, the saved files, the message of the day, and every player's and account's role.</summary>
        public string Fingerprint()
        {
            var text = new StringBuilder();
            Assert.True(_maps.TryGetMap(MapId, out var world, out _, out _, out var lookup));
            var options = new JsonSerializerOptions { IncludeFields = true };
            foreach (int id in lookup.Keys.OrderBy(k => k))
            {
                var e = lookup[id];
                if (!world.IsAlive(e)) continue;
                text.Append(id).Append(':');
                foreach (var component in world.GetAllComponents(e).Where(c => c != null)
                                                .OrderBy(c => c!.GetType().FullName, StringComparer.Ordinal))
                {
                    text.Append(component!.GetType().Name).Append('=');
                    try { text.Append(JsonSerializer.Serialize(component, component.GetType(), options)); }
                    catch { text.Append(component); }
                    text.Append(';');
                }
                text.Append('\n');
            }

            var b = _session.Build;
            text.Append($"build {b.Placed} {b.Origin} {b.Yaw} {b.Cursor} {b.LastStep} {string.Join(",", b.Placed_Entities)}\n");
            text.Append($"roles {_session.Role} {_other.Role} {_users.RoleOf("other")} grants {_users.GrantsOf("other")} custom {_users.CustomOf("other")} defined {string.Join(",", _server.Roles.Names)}\n");
            text.Append($"other {_sessionsOnline()} muted {_other.MutedUntilUtc > DateTime.UtcNow}\n");
            text.Append($"locks {string.Join(",", _server.Auth.LockedNames().Select(l => l.Name))}\n");
            var env = _server.WorldEnvironment;
            text.Append($"weather {env.CurrentScenario} {env.TargetWind} {env.TargetGustiness} {env.Pinned}\n");
            foreach (string dir in new[] { _mapDir, _compositeDir })
                if (Directory.Exists(dir))
                    foreach (string file in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
                        text.Append(Path.GetFileName(file)).Append(' ')
                            .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).Append('\n');
            text.Append("motd ").Append(GameServer.ReadMotd()).Append('\n');
            return text.ToString();
        }
    }

    private sealed class Users : IUserRepository
    {
        private readonly Dictionary<string, UserRole> _roles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _grants = new(StringComparer.OrdinalIgnoreCase);
        public void Add(string name, UserRole role) => _roles[name] = role;
        public UserRole? RoleOf(string name) => _roles.TryGetValue(name, out var r) ? r : null;
        public string GrantsOf(string name) => _grants.TryGetValue(name, out var g) ? g : "";
        private readonly Dictionary<string, string?> _custom = new(StringComparer.OrdinalIgnoreCase);
        public string? CustomOf(string name) => _custom.TryGetValue(name, out var c) ? c : null;
        public bool SetCustomRole(string username, string? role)
        {
            if (!_roles.ContainsKey(username)) return false;
            _custom[username] = role;
            return true;
        }
        public bool SetGrants(string username, string grants)
        {
            if (!_roles.ContainsKey(username)) return false;
            _grants[username] = grants;
            return true;
        }
        public UserData? GetUser(string username) =>
            _roles.TryGetValue(username.Trim(), out var role)
                ? new UserData { Username = username.Trim().ToLowerInvariant(), Role = role, Permissions = GrantsOf(username.Trim()), CustomRole = CustomOf(username.Trim()) }
                : null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
        public bool SetRole(string username, UserRole role)
        {
            if (!_roles.ContainsKey(username)) return false;
            _roles[username] = role;
            return true;
        }
    }
}
