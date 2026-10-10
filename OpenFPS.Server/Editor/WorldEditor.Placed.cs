using System.Globalization;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Systems;

namespace OpenFPS.Server.Editor;

/// <summary>
/// Everything placed on a map with the editor, wherever it is (docs/WORLD_EDITOR.md section 17): listed
/// nearest first with who placed it and when, filtered by words, and taken away or gone to without
/// walking there. /edit placed, /edit remove #ID, /edit goto #ID, and the F12 dialog's Edit tab.
/// </summary>
public sealed partial class WorldEditor
{
    /// <summary>The most rows the list sends; the filter finds the rest.</summary>
    public const int PlacedListed = 200;

    /// <summary>One thing placed with the editor, as the list says it.</summary>
    internal sealed record PlacedThing(int Id, string Name, string Kind, string Where, float Distance, string? By, DateTime? At)
    {
        /// <summary>"Megaphone, 104 metres north east, placed by cody, 9 October 14:02".</summary>
        public string Label
        {
            get
            {
                string what = Name.Equals(Kind, StringComparison.OrdinalIgnoreCase) ? Name : $"{Name}, {Kind}";
                string when = By == null ? "placed earlier" : $"placed by {By}{(At is { } t ? $", {t.ToLocalTime():d MMMM HH:mm}" : "")}";
                return $"{what}, {Where}, {when}";
            }
        }
    }

    /// <summary>What a placed thing is: a prefab's name, a vehicle's.</summary>
    private string KindOf(OverlayAddition a)
    {
        if (IsVehicleId(a.Entity.PrefabId, out string preset)) return KnownVehicle(preset) ? VehicleOf(preset).Name : "Vehicle";
        return _maps.Prefabs.TryGetValue(a.Entity.PrefabId.ToLowerInvariant(), out var t) ? t.Name : a.Entity.PrefabId;
    }

    /// <summary>Where a thing is from you, across the map: metres and a compass word, and up or down when it
    /// is well above or below you.</summary>
    private static string FarWhere(World world, Entity e, Vector3 feet, float yaw)
    {
        var (d, toward, contains) = Reach(world, e, feet);
        if (contains || d < 0.5f) return Where(world, e, feet, yaw);
        float shown = d >= 10f ? MathF.Round(d) : MathF.Round(d, 1);
        string at = $"{Metres(shown)} {CompassOf(MathF.Atan2(toward.X, toward.Z))}";
        var (lo, hi) = Box(world, e);
        if (lo.Y > feet.Y + 2.5f) at += $", {Metres(MathF.Round(lo.Y - feet.Y))} up";
        else if (hi.Y < feet.Y - 2.5f) at += $", {Metres(MathF.Round(feet.Y - hi.Y))} down";
        return at;
    }

    /// <summary>The words of a filter, and "within N" (or "Nm") as a distance.</summary>
    internal static (List<string> Words, float? Within) PlacedFilterOf(string filter)
    {
        var words = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        float? within = null;
        static bool IsUnit(string w) => w.ToLowerInvariant() is "m" or "metre" or "metres" or "meter" or "meters";
        for (int i = 0; i < words.Count; i++)
        {
            string w = words[i].ToLowerInvariant();
            if (w == "within" && i + 1 < words.Count && TryNumber(words[i + 1].TrimEnd('m', 'M'), out float r) && r > 0)
            {
                within = r;
                int take = i + 2 < words.Count && IsUnit(words[i + 2]) ? 3 : 2;
                words.RemoveRange(i, take);
                i--;
                continue;
            }
            if (w.Length > 1 && w.EndsWith('m') && TryNumber(w[..^1], out r) && r > 0)
            {
                within = r;
                words.RemoveAt(i);
                i--;
            }
        }
        return (words, within);
    }

