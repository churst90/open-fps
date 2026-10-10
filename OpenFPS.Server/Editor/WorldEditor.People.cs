using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;

namespace OpenFPS.Server.Editor;

/// <summary>A person put on a map, changed or taken off: as they were and as they are (null: not there).</summary>
public sealed record PersonOp(string MapId, string Name, CharacterData? Before, CharacterData? After) : EditOp(MapId)
{
    public override string What => Before == null ? $"put {Name} on the map" : After == null ? $"took {Name} off the map" : $"changed where {Name} goes";
}

/// <summary>
/// People (docs/WORLD_EDITOR.md section 18): characters with lives of their own (CharacterSystem), put
/// on a map with a name and a voice, their day's places chosen from the places the map has (its bus
/// stops, front entrances, lobbies and squares, as HauntFinder finds them); and how many people walk
/// the map's pavements (VehicleSystem walkers), as a map setting.
/// </summary>
public sealed partial class WorldEditor
{
    /// <summary>The map setting that says how many people walk its pavements: people per 100 metres.</summary>
    public const string WalkersSetting = "Walkers";
    public const float MaxWalkersPer100 = 20f;
    /// <summary>The most people the editor sets walking on one map.</summary>
    public const int MaxWalkers = 500;
    /// <summary>A pavement shorter than this is not a walk, metres (tools/gen_city.py's WALK_MIN).</summary>
    private const float WalkMin = 25f;

    /// <summary>The kinds of life a character may lead; CharacterSystem has one so far.</summary>
    public static readonly string[] PersonKinds = { "homeless" };

    private static readonly JsonSerializerOptions Same = new() { WriteIndented = false };
    private static bool SamePerson(CharacterData? a, CharacterData? b)
        => (a == null && b == null) || (a != null && b != null && JsonSerializer.Serialize(a, Same) == JsonSerializer.Serialize(b, Same));

    private static CharacterData CopyPerson(CharacterData c) => new()
    {
        Name = c.Name, Voice = c.Voice, Kind = c.Kind, Description = c.Description, Places = c.Places?.ToList(),
    };

    /// <summary>The people the editor put on a map, in order.</summary>
    private List<CharacterData> PeopleOf(string mapId) => Overlays.Get(mapId).People ?? new List<CharacterData>();

    /// <summary>The voices a person may speak in: those that recorded a character's lines first.</summary>
    private static List<string> PersonVoices()
        => Speech.Voices.OrderBy(v => PedestrianSpeech.IsCharacterVoice(v) ? 0 : 1).ThenBy(v => v, StringComparer.Ordinal).ToList();

    // ── The places a map has ────────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, (int Things, List<string> Names)> _places = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The places on a map a character's day can take them, by name, as CharacterSystem finds them;
    /// found again when the map's things change.</summary>
    internal List<string> PlacesOf(string mapId)
    {
        if (!_maps.TryGetMap(mapId, out var world, out _, out var grid, out _) || !_maps.TryGetMapData(mapId, out var data)) return new List<string>();
        int things = _maps.AuthoredEntities(mapId).Count;
        if (_places.TryGetValue(mapId, out var known) && known.Things == things) return known.Names;
        var paths = Pavements.Build(Pavements.StripsOf(world));
        var names = HauntFinder.Find(world, grid, data, paths, Pavements.SolidsOf(world)).Select(h => h.Name).Distinct().ToList();
        _places[mapId] = (things, names);
        return names;
    }

    // ── Commands ────────────────────────────────────────────────────────────────────────────────

