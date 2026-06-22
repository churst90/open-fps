using Arch.Core;
using OpenFPS.Common.Components;
using System.Numerics;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Implements the Acoustic-Physics Bridge.
/// Monitors entities with colliders (like doors) and updates corresponding portals.
/// </summary>
public static class PhysicsAcousticBridgeSystem
{
    public static void Update(World world)
    {
        // Find entities that are portals but also have colliders (e.g. doors)
        world.Query(new QueryDescription().WithAll<PortalComponent, ColliderComponent, Transform>(), 
        (ref PortalComponent portal, ref ColliderComponent collider, ref Transform transform) =>
        {
            // If the collider is solid, the door is closed, aperture is 0.
            // If the collider is NOT solid (e.g. door opened/disabled), aperture expands.
            // Alternatively, aperture size could scale with the physical size of the door opening.
            float targetAperture = collider.IsSolid ? 0.0f : Math.Max(collider.Size.X, collider.Size.Z);
            
            if (Math.Abs(portal.ApertureSize - targetAperture) > 0.01f)
            {
                portal.ApertureSize = targetAperture;
                // A networking system would then flag this portal change for sync to clients
            }
        });
    }
}
