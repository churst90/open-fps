using System;
using System.Collections.Generic;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Core;

/// <summary>
/// The arithmetic of rounds: what is in a gun, what a person carries spare, and how the two are
/// spoken. Shared by carrying (<see cref="HandsService"/>), which says what a gun holds when you pick
/// it up or draw it, and by fighting (<see cref="CombatService"/>), which spends and refills it.
/// </summary>
public static class Arms
{
    /// <summary>
    /// Spare magazines that come with a gun that has never been picked up: three, which is what a
    /// rifleman's chest rig carries besides the one in the rifle, and enough that a player who finds a
    /// gun can play with it straight away.
    /// </summary>
    public const int SpareMagazines = 3;

    /// <summary>The weapon an item is, if it is one.</summary>
    public static bool IsWeapon(World world, Entity item, out WeaponDefinition weapon)
    {
        weapon = default!;
        return world.IsAlive(item) && world.Has<ItemComponent>(item)
            && WeaponRegistry.TryGet(world.Get<ItemComponent>(item).WeaponId, out weapon);
    }

    /// <summary>
    /// The rounds in a weapon, made the first time anything asks: a gun that has never been touched
    /// comes full, with <see cref="SpareMagazines"/> beside it. Lazily rather than at spawn, so a gun
    /// placed by a map, a prefab, /give or a test is loaded the same way without each remembering to.
    /// </summary>
    public static ref AmmoComponent Ammo(World world, Entity item, WeaponDefinition weapon)
    {
        if (!world.Has<AmmoComponent>(item))
            world.Add(item, new AmmoComponent
            {
                Rounds = weapon.MagazineCapacity,
                Capacity = weapon.MagazineCapacity,
                SpareRounds = weapon.MagazineCapacity * SpareMagazines,
            });
        return ref world.Get<AmmoComponent>(item);
    }

    /// <summary>Rounds in a weapon item, or -1 for something that is not one.</summary>
    public static int RoundsIn(World world, Entity item)
        => IsWeapon(world, item, out var weapon) ? Ammo(world, item, weapon).Rounds : -1;

    /// <summary>
    /// Whoever picks a gun up first pockets the spare rounds that came with it. Returns how many.
    /// </summary>
    public static int PocketSpares(World world, Entity holder, Entity item)
    {
        if (!IsWeapon(world, item, out var weapon)) return 0;
        ref var ammo = ref Ammo(world, item, weapon);
        int spare = ammo.SpareRounds;
        if (spare <= 0) return 0;
        ammo.SpareRounds = 0;
        AddReserve(world, holder, weapon.AmmoId, spare);
        return spare;
    }

    /// <summary>Spare rounds of one kind a person carries.</summary>
    public static int Reserve(World world, Entity holder, string ammoId)
    {
        if (!world.Has<AmmoReserveComponent>(holder)) return 0;
        var rounds = world.Get<AmmoReserveComponent>(holder).Rounds;
        return rounds != null && rounds.TryGetValue(Key(ammoId), out int n) ? n : 0;
    }

    /// <summary>Adds (or, negative, takes) spare rounds of one kind. Never below none.</summary>
    public static void AddReserve(World world, Entity holder, string ammoId, int count)
    {
        if (!world.Has<AmmoReserveComponent>(holder)) world.Add(holder, new AmmoReserveComponent());
        ref var reserve = ref world.Get<AmmoReserveComponent>(holder);
        reserve.Rounds ??= new Dictionary<string, int>();
        string key = Key(ammoId);
        reserve.Rounds.TryGetValue(key, out int had);
        reserve.Rounds[key] = Math.Max(0, had + count);
    }

    /// <summary>Everything a person carries spare, in words, or "" for nothing.</summary>
    public static string ReserveWords(World world, Entity holder)
    {
        if (!world.Has<AmmoReserveComponent>(holder)) return "";
        var rounds = world.Get<AmmoReserveComponent>(holder).Rounds;
        if (rounds == null) return "";
        var parts = new List<string>();
        foreach (var ammo in Ammunition.All)
            if (rounds.TryGetValue(ammo.Id, out int n) && n > 0) parts.Add(Ammunition.Count(ammo, n));
        return parts.Count switch
        {
            0 => "",
            1 => parts[0],
            _ => string.Join(", ", parts.GetRange(0, parts.Count - 1)) + " and " + parts[^1],
        };
    }

    /// <summary>"30 rounds", "1 round", "empty"; shells for a shotgun.</summary>
    public static string RoundsWords(WeaponDefinition weapon, int rounds)
    {
        if (rounds <= 0) return "empty";
        var ammo = Ammunition.Get(weapon.AmmoId);
        string unit = ammo == null ? (rounds == 1 ? "round" : "rounds") : rounds == 1 ? ammo.UnitSingular : ammo.Unit;
        return $"{rounds} {unit}";
    }

    /// <summary>A reserve is filed under the ammunition's own id, whatever name it was asked by.</summary>
    private static string Key(string ammoId) => Ammunition.Get(ammoId)?.Id ?? ammoId;
}
