using Arch.Core;
using OpenFPS.Common.Components;
using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Server.Systems;

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

                    // Calculate world position: Parent Position + (Parent Rotation * Local Position)
                    Vector3 worldPos = parentTransform.Position + Vector3.Transform(parent.LocalPosition, parentTransform.Rotation);
                    
                    // Calculate world rotation: Parent Rotation * Local Rotation
                    Quaternion worldRot = parentTransform.Rotation * parent.LocalRotation;

                    // Only dirty if actually moved
                    if (transform.Position != worldPos || transform.Rotation != worldRot)
                    {
                        transform.Position = worldPos;
                        transform.Rotation = worldRot;
                        transform.IsDirty = true;
                    }
                }
            }
        });
    }
}