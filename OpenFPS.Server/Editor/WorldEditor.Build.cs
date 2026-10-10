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
/// The quick build (docs/WORLD_EDITOR.md section 15): a floor, wall, roof, door, window or prefab at a
/// size you give, in one command. Pieces are the basic prefabs scaled, as the city's are; a door or a
/// window can be fitted into the wall in front of you, which is cut into the pieces round the opening.
/// </summary>
public sealed partial class WorldEditor
{
    /// <summary>How far in front of you a wall may be to fit a door or window into it, metres.</summary>
    public const float FitReach = 3f;
    /// <summary>How far a door leaf overlaps each jamb, metres: tools/gen_city.py's DOOR_LAP. A leaf only as
    /// wide as its opening leaves a slot you can walk round shut.</summary>
    public const float DoorLap = 0.05f;
    /// <summary>The thickest thing a door is cut into: thicker is a building or a block, not a wall.</summary>
    public const float MaxWallThickness = 1f;
    /// <summary>A piece of a cut wall thinner than this is left out.</summary>
    private const float MinPiece = 0.01f;

    /// <summary>The words /edit build takes for a kind of piece, in the order the dialog shows them.</summary>
    public static readonly string[] BuildKinds = { "floor", "wall", "roof", "door", "window", "shape", "prefab" };

    private static readonly string[] WhereWords = { "ahead", "here", "cursor" };
    private static readonly string[] WhereLabels = { "In front of you", "At your feet", "At the build cursor" };
    private static readonly string[] FacingWords = { "me", "north", "east", "south", "west" };
    private static readonly string[] FacingLabels = { "The way you face", "North", "East", "South", "West" };

    public const string BuildUsage =
        "Say /edit build floor|wall|roof|door|window|prefab, then what you want of: width, length, height, thickness, above "
        + "(metres), material NAME, type KIND (a door's), prefab ID, where ahead|here|cursor (or ahead METRES, here, cursor), "
        + "distance METRES, facing me|north|east|south|west, fit yes|no (a door or window into the wall in front of you; or fit, free). "
        + "Example: /edit build wall length 6 height 2.7 material brick ahead 2. "
        + "Shapes: /edit build stairs 14 steps up north, column 0.3 by 3, ramp 1.5 by 6 by 0.5, cone, ball, dome, arch, "
        + "roof gable over the floor.";

    /// <summary>One field of a kind of piece: what it is, and what it holds when nobody has said.</summary>
    private sealed record BuildField(FieldDescriptor Field, string Default);

    private static FieldDescriptor MetresField(string word, string label, double min, double max, string help)
        => new() { Path = word, Label = label, Unit = "m", Min = min, Max = max, Help = help };

    private static FieldDescriptor ChoiceField(string word, string label, IReadOnlyList<string> choices, string help)
        => new() { Path = word, Label = label, Type = FieldType.Choice, Choices = choices, Help = help };

