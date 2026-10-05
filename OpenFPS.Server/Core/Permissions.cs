using System;
using System.Collections.Generic;
using System.Linq;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Core;

/// <summary>
/// Who may run what. A permission is the name of a command (its main name, not an alias), or one of
/// the few powers that are part of a command rather than a command of their own (<see cref="FireAny"/>,
/// <see cref="JoinPrivate"/>). A role is a set of them; an account can also be given single ones on
/// top of its role (/grant), which is how a player is trusted with one or two commands and no more.
///
/// The roles, and why (docs/SERVER_SECURITY.md has the table):
/// - Player: the game. Nothing here.
/// - Moderator: looks after people, never the world. Announcements, where somebody is, going to them
///   and bringing them, kicking and muting. A moderator can stop a player spoiling the game for
///   others without being able to change the city or see anybody's address.
/// - Dev: builds the world and tests it. Placing, grouping, saving maps, seats, the sound tools,
///   teleporting, firing any weapon, giving items. No power over other people: a builder is not a
///   moderator.
/// - Admin: everything, and alone changes roles, grants single commands, and sees and changes
///   accounts and addresses.
/// </summary>
public static class Permissions
{
    /// <summary>Firing a weapon you are not holding: /fire akm with empty hands.</summary>
    public const string FireAny = "fire-any";
    /// <summary>Going to somebody else's private map.</summary>
    public const string JoinPrivate = "join-private";
    /// <summary>Changing what somebody else built: ungrouping it, saving it, its seats.</summary>
    public const string EditAny = "edit-any";
    /// <summary>Moving another player: /move sean 10 20 0, /move sean to cody.</summary>
    public const string MovePlayer = "move-player";
    /// <summary>Holding and firing the admin gun, and setting it: /calibre, /admingun. The admin's alone.</summary>
    public const string AdminGun = "admin-gun";

    private static readonly UserRole[] Mod = { UserRole.Moderator }, Dev = { UserRole.Dev },
        ModDev = { UserRole.Moderator, UserRole.Dev }, AdminOnly = Array.Empty<UserRole>();

    /// <summary>Every gated permission and the roles below Admin that have it. Admin has them all.</summary>
    private static readonly Dictionary<string, (UserRole[] Roles, string What)> Table = new()
    {
        ["announce"] = (ModDev, "say something to everybody as staff"),
        ["setmotd"] = (ModDev, "set the message of the day"),
        ["where"] = (ModDev, "find where a player is"),
        ["tp"] = (ModDev, "teleport yourself, to a place or to a player"),
        ["bring"] = (Mod, "bring a player to you"),
        ["kick"] = (Mod, "disconnect a player"),
        ["mute"] = (Mod, "stop a player chatting for a while"),
        ["unmute"] = (Mod, "let a muted player chat again"),
        [JoinPrivate] = (ModDev, "go to somebody else's private map"),
        ["give"] = (Dev, "give a player an item"),
        [FireAny] = (Dev, "fire any weapon without holding it"),
        ["spawn"] = (Dev, "make an object"),
        ["set_sound"] = (Dev, "change an object's sound"),
        ["set_audio_mode"] = (Dev, "change how an object plays its sounds"),
        ["play_folder"] = (Dev, "play a folder of sounds"),
        ["start_state"] = (Dev, "set an object's start and stop sounds"),
        ["group"] = (Dev, "gather objects into one"),
        ["ungroup"] = (Dev, "take a group apart"),
        ["saveas"] = (Dev, "save a group as a design"),
        ["place"] = (Dev, "place a saved design"),
        ["origin"] = (Dev, "set the building cursor"),
        ["at"] = (Dev, "move the building cursor"),
        ["put"] = (Dev, "build at the cursor"),
        ["undo"] = (Dev, "undo the last thing built"),
        ["addseat"] = (Dev, "add a seat"),
        ["removeseat"] = (Dev, "remove a seat"),
        ["drivable"] = (Dev, "make a group drivable"),
        ["savemap"] = (Dev, "save the map"),
        [EditAny] = (Dev, "change things other people built"),
        [MovePlayer] = (AdminOnly, "move another player to a place or to another player"),
        [AdminGun] = (AdminOnly, "hold, fire and set the admin gun"),
        ["sessions"] = (AdminOnly, "list connections and addresses"),
        ["user"] = (AdminOnly, "read an account"),
        ["throttled"] = (AdminOnly, "list addresses being held back"),
        ["unlock"] = (AdminOnly, "unlock an account"),
        ["setrole"] = (AdminOnly, "change a player's role"),
        ["grant"] = (AdminOnly, "give a player a single permission"),
        ["revoke"] = (AdminOnly, "take a single permission back"),
        ["role"] = (AdminOnly, "make and change custom roles"),
    };

    /// <summary>The main name of a command typed under another one.</summary>
    public static string Canonical(string command) => command switch
    {
        "locate" => "where",
        "move" => "tp",
        "goto" => "tp",
        "account" => "user",
        "ratelimit" => "throttled",
        "calibre" or "caliber" or "admingun" => AdminGun,
        _ => command,
    };

    public static bool IsGated(string permission) => Table.ContainsKey(permission);

    public static IEnumerable<string> All => Table.Keys;

    public static string Describe(string permission) => Table.TryGetValue(permission, out var t) ? t.What : "";

    /// <summary>Whether a role has a permission without any grant.</summary>
    public static bool RoleHas(UserRole role, string permission)
        => role == UserRole.Admin || (Table.TryGetValue(permission, out var t) && t.Roles.Contains(role));

    /// <summary>Whether this account may: by its role, or by a permission granted to it.</summary>
    public static bool Has(UserRole role, IReadOnlyCollection<string> grants, string permission)
        => !IsGated(permission) || RoleHas(role, permission) || grants.Contains(permission);

    /// <summary>Grants as stored: comma separated, lower case, only known permissions, each once.</summary>
    public static HashSet<string> Parse(string? stored)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(stored)) return set;
        foreach (var p in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string c = Canonical(p.ToLowerInvariant());
            if (IsGated(c)) set.Add(c);
        }
        return set;
    }

    public static string Format(IEnumerable<string> grants) => string.Join(",", grants.OrderBy(g => g, StringComparer.Ordinal));
}
