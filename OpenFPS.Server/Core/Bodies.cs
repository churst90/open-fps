using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Systems;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// On a body: whose it was, and when it was left there. Server-only, like <see cref="Pedestrian"/>: a
/// client knows a body by its name and by being an item, and needs nothing else.
/// </summary>
public struct Corpse
{
    /// <summary>Who it was: a player's name, or "a pedestrian".</summary>
    public string Of;
    public bool WasPlayer;
    /// <summary>When it was left, seconds on the combat clock.</summary>
    public double LaidAt;
}

/// <summary>
/// On a bag left beside a body: whose things are in it, when it was left, and the things themselves,
/// written down the way the store writes down what a player logs out with (<see cref="Belongings"/>):
/// each made again from its prefab when somebody takes it out. Server-only.
/// </summary>
public struct BelongingsBag
{
    public string Of;
    public double LaidAt;
    public Belongings Contents;
}

/// <summary>
/// What is left when somebody is killed: a body, lying where they fell, that can be picked up.
///
/// Cody (2026-10-05): "When you shoot an npc it should fall to the ground where it was shot and play
/// the item beacon sound and when you walk over to it, you can grab it." So a body is an ITEM, made
/// the way any item is (an <see cref="ItemComponent"/>, the item beacon category, no collider), and
/// everything an item already does it does: E picks it up, the inventory lists it, /drop puts it
/// down. What is its own is only its weight, which is a person's, and so takes both arms and slows
/// whoever carries it (<see cref="HandsService.SpeedLimit"/>).
///
/// It is a new entity, not the person: a walker who was an NPC is taken off the street (and somebody
/// comes walking along later, out of sight), and a player gets up again at the spawn, while the body
/// stays.
/// </summary>
public static class Bodies
{
    /// <summary>
    /// What a body weighs, kilograms: the reference adult of radiological protection (ICRP Publication
    /// 23's "Reference Man", 70 kg). Nobody in the game has a weight of their own yet; when somebody
    /// does, theirs is used instead.
    /// </summary>
    public const float MassKg = 70f;

    /// <summary>A body nobody is carrying is taken away after this long, seconds: half an hour.
    /// Long enough to go and come back for it, and short enough that a street does not fill up.</summary>
    public const double UncarriedSeconds = 30 * 60;

    /// <summary>The most bodies one map holds, and the most bags of belongings. Past it, the oldest
    /// nobody is carrying goes first, so a long fight does not leave a few hundred item beacons crowding
    /// out the one gun somebody dropped.</summary>
    public const int MaxPerMap = 30;

    /// <summary>What an empty bag weighs, kilograms: a canvas holdall.</summary>
    public const float BagMassKg = 1f;

    /// <summary>How far beside the body the bag lies, metres: the two are together, and are two things.</summary>
    public const float BagBesideMetres = 0.5f;

    /// <summary>"sean's belongings", "a pedestrian's belongings".</summary>
    public static string BagNameFor(World world, Entity person) => WhoFor(world, person) + "'s belongings";

    /// <summary>"body of cody", "body of a pedestrian": the name a body goes by, from who it was.</summary>
    public static string NameFor(World world, Entity person)
        => "body of " + WhoFor(world, person);

    /// <summary>Who somebody was, to name their body: a player by their name; anyone else by what they
    /// are ("Pedestrian, Wharf Avenue, west side" is a pedestrian), as the passing narration says them.</summary>
    public static string WhoFor(World world, Entity person)
    {
        if (world.Has<PlayerComponent>(person) && !string.IsNullOrWhiteSpace(world.Get<PlayerComponent>(person).Username))
            return world.Get<PlayerComponent>(person).Username;
        string name = world.Has<IdentityComponent>(person) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(person).Name)
            ? world.Get<IdentityComponent>(person).Name
            : world.Has<NameComponent>(person) ? world.Get<NameComponent>(person).Name ?? "" : "";
        name = name.Trim();
        int comma = name.IndexOf(',');
        if (comma > 0) name = name[..comma].TrimEnd();
        int end = name.Length;
        while (end > 0 && char.IsDigit(name[end - 1])) end--;
        if (end > 0 && end < name.Length && name[end - 1] == ' ') name = name[..(end - 1)].TrimEnd();
        if (name.Length == 0 || name.StartsWith("someone", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("somebody", StringComparison.OrdinalIgnoreCase)) return "someone";
        string lower = name.ToLowerInvariant();
        return ("aeiou".Contains(lower[0]) ? "an " : "a ") + lower;
    }

    /// <summary>
    /// Leaves a body where <paramref name="person"/> fell: on the ground under them, facing the way they
    /// faced, as an item with the item beacon. Returns it.
    /// </summary>
    public static Entity Lay(MapManager maps, string mapId, World world, SpatialGrid<Entity> grid, Entity person, double now)
    {
        var at = world.Get<Transform>(person).Position;
        var rotation = world.Get<Transform>(person).Rotation;
        var where = GroundUnder(world, grid, at, person);
        string name = NameFor(world, person);
        var corpse = new Corpse { Of = WhoFor(world, person), WasPlayer = world.Has<PlayerComponent>(person), LaidAt = now };
        var body = maps.SpawnEntity(mapId, w => w.Create(
            EntityType.Item,
            new Transform { Position = where, Rotation = rotation, IsDirty = true },
            new NameComponent { Name = name },
            new IdentityComponent { Name = name, Description = "dead", Announce = true, BeaconCategory = Beacons.Item },
            new MaterialComponent { Material = "Skin" },
            // Both arms: a person is carried over the shoulder, never in one hand.
            new ItemComponent { MassKg = MassKg, Hands = 2, WeaponId = "" },
            corpse));
        Log.Information("Map {Map}: the {Name} ({Id}) lies at {At}.", mapId, name, body.Id, where);
        return body;
    }