    /// <summary>The fields of a kind, in order, with their choices as this player may have them.</summary>
    private List<BuildField> BuildFields(UserSession s, string kind)
    {
        var fields = new List<BuildField>();
        void Add(FieldDescriptor f, string d) => fields.Add(new BuildField(f, d));
        const string across = "Left to right as you face it.";
        const string ahead = "Away from you, the way you face.";
        string Own(string material, bool upright)
            => BuildMaterials(s, kind).FirstOrDefault(m => m.Material == material).Prefab is { ColliderSize: { } size }
                ? FieldDescriptor.Format(upright ? size.Z : size.Y) : "0.2";

        switch (kind)
        {
            case "floor":
            case "roof":
            {
                var materials = BuildMaterials(s, kind);
                string material = Preferred(materials, kind == "floor" ? "Wood" : "Asphalt");
                if (kind == "roof") Add(MetresField("above", "height above the floor", 0, 100, "How high its underside is above the floor you stand on."), "2.7");
                Add(MetresField("width", "width", 0.1, 200, across), "4");
                Add(MetresField("length", "length", 0.1, 200, ahead), "4");
                Add(MetresField("thickness", "thickness", 0.0005, 5, "Choosing a material puts its own thickness here."), Own(material, false));
                Add(ChoiceField("material", "material", materials.Select(m => m.Material).ToList(), "What it is made of: how it sounds underfoot and in the room."), material);
                break;
            }
            case "wall":
            {
                var materials = BuildMaterials(s, kind);
                string material = Preferred(materials, "Brick");
                Add(MetresField("length", "length", 0.1, 200, across), "4");
                Add(MetresField("height", "height", 0.1, 50, "From the floor you stand on up."), "2.7");
                Add(MetresField("thickness", "thickness", 0.005, 5, "Choosing a material puts its own thickness here."), Own(material, true));
                Add(ChoiceField("material", "material", materials.Select(m => m.Material).ToList(), "What it is made of: what it lets through and how it rings."), material);
                break;
            }
            case "door":
            {
                var types = DoorTypes(s);
                Add(MetresField("width", "width", 0.4, 4, "The doorway's width. The leaf laps each side by 5 centimetres."), "0.9");
                Add(MetresField("height", "height", 1.5, 4, "From the floor up."), "2.1");
                Add(ChoiceField("type", "door type", types.Select(t => t.Word).ToList(), "How it opens: a knob, a push bar, glass, or sliding."),
                    types.Count > 0 ? types[0].Word : "");
                break;
            }
            case "window":
                Add(MetresField("width", "width", 0.2, 10, "The glass, left to right."), "1.2");
                Add(MetresField("height", "height", 0.2, 10, "The glass, bottom to top."), "1.2");
                Add(MetresField("above", "height above the floor", 0, 20, "Where the bottom of the glass is: the sill."), "0.9");
                break;
            case "shape":
                AddShapeFields(s, Add);
                break;
            case "prefab":
            {
                var placeable = Placeable(s).ToList();
                var cats = Categories.Where(c => placeable.Any(t => CategoryOf(t) == c)).ToList();
                var first = placeable.Where(t => CategoryOf(t) == cats.FirstOrDefault()).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                Add(ChoiceField("category", "category", cats.Select(CategoryWord).ToList(), "Narrows the list of prefabs."), cats.Count > 0 ? CategoryWord(cats[0]) : "");
                Add(ChoiceField("prefab", "prefab", placeable.Select(t => t.Id).ToList(), "What to put down."), first?.Id ?? "");
                var size = first?.ColliderSize ?? Vector3.One;
                Add(MetresField("width", "width", 0.05, 500, across + " Only for a prefab that can be sized."), FieldDescriptor.Format(size.X));
                Add(MetresField("height", "height", 0.05, 500, "Bottom to top. Only for a prefab that can be sized."), FieldDescriptor.Format(size.Y));
                Add(MetresField("depth", "depth", 0.05, 500, ahead + " Only for a prefab that can be sized."), FieldDescriptor.Format(size.Z));
                break;
            }
        }
        Add(ChoiceField("where", "where", WhereWords, "In front of you, at your feet, or at the build cursor (/origin and /at)."), kind is "floor" or "roof" ? "here" : "ahead");
        Add(MetresField("distance", "distance in front of you", 0, 50, "From you to its near edge."), "1");
        Add(ChoiceField("facing", "facing", FacingWords, "Which way its front faces: the way you face, or a compass direction."), "me");
        if (kind is "door" or "window")
            fields.Add(new BuildField(new FieldDescriptor
            {
                Path = "fit", Label = "fit into the wall in front of you", Type = FieldType.Bool,
                Help = "Cuts an opening where you face and puts it in, facing as the wall does.",
            }, "true"));
        return fields;
    }

    /// <summary>A category as one word, to send: "floors-roads-and-roofs".</summary>
    private static string CategoryWord(string category) => category.ToLowerInvariant().Replace(",", "").Replace(' ', '-');

    private static string Preferred(List<(string Material, PrefabTemplate Prefab)> materials, string wanted)
        => materials.Any(m => m.Material == wanted) ? wanted : materials.Count > 0 ? materials[0].Material : "";

