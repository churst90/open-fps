using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;
using OpenFPS.Server.Core;
using OpenFPS.Server.Editor;
using OpenFPS.Server.Repositories;
using Rig = OpenFPS.Tests.WorldEditorTests.Rig;

namespace OpenFPS.Tests;

/// <summary>
/// Shapes in the quick build (docs/WORLD_EDITOR.md 19, docs/GEOMETRY.md 12): said as phrases ("stairs 14 steps up
/// north", "column 0.3 by 3", "roof gable over the floor") or chosen in Control+B's Shape kind, whose fields come into
/// use with the shape chosen; each placed with its form, kept when the map is loaded again, and refused when it
/// cannot be made.
/// </summary>
public class EditorShapeBuildTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-shape-build-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Rig Mine(string dir)
    {
        var rig = new Rig(dir, UserRole.Player);
        rig.On("mine");
        return rig;
    }

    private static ColliderComponent Collider(Rig rig, int id) => rig.World("mine").Get<ColliderComponent>(rig.Thing(id));

    private static void Near(Vector3 want, Vector3 got) => Assert.True(Vector3.Distance(want, got) < 1e-3f, $"wanted {want}, got {got}");

    [Fact]
    public void Stairs_said_as_a_phrase_climb_the_way_asked_and_are_kept()
    {
        var rig = Mine(_dir);
        string said = rig.Run("edit", "build", "stairs", "14", "steps", "up", "north");
        Assert.StartsWith("Placed: stairs, 14 steps of 18 centimetres on 28 centimetres goings, 1 wide, concrete,", said);
        Assert.EndsWith("facing north.", said);
        int id = rig.Selected;
        var c = Collider(rig, id);
        Assert.Equal(ShapeKind.Stairs, c.Form?.Kind);
        Assert.Equal(14, c.Form!.Steps);
        Near(new Vector3(1f, 2.52f, 3.92f), c.Size);
        // Climbing north: its +Z, the way the steps rise, is north.
        Near(Vector3.UnitZ, Vector3.Transform(Vector3.UnitZ, rig.PoseOf(id).Rotation));

        var maps = new MapManager(new MapRepository(rig.MapDir), rig.Prefabs)
        {
            Access = new MapAccessRepository(rig.AccessPath),
            Overlays = new MapOverlayStore(Path.Combine(rig.MapDir, "overlays")),
        };
        maps.Initialize();
        Assert.True(maps.TryGetMap("mine", out var world, out _, out _, out _));
        var again = world.Get<ColliderComponent>(maps.AuthoredEntities("mine")[id]);
        Assert.Equal(c.Form, again.Form);
        Assert.Equal("Undid: built stairs, 14 steps.", rig.Run("edit", "undo"));
    }

    [Fact]
    public void A_column_a_cone_a_ball_and_a_ramp()
    {
        var rig = Mine(_dir);
        Assert.StartsWith("Placed: round column, 0.3 metres across and 3 metres high, concrete,", rig.Run("edit", "build", "column", "0.3", "by", "3"));
        var c = Collider(rig, rig.Selected);
        Assert.Equal(ShapeKind.Cylinder, c.Form?.Kind);
        Near(new Vector3(0.3f, 3f, 0.3f), c.Size);

        Assert.StartsWith("Placed: cone,", rig.Run("edit", "build", "cone", "1", "by", "2", "top", "0.5", "wood"));
        c = Collider(rig, rig.Selected);
        Assert.Equal(0.5f, c.Form!.Top);
        Assert.Equal("Wood", rig.World("mine").Get<MaterialComponent>(rig.Thing(rig.Selected)).Material);

        // /edit place takes a shape said as a phrase too.
        Assert.StartsWith("Placed: ball, 0.5 metres across", rig.Run("edit", "place", "ball", "0.5"));
        Near(new Vector3(0.5f), Collider(rig, rig.Selected).Size);

        Assert.StartsWith("Placed: ramp, 1.5 wide, rising 0.5 metres over 6 metres", rig.Run("edit", "build", "ramp", "1.5", "by", "6", "by", "0.5"));
        Assert.Equal(ShapeKind.Wedge, Collider(rig, rig.Selected).Form?.Kind);
    }

    /// <summary>A gable roof over the floor you stand on: the floor's own outline and turn, its eaves 2.7 m over the
    /// floor's top, rising a quarter of its narrow side.</summary>
    [Fact]
    public void A_roof_goes_over_the_floor_you_stand_on()
    {
        var rig = Mine(_dir);
        rig.Run("edit", "build", "floor", "width", "6", "length", "8", "material", "Concrete", "here");
        int floor = rig.Selected;
        var fp = rig.PoseOf(floor);
        string said = rig.Run("edit", "build", "roof", "gable", "over", "the", "floor");
        Assert.StartsWith("Placed: gable roof, 6 by 8 metres, rising 1.5 metres", said);
        var c = Collider(rig, rig.Selected);
        Assert.Equal(ShapeKind.Roof, c.Form?.Kind);
        Assert.Equal(RoofStyle.Gable, c.Form!.Style);
        Assert.Equal(8, c.Form.Outline!.Length);
        Near(new Vector3(6f, 1.5f, 8f), c.Size);
        float floorTop = fp.Position.Y + 0.05f;
        Near(new Vector3(fp.Position.X, floorTop + 2.7f + 0.75f, fp.Position.Z), rig.PoseOf(rig.Selected).Position);
        // A plain "roof" is still the roof slab it always was.
        Assert.StartsWith("Placed: roof, 4 by 4 metres", rig.Run("edit", "build", "roof"));
        Assert.Null(Collider(rig, rig.Selected).Form);
    }

    [Fact]
    public void What_cannot_be_made_is_refused_and_said()
    {
        var rig = Mine(_dir);
        Assert.Equal("Not built: each rise is 0.667 m, over the 0.4 m a body can step.",
                     rig.Run("edit", "build", "stairs", "3", "steps", "1", "by", "1", "by", "2"));
        Assert.StartsWith("A round column takes 2 sizes", rig.Run("edit", "build", "column", "1", "2", "3"));
    }

    /// <summary>Control+B's Shape kind: the shape's own fields in use, the others dimmed; the command it makes builds.</summary>
    [Fact]
    public void The_dialog_offers_shapes_and_their_own_fields()
    {
        var rig = Mine(_dir);
        var catalog = BuildCatalog.From(rig.Menu("build", "form")!);
        Assert.Contains(catalog.Kinds, k => k.Word == "shape");
        var form = new BuildForm(catalog, new BuildMemory());
        form.SetKind("shape");
        Assert.Equal("stairs", form.Text("shape"));
        Assert.True(form.IsEnabled("steps"));
        Assert.False(form.IsEnabled("top"));
        Assert.False(form.IsEnabled("style"));
        form.Set("shape", "cone");
        Assert.True(form.IsEnabled("top"));
        Assert.False(form.IsEnabled("steps"));
        Assert.False(form.IsEnabled("length"));
        Assert.Equal("1.5", form.Text("height"));
        form.Set("shape", "stairs");
        Assert.Equal("14", form.Text("steps"));
        Assert.True(form.TryCommand(out var command, out var error), error);
        Assert.Equal("/edit build shape shape stairs width 1 length 3.92 height 2.52 steps 14 landing 0 material Concrete where ahead distance 1 facing me dialog",
                     command);
        var parts = command.TrimStart('/').Split(' ');
        rig.Run(parts[0], parts[1..]);
        Assert.Equal(ShapeKind.Stairs, Collider(rig, rig.Selected).Form?.Kind);
    }
}
