using System.Numerics;
using OpenFPS.Common.Geometry;

namespace OpenFPS.Common;

// The one place the game's entities become solids of the triangle world (docs/GEOMETRY.md), kept on the
// host's side of the geometry library: OpenFPS.Common.Geometry knows positions, turns, shapes and surfaces,
// and nothing of entities, definitions or the wire (docs/SOUND_LIBRARY_BOUNDARY.md).

/// <summary>Where an entity goes in stage 1's geometry.</summary>
public enum GeometryRole
{
    /// <summary>Not solid, or it moves: the triangle world has nothing to do with it.</summary>
    None,
    /// <summary>A static solid box: triangles in its tile.</summary>
    Static,
    /// <summary>A door leaf: a box of its own, placed by an instance that follows it.</summary>
    Mover,
    /// <summary>Static and solid, but not a box (or not a fixed object): tested as it always was.</summary>
    Unindexed,
    /// <summary>A fixed box that is not solid but is said by name: a look finds it (the Announced layer),
    /// nothing else does.</summary>
    SightOnly,
}

/// <summary>What the game's entities are as geometry: the one place a box becomes a solid with a surface,
/// shared by the server and the client so the two make the same thing of the same entity.</summary>
public static class EntityGeometry
{
    /// <summary>
    /// Where an entity goes: a fixed solid box (<see cref="Components.EntityType.StaticObject"/>) is in the
    /// triangle world, a door leaf (one with a portal) as a mover; any other solid that does not move is
    /// unindexed; anything that moves is not geometry here.
    /// </summary>
    public static GeometryRole Classify(Components.EntityType type, bool moves, in Components.ColliderComponent collider,
                                        int portalRegionA, int portalRegionB, bool announced = false)
    {
        if (moves) return GeometryRole.None;
        // A tile of ground: not solid to the box readers, a heightfield to the triangle world.
        if (collider.Shape == Components.ColliderShape.Terrain) return GeometryRole.Static;
        if (!collider.IsSolid)
        {
            // A fixed box said by name, not an opening: a look finds it (SightGrid's index).
            var n = collider.Size;
            return announced && type == Components.EntityType.StaticObject && collider.Shape == Components.ColliderShape.Box
                   && n.X > 0f && n.Y > 0f && n.Z > 0f && portalRegionA == 0 && portalRegionB == 0
                ? GeometryRole.SightOnly : GeometryRole.None;
        }
        if (type != Components.EntityType.StaticObject) return GeometryRole.Unindexed;
        var s = collider.Size;
        if (collider.Shape != Components.ColliderShape.Box || !(s.X > 0f && s.Y > 0f && s.Z > 0f)) return GeometryRole.Unindexed;
        return portalRegionA != 0 || portalRegionB != 0 ? GeometryRole.Mover : GeometryRole.Static;
    }

    /// <summary>Where a definition goes (<see cref="Classify"/>), as a client holds it.</summary>
    public static GeometryRole RoleOf(Networking.EntityDefinition? def)
        => def == null ? GeometryRole.None : Classify(def.Type, def.Moves, def.Collider, def.Portal.RegionAId, def.Portal.RegionBId,
                                                      def.Identity.Announce);

    /// <summary>A definition's solid at <paramref name="transform"/>, made exactly as the server makes the
    /// entity's (ServerGeometry.SpecOf), so the two build the same triangles.</summary>
    public static SolidSpec SpecOf(Networking.EntityDefinition def, in Components.Transform transform, GeometryRole role)
    {
        var a = def.Acoustics;
        bool emitter = !string.IsNullOrEmpty(def.SoundEmitter.SoundId);
        var surface = SurfaceOf(def.Material.Material, def.Collider.Size, a.LeafMetres, a.StudSpacingMetres, a.IsHollow,
                                a.ShellThickness, a.Absorption, emitter, moves: false, doorLeaf: role == GeometryRole.Mover,
                                def.Identity.Name);
        if (role == GeometryRole.SightOnly) surface = SightOnly(surface);
        if (def.Collider.Shape == Components.ColliderShape.Terrain)
            return def.Terrain is { Posts: >= 2 } terrain ? TerrainSpec(def.EntityId, transform.Position, terrain, surface) : default;
        return SolidSpec.Of(def.EntityId, transform.Position, transform.Rotation, def.Collider.Size, surface,
                            Shapes.Make(def.Collider.Form, def.Collider.Size));
    }

    /// <summary>A tile of ground as a solid: its heightfield, from the entity's position (the middle of the
    /// tile, its height the base the centimetres count from).</summary>
    public static SolidSpec TerrainSpec(int owner, Vector3 position, Components.TerrainTileComponent terrain, in Surface surface)
        => SolidSpec.OfTerrain(owner, position, terrain.Field(position.Y), surface);

    /// <summary>A surface only a look meets (<see cref="GeometryLayers.Announced"/>).</summary>
    public static Surface SightOnly(in Surface surface) => surface with { Layers = GeometryLayers.Announced, Flags = SurfaceFlags.None };

    /// <summary>
    /// The surface of a solid box entity. Every physical layer, and the acoustic one unless it is a sound
    /// source's own box or something that moves (the rule SteamAudioScene.BoxesFromWorld applies).
    /// </summary>
    public static Surface SurfaceOf(string? material, Vector3 size, float leafMetres, float studSpacingMetres,
                                    bool hollow, float shellThickness, float absorption, bool emitter, bool moves,
                                    bool doorLeaf, string? name)
    {
        // Kept empty when empty: some queries read that as "Generic", some pass it on.
        var mat = material ?? "";
        var layers = GeometryLayers.Physical;
        if (!emitter && !moves) layers |= GeometryLayers.Acoustics;
        var flags = SurfaceFlags.None;
        if (string.Equals(mat, "Glass", StringComparison.OrdinalIgnoreCase)) flags |= SurfaceFlags.Glass;
        if (doorLeaf) flags |= SurfaceFlags.DoorLeaf;
        if (hollow) flags |= SurfaceFlags.Hollow;
        if (name != null && name.Contains("roof", StringComparison.OrdinalIgnoreCase)) flags |= SurfaceFlags.Roof;
        return new Surface(mat, new Construction(size, new WallBuild(leafMetres, studSpacingMetres), shellThickness),
                           layers, flags, absorption);
    }
}
