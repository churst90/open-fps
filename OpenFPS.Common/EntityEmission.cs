using System.Numerics;
using OpenFPS.Common.Networking;
using OpenFPS.Sound;
using OpenFPS.Models;

namespace OpenFPS.Common;

/// <summary>Where an entity's sound comes out (<see cref="AudioEmission"/>), from its emitter component and where
/// it stands: the host's side of the one answer.</summary>
public static class EntityEmission
{
    /// <summary>The emitter's offset in the entity's own frame: authored, or else the one its kind implies.</summary>
    public static Vector3 LocalOffset(EntityDefinition? def)
        => def == null ? Vector3.Zero
           : LocalOffset(def.SoundEmitter.Offset, def.SoundEmitter.IsSynth, def.SoundEmitter.SoundId);

    /// <summary>The emitter's offset in the thing's own frame: <paramref name="authored"/>, or else the one its
    /// kind implies (a synthesised vehicle's engine voice at its exhaust slot). The kind is the catalogue's, so
    /// this is the host's, not the sound library's (AudioEmission).</summary>
    public static Vector3 LocalOffset(Vector3 authored, bool synth, string? soundId)
    {
        if (authored != Vector3.Zero) return authored;

        // The vehicle's own memo (MachineRegistry.VehicleFor), which a model edit or a load renews. A cache
        // here kept the zero of a preset asked for before the registry knew it, and an edited exhaust's old
        // place. A preset nobody knows is logged once, by the table.
        if (synth && ModelKindTable.KnownVehiclePreset(soundId) is { } preset)
            return MachineRegistry.VehicleFor(preset).ExhaustOffset;
        return Vector3.Zero;
    }

    /// <summary>Where this entity's sound comes out, in world space.</summary>
    public static Vector3 PointFor(in EntitySnapshot snap)
        => AudioEmission.PointFor(snap.Transform.Position, snap.Transform.Rotation, LocalOffset(snap.Definition));

    /// <summary>How big a sphere may be probed at the entity's emission point (<see cref="AudioEmission.OcclusionRadiusFor"/>).</summary>
    public static float OcclusionRadiusFor(in EntitySnapshot snap)
        => AudioEmission.OcclusionRadiusFor(snap.Transform.Position, PointFor(snap));
}
