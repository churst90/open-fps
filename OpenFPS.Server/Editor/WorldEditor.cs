using System.Globalization;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;


namespace OpenFPS.Server.Editor;

/// <summary>
/// The world editor on the server (docs/WORLD_EDITOR.md): /edit and the F12 menu. Every operation is
/// made here, on the tick thread, in the order it arrives; it changes the live map, is kept in the map's
/// overlay at once, goes on the editor's own undo stack, and is told to the other editors on the map.
/// The world itself reaches every client through the messages it always has: a moved thing's state and
/// definition, a removal, a new definition.
/// </summary>
public sealed partial class WorldEditor
{
    public const string Refusal = "The world editor is for this map's owner, the people they ask to edit it, and developers.";
    public const int MaxUndo = 200;
    public const float DefaultStep = 0.5f;
    /// <summary>How far from the map's middle, on any axis, the editor puts a thing: twice the largest map.</summary>
    public const float MaxDistanceMetres = 20_000f;
    /// <summary>The body a solid thing must keep clear of: a player's cylinder, feet to head.</summary>
    private const float FootPadding = 0.15f;

    private readonly MapManager _maps;
    private readonly GameServer _server;
    private readonly SessionManager _sessions;

    public WorldEditor(MapManager maps, GameServer server, SessionManager sessions)
    {
        _maps = maps;
        _server = server;
        _sessions = sessions;
    }

    public MapOverlayStore Overlays => _maps.Overlays ??= new MapOverlayStore(null);
    public ModelStore Models => _server.Models;

    /// <summary>What one editor has in hand: the map, what is selected, the nudge step, the last menu.</summary>
    private sealed class Hand
    {
        public string MapId = "";
        public int? Selected;
        public float Step = DefaultStep;
        public string LastMenu = "root";
        /// <summary>Things held together, for a group (phase 2).</summary>
        public readonly List<int> Held = new();
        /// <summary>What choosing a prefab from Place does.</summary>
        public PlaceMode Mode = PlaceMode.Feet;
        /// <summary>The last prefab placed, for /edit again, and whether it went to the build cursor.</summary>
        public string? LastPlaced;
        public bool LastAtCursor;
    }

