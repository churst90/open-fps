using System.Globalization;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

using EntityData = OpenFPS.Server.Repositories.EntityData;

namespace OpenFPS.Server.Editor;

/// <summary>The map's spawn point as the overlay keeps it (null: the map file's own), before and after.</summary>
public sealed record SpawnSetOp(string MapId, OverlaySpawn? Before, OverlaySpawn? After) : EditOp(MapId)
{
    public override string What => "moved the spawn point";
}

/// <summary>
/// Versions of a map's edits (docs/WORLD_EDITOR.md section 18): saved by name, listed with who and when,
/// restored as one undoable step with what the map had saved first, and written into a map file on
/// request ("baked") where the file is somebody's own and not a generator's.
/// </summary>
public sealed partial class WorldEditor
{
    private MapVersionStore? _versions;
    /// <summary>The saved versions of every map's edits, beside the overlays.</summary>
    public MapVersionStore Versions => _versions ??= new MapVersionStore(Overlays.Directory);

    /// <summary>The longest name a version may have.</summary>
    public const int MaxVersionName = 60;

    /// <summary>"12 placed, 4 changed, 1 removed": what an overlay holds, in a few words.</summary>
    internal static string Counts(MapOverlay o)
    {
        var parts = new List<string> { $"{o.Added.Count} placed", $"{o.Changed.Count} changed", $"{o.Removed.Count} removed" };
        if (o.Spawn != null) parts.Add("spawn moved");
        if (o.Settings is { Count: > 0 } s) parts.Add(Plural(s.Count, "map setting"));
        if (o.Pins.Count > 0) parts.Add(Plural(o.Pins.Count, "pin"));
        if (o.Routes is { Count: > 0 } routes) parts.Add(RoutesSaid(routes.Count));
        if (o.People is { Count: > 0 } people) parts.Add(people.Count == 1 ? "1 person" : $"{people.Count} people");
        return string.Join(", ", parts);
    }

    private static string RoutesSaid(int n) => n == 1 ? "1 road, path or railway" : $"{n} roads, paths or railways";

    private static string VersionLabel(MapVersion v)
        => $"Version {v.Number}, {v.Name}, by {v.Author}, {v.SavedUtc.ToLocalTime():d MMMM HH:mm}: {Counts(v.Overlay)}";

    /// <summary>Whether this player may write the map's edits into its file, and if not, why not.</summary>
    private bool MayBake(UserSession s, out string why)
    {
        why = "";
        string mapId = s.CurrentMapId;
        string name = _maps.DisplayName(mapId);
        if (!_maps.IsOwner(mapId, s.Username) && !s.Can(Permissions.MapsAny))
        { why = $"Only the owner of {name}, or somebody with maps-any, can write its edits into its file."; return false; }
        if (_maps.IsShipped(mapId))
        {
            why = $"{name} is written by a program in tools and must stay byte for byte what that program writes, so its edits stay "
                + "beside it in the overlay, where they are laid over it every time it loads. To make them part of the map, change the program.";
            return false;
        }
        if (_maps.FileOf(mapId) is not { } path || !File.Exists(path)) { why = $"{name} has no file to write into."; return false; }
        return true;
    }

