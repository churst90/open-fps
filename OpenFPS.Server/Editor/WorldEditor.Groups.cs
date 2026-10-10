using System.Numerics;
using System.Text.Json;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Editor;

/// <summary>One part of a group: a prefab, where it stands from the group's origin in the group's own
/// frame (right, forward, up, as the builder faced), and how it is turned.</summary>
public sealed record GroupPart
{
    [Tunable("", 0, 0, "The prefab this part is made from, by id.")]
    public string PrefabId { get; init; } = "";
    [Tunable("", 0, 0, "What this part is called when it is placed. Empty: the prefab's name.")]
    public string? Name { get; init; }
    [Tunable("m", -500, 500, "How far right of the group's origin this part stands.", Label = "right", Step = 0.05)]
    public float RightMetres { get; init; }
    [Tunable("m", -500, 500, "How far forward of the group's origin this part stands.", Label = "forward", Step = 0.05)]
    public float ForwardMetres { get; init; }
    [Tunable("m", -100, 500, "How far above the group's origin (its lowest point) the middle of this part is.", Label = "up", Step = 0.05)]
    public float UpMetres { get; init; }
    [Tunable("degrees", -360, 360, "How far this part is turned clockwise from the way the group faces.", Label = "turn", Step = 15)]
    public float TurnDegrees { get; init; }
    /// <summary>Its scale, as the thing had it. Kept, not shown.</summary>
    public Vector3 Scale { get; init; } = Vector3.One;
    /// <summary>Its own settings, as the thing had them (EntitySettings paths). Kept, not shown.</summary>
    public Dictionary<string, string>? Settings { get; init; }
}

/// <summary>
/// A group (docs/WORLD_EDITOR.md section 11.5): things held together and kept as one model, placed again
/// as those things. A placed group is its things, each one editable on its own; a new version of the
/// group is what the next placing uses.
/// </summary>
public sealed record GroupSpec
{
    [Tunable("", 0, 0, "What the group is called.")]
    public string Name { get; init; } = "";
    public GroupPart[] Parts { get; init; } = Array.Empty<GroupPart>();
    [Tunable("", 0, 1, "On: listed in Place under Buildings. Off: under Groups.", Label = "a building")]
    public bool Building { get; init; }
}

/// <summary>Groups as a kind: made in the editor only, kept in the model store, held by the server alone.</summary>
public sealed class GroupKind : EditorKind
{
    public const string KindId = "group";
    private readonly ModelStore _store;
    private readonly PrefabRepository _prefabs;

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new Vector3Converter() },
    };

    public GroupKind(ModelStore store, PrefabRepository prefabs) { _store = store; _prefabs = prefabs; }

    public override string Kind => KindId;
    public override string Spoken => "group";
    public override bool ToClients => false;
    public override IReadOnlyList<FieldNode> Fields => ModelKinds.Describe(typeof(GroupSpec));
    public override IEnumerable<string> Ids => _store.Histories.Where(h => h.Kind == KindId).Select(h => h.Id);
    public override bool Knows(string id) => _store.History(KindId, id) != null;
    public override string CurrentJson(string id)
        => _store.SpecJson(KindId, id, _store.CurrentVersion(KindId, id)) ?? throw new ArgumentException($"No group '{id}'.");
    public override string? BuiltInJson(string id) => null;

    public override string Check(string id, string json)
    {
        var spec = JsonSerializer.Deserialize<GroupSpec>(json, Json) ?? throw new ArgumentException("The group is empty.");
        if (spec.Parts.Length == 0) throw new ArgumentException("A group has at least one part.");
        if (spec.Parts.Length > WorldEditor.MaxHeld) throw new ArgumentException($"A group has at most {WorldEditor.MaxHeld} parts.");
        foreach (var p in spec.Parts)
            if (!_prefabs.Prefabs.ContainsKey(p.PrefabId.ToLowerInvariant())) throw new ArgumentException($"There is no prefab called {p.PrefabId}.");
        return JsonSerializer.Serialize(spec, Json);
    }

    public override void Apply(string id, string json) { }

    public static GroupSpec Read(string json) => JsonSerializer.Deserialize<GroupSpec>(json, Json)!;
}

