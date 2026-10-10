using System.Globalization;
using System.Numerics;
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
/// Things from the map file that the editor changed or removed (docs/WORLD_EDITOR.md section 18): the
/// overlay's Changed and Removed, listed with what was changed and where they are from you, filtered
/// by words, and each put back as the map file has it, undoably. /edit changed, /edit putback #ID, and
/// the F12 dialog's Edit tab beside "Placed on this map".
/// </summary>
public sealed partial class WorldEditor
{
    /// <summary>One thing from the map file the editor has changed or removed, as the list says it.</summary>
    internal sealed record ChangedThing(int Id, string Name, string Kind, string What, string Where, float Distance, bool Removed)
    {
        /// <summary>"Concrete Wall, moved 2 metres east, 4 metres north west" or "Ground, removed, it was 3 metres south".</summary>
        public string Label
        {
            get
            {
                string what = Name.Equals(Kind, StringComparison.OrdinalIgnoreCase) ? Name : $"{Name}, {Kind}";
                return Removed ? $"{what}, removed, it was {Where}" : $"{what}, {What}, {Where}";
            }
        }
    }

    /// <summary>A prefab's own name, or its id if the server has no such prefab.</summary>
    private string KindName(string prefab)
        => _maps.Prefabs.TryGetValue(prefab.ToLowerInvariant(), out var t) ? t.Name : prefab;

    /// <summary>Where a point is from you, across the map: metres and a compass word, and up or down.</summary>
    private static string PointWhere(Vector3 at, Vector3 feet)
    {
        var flat = new Vector2(at.X - feet.X, at.Z - feet.Z);
        float d = flat.Length();
        string where = d < 0.5f ? "here" : $"{Metres(d >= 10f ? MathF.Round(d) : MathF.Round(d, 1))} {CompassOf(MathF.Atan2(flat.X, flat.Y))}";
        if (at.Y > feet.Y + 2.5f) where += $", {Metres(MathF.Round(at.Y - feet.Y))} up";
        else if (at.Y < feet.Y - 2.5f) where += $", {Metres(MathF.Round(feet.Y - at.Y))} down";
        return where;
    }

    /// <summary>What the editor changed of a thing from the map file, in a few words: "moved 2 metres east,
    /// turned 90 degrees clockwise, volume 0.8".</summary>
    private static string WhatChanged(World world, Entity e, OverlayChange c)
    {
        var parts = new List<string>();
        var by = c.Position - c.Was;
        if (by.Length() >= 0.01f) parts.Add($"moved {Offset(by)}");
        if (c.WasRotation is { } wasTurn)
        {
            float degrees = (YawOf(c.Rotation) - YawOf(wasTurn)) * 180f / MathF.PI;
            degrees = ((degrees + 180f) % 360f + 360f) % 360f - 180f;
            if (MathF.Abs(degrees) >= 0.5f)
                parts.Add($"turned {FieldDescriptor.Format(MathF.Round(MathF.Abs(degrees)))} degrees {(degrees > 0 ? "clockwise" : "anticlockwise")}");
        }
        if (c.WasScale is { } wasScale && Vector3.Distance(wasScale, c.Scale) > 1e-4f && world.Has<ColliderComponent>(e))
        {
            var z = world.Get<ColliderComponent>(e).Size;
            parts.Add($"resized to {FieldDescriptor.Format(MathF.Round(z.X, 2))} by {FieldDescriptor.Format(MathF.Round(z.Z, 2))} metres and {FieldDescriptor.Format(MathF.Round(z.Y, 2))} high");
        }
        if (c.Settings != null)
            foreach (var (path, value) in c.Settings)
            {
                var setting = EntitySettings.Named(path);
                parts.Add($"{setting?.Field.Label ?? path} {setting?.Field.Say(value) ?? value}");
            }
        return parts.Count == 0 ? "changed" : string.Join(", ", parts);
    }