    /// <summary>Things placed with the editor on the map you are on that match a filter, nearest first;
    /// <paramref name="total"/> is how many there are in all.</summary>
    internal List<PlacedThing> Placed(UserSession s, string filter, out int total)
    {
        total = 0;
        var list = new List<PlacedThing>();
        if (!_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _) || !TryBody(s, _ => { }, out _, out var feet, out float yaw)) return list;
        var authored = _maps.AuthoredEntities(s.CurrentMapId);
        var (words, within) = PlacedFilterOf(filter);
        foreach (var a in Overlays.Get(s.CurrentMapId).Added)
        {
            int id = a.Entity.EntityId;
            if (!authored.TryGetValue(id, out var e) || !world.IsAlive(e) || !world.Has<Transform>(e)) continue;
            total++;
            string name = NameOf(world, e);
            string kind = KindOf(a);
            float distance = Reach(world, e, feet).Distance;
            if (within is float r && distance > r) continue;
            string hay = $"{name} {kind} {a.Entity.PrefabId.Replace('_', ' ')} {a.PlacedBy ?? "earlier"} #{id}";
            if (!words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new PlacedThing(id, name, kind, FarWhere(world, e, feet, yaw), distance, a.PlacedBy, a.PlacedAt));
        }
        return list.OrderBy(t => t.Distance).ThenBy(t => t.Id).ToList();
    }

    /// <summary>The list's headline: how many, and what the filter kept.</summary>
    private static string PlacedSummary(int shown, int total, string filter)
    {
        if (total == 0) return "Nothing has been placed on this map with the editor";
        string said = filter.Length == 0 ? $"{Plural(total, "thing")} placed on this map, nearest first"
                                         : $"{shown} of {total} placed on this map match {filter}, nearest first";
        return shown > PlacedListed ? $"{said}, the nearest {PlacedListed} listed" : said;
    }

    /// <summary>
    /// /edit placed [WORDS]: the things placed with the editor here, nearest first, matching every word
    /// (a name, a kind, who placed it; "within 20" for those within 20 metres). The words are kept as the
    /// dialog's filter. With "dialog" on the end, only the dialog's Edit tab answers.
    /// </summary>
    private void PlacedCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        bool dialog = args.Length > 0 && args[^1].Equals("dialog", StringComparison.OrdinalIgnoreCase);
        if (dialog) args = args[..^1];
        var hand = HandOf(s);
        string filter = string.Join(" ", args).Trim();
        hand.PlacedFilter = filter;
        if (dialog)
        {
            if (hand.Dialog && !s.IsTextClient) reply(DialogTab(s, hand.DialogTab));
            return;
        }
        if (!s.IsTextClient) { SendMenu(s, "placed", reply, refresh: false); return; }
        var list = Placed(s, filter, out int total);
        if (total == 0) { Say(reply, "Nothing has been placed on this map with the editor."); return; }
        if (list.Count == 0) { Say(reply, $"Nothing placed here matches {filter}. {Plural(total, "thing")} placed in all."); return; }
        const int Said = 30;
        Say(reply, $"{PlacedSummary(list.Count, total, filter)}: " + string.Join("; ", list.Take(Said).Select(t => $"#{t.Id} {t.Label}"))
                 + (list.Count > Said ? $"; and {list.Count - Said} more" : "")
                 + ". /edit remove #NUMBER takes one away; /edit goto #NUMBER takes you to it.");
    }

    private EditorMenu PlacedMenu(UserSession s, string filter)
    {
        var list = Placed(s, filter, out int total);
        var items = new List<EditorMenuItem>
        {
            Typed(filter.Length == 0 ? "Filter, typed" : $"Filter, now {filter}, typed", "/edit placed ", "words to filter by",
                  "Words in a name, a kind or who placed it. within 20 keeps those within 20 metres. Nothing typed lists them all.", filter),
        };
        items.AddRange(list.Take(PlacedListed).Select(t => Opens(t.Label, $"placedone:{t.Id}")));
        if (list.Count == 0) items.Add(Info(total == 0 ? "Nothing has been placed on this map with the editor." : $"Nothing matches {filter}."));
        return Menu(PlacedSummary(list.Count, total, filter), items);
    }

    private EditorMenu? PlacedOneMenu(UserSession s, int id)
    {
        var t = Placed(s, "", out _).FirstOrDefault(p => p.Id == id);
        if (t == null) return null;
        var items = new List<EditorMenuItem>
        {
            Info($"{t.Label}, number {id}"),
            Act("Remove it", $"edit remove #{id}", stay: false),
            Act("Select it, to change it", $"edit select #{id}"),
        };
        if (MayGo(s)) items.Insert(2, Act("Go to it", $"edit goto #{id}", stay: false));
        return Menu(t.Name, items);
    }

    // ── Removing by number ──────────────────────────────────────────────────────────────────────

    /// <summary>/edit remove #ID [#ID ...] and /edit remove held: things taken away wherever they are,
    /// all or none, one undo for all.</summary>
    private void RemoveCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (args.Length > 0 && args[^1].Equals("dialog", StringComparison.OrdinalIgnoreCase)) args = args[..^1];
        var hand = HandOf(s);
        List<int> ids;
        if (args.Length == 1 && args[0].Equals("held", StringComparison.OrdinalIgnoreCase))
        {
            ids = hand.Held.ToList();
            if (ids.Count == 0) { Say(reply, "Nothing is ticked or held to remove."); return; }
        }
        else
        {
            ids = new List<int>();
            foreach (var word in args)
            {
                if (!int.TryParse(word.TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                { Say(reply, "Say /edit remove #NUMBER, more numbers to remove several, or /edit remove held. /edit placed lists what was placed."); return; }
                ids.Add(id);
            }
            if (ids.Count == 0) { Delete(s, reply); return; }
        }
        RemoveIds(s, ids.Distinct().ToList(), reply);
    }

    private void RemoveIds(UserSession s, List<int> ids, Action<IMessage> reply)
    {
        if (!TryBody(s, reply, out var world, out var feet, out float yaw)) return;
        string mapId = s.CurrentMapId;
        var authored = _maps.AuthoredEntities(mapId);
        var o = Overlays.Get(mapId);
        var found = new List<(int Id, Entity E, string Name, string Where)>();
        foreach (int id in ids)
        {
            if (!authored.TryGetValue(id, out var e) || !world.IsAlive(e) || !(Editable(world, e) || o.AdditionFor(id) is { IsVehicle: true }))
            { Say(reply, $"Nothing removed: there is nothing numbered {id} on this map the editor can take away."); return; }
            string name = NameOf(world, e);
            if (Aboard(world, e) is { } rider) { Say(reply, $"Nothing removed: {rider} is in {name}."); return; }
            found.Add((id, e, name, FarWhere(world, e, feet, yaw)));
        }
        var hand = HandOf(s);
        var ops = new List<EditOp>();
        foreach (var f in found)
        {
            var thing = Take(mapId, world, f.E, f.Id);
            Remove(mapId, world, f.E, f.Id);
            ops.Add(new DeleteOp(mapId, thing, f.Name));
            hand.Held.Remove(f.Id);
            if (hand.Selected == f.Id) hand.Selected = null;
        }
        Push(s, ops.Count == 1 ? ops[0] : new BatchOp(mapId, ops, $"removed {Plural(ops.Count, "thing")}"));
        Serilog.Log.Information("WorldEditor: {User} removed {Things} on '{Map}'.", s.Username,
                                string.Join(", ", found.Select(f => $"{f.Name} (#{f.Id})")), mapId);
        Say(reply, found.Count == 1
            ? $"Removed {found[0].Name}, {found[0].Where}. Undo puts it back."
            : $"Removed {found.Count} things: {string.Join("; ", found.Select(f => $"{f.Name}, {f.Where}"))}. One undo puts them back.");
        Notify(s, found.Count == 1 ? $"{s.Username} removed {found[0].Name}." : $"{s.Username} removed {found.Count} things.");
        Refresh(s, reply);
    }

    // ── Ticking what the list shows ─────────────────────────────────────────────────────────────

    /// <summary>/edit select add placed [WORDS]: every thing the list shows (the words given, or the list's
    /// own filter) held as well, for moving, grouping, saving as a building or removing together.</summary>
    private void HoldPlaced(UserSession s, string filter, bool dialog, Action<IMessage> reply)
    {
        var hand = HandOf(s);
        if (!_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _)) return;
        var authored = _maps.AuthoredEntities(s.CurrentMapId);
        int added = 0;
        foreach (var t in Placed(s, filter.Length > 0 ? filter : hand.PlacedFilter, out _).Take(PlacedListed))
        {
            if (hand.Held.Count >= MaxHeld) break;
            if (hand.Held.Contains(t.Id) || !authored.TryGetValue(t.Id, out var e) || !Editable(world, e)) continue;
            hand.Held.Add(t.Id);
            added++;
        }
        Say(reply, added == 0 ? "Nothing more to tick: what the list shows is ticked already, or cannot be held."
                 : dialog ? $"Ticked {added}: {hand.Held.Count} ticked." : $"Holding {Plural(added, "thing")} more: {Plural(hand.Held.Count, "thing")} held.");
        Refresh(s, reply);
    }

    // ── Going to one ────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether this player may be moved to a thing: as /move moves them (on their own map, or with
    /// the move permission), or with tp-free.</summary>
    private bool MayGo(UserSession s)
        => s.Can(Permissions.TeleportFree) || s.Can("move") || _maps.IsOwner(s.CurrentMapId, s.Username);

    /// <summary>/edit goto #ID: you, standing beside the thing, facing it.</summary>
    private void GoTo(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (!MayGo(s)) { Say(reply, "Going to a thing needs the move permission here, as /move does: your own map, or a developer's."); return; }
        if (args.Length > 0 && args[^1].Equals("dialog", StringComparison.OrdinalIgnoreCase)) args = args[..^1];
        if (args.Length != 1 || !int.TryParse(args[0].TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
        { Say(reply, "Say /edit goto #NUMBER. /edit placed lists what was placed, with numbers."); return; }
        if (!TryBody(s, reply, out var world, out var feet, out _)) return;
        if (!_maps.TryGetMap(s.CurrentMapId, out _, out _, out var grid, out _)) return;
        Vector3 lo, hi;
        string name;
        if (_maps.AuthoredEntities(s.CurrentMapId).TryGetValue(id, out var e) && world.IsAlive(e) && world.Has<Transform>(e))
        {
            name = NameOf(world, e);
            (lo, hi) = Box(world, e);
        }
        else if (Overlays.Get(s.CurrentMapId).Removed.FirstOrDefault(r => r.Id == id) is { } removed)
        {
            // Something from the map file the editor removed: where it stood.
            name = $"where {removed.Name ?? KindName(removed.Prefab)} was";
            var half = SizeAt(removed.Prefab, Vector3.One) * 0.5f;
            (lo, hi) = (removed.Was - half, removed.Was + half);
        }
        else { Say(reply, $"There is nothing numbered {id} on this map."); return; }
        if (SpotBeside(world, grid, lo, hi, feet) is not { } spot) { Say(reply, $"There is no room to stand beside {name}."); return; }
        var centre = (lo + hi) * 0.5f;
        float face = MathF.Atan2(centre.X - spot.X, centre.Z - spot.Z);
        if (world.Has<OccupantComponent>(s.Entity)) CompositeService.Disembark(world, s.Entity);
        ref var tr = ref world.Get<Transform>(s.Entity);
        tr.Position = spot;
        tr.Rotation = Quaternion.CreateFromYawPitchRoll(face, 0f, 0f);
        tr.IsDirty = true;
        if (world.Has<PlayerComponent>(s.Entity)) world.Get<PlayerComponent>(s.Entity).Yaw = face;
        _server.SendToSession(s, new PlayerSpawned { EntityId = s.Entity.Id, SpawnTransform = tr });
        Say(reply, $"You are beside {name}, facing it.");
        Refresh(s, reply);
    }

    /// <summary>Where a body can stand beside a thing: out from its side nearest you first, then round it an
    /// eighth of a turn at a time, on whatever floor is there.</summary>
    private static Vector3? SpotBeside(World world, SpatialGrid<Entity> grid, Vector3 lo, Vector3 hi, Vector3 from)
    {
        var centre = (lo + hi) * 0.5f;
        float reach = MathF.Max(hi.X - lo.X, hi.Z - lo.Z) * 0.5f + PhysicsConstants.PlayerRadius + 0.5f;
        var away = new Vector3(from.X - centre.X, 0f, from.Z - centre.Z);
        var first = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : -Vector3.UnitZ;
        for (int k = 0; k < 8; k++)
        {
            // 0, +45, -45, +90, -90 ... degrees from the side nearest you.
            float turn = (k + 1) / 2 * (k % 2 == 1 ? 1 : -1) * MathF.PI / 4f;
            var dir = Vector3.Transform(first, Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn));
            var p = new Vector3(centre.X, 0f, centre.Z) + dir * reach;
            float ground = PhysicsUtils.GetGroundHeight(world, grid, new Vector3(p.X, lo.Y + 1.5f, p.Z), out _);
            if (ground < -500f) continue;
            var stand = new Vector3(p.X, ground + 0.05f, p.Z);
            if (!MovementSystem.CheckCollision(world, grid, stand, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight)) return stand;
        }
        return null;
    }
}
