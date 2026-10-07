using Arch.Core;
using OpenFPS.Common.Components;
using System.Numerics;

namespace OpenFPS.Server.Systems;

/// <summary>Places every part from its parent's pose and its own local pose, each tick.</summary>
public static class ParentSystem
{
    public static void Update(World world, Dictionary<int, Entity> lookup)
    {
        world.Query(new QueryDescription().WithAll<Transform, ParentComponent>(), (Entity e, ref Transform transform, ref ParentComponent parent) =>
        {
            if (parent.ParentEntityId == -1) return;

            if (lookup.TryGetValue(parent.ParentEntityId, out var parentEntity))
            {
                if (world.Has<Transform>(parentEntity))
                {
                    var parentTransform = world.Get<Transform>(parentEntity);

                    Vector3 worldPos = parentTransform.Position + Vector3.Transform(parent.LocalPosition, parentTransform.Rotation);

                    Quaternion worldRot = parentTransform.Rotation * parent.LocalRotation;

                    if (transform.Position != worldPos || transform.Rotation != worldRot)
                    {
                        transform.Position = worldPos;
                        transform.Rotation = worldRot;
                        transform.IsDirty = true;
                        // A door leaf in a building is placed here: the triangle world follows it.
                        if (world.Has<DoorComponent>(e)) OpenFPS.Common.MoverPoses.Moved();
                    }
                }
            }
        });
    }
}