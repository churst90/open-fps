using JoltPhysicsSharp;
using OpenFPS.Geometry;

namespace OpenFPS.Physics;

/// <summary>
/// A triangle mesh shape made ahead of any world (docs/PHYSICS_BOUNDARY.md 18.7): Jolt builds its tree when the shape is
/// made, which for a tile's worth of triangles takes milliseconds, so it is done on a worker and the world only takes
/// the finished shape (<see cref="BodyWorld.AddMesh(BuiltMesh)"/>). Making one touches no world and no shared state, so
/// any number may be made at once. A mesh no world took is freed by <see cref="Dispose"/>.
/// </summary>
public sealed class BuiltMesh : IDisposable
{
    private Shape? _shape;

    public int Triangles { get; }

    private BuiltMesh(Shape shape, int triangles) { _shape = shape; Triangles = triangles; }

    /// <summary>The mesh of our own asset (its frame, counter-clockwise from outside). Safe on any thread.</summary>
    public static BuiltMesh Build(MeshAsset mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.TriangleCount == 0) throw new ArgumentException("a mesh with no triangles", nameof(mesh));
        if (!BodyWorld.TryStartNative(out var problem)) throw new InvalidOperationException("Jolt Physics could not start: " + problem);
        var triangles = new IndexedTriangle[mesh.TriangleCount];
        for (int i = 0; i < triangles.Length; i++)
            triangles[i] = new IndexedTriangle(mesh.Indices[i * 3], mesh.Indices[i * 3 + 1], mesh.Indices[i * 3 + 2]);
        using var settings = new MeshShapeSettings(mesh.Vertices, triangles);
        return new BuiltMesh(settings.Create(), triangles.Length);
    }

    /// <summary>Hands the shape to a world; the mesh is empty afterwards.</summary>
    internal Shape Release()
    {
        var s = _shape ?? throw new InvalidOperationException("this mesh was already given to a world");
        _shape = null;
        return s;
    }

    public void Dispose()
    {
        _shape?.Dispose();
        _shape = null;
    }
}
