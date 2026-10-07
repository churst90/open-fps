using System.Globalization;
using System.Text.Json.Nodes;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;

namespace OpenFPS.Server.Editor;

/// <summary>
/// The library in the world editor (docs/WORLD_EDITOR.md sections 5 and 11.1 to 11.3): changing a model's
/// fields, its versions and the map's pins, where it is used, new models from a template or a copy,
/// replacing one model with another, and retiring one.
/// </summary>
public sealed partial class WorldEditor
{
    public const string ModelsRefusal = "Changing a model changes it on every map, so it needs edit-models: developers and administrators.";

    /// <summary>Every kind of model the editor has: the library's, and the prefabs once the server has them.</summary>
    public ModelCatalog Catalog
    {
        get
        {
            var catalog = Models.Catalog;
            if (catalog.Get(PrefabKind.KindId) == null) catalog.Add(new PrefabKind(_maps.PrefabRepository));
            if (catalog.Get(GroupKind.KindId) == null) catalog.Add(new GroupKind(Models, _maps.PrefabRepository));
            return catalog;
        }
    }

    /// <summary>"compressor hum level": the labels on the way to a field, joined.</summary>
    internal static string FullLabel(IReadOnlyList<FieldNode> root, string path)
    {
        if (!ModelKinds.TryParsePath(path, out var segments)) return path;
        var words = new List<string>();
        IReadOnlyList<FieldNode> level = root;
        foreach (var (name, index) in segments)
        {
            var node = level.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (node == null) return path;
            words.Add(index >= 0 ? $"{node.Label} {index + 1}" : node.Label);
            level = node.Children;
        }
        return string.Join(" ", words);
    }

    internal string ModelName(string kind, string id) => Catalog.Get(kind)?.Name(id) ?? id;

    private static string Plural(int n, string one) => n == 1 ? $"1 {one}" : $"{n} {one}s";

