using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The fire selector on every gun, automatic fire held on Enter, and the admin gun: the admin's alone,
/// never empty, any calibre, and kill, vaporize, freeze and inspect doing what they say to what the
/// round meets. Plus every designed sound rendering clean.
/// </summary>
public class AdminGunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-admingun-{Guid.NewGuid():N}");

    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    // ── The selector ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSelectorStepsThroughTheGunsOwnSettingsAndComesRound()
    {
        var akm = WeaponRegistry.Akm;
        Assert.Equal(FireMode.Semi, FireSelector.Default(akm));
        Assert.Equal(FireMode.Safe, FireSelector.Step(akm, FireMode.Semi, 1));
        Assert.Equal(FireMode.Auto, FireSelector.Step(akm, FireMode.Safe, 1));
        Assert.Equal(FireMode.Auto, FireSelector.Step(akm, FireMode.Semi, -1));
        Assert.Equal("semi", FireSelector.Spoken(akm, FireMode.Semi));
        // A two-position safety's second setting is "fire", as it is marked.
        Assert.Equal("fire", FireSelector.Spoken(WeaponRegistry.M700, FireMode.Semi));
        Assert.False(FireSelector.Has(WeaponRegistry.Glock));
        Assert.False(FireSelector.Has(WeaponRegistry.Revolver357));
        Assert.True(FireSelector.HasAuto(WeaponRegistry.Ar15));
        Assert.False(FireSelector.TryParse(WeaponRegistry.M700, "auto", out _));
    }

    [Fact]
    public void XMovesTheLeverItIsHeardAndSafeFiresNothing()
    {
        var r = new Rig(_dir);
        var gun = r.Arm(r.Shooter, "akm_rifle", "AKM");

        Assert.Equal("Safe.", r.Run(r.Shooter, "selector", "next"));
        Assert.Contains(r.Heard(), s => s.SynthKey == "selector:akm");
        r.ClearHeard();
        Assert.Equal("Safe.", r.Run(r.Shooter, "fire"));
        Assert.Equal(30, r.World.Get<AmmoComponent>(gun).Rounds);
        Assert.Empty(r.Heard());              // no report, no click: the word is all

        Assert.Equal("Auto.", r.Run(r.Shooter, "selector", "next"));
        Assert.Equal("Semi.", r.Run(r.Shooter, "selector", "next"));
        Assert.Equal("Auto.", r.Run(r.Shooter, "selector", "back"));
        Assert.Equal("Safe.", r.Run(r.Shooter, "selector", "safe"));
    }

    [Fact]
    public void AGlockHasNoSelector()
    {
        var r = new Rig(_dir);
        r.Arm(r.Shooter, "glock_pistol", "Glock 17");
        Assert.Equal(FireSelector.NoSelector, r.Run(r.Shooter, "selector", "next"));
    }

    [Fact]
    public void OnAutoTheGunFiresAtItsRateUntilTheTriggerIsLetGo()
    {
        var r = new Rig(_dir);
        var gun = r.Arm(r.Shooter, "akm_rifle", "AKM");
        r.Run(r.Shooter, "selector", "auto");

        r.Run(r.Shooter, "fire");
        Assert.True(r.Combat.IsFiringAutomatic(r.Shooter));
        r.Tick(0.5);                                    // 600 a minute: five more in half a second
        int fired = 30 - r.World.Get<AmmoComponent>(gun).Rounds;
        Assert.InRange(fired, 5, 7);

        r.Run(r.Shooter, "cease");
        Assert.False(r.Combat.IsFiringAutomatic(r.Shooter));
        int after = r.World.Get<AmmoComponent>(gun).Rounds;
        r.Tick(0.5);
        Assert.Equal(after, r.World.Get<AmmoComponent>(gun).Rounds);
    }

    [Fact]
    public void AutoStopsWhenTheMagazineRunsDry()
    {
        var r = new Rig(_dir);
        var gun = r.Arm(r.Shooter, "akm_rifle", "AKM");
        r.World.Get<AmmoComponent>(gun).Rounds = 3;
        r.Run(r.Shooter, "selector", "auto");
        r.Run(r.Shooter, "fire");
        r.Tick(1.0);
        Assert.Equal(0, r.World.Get<AmmoComponent>(gun).Rounds);
        Assert.False(r.Combat.IsFiringAutomatic(r.Shooter));
        Assert.Contains(r.Heard(), s => s.SynthKey == "dryfire:akm");
    }

    // ── Who may have it ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyTheAdminMayHaveTheAdminGun()
    {
        var r = new Rig(_dir);
        Assert.False(Permissions.RoleHas(UserRole.Dev, Permissions.AdminGun));
        Assert.False(Permissions.RoleHas(UserRole.Moderator, Permissions.AdminGun));
        Assert.True(Permissions.RoleHas(UserRole.Admin, Permissions.AdminGun));

        Assert.False(r.Hands.Give(r.Other, AdminGun.PrefabId, 1, out _, out _, out string refused));
        Assert.Contains("admin's alone", refused);

        r.Maps.SpawnPrefab(r.MapId, AdminGun.PrefabId, r.World.Get<Transform>(r.Other.Entity).Position + new Vector3(0.3f, 0, 0));
        Assert.False(r.Hands.Take(r.Other, "admin gun", out string message));
        Assert.Contains("admin's alone", message);
        // The commands are gated too.
        Assert.DoesNotContain("Glock", r.Run(r.Other, "calibre", "glock"));
    }

    [Fact]
    public void TheAdminGunNeverRunsDryAndIsHeardAsItself()
    {
        var r = new Rig(_dir);
        var gun = r.ArmAdmin();
        Assert.True(r.Maps.TryGetMap(r.MapId, out _, out _, out _, out var lookup));
        Assert.Equal((AdminGun.WeaponId, 999, ""), CombatService.Held(r.World, r.Shooter.Entity, lookup));

        for (int i = 0; i < 40; i++) { r.Run(r.Shooter, "fire"); r.Tick(0.1); }
        Assert.Equal(40, r.Heard().Count(s => s.SynthKey == AdminGun.ReportKey("ar15", AdminGun.DefaultReport)));
        Assert.False(r.World.Has<AmmoComponent>(gun));
        Assert.Equal("The admin gun never runs dry.", r.Run(r.Shooter, "reload"));
    }

    [Fact]
    public void CalibreChoosesTheRoundsAndTheReport()
    {
        var r = new Rig(_dir);
        r.ArmAdmin();
        Assert.Equal("Glock 17, 9x19mm.", r.Run(r.Shooter, "calibre", "glock"));
        Assert.Equal("M700, .308 Winchester.", r.Run(r.Shooter, "calibre", ".308"));
        r.Run(r.Shooter, "fire");
        Assert.Contains(r.Heard(), s => s.SynthKey == AdminGun.ReportKey("m700", 1));
        Assert.StartsWith("No calibre called", r.Run(r.Shooter, "calibre", "bazooka"));
        Assert.StartsWith("Report 3", r.Run(r.Shooter, "admingun", "report", "3"));
    }

    // ── The modes ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void XStepsTheModesAndSaysThem()
    {
        var r = new Rig(_dir);
        r.ArmAdmin();
        Assert.Equal("Vaporize.", r.Run(r.Shooter, "selector", "next"));
        Assert.Equal("Freeze.", r.Run(r.Shooter, "selector", "next"));
        Assert.Equal("Inspect.", r.Run(r.Shooter, "selector", "next"));
        Assert.Equal("Kill.", r.Run(r.Shooter, "selector", "next"));
        Assert.Equal("Inspect.", r.Run(r.Shooter, "selector", "back"));
        Assert.Contains(r.Heard(), s => s.SynthKey == AdminGun.ModeKey(AdminGunMode.Inspect));
    }

    [Fact]
    public void KillKillsInOneShot()
    {
        var r = new Rig(_dir);
        r.ArmAdmin();
        var walker = r.Walker(r.Feet + new Vector3(0, 0, 10));
        r.Run(r.Shooter, "fire");
        r.Tick(0.2);
        Assert.True(r.World.Has<DeadComponent>(walker));
        Assert.Contains(r.SentTo(r.Shooter).OfType<HitConfirm>(), c => c.Killed);
    }

    [Fact]
    public void FreezeHoldsAPlayerForTenSecondsAndTellsThem()
    {
        var r = new Rig(_dir);
        r.ArmAdmin();
        r.Run(r.Shooter, "selector", "freeze");
        r.Place(r.Other, r.Feet + new Vector3(0, 0, 8));
        r.Run(r.Shooter, "fire");
        r.Tick(0.2);

        Assert.True(r.World.Has<FrozenComponent>(r.Other.Entity));
        Assert.Equal(100, r.World.Get<HealthComponent>(r.Other.Entity).Current);
        Assert.Contains(r.SaidTo(r.Other), t => t.StartsWith("You are frozen"));
        Assert.Contains(r.SaidTo(r.Shooter), t => t.StartsWith("Froze other"));
        Assert.Contains(r.Events, e => e.Sounds.Any(s => s.SynthKey == AdminGun.HitKey(AdminGunMode.Freeze)));

        r.Tick(AdminGun.FreezeSeconds);
        Assert.False(r.World.Has<FrozenComponent>(r.Other.Entity));
        Assert.Contains("You can move again.", r.SaidTo(r.Other));
    }

    [Fact]
    public void FreezeRefusesWhatDoesNotMove()
    {
        var r = new Rig(_dir);
        r.ArmAdmin();
        r.Run(r.Shooter, "selector", "freeze");
        var crate = r.Crate(r.Feet + new Vector3(0, 1.5f, 8));
        r.Run(r.Shooter, "fire");
        r.Tick(0.2);
        Assert.False(r.World.Has<FrozenComponent>(crate));
        Assert.Contains(r.SaidTo(r.Shooter), t => t.StartsWith("Nothing to freeze"));
    }

    [Fact]
    public void VaporizeRemovesAThingAndKillsAPlayerInstead()
    {
        var r = new Rig(_dir);
        r.ArmAdmin();
        r.Run(r.Shooter, "selector", "vaporize");
        var crate = r.Crate(r.Feet + new Vector3(0, 1.5f, 8));
        int id = crate.Id;
        r.Run(r.Shooter, "fire");
        r.Tick(0.2);
        Assert.False(r.World.IsAlive(crate) && r.World.Has<IdentityComponent>(crate) && r.World.Get<IdentityComponent>(crate).Name == "test crate");
        Assert.Contains(r.SaidTo(r.Shooter), t => t.StartsWith($"Vaporized test crate, id {id}"));
        Assert.Contains(r.Events, e => e.Sounds.Any(s => s.SynthKey == AdminGun.HitKey(AdminGunMode.Vaporize)));

        r.Place(r.Other, r.Feet + new Vector3(0, 0, 8));
        r.Now += 1;
        r.Run(r.Shooter, "fire");
        r.Tick(0.2);
        Assert.True(r.World.IsAlive(r.Other.Entity));
        Assert.True(r.World.Has<DeadComponent>(r.Other.Entity));
        Assert.Contains(r.SaidTo(r.Shooter), t => t == "Players are not vaporized: other is killed instead.");
    }

    [Fact]
    public void TheFloorAndTheGroundAreNeverVaporized()
    {
        var world = World.Create();
        var floor = world.Create(new IdentityComponent { Name = "Kestrel House second floor", PrefabId = "concrete_floor" });
        var slab = world.Create(new IdentityComponent { Name = "something" },
                                new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(10f, 0.3f, 8f), IsSolid = true });
        var wall = world.Create(new IdentityComponent { Name = "north wall", PrefabId = "brick_wall" },
                                new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(10f, 3f, 0.3f), IsSolid = true });
        Assert.True(CombatService.IsGroundLike(world, floor));
        Assert.True(CombatService.IsGroundLike(world, slab));
        Assert.False(CombatService.IsGroundLike(world, wall));
        World.Destroy(world);
    }

    [Fact]
    public void InspectSaysWhatItIsAndHurtsNobody()
    {
        var r = new Rig(_dir);
        r.ArmAdmin();
        r.Run(r.Shooter, "selector", "inspect");
        var walker = r.Walker(r.Feet + new Vector3(0, 0, 10));
        r.Run(r.Shooter, "fire");
        r.Tick(0.2);
        Assert.Equal(100, r.World.Get<HealthComponent>(walker).Current);
        string said = Assert.Single(r.SaidTo(r.Shooter), t => t.StartsWith("someone walking:"));
        Assert.Contains("somebody walking", said);
        Assert.Contains($"Id {walker.Id}", said);
        Assert.Contains("10 metres", said);
        Assert.Contains("owned by nobody", said);
        Assert.Contains(r.Events, e => e.Sounds.Any(s => s.SynthKey == AdminGun.HitKey(AdminGunMode.Inspect)));
    }

    // ── The keys ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void XYAndEnterSendWhatTheyShould()
    {
        var (session, sent) = NewClient();
        session.Press(GameKey.X);
        Assert.DoesNotContain(sent, m => m is TextCommand { Command: "selector" });

        session.HandleMessage(new StatsUpdate { Health = 100, MaxHealth = 100, HeldWeaponId = "akm", HeldRounds = 30 });
        session.Press(GameKey.X);
        Assert.Equal(new[] { "next" }, Assert.IsType<TextCommand>(sent[^1]).Args);
        session.Press(GameKey.X, KeyModifiers.Shift);
        Assert.Equal(new[] { "back" }, Assert.IsType<TextCommand>(sent[^1]).Args);
        int before = sent.Count;
        session.Press(GameKey.Y);
        Assert.Equal(before, sent.Count);       // only the admin gun has a calibre to change

        session.HandleMessage(new StatsUpdate { Health = 100, MaxHealth = 100, HeldWeaponId = AdminGun.WeaponId, HeldRounds = 999 });
        session.Press(GameKey.Y);
        var calibre = Assert.IsType<TextCommand>(sent[^1]);
        Assert.Equal("calibre", calibre.Command);
        session.Press(GameKey.Enter);
        Assert.Equal("fire", Assert.IsType<TextCommand>(sent[^1]).Command);
    }

    [Fact]
    public void NoNewGameKeyIsAScreenReaderKey()
    {
        var (session, _) = NewClient();
        foreach (var key in ClientGameSession.ScreenReaderKeys)
            Assert.False(session.IsBound(InputContext.Gameplay, key));
    }

    // ── The sounds ──────────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> Keys()
    {
        for (int v = 0; v <= AdminGun.ReportVariants; v++)
            foreach (var w in WeaponRegistry.All) yield return new object[] { AdminGun.ReportKey(w.Id, v) };
        foreach (var m in AdminGun.Modes) yield return new object[] { AdminGun.ModeKey(m) };
        foreach (var m in new[] { AdminGunMode.Vaporize, AdminGunMode.Freeze, AdminGunMode.Inspect }) yield return new object[] { AdminGun.HitKey(m) };
        foreach (var k in new[] { "charge", "leave", "arrive", "ready" }) yield return new object[] { TeleporterSounds.Key(k) };
        foreach (var p in new[] { "akm_rifle", "glock_pistol", "m700_rifle", "admin_gun", "sword", "teleporter", "crowbar", "torch", "box" })
            yield return new object[] { HandOverSounds.Key(p) };
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void EveryDesignedSoundRendersCleanAndInsideFullScale(string key)
    {
        Assert.True(AdminGunSynth.TryRender(key, 3, out var pcm), key);
        Assert.True(pcm.Length > 100, key);
        Assert.All(pcm, v => Assert.True(float.IsFinite(v)));
        float peak = pcm.Max(MathF.Abs);
        Assert.InRange(peak, 0.05f, 1.0f);
    }

    [Fact]
    public void TheReportIsTheCalibresOwnWithALayerAndNoQuieter()
    {
        var plain = AdminGunSynth.Report(WeaponRegistry.Ar15, 0, 1);
        var shot = WeaponSynth.MuzzleBlast(WeaponProfile.From(WeaponRegistry.Ar15), 1);
        Assert.Equal(shot.Take(200), plain.Take(200));
        for (int v = 1; v <= AdminGun.ReportVariants; v++)
        {
            var layered = AdminGunSynth.Report(WeaponRegistry.Ar15, v, 1);
            Assert.True(layered.Length > shot.Length || v == 4, $"variant {v}");
            // The shot's own first millisecond is unchanged by the layer, give or take the ceiling.
            float a = shot.Take(48).Max(MathF.Abs), b = layered.Take(48).Max(MathF.Abs);
            Assert.InRange(b / a, 0.8f, 1.3f);
        }
    }

    [Fact]
    public void HandOverKnowsWhatEachThingIs()
    {
        Assert.Equal(HandOverSounds.Kind.Gun, HandOverSounds.KindOf("akm_rifle", out var w));
        Assert.Equal("akm", w!.Id);
        Assert.Equal(HandOverSounds.Kind.Gun, HandOverSounds.KindOf("m700_rifle", out w));
        Assert.Equal("m700", w!.Id);
        Assert.Equal(HandOverSounds.Kind.Sword, HandOverSounds.KindOf("sword", out _));
        Assert.Equal(HandOverSounds.Kind.Teleporter, HandOverSounds.KindOf("teleporter", out _));
        Assert.Equal(HandOverSounds.Kind.Crowbar, HandOverSounds.KindOf("crowbar", out _));
        Assert.Equal(HandOverSounds.Kind.Torch, HandOverSounds.KindOf("torch", out _));
        Assert.Equal(HandOverSounds.Kind.Soft, HandOverSounds.KindOf("box", out _));
        Assert.Equal(HandOverSounds.Key("sword"), HandOverSounds.Sound("sword", Vector3.Zero).SynthKey);
        Assert.Equal(TeleporterSounds.Key(TeleporterSounds.Leave), TeleporterSounds.Sound(TeleporterSounds.Leave, Vector3.Zero).SynthKey);
    }

    [Fact]
    public void TheStunGunCracklesAtSeventeenSparksASecond()
    {
        var pcm = AdminGun.RenderFreeze(48000, 1);
        // Count the sparks: samples far above the hum, at least 30 ms apart.
        int count = 0, last = -100000;
        for (int i = 0; i < pcm.Length; i++)
            if (MathF.Abs(pcm[i]) > 0.5f && i - last > 0.03f * 48000) { count++; last = i; }
        float seconds = AdminGun.HitSeconds(AdminGunMode.Freeze) - 0.25f;
        Assert.InRange(count / seconds, 14f, 20f);
    }

    // ── Rigs ────────────────────────────────────────────────────────────────────────────────────

    private static (ClientGameSession, List<IMessage>) NewClient()
    {
        AcousticRegistry.Initialize();
        var network = new ClientNetworkService();
        var sent = new List<IMessage>();
        network.Sending = sent.Add;
        var session = new ClientGameSession(network, new QuietSpeech(), new Shell(), new AudioEngineFacade(),
            microphone: new NullMicrophoneCapture("none"), enableAudio: false);
        return (session, sent);
    }

    private sealed class QuietSpeech : ISpeechOutput
    {
        public string BackendName => "test";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) { }
        public void Interrupt() { }
        public void Dispose() { }
    }

    private sealed class Shell : IClientShell
    {
        public bool IsGameInputActive { get; set; } = true;
        public event Action<string>? CommandEntered { add { } remove { } }
        public void ShowLoading(string status, bool speak = true) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void ShowGameMenu(Action<GameMenuChoice> chosen) { }
        public void ReturnToMenu() { }
        public void Quit() { }
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }

    /// <summary>The weapons range again: the default map, the real handler and combat service, the clock
    /// in the test's hand. The shooter is the admin, facing north; the other is a player.</summary>
    private sealed class Rig
    {
        public readonly MapManager Maps;
        public readonly HandsService Hands;
        public readonly SessionManager Sessions = new();
        public readonly GameServer Server;
        public readonly CommandHandler Commands;
        public readonly CombatService Combat;
        public readonly string MapId = "default";
        public readonly Vector3 Feet = new(140, 0, 140);
        public World World = null!;
        public SpatialGrid<Entity> Grid = null!;
        public readonly UserSession Shooter, Other;
        public double Now = 100;
        public readonly List<(UserSession To, IMessage Message)> ServerSent = new();
        private int _nextConnection = 1;
        private readonly List<Entity> _statics = new();

        public Rig(string dir)
        {
            string mapDir = Path.Combine(dir, Guid.NewGuid().ToString("N"), "maps");
            Directory.CreateDirectory(mapDir);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "default.json"), Path.Combine(mapDir, "default.json"));
            var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            Maps = new MapManager(new MapRepository(mapDir), prefabs);
            Maps.Initialize();
            Assert.True(Maps.TryGetMap(MapId, out World, out _, out Grid, out _));
            Hands = new HandsService(Maps);
            Server = new GameServer(new NoUsers());
            Server.Attach(Maps, Sessions, new OccupancyService(Maps), Hands);
            Server.Sent = (to, m) => ServerSent.Add((to, m));
            Combat = new CombatService(Maps, Server, Sessions) { Clock = () => Now, HipDispersionRadians = 0f };
            Commands = new CommandHandler(Sessions, Maps, Server, hands: Hands, combat: Combat);
            Shooter = Player("cody", Feet, UserRole.Admin);
            Other = Player("other", Feet + new Vector3(6, 0, 0), UserRole.Player);
        }

        public UserSession Player(string name, Vector3 at, UserRole role)
        {
            int connection = _nextConnection++;
            var entity = World.Create(
                new PlayerComponent { ConnectionId = connection, Username = name },
                EntityType.Player,
                new Transform { Position = at, Rotation = Quaternion.Identity },
                new Velocity(), new MaterialComponent { Material = "Generic" },
                new NameComponent { Name = name },
                new HealthComponent { Current = 100, Max = 100 },
                new ColliderComponent
                {
                    Shape = ColliderShape.Cylinder,
                    Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight, PhysicsConstants.PlayerRadius * 2),
                    IsSolid = true,
                });
            Maps.IndexEntity(MapId, entity);
            var session = new UserSession { ConnectionId = connection, Username = name, Entity = entity, CurrentMapId = MapId, Welcomed = true, Role = role };
            Sessions.AddSession(connection, session);
            return session;
        }

        public void Place(UserSession who, Vector3 at) => World.Get<Transform>(who.Entity).Position = at;

        public Entity Walker(Vector3 at) => Maps.SpawnEntity(MapId, w => w.Create(
            EntityType.NPC,
            new Transform { Position = at, Rotation = Quaternion.Identity },
            new Velocity(),
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.5f, 1.8f, 0.5f), IsSolid = false },
            new NameComponent { Name = "someone walking" },
            new IdentityComponent { Name = "someone walking" },
            new Pedestrian { Voice = "", Pair = "" },
            new HealthComponent { Current = 100, Max = 100 }));

        /// <summary>A crate standing in the way: a fixed, solid box.</summary>
        public Entity Crate(Vector3 at)
        {
            var e = Maps.SpawnEntity(MapId, w => w.Create(
                new Transform { Position = at, Rotation = Quaternion.Identity },
                new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(1f, 1f, 1f), IsSolid = true },
                new IdentityComponent { Name = "test crate", PrefabId = "crate" },
                new MaterialComponent { Material = "Wood" }));
            _statics.Add(e);
            return e;
        }

        public Entity Arm(UserSession who, string prefab, string name)
        {
            var gun = Maps.SpawnPrefab(MapId, prefab, World.Get<Transform>(who.Entity).Position + new Vector3(0.3f, 0, 0));
            Assert.NotEqual(Entity.Null, gun);
            Assert.True(Hands.Take(who, name, out string message), message);
            return gun;
        }

        public Entity ArmAdmin() => Arm(Shooter, AdminGun.PrefabId, "admin gun");

        public string Run(UserSession who, string command, params string[] args)
        {
            RefreshGrid();
            var said = new List<string>();
            Commands.HandleTextCommand(who.ConnectionId, new TextCommand { Command = command, Args = args },
                m => { if (m is TextEvent t) said.Add(t.Text); });
            Server.DrainCommandBuffer();
            return string.Join(" | ", said);
        }

        private void RefreshGrid()
        {
            Grid.Clear();
            World.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(),
                (Entity e, ref Transform t, ref ColliderComponent c) => Grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, false));
            foreach (var e in _statics)
                if (World.IsAlive(e) && World.Has<ColliderComponent>(e))
                    Grid.AddOverlapping(World.Get<Transform>(e).Position, World.Get<ColliderComponent>(e).Size, Quaternion.Identity, e, false);
        }

        public void Tick(double seconds)
        {
            int ticks = (int)Math.Ceiling(seconds / PhysicsConstants.FixedDeltaTime);
            for (int i = 0; i < ticks; i++)
            {
                Now += PhysicsConstants.FixedDeltaTime;
                RefreshGrid();
                Combat.Update(MapId, World);
            }
        }

        public IEnumerable<IMessage> SentTo(UserSession who) => ServerSent.Where(s => s.To == who).Select(s => s.Message);
        public List<string> SaidTo(UserSession who) => SentTo(who).OfType<TextEvent>().Select(t => t.Text).ToList();
        public IEnumerable<WorldAudioEvent> Events => ServerSent.Select(s => s.Message).OfType<WorldAudioEvent>();
        public List<TransientSound> Heard() => SentTo(Shooter).OfType<WorldAudioEvent>().SelectMany(e => e.Sounds).ToList();
        public void ClearHeard() => ServerSent.RemoveAll(s => s.Message is WorldAudioEvent);
    }
}
