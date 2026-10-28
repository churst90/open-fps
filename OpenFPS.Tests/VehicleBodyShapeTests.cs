using System.Numerics;
using OpenFPS.Geometry;
using OpenFPS.Models;
using OpenFPS.Physics;

namespace OpenFPS.Tests;

/// <summary>
/// A vehicle's one shape (docs/PHYSICS_BOUNDARY.md 18.2 step 5, decision 13): the shell mesh made from its own body,
/// once per vehicle kind, and standing in a body world as a kinematic body that a falling ball lands on.
/// </summary>
public class VehicleBodyShapeTests
{
    private static string AnyCar() => Catalogue.Shared.MachineIds.First(id => Catalogue.Shared.KnowsMachine(id));

    [Fact]
    public void TheShellIsMadeOncePerVehicleKind()
    {
        string id = AnyCar();
        var a = MachineRegistry.Shell(id);
        Assert.Same(a, MachineRegistry.Shell(id));
        Assert.Same(a, MachineRegistry.Shell(id.ToUpperInvariant()));
        var sized = MachineRegistry.Shell(id, new Vector3(2f, 1.5f, 5f));
        Assert.NotSame(a, sized);
        Assert.Same(sized, MachineRegistry.Shell(id, new Vector3(2f, 1.5f, 5f)));
    }

    [Fact]
    public void ManyBodiesOfOneKindShareOneShapeAndABallLandsOnIt()
    {
        Assert.True(BodyWorld.TryStartNative(out var problem), problem);
        string id = AnyCar();
        var mesh = MachineRegistry.Shell(id).Shell.Mesh;
        using var w = new BodyWorld();
        int one = Catalogue.Shared.AddVehicleBody(w, id, new Vector3(0f, 0f, 0f), Quaternion.Identity);
        int two = Catalogue.Shared.AddVehicleBody(w, id, new Vector3(20f, 0f, 0f), Quaternion.Identity);
        Assert.NotEqual(one, two);
        Assert.Equal(2, w.BodyCount);
        Assert.Equal(w.State(one).ShapeId, w.State(two).ShapeId);
        Assert.Equal(new Vector3(20f, 0f, 0f), w.Position(two));

        float roof = mesh.BoundsMax.Y;
        int ball = w.Add(w.AddSphere(0.1f), BodyMotion.Dynamic, new Vector3(0f, roof + 1f, 0f), Quaternion.Identity, "Metal", massKg: 1f);
        for (int i = 0; i < 240; i++) w.Step();
        Assert.True(w.Position(ball).Y > 0.3f * roof, $"the ball fell through the car to {w.Position(ball).Y}; the roof is at {roof}");
        Assert.True(w.Position(ball).Y < roof + 1f, "the ball did not fall at all");
    }
}
