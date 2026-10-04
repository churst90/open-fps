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
/// A gun that holds what it holds, a reload that takes as long as the hands take, a hit that hurts,
/// a chime for the shooter alone, and keys that do what the moment calls for.
///
/// Every server case here runs the real command handler against a real map, with the clock held in
/// the test's hand: a reload's length is a claim about time, and a test that waited for it would be
/// a test nobody runs.
/// </summary>
public class WeaponsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-weapons-{Guid.NewGuid():N}");

    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    // ── Rounds ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FiringSpendsOneRound()
    {
        var r = new Range(_dir);
        var gun = r.Arm(r.Shooter, "akm");

        r.Run(r.Shooter, "fire");

        Assert.Equal(29, r.World.Get<AmmoComponent>(gun).Rounds);
        Assert.Contains(r.Heard(), s => s.SynthKey == "weapon:akm");
    }

    [Fact]
    public void ATriggerFasterThanTheActionDoesNothing()
    {
        var r = new Range(_dir);
        var gun = r.Arm(r.Shooter, "shotgun");

        r.Run(r.Shooter, "fire");
        r.Now += 0.2;                              // a pump takes most of a second to work
        r.Run(r.Shooter, "fire");
        Assert.Equal(5, r.World.Get<AmmoComponent>(gun).Rounds);

        r.Now += WeaponRegistry.Shotgun.SecondsBetweenShots;
        r.Run(r.Shooter, "fire");
        Assert.Equal(4, r.World.Get<AmmoComponent>(gun).Rounds);
    }

    [Fact]
    public void AnEmptyGunClicksAndFiresNothing()
    {
        var r = new Range(_dir);
        var gun = r.Arm(r.Shooter, "glock");
        r.World.Get<AmmoComponent>(gun).Rounds = 0;
        r.ClearHeard();

        string said = r.Run(r.Shooter, "fire");

        Assert.Equal("Empty. R to reload.", said);
        Assert.Equal(0, r.World.Get<AmmoComponent>(gun).Rounds);
        var heard = r.Heard();
        Assert.DoesNotContain(heard, s => s.SynthKey.StartsWith("weapon:"));
        var click = Assert.Single(heard, s => s.SynthKey == "dryfire:glock");
        // Heard from the hands, following the body: others hear it too.
        Assert.True(click.OnBody);
    }

    [Fact]
    public void AReloadTakesItsTimeAndThenFillsFromTheReserve()
    {
        var r = new Range(_dir);
        var gun = r.Arm(r.Shooter, "akm");
        r.World.Get<AmmoComponent>(gun).Rounds = 10;
        int reserve = Arms.Reserve(r.World, r.Shooter.Entity, "7.62x39");
        Assert.Equal(90, reserve);                 // three spare magazines came with it
        r.ClearHeard();

        r.Run(r.Shooter, "reload");
        float seconds = WeaponHandling.ReloadSeconds(WeaponRegistry.Akm, 20, fromEmpty: false);
        Assert.Contains(r.Heard(), s => s.SynthKey == WeaponHandling.ReloadKey(WeaponRegistry.Akm, 20, false));
        Assert.Equal(10, r.World.Get<AmmoComponent>(gun).Rounds);   // not yet

        // The gun cannot be fired while the hands are busy with it.
        r.Now += seconds * 0.5;
        r.Combat.Update(r.MapId, r.World);
        Assert.StartsWith("Still reloading", r.Run(r.Shooter, "fire"));
        Assert.Equal(10, r.World.Get<AmmoComponent>(gun).Rounds);

        r.Now += seconds * 0.5 + 0.01;
        r.Combat.Update(r.MapId, r.World);
        Assert.Equal(30, r.World.Get<AmmoComponent>(gun).Rounds);
        Assert.Equal(70, Arms.Reserve(r.World, r.Shooter.Entity, "7.62x39"));
        Assert.Contains(r.SentTo(r.Shooter).OfType<TextEvent>(), t => t.Text == "30 rounds, 70 spare.");
        Assert.False(r.Combat.IsReloading(r.Shooter));
    }

    [Fact]
    public void AReloadIsCappedByTheMagazineAndByWhatYouCarry()
    {
        var r = new Range(_dir);
        var gun = r.Arm(r.Shooter, "akm");
        Assert.StartsWith("The AKM is full", r.Run(r.Shooter, "reload"));

        r.World.Get<AmmoComponent>(gun).Rounds = 0;
        Arms.AddReserve(r.World, r.Shooter.Entity, "7.62x39", -85);      // five left
        r.Run(r.Shooter, "reload");
        r.Now += WeaponRegistry.Akm.ReloadSeconds + 0.1;
        r.Combat.Update(r.MapId, r.World);
        Assert.Equal(5, r.World.Get<AmmoComponent>(gun).Rounds);
        Assert.Equal(0, Arms.Reserve(r.World, r.Shooter.Entity, "7.62x39"));

        r.World.Get<AmmoComponent>(gun).Rounds = 0;
        Assert.Equal("You have no 7.62x39 rounds left.", r.Run(r.Shooter, "reload"));
    }

    [Fact]
    public void FromEmptyTakesLongerThanATopUp()
    {
        foreach (var w in WeaponRegistry.All.Where(w => w.Feed == WeaponFeed.Magazine))
            Assert.True(WeaponHandling.ReloadSeconds(w, w.MagazineCapacity, true) > WeaponHandling.ReloadSeconds(w, w.MagazineCapacity, false), w.Id);
        // A tube is loaded a shell at a time.
        var pump = WeaponRegistry.Shotgun;
        Assert.True(WeaponHandling.ReloadSeconds(pump, 6, false) > WeaponHandling.ReloadSeconds(pump, 1, false) + 2f);
    }

    [Fact]
    public void PuttingTheGunAwayAbandonsTheReload()
    {
        var r = new Range(_dir);
        var gun = r.Arm(r.Shooter, "akm");
        r.World.Get<AmmoComponent>(gun).Rounds = 3;
        r.Run(r.Shooter, "reload");
        r.Run(r.Shooter, "stow");
        r.Now += 10;
        r.Combat.Update(r.MapId, r.World);
        Assert.Equal(3, r.World.Get<AmmoComponent>(gun).Rounds);
        Assert.Equal(90, Arms.Reserve(r.World, r.Shooter.Entity, "7.62x39"));
    }

    // ── Hits ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AHitWoundsSomebodyWalkingAndOnlyTheShooterIsTold()
    {
        var r = new Range(_dir);
        r.Arm(r.Shooter, "glock");
        var walker = r.Walker(r.Feet + new Vector3(0, 0, 10));

        string said = r.Run(r.Shooter, "fire");

        Assert.Equal(75, r.World.Get<HealthComponent>(walker).Current);
        var confirm = Assert.Single(r.Replies.OfType<HitConfirm>());
        Assert.Equal(walker.Id, confirm.TargetEntityId);
        Assert.False(confirm.Killed);
        // After the chime, what and how far (Cody, 2026-10-04: the chime alone did not say what was hit).
        Assert.Equal("Hit pedestrian at 10 metres.", said);
        // Nobody else hears of it but by the shot.
        Assert.DoesNotContain(r.ServerSent, m => m.Message is HitConfirm);
    }

    [Fact]
    public void AKillIsConfirmedOnceAndTheBodyIsTakenAway()
    {
        var r = new Range(_dir);
        r.Arm(r.Shooter, "akm");
        var walker = r.Walker(r.Feet + new Vector3(0, 0, 10));

        for (int i = 0; i < 5; i++) { r.Run(r.Shooter, "fire"); r.Now += 1; }

        var confirms = r.Replies.OfType<HitConfirm>().ToList();
        Assert.Equal(3, confirms.Count);           // 45 a hit: 55, 10, dead; then nothing to hit
        Assert.Single(confirms, c => c.Killed);
        Assert.True(confirms[^1].Killed);
        Assert.True(r.World.Has<DeadComponent>(walker));
        Assert.Contains(r.Events, e => e.Label == "a body falling");

        r.Now += CombatService.BodySeconds + 0.1;
        r.Combat.Update(r.MapId, r.World);
        Assert.False(r.World.IsAlive(walker));
    }

    [Fact]
    public void APlayerKilledIsToldAndGetsUpAtTheSpawn()
    {
        var r = new Range(_dir);
        r.Arm(r.Shooter, "akm");
        r.Place(r.Other, r.Feet + new Vector3(0, 0, 8));

        for (int i = 0; i < 3; i++) { r.Run(r.Shooter, "fire"); r.Now += 1; }

        Assert.Contains(r.SentTo(r.Other).OfType<TextEvent>(), t => t.Text == "You died.");
        Assert.Contains(r.SentTo(r.Other).OfType<TextEvent>(), t => t.Text.StartsWith("You are hit."));
        Assert.True(r.World.Has<DeadComponent>(r.Other.Entity));
        Assert.Equal("You are dead.", r.Run(r.Other, "fire"));

        r.Now += CombatService.PlayerRespawnSeconds + 0.1;
        r.Combat.Update(r.MapId, r.World);
        Assert.False(r.World.Has<DeadComponent>(r.Other.Entity));
        Assert.Equal(100, r.World.Get<HealthComponent>(r.Other.Entity).Current);
        Assert.Contains(r.SentTo(r.Other), m => m is PlayerSpawned);
        Assert.Equal(r.Maps.GetSpawnPoint(r.MapId).Position, r.World.Get<Transform>(r.Other.Entity).Position);
    }

    [Fact]
    public void BuckshotSpreadsWithDistance()
    {
        var shotgun = WeaponRegistry.Shotgun;
        Assert.Equal(shotgun.Damage * shotgun.PelletsPerShot, shotgun.DamageAt(10f));    // all nine land
        Assert.True(shotgun.DamageAt(36f) < shotgun.DamageAt(10f) / 3);                   // the pattern is 0.9 m
        Assert.Equal(WeaponRegistry.Akm.Damage, WeaponRegistry.Akm.DamageAt(90f));
        Assert.True(WeaponRegistry.Glock.DamageAt(100f) < WeaponRegistry.Glock.Damage);
    }

    // ── Carrying a loaded gun ───────────────────────────────────────────────────────────────────

    [Fact]
    public void DrawingAndStowingSayWhatIsInTheGun()
    {
        var r = new Range(_dir);
        r.Arm(r.Shooter, "akm");
        string stowed = r.Run(r.Shooter, "stow");
        Assert.Contains("The AKM has 30 rounds in it.", stowed);
        Assert.Equal("You draw the AKM, 30 rounds.", r.Run(r.Shooter, "draw"));
        string inv = r.Run(r.Shooter, "inv");
        Assert.Contains("AKM, 30 rounds, in both hands", inv);
        Assert.Contains("90 rounds of 7.62x39", inv);
        Assert.Contains("The AKM has 30 rounds.", r.Run(r.Shooter, "ammo"));
    }

    [Fact]
    public void PickingAGunUpPocketsItsSpareMagazinesOnce()
    {
        var r = new Range(_dir);
        var gun = r.Arm(r.Shooter, "glock");
        Assert.Equal(51, Arms.Reserve(r.World, r.Shooter.Entity, "9mm"));
        r.Run(r.Shooter, "drop");
        r.Run(r.Shooter, "take");
        Assert.Equal(51, Arms.Reserve(r.World, r.Shooter.Entity, "9mm"));
        Assert.Equal(0, r.World.Get<AmmoComponent>(gun).SpareRounds);
    }

    // ── Ammunition given by staff ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(new[] { "9mm", "60" }, "9mm", 60)]
    [InlineData(new[] { "ammo", "9mm", "60" }, "9mm", 60)]
    [InlineData(new[] { "12", "gauge", "8" }, "12 gauge", 8)]
    [InlineData(new[] { "7.62" }, "7.62x39", 30)]
    public void AmmunitionIsReadFromWhatWasTyped(string[] words, string id, int count)
    {
        Assert.True(CombatService.TryParseAmmo(words, out var ammo, out int n));
        Assert.Equal(id, ammo.Id);
        Assert.Equal(count, n);
    }

    [Fact]
    public void AnItemNameIsNotAmmunition()
    {
        Assert.False(CombatService.TryParseAmmo(new[] { "akm_rifle" }, out _, out _));
        Assert.False(CombatService.TryParseAmmo(new[] { "torch" }, out _, out _));
    }

    [Fact]
    public void StaffGiveAmmunitionInTheSameWordsAsAnItem()
    {
        var r = new Range(_dir);
        r.Shooter.Role = UserRole.Dev;

        Assert.Equal("You gave other 60 rounds of 9mm.", r.Run(r.Shooter, "give", "other", "9mm", "60"));
        Assert.Contains(r.SentTo(r.Other).OfType<TextEvent>(), t => t.Text == "shooter gave you 60 rounds of 9mm.");
        Assert.Equal(60, Arms.Reserve(r.World, r.Other.Entity, "9mm"));

        Assert.Equal("You gave other 1 shell of 12 gauge.", r.Run(r.Shooter, "give", "other", "ammo", "12", "gauge", "1"));
        // A player may not.
        Assert.Equal("You do not have permission to execute this command.", r.Run(r.Other, "give", "9mm", "60"));
    }

    // ── The sound of it ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryHandlingKeyRendersAsLongAsItsRoutineAndNoLonger()
    {
        foreach (var w in WeaponRegistry.All)
        {
            foreach (var key in new[] { WeaponHandling.ReloadKey(w, w.MagazineCapacity, true),
                                        WeaponHandling.ReloadKey(w, 2, false), WeaponHandling.DryFireKey(w) })
            {
                Assert.True(WeaponHandling.TryParseKey(key, out var spec), key);
                var pcm = WeaponHandling.Render(spec, 48000, seed: 3);
                float seconds = WeaponHandling.Seconds(spec);
                Assert.InRange(pcm.Length / 48000f, seconds * 0.95f, seconds + 0.26f);
                Assert.All(pcm, v => Assert.True(float.IsFinite(v)));
                Assert.InRange(pcm.Max(MathF.Abs), 0.99f, 1.0001f);
                Assert.InRange(WeaponHandling.LevelDb(spec), 55f, 85f);
            }
        }
        Assert.False(WeaponHandling.TryParseKey("reload:akm:30", out _));
        Assert.False(WeaponHandling.TryParseKey("reload:nothing:30:e", out _));
    }

    [Fact]
    public void TheHitAndKillChimesAreShortBrightAndDifferent()
    {
        var hit = UiSounds.Render(UiCue.Hit);
        var kill = UiSounds.Render(UiCue.Kill);
        Assert.InRange(hit.Length / (float)UiSounds.SampleRate, 0.05f, 0.15f);
        Assert.InRange(kill.Length / (float)UiSounds.SampleRate, 0.2f, 0.6f);
        Assert.NotEqual(hit.Length, kill.Length);
        Assert.InRange(hit.Max(MathF.Abs), 0.5f, 0.95f);
    }

    // ── The keys ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RWindsTheWindowInAVehicleReloadsAGunAndOtherwiseStows()
    {
        var (session, sent) = NewClient();
        session.Press(GameKey.R);
        Assert.Equal("stow", Assert.IsType<TextCommand>(sent[^1]).Command);

        session.PlayerState.HeldWeaponId = "akm";
        session.Press(GameKey.R);
        Assert.Equal("reload", Assert.IsType<TextCommand>(sent[^1]).Command);

        session.PlayerState.RidingEntityId = 12;
        session.Press(GameKey.R);
        Assert.Equal("window", Assert.IsType<TextCommand>(sent[^1]).Command);

        // Shift+R is still draw, wherever you are.
        session.Press(GameKey.R, KeyModifiers.Shift);
        Assert.Equal("draw", Assert.IsType<TextCommand>(sent[^1]).Command);
    }

    [Fact]
    public void EnterFiresOnlyWithAGunInYourHands()
    {
        var (session, sent) = NewClient();
        session.Press(GameKey.Enter);
        Assert.DoesNotContain(sent, m => m is TextCommand { Command: "fire" });

        session.HandleMessage(new StatsUpdate { Health = 100, MaxHealth = 100, HeldWeaponId = "glock", HeldRounds = 17 });
        session.Press(GameKey.Enter);
        Assert.Equal("fire", Assert.IsType<TextCommand>(sent[^1]).Command);

        session.HandleMessage(new StatsUpdate { Health = 100, MaxHealth = 100 });
        int before = sent.Count;
        session.Press(GameKey.Enter);
        Assert.DoesNotContain(sent.Skip(before), m => m is TextCommand { Command: "fire" });
    }

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

    /// <summary>
    /// A shooting range: the default map, the real command handler and combat service with the
    /// clock in the test's hand, a shooter facing north in an empty corner and somebody else
    /// standing off to the side.
    /// </summary>
    private sealed class Range
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
        public readonly List<IMessage> Replies = new();
        public readonly List<(UserSession To, IMessage Message)> ServerSent = new();
        private int _nextConnection = 1;

        public Range(string dir)
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
            Combat = new CombatService(Maps, Server, Sessions) { Clock = () => Now };
            Commands = new CommandHandler(Sessions, Maps, Server, hands: Hands, combat: Combat);
            Shooter = Player("shooter", Feet);
            Other = Player("other", Feet + new Vector3(6, 0, 0));
        }

        public UserSession Player(string name, Vector3 at)
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
            var session = new UserSession { ConnectionId = connection, Username = name, Entity = entity, CurrentMapId = MapId, Welcomed = true };
            Sessions.AddSession(connection, session);
            return session;
        }

        public void Place(UserSession who, Vector3 at)
        {
            ref var t = ref World.Get<Transform>(who.Entity);
            t.Position = at;
        }

        /// <summary>Somebody walking, as the vehicle system makes them: not solid to the walls.</summary>
        public Entity Walker(Vector3 at) => Maps.SpawnEntity(MapId, w => w.Create(
            EntityType.NPC,
            new Transform { Position = at, Rotation = Quaternion.Identity },
            new Velocity(),
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.5f, 1.8f, 0.5f), IsSolid = false },
            new NameComponent { Name = "someone walking" },
            new IdentityComponent { Name = "someone walking" },
            new Pedestrian { Voice = "", Pair = "" },
            new HealthComponent { Current = 100, Max = 100 }));

        /// <summary>A gun from its prefab, put in a player's hands.</summary>
        public Entity Arm(UserSession who, string weaponId)
        {
            string prefab = weaponId switch { "akm" => "akm_rifle", "glock" => "glock_pistol", _ => "" };
            Entity gun;
            if (prefab.Length > 0) gun = Maps.SpawnPrefab(MapId, prefab, World.Get<Transform>(who.Entity).Position + new Vector3(0.3f, 0, 0));
            else
            {
                var w = WeaponRegistry.Get(weaponId)!;
                gun = Maps.SpawnEntity(MapId, wd => wd.Create(
                    new Transform { Position = World.Get<Transform>(who.Entity).Position + new Vector3(0.3f, 0, 0), Rotation = Quaternion.Identity },
                    new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.1f, 0.1f, 1f), IsSolid = false },
                    new MaterialComponent { Material = "Metal" },
                    new IdentityComponent { Name = w.DisplayName },
                    new ItemComponent { MassKg = 3.5f, Hands = 2, WeaponId = weaponId },
                    EntityType.Item));
            }
            Assert.NotEqual(Entity.Null, gun);
            Assert.True(Hands.Take(who, World.Get<IdentityComponent>(gun).Name, out string message), message);
            return gun;
        }

        /// <summary>Runs a command as a player and returns what was said back; anything else said back
        /// (a HitConfirm) is kept in <see cref="Replies"/>.</summary>
        public string Run(UserSession who, string command, params string[] args)
        {
            RefreshGrid();
            var said = new List<string>();
            Commands.HandleTextCommand(who.ConnectionId, new TextCommand { Command = command, Args = args },
                m => { if (m is TextEvent t) said.Add(t.Text); else Replies.Add(m); });
            Server.DrainCommandBuffer();
            return string.Join(" | ", said);
        }

        /// <summary>The server re-indexes everything that moves every tick; do the same.</summary>
        private void RefreshGrid()
        {
            Grid.Clear();
            World.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(),
                (Entity e, ref Transform t, ref ColliderComponent c) => Grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, false));
        }

        public IEnumerable<IMessage> SentTo(UserSession who) => ServerSent.Where(s => s.To == who).Select(s => s.Message);

        public IEnumerable<WorldAudioEvent> Events => ServerSent.Select(s => s.Message).OfType<WorldAudioEvent>();

        /// <summary>What the shooter heard: every recipient gets its own copy of an event.</summary>
        public List<TransientSound> Heard() => SentTo(Shooter).OfType<WorldAudioEvent>().SelectMany(e => e.Sounds).ToList();

        public void ClearHeard() => ServerSent.RemoveAll(s => s.Message is WorldAudioEvent);
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }

    /// <summary>What a shot struck, in words after the chime (Cody, 2026-10-04): a pedestrian by what they
    /// are, a player by name, the head when it was the head, and how far.</summary>
    [Fact]
    public void AHitSaysWhatAndHowFar()
    {
        var world = Arch.Core.World.Create();
        var walker = world.Create(new OpenFPS.Common.Components.Transform(), new OpenFPS.Server.Systems.Pedestrian());
        var player = world.Create(new OpenFPS.Common.Components.PlayerComponent { Username = "sean" });
        Assert.Equal("Hit pedestrian at 17 metres.", OpenFPS.Server.Core.CombatService.HitWords(world, walker, false, false, 16.6f));
        Assert.Equal("Killed sean in the head at 340 metres.", OpenFPS.Server.Core.CombatService.HitWords(world, player, true, true, 340.2f));
        Arch.Core.World.Destroy(world);
    }
}
