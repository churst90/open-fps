using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;
using OpenFPS.Server.Core;
using OpenFPS.Server.Editor;
using OpenFPS.Server.Repositories;
using Rig = OpenFPS.Tests.WorldEditorTests.Rig;

namespace OpenFPS.Tests;

/// <summary>
/// A map entity's own form over its prefab's (a ramp laid as stairs) is kept when the world editor
/// makes the thing again and when the overlay copies the entry (probable bug 9 of 2026-10-07).
/// </summary>
public class EditorFormTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-editor-form-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ShapeSpec Flight() => new() { Kind = ShapeKind.Stairs, Steps = 4 };

    [Fact]
    public void TheOverlaysCopyOfAnEntryKeepsItsForm()
    {
        var entry = new EntityData { EntityId = 7, PrefabId = "concrete_ramp", Form = Flight() };
        var copy = MapOverlayStore.Clone(entry);
        Assert.Equal(Flight(), copy.Form);
        Assert.NotSame(entry.Form, copy.Form);
    }

    [Fact]
    public void AThingMadeAgainKeepsTheFormItsMapGaveIt()
    {
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        var map = MapTemplates.Flat("formed", "tester");
        // The prefab is a wedge; this map lays it as a flight of four steps (0.125 m risers).
        map.Entities.Add(new EntityData { EntityId = 2, PrefabId = "concrete_ramp", Position = new Vector3(10, 0.25f, 10), Form = Flight() });
        Assert.True(rig.Maps.CreateMap(map, out string error), error);
        rig.On("formed");
        var world = rig.World("formed");
        Assert.Equal(ShapeKind.Stairs, world.Get<ColliderComponent>(rig.Thing(2)).Form?.Kind);

        // A new version of the prefab makes every ramp again where it stands.
        var before = rig.Thing(2);
        Assert.StartsWith("Name of the prefab concrete_ramp", rig.Run("edit", "model", "set", "prefab", "concrete_ramp", "Name", "Test", "Ramp"));
        Assert.NotEqual(before, rig.Thing(2));

        var form = world.Get<ColliderComponent>(rig.Thing(2)).Form;
        Assert.Equal(Flight(), form);
    }

    /// <summary>A copy, and a deletion undone, are made from the entry as well: stairs, not the prefab's wedge.</summary>
    [Fact]
    public void ACopyAndAnUndoneDeletionKeepTheForm()
    {
        var rig = new Rig(_dir, UserRole.Dev);
        var map = MapTemplates.Flat("formed", "tester");
        map.Entities.Add(new EntityData { EntityId = 2, PrefabId = "concrete_ramp", Position = new Vector3(10, 0.25f, 10), Form = Flight() });
        Assert.True(rig.Maps.CreateMap(map, out string error), error);
        rig.On("formed");
        var world = rig.World("formed");

        Assert.StartsWith("Selected", rig.Run("edit", "select", "#2"));
        Assert.StartsWith("Copied", rig.Run("edit", "duplicate"));
        int copy = rig.Selected;
        Assert.Equal(Flight(), world.Get<ColliderComponent>(rig.Thing(copy)).Form);

        Assert.StartsWith("Selected", rig.Run("edit", "select", "#2"));
        Assert.StartsWith("Deleted", rig.Run("edit", "delete"));
        Assert.StartsWith("Undid", rig.Run("edit", "undo"));
        Assert.Equal(Flight(), world.Get<ColliderComponent>(rig.Thing(2)).Form);
    }
}
