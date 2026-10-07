using System.Numerics;
using OpenFPS.Common.Networking;

namespace OpenFPS.Common;

/// <summary>
/// Where an entity's sound comes out, one answer for every consumer: the voice, the occlusion probe and
/// map validation. Derived separately, the voice went to the tailpipe while the probe stayed at the
/// car's origin on the road, asking what could be heard where no sound was coming from.
/// </summary>
public static class AudioEmission
{
    /// <summary>
    /// Radius of the occlusion probe, metres, for an emitter with room for it: sampling a sphere makes
    /// occlusion a fraction, not a switch that flips as a fence post goes past. Half a metre is about the
    /// acoustic size of the things that make noise here.
    /// </summary>
    public const float DefaultOcclusionRadius = 0.5f;

    /// <summary>Smallest the probe shrinks to without room for the full radius. Not zero: a point probe is
    /// the switch again.</summary>
    public const float MinOcclusionRadius = 0.1f;

    /// <summary>The emitter's offset in the entity's own frame: authored, or else the one its kind
    /// implies (a vehicle's engine voice at its exhaust slot).</summary>
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
    /// The exhaust slot of a vehicle preset, worked out once: asked twice a frame per car on the audio
    /// thread, and building the profile builds a whole engine, gearbox and tyre model each time.
    /// </summary>
    private static Vector3 PresetOffset(string preset)
    {
        if (_presetOffsets.TryGetValue(preset, out var cached)) return cached;
        var offset = MachineRegistry.Knows(preset) ? MachineRegistry.VehicleFor(preset).ExhaustOffset : Vector3.Zero;
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
    /// How big a sphere may be probed at the emission point without reaching through what the emitter
    /// stands on: its height above the entity's origin. A tailpipe a third of a metre up gets a third of
    /// a metre; a half-metre sphere had half its samples inside the road. An emitter at its origin gets
    /// the minimum, to be fixed by authoring the offset, not by lifting the probe.
    /// </summary>
    public static float OcclusionRadiusFor(in EntitySnapshot snap)
    {
        float clearance = MathF.Max(0f, PointFor(snap).Y - snap.Transform.Position.Y);
        return Math.Clamp(clearance, MinOcclusionRadius, DefaultOcclusionRadius);
    }
}
