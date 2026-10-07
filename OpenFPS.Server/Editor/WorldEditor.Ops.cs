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

/// <summary>Where a thing is, which way it is turned, and how big: everything a move changes.</summary>
public readonly record struct Pose(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public bool Near(Pose o)
        => Vector3.Distance(Position, o.Position) < 1e-3f
           && MathF.Abs(Quaternion.Dot(Rotation, o.Rotation)) > 1f - 1e-5f
           && Vector3.Distance(Scale, o.Scale) < 1e-4f;
}

/// <summary>Everything about a placed thing that is needed to put it back exactly.</summary>
public sealed record Snapshot(int Id, EntityData Data, Dictionary<string, string>? Settings, bool Added,
                              OverlayChange? Change, Vector3 Was);

/// <summary>One operation of the world editor, as its undo stack keeps it (docs/WORLD_EDITOR.md section 6).</summary>
public abstract record EditOp(string MapId)
{
    /// <summary>What it did, as said after "Undid" or "Redid": "moved Fountain".</summary>
    public abstract string What { get; }
}

public sealed record PoseOp(string MapId, int Id, string Name, string Verb, Pose Before, Pose After) : EditOp(MapId)
{
    public override string What => $"{Verb} {Name}";
}

public sealed record PlaceOp(string MapId, Snapshot Thing, string Verb, string Name) : EditOp(MapId)
{
    public override string What => $"{Verb} {Name}";
}

public sealed record DeleteOp(string MapId, Snapshot Thing, string Name) : EditOp(MapId)
{
    public override string What => $"deleted {Name}";
}

public sealed record SetOp(string MapId, int Id, string Name, string Path, string Label, string? Before, string After) : EditOp(MapId)
{
    public override string What => $"set {Label} of {Name}";
}

public sealed record SpawnOp(string MapId, Vector3 BeforePosition, Quaternion BeforeRotation, OverlaySpawn? BeforeOverlay,
                             Vector3 AfterPosition, Quaternion AfterRotation) : EditOp(MapId)
{
    public override string What => "moved the spawn point";
}

public sealed record ModelOp(string MapId, string Kind, string Id, string Label, int Before, int After) : EditOp(MapId)
{
    public override string What => $"changed {Label} of the {ModelKinds.Spoken(Kind)} {Id}";
}

public sealed partial class WorldEditor
{
    // ── The stack ───────────────────────────────────────────────────────────────────────────────

    private void Push(UserSession s, EditOp op)
    {
        var (undo, redo) = Stacks(s);
        undo.Add(op);
        if (undo.Count > MaxUndo) undo.RemoveAt(0);
        redo.Clear();
        Touch(s, op);
    }

    private void Touch(UserSession s, EditOp op)
    {
        int? id = op switch { PoseOp p => p.Id, PlaceOp p => p.Thing.Id, DeleteOp d => d.Thing.Id, SetOp t => t.Id, _ => null };
        if (id is int i) _touchedBy[(op.MapId, i)] = s.Username;
    }

    /// <summary>What Undo would take back, or null.</summary>
    internal string? NextUndo(UserSession s) => Stacks(s).Undo.Count > 0 ? Stacks(s).Undo[^1].What : null;
    internal string? NextRedo(UserSession s) => Stacks(s).Redo.Count > 0 ? Stacks(s).Redo[^1].What : null;

    private void Undo(UserSession s, Action<IMessage> reply)
    {
        var (undo, redo) = Stacks(s);
        if (undo.Count == 0) { Say(reply, "Nothing to undo."); return; }
        var op = undo[^1];
        if (!Reverse(s, op, forward: false, out string why)) { Say(reply, $"Cannot undo: {why}"); return; }
        undo.RemoveAt(undo.Count - 1);
        redo.Add(op);
        Touch(s, op);
        Say(reply, $"Undid: {op.What}.");
        Notify(s, $"{s.Username} undid: {op.What}.");
        Refresh(s, reply);
    }

    private void Redo(UserSession s, Action<IMessage> reply)
    {
        var (undo, redo) = Stacks(s);
        if (redo.Count == 0) { Say(reply, "Nothing to redo."); return; }
        var op = redo[^1];
        if (!Reverse(s, op, forward: true, out string why)) { Say(reply, $"Cannot redo: {why}"); return; }
        redo.RemoveAt(redo.Count - 1);
        undo.Add(op);
        Touch(s, op);
        Say(reply, $"Redid: {op.What}.");
        Notify(s, $"{s.Username} redid: {op.What}.");
        Refresh(s, reply);
    }