    /// <summary>
    /// Things from the map file on the map you are on that the editor changed or removed, matching a
    /// filter, nearest first; <paramref name="total"/> is how many there are in all. A change whose thing
    /// the map file no longer has where it had it is left out and counted in <paramref name="lost"/>.
    /// </summary>
    internal List<ChangedThing> ChangedList(UserSession s, string filter, out int total, out int lost)
    {
        total = 0; lost = 0;
        var list = new List<ChangedThing>();
        if (!_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _) || !TryBody(s, _ => { }, out _, out var feet, out float yaw)) return list;
        var authored = _maps.AuthoredEntities(s.CurrentMapId);
        var o = Overlays.Get(s.CurrentMapId);
        var (words, within) = PlacedFilterOf(filter);
        bool Keep(string hay, float distance)
            => (within is not float r || distance <= r) && words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase));
        foreach (var c in o.Changed)
        {
            if (!authored.TryGetValue(c.Id, out var e) || !world.IsAlive(e) || !world.Has<Transform>(e)) { lost++; continue; }
            total++;
            string name = NameOf(world, e), kind = KindName(c.Prefab), what = WhatChanged(world, e, c);
            float distance = Reach(world, e, feet).Distance;
            if (!Keep($"{name} {kind} {c.Prefab.Replace('_', ' ')} {what} changed #{c.Id}", distance)) continue;
            list.Add(new ChangedThing(c.Id, name, kind, what, FarWhere(world, e, feet, yaw), distance, Removed: false));
        }
        foreach (var r in o.Removed)
        {
            total++;
            string kind = KindName(r.Prefab), name = r.Name is { Length: > 0 } n ? n : kind;
            float distance = new Vector2(r.Was.X - feet.X, r.Was.Z - feet.Z).Length();
            if (!Keep($"{name} {kind} {r.Prefab.Replace('_', ' ')} removed deleted #{r.Id}", distance)) continue;
            list.Add(new ChangedThing(r.Id, name, kind, "removed", PointWhere(r.Was, feet), distance, Removed: true));
        }
        return list.OrderBy(t => t.Distance).ThenBy(t => t.Id).ToList();
    }

    /// <summary>The list's headline: how many, and what the filter kept.</summary>
    private static string ChangedSummary(int shown, int total, int lost, string filter)
    {
        string gone = lost > 0 ? $"; {Plural(lost, "change")} the map file no longer matches, kept in case it does again" : "";
        if (total == 0) return "Nothing from the map file has been changed or removed with the editor" + gone;
        string said = filter.Length == 0 ? $"{Plural(total, "thing")} from the map file changed or removed, nearest first"
                                         : $"{shown} of {total} changed or removed match {filter}, nearest first";
        return (shown > PlacedListed ? $"{said}, the nearest {PlacedListed} listed" : said) + gone;
    }

    /// <summary>/edit changed [WORDS]: the things from the map file changed or removed here, nearest first,
    /// matching every word ("removed", "moved", a name; "within 20"). With "dialog" on the end, only the
    /// dialog's Edit tab answers.</summary>
    private void ChangedCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        bool dialog = args.Length > 0 && args[^1].Equals("dialog", StringComparison.OrdinalIgnoreCase);
        if (dialog) args = args[..^1];
        var hand = HandOf(s);
        string filter = string.Join(" ", args).Trim();
        hand.ChangedFilter = filter;
        if (dialog)
        {
            if (hand.Dialog && !s.IsTextClient) reply(DialogTab(s, hand.DialogTab));
            return;
        }
        if (!s.IsTextClient) { SendMenu(s, "changed", reply, refresh: false); return; }
        var list = ChangedList(s, filter, out int total, out int lost);
        if (total == 0) { Say(reply, ChangedSummary(0, 0, lost, "") + "."); return; }
        if (list.Count == 0) { Say(reply, $"Nothing changed here matches {filter}. {Plural(total, "thing")} changed or removed in all."); return; }
        const int Said = 30;
        Say(reply, $"{ChangedSummary(list.Count, total, lost, filter)}: " + string.Join("; ", list.Take(Said).Select(t => $"#{t.Id} {t.Label}"))
                 + (list.Count > Said ? $"; and {list.Count - Said} more" : "")
                 + ". /edit putback #NUMBER puts one back as the map has it; /edit goto #NUMBER takes you to it.");
    }

    private EditorMenu ChangedMenu(UserSession s, string filter)
    {
        var list = ChangedList(s, filter, out int total, out int lost);
        var items = new List<EditorMenuItem>
        {
            Typed(filter.Length == 0 ? "Filter, typed" : $"Filter, now {filter}, typed", "/edit changed ", "words to filter by",
                  "Words in a name or a kind, or what was done: moved, turned, removed. within 20 keeps those within 20 metres. Nothing typed lists them all.", filter),
        };
        items.AddRange(list.Take(PlacedListed).Select(t => Opens(t.Label, $"changedone:{t.Id}")));
        if (list.Count == 0) items.Add(Info(total == 0 ? ChangedSummary(0, 0, lost, "") + "." : $"Nothing matches {filter}."));
        return Menu(ChangedSummary(list.Count, total, lost, filter), items);
    }

    private EditorMenu? ChangedOneMenu(UserSession s, int id)
    {
        var t = ChangedList(s, "", out _, out _).FirstOrDefault(p => p.Id == id);
        if (t == null) return null;
        var items = new List<EditorMenuItem>
        {
            Info($"{t.Label}, number {id}"),
            Act(t.Removed ? "Bring it back as the map has it" : "Put it back as the map has it", $"edit putback #{id}", stay: false),
        };
        if (MayGo(s)) items.Add(Act("Go to it", $"edit goto #{id}", stay: false));
        if (!t.Removed) items.Add(Act("Select it, to change it", $"edit select #{id}"));
        return Menu(t.Name, items);
    }

    // ── The map file as it is on disk ───────────────────────────────────────────────────────────

    private readonly Dictionary<string, (string Path, DateTime Stamp, MapData Data, Dictionary<int, EntityData> ById)> _files
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A map's own file as it is on disk, read once and again whenever it changes; null if it
    /// has none or it does not read.</summary>
    private (MapData Data, Dictionary<int, EntityData> ById)? OwnFile(string mapId)
    {
        string? path = _maps.FileOf(mapId);
        if (path == null || !File.Exists(path)) return null;
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_files.TryGetValue(mapId, out var f) && f.Path == path && f.Stamp == stamp) return (f.Data, f.ById);
        var data = _maps.ReadOwnFile(mapId);
        if (data == null) return null;
        var byId = new Dictionary<int, EntityData>();
        foreach (var e in data.Entities) byId.TryAdd(e.EntityId, e);
        _files[mapId] = (path, stamp, data, byId);
        return (data, byId);
    }

    // ── Putting back ────────────────────────────────────────────────────────────────────────────

    /// <summary>/edit putback #ID [#ID ...]: things from the map file put back as it has them, wherever
    /// they are, all or none, one undo for all.</summary>
    private void PutBackCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (args.Length > 0 && args[^1].Equals("dialog", StringComparison.OrdinalIgnoreCase)) args = args[..^1];
        var ids = new List<int>();
        foreach (var word in args)
        {
            if (!int.TryParse(word.TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            { ids.Clear(); break; }
            ids.Add(id);
        }
        if (ids.Count == 0) { Say(reply, "Say /edit putback #NUMBER, more numbers to put several back. /edit changed lists what was changed or removed."); return; }
        PutBack(s, ids.Distinct().ToList(), reply);
    }

    private void PutBack(UserSession s, List<int> ids, Action<IMessage> reply)
    {
        if (!TryBody(s, reply, out var world, out _, out _)) return;
        string mapId = s.CurrentMapId;
        var o = Overlays.Get(mapId);
        var authored = _maps.AuthoredEntities(mapId);
        var file = OwnFile(mapId);
        if (file == null) { Say(reply, "Nothing put back: this map's file could not be read, so what it has is not known."); return; }
        var ops = new List<EditOp>();
        var names = new List<(int Id, string Name, bool Removed)>();
        foreach (int id in ids)
        {
            var change = o.ChangeFor(id);
            var removal = o.Removed.FirstOrDefault(r => r.Id == id);
            if (change == null && removal == null)
            { Say(reply, $"Nothing put back: number {id} is not something from the map file that the editor changed or removed. /edit changed lists them."); return; }
            string prefab = change?.Prefab ?? removal!.Prefab;
            var original = MapOverlayStore.Find(file.Value.Data, file.Value.ById, id, prefab, change?.Was ?? removal!.Was, null);
            if (original == null)
            { Say(reply, $"Nothing put back: the map file no longer has a {KindName(prefab)} where it had one."); return; }
            var data = MapOverlayStore.Clone(original);
            data.EntityId = id;
            var target = new Snapshot(id, data, null, Added: false, Change: null, Was: original.Position);
            if (change != null)
            {
                if (!authored.TryGetValue(id, out var e) || !world.IsAlive(e))
                { Say(reply, $"Nothing put back: number {id} is not on the map."); return; }
                string name = NameOf(world, e);
                if (Aboard(world, e) is { } rider) { Say(reply, $"Nothing put back: {rider} is in {name}."); return; }
                var now = Take(mapId, world, e, id);
                ops.Add(new BatchOp(mapId, new EditOp[] { new DeleteOp(mapId, now, name), new PlaceOp(mapId, target, "put back", name) },
                                    $"put back {name} as the map has it"));
                names.Add((id, name, false));
            }
            else
            {
                string name = original.Name is { Length: > 0 } n ? n : removal!.Name ?? KindName(prefab);
                ops.Add(new PlaceOp(mapId, target, "brought back", name));
                names.Add((id, name, true));
            }
        }
        var op = ops.Count == 1 ? ops[0] : new BatchOp(mapId, ops, $"put back {Plural(ops.Count, "thing")} as the map has them");
        if (!Reverse(s, op, forward: true, out string why)) { Say(reply, $"Nothing put back: {why}"); return; }
        Push(s, op);
        Serilog.Log.Information("WorldEditor: {User} put back {Things} on '{Map}' as the map file has them.", s.Username,
                                string.Join(", ", names.Select(n => $"{n.Name} (#{n.Id})")), mapId);
        string Where(int id)
            => TryBody(s, _ => { }, out var w, out var feet, out float yaw) && authored.TryGetValue(id, out var e) && w.IsAlive(e)
                ? FarWhere(w, e, feet, yaw) : "";
        if (names.Count == 1)
        {
            var (id, name, removed) = names[0];
            Say(reply, removed ? $"Brought back {name}, {Where(id)}. Undo removes it again."
                               : $"Put back {name} as the map has it, {Where(id)}. Undo changes it again.");
        }
        else Say(reply, $"Put back {names.Count} things as the map has them: {string.Join("; ", names.Select(n => $"{n.Name}, {Where(n.Id)}"))}. One undo changes them again.");
        Notify(s, names.Count == 1 ? $"{s.Username} put back {names[0].Name} as the map has it." : $"{s.Username} put back {names.Count} things as the map has them.");
        Refresh(s, reply);
    }
}
