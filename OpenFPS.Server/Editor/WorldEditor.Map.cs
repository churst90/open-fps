using System.Globalization;
using System.Numerics;
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

    /// <summary>
    /// The map's size from its south-west corner at the ground (MinBound, which never moves), stored as
    /// "EAST NORTH HEIGHT" in metres, the order players type it (/setmapsize).
    /// </summary>
    public const string Size = "Size";

    /// <summary>A size as stored: "200 300 40".</summary>
    public static string FormatSize(float east, float north, float height)
        => string.Join(" ", new[] { east, north, height }.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)));

    /// <summary>A stored size: metres east, north and up. False for anything else.</summary>
    public static bool TryParseSize(string? stored, out float east, out float north, out float height)
    {
        east = north = height = 0f;
        var parts = (stored ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out east) && float.IsFinite(east) && east > 0f
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out north) && float.IsFinite(north) && north > 0f
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out height) && float.IsFinite(height) && height > 0f;
    }

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
        else if (path.Equals(Size, StringComparison.OrdinalIgnoreCase))
        {
            // No stored size is the map file's own, which is what the map already has.
            if (!TryParseSize(stored, out float east, out float north, out float height)) return;
            map.Size = new System.Numerics.Vector3(east, height, north);
            map.MaxBound = map.MinBound + map.Size;
        }
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
        if (path.Equals(Size, StringComparison.OrdinalIgnoreCase))
        {
            var size = map.MaxBound - map.MinBound;
            return FormatSize(size.X, size.Z, size.Y);
        }
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
        if (path == MapSettings.Size) Resized(mapId, map);
        if (path.StartsWith(MapSettings.BeaconPrefix, StringComparison.OrdinalIgnoreCase) || path == MapSettings.Size)
        {
            var update = new MapSettingsUpdate
            {
                MapId = mapId,
                BeaconPolicy = map.BeaconPolicy == null ? Array.Empty<string>() : map.BeaconPolicy.Select(kv => $"{kv.Key}={kv.Value}").ToArray(),
                HasPlayArea = true, PlayMin = map.WalkMin, PlayMax = map.WalkMax,
            };
            foreach (var session in _sessions.GetSessionsInMap(mapId).ToList())
                if (!session.IsTextClient) _server.SendToSession(session, update);
        }
        // The weather and the hour are in the next state of the sky each player is sent (GetStateForMap).
    }

    /// <summary>
    /// What loading did with the bounds, done again for new ones: the zone, the natural ground (laid
    /// again under the new bounds, or taken up where the map's own ground now covers them) and how far
    /// the map is heard. The acoustic grid's corner is MinBound, which a resize never moves.
    /// </summary>
    private void Resized(string mapId, MapData map)
    {
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return;
        world.Query(new QueryDescription().WithAll<ZoneComponent>(), (ref ZoneComponent zone) =>
        {
            zone.Size = map.Size;
            zone.MinBound = map.MinBound;
            zone.MaxBound = map.MaxBound;
        });
        if (NaturalGround(mapId) is { } old && old != Entity.Null)
        {
            int oldId = old.Id;
            _maps.DestroyEntity(mapId, old);
            _server.BroadcastRemoval(mapId, oldId);
        }
        if (!MapManager.GroundCovers(world, map.WalkMin, map.WalkMax))
        {
            var (at, scale) = MapManager.NaturalGroundPose(map);
            var made = _maps.SpawnPrefab(mapId, _maps.NaturalGroundOf(map), at, Quaternion.Identity, scale, "Ground");
            if (made != Entity.Null) _server.SyncAudioComponent(made.Id);
        }
        _maps.RefreshGrid(mapId);
        _maps.RefreshEarshot(mapId);
    }

    /// <summary>The natural ground the loader laid on a map, or null if the map lays its own.</summary>
    private Entity? NaturalGround(string mapId)
    {
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return null;
        var authored = _maps.AuthoredEntities(mapId).Values.Select(e => e.Id).ToHashSet();
        Entity? found = null;
        world.Query(new QueryDescription().WithAll<IdentityComponent, Transform>(), (Entity e, ref IdentityComponent ident) =>
        {
            if (found == null && !authored.Contains(e.Id) && ident.Name == "Ground" && MapSettings.GroundPrefabs.Contains(ident.PrefabId ?? "")) found = e;
        });
        return found;
    }

    // Generous for a map built in the game; the city is 1,800 by 2,500.
    private const float MinSide = 10f, MaxSide = 4000f, MinHeight = 5f, MaxHeight = 1000f;

    /// <summary>
    /// /setmapsize [EAST NORTH HEIGHT] [force]: the map's size in metres, from its south-west corner at the
    /// ground, which stays where it is, so nothing on the map moves. Its owner's, or anybody's with
    /// maps-any; never a map a generator writes. Kept in the overlay like the map's other settings, with undo.
    /// </summary>
    public void SetMapSize(UserSession s, string[] args, Action<IMessage> reply)
    {
        string mapId = s.CurrentMapId;
        if (!_maps.TryGetMapData(mapId, out var map)) { Say(reply, $"Map '{mapId}' is not loaded."); return; }
        var words = args.Where(a => !a.Equals("force", StringComparison.OrdinalIgnoreCase)).ToArray();
        bool force = words.Length < args.Length;
        if (words.Length == 0) { Say(reply, SaySize(map) + " /setmapsize EAST NORTH HEIGHT changes it."); return; }

        if (!_maps.IsOwner(mapId, s.Username) && !s.Can(Permissions.MapsAny))
        { Say(reply, $"{map.DisplayName} is not yours: only its owner, or somebody with maps-any, can change its size."); return; }
        if (_maps.IsShipped(mapId))
        { Say(reply, $"{map.DisplayName} is made by a program in tools, so its size is set there. /setmapsize changes maps made with /map new."); return; }

        var n = new float[3];
        if (words.Length != 3 || Enumerable.Range(0, 3).Any(i => !float.TryParse(words[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]) || !float.IsFinite(n[i])))
        { Say(reply, "Say /setmapsize EAST NORTH HEIGHT in metres, such as /setmapsize 200 300 40, and force to leave things outside it."); return; }
        float east = n[0], north = n[1], height = n[2];
        if (east < MinSide || east > MaxSide || north < MinSide || north > MaxSide || height < MinHeight || height > MaxHeight)
        { Say(reply, $"East and north are {FieldDescriptor.Format(MinSide)} to {FieldDescriptor.Format(MaxSide)} metres, and the height {FieldDescriptor.Format(MinHeight)} to {FieldDescriptor.Format(MaxHeight)}."); return; }

        string after = MapSettings.FormatSize(east, north, height);
        string now = MapSettings.Get(map, MapSettings.Size);
        if (after == now) { Say(reply, "The map is already that size."); return; }

        var newMax = map.MinBound + new Vector3(east, height, north);
        var left = LeftOutside(mapId, map, newMax, out bool spawnOut);
        if ((left.Count > 0 || spawnOut) && !force)
        {
            Say(reply, $"That would leave {Outside(left, spawnOut)} outside the map. "
                     + $"/setmapsize {after} force does it anyway; nothing is moved or deleted, and nobody can walk out to what is outside.");
            return;
        }

        var settings = Overlays.Get(mapId).Settings;
        // Never null before, so undo puts back this size and not "whatever the file says".
        string before = settings != null && settings.TryGetValue(MapSettings.Size, out var kept) ? kept : now;
        ApplyMapSetting(mapId, MapSettings.Size, after);
        Push(s, new MapSetOp(mapId, MapSettings.Size, "size", before, after));

        int pulledIn = 0;
        _maps.TryGetMap(mapId, out var world, out _, out _, out _);
        foreach (var other in _sessions.GetSessionsInMap(mapId).ToList())
        {
            if (world == null || other.Entity == Entity.Null || !world.IsAlive(other.Entity) || !world.Has<Transform>(other.Entity)) continue;
            var p = world.Get<Transform>(other.Entity).Position;
            if (p.X >= map.WalkMin.X && p.X <= map.WalkMax.X && p.Z >= map.WalkMin.Z && p.Z <= map.WalkMax.Z) continue;
            pulledIn++;
            if (other != s) _server.SendToSession(other, new TextEvent { Text = $"{s.Username} made this map smaller. You are past its new edge; your next step brings you inside it." });
        }

        Say(reply, $"{SaySize(map)}"
                 + (left.Count > 0 || spawnOut ? $" Left outside: {Outside(left, spawnOut)}." : "")
                 + (pulledIn > 0 ? $" {Plural(pulledIn, "player")} past the new edge {(pulledIn == 1 ? "is" : "are")} brought inside at the next step." : ""));
        Notify(s, $"{s.Username} set the map's size to {FieldDescriptor.Format(east)} by {FieldDescriptor.Format(north)} metres, {FieldDescriptor.Format(height)} high.");
        Refresh(s, reply);
    }

    /// <summary>"This map is 200 metres east, 300 north and 40 high, from its south-west corner at ..."</summary>
    private static string SaySize(MapData map)
    {
        var size = map.MaxBound - map.MinBound;
        string play = map.PlayMin != null || map.PlayMax != null
            ? $" People can go {FieldDescriptor.Format(MathF.Round(map.WalkMax.X - map.WalkMin.X))} by {FieldDescriptor.Format(MathF.Round(map.WalkMax.Z - map.WalkMin.Z))} metres of it."
            : "";
        return $"{map.DisplayName} is {FieldDescriptor.Format(MathF.Round(size.X, 2))} metres east, {FieldDescriptor.Format(MathF.Round(size.Z, 2))} north "
             + $"and {FieldDescriptor.Format(MathF.Round(size.Y, 2))} high, from its south-west corner at {PlayerCoordinates.Format(map.MinBound)}.{play}";
    }

    /// <summary>The map's things inside its bounds now that would be outside new ones, and whether the
    /// spawn point would be. Below the ground does not count: the corner at the ground does not move.</summary>
    private List<string> LeftOutside(string mapId, MapData map, Vector3 newMax, out bool spawnOut)
    {
        static bool Within(Vector3 p, Vector3 lo, Vector3 hi) => p.X >= lo.X && p.X <= hi.X && p.Z >= lo.Z && p.Z <= hi.Z && p.Y <= hi.Y;
        spawnOut = Within(map.SpawnPoint.Position, map.MinBound, map.MaxBound) && !Within(map.SpawnPoint.Position, map.MinBound, newMax);
        var names = new List<string>();
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return names;
        foreach (var e in _maps.AuthoredEntities(mapId).Values.Where(e => world.IsAlive(e) && world.Has<Transform>(e)))
        {
            var p = world.Get<Transform>(e).Position;
            if (Within(p, map.MinBound, map.MaxBound) && !Within(p, map.MinBound, newMax)) names.Add(NameOf(world, e));
        }
        return names;
    }

    /// <summary>"3 things: a wall, a box and a lamp, and the spawn point", named up to three.</summary>
    private static string Outside(List<string> names, bool spawn)
    {
        string things = names.Count == 0 ? ""
            : $"{Plural(names.Count, "thing")}: {string.Join(", ", names.Take(3))}{(names.Count > 3 ? $" and {names.Count - 3} more" : "")}";
        return spawn ? (things.Length > 0 ? things + ", and the spawn point" : "the spawn point") : things;
    }

    /// <summary>The natural ground laid again as another prefab, where the old one was.</summary>
    private bool RelayGround(string mapId, string prefab)
    {
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return false;
        if (NaturalGround(mapId) is not { } old) return false;   // the map lays its own ground
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