    private readonly Dictionary<string, Hand> _hands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string User, string Map), (List<EditOp> Undo, List<EditOp> Redo)> _stacks = new();
    /// <summary>Who last changed each thing, for the undo that is refused because of it.</summary>
    private readonly Dictionary<(string Map, int Id), string> _touchedBy = new();

    private Hand HandOf(UserSession s)
    {
        if (!_hands.TryGetValue(s.Username, out var h)) _hands[s.Username] = h = new Hand();
        if (!h.MapId.Equals(s.CurrentMapId, StringComparison.OrdinalIgnoreCase))
        {
            h.MapId = s.CurrentMapId;
            h.Selected = null;
            h.Held.Clear();
            h.LastMenu = "root";
        }
        return h;
    }

    private (List<EditOp> Undo, List<EditOp> Redo) Stacks(UserSession s)
    {
        var key = (s.Username.ToLowerInvariant(), s.CurrentMapId.ToLowerInvariant());
        if (!_stacks.TryGetValue(key, out var st)) _stacks[key] = st = (new List<EditOp>(), new List<EditOp>());
        return st;
    }

    /// <summary>Who may edit the map they are on: <see cref="Permissions.Edit"/>, the owner, or an editor the owner named.</summary>
    public static bool MayEdit(MapManager maps, UserSession s)
        => s.Can(Permissions.Edit) || maps.IsOwner(s.CurrentMapId, s.Username) || maps.IsEditor(s.CurrentMapId, s.Username);

    /// <summary>Whether /edit with these words asks for the menu (F12), which is refused in words of its own.</summary>
    public static bool AsksForMenu(string[] args) => args.Length == 0 || args[0].Equals("menu", StringComparison.OrdinalIgnoreCase);

    private static void Say(Action<IMessage> reply, string text) => reply(new TextEvent { Text = text });

    // ── The commands ────────────────────────────────────────────────────────────────────────────

    public const string Usage =
        "Usage: /edit on its own opens the menu. /edit select nearest|within METRES|NAME|#ID, /edit select add nearest|NAME|#ID, "
        + "/edit select group, /edit select clear, /edit held move|nudge|turn ..., /edit selected, /edit move EAST NORTH UP, "
        + "/edit nudge DIRECTION [METRES], /edit turn DEGREES, "
        + "/edit face DIRECTION, /edit bring, /edit duplicate, /edit row COUNT [SPACING], /edit delete, /edit set FIELD VALUE, "
        + "/edit up FIELD, /edit down FIELD, /edit settings, /edit place PREFAB [at cursor], /edit place group ID, /edit again, "
        + "/edit build floor|wall|roof|door|window|prefab [FIELD VALUE ...], "
        + "/edit find WORDS, /edit preview PREFAB, /edit prefabs [CATEGORY], /edit group NAME, /edit spawn here, /edit step METRES, "
        + "/edit info, /edit map settings, /edit map set weather|time|ground|beacon CATEGORY VALUE, "
        + "/edit model show|set|up|down|versions|where|use|pin|unpin|new|copy|replace|retire|restore|remove KIND ID ..., /edit undo, /edit redo.";

    /// <summary>What an /edit costs against MessageLimits.Edits: 0 to look, 1 to change, more to change many things.</summary>
    public static double EditCost(string[] args)
    {
        if (args.Length == 0) return 0;
        string Word(int i) => args.Length > i ? args[i].ToLowerInvariant() : "";
        switch (Word(0))
        {
            case "menu": case "info": case "selected": case "settings": case "fields": case "prefabs":
            case "find": case "search": case "select": case "hold": case "step":
                return 0;
            case "map":
                return Word(1) == "set" ? 1 : 0;
            case "model":
                return Word(1) switch
                {
                    "show" or "versions" or "where" or "" => 0,
                    "replace" => args.Any(a => a.Equals("everywhere", StringComparison.OrdinalIgnoreCase)) ? 10 : 5,
                    _ => 1,
                };
            case "held":
                return 5;
            case "row":
                return int.TryParse(Word(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                    ? 1 + Math.Clamp(n, 0, MaxRow) / 10.0 : 1;
            case "place":
                return Word(1) switch { "mode" => 0, "group" => 5, _ => 1 };
            case "build":
                return Word(1) == "form" ? 0 : 1;
            default:
                return 1;
        }
    }

    /// <summary>Everything /edit does. The caller has checked the player may edit here.</summary>
    public void Handle(UserSession s, string[] args, Action<IMessage> reply)
    {
        using var saving = Overlays.Defer();
        using var filing = _maps.DeferGrid();
        Run(s, args, reply);
    }

    private void Run(UserSession s, string[] args, Action<IMessage> reply)
    {
        var hand = HandOf(s);
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "menu";
        string[] rest = args.Length > 1 ? args[1..] : Array.Empty<string>();
        switch (verb)
        {
            case "menu":
                SendMenu(s, rest.Length > 0 ? string.Join(" ", rest) : "root", reply, refresh: false);
                return;
            case "info": Say(reply, MapInfo(s)); return;
            case "spawn":
                if (rest.Length == 0 || !rest[0].Equals("here", StringComparison.OrdinalIgnoreCase)) { Say(reply, "Say /edit spawn here."); return; }
                SpawnHere(s, reply);
                return;
            case "select":
                if (rest.Length == 1 && rest[0].Equals("group", StringComparison.OrdinalIgnoreCase))
                {
                    HoldPlacement(s, reply);
                    return;
                }
                if (rest.Length > 0 && rest[0].ToLowerInvariant() is "add" or "clear" or "hold")
                {
                    Hold(s, rest[0].Equals("clear", StringComparison.OrdinalIgnoreCase) ? new[] { "clear" } : rest[1..], reply);
                    return;
                }
                Select(s, rest, reply);
                return;
            case "hold": Hold(s, rest, reply); return;
            case "held": HeldCommand(s, rest, reply); return;
            case "row": Row(s, rest, reply); return;
            case "again": Again(s, reply); return;
            case "find":
            case "search":
                if (rest.Length == 0) { Say(reply, "Say /edit find WORDS: prefabs whose name has every word."); return; }
                if (s.IsTextClient)
                {
                    var found = Find(s, string.Join(" ", rest));
                    Say(reply, found.Count == 0 ? $"Nothing is called {string.Join(" ", rest)}."
                        : $"Found {found.Count}: " + string.Join("; ", found.Select(t => $"{t.Id}, {t.Name}")) + ". /edit place PREFAB puts one down.");
                }
                else SendMenu(s, "find:" + string.Join(" ", rest), reply, refresh: false);
                return;
            case "preview": Preview(s, rest, reply); return;
            case "group": MakeGroup(s, rest, reply); return;
            case "map": MapCommand(s, rest, reply); return;
            case "selected":
                if (TrySelected(s, reply, out var w, out var e, out int id)) Say(reply, Summary(s, w, e, id));
                return;
            case "move": Move(s, rest, reply); return;
            case "nudge": Nudge(s, rest, reply); return;
            case "turn": Turn(s, rest, reply); return;
            case "face": Face(s, rest, reply); return;
            case "bring": Bring(s, reply); return;
            case "duplicate":
            case "copy": Duplicate(s, reply); return;
            case "delete":
            case "remove": Delete(s, reply); return;
            case "set": SetField(s, rest, reply, 0); return;
            case "up": SetField(s, rest, reply, +1); return;
            case "down": SetField(s, rest, reply, -1); return;
            case "settings":
            case "fields": SaySettings(s, reply); return;
            case "place": PlaceCommand(s, rest, reply); return;
            case "build": BuildCommand(s, rest, reply); return;
            case "prefabs": SayPrefabs(s, rest, reply); return;
            case "step": SetStep(s, rest, reply); return;
            case "model": ModelCommand(s, rest, reply); return;
            case "undo": Undo(s, reply); return;
            case "redo": Redo(s, reply); return;
            default: Say(reply, Usage); return;
        }
    }

    // ── Where things are, in words ──────────────────────────────────────────────────────────────

    private bool TryBody(UserSession s, Action<IMessage> reply, out World world, out Vector3 feet, out float yaw)
    {
        world = null!; feet = default; yaw = 0f;
        if (!_maps.TryGetMap(s.CurrentMapId, out world, out _, out _, out _)) { Say(reply, $"Map '{s.CurrentMapId}' is not loaded."); return false; }
        if (s.Entity == Entity.Null || !world.IsAlive(s.Entity) || !world.Has<Transform>(s.Entity))
        { Say(reply, "You are not in the world yet."); return false; }
        var t = world.Get<Transform>(s.Entity);
        feet = t.Position;
        yaw = world.Has<PlayerComponent>(s.Entity) ? world.Get<PlayerComponent>(s.Entity).Yaw : YawOf(t.Rotation);
        return true;
    }

    /// <summary>The heading a rotation faces, radians, 0 north and increasing clockwise (east is +pi/2).</summary>
    public static float YawOf(Quaternion q)
    {
        var f = Vector3.Transform(Vector3.UnitZ, q);
        return MathF.Atan2(f.X, f.Z);
    }

    private static readonly string[] Compass8 = { "north", "north east", "east", "south east", "south", "south west", "west", "north west" };
    private static readonly Vector3[] Compass4 = { Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitZ, -Vector3.UnitX };
    private static readonly string[] Compass4Names = { "north", "east", "south", "west" };

    public static string CompassOf(float yaw)
    {
        float deg = yaw * 180f / MathF.PI;
        deg = ((deg % 360f) + 360f) % 360f;
        return Compass8[(int)MathF.Round(deg / 45f) % 8];
    }

    /// <summary>Which of north, east, south, west a heading is nearest: 0 to 3.</summary>
    private static int Quarter(float yaw)
    {
        float deg = yaw * 180f / MathF.PI;
        deg = ((deg % 360f) + 360f) % 360f;
        return (int)MathF.Round(deg / 90f) % 4;
    }

    public static string Metres(float m)
    {
        string n = FieldDescriptor.Format(MathF.Round(m, 2));
        return n == "1" ? "1 metre" : $"{n} metres";
    }

    private static string NameOf(World world, Entity e)
    {
        if (world.Has<IdentityComponent>(e) && world.Get<IdentityComponent>(e).Name is { Length: > 0 } n) return n;
        if (world.Has<NameComponent>(e) && world.Get<NameComponent>(e).Name is { Length: > 0 } m) return m;
        return PrefabOf(world, e) is { Length: > 0 } p ? p : "thing";
    }

    private static string PrefabOf(World world, Entity e)
        => world.Has<IdentityComponent>(e) ? world.Get<IdentityComponent>(e).PrefabId ?? "" : "";

    /// <summary>A thing's box in the world, axis aligned (its turned extent), or a point if it has no body.</summary>
    private static (Vector3 Lo, Vector3 Hi) Box(World world, Entity e)
    {
        var t = world.Get<Transform>(e);
        if (!world.Has<ColliderComponent>(e)) return (t.Position, t.Position);
        var half = CompositeAcoustics.AxisAlignedHalfExtents(world.Get<ColliderComponent>(e).Size * 0.5f, t.Rotation);
        return (t.Position - half, t.Position + half);
    }

    /// <summary>How far a thing is from your feet across the ground, which way, and whether you are on or in it.</summary>
    private static (float Distance, Vector3 Toward, bool Contains) Reach(World world, Entity e, Vector3 feet)
    {
        var (lo, hi) = Box(world, e);
        var closest = Vector3.Clamp(feet, lo, hi);
        var flat = new Vector3(closest.X - feet.X, 0f, closest.Z - feet.Z);
        // Over or under it, or inside it: the ground you stand on, the room you are in, the roof above.
        bool contains = feet.X >= lo.X && feet.X <= hi.X && feet.Z >= lo.Z && feet.Z <= hi.Z;
        var centre = world.Get<Transform>(e).Position;
        var toward = flat.LengthSquared() > 1e-6f ? flat : new Vector3(centre.X - feet.X, 0f, centre.Z - feet.Z);
        return (flat.Length(), toward, contains);
    }

    private static string Where(World world, Entity e, Vector3 feet, float yaw)
    {
        var (d, toward, contains) = Reach(world, e, feet);
        if (contains)
        {
            var (lo, hi) = Box(world, e);
            return hi.Y <= feet.Y + 0.3f ? "under you" : lo.Y >= feet.Y + PhysicsConstants.PlayerHeight ? "above you" : "around you";
        }
        string dir = toward.LengthSquared() > 1e-6f ? DirectionWords.Relative(yaw, toward) : "here";
        return d < 0.05f ? $"beside you, {dir}" : $"{Metres(d)} {dir}";
    }

    private string Summary(UserSession s, World world, Entity e, int id)
    {
        if (!TryBody(s, _ => { }, out _, out var feet, out float yaw)) return NameOf(world, e);
        var t = world.Get<Transform>(e);
        var z = world.Has<ColliderComponent>(e) ? world.Get<ColliderComponent>(e).Size : Vector3.Zero;
        string size = z != Vector3.Zero
            ? $", {FieldDescriptor.Format(z.X)} metres wide, {FieldDescriptor.Format(z.Z)} deep and {FieldDescriptor.Format(z.Y)} high"
            : "";
        string model = world.Has<SoundEmitterComponent>(e) && ModelKinds.TryModelOfSound(world.Get<SoundEmitterComponent>(e).SoundId, out var kind, out var mid)
            ? $", a {ModelKinds.Spoken(kind)}, model {mid}" : "";
        return $"{NameOf(world, e)}, {Where(world, e, feet, yaw)}, facing {CompassOf(YawOf(t.Rotation))}{size}{model}. "
             + $"At {PlayerCoordinates.Format(t.Position)}, number {id}.";
    }

    // ── Selecting ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether a thing is one the editor may take hold of: from the map or placed by the editor, and not moving or carried.</summary>
    private static bool Editable(World world, Entity e)
        => world.IsAlive(e) && world.Has<Transform>(e)
           && !world.Has<Velocity>(e) && !world.Has<PlayerComponent>(e) && !world.Has<HeldComponent>(e);

    private List<(int Id, Entity E, float Distance, bool Contains)> Candidates(World world, string mapId, Vector3 feet)
    {
        var list = new List<(int, Entity, float, bool)>();
        foreach (var (id, e) in _maps.AuthoredEntities(mapId))
        {
            if (!Editable(world, e)) continue;
            var (d, _, contains) = Reach(world, e, feet);
            list.Add((id, e, d, contains));
        }
        return list;
    }

    /// <summary>The nearest things that are not under or around you, nearest first.</summary>
    internal List<(int Id, Entity E, float Distance, bool Contains)> Nearest(UserSession s, int count)
    {
        if (!_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _) || !TryBody(s, _ => { }, out _, out var feet, out _))
            return new();
        return Candidates(world, s.CurrentMapId, feet).Where(c => !c.Contains)
               .OrderBy(c => c.Distance).ThenBy(c => c.Id).Take(count).ToList();
    }

    /// <summary>Everything within a distance, what you are on and in first.</summary>
    internal List<(int Id, Entity E, float Distance, bool Contains)> Within(UserSession s, float metres, int cap = 40)
    {
        if (!_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _) || !TryBody(s, _ => { }, out _, out var feet, out _))
            return new();
        return Candidates(world, s.CurrentMapId, feet).Where(c => c.Distance <= metres)
               .OrderBy(c => c.Contains ? 0 : 1).ThenBy(c => c.Distance).ThenBy(c => c.Id).Take(cap).ToList();
    }

    private void Select(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (!TryBody(s, reply, out var world, out var feet, out float yaw)) return;
        var hand = HandOf(s);
        string what = string.Join(" ", args).Trim();
        if (what.Length == 0) { Say(reply, "Say /edit select nearest, /edit select within METRES, /edit select NAME, or /edit select #NUMBER."); return; }

        if (what.Equals("nearest", StringComparison.OrdinalIgnoreCase) || what.StartsWith("nearest ", StringComparison.OrdinalIgnoreCase))
        {
            var near = Nearest(s, 1);
            if (near.Count == 0) { Say(reply, "There is nothing here the editor can take hold of."); return; }
            SelectId(s, near[0].Id, reply);
            return;
        }
        if (args[0].Equals("within", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2 || !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) || r <= 0 || r > 500)
            { Say(reply, "Say /edit select within METRES, up to 500."); return; }
            var list = Within(s, r);
            if (list.Count == 0) { Say(reply, $"Nothing within {Metres(r)}."); return; }
            if (s.IsTextClient)
                Say(reply, $"Within {Metres(r)}: " + string.Join("; ", list.Select(c => $"#{c.Id} {NameOf(world, c.E)}, {Where(world, c.E, feet, yaw)}")) + ". /edit select #NUMBER takes one.");
            else SendMenu(s, $"select.within:{FieldDescriptor.Format(r)}", reply, refresh: false);
            return;
        }
        if (what.StartsWith('#'))
        {
            if (!int.TryParse(what[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) { Say(reply, "A number is # and digits: /edit select #1002."); return; }
            SelectId(s, id, reply);
            return;
        }
        // By name: what you said, anywhere in the name or the prefab, nearest first.
        var named = Candidates(world, s.CurrentMapId, feet)
            .Where(c => NameOf(world, c.E).Contains(what, StringComparison.OrdinalIgnoreCase)
                        || PrefabOf(world, c.E).Contains(what.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Distance).ThenBy(c => c.Id).ToList();
        if (named.Count == 0) { Say(reply, $"Nothing here is called {what}."); return; }
        if (named.Count > 1 && s.IsTextClient)
            Say(reply, $"{named.Count} things are called {what}; the nearest is selected. Others: "
                     + string.Join("; ", named.Skip(1).Take(8).Select(c => $"#{c.Id} {NameOf(world, c.E)}, {Where(world, c.E, feet, yaw)}")) + ".");
        SelectId(s, named[0].Id, reply);
    }

    private void SelectId(UserSession s, int id, Action<IMessage> reply)
    {
        if (!_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _)) return;
        if (!_maps.AuthoredEntities(s.CurrentMapId).TryGetValue(id, out var e) || !Editable(world, e))
        { Say(reply, $"There is nothing numbered {id} here the editor can take hold of."); return; }
        var hand = HandOf(s);
        hand.Selected = id;
        Say(reply, "Selected " + Summary(s, world, e, id));
        if (!s.IsTextClient) SendMenu(s, "selected", reply, refresh: false);
    }

    /// <summary>The selected thing, alive; or the reason there is none, said.</summary>
    private bool TrySelected(UserSession s, Action<IMessage> reply, out World world, out Entity e, out int id)
    {
        e = Entity.Null; id = 0;
        if (!_maps.TryGetMap(s.CurrentMapId, out world, out _, out _, out _)) { Say(reply, $"Map '{s.CurrentMapId}' is not loaded."); return false; }
        var hand = HandOf(s);
        if (hand.Selected is not int selected) { Say(reply, "Nothing is selected. /edit select nearest, or Select in the menu."); return false; }
        id = selected;
        if (!_maps.AuthoredEntities(s.CurrentMapId).TryGetValue(selected, out e) || !Editable(world, e))
        {
            hand.Selected = null;
            Say(reply, _touchedBy.TryGetValue((s.CurrentMapId, selected), out var who) && !who.Equals(s.Username, StringComparison.OrdinalIgnoreCase)
                ? $"What you had selected has gone: {who} deleted it." : "What you had selected has gone.");
            return false;
        }
        return true;
    }

    // ── Map ─────────────────────────────────────────────────────────────────────────────────────

    internal string MapInfo(UserSession s)
    {
        if (!_maps.TryGetMapData(s.CurrentMapId, out var d)) return $"Map '{s.CurrentMapId}' is not loaded.";
        var size = d.MaxBound - d.MinBound;
        string owner = string.IsNullOrWhiteSpace(d.OwnerId) ? "the server's" : d.OwnerId.Equals(s.Username, StringComparison.OrdinalIgnoreCase) ? "yours" : $"{d.OwnerId}'s";
        int things = _maps.AuthoredEntities(s.CurrentMapId).Count;
        return $"{d.DisplayName}, {owner}, {(d.IsPublic ? "public" : "private")}. "
             + $"{FieldDescriptor.Format(MathF.Round(size.X))} by {FieldDescriptor.Format(MathF.Round(size.Z))} metres. "
             + $"{things} things. Spawn at {PlayerCoordinates.Format(d.SpawnPoint.Position)}, facing {CompassOf(YawOf(d.SpawnPoint.Rotation))}.";
    }

    private void SpawnHere(UserSession s, Action<IMessage> reply)
    {
        if (!TryBody(s, reply, out _, out var feet, out float yaw)) return;
        if (!_maps.TryGetMapData(s.CurrentMapId, out var d)) return;
        var o = Overlays.Get(s.CurrentMapId);
        var op = new SpawnOp(s.CurrentMapId, d.SpawnPoint.Position, d.SpawnPoint.Rotation, o.Spawn == null ? null : Copy(o.Spawn),
                             feet, Quaternion.CreateFromYawPitchRoll(Snap(yaw, 45f), 0f, 0f));
        ApplySpawn(op.MapId, op.AfterPosition, op.AfterRotation, new OverlaySpawn { Position = op.AfterPosition, Rotation = op.AfterRotation });
        Push(s, op);
        Say(reply, $"Spawn point set here, at {PlayerCoordinates.Format(feet)}, facing {CompassOf(YawOf(op.AfterRotation))}.");
        Notify(s, $"{s.Username} moved the spawn point.");
        Refresh(s, reply);
    }

    private static OverlaySpawn Copy(OverlaySpawn o) => new() { Position = o.Position, Rotation = o.Rotation };

    private void ApplySpawn(string mapId, Vector3 position, Quaternion rotation, OverlaySpawn? overlay)
    {
        if (!_maps.TryGetMapData(mapId, out var d)) return;
        d.SpawnPoint = new Transform { Position = position, Rotation = rotation };
        Overlays.Get(mapId).Spawn = overlay;
        Overlays.Save(mapId);
    }

    private static float Snap(float yaw, float degrees)
    {
        float step = degrees * MathF.PI / 180f;
        return MathF.Round(yaw / step) * step;
    }

    private void SetStep(UserSession s, string[] args, Action<IMessage> reply)
    {
        var hand = HandOf(s);
        if (args.Length == 0 || !float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float m) || m < 0.01f || m > 50f)
        { Say(reply, $"The step is {Metres(hand.Step)}. Say /edit step METRES, from 0.01 to 50."); return; }
        hand.Step = m;
        Say(reply, $"Step {Metres(m)}.");
        Refresh(s, reply);
    }

    // ── Other editors, and the menu after a change ──────────────────────────────────────────────

    /// <summary>Tells the other editors on the map, in a few words. Players who cannot edit are not told.</summary>
    private void Notify(UserSession actor, string text)
    {
        foreach (var other in _sessions.GetSessionsInMap(actor.CurrentMapId).ToList())
        {
            if (other == actor || other.Username.Equals(actor.Username, StringComparison.OrdinalIgnoreCase)) continue;
            if (!MayEdit(_maps, other)) continue;
            _server.SendToSession(other, new TextEvent { Text = text });
        }
    }

    /// <summary>The menu the editor last asked for, again, to replace the open one silently.</summary>
    private void Refresh(UserSession s, Action<IMessage> reply)
    {
        if (s.IsTextClient) return;
        SendMenu(s, HandOf(s).LastMenu, reply, refresh: true);
    }
}
