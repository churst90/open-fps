using System;
using System.Numerics;

namespace OpenFPS.Common.Geometry;

/// <summary>
/// Which queries see a surface (docs/GEOMETRY.md 2.1). A chain-link fence blocks movement but hardly any
/// sound; a sound source's own box is in the way of a walker but not of the sound it makes; glass blocks a
/// body and a bullet but not a look. Every query names the layers it asks about, and a surface answers it
/// only when it shares one.
/// </summary>
[Flags]
public enum GeometryLayers : byte
{
    None = 0,
    /// <summary>What a walking body collides with.</summary>
    Movement = 1,
    /// <summary>What a round in flight strikes.</summary>
    Bullets = 2,
    /// <summary>What a look (the scope, a sight line) stops at.</summary>
    Sight = 4,
    /// <summary>What sound meets: the acoustic scene (no sound source's own box, nothing that moves).</summary>
    Acoustics = 8,
    /// <summary>What can be stood on.</summary>
    Ground = 16,
    /// <summary>Everything physical: what a static solid box is today.</summary>
    Physical = Movement | Bullets | Sight | Ground,
    All = Movement | Bullets | Sight | Acoustics | Ground,
    /// <summary>What a look finds that nothing else meets: a thing that is said by name but is not solid
    /// (a sign, a counter's front, a place's marker). In no other layer, so no other query sees it.</summary>
    Announced = 32,
}

/// <summary>What a surface is, beyond its material, for the queries that need to know.</summary>
[Flags]
public enum SurfaceFlags : ushort
{
    None = 0,
    /// <summary>A thin slab at ground level with the sky over it: left out of the listener's own trace
    /// (SteamAudioScene.WithoutOpenGround).</summary>
    OpenGround = 1,
    /// <summary>The top of a building (the scope's "on a roof").</summary>
    Roof = 2,
    /// <summary>Seen through by a look unless asked otherwise.</summary>
    Glass = 4,
    /// <summary>A porous volume (a tree's crown): attenuates rather than reflects.</summary>
    Foliage = 8,
    /// <summary>A door leaf: belongs in its opening, never taken for a jamb.</summary>
    DoorLeaf = 16,
    /// <summary>A hollow shell: walls of <see cref="Construction.ShellThickness"/> round an empty inside.</summary>
    Hollow = 32,
}

/// <summary>
/// What <see cref="WallTransmission"/> needs and a triangle does not carry: the panel the sound goes
/// through (today the box's own size: its smallest side is its thickness, the other two its face) and how
/// it is built (the prefab's leaves and studs), and for a hollow shell how thick the shell is.
/// </summary>
public readonly record struct Construction(Vector3 PanelSize, WallBuild Build, float ShellThickness = 0f);

/// <summary>
/// One entry of a surface table (docs/GEOMETRY.md 2.1): what a triangle is made of, how it is built,
/// which queries see it and what it is. <see cref="Absorption"/> is an authored override of the
/// material's broadband absorption, 0 when there is none (AcousticComponent.Absorption).
/// </summary>
public readonly record struct Surface(string Material, Construction Construction, GeometryLayers Layers,
                                      SurfaceFlags Flags, float Absorption = 0f)
{
    public bool Is(SurfaceFlags flag) => (Flags & flag) != 0;
    public bool Sees(GeometryLayers layers) => (Layers & layers) != 0;
}

