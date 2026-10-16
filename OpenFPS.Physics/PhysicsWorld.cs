using System.Numerics;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;

namespace OpenFPS.Common;

/// <summary>
/// A solid the physics meets as a box: a thing that moves (a person, a car, a crate in a hand) or a static
/// thing the triangle world does not hold. <see cref="Rotation"/> is how it stands for a body to meet
/// (<see cref="SharedMovementEngine.StandingRotation"/>): a person's cylinder upright whatever way they look.
/// </summary>
public readonly record struct PhysicsBody(int Id, Vector3 Centre, Vector3 Size, Quaternion Rotation, string Material)
{
    /// <summary>A solid's box from what both sides know of it: its place, size, shape, facing and material
    /// (an empty material is <c>Generic</c>, as every reader took it).</summary>
    public static PhysicsBody Of(int id, Vector3 centre, Vector3 size, ColliderShape shape, Quaternion rotation, string? material)
        => new(id, centre, size, SharedMovementEngine.StandingRotation(shape, rotation),
               string.IsNullOrEmpty(material) ? "Generic" : material);

    /// <summary>
    /// Whether its footprint, standing, comes within <paramref name="reach"/> of <paramref name="at"/> across
    /// the ground (height not counted). The one rule for what is near, which a server's grid and a client's
    /// list can both answer exactly: a grid's cells round a point hold every box this says is near.
    /// </summary>
    public bool Near(Vector3 at, float reach)
    {
        var half = Size * 0.5f;
        float ex = half.X, ez = half.Z;
        if (Rotation != Quaternion.Identity)
        {
            var x = Vector3.Transform(new Vector3(half.X, 0f, 0f), Rotation);
            var y = Vector3.Transform(new Vector3(0f, half.Y, 0f), Rotation);
            var z = Vector3.Transform(new Vector3(0f, 0f, half.Z), Rotation);
            ex = MathF.Abs(x.X) + MathF.Abs(y.X) + MathF.Abs(z.X);
            ez = MathF.Abs(x.Z) + MathF.Abs(y.Z) + MathF.Abs(z.Z);
        }
        float dx = MathF.Max(0f, MathF.Abs(at.X - Centre.X) - ex);
        float dz = MathF.Max(0f, MathF.Abs(at.Z - Centre.Z) - ez);
        return dx * dx + dz * dz <= reach * reach;
    }

    /// <summary>As the step takes it.</summary>
    public SharedMovementEngine.Collider Collider => new() { Position = Centre, Size = Size, Rotation = Rotation, Material = Material };

    /// <summary>By id, so every side lists the same bodies in the same order and a tie between two goes the
    /// same way everywhere.</summary>
    public static readonly Comparison<PhysicsBody> ById = static (a, b) => a.Id.CompareTo(b.Id);
}

/// <summary>
/// The world as the physics sees it (docs/PHYSICS_BOUNDARY.md 4.6): the triangle world, the statics it does
/// not hold and the moving bodies as boxes. The server fills one from its world and grid, a client from its
/// snapshot, and the ground probe, the gather and the vehicle step read only this, so both sides ask the same
/// question of what each has.
/// </summary>
public interface IPhysicsWorld
{
    /// <summary>The static solids as triangles, door leaves where they stand; null where none is built (every
    /// solid is then a box).</summary>
    TriangleWorld? Triangles { get; }

    /// <summary>Owners whose place in <see cref="Triangles"/> is being rebuilt, which it must not count (a
    /// client builds its triangles in the background); null when none.</summary>
    IReadOnlySet<int>? Stale { get; }

    /// <summary>
    /// The solids <see cref="Triangles"/> does not answer for (every solid when there are none) whose
    /// footprint comes within <paramref name="reach"/> of <paramref name="at"/> (<see cref="PhysicsBody.Near"/>):
    /// the statics it does not hold and everything that moves, each once, by id. Cleared first.
    /// </summary>
    void Near(Vector3 at, float reach, List<PhysicsBody> into);

    /// <summary>Every solid as a box, each once, by id: the last resort of a ground probe that found nothing near.</summary>
    void Everything(List<PhysicsBody> into);

    /// <summary>
    /// Every solid as a box round <paramref name="at"/>, those the triangles hold too, in the order the side
    /// keeps them: a vehicle's collision test, which still meets the world as boxes (stage 4 moves it onto the
    /// triangles with the body as a mesh). May hold some further out than <paramref name="radius"/>.
    /// </summary>
    void Boxes(Vector3 at, float radius, List<PhysicsBody> into);
}

/// <summary>
/// The triangles a body counts: not its own (<see cref="Own"/>), not those it is told to leave out (a car's
/// parts and the people in it), not those the world is rebuilding.
/// </summary>
public struct BodyFilter : IGeometryFilter
{
    public int Own;
    public IReadOnlyList<int>? Ignore;
    public IReadOnlySet<int>? Stale;

    public BodyFilter(IPhysicsWorld world, int own, IReadOnlyList<int>? ignore = null)
    {
        Own = own;
        Ignore = ignore is { Count: > 0 } ? ignore : null;
        Stale = world.Stale;
    }

    public readonly bool Accept(int owner, in Surface surface) => !Ignores(owner) && (Stale == null || !Stale.Contains(owner));

    /// <summary>Whether a body is left out (its own, or one it was told to leave), whatever the world is
    /// rebuilding.</summary>
    public readonly bool Ignores(int id)
    {
        if (id == Own) return true;
        if (Ignore != null) for (int i = 0; i < Ignore.Count; i++) if (Ignore[i] == id) return true;
        return false;
    }
}
