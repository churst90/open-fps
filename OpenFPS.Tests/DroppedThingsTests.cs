using System.Diagnostics;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Geometry;
using OpenFPS.Materials;
using OpenFPS.Physics;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using OpenFPS.Sound;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Dropped and thrown things as real bodies (docs/PHYSICS_BOUNDARY.md 18.2 step 7, 18.11): the server's body
/// world on the default map's own floor, an item let go from the hand at the height it was held, the fall's
/// speed read from the first contact, a bounce heard as a second impact, a body that lies still leaving the
/// world, and a throw that follows the pitch the right way up.
/// </summary>
public class DroppedThingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-drops-{Guid.NewGuid():N}");
    private readonly ITestOutputHelper _out;

    public DroppedThingsTests(ITestOutputHelper output)
    {
        _out = output;
        AcousticRegistry.Initialize();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    // ── The fall and the first contact ──────────────────────────────────────────────────────────

    [Fact]
    public void ADroppedItemFallsToTheFloorAtTheSpeedOfItsFallAndStaysThere()
    {
        using var f = new Fixture(_dir);
        var player = f.Player(new Vector3(0f, f.Floor(0f, 10f), 10f));
        var item = f.Item("Crowbar", player, massKg: 5f, material: "Metal");
        Assert.True(f.Hands.Take(player, "", out string message), message);
        f.TickParent();
        float handY = f.World.Get<Transform>(item).Position.Y;
        float floor = f.Geometry.FloorAt(0f, 10f, 100f, GeometryLayers.Ground, ref f.Any, out _);

        Assert.True(f.Hands.Drop(player, "", out message, f.Heard), message);

        var first = f.RunUntilRest(item, out var impacts);
        Assert.NotNull(first);
        float speed = MathF.Sqrt(2f * f.Gravity * MathF.Max(0.05f, handY - floor));
        Assert.Equal(speed, first!.Value.ClosingSpeed, speed * 0.05f);

        // It lies on the floor, its bottom on the floor and its middle half its height above, and no body
        // carries it any more.
        var t = f.World.Get<Transform>(item);
        float rest = f.Geometry.FloorAt(t.Position.X, t.Position.Z, t.Position.Y + 0.5f, GeometryLayers.Ground, ref f.Any, out _);
        float halfHeight = f.World.Get<ColliderComponent>(item).Size.Y * 0.5f;
        Assert.Equal(rest + halfHeight, t.Position.Y, 0.03f);
        Assert.False(BodyWorldSystem.IsMoving(f.World, item));
        Assert.Equal(0, BodyWorldSystem.MovingCount(f.World));
        Assert.NotEmpty(impacts);
    }

    [Fact]
    public void ABounceMakesASecondQuieterImpact()
    {
        using var f = new Fixture(_dir);
        var player = f.Player(new Vector3(0f, f.Floor(0f, 10f), 10f));
        // Glass on concrete: the table's one measured restitution, 0.93, so it really bounces.
        var item = f.Item("Bottle", player, massKg: 0.5f, material: "Glass");
        Assert.True(f.Hands.Take(player, "", out string message), message);
        f.TickParent();
        Assert.True(f.Hands.Drop(player, "", out message, f.Heard), message);

        var first = f.RunUntilRest(item, out var impacts);
        Assert.NotNull(first);
        Assert.True(impacts.Count >= 2, $"{impacts.Count} impact(s) heard");

        float firstKnock = impacts[0].Sounds.First(s => s.Character == SoundCharacter.Knock).LevelDb;
        float secondKnock = impacts[1].Sounds.First(s => s.Character == SoundCharacter.Knock).LevelDb;
        Assert.True(secondKnock < firstKnock, $"the second impact was {secondKnock:F2} dB against the first's {firstKnock:F2} dB");
        Assert.True(impacts[1].Contact.ClosingSpeed < impacts[0].Contact.ClosingSpeed);
    }

    // ── Throwing ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AThrowGoesWhereThePlayerLooksPitchIncluded()
    {
        using var up = new Fixture(_dir);
        var player = up.Player(new Vector3(0f, up.Floor(0f, 10f), 10f));
        var item = up.Item("Torch", player, massKg: 0.4f, material: "Plastic");
        Assert.True(up.Hands.Take(player, "", out string message), message);
        up.TickParent();

        // Looking up is a negative pitch (CreateFromYawPitchRoll takes +Z toward -Y): the item goes up.
        up.Face(player, pitch: -0.6f);
        Assert.True(up.Hands.Throw(player, "", out message, up.Heard), message);
        up.TickBodies();
        Assert.True(up.World.Get<Velocity>(item).Linear.Y > 1f,
                    $"looking up threw it at {up.World.Get<Velocity>(item).Linear:F2} m/s");

        using var down = new Fixture(_dir);
        var player2 = down.Player(new Vector3(0f, down.Floor(0f, 10f), 10f));
        var item2 = down.Item("Torch", player2, massKg: 0.4f, material: "Plastic");
        Assert.True(down.Hands.Take(player2, "", out message), message);
        down.TickParent();
        down.Face(player2, pitch: 0.6f);
        Assert.True(down.Hands.Throw(player2, "", out message, down.Heard), message);
        down.TickBodies();
        Assert.True(down.World.Get<Velocity>(item2).Linear.Y < -1f,
                    $"looking down threw it at {down.World.Get<Velocity>(item2).Linear:F2} m/s");
    }

    [Fact]
    public void AThrowLeavesTheHandAtTheLawsSpeedAndTooHeavySaysSo()
    {
        using var f = new Fixture(_dir);
        var player = f.Player(new Vector3(0f, f.Floor(0f, 10f), 10f));
        var item = f.Item("Crowbar", player, massKg: 5f, material: "Metal");
        Assert.True(f.Hands.Take(player, "", out string message), message);
        f.TickParent();
        f.Face(player, pitch: 0f);
        Assert.True(f.Hands.Throw(player, "", out message, f.Heard), message);
        f.TickBodies();

        float speed = f.World.Get<Velocity>(item).Linear.Length();
        float law = DroppedThings.ThrowSpeed(5f);
        Assert.Equal(11f * MathF.Pow(5f, -0.2f), law, 1e-4f);
        Assert.Equal(law, speed, 0.4f);

        // Too heavy says so, in words, and stays in the hand.
        var other = f.Player(new Vector3(2f, f.Floor(2f, 10f), 10f));
        var anvil = f.Item("Anvil", other, massKg: 40f, material: "Metal");
        Assert.True(f.Hands.Take(other, "", out message), message);
        Assert.False(f.Hands.Throw(other, "", out message, f.Heard));
        Assert.Contains("too heavy to throw", message);
        Assert.True(f.World.Has<HeldComponent>(anvil));
    }

    // ── What the world costs ───────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTickWithMovingItemsIsMeasured()
    {
        using var world = new BodyWorld();
        int floor = world.AddBox(new Vector3(60f, 0.5f, 60f));
        world.Add(floor, BodyMotion.Static, new Vector3(0f, -0.5f, 0f), Quaternion.Identity, "Concrete");
        int shape = world.AddBox(new Vector3(0.15f, 0.15f, 0.15f));

        foreach (int n in new[] { 1, 10, 50 })
        {
            var ids = new List<int>();
            // Falling, not asleep on the floor: every one is still moving for the whole hundred ticks.
            for (int i = 0; i < n; i++)
                ids.Add(world.Add(shape, BodyMotion.Dynamic, new Vector3((i % 10) * 0.5f - 2f, 60f + i / 10, 0.25f),
                                  Quaternion.Identity, "Wood", 2f));
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) world.Tick();
            clock.Stop();
            _out.WriteLine($"{n} moving item(s): {clock.Elapsed.TotalMilliseconds / 100:F3} ms a tick");
            foreach (int id in ids) world.Remove(id);
        }
    }

    // ═══ The rig ═══════════════════════════════════════════════════════════════════════════════

    private sealed record Impact(ContactEvent Contact, List<TransientSound> Sounds);

    private sealed class Fixture : IDisposable
    {
        public const string MapId = "default";
        public readonly MapManager Maps;
        public readonly HandsService Hands;
        public readonly World World;
        public readonly SpatialGrid<Entity> Grid;
        public readonly Dictionary<int, Entity> Lookup;
        public readonly TriangleWorld Geometry;
        public readonly float Gravity;
        public AcceptAll Any = new();
        private int _nextConnection = 1;
        private int _ticks;
        private readonly List<(int Tick, List<TransientSound> Sounds)> _heard = new();
        public readonly List<Impact> Imps = new();

        public Fixture(string dir)
        {
            string mapDir = Path.Combine(dir, "maps");
            Directory.CreateDirectory(mapDir);
            foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "maps"), "*.json"))
                File.Copy(file, Path.Combine(mapDir, Path.GetFileName(file)), overwrite: true);

            var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            Maps = new MapManager(new MapRepository(mapDir), prefabs);
            Maps.Initialize();
            Hands = new HandsService(Maps);
            Assert.True(Maps.TryGetMap(MapId, out World, out _, out Grid, out Lookup));
            Assert.True(Maps.TryGetGeometry(MapId, out var geometry));
            Geometry = geometry.World;
            Gravity = PhysicsConstants.GravityOf(World);
            WaitReady();
        }

        public void Heard(int id, string label, IReadOnlyList<TransientSound> sounds)
            => _heard.Add((_ticks, sounds.ToList()));

        public float Floor(float x, float z)
            => Geometry.FloorAt(x, z, 1000f, GeometryLayers.Ground, ref Any, out _) + 0.01f;

        public UserSession Player(Vector3 at)
        {
            int connection = _nextConnection++;
            var entity = World.Create(
                new PlayerComponent { ConnectionId = connection, Username = "cody" },
                EntityType.Player,
                new Transform { Position = at, Rotation = Quaternion.Identity },
                new Velocity { Linear = Vector3.Zero },
                new MaterialComponent { Material = "Generic" },
                new NameComponent { Name = "cody" },
                new ColliderComponent
                {
                    Shape = ColliderShape.Cylinder,
                    Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight,
                                       PhysicsConstants.PlayerRadius * 2),
                    IsSolid = true,
                });
            Maps.IndexEntity(MapId, entity);
            return new UserSession { ConnectionId = connection, Username = "cody", Entity = entity, CurrentMapId = MapId };
        }

        public void Face(UserSession player, float pitch)
            => World.Get<Transform>(player.Entity).Rotation = Quaternion.CreateFromYawPitchRoll(0f, pitch, 0f);

        public Entity Item(string name, UserSession holder, float massKg, string material)
        {
            var at = World.Get<Transform>(holder.Entity).Position + new Vector3(0.4f, 0f, 0f);
            var e = Maps.SpawnEntity(MapId, w => w.Create(
                new Transform { Position = at, Rotation = Quaternion.Identity },
                new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.2f, 0.1f, 1f), IsSolid = false },
                new MaterialComponent { Material = material, Variant = "0" },
                new IdentityComponent { Name = name },
                new ItemComponent { MassKg = massKg, Hands = 1, WeaponId = "" },
                EntityType.Item));
            Assert.NotEqual(Entity.Null, e);
            return e;
        }

        public void TickParent() => ParentSystem.Update(World, Lookup);

        /// <summary>A body-world tick as the server does it: the stream opens, the bodies step, it closes.</summary>
        public void TickBodies()
        {
            int tick = _ticks;
            var contacts = ContactStreams.Of(World);
            contacts.Begin();
            Maps.TryGetGeometry(MapId, out var geometry);
            BodyWorldSystem.Update(MapId, World, geometry, contacts, Heard);
            contacts.Close();
            _ticks++;
            var thisTick = _heard.Where(h => h.Tick == tick).SelectMany(h => h.Sounds).ToList();
            foreach (var e in contacts.Events)
                if (e.Phase == ContactPhase.Began && BodyWorld.IsContactId(e.Body))
                    Imps.Add(new Impact(e, thisTick));
        }

        /// <summary>Runs the map's body world until the item is out of it; returns its first contact.</summary>
        public ContactEvent? RunUntilRest(Entity item, out List<Impact> impacts)
        {
            for (int i = 0; i < 400; i++)
            {
                TickBodies();
                if (!BodyWorldSystem.IsMoving(World, item)) break;
            }
            impacts = Imps.Where(x => x.Sounds.Count > 0).ToList();
            return Imps.Count > 0 ? Imps[0].Contact : null;
        }

        public void WaitReady()
        {
            for (int i = 0; i < 2000; i++)
            {
                TickBodies();
                if (BodyWorldSystem.IsReady(World) && BodyWorldSystem.MovingCount(World) == 0) return;
                Thread.Sleep(5);
            }
            Assert.Fail("the default map's body world was not ready in ten seconds");
        }

        public void Dispose() => BodyWorldSystem.Forget(World);
    }
}
