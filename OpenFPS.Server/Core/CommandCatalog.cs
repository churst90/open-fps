using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenFPS.Server.Core;

/// <summary>
/// Every command the server answers, with its other names, how to type it and what it does: what /help
/// reads out, what a mistyped command is matched against, and the one place a command's usage is
/// written down. A player without menus has only this to find out what they can do, so /help lists
/// only the commands that player may use, and a mistake is answered with the nearest real command
/// rather than a dead end.
/// </summary>
public static class CommandCatalog
{
    public sealed record Entry(string Name, string[] Aliases, string Group, string Usage, string What);

    private static Entry E(string group, string name, string usage, string what, params string[] aliases)
        => new(name, aliases, group, usage, what);

    // In the order /help reads them out.
    public static readonly string[] Groups =
        { "Talking", "Moving", "Things", "Doors and vehicles", "People", "Building", "Sound tools", "Moderation", "Administration" };

    public static readonly IReadOnlyList<Entry> All = new[]
    {
        E("Talking", "all", "/all MESSAGE", "say something to everyone on the server"),
        E("Talking", "pm", "/pm NAME MESSAGE", "a private message to one player"),
        E("Talking", "motd", "/motd", "read the message of the day"),
        E("Talking", "afk", "/afk", "mark yourself away, or back", "away"),
        E("Moving", "join", "/join [MAP]", "go to another map; on its own, list the maps", "travel"),
        E("Moving", "tp", "/tp x y z, or /tp NAME", "teleport to a place (x east, y north, z height) or to a player", "move", "goto"),
        E("Moving", "where", "/where [NAME]", "where a player is", "locate"),
        E("Moving", "scan", "/scan", "what is around you"),
        E("Moving", "room", "/room", "the room you are in and what it is made of"),
        E("Things", "take", "/take [THING]", "pick up the nearest thing, or the one named", "get", "grab", "pickup"),
        E("Things", "drop", "/drop [THING]", "put down what you hold", "putdown"),
        E("Things", "stow", "/stow [THING]", "sling what you hold onto your back", "sling"),
        E("Things", "draw", "/draw [THING]", "take something off your back into your hands", "equip", "wield", "unsling"),
        E("Things", "hands", "/hands", "what is in your hands"),
        E("Things", "inv", "/inv", "what you are carrying", "i", "inventory"),
        E("Things", "fire", "/fire", "fire the weapon you hold", "shoot"),
        E("Things", "clap", "/clap", "clap your hands"),
        E("Things", "knock", "/knock", "knock on the door in front of you"),
        E("Doors and vehicles", "open", "/open", "open the door in front of you"),
        E("Doors and vehicles", "close", "/close", "close the door in front of you", "shut"),
        E("Doors and vehicles", "doors", "/doors", "the doors near you"),
        E("Doors and vehicles", "enter", "/enter [SEAT]", "get into the vehicle beside you", "board", "getin"),
        E("Doors and vehicles", "exit", "/exit", "get out", "getout"),
        E("Doors and vehicles", "seats", "/seats", "the seats of the vehicle beside you"),
        E("Doors and vehicles", "ignition", "/ignition [on|off]", "start or stop the engine from the driver's seat", "key"),
        E("People", "friend", "/friend add NAME, /friend remove NAME", "your friends list", "unfriend"),
        E("People", "friends", "/friends", "your friends and who is online"),
        E("People", "profile", "/profile [NAME]", "a player's role, status and map", "whois"),
        E("People", "realname", "/realname [NAME]", "the name shown on your profile"),
        E("People", "team", "/team [create NAME|invite PLAYER|join NAME|leave|list [NAME]|kick PLAYER|open|close|chat MESSAGE]",
          "your team: on its own, who is in it"),
        E("Talking", "t", "/t MESSAGE", "talk to your team"),
        E("People", "perms", "/perms [NAME|all]", "what you can use; all lists every permission", "permissions"),
        E("Building", "spawn", "/spawn Box|Cylinder MATERIAL X Y Z", "make an object"),
        E("Building", "prefabs", "/prefabs", "the things that can be placed"),
        E("Building", "composites", "/composites", "the saved designs"),
        E("Building", "group", "/group NAME [RADIUS] [free]", "gather what is around you into one thing"),
        E("Building", "ungroup", "/ungroup", "take a group apart"),
        E("Building", "saveas", "/saveas ID", "save a group as a design"),
        E("Building", "place", "/place ID [YAW]", "place a saved design"),
        E("Building", "origin", "/origin", "start building where you stand"),
        E("Building", "at", "/at RIGHT UP FORWARD, or /at DIRECTION METRES", "move the building cursor"),
        E("Building", "put", "/put PREFAB [TURN] [run COUNT [DIRECTION]]", "build at the cursor"),
        E("Building", "undo", "/undo", "take back the last thing built"),
        E("Building", "addseat", "/addseat NAME [drive]", "add a seat to a group"),
        E("Building", "removeseat", "/removeseat NAME", "remove a seat"),
        E("Building", "drivable", "/drivable PRESET", "make a group a vehicle"),
        E("Building", "savemap", "/savemap", "save the map"),
        E("Building", "give", "/give [NAME] ITEM [COUNT]", "give a player an item"),
        E("Sound tools", "set_sound", "/set_sound SOUND [VOLUME]", "change the sound of the object in front of you"),
        E("Sound tools", "set_audio_mode", "/set_audio_mode MODE", "how an object plays its sounds"),
        E("Sound tools", "play_folder", "/play_folder FOLDER", "play a folder of sounds"),
        E("Sound tools", "start_state", "/start_state START LOOP", "an object's start and loop sounds"),
        E("Moderation", "announce", "/announce MESSAGE", "say something to everybody as staff"),
        E("Moderation", "setmotd", "/setmotd [TEXT]", "set or clear the message of the day"),
        E("Moderation", "bring", "/bring NAME", "bring a player to you"),
        E("Moderation", "kick", "/kick NAME [REASON]", "disconnect a player"),
        E("Moderation", "mute", "/mute NAME [MINUTES]", "stop a player chatting, 10 minutes if not said"),
        E("Moderation", "unmute", "/unmute NAME", "let a muted player chat again"),
        E("Administration", "setrole", "/setrole NAME ROLE", "give a player a role: player, moderator, dev, admin, or one you made"),
        E("Administration", "role", "/role list|create|add|remove|show|delete ...", "make roles of your own"),
        E("Administration", "grant", "/grant NAME PERMISSION", "give a player one command"),
        E("Administration", "revoke", "/revoke NAME PERMISSION", "take it back"),
        E("Administration", "sessions", "/sessions", "who is connected, from where"),
        E("Administration", "user", "/user NAME", "an account's details", "account"),
        E("Administration", "throttled", "/throttled", "addresses being held back", "ratelimit"),
        E("Administration", "unlock", "/unlock NAME|ADDRESS", "lift a login lock"),
        E("Talking", "help", "/help [COMMAND|settings]", "this list, or how to use one command", "commands", "?"),
    };

