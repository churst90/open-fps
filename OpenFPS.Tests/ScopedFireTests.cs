using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
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
/// A shot through the scope, on the real server: the bullet is flown, it arrives when it arrives, and
/// a walking target has to be led. The clock is the test's, stepped a tick at a time the way the
/// server steps it, with the walker moved each tick as the vehicle system would.
/// </summary>
public class ScopedFireTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-scope-{Guid.NewGuid():N}");
    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void TheM700ComesScopedAndIsGivable()
    {
        var r = new Field(_dir);
        Assert.Contains("m700_rifle", r.Hands.GivableItems());
        var gun = r.Arm(r.Shooter);
        var held = CombatService.Held(r.World, r.Shooter.Entity, r.Lookup);
        Assert.Equal("m700", held.WeaponId);
        Assert.Equal(5, held.Rounds);
        Assert.Equal("scope_4_12", held.ScopeId);
        Assert.NotEqual(Entity.Null, gun);
    }

    [Fact]
    public void AShotArrivesWhenTheBulletDoesNotWhenTheTriggerBreaks()
    {
        var r = new Field(_dir);
        r.Arm(r.Shooter);
        var walker = r.Walker(r.Feet + new Vector3(0f, 0f, 300f));
        r.Fire(r.AimAt(r.Feet + new Vector3(0f, 0f, 300f)), elevationMil: ComeUp(300f));
        Assert.Equal(1, r.Combat.BulletsInFlight);
        Assert.Empty(r.HitsOn(walker));

        // 300 m is about 0.43 s. Nothing at a quarter of a second; a hit by half a second.
        r.Run(0.25);
        Assert.Empty(r.HitsOn(walker));
        r.Run(0.3);
        Assert.Single(r.HitsOn(walker));
        Assert.Equal(0, r.Combat.BulletsInFlight);
        // A round spent, and a bolt worked after it.
        Assert.Contains(r.Heard(), s => s.SynthKey == "weapon:m700");
        Assert.Contains(r.Heard(), s => s.SynthKey == "cycle:m700" && s.DelaySeconds > 0f);
    }

    [Fact]
    public void WithoutTheTurretTheDropMissesLowAndTheSpotterSaysSo()
    {
        var r = new Field(_dir);
        r.Arm(r.Shooter);
        var walker = r.Walker(r.Feet + new Vector3(0f, 0f, 500f));
        r.Fire(r.AimAt(r.Feet + new Vector3(0f, 0f, 500f)), elevationMil: 0f);
        r.Run(1.2);
        Assert.Empty(r.HitsOn(walker));
        string call = Assert.Single(r.Said(), t => t.StartsWith("Miss"));
        Assert.Contains("low", call);
        // This range lies off the default map's floor: the round has nothing to end in, and a round that
        // ends in nothing says nothing more.
        Assert.DoesNotContain(r.Said(), t => t.StartsWith("Hit "));
    }

    [Fact]
    public void AWalkingTargetIsMissedUnledAndHitLed()
    {
        const float range = 300f, walk = 1.5f;
        var tof = ExternalBallistics.Fly(WeaponRegistry.M700, range, ExternalBallistics.ZeroAngle(WeaponRegistry.M700, range, 0.038f),
                                         0.038f, Air.Standard, Vector3.Zero).Seconds;

        var unled = new Field(_dir);
        unled.Arm(unled.Shooter);
        var a = unled.Walker(unled.Feet + new Vector3(0f, 0f, range), new Vector3(walk, 0f, 0f));
        unled.Fire(unled.AimAt(unled.Feet + new Vector3(0f, 0f, range)), ComeUp(range));
        unled.Run(1.0);
        Assert.Empty(unled.HitsOn(a));
        string call = Assert.Single(unled.Said(), t => t.StartsWith("Miss"));
        Assert.Contains("left", call);   // it walked right; the bullet went where it had been

        var led = new Field(Path.Combine(_dir, "led"));
        led.Arm(led.Shooter);
        var b = led.Walker(led.Feet + new Vector3(0f, 0f, range), new Vector3(walk, 0f, 0f));
        led.Fire(led.AimAt(led.Feet + new Vector3(walk * tof, 0f, range)), ComeUp(range));
        led.Run(1.0);
        Assert.Single(led.HitsOn(b));
    }

    [Fact]
    public void AnAimFarFromTheServersOwnIsNotTrusted()
    {
        var r = new Field(_dir);
        r.Arm(r.Shooter);
        var behind = r.Walker(r.Feet + new Vector3(0f, 0f, -50f));
        // Facing north on the server; the shot claims to face south, where somebody stands.
        r.Fire((MathF.PI, 0.01f), 0f);
        r.Run(0.5);
        Assert.Empty(r.HitsOn(behind));
    }

    private static float ComeUp(float metres)
        => ExternalBallistics.ComeUpMil(WeaponRegistry.M700, metres, 100f, ScopeRegistry.Scope4To12.SightHeightMetres);

    /// <summary>
    /// Open ground off the edge of the default map's buildings: nothing between the shooter and the
    /// far end of a 600 m range, the real command handler, combat service and prefabs.
    /// </summary>
    private sealed class Field
    {
        public readonly MapManager Maps;
        public readonly HandsService Hands;
        public readonly SessionManager Sessions = new();
        public readonly GameServer Server;
        public readonly CombatService Combat;
        public readonly string MapId = "default";
        public readonly Vector3 Feet = new(140, 0, 140);
        public World World = null!;
        public SpatialGrid<Entity> Grid = null!;
        public Dictionary<int, Entity> Lookup = null!;
        public readonly UserSession Shooter;
        public double Now = 100;
        public readonly List<(UserSession To, IMessage Message)> ServerSent = new();
        private readonly List<(Entity E, Vector3 V)> _walkers = new();

        public Field(string dir)
        {
            string mapDir = Path.Combine(dir, Guid.NewGuid().ToString("N"), "maps");
            Directory.CreateDirectory(mapDir);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "default.json"), Path.Combine(mapDir, "default.json"));
            var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            Maps = new MapManager(new MapRepository(mapDir), prefabs);
            Maps.Initialize();
            Assert.True(Maps.TryGetMap(MapId, out World, out _, out Grid, out Lookup));
            Hands = new HandsService(Maps);
            Server = new GameServer(new NoUsers());
            Server.Attach(Maps, Sessions, new OccupancyService(Maps), Hands);
            Server.Sent = (to, m) => ServerSent.Add((to, m));
            Combat = new CombatService(Maps, Server, Sessions) { Clock = () => Now };
            var entity = World.Create(
                new PlayerComponent { ConnectionId = 1, Username = "shooter" },
                EntityType.Player,
                new Transform { Position = Feet, Rotation = Quaternion.Identity },
                new Velocity(), new MaterialComponent { Material = "Generic" },
                new NameComponent { Name = "shooter" },
                new HealthComponent { Current = 100, Max = 100 },
                new ColliderComponent
                {
                    Shape = ColliderShape.Cylinder,
                    Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight, PhysicsConstants.PlayerRadius * 2),
                    IsSolid = true,
                });
            Maps.IndexEntity(MapId, entity);
            Shooter = new UserSession { ConnectionId = 1, Username = "shooter", Entity = entity, CurrentMapId = MapId, Welcomed = true };
            Sessions.AddSession(1, Shooter);
        }

        public Entity Arm(UserSession who)
        {
            var gun = Maps.SpawnPrefab(MapId, "m700_rifle", World.Get<Transform>(who.Entity).Position + new Vector3(0.3f, 0, 0));
            Assert.NotEqual(Entity.Null, gun);
            Assert.True(Hands.Take(who, World.Get<IdentityComponent>(gun).Name, out string message), message);
            return gun;
        }

        public Entity Walker(Vector3 at, Vector3 velocity = default)
        {
            var e = Maps.SpawnEntity(MapId, w => w.Create(
                EntityType.NPC,
                new Transform { Position = at, Rotation = Quaternion.Identity },
                new Velocity { Linear = velocity },
                new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.5f, 1.8f, 0.5f), IsSolid = false },
                new NameComponent { Name = "someone walking" },
                new IdentityComponent { Name = "someone walking" },
                new Pedestrian { Voice = "", Pair = "" },
                new HealthComponent { Current = 100, Max = 100 }));
            _walkers.Add((e, velocity));
            return e;
        }

        /// <summary>The yaw and pitch from the shooter's eye to a person's chest standing at
        /// <paramref name="feet"/>. Increasing pitch looks down.</summary>
        public (float Yaw, float Pitch) AimAt(Vector3 feet)
        {
            Vector3 eye = Feet + new Vector3(0f, CombatService.EyeHeight, 0f);
            Vector3 to = feet + new Vector3(0f, 1.2f, 0f) - eye;
            float yaw = MathF.Atan2(to.X, to.Z);
            float pitch = -MathF.Atan2(to.Y, new Vector2(to.X, to.Z).Length());
            return (yaw, pitch);
        }

        public void Fire((float Yaw, float Pitch) aim, float elevationMil)
        {
            RefreshGrid();
            Combat.FireScoped(Shooter, new ScopedShot { Yaw = aim.Yaw, Pitch = aim.Pitch, ElevationMil = elevationMil, Magnification = 12f },
                              m => ServerSent.Add((Shooter, m)));
            Combat.Update(MapId, World);
        }

        /// <summary>Runs the server's ticks for so many seconds: the walkers walk, then the combat runs.</summary>
        public void Run(double seconds)
        {
            int ticks = (int)Math.Ceiling(seconds / PhysicsConstants.FixedDeltaTime);
            for (int i = 0; i < ticks; i++)
            {
                Now += PhysicsConstants.FixedDeltaTime;
                foreach (var (e, v) in _walkers)
                    if (World.IsAlive(e)) World.Get<Transform>(e).Position += v * PhysicsConstants.FixedDeltaTime;
                RefreshGrid();
                Combat.Update(MapId, World);
            }
        }

        private void RefreshGrid()
        {
            Grid.Clear();
            World.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(),
                (Entity e, ref Transform t, ref ColliderComponent c) => Grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, false));
        }

        public List<HitConfirm> HitsOn(Entity target)
            => ServerSent.Where(s => s.To == Shooter).Select(s => s.Message).OfType<HitConfirm>().Where(h => h.TargetEntityId == target.Id).ToList();

        public List<string> Said() => ServerSent.Where(s => s.To == Shooter).Select(s => s.Message).OfType<TextEvent>().Select(t => t.Text).ToList();

        public List<TransientSound> Heard() => ServerSent.Where(s => s.To == Shooter).Select(s => s.Message)
            .OfType<WorldAudioEvent>().SelectMany(e => e.Sounds).ToList();
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }
}