public sealed partial class WorldEditor
{
    internal IEnumerable<string> GroupIds() => Catalog.Get(GroupKind.KindId)!.Ids.Where(i => !Models.IsRetired(GroupKind.KindId, i));

    internal GroupSpec? GroupOf(string id)
    {
        var kind = Catalog.Get(GroupKind.KindId)!;
        return kind.Canonical(id) is { } c ? GroupKind.Read(kind.CurrentJson(c)) : null;
    }

    /// <summary>A heading as clockwise degrees from north.</summary>
    private static float Degrees(float yaw) => yaw * 180f / MathF.PI;

    /// <summary>
    /// /edit group NAME, /edit building NAME: the held things (or the selected one) kept as a group, a model
    /// of their own, with their places from an origin at the middle of them on the ground, in the frame of
    /// the way you face. The things stay where they are. A building is a group listed under Buildings.
    /// </summary>
    private void MakeGroup(UserSession s, string[] args, Action<IMessage> reply, bool building = false)
    {
        string what = building ? "building" : "group";
        if (args.Length == 0) { Say(reply, $"Say /edit {what} NAME: the things you hold become a {what} of that name."); return; }
        string id = string.Join("_", args).ToLowerInvariant();
        if (!ModelStore.IsSafeId(id)) { Say(reply, $"A {what}'s name is letters, digits, _ and -, up to 64."); return; }
        if (!s.Can(Permissions.EditModels)) { Say(reply, $"A {what} is a model every map can place, so making one needs edit-models."); return; }
        var kind = Catalog.Get(GroupKind.KindId)!;
        if (kind.Knows(id)) { Say(reply, $"There is already a group or building called {id}."); return; }
        if (!TryBody(s, reply, out var world, out _, out float yaw)) return;
        var hand = HandOf(s);
        var ids = hand.Held.Count > 0 ? hand.Held.ToList() : hand.Selected is int one ? new List<int> { one } : new List<int>();
        // A group is made of prefabs: a parked vehicle in the hand is left out of it.
        var things = ids.Select(i => (Id: i, Found: _maps.AuthoredEntities(s.CurrentMapId).TryGetValue(i, out var e) && Editable(world, e)
                                                     && _maps.Prefabs.ContainsKey(PrefabOf(world, e).ToLowerInvariant()), E: e))
                        .Where(x => x.Found).Select(x => (x.Id, x.E)).ToList();
        if (things.Count == 0) { Say(reply, "Hold some things first: /edit select add nearest, or by name or number."); return; }

        // The origin: the middle of them across the ground, at their lowest point.
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var (_, e) in things) { var (a, b) = Box(world, e); lo = Vector3.Min(lo, a); hi = Vector3.Max(hi, b); }
        var origin = new Vector3((lo.X + hi.X) * 0.5f, lo.Y, (lo.Z + hi.Z) * 0.5f);
        int q = Quarter(yaw);
        var forward = Compass4[q];
        var right = Compass4[(q + 1) % 4];
        float frame = q * 90f;
        var data = DataOf(s.CurrentMapId);
        var o = Overlays.Get(s.CurrentMapId);
        var parts = things.Select(t =>
        {
            var tr = RestOf(world, t.E);
            var off = tr.Position - origin;
            float turn = Degrees(YawOf(tr.Rotation)) - frame;
            turn = ((turn % 360f) + 540f) % 360f - 180f;
            var settings = o.AdditionFor(t.Id)?.Settings ?? o.ChangeFor(t.Id)?.Settings;
            return new GroupPart
            {
                PrefabId = PrefabOf(world, t.E),
                Name = data.TryGetValue(t.Id, out var d) ? d.Name : null,
                RightMetres = MathF.Round(Vector3.Dot(off, right), 3),
                ForwardMetres = MathF.Round(Vector3.Dot(off, forward), 3),
                UpMetres = MathF.Round(off.Y, 3),
                TurnDegrees = MathF.Round(turn, 2),
                Scale = tr.Scale,
                Settings = settings == null ? null : new Dictionary<string, string>(settings),
            };
        }).ToArray();
        var spec = new GroupSpec { Name = Capital(id.Replace('_', ' ').Replace('-', ' ')), Parts = parts, Building = building };
        ModelUpdate update;
        try { update = Models.Commit(GroupKind.KindId, id, JsonSerializer.Serialize(spec, GroupKind.Json), s.Username, $"made from {Plural(parts.Length, "thing")} on {_maps.DisplayName(s.CurrentMapId)}"); }
        catch (Exception ex) { Say(reply, $"Not made: {Reason(ex)}"); return; }
        Push(s, new CreateOp(s.CurrentMapId, GroupKind.KindId, id));
        string listed = building ? BuildingsCategory : GroupsCategory;
        Say(reply, $"Made the {what} {id} from {Plural(parts.Length, "thing")}. The things stay where they are. Place it from Place, {listed}, or /edit place group {id}.");
        Notify(s, $"{s.Username} made the {what} {id}.");
        Refresh(s, reply);
    }

    /// <summary>/edit place group ID: a group's things, placed in front of you, turned the way you face. One undo takes them away.</summary>
    private void PlaceGroup(UserSession s, string word, Action<IMessage> reply)
    {
        var kind = Catalog.Get(GroupKind.KindId)!;
        if (kind.Canonical(word) is not { } id || GroupOf(id) is not { } spec) { Say(reply, $"There is no group called {word}."); return; }
        if (Models.IsRetired(GroupKind.KindId, id)) { Say(reply, $"The group {id} is retired, so it is not offered for new things."); return; }
        if (Full(s, spec.Parts.Length, out string full)) { Say(reply, full); return; }
        foreach (var part in spec.Parts)
            if (_maps.Prefabs.TryGetValue(part.PrefabId.ToLowerInvariant(), out var pt) && !MayPlace(s, pt, out full)) { Say(reply, full); return; }
        if (!TryBody(s, reply, out _, out var feet, out float yaw)) return;
        int q = Quarter(yaw);
        var forward = Compass4[q];
        var right = Compass4[(q + 1) % 4];
        // Its nearest part a metre in front of you.
        float back = spec.Parts.Max(p => -p.ForwardMetres + HalfDepth(p));
        var origin = feet + forward * (PhysicsConstants.PlayerRadius + 1f + MathF.Max(0f, back));
        var ops = new List<EditOp>();
        var placedIds = new List<int>();
        string placement = $"{id}@{Overlays.Get(s.CurrentMapId).NextId}";
        foreach (var p in spec.Parts)
        {
            if (!_maps.Prefabs.TryGetValue(p.PrefabId.ToLowerInvariant(), out var t)) continue;
            var at = origin + right * p.RightMetres + forward * p.ForwardMetres + Vector3.UnitY * p.UpMetres;
            var rotation = Quaternion.CreateFromYawPitchRoll((q * 90f + p.TurnDegrees) * MathF.PI / 180f, 0f, 0f);
            if (!PlaceOne(s.CurrentMapId, t, new Pose(at, rotation, p.Scale), p.Name, p.Settings, out var thing, out string why, placement))
            {
                // All or nothing: a group half placed is not the group.
                foreach (var done in ops.AsEnumerable().Reverse()) Reverse(s, done, forward: false, out _);
                Say(reply, $"Not placed: {why}");
                return;
            }
            ops.Add(new PlaceOp(s.CurrentMapId, thing, "placed", t.Name));
            placedIds.Add(thing.Id);
        }
        if (ops.Count == 0) { Say(reply, $"None of the group {id}'s prefabs are here."); return; }
        Push(s, new BatchOp(s.CurrentMapId, ops, $"placed the group {id}"));
        var hand = HandOf(s);
        hand.Held.Clear();
        hand.Held.AddRange(placedIds);
        hand.Selected = placedIds[0];
        Say(reply, $"Placed the group {id}, {Plural(ops.Count, "thing")}, in front of you, facing {Compass4Names[q]}. They are held: /edit held move, nudge or turn moves them as one, and each is its own thing too.");
        Notify(s, $"{s.Username} placed the group {id}.");
        Refresh(s, reply);
    }

    private float HalfDepth(GroupPart p)
        => _maps.Prefabs.TryGetValue(p.PrefabId.ToLowerInvariant(), out var t) && t.ColliderSize is { } z ? MathF.Max(z.X, z.Z) * 0.5f : 0.5f;

    // ── A placed group, held and moved as one ───────────────────────────────────────────────────

    /// <summary>The group placing a thing was put down by, or null.</summary>
    private string? PlacementOf(string mapId, int id) => Overlays.Get(mapId).AdditionFor(id)?.Placement;

    /// <summary>The things of one group placing still on the map.</summary>
    private List<int> PartsOf(string mapId, string placement)
        => Overlays.Get(mapId).Added.Where(a => a.Placement == placement).Select(a => a.Entity.EntityId)
               .Where(i => _maps.AuthoredEntities(mapId).ContainsKey(i)).ToList();

    /// <summary>/edit select group: every thing put down by the same group placing as the selected one, held.</summary>
    private void HoldPlacement(UserSession s, Action<IMessage> reply)
    {
        if (!TrySelected(s, reply, out var world, out var e, out int id)) return;
        if (PlacementOf(s.CurrentMapId, id) is not { } placement)
        { Say(reply, $"{NameOf(world, e)} was not placed as part of a group. /edit select add holds things one at a time."); return; }
        var hand = HandOf(s);
        hand.Held.Clear();
        hand.Held.AddRange(PartsOf(s.CurrentMapId, placement));
        Say(reply, $"Holding the {Plural(hand.Held.Count, "thing")} of the group {GroupWord(placement)} placed with {NameOf(world, e)}. "
                 + "/edit held move, nudge or turn moves them as one.");
        Refresh(s, reply);
    }

    /// <summary>"yard" from "yard@900000004".</summary>
    private static string GroupWord(string placement) => placement[..Math.Max(0, placement.LastIndexOf('@'))];

    /// <summary>/edit held move|nudge|turn ...: the held things moved or turned together, one undo for all.</summary>
    private void HeldCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        string[] rest = args.Length > 1 ? args[1..] : Array.Empty<string>();
        switch (verb)
        {
            case "move":
            {
                if (rest.Length < 3 || !TryNumber(rest[0], out float east) || !TryNumber(rest[1], out float north) || !TryNumber(rest[2], out float up))
                { Say(reply, "Say /edit held move EAST NORTH UP, in metres. Negative goes west, south, down."); return; }
                if (MathF.Abs(east) > 1000 || MathF.Abs(north) > 1000 || MathF.Abs(up) > 1000) { Say(reply, "A move is at most 1000 metres each way."); return; }
                var by = PlayerCoordinates.ToWorld(east, north, up);
                MoveHeld(s, reply, "moved", (_, p) => p with { Position = p.Position + by }, what => $"Moved {what} {Offset(by)}.");
                return;
            }
            case "nudge":
            {
                if (!TryBody(s, reply, out _, out _, out float yaw)) return;
                if (rest.Length == 0 || !TryDirection(rest[0], yaw, out var dir))
                { Say(reply, "Say /edit held nudge north, south, east, west, up, down, forward, back, left or right, and metres if not the step."); return; }
                float metres = HandOf(s).Step;
                if (rest.Length > 1 && (!TryNumber(rest[1], out metres) || metres <= 0 || metres > 50)) { Say(reply, "A nudge is up to 50 metres."); return; }
                var by = dir * metres;
                MoveHeld(s, reply, "moved", (_, p) => p with { Position = p.Position + by }, what => $"Moved {what} {Offset(by)}.");
                return;
            }
            case "turn":
            {
                if (rest.Length == 0 || !TryNumber(rest[0], out float degrees) || MathF.Abs(degrees) > 360)
                { Say(reply, "Say /edit held turn DEGREES: positive is clockwise, negative anticlockwise, about the middle of them."); return; }
                var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180f);
                MoveHeld(s, reply, "turned", (centre, p) => p with
                {
                    Position = centre + Vector3.Transform(p.Position - centre, turn),
                    Rotation = Quaternion.Normalize(Quaternion.Concatenate(p.Rotation, turn)),
                }, what => $"Turned {what} {FieldDescriptor.Format(MathF.Abs(degrees))} degrees {(degrees >= 0 ? "clockwise" : "anticlockwise")} about their middle.");
                return;
            }
            default:
                Say(reply, "Say /edit held move EAST NORTH UP, /edit held nudge DIRECTION [METRES], or /edit held turn DEGREES: the held things together.");
                return;
        }
    }

    /// <summary>
    /// Moves every held thing to a pose worked out from its own and from the middle of them all (across
    /// the ground, at their lowest point), all or none: if one would go out of reach or through somebody,
    /// nothing moves.
    /// </summary>
    private void MoveHeld(UserSession s, Action<IMessage> reply, string verb, Func<Vector3, Pose, Pose> to, Func<string, string> said)
    {
        if (!_maps.TryGetMap(s.CurrentMapId, out var world, out _, out _, out _)) { Say(reply, $"Map '{s.CurrentMapId}' is not loaded."); return; }
        var hand = HandOf(s);
        var things = hand.Held.Select(i => (Id: i, Found: _maps.AuthoredEntities(s.CurrentMapId).TryGetValue(i, out var e) && Editable(world, e), E: e))
                              .Where(x => x.Found).Select(x => (x.Id, x.E)).ToList();
        if (things.Count == 0) { Say(reply, "Nothing is held. /edit select group holds a placed group; /edit select add holds one thing more."); return; }

        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var (_, e) in things) { var (a, b) = Box(world, e); lo = Vector3.Min(lo, a); hi = Vector3.Max(hi, b); }
        var centre = new Vector3((lo.X + hi.X) * 0.5f, lo.Y, (lo.Z + hi.Z) * 0.5f);

        string what = HeldWords(s.CurrentMapId, things.Select(t => t.Id).ToList());
        var moves = new List<(int Id, Entity E, string Name, Pose Before, Pose After)>();
        foreach (var (id, e) in things)
        {
            var before = PoseOf(world, e);
            var after = to(centre, before);
            string name = NameOf(world, e);
            if (!InReach(after.Position)) { Say(reply, $"Not {verb}: for {name}, {TooFar}"); return; }
            if (BlockedBy(world, e, after) is { } who) { Say(reply, $"Not {verb}: that would put {name} through {who}."); return; }
            moves.Add((id, e, name, before, after));
        }
        var ops = new List<EditOp>();
        foreach (var m in moves)
        {
            ApplyPose(s.CurrentMapId, world, m.E, m.After);
            Record(s.CurrentMapId, m.Id, world, m.E);
            ops.Add(new PoseOp(s.CurrentMapId, m.Id, m.Name, verb, m.Before, m.After));
        }
        Push(s, new BatchOp(s.CurrentMapId, ops, $"{verb} {what}"));
        Say(reply, said(what) + " One undo puts them back.");
        Notify(s, $"{s.Username} {verb} {what}.");
        Refresh(s, reply);
    }

    /// <summary>"the group yard" when the things are the whole of one group placing, "the 3 held things" otherwise.</summary>
    private string HeldWords(string mapId, List<int> ids)
    {
        var placements = ids.Select(i => PlacementOf(mapId, i)).Distinct().ToList();
        if (placements is [{ } one] && PartsOf(mapId, one).Count == ids.Count) return $"the group {GroupWord(one)}";
        return ids.Count == 1 ? "the held thing" : $"the {ids.Count} held things";
    }
}
