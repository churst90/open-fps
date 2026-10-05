using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// Picking things up, carrying them, and putting them down.
///
/// An item in your hands is the SAME ENTITY as one on the ground — which is what
/// <see cref="InventoryComponent"/> already said it believed, and is the right belief. A rifle you
/// are carrying has a material, a mass and a position, so dropping it makes the noise that mass and
/// that material make meeting that floor, through a calculation that already exists and knows
/// nothing about rifles. Items as rows in a table would need every bit of that inventing again.
///
/// Carrying is the composite machinery once more: a carried item wears a <see cref="ParentComponent"/>
/// pointing at its holder, and ParentSystem has carried children with their parent every tick since
/// long before any of this. A held rifle and a wall in a house are attached the same way, and for
/// the same reason — unlike an OCCUPANT, a held thing really is rigidly attached, and points where
/// you point.
///
/// Two hands, and something that needs both fills both slots. That constraint is what makes carrying
/// things a spatial decision made out loud rather than a menu scrolled through.
///
/// There are three places a thing can be and only three, and each is one question with one answer:
/// <see cref="HeldComponent"/> says SOMEBODY HAS IT (so nobody else can lift it off the floor, or
/// off your back); <see cref="HandsComponent"/> says which of them are in your hands; and
/// <see cref="InventoryComponent"/> says which are slung on you instead. Nothing is a flag that
/// some code remembers to check — a stowed rifle is still a rifle at a position in the world, riding
/// on your back, and if it falls it falls from there.
/// </summary>
public class HandsService
{
    private readonly MapManager _maps;

    /// <summary>How far you can reach for something on the ground.</summary>
    public const float Reach = PhysicsConstants.InteractionRange;

    /// <summary>
    /// What a person can carry slung on them, kilograms.
    ///
    /// A mass and not a slot count, for the same reason everything else here is physical: two
    /// rifles and a crowbar is a load, and six torches is not, and a number of pockets cannot tell
    /// those apart. It also means the limit is one a player can reason about out loud — the /inv
    /// readout says what it weighs, so "I can't take that as well" is arithmetic they can follow
    /// rather than a rule they have to learn.
    /// </summary>
    public const float CarryCapacityKg = 25f;

    /// <summary>Where a carried thing sits relative to its holder: at chest height, a little forward
    /// and out to the side it is held on. Stowed, it rides on the back.</summary>
    private static readonly Vector3 RightHand = new(0.25f, 1.25f, 0.35f);
    private static readonly Vector3 LeftHand = new(-0.25f, 1.25f, 0.35f);
    private static readonly Vector3 BothHands = new(0f, 1.25f, 0.45f);
    private static readonly Vector3 Back = new(0f, 1.1f, -0.2f);

    public HandsService(MapManager maps) => _maps = maps;

    /// <summary>
    /// Told the id of every item that is picked up or put down, so its definition goes out again: an item
    /// is a beacon on the ground and not in somebody's hands (EntityDefinitionFactory), and a definition
    /// is otherwise sent only once. The server wires it to GameServer.SyncAudioComponent.
    /// </summary>
    public Action<int>? Carried { get; set; }

    /// <summary>What is in a player's hands, right first. The same entity twice means it fills both.</summary>
    public static (Entity? Right, Entity? Left) Holding(World world, Entity player, Dictionary<int, Entity> lookup)
    {
        if (!world.Has<HandsComponent>(player)) return (null, null);
        var hands = world.Get<HandsComponent>(player);
        return (Find(world, lookup, hands.RightEntityId), Find(world, lookup, hands.LeftEntityId));
    }

    /// <summary>What a player has slung on them rather than in their hands.</summary>
    public static List<Entity> Stowed(World world, Entity player, Dictionary<int, Entity> lookup)
    {
        var carried = new List<Entity>();
        if (!world.Has<InventoryComponent>(player)) return carried;
        foreach (int id in Bag(world, player))
        {
            var item = Find(world, lookup, id);
            if (item != null) carried.Add(item.Value);
        }
        return carried;
    }

    /// <summary>The list of ids on a player's back, made if the component arrived without one.</summary>
    private static List<int> Bag(World world, Entity player)
    {
        ref var inventory = ref world.Get<InventoryComponent>(player);
        return inventory.ItemEntityIds ??= new List<int>();
    }

    private static Entity? Find(World world, Dictionary<int, Entity> lookup, int id)
        => id >= 0 && lookup.TryGetValue(id, out var e) && world.IsAlive(e) ? e : null;

