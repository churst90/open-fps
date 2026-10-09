using System.Numerics;
using Arch.Core;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Core;

/// <summary>
/// Tiles of ground as entities (docs/GEOMETRY.md 2.3): one per 250 m tile, its heights on 2 m posts. The
/// entity stands at the middle of its tile at its base height; its collider is not solid, so the readers
/// of boxes pass it by, and the triangle world takes it as a heightfield.
/// </summary>
public static class TerrainTiles
{
    /// <summary>Post to post, metres (decision 1 of docs/GEOMETRY.md section 8).</summary>
    public const float Spacing = 2f;

    /// <summary>What a tile of ground is called when it is struck or stood on.</summary>
    public const string Name = "Ground";

    /// <summary>
    /// A tile of ground whose post (0, 0) is at (<paramref name="cornerX"/>, <paramref name="cornerZ"/>), its
    /// heights in metres row by row (post (i, j) at [j * posts + i]), counted in centimetres over the lowest.
    /// </summary>
    public static TerrainTileComponent Component(int posts, float spacing, float[] heights, out float baseY,
                                                 byte[]? cells = null, string[]? materials = null)
    {
        float lo = float.MaxValue;
        foreach (float h in heights) lo = MathF.Min(lo, h);
        // A whole centimetre, so the base is the same float everywhere it is read back.
        baseY = MathF.Floor(lo * 100f) / 100f;
        return new TerrainTileComponent
        {
            Posts = posts,
            Spacing = spacing,
            HeightsCm = OpenFPS.Common.Geometry.Heightfield.ToCentimetres(heights, baseY),
            Cells = cells ?? new byte[(posts - 1) * (posts - 1)],
            Materials = materials ?? new[] { "Dirt" },
        };
    }

    /// <summary>The entity for a tile of ground: at the middle of its square, at its base height.</summary>
    public static Entity Spawn(World world, float cornerX, float cornerZ, float baseY, TerrainTileComponent terrain)
    {
        float half = terrain.Size * 0.5f;
        var e = world.Create(
            new Transform { Position = new Vector3(cornerX + half, baseY, cornerZ + half), Rotation = Quaternion.Identity, Scale = Vector3.One },
            new NameComponent { Name = Name },
            new IdentityComponent { Name = Name, Description = "The ground." },
            EntityType.StaticObject,
            new ColliderComponent { Shape = ColliderShape.Terrain, Size = Vector3.Zero, IsSolid = false },
            new MaterialComponent { Material = terrain.Materials.Length > 0 ? terrain.Materials[0] : "Dirt" },
            new AcousticComponent());
        world.Add(e, terrain);
        return e;
    }
}
