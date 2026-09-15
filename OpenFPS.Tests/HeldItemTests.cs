using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Picking things up, carrying them, and putting them down.
///
/// The claim these hold is that an item you are carrying is the SAME ENTITY as one on the ground —
/// not a row in a table, not a name in a list. Everything worth having follows from that and from
/// nothing else: a carried thing has a position, so it comes with you and other people can hear it
/// go past; it has a mass and a material, so putting it down makes the noise those two make meeting
/// that floor from the height it actually fell, out of a calculation that already existed and has
/// never heard of a sword; and nobody can lift it out of your hands because somebody already has it.
///
/// The other claim is that TWO HANDS is a real constraint rather than a slot count. A rifle spends
/// both, so a rifle and a torch is a decision, and a decision made out loud in the moment is worth
/// more to a player who cannot see their own hands than a list they can scroll. The back is what
/// makes that liveable, and the back is limited by weight, because weight is what a back is limited
/// by.
/// </summary>
public class HeldItemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-hands-{Guid.NewGuid():N}");

    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    // ── Picking things up ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void WhatYouPickUpComesWithYou()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        var sword = f.Item("Iron Sword", new Vector3(20.5f, 0, 20), massKg: 2f);

        Assert.True(f.Hands.Take(player, "", out string message), message);
        Assert.Contains("Iron Sword", message);

        // It is held, it is parented, and it is the same entity it was lying on the floor.
        Assert.True(f.World.Has<HeldComponent>(sword));
        Assert.Equal(player.Entity.Id, f.World.Get<HeldComponent>(sword).HolderEntityId);
        Assert.Equal(sword.Id, f.World.Get<HandsComponent>(player.Entity).RightEntityId);

        // And it moves because the holder does — through ParentSystem, which has carried children
        // with their parent since long before anything could be picked up.
        f.Move(player, new Vector3(40, 0, 20));
        f.Tick(1);
        Assert.True(Vector3.Distance(f.World.Get<Transform>(sword).Position, new Vector3(40, 0, 20)) < 2f);
    }

    [Fact]
    public void YouCannotReachSomethingAcrossTheRoom()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        f.Item("Iron Sword", new Vector3(28, 0, 20));

        Assert.False(f.Hands.Take(player, "", out string message));
        // Refusing says WHERE it is, because a player who cannot see it has no other way to find out
        // whether "no" meant "not here" or "not yet".
        Assert.Contains("metres away", message);
        Assert.Contains("Get closer", message);
    }

    [Fact]
    public void YouCanSayWhichOne()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        f.Item("Iron Sword", new Vector3(20.4f, 0, 20));
        var torch = f.Item("Torch", new Vector3(20.6f, 0, 20), massKg: 0.4f);

        // The sword is nearer, so bare /take would have taken it. Naming beats proximity.
        Assert.True(f.Hands.Take(player, "torch", out string message), message);
        Assert.Contains("Torch", message);
        Assert.Equal(torch.Id, f.World.Get<HandsComponent>(player.Entity).RightEntityId);
    }

    [Fact]
    public void NobodyCanLiftWhatSomebodyElseHasHoldOf()
    {
        var f = new Fixture(_dir);
        var first = f.Player("first", new Vector3(20, 0, 20));
        var second = f.Player("second", new Vector3(20.8f, 0, 20));
        var sword = f.Item("Iron Sword", new Vector3(20.4f, 0, 20));

        Assert.True(f.Hands.Take(first, "", out _));
        // Within arm's length of it, and it will not come: somebody already has hold of it.
        Assert.False(f.Hands.Take(second, "sword", out string message));
        Assert.DoesNotContain("You take", message);
        Assert.Equal(first.Entity.Id, f.World.Get<HeldComponent>(sword).HolderEntityId);
        Assert.False(f.World.Has<HandsComponent>(second.Entity)
                     && f.World.Get<HandsComponent>(second.Entity).RightEntityId == sword.Id);
    }

    // ── Two hands ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARifleSpendsBothHands()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        var rifle = f.Item("AKM", new Vector3(20.4f, 0, 20), massKg: 3.3f, hands: 2, weaponId: "akm");
        f.Item("Torch", new Vector3(20.5f, 0, 20), massKg: 0.4f);

        Assert.True(f.Hands.Take(player, "akm", out string message), message);
        Assert.Contains("both hands", message);

        // The same id in both slots, so "have I a hand free" is one question with one answer.
        var hands = f.World.Get<HandsComponent>(player.Entity);
        Assert.Equal(rifle.Id, hands.RightEntityId);
        Assert.Equal(rifle.Id, hands.LeftEntityId);

        Assert.False(f.Hands.Take(player, "torch", out string refusal));
        Assert.Contains("Your hands are full", refusal);
        // And it says what is in them, because "full" without "of what" is a dead end.
        Assert.Contains("AKM", refusal);
    }

    [Fact]
    public void ATwoHandedThingWillNotGoIntoOneFreeHand()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        f.Item("Torch", new Vector3(20.4f, 0, 20), massKg: 0.4f);
        f.Item("AKM", new Vector3(20.5f, 0, 20), massKg: 3.3f, hands: 2, weaponId: "akm");

        Assert.True(f.Hands.Take(player, "torch", out _));
        Assert.False(f.Hands.Take(player, "akm", out string refusal));
        Assert.Contains("takes both hands", refusal);
        Assert.Contains("Torch", refusal);
    }

    [Fact]
    public void TwoOneHandedThingsFitAndTheThirdDoesNot()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        f.Item("Torch", new Vector3(20.3f, 0, 20), massKg: 0.4f);
        f.Item("Iron Sword", new Vector3(20.4f, 0, 20));
        f.Item("Crowbar", new Vector3(20.5f, 0, 20), massKg: 4f);

        Assert.True(f.Hands.Take(player, "torch", out string a), a);
        Assert.Contains("right hand", a);
        Assert.True(f.Hands.Take(player, "sword", out string b), b);
        Assert.Contains("left hand", b);
        Assert.False(f.Hands.Take(player, "crowbar", out _));
    }

    // ── Putting things down ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void WhatYouPutDownLandsAndIsHeard()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        var sword = f.Item("Iron Sword", new Vector3(20.4f, 0, 20), massKg: 2f, material: "Metal");
        Assert.True(f.Hands.Take(player, "", out _));

        var heard = new List<TransientSound>();
        Assert.True(f.Hands.Drop(player, "", out string message, (_, _, sounds) => heard.AddRange(sounds)), message);
        Assert.Contains("Iron Sword", message);

        // Free of the hand, free of the holder, and on the floor in front of where you stand.
        Assert.False(f.World.Has<HeldComponent>(sword));
        Assert.False(f.World.Has<ParentComponent>(sword));
        Assert.Equal(-1, f.World.Get<HandsComponent>(player.Entity).RightEntityId);

        var landed = f.World.Get<Transform>(sword).Position;
        Assert.True(Vector3.Distance(new Vector3(landed.X, 0, landed.Z), new Vector3(20, 0, 20)) < 1.5f);

        // And it made a noise nobody recorded.
        Assert.NotEmpty(heard);
        Assert.All(heard, s => Assert.True(s.LevelDb > 0f, "a landing with no level is a landing nobody hears"));
    }

    [Fact]
    public void WhatItWeighsIsWhatItSoundsLike()
    {
        var f = new Fixture(_dir);
        var light = f.Player("light", new Vector3(20, 0, 20));
        var heavy = f.Player("heavy", new Vector3(30, 0, 30));
        f.Item("Torch", new Vector3(20.3f, 0, 20), massKg: 0.3f, material: "Metal");
        f.Item("Anvil", new Vector3(30.3f, 0, 30), massKg: 20f, material: "Metal");

        Assert.True(f.Hands.Take(light, "torch", out _));
        Assert.True(f.Hands.Take(heavy, "anvil", out _));

        var quiet = new List<TransientSound>();
        var loud = new List<TransientSound>();
        Assert.True(f.Hands.Drop(light, "", out _, (_, _, s) => quiet.AddRange(s)));
        Assert.True(f.Hands.Drop(heavy, "", out _, (_, _, s) => loud.AddRange(s)));

        Assert.NotEmpty(quiet);
        Assert.NotEmpty(loud);
        // Same material, same floor, same fall. The only difference is the mass, and it is audible —
        // which is the whole reason items carry one.
        Assert.True(loud.Max(s => s.LevelDb) > quiet.Max(s => s.LevelDb) + 3f,
                    $"a 20 kg anvil should land louder than a 300 g torch: {loud.Max(s => s.LevelDb):F1} vs {quiet.Max(s => s.LevelDb):F1} dB");
    }

    [Fact]
    public void DroppingWithAnEmptyRightHandStillDropsWhatYouHold()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        f.Item("Torch", new Vector3(20.3f, 0, 20), massKg: 0.4f);
        var sword = f.Item("Iron Sword", new Vector3(20.4f, 0, 20));

        Assert.True(f.Hands.Take(player, "torch", out _));      // right
        Assert.True(f.Hands.Take(player, "sword", out _));      // left
        Assert.True(f.Hands.Drop(player, "right", out _));      // right is empty again

        // Bare /drop used to look only at the right hand and report holding nothing, while a sword
        // sat in the left.
        Assert.True(f.Hands.Drop(player, "", out string message), message);
        Assert.Contains("Iron Sword", message);
        Assert.False(f.World.Has<HeldComponent>(sword));
    }

    [Fact]
    public void PuttingItDownPutsItBackWhereAnybodyCanTakeIt()
    {
        var f = new Fixture(_dir);
        var first = f.Player("first", new Vector3(20, 0, 20));
        var second = f.Player("second", new Vector3(20.8f, 0, 20));
        f.Item("Iron Sword", new Vector3(20.4f, 0, 20));

        Assert.True(f.Hands.Take(first, "", out _));
        Assert.True(f.Hands.Drop(first, "", out _));
        Assert.True(f.Hands.Take(second, "sword", out string message), message);
    }

    // ── The back ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SlingingItFreesYourHandsWithoutPuttingItDown()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        var rifle = f.Item("AKM", new Vector3(20.4f, 0, 20), massKg: 3.3f, hands: 2, weaponId: "akm");

        Assert.True(f.Hands.Take(player, "akm", out _));
        Assert.True(f.Hands.Stow(player, "", out string message), message);
        Assert.Contains("onto your back", message);

        // Hands empty, but you still HAVE it: it is on you, at a position, parented to you.
        var hands = f.World.Get<HandsComponent>(player.Entity);
        Assert.Equal(-1, hands.RightEntityId);
        Assert.Equal(-1, hands.LeftEntityId);
        Assert.True(f.World.Has<HeldComponent>(rifle));
        Assert.True(f.World.Has<ParentComponent>(rifle));
        Assert.Contains(rifle.Id, f.World.Get<InventoryComponent>(player.Entity).ItemEntityIds);

        f.Move(player, new Vector3(45, 0, 20));
        f.Tick(1);
        Assert.True(Vector3.Distance(f.World.Get<Transform>(rifle).Position, new Vector3(45, 0, 20)) < 2f);
    }

    [Fact]
    public void NobodyCanLiftARifleOffYourBack()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        var thief = f.Player("thief", new Vector3(20.8f, 0, 20));
        var rifle = f.Item("AKM", new Vector3(20.4f, 0, 20), massKg: 3.3f, hands: 2, weaponId: "akm");

        Assert.True(f.Hands.Take(player, "akm", out _));
        Assert.True(f.Hands.Stow(player, "", out _));

        // Standing right next to it. Slung is still had, so it does not come off your back.
        Assert.False(f.Hands.Take(thief, "akm", out string message));
        Assert.DoesNotContain("You take", message);
        Assert.Equal(player.Entity.Id, f.World.Get<HeldComponent>(rifle).HolderEntityId);
        Assert.Contains(rifle.Id, f.World.Get<InventoryComponent>(player.Entity).ItemEntityIds);
    }

    [Fact]
    public void YouTakeItBackOffYourBackByName()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        var rifle = f.Item("AKM", new Vector3(20.3f, 0, 20), massKg: 3.3f, hands: 2, weaponId: "akm");
        f.Item("Torch", new Vector3(20.4f, 0, 20), massKg: 0.4f);

        Assert.True(f.Hands.Take(player, "akm", out _));
        Assert.True(f.Hands.Stow(player, "", out _));
        Assert.True(f.Hands.Take(player, "torch", out _));
        Assert.True(f.Hands.Stow(player, "", out _));

        Assert.True(f.Hands.Draw(player, "akm", out string message), message);
        Assert.Contains("AKM", message);
        Assert.Equal(rifle.Id, f.World.Get<HandsComponent>(player.Entity).RightEntityId);
        Assert.DoesNotContain(rifle.Id, f.World.Get<InventoryComponent>(player.Entity).ItemEntityIds);
    }

    [Fact]
    public void YouCannotDrawWhatYouHaveNoHandsFor()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        f.Item("AKM", new Vector3(20.3f, 0, 20), massKg: 3.3f, hands: 2, weaponId: "akm");
        f.Item("Torch", new Vector3(20.4f, 0, 20), massKg: 0.4f);

        Assert.True(f.Hands.Take(player, "akm", out _));
        Assert.True(f.Hands.Stow(player, "", out _));
        Assert.True(f.Hands.Take(player, "torch", out _));

        Assert.False(f.Hands.Draw(player, "akm", out string refusal));
        Assert.Contains("takes both hands", refusal);
        Assert.Contains("Torch", refusal);
    }

    [Fact]
    public void ABackIsLimitedByWeightAndNotByPockets()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        for (int i = 0; i < 4; i++) f.Item($"Anvil {i}", new Vector3(20.2f + i * 0.05f, 0, 20), massKg: 9f, material: "Metal");

        // Two nine-kilo anvils go on. The third is eighteen plus nine against a limit of twenty-five,
        // and no number of pockets changes that.
        for (int i = 0; i < 2; i++)
        {
            Assert.True(f.Hands.Take(player, $"Anvil {i}", out string t), t);
            Assert.True(f.Hands.Stow(player, "", out string s), s);
        }

        Assert.True(f.Hands.Take(player, "Anvil 2", out _));
        Assert.False(f.Hands.Stow(player, "", out string refusal));
        // The refusal is the arithmetic, so a player can work out what to put down.
        Assert.Contains("18.0", refusal);
        Assert.Contains("25", refusal);

        // And a fistful of light things is not a load, however many of them there are.
        Assert.True(f.Hands.Drop(player, "", out _));
        for (int i = 0; i < 6; i++)
        {
            var torch = f.Item($"Torch {i}", new Vector3(20.2f, 0, 20), massKg: 0.3f);
            Assert.True(f.Hands.Take(player, $"Torch {i}", out _));
            Assert.True(f.Hands.Stow(player, "", out string s), s);
        }
        Assert.Equal(8, f.World.Get<InventoryComponent>(player.Entity).ItemEntityIds.Count);
    }

    [Fact]
    public void YouCanPutDownWhatIsOnYourBackWithoutTakingItOffFirst()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        var rifle = f.Item("AKM", new Vector3(20.3f, 0, 20), massKg: 3.3f, hands: 2, weaponId: "akm");

        Assert.True(f.Hands.Take(player, "akm", out _));
        Assert.True(f.Hands.Stow(player, "", out _));

        var heard = new List<TransientSound>();
        Assert.True(f.Hands.Drop(player, "akm", out string message, (_, _, s) => heard.AddRange(s)), message);
        Assert.False(f.World.Has<HeldComponent>(rifle));
        Assert.Empty(f.World.Get<InventoryComponent>(player.Entity).ItemEntityIds);
        Assert.NotEmpty(heard);
    }

    // ── The readout ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheReadoutIsASentenceAndSaysBothPlaces()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        f.Item("Iron Sword", new Vector3(20.3f, 0, 20), massKg: 2f);
        f.Item("Torch", new Vector3(20.4f, 0, 20), massKg: 0.4f);

        Assert.Equal("Your hands are empty. You have nothing on your back.", f.Hands.Readout(player));

        Assert.True(f.Hands.Take(player, "sword", out _));
        Assert.True(f.Hands.Stow(player, "", out _));
        Assert.True(f.Hands.Take(player, "torch", out _));

        string readout = f.Hands.Readout(player);
        Assert.Contains("Torch in your right hand", readout);
        Assert.Contains("On your back", readout);
        Assert.Contains("an Iron Sword", readout);   // spoken aloud, so "a Iron Sword" is a stumble
        // What it weighs and what is left, because the weight is the other limit.
        Assert.Contains("2.0 of 25 kilograms", readout);
    }

    // ── Things the map itself put there ─────────────────────────────────────────────────────────

    [Fact]
    public void SomethingTheMapPutThereCanBePickedUpAndReadBack()
    {
        // The demo map leaves a crowbar on the concrete a few steps from the spawn point. Taking a
        // thing the MAP spawned is not the same code path as taking one a command spawned, and the
        // difference used to be fatal in a way nothing noticed: a map entity was registered in the
        // map's lookup under the id its AUTHOR wrote in the JSON and not under its runtime id, so
        // the moment anything held a runtime id — which is what every component carries — it
        // resolved to nothing. /take said "You take the Crowbar"; /inv said your hands were empty;
        // and the crowbar was gone from the floor, because it really had been picked up.
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(1.2f, 0, 2.5f));

        Assert.True(f.Hands.Take(player, "crowbar", out string message), message);
        Assert.Contains("Crowbar", message);

        // The readout resolves the id it stored, which is the half that was broken.
        Assert.Contains("Crowbar in your right hand", f.Hands.Readout(player));
        Assert.True(f.Hands.Stow(player, "", out _));
        Assert.Contains("Crowbar", f.Hands.Readout(player));
        Assert.True(f.Hands.Draw(player, "crowbar", out string drawn), drawn);
        Assert.Contains("Crowbar", drawn);
    }

    // ── The join to the gun ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheWeaponYouAreHoldingIsTheWeaponYouFire()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        f.Item("AKM", new Vector3(20.3f, 0, 20), massKg: 3.3f, hands: 2, weaponId: "akm");

        // Holding nothing is being unarmed. There is no EquippedWeaponComponent to disagree with.
        Assert.False(HandsService.TryGetHeldWeapon(f.World, player.Entity, f.Lookup, out _, out _));

        Assert.True(f.Hands.Take(player, "akm", out _));
        Assert.True(HandsService.TryGetHeldWeapon(f.World, player.Entity, f.Lookup, out var weapon, out _));
        Assert.Equal("akm", weapon.Id);

        // On your back is not in your hands: slung, you are not armed.
        Assert.True(f.Hands.Stow(player, "", out _));
        Assert.False(HandsService.TryGetHeldWeapon(f.World, player.Entity, f.Lookup, out _, out _));

        // And putting it down disarms you with no bookkeeping anywhere.
        Assert.True(f.Hands.Draw(player, "akm", out _));
        Assert.True(HandsService.TryGetHeldWeapon(f.World, player.Entity, f.Lookup, out _, out _));
        Assert.True(f.Hands.Drop(player, "", out _));
        Assert.False(HandsService.TryGetHeldWeapon(f.World, player.Entity, f.Lookup, out _, out _));
    }

    [Fact]
    public void AThingThatIsNotAGunIsNotAGun()
    {
        var f = new Fixture(_dir);
        var player = f.Player("cody", new Vector3(20, 0, 20));
        f.Item("Iron Sword", new Vector3(20.3f, 0, 20), massKg: 2f);

        Assert.True(f.Hands.Take(player, "", out _));
        Assert.False(HandsService.TryGetHeldWeapon(f.World, player.Entity, f.Lookup, out _, out _));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private sealed class Fixture
    {
        public readonly MapManager Maps;
        public readonly HandsService Hands;
        public readonly SessionManager Sessions = new();
        public readonly string MapId = "default";

        public World World = null!;
        public Dictionary<int, Entity> Lookup = null!;
        public SpatialGrid<Entity> Grid = null!;

        private int _nextConnection = 1;

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
        }

        /// <summary>Something on the floor: a name, a mass, a material and a position. That is all an
        /// item is, and all of it matters to something.</summary>
        public Entity Item(string name, Vector3 at, float massKg = 2f, int hands = 1,
                           string weaponId = "", string material = "Metal")
        {
            var e = Maps.SpawnEntity(MapId, w => w.Create(
                new Transform { Position = at, Rotation = Quaternion.Identity },
                new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.2f, 0.1f, 1f), IsSolid = false },
                new MaterialComponent { Material = material, Variant = "0" },
                new IdentityComponent { Name = name },
                new ItemComponent { MassKg = massKg, Hands = hands, WeaponId = weaponId },
                EntityType.StaticObject));
            Assert.NotEqual(Entity.Null, e);
            return e;
        }

        public UserSession Player(string username, Vector3 at)
        {
            int connection = _nextConnection++;
            var entity = World.Create(
                new PlayerComponent { ConnectionId = connection, Username = username },
                EntityType.Player,
                new Transform { Position = at, Rotation = Quaternion.Identity },
                new Velocity { Linear = Vector3.Zero },
                new MaterialComponent { Material = "Generic" },
                new NameComponent { Name = username },
                new ColliderComponent
                {
                    Shape = ColliderShape.Cylinder,
                    Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight,
                                       PhysicsConstants.PlayerRadius * 2),
                    IsSolid = true,
                });
            Maps.IndexEntity(MapId, entity);

            var session = new UserSession { ConnectionId = connection, Username = username, Entity = entity, CurrentMapId = MapId };
            Sessions.AddSession(connection, session);
            return session;
        }

        /// <summary>Puts a player somewhere, the way the movement system would have.</summary>
        public void Move(UserSession session, Vector3 to)
        {
            ref var t = ref World.Get<Transform>(session.Entity);
            t.Position = to;
            t.IsDirty = true;
        }

        /// <summary>Ticks the one system that carrying depends on, the way the server ticks it.</summary>
        public void Tick(int ticks)
        {
            for (int i = 0; i < ticks; i++) ParentSystem.Update(World, Lookup);
        }
    }
}