    /// <summary>The weapon a player is holding, if they are holding one.</summary>
    public static bool TryGetHeldWeapon(World world, Entity player, Dictionary<int, Entity> lookup,
                                        out WeaponDefinition weapon, out Entity item)
    {
        weapon = default!; item = Entity.Null;
        var (right, left) = Holding(world, player, lookup);
        foreach (var candidate in new[] { right, left })
        {
            if (candidate == null || !world.Has<ItemComponent>(candidate.Value)) continue;
            string id = world.Get<ItemComponent>(candidate.Value).WeaponId;
            if (!string.IsNullOrEmpty(id) && WeaponRegistry.TryGet(id, out weapon))
            {
                item = candidate.Value;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Picks something up.
    ///
    /// Refusing tells the player WHAT IS IN THE WAY rather than merely no — a player who cannot see
    /// their own hands has no other way to find out why a thing will not come, and "your hands are
    /// full" is an instruction where "you cannot" is a dead end.
    /// </summary>
    /// <summary>The prefabs a player can carry: what /give can hand out.</summary>
    public IEnumerable<string> GivableItems()
        => _maps.Prefabs.Where(p => p.Value.IsItem || p.Value.Type == EntityType.Item).Select(p => p.Key).OrderBy(k => k);

    /// <summary>
    /// New items made from a prefab and given to a player (staff /give): into their hands while they have
    /// a hand free, then onto their back while it takes the weight, and anything left at their feet.
    /// <paramref name="name"/> is the item's name, <paramref name="placed"/> where they went, for the
    /// receiver ("1 in both hands, 1 on your back").
    /// </summary>
    /// <summary>
    /// The item prefab a player means: its id, its name ("AKM", "Glock 17"), or the start of either
    /// when only one item fits ("akm", "glock"). Null when none or more than one does.
    /// </summary>
    public string? ResolveItem(string typed)
    {
        string t = typed.Trim().ToLowerInvariant().Replace(' ', '_');
        var items = _maps.Prefabs.Where(p => p.Value.IsItem || p.Value.Type == EntityType.Item).ToList();
        string Display(KeyValuePair<string, PrefabTemplate> p) => (p.Value.Name ?? "").ToLowerInvariant().Replace(' ', '_');
        var exact = items.FirstOrDefault(p => p.Key.Equals(t, StringComparison.OrdinalIgnoreCase) || Display(p) == t);
        if (exact.Key != null) return exact.Key;
        var starts = items.Where(p => p.Key.StartsWith(t, StringComparison.OrdinalIgnoreCase) || Display(p).StartsWith(t)).ToList();
        return starts.Count == 1 ? starts[0].Key : null;
    }

    /// <summary>The most of one thing a single /give makes.</summary>
    public const int MaxGive = 50;

    public bool Give(UserSession to, string prefabId, int count, out string name, out string placed, out string message)
    {
        name = placed = message = "";
        prefabId = ResolveItem(prefabId) ?? prefabId;
        if (!_maps.Prefabs.TryGetValue(prefabId, out var template) || !(template.IsItem || template.Type == EntityType.Item))
        { message = $"There is no item called {prefabId}. Items: {string.Join(", ", GivableItems())}."; return false; }
        if (!TryGetHolder(to, out var world, out _, out var lookup) || to.Entity == Entity.Null || !world.IsAlive(to.Entity))
        { message = $"{to.Username} is not in the world just now."; return false; }

        count = Math.Clamp(count, 1, MaxGive);
        var at = world.Get<Transform>(to.Entity).Position;
        int inHands = 0, onBack = 0, atFeet = 0;
        string loaded = "";
        string handsWord = "";
        for (int k = 0; k < count; k++)
        {
            var item = _maps.SpawnPrefab(to.CurrentMapId, prefabId, at);
            if (item == Entity.Null) { message = $"The {prefabId} could not be made."; return false; }
            name = NameOf(world, item);
            Carried?.Invoke(item.Id);
            // A gun given comes loaded, and its spare magazines go into the receiver's pockets; the
            // first gun's says so.
            string gun = Loaded(world, to.Entity, item);
            if (loaded.Length == 0) loaded = gun;
            if (PutInHands(world, to.Entity, item, out _))
            {
                inHands++;
                handsWord = WhereItWent(world, to.Entity, item);
            }
            else if (CarriedMassKg(world, to.Entity, lookup) + MassOf(world, item) <= CarryCapacityKg)
            {
                if (!world.Has<InventoryComponent>(to.Entity)) world.Add(to.Entity, new InventoryComponent());
                Bag(world, to.Entity).Add(item.Id);
                Attach(world, to.Entity, item, Back, bothHands: false);
                onBack++;
            }
            else atFeet++;   // made where they stand: too heavy to carry
        }
        _maps.RefreshGrid(to.CurrentMapId);
        var parts = new List<string>();
        if (inHands > 0) parts.Add(inHands == 1 ? $"in {handsWord}" : $"{inHands} in your hands");
        if (onBack > 0) parts.Add($"{(parts.Count > 0 || onBack > 1 ? onBack + " " : "")}on your back");
        if (atFeet > 0) parts.Add($"{atFeet} at your feet, too heavy to carry");
        placed = (parts.Count == 1 && inHands + onBack + atFeet == 1 ? parts[0] : string.Join(", ", parts)) + loaded;
        Log.Information("{User} was given {Count} {Item}.", to.Username, count, name);
        return true;
    }

    /// <summary>
    /// The interact key's pick-up: the nearest loose item within <paramref name="metres"/>, if there is
    /// one. False (and nothing said) when there is none, so the key can mean something else.
    /// </summary>
    /// <summary>What a player is carrying, for the inventory list: hands first, then the back.</summary>
    public InventoryList List(UserSession session)
    {
        var ids = new List<int>(); var labels = new List<string>(); var places = new List<string>();
        if (!TryGetHolder(session, out var world, out _, out var lookup) || session.Entity == Entity.Null || !world.IsAlive(session.Entity))
            return new InventoryList();
        void Add(Entity e, string place)
        {
            string label = NameOf(world, e);
            if (Arms.IsWeapon(world, e, out var weapon)) label += ", " + Arms.RoundsWords(weapon, Arms.Ammo(world, e, weapon).Rounds);
            ids.Add(e.Id); labels.Add(label); places.Add(place);
        }
        if (world.Has<HandsComponent>(session.Entity))
        {
            var hands = world.Get<HandsComponent>(session.Entity);
            bool both = hands.RightEntityId >= 0 && hands.RightEntityId == hands.LeftEntityId;
            if (hands.RightEntityId >= 0 && lookup.TryGetValue(hands.RightEntityId, out var r)) Add(r, both ? "both hands" : "right hand");
            if (!both && hands.LeftEntityId >= 0 && lookup.TryGetValue(hands.LeftEntityId, out var l)) Add(l, "left hand");
        }
        foreach (var e in Stowed(world, session.Entity, lookup)) Add(e, "back");
        return new InventoryList { Ids = ids.ToArray(), Labels = labels.ToArray(), Places = places.ToArray() };
    }

    public bool TakeWithin(UserSession session, float metres, out string message)
    {
        message = "";
        if (!TryGetHolder(session, out var world, out _, out _) || session.Entity == Entity.Null || !world.IsAlive(session.Entity))
            return false;
        var from = world.Get<Transform>(session.Entity).Position;
        if (Nearest(world, from, "", out _, out _, reach: metres) == null) return false;
        Take(session, "", out message);
        return true;
    }

    public bool Take(UserSession session, string named, out string message)
    {
        message = "";
        if (!TryGetHolder(session, out var world, out _, out var lookup)) { message = "The map is not loaded."; return false; }
        if (session.Entity == Entity.Null || !world.IsAlive(session.Entity))
        { message = "You are not in the world yet."; return false; }

        var from = world.Get<Transform>(session.Entity).Position;
        var item = Nearest(world, from, named, out _, out string name);
        if (item == null)
        {
            var anywhere = Nearest(world, from, named, out float away, out string itsName, reach: 30f);
            message = anywhere != null
                ? $"The {itsName} is {away:F1} metres away. Get closer."
                : string.IsNullOrEmpty(named) ? "There is nothing within reach to pick up."
                                              : $"There is no {named} near you.";
            return false;
        }

        if (!PutInHands(world, session.Entity, item.Value, out string why))
        { message = $"{why} {Carrying(world, lookup, world.Get<HandsComponent>(session.Entity))}"; return false; }

        _maps.RefreshGrid(session.CurrentMapId);
        Carried?.Invoke(item.Value.Id);
        message = $"You take the {name} in {WhereItWent(world, session.Entity, item.Value)}{Loaded(world, session.Entity, item.Value)}."
                + AnotherWithinReach(world, from);
        Log.Information("{User} picked up {Item} ({Id}).", session.Username, name, item.Value.Id);
        return true;
    }

    /// <summary>
    /// Slings what you are holding onto your back, freeing the hand.
    ///
    /// The whole point of having a bag at all: two hands is a hard limit and a rifle spends both, so
    /// without somewhere to put a thing down that is not the ground, carrying a rifle would mean
    /// carrying nothing else ever. Stowed is not stored — it stays an entity on your back at a
    /// position, and it comes back to your hands from there.
    /// </summary>
    public bool Stow(UserSession session, string which, out string message)
    {
        message = "";
        if (!TryGetHolder(session, out var world, out _, out var lookup)) { message = "The map is not loaded."; return false; }
        if (!world.Has<HandsComponent>(session.Entity)) { message = "You are not holding anything."; return false; }

        var chosen = InHands(world, session.Entity, lookup, which);
        if (chosen.Count == 0)
        {
            message = string.IsNullOrEmpty(which) ? "You are not holding anything."
                                                  : $"You are not holding a {which}.";
            return false;
        }

        var names = new List<string>();
        var rounds = new List<string>();
        string refused = "";
        foreach (var item in chosen)
        {
            float mass = MassOf(world, item);
            float already = CarriedMassKg(world, session.Entity, lookup);
            if (already + mass > CarryCapacityKg)
            {
                // Refusing says the arithmetic, because the arithmetic is the rule: a player who is
                // told the numbers can work out what to put down, and one told "too heavy" cannot.
                refused = $"The {NameOf(world, item)} will not go on as well — {already:F1} plus "
                        + $"{mass:F1} is over the {CarryCapacityKg:F0} kilograms you can manage.";
                continue;
            }

            ClearFromHands(world, session.Entity, item.Id);
            if (!world.Has<InventoryComponent>(session.Entity)) world.Add(session.Entity, new InventoryComponent());
            Bag(world, session.Entity).Add(item.Id);
            Attach(world, session.Entity, item, Back, bothHands: false);
            names.Add(NameOf(world, item));
            if (Arms.IsWeapon(world, item, out var weapon))
                rounds.Add(Arms.Ammo(world, item, weapon).Rounds is int n && n > 0
                    ? $"the {NameOf(world, item)} has {Arms.RoundsWords(weapon, n)} in it"
                    : $"the {NameOf(world, item)} is empty");
        }

        if (names.Count == 0) { message = refused; return false; }

        _maps.RefreshGrid(session.CurrentMapId);
        message = $"You sling the {string.Join(" and the ", names)} onto your back."
                + (rounds.Count > 0 ? " " + Capitalised(string.Join(", and ", rounds)) + "." : "")
                + (refused.Length > 0 ? " " + refused : "");
        return true;
    }

    /// <summary>
    /// Takes something off your back and puts it in your hands.
    ///
    /// Named, because a bag you cannot see is a bag you address by saying what you want out of it.
    /// </summary>
    public bool Draw(UserSession session, string named, out string message)
    {
        message = "";
        if (!TryGetHolder(session, out var world, out _, out var lookup)) { message = "The map is not loaded."; return false; }

        var carried = Stowed(world, session.Entity, lookup);
        if (carried.Count == 0) { message = "You have nothing on your back."; return false; }

        // An index and not FirstOrDefault: a miss there is default(Entity), id 0, which is not
        // Entity.Null (id -1) but a real entity, the first one the map spawned.
        int found = string.IsNullOrEmpty(named)
            ? carried.Count - 1                               // the last thing you put there
            : carried.FindIndex(e => Matches(world, e, named));
        if (found < 0)
        { message = $"You have no {named} on your back. {WhatYouAreCarrying(world, session.Entity, lookup)}"; return false; }
        var item = carried[found];

        if (!PutInHands(world, session.Entity, item, out string why))
        { message = $"{why} {Carrying(world, lookup, world.Get<HandsComponent>(session.Entity))}"; return false; }

        Bag(world, session.Entity).Remove(item.Id);
        _maps.RefreshGrid(session.CurrentMapId);
        // A gun is DRAWN, and what matters about it the moment it is in your hands is what is in it.
        message = Arms.IsWeapon(world, item, out var weapon)
            ? $"You draw the {NameOf(world, item)}, {Arms.RoundsWords(weapon, Arms.Ammo(world, item, weapon).Rounds)}."
            : $"You take the {NameOf(world, item)} off your back, into {WhereItWent(world, session.Entity, item)}.";
        return true;
    }

    /// <summary>
    /// Puts something down, and it lands.
    ///
    /// The landing is the interesting half and it costs nothing: a dropped thing has a mass and a
    /// material and the ground has a material, which is the whole input to the impact calculation
    /// everything else already uses. A dropped steel bar and a dropped cushion are as different as
    /// they should be, with nobody having recorded either. It falls from wherever it actually was —
    /// a hand at chest height, or a back — so the height is read off the world rather than assumed.
    /// </summary>
    public bool Drop(UserSession session, string which, out string message,
                     Action<int, string, IReadOnlyList<TransientSound>>? heard = null)
    {
        message = "";
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out var grid, out var lookup))
        { message = "The map is not loaded."; return false; }
        if (session.Entity == Entity.Null || !world.IsAlive(session.Entity))
        { message = "You are not in the world yet."; return false; }

        bool all = which.Equals("all", StringComparison.OrdinalIgnoreCase);
        var dropping = InHands(world, session.Entity, lookup, all ? "all" : which);

        // A name, or "all", also reaches what is slung on your back: a thing you are carrying is a
        // thing you can put down, and making a player draw it first to drop it is ceremony.
        if (all || (dropping.Count == 0 && !string.IsNullOrEmpty(which) && !IsHandWord(which)))
            foreach (var stowed in Stowed(world, session.Entity, lookup))
                if (all || Matches(world, stowed, which))
                    if (!dropping.Contains(stowed)) dropping.Add(stowed);

        if (dropping.Count == 0)
        {
            message = which.Equals("left", StringComparison.OrdinalIgnoreCase) ? "Your left hand is empty."
                    : which.Equals("right", StringComparison.OrdinalIgnoreCase) ? "Your right hand is empty."
                    : string.IsNullOrEmpty(which) ? "You are not holding anything."
                    : $"You have no {which}.";
            return false;
        }

        var at = world.Get<Transform>(session.Entity).Position;
        var forward = Vector3.Transform(new Vector3(0, 0, 1), world.Get<Transform>(session.Entity).Rotation);
        var names = new List<string>();

        foreach (var item in dropping)
        {
            names.Add(NameOf(world, item));
            float from = world.Get<Transform>(item).Position.Y;

            ClearFromHands(world, session.Entity, item.Id);
            if (world.Has<InventoryComponent>(session.Entity)) Bag(world, session.Entity).Remove(item.Id);
            if (world.Has<HeldComponent>(item)) world.Remove<HeldComponent>(item);
            if (world.Has<ParentComponent>(item)) world.Remove<ParentComponent>(item);

            // At your feet, just in front, on whatever the floor turns out to be.
            var landing = at + forward * 0.6f;
            float ground = PhysicsUtils.GetGroundHeight(world, grid, landing, out string floor);
            // No floor found under that point is "-1000", and the item went a kilometre under the map,
            // out of reach and out of every beacon's range. It lands where you are standing instead.
            if (ground < -900f) { ground = at.Y; floor = ""; }
            landing.Y = ground;

            ref var t = ref world.Get<Transform>(item);
            t.Position = landing;
            t.IsDirty = true;
            Carried?.Invoke(item.Id);

            if (heard != null && world.Has<ItemComponent>(item))
            {
                // It falls from where it was — a hand, or a back — so it arrives at sqrt(2gh), the
                // same arithmetic that tells a listener which floor a window was on.
                float fell = MathF.Max(0.05f, from - ground);
                float speed = MathF.Sqrt(2f * PhysicsConstants.Gravity * fell);
                float mass = MassOf(world, item);
                var itemMaterial = AcousticRegistry.GetProperties(
                    world.Has<MaterialComponent>(item) ? world.Get<MaterialComponent>(item).Material ?? "Generic" : "Generic");
                var floorMaterial = AcousticRegistry.GetProperties(string.IsNullOrEmpty(floor) ? "Generic" : floor);
                heard(item.Id, NameOf(world, item),
                      ImpactAcoustics.Between(itemMaterial, floorMaterial, landing, speed,
                                              mass, mass * 500f, 2f, 2f, 0.2f));
            }
        }

        _maps.RefreshGrid(session.CurrentMapId);
        message = $"You put down the {string.Join(" and the ", names)}.";
        Log.Information("{User} put down {Items}.", session.Username, string.Join(", ", names));
        return true;
    }

    /// <summary>
    /// Everything a player has on them, hands first, in words.
    ///
    /// This is the whole inventory screen, and it is a sentence rather than a grid because that is
    /// what a screen reader can take in at a go. Hands come first because hands are the part with a
    /// hard limit, and the weight comes last because the weight is the other limit.
    /// </summary>
    public string Readout(UserSession session)
    {
        if (!TryGetHolder(session, out var world, out _, out var lookup)) return "The map is not loaded.";
        if (session.Entity == Entity.Null || !world.IsAlive(session.Entity)) return "You are not in the world yet.";

        var hands = world.Has<HandsComponent>(session.Entity) ? world.Get<HandsComponent>(session.Entity) : new HandsComponent();
        string spare = Arms.ReserveWords(world, session.Entity);
        return Carrying(world, lookup, hands) + " " + WhatYouAreCarrying(world, session.Entity, lookup)
             + (spare.Length > 0 ? $" Spare ammunition: {spare}." : "");
    }

    /// <summary>What a player is carrying, in words.</summary>
    public static string Carrying(World world, Dictionary<int, Entity> lookup, HandsComponent hands)
    {
        if (hands.RightEntityId < 0 && hands.LeftEntityId < 0) return "Your hands are empty.";
        if (hands.RightEntityId == hands.LeftEntityId)
        {
            var both = Find(world, lookup, hands.RightEntityId);
            return both == null ? "Your hands are empty."
                                : $"You are holding the {Labelled(world, both.Value)} in both hands.";
        }

        var parts = new List<string>();
        var right = Find(world, lookup, hands.RightEntityId);
        var left = Find(world, lookup, hands.LeftEntityId);
        if (right != null) parts.Add($"the {Labelled(world, right.Value)} in your right hand");
        if (left != null) parts.Add($"the {Labelled(world, left.Value)} in your left hand");
        return parts.Count == 0 ? "Your hands are empty." : "You are holding " + string.Join(" and ", parts) + ".";
    }

    /// <summary>What is slung on a player's back, with what it weighs and what is left.</summary>
    public static string WhatYouAreCarrying(World world, Entity player, Dictionary<int, Entity> lookup)
    {
        var carried = Stowed(world, player, lookup);
        if (carried.Count == 0) return "You have nothing on your back.";
        float mass = 0f;
        var named = new List<string>();
        foreach (var item in carried) { mass += MassOf(world, item); named.Add(WithArticle(LabelledInList(world, item))); }
        return $"You have {string.Join(", ", named)} on your back, {mass:F1} of {CarryCapacityKg:F0} kilograms.";
    }

    /// <summary>What a player has slung on them weighs, kilograms. Hands do not count: hands are
    /// limited by being two, not by being weak.</summary>
    public static float CarriedMassKg(World world, Entity player, Dictionary<int, Entity> lookup)
    {
        float mass = 0f;
        foreach (var item in Stowed(world, player, lookup)) mass += MassOf(world, item);
        return mass;
    }

    // ── Leaving the world with your things, and coming back with them ───────────────────────────

    /// <summary>
    /// What a player is carrying, written down to be kept while they are away: hands first, then the
    /// back in the order it was loaded, then the spare rounds. <paramref name="packed"/> is the things
    /// written down, which the caller takes out of the world once the record is safely stored
    /// (<see cref="Release"/>). A thing not made from a prefab cannot be made again, so it is left out
    /// and stays with the body, to be put down like anything else.
    /// </summary>
    public Belongings Pack(World world, Entity player, Dictionary<int, Entity> lookup, out List<Entity> packed)
    {
        var kept = new Belongings();
        packed = new List<Entity>();

        void Keep(Entity item, string place, List<Entity> into)
        {
            string prefab = world.Has<IdentityComponent>(item) ? world.Get<IdentityComponent>(item).PrefabId ?? "" : "";
            if (prefab.Length == 0 || !_maps.Prefabs.ContainsKey(prefab)) return;
            var saved = new SavedItem { Prefab = prefab, Place = place };
            // A gun nobody has asked about yet is loaded the way it would be the moment anybody did.
            if (Arms.IsWeapon(world, item, out var weapon)) Arms.Ammo(world, item, weapon);
            if (world.Has<AmmoComponent>(item))
            {
                var ammo = world.Get<AmmoComponent>(item);
                saved.Rounds = ammo.Rounds;
                saved.Capacity = ammo.Capacity;
                saved.SpareRounds = ammo.SpareRounds;
            }
            kept.Items.Add(saved);
            into.Add(item);
        }

        if (world.Has<HandsComponent>(player))
        {
            var hands = world.Get<HandsComponent>(player);
            var right = Find(world, lookup, hands.RightEntityId);
            var left = Find(world, lookup, hands.LeftEntityId);
            if (right != null && hands.RightEntityId == hands.LeftEntityId) Keep(right.Value, SavedItem.BothHands, packed);
            else
            {
                if (right != null) Keep(right.Value, SavedItem.Right, packed);
                if (left != null) Keep(left.Value, SavedItem.Left, packed);
            }
        }
        foreach (var item in Stowed(world, player, lookup)) Keep(item, SavedItem.Back, packed);

        if (world.Has<AmmoReserveComponent>(player) && world.Get<AmmoReserveComponent>(player).Rounds is { } rounds)
            foreach (var (ammoId, n) in rounds)
                if (n > 0) kept.Spares[ammoId] = n;
        return kept;
    }

    /// <summary>
    /// Takes packed things off a player and out of the world, once what they were is stored: out of
    /// the hands and off the back first, so nothing is left naming them, then destroyed. The spare
    /// rounds go too. Returns the ids destroyed, for the caller to tell the clients.
    /// </summary>
    public List<int> Release(string mapId, World world, Entity player, IEnumerable<Entity> packed)
    {
        var gone = new List<int>();
        foreach (var item in packed)
        {
            ClearFromHands(world, player, item.Id);
            if (world.Has<InventoryComponent>(player)) Bag(world, player).Remove(item.Id);
            if (!world.IsAlive(item)) continue;
            gone.Add(item.Id);
            _maps.DestroyEntity(mapId, item);
        }
        if (world.Has<AmmoReserveComponent>(player)) world.Get<AmmoReserveComponent>(player).Rounds?.Clear();
        _maps.RefreshGrid(mapId);
        return gone;
    }

    /// <summary>
    /// Gives a player back what they were carrying when they left: each thing made again from its
    /// prefab, into the hand it was in (or both, or onto the back), loaded as it was, and the spare
    /// rounds into their pockets. Nothing is weighed or refused: it was all being carried when it was
    /// put away. A thing whose prefab this server no longer has cannot be made, and is said in the log.
    /// Returns how many things came back.
    /// </summary>
    public int Unpack(string mapId, World world, Entity player, Belongings kept, string username = "")
    {
        var at = world.Get<Transform>(player).Position;
        if (!world.Has<HandsComponent>(player)) world.Add(player, new HandsComponent());
        int made = 0;
        foreach (var saved in kept.Items)
        {
            if (!_maps.Prefabs.ContainsKey(saved.Prefab))
            {
                Log.Warning("{User} had a {Prefab}, which this server can no longer make; it is lost.", username, saved.Prefab);
                continue;
            }
            var item = _maps.SpawnPrefab(mapId, saved.Prefab, at);
            if (item == Entity.Null) continue;
            if (saved.Rounds is int rounds)
                SetOrAdd(world, item, new AmmoComponent
                {
                    Rounds = rounds,
                    Capacity = saved.Capacity ?? rounds,
                    SpareRounds = saved.SpareRounds ?? 0,
                });

            ref var hands = ref world.Get<HandsComponent>(player);
            switch (saved.Place)
            {
                case SavedItem.BothHands when hands.RightEntityId < 0 && hands.LeftEntityId < 0:
                    hands.RightEntityId = hands.LeftEntityId = item.Id;
                    Attach(world, player, item, BothHands, bothHands: true);
                    break;
                case SavedItem.Right when hands.RightEntityId < 0:
                    hands.RightEntityId = item.Id;
                    Attach(world, player, item, RightHand, bothHands: false);
                    break;
                case SavedItem.Left when hands.LeftEntityId < 0:
                    hands.LeftEntityId = item.Id;
                    Attach(world, player, item, LeftHand, bothHands: false);
                    break;
                default:
                    // The back, or a hand somehow already full: it rides on the back instead.
                    if (!world.Has<InventoryComponent>(player)) world.Add(player, new InventoryComponent());
                    Bag(world, player).Add(item.Id);
                    Attach(world, player, item, Back, bothHands: false);
                    break;
            }
            Carried?.Invoke(item.Id);
            made++;
        }
        foreach (var (ammoId, n) in kept.Spares)
            if (n > 0) Arms.AddReserve(world, player, ammoId, n);
        _maps.RefreshGrid(mapId);
        return made;
    }

    // ── The mechanics the four commands share ───────────────────────────────────────────────────

    private bool TryGetHolder(UserSession session, out World world, out SpatialGrid<Entity> grid,
                              out Dictionary<int, Entity> lookup)
        => _maps.TryGetMap(session.CurrentMapId, out world, out _, out grid, out lookup);

    /// <summary>
    /// Fills a hand, or says which hand is in the way.
    ///
    /// Something needing both hands is recorded in BOTH slots — the same entity id twice — so that
    /// "have I a hand free" stays one question with one answer.
    /// </summary>
    private static bool PutInHands(World world, Entity player, Entity item, out string why)
    {
        why = "";
        if (!world.Has<HandsComponent>(player)) world.Add(player, new HandsComponent());
        ref var hands = ref world.Get<HandsComponent>(player);
        bool needsBoth = world.Has<ItemComponent>(item) && world.Get<ItemComponent>(item).Hands >= 2;
        string name = NameOf(world, item);

        if (needsBoth && (hands.RightEntityId >= 0 || hands.LeftEntityId >= 0))
        { why = $"The {name} takes both hands, and yours are not empty."; return false; }
        if (!needsBoth && hands.RightEntityId >= 0 && hands.LeftEntityId >= 0)
        { why = "Your hands are full."; return false; }

        int id = item.Id;
        if (needsBoth) { hands.RightEntityId = id; hands.LeftEntityId = id; }
        else if (hands.RightEntityId < 0) hands.RightEntityId = id;
        else hands.LeftEntityId = id;

        Attach(world, player, item, needsBoth ? BothHands : hands.RightEntityId == id ? RightHand : LeftHand, needsBoth);
        return true;
    }

    /// <summary>
    /// Hangs an item off its holder at an offset, and puts it there now rather than next tick.
    ///
    /// Now rather than next tick because the spatial grid is rebuilt immediately after, and a thing
    /// indexed at the place it was lying on the floor is a thing other players can still trip over
    /// while you walk away with it.
    /// </summary>
    private static void Attach(World world, Entity player, Entity item, Vector3 offset, bool bothHands)
    {
        SetOrAdd(world, item, new HeldComponent { HolderEntityId = player.Id, BothHands = bothHands });
        SetOrAdd(world, item, new ParentComponent
        {
            ParentEntityId = player.Id,
            LocalPosition = offset,
            LocalRotation = Quaternion.Identity,
        });
        // A thing being carried moves, so the grid has to treat it as something that moves.
        if (!world.Has<Velocity>(item)) world.Add(item, new Velocity());

        var holder = world.Get<Transform>(player);
        ref var t = ref world.Get<Transform>(item);
        t.Position = holder.Position + Vector3.Transform(offset, holder.Rotation);
        t.Rotation = holder.Rotation;
        t.IsDirty = true;
    }

    private static void SetOrAdd<T>(World world, Entity e, T value) where T : struct
    {
        if (world.Has<T>(e)) world.Set(e, value); else world.Add(e, value);
    }

    private static void ClearFromHands(World world, Entity player, int itemId)
    {
        if (!world.Has<HandsComponent>(player)) return;
        ref var hands = ref world.Get<HandsComponent>(player);
        if (hands.RightEntityId == itemId) hands.RightEntityId = -1;
        if (hands.LeftEntityId == itemId) hands.LeftEntityId = -1;
    }

    private static bool IsHandWord(string which)
        => which.Equals("left", StringComparison.OrdinalIgnoreCase)
        || which.Equals("right", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which held things a word means: a hand, everything, a name, or — said on its own — whatever
    /// is in the hand that has something in it.
    /// </summary>
    private static List<Entity> InHands(World world, Entity player, Dictionary<int, Entity> lookup, string which)
    {
        var chosen = new List<Entity>();
        if (!world.Has<HandsComponent>(player)) return chosen;
        var hands = world.Get<HandsComponent>(player);
        var right = Find(world, lookup, hands.RightEntityId);
        var left = Find(world, lookup, hands.LeftEntityId);

        bool all = which.Equals("all", StringComparison.OrdinalIgnoreCase);
        if (which.Equals("right", StringComparison.OrdinalIgnoreCase)) { if (right != null) chosen.Add(right.Value); return chosen; }
        if (which.Equals("left", StringComparison.OrdinalIgnoreCase)) { if (left != null) chosen.Add(left.Value); return chosen; }

        foreach (var hand in new[] { right, left })
        {
            if (hand == null || chosen.Contains(hand.Value)) continue;
            if (all || string.IsNullOrEmpty(which)
                    || Matches(world, hand.Value, which))
                chosen.Add(hand.Value);
            // Bare /drop means the one thing, not both: the right hand if it has anything.
            if (string.IsNullOrEmpty(which) && chosen.Count > 0) break;
        }
        return chosen;
    }

    private static string WhereItWent(World world, Entity player, Entity item)
    {
        var hands = world.Get<HandsComponent>(player);
        if (hands.RightEntityId == item.Id && hands.LeftEntityId == item.Id) return "both hands";
        return hands.RightEntityId == item.Id ? "your right hand" : "your left hand";
    }

    /// <summary>
    /// "a torch", "an AKM".
    ///
    /// Worth the four lines because every one of these sentences is SPOKEN, and a screen reader
    /// reads "a AKM" exactly as written — a stumble in the middle of the one line telling a player
    /// what they are carrying. The rule is the sound of the first letter, which is right for an
    /// initialism read letter by letter as well as for an ordinary word.
    /// </summary>
    private static string WithArticle(string name)
        => string.IsNullOrEmpty(name) ? "a thing"
         : "AEIOU".Contains(char.ToUpperInvariant(name[0])) ? $"an {name}" : $"a {name}";

    /// <summary>A thing's name, and for a gun what is in it: "AKM, 30 rounds,", "Glock 17, empty,".
    /// The trailing comma closes the aside, so it reads in the middle of a sentence.</summary>
    private static string Labelled(World world, Entity item)
        => Arms.IsWeapon(world, item, out var weapon)
            ? $"{NameOf(world, item)}, {Arms.RoundsWords(weapon, Arms.Ammo(world, item, weapon).Rounds)},"
            : NameOf(world, item);

    /// <summary>The same for a list, where commas already separate the things: "AKM with 30 rounds",
    /// "Glock 17 with nothing in it".</summary>
    private static string LabelledInList(World world, Entity item)
        => Arms.IsWeapon(world, item, out var weapon)
            ? Arms.Ammo(world, item, weapon).Rounds is int n && n > 0
                ? $"{NameOf(world, item)} with {Arms.RoundsWords(weapon, n)}"
                : $"{NameOf(world, item)} with nothing in it"
            : NameOf(world, item);

    /// <summary>
    /// What a gun just come into somebody's hands holds, and the spare rounds that came with it,
    /// pocketed: ", with 30 rounds in it and 90 spare". Empty for anything that is not a gun.
    /// </summary>
    private static string Loaded(World world, Entity holder, Entity item)
    {
        if (!Arms.IsWeapon(world, item, out var weapon)) return "";
        int pocketed = Arms.PocketSpares(world, holder, item);
        int rounds = Arms.Ammo(world, item, weapon).Rounds;
        string inIt = rounds > 0 ? $"with {Arms.RoundsWords(weapon, rounds)} in it" : "empty";
        return pocketed > 0 ? $", {inIt}, and {pocketed} spare" : $", {inIt}";
    }

    private static string Capitalised(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static float MassOf(World world, Entity item)
        => world.Has<ItemComponent>(item) ? MathF.Max(0.01f, world.Get<ItemComponent>(item).MassKg) : 1f;

    /// <summary>
    /// " Another 2 within reach." when more loose things lie within E's reach after taking one, so a
    /// second gun beside the first is not a secret (Cody, 2026-10-04); "" when nothing does.
    /// </summary>
    internal static string AnotherWithinReach(World world, Vector3 from)
    {
        int left = 0;
        world.Query(new QueryDescription().WithAll<Transform, ItemComponent>(), (Entity e, ref Transform t, ref ItemComponent _) =>
        {
            if (world.Has<HeldComponent>(e)) return;
            if (Vector3.Distance(from, t.Position) <= PhysicsConstants.PickUpReach) left++;
        });
        return left switch { 0 => "", 1 => " Another one within reach.", _ => $" Another {left} within reach." };
    }

    private static Entity? Nearest(World world, Vector3 from, string named, out float distance,
                                   out string name, float reach = Reach)
    {
        Entity? best = null;
        float bestDistance = reach;
        string bestName = "thing";
        world.Query(new QueryDescription().WithAll<Transform, ItemComponent>(), (Entity e, ref Transform t, ref ItemComponent _) =>
        {
            if (world.Has<HeldComponent>(e)) return;                   // somebody already has it
            string thisName = NameOf(world, e);
            if (!string.IsNullOrEmpty(named)
                && !Matches(world, e, named)) return;
            float d = Vector3.Distance(from, t.Position);
            if (d > bestDistance) return;
            bestDistance = d; best = e; bestName = thisName;
        });
        distance = bestDistance; name = bestName;
        return best;
    }

    /// <summary>
    /// Whether a thing is the one named: by its name or part of it ("akm"), or by its own number ("#6577"),
    /// which is how the inventory list picks one of ten rifles that all have the same name.
    /// </summary>
    private static bool Matches(World world, Entity e, string named)
    {
        if (named.StartsWith('#') && int.TryParse(named.AsSpan(1), out int id)) return e.Id == id;
        return NameOf(world, e).Contains(named, StringComparison.OrdinalIgnoreCase);
    }

    private static string NameOf(World world, Entity e)
        => world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).Name)
            ? world.Get<IdentityComponent>(e).Name
            : world.Has<NameComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<NameComponent>(e).Name)
                ? world.Get<NameComponent>(e).Name
                : "thing";
}
