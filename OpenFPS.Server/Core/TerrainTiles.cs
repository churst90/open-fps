using System.Numerics;
using Arch.Core;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Core;

/// <summary>
/// Tiles of ground as entities (docs/GEOMETRY.md 2.3): one per 250 m tile, its heights on 2 m posts. The
/// entity stands at the middle of its tile at its base height; its collider is not solid, so the readers
/// of boxes pass it by, and the triangle world takes it as a heightfield. The host's half of laying a
/// map's ground: the slabs found among a world's entities, and the laid tiles and the world's stored ones
/// made into entities (TerrainBuilder and WorldTile are OpenFPS.World's).
/// </summary>
public static class TerrainTiles
{
    /// <summary>Post to post, metres: the laid ground's (TerrainBuilder.Spacing).</summary>
    public const float Spacing = TerrainBuilder.Spacing;

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

    /// <summary>The slabs of a world that could rest on the ground: fixed, solid, boxes or a footprint's outline
    /// stood up (a prism), thin and level enough to be a floor (at most 0.6 m thick, at least a metre each way,
    /// tilted under 30 degrees). The prisms are the real places' floors: left out, the ground came up through
    /// 1,253 rooms' floors on Albany (and footsteps indoors were on dirt).</summary>
    public static List<TerrainBuilder.Slab> Slabs(World world)
    {
        var slabs = new List<TerrainBuilder.Slab>();
        var q = new QueryDescription().WithAll<Transform, ColliderComponent>();
        world.Query(in q, (Entity e, ref Transform t, ref ColliderComponent c) =>
        {
            if (!c.IsSolid || c.Shape != ColliderShape.Box) return;
            if (c.Form != null && (c.Form.Kind != OpenFPS.Common.Geometry.ShapeKind.Prism || c.Form.Outline is not { Length: >= 6 })) return;
            if (world.Has<Velocity>(e) || world.Has<PlayerComponent>(e) || world.Has<DoorComponent>(e)) return;
            if (world.Has<EntityType>(e) && world.Get<EntityType>(e) != EntityType.StaticObject) return;
            if (TerrainBuilder.SlabOf(t.Position, t.Rotation, c.Size, c.Form?.Outline) is { } slab) slabs.Add(slab);
        });
        return slabs;
    }

    /// <summary>The laid tiles as entities of a world: each a tile of ground, dirt.</summary>
    public static List<Entity> Spawn(World world, IEnumerable<TerrainBuilder.Tile> tiles)
    {
        var made = new List<Entity>();
        foreach (var t in tiles)
        {
            var c = Component(t.Posts, t.Spacing, t.Heights, out float baseY);
            made.Add(Spawn(world, t.CornerX, t.CornerZ, baseY, c));
        }
        return made;
    }

    /// <summary>A world tile's ground as the component a tile of ground carries and is sent with.</summary>
    public static TerrainTileComponent ToComponent(this OpenFPS.Server.OneWorld.WorldTile.TerrainData t) => new()
    {
        Posts = t.Posts, Spacing = t.Spacing, HeightsCm = t.HeightsCm, Cells = t.Cells, Materials = t.Materials,
    };
}