/// <summary>
/// An immutable set of triangles in its own local frame (docs/GEOMETRY.md 2.1): vertices in metres,
/// three indices a triangle, a surface slot per triangle, whether it is a closed solid and whether that
/// solid is convex. Identified by the hash of its contents, so the same hash is the same mesh on every
/// machine. Triangles wind counter-clockwise seen from outside: the face normal (b - a) x (c - a) points
/// out of the solid.
/// </summary>
public sealed class MeshAsset
{
    public Vector3[] Vertices { get; }
    public int[] Indices { get; }
    /// <summary>The surface slot of each triangle: an index into whatever surface list places the mesh.</summary>
    public byte[] TriangleSurface { get; }
    public int SurfaceCount { get; }
    public bool Closed { get; }
    public bool Convex { get; }
    public ulong Hash { get; }
    public int TriangleCount => Indices.Length / 3;
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }

    public MeshAsset(Vector3[] vertices, int[] indices, byte[] triangleSurface, bool closed, bool convex)
    {
        if (indices.Length % 3 != 0) throw new ArgumentException("indices must come in threes", nameof(indices));
        if (triangleSurface.Length != indices.Length / 3) throw new ArgumentException("one surface slot per triangle", nameof(triangleSurface));
        Vertices = vertices; Indices = indices; TriangleSurface = triangleSurface;
        Closed = closed; Convex = convex;
        int surfaces = 0;
        foreach (byte s in triangleSurface) surfaces = Math.Max(surfaces, s + 1);
        SurfaceCount = Math.Max(1, surfaces);
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var v in vertices) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
        BoundsMin = vertices.Length > 0 ? lo : Vector3.Zero;
        BoundsMax = vertices.Length > 0 ? hi : Vector3.Zero;
        Hash = ContentHash(vertices, indices, triangleSurface, closed, convex);
    }

    /// <summary>FNV-1a over the exact bits of everything that defines the mesh: the same on every machine.</summary>
    public static ulong ContentHash(Vector3[] vertices, int[] indices, byte[] surfaces, bool closed, bool convex)
    {
        ulong h = 14695981039346656037UL;
        void Mix(uint v) { for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211UL; } }
        Mix((uint)vertices.Length); Mix((uint)indices.Length);
        foreach (var v in vertices) { Mix(BitConverter.SingleToUInt32Bits(v.X)); Mix(BitConverter.SingleToUInt32Bits(v.Y)); Mix(BitConverter.SingleToUInt32Bits(v.Z)); }
        foreach (int i in indices) Mix((uint)i);
        foreach (byte s in surfaces) Mix(s);
        Mix((closed ? 1u : 0u) | (convex ? 2u : 0u));
        return h;
    }
}

/// <summary>
/// Shapes generated from a few numbers by the same code on server and client (docs/GEOMETRY.md 2.6), so
/// a map sends the numbers and never the triangles. Stage 1 has the one every entity is today: the box.
/// </summary>
public static class ShapeLibrary
{
    /// <summary>A box's corners as multiples of its half size.</summary>
    internal static readonly Vector3[] BoxCorners =
    {
        new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1),
        new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1),
    };

    /// <summary>The box's twelve triangles, wound so each face normal points out of the box.</summary>
    internal static readonly int[] BoxTriangles =
    {
        0, 3, 2,  0, 2, 1,   // -Z
        4, 5, 6,  4, 6, 7,   // +Z
        0, 4, 7,  0, 7, 3,   // -X
        1, 2, 6,  1, 6, 5,   // +X
        0, 1, 5,  0, 5, 4,   // -Y
        3, 7, 6,  3, 6, 2,   // +Y
    };

    private static readonly byte[] OneSurface = new byte[12];

    /// <summary>A closed, convex box of <paramref name="size"/> centred on its own origin: 8 vertices,
    /// 12 triangles, one surface.</summary>
    public static MeshAsset Box(Vector3 size)
    {
        var h = size * 0.5f;
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++) v[i] = BoxCorners[i] * h;
        return new MeshAsset(v, BoxTriangles, OneSurface, closed: true, convex: true);
    }

    /// <summary>
    /// The world position of corner <paramref name="corner"/> of a box, relative to <paramref name="origin"/>:
    /// (centre - origin) + rotation·(corner · half size). Written once here because every builder must
    /// do it in exactly this order for the server and the client to hold the same bits.
    /// </summary>
    public static Vector3 BoxCorner(int corner, Vector3 centre, Vector3 size, Quaternion rotation, Vector3 origin)
        => BoxCorner(corner, centre, size, RotationMatrix(rotation), origin);

    /// <summary>The same with the turn already made a matrix (<see cref="RotationMatrix"/>).</summary>
    public static Vector3 BoxCorner(int corner, Vector3 centre, Vector3 size, in Matrix4x4 turn, Vector3 origin)
    {
        var local = BoxCorners[corner] * (size * 0.5f);
        return (centre - origin) + Vector3.TransformNormal(local, turn);
    }

    /// <summary>
    /// A turn as the matrix every corner is placed with: the quaternion made unit length first (a map
    /// writes six digits, and 0.707082/0.707131 is not quite unit), then the classical matrix. For a turn
    /// about the vertical alone its middle row and column are exactly (0, 1, 0), so a box's top is exactly
    /// its centre plus half its height, as every box test reads it: Vector3.Transform with a quaternion
    /// that is not unit length scales the height by its length squared, and a gravel strip's top came out
    /// a float's last bit low, which was enough to step a body up onto a kerb the box path did not.
    /// </summary>
    public static Matrix4x4 RotationMatrix(Quaternion rotation)
    {
        float len = rotation.LengthSquared();
        if (len < 1e-6f) return Matrix4x4.Identity;
        if (len != 1f) rotation = Quaternion.Normalize(rotation);
        return Matrix4x4.CreateFromQuaternion(rotation);
    }
}