    private static readonly Dictionary<string, Entry> ByName = BuildIndex();

    private static Dictionary<string, Entry> BuildIndex()
    {
        var d = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in All)
        {
            d[e.Name] = e;
            foreach (var a in e.Aliases) d[a] = e;
        }
        return d;
    }

    public static bool TryFind(string name, out Entry entry) => ByName.TryGetValue(name.TrimStart('/'), out entry!);

    /// <summary>The permission a command needs: its main name through Permissions (tp for move).</summary>
    private static bool MayUse(UserSession s, Entry e) => s.Can(Permissions.Canonical(e.Name));

    /// <summary>/help: the commands this player may use, by group, one line each group.</summary>
    public static string Help(UserSession s)
    {
        var lines = new List<string>();
        foreach (var g in Groups)
        {
            var names = All.Where(e => e.Group == g && MayUse(s, e)).Select(e => "/" + e.Name).ToList();
            if (names.Count > 0) lines.Add($"{g}: {string.Join(", ", names)}.");
        }
        return string.Join(" ", lines) + " /help and a command says how to use it. /help settings lists your own sound settings.";
    }

    /// <summary>/help NAME: how to type it and what it does, and whether this player may.</summary>
    public static string HelpFor(UserSession s, string name)
    {
        if (!TryFind(name, out var e)) return Unknown(s, name);
        string also = e.Aliases.Length > 0 ? $" Also {string.Join(", ", e.Aliases.Select(a => "/" + a))}." : "";
        string may = MayUse(s, e) ? "" : " You do not have permission to use it.";
        return $"{e.Usage}: {e.What}.{also}{may}";
    }

    /// <summary>A command nobody has: the nearest one this player may use, if any is near.</summary>
    public static string Unknown(UserSession s, string typed)
    {
        typed = typed.TrimStart('/').ToLowerInvariant();
        var candidates = All.Where(e => MayUse(s, e)).SelectMany(e => e.Aliases.Prepend(e.Name).Select(n => (Name: n, Entry: e)));
        var best = candidates.Select(c => (c.Name, Distance: Distance(typed, c.Name)))
                             .Where(c => c.Distance <= Math.Max(1, c.Name.Length / 3) || (typed.Length >= 3 && c.Name.StartsWith(typed)))
                             .OrderBy(c => c.Distance).ThenBy(c => c.Name.Length).Select(c => c.Name).Distinct().Take(3).ToList();
        return best.Count == 0
            ? $"There is no command {typed}. /help lists the ones you can use."
            : $"There is no command {typed}. Did you mean {string.Join(" or ", best.Select(b => "/" + b))}?";
    }

    /// <summary>Edit distance: letters added, removed, changed or swapped.</summary>
    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}
