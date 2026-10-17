using System.Numerics;
using OpenFPS.Common.Networking;

namespace OpenFPS.Common;

/// <summary>Where an entity's sound comes out (<see cref="AudioEmission"/>), from its emitter component and where
/// it stands: the host's side of the one answer.</summary>
public static class EntityEmission
{
    /// <summary>The emitter's offset in the entity's own frame: authored, or else the one its kind implies.</summary>
    public static Vector3 LocalOffset(EntityDefinition? def)
        => def == null ? Vector3.Zero
           : AudioEmission.LocalOffset(def.SoundEmitter.Offset, def.SoundEmitter.IsSynth, def.SoundEmitter.SoundId);

    /// <summary>Where this entity's sound comes out, in world space.</summary>
    public static Vector3 PointFor(in EntitySnapshot snap)
        => AudioEmission.PointFor(snap.Transform.Position, snap.Transform.Rotation, LocalOffset(snap.Definition));

    /// <summary>How big a sphere may be probed at the entity's emission point (<see cref="AudioEmission.OcclusionRadiusFor"/>).</summary>
    public static float OcclusionRadiusFor(in EntitySnapshot snap)
        => AudioEmission.OcclusionRadiusFor(snap.Transform.Position, PointFor(snap));
}
