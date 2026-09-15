using System;
using System.Collections.Generic;
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
/// Carrying is the composite machinery once more: a held item wears a <see cref="ParentComponent"/>
/// pointing at its holder, and ParentSystem has carried children with their parent every tick since
/// long before any of this. A held rifle and a wall in a house are attached the same way, and for
/// the same reason — unlike an OCCUPANT, a held thing really is rigidly attached, and points where
/// you point.
///
/// Two hands, and something that needs both fills both slots. That constraint is what makes carrying
/// things a spatial decision made out loud rather than a menu scrolled through.
/// </summary>
public class HandsService
{
    private readonly MapManager _maps;

    /// <summary>How far you can reach for something on the ground.</summary>
    public const float Reach = PhysicsConstants.InteractionRange;

    /// <summary>Where a held thing sits relative to its holder: at chest height, a little forward
    /// and out to the side it is held on.</summary>
    private static readonly Vector3 RightHand = new(0.25f, 1.25f, 0.35f);
    private static readonly Vector3 LeftHand = new(-0.25f, 1.25f, 0.35f);
    private static readonly Vector3 BothHands = new(0f, 1.25f, 0.45f);

    public HandsService(MapManager maps) => _maps = maps;

    /// <summary>What is in a player's hands, right first. The same entity twice means it fills both.</summary>
    public static (Entity? Right, Entity? Left) Holding(World world, Entity player, Dictionary<int, Entity> lookup)
    {
        if (!world.Has<HandsComponent>(player)) return (null, null);
        var hands = world.Get<HandsComponent>(player);
        return (Find(world, lookup, hands.RightEntityId), Find(world, lookup, hands.LeftEntityId));
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
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out _, out var lookup))
        { message = "The map is not loaded."; return false; }
        if (session.Entity == Entity.Null || !world.IsAlive(session.Entity))
        { message = "You are not in the world yet."; return false; }

        var from = world.Get<Transform>(session.Entity).Position;
        var item = Nearest(world, from, named, out float distance, out string name);
        if (item == null)
        {
            var anywhere = Nearest(world, from, named, out float away, out string itsName, reach: 30f);
            message = anywhere != null
                ? $"The {itsName} is {away:F1} metres away. Get closer."
                : string.IsNullOrEmpty(named) ? "There is nothing within reach to pick up."
                                              : $"There is no {named} near you.";
            return false;
        }

        if (!world.Has<HandsComponent>(session.Entity)) world.Add(session.Entity, new HandsComponent());
        ref var hands = ref world.Get<HandsComponent>(session.Entity);
        var wanted = world.Get<ItemComponent>(item.Value);
        bool needsBoth = wanted.Hands >= 2;

        if (needsBoth && (hands.RightEntityId >= 0 || hands.LeftEntityId >= 0))
        { message = $"The {name} takes both hands, and yours are not empty. {Carrying(world, lookup, hands)}"; return false; }
        if (!needsBoth && hands.RightEntityId >= 0 && hands.LeftEntityId >= 0)
        { message = $"Your hands are full. {Carrying(world, lookup, hands)}"; return false; }

        int id = item.Value.Id;
        if (needsBoth) { hands.RightEntityId = id; hands.LeftEntityId = id; }
        else if (hands.RightEntityId < 0) hands.RightEntityId = id;
        else hands.LeftEntityId = id;

        var offset = needsBoth ? BothHands : hands.RightEntityId == id ? RightHand : LeftHand;
        world.Add(item.Value, new HeldComponent { HolderEntityId = session.Entity.Id, BothHands = needsBoth });
        world.Add(item.Value, new ParentComponent
        {
            ParentEntityId = session.Entity.Id,
            LocalPosition = offset,
            LocalRotation = Quaternion.Identity,
        });
        // A thing being carried moves, so the grid has to treat it as something that moves.
        if (!world.Has<Velocity>(item.Value)) world.Add(item.Value, new Velocity());
        _maps.RefreshGrid(session.CurrentMapId);

        message = needsBoth
            ? $"You take the {name} in both hands."
            : $"You take the {name} in your {(hands.RightEntityId == id ? "right" : "left")} hand.";
        Log.Information("{User} picked up {Item} ({Id}).", session.Username, name, id);
        return true;
    }

    /// <summary>
    /// Puts something down, and it lands.
    ///
    /// The landing is the interesting half and it costs nothing: a dropped thing has a mass and a
    /// material and the ground has a material, which is the whole input to the impact calculation
    /// everything else already uses. A dropped steel bar and a dropped cushion are as different as
    /// they should be, with nobody having recorded either.
    /// </summary>
    public bool Drop(UserSession session, string which, out string message,
                     Action<int, string, IReadOnlyList<TransientSound>>? heard = null)
    {
        message = "";
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out var grid, out var lookup))
        { message = "The map is not loaded."; return false; }
        if (!world.Has<HandsComponent>(session.Entity)) { message = "You are not holding anything."; return false; }

        ref var hands = ref world.Get<HandsComponent>(session.Entity);
        bool all = which.Equals("all", StringComparison.OrdinalIgnoreCase);
        bool leftOnly = which.Equals("left", StringComparison.OrdinalIgnoreCase);
        bool rightOnly = which.Equals("right", StringComparison.OrdinalIgnoreCase);

        var dropping = new List<int>();
        if (all || rightOnly || string.IsNullOrEmpty(which)) if (hands.RightEntityId >= 0) dropping.Add(hands.RightEntityId);
        if (all || leftOnly) if (hands.LeftEntityId >= 0 && !dropping.Contains(hands.LeftEntityId)) dropping.Add(hands.LeftEntityId);
        if (leftOnly && hands.LeftEntityId < 0) { message = "Your left hand is empty."; return false; }
        if (dropping.Count == 0) { message = "You are not holding anything."; return false; }

        var names = new List<string>();
        foreach (int id in dropping)
        {
            if (!lookup.TryGetValue(id, out var item) || !world.IsAlive(item)) continue;
            names.Add(NameOf(world, item));

            if (hands.RightEntityId == id) hands.RightEntityId = -1;
            if (hands.LeftEntityId == id) hands.LeftEntityId = -1;

            world.Remove<HeldComponent>(item);
            world.Remove<ParentComponent>(item);

            // At your feet, just in front, on whatever the floor turns out to be.
            var at = world.Get<Transform>(session.Entity).Position;
            var forward = Vector3.Transform(new Vector3(0, 0, 1), world.Get<Transform>(session.Entity).Rotation);
            var landing = at + forward * 0.6f;
            float ground = PhysicsUtils.GetGroundHeight(world, grid, landing, out string floor);
            landing.Y = ground;

            ref var t = ref world.Get<Transform>(item);
            float fell = MathF.Max(0.1f, RightHand.Y - 0f);
            t.Position = landing;
            t.IsDirty = true;

            if (heard != null && world.Has<ItemComponent>(item))
            {
                // It falls the height of your hands, so it arrives at sqrt(2gh) — the same arithmetic
                // that tells a listener which floor a window was on.
                float speed = MathF.Sqrt(2f * PhysicsConstants.Gravity * fell);
                var mass = world.Get<ItemComponent>(item).MassKg;
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
        return true;
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