    /// <summary>
    /// Takes an operation back (forward false) or does it again (forward true), if the thing is still as
    /// the operation, or its undo, left it. If somebody has changed it since, nothing happens and
    /// <paramref name="why"/> says who.
    /// </summary>
    private bool Reverse(UserSession s, EditOp op, bool forward, out string why)
    {
        why = "";
        if (!op.MapId.Equals(s.CurrentMapId, StringComparison.OrdinalIgnoreCase)) { why = "that was on another map."; return false; }
        if (!_maps.TryGetMap(op.MapId, out var world, out _, out _, out _)) { why = "the map is not loaded."; return false; }
        var authored = _maps.AuthoredEntities(op.MapId);
        string ChangedBy(int id, string name)
            => _touchedBy.TryGetValue((op.MapId, id), out var who) && !who.Equals(s.Username, StringComparison.OrdinalIgnoreCase)
                ? $"{who} has changed {name} since." : $"{name} has been changed since.";

        switch (op)
        {
            case PoseOp p:
            {
                if (!authored.TryGetValue(p.Id, out var e) || !Editable(world, e)) { why = $"{p.Name} has been deleted."; return false; }
                var expect = forward ? p.Before : p.After;
                if (!PoseOf(world, e).Near(expect)) { why = ChangedBy(p.Id, p.Name); return false; }
                var to = forward ? p.After : p.Before;
                if (BlockedBy(world, e, to) is { } who) { why = $"that would put {p.Name} through {who}."; return false; }
                ApplyPose(op.MapId, world, e, to);
                Record(op.MapId, p.Id, world, e);
                return true;
            }
            case PlaceOp p:
            {
                bool there = authored.TryGetValue(p.Thing.Id, out var e) && world.IsAlive(e);
                if (forward)
                {
                    if (there) { why = $"{p.Thing.Data.Name ?? p.Thing.Data.PrefabId} is already there."; return false; }
                    return Restore(op.MapId, p.Thing, out why);
                }
                if (!there) { why = "it has been deleted already."; return false; }
                if (!PoseOf(world, e).Near(PoseOf(p.Thing.Data))) { why = ChangedBy(p.Thing.Id, NameOf(world, e)); return false; }
                Remove(op.MapId, world, e, p.Thing.Id);
                return true;
            }
            case DeleteOp d:
            {
                bool there = authored.TryGetValue(d.Thing.Id, out var e) && world.IsAlive(e);
                if (!forward)
                {
                    if (there) { why = $"{d.Name} is already back."; return false; }
                    return Restore(op.MapId, d.Thing, out why);
                }
                if (!there) { why = $"{d.Name} has been deleted already."; return false; }
                if (!PoseOf(world, e).Near(PoseOf(d.Thing.Data))) { why = ChangedBy(d.Thing.Id, d.Name); return false; }
                Remove(op.MapId, world, e, d.Thing.Id);
                return true;
            }
            case SetOp t:
            {
                if (!authored.TryGetValue(t.Id, out var e) || !Editable(world, e)) { why = $"{t.Name} has been deleted."; return false; }
                var setting = EntitySettings.Named(t.Path);
                if (setting == null) { why = "that setting is gone."; return false; }
                string now = setting.Get(world, e);
                string expect = forward ? (t.Before ?? now) : t.After;
                if (!SameValue(now, expect)) { why = ChangedBy(t.Id, t.Name); return false; }
                string to = forward ? t.After : (t.Before ?? now);
                setting.Set(world, e, to);
                KeepSetting(op.MapId, t.Id, world, e, t.Path, to);
                _server.SyncAudioComponent(e.Id);
                return true;
            }
            case SpawnOp sp:
            {
                if (!_maps.TryGetMapData(op.MapId, out var d)) { why = "the map is not loaded."; return false; }
                var expect = forward ? sp.BeforePosition : sp.AfterPosition;
                if (Vector3.Distance(d.SpawnPoint.Position, expect) > 1e-3f) { why = "the spawn point has been moved since."; return false; }
                if (forward) ApplySpawn(op.MapId, sp.AfterPosition, sp.AfterRotation, new OverlaySpawn { Position = sp.AfterPosition, Rotation = sp.AfterRotation });
                else ApplySpawn(op.MapId, sp.BeforePosition, sp.BeforeRotation, sp.BeforeOverlay == null ? null : Copy(sp.BeforeOverlay));
                return true;
            }
            case ModelOp m:
            {
                int expect = forward ? m.Before : m.After;
                if (Models.CurrentVersion(m.Kind, m.Id) != expect)
                { why = $"the {ModelKinds.Spoken(m.Kind)} {m.Id} has been changed since; it is at version {Models.CurrentVersion(m.Kind, m.Id)}."; return false; }
                if (!s.Can(Permissions.EditModels)) { why = "changing models needs edit-models."; return false; }
                var update = Models.SetCurrent(m.Kind, m.Id, forward ? m.After : m.Before);
                if (update == null) { why = "that version is not kept."; return false; }
                _server.BroadcastModel(update);
                return true;
            }
        }
        why = "that cannot be undone.";
        return false;
    }

