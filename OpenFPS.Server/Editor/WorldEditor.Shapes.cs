using System.Globalization;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Geometry;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Editor;

/// <summary>
/// Shapes in the quick build (docs/GEOMETRY.md 12, docs/WORLD_EDITOR.md 19): stairs, a ramp, a round column, a
/// cone, a ball, a dome, an arch or a roof, sized and made of a wall's material, from the dialog's Shape kind or
/// said as a phrase: "/edit build stairs 14 steps up north", "/edit build column 0.3 by 3", "/edit build roof
/// gable over the floor".
/// </summary>
public sealed partial class WorldEditor
{
    /// <summary>One shape the build offers: its word, what it is called, the fields it uses (the others are
    /// dimmed), and what choosing it puts in the fields.</summary>
    private sealed record ShapeChoice(string Word, string Label, string Uses, string Sets);

    private static readonly ShapeChoice[] ShapeChoices =
    {
        new("stairs", "Stairs", "width length height steps landing", "width=1 length=3.92 height=2.52 steps=14 landing=0"),
        new("ramp", "Ramp", "width length height", "width=1.5 length=6 height=0.5"),
        new("column", "Round column", "width height", "width=0.3 height=3"),
        new("cone", "Cone", "width height top", "width=1 height=1.5 top=0"),
        new("ball", "Ball", "width", "width=1"),
        new("dome", "Dome", "width height", "width=6 height=3"),
        new("arch", "Arch", "width length height thickness", "width=3 length=0.6 height=3.5 thickness=0.5"),
        new("roof", "Roof", "style over above rise width length", "style=gable over=floor above=2.7 rise=0 width=8 length=10"),
    };

    private static readonly (string Word, string Label)[] RoofStyles =
        { ("gable", "Gable"), ("hip", "Hip"), ("shed", "Shed, one slope"), ("flat", "Flat") };

    private static readonly (string Word, string Label)[] OverChoices =
        { ("floor", "The floor you stand on"), ("size", "The width and length given") };

