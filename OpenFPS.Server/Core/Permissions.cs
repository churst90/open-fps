using System;
using System.Collections.Generic;
using System.Linq;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Core;

/// <summary>
/// Who may run what. A permission is the name of a command (its main name, not an alias), or one of
/// the few powers that are part of a command rather than a command of their own (<see cref="FireAny"/>,
/// <see cref="JoinPrivate"/> and the rest below). A role is a set of them; an account can also be given
/// single ones on top of its role (/grant), which is how a player is trusted with one or two commands
/// and no more.
///
/// A permission has a scope. The building verbs, spawning, moving yourself by coordinates, saving a map
/// and the sound tools are allowed to EVERYBODY on a map they own (<see cref="OnOwnMap"/>); holding the
/// permission is what allows them on any map. Owning a map is a scope, not a role.
///
/// The roles, and why (docs/PLAN_2026-10-05.md section 1 is the agreed table, docs/SERVER_SECURITY.md
/// the reference):
/// - Player: the game, and building on maps of their own.
/// - Moderator: looks after people, never the world. Announcements, where somebody is, bringing them,
///   kicking and muting, joining private maps to answer a report.
/// - Dev: builds the world and tests it, on any map. Firing any weapon, giving ordinary items, joining
///   private maps, and granting a player what the developer can do themselves. No power over people.
/// - Admin: everything, and alone changes roles, makes custom roles, grants anything to anybody, gives
///   premium items, moves other players, and sees and changes accounts and addresses.
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
    /// <summary>Giving premium items: the teleporter and vehicles.</summary>
    public const string GivePremium = "give-premium";
    /// <summary>/tp without a teleporter.</summary>
    public const string TeleportFree = "tp-free";
    /// <summary>Granting and revoking any permission for anybody. Without it /grant and /revoke reach
    /// only players, and only permissions the granter holds.</summary>
    public const string GrantAny = "grant-any";
    /// <summary>/perms NAME: reading somebody else's permissions.</summary>
    public const string PermsAny = "perms-any";
    /// <summary>Kicked or muted only by somebody who has it too.</summary>
    public const string Protected = "protected";
    /// <summary>/map public, private, invite and uninvite on a map that is not yours.</summary>
    public const string MapsAny = "maps-any";
    /// <summary>Holding and firing the admin gun, and setting it: /calibre, /admingun. The admin's alone.</summary>
    public const string AdminGun = "admin-gun";

    /// <summary>The world editor (F12, /edit): on any map with it, on your own map without it, and on a
    /// map whose owner made you one of its editors (<see cref="ForMapEditors"/>). docs/WORLD_EDITOR.md.</summary>
    public const string Edit = "edit";
    /// <summary>Changing a model in the library (a machine, a fountain), which changes it on every map.</summary>
    public const string EditModels = "edit-models";

    /// <summary>The permissions that are powers within a command rather than commands of their own.</summary>
    public static readonly IReadOnlyList<string> Powers = new[]
    {
        FireAny, JoinPrivate, EditAny, MovePlayer, GivePremium, TeleportFree, GrantAny, PermsAny, Protected, MapsAny, AdminGun,
        EditModels,
    };

    /// <summary>
    /// Whether a permission is also held on a map whose owner made you one of its editors
    /// (/map editor add NAME). Only the world editor: an invited editor may change the map with it and
    /// do nothing else an owner may (no /spawn, no /savemap).
    /// </summary>
    public static bool ForMapEditors(string permission) => permission == Edit;

    private static readonly UserRole[] Mod = { UserRole.Moderator }, Dev = { UserRole.Dev },
        ModDev = { UserRole.Moderator, UserRole.Dev }, AdminOnly = Array.Empty<UserRole>();

    /// <summary>Every gated permission, the roles below Admin that have it on any map, and whether
    /// everybody has it on a map they own. Admin has them all.</summary>
    private static readonly Dictionary<string, (UserRole[] Roles, string What, bool OwnMap)> Table = new()
    {
        ["announce"] = (Mod, "say something to everybody as staff", false),
        ["setmotd"] = (Mod, "set the message of the day", false),
        ["where"] = (Mod, "find where a player is", false),
        ["bring"] = (Mod, "bring a player to you", false),
        ["kick"] = (Mod, "disconnect a player", false),
        ["mute"] = (Mod, "stop a player chatting for a while", false),
        ["unmute"] = (Mod, "let a muted player chat again", false),
        [JoinPrivate] = (ModDev, "go to somebody else's private map", false),
        ["give"] = (Dev, "give a player an ordinary item", false),
        [GivePremium] = (AdminOnly, "give premium items: the teleporter and vehicles", false),
        [FireAny] = (Dev, "fire any weapon without holding it", false),
        ["move"] = (Dev, "move yourself by coordinates, on any map", true),
        ["spawn"] = (Dev, "make an object, a walker, a vehicle or a train, on any map", true),
        ["set_sound"] = (Dev, "change an object's sound, on any map", true),
        ["set_audio_mode"] = (Dev, "change how an object plays its sounds, on any map", true),
        ["play_folder"] = (Dev, "play a folder of sounds, on any map", true),
        ["start_state"] = (Dev, "set an object's start and stop sounds, on any map", true),
        ["group"] = (Dev, "gather objects into one, on any map", true),
        ["ungroup"] = (Dev, "take a group apart, on any map", true),
        ["saveas"] = (Dev, "save a group as a design, on any map", true),
        ["place"] = (Dev, "place a saved design, on any map", true),
        ["origin"] = (Dev, "set the building cursor, on any map", true),
        ["at"] = (Dev, "move the building cursor, on any map", true),
        ["put"] = (Dev, "build at the cursor, on any map", true),
        ["undo"] = (Dev, "undo the last thing built, on any map", true),
        ["addseat"] = (Dev, "add a seat, on any map", true),
        ["removeseat"] = (Dev, "remove a seat, on any map", true),
        ["drivable"] = (Dev, "make a group drivable, on any map", true),
        ["savemap"] = (Dev, "save the map, on any map", true),
        [Edit] = (Dev, "edit the world with the world editor, F12, on any map", true),
        [EditModels] = (Dev, "change a model in the library, which changes it on every map", false),
        ["weather"] = (Dev, "set the weather and the wind for the whole server", false),
        [EditAny] = (Dev, "change things other people built", false),
        ["grant"] = (Dev, "give a player a single permission you have yourself", false),
        ["revoke"] = (Dev, "take back a single permission you have yourself", false),
        [PermsAny] = (Dev, "read somebody else's permissions", false),
        [GrantAny] = (AdminOnly, "grant and revoke any permission, for anybody", false),
        [TeleportFree] = (AdminOnly, "use /tp without a teleporter", false),
        [MovePlayer] = (AdminOnly, "move another player to a place or to another player", false),
        [AdminGun] = (AdminOnly, "hold, fire and set the admin gun", false),
        [Protected] = (AdminOnly, "cannot be kicked or muted by somebody without this too", false),
        [MapsAny] = (AdminOnly, "make any map public or private, and invite people to it", false),
        ["sessions"] = (AdminOnly, "list connections and addresses", false),
        ["user"] = (AdminOnly, "read an account", false),
        ["throttled"] = (AdminOnly, "list addresses being held back", false),
        ["unlock"] = (AdminOnly, "unlock an account", false),
        ["setrole"] = (AdminOnly, "change a player's role", false),
        ["role"] = (AdminOnly, "make and change custom roles", false),
    };

    /// <summary>The main name of a command typed under another one.</summary>
    public static string Canonical(string command) => command switch
    {
        "locate" => "where",
        "goto" => "tp",
        "teleport" => "tp",
        "account" => "user",
        "ratelimit" => "throttled",
        "calibre" or "caliber" or "admingun" => AdminGun,
        _ => command,
    };

    public static bool IsGated(string permission) => Table.ContainsKey(permission);

    public static IEnumerable<string> All => Table.Keys;

    public static string Describe(string permission) => Table.TryGetValue(permission, out var t) ? t.What : "";

    /// <summary>Whether everybody may use this on a map they own: the scope half of a permission.</summary>
    public static bool OnOwnMap(string permission) => Table.TryGetValue(permission, out var t) && t.OwnMap;

    /// <summary>What a custom role or a /grant may carry: any permission but the ones that hand out
    /// permissions and roles. Those stay with administrators.</summary>
    public static bool Grantable(string permission)
        => IsGated(permission) && permission is not ("grant" or "revoke" or "setrole" or "role" or GrantAny);

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