    /// <summary>
    /// Leaves a bag beside a body with everything the person carried in it: what was in their hands and on
    /// their back, and their spare rounds. Cody (2026-10-05): "When a player dies, their inventory should
    /// drop alongside their body, so 2 items are together, body/corpse of [entity/npc] and their
    /// bag/inventory pack/belongings." The things go out of the world into the bag's list (the store's
    /// own form, <see cref="HandsService.Pack"/>), so they come back as themselves, loaded as they were,
    /// to whoever takes them out. Anything that could not be written down (not made from a prefab) is put
    /// down on the ground where they fell instead. Returns the bag, or <see cref="Entity.Null"/> when they
    /// carried nothing; <paramref name="gone"/> is the ids taken out of the world, for the clients.
    /// </summary>
    public static Entity LeaveBelongings(MapManager maps, HandsService hands, string mapId, World world, SpatialGrid<Entity> grid,
                                         Dictionary<int, Entity> lookup, Entity person, double now, out List<int> gone)
    {
        gone = new List<int>();
        var kept = hands.Pack(world, person, lookup, out var packed);
        if (kept.IsEmpty) return Entity.Null;
        float mass = BagMassKg;
        foreach (var item in packed)
            if (world.Has<ItemComponent>(item)) mass += MathF.Max(0.01f, world.Get<ItemComponent>(item).MassKg);
        gone = hands.Release(mapId, world, person, packed);

        var t = world.Get<Transform>(person);
        var right = Vector3.Transform(Vector3.UnitX, t.Rotation); right.Y = 0f;
        right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;
        var where = GroundUnder(world, grid, t.Position + right * BagBesideMetres, person);
        string name = BagNameFor(world, person);
        var bag = maps.SpawnEntity(mapId, w => w.Create(
            EntityType.Item,
            new Transform { Position = where, Rotation = t.Rotation, IsDirty = true },
            new NameComponent { Name = name },
            new IdentityComponent { Name = name, Description = "a bag", Announce = true, BeaconCategory = Beacons.Item },
            new MaterialComponent { Material = "Leather" },
            new ItemComponent { MassKg = mass, Hands = 1, WeaponId = "" },
            new BelongingsBag { Of = WhoFor(world, person), LaidAt = now, Contents = kept }));
        Log.Information("Map {Map}: {Name} ({Id}) lies beside the body: {Count} thing(s) and {Rounds} spare round(s).",
                        mapId, name, bag.Id, kept.Items.Count, kept.Spares.Values.Sum());
        return bag;
    }

    /// <summary>The floor under a point, not counting the person standing on it; where they stand when
    /// no floor is found under them.</summary>
    public static Vector3 GroundUnder(World world, SpatialGrid<Entity> grid, Vector3 at, Entity? skip = null)
    {
        float ground = PhysicsUtils.GetGroundHeight(world, grid, at + new Vector3(0, 0.5f, 0),
                                                    skip is { } s ? new[] { s } : null, out _);
        if (!float.IsFinite(ground) || ground < at.Y - 3f) ground = at.Y;
        return new Vector3(at.X, ground, at.Z);
    }

    /// <summary>
    /// The bodies on a map that are due to go: any nobody is carrying that has lain for
    /// <see cref="UncarriedSeconds"/>, and then, past <see cref="MaxPerMap"/>, the oldest nobody is
    /// carrying. A body somebody has hold of is never taken from them.
    /// </summary>
    public static List<Entity> Due(World world, double now)
    {
        var gone = new List<Entity>();
        var bodies = new List<(Entity E, double LaidAt)>();
        world.Query(new QueryDescription().WithAll<Corpse>(), (Entity e, ref Corpse c) => bodies.Add((e, c.LaidAt)));
        Choose(world, bodies, now, gone);
        // The bags by the same rule, counted on their own: thirty bodies and thirty bags.
        var bags = new List<(Entity E, double LaidAt)>();
        world.Query(new QueryDescription().WithAll<BelongingsBag>(), (Entity e, ref BelongingsBag b) => bags.Add((e, b.LaidAt)));
        Choose(world, bags, now, gone);
        return gone;
    }

    private static void Choose(World world, List<(Entity E, double LaidAt)> all, double now, List<Entity> gone)
    {
        var lying = new List<(Entity E, double LaidAt)>();
        int dropped = 0;
        foreach (var (e, laidAt) in all)
        {
            if (world.Has<HeldComponent>(e)) continue;
            if (now - laidAt >= UncarriedSeconds) { gone.Add(e); dropped++; }
            else lying.Add((e, laidAt));
        }
        int over = all.Count - dropped - MaxPerMap;
        if (over <= 0) return;
        lying.Sort((a, b) => a.LaidAt != b.LaidAt ? a.LaidAt.CompareTo(b.LaidAt) : a.E.Id.CompareTo(b.E.Id));
        for (int i = 0; i < over && i < lying.Count; i++) gone.Add(lying[i].E);
    }
}
