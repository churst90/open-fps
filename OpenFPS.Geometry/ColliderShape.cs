namespace OpenFPS.Common.Components;

/// <summary>
/// The form of a thing's collider, as the entity model says it and the wire carries it (ColliderComponent).
/// Here, under the physics, so the body on foot can ask how a collider stands without knowing the entity
/// model (docs/PHYSICS_BOUNDARY.md 4.3); its namespace is the one it had in OpenFPS.Common.
/// Terrain is a tile of ground (TerrainTileComponent); its collider is not solid, so the readers of
/// boxes pass it by, and the triangle world takes it as a heightfield. Append new shapes only.
/// </summary>
public enum ColliderShape { Box, Sphere, Cylinder, Cone, Polygon, Terrain }

public static class ColliderShapes
{
    /// <summary>Round in plan: what a fire burning over it takes as a circle (FireShape.Footprint).</summary>
    public static bool IsRound(this ColliderShape shape)
        => shape is ColliderShape.Cylinder or ColliderShape.Sphere or ColliderShape.Cone;
}