    private static bool SameValue(string a, string b)
    {
        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            return Math.Abs(x - y) <= 1e-4 * Math.Max(1, Math.Abs(y));
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    // ── Poses ───────────────────────────────────────────────────────────────────────────────────

    private static Pose PoseOf(World world, Entity e)
    {
        var t = world.Get<Transform>(e);
        return new Pose(t.Position, t.Rotation, t.Scale);
    }

    private static Pose PoseOf(EntityData d) => new(d.Position, d.Rotation, d.Scale);

    /// <summary>The size a prefab is at a scale, metres; zero if it has no body.</summary>
    private Vector3 SizeAt(string prefab, Vector3 scale)
        => _maps.Prefabs.TryGetValue(prefab.ToLowerInvariant(), out var t) && t.ColliderSize is { } size ? size * scale : Vector3.Zero;

    /// <summary>
    /// Puts a thing where a pose says, in the server's world: its transform, its body's size if the scale
    /// changed, the static grid and triangle world (MapManager.RefreshGrid), and its state and definition
    /// to every client that has it.
    /// </summary>
    private void ApplyPose(string mapId, World world, Entity e, Pose pose)
    {
        ref var t = ref world.Get<Transform>(e);
        var oldScale = t.Scale;
        t.Position = pose.Position;
        t.Rotation = pose.Rotation;
        t.Scale = pose.Scale;
        t.IsDirty = true;
        if (world.Has<ColliderComponent>(e) && Vector3.Distance(oldScale, pose.Scale) > 1e-6f)
        {
            ref var c = ref world.Get<ColliderComponent>(e);
            var size = SizeAt(PrefabOf(world, e), pose.Scale);
            c.Size = size != Vector3.Zero ? size
                   : new Vector3(Ratio(c.Size.X, oldScale.X, pose.Scale.X), Ratio(c.Size.Y, oldScale.Y, pose.Scale.Y), Ratio(c.Size.Z, oldScale.Z, pose.Scale.Z));
        }
        // A doorway's opening goes with its leaf.
        if (world.Has<PortalComponent>(e))
        {
            ref var p = ref world.Get<PortalComponent>(e);
            p.OpeningCentre = pose.Position;
            p.OpeningRotation = pose.Rotation;
        }
        _maps.RefreshGrid(mapId);
        _server.SyncAudioComponent(e.Id);
    }

    private static float Ratio(float size, float from, float to) => MathF.Abs(from) > 1e-6f ? size / from * to : size;

    /// <summary>The player a solid thing at a pose would stand inside, or null. Things with no solid body never block.</summary>
    private string? BlockedBy(World world, Entity e, Pose pose)
    {
        if (!world.Has<ColliderComponent>(e)) return null;
        var c = world.Get<ColliderComponent>(e);
        if (!c.IsSolid || c.Shape != ColliderShape.Box) return null;
        var size = SizeAt(PrefabOf(world, e), pose.Scale);
        if (size == Vector3.Zero) size = c.Size;
        return BlockedBy(world, pose.Position, pose.Rotation, size);
    }

    private static string? BlockedBy(World world, Vector3 at, Quaternion rotation, Vector3 size)
    {
        var half = CompositeAcoustics.AxisAlignedHalfExtents(size * 0.5f, rotation);
        var lo = at - half;
        var hi = at + half;
        string? who = null;
        world.Query(new QueryDescription().WithAll<PlayerComponent, Transform>(), (ref PlayerComponent p, ref Transform t) =>
        {
            if (who != null) return;
            var f = t.Position;
            float r = PhysicsConstants.PlayerRadius;
            if (f.X + r <= lo.X || f.X - r >= hi.X || f.Z + r <= lo.Z || f.Z - r >= hi.Z) return;
            if (f.Y + PhysicsConstants.PlayerHeight <= lo.Y || f.Y + FootPadding >= hi.Y) return;
            who = p.Username;
        });
        return who;
    }

    // ── Keeping: the overlay and the map's own data ─────────────────────────────────────────────

    private readonly Dictionary<string, Dictionary<int, EntityData>> _data = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A map's entries by id, as the map's data holds them now (base and added).</summary>
    private Dictionary<int, EntityData> DataOf(string mapId)
    {
        if (_data.TryGetValue(mapId, out var d)) return d;
        d = new Dictionary<int, EntityData>();
        if (_maps.TryGetMapData(mapId, out var map))
            foreach (var e in map.Entities) d.TryAdd(e.EntityId, e);
        _data[mapId] = d;
        return d;
    }

    /// <summary>Keeps where a thing is now: in its overlay entry (a change of a map-file thing, or the addition) and in the map's data.</summary>
    private void Record(string mapId, int id, World world, Entity e)
    {
        var pose = PoseOf(world, e);
        var o = Overlays.Get(mapId);
        var data = DataOf(mapId);
        if (o.AdditionFor(id) is { } a)
        {
            a.Entity.Position = pose.Position; a.Entity.Rotation = pose.Rotation; a.Entity.Scale = pose.Scale;
        }
        else
        {
            var c = ChangeEntry(o, mapId, id, world, e);
            c.Position = pose.Position; c.Rotation = pose.Rotation; c.Scale = pose.Scale;
            // Back where the map file has it, with nothing else changed: nothing to keep.
            if ((c.Settings == null || c.Settings.Count == 0) && c.WasRotation.HasValue && c.WasScale.HasValue
                && pose.Near(new Pose(c.Was, c.WasRotation.Value, c.WasScale.Value)))
                o.Changed.Remove(c);
        }
        if (data.TryGetValue(id, out var entry))
        {
            entry.Position = pose.Position; entry.Rotation = pose.Rotation; entry.Scale = pose.Scale;
        }
        Overlays.Save(mapId);
    }

    /// <summary>A map-file thing's change entry, made from how the map's data has it now if there is none.</summary>
    private OverlayChange ChangeEntry(MapOverlay o, string mapId, int id, World world, Entity e)
    {
        var c = o.ChangeFor(id);
        if (c != null) return c;
        DataOf(mapId).TryGetValue(id, out var was);
        var pose = PoseOf(world, e);
        c = new OverlayChange
        {
            Id = id, Prefab = was?.PrefabId ?? PrefabOf(world, e), Was = was?.Position ?? pose.Position,
            WasRotation = was?.Rotation, WasScale = was?.Scale,
            Position = pose.Position, Rotation = pose.Rotation, Scale = pose.Scale,
        };
        o.Changed.Add(c);
        return c;
    }

    /// <summary>Keeps one setting of a thing in its overlay entry.</summary>
    private void KeepSetting(string mapId, int id, World world, Entity e, string path, string value)
    {
        var o = Overlays.Get(mapId);
        Dictionary<string, string> settings;
        if (o.AdditionFor(id) is { } a) settings = a.Settings ??= new Dictionary<string, string>();
        else settings = ChangeEntry(o, mapId, id, world, e).Settings ??= new Dictionary<string, string>();
        settings[EntitySettings.Named(path)?.Field.Path ?? path] = value;
        if (path.Equals("Name", StringComparison.OrdinalIgnoreCase) && DataOf(mapId).TryGetValue(id, out var entry)) entry.Name = value;
        Overlays.Save(mapId);
    }

    /// <summary>Everything needed to put a thing back exactly as it is now.</summary>
    private Snapshot Take(string mapId, World world, Entity e, int id)
    {
        var o = Overlays.Get(mapId);
        var data = DataOf(mapId);
        var pose = PoseOf(world, e);
        data.TryGetValue(id, out var entry);
        var copy = entry != null ? MapOverlayStore.Clone(entry) : new EntityData { EntityId = id, PrefabId = PrefabOf(world, e) };
        copy.Position = pose.Position; copy.Rotation = pose.Rotation; copy.Scale = pose.Scale;
        var add = o.AdditionFor(id);
        var change = o.ChangeFor(id);
        var settings = add?.Settings ?? change?.Settings;
        return new Snapshot(id, copy, settings == null ? null : new Dictionary<string, string>(settings), add != null,
                            change == null ? null : new OverlayChange
                            {
                                Id = change.Id, Prefab = change.Prefab, Was = change.Was, Position = change.Position,
                                Rotation = change.Rotation, Scale = change.Scale,
                                Settings = change.Settings == null ? null : new Dictionary<string, string>(change.Settings),
                                WasRotation = change.WasRotation, WasScale = change.WasScale,
                            },
                            change?.Was ?? entry?.Position ?? pose.Position);
    }

    /// <summary>Takes a thing off the map: the world, every client, the overlay and the map's data.</summary>
    private void Remove(string mapId, World world, Entity e, int id)
    {
        var o = Overlays.Get(mapId);
        var data = DataOf(mapId);
        if (o.AdditionFor(id) is { } a) o.Added.Remove(a);
        else
        {
            var c = o.ChangeFor(id);
            if (c != null) o.Changed.Remove(c);
            data.TryGetValue(id, out var entry);
            o.Removed.RemoveAll(r => r.Id == id);
            o.Removed.Add(new OverlayRemoval { Id = id, Prefab = c?.Prefab ?? entry?.PrefabId ?? PrefabOf(world, e), Was = c?.Was ?? entry?.Position ?? PoseOf(world, e).Position });
        }
        if (data.Remove(id, out var gone) && _maps.TryGetMapData(mapId, out var map)) map.Entities.Remove(gone);
        int runtime = e.Id;
        _maps.DestroyEntity(mapId, e);
        _server.BroadcastRemoval(mapId, runtime);
        _maps.AuthoredEntities(mapId).Remove(id);
        Overlays.Save(mapId);
    }

    /// <summary>Puts a thing on the map from a snapshot: made again, its settings, the overlay and the map's data.</summary>
    private bool Restore(string mapId, Snapshot thing, out string why)
    {
        why = "";
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) { why = "the map is not loaded."; return false; }
        var d = thing.Data;
        var size = SizeAt(d.PrefabId, d.Scale);
        if (_maps.Prefabs.TryGetValue(d.PrefabId.ToLowerInvariant(), out var template) && template.ColliderSize.HasValue
            && (template.IsSolid ?? true) && (template.Shape ?? ColliderShape.Box) == ColliderShape.Box
            && BlockedBy(world, d.Position, d.Rotation, size) is { } who)
        { why = $"that would put {d.Name ?? template.Name} through {who}."; return false; }

        Entity e;
        try { e = _maps.SpawnPrefab(mapId, d.PrefabId, d.Position, d.Rotation, d.Scale, d.Name); }
        catch (Exception ex) { why = ex.Message; return false; }
        if (e == Entity.Null) { why = "it could not be made."; return false; }
        if (thing.Settings != null)
            foreach (var (path, value) in thing.Settings) EntitySettings.TrySet(world, e, path, value, out _);
        _maps.AuthoredEntities(mapId)[thing.Id] = e;

        var o = Overlays.Get(mapId);
        var copy = MapOverlayStore.Clone(d);
        if (thing.Added)
        {
            o.Added.RemoveAll(a => a.Entity.EntityId == thing.Id);
            o.Added.Add(new OverlayAddition { Entity = MapOverlayStore.Clone(d), Settings = thing.Settings == null ? null : new Dictionary<string, string>(thing.Settings) });
        }
        else
        {
            o.Removed.RemoveAll(r => r.Id == thing.Id);
            if (thing.Change != null)
            {
                o.Changed.RemoveAll(c => c.Id == thing.Id);
                o.Changed.Add(thing.Change);
            }
        }
        DataOf(mapId)[thing.Id] = copy;
        if (_maps.TryGetMapData(mapId, out var map)) map.Entities.Add(copy);
        Overlays.Save(mapId);
        return true;
    }

