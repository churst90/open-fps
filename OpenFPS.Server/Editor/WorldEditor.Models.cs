using System.Text.Json.Nodes;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;

namespace OpenFPS.Server.Editor;

public sealed partial class WorldEditor
{
    public const string ModelsRefusal = "Changing a model changes it on every map, so it needs edit-models: developers and administrators.";

    /// <summary>A kind as typed: its id ("small_machine") or its spoken name ("machine").</summary>
    internal static string? KindNamed(string word)
    {
        foreach (var kind in ModelLibrary.AllKinds)
            if (kind.Equals(word, StringComparison.OrdinalIgnoreCase)
                || ModelKinds.Spoken(kind).Replace(' ', '_').Equals(word.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase))
                return kind;
        return null;
    }

    /// <summary>"compressor hum level": the labels on the way to a field, joined.</summary>
    internal static string FullLabel(Type type, string path)
    {
        if (!ModelKinds.TryParsePath(path, out var segments)) return path;
        var words = new List<string>();
        IReadOnlyList<FieldNode> level = ModelKinds.Describe(type);
        foreach (var (name, index) in segments)
        {
            var node = level.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (node == null) return path;
            words.Add(index >= 0 ? $"{node.Label} {index + 1}" : node.Label);
            level = node.Children;
        }
        return string.Join(" ", words);
    }

    /// <summary>The model's own name, when it has one ("Condenser unit, 3 ton").</summary>
    internal static string ModelName(string kind, string id)
    {
        try
        {
            var node = JsonNode.Parse(ModelLibrary.SpecJson(ModelLibrary.Model(kind, id)));
            if (node is JsonObject o && o.TryGetPropertyValue("Name", out var n) && n is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
                return s;
        }
        catch (Exception) { }
        return id;
    }

    /// <summary>How many things on each loaded map play this model.</summary>
    internal (int Here, int Elsewhere) UsedBy(string kind, string id, string here)
    {
        int h = 0, other = 0;
        foreach (var (mapId, entry) in _maps.GetAllMaps())
        {
            int n = 0;
            entry.world.Query(new QueryDescription().WithAll<SoundEmitterComponent>(), (ref SoundEmitterComponent em) =>
            {
                if (ModelKinds.TryModelOfSound(em.SoundId, out var k, out var i) && k == kind && i.Equals(id, StringComparison.OrdinalIgnoreCase)) n++;
            });
            if (mapId.Equals(here, StringComparison.OrdinalIgnoreCase)) h += n; else other += n;
        }
        return (h, other);
    }

    private void ModelCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (args.Length < 3 || verb is not ("show" or "set" or "up" or "down" or "versions"))
        { Say(reply, "Say /edit model show KIND ID, /edit model set KIND ID FIELD VALUE, /edit model up|down KIND ID FIELD, or /edit model versions KIND ID."); return; }
        string? kind = KindNamed(args[1]);
        if (kind == null) { Say(reply, $"There is no kind of model called {args[1]}. Kinds: {string.Join(", ", ModelLibrary.AllKinds)}."); return; }
        string id = args[2];
        if (!ModelLibrary.Knows(kind, id)) { Say(reply, $"There is no {ModelKinds.Spoken(kind)} called {id}."); return; }
        id = ModelLibrary.Ids(kind).First(i => i.Equals(id, StringComparison.OrdinalIgnoreCase));

        switch (verb)
        {
            case "show":
            {
                var (here, elsewhere) = UsedBy(kind, id, s.CurrentMapId);
                Say(reply, $"{ModelName(kind, id)}: the {ModelKinds.Spoken(kind)} {id}, version {Models.CurrentVersion(kind, id)}. "
                         + $"Used by {here} thing{(here == 1 ? "" : "s")} on this map and {elsewhere} on others.");
                return;
            }
            case "versions":
            {
                var h = Models.History(kind, id);
                if (h == null || h.Versions.Count == 0) { Say(reply, $"The {ModelKinds.Spoken(kind)} {id} has not been changed: version 0, as built."); return; }
                Say(reply, string.Join(" ", h.Versions.OrderByDescending(v => v.Version).Select(v =>
                    $"Version {v.Version} by {v.Author}, {v.SavedUtc:d MMMM HH:mm}: {v.Note}{(v.Version == h.Current ? ", in use" : "")}."))
                    + (h.Current == 0 ? " Version 0, as built, is in use." : ""));
                return;
            }
        }

        if (!s.Can(Permissions.EditModels)) { Say(reply, ModelsRefusal); return; }
        if (args.Length < 4 || (verb == "set" && args.Length < 5))
        { Say(reply, verb == "set" ? "Say /edit model set KIND ID FIELD VALUE." : $"Say /edit model {verb} KIND ID FIELD."); return; }
        int step = verb == "up" ? 1 : verb == "down" ? -1 : 0;
        SetModelField(s, kind, id, args[3], step == 0 ? string.Join(" ", args[4..]) : null, step, reply);
    }

    private void SetModelField(UserSession s, string kind, string id, string path, string? typed, int step, Action<IMessage> reply)
    {
        var type = ModelLibrary.TypeOf(kind)!;
        var node = ModelKinds.NodeAt(type, path, out var field);
        string spoken = $"the {ModelKinds.Spoken(kind)} {id}";
        if (field == null || node == null) { Say(reply, $"{Capital(spoken)} has no field {path}."); return; }
        string label = FullLabel(type, field.Path);
        field = field with { Label = label };
        var root = JsonNode.Parse(Models.CurrentJson(kind, id))!;
        string? before = ModelKinds.GetValue(root, field.Path, node);
        if (before == null) { Say(reply, $"{Capital(spoken)} has no {label}: that part is not in it."); return; }
        string want = typed ?? field.Stepped(before, step) ?? before;
        if (!field.TryParse(want, out string value, out string error)) { Say(reply, error); return; }
        if (SameValue(before, value)) { Say(reply, $"{Capital(label)} is already {field.Say(value)}{(step != 0 ? ", the end of its range" : "")}."); return; }
        if (!ModelKinds.TrySet(root, field.Path, value, field, node, out error)) { Say(reply, error); return; }

        int beforeVersion = Models.CurrentVersion(kind, id);
        ModelUpdate update;
        try { update = Models.Commit(kind, id, root.ToJsonString(), s.Username, $"{field.Path} {field.Say(before)} to {field.Say(value)}"); }
        catch (Exception ex) { Say(reply, $"Not changed: the model would not read back ({ex.Message})."); return; }
        _server.BroadcastModel(update);
        Push(s, new ModelOp(s.CurrentMapId, kind, id, label, beforeVersion, update.Version));
        Say(reply, $"{Capital(label)} of {spoken}, {field.Say(value)}. Version {update.Version}, on every map.");
        Notify(s, $"{s.Username} changed the {label} of {spoken}.");
        Refresh(s, reply);
    }
}
