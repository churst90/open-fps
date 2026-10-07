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
        return parts[0] switch
        {
            "root" => Root(s),
            "map" => MapMenu(s),
            "select" => SelectMenu(),
            "select.nearest" => Listing(s, "Nearest things", Nearest(s, 8)),
            "select.within" when parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
                => Listing(s, $"Within {Metres(r)}", Within(s, r)),
            "selected" => SelectedMenu(s),
            "nudge" => NudgeMenu(s),
            "turn" => TurnMenu(s),
            "delete" => DeleteMenu(s),
            "settings" => SettingsMenu(s),
            "setting" when parts.Length > 1 => SettingMenu(s, parts[1]),
            "place" => PlaceMenu(s),
            "place.cat" when parts.Length > 1 => CategoryMenu(s, parts[1]),
            "library" => LibraryMenu(),
            "kind" when parts.Length > 1 => KindMenu(parts[1]),
            "model" when parts.Length > 2 => ModelMenu(s, parts[1], parts[2], parts.Length > 3 ? parts[3] : ""),
            "mfield" when parts.Length > 3 => ModelFieldMenu(s, parts[1], parts[2], parts[3]),
            "versions" when parts.Length > 2 => VersionsMenu(parts[1], parts[2]),
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
            items.Add(Info(d.Editors.Count == 0 ? "Editors: none besides the owner" : $"Editors: {string.Join(", ", d.Editors)}"));
            if (_maps.IsOwner(s.CurrentMapId, s.Username) || s.Can(Permissions.MapsAny))
            {
                items.Add(Typed("Add an editor, typed", "/map editor add "));
                items.Add(Typed("Remove an editor, typed", "/map editor remove "));
            }
        }
        return Menu("Map", items);
    }

    private static EditorMenu SelectMenu() => Menu("Select", new[]
    {
        Opens("Nearest things", "select.nearest"),
        Opens("Within 5 metres", "select.within:5"),
        Opens("Within 10 metres", "select.within:10"),
        Opens("Within 20 metres", "select.within:20"),
        Typed("By name, typed", "/edit select "),
        Typed("By number, typed", "/edit select #"),
    });

    private EditorMenu Listing(UserSession s, string title, List<(int Id, Entity E, float Distance, bool Contains)> things)
    {
        var items = new List<EditorMenuItem>();
        if (_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _) && TryBody(s, _ => { }, out _, out var feet, out float yaw))
            foreach (var c in things)
                items.Add(Act($"{NameOf(world, c.E)}, {Where(world, c.E, feet, yaw)}", $"edit select #{c.Id}"));
        return Menu(title, items);
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
            Opens("Delete", "delete"),
            Opens("Settings", "settings"),
        };
        if (world.Has<SoundEmitterComponent>(e) && ModelKinds.TryModelOfSound(world.Get<SoundEmitterComponent>(e).SoundId, out var kind, out var mid))
            items.Add(Opens($"Its model: {ModelKinds.Spoken(kind)} {mid}, version {Models.CurrentVersion(kind, mid)}", $"model:{kind}:{mid}"));
        return Menu(NameOf(world, e), items);
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
        return FieldMenu(setting.Field, setting.Get(world, e), $"/edit set {setting.Field.Path} ",
                         $"edit up {setting.Field.Path}", $"edit down {setting.Field.Path}", mayChange: true);
    }

    /// <summary>One field: its value and range, a way to type a value, a step either way, its help and source.</summary>
    private static EditorMenu FieldMenu(FieldDescriptor field, string? value, string typed, string up, string down, bool mayChange)
    {
        var items = new List<EditorMenuItem>();
        string range = field.RangeText;
        items.Add(Info(value == null ? $"{Capital(field.Label)}: not part of this model"
                                     : $"{Capital(field.Label)}, {field.Say(value)}{(range.Length > 0 && !field.ReadOnly ? $", {range}" : "")}"));
        if (value != null && mayChange && !field.ReadOnly)
        {
            items.Add(Typed("Type a value", typed));
            if (field.Type is FieldType.Number or FieldType.Integer)
            {
                string step = FieldDescriptor.Format(field.EffectiveStep) + (field.Unit.Length > 0 ? " " + field.Unit : "");
                items.Add(Act($"Up {step}", up));
                items.Add(Act($"Down {step}", down));
            }
        }
        items.Add(Info(field.ReadOnly ? "Not described yet, so it cannot be changed here." : field.Help));
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

    private IEnumerable<PrefabTemplate> Placeable(UserSession s)
        => _maps.Prefabs.Values.Where(t => MayPlace(s, t, out _));

    private EditorMenu PlaceMenu(UserSession s)
    {
        var groups = Placeable(s).GroupBy(CategoryOf).ToDictionary(g => g.Key, g => g.Count());
        var items = Categories.Where(groups.ContainsKey).Select(c => Opens($"{c}, {groups[c]}", $"place.cat:{c}"));
        return Menu("Place", items);
    }

    private EditorMenu? CategoryMenu(UserSession s, string category)
    {
        if (!Categories.Contains(category)) return null;
        var items = Placeable(s).Where(t => CategoryOf(t) == category).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Id)
            .Select(t =>
            {
                string size = t.ColliderSize is { } z ? $", {FieldDescriptor.Format(z.X)} by {FieldDescriptor.Format(z.Z)} by {FieldDescriptor.Format(z.Y)} high" : "";
                return Act($"{t.Name}{size}", $"edit place {t.Id}");
            });
        return Menu(category, items);
    }

    private void SayPrefabs(UserSession s, string[] args, Action<IMessage> reply)
    {
        string? category = args.Length > 0 ? Categories.FirstOrDefault(c => c.StartsWith(string.Join(" ", args), StringComparison.OrdinalIgnoreCase)) : null;
        if (category == null)
        {
            var groups = Placeable(s).GroupBy(CategoryOf).ToDictionary(g => g.Key, g => g.Count());
            Say(reply, "Categories: " + string.Join("; ", Categories.Where(groups.ContainsKey).Select(c => $"{c}, {groups[c]}"))
                     + ". /edit prefabs CATEGORY lists one; /edit place PREFAB puts one at your feet.");
            return;
        }
        var list = Placeable(s).Where(t => CategoryOf(t) == category).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Say(reply, $"{category}: " + string.Join("; ", list.Select(t => $"{t.Id}, {t.Name}")) + ".");
    }

    // ── Library ─────────────────────────────────────────────────────────────────────────────────

    private static EditorMenu LibraryMenu()
        => Menu("Library", ModelLibrary.AllKinds.OrderBy(k => ModelKinds.Spoken(k))
            .Select(k => Opens($"{Capital(ModelKinds.Spoken(k))}, {ModelLibrary.Ids(k).Count()} models", $"kind:{k}")));

    private EditorMenu? KindMenu(string kind)
    {
        if (ModelLibrary.TypeOf(kind) == null) return null;
        var items = ModelLibrary.Ids(kind).OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
            .Select(id => Opens($"{id}: {ModelName(kind, id)}, version {Models.CurrentVersion(kind, id)}", $"model:{kind}:{id}"));
        return Menu(Capital(ModelKinds.Spoken(kind)), items);
    }

    private EditorMenu? ModelMenu(UserSession s, string kind, string id, string groupPath)
    {
        var type = ModelLibrary.TypeOf(kind);
        if (type == null || !ModelLibrary.Knows(kind, id)) return null;
        var root = JsonNode.Parse(Models.CurrentJson(kind, id))!;
        IReadOnlyList<FieldNode> level;
        FieldNode? at = null;
        if (groupPath.Length == 0) level = ModelKinds.Describe(type);
        else
        {
            at = ModelKinds.NodeAt(type, groupPath, out _);
            if (at == null || at.Kind == FieldNodeKind.Scalar) return null;
            level = at.Children;
        }
        var items = new List<EditorMenuItem>();
        if (groupPath.Length == 0)
        {
            var (here, elsewhere) = UsedBy(kind, id, s.CurrentMapId);
            items.Add(Info($"{ModelName(kind, id)}: the {ModelKinds.Spoken(kind)} {id}, version {Models.CurrentVersion(kind, id)}, "
                         + $"used by {here} here and {elsewhere} elsewhere"
                         + (s.Can(Permissions.EditModels) ? "" : ". Changing it needs edit-models")));
        }

        // A list without an index: its items.
        if (at is { Kind: FieldNodeKind.List } && !groupPath.EndsWith(']'))
        {
            if (ModelKinds.Get(root, groupPath) is JsonArray arr)
                for (int i = 0; i < arr.Count; i++)
                {
                    string named = arr[i] is JsonObject o && o.TryGetPropertyValue("Name", out var n) && n is JsonValue v && v.TryGetValue(out string? nm) && !string.IsNullOrWhiteSpace(nm) ? $": {nm}" : "";
                    items.Add(Opens($"{Capital(at.Label)} {i + 1}{named}", $"model:{kind}:{id}:{groupPath}[{i}]"));
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
                    items.Add(Opens($"{Capital(node.Label)}, {said}{(node.Field!.ReadOnly ? ", read only" : "")}", $"mfield:{kind}:{id}:{path}"));
                    break;
                }
                case FieldNodeKind.Group:
                    items.Add(ModelKinds.Get(root, path) is JsonObject ? Opens(Capital(node.Label), $"model:{kind}:{id}:{path}") : Info($"{Capital(node.Label)}: none"));
                    break;
                case FieldNodeKind.List:
                    int count = ModelKinds.Get(root, path) is JsonArray a ? a.Count : 0;
                    items.Add(Opens($"{Capital(node.Label)}, {count} item{(count == 1 ? "" : "s")}", $"model:{kind}:{id}:{path}"));
                    break;
            }
        }
        if (groupPath.Length == 0) items.Add(Opens("Versions", $"versions:{kind}:{id}"));
        string title = groupPath.Length == 0 ? ModelName(kind, id) : Capital(FullLabel(type, groupPath));
        return Menu(title, items);
    }

    private EditorMenu? ModelFieldMenu(UserSession s, string kind, string id, string path)
    {
        var type = ModelLibrary.TypeOf(kind);
        if (type == null || !ModelLibrary.Knows(kind, id)) return null;
        var node = ModelKinds.NodeAt(type, path, out var field);
        if (node == null || field == null) return null;
        field = field with { Label = FullLabel(type, field.Path) };
        var root = JsonNode.Parse(Models.CurrentJson(kind, id))!;
        string? value = ModelKinds.GetValue(root, field.Path, node);
        return FieldMenu(field, value, $"/edit model set {kind} {id} {field.Path} ",
                         $"edit model up {kind} {id} {field.Path}", $"edit model down {kind} {id} {field.Path}",
                         mayChange: s.Can(Permissions.EditModels));
    }

    private EditorMenu? VersionsMenu(string kind, string id)
    {
        if (ModelLibrary.TypeOf(kind) == null || !ModelLibrary.Knows(kind, id)) return null;
        var h = Models.History(kind, id);
        var items = new List<EditorMenuItem>();
        if (h != null)
            foreach (var v in h.Versions.OrderByDescending(v => v.Version))
                items.Add(Info($"Version {v.Version}{(v.Version == h.Current ? ", in use" : "")}: {v.Note}, by {v.Author}, {v.SavedUtc:d MMMM HH:mm}"));
        items.Add(Info($"Version 0, as built{((h?.Current ?? 0) == 0 ? ", in use" : "")}"));
        return Menu("Versions", items);
    }

    private static EditorMenu TestMenu() => Menu("Test tools", new[]
    {
        Act("What is around me", "scan"),
        Act("Map information", "edit info"),
    });
}