    // ── Where a model is used ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// How many things on each loaded map use a model: a prefab by being made from it, a model by the
    /// sound that names it, an engine by being in a vehicle or a machine that is heard.
    /// </summary>
    internal List<(string MapId, int Count)> WhereUsed(string kind, string id)
    {
        var result = new List<(string, int)>();
        var engineOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool Uses(string? sound)
        {
            if (!ModelKinds.TryModelOfSound(sound, out var k, out var i)) return false;
            if (kind == ModelLibrary.Kinds.Engine)
            {
                if (!engineOf.TryGetValue(k + ":" + i, out var engine))
                {
                    engine = k == ModelLibrary.Kinds.Vehicle ? MachineRegistry.EngineKeyFor(i)
                           : k == ModelLibrary.Kinds.SmallMachine ? ModelLibrary.SmallMachine(i).EngineKey ?? ""
                           : "";
                    engineOf[k + ":" + i] = engine;
                }
                return engine.Equals(id, StringComparison.OrdinalIgnoreCase);
            }
            return k == kind && i.Equals(id, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var (mapId, entry) in _maps.GetAllMaps())
        {
            int n = 0;
            if (kind == PrefabKind.KindId)
                entry.world.Query(new QueryDescription().WithAll<IdentityComponent>(), (ref IdentityComponent ident) =>
                {
                    if (id.Equals(ident.PrefabId, StringComparison.OrdinalIgnoreCase)) n++;
                });
            else
                entry.world.Query(new QueryDescription().WithAll<SoundEmitterComponent>(), (ref SoundEmitterComponent em) =>
                {
                    if (Uses(em.SoundId)) n++;
                });
            if (n > 0) result.Add((mapId, n));
        }
        return result.OrderByDescending(r => r.Item2).ThenBy(r => r.Item1).ToList();
    }

    /// <summary>How many things on this map, and on the others, use a model.</summary>
    internal (int Here, int Elsewhere) UsedBy(string kind, string id, string here)
    {
        int h = 0, other = 0;
        foreach (var (mapId, n) in WhereUsed(kind, id))
            if (mapId.Equals(here, StringComparison.OrdinalIgnoreCase)) h += n; else other += n;
        return (h, other);
    }

    // ── Versions and pins ───────────────────────────────────────────────────────────────────────

    /// <summary>The version a map pins a model at, or null.</summary>
    internal int? PinOf(string mapId, string kind, string id)
        => Overlays.Get(mapId).Pins.TryGetValue(ModelStore.PinKey(kind, id), out int v) ? v : null;

    /// <summary>The version of a model that a map uses: its pin, or the current one.</summary>
    internal int VersionOn(string mapId, string kind, string id) => PinOf(mapId, kind, id) ?? Models.CurrentVersion(kind, id);

    /// <summary>A model's new current version, to every game client on a map that does not pin it.</summary>
    private void SendModel(ModelUpdate update)
    {
        if (Catalog.Get(update.Kind) is not { ToClients: true }) return;
        foreach (var session in _sessions.GetAllSessions().ToList())
        {
            if (session.IsTextClient || PinOf(session.CurrentMapId, update.Kind, update.Id) != null) continue;
            _server.SendToSession(session, update);
        }
    }

    /// <summary>After a pin is set or lifted: the players on the map are sent the version it now uses.</summary>
    private void SendModelToMap(string mapId, string kind, string id)
    {
        if (Catalog.Get(kind) is not { ToClients: true }) return;
        int version = VersionOn(mapId, kind, id);
        if (Models.SpecJson(kind, id, version) is not { } json) return;
        var update = new ModelUpdate { Kind = kind, Id = id, Version = version, SpecJson = json };
        foreach (var session in _sessions.GetSessionsInMap(mapId).ToList())
            if (!session.IsTextClient) _server.SendToSession(session, update);
    }

    /// <summary>Puts a model's version in use after a change: to the clients, and for a prefab, the things made from it made again.</summary>
    private void Publish(ModelUpdate update)
    {
        SendModel(update);
        if (update.Kind == PrefabKind.KindId) RemakeAll(update.Id);
    }

    private string VersionWords(string kind, string id, int version)
    {
        if (version == 0) return "version 0, as built";
        var v = Models.History(kind, id)?.Versions.FirstOrDefault(x => x.Version == version);
        return v == null ? $"version {version}" : $"version {version}, {v.Note}";
    }

    // ── The commands ────────────────────────────────────────────────────────────────────────────

    public const string ModelUsage =
        "Say /edit model show KIND ID, set KIND ID FIELD VALUE, up|down KIND ID FIELD, versions KIND ID, where KIND ID, "
        + "use KIND ID VERSION, pin KIND ID VERSION, unpin KIND ID, new KIND TEMPLATE NEWID, copy KIND ID NEWID, "
        + "replace KIND ID with OTHER [here|everywhere], retire KIND ID, or restore KIND ID.";

    private static readonly HashSet<string> ModelVerbs = new(StringComparer.OrdinalIgnoreCase)
        { "show", "set", "up", "down", "versions", "where", "use", "pin", "unpin", "new", "copy", "replace", "retire", "restore", "remove" };

    private void ModelCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (args.Length < 3 || !ModelVerbs.Contains(verb)) { Say(reply, ModelUsage); return; }
        var kind = Catalog.Named(args[1]);
        if (kind == null) { Say(reply, $"There is no kind of model called {args[1]}. Kinds: {string.Join(", ", Catalog.All.Select(k => k.Kind).OrderBy(k => k))}."); return; }
        if (kind.Canonical(args[2]) is not { } id) { Say(reply, $"There is no {kind.Spoken} called {args[2]}."); return; }
        string spoken = $"the {kind.Spoken} {id}";

        switch (verb)
        {
            case "show":
            {
                var (here, elsewhere) = UsedBy(kind.Kind, id, s.CurrentMapId);
                string pin = PinOf(s.CurrentMapId, kind.Kind, id) is int p ? $" This map pins it at version {p}." : "";
                Say(reply, $"{kind.Name(id)}: {spoken}, version {Models.CurrentVersion(kind.Kind, id)}"
                         + (Models.IsRetired(kind.Kind, id) ? ", retired" : "") + ". "
                         + $"Used by {Plural(here, "thing")} on this map and {elsewhere} on others.{pin}");
                return;
            }
            case "versions":
            {
                var h = Models.History(kind.Kind, id);
                if (h == null || h.Versions.Count == 0) { Say(reply, $"{Capital(spoken)} has not been changed: version 0, as built."); return; }
                int? pinned = PinOf(s.CurrentMapId, kind.Kind, id);
                Say(reply, string.Join(" ", h.Versions.OrderByDescending(v => v.Version).Select(v =>
                    $"Version {v.Version} by {v.Author}, {v.SavedUtc.ToLocalTime():d MMMM HH:mm}: {v.Note}{(v.Version == h.Current ? ", in use" : "")}{(v.Version == pinned ? ", pinned here" : "")}."))
                    + (h.Base != null ? $" Version 0, as built{(h.Current == 0 ? ", in use" : "")}{(pinned == 0 ? ", pinned here" : "")}." : ""));
                return;
            }
            case "where":
            {
                var used = WhereUsed(kind.Kind, id);
                Say(reply, used.Count == 0 ? $"Nothing on a loaded map uses {spoken}."
                    : $"{Capital(spoken)} is used on " + string.Join("; ", used.Select(u => $"{_maps.DisplayName(u.MapId)}, {Plural(u.Count, "thing")}")) + ".");
                return;
            }
            case "pin":
            case "unpin":
                Pin(s, kind, id, verb == "unpin" ? null : args.Length > 3 ? args[3] : "", reply);
                return;
            case "replace":
                Replace(s, kind, id, args[3..], reply);
                return;
        }

        if (!s.Can(Permissions.EditModels)) { Say(reply, ModelsRefusal); return; }
        switch (verb)
        {
            case "use":
                UseVersion(s, kind, id, args.Length > 3 ? args[3] : "", reply);
                return;
            case "new":
            case "copy":
                MakeModel(s, kind, id, args.Length > 3 ? args[3] : "", fromBuiltIn: verb == "new", reply);
                return;
            case "retire":
            case "restore":
                Retire(s, kind, id, verb == "retire", reply);
                return;
            case "remove":
                RemoveItem(s, kind, id, args.Length > 3 ? args[3] : "", reply);
                return;
        }
        if (args.Length < 4 || (verb == "set" && args.Length < 5))
        { Say(reply, verb == "set" ? "Say /edit model set KIND ID FIELD VALUE." : $"Say /edit model {verb} KIND ID FIELD."); return; }
        int step = verb == "up" ? 1 : verb == "down" ? -1 : 0;
        SetModelField(s, kind, id, args[3], step == 0 ? string.Join(" ", args[4..]) : null, step, reply);
    }

