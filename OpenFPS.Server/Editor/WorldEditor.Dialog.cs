using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Editor;

/// <summary>
/// The F12 editor dialog's content (docs/WORLD_EDITOR.md section 16). The dialog is drawn by the client
/// from editor menus whose items carry a <see cref="EditorMenuItem.Section"/>: "dialog" holds every tab
/// when it opens, "dialog.TAB" one tab brought up to date after a change. Every button sends a plain
/// /edit command, so the dialog, the menus and the MUD stay one path.
/// </summary>
public sealed partial class WorldEditor
{
    /// <summary>The dialog's tabs, in order.</summary>
    public static readonly string[] DialogTabs = { "place", "edit", "build", "world" };

    /// <summary>How far the Edit tab looks for things, and how many it lists.</summary>
    public const float DialogReach = 20f;
    public const int DialogThings = 40;

    /// <summary>The most model fields one Build tab lists; an engine has about a hundred.</summary>
    private const int DialogFieldCap = 400;

    /// <summary>Whether /edit with these words is F12 asking for the dialog, which a player who may not edit
    /// is not answered at all: F12 does nothing for them.</summary>
    public static bool AsksForDialog(string[] args)
        => args.Length >= 1 && args[0].Equals("dialog", StringComparison.OrdinalIgnoreCase);

