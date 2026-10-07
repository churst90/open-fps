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

/// <summary>What choosing a prefab from the Place menu does (docs/WORLD_EDITOR.md section 11.4).</summary>
public enum PlaceMode { Feet, Cursor, Preview }

/// <summary>
/// Placing, phase 2: search, preview by ear, rows, again, at the build cursor, and holding more than
/// one thing for a group.
/// </summary>
public sealed partial class WorldEditor
{
    /// <summary>How long a preview plays, seconds.</summary>
    public const float PreviewSeconds = 6f;
    /// <summary>The ids previews are sent under: far above any entity's, so a preview never meets one.</summary>
    public const int FirstPreviewId = 1_950_000_000;
    private int _nextPreview;

    /// <summary>The most copies one row makes.</summary>
    public const int MaxRow = 50;

    private void PlaceCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (args.Length >= 2 && args[0].Equals("mode", StringComparison.OrdinalIgnoreCase))
        {
            var hand = HandOf(s);
            hand.Mode = args[1].ToLowerInvariant() switch { "cursor" => PlaceMode.Cursor, "preview" => PlaceMode.Preview, _ => PlaceMode.Feet };
            Say(reply, $"Choosing a prefab will {PlaceModeWords(hand.Mode)}.");
            if (!s.IsTextClient) SendMenu(s, "place", reply, refresh: false);
            return;
        }
        if (args.Length >= 2 && args[0].Equals("group", StringComparison.OrdinalIgnoreCase))
        {
            PlaceGroup(s, args[1], reply);
            return;
        }
        bool atCursor = args.Length >= 3 && args[^2].Equals("at", StringComparison.OrdinalIgnoreCase) && args[^1].Equals("cursor", StringComparison.OrdinalIgnoreCase);
        Place(s, atCursor ? args[..^2] : args, reply, atCursor);
    }

    /// <summary>Where a thing put down now goes: at your feet (in front of you if solid), or at the build cursor.</summary>
    private bool PlacePose(UserSession s, Action<IMessage> reply, bool atCursor, Vector3 size, bool solid, out Pose pose, out string where)
    {
        pose = default; where = "";
        if (!TryBody(s, reply, out _, out var feet, out float yaw)) return false;
        if (atCursor)
        {
            if (!s.Build.Placed) { Say(reply, "There is no build cursor yet. /origin sets one where you stand, and /at moves it."); return false; }
            // Standing on the cursor, as /put does: the thing's bottom at the cursor.
            var at = s.Build.WorldCursor + new Vector3(0f, size.Y * 0.5f, 0f);
            pose = new Pose(at, s.Build.Facing(0f), Vector3.One);
            where = $"at the build cursor, {s.Build.Describe()}";
            return true;
        }
        pose = InFront(feet, yaw, size, solid, new Pose(feet, Quaternion.Identity, Vector3.One));
        where = solid ? $"{Metres(PhysicsConstants.PlayerRadius + size.Z * 0.5f + 0.1f)} in front of you" : "at your feet";
        return true;
    }

    private void Place(UserSession s, string[] args, Action<IMessage> reply, bool atCursor)
    {
        if (args.Length == 0) { Say(reply, "Say /edit place PREFAB, and at cursor to put it at the build cursor. /edit find WORDS searches."); return; }
        string prefab = args[0].ToLowerInvariant();
        if (!_maps.Prefabs.TryGetValue(prefab, out var t)) { Say(reply, $"There is no prefab called {args[0]}. /edit find WORDS searches."); return; }
        if (Models.IsRetired(PrefabKind.KindId, t.Id)) { Say(reply, $"{t.Name} is retired, so it is not offered for new things."); return; }
        if (!MayPlace(s, t, out string refusal) || Full(s, 1, out refusal)) { Say(reply, refusal); return; }
        var size = t.ColliderSize ?? Vector3.Zero;
        bool solid = t.ColliderSize.HasValue && (t.IsSolid ?? true);
        if (!PlacePose(s, reply, atCursor, size, solid, out var pose, out string where)) return;
        if (!PlaceOne(s.CurrentMapId, t, pose, null, null, out var thing, out string why)) { Say(reply, $"Not placed: {why}"); return; }
        Push(s, new PlaceOp(s.CurrentMapId, thing, "placed", t.Name));
        var hand = HandOf(s);
        hand.Selected = thing.Id;
        hand.LastPlaced = t.Id;
        hand.LastAtCursor = atCursor;
        Say(reply, $"Placed {t.Name} {where}, facing {CompassOf(YawOf(pose.Rotation))}. It is selected.");
        Notify(s, $"{s.Username} placed {t.Name}.");
        Refresh(s, reply);
    }

    /// <summary>One new thing on the map, kept in its overlay as an addition.</summary>
    private bool PlaceOne(string mapId, PrefabTemplate t, Pose pose, string? name, Dictionary<string, string>? settings, out Snapshot thing, out string why)
    {
        var o = Overlays.Get(mapId);
        int id = o.NextId++;
        var data = new EntityData { EntityId = id, PrefabId = t.Id, Position = pose.Position, Rotation = pose.Rotation, Scale = pose.Scale, Name = name };
        thing = new Snapshot(id, data, settings, Added: true, Change: null, Was: pose.Position);
        if (Restore(mapId, thing, out why)) return true;
        o.NextId--;
        return false;
    }

    /// <summary>/edit again: what was placed last, again, where you stand now (or at the build cursor, if it was put there).</summary>
    private void Again(UserSession s, Action<IMessage> reply)
    {
        var hand = HandOf(s);
        if (hand.LastPlaced == null) { Say(reply, "Nothing has been placed yet to place again."); return; }
        Place(s, new[] { hand.LastPlaced }, reply, hand.LastAtCursor);
    }

    // ── Preview ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// /edit preview PREFAB: the prefab made where you stand, for you alone, for a few seconds, and taken
    /// away. Nothing on the map changes and nobody else hears it: it is made in a world of its own and
    /// sent to you as a definition under an id no entity has, then removed.
    /// </summary>
    private void Preview(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (args.Length == 0) { Say(reply, "Say /edit preview PREFAB."); return; }
        if (!_maps.Prefabs.TryGetValue(args[0].ToLowerInvariant(), out var t)) { Say(reply, $"There is no prefab called {args[0]}."); return; }
        if (!TryBody(s, reply, out _, out var feet, out float yaw)) return;
        if (!t.HasEmitter) { Say(reply, $"{t.Name} makes no sound of its own, so there is nothing to hear. Place it to hear how it sounds when struck or walked on."); return; }
        if (s.IsTextClient) { Say(reply, $"{t.Name} would be played to you here; a text client cannot hear it."); return; }

        var scratch = World.Create();
        try
        {
            // A step in front of you, at ear height for a thing on the ground: heard, not inside your head.
            var at = feet + Compass4[Quarter(yaw)] * 2f + new Vector3(0f, (t.ColliderSize?.Y ?? 0f) * 0.5f, 0f);
            var e = _maps.PrefabRepository.Spawn(scratch, t.Id, at, Quaternion.CreateFromYawPitchRoll(Quarter(yaw) * MathF.PI / 2f, 0f, 0f));
            var def = EntityDefinitionFactory.From(scratch, e);
            int id = FirstPreviewId + (_nextPreview++ % 1000);
            def.EntityId = id;
            // Nothing to bump into: a preview is a sound, not a thing.
            var c = def.Collider; c.IsSolid = false; def.Collider = c;
            _server.SendToSession(s, def);
            int connection = s.ConnectionId;
            _server.After(TimeSpan.FromSeconds(PreviewSeconds), () =>
            {
                if (_sessions.TryGetSession(connection, out var still) && still == s)
                    _server.SendToSession(s, new EntityRemoved { EntityIds = new List<int> { id } });
            });
        }
        finally { World.Destroy(scratch); }
        Say(reply, $"Playing {t.Name} two metres in front of you for {FieldDescriptor.Format(PreviewSeconds)} seconds, to you alone.");
    }

    // ── Rows ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>/edit row COUNT [SPACING]: copies of the selected thing in a line the way you face. One undo takes the row away.</summary>
    private void Row(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (args.Length == 0 || !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 1 || count > MaxRow)
        { Say(reply, $"Say /edit row COUNT [SPACING]: up to {MaxRow} copies, spaced a width apart unless you say metres."); return; }
        float spacing = 0f;
        if (args.Length > 1 && (!TryNumber(args[1], out spacing) || spacing <= 0 || spacing > 100)) { Say(reply, "The spacing is metres, more than 0 and up to 100."); return; }
        if (!TryBody(s, reply, out _, out _, out float yaw)) return;
        if (!TrySelected(s, reply, out var world, out var e, out int id)) return;
        if (Full(s, count, out string full)) { Say(reply, full); return; }
        var source = Take(s.CurrentMapId, world, e, id);
        var dir = Compass4[Quarter(yaw)];
        var (lo, hi) = Box(world, e);
        float along = spacing > 0 ? spacing : MathF.Max(0.5f, MathF.Abs(Vector3.Dot(hi - lo, dir)));
        string name = NameOf(world, e);
        var ops = new List<EditOp>();
        int made = 0;
        string? stopped = null;
        var o = Overlays.Get(s.CurrentMapId);
        for (int i = 1; i <= count; i++)
        {
            int newId = o.NextId++;
            var data = MapOverlayStore.Clone(source.Data);
            data.EntityId = newId;
            data.Position += dir * along * i;
            data.Tile = null;
            var thing = new Snapshot(newId, data, source.Settings, Added: true, Change: null, Was: data.Position);
            if (!Restore(s.CurrentMapId, thing, out string why)) { o.NextId--; stopped = why; break; }
            ops.Add(new PlaceOp(s.CurrentMapId, thing, "copied", name));
            made++;
        }
        if (made == 0) { Say(reply, $"No row: {stopped}"); return; }
        Push(s, new BatchOp(s.CurrentMapId, ops, $"made a row of {made} {name}"));
        Say(reply, $"A row of {made} {name}, {Metres(along)} apart going {Compass4Names[Quarter(yaw)]}."
                 + (stopped != null ? $" Stopped there: {stopped}" : "") + " One undo takes the row away.");
        Notify(s, $"{s.Username} made a row of {name}.");
        Refresh(s, reply);
    }

    // ── Holding more than one ───────────────────────────────────────────────────────────────────

    /// <summary>/edit select add nearest|NAME|#ID and /edit select clear: things held together, for a group.</summary>
    private void Hold(UserSession s, string[] args, Action<IMessage> reply)
    {
        var hand = HandOf(s);
        if (args.Length == 0) { Say(reply, "Say /edit select add nearest, /edit select add NAME or /edit select add #NUMBER; /edit select clear lets go of them all."); return; }
        if (args.Length == 1 && args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            int n = hand.Held.Count;
            hand.Held.Clear();
            Say(reply, n == 0 ? "Nothing was held." : $"Let go of {Plural(n, "thing")}.");
            Refresh(s, reply);
            return;
        }
        // Select it the usual way, then hold what was selected.
        int? before = hand.Selected;
        var said = new List<IMessage>();
        Select(s, args, said.Add);
        if (hand.Selected is not int id || (id == before && said.OfType<TextEvent>().Any(t => !t.Text.StartsWith("Selected"))))
        {
            foreach (var m in said) reply(m);
            return;
        }
        if (!_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _) || !_maps.AuthoredEntities(s.CurrentMapId).TryGetValue(id, out var e)) return;
        if (hand.Held.Contains(id)) { Say(reply, $"{NameOf(world, e)} is held already; {Plural(hand.Held.Count, "thing")} held."); return; }
        hand.Held.Add(id);
        Say(reply, $"Holding {NameOf(world, e)} as well: {Plural(hand.Held.Count, "thing")} held. /edit group NAME makes them a group.");
        Refresh(s, reply);
    }
}
