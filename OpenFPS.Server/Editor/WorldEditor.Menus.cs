using System.Globalization;
using System.Text.Json.Nodes;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

using EntityData = OpenFPS.Server.Repositories.EntityData;

namespace OpenFPS.Server.Editor;

/// <summary>
/// The world editor's menus, built here and only shown by the client (EditorMenu): the client knows no
/// kind of thing and no field, it shows lists. A menu's path is what asks for it again ("edit menu PATH").
/// </summary>
public sealed partial class WorldEditor
{
    private static EditorMenuItem Info(string label) => new() { Label = label, Kind = EditorItemKind.Info };
    private static EditorMenuItem Opens(string label, string path) => new() { Label = label, Kind = EditorItemKind.Menu, Command = path };
    private static EditorMenuItem Act(string label, string command, bool stay = true) => new() { Label = label, Kind = EditorItemKind.Action, Command = command, Stay = stay };
    private static EditorMenuItem Typed(string label, string prefill) => new() { Label = label, Kind = EditorItemKind.Input, Command = prefill };

    /// <summary>Sends the menu at a path, or says there is none.</summary>
    internal void SendMenu(UserSession s, string path, Action<IMessage> reply, bool refresh)
    {
        var menu = BuildMenu(s, path);
        if (menu == null)
        {
            if (!refresh) Say(reply, $"There is no editor menu called {path}.");
            return;
        }
        if (!refresh) HandOf(s).LastMenu = path;
        menu.Refresh = refresh;
        reply(menu);
    }

    /// <summary>The menu at a path, made from what is there now; null for a path that names nothing.</summary>
    public EditorMenu? BuildMenu(UserSession s, string path)
    {
        var parts = path.Split(':', 4);
        string Part(int i) => parts.Length > i ? parts[i] : "";
        return parts[0] switch
        {
            "root" => Root(s),
            "map" => MapMenu(s),
            "mapsettings" => MapSettingsMenu(s),
            "mapsetting" when parts.Length > 1 => MapSettingMenu(s, parts[1]),
            "beacons" => BeaconsMenu(s),
            "select" => SelectMenu(s),
            "select.nearest" => Listing(s, "Nearest things", Nearest(s, 8)),
            "select.within" when parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
                => Listing(s, $"Within {Metres(r)}", Within(s, r)),
            "held" => HeldMenu(s),
            "places" => PlacesMenu(s),
            "doors" => DoorsMenu(s),
            "selected" => SelectedMenu(s),
            "nudge" => NudgeMenu(s),
            "turn" => TurnMenu(s),
            "delete" => DeleteMenu(s),
            "settings" => SettingsMenu(s),
            "setting" when parts.Length > 1 => SettingMenu(s, parts[1]),
            "place" => PlaceMenu(s),
            "place.cat" when parts.Length > 1 => CategoryMenu(s, parts[1]),
            "place.mode" => PlaceModeMenu(s),
            "find" when parts.Length > 1 => FindMenu(s, string.Join(":", parts[1..])),
            "rows" => RowsMenu(s),
            "library" => LibraryMenu(),
            "kind" when parts.Length > 1 => KindMenu(parts[1], retired: false),
            "retired" when parts.Length > 1 => KindMenu(parts[1], retired: true),
            "templates" when parts.Length > 1 => TemplatesMenu(parts[1]),
            "model" when parts.Length > 2 => ModelMenu(s, parts[1], parts[2], Part(3)),
            "mfield" when parts.Length > 3 => ModelFieldMenu(s, parts[1], parts[2], parts[3]),
            "versions" when parts.Length > 2 => VersionsMenu(s, parts[1], parts[2]),
            "version" when parts.Length > 3 => VersionMenu(s, parts[1], parts[2], parts[3]),
            "where" when parts.Length > 2 => WhereMenu(parts[1], parts[2]),
            "replace" when parts.Length > 2 => ReplaceMenu(s, parts[1], parts[2]),
            "replacewith" when parts.Length > 3 => ReplaceWithMenu(s, parts[1], parts[2], parts[3]),
            "test" => TestMenu(),
            _ => null,
        } is { } menu ? Stamp(menu, path) : null;
    }

    private static EditorMenu Stamp(EditorMenu m, string path) { m.Path = path; return m; }

    private static EditorMenu Menu(string title, IEnumerable<EditorMenuItem> items) => new() { Title = title, Items = items.ToArray() };

