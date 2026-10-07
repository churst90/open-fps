using System.Collections.Generic;

namespace OpenFPS.Common;

/// <summary>
/// Beacons: a short blip that says there is a door here, something to pick up here. Each is in a
/// category; the map sets a policy per category (forced on, on or off by default, forbidden) and each
/// player switches within it. Most are not placed: a door is a door beacon by being a door, a parked car
/// by being something to get into. An authored beacon (a prefab of Type Beacon) names its category,
/// a waypoint by default.
/// </summary>
public static class Beacons
{
    public const string Door = "door", Exit = "exit", Stairs = "stairs", Item = "item",
                        Vehicle = "vehicle", Waypoint = "waypoint", Player = "player";

    /// <summary>Every category, in the order they are read out. A player is a beacon by being a player:
    /// every other person on the map, and your own team's in a tone of their own.</summary>
    public static readonly string[] Categories = { Door, Exit, Stairs, Item, Vehicle, Waypoint, Player };

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

    /// <summary>Whether a category is heard: forced and forbidden are the map's; otherwise the player's
    /// choice, or the map's default.</summary>
    public static bool IsOn(Policy policy, bool? playerChoice) => policy switch
    {
        Policy.ForcedOn => true,
        Policy.Forbidden => false,
        Policy.DefaultOff => playerChoice ?? false,
        _ => playerChoice ?? true,
    };

    /// <summary>Whether the player is allowed to change a category at all on this map.</summary>
    public static bool PlayerMayChange(Policy policy) => policy is Policy.DefaultOn or Policy.DefaultOff;

    /// <summary>A map's policies from "category=policy" strings; anything not mentioned is
    /// <see cref="Unset"/>.</summary>
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