    /// <summary>
    /// The materials a floor, wall or roof can be: those a solid plain box of that kind is made of, and that
    /// the acoustic registry knows (an unknown name would silently become Generic). One prefab per material,
    /// the one named for it if there is one.
    /// </summary>
    private List<(string Material, PrefabTemplate Prefab)> BuildMaterials(UserSession s, string kind)
    {
        string part = kind == "floor" ? "floor" : kind == "roof" ? "roof" : "wall";
        return Placeable(s)
            .Where(t => Plain(t) && t.Id.Contains(part, StringComparison.OrdinalIgnoreCase) && !t.Id.StartsWith("shore_", StringComparison.OrdinalIgnoreCase)
                        && t.Material != "None" && AcousticRegistry.IsKnown(t.Material))
            .GroupBy(t => t.Material)
            .Select(g => (g.Key, g.OrderBy(t => t.Id.Equals($"{g.Key}_{part}", StringComparison.OrdinalIgnoreCase) ? 0 : t.Id.Contains("generic") ? 2 : 1)
                              .ThenBy(t => t.Id, StringComparer.Ordinal).First()))
            .OrderBy(m => m.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A solid box that stands still and makes no sound of its own: a thing that can be sized.</summary>
    private static bool Plain(PrefabTemplate t)
        => t.ColliderSize.HasValue && (t.IsSolid ?? true) && (t.Shape ?? ColliderShape.Box) == ColliderShape.Box
           && t.IsDoor != true && !t.IsItem && !t.HasEmitter && t.Type == EntityType.StaticObject
           && !t.RoomSize.HasValue && !t.RegionAId.HasValue && !t.RegionBId.HasValue && t.Form == null;

    /// <summary>The doors opened by hand, one prefab per kind of door (DoorEvents.Slug).</summary>
    private List<(string Word, string Label, PrefabTemplate Prefab)> DoorTypes(UserSession s)
        => Placeable(s)
            .Where(t => t.IsDoor == true && !(t.Powered ?? false) && t.ColliderSize.HasValue
                        && DoorEvents.TryParseKind(t.DoorKind, out var k) && k is not (DoorKind.AutoSliding or DoorKind.Elevator))
            .GroupBy(t => { DoorEvents.TryParseKind(t.DoorKind, out var k); return DoorEvents.Slug(k); })
            .Select(g =>
            {
                var t = g.OrderBy(p => p.Id, StringComparer.Ordinal).First();
                return (g.Key, $"{t.Name}, {g.Key.Replace('-', ' ')}", t);
            })
            .OrderBy(d => d.Item1 == "knob" ? 0 : 1).ThenBy(d => d.Item1, StringComparer.Ordinal)
            .ToList();

    /// <summary>What a window is made of: the first plain glass wall.</summary>
    private PrefabTemplate? Glazing(UserSession s)
        => BuildMaterials(s, "wall").Where(m => m.Material == "Glass").Select(m => m.Prefab).FirstOrDefault();

    // ── The form the dialog is built from ───────────────────────────────────────────────────────

    /// <summary>
    /// The build dialog's description (Control+B), as an editor menu with Path "build.form": the kinds as
    /// Action items (Command the word); each kind's fields as Input items (Command the kind, Label the word,
    /// the rest as a typed value's); each choice as an Info item (Command "KIND.FIELD", Label said, Value
    /// sent, Help a prefab's category, Prompt the "field=value" pairs it sets, Count 1 for a prefab that can
    /// be sized). No new message: the client builds the dialog from this.
    /// </summary>
    internal EditorMenu BuildForm(UserSession s)
    {
        var items = new List<EditorMenuItem>();
        foreach (string kind in BuildKinds)
            items.Add(new EditorMenuItem { Label = Capital(kind), Kind = EditorItemKind.Action, Command = kind });
        foreach (string kind in BuildKinds)
        {
            foreach (var (f, d) in BuildFields(s, kind))
            {
                items.Add(new EditorMenuItem
                {
                    Label = f.Path, Kind = EditorItemKind.Input, Command = kind, Prompt = f.Label, Value = d,
                    ValueType = f.Type, Unit = f.Unit, Min = f.Min, Max = f.Max, Help = f.Help,
                });
                foreach (var option in Options(s, kind, f.Path)) items.Add(option);
            }
        }
        return new EditorMenu { Path = "build.form", Title = "Build", Items = items.ToArray() };
    }

    private IEnumerable<EditorMenuItem> Options(UserSession s, string kind, string field)
    {
        EditorMenuItem Option(string label, string value, string sets = "", string help = "", bool sized = false)
            => new() { Label = label, Kind = EditorItemKind.Info, Command = $"{kind}.{field}", Value = value, Prompt = sets, Help = help, Count = (byte)(sized ? 1 : 0) };
        switch (field)
        {
            case "material":
                foreach (var (material, t) in BuildMaterials(s, kind))
                {
                    var size = t.ColliderSize!.Value;
                    yield return Option($"{material}, {t.Name}", material, kind == "shape" ? "" : "thickness=" + FieldDescriptor.Format(kind == "wall" ? size.Z : size.Y));
                }
                break;
            case "type":
                foreach (var (word, label, _) in DoorTypes(s)) yield return Option(label, word);
                break;
            case "category":
                var placeable = Placeable(s).ToList();
                foreach (var c in Categories.Where(c => placeable.Any(t => CategoryOf(t) == c))) yield return Option(c, CategoryWord(c));
                break;
            case "prefab":
                foreach (var t in Placeable(s).OrderBy(t => Array.IndexOf(Categories, CategoryOf(t))).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var size = t.ColliderSize ?? Vector3.Zero;
                    string sets = t.ColliderSize.HasValue
                        ? $"width={FieldDescriptor.Format(size.X)} height={FieldDescriptor.Format(size.Y)} depth={FieldDescriptor.Format(size.Z)}" : "";
                    yield return Option(t.Name, t.Id, sets, CategoryWord(CategoryOf(t)), sized: Plain(t));
                }
                break;
            case "where":
                for (int i = 0; i < WhereWords.Length; i++) yield return Option(WhereLabels[i], WhereWords[i]);
                break;
            case "shape" or "style" or "over":
                foreach (var o in ShapeOptions(field)) yield return Option(o.Label, o.Value, o.Sets, o.Uses);
                break;
            case "facing":
                for (int i = 0; i < FacingWords.Length; i++) yield return Option(FacingLabels[i], FacingWords[i]);
                break;
        }
    }

    // ── /edit build ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether /edit with these words is the build dialog asking for its form, which a player who
    /// may not edit is not answered at all: Control+B does nothing for them.</summary>
    public static bool AsksForBuildForm(string[] args)
        => args.Length >= 2 && args[0].Equals("build", StringComparison.OrdinalIgnoreCase) && args[1].Equals("form", StringComparison.OrdinalIgnoreCase);

    private void BuildCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (args.Length > 0 && args[0].Equals("form", StringComparison.OrdinalIgnoreCase))
        {
            if (s.IsTextClient) Say(reply, BuildUsage);
            else reply(BuildForm(s));
            return;
        }
        // From the dialog the answer comes back as a menu it can tell from chat, so a refusal keeps it open.
        bool dialog = args.Any(a => a.Equals("dialog", StringComparison.OrdinalIgnoreCase));
        void Answer(bool placed, string text)
        {
            if (dialog && !s.IsTextClient) reply(new EditorMenu { Path = placed ? "build.placed" : "build.refused", Title = text });
            else Say(reply, text);
        }
        var words = args.Where(a => !a.Equals("dialog", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (words.Length == 0) { Answer(false, BuildUsage); return; }
        // A shape said as a phrase: "stairs 14 steps up north", "column 0.3 by 3", "roof gable over the floor".
        if (TryShapePhrase(words, out var phrase, out string phraseError)) words = new[] { "shape" }.Concat(phrase).ToArray();
        else if (phraseError.Length > 0) { Answer(false, phraseError); return; }
        string kind = words[0].ToLowerInvariant();
        if (!BuildKinds.Contains(kind)) { Answer(false, $"There is nothing called {words[0]} to build. " + BuildUsage); return; }
        if (!TryReadBuild(s, kind, words[1..], out var values, out string error)) { Answer(false, error); return; }
        if (kind == "shape")
            FillShape(values, words.Skip(1).Select(w => w.ToLowerInvariant()).Where(w => BuildFields(s, kind).Any(f => f.Field.Path == w)).ToHashSet());
        if (Build(s, kind, values, out string said, out string notice)) { Answer(true, said); Notify(s, notice); Refresh(s, reply); }
        else Answer(false, said);
    }

    /// <summary>The words after the kind, as field values: defaults for what was not said, each checked.</summary>
    private bool TryReadBuild(UserSession s, string kind, string[] words, out Dictionary<string, string> values, out string error)
    {
        error = "";
        var fields = BuildFields(s, kind);
        values = fields.ToDictionary(f => f.Field.Path, f => f.Default, StringComparer.OrdinalIgnoreCase);
        // A prefab named without a size is its own size.
        bool sized = false;
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i].ToLowerInvariant();
            // The short forms a person types: here, cursor, ahead 2, fit, free.
            if (w is "here" or "cursor") { values["where"] = w; continue; }
            if (w == "ahead" && (i + 1 >= words.Length || !TryNumber(words[i + 1], out _))) { values["where"] = "ahead"; continue; }
            if (w == "ahead") { values["where"] = "ahead"; w = "distance"; }
            if (w is "fit" or "free" && values.ContainsKey("fit") && (i + 1 >= words.Length || fields.Any(f => f.Field.Path == words[i + 1].ToLowerInvariant())))
            { values["fit"] = w == "fit" ? "true" : "false"; continue; }
            var field = fields.FirstOrDefault(f => f.Field.Path == w);
            if (field == null)
            {
                error = $"A {kind} has no {words[i]}. It takes: {string.Join(", ", fields.Select(f => f.Field.Path))}.";
                return false;
            }
            if (i + 1 >= words.Length) { error = $"Say a value after {w}."; return false; }
            if (!field.Field.TryParse(words[++i], out string value, out error)) return false;
            values[field.Field.Path] = value;
            if (kind == "prefab" && w is "width" or "height" or "depth") sized = true;
        }
        if (kind == "prefab" && !sized && _maps.Prefabs.TryGetValue(values["prefab"].ToLowerInvariant(), out var t) && t.ColliderSize is { } own)
        {
            values["width"] = Num(own.X);
            values["height"] = Num(own.Y);
            values["depth"] = Num(own.Z);
        }
        if (kind is "floor" or "wall" or "roof" && !words.Any(x => x.Equals("thickness", StringComparison.OrdinalIgnoreCase)))
        {
            // A material named without a thickness is its own thickness.
            string material = values["material"];
            var mine = BuildMaterials(s, kind).FirstOrDefault(m => m.Material == material);
            if (mine.Prefab?.ColliderSize is { } size) values["thickness"] = Num(kind == "wall" ? size.Z : size.Y);
        }
        return true;
    }

    private static string Num(float f) => ((double)f).ToString("R", CultureInfo.InvariantCulture);

    private static float Value(Dictionary<string, string> v, string word)
        => float.Parse(v[word], NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Makes one piece, or fits a door or window into a wall. <paramref name="said"/> is the answer either way.</summary>
    private bool Build(UserSession s, string kind, Dictionary<string, string> v, out string said, out string notice)
    {
        notice = "";
        if (!TryBody(s, x => { }, out var world, out var feet, out float yaw)) { said = "You are not in the world yet."; return false; }
        if (kind == "shape") return BuildShape(s, world, feet, yaw, v, out said, out notice);

        PrefabTemplate? template;
        Vector3 size;
        string what;
        switch (kind)
        {
            case "floor":
            case "wall":
            case "roof":
                template = BuildMaterials(s, kind).FirstOrDefault(m => m.Material == v["material"]).Prefab;
                if (template == null) { said = $"There is no {v["material"]} {kind} to build from."; return false; }
                size = kind == "wall" ? new Vector3(Value(v, "length"), Value(v, "height"), Value(v, "thickness"))
                                      : new Vector3(Value(v, "width"), Value(v, "thickness"), Value(v, "length"));
                what = kind == "wall"
                    ? $"wall, {Metres(size.X)} long and {FieldDescriptor.Format(size.Y)} high, {v["material"].ToLowerInvariant()}"
                    : $"{kind}, {FieldDescriptor.Format(size.X)} by {Metres(size.Z)}, {v["material"].ToLowerInvariant()}";
                break;
            case "door":
                template = DoorTypes(s).FirstOrDefault(d => d.Word == v["type"]).Prefab;
                if (template == null) { said = $"There is no {v["type"]} door to build."; return false; }
                size = new Vector3(Value(v, "width"), Value(v, "height"), template.ColliderSize!.Value.Z);
                what = $"door, {FieldDescriptor.Format(size.X)} by {Metres(size.Y)}, {v["type"].Replace('-', ' ')}";
                break;
            case "window":
                template = Glazing(s);
                if (template == null) { said = "There is no glass to make a window of."; return false; }
                size = new Vector3(Value(v, "width"), Value(v, "height"), template.ColliderSize!.Value.Z);
                what = $"window, {FieldDescriptor.Format(size.X)} by {Metres(size.Y)}, {FieldDescriptor.Format(Value(v, "above"))} above the floor";
                break;
            default:
                if (!_maps.Prefabs.TryGetValue(v["prefab"].ToLowerInvariant(), out template)) { said = $"There is no prefab called {v["prefab"]}."; return false; }
                if (Models.IsRetired(PrefabKind.KindId, template.Id)) { said = $"{template.Name} is retired, so it is not offered for new things."; return false; }
                size = Plain(template) ? new Vector3(Value(v, "width"), Value(v, "height"), Value(v, "depth")) : template.ColliderSize ?? Vector3.Zero;
                what = Plain(template) ? $"{template.Name}, {FieldDescriptor.Format(size.X)} by {FieldDescriptor.Format(size.Z)} by {Metres(size.Y)} high" : template.Name;
                break;
        }
        if (!MayPlace(s, template, out said)) return false;
        string name = template.Name;
        notice = $"{s.Username} built {Article(kind == "prefab" ? name : kind)}.";

        if (kind is "door" or "window" && v["fit"] == "true")
            return FitIntoWall(s, world, feet, yaw, kind, template, size, kind == "window" ? Value(v, "above") : 0f, what, out said);

        if (Full(s, 1, out said)) return false;
        bool solid = template.ColliderSize.HasValue && (template.IsSolid ?? true);
        var pose = BuildPose(s, kind, v, feet, yaw, size, solid, out string where, out said);
        if (pose == null) return false;
        var scale = ScaleFor(template, size);
        if (!PlaceOne(s.CurrentMapId, template, pose.Value with { Scale = scale }, null, null, out var thing, out string why))
        { said = $"Not built: {why}"; return false; }
        Push(s, new PlaceOp(s.CurrentMapId, thing, "built", name));
        HandOf(s).Selected = thing.Id;
        said = $"Placed: {what}, {where}.";
        return true;
    }

    private static string Article(string noun) => ("aeiou".Contains(char.ToLowerInvariant(noun[0])) ? "an " : "a ") + noun.ToLowerInvariant();

    /// <summary>A prefab's scale to be a size; an axis the prefab has no extent on keeps 1.</summary>
    private static Vector3 ScaleFor(PrefabTemplate t, Vector3 size)
    {
        var b = t.ColliderSize ?? Vector3.One;
        static float S(float want, float bas) => bas > 1e-6f && want > 0f ? want / bas : 1f;
        return new Vector3(S(size.X, b.X), S(size.Y, b.Y), S(size.Z, b.Z));
    }

    /// <summary>
    /// Where a piece goes: in front of you (its near edge so far ahead), at your feet (a floor or roof over
    /// you; anything solid just clear of you), or at the build cursor (centred on it). A floor's top is the
    /// level, a roof's underside is "above" over it, and anything else stands on it. Null, with the reason
    /// in <paramref name="error"/>, when there is no cursor.
    /// </summary>
    private Pose? BuildPose(UserSession s, string kind, Dictionary<string, string> v, Vector3 feet, float yaw, Vector3 size, bool solid,
                            out string where, out string error)
    {
        where = ""; error = "";
        string at = v["where"];
        bool cursor = at == "cursor";
        if (cursor && !s.Build.Placed) { error = "There is no build cursor yet. /origin sets one where you stand, and /at moves it."; return null; }
        var level = cursor ? s.Build.WorldCursor : feet;
        float headingYaw = cursor ? s.Build.Yaw : yaw;
        int facing = Array.IndexOf(FacingWords, v["facing"]);
        float facingYaw = facing <= 0 ? Quarter(headingYaw) * MathF.PI / 2f : (facing - 1) * MathF.PI / 2f;
        var rotation = Quaternion.CreateFromYawPitchRoll(facingYaw, 0f, 0f);

        float y = kind switch
        {
            "floor" => level.Y - size.Y * 0.5f,
            "roof" => level.Y + Value(v, "above") + size.Y * 0.5f,
            "window" => level.Y + Value(v, "above") + size.Y * 0.5f,
            _ => level.Y + size.Y * 0.5f,
        };
        var centre = new Vector3(level.X, y, level.Z);
        if (!cursor)
        {
            var dir = Compass4[Quarter(yaw)];
            var half = CompositeAcoustics.AxisAlignedHalfExtents(size * 0.5f, rotation);
            float along = MathF.Abs(Vector3.Dot(half, dir));
            bool overhead = kind is "floor" or "roof";
            if (at == "ahead")
            {
                float d = Value(v, "distance");
                centre += dir * (d + along);
                where = $"{Metres(d)} in front of you";
            }
            else if (solid && !overhead)
            {
                centre += dir * (PhysicsConstants.PlayerRadius + 0.1f + along);
                where = "just in front of you";
            }
            else where = kind == "roof" ? "over you" : "at your feet";
        }
        else where = "at the build cursor";
        if (kind != "floor" && kind != "roof") where += $", facing {Compass4Names[(int)MathF.Round(facingYaw / (MathF.PI / 2f)) % 4]}";
        return new Pose(centre, rotation, Vector3.One);
    }

    // ── Fitting a door or window into a wall ────────────────────────────────────────────────────

    /// <summary>
    /// Fits a door or window into the wall you face: finds it within <see cref="FitReach"/>, cuts an opening
    /// centred where you look (kept inside the wall's ends, its bottom on your floor or at the sill), takes
    /// the wall away and puts back the pieces round the opening, then the door or window in it, facing as
    /// the wall does. One undo puts the wall back whole. A door opens away from you, and joins the places
    /// on either side of it, as a door on a generated map does.
    /// </summary>
    private bool FitIntoWall(UserSession s, World world, Vector3 feet, float yaw, string kind, PrefabTemplate leafPrefab,
                             Vector3 size, float above, string what, out string said)
    {
        string mapId = s.CurrentMapId;
        float bottom = feet.Y + above;
        float middle = bottom + MathF.Min(size.Y * 0.5f, 1.2f);
        var origin = new Vector3(feet.X, middle, feet.Z);
        var look = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        if (!NearestInFront(world, mapId, origin, look, out int wallId, out var wall, out float t))
        { said = $"There is no wall within {Metres(FitReach)} in front of you to fit the {kind} into."; return false; }
        string wallName = NameOf(world, wall);
        var wt = world.Get<Transform>(wall);
        var wc = world.Get<ColliderComponent>(wall);
        string prefabId = PrefabOf(world, wall);
        if (world.Has<DoorComponent>(wall) || !_maps.Prefabs.TryGetValue(prefabId.ToLowerInvariant(), out var wallPrefab) || !Plain(wallPrefab))
        { said = $"The nearest thing in front of you is {wallName}, which is not a wall to cut."; return false; }
        if (Vector3.Transform(Vector3.UnitY, wt.Rotation).Y < 0.999f)
        { said = $"{wallName} is tilted; a door or window goes into an upright wall."; return false; }

        // The wall in its own frame: which of its sides runs along it, and where you look along that.
        int along = wc.Size.X >= wc.Size.Z ? 0 : 2;
        float length = along == 0 ? wc.Size.X : wc.Size.Z;
        float thick = along == 0 ? wc.Size.Z : wc.Size.X;
        if (thick > MaxWallThickness) { said = $"{wallName} is {Metres(thick)} thick: a door or window is cut into a wall, not a block."; return false; }
        if (size.X > length + 1e-4f) { said = $"{wallName} is only {Metres(length)} long; the {kind} is {Metres(size.X)} wide."; return false; }
        var inverse = Quaternion.Inverse(wt.Rotation);
        var hit = Vector3.Transform(origin + look * t - wt.Position, inverse);
        float u = along == 0 ? hit.X : hit.Z;
        float half = size.X * 0.5f;
        float c = Math.Clamp(u, -length * 0.5f + half, length * 0.5f - half);

        float wallBottom = -wc.Size.Y * 0.5f, wallTop = wc.Size.Y * 0.5f;
        float yb = MathF.Max(bottom - wt.Position.Y, wallBottom);
        float yt = yb + size.Y;
        if (yt > wallTop + 1e-3f)
        {
            said = $"{wallName} reaches {Metres(wallTop - yb + (kind == "window" ? above : 0f))} above your floor; "
                 + (kind == "window" ? $"the window's top would be at {Metres(above + size.Y)}." : $"the door is {Metres(size.Y)} high.");
            return false;
        }

        // The pieces round the opening, in the wall's frame: (from, to) along it and (from, to) up it.
        var pieces = new List<(float A0, float A1, float Y0, float Y1)>
        {
            (-length * 0.5f, c - half, wallBottom, wallTop),
            (c + half, length * 0.5f, wallBottom, wallTop),
            (c - half, c + half, yt, wallTop),
            (c - half, c + half, wallBottom, yb),
        };
        pieces.RemoveAll(p => p.A1 - p.A0 < MinPiece || p.Y1 - p.Y0 < MinPiece);
        if (Full(s, pieces.Count, out said)) return false;

        var source = Take(mapId, world, wall, wallId);
        var baseSize = wallPrefab.ColliderSize!.Value;
        Vector3 Local(float a, float y) => along == 0 ? new Vector3(a, y, 0f) : new Vector3(0f, y, a);
        var made = new List<Snapshot>();
        foreach (var (a0, a1, y0, y1) in pieces)
        {
            var data = MapOverlayStore.Clone(source.Data);
            data.Position = wt.Position + Vector3.Transform(Local((a0 + a1) * 0.5f, (y0 + y1) * 0.5f), wt.Rotation);
            var sc = data.Scale;
            float l = a1 - a0, h = y1 - y0;
            sc.Y = baseSize.Y > 1e-6f ? h / baseSize.Y : sc.Y * h / wc.Size.Y;
            if (along == 0) sc.X = baseSize.X > 1e-6f ? l / baseSize.X : sc.X * l / length;
            else sc.Z = baseSize.Z > 1e-6f ? l / baseSize.Z : sc.Z * l / length;
            data.Scale = sc;
            data.Tile = null;
            made.Add(new Snapshot(0, data, source.Settings == null ? null : new Dictionary<string, string>(source.Settings), Added: true, Change: null, Was: data.Position));
        }

        // The leaf runs along the wall; its front faces you if a door is pushed from its front, so it opens away from you.
        float wallYaw = YawOf(wt.Rotation);
        float leafYaw = along == 0 ? wallYaw : wallYaw - MathF.PI / 2f;
        var front = new Vector3(MathF.Sin(leafYaw), 0f, MathF.Cos(leafYaw));
        bool youInFront = Vector3.Dot(feet - wt.Position, front) > 0f;
        bool pushedFromFront = (leafPrefab.PushSide ?? 1f) >= 0f;
        if (youInFront != pushedFromFront) leafYaw += MathF.PI;
        var leafRotation = Quaternion.CreateFromYawPitchRoll(leafYaw, 0f, 0f);
        var leafCentre = wt.Position + Vector3.Transform(Local(c, (yb + yt) * 0.5f), wt.Rotation);
        var leafSize = new Vector3(kind == "door" ? size.X + 2f * DoorLap : size.X, size.Y, leafPrefab.ColliderSize!.Value.Z);
        var leaf = new EntityData
        {
            PrefabId = leafPrefab.Id, Position = leafCentre, Rotation = leafRotation, Scale = ScaleFor(leafPrefab, leafSize),
        };
        if (kind == "door")
        {
            // The rooms either side, as gen_city.py's door() names them: A behind the leaf's front, B in front of it.
            var normal = Vector3.Transform(Vector3.UnitZ, leafRotation);
            int behind = RegionAt(world, leafCentre - normal * (thick * 0.5f + 0.5f));
            int before = RegionAt(world, leafCentre + normal * (thick * 0.5f + 0.5f));
            if (behind != before)
            {
                leaf.RegionAId = AuthoredIdOf(mapId, behind);
                leaf.RegionBId = AuthoredIdOf(mapId, before);
            }
        }

        // The wall goes, the pieces and the leaf come, all or none.
        var ops = new List<EditOp>();
        Remove(mapId, world, wall, wallId);
        ops.Add(new DeleteOp(mapId, source, wallName));
        var o = Overlays.Get(mapId);
        string? failed = null;
        foreach (var piece in made.Append(new Snapshot(0, leaf, null, Added: true, Change: null, Was: leaf.Position)))
        {
            int id = o.NextId++;
            piece.Data.EntityId = id;
            var thing = piece with { Id = id, Was = piece.Data.Position };
            if (!Restore(mapId, thing, out string why)) { o.NextId--; failed = why; break; }
            ops.Add(new PlaceOp(mapId, thing, "built", piece.Data == leaf ? leafPrefab.Name : wallName));
        }
        if (failed != null)
        {
            for (int i = ops.Count - 1; i >= 0; i--) Reverse(s, ops[i], forward: false, out _, nested: true);
            said = $"Not built: {failed}";
            return false;
        }
        Push(s, new BatchOp(mapId, ops, $"fitted {Article(kind)} into {wallName}"));
        HandOf(s).Selected = ((PlaceOp)ops[^1]).Thing.Id;
        said = $"Placed: {what}, in {wallName}, {Metres(Vector3.Distance(new Vector3(feet.X, 0f, feet.Z), new Vector3(leafCentre.X, 0f, leafCentre.Z)))} in front of you.";
        return true;
    }

    /// <summary>The nearest solid box the ray meets within <see cref="FitReach"/>: its authored id, entity and distance along the ray.</summary>
    private bool NearestInFront(World world, string mapId, Vector3 origin, Vector3 dir, out int id, out Entity hit, out float distance)
    {
        id = 0; hit = Entity.Null; distance = FitReach;
        bool found = false;
        foreach (var (aid, e) in _maps.AuthoredEntities(mapId))
        {
            if (!Editable(world, e) || !world.Has<ColliderComponent>(e) || world.Has<RegionComponent>(e)) continue;
            var c = world.Get<ColliderComponent>(e);
            if (!c.IsSolid || c.Shape != ColliderShape.Box) continue;
            var t = world.Get<Transform>(e);
            if (!RayHitsBox(origin, dir, t.Position, t.Rotation, c.Size * 0.5f, out float at) || at > distance) continue;
            (id, hit, distance, found) = (aid, e, at, true);
        }
        return found;
    }

    /// <summary>Where a ray first meets a turned box, metres along it (0 if it starts inside).</summary>
    internal static bool RayHitsBox(Vector3 origin, Vector3 dir, Vector3 centre, Quaternion rotation, Vector3 half, out float at)
    {
        var inverse = Quaternion.Inverse(rotation);
        var o = Vector3.Transform(origin - centre, inverse);
        var d = Vector3.Transform(dir, inverse);
        float near = 0f, far = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float oa = axis == 0 ? o.X : axis == 1 ? o.Y : o.Z;
            float da = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
            float ha = axis == 0 ? half.X : axis == 1 ? half.Y : half.Z;
            if (MathF.Abs(da) < 1e-8f)
            {
                if (MathF.Abs(oa) > ha) { at = 0f; return false; }
                continue;
            }
            float t1 = (-ha - oa) / da, t2 = (ha - oa) / da;
            if (t1 > t2) (t1, t2) = (t2, t1);
            near = MathF.Max(near, t1);
            far = MathF.Min(far, t2);
            if (near > far) { at = 0f; return false; }
        }
        at = near;
        return true;
    }

    /// <summary>The smallest named place whose box holds a point, or the outside (-1).</summary>
    private static int RegionAt(World world, Vector3 p)
    {
        int best = AcousticConstants.GlobalRegionId;
        float volume = float.MaxValue;
        world.Query(new QueryDescription().WithAll<Transform, RegionComponent>(), (Entity e, ref Transform t, ref RegionComponent r) =>
        {
            var size = r.RoomSize;
            if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) return;
            var local = Vector3.Transform(p - t.Position, Quaternion.Inverse(t.Rotation));
            if (MathF.Abs(local.X) > size.X * 0.5f || MathF.Abs(local.Y) > size.Y * 0.5f || MathF.Abs(local.Z) > size.Z * 0.5f) return;
            float v = size.X * size.Y * size.Z;
            if (v < volume) { volume = v; best = e.Id; }
        });
        return best;
    }

    /// <summary>The map's own number for a live entity, or -1 (the outside) if it has none.</summary>
    private int AuthoredIdOf(string mapId, int runtimeId)
    {
        if (runtimeId == AcousticConstants.GlobalRegionId) return AcousticConstants.GlobalRegionId;
        foreach (var (id, e) in _maps.AuthoredEntities(mapId))
            if (e.Id == runtimeId) return id;
        return AcousticConstants.GlobalRegionId;
    }
}