    private EditorMenu Root(UserSession s)
    {
        var items = new List<EditorMenuItem>
        {
            Opens("Map", "map"),
            Opens("Place", "place"),
            Opens("Select", "select"),
        };
        if (HandOf(s).Selected is int id && _maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _)
            && _maps.AuthoredEntities(s.CurrentMapId).TryGetValue(id, out var e) && Editable(world, e))
            items.Add(Opens($"Selected: {NameOf(world, e)}", "selected"));
        if (HandOf(s).Held.Count > 0) items.Add(Opens($"Held, {Plural(HandOf(s).Held.Count, "thing")}", "held"));
        items.Add(Opens("Places and rooms", "places"));
        items.Add(Opens("Library", "library"));
        items.Add(Opens("Test tools", "test"));
        items.Add(NextUndo(s) is { } u ? Act($"Undo: {u}", "edit undo") : Info("Nothing to undo"));
        items.Add(NextRedo(s) is { } r ? Act($"Redo: {r}", "edit redo") : Info("Nothing to redo"));
        string map = _maps.DisplayName(s.CurrentMapId);
        return Menu($"World editor, {map}", items);
    }

    private EditorMenu MapMenu(UserSession s)
    {
        var items = new List<EditorMenuItem>();
        if (_maps.TryGetMapData(s.CurrentMapId, out var d))
        {
            var size = d.MaxBound - d.MinBound;
            string owner = string.IsNullOrWhiteSpace(d.OwnerId) ? "the server's" : d.OwnerId.Equals(s.Username, StringComparison.OrdinalIgnoreCase) ? "yours" : $"{d.OwnerId}'s";
            items.Add(Info($"{d.DisplayName}, {owner}, {(d.IsPublic ? "public" : "private")}"));
            items.Add(Info($"Size {FieldDescriptor.Format(MathF.Round(size.X))} by {FieldDescriptor.Format(MathF.Round(size.Z))} metres, from {PlayerCoordinates.Format(d.MinBound)} to {PlayerCoordinates.Format(d.MaxBound)}"));
            items.Add(Info(d.TileMetres > 0 ? $"Streamed in tiles of {FieldDescriptor.Format(d.TileMetres)} metres" : "Sent whole, not in tiles"));
            items.Add(Info($"{_maps.AuthoredEntities(s.CurrentMapId).Count} things"));
            items.Add(Info($"Spawn point at {PlayerCoordinates.Format(d.SpawnPoint.Position)}, facing {CompassOf(YawOf(d.SpawnPoint.Rotation))}"));
            items.Add(Act("Set spawn here", "edit spawn here"));
            items.Add(Opens("Settings: weather, time, ground", "mapsettings"));
            items.Add(Opens("Beacon rules", "beacons"));
            int pins = Overlays.Get(s.CurrentMapId).Pins.Count;
            items.Add(Info(pins == 0 ? "No models pinned to a version of their own" : $"{Plural(pins, "model")} pinned: " + string.Join(", ", Overlays.Get(s.CurrentMapId).Pins.Select(p => $"{p.Key} at version {p.Value}"))));
            items.Add(Info(d.Editors.Count == 0 ? "Editors: none besides the owner" : $"Editors: {string.Join(", ", d.Editors)}"));
            if (_maps.IsOwner(s.CurrentMapId, s.Username) || s.Can(Permissions.MapsAny))
            {
                items.Add(Typed("Add an editor, typed", "/map editor add "));
                items.Add(Typed("Remove an editor, typed", "/map editor remove "));
            }
        }
        return Menu("Map", items);
    }

    private EditorMenu SelectMenu(UserSession s)
    {
        var items = new List<EditorMenuItem>
        {
            Opens("Nearest things", "select.nearest"),
            Opens("Within 5 metres", "select.within:5"),
            Opens("Within 10 metres", "select.within:10"),
            Opens("Within 20 metres", "select.within:20"),
            Opens("Doors near you", "doors"),
            Opens("Places and rooms", "places"),
            Typed("By name, typed", "/edit select "),
            Typed("By number, typed", "/edit select #"),
            Act("Hold the nearest as well", "edit select add nearest"),
            Typed("Hold one as well, by name or number, typed", "/edit select add "),
        };
        if (HandOf(s).Held.Count > 0) items.Add(Act($"Let go of the {Plural(HandOf(s).Held.Count, "held thing")}", "edit select clear"));
        return Menu("Select", items);
    }

    private EditorMenu Listing(UserSession s, string title, List<(int Id, Entity E, float Distance, bool Contains)> things, string verb = "select")
    {
        var items = new List<EditorMenuItem>();
        if (_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _) && TryBody(s, _ => { }, out _, out var feet, out float yaw))
            foreach (var c in things)
                items.Add(Act($"{NameOf(world, c.E)}, {Where(world, c.E, feet, yaw)}", $"edit {verb} #{c.Id}"));
        return Menu(title, items);
    }

    /// <summary>The named places and rooms on the map, nearest first: what a place's name and a room's materials are set on.</summary>
    private EditorMenu PlacesMenu(UserSession s)
    {
        var list = new List<(int Id, Entity E, float Distance, bool Contains)>();
        if (_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _) && TryBody(s, _ => { }, out _, out var feet, out _))
            list = Candidates(world, s.CurrentMapId, feet).Where(c => world.Has<RegionComponent>(c.E))
                .OrderBy(c => c.Contains ? 0 : 1).ThenBy(c => c.Distance).ThenBy(c => c.Id).Take(40).ToList();
        var menu = Listing(s, "Places and rooms", list);
        if (menu.Items.Length == 0) menu.Items = new[] { Info("This map has no named places or rooms. Place one from Place, Places and markers.") };
        return menu;
    }

    private EditorMenu DoorsMenu(UserSession s)
    {
        var list = new List<(int Id, Entity E, float Distance, bool Contains)>();
        if (_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _) && TryBody(s, _ => { }, out _, out var feet, out _))
            list = Candidates(world, s.CurrentMapId, feet).Where(c => world.Has<DoorComponent>(c.E))
                .OrderBy(c => c.Distance).ThenBy(c => c.Id).Take(20).ToList();
        var menu = Listing(s, "Doors near you", list);
        if (menu.Items.Length == 0) menu.Items = new[] { Info("No doors on this map.") };
        return menu;
    }

    private bool Holding(UserSession s, out World world, out Entity e, out int id)
    {
        world = null!; e = Entity.Null; id = 0;
        if (HandOf(s).Selected is not int selected) return false;
        id = selected;
        return _maps.TryGetMap(s.CurrentMapId, out world, out _, out _, out _)
               && _maps.AuthoredEntities(s.CurrentMapId).TryGetValue(selected, out e) && Editable(world, e);
    }

    private EditorMenu SelectedMenu(UserSession s)
    {
        if (!Holding(s, out var world, out var e, out int id)) return Menu("Nothing selected", new[] { Opens("Select", "select") });
        var items = new List<EditorMenuItem>
        {
            Info(Summary(s, world, e, id)),
            Typed("Move by numbers: east, north, up", "/edit move "),
            Opens($"Nudge, step {Metres(HandOf(s).Step)}", "nudge"),
            Opens($"Turn, facing {CompassOf(YawOf(world.Get<Transform>(e).Rotation))}", "turn"),
            Act("Bring to you", "edit bring"),
            Act("Duplicate", "edit duplicate"),
            Opens("A row of copies", "rows"),
            Opens("Delete", "delete"),
            Opens("Settings", "settings"),
        };
        if (!HandOf(s).Held.Contains(id)) items.Add(Act("Hold it as well, to group", $"edit select add #{id}"));
        if (world.Has<SoundEmitterComponent>(e) && ModelKinds.TryModelOfSound(world.Get<SoundEmitterComponent>(e).SoundId, out var kind, out var mid))
            items.Add(Opens($"Its model: {ModelKinds.Spoken(kind)} {mid}, version {VersionOn(s.CurrentMapId, kind, mid)}", $"model:{kind}:{mid}"));
        string prefab = PrefabOf(world, e);
        if (Catalog.Get(PrefabKind.KindId) is { } pk && pk.Knows(prefab))
            items.Add(Opens($"Its prefab: {prefab}, version {Models.CurrentVersion(PrefabKind.KindId, prefab)}", $"model:{PrefabKind.KindId}:{prefab}"));
        return Menu(NameOf(world, e), items);
    }

    private EditorMenu HeldMenu(UserSession s)
    {
        var items = new List<EditorMenuItem>();
        var hand = HandOf(s);
        if (_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _) && TryBody(s, _ => { }, out _, out var feet, out float yaw))
            foreach (int id in hand.Held)
                if (_maps.AuthoredEntities(s.CurrentMapId).TryGetValue(id, out var e) && Editable(world, e))
                    items.Add(Act($"{NameOf(world, e)}, {Where(world, e, feet, yaw)}", $"edit select #{id}"));
        items.Add(Typed("Group them, typed: a name for the group", "/edit group "));
        items.Add(Act("Let go of them all", "edit select clear"));
        return Menu($"Held, {Plural(hand.Held.Count, "thing")}", items);
    }

    private static readonly string[] NudgeWords = { "north", "south", "east", "west", "up", "down", "forward", "back", "left", "right" };

    private EditorMenu NudgeMenu(UserSession s)
    {
        var items = new List<EditorMenuItem> { Typed($"Step, {Metres(HandOf(s).Step)}, typed", "/edit step ") };
        foreach (var w in NudgeWords) items.Add(Act(Capital(w), $"edit nudge {w}"));
        return Menu("Nudge", items);
    }

    private EditorMenu TurnMenu(UserSession s)
    {
        var items = new List<EditorMenuItem>();
        if (Holding(s, out var world, out var e, out _)) items.Add(Info($"Facing {CompassOf(YawOf(world.Get<Transform>(e).Rotation))}"));
        items.Add(Act("15 degrees clockwise", "edit turn 15"));
        items.Add(Act("15 degrees anticlockwise", "edit turn -15"));
        items.Add(Act("90 degrees clockwise", "edit turn 90"));
        items.Add(Act("90 degrees anticlockwise", "edit turn -90"));
        foreach (var w in new[] { "north", "east", "south", "west" }) items.Add(Act($"Face {w}", $"edit face {w}"));
        items.Add(Typed("By degrees, typed", "/edit turn "));
        return Menu("Turn", items);
    }

    private EditorMenu RowsMenu(UserSession s)
    {
        var items = new List<EditorMenuItem>();
        foreach (int n in new[] { 2, 3, 5, 10 }) items.Add(Act($"{n} copies the way you face, a width apart", $"edit row {n}"));
        items.Add(Typed("Copies and spacing in metres, typed", "/edit row "));
        return Menu("A row of copies", items);
    }

    private EditorMenu DeleteMenu(UserSession s)
    {
        if (!Holding(s, out var world, out var e, out _)) return Menu("Nothing selected", Array.Empty<EditorMenuItem>());
        string name = NameOf(world, e);
        // The yes closes the editor: what it was looking at has gone. Undo puts it back.
        return Menu($"Delete {name}?", new[] { Act($"Yes, delete {name}", "edit delete", stay: false), Info("No: Escape keeps it") });
    }

    private EditorMenu SettingsMenu(UserSession s)
    {
        if (!Holding(s, out var world, out var e, out _)) return Menu("Nothing selected", Array.Empty<EditorMenuItem>());
        var items = EntitySettings.For(world, e)
            .Select(x => Opens($"{Capital(x.Field.Label)}, {x.Field.Say(x.Get(world, e))}", $"setting:{x.Field.Path}"));
        return Menu($"Settings of {NameOf(world, e)}", items);
    }

    private EditorMenu? SettingMenu(UserSession s, string path)
    {
        var setting = EntitySettings.Named(path);
        if (setting == null || !Holding(s, out var world, out var e, out _) || !setting.Applies(world, e)) return null;
        var menu = FieldMenu(setting.Field, setting.Get(world, e), $"/edit set {setting.Field.Path} ",
                             $"edit up {setting.Field.Path}", $"edit down {setting.Field.Path}", mayChange: true,
                             choose: v => $"edit set {setting.Field.Path} {v}");
        // A thing's model: the other models of its kind, to choose from.
        if (setting.Field.Path == EntitySettings.ModelPath
            && ModelKinds.TryModelOfSound(world.Get<SoundEmitterComponent>(e).SoundId, out var kind, out var now))
        {
            var items = menu.Items.ToList();
            foreach (var other in ModelLibrary.Ids(kind).Where(i => !i.Equals(now, StringComparison.OrdinalIgnoreCase) && !Models.IsRetired(kind, i))
                                                     .OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
                items.Add(Act($"Use {other}: {ModelName(kind, other)}", $"edit set {EntitySettings.ModelPath} {other}"));
            menu.Items = items.ToArray();
        }
        return menu;
    }

    /// <summary>One field: its value and range, a way to type a value, a step either way, the choices if it
    /// has them, its help and source.</summary>
    private static EditorMenu FieldMenu(FieldDescriptor field, string? value, string typed, string up, string down, bool mayChange,
                                        Func<string, string>? choose = null)
    {
        var items = new List<EditorMenuItem>();
        string range = field.RangeText;
        items.Add(Info(value == null ? $"{Capital(field.Label)}: not part of this model"
                                     : $"{Capital(field.Label)}, {field.Say(value)}{(range.Length > 0 && !field.ReadOnly && field.Type != FieldType.Choice ? $", {range}" : "")}"));
        if (mayChange && !field.ReadOnly)
        {
            if (field.Type is FieldType.Choice or FieldType.Bool && choose != null)
            {
                var choices = field.Type == FieldType.Bool ? new[] { "on", "off" } : field.Choices;
                foreach (var c in choices.Take(60))
                    if (value == null || !field.Say(value).Equals(c, StringComparison.OrdinalIgnoreCase)) items.Add(Act(Capital(c), choose(c)));
            }
            else items.Add(Typed("Type a value", typed));
            if (value != null && field.Type is FieldType.Number or FieldType.Integer)
            {
                string step = FieldDescriptor.Format(field.EffectiveStep) + (field.Unit.Length > 0 ? " " + field.Unit : "");
                items.Add(Act($"Up {step}", up));
                items.Add(Act($"Down {step}", down));
            }
        }
        items.Add(Info(field.ReadOnly ? "Not described yet, or not a physical part, so it cannot be changed here." : field.Help));
        if (field.Source.Length > 0) items.Add(Info($"Source: {field.Source}"));
        return Menu(Capital(field.Label), items);
    }

    // ── Place ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The categories prefabs are browsed by, in the order they are listed.</summary>
    public static readonly string[] Categories =
    {
        "Walls and fences", "Floors, roads and roofs", "Doors", "Machines", "Water", "Fire", "Trees and plants",
        "Sounds", "Places and markers", "Things to carry", "Other",
    };

    /// <summary>Which category a prefab is browsed under: from what it is, never a list kept by hand.</summary>
    public static string CategoryOf(PrefabTemplate t)
    {
        string id = t.Id.ToLowerInvariant();
        string sound = (t.SoundId ?? "").ToLowerInvariant();
        if (t.IsItem) return "Things to carry";
        if (t.IsDoor == true) return "Doors";
        if (sound.StartsWith("machine:")) return "Machines";
        if (sound.StartsWith("water:") || sound.StartsWith("flow:") || sound.StartsWith("shore:") || id.Contains("water") || id.StartsWith("shore_")) return "Water";
        if (sound.StartsWith("fire:")) return "Fire";
        if (sound.StartsWith("foliage:") || id.Contains("tree") || id.Contains("foliage") || id.Contains("hedge")) return "Trees and plants";
        if (t.RoomSize.HasValue || t.RegionAId.HasValue || id.Contains("region") || id.Contains("portal") || id.Contains("marker")
            || id.Contains("named_place") || t.Type is EntityType.Trigger or EntityType.Beacon) return "Places and markers";
        if (id.Contains("floor") || id.Contains("road") || id.Contains("roof") || id.Contains("ground") || id.Contains("ceiling")) return "Floors, roads and roofs";
        if (id.Contains("wall") || id.Contains("fence") || id.Contains("pillar") || id.Contains("building") || id.Contains("boulder")) return "Walls and fences";
        if (t.HasEmitter) return "Sounds";
        return "Other";
    }

    /// <summary>The category groups are listed under.</summary>
    public const string GroupsCategory = "Groups";

    private IEnumerable<PrefabTemplate> Placeable(UserSession s)
        => _maps.Prefabs.Values.Where(t => MayPlace(s, t, out _) && !Models.IsRetired(PrefabKind.KindId, t.Id));

    private EditorMenu PlaceMenu(UserSession s)
    {
        var hand = HandOf(s);
        var groups = Placeable(s).GroupBy(CategoryOf).ToDictionary(g => g.Key, g => g.Count());
        var items = new List<EditorMenuItem>
        {
            Opens($"Choosing a prefab will: {PlaceModeWords(hand.Mode)}. Change", "place.mode"),
            Typed("Search, typed", "/edit find "),
        };
        if (hand.LastPlaced is { } last && _maps.Prefabs.TryGetValue(last.ToLowerInvariant(), out var lt))
            items.Add(Act($"Again: {lt.Name}, where you stand", "edit again"));
        items.AddRange(Categories.Where(groups.ContainsKey).Select(c => Opens($"{c}, {groups[c]}", $"place.cat:{c}")));
        int groupCount = GroupIds().Count();
        if (groupCount > 0) items.Add(Opens($"{GroupsCategory}, {groupCount}", $"place.cat:{GroupsCategory}"));
        return Menu($"Place, {PlaceModeWords(hand.Mode)}", items);
    }

    private EditorMenu PlaceModeMenu(UserSession s) => Menu("Choosing a prefab will", new[]
    {
        Act("Place it at your feet, or just in front of you if it is solid", "edit place mode feet", stay: false),
        Act("Place it at the build cursor (/build)", "edit place mode cursor", stay: false),
        Act("Play a preview of it to you alone, placing nothing", "edit place mode preview", stay: false),
    });

    /// <summary>What choosing a prefab from Place does, said.</summary>
    private static string PlaceModeWords(PlaceMode mode) => mode switch
    {
        PlaceMode.Cursor => "place at the build cursor",
        PlaceMode.Preview => "preview by ear",
        _ => "place at your feet",
    };

    /// <summary>The command choosing a prefab sends, by the mode the editor's place is in.</summary>
    private static string PlaceCommand(PlaceMode mode, string prefab) => mode switch
    {
        PlaceMode.Cursor => $"edit place {prefab} at cursor",
        PlaceMode.Preview => $"edit preview {prefab}",
        _ => $"edit place {prefab}",
    };

    private EditorMenuItem PrefabItem(UserSession s, PrefabTemplate t)
    {
        string size = t.ColliderSize is { } z ? $", {FieldDescriptor.Format(z.X)} by {FieldDescriptor.Format(z.Z)} by {FieldDescriptor.Format(z.Y)} high" : "";
        return Act($"{t.Name}{size}", PlaceCommand(HandOf(s).Mode, t.Id));
    }

    private EditorMenu? CategoryMenu(UserSession s, string category)
    {
        if (category == GroupsCategory)
            return Menu(GroupsCategory, GroupIds().OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
                .Select(id => Act($"{ModelName(GroupKind.KindId, id)}, {Plural(GroupOf(id)?.Parts.Length ?? 0, "part")}", $"edit place group {id}")));
        if (!Categories.Contains(category)) return null;
        var items = Placeable(s).Where(t => CategoryOf(t) == category).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Id)
            .Select(t => PrefabItem(s, t));
        return Menu(category, items);
    }

    /// <summary>Prefabs whose name or id has every word searched for, best first.</summary>
    internal List<PrefabTemplate> Find(UserSession s, string words)
    {
        var terms = words.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Select(w => w.ToLowerInvariant()).ToArray();
        if (terms.Length == 0) return new();
        bool Has(PrefabTemplate t, string w) => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || t.Id.Contains(w, StringComparison.OrdinalIgnoreCase)
                                               || CategoryOf(t).Contains(w, StringComparison.OrdinalIgnoreCase);
        return Placeable(s).Where(t => terms.All(w => Has(t, w)))
            .OrderBy(t => t.Name.StartsWith(terms[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Take(40).ToList();
    }

    private EditorMenu FindMenu(UserSession s, string words)
    {
        var found = Find(s, words);
        var items = found.Select(t => PrefabItem(s, t)).ToList();
        if (items.Count == 0) items.Add(Info($"Nothing is called {words}."));
        items.Add(Typed("Search again, typed", "/edit find "));
        return Menu($"Found for {words}, {found.Count}", items);
    }

    private void SayPrefabs(UserSession s, string[] args, Action<IMessage> reply)
    {
        string? category = args.Length > 0 ? Categories.FirstOrDefault(c => c.StartsWith(string.Join(" ", args), StringComparison.OrdinalIgnoreCase)) : null;
        if (category == null)
        {
            var groups = Placeable(s).GroupBy(CategoryOf).ToDictionary(g => g.Key, g => g.Count());
            Say(reply, "Categories: " + string.Join("; ", Categories.Where(groups.ContainsKey).Select(c => $"{c}, {groups[c]}"))
                     + ". /edit prefabs CATEGORY lists one; /edit find WORDS searches; /edit place PREFAB puts one at your feet.");
            return;
        }
        var list = Placeable(s).Where(t => CategoryOf(t) == category).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Say(reply, $"{category}: " + string.Join("; ", list.Select(t => $"{t.Id}, {t.Name}")) + ".");
    }

    // ── Library ─────────────────────────────────────────────────────────────────────────────────

    private EditorMenu LibraryMenu()
        => Menu("Library", Catalog.All.OrderBy(k => k.Spoken)
            .Select(k =>
            {
                int n = k.Ids.Count(i => !Models.IsRetired(k.Kind, i));
                return Opens($"{Capital(k.Spoken)}, {n} model{(n == 1 ? "" : "s")}", $"kind:{k.Kind}");
            }));

    private EditorMenu? KindMenu(string kindId, bool retired)
    {
        var kind = Catalog.Get(kindId);
        if (kind == null) return null;
        var ids = kind.Ids.Where(i => Models.IsRetired(kind.Kind, i) == retired).OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
        var items = ids.Select(id => Opens($"{id}: {kind.Name(id)}, version {Models.CurrentVersion(kind.Kind, id)}", $"model:{kind.Kind}:{id}")).ToList();
        if (!retired)
        {
            if (kind.Ids.Any(i => kind.BuiltInJson(i) != null && !Models.IsRetired(kind.Kind, i)))
                items.Insert(0, Opens("New from a template", $"templates:{kind.Kind}"));
            int gone = kind.Ids.Count(i => Models.IsRetired(kind.Kind, i));
            if (gone > 0) items.Add(Opens($"Retired, {gone}", $"retired:{kind.Kind}"));
        }
        return Menu(retired ? $"Retired {kind.Spoken} models" : Capital(kind.Spoken), items);
    }

    private EditorMenu? TemplatesMenu(string kindId)
    {
        var kind = Catalog.Get(kindId);
        if (kind == null) return null;
        var items = kind.Ids.Where(i => kind.BuiltInJson(i) != null && !Models.IsRetired(kind.Kind, i)).OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
            .Select(id => Typed($"{id}: {kind.Name(id)}. Type the new model's id", $"/edit model new {kind.Kind} {id} "));
        return Menu($"New {kind.Spoken} from a template, as built", items);
    }

    private EditorMenu? ModelMenu(UserSession s, string kindId, string id, string groupPath)
    {
        var kind = Catalog.Get(kindId);
        if (kind == null || !kind.Knows(id)) return null;
        var fields = kind.Fields;
        var root = JsonNode.Parse(kind.CurrentJson(id))!;
        IReadOnlyList<FieldNode> level;
        FieldNode? at = null;
        if (groupPath.Length == 0) level = fields;
        else
        {
            at = ModelKinds.NodeAt(fields, groupPath, out _);
            if (at == null || at.Kind == FieldNodeKind.Scalar) return null;
            level = at.Children;
        }
        var items = new List<EditorMenuItem>();
        bool mayChange = s.Can(Permissions.EditModels);
        if (groupPath.Length == 0)
        {
            var (here, elsewhere) = UsedBy(kind.Kind, id, s.CurrentMapId);
            string pin = PinOf(s.CurrentMapId, kind.Kind, id) is int p ? $", pinned here at version {p}" : "";
            items.Add(Info($"{kind.Name(id)}: the {kind.Spoken} {id}, version {Models.CurrentVersion(kind.Kind, id)}{pin}"
                         + (Models.IsRetired(kind.Kind, id) ? ", retired" : "")
                         + $", used by {here} here and {elsewhere} elsewhere"
                         + (mayChange ? "" : ". Changing it needs edit-models")));
        }

        // A list without an index: its items.
        if (at is { Kind: FieldNodeKind.List } && !groupPath.EndsWith(']'))
        {
            if (ModelKinds.Get(root, groupPath) is JsonArray arr)
                for (int i = 0; i < arr.Count; i++)
                {
                    string named = arr[i] is JsonObject o && (o.TryGetPropertyValue("Name", out var n) || o.TryGetPropertyValue("PrefabId", out n))
                                   && n is JsonValue v && v.TryGetValue(out string? nm) && !string.IsNullOrWhiteSpace(nm) ? $": {nm}" : "";
                    items.Add(Opens($"{Capital(at.Label)} {i + 1}{named}", $"model:{kind.Kind}:{id}:{groupPath}[{i}]"));
                }
            return Menu(Capital(at.Label), items);
        }

        string prefix = groupPath.Length == 0 ? "" : groupPath + ".";
        foreach (var node in level)
        {
            string path = prefix + node.Name;
            switch (node.Kind)
            {
                case FieldNodeKind.Scalar:
                {
                    string? value = ModelKinds.GetValue(root, path, node);
                    string said = value == null ? "none" : node.Field!.Say(value);
                    items.Add(Opens($"{Capital(node.Label)}, {said}{(node.Field!.ReadOnly ? ", read only" : "")}", $"mfield:{kind.Kind}:{id}:{path}"));
                    break;
                }
                case FieldNodeKind.Group:
                    items.Add(ModelKinds.Get(root, path) is JsonObject ? Opens(Capital(node.Label), $"model:{kind.Kind}:{id}:{path}") : Info($"{Capital(node.Label)}: none"));
                    break;
                case FieldNodeKind.List:
                    int count = ModelKinds.Get(root, path) is JsonArray a ? a.Count : 0;
                    items.Add(Opens($"{Capital(node.Label)}, {count} item{(count == 1 ? "" : "s")}", $"model:{kind.Kind}:{id}:{path}"));
                    break;
            }
        }
        // An item of a list: it can be taken out.
        if (groupPath.EndsWith(']') && mayChange)
            items.Add(Act($"Take this {at?.Label ?? "item"} out of the model", $"edit model remove {kind.Kind} {id} {groupPath}"));
        if (groupPath.Length == 0)
        {
            items.Add(Opens("Versions", $"versions:{kind.Kind}:{id}"));
            items.Add(Opens("Where it is used", $"where:{kind.Kind}:{id}"));
            if (kind.Kind != ModelLibrary.Kinds.Engine) items.Add(Opens("Replace it with another", $"replace:{kind.Kind}:{id}"));
            if (mayChange)
            {
                items.Add(Typed("Copy it, as it is now: type the new model's id", $"/edit model copy {kind.Kind} {id} "));
                items.Add(Models.IsRetired(kind.Kind, id)
                    ? Act("Bring it back: offer it for new things again", $"edit model restore {kind.Kind} {id}")
                    : Act("Retire it: offer it no longer for new things", $"edit model retire {kind.Kind} {id}"));
            }
        }
        string title = groupPath.Length == 0 ? kind.Name(id) : Capital(FullLabel(fields, groupPath));
        return Menu(title, items);
    }

    private EditorMenu? ModelFieldMenu(UserSession s, string kindId, string id, string path)
    {
        var kind = Catalog.Get(kindId);
        if (kind == null || !kind.Knows(id)) return null;
        var node = ModelKinds.NodeAt(kind.Fields, path, out var field);
        if (node == null || field == null) return null;
        field = field with { Label = FullLabel(kind.Fields, field.Path) };
        var root = JsonNode.Parse(kind.CurrentJson(id))!;
        string? value = ModelKinds.GetValue(root, field.Path, node);
        return FieldMenu(field, value, $"/edit model set {kind.Kind} {id} {field.Path} ",
                         $"edit model up {kind.Kind} {id} {field.Path}", $"edit model down {kind.Kind} {id} {field.Path}",
                         mayChange: s.Can(Permissions.EditModels), choose: v => $"edit model set {kind.Kind} {id} {field.Path} {v}");
    }

    private EditorMenu? VersionsMenu(UserSession s, string kindId, string id)
    {
        var kind = Catalog.Get(kindId);
        if (kind == null || !kind.Knows(id)) return null;
        var h = Models.History(kind.Kind, id);
        int? pinned = PinOf(s.CurrentMapId, kind.Kind, id);
        var items = new List<EditorMenuItem>();
        string Marks(int v) => (v == (h?.Current ?? 0) ? ", in use" : "") + (v == pinned ? ", pinned on this map" : "");
        if (h != null)
            foreach (var v in h.Versions.OrderByDescending(v => v.Version))
                items.Add(Opens($"Version {v.Version}{Marks(v.Version)}: {v.Note}, by {v.Author}, {v.SavedUtc.ToLocalTime():d MMMM HH:mm}", $"version:{kind.Kind}:{id}:{v.Version}"));
        if (h == null || h.Base != null) items.Add(Opens($"Version 0, as built{Marks(0)}", $"version:{kind.Kind}:{id}:0"));
        if (pinned != null) items.Add(Act("Lift this map's pin: use the current version here", $"edit model unpin {kind.Kind} {id}"));
        if (kind.ToClients) items.Add(Info("A pin is for what is heard: what the server simulates with a model, such as a vehicle's mass, is the current version on every map."));
        return Menu($"Versions of {kind.Name(id)}", items);
    }

    private EditorMenu? VersionMenu(UserSession s, string kindId, string id, string versionWord)
    {
        var kind = Catalog.Get(kindId);
        if (kind == null || !kind.Knows(id) || !TryVersion(versionWord, out int version) || !Models.HasVersion(kind.Kind, id, version)) return null;
        var items = new List<EditorMenuItem> { Info(Capital(VersionWords(kind.Kind, id, version))) };
        bool current = Models.CurrentVersion(kind.Kind, id) == version;
        if (!current && s.Can(Permissions.EditModels) && Models.History(kind.Kind, id) != null)
            items.Add(Act("Use it on every map that does not pin another", $"edit model use {kind.Kind} {id} {version}"));
        if (kind.ToClients && PinOf(s.CurrentMapId, kind.Kind, id) != version)
            items.Add(Act("Pin it on this map", $"edit model pin {kind.Kind} {id} {version}"));
        return Menu($"Version {version} of {kind.Name(id)}", items);
    }

    private EditorMenu? WhereMenu(string kindId, string id)
    {
        var kind = Catalog.Get(kindId);
        if (kind == null || !kind.Knows(id)) return null;
        var used = WhereUsed(kind.Kind, id);
        var items = used.Select(u =>
        {
            int? pin = PinOf(u.MapId, kind.Kind, id);
            return Info($"{_maps.DisplayName(u.MapId)}: {Plural(u.Count, "thing")}{(pin is int p ? $", pinned at version {p}" : "")}");
        }).ToList();
        if (items.Count == 0) items.Add(Info("Nothing on a loaded map uses it."));
        return Menu($"Where {kind.Name(id)} is used", items);
    }

    private EditorMenu? ReplaceMenu(UserSession s, string kindId, string id)
    {
        var kind = Catalog.Get(kindId);
        if (kind == null || !kind.Knows(id)) return null;
        var items = kind.Ids.Where(i => !i.Equals(id, StringComparison.OrdinalIgnoreCase) && !Models.IsRetired(kind.Kind, i))
            .OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
            .Select(other => Opens($"With {other}: {kind.Name(other)}", $"replacewith:{kind.Kind}:{id}:{other}")).ToList();
        return Menu($"Replace {id} with", items);
    }

    private EditorMenu? ReplaceWithMenu(UserSession s, string kindId, string id, string other)
    {
        var kind = Catalog.Get(kindId);
        if (kind == null || !kind.Knows(id) || !kind.Knows(other)) return null;
        var (here, elsewhere) = UsedBy(kind.Kind, id, s.CurrentMapId);
        var items = new List<EditorMenuItem>
        {
            Act($"On this map, {Plural(here, "thing")}", $"edit model replace {kind.Kind} {id} with {other} here"),
        };
        if (s.Can(Permissions.EditModels)) items.Add(Act($"On every loaded map, {Plural(here + elsewhere, "thing")}", $"edit model replace {kind.Kind} {id} with {other} everywhere"));
        return Menu($"Replace {id} with {other}", items);
    }

    private static EditorMenu TestMenu() => Menu("Test tools", new[]
    {
        Act("What is around me", "scan"),
        Act("Map information", "edit info"),
    });
}