    /// <summary>The words a phrase may start with, and the shape each is.</summary>
    private static readonly Dictionary<string, string> ShapeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stairs"] = "stairs", ["staircase"] = "stairs", ["steps"] = "stairs", ["flight"] = "stairs",
        ["ramp"] = "ramp", ["column"] = "column", ["pillar"] = "column", ["post"] = "column", ["trunk"] = "column",
        ["cone"] = "cone", ["ball"] = "ball", ["sphere"] = "ball", ["dome"] = "dome", ["arch"] = "arch",
    };

    private void AddShapeFields(UserSession s, Action<FieldDescriptor, string> add)
    {
        const string across = "Left to right as you face it; a round thing's width across.";
        add(ChoiceField("shape", "shape", ShapeChoices.Select(c => c.Word).ToList(), "What to build. Its own fields come into use; the others are dimmed."), "stairs");
        add(MetresField("width", "width", 0.05, 200, across), "1");
        add(MetresField("length", "length", 0.05, 200, "Away from you, the way it faces: stairs climb that way."), "3.92");
        add(MetresField("height", "height", 0.05, 100, "Bottom to top."), "2.52");
        add(new FieldDescriptor { Path = "steps", Label = "steps", Type = FieldType.Integer, Min = 1, Max = 200, Help = "How many steps; each rises the height over this, at most 0.4 metres." }, "14");
        add(MetresField("landing", "landing at the top", 0, 50, "A flat landing at the top, part of the length."), "0");
        add(new FieldDescriptor { Path = "top", Label = "top", Min = 0, Max = 1, Help = "The top's width over the base's: 0 a point, 1 a column." }, "0");
        add(MetresField("thickness", "thickness of the arch", 0.05, 20, "The ring over the opening and the piers beside it."), "0.5");
        add(ChoiceField("style", "roof style", RoofStyles.Select(r => r.Word).ToList(), "Gable ends, hipped all round, one slope, or flat."), "gable");
        add(ChoiceField("over", "roof over", OverChoices.Select(o => o.Word).ToList(), "Over the floor you stand on (its own shape), or the width and length given."), "floor");
        add(MetresField("above", "height above the floor", 0, 100, "Where the roof's eaves are: the tops of the walls."), "2.7");
        add(MetresField("rise", "rise to the ridge", 0, 50, "From the eaves to the ridge; 0 for a quarter of its narrow side (6 in 12)."), "0");
        var materials = BuildMaterials(s, "shape");
        add(ChoiceField("material", "material", materials.Select(m => m.Material).ToList(), "What it is made of: how it sounds struck, walked on and heard through."),
            Preferred(materials, "Concrete"));
    }

    private static IEnumerable<(string Label, string Value, string Sets, string Uses)> ShapeOptions(string field) => field switch
    {
        "shape" => ShapeChoices.Select(c => (c.Label, c.Word, c.Sets, c.Uses)),
        "style" => RoofStyles.Select(r => (r.Label, r.Word, "", "")),
        _ => OverChoices.Select(o => (o.Label, o.Word, "", "")),
    };

    /// <summary>
    /// A shape said as a phrase, as the field words /edit build takes: "stairs 14 steps up north", "column 0.3
    /// by 3", "roof gable over the floor", "ramp 1.5 by 6 by 0.5 wood here". False when the first word is no
    /// shape (a plain roof is a roof slab, as before).
    /// </summary>
    internal static bool TryShapePhrase(string[] words, out string[] fields, out string error)
    {
        fields = Array.Empty<string>(); error = "";
        if (words.Length == 0) return false;
        string first = words[0].ToLowerInvariant();
        string? shape = ShapeWords.TryGetValue(first, out var sw) ? sw : null;
        if (shape == null && first == "roof" && words.Skip(1).Any(w => RoofStyles.Any(r => r.Word == w.ToLowerInvariant())))
            shape = "roof";
        if (shape == null) return false;
        var choice = ShapeChoices.First(c => c.Word == shape);
        var values = new List<string> { "shape", shape };
        var numbers = new List<string>();
        for (int i = 1; i < words.Length; i++)
        {
            string w = words[i].ToLowerInvariant();
            if (w is "by" or "x" or "the" or "i" or "stand" or "on" or "a" or "of" or "metres" or "meters" or "m" or "high" or "wide" or "long" or "facing") continue;
            if (double.TryParse(w.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                // A count before "steps"; otherwise sizes, in the order the shape reads them.
                if (i + 1 < words.Length && words[i + 1].ToLowerInvariant() is "steps" or "step" or "treads") { values.Add("steps"); values.Add(w); i++; continue; }
                numbers.Add(w.Replace(',', '.'));
                continue;
            }
            if (w is "up" or "climbing" && i + 1 < words.Length && FacingWords.Contains(words[i + 1].ToLowerInvariant())) { values.Add("facing"); values.Add(words[++i].ToLowerInvariant()); continue; }
            if (FacingWords.Contains(w) && w != "me") { values.Add("facing"); values.Add(w); continue; }
            if (RoofStyles.Any(r => r.Word == w)) { values.Add("style"); values.Add(w); continue; }
            if (w == "over") { values.Add("over"); values.Add("floor"); continue; }
            if (w is "floor" or "ground") continue;
            if (w is "here" or "cursor" or "ahead" or "fit" or "free")
            {
                values.Add(w);
                continue;
            }
            // Anything else is a field word with its value, or a material.
            if (i + 1 < words.Length && w is "width" or "length" or "height" or "steps" or "landing" or "top" or "thickness" or "style"
                or "over" or "above" or "rise" or "material" or "where" or "distance")
            { values.Add(w); values.Add(words[++i]); continue; }
            values.Add("material"); values.Add(words[i]);
        }
        // Sizes in the order the shape is said: a column across then high; stairs, a ramp and an arch across,
        // along and high; a dome across and high; a ball across; a roof across and along.
        string[] order = shape switch
        {
            "column" or "dome" or "cone" => new[] { "width", "height" },
            "ball" => new[] { "width" },
            "roof" => new[] { "width", "length", "rise" },
            _ => new[] { "width", "length", "height" },
        };
        if (numbers.Count > order.Length) { error = $"A {choice.Label.ToLowerInvariant()} takes {order.Length} sizes: {string.Join(", ", order)}."; return false; }
        for (int k = 0; k < numbers.Count; k++) { values.Add(order[k]); values.Add(numbers[k]); }
        if (shape == "roof" && numbers.Count > 0 && !values.Contains("over")) { values.Add("over"); values.Add("size"); }
        fields = values.ToArray();
        return true;
    }

    /// <summary>
    /// The numbers a shape's fields leave unsaid, from those said: stairs at a comfortable going (0.28 m) and
    /// rise (0.18 m) for the steps asked; a column's depth its width.
    /// </summary>
    private static void FillShape(Dictionary<string, string> v, ICollection<string> said)
    {
        string shape = v["shape"];
        string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);
        if (shape == "stairs")
        {
            int steps = int.Parse(v["steps"], CultureInfo.InvariantCulture);
            if (!said.Contains("height")) v["height"] = F(Math.Round(steps * 0.18, 3));
            if (!said.Contains("length")) v["length"] = F(Math.Round(steps * 0.28 + double.Parse(v["landing"], CultureInfo.InvariantCulture), 3));
            if (!said.Contains("width")) v["width"] = "1";
        }
        else if (said.Count > 0 && !said.Contains("length")) v["length"] = v["width"];
    }

    /// <summary>Builds a shape: a plain wall prefab of the material, sized, with the form.</summary>
    private bool BuildShape(UserSession s, World world, Vector3 feet, float yaw, Dictionary<string, string> v, out string said, out string notice)
    {
        notice = "";
        var template = BuildMaterials(s, "shape").FirstOrDefault(m => m.Material == v["material"]).Prefab;
        if (template == null) { said = $"There is nothing made of {v["material"]} to build a shape from."; return false; }
        string shape = v["shape"];
        var size = new Vector3(Value(v, "width"), Value(v, "height"), Value(v, "length"));
        ShapeSpec form;
        string what;
        Pose? at = null;
        string where = "";
        switch (shape)
        {
            case "stairs":
            {
                int steps = int.Parse(v["steps"], CultureInfo.InvariantCulture);
                form = new ShapeSpec { Kind = ShapeKind.Stairs, Steps = steps, Landing = Value(v, "landing") };
                what = $"stairs, {steps} steps of {Centimetres(size.Y / steps)} on {Centimetres((size.Z - form.Landing) / steps)} goings, {FieldDescriptor.Format(size.X)} wide";
                break;
            }
            case "ramp":
                form = new ShapeSpec { Kind = ShapeKind.Wedge };
                what = $"ramp, {FieldDescriptor.Format(size.X)} wide, rising {Metres(size.Y)} over {Metres(size.Z)}";
                break;
            case "column":
                size.Z = size.X;
                form = new ShapeSpec { Kind = ShapeKind.Cylinder };
                what = $"round column, {Metres(size.X)} across and {Metres(size.Y)} high";
                break;
            case "cone":
                size.Z = size.X;
                form = new ShapeSpec { Kind = ShapeKind.Cone, Top = Value(v, "top") };
                what = $"cone, {Metres(size.X)} across and {Metres(size.Y)} high";
                break;
            case "ball":
                size = new Vector3(size.X);
                form = new ShapeSpec { Kind = ShapeKind.Sphere };
                what = $"ball, {Metres(size.X)} across";
                break;
            case "dome":
                size.Z = size.X;
                form = new ShapeSpec { Kind = ShapeKind.Dome };
                what = $"dome, {Metres(size.X)} across and {Metres(size.Y)} high";
                break;
            case "arch":
                form = new ShapeSpec { Kind = ShapeKind.Arch, Thickness = Value(v, "thickness") };
                what = $"arch, {Metres(size.X)} wide and {Metres(size.Y)} high, {Metres(size.Z)} deep";
                break;
            default:
            {
                if (!RoofFor(s, world, feet, v, out form, out size, out var pose, out where, out said)) return false;
                at = pose;
                what = $"{v["style"]} roof, {FieldDescriptor.Format(size.X)} by {Metres(size.Z)}, rising {Metres(size.Y)}";
                break;
            }
        }
        if (Shapes.Problem(form, size, PhysicsConstants.StepHeight) is { } problem) { said = $"Not built: {problem}."; return false; }
        if (!MayPlace(s, template, out said)) return false;
        if (Full(s, 1, out said)) return false;
        if (at == null)
        {
            at = BuildPose(s, "shape", v, feet, yaw, size, solid: true, out where, out said);
            if (at == null) return false;
        }
        if (!PlaceOne(s.CurrentMapId, template, at.Value with { Scale = ScaleFor(template, size) }, null, null, out var thing, out string why, form: form))
        { said = $"Not built: {why}"; return false; }
        Push(s, new PlaceOp(s.CurrentMapId, thing, "built", form.ToString()));
        HandOf(s).Selected = thing.Id;
        notice = $"{s.Username} built {Article(shape)}.";
        said = $"Placed: {what}, {v["material"].ToLowerInvariant()}, {where}.";
        return true;
    }

    private static string Centimetres(float m) => $"{Math.Round(m * 100):0} centimetres";

    /// <summary>
    /// A roof's form, size and pose: over the floor you stand on (its own outline, turned as it is, the eaves
    /// "above" over its top), or the width and length given, centred where a piece would go. Its rise is the
    /// one said, or a quarter of its narrow side.
    /// </summary>
    private bool RoofFor(UserSession s, World world, Vector3 feet, Dictionary<string, string> v, out ShapeSpec form, out Vector3 size,
                         out Pose? pose, out string where, out string said)
    {
        said = ""; where = ""; pose = null; size = default;
        var style = v["style"] switch { "hip" => RoofStyle.Hip, "shed" => RoofStyle.Shed, "flat" => RoofStyle.Flat, _ => RoofStyle.Gable };
        form = new ShapeSpec { Kind = ShapeKind.Roof, Style = style };
        float above = Value(v, "above");
        if (v["over"] == "floor")
        {
            if (!FloorUnder(world, s.CurrentMapId, feet, out var floor, out string floorName))
            { said = "There is no floor under you to put a roof over."; return false; }
            var ft = world.Get<Transform>(floor);
            var fc = world.Get<ColliderComponent>(floor);
            float narrow = MathF.Min(fc.Size.X, fc.Size.Z);
            float rise = style == RoofStyle.Flat ? 0.2f : Value(v, "rise") > 0f ? Value(v, "rise") : narrow / 4f;
            form.Outline = fc.Form is { Outline: { Length: >= 6 } outline } ? (float[])outline.Clone()
                : new[] { -fc.Size.X / 2, -fc.Size.Z / 2, fc.Size.X / 2, -fc.Size.Z / 2, fc.Size.X / 2, fc.Size.Z / 2, -fc.Size.X / 2, fc.Size.Z / 2 };
            size = new Vector3(fc.Size.X, rise, fc.Size.Z);
            float top = ft.Position.Y + fc.Size.Y * 0.5f;
            pose = new Pose(new Vector3(ft.Position.X, top + above + rise * 0.5f, ft.Position.Z), ft.Rotation, Vector3.One);
            where = $"over {floorName}, its eaves {Metres(above)} above it";
            return true;
        }
        float w = Value(v, "width"), l = Value(v, "length");
        float r = style == RoofStyle.Flat ? 0.2f : Value(v, "rise") > 0f ? Value(v, "rise") : MathF.Min(w, l) / 4f;
        form.Outline = new[] { -w / 2, -l / 2, w / 2, -l / 2, w / 2, l / 2, -w / 2, l / 2 };
        size = new Vector3(w, r, l);
        return true;
    }

    /// <summary>The floor a body stands on: the highest solid box (or footprint) top just under the feet; of two
    /// tops in the same place, the smaller (a floor laid on the ground, as the triangle world's ties have it).</summary>
    private bool FloorUnder(World world, string mapId, Vector3 feet, out Entity floor, out string name)
    {
        floor = Entity.Null; name = "";
        float best = float.MaxValue, bestArea = float.MaxValue;
        var from = feet + new Vector3(0f, 0.3f, 0f);
        foreach (var (_, e) in _maps.AuthoredEntities(mapId))
        {
            if (!world.IsAlive(e) || !world.Has<ColliderComponent>(e) || world.Has<RegionComponent>(e)) continue;
            var c = world.Get<ColliderComponent>(e);
            if (!c.IsSolid || c.Shape != ColliderShape.Box) continue;
            if (c.Form is { } f && f.Kind is not (ShapeKind.Prism or ShapeKind.Box)) continue;
            var t = world.Get<Transform>(e);
            if (!RayHitsBox(from, -Vector3.UnitY, t.Position, t.Rotation, c.Size * 0.5f, out float d) || d > 1.5f) continue;
            float area = c.Size.X * c.Size.Z;
            if (d < best - 1e-3f || (d <= best + 1e-3f && area < bestArea)) (floor, best, bestArea) = (e, d, area);
        }
        if (floor == Entity.Null) return false;
        name = NameOf(world, floor);
        return true;
    }
}