    // ── The operations ──────────────────────────────────────────────────────────────────────────

    /// <summary>Moves the selected thing to a pose, if that keeps it out of everybody's way, and keeps it.</summary>
    private void Repose(UserSession s, Action<IMessage> reply, string verb, Func<World, Entity, Pose, Pose?> to, Func<string, string> said)
    {
        if (!TrySelected(s, reply, out var world, out var e, out int id)) return;
        var before = PoseOf(world, e);
        var after = to(world, e, before);
        if (after == null) return;
        string name = NameOf(world, e);
        if (BlockedBy(world, e, after.Value) is { } who) { Say(reply, $"Not {verb}: that would put {name} through {who}."); return; }
        ApplyPose(s.CurrentMapId, world, e, after.Value);
        Record(s.CurrentMapId, id, world, e);
        Push(s, new PoseOp(s.CurrentMapId, id, name, verb, before, after.Value));
        Say(reply, said(name));
        Notify(s, $"{s.Username} {verb} {name}.");
        Refresh(s, reply);
    }

    private static bool TryNumber(string s, out float f)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f) && float.IsFinite(f);

    private void Move(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 3 || !TryNumber(args[0], out float east) || !TryNumber(args[1], out float north) || !TryNumber(args[2], out float up))
        { Say(reply, "Say /edit move EAST NORTH UP, in metres: /edit move 1 0 0 is a metre east. Negative goes west, south, down."); return; }
        if (MathF.Abs(east) > 1000 || MathF.Abs(north) > 1000 || MathF.Abs(up) > 1000) { Say(reply, "A move is at most 1000 metres each way."); return; }
        var by = PlayerCoordinates.ToWorld(east, north, up);
        Repose(s, reply, "moved", (_, _, p) => p with { Position = p.Position + by },
               name => $"Moved {name} {Offset(by)}.");
    }

    /// <summary>"0.5 metres north and 1 metre up".</summary>
    private static string Offset(Vector3 by)
    {
        var parts = new List<string>();
        if (MathF.Abs(by.Z) > 1e-4f) parts.Add($"{Metres(MathF.Abs(by.Z))} {(by.Z > 0 ? "north" : "south")}");
        if (MathF.Abs(by.X) > 1e-4f) parts.Add($"{Metres(MathF.Abs(by.X))} {(by.X > 0 ? "east" : "west")}");
        if (MathF.Abs(by.Y) > 1e-4f) parts.Add($"{Metres(MathF.Abs(by.Y))} {(by.Y > 0 ? "up" : "down")}");
        return parts.Count == 0 ? "nowhere" : string.Join(" and ", parts);
    }

    /// <summary>A direction word as a unit step: compass words, up and down, and your own forward, back,
    /// left and right, each squared to the nearest of north, east, south and west.</summary>
    internal static bool TryDirection(string word, float yaw, out Vector3 step)
    {
        int q = Quarter(yaw);
        step = word.ToLowerInvariant() switch
        {
            "north" or "n" => Vector3.UnitZ,
            "south" or "s" => -Vector3.UnitZ,
            "east" or "e" => Vector3.UnitX,
            "west" or "w" => -Vector3.UnitX,
            "up" or "u" => Vector3.UnitY,
            "down" or "d" => -Vector3.UnitY,
            "forward" or "forwards" or "ahead" or "f" => Compass4[q],
            "back" or "backward" or "backwards" or "b" => Compass4[(q + 2) % 4],
            "right" or "r" => Compass4[(q + 1) % 4],
            "left" or "l" => Compass4[(q + 3) % 4],
            _ => Vector3.Zero,
        };
        return step != Vector3.Zero;
    }

    private void Nudge(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (!TryBody(s, reply, out _, out _, out float yaw)) return;
        var hand = HandOf(s);
        if (args.Length == 0 || !TryDirection(args[0], yaw, out var dir))
        { Say(reply, "Say /edit nudge north, south, east, west, up, down, forward, back, left or right, and metres if not the step."); return; }
        float metres = hand.Step;
        if (args.Length > 1 && (!TryNumber(args[1], out metres) || metres <= 0 || metres > 50)) { Say(reply, "A nudge is up to 50 metres."); return; }
        var by = dir * metres;
        Repose(s, reply, "moved", (_, _, p) => p with { Position = p.Position + by }, name => $"Moved {name} {Offset(by)}.");
    }

    private void Turn(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (args.Length == 0 || !TryNumber(args[0], out float degrees) || MathF.Abs(degrees) > 360)
        { Say(reply, "Say /edit turn DEGREES: positive is clockwise, negative anticlockwise."); return; }
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180f);
        Repose(s, reply, "turned", (_, _, p) => p with { Rotation = Quaternion.Normalize(Quaternion.Concatenate(p.Rotation, turn)) },
               name => $"Turned {name} {FieldDescriptor.Format(MathF.Abs(degrees))} degrees {(degrees >= 0 ? "clockwise" : "anticlockwise")}.");
    }

    private void Face(UserSession s, string[] args, Action<IMessage> reply)
    {
        string word = string.Join(" ", args).Trim().ToLowerInvariant().Replace("-", " ");
        int index = Array.IndexOf(Compass8, word);
        if (index < 0) { Say(reply, "Say /edit face north, north east, east, south east, south, south west, west or north west."); return; }
        float yaw = index * MathF.PI / 4f;
        Repose(s, reply, "turned", (_, _, p) => p with { Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f) },
               name => $"{name} faces {Compass8[index]}.");
    }

    /// <summary>Where a thing put down by a player goes: standing on the ground at their feet, or, if it is
    /// solid, just in front of them, squared to the way they face, so they are not inside it.</summary>
    private static Pose InFront(Vector3 feet, float yaw, Vector3 size, bool solid, Pose pose)
    {
        int q = Quarter(yaw);
        var rotation = Quaternion.CreateFromYawPitchRoll(q * MathF.PI / 2f, 0f, 0f);
        var at = feet + new Vector3(0f, size.Y * 0.5f, 0f);
        if (solid) at += Compass4[q] * (PhysicsConstants.PlayerRadius + size.Z * 0.5f + 0.1f);
        return pose with { Position = at, Rotation = rotation };
    }

    private void Bring(UserSession s, Action<IMessage> reply)
    {
        if (!TryBody(s, reply, out _, out var feet, out float yaw)) return;
        Repose(s, reply, "brought", (w, e, p) =>
        {
            bool solid = w.Has<ColliderComponent>(e) && w.Get<ColliderComponent>(e).IsSolid;
            var size = w.Has<ColliderComponent>(e) ? w.Get<ColliderComponent>(e).Size : Vector3.Zero;
            var placed = InFront(feet, yaw, size, solid, p);
            // Keep its own turn: bringing a thing is moving it, not turning it.
            return placed with { Rotation = p.Rotation };
        }, name => $"Brought {name} to you.");
    }

    private void Duplicate(UserSession s, Action<IMessage> reply)
    {
        if (!TryBody(s, reply, out _, out _, out float yaw)) return;
        if (!TrySelected(s, reply, out var world, out var e, out int id)) return;
        var source = Take(s.CurrentMapId, world, e, id);
        var dir = Compass4[Quarter(yaw)];
        var (lo, hi) = Box(world, e);
        float along = MathF.Max(0.5f, MathF.Abs(Vector3.Dot(hi - lo, dir)));
        var o = Overlays.Get(s.CurrentMapId);
        int newId = o.NextId++;
        var data = MapOverlayStore.Clone(source.Data);
        data.EntityId = newId;
        data.Position += dir * along;
        data.Tile = null;
        var thing = new Snapshot(newId, data, source.Settings, Added: true, Change: null, Was: data.Position);
        if (!Restore(s.CurrentMapId, thing, out string why)) { o.NextId--; Say(reply, $"Not copied: {why}"); return; }
        string name = NameOf(world, e);
        Push(s, new PlaceOp(s.CurrentMapId, thing, "copied", name));
        HandOf(s).Selected = newId;
        Say(reply, $"Copied {name}, {Metres(along)} {Compass4Names[Quarter(yaw)]} of it. The copy is selected.");
        Notify(s, $"{s.Username} copied {name}.");
        Refresh(s, reply);
    }

    private void Delete(UserSession s, Action<IMessage> reply)
    {
        if (!TrySelected(s, reply, out var world, out var e, out int id)) return;
        string name = NameOf(world, e);
        var thing = Take(s.CurrentMapId, world, e, id);
        Remove(s.CurrentMapId, world, e, id);
        Push(s, new DeleteOp(s.CurrentMapId, thing, name));
        HandOf(s).Selected = null;
        Say(reply, $"Deleted {name}. Undo puts it back.");
        Notify(s, $"{s.Username} deleted {name}.");
        if (!s.IsTextClient) SendMenu(s, "root", reply, refresh: true);
    }

    private void Place(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (args.Length == 0) { Say(reply, "Say /edit place PREFAB. /edit prefabs lists them."); return; }
        if (!TryBody(s, reply, out var world, out var feet, out float yaw)) return;
        string prefab = args[0].ToLowerInvariant();
        if (!_maps.Prefabs.TryGetValue(prefab, out var t)) { Say(reply, $"There is no prefab called {args[0]}. /edit prefabs lists them."); return; }
        if (!MayPlace(s, t, out string refusal)) { Say(reply, refusal); return; }
        var size = t.ColliderSize ?? Vector3.Zero;
        bool solid = t.ColliderSize.HasValue && (t.IsSolid ?? true);
        var pose = InFront(feet, yaw, size, solid, new Pose(feet, Quaternion.Identity, Vector3.One));
        var o = Overlays.Get(s.CurrentMapId);
        int id = o.NextId++;
        var data = new EntityData { EntityId = id, PrefabId = t.Id, Position = pose.Position, Rotation = pose.Rotation, Scale = Vector3.One };
        var thing = new Snapshot(id, data, null, Added: true, Change: null, Was: pose.Position);
        if (!Restore(s.CurrentMapId, thing, out string why)) { o.NextId--; Say(reply, $"Not placed: {why}"); return; }
        Push(s, new PlaceOp(s.CurrentMapId, thing, "placed", t.Name));
        HandOf(s).Selected = id;
        string where = solid ? $"{Metres(PhysicsConstants.PlayerRadius + size.Z * 0.5f + 0.1f)} in front of you" : "at your feet";
        Say(reply, $"Placed {t.Name} {where}, facing {Compass4Names[Quarter(yaw)]}. It is selected.");
        Notify(s, $"{s.Username} placed {t.Name}.");
        Refresh(s, reply);
    }

    /// <summary>Whether a player may put this prefab on the map: never a premium item, and things to carry
    /// only with the give permission, since a thing on the floor is a thing anybody can pick up.</summary>
    private static bool MayPlace(UserSession s, PrefabTemplate t, out string refusal)
    {
        refusal = "";
        if (t.Premium) { refusal = $"{t.Name} cannot be placed: it is a premium item."; return false; }
        if (string.Equals(t.WeaponId, OpenFPS.Common.AdminGun.WeaponId, StringComparison.OrdinalIgnoreCase))
        { refusal = $"{t.Name} cannot be placed."; return false; }
        if (t.IsItem && !s.Can("give")) { refusal = $"Placing things to carry, like {t.Name}, needs the give permission."; return false; }
        return true;
    }

    // ── Settings ────────────────────────────────────────────────────────────────────────────────

    private void SaySettings(UserSession s, Action<IMessage> reply)
    {
        if (!TrySelected(s, reply, out var world, out var e, out _)) return;
        var lines = EntitySettings.For(world, e).Select(x => $"{x.Field.Path}: {x.Field.Label}, {x.Field.Say(x.Get(world, e))}");
        Say(reply, $"{NameOf(world, e)}: " + string.Join("; ", lines) + ". /edit set FIELD VALUE changes one.");
    }

    /// <summary>/edit set FIELD VALUE, /edit up FIELD, /edit down FIELD: one of the selected thing's settings.</summary>
    private void SetField(UserSession s, string[] args, Action<IMessage> reply, int step)
    {
        if (args.Length < (step == 0 ? 2 : 1)) { Say(reply, step == 0 ? "Say /edit set FIELD VALUE. /edit settings lists them." : "Say /edit up FIELD or /edit down FIELD."); return; }
        if (!TrySelected(s, reply, out var world, out var e, out int id)) return;
        var setting = EntitySettings.Named(args[0]);
        string name = NameOf(world, e);
        if (setting == null || !setting.Applies(world, e)) { Say(reply, $"{name} has no setting called {args[0]}. /edit settings lists them."); return; }
        var field = setting.Field;
        string before = setting.Get(world, e);
        string typed = step == 0 ? string.Join(" ", args[1..]) : field.Stepped(before, step) ?? before;
        if (!field.TryParse(typed, out string value, out string error)) { Say(reply, error); return; }
        if (SameValue(before, value)) { Say(reply, $"{Capital(field.Label)} is already {field.Say(value)}{(step != 0 ? ", the end of its range" : "")}."); return; }

        if (setting.Keep == EntitySettings.Keeping.Scale)
        {
            // A size is the thing's scale: changed as a move, so it is checked against the people near it.
            float want = float.Parse(value, CultureInfo.InvariantCulture);
            Repose(s, reply, "resized", (w, ent, p) =>
            {
                var size = w.Get<ColliderComponent>(ent).Size;
                float now = EntitySettings.Axis(size, setting.Axis);
                if (now <= 1e-6f) { Say(reply, $"{name} has no {field.Label} to change."); return null; }
                float factor = want / now;
                var scale = p.Scale;
                scale = setting.Axis switch { 0 => scale with { X = scale.X * factor }, 1 => scale with { Y = scale.Y * factor }, _ => scale with { Z = scale.Z * factor } };
                return p with { Scale = scale };
            }, n => $"{Capital(field.Label)} of {n}, {field.Say(value)}.");
            return;
        }

        setting.Set(world, e, value);
        KeepSetting(s.CurrentMapId, id, world, e, field.Path, value);
        _server.SyncAudioComponent(e.Id);
        Push(s, new SetOp(s.CurrentMapId, id, name, field.Path, field.Label, before, value));
        Say(reply, $"{Capital(field.Label)}, {field.Say(value)}.");
        Notify(s, $"{s.Username} set the {field.Label} of {name}.");
        Refresh(s, reply);
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
