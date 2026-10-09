using System.Globalization;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Editor;

/// <summary>A map setting changed: its stored value before and after (null: not set, the map's own).</summary>
public sealed record MapSetOp(string MapId, string Path, string Label, string? Before, string? After) : EditOp(MapId)
{
    public override string What => $"set the map's {Label}";
}

/// <summary>
/// A map's own settings in the world editor (docs/WORLD_EDITOR.md section 11.7): whether it holds the sky
/// (a weather, an hour) or follows the server's, the natural ground laid where it has none, and its beacon
/// rules. Kept in the overlay's Settings and laid on the map's data when it loads (MapOverlayStore), so
/// a map file is never written.
/// </summary>
public static class MapSettings
{
    public const string Weather = "Weather", Hour = "Hour", Ground = "Ground", BeaconPrefix = "Beacon.";

    /// <summary>"server" or a weather the map holds.</summary>
    public static readonly string[] WeatherWords = { "server", "clear", "rain", "snow", "storm" };

    /// <summary>The prefabs natural ground may be, where the map has the prefab.</summary>
    public static readonly string[] GroundPrefabs = { "dirt_floor", "grass_floor", "gravel_floor", "concrete_floor", "asphalt_road", "brick_floor" };

    /// <summary>Beacon policies as said, and as MapData.BeaconPolicy keeps them.</summary>
    public static readonly (string Words, string Stored)[] Policies =
    {
        ("on unless a player turns it off", "default_on"), ("off unless a player turns it on", "default_off"),
        ("always on", "forced_on"), ("never", "forbidden"),
    };

    public static FieldDescriptor Field(string path, IReadOnlyCollection<string> grounds) => path switch
    {
        Weather => new FieldDescriptor
        {
            Path = Weather, Label = "weather", Type = FieldType.Choice, Choices = WeatherWords,
            Help = "The weather on this map: the server's, which changes by itself, or one this map always has.",
        },
        Hour => new FieldDescriptor
        {
            Path = Hour, Label = "time of day", Type = FieldType.Text,
            Help = "The time of day on this map: server for the server's clock, or an hour this map always has, 0 to 24 (14.5 or 14:30 is half past two).",
        },
        Ground => new FieldDescriptor
        {
            Path = Ground, Label = "natural ground", Type = FieldType.Choice, Choices = grounds.ToArray(),
            Help = "What is laid where the map has no ground of its own: dirt unless it says otherwise.",
        },
        _ when path.StartsWith(BeaconPrefix, StringComparison.OrdinalIgnoreCase) => new FieldDescriptor
        {
            Path = BeaconPrefix + path[BeaconPrefix.Length..].ToLowerInvariant(), Label = $"{path[BeaconPrefix.Length..].ToLowerInvariant()} beacons",
            Type = FieldType.Choice, Choices = Policies.Select(p => p.Words).ToArray(),
            Help = "Whether beacons of this kind are heard on this map: on unless a player turns them off, off unless turned on, always, or never.",
        },
        _ => throw new ArgumentException(path),
    };