    private void SetModelField(UserSession s, EditorKind kind, string id, string path, string? typed, int step, Action<IMessage> reply)
    {
        var node = ModelKinds.NodeAt(kind.Fields, path, out var field);
        string spoken = $"the {kind.Spoken} {id}";
        if (field == null || node == null) { Say(reply, $"{Capital(spoken)} has no field {path}."); return; }
        string label = FullLabel(kind.Fields, field.Path);
        field = field with { Label = label };
        var root = JsonNode.Parse(kind.CurrentJson(id))!;
        string? before = ModelKinds.GetValue(root, field.Path, node);
        if (before == null && field.Type is FieldType.Number or FieldType.Integer && step != 0) { Say(reply, $"{Capital(spoken)} has no {label}: that part is not in it."); return; }
        string want = typed ?? field.Stepped(before ?? "", step) ?? before ?? "";
        if (!field.TryParse(want, out string value, out string error)) { Say(reply, error); return; }
        if (before != null && SameValue(before, value)) { Say(reply, $"{Capital(label)} is already {field.Say(value)}{(step != 0 ? ", the end of its range" : "")}."); return; }
        if (!ModelKinds.TrySet(root, field.Path, value, field, node, out error)) { Say(reply, error); return; }

        int beforeVersion = Models.CurrentVersion(kind.Kind, id);
        ModelUpdate update;
        try { update = Models.Commit(kind.Kind, id, root.ToJsonString(), s.Username, $"{field.Path} {(before == null ? "none" : field.Say(before))} to {field.Say(value)}"); }
        catch (Exception ex) { Say(reply, $"Not changed: {Reason(ex)}"); return; }
        Publish(update);
        Push(s, new ModelOp(s.CurrentMapId, kind.Kind, id, label, beforeVersion, update.Version));
        string pinned = PinOf(s.CurrentMapId, kind.Kind, id) is int p ? $" This map pins version {p}, so it is not heard here." : "";
        Say(reply, $"{Capital(label)} of {spoken}, {field.Say(value)}. Version {update.Version}, on every map.{pinned}");
        Notify(s, $"{s.Username} changed the {label} of {spoken}.");
        Refresh(s, reply);
    }

