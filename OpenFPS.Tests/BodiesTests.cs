using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Rig = OpenFPS.Tests.WeaponsTests.Range;

namespace OpenFPS.Tests;

/// <summary>
/// Somebody killed leaves a body where they fell, an item with the item beacon that can be picked up,
/// carried over the shoulder at a slow walk, and put down; the person comes back a minute later; and
/// bodies do not pile up for ever (Cody, 2026-10-05).
/// </summary>
public class BodiesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-bodies-{Guid.NewGuid():N}");

    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    // ── Where they fell ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AWalkerKilledLeavesABodyItemWhereTheyFell()
    {
        var r = new Rig(_dir);
        var at = r.Feet + new Vector3(3, 0, 10);
        var walker = r.Walker(at);

        r.Kill(walker);

        var body = Assert.Single(r.Bodies());
        var t = r.World.Get<Transform>(body);
        Assert.Equal(at.X, t.Position.X, 3);
        Assert.Equal(at.Z, t.Position.Z, 3);
        Assert.Equal("body of someone", r.World.Get<IdentityComponent>(body).Name);
        var item = r.World.Get<ItemComponent>(body);
        Assert.Equal(Bodies.MassKg, item.MassKg);
        Assert.Equal(2, item.Hands);

        // What every client is told: an item, with the item beacon, called a body. Not a walking person.
        var def = EntityDefinitionFactory.From(r.World, body);
        Assert.Equal(EntityType.Item, def.Type);
        Assert.Equal(Beacons.Item, def.Identity.BeaconCategory);
        Assert.True(def.Identity.Announce);
        Assert.False(r.World.Has<Pedestrian>(body));
        Assert.False(r.World.Has<ColliderComponent>(body));

        // The fall is heard from the body.
        Assert.Contains(r.Events, e => e.Label == "a body falling" && e.SourceEntityId == body.Id);

        // The person is taken off the street next tick; the body stays.
        r.Combat.Update(r.MapId, r.World);
        Assert.False(r.World.IsAlive(walker));
        Assert.True(r.World.IsAlive(body));
    }

    [Theory]
    [InlineData("Pedestrian, Wharf Avenue, west side", "a pedestrian")]
    [InlineData("Pedestrian 14", "a pedestrian")]
    [InlineData("someone walking", "someone")]
    [InlineData("", "someone")]
    public void AWalkerIsNamedByWhatTheyWere(string name, string who)
    {
        var world = World.Create();
        var e = world.Create(new IdentityComponent { Name = name }, new Pedestrian());
        Assert.Equal(who, Bodies.WhoFor(world, e));
        Assert.Equal("body of " + who, Bodies.NameFor(world, e));
        World.Destroy(world);
    }

    [Fact]
    public void APlayerKilledLeavesTheirOwnBodyAndIsNoPlayerBeaconWhileDead()
    {
        var r = new Rig(_dir);
        var at = r.Feet + new Vector3(0, 0, 8);
        r.Place(r.Other, at);

        r.Kill(r.Other.Entity);

        var body = Assert.Single(r.Bodies());
        Assert.Equal("body of other", r.World.Get<IdentityComponent>(body).Name);
        Assert.True(r.World.Get<Corpse>(body).WasPlayer);
        Assert.Equal(at.X, r.World.Get<Transform>(body).Position.X, 3);
        Assert.NotEqual(r.Other.Entity, body);
        // Dead, they are not a player beacon; the definition goes out again to say so.
        Assert.Equal("", EntityDefinitionFactory.From(r.World, r.Other.Entity).Identity.BeaconCategory);
        Assert.Contains(r.Other.Entity.Id, r.Server.PendingDefinitionResends);
        // And a dead player cannot pick anything up, their own body included.
        Assert.False(r.Hands.Take(r.Other, "body", out string refused));
        Assert.Equal("You are dead.", refused);
    }

    // ── Picking it up ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ABodyIsTakenOverTheShoulderInBothHandsAndPutDownAgain()
    {
        var r = new Rig(_dir);
        // A pistol in the shooter's hand, slung on the back first, so there is something to try to draw.
        Assert.True(r.Hands.Give(r.Shooter, "glock_pistol", 1, out _, out _, out _));
        Assert.True(r.Hands.Stow(r.Shooter, "", out _));
        var walker = r.Walker(r.Feet + new Vector3(0, 0, 1.5f));
        r.Kill(walker);
        r.Combat.Update(r.MapId, r.World);
        var body = Assert.Single(r.Bodies());

        // E: the nearest loose thing within reach is the body.
        Assert.True(r.Hands.TakeWithin(r.Shooter, PhysicsConstants.PickUpReach, out string took));
        Assert.StartsWith("You lift the body of someone over your shoulder.", took);
        var hands = r.World.Get<HandsComponent>(r.Shooter.Entity);
        Assert.Equal(body.Id, hands.RightEntityId);
        Assert.Equal(body.Id, hands.LeftEntityId);
        Assert.True(r.World.Has<HeldComponent>(body));
        Assert.Equal("", EntityDefinitionFactory.From(r.World, body).Identity.BeaconCategory);

        // Hands full: no gun comes off the back, and the body does not go onto it.
        Assert.False(r.Hands.Draw(r.Shooter, "glock", out string drew));
        Assert.Contains("hands are full", drew);
        Assert.False(r.Hands.Stow(r.Shooter, "", out string slung));
        Assert.Contains("70 kilograms", slung);

        // The inventory lists it.
        var list = r.Hands.List(r.Shooter);
        int i = Array.IndexOf(list.Ids, body.Id);
        Assert.True(i >= 0);
        Assert.Equal("body of someone", list.Labels[i]);
        Assert.Equal("both hands", list.Places[i]);

        // Put down where you stand: set down, not dropped, and an item beacon again.
        var heard = new List<(int Id, string Label, IReadOnlyList<TransientSound> Sounds)>();
        Assert.True(r.Hands.Drop(r.Shooter, "body", out string put, (id, label, s) => heard.Add((id, label, s))));
        Assert.Equal("You put down the body of someone.", put);
        var down = Assert.Single(heard);
        Assert.Equal(body.Id, down.Id);
        Assert.Equal("a body set down", down.Label);
        Assert.NotEmpty(down.Sounds);
        Assert.False(r.World.Has<HeldComponent>(body));
        Assert.True(Vector3.Distance(r.World.Get<Transform>(body).Position, r.World.Get<Transform>(r.Shooter.Entity).Position) < 1f);
        Assert.Equal(Beacons.Item, EntityDefinitionFactory.From(r.World, body).Identity.BeaconCategory);
    }

    [Fact]
    public void SettingABodyDownIsSofterThanItFalling()
    {
        var r = new Rig(_dir);
        var fell = CombatService.BodyFall(r.World, r.Grid, r.Feet);
        var set = CombatService.BodyFall(r.World, r.Grid, r.Feet, lowered: true);
        Assert.Equal(fell.Count, set.Count);
        Assert.True(set.Max(s => s.LevelDb) < fell.Max(s => s.LevelDb));
    }

    [Fact]
    public void CarryingABodyIsAWalkingPaceAndRunningDoesNotHelp()
    {
        var r = new Rig(_dir);
        var walker = r.Walker(r.Feet + new Vector3(0, 0, 1));
        r.Kill(walker);
        Assert.True(r.Maps.TryGetMap(r.MapId, out _, out _, out _, out var lookup));
        Assert.Equal(0f, HandsService.SpeedLimit(r.World, r.Shooter.Entity, lookup));
        Assert.True(r.Hands.Take(r.Shooter, "body", out _));

        float limit = HandsService.SpeedLimit(r.World, r.Shooter.Entity, lookup);
        Assert.Equal(PhysicsConstants.CarryingSpeed, limit);
        Assert.Equal(PhysicsConstants.CarryingSpeed, PhysicsConstants.FootSpeed(sprint: false, limit));
        Assert.Equal(PhysicsConstants.CarryingSpeed, PhysicsConstants.FootSpeed(sprint: true, limit));
        Assert.Equal(PhysicsConstants.WalkSpeed, PhysicsConstants.FootSpeed(sprint: false, 0f));
        Assert.Equal(PhysicsConstants.SprintSpeed, PhysicsConstants.FootSpeed(sprint: true, 0f));

        // A gun in the hands is no burden.
        Assert.True(r.Hands.Drop(r.Shooter, "body", out _));
        r.Arm(r.Shooter, "akm");
        Assert.Equal(0f, HandsService.SpeedLimit(r.World, r.Shooter.Entity, lookup));
    }

    [Fact]
    public void SomebodyKilledCarryingABodyLetsGoOfIt()
    {
        var r = new Rig(_dir);
        var walker = r.Walker(r.Feet + new Vector3(6, 0, 1));
        r.Kill(walker);
        var carried = Assert.Single(r.Bodies());
        Assert.True(r.Hands.Take(r.Other, "body", out string took), took);

        r.Kill(r.Other.Entity);

        Assert.False(r.World.Has<HeldComponent>(carried));
        Assert.Equal(-1, r.World.Get<HandsComponent>(r.Other.Entity).RightEntityId);
        Assert.Equal(2, r.Bodies().Count);
        Assert.Contains(carried.Id, r.Server.PendingDefinitionResends);
    }

    // ── Coming back ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APlayerComesBackAfterAMinuteAndIsToldTwice()
    {
        var r = new Rig(_dir);
        r.Place(r.Other, r.Feet + new Vector3(0, 0, 8));
        r.Kill(r.Other.Entity);
        Assert.Equal(60, CombatService.PlayerRespawnSeconds);
        Assert.Contains("You are dead. You come back in 60 seconds.", r.SaidTo(r.Other));

        r.Now += 49;
        r.Combat.Update(r.MapId, r.World);
        Assert.DoesNotContain("You come back in 10 seconds.", r.SaidTo(r.Other));

        r.Now += 1.5;
        r.Combat.Update(r.MapId, r.World);
        r.Combat.Update(r.MapId, r.World);
        Assert.Single(r.SaidTo(r.Other), t => t == "You come back in 10 seconds.");

        r.Now += 9;                                     // 59.5 s: still dead
        r.Combat.Update(r.MapId, r.World);
        Assert.True(r.World.Has<DeadComponent>(r.Other.Entity));

        r.Now += 0.6;
        r.Combat.Update(r.MapId, r.World);
        Assert.False(r.World.Has<DeadComponent>(r.Other.Entity));
        Assert.Equal(r.Maps.GetSpawnPoint(r.MapId).Position, r.World.Get<Transform>(r.Other.Entity).Position);
        Assert.Equal(Beacons.Player, EntityDefinitionFactory.From(r.World, r.Other.Entity).Identity.BeaconCategory);
        // The body stays where they fell.
        Assert.Single(r.Bodies());
    }

    [Fact]
    public void SomebodyNewWalksTheWayAMinuteAfterAWalkerIsKilled()
    {
        var r = new Rig(_dir);
        var retired = new List<int>();
        var replaced = new List<int>();
        r.Combat.RetireWalker = (map, world, dead) => { retired.Add(dead.Id); return true; };
        r.Combat.ReplaceWalker = (map, world, id) => { replaced.Add(id); return Entity.Null; };
        var walker = r.Walker(r.Feet + new Vector3(0, 0, 10));
        int id = walker.Id;

        r.Kill(walker);
        r.Combat.Update(r.MapId, r.World);
        Assert.Equal(new[] { id }, retired);
        Assert.False(r.World.IsAlive(walker));        // taken off even when the retire left them
        Assert.Single(r.Bodies());

        r.Now += CombatService.WalkerRespawnSeconds - 1;
        r.Combat.Update(r.MapId, r.World);
        Assert.Empty(replaced);

        r.Now += 1.1;
        r.Combat.Update(r.MapId, r.World);
        r.Combat.Update(r.MapId, r.World);
        Assert.Equal(new[] { id }, replaced);
        Assert.Single(r.Bodies());                    // and the body is still there
    }

    // ── What they carried ───────────────────────────────────────────────────────────────────────

    private static List<Entity> Bags(Rig r)
    {
        var found = new List<Entity>();
        r.World.Query(new QueryDescription().WithAll<BelongingsBag>(), (Entity e) => found.Add(e));
        return found;
    }

    /// <summary>Cody (2026-10-05): "When a player dies, their inventory should drop alongside their body,
    /// so 2 items are together".</summary>
    [Fact]
    public void APlayerKilledLeavesTheirThingsInABagBesideTheBodyAndGetsUpWithNothing()
    {
        var r = new Rig(_dir);
        var at = r.Feet + new Vector3(0, 0, 8);
        r.Place(r.Other, at);
        Assert.True(r.Hands.Give(r.Other, "akm_rifle", 1, out _, out _, out string m), m);
        Assert.True(r.Hands.Give(r.Other, "glock_pistol", 1, out _, out string placed, out m), m);
        Assert.Contains("back", placed);
        Assert.True(r.Maps.TryGetMap(r.MapId, out _, out _, out _, out var lookup));
        var rifle = HandsService.Holding(r.World, r.Other.Entity, lookup).Right!.Value;
        int reserve = Arms.Reserve(r.World, r.Other.Entity, "7.62x39");
        Assert.True(reserve > 0);

        r.Kill(r.Other.Entity);

        var body = Assert.Single(r.Bodies());
        var bag = Assert.Single(Bags(r));
        Assert.Equal("other's belongings", r.World.Get<IdentityComponent>(bag).Name);
        var def = EntityDefinitionFactory.From(r.World, bag);
        Assert.Equal(EntityType.Item, def.Type);
        Assert.Equal(Beacons.Item, def.Identity.BeaconCategory);
        Assert.True(Vector3.Distance(r.World.Get<Transform>(bag).Position, r.World.Get<Transform>(body).Position) <= Bodies.BagBesideMetres + 0.01f);
        var contents = r.World.Get<BelongingsBag>(bag).Contents;
        Assert.Equal(new[] { "akm_rifle", "glock_pistol" }, contents.Items.Select(i => i.Prefab).OrderBy(x => x));
        Assert.Equal(reserve, contents.Spares["7.62x39"]);
        Assert.True(r.World.Get<ItemComponent>(bag).MassKg > 4f);

        // Nothing left on the dead player, and the things themselves are out of the world, in the bag.
        var (right, left) = HandsService.Holding(r.World, r.Other.Entity, lookup);
        Assert.Null(right);
        Assert.Null(left);
        Assert.Empty(HandsService.Stowed(r.World, r.Other.Entity, lookup));
        Assert.Equal(0, Arms.Reserve(r.World, r.Other.Entity, "7.62x39"));
        Assert.False(r.World.IsAlive(rifle));

        r.Now += CombatService.PlayerRespawnSeconds + 0.1;
        r.Combat.Update(r.MapId, r.World);
        Assert.False(r.World.Has<DeadComponent>(r.Other.Entity));
        Assert.Equal("Your hands are empty. You have nothing on your back.", r.Hands.Readout(r.Other));
        Assert.Single(Bags(r));
    }

    [Fact]
    public void TakingTheBagEmptiesItIntoYourHandsYourBackAndYourPockets()
    {
        var r = new Rig(_dir);
        r.Place(r.Other, r.Feet + new Vector3(0, 0, 1));
        Assert.True(r.Hands.Give(r.Other, "akm_rifle", 1, out _, out _, out string m), m);
        Assert.True(r.Hands.Give(r.Other, "glock_pistol", 1, out _, out _, out m), m);
        int rifleRounds = Arms.Reserve(r.World, r.Other.Entity, "7.62x39");
        r.Kill(r.Other.Entity);
        var bag = Assert.Single(Bags(r));

        Assert.True(r.Hands.Take(r.Shooter, "belongings", out string took), took);
        Assert.StartsWith("You go through other's belongings and take the AKM in both hands, the Glock 17 on your back", took);
        Assert.Contains(" of 7.62x39", took);
        Assert.True(r.Maps.TryGetMap(r.MapId, out _, out _, out _, out var lookup));
        Assert.True(HandsService.TryGetHeldWeapon(r.World, r.Shooter.Entity, lookup, out var weapon, out _));
        Assert.Equal("akm", weapon.Id);
        Assert.Single(HandsService.Stowed(r.World, r.Shooter.Entity, lookup));
        Assert.Equal(rifleRounds, Arms.Reserve(r.World, r.Shooter.Entity, "7.62x39"));
        // Emptied, the bag is gone, and the body is still there.
        Assert.False(r.World.IsAlive(bag));
        Assert.Single(r.Bodies());
    }

    [Fact]
    public void WhatWillNotGoStaysInTheBag()
    {
        var r = new Rig(_dir);
        r.Place(r.Other, r.Feet + new Vector3(0, 0, 1));
        Assert.True(r.Hands.Give(r.Other, "akm_rifle", 1, out _, out _, out string m), m);
        r.Kill(r.Other.Entity);
        var bag = Assert.Single(Bags(r));
        // The shooter's hands are full and their back is loaded to within a kilogram of what it takes.
        Assert.True(r.Hands.Give(r.Shooter, "akm_rifle", 8, out _, out _, out m), m);
        Assert.True(r.Maps.TryGetMap(r.MapId, out _, out _, out _, out var lookup));
        Assert.True(HandsService.CarriedMassKg(r.World, r.Shooter.Entity, lookup) + 3.3f > HandsService.CarryCapacityKg);

        Assert.True(r.Hands.Take(r.Shooter, "belongings", out string took), took);
        Assert.Contains("The AKM stays in it", took);
        Assert.True(r.World.IsAlive(bag));
        Assert.Equal("akm_rifle", Assert.Single(r.World.Get<BelongingsBag>(bag).Contents.Items).Prefab);
        Assert.Empty(r.World.Get<BelongingsBag>(bag).Contents.Spares);   // the rounds went into pockets
        Assert.Equal(Beacons.Item, EntityDefinitionFactory.From(r.World, bag).Identity.BeaconCategory);
    }

    [Fact]
    public void SomebodyWhoCarriedNothingLeavesNoBag()
    {
        var r = new Rig(_dir);
        r.Kill(r.Walker(r.Feet + new Vector3(0, 0, 5)));
        r.Place(r.Other, r.Feet + new Vector3(0, 0, 9));
        r.Kill(r.Other.Entity);
        Assert.Equal(2, r.Bodies().Count);
        Assert.Empty(Bags(r));
    }

    [Fact]
    public void ABagIsTakenAwayAfterHalfAnHourLikeABody()
    {
        var r = new Rig(_dir);
        r.Place(r.Other, r.Feet + new Vector3(0, 0, 8));
        Assert.True(r.Hands.Give(r.Other, "glock_pistol", 1, out _, out _, out string m), m);
        r.Kill(r.Other.Entity);
        Assert.Single(Bags(r));
        r.Now += Bodies.UncarriedSeconds + 1;
        r.Combat.Update(r.MapId, r.World);
        Assert.Empty(Bags(r));
        Assert.Empty(r.Bodies());
    }

    // ── Not for ever ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ABodyNobodyCarriesIsTakenAwayAfterHalfAnHour()
    {
        var r = new Rig(_dir);
        var a = r.Walker(r.Feet + new Vector3(0, 0, 1));
        var b = r.Walker(r.Feet + new Vector3(0, 0, 20));
        r.Kill(a);
        r.Kill(b);
        r.Combat.Update(r.MapId, r.World);
        Assert.Equal(2, r.Bodies().Count);
        Assert.True(r.Hands.Take(r.Shooter, "body", out _));          // the near one

        r.Now += Bodies.UncarriedSeconds - 1;
        r.Combat.Update(r.MapId, r.World);
        Assert.Equal(2, r.Bodies().Count);

        r.Now += 2;
        r.Combat.Update(r.MapId, r.World);
        var left = Assert.Single(r.Bodies());
        Assert.True(r.World.Has<HeldComponent>(left));                // the carried one is never taken
    }

    [Fact]
    public void AMapHoldsThirtyBodiesAndTheOldestUncarriedGoesFirst()
    {
        var r = new Rig(_dir);
        Assert.True(r.Maps.TryGetMap(r.MapId, out var world, out _, out var grid, out _));
        var laid = new List<Entity>();
        for (int i = 0; i < Bodies.MaxPerMap + 2; i++)
        {
            var w = r.Walker(r.Feet + new Vector3(i * 0.5f, 0, 30));
            laid.Add(Bodies.Lay(r.Maps, r.MapId, world, grid, w, r.Now + i));
            r.Maps.DestroyEntity(r.MapId, w);
        }
        // The very oldest is being carried.
        r.Place(r.Shooter, r.World.Get<Transform>(laid[0]).Position);
        Assert.True(r.Hands.Take(r.Shooter, "#" + laid[0].Id, out string took), took);

        r.Now += 100;
        r.Combat.Update(r.MapId, r.World);

        var bodies = r.Bodies();
        Assert.Equal(Bodies.MaxPerMap, bodies.Count);
        Assert.Contains(laid[0], bodies);                 // carried: kept
        Assert.DoesNotContain(laid[1], bodies);           // the two oldest lying ones went
        Assert.DoesNotContain(laid[2], bodies);
        Assert.Contains(laid[3], bodies);
        Assert.Contains(laid[^1], bodies);
    }
}