    /// <summary>/edit map save NAME, versions, restore NUMBER|NAME, bake [now]: the map's versions.</summary>
    private bool VersionCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        string rest = string.Join(" ", args.Skip(1)).Trim();
        switch (verb)
        {
            case "save": SaveVersion(s, rest, reply); return true;
            case "versions": SayVersions(s, reply); return true;
            case "restore": RestoreVersion(s, rest, reply); return true;
            case "bake": Bake(s, rest.Equals("now", StringComparison.OrdinalIgnoreCase), reply); return true;
            default: return false;
        }
    }

    private void SaveVersion(UserSession s, string name, Action<IMessage> reply)
    {
        if (name.Length == 0) { Say(reply, "Say /edit map save NAME: a few words to know it by, such as before the market."); return; }
        if (name.Length > MaxVersionName) { Say(reply, $"A version's name is at most {MaxVersionName} letters."); return; }
        var o = Overlays.Get(s.CurrentMapId);
        var v = Versions.Save(s.CurrentMapId, o, name, s.Username, automatic: false);
        Say(reply, $"Saved version {v.Number} of this map, {name}: {Counts(v.Overlay)}.");
        Notify(s, $"{s.Username} saved version {v.Number} of this map, {name}.");
        Refresh(s, reply);
    }

    private void SayVersions(UserSession s, Action<IMessage> reply)
    {
        var all = Versions.Of(s.CurrentMapId);
        if (all.Count == 0) { Say(reply, "No versions of this map have been saved. /edit map save NAME saves one."); return; }
        if (!s.IsTextClient) { SendMenu(s, "mapversions", reply, refresh: false); return; }
        Say(reply, "Versions of this map, newest first: " + string.Join("; ", all.Reverse().Take(30).Select(VersionLabel))
                 + ". /edit map restore NUMBER puts one back; what the map has now is saved first.");
    }

    // ── Restoring ───────────────────────────────────────────────────────────────────────────────

    private static bool SameSettings(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
        if (ca != cb) return false;
        if (ca == 0) return true;
        foreach (var (k, v) in a!)
            if (!b!.TryGetValue(k, out var w) || !SameValue(v, w)) return false;
        return true;
    }

    private static bool SameAddition(OverlayAddition a, OverlayAddition b)
        => a.Entity.PrefabId.Equals(b.Entity.PrefabId, StringComparison.OrdinalIgnoreCase) && PoseOf(a.Entity).Near(PoseOf(b.Entity))
           && a.Entity.Name == b.Entity.Name && a.Placement == b.Placement && SameSettings(a.Settings, b.Settings);

    private static bool SameChange(OverlayChange a, OverlayChange b)
        => new Pose(a.Position, a.Rotation, a.Scale).Near(new Pose(b.Position, b.Rotation, b.Scale)) && SameSettings(a.Settings, b.Settings);

    private static OverlayChange CopyChange(OverlayChange c) => new()
    {
        Id = c.Id, Prefab = c.Prefab, Was = c.Was, Position = c.Position, Rotation = c.Rotation, Scale = c.Scale,
        Settings = c.Settings == null ? null : new Dictionary<string, string>(c.Settings), WasRotation = c.WasRotation, WasScale = c.WasScale,
    };

    /// <summary>A map setting's name, as said.</summary>
    private string SettingLabel(string path)
    {
        if (path.Equals(MapSettings.Size, StringComparison.OrdinalIgnoreCase)) return "size";
        try { return MapSettings.Field(path, GroundChoices()).Label; }
        catch (ArgumentException) { return path.ToLowerInvariant(); }
    }

    /// <summary>
    /// The operations that take a map from its edits now to <paramref name="target"/>'s: things taken away
    /// and put down, things from the map file put as the target has them, the spawn point, the map's
    /// settings and pins. Each is an operation the editor already undoes, so the whole is one undo.
    /// What cannot be done is said in <paramref name="notes"/>.
    /// </summary>
    private List<EditOp> OpsToReach(UserSession s, MapOverlay target, List<string> notes, out int away, out int down)
    {
        away = 0; down = 0;
        string mapId = s.CurrentMapId;
        var ops = new List<EditOp>();
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return ops;
        var now = Overlays.Get(mapId);
        var authored = _maps.AuthoredEntities(mapId);
        var deletes = new List<EditOp>();
        var places = new List<EditOp>();

        bool Live(int id, out Entity e) => authored.TryGetValue(id, out e) && world.IsAlive(e);
        void TakeAway(int id)
        {
            if (!Live(id, out var e)) return;
            deletes.Add(new DeleteOp(mapId, Take(mapId, world, e, id), NameOf(world, e)));
        }

        // Things placed with the editor.
        var nowAdded = now.Added.ToDictionary(a => a.Entity.EntityId);
        var wantAdded = target.Added.ToDictionary(a => a.Entity.EntityId);
        foreach (var (id, a) in nowAdded)
            if (!wantAdded.TryGetValue(id, out var w) || !SameAddition(a, w)) TakeAway(id);
        foreach (var (id, a) in wantAdded)
        {
            if (nowAdded.TryGetValue(id, out var have) && SameAddition(have, a)) continue;
            var thing = new Snapshot(id, MapOverlayStore.Clone(a.Entity), a.Settings == null ? null : new Dictionary<string, string>(a.Settings),
                                     Added: true, Change: null, Was: a.Entity.Position, a.Placement, a.PlacedBy ?? "", a.PlacedAt);
            places.Add(new PlaceOp(mapId, thing, "put down", a.Entity.Name ?? KindName(a.Entity.PrefabId)));
        }

        // Things from the map file: removed, changed, or as the file has them.
        var ids = now.Changed.Select(c => c.Id).Concat(now.Removed.Select(r => r.Id))
                     .Concat(target.Changed.Select(c => c.Id)).Concat(target.Removed.Select(r => r.Id)).Distinct().ToList();
        (MapData Data, Dictionary<int, EntityData> ById)? file = ids.Count > 0 ? OwnFile(mapId) : null;
        int lostFromFile = 0;
        foreach (int id in ids)
        {
            var haveChange = now.ChangeFor(id);
            bool haveRemoved = now.Removed.Any(r => r.Id == id);
            var wantChange = target.ChangeFor(id);
            var wantRemoval = target.Removed.FirstOrDefault(r => r.Id == id);
            bool same = (haveRemoved && wantRemoval != null)
                        || (!haveRemoved && wantRemoval == null && haveChange == null && wantChange == null)
                        || (haveChange != null && wantChange != null && SameChange(haveChange, wantChange));
            if (same) continue;
            if (!haveRemoved) TakeAway(id);
            if (wantRemoval != null) continue;
            string prefab = wantChange?.Prefab ?? haveChange?.Prefab ?? now.Removed.First(r => r.Id == id).Prefab;
            var was = wantChange?.Was ?? haveChange?.Was ?? now.Removed.First(r => r.Id == id).Was;
            var original = file == null ? null : MapOverlayStore.Find(file.Value.Data, file.Value.ById, id, prefab, was, null);
            if (original == null) { lostFromFile++; continue; }
            var data = MapOverlayStore.Clone(original);
            data.EntityId = id;
            if (wantChange != null) { data.Position = wantChange.Position; data.Rotation = wantChange.Rotation; data.Scale = wantChange.Scale; }
            var snapshot = new Snapshot(id, data, wantChange?.Settings == null ? null : new Dictionary<string, string>(wantChange.Settings),
                                        Added: false, Change: wantChange == null ? null : CopyChange(wantChange), Was: original.Position);
            places.Add(new PlaceOp(mapId, snapshot, "put back", original.Name is { Length: > 0 } n ? n : KindName(prefab)));
        }
        if (lostFromFile > 0) notes.Add($"{Plural(lostFromFile, "thing")} the map file no longer has where it had them could not be put back");

        away = deletes.Count;
        down = places.Count;
        // Roads, paths and railways: their data and trains (their pieces are additions, above). Taken up
        // before their pieces go, laid after their pieces are down.
        var nowRoutes = now.Routes ?? new List<OverlayRoute>();
        var wantRoutes = target.Routes ?? new List<OverlayRoute>();
        foreach (var r in nowRoutes.Where(r => !wantRoutes.Any(w => MapVersionStore.Same(w, r))))
            ops.Add(new RouteOp(mapId, MapVersionStore.CopyOf(r), Adding: false));
        ops.AddRange(deletes);
        ops.AddRange(places);
        foreach (var r in wantRoutes.Where(r => !nowRoutes.Any(w => MapVersionStore.Same(w, r))))
            ops.Add(new RouteOp(mapId, MapVersionStore.CopyOf(r), Adding: true));

        // People: each by name, put on, changed or taken off.
        var nowPeople = now.People ?? new List<CharacterData>();
        var wantPeople = target.People ?? new List<CharacterData>();
        foreach (var name in nowPeople.Select(p => p.Name).Concat(wantPeople.Select(p => p.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            var have = nowPeople.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var want = wantPeople.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (SamePerson(have, want)) continue;
            ops.Add(new PersonOp(mapId, name, have == null ? null : CopyPerson(have), want == null ? null : CopyPerson(want)));
        }

        // The spawn point, the map's settings and its pins.
        bool sameSpawn = (now.Spawn == null && target.Spawn == null)
                         || (now.Spawn != null && target.Spawn != null && new Pose(now.Spawn.Position, now.Spawn.Rotation, System.Numerics.Vector3.One)
                                 .Near(new Pose(target.Spawn.Position, target.Spawn.Rotation, System.Numerics.Vector3.One)));
        if (!sameSpawn) ops.Add(new SpawnSetOp(mapId, now.Spawn == null ? null : Copy(now.Spawn), target.Spawn == null ? null : Copy(target.Spawn)));

        bool mayResize = (_maps.IsOwner(mapId, s.Username) || s.Can(Permissions.MapsAny)) && !_maps.IsShipped(mapId);
        var paths = (now.Settings?.Keys ?? Enumerable.Empty<string>()).Concat(target.Settings?.Keys ?? Enumerable.Empty<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var path in paths)
        {
            string? before = now.Settings != null && now.Settings.TryGetValue(path, out var b) ? b : null;
            string? after = target.Settings != null && target.Settings.TryGetValue(path, out var a) ? a : null;
            if (before == after) continue;
            if (path.Equals(MapSettings.Size, StringComparison.OrdinalIgnoreCase))
            {
                if (!mayResize) { notes.Add("the map's size was left as it is: only its owner changes it"); continue; }
                // No size kept is the file's own: said as a size, so its undo has one to go back to.
                if (after == null)
                {
                    var own = OwnFile(mapId)?.Data;
                    if (own == null) continue;
                    var size = own.MaxBound - own.MinBound;
                    after = MapSettings.FormatSize(size.X, size.Z, size.Y);
                    if (after == before) continue;
                }
            }
            ops.Add(new MapSetOp(mapId, path, SettingLabel(path), before, after));
        }
        foreach (var key in now.Pins.Keys.Concat(target.Pins.Keys).Distinct().ToList())
        {
            int? before = now.Pins.TryGetValue(key, out int b) ? b : null;
            int? after = target.Pins.TryGetValue(key, out int a) ? a : null;
            if (before == after) continue;
            var parts = key.Split(':', 2);
            if (parts.Length == 2) ops.Add(new PinOp(mapId, parts[0], parts[1], before, after));
        }
        return ops;
    }

    /// <summary>/edit map restore NUMBER|NAME: the map's edits made what a version has, as one undo; what
    /// the map had is saved as a version first.</summary>
    private void RestoreVersion(UserSession s, string which, Action<IMessage> reply)
    {
        if (which.Length == 0) { Say(reply, "Say /edit map restore NUMBER, or a version's name. /edit map versions lists them."); return; }
        string mapId = s.CurrentMapId;
        if (Versions.Find(mapId, which) is not { } version) { Say(reply, $"This map has no version {which}. /edit map versions lists them."); return; }
        var before = MapVersionStore.Copy(Overlays.Get(mapId));
        var notes = new List<string>();
        var ops = OpsToReach(s, version.Overlay, notes, out int away, out int down);
        if (ops.Count == 0) { Say(reply, $"This map is already as version {version.Number}, {version.Name}, has it."); return; }
        var batch = new BatchOp(mapId, ops, $"restored version {version.Number}, {version.Name}");
        if (!Reverse(s, batch, forward: true, out string why)) { Say(reply, $"Not restored: {why}"); return; }
        var o = Overlays.Get(mapId);
        if (version.Overlay.NextId > o.NextId) o.NextId = version.Overlay.NextId;
        Overlays.Save(mapId);
        Push(s, batch);
        var saved = Versions.Save(mapId, before, $"before restoring version {version.Number}", s.Username, automatic: true);
        int routes = ops.OfType<RouteOp>().Count();
        int people = ops.OfType<PersonOp>().Count();
        int other = ops.Count - away - down - routes - people;
        var said = new List<string>();
        if (away > 0) said.Add($"{Plural(away, "thing")} taken away");
        if (down > 0) said.Add($"{Plural(down, "thing")} put down or back");
        if (routes > 0) said.Add($"{RoutesSaid(routes)} taken up or laid");
        if (people > 0) said.Add(people == 1 ? "1 person put on, changed or taken off" : $"{people} people put on, changed or taken off");
        if (other > 0) said.Add(Plural(other, "map setting") + " changed");
        Say(reply, $"Restored version {version.Number}, {version.Name}: {string.Join(", ", said)}."
                 + (notes.Count > 0 ? $" {Capital(string.Join("; ", notes))}." : "")
                 + $" What the map had is saved as version {saved.Number}. Undo puts it back.");
        Notify(s, $"{s.Username} restored version {version.Number} of this map, {version.Name}.");
        Serilog.Log.Information("WorldEditor: {User} restored version {Number} ({Name}) of '{Map}': {Ops} operations.", s.Username, version.Number, version.Name, mapId, ops.Count);
        Refresh(s, reply);
    }

    /// <summary>The spawn point as an overlay keeps it, laid on the map: null is the map file's own.</summary>
    private bool ApplySpawnSet(string mapId, OverlaySpawn? spawn, out string why)
    {
        why = "";
        if (spawn != null) { ApplySpawn(mapId, spawn.Position, spawn.Rotation, Copy(spawn)); return true; }
        if (OwnFile(mapId)?.Data is not { } own) { why = "the map file could not be read for its spawn point."; return false; }
        ApplySpawn(mapId, own.SpawnPoint.Position, own.SpawnPoint.Rotation, null);
        return true;
    }

    private bool ReverseSpawnSet(SpawnSetOp op, bool forward, out string why)
    {
        why = "";
        var now = Overlays.Get(op.MapId).Spawn;
        var expect = forward ? op.Before : op.After;
        bool same = (now == null && expect == null)
                    || (now != null && expect != null && System.Numerics.Vector3.Distance(now.Position, expect.Position) < 1e-3f);
        if (!same) { why = "the spawn point has been moved since."; return false; }
        return ApplySpawnSet(op.MapId, forward ? op.After : op.Before, out why);
    }

    // ── Baking ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The settings of a thing a map file holds itself: its name, a door's sides, indoors and a
    /// room's materials. The rest (a sound's volume, range and model) stay in the overlay.</summary>
    private static readonly string[] FaceWords = { "Floor", "Ceiling", "North", "South", "East", "West" };

    /// <summary>Whether a map setting is a field of the map file (MapSettings.Apply lays it on MapData).</summary>
    private static bool FileHolds(string path)
        => path.Equals(MapSettings.Weather, StringComparison.OrdinalIgnoreCase) || path.Equals(MapSettings.Hour, StringComparison.OrdinalIgnoreCase)
           || path.Equals(MapSettings.Ground, StringComparison.OrdinalIgnoreCase) || path.Equals(MapSettings.Size, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(MapSettings.BeaconPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lays the settings a map entry can hold onto it; returns those it cannot.</summary>
    private static Dictionary<string, string>? Carry(EntityData e, Dictionary<string, string>? settings, World world, Entity live)
    {
        if (settings == null || settings.Count == 0) return null;
        var left = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, value) in settings)
        {
            switch (path)
            {
                case "Name": e.Name = value; break;
                case "KeyedSide": e.KeyedSide = value == "front" ? 1f : value == "back" ? -1f : 0f; break;
                case "PushSide": e.PushSide = value == "back" ? -1f : 1f; break;
                case "Indoor": e.IsIndoor = value == "true"; break;
                default:
                    int face = Array.IndexOf(FaceWords, path);
                    if (face < 0) { left[path] = value; break; }
                    // All six, from the room as it is, with this one as kept.
                    var six = e.RoomMaterials is { Length: 6 } m ? m.ToArray() : new string[6];
                    for (int i = 0; i < 6; i++)
                        if (string.IsNullOrEmpty(six[i]))
                            six[i] = live != Entity.Null && world.IsAlive(live) && EntitySettings.Named(FaceWords[i]) is { } st && st.Applies(world, live)
                                ? st.Get(world, live) : "Generic";
                    six[face] = value;
                    e.RoomMaterials = six;
                    break;
            }
        }
        return left.Count == 0 ? null : left;
    }

    /// <summary>
    /// /edit map bake [now]: the map's edits written into its own file, where the file is a player's map and
    /// not a generator's. Without "now" it says what it will do. What a map file cannot hold (a sound's
    /// level, range and model; parked vehicles; pins) stays in the overlay. The file as it was is kept
    /// beside it, a version of the edits is saved first, and undo cannot take a bake back.
    /// </summary>
    private void Bake(UserSession s, bool now, Action<IMessage> reply)
    {
        string mapId = s.CurrentMapId;
        if (!MayBake(s, out string refusal)) { Say(reply, refusal); return; }
        var o = Overlays.Get(mapId);
        if (o.IsEmpty) { Say(reply, "This map has no edits to write into its file."); return; }
        if (!now)
        {
            Say(reply, $"Baking writes this map's edits into its file: {Counts(o)}. A sound's volume, range and model, parked vehicles and pinned "
                     + "models stay in the overlay, since a map file does not hold them. The file as it was is kept beside it, and a version of the "
                     + "edits is saved first; undo cannot take it back. Say /edit map bake now to do it.");
            return;
        }
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return;
        string path = _maps.FileOf(mapId)!;
        var file = _maps.ReadOwnFile(mapId);
        if (file == null) { Say(reply, "Not baked: the map file could not be read."); return; }
        var saved = Versions.Save(mapId, o, "before baking", s.Username, automatic: true);

        // What loading does, onto the file's data, then each thing's settings the file can hold.
        var laid = MapVersionStore.Copy(o);
        MapOverlayStore.Lay(file, laid);
        var authored = _maps.AuthoredEntities(mapId);
        var byId = file.Entities.GroupBy(e => e.EntityId).ToDictionary(g => g.Key, g => g.First());
        var residual = new MapOverlay { MapId = mapId, NextId = o.NextId, Pins = new Dictionary<string, int>(o.Pins) };
        void Keep(int id, Dictionary<string, string>? settings)
        {
            if (!byId.TryGetValue(id, out var e)) return;
            var left = Carry(e, settings, world, authored.TryGetValue(id, out var live) ? live : Entity.Null);
            if (left == null) return;
            residual.Changed.Add(new OverlayChange
            {
                Id = id, Prefab = e.PrefabId, Was = e.Position, Position = e.Position, Rotation = e.Rotation, Scale = e.Scale,
                WasRotation = e.Rotation, WasScale = e.Scale, Settings = left,
            });
        }
        foreach (var c in laid.Changed) Keep(c.Id, c.Settings);
        foreach (var a in laid.Added.Where(a => !a.IsVehicle)) Keep(a.Entity.EntityId, a.Settings);
        residual.Added.AddRange(o.Added.Where(a => a.IsVehicle));
        // The map's settings a file holds are laid by Lay (weather, hour, ground, beacons, size); the rest stay.
        if (o.Settings != null)
            foreach (var (key, value) in o.Settings.Where(kv => !FileHolds(kv.Key)))
                (residual.Settings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[key] = value;

        string backup = $"{path}.before-bake-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Copy(path, backup, overwrite: false);
            _maps.WriteOwnFile(file);
        }
        catch (Exception ex) { Say(reply, $"Not baked: {ex.Message}"); return; }

        o.Spawn = null;
        o.Changed = residual.Changed;
        o.Removed.Clear();
        o.Added = residual.Added;
        o.Settings = residual.Settings;
        // Roads and railways are the file's own roads and tracks now.
        o.Routes = null;
        o.People = null;
        Overlays.Save(mapId);
        _files.Remove(mapId);
        _data.Remove(mapId);
        // The undo history of this map was about the overlay as it was: no editor's undo applies to the file.
        foreach (var key in _stacks.Keys.Where(k => k.Map.Equals(mapId, StringComparison.OrdinalIgnoreCase)).ToList()) _stacks.Remove(key);
        Serilog.Log.Information("WorldEditor: {User} baked the edits of '{Map}' into {Path}; the file as it was is {Backup}.", s.Username, mapId, path, backup);
        Say(reply, $"Baked: this map's file now has its edits. {(residual.IsEmpty ? "Nothing is left in the overlay." : $"Left in the overlay, since a map file does not hold them: {Counts(residual)}.")} "
                 + $"The file as it was is kept as {Path.GetFileName(backup)}, and the edits as they were are version {saved.Number}. "
                 + "The undo history of this map is cleared.");
        Notify(s, $"{s.Username} wrote this map's edits into its file. Your undo history here is cleared.");
        Refresh(s, reply);
    }

    // ── Menus and the dialog ────────────────────────────────────────────────────────────────────

    private EditorMenu VersionsMenu(UserSession s)
    {
        var items = new List<EditorMenuItem>
        {
            Typed("Save a version, typed", "/edit map save ", "name for the version", "A few words to know it by, such as before the market."),
        };
        foreach (var v in Versions.Of(s.CurrentMapId).Reverse()) items.Add(Opens(VersionLabel(v), $"mapversion:{v.Number}"));
        if (Versions.Of(s.CurrentMapId).Count == 0) items.Add(Info("No versions saved yet."));
        items.Add(Opens("Write the edits into the map file", "mapbake"));
        return Menu("Versions of this map", items);
    }

    private EditorMenu? VersionMenu(UserSession s, string which)
    {
        if (Versions.Find(s.CurrentMapId, which) is not { } v) return null;
        return Menu($"Version {v.Number}, {v.Name}", new[]
        {
            Info(VersionLabel(v)),
            Act("Restore this version", $"edit map restore {v.Number}", stay: false),
            Info("What the map has now is saved as a version first, and undo puts it back."),
        });
    }

    private EditorMenu BakeMenu(UserSession s)
    {
        if (!MayBake(s, out string why)) return Menu("Write the edits into the map file", new[] { Info(why) });
        return Menu("Write the edits into the map file", new[]
        {
            Info($"This map's edits, {Counts(Overlays.Get(s.CurrentMapId))}, are written into its file. A sound's volume, range and model, parked vehicles and pins stay in the overlay. Undo cannot take it back; the file as it was is kept beside it."),
            Act("Yes, write them into the file", "edit map bake now", stay: false),
        });
    }

    /// <summary>The World tab's versions: a line saying what may be done, and each version, newest first.</summary>
    private IEnumerable<EditorMenuItem> DialogVersions(UserSession s)
    {
        bool bake = MayBake(s, out string why);
        var o = Overlays.Get(s.CurrentMapId);
        string help = bake
            ? $"Writes this map's edits, {Counts(o)}, into its file. A sound's volume, range and model, parked vehicles and pins stay in the overlay. Asks first; undo cannot take it back."
            : why;
        var all = Versions.Of(s.CurrentMapId);
        yield return Line("world.versioninfo", all.Count == 0 ? "No versions saved yet" : Plural(all.Count, "version") + " saved, newest first",
                          bake ? "bake" : "", help);
        foreach (var v in all.Reverse())
            yield return Line("world.version", VersionLabel(v), v.Number.ToString(CultureInfo.InvariantCulture), prompt: v.Name);
    }
}