    /// <summary>/edit model remove KIND ID LIST[N]: one item of a list (a fountain's fall, a group's part) taken out, a new version.</summary>
    private void RemoveItem(UserSession s, EditorKind kind, string id, string path, Action<IMessage> reply)
    {
        string spoken = $"the {kind.Spoken} {id}";
        if (!ModelKinds.TryParsePath(path, out var segments) || segments[^1].Index < 0)
        { Say(reply, "Say /edit model remove KIND ID LIST[NUMBER], counting from 0: Parts[2] is the third part."); return; }
        var node = ModelKinds.NodeAt(kind.Fields, path, out _);
        if (node == null || node.Kind != FieldNodeKind.List) { Say(reply, $"{Capital(spoken)} has no list {path}."); return; }
        var root = JsonNode.Parse(kind.CurrentJson(id))!;
        string listPath = path[..path.LastIndexOf('[')];
        if (ModelKinds.Get(root, listPath) is not JsonArray arr || segments[^1].Index >= arr.Count) { Say(reply, $"{Capital(spoken)} has no item {segments[^1].Index + 1} there."); return; }
        if (arr.Count == 1) { Say(reply, $"That is the only one; {spoken} keeps at least one."); return; }
        int index = segments[^1].Index;
        arr.RemoveAt(index);
        string label = $"{FullLabel(kind.Fields, listPath)} {index + 1}";
        int beforeVersion = Models.CurrentVersion(kind.Kind, id);
        ModelUpdate update;
        try { update = Models.Commit(kind.Kind, id, root.ToJsonString(), s.Username, $"took out {label}"); }
        catch (Exception ex) { Say(reply, $"Not changed: {Reason(ex)}"); return; }
        Publish(update);
        Push(s, new ModelOp(s.CurrentMapId, kind.Kind, id, label, beforeVersion, update.Version));
        Say(reply, $"Took {label} out of {spoken}. Version {update.Version}; {arr.Count} left.");
        Notify(s, $"{s.Username} took {label} out of {spoken}.");
        if (!s.IsTextClient) SendMenu(s, $"model:{kind.Kind}:{id}:{listPath}", reply, refresh: false);
    }

    /// <summary>Why a model would not be kept, in words: the first line of what refused it.</summary>
    private static string Reason(Exception ex)
    {
        string m = ex is System.Text.Json.JsonException ? "it would not read back as that kind of model." : ex.Message;
        return m.Split('\n')[0];
    }

    private static bool TryVersion(string word, out int version)
        => int.TryParse(word.TrimStart('v', 'V'), NumberStyles.Integer, CultureInfo.InvariantCulture, out version) && version >= 0;

