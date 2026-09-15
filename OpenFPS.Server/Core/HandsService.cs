using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
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
        message = $"You take the {name} in {WhereItWent(world, session.Entity, item.Value)}.";
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
        }

        if (names.Count == 0) { message = refused; return false; }

        _maps.RefreshGrid(session.CurrentMapId);
        message = $"You sling the {string.Join(" and the ", names)} onto your back."
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

        var item = string.IsNullOrEmpty(named)
            ? carried[^1]                                     // the last thing you put there
            : carried.FirstOrDefault(e => NameOf(world, e).Contains(named, StringComparison.OrdinalIgnoreCase));
        if (item == Entity.Null)
        { message = $"You have no {named} on your back. {WhatYouAreCarrying(world, session.Entity, lookup)}"; return false; }

        if (!PutInHands(world, session.Entity, item, out string why))
        { message = $"{why} {Carrying(world, lookup, world.Get<HandsComponent>(session.Entity))}"; return false; }

        Bag(world, session.Entity).Remove(item.Id);
        _maps.RefreshGrid(session.CurrentMapId);
        message = $"You take the {NameOf(world, item)} off your back, into {WhereItWent(world, session.Entity, item)}.";
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
                if (all || NameOf(world, stowed).Contains(which, StringComparison.OrdinalIgnoreCase))
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
            landing.Y = ground;

            ref var t = ref world.Get<Transform>(item);
            t.Position = landing;
            t.IsDirty = true;

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
        return Carrying(world, lookup, hands) + " " + WhatYouAreCarrying(world, session.Entity, lookup);
    }

    /// <summary>What a player is carrying, in words.</summary>
    public static string Carrying(World world, Dictionary<int, Entity> lookup, HandsComponent hands)
    {
        if (hands.RightEntityId < 0 && hands.LeftEntityId < 0) return "Your hands are empty.";
        if (hands.RightEntityId == hands.LeftEntityId)
        {
            var both = Find(world, lookup, hands.RightEntityId);
            return both == null ? "Your hands are empty."
                                : $"You are holding the {NameOf(world, both.Value)} in both hands.";
        }

        var parts = new List<string>();
        var right = Find(world, lookup, hands.RightEntityId);
        var left = Find(world, lookup, hands.LeftEntityId);
        if (right != null) parts.Add($"the {NameOf(world, right.Value)} in your right hand");
        if (left != null) parts.Add($"the {NameOf(world, left.Value)} in your left hand");
        return parts.Count == 0 ? "Your hands are empty." : "You are holding " + string.Join(" and ", parts) + ".";
    }

    /// <summary>What is slung on a player's back, with what it weighs and what is left.</summary>
    public static string WhatYouAreCarrying(World world, Entity player, Dictionary<int, Entity> lookup)
    {
        var carried = Stowed(world, player, lookup);
        if (carried.Count == 0) return "You have nothing on your back.";
        float mass = 0f;
        var named = new List<string>();
        foreach (var item in carried) { mass += MassOf(world, item); named.Add(WithArticle(NameOf(world, item))); }
        return $"On your back: {string.Join(", ", named)} — {mass:F1} of {CarryCapacityKg:F0} kilograms.";
    }

    /// <summary>What a player has slung on them weighs, kilograms. Hands do not count: hands are
    /// limited by being two, not by being weak.</summary>
    public static float CarriedMassKg(World world, Entity player, Dictionary<int, Entity> lookup)
    {
        float mass = 0f;
        foreach (var item in Stowed(world, player, lookup)) mass += MassOf(world, item);
        return mass;
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
                    || NameOf(world, hand.Value).Contains(which, StringComparison.OrdinalIgnoreCase))
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

    private static float MassOf(World world, Entity item)
        => world.Has<ItemComponent>(item) ? MathF.Max(0.01f, world.Get<ItemComponent>(item).MassKg) : 1f;

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
                && thisName.IndexOf(named, StringComparison.OrdinalIgnoreCase) < 0) return;
            float d = Vector3.Distance(from, t.Position);
            if (d > bestDistance) return;
            bestDistance = d; best = e; bestName = thisName;
        });
        distance = bestDistance; name = bestName;
        return best;
    }

    private static string NameOf(World world, Entity e)
        => world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).Name)
            ? world.Get<IdentityComponent>(e).Name
            : world.Has<NameComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<NameComponent>(e).Name)
                ? world.Get<NameComponent>(e).Name
                : "thing";
}
