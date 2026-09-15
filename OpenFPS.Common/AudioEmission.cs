using System;
using System.Numerics;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Common;

/// <summary>
/// Where an entity's sound comes out, and how big a thing is making it.
///
/// One answer, in one place, for every consumer — the voice that is placed, the occlusion probe that
/// is fired, and anything that validates a map. They were separately derived before and they
/// disagreed: the voice was moved to the tailpipe and the occlusion probe was left at the car's
/// origin on the road, so the engine was asked what could be heard at a point no sound was coming
/// from. That disagreement is a general fault with a general fix, and it is why this is a shared,
/// pure function rather than a line inside the audio system.
/// </summary>
public static class AudioEmission
{
    /// <summary>
    /// Radius of the volumetric occlusion probe, metres, for an emitter with room for it.
    ///
    /// A source is not a point, and testing it as one makes occlusion a switch: a car is either
    /// entirely visible or entirely hidden, and it flips between the two as a fence post goes past.
    /// Sampling a small sphere is what turns that into a fraction. Half a metre is about the acoustic
    /// size of the things that make noise in this world.
    /// </summary>
    public const float DefaultOcclusionRadius = 0.5f;

    /// <summary>
    /// Smallest the probe may shrink to when there is no room for the full radius.
    ///
    /// Not zero: a true point probe is the binary test again. Small enough that an emitter sitting
    /// right on its own supporting surface is still tested at a believable size.
    /// </summary>
    public const float MinOcclusionRadius = 0.1f;

    /// <summary>The emitter's offset in the entity's OWN frame — authored if the prefab says so, and
    /// otherwise the one the entity's kind implies. A vehicle's engine voice sits at its exhaust slot.</summary>
    public static Vector3 LocalOffset(EntityDefinition? def)
    {
        if (def == null) return Vector3.Zero;
        var authored = def.SoundEmitter.Offset;
        if (authored != Vector3.Zero) return authored;

        string id = def.SoundEmitter.SoundId ?? "";
        if (def.SoundEmitter.IsSynth && id.StartsWith("engine:", StringComparison.OrdinalIgnoreCase))
            return PresetOffset(id[7..]);
        return Vector3.Zero;
    }

    /// <summary>
    /// The exhaust slot of a vehicle preset, worked out once per preset.
    ///
    /// Memoised because this is asked on the hot path — for every car, twice a frame, once to place
    /// the voice and once to aim the occlusion probe — and VehicleProfile.ByName BUILDS a profile on
    /// every call: a whole engine, gearbox and tyre model, thrown away after one field is read. Thirty
    /// cars at sixty frames a second is thirty-six hundred of those a second, on the audio thread, for
    /// a number that cannot change.
    /// </summary>
    private static Vector3 PresetOffset(string preset)
    {
        if (_presetOffsets.TryGetValue(preset, out var cached)) return cached;
        var offset = VehicleProfile.Presets.ContainsKey(preset) ? VehicleProfile.ByName(preset).ExhaustOffset : Vector3.Zero;
        _presetOffsets[preset] = offset;
        return offset;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Vector3> _presetOffsets = new();

    /// <summary>Where this entity's sound comes out, in world space.</summary>
    public static Vector3 PointFor(in EntitySnapshot snap)
    {
        var local = LocalOffset(snap.Definition);
        if (local == Vector3.Zero) return snap.Transform.Position;
        return snap.Transform.Position + Vector3.Transform(local, snap.Transform.Rotation);
    }

    /// <summary>
    /// How big a sphere may be probed at that point without reaching through what the emitter is
    /// standing on.
    ///
    /// The rule is geometric, not a fudge factor: an entity rests at its own origin, so the clearance
    /// available above that surface is exactly how high the emission point sits. A tailpipe a third of
    /// a metre up gets a third of a metre of radius and the sphere stays in the air where the sound
    /// is; a half-metre sphere at the same place would have had half its samples inside the road.
    ///
    /// An emitter authored at the origin gets the floor, which is honest — there is nowhere for a
    /// sphere to go — and is the case to fix by authoring the offset, not by lifting the probe.
    /// </summary>
    public static float OcclusionRadiusFor(in EntitySnapshot snap)
    {
        float clearance = MathF.Max(0f, PointFor(snap).Y - snap.Transform.Position.Y);
        return Math.Clamp(clearance, MinOcclusionRadius, DefaultOcclusionRadius);
    }
}