    /// <summary>/edit model use KIND ID VERSION: that version is the current one, on every map that does not pin another.</summary>
    private void UseVersion(UserSession s, EditorKind kind, string id, string word, Action<IMessage> reply)
    {
        if (!TryVersion(word, out int version)) { Say(reply, "Say /edit model use KIND ID VERSION: a version number, 0 for as built."); return; }
        int before = Models.CurrentVersion(kind.Kind, id);
        if (before == version) { Say(reply, $"The {kind.Spoken} {id} is already at version {version}."); return; }
        if (!Models.HasVersion(kind.Kind, id, version) || Models.History(kind.Kind, id) == null)
        { Say(reply, $"The {kind.Spoken} {id} has no version {version}."); return; }
        var update = Models.SetCurrent(kind.Kind, id, version);
        if (update == null) { Say(reply, $"The {kind.Spoken} {id} has no version {version}."); return; }
        Publish(update);
        Push(s, new ModelOp(s.CurrentMapId, kind.Kind, id, "version in use", before, version));
        Say(reply, $"The {kind.Spoken} {id} is at {VersionWords(kind.Kind, id, version)}, on every map that does not pin it.");
        Notify(s, $"{s.Username} put the {kind.Spoken} {id} back to version {version}.");
        Refresh(s, reply);
    }

    /// <summary>/edit model pin KIND ID VERSION and unpin: the map holds a model at a version of its own.</summary>
    private void Pin(UserSession s, EditorKind kind, string id, string? word, Action<IMessage> reply)
    {
        if (!kind.ToClients) { Say(reply, $"A {kind.Spoken} is the server's, one for every map, so it cannot be pinned on one map."); return; }
        int? before = PinOf(s.CurrentMapId, kind.Kind, id);
        int? after = null;
        if (word != null)
        {
            if (!TryVersion(word, out int v)) { Say(reply, "Say /edit model pin KIND ID VERSION: a version number, 0 for as built."); return; }
            if (!Models.HasVersion(kind.Kind, id, v) || (v > 0 && Models.History(kind.Kind, id) == null)) { Say(reply, $"The {kind.Spoken} {id} has no version {v}."); return; }
            after = v;
        }
        if (before == after) { Say(reply, after == null ? $"This map does not pin the {kind.Spoken} {id}." : $"This map already pins it at version {after}."); return; }
        ApplyPin(s.CurrentMapId, kind.Kind, id, after);
        Push(s, new PinOp(s.CurrentMapId, kind.Kind, id, before, after));
        Say(reply, after is int a
            ? $"This map uses the {kind.Spoken} {id} at {VersionWords(kind.Kind, id, a)}, whatever the other maps use. What the server simulates with it stays the current version."
            : $"This map uses the {kind.Spoken} {id} at the current version, {Models.CurrentVersion(kind.Kind, id)}, again.");
        Notify(s, $"{s.Username} {(after == null ? "lifted the pin on" : "pinned")} the {kind.Spoken} {id}.");
        Refresh(s, reply);
    }

    private void ApplyPin(string mapId, string kind, string id, int? version)
    {
        var pins = Overlays.Get(mapId).Pins;
        if (version is int v) pins[ModelStore.PinKey(kind, id)] = v;
        else pins.Remove(ModelStore.PinKey(kind, id));
        Overlays.Save(mapId);
        SendModelToMap(mapId, kind, id);
    }

