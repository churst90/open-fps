namespace OpenFPS.Server.Core;

/// <summary>
/// Every command the server answers, its other names, how to type it and what it does: what /help reads
/// out and what a mistyped command is matched against. /help lists only what this player may use, and a
/// mistake is answered with the nearest real command rather than a dead end.
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
        E("Moving", "join", "/join [MAP], /join world [PLACE]", "go to another map, or arrive at a place in the world; on its own, list them", "travel"),
        E("Moving", "tp", "/tp PLACE, /tp PLAYER, /tp MAP, or /tp x y z", "with a teleporter in your inventory: go to a named place, a player, a map, or a point (x east, y north, z height)", "goto", "teleport"),
        E("Moving", "map", "/map [new NAME|public|private|invite PLAYER|uninvite PLAYER|editor add|remove PLAYER]", "maps of your own: make one, open it to everybody or close it, invite people, ask people to edit it; on its own, the map you are on"),
        E("Moving", "maps", "/maps [mine]", "the maps you can go to, or the ones you own"),
        E("Moving", "where", "/where [NAME]", "where a player is", "locate"),
        E("Moving", "scan", "/scan", "what is around you"),
        E("Moving", "room", "/room", "the room you are in and what it is made of"),
        E("Moving", "detail", "/detail [low|medium|high]", "how far round you a large map is loaded: everything to 150, 300 or 500 metres, buildings and roads to 500, 800 or 1200; medium by default"),
        E("Things", "take", "/take [THING]", "pick up the nearest thing, or the one named", "get", "grab", "pickup"),
        E("Things", "drop", "/drop [THING]", "put down what you hold", "putdown"),
        E("Things", "stow", "/stow [THING]", "sling what you hold onto your back", "sling"),
        E("Things", "draw", "/draw [THING]", "take something off your back into your hands", "equip", "wield", "unsling"),
        E("Things", "hands", "/hands", "what is in your hands"),
        E("Things", "inv", "/inv", "what you are carrying", "i", "inventory"),
        E("Things", "hand", "/hand [THING]", "give what you hold to the person beside you, if they will take it", "offer"),
        E("Things", "fire", "/fire", "fire the weapon you hold", "shoot"),
        E("Things", "aimassist", "/aimassist [on|off]", "aim assistance from the hip: a shot near somebody in plain view is turned onto them; on by default"),
        E("Things", "reload", "/reload", "reload the gun in your hands from your spare ammunition"),
        E("Things", "ammo", "/ammo", "what is in your gun and the spare ammunition you carry"),
        E("Things", "selector", "/selector [next|back|safe|semi|auto]", "move the fire selector on the gun in your hands; X and Shift X. On the admin gun, its mode"),
        E("Things", "cease", "/cease", "let go of the trigger: a gun on automatic stops firing. Letting go of Enter sends it"),
        E("Things", "calibre", "/calibre NAME|next|back", "which weapon's rounds and report the admin gun fires; Y and Shift Y", "caliber"),
        E("Things", "admingun", "/admingun [report N]", "how the admin gun is set, or which of its report sounds it is heard with"),
        E("Things", "scope", "/scope [tone on|off]", "raise or lower the scope on the rifle in your hands; numpad star. With tone, the guidance tone on or off"),
        E("Things", "zoom", "/zoom in|out|POWER", "the scope's magnification: numpad plus and minus"),
        E("Things", "range", "/range", "the rangefinder: the distance to what the crosshair is on; numpad period"),
        E("Things", "zero", "/zero [METRES]", "set the scope's elevation turret for a distance; numpad 1 and 3 click it"),
        E("Things", "clap", "/clap", "clap your hands"),
        E("Things", "knock", "/knock", "knock on the door in front of you"),
        E("Doors and vehicles", "open", "/open", "open the door in front of you"),
        E("Doors and vehicles", "close", "/close", "close the door in front of you", "shut"),
        E("Doors and vehicles", "doors", "/doors", "the doors near you"),
        E("Doors and vehicles", "enter", "/enter [SEAT]", "get into the vehicle beside you", "board", "getin"),
        E("Doors and vehicles", "exit", "/exit", "get out", "getout"),
        E("Doors and vehicles", "seats", "/seats", "the seats of the vehicle beside you"),
        E("Doors and vehicles", "window", "/window [down|up|half]", "roll the side windows the other way, or down, up or half way; R in a vehicle", "windows"),
        E("Doors and vehicles", "ignition", "/ignition [on|off]", "start or stop the engine from the driver's seat", "key"),
        E("Doors and vehicles", "horn", "/horn", "a short blast on the horn from the driver's seat; hold H to sound it for as long as you like"),
        E("Doors and vehicles", "siren", "/siren [on|off|wail|yelp|phaser|hilo|next]", "the siren, on a vehicle that has one: U switches it, Shift+U changes the tone"),
        E("People", "friend", "/friend add NAME, /friend remove NAME", "your friends list", "unfriend"),
        E("People", "friends", "/friends", "your friends and who is online"),
        E("People", "profile", "/profile [NAME]", "a player's role, status and map", "whois"),
        E("People", "realname", "/realname [NAME]", "the name shown on your profile"),
        E("People", "team", "/team [create NAME|invite PLAYER|join NAME|leave|list [NAME]|kick PLAYER|open|close|chat MESSAGE]",
          "your team: on its own, who is in it"),
        E("Talking", "t", "/t MESSAGE", "talk to your team"),
        E("People", "perms", "/perms [NAME|all]", "what you can use; all lists every permission", "permissions"),
        E("Building", "spawn", "/spawn walker [NAME], /spawn vehicle PRESET, /spawn train PRESET|out [NAME], /spawn fire PRESET|lightning|water|out, or /spawn Box|Cylinder MATERIAL X Y Z", "make somebody walking, a parked vehicle, a train on a track already there (or take one off), a fire to test (lightning strikes what is ahead, water douses it), or an object"),
        E("Building", "move", "/move x y z, /move NAME, /move NAME x y z, or /move NAME to OTHER", "move yourself by coordinates; moving somebody else needs move-player"),
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
        E("Building", "setmapsize", "/setmapsize [EAST NORTH HEIGHT] [force]", "on a map you own: its size in metres, from its south-west corner at the ground; on its own, the size now. Refused if things would be left outside, unless you add force"),
        E("Building", "edit", "/edit [select nearest|NAME|#ID, select add NAME, select group, held move|nudge|turn ..., move EAST NORTH UP, nudge DIRECTION, turn DEGREES, duplicate, row COUNT, delete, set FIELD VALUE, placed [WORDS], remove #ID [#ID ...], remove held, goto #ID, changed [WORDS], putback #ID, place PREFAB|vehicle:PRESET [at cursor], find WORDS, preview PREFAB, again, group NAME, building NAME, spawn here, map set SETTING VALUE, map save NAME, map versions, map restore NUMBER, map bake [now], route start|new road|path|railway [FIELD VALUE ...], route point|points|station|crossing|finish|cancel|remove ..., routes, person add NAME, person place add|drop PLACE, person remove NAME, people, walkers NUMBER, model set|add|remove|versions|pin|use|new|copy|replace|retire KIND ID ..., undo, redo]",
          "the world editor, also F12: select things and move, turn, copy, delete and change them, place, search and preview prefabs, park vehicles, list everything placed on the map and remove it or go to it from anywhere, list what was changed from the map file and put it back as the map has it, save versions of the map and restore them, write the edits into your own map's file, lay roads, paths and railways by walking them or typing their points, put people on the map and set how many walk its pavements, group things or save them as a building, set the map's weather, time, ground and beacon rules, change, pin, copy and replace models; kept for the next start"),
        E("Building", "give", "/give [NAME] ITEM [COUNT], /give [NAME] [ammo] KIND [COUNT], or /give [NAME] vehicle PRESET", "give a player an item, spare rounds (9mm, .45, .357, 5.56, 7.62x39, .308, 12 gauge), or a vehicle parked beside them"),
        E("Sound tools", "set_sound", "/set_sound SOUND [VOLUME]", "change the sound of the object in front of you"),
        E("Sound tools", "set_audio_mode", "/set_audio_mode MODE", "how an object plays its sounds"),
        E("Sound tools", "play_folder", "/play_folder FOLDER", "play a folder of sounds"),
        E("Sound tools", "start_state", "/start_state START LOOP", "an object's start and loop sounds"),
        E("Sound tools", "weather", "/weather [clear|rain|snow|storm|auto], or /weather wind SPEED [DIRECTION] [steady|gusty|very gusty]",
          "the weather and the wind now, or set them for the whole server for testing; what you set holds until /weather auto"),
        E("Moderation", "announce", "/announce MESSAGE", "say something to everybody as staff"),
        E("Moderation", "setmotd", "/setmotd [TEXT]", "set or clear the message of the day"),
        E("Moderation", "bring", "/bring NAME", "bring a player to you"),
        E("Moderation", "kick", "/kick NAME [REASON]", "disconnect a player"),
        E("Moderation", "mute", "/mute NAME [MINUTES]", "stop a player chatting, 10 minutes if not said"),
        E("Moderation", "unmute", "/unmute NAME", "let a muted player chat again"),
        E("Moderation", "ban", "/ban NAME [DURATION] [REASON]", "ban a player's account and remove them if they are on; a duration is 30m, 2h, 7d or 4w, and without one the ban lasts until lifted. Nobody bans an owner, and only somebody whose role is above theirs"),
        E("Moderation", "unban", "/unban NAME", "lift a ban"),
        E("Moderation", "bans", "/bans", "who is banned, by whom, when, until when and why"),
        E("Administration", "setrole", "/setrole NAME ROLE", "give a player a role: player, moderator, dev, admin, owner, or one you made. Only an owner makes or changes an owner, and the last owner stays one"),
        E("Administration", "role", "/role list|create|add|remove|show|delete ...", "make roles of your own; the built-in roles cannot be changed"),
        E("Administration", "grant", "/grant NAME PERMISSION", "give a player one permission; a developer can grant only what they have, and only to players"),
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

    /// <summary>Whether this player may use a command somewhere: by permission, or on a map of their own
    /// (everybody may make one, so the building verbs are everybody's there).</summary>
    private static bool MayUse(UserSession s, Entry e)
        => s.Can(Permissions.Canonical(e.Name)) || Permissions.OnOwnMap(Permissions.Canonical(e.Name));

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
        string perm = Permissions.Canonical(e.Name);
        string may = s.Can(perm) ? "" : Permissions.OnOwnMap(perm) ? " You can use it on maps you own (/map new NAME makes one)."
                   : " You do not have permission to use it.";
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