/// <summary>
/// The street's side of it, on the city: a walker taken off and somebody new on the same walk, and a
/// parked car's driver who is shot on the pavement.
/// </summary>
public class StreetBodiesTests
{
    private static (MapManager Maps, World World, VehicleSystem Vehicles) LoadCity(StreetLifeData? life = null)
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        if (life != null)
        {
            Assert.True(maps.TryGetMapData("city", out var data));
            data.StreetLife = life;
        }
        var vehicles = new VehicleSystem();
        vehicles.Spawn(maps);
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        return (maps, world, vehicles);
    }

    [Fact]
    public void AWalkerIsTakenOffAndSomebodyNewWalksTheSameWay()
    {
        var (maps, world, vehicles) = LoadCity();
        var removed = new List<int>();
        vehicles.Removed = (_, id) => removed.Add(id);
        Entity walker = Entity.Null;
        world.Query(new QueryDescription().WithAll<Pedestrian, Transform>(), (Entity e) => { if (walker == Entity.Null) walker = e; });
        Assert.NotEqual(Entity.Null, walker);
        string name = world.Get<NameComponent>(walker).Name;
        string voice = world.Get<Pedestrian>(walker).Voice;
        // A person is a body to whatever meets them, not "Generic" (2026-10-05).
        Assert.Equal(PhysicsConstants.PersonMaterial, world.Get<MaterialComponent>(walker).Material);
        world.Add(walker, new DeadComponent());

        Assert.True(vehicles.RetireWalker("city", world, walker));
        Assert.False(world.IsAlive(walker));
        Assert.Contains(walker.Id, removed);
        Assert.True(vehicles.IsRetiredForTest(walker.Id));
        for (int i = 0; i < 30; i++) vehicles.Update("city", world, 1f / 30f);   // nobody walking it meanwhile

        var fresh = vehicles.ReplaceWalker("city", world, walker.Id);
        Assert.NotEqual(Entity.Null, fresh);
        Assert.True(world.IsAlive(fresh));
        Assert.Equal(name, world.Get<NameComponent>(fresh).Name);
        Assert.Equal(voice, world.Get<Pedestrian>(fresh).Voice);
        Assert.Equal(PhysicsConstants.PersonMaterial, world.Get<MaterialComponent>(fresh).Material);
        Assert.False(world.Has<DeadComponent>(fresh));
        Assert.False(vehicles.IsRetiredForTest(walker.Id));
        Assert.Equal(Entity.Null, vehicles.ReplaceWalker("city", world, walker.Id));   // once only
    }

    /// <summary>
    /// The driver out of a parked car was a Pedestrian the walkers' table did not know: taking them away
    /// destroyed them and left the car still holding the dead id, so it walked a stale handle, destroyed
    /// it again (taking a recycled id out of the lookup) and told every client to remove it. Now the car
    /// lets go of them and waits, and somebody comes back out of the door for it a minute on.
    /// </summary>
    [Fact]
    public void AParkedCarsDriverShotOnThePavementIsLetGoAndTheCarIsFetchedLater()
    {
        var (maps, world, vehicles) = LoadCity(new StreetLifeData { ParkEverySeconds = 5 });
        var removed = new List<int>();
        vehicles.Removed = (_, id) => removed.Add(id);
        const float dt = 1f / 30f;

        // default(Entity) is id 0, a real entity (the ground), not Entity.Null: found says whether one was.
        (Entity Car, string Phase, int Step, Entity Driver, float Away) parked = default;
        bool found = false;
        for (int tick = 0; tick < 30 * 600 && !found; tick++)
        {
            vehicles.Update("city", world, dt);
            foreach (var p in vehicles.ParkedForTest("city"))
                if (p.Driver != Entity.Null && world.IsAlive(p.Driver) && p.Step is 3) { parked = p; found = true; break; }
        }
        Assert.True(found, "nobody parked and got out in ten minutes");
        var driver = parked.Driver;
        Assert.True(world.Has<Pedestrian>(driver));

        world.Add(driver, new DeadComponent());
        var where = world.Get<Transform>(driver).Position;
        vehicles.Update("city", world, dt);
        Assert.Equal(where, world.Get<Transform>(driver).Position);      // a dead driver is not walked on

        Assert.False(vehicles.RetireWalker("city", world, driver));     // nobody new on a walk...
        Assert.False(world.IsAlive(driver));                            // ...but they are taken off
        Assert.Single(removed, id => id == driver.Id);
        var now = vehicles.ParkedForTest("city").Single(p => p.Car == parked.Car);
        Assert.Equal(Entity.Null, now.Driver);
        Assert.Equal(7, now.Step);
        Assert.True(now.Away >= CombatService.WalkerRespawnSeconds);

        // Somebody comes back for it after a minute or more, and it drives away.
        bool gone = false;
        for (int tick = 0; tick < 30 * (now.Away + 60) && !gone; tick++)
        {
            vehicles.Update("city", world, dt);
            var p = vehicles.ParkedForTest("city").Where(x => x.Car == parked.Car).ToList();
            gone = p.Count == 0 || p[0].Phase != "Parked";
        }
        Assert.True(gone, "nobody came back for the car");
    }
}