    /// <summary>/edit model new KIND TEMPLATE NEWID (as built) and copy KIND ID NEWID (as it is now): a model of one's own.</summary>
    private void MakeModel(UserSession s, EditorKind kind, string source, string newId, bool fromBuiltIn, Action<IMessage> reply)
    {
        if (newId.Length == 0 || !ModelStore.IsSafeId(newId))
        { Say(reply, $"Say /edit model {(fromBuiltIn ? "new KIND TEMPLATE NEWID" : "copy KIND ID NEWID")}: the new id is letters, digits, _ and -, up to 64."); return; }
        if (kind.Knows(newId)) { Say(reply, $"There is already a {kind.Spoken} called {newId}."); return; }
        string? json = fromBuiltIn ? kind.BuiltInJson(source) : kind.CurrentJson(source);
        if (json == null) { Say(reply, $"The {kind.Spoken} {source} was made in the editor, so it has no as-built version to start from. Copy it instead."); return; }
        if (fromBuiltIn && Models.IsRetired(kind.Kind, source)) { Say(reply, $"The {kind.Spoken} {source} is retired, so it is not offered as a template."); return; }
        var node = JsonNode.Parse(kind.Renamed(json, newId))!;
        string name = Capital(newId.Replace('_', ' ').Replace('-', ' '));
        if (node is JsonObject o && o.ContainsKey("Name")) o["Name"] = name;
        string note = fromBuiltIn ? $"made from {source}, as built" : $"copied from {source}, version {Models.CurrentVersion(kind.Kind, source)}";
        ModelUpdate update;
        try { update = Models.Commit(kind.Kind, newId, node.ToJsonString(), s.Username, note, madeFrom: $"{kind.Kind}:{source}"); }
        catch (Exception ex) { Say(reply, $"Not made: {Reason(ex)}"); return; }
        SendModel(update);
        Push(s, new CreateOp(s.CurrentMapId, kind.Kind, newId));
        string use = kind.Kind == PrefabKind.KindId ? "Place it from Place, or /edit place " + newId + "."
                   : kind.Kind == ModelLibrary.Kinds.Engine ? "Give a vehicle it with the vehicle's engine field."
                   : kind.Kind == ModelLibrary.Kinds.Vehicle ? "/give vehicle " + newId + " parks one beside you."
                   : "Give a thing it with its model setting, or replace another model with it.";
        Say(reply, $"Made the {kind.Spoken} {newId}, {note}. Version 1. {use}");
        Notify(s, $"{s.Username} made the {kind.Spoken} {newId}.");
        Refresh(s, reply);
    }

    private void Retire(UserSession s, EditorKind kind, string id, bool retire, Action<IMessage> reply)
    {
        if (!Models.SetRetired(kind.Kind, id, retire))
        { Say(reply, retire ? $"The {kind.Spoken} {id} is already retired." : $"The {kind.Spoken} {id} is not retired."); return; }
        Push(s, new RetireOp(s.CurrentMapId, kind.Kind, id, retire));
        var (here, elsewhere) = UsedBy(kind.Kind, id, s.CurrentMapId);
        Say(reply, retire
            ? $"Retired the {kind.Spoken} {id}: it is no longer offered for new things."
              + ((here + elsewhere) switch { 0 => " Nothing on a loaded map uses it.", 1 => " The one thing using it keeps it.", int n => $" The {n} things using it keep it." })
            : $"The {kind.Spoken} {id} is offered again.");
        Notify(s, $"{s.Username} {(retire ? "retired" : "brought back")} the {kind.Spoken} {id}.");
        Refresh(s, reply);
    }

    // ── Replacing one model with another ────────────────────────────────────────────────────────

