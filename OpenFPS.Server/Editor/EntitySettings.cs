using System.Globalization;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;

namespace OpenFPS.Server.Editor;

/// <summary>
/// A placed thing's own settings, described the same way a model's fields are (FieldDescriptor), with
/// the component each is read from and written to. A field is offered only when the thing has its
/// component, so a wall has a name and a size and a fountain has a level and a range too. The editor's
/// settings menu is made from this list; nothing about one kind of thing is written anywhere else.
/// </summary>
public static class EntitySettings
{
    /// <summary>How a setting is kept: in the overlay's settings, or as the thing's scale (a size).</summary>
    public enum Keeping { Settings, Scale }

    public sealed record Setting(FieldDescriptor Field, Keeping Keep,
                                 Func<World, Entity, bool> Applies,
                                 Func<World, Entity, string> Get,
                                 Action<World, Entity, string> Set,
                                 int Axis = -1);

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

    /// <summary>Every setting there is, in the order the menu says them.</summary>
    public static readonly IReadOnlyList<Setting> All = new[]
    {
        new Setting(
            new FieldDescriptor { Path = "Name", Label = "name", Type = FieldType.Text,
                                  Help = "What it is called when it is announced, scanned or selected." },
            Keeping.Settings,
            (w, e) => w.Has<IdentityComponent>(e) || w.Has<NameComponent>(e),
            (w, e) => w.Has<IdentityComponent>(e) ? w.Get<IdentityComponent>(e).Name : w.Get<NameComponent>(e).Name,
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
