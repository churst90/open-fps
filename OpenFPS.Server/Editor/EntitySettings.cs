using System.Globalization;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;

namespace OpenFPS.Server.Editor;

/// <summary>
/// A placed thing's own settings, described the same way a model's fields are (FieldDescriptor), with
/// the component each is read from and written to. A field is offered only when the thing has its
/// component, so a wall has a name and a size, a fountain has a level, a range and a model, a door has
/// its sides and a room its six materials. The editor's settings menu is made from this list; nothing
/// about one kind of thing is written anywhere else.
/// </summary>
public static class EntitySettings
{
    /// <summary>How a setting is kept: in the overlay's settings, or as the thing's scale (a size).</summary>
    public enum Keeping { Settings, Scale }

    /// <param name="Refuse">A check beyond the field's type and range, said if it fails (a model that is not one).</param>
    /// <param name="Remakes">The setting changes what the thing is (its model): it is made again, so every
    /// client hears the change at once.</param>
    public sealed record Setting(FieldDescriptor Field, Keeping Keep,
                                 Func<World, Entity, bool> Applies,
                                 Func<World, Entity, string> Get,
                                 Action<World, Entity, string> Set,
                                 int Axis = -1,
                                 Func<World, Entity, string, string?>? Refuse = null,
                                 bool Remakes = false);

    /// <summary>The path of the setting that says which model a thing's sound plays.</summary>
    public const string ModelPath = "Model";

    private static string Num(float f) => ((double)f).ToString("R", CultureInfo.InvariantCulture);
    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    private static bool Sized(World w, Entity e)
        => w.Has<ColliderComponent>(e) && w.Has<Transform>(e) && !w.Has<RegionComponent>(e) && !w.Has<PortalComponent>(e);

    private static Setting Size(int axis, string name, string label, string help) => new(
        new FieldDescriptor { Path = name, Label = label, Unit = "m", Min = 0.05, Max = 500, Step = 0.1, Help = help },
        Keeping.Scale, Sized,
        (w, e) => Num(Axis(w.Get<ColliderComponent>(e).Size, axis)),
        (w, e, v) => { /* sizes change through the scale: see WorldEditor.SetSize */ },
        axis);

    public static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    // ── A door's sides ──────────────────────────────────────────────────────────────────────────

    private static readonly string[] KeyedWords = { "neither", "front", "back" };
    private static readonly string[] PushWords = { "front", "back" };

    private static bool IsDoor(World w, Entity e) => w.Has<DoorComponent>(e);

    // ── A room's six faces ──────────────────────────────────────────────────────────────────────

    private static readonly string[] FaceWords = { "Floor", "Ceiling", "North", "South", "East", "West" };

    private static bool IsRoom(World w, Entity e) => w.Has<RegionComponent>(e) && w.Get<RegionComponent>(e).RoomSize.X > 0f;

    /// <summary>A material's name from the index a room's faces are kept as.</summary>
    public static string MaterialName(int index)
    {
        foreach (var name in AcousticRegistry.KnownMaterials())
            if (AcousticRegistry.TryGetResonanceIndex(name, out int i) && i == index) return name;
        return "Generic";
    }

    private static Setting Face(int face) => new(
        new FieldDescriptor
        {
            Path = FaceWords[face], Label = face < 2 ? FaceWords[face].ToLowerInvariant() : $"{FaceWords[face].ToLowerInvariant()} wall",
            Type = FieldType.Choice, Choices = AcousticRegistry.KnownMaterials(),
            Help = "What this face of the room is made of. It decides how long the room rings and how bright it sounds.",
        },
        Keeping.Settings, IsRoom,
        (w, e) => MaterialName(w.Get<RegionComponent>(e).Materials is { Length: 6 } m ? m[face] : 0),
        (w, e, v) =>
        {
            if (!AcousticRegistry.TryGetResonanceIndex(v, out int index)) return;
            ref var r = ref w.Get<RegionComponent>(e);
            var faces = r.Materials is { Length: 6 } m ? (int[])m.Clone() : new int[6];
            faces[face] = index;
            r.Materials = faces;
        });