    /// <summary>/edit model replace KIND ID with OTHER [here|everywhere]: every thing using one model uses another.</summary>
    private void Replace(UserSession s, EditorKind kind, string id, string[] rest, Action<IMessage> reply)
    {
        var words = rest.Where(w => !w.Equals("with", StringComparison.OrdinalIgnoreCase)).ToList();
        if (words.Count == 0) { Say(reply, "Say /edit model replace KIND ID with OTHER, and here or everywhere (here if not said)."); return; }
        if (kind.Canonical(words[0]) is not { } other) { Say(reply, $"There is no {kind.Spoken} called {words[0]}."); return; }
        if (other.Equals(id, StringComparison.OrdinalIgnoreCase)) { Say(reply, "That is the same model."); return; }
        if (Models.IsRetired(kind.Kind, other)) { Say(reply, $"The {kind.Spoken} {other} is retired, so it is not offered for new things."); return; }
        if (kind.Kind == ModelLibrary.Kinds.Engine) { Say(reply, "An engine is chosen in each vehicle's engine field; there are no things that play an engine on their own."); return; }
        bool everywhere = words.Count > 1 && words[1].Equals("everywhere", StringComparison.OrdinalIgnoreCase);
        if (everywhere && !s.Can(Permissions.EditModels)) { Say(reply, "Replacing a model on every map needs edit-models. /edit model replace KIND ID with OTHER here does this map."); return; }
        var maps = everywhere ? _maps.GetAllMaps().Select(m => m.Key).ToList() : new List<string> { s.CurrentMapId };

        var ops = new List<EditOp>();
        int count = 0, refused = 0;
        foreach (string mapId in maps)
        {
            if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) continue;
            foreach (var (thingId, e) in _maps.AuthoredEntities(mapId).ToList())
            {
                if (!Editable(world, e)) continue;
                if (kind.Kind == PrefabKind.KindId)
                {
                    if (!id.Equals(PrefabOf(world, e), StringComparison.OrdinalIgnoreCase)) continue;
                    if (SwapPrefab(mapId, world, e, thingId, other, out var swap)) { ops.AddRange(swap); count++; }
                    else refused++;
                    continue;
                }
                if (!world.Has<SoundEmitterComponent>(e)) continue;
                if (!ModelKinds.TryModelOfSound(world.Get<SoundEmitterComponent>(e).SoundId, out var k, out var i)
                    || k != kind.Kind || !i.Equals(id, StringComparison.OrdinalIgnoreCase)) continue;
                var setting = EntitySettings.Named(EntitySettings.ModelPath)!;
                string before = setting.Get(world, e);
                string name = NameOf(world, e);
                ApplySetting(mapId, thingId, world, e, setting, other);
                ops.Add(new SetOp(mapId, thingId, name, setting.Field.Path, setting.Field.Label, before, other));
                count++;
            }
        }
        if (count == 0) { Say(reply, $"Nothing {(everywhere ? "on a loaded map" : "on this map")} uses the {kind.Spoken} {id}{(refused > 0 ? $"; {refused} could not be changed" : "")}."); return; }
        Push(s, new BatchOp(s.CurrentMapId, ops, $"replaced the {kind.Spoken} {id} with {other}"));
        Say(reply, $"Replaced the {kind.Spoken} {id} with {other} on {Plural(count, "thing")}{(everywhere ? " on every loaded map" : " on this map")}."
                 + (refused > 0 ? $" {refused} could not be changed: somebody is standing where the new one would be." : "") + " One undo puts them all back.");
        Notify(s, $"{s.Username} replaced the {kind.Spoken} {id} with {other}.");
        Refresh(s, reply);
    }

    /// <summary>One thing made from one prefab made from another, where it stands, with its name: a delete and a place.</summary>
    private bool SwapPrefab(string mapId, World world, Entity e, int id, string prefab, out List<EditOp> ops)
    {
        ops = new List<EditOp>();
        string name = NameOf(world, e);
        var thing = Take(mapId, world, e, id);
        var o = Overlays.Get(mapId);
        int newId = o.NextId++;
        var data = MapOverlayStore.Clone(thing.Data);
        data.EntityId = newId;
        data.PrefabId = _maps.Prefabs.TryGetValue(prefab.ToLowerInvariant(), out var t) ? t.Id : prefab;
        data.Tile = null;
        Remove(mapId, world, e, id);
        var placed = new Snapshot(newId, data, thing.Settings, Added: true, Change: null, Was: data.Position);
        if (!Restore(mapId, placed, out _))
        {
            o.NextId--;
            Restore(mapId, thing, out _);
            return false;
        }
        ops.Add(new DeleteOp(mapId, thing, name));
        ops.Add(new PlaceOp(mapId, placed, "placed", t?.Name ?? prefab));
        return true;
    }
}