    /// <summary>/edit dialog [open TAB | tab TAB | model KIND ID | close].</summary>
    private void DialogCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (s.IsTextClient) { Say(reply, "The editor dialog is for the game's windows. /edit on its own opens the menu."); return; }
        var hand = HandOf(s);
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "open";
        string Tab(int i) => args.Length > i && DialogTabs.Contains(args[i].ToLowerInvariant()) ? args[i].ToLowerInvariant() : hand.DialogTab;
        switch (verb)
        {
            case "open":
                hand.Dialog = true;
                hand.DialogTab = Tab(1);
                reply(DialogWhole(s));
                return;
            case "tab":
                if (!hand.Dialog) return;
                hand.DialogTab = Tab(1);
                reply(DialogTab(s, hand.DialogTab));
                return;
            case "model":
                if (!hand.Dialog) return;
                if (args.Length >= 3 && Catalog.Get(args[1]) is { } kind && kind.Knows(args[2])) hand.DialogModel = (kind.Kind, args[2]);
                reply(DialogTab(s, "build"));
                return;
            case "close":
                hand.Dialog = false;
                return;
            default:
                Say(reply, "Say /edit dialog, or press F12.");
                return;
        }
    }

    /// <summary>Every tab, as the dialog opens.</summary>
    internal EditorMenu DialogWhole(UserSession s)
    {
        var items = new List<EditorMenuItem>();
        items.AddRange(DialogPlace(s));
        items.AddRange(BuildForm(s).Items.Select(i => { i.Section = "piece"; return i; }));
        items.AddRange(DialogEdit(s));
        items.AddRange(DialogBuild(s));
        items.AddRange(DialogWorld(s));
        items.AddRange(DialogFooter(s));
        return new EditorMenu { Path = "dialog", Title = $"World editor, {_maps.DisplayName(s.CurrentMapId)}", Items = items.ToArray() };
    }

    /// <summary>One tab, current, with the footer: sent after a change, and when the tab is shown.</summary>
    internal EditorMenu DialogTab(UserSession s, string tab)
    {
        var items = tab switch
        {
            "edit" => DialogEdit(s),
            "build" => DialogBuild(s),
            "world" => DialogWorld(s),
            _ => DialogPlace(s),
        };
        return new EditorMenu
        {
            Path = "dialog." + tab, Title = $"World editor, {_maps.DisplayName(s.CurrentMapId)}",
            Items = items.Concat(DialogFooter(s)).ToArray(), Refresh = true,
        };
    }

    private static EditorMenuItem Line(string section, string label, string value = "", string help = "", string prompt = "")
        => new() { Section = section, Label = label, Kind = EditorItemKind.Info, Value = value, Help = help, Prompt = prompt };

    /// <summary>A field as a labelled box: <paramref name="command"/> with the value on the end changes it.</summary>
    private static EditorMenuItem Box(string section, FieldDescriptor field, string? value, string command)
    {
        var item = TypedField(Capital(field.Label), field, value, command);
        item.Section = section;
        if (field.Type is FieldType.Choice or FieldType.Bool)
        {
            item.ValueType = field.Type;
            item.Value = value ?? "";
        }
        if (field.ReadOnly) item.Kind = EditorItemKind.Info;
        return item;
    }

    /// <summary>The choices of a choice field, each tied to its box by the command.</summary>
    private static IEnumerable<EditorMenuItem> Choices(string section, string command, IEnumerable<(string Label, string Value)> choices)
        => choices.Select(c => new EditorMenuItem { Section = section, Kind = EditorItemKind.Info, Command = command, Label = c.Label, Value = c.Value });

    private IEnumerable<EditorMenuItem> DialogFooter(UserSession s)
    {
        yield return NextUndo(s) is { } u
            ? new EditorMenuItem { Section = "foot.undo", Kind = EditorItemKind.Action, Label = $"Undo: {u}", Command = "edit undo" }
            : Line("foot.undo", "Nothing to undo");
        yield return NextRedo(s) is { } r
            ? new EditorMenuItem { Section = "foot.redo", Kind = EditorItemKind.Action, Label = $"Redo: {r}", Command = "edit redo" }
            : Line("foot.redo", "Nothing to redo");
    }

    // ── Place ───────────────────────────────────────────────────────────────────────────────────

    private IEnumerable<EditorMenuItem> DialogPlace(UserSession s)
    {
        // One stream in category order, so the client's categories come in that order. A group's value is
        // "group:ID" and a vehicle's "vehicle:PRESET", which /edit place takes as it takes a prefab.
        foreach (var r in PlaceRows(s))
        {
            // Count 1: it has a sound of its own, so it can be previewed.
            yield return new EditorMenuItem
            {
                Section = "place.prefab", Kind = EditorItemKind.Info, Label = r.Label, Value = r.Value, Prompt = r.Category,
                Help = r.Help, Count = (byte)(r.Previewable ? 1 : 0),
            };
        }
        var hand = HandOf(s);
        if (hand.LastPlaced is { } last && PlaceName(last) is { } lastName)
            yield return new EditorMenuItem { Section = "place.again", Kind = EditorItemKind.Action, Label = lastName, Command = "edit again" };
        foreach (var item in DialogRoutes(s)) yield return item;
    }

    // ── Edit ────────────────────────────────────────────────────────────────────────────────────

    private IEnumerable<EditorMenuItem> DialogEdit(UserSession s)
    {
        var items = new List<EditorMenuItem>();
        if (!_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _) || !TryBody(s, _ => { }, out _, out var feet, out float yaw))
            return items;
        var hand = HandOf(s);
        var authored = _maps.AuthoredEntities(s.CurrentMapId);
        var ids = Within(s, DialogReach, DialogThings).Select(c => c.Id).ToList();
        // What is chosen or ticked stays in the list wherever it is.
        foreach (int extra in hand.Held.Prepend(hand.Selected ?? -1))
            if (extra >= 0 && !ids.Contains(extra)) ids.Add(extra);
        foreach (int id in ids)
            if (authored.TryGetValue(id, out var e) && Editable(world, e))
                items.Add(new EditorMenuItem
                {
                    Section = "edit.thing", Kind = EditorItemKind.Info, Label = $"{NameOf(world, e)}, {Where(world, e, feet, yaw)}",
                    Value = id.ToString(CultureInfo.InvariantCulture), Checked = hand.Held.Contains(id),
                });

        // Everything placed with the editor, wherever it is. The line's Value is the filter in force and
        // its Prompt says whether Go to it is allowed.
        var placed = Placed(s, hand.PlacedFilter, out int total);
        items.Add(Line("edit.placedinfo", PlacedSummary(placed.Count, total, hand.PlacedFilter), hand.PlacedFilter, prompt: MayGo(s) ? "goto" : ""));
        foreach (var one in placed.Take(PlacedListed))
            items.Add(new EditorMenuItem
            {
                Section = "edit.placed", Kind = EditorItemKind.Info, Label = one.Label, Value = one.Id.ToString(CultureInfo.InvariantCulture),
                Prompt = one.Kind, Help = one.Name, Checked = hand.Held.Contains(one.Id),
            });

        // Things from the map file changed or removed: the same, with "removed" or "changed" in the row's Prompt.
        var changed = ChangedList(s, hand.ChangedFilter, out int changedTotal, out int lost);
        items.Add(Line("edit.changedinfo", ChangedSummary(changed.Count, changedTotal, lost, hand.ChangedFilter), hand.ChangedFilter, prompt: MayGo(s) ? "goto" : ""));
        foreach (var one in changed.Take(PlacedListed))
            items.Add(new EditorMenuItem
            {
                Section = "edit.changed", Kind = EditorItemKind.Info, Label = one.Label, Value = one.Id.ToString(CultureInfo.InvariantCulture),
                Prompt = one.Removed ? "removed" : "changed", Help = one.Name,
            });

        if (!Holding(s, out _, out var chosen, out int chosenId)) return items;
        items.Add(Line("edit.chosen", NameOf(world, chosen), chosenId.ToString(CultureInfo.InvariantCulture), Summary(s, world, chosen, chosenId)));
        var t = world.Get<Transform>(chosen);
        var p = t.Position;
        string at = string.Join(" ", new[] { p.X, p.Z, p.Y }.Select(v => FieldDescriptor.Format(MathF.Round(v, 2))));
        var position = TypedNumber("Position", "/edit move to ", "position: metres east, north and up", "", -MaxDistanceMetres, MaxDistanceMetres,
                                   "Where its middle is: three numbers, east, north and up, as F1 says where you are.", at, count: 3);
        position.Section = "edit.field";
        items.Add(position);
        float degrees = YawOf(t.Rotation) * 180f / MathF.PI;
        degrees = ((MathF.Round(degrees) % 360f) + 360f) % 360f;
        var facing = TypedNumber("Facing", "/edit face ", "facing, in degrees", "", 0, 360,
                                 "Clockwise from north: 0 north, 90 east, 180 south, 270 west.", FieldDescriptor.Format(degrees));
        facing.Section = "edit.field";
        items.Add(facing);

        foreach (var x in EntitySettings.For(world, chosen))
        {
            var field = x.Field;
            string value = x.Get(world, chosen);
            string command = $"/edit set {field.Path} ";
            // A thing's model is chosen from the others of its kind, as the menu offers them.
            if (field.Path == EntitySettings.ModelPath && world.Has<SoundEmitterComponent>(chosen)
                && ModelKinds.TryModelOfSound(world.Get<SoundEmitterComponent>(chosen).SoundId, out var kind, out var now))
            {
                var models = ModelLibrary.Ids(kind).Where(i => i.Equals(now, StringComparison.OrdinalIgnoreCase) || !Models.IsRetired(kind, i))
                    .OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
                items.Add(Box("edit.field", field with { Type = FieldType.Choice, Choices = models }, now, command));
                items.AddRange(Choices("edit.choice", command, models.Select(m => ($"{m}: {ModelName(kind, m)}", m))));
                continue;
            }
            items.Add(Box("edit.field", field, value, command));
            if (field.Type == FieldType.Choice)
                items.AddRange(Choices("edit.choice", command, field.Choices.Select(c => (c, c))));
        }
        return items;
    }

    // ── Build: the library ──────────────────────────────────────────────────────────────────────

    private IEnumerable<EditorMenuItem> DialogBuild(UserSession s)
    {
        var items = new List<EditorMenuItem>();
        var kinds = Catalog.All.OrderBy(k => k.Kind == PrefabKind.KindId ? 0 : 1).ThenBy(k => k.Spoken, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var k in kinds)
        {
            var ids = k.Ids.OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
            items.Add(Line("build.kind", $"{Capital(k.Spoken)}, {ids.Count}", k.Kind));
            foreach (var id in ids)
            {
                string category = k.Kind == PrefabKind.KindId && _maps.Prefabs.TryGetValue(id.ToLowerInvariant(), out var pt) ? CategoryOf(pt) : "";
                items.Add(Line("build.model", $"{id}: {k.Name(id)}, version {Models.CurrentVersion(k.Kind, id)}{(Models.IsRetired(k.Kind, id) ? ", retired" : "")}",
                               id, category, k.Kind));
            }
        }

        var hand = HandOf(s);
        if (hand.DialogModel is not { } entry || Catalog.Get(entry.Kind) is not { } kind || !kind.Knows(entry.Id)) return items;
        string modelId = entry.Id;
        bool mayChange = s.Can(Permissions.EditModels);
        var (here, elsewhere) = UsedBy(kind.Kind, modelId, s.CurrentMapId);
        int? pinned = PinOf(s.CurrentMapId, kind.Kind, modelId);
        string summary = $"{kind.Name(modelId)}: the {kind.Spoken} {modelId}, version {Models.CurrentVersion(kind.Kind, modelId)}"
                       + (pinned is int pv ? $", pinned here at version {pv}" : "") + (Models.IsRetired(kind.Kind, modelId) ? ", retired" : "")
                       + $", used by {here} here and {elsewhere} elsewhere." + (mayChange ? "" : " Changing it needs edit-models.");
        // What the player may do with it, as words the client reads.
        var may = new List<string>();
        if (mayChange) may.AddRange(new[] { "change", "copy", "use", "everywhere" });
        if (kind.ToClients) may.Add("pin");
        if (kind.Kind != ModelLibrary.Kinds.Engine) may.Add("replace");
        items.Add(Line("build.chosen", kind.Name(modelId), $"{kind.Kind} {modelId}", summary, string.Join(" ", may)));

        var root = JsonNode.Parse(kind.CurrentJson(modelId))!;
        int count = 0;
        void Walk(IReadOnlyList<FieldNode> level, string prefix)
        {
            foreach (var node in level)
            {
                if (count >= DialogFieldCap) return;
                string path = prefix + node.Name;
                switch (node.Kind)
                {
                    case FieldNodeKind.Scalar:
                        if (ModelKinds.GetValue(root, path, node) is { } value) AddField(node.Field!, path, value);
                        break;
                    case FieldNodeKind.Group:
                        if (ModelKinds.Get(root, path) is JsonObject) Walk(node.Children, path + ".");
                        break;
                    case FieldNodeKind.List:
                        if (ModelKinds.Get(root, path) is not JsonArray arr) break;
                        for (int i = 0; i < arr.Count; i++)
                        {
                            string at = $"{path}[{i}]";
                            if (node.IsValueList) { if (ModelKinds.GetValue(root, at, node) is { } v) AddField(node.Field!, at, v); }
                            else Walk(node.Children, at + ".");
                        }
                        break;
                }
            }
        }
        void AddField(FieldDescriptor field, string path, string value)
        {
            count++;
            var f = field with { Path = path, Label = FullLabel(kind.Fields, path), ReadOnly = field.ReadOnly || !mayChange };
            string command = $"/edit model set {kind.Kind} {modelId} {path} ";
            items.Add(Box("build.field", f, value, command));
            if (f.Type == FieldType.Choice && !f.ReadOnly) items.AddRange(Choices("build.choice", command, f.Choices.Select(c => (c, c))));
        }
        Walk(kind.Fields, "");

        var history = Models.History(kind.Kind, modelId);
        int current = Models.CurrentVersion(kind.Kind, modelId);
        string Marks(int v) => string.Join(" ", new[] { v == current ? "inuse" : "", v == pinned ? "pinned" : "" }.Where(w => w.Length > 0));
        string Said(int v) => (v == current ? ", in use" : "") + (v == pinned ? ", pinned on this map" : "");
        if (history != null)
            foreach (var v in history.Versions.OrderByDescending(v => v.Version))
                items.Add(Line("build.version", $"Version {v.Version}{Said(v.Version)}: {v.Note}, by {v.Author}, {v.SavedUtc.ToLocalTime():d MMMM HH:mm}",
                               v.Version.ToString(CultureInfo.InvariantCulture), prompt: Marks(v.Version)));
        if (history == null || history.Base != null)
            items.Add(Line("build.version", $"Version 0, as built{Said(0)}", "0", prompt: Marks(0)));

        var used = WhereUsed(kind.Kind, modelId);
        foreach (var u in used)
            items.Add(Line("build.where", $"{_maps.DisplayName(u.MapId)}: {Plural(u.Count, "thing")}{(PinOf(u.MapId, kind.Kind, modelId) is int up ? $", pinned at version {up}" : "")}"));
        if (used.Count == 0) items.Add(Line("build.where", "Nothing on a loaded map uses it."));

        foreach (var other in kind.Ids.Where(i => !i.Equals(modelId, StringComparison.OrdinalIgnoreCase) && !Models.IsRetired(kind.Kind, i))
                                      .OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
            items.Add(Line("build.replace", $"{other}: {kind.Name(other)}", other));
        return items;
    }

    // ── World ───────────────────────────────────────────────────────────────────────────────────

    private IEnumerable<EditorMenuItem> DialogWorld(UserSession s)
    {
        var items = new List<EditorMenuItem>();
        if (!_maps.TryGetMapData(s.CurrentMapId, out var d)) return items;

        foreach (var path in new[] { MapSettings.Weather, MapSettings.Hour, MapSettings.Ground })
        {
            var field = MapSettings.Field(path, GroundChoices());
            string now = MapSettings.Get(d, path);
            string word = path == MapSettings.Hour ? "time" : path.ToLowerInvariant();
            string command = $"/edit map set {word} ";
            if (path == MapSettings.Hour)
            {
                string held = now != "server" && MapSettings.TryHour(now, out var hour) && hour is float h ? MapSettings.SayHour(h) : "server";
                items.Add(Box("world.field", field, held, command));
                continue;
            }
            items.Add(Box("world.field", field, now, command));
            var choices = path == MapSettings.Weather
                ? MapSettings.WeatherWords.Select(w => (w == "server" ? "The server's weather" : $"Always {w}", w))
                : field.Choices.Select(g => (_maps.Prefabs.TryGetValue(g, out var p) ? p.Name : g, g));
            items.AddRange(Choices("world.choice", command, choices));
        }

        bool owner = _maps.IsOwner(s.CurrentMapId, s.Username) || s.Can(Permissions.MapsAny);
        var can = new List<string>();
        if (owner) can.Add("editors");
        if (owner && !_maps.IsShipped(s.CurrentMapId))
        {
            can.Add("size");
            var size = TypedNumber("Map size", "/setmapsize ", "size: metres east, north and high", "", MinHeight, MaxSide,
                "Three numbers: east, north and height. The south-west corner stays where it is. Refused if things would be left outside.",
                MapSettings.Get(d, MapSettings.Size), count: 3);
            size.Section = "world.size";
            items.Add(size);
        }
        items.Add(Line("world.can", "", string.Join(" ", can)));

        // Rooms and areas: the named places and rooms, nearest first, as the menu lists them.
        if (_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _) && TryBody(s, _ => { }, out _, out var feet, out float yaw))
            foreach (var c in Candidates(world, s.CurrentMapId, feet).Where(c => world.Has<RegionComponent>(c.E))
                         .OrderBy(c => c.Contains ? 0 : 1).ThenBy(c => c.Distance).ThenBy(c => c.Id).Take(DialogThings))
                items.Add(Line("world.room", $"{NameOf(world, c.E)}, {Where(world, c.E, feet, yaw)}", c.Id.ToString(CultureInfo.InvariantCulture)));

        foreach (var category in Beacons.Categories.Where(c => c != Beacons.Player))
        {
            string path = MapSettings.BeaconPrefix + category;
            var field = MapSettings.Field(path, GroundChoices());
            string command = $"/edit map set beacon {category} ";
            items.Add(Box("world.beacon", field with { Choices = MapSettings.Policies.Select(p => p.Stored).ToArray() }, MapSettings.Get(d, path), command));
            items.AddRange(Choices("world.choice", command, MapSettings.Policies.Select(p => (Capital(p.Words), p.Stored))));
        }

        items.AddRange(DialogVersions(s));
        items.AddRange(DialogLaid(s));

        foreach (var name in d.Editors) items.Add(Line("world.editor", name, name));

        foreach (var (key, version) in Overlays.Get(s.CurrentMapId).Pins)
        {
            var parts = key.Split(':', 2);
            items.Add(Line("world.pin", $"{key} at version {version}", parts.Length == 2 ? $"{parts[0]} {parts[1]}" : key));
        }

        var size2 = d.MaxBound - d.MinBound;
        string ownerWord = string.IsNullOrWhiteSpace(d.OwnerId) ? "the server's" : d.OwnerId.Equals(s.Username, StringComparison.OrdinalIgnoreCase) ? "yours" : $"{d.OwnerId}'s";
        items.Add(Line("world.info", $"{d.DisplayName}, {ownerWord}, {(d.IsPublic ? "public" : "private")}"));
        items.Add(Line("world.info", $"Size {FieldDescriptor.Format(MathF.Round(size2.X))} by {FieldDescriptor.Format(MathF.Round(size2.Z))} metres and {FieldDescriptor.Format(MathF.Round(size2.Y))} high, from {PlayerCoordinates.Format(d.MinBound)} to {PlayerCoordinates.Format(d.MaxBound)}"));
        items.Add(Line("world.info", d.TileMetres > 0 ? $"Streamed in tiles of {FieldDescriptor.Format(d.TileMetres)} metres" : "Sent whole, not in tiles"));
        items.Add(Line("world.info", $"{_maps.AuthoredEntities(s.CurrentMapId).Count} things"));
        items.Add(Line("world.info", $"Spawn point at {PlayerCoordinates.Format(d.SpawnPoint.Position)}, facing {CompassOf(YawOf(d.SpawnPoint.Rotation))}"));
        return items;
    }
}