    /// <summary>/edit person add NAME [voice VOICE] | choose NAME | place add|drop PLACE | voice VOICE | remove NAME; /edit people.</summary>
    private void PersonCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        bool dialog = args.Length > 0 && args[^1].Equals("dialog", StringComparison.OrdinalIgnoreCase);
        if (dialog) args = args[..^1];
        var hand = HandOf(s);
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        string rest = string.Join(" ", args.Skip(1)).Trim();
        string mapId = s.CurrentMapId;
        switch (verb)
        {
            case "add":
            {
                int at = Array.FindIndex(args, 1, a => a.Equals("voice", StringComparison.OrdinalIgnoreCase));
                string name = string.Join(" ", at < 0 ? args[1..] : args[1..at]).Trim();
                string? voice = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
                AddPerson(s, name, voice, reply);
                return;
            }
            case "choose":
                if (Person(mapId, rest) is not { } chosen) { Say(reply, $"Nobody called {rest} was put on this map with the editor. /edit people lists them."); return; }
                hand.Person = chosen.Name;
                if (!dialog) Say(reply, $"{chosen.Name}: {SayPerson(mapId, chosen)}.");
                Refresh(s, reply);
                return;
            case "place":
            {
                var words = args.Skip(1).ToArray();
                if (words.Length < 2 || words[0].ToLowerInvariant() is not ("add" or "drop" or "remove"))
                { Say(reply, "Say /edit person place add PLACE, or /edit person place drop PLACE, for the person chosen with /edit person choose NAME."); return; }
                PersonPlace(s, words[0].Equals("add", StringComparison.OrdinalIgnoreCase), string.Join(" ", words[1..]).Trim(), reply);
                return;
            }
            case "voice":
            {
                if (hand.Person == null || Person(mapId, hand.Person) is not { } who) { Say(reply, "Choose somebody first: /edit person choose NAME."); return; }
                if (!PersonVoices().Contains(rest)) { Say(reply, $"There is no voice called {rest}. Voices: {string.Join(", ", PersonVoices())}."); return; }
                var after = CopyPerson(who);
                after.Voice = rest;
                ChangePerson(s, who, after, $"{who.Name} speaks in the voice {rest} now.", reply);
                return;
            }
            case "remove":
            {
                if (Person(mapId, rest) is not { } gone) { Say(reply, $"Nobody called {rest} was put on this map with the editor. /edit people lists them."); return; }
                var op = new PersonOp(mapId, gone.Name, CopyPerson(gone), null);
                ApplyPerson(mapId, op.Before, null);
                Push(s, op);
                if (hand.Person?.Equals(gone.Name, StringComparison.OrdinalIgnoreCase) == true) hand.Person = null;
                Say(reply, $"{gone.Name} is off the map. Undo puts them back.");
                Notify(s, $"{s.Username} took {gone.Name} off the map.");
                Refresh(s, reply);
                return;
            }
            default:
                Say(reply, "Say /edit person add NAME [voice VOICE], /edit person choose NAME, /edit person place add|drop PLACE, "
                         + "/edit person voice VOICE, /edit person remove NAME, or /edit people. /edit walkers NUMBER sets how many walk the pavements.");
                return;
        }
    }

    private CharacterData? Person(string mapId, string name)
        => name.Length == 0 ? null : PeopleOf(mapId).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
           ?? PeopleOf(mapId).FirstOrDefault(p => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>"homeless, voice alex, goes to: the bus stop, Main Street; outside Tower 1 front entrance" or "any place the map has".</summary>
    private static string SayPerson(string mapId, CharacterData c)
        => $"{c.Kind}, voice {c.Voice}, goes to " + (c.Places is { Count: > 0 } p ? string.Join("; ", p) : "any place the map has");

    private void AddPerson(UserSession s, string name, string? voice, Action<IMessage> reply)
    {
        string mapId = s.CurrentMapId;
        if (name.Length == 0 || name.Length > 40) { Say(reply, "Say /edit person add NAME, and voice VOICE if you like. A name is 1 to 40 letters."); return; }
        if (!_maps.TryGetMapData(mapId, out var map)) return;
        if ((map.Characters ?? new()).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } there)
        { Say(reply, $"There is somebody called {there.Name} on this map already."); return; }
        var voices = PersonVoices();
        voice ??= voices.FirstOrDefault() ?? "";
        if (voices.Count > 0 && !voices.Contains(voice)) { Say(reply, $"There is no voice called {voice}. Voices: {string.Join(", ", voices)}."); return; }
        var person = new CharacterData { Name = name, Voice = voice, Kind = PersonKinds[0] };
        var op = new PersonOp(mapId, name, null, CopyPerson(person));
        ApplyPerson(mapId, null, op.After);
        Push(s, op);
        HandOf(s).Person = name;
        int places = PlacesOf(mapId).Count;
        Say(reply, $"{name} lives on this map now, {SayPerson(mapId, person)}."
                 + (places == 0 ? " The map has no bus stops, front entrances, lobbies or squares yet, so they stay away until it has." : $" The map has {Plural(places, "place")} for their day; /edit person place add PLACE keeps them to some."));
        Notify(s, $"{s.Username} put {name} on the map.");
        Refresh(s, reply);
    }

    private void PersonPlace(UserSession s, bool add, string place, Action<IMessage> reply)
    {
        string mapId = s.CurrentMapId;
        var hand = HandOf(s);
        if (hand.Person == null || Person(mapId, hand.Person) is not { } who) { Say(reply, "Choose somebody first: /edit person choose NAME."); return; }
        var places = PlacesOf(mapId);
        string? match = places.FirstOrDefault(p => p.Equals(place, StringComparison.OrdinalIgnoreCase))
                        ?? places.FirstOrDefault(p => p.StartsWith(place, StringComparison.OrdinalIgnoreCase))
                        ?? (who.Places ?? new()).FirstOrDefault(p => p.Equals(place, StringComparison.OrdinalIgnoreCase));
        if (match == null) { Say(reply, $"This map has no place called {place}. /edit people lists them."); return; }
        var after = CopyPerson(who);
        after.Places ??= new List<string>();
        bool has = after.Places.Any(p => p.Equals(match, StringComparison.OrdinalIgnoreCase));
        if (add == has) { Say(reply, add ? $"{who.Name} goes to {match} already." : $"{who.Name} does not go to {match}."); return; }
        if (add) after.Places.Add(match); else after.Places.RemoveAll(p => p.Equals(match, StringComparison.OrdinalIgnoreCase));
        if (after.Places.Count == 0) after.Places = null;
        ChangePerson(s, who, after,
                     add ? $"{who.Name} goes to {match}: {Plural(after.Places!.Count, "place")} in their day."
                         : $"{who.Name} no longer goes to {match}: {(after.Places == null ? "any place the map has" : Plural(after.Places.Count, "place"))}.", reply);
    }

    private void ChangePerson(UserSession s, CharacterData before, CharacterData after, string said, Action<IMessage> reply)
    {
        var op = new PersonOp(s.CurrentMapId, before.Name, CopyPerson(before), CopyPerson(after));
        ApplyPerson(s.CurrentMapId, op.Before, op.After);
        Push(s, op);
        Say(reply, said);
        Notify(s, $"{s.Username} changed where {before.Name} goes.");
        Refresh(s, reply);
    }

    /// <summary>A person put on, changed or taken off, live: the overlay, the map's data, and CharacterSystem.</summary>
    private void ApplyPerson(string mapId, CharacterData? before, CharacterData? after)
    {
        string name = (after ?? before)!.Name;
        var o = Overlays.Get(mapId);
        o.People ??= new List<CharacterData>();
        int at = o.People.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) o.People.RemoveAt(at);
        if (after != null) o.People.Insert(at >= 0 ? at : o.People.Count, CopyPerson(after));
        if (o.People.Count == 0) o.People = null;
        Overlays.Save(mapId);
        if (_maps.TryGetMapData(mapId, out var map))
        {
            map.Characters?.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (after != null) (map.Characters ??= new List<CharacterData>()).Add(CopyPerson(after));
        }
        if (before != null && after != null) _server.Characters.Change(mapId, CopyPerson(after));
        else if (after != null) _server.Characters.AddLive(_maps, mapId, CopyPerson(after));
        else if (_server.Characters.RemoveLive(_maps, mapId, name) is int gone) _server.BroadcastRemoval(mapId, gone);
    }

    private bool ReversePerson(PersonOp op, bool forward, out string why)
    {
        why = "";
        var now = PeopleOf(op.MapId).FirstOrDefault(p => p.Name.Equals(op.Name, StringComparison.OrdinalIgnoreCase));
        var expect = forward ? op.Before : op.After;
        if (!SamePerson(now, expect)) { why = $"{op.Name} has been changed since."; return false; }
        ApplyPerson(op.MapId, now, forward ? op.After : op.Before);
        return true;
    }

    // ── Walkers ─────────────────────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, List<Entity>> _walkers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>People per 100 metres of pavement the map's setting asks for; 0 when it asks for none.</summary>
    private float WalkerDensity(string mapId)
        => Overlays.Get(mapId).Settings is { } set && set.TryGetValue(WalkersSetting, out var v)
           && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float d) ? d : 0f;

    /// <summary>The map's pavements, each with the height of its top: where people walk.</summary>
    private static List<(Vector3 A, Vector3 B, string Name)> PavementLines(World world)
    {
        var lines = new List<(Vector3, Vector3, string)>();
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithNone<Velocity>(),
            (Entity e, ref Transform t, ref ColliderComponent c) =>
            {
                string name = NameOf(world, e);
                if (!Pavements.IsPavement(name) || c.Shape != ColliderShape.Box || c.Size.Y > 0.5f) return;
                var x = Vector3.Transform(Vector3.UnitX, t.Rotation); x.Y = 0f;
                var z = Vector3.Transform(Vector3.UnitZ, t.Rotation); z.Y = 0f;
                if (x.LengthSquared() < 1e-6f || z.LengthSquared() < 1e-6f) return;
                bool alongX = c.Size.X >= c.Size.Z;
                var axis = Vector3.Normalize(alongX ? x : z);
                float half = (alongX ? c.Size.X : c.Size.Z) * 0.5f - 1f;
                if (half * 2f < WalkMin) return;
                float top = t.Position.Y + c.Size.Y * 0.5f + 0.03f;
                var centre = t.Position with { Y = top };
                lines.Add((centre - axis * half, centre + axis * half, name));
            });
        return lines;
    }

    /// <summary>
    /// The people the map's setting asks for, walking its pavements back and forth, made again: those made
    /// before are taken away first. Spread evenly along every pavement, each at their own pace, as
    /// tools/gen_city.py spreads the city's own. Returns how many walk.
    /// </summary>
    internal int MakeWalkers(string mapId)
    {
        if (_walkers.Remove(mapId, out var old) && _maps.TryGetMap(mapId, out var was, out _, out _, out _))
            foreach (var e in old)
            {
                if (!was.IsAlive(e)) continue;
                int id = e.Id;
                _server.Vehicles.Forget(mapId, id);
                _maps.DestroyEntity(mapId, e);
                _server.BroadcastRemoval(mapId, id);
            }
        float density = WalkerDensity(mapId);
        if (density <= 0f || !_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return 0;
        var lines = PavementLines(world);
        float total = lines.Sum(l => Flat(l.B - l.A));
        int count = Math.Min(MaxWalkers, (int)MathF.Round(total / 100f * density));
        if (count == 0) return 0;
        var rng = new Random(CharacterSystem.Seed(20261010, mapId, "walkers", 0, 0));
        var made = new List<Entity>();
        for (int k = 0; k < count; k++)
        {
            // Evenly along all the pavement there is, one after another.
            float at = (k + 0.5f) * total / count;
            int i = 0;
            while (i < lines.Count - 1 && at > Flat(lines[i].B - lines[i].A)) { at -= Flat(lines[i].B - lines[i].A); i++; }
            var (a, b, name) = lines[i];
            if (k % 2 == 1) (a, b) = (b, a);
            float length = Flat(b - a);
            float kmh = 4.2f + (float)rng.NextDouble() * 1.2f;
            var vd = new VehicleData
            {
                Name = $"Pedestrian, {name}", Preset = "walker", RoadStart = a, RoadEnd = b, SpeedsKmh = new[] { kmh, kmh * 0.92f },
                AccelerationMps2 = 0.8f, BrakingMps2 = 1f, WaitSeconds = 3f + (float)rng.NextDouble() * 6f,
                // Started part way along their walk, as somebody already out would be.
                StartDelaySeconds = (k % 2 == 1 ? length - at : at) / (kmh / 3.6f) * (float)rng.NextDouble(),
            };
            var e = _server.Vehicles.SpawnOne(_maps, Composites, mapId, vd);
            if (e != Entity.Null) made.Add(e);
        }
        _walkers[mapId] = made;
        return made.Count;
    }

    /// <summary>Every map's walkers as its setting asks, once at start after the map's own vehicles (Program).</summary>
    public int WalkKeptWalkers()
    {
        int n = 0;
        foreach (string mapId in _maps.LoadedMapIds.ToList())
            if (WalkerDensity(mapId) > 0f) n += MakeWalkers(mapId);
        if (n > 0) Serilog.Log.Information("WorldEditor: {Count} people walk the pavements as the maps' settings ask.", n);
        return n;
    }

    /// <summary>"1 person", "2 people".</summary>
    private static string PeopleSaid(int n) => n == 1 ? "1 person" : $"{n} people";

    /// <summary>How many people the setting has walking here now.</summary>
    internal int WalkersOn(string mapId) => _walkers.TryGetValue(mapId, out var l) ? l.Count : 0;

    /// <summary>"2.3 kilometres of pavement" or "no pavement".</summary>
    private string PavementSaid(string mapId)
    {
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return "no pavement";
        float total = PavementLines(world).Sum(l => Flat(l.B - l.A));
        return total <= 0f ? "no pavement" : total >= 1000f ? $"{FieldDescriptor.Format(MathF.Round(total / 1000f, 1))} kilometres of pavement" : $"{Metres(MathF.Round(total))} of pavement";
    }

    /// <summary>/edit walkers NUMBER: how many people walk the map's pavements, per 100 metres, besides the map's own.</summary>
    private void WalkersCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        string mapId = s.CurrentMapId;
        if (args.Length == 0)
        {
            Say(reply, $"{FieldDescriptor.Format(WalkerDensity(mapId))} people per 100 metres of pavement walk this map besides its own: {PeopleSaid(WalkersOn(mapId))} on {PavementSaid(mapId)}. "
                     + "/edit walkers NUMBER changes it, 0 to 20; the city has about 3.");
            return;
        }
        if (!TryNumber(args[0], out float density) || density < 0f || density > MaxWalkersPer100)
        { Say(reply, $"Say /edit walkers NUMBER: people per 100 metres of pavement, 0 to {FieldDescriptor.Format(MaxWalkersPer100)}."); return; }
        string? stored = density > 0f ? FieldDescriptor.Format(density) : null;
        var o = Overlays.Get(mapId);
        string? before = o.Settings != null && o.Settings.TryGetValue(WalkersSetting, out var b) ? b : null;
        if (before == stored) { Say(reply, $"It is {FieldDescriptor.Format(density)} per 100 metres already."); return; }
        ApplyMapSetting(mapId, WalkersSetting, stored);
        Push(s, new MapSetOp(mapId, WalkersSetting, "walkers", before, stored));
        Say(reply, density == 0f ? "Nobody walks the pavements for this setting now; the map's own people still do."
                 : $"{PeopleSaid(WalkersOn(mapId))} {(WalkersOn(mapId) == 1 ? "walks" : "walk")} this map's {PavementSaid(mapId)} now, {FieldDescriptor.Format(density)} per 100 metres, besides its own."
                   + (WalkersOn(mapId) == 0 ? " There is no pavement long enough to walk: lay a path, or name a floor a pavement." : ""));
        Notify(s, $"{s.Username} set how many people walk this map.");
        Refresh(s, reply);
    }

    // ── Menus and the dialog ────────────────────────────────────────────────────────────────────

    /// <summary>/edit people: the walkers, the people on the map and the places they can go.</summary>
    private void SayPeople(UserSession s, Action<IMessage> reply)
    {
        if (!s.IsTextClient) { SendMenu(s, "people", reply, refresh: false); return; }
        string mapId = s.CurrentMapId;
        var people = _maps.TryGetMapData(mapId, out var map) ? map.Characters ?? new() : new List<CharacterData>();
        var places = PlacesOf(mapId);
        Say(reply, $"Walkers: {FieldDescriptor.Format(WalkerDensity(mapId))} per 100 metres, {PeopleSaid(WalkersOn(mapId))} on {PavementSaid(mapId)}. "
                 + (people.Count == 0 ? "Nobody lives on this map. " : "People: " + string.Join("; ", people.Select(c => $"{c.Name}, {SayPerson(mapId, c)}")) + ". ")
                 + (places.Count == 0 ? "The map has no places for a person's day yet." : "Places: " + string.Join("; ", places) + ".")
                 + " /edit person add NAME puts somebody on it.");
    }

    private EditorMenu PeopleMenu(UserSession s)
    {
        string mapId = s.CurrentMapId;
        var items = new List<EditorMenuItem>
        {
            TypedNumber($"Walkers, {FieldDescriptor.Format(WalkerDensity(mapId))} per 100 metres of pavement, typed", "/edit walkers ", "walkers per 100 metres of pavement", "",
                        0, MaxWalkersPer100, $"How many people walk this map's {PavementSaid(mapId)}, besides its own. The city has about 3.", FieldDescriptor.Format(WalkerDensity(mapId))),
            Typed("Put a person on the map, typed: their name", "/edit person add ", "name of the person",
                  $"They speak in the voice {PersonVoices().FirstOrDefault() ?? "none"}; /edit person voice VOICE changes it."),
        };
        if (_maps.TryGetMapData(mapId, out var map))
            foreach (var c in map.Characters ?? new())
            {
                bool mine = PeopleOf(mapId).Any(p => p.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
                items.Add(mine ? Opens($"{c.Name}, {SayPerson(mapId, c)}", $"person:{c.Name}") : Info($"{c.Name}, {SayPerson(mapId, c)}, from the map file"));
            }
        return Menu("People", items);
    }

    private EditorMenu? PersonMenu(UserSession s, string name)
    {
        string mapId = s.CurrentMapId;
        if (Person(mapId, name) is not { } c) return null;
        HandOf(s).Person = c.Name;
        var items = new List<EditorMenuItem> { Info($"{c.Name}, {SayPerson(mapId, c)}") };
        foreach (var place in PlacesOf(mapId))
        {
            bool has = c.Places?.Contains(place, StringComparer.OrdinalIgnoreCase) == true;
            items.Add(Act(has ? $"Goes to {place}: stop" : $"Does not go to {place}: start", $"edit person place {(has ? "drop" : "add")} {place}"));
        }
        foreach (var voice in PersonVoices()) items.Add(Act($"Voice {voice}{(voice == c.Voice ? ", now" : "")}", $"edit person voice {voice}"));
        items.Add(Act($"Take {c.Name} off the map", $"edit person remove {c.Name}", stay: false));
        return Menu(c.Name, items);
    }

    /// <summary>The World tab's people: the walkers' box, the people on the map, the chosen one's places, the voices.</summary>
    private IEnumerable<EditorMenuItem> DialogPeople(UserSession s)
    {
        string mapId = s.CurrentMapId;
        var walkers = TypedNumber("Walkers per 100 metres of pavement", "/edit walkers ", "walkers per 100 metres of pavement", "", 0, MaxWalkersPer100,
                                  $"Now {PeopleSaid(WalkersOn(mapId))} on {PavementSaid(mapId)}, besides the map's own. The city has about 3.",
                                  FieldDescriptor.Format(WalkerDensity(mapId)));
        walkers.Section = "world.walkers";
        yield return walkers;
        var hand = HandOf(s);
        if (hand.Person != null && Person(mapId, hand.Person) == null) hand.Person = null;
        var chosen = hand.Person == null ? null : Person(mapId, hand.Person);
        yield return Line("world.personinfo", chosen == null ? "" : $"{chosen.Name}, {SayPerson(mapId, chosen)}", chosen?.Name ?? "", prompt: chosen?.Voice ?? "");
        if (_maps.TryGetMapData(mapId, out var map))
            foreach (var c in map.Characters ?? new())
            {
                bool mine = PeopleOf(mapId).Any(p => p.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
                yield return Line("world.person", $"{c.Name}, {SayPerson(mapId, c)}{(mine ? "" : ", from the map file")}", mine ? c.Name : "");
            }
        if (chosen != null)
            foreach (var place in PlacesOf(mapId))
                yield return new EditorMenuItem
                {
                    Section = "world.place", Kind = EditorItemKind.Info, Label = place, Value = place,
                    Checked = chosen.Places?.Contains(place, StringComparer.OrdinalIgnoreCase) == true,
                };
        foreach (var voice in PersonVoices()) yield return Line("world.voice", voice, voice);
    }
}