    /// <summary>Every setting there is, in the order the menu says them.</summary>
    public static readonly IReadOnlyList<Setting> All = new[]
    {
        new Setting(
            new FieldDescriptor { Path = "Name", Label = "name", Type = FieldType.Text,
                                  Help = "What it is called when it is announced, scanned or selected. A room's name is the place's name." },
            Keeping.Settings,
            (w, e) => w.Has<IdentityComponent>(e) || w.Has<NameComponent>(e) || w.Has<RegionComponent>(e),
            (w, e) => w.Has<IdentityComponent>(e) ? w.Get<IdentityComponent>(e).Name
                    : w.Has<NameComponent>(e) ? w.Get<NameComponent>(e).Name
                    : w.Get<RegionComponent>(e).FriendlyName,
            (w, e, v) =>
            {
                if (w.Has<IdentityComponent>(e)) w.Get<IdentityComponent>(e).Name = v;
                if (w.Has<NameComponent>(e)) w.Get<NameComponent>(e).Name = v;
                if (w.Has<RegionComponent>(e)) w.Get<RegionComponent>(e).FriendlyName = v;
            }),
        Size(0, "Width", "width", "Its size east to west before it is turned: the prefab's size times its scale."),
        Size(1, "Height", "height", "Its size from bottom to top."),
        Size(2, "Depth", "depth", "Its size north to south before it is turned."),
        new Setting(
            new FieldDescriptor { Path = ModelPath, Label = "model", Type = FieldType.Text,
                                  Help = "The model its sound is made by, by id: another model of the same kind. The library lists them." },
            Keeping.Settings,
            (w, e) => w.Has<SoundEmitterComponent>(e) && ModelKinds.TryModelOfSound(w.Get<SoundEmitterComponent>(e).SoundId, out var k, out _)
                      && k != ModelLibrary.Kinds.Vehicle,
            (w, e) => ModelKinds.TryModelOfSound(w.Get<SoundEmitterComponent>(e).SoundId, out _, out var id) ? id : "",
            (w, e, v) =>
            {
                ref var em = ref w.Get<SoundEmitterComponent>(e);
                if (ModelKinds.WithModel(em.SoundId, v) is { } sound) em.SoundId = sound;
            },
            Refuse: (w, e, v) =>
            {
                if (!ModelKinds.TryModelOfSound(w.Get<SoundEmitterComponent>(e).SoundId, out var kind, out _)) return "Its sound is not made by a model.";
                return ModelLibrary.Knows(kind, v) ? null : $"There is no {ModelKinds.Spoken(kind)} called {v}.";
            },
            Remakes: true),
        new Setting(
            new FieldDescriptor { Path = "Volume", Label = "volume", Unit = "", Min = 0, Max = 4, Step = 0.05,
                                  Help = "How loud its sound plays, 1 as the prefab made it. A physical model's level is its model's." },
            Keeping.Settings,
            (w, e) => w.Has<SoundEmitterComponent>(e),
            (w, e) => Num(w.Get<SoundEmitterComponent>(e).Volume),
            (w, e, v) => w.Get<SoundEmitterComponent>(e).Volume = F(v)),
        new Setting(
            new FieldDescriptor { Path = "Range", Label = "range", Unit = "m", Min = 1, Max = 3000, Step = 5,
                                  Help = "How far away its sound can be heard at all." },
            Keeping.Settings,
            (w, e) => w.Has<SoundEmitterComponent>(e),
            (w, e) => Num(w.Get<SoundEmitterComponent>(e).Range),
            (w, e, v) => w.Get<SoundEmitterComponent>(e).Range = F(v)),
        new Setting(
            new FieldDescriptor { Path = "MinDistance", Label = "minimum distance", Unit = "m", Min = 0.1, Max = 100, Step = 0.1,
                                  Help = "Inside this distance its sound gets no louder: about the size of the thing." },
            Keeping.Settings,
            (w, e) => w.Has<SoundEmitterComponent>(e),
            (w, e) => Num(w.Get<SoundEmitterComponent>(e).MinDistance),
            (w, e, v) => w.Get<SoundEmitterComponent>(e).MinDistance = F(v)),
        new Setting(
            new FieldDescriptor { Path = "KeyedSide", Label = "locked side", Type = FieldType.Choice, Choices = KeyedWords,
                                  Help = "Which side needs a key to open it: its front (the way the leaf faces), its back, or neither." },
            Keeping.Settings, IsDoor,
            (w, e) => w.Get<DoorComponent>(e).KeyedSide > 0f ? "front" : w.Get<DoorComponent>(e).KeyedSide < 0f ? "back" : "neither",
            (w, e, v) => w.Get<DoorComponent>(e).KeyedSide = v == "front" ? 1f : v == "back" ? -1f : 0f),
        new Setting(
            new FieldDescriptor { Path = "PushSide", Label = "push side", Type = FieldType.Choice, Choices = PushWords,
                                  Help = "Which face of a hinged door you push it open from: its front, or its back (you pull from the other)." },
            Keeping.Settings, IsDoor,
            (w, e) => w.Get<DoorComponent>(e).PushSide < 0f ? "back" : "front",
            (w, e, v) => w.Get<DoorComponent>(e).PushSide = v == "back" ? -1f : 1f),
        new Setting(
            new FieldDescriptor { Path = "Indoor", Label = "indoors", Type = FieldType.Bool,
                                  Help = "Whether this place is inside, under a roof, or a named place in the open." },
            Keeping.Settings, (w, e) => w.Has<RegionComponent>(e),
            (w, e) => w.Get<RegionComponent>(e).IsIndoor ? "true" : "false",
            (w, e, v) => w.Get<RegionComponent>(e).IsIndoor = v == "true"),
        Face(0), Face(1), Face(2), Face(3), Face(4), Face(5),
    };

    /// <summary>The settings a thing has.</summary>
    public static IEnumerable<Setting> For(World world, Entity entity)
        => All.Where(s => s.Applies(world, entity));

    /// <summary>A setting by its path, whatever the case.</summary>
    public static Setting? Named(string path)
        => All.FirstOrDefault(s => s.Field.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Puts a stored value on a thing (one already checked by the field, or read back from an overlay).
    /// Sizes are not set here: they are the thing's scale, which the editor changes as a move.
    /// </summary>
    public static bool TrySet(World world, Entity entity, string path, string stored, out string error)
    {
        error = "";
        var s = Named(path);
        if (s == null) { error = $"There is no setting called {path}."; return false; }
        if (!s.Applies(world, entity)) { error = $"This has no {s.Field.Label}."; return false; }
        if (s.Keep != Keeping.Settings) { error = $"{s.Field.Label} is a size."; return false; }
        s.Set(world, entity, stored);
        return true;
    }
}
