using System;
using System.Collections.Generic;

namespace OpenFPS.Common;

/// <summary>
/// Beacons: a short blip that says "there is a door here", "there is something to pick up here".
///
/// Every beacon belongs to a CATEGORY, and whether a category is heard is decided by two people.
/// The map's author sets a policy for each one — forced on, on by default, off by default, or
/// forbidden — and within that each player switches the categories they want. A tutorial map can
/// force door beacons on; a competitive one can forbid item beacons; a player who has learned the
/// map can turn doors off.
///
/// Most beacons are not placed by anybody. A door is a door beacon because it is a door, an item
/// because it is an item, a parked car because somebody can get into it — so a map with doors in it
/// has door beacons without its author knowing the feature exists. An authored beacon (a prefab of
/// Type Beacon) says its own category, and defaults to a waypoint.
/// </summary>
public static class Beacons
{
    public const string Door = "door", Exit = "exit", Stairs = "stairs", Item = "item",
                        Vehicle = "vehicle", Waypoint = "waypoint";

    /// <summary>Every category, in the order they are read out.</summary>
    public static readonly string[] Categories = { Door, Exit, Stairs, Item, Vehicle, Waypoint };

    public enum Policy { DefaultOn, DefaultOff, ForcedOn, Forbidden }

    /// <summary>What a map says when it says nothing: every category on, and each player's to change.</summary>
    public const Policy Unset = Policy.DefaultOn;

    public static bool IsCategory(string? name)
        => name != null && Array.Exists(Categories, c => c.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A policy as a map file spells it: "default_on", "default_off", "forced_on", "forbidden".</summary>
    public static bool TryParse(string? text, out Policy policy)
    {
        policy = Unset;
        switch (text?.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_'))
        {
            case "default_on": case "on": policy = Policy.DefaultOn; return true;
            case "default_off": case "off": policy = Policy.DefaultOff; return true;
            case "forced_on": case "forced": case "always": policy = Policy.ForcedOn; return true;
            case "forbidden": case "never": policy = Policy.Forbidden; return true;
            default: return false;
        }
    }

    public static string Spell(Policy p) => p switch
    {
        Policy.DefaultOff => "default_off",
        Policy.ForcedOn => "forced_on",
        Policy.Forbidden => "forbidden",
        _ => "default_on",
    };

    /// <summary>
    /// Whether a category is heard: the map's policy, and within it the player's choice. A forced
    /// category is on whatever the player said, a forbidden one off; otherwise the player's word
    /// wins, and with no word the map's default.
    /// </summary>
    public static bool IsOn(Policy policy, bool? playerChoice) => policy switch
    {
        Policy.ForcedOn => true,
        Policy.Forbidden => false,
        Policy.DefaultOff => playerChoice ?? false,
        _ => playerChoice ?? true,
    };

    /// <summary>Whether the player is allowed to change a category at all on this map.</summary>
    public static bool PlayerMayChange(Policy policy) => policy is Policy.DefaultOn or Policy.DefaultOff;

    /// <summary>
    /// A map's policies from the form they travel in — "category=policy" strings — with anything it
    /// does not mention left at <see cref="Unset"/>.
    /// </summary>
    public static Dictionary<string, Policy> ReadPolicies(IEnumerable<string>? entries)
    {
        var map = new Dictionary<string, Policy>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Categories) map[c] = Unset;
        if (entries == null) return map;
        foreach (var entry in entries)
        {
            int eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            string cat = entry[..eq].Trim();
            if (IsCategory(cat) && TryParse(entry[(eq + 1)..], out var p)) map[cat] = p;
        }
        return map;
    }
}