    /// <summary>An hour as typed: "server", "14", "14.5" or "14:30". Null if it is none of those.</summary>
    public static bool TryHour(string typed, out float? hour)
    {
        hour = null;
        string t = typed.Trim().ToLowerInvariant();
        if (t is "server" or "the server's" or "") return true;
        if (t.Contains(':'))
        {
            var p = t.Split(':');
            if (p.Length == 2 && int.TryParse(p[0], out int h) && int.TryParse(p[1], out int m) && h is >= 0 and <= 24 && m is >= 0 and < 60 && h * 60 + m <= 24 * 60)
            { hour = h + m / 60f; return true; }
            return false;
        }
        if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f >= 0 && f <= 24) { hour = f; return true; }
        return false;
    }

    /// <summary>An hour as said: "half past two in the afternoon" is too long; "14:30".</summary>
    public static string SayHour(float hour)
    {
        int minutes = (int)MathF.Round(hour * 60f) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    public static WeatherType? WeatherOf(string? word) => word?.ToLowerInvariant() switch
    {
        "clear" => WeatherType.Clear, "rain" => WeatherType.Rain, "snow" => WeatherType.Snow, "storm" => WeatherType.Storm, _ => null,
    };

    /// <summary>A stored setting laid on a map's data: what loading the overlay and changing it live both do.</summary>
    public static void Apply(MapData map, string path, string? stored)
    {
        if (path.Equals(Weather, StringComparison.OrdinalIgnoreCase)) map.HeldWeather = WeatherOf(stored) != null ? stored!.ToLowerInvariant() : null;
        else if (path.Equals(Hour, StringComparison.OrdinalIgnoreCase)) map.HeldHour = stored != null && TryHour(stored, out var h) ? h : null;
        else if (path.Equals(Ground, StringComparison.OrdinalIgnoreCase)) map.GroundPrefab = stored;
        else if (path.StartsWith(BeaconPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string category = path[BeaconPrefix.Length..].ToLowerInvariant();
            map.BeaconPolicy ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (stored == null) map.BeaconPolicy.Remove(category);
            else map.BeaconPolicy[category] = stored;
        }
    }

    /// <summary>A setting's value as the map has it now, stored form.</summary>
    public static string Get(MapData map, string path)
    {
        if (path.Equals(Weather, StringComparison.OrdinalIgnoreCase)) return map.HeldWeather ?? "server";
        if (path.Equals(Hour, StringComparison.OrdinalIgnoreCase)) return map.HeldHour is float h ? h.ToString("R", CultureInfo.InvariantCulture) : "server";
        if (path.Equals(Ground, StringComparison.OrdinalIgnoreCase)) return map.GroundPrefab ?? MapManager.NaturalGroundPrefab;
        if (path.StartsWith(BeaconPrefix, StringComparison.OrdinalIgnoreCase))
            return map.BeaconPolicy != null && map.BeaconPolicy.TryGetValue(path[BeaconPrefix.Length..], out var p) ? p : "default_on";
        return "";
    }

    /// <summary>A stored value as said.</summary>
    public static string Say(string path, string stored)
    {
        if (path.Equals(Weather, StringComparison.OrdinalIgnoreCase)) return stored == "server" ? "the server's" : stored;
        if (path.Equals(Hour, StringComparison.OrdinalIgnoreCase)) return TryHour(stored, out var h) && h is float f ? SayHour(f) : "the server's clock";
        if (path.StartsWith(BeaconPrefix, StringComparison.OrdinalIgnoreCase))
            return Policies.FirstOrDefault(p => p.Stored == stored).Words ?? stored;
        return stored;
    }
}

public sealed partial class WorldEditor
{
    /// <summary>The ground prefabs this server has, by id.</summary>
    private List<string> GroundChoices() => MapSettings.GroundPrefabs.Where(g => _maps.Prefabs.ContainsKey(g)).ToList();

    private static IEnumerable<string> SettingPaths()
        => new[] { MapSettings.Weather, MapSettings.Hour, MapSettings.Ground }
           .Concat(Beacons.Categories.Where(c => c != Beacons.Player).Select(c => MapSettings.BeaconPrefix + c));

    /// <summary>/edit map [set FIELD VALUE]: the map's settings, said or changed.</summary>
    private void MapCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (!_maps.TryGetMapData(s.CurrentMapId, out var map)) { Say(reply, $"Map '{s.CurrentMapId}' is not loaded."); return; }
        if (args.Length == 0 || args[0].Equals("settings", StringComparison.OrdinalIgnoreCase))
        {
            Say(reply, "Map settings: " + string.Join("; ", SettingPaths().Select(p => $"{MapSettings.Field(p, GroundChoices()).Label}, {SaySetting(p, MapSettings.Get(map, p))}"))
                     + ". /edit map set weather|time|ground|beacon CATEGORY VALUE changes one.");
            return;
        }
        if (!args[0].Equals("set", StringComparison.OrdinalIgnoreCase) || args.Length < 3)
        { Say(reply, "Say /edit map settings, or /edit map set weather server|clear|rain|snow|storm, /edit map set time server|HOUR, /edit map set ground PREFAB, /edit map set beacon CATEGORY on|off|always|never."); return; }

        string field = args[1].ToLowerInvariant();
        string path; string typed;
        switch (field)
        {
            case "weather": path = MapSettings.Weather; typed = string.Join(" ", args[2..]); break;
            case "time": case "hour": path = MapSettings.Hour; typed = string.Join(" ", args[2..]); break;
            case "ground": path = MapSettings.Ground; typed = string.Join(" ", args[2..]); break;
            case "beacon": case "beacons":
                if (args.Length < 4 || !Beacons.IsCategory(args[2]) || args[2].Equals(Beacons.Player, StringComparison.OrdinalIgnoreCase))
                { Say(reply, $"Say /edit map set beacon CATEGORY on|off|always|never. Categories: {string.Join(", ", Beacons.Categories.Where(c => c != Beacons.Player))}."); return; }
                path = MapSettings.BeaconPrefix + args[2].ToLowerInvariant(); typed = string.Join(" ", args[3..]);
                break;
            default:
                // A path as the menu sends it: "Beacon.door".
                if (SettingPaths().FirstOrDefault(p => p.Equals(args[1], StringComparison.OrdinalIgnoreCase)) is { } known) { path = known; typed = string.Join(" ", args[2..]); break; }
                Say(reply, "The map's settings are weather, time, ground and beacon CATEGORY.");
                return;
        }
        if (!TryMapValue(path, typed, out string? stored, out string error)) { Say(reply, error); return; }
        string before = MapSettings.Get(map, path);
        if (before == (stored ?? DefaultOf(path))) { Say(reply, $"The {MapSettings.Field(path, GroundChoices()).Label} is already {SaySetting(path, before)}."); return; }
        var o = Overlays.Get(s.CurrentMapId);
        o.Settings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? keptBefore = o.Settings.TryGetValue(path, out var kb) ? kb : null;
        ApplyMapSetting(s.CurrentMapId, path, stored);
        string label = MapSettings.Field(path, GroundChoices()).Label;
        Push(s, new MapSetOp(s.CurrentMapId, path, label, keptBefore, stored));
        string note = path == MapSettings.Ground && !HasNaturalGround(s.CurrentMapId)
            ? " This map lays its own ground everywhere people walk, so nothing is laid; it applies if that ground is taken away and the map loads again." : "";
        Say(reply, $"This map's {label}: {SaySetting(path, MapSettings.Get(map, path))}.{note}");
        Notify(s, $"{s.Username} set the map's {label}.");
        Refresh(s, reply);
    }

    /// <summary>A map setting as said, a ground by its prefab's name ("Grass").</summary>
    private string SaySetting(string path, string stored)
        => path == MapSettings.Ground && _maps.Prefabs.TryGetValue(stored.ToLowerInvariant(), out var p) ? p.Name : MapSettings.Say(path, stored);

    private static string DefaultOf(string path)
        => path == MapSettings.Ground ? MapManager.NaturalGroundPrefab : path.StartsWith(MapSettings.BeaconPrefix) ? "default_on" : "server";

    /// <summary>A typed value of a map setting in its stored form; null stored is "the map's own" (no entry).</summary>
    private bool TryMapValue(string path, string typed, out string? stored, out string error)
    {
        stored = null; error = "";
        string t = typed.Trim().ToLowerInvariant();
        if (path == MapSettings.Weather)
        {
            if (!MapSettings.WeatherWords.Contains(t)) { error = $"The weather is one of: {string.Join(", ", MapSettings.WeatherWords)}."; return false; }
            stored = t == "server" ? null : t;
            return true;
        }
        if (path == MapSettings.Hour)
        {
            if (!MapSettings.TryHour(t, out var hour)) { error = "The time is server, or an hour from 0 to 24: 14, 14.5 or 14:30."; return false; }
            stored = hour?.ToString("R", CultureInfo.InvariantCulture);
            return true;
        }
        if (path == MapSettings.Ground)
        {
            var grounds = GroundChoices();
            var match = grounds.FirstOrDefault(g => g.Equals(t, StringComparison.OrdinalIgnoreCase) || g.Equals(t + "_floor", StringComparison.OrdinalIgnoreCase)
                                                    || (_maps.Prefabs.TryGetValue(g, out var p) && p.Name.Equals(typed.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (match == null) { error = $"The natural ground is one of: {string.Join(", ", grounds)}."; return false; }
            stored = match == MapManager.NaturalGroundPrefab ? null : match;
            return true;
        }
        // A beacon rule, by its words or its stored name or a short word.
        string word = t switch { "on" => "default_on", "off" => "default_off", "always" or "forced" => "forced_on", "never" => "forbidden", _ => t.Replace(' ', '_') };
        var policy = MapSettings.Policies.FirstOrDefault(p => p.Stored == word || p.Words.Equals(t, StringComparison.OrdinalIgnoreCase));
        if (policy.Stored == null) { error = "A beacon rule is on, off, always or never."; return false; }
        stored = policy.Stored == "default_on" ? null : policy.Stored;
        return true;
    }

    /// <summary>A map setting changed live: kept in the overlay, laid on the map's data, and heard.</summary>
    private void ApplyMapSetting(string mapId, string path, string? stored)
    {
        if (!_maps.TryGetMapData(mapId, out var map)) return;
        var o = Overlays.Get(mapId);
        o.Settings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (stored == null) o.Settings.Remove(path); else o.Settings[path] = stored;
        if (o.Settings.Count == 0) o.Settings = null;
        Overlays.Save(mapId);
        MapSettings.Apply(map, path, stored);
        if (path == MapSettings.Ground) RelayGround(mapId, stored ?? MapManager.NaturalGroundPrefab);
        if (path.StartsWith(MapSettings.BeaconPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var update = new MapSettingsUpdate
            {
                MapId = mapId,
                BeaconPolicy = map.BeaconPolicy == null ? Array.Empty<string>() : map.BeaconPolicy.Select(kv => $"{kv.Key}={kv.Value}").ToArray(),
            };
            foreach (var session in _sessions.GetSessionsInMap(mapId).ToList())
                if (!session.IsTextClient) _server.SendToSession(session, update);
        }
        // The weather and the hour are in the next state of the sky each player is sent (GetStateForMap).
    }

    /// <summary>The natural ground laid again as another prefab, where the old one was.</summary>
    private bool RelayGround(string mapId, string prefab)
    {
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return false;
        var authored = _maps.AuthoredEntities(mapId).Values.Select(e => e.Id).ToHashSet();
        Entity old = Entity.Null;
        world.Query(new QueryDescription().WithAll<IdentityComponent, Transform>(), (Entity e, ref IdentityComponent ident) =>
        {
            if (old == Entity.Null && !authored.Contains(e.Id) && ident.Name == "Ground" && MapSettings.GroundPrefabs.Contains(ident.PrefabId ?? "")) old = e;
        });
        if (old == Entity.Null) return false;   // the map lays its own ground
        var t = world.Get<Transform>(old);
        int oldId = old.Id;
        _maps.DestroyEntity(mapId, old);
        _server.BroadcastRemoval(mapId, oldId);
        var made = _maps.SpawnPrefab(mapId, prefab, t.Position, t.Rotation, t.Scale, "Ground");
        _maps.RefreshGrid(mapId);
        if (made != Entity.Null) _server.SyncAudioComponent(made.Id);
        return true;
    }

    /// <summary>Whether the map has natural ground the loader laid (it has no ground of its own under all of it).</summary>
    private bool HasNaturalGround(string mapId)
    {
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return false;
        var authored = _maps.AuthoredEntities(mapId).Values.Select(e => e.Id).ToHashSet();
        bool found = false;
        world.Query(new QueryDescription().WithAll<IdentityComponent>(), (Entity e, ref IdentityComponent ident) =>
        {
            if (!authored.Contains(e.Id) && ident.Name == "Ground" && MapSettings.GroundPrefabs.Contains(ident.PrefabId ?? "")) found = true;
        });
        return found;
    }

    private EditorMenu MapSettingsMenu(UserSession s)
    {
        var items = new List<EditorMenuItem>();
        if (_maps.TryGetMapData(s.CurrentMapId, out var map))
            foreach (var path in new[] { MapSettings.Weather, MapSettings.Hour, MapSettings.Ground })
                items.Add(Opens($"{Capital(MapSettings.Field(path, GroundChoices()).Label)}, {SaySetting(path, MapSettings.Get(map, path))}", $"mapsetting:{path}"));
        items.Add(Info("A held weather or hour is what this map's players hear; the server's sky goes on everywhere else."));
        return Menu("Map settings", items);
    }

    private EditorMenu BeaconsMenu(UserSession s)
    {
        var items = new List<EditorMenuItem>();
        if (_maps.TryGetMapData(s.CurrentMapId, out var map))
            foreach (var category in Beacons.Categories.Where(c => c != Beacons.Player))
            {
                string path = MapSettings.BeaconPrefix + category;
                items.Add(Opens($"{Capital(category)} beacons, {MapSettings.Say(path, MapSettings.Get(map, path))}", $"mapsetting:{path}"));
            }
        return Menu("Beacon rules", items);
    }

    private EditorMenu? MapSettingMenu(UserSession s, string path)
    {
        if (!_maps.TryGetMapData(s.CurrentMapId, out var map) || !SettingPaths().Contains(path, StringComparer.OrdinalIgnoreCase)) return null;
        var field = MapSettings.Field(path, GroundChoices());
        string now = MapSettings.Get(map, path);
        var items = new List<EditorMenuItem> { Info($"{Capital(field.Label)}, {SaySetting(path, now)}") };
        if (path == MapSettings.Hour)
        {
            items.Add(Act("The server's clock", "edit map set time server"));
            foreach (var h in new[] { 0, 6, 9, 12, 15, 18, 21 }) items.Add(Act($"Hold it at {h:00}:00", $"edit map set time {h}"));
            string held = now != "server" && MapSettings.TryHour(now, out var hour) && hour is float at ? MapSettings.SayHour(at) : "";
            items.Add(Typed("Hold it at an hour, typed", "/edit map set time ", "hour", "0 to 24: 14.5 or 14:30 is half past two.", held));
        }
        else if (path == MapSettings.Ground)
            foreach (var g in field.Choices) items.Add(Act(_maps.Prefabs.TryGetValue(g, out var p) ? p.Name : g, $"edit map set ground {g}"));
        else if (path == MapSettings.Weather)
            foreach (var w in MapSettings.WeatherWords) items.Add(Act(w == "server" ? "The server's weather" : $"Always {w}", $"edit map set weather {w}"));
        else
        {
            string category = path[MapSettings.BeaconPrefix.Length..];
            foreach (var (words, stored) in MapSettings.Policies)
                items.Add(Act(Capital(words), $"edit map set beacon {category} {stored}"));
        }
        items.Add(Info(field.Help));
        return Menu(Capital(field.Label), items);
    }
}
